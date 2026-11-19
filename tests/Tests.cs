// Minimal test runner (no test framework needed: only the .NET Framework compiler is required).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RamOptimiser.Tests
{
    public static class TestMain
    {
        static int failed, passed;

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
            catch (Exception ex) { Check(name, false, "threw " + ex.GetType().Name); }
        }

        /// <summary>Main's exit code when it refuses to run with an elevated token.</summary>
        internal const int ExitElevated = 3;
        /// <summary>Set to 1 to run the suite elevated anyway (a deliberate choice, never the default).</summary>
        internal const string AllowElevatedVariable = "RAMOPT_TESTS_ALLOW_ELEVATED";

        /// <summary>
        /// The suites write and delete files by path in %TEMP% folders that any same-user process can redirect, so
        /// an elevated run would turn a medium-integrity swap into an administrator's write or delete (audit run-4
        /// record 21). No test needs admin rights, so an elevated run is refused unless the override is exactly "1".
        /// </summary>
        internal static bool RefuseElevated(bool elevated, string allowValue)
        {
            return elevated && allowValue != "1";
        }

        public static int Main()
        {
            if (RefuseElevated(Gui.Elevation.IsElevated(), Environment.GetEnvironmentVariable(AllowElevatedVariable)))
            {
                Console.WriteLine("Refusing to run elevated: the tests use %TEMP% by path and must run from a non-elevated shell"
                    + " (set " + AllowElevatedVariable + "=1 to override).");
                return ExitElevated;
            }

            // Like both exes, restrict the DLL search before anything else, so the whole suite runs under it.
            bool dllSearchRestricted = Native.RestrictDllSearchToSystem32();
            var searchAttrs = typeof(Native).Assembly.GetCustomAttributes(typeof(DefaultDllImportSearchPathsAttribute), false);
            Check("dll search: P/Invoke imports search System32 only", searchAttrs.Length == 1
                && ((DefaultDllImportSearchPathsAttribute)searchAttrs[0]).Paths == DllImportSearchPath.System32,
                searchAttrs.Length + " attribute(s)");
            Check("dll search: process default limited to System32", dllSearchRestricted);

            // Area parsing
            Check("parse single", AreaNames.Parse("standby") == Areas.StandbyList);
            Check("parse list, spaces, case", AreaNames.Parse(" WorkingSets , modified") == (Areas.WorkingSets | Areas.ModifiedList));
            Check("parse default excludes full standby", (AreaNames.Parse("default") & Areas.StandbyList) == 0);
            Check("parse all includes standby", AreaNames.Parse("all") == Areas.All && (Areas.All & Areas.StandbyList) != 0);
            Throws<ArgumentException>("parse unknown", () => AreaNames.Parse("ram"));
            Throws<ArgumentException>("parse empty", () => AreaNames.Parse(" , "));
            Check("every flag has a name", AllFlagsNamed());
            Check("working sets run before the lists they feed", IndexOf(Areas.WorkingSets) < IndexOf(Areas.ModifiedList)
                && IndexOf(Areas.ModifiedList) < IndexOf(Areas.StandbyList));
            Check("parse volumes", AreaNames.Parse("volumes") == Areas.VolumeCache);
            Check("volume flush is opt-in", (Areas.Default & Areas.VolumeCache) == 0 && (Areas.All & Areas.VolumeCache) != 0);
            Check("volume flush runs before the modified list it feeds", IndexOf(Areas.VolumeCache) < IndexOf(Areas.ModifiedList));
            Check("combine runs first", IndexOf(Areas.CombinePages) == 0);
            const ulong Ram16 = 16UL << 30; // 2% = 328 MB, above the 256 MB floor
            Check("another pass: big gain", Cleaner.AnotherPass(1200L << 20, Ram16, 1, 3));
            Check("another pass: gain under 2% of RAM stops", !Cleaner.AnotherPass(300L << 20, Ram16, 1, 3));
            Check("another pass: 256 MB floor on small RAM", Cleaner.AnotherPass(256L << 20, 4UL << 30, 1, 3)
                && !Cleaner.AnotherPass(255L << 20, 4UL << 30, 1, 3));
            Check("another pass: capped", !Cleaner.AnotherPass(4000L << 20, Ram16, 3, 3));
            Check("another pass: none when in use grew", !Cleaner.AnotherPass(-500L << 20, Ram16, 1, 3));
            Check("another pass: one-pass cap", !Cleaner.AnotherPass(4000L << 20, Ram16, 1, 1));
            Check("volume summary: all ok", Cleaner.SummariseVolumes(new[] { "C:", "D:" }, new string[0]) == null);
            Check("volume summary: partial names the failures",
                Cleaner.SummariseVolumes(new[] { "C:" }, new[] { "E: access is denied" }) == "flushed 1 of 2 drives; E: access is denied");

            // Options
            Check("no args = help", Options.Parse(new string[0]).Command == Command.Help);
            var o = Options.Parse(new[] { "watch", "--threshold", "90", "--interval", "5", "--cooldown", "0", "--all" });
            Check("watch options", o.Command == Command.Watch && o.ThresholdPercent == 90 && o.IntervalSeconds == 5
                && o.CooldownSeconds == 0 && o.Areas == Areas.All);
            Check("clean default areas", Options.Parse(new[] { "clean" }).Areas == Areas.Default);
            Throws<ArgumentException>("threshold > 100", () => Options.Parse(new[] { "watch", "--threshold", "101" }));
            Throws<ArgumentException>("threshold negative", () => Options.Parse(new[] { "watch", "--threshold", "-5" }));
            Throws<ArgumentException>("missing value", () => Options.Parse(new[] { "clean", "--areas" }));
            Throws<ArgumentException>("unknown command", () => Options.Parse(new[] { "purge" }));
            Throws<ArgumentException>("status takes no options", () => Options.Parse(new[] { "status", "--all" }));

            // Auto-clean policy
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
            var cd = TimeSpan.FromMinutes(5);
            Check("below threshold", !AutoCleanPolicy.ShouldClean(84, 85, null, t0, cd));
            Check("at threshold, first time", AutoCleanPolicy.ShouldClean(85, 85, null, t0, cd));
            Check("inside cooldown", !AutoCleanPolicy.ShouldClean(95, 85, t0, t0.AddMinutes(4), cd));
            Check("cooldown elapsed", AutoCleanPolicy.ShouldClean(95, 85, t0, t0.AddMinutes(5), cd));

            // Formatting
            Check("bytes", Format.Bytes(512L) == "512 B", Format.Bytes(512L));
            Check("megabytes", Format.Bytes(1536L * 1024) == "1.5 MB", Format.Bytes(1536L * 1024));
            Check("negative", Format.Bytes(-2048L) == "-2.0 KB", Format.Bytes(-2048L));

            // Top apps: grouped by name, largest first, Idle left out, capped at n.
            var top = AppMemoryList.Aggregate(new[] {
                new KeyValuePair<string, ulong>("msedge", 300), new KeyValuePair<string, ulong>("MSEDGE", 200),
                new KeyValuePair<string, ulong>("code", 400), new KeyValuePair<string, ulong>("Idle", 9999),
                new KeyValuePair<string, ulong>("", 50), new KeyValuePair<string, ulong>("tiny", 1) }, 2);
            Check("top apps: grouped and ordered", top.Count == 2 && top[0].Name == "msedge" && top[0].Bytes == 500
                && top[0].Processes == 2 && top[1].Name == "code", top.Count > 0 ? top[0].Name + " " + top[0].Bytes : "empty");
            Check("top apps: live list works", AppMemoryList.Top(5).Count > 0);
            MemorySnapshot live = MemorySnapshot.Take();
            Check("commit: read and below limit", live.CommitLimit > 0 && live.CommitTotal > 0 && live.CommitTotal <= live.CommitLimit
                && live.CommitPercent <= 100, Format.Bytes(live.CommitTotal) + " of " + Format.Bytes(live.CommitLimit));

            // Native layouts must match the x64 C structs, or the kernel reads garbage.
            Check("x64 process", IntPtr.Size == 8, "build with /platform:x64");
            Check("MEMORY_COMBINE_INFORMATION_EX size", Marshal.SizeOf(typeof(Native.MEMORY_COMBINE_INFORMATION_EX)) == 24);
            Check("SYSTEM_MEMORY_LIST_INFORMATION size", Marshal.SizeOf(typeof(Native.SYSTEM_MEMORY_LIST_INFORMATION)) == 22 * 8);
            Check("MEMORYSTATUSEX size", Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) == 64);
            Check("PERFORMANCE_INFORMATION size", Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION)) == 104);

            // Live, read-only: a snapshot is self-consistent.
            MemorySnapshot s = MemorySnapshot.Take();
            Check("snapshot total > 0", s.TotalPhys > 0);
            Check("available <= total", s.AvailablePhys <= s.TotalPhys);
            Check("load is a percent", s.LoadPercent <= 100);
            if (s.HasLists)
                Check("free+standby ~ available", Near(s.FreeAndZero + s.Standby, s.AvailablePhys, 0.10),
                    Format.Bytes(s.FreeAndZero + s.Standby) + " vs " + Format.Bytes(s.AvailablePhys));
            var sw = new StringWriter();
            Program.PrintStatus(sw, s);
            Check("status output", sw.ToString().Contains("In use"));

            // Status-code mapping
            Check("privilege status explained", Cleaner.DescribeStatus(Native.STATUS_PRIVILEGE_NOT_HELD).Contains("administrator"));
            Check("version gate", Cleaner.AtLeast(new Version(10, 0), 6, 3) && !Cleaner.AtLeast(new Version(6, 2), 6, 3));

            // Report math
            var rep = new CleanReport
            {
                Before = new MemorySnapshot { TotalPhys = 100, AvailablePhys = 40 },
                After = new MemorySnapshot { TotalPhys = 100, AvailablePhys = 55 }
            };
            rep.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            rep.Results.Add(new AreaResult { Area = Areas.CombinePages, Skipped = true });
            Check("freed = drop in use", rep.InUseFreed == 15);
            Check("skipped is not a failure", rep.AllOk);
            rep.Results.Add(new AreaResult { Area = Areas.StandbyList, Ok = false });
            Check("failure detected", !rep.AllOk);

            EngineTests();
            WatchTests();
            JournalTests();

            // GUI plumbing (settings, auto-clean service, startup task, single instance, tray icon)
            int guiFailed = GuiPlumbingTests.Run();

            // GUI logic (wording, validation, theme rules, icon) and static XAML checks
            int appFailed = AppTests.Run();

            // Per-package test files register themselves: every other suite class is found and run, in name order.
            List<Type> suites = DiscoverSuites(typeof(TestMain).Assembly);
            int discoveredPassed = 0, discoveredFailed = 0;
            foreach (Type suite in suites)
            {
                int suitePassed, suiteFailed;
                RunSuite(suite, out suitePassed, out suiteFailed);
                discoveredPassed += suitePassed;
                discoveredFailed += suiteFailed;
            }
            var names = suites.ConvertAll(t => t.Name);
            var sortedNames = new List<string>(names);
            sortedNames.Sort(StringComparer.Ordinal);
            Check("discovery: finds the sample suite", suites.Contains(typeof(DiscoverySampleTests)), string.Join(", ", names.ToArray()));
            Check("discovery: leaves out the suites Main runs by hand", !suites.Contains(typeof(AppTests))
                && !suites.Contains(typeof(GuiPlumbingTests)) && !suites.Contains(typeof(TestMain)));
            Check("discovery: runs suites in name order", string.Join(",", names.ToArray()) == string.Join(",", sortedNames.ToArray()));
            Check("discovery: the sample suite ran and its checks are counted",
                DiscoverySampleTests.Passed == 1 && discoveredPassed >= DiscoverySampleTests.Passed,
                DiscoverySampleTests.Passed + " sample, " + discoveredPassed + " discovered");

            int totalPassed = passed + GuiPlumbingTests.Passed + AppTests.Passed + discoveredPassed,
                totalFailed = failed + guiFailed + appFailed + discoveredFailed;

            Console.WriteLine("{0} passed, {1} failed", totalPassed, totalFailed);
            return totalFailed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- suite discovery

        // A suite is a static class in this namespace named *Tests (other than the ones Main runs by hand)
        // with public static int Run() and a public static int Passed property or field.
        internal static List<Type> DiscoverSuites(Assembly assembly)
        {
            var suites = new List<Type>();
            foreach (Type t in assembly.GetTypes())
            {
                if (!t.IsClass || !t.IsAbstract || !t.IsSealed || t.IsNested) continue; // static, top-level classes only
                if (t.Namespace != "RamOptimiser.Tests" || !t.Name.EndsWith("Tests", StringComparison.Ordinal)) continue;
                if (t == typeof(AppTests) || t == typeof(GuiPlumbingTests) || t.Name == "Tests") continue;
                MethodInfo run = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (run == null || run.ReturnType != typeof(int) || PassedMember(t) == null) continue;
                suites.Add(t);
            }
            suites.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return suites;
        }

        static MemberInfo PassedMember(Type t)
        {
            PropertyInfo p = t.GetProperty("Passed", BindingFlags.Public | BindingFlags.Static);
            if (p != null && p.PropertyType == typeof(int) && p.GetGetMethod() != null) return p;
            FieldInfo f = t.GetField("Passed", BindingFlags.Public | BindingFlags.Static);
            if (f != null && f.FieldType == typeof(int)) return f;
            return null;
        }

        // A suite that throws counts as one failure, so a crash in one file never hides the others.
        static void RunSuite(Type suite, out int suitePassed, out int suiteFailed)
        {
            try
            {
                suiteFailed = (int)suite.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                    .Invoke(null, null);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Console.WriteLine("FAIL " + suite.Name + ": threw " + inner.GetType().Name + ": " + inner.Message);
                suiteFailed = 1;
            }
            MemberInfo m = PassedMember(suite);
            PropertyInfo p = m as PropertyInfo;
            suitePassed = p != null ? (int)p.GetValue(null, null) : (int)((FieldInfo)m).GetValue(null);
        }

        // ---------------------------------------------------------------- engine: outcomes, passes, drives, restore

        static void EngineTests()
        {
            // Help lists every area from the area table, and names both opt-in areas as left out of the default.
            string help = Program.HelpText();
            var helpLines = new List<string>();
            foreach (string line in help.Split('\n')) helpLines.Add(line.TrimEnd('\r'));
            foreach (string name in AreaNames.KnownNames())
                Check("help: lists area " + name, helpLines.Exists(l => l.StartsWith("  " + name + " ")));
            string areasHeader = helpLines.Find(l => l.StartsWith("Areas ("));
            Check("help: default leaves out standby and volumes", areasHeader != null
                && areasHeader.Contains("'standby'") && areasHeader.Contains("'volumes'"), areasHeader);
            Check("help: documents the watch give-up exit code", help.Contains(Program.ExitWatchFailed + " watch"));

            // Partial outcome: some of an area's work failed. Not a full success, so 'ramopt clean' exits 2.
            var partialArea = new AreaResult { Area = Areas.VolumeCache, Ok = true, Partial = true, Message = "flushed 1 of 2 drives; E: access is denied" };
            Check("outcome: partial", partialArea.Outcome == AreaOutcome.Partial);
            Check("outcome: skipped wins", new AreaResult { Ok = true, Skipped = true }.Outcome == AreaOutcome.Skipped);
            Check("outcome: failed", new AreaResult().Outcome == AreaOutcome.Failed);
            var partialReport = new CleanReport { Before = new MemorySnapshot { TotalPhys = 100 }, After = new MemorySnapshot { TotalPhys = 100 } };
            partialReport.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            partialReport.Results.Add(partialArea);
            Check("partial area is not all ok", !partialReport.AllOk);
            var printed = new StringWriter();
            Program.PrintReport(printed, partialReport);
            Check("cli report: partial shown as partial", printed.ToString().Contains("volumes      partial: flushed 1 of 2 drives"), printed.ToString());

            // Drives: one refuses -> partial; not-ready drives are skipped, never flushed.
            var flushedLetters = new List<string>();
            Func<string, int> flush = letter => { flushedLetters.Add(letter); return letter == "E:" ? 5 : letter == "D:" ? 21 : 0; };
            var vol = new AreaResult { Area = Areas.VolumeCache };
            Cleaner.FlushVolumes(vol, Drives("C:", true, "E:", true), flush);
            Check("volumes: one drive refuses -> partial", vol.Outcome == AreaOutcome.Partial
                && vol.Message != null && vol.Message.StartsWith("flushed 1 of 2 drives; E: "), vol.Outcome + " " + vol.Message);
            flushedLetters.Clear();
            vol = new AreaResult { Area = Areas.VolumeCache };
            Cleaner.FlushVolumes(vol, Drives("C:", true, "D:", false), flush);
            Check("volumes: not-ready drive skipped, not failed", vol.Outcome == AreaOutcome.Ok && !flushedLetters.Contains("D:")
                && vol.Message == "flushed 1 of 2 drives; D: not ready", vol.Outcome + " " + vol.Message);
            flushedLetters.Clear();
            vol = new AreaResult { Area = Areas.VolumeCache };
            Cleaner.FlushVolumes(vol, Drives("D:", false), flush);
            Check("volumes: no drive ready -> skipped", vol.Outcome == AreaOutcome.Skipped && flushedLetters.Count == 0, vol.Outcome + " " + vol.Message);
            vol = new AreaResult { Area = Areas.VolumeCache };
            Cleaner.FlushVolumes(vol, Drives("E:", true), flush);
            Check("volumes: every drive refuses -> failed", vol.Outcome == AreaOutcome.Failed && vol.Win32Error == 5, vol.Outcome + " " + vol.Message);

            // Passes: pass 1 is measured like-for-like (trim + flush only), and a later pass's failure is reported.
            const long MB = 1024L * 1024;
            CleanReport p = FakeClean(Areas.WorkingSets | Areas.SystemFileCache | Areas.ModifiedList, 100 * MB, 1024 * MB, 100 * MB, null);
            Check("passes: file cache gain does not start pass 2", p.Passes == 1, p.Passes + " passes");
            p = FakeClean(Areas.WorkingSets | Areas.SystemFileCache | Areas.ModifiedList, 1024 * MB, 1024 * MB, 1024 * MB, null);
            Check("passes: big like-for-like gain still repeats", p.Passes == 3, p.Passes + " passes");
            p = FakeClean(Areas.WorkingSets | Areas.ModifiedList, 1024 * MB, 0, 1024 * MB,
                (a, n) => a == Areas.ModifiedList && n == 2 ? "insufficient system resources" : null);
            AreaResult pTrim = Find(p, Areas.WorkingSets), pFlush = Find(p, Areas.ModifiedList);
            Check("passes: pass 2 flush failure reported", p.Passes == 1 && pFlush.Ok
                && pFlush.Message == "pass 2 failed: insufficient system resources", p.Passes + " / " + pFlush.Message);
            p = FakeClean(Areas.WorkingSets | Areas.ModifiedList, 1024 * MB, 0, 1024 * MB,
                (a, n) => a == Areas.WorkingSets && n == 3 ? "throw" : null);
            pTrim = Find(p, Areas.WorkingSets);
            Check("passes: pass count kept with a later exception", p.Passes == 2
                && pTrim.Message == "2 passes; pass 3 failed: Page file is full", p.Passes + " / " + pTrim.Message);

            // Restore: a service still stopping is waited for, then started.
            var svc = new FakeService { Status = System.ServiceProcess.ServiceControllerStatus.StopPending };
            var act = new ServiceAction { Name = "x" };
            ServiceTrimmer.RestoreOne(svc, act, TimeSpan.FromSeconds(10));
            Check("restore: stop-pending waits, then starts", act.Started && !act.Failed && act.Outcome == ServiceOutcome.Started
                && svc.Calls == "wait:Stopped,start,wait:Running,", svc.Calls + " " + act.Message);
            svc = new FakeService { Status = System.ServiceProcess.ServiceControllerStatus.StopPending, Stuck = true };
            act = new ServiceAction { Name = "x" };
            ServiceTrimmer.RestoreOne(svc, act, TimeSpan.FromSeconds(10));
            Check("restore: still stopping is its own message", act.Failed && !act.Started && act.Outcome == ServiceOutcome.StillStopping
                && act.Message == "was still stopping after 10 s" && !svc.Calls.Contains("start"), svc.Calls + " " + act.Message);
            svc = new FakeService { Status = System.ServiceProcess.ServiceControllerStatus.Paused };
            act = new ServiceAction { Name = "x" };
            ServiceTrimmer.RestoreOne(svc, act, TimeSpan.FromSeconds(10));
            Check("restore: paused continues", act.Started && act.Outcome == ServiceOutcome.Started && svc.Calls == "continue,wait:Running,", svc.Calls);
            svc = new FakeService { Status = System.ServiceProcess.ServiceControllerStatus.Stopped };
            act = new ServiceAction { Name = "x" };
            ServiceTrimmer.RestoreOne(svc, act, TimeSpan.FromSeconds(10));
            Check("restore: stopped starts", act.Started && act.Outcome == ServiceOutcome.Started && svc.Calls == "start,wait:Running,", svc.Calls);
            // A service stuck pausing never reaches Running: the start timed out.
            svc = new FakeService { Status = System.ServiceProcess.ServiceControllerStatus.PausePending };
            act = new ServiceAction { Name = "x" };
            ServiceTrimmer.RestoreOne(svc, act, TimeSpan.FromSeconds(10));
            Check("restore: a start that never finishes timed out", act.Failed && !act.Started && act.Outcome == ServiceOutcome.StartTimedOut
                && act.Message == "still starting after 10 s", act.Outcome + " " + act.Message);

            // Backoff policy, now in Core for both the CLI and the app.
            var five = TimeSpan.FromMinutes(5);
            Check("policy backoff: none", AutoCleanPolicy.ThresholdCooldown(five, 0) == five);
            Check("policy backoff: capped at 16x", AutoCleanPolicy.ThresholdCooldown(five, 9) == TimeSpan.FromMinutes(80));
            Check("policy streak", AutoCleanPolicy.NextStreak(0, 90, 85) == 1 && AutoCleanPolicy.NextStreak(2, 50, 85) == 0);
        }

        // ---------------------------------------------------------------- Power Clean journal (P3, E10)

        static void JournalTests()
        {
            // The file is user-writable: only catalog names survive, whatever it holds.
            List<string> kept = StoppedServiceJournal.Keep(new[] { "Spooler", " SysMain ", "WinDefend", "spooler", null, "",
                "CDPUserSvc_1a2b3", "CDPUserSvc_zz", "wuauserv", "CDPUserSvc_" + new string('a', 300) });
            Check("journal keep: catalog names only, trimmed, no duplicates", string.Join(",", kept.ToArray()) == "Spooler,SysMain,CDPUserSvc_1a2b3",
                string.Join(",", kept.ToArray()));
            Check("journal keep: null list is empty", StoppedServiceJournal.Keep(null).Count == 0);

            List<string> parsed = StoppedServiceJournal.Parse("# RamOptimiser\r\nSpooler\n\nWinDefend\r\n  WSearch  \r\n;x\r\nC:\\Windows\\evil.exe\r\n\0\x0001Fax\0\r\n");
            Check("journal parse: comments, blanks, foreign names and control bytes dropped", string.Join(",", parsed.ToArray()) == "Spooler,WSearch",
                string.Join(",", parsed.ToArray()));
            Check("journal parse: null and empty", StoppedServiceJournal.Parse(null).Count == 0 && StoppedServiceJournal.Parse("").Count == 0);
            var many = new System.Text.StringBuilder();
            for (int i = 0; i < 100; i++) many.Append("CDPUserSvc_" + i.ToString("x") + "\n");
            Check("journal parse: capped at MaxNames", StoppedServiceJournal.Parse(many.ToString()).Count == StoppedServiceJournal.MaxNames,
                StoppedServiceJournal.Parse(many.ToString()).Count.ToString());

            var names = new List<string> { "Spooler", "CDPUserSvc_1a2b3" };
            List<string> round = StoppedServiceJournal.Parse(StoppedServiceJournal.Serialize(names));
            Check("journal serialize: round trip", string.Join(",", round.ToArray()) == "Spooler,CDPUserSvc_1a2b3", StoppedServiceJournal.Serialize(names));
            Check("journal serialize: foreign names are not written", StoppedServiceJournal.Parse(StoppedServiceJournal.Serialize(new[] { "WinDefend" })).Count == 0
                && !StoppedServiceJournal.Serialize(new[] { "WinDefend" }).Contains("WinDefend"));

            Check("journal with: appends", string.Join(",", StoppedServiceJournal.With(names, "SysMain").ToArray()) == "Spooler,CDPUserSvc_1a2b3,SysMain");
            Check("journal with: no duplicate", StoppedServiceJournal.With(names, "spooler").Count == 2);
            Check("journal with: foreign or null name ignored", StoppedServiceJournal.With(names, "WinDefend").Count == 2
                && StoppedServiceJournal.With(names, null).Count == 2);
            Check("journal without: removes, any case", string.Join(",", StoppedServiceJournal.Without(names, "SPOOLER").ToArray()) == "CDPUserSvc_1a2b3");
            Check("journal without: unknown name leaves the list", StoppedServiceJournal.Without(names, "Fax").Count == 2
                && StoppedServiceJournal.Without(names, null).Count == 2);

            // Record before the stop; forget the record when the stop is refused.
            var j = new FakeJournal();
            int err = ServiceTrimmer.StopRecorded("Spooler", j, false, n => { j.Calls += "stop:" + n + ","; return 0; });
            Check("stop recorded: journal written before the stop", err == 0 && j.Calls == "add:Spooler,stop:Spooler,", j.Calls);
            j = new FakeJournal();
            err = ServiceTrimmer.StopRecorded("Spooler", j, false, n => { j.Calls += "stop:" + n + ","; return 1051; });
            Check("stop recorded: a refused stop forgets the record", err == 1051 && j.Calls == "add:Spooler,stop:Spooler,remove:Spooler,", j.Calls);
            j = new FakeJournal();
            err = ServiceTrimmer.StopRecorded("Spooler", j, true, n => { j.Calls += "stop:" + n + ","; return 1062; });
            Check("stop recorded: a service stopped earlier keeps its record", err == 1062 && !j.Calls.Contains("remove"), j.Calls);
            j = new FakeJournal { FailAdd = true };
            err = ServiceTrimmer.StopRecorded("Spooler", j, false, n => { j.Calls += "stop:" + n + ","; return 0; });
            Check("stop recorded: no record, no stop", err == ServiceTrimmer.JournalWriteFailed && !j.Calls.Contains("stop"), j.Calls);
            bool stopped = false;
            err = ServiceTrimmer.StopRecorded("Spooler", null, false, n => { stopped = true; return 0; });
            Check("stop recorded: no journal (CLI) still stops", err == 0 && stopped);

            Check("missing service: 1060 is missing", ServiceTrimmer.IsMissingService(
                new InvalidOperationException("Service x was not found", new System.ComponentModel.Win32Exception(1060))));
            Check("missing service: access denied is not", !ServiceTrimmer.IsMissingService(
                new InvalidOperationException("Cannot open x", new System.ComponentModel.Win32Exception(5))));
            Check("missing service: no inner error is not", !ServiceTrimmer.IsMissingService(new InvalidOperationException("x")));
        }

        sealed class FakeJournal : IStoppedServiceJournal
        {
            public string Calls = "";
            public bool FailAdd;
            public bool Add(string name) { Calls += "add:" + name + ","; return !FailAdd; }
            public bool Remove(string name) { Calls += "remove:" + name + ","; return true; }
        }

        static List<KeyValuePair<string, bool>> Drives(params object[] letterReady)
        {
            var list = new List<KeyValuePair<string, bool>>();
            for (int i = 0; i < letterReady.Length; i += 2) list.Add(new KeyValuePair<string, bool>((string)letterReady[i], (bool)letterReady[i + 1]));
            return list;
        }

        static AreaResult Find(CleanReport r, Areas area)
        {
            return r.Results.Find(x => x.Area == area);
        }

        /// <summary>Runs Cleaner.RunAreas on a fake 16 GB machine where each area frees a fixed amount per call.
        /// failure(area, nth call) returns a failure message, "throw", or null.</summary>
        static CleanReport FakeClean(Areas areas, long trimGain, long otherGain, long flushGain, Func<Areas, int, string> failure)
        {
            var report = new CleanReport { Before = new MemorySnapshot { TotalPhys = 16UL << 30 } };
            long mem = 12L << 30;
            var calls = new Dictionary<Areas, int>();
            Cleaner.RunAreas(areas, report, Cleaner.DefaultMaxPasses, () => (ulong)mem, r =>
            {
                int n;
                calls.TryGetValue(r.Area, out n);
                calls[r.Area] = ++n;
                string why = failure == null ? null : failure(r.Area, n);
                if (why == "throw") throw new InvalidOperationException("Page file is full.");
                if (why != null) { r.Ok = false; r.NtStatus = unchecked((int)0xC000009A); r.Message = why; return; }
                mem -= r.Area == Areas.WorkingSets ? trimGain : r.Area == Areas.ModifiedList ? flushGain : otherGain;
                r.Ok = true;
            });
            return report;
        }

        sealed class FakeService : ServiceTrimmer.IServiceControl
        {
            public System.ServiceProcess.ServiceControllerStatus Status { get; set; }
            public bool Stuck;     // never leaves StopPending
            public string Calls = "";
            public void Start() { Calls += "start,"; Status = System.ServiceProcess.ServiceControllerStatus.StartPending; }
            public void Continue() { Calls += "continue,"; Status = System.ServiceProcess.ServiceControllerStatus.ContinuePending; }
            public void WaitForStatus(System.ServiceProcess.ServiceControllerStatus want, TimeSpan timeout)
            {
                Calls += "wait:" + want + ",";
                var s = System.ServiceProcess.ServiceControllerStatus.StopPending;
                if (want == System.ServiceProcess.ServiceControllerStatus.Stopped && Status == s && !Stuck) Status = want;
                if (want == System.ServiceProcess.ServiceControllerStatus.Running
                    && (Status == System.ServiceProcess.ServiceControllerStatus.StartPending || Status == System.ServiceProcess.ServiceControllerStatus.ContinuePending))
                    Status = want;
                if (Status != want) throw new System.ServiceProcess.TimeoutException("timed out");
            }
        }

        // ---------------------------------------------------------------- watch loop

        sealed class WatchRig
        {
            public int Tick, MaxTicks = 6, Cleans, IntervalSeconds = 10;
            public Func<int, uint> Load = t => 95;
            public uint LoadAfter = 50;
            public Func<int, bool> SampleThrows = t => false;
            public Func<int, bool> CleanThrows = t => false;
            public Func<int, DateTime> Wall;
            public StringWriter Out = new StringWriter(), Err = new StringWriter();
            public Exception Escaped;

            public int Run(int threshold, int cooldown)
            {
                var t0 = new DateTime(2026, 11, 1, 12, 0, 0);
                if (Wall == null) Wall = t => t0.AddSeconds(t * IntervalSeconds);
                var o = new Options { Command = Command.Watch, ThresholdPercent = threshold, IntervalSeconds = IntervalSeconds, CooldownSeconds = cooldown };
                var w = new Watcher
                {
                    Sample = () =>
                    {
                        if (SampleThrows(Tick)) throw new System.ComponentModel.Win32Exception(8);
                        return new MemorySnapshot { TotalPhys = 100, AvailablePhys = 5, LoadPercent = Load(Tick) };
                    },
                    Clean = a =>
                    {
                        Cleans++;
                        if (CleanThrows(Tick)) throw new InvalidOperationException("clean broke");
                        return new CleanReport
                        {
                            Before = new MemorySnapshot { TotalPhys = 100, AvailablePhys = 5, LoadPercent = 95 },
                            After = new MemorySnapshot { TotalPhys = 100, AvailablePhys = 50, LoadPercent = LoadAfter }
                        };
                    },
                    Elapsed = () => TimeSpan.FromSeconds(Tick * IntervalSeconds),
                    Now = () => Wall(Tick),
                    WaitStop = t => ++Tick >= MaxTicks
                };
                try { return w.Run(o, Out, Err); }
                catch (Exception ex) { Escaped = ex; return -1; }
            }
        }

        static void WatchTests()
        {
            // The cooldown follows a monotonic clock: setting the wall clock back an hour (DST) does not stop cleaning.
            var rig = new WatchRig { MaxTicks = 4 };
            var t0 = new DateTime(2026, 11, 1, 12, 0, 0);
            rig.Wall = t => t0.AddSeconds(t * 10).AddHours(t >= 2 ? -1 : 0);
            rig.Run(85, 30);
            Check("watch: clock set back does not block cleaning", rig.Cleans == 2, rig.Cleans + " cleans");

            // Futile cleans double the wait: 10 s cooldown, load stays high -> cleans at 0 s and 20 s, then waits 40 s.
            rig = new WatchRig { LoadAfter = 95 };
            rig.Run(85, 10);
            Check("watch: futile cleans back off", rig.Cleans == 2, rig.Cleans + " cleans");
            // Load easing by itself resets the backoff: cleans at 0, 30 and 50 s.
            rig = new WatchRig { LoadAfter = 95, Load = t => t == 2 ? 50u : 95u };
            rig.Run(85, 10);
            Check("watch: backoff resets when load drops", rig.Cleans == 3, rig.Cleans + " cleans");
            rig = new WatchRig { LoadAfter = 50 };
            rig.Run(85, 10);
            Check("watch: cleans that help keep the normal cooldown", rig.Cleans == 6, rig.Cleans + " cleans");
            // --cooldown 0 still backs off: the check interval is the unit, so cleans at 0 s and 20 s, then waits 40 s.
            rig = new WatchRig { LoadAfter = 95 };
            rig.Run(85, 0);
            Check("watch: futile cleans back off with no cooldown", rig.Cleans == 2, rig.Cleans + " cleans");
            rig = new WatchRig { LoadAfter = 50 };
            rig.Run(85, 0);
            Check("watch: no cooldown cleans every interval while cleans help", rig.Cleans == 6, rig.Cleans + " cleans");

            // One failed sample is logged with a timestamp and watching goes on.
            rig = new WatchRig { MaxTicks = 4, SampleThrows = t => t == 1 };
            int code = rig.Run(85, 30);
            Check("watch: a failed sample is logged and watching continues", rig.Escaped == null && code == Program.ExitOk
                && rig.Err.ToString().StartsWith("[12:00:10] error: ") && rig.Out.ToString().Contains("Stopped."),
                (rig.Escaped != null ? rig.Escaped.Message : "") + " " + rig.Err);
            // A failed clean is logged, and still waits the cooldown (cleans at 0 s and 30 s, not every tick).
            rig = new WatchRig { MaxTicks = 4, CleanThrows = t => t == 0 };
            code = rig.Run(85, 30);
            Check("watch: a failed clean is logged and watching continues", rig.Escaped == null && code == Program.ExitOk
                && rig.Err.ToString().Contains("error: clean broke") && rig.Cleans == 2, rig.Cleans + " " + rig.Err);
            // Errors in a row: give up with its own exit code, without waiting past the last one.
            rig = new WatchRig { MaxTicks = 100, SampleThrows = t => true };
            code = rig.Run(85, 30);
            Check("watch: gives up after errors in a row", code == Program.ExitWatchFailed && rig.Tick == Watcher.MaxConsecutiveErrors - 1,
                code + " after " + rig.Tick + " waits; " + (rig.Escaped != null ? rig.Escaped.Message : ""));
            // A success between errors resets the count.
            rig = new WatchRig { MaxTicks = 12, SampleThrows = t => t != 4 };
            code = rig.Run(85, 30);
            Check("watch: a success resets the error count", code == Program.ExitWatchFailed && rig.Tick == 4 + Watcher.MaxConsecutiveErrors,
                code + " after " + rig.Tick + " waits");
            // Cleans that always fail count even when the samples between them work (cooldown longer than the interval).
            rig = new WatchRig { MaxTicks = 100, CleanThrows = t => true };
            code = rig.Run(85, 30);
            Check("watch: gives up after failed cleans in a row", code == Program.ExitWatchFailed && rig.Cleans == Watcher.MaxConsecutiveErrors
                && rig.Err.ToString().Contains("Stopped after " + Watcher.MaxConsecutiveErrors + " failed cleans in a row."),
                code + " after " + rig.Cleans + " cleans; " + rig.Err);
            // A clean that works resets that count: cleans at 0 and 30 s fail, 60 s works, then five more fail.
            rig = new WatchRig { MaxTicks = 100, CleanThrows = t => t != 6 };
            code = rig.Run(85, 30);
            Check("watch: a clean that works resets the failed-clean count", code == Program.ExitWatchFailed
                && rig.Cleans == 3 + Watcher.MaxConsecutiveErrors, code + " after " + rig.Cleans + " cleans");
        }

        static bool Near(ulong a, ulong b, double tolerance)
        {
            return Math.Abs((double)a - b) <= tolerance * Math.Max(a, b);
        }

        static int IndexOf(Areas a)
        {
            for (int i = 0; i < AreaNames.Known.Length; i++) if (AreaNames.Known[i].Value == a) return i;
            return -1;
        }

        static bool AllFlagsNamed()
        {
            var seen = new List<Areas>();
            foreach (var kv in AreaNames.Known) seen.Add(kv.Value);
            for (int bit = 1; bit <= (int)Areas.All; bit <<= 1)
                if (((int)Areas.All & bit) != 0 && !seen.Contains((Areas)bit)) return false;
            return true;
        }
    }
}
