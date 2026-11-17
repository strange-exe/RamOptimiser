using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RamOptimiser
{
    /// <summary>What Windows says about memory right now: its own low and high signals, not the user's threshold.</summary>
    public enum MemoryCondition { Unknown, Low, Neutral, High }

    /// <summary>
    /// Windows' memory resource notifications (CreateMemoryResourceNotification). Both handles are created once, on
    /// first use, and kept for the life of the process; Windows closes them at exit. Every call is read-only, never
    /// blocks and never throws. The low level is a fixed kernel level, so Low may be rare: Neutral and High are the
    /// usual answers, and that is honest output.
    /// </summary>
    public static class MemoryPressure
    {
        static readonly object gate = new object();
        static bool created;
        static SafeWaitHandle low, high;
        static WaitHandle lowWait;

        static void Ensure()
        {
            lock (gate)
            {
                if (created) return;
                created = true;
                low = Create(Native.LowMemoryResourceNotification);
                high = Create(Native.HighMemoryResourceNotification);
                if (Usable(low)) lowWait = new KernelWaitHandle(low);
            }
        }

        static SafeWaitHandle Create(int type)
        {
            try { return Native.CreateMemoryResourceNotification(type); }
            catch (EntryPointNotFoundException) { return null; }
            catch (DllNotFoundException) { return null; }
        }

        static bool Usable(SafeWaitHandle h)
        {
            return h != null && !h.IsInvalid && !h.IsClosed;
        }

        /// <summary>The low-memory notification handle (signalled while memory is low), or null when unavailable.</summary>
        internal static SafeWaitHandle LowHandle
        {
            get { Ensure(); return Usable(low) ? low : null; }
        }

        /// <summary>
        /// The low-memory notification as a WaitHandle for WaitHandle.WaitAny, or null when unavailable. The same
        /// object every call; it does not own the handle, so disposing it never closes the notification.
        /// </summary>
        public static WaitHandle LowWaitHandle()
        {
            Ensure();
            return lowWait;
        }

        /// <summary>Asks Windows for both signals now. Unknown when either cannot be read.</summary>
        public static MemoryCondition Query()
        {
            try
            {
                Ensure();
                bool lowOk = false, lowState = false, highOk = false, highState = false;
                int state;
                if (Usable(low) && Native.QueryMemoryResourceNotification(low, out state)) { lowOk = true; lowState = state != 0; }
                if (Usable(high) && Native.QueryMemoryResourceNotification(high, out state)) { highOk = true; highState = state != 0; }
                return Classify(lowOk, lowState, highOk, highState);
            }
            catch (EntryPointNotFoundException) { return MemoryCondition.Unknown; }
            catch (DllNotFoundException) { return MemoryCondition.Unknown; }
            catch (ObjectDisposedException) { return MemoryCondition.Unknown; }
        }

        /// <summary>
        /// Pure: the condition two query results add up to. A failed query is Unknown; low alone is Low, high alone
        /// is High, neither is the in-between band (Neutral), and both at once (should not happen) is Unknown.
        /// </summary>
        internal static MemoryCondition Classify(bool lowOk, bool low, bool highOk, bool high)
        {
            if (!lowOk || !highOk) return MemoryCondition.Unknown;
            if (low && high) return MemoryCondition.Unknown;
            if (low) return MemoryCondition.Low;
            if (high) return MemoryCondition.High;
            return MemoryCondition.Neutral;
        }

        /// <summary>
        /// A WaitHandle over a handle someone else owns (the same pattern as SingleInstance's, which the CLI does not
        /// compile). It holds a reference on the owner while alive and never closes the handle itself.
        /// </summary>
        sealed class KernelWaitHandle : WaitHandle
        {
            readonly SafeWaitHandle owner;
            bool added;

            public KernelWaitHandle(SafeWaitHandle handle)
            {
                bool ok = false;
                handle.DangerousAddRef(ref ok);
                SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), false);
                owner = handle;
                added = ok;
            }

            protected override void Dispose(bool explicitDisposing)
            {
                base.Dispose(explicitDisposing);
                if (added) { added = false; owner.DangerousRelease(); }
            }
        }
    }
}
