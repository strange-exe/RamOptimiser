// Package 7A (preview visuals): load history, per-part change, clean progress, tray flyout, Mica setting.
// Pure decisions only; the WPF parts render what these return. Registered by TestMain through reflection.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using RamOptimiser.App;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class VisualsTests
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
            ChartTests();
            ChartNameTests();
            ChangeTests();
            ProgressEngineTests();
            ProgressViewTests();
            PlacementTests();
            GateTests();
            FocusTests();
            StaggerTests();
            FlyoutTextTests();
            MicaTests();
            SourceTests();
            return failed;
        }

        static readonly DateTime T0 = new DateTime(2026, 11, 18, 14, 22, 0, DateTimeKind.Utc);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------------------------------------------------------- load history (ring buffer)

        static void HistoryTests()
        {
            var h = new LoadHistory();
            Check("history: empty at start", h.Samples(T0).Count == 0 && h.Cleans(T0).Count == 0);
            for (int i = 0; i <= 300; i++) h.Add(T0.AddSeconds(2 * i), 60 + (i % 10));
            DateTime now = T0.AddSeconds(600);
            List<LoadSample> s = h.Samples(now);
            Check("history: keeps ten minutes", s.Count > 0 && s.Count <= LoadHistory.Capacity
                && now - s[0].Utc <= LoadHistory.Window, s.Count.ToString());
            Check("history: oldest first", s.Count > 1 && s[0].Utc < s[s.Count - 1].Utc);
            Check("history: newest kept", s.Count > 0 && s[s.Count - 1].Utc == now);

            // Twenty minutes later, with one new sample: everything older than the window is gone.
            DateTime later = now.AddMinutes(20);
            h.Add(later, 70);
            s = h.Samples(later);
            Check("history: drops older samples", s.Count == 1 && s[0].Load == 70, s.Count.ToString());

            // Clean marks live in the same window.
            var c = new LoadHistory();
            c.Add(T0, 80);
            c.MarkClean(T0.AddMinutes(1));
            c.MarkClean(T0.AddMinutes(9));
            c.Add(T0.AddMinutes(12), 70);
            List<DateTime> cleans = c.Cleans(T0.AddMinutes(12));
            Check("history: keeps clean marks in window", cleans.Count == 1 && cleans[0] == T0.AddMinutes(9), cleans.Count.ToString());

            // A clock that goes back (time sync) restarts the history rather than drawing it backwards.
            var b = new LoadHistory();
            b.Add(T0, 50);
            b.Add(T0.AddSeconds(2), 51);
            b.MarkClean(T0.AddSeconds(2));
            b.Add(T0.AddSeconds(-30), 52);
            Check("history: clock going back starts again", b.Samples(T0.AddSeconds(-30)).Count == 1 && b.Cleans(T0.AddSeconds(-30)).Count == 0);

            var clamp = new LoadHistory();
            clamp.Add(T0, 140);
            clamp.Add(T0.AddSeconds(2), -5);
            List<LoadSample> cs = clamp.Samples(T0.AddSeconds(2));
            Check("history: load clamped to 0..100", cs.Count == 2 && cs[0].Load == 100 && cs[1].Load == 0);

            // Bounded: a flood of samples (extra ones after cleans) never grows past the capacity.
            var flood = new LoadHistory();
            for (int i = 0; i < 2000; i++) flood.Add(T0.AddMilliseconds(100 * i), 50);
            Check("history: bounded by capacity", flood.Samples(T0.AddMilliseconds(100 * 1999)).Count == LoadHistory.Capacity);
            for (int i = 0; i < 500; i++) flood.MarkClean(T0.AddMilliseconds(100 * 1999));
            Check("history: clean marks bounded", flood.Cleans(T0.AddMilliseconds(100 * 1999)).Count <= LoadHistory.MaxCleanMarks);
        }

        // ---------------------------------------------------------------- chart geometry

        static void ChartTests()
        {
            Check("chart: 100 at the top", Near(LoadChartMath.Y(100, 50, 64), 0));
            Check("chart: floor at the bottom", Near(LoadChartMath.Y(50, 50, 64), 64));
            Check("chart: maps load 50 to 100 to height", Near(LoadChartMath.Y(75, 50, 64), 32) && Near(LoadChartMath.Y(85, 50, 64), 64 - 0.7 * 64));
            Check("chart: clamps below the floor", Near(LoadChartMath.Y(30, 50, 64), 64));
            Check("chart: clamps above 100", Near(LoadChartMath.Y(130, 50, 64), 0));

            var high = new List<LoadSample> { new LoadSample { Utc = T0, Load = 70 }, new LoadSample { Utc = T0, Load = 92 } };
            var low = new List<LoadSample> { new LoadSample { Utc = T0, Load = 70 }, new LoadSample { Utc = T0, Load = 31 } };
            Check("chart: floor 50 while load stays at or above it", LoadChartMath.Floor(high) == 50);
            Check("chart: floor 0 once load dips below 50, so the line is never flattened", LoadChartMath.Floor(low) == 0);
            Check("chart: floor 50 with no samples", LoadChartMath.Floor(new List<LoadSample>()) == 50);

            DateTime now = T0.AddMinutes(10);
            Check("chart: now at the right edge", Near(LoadChartMath.X(now, now, 300), 300));
            Check("chart: ten minutes ago at the left edge", Near(LoadChartMath.X(T0, now, 300), 0));
            Check("chart: five minutes ago in the middle", Near(LoadChartMath.X(T0.AddMinutes(5), now, 300), 150));
            Check("chart: older than the window clamps to the left", Near(LoadChartMath.X(T0.AddMinutes(-3), now, 300), 0));

            List<int> labels = LoadChartMath.Labels(50, 85, 64, 14);
            Check("chart: labels 100, threshold, floor", Same(labels, 100, 85, 50), Join(labels));
            labels = LoadChartMath.Labels(50, 95, 64, 14);
            Check("chart: 100 label dropped next to a 95 threshold", Same(labels, 95, 50), Join(labels));
            labels = LoadChartMath.Labels(50, null, 64, 14);
            Check("chart: no threshold label when auto-clean is off", Same(labels, 100, 50), Join(labels));
            labels = LoadChartMath.Labels(50, 50, 64, 14);
            Check("chart: floor label dropped under a threshold at the floor", Same(labels, 100, 50), Join(labels));
            labels = LoadChartMath.Labels(0, 85, 64, 14);
            Check("chart: floor 0 keeps its label; 100 gives way to a close threshold", Same(labels, 85, 0), Join(labels));
            labels = LoadChartMath.Labels(0, 60, 64, 14);
            Check("chart: floor 0 with a low threshold keeps all three", Same(labels, 100, 60, 0), Join(labels));
        }

        // ---------------------------------------------------------------- the chart's text summary

        static void ChartNameTests()
        {
            var samples = new List<LoadSample>();
            Check("chart name: nothing yet", AppText.ChartName(samples, new List<DateTime>(), Inv) == "Load history: not enough readings yet.");
            samples.Add(new LoadSample { Utc = T0, Load = 76 });
            Check("chart name: one reading is not enough", AppText.ChartName(samples, new List<DateTime>(), Inv) == "Load history: not enough readings yet.");
            for (int i = 1; i <= 300; i++)
                samples.Add(new LoadSample { Utc = T0.AddSeconds(2 * i), Load = i == 150 ? 85 : i == 200 ? 69 : 75 });
            samples.Add(new LoadSample { Utc = T0.AddMinutes(10), Load = 71 });
            var cleans = new List<DateTime> { new DateTime(2026, 11, 18, 14, 23, 0), new DateTime(2026, 11, 18, 14, 32, 0) };
            string name = AppText.ChartName(samples, cleans, Inv);
            Check("chart name: says range, now and cleans", name == "Load over the last 10 minutes: now 71%, between 69% and 85%. Cleans at 14:23 and 14:32.", name);

            var steady = new List<LoadSample> { new LoadSample { Utc = T0, Load = 64 }, new LoadSample { Utc = T0.AddSeconds(50), Load = 64 } };
            name = AppText.ChartName(steady, new List<DateTime>(), Inv);
            Check("chart name: steady and under a minute", name == "Load over the last minute: steady at 64%. No cleans.", name);

            var three = new List<LoadSample> { new LoadSample { Utc = T0, Load = 60 }, new LoadSample { Utc = T0.AddMinutes(3), Load = 66 } };
            name = AppText.ChartName(three, new List<DateTime> { new DateTime(2026, 11, 18, 14, 23, 0) }, Inv);
            Check("chart name: one clean", name == "Load over the last 3 minutes: now 66%, between 60% and 66%. One clean, at 14:23.", name);

            var many = new List<DateTime>();
            for (int i = 0; i < 5; i++) many.Add(new DateTime(2026, 11, 18, 14, 20 + i, 0));
            name = AppText.ChartName(three, many, Inv);
            Check("chart name: many cleans counted", name.EndsWith(" 5 cleans, the last at 14:24."), name);
            var three3 = new List<DateTime> { many[0], many[1], many[2] };
            name = AppText.ChartName(three, three3, Inv);
            Check("chart name: three cleans listed", name.EndsWith(" Cleans at 14:20, 14:21 and 14:22."), name);

            Check("chart heading", AppText.ChartHeading == "Load, last 10 minutes");
            Check("chart edge times", AppText.ChartFrom(new DateTime(2026, 11, 18, 14, 32, 0), Inv) == "14:22"
                && AppText.ChartNow == "Now");
        }

        // ---------------------------------------------------------------- per-part change after a clean

        static void ChangeTests()
        {
            const ulong GB = 1UL << 30, MB = 1UL << 20;
            var before = new MemorySnapshot { TotalPhys = 16 * GB, AvailablePhys = 4 * GB, LoadPercent = 75, HasLists = true,
                FreeAndZero = 500 * MB, Standby = 1800 * MB + 2 * GB, Modified = 102 * MB + MB / 2 };
            var after = new MemorySnapshot { TotalPhys = 16 * GB, AvailablePhys = 6 * GB, LoadPercent = 62, HasLists = true,
                FreeAndZero = 900 * MB, Standby = 5 * GB + 512 * MB, Modified = 0 };
            var report = new CleanReport { Before = before, After = after };
            report.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            string[] c = PartChanges.From(report);
            Check("change: four parts in map order", c != null && c.Length == 4);
            if (c != null && c.Length == 4)
            {
                MemoryMap mb = MemoryMap.From(before), ma = MemoryMap.From(after);
                for (int i = 0; i < 4; i++)
                    Check("change: part " + (MapPart)i + " is the map's signed change", c[i] == AppText.Change(MemoryMap.Change(mb, ma, (MapPart)i)), c[i]);
                Check("change: modified dropped", c[(int)MapPart.Modified].StartsWith("-"), c[(int)MapPart.Modified]);
                Check("change: free grew", c[(int)MapPart.Free] == "+400.0 MB", c[(int)MapPart.Free]);
            }
            Check("change: none without a report", PartChanges.From(null) == null);
            Check("change: none for a busy skip (nothing ran)", PartChanges.From(new CleanReport { Before = before, After = before, Stopped = CleanStop.Busy }) == null);
            Check("change: none without an after snapshot", PartChanges.From(new CleanReport { Before = before }) == null);
            var twoPart = new MemorySnapshot { TotalPhys = 16 * GB, AvailablePhys = 6 * GB, LoadPercent = 62, HasLists = false };
            Check("change: none when the map's detail changed between snapshots", PartChanges.From(new CleanReport { Before = before, After = twoPart }) == null);
            var twoBefore = new MemorySnapshot { TotalPhys = 16 * GB, AvailablePhys = 4 * GB, LoadPercent = 75, HasLists = false };
            string[] two = PartChanges.From(new CleanReport { Before = twoBefore, After = twoPart });
            Check("change: a two-part map changes in use and available only", two != null && two[(int)MapPart.InUse] == "-2.0 GB"
                && two[(int)MapPart.Free] == "+2.0 GB" && two[(int)MapPart.Modified] == null && two[(int)MapPart.Standby] == null,
                two == null ? "null" : string.Join("|", two));

            string note = AppText.ChangeNote(new DateTime(2026, 11, 18, 14, 32, 0), Inv);
            Check("change note names the clean's time", note == "The + and - figures are what the clean at 14:32 changed.", note);
            Check("change: row name with change", AppText.RowName("In use", null, "11.2 GB", "-2.1 GB") == "In use: 11.2 GB, changed by -2.1 GB");
        }

        // ---------------------------------------------------------------- clean progress from the engine

        static void ProgressEngineTests()
        {
            var steps = new List<string>();
            var req = new CleanRequest
            {
                Areas = Areas.WorkingSets | Areas.SystemFileCache | Areas.ModifiedList,
                Progress = p => steps.Add(p.Step + "/" + p.Total + " " + p.Area)
            };
            ulong inUse = 10UL << 30;
            var snap = new MemorySnapshot { TotalPhys = 16UL << 30, AvailablePhys = 6UL << 30, LoadPercent = 62 };
            // In use drops 2 GB per trim, so the cleaner runs extra trim-and-flush passes: they are not new steps.
            CleanReport report = Cleaner.Clean(req, () => TimeSpan.Zero, () => snap, () => inUse,
                (rep, r, stop) => { r.Ok = true; if (r.Area == Areas.WorkingSets && inUse > (4UL << 30)) inUse -= 2UL << 30; },
                null, (names, token) => new List<ServiceAction>());
            Check("progress: one step per area, in clean order", string.Join(",", steps.ToArray())
                == "1/3 WorkingSets,2/3 SystemFileCache,3/3 ModifiedList", string.Join(",", steps.ToArray()));
            Check("progress: extra passes ran without new steps", report.Passes > 1, report.Passes.ToString());

            steps.Clear();
            req = new CleanRequest
            {
                Areas = Areas.Services | Areas.WorkingSets,
                Services = new List<string>(),
                Progress = p => steps.Add(p.Step + "/" + p.Total + " " + p.Area)
            };
            Cleaner.Clean(req, () => TimeSpan.Zero, () => snap, () => 0UL, (rep, r, stop) => { r.Ok = true; }, null,
                (names, token) => new List<ServiceAction>());
            Check("progress: services are step 1", string.Join(",", steps.ToArray()) == "1/2 Services,2/2 WorkingSets", string.Join(",", steps.ToArray()));

            // A progress handler that throws never stops the clean.
            req = new CleanRequest { Areas = Areas.WorkingSets | Areas.ModifiedList, Progress = p => { throw new InvalidOperationException("ui gone"); } };
            CleanReport r2 = Cleaner.Clean(req, () => TimeSpan.Zero, () => snap, () => 0UL, (rep, r, stop) => { r.Ok = true; }, null,
                (names, token) => new List<ServiceAction>());
            Check("progress: a failing handler is ignored", r2.Results.Count == 2 && r2.AllOk);

            // Areas a stop leaves out are not announced as steps.
            steps.Clear();
            var cts = new System.Threading.CancellationTokenSource();
            req = new CleanRequest
            {
                Areas = Areas.WorkingSets | Areas.SystemFileCache, Cancel = cts.Token,
                Progress = p => steps.Add(p.Step + "/" + p.Total + " " + p.Area)
            };
            Cleaner.Clean(req, () => TimeSpan.Zero, () => snap, () => 0UL, (rep, r, stop) => { r.Ok = true; cts.Cancel(); }, null,
                (names, token) => new List<ServiceAction>());
            Check("progress: no step for an area that did not run", string.Join(",", steps.ToArray()) == "1/2 WorkingSets", string.Join(",", steps.ToArray()));
        }

        // ---------------------------------------------------------------- clean progress in the window

        static void ProgressViewTests()
        {
            Check("progress line: area step", AppText.ProgressLine(new CleanProgress { Step = 2, Total = 6, Area = Areas.WorkingSets })
                == "Process working sets (2 of 6)");
            Check("progress line: services step", AppText.ProgressLine(new CleanProgress { Step = 1, Total = 9, Area = Areas.Services })
                == "Stopping services (1 of 9)");
            Check("progress line: short standby label", AppText.ProgressLine(new CleanProgress { Step = 7, Total = 9, Area = Areas.StandbyList })
                == "Full standby cache (7 of 9)");
            Check("progress line: none before the first step", AppText.ProgressLine(null) == null);
            Check("progress region name", AppText.CleaningName == "Cleaning memory");

            var v = new CleanProgressView();
            Check("progress view: idle hides the bar", !v.Running && !v.BarVisible(T0));
            v.Start(T0);
            Check("progress view: hidden under 400 ms", v.Running && !v.BarVisible(T0.AddMilliseconds(399)));
            Check("progress view: shown from 400 ms", v.BarVisible(T0.AddMilliseconds(400)));
            Check("progress view: no line before a step", v.Line == null);
            Check("progress view: takes a step", v.Step(new CleanProgress { Step = 1, Total = 3, Area = Areas.CombinePages }) && v.Line == "Combine identical pages (1 of 3)", v.Line);
            v.Finish();
            Check("progress view: finish hides it", !v.Running && !v.BarVisible(T0.AddSeconds(5)) && v.Line == null);
            Check("progress view: a late step after finish is ignored", !v.Step(new CleanProgress { Step = 2, Total = 3, Area = Areas.WorkingSets }) && v.Line == null);
            v.Start(T0.AddSeconds(10));
            Check("progress view: a new clean starts clean", v.Line == null && !v.BarVisible(T0.AddSeconds(10)));
            v.Start(T0.AddSeconds(20));
            Check("progress view: a second start keeps the first start time", v.BarVisible(T0.AddSeconds(10).AddMilliseconds(400)));
        }

        // ---------------------------------------------------------------- tray flyout placement and race guard

        static void PlacementTests()
        {
            var screen = new PixelRect(0, 0, 1920, 1080);
            var bottomWork = new PixelRect(0, 0, 1920, 1032);
            Check("edge: bottom taskbar", FlyoutPlacement.Edge(screen, bottomWork) == TaskbarEdge.Bottom);
            Check("edge: top taskbar", FlyoutPlacement.Edge(screen, new PixelRect(0, 48, 1920, 1032)) == TaskbarEdge.Top);
            Check("edge: left taskbar", FlyoutPlacement.Edge(screen, new PixelRect(60, 0, 1860, 1080)) == TaskbarEdge.Left);
            Check("edge: right taskbar", FlyoutPlacement.Edge(screen, new PixelRect(0, 0, 1860, 1080)) == TaskbarEdge.Right);
            Check("edge: auto-hide (no inset) counts as bottom", FlyoutPlacement.Edge(screen, screen) == TaskbarEdge.Bottom);

            PixelPoint p = FlyoutPlacement.Place(1700, 1056, screen, bottomWork, 360, 400, 12);
            Check("place: above a bottom taskbar, centred on the icon", p.X == 1700 - 180 && p.Y == 1032 - 12 - 400, p.X + "," + p.Y);
            p = FlyoutPlacement.Place(1910, 1056, screen, bottomWork, 360, 400, 12);
            Check("place: clamps to the work area", p.X == 1920 - 12 - 360, p.X.ToString());
            p = FlyoutPlacement.Place(5, 1056, screen, bottomWork, 360, 400, 12);
            Check("place: clamps on the left too", p.X == 12, p.X.ToString());
            p = FlyoutPlacement.Place(30, 600, screen, new PixelRect(60, 0, 1860, 1080), 360, 400, 12);
            Check("place: beside a left taskbar, centred on the icon", p.X == 60 + 12 && p.Y == 600 - 200, p.X + "," + p.Y);
            p = FlyoutPlacement.Place(1890, 1050, screen, new PixelRect(0, 0, 1860, 1080), 360, 400, 12);
            Check("place: beside a right taskbar, clamped to the bottom", p.X == 1860 - 12 - 360 && p.Y == 1080 - 12 - 400, p.X + "," + p.Y);
            p = FlyoutPlacement.Place(1700, 20, screen, new PixelRect(0, 48, 1920, 1032), 360, 400, 12);
            Check("place: under a top taskbar", p.Y == 48 + 12 && p.X == 1700 - 180, p.X + "," + p.Y);
            // Opened from the keyboard, the pointer can be anywhere: the flyout goes to the tray end of the taskbar.
            p = FlyoutPlacement.Place(400, 300, screen, bottomWork, 360, 400, 12);
            Check("place: pointer away from the taskbar uses the tray corner", p.X == 1920 - 12 - 360 && p.Y == 1032 - 12 - 400, p.X + "," + p.Y);
            p = FlyoutPlacement.Place(400, 300, screen, new PixelRect(60, 0, 1860, 1080), 360, 400, 12);
            Check("place: pointer away from a left taskbar uses its bottom", p.X == 72 && p.Y == 1080 - 12 - 400, p.X + "," + p.Y);
            // Taller than the work area: the top stays on screen.
            p = FlyoutPlacement.Place(1700, 1056, screen, new PixelRect(0, 0, 1920, 300), 360, 400, 12);
            Check("place: too tall keeps the top on screen", p.Y == 12, p.Y.ToString());
            // A second monitor to the left (negative coordinates).
            var left = new PixelRect(-1280, 0, 1280, 1024);
            p = FlyoutPlacement.Place(-100, 1000, left, new PixelRect(-1280, 0, 1280, 984), 360, 400, 12);
            Check("place: works on a monitor with negative coordinates", p.X == -12 - 360 && p.Y == 984 - 12 - 400, p.X + "," + p.Y);
        }

        static void GateTests()
        {
            var g = new FlyoutGate();
            Check("flyout gate: opens at first", g.ShouldOpen(T0));
            g.Closed(T0, true);
            Check("flyout gate: ignores reopen race", !g.ShouldOpen(T0.AddMilliseconds(250)));
            Check("flyout gate: opens after the guard", g.ShouldOpen(T0.AddMilliseconds(300)));
            g.Closed(T0, false);
            Check("flyout gate: a close by Esc or a button does not block", g.ShouldOpen(T0.AddMilliseconds(10)));
            g.Closed(T0, true);
            Check("flyout gate: a clock that goes back does not block for long", g.ShouldOpen(T0.AddSeconds(-5)));
        }

        // Review fix: Clean memory turning off while it holds focus hands focus to Open window, and back when it returns.
        static void FocusTests()
        {
            var f = new FlyoutFocus();
            Check("flyout focus: disabled while focused moves to Open window",
                f.CleanEnabledChanged(true, false, true, false) == FlyoutFocusMove.ToOpen);
            Check("flyout focus: enabled again returns to Clean memory",
                f.CleanEnabledChanged(true, true, false, true) == FlyoutFocusMove.ToClean);
            Check("flyout focus: only returns once", f.CleanEnabledChanged(true, true, false, true) == FlyoutFocusMove.None);
            Check("flyout focus: disabled while not focused stays put",
                new FlyoutFocus().CleanEnabledChanged(true, false, false, false) == FlyoutFocusMove.None);
            Check("flyout focus: closed flyout never moves focus",
                new FlyoutFocus().CleanEnabledChanged(false, false, true, false) == FlyoutFocusMove.None);
            var moved = new FlyoutFocus();
            moved.CleanEnabledChanged(true, false, true, false);
            Check("flyout focus: user moved on, so focus stays where they put it",
                moved.CleanEnabledChanged(true, true, false, false) == FlyoutFocusMove.None);
            var hidden = new FlyoutFocus();
            hidden.CleanEnabledChanged(true, false, true, false);
            hidden.Reset();
            Check("flyout focus: closing forgets the move", hidden.CleanEnabledChanged(true, true, false, true) == FlyoutFocusMove.None);
            Check("flyout focus: no change of state, no move", new FlyoutFocus().CleanEnabledChanged(true, true, true, false) == FlyoutFocusMove.None);

            var screen = new PixelRect(0, 0, 1920, 1080);
            var bottomWork = new PixelRect(0, 0, 1920, 1032);
            Check("on taskbar: pointer on a bottom bar", FlyoutPlacement.OnTaskbar(1800, 1060, screen, bottomWork));
            Check("on taskbar: pointer on the desktop is not", !FlyoutPlacement.OnTaskbar(900, 500, screen, bottomWork));
            Check("on taskbar: pointer on a left bar", FlyoutPlacement.OnTaskbar(20, 500, screen, new PixelRect(60, 0, 1860, 1080)));
            Check("on taskbar: auto-hide bar is never under the pointer", !FlyoutPlacement.OnTaskbar(1800, 1079, screen, screen));
        }

        // Preview motion table: the signed changes come in 30 ms apart, in map order; hidden parts take no slot.
        static void StaggerTests()
        {
            var shown = new bool[] { true, true, true, true };
            Check("stagger: in use first, at once", ChangeStagger.Delay(shown, (int)MapPart.InUse) == TimeSpan.Zero);
            Check("stagger: modified 30 ms", ChangeStagger.Delay(shown, (int)MapPart.Modified) == TimeSpan.FromMilliseconds(30));
            Check("stagger: free 90 ms", ChangeStagger.Delay(shown, (int)MapPart.Free) == TimeSpan.FromMilliseconds(90));
            Check("stagger: note after the last figure", ChangeStagger.NoteDelay(shown) == TimeSpan.FromMilliseconds(120));
            var two = new bool[] { true, false, false, true };
            Check("stagger: hidden parts take no slot", ChangeStagger.Delay(two, (int)MapPart.Free) == TimeSpan.FromMilliseconds(30));
            Check("stagger: note after two figures", ChangeStagger.NoteDelay(two) == TimeSpan.FromMilliseconds(60));
        }

        static void FlyoutTextTests()
        {
            var s = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85 };
            Check("flyout auto: armed", AppText.FlyoutAutoLine(AutoCleanState.Armed, new AutoCleanStatus(), s, Inv) == "On, when load reaches 85%");
            Check("flyout auto: off", AppText.FlyoutAutoLine(AutoCleanState.Off, null, s, Inv) == "Off");
            Check("flyout auto: needs admin", AppText.FlyoutAutoLine(AutoCleanState.NeedsAdmin, null, s, Inv) == "Paused: needs administrator rights");
            var paused = new AutoCleanStatus { Paused = true, PausedUntilUtc = T0 };
            Check("flyout auto: paused", AppText.FlyoutAutoLine(AutoCleanState.Paused, paused, s, Inv) == AppText.PausedLine(paused, Inv));
            Check("flyout auto: backing off", AppText.FlyoutAutoLine(AutoCleanState.BackingOff, new AutoCleanStatus { FutileStreak = 2 }, s, Inv)
                == "On, waiting longer: recent cleans did not help");
            Check("flyout pause button", AppText.FlyoutPauseText(false) == "Pause for 1 hour" && AppText.FlyoutPauseText(true) == "Resume");
            // WCAG 2.5.3: the accessible name starts with the visible label.
            Check("flyout pause names hold their label", AppText.FlyoutPauseName(false).StartsWith(AppText.FlyoutPauseText(false))
                && AppText.FlyoutPauseName(true).StartsWith(AppText.FlyoutPauseText(true)) && AppText.FlyoutPauseName(true) == AppText.ResumeMenuText);
            Check("flyout name", AppText.FlyoutName == "RAM Optimiser quick view");

            var good = new CleanReport { Before = new MemorySnapshot { TotalPhys = 16UL << 30, AvailablePhys = 4UL << 30 },
                After = new MemorySnapshot { TotalPhys = 16UL << 30, AvailablePhys = 6UL << 30 } };
            good.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            Check("result glyph: freed is a check", AppText.ResultGlyphKey(good, null) == "IconCheck" && AppText.ResultGlyphBrush(good, null) == "Ok");
            var none = new CleanReport { Before = good.Before, After = good.Before };
            none.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            Check("result glyph: nothing freed is a dash", AppText.ResultGlyphKey(none, null) == "IconDash" && AppText.ResultGlyphBrush(none, null) == "MutedText");
            var bad = new CleanReport { Before = good.Before, After = good.After };
            bad.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = false, Message = "x" });
            Check("result glyph: a failure is a warning", AppText.ResultGlyphKey(bad, null) == "IconWarn" && AppText.ResultGlyphBrush(bad, null) == "Caution");
            Check("result glyph: an error is a warning", AppText.ResultGlyphKey(null, new Exception("x")) == "IconWarn");
        }

        // ---------------------------------------------------------------- Mica

        static void MicaTests()
        {
            Check("mica: setting hidden on Windows 10", !MicaPolicy.SettingShown(19045));
            Check("mica: setting shown on Windows 11", MicaPolicy.SettingShown(22000) && MicaPolicy.SettingShown(26100));
            Check("mica: false below 22621", !MicaPolicy.Use(true, 22000, false) && !MicaPolicy.Use(true, 22620, false));
            Check("mica: on where supported", MicaPolicy.Use(true, 22621, false) && MicaPolicy.Use(true, 26100, false));
            Check("mica: false in high contrast", !MicaPolicy.Use(true, 26100, true));
            Check("mica: false when the setting is off", !MicaPolicy.Use(false, 26100, false));
            Check("mica: switch usable only where supported", MicaPolicy.SwitchEnabled(22621, false) && !MicaPolicy.SwitchEnabled(22000, false)
                && !MicaPolicy.SwitchEnabled(26100, true));

            Check("mica note: supported", AppText.MicaNote(26100, false) == "Mica, tinted by your wallpaper. Solid when the window is inactive or transparency effects are off.");
            Check("mica note: older Windows 11", AppText.MicaNote(22000, false) == "Needs Windows 11 version 22H2 or later. Until then the window stays solid.");
            Check("mica note: high contrast", AppText.MicaNote(26100, true) == "Not used with high contrast, which keeps solid system colours.");

            var d = new AppSettings();
            Check("settings: Mica off by default", !d.UseMica);
            Check("settings: Mica off writes no key", !SettingsStore.Serialize(d).Contains("UseMica"));
            string text = SettingsStore.Serialize(new AppSettings { UseMica = true });
            Check("settings: Mica on written", text.Contains("UseMica=true\r\n"), text);
            Check("settings: Mica round trip", SettingsStore.Parse(text).UseMica);
            Check("settings: Mica bad value keeps it off", !SettingsStore.Parse("UseMica=maybe").UseMica);
            Check("settings: reset turns Mica off", !AppSettings.ResetDefaults(new AppSettings { UseMica = true }).UseMica);
            AppSettings merged = SettingsMerge.Merge(d, new AppSettings { UseMica = true }, d.Clone());
            Check("settings: turning Mica on survives a merge", merged.UseMica);
            merged = SettingsMerge.Merge(new AppSettings { UseMica = true }, new AppSettings(), new AppSettings { UseMica = true });
            Check("settings: turning Mica off survives a merge", !merged.UseMica);
        }

        // ---------------------------------------------------------------- WPF wiring, checked in source

        static string AppDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetFullPath(Path.Combine(dir, Path.Combine("..", Path.Combine("src", "App"))));
        }

        static void SourceTests()
        {
            string dir = AppDir();
            string xamlDir = Path.Combine(dir, "Xaml");
            string main = File.ReadAllText(Path.Combine(xamlDir, "MainWindow.xaml"));
            foreach (string name in new[] { "HistoryChart", "HistoryPanel", "ChangeNote", "ChgInUse", "ChgModified", "ChgStandby", "ChgFree",
                         "CleanProgress", "ProgressSegment", "ProgressText", "MicaRow", "MicaCheck", "MicaNote" })
                Check("main window has " + name, main.Contains("x:Name=\"" + name + "\""));
            // Owner decision: no Cancel or Stop while a clean runs. The progress block holds no button at all.
            var mainDoc = new XmlDocument();
            mainDoc.LoadXml(main);
            var mns = new XmlNamespaceManager(mainDoc.NameTable);
            mns.AddNamespace("w", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
            mns.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
            XmlNode progress = mainDoc.SelectSingleNode("//*[@x:Name='CleanProgress']", mns);
            Check("progress has no Cancel or Stop button", progress != null && progress.SelectNodes(".//w:Button", mns).Count == 0
                && !Regex.IsMatch(main, "x:Name=\"(Cancel|Stop)Clean"));

            string flyPath = Path.Combine(xamlDir, "Flyout.xaml");
            Check("flyout XAML exists", File.Exists(flyPath));
            if (File.Exists(flyPath))
            {
                var doc = new XmlDocument();
                try { doc.Load(flyPath); Check("flyout XAML is well-formed", true); }
                catch (Exception ex) { Check("flyout XAML is well-formed", false, ex.Message); return; }
                string fly = Regex.Replace(File.ReadAllText(flyPath), "<!--.*?-->", "", RegexOptions.Singleline);
                Match raw = new Regex("#[0-9A-Fa-f]{3,8}\\b").Match(fly);
                Check("flyout has no raw colours", !raw.Success, raw.Success ? raw.Value : null);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("w", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
                foreach (string tag in new[] { "Button", "CheckBox" })
                    foreach (XmlElement e in doc.SelectNodes("//w:" + tag, ns))
                        Check("flyout " + tag + " " + e.GetAttribute("x:Name") + " has a name", e.HasAttribute("AutomationProperties.Name"));
                foreach (string name in new[] { "FlyClean", "FlyPause", "FlyOpen", "FlyLoad", "FlyMap", "FlyAutoLine", "FlyResultText" })
                    Check("flyout has " + name, fly.Contains("x:Name=\"" + name + "\""));
                Check("flyout keeps Tab inside", fly.Contains("KeyboardNavigation.TabNavigation=\"Cycle\""));
                Check("flyout is not in the taskbar", fly.Contains("ShowInTaskbar=\"False\""));
            }

            string tray = File.ReadAllText(Path.Combine(dir, "TrayController.cs"));
            Check("tray: left click asks for the flyout", Regex.IsMatch(tray, @"MouseButtons\.Left\)\s*Raise\(FlyoutRequested\)"));
            Check("tray: right click keeps the menu", tray.Contains("ContextMenuStrip = menu"));

            string flyout = File.ReadAllText(Path.Combine(dir, "TrayFlyout.cs"));
            Check("flyout closes on Escape", Regex.IsMatch(flyout, @"e\.Key != Key\.Escape\) return;\s*e\.Handled = true;\s*Hide\(false\);"));
            Check("flyout closes on focus loss", Regex.IsMatch(flyout, @"window\.Deactivated \+= delegate \{ Hide\(true\); \};"));
            // Review fix: focus is read before Clean memory turns off (afterwards WPF has already started moving it).
            Check("flyout reads Clean memory's focus before disabling it",
                Regex.IsMatch(flyout, @"bool cleanFocused = clean\.IsKeyboardFocused;\s*clean\.IsEnabled = st\.CleanEnabled;"));
            Check("flyout focus goes through FlyoutFocus", flyout.Contains("focus.CleanEnabledChanged(") && !flyout.Contains("!window.IsKeyboardFocusWithin"));
            Check("flyout falls back to the primary screen off the taskbar",
                flyout.Contains("FlyoutPlacement.OnTaskbar(") && flyout.Contains("Screen.PrimaryScreen"));
            Check("flyout motion goes through Motion", flyout.Contains("Motion.To(") && !flyout.Contains("new DoubleAnimation"));

            string theme = File.ReadAllText(Path.Combine(dir, "ThemeManager.cs"));
            Check("mica uses the documented backdrop attribute", theme.Contains("DWMWA_SYSTEMBACKDROP_TYPE = 38") && theme.Contains("DWMSBT_MAINWINDOW = 2"));
            Check("mica is decided by MicaPolicy.Use", theme.Contains("MicaPolicy.Use("));

            string visuals = File.ReadAllText(Path.Combine(dir, "AppShell.Visuals.cs"));
            Check("progress reaches the window through Post", visuals.Contains("req.Progress = p => Post("));
            Match seg = Regex.Match(visuals, @"void MoveSegment\(\)(.*?)\n        \}", RegexOptions.Singleline);
            string body = seg.Success ? seg.Groups[1].Value : "";
            int still = body.IndexOf("if (!Motion.On", StringComparison.Ordinal);
            int sweep = body.IndexOf("BeginAnimation(TranslateTransform.XProperty, sweep)", StringComparison.Ordinal);
            Check("progress bar respects reduced motion (MoveSegment stops before the sweep)", still >= 0 && sweep > still);
            // Review fix: the toggle must hand SaveAndApply the settings from before the change, or ApplySettings sees no
            // change and the backdrop is never put on or taken off until a restart.
            Check("mica toggle passes the settings from before the change",
                Regex.IsMatch(visuals, @"OnToggle\(micaCheck, on =>\s*\{\s*AppSettings before = settings\.Clone\(\);\s*settings\.UseMica = on;\s*SaveAndApply\(before\);"));
            Check("change figures are staggered", visuals.Contains("ChangeStagger.Delay(") && visuals.Contains("ChangeStagger.NoteDelay("));
        }

        // ---------------------------------------------------------------- helpers

        static bool Near(double a, double b) { return Math.Abs(a - b) < 0.001; }

        static bool Same(List<int> got, params int[] want)
        {
            if (got == null || got.Count != want.Length) return false;
            for (int i = 0; i < want.Length; i++) if (got[i] != want[i]) return false;
            return true;
        }

        static string Join(List<int> l)
        {
            if (l == null) return "null";
            var parts = new List<string>();
            foreach (int i in l) parts.Add(i.ToString(CultureInfo.InvariantCulture));
            return string.Join(",", parts.ToArray());
        }
    }
}
