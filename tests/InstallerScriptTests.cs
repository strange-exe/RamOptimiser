// The setup script, checked in source. The installer is only compiled by site\scripts\package.ps1 and is never run
// here, so these checks hold the security choices in installer\RamOptimiser.iss in place: an admin install into
// Program Files that the user cannot redirect, the app started afterwards without admin rights, and an uninstall
// that removes only this copy's logon tasks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class InstallerScriptTests
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
            string text = Script();
            Check("installer: the script exists and has a [Setup] section", text.Contains("[Setup]\n"), text.Length + " chars");
            Dictionary<string, List<string>> sections = Sections(text);
            EncodingTests(text);
            SetupTests(sections);
            FilesTests(sections);
            RunTests(sections);
            UninstallTests(sections, text);
            return failed;
        }

        static string Script()
        {
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine("..", Path.Combine("installer", "RamOptimiser.iss"))));
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        // ---------------------------------------------------------------- parsing

        // Section name (lower case, with brackets) -> its lines, without blank lines and ';' comments. The [Code]
        // section keeps every line, since Pascal has its own comment syntax.
        static Dictionary<string, List<string>> Sections(string text)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string> current = null;
            string currentName = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    currentName = line.ToLowerInvariant();
                    if (!result.TryGetValue(currentName, out current)) result[currentName] = current = new List<string>();
                    continue;
                }
                if (current == null) continue;
                if (currentName != "[code]" && (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))) continue;
                current.Add(line);
            }
            return result;
        }

        static List<string> Lines(Dictionary<string, List<string>> sections, string name)
        {
            List<string> lines;
            return sections.TryGetValue(name, out lines) ? lines : new List<string>();
        }

        // A [Setup] directive's value, or null when it is not set. Directive names are case-insensitive.
        static string Directive(Dictionary<string, List<string>> sections, string name)
        {
            string found = null;
            foreach (string line in Lines(sections, "[setup]"))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), name, StringComparison.OrdinalIgnoreCase))
                    found = line.Substring(eq + 1).Trim();
            }
            return found;
        }

        // One entry line ("Name: value; Name: "quoted; value"") as parameter -> value, quotes removed ("" is ").
        static Dictionary<string, string> Entry(string line)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<string>();
            var part = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') quoted = !quoted;
                if (c == ';' && !quoted) { parts.Add(part.ToString()); part.Length = 0; continue; }
                part.Append(c);
            }
            parts.Add(part.ToString());
            foreach (string p in parts)
            {
                int colon = p.IndexOf(':');
                if (colon <= 0) continue;
                string value = p.Substring(colon + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    value = value.Substring(1, value.Length - 2).Replace("\"\"", "\"");
                result[p.Substring(0, colon).Trim()] = value;
            }
            return result;
        }

        static string Get(Dictionary<string, string> entry, string name)
        {
            string value;
            return entry.TryGetValue(name, out value) ? value : "";
        }

        static bool HasFlag(Dictionary<string, string> entry, string flag)
        {
            return Array.IndexOf(Get(entry, "Flags").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                flag.ToLowerInvariant()) >= 0;
        }

        // The body of a Pascal function or procedure: from its header to the "end;" at the start of a line.
        static string Routine(string text, string header)
        {
            int start = text.IndexOf(header, StringComparison.Ordinal);
            if (start < 0) return "";
            int end = text.IndexOf("\nend;", start, StringComparison.Ordinal);
            return end < 0 ? "" : text.Substring(start, end - start);
        }

        static int Count(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        // ---------------------------------------------------------------- the file itself

        static void EncodingTests(string text)
        {
            bool ascii = true, cr = false;
            foreach (char c in text)
            {
                if (c > 0x7E || (c < 0x20 && c != '\n' && c != '\t')) ascii = false;
                if (c == '\r') cr = true;
            }
            Check("installer: plain ASCII with LF line endings", ascii && !cr, cr ? "has CR" : "non-ASCII or control character");
            string lower = text.ToLowerInvariant();
            Check("installer: unsigned, with no signing tool and no secrets in the script",
                !lower.Contains("signtool") && !lower.Contains("signeduninstaller") && !lower.Contains("password")
                && !lower.Contains("encryption"), "signing or password directive found");
            Check("installer: every web link is HTTPS", !lower.Contains("http://"));
        }

        // ---------------------------------------------------------------- [Setup]

        static void SetupTests(Dictionary<string, List<string>> s)
        {
            Check("setup: always runs as administrator", Directive(s, "PrivilegesRequired") == "admin", Directive(s, "PrivilegesRequired"));
            Check("setup: no per-user install mode can be chosen", Directive(s, "PrivilegesRequiredOverridesAllowed") == null,
                Directive(s, "PrivilegesRequiredOverridesAllowed"));
            string dir = Directive(s, "DefaultDirName") ?? "";
            Check("setup: installs under Program Files", dir.StartsWith("{autopf}\\", StringComparison.Ordinal) && dir.IndexOf('{', 1) < 0, dir);
            Check("setup: the folder cannot be changed in the wizard", Directive(s, "DisableDirPage") == "yes", Directive(s, "DisableDirPage"));
            Check("setup: a folder recorded by an earlier install is not reused", Directive(s, "UsePreviousAppDir") == "no",
                Directive(s, "UsePreviousAppDir"));
            Check("setup: x64 only, in 64-bit install mode", Directive(s, "ArchitecturesAllowed") == "x64compatible"
                && Directive(s, "ArchitecturesInstallIn64BitMode") == "x64compatible",
                Directive(s, "ArchitecturesAllowed") + " / " + Directive(s, "ArchitecturesInstallIn64BitMode"));
            Check("setup: closes a running copy and does not restart it as administrator",
                Directive(s, "CloseApplications") == "yes" && Directive(s, "RestartApplications") == "no",
                Directive(s, "CloseApplications") + " / " + Directive(s, "RestartApplications"));
            Check("setup: Windows 10 or later", Directive(s, "MinVersion") == "10.0", Directive(s, "MinVersion"));
            string appId = Directive(s, "AppId") ?? "";
            Check("setup: a fixed AppId GUID", Regex.IsMatch(appId, "^\\{\\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\\}$"), appId);
            Check("setup: version and output name come from the build", Directive(s, "AppVersion") == "{#AppVersion}"
                && Directive(s, "OutputBaseFilename") == "RamOptimiser-{#AppVersion}-setup", Directive(s, "OutputBaseFilename"));
            Check("setup: uninstall entry shows the app's icon", Directive(s, "UninstallDisplayIcon") == "{app}\\RamOptimiser.exe",
                Directive(s, "UninstallDisplayIcon"));
        }

        // ---------------------------------------------------------------- [Files], [Icons], [Tasks]

        static void FilesTests(Dictionary<string, List<string>> s)
        {
            var sources = new List<string>();
            bool allIntoApp = true, allIgnoreVersion = true;
            foreach (string line in Lines(s, "[files]"))
            {
                Dictionary<string, string> e = Entry(line);
                sources.Add(Get(e, "Source"));
                allIntoApp &= Get(e, "DestDir") == "{app}";
                allIgnoreVersion &= HasFlag(e, "ignoreversion");
            }
            Check("files: the two exes, the README and the LICENSE, nothing else", sources.Count == 4
                && sources.Contains("{#BinDir}\\RamOptimiser.exe") && sources.Contains("{#BinDir}\\ramopt.exe")
                && sources.Contains("{#RootDir}\\README.md") && sources.Contains("{#RootDir}\\LICENSE"), string.Join(", ", sources.ToArray()));
            Check("files: all into the install folder, replaced whatever their version", allIntoApp && allIgnoreVersion);

            bool startMenu = false, desktopOptional = false;
            foreach (string line in Lines(s, "[icons]"))
            {
                Dictionary<string, string> e = Entry(line);
                if (Get(e, "Name").StartsWith("{autoprograms}\\", StringComparison.Ordinal) && Get(e, "Filename") == "{app}\\RamOptimiser.exe"
                    && Get(e, "Tasks") == "") startMenu = true;
                if (Get(e, "Name").StartsWith("{autodesktop}\\", StringComparison.Ordinal) && Get(e, "Tasks") == "desktopicon") desktopOptional = true;
            }
            bool unchecked_ = false;
            foreach (string line in Lines(s, "[tasks]"))
            {
                Dictionary<string, string> e = Entry(line);
                if (Get(e, "Name") == "desktopicon" && HasFlag(e, "unchecked")) unchecked_ = true;
            }
            Check("icons: a Start menu shortcut, and a desktop one only when ticked (off by default)", startMenu && desktopOptional && unchecked_);
        }

        // ---------------------------------------------------------------- [Run]

        static void RunTests(Dictionary<string, List<string>> s)
        {
            List<string> lines = Lines(s, "[run]");
            bool allSafe = lines.Count > 0;
            foreach (string line in lines)
            {
                Dictionary<string, string> e = Entry(line);
                allSafe &= HasFlag(e, "postinstall") && HasFlag(e, "runasoriginaluser") && !HasFlag(e, "runascurrentuser")
                    && !HasFlag(e, "shellexec") && HasFlag(e, "skipifsilent") && HasFlag(e, "nowait");
            }
            Check("run: the app starts only from the finish page's tick box, as the user who started setup", allSafe,
                string.Join(" | ", lines.ToArray()));
            Check("run: it starts the installed app", lines.Count == 1 && Get(Entry(lines[0]), "Filename") == "{app}\\RamOptimiser.exe",
                lines.Count + " entries");
        }

        // ---------------------------------------------------------------- uninstall

        static void UninstallTests(Dictionary<string, List<string>> s, string text)
        {
            List<string> lines = Lines(s, "[uninstallrun]");
            Dictionary<string, string> e = lines.Count == 1 ? Entry(lines[0]) : new Dictionary<string, string>();
            Check("uninstall run: one entry, run once however often the app was installed", lines.Count == 1 && Get(e, "RunOnceId").Length > 0,
                lines.Count + " entries");
            Check("uninstall run: the 64-bit System32 PowerShell by absolute path", Get(e, "Filename") == "{sys}\\WindowsPowerShell\\v1.0\\powershell.exe",
                Get(e, "Filename"));
            Check("uninstall run: hidden, with parameters built in [Code]", HasFlag(e, "runhidden") && Get(e, "Parameters") == "{code:TaskCleanupParams}",
                Get(e, "Parameters"));

            string p = Routine(text, "function TaskCleanupParams(");
            Check("task cleanup: no profile, never interactive", p.Contains("-NoProfile -NonInteractive"), p.Length + " chars");
            Check("task cleanup: compares with this install's exe, quotes doubled for PowerShell",
                p.Contains("ExpandConstant('{app}\\RamOptimiser.exe')") && p.Contains("StringChangeEx("), "");
            Check("task cleanup: only root-folder tasks", p.Contains("GetFolder(''\\'')") && !p.Contains("GetFolders"), "");
            Check("task cleanup: only the app's task names", p.Contains("-ne ''" + StartupTask.LegacyTaskName + "''")
                && p.Contains("-notmatch ''^" + StartupTask.TaskNamePrefix + "S-1-[0-9-]+$''"), "");
            Check("task cleanup: one exec action whose path is exactly this exe", p.Contains("$a.Count -eq 1")
                && p.Contains("$a[0].Type -eq 0") && p.Contains("$a[0].Path.Trim([char]34) -eq $app"), "");
            Check("task cleanup: deletes in one place only", Count(p, "DeleteTask") == 1, Count(p, "DeleteTask") + " calls");
            Check("task cleanup: the script has no double quote of its own (only the two around -Command)", Count(p, "\"") == 2,
                Count(p, "\"") + " double quotes");

            string init = Routine(text, "function InitializeUninstall:");
            string names = Routine(text, "function AppMutexes:");
            Check("uninstall: asks to close the app while any account's copy runs (its mutex prefix)",
                init.Contains("CheckForMutexes(AppMutexes)") && names.Contains("'" + SingleInstance.NamePrefix + "'"),
                init.Length + " chars");
            Check("uninstall: mutex names only from well-formed SIDs in the profile list", names.Contains("ProfileList")
                && names.Contains("'S-1-'") && names.Contains("Ok := False"), names.Length + " chars");
            Check("uninstall: a silent uninstall stops rather than waits", init.Contains("SuppressibleMsgBox(") && init.Contains("IDCANCEL"), "");

            string post = Routine(text, "procedure CurUninstallStepChanged(");
            Check("uninstall: removes the app's machine key", post.Contains("usPostUninstall")
                && post.Contains("RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\\" + AppRegistry.RootName + "')"), post.Length + " chars");
            Check("uninstall: per-user settings are kept", !text.ToLowerInvariant().Contains("{localappdata}")
                && !text.Contains("[UninstallDelete]"), "");

            string prepare = Routine(text, "function PrepareToInstall(");
            Check("setup: refuses any folder but its own under Program Files, /DIR= included",
                prepare.Contains("ExpandConstant('{app}')") && prepare.Contains("ExpandConstant('{autopf}\\RAM Optimiser')")
                && prepare.Contains("CompareText("), prepare.Length + " chars");
        }
    }
}
