// Package 3C (R4+E21 part 2, CLI): ramopt clean and watch add each clean to the clean log unless --no-log, the log
// record is written under the clean lock, and --json prints machine-readable output for status, clean, clean
// --dry-run and watch. Fakes only: no test here cleans memory, takes the real clean lock or writes the real
// %LOCALAPPDATA%\RamOptimiser\cleans.log.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace RamOptimiser.Tests
{
    public static class CliLogTests
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

        public static int Run()
        {
            OptionTests();
            NameTests();
            StatusJsonTests();
            DryRunJsonTests();
            CleanTests();
            WatchTests();
            HelpTests();
            return failed;
        }

        static readonly DateTime T0 = new DateTime(2026, 11, 17, 14, 32, 5, DateTimeKind.Utc);

        static MemorySnapshot Snap(uint load)
        {
            return new MemorySnapshot { TotalPhys = 16UL << 30, AvailablePhys = (16UL << 30) * (100 - load) / 100, LoadPercent = load };
        }

        static MemorySnapshot FullSnap()
        {
            var s = Snap(81);
            s.SystemCache = 1UL << 30;
            s.HasLists = true;
            s.FreeAndZero = 2UL << 30;
            s.Standby = 3UL << 30;
            s.StandbyByPriority = new ulong[] { 100, 1, 2, 3, 4, 5, 6, ulong.MaxValue };
            s.RepurposedByPriority = new ulong[] { 7, 0, 0, 0, 0, 0, 0, 9 };
            s.Modified = 500UL << 20;
            s.ModifiedNoWrite = 10UL << 20;
            s.Condition = MemoryCondition.Neutral;
            s.CommitTotal = 30UL << 30;
            s.CommitLimit = 40UL << 30;
            return s;
        }

        static Dictionary<string, object> Parse(string json)
        {
            try { return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
        }

        static List<string> Lines(StringWriter w)
        {
            var lines = new List<string>();
            foreach (string l in w.ToString().Split('\n'))
            {
                string t = l.TrimEnd('\r');
                if (t.Length > 0) lines.Add(t);
            }
            return lines;
        }

        // ---------------------------------------------------------------- options

        static void OptionTests()
        {
            Check("options: --json and --no-log are off by default", !Options.Parse(new[] { "clean" }).Json && !Options.Parse(new[] { "clean" }).NoLog);
            Check("options: --json accepted by status", Options.Parse(new[] { "status", "--json" }).Json);
            Check("options: --json accepted by clean", Options.Parse(new[] { "clean", "--JSON", "--areas", "registry" }).Json);
            Check("options: --json accepted by clean --dry-run", Options.Parse(new[] { "clean", "--dry-run", "--json" }).Json);
            Check("options: --json accepted by watch", Options.Parse(new[] { "watch", "--json", "--threshold", "90" }).Json);
            Throws<ArgumentException>("options: --json rejected by help", () => Options.Parse(new[] { "help", "--json" }));
            Throws<ArgumentException>("options: --json rejected by --help", () => Options.Parse(new[] { "--help", "--json" }));
            Check("options: help alone still works", Options.Parse(new[] { "help" }).Command == Command.Help);
            Throws<ArgumentException>("options: --no-log rejected by help", () => Options.Parse(new[] { "help", "--no-log" }));
            Check("options: help with a topic still prints help", Options.Parse(new[] { "help", "clean" }).Command == Command.Help
                && Options.Parse(new[] { "-h", "foo" }).Command == Command.Help && Options.Parse(new[] { "/?", "x" }).Command == Command.Help);
            Check("options: --no-log with clean", Options.Parse(new[] { "clean", "--no-log" }).NoLog);
            Check("options: --no-log with watch", Options.Parse(new[] { "watch", "--NO-LOG" }).NoLog);
            Throws<ArgumentException>("options: --no-log rejected by status", () => Options.Parse(new[] { "status", "--no-log" }));
            try { Options.Parse(new[] { "status", "--no-log" }); }
            catch (ArgumentException ex) { Check("options: --no-log message", ex.Message == "--no-log works only with 'clean' and 'watch'.", ex.Message); }
            Throws<ArgumentException>("options: status still refuses other options", () => Options.Parse(new[] { "status", "--json", "--all" }));
        }

        // ---------------------------------------------------------------- record vocabulary

        static void NameTests()
        {
            Check("names: stopped values", CleanLog.StopName(CleanStop.None) == "none" && CleanLog.StopName(CleanStop.Cancelled) == "cancelled"
                && CleanLog.StopName(CleanStop.TimedOut) == "timed-out" && CleanLog.StopName(CleanStop.Busy) == "busy");
            Check("names: advice values", CleanLog.AdviceName(CleanAdvice.CommitBound) == "commit-bound" && CleanLog.AdviceName(CleanAdvice.WillHelp) == "will-help"
                && CleanLog.AdviceName(CleanAdvice.Mixed) == "mixed" && CleanLog.AdviceName(CleanAdvice.NotNeeded) == "not-needed"
                && CleanLog.AdviceName(CleanAdvice.None) == "none");
        }

        // ---------------------------------------------------------------- status --json

        static void StatusJsonTests()
        {
            var o = Options.Parse(new[] { "status", "--json" });
            var outW = new StringWriter();
            int code = Program.Dispatch(o, outW, FullSnap, a => { throw new InvalidOperationException("no clean"); }, x => 99, () => T0);
            List<string> lines = Lines(outW);
            Dictionary<string, object> d = lines.Count == 1 ? Parse(lines[0]) : null;
            Check("cli: --json status is one object", code == Program.ExitOk && d != null, outW.ToString());
            if (d == null) return;
            var lists = d["lists"] as Dictionary<string, object>;
            var commit = d["commit"] as Dictionary<string, object>;
            Check("cli: --json status fields", (int)d["v"] == 1 && (string)d["time"] == "2026-11-17T14:32:05Z"
                && Convert.ToUInt64(d["totalPhys"]) == (16UL << 30) && Convert.ToInt64(d["loadPercent"]) == 81
                && Convert.ToUInt64(d["inUse"]) == FullSnap().InUse && Convert.ToUInt64(d["systemCache"]) == (1UL << 30)
                && (string)d["condition"] == "neutral" && (string)d["advice"] == CleanLog.AdviceName(CleanEstimate.Advise(FullSnap())), lines[0]);
            Check("cli: --json status lists", lists != null && Convert.ToUInt64(lists["standby"]) == (3UL << 30)
                && ((object[])lists["standbyByPriority"]).Length == 8 && ((object[])lists["repurposedByPriority"]).Length == 8
                && Convert.ToUInt64(lists["modifiedNoWrite"]) == (10UL << 20), lines[0]);
            Check("cli: --json status keeps ulong max exact", lines[0].Contains(",18446744073709551615]"), lines[0]);
            Check("cli: --json status commit", commit != null && Convert.ToInt64(commit["percent"]) == 75
                && Convert.ToUInt64(commit["limit"]) == (40UL << 30), lines[0]);

            string bare = Program.StatusJson(Snap(50), T0);
            Dictionary<string, object> b = Parse(bare);
            Check("cli: --json status without lists or commit says null", b != null && b.ContainsKey("lists") && b["lists"] == null
                && b.ContainsKey("commit") && b["commit"] == null && (string)b["condition"] == "unknown", bare);
            Check("cli: --json status is one line", bare.IndexOf('\n') < 0 && bare.IndexOf('\r') < 0);
        }

        // ---------------------------------------------------------------- clean --dry-run --json

        static void DryRunJsonTests()
        {
            var o = Options.Parse(new[] { "clean", "--dry-run", "--json", "--areas", "modified,standby-low,registry" });
            var outW = new StringWriter();
            bool cleaned = false;
            int code = Program.Dispatch(o, outW, FullSnap, a => { cleaned = true; return 0; }, x => 99, () => T0);
            List<string> lines = Lines(outW);
            Dictionary<string, object> d = lines.Count == 1 ? Parse(lines[0]) : null;
            Check("cli: --json dry run is one estimate object and never cleans", code == Program.ExitOk && !cleaned && d != null
                && (string)d["type"] == "estimate" && (int)d["v"] == 1, outW.ToString());
            if (d == null) return;
            var areas = d["areas"] as object[];
            var byName = new Dictionary<string, Dictionary<string, object>>();
            if (areas != null) foreach (object a in areas) { var ad = (Dictionary<string, object>)a; byName[(string)ad["name"]] = ad; }
            List<AreaEstimate> rows = CleanEstimate.For(FullSnap(), o.Areas);
            Check("cli: --json dry run lists each area", areas != null && areas.Length == rows.Count && byName.ContainsKey("modified")
                && byName.ContainsKey("standby-low") && byName.ContainsKey("registry"), lines[0]);
            bool kindsMatch = byName.Count == rows.Count;
            foreach (AreaEstimate e in rows)
            {
                Dictionary<string, object> ad;
                if (!byName.TryGetValue(AreaNames.NameOf(e.Area), out ad)) { kindsMatch = false; continue; }
                string kind = e.Kind == EstimateKind.Bytes ? "bytes" : e.Kind == EstimateKind.Unknown ? "unknown" : "none";
                kindsMatch &= (string)ad["kind"] == kind && (string)ad["note"] == e.Note
                    && (e.Kind == EstimateKind.Bytes ? Convert.ToUInt64(ad["bytes"]) == e.Bytes : ad["bytes"] == null)
                    && (bool)ad["upperBound"] == e.UpperBound;
            }
            Check("cli: --json dry run kinds, bytes and notes", kindsMatch, lines[0]);
            Check("cli: --json dry run totals and advice", Convert.ToUInt64(d["inUseDropAtLeast"]) == CleanEstimate.InUseDropAtLeast(rows)
                && Convert.ToUInt64(d["fileCacheDropUpTo"]) == CleanEstimate.FileCacheDropUpTo(rows)
                && (string)d["advice"] == CleanLog.AdviceName(CleanEstimate.Advise(FullSnap())), lines[0]);

            var text = new StringWriter();
            Program.Dispatch(Options.Parse(new[] { "clean", "--dry-run" }), text, FullSnap, a => 0, x => 99, () => T0);
            Check("cli: dry run without --json stays text", text.ToString().StartsWith("Dry run: nothing was cleaned."), text.ToString());
        }

        // ---------------------------------------------------------------- clean

        static CleanReport Report(CleanStop stop)
        {
            var r = new CleanReport { Before = Snap(81), After = Snap(74), Duration = TimeSpan.FromMilliseconds(3210), Stopped = stop };
            r.Results.Add(new AreaResult { Area = Areas.RegistryCache, Ok = true, Duration = TimeSpan.FromMilliseconds(5) });
            if (stop == CleanStop.Cancelled || stop == CleanStop.TimedOut)
                r.Results.Add(new AreaResult { Area = Areas.ModifiedList, NotRun = true });
            return r;
        }

        /// <summary>A fake Cleaner.Clean: records the request, runs UnderLock as the real one does (not for a busy clean).</summary>
        sealed class FakeClean
        {
            public CleanRequest Request;
            public bool InLock;
            public CleanStop Stop;
            public bool SkipUnderLock;
            public CleanReport Clean(CleanRequest req)
            {
                Request = req;
                CleanReport r = Report(Stop);
                if (Stop == CleanStop.Busy) { r.Before = Snap(81); r.After = r.Before; r.Results.Clear(); return r; }
                InLock = true;
                try { if (req.UnderLock != null && !SkipUnderLock) req.UnderLock(r); }
                finally { InLock = false; }
                return r;
            }
        }

        static void CleanTests()
        {
            // Text clean: the report as before, and one record appended under the lock.
            var fake = new FakeClean();
            var appended = new List<string>();
            bool appendedInLock = false;
            var rec = new CleanRecorder(CleanRecorder.CliSource, true, l => { appended.Add(l); appendedInLock = fake.InLock; return true; }, () => T0);
            var outW = new StringWriter();
            var errW = new StringWriter();
            int code = Program.CleanOnce(Options.Parse(new[] { "clean", "--areas", "registry" }), CancellationToken.None, fake.Clean, rec, outW, errW);
            Dictionary<string, object> d = appended.Count == 1 ? Parse(appended[0]) : null;
            Check("cli: clean logs one record by default", code == Program.ExitOk && d != null && (string)d["type"] == "clean"
                && (string)d["source"] == "cli" && (string)d["reason"] == "manual" && (string)d["stopped"] == "none"
                && (string)d["time"] == "2026-11-17T14:32:05Z", string.Join(" | ", appended));
            Check("cli: the clean record is written under the clean lock", appendedInLock && fake.Request != null && fake.Request.UnderLock != null);
            Check("cli: clean without --json prints the text report", outW.ToString().Contains("In use: ") && !outW.ToString().Contains("{")
                && errW.ToString() == "", outW + "|" + errW);
            Check("cli: the clean request keeps the lock wait", fake.Request != null && fake.Request.LockWait == Program.CliLockWait);

            // --json: the same record on stdout, and nothing else there.
            fake = new FakeClean();
            appended.Clear();
            rec = new CleanRecorder(CleanRecorder.CliSource, true, l => { appended.Add(l); return true; }, () => T0);
            outW = new StringWriter();
            errW = new StringWriter();
            code = Program.CleanOnce(Options.Parse(new[] { "clean", "--json" }), CancellationToken.None, fake.Clean, rec, outW, errW);
            List<string> lines = Lines(outW);
            Check("cli: --json clean writes nothing else to stdout", code == Program.ExitOk && lines.Count == 1 && Parse(lines[0]) != null
                && appended.Count == 1 && lines[0] == appended[0] && errW.ToString() == "", outW + "|" + errW);

            // --no-log: nothing appended; --json still prints the record.
            fake = new FakeClean();
            int calls = 0;
            rec = new CleanRecorder(CleanRecorder.CliSource, false, l => { calls++; return true; }, () => T0);
            outW = new StringWriter();
            code = Program.CleanOnce(Options.Parse(new[] { "clean", "--no-log", "--json" }), CancellationToken.None, fake.Clean, rec, outW, new StringWriter());
            lines = Lines(outW);
            Check("cli: --no-log writes no record", calls == 0 && code == Program.ExitOk);
            Check("cli: --no-log --json still prints the record", lines.Count == 1 && Parse(lines[0]) != null && (string)Parse(lines[0])["source"] == "cli", outW.ToString());

            // Busy: the lock was never taken, so the record is written after; the error stays on stderr.
            fake = new FakeClean { Stop = CleanStop.Busy };
            appended.Clear();
            rec = new CleanRecorder(CleanRecorder.CliSource, true, l => { appended.Add(l); return true; }, () => T0);
            outW = new StringWriter();
            errW = new StringWriter();
            code = Program.CleanOnce(Options.Parse(new[] { "clean", "--json" }), CancellationToken.None, fake.Clean, rec, outW, errW);
            lines = Lines(outW);
            Check("cli: busy clean logs stopped busy", code == Program.ExitBusy && appended.Count == 1 && (string)Parse(appended[0])["stopped"] == "busy",
                string.Join(" | ", appended));
            Check("cli: busy --json keeps the error on stderr", errW.ToString().TrimEnd() == Program.BusyMessage
                && lines.Count == 1 && lines[0] == appended[0], outW + "|" + errW);

            // Stopped cleans keep exit code 2 and name the stop.
            foreach (CleanStop stop in new[] { CleanStop.Cancelled, CleanStop.TimedOut })
            {
                fake = new FakeClean { Stop = stop };
                appended.Clear();
                rec = new CleanRecorder(CleanRecorder.CliSource, true, l => { appended.Add(l); return true; }, () => T0);
                code = Program.CleanOnce(Options.Parse(new[] { "clean" }), CancellationToken.None, fake.Clean, rec, new StringWriter(), new StringWriter());
                Check("cli: a " + CleanLog.StopName(stop) + " clean logs its stop and exits 2", code == Program.ExitPartial && appended.Count == 1
                    && (string)Parse(appended[0])["stopped"] == CleanLog.StopName(stop), string.Join(" | ", appended));
            }

            // A clean whose UnderLock never ran (another code path) is still recorded exactly once.
            fake = new FakeClean { SkipUnderLock = true };
            appended.Clear();
            rec = new CleanRecorder(CleanRecorder.CliSource, true, l => { appended.Add(l); return true; }, () => T0);
            Program.CleanOnce(Options.Parse(new[] { "clean" }), CancellationToken.None, fake.Clean, rec, new StringWriter(), new StringWriter());
            Check("cli: a record is written once even without UnderLock", appended.Count == 1);

            // A failed append: the clean still counts, and stderr says so once.
            fake = new FakeClean();
            rec = new CleanRecorder(CleanRecorder.CliSource, true, l => false, () => T0);
            outW = new StringWriter();
            errW = new StringWriter();
            code = Program.CleanOnce(Options.Parse(new[] { "clean", "--json" }), CancellationToken.None, fake.Clean, rec, outW, errW);
            Check("cli: a failed log write is a note on stderr", code == Program.ExitOk && errW.ToString().TrimEnd() == CleanRecorder.LogFailedNote
                && Lines(outW).Count == 1, outW + "|" + errW);

            // An append that throws must not break the clean.
            fake = new FakeClean();
            rec = new CleanRecorder(CleanRecorder.CliSource, true, l => { throw new IOException("disk"); }, () => T0);
            code = -1;
            try { code = Program.CleanOnce(Options.Parse(new[] { "clean" }), CancellationToken.None, fake.Clean, rec, new StringWriter(), new StringWriter()); }
            catch (Exception) { code = -2; }
            Check("cli: a throwing log write never breaks the clean", code == Program.ExitOk, code.ToString());

            // A lock note goes to stderr in --json mode, so stdout stays one object.
            fake = new FakeClean();
            rec = new CleanRecorder(CleanRecorder.CliSource, false, null, () => T0);
            outW = new StringWriter();
            errW = new StringWriter();
            Func<CleanRequest, CleanReport> noted = req => { CleanReport r = fake.Clean(req); r.LockNote = "ran without the clean lock"; return r; };
            Program.CleanOnce(Options.Parse(new[] { "clean", "--json" }), CancellationToken.None, noted, rec, outW, errW);
            Check("cli: --json clean puts the lock note on stderr", Lines(outW).Count == 1 && errW.ToString().Contains("note: ran without the clean lock"),
                outW + "|" + errW);

            Check("cli: progress lines go to stdout without --json", Program.InfoWriter(Options.Parse(new[] { "clean" }), Console.Out, Console.Error) == Console.Out);
            Check("cli: progress lines go to stderr with --json", Program.InfoWriter(Options.Parse(new[] { "clean", "--json" }), Console.Out, Console.Error) == Console.Error);
        }

        // ---------------------------------------------------------------- watch

        static void WatchTests()
        {
            var o = Options.Parse(new[] { "watch", "--json", "--threshold", "85", "--interval", "10", "--cooldown", "0" });
            int tick = 0, cleans = 0;
            var appended = new List<string>();
            var outW = new StringWriter();
            var errW = new StringWriter();
            var wakes = new[] { WakeReason.LowMemory, WakeReason.Stop };
            var w = new Watcher
            {
                Sample = () => Snap(95),
                Clean = a => { cleans++; return Report(CleanStop.None); },
                Elapsed = () => TimeSpan.FromSeconds(tick * 10),
                Now = () => new DateTime(2026, 11, 17, 12, 0, 0).AddSeconds(tick * 10),
                Wait = (t, low) => wakes[Math.Min(tick++, wakes.Length - 1)],
                Recorder = new CleanRecorder(CleanRecorder.WatchSource, true, l => { appended.Add(l); return true; }, () => T0)
            };
            int code = w.Run(o, outW, errW);
            List<string> lines = Lines(outW);
            bool allRecords = lines.Count == cleans && cleans > 0;
            foreach (string l in lines)
            {
                Dictionary<string, object> d = Parse(l);
                allRecords &= d != null && (string)d["type"] == "clean" && (string)d["source"] == "watch";
            }
            Check("watch: --json prints one record per clean and nothing else", code == Program.ExitOk && allRecords && cleans == 2, outW.ToString());
            Check("watch: --json sends its text lines to stderr", errW.ToString().Contains("cleaning") && errW.ToString().Contains("Stopped."), errW.ToString());
            Check("watch: logs each clean by default", appended.Count == 2 && lines.Count == 2 && appended[0] == lines[0] && appended[1] == lines[1],
                string.Join(" | ", appended));
            Check("watch: reason is threshold, or low-memory after a low wake", appended.Count == 2
                && (string)Parse(appended[0])["reason"] == "threshold" && (string)Parse(appended[1])["reason"] == "low-memory",
                string.Join(" | ", appended));

            // --no-log in text mode: the text report as before, no record.
            tick = 0;
            int calls = 0;
            outW = new StringWriter();
            w = new Watcher
            {
                Sample = () => Snap(95),
                Clean = a => Report(CleanStop.None),
                Elapsed = () => TimeSpan.FromSeconds(tick * 10),
                Now = () => new DateTime(2026, 11, 17, 12, 0, 0).AddSeconds(tick * 10),
                WaitStop = t => ++tick >= 2,
                Recorder = new CleanRecorder(CleanRecorder.WatchSource, false, l => { calls++; return true; }, () => T0)
            };
            w.Run(Options.Parse(new[] { "watch", "--no-log", "--cooldown", "0" }), outW, new StringWriter());
            Check("watch: --no-log writes no record", calls == 0);
            Check("watch: text mode still prints the report", outW.ToString().Contains("In use: ") && !outW.ToString().Contains("{\"v\""), outW.ToString());

            // Busy turns are recorded as busy; a failing log is one note, not one per clean.
            tick = 0;
            appended.Clear();
            outW = new StringWriter();
            errW = new StringWriter();
            w = new Watcher
            {
                Sample = () => Snap(95),
                Clean = a => { MemorySnapshot s = Snap(95); return new CleanReport { Before = s, After = s, Stopped = CleanStop.Busy }; },
                Elapsed = () => TimeSpan.FromSeconds(tick * 10),
                Now = () => new DateTime(2026, 11, 17, 12, 0, 0).AddSeconds(tick * 10),
                WaitStop = t => ++tick >= 3,
                Recorder = new CleanRecorder(CleanRecorder.WatchSource, true, l => { appended.Add(l); return false; }, () => T0)
            };
            code = w.Run(Options.Parse(new[] { "watch", "--json", "--cooldown", "0" }), outW, errW);
            lines = Lines(outW);
            int notes = 0;
            foreach (string l in Lines(errW)) if (l == CleanRecorder.LogFailedNote) notes++;
            Check("watch: busy turns are recorded as busy", code == Program.ExitOk && appended.Count == 3 && lines.Count == 3
                && (string)Parse(lines[0])["stopped"] == "busy", outW.ToString());
            Check("watch: a failing log is noted once", notes == 1, errW.ToString());

            // No recorder (as older callers build it): --json still prints records, nothing is logged.
            tick = 0;
            outW = new StringWriter();
            w = new Watcher
            {
                Sample = () => Snap(95),
                Clean = a => Report(CleanStop.None),
                Elapsed = () => TimeSpan.FromSeconds(tick * 10),
                Now = () => new DateTime(2026, 11, 17, 12, 0, 0).AddSeconds(tick * 10),
                WaitStop = t => ++tick >= 1
            };
            w.Run(Options.Parse(new[] { "watch", "--json" }), outW, new StringWriter());
            lines = Lines(outW);
            Check("watch: --json without a recorder still prints records", lines.Count == 1 && Parse(lines[0]) != null, outW.ToString());

            // RunWatch's clean: no lock wait, the record appended under the clean lock, and the in-clean flag set
            // only while the clean runs (the Ctrl+C handler reads it).
            var fake = new FakeClean();
            var watchLines = new List<string>();
            bool appendedInLock = false;
            var flags = new List<bool>();
            var rec = new CleanRecorder(CleanRecorder.WatchSource, true, l => { watchLines.Add(l); appendedInLock = fake.InLock; return true; }, () => T0);
            var wo = Options.Parse(new[] { "watch", "--timeout", "40" });
            Func<Areas, CleanReport> watchClean = Program.WatchClean(wo, CancellationToken.None, rec, fake.Clean, on => flags.Add(on));
            CleanReport wr = watchClean(Areas.WorkingSets);
            Check("watch clean: the request has no lock wait and the areas asked for", fake.Request != null
                && fake.Request.LockWait == TimeSpan.Zero && fake.Request.Areas == Areas.WorkingSets
                && fake.Request.Deadline == TimeSpan.FromSeconds(40));
            Check("watch clean: the record is appended under the clean lock", watchLines.Count == 1 && appendedInLock, watchLines.Count.ToString());
            Check("watch clean: the report is returned", wr != null);
            Check("watch clean: in-clean is set, then cleared", flags.Count == 2 && flags[0] && !flags[1], flags.Count.ToString());
            flags.Clear();
            Func<Areas, CleanReport> throwing = Program.WatchClean(wo, CancellationToken.None, rec,
                req => { throw new InvalidOperationException("x"); }, on => flags.Add(on));
            try { throwing(Areas.WorkingSets); } catch (InvalidOperationException) { }
            Check("watch clean: in-clean is cleared when the clean throws", flags.Count == 2 && !flags[1]);
        }

        // ---------------------------------------------------------------- help

        static void HelpTests()
        {
            string help = Program.HelpText();
            Check("help: documents --json", help.Contains("--json"));
            Check("help: documents --no-log", help.Contains("--no-log"));
            Check("help: names the clean log", help.Contains("cleans.log"));
            Check("help: dry run shows --json", help.Contains("ramopt clean --dry-run [--areas LIST | --all] [--json]"), help);
            Check("help: watch shows --no-log and --json", help.Contains("[--no-log] [--json]")
                && help.IndexOf("ramopt watch", StringComparison.Ordinal) >= 0
                && help.IndexOf("[--no-log] [--json]", help.IndexOf("ramopt watch", StringComparison.Ordinal), StringComparison.Ordinal) > 0
                && help.IndexOf("[--no-log] [--json]", help.IndexOf("ramopt watch", StringComparison.Ordinal), StringComparison.Ordinal)
                    < help.IndexOf("clean whenever load", StringComparison.Ordinal), help);
            Check("help: no usage line is wider than 120", MaxLineWidth(help) <= 120, MaxLineWidth(help).ToString());
        }

        static int MaxLineWidth(string text)
        {
            int max = 0;
            foreach (string line in text.Split('\n')) max = Math.Max(max, line.TrimEnd('\r').Length);
            return max;
        }
    }
}
