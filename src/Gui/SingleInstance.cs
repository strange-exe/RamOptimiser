using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RamOptimiser.Gui
{
    /// <summary>
    /// One instance per user per session. The first instance owns a named mutex and waits on a named event;
    /// a later instance signals that event (SignalFirstInstance) and exits, and the first raises
    /// ActivationRequested on a background thread (marshal to the UI thread before touching windows). When the
    /// request will be raised (a handler takes it now, or none has been added yet and the first one gets it), the
    /// first instance also sets a second named event, the acknowledgement (SignalAndWaitForAck). A copy that has
    /// begun closing (BeginClosing) or whose handlers were removed again is exiting and sends none (SecondLaunch).
    ///
    /// Both kernel objects get an explicit security descriptor: the current user as owner, full access for
    /// that user only, and a Medium integrity label. Without the label, objects created by the elevated
    /// instance (started by the HIGHEST-level logon task) could not be opened by a normal, non-elevated launch
    /// of the same user. The explicit owner keeps them owned by the user even when created elevated (whose
    /// default owner is Administrators).
    ///
    /// An existing object under either name is trusted only when it is owned by the current user and can be
    /// waited on and signalled. Anything else (another owner, another object type, missing access rights)
    /// throws SingleInstanceNameTakenException, a Win32Exception: callers then run without single-instance
    /// rather than exit, so a squatted name cannot stop the app.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        public const string NamePrefix = "Local\\RamOptimiser-";

        readonly SafeWaitHandle mutex;
        readonly SafeWaitHandle activate;
        readonly SafeWaitHandle ack;        // null when the acknowledgement name is unusable (CreateAck)
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        // Our own wake for a kept request (Resignal). Unnamed, so the waiter tells it apart from a launch's request:
        // a replay is raised but never answered.
        readonly AutoResetEvent replay = new AutoResetEvent(false);
        readonly Thread waiter;
        readonly bool isFirst;
        int disposed;
        readonly object handlerGate = new object();
        EventHandler activationRequested;   // guarded by handlerGate
        bool pendingActivation;             // a signal arrived with no handler; guarded by handlerGate
        bool subscribed;                    // a handler has been added at least once; guarded by handlerGate
        bool closing;                       // BeginClosing ran: no answer, no handler call; guarded by handlerGate

        /// <summary>
        /// Raised on a background thread when another instance asks this one to show itself. A request that
        /// arrives before anyone subscribes is kept and raised once the first handler is added.
        /// </summary>
        public event EventHandler ActivationRequested
        {
            add
            {
                bool replay;
                lock (handlerGate)
                {
                    activationRequested += value;
                    if (activationRequested != null) subscribed = true;
                    replay = pendingActivation && activationRequested != null;
                    if (replay) pendingActivation = false;
                }
                if (replay) Resignal();
            }
            remove { lock (handlerGate) activationRequested -= value; }
        }

        /// <summary>
        /// Wakes the waiter thread to raise the kept request. Through the private replay event, not the named
        /// activation event: the launch that asked was answered when its request arrived, and an answer sent now
        /// could reach a later launch just before this copy starts closing.
        /// </summary>
        void Resignal()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            try { replay.Set(); }
            catch (ObjectDisposedException) { } // Dispose won the race: nobody is left to activate
        }

        public SingleInstance() : this(CurrentUserSid()) { }

        /// <summary>id is normally the user's SID; tests pass a unique value so they never meet the real app.</summary>
        internal SingleInstance(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id is required.", "id");
            string sid = CurrentUserSid();
            IntPtr sd;
            if (!GuiNative.ConvertStringSecurityDescriptorToSecurityDescriptor(
                    "O:" + sid + "D:(A;;GA;;;" + sid + ")S:(ML;;NW;;;ME)", GuiNative.SDDL_REVISION_1, out sd, IntPtr.Zero))
                throw new Win32Exception();
            try
            {
                var sa = new GuiNative.SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(GuiNative.SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = sd,
                    bInheritHandle = false
                };
                // The event is created (or opened) before the mutex, so a second instance that signals while
                // the first is still starting up is not lost: the auto-reset event stays set until the first waits.
                activate = GuiNative.CreateEvent(ref sa, false, false, EventName(id));
                if (activate.IsInvalid)
                    activate = GuiNative.OpenEvent(SecurityNative.SYNCHRONIZE | GuiNative.EVENT_MODIFY_STATE
                        | SecurityNative.READ_CONTROL, false, EventName(id));
                if (activate.IsInvalid)
                    throw Taken(Marshal.GetLastWin32Error(), "the activation event exists but cannot be waited on and signalled");
                if (!IsTrustedOwner(OwnerSidOf(activate), sid))
                {
                    activate.Dispose();
                    throw Taken(GuiNative.ERROR_ACCESS_DENIED, "the activation event is owned by another account");
                }

                mutex = GuiNative.CreateMutex(ref sa, false, MutexName(id));
                int error = Marshal.GetLastWin32Error();
                if (mutex.IsInvalid)
                {
                    // Exists, but made with security we may not open: another first instance only when the
                    // object is ours (owned by this user); another object type or owner is hostile.
                    if (error != GuiNative.ERROR_ACCESS_DENIED || !MutexOwnedBy(MutexName(id), sid))
                    {
                        activate.Dispose();
                        throw Taken(error, error == GuiNative.ERROR_ACCESS_DENIED
                            ? "the lock is held by another account" : "the lock name is held by another kind of object");
                    }
                    isFirst = false;
                }
                else
                {
                    if (error == GuiNative.ERROR_ALREADY_EXISTS && !IsTrustedOwner(OwnerSidOf(mutex), sid))
                    {
                        mutex.Dispose();
                        activate.Dispose();
                        throw Taken(GuiNative.ERROR_ACCESS_DENIED, "the lock is owned by another account");
                    }
                    isFirst = error != GuiNative.ERROR_ALREADY_EXISTS;
                }
                // Last, so the throws above have nothing more to close; optional, so it never throws itself.
                bool ackCreated;
                SafeWaitHandle answer = CreateAck(ref sa, sid, AckName(id), out ackCreated);
                // The lock is held but this launch made the acknowledgement event: the holder has none, so it is an
                // older copy that never answers (or one between its lock and its event, still starting). Asking it
                // is Unconfirmed then, and the launch exits as it always did instead of waiting for the lock.
                if (answer != null && ackCreated && !isFirst)
                {
                    answer.Dispose();
                    answer = null;
                }
                ack = answer;
            }
            finally { GuiNative.LocalFree(sd); }

            if (isFirst)
            {
                waiter = new Thread(WaitLoop) { IsBackground = true, Name = "RamOptimiser activation" };
                waiter.Start();
            }
        }

        /// <summary>
        /// Takes the lock from a copy that is about to give it up (the copy that asked for elevation, P10). The
        /// first try that is not first is disposed; then this waits for released (the old copy's exit) for at
        /// most timeout and tries once more, returning that instance whether or not it is first. create's
        /// exceptions (SingleInstanceNameTakenException) reach the caller unchanged.
        /// </summary>
        internal static SingleInstance AcquireAfter(Func<SingleInstance> create, WaitHandle released, TimeSpan timeout)
        {
            if (create == null) throw new ArgumentNullException("create");
            if (released == null) throw new ArgumentNullException("released");
            SingleInstance first = create();
            if (first.IsFirstInstance) return first;
            first.Dispose();
            released.WaitOne(timeout);
            return create();
        }

        /// <summary>
        /// The acknowledgement event: made like the activation event (same descriptor, opened with the same rights,
        /// trusted only when owned by the user), but optional. A name that fails any of those checks gives null:
        /// activation then works as before, only unacknowledged, and a later launch exits as it always did. It is a
        /// manual-reset event, so one answer releases every launch waiting at that moment (their signals can merge
        /// into one wake of the waiter). created is true when this call made the event.
        /// </summary>
        static SafeWaitHandle CreateAck(ref GuiNative.SECURITY_ATTRIBUTES sa, string sid, string name, out bool created)
        {
            SafeWaitHandle handle = GuiNative.CreateEvent(ref sa, true, false, name);
            created = !handle.IsInvalid && Marshal.GetLastWin32Error() != GuiNative.ERROR_ALREADY_EXISTS;
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = GuiNative.OpenEvent(SecurityNative.SYNCHRONIZE | GuiNative.EVENT_MODIFY_STATE
                    | SecurityNative.READ_CONTROL, false, name);
            }
            if (handle.IsInvalid || !IsTrustedOwner(OwnerSidOf(handle), sid))
            {
                handle.Dispose();
                created = false;
                return null;
            }
            return handle;
        }

        static SingleInstanceNameTakenException Taken(int error, string why)
        {
            return new SingleInstanceNameTakenException(error,
                "Another program holds this app's single-instance name (" + why + "); running without single-instance.");
        }

        static bool MutexOwnedBy(string name, string sid)
        {
            using (SafeWaitHandle probe = SecurityNative.OpenMutex(SecurityNative.READ_CONTROL, false, name))
                return !probe.IsInvalid && IsTrustedOwner(OwnerSidOf(probe), sid);
        }

        public bool IsFirstInstance { get { return isFirst; } }

        /// <summary>Test hook: runs after SignalAndWaitForAck has sent its request, before it waits for the answer.</summary>
        internal Action Signalled { get; set; }

        /// <summary>
        /// The first step of every way out (Exit, the fatal error box, the emergency cleanup): from now on a request
        /// is neither answered nor raised, whatever the handlers, so a launch waits for the lock and starts once this
        /// copy lets go, instead of handing its request to a copy that is going away. Idempotent, safe after
        /// Dispose, never throws.
        /// </summary>
        public void BeginClosing()
        {
            lock (handlerGate) closing = true;
        }

        public static string MutexName(string id) { return NamePrefix + id; }
        public static string EventName(string id) { return NamePrefix + id + "-activate"; }
        public static string AckName(string id) { return NamePrefix + id + "-ack"; }

        public static string CurrentUserSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return identity.User.Value;
        }

        /// <summary>Only the current user. Our own objects name the user as owner even when created elevated.</summary>
        internal static bool IsTrustedOwner(string ownerSid, string userSid)
        {
            return !string.IsNullOrEmpty(ownerSid) && !string.IsNullOrEmpty(userSid)
                && string.Equals(ownerSid, userSid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The owner SID of a kernel object (the handle needs READ_CONTROL), or null when it cannot be read.</summary>
        internal static string OwnerSidOf(SafeWaitHandle handle)
        {
            if (handle == null || handle.IsInvalid) return null;
            IntPtr owner, sd;
            uint rc = SecurityNative.GetSecurityInfo(handle, SecurityNative.SE_KERNEL_OBJECT,
                SecurityNative.OWNER_SECURITY_INFORMATION, out owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out sd);
            if (rc != 0) return null;
            try { return owner == IntPtr.Zero ? null : new SecurityIdentifier(owner).Value; }
            finally { GuiNative.LocalFree(sd); }
        }

        /// <summary>Asks the first instance to show its window. False when this is the first instance or the signal failed.</summary>
        public bool SignalFirstInstance()
        {
            if (isFirst || activate.IsInvalid || Volatile.Read(ref disposed) != 0) return false;
            return GuiNative.SetEvent(activate);
        }

        /// <summary>
        /// Asks the first instance to show its window and waits up to timeout for its acknowledgement, which it sends
        /// only when the request will be raised (a handler, or none added yet). NoAnswer: the copy is exiting (it
        /// has begun closing, or its handlers were removed). Unconfirmed: this is the first instance, the signal failed, the acknowledgement
        /// event is unusable, or the running copy has none (an older copy); the request is still sent then, as
        /// SignalFirstInstance does.
        /// </summary>
        public ActivationAnswer SignalAndWaitForAck(TimeSpan timeout)
        {
            if (isFirst || Volatile.Read(ref disposed) != 0) return ActivationAnswer.Unconfirmed;
            if (ack == null)
            {
                SignalFirstInstance();
                return ActivationAnswer.Unconfirmed;
            }
            // An answer left set for a launch that stopped waiting is not an answer to this one.
            GuiNative.ResetEvent(ack);
            if (!GuiNative.SetEvent(activate)) return ActivationAnswer.Unconfirmed;
            Action signalled = Signalled;
            if (signalled != null) signalled();
            using (var answered = new HandleWaitHandle(ack))
                return answered.WaitOne(timeout) ? ActivationAnswer.Acknowledged : ActivationAnswer.NoAnswer;
        }

        void WaitLoop()
        {
            // An exception on this thread would end the process (WaitAny throws UnauthorizedAccessException on
            // a handle without SYNCHRONIZE, for one). The waiter ends instead; only activation stops working.
            try
            {
                using (var activateWait = new HandleWaitHandle(activate))
                using (HandleWaitHandle answer = ack == null ? null : new HandleWaitHandle(ack))
                {
                    var handles = new WaitHandle[] { stop, activateWait, replay };
                    int woke;
                    while ((woke = WaitHandle.WaitAny(handles)) != 0)
                    {
                        // A launch's request (1) or our own replay of a kept one (2). A request raises the handler
                        // anyway, so a replay queued behind it would only raise it twice.
                        bool asked = woke == 1;
                        if (asked) replay.Reset();
                        EventHandler handler;
                        bool taken;
                        lock (handlerGate)
                        {
                            handler = closing ? null : activationRequested;
                            if (handler == null && !closing) pendingActivation = true;
                            // Taken when a handler gets it now, or none has been added yet (the copy is starting and
                            // the first handler gets the kept request). Once the handlers are gone again, or the copy
                            // has begun closing, nothing is sent, and the launch that asked waits for the lock instead.
                            // A replay is never answered: its launch had its answer when the request arrived.
                            taken = asked && !closing && (handler != null || !subscribed);
                        }
                        if (taken && answer != null) GuiNative.SetEvent(answer.SafeWaitHandle);
                        if (handler == null) continue;
                        try { handler(this, EventArgs.Empty); }
                        catch (Exception ex) { Trace.WriteLine("ActivationRequested handler failed: " + ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Single-instance activation wait stopped: " + ex.GetType().Name);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Set();
            // The waiter keeps its own reference to the activation handle, so closing ours is safe at once.
            // The stop event is closed only once the waiter has left (not when Dispose runs inside a handler).
            bool waiterGone = waiter == null || (waiter != Thread.CurrentThread && waiter.Join(2000));
            mutex.Dispose();
            activate.Dispose();
            if (ack != null) ack.Dispose();
            if (waiterGone)
            {
                stop.Dispose();
                replay.Dispose();
            }
        }

        /// <summary>Lets WaitHandle.WaitAny wait on a handle created by P/Invoke, without owning it.</summary>
        sealed class HandleWaitHandle : WaitHandle
        {
            public HandleWaitHandle(SafeWaitHandle handle)
            {
                bool added = false;
                handle.DangerousAddRef(ref added);
                SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), false);
                this.handle = handle;
                this.added = added;
            }

            readonly SafeWaitHandle handle;
            readonly bool added;

            protected override void Dispose(bool explicitDisposing)
            {
                base.Dispose(explicitDisposing);
                if (added) handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// The single-instance mutex or event exists but is not this user's (another owner, another object type,
    /// or not usable). A Win32Exception, so existing catch blocks keep running without single-instance.
    /// </summary>
    public sealed class SingleInstanceNameTakenException : Win32Exception
    {
        public SingleInstanceNameTakenException(int error, string message) : base(error, message) { }
    }
}
