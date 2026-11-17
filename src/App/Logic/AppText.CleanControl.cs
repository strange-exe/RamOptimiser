// Wording for a clean that was stopped, hit its time limit, or was skipped because another clean was running
// (E9, E22; package 2B). Where the app shows it is package 3A's choice; this file only supplies the text. Pure: no WPF.
using System;
using System.Globalization;

namespace RamOptimiser.App
{
    public static partial class AppText
    {
        /// <summary>
        /// The result line for a clean that did not complete: "Last clean at 14:32 was stopped after 3 of 6 areas.
        /// Load went from 81% to 79%.", "Auto-clean at 14:32 hit its 2-minute limit after 3 of 6 areas. ..." or
        /// "... was skipped: another RamOptimiser clean was running." Null for a complete clean (use LastClean).
        /// </summary>
        public static string StoppedCleanLine(DateTime localTime, DateTime localNow, CleanReport report, bool automatic, CultureInfo culture)
        {
            if (report == null || report.Completed) return null;
            string head = (automatic ? "Auto-clean " : "Last clean ") + When(localTime, localNow, culture);
            if (report.Stopped == CleanStop.Busy) return head + " was skipped: another RamOptimiser clean was running.";
            string what = report.Stopped == CleanStop.TimedOut ? " hit its " + LimitText(report.Deadline) + " limit" : " was stopped";
            int ran = report.AreasRun, total = report.AreasTotal;
            string where = ran == 0 ? " before any area ran."
                : " after " + ran.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture)
                    + (total == 1 ? " area." : " areas.");
            string text = head + what + where;
            if (report.Before != null && report.After != null)
                text += " Load went from " + report.Before.LoadPercent.ToString(CultureInfo.InvariantCulture) + "% to "
                    + report.After.LoadPercent.ToString(CultureInfo.InvariantCulture) + "%.";
            return text;
        }

        /// <summary>"2-minute" for whole minutes, else "90-second"; "time" when there was no limit.</summary>
        public static string LimitText(TimeSpan? limit)
        {
            if (limit == null) return "time";
            long seconds = (long)Math.Round(limit.Value.TotalSeconds);
            if (seconds >= 60 && seconds % 60 == 0) return (seconds / 60).ToString(CultureInfo.InvariantCulture) + "-minute";
            return seconds.ToString(CultureInfo.InvariantCulture) + "-second";
        }
    }
}
