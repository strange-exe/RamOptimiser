// Wording for the clean report's timing and the "will a clean help" line (package 1B). Pure: no WPF here.
// Where the GUI shows these is a UI-track choice; this file only supplies the text.
using System;
using System.Globalization;
using Advice = RamOptimiser.CleanAdvice;

namespace RamOptimiser.App
{
    public static partial class AppText
    {
        /// <summary>How long a clean or one area took: "&lt;0.1 s" or "1.8 s".</summary>
        public static string DurationText(TimeSpan t)
        {
            return Format.Seconds(t);
        }

        /// <summary>
        /// The muted line under Clean memory: whether a clean helps now, from CleanEstimate.Advise. Null when Windows
        /// gave no signal, so the line is hidden rather than guessed.
        /// </summary>
        public static string CleanAdvice(Advice a, MemorySnapshot s)
        {
            switch (a)
            {
                case Advice.CommitBound:
                    string reserved = s == null || s.CommitLimit == 0 ? "most"
                        : s.CommitPercent.ToString(CultureInfo.InvariantCulture) + "%";
                    // The window shows the commit warning instead (AdviceFor); this stays for a caller with no warning.
                    return "Apps have reserved " + reserved + " of the memory Windows can give out. "
                        + "Cleaning does not lower that; closing apps does.";
                default: return CleanEstimate.AdviceSentence(a); // shared with ramopt; null for None
            }
        }
    }
}
