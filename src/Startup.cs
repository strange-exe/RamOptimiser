using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RamOptimiser
{
    public enum Verdict
    {
        /// <summary>Nothing most people need stops working.</summary>
        Safe,
        /// <summary>A feature some people use stops until it is turned back on.</summary>
        Caution,
        /// <summary>Windows, security or hardware support: shown, never offered.</summary>
        Keep,
        /// <summary>Not in the built-in list: shown, never offered for services.</summary>
        Unknown
    }

    /// <summary>What the app knows about one startup app (by exe file name) or service (by service name).</summary>
    public sealed class KnownItem
    {
        public readonly bool IsService;
        public readonly string Match;
        public readonly string Label;
        public readonly string Purpose;
        public readonly Verdict Verdict;
        public readonly string Effect;

        public KnownItem(bool isService, string match, string label, string purpose, Verdict verdict, string effect)
        {
            IsService = isService; Match = match; Label = label; Purpose = purpose; Verdict = verdict; Effect = effect;
        }
    }

    /// <summary>
    /// The built-in knowledge base (StartupKb.cs, generated from researched entries) plus the Power Clean catalog.
    /// Compiled in, never read from a file the user can write: the elevated app acts only on what is listed here.
    /// </summary>
    public static partial class StartupKnowledge
    {
        static readonly KnownItem[] Entries = BuildEntries();

        public static KnownItem FindApp(string exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return null;
            foreach (KnownItem k in Entries)
                if (!k.IsService && string.Equals(k.Match, exeName, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }

        public static KnownItem FindService(string serviceName)
        {
            if (string.IsNullOrEmpty(serviceName)) return null;
            foreach (KnownItem k in Entries)
                if (k.IsService && string.Equals(k.Match, serviceName, StringComparison.OrdinalIgnoreCase)) return k;
            ServiceRule rule = ServiceCatalog.Find(serviceName);
            if (rule == null) return null;
            return new KnownItem(true, serviceName, rule.Label, rule.Why,
                rule.Tier == ServiceTier.Suggested ? Verdict.Safe : Verdict.Caution, null);
        }

        public static IEnumerable<KnownItem> Services()
        {
            foreach (KnownItem k in Entries) if (k.IsService) yield return k;
        }

        /// <summary>Only listed services that are safe or come with a caution may have their startup changed.</summary>
        public static bool CanDisableService(string serviceName)
        {
            KnownItem k = FindService(serviceName);
            return k != null && (k.Verdict == Verdict.Safe || k.Verdict == Verdict.Caution);
        }
    }

    // ====================================================================== startup apps

    /// <summary>
    /// Where a startup entry lives. The RunOnce and Packaged kinds are listed only by StartupApps.Query(true) and
    /// are read-only: a RunOnce entry can't be paused reversibly, and a packaged task belongs to Settings.
    /// </summary>
    public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder, UserRunOnce, MachineRunOnce, MachineRunOnce32, Packaged }

    public sealed class StartupApp
    {
        public StartupSource Source;
        /// <summary>The Run value name, or the file name in a Startup folder: also the StartupApproved value name.
        /// For a RunOnce row, the value name without its "!" or "*" prefix; for a packaged row, its display name.</summary>
        public string Name;
        public string ExeName;
        public string ExePath;
        /// <summary>The company the file names (its version info). Kept for today's callers; AppText.PublisherLine
        /// is the line that says whether a signature backs it.</summary>
        public string Publisher;
        /// <summary>The file's embedded Authenticode signature. Display only: never part of a Keep or switch decision.</summary>
        public SignatureState Signature;
        /// <summary>Who signed the file, for a Signed row whose certificate name could be read; else null.</summary>
        public string Signer;
        /// <summary>The company the file names, which anyone can write into a file: the same value as Publisher.</summary>
        public string ClaimedCompany;
        /// <summary>The checked file is owned by TrustedInstaller or SYSTEM (SignatureInfo.SystemOwned).</summary>
        public bool SystemOwned;
        /// <summary>
        /// The program is a host that runs another file named in its command (rundll32 x.dll, wscript x.vbs,
        /// node x.js): the host's signature says nothing about that file.
        /// </summary>
        public bool RunsFile;
        public bool Enabled;
        public KnownItem Known;
        /// <summary>Packaged rows: the package family name (e.g. 5319275A.WhatsAppDesktop_cv1g1gvanyjgm).</summary>
        public string PackageFamily;
        /// <summary>Packaged rows: the task's state as Windows records it. Meaningless for other rows.</summary>
        public PackagedState Packaged;
        /// <summary>Packaged rows: a policy decides whether it starts (states 3 and 4).</summary>
        public bool PolicySet;
        /// <summary>Entries under HKLM or the all-users folder need administrator rights to change.</summary>
        public bool NeedsAdmin
        {
            get
            {
                switch (Source)
                {
                    case StartupSource.MachineRun: case StartupSource.MachineRun32: case StartupSource.CommonFolder:
                    case StartupSource.MachineRunOnce: case StartupSource.MachineRunOnce32:
                        return true;
                    default:
                        return false;
                }
            }
        }
        /// <summary>Only Run and Startup-folder rows have a reversible on/off switch (StartupApproved).</summary>
        public bool CanToggle
        {
            get { return !StartupApps.IsRunOnce(Source) && Source != StartupSource.Packaged; }
        }
    }

    /// <summary>
    /// Startup apps from the Run keys and Startup folders, turned off and on the way Task Manager does it: a value
    /// under Explorer\StartupApproved, so the app's own entry is never deleted and on/off is always reversible.
    /// The Run keys are documented (learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys);
    /// the StartupApproved value format is not: first byte even = on, odd = off (Task Manager writes 02 and 03),
    /// then an 8-byte FILETIME of the change. Observed on this PC (02, 03, 06); may change in a future Windows.
    /// </summary>
    public static class StartupApps
    {
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
        const string RunOnce32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";
        const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        /// <summary>Shortcuts are a few KB; anything bigger is not read.</summary>
        const int MaxShortcutBytes = 64 * 1024;

        /// <summary>The rows the Startup tab shows today: Run keys and Startup folders, shortcuts listed by name.</summary>
        public static List<StartupApp> Query()
        {
            return Query(false);
        }

        /// <summary>
        /// With includeExtraSources, also the RunOnce entries and packaged (Store / MSIX) startup tasks, both
        /// read-only, and each Startup-folder shortcut's real target. The Startup tab asks for them; the no-argument
        /// form keeps the old list for any caller that can't show read-only rows.
        /// </summary>
        public static List<StartupApp> Query(bool includeExtraSources)
        {
            var list = new List<StartupApp>();
            foreach (StartupSource source in IncludedSources(includeExtraSources))
            {
                switch (source)
                {
                    case StartupSource.UserRun: ReadRun(list, Registry.CurrentUser, RunPath, source); break;
                    case StartupSource.MachineRun: ReadRun(list, Registry.LocalMachine, RunPath, source); break;
                    case StartupSource.MachineRun32: ReadRun(list, Registry.LocalMachine, Run32Path, source); break;
                    // learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
                    case StartupSource.UserRunOnce: ReadRun(list, Registry.CurrentUser, RunOncePath, source); break;
                    case StartupSource.MachineRunOnce: ReadRun(list, Registry.LocalMachine, RunOncePath, source); break;
                    case StartupSource.MachineRunOnce32: ReadRun(list, Registry.LocalMachine, RunOnce32Path, source); break;
                    case StartupSource.UserFolder:
                        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), source, includeExtraSources); break;
                    case StartupSource.CommonFolder:
                        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), source, includeExtraSources); break;
                    case StartupSource.Packaged: list.AddRange(PackagedStartup.Read()); break;
                }
            }
            string[] roots = ProtectedRoots();
            foreach (StartupApp a in list)
            {
                // ApprovedKey is never consulted for a row without a StartupApproved switch.
                if (a.Source == StartupSource.Packaged) a.Enabled = PackagedStartup.IsOn(a.Packaged);
                else if (IsRunOnce(a.Source)) a.Enabled = true;
                else a.Enabled = IsApprovedEnabled(ReadApproved(a));
                a.Known = KnownFor(a, roots);
                // After Known, which a signature never changes. Inspect opens only local-drive files, no network.
                // A user shortcut's target is not known to be what runs: neither its claim nor its signature is shown.
                ApplyPublisher(a, IsUserShortcut(a) ? null : PublisherOf(a.ExePath), ChecksSignature(a) ? Signature.Inspect(a.ExePath) : null);
            }
            return list;
        }

        /// <summary>
        /// The sources a listing reads, in order. Run-once and packaged rows come only with includeExtraSources
        /// (for a caller that can show read-only rows). Pure.
        /// </summary>
        public static List<StartupSource> IncludedSources(bool includeExtraSources)
        {
            var sources = new List<StartupSource> { StartupSource.UserRun, StartupSource.MachineRun, StartupSource.MachineRun32 };
            if (includeExtraSources)
            {
                sources.Add(StartupSource.UserRunOnce);
                sources.Add(StartupSource.MachineRunOnce);
                sources.Add(StartupSource.MachineRunOnce32);
            }
            sources.Add(StartupSource.UserFolder);
            sources.Add(StartupSource.CommonFolder);
            if (includeExtraSources) sources.Add(StartupSource.Packaged);
            return sources;
        }

        /// <summary>
        /// The built-in list's entry for a row, with a Keep match only where it can be trusted (TrustedKnown). A
        /// shortcut in the user's own Startup folder never earns Keep: its target path comes from LinkInfo or the
        /// environment block, which the user can write to disagree with what the shell really runs. Pure.
        /// </summary>
        public static KnownItem KnownFor(StartupApp a, IList<string> protectedRoots)
        {
            if (a == null) return null;
            string trusted = a.Source == StartupSource.UserFolder ? null : a.ExePath;
            return TrustedKnown(StartupKnowledge.FindApp(a.ExeName), trusted, protectedRoots);
        }

        /// <summary>
        /// Whether a row's program gets a signature check: any row with an exe path, except a packaged app, which is
        /// signed as a package (AppxSignature.p7x) rather than per exe, so "Not signed" would be wrong for it. Pure.
        /// </summary>
        public static bool ChecksSignature(StartupApp a)
        {
            return a != null && !string.IsNullOrEmpty(a.ExePath) && a.Source != StartupSource.Packaged && !IsUserShortcut(a);
        }

        /// <summary>
        /// A shortcut in the user's own Startup folder: its target comes from LinkInfo or the environment block, which
        /// the user can write to disagree with what the shell really runs (the same reason KnownFor gives it no Keep).
        /// The all-users folder needs administrator rights to write, so its shortcuts are taken at their word. Pure.
        /// </summary>
        public static bool IsUserShortcut(StartupApp a)
        {
            return a != null && a.Source == StartupSource.UserFolder && a.Name != null
                && a.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The longest company or signer name kept: one short line in the Startup tab.</summary>
        public const int MaxNameChars = 100;

        /// <summary>
        /// Fills a row's publisher facts: the company the file claims and what its signature says (NotChecked when
        /// there is no result). Both names come from the file and its certificate, so they are cleaned to one short
        /// line (CleanName). Touches nothing else, so Known, Keep and the switch stay as the location decided. Pure.
        /// </summary>
        public static void ApplyPublisher(StartupApp a, string claimed, SignatureInfo signature)
        {
            if (a == null) return;
            a.ClaimedCompany = CleanName(claimed);
            a.Publisher = a.ClaimedCompany;
            a.Signature = signature == null ? SignatureState.NotChecked : signature.State;
            a.Signer = signature == null ? null : CleanName(signature.Signer);
            a.SystemOwned = signature != null && signature.SystemOwned;
        }

        /// <summary>
        /// A name fit for one line: format characters (bidi overrides, zero-width marks) are dropped; control
        /// characters and line or paragraph separators become spaces; runs of white space become one; the result is
        /// cut to MaxNameChars without splitting a surrogate pair. Null when nothing is left. Pure.
        /// </summary>
        public static string CleanName(string name)
        {
            if (name == null) return null;
            var sb = new StringBuilder(Math.Min(name.Length, MaxNameChars * 2));
            bool space = false;
            int i = 0;
            while (i < name.Length)
            {
                // Code point by code point: a supplementary format character (a tag character) comes as a surrogate
                // pair, and a surrogate without its partner is dropped too.
                int width;
                UnicodeCategory cat = SafeText.CategoryAt(name, i, out width);
                int at = i;
                i += width;
                if (cat == UnicodeCategory.Format || cat == UnicodeCategory.Surrogate) continue;
                bool blank = (width == 1 && char.IsWhiteSpace(name[at])) || cat == UnicodeCategory.Control
                    || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator;
                if (blank) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(name, at, width);
                if (sb.Length > MaxNameChars) break;
            }
            int n = Math.Min(sb.Length, MaxNameChars);
            if (n < sb.Length && char.IsHighSurrogate(sb[n - 1])) n--;
            string s = sb.ToString(0, n).TrimEnd();
            return s.Length == 0 ? null : s;
        }

        public static bool IsRunOnce(StartupSource s)
        {
            return s == StartupSource.UserRunOnce || s == StartupSource.MachineRunOnce || s == StartupSource.MachineRunOnce32;
        }

        /// <summary>
        /// A RunOnce value name without its documented prefixes: "!" (delete only after the command succeeds) and
        /// "*" (run in safe mode too). A name that is nothing but prefixes is kept as it is. Pure.
        /// </summary>
        public static string RunOnceDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string trimmed = name.TrimStart('!', '*');
            return trimmed.Length == 0 ? name : trimmed;
        }

        /// <summary>Refuses a row with no reversible switch, before any key is opened. Pure.</summary>
        public static void EnsureCanToggle(StartupApp a)
        {
            if (a == null) throw new ArgumentNullException("a");
            if (!a.CanToggle) throw new InvalidOperationException("this entry can't be changed here");
        }

        /// <summary>
        /// Points a Startup-folder row at the program its shortcut really runs, so the built-in list matches the real
        /// exe and not "{shortcut name}.exe". A target that is not an .exe on a local drive keeps the name-based
        /// guess: a share (\\host\...) would make the elevated app connect to it to read the publisher. Pure.
        /// </summary>
        public static void ApplyShortcutTarget(StartupApp a, string target, string arguments)
        {
            if (a == null || !IsLocalDrivePath(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (a != null && a.ExeName == null) a.ExeName = Path.GetFileNameWithoutExtension(a.Name) + ".exe";
                return;
            }
            a.ExePath = target;
            string command = "\"" + target + "\" " + (arguments ?? "");
            a.ExeName = LaunchedExe(command) ?? SafeFileName(target);
            a.RunsFile = RunsOtherFile(command);
        }

        /// <summary>A fully qualified path on a lettered drive ("C:\..."): no share, device or \\?\ prefix. Pure.</summary>
        public static bool IsLocalDrivePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 3) return false;
            char d = char.ToUpperInvariant(path[0]);
            return d >= 'A' && d <= 'Z' && path[1] == ':' && path[2] == '\\';
        }

        /// <summary>
        /// The built-in list matches by file name, which anyone can copy. A Keep match locks the switch, so it counts
        /// only for a program in a folder only administrators can write; elsewhere the row is treated as unknown. Pure.
        /// </summary>
        public static KnownItem TrustedKnown(KnownItem k, string exePath, IList<string> protectedRoots)
        {
            if (k == null || k.Verdict != Verdict.Keep) return k;
            foreach (string root in protectedRoots)
                if (IsInside(exePath, root)) return k;
            return null;
        }

        static string[] ProtectedRoots()
        {
            var roots = new List<string>();
            foreach (string r in new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432") })
                if (!string.IsNullOrEmpty(r)) roots.Add(r);
            return roots.ToArray();
        }

        /// <summary>True when the normalised path lies under dir ("..", "." and mixed slashes resolved first). Pure.</summary>
        public static bool IsInside(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            try
            {
                string full = Path.GetFullPath(path), root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        static void ReadRun(List<StartupApp> list, RegistryKey hive, string path, StartupSource source)
        {
            using (RegistryKey key = hive.OpenSubKey(path))
            {
                if (key == null) return;
                foreach (string name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue; // the (Default) value is not a startup entry
                    string command = key.GetValue(name) as string;
                    string exe = ExePathOf(command);
                    list.Add(new StartupApp { Source = source, Name = IsRunOnce(source) ? RunOnceDisplayName(name) : name, ExePath = exe,
                        ExeName = LaunchedExe(command) ?? SafeFileName(exe), RunsFile = RunsOtherFile(command) });
                }
            }
        }

        static void ReadFolder(List<StartupApp> list, string dir, StartupSource source, bool readShortcuts)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (string file in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                bool isExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                var a = new StartupApp { Source = source, Name = name, ExePath = isExe ? file : null,
                    ExeName = isExe ? name : Path.GetFileNameWithoutExtension(name) + ".exe" };
                // A shortcut's target is read from its bytes (LnkReader), never resolved through the shell.
                if (readShortcuts && name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string target, args;
                    if (LnkReader.TryReadTarget(ReadRegularFile(file, MaxShortcutBytes), out target, out args))
                        ApplyShortcutTarget(a, target, args);
                }
                list.Add(a);
            }
        }

        /// <summary>
        /// The bytes of a small regular file, or null. The file is opened without following a link
        /// (FILE_FLAG_OPEN_REPARSE_POINT) and refused when it is a link, a directory or a device, or larger than
        /// maxBytes, so a planted junction or symbolic link can't make the elevated app read another file.
        /// </summary>
        public static byte[] ReadRegularFile(string path, int maxBytes)
        {
            if (string.IsNullOrEmpty(path) || maxBytes <= 0) return null;
            using (SafeFileHandle h = CreateFileW(path, GenericRead, FileShareRead, IntPtr.Zero, OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                if (GetFileType(h) != FileTypeDisk) return null;
                FileInfoByHandle info;
                if (!GetFileInformationByHandle(h, out info)) return null;
                const uint refused = (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device);
                if ((info.FileAttributes & refused) != 0) return null;
                long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
                if (size > maxBytes) return null;
                try
                {
                    using (var fs = new FileStream(h, FileAccess.Read))
                    {
                        var buffer = new byte[size];
                        int read = 0;
                        while (read < buffer.Length)
                        {
                            int n = fs.Read(buffer, read, buffer.Length - read);
                            if (n <= 0) return null; // shrank while reading
                            read += n;
                        }
                        return buffer;
                    }
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        }

        const uint GenericRead = 0x80000000, FileShareRead = 0x1, OpenExisting = 3;
        const uint FileFlagOpenReparsePoint = 0x00200000, FileFlagSequentialScan = 0x08000000, FileTypeDisk = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct FileInfoByHandle
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint GetFileType(SafeFileHandle file);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoByHandle info);

        /// <summary>The program path at the start of a Run command: quoted, or up to the first ".exe". Pure.</summary>
        public static string ExePathOf(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string c = Environment.ExpandEnvironmentVariables(command.Trim());
            if (c.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = c.IndexOf('"', 1);
                return end > 1 ? c.Substring(1, end - 1) : null;
            }
            int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) return c.Substring(0, exe + 4);
            int space = c.IndexOf(' ');
            return space > 0 ? c.Substring(0, space) : c;
        }

        /// <summary>
        /// Squirrel-style launchers (Discord, Slack, classic Teams) run "Update.exe --processStart App.exe": the app is
        /// the argument, not Update.exe. Null for any other command. Pure.
        /// </summary>
        public static string LaunchedExe(string command)
        {
            string exe = SafeFileName(ExePathOf(command));
            if (!string.Equals(exe, "Update.exe", StringComparison.OrdinalIgnoreCase)) return null;
            const string flag = "--processStart";
            int at = command.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            string rest = command.Substring(at + flag.Length).TrimStart(' ', '=');
            if (rest.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = rest.IndexOf('"', 1);
                return end > 1 ? SafeFileName(rest.Substring(1, end - 1)) : null;
            }
            int space = rest.IndexOf(' ');
            string token = space > 0 ? rest.Substring(0, space) : rest;
            return token.Length > 0 ? SafeFileName(token) : null;
        }

        static string SafeFileName(string path)
        {
            if (path == null) return null;
            try { return Path.GetFileName(path); }
            catch (ArgumentException) { return null; }
        }

        /// <summary>Programs whose job is to run another file named in their command line.</summary>
        static readonly HashSet<string> ScriptHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "rundll32.exe", "regsvr32.exe", "wscript.exe", "cscript.exe", "mshta.exe", "powershell.exe", "pwsh.exe",
            "cmd.exe", "node.exe", "python.exe", "pythonw.exe", "java.exe", "javaw.exe" };

        /// <summary>
        /// Whether a command runs a script or DLL host with something after it (rundll32 x.dll,Entry; wscript x.vbs;
        /// powershell -File x.ps1): the host is what gets checked, not the file it runs. Pure.
        /// </summary>
        public static bool RunsOtherFile(string command)
        {
            string exe = ExePathOf(command);
            if (exe == null || !ScriptHosts.Contains(SafeFileName(exe) ?? "")) return false;
            string c = Environment.ExpandEnvironmentVariables(command.Trim());
            int at = c.IndexOf(exe, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            int after = at + exe.Length;
            if (after < c.Length && c[after] == '"') after++;
            return c.Substring(after).Trim().Length > 0;
        }

        static string PublisherOf(string exePath)
        {
            return PublisherOf(exePath, File.GetAttributes, p => FileVersionInfo.GetVersionInfo(p).CompanyName);
        }

        /// <summary>
        /// The company the file names, through the same gate as the signature check: a link (it could lead to a
        /// share) or an offline or cloud placeholder (reading its version resource would download it) is not read.
        /// attributes and company are the file system's; tests pass fakes.
        /// </summary>
        internal static string PublisherOf(string exePath, Func<string, FileAttributes> attributes, Func<string, string> company)
        {
            // Local drives only: a share in a user-writable Run value would make the elevated app connect to it.
            if (!IsLocalDrivePath(exePath)) return null;
            try
            {
                if (Signature.SkipForAttributes(attributes(exePath))) return null;
                string name = company(exePath);
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static void ApprovedKey(StartupApp a, out RegistryKey hive, out string sub)
        {
            switch (a.Source)
            {
                case StartupSource.UserRun: hive = Registry.CurrentUser; sub = ApprovedBase + "Run"; break;
                case StartupSource.MachineRun: hive = Registry.LocalMachine; sub = ApprovedBase + "Run"; break;
                case StartupSource.MachineRun32: hive = Registry.LocalMachine; sub = ApprovedBase + "Run32"; break;
                case StartupSource.UserFolder: hive = Registry.CurrentUser; sub = ApprovedBase + "StartupFolder"; break;
                case StartupSource.CommonFolder: hive = Registry.LocalMachine; sub = ApprovedBase + "StartupFolder"; break;
                // RunOnce and packaged rows have no StartupApproved value: never map them to someone else's.
                default: throw new InvalidOperationException("this entry can't be changed here");
            }
        }

        static byte[] ReadApproved(StartupApp a)
        {
            RegistryKey hive; string sub;
            ApprovedKey(a, out hive, out sub);
            using (RegistryKey key = hive.OpenSubKey(sub)) return key == null ? null : key.GetValue(a.Name) as byte[];
        }

        /// <summary>No value means on (Windows has not recorded a choice yet); otherwise the low bit of byte 0 is "off". Pure.</summary>
        public static bool IsApprovedEnabled(byte[] value)
        {
            return value == null || value.Length == 0 || (value[0] & 1) == 0;
        }

        /// <summary>The 12-byte value Task Manager writes: 02 (on) or 03 (off), three zero bytes, FILETIME. Pure.</summary>
        public static byte[] ApprovedBytes(bool enabled, DateTime utc)
        {
            var b = new byte[12];
            b[0] = enabled ? (byte)2 : (byte)3;
            if (!enabled) BitConverter.GetBytes(utc.ToFileTimeUtc()).CopyTo(b, 4);
            return b;
        }

        /// <summary>
        /// Turns one entry on or off. Throws UnauthorizedAccessException for an all-users entry when not elevated, and
        /// IOException when a per-user key does not resolve inside this user's own hive (a registry link the user
        /// planted could otherwise point an elevated write at a machine-wide key). Throws InvalidOperationException
        /// for a RunOnce or packaged row, which has no reversible switch.
        /// </summary>
        public static void SetEnabled(StartupApp a, bool enabled)
        {
            EnsureCanToggle(a);
            RegistryKey hive; string sub;
            ApprovedKey(a, out hive, out sub);
            using (RegistryKey key = hive.CreateSubKey(sub))
            {
                if (hive == Registry.CurrentUser && !IsOwnUserKey(KernelPath(key), CurrentSid(), sub))
                    throw new IOException("its settings key points outside your user profile");
                key.SetValue(a.Name, ApprovedBytes(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
            }
        }

        /// <summary>True when a kernel key path is exactly \REGISTRY\USER\{sid}\{sub}. Pure.</summary>
        public static bool IsOwnUserKey(string kernelPath, string sid, string sub)
        {
            if (string.IsNullOrEmpty(kernelPath) || string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(sub)) return false;
            return string.Equals(kernelPath, @"\REGISTRY\USER\" + sid + @"\" + sub.Trim('\\'), StringComparison.OrdinalIgnoreCase);
        }

        static string CurrentSid()
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) return id.User == null ? null : id.User.Value;
        }

        /// <summary>The key's real path after any links were followed (NtQueryKey, KeyNameInformation); null if unknown.</summary>
        public static string KernelPath(RegistryKey key)
        {
            int size = 512;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int needed;
                    int status = NtQueryKey(key.Handle, KeyNameInformation, buf, size, out needed);
                    if (status == 0)
                    {
                        int bytes = Marshal.ReadInt32(buf);
                        if (bytes < 0 || bytes > size - 4) return null;
                        return Marshal.PtrToStringUni(IntPtr.Add(buf, 4), bytes / 2);
                    }
                    if (status != StatusBufferTooSmall && status != StatusBufferOverflow) return null;
                    size = Math.Max(needed, size * 2);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }

        const int KeyNameInformation = 3;
        const int StatusBufferTooSmall = unchecked((int)0xC0000023), StatusBufferOverflow = unchecked((int)0x80000005);

        [DllImport("ntdll.dll")]
        static extern int NtQueryKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, int infoClass, IntPtr info, int length, out int resultLength);
    }

    // ====================================================================== services at startup

    public enum StartMode { Automatic, AutomaticDelayed, Manual, Disabled, Other }

    public sealed class StartupService
    {
        public string Name;
        public string DisplayName;
        public StartMode Mode;
        public bool Running;
        public long MemoryBytes = -1;
        public int SharedWith;
        public KnownItem Known;
        /// <summary>What this app's journal says about the service, checked against its start type now.</summary>
        public JournalState Journal;
        /// <summary>This app set it to Manual and recorded how it started before (Journal is OffByApp).</summary>
        public bool DisabledByApp { get { return Journal == JournalState.OffByApp; } }
        /// <summary>The service's own permissions deny administrators a startup change (e.g. Gaming Services).</summary>
        public bool Protected;
        public bool CanDisable { get { return Mode == StartMode.Automatic || Mode == StartMode.AutomaticDelayed; } }
    }

    /// <summary>
    /// A service's journal entry against its start type now. Untouched: no entry. OffByApp: this app set it to Manual
    /// and it still is. TurnedBackOn: something set it back to start with Windows. OffElsewhere: someone Disabled it.
    /// Orphan: the service is no longer listed, no longer installed, or can't be read. Only OffByApp can be undone;
    /// the last three are tidied away by an elevated session (StartupServices.TidyJournal).
    /// </summary>
    public enum JournalState { Untouched, OffByApp, TurnedBackOn, OffElsewhere, Orphan }

    /// <summary>
    /// How a Startup-tab change of a service went. TurnedOff* and TurnedOn* other than the plain ones are partial:
    /// the start type changed, but stopping or starting it now did not go as asked. Refused* and Failed changed nothing.
    /// </summary>
    public enum StartupChangeKind
    {
        TurnedOff, TurnedOffWasNotRunning, TurnedOffStillRunningDependents, TurnedOffStopFailed, TurnedOffStillStopping,
        TurnedOn, TurnedOnStartFailed,
        /// <summary>Starts with Windows again, but the delayed start it had could not be put back.</summary>
        TurnedOnDelayNotRestored,
        AlreadyOn,
        RefusedNotListed, RefusedNotAtStartup, RefusedNotChangedByApp, RefusedChangedElsewhere, RefusedProtected, RefusedNeedsAdmin,
        Failed
    }

    public static class StartupChanges
    {
        /// <summary>False for the refusals and Failed (nothing changed); true otherwise, partial changes included.</summary>
        public static bool IsOk(StartupChangeKind k)
        {
            switch (k)
            {
                case StartupChangeKind.RefusedNotListed:
                case StartupChangeKind.RefusedNotAtStartup:
                case StartupChangeKind.RefusedNotChangedByApp:
                case StartupChangeKind.RefusedChangedElsewhere:
                case StartupChangeKind.RefusedProtected:
                case StartupChangeKind.RefusedNeedsAdmin:
                case StartupChangeKind.Failed:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Short words for a change, display only, for callers without the app's wording; the Startup tab shows
        /// AppText.StartupChangeReason and StartupChangeNote instead. A test keeps the refusal words equal to
        /// StartupChangeReason's. Nothing may branch on this text.
        /// </summary>
        internal static string DisplayMessage(StartupChangeKind k, string detail)
        {
            switch (k)
            {
                case StartupChangeKind.RefusedNotListed: return "not in the list of services that are safe to change";
                case StartupChangeKind.RefusedNotAtStartup: return "does not start with Windows";
                case StartupChangeKind.RefusedNotChangedByApp: return "was not changed by this app";
                case StartupChangeKind.RefusedChangedElsewhere: return "its start type was changed after this app turned it off, so it was left as it is";
                case StartupChangeKind.RefusedProtected: return "Windows protects this service, so its startup can't be changed";
                case StartupChangeKind.RefusedNeedsAdmin: return "needs administrator rights";
                case StartupChangeKind.Failed: return detail;
                case StartupChangeKind.TurnedOff: return "won't start with Windows; stopped";
                case StartupChangeKind.TurnedOffWasNotRunning: return "won't start with Windows";
                case StartupChangeKind.TurnedOffStillRunningDependents: return "won't start with Windows; still running until restart";
                case StartupChangeKind.TurnedOffStopFailed: return "won't start with Windows; could not stop it now";
                case StartupChangeKind.TurnedOffStillStopping: return "won't start with Windows; still stopping";
                case StartupChangeKind.TurnedOn: return "starts with Windows again";
                case StartupChangeKind.TurnedOnStartFailed: return "starts with Windows again; start it now failed";
                case StartupChangeKind.TurnedOnDelayNotRestored: return "starts with Windows again; not delayed as before";
                case StartupChangeKind.AlreadyOn: return "already starts with Windows";
                default: return detail;
            }
        }
    }

    public sealed class StartupChange
    {
        public StartupChangeKind Kind;
        /// <summary>The Windows error text, for Failed only.</summary>
        public string Detail;

        /// <summary>Derived from Kind: false for a refusal or a failure (nothing changed).</summary>
        public bool Ok { get { return StartupChanges.IsOk(Kind); } }

        /// <summary>Display only, derived from Kind and Detail (see StartupChanges.DisplayMessage).</summary>
        public string Message { get { return StartupChanges.DisplayMessage(Kind, Detail); } }
    }

    /// <summary>
    /// Services that start with Windows. "Disable" sets a listed safe or caution service to Manual (it can still
    /// be started on demand) and stops it; the old start type is kept under an administrators-only key in HKLM so
    /// Re-enable puts it back. ChangeServiceConfig / ChangeServiceConfig2 (learn.microsoft.com/en-us/windows/win32/api/winsvc/).
    /// </summary>
    public static class StartupServices
    {
        const string JournalPath = @"SOFTWARE\RamOptimiser\ServiceStartup";

        public static List<StartupService> Query()
        {
            Dictionary<string, string> journal = ReadJournal();
            var result = new List<StartupService>();
            var runningPerPid = new Dictionary<int, int>();
            var pids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            using (var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode, ProcessId, PathName FROM Win32_Service"))
            using (ManagementObjectCollection all = searcher.Get())
            {
                foreach (ManagementObject mo in all)
                {
                    using (mo)
                    {
                        string name = mo["Name"] as string;
                        bool running = string.Equals(mo["State"] as string, "Running", StringComparison.OrdinalIgnoreCase);
                        int pid = mo["ProcessId"] == null ? 0 : Convert.ToInt32(mo["ProcessId"], CultureInfo.InvariantCulture);
                        if (running && pid > 0) { int n; runningPerPid.TryGetValue(pid, out n); runningPerPid[pid] = n + 1; }
                        KnownItem known = StartupKnowledge.FindService(name);
                        string path = mo["PathName"] as string;
                        bool thirdParty = !string.IsNullOrEmpty(path) && !IsUnder(StartupApps.ExePathOf(path), windir);
                        bool recorded = journal.ContainsKey(name);
                        // Listed services always; others only when they are third-party and running (Windows has hundreds).
                        if (known == null && !recorded && !(thirdParty && running)) continue;
                        var s = new StartupService
                        {
                            Name = name,
                            DisplayName = mo["DisplayName"] as string ?? name,
                            Running = running,
                            Known = known,
                            // Classified against the current start type below.
                            Journal = recorded ? JournalState.OffByApp : JournalState.Untouched,
                        };
                        if (running && pid > 0) pids[name] = pid;
                        result.Add(s);
                    }
                }
            }
            bool elevated = Privileges.IsElevated();
            foreach (StartupService s in result)
            {
                s.Mode = ReadMode(s.Name);
                // Only meaningful with admin rights: without them every service would look protected.
                s.Protected = elevated && StartupKnowledge.CanDisableService(s.Name) && !CanChangeConfig(s.Name);
                // The journal only says this app once set it to Manual. If it is no longer Manual (another admin,
                // a policy, an updater, or a Disable whose change failed), the row shows the real start type and
                // the state says what happened. ReadJournal keeps listed names only, so listed is true here.
                s.Journal = ClassifyJournal(s.Journal != JournalState.Untouched, true, s.Mode != StartMode.Other, s.Mode);
                int pid;
                if (!pids.TryGetValue(s.Name, out pid)) continue;
                int n; runningPerPid.TryGetValue(pid, out n); s.SharedWith = n;
                try { using (Process p = Process.GetProcessById(pid)) s.MemoryBytes = p.PrivateMemorySize64; }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            result.Sort((a, b) =>
            {
                int c = Rank(a).CompareTo(Rank(b));
                return c != 0 ? c : b.MemoryBytes.CompareTo(a.MemoryBytes);
            });
            return result;
        }

        /// <summary>Offered rows first (safe, then caution), then locked ones.</summary>
        static int Rank(StartupService s)
        {
            Verdict v = s.Known == null ? Verdict.Unknown : s.Known.Verdict;
            return v == Verdict.Safe ? 0 : v == Verdict.Caution ? 1 : v == Verdict.Unknown ? 2 : 3;
        }

        static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            return path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        static StartMode ReadMode(string name)
        {
            try { return ReadModeOrThrow(name); }
            catch (InvalidOperationException) { return StartMode.Other; }
        }

        /// <summary>As ReadMode, but a service that can't be opened throws InvalidOperationException instead of reading as Other.</summary>
        static StartMode ReadModeOrThrow(string name)
        {
            using (var sc = new ServiceController(name))
            {
                switch (sc.StartType)
                {
                    case ServiceStartMode.Automatic: return IsDelayed(name) ? StartMode.AutomaticDelayed : StartMode.Automatic;
                    case ServiceStartMode.Manual: return StartMode.Manual;
                    case ServiceStartMode.Disabled: return StartMode.Disabled;
                    default: return StartMode.Other;
                }
            }
        }

        /// <summary>A journal entry counts only while the service is still at the Manual start this app set. Pure.</summary>
        public static bool JournalStillHolds(bool recorded, StartMode now)
        {
            return ClassifyJournal(recorded, true, true, now) == JournalState.OffByApp;
        }

        /// <summary>
        /// The journal state machine, pure. Untouched -(Disable)-> OffByApp -(Reenable)-> Untouched; OffByApp becomes
        /// TurnedBackOn or OffElsewhere when something else sets Automatic or Disabled; any record becomes Orphan when
        /// the service leaves the list or is uninstalled; TidyJournal returns the last three to Untouched. Someone
        /// setting it to Manual again can't be told apart from OffByApp: Windows keeps no reliable change history.
        /// </summary>
        public static JournalState ClassifyJournal(bool recorded, bool listed, bool installed, StartMode now)
        {
            if (!recorded) return JournalState.Untouched;
            if (!listed || !installed) return JournalState.Orphan;
            switch (now)
            {
                case StartMode.Manual: return JournalState.OffByApp;
                case StartMode.Automatic:
                case StartMode.AutomaticDelayed: return JournalState.TurnedBackOn;
                case StartMode.Disabled: return JournalState.OffElsewhere;
                default: return JournalState.Orphan; // the start type can't be read
            }
        }

        /// <summary>Entries this app can no longer act on: their record is dead and is removed by TidyJournal.</summary>
        public static bool ShouldForget(JournalState s)
        {
            return s == JournalState.TurnedBackOn || s == JournalState.OffElsewhere || s == JournalState.Orphan;
        }

        /// <summary>
        /// TidyJournal's decision, pure: the value names to delete, in the order given. Data other than "auto" or
        /// "auto-delayed" is malformed and goes; a name not listed goes without its service being read; otherwise
        /// the start type decides (ShouldForget). A mode function that throws keeps that one name for the next tidy.
        /// </summary>
        internal static List<string> NamesToForget(IEnumerable<KeyValuePair<string, string>> values, Func<string, bool> listed,
            Func<string, StartMode> mode)
        {
            var forget = new List<string>();
            if (values == null) return forget;
            foreach (KeyValuePair<string, string> v in values)
            {
                string name = v.Key;
                if (string.IsNullOrEmpty(name)) continue; // the key's default value is not a record
                if (v.Value != "auto" && v.Value != "auto-delayed") { forget.Add(name); continue; }
                bool isListed = listed(name);
                StartMode now = StartMode.Other;
                if (isListed)
                {
                    try { now = mode(name); }
                    catch (InvalidOperationException) { continue; }
                    catch (Win32Exception) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    catch (System.Security.SecurityException) { continue; }
                    catch (ArgumentException) { continue; } // a name ServiceController refuses, e.g. over 80 characters
                }
                if (ShouldForget(ClassifyJournal(true, isListed, now != StartMode.Other, now))) forget.Add(name);
            }
            return forget;
        }

        /// <summary>
        /// Deletes the journal entries this app can no longer act on (ShouldForget): back on Automatic, Disabled by
        /// someone else, no longer listed, uninstalled, or malformed. Elevated only (returns 0 otherwise). Deletes
        /// values inside the journal key and nothing else: never the key, never a service change. Returns the count.
        /// </summary>
        public static int TidyJournal()
        {
            if (!Privileges.IsElevated()) return 0;
            lock (ServiceTrimmer.Operation)
            {
                int deleted = 0;
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(JournalPath, true))
                    {
                        if (key == null) return 0;
                        var values = new List<KeyValuePair<string, string>>();
                        foreach (string name in key.GetValueNames())
                            values.Add(new KeyValuePair<string, string>(name, key.GetValue(name) as string));
                        foreach (string name in NamesToForget(values, StartupKnowledge.CanDisableService, ModeForTidy))
                        {
                            key.DeleteValue(name, false);
                            deleted++;
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
                catch (IOException) { }
                return deleted;
            }
        }

        /// <summary>Other only when the service is not installed; any other read failure throws, so tidy keeps that record.</summary>
        static StartMode ModeForTidy(string name)
        {
            try { return ReadModeOrThrow(name); }
            catch (InvalidOperationException ex)
            {
                if (ServiceTrimmer.IsMissingService(ex)) return StartMode.Other;
                throw;
            }
        }

        /// <summary>"Access denied" from an elevated process means the service's own permissions say no, not missing rights.</summary>
        internal static StartupChangeKind DeniedKind(bool elevated)
        {
            return elevated ? StartupChangeKind.RefusedProtected : StartupChangeKind.RefusedNeedsAdmin;
        }

        /// <summary>
        /// How a re-enable went once its start type is Automatic again. The delayed start matters only when the record
        /// was "auto-delayed". When both it and the start now failed, the failed start is reported: the service not
        /// running now is what the user sees, and its note says it starts at the next restart.
        /// </summary>
        internal static StartupChangeKind ReenableKind(string recorded, bool delayRestored, bool started)
        {
            if (!started) return StartupChangeKind.TurnedOnStartFailed;
            if (recorded == "auto-delayed" && !delayRestored) return StartupChangeKind.TurnedOnDelayNotRestored;
            return StartupChangeKind.TurnedOn;
        }

        /// <summary>Sets a listed service to Manual and stops it. Needs administrator rights.</summary>
        public static StartupChange Disable(string name)
        {
            lock (ServiceTrimmer.Operation) return DisableLocked(name);
        }

        static StartupChange DisableLocked(string name)
        {
            if (!StartupKnowledge.CanDisableService(name)) return Change(StartupChangeKind.RefusedNotListed);
            StartMode before = ReadMode(name);
            if (before != StartMode.Automatic && before != StartMode.AutomaticDelayed) return Change(StartupChangeKind.RefusedNotAtStartup);
            bool journaled = false;
            try
            {
                // Record first, so a crash after the change still leaves the way back.
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(JournalPath))
                    key.SetValue(name, before == StartMode.AutomaticDelayed ? "auto-delayed" : "auto", RegistryValueKind.String);
                journaled = true;
                SetStartType(name, SERVICE_DEMAND_START);
            }
            catch (UnauthorizedAccessException) { if (journaled) ForgetJournal(name); return Change(DeniedKind(Privileges.IsElevated())); }
            catch (Win32Exception ex) { if (journaled) ForgetJournal(name); return Failed(ex); }

            // ControlService stops this service only; it refuses when a running service depends on it.
            int err = NativeService.StopOnly(name);
            if (err == NativeService.ErrorServiceNotActive) return Change(StartupChangeKind.TurnedOffWasNotRunning);
            if (err == NativeService.ErrorDependentServicesRunning) return Change(StartupChangeKind.TurnedOffStillRunningDependents);
            if (err != 0) return Change(StartupChangeKind.TurnedOffStopFailed);
            try
            {
                using (var sc = new ServiceController(name)) sc.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTrimmer.StopTimeout);
            }
            catch (InvalidOperationException) { return Change(StartupChangeKind.TurnedOffStopFailed); }
            catch (System.ServiceProcess.TimeoutException) { return Change(StartupChangeKind.TurnedOffStillStopping); }
            return Change(StartupChangeKind.TurnedOff);
        }

        /// <summary>Puts back the start type recorded by Disable. Only for services this app changed and that are still Manual.</summary>
        public static StartupChange Reenable(string name)
        {
            lock (ServiceTrimmer.Operation) return ReenableLocked(name);
        }

        static StartupChange ReenableLocked(string name)
        {
            string recorded;
            if (!ReadJournal().TryGetValue(name, out recorded)) return Change(StartupChangeKind.RefusedNotChangedByApp);
            if (!StartupKnowledge.CanDisableService(name)) return Change(StartupChangeKind.RefusedNotListed);
            StartMode now = ReadMode(name);
            if (now == StartMode.Automatic || now == StartMode.AutomaticDelayed)
            {
                ForgetJournal(name); // someone already turned it back on
                return Change(StartupChangeKind.AlreadyOn);
            }
            // Only undo this app's own change: a service someone has since Disabled is left exactly as it is.
            if (now != StartMode.Manual) return Change(StartupChangeKind.RefusedChangedElsewhere);
            try { SetStartType(name, SERVICE_AUTO_START); }
            catch (UnauthorizedAccessException) { return Change(DeniedKind(Privileges.IsElevated())); }
            catch (Win32Exception ex) { return Failed(ex); }
            // It starts with Windows from here, whatever follows, so the record goes now: a failed delay setting
            // must not leave a record that makes the row say it is still off.
            ForgetJournal(name);
            // "auto" asks for no delay, which clears a delay flag left over; a failure there changes nothing that matters.
            bool delayRestored = TrySetDelayed(name, recorded == "auto-delayed");
            bool started = true;
            try
            {
                using (var sc = new ServiceController(name))
                    if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            }
            catch (InvalidOperationException) { started = false; }
            return Change(ReenableKind(recorded, delayRestored, started));
        }

        static void ForgetJournal(string name)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(JournalPath, true))
                    if (key != null) key.DeleteValue(name, false);
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            catch (IOException) { }
        }

        /// <summary>Only well-formed records for listed services count; anything else in the key is ignored.</summary>
        static Dictionary<string, string> ReadJournal()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(JournalPath))
            {
                if (key == null) return map;
                foreach (string name in key.GetValueNames())
                {
                    string v = key.GetValue(name) as string;
                    if ((v == "auto" || v == "auto-delayed") && StartupKnowledge.CanDisableService(name)) map[name] = v;
                }
            }
            return map;
        }

        static StartupChange Change(StartupChangeKind kind) { return new StartupChange { Kind = kind }; }

        static StartupChange Failed(Win32Exception ex)
        {
            return new StartupChange { Kind = StartupChangeKind.Failed, Detail = ex.Message.TrimEnd('.') };
        }

        // ------------------------------------------------------------------ winsvc.h
        const uint SC_MANAGER_CONNECT = 0x0001, SERVICE_QUERY_CONFIG = 0x0001, SERVICE_CHANGE_CONFIG = 0x0002;
        const uint SERVICE_NO_CHANGE = 0xFFFFFFFF, SERVICE_AUTO_START = 2, SERVICE_DEMAND_START = 3;
        const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_DELAYED_AUTO_START_INFO { public int fDelayedAutostart; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManagerW(string machine, string database, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseServiceHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ChangeServiceConfigW(IntPtr service, uint type, uint start, uint errorControl, string binaryPath,
            string loadOrderGroup, IntPtr tagId, string dependencies, string startName, string password, string displayName);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ChangeServiceConfig2W(IntPtr service, uint level, ref SERVICE_DELAYED_AUTO_START_INFO info);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryServiceConfig2W(IntPtr service, uint level, out SERVICE_DELAYED_AUTO_START_INFO info, int size, out int needed);

        static IntPtr Open(string name, uint access, out IntPtr scm)
        {
            scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr svc = OpenServiceW(scm, name, access);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                CloseServiceHandle(scm);
                scm = IntPtr.Zero; // closed here, so a caller's finally must not close it again
                if (err == 5) throw new UnauthorizedAccessException();
                throw new Win32Exception(err);
            }
            return svc;
        }

        /// <summary>Whether this process may change the service's startup: opens it for SERVICE_CHANGE_CONFIG and closes it.</summary>
        static bool CanChangeConfig(string name)
        {
            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
            try { svc = Open(name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG, out scm); return true; }
            catch (UnauthorizedAccessException) { return false; }
            catch (Win32Exception) { return false; }
            finally
            {
                if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                if (scm != IntPtr.Zero) CloseServiceHandle(scm);
            }
        }

        static bool IsDelayed(string name)
        {
            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
            try
            {
                svc = Open(name, SERVICE_QUERY_CONFIG, out scm);
                SERVICE_DELAYED_AUTO_START_INFO info; int needed;
                return QueryServiceConfig2W(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, out info, Marshal.SizeOf(typeof(SERVICE_DELAYED_AUTO_START_INFO)), out needed)
                    && info.fDelayedAutostart != 0;
            }
            catch (Win32Exception) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally
            {
                if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                if (scm != IntPtr.Zero) CloseServiceHandle(scm);
            }
        }

        /// <summary>Sets the start type (ChangeServiceConfigW). Throws UnauthorizedAccessException or Win32Exception.</summary>
        static void SetStartType(string name, uint start)
        {
            IntPtr scm;
            IntPtr svc = Open(name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG, out scm);
            try
            {
                if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                    throw Error();
            }
            finally
            {
                CloseServiceHandle(svc);
                CloseServiceHandle(scm);
            }
        }

        /// <summary>
        /// Sets or clears the delayed automatic start (ChangeServiceConfig2W). False when it could not be set; it never
        /// throws, because the start type is already changed by then and that change stands.
        /// </summary>
        static bool TrySetDelayed(string name, bool delayed)
        {
            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
            try
            {
                svc = Open(name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG, out scm);
                var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed ? 1 : 0 };
                return ChangeServiceConfig2W(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info);
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (Win32Exception) { return false; }
            finally
            {
                if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                if (scm != IntPtr.Zero) CloseServiceHandle(scm);
            }
        }

        static Exception Error()
        {
            int err = Marshal.GetLastWin32Error();
            return err == 5 ? (Exception)new UnauthorizedAccessException() : new Win32Exception(err);
        }
    }
}
