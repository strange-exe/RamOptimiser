// Wording for why automatic cleans are waiting (P5). Pure, so the test runner compiles it as it is.
using System;
using System.Globalization;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    public static partial class AppText
    {
        /// <summary>
        /// The auto-clean row's note for a hold, or null when nothing should show: no hold, asleep (no one sees
        /// it), or a lock or battery hold with no schedule, which changes nothing the user set.
        /// </summary>
        public static string AutoHoldText(HoldReason r, AppSettings s)
        {
            if (s == null) throw new ArgumentNullException("s");
            string threshold = s.ThresholdPercent.ToString(CultureInfo.InvariantCulture) + "%";
            switch (r)
            {
                case HoldReason.Resuming:
                    return "Paused for 2 minutes after the PC woke up, while apps load back in.";
                case HoldReason.Busy:
                    return "Paused while a full-screen app or presentation is running.";
                case HoldReason.Away:
                    return s.IntervalMinutes > 0 ? "While the PC is locked, only cleans when load reaches " + threshold + "." : null;
                case HoldReason.OnBattery:
                    return s.IntervalMinutes > 0 ? "On battery, only cleans when load reaches " + threshold + "." : null;
                default:
                    return null;
            }
        }
    }
}
