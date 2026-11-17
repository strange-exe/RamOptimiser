using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace RamOptimiser
{
    public enum Command { Help, Status, Clean, Watch }

    public sealed class Options
    {
        public Command Command = Command.Help;
        public Areas Areas = Areas.Default;
        public int ThresholdPercent = 85;
        public int IntervalSeconds = 30;
        public int CooldownSeconds = 300;
        /// <summary>clean --dry-run: show what a clean would do, change nothing, need no admin.</summary>
        public bool DryRun;
        /// <summary>clean and watch --timeout: each clean stops after this many seconds; 0 means no limit.</summary>
        public int TimeoutSeconds;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            if (args.Length == 0) return o;
            switch (args[0].ToLowerInvariant())
            {
                case "status": o.Command = Command.Status; break;
                case "clean": o.Command = Command.Clean; break;
                case "watch": o.Command = Command.Watch; break;
                case "help": case "-h": case "--help": case "/?": o.Command = Command.Help; return o;
                default: throw new ArgumentException("Unknown command '" + args[0] + "'.");
            }
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                switch (a)
                {
                    case "--areas": o.Areas = AreaNames.Parse(Value(args, ref i)); break;
                    case "--all": o.Areas = Areas.All; break;
                    case "--threshold": o.ThresholdPercent = Int(args, ref i, 1, 100); break;
                    case "--interval": o.IntervalSeconds = Int(args, ref i, 1, 86400); break;
                    case "--cooldown": o.CooldownSeconds = Int(args, ref i, 0, 86400); break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--timeout": o.TimeoutSeconds = Int(args, ref i, 1, 3600); break;
                    default: throw new ArgumentException("Unknown option '" + args[i] + "'.");
                }
            }
            if (o.DryRun && o.Command != Command.Clean)
                throw new ArgumentException("--dry-run works only with 'clean'.");
            if (o.Command == Command.Status && args.Length > 1)
                throw new ArgumentException("'status' takes no options.");
            return o;
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value.");
            return args[++i];
        }

        private static int Int(string[] args, ref int i, int min, int max)
        {
            string name = args[i];
            string v = Value(args, ref i);
            int n;
            if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < min || n > max)
                throw new ArgumentException(name + " must be a whole number from " + min + " to " + max + ".");
            return n;
        }
    }

    /// <summary>Why a watch wait ended.</summary>
    public enum WakeReason { Interval, Stop, LowMemory }

    /// <summary>When watch also listens for Windows' low-memory signal. Pure.</summary>
    public static class WatchSchedule
    {
        /// <summary>
        /// True only when (a) there has been no clean, or the effective cooldown has passed, since inside it a low wake
        /// cannot lead to a clean; and (b) the last wake was not a low wake that did not clean. The signal stays set for
        /// as long as memory is low, so without (b) watch would wake again at once, in a hot loop.
        /// </summary>
        public static bool IncludeLowSignal(TimeSpan? sinceLastClean, TimeSpan effectiveCooldown, WakeReason lastWake, bool cleanedThisTick)
        {
            bool cooled = sinceLastClean == null || sinceLastClean.Value >= effectiveCooldown;
            bool mutedAfterIdleWake = lastWake == WakeReason.LowMemory && !cleanedThisTick;
            return cooled && !mutedAfterIdleWake;
        }
    }

    /// <summary>The watch loop. Its sampler, cleaner, clocks and wait are fields, so tests can drive it.</summary>
    public sealed class Watcher
    {
        /// <summary>
        /// Errors in a row after which watch gives up with ExitWatchFailed: failed iterations, or failed cleans
        /// (counted on their own, as the samples between two cleans can work while every clean fails).
        /// </summary>
        public const int MaxConsecutiveErrors = 5;

        public Func<MemorySnapshot> Sample = MemorySnapshot.Take;
        public Func<Areas, CleanReport> Clean = Cleaner.Clean;
        /// <summary>Monotonic time since watching started: the cooldown must not move with DST or clock changes.</summary>
        public Func<TimeSpan> Elapsed;
        /// <summary>Wall-clock time, for the log lines only.</summary>
        public Func<DateTime> Now = () => DateTime.Now;
        /// <summary>Waits one interval; returns true when watching should stop. Used when Wait is null.</summary>
        public Func<TimeSpan, bool> WaitStop;
        /// <summary>
        /// Waits one interval, and also for Windows' low-memory signal when the flag is true; says why it woke.
        /// Null falls back to WaitStop, which never reports a low wake.
        /// </summary>
        public Func<TimeSpan, bool, WakeReason> Wait;

        WakeReason DoWait(TimeSpan interval, bool includeLow)
        {
            if (Wait != null) return Wait(interval, includeLow);
            return WaitStop(interval) ? WakeReason.Stop : WakeReason.Interval;
        }

        /// <summary>
        /// Samples, cleans when AutoCleanPolicy says so, and waits, until the wait says stop (ExitOk). The wait also
        /// ends early on Windows' low-memory signal when WatchSchedule allows it; the clean decision is unchanged. A
        /// failed sample or clean is logged with a timestamp and watching goes on; MaxConsecutiveErrors failed
        /// iterations, or failed cleans, in a row end it (ExitWatchFailed).
        /// </summary>
        public int Run(Options o, TextWriter output, TextWriter errors)
        {
            TimeSpan? last = null;
            TimeSpan cooldown = TimeSpan.FromSeconds(o.CooldownSeconds);
            TimeSpan interval = TimeSpan.FromSeconds(o.IntervalSeconds);
            int futileStreak = 0, errorsInARow = 0, failedCleans = 0;
            WakeReason wake = WakeReason.Interval;
            while (true)
            {
                bool cleaning = false, cleanedThisTick = false;
                try
                {
                    MemorySnapshot s = Sample();
                    TimeSpan now = Elapsed();
                    // Pressure eased by itself: the next time load climbs, the normal cooldown applies again.
                    if (s.LoadPercent < o.ThresholdPercent) futileStreak = 0;
                    if (AutoCleanPolicy.ShouldClean(s.LoadPercent, o.ThresholdPercent, last, now,
                        AutoCleanPolicy.ThresholdCooldown(cooldown, interval, futileStreak)))
                    {
                        output.WriteLine("[{0:HH:mm:ss}] load {1}% >= {2}%: cleaning", Now(), s.LoadPercent, o.ThresholdPercent);
                        // Recorded before cleaning, so a clean that keeps failing still waits the cooldown.
                        last = now;
                        cleaning = true;
                        CleanReport report = Clean(o.Areas);
                        cleaning = false;
                        if (report.Stopped == CleanStop.Busy)
                        {
                            // Another clean (ramopt or the app) was running. Not an error, and not futile: before
                            // equals after only because nothing ran, so it must not double the wait.
                            output.WriteLine("[{0:HH:mm:ss}] another clean is running: skipped this one", Now());
                        }
                        else
                        {
                            // Only a clean that ran counts for the low signal: a failed one must not be retried at once.
                            cleanedThisTick = true;
                            failedCleans = 0;
                            Program.PrintReport(output, report);
                            if (CleanEstimate.Advise(s) == CleanAdvice.CommitBound)
                                output.WriteLine("  note: committed memory is {0}% of the limit; cleaning does not lower it. Close programs you are not using.",
                                    s.CommitPercent);
                            futileStreak = AutoCleanPolicy.NextStreak(futileStreak, report.After.LoadPercent, o.ThresholdPercent);
                        }
                    }
                    errorsInARow = 0;
                }
                catch (Exception ex)
                {
                    errorsInARow++;
                    if (cleaning) failedCleans++;
                    errors.WriteLine("[{0:HH:mm:ss}] error: {1}", Now(), ex.Message);
                    if (errorsInARow >= MaxConsecutiveErrors)
                    {
                        errors.WriteLine("Stopped after {0} errors in a row.", errorsInARow);
                        return Program.ExitWatchFailed;
                    }
                    if (failedCleans >= MaxConsecutiveErrors)
                    {
                        errors.WriteLine("Stopped after {0} failed cleans in a row.", failedCleans);
                        return Program.ExitWatchFailed;
                    }
                }
                TimeSpan? since = last == null ? (TimeSpan?)null : Elapsed() - last.Value;
                bool includeLow = WatchSchedule.IncludeLowSignal(since,
                    AutoCleanPolicy.ThresholdCooldown(cooldown, interval, futileStreak), wake, cleanedThisTick);
                wake = DoWait(interval, includeLow);
                if (wake == WakeReason.Stop) break;
                if (wake == WakeReason.LowMemory)
                    output.WriteLine("[{0:HH:mm:ss}] Windows reports low memory: checking now", Now());
            }
            output.WriteLine("Stopped.");
            return Program.ExitOk;
        }
    }

    public static class Program
    {
        public const int ExitOk = 0, ExitUsage = 1, ExitPartial = 2, ExitNotElevated = 3, ExitWatchFailed = 4, ExitBusy = 5;

        /// <summary>How long ramopt clean waits for another clean to finish before giving up with ExitBusy.</summary>
        internal static readonly TimeSpan CliLockWait = TimeSpan.FromSeconds(2);

        internal const string BusyMessage = "error: another clean is running (ramopt or RamOptimiser). Try again when it finishes.";

        public static int Main(string[] args)
        {
            Native.RestrictDllSearchToSystem32();
            Options o;
            try { o = Options.Parse(args); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                Console.Error.WriteLine("Run 'ramopt help' for usage.");
                return ExitUsage;
            }

            return Dispatch(o, Console.Out, MemorySnapshot.Take, areas => RunClean(o), RunWatch);
        }

        /// <summary>
        /// Runs a parsed command. The sampler, cleaner and watch are passed in, so tests can prove a dry run never
        /// reaches the cleaner without calling Main (which changes the process's DLL search).
        /// </summary>
        internal static int Dispatch(Options o, TextWriter output, Func<MemorySnapshot> sample, Func<Areas, int> runClean,
            Func<Options, int> runWatch)
        {
            switch (o.Command)
            {
                case Command.Status: PrintStatus(output, sample()); return ExitOk;
                case Command.Clean:
                    // Before the elevation check: a dry run only reads, needs no admin and never reaches the cleaner.
                    if (o.DryRun) { PrintDryRun(output, sample(), o.Areas); return ExitOk; }
                    return runClean(o.Areas);
                case Command.Watch: return runWatch(o);
                default: PrintHelp(); return ExitOk;
            }
        }

        private static int RunClean(Options o)
        {
            if (!Privileges.IsElevated())
            {
                Console.Error.WriteLine("error: cleaning needs administrator rights. Open an elevated terminal and run it again.");
                return ExitNotElevated;
            }
            // Not disposed: the Ctrl+C handler runs on a thread of its own and may still touch it as the clean ends.
            var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler onKey = (s, e) => { e.Cancel = OnCancelKey(cancel, Console.Out); };
            Console.CancelKeyPress += onKey;
            CleanReport report;
            try { report = Cleaner.Clean(RequestFor(o, o.Areas, cancel.Token, CliLockWait)); }
            finally { Console.CancelKeyPress -= onKey; }
            return ReportClean(report, Console.Out, Console.Error);
        }

        /// <summary>
        /// Ctrl+C during a clean: the first press asks the clean to stop at its next safe point and keeps the process
        /// (returns true, for e.Cancel); a later press returns false, so Windows ends the process.
        /// </summary>
        internal static bool OnCancelKey(CancellationTokenSource cancel, TextWriter output)
        {
            if (cancel.IsCancellationRequested) return false;
            output.WriteLine("Stopping after the current step...");
            cancel.Cancel();
            return true;
        }

        /// <summary>The clean ramopt asks for: its areas, --timeout as the deadline, the Ctrl+C token and the lock wait.</summary>
        internal static CleanRequest RequestFor(Options o, Areas areas, CancellationToken cancel, TimeSpan lockWait)
        {
            return new CleanRequest
            {
                Areas = areas,
                Cancel = cancel,
                Deadline = o.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(o.TimeoutSeconds) : (TimeSpan?)null,
                LockWait = lockWait
            };
        }

        /// <summary>Prints a clean's outcome (a busy one as an error) and returns ramopt clean's exit code.</summary>
        internal static int ReportClean(CleanReport r, TextWriter output, TextWriter errors)
        {
            if (r.Stopped == CleanStop.Busy)
            {
                errors.WriteLine(BusyMessage);
                return ExitBusy;
            }
            PrintReport(output, r);
            return ExitCodeFor(r);
        }

        /// <summary>Busy: ExitBusy. Stopped (Ctrl+C or the time limit), or an area failed or partly worked: ExitPartial.</summary>
        internal static int ExitCodeFor(CleanReport r)
        {
            if (r.Stopped == CleanStop.Busy) return ExitBusy;
            if (!r.Completed) return ExitPartial;
            return r.AllOk ? ExitOk : ExitPartial;
        }

        /// <summary>
        /// The line after a stopped clean's report: "Stopped after workingsets; not run: modified, registry." or
        /// "Hit the 120 s limit after 3 of 6 areas; not run: ...". Null for a complete or busy clean. Pure.
        /// </summary>
        internal static string StopLine(CleanReport r)
        {
            if (r == null || r.Completed || r.Stopped == CleanStop.Busy) return null;
            var notRun = new List<string>();
            string lastRan = null;
            foreach (AreaResult a in r.Results)
            {
                string name = a.Area == Areas.Services ? "services" : AreaNames.NameOf(a.Area);
                if (a.NotRun) notRun.Add(name);
                else if (a.Area != Areas.Services) lastRan = name;
            }
            string rest = notRun.Count == 0 ? "." : "; not run: " + string.Join(", ", notRun) + ".";
            if (r.Stopped == CleanStop.TimedOut)
            {
                string limit = r.Deadline == null ? "time"
                    : Math.Round(r.Deadline.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
                return "Hit the " + limit + " limit after " + r.AreasRun + " of " + r.AreasTotal + " areas" + rest;
            }
            return (lastRan == null ? "Stopped before any area ran" : "Stopped after " + lastRan) + rest;
        }

        private static int RunWatch(Options o)
        {
            if (!Privileges.IsElevated())
            {
                Console.Error.WriteLine("error: watch mode cleans memory and needs administrator rights.");
                return ExitNotElevated;
            }
            // One source for Ctrl+C: it ends the wait, and stops a clean under way at its next safe point. Not disposed:
            // the Ctrl+C handler runs on a thread of its own.
            var cancel = new CancellationTokenSource();
            WaitHandle stop = cancel.Token.WaitHandle;
            Console.CancelKeyPress += (s, e) => { e.Cancel = OnCancelKey(cancel, Console.Out); };
            Console.WriteLine("Watching memory: clean when load >= {0}%, check every {1}s, wait {2}s between cleans. Ctrl+C stops.",
                o.ThresholdPercent, o.IntervalSeconds, o.CooldownSeconds);
            Stopwatch clock = Stopwatch.StartNew();
            // Windows' low-memory signal can end a wait early; the interval poll stays, because the signal fires at
            // a fixed kernel level, not at --threshold. Null when the signal is unavailable.
            WaitHandle low = MemoryPressure.LowWaitHandle();
            // No lock wait: a clean that finds another one running skips this turn and tries again later.
            var watch = new Watcher
            {
                Elapsed = () => clock.Elapsed,
                Clean = areas => Cleaner.Clean(RequestFor(o, areas, cancel.Token, TimeSpan.Zero))
            };
            watch.Wait = (t, includeLow) =>
            {
                WaitHandle[] handles = includeLow && low != null ? new WaitHandle[] { stop, low } : new WaitHandle[] { stop };
                int i = WaitHandle.WaitAny(handles, t);
                return i == WaitHandle.WaitTimeout ? WakeReason.Interval : i == 0 ? WakeReason.Stop : WakeReason.LowMemory;
            };
            return watch.Run(o, Console.Out, Console.Error);
        }

        public static void PrintStatus(TextWriter w, MemorySnapshot s)
        {
            w.WriteLine("Physical memory  {0} total, {1}% load", Format.Bytes(s.TotalPhys), s.LoadPercent);
            w.WriteLine("  In use         {0}", Format.Bytes(s.InUse));
            w.WriteLine("  Available      {0}  (free + standby cache)", Format.Bytes(s.AvailablePhys));
            if (s.HasLists)
            {
                w.WriteLine("    Free/zero    {0}", Format.Bytes(s.FreeAndZero));
                w.WriteLine("    Standby      {0}  (cache, given back on demand; {1} low priority)",
                    Format.Bytes(s.Standby), Format.Bytes(s.StandbyLowPriority));
                w.WriteLine("  Modified       {0}  (waiting to be written to disk; {1} can be written now)",
                    Format.Bytes(s.Modified), Format.Bytes(s.Modified - Math.Min(s.ModifiedNoWrite, s.Modified)));
            }
            else
            {
                w.WriteLine("  (standby/modified breakdown needs administrator rights)");
            }
            w.WriteLine("  File cache     {0}", Format.Bytes(s.SystemCache));
            if (s.CommitLimit == 0) w.WriteLine("  Committed      unknown");
            else
                w.WriteLine("  Committed      {0} of {1} ({2}% of the limit; cleaning does not lower this)",
                    Format.Bytes(s.CommitTotal), Format.Bytes(s.CommitLimit), s.CommitPercent);
            w.WriteLine("  Windows says   {0}", ConditionText(s.Condition));
        }

        /// <summary>Windows' own memory signal in words, for the status line.</summary>
        internal static string ConditionText(MemoryCondition c)
        {
            switch (c)
            {
                case MemoryCondition.Low: return "memory is low";
                case MemoryCondition.Neutral: return "memory is not low";
                case MemoryCondition.High: return "memory is plentiful";
                default: return "unknown";
            }
        }

        /// <summary>
        /// What a clean of these areas would likely do, from one snapshot. Reads only: changes nothing and needs no
        /// admin (without it, the list-based figures can't be estimated, and a line says so).
        /// </summary>
        public static void PrintDryRun(TextWriter w, MemorySnapshot s, Areas areas)
        {
            w.WriteLine("Dry run: nothing was cleaned.");
            List<AreaEstimate> rows = CleanEstimate.For(s, areas);
            bool needsLists = false, unknown = false;
            foreach (AreaEstimate e in rows)
            {
                w.WriteLine("  {0,-13} {1}", AreaNames.NameOf(e.Area), EstimateText(e));
                if (e.Kind == EstimateKind.Unknown) unknown = true;
                if (e.Note == CleanEstimate.NeedsAdmin) needsLists = true;
            }
            if (needsLists) w.WriteLine("(the standby and modified figures need an elevated terminal)");
            ulong drop = CleanEstimate.InUseDropAtLeast(rows);
            ulong cache = CleanEstimate.FileCacheDropUpTo(rows);
            bool trims = (areas & Areas.WorkingSets) != 0;
            string plusTrim = trims ? ", plus whatever trimming releases." : ".";
            if (drop > 0 && cache > 0)
                w.WriteLine("In use could drop by about {0}, and by up to {1} more from the file cache{2}", Format.Bytes(drop), Format.Bytes(cache), plusTrim);
            else if (drop > 0)
                w.WriteLine("In use could drop by about {0}{1}", Format.Bytes(drop), plusTrim);
            else if (cache > 0)
                w.WriteLine("In use could drop by up to {0} from the file cache{1}", Format.Bytes(cache), plusTrim);
            else if (trims)
                w.WriteLine("In use could drop by whatever trimming releases; that can't be estimated.");
            else if (unknown)
                w.WriteLine("How much In use could drop can't be estimated.");
            else
                w.WriteLine("In use is not likely to drop: these areas work on cache that already counts as available, or write to disk.");
            string advice = AdviceText(CleanEstimate.Advise(s), s);
            if (advice != null) w.WriteLine(advice);
        }

        /// <summary>One dry-run row: "about 560.0 MB written to disk, ...", "can't be estimated: why" or "no memory figure: why".</summary>
        internal static string EstimateText(AreaEstimate e)
        {
            switch (e.Kind)
            {
                case EstimateKind.Bytes: return (e.UpperBound ? "up to " : "about ") + Format.Bytes(e.Bytes) + " " + e.Note;
                case EstimateKind.NoMemoryFigure: return "no memory figure: " + e.Note;
                default: return "can't be estimated: " + e.Note;
            }
        }

        /// <summary>The advice as one terminal line, or null when Windows gave no signal.</summary>
        internal static string AdviceText(CleanAdvice a, MemorySnapshot s)
        {
            switch (a)
            {
                case CleanAdvice.CommitBound:
                    return "Committed memory is " + (s == null ? 0 : s.CommitPercent)
                        + "% of the limit; cleaning does not lower it. Close programs you are not using.";
                default: return CleanEstimate.AdviceSentence(a);
            }
        }

        public static void PrintReport(TextWriter w, CleanReport r)
        {
            foreach (AreaResult a in r.Results)
            {
                AreaOutcome outcome = a.Outcome;
                string state = a.NotRun ? "not run" : outcome == AreaOutcome.Skipped ? "skipped" : outcome == AreaOutcome.Partial ? "partial"
                    : outcome == AreaOutcome.Ok ? "ok" : "FAILED";
                // Skipped areas did no work, so they show no time; the stop line says why the rest did not run.
                string time = outcome == AreaOutcome.Skipped ? "" : " (" + Format.Seconds(a.Duration) + ")";
                string message = a.NotRun || a.Message == null ? "" : ": " + a.Message;
                w.WriteLine("  {0,-12} {1}{2}{3}", AreaNames.NameOf(a.Area), state, message, time);
            }
            w.WriteLine("In use: {0} -> {1} ({2} freed)",
                Format.Bytes(r.Before.InUse), Format.Bytes(r.After.InUse), Format.Bytes(r.InUseFreed));
            if (r.Before.HasLists && r.After.HasLists)
                w.WriteLine("Free/zero: {0} -> {1}   Standby: {2} -> {3}",
                    Format.Bytes(r.Before.FreeAndZero), Format.Bytes(r.After.FreeAndZero),
                    Format.Bytes(r.Before.Standby), Format.Bytes(r.After.Standby));
            w.WriteLine("Took {0}.", Format.Seconds(r.Duration));
            string stopLine = StopLine(r);
            if (stopLine != null) w.WriteLine(stopLine);
            if (r.LockNote != null) w.WriteLine("note: " + r.LockNote);
        }

        private static void PrintHelp()
        {
            Console.WriteLine(HelpText());
        }

        /// <summary>The help text. The area list and the default set come from AreaNames.Known, so they cannot drift.</summary>
        public static string HelpText()
        {
            var w = new StringWriter(CultureInfo.InvariantCulture);
            w.WriteLine("ramopt - trims Windows memory working sets and caches (cleaning needs an elevated terminal)");
            w.WriteLine();
            w.WriteLine("Usage:");
            w.WriteLine("  ramopt status                          show memory use (no admin needed)");
            w.WriteLine("  ramopt clean [--areas LIST | --all] [--timeout S]");
            w.WriteLine("                                         clean once; Ctrl+C stops it after the current step");
            w.WriteLine("  ramopt clean --dry-run [--areas LIST | --all]   show what a clean would do; changes nothing (no admin needed)");
            w.WriteLine("  ramopt watch [--threshold N] [--interval S] [--cooldown S] [--timeout S] [--areas LIST | --all]");
            w.WriteLine("                                         clean whenever load reaches N% (default 85, every 30s, 300s apart);");
            w.WriteLine("                                         a clean that leaves load at N% or more doubles the wait (up to {0}x)",
                1 << AutoCleanPolicy.MaxBackoffSteps);
            w.WriteLine("  --timeout S                            end each clean after S seconds (1-3600), once the step under way finishes");
            w.WriteLine("If another clean is running, clean waits up to {0} s for it and then gives up; watch skips that turn.",
                CliLockWait.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            w.WriteLine();
            var optIn = new List<string>();
            foreach (var kv in AreaNames.Known) if ((Areas.Default & kv.Value) == 0) optIn.Add("'" + kv.Key + "'");
            w.WriteLine("Areas (comma separated; default = all but {0}):", JoinAnd(optIn));
            foreach (var kv in AreaNames.Known) w.WriteLine("  {0,-13} {1}", kv.Key, AreaHelp(kv.Value));
            w.WriteLine();
            w.WriteLine("Exit codes: {0} ok, {1} bad arguments, {2} some area failed or only partly worked, or the clean was stopped,",
                ExitOk, ExitUsage, ExitPartial);
            w.Write("            {0} not elevated, {1} watch stopped after {2} errors in a row, {3} another clean is running.",
                ExitNotElevated, ExitWatchFailed, Watcher.MaxConsecutiveErrors, ExitBusy);
            return w.ToString();
        }

        /// <summary>One line of help per area; throws for an area without one, so a new area cannot be left out.</summary>
        internal static string AreaHelp(Areas area)
        {
            switch (area)
            {
                case Areas.CombinePages: return "merge identical memory pages (Windows 10+)";
                case Areas.WorkingSets: return "trim every process's working set";
                case Areas.SystemFileCache: return "trim the system file cache";
                case Areas.VolumeCache: return "write each fixed drive's cached file changes to disk (a burst of disk writes)";
                case Areas.ModifiedList: return "write dirty pages to disk";
                case Areas.LowPriorityStandby: return "drop low-priority standby cache";
                case Areas.StandbyList: return "drop ALL standby cache (frees most, but apps and files load slower afterwards)";
                case Areas.RegistryCache: return "flush registry changes to disk (Windows 8.1+)";
                default: throw new ArgumentOutOfRangeException("area");
            }
        }

        static string JoinAnd(IList<string> items)
        {
            if (items.Count < 2) return string.Join("", items);
            var head = new List<string>(items);
            head.RemoveAt(head.Count - 1);
            return string.Join(", ", head) + " and " + items[items.Count - 1];
        }
    }
}
