// Tests for the HKLM\SOFTWARE\RamOptimiser trust check (MachineKeyTrust), the key walk (AppRegistry) and Power
// Clean's registry journal (PowerCleanJournal). The walk and the journal run over an in-memory registry: nothing
// here writes the real registry. The real MachineRegistry is only read (HKLM\SOFTWARE\Microsoft and the
// HKLM\SYSTEM\CurrentControlSet link), to prove its link check and kernel-path plumbing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class MachineKeyTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        const string Ba = "S-1-5-32-544", Sy = "S-1-5-18", Users = "S-1-5-32-545", Everyone = "S-1-1-0";
        const string AuthUsers = "S-1-5-11", Interactive = "S-1-5-4", CreatorOwner = "S-1-3-0", OwnerRights = "S-1-3-4";
        const string AppPackages = "S-1-15-2-1", SomeUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        const string OtherUser = "S-1-5-21-1111111111-2222222222-3333333333-1002";
        const int KeyRead = 0x20019, KeyAll = 0xF003F;

        public static int Run()
        {
            TrustTests();
            DescriptorTests();
            WalkTests();
            JournalTests();
            StartupJournalTests();
            RealRegistryTests();
            Check("journal file: the old stopped-services.txt store is gone",
                typeof(PowerCleanJournal).Assembly.GetType("RamOptimiser.Gui.StoppedServiceStore") == null);
            return failed;
        }

        // ---------------------------------------------------------------- MachineKeyTrust.Problem (pure)

        static KeyAce Allow(string sid, int mask) { return new KeyAce(true, sid, mask, false); }

        static string P(string owner, params KeyAce[] aces) { return MachineKeyTrust.Problem(owner, aces); }

        static void TrustTests()
        {
            KeyAce[] good = { Allow(Sy, KeyAll), Allow(Ba, KeyAll), Allow(Users, KeyRead) };
            Check("trust: Administrators owner, admin-only writes", P(Ba, good) == null, P(Ba, good));
            Check("trust: SYSTEM owner", P(Sy, good) == null);
            Check("trust: owner compared ignoring case", P("s-1-5-32-544", good) == null);
            Check("trust: a user owner is refused", P(SomeUser, good) != null);
            Check("trust: a Users owner is refused", P(Users, good) != null);
            Check("trust: no owner is refused", P(null, good) != null);
            Check("trust: an empty owner is refused", P("", good) != null);
            Check("trust: a NULL DACL is refused", MachineKeyTrust.Problem(Ba, null) != null);
            Check("trust: an empty DACL is fine (only the owner)", P(Ba) == null);

            int[] rights = { 0x2, 0x4, 0x20, 0x10000, 0x40000, 0x80000, 0x02000000, 0x10000000, 0x40000000 };
            foreach (int r in rights)
                Check("trust: Users granted 0x" + r.ToString("X") + " is refused", P(Ba, Allow(Sy, KeyAll), Allow(Users, r)) != null);
            Check("trust: Users granted full control is refused", P(Ba, Allow(Users, KeyAll)) != null);
            Check("trust: Users read is fine", P(Ba, Allow(Users, KeyRead)) == null);
            Check("trust: Users read plus notify and enumerate is fine", P(Ba, Allow(Users, 0x20000 | 0x1 | 0x8 | 0x10)) == null);
            Check("trust: generic read is fine", P(Ba, Allow(Users, unchecked((int)0x80000000))) == null);
            foreach (string sid in new[] { Everyone, AuthUsers, Interactive, OwnerRights, AppPackages, SomeUser })
                Check("trust: " + sid + " allowed to set values is refused", P(Ba, Allow(sid, 0x2)) != null);
            Check("trust: deny entries never grant", P(Ba, new KeyAce(false, Everyone, KeyAll, false), Allow(Ba, KeyAll)) == null);
            Check("trust: SYSTEM and Administrators may write", P(Ba, Allow(Sy, -1), Allow(Ba, -1)) == null);
            Check("trust: one bad entry among good ones is refused", P(Ba, Allow(Sy, KeyAll), Allow(Ba, KeyAll), Allow(AuthUsers, 0x4), Allow(Users, KeyRead)) != null);
            Check("trust: CREATOR OWNER inherit-only is fine (each subkey's owner is checked)",
                P(Ba, new KeyAce(true, CreatorOwner, 0x10000000, true), Allow(Ba, KeyAll)) == null);
            Check("trust: CREATOR OWNER on the key itself is refused", P(Ba, Allow(CreatorOwner, KeyAll)) != null);
            Check("trust: Users write, inherit-only, is refused (it reaches the subkeys)",
                P(Ba, new KeyAce(true, Users, 0x2, true)) != null);
            Check("trust: an entry of unknown kind is refused", P(Ba, new KeyAce(true, null, -1, false)) != null);
            string why = P(SomeUser, good);
            Check("trust: the reason names the owner", why != null && why.Contains(SomeUser), why);
            why = P(Ba, Allow(Everyone, 0x2));
            Check("trust: the reason names the account", why != null && why.Contains(Everyone), why);
        }

        // ---------------------------------------------------------------- descriptors (SDDL in, verdict out)

        static string Pd(string sddl)
        {
            var sd = new RawSecurityDescriptor(sddl);
            var b = new byte[sd.BinaryLength];
            sd.GetBinaryForm(b, 0);
            return MachineKeyTrust.ProblemOfDescriptor(b);
        }

        static void DescriptorTests()
        {
            Check("descriptor: the app's own SDDL passes", Pd(MachineKeyTrust.Sddl) == null, Pd(MachineKeyTrust.Sddl));
            var own = new RawSecurityDescriptor(MachineKeyTrust.Sddl);
            Check("descriptor: owner Administrators", own.Owner != null && own.Owner.Value == Ba);
            Check("descriptor: protected from SOFTWARE's entries", (own.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
            List<KeyAce> aces = MachineKeyTrust.AcesOf(own);
            Check("descriptor: three entries, all inherited by subkeys", aces != null && aces.Count == 3
                && own.DiscretionaryAcl.Count == 3 && AllContainerInherit(own));
            Check("descriptor: SYSTEM and Administrators full, Users read", aces != null && aces.Count == 3
                && aces[0].Sid == Sy && aces[0].Mask == KeyAll && aces[1].Sid == Ba && aces[1].Mask == KeyAll
                && aces[2].Sid == Users && aces[2].Mask == KeyRead && aces[2].Allow && !aces[2].InheritOnly);

            // A key an older version created under SOFTWARE inherits SOFTWARE's entries; it stays usable.
            Check("descriptor: an inherited SOFTWARE-style DACL passes",
                Pd("O:BAG:SYD:AI(A;CIID;KR;;;BU)(A;CIID;KA;;;BA)(A;CIID;KA;;;SY)(A;CIIOID;GA;;;CO)(A;CIID;KR;;;AC)") == null);
            // Read from a real HKLM\SOFTWARE\RamOptimiser an earlier version created (Windows 11, 2026-11-19).
            const string earlier = "O:BAG:" + SomeUser + "D:AI(A;CIID;KR;;;BU)(A;CIID;KA;;;BA)(A;CIID;KA;;;SY)(A;CIIOID;KA;;;CO)"
                + "(A;CIID;KR;;;AC)(A;CIID;KR;;;S-1-15-3-1024-1065365936-1281604716-3511738428-1654721687-432734479-3232135806-4053264122-3456934681)";
            Check("descriptor: the key an earlier version created passes", Pd(earlier) == null, Pd(earlier));
            Check("descriptor: the same, owned by a user, is refused",
                Pd("O:" + SomeUser + "G:SYD:AI(A;CIID;KR;;;BU)(A;CIID;KA;;;BA)(A;CIID;KA;;;SY)") != null);
            Check("descriptor: Users full control is refused", Pd("O:BAG:SYD:P(A;CI;KA;;;BA)(A;CI;KA;;;BU)") != null);
            Check("descriptor: Users write is refused", Pd("O:BAG:SYD:P(A;CI;KA;;;BA)(A;CI;KW;;;BU)") != null);
            Check("descriptor: Everyone generic all, inherit-only, is refused", Pd("O:BAG:SYD:P(A;CI;KA;;;BA)(A;CIIO;GA;;;WD)") != null);
            Check("descriptor: a user allowed to change the DACL is refused", Pd("O:BAG:SYD:P(A;;KA;;;BA)(A;;WD;;;" + SomeUser + ")") != null);
            Check("descriptor: a deny entry for Everyone is fine", Pd("O:BAG:SYD:P(D;;KA;;;WD)(A;;KA;;;BA)") == null);
            const string callback = "O:BAG:SYD:P(A;;KA;;;BA)(XA;;KA;;;WD;(WIN://x == 1))";
            Exception parse = Catch(() => new RawSecurityDescriptor(callback));
            Check("test setup: a callback entry parses", parse == null, parse == null ? null : parse.Message);
            if (parse == null) Check("descriptor: a callback allow entry counts as allow", Pd(callback) != null);
            Check("descriptor: a NULL DACL is refused", Pd("O:BAG:SYD:NO_ACCESS_CONTROL") != null);
            Check("descriptor: no DACL at all is refused", Pd("O:BAG:SY") != null);
            Check("descriptor: no owner is refused", Pd("D:P(A;;KA;;;BA)") != null);
            Check("descriptor: empty bytes are refused", MachineKeyTrust.ProblemOfDescriptor(new byte[0]) != null
                && MachineKeyTrust.ProblemOfDescriptor(null) != null);
            Check("descriptor: garbage bytes are refused", MachineKeyTrust.ProblemOfDescriptor(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) != null);
        }

        static bool AllContainerInherit(RawSecurityDescriptor sd)
        {
            foreach (GenericAce ace in sd.DiscretionaryAcl)
                if ((ace.AceFlags & AceFlags.ContainerInherit) == 0) return false;
            return true;
        }

        // ---------------------------------------------------------------- AppRegistry.Open over the fake registry

        static void WalkTests()
        {
            var reg = new FakeRegistry();
            string problem;
            using (IMachineKey k = AppRegistry.Open(reg, new[] { "PowerClean", SomeUser }, false, false, out problem))
                Check("walk: missing keys read as missing", k == null && problem == null && reg.Find("RamOptimiser") == null, problem);

            using (IMachineKey k = AppRegistry.Open(reg, new[] { "PowerClean", SomeUser }, true, true, out problem))
                Check("walk: create makes every level", k != null && problem == null && reg.Find("RamOptimiser", "PowerClean", SomeUser) != null, problem);
            FakeNode root = reg.Find("RamOptimiser");
            Check("walk: RamOptimiser is created with the protected descriptor", root != null && root.CreatedWith == MachineKeyTrust.Sddl,
                root == null ? "missing" : root.CreatedWith);
            FakeNode pc = reg.Find("RamOptimiser", "PowerClean"), sidKey = reg.Find("RamOptimiser", "PowerClean", SomeUser);
            Check("walk: the keys below inherit (no descriptor of their own)", pc != null && pc.Created && pc.CreatedWith == null
                && sidKey != null && sidKey.Created && sidKey.CreatedWith == null);
            Check("walk: only the last key is opened writable", reg.WritableOpens == "RamOptimiser\\PowerClean\\" + SomeUser + ";", reg.WritableOpens);
            using (IMachineKey k = AppRegistry.Open(reg, new[] { "PowerClean", SomeUser }, false, false, out problem))
                Check("walk: an existing safe chain opens", k != null && problem == null, problem);

            // Each level is checked: the root, a middle key, the last key.
            foreach (string[] at in new[] { new[] { "RamOptimiser" }, new[] { "RamOptimiser", "PowerClean" }, new[] { "RamOptimiser", "PowerClean", SomeUser } })
            {
                string where = string.Join("\\", at);
                reg = SafeChain();
                reg.Find(at).Sddl = "O:" + SomeUser + "G:SYD:(A;CI;KA;;;BA)";
                Check("walk: a user-owned " + where + " is refused", Refused(reg, out problem) && problem.Contains(SomeUser), problem);
                reg = SafeChain();
                reg.Find(at).Sddl = "O:BAG:SYD:(A;CI;KA;;;BA)(A;CI;KW;;;BU)";
                Check("walk: a Users-writable " + where + " is refused", Refused(reg, out problem), problem);
                reg = SafeChain();
                reg.Find(at).SetValue(AppRegistry.LinkValueName, @"\REGISTRY\MACHINE\SOFTWARE\Elsewhere");
                Check("walk: a link at " + where + " is refused", Refused(reg, out problem) && problem.Contains("link"), problem);
                reg = SafeChain();
                reg.Find(at).KernelOverride = @"\REGISTRY\USER\" + SomeUser + @"\Software\Planted";
                Check("walk: " + where + " resolving elsewhere is refused", Refused(reg, out problem), problem);
                reg = SafeChain();
                reg.Find(at).KernelOverride = null;
                reg.Find(at).NoKernelPath = true;
                Check("walk: " + where + " with an unreadable kernel name is refused", Refused(reg, out problem), problem);
            }

            reg = SafeChain();
            reg.Find("RamOptimiser").Sddl = "O:" + SomeUser + "G:SYD:(A;CI;KA;;;BA)";
            using (IMachineKey k = AppRegistry.Open(reg, new[] { "ServiceStartup" }, true, true, out problem))
                Check("walk: nothing is created under a refused key", k == null && problem != null && reg.Find("RamOptimiser", "ServiceStartup") == null, problem);

            reg = new FakeRegistry { DenyCreate = true };
            Exception thrown = Catch(() => AppRegistry.Open(reg, new[] { "ServiceStartup" }, true, true, out problem));
            Check("walk: access denied on create is UnauthorizedAccessException", thrown is UnauthorizedAccessException,
                thrown == null ? "nothing thrown" : thrown.GetType().Name);
            reg = SafeChain();
            reg.FailOpen = true;
            thrown = Catch(() => { using (AppRegistry.Open(reg, new[] { "ServiceStartup" }, false, false, out problem)) { } });
            Check("walk: a registry error is a problem, not an exception", thrown == null && problem != null, problem);
            Check("walk: every opened key is closed", reg.OpenHandles == 0, reg.OpenHandles.ToString());
            Check("walk: a name with a backslash is refused", Catch(() => AppRegistry.Open(reg, new[] { "a\\b" }, false, false, out problem)) is ArgumentException);
            Check("walk: an empty name is refused", Catch(() => AppRegistry.Open(reg, new[] { "" }, false, false, out problem)) is ArgumentException);
        }

        /// <summary>RamOptimiser\PowerClean\SomeUser, all safe (inherited SOFTWARE-style entries, Administrators owner).</summary>
        static FakeRegistry SafeChain()
        {
            var reg = new FakeRegistry();
            reg.Add("RamOptimiser", "PowerClean", SomeUser);
            return reg;
        }

        static bool Refused(FakeRegistry reg, out string problem)
        {
            using (IMachineKey k = AppRegistry.Open(reg, new[] { "PowerClean", SomeUser }, false, false, out problem))
                return k == null && problem != null && reg.OpenHandles == 0; // a refusal leaves no key open
        }

        static Exception Catch(Action a)
        {
            try { a(); return null; }
            catch (Exception ex) { return ex; }
        }

        // ---------------------------------------------------------------- the Startup tab's ServiceStartup journal

        static void StartupJournalTests()
        {
            string problem;
            var reg = new FakeRegistry();
            Check("startup journal: a missing key reads as empty, with no problem",
                StartupServices.ReadJournal(reg, out problem).Count == 0 && problem == null, problem);

            reg = new FakeRegistry();
            reg.Add("RamOptimiser", "ServiceStartup").SetValue("SysMain", "auto");
            Dictionary<string, string> map = StartupServices.ReadJournal(reg, out problem);
            Check("startup journal: a safe key is read", map.Count == 1 && map["SysMain"] == "auto" && problem == null, problem);

            // A refused key reads as empty, and says why, so Query's caller can log it rather than drop every way back.
            reg = new FakeRegistry();
            reg.Add("RamOptimiser", "ServiceStartup").SetValue("SysMain", "auto");
            reg.Find("RamOptimiser", "ServiceStartup").Sddl = "O:BAG:SYD:(A;CI;KA;;;BA)(A;CI;KW;;;BU)";
            map = StartupServices.ReadJournal(reg, out problem);
            Check("startup journal: a Users-writable key reads as empty and says why",
                map.Count == 0 && problem != null && problem.Contains("ServiceStartup"), problem);

            reg = new FakeRegistry();
            reg.Add("RamOptimiser", "ServiceStartup").SetValue("SysMain", "auto");
            reg.Find("RamOptimiser").SetValue(AppRegistry.LinkValueName, @"\REGISTRY\MACHINE\SOFTWARE\Elsewhere");
            map = StartupServices.ReadJournal(reg, out problem);
            Check("startup journal: a link above it reads as empty and says why",
                map.Count == 0 && problem != null && problem.Contains("link"), problem);

            // Every use of the ServiceStartup key goes through the checked walk: no direct HKLM open of the
            // RamOptimiser path (it would follow links, skip the owner and permission checks and create keys at a
            // link's target), and every JournalKey use outside its declaration is an AppRegistry.Open call.
            string[] lines = File.ReadAllLines(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine("..", Path.Combine("src", "Startup.cs")))));
            var direct = new List<string>();
            var bypass = new List<string>();
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.StartsWith("//")) continue;
                if (t.Contains("RamOptimiser") && (t.Contains("Registry.") || t.Contains("OpenSubKey") || t.Contains("CreateSubKey")))
                    direct.Add(t);
                if (t.Contains("JournalKey") && !t.Contains("AppRegistry.Open(") && !t.StartsWith("static readonly string[] JournalKey"))
                    bypass.Add(t);
            }
            Check("startup journal: no direct HKLM open of the RamOptimiser key", direct.Count == 0, string.Join(" | ", direct.ToArray()));
            Check("startup journal: every JournalKey use is a checked AppRegistry.Open", bypass.Count == 0,
                string.Join(" | ", bypass.ToArray()));
        }

        // ---------------------------------------------------------------- PowerCleanJournal over the fake registry

        static string Names(PowerCleanJournal j)
        {
            List<string> list = j.Read();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", list.ToArray());
        }

        static void JournalTests()
        {
            var reg = new FakeRegistry();
            var j = new PowerCleanJournal(reg, SomeUser);
            Check("journal: key path", j.KeyPath == @"HKLM\SOFTWARE\RamOptimiser\PowerClean\" + SomeUser, j.KeyPath);
            string problem;
            Check("journal: missing reads as empty", j.Read(out problem).Count == 0 && problem == null, problem);
            Check("journal: add records", j.Add("Spooler") && j.Add("SysMain") && j.Add("spooler"));
            FakeNode key = reg.Find("RamOptimiser", "PowerClean", SomeUser);
            Check("journal: one REG_SZ value per name, once each", key != null && key.Values.Count == 2
                && key.Has("Spooler") && key.Has("SysMain") && key.GetValue("Spooler") != null, key == null ? "no key" : key.Dump());
            Check("journal: both names read back", Names(j) == "Spooler,SysMain", Names(j));
            Check("journal: the root key got the protected descriptor", reg.Find("RamOptimiser").CreatedWith == MachineKeyTrust.Sddl);
            Check("journal: a foreign name is not recorded", j.Add("WinDefend") && !key.Has("WinDefend"));
            Check("journal: remove, any case", j.Remove("SPOOLER") && Names(j) == "SysMain" && !key.Has("Spooler"), Names(j));
            Check("journal: removing an unknown name keeps the rest", j.Remove("Fax") && Names(j) == "SysMain");
            Check("journal: removing the last value removes the subkey", j.Remove("SysMain")
                && reg.Find("RamOptimiser", "PowerClean", SomeUser) == null && reg.Find("RamOptimiser", "PowerClean") != null);
            Check("journal: remove with no key is fine", j.Remove("Spooler"));
            Check("journal: empty again", j.Read().Count == 0);

            // Per user: another administrator's journal is a different subkey.
            var other = new PowerCleanJournal(reg, OtherUser);
            j.Add("Spooler");
            Check("journal: per-user SID subkeys", other.Read().Count == 0 && other.Add("Fax") && Names(j) == "Spooler" && Names(other) == "Fax");

            // Defence in depth: only catalog names are taken, and only the first MaxNames values are looked at.
            reg = new FakeRegistry();
            key = reg.Add("RamOptimiser", "PowerClean", SomeUser);
            key.SetValue("WinDefend", "x");
            key.SetValue("wuauserv", "x");
            key.SetValue("Spooler", "x");
            key.SetValue(" SysMain ", "x");
            j = new PowerCleanJournal(reg, SomeUser);
            Check("journal: planted non-catalog names are dropped", Names(j) == "Spooler,SysMain", Names(j));

            reg = new FakeRegistry();
            key = reg.Add("RamOptimiser", "PowerClean", SomeUser);
            for (int i = 0; i < StoppedServiceJournal.MaxNames + 16; i++) key.SetValue("CDPUserSvc_" + (0x1000 + i).ToString("x"), "x");
            j = new PowerCleanJournal(reg, SomeUser);
            Check("journal: capped at MaxNames", j.Read().Count == StoppedServiceJournal.MaxNames, j.Read().Count.ToString());
            Check("journal: no new record past the cap", !j.Add("Spooler") && !key.Has("Spooler"));
            Check("journal: a name already there is still fine past the cap", j.Add("CDPUserSvc_1000"));

            reg = new FakeRegistry();
            key = reg.Add("RamOptimiser", "PowerClean", SomeUser);
            for (int i = 0; i < StoppedServiceJournal.MaxNames; i++) key.SetValue("Junk" + i, "x");
            key.SetValue("Spooler", "x");
            j = new PowerCleanJournal(reg, SomeUser);
            Check("journal: values beyond MaxNames are ignored", j.Read().Count == 0, Names(j));

            // An unsafe key: read as empty with a reason, no record taken, nothing removed.
            foreach (string at in new[] { "RamOptimiser", "PowerClean", SomeUser })
            {
                reg = new FakeRegistry();
                key = reg.Add("RamOptimiser", "PowerClean", SomeUser);
                key.SetValue("Spooler", "x");
                FakeNode bad = at == "RamOptimiser" ? reg.Find("RamOptimiser") : at == "PowerClean" ? reg.Find("RamOptimiser", "PowerClean") : key;
                bad.Sddl = "O:" + SomeUser + "G:SYD:(A;CI;KA;;;BA)(A;CI;KA;;;" + SomeUser + ")";
                j = new PowerCleanJournal(reg, SomeUser);
                List<string> read = j.Read(out problem);
                Check("journal: unsafe " + at + " reads as empty, with a reason", read.Count == 0 && problem != null, problem);
                Check("journal: unsafe " + at + " takes no record", !j.Add("SysMain") && !key.Has("SysMain"));
                Check("journal: unsafe " + at + " removes nothing", !j.Remove("Spooler") && key.Has("Spooler"));
            }
            reg = new FakeRegistry();
            key = reg.Add("RamOptimiser", "PowerClean", SomeUser);
            key.SetValue("Spooler", "x");
            key.SetValue(AppRegistry.LinkValueName, @"\REGISTRY\MACHINE\SOFTWARE\Elsewhere");
            j = new PowerCleanJournal(reg, SomeUser);
            Check("journal: a link key reads as empty", j.Read(out problem).Count == 0 && problem != null, problem);
            Check("journal: a link key takes no record", !j.Add("SysMain"));

            // Not elevated: Windows refuses the create, and the journal says no rather than throwing.
            reg = new FakeRegistry { DenyCreate = true };
            j = new PowerCleanJournal(reg, SomeUser);
            bool added = true;
            Exception denied = Catch(() => added = j.Add("Spooler"));
            Check("journal: access denied is Add false, not an exception", denied == null && !added, denied == null ? null : denied.GetType().Name);
            reg = new FakeRegistry();
            key = reg.Add("RamOptimiser", "PowerClean", SomeUser);
            key.SetValue("Spooler", "x");
            reg.FailOpen = true;
            j = new PowerCleanJournal(reg, SomeUser);
            Check("journal: a registry error reads as empty with a reason", j.Read(out problem).Count == 0 && problem != null);
            Check("journal: a registry error is Remove false", !j.Remove("Spooler"));
            Check("journal: every handle is closed", reg.OpenHandles == 0, reg.OpenHandles.ToString());

            // A refused record is logged with its reason, once per process (the README's "logs why in error.log").
            var lines = new List<string>();
            var once = new OnceLog(lines.Add);
            j = new PowerCleanJournal(new FakeRegistry { DenyCreate = true }, SomeUser, once);
            bool firstAdd = j.Add("Spooler"), secondAdd = j.Add("SysMain");
            var j2 = new PowerCleanJournal(new FakeRegistry { DenyCreate = true }, SomeUser, once);
            bool thirdAdd = j2.Add("Spooler");
            string logged = lines.Count == 0 ? "nothing logged" : string.Join(" | ", lines.ToArray());
            Check("journal: the first refusal is logged, with the service and why", lines.Count >= 1 && !firstAdd
                && lines[0].Contains("Spooler") && lines[0].Contains("Access is denied"), logged);
            Check("journal: later refusals in the process are not logged again", lines.Count == 1 && !secondAdd && !thirdAdd, logged);

            var full = new List<string>();
            reg = new FakeRegistry();
            key = reg.Add("RamOptimiser", "PowerClean", SomeUser);
            for (int i = 0; i < StoppedServiceJournal.MaxNames; i++) key.SetValue("CDPUserSvc_" + (0x2000 + i).ToString("x"), "x");
            j = new PowerCleanJournal(reg, SomeUser, new OnceLog(full.Add));
            Check("journal: a full journal's refusal says it is full", !j.Add("Spooler") && full.Count == 1 && full[0].Contains("full"),
                full.Count == 0 ? "nothing logged" : full[0]);

            var unsafeLines = new List<string>();
            reg = new FakeRegistry();
            key = reg.Add("RamOptimiser", "PowerClean", SomeUser);
            key.Sddl = "O:" + SomeUser + "G:SYD:(A;CI;KA;;;BA)(A;CI;KA;;;" + SomeUser + ")";
            j = new PowerCleanJournal(reg, SomeUser, new OnceLog(unsafeLines.Add));
            Check("journal: a refused key's refusal gives the key's problem", !j.Add("SysMain") && unsafeLines.Count == 1
                && unsafeLines[0].Contains(@"PowerClean\" + SomeUser), unsafeLines.Count == 0 ? "nothing logged" : unsafeLines[0]);

            var none = new List<string>();
            j = new PowerCleanJournal(new FakeRegistry(), SomeUser, new OnceLog(none.Add));
            Check("journal: records that are made log nothing", j.Add("Spooler") && j.Add("WinDefend") && j.Remove("Spooler") && none.Count == 0,
                none.Count == 0 ? null : none[0]);
            Check("journal: the current user's journal logs to the process's error log, a bare one nowhere",
                PowerCleanJournal.ForCurrentUser().RefusalLog == PowerCleanJournal.ProcessRefusals && PowerCleanJournal.ProcessRefusals != null
                && new PowerCleanJournal(new FakeRegistry(), SomeUser).RefusalLog == null);

            Check("journal: a malformed SID is refused", Catch(() => new PowerCleanJournal(new FakeRegistry(), @"S-1-5-21\..\x")) is ArgumentException);
            Check("journal: the SID is used in its canonical form", new PowerCleanJournal(new FakeRegistry(), "s-1-5-18").KeyPath.EndsWith(@"\S-1-5-18", StringComparison.Ordinal));
            PowerCleanJournal mine = PowerCleanJournal.ForCurrentUser();
            string sid;
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) sid = id.User.Value;
            Check("journal: the current user's journal", mine != null && mine.KeyPath.EndsWith(@"\PowerClean\" + sid, StringComparison.Ordinal));
        }

        // ---------------------------------------------------------------- the real registry, read only

        static void RealRegistryTests()
        {
            var real = new MachineRegistry();
            using (IMachineKey software = real.OpenSoftware())
            using (IMachineKey ms = real.OpenChild(software, "Microsoft", false))
            {
                Check("real registry: SOFTWARE\\Microsoft opens", ms != null);
                if (ms != null)
                {
                    Check("real registry: kernel path", string.Equals(ms.KernelPath, @"\REGISTRY\MACHINE\SOFTWARE\Microsoft", StringComparison.OrdinalIgnoreCase), ms.KernelPath);
                    Check("real registry: a plain key is not a link", !Array.Exists(ms.ValueNames(),
                        n => string.Equals(n, AppRegistry.LinkValueName, StringComparison.OrdinalIgnoreCase)));
                    var sd = new RawSecurityDescriptor(ms.ReadSecurity(), 0);
                    Check("real registry: owner and DACL read", sd.Owner != null && MachineKeyTrust.AcesOf(sd) != null);
                }
                Check("real registry: a missing key is null", real.OpenChild(software, "RamOptimiser-test-" + Guid.NewGuid().ToString("N"), false) == null);
            }
            // HKLM\SYSTEM\CurrentControlSet is a registry link Windows makes at boot: opened as itself, it is seen as one.
            using (IMachineKey system = MachineRegistry.OpenMachineKey("SYSTEM"))
            using (IMachineKey ccs = new MachineRegistry().OpenChild(system, "CurrentControlSet", false))
            using (IMachineKey select = new MachineRegistry().OpenChild(system, "Select", false))
            {
                Check("real registry: CurrentControlSet is opened as the link itself", ccs != null
                    && Array.Exists(ccs.ValueNames(), n => string.Equals(n, AppRegistry.LinkValueName, StringComparison.OrdinalIgnoreCase)));
                string why = ccs == null ? null : AppRegistry.KeyProblem(ccs, @"\REGISTRY\MACHINE\SYSTEM\CurrentControlSet");
                Check("real registry: the link is refused", why != null && why.Contains("link"), why);
                Check("real registry: a plain key has no link value", select != null
                    && !Array.Exists(select.ValueNames(), n => string.Equals(n, AppRegistry.LinkValueName, StringComparison.OrdinalIgnoreCase))
                    && select.ValueNames().Length > 0);
            }
        }

        // ---------------------------------------------------------------- in-memory registry

        /// <summary>A key: an SDDL string for its descriptor, ordered values, and children by case-insensitive name.</summary>
        sealed class FakeNode
        {
            public const string Inherited = "O:BAG:SYD:AI(A;CIID;KA;;;SY)(A;CIID;KA;;;BA)(A;CIID;KR;;;BU)(A;CIIOID;GA;;;CO)";
            public string Name, Sddl = Inherited, CreatedWith, KernelOverride;
            public bool Created, NoKernelPath;
            public FakeNode Parent;
            public readonly Dictionary<string, FakeNode> Children = new Dictionary<string, FakeNode>(StringComparer.OrdinalIgnoreCase);
            public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();

            public string Path { get { return Parent == null ? @"\REGISTRY\MACHINE\" + Name : Parent.Path + @"\" + Name; } }

            int Index(string name) { return Values.FindIndex(v => string.Equals(v.Key, name, StringComparison.OrdinalIgnoreCase)); }

            public bool Has(string name) { return Index(name) >= 0; }

            public string GetValue(string name) { int i = Index(name); return i < 0 ? null : Values[i].Value; }

            public void SetValue(string name, string value)
            {
                int i = Index(name);
                if (i >= 0) Values[i] = new KeyValuePair<string, string>(Values[i].Key, value);
                else Values.Add(new KeyValuePair<string, string>(name, value));
            }

            public void DeleteValue(string name) { int i = Index(name); if (i >= 0) Values.RemoveAt(i); }

            public string Dump()
            {
                var parts = new List<string>();
                foreach (var v in Values) parts.Add(v.Key + "=" + v.Value);
                return string.Join(";", parts.ToArray());
            }
        }

        sealed class FakeRegistry : IMachineRegistry
        {
            public readonly FakeNode Software = new FakeNode { Name = "SOFTWARE", Sddl = "O:SYG:SYD:PAI(A;CI;KR;;;BU)(A;CI;KA;;;BA)(A;CI;KA;;;SY)(A;CIIO;GA;;;CO)" };
            public bool DenyCreate, FailOpen;
            public int OpenHandles;
            public string WritableOpens = "";

            public FakeNode Add(params string[] path)
            {
                FakeNode n = Software;
                foreach (string name in path)
                {
                    FakeNode c;
                    if (!n.Children.TryGetValue(name, out c)) { c = new FakeNode { Name = name, Parent = n }; n.Children[name] = c; }
                    n = c;
                }
                return n;
            }

            public FakeNode Find(params string[] path)
            {
                FakeNode n = Software;
                foreach (string name in path) if (!n.Children.TryGetValue(name, out n)) return null;
                return n;
            }

            public IMachineKey OpenSoftware() { return new FakeKey(this, Software, false); }

            public IMachineKey OpenChild(IMachineKey parent, string name, bool writable)
            {
                if (FailOpen) throw new IOException("The device is not ready");
                FakeNode c;
                if (!((FakeKey)parent).Node.Children.TryGetValue(name, out c)) return null;
                if (writable) WritableOpens += c.Path.Substring(@"\REGISTRY\MACHINE\SOFTWARE\".Length) + ";";
                return new FakeKey(this, c, writable);
            }

            public void CreateChild(IMachineKey parent, string name, string sddl)
            {
                if (DenyCreate) throw new UnauthorizedAccessException("Access is denied");
                FakeNode p = ((FakeKey)parent).Node;
                if (p.Children.ContainsKey(name)) return;
                p.Children[name] = new FakeNode { Name = name, Parent = p, Sddl = sddl ?? FakeNode.Inherited, CreatedWith = sddl, Created = true };
            }
        }

        sealed class FakeKey : IMachineKey
        {
            readonly FakeRegistry owner;
            public readonly FakeNode Node;
            readonly bool writable;
            bool disposed;

            public FakeKey(FakeRegistry owner, FakeNode node, bool writable)
            {
                this.owner = owner;
                Node = node;
                this.writable = writable;
                owner.OpenHandles++;
            }

            public string KernelPath { get { return Node.NoKernelPath ? null : Node.KernelOverride ?? Node.Path; } }

            public byte[] ReadSecurity()
            {
                var sd = new RawSecurityDescriptor(Node.Sddl);
                var b = new byte[sd.BinaryLength];
                sd.GetBinaryForm(b, 0);
                return b;
            }

            public string[] ValueNames() { return Node.Values.ConvertAll(v => v.Key).ToArray(); }

            public string GetString(string name) { return Node.GetValue(name); }

            public void SetString(string name, string value) { Writable(); Node.SetValue(name, value); }

            public void DeleteValue(string name) { Writable(); Node.DeleteValue(name); }

            public int SubKeyCount { get { return Node.Children.Count; } }

            public void DeleteKey()
            {
                Writable();
                if (Node.Values.Count > 0 || Node.Children.Count > 0) throw new IOException("Cannot delete a key with values or subkeys");
                Node.Parent.Children.Remove(Node.Name);
            }

            void Writable() { if (!writable) throw new UnauthorizedAccessException("Cannot write to the registry key."); }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.OpenHandles--;
            }
        }
    }
}
