// Package 1B: per-area and total clean timing, the pages-combined result (E8+E16), the Windows memory signal,
// the per-area estimate, ramopt clean --dry-run and the commit line in ramopt status (R1+E17+E18), and the watch
// wake-up on Windows' low-memory notification (E20). Pure code and fakes only: nothing here cleans memory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using RamOptimiser.App;

namespace RamOptimiser.Tests
{
    public static class EngineReportTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        const ulong MB = 1UL << 20, GB = 1UL << 30;

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

        public static int Run()
        {
            TimingTests();
            FormatTests();
            CombineTests();
            ReportTests();
            PressureTests();
            AdviceTests();
            EstimateTests();
            SnapshotTests();
            OptionsTests();
            StatusTests();
            DryRunTests();
            WatchWakeTests();
            AdviceTextTests();
            return failed;
        }

        // ---------------------------------------------------------------- E8+E16: timing

        static void TimingTests()
        {
            // Each area gets its own time: the fake clock moves 100 ms while combine runs and 300 ms while registry runs.
            TimeSpan clock = TimeSpan.Zero;
            var report = new CleanReport { Before = new MemorySnapshot { TotalPhys = 16 * GB } };
            Cleaner.RunAreas(Areas.CombinePages | Areas.RegistryCache, report, Cleaner.DefaultMaxPasses, () => 0UL, r =>
            {
                clock += TimeSpan.FromMilliseconds(r.Area == Areas.CombinePages ? 100 : 300);
                r.Ok = true;
            }, () => clock);
            AreaResult combine = Find(report, Areas.CombinePages), registry = Find(report, Areas.RegistryCache);
            Check("timing: each area gets its own duration", combine != null && registry != null
                && combine.Duration == TimeSpan.FromMilliseconds(100) && registry.Duration == TimeSpan.FromMilliseconds(300),
                combine == null || registry == null ? "missing row" : combine.Duration + " / " + registry.Duration);

            // Three passes, each run takes 1 s: the time lands on the one trim row and the one flush row.
            clock = TimeSpan.Zero;
            report = new CleanReport { Before = new MemorySnapshot { TotalPhys = 16 * GB } };
            long mem = 12L << 30;
            Cleaner.RunAreas(Areas.WorkingSets | Areas.ModifiedList, report, Cleaner.DefaultMaxPasses, () => (ulong)mem, r =>
            {
                clock += TimeSpan.FromSeconds(1);
                mem -= 1L << 30;
                r.Ok = true;
            }, () => clock);
            AreaResult trim = Find(report, Areas.WorkingSets), flush = Find(report, Areas.ModifiedList);
            int trimRows = report.Results.FindAll(x => x.Area == Areas.WorkingSets).Count;
            Check("timing: extra passes add to trim and flush", report.Passes == 3 && trimRows == 1
                && trim.Duration == TimeSpan.FromSeconds(3) && flush.Duration == TimeSpan.FromSeconds(3),
                report.Passes + " passes, " + trimRows + " trim rows, " + trim.Duration + " / " + flush.Duration);

            // An area that throws still gets its time.
            clock = TimeSpan.Zero;
            report = new CleanReport { Before = new MemorySnapshot { TotalPhys = 16 * GB } };
            Cleaner.RunAreas(Areas.RegistryCache, report, Cleaner.DefaultMaxPasses, () => 0UL, r =>
            {
                clock += TimeSpan.FromMilliseconds(200);
                throw new InvalidOperationException("broke");
            }, () => clock);
            registry = Find(report, Areas.RegistryCache);
            Check("timing: an area that throws still gets a duration", registry != null && !registry.Ok
                && registry.Duration == TimeSpan.FromMilliseconds(200), registry == null ? "missing" : registry.Duration.ToString());

            // The total covers both snapshots and every area: 50 ms per snapshot, 100 ms per area.
            clock = TimeSpan.Zero;
            int snapshots = 0;
            CleanReport total = Cleaner.Clean(Areas.CombinePages | Areas.RegistryCache, null, Cleaner.DefaultMaxPasses, () => clock,
                () => { clock += TimeSpan.FromMilliseconds(50); snapshots++; return new MemorySnapshot { TotalPhys = 16 * GB, PageSize = 4096 }; },
                () => 0UL,
                (rep, r) => { clock += TimeSpan.FromMilliseconds(100); r.Ok = true; });
            Check("timing: report total covers both snapshots and every area", total.Duration == TimeSpan.FromMilliseconds(300)
                && snapshots == 2 && total.Before != null && total.After != null && total.Results.Count == 2,
                total.Duration + ", " + snapshots + " snapshots, " + total.Results.Count + " rows");
            Check("timing: the seam keeps each area's time", total.Results.Count == 2
                && total.Results[0].Duration == TimeSpan.FromMilliseconds(100) && total.Results[1].Duration == TimeSpan.FromMilliseconds(100));
            // The run gets the report, so combine can record what it merged.
            CleanReport merged = Cleaner.Clean(Areas.CombinePages, null, 1, () => TimeSpan.Zero,
                () => new MemorySnapshot { TotalPhys = 16 * GB, PageSize = 4096 }, () => 0UL,
                (rep, r) => { rep.CombinedBytes = 120 * MB; r.Ok = true; });
            Check("timing: the run can set what combine merged", merged.CombinedBytes == 120 * MB);
            Check("combine: bytes are 0 when nothing ran", new CleanReport().CombinedBytes == 0);
        }

