using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;

namespace RamOptimiser
{
    /// <summary>Memory areas that can be cleaned. Order of the values is the order they run.</summary>
    [Flags]
    public enum Areas
    {
        None = 0,
        WorkingSets = 1,          // trim every process's working set (pages move to standby/modified lists)
        SystemFileCache = 2,      // trim the system file cache working set
        ModifiedList = 4,         // write modified pages to disk so they become standby
        LowPriorityStandby = 8,   // drop standby pages at priority 0 (least likely to be reused)
        StandbyList = 16,         // drop ALL standby pages (the whole disk cache: slows later file/app loads)
        CombinePages = 32,        // merge identical physical pages (Windows 10+)
        RegistryCache = 64,       // flush registry hive changes to disk (Windows 8.1+)
        VolumeCache = 128,        // write each fixed drive's dirty file cache to disk (FlushFileBuffers on the volume)

        // Same safe default as memreduct's auto-clean: everything except the full standby purge.
        // The volume flush is opt-in too: it forces a burst of disk writes for a usually small gain.
        Default = WorkingSets | SystemFileCache | ModifiedList | LowPriorityStandby | CombinePages | RegistryCache,
        All = Default | StandbyList | VolumeCache,

        // Power Clean only: stop the chosen catalog services first (see ServiceTrimmer). Not an area name on the
        // command line or in settings.ini, and not part of All: it needs a service list to act on.
        Services = 256
    }

    public static class AreaNames
    {
        // Ordered: this is also the execution order (working sets first so their pages reach the lists we purge next).
        // Combine runs first: measured (2026-11-11) neutral to +195 MB before the trim and no better after it, and
        // run first it cannot be confused with the rebound that follows a flush.
        public static readonly KeyValuePair<string, Areas>[] Known =
        {
            new KeyValuePair<string, Areas>("combine", Areas.CombinePages),
            new KeyValuePair<string, Areas>("workingsets", Areas.WorkingSets),
            new KeyValuePair<string, Areas>("filecache", Areas.SystemFileCache),
            new KeyValuePair<string, Areas>("volumes", Areas.VolumeCache),   // before "modified": its written pages land there
            new KeyValuePair<string, Areas>("modified", Areas.ModifiedList),
            new KeyValuePair<string, Areas>("standby-low", Areas.LowPriorityStandby),
            new KeyValuePair<string, Areas>("standby", Areas.StandbyList),
            new KeyValuePair<string, Areas>("registry", Areas.RegistryCache),
        };

        public static Areas Parse(string list)
        {
            if (list == null || list.Trim().Length == 0)
                throw new ArgumentException("No areas given.");
            Areas result = Areas.None;
            foreach (string raw in list.Split(','))
            {
                string name = raw.Trim().ToLowerInvariant();
                if (name.Length == 0) continue;
                if (name == "default") { result |= Areas.Default; continue; }
                if (name == "all") { result |= Areas.All; continue; }
                bool found = false;
                foreach (var kv in Known)
                    if (kv.Key == name) { result |= kv.Value; found = true; break; }
                if (!found)
                    throw new ArgumentException("Unknown area '" + raw.Trim() + "'. Known: " + string.Join(", ", KnownNames()) + ", default, all.");
            }
            if (result == Areas.None) throw new ArgumentException("No areas given.");
            return result;
        }

        public static string NameOf(Areas single)
        {
            foreach (var kv in Known) if (kv.Value == single) return kv.Key;
            throw new ArgumentOutOfRangeException("single");
        }

        public static IEnumerable<string> KnownNames()
        {
            foreach (var kv in Known) yield return kv.Key;
        }
    }

    /// <summary>A point-in-time view of physical memory, in bytes.</summary>
    public sealed class MemorySnapshot
    {
        public ulong TotalPhys;
        public ulong AvailablePhys;    // free + zero + standby: what Windows can hand out without paging
        public uint LoadPercent;
        public ulong SystemCache;      // system file cache working set
        public bool HasLists;          // false when the page-list query was refused (needs admin)
        public ulong FreeAndZero;      // truly unused pages
        public ulong Standby;          // cached pages: counted as "available", reused on demand
        public ulong Modified;         // dirty pages waiting to be written
        /// <summary>Standby bytes at each of the 8 priorities (0 is reused first); null when !HasLists.</summary>
        public ulong[] StandbyByPriority;
        /// <summary>Standby at priority 0: what the standby-low area drops. 0 when unknown.</summary>
        public ulong StandbyLowPriority
        {
            get { return StandbyByPriority == null || StandbyByPriority.Length == 0 ? 0 : StandbyByPriority[0]; }
        }
        /// <summary>
        /// Raw repurposed page counts per priority, most likely cumulative since boot (as RAMMap's Repurposed column), so
        /// only the difference between two samples means anything. Not used in any advice. Null when !HasLists.
        /// </summary>
        public ulong[] RepurposedByPriority;
        /// <summary>Modified pages Windows may not write yet (the modified no-write list), in bytes.</summary>
        public ulong ModifiedNoWrite;
        /// <summary>Modified pages bound for the page file, in bytes.</summary>
        public ulong ModifiedPageFile;
        /// <summary>The page size the counts were multiplied by (4096 when GetPerformanceInfo fails).</summary>
        public ulong PageSize;
        /// <summary>What Windows' own low and high memory signals say at this sample.</summary>
        public MemoryCondition Condition;
        /// <summary>Memory programs have asked Windows to back (RAM plus page file); 0 when unknown.</summary>
        public ulong CommitTotal;
        /// <summary>Most that can be committed before "out of memory"; 0 when unknown.</summary>
        public ulong CommitLimit;
        /// <summary>Commit charge as a percent of the limit: the pressure cleaning cannot lower. 0 when unknown.</summary>
        public uint CommitPercent { get { return CommitLimit == 0 ? 0 : (uint)Math.Min(100, CommitTotal * 100 / CommitLimit); } }

