// Pure, testable logic for the GUI: wording, validation and theme resolution. No WPF or WinForms here,
// so the test runner compiles this file as it is.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    public enum ResolvedTheme { Light, Dark, HighContrast }

    public enum AreaState { Ok, Failed, Skipped, Partial }

    /// <summary>One line of the clean result: the area's label, its state and the words shown for it.</summary>
    public sealed class AreaLine
    {
        public Areas Area;
        public string Label;
        public AreaState State;
        /// <summary>"ok", "failed: reason" or "skipped: reason" (a Partial area reads "ok: what it did; what failed").</summary>
        public string Status;
        /// <summary>How long the area took ("1.8 s", "&lt;0.1 s"); null for a skipped area, which ran nothing (E8).</summary>
        public string Time;
    }

    /// <summary>
    /// What shows under a Startup row after a change (E13): an error when nothing changed (a refusal or a failure),
    /// or a neutral note when the change worked only in part. Always a glyph plus words, never colour alone.
    /// </summary>
    public sealed class RowNote
    {
        public string Title, Text;
        public bool IsError;
        /// <summary>The Styles.xaml geometry: IconError for an error, IconWarn for a note.</summary>
        public string Glyph { get { return IsError ? "IconError" : "IconWarn"; } }
        /// <summary>The glyph's brush token.</summary>
        public string Stroke { get { return IsError ? "Danger" : "Caution"; } }
        /// <summary>The bar's style: the danger-tinted InfoBar, or the info-tinted NoticeBar.</summary>
        public string BarStyle { get { return IsError ? "InfoBar" : "NoticeBar"; } }
        public string DismissName { get { return IsError ? "Dismiss error" : "Dismiss note"; } }
        /// <summary>The bar's accessible name, read out once when it first shows.</summary>
        public string Spoken { get { return Title + ". " + Text; } }
    }

    /// <summary>Where the user paused auto-clean, for the bar's second line (2A); Unknown says nothing.</summary>
    public enum PauseOrigin { Unknown, Tray, Settings, Restored }

    /// <summary>
    /// Keeps the "will a clean help" line from flickering at a band edge (R1): the first reading shows at once, and
    /// after that a different advice shows only once two samples in a row agree on it. Reset when the figures go
    /// stale, so the next live reading shows at once again. Pure.
    /// </summary>
    public sealed class AdviceSmoother
    {
        CleanAdvice pending;
        int count;
        bool started;

        public CleanAdvice Shown { get; private set; }

        public CleanAdvice Next(CleanAdvice candidate)
        {
            if (!started)
            {
                started = true;
                Shown = candidate;
                count = 0;
                return Shown;
            }
            if (candidate == Shown) { count = 0; return Shown; }
            if (count > 0 && candidate == pending) count++;
            else { pending = candidate; count = 1; }
            if (count >= 2)
            {
                Shown = candidate;
                count = 0;
            }
            return Shown;
        }

        public void Reset()
        {
            started = false;
            count = 0;
            Shown = CleanAdvice.None;
        }
    }

    /// <summary>
    /// Auto-clean as the user sees it, derived each time and never stored. First match wins: Off, NeedsAdmin,
    /// Paused (the user's pause), BackingOff (recent cleans did not help, so the threshold wait is longer), Armed.
    /// </summary>
    public enum AutoCleanState { Off, NeedsAdmin, Paused, BackingOff, Armed }

    /// <summary>One status message about auto-clean: a line, an optional second line, a glyph and its colour.</summary>
    public sealed class AutoNotice
    {
        public string Title, Detail;
        /// <summary>Styles.xaml / MainWindow.xaml geometry key and its stroke brush key; Glyph null for words alone.</summary>
        public string Glyph, Stroke;
        /// <summary>Offers Resume (a pause), or a dismiss button (back-off; it comes back when the streak changes).</summary>
        public bool Resume, Dismiss;
        /// <summary>Changes when the message does, so a dismissed one stays hidden only until there is news.</summary>
        public string Key;

        /// <summary>Both lines, for the accessible name.</summary>
        public string Name { get { return Detail == null ? Title : Title + " " + Detail; } }
    }

    /// <summary>The Memory tab bar's decision for one refresh (AppText.NextAutoBar).</summary>
    public sealed class AutoBarStep
    {
        public bool Show, Announce;
        public string DismissedKey, AnnouncedKind;
    }

    public static partial class AppText
    {
        public const string AppName = "RAM Optimiser";
        /// <summary>.NET Framework's NotifyIcon.Text throws above 63 characters (newer .NET allows 127).</summary>
        public const int MaxTooltipLength = 63;

        public const string StandbyHelp = "Standby is Windows' cache of recent files. It already counts as available, "
            + "so clearing it frees nothing you lack and apps load slower until it refills.";
        public const string NeedsAdmin = "needs administrator rights";
        public const string CleanNeedsAdmin = "Cleaning needs administrator rights. Use Restart as administrator, below this button.";
        public const string LastAreaReason = "At least one area must stay selected.";
        public const string StartupNeedsAdmin = "Needs administrator rights: restart as administrator to change this.";

        /// <summary>The Restart as administrator link while the UAC prompt is up (its accessible name stays the same).</summary>
        public const string WaitingForApproval = "Waiting for approval...";
        public const string WaitingForApprovalHelp = "Waiting for the administrator prompt.";
        /// <summary>The tray menu's disabled item while settings.ini could not be written.</summary>
        public const string SettingsNotSaved = "Settings not saved";

        public static string RestartFailed(string reason)
        {
            return "Could not restart as administrator: " + Plain(reason, "unknown error") + ".";
        }

        /// <summary>Says what a failed save means: the change holds only for this run.</summary>
        public static string SaveFailedText(string reason)
        {
            return "Settings could not be saved: " + Plain(reason, "unknown error") + ". They apply until RAM Optimiser closes.";
        }

        /// <summary>A startup row the app may not change, read as one piece of text that always states on or off.</summary>
        public static string LockedRowName(string label, string lockText, bool on)
        {
            return label + ": " + lockText + (on ? ", starts with Windows" : ", does not start with Windows");
        }

        /// <summary>
        /// The line under Start with Windows for a task that exists but is not this program's (stale), or that
        /// could not be read. Null when there is no task or it is ours: the switch alone says that.
        /// </summary>
        public static string StartupStatusLine(StartupTaskStatus status)
        {
            if (status == null) return null;
            const string Replaces = " Turning this on replaces it.";
            switch (status.State)
            {
                case StartupTaskState.Other:
                    if (status.SameProgram) return "A start-up task for this program exists with other settings." + Replaces;
                    if (!string.IsNullOrEmpty(status.Command))
                        return "A start-up task exists but starts another copy: " + status.Command + "." + Replaces;
                    return "A start-up task exists but does not start this program." + Replaces;
                case StartupTaskState.Unreadable:
                    string error = string.IsNullOrEmpty(status.Error) ? null : status.Error.Trim().TrimEnd('.');
                    return string.IsNullOrEmpty(error) ? "Could not read the start-up task." : "Could not read the start-up task: " + error + ".";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Everything said under Start with Windows, in order: a failed change, the task's state, then why the
        /// switch is locked. Running as another account replaces it all: nothing here applies to the user.
        /// </summary>
        public static string StartupReason(StartupTaskStatus status, bool elevated, string otherAccount, string failure)
        {
            if (!string.IsNullOrEmpty(otherAccount)) return StartupOtherAccount(otherAccount);
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(failure)) parts.Add(failure);
            string line = StartupStatusLine(status);
            if (line != null) parts.Add(line);
            if (!elevated) parts.Add(StartupNeedsAdmin);
            return parts.Count == 0 ? null : string.Join(" ", parts.ToArray());
        }

        public static string StartupOtherAccount(string account)
        {
            return "Can't change this here: this copy runs as " + account + ", so the task would start for that account, not yours.";
        }

        /// <summary>Shown for as long as an elevated copy runs as another account than the one that started it.</summary>
        public static string OtherAccountNotice(string account)
        {
            return "Running as " + account + ", the account that approved the administrator prompt. "
                + "This copy uses that account's settings and start-up task, not yours.";
        }

        public static bool StartupSwitchEnabled(bool elevated, bool busy, string otherAccount)
        {
            return elevated && !busy && string.IsNullOrEmpty(otherAccount);
        }

        /// <summary>
        /// The not-elevated notice. Some Windows builds let any user read the standby and modified lists;
        /// only when they are refused does the notice say so.
        /// </summary>
        public static string ElevationNotice(bool listsReadable)
        {
            return listsReadable
                ? "Cleaning needs administrator rights. Until then RAM Optimiser shows memory status but cannot clean."
                : "Cleaning needs administrator rights. Until then RAM Optimiser shows memory load but cannot clean or read the standby and modified lists.";
        }

        public static string ThresholdError
        {
            get
            {
                return "Enter a whole number from " + AppSettings.MinThresholdPercent.ToString(CultureInfo.InvariantCulture)
                    + " to " + AppSettings.MaxThresholdPercent.ToString(CultureInfo.InvariantCulture) + ".";
            }
        }

        public static string AreaLabel(Areas area)
        {
            switch (area)
            {
                case Areas.WorkingSets: return "Process working sets";
                case Areas.SystemFileCache: return "System file cache";
                case Areas.ModifiedList: return "Modified pages";
                case Areas.LowPriorityStandby: return "Low-priority standby";
                case Areas.StandbyList: return "Full standby cache (slower afterwards)";
                case Areas.CombinePages: return "Combine identical pages";
                case Areas.RegistryCache: return "Registry cache";
                case Areas.VolumeCache: return "Drive write cache (writes to disk)";
                case Areas.Services: return "Unused services (Power Clean)";
                default: throw new ArgumentOutOfRangeException("area");
            }
        }

        /// <summary>Status words for one area. Colour is never the only signal: the words say it all.</summary>
        public static string Status(AreaResult r)
        {
            if (r == null) throw new ArgumentNullException("r");
            // An ok still carries what it did: "stopped 3 services", "3 passes", "flushed 1 of 2 drives; E: ...".
            if (r.Ok) return string.IsNullOrEmpty(r.Message) ? "ok" : "ok: " + Plain(r.Message, "");
            if (r.Skipped) return "skipped: " + Plain(r.Message, "not available on this PC");
            return "failed: " + FailureReason(r);
        }

        internal static string FailureReason(AreaResult r)
        {
            if (r.NtStatus == Native.STATUS_PRIVILEGE_NOT_HELD || r.NtStatus == Native.STATUS_ACCESS_DENIED
                || r.Win32Error == ErrorPrivilegeNotHeld || r.Win32Error == ErrorAccessDenied)
                return NeedsAdmin;
            if (r.NtStatus != 0)
                return "not supported on this PC (0x" + r.NtStatus.ToString("X8", CultureInfo.InvariantCulture) + ")";
            if (r.Win32Error != 0)
                return Plain(r.Message, "Windows error") + " (error " + r.Win32Error.ToString(CultureInfo.InvariantCulture) + ")";
            return Plain(r.Message, "unknown error");
        }

        const int ErrorAccessDenied = 5, ErrorPrivilegeNotHeld = 1314;

        /// <summary>Lower-cases the first letter and drops a trailing full stop, so it reads after "failed: ".</summary>
        internal static string Plain(string message, string fallback)
        {
            string m = message == null ? "" : message.Trim();
            while (m.EndsWith(".", StringComparison.Ordinal)) m = m.Substring(0, m.Length - 1).TrimEnd();
            if (m.Length == 0) return fallback;
            if (m.Length > 1 && char.IsUpper(m[0]) && !char.IsUpper(m[1]))
                m = char.ToLowerInvariant(m[0]) + m.Substring(1);
            return m;
        }

        public static AreaLine Describe(AreaResult r)
        {
            return new AreaLine
            {
                Area = r.Area,
                Label = AreaLabel(r.Area),
                // Partial keeps its "ok: ..." words, with a caution glyph that matches the summary's warning.
                State = r.Outcome == AreaOutcome.Partial ? AreaState.Partial
                    : r.Ok ? AreaState.Ok : r.Skipped ? AreaState.Skipped : AreaState.Failed,
                Status = Status(r),
                Time = r.Skipped ? null : DurationText(r.Duration)
            };
        }

        /// <summary>A result row's accessible name: "Process working sets: ok, 1.8 s".</summary>
        public static string AreaRowName(AreaLine line)
        {
            string name = line.Label + ": " + line.Status;
            return line.Time == null ? name : name + ", " + line.Time;
        }

        /// <summary>The last line of the per-area details: "Took 3.4 s."; null when the clean never ran (E8).</summary>
        public static string TookLine(CleanReport report)
        {
            if (report == null || report.Stopped == CleanStop.Busy) return null;
            return "Took " + DurationText(report.Duration) + ".";
        }

        public static string Headline(CleanReport report)
        {
            if (report == null) return "Clean did not finish";
            long freed = report.InUseFreed;
            return freed > 0 ? "Freed " + Format.Bytes(freed) : "Nothing freed";
        }

        public static string Summary(CleanReport report)
        {
            if (report == null) throw new ArgumentNullException("report");
            int ok = 0;
            foreach (AreaResult r in report.Results) if (r.Ok) ok++;
            int total = report.Results.Count;
            return "In use went from " + Format.Bytes(report.Before.InUse) + " to " + Format.Bytes(report.After.InUse)
                + " (" + report.After.LoadPercent.ToString(CultureInfo.InvariantCulture) + "% load). "
                + ok.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture)
                + (total == 1 ? " area" : " areas") + " cleaned.";
        }

        public static string FailedSummary(Exception error)
        {
            return error == null ? "The clean stopped before it could report a result."
                : "The clean stopped: " + Plain(error.Message, "unknown error") + ".";
        }

        public static string InUseLine(MemorySnapshot s)
        {
            return Format.Bytes(s.InUse) + " of " + Format.Bytes(s.TotalPhys) + " in use";
        }

        /// <summary>One name for the load card, so a screen reader hears the number and its meaning together.</summary>
        public static string LoadCardName(MemorySnapshot s)
        {
            return "Memory load " + s.LoadPercent.ToString(CultureInfo.InvariantCulture) + " percent, " + InUseLine(s);
        }

        public static string RowName(string label, string hint, string value)
        {
            string name = label;
            if (!string.IsNullOrEmpty(hint)) name += ", " + hint.Replace("+", "plus");
            return name + ": " + value;
        }

        /// <summary>The row's name with the change from the last clean, e.g. "In use: 11.6 GB, changed by -2.8 GB".</summary>
        public static string RowName(string label, string hint, string value, string change)
        {
            string name = RowName(label, hint, value);
            return string.IsNullOrEmpty(change) ? name : name + ", changed by " + change;
        }

        /// <summary>The one admin line under Clean memory, followed by the Restart as administrator link.</summary>
        public const string AdminLine = "Needs administrator rights to clean.";
        /// <summary>Shown under the map when the page lists are refused and the map has only two parts.</summary>
        public const string ListsNeedAdmin = "Modified, standby and free need administrator rights to read.";

        /// <summary>Under a two-part map: why modified, standby and free are missing.</summary>
        public static string ListsNote(bool elevated)
        {
            return elevated ? "Modified, standby and free could not be read." : ListsNeedAdmin;
        }

        /// <summary>The words after the big percent: "of 15.2 GB in use".</summary>
        public static string HeadlineCaption(MemorySnapshot s)
        {
            return "of " + Format.Bytes(s.TotalPhys) + " in use";
        }

        /// <summary>The map's accessible name: every part as text, so the bar is never the only way to read it.</summary>
        public static string MapName(MemoryMap m, ulong total)
        {
            string text = "Memory map of " + Format.Bytes(total) + ": in use " + Format.Bytes(m.Bytes(MapPart.InUse));
            if (!m.Detailed) return text + ", available " + Format.Bytes(m.Bytes(MapPart.Free));
            return text + ", modified " + Format.Bytes(m.Bytes(MapPart.Modified)) + ", standby " + Format.Bytes(m.Bytes(MapPart.Standby))
                + ", free " + Format.Bytes(m.Bytes(MapPart.Free));
        }

        const long OneMegabyte = 1024 * 1024;

        /// <summary>Changes under 1 MB are noise between two readings, not something the clean did.</summary>
        public static bool IsNoChange(long bytes)
        {
            return bytes > -OneMegabyte && bytes < OneMegabyte;
        }

        /// <summary>A signed change for the rows after a clean: "-2.8 GB", "+117.0 MB", or "0 MB".</summary>
        public static string Change(long bytes)
        {
            if (IsNoChange(bytes)) return "0 MB";
            return (bytes < 0 ? "-" : "+") + Format.Bytes(bytes == long.MinValue ? long.MaxValue : Math.Abs(bytes));
        }

        /// <summary>The one muted sentence under "Freed 2.8 GB".</summary>
        public static string ResultSentence(CleanReport report)
        {
            if (report == null) return FailedSummary(null);
            int ok = 0, failed = 0;
            foreach (AreaResult r in report.Results)
            {
                if (r.Ok) ok++;
                else if (!r.Skipped) failed++;
            }
            int total = report.Results.Count;
            string text = ok.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture)
                + (total == 1 ? " area" : " areas") + " cleaned";
            if (failed > 0) text += ", " + failed.ToString(CultureInfo.InvariantCulture) + " failed";
            return text + "; load is now " + report.After.LoadPercent.ToString(CultureInfo.InvariantCulture) + "%.";
        }

        /// <summary>The disclosure that shows the per-area results.</summary>
        public static string DetailsLabel(bool expanded, int count)
        {
            if (expanded) return "Hide areas";
            return "Show " + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " area" : " areas");
        }

        public static string TrayTooltip(MemorySnapshot s)
        {
            string text = s == null ? AppName + ": memory status unavailable"
                : AppName + ": " + s.LoadPercent.ToString(CultureInfo.InvariantCulture) + "% ("
                  + Format.Bytes(s.InUse) + " of " + Format.Bytes(s.TotalPhys) + ")";
            return text.Length <= MaxTooltipLength ? text : text.Substring(0, MaxTooltipLength);
        }

        /// <summary>
        /// With a second line (TrayStatus.TooltipLine): "RAM Optimiser: 72%", a line break, then that line, cut at the
        /// last space that keeps the whole within MaxTooltipLength (long time formats such as "12:32 p. m." can need
        /// it). Never cut mid-word: a line with no such space is left out. line2 null: TrayTooltip(s).
        /// </summary>
        public static string TrayTooltip(MemorySnapshot s, string line2)
        {
            return TrayTooltip(s, string.IsNullOrEmpty(line2) ? null : new[] { line2 });
        }

        /// <summary>
        /// With the second line's forms, longest first (TrayStatus.TooltipLines): the first form that fits whole, else
        /// the shortest cut at a word as above. Null or empty: TrayTooltip(s).
        /// </summary>
        public static string TrayTooltip(MemorySnapshot s, IList<string> forms)
        {
            if (forms == null || forms.Count == 0) return TrayTooltip(s);
            string line1 = AppName + ": " + (s == null ? "load unknown" : s.LoadPercent.ToString(CultureInfo.InvariantCulture) + "%");
            int room = MaxTooltipLength - line1.Length - 1;
            string fit = null;
            foreach (string form in forms)
            {
                if (string.IsNullOrEmpty(form)) continue;
                string line = form.Trim();
                if (line.Length > 0 && line.Length <= room) { fit = line; break; }
            }
            if (fit == null)
            {
                string last = forms[forms.Count - 1];
                fit = string.IsNullOrEmpty(last) ? null : FitWords(last.Trim(), room);
            }
            return fit == null ? TrayTooltip(s) : line1 + "\n" + fit;
        }

        /// <summary>The note under Settings > Activity's log path; without a data folder there is no file to describe.</summary>
        public static string CleanLogNote(bool hasFile)
        {
            const string lists = " The lists above show this session only.";
            return hasFile
                ? "Every clean the app runs is added to this file, kept under 256 KB with one older copy." + lists
                : "This account has no local data folder, so cleans are not saved to a file." + lists;
        }

        /// <summary>text if it is at most room characters, else its words up to the last space that fits; null if none.</summary>
        static string FitWords(string text, int room)
        {
            if (room <= 0 || text.Length == 0) return null;
            if (text.Length <= room) return text;
            int cut = text.LastIndexOf(' ', room);
            if (cut <= 0) return null;
            string fit = text.Substring(0, cut).TrimEnd(' ', ',', ';', ':');
            return fit.Length == 0 ? null : fit;
        }

        /// <summary>The auto-clean row's second line, e.g. "when load reaches 85%, and every 30 minutes".</summary>
        public static string AutoDescription(AppSettings s)
        {
            string text = "when load reaches " + s.ThresholdPercent.ToString(CultureInfo.InvariantCulture) + "%";
            if (s.IntervalMinutes > 0) text += ", and " + IntervalPhrase(s.IntervalMinutes);
            return text;
        }

        /// <summary>The Clean button's label. It keeps its name under Power Clean (the line under it says so),
        /// so the main action never renames itself.</summary>
        public static string CleanLabel(bool powerClean, bool cleaning)
        {
            return cleaning ? "Cleaning..." : "Clean memory";
        }

        /// <summary>The line under Clean memory while Power Clean is on; null when it is off.</summary>
        public static string PowerNote(bool powerClean, int n)
        {
            if (!powerClean) return null;
            string text = "Power Clean is on: cleans every area";
            if (n > 0) text += " and stops " + n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " chosen service" : " chosen services");
            return text + ".";
        }

        /// <summary>First letter upper case: the shared phrases above read mid-sentence, labels start a line.</summary>
        public static string Sentence(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>
        /// The line under Clean memory after any clean, manual or automatic: "Last clean at 14:32 freed 2.8 GB.
        /// Load went from 81% to 74%." Failures are counted, so a partial clean never reads as a full one.
        /// </summary>
        public static string LastClean(DateTime localTime, CleanReport report, bool automatic, CultureInfo culture)
        {
            return LastClean(localTime, localTime, report, automatic, culture);
        }

        /// <summary>
        /// As above, read at localNow: a clean from an earlier day says so ("Last clean yesterday at 14:32", "on
        /// 15/11/2026 at 14:32"), since the app can sit in the tray for days. Recomputed as the day changes.
        /// </summary>
        public static string LastClean(DateTime localTime, DateTime localNow, CleanReport report, bool automatic, CultureInfo culture)
        {
            string head = (automatic ? "Auto-clean " : "Last clean ") + When(localTime, localNow, culture);
            if (report == null || report.Before == null || report.After == null) return head + " did not finish.";
            int failed = CountOutcome(report, AreaOutcome.Failed), partial = CountOutcome(report, AreaOutcome.Partial);
            string text = head + (report.InUseFreed > 0 ? " freed " + Format.Bytes(report.InUseFreed) : " freed nothing")
                + ". Load went from " + report.Before.LoadPercent.ToString(CultureInfo.InvariantCulture) + "% to "
                + report.After.LoadPercent.ToString(CultureInfo.InvariantCulture) + "%.";
            if (failed > 0) text += " " + failed.ToString(CultureInfo.InvariantCulture) + (failed == 1 ? " area failed." : " areas failed.");
            if (partial > 0) text += " " + partial.ToString(CultureInfo.InvariantCulture) + (partial == 1 ? " area partly failed." : " areas partly failed.");
            return text;
        }

        /// <summary>"at 14:32" today, "yesterday at 14:32", or "on 15/11/2026 at 14:32" (the culture's short date).</summary>
        public static string When(DateTime localTime, DateTime localNow, CultureInfo culture)
        {
            string time = "at " + localTime.ToString("t", culture);
            if (localTime.Date == localNow.Date) return time;
            if (localTime.Date == localNow.Date.AddDays(-1)) return "yesterday " + time;
            return "on " + localTime.ToString("d", culture) + " " + time;
        }

        /// <summary>
        /// The result line: the last clean, plus why it stopped when it threw before reporting anything. A clean that
        /// hit its time limit or was skipped because another clean was running says so (StoppedCleanLine).
        /// </summary>
        public static string ResultLine(DateTime localTime, DateTime localNow, CleanReport report, Exception error, bool automatic, CultureInfo culture)
        {
            string stopped = StoppedCleanLine(localTime, localNow, report, automatic, culture);
            if (stopped != null) return stopped;
            string line = LastClean(localTime, localNow, report, automatic, culture);
            if (report == null && error != null) line += " " + FailedSummary(error);
            return line;
        }

        /// <summary>The words after the load figure until a sample arrives, or when none could be read; null once live.</summary>
        public static string MemoryCaption(MemoryViewState state)
        {
            if (state == MemoryViewState.Initial) return "Reading memory...";
            if (state == MemoryViewState.FailedNoData) return "Unavailable";
            return null;
        }

        public const string CleanNeedsData = "Clean memory waits until memory status can be read.";

        /// <summary>Clean needs administrator rights, no clean running, and memory status read at least once if it failed.</summary>
        public static bool CleanEnabled(bool elevated, bool cleaning, MemoryViewState state)
        {
            return elevated && !cleaning && state != MemoryViewState.FailedNoData;
        }

        /// <summary>Clean memory's help text: why it is unavailable, else the Power Clean note (or nothing).</summary>
        public static string CleanHelp(bool elevated, MemoryViewState state, string powerNote)
        {
            if (!elevated) return CleanNeedsAdmin;
            if (state == MemoryViewState.FailedNoData) return CleanNeedsData;
            return powerNote ?? "";
        }

        /// <summary>The banner when a sample fails; before any figure was read it also says Clean waits.</summary>
        public static string SampleErrorText(string message, int retrySeconds, bool hasData)
        {
            string text = "Memory status could not be read: " + Plain(message, "unknown error") + ". Trying again every "
                + retrySeconds.ToString(CultureInfo.InvariantCulture) + " seconds.";
            return hasData ? text : text + " Clean memory waits until it can be read.";
        }

        /// <summary>Under the figures while sampling fails: when they were last read, so they don't pass for current.</summary>
        public static string LastReadLine(DateTime localRead, DateTime localNow, CultureInfo culture)
        {
            return "Last read " + When(localRead, localNow, culture) + ".";
        }

        static int CountOutcome(CleanReport report, AreaOutcome outcome)
        {
            int n = 0;
            foreach (AreaResult r in report.Results) if (r.Outcome == outcome) n++;
            return n;
        }

        /// <summary>True when the clean ran every area it tried in full (skips are fine): the line gets a check, else a
        /// warning. An area that only partly worked (some drives not flushed), and a clean stopped by its time limit,
        /// get the warning too. A busy skip ran nothing and failed nothing: it gets the dash for nothing freed.</summary>
        public static bool CleanWentWell(CleanReport report)
        {
            return report != null && report.Before != null && report.After != null && report.AllOk
                && (report.Completed || report.Stopped == CleanStop.Busy);
        }

        /// <summary>"18.2 GB of 24.0 GB", or "unknown" when Windows did not report the commit limit.</summary>
        public static string CommitText(MemorySnapshot s)
        {
            if (s == null || s.CommitLimit == 0) return "unknown";
            return Format.Bytes(s.CommitTotal) + " of " + Format.Bytes(s.CommitLimit);
        }

        public static string CommitName(MemorySnapshot s)
        {
            if (s == null || s.CommitLimit == 0) return "Committed: unknown";
            return "Committed " + CommitText(s) + ", " + s.CommitPercent.ToString(CultureInfo.InvariantCulture) + " percent of the limit";
        }

        // ------------------------------------------------------------------ commit warning (U15)

        /// <summary>The warning turns on at 90% of the commit limit and off below 87%, so 2-second samples near the
        /// edge do not flicker it.</summary>
        public const int CommitWarnPercent = 90, CommitClearPercent = 87;
        public const string CommitWarning = "Close to the limit: apps may fail to start or to save work. "
            + "Cleaning does not lower this; closing apps does.";

        /// <summary>Normal to Warning at 90% or more; Warning to Normal below 87%; never with an unknown limit. Pure.</summary>
        public static bool CommitWarnOn(bool wasOn, MemorySnapshot s)
        {
            if (s == null || s.CommitLimit == 0) return false;
            return s.CommitPercent >= (wasOn ? CommitClearPercent : CommitWarnPercent);
        }

        /// <summary>The commit row's accessible name, with ". Close to the limit" while the warning shows.</summary>
        public static string CommitName(MemorySnapshot s, bool warn)
        {
            string name = CommitName(s);
            return warn && s != null && s.CommitLimit != 0 ? name + ". Close to the limit" : name;
        }

        // ------------------------------------------------------------------ top apps (R5, U17)

        public const string TopAppsNote = "Ranked by private memory. Cleaning does not shrink it; closing the app does.";
        const int MaxDisplayName = 64;

        /// <summary>
        /// A process name as shown: whoever names an exe controls it, so control, format (bidi, zero-width, tag) and
        /// line or paragraph separator characters and unpaired surrogates are dropped, code point by code point; the
        /// rest is trimmed and cut to 64 characters with "...", and an empty result reads "(no name)". A surrogate
        /// pair is never split. The terminal's top uses the same SafeText.Name. Pure.
        /// </summary>
        public static string DisplayName(string raw)
        {
            return SafeText.Name(raw, MaxDisplayName, "...");
        }

        /// <summary>The top-apps row as one phrase: "msedge: 2.9 GB private, 1.6 GB in RAM, 24 processes".</summary>
        public static string TopAppName(AppMemory a)
        {
            string text = DisplayName(a.Name) + ": " + Format.Bytes(a.PrivateBytes) + " private, " + Format.Bytes(a.Bytes) + " in RAM";
            return a.Processes > 1 ? text + ", " + a.Processes.ToString(CultureInfo.InvariantCulture) + " processes" : text;
        }

        /// <summary>The row's tooltip, one line each: the full name, then what each figure means and what a clean does to it.</summary>
        public static List<string> TopAppTip(AppMemory a)
        {
            string name = DisplayName(a.Name);
            if (a.Processes > 1) name += ", " + a.Processes.ToString(CultureInfo.InvariantCulture) + " processes";
            return new List<string>
            {
                name,
                "Private: " + Format.Bytes(a.PrivateBytes) + ". Memory only this app can use. A clean does not change it.",
                "In RAM: " + Format.Bytes(a.Bytes) + ". The part held in physical memory now. A clean lowers it for a while, then it grows back."
            };
        }

        // ------------------------------------------------------------------ shortcuts and locked controls (U16, U21)

        /// <summary>"Memory (Ctrl+1)": a control's label with its keys, for tooltips.</summary>
        public static string ShortcutHint(string label, string keys)
        {
            return label + " (" + keys + ")";
        }

        public const string MustStaySelected = "Must stay selected";

        /// <summary>"At least one area must stay selected, so Process working sets stays on." Uses the short visible labels.</summary>
        public static string LastAreaReasonFor(Areas area)
        {
            return "At least one area must stay selected, so " + ShortAreaLabel(area) + " stays on.";
        }

        /// <summary>The labels the Settings list shows (MainWindow.xaml), without AreaLabel's cost notes.</summary>
        static string ShortAreaLabel(Areas area)
        {
            switch (area)
            {
                case Areas.StandbyList: return "Full standby cache";
                case Areas.VolumeCache: return "Drive write cache";
                default: return AreaLabel(area);
            }
        }

        /// <summary>The Clean button's tooltip while it is disabled for a reason the user can act on; null otherwise.</summary>
        public static string CleanTooltip(bool elevated, bool cleaning, MemoryViewState state)
        {
            if (cleaning) return null;
            if (!elevated) return CleanNeedsAdmin;
            if (state == MemoryViewState.FailedNoData) return CleanNeedsData;
            return null;
        }

        /// <summary>A disabled control's tooltip: its reason, only while it is disabled and has one.</summary>
        public static string DisabledTip(bool enabled, string reason)
        {
            return enabled || string.IsNullOrEmpty(reason) ? null : reason;
        }

        // ------------------------------------------------------------------ will a clean help (R1 wiring)

        /// <summary>The advice line's glyph: a warning when memory is short or commit-bound, else an info mark; null hides it.</summary>
        public static string AdviceGlyph(RamOptimiser.CleanAdvice a)
        {
            if (a == RamOptimiser.CleanAdvice.None) return null;
            return AdviceStrong(a) ? "IconWarn" : "IconInfo";
        }

        public static string AdviceStroke(RamOptimiser.CleanAdvice a) { return AdviceStrong(a) ? "Caution" : "MutedText"; }

        /// <summary>Text colour: the warnings read in full Text, the calm states stay muted.</summary>
        public static string AdviceTextBrush(RamOptimiser.CleanAdvice a) { return AdviceStrong(a) ? "Text" : "MutedText"; }

        static bool AdviceStrong(RamOptimiser.CleanAdvice a)
        {
            return a == RamOptimiser.CleanAdvice.WillHelp || a == RamOptimiser.CleanAdvice.CommitBound;
        }

        /// <summary>
        /// The window's advice for one sample: the engine's, except that commit-bound gives way to Windows' own low or
        /// plenty signal. The commit warning (U15) owns that message and is always on at 90% or more, where the engine
        /// says commit-bound, so the screen never says "cleaning does not lower this" twice. Pure.
        /// </summary>
        public static RamOptimiser.CleanAdvice AdviceFor(MemorySnapshot s)
        {
            RamOptimiser.CleanAdvice a = CleanEstimate.Advise(s);
            return a == RamOptimiser.CleanAdvice.CommitBound ? CleanEstimate.ForCondition(s.Condition) : a;
        }

        /// <summary>The advice is about now: shown only while the figures are live, never from a stale or missing read.</summary>
        public static bool AdviceVisible(MemoryViewState state)
        {
            return state == MemoryViewState.Live;
        }

        /// <summary>The error under a startup row that could not be changed: a title and the reason as a sentence.</summary>
        public static string ChangeFailedTitle(string label, bool turningOn)
        {
            return (turningOn ? "Couldn't turn on " : "Couldn't turn off ") + label;
        }

        public static string ChangeFailedText(string reason, bool turningOn)
        {
            return Sentence(Plain(reason, "unknown error")) + ". It is still " + (turningOn ? "off." : "on.");
        }

        /// <summary>The closed list's header says what is inside: "Services to stop: 4 of 26 chosen".</summary>
        public static string ServicesHeader(int chosen, int total)
        {
            if (total <= 0) return "Services to stop";
            return "Services to stop: " + chosen.ToString(CultureInfo.InvariantCulture) + " of "
                + total.ToString(CultureInfo.InvariantCulture) + " chosen";
        }

        /// <summary>The Power Clean row's second line, e.g. "every area, and stops 4 services until restart".</summary>
        public static string PowerDescription(AppSettings s)
        {
            int n = s.PowerServices == null ? 0 : s.PowerServices.Count;
            if (n == 0) return "every area; no services chosen";
            return "every area, plus " + n.ToString(CultureInfo.InvariantCulture)
                + (n == 1 ? " chosen service" : " chosen services") + " if running, until restart";
        }

        /// <summary>
        /// One service row's detail, sentences joined with ". ": why it is safe to stop, then its memory or
        /// state, e.g. "Only the Maps app uses it. Uses 6.2 MB".
        /// </summary>
        public static string ServiceDetail(ServiceRule rule, ServiceState state)
        {
            var parts = new List<string> { rule.Why };
            if (state == null || !state.Running) parts.Add("Not running");
            else if (state.SharedWith > 1) parts.Add("Shares a process with other services, so little is freed");
            else if (state.MemoryBytes >= 0) parts.Add("Uses " + Format.Bytes((ulong)state.MemoryBytes));
            if (rule.Triggered) parts.Add("Windows may start it again by itself");
            return string.Join(". ", parts.ToArray());
        }

        public const string ServicesNeedAdmin = "Stopping and restoring services needs administrator rights.";

        // ------------------------------------------------------------------ Startup view

        /// <summary>The verdict as words, never colour alone. Unknown apps can still be turned off (Task Manager allows it).</summary>
        public static string VerdictText(Verdict v, bool isService)
        {
            switch (v)
            {
                case Verdict.Safe: return "Safe to turn off";
                case Verdict.Caution: return "Turn off with care";
                case Verdict.Keep: return "Keep on";
                default: return isService ? "Not in the app's list: left alone" : "Unknown app: usually safe to turn off";
            }
        }

        /// <summary>As below, with the real Windows folder.</summary>
        public static string AppDetail(StartupApp a)
        {
            return AppDetail(a, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }

        /// <summary>
        /// Sentences joined with ". ": purpose, what turning it off does and which file runs (for a row the built-in
        /// list knows), then who signed it (PublisherLine: a company the file only claims never reads as its
        /// publisher), "Starts for every user", and why a packaged or run-once row has no switch here. Pure.
        /// </summary>
        public static string AppDetail(StartupApp a, string windowsFolder)
        {
            var parts = new List<string>();
            if (a.Known != null)
            {
                parts.Add(a.Known.Purpose);
                // A run-once row has no off to describe.
                if (!string.IsNullOrEmpty(a.Known.Effect) && !StartupApps.IsRunOnce(a.Source)) parts.Add("If off: " + a.Known.Effect);
                // The list matches by file name only, so say which file this row really runs.
                parts.Add("Runs " + (a.ExePath ?? a.Name));
            }
            parts.Add(PublisherLine(a, windowsFolder));
            if (a.NeedsAdmin) parts.Add("Starts for every user");
            string source = StartupSourceDetail(a);
            if (source != null) parts.Add(source);
            return JoinSentences(parts);
        }

        /// <summary>The verdict line of a run-once row: there is no switch, so no list verdict applies.</summary>
        public const string RunOnceVerdictText = "Runs once: nothing to turn off";
        /// <summary>The verdict line of a packaged row a policy decides: the user can't turn it off anywhere.</summary>
        public const string PolicyVerdictText = "Set by policy: nothing to turn off";

        /// <summary>
        /// The verdict words of an app row nobody here can turn off (a run-once row, a policy-set packaged row), or
        /// null for every other row, whose words are VerdictText's. Pure.
        /// </summary>
        public static string AppVerdictWords(StartupApp a)
        {
            if (a == null) return null;
            if (StartupApps.IsRunOnce(a.Source)) return RunOnceVerdictText;
            if (a.Source == StartupSource.Packaged && a.PolicySet) return PolicyVerdictText;
            return null;
        }

        /// <summary>
        /// An app row's verdict: the built-in list's, except for a row AppVerdictWords speaks for, which can't be
        /// turned off and so gets none (Unknown). Pure.
        /// </summary>
        public static Verdict AppVerdict(StartupApp a)
        {
            if (a == null || a.Known == null || AppVerdictWords(a) != null) return Verdict.Unknown;
            return a.Known.Verdict;
        }

        /// <summary>An app row's verdict as words: AppVerdictWords when it speaks, else VerdictText. Pure.</summary>
        public static string AppVerdictText(StartupApp a)
        {
            return AppVerdictWords(a) ?? VerdictText(AppVerdict(a), false);
        }

        /// <summary>
        /// Parts joined as sentences with ". ", except after a part that already ends in a full stop (a company such
        /// as "Discord Inc."), which gets only the space. No final full stop, like AppDetail. Pure.
        /// </summary>
        public static string JoinSentences(IList<string> parts)
        {
            var sb = new StringBuilder();
            foreach (string p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (sb.Length > 0) sb.Append(sb[sb.Length - 1] == '.' ? " " : ". ");
                sb.Append(p);
            }
            return sb.ToString();
        }

        /// <summary>A row's detail as shown: with one final full stop, never two. Pure.</summary>
        public static string DetailLine(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return "";
            return detail.EndsWith(".", StringComparison.Ordinal) ? detail : detail + ".";
        }

        /// <summary>
        /// A locked Startup row's accessible name: LockedRowName, except that a run-once row says when it runs (it
        /// never "starts with Windows"), plus ". Look closer: see who signed it" when the row has the marker, the
        /// way StartupSwitchName does for a row with a switch. Pure.
        /// </summary>
        public static string StartupLockedName(string label, string lockText, bool on, bool runsOnce, string marker)
        {
            string name = runsOnce ? label + ": Runs once at the next sign-in" : LockedRowName(label, lockText, on);
            return name + (marker != null ? ". " + marker + ": see who signed it" : "");
        }

        /// <summary>The marker beside a row's name whose signature does not back it (E26). Words plus a glyph.</summary>
        public const string LookCloserText = "Look closer";

        /// <summary>As below, with the real Windows folder.</summary>
        public static string LookCloser(StartupApp a)
        {
            return LookCloser(a, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }

        /// <summary>
        /// LookCloserText when the row's program is unsigned (outside Windows' own files, which are catalog-signed and
        /// read "not checked"), its signature does not check out or is blocked, or it is signed by a company other than
        /// the one the file names; else null. A host (rundll32, wscript, powershell) that runs another file is marked
        /// whatever its own signature, Windows' own hosts included: the check covers the host, never the file it runs.
        /// A row that was not checked (a packaged app, a user's own shortcut, a missing file) gets no marker: there is
        /// nothing to say about it. Display only: never part of a Keep or switch decision. Pure.
        /// </summary>
        public static string LookCloser(StartupApp a, string windowsFolder)
        {
            if (a == null || a.Signature == SignatureState.NotChecked) return null;
            if (a.RunsFile) return LookCloserText;
            switch (a.Signature)
            {
                case SignatureState.Signed:
                    bool mismatch = !string.IsNullOrEmpty(a.Signer) && !string.IsNullOrEmpty(a.ClaimedCompany)
                        && !Signature.SameCompany(a.Signer, a.ClaimedCompany);
                    return mismatch ? LookCloserText : null;
                case SignatureState.Unsigned:
                    bool windowsFile = a.SystemOwned && StartupApps.IsInside(a.ExePath, windowsFolder);
                    return windowsFile ? null : LookCloserText;
                case SignatureState.Invalid:
                case SignatureState.Distrusted:
                    return LookCloserText;
                default:
                    return null;
            }
        }

        /// <summary>
        /// A startup switch's accessible name: "Steam, starts with Windows", plus ". Turned off by this app" and
        /// ". Look closer: see who signed it" when they apply, so the marker is never visual only. Pure.
        /// </summary>
        public static string StartupSwitchName(string label, bool byApp, string marker)
        {
            return label + ", starts with Windows" + (byApp ? ". Turned off by this app" : "")
                + (marker != null ? ". " + marker + ": see who signed it" : "");
        }

        /// <summary>
        /// The words in place of an app row's switch, or null for a row with one. First match wins: a row with no
        /// reversible switch ("In Settings", "Runs once"), a program the list says to keep ("Left on"), an every-user
        /// entry without admin rights ("Needs admin"). Pure.
        /// </summary>
        public static string AppLock(StartupApp a, bool elevated)
        {
            if (a == null) return null;
            string source = StartupSourceLock(a);
            if (source != null) return source;
            if (a.Known != null && a.Known.Verdict == Verdict.Keep) return "Left on";
            if (a.NeedsAdmin && !elevated) return "Needs admin";
            return null;
        }

        /// <summary>The glyph beside AppLock's words: a shield for Left on, a lock for Needs admin, none for the source locks.</summary>
        public static string AppLockGlyph(StartupApp a, bool elevated)
        {
            if (a == null || StartupSourceLock(a) != null) return null;
            if (a.Known != null && a.Known.Verdict == Verdict.Keep) return "IconShield";
            if (a.NeedsAdmin && !elevated) return "IconLock";
            return null;
        }

        /// <summary>
        /// The app half of the Startup summary: apps on (CountsAsAppOn: packaged rows count, run-once rows never), and
        /// of those, the ones safe to turn off that have a switch here (a packaged app is turned off in Settings). Pure.
        /// </summary>
        public static void CountStartupApps(IList<StartupApp> apps, out int on, out int safeOn)
        {
            on = 0; safeOn = 0;
            if (apps == null) return;
            foreach (StartupApp a in apps)
            {
                if (!CountsAsAppOn(a)) continue;
                on++;
                if (a.CanToggle && a.Known != null && a.Known.Verdict == Verdict.Safe) safeOn++;
            }
        }

        /// <summary>A row's key for focus, errors and the change flow: a Run value and a packaged task may share a name. Pure.</summary>
        public static string StartupAppKey(StartupApp a)
        {
            return "app|" + a.Source + "|" + a.Name;
        }

        /// <summary>
        /// The "Open Startup settings" button shows on the apps list while it has a packaged row Settings can change
        /// (a policy-set one can't be changed there). Pure.
        /// </summary>
        public static bool ShowsStartupSettings(bool appsView, IList<StartupApp> apps)
        {
            if (!appsView || apps == null) return false;
            foreach (StartupApp a in apps)
                if (a != null && a.Source == StartupSource.Packaged && !a.PolicySet) return true;
            return false;
        }

        /// <summary>
        /// What shows under a service row after a change: an error with today's words when nothing changed
        /// (StartupChangeReason into ChangeFailedText), a note titled with the row's name when the change worked only
        /// in part (StartupChangeNote), and nothing when it went as asked. Pure.
        /// </summary>
        public static RowNote ServiceChangeNote(StartupChange r, string label, bool on)
        {
            if (r == null) return null;
            if (!r.Ok)
                return new RowNote { IsError = true, Title = ChangeFailedTitle(label, on),
                    Text = ChangeFailedText(StartupChangeReason(r.Kind, r.Detail), on) };
            string note = StartupChangeNote(r.Kind);
            return note == null ? null : new RowNote { Title = label, Text = note };
        }

        /// <summary>The error under an app row whose switch could not be changed. Pure.</summary>
        public static RowNote AppChangeError(string label, bool on, string failure)
        {
            return new RowNote { IsError = true, Title = ChangeFailedTitle(label, on), Text = ChangeFailedText(failure, on) };
        }

        public static string ServiceDetail(StartupService s)
        {
            var parts = new List<string>();
            if (s.Known != null)
            {
                parts.Add(s.Known.Purpose);
                if (!string.IsNullOrEmpty(s.Known.Effect)) parts.Add("If off: " + s.Known.Effect);
            }
            parts.Add(StartLabel(s));
            // E12: once, in the row detail, when something else changed a service this app turned off.
            string journal = JournalNote(s.Journal);
            if (journal != null) parts.Add(journal);
            if (s.Protected) parts.Add("Windows protects this service, so its startup can't be changed here");
            if (s.Running && s.SharedWith <= 1 && s.MemoryBytes >= 0) parts.Add("Uses " + Format.Bytes((ulong)s.MemoryBytes));
            return string.Join(". ", parts.ToArray());
        }

        static string StartLabel(StartupService s)
        {
            if (s.DisabledByApp) return "Turned off by this app; starts only when something asks for it";
            switch (s.Mode)
            {
                case StartMode.Automatic: return "Starts with Windows";
                case StartMode.AutomaticDelayed: return "Starts shortly after Windows";
                case StartMode.Manual: return "Starts only when something asks for it";
                case StartMode.Disabled: return "Disabled";
                default: return "Start type unknown";
            }
        }

        /// <summary>The line under the title, e.g. "4 apps and 24 services start with Windows. 9 are safe to turn off".</summary>
        public static string StartupSummary(int appsOn, int servicesOn, int safeOn)
        {
            string head = Count(appsOn, "app") + " and " + Count(servicesOn, "listed service") + " start with Windows";
            return safeOn == 0 ? head + "." : head + ". " + Count(safeOn, "is", "are", true) + " safe to turn off.";
        }

        static string Count(int n, string noun)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
        }

        static string Count(int n, string one, string many, bool bare)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        }

        public const string StartupAppsNote = "Turning an app off works like Task Manager: it stays installed and can be turned back on here.";
        public const string StartupServicesNote = "Turning a service off sets it to start only when needed and stops it now. "
            + "Only services in the app's list can be changed, and only ones this app turned off can be turned back on.";

        /// <summary>Status under the Restore button.</summary>
        public static string RestoreStatus(int stoppedByApp)
        {
            if (stoppedByApp == 0) return "Stopped services come back when Windows restarts.";
            return stoppedByApp.ToString(CultureInfo.InvariantCulture)
                + (stoppedByApp == 1 ? " service is" : " services are") + " stopped by this app. They come back when you quit the app or restart Windows.";
        }

        /// <summary>
        /// The result line after start-up started again the services an earlier run's Power Clean left stopped (it
        /// crashed, was ended, or Windows signed out first). Null when there is nothing to say: none were left, or
        /// every one is no longer installed (a per-user service from an earlier sign-in).
        /// </summary>
        public static string RecoveryLine(IList<ServiceAction> done, string failure)
        {
            const string Retry = " Try Restore services in Settings, or restart Windows.";
            if (failure != null) return "Services an earlier Power Clean stopped could not be started again: " + Plain(failure, "unknown error") + "." + Retry;
            int started = 0, failed = 0;
            if (done != null)
                foreach (ServiceAction a in done)
                {
                    if (a.Started) started++;
                    else if (a.Failed) failed++;
                }
            int total = started + failed;
            if (total == 0) return null;
            string head = "Power Clean left " + Count(total, "service") + " stopped when the app last closed.";
            if (failed == 0) return head + (total == 1 ? " It is running again." : " They are running again.");
            if (started == 0) return head + (total == 1 ? " It" : " They") + " could not be started again." + Retry;
            return head + " " + started.ToString(CultureInfo.InvariantCulture) + (started == 1 ? " is" : " are") + " running again; "
                + failed.ToString(CultureInfo.InvariantCulture) + " could not be started." + Retry;
        }

        /// <summary>The check: nothing failed.</summary>
        public static bool RecoveryWentWell(IList<ServiceAction> done, string failure)
        {
            if (failure != null) return false;
            if (done != null) foreach (ServiceAction a in done) if (a.Failed) return false;
            return true;
        }

        /// <summary>One row per service started or failed, for the details under the result line.</summary>
        public static List<AreaLine> RecoveryLines(IList<ServiceAction> done)
        {
            var lines = new List<AreaLine>();
            if (done == null) return lines;
            foreach (ServiceAction a in done)
            {
                if (!a.Started && !a.Failed) continue;
                lines.Add(new AreaLine
                {
                    Area = Areas.Services,
                    Label = a.Label,
                    State = a.Started ? AreaState.Ok : AreaState.Failed,
                    Status = a.Started ? "ok" : "failed: " + Plain(a.Message, "unknown error")
                });
            }
            return lines;
        }

        public static string CooldownText(int seconds)
        {
            if (seconds <= 0) return "no minimum wait between cleans";
            if (seconds % 60 != 0) return "at most every " + seconds.ToString(CultureInfo.InvariantCulture) + " seconds";
            int minutes = seconds / 60;
            return minutes == 1 ? "at most once a minute" : "at most every " + minutes.ToString(CultureInfo.InvariantCulture) + " minutes";
        }

        // ------------------------------------------------------------------ auto-clean state (P6, R2+U6, P5)

        public const string PauseMenuText = "Pause auto-clean for 1 hour";
        public const string ResumeMenuText = "Resume auto-clean";
        /// <summary>The Settings row's button, beside "Pause automatic cleans"; PauseRowButtonName gives its accessible name.</summary>
        public const string PauseButtonText = "Pause for 1 hour";
        public const string ResumeButtonText = "Resume";
        public const string ManualStillWorks = "Manual cleans still work.";
        public const string DismissBackoffName = "Dismiss the auto-clean wait message";
        const string ClosingAdvice = "Closing an app frees more memory than cleaning.";

        /// <summary>st may be null (not read yet): then not paused and not backing off.</summary>
        public static AutoCleanState AutoState(bool enabled, bool elevated, AutoCleanStatus st)
        {
            if (!enabled) return AutoCleanState.Off;
            if (!elevated) return AutoCleanState.NeedsAdmin;
            if (st != null && st.Paused) return AutoCleanState.Paused;
            if (st != null && st.FutileStreak > 0) return AutoCleanState.BackingOff;
            return AutoCleanState.Armed;
        }

        /// <summary>Pause is offered only where it would change something: auto-clean on, elevated and not paused.</summary>
        public static bool CanPause(AutoCleanState state)
        {
            return state == AutoCleanState.Armed || state == AutoCleanState.BackingOff;
        }

        /// <summary>"Paused until 15:30", or null when not paused. A pause lasts at most an hour, so no date.</summary>
        public static string PausedLine(AutoCleanStatus st, CultureInfo culture)
        {
            if (st == null || !st.Paused || !st.PausedUntilUtc.HasValue) return null;
            return "Paused until " + st.PausedUntilUtc.Value.ToLocalTime().ToString("t", culture);
        }

        /// <summary>The tray's Auto-clean item: the tick is the setting, the words say what holds it.</summary>
        public static string TrayAutoLabel(AutoCleanState state, AutoCleanStatus st, CultureInfo culture)
        {
            if (state == AutoCleanState.NeedsAdmin) return "Auto-clean (paused: needs admin)";
            string paused = state == AutoCleanState.Paused ? PausedLine(st, culture) : null;
            return paused == null ? "Auto-clean" : "Auto-clean (" + char.ToLowerInvariant(paused[0]) + paused.Substring(1) + ")";
        }

        /// <summary>The Settings pause row's button label.</summary>
        public static string PauseRowButtonText(bool paused) { return paused ? ResumeButtonText : PauseButtonText; }

        /// <summary>
        /// Its accessible name, which starts with the visible label (WCAG 2.5.3), so "click Pause for 1 hour" works
        /// for speech users; the row's own words ("Pause automatic cleans") go in the help text.
        /// </summary>
        public static string PauseRowButtonName(bool paused) { return paused ? ResumeMenuText : PauseButtonText; }

        /// <summary>
        /// The Memory tab bar for one refresh. A dismissal lasts while the same notice stands: once the bar has
        /// nothing to say, or says something else, it is forgotten, so the next back-off episode shows even at the
        /// same streak. The sample-error bar takes the slot without forgetting anything. A kind (paused, backoff) is
        /// read out once when it starts showing, not on every streak step. Pure.
        /// </summary>
        /// <summary>Everything a notice shows, so a refresh that would draw the same thing can be skipped. Pure.</summary>
        public static string RenderKey(AutoNotice n)
        {
            if (n == null) return "";
            return n.Key + "|" + n.Title + "|" + n.Detail + "|" + n.Glyph + "|" + n.Stroke + "|" + n.Resume + "|" + n.Dismiss;
        }

        public static AutoBarStep NextAutoBar(AutoNotice n, bool errorShown, string dismissedKey, string announcedKind)
        {
            string dismissed = n != null && n.Key == dismissedKey ? dismissedKey : null;
            bool show = n != null && !errorShown && dismissed == null;
            string kind = n == null ? null : n.Key.Split(':')[0];
            return new AutoBarStep
            {
                Show = show,
                Announce = show && kind != announcedKind,
                DismissedKey = dismissed,
                AnnouncedKind = show ? kind : n == null ? null : announcedKind
            };
        }

        /// <summary>The small line of the Settings pause row.</summary>
        public static string PauseNote(AutoCleanStatus st, CultureInfo culture)
        {
            string paused = PausedLine(st, culture);
            return paused == null ? ManualStillWorks : paused + ". " + ManualStillWorks;
        }

        /// <summary>"Waiting longer: the last 2 cleans left load at 85% or more." Null for a streak of 0.</summary>
        public static string BackoffLine(int streak, int thresholdPercent)
        {
            if (streak <= 0) return null;
            string cleans = streak == 1 ? "the last clean" : "the last " + streak.ToString(CultureInfo.InvariantCulture) + " cleans";
            return "Waiting longer: " + cleans + " left load at " + thresholdPercent.ToString(CultureInfo.InvariantCulture) + "% or more.";
        }

        /// <summary>When the next clean for high load can run, if still ahead, then the honest advice.</summary>
        public static string BackoffAdvice(DateTime? nextUtc, DateTime nowUtc, CultureInfo culture)
        {
            if (!nextUtc.HasValue || nextUtc.Value <= nowUtc) return ClosingAdvice;
            return "Next clean for high load after " + nextUtc.Value.ToLocalTime().ToString("t", culture) + ". " + ClosingAdvice;
        }

        /// <summary>The Memory tab's bar while backing off. Interval cleans still run, hence "for high load".</summary>
        public static string BackoffTitle(TimeSpan wait)
        {
            return "Auto-clean now waits " + DurationPhrase(wait) + " between cleans for high load, because recent cleans did not help.";
        }

        /// <summary>The Settings line under Clean automatically; streak 0 gives the plain CooldownText.</summary>
        public static string CooldownText(int seconds, int streak)
        {
            string plain = CooldownText(seconds);
            if (streak <= 0 || seconds <= 0) return plain;
            TimeSpan wait = AutoCleanPolicy.ThresholdCooldown(TimeSpan.FromSeconds(seconds), streak);
            return plain + "; now every " + DurationPhrase(wait) + " because recent cleans did not help";
        }

        /// <summary>"1 minute", "20 minutes", "1 hour", "1 hour 20 minutes"; "90 seconds" when not whole minutes.</summary>
        public static string DurationPhrase(TimeSpan t)
        {
            long seconds = (long)Math.Round(Math.Max(0, t.TotalSeconds));
            if (seconds % 60 != 0) return Count(seconds, "second");
            long minutes = seconds / 60, hours = minutes / 60, rest = minutes % 60;
            if (hours == 0) return Count(minutes, "minute");
            return rest == 0 ? Count(hours, "hour") : Count(hours, "hour") + " " + Count(rest, "minute");
        }

        static string Count(long n, string unit)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + unit + (n == 1 ? "" : "s");
        }

        /// <summary>
        /// The bar at the top of the Memory tab, so a pause or a back-off is seen without scrolling to the auto-clean
        /// card. Null when there is nothing to say (armed, off, or needing admin, which has its own line).
        /// </summary>
        public static AutoNotice AutoBar(AutoCleanState state, AutoCleanStatus st, AppSettings s, CultureInfo culture)
        {
            return AutoBar(state, st, s, culture, PauseOrigin.Unknown);
        }

        /// <summary>"Paused from the tray for 1 hour." for the bar's second line; null when the origin is not known.</summary>
        public static string PauseOriginLine(PauseOrigin origin)
        {
            string length = DurationPhrase(AutoCleanService.PauseLength);
            switch (origin)
            {
                case PauseOrigin.Tray: return "Paused from the tray for " + length + ".";
                case PauseOrigin.Settings: return "Paused from Settings for " + length + ".";
                case PauseOrigin.Restored: return "Paused before the app restarted.";
                default: return null;
            }
        }

        /// <summary>The origin lasts as long as the pause it describes: once the service is not paused, it is forgotten.</summary>
        public static PauseOrigin KeepPauseOrigin(PauseOrigin origin, AutoCleanStatus st)
        {
            return st != null && st.Paused ? origin : PauseOrigin.Unknown;
        }

        /// <summary>As above; a pause's second line also says where it came from, when that is known.</summary>
        public static AutoNotice AutoBar(AutoCleanState state, AutoCleanStatus st, AppSettings s, CultureInfo culture, PauseOrigin origin)
        {
            if (s == null) throw new ArgumentNullException("s");
            if (state == AutoCleanState.Paused)
            {
                string paused = PausedLine(st, culture);
                if (paused == null) return null;
                string from = PauseOriginLine(origin);
                return new AutoNotice
                {
                    Title = "Auto-clean is p" + paused.Substring(1) + ".",
                    Detail = from == null ? ManualStillWorks : from + " " + ManualStillWorks,
                    Glyph = "IconPause", Stroke = "Accent", Resume = true, Key = "paused"
                };
            }
            if (state == AutoCleanState.BackingOff && st != null)
            {
                return new AutoNotice
                {
                    Title = BackoffTitle(AutoCleanService.ThresholdCooldown(s, st.FutileStreak)),
                    Glyph = "IconWarn", Stroke = "Caution", Dismiss = true,
                    Key = "backoff:" + st.FutileStreak.ToString(CultureInfo.InvariantCulture)
                };
            }
            return null;
        }

        /// <summary>
        /// The line under the auto-clean card's description: the admin reason, the pause with Resume, why the system
        /// holds cleans (P5), or the back-off in two lines. Null when auto-clean is off or simply armed.
        /// </summary>
        public static AutoNotice AutoCard(AutoCleanState state, AutoCleanStatus st, AppSettings s, DateTime nowUtc, CultureInfo culture)
        {
            if (s == null) throw new ArgumentNullException("s");
            switch (state)
            {
                case AutoCleanState.Off:
                    return null;
                case AutoCleanState.NeedsAdmin:
                    return new AutoNotice { Title = "Paused: needs administrator rights", Key = "admin" };
                case AutoCleanState.Paused:
                    string paused = PausedLine(st, culture);
                    if (paused == null) return null;
                    return new AutoNotice { Title = paused + ".", Glyph = "IconPause", Stroke = "MutedText", Resume = true, Key = "paused" };
            }
            string hold = st == null ? null : AutoHoldText(st.Hold, s);
            if (hold != null) return new AutoNotice { Title = hold, Key = "hold:" + st.Hold };
            if (state == AutoCleanState.BackingOff && st != null)
            {
                return new AutoNotice
                {
                    Title = BackoffLine(st.FutileStreak, s.ThresholdPercent),
                    Detail = BackoffAdvice(st.NextThresholdCleanUtc, nowUtc, culture),
                    Glyph = "IconWarn", Stroke = "Caution",
                    Key = "backoff:" + st.FutileStreak.ToString(CultureInfo.InvariantCulture)
                };
            }
            return null;
        }

        /// <summary>"every 30 minutes", "every hour", "every 2 hours".</summary>
        public static string IntervalPhrase(int minutes)
        {
            if (minutes <= 0) return "never";
            if (minutes == 60) return "every hour";
            if (minutes % 60 == 0) return "every " + (minutes / 60).ToString(CultureInfo.InvariantCulture) + " hours";
            if (minutes == 1) return "every minute";
            return "every " + minutes.ToString(CultureInfo.InvariantCulture) + " minutes";
        }

        public static string IntervalLabel(int minutes)
        {
            if (minutes <= 0) return "Off";
            string p = IntervalPhrase(minutes);
            return char.ToUpperInvariant(p[0]) + p.Substring(1);
        }

        static readonly int[] StandardIntervals = { 0, 15, 30, 60, 120 };

        /// <summary>The interval choices; a stored value outside the list is kept as an extra choice.</summary>
        public static int[] IntervalChoices(int current)
        {
            var list = new List<int>(StandardIntervals);
            if (!list.Contains(current) && AppSettings.InRange(current, AppSettings.MinIntervalMinutes, AppSettings.MaxIntervalMinutes))
            {
                list.Add(current);
                list.Sort();
            }
            return list.ToArray();
        }

        public static string LastAutoClean(DateTime localTime, CleanReport report, CultureInfo culture)
        {
            return LastAutoClean(localTime, localTime, report, culture);
        }

        /// <summary>
        /// The tray's status item for an automatic clean, read at localNow: "Last auto-clean at 14:32: freed 1.2 GB",
        /// "Last auto-clean yesterday at 14:32: ..." (When), since the tray can sit unopened for days.
        /// </summary>
        public static string LastAutoClean(DateTime localTime, DateTime localNow, CleanReport report, CultureInfo culture)
        {
            string at = "Last auto-clean " + When(localTime, localNow, culture);
            if (report == null) return at + " did not finish";
            int failed = CountOutcome(report, AreaOutcome.Failed), partial = CountOutcome(report, AreaOutcome.Partial);
            string text = at + ": " + (report.InUseFreed > 0 ? "freed " + Format.Bytes(report.InUseFreed) : "nothing freed");
            if (failed > 0) text += ", " + failed.ToString(CultureInfo.InvariantCulture) + (failed == 1 ? " area failed" : " areas failed");
            if (partial > 0) text += ", " + partial.ToString(CultureInfo.InvariantCulture) + (partial == 1 ? " area partly failed" : " areas partly failed");
            return text;
        }
    }

    public enum VerdictFilter { All, Safe, Caution, Keep, Unknown }

    public enum StartupSort { Verdict, Name }

    /// <summary>The Startup tab's search, verdict filter and sort, kept apart from WPF so they are tested.</summary>
    public static class StartupFilter
    {
        public static readonly VerdictFilter[] Filters = { VerdictFilter.All, VerdictFilter.Safe, VerdictFilter.Caution, VerdictFilter.Keep, VerdictFilter.Unknown };

        public static bool Matches(VerdictFilter filter, Verdict v)
        {
            switch (filter)
            {
                case VerdictFilter.Safe: return v == Verdict.Safe;
                case VerdictFilter.Caution: return v == Verdict.Caution;
                case VerdictFilter.Keep: return v == Verdict.Keep;
                case VerdictFilter.Unknown: return v == Verdict.Unknown;
                default: return true;
            }
        }

        /// <summary>Esc in the search box clears it only when there is text; on an empty box the key is left alone.</summary>
        public static bool EscapeClears(string text)
        {
            return !string.IsNullOrEmpty(text);
        }

        /// <summary>Case-insensitive search over the shown name and the real name; blank matches everything.</summary>
        public static bool Matches(string search, string label, string name)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            string s = search.Trim();
            return (label != null && label.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                || (name != null && name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string Label(VerdictFilter filter, int count)
        {
            string name;
            switch (filter)
            {
                case VerdictFilter.Safe: name = "Safe to turn off"; break;
                case VerdictFilter.Caution: name = "Turn off with care"; break;
                case VerdictFilter.Keep: name = "Keep on"; break;
                case VerdictFilter.Unknown: name = "Not in the app's list"; break;
                default: name = "All verdicts"; break;
            }
            return name + " (" + count.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Verdict order puts what can be turned off first: safe, care, unknown, keep; then by name.</summary>
        public static int Rank(Verdict v)
        {
            switch (v)
            {
                case Verdict.Safe: return 0;
                case Verdict.Caution: return 1;
                case Verdict.Unknown: return 2;
                default: return 3;
            }
        }

        public static int Compare(StartupSort sort, Verdict a, string labelA, Verdict b, string labelB)
        {
            if (sort == StartupSort.Verdict)
            {
                int r = Rank(a).CompareTo(Rank(b));
                if (r != 0) return r;
            }
            return string.Compare(labelA, labelB, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    /// <summary>The threshold text box: a whole number in the AppSettings range, nothing else.</summary>
    public static class ThresholdInput
    {
        public static bool TryParse(string text, out int value)
        {
            value = 0;
            if (text == null) return false;
            string t = text.Trim();
            if (t.Length == 0 || t.Length > 3) return false;
            foreach (char c in t) if (c < '0' || c > '9') return false;
            value = int.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
            return AppSettings.InRange(value, AppSettings.MinThresholdPercent, AppSettings.MaxThresholdPercent);
        }
    }

    public static class AreaRules
    {
        /// <summary>False when area is the only one selected: the list may never become empty.</summary>
        public static bool CanUntick(Areas selected, Areas area)
        {
            return (selected & area) == 0 || (selected & ~area & Areas.All) != Areas.None;
        }

        public static Areas Toggle(Areas selected, Areas area, bool on)
        {
            if (on) return (selected | area) & Areas.All;
            return CanUntick(selected, area) ? selected & ~area & Areas.All : selected;
        }

        /// <summary>
        /// U21: what ticking (on) or unticking one area box does. Unticking the last selected area is refused: the box
        /// is ticked again, nothing is saved, and the reason is shown and read out. Otherwise the new selection is
        /// saved when it changed. Pure.
        /// </summary>
        public static AreaToggle Decide(Areas selected, Areas area, bool on)
        {
            if (!on && !CanUntick(selected, area))
                return new AreaToggle { Selected = selected, Refused = true, Reason = AppText.LastAreaReasonFor(area) };
            Areas next = Toggle(selected, area, on);
            return new AreaToggle { Selected = next, Save = next != selected };
        }
    }

    /// <summary>One area toggle's outcome (AreaRules.Decide).</summary>
    public sealed class AreaToggle
    {
        /// <summary>The areas to keep: the new selection, or the old one when refused.</summary>
        public Areas Selected;
        /// <summary>The selection changed: save and apply it.</summary>
        public bool Save;
        /// <summary>Unticking the last area: tick the box again, save nothing, show Reason.</summary>
        public bool Refused;
        public string Reason;
    }

    public static class ThemeResolver
    {
        /// <summary>
        /// appsUseLightTheme / systemUsesLightTheme are the HKCU Personalize DWORDs (null when missing).
        /// High contrast wins over everything; then the user's choice; System follows the app setting,
        /// which Windows treats as light when the value is missing.
        /// </summary>
        public static ResolvedTheme Resolve(ThemeMode mode, int? appsUseLightTheme, bool highContrast)
        {
            if (highContrast) return ResolvedTheme.HighContrast;
            if (mode == ThemeMode.Light) return ResolvedTheme.Light;
            if (mode == ThemeMode.Dark) return ResolvedTheme.Dark;
            return appsUseLightTheme.HasValue && appsUseLightTheme.Value == 0 ? ResolvedTheme.Dark : ResolvedTheme.Light;
        }

        /// <summary>A missing value means the Windows 10 default: a dark taskbar.</summary>
        public static bool TaskbarIsLight(int? systemUsesLightTheme)
        {
            return systemUsesLightTheme.HasValue && systemUsesLightTheme.Value != 0;
        }

        /// <summary>Tray digits: #1A1A1A on a light taskbar, #FFFFFF on a dark one (design.md), as 0xAARRGGBB.</summary>
        public static int TrayTextArgb(bool taskbarIsLight)
        {
            return taskbarIsLight ? unchecked((int)0xFF1A1A1A) : unchecked((int)0xFFFFFFFF);
        }

        /// <summary>
        /// Broadcasts that can change the app's colours: the light/dark setting and taskbar colour (General,
        /// "ImmersiveColorSet"), system colours, visual style and high contrast. Wallpaper, power, locale and the
        /// rest change nothing here, so the token dictionary is not swapped for them.
        /// </summary>
        public static bool AffectsTheme(Microsoft.Win32.UserPreferenceCategory category)
        {
            switch (category)
            {
                case Microsoft.Win32.UserPreferenceCategory.General:
                case Microsoft.Win32.UserPreferenceCategory.Color:
                case Microsoft.Win32.UserPreferenceCategory.VisualStyle:
                case Microsoft.Win32.UserPreferenceCategory.Accessibility:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Under the Theme box while high contrast overrides it; null otherwise.</summary>
        public static string ThemeNote(bool highContrast)
        {
            return highContrast ? "High contrast is on, so Windows' own colours are used." : null;
        }

        public static string ThemeLabel(ThemeMode mode)
        {
            switch (mode)
            {
                case ThemeMode.Light: return "Light";
                case ThemeMode.Dark: return "Dark";
                default: return "Match Windows";
            }
        }
    }

    public sealed class LaunchOptions
    {
        public bool StartInTray;
        /// <summary>The SID of the account that restarted the app as administrator, when it was well formed.</summary>
        public string LauncherSid;
        /// <summary>The pid of the copy that asked for elevation (P10), or 0. Only selects a process to wait for.</summary>
        public int HandoffFrom;

        /// <summary>
        /// "--tray" starts hidden in the notification area. "--launched-by SID" names the account that asked
        /// for elevation; a value that is not a strict SID is dropped (and not consumed). "--handoff=PID" (one
        /// token, plain digits, above 0; the first valid one counts) names the copy to wait for. Anything else is
        /// ignored.
        /// </summary>
        public static LaunchOptions Parse(string[] args)
        {
            var o = new LaunchOptions();
            if (args == null) return o;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == null) continue;
                if (string.Equals(a.Trim(), StartupTask.TrayArgument, StringComparison.OrdinalIgnoreCase))
                    o.StartInTray = true;
                else if (a == Elevation.LauncherArgument && i + 1 < args.Length && Elevation.IsWellFormedSid(args[i + 1]))
                    o.LauncherSid = args[++i];
                else if (o.HandoffFrom == 0)
                    o.HandoffFrom = ParseHandoff(a);
            }
            return o;
        }

        static int ParseHandoff(string a)
        {
            if (!a.StartsWith(Elevation.HandoffArgument, StringComparison.OrdinalIgnoreCase)) return 0;
            int pid;
            if (!int.TryParse(a.Substring(Elevation.HandoffArgument.Length), NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return 0;
            return pid > 0 ? pid : 0;
        }
    }

    /// <summary>Appends unhandled errors to %LOCALAPPDATA%\RamOptimiser\error.log, keeping it under ~1 MB.</summary>
    public static class ErrorLog
    {
        public const long MaxBytes = 1024 * 1024;

        public static string DefaultPath
        {
            get { return Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath), "error.log"); }
        }

        public static string FormatEntry(DateTime utc, string context, Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append(utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            sb.Append(' ').Append(string.IsNullOrEmpty(context) ? "error" : context).Append("\r\n");
            sb.Append(ex == null ? "(no exception)" : ex.ToString()).Append("\r\n\r\n");
            return sb.ToString();
        }

        /// <summary>Never throws: a failing log must not hide the error it was meant to record.</summary>
        public static bool TryWrite(string path, string context, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                SafeUserFolder.Check(Path.GetDirectoryName(path), path, path + ".old");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, FormatEntry(DateTime.UtcNow, context, ex), new UTF8Encoding(false));
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    // ---------------------------------------------------------------------- problem notices (R6)

    public enum NoticeKind { CleanFailed, NotHelping }

    /// <summary>A Windows notification the tray icon shows: plain text with counts and times only, no names or paths.</summary>
    public sealed class Notice
    {
        public NoticeKind Kind;
        public string Title;
        public string Text;
    }

    /// <summary>
    /// When the opt-in notices show (owner decision 2026-11-17: off by default, a Settings switch). Only when something
    /// needs the user: an automatic clean that failed, partly failed or did not finish, or cleans that stopped helping
    /// at the maximum back-off. Never for a manual clean, a clean that went well or a busy skip, and never while the
    /// window can be seen (it already shows the same). NoticeGate keeps each kind to once an hour. Pure.
    /// </summary>
    public static class Notices
    {
        public const string CleanFailedTitle = "Auto-clean had a problem";
        public const string NotHelpingTitle = "Auto-clean is not helping";

        public static Notice ForClean(bool enabled, bool windowShowing, CleanReason reason, CleanReport report, Exception error,
            DateTime localTime, CultureInfo culture)
        {
            if (!enabled || windowShowing || reason == CleanReason.Manual) return null;
            string at = "At " + localTime.ToString("t", culture);
            bool unfinished = error != null || report == null || report.Before == null || report.After == null
                || report.Stopped == CleanStop.TimedOut || report.Stopped == CleanStop.Cancelled;
            if (report != null && report.Stopped == CleanStop.Busy && error == null) return null;
            if (unfinished)
                return new Notice { Kind = NoticeKind.CleanFailed, Title = CleanFailedTitle, Text = at + " the clean did not finish. Open RAM Optimiser for details." };
            int failed = 0, partial = 0;
            foreach (AreaResult a in report.Results)
            {
                if (a.Outcome == AreaOutcome.Failed) failed++;
                else if (a.Outcome == AreaOutcome.Partial) partial++;
            }
            string what = failed > 0 ? AreaCount(failed) + " failed" : partial > 0 ? AreaCount(partial) + " partly failed" : null;
            if (what == null) return null;
            return new Notice { Kind = NoticeKind.CleanFailed, Title = CleanFailedTitle, Text = at + ", " + what + ". Open RAM Optimiser to see which." };
        }

        /// <summary>Only on the step into the maximum back-off; it re-arms once the streak drops below it.</summary>
        public static Notice ForStreak(bool enabled, bool windowShowing, int oldStreak, int newStreak, int thresholdPercent)
        {
            int max = AutoCleanPolicy.MaxBackoffSteps;
            if (!enabled || windowShowing || oldStreak >= max || newStreak < max) return null;
            return new Notice
            {
                Kind = NoticeKind.NotHelping, Title = NotHelpingTitle,
                Text = "The last " + newStreak.ToString(CultureInfo.InvariantCulture) + " cleans left load at "
                    + thresholdPercent.ToString(CultureInfo.InvariantCulture) + "% or more. Closing an app will free more memory than cleaning."
            };
        }

        /// <summary>The Recent activity line and cleans.log event for a notice that was shown.</summary>
        public static ActivityEvent ShownEvent(Notice n)
        {
            return new ActivityEvent { Kind = "notice-shown", Text = "Notification shown: " + (n == null ? "" : n.Title) + "." };
        }

        static string AreaCount(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " area" : " areas");
        }
    }

    /// <summary>At most one notice of a kind an hour; a clock set back allows one. Pure.</summary>
    public static class NoticeGate
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(60);

        public static bool Allow(DateTime? lastShownUtc, DateTime nowUtc)
        {
            if (lastShownUtc == null) return true;
            TimeSpan since = nowUtc - lastShownUtc.Value;
            return since < TimeSpan.Zero || since >= Interval;
        }
    }

    // ---------------------------------------------------------------------- announcements (U9)

    /// <summary>
    /// Whether a status line is read out: only text that is new for that line (ordinal comparison). Hiding the line
    /// resets it (last null), so the same message coming back later is read out again. Pure.
    /// </summary>
    public static class AnnounceGate
    {
        public static bool ShouldAnnounce(string last, string next)
        {
            return !string.IsNullOrEmpty(next) && !string.Equals(last, next, StringComparison.Ordinal);
        }
    }

    public static partial class AppText
    {
        /// <summary>The Startup tab's count while a search or filter is on: "Nothing matches", "1 shown", "12 shown".</summary>
        public static string SearchCount(int shown)
        {
            if (shown <= 0) return "Nothing matches";
            return shown.ToString(CultureInfo.InvariantCulture) + " shown";
        }

        /// <summary>The count shows only while the list is narrowed: search text, or a filter other than the first (All).</summary>
        public static bool SearchCountVisible(string search, int filterIndex)
        {
            return filterIndex > 0 || (search != null && search.Trim().Length > 0);
        }

        // ------------------------------------------------------------------ notices switch (R6) and reset (P16)

        public const string NotifyRowNote = "A Windows notification when an automatic clean fails or stops helping. Never for routine cleans.";
        public const string ResetRowNote = "Puts every setting back to its default and turns auto-clean off. Start with Windows is left as it is.";
        public const string ResetConfirm = "Reset all settings? This cannot be undone.";

        /// <summary>After a reset; services Power Clean stopped this session are not started by it.</summary>
        public static string ResetDone(bool servicesStopped)
        {
            return servicesStopped
                ? "Settings were reset. Services stopped by Power Clean stay stopped until you restore them or quit."
                : "Settings were reset.";
        }

        public static ActivityEvent ResetEvent()
        {
            return new ActivityEvent { Kind = "settings-reset", Text = "Settings were reset to their defaults." };
        }
    }
}
