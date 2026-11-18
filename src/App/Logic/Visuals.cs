// Package 7A: the decisions behind the load history chart, the per-part change after a clean, the clean progress
// line, the tray flyout's placement and the Mica setting. Pure logic, no WPF, so the test runner compiles it as it is.
using System;
using System.Collections.Generic;

namespace RamOptimiser.App
{
    /// <summary>One load reading: when (UTC) and the load percent (0 to 100).</summary>
    public sealed class LoadSample
    {
        public DateTime Utc;
        public int Load;
    }

    /// <summary>
    /// The last ten minutes of load for this session, in memory only: a ring of at most Capacity readings plus the
    /// times of cleans. It keeps filling while the window is hidden. A clock that goes back (time sync) starts it
    /// again, so the line is never drawn backwards. UI thread only.
    /// </summary>
    public sealed class LoadHistory
    {
        /// <summary>Ten minutes at the two-second sample rate.</summary>
        public const int Capacity = 300;
        public const int MaxCleanMarks = 64;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        readonly LoadSample[] ring = new LoadSample[Capacity];
        int start, count;
        readonly List<DateTime> cleans = new List<DateTime>();
        DateTime newest = DateTime.MinValue;

        public void Add(DateTime utc, int load)
        {
            if (utc < newest) Clear();
            if (load < 0) load = 0;
            if (load > 100) load = 100;
            if (count == Capacity)
            {
                start = (start + 1) % Capacity;
                count--;
            }
            ring[(start + count) % Capacity] = new LoadSample { Utc = utc, Load = load };
            count++;
            newest = utc;
        }

        public void MarkClean(DateTime utc)
        {
            if (newest - utc > Window) return;
            cleans.Add(utc);
            if (cleans.Count > MaxCleanMarks) cleans.RemoveAt(0);
        }

        /// <summary>The readings of the last ten minutes before now, oldest first.</summary>
        public List<LoadSample> Samples(DateTime now)
        {
            var list = new List<LoadSample>(count);
            for (int i = 0; i < count; i++)
            {
                LoadSample s = ring[(start + i) % Capacity];
                if (InWindow(s.Utc, now)) list.Add(s);
            }
            return list;
        }

        /// <summary>The clean times (UTC) of the last ten minutes before now, oldest first.</summary>
        public List<DateTime> Cleans(DateTime now)
        {
            var list = new List<DateTime>();
            foreach (DateTime c in cleans) if (InWindow(c, now)) list.Add(c);
            return list;
        }

        static bool InWindow(DateTime t, DateTime now)
        {
            return t <= now && now - t <= Window;
        }

        void Clear()
        {
            start = 0;
            count = 0;
            cleans.Clear();
        }
    }

    /// <summary>The chart's geometry: load to y, time to x, and which axis labels fit. Pure.</summary>
    public static class LoadChartMath
    {
        /// <summary>
        /// The bottom of the scale: 50%, where load usually sits, or 0 once any reading is lower, so a dip is drawn
        /// as a dip instead of being flattened onto the floor.
        /// </summary>
        public static int Floor(List<LoadSample> samples)
        {
            if (samples != null)
                foreach (LoadSample s in samples) if (s.Load < 50) return 0;
            return 50;
        }

        /// <summary>Y of a load in a chart of the given height: 100% at the top, the floor at the bottom.</summary>
        public static double Y(double load, int floor, double height)
        {
            if (load > 100) load = 100;
            if (load < floor) load = floor;
            return height * (100 - load) / (100 - floor);
        }

        /// <summary>X of a time in a chart of the given width: now at the right edge, ten minutes ago at the left.</summary>
        public static double X(DateTime t, DateTime now, double width)
        {
            double f = 1 - (now - t).TotalMilliseconds / LoadHistory.Window.TotalMilliseconds;
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            return f * width;
        }

        /// <summary>
        /// The axis labels, top first: 100, the auto-clean threshold (null when auto-clean is off) and the floor. The
        /// threshold wins: 100 or the floor is left out when it would sit closer than minGap to it.
        /// </summary>
        public static List<int> Labels(int floor, int? threshold, double height, double minGap)
        {
            var list = new List<int>();
            // A threshold at the floor or at 100 is the floor's or 100's own label.
            bool hasT = threshold.HasValue && threshold.Value > floor && threshold.Value < 100;
            double ty = hasT ? Y(threshold.Value, floor, height) : 0;
            if (!hasT || Math.Abs(Y(100, floor, height) - ty) >= minGap) list.Add(100);
            if (hasT) list.Add(threshold.Value);
            if (!hasT || Math.Abs(Y(floor, floor, height) - ty) >= minGap) list.Add(floor);
            return list;
        }
    }

