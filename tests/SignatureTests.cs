// Tests for the Authenticode publisher check (E26, engine side): the status table, the company-name match, the
// detail line, the file checks (missing, unsigned copy, tampered signed copy) and that a signature never changes a
// Keep verdict. The environment tests copy files into a fresh temp folder; nothing is run, installed or trusted.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RamOptimiser.App;

namespace RamOptimiser.Tests
{
    public static class SignatureTests
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
            ClassifyTests();
            SameCompanyTests();
            PublisherLineTests();
            RowTests();
            FileTests();
            return failed;
        }

        // ------------------------------------------------------------------ status table (WinVerifyTrust LONG + GetLastError)

        const int NoSignature = unchecked((int)0x800B0100), SubjectFormUnknown = unchecked((int)0x800B0003),
            ProviderUnknown = unchecked((int)0x800B0001), ExplicitDistrust = unchecked((int)0x800B0111),
            SubjectNotTrusted = unchecked((int)0x800B0004), SecuritySettings = unchecked((int)0x80092026),
            BadDigest = unchecked((int)0x80096010), UntrustedRoot = unchecked((int)0x800B0109);

        static void ClassifyTests()
        {
            Check("classify: 0 is signed", Signature.Classify(0, 0) == SignatureState.Signed);
            Check("classify: no signature, last error no signature, is unsigned",
                Signature.Classify(NoSignature, NoSignature) == SignatureState.Unsigned);
            Check("classify: no signature, last error subject form unknown, is unsigned",
                Signature.Classify(NoSignature, SubjectFormUnknown) == SignatureState.Unsigned);
            Check("classify: no signature, last error provider unknown, is unsigned",
                Signature.Classify(NoSignature, ProviderUnknown) == SignatureState.Unsigned);
            Check("classify: no signature with any other last error is invalid",
                Signature.Classify(NoSignature, 5) == SignatureState.Invalid
                && Signature.Classify(NoSignature, 0) == SignatureState.Invalid);
            Check("classify: explicit distrust is distrusted", Signature.Classify(ExplicitDistrust, 0) == SignatureState.Distrusted);
            Check("classify: subject not trusted is invalid", Signature.Classify(SubjectNotTrusted, 0) == SignatureState.Invalid);
            // The doc: "not explicitly trusted by the admin and admin policy has disabled user trust. No signature,
            // publisher or timestamp errors." A policy block, so it must not read as a broken signature.
            Check("classify: security settings is a policy block, not a broken signature",
                Signature.Classify(SecuritySettings, 0) == SignatureState.Distrusted);
            Check("classify: a bad digest is invalid", Signature.Classify(BadDigest, 0) == SignatureState.Invalid);
            Check("classify: an untrusted root is invalid", Signature.Classify(UntrustedRoot, 0) == SignatureState.Invalid);
        }

        // ------------------------------------------------------------------ company names

        static void SameCompanyTests()
        {
            Check("same company: a name matches itself", Signature.SameCompany("Microsoft Corporation", "Microsoft Corporation"));
            Check("same company: suffix words and punctuation are ignored", Signature.SameCompany("Google LLC", "Google Inc."));
            Check("same company: case is ignored", Signature.SameCompany("CONTOSO, LTD.", "contoso ltd"));
            Check("same company: one name starting the other matches", Signature.SameCompany("Microsoft", "Microsoft Windows"));
            Check("same company: different companies differ", !Signature.SameCompany("Microsoft Corporation", "Contoso Ltd"));
            Check("same company: empty is never the same", !Signature.SameCompany("", "Contoso")
                && !Signature.SameCompany("Contoso", null) && !Signature.SameCompany(null, null));
            Check("same company: a name of only suffix words is never the same", !Signature.SameCompany("Inc.", "Contoso Inc."));
            Check("same company: a prefix must end on a word", !Signature.SameCompany("Micro", "Microsoft"));
        }

        // ------------------------------------------------------------------ the detail line

        static void PublisherLineTests()
        {
            const string ms = "Microsoft Corporation";
            Check("line: signed, no claim", AppText.PublisherLine(SignatureState.Signed, "Contoso Ltd", null, false) == "Signed by Contoso Ltd");
            Check("line: signed, matching claim", AppText.PublisherLine(SignatureState.Signed, "Contoso Ltd", "Contoso Limited", false)
                == "Signed by Contoso Ltd");
            Check("line: signed, different claim",
                AppText.PublisherLine(SignatureState.Signed, "Contoso Ltd", ms, false) == "Signed by Contoso Ltd, though the file says Microsoft Corporation");
            Check("line: unsigned with a claim", AppText.PublisherLine(SignatureState.Unsigned, null, ms, false)
                == "Not signed; the file says Microsoft Corporation");
            Check("line: unsigned, no claim", AppText.PublisherLine(SignatureState.Unsigned, null, null, false) == "Not signed");
            Check("line: invalid with a claim", AppText.PublisherLine(SignatureState.Invalid, null, ms, false)
                == "Signature does not check out; the file says Microsoft Corporation");
            Check("line: invalid, no claim", AppText.PublisherLine(SignatureState.Invalid, null, null, false) == "Signature does not check out");
            Check("line: distrusted", AppText.PublisherLine(SignatureState.Distrusted, null, ms, false) == "Signature blocked by Windows");
            Check("line: unsigned in the Windows folder is not checked",
                AppText.PublisherLine(SignatureState.Unsigned, null, ms, true) == "In the Windows folder; signature not checked");
            Check("line: invalid in the Windows folder still says so",
                AppText.PublisherLine(SignatureState.Invalid, null, null, true) == "Signature does not check out");
            Check("line: not checked with a claim keeps today's line", AppText.PublisherLine(SignatureState.NotChecked, null, ms, false)
                == "From Microsoft Corporation");
            Check("line: not checked, no claim", AppText.PublisherLine(SignatureState.NotChecked, null, null, false) == "Publisher unknown");
            Check("line: signed without a readable name says only that", AppText.PublisherLine(SignatureState.Signed, "", null, false) == "Signed"
                && AppText.PublisherLine(SignatureState.Signed, null, ms, false) == "Signed; the file says Microsoft Corporation");
        }

        // ------------------------------------------------------------------ rows

        static void RowTests()
        {
            const string win = @"C:\Windows";
            var row = new StartupApp { Source = StartupSource.UserRun, Name = "x", ExePath = @"C:\Users\a\x.exe" };
            StartupApps.ApplyPublisher(row, "Microsoft Corporation", new SignatureInfo(SignatureState.Unsigned, null));
            Check("row: the claim and the state are filled", row.ClaimedCompany == "Microsoft Corporation"
                && row.Signature == SignatureState.Unsigned && row.Signer == null);
            Check("row: Publisher keeps the file's claim for today's callers", row.Publisher == "Microsoft Corporation");
            Check("app text: an unsigned row with a claim says the file says it",
                AppText.PublisherLine(row, win) == "Not signed; the file says Microsoft Corporation");

            var sys = new StartupApp { Source = StartupSource.MachineRun, Name = "s", ExePath = @"C:\Windows\System32\s.exe" };
            StartupApps.ApplyPublisher(sys, "Microsoft Corporation", new SignatureInfo(SignatureState.Unsigned, null, 0, true));
            Check("app text: an unsigned Windows file owned by Windows is not checked",
                AppText.PublisherLine(sys, win) == "In the Windows folder; signature not checked");
            // C:\Windows\Tasks, Temp, Tracing and spool\drivers\color are writable by standard users: a file put there
            // is owned by whoever put it, and its missing signature is real.
            var planted = new StartupApp { Source = StartupSource.UserRun, Name = "p", ExePath = @"C:\Windows\Tasks\p.exe" };
            StartupApps.ApplyPublisher(planted, "Microsoft Corporation", new SignatureInfo(SignatureState.Unsigned, null, 0, false));
            Check("app text: an unsigned file a user put in the Windows folder is not signed",
                AppText.PublisherLine(planted, win) == "Not signed; the file says Microsoft Corporation", AppText.PublisherLine(planted, win));
            Check("row: the owner fact is copied", sys.SystemOwned && !planted.SystemOwned);

            // A host that runs another file: the host's signature says nothing about that file.
            Check("host: rundll32 with a DLL runs another file", StartupApps.RunsOtherFile(@"C:\Windows\System32\rundll32.exe C:\Users\a\x.dll,Start"));
            Check("host: wscript with a script runs another file", StartupApps.RunsOtherFile("wscript.exe \"C:\\Users\\a\\x.vbs\""));
            Check("host: quoted powershell with a script runs another file",
                StartupApps.RunsOtherFile("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -File C:\\a.ps1"));
            Check("host: a host with no argument runs nothing else", !StartupApps.RunsOtherFile(@"C:\Windows\System32\rundll32.exe"));
            Check("host: an ordinary program with flags is not a host", !StartupApps.RunsOtherFile("\"C:\\Program Files\\App\\app.exe\" --minimized")
                && !StartupApps.RunsOtherFile(null));
            var host = new StartupApp { Source = StartupSource.UserRun, Name = "h", ExePath = @"C:\Tools\node.exe", RunsFile = true };
            StartupApps.ApplyPublisher(host, "OpenJS Foundation", new SignatureInfo(SignatureState.Signed, "OpenJS Foundation"));
            Check("app text: a signed host says the file it runs is not checked",
                AppText.PublisherLine(host, win) == "Signed by OpenJS Foundation; it runs another file, which is not checked", AppText.PublisherLine(host, win));
            var sysHost = new StartupApp { Source = StartupSource.UserRun, Name = "r", ExePath = @"C:\Windows\System32\rundll32.exe", RunsFile = true };
            StartupApps.ApplyPublisher(sysHost, "Microsoft Corporation", new SignatureInfo(SignatureState.Unsigned, null, 0, true));
            Check("app text: a Windows host running another file says neither is checked",
                AppText.PublisherLine(sysHost, win) == "In the Windows folder; it runs another file; neither is checked", AppText.PublisherLine(sysHost, win));

            // Names come from the file and its certificate: control, format and line-separator characters go, the
            // rest is cut to one short line.
            string rlo = ((char)0x202E).ToString(), lsep = ((char)0x2028).ToString(), zw = ((char)0x200B).ToString();
            var odd = new StartupApp { Source = StartupSource.UserRun, Name = "o", ExePath = @"C:\Users\a\o.exe" };
            StartupApps.ApplyPublisher(odd, "Micro" + rlo + "soft" + lsep + "Corp\t\n  Ltd" + zw, new SignatureInfo(SignatureState.Signed, "Con" + rlo + "toso" + new string('x', 300)));
            Check("row: a claimed name loses control and format characters", odd.ClaimedCompany == "Microsoft Corp Ltd" && odd.Publisher == odd.ClaimedCompany,
                odd.ClaimedCompany);
            Check("row: a signer name is cleaned and capped", odd.Signer != null && odd.Signer.StartsWith("Contosox", StringComparison.Ordinal)
                && odd.Signer.Length <= StartupApps.MaxNameChars, odd.Signer == null ? "null" : odd.Signer.Length.ToString());
            string pair = char.ConvertFromUtf32(0x1F600);
            var cut = new StartupApp();
            StartupApps.ApplyPublisher(cut, new string('a', StartupApps.MaxNameChars - 1) + pair, null);
            Check("row: the cap never splits a surrogate pair", cut.ClaimedCompany.Length == StartupApps.MaxNameChars - 1
                && !char.IsHighSurrogate(cut.ClaimedCompany[cut.ClaimedCompany.Length - 1]), cut.ClaimedCompany.Length.ToString());
            var blank = new StartupApp();
            StartupApps.ApplyPublisher(blank, rlo + " " + zw, null);
            Check("row: a name of nothing but hidden characters is no name", blank.ClaimedCompany == null);

            // The version resource is read only after the same attribute gate as the signature check.
            bool read = false;
            string claim = StartupApps.PublisherOf(@"C:\Users\a\OneDrive\x.exe", p => FileAttributes.Archive | (FileAttributes)0x400000,
                p => { read = true; return "Contoso"; });
            Check("claim: a cloud placeholder's version info is never read", claim == null && !read);
            claim = StartupApps.PublisherOf(@"C:\Users\a\link.exe", p => FileAttributes.ReparsePoint, p => { read = true; return "Contoso"; });
            Check("claim: a link's version info is never read", claim == null && !read);
            claim = StartupApps.PublisherOf(@"C:\Users\a\x.exe", p => FileAttributes.Archive, p => "  Contoso  ");
            Check("claim: an ordinary file's company is read", claim == "Contoso", claim);
            var near = new StartupApp { Source = StartupSource.UserRun, Name = "n", ExePath = @"C:\WindowsApps2\n.exe" };
            StartupApps.ApplyPublisher(near, null, new SignatureInfo(SignatureState.Unsigned, null));
            Check("app text: a folder that only starts with the Windows name is not the Windows folder",
                AppText.PublisherLine(near, win) == "Not signed");
            StartupApps.ApplyPublisher(sys, null, null);
            Check("row: no result means not checked", sys.Signature == SignatureState.NotChecked && sys.Signer == null
                && AppText.PublisherLine(sys, win) == "Publisher unknown");
            Check("app text: a null row is unknown", AppText.PublisherLine(null, win) == "Publisher unknown");
            Check("row: run and folder rows with a program are checked", StartupApps.ChecksSignature(row)
                && StartupApps.ChecksSignature(new StartupApp { Source = StartupSource.CommonFolder, ExePath = @"C:\p\a.exe" }));
            Check("row: a packaged app is signed as a package, so its exe is not checked",
                !StartupApps.ChecksSignature(new StartupApp { Source = StartupSource.Packaged, ExePath = @"C:\Program Files\WindowsApps\p\a.exe" }));
            Check("row: a row with no program is not checked", !StartupApps.ChecksSignature(new StartupApp { Source = StartupSource.UserFolder, Name = "x.url" })
                && !StartupApps.ChecksSignature(null));
            // A shortcut in the user's own Startup folder can name one program in LinkInfo and run another: its target
            // is not what runs, so naming its signer would vouch for an unverified path.
            Check("row: a user Startup-folder shortcut's target is not checked",
                !StartupApps.ChecksSignature(new StartupApp { Source = StartupSource.UserFolder, Name = "Teams.lnk", ExePath = @"C:\Program Files\T\t.exe" })
                && !StartupApps.ChecksSignature(new StartupApp { Source = StartupSource.UserFolder, Name = "TEAMS.LNK", ExePath = @"C:\Program Files\T\t.exe" }));
            Check("row: a program file in the user's Startup folder is checked",
                StartupApps.ChecksSignature(new StartupApp { Source = StartupSource.UserFolder, Name = "x.exe", ExePath = @"C:\Users\a\Startup\x.exe" }));
            Check("row: an all-users Startup shortcut (administrators only) is checked",
                StartupApps.ChecksSignature(new StartupApp { Source = StartupSource.CommonFolder, Name = "a.lnk", ExePath = @"C:\Program Files\A\a.exe" }));

            // Owner decision E26 (3): a valid signature never relaxes the location-based Keep lock.
            KnownItem keep = null;
            foreach (string exe in new[] { "SecurityHealthSystray.exe", "RtkAudUService64.exe", "igfxEM.exe" })
            {
                KnownItem k = StartupKnowledge.FindApp(exe);
                if (k != null && k.Verdict == Verdict.Keep) { keep = k; break; }
            }
            Check("keep lock: the built-in list has a Keep app to test with", keep != null);
            if (keep != null)
            {
                var fake = new StartupApp { Source = StartupSource.UserRun, Name = "k", ExeName = keep.Match, ExePath = @"C:\Users\a\Downloads\" + keep.Match };
                StartupApps.ApplyPublisher(fake, "Microsoft Corporation", new SignatureInfo(SignatureState.Signed, "Microsoft Windows"));
                Check("keep lock: a signed copy outside the protected folders earns no Keep",
                    StartupApps.KnownFor(fake, new[] { @"C:\Windows", @"C:\Program Files" }) == null);
            }

            Check("attributes: reparse points, offline and cloud placeholders are skipped",
                Signature.SkipForAttributes(FileAttributes.ReparsePoint | FileAttributes.Archive)
                && Signature.SkipForAttributes(FileAttributes.Offline)
                && Signature.SkipForAttributes((FileAttributes)0x400000)
                && Signature.SkipForAttributes((FileAttributes)0x40000));
            Check("attributes: an ordinary file is checked", !Signature.SkipForAttributes(FileAttributes.Archive)
                && !Signature.SkipForAttributes(FileAttributes.Normal) && !Signature.SkipForAttributes(FileAttributes.ReadOnly));
        }

        // ------------------------------------------------------------------ files (environment)

        static void FileTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ramopt-sig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Check("signature: missing file is not checked", Signature.Inspect(Path.Combine(dir, "missing.exe")).State == SignatureState.NotChecked);
                Check("signature: no path is not checked", Signature.Inspect(null).State == SignatureState.NotChecked
                    && Signature.Inspect("").State == SignatureState.NotChecked);
                Check("signature: a share is never opened", Signature.Inspect(@"\\localhost\c$\Windows\notepad.exe").State == SignatureState.NotChecked);
                Check("signature: a relative path is never opened", Signature.Inspect(@"Windows\notepad.exe").State == SignatureState.NotChecked);

                // The test runner itself is built here and never signed.
                string self = Assembly.GetEntryAssembly().Location, copy = Path.Combine(dir, "unsigned.exe");
                File.Copy(self, copy);
                SignatureInfo unsigned = Signature.Inspect(copy);
                Check("signature: an unsigned copy reads as unsigned", unsigned.State == SignatureState.Unsigned, unsigned.State + " 0x" + unsigned.Status.ToString("X8"));
                Check("signature: an unsigned file has no signer", unsigned.Signer == null);
                Check("signature: a file the user made is not owned by Windows", !unsigned.SystemOwned);

                string winFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
                if (File.Exists(winFile))
                {
                    SignatureInfo sys = Signature.Inspect(winFile);
                    Check("signature: a Windows file is owned by Windows", sys.State != SignatureState.NotChecked && sys.SystemOwned,
                        sys.State + " owned " + sys.SystemOwned);
                }

                string signed = SignedCandidate();
                if (signed == null)
                {
                    Console.WriteLine("SKIP signature: no embedded-signed candidate on this PC (tampered and signer tests)");
                    return;
                }
                SignatureInfo real = Signature.Inspect(signed);
                Check("signature: a signed candidate names a signer containing Microsoft",
                    real.State == SignatureState.Signed && real.Signer != null && real.Signer.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0,
                    signed + ": " + real.State + " " + real.Signer);

                string tampered = Path.Combine(dir, "tampered.exe");
                File.Copy(signed, tampered);
                SignatureInfo before = Signature.Inspect(tampered);
                Check("signature: an untouched copy of a signed file is signed", before.State == SignatureState.Signed, before.State.ToString());
                // Same length, and every time stamp put back the way a program that wants to hide the change would:
                // a result kept from the first check must not answer for the new bytes.
                DateTime written = File.GetLastWriteTimeUtc(tampered), created = File.GetCreationTimeUtc(tampered);
                byte[] bytes = File.ReadAllBytes(tampered);
                bytes[bytes.Length / 2] ^= 0xFF;
                File.WriteAllBytes(tampered, bytes);
                File.SetLastWriteTimeUtc(tampered, written);
                File.SetCreationTimeUtc(tampered, created);
                SignatureInfo after = Signature.Inspect(tampered);
                Check("signature: a tampered signed file reads as invalid", after.State == SignatureState.Invalid,
                    after.State + " 0x" + after.Status.ToString("X8"));
                Check("signature: a tampered file names no signer", after.Signer == null);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// The first Microsoft program present that ships with an embedded signature, or null. Files under the
        /// Windows folder (csc.exe, notepad.exe) and Program Files\Windows Defender are catalog-signed on Windows 11
        /// (Get-AuthenticodeSignature says "Catalog"), so they have no embedded signature to check. Picked by
        /// existence only, so a broken check fails the tests instead of skipping them.
        /// </summary>
        static string SignedCandidate()
        {
            string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var candidates = new List<string> {
                Path.Combine(x86, @"Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe"),
                Path.Combine(data, @"Microsoft\Windows Defender\Platform\CurrentVersion\MpCmdRun.exe"),
                Path.Combine(x86, @"Microsoft\Edge\Application\msedge.exe") };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }
    }
}
