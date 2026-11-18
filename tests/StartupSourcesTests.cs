// Tests for the extra startup sources (E24, engine side): the bounded .lnk reader, the packaged-app manifest
// lookup, RunOnce and packaged rows (read-only), the opt-in listing gate and their wording.
// The .lnk fixtures are built here to the MS-SHLLINK layout; nothing is resolved, run or written.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RamOptimiser.App;

namespace RamOptimiser.Tests
{
    public static class StartupSourcesTests
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
            LnkTests();
            ManifestTests();
            RowTests();
            WordingTests();
            ListingTests();
            CleanNameTests();
            return failed;
        }

        // ------------------------------------------------------------------ names from the registry and shortcuts

        static void CleanNameTests()
        {
            // Format characters outside the basic plane arrive as surrogate pairs; each half alone reads "Surrogate".
            string tag = "a" + char.ConvertFromUtf32(0xE0041) + "b";
            Check("startup name: drops supplementary tag characters", StartupApps.CleanName(tag) == "ab", StartupApps.CleanName(tag));
            Check("startup name: drops unpaired surrogates", StartupApps.CleanName("a" + (char)0xD800 + "b") == "ab"
                && StartupApps.CleanName("a" + (char)0xDC00 + "b") == "ab");
            string smile = "a" + char.ConvertFromUtf32(0x1F600);
            Check("startup name: keeps a real supplementary character", StartupApps.CleanName(smile) == smile);
            Check("startup name: controls still become one space", StartupApps.CleanName("a" + (char)9 + (char)10 + "b") == "a b");
        }

        // ------------------------------------------------------------------ .lnk fixtures (MS-SHLLINK 2.1 to 2.5)

        static readonly byte[] ShellLinkClsid = { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 };

        const uint HasLinkTargetIDList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasArguments = 0x20, IsUnicode = 0x80;

        static byte[] Header(byte[] clsid, uint flags)
        {
            var b = new byte[76];
            BitConverter.GetBytes(0x4Cu).CopyTo(b, 0);
            clsid.CopyTo(b, 4);
            BitConverter.GetBytes(flags).CopyTo(b, 20);
            return b;
        }

        static byte[] Ansi(string s) { return Encoding.ASCII.GetBytes(s + "\0"); }
        static byte[] Wide(string s) { return Encoding.Unicode.GetBytes(s + "\0"); }

        /// <summary>A LinkInfo with a VolumeID and a local base path; the Unicode offsets only when uniBase is given.</summary>
        static byte[] LinkInfo(string ansiBase, string ansiSuffix, string uniBase, string uniSuffix)
        {
            bool uni = uniBase != null;
            int headerSize = uni ? 0x24 : 0x1C;
            byte[] volume = new byte[17];
            BitConverter.GetBytes(17u).CopyTo(volume, 0);
            BitConverter.GetBytes(3u).CopyTo(volume, 4);
            BitConverter.GetBytes(0x10u).CopyTo(volume, 12);
            byte[] baseA = Ansi(ansiBase), suffixA = Ansi(ansiSuffix);
            byte[] baseU = uni ? Wide(uniBase) : new byte[0], suffixU = uni ? Wide(uniSuffix ?? "") : new byte[0];
            int volumeOff = headerSize, baseOff = volumeOff + volume.Length, suffixOff = baseOff + baseA.Length;
            int uniBaseOff = suffixOff + suffixA.Length, uniSuffixOff = uniBaseOff + baseU.Length;
            int total = uniSuffixOff + suffixU.Length;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((uint)total); w.Write((uint)headerSize); w.Write(1u);
            w.Write((uint)volumeOff); w.Write((uint)baseOff); w.Write(0u); w.Write((uint)suffixOff);
            if (uni) { w.Write((uint)uniBaseOff); w.Write((uint)uniSuffixOff); }
            w.Write(volume); w.Write(baseA); w.Write(suffixA); w.Write(baseU); w.Write(suffixU);
            w.Flush();
            return ms.ToArray();
        }

        static byte[] EnvBlock(string target)
        {
            var b = new byte[0x314];
            BitConverter.GetBytes(0x314u).CopyTo(b, 0);
            BitConverter.GetBytes(0xA0000001u).CopyTo(b, 4);
            Encoding.ASCII.GetBytes(target).CopyTo(b, 8);
            Encoding.Unicode.GetBytes(target).CopyTo(b, 8 + 260);
            return b;
        }

        /// <summary>Header, optional IDList and LinkInfo, the counted Unicode strings (name, args), extra data, terminal.</summary>
        static byte[] Lnk(byte[] idList, byte[] linkInfo, string name, string args, byte[] extra)
        {
            uint flags = IsUnicode;
            if (idList != null) flags |= HasLinkTargetIDList;
            if (linkInfo != null) flags |= HasLinkInfo;
            if (name != null) flags |= HasName;
            if (args != null) flags |= HasArguments;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Header(ShellLinkClsid, flags));
            if (idList != null) { w.Write((ushort)idList.Length); w.Write(idList); }
            if (linkInfo != null) w.Write(linkInfo);
            foreach (string s in new[] { name, args })
                if (s != null) { w.Write((ushort)s.Length); w.Write(Encoding.Unicode.GetBytes(s)); }
            if (extra != null) w.Write(extra);
            w.Write(0u); // TerminalBlock
            w.Flush();
            return ms.ToArray();
        }

        static void LnkTests()
        {
            string path, args;
            byte[] plain = Lnk(null, LinkInfo(@"C:\Apps\x.exe", "", null, null), null, null, null);
            Check("lnk: reads the local base path", LnkReader.TryReadTarget(plain, out path, out args) && path == @"C:\Apps\x.exe", path);

            byte[] split = Lnk(null, LinkInfo(@"C:\Apps\", "y.exe", null, null), null, null, null);
            Check("lnk: joins the common path suffix", LnkReader.TryReadTarget(split, out path, out args) && path == @"C:\Apps\y.exe", path);

            byte[] uni = Lnk(null, LinkInfo(@"C:\Apps\ansi.exe", "", @"C:\Apps\uni.exe", ""), null, null, null);
            Check("lnk: prefers the unicode base path", LnkReader.TryReadTarget(uni, out path, out args) && path == @"C:\Apps\uni.exe", path);

            byte[] env = Lnk(new byte[] { 0, 0 }, null, null, null, EnvBlock(@"%SystemRoot%\x.exe"));
            string expected = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\x.exe");
            Check("lnk: uses the environment block when there is no link info",
                LnkReader.TryReadTarget(env, out path, out args) && path == expected, path);

            byte[] both = Lnk(null, LinkInfo(@"C:\Apps\x.exe", "", null, null), null, null, EnvBlock(@"C:\Other\z.exe"));
            Check("lnk: without HasExpString, link info wins over the environment block", LnkReader.TryReadTarget(both, out path, out args)
                && path == @"C:\Apps\x.exe", path);
            // MS-SHLLINK 2.1.1: HasExpString (0x200) says the target is the environment block; LinkInfo is only a hint.
            byte[] expString = (byte[])both.Clone();
            expString[21] |= 0x02;
            Check("lnk: HasExpString prefers the environment block", LnkReader.TryReadTarget(expString, out path, out args)
                && path == @"C:\Other\z.exe", path);
            // ForceNoLinkInfo (0x100): the LinkInfo is ignored.
            byte[] forceNo = (byte[])plain.Clone();
            forceNo[21] |= 0x01;
            Check("lnk: ForceNoLinkInfo ignores the link info", !LnkReader.TryReadTarget(forceNo, out path, out args) && path == null, path);
            byte[] forceNoEnv = (byte[])both.Clone();
            forceNoEnv[21] |= 0x01;
            Check("lnk: ForceNoLinkInfo falls back to the environment block", LnkReader.TryReadTarget(forceNoEnv, out path, out args)
                && path == @"C:\Other\z.exe", path);

            byte[] withArgs = Lnk(null, LinkInfo(@"C:\Apps\x.exe", "", null, null), "Contoso", "--tray --min", null);
            Check("lnk: reads arguments", LnkReader.TryReadTarget(withArgs, out path, out args)
                && path == @"C:\Apps\x.exe" && args == "--tray --min", path + " | " + args);
            Check("lnk: no arguments reads as empty", LnkReader.TryReadTarget(plain, out path, out args) && args == "", args);

            byte[] noTarget = Lnk(new byte[] { 0, 0 }, null, "Settings", null, null);
            Check("lnk: an id list alone gives no path", !LnkReader.TryReadTarget(noTarget, out path, out args) && path == null);

            byte[] wrongClsid = (byte[])plain.Clone();
            wrongClsid[4] ^= 0xFF;
            Check("lnk: wrong CLSID returns false", !LnkReader.TryReadTarget(wrongClsid, out path, out args));
            byte[] wrongSize = (byte[])plain.Clone();
            wrongSize[0] = 0x4D;
            Check("lnk: wrong header size returns false", !LnkReader.TryReadTarget(wrongSize, out path, out args));

            // LocalBasePathOffset sits at LinkInfo + 16; point it far past the end of the structure.
            byte[] pastEnd = (byte[])plain.Clone();
            BitConverter.GetBytes(0x7FFFu).CopyTo(pastEnd, 76 + 16);
            Check("lnk: offset past the end returns false", !LnkReader.TryReadTarget(pastEnd, out path, out args));
            byte[] noNul = Lnk(null, LinkInfo(@"C:\Apps\x.exe", "", null, null), null, null, null);
            int infoSize = BitConverter.ToInt32(noNul, 76);
            for (int i = 76 + 0x1C + 17; i < 76 + infoSize; i++) if (noNul[i] == 0) noNul[i] = (byte)'a';
            Check("lnk: a string without its terminator returns false", !LnkReader.TryReadTarget(noNul, out path, out args));
            byte[] tinyInfo = (byte[])plain.Clone();
            BitConverter.GetBytes(8u).CopyTo(tinyInfo, 76);
            Check("lnk: link info smaller than its header returns false", !LnkReader.TryReadTarget(tinyInfo, out path, out args));

            // A count of 32768 characters with enough bytes behind it: only the cap can refuse it.
            var big = new MemoryStream();
            var w = new BinaryWriter(big);
            w.Write(Header(ShellLinkClsid, HasArguments | IsUnicode));
            w.Write((ushort)0x8000);
            w.Write(new byte[0x8000 * 2]);
            w.Write(0u);
            w.Flush();
            Check("lnk: oversized string count returns false", !LnkReader.TryReadTarget(big.ToArray(), out path, out args));

            byte[] badBlock = Lnk(null, LinkInfo(@"C:\Apps\x.exe", "", null, null), null, null, new byte[] { 6, 0, 0, 0, 1, 0, 0, 0 });
            Check("lnk: an extra block smaller than its header returns false", !LnkReader.TryReadTarget(badBlock, out path, out args));

            Check("lnk: null and empty return false", !LnkReader.TryReadTarget(null, out path, out args)
                && !LnkReader.TryReadTarget(new byte[0], out path, out args));

            byte[] full = Lnk(new byte[] { 4, 0, 0x1F, 0x50, 0, 0 }, LinkInfo(@"C:\Apps\ansi.exe", "", @"C:\Apps\full.exe", ""),
                "Contoso", "--tray", EnvBlock(@"C:\Other\z.exe"));
            Check("lnk: a full file reads", LnkReader.TryReadTarget(full, out path, out args) && path == @"C:\Apps\full.exe" && args == "--tray", path);
            int accepted = 0, threw = 0;
            for (int len = 0; len < full.Length; len++)
            {
                byte[] cut = new byte[len];
                Array.Copy(full, cut, len);
                try { if (LnkReader.TryReadTarget(cut, out path, out args)) accepted++; }
                catch (Exception) { threw++; }
            }
            Check("lnk: every truncation of a valid file returns false without throwing", accepted == 0 && threw == 0,
                accepted + " accepted, " + threw + " threw");
        }

        // ------------------------------------------------------------------ AppxManifest.xml

        const string DesktopManifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\""
            + " xmlns:desktop=\"http://schemas.microsoft.com/appx/manifest/desktop/windows10\">"
            + "<Applications><Application Id=\"App\" Executable=\"app\\Main.exe\"><Extensions>"
            + "<desktop:Extension Category=\"windows.startupTask\" Executable=\"app\\Tray.exe\" EntryPoint=\"Windows.FullTrustApplication\">"
            + "<desktop:StartupTask TaskId=\"TrayStartup\" Enabled=\"true\" DisplayName=\"Contoso tray\" />"
            + "</desktop:Extension>"
            + "<desktop:Extension Category=\"windows.startupTask\" Executable=\"app\\Sync.exe\" EntryPoint=\"Windows.FullTrustApplication\">"
            + "<desktop:StartupTask TaskId=\"SyncStartup\" Enabled=\"false\" DisplayName=\"ms-resource:SyncName\" />"
            + "</desktop:Extension>"
            + "</Extensions></Application></Applications></Package>";

        const string Uap5Manifest =
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\""
            + " xmlns:uap5=\"http://schemas.microsoft.com/appx/manifest/uap/windows10/5\">"
            + "<Applications><Application Id=\"App\" Executable=\"Main.exe\"><Extensions>"
            + "<uap5:Extension Category=\"windows.startupTask\" Executable=\"Bin\\Helper.exe\" EntryPoint=\"Helper.App\">"
            + "<uap5:StartupTask TaskId=\"HelperTask\" Enabled=\"true\" DisplayName=\"Helper\" />"
            + "</uap5:Extension></Extensions></Application></Applications></Package>";

        static void ManifestTests()
        {
            string exe, name;
            Check("manifest: finds the desktop startup task executable",
                PackagedStartup.FindStartupTask(DesktopManifest, "TrayStartup", out exe, out name) && exe == @"app\Tray.exe" && name == "Contoso tray",
                exe + " | " + name);
            Check("manifest: finds the uap5 startup task",
                PackagedStartup.FindStartupTask(Uap5Manifest, "HelperTask", out exe, out name) && exe == @"Bin\Helper.exe" && name == "Helper",
                exe + " | " + name);
            Check("manifest: another TaskId is ignored",
                PackagedStartup.FindStartupTask(DesktopManifest, "SyncStartup", out exe, out name) && exe == @"app\Sync.exe"
                && !PackagedStartup.FindStartupTask(DesktopManifest, "Missing", out exe, out name) && exe == null, exe);
            Check("manifest: task id matches without regard to case",
                PackagedStartup.FindStartupTask(DesktopManifest, "traystartup", out exe, out name) && exe == @"app\Tray.exe");

            string dtd = "<?xml version=\"1.0\"?><!DOCTYPE Package [<!ENTITY e \"C:\\evil.exe\">]>"
                + "<Package><Extension Executable=\"&e;\"><StartupTask TaskId=\"T\" DisplayName=\"x\" /></Extension></Package>";
            bool threw = false, found = true;
            try { found = PackagedStartup.FindStartupTask(dtd, "T", out exe, out name); }
            catch (Exception) { threw = true; exe = "threw"; }
            Check("manifest: a DTD is refused", !found && !threw && exe == null, exe);
            Check("manifest: broken XML returns false", !PackagedStartup.FindStartupTask("<Package><Extension", "T", out exe, out name));
            Check("manifest: an extension without an executable returns false", !PackagedStartup.FindStartupTask(
                "<Package><Extension><StartupTask TaskId=\"T\" /></Extension></Package>", "T", out exe, out name));
            Check("manifest: null input returns false", !PackagedStartup.FindStartupTask(null, "T", out exe, out name)
                && !PackagedStartup.FindStartupTask(Uap5Manifest, null, out exe, out name));

            const string folder = @"C:\Program Files\WindowsApps\Contoso.Tray_1.0.0.0_x64__abc";
            Check("manifest: an executable outside the package is rejected",
                PackagedStartup.PackagedExePath(folder, @"..\..\evil.exe") == null
                && PackagedStartup.PackagedExePath(folder, @"C:\Windows\evil.exe") == null
                && PackagedStartup.PackagedExePath(folder, "a|b.exe") == null);
            Check("manifest: an executable inside the package is kept",
                PackagedStartup.PackagedExePath(folder, @"app\Tray.exe") == folder + @"\app\Tray.exe");

            Check("packaged: display name kept", PackagedStartup.DisplayNameFor("Contoso tray", "Contoso.Tray_abc") == "Contoso tray");
            Check("packaged: ms-resource name falls back to the family name",
                PackagedStartup.DisplayNameFor("ms-resource:Name", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm") == "WhatsAppDesktop");
            Check("packaged: family without a dot", PackagedStartup.DisplayNameFor("", "Claude_pzs8sxrjxfjjc") == "Claude");
            Check("packaged: family without an underscore", PackagedStartup.DisplayNameFor(null, "Contoso.App") == "App");
        }

        // ------------------------------------------------------------------ rows

        static void RowTests()
        {
            Check("runonce: ! and * prefixes stripped for display", StartupApps.RunOnceDisplayName("!Setup") == "Setup"
                && StartupApps.RunOnceDisplayName("*Fix") == "Fix" && StartupApps.RunOnceDisplayName("*!Both") == "Both"
                && StartupApps.RunOnceDisplayName("Plain") == "Plain" && StartupApps.RunOnceDisplayName("!") == "!");

            var userOnce = new StartupApp { Source = StartupSource.UserRunOnce, Name = "Setup" };
            var machineOnce = new StartupApp { Source = StartupSource.MachineRunOnce, Name = "Setup" };
            var machineOnce32 = new StartupApp { Source = StartupSource.MachineRunOnce32, Name = "Setup" };
            var packaged = new StartupApp { Source = StartupSource.Packaged, Name = "Contoso", PackageFamily = "Contoso.App_abc" };
            Check("runonce rows cannot be toggled", !userOnce.CanToggle && !machineOnce.CanToggle && !machineOnce32.CanToggle);
            Check("packaged rows cannot be toggled", !packaged.CanToggle);
            bool classic = true;
            foreach (StartupSource s in new[] { StartupSource.UserRun, StartupSource.MachineRun, StartupSource.MachineRun32,
                StartupSource.UserFolder, StartupSource.CommonFolder })
                classic &= new StartupApp { Source = s }.CanToggle;
            Check("run and folder rows can still be toggled", classic);
            Check("runonce: machine rows need admin like Run", machineOnce.NeedsAdmin && machineOnce32.NeedsAdmin && !userOnce.NeedsAdmin);
            Check("packaged: rows are per user", !packaged.NeedsAdmin);
            Check("run and folder rows keep their admin rule", !new StartupApp { Source = StartupSource.UserRun }.NeedsAdmin
                && !new StartupApp { Source = StartupSource.UserFolder }.NeedsAdmin
                && new StartupApp { Source = StartupSource.MachineRun }.NeedsAdmin
                && new StartupApp { Source = StartupSource.CommonFolder }.NeedsAdmin);

            Check("packaged: states 2 and 4 are on", !PackagedStartup.IsOn(PackagedState.Disabled) && !PackagedStartup.IsOn(PackagedState.DisabledByUser)
                && PackagedStartup.IsOn(PackagedState.Enabled) && !PackagedStartup.IsOn(PackagedState.DisabledByPolicy)
                && PackagedStartup.IsOn(PackagedState.EnabledByPolicy));
            Check("packaged: states 3 and 4 are set by policy", !PackagedStartup.IsPolicy(PackagedState.Disabled)
                && !PackagedStartup.IsPolicy(PackagedState.DisabledByUser) && !PackagedStartup.IsPolicy(PackagedState.Enabled)
                && PackagedStartup.IsPolicy(PackagedState.DisabledByPolicy) && PackagedStartup.IsPolicy(PackagedState.EnabledByPolicy));
            Check("packaged: enum mirrors StartupTaskState", (int)PackagedState.Disabled == 0 && (int)PackagedState.DisabledByUser == 1
                && (int)PackagedState.Enabled == 2 && (int)PackagedState.DisabledByPolicy == 3 && (int)PackagedState.EnabledByPolicy == 4);
            PackagedState st;
            Check("packaged: a DWORD 0 to 4 is a state", PackagedStartup.TryParseState(2, out st) && st == PackagedState.Enabled
                && PackagedStartup.TryParseState(0, out st) && st == PackagedState.Disabled);
            Check("packaged: anything else is not a state", !PackagedStartup.TryParseState(5, out st) && !PackagedStartup.TryParseState(-1, out st)
                && !PackagedStartup.TryParseState("2", out st) && !PackagedStartup.TryParseState(null, out st)
                && !PackagedStartup.TryParseState(new byte[] { 2 }, out st));

            bool threw = false;
            try { StartupApps.EnsureCanToggle(userOnce); }
            catch (InvalidOperationException ex) { threw = ex.Message == "this entry can't be changed here"; }
            bool threwPackaged = false;
            try { StartupApps.EnsureCanToggle(packaged); }
            catch (InvalidOperationException) { threwPackaged = true; }
            bool runOk = true;
            try { StartupApps.EnsureCanToggle(new StartupApp { Source = StartupSource.UserRun, Name = "X" }); }
            catch (InvalidOperationException) { runOk = false; }
            Check("set enabled: a non-toggleable row throws", threw && threwPackaged && runOk);
            // SetEnabled itself refuses before opening any key, so these calls write nothing.
            bool setOnce = false, setPackaged = false;
            try { StartupApps.SetEnabled(userOnce, false); }
            catch (InvalidOperationException ex) { setOnce = ex.Message == "this entry can't be changed here"; }
            try { StartupApps.SetEnabled(packaged, true); }
            catch (InvalidOperationException ex) { setPackaged = ex.Message == "this entry can't be changed here"; }
            Check("set enabled: SetEnabled refuses run-once and packaged rows", setOnce && setPackaged);
            bool keyless = true;
            foreach (StartupSource s in new[] { StartupSource.UserRunOnce, StartupSource.MachineRunOnce, StartupSource.MachineRunOnce32,
                StartupSource.Packaged })
            {
                Microsoft.Win32.RegistryKey hive; string sub;
                try { StartupApps.ApprovedKey(new StartupApp { Source = s, Name = "X" }, out hive, out sub); keyless = false; }
                catch (InvalidOperationException) { }
            }
            Check("set enabled: rows without a StartupApproved value map to no key", keyless);

            // A shortcut target must be a local drive path: a share would make the elevated app open it to read
            // the publisher, connecting with the user's credentials.
            foreach (string remote in new[] { @"\\host\share\a.exe", @"\\?\UNC\host\share\a.exe", @"\\?\C:\Apps\a.exe", @"a.exe", @"C:a.exe" })
            {
                var r = new StartupApp { Source = StartupSource.UserFolder, Name = "Remote.lnk" };
                StartupApps.ApplyShortcutTarget(r, remote, "");
                Check("shortcut: a non-local target keeps the guess: " + remote, r.ExePath == null && r.ExeName == "Remote.exe", r.ExePath);
            }
            Check("local drive path rule", StartupApps.IsLocalDrivePath(@"C:\Apps\a.exe") && StartupApps.IsLocalDrivePath(@"d:\a.exe")
                && !StartupApps.IsLocalDrivePath(@"\\host\s\a.exe") && !StartupApps.IsLocalDrivePath(null) && !StartupApps.IsLocalDrivePath("C:")
                && !StartupApps.IsLocalDrivePath(@"1:\a.exe"));

            // A shortcut in the user's own Startup folder never earns a Keep lock: its LinkInfo path is a hint
            // anyone can write, not what runs. The same exe from an admin-only source still does.
            string[] roots = { @"C:\Windows" };
            string spoofed = @"C:\Windows\System32\SecurityHealthSystray.exe";
            var userLnk = new StartupApp { Source = StartupSource.UserFolder, Name = "Security.lnk" };
            StartupApps.ApplyShortcutTarget(userLnk, spoofed, "");
            KnownItem fromLnk = StartupApps.KnownFor(userLnk, roots);
            Check("shortcut: a user Startup-folder shortcut is never Keep", userLnk.ExeName == "SecurityHealthSystray.exe"
                && (fromLnk == null || fromLnk.Verdict != Verdict.Keep), fromLnk == null ? "null" : fromLnk.Verdict.ToString());
            KnownItem fromRun = StartupApps.KnownFor(new StartupApp { Source = StartupSource.MachineRun, Name = "SecurityHealth",
                ExePath = spoofed, ExeName = "SecurityHealthSystray.exe" }, roots);
            Check("shortcut: the same exe from Run under a protected root is Keep", fromRun != null && fromRun.Verdict == Verdict.Keep);

            var lnk = new StartupApp { Source = StartupSource.UserFolder, Name = "Contoso.lnk" };
            StartupApps.ApplyShortcutTarget(lnk, @"C:\Apps\Contoso\tray.exe", "--min");
            Check("shortcut: the real exe is used", lnk.ExePath == @"C:\Apps\Contoso\tray.exe" && lnk.ExeName == "tray.exe", lnk.ExeName);
            var squirrel = new StartupApp { Source = StartupSource.UserFolder, Name = "Discord.lnk" };
            StartupApps.ApplyShortcutTarget(squirrel, @"C:\Users\x\AppData\Local\Discord\Update.exe", "--processStart Discord.exe");
            Check("shortcut: a launcher's app is matched", squirrel.ExeName == "Discord.exe"
                && squirrel.ExePath == @"C:\Users\x\AppData\Local\Discord\Update.exe", squirrel.ExeName);
            var doc = new StartupApp { Source = StartupSource.UserFolder, Name = "Notes.lnk" };
            StartupApps.ApplyShortcutTarget(doc, @"C:\Docs\notes.txt", "");
            Check("shortcut: a non-exe target keeps the guess", doc.ExePath == null && doc.ExeName == "Notes.exe", doc.ExeName);
            var unread = new StartupApp { Source = StartupSource.UserFolder, Name = "Odd.lnk" };
            StartupApps.ApplyShortcutTarget(unread, null, null);
            Check("shortcut: no target keeps the guess", unread.ExePath == null && unread.ExeName == "Odd.exe", unread.ExeName);
        }

        // ------------------------------------------------------------------ wording

        static void WordingTests()
        {
            var packaged = new StartupApp { Source = StartupSource.Packaged, Name = "Contoso", Packaged = PackagedState.Enabled, Enabled = true };
            var policy = new StartupApp { Source = StartupSource.Packaged, Name = "Contoso", Packaged = PackagedState.EnabledByPolicy,
                PolicySet = true, Enabled = true };
            var once = new StartupApp { Source = StartupSource.UserRunOnce, Name = "Setup", Enabled = true };
            var run = new StartupApp { Source = StartupSource.UserRun, Name = "Discord", Enabled = true };

            Check("wording: packaged detail", AppText.StartupSourceDetail(packaged) + "." == "Store app. Turn it on or off in Settings, Apps, Startup.",
                AppText.StartupSourceDetail(packaged));
            Check("wording: policy detail", AppText.StartupSourceDetail(policy) + "." == "Your organisation controls whether it starts.",
                AppText.StartupSourceDetail(policy));
            Check("wording: run-once detail", AppText.StartupSourceDetail(once) + "." == "Runs once at the next sign-in, then Windows removes it.",
                AppText.StartupSourceDetail(once));
            Check("wording: other rows add nothing", AppText.StartupSourceDetail(run) == null);
            Check("wording: lock texts", AppText.StartupSourceLock(packaged) == "In Settings" && AppText.StartupSourceLock(policy) == "Set by policy"
                && AppText.StartupSourceLock(once) == "Runs once" && AppText.StartupSourceLock(run) == null);
            Check("wording: settings link", AppText.OpenStartupSettings == "Open Startup settings"
                && AppText.StartupSettingsUri == "ms-settings:startupapps");
            Check("wording: no exclamation marks", (AppText.StartupSourceDetail(packaged) + AppText.StartupSourceDetail(policy)
                + AppText.StartupSourceDetail(once)).IndexOf('!') < 0);

            Check("summary: run-once rows are not counted", !AppText.CountsAsAppOn(once));
            Check("summary: enabled packaged rows count", AppText.CountsAsAppOn(packaged)
                && !AppText.CountsAsAppOn(new StartupApp { Source = StartupSource.Packaged, Packaged = PackagedState.DisabledByUser }));
            Check("summary: enabled run rows count", AppText.CountsAsAppOn(run)
                && !AppText.CountsAsAppOn(new StartupApp { Source = StartupSource.UserRun, Enabled = false }));
        }

        // ------------------------------------------------------------------ listing (read-only: registry, Startup folders)

        static void ListingTests()
        {
            List<StartupApp> plain = StartupApps.Query();
            bool onlyClassic = true;
            foreach (StartupApp a in plain) onlyClassic &= a.CanToggle;
            Check("gate: the default listing has no new row kinds", onlyClassic);
            // The same gate without depending on what this PC has installed.
            List<StartupSource> plainSources = StartupApps.IncludedSources(false), allSources = StartupApps.IncludedSources(true);
            StartupSource[] extras = { StartupSource.UserRunOnce, StartupSource.MachineRunOnce, StartupSource.MachineRunOnce32, StartupSource.Packaged };
            bool gateOk = plainSources.Count == 5;
            foreach (StartupSource s in extras) gateOk &= !plainSources.Contains(s) && allSources.Contains(s);
            foreach (StartupSource s in plainSources) gateOk &= allSources.Contains(s);
            Check("gate: the default sources leave out run-once and packaged", gateOk,
                string.Join(",", plainSources.ConvertAll(s => s.ToString()).ToArray()));

            // Manifests are decoded strictly: UTF-8 (with or without a BOM) or UTF-16 with a BOM; anything else is refused.
            string xml = "<Package><x a=\"" + (char)0xE9 + "\"/></Package>";
            byte[] utf8 = new UTF8Encoding(false).GetBytes(xml);
            byte[] utf8Bom = new UTF8Encoding(true).GetPreamble();
            byte[] withBom = new byte[utf8Bom.Length + utf8.Length];
            utf8Bom.CopyTo(withBom, 0); utf8.CopyTo(withBom, utf8Bom.Length);
            byte[] utf16 = new byte[2 + Encoding.Unicode.GetByteCount(xml)];
            utf16[0] = 0xFF; utf16[1] = 0xFE;
            Encoding.Unicode.GetBytes(xml).CopyTo(utf16, 2);
            byte[] latin1 = Encoding.GetEncoding(28591).GetBytes(xml);
            Check("manifest: UTF-8 decodes", PackagedStartup.DecodeManifest(utf8) == xml);
            Check("manifest: UTF-8 with a BOM decodes", PackagedStartup.DecodeManifest(withBom) == xml);
            Check("manifest: UTF-16 with a BOM decodes", PackagedStartup.DecodeManifest(utf16) == xml);
            Check("manifest: bytes that are not UTF-8 are refused", PackagedStartup.DecodeManifest(latin1) == null);
            Check("manifest: null is refused", PackagedStartup.DecodeManifest(null) == null);

            List<StartupApp> all = null; string error = null;
            try { all = StartupApps.Query(true); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            Check("listing: the full listing runs", all != null, error);
            if (all == null) return;
            Check("listing: the full listing keeps every default row", all.Count >= plain.Count, all.Count + " vs " + plain.Count);
            bool sane = true; string bad = null;
            foreach (StartupApp a in all)
            {
                bool extra = a.Source == StartupSource.Packaged || a.Source == StartupSource.UserRunOnce
                    || a.Source == StartupSource.MachineRunOnce || a.Source == StartupSource.MachineRunOnce32;
                if (extra && a.CanToggle) { sane = false; bad = a.Name; }
                if (a.Source == StartupSource.Packaged && (string.IsNullOrEmpty(a.PackageFamily) || string.IsNullOrEmpty(a.Name)))
                { sane = false; bad = a.Name; }
                if (a.Source == StartupSource.Packaged && a.Enabled != PackagedStartup.IsOn(a.Packaged)) { sane = false; bad = a.Name; }
            }
            Check("listing: extra rows are read-only and complete", sane, bad);
        }
    }
}
