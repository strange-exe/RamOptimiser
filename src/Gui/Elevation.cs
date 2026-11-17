using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

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
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentException("exePath is required.", "exePath");
            var psi = new ProcessStartInfo(exePath, args ?? "")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
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
    }
}
