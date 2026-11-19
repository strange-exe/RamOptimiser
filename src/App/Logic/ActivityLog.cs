// What happened, and where the app shows it (package 3A: P7, and the app's part of E9, E22 and R4+E21): the
// tray tooltip's second line and status item, this session's Recent cleans and Recent activity lists, and the
// records the app adds to cleans.log. Pure: no WPF, so the tests compile it. There is one log file, cleans.log,
// shared with ramopt and written by CleanLog; the app never reads it back, so the lists come from memory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    /// <summary>The clean the tray talks about: when it ended, why, its report (null when it threw) and its error.</summary>
    public sealed class LastCleanInfo
    {
        public DateTime Utc;
        public CleanReason Reason;
        public CleanReport Report;
        public Exception Error;

        public bool Automatic { get { return Reason != CleanReason.Manual; } }
    }

    /// <summary>One line of Recent activity: a kind for the log record ("paused") and the words shown.</summary>
    public sealed class ActivityEvent
    {
        public string Kind;
        public string Text;
    }

    /// <summary>The tray's second tooltip line and its status item.</summary>
    public static class TrayStatus
    {
        /// <summary>
        /// The tooltip's second line, the first that applies: needs admin, paused, the last clean went wrong (failed,
        /// partly failed, hit its time limit, did not finish), backing off, the last clean's result. Null when there
        /// is nothing to say. This is the longest form; the tray uses TooltipLines, which also has shorter ones.
        /// </summary>
        public static string TooltipLine(AutoCleanState state, AutoCleanStatus st, LastCleanInfo last, DateTime localNow, CultureInfo culture)
        {
            IList<string> lines = TooltipLines(state, st, last, localNow, culture);
            return lines.Count == 0 ? null : lines[0];
        }

        /// <summary>
        /// The same line in the forms AppText.TrayTooltip tries in turn to fit the 63 characters NotifyIcon allows,
        /// longest first. A line about a clean drops its "when" before anything else, so the outcome is never what gets
        /// cut: "Auto-clean yesterday at 2:32 PM hit its time limit", then "Auto-clean yesterday hit its time limit",
        /// then "Last auto-clean hit its time limit". Empty when there is nothing to say.
        /// </summary>
        public static IList<string> TooltipLines(AutoCleanState state, AutoCleanStatus st, LastCleanInfo last, DateTime localNow, CultureInfo culture)
        {
            if (state == AutoCleanState.NeedsAdmin) return new[] { "Auto-clean paused: needs admin" };
            if (state == AutoCleanState.Paused)
            {
                string paused = AppText.PausedLine(st, culture);
                if (paused != null) return new[] { "Auto-clean p" + paused.Substring(1) };
            }
            string outcome = last == null ? null : FailureOutcome(last.Report);
            if (outcome == null)
            {
                if (state == AutoCleanState.BackingOff) return new[] { "Auto-clean slowed: cleans are not helping" };
                if (last == null) return new string[0];
                outcome = ResultOutcome(last.Report);
            }
            var lines = new List<string>();
            foreach (string head in Heads(last, localNow, culture)) lines.Add(head + outcome);
            return lines;
        }

        /// <summary>The clean's name with its time, then with its day only (an earlier day), then with neither.</summary>
        static IEnumerable<string> Heads(LastCleanInfo last, DateTime localNow, CultureInfo culture)
        {
            string name = last.Automatic ? "Auto-clean " : "Clean ";
            DateTime local = last.Utc.ToLocalTime();
            yield return name + AppText.When(local, localNow, culture);
            if (local.Date == localNow.Date.AddDays(-1)) yield return name + "yesterday";
            else if (local.Date != localNow.Date) yield return name + "on " + local.ToString("d", culture);
            yield return last.Automatic ? "Last auto-clean" : "Last clean";
        }

        /// <summary>What went wrong, after the head (" hit its time limit", ": 2 areas failed"); null when nothing did.</summary>
        static string FailureOutcome(CleanReport r)
        {
            if (r == null || r.Before == null || r.After == null) return " did not finish";
            if (r.Stopped == CleanStop.TimedOut) return " hit its time limit";
            if (r.Stopped == CleanStop.Cancelled) return " was stopped";
            if (r.Stopped == CleanStop.Busy) return null; // nothing ran, nothing failed
            int failed = 0, partial = 0;
            foreach (AreaResult a in r.Results)
            {
                if (a.Outcome == AreaOutcome.Failed) failed++;
                else if (a.Outcome == AreaOutcome.Partial) partial++;
            }
            if (failed > 0) return ": " + AreaCount(failed) + " failed";
            if (partial > 0) return ": " + AreaCount(partial) + " partly failed";
            return null;
        }

        static string ResultOutcome(CleanReport r)
        {
            if (r.Stopped == CleanStop.Busy) return " was skipped";
            return r.InUseFreed > 0 ? " freed " + Format.Bytes(r.InUseFreed) : " freed nothing";
        }

        static string AreaCount(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " area" : " areas");
        }

        /// <summary>
        /// The tray menu's status item: the same words as the window's result line (LastAutoClean for automatic
        /// cleans), so the tray never says something the window does not. Null before the first clean.
        /// </summary>
        public static string MenuLine(LastCleanInfo last, DateTime localNow, CultureInfo culture)
        {
            if (last == null) return null;
            DateTime local = last.Utc.ToLocalTime();
            string stopped = AppText.StoppedCleanLine(local, localNow, last.Report, last.Automatic, culture);
            if (stopped != null) return stopped;
            if (last.Automatic) return AppText.LastAutoClean(local, localNow, last.Report, culture);
            return AppText.ResultLine(local, localNow, last.Report, last.Error, false, culture);
        }
    }

    /// <summary>Wording and safety for the Recent cleans and Recent activity rows, and the event records in cleans.log.</summary>
    public static class ActivityLog
    {
        /// <summary>The longest row text, in characters.</summary>
        public const int MaxLine = 500;

        /// <summary>
        /// One safe line: CR, LF, tab and the Unicode line and paragraph separators become spaces, other control and
        /// format characters (bidi overrides, zero-width marks) are dropped by category, lone surrogates are dropped,
        /// and the text is cut at MaxLine without splitting a pair. Null gives "". The rules are SafeText.Line's
        /// (src/Core.cs), shared with the clean records CleanLog writes for ramopt.exe as well as the app.
        /// </summary>
        public static string Sanitise(string text)
        {
            return SafeText.Line(text, MaxLine);
        }

        /// <summary>"14:32" today, "Yesterday 14:32", or the culture's short date and the time before that.</summary>
        public static string RowTime(DateTime localTime, DateTime localNow, CultureInfo culture)
        {
            string time = localTime.ToString("t", culture);
            if (localTime.Date == localNow.Date) return time;
            if (localTime.Date == localNow.Date.AddDays(-1)) return "Yesterday " + time;
            return localTime.ToString("d", culture) + " " + time;
        }

        /// <summary>A row of either list: its time, two spaces, then the text made safe.</summary>
        public static string Row(DateTime localTime, DateTime localNow, string text, CultureInfo culture)
        {
            return RowTime(localTime, localNow, culture) + "  " + Sanitise(text);
        }

        /// <summary>A Recent cleans row for one history entry.</summary>
        public static string CleanRow(CleanHistoryEntry e, DateTime localNow, CultureInfo culture)
        {
            if (e == null) throw new ArgumentNullException("e");
            return Row(e.EndUtc.ToLocalTime(), localNow, CleanSummary(e.Reason, e.Report, e.Error, e.LoadLater), culture);
        }

        /// <summary>"Clean", "Auto-clean (high load)" or "Auto-clean (schedule)".</summary>
        public static string Label(CleanReason reason)
        {
            switch (reason)
            {
                case CleanReason.Threshold: return "Auto-clean (high load)";
                case CleanReason.Interval: return "Auto-clean (schedule)";
                default: return "Clean";
            }
        }

        /// <summary>
        /// What one clean did, on one line: "Auto-clean (high load): freed 1.2 GB; load 91% to 78%, 80% five minutes
        /// later; 3 of 4 areas ok; Drive write cache (writes to disk) failed: access is denied". A stopped clean says
        /// where it stopped instead of the areas ok; a busy skip and a clean that threw say only that.
        /// </summary>
        public static string CleanSummary(CleanReason reason, CleanReport report, string error, uint? loadLater)
        {
            string head = Label(reason);
            if (report != null && report.Stopped == CleanStop.Busy) return head + ": skipped, another RamOptimiser clean was running";
            if (report == null || report.Before == null || report.After == null)
                return Sanitise(head + ": did not finish: " + AppText.Plain(error, "unknown error"));

            var parts = new List<string>();
            parts.Add(report.InUseFreed > 0 ? "freed " + Format.Bytes(report.InUseFreed) : "freed nothing");
            string load = "load " + Percent(report.Before.LoadPercent) + " to " + Percent(report.After.LoadPercent);
            if (loadLater.HasValue) load += ", " + Percent(loadLater.Value) + " five minutes later";
            parts.Add(load);
            if (!report.Completed)
            {
                int ran = report.AreasRun, total = report.AreasTotal;
                string what = report.Stopped == CleanStop.TimedOut ? "hit its " + AppText.LimitText(report.Deadline) + " limit" : "was stopped";
                parts.Add(what + " after " + Number(ran) + " of " + Number(total) + (total == 1 ? " area" : " areas"));
            }
            else
            {
                int ok = 0, total = report.Results.Count;
                foreach (AreaResult a in report.Results) if (a.Outcome == AreaOutcome.Ok) ok++;
                parts.Add(Number(ok) + " of " + Number(total) + (total == 1 ? " area ok" : " areas ok"));
            }
            foreach (AreaResult a in report.Results)
            {
                if (a.Outcome == AreaOutcome.Failed) parts.Add(AppText.AreaLabel(a.Area) + " failed: " + AppText.FailureReason(a));
                else if (a.Outcome == AreaOutcome.Partial) parts.Add(AppText.AreaLabel(a.Area) + " partly worked: " + AppText.Plain(a.Message, "part of it failed"));
            }
            return Sanitise(head + ": " + string.Join("; ", parts.ToArray()));
        }

        static string Percent(uint p) { return p.ToString(CultureInfo.InvariantCulture) + "%"; }

        static string Number(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        /// <summary>
        /// The cleans.log record for an activity event, one line: {"v":1,"type":"event","time":"...Z","source":"gui",
        /// "event":"paused","text":"..."}. The text is made safe and cut to CleanLog.MaxTextChars. Pure.
        /// </summary>
        public static string EventRecord(DateTime utc, ActivityEvent e)
        {
            if (e == null) throw new ArgumentNullException("e");
            DateTime u = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("v"); w.Number(1);
            w.Name("type"); w.String("event");
            w.Name("time"); w.String(u.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture));
            w.Name("source"); w.String(GuiCleanLog.Source);
            w.Name("event"); w.String(Cap(Sanitise(e.Kind)));
            w.Name("text"); w.String(Cap(Sanitise(e.Text)));
            w.EndObject();
            return w.ToString();
        }

        static string Cap(string s)
        {
            if (s.Length <= CleanLog.MaxTextChars) return s;
            int n = CleanLog.MaxTextChars;
            if (char.IsHighSurrogate(s[n - 1])) n--;
            return s.Substring(0, n);
        }
    }

    /// <summary>The app's clean records in cleans.log: written under the clean lock, so they never interleave with ramopt's.</summary>
    public static class GuiCleanLog
    {
        public const string Source = "gui";

        public static string ReasonName(CleanReason reason)
        {
            switch (reason)
            {
                case CleanReason.Threshold: return "threshold";
                case CleanReason.Interval: return "interval";
                default: return "manual";
            }
        }

        public static string StopName(CleanStop stop)
        {
            switch (stop)
            {
                case CleanStop.Cancelled: return "cancelled";
                case CleanStop.TimedOut: return "timed-out";
                case CleanStop.Busy: return "busy";
                default: return "none";
            }
        }

        /// <summary>The clean record for job's report, as CleanLog writes it. Pure.</summary>
        public static string Record(CleanJob job, CleanReport report, DateTime utc)
        {
            return CleanLog.FormatClean(report, Source, ReasonName(job.Reason), StopName(report.Stopped), job.Id, utc);
        }

        /// <summary>
        /// Runs job through clean (Cleaner.Clean in the app) with the clean lock taken without waiting (a clean already
        /// running elsewhere means this one is skipped, not queued), and appends the record while the lock is held. A
        /// busy skip ran nothing and held no lock: its record is appended after. A clean that throws before its record
        /// was written gets one with its error (no before or after) and is rethrown, so every clean leaves one line.
        /// A failing append never fails the clean.
        /// </summary>
        public static CleanReport Run(CleanJob job, ICollection<string> services, Func<CleanRequest, CleanReport> clean,
            Action<string> append, Func<DateTime> clock)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (clean == null) throw new ArgumentNullException("clean");
            var req = new CleanRequest { Areas = job.Areas, Services = services, Deadline = job.Deadline, LockWait = TimeSpan.Zero };
            bool written = false;
            req.UnderLock = r =>
            {
                if (r == null) return;
                written = true;
                TryAppend(append, () => Record(job, r, clock()));
            };
            CleanReport report;
            try { report = clean(req); }
            catch (Exception ex)
            {
                if (!written) TryAppend(append, () => ErrorRecord(job, ex, clock()));
                throw;
            }
            if (report != null && report.Stopped == CleanStop.Busy) TryAppend(append, () => Record(job, report, clock()));
            return report;
        }

        /// <summary>The record of a clean that threw before reporting: no before or after, and its error. Pure.</summary>
        public static string ErrorRecord(CleanJob job, Exception error, DateTime utc)
        {
            return CleanLog.FormatClean(new CleanReport(), Source, ReasonName(job.Reason), StopName(CleanStop.None), job.Id, utc,
                ActivityLog.Sanitise(AppText.Plain(error == null ? null : error.Message, "unknown error")));
        }

        static void TryAppend(Action<string> append, Func<string> record)
        {
            if (append == null) return;
            try { append(record()); }
            catch (Exception) { } // the log is a record, never a reason for a clean to fail
        }
    }

    /// <summary>
    /// The app's one writer to cleans.log. The clean thread (under the clean lock), the tick thread (rebounds) and the
    /// UI (activity events) all write; one gate means they never collide with each other, so CleanLog.TryAppend's
    /// short retries only have to absorb ramopt. Post queues a line for one worker at a time, which writes the queue
    /// in order, so "paused" never lands after "resumed". WriteNow writes on the caller's thread. Never throws.
    /// </summary>
    public sealed class SerialLog
    {
        readonly Action<string> write;
        readonly Action<Action> post;
        readonly object writeGate = new object();
        readonly Queue<string> pending = new Queue<string>();
        bool draining; // guarded by pending

        /// <param name="write">Writes one line (CleanLog.TryAppend in the app).</param>
        /// <param name="post">Runs work off the caller's thread (the thread pool in the app).</param>
        public SerialLog(Action<string> write, Action<Action> post)
        {
            if (write == null) throw new ArgumentNullException("write");
            if (post == null) throw new ArgumentNullException("post");
            this.write = write;
            this.post = post;
        }

        /// <summary>Writes line now, on this thread, after any write already under way.</summary>
        public void WriteNow(string line)
        {
            lock (writeGate) Write(line);
        }

        /// <summary>Queues line; a worker writes the queue in order. Returns at once.</summary>
        public void Post(string line)
        {
            lock (pending)
            {
                pending.Enqueue(line);
                if (draining) return;
                draining = true;
            }
            try { post(Drain); }
            catch (Exception) { lock (pending) draining = false; }
        }

        void Drain()
        {
            while (true)
            {
                string line;
                lock (pending)
                {
                    if (pending.Count == 0) { draining = false; return; }
                    line = pending.Dequeue();
                }
                lock (writeGate) Write(line);
            }
        }

        void Write(string line)
        {
            try { write(line); }
            catch (Exception) { } // the log is a record, never a reason for anything else to fail
        }
    }

    /// <summary>This session's Recent activity rows, newest first, at most capacity.</summary>
    public sealed class ActivityHistory
    {
        readonly int capacity;
        readonly List<string> lines = new List<string>(); // oldest first

        public ActivityHistory(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            this.capacity = capacity;
        }

        public void Add(string line)
        {
            lines.Add(ActivityLog.Sanitise(line));
            while (lines.Count > capacity) lines.RemoveAt(0);
        }

        public IList<string> Newest()
        {
            var list = new List<string>(lines);
            list.Reverse();
            return list;
        }
    }

    /// <summary>Whether a sampling failure goes in the activity list: a new message, or the same one 10 minutes on.</summary>
    public static class ErrorGate
    {
        public static readonly TimeSpan Repeat = TimeSpan.FromMinutes(10);

        public static bool ShouldLog(string lastMessage, DateTime? lastUtc, string message, DateTime nowUtc)
        {
            if (lastUtc == null || !string.Equals(lastMessage, message, StringComparison.Ordinal)) return true;
            TimeSpan since = nowUtc - lastUtc.Value;
            return since < TimeSpan.Zero || since >= Repeat;
        }
    }

    /// <summary>The auto-clean changes worth a line in Recent activity, found by comparing two statuses. Pure.</summary>
    public static class ActivityEvents
    {
        /// <summary>
        /// A pause starting, ending by itself or resumed by the user (userResumed), and the back-off starting or
        /// ending. A longer back-off is not news. before null: the first status, so only a pause (restored from an
        /// earlier run) counts.
        /// </summary>
        public static List<ActivityEvent> FromStatus(AutoCleanStatus before, AutoCleanStatus after, bool userResumed,
            int thresholdPercent, CultureInfo culture)
        {
            var list = new List<ActivityEvent>();
            bool wasPaused = before != null && before.Paused, isPaused = after != null && after.Paused;
            if (!wasPaused && isPaused)
            {
                string paused = AppText.PausedLine(after, culture);
                list.Add(new ActivityEvent { Kind = "paused", Text = paused == null ? "Auto-clean paused." : "Auto-clean p" + paused.Substring(1) + "." });
            }
            else if (wasPaused && !isPaused)
            {
                list.Add(userResumed
                    ? new ActivityEvent { Kind = "resumed", Text = "Auto-clean resumed." }
                    : new ActivityEvent { Kind = "pause-ended", Text = "Auto-clean pause ended." });
            }
            if (before == null) return list;
            int was = before.FutileStreak, now = after == null ? 0 : after.FutileStreak;
            if (was == 0 && now > 0)
            {
                string line = AppText.BackoffLine(now, thresholdPercent);
                list.Add(new ActivityEvent { Kind = "backoff-started", Text = "Auto-clean is " + char.ToLowerInvariant(line[0]) + line.Substring(1) });
            }
            else if (was > 0 && now == 0)
                list.Add(new ActivityEvent { Kind = "backoff-ended", Text = "Auto-clean is back to its normal wait." });
            return list;
        }

        /// <summary>
        /// True only for a sampling failure: that is what "Memory status could not be read" and its recovery line
        /// mean. An error from the service's own handlers (Other) is not about reading memory.
        /// </summary>
        public static bool IsReadFailure(ServiceErrorSource source)
        {
            return source == ServiceErrorSource.Sample;
        }

        public static ActivityEvent ReadFailed(string message)
        {
            return new ActivityEvent { Kind = "read-failed", Text = "Memory status could not be read: " + AppText.Plain(message, "unknown error") + "." };
        }

        public static ActivityEvent ReadRecovered()
        {
            return new ActivityEvent { Kind = "read-recovered", Text = "Memory status can be read again." };
        }
    }
}