        public ulong InUse { get { return TotalPhys > AvailablePhys ? TotalPhys - AvailablePhys : 0; } }

        public static MemorySnapshot Take()
        {
            var s = new MemorySnapshot();
            var ms = new Native.MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (!Native.GlobalMemoryStatusEx(ref ms)) throw new Win32Exception();
            s.TotalPhys = ms.ullTotalPhys;
            s.AvailablePhys = ms.ullAvailPhys;
            s.LoadPercent = ms.dwMemoryLoad;

            Native.PERFORMANCE_INFORMATION pi;
            uint piSize = (uint)Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION));
            ulong pageSize = 4096;
            if (Native.GetPerformanceInfo(out pi, piSize))
            {
                pageSize = pi.PageSize.ToUInt64();
                s.SystemCache = pi.SystemCache.ToUInt64() * pageSize;
                s.CommitTotal = pi.CommitTotal.ToUInt64() * pageSize;
                s.CommitLimit = pi.CommitLimit.ToUInt64() * pageSize;
            }

            s.PageSize = pageSize;

            Native.SYSTEM_MEMORY_LIST_INFORMATION li;
            if (TryQueryLists(out li)) FillLists(s, li, pageSize);
            s.Condition = MemoryPressure.Query();
            return s;
        }

        /// <summary>
        /// Pure: copies the page-list figures into s, in bytes (repurposed counts stay in pages). Standby stays the
        /// total of the 8 priorities. Missing priority arrays read as zero. Tests pass a fake struct.
        /// </summary>
        internal static void FillLists(MemorySnapshot s, Native.SYSTEM_MEMORY_LIST_INFORMATION li, ulong pageSize)
        {
            s.HasLists = true;
            s.PageSize = pageSize;
            s.FreeAndZero = (li.FreePageCount.ToUInt64() + li.ZeroPageCount.ToUInt64()) * pageSize;
            s.StandbyByPriority = new ulong[8];
            s.RepurposedByPriority = new ulong[8];
            ulong standby = 0;
            for (int i = 0; i < 8; i++)
            {
                if (li.PageCountByPriority != null && i < li.PageCountByPriority.Length)
                    s.StandbyByPriority[i] = li.PageCountByPriority[i].ToUInt64() * pageSize;
                if (li.RepurposedPagesByPriority != null && i < li.RepurposedPagesByPriority.Length)
                    s.RepurposedByPriority[i] = li.RepurposedPagesByPriority[i].ToUInt64();
                standby += s.StandbyByPriority[i];
            }
            s.Standby = standby;
            s.Modified = li.ModifiedPageCount.ToUInt64() * pageSize;
            s.ModifiedNoWrite = li.ModifiedNoWritePageCount.ToUInt64() * pageSize;
            s.ModifiedPageFile = li.ModifiedPageCountPageFile.ToUInt64() * pageSize;
        }

        private static bool TryQueryLists(out Native.SYSTEM_MEMORY_LIST_INFORMATION info)
        {
            info = new Native.SYSTEM_MEMORY_LIST_INFORMATION();
            int size = Marshal.SizeOf(typeof(Native.SYSTEM_MEMORY_LIST_INFORMATION));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int returned;
                int status = Native.NtQuerySystemInformation(Native.SystemMemoryListInformation, buf, size, out returned);
                if (status != Native.STATUS_SUCCESS) return false;
                info = (Native.SYSTEM_MEMORY_LIST_INFORMATION)Marshal.PtrToStructure(buf, typeof(Native.SYSTEM_MEMORY_LIST_INFORMATION));
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    /// <summary>How one area went. Partial: it did part of its work (e.g. flushed some drives, not all).</summary>
    public enum AreaOutcome { Ok, Partial, Skipped, Failed }

    public sealed class AreaResult
    {
        public Areas Area;
        public bool Ok;
        public bool Skipped;
        /// <summary>Set together with Ok when only part of the work succeeded; Message says what failed.</summary>
        public bool Partial;
        public string Message;
        public int NtStatus;     // the failing NTSTATUS, or 0
        public int Win32Error;   // the failing Win32 error code, or 0
        /// <summary>Time spent on this area, every pass included (an area that threw still has its time).</summary>
        public TimeSpan Duration;

        /// <summary>The one outcome the flags add up to. Skipped wins, then failed, then partial.</summary>
        public AreaOutcome Outcome
        {
            get
            {
                if (Skipped) return AreaOutcome.Skipped;
                if (!Ok) return AreaOutcome.Failed;
                return Partial ? AreaOutcome.Partial : AreaOutcome.Ok;
            }
        }
    }

    public sealed class CleanReport
    {
        public MemorySnapshot Before;
        public MemorySnapshot After;
        public List<AreaResult> Results = new List<AreaResult>();
        /// <summary>Per-service outcome when the clean stopped services (Power Clean); empty otherwise.</summary>
        public List<ServiceAction> Services = new List<ServiceAction>();
        /// <summary>Trim-and-flush passes run (1 unless both working sets and the modified list were cleaned).</summary>
        public int Passes = 1;
        /// <summary>The whole clean, from the before snapshot to the after snapshot.</summary>
        public TimeSpan Duration;
        /// <summary>What combine merged, in bytes; 0 when it did not run, merged nothing, or Windows did not say.</summary>
        public ulong CombinedBytes;

        /// <summary>Drop in "in use" memory (what Task Manager shows as In use). Negative if it grew.</summary>
        public long InUseFreed { get { return (long)Before.InUse - (long)After.InUse; } }

        /// <summary>True when every area did all its work or was skipped; a partial area is not ok.</summary>
        public bool AllOk
        {
            get
            {
                foreach (var r in Results)
                    if (r.Outcome == AreaOutcome.Failed || r.Outcome == AreaOutcome.Partial) return false;
                return true;
            }
        }
    }

    public static class Privileges
    {
        public static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>Enables a privilege on this process's token. Returns false when the token does not hold it.</summary>
        public static bool Enable(string name)
        {
            IntPtr token;
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY, out token))
                return false;
            try
            {
                var tp = new Native.TOKEN_PRIVILEGES { PrivilegeCount = 1, Attributes = Native.SE_PRIVILEGE_ENABLED };
                if (!Native.LookupPrivilegeValue(null, name, out tp.Luid)) return false;
                if (!Native.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                // AdjustTokenPrivileges "succeeds" even when the privilege is absent; this is the real signal.
                return Marshal.GetLastWin32Error() != Native.ERROR_NOT_ALL_ASSIGNED;
            }
            finally { Native.CloseHandle(token); }
        }
    }

    public static class Cleaner
    {
        public const string SeProfileSingleProcess = "SeProfileSingleProcessPrivilege";
        public const string SeIncreaseQuota = "SeIncreaseQuotaPrivilege";

        /// <summary>Runs each requested area in a fixed order; one failure never stops the others.</summary>
        public static CleanReport Clean(Areas areas)
        {
            return Clean(areas, null);
        }

        /// <summary>
        /// Same, and with Areas.Services set, first stops the chosen catalog services (the list is filtered
        /// against ServiceCatalog, so nothing outside it is ever stopped). Their pages then reach the lists the
        /// later areas purge, and the before/after figures include what the services held.
        /// </summary>
        public static CleanReport Clean(Areas areas, ICollection<string> services)
        {
            return Clean(areas, services, DefaultMaxPasses);
        }

        /// <summary>
        /// Trimming empties the Memory Compression store into the modified list, and flushing that list refills the
        /// store, so one pass leaves memory a second pass can free. Measured on 2026-11-11 (16 GB PC): pass 1 freed
        /// 2.0 GB, pass 2 1.2 GB, pass 3 0.6 GB. More passes write more to the page file for less, so they stop at 3.
        /// </summary>
        public const int DefaultMaxPasses = 3;
        /// <summary>A further pass runs only if the last one freed at least this much (or 2% of RAM, if larger).</summary>
        public const long MinPassGain = 256L * 1024 * 1024;

        /// <summary>Pure: whether another trim-and-flush pass is worth running.</summary>
        internal static bool AnotherPass(long freedLastPass, ulong totalPhys, int passesDone, int maxPasses)
        {
            if (passesDone >= maxPasses) return false;
            long floor = Math.Max(MinPassGain, (long)(totalPhys / 50));
            return freedLastPass >= floor;
        }

        public static CleanReport Clean(Areas areas, ICollection<string> services, int maxPasses)
        {
            Privileges.Enable(SeProfileSingleProcess);
            Privileges.Enable(SeIncreaseQuota);
            Version os = Environment.OSVersion.Version;
            // Monotonic: DST and clock changes do not move it.
            Stopwatch watch = Stopwatch.StartNew();
            return Clean(areas, services, maxPasses, () => watch.Elapsed, MemorySnapshot.Take, SafeInUse,
                (report, r) => Run(r.Area, os, r, report));
        }

        /// <summary>
        /// The clean with its clock, snapshots, in-use reader and area runner passed in, so tests can time it with
        /// fakes. Duration runs from before the first snapshot to after the last.
        /// </summary>
        internal static CleanReport Clean(Areas areas, ICollection<string> services, int maxPasses, Func<TimeSpan> clock,
            Func<MemorySnapshot> snapshot, Func<ulong> inUse, Action<CleanReport, AreaResult> run)
        {
            var report = new CleanReport();
            TimeSpan start = clock();
            report.Before = snapshot();

            if ((areas & Areas.Services) != 0)
            {
                var r = new AreaResult { Area = Areas.Services };
                TimeSpan t0 = clock();
                List<string> chosen = ServiceCatalog.Filter(services);
                if (chosen.Count == 0) Skip(r, "no services chosen");
                else
                {
                    try
                    {
                        report.Services = ServiceTrimmer.Stop(chosen);
                        r.Ok = true;
                        foreach (ServiceAction a in report.Services) if (a.Failed) r.Ok = false;
                        r.Message = ServiceTrimmer.Summarise(report.Services);
                    }
                    catch (Exception ex) { r.Ok = false; r.Message = ex.Message; }
                }
                r.Duration += clock() - t0;
                report.Results.Add(r);
            }

            RunAreas(areas, report, maxPasses, inUse, r => run(report, r), clock);

            report.After = snapshot();
            report.Duration = clock() - start;
            return report;
        }

        /// <summary>As below, untimed (every area's Duration stays zero).</summary>
        internal static void RunAreas(Areas areas, CleanReport report, int maxPasses, Func<ulong> inUse, Action<AreaResult> run)
        {
            RunAreas(areas, report, maxPasses, inUse, run, () => TimeSpan.Zero);
        }

        /// <summary>
        /// Runs each requested area in AreaNames.Known order and adds its result to the report, then repeats trim and
        /// flush while it pays. inUse reads "in use" bytes (0 when unknown); run cleans r.Area into r; clock times each
        /// area, and later passes add their time to the one trim and flush row. Tests pass fakes.
        /// </summary>
        internal static void RunAreas(Areas areas, CleanReport report, int maxPasses, Func<ulong> inUse, Action<AreaResult> run,
            Func<TimeSpan> clock)
        {
            bool repeat = (areas & Areas.WorkingSets) != 0 && (areas & Areas.ModifiedList) != 0;
            ulong passStart = 0;
            // What the areas between the trim and the flush (file cache, drives) freed: later passes repeat only trim and
            // flush, so pass 1 leaves this out to be compared like-for-like.
            long freedBetween = 0;
            AreaResult trim = null, flush = null;
            foreach (var kv in AreaNames.Known)
            {
                if ((areas & kv.Value) == 0) continue;
                var r = new AreaResult { Area = kv.Value };
                bool between = repeat && trim != null && kv.Value != Areas.ModifiedList && flush == null;
                ulong areaStart = 0;
                if (kv.Value == Areas.WorkingSets && repeat) passStart = inUse();
                if (between) areaStart = inUse();
                string why = Timed(run, r, r, clock);
                if (why != null) { r.Ok = false; r.Message = why; }
                if (between && areaStart != 0)
                {
                    ulong areaEnd = inUse();
                    if (areaEnd != 0) freedBetween += (long)areaStart - (long)areaEnd;
                }
                report.Results.Add(r);
                if (kv.Value == Areas.WorkingSets) trim = r;
                if (kv.Value == Areas.ModifiedList) flush = r;
                // Further passes run before the standby purges, so the pages they flush are purged too.
                if (kv.Value == Areas.ModifiedList && repeat && trim.Ok && flush.Ok)
                    report.Passes = RepeatPasses(report.Before.TotalPhys, passStart, freedBetween, maxPasses, inUse, run, trim, flush, clock);
            }
            if (report.Passes > 1) trim.Message = Note(report.Passes + " passes", trim.Message);
        }

        /// <summary>
        /// Runs one area and adds the time it took to timeTo (the area itself, or the pass-1 row a later pass belongs
        /// to), even when it throws. Returns the exception's message, or null.
        /// </summary>
        static string Timed(Action<AreaResult> run, AreaResult r, AreaResult timeTo, Func<TimeSpan> clock)
        {
            TimeSpan t0 = clock();
            try { run(r); return null; }
            catch (Exception ex) { return ex.Message; }
            finally { timeTo.Duration += clock() - t0; }
        }

        /// <summary>
        /// Runs trim then flush again while each pass still frees enough; returns the total pass count. A pass that
        /// fails is noted on the trim or flush result ("pass 2 failed: why"); the area keeps its pass-1 outcome.
        /// Each pass's time is added to the pass-1 trim and flush results.
        /// </summary>
        private static int RepeatPasses(ulong total, ulong passStart, long freedBetween, int maxPasses, Func<ulong> inUse,
            Action<AreaResult> run, AreaResult trimResult, AreaResult flushResult, Func<TimeSpan> clock)
        {
            int passes = 1;
            ulong before = passStart;
            long leaveOut = freedBetween;
            while (true)
            {
                ulong now = inUse();
                long freed = (long)before - (long)now - leaveOut;
                leaveOut = 0;
                if (before == 0 || now == 0 || !AnotherPass(freed, total, passes, maxPasses)) return passes;
                var trim = new AreaResult { Area = Areas.WorkingSets };
                var flush = new AreaResult { Area = Areas.ModifiedList };
                string why = Timed(run, trim, trimResult, clock);
                if (why == null && trim.Ok) why = Timed(run, flush, flushResult, clock);
                AreaResult failed = !trim.Ok ? trim : !flush.Ok ? flush : null;
                if (failed != null)
                {
                    AreaResult target = failed == trim ? trimResult : flushResult;
                    string reason = (why ?? failed.Message ?? "unknown error").Trim().TrimEnd('.');
                    target.Message = Note(target.Message, "pass " + (passes + 1) + " failed: " + reason);
                    return passes;
                }
                passes++;
                before = now;
            }
        }

        /// <summary>"first; second", or whichever one is not null.</summary>
        static string Note(string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return second;
            if (string.IsNullOrEmpty(second)) return first;
            return first + "; " + second;
        }

        static ulong SafeInUse()
        {
            try { return MemorySnapshot.Take().InUse; }
            catch (Win32Exception) { return 0; }
        }

        private static void Run(Areas area, Version os, AreaResult r, CleanReport report)
        {
            switch (area)
            {
                case Areas.WorkingSets: SetResult(r, MemoryListCommand(Native.MemoryEmptyWorkingSets)); break;
                case Areas.ModifiedList: SetResult(r, MemoryListCommand(Native.MemoryFlushModifiedList)); break;
                case Areas.LowPriorityStandby: SetResult(r, MemoryListCommand(Native.MemoryPurgeLowPriorityStandbyList)); break;
                case Areas.StandbyList: SetResult(r, MemoryListCommand(Native.MemoryPurgeStandbyList)); break;
                case Areas.SystemFileCache:
                    // Documented: passing (SIZE_T)-1 for both sizes flushes the cache.
                    if (Native.SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0)) r.Ok = true;
                    else
                    {
                        r.Ok = false;
                        r.Win32Error = Marshal.GetLastWin32Error();
                        r.Message = new Win32Exception(r.Win32Error).Message;
                    }
                    break;
                case Areas.RegistryCache:
                    if (!AtLeast(os, 6, 3)) { Skip(r, "needs Windows 8.1 or later"); break; }
                    SetResult(r, Native.NtSetSystemInformation(Native.SystemRegistryReconciliationInformation, IntPtr.Zero, 0));
                    break;
                case Areas.CombinePages:
                    if (!AtLeast(os, 10, 0)) { Skip(r, "needs Windows 10 or later"); break; }
                    ulong pages;
                    SetResult(r, CombinePages(out pages));
                    if (r.Ok)
                    {
                        ulong pageSize = report.Before != null && report.Before.PageSize != 0 ? report.Before.PageSize : 4096;
                        report.CombinedBytes += pages * pageSize;
                        r.Message = CombineRowMessage(pages, pageSize);
                    }
                    break;
                case Areas.VolumeCache: FlushVolumes(r); break;
            }
        }

        /// <summary>
        /// FlushFileBuffers on a volume handle writes every open file's cached data on that volume
        /// (documented; needs administrator rights). Drives that refuse are reported, not fatal.
        /// </summary>
        private static void FlushVolumes(AreaResult r)
        {
            var drives = new List<KeyValuePair<string, bool>>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                drives.Add(new KeyValuePair<string, bool>(d.Name.Substring(0, 2), d.IsReady)); // "C:", ready
            }
            FlushVolumes(r, drives, FlushVolume);
        }

        /// <summary>
        /// Flushes each fixed drive (letter, ready) with flush, which returns 0 or a Win32 error. Tests pass fakes.
        /// A drive that is not ready (locked, offline, no media) is skipped, not flushed and not a failure. Some drives
        /// refusing makes the area partial; all of them refusing makes it fail.
        /// </summary>
        internal static void FlushVolumes(AreaResult r, IEnumerable<KeyValuePair<string, bool>> drives, Func<string, int> flush)
        {
            var flushed = new List<string>();
            var failed = new List<string>();
            var notReady = new List<string>();
            foreach (var d in drives)
            {
                string letter = d.Key;
                if (!d.Value) { notReady.Add(letter + " not ready"); continue; }
                int err = flush(letter);
                if (err == 0) flushed.Add(letter);
                else failed.Add(letter + " " + new Win32Exception(err).Message.TrimEnd('.').ToLowerInvariant());
                if (err != 0 && r.Win32Error == 0) r.Win32Error = err;
            }
            if (flushed.Count == 0 && failed.Count == 0)
            {
                Skip(r, notReady.Count == 0 ? "no fixed drives found" : "no fixed drive ready; " + string.Join("; ", notReady));
                return;
            }
            r.Ok = flushed.Count > 0;
            r.Partial = r.Ok && failed.Count > 0;
            var notes = new List<string>(failed);
            notes.AddRange(notReady);
            r.Message = SummariseVolumes(flushed, notes);
            if (r.Ok && !r.Partial) r.Win32Error = 0;
        }

        /// <summary>FlushFileBuffers on the "\\.\C:" volume handle: 0, or the Win32 error.</summary>
        private static int FlushVolume(string letter)
        {
            using (SafeFileHandle h = Native.CreateFile(@"\\.\" + letter, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                return h.IsInvalid ? Marshal.GetLastWin32Error()
                    : (Native.FlushFileBuffers(h) ? 0 : Marshal.GetLastWin32Error());
            }
        }

        /// <summary>Null when every drive flushed; otherwise "flushed N of M drives; X: why" (failed or not ready).</summary>
        internal static string SummariseVolumes(IList<string> flushed, IList<string> failed)
        {
            if (failed.Count == 0) return null;
            return "flushed " + flushed.Count + " of " + (flushed.Count + failed.Count) + " drives; " + string.Join("; ", failed);
        }

        private static int MemoryListCommand(int command)
        {
            IntPtr buf = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buf, command);
                return Native.NtSetSystemInformation(Native.SystemMemoryListInformation, buf, sizeof(int));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Merges identical pages; pagesCombined is what the kernel writes back into the struct (phnt treats it as
        /// in/out; not yet confirmed at runtime, so 0 may also mean Windows did not say).
        /// </summary>
        private static int CombinePages(out ulong pagesCombined)
        {
            pagesCombined = 0;
            int size = Marshal.SizeOf(typeof(Native.MEMORY_COMBINE_INFORMATION_EX));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(new Native.MEMORY_COMBINE_INFORMATION_EX(), buf, false);
                int status = Native.NtSetSystemInformation(Native.SystemCombinePhysicalMemoryInformation, buf, size);
                if (status == Native.STATUS_SUCCESS)
                {
                    var info = (Native.MEMORY_COMBINE_INFORMATION_EX)Marshal.PtrToStructure(buf, typeof(Native.MEMORY_COMBINE_INFORMATION_EX));
                    pagesCombined = info.PagesCombined.ToUInt64();
                }
                return status;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Pure: "merged 120.0 MB", or "no identical pages found" when nothing merged.</summary>
        internal static string CombineMessage(ulong pages, ulong pageSize)
        {
            if (pages == 0) return "no identical pages found";
            return "merged " + Format.Bytes(pages * pageSize);
        }

        /// <summary>
        /// Whether a run has confirmed that Windows writes PagesCombined back into the buffer. Until then a zero
        /// count may only mean "not reported", so the row claims nothing for it (open item from wave 1).
        /// </summary>
        internal static readonly bool PagesCombinedConfirmed = false;

        /// <summary>Pure: the combine row's message, or null for a zero count while the count is unconfirmed.</summary>
        internal static string CombineRowMessage(ulong pages, ulong pageSize)
        {
            if (pages == 0 && !PagesCombinedConfirmed) return null;
            return CombineMessage(pages, pageSize);
        }

        private static void SetResult(AreaResult r, int status)
        {
            r.Ok = status == Native.STATUS_SUCCESS;
            if (!r.Ok) { r.NtStatus = status; r.Message = DescribeStatus(status); }
        }

        private static void Skip(AreaResult r, string why) { r.Skipped = true; r.Message = why; }

        internal static bool AtLeast(Version v, int major, int minor)
        {
            return v.Major > major || (v.Major == major && v.Minor >= minor);
        }

        public static string DescribeStatus(int status)
        {
            if (status == Native.STATUS_PRIVILEGE_NOT_HELD || status == Native.STATUS_ACCESS_DENIED)
                return "needs administrator rights (run from an elevated terminal)";
            string text = new Win32Exception(Native.RtlNtStatusToDosError(status)).Message;
            return text + " (NTSTATUS 0x" + status.ToString("X8", CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>One app's memory: every process with the same name added together (Edge runs as many).</summary>
    public sealed class AppMemory
    {
        public string Name;
        public int Processes;
        /// <summary>Working set: RAM the processes hold now, shared pages included (as Task Manager's details view).</summary>
        public ulong Bytes;
    }

    public static class AppMemoryList
    {
        /// <summary>The n apps holding the most RAM right now. Needs no elevation; processes it cannot read are skipped.</summary>
        public static List<AppMemory> Top(int n)
        {
            var samples = new List<KeyValuePair<string, ulong>>();
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    try { samples.Add(new KeyValuePair<string, ulong>(p.ProcessName, (ulong)p.WorkingSet64)); }
                    catch (InvalidOperationException) { } // exited meanwhile
                }
            }
            return Aggregate(samples, n);
        }

        /// <summary>Groups by name (case-insensitive), largest first, ties by name; Idle and empty names left out. Pure.</summary>
        public static List<AppMemory> Aggregate(IEnumerable<KeyValuePair<string, ulong>> samples, int n)
        {
            var map = new Dictionary<string, AppMemory>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in samples)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Key == "Idle") continue;
                AppMemory a;
                if (!map.TryGetValue(kv.Key, out a)) map[kv.Key] = a = new AppMemory { Name = kv.Key };
                a.Processes++;
                a.Bytes += kv.Value;
            }
            var list = new List<AppMemory>(map.Values);
            list.Sort((x, y) => x.Bytes != y.Bytes ? y.Bytes.CompareTo(x.Bytes) : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
            if (n >= 0 && list.Count > n) list.RemoveRange(n, list.Count - n);
            return list;
        }
    }

    /// <summary>Decides when watch mode should clean: above the threshold and outside the cooldown.</summary>
    public static class AutoCleanPolicy
    {
        /// <summary>Each clean in a row that leaves load at or above the threshold doubles the wait, up to this many times.</summary>
        public const int MaxBackoffSteps = 4;

        public static bool ShouldClean(uint loadPercent, int thresholdPercent, DateTime? lastClean, DateTime now, TimeSpan cooldown)
        {
            if (loadPercent < thresholdPercent) return false;
            return lastClean == null || now - lastClean.Value >= cooldown;
        }

        /// <summary>The same on a monotonic clock (time since watching started), which DST and clock changes do not move.</summary>
        public static bool ShouldClean(uint loadPercent, int thresholdPercent, TimeSpan? lastClean, TimeSpan now, TimeSpan cooldown)
        {
            if (loadPercent < thresholdPercent) return false;
            return lastClean == null || now - lastClean.Value >= cooldown;
        }

        /// <summary>
        /// The cooldown, doubled per futile clean in a row (at most 2^MaxBackoffSteps times). When cleaning does not get
        /// load under the threshold, the memory is really in use, and cleaning again soon only makes programs read their
        /// pages back from disk. Pure.
        /// </summary>
        public static TimeSpan ThresholdCooldown(TimeSpan cooldown, int futileStreak)
        {
            int steps = Math.Max(0, Math.Min(MaxBackoffSteps, futileStreak));
            return TimeSpan.FromTicks(cooldown.Ticks * (1L << steps));
        }

        /// <summary>
        /// As above, for a cooldown that may be shorter than the check interval (watch --cooldown 0): once a clean
        /// was futile, the backoff doubles the longer of the two, so a zero cooldown still backs off. Pure.
        /// </summary>
        public static TimeSpan ThresholdCooldown(TimeSpan cooldown, TimeSpan interval, int futileStreak)
        {
            if (futileStreak <= 0) return cooldown;
            return ThresholdCooldown(cooldown > interval ? cooldown : interval, futileStreak);
        }

        /// <summary>The streak after a clean: one more when load stayed at or above the threshold, else back to 0. Pure.</summary>
        public static int NextStreak(int streak, uint loadAfter, int thresholdPercent)
        {
            return loadAfter >= thresholdPercent ? Math.Min(MaxBackoffSteps, streak + 1) : 0;
        }
    }

    public static class Format
    {
        public static string Bytes(long bytes)
        {
            string sign = bytes < 0 ? "-" : "";
            double v = Math.Abs((double)bytes);
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return sign + v.ToString(i == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[i];
        }

        public static string Bytes(ulong bytes) { return Bytes((long)Math.Min(bytes, (ulong)long.MaxValue)); }

        /// <summary>"&lt;0.1 s" under 100 ms, otherwise one decimal in the invariant culture: "1.8 s".</summary>
        public static string Seconds(TimeSpan t)
        {
            if (t.TotalMilliseconds < 100) return "<0.1 s";
            return t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }
    }

    /// <summary>What the current sample says about cleaning now. Recomputed per sample; no stored state.</summary>
    public enum CleanAdvice { None, CommitBound, WillHelp, Mixed, NotNeeded }

    public enum EstimateKind { Bytes, Unknown, NoMemoryFigure }

    /// <summary>What one area is likely to do, from a snapshot. Note says what the figure means, or why there is none.</summary>
    public sealed class AreaEstimate
    {
        public Areas Area;
        public EstimateKind Kind;
        public ulong Bytes;
        /// <summary>True when Bytes is a ceiling ("up to"), false when it is the likely amount ("about").</summary>
        public bool UpperBound;
        public string Note;
    }

    /// <summary>Pure estimates and advice from one snapshot, for the dry run, status and the app's advice line.</summary>
    public static class CleanEstimate
    {
        /// <summary>At or above this commit percent, commit is the limit and cleaning cannot lower it.</summary>
        public const int CommitBoundPercent = 90;

        public const string NeedsAdmin = "needs administrator rights";

        /// <summary>One row per chosen area, in AreaNames.Known order. Services is never listed.</summary>
        public static List<AreaEstimate> For(MemorySnapshot s, Areas areas)
        {
            var rows = new List<AreaEstimate>();
            if (s == null) return rows;
            foreach (var kv in AreaNames.Known)
                if ((areas & kv.Value) != 0) rows.Add(Estimate(s, kv.Value));
            return rows;
        }

        static AreaEstimate Estimate(MemorySnapshot s, Areas area)
        {
            var e = new AreaEstimate { Area = area };
            bool listBased = area == Areas.ModifiedList || area == Areas.LowPriorityStandby || area == Areas.StandbyList;
            if (listBased && !s.HasLists) { e.Kind = EstimateKind.Unknown; e.Note = NeedsAdmin; return e; }
            switch (area)
            {
                case Areas.CombinePages:
                    e.Kind = EstimateKind.Unknown; e.Note = "depends on how many pages are identical"; break;
                case Areas.WorkingSets:
                    e.Kind = EstimateKind.Unknown; e.Note = "programs read back what they touch next"; break;
                case Areas.SystemFileCache:
                    // SystemCache is "the size of the standby list plus the system working set" (PERFORMANCE_INFORMATION,
                    // learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information). Only the working
                    // set can move to standby; with the lists unreadable the whole figure is shown and said to include it.
                    e.Kind = EstimateKind.Bytes; e.UpperBound = true;
                    if (s.HasLists)
                    {
                        e.Bytes = s.SystemCache - Math.Min(s.Standby, s.SystemCache);
                        e.Note = "of file cache moved to standby, where it counts as available";
                    }
                    else
                    {
                        e.Bytes = s.SystemCache;
                        e.Note = "of file cache moved to standby (this figure also counts cache already on standby)";
                    }
                    break;
                case Areas.ModifiedList:
                    e.Kind = EstimateKind.Bytes; e.Bytes = s.Modified - Math.Min(s.ModifiedNoWrite, s.Modified);
                    e.Note = "written to disk, then counted as available"; break;
                case Areas.LowPriorityStandby:
                    // The purge drops priority 0 only (see Areas.LowPriorityStandby). Standby already counts as available.
                    e.Kind = EstimateKind.Bytes; e.Bytes = s.StandbyLowPriority;
                    e.Note = "of low-priority cache dropped (already counted as available)"; break;
                case Areas.StandbyList:
                    e.Kind = EstimateKind.Bytes; e.Bytes = s.Standby;
                    e.Note = "of cache dropped (already counted as available; apps and files load slower afterwards)"; break;
                case Areas.RegistryCache:
                    e.Kind = EstimateKind.NoMemoryFigure; e.Note = "writes registry changes to disk"; break;
                case Areas.VolumeCache:
                    e.Kind = EstimateKind.NoMemoryFigure; e.Note = "writes each drive's cached file changes to disk"; break;
                default:
                    e.Kind = EstimateKind.Unknown; e.Note = "no estimate for this area"; break;
            }
            return e;
        }

        /// <summary>
        /// The least In use is likely to drop: the modified pages written to disk. Purging standby lowers nothing,
        /// as standby already counts as available; trimming and combining can't be estimated.
        /// </summary>
        public static ulong InUseDropAtLeast(IList<AreaEstimate> e)
        {
            ulong sum = 0;
            if (e == null) return 0;
            foreach (AreaEstimate a in e)
                if (a != null && a.Area == Areas.ModifiedList && a.Kind == EstimateKind.Bytes) sum += a.Bytes;
            return sum;
        }

        /// <summary>
        /// The most In use could drop from flushing the file cache, which moves its pages to standby. A ceiling, not
        /// a likely amount: the system working set holds more than file pages. Zero when filecache is not chosen.
        /// </summary>
        public static ulong FileCacheDropUpTo(IList<AreaEstimate> e)
        {
            ulong sum = 0;
            if (e == null) return 0;
            foreach (AreaEstimate a in e)
                if (a != null && a.Area == Areas.SystemFileCache && a.Kind == EstimateKind.Bytes) sum += a.Bytes;
            return sum;
        }

        /// <summary>
        /// The advice sentence the app and the terminal share, or null for CommitBound (each surface words that one
        /// with its own figure) and None.
        /// </summary>
        public static string AdviceSentence(CleanAdvice a)
        {
            switch (a)
            {
                case CleanAdvice.WillHelp: return "Windows reports low memory: a clean can make room now.";
                case CleanAdvice.Mixed: return "Windows is not short of memory. A clean frees some now, and programs read part of it back.";
                case CleanAdvice.NotNeeded: return "Windows has plenty of free memory. Cleaning now mostly empties cache and slows the next few minutes.";
                default: return null;
            }
        }

        /// <summary>
        /// Commit at or above CommitBoundPercent wins (cleaning cannot lower it), then Windows' own signal: low means
        /// a clean helps, high means it is not needed, in between is mixed, unknown gives no advice.
        /// </summary>
        public static CleanAdvice Advise(MemorySnapshot s)
        {
            if (s == null) return CleanAdvice.None;
            if (s.CommitLimit > 0 && s.CommitPercent >= CommitBoundPercent) return CleanAdvice.CommitBound;
            switch (s.Condition)
            {
                case MemoryCondition.Low: return CleanAdvice.WillHelp;
                case MemoryCondition.High: return CleanAdvice.NotNeeded;
                case MemoryCondition.Neutral: return CleanAdvice.Mixed;
                default: return CleanAdvice.None;
            }
        }
    }
}
