// Process-wide COM security for the GUI (review 2026-11-19 of the update check): the elevated copy talks to
// Explorer's ShellWindows server, which runs at the user's medium integrity, so it must refuse custom marshalers
// and say who may call into it. These tests check the policy values and that AppMain applies them first. Nothing
// here calls CoInitializeSecurity: it is once per process, and this runner's own COM use would then follow it.
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using RamOptimiser.App;

namespace RamOptimiser.Tests
{
    public static class ComSecurityTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        public static int Run()
        {
            PolicyTests();
            LogTests();
            WiringTests();
            return failed;
        }

        static void PolicyTests()
        {
            Check("com: no custom marshalers, no activate-as-activator", ComSecurityPolicy.Capabilities == (0x2000 | 0x1000),
                ComSecurityPolicy.Capabilities.ToString("X"));
            Check("com: identify-level impersonation for calls out (servers cannot act as this admin)", ComSecurityPolicy.ImpLevel == 2);
            Check("com: the system's default authentication level", ComSecurityPolicy.AuthnLevel == 0);

            RawSecurityDescriptor sd = null;
            try { sd = new RawSecurityDescriptor(ComSecurityPolicy.AccessSddl); }
            catch (Exception ex) { Check("com: the access list parses", false, ex.Message); return; }
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            Check("com: owner and group are set (CoInitializeSecurity requires both)", admins.Equals(sd.Owner) && admins.Equals(sd.Group));
            Check("com: no system ACL", sd.SystemAcl == null);
            Check("com: a DACL, never a NULL one (that lets anyone call)", sd.DiscretionaryAcl != null);
            if (sd.DiscretionaryAcl == null) return;

            var allowed = new System.Collections.Generic.List<string>();
            bool onlyAllow = true, onlyExecute = true;
            foreach (GenericAce ace in sd.DiscretionaryAcl)
            {
                var known = ace as CommonAce;
                if (known == null || known.AceQualifier != AceQualifier.AccessAllowed) { onlyAllow = false; continue; }
                if (known.AccessMask != 3) onlyExecute = false; // COM_RIGHTS_EXECUTE | COM_RIGHTS_EXECUTE_LOCAL
                allowed.Add(known.SecurityIdentifier.Value);
            }
            allowed.Sort(StringComparer.Ordinal);
            string got = string.Join(",", allowed.ToArray());
            Check("com: only SYSTEM, Administrators and the process's own user may call in", got == "S-1-5-10,S-1-5-18,S-1-5-32-544", got);
            Check("com: allow entries only", onlyAllow);
            Check("com: local execute rights only", onlyExecute);
        }

        static void LogTests()
        {
            Check("com log: nothing on success", ComSecurityPolicy.LogLine(0) == null);
            string late = ComSecurityPolicy.LogLine(unchecked((int)0x80010119));
            Check("com log: too late is named", late != null && late.Contains("already set") && late.Contains("0x80010119"), late);
            string other = ComSecurityPolicy.LogLine(unchecked((int)0x80070057));
            Check("com log: another failure gives its code", other != null && other.Contains("0x80070057"), other);
        }

        static string Source(params string[] parts)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("..", "src"));
            foreach (string part in parts) path = Path.Combine(path, part);
            path = Path.GetFullPath(path);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        static void WiringTests()
        {
            string main = Source("App", "AppMain.cs");
            int at = main.IndexOf("public static int Main(string[] args)", StringComparison.Ordinal);
            string body = at < 0 ? "" : main.Substring(at);
            int dll = body.IndexOf("RamOptimiser.Native.RestrictDllSearchToSystem32();", StringComparison.Ordinal);
            int com = body.IndexOf("ComSecurity.Apply();", StringComparison.Ordinal);
            int next = body.IndexOf("AppDomain.CurrentDomain.UnhandledException", StringComparison.Ordinal);
            Check("wiring: Main applies COM security straight after the DLL search fix, before anything else",
                dll >= 0 && com > dll && next > com && body.Substring(dll, com - dll).Split('\n').Length == 2, com + " " + dll);

            string apply = Source("App", "ComSecurity.cs");
            Check("wiring: Apply passes the policy's descriptor, made absolute", apply.Contains("ComSecurityPolicy.AccessSddl")
                && apply.Contains("ConvertStringSecurityDescriptorToSecurityDescriptorW") && apply.Contains("MakeAbsoluteSD("));
            Check("wiring: Apply passes the policy's levels and capabilities", apply.Contains("ComSecurityPolicy.AuthnLevel")
                && apply.Contains("ComSecurityPolicy.ImpLevel") && apply.Contains("ComSecurityPolicy.Capabilities"));
            Check("wiring: Apply logs a failure and never throws", apply.Contains("ComSecurityPolicy.LogLine(")
                && apply.Contains("ErrorLog.TryWrite(") && apply.Contains("catch (Exception"));
        }
    }
}
