// Package 4B (E19, CLI only): per-process trim that spares the app in front and a user list (ramopt clean/watch
// --trim each), and ramopt top [N] [--by private|ws] [--json]. Fakes only: no test here opens, trims or lists a
// real process for trimming; the process list and the trim call are passed in.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RamOptimiser.Tests
{
    public static class ProcessTrimmerTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        static void Throws<T>(string name, Action a) where T : Exception
        {
            try { a(); Check(name, false, "no exception"); }
            catch (T) { Check(name, true); }
            catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
        }

        static string Message<T>(Action a) where T : Exception
        {
            try { a(); return null; }
            catch (T ex) { return ex.Message; }
        }

        /// <summary>True when s has no control character but CR and LF, no format character and no unpaired surrogate.</summary>
        static bool Clean(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r' || c == '\n') continue;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                    if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(s, i) == System.Globalization.UnicodeCategory.Format) return false;
                    i++;
                    continue;
                }
                if (char.IsLowSurrogate(c) || char.IsControl(c)
                    || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) return false;
            }
            return true;
        }

        static string Codes(string s)
        {
            if (s == null) return "null";
            var parts = new List<string>();
            foreach (char c in s) parts.Add(c >= 0x20 && c < 0x7F ? c.ToString() : "<" + ((int)c).ToString("X4") + ">");
            return string.Join("", parts.ToArray());
        }

        /// <summary>
        /// Guard: the app and ramopt may run elevated, and per-process trim must stay limited to what an ordinary
        /// administrator token can open. Nothing may enable SeDebugPrivilege or call Process.EnterDebugMode.
        /// </summary>
        static void DebugPrivilegeGuard()
        {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("..", "src")));
            var hits = new List<string>();
            foreach (string f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(f);
                if (text.Contains("\"SeDebugPrivilege\"") || text.Contains("EnterDebugMode") || text.Contains("SE_DEBUG_NAME"))
                    hits.Add(Path.GetFileName(f));
            }
            Check("guard: nothing enables SeDebugPrivilege", Directory.Exists(root) && hits.Count == 0, string.Join(", ", hits.ToArray()));
            Check("guard: per-process trim opens with set-quota and limited query only",
                File.ReadAllText(Path.Combine(root, "ProcessTrimmer.cs")).Contains(
                    "Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid)"));
        }

        public static int Run()
        {
            PlanTests();
            ExclusionTests();
            SummaryTests();
            TrimEachTests();
            TopTests();
            OptionTests();
            CliTests();
            DebugPrivilegeGuard();
            return failed;
        }

        const int Self = 999;

        static ProcessSample P(int id, string name)
        {
            return new ProcessSample { Id = id, Name = name };
        }

        static ProcessSample P(int id, string name, ulong ws, ulong priv)
        {
            return new ProcessSample { Id = id, Name = name, WorkingSet = ws, Private = priv };
        }

        static SpareReason ReasonFor(List<KeyValuePair<ProcessSample, SpareReason>> plan, int id)
        {
            foreach (var kv in plan) if (kv.Key.Id == id) return kv.Value;
            throw new InvalidOperationException("pid " + id + " not in the plan");
        }

        static List<ProcessSample> Desktop()
        {
            return new List<ProcessSample> {
                P(0, "Idle"), P(4, "System"), P(120, "Registry"), P(130, "Secure System"), P(140, "Memory Compression"),
                P(10, "msedge"), P(11, "msedge"), P(12, "MSEDGE"), P(20, "obs64"), P(30, "notepad"), P(Self, "ramopt")
            };
        }

        // ---------------------------------------------------------------- plan

        static void PlanTests()
        {
            var plan = ProcessTrimmer.Plan(Desktop(), new List<string>(), 11, Self);
            Check("trim plan: the foreground app is spared with all its processes",
                ReasonFor(plan, 10) == SpareReason.Foreground && ReasonFor(plan, 11) == SpareReason.Foreground
                && ReasonFor(plan, 12) == SpareReason.Foreground,
                ReasonFor(plan, 10) + " " + ReasonFor(plan, 11) + " " + ReasonFor(plan, 12));
            Check("trim plan: other apps are trimmed", ReasonFor(plan, 20) == SpareReason.None && ReasonFor(plan, 30) == SpareReason.None);
            Check("trim plan: every process is in the plan once", plan.Count == Desktop().Count, plan.Count.ToString());

            var withExe = ProcessTrimmer.Plan(Desktop(), new List<string> { "OBS64.EXE" }, 0, Self);
            var bare = ProcessTrimmer.Plan(Desktop(), new List<string> { "obs64" }, 0, Self);
            var exeOnProcess = ProcessTrimmer.Plan(new List<ProcessSample> { P(50, "Game.exe") }, new List<string> { "game" }, 0, Self);
            Check("trim plan: exclusion matches with or without .exe, any case",
                ReasonFor(withExe, 20) == SpareReason.Excluded && ReasonFor(bare, 20) == SpareReason.Excluded
                && ReasonFor(exeOnProcess, 50) == SpareReason.Excluded && ReasonFor(withExe, 30) == SpareReason.None);

            Check("trim plan: self, Idle, System, Registry and Memory Compression are spared",
                ReasonFor(plan, Self) == SpareReason.Self && ReasonFor(plan, 0) == SpareReason.System
                && ReasonFor(plan, 4) == SpareReason.System && ReasonFor(plan, 120) == SpareReason.System
                && ReasonFor(plan, 130) == SpareReason.System && ReasonFor(plan, 140) == SpareReason.System);
            var renamed = ProcessTrimmer.Plan(new List<ProcessSample> { P(0, "x"), P(4, "y"), P(77, "memory compression") },
                new List<string>(), 0, Self);
            Check("trim plan: pid 0 and 4 are spared whatever their name; names compare in any case",
                ReasonFor(renamed, 0) == SpareReason.System && ReasonFor(renamed, 4) == SpareReason.System
                && ReasonFor(renamed, 77) == SpareReason.System);

            var none = ProcessTrimmer.Plan(Desktop(), new List<string>(), 0, Self);
            var gone = ProcessTrimmer.Plan(Desktop(), new List<string>(), 4242, Self);
            Check("trim plan: no foreground spares nothing for that reason",
                ReasonFor(none, 10) == SpareReason.None && ReasonFor(none, 11) == SpareReason.None
                && ReasonFor(gone, 11) == SpareReason.None);
            var fgSelf = ProcessTrimmer.Plan(Desktop(), new List<string>(), Self, Self);
            Check("trim plan: self wins over foreground", ReasonFor(fgSelf, Self) == SpareReason.Self);
            var nullList = ProcessTrimmer.Plan(Desktop(), null, 11, Self);
            Check("trim plan: a null exclusion list is empty", ReasonFor(nullList, 20) == SpareReason.None);
        }

        // ---------------------------------------------------------------- exclusions

        static void ExclusionTests()
        {
            List<string> path = ProcessTrimmer.ParseExclusions("C:\\tools\\obs64.exe,obs64.exe,..\\x,a/b");
            Check("trim exclusions: a path is dropped", path.Count == 1 && path[0] == "obs64", string.Join("|", path));
            List<string> wild = ProcessTrimmer.ParseExclusions("ms*.exe,game?.exe,notepad");
            Check("trim exclusions: a wildcard is dropped", wild.Count == 1 && wild[0] == "notepad", string.Join("|", wild));
            var many = new List<string>();
            for (int i = 1; i <= 33; i++) many.Add("app" + i + ".exe");
            List<string> capped = ProcessTrimmer.ParseExclusions(string.Join(",", many));
            Check("trim exclusions: the 33rd entry is dropped", capped.Count == 32 && capped[31] == "app32" && !capped.Contains("app33"),
                capped.Count.ToString());
            List<string> dup = ProcessTrimmer.ParseExclusions("obs64.exe, OBS64 ,obs64.EXE,steam");
            Check("trim exclusions: duplicates are removed", dup.Count == 2 && dup[0] == "obs64" && dup[1] == "steam", string.Join("|", dup));
            Check("trim exclusions: quotes, empties and null are dropped",
                ProcessTrimmer.ParseExclusions("\"obs64\",,  ,'x'").Count == 0 && ProcessTrimmer.ParseExclusions(null).Count == 0);
            Check("trim exclusions: spaces, dots, brackets, plus and dashes are kept",
                ProcessTrimmer.ParseExclusions("Secure System,my-app (x86)+.exe")[1] == "my-app (x86)+");
            Check("trim exclusions: a name over 100 characters is dropped", ProcessTrimmer.ParseExclusions(new string('a', 101)).Count == 0
                && ProcessTrimmer.ParseExclusions(new string('a', 100)).Count == 1);
            Check("trim exclusions: the first invalid entry is named", ProcessTrimmer.FirstInvalidExclusion("obs64,C:\\x.exe,*") == "C:\\x.exe"
                && ProcessTrimmer.FirstInvalidExclusion("obs64, steam.exe") == null);
        }

        // ---------------------------------------------------------------- summary

        static void SummaryTests()
        {
            string s = ProcessTrimmer.Summarise(142, new List<string> { "msedge" }, new List<string> { "obs64" }, 9);
            Check("trim summary: names the spared groups and the refusal count",
                s == "trimmed 142 processes; spared msedge (in front) and obs64 (your list); 9 refused", s);
            string one = ProcessTrimmer.Summarise(1, new List<string>(), new List<string>(), 0);
            Check("trim summary: one process, nothing spared, nothing refused", one == "trimmed 1 process", one);
            string list = ProcessTrimmer.Summarise(3, new List<string>(), new List<string> { "obs64", "steam" }, 1);
            Check("trim summary: several names in one group", list == "trimmed 3 processes; spared obs64, steam (your list); 1 refused", list);
        }

        // ---------------------------------------------------------------- act

        static void TrimEachTests()
        {
            var tried = new List<int>();
            var r = new AreaResult { Area = Areas.WorkingSets };
            var req = new CleanRequest { Trim = TrimMode.EachProcess, TrimExclusions = new List<string> { "obs64" } };
            ProcessTrimmer.TrimEach(req, r, Desktop(), 11, Self, pid => { tried.Add(pid); return pid != 30; }, () => CleanStop.None);
            Check("trim each: only planned processes are trimmed", tried.Count == 1 && tried[0] == 30, string.Join(",", tried));
            Check("trim each: all refused is a failure", !r.Ok && r.Outcome == AreaOutcome.Failed, r.Message);
            Check("trim each: the message names the spared apps",
                r.Message == "trimmed 0 processes; spared msedge (in front) and obs64 (your list); 1 refused", r.Message);

            // The app in front is named by whoever named its exe: its name reaches the terminal and cleans.log cleaned.
            string hostile = (char)0x202E + "evil" + (char)27 + "[2J" + char.ConvertFromUtf32(0xE0041) + (char)0xD800;
            var rh = new AreaResult { Area = Areas.WorkingSets };
            ProcessTrimmer.TrimEach(new CleanRequest { Trim = TrimMode.EachProcess }, rh,
                new List<ProcessSample> { P(50, hostile), P(51, "other") }, 50, Self, pid => true, () => CleanStop.None);
            Check("trim each: the spared app's name is cleaned", rh.Message == "trimmed 1 process; spared evil[2J (in front)", Codes(rh.Message));
            var rn = new AreaResult { Area = Areas.WorkingSets };
            ProcessTrimmer.TrimEach(new CleanRequest { Trim = TrimMode.EachProcess }, rn,
                new List<ProcessSample> { P(60, ((char)0x200B).ToString() + "x"), P(61, "other") }, 60, Self, pid => true, () => CleanStop.None);
            Check("trim each: a name that is only invisible characters reads (no name)",
                ProcessTrimmer.Summarise(0, new List<string> { SafeText.Name(((char)0x200B).ToString(), 64, "...") }, null, 0)
                == "trimmed 0 processes; spared (no name) (in front)" && rn.Message == "trimmed 1 process; spared x (in front)", Codes(rn.Message));

            var procs = new List<ProcessSample> { P(Self, "ramopt") };
            for (int i = 1; i <= 5; i++) procs.Add(P(100 + i, "app" + i));
            var r2 = new AreaResult { Area = Areas.WorkingSets };
            ProcessTrimmer.TrimEach(new CleanRequest { Trim = TrimMode.EachProcess }, r2, procs, 0, Self, pid =>
            {
                if (pid == 102) throw new InvalidOperationException("boom");
                return pid != 101;
            }, () => CleanStop.None);
            Check("trim each: some refused is still ok, and a throw counts as refused",
                r2.Ok && r2.Outcome == AreaOutcome.Ok && r2.Message == "trimmed 3 processes; 2 refused", r2.Message);

            var r3 = new AreaResult { Area = Areas.WorkingSets };
            var noFg = new CleanRequest { Trim = TrimMode.EachProcess, SpareForeground = false };
            var tried3 = new List<int>();
            ProcessTrimmer.TrimEach(noFg, r3, Desktop(), 11, Self, pid => { tried3.Add(pid); return true; }, () => CleanStop.None);
            Check("trim each: --no-spare-foreground trims the app in front", tried3.Contains(11) && tried3.Contains(10)
                && !tried3.Contains(Self) && !tried3.Contains(4), string.Join(",", tried3));

            var big = new List<ProcessSample>();
            for (int i = 0; i < 70; i++) big.Add(P(1000 + i, "p" + i));
            int checks = 0, trims = 0;
            var r4 = new AreaResult { Area = Areas.WorkingSets };
            ProcessTrimmer.TrimEach(new CleanRequest { Trim = TrimMode.EachProcess }, r4, big, 0, Self, pid => { trims++; return true; },
                () => { checks++; return checks >= 1 ? CleanStop.Cancelled : CleanStop.None; });
            Check("trim each: the stop is checked every 32 processes", trims == 32 && checks == 1, trims + " trims, " + checks + " checks");
            Check("trim each: a stop notes the processes left", r4.Ok && r4.Message == "trimmed 32 processes; 38 not trimmed: stopped", r4.Message);

            var r5 = new AreaResult { Area = Areas.WorkingSets };
            ProcessTrimmer.TrimEach(new CleanRequest { Trim = TrimMode.EachProcess }, r5, new List<ProcessSample> { P(Self, "ramopt") },
                0, Self, pid => true, () => CleanStop.None);
            Check("trim each: nothing to trim is not a failure", r5.Ok && r5.Message == "trimmed 0 processes", r5.Message);

            Check("trim each: the clean routes working sets to it only in each-process mode",
                Cleaner.TrimsEachProcess(new CleanRequest { Trim = TrimMode.EachProcess }, Areas.WorkingSets)
                && !Cleaner.TrimsEachProcess(new CleanRequest(), Areas.WorkingSets)
                && !Cleaner.TrimsEachProcess(new CleanRequest { Trim = TrimMode.EachProcess }, Areas.ModifiedList));
            var defaults = new CleanRequest();
            Check("trim each: a request defaults to system-wide trim and spares the foreground",
                defaults.Trim == TrimMode.System && defaults.SpareForeground);
        }

        // ---------------------------------------------------------------- top

        static void TopTests()
        {
            var samples = new List<ProcessSample> {
                P(1, "msedge", 1000, 300), P(2, "MSEDGE", 1000, 300), P(3, "code", 900, 600), P(4, "beta", 50, 600),
                P(5, "Idle", 99999, 99999), P(6, "", 5, 5), P(7, "tiny", 1, 1)
            };
            List<AppMemory> byPrivate = AppMemoryList.Aggregate(samples, 3, AppSort.Private);
            Check("top: sorts by private by default, ties by name", byPrivate.Count == 3 && byPrivate[0].Name == "beta"
                && byPrivate[1].Name == "code" && byPrivate[2].Name == "msedge" && byPrivate[2].PrivateBytes == 600
                && byPrivate[2].Bytes == 2000 && byPrivate[2].Processes == 2,
                byPrivate.Count > 0 ? byPrivate[0].Name + " " + byPrivate[0].PrivateBytes : "empty");
            List<AppMemory> byWs = AppMemoryList.Aggregate(samples, 10, AppSort.WorkingSet);
            Check("top: --by ws sorts by working set", byWs.Count == 4 && byWs[0].Name == "msedge" && byWs[1].Name == "code"
                && byWs[2].Name == "beta" && byWs[3].Name == "tiny", byWs.Count.ToString());
            Check("top: the default sort is private", new Options().TopBy == AppSort.Private && new Options().TopCount == 10);

            var rows = new List<AppMemory> {
                new AppMemory { Name = "msedge", Processes = 24, PrivateBytes = (ulong)(1.9 * (1L << 30)), Bytes = (ulong)(2.1 * (1L << 30)) },
                new AppMemory { Name = "Code", Processes = 11, PrivateBytes = (ulong)(812.0 * (1L << 20)), Bytes = (ulong)(950.3 * (1L << 20)) },
                new AppMemory { Name = "a-very-long-process-name-indeed", Processes = 1, PrivateBytes = 1, Bytes = 2 }
            };
            var w = new StringWriter();
            Program.PrintTop(w, rows);
            string[] lines = w.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Check("top: header", lines[0] == "App                    Processes    Private   Working set", lines[0]);
            Check("top: rows line up", lines[1] == "msedge                        24     1.9 GB        2.1 GB"
                && lines[2] == "Code                          11   812.0 MB      950.3 MB", lines[1] + "|" + lines[2]);
            Check("top: a long name is cut to keep the columns", lines[3].StartsWith("a-very-long-process..  ")
                && lines[3].Length == lines[1].Length, lines[3]);
            Check("top: the footer explains the columns", lines[lines.Length - 1]
                == "Private: memory only that app uses. Working set: RAM it holds now, shared pages included; it drops right after a clean.",
                lines[lines.Length - 1]);
            // Names are chosen by whoever names an exe: no escape sequence, bidi override or tag character reaches the
            // terminal, and a cut never splits a surrogate pair.
            string hostile = (char)27 + "]0;owned" + (char)7 + (char)0x202E + "app" + char.ConvertFromUtf32(0xE0041) + (char)0xDC00;
            string pairAtCut = new string('x', 18) + char.ConvertFromUtf32(0x1F600) + "yyyy";
            var odd = new StringWriter();
            Program.PrintTop(odd, new List<AppMemory> {
                new AppMemory { Name = hostile, Processes = 1, PrivateBytes = 1, Bytes = 1 },
                new AppMemory { Name = pairAtCut, Processes = 1, PrivateBytes = 1, Bytes = 1 } });
            string[] oddLines = odd.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Check("top: a hostile name is printed cleaned", oddLines.Length > 2 && oddLines[1].StartsWith("]0;ownedapp   ")
                && Clean(odd.ToString()), oddLines.Length > 1 ? Codes(oddLines[1]) : odd.ToString());
            Check("top: a cut never splits a surrogate pair", oddLines.Length > 2 && oddLines[2].StartsWith(new string('x', 18) + "..")
                && Clean(oddLines[2]), oddLines.Length > 2 ? Codes(oddLines[2]) : "");

            var empty = new StringWriter();
            Program.PrintTop(empty, new List<AppMemory>());
            Check("top: an empty list says so", empty.ToString().Contains("No apps could be read."), empty.ToString());

            DateTime t0 = new DateTime(2026, 11, 18, 9, 5, 0, DateTimeKind.Utc);
            string json = Program.TopJson(new List<AppMemory> { new AppMemory { Name = "ms\"edge", Processes = 2, PrivateBytes = 600, Bytes = 2000 } },
                AppSort.Private, t0);
            Check("top: --json shape", json == "{\"v\":1,\"type\":\"top\",\"time\":\"" + CleanLog.Time(t0) + "\",\"by\":\"private\","
                + "\"apps\":[{\"name\":\"ms\\\"edge\",\"processes\":2,\"private\":600,\"workingSet\":2000}]}", json);
            string wsJson = Program.TopJson(new List<AppMemory>(), AppSort.WorkingSet, t0);
            Check("top: --json by ws, no apps", wsJson.Contains("\"by\":\"ws\"") && wsJson.EndsWith("\"apps\":[]}"), wsJson);
        }

        // ---------------------------------------------------------------- options

        static void OptionTests()
        {
            Options top = Options.Parse(new[] { "top" });
            Check("options: top alone", top.Command == Command.Top && top.TopCount == 10 && top.TopBy == AppSort.Private && !top.Json);
            Options t5 = Options.Parse(new[] { "top", "5", "--by", "ws", "--json" });
            Check("options: top takes N 1-100", t5.TopCount == 5 && t5.TopBy == AppSort.WorkingSet && t5.Json
                && Options.Parse(new[] { "top", "100" }).TopCount == 100 && Options.Parse(new[] { "top", "1" }).TopCount == 1);
            Check("options: top --by private", Options.Parse(new[] { "TOP", "--by", "PRIVATE" }).TopBy == AppSort.Private);
            Throws<ArgumentException>("options: top 0 is rejected", () => Options.Parse(new[] { "top", "0" }));
            Throws<ArgumentException>("options: top 101 is rejected", () => Options.Parse(new[] { "top", "101" }));
            Throws<ArgumentException>("options: top -5 is rejected", () => Options.Parse(new[] { "top", "-5" }));
            Throws<ArgumentException>("options: top N twice is rejected", () => Options.Parse(new[] { "top", "5", "6" }));
            Throws<ArgumentException>("options: top --by other is rejected", () => Options.Parse(new[] { "top", "--by", "cpu" }));
            Throws<ArgumentException>("options: top refuses clean options", () => Options.Parse(new[] { "top", "--all" }));
            Throws<ArgumentException>("options: --by works only with top", () => Options.Parse(new[] { "status", "--by", "ws" }));
            Throws<ArgumentException>("options: a number works only with top", () => Options.Parse(new[] { "clean", "5" }));
            string n0 = Message<ArgumentException>(() => Options.Parse(new[] { "top", "0" }));
            Check("options: top N message", n0 == "top N must be a whole number from 1 to 100.", n0);

            Options each = Options.Parse(new[] { "clean", "--trim", "each", "--spare", "obs64.exe,Steam", "--no-spare-foreground" });
            Check("options: --trim each parses", each.TrimMode == TrimMode.EachProcess && !each.SpareForeground
                && each.Spare.Count == 2 && each.Spare[0] == "obs64" && each.Spare[1] == "Steam");
            Options defaults = Options.Parse(new[] { "clean" });
            Check("options: trim defaults to system and spares the foreground", defaults.TrimMode == TrimMode.System
                && defaults.SpareForeground && defaults.Spare.Count == 0);
            Check("options: --trim system parses", Options.Parse(new[] { "watch", "--trim", "SYSTEM" }).TrimMode == TrimMode.System);
            Check("options: --trim each works with watch", Options.Parse(new[] { "watch", "--trim", "each" }).TrimMode == TrimMode.EachProcess);
            Throws<ArgumentException>("options: --trim other is rejected", () => Options.Parse(new[] { "clean", "--trim", "some" }));
            Throws<ArgumentException>("options: --spare with a path is rejected",
                () => Options.Parse(new[] { "clean", "--trim", "each", "--spare", "C:\\Tools\\obs64.exe" }));
            string pathMsg = Message<ArgumentException>(() => Options.Parse(new[] { "clean", "--trim", "each", "--spare", "obs64,a/b.exe" }));
            Check("options: --spare names the bad entry", pathMsg != null && pathMsg.Contains("'a/b.exe'"), pathMsg);
            var many = new List<string>();
            for (int i = 0; i < 33; i++) many.Add("a" + i);
            Throws<ArgumentException>("options: --spare over 32 names is rejected",
                () => Options.Parse(new[] { "clean", "--trim", "each", "--spare", string.Join(",", many) }));
            Throws<ArgumentException>("options: --spare needs --trim each", () => Options.Parse(new[] { "clean", "--spare", "obs64" }));
            Throws<ArgumentException>("options: --no-spare-foreground needs --trim each",
                () => Options.Parse(new[] { "clean", "--trim", "system", "--no-spare-foreground" }));
            Throws<ArgumentException>("options: --trim not with status", () => Options.Parse(new[] { "status", "--trim", "each" }));
            Throws<ArgumentException>("options: --trim not with top", () => Options.Parse(new[] { "top", "--trim", "each" }));
            Throws<ArgumentException>("options: --trim not with a dry run", () => Options.Parse(new[] { "clean", "--dry-run", "--trim", "each" }));
            Throws<ArgumentException>("options: --trim each needs the workingsets area",
                () => Options.Parse(new[] { "clean", "--trim", "each", "--areas", "standby-low" }));
            // A repeated option is a conflict, not "the last one wins": a second --spare would silently drop the first list.
            string twiceTrim = Message<ArgumentException>(() => Options.Parse(new[] { "clean", "--trim", "system", "--trim", "each" }));
            Check("options: --trim twice is refused", twiceTrim == "--trim given twice.", twiceTrim);
            string twiceBy = Message<ArgumentException>(() => Options.Parse(new[] { "top", "--by", "ws", "--by", "private" }));
            Check("options: --by twice is refused", twiceBy == "--by given twice.", twiceBy);
            string twiceSpare = Message<ArgumentException>(() => Options.Parse(new[] { "clean", "--trim", "each", "--spare", "obs64", "--spare", "steam" }));
            Check("options: --spare twice is refused", twiceSpare == "--spare given twice.", twiceSpare);
            Check("options: --trim each with --no-spare-foreground in either order",
                !Options.Parse(new[] { "clean", "--no-spare-foreground", "--trim", "each" }).SpareForeground);
        }

        // ---------------------------------------------------------------- cli

        static void CliTests()
        {
            Options o = Options.Parse(new[] { "clean", "--trim", "each", "--spare", "obs64", "--no-spare-foreground" });
            CleanRequest req = Program.RequestFor(o, o.Areas, CancellationToken.None, TimeSpan.Zero);
            Check("cli: the clean request carries the trim mode and the list", req.Trim == TrimMode.EachProcess && !req.SpareForeground
                && req.TrimExclusions != null && req.TrimExclusions.Count == 1);
            CleanRequest sys = Program.RequestFor(Options.Parse(new[] { "clean" }), Areas.Default, CancellationToken.None, TimeSpan.Zero);
            Check("cli: a plain clean asks for the system-wide trim", sys.Trim == TrimMode.System && sys.SpareForeground);

            int asked = -1;
            AppSort askedBy = AppSort.Private;
            Func<int, AppSort, List<AppMemory>> source = (n, by) =>
            {
                asked = n; askedBy = by;
                return new List<AppMemory> { new AppMemory { Name = "code", Processes = 3, PrivateBytes = 10, Bytes = 20 } };
            };
            Func<MemorySnapshot> noSample = () => { throw new InvalidOperationException("sampled"); };
            var text = new StringWriter();
            int code = Program.Dispatch(Options.Parse(new[] { "top", "7", "--by", "ws" }), text, noSample, a => 99, x => 99,
                () => DateTime.UtcNow, source);
            Check("cli: top lists the apps with N and the sort", code == Program.ExitOk && asked == 7 && askedBy == AppSort.WorkingSet
                && text.ToString().Contains("code "), text.ToString());
            var json = new StringWriter();
            int jcode = Program.Dispatch(Options.Parse(new[] { "top", "--json" }), json, noSample, a => 99, x => 99,
                () => new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc), source);
            Check("cli: top --json prints one object", jcode == Program.ExitOk && json.ToString().TrimEnd().StartsWith("{\"v\":1,\"type\":\"top\"")
                && json.ToString().TrimEnd().Split('\n').Length == 1, json.ToString());

            string help = Program.HelpText();
            Check("help: documents top", help.Contains("ramopt top [N] [--by private|ws] [--json]"), help);
            Check("help: documents --trim each honestly", help.Contains("--trim each")
                && help.Contains("trim app by app instead of all at once; spares the app in front.")
                && help.Contains("Frees less: Windows services and protected processes are left alone."), help);
            Check("help: documents --spare and --no-spare-foreground", help.Contains("--spare LIST") && help.Contains("--no-spare-foreground"));
        }
    }
}
