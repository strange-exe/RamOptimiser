// Tests for package 3A: the automatic clean's time limit and the clean lock as the app sees them (E9, E22), the
// recent-clean history with its five-minute rebound and the clean log records the app writes (R4+E21), and the
// activity list, tray tooltip line and tray status item (P7). Services run on fake clocks with fake cleans;
// nothing here cleans memory, takes the real clean lock or writes outside a temp folder.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RamOptimiser.App;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class ActivityHistoryTests
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
            HistoryTests();
            ServiceDeadlineTests();
            ServiceBusyTests();
            ServiceHistoryTests();
            CleanRecordTests();
            SummaryTests();
            SanitiseTests();
            ActivityListTests();
            ErrorGateTests();
            EventTests();
            TooltipTests();
            TrayStatusTests();
            MenuLineTests();
            ResultLineTests();
            return failed;
        }

        static readonly DateTime T0 = new DateTime(2026, 11, 17, 14, 32, 0, DateTimeKind.Utc);
        const ulong GB = 1024UL * 1024 * 1024;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static MemorySnapshot Snap(uint load)
        {
            return new MemorySnapshot { TotalPhys = 100, AvailablePhys = 100 - load, LoadPercent = load };
        }

        /// <summary>A 16 GB PC at the given load and bytes in use.</summary>
        static MemorySnapshot Big(uint load, ulong inUse)
        {
            return new MemorySnapshot { TotalPhys = 16 * GB, AvailablePhys = 16 * GB - inUse, LoadPercent = load };
        }

        static CleanReport Ran(uint before, uint after)
        {
            return new CleanReport { Before = Snap(before), After = Snap(after) };
        }

        static CleanReport Busy(uint load)
        {
            MemorySnapshot s = Snap(load);
            return new CleanReport { Before = s, After = s, Stopped = CleanStop.Busy };
        }

        static CleanHistoryEntry Entry(string id, DateTime end, CleanReport r)
        {
            return new CleanHistoryEntry { Id = id, EndUtc = end, Reason = CleanReason.Threshold, Report = r };
        }

        // ------------------------------------------------------------------ CleanHistory (R4+E21)

        static void HistoryTests()
        {
            var h = new CleanHistory();
            for (int i = 0; i < 25; i++) h.Add(Entry(i.ToString(Inv), T0.AddMinutes(i), Ran(90, 70)));
            List<CleanHistoryEntry> all = h.Snapshot();
            Check("history: keeps the last 20", all.Count == CleanHistory.Capacity && CleanHistory.Capacity == 20, all.Count.ToString(Inv));
            Check("history: newest first", all[0].Id == "24" && all[19].Id == "5", all[0].Id + " .. " + all[19].Id);

            h = new CleanHistory();
            h.Add(Entry("a", T0, Ran(90, 70)));
            List<CleanHistoryEntry> due = h.DueRebound(T0 + TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1), 75);
            Check("history: no rebound before 5 minutes", due.Count == 0, due.Count.ToString(Inv));
            due = h.DueRebound(T0 + TimeSpan.FromMinutes(5), 75);
            Check("history: rebound due after 5 minutes", due.Count == 1 && due[0].Id == "a" && due[0].LoadLater == 75,
                due.Count.ToString(Inv));
            Check("history: rebound due only once", h.DueRebound(T0 + TimeSpan.FromMinutes(6), 80).Count == 0);
            Check("history: rebound kept on the entry", h.Snapshot()[0].LoadLater == 75);

            h = new CleanHistory();
            h.Add(Entry("a", T0, Ran(90, 70)));
            Check("history: a reading long after five minutes is not a rebound",
                h.DueRebound(T0 + TimeSpan.FromMinutes(30), 75).Count == 0 && h.Snapshot()[0].LoadLater == null);
            Check("history: a missed rebound is not offered later", h.DueRebound(T0 + TimeSpan.FromMinutes(31), 75).Count == 0);

            h = new CleanHistory();
            h.Add(Entry("a", T0, Ran(90, 70)));
            h.Add(Entry("b", T0 + TimeSpan.FromMinutes(2), Ran(88, 72)));
            due = h.DueRebound(T0 + TimeSpan.FromMinutes(5), 75);
            Check("history: a later clean cancels the earlier rebound", due.Count == 0, due.Count.ToString(Inv));
            due = h.DueRebound(T0 + TimeSpan.FromMinutes(7), 76);
            Check("history: the later clean gets its own rebound", due.Count == 1 && due[0].Id == "b");

            h = new CleanHistory();
            h.Add(Entry("busy", T0, Busy(90)));
            List<CleanHistoryEntry> snap = h.Snapshot();
            Check("history: busy clean recorded as skipped", snap.Count == 1 && snap[0].Stopped == CleanStop.Busy && !snap[0].Ran);
            Check("history: busy clean gets no rebound", h.DueRebound(T0 + TimeSpan.FromMinutes(5), 75).Count == 0);

            h = new CleanHistory();
            h.Add(new CleanHistoryEntry { Id = "x", EndUtc = T0, Reason = CleanReason.Manual, Report = null, Error = "boom" });
            Check("history: a clean that threw is recorded", h.Snapshot().Count == 1 && h.Snapshot()[0].Error == "boom");
            Check("history: a clean that threw gets no rebound", h.DueRebound(T0 + TimeSpan.FromMinutes(5), 75).Count == 0);

            h = new CleanHistory();
            h.Add(Entry("a", T0, Ran(90, 70)));
            h.Snapshot()[0].LoadLater = 1;
            Check("history: snapshot is a copy", h.Snapshot()[0].LoadLater == null);
            Check("history: freed and loads come from the report",
                h.Snapshot()[0].LoadBefore == 90 && h.Snapshot()[0].LoadAfter == 70 && h.Snapshot()[0].Freed == 20);

            h = new CleanHistory();
            h.Add(Entry("a", T0, Ran(90, 70)));
            Check("history: a clock set back drops the rebound", h.DueRebound(T0 - TimeSpan.FromHours(1), 75).Count == 0
                && h.DueRebound(T0 + TimeSpan.FromMinutes(5), 75).Count == 0);

            bool threw = false;
            try { new CleanHistory().Add(null); }
            catch (ArgumentNullException) { threw = true; }
            Check("history: refuses a null entry", threw);
        }

        // ------------------------------------------------------------------ the service: deadline (E9)

        /// <summary>A service on a fake clock whose clean records the job it was given and returns Report.</summary>
        sealed class JobRig : IDisposable
        {
            public DateTime Now = T0;
            public uint Load = 10;
            public CleanReport Report = Ran(90, 60);
            public readonly List<CleanJob> Jobs = new List<CleanJob>();
            public readonly AutoCleanService Svc;

            public JobRig(AppSettings s, TimeSpan autoDeadline)
            {
                Svc = new AutoCleanService(() => Snap(Load), job => { Jobs.Add(job); return Report; }, () => Now,
                    null, null, null, autoDeadline);
                Svc.Update(s);
            }

            public void Dispose() { Svc.Dispose(); }
        }

        static AppSettings Threshold85() { return new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300 }; }
        static AppSettings Interval10() { return new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 99, IntervalMinutes = 10 }; }

        static void ServiceDeadlineTests()
        {
            Check("auto-clean: the time limit is 120 s", Cleaner.AutoCleanDeadline == TimeSpan.FromSeconds(120));
            using (var rig = new JobRig(Threshold85(), Cleaner.AutoCleanDeadline))
            {
                rig.Load = 95;
                rig.Svc.RunTick();
                Check("auto-clean: a clean for high load gets the 120 s deadline",
                    rig.Jobs.Count == 1 && rig.Jobs[0].Deadline == TimeSpan.FromSeconds(120) && rig.Jobs[0].Reason == CleanReason.Threshold,
                    rig.Jobs.Count.ToString(Inv));
                rig.Svc.RunManualCleanForTests();
                Check("auto-clean: a manual clean has no deadline",
                    rig.Jobs.Count == 2 && rig.Jobs[1].Deadline == null && rig.Jobs[1].Reason == CleanReason.Manual);
                Check("auto-clean: each clean gets its own 8-character id",
                    rig.Jobs[0].Id != null && rig.Jobs[0].Id.Length == 8 && rig.Jobs[0].Id != rig.Jobs[1].Id);
                Check("auto-clean: the job carries the settings' areas", rig.Jobs[0].Areas == Threshold85().CleanAreas);
            }
            using (var rig = new JobRig(Interval10(), TimeSpan.FromSeconds(7)))
            {
                rig.Now = T0 + TimeSpan.FromMinutes(10);
                rig.Svc.RunTick();
                Check("auto-clean: a scheduled clean gets the deadline it was built with",
                    rig.Jobs.Count == 1 && rig.Jobs[0].Deadline == TimeSpan.FromSeconds(7) && rig.Jobs[0].Reason == CleanReason.Interval);
            }

            // The old constructors still work and still see the areas.
            Areas seen = Areas.None;
            using (var svc = new AutoCleanService(() => Snap(10), a => { seen = a; return Ran(90, 60); }, () => T0))
            {
                svc.Update(Threshold85());
                svc.RunManualCleanForTests();
                Check("auto-clean: the areas-only constructor still cleans", seen == Threshold85().CleanAreas);
            }

            bool threw = false;
            try { new AutoCleanService(() => Snap(10), (Func<CleanJob, CleanReport>)null, () => T0, null, null, null, Cleaner.AutoCleanDeadline); }
            catch (ArgumentNullException) { threw = true; }
            Check("auto-clean: the job constructor refuses a null clean", threw);
        }

        // ------------------------------------------------------------------ the service: busy (E22)

        static void ServiceBusyTests()
        {
            using (var rig = new JobRig(Threshold85(), Cleaner.AutoCleanDeadline))
            {
                rig.Load = 95;
                rig.Report = Busy(95);
                rig.Svc.RunTick();
                Check("auto-clean: a busy clean does not count as futile", rig.Svc.FutileStreak == 0, rig.Svc.FutileStreak.ToString(Inv));
            }
            using (var rig = new JobRig(Threshold85(), Cleaner.AutoCleanDeadline))
            {
                // Control: the same load after a clean that ran does count, so the test above is not vacuous.
                rig.Load = 95;
                rig.Report = Ran(95, 95);
                rig.Svc.RunTick();
                Check("auto-clean: a clean that ran and left load high counts as futile", rig.Svc.FutileStreak == 1,
                    rig.Svc.FutileStreak.ToString(Inv));
            }
            using (var rig = new JobRig(Threshold85(), Cleaner.AutoCleanDeadline))
            {
                rig.Load = 95;
                rig.Report = Ran(95, 95);
                rig.Svc.RunTick();
                rig.Report = Busy(95);
                rig.Now = T0 + TimeSpan.FromHours(1);
                rig.Svc.RunTick();
                Check("auto-clean: a busy clean keeps the streak it found", rig.Svc.FutileStreak == 1, rig.Svc.FutileStreak.ToString(Inv));
            }
        }

        // ------------------------------------------------------------------ the service: history and rebound

        static void ServiceHistoryTests()
        {
            using (var rig = new JobRig(Threshold85(), Cleaner.AutoCleanDeadline))
            {
                var rebounds = new List<KeyValuePair<CleanHistoryEntry, MemorySnapshot>>();
                rig.Svc.Rebound += (e, s) => rebounds.Add(new KeyValuePair<CleanHistoryEntry, MemorySnapshot>(e, s));
                rig.Load = 95;
                rig.Report = Ran(95, 70);
                rig.Svc.RunTick();
                List<CleanHistoryEntry> h = rig.Svc.History;
                Check("auto-clean: the clean is in the history", h.Count == 1 && h[0].Reason == CleanReason.Threshold
                    && h[0].Id == rig.Jobs[0].Id && h[0].EndUtc == T0, h.Count.ToString(Inv));

                rig.Load = 72;
                rig.Now = T0 + TimeSpan.FromMinutes(4);
                rig.Svc.RunTick();
                Check("auto-clean: no rebound before five minutes", rebounds.Count == 0);
                rig.Load = 79;
                rig.Now = T0 + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
                rig.Svc.RunTick();
                Check("auto-clean: rebound event fires on the first sample at or after 5 minutes",
                    rebounds.Count == 1 && rebounds[0].Key.Id == h[0].Id && rebounds[0].Key.LoadLater == 79
                    && rebounds[0].Value != null && rebounds[0].Value.LoadPercent == 79, rebounds.Count.ToString(Inv));
                rig.Now = T0 + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(3);
                rig.Svc.RunTick();
                Check("auto-clean: the rebound fires once", rebounds.Count == 1);
                Check("auto-clean: the history shows the rebound", rig.Svc.History[0].LoadLater == 79);

                rig.Report = null;
                rig.Svc.RunManualCleanForTests();
                Check("auto-clean: a manual clean is in the history too",
                    rig.Svc.History.Count == 2 && rig.Svc.History[0].Reason == CleanReason.Manual);
            }

            using (var svc = new AutoCleanService(() => Snap(10), job => { throw new InvalidOperationException("Access is denied."); },
                () => T0, null, null, null, Cleaner.AutoCleanDeadline))
            {
                svc.Update(Threshold85());
                svc.RunManualCleanForTests();
                List<CleanHistoryEntry> h = svc.History;
                Check("auto-clean: a clean that threw is in the history with its message",
                    h.Count == 1 && h[0].Report == null && h[0].Error == "Access is denied.");
            }
        }

        // ------------------------------------------------------------------ the records the app writes (R4+E21, E22)

        static void CleanRecordTests()
        {
            Check("record: reason names", GuiCleanLog.ReasonName(CleanReason.Manual) == "manual"
                && GuiCleanLog.ReasonName(CleanReason.Threshold) == "threshold" && GuiCleanLog.ReasonName(CleanReason.Interval) == "interval");
            Check("record: stop names", GuiCleanLog.StopName(CleanStop.None) == "none" && GuiCleanLog.StopName(CleanStop.Cancelled) == "cancelled"
                && GuiCleanLog.StopName(CleanStop.TimedOut) == "timed-out" && GuiCleanLog.StopName(CleanStop.Busy) == "busy");

            var job = new CleanJob { Areas = Areas.WorkingSets | Areas.LowPriorityStandby, Reason = CleanReason.Threshold,
                Deadline = TimeSpan.FromSeconds(120), Id = "7f3a9c2e" };
            var services = new List<string> { "SysMain" };
            var lines = new List<string>();
            bool appendedUnderLock = false, underLock = false;
            CleanRequest seen = null;
            CleanReport report = GuiCleanLog.Run(job, services, req =>
            {
                seen = req;
                var r = Ran(91, 78);
                r.Stopped = CleanStop.TimedOut;
                underLock = true;
                if (req.UnderLock != null) req.UnderLock(r);
                appendedUnderLock = lines.Count == 1;
                underLock = false;
                return r;
            }, line => lines.Add(line), () => T0);
            Check("record: the request carries the job", seen != null && seen.Areas == job.Areas && ReferenceEquals(seen.Services, services)
                && seen.Deadline == job.Deadline && seen.LockWait == TimeSpan.Zero);
            Check("record: written under the clean lock", appendedUnderLock && lines.Count == 1, lines.Count.ToString(Inv));
            string line0 = lines.Count > 0 ? lines[0] : "";
            Check("record: one gui clean record", line0.Contains("\"type\":\"clean\"") && line0.Contains("\"id\":\"7f3a9c2e\"")
                && line0.Contains("\"source\":\"gui\"") && line0.Contains("\"reason\":\"threshold\"") && line0.Contains("\"stopped\":\"timed-out\"")
                && line0.Contains("\"time\":\"2026-11-17T14:32:00Z\""), line0);
            Check("record: the report is returned", report != null && report.Stopped == CleanStop.TimedOut);
            Check("record: unused flag", !underLock);

            lines.Clear();
            report = GuiCleanLog.Run(job, null, req => Busy(90), line => lines.Add(line), () => T0);
            Check("record: a busy skip is logged once, after the lock", lines.Count == 1 && lines[0].Contains("\"stopped\":\"busy\""),
                lines.Count.ToString(Inv));

            lines.Clear();
            report = GuiCleanLog.Run(job, null, req => { req.UnderLock(Ran(90, 80)); return Ran(90, 80); },
                line => { throw new System.IO.IOException("disk full"); }, () => T0);
            Check("record: a failing append never breaks the clean", report != null && report.After.LoadPercent == 80);

            report = GuiCleanLog.Run(job, null, req => null, line => lines.Add(line), () => T0);
            Check("record: a clean with no report logs nothing", report == null && lines.Count == 0);

            string ev = ActivityLog.EventRecord(T0, new ActivityEvent { Kind = "paused", Text = "Auto-clean paused \"until\"\nlater." });
            Check("record: an event is one JSON line", ev.IndexOf('\n') < 0 && ev.IndexOf('\r') < 0
                && ev.StartsWith("{\"v\":1,\"type\":\"event\",\"time\":\"2026-11-17T14:32:00Z\",\"source\":\"gui\",\"event\":\"paused\",\"text\":", StringComparison.Ordinal)
                && ev.Contains("\\\"until\\\""), ev);
            string longText = new string('a', 600);
            string capped = ActivityLog.EventRecord(T0, new ActivityEvent { Kind = "read-failed", Text = longText });
            Check("record: event text capped at 200", capped.Contains("\"" + new string('a', CleanLog.MaxTextChars) + "\"")
                && !capped.Contains(new string('a', CleanLog.MaxTextChars + 1)), capped.Length.ToString(Inv));
        }

        // ------------------------------------------------------------------ the Recent cleans rows (P7, R4)

        static CleanReport Report(uint before, ulong inUseBefore, uint after, ulong inUseAfter, params AreaResult[] results)
        {
            var r = new CleanReport { Before = Big(before, inUseBefore), After = Big(after, inUseAfter) };
            r.Results.AddRange(results);
            return r;
        }

        static AreaResult Ok(Areas a) { return new AreaResult { Area = a, Ok = true }; }

        static void SummaryTests()
        {
            ulong before = 13 * GB, after = before - 1288490189UL; // 1.2 GB freed
            CleanReport r = Report(91, before, 78, after, Ok(Areas.WorkingSets), Ok(Areas.SystemFileCache), Ok(Areas.LowPriorityStandby),
                new AreaResult { Area = Areas.VolumeCache, Ok = false, Message = "Access is denied." });
            string s = ActivityLog.CleanSummary(CleanReason.Threshold, r, null, null);
            Check("activity: clean line", s == "Auto-clean (high load): freed 1.2 GB; load 91% to 78%; 3 of 4 areas ok; "
                + "Drive write cache (writes to disk) failed: access is denied", s);

            r = Report(91, before, 78, after, Ok(Areas.WorkingSets));
            s = ActivityLog.CleanSummary(CleanReason.Manual, r, null, 80);
            Check("activity: the rebound is in the row", s == "Clean: freed 1.2 GB; load 91% to 78%, 80% five minutes later; 1 of 1 area ok", s);

            s = ActivityLog.CleanSummary(CleanReason.Interval, Report(50, before, 50, before), null, null);
            Check("activity: freed nothing", s == "Auto-clean (schedule): freed nothing; load 50% to 50%; 0 of 0 areas ok", s);

            s = ActivityLog.CleanSummary(CleanReason.Manual, null, "Access is denied.", null);
            Check("activity: did not finish", s == "Clean: did not finish: access is denied", s);
            s = ActivityLog.CleanSummary(CleanReason.Manual, null, null, null);
            Check("activity: did not finish, no message", s == "Clean: did not finish: unknown error", s);

            s = ActivityLog.CleanSummary(CleanReason.Threshold, Busy(90), null, null);
            Check("activity: busy", s == "Auto-clean (high load): skipped, another RamOptimiser clean was running", s);

            r = Report(81, before, 79, before - GB / 2, Ok(Areas.WorkingSets), Ok(Areas.SystemFileCache), Ok(Areas.LowPriorityStandby),
                NotRun(Areas.ModifiedList), NotRun(Areas.VolumeCache), NotRun(Areas.RegistryCache));
            r.Stopped = CleanStop.TimedOut;
            r.Deadline = TimeSpan.FromSeconds(120);
            s = ActivityLog.CleanSummary(CleanReason.Threshold, r, null, null);
            Check("activity: hit the time limit", s == "Auto-clean (high load): freed 512.0 MB; load 81% to 79%; hit its 2-minute limit after 3 of 6 areas", s);

            r = Report(81, before, 79, before, new AreaResult { Area = Areas.VolumeCache, Ok = true, Partial = true, Message = "flushed 1 of 2 drives; E: refused" });
            s = ActivityLog.CleanSummary(CleanReason.Manual, r, null, null);
            Check("activity: partly worked", s == "Clean: freed nothing; load 81% to 79%; 0 of 1 area ok; Drive write cache (writes to disk) partly worked: flushed 1 of 2 drives; E: refused", s);

            var entry = new CleanHistoryEntry { Id = "a", EndUtc = T0, Reason = CleanReason.Manual, Report = Report(91, before, 78, after, Ok(Areas.WorkingSets)), LoadLater = 80 };
            DateTime local = T0.ToLocalTime();
            string row = ActivityLog.CleanRow(entry, local, Inv);
            Check("activity: a row starts with the time", row == local.ToString("t", Inv) + "  " + ActivityLog.CleanSummary(CleanReason.Manual, entry.Report, null, 80), row);
            row = ActivityLog.CleanRow(entry, local.AddDays(1), Inv);
            Check("activity: a row from yesterday says so", row.StartsWith("Yesterday " + local.ToString("t", Inv) + "  ", StringComparison.Ordinal), row);
        }

        static AreaResult NotRun(Areas a)
        {
            return new AreaResult { Area = a, Skipped = true, NotRun = true, Message = "not run: time limit reached" };
        }

        static void SanitiseTests()
        {
            string s = ActivityLog.Sanitise("one\r\ntwo\tthree");
            Check("activity: one line", s == "one  two three", s);
            string bidi = "a" + char.ConvertFromUtf32(0x202E) + "b" + char.ConvertFromUtf32(0x200B) + "c" + char.ConvertFromUtf32(7) + "d";
            s = ActivityLog.Sanitise(bidi);
            Check("activity: format characters dropped", s == "abcd", s);
            s = ActivityLog.Sanitise(new string('x', 600));
            Check("activity: capped", s.Length == ActivityLog.MaxLine && ActivityLog.MaxLine == 500, s.Length.ToString(Inv));
            string pair = char.ConvertFromUtf32(0x1F600);
            s = ActivityLog.Sanitise(new string('x', ActivityLog.MaxLine - 1) + pair);
            Check("activity: a cap never splits a surrogate pair", s.Length == ActivityLog.MaxLine - 1, s.Length.ToString(Inv));
            Check("activity: null is empty", ActivityLog.Sanitise(null) == "");
            s = ActivityLog.CleanSummary(CleanReason.Manual, null, "line one\r\nline two", null);
            Check("activity: an error message stays on one line", s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0, s);
        }

        static void ActivityListTests()
        {
            var a = new ActivityHistory(20);
            for (int i = 0; i < 25; i++) a.Add("line " + i.ToString(Inv));
            IList<string> n = a.Newest();
            Check("activity history: newest 20, newest first", n.Count == 20 && n[0] == "line 24" && n[19] == "line 5",
                n.Count.ToString(Inv) + " " + (n.Count > 0 ? n[0] : ""));
            a.Add("bad\nline");
            Check("activity history: lines are made safe", a.Newest()[0] == "bad line", a.Newest()[0]);
            Check("activity history: empty at first", new ActivityHistory(20).Newest().Count == 0);
            string row = ActivityLog.Row(T0.ToLocalTime(), T0.ToLocalTime(), "Auto-clean resumed.", Inv);
            Check("activity: event row", row == T0.ToLocalTime().ToString("t", Inv) + "  Auto-clean resumed.", row);
        }

        static void ErrorGateTests()
        {
            Check("error gate: first is logged", ErrorGate.ShouldLog(null, null, "denied", T0));
            Check("error gate: same message after 5 minutes is not", !ErrorGate.ShouldLog("denied", T0, "denied", T0.AddMinutes(5)));
            Check("error gate: a different message is", ErrorGate.ShouldLog("denied", T0, "timeout", T0.AddMinutes(1)));
            Check("error gate: same message after 10 minutes is", ErrorGate.ShouldLog("denied", T0, "denied", T0.AddMinutes(10)));
            Check("error gate: a clock set back logs", ErrorGate.ShouldLog("denied", T0, "denied", T0.AddMinutes(-1)));
        }

        static void EventTests()
        {
            var off = new AutoCleanStatus();
            var paused = new AutoCleanStatus { Paused = true, PausedUntilUtc = T0 + TimeSpan.FromHours(1) };
            List<ActivityEvent> e = ActivityEvents.FromStatus(off, paused, false, 85, Inv);
            string until = (T0 + TimeSpan.FromHours(1)).ToLocalTime().ToString("t", Inv);
            Check("events: a pause", e.Count == 1 && e[0].Kind == "paused" && e[0].Text == "Auto-clean paused until " + until + ".",
                e.Count > 0 ? e[0].Text : "none");
            e = ActivityEvents.FromStatus(paused, off, true, 85, Inv);
            Check("events: a resume", e.Count == 1 && e[0].Kind == "resumed" && e[0].Text == "Auto-clean resumed.");
            e = ActivityEvents.FromStatus(paused, off, false, 85, Inv);
            Check("events: a pause that ended", e.Count == 1 && e[0].Kind == "pause-ended" && e[0].Text == "Auto-clean pause ended.");
            e = ActivityEvents.FromStatus(off, new AutoCleanStatus { FutileStreak = 1 }, false, 85, Inv);
            Check("events: back-off entered", e.Count == 1 && e[0].Kind == "backoff-started"
                && e[0].Text == "Auto-clean is waiting longer: the last clean left load at 85% or more.", e.Count > 0 ? e[0].Text : "none");
            e = ActivityEvents.FromStatus(new AutoCleanStatus { FutileStreak = 1 }, new AutoCleanStatus { FutileStreak = 2 }, false, 85, Inv);
            Check("events: a longer back-off is not news", e.Count == 0);
            e = ActivityEvents.FromStatus(new AutoCleanStatus { FutileStreak = 2 }, off, false, 85, Inv);
            Check("events: back-off left", e.Count == 1 && e[0].Kind == "backoff-ended" && e[0].Text == "Auto-clean is back to its normal wait.");
            Check("events: nothing changed", ActivityEvents.FromStatus(paused, paused, false, 85, Inv).Count == 0);
            Check("events: before the first status", ActivityEvents.FromStatus(null, off, false, 85, Inv).Count == 0);
            Check("events: a restored pause at start is logged", ActivityEvents.FromStatus(null, paused, false, 85, Inv).Count == 1);
            Check("events: read failed", ActivityEvents.ReadFailed("Access is denied.").Text == "Memory status could not be read: access is denied."
                && ActivityEvents.ReadFailed("x").Kind == "read-failed");
            Check("events: read again", ActivityEvents.ReadRecovered().Text == "Memory status can be read again.");
        }

        // ------------------------------------------------------------------ tray tooltip (P7)

        static LastCleanInfo Last(CleanReason reason, CleanReport report)
        {
            return new LastCleanInfo { Utc = T0, Reason = reason, Report = report };
        }

        static void TooltipTests()
        {
            MemorySnapshot s = Big(72, 11 * GB);
            Check("tray tip: unchanged without a status line", AppText.TrayTooltip(s, null) == AppText.TrayTooltip(s)
                && AppText.TrayTooltip(null, null) == AppText.TrayTooltip(null));
            Check("tray tip: two lines", AppText.TrayTooltip(s, "Auto-clean at 14:32 freed 1.2 GB") == "RAM Optimiser: 72%\nAuto-clean at 14:32 freed 1.2 GB",
                AppText.TrayTooltip(s, "Auto-clean at 14:32 freed 1.2 GB"));

            string longLine = "Auto-clean at 14:32 freed a great deal of memory and then some more words follow here";
            string tip = AppText.TrayTooltip(s, longLine);
            string second = tip.Substring(tip.IndexOf('\n') + 1);
            Check("tray tip: never cuts mid-word", tip.Length <= AppText.MaxTooltipLength && longLine.StartsWith(second, StringComparison.Ordinal)
                && second.Length < longLine.Length && longLine[second.Length] == ' ' && tip.StartsWith("RAM Optimiser: 72%\n", StringComparison.Ordinal), tip);
            tip = AppText.TrayTooltip(s, new string('w', 80));
            Check("tray tip: an unbreakable line is left out", tip == AppText.TrayTooltip(s), tip);
            tip = AppText.TrayTooltip(null, "Auto-clean paused: needs admin");
            Check("tray tip: no sample", tip == "RAM Optimiser: load unknown\nAuto-clean paused: needs admin", tip);

            MemorySnapshot full = Big(100, 15 * GB);
            ulong inUse = 15 * GB, freed = (ulong)(1023.9 * 1024 * 1024);
            var failedArea = new AreaResult { Area = Areas.VolumeCache, Ok = false, Message = "no" };
            var reports = new List<CleanReport>
            {
                null,
                Report(100, inUse, 99, inUse - freed, Ok(Areas.WorkingSets)),
                Report(100, inUse, 100, inUse, Ok(Areas.WorkingSets)),
                Report(100, inUse, 99, inUse - freed, failedArea, failedArea),
                Busy(100)
            };
            var timed = Report(100, inUse, 99, inUse - freed, Ok(Areas.WorkingSets));
            timed.Stopped = CleanStop.TimedOut;
            reports.Add(timed);
            var states = new[] { AutoCleanState.Off, AutoCleanState.NeedsAdmin, AutoCleanState.Paused, AutoCleanState.BackingOff, AutoCleanState.Armed };
            bool allFit = true, line1 = true;
            string worst = "";
            foreach (string name in new[] { "en-US", "es-MX", "ko-KR" })
            {
                var c = new CultureInfo(name);
                var st = new AutoCleanStatus { Paused = true, PausedUntilUtc = T0.AddMinutes(59), FutileStreak = 3 };
                foreach (AutoCleanState state in states)
                    foreach (CleanReason reason in new[] { CleanReason.Manual, CleanReason.Threshold, CleanReason.Interval })
                        foreach (CleanReport r in reports)
                            foreach (int days in new[] { 0, 1, 9 })
                            {
                                string line2 = TrayStatus.TooltipLine(state, st, Last(reason, r), T0.ToLocalTime().AddDays(days), c);
                                string t = AppText.TrayTooltip(full, line2);
                                if (t.Length > AppText.MaxTooltipLength) { allFit = false; worst = t; }
                                if (!t.StartsWith("RAM Optimiser: 100%", StringComparison.Ordinal)) { line1 = false; worst = t; }
                            }
            }
            Check("tray tip: within 63 for every status", allFit, worst);
            Check("tray tip: line 1 always present", line1, worst);
        }

        static void TrayStatusTests()
        {
            CultureInfo c = Inv;
            DateTime now = T0.ToLocalTime();
            string at = now.ToString("t", c);
            var paused = new AutoCleanStatus { Paused = true, PausedUntilUtc = T0.AddHours(1), FutileStreak = 2 };
            var backing = new AutoCleanStatus { FutileStreak = 2 };
            var armed = new AutoCleanStatus();
            CleanReport failedReport = Report(90, 10 * GB, 80, 9 * GB, Ok(Areas.WorkingSets),
                new AreaResult { Area = Areas.VolumeCache, Ok = false, Message = "no" }, new AreaResult { Area = Areas.RegistryCache, Ok = false, Message = "no" });
            LastCleanInfo failedClean = Last(CleanReason.Manual, failedReport);
            LastCleanInfo goodAuto = Last(CleanReason.Threshold, Report(90, 10 * GB, 80, 10 * GB - 1288490189UL, Ok(Areas.WorkingSets)));

            Check("tray status: needs admin beats paused",
                TrayStatus.TooltipLine(AutoCleanState.NeedsAdmin, paused, failedClean, now, c) == "Auto-clean paused: needs admin");
            string pausedLine = TrayStatus.TooltipLine(AutoCleanState.Paused, paused, failedClean, now, c);
            Check("tray status: paused beats failure", pausedLine == "Auto-clean paused until " + T0.AddHours(1).ToLocalTime().ToString("t", c), pausedLine);
            string fail = TrayStatus.TooltipLine(AutoCleanState.BackingOff, backing, failedClean, now, c);
            Check("tray status: failure beats backing off", fail == "Clean at " + at + ": 2 areas failed", fail);
            Check("tray status: backing off beats success",
                TrayStatus.TooltipLine(AutoCleanState.BackingOff, backing, goodAuto, now, c) == "Auto-clean slowed: cleans are not helping");
            string ok = TrayStatus.TooltipLine(AutoCleanState.Armed, armed, goodAuto, now, c);
            Check("tray status: success", ok == "Auto-clean at " + at + " freed 1.2 GB", ok);
            Check("tray status: nothing gives null", TrayStatus.TooltipLine(AutoCleanState.Armed, armed, null, now, c) == null
                && TrayStatus.TooltipLine(AutoCleanState.Off, null, null, now, c) == null);

            CleanReport partial = Report(90, 10 * GB, 80, 9 * GB, new AreaResult { Area = Areas.VolumeCache, Ok = true, Partial = true, Message = "E: refused" });
            string p = TrayStatus.TooltipLine(AutoCleanState.BackingOff, backing, Last(CleanReason.Threshold, partial), now, c);
            Check("tray status: partial counts as failure", p == "Auto-clean at " + at + ": 1 area partly failed", p);

            Check("tray status: did not finish", TrayStatus.TooltipLine(AutoCleanState.Armed, armed, Last(CleanReason.Interval, null), now, c)
                == "Auto-clean at " + at + " did not finish");
            CleanReport timed = Report(90, 10 * GB, 80, 9 * GB, Ok(Areas.WorkingSets));
            timed.Stopped = CleanStop.TimedOut;
            Check("tray status: hit the time limit is a failure", TrayStatus.TooltipLine(AutoCleanState.BackingOff, backing, Last(CleanReason.Threshold, timed), now, c)
                == "Auto-clean at " + at + " hit its time limit");
            Check("tray status: a busy skip is not a failure", TrayStatus.TooltipLine(AutoCleanState.BackingOff, backing, Last(CleanReason.Threshold, Busy(90)), now, c)
                == "Auto-clean slowed: cleans are not helping");
            Check("tray status: a busy skip", TrayStatus.TooltipLine(AutoCleanState.Armed, armed, Last(CleanReason.Threshold, Busy(90)), now, c)
                == "Auto-clean at " + at + " was skipped");
            Check("tray status: freed nothing", TrayStatus.TooltipLine(AutoCleanState.Off, null, Last(CleanReason.Manual, Report(50, GB, 50, GB)), now, c)
                == "Clean at " + at + " freed nothing");
            string older = TrayStatus.TooltipLine(AutoCleanState.Armed, armed, goodAuto, now.AddDays(1), c);
            Check("tray status: an older clean says when", older == "Auto-clean yesterday at " + at + " freed 1.2 GB", older);
        }

        static void MenuLineTests()
        {
            CultureInfo c = Inv;
            DateTime local = T0.ToLocalTime();
            CleanReport r = Report(90, 10 * GB, 80, 10 * GB - 1288490189UL, Ok(Areas.WorkingSets));
            Check("menu line: none before the first clean", TrayStatus.MenuLine(null, local, c) == null);
            Check("menu line: auto-clean uses LastAutoClean", TrayStatus.MenuLine(Last(CleanReason.Threshold, r), local, c)
                == AppText.LastAutoClean(local, r, c));
            Check("menu line: manual uses LastClean", TrayStatus.MenuLine(Last(CleanReason.Manual, r), local, c)
                == AppText.LastClean(local, local, r, false, c));
            CleanReport b = Busy(90);
            Check("menu line: a busy skip says so", TrayStatus.MenuLine(Last(CleanReason.Interval, b), local, c)
                == AppText.StoppedCleanLine(local, local, b, true, c));
            string older = TrayStatus.MenuLine(Last(CleanReason.Threshold, r), local.AddDays(1), c);
            Check("menu line: an older auto-clean says when", older.StartsWith("Last auto-clean yesterday at ", StringComparison.Ordinal), older);
            var withError = new LastCleanInfo { Utc = T0, Reason = CleanReason.Manual, Report = null, Error = new InvalidOperationException("Access is denied.") };
            string err = TrayStatus.MenuLine(withError, local, c);
            Check("menu line: a clean that threw gives the reason", err.EndsWith("The clean stopped: access is denied.", StringComparison.Ordinal), err);
        }

        static void ResultLineTests()
        {
            CultureInfo c = Inv;
            DateTime local = T0.ToLocalTime();
            CleanReport timed = Report(81, 10 * GB, 79, 9 * GB, Ok(Areas.WorkingSets), NotRun(Areas.StandbyList));
            timed.Stopped = CleanStop.TimedOut;
            timed.Deadline = TimeSpan.FromSeconds(120);
            string line = AppText.ResultLine(local, local, timed, null, true, c);
            Check("result line: a clean that hit its limit says so", line == "Auto-clean at " + local.ToString("t", c)
                + " hit its 2-minute limit after 1 of 2 areas. Load went from 81% to 79%.", line);
            Check("result line: a busy skip says so", AppText.ResultLine(local, local, Busy(90), null, false, c)
                == "Last clean at " + local.ToString("t", c) + " was skipped: another RamOptimiser clean was running.");
            Check("result line: a clean that hit its limit did not go well", !AppText.CleanWentWell(timed));
            Check("result line: a busy skip is not a failure", AppText.CleanWentWell(Busy(90)));
            CleanReport done = Report(81, 10 * GB, 79, 9 * GB, Ok(Areas.WorkingSets));
            Check("result line: a complete clean is unchanged", AppText.ResultLine(local, local, done, null, false, c)
                == AppText.LastClean(local, local, done, false, c));
        }
    }
}