    /// <summary>The signed change of each map part from the last clean, shown under the legend until the next one.</summary>
    public static class PartChanges
    {
        /// <summary>
        /// "-2.1 GB", "+400.0 MB" per MapPart (indexed by the enum), or null when there is nothing honest to show: no
        /// report, a busy skip (nothing ran), a missing snapshot, or a map whose detail changed between the two. A
        /// two-part map has only in use and available (the Free slot); Modified and Standby are null.
        /// </summary>
        public static string[] From(CleanReport report)
        {
            if (report == null || report.Stopped == CleanStop.Busy || report.Before == null || report.After == null) return null;
            if (report.Before.HasLists != report.After.HasLists) return null;
            MemoryMap before = MemoryMap.From(report.Before), after = MemoryMap.From(report.After);
            var result = new string[4];
            foreach (MapPart part in MemoryMap.Parts)
            {
                if (!before.Detailed && (part == MapPart.Modified || part == MapPart.Standby)) continue;
                result[(int)part] = AppText.Change(MemoryMap.Change(before, after, part));
            }
            return result;
        }
    }

    /// <summary>
    /// What the window shows while a clean runs: the bar appears only after ShowAfter, so a quick clean never flashes
    /// it, and the step line follows the engine's CleanProgress. Steps that arrive when no clean runs are ignored.
    /// </summary>
    public sealed class CleanProgressView
    {
        public static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(400);

        DateTime? started;
        string line;

        public bool Running { get { return started.HasValue; } }
        /// <summary>"Process working sets (2 of 6)", or null before the first step.</summary>
        public string Line { get { return line; } }

        public void Start(DateTime utc)
        {
            if (started.HasValue) return;
            started = utc;
            line = null;
        }

        /// <summary>Takes a step; false (and no change) when no clean is running.</summary>
        public bool Step(CleanProgress p)
        {
            if (!started.HasValue || p == null) return false;
            line = AppText.ProgressLine(p);
            return true;
        }

        public void Finish()
        {
            started = null;
            line = null;
        }

        public bool BarVisible(DateTime now)
        {
            return started.HasValue && now - started.Value >= ShowAfter;
        }
    }

    /// <summary>A rectangle in physical screen pixels (Left, Top, Width, Height).</summary>
    public struct PixelRect
    {
        public int Left, Top, Width, Height;

        public PixelRect(int left, int top, int width, int height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public int Right { get { return Left + Width; } }
        public int Bottom { get { return Top + Height; } }
    }

    public struct PixelPoint
    {
        public int X, Y;

        public PixelPoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    public enum TaskbarEdge { Bottom, Top, Left, Right }

    /// <summary>Where the tray flyout opens, in physical pixels, next to the taskbar that holds the icon. Pure.</summary>
    public static class FlyoutPlacement
    {
        /// <summary>The side the work area is cut from; an auto-hiding taskbar cuts nothing and counts as the bottom.</summary>
        public static TaskbarEdge Edge(PixelRect screen, PixelRect work)
        {
            if (work.Bottom < screen.Bottom) return TaskbarEdge.Bottom;
            if (work.Top > screen.Top) return TaskbarEdge.Top;
            if (work.Left > screen.Left) return TaskbarEdge.Left;
            if (work.Right < screen.Right) return TaskbarEdge.Right;
            return TaskbarEdge.Bottom;
        }

        /// <summary>
        /// The flyout's top-left: margin inside the work area on the taskbar's side, centred on the pointer when the
        /// pointer is on the taskbar. Opened from the keyboard the pointer can be anywhere, so then it goes to the tray
        /// end (right, or bottom for a side taskbar). Always clamped into the work area, top and left winning.
        /// </summary>
        /// <summary>
        /// The point is on the taskbar of this screen (outside the work area on the taskbar's side). An auto-hiding
        /// taskbar cuts nothing from the work area, so it never counts.
        /// </summary>
        public static bool OnTaskbar(int x, int y, PixelRect screen, PixelRect work)
        {
            switch (Edge(screen, work))
            {
                case TaskbarEdge.Bottom: return y >= work.Bottom;
                case TaskbarEdge.Top: return y < work.Top;
                case TaskbarEdge.Left: return x < work.Left;
                default: return x >= work.Right;
            }
        }

