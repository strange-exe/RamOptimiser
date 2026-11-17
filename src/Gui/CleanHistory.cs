using System;
using System.Collections.Generic;

namespace RamOptimiser.Gui
{
    /// <summary>One clean in the app's recent history: when it ended, why it ran, its report and the load five minutes on.</summary>
    public sealed class CleanHistoryEntry
    {
        /// <summary>The id its clean log record carries (CleanLog.NewId), so the rebound line can point at it.</summary>
        public string Id;
        public DateTime EndUtc;
        public CleanReason Reason;
        /// <summary>Null when the clean threw before reporting (Error says why).</summary>
        public CleanReport Report;
        public string Error;
        /// <summary>The load about five minutes after the clean (the rebound), or null until then or when there is none.</summary>
        public uint? LoadLater;
        /// <summary>True once the rebound was taken, missed or cancelled, so it is handed out at most once.</summary>
        internal bool ReboundDone;

        public CleanStop Stopped { get { return Report == null ? CleanStop.None : Report.Stopped; } }

        /// <summary>The clean ran and has both snapshots (a busy skip ran nothing).</summary>
        public bool Ran
        {
            get { return Report != null && Report.Before != null && Report.After != null && Report.Stopped != CleanStop.Busy; }
        }

        public long Freed { get { return Ran ? Report.InUseFreed : 0; } }
        public uint? LoadBefore { get { return Ran ? Report.Before.LoadPercent : (uint?)null; } }
        public uint? LoadAfter { get { return Ran ? Report.After.LoadPercent : (uint?)null; } }

        public CleanHistoryEntry Copy()
        {
            return (CleanHistoryEntry)MemberwiseClone();
        }
    }

    /// <summary>
    /// The last cleans of this session, in memory only: the app never reads cleans.log back, so a file another program
    /// wrote can't put text on screen. Not thread-safe; AutoCleanService guards it with its lock. Pure: the clock is
    /// passed in.
    /// </summary>
    public sealed class CleanHistory
    {
        public const int Capacity = 20;
        /// <summary>How long after a clean the load is read again: the honest cost and benefit figure.</summary>
        public static readonly TimeSpan ReboundDelay = TimeSpan.FromMinutes(5);
        /// <summary>
        /// A reading later than ReboundDelay plus this (the PC slept, or sampling stopped) is not "five minutes later",
        /// so that rebound is dropped rather than shown with the wrong meaning.
        /// </summary>
        public static readonly TimeSpan ReboundWindow = TimeSpan.FromMinutes(2);

        readonly List<CleanHistoryEntry> entries = new List<CleanHistoryEntry>(); // oldest first

        public int Count { get { return entries.Count; } }

        /// <summary>
        /// Adds the clean, keeping the newest Capacity. A rebound still pending for an earlier clean is dropped: this
        /// clean changes the load, so that reading would no longer be the earlier clean's.
        /// </summary>
        public void Add(CleanHistoryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            foreach (CleanHistoryEntry e in entries) e.ReboundDone = true;
            entry.ReboundDone = !entry.Ran;
            entries.Add(entry);
            while (entries.Count > Capacity) entries.RemoveAt(0);
        }

        /// <summary>
        /// Records load as the rebound of each clean that ended ReboundDelay ago (within ReboundWindow), and returns
        /// copies of those entries. Each entry is returned at most once; one whose moment passed unseen, or whose time
        /// lies after nowUtc (the clock went back), never is.
        /// </summary>
        public List<CleanHistoryEntry> DueRebound(DateTime nowUtc, uint load)
        {
            var due = new List<CleanHistoryEntry>();
            foreach (CleanHistoryEntry e in entries)
            {
                if (e.ReboundDone) continue;
                TimeSpan age = nowUtc - e.EndUtc;
                if (age >= TimeSpan.Zero && age < ReboundDelay) continue;
                e.ReboundDone = true;
                if (age < TimeSpan.Zero || age > ReboundDelay + ReboundWindow) continue;
                e.LoadLater = load;
                due.Add(e.Copy());
            }
            return due;
        }

        /// <summary>Copies, newest first.</summary>
        public List<CleanHistoryEntry> Snapshot()
        {
            var list = new List<CleanHistoryEntry>(entries.Count);
            for (int i = entries.Count - 1; i >= 0; i--) list.Add(entries[i].Copy());
            return list;
        }
    }
}
