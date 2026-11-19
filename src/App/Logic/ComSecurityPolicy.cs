using System;

namespace RamOptimiser.App
{
    /// <summary>
    /// The process-wide COM security the GUI sets first thing in Main (ComSecurity.Apply). The elevated copy calls
    /// Explorer's ShellWindows server, which runs as the signed-in user at medium integrity, to open the download
    /// page unelevated; without this, COM would set the registry defaults on first use, which accept any custom
    /// marshaler such a server hands back. Pure values, so the tests check them without calling COM.
    /// https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coinitializesecurity
    /// </summary>
    public static class ComSecurityPolicy
    {
        /// <summary>
        /// Who may call into this process: SYSTEM, Administrators and the process's own user (PS, "SELF" in COM),
        /// each with COM_RIGHTS_EXECUTE | COM_RIGHTS_EXECUTE_LOCAL. The app serves no COM objects; a NULL descriptor
        /// would let anyone in, anonymous callers included. Owner and group are set, as CoInitializeSecurity requires.
        /// </summary>
        public const string AccessSddl = "O:BAG:BAD:(A;;0x3;;;SY)(A;;0x3;;;BA)(A;;0x3;;;PS)";

        /// <summary>RPC_C_AUTHN_LEVEL_DEFAULT: the system picks, as it would without this call.</summary>
        public const uint AuthnLevel = 0;

        /// <summary>RPC_C_IMP_LEVEL_IDENTIFY: a server this app calls may learn who it is, never act as it.</summary>
        public const uint ImpLevel = 2;

        /// <summary>EOAC_NO_CUSTOM_MARSHAL (only system-marked safe marshalers unmarshal here) | EOAC_DISABLE_AAA.</summary>
        public const uint Capabilities = 0x2000 | 0x1000;

        public const int RpcETooLate = unchecked((int)0x80010119);

        /// <summary>The error.log line for a CoInitializeSecurity result, or null when it worked.</summary>
        public static string LogLine(int hr)
        {
            if (hr >= 0) return null;
            string code = "0x" + hr.ToString("X8");
            if (hr == RpcETooLate)
                return "COM security was already set for this process (" + code + "); the stricter settings are not in force";
            return "could not set COM security (" + code + "); the system defaults apply";
        }
    }
}
