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
        /// <summary>--json: machine-readable output on stdout (status, clean, clean --dry-run, watch); errors stay text on stderr.</summary>
        public bool Json;
        /// <summary>clean and watch --no-log: do not add cleans to the clean log.</summary>
        public bool NoLog;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            if (args.Length == 0) return o;
            switch (args[0].ToLowerInvariant())
            {
                case "status": o.Command = Command.Status; break;
                case "clean": o.Command = Command.Clean; break;
                case "watch": o.Command = Command.Watch; break;
                case "help": case "-h": case "--help": case "/?":
                    // Anything after help is ignored, as before, except output options: a script asking for JSON
                    // must not get the help text on stdout with exit 0.
                    for (int i = 1; i < args.Length; i++)
                    {
                        string h = args[i].ToLowerInvariant();
                        if (h == "--json" || h == "--no-log") throw new ArgumentException("'help' takes no options.");
                    }
                    o.Command = Command.Help;
                    return o;
                default: throw new ArgumentException("Unknown command '" + args[0] + "'.");
            }
            bool onlyJson = true;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a != "--json") onlyJson = false;
                switch (a)
                {
                    case "--json": o.Json = true; break;
                    case "--no-log": o.NoLog = true; break;
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
            if (o.NoLog && o.Command != Command.Clean && o.Command != Command.Watch)
                throw new ArgumentException("--no-log works only with 'clean' and 'watch'.");
            if (o.Command == Command.Status && !onlyJson)
                throw new ArgumentException("'status' takes no options other than --json.");
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

    /// <summary>
    /// Builds each clean's log record and appends it to the clean log unless logging is off (--no-log). Write is the
    /// CleanRequest.UnderLock step, so a record is appended while the clean lock is still held and appends from
    /// ramopt and the app never interleave; Finish covers a clean whose UnderLock did not run (a busy one, which never
    /// took the lock). Never throws: a failed append leaves the clean as it was, and TakeWarning says so once.
    /// </summary>
    public sealed class CleanRecorder
    {
        public const string CliSource = "cli", WatchSource = "watch";
        public const string LogFailedNote = "note: could not add this clean to the clean log.";

        readonly string source;
        readonly bool log;
        readonly Func<string, bool> append;
        readonly Func<DateTime> utcNow;
        CleanReport recorded;
        string line;
        bool failed, warned;

        /// <summary>Why the next clean runs: manual, threshold, interval or low-memory.</summary>
        public string Reason = "manual";

        /// <summary>append gets one record without its line break and says whether it was written; unused when log is false.</summary>
        public CleanRecorder(string source, bool log, Func<string, bool> append, Func<DateTime> utcNow)
        {
            this.source = source;
            this.log = log && append != null;
            this.append = append;
            this.utcNow = utcNow;
        }

        /// <summary>The real recorder: CleanLog.TryAppend into the data folder (none for an account without one).</summary>
        public static CleanRecorder ForLog(string source, bool log)
        {
            return new CleanRecorder(source, log, l => CleanLog.TryAppend(AppPaths.DataFolder, l), () => DateTime.UtcNow);
        }

        /// <summary>The last clean's record, or null before the first.</summary>
        public string Line { get { return line; } }

        /// <summary>Records this clean (once): builds its record and appends it when logging is on.</summary>
        public void Write(CleanReport r)
        {
            if (r == null || ReferenceEquals(r, recorded)) return;
            recorded = r;
            line = null;
            try
            {
                line = CleanLog.FormatClean(r, source, Reason, CleanLog.StopName(r.Stopped), CleanLog.NewId(), utcNow());
                if (log && !append(line)) failed = true;
            }
            catch (Exception) { failed = true; }
        }

        /// <summary>Records the clean if Write has not already, and returns its record (null if it could not be built).</summary>
        public string Finish(CleanReport r)
        {
            Write(r);
            return line;
        }

        /// <summary>LogFailedNote the first time an append has failed, then null, so a long watch says it once.</summary>
        public string TakeWarning()
        {
            if (!failed || warned) return null;
            warned = true;
            return LogFailedNote;
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
        /// <summary>
        /// Records each clean (source watch). Null: nothing is logged, and --json builds its records without one. A
        /// Clean that wires Recorder.Write as its UnderLock gets its record appended under the clean lock.
        /// </summary>
        public CleanRecorder Recorder;

        WakeReason DoWait(TimeSpan interval, bool includeLow)
        {
            if (Wait != null) return Wait(interval, includeLow);
            return WaitStop(interval) ? WakeReason.Stop : WakeReason.Interval;
        }

        /// <summary>
        /// Samples, cleans when AutoCleanPolicy says so, and waits, until the wait says stop (ExitOk). The wait also
        /// ends early on Windows' low-memory signal when WatchSchedule allows it; the clean decision is unchanged. A
        /// failed sample or clean is logged with a timestamp and watching goes on; MaxConsecutiveErrors failed
        /// iterations, or failed cleans, in a row end it (ExitWatchFailed). With --json, stdout gets one record per
        /// clean (busy turns included) and the other lines go to errors.
        /// </summary>
        public int Run(Options o, TextWriter output, TextWriter errors)
        {
            TextWriter info = Program.InfoWriter(o, output, errors);
            CleanRecorder recorder = Recorder ?? new CleanRecorder(CleanRecorder.WatchSource, false, null, () => DateTime.UtcNow);
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
                        info.WriteLine("[{0:HH:mm:ss}] load {1}% >= {2}%: cleaning", Now(), s.LoadPercent, o.ThresholdPercent);
                        // Recorded before cleaning, so a clean that keeps failing still waits the cooldown.
                        last = now;
                        cleaning = true;
                        recorder.Reason = wake == WakeReason.LowMemory ? "low-memory" : "threshold";
                        CleanReport report = Clean(o.Areas);
                        cleaning = false;
                        string record = recorder.Finish(report);
                        string warning = recorder.TakeWarning();
                        if (warning != null) errors.WriteLine(warning);
                        if (o.Json && record != null) output.WriteLine(record);
                        if (report.Stopped == CleanStop.Busy)
                        {
                            // Another clean (ramopt or the app) was running. Not an error, and not futile: before
                            // equals after only because nothing ran, so it must not double the wait.
                            info.WriteLine("[{0:HH:mm:ss}] another clean is running: skipped this one", Now());
                        }
                        else
                        {
                            // Only a clean that ran counts for the low signal: a failed one must not be retried at once.
                            cleanedThisTick = true;
                            failedCleans = 0;
                            if (!o.Json) Program.PrintReport(output, report);
                            else if (report.LockNote != null) info.WriteLine("note: " + report.LockNote);
                            if (CleanEstimate.Advise(s) == CleanAdvice.CommitBound)
                                info.WriteLine("  note: committed memory is {0}% of the limit; cleaning does not lower it. Close programs you are not using.",
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
                    info.WriteLine("[{0:HH:mm:ss}] Windows reports low memory: checking now", Now());
            }
            info.WriteLine("Stopped.");
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
            return Dispatch(o, output, sample, runClean, runWatch, () => DateTime.UtcNow);
        }

        /// <summary>As above, with the clock for --json status passed in.</summary>
        internal static int Dispatch(Options o, TextWriter output, Func<MemorySnapshot> sample, Func<Areas, int> runClean,
            Func<Options, int> runWatch, Func<DateTime> utcNow)
        {
            switch (o.Command)
            {
                case Command.Status:
                    if (o.Json) output.WriteLine(StatusJson(sample(), utcNow()));
                    else PrintStatus(output, sample());
                    return ExitOk;
                case Command.Clean:
                    // Before the elevation check: a dry run only reads, needs no admin, never reaches the cleaner and
                    // never writes the clean log.
                    if (o.DryRun)
                    {
                        if (o.Json) output.WriteLine(DryRunJson(sample(), o.Areas));
                        else PrintDryRun(output, sample(), o.Areas);
                        return ExitOk;
                    }
                    return runClean(o.Areas);
                case Command.Watch: return runWatch(o);
                default: PrintHelp(); return ExitOk;
            }
        }

        /// <summary>Where progress and notes go: stdout, or stderr with --json so stdout carries only JSON.</summary>
        internal static TextWriter InfoWriter(Options o, TextWriter output, TextWriter errors)
        {
            return o.Json ? errors : output;
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
            TextWriter info = InfoWriter(o, Console.Out, Console.Error);
            ConsoleCancelEventHandler onKey = (s, e) => { e.Cancel = OnCancelKey(cancel, info); };
            Console.CancelKeyPress += onKey;
            try
            {
                return CleanOnce(o, cancel.Token, Cleaner.Clean, CleanRecorder.ForLog(CleanRecorder.CliSource, !o.NoLog),
                    Console.Out, Console.Error);
            }
            finally { Console.CancelKeyPress -= onKey; }
        }

        /// <summary>
        /// One ramopt clean with the cleaner and recorder passed in: the record is written under the clean lock (or
        /// after a busy clean, which never took it), then the text report or, with --json, the record alone is
        /// printed. Returns ramopt clean's exit code.
        /// </summary>
        internal static int CleanOnce(Options o, CancellationToken cancel, Func<CleanRequest, CleanReport> clean, CleanRecorder recorder,
            TextWriter output, TextWriter errors)
        {
            CleanRequest req = RequestFor(o, o.Areas, cancel, CliLockWait);
            recorder.Reason = "manual";
            req.UnderLock = recorder.Write;
            CleanReport report = clean(req);
            string record = recorder.Finish(report);
            string warning = recorder.TakeWarning();
            if (warning != null) errors.WriteLine(warning);
            if (!o.Json) return ReportClean(report, output, errors);
            if (record != null) output.WriteLine(record);
            if (report.Stopped == CleanStop.Busy) errors.WriteLine(BusyMessage);
            else if (report.LockNote != null) errors.WriteLine("note: " + report.LockNote);
            return ExitCodeFor(report);
        }

        /// <summary>
        /// Ctrl+C during a clean: the first press asks the clean to stop at its next safe point and keeps the process
        /// (returns true, for e.Cancel); a later press returns false, so Windows ends the process.
        /// </summary>
        internal static bool OnCancelKey(CancellationTokenSource cancel, TextWriter output)
        {
            return OnCancelKey(cancel, output, () => true);
        }

        /// <summary>
        /// As above; cleaning says whether a clean is running. Watch spends most of its time waiting between checks,
        /// and then Ctrl+C just ends the wait, with no "Stopping after the current step" for a step that isn't there.
        /// </summary>
        internal static bool OnCancelKey(CancellationTokenSource cancel, TextWriter output, Func<bool> cleaning)
        {
            if (cancel.IsCancellationRequested) return false;
            if (cleaning()) output.WriteLine("Stopping after the current step...");
            cancel.Cancel();
            return true;
        }

        /// <summary>The clean ramopt asks for: its areas, --timeout as the deadline, the Ctrl+C token and the lock wait.</summary>
        /// <summary>
        /// watch's clean: no wait for the clean lock (a busy turn is skipped), the record appended under the lock by
        /// recorder.Write, and inClean(true) while it runs, then inClean(false), for the Ctrl+C handler.
        /// </summary>
        internal static Func<Areas, CleanReport> WatchClean(Options o, CancellationToken cancel, CleanRecorder recorder,
            Func<CleanRequest, CleanReport> clean, Action<bool> inClean)
        {
            return areas =>
            {
                inClean(true);
                try
                {
                    CleanRequest req = RequestFor(o, areas, cancel, TimeSpan.Zero);
                    req.UnderLock = recorder.Write;
                    return clean(req);
                }
                finally { inClean(false); }
            };
        }

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
                return "Hit the " + limit + " limit after " + r.AreasRun + " of " + r.AreasTotal + (r.AreasTotal == 1 ? " area" : " areas") + rest;
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
            int inClean = 0; // 1 while a clean runs, read by the Ctrl+C handler on its own thread
            TextWriter info = InfoWriter(o, Console.Out, Console.Error);
            Console.CancelKeyPress += (s, e) => { e.Cancel = OnCancelKey(cancel, info, () => Volatile.Read(ref inClean) == 1); };
            info.WriteLine("Watching memory: clean when load >= {0}%, check every {1}s, wait {2}s between cleans. Ctrl+C stops.",
                o.ThresholdPercent, o.IntervalSeconds, o.CooldownSeconds);
            Stopwatch clock = Stopwatch.StartNew();
            // Windows' low-memory signal can end a wait early; the interval poll stays, because the signal fires at
            // a fixed kernel level, not at --threshold. Null when the signal is unavailable.
            WaitHandle low = MemoryPressure.LowWaitHandle();
            // No lock wait: a clean that finds another one running skips this turn and tries again later. Each clean's
            // record is appended under the clean lock, unless --no-log.
            CleanRecorder recorder = CleanRecorder.ForLog(CleanRecorder.WatchSource, !o.NoLog);
            var watch = new Watcher
            {
                Elapsed = () => clock.Elapsed,
                Recorder = recorder,
                Clean = WatchClean(o, cancel.Token, recorder, Cleaner.Clean, on => Volatile.Write(ref inClean, on ? 1 : 0))
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

        /// <summary>
        /// status --json: one object (v1, documented in the README). "lists" is null without the page-list figures
        /// (they need admin), "commit" null when Windows gave no commit figures. Bytes throughout, except
        /// repurposedByPriority: Windows' raw page counts, cumulative since boot, as MemorySnapshot keeps them. Pure.
        /// </summary>
        internal static string StatusJson(MemorySnapshot s, DateTime utc)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("v"); w.Number(1);
            w.Name("time"); w.String(CleanLog.Time(utc));
            w.Name("totalPhys"); w.Number(s.TotalPhys);
            w.Name("availablePhys"); w.Number(s.AvailablePhys);
            w.Name("inUse"); w.Number(s.InUse);
            w.Name("loadPercent"); w.Number((long)s.LoadPercent);
            w.Name("systemCache"); w.Number(s.SystemCache);
            w.Name("lists");
            if (!s.HasLists) w.Null();
            else
            {
                w.BeginObject();
                w.Name("freeAndZero"); w.Number(s.FreeAndZero);
                w.Name("standby"); w.Number(s.Standby);
                w.Name("standbyByPriority"); Numbers(w, s.StandbyByPriority);
                w.Name("modified"); w.Number(s.Modified);
                w.Name("modifiedNoWrite"); w.Number(s.ModifiedNoWrite);
                w.Name("repurposedByPriority"); Numbers(w, s.RepurposedByPriority);
                w.EndObject();
            }
            w.Name("commit");
            if (s.CommitLimit == 0) w.Null();
            else
            {
                w.BeginObject();
                w.Name("total"); w.Number(s.CommitTotal);
                w.Name("limit"); w.Number(s.CommitLimit);
                w.Name("percent"); w.Number((long)s.CommitPercent);
                w.EndObject();
            }
            w.Name("condition"); w.String(CleanLog.ConditionName(s.Condition));
            w.Name("advice"); w.String(CleanLog.AdviceName(CleanEstimate.Advise(s)));
            w.EndObject();
            return w.ToString();
        }

        static void Numbers(JsonWriter w, ulong[] values)
        {
            if (values == null) { w.Null(); return; }
            w.BeginArray();
            foreach (ulong v in values) w.Number(v);
            w.EndArray();
        }

        /// <summary>
        /// clean --dry-run --json: {"v":1,"type":"estimate","areas":[...],"inUseDropAtLeast":..,"fileCacheDropUpTo":..,
        /// "advice":..}. Each area has name, kind (bytes, unknown or none), bytes (null unless kind is bytes),
        /// upperBound (true when bytes is a ceiling) and note. The same figures as the text dry run. Pure.
        /// </summary>
        internal static string DryRunJson(MemorySnapshot s, Areas areas)
        {
            List<AreaEstimate> rows = CleanEstimate.For(s, areas);
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("v"); w.Number(1);
            w.Name("type"); w.String("estimate");
            w.Name("areas");
            w.BeginArray();
            foreach (AreaEstimate e in rows)
            {
                w.BeginObject();
                w.Name("name"); w.String(AreaNames.NameOf(e.Area));
                w.Name("kind"); w.String(e.Kind == EstimateKind.Bytes ? "bytes" : e.Kind == EstimateKind.Unknown ? "unknown" : "none");
                w.Name("bytes"); if (e.Kind == EstimateKind.Bytes) w.Number(e.Bytes); else w.Null();
                w.Name("upperBound"); w.Bool(e.UpperBound);
                w.Name("note"); w.String(e.Note);
                w.EndObject();
            }
            w.EndArray();
            w.Name("inUseDropAtLeast"); w.Number(CleanEstimate.InUseDropAtLeast(rows));
            w.Name("fileCacheDropUpTo"); w.Number(CleanEstimate.FileCacheDropUpTo(rows));
            w.Name("advice"); w.String(CleanLog.AdviceName(CleanEstimate.Advise(s)));
            w.EndObject();
            return w.ToString();
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

        /// <summary>Where the clean log lives, as help shows it.</summary>
        internal static readonly string LogPathText = Path.Combine(Path.Combine("%LOCALAPPDATA%", "RamOptimiser"), CleanLog.FileName);

        /// <summary>The help text. The area list and the default set come from AreaNames.Known, so they cannot drift.</summary>
        public static string HelpText()
        {
            var w = new StringWriter(CultureInfo.InvariantCulture);
            w.WriteLine("ramopt - trims Windows memory working sets and caches (cleaning needs an elevated terminal)");
            w.WriteLine();
            w.WriteLine("Usage:");
            w.WriteLine("  ramopt status [--json]                 show memory use (no admin needed)");
            w.WriteLine("  ramopt clean [--areas LIST | --all] [--timeout S] [--no-log] [--json]");
            w.WriteLine("                                         clean once; Ctrl+C stops it after the current step");
            w.WriteLine("  ramopt clean --dry-run [--areas LIST | --all] [--json]");
            w.WriteLine("                                         show what a clean would do; changes nothing (no admin needed)");
            w.WriteLine("  ramopt watch [--threshold N] [--interval S] [--cooldown S] [--timeout S] [--areas LIST | --all]");
            w.WriteLine("               [--no-log] [--json]");
            w.WriteLine("                                         clean whenever load reaches N% (default 85, every 30s, 300s apart);");
            w.WriteLine("                                         a clean that leaves load at N% or more doubles the wait (up to {0}x)",
                1 << AutoCleanPolicy.MaxBackoffSteps);
            w.WriteLine("  --timeout S                            end each clean after S seconds (1-3600), once the step under way finishes");
            w.WriteLine("  --no-log                               clean and watch: don't add cleans to the clean log");
            w.WriteLine("  --json                                 print JSON on stdout: one object (status, clean, clean --dry-run),");
            w.WriteLine("                                         or one line per clean (watch); messages and errors go to stderr");
            w.WriteLine("clean and watch add one line per clean to {0} unless --no-log; a dry run never does.", LogPathText);
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