        public static PixelPoint Place(int anchorX, int anchorY, PixelRect screen, PixelRect work, int width, int height, int margin)
        {
            TaskbarEdge edge = Edge(screen, work);
            bool onBar = OnTaskbar(anchorX, anchorY, screen, work);
            int x, y;
            if (edge == TaskbarEdge.Bottom || edge == TaskbarEdge.Top)
            {
                x = onBar ? anchorX - width / 2 : work.Right - margin - width;
                y = edge == TaskbarEdge.Bottom ? work.Bottom - margin - height : work.Top + margin;
            }
            else
            {
                y = onBar ? anchorY - height / 2 : work.Bottom - margin - height;
                x = edge == TaskbarEdge.Left ? work.Left + margin : work.Right - margin - width;
            }
            return new PixelPoint(Clamp(x, work.Left + margin, work.Right - margin - width),
                Clamp(y, work.Top + margin, work.Bottom - margin - height));
        }

        static int Clamp(int v, int min, int max)
        {
            if (v > max) v = max;
            if (v < min) v = min;
            return v;
        }
    }

    /// <summary>
    /// Clicking the tray icon while the flyout is open first deactivates it (closing it), then delivers the click: the
    /// guard stops that click from opening it again at once, so a second click closes the flyout as expected.
    /// </summary>
    public sealed class FlyoutGate
    {
        public static readonly TimeSpan Guard = TimeSpan.FromMilliseconds(300);

        DateTime? deactivatedAt;

        public bool ShouldOpen(DateTime now)
        {
            if (!deactivatedAt.HasValue) return true;
            TimeSpan since = now - deactivatedAt.Value;
            return since < TimeSpan.Zero || since >= Guard;
        }

        /// <summary>byDeactivation: it closed because it lost focus (only then is a click right after it the same gesture).</summary>
        public void Closed(DateTime now, bool byDeactivation)
        {
            deactivatedAt = byDeactivation ? now : (DateTime?)null;
        }
    }

    public enum FlyoutFocusMove { None, ToOpen, ToClean }

    /// <summary>
    /// Clean memory in the flyout turns off while a clean runs. If it held keyboard focus, focus goes to Open window
    /// (always there) rather than being dropped, and comes back when the button is enabled again, unless the user has
    /// moved on. Mirrors refocusClean in the main window.
    /// </summary>
    public sealed class FlyoutFocus
    {
        bool moved;

        /// <param name="shown">The flyout is open.</param>
        /// <param name="enabled">Clean memory's new enabled state.</param>
        /// <param name="cleanFocused">Clean memory held keyboard focus just before the change.</param>
        /// <param name="openFocused">Open window holds keyboard focus now.</param>
        public FlyoutFocusMove CleanEnabledChanged(bool shown, bool enabled, bool cleanFocused, bool openFocused)
        {
            if (!enabled)
            {
                if (!shown || !cleanFocused) return FlyoutFocusMove.None;
                moved = true;
                return FlyoutFocusMove.ToOpen;
            }
            if (!moved) return FlyoutFocusMove.None;
            moved = false;
            return shown && openFocused ? FlyoutFocusMove.ToClean : FlyoutFocusMove.None;
        }

        /// <summary>The flyout closed: the next open puts focus on its first button anyway.</summary>
        public void Reset() { moved = false; }
    }

    /// <summary>
    /// The per-part change figures come in one after another, 30 ms apart in map order (in use, modified, standby,
    /// free), then the note; a part with no figure takes no slot.
    /// </summary>
    public static class ChangeStagger
    {
        public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(30);

        public static TimeSpan Delay(bool[] shown, int index)
        {
            int slot = 0;
            for (int i = 0; i < index && i < shown.Length; i++) if (shown[i]) slot++;
            return TimeSpan.FromTicks(Step.Ticks * slot);
        }

        public static TimeSpan NoteDelay(bool[] shown)
        {
            return Delay(shown, shown.Length);
        }
    }

    /// <summary>
    /// The Mica backdrop setting: shown on Windows 11 (build 22000), usable where DWMWA_SYSTEMBACKDROP_TYPE exists
    /// (build 22621, version 22H2), and never under high contrast, which needs solid system colours.
    /// </summary>
    public static class MicaPolicy
    {
        public const int Windows11Build = 22000;
        public const int BackdropBuild = 22621;

        public static bool SettingShown(int build) { return build >= Windows11Build; }

        public static bool Supported(int build) { return build >= BackdropBuild; }

        public static bool SwitchEnabled(int build, bool highContrast) { return Supported(build) && !highContrast; }

        public static bool Use(bool setting, int build, bool highContrast) { return setting && SwitchEnabled(build, highContrast); }
    }
}
