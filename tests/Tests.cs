// Minimal test runner (no test framework needed: only the .NET Framework compiler is required).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RamOptimiser.Tests
{
    public static class TestMain
    {
        static int failed, passed;

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        static void Throws<T>(string name, Action a) where T : Exception
        {
            try { a(); Check(name, false, "no exception"); }
            catch (T) { Check(name, true); }
            catch (Exception ex) { Check(name, false, "threw " + ex.GetType().Name); }
        }

        public static int Main()
        {
            // Like both exes, restrict the DLL search before anything else, so the whole suite runs under it.
            bool dllSearchRestricted = Native.RestrictDllSearchToSystem32();
            var searchAttrs = typeof(Native).Assembly.GetCustomAttributes(typeof(DefaultDllImportSearchPathsAttribute), false);
            Check("dll search: P/Invoke imports search System32 only", searchAttrs.Length == 1
                && ((DefaultDllImportSearchPathsAttribute)searchAttrs[0]).Paths == DllImportSearchPath.System32,
                searchAttrs.Length + " attribute(s)");
            Check("dll search: process default limited to System32", dllSearchRestricted);

            // Area parsing
            Check("parse single", AreaNames.Parse("standby") == Areas.StandbyList);
            Check("parse list, spaces, case", AreaNames.Parse(" WorkingSets , modified") == (Areas.WorkingSets | Areas.ModifiedList));
            Check("parse default excludes full standby", (AreaNames.Parse("default") & Areas.StandbyList) == 0);
            Check("parse all includes standby", AreaNames.Parse("all") == Areas.All && (Areas.All & Areas.StandbyList) != 0);
            Throws<ArgumentException>("parse unknown", () => AreaNames.Parse("ram"));
            Throws<ArgumentException>("parse empty", () => AreaNames.Parse(" , "));
            Check("every flag has a name", AllFlagsNamed());
            Check("working sets run before the lists they feed", IndexOf(Areas.WorkingSets) < IndexOf(Areas.ModifiedList)
                && IndexOf(Areas.ModifiedList) < IndexOf(Areas.StandbyList));
            Check("parse volumes", AreaNames.Parse("volumes") == Areas.VolumeCache);
            Check("volume flush is opt-in", (Areas.Default & Areas.VolumeCache) == 0 && (Areas.All & Areas.VolumeCache) != 0);
            Check("volume flush runs before the modified list it feeds", IndexOf(Areas.VolumeCache) < IndexOf(Areas.ModifiedList));
            Check("combine runs first", IndexOf(Areas.CombinePages) == 0);
            const ulong Ram16 = 16UL << 30; // 2% = 328 MB, above the 256 MB floor
            Check("another pass: big gain", Cleaner.AnotherPass(1200L << 20, Ram16, 1, 3));
            Check("another pass: gain under 2% of RAM stops", !Cleaner.AnotherPass(300L << 20, Ram16, 1, 3));
            Check("another pass: 256 MB floor on small RAM", Cleaner.AnotherPass(256L << 20, 4UL << 30, 1, 3)
                && !Cleaner.AnotherPass(255L << 20, 4UL << 30, 1, 3));
            Check("another pass: capped", !Cleaner.AnotherPass(4000L << 20, Ram16, 3, 3));
            Check("another pass: none when in use grew", !Cleaner.AnotherPass(-500L << 20, Ram16, 1, 3));
            Check("another pass: one-pass cap", !Cleaner.AnotherPass(4000L << 20, Ram16, 1, 1));
            Check("volume summary: all ok", Cleaner.SummariseVolumes(new[] { "C:", "D:" }, new string[0]) == null);
            Check("volume summary: partial names the failures",
                Cleaner.SummariseVolumes(new[] { "C:" }, new[] { "E: access is denied" }) == "flushed 1 of 2 drives; E: access is denied");

            // Options
            Check("no args = help", Options.Parse(new string[0]).Command == Command.Help);
            var o = Options.Parse(new[] { "watch", "--threshold", "90", "--interval", "5", "--cooldown", "0", "--all" });
            Check("watch options", o.Command == Command.Watch && o.ThresholdPercent == 90 && o.IntervalSeconds == 5
                && o.CooldownSeconds == 0 && o.Areas == Areas.All);
            Check("clean default areas", Options.Parse(new[] { "clean" }).Areas == Areas.Default);
            Throws<ArgumentException>("threshold > 100", () => Options.Parse(new[] { "watch", "--threshold", "101" }));
            Throws<ArgumentException>("threshold negative", () => Options.Parse(new[] { "watch", "--threshold", "-5" }));
            Throws<ArgumentException>("missing value", () => Options.Parse(new[] { "clean", "--areas" }));
            Throws<ArgumentException>("unknown command", () => Options.Parse(new[] { "purge" }));
            Throws<ArgumentException>("status takes no options", () => Options.Parse(new[] { "status", "--all" }));

            // Auto-clean policy
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
            var cd = TimeSpan.FromMinutes(5);
            Check("below threshold", !AutoCleanPolicy.ShouldClean(84, 85, null, t0, cd));
            Check("at threshold, first time", AutoCleanPolicy.ShouldClean(85, 85, null, t0, cd));
            Check("inside cooldown", !AutoCleanPolicy.ShouldClean(95, 85, t0, t0.AddMinutes(4), cd));
            Check("cooldown elapsed", AutoCleanPolicy.ShouldClean(95, 85, t0, t0.AddMinutes(5), cd));

            // Formatting
            Check("bytes", Format.Bytes(512L) == "512 B", Format.Bytes(512L));
            Check("megabytes", Format.Bytes(1536L * 1024) == "1.5 MB", Format.Bytes(1536L * 1024));
            Check("negative", Format.Bytes(-2048L) == "-2.0 KB", Format.Bytes(-2048L));

            // Top apps: grouped by name, largest first, Idle left out, capped at n.
            var top = AppMemoryList.Aggregate(new[] {
                new KeyValuePair<string, ulong>("msedge", 300), new KeyValuePair<string, ulong>("MSEDGE", 200),
                new KeyValuePair<string, ulong>("code", 400), new KeyValuePair<string, ulong>("Idle", 9999),
                new KeyValuePair<string, ulong>("", 50), new KeyValuePair<string, ulong>("tiny", 1) }, 2);
            Check("top apps: grouped and ordered", top.Count == 2 && top[0].Name == "msedge" && top[0].Bytes == 500
                && top[0].Processes == 2 && top[1].Name == "code", top.Count > 0 ? top[0].Name + " " + top[0].Bytes : "empty");
            Check("top apps: live list works", AppMemoryList.Top(5).Count > 0);
            MemorySnapshot live = MemorySnapshot.Take();
            Check("commit: read and below limit", live.CommitLimit > 0 && live.CommitTotal > 0 && live.CommitTotal <= live.CommitLimit
                && live.CommitPercent <= 100, Format.Bytes(live.CommitTotal) + " of " + Format.Bytes(live.CommitLimit));

            // Native layouts must match the x64 C structs, or the kernel reads garbage.
            Check("x64 process", IntPtr.Size == 8, "build with /platform:x64");
            Check("MEMORY_COMBINE_INFORMATION_EX size", Marshal.SizeOf(typeof(Native.MEMORY_COMBINE_INFORMATION_EX)) == 24);
            Check("SYSTEM_MEMORY_LIST_INFORMATION size", Marshal.SizeOf(typeof(Native.SYSTEM_MEMORY_LIST_INFORMATION)) == 22 * 8);
            Check("MEMORYSTATUSEX size", Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) == 64);
            Check("PERFORMANCE_INFORMATION size", Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION)) == 104);

            // Live, read-only: a snapshot is self-consistent.
            MemorySnapshot s = MemorySnapshot.Take();
            Check("snapshot total > 0", s.TotalPhys > 0);
            Check("available <= total", s.AvailablePhys <= s.TotalPhys);
            Check("load is a percent", s.LoadPercent <= 100);
            if (s.HasLists)
                Check("free+standby ~ available", Near(s.FreeAndZero + s.Standby, s.AvailablePhys, 0.10),
                    Format.Bytes(s.FreeAndZero + s.Standby) + " vs " + Format.Bytes(s.AvailablePhys));
            var sw = new StringWriter();
            Program.PrintStatus(sw, s);
            Check("status output", sw.ToString().Contains("In use"));

            // Status-code mapping
            Check("privilege status explained", Cleaner.DescribeStatus(Native.STATUS_PRIVILEGE_NOT_HELD).Contains("administrator"));
            Check("version gate", Cleaner.AtLeast(new Version(10, 0), 6, 3) && !Cleaner.AtLeast(new Version(6, 2), 6, 3));

            // Report math
            var rep = new CleanReport
            {
                Before = new MemorySnapshot { TotalPhys = 100, AvailablePhys = 40 },
                After = new MemorySnapshot { TotalPhys = 100, AvailablePhys = 55 }
            };
            rep.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            rep.Results.Add(new AreaResult { Area = Areas.CombinePages, Skipped = true });
            Check("freed = drop in use", rep.InUseFreed == 15);
            Check("skipped is not a failure", rep.AllOk);
            rep.Results.Add(new AreaResult { Area = Areas.StandbyList, Ok = false });
            Check("failure detected", !rep.AllOk);

            // GUI plumbing (settings, auto-clean service, startup task, single instance, tray icon)
            int guiFailed = GuiPlumbingTests.Run();

            // GUI logic (wording, validation, theme rules, icon) and static XAML checks
            int appFailed = AppTests.Run();
            int totalPassed = passed + GuiPlumbingTests.Passed + AppTests.Passed, totalFailed = failed + guiFailed + appFailed;

            Console.WriteLine("{0} passed, {1} failed", totalPassed, totalFailed);
            return totalFailed == 0 ? 0 : 1;
        }

        static bool Near(ulong a, ulong b, double tolerance)
        {
            return Math.Abs((double)a - b) <= tolerance * Math.Max(a, b);
        }

        static int IndexOf(Areas a)
        {
            for (int i = 0; i < AreaNames.Known.Length; i++) if (AreaNames.Known[i].Value == a) return i;
            return -1;
        }

        static bool AllFlagsNamed()
        {
            var seen = new List<Areas>();
            foreach (var kv in AreaNames.Known) seen.Add(kv.Value);
            for (int bit = 1; bit <= (int)Areas.All; bit <<= 1)
                if (((int)Areas.All & bit) != 0 && !seen.Contains((Areas)bit)) return false;
            return true;
        }
    }
}
