using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace RamOptimiser
{
    /// <summary>What a try for the clean lock got. Unavailable: the lock could not be used, so the clean runs unguarded.</summary>
    public enum LockState { Held, Busy, Unavailable }

    /// <summary>
    /// The cross-process clean lock: ramopt clean, ramopt watch and the app never flush and purge at the same time,
    /// which would double the page-file writes and make each one's "freed" figure wrong.
    /// A named mutex in the Global namespace, because the Local namespace is per session and the memory lists are
    /// system-wide (learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces). Only Administrators
    /// and SYSTEM may open it, so a standard-user program cannot hold it to block cleans. Such a program can still take
    /// the name first; the owner check sees that, and the clean runs unguarded (today's behaviour) with a note.
    /// A .NET Mutex belongs to the thread that took it, so take it, clean and dispose on one thread.
    /// </summary>
    public sealed class CleanLock : IDisposable
    {
        public const string Name = @"Global\RamOptimiser-Clean";
        const string AdministratorsSid = "S-1-5-32-544", SystemSid = "S-1-5-18";
        const string Unguarded = ", so this clean did not check for other cleans";

        Mutex mutex;

        CleanLock(Mutex m) { mutex = m; }

        /// <summary>Takes the lock, waiting up to wait for a clean in another process to finish.</summary>
        public static LockState TryAcquire(TimeSpan wait, out CleanLock held, out string note)
        {
            return TryAcquire(Name, AdminOnly(), TrustedOwner, wait, out held, out note);
        }

        /// <summary>
        /// The same with the name, the security for a new mutex and the owner rule passed in: tests use Local names and
        /// a descriptor the current user owns. note says why the lock is Unavailable, or that the last holder ended
        /// without releasing it; it is null otherwise.
        /// </summary>
        internal static LockState TryAcquire(string name, MutexSecurity security, Func<string, bool> trusted, TimeSpan wait,
            out CleanLock held, out string note)
        {
            held = null;
            note = null;
            Mutex m = null;
            try
            {
                bool createdNew;
                // When the name exists, the security passed here is ignored, and a caller without FullControl gets
                // UnauthorizedAccessException (Mutex constructor docs, .NET Framework 4.8).
                m = new Mutex(false, name, out createdNew, security);
                string owner = createdNew ? null : OwnerOf(m);
                if (Classify(createdNew, owner, trusted) == LockState.Unavailable)
                {
                    note = (owner == null ? "could not read who owns the clean lock" : "another program owns the clean lock's name") + Unguarded;
                    return LockState.Unavailable;
                }
                bool got;
                try { got = m.WaitOne(Clamp(wait)); }
                catch (AbandonedMutexException)
                {
                    // The wait succeeded: this thread owns it now. The holder ended (or crashed) mid-clean.
                    got = true;
                    note = "the last clean ended without finishing";
                }
                if (!got) return LockState.Busy;
                held = new CleanLock(m);
                m = null;
                return LockState.Held;
            }
            catch (UnauthorizedAccessException ex) { note = Unopened(ex); }
            catch (WaitHandleCannotBeOpenedException ex) { note = Unopened(ex); }
            catch (InvalidOperationException ex) { note = Unopened(ex); }
            catch (IOException ex) { note = Unopened(ex); }
            finally { if (m != null) m.Close(); }
            return LockState.Unavailable;
        }

        static string Unopened(Exception ex)
        {
            return "could not open the clean lock (" + ex.Message.Trim().TrimEnd('.') + ")" + Unguarded;
        }

        /// <summary>WaitOne takes 0 to int.MaxValue milliseconds.</summary>
        static TimeSpan Clamp(TimeSpan wait)
        {
            if (wait < TimeSpan.Zero) return TimeSpan.Zero;
            TimeSpan max = TimeSpan.FromMilliseconds(int.MaxValue);
            return wait > max ? max : wait;
        }

        /// <summary>The owner SID of an existing mutex, or null when it cannot be read.</summary>
        static string OwnerOf(Mutex m)
        {
            try
            {
                var sid = m.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                return sid == null ? null : sid.Value;
            }
            catch (UnauthorizedAccessException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (IdentityNotMappedException) { return null; }
        }

        /// <summary>Only Administrators and SYSTEM may open the mutex; Administrators own it.</summary>
        internal static MutexSecurity AdminOnly()
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var sec = new MutexSecurity();
            // A process that is not elevated cannot set this owner, so its create fails and it runs unguarded; it could
            // not clean without elevation anyway.
            sec.SetOwner(admins);
            sec.AddAccessRule(new MutexAccessRule(admins, MutexRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new MutexAccessRule(system, MutexRights.FullControl, AccessControlType.Allow));
            return sec;
        }

        /// <summary>
        /// Pure: Held when the lock may be waited for (this call created it, or Administrators or SYSTEM own it), else
        /// Unavailable (someone else took the name, or its owner could not be read).
        /// </summary>
        internal static LockState Classify(bool createdNew, string ownerSid)
        {
            return Classify(createdNew, ownerSid, TrustedOwner);
        }

        static LockState Classify(bool createdNew, string ownerSid, Func<string, bool> trusted)
        {
            if (createdNew) return LockState.Held;
            return ownerSid != null && trusted(ownerSid) ? LockState.Held : LockState.Unavailable;
        }

        /// <summary>Administrators (S-1-5-32-544) or SYSTEM (S-1-5-18).</summary>
        internal static bool TrustedOwner(string sid)
        {
            return sid == AdministratorsSid || sid == SystemSid;
        }

        /// <summary>Releases and closes the lock. Called from another thread than the taker's, it only closes the handle.</summary>
        public void Dispose()
        {
            Mutex m = mutex;
            if (m == null) return;
            mutex = null;
            try { m.ReleaseMutex(); }
            catch (ApplicationException) { } // not this thread's: the kernel abandons it when the owner thread ends
            finally { m.Close(); }
        }
    }
}
