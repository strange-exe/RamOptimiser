using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
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

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        const uint TokenQuery = 0x0008;
        const int TokenIntegrityLevel = 25;
        /// <summary>SECURITY_MANDATORY_HIGH_RID. https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids</summary>
        public const long HighIntegrityRid = 0x3000;

        /// <summary>
        /// Whether this process runs at high integrity or above, whatever its groups (IsElevated asks only about the
        /// Administrators group). When the level cannot be read the answer is true, so callers take the cautious path.
        /// </summary>
        public static bool IsHighIntegrity()
        {
            IntPtr token = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return true;
                int size;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out size);
                if (size <= 0 || size > 4096) return true;
                buffer = Marshal.AllocHGlobal(size);
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out size)) return true;
                // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first member is the label SID.
                long? rid = IntegrityRid(new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value);
                return rid == null || IsHighIntegrityRid(rid.Value);
            }
            catch (Exception) { return true; }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        public static bool IsHighIntegrityRid(long rid) { return rid >= HighIntegrityRid; }

        /// <summary>The level of a mandatory label SID ("S-1-16-12288" gives 12288); null for any other text.</summary>
        public static long? IntegrityRid(string sid)
        {
            const string prefix = "S-1-16-";
            if (sid == null || !sid.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string digits = sid.Substring(prefix.Length);
            uint rid;
            if (digits.Length == 0 || !uint.TryParse(digits, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out rid)) return null;
            return rid;
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
