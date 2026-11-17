using System;
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

    public static class Program
    {
        public const int ExitOk = 0, ExitUsage = 1, ExitPartial = 2, ExitNotElevated = 3;

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
                DateTime? last = null;
                do
                {
                    MemorySnapshot s = MemorySnapshot.Take();
                    DateTime now = DateTime.Now;
                    if (AutoCleanPolicy.ShouldClean(s.LoadPercent, o.ThresholdPercent, last, now, TimeSpan.FromSeconds(o.CooldownSeconds)))
                    {
                        Console.WriteLine("[{0:HH:mm:ss}] load {1}% >= {2}%: cleaning", now, s.LoadPercent, o.ThresholdPercent);
                        PrintReport(Console.Out, Cleaner.Clean(o.Areas));
                        last = now;
                    }
                } while (!stop.WaitOne(TimeSpan.FromSeconds(o.IntervalSeconds)));
            }
            Console.WriteLine("Stopped.");
            return ExitOk;
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
                string state = a.Skipped ? "skipped" : a.Ok ? "ok" : "FAILED";
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
            Console.WriteLine(
@"ramopt - trims Windows memory working sets and caches (cleaning needs an elevated terminal)

Usage:
  ramopt status                          show memory use (no admin needed)
  ramopt clean [--areas LIST | --all]    clean once
  ramopt watch [--threshold N] [--interval S] [--cooldown S] [--areas LIST | --all]
                                         clean whenever load reaches N% (default 85, every 30s, 300s apart)

Areas (comma separated; default = all but 'standby'):
  workingsets   trim every process's working set
  filecache     trim the system file cache
  modified      write dirty pages to disk
  standby-low   drop low-priority standby cache
  standby       drop ALL standby cache (frees most, but apps and files load slower afterwards)
  combine       merge identical memory pages (Windows 10+)
  registry      flush registry changes to disk (Windows 8.1+)

Exit codes: 0 ok, 1 bad arguments, 2 some area failed, 3 not elevated.");
        }
    }
}
