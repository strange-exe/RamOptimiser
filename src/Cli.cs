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
                    default: throw new ArgumentException("Unknown option '" + args[i] + "'.");
                }
            }
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
        /// <summary>Waits one interval; returns true when watching should stop.</summary>
        public Func<TimeSpan, bool> WaitStop;

        /// <summary>
        /// Samples, cleans when AutoCleanPolicy says so, and waits, until WaitStop says stop (ExitOk). A failed sample
        /// or clean is logged with a timestamp and watching goes on; MaxConsecutiveErrors failed iterations, or failed
        /// cleans, in a row end it (ExitWatchFailed).
        /// </summary>
        public int Run(Options o, TextWriter output, TextWriter errors)
        {
            TimeSpan? last = null;
            TimeSpan cooldown = TimeSpan.FromSeconds(o.CooldownSeconds);
            TimeSpan interval = TimeSpan.FromSeconds(o.IntervalSeconds);
            int futileStreak = 0, errorsInARow = 0, failedCleans = 0;
            do
            {
                bool cleaning = false;
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
                        failedCleans = 0;
                        Program.PrintReport(output, report);
                        futileStreak = AutoCleanPolicy.NextStreak(futileStreak, report.After.LoadPercent, o.ThresholdPercent);
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
            } while (!WaitStop(TimeSpan.FromSeconds(o.IntervalSeconds)));
            output.WriteLine("Stopped.");
            return Program.ExitOk;
        }
    }

    public static class Program
    {
        public const int ExitOk = 0, ExitUsage = 1, ExitPartial = 2, ExitNotElevated = 3, ExitWatchFailed = 4;

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

            switch (o.Command)
            {
                case Command.Status: PrintStatus(Console.Out, MemorySnapshot.Take()); return ExitOk;
                case Command.Clean: return RunClean(o.Areas);
                case Command.Watch: return RunWatch(o);
                default: PrintHelp(); return ExitOk;
            }
        }

        private static int RunClean(Areas areas)
        {
            if (!Privileges.IsElevated())
            {
                Console.Error.WriteLine("error: cleaning needs administrator rights. Open an elevated terminal and run it again.");
                return ExitNotElevated;
            }
            CleanReport report = Cleaner.Clean(areas);
            PrintReport(Console.Out, report);
            return report.AllOk ? ExitOk : ExitPartial;
        }

        private static int RunWatch(Options o)
        {
            if (!Privileges.IsElevated())
            {
                Console.Error.WriteLine("error: watch mode cleans memory and needs administrator rights.");
                return ExitNotElevated;
            }
            using (var stop = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
                Console.WriteLine("Watching memory: clean when load >= {0}%, check every {1}s, wait {2}s between cleans. Ctrl+C stops.",
                    o.ThresholdPercent, o.IntervalSeconds, o.CooldownSeconds);
                Stopwatch clock = Stopwatch.StartNew();
                var watch = new Watcher { Elapsed = () => clock.Elapsed, WaitStop = t => stop.WaitOne(t) };
                return watch.Run(o, Console.Out, Console.Error);
            }
        }

        public static void PrintStatus(TextWriter w, MemorySnapshot s)
        {
            w.WriteLine("Physical memory  {0} total, {1}% load", Format.Bytes(s.TotalPhys), s.LoadPercent);
            w.WriteLine("  In use         {0}", Format.Bytes(s.InUse));
            w.WriteLine("  Available      {0}  (free + standby cache)", Format.Bytes(s.AvailablePhys));
            if (s.HasLists)
            {
                w.WriteLine("    Free/zero    {0}", Format.Bytes(s.FreeAndZero));
                w.WriteLine("    Standby      {0}  (cache, given back on demand)", Format.Bytes(s.Standby));
                w.WriteLine("  Modified       {0}  (waiting to be written to disk)", Format.Bytes(s.Modified));
            }
            else
            {
                w.WriteLine("  (standby/modified breakdown needs administrator rights)");
            }
            w.WriteLine("  File cache     {0}", Format.Bytes(s.SystemCache));
        }

        public static void PrintReport(TextWriter w, CleanReport r)
        {
            foreach (AreaResult a in r.Results)
            {
                AreaOutcome outcome = a.Outcome;
                string state = outcome == AreaOutcome.Skipped ? "skipped" : outcome == AreaOutcome.Partial ? "partial"
                    : outcome == AreaOutcome.Ok ? "ok" : "FAILED";
                w.WriteLine("  {0,-12} {1}{2}", AreaNames.NameOf(a.Area), state, a.Message == null ? "" : ": " + a.Message);
            }
            w.WriteLine("In use: {0} -> {1} ({2} freed)",
                Format.Bytes(r.Before.InUse), Format.Bytes(r.After.InUse), Format.Bytes(r.InUseFreed));
            if (r.Before.HasLists && r.After.HasLists)
                w.WriteLine("Free/zero: {0} -> {1}   Standby: {2} -> {3}",
                    Format.Bytes(r.Before.FreeAndZero), Format.Bytes(r.After.FreeAndZero),
                    Format.Bytes(r.Before.Standby), Format.Bytes(r.After.Standby));
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
            w.WriteLine("  ramopt clean [--areas LIST | --all]    clean once");
            w.WriteLine("  ramopt watch [--threshold N] [--interval S] [--cooldown S] [--areas LIST | --all]");
            w.WriteLine("                                         clean whenever load reaches N% (default 85, every 30s, 300s apart);");
            w.WriteLine("                                         a clean that leaves load at N% or more doubles the wait (up to {0}x)",
                1 << AutoCleanPolicy.MaxBackoffSteps);
            w.WriteLine();
            var optIn = new List<string>();
            foreach (var kv in AreaNames.Known) if ((Areas.Default & kv.Value) == 0) optIn.Add("'" + kv.Key + "'");
            w.WriteLine("Areas (comma separated; default = all but {0}):", JoinAnd(optIn));
            foreach (var kv in AreaNames.Known) w.WriteLine("  {0,-13} {1}", kv.Key, AreaHelp(kv.Value));
            w.WriteLine();
            w.WriteLine("Exit codes: {0} ok, {1} bad arguments, {2} some area failed or only partly worked, {3} not elevated,",
                ExitOk, ExitUsage, ExitPartial, ExitNotElevated);
            w.Write("            {0} watch stopped after {1} errors in a row.", ExitWatchFailed, Watcher.MaxConsecutiveErrors);
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
