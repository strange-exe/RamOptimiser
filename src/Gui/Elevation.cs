using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RamOptimiser.Gui
{
    public static class Elevation
    {
        /// <summary>Win32 ERROR_CANCELLED: the user answered No on the UAC prompt.</summary>
        public const int ErrorCancelled = 1223;

        public static bool IsElevated()
        {
            return Privileges.IsElevated();
        }

        /// <summary>
        /// Starts exePath again through the UAC prompt. Returns false when the user cancels the prompt;
        /// any other failure is rethrown. The caller decides whether to exit after a true result.
        /// </summary>
        public static bool TryRelaunchElevated(string exePath, string args)
        {
            return TryRelaunchElevated(exePath, args, IntPtr.Zero);
        }

        /// <summary>
        /// As above, naming the window that asked. ErrorDialog stays off, so no shell error box appears. Note:
        /// .NET Framework copies ErrorDialogParentHandle into SHELLEXECUTEINFO.hwnd only when ErrorDialog is on
        /// (reference source, Process.StartWithShellExecuteEx), so with it off the handle does not own the UAC
        /// prompt; it is kept so a later change of that rule needs no caller change.
        /// </summary>
        public static bool TryRelaunchElevated(string exePath, string args, IntPtr ownerWindow)
        {
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentException("exePath is required.", "exePath");
            var psi = new ProcessStartInfo(exePath, args ?? "")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                ErrorDialog = false,
                ErrorDialogParentHandle = ownerWindow
            };
            try
            {
                using (Process.Start(psi)) { }
                return true;
            }
            catch (Win32Exception ex)
            {
                if (IsUacCancel(ex)) return false;
                throw;
            }
        }

        internal static bool IsUacCancel(Win32Exception ex)
        {
            return ex != null && ex.NativeErrorCode == ErrorCancelled;
        }

        /// <summary>
        /// Runs TryRelaunchElevated on its own STA thread (ShellExecuteEx expects COM initialised; the thread pool
        /// is MTA), so the window keeps painting while the UAC prompt is up. done runs on that worker thread with
        /// (true, null) after a launch, (false, null) after a cancel, or (false, ex) after a failure.
        /// </summary>
        public static void RelaunchAsync(string exePath, string args, IntPtr ownerWindow, Action<bool, Exception> done)
        {
            RelaunchAsync(() => TryRelaunchElevated(exePath, args, ownerWindow), done);
        }

        /// <summary>The worker itself, with the launch passed in so tests never reach UAC. Returns the started thread.</summary>
        internal static Thread RelaunchAsync(Func<bool> launch, Action<bool, Exception> done)
        {
            if (launch == null) throw new ArgumentNullException("launch");
            if (done == null) throw new ArgumentNullException("done");
            var t = new Thread(() =>
            {
                bool launched = false;
                Exception error = null;
                try { launched = launch(); }
                catch (Exception ex) { error = ex; }
                // An exception here would end the process: the caller's report is best effort.
                try { done(launched, error); }
                catch (Exception ex) { Trace.WriteLine("Elevation result handler failed: " + ex.GetType().Name); }
            }) { IsBackground = true, Name = "elevate" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return t;
        }

        /// <summary>
        /// Tells the elevated copy which process asked for it, so it waits for that copy to exit before taking
        /// the single-instance lock. The value only ever selects a pid to wait for, for at most 10 s.
        /// </summary>
        public const string HandoffArgument = "--handoff=";

        public static string HandoffArgs(int pid)
        {
            if (pid <= 0) throw new ArgumentOutOfRangeException("pid");
            return HandoffArgument + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Names the account that asked for elevation. For a standard user, UAC asks for an administrator's
        /// password and the new copy runs as that administrator, with that account's settings and start-up
        /// task; the copy compares this SID with its own to notice.
        /// </summary>
        public const string LauncherArgument = "--launched-by";

        /// <summary>Most sub-authorities a SID can have (SID_MAX_SUB_AUTHORITIES).</summary>
        const int MaxSubAuthorities = 15;

        /// <summary>The command line for the elevated copy: the launching user's SID, checked first.</summary>
        public static string RelaunchArgs(string launcherSid)
        {
            if (!IsWellFormedSid(launcherSid)) throw new ArgumentException("Not a SID.", "launcherSid");
            return LauncherArgument + " " + launcherSid;
        }

        /// <summary>
        /// Strict SID text: "S-1-", a decimal authority, then 1 to 15 decimal sub-authorities, each a 32-bit
        /// value without leading zeros. Nothing else (spaces, quotes, hex, lower case) passes, because the
        /// value crosses the UAC boundary on the command line.
        /// </summary>
        public static bool IsWellFormedSid(string sid)
        {
            if (string.IsNullOrEmpty(sid) || sid.Length > 200) return false;
            string[] parts = sid.Split('-');
            if (parts.Length < 4 || parts.Length > 3 + MaxSubAuthorities) return false;
            if (parts[0] != "S" || parts[1] != "1") return false;
            for (int i = 2; i < parts.Length; i++)
                if (!IsDecimalUInt32(parts[i])) return false;
            return true;
        }

        static bool IsDecimalUInt32(string s)
        {
            if (s.Length == 0 || s.Length > 10) return false;
            if (s.Length > 1 && s[0] == '0') return false;
            foreach (char c in s)
                if (c < '0' || c > '9') return false;
            return ulong.Parse(s, System.Globalization.CultureInfo.InvariantCulture) <= uint.MaxValue;
        }

        /// <summary>True when a well-formed launcher SID names another account than this process runs as.</summary>
        public static bool IsOtherAccount(string launcherSid, string currentSid)
        {
            return IsWellFormedSid(launcherSid) && !string.IsNullOrEmpty(currentSid)
                && !string.Equals(launcherSid, currentSid, StringComparison.Ordinal);
        }
    }
}