        // ---------------------------------------------------------------- E8+E16: seconds

        static void FormatTests()
        {
            Check("format: under 100 ms reads <0.1 s", Format.Seconds(TimeSpan.FromMilliseconds(99)) == "<0.1 s",
                Format.Seconds(TimeSpan.FromMilliseconds(99)));
            Check("format: zero reads <0.1 s", Format.Seconds(TimeSpan.Zero) == "<0.1 s");
            Check("format: negative reads <0.1 s", Format.Seconds(TimeSpan.FromSeconds(-3)) == "<0.1 s");
            Check("format: 100 ms reads 0.1 s", Format.Seconds(TimeSpan.FromMilliseconds(100)) == "0.1 s",
                Format.Seconds(TimeSpan.FromMilliseconds(100)));
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            string german;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                german = Format.Seconds(TimeSpan.FromMilliseconds(1840));
            }
            finally { Thread.CurrentThread.CurrentCulture = saved; }
            Check("format: 1.84 s reads 1.8 s under de-DE culture", german == "1.8 s", german);
            Check("format: duration text wraps the same format", AppText.DurationText(TimeSpan.FromMilliseconds(3400)) == "3.4 s"
                && AppText.DurationText(TimeSpan.FromMilliseconds(20)) == "<0.1 s", AppText.DurationText(TimeSpan.FromMilliseconds(3400)));
        }

        // ---------------------------------------------------------------- E8+E16: pages combined

        static void CombineTests()
        {
            Check("combine: message names the merged size", Cleaner.CombineMessage(30720, 4096) == "merged 120.0 MB",
                Cleaner.CombineMessage(30720, 4096));
            Check("combine: zero pages says none found", Cleaner.CombineMessage(0, 4096) == "no identical pages found");
            // Until an elevated run confirms Windows writes PagesCombined back, a zero count may only mean "not
            // reported", so the row claims nothing for it.
            Check("combine: the row names a merged size", Cleaner.CombineRowMessage(30720, 4096) == "merged 120.0 MB");
            Check("combine: the row says nothing for zero pages while the count is unconfirmed",
                !Cleaner.PagesCombinedConfirmed && Cleaner.CombineRowMessage(0, 4096) == null);
        }

        // ---------------------------------------------------------------- E8+E16: report lines

        static void ReportTests()
        {
            var r = new CleanReport
            {
                Before = new MemorySnapshot { TotalPhys = 16 * GB, AvailablePhys = 3 * GB },
                After = new MemorySnapshot { TotalPhys = 16 * GB, AvailablePhys = 5 * GB },
                Duration = TimeSpan.FromMilliseconds(3400)
            };
            r.Results.Add(new AreaResult { Area = Areas.CombinePages, Ok = true, Message = "merged 120.0 MB", Duration = TimeSpan.FromMilliseconds(400) });
            r.Results.Add(new AreaResult { Area = Areas.StandbyList, Ok = true, Duration = TimeSpan.FromMilliseconds(20) });
            r.Results.Add(new AreaResult { Area = Areas.RegistryCache, Skipped = true, Message = "needs Windows 8.1 or later", Duration = TimeSpan.FromSeconds(1) });
            var w = new StringWriter();
            Program.PrintReport(w, r);
            List<string> lines = Lines(w.ToString());
            Check("report: area time follows the message", lines.Contains("  combine      ok: merged 120.0 MB (0.4 s)"), w.ToString());
            Check("report: short area reads <0.1 s", lines.Contains("  standby      ok (<0.1 s)"), w.ToString());
            Check("report: skipped area prints no time", lines.Contains("  registry     skipped: needs Windows 8.1 or later"), w.ToString());
            Check("report: last line is the total time", lines.Count > 0 && lines[lines.Count - 1] == "Took 3.4 s.", w.ToString());
        }

        // ---------------------------------------------------------------- R1: memory signal

        static void PressureTests()
        {
            Check("pressure: low only is Low", MemoryPressure.Classify(true, true, true, false) == MemoryCondition.Low);
            Check("pressure: high only is High", MemoryPressure.Classify(true, false, true, true) == MemoryCondition.High);
            Check("pressure: neither is Neutral", MemoryPressure.Classify(true, false, true, false) == MemoryCondition.Neutral);
            Check("pressure: both is Unknown", MemoryPressure.Classify(true, true, true, true) == MemoryCondition.Unknown);
            Check("pressure: failed query is Unknown", MemoryPressure.Classify(false, true, true, false) == MemoryCondition.Unknown
                && MemoryPressure.Classify(true, false, false, true) == MemoryCondition.Unknown
                && MemoryPressure.Classify(false, false, false, false) == MemoryCondition.Unknown);

            // Live and read-only: the query answers without throwing, and the low handle can be waited on with no wait.
            MemoryCondition live = MemoryPressure.Query();
            Check("pressure: live query answers", Enum.IsDefined(typeof(MemoryCondition), live), live.ToString());
            Check("pressure: live query repeats", Enum.IsDefined(typeof(MemoryCondition), MemoryPressure.Query()));
            WaitHandle low = MemoryPressure.LowWaitHandle();
            bool waited = false;
            try { if (low != null) { low.WaitOne(0); waited = true; } }
            catch (Exception) { waited = false; }
            Check("pressure: low handle can be waited on", low != null && waited);
            Check("pressure: low handle is shared", ReferenceEquals(low, MemoryPressure.LowWaitHandle()));
        }

        // ---------------------------------------------------------------- R1: advice

        static void AdviceTests()
        {
            Check("advice: commit at 90% wins over low memory",
                CleanEstimate.Advise(new MemorySnapshot { CommitTotal = 90, CommitLimit = 100, Condition = MemoryCondition.Low }) == CleanAdvice.CommitBound);
            Check("advice: 89% commit falls through",
                CleanEstimate.Advise(new MemorySnapshot { CommitTotal = 89, CommitLimit = 100, Condition = MemoryCondition.Low }) == CleanAdvice.WillHelp);
            Check("advice: unknown commit limit ignored",
                CleanEstimate.Advise(new MemorySnapshot { CommitTotal = 95, CommitLimit = 0, Condition = MemoryCondition.Low }) == CleanAdvice.WillHelp);
            Check("advice: high is NotNeeded", CleanEstimate.Advise(new MemorySnapshot { Condition = MemoryCondition.High }) == CleanAdvice.NotNeeded);
            Check("advice: neutral is Mixed", CleanEstimate.Advise(new MemorySnapshot { Condition = MemoryCondition.Neutral }) == CleanAdvice.Mixed);
            Check("advice: unknown is None", CleanEstimate.Advise(new MemorySnapshot { Condition = MemoryCondition.Unknown }) == CleanAdvice.None);
            Check("advice: null snapshot is None", CleanEstimate.Advise(null) == CleanAdvice.None);
            Check("advice: commit bound level is 90", CleanEstimate.CommitBoundPercent == 90);
        }

        // ---------------------------------------------------------------- R1: estimate

        static MemorySnapshot ListSnap()
        {
            var s = new MemorySnapshot
            {
                TotalPhys = 16 * GB, AvailablePhys = 6 * GB, HasLists = true, PageSize = 4096,
                // SystemCache is the standby list plus the system working set (PERFORMANCE_INFORMATION), so it is
                // never below Standby: here a 1 GB system working set on top of 5 GB of standby.
                Modified = 600 * MB, ModifiedNoWrite = 40 * MB, SystemCache = 6 * GB,
                StandbyByPriority = new ulong[8], Standby = 5 * GB
            };
            s.StandbyByPriority[0] = 1200 * MB;
            s.StandbyByPriority[5] = 5 * GB - 1200 * MB;
            return s;
        }

        static AreaEstimate Row(List<AreaEstimate> rows, Areas area)
        {
            return rows.Find(e => e.Area == area);
        }

        static void EstimateTests()
        {
            List<AreaEstimate> rows = CleanEstimate.For(ListSnap(), Areas.ModifiedList);
            AreaEstimate modified = Row(rows, Areas.ModifiedList);
            Check("estimate: modified minus no-write", rows.Count == 1 && modified.Kind == EstimateKind.Bytes && modified.Bytes == 560 * MB,
                rows.Count + " rows, " + (modified == null ? "none" : Format.Bytes(modified.Bytes)));

            MemorySnapshot clamp = ListSnap();
            clamp.ModifiedNoWrite = 700 * MB;
            modified = Row(CleanEstimate.For(clamp, Areas.ModifiedList), Areas.ModifiedList);
            Check("estimate: no-write above modified clamps to 0", modified.Kind == EstimateKind.Bytes && modified.Bytes == 0, Format.Bytes(modified.Bytes));

            AreaEstimate low = Row(CleanEstimate.For(ListSnap(), Areas.LowPriorityStandby), Areas.LowPriorityStandby);
            Check("estimate: standby-low is priority 0 only", low.Kind == EstimateKind.Bytes && low.Bytes == 1200 * MB, Format.Bytes(low.Bytes));
            AreaEstimate standby = Row(CleanEstimate.For(ListSnap(), Areas.StandbyList), Areas.StandbyList);
            Check("estimate: standby is the whole standby total", standby.Kind == EstimateKind.Bytes && standby.Bytes == 5 * GB);
            AreaEstimate cache = Row(CleanEstimate.For(ListSnap(), Areas.SystemFileCache), Areas.SystemFileCache);
            Check("estimate: file cache leaves out what is already on standby", cache.Kind == EstimateKind.Bytes && cache.Bytes == 1 * GB
                && cache.UpperBound, Format.Bytes(cache.Bytes));
            MemorySnapshot noLists = ListSnap();
            noLists.HasLists = false;
            cache = Row(CleanEstimate.For(noLists, Areas.SystemFileCache), Areas.SystemFileCache);
            Check("estimate: file cache without the lists is the whole figure, said to include standby", cache.Kind == EstimateKind.Bytes
                && cache.Bytes == 6 * GB && cache.UpperBound && cache.Note.Contains("already on standby"), cache.Note);
            MemorySnapshot odd = ListSnap();
            odd.SystemCache = 2 * GB;
            cache = Row(CleanEstimate.For(odd, Areas.SystemFileCache), Areas.SystemFileCache);
            Check("estimate: file cache below standby clamps to 0", cache.Kind == EstimateKind.Bytes && cache.Bytes == 0, Format.Bytes(cache.Bytes));

            MemorySnapshot refused = ListSnap();
            refused.HasLists = false;
            List<AreaEstimate> listRows = CleanEstimate.For(refused, Areas.ModifiedList | Areas.LowPriorityStandby | Areas.StandbyList);
            Check("estimate: lists refused gives unknown with admin note", listRows.Count == 3
                && listRows.TrueForAll(e => e.Kind == EstimateKind.Unknown && e.Note == "needs administrator rights"),
                string.Join(" | ", listRows.ConvertAll(e => e.Area + " " + e.Kind + " " + e.Note).ToArray()));

            List<AreaEstimate> all = CleanEstimate.For(ListSnap(), Areas.All);
            var expected = new List<Areas>();
            foreach (var kv in AreaNames.Known) expected.Add(kv.Value);
            Check("estimate: rows follow AreaNames.Known order", string.Join(",", all.ConvertAll(e => e.Area.ToString()).ToArray())
                == string.Join(",", expected.ConvertAll(a => a.ToString()).ToArray()));
            List<AreaEstimate> withServices = CleanEstimate.For(ListSnap(), Areas.Default | Areas.Services);
            Check("estimate: services never listed", !withServices.Exists(e => e.Area == Areas.Services)
                && withServices.Count == CleanEstimate.For(ListSnap(), Areas.Default).Count);
            Check("estimate: registry and volumes have no memory figure",
                Row(all, Areas.RegistryCache).Kind == EstimateKind.NoMemoryFigure && Row(all, Areas.VolumeCache).Kind == EstimateKind.NoMemoryFigure);
            Check("estimate: trimming and combining can't be estimated",
                Row(all, Areas.WorkingSets).Kind == EstimateKind.Unknown && Row(all, Areas.CombinePages).Kind == EstimateKind.Unknown);
            Check("estimate: in-use drop counts modified only", CleanEstimate.InUseDropAtLeast(all) == 560 * MB,
                Format.Bytes(CleanEstimate.InUseDropAtLeast(all)));
            Check("estimate: in-use drop is 0 without the lists", CleanEstimate.InUseDropAtLeast(CleanEstimate.For(refused, Areas.All)) == 0);
            Check("estimate: every row has a note", all.TrueForAll(e => !string.IsNullOrEmpty(e.Note)));
        }

        // ---------------------------------------------------------------- R1: snapshot

        static void SnapshotTests()
        {
            var li = new Native.SYSTEM_MEMORY_LIST_INFORMATION
            {
                ZeroPageCount = new UIntPtr(5), FreePageCount = new UIntPtr(10), ModifiedPageCount = new UIntPtr(20),
                ModifiedNoWritePageCount = new UIntPtr(3), ModifiedPageCountPageFile = new UIntPtr(7),
                PageCountByPriority = new UIntPtr[8], RepurposedPagesByPriority = new UIntPtr[8]
            };
            for (int i = 0; i < 8; i++)
            {
                li.PageCountByPriority[i] = new UIntPtr((ulong)(i + 1));
                li.RepurposedPagesByPriority[i] = new UIntPtr((ulong)(100 + i));
            }
            var s = new MemorySnapshot();
            MemorySnapshot.FillLists(s, li, 4096);
            ulong sum = 0;
            foreach (ulong b in s.StandbyByPriority) sum += b;
            Check("snapshot: standby total equals the sum of priorities", s.HasLists && s.Standby == 36UL * 4096 && sum == s.Standby,
                s.Standby + " vs " + sum);
            Check("snapshot: low priority is priority 0", s.StandbyLowPriority == 4096 && s.StandbyByPriority[7] == 8UL * 4096);
            Check("snapshot: modified figures in bytes", s.Modified == 20UL * 4096 && s.ModifiedNoWrite == 3UL * 4096
                && s.ModifiedPageFile == 7UL * 4096 && s.FreeAndZero == 15UL * 4096);
            Check("snapshot: repurposed stays in pages", s.RepurposedByPriority != null && s.RepurposedByPriority.Length == 8
                && s.RepurposedByPriority[0] == 100 && s.RepurposedByPriority[7] == 107);
            Check("snapshot: page size kept", s.PageSize == 4096);

            var empty = new MemorySnapshot();
            MemorySnapshot.FillLists(empty, new Native.SYSTEM_MEMORY_LIST_INFORMATION(), 4096);
            Check("snapshot: missing priority arrays read as zero", empty.HasLists && empty.Standby == 0 && empty.StandbyLowPriority == 0
                && empty.StandbyByPriority != null && empty.StandbyByPriority.Length == 8);
            Check("snapshot: no lists means no priority figures", new MemorySnapshot().StandbyByPriority == null
                && new MemorySnapshot().StandbyLowPriority == 0);

            MemorySnapshot live = MemorySnapshot.Take();
            Check("snapshot: live page size and condition", live.PageSize > 0 && Enum.IsDefined(typeof(MemoryCondition), live.Condition),
                live.PageSize + " " + live.Condition);
            if (live.HasLists)
            {
                ulong liveSum = 0;
                foreach (ulong b in live.StandbyByPriority) liveSum += b;
                Check("snapshot: live standby equals its priorities", liveSum == live.Standby);
            }
        }

        // ---------------------------------------------------------------- R1: options

        static void OptionsTests()
        {
            Options o = Options.Parse(new[] { "clean", "--dry-run", "--areas", "modified,standby-low" });
            Check("options: --dry-run parses with clean", o.Command == Command.Clean && o.DryRun
                && o.Areas == (Areas.ModifiedList | Areas.LowPriorityStandby));
            Check("options: --dry-run with --all", Options.Parse(new[] { "clean", "--all", "--DRY-RUN" }).DryRun);
            Check("options: clean is not a dry run by default", !Options.Parse(new[] { "clean" }).DryRun);
            Throws<ArgumentException>("options: --dry-run rejected with status", () => Options.Parse(new[] { "status", "--dry-run" }));
            Throws<ArgumentException>("options: --dry-run rejected with watch", () => Options.Parse(new[] { "watch", "--dry-run" }));
            try { Options.Parse(new[] { "watch", "--dry-run" }); }
            catch (ArgumentException ex) { Check("options: --dry-run message", ex.Message == "--dry-run works only with 'clean'.", ex.Message); }
            Check("help: lists --dry-run", Program.HelpText().Contains(
                "  ramopt clean --dry-run [--areas LIST | --all]   show what a clean would do; changes nothing (no admin needed)"));
        }

        // ---------------------------------------------------------------- R1: status

        static void StatusTests()
        {
            var s = new MemorySnapshot
            {
                TotalPhys = 16 * GB, AvailablePhys = 6 * GB, LoadPercent = 62,
                CommitTotal = 19 * GB, CommitLimit = 25 * GB, Condition = MemoryCondition.Neutral
            };
            var w = new StringWriter();
            Program.PrintStatus(w, s);
            List<string> lines = Lines(w.ToString());
            Check("status: committed line shows percent", lines.Contains("  Committed      19.0 GB of 25.0 GB (76% of the limit; cleaning does not lower this)"),
                w.ToString());
            Check("status: windows says line", lines.Contains("  Windows says   memory is not low"), w.ToString());

            s.CommitLimit = 0;
            w = new StringWriter();
            Program.PrintStatus(w, s);
            Check("status: committed unknown when limit is 0", Lines(w.ToString()).Contains("  Committed      unknown"), w.ToString());

            MemorySnapshot lists = ListSnap();
            lists.Modified = 640 * MB;
            w = new StringWriter();
            Program.PrintStatus(w, lists);
            lines = Lines(w.ToString());
            Check("status: standby names the low-priority part", lines.Contains("    Standby      5.0 GB  (cache, given back on demand; 1.2 GB low priority)"), w.ToString());
            Check("status: modified names what can be written now",
                lines.Contains("  Modified       640.0 MB  (waiting to be written to disk; 600.0 MB can be written now)"), w.ToString());

            Check("status: condition words", Program.ConditionText(MemoryCondition.Low) == "memory is low"
                && Program.ConditionText(MemoryCondition.Neutral) == "memory is not low"
                && Program.ConditionText(MemoryCondition.High) == "memory is plentiful"
                && Program.ConditionText(MemoryCondition.Unknown) == "unknown");
        }

        // ---------------------------------------------------------------- R1: dry run

        static void DryRunTests()
        {
            MemorySnapshot s = ListSnap();
            s.Condition = MemoryCondition.Neutral;
            var w = new StringWriter();
            Program.PrintDryRun(w, s, Areas.Default);
            List<string> lines = Lines(w.ToString());
            Check("dry run: first line says nothing was cleaned", lines.Count > 0 && lines[0] == "Dry run: nothing was cleaned.", w.ToString());
            Check("dry run: combine row", lines.Contains("  combine       can't be estimated: depends on how many pages are identical"), w.ToString());
            Check("dry run: modified row", lines.Contains("  modified      about 560.0 MB written to disk, then counted as available"), w.ToString());
            Check("dry run: file cache row", lines.Contains("  filecache     up to 1.0 GB of file cache moved to standby, where it counts as available"), w.ToString());
            Check("dry run: standby-low row", lines.Contains("  standby-low   about 1.2 GB of low-priority cache dropped (already counted as available)"), w.ToString());
            Check("dry run: registry row", lines.Contains("  registry      no memory figure: writes registry changes to disk"), w.ToString());
            Check("dry run: in-use line", lines.Contains("In use could drop by about 560.0 MB, and by up to 1.0 GB more from the file cache, plus whatever trimming releases."), w.ToString());
            Check("dry run: advice line", lines.Count > 0
                && lines[lines.Count - 1] == "Windows is not short of memory. A clean frees some now, and programs read part of it back.", w.ToString());
            Check("dry run: no admin note when lists are readable", !w.ToString().Contains("elevated terminal"));

            MemorySnapshot refused = ListSnap();
            refused.HasLists = false;
            w = new StringWriter();
            Program.PrintDryRun(w, refused, Areas.Default);
            lines = Lines(w.ToString());
            Check("dry run: admin note when lists refused", lines.Contains("(the standby and modified figures need an elevated terminal)")
                && lines.Contains("  modified      can't be estimated: needs administrator rights"), w.ToString());

            w = new StringWriter();
            Program.PrintDryRun(w, ListSnap(), Areas.RegistryCache);
            Check("dry run: no admin note without list areas", !w.ToString().Contains("elevated terminal"), w.ToString());
            Check("dry run: unknown advice prints no advice line", Lines(w.ToString()).Count == 3, w.ToString());

            // The file cache alone: flushing it moves its pages to standby, so In use can drop.
            w = new StringWriter();
            Program.PrintDryRun(w, ListSnap(), Areas.SystemFileCache);
            lines = Lines(w.ToString());
            Check("dry run: file cache alone says In use can drop", lines.Contains("In use could drop by up to 1.0 GB from the file cache.")
                && !w.ToString().Contains("not likely to drop"), w.ToString());
            w = new StringWriter();
            Program.PrintDryRun(w, ListSnap(), Areas.SystemFileCache | Areas.WorkingSets);
            Check("dry run: file cache with trimming", Lines(w.ToString()).Contains(
                "In use could drop by up to 1.0 GB from the file cache, plus whatever trimming releases."), w.ToString());

            // Through the dispatcher: a dry run never reaches the elevation check or the cleaner, elevated or not.
            // The fakes throw, so a bug here can never clean memory for real.
            var dryOut = new StringWriter();
            int dryCode = Program.Dispatch(Options.Parse(new[] { "clean", "--dry-run", "--areas", "registry" }), dryOut,
                () => ListSnap(), a => { throw new InvalidOperationException("dry run reached the cleaner"); },
                o => { throw new InvalidOperationException("dry run reached watch"); });
            Check("dry run: the dispatcher never calls the cleaner", dryCode == Program.ExitOk
                && dryOut.ToString().StartsWith("Dry run: nothing was cleaned."), dryCode + " " + dryOut);
            int cleans = 0;
            int cleanCode = Program.Dispatch(Options.Parse(new[] { "clean", "--areas", "registry" }), new StringWriter(),
                () => ListSnap(), a => { cleans++; return a == Areas.RegistryCache ? 7 : -1; },
                o => { throw new InvalidOperationException("clean reached watch"); });
            Check("dispatch: a real clean goes to the cleaner with its areas", cleans == 1 && cleanCode == 7, cleans + " " + cleanCode);
        }

        // ---------------------------------------------------------------- E20: watch wake-up

        sealed class LowRig
        {
            public int Waits, MaxWaits = 40, IntervalSeconds = 10, Cleans;
            public double Seconds;
            public Func<double, uint> Load = t => 95;
            public uint LoadAfter = 50;
            public bool LowSignalled = true, CleanThrows;
            public ulong CommitTotal, CommitLimit;
            public List<bool> Flags = new List<bool>();
            public List<double> FlagTimes = new List<double>();
            public StringWriter Out = new StringWriter(), Err = new StringWriter();

            public int Run(int threshold, int cooldown)
            {
                var o = new Options { Command = Command.Watch, ThresholdPercent = threshold, IntervalSeconds = IntervalSeconds, CooldownSeconds = cooldown };
                var t0 = new DateTime(2026, 11, 1, 12, 0, 0);
                var w = new Watcher
                {
                    Sample = () => new MemorySnapshot
                    {
                        TotalPhys = 100, AvailablePhys = 5, LoadPercent = Load(Seconds), CommitTotal = CommitTotal, CommitLimit = CommitLimit
                    },
                    Clean = a =>
                    {
                        Cleans++;
                        if (CleanThrows) throw new InvalidOperationException("clean failed");
                        return new CleanReport
                        {
                            Before = new MemorySnapshot { TotalPhys = 100, AvailablePhys = 5, LoadPercent = 95 },
                            After = new MemorySnapshot { TotalPhys = 100, AvailablePhys = 50, LoadPercent = LoadAfter }
                        };
                    },
                    Elapsed = () => TimeSpan.FromSeconds(Seconds),
                    Now = () => t0.AddSeconds(Seconds),
                    // A low wake comes after 1 s; a wait that does not listen for it lasts the interval.
                    Wait = (t, includeLow) =>
                    {
                        Flags.Add(includeLow);
                        FlagTimes.Add(Seconds);
                        if (++Waits >= MaxWaits) return WakeReason.Stop;
                        if (includeLow && LowSignalled) { Seconds += 1; return WakeReason.LowMemory; }
                        Seconds += t.TotalSeconds;
                        return WakeReason.Interval;
                    }
                };
                return w.Run(o, Out, Err);
            }
        }

        static void WatchWakeTests()
        {
            var cd = TimeSpan.FromSeconds(300);
            Check("low signal: no clean yet after an interval listens", WatchSchedule.IncludeLowSignal(null, cd, WakeReason.Interval, false));
            Check("low signal: inside the cooldown does not listen", !WatchSchedule.IncludeLowSignal(TimeSpan.FromSeconds(10), cd, WakeReason.Interval, false));
            Check("low signal: cooldown over listens", WatchSchedule.IncludeLowSignal(TimeSpan.FromSeconds(300), cd, WakeReason.Interval, false));
            Check("low signal: low wake without a clean does not listen", !WatchSchedule.IncludeLowSignal(null, cd, WakeReason.LowMemory, false));
            Check("low signal: low wake with a clean and the cooldown over listens",
                WatchSchedule.IncludeLowSignal(TimeSpan.Zero, TimeSpan.Zero, WakeReason.LowMemory, true)
                && WatchSchedule.IncludeLowSignal(TimeSpan.FromSeconds(300), cd, WakeReason.LowMemory, true));

            // Cleans at 0 s; load eases at 300 s. Inside the cooldown the low signal is never listened for.
            var rig = new LowRig { Load = t => t < 300 ? 95u : 50u };
            int code = rig.Run(85, 300);
            bool quietInCooldown = true;
            int at300 = -1;
            for (int i = 0; i < rig.Flags.Count; i++)
            {
                if (rig.FlagTimes[i] < 300 && rig.Flags[i]) quietInCooldown = false;
                if (at300 < 0 && rig.FlagTimes[i] >= 300) at300 = i;
            }
            Check("watch: low wake inside the cooldown does not listen again until the cooldown ends", code == Program.ExitOk
                && rig.Cleans == 1 && quietInCooldown && at300 >= 0 && rig.Flags[at300],
                rig.Cleans + " cleans; " + string.Join(",", rig.Flags.ConvertAll(f => f ? "1" : "0").ToArray()));

            // Load under the threshold: a low wake that does not clean mutes the signal for one interval.
            rig = new LowRig { Load = t => 50, MaxWaits = 6 };
            rig.Run(85, 300);
            Check("watch: low wake with load under the threshold mutes the signal for one interval", rig.Cleans == 0 && rig.Flags.Count >= 3
                && rig.Flags[0] && !rig.Flags[1] && rig.Flags[2],
                string.Join(",", rig.Flags.ConvertAll(f => f ? "1" : "0").ToArray()));
            Check("watch: low wake logs a line", rig.Out.ToString().Contains("[12:00:01] Windows reports low memory: checking now"), rig.Out.ToString());
            Check("watch: stop still ends watching", rig.Out.ToString().EndsWith("Stopped." + Environment.NewLine), rig.Out.ToString());

            // A clean that throws did not clean: after a low wake it mutes the signal, so with --cooldown 0 a failing
            // clean is not retried back to back on the still-set signal.
            rig = new LowRig { CleanThrows = true, MaxWaits = 4 };
            rig.Run(85, 0);
            Check("watch: a failed clean after a low wake mutes the signal", rig.Flags.Count >= 2 && rig.Flags[0] && !rig.Flags[1],
                string.Join(",", rig.Flags.ConvertAll(f => f ? "1" : "0").ToArray()));

            // Without a low signal, the plain interval wait is unchanged.
            rig = new LowRig { Load = t => 50, LowSignalled = false, MaxWaits = 4 };
            rig.Run(85, 300);
            Check("watch: no low signal, no low line", !rig.Out.ToString().Contains("low memory") && rig.Waits == 4);

            // The WaitStop fallback when no Wait is set.
            int stops = 0;
            var plain = new Watcher
            {
                Sample = () => new MemorySnapshot { TotalPhys = 100, AvailablePhys = 60, LoadPercent = 40 },
                Clean = a => { throw new InvalidOperationException("must not clean"); },
                Elapsed = () => TimeSpan.FromSeconds(stops * 10),
                WaitStop = t => ++stops >= 3
            };
            var plainOut = new StringWriter();
            code = plain.Run(new Options { Command = Command.Watch, ThresholdPercent = 85 }, plainOut, new StringWriter());
            Check("watch: the WaitStop fallback still works", code == Program.ExitOk && stops == 3 && plainOut.ToString().Contains("Stopped."));

            // Commit bound: a clean is reported with the note that it cannot lower commit.
            rig = new LowRig { CommitTotal = 95, CommitLimit = 100, MaxWaits = 2 };
            rig.Run(85, 300);
            Check("watch: commit-bound clean prints the note",
                rig.Out.ToString().Contains("  note: committed memory is 95% of the limit; cleaning does not lower it. Close programs you are not using."),
                rig.Out.ToString());
            rig = new LowRig { CommitTotal = 50, CommitLimit = 100, MaxWaits = 2 };
            rig.Run(85, 300);
            Check("watch: no note below the commit bound", rig.Cleans == 1 && !rig.Out.ToString().Contains("note:"), rig.Out.ToString());
        }

        // ---------------------------------------------------------------- R1: GUI wording

        static void AdviceTextTests()
        {
            var s = new MemorySnapshot { CommitTotal = 92, CommitLimit = 100 };
            string bound = AppText.CleanAdvice(CleanAdvice.CommitBound, s);
            Check("advice text: commit bound names the percent", bound == "Programs have reserved 92% of the memory Windows can give out. "
                + "Cleaning does not lower that; closing programs does.", bound);
            Check("advice text: none is null", AppText.CleanAdvice(CleanAdvice.None, s) == null);
            bool tidy = true;
            foreach (CleanAdvice a in new[] { CleanAdvice.CommitBound, CleanAdvice.WillHelp, CleanAdvice.Mixed, CleanAdvice.NotNeeded })
            {
                string text = AppText.CleanAdvice(a, s);
                if (string.IsNullOrEmpty(text) || !text.EndsWith(".") || text.Contains("!") || char.IsLower(text[0])) tidy = false;
            }
            Check("advice text: sentences end with a full stop and have no exclamation mark", tidy);
            Check("advice text: mixed wording", AppText.CleanAdvice(CleanAdvice.Mixed, s)
                == "Windows is not short of memory. A clean frees some now, and programs read part of it back.");
            Check("advice text: null snapshot is safe", AppText.CleanAdvice(CleanAdvice.CommitBound, null) != null);
            // One source for the shared sentences: the app and the terminal say the same thing.
            bool same = true;
            foreach (CleanAdvice a in new[] { CleanAdvice.WillHelp, CleanAdvice.Mixed, CleanAdvice.NotNeeded })
                same &= CleanEstimate.AdviceSentence(a) != null && AppText.CleanAdvice(a, s) == CleanEstimate.AdviceSentence(a)
                    && Program.AdviceText(a, s) == CleanEstimate.AdviceSentence(a);
            Check("advice text: app and terminal share one sentence per advice", same);
            Check("advice text: the shared sentence has none for commit bound or none",
                CleanEstimate.AdviceSentence(CleanAdvice.CommitBound) == null && CleanEstimate.AdviceSentence(CleanAdvice.None) == null);
        }

        // ---------------------------------------------------------------- helpers

        static AreaResult Find(CleanReport r, Areas area)
        {
            return r.Results.Find(x => x.Area == area);
        }

        static List<string> Lines(string text)
        {
            var list = new List<string>();
            foreach (string line in text.Split('\n')) list.Add(line.TrimEnd('\r'));
            while (list.Count > 0 && list[list.Count - 1].Length == 0) list.RemoveAt(list.Count - 1);
            return list;
        }
    }
}
