// Package 7A wording: the load history chart, the change note, the clean progress line, the tray flyout and Mica.
using System;
using System.Collections.Generic;
using System.Globalization;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    public static partial class AppText
    {
        // ------------------------------------------------------------------ load history chart

        public const string ChartHeading = "Load, last 10 minutes";
        public const string ChartNow = "Now";
        public const string ChartCleanKey = "Clean";
        public const string ChartAutoKey = "Auto-clean";
        const string ChartTooFew = "Load history: not enough readings yet.";

        /// <summary>The time at the chart's left edge, ten minutes before nowLocal.</summary>
        public static string ChartFrom(DateTime nowLocal, CultureInfo culture)
        {
            if (nowLocal < DateTime.MinValue + LoadHistory.Window) return "";
            return (nowLocal - LoadHistory.Window).ToString("t", culture);
        }

        /// <summary>
        /// The chart in words, so the line is never the only way to read it: "Load over the last 10 minutes: now 71%,
        /// between 69% and 85%. Cleans at 14:23 and 14:32." cleansLocal are local times, oldest first.
        /// </summary>
        public static string ChartName(List<LoadSample> samples, List<DateTime> cleansLocal, CultureInfo culture)
        {
            if (samples == null || samples.Count < 2) return ChartTooFew;
            int min = 100, max = 0;
            foreach (LoadSample s in samples)
            {
                if (s.Load < min) min = s.Load;
                if (s.Load > max) max = s.Load;
            }
            int now = samples[samples.Count - 1].Load;
            int minutes = (int)Math.Round((samples[samples.Count - 1].Utc - samples[0].Utc).TotalMinutes);
            string span = minutes <= 1 ? "the last minute" : "the last " + Num(minutes) + " minutes";
            string text = "Load over " + span + ": ";
            text += min == max ? "steady at " + Num(now) + "%." : "now " + Num(now) + "%, between " + Num(min) + "% and " + Num(max) + "%.";
            return text + " " + CleansSentence(cleansLocal, culture);
        }

        static string CleansSentence(List<DateTime> cleans, CultureInfo culture)
        {
            int n = cleans == null ? 0 : cleans.Count;
            if (n == 0) return "No cleans.";
            if (n == 1) return "One clean, at " + cleans[0].ToString("t", culture) + ".";
            if (n > 3) return Num(n) + " cleans, the last at " + cleans[n - 1].ToString("t", culture) + ".";
            var times = new List<string>();
            foreach (DateTime c in cleans) times.Add(c.ToString("t", culture));
            string last = times[times.Count - 1];
            times.RemoveAt(times.Count - 1);
            return "Cleans at " + string.Join(", ", times.ToArray()) + " and " + last + ".";
        }

        static string Num(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------------ per-part change

        /// <summary>The line under the legend while the signed figures show.</summary>
        public static string ChangeNote(DateTime cleanLocal, CultureInfo culture)
        {
            return "The + and - figures are what the clean at " + cleanLocal.ToString("t", culture) + " changed.";
        }

        // ------------------------------------------------------------------ clean progress

        /// <summary>The progress region's name; only the start and the end of a clean are announced.</summary>
        public const string CleaningName = "Cleaning memory";

        /// <summary>"Process working sets (2 of 6)" or "Stopping services (1 of 9)"; null before the first step.</summary>
        public static string ProgressLine(CleanProgress p)
        {
            if (p == null) return null;
            string label = p.Area == Areas.Services ? "Stopping services" : ShortAreaLabel(p.Area);
            return label + " (" + Num(p.Step) + " of " + Num(p.Total) + ")";
        }

        // ------------------------------------------------------------------ tray flyout

        public const string FlyoutName = "RAM Optimiser quick view";
        public const string FlyoutCaption = "RAM Optimiser";
        public const string FlyoutOpenText = "Open window";
        public const string FlyoutNoClean = "No clean yet this session.";

        /// <summary>The flyout's auto-clean line: what it does now, in a few words.</summary>
        public static string FlyoutAutoLine(AutoCleanState state, AutoCleanStatus st, AppSettings s, CultureInfo culture)
        {
            switch (state)
            {
                case AutoCleanState.Off: return "Off";
                case AutoCleanState.NeedsAdmin: return "Paused: needs administrator rights";
                case AutoCleanState.Paused: return PausedLine(st, culture) ?? "Paused";
                case AutoCleanState.BackingOff: return "On, waiting longer: recent cleans did not help";
                default: return "On, " + AutoDescription(s);
            }
        }

        public static string FlyoutPauseText(bool paused) { return PauseRowButtonText(paused); }

        /// <summary>Starts with the visible label (WCAG 2.5.3).</summary>
        public static string FlyoutPauseName(bool paused) { return PauseRowButtonName(paused); }

        /// <summary>The result glyph's icon key, the same rule as the Memory tab's result line.</summary>
        public static string ResultGlyphKey(CleanReport report, Exception error)
        {
            if (error != null || !CleanWentWell(report)) return "IconWarn";
            return report.InUseFreed > 0 ? "IconCheck" : "IconDash";
        }

        /// <summary>The glyph's stroke token. Never the only signal: the line beside it says the same.</summary>
        public static string ResultGlyphBrush(CleanReport report, Exception error)
        {
            string key = ResultGlyphKey(report, error);
            return key == "IconWarn" ? "Caution" : key == "IconCheck" ? "Ok" : "MutedText";
        }

        // ------------------------------------------------------------------ Mica

        public const string MicaTitle = "Use the Windows 11 backdrop";

        public static string MicaNote(int build, bool highContrast)
        {
            if (highContrast) return "Not used with high contrast, which keeps solid system colours.";
            if (!MicaPolicy.Supported(build)) return "Needs Windows 11 version 22H2 or later. Until then the window stays solid.";
            return "Mica, tinted by your wallpaper. Solid when the window is inactive or transparency effects are off.";
        }
    }
}
