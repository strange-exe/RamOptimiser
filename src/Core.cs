using System;
using System.Collections.Generic;
using System.ComponentModel;
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

            Native.SYSTEM_MEMORY_LIST_INFORMATION li;
            if (TryQueryLists(out li))
            {
                s.HasLists = true;
                s.FreeAndZero = (li.FreePageCount.ToUInt64() + li.ZeroPageCount.ToUInt64()) * pageSize;
                ulong standby = 0;
                foreach (UIntPtr p in li.PageCountByPriority) standby += p.ToUInt64();
                s.Standby = standby * pageSize;
                s.Modified = li.ModifiedPageCount.ToUInt64() * pageSize;
            }
            return s;
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

    public sealed class AreaResult
    {
        public Areas Area;
        public bool Ok;
        public bool Skipped;
        public string Message;
        public int NtStatus;     // the failing NTSTATUS, or 0
        public int Win32Error;   // the failing Win32 error code, or 0
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

        /// <summary>Drop in "in use" memory (what Task Manager shows as In use). Negative if it grew.</summary>
        public long InUseFreed { get { return (long)Before.InUse - (long)After.InUse; } }

        public bool AllOk
        {
            get { foreach (var r in Results) if (!r.Ok && !r.Skipped) return false; return true; }
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
            var report = new CleanReport();
            Privileges.Enable(SeProfileSingleProcess);
            Privileges.Enable(SeIncreaseQuota);
            report.Before = MemorySnapshot.Take();

            if ((areas & Areas.Services) != 0)
            {
                var r = new AreaResult { Area = Areas.Services };
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
                report.Results.Add(r);
            }

            Version os = Environment.OSVersion.Version;
            bool repeat = (areas & Areas.WorkingSets) != 0 && (areas & Areas.ModifiedList) != 0;
            ulong passStart = 0;
            AreaResult trim = null, flush = null;
            foreach (var kv in AreaNames.Known)
            {
                if ((areas & kv.Value) == 0) continue;
                var r = new AreaResult { Area = kv.Value };
                if (kv.Value == Areas.WorkingSets && repeat) passStart = SafeInUse();
                try { Run(kv.Value, os, r); }
                catch (Exception ex) { r.Ok = false; r.Message = ex.Message; }
                report.Results.Add(r);
                if (kv.Value == Areas.WorkingSets) trim = r;
                if (kv.Value == Areas.ModifiedList) flush = r;
                // Further passes run before the standby purges, so the pages they flush are purged too.
                if (kv.Value == Areas.ModifiedList && repeat && trim.Ok && flush.Ok)
                    report.Passes = RepeatPasses(os, report.Before.TotalPhys, passStart, maxPasses);
            }
            if (report.Passes > 1) trim.Message = report.Passes + " passes";

            report.After = MemorySnapshot.Take();
            return report;
        }

        /// <summary>Runs trim then flush again while each pass still frees enough; returns the total pass count.</summary>
        private static int RepeatPasses(Version os, ulong total, ulong passStart, int maxPasses)
        {
            int passes = 1;
            ulong before = passStart;
            while (true)
            {
                ulong now = SafeInUse();
                long freed = (long)before - (long)now;
                if (before == 0 || now == 0 || !AnotherPass(freed, total, passes, maxPasses)) return passes;
                var trim = new AreaResult { Area = Areas.WorkingSets };
                var flush = new AreaResult { Area = Areas.ModifiedList };
                try { Run(Areas.WorkingSets, os, trim); Run(Areas.ModifiedList, os, flush); }
                catch (Exception) { return passes; }
                if (!trim.Ok || !flush.Ok) return passes;
                passes++;
                before = now;
            }
        }

        static ulong SafeInUse()
        {
            try { return MemorySnapshot.Take().InUse; }
            catch (Win32Exception) { return 0; }
        }

        private static void Run(Areas area, Version os, AreaResult r)
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
                    SetResult(r, CombinePages());
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
            var flushed = new List<string>();
            var failed = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                string letter = d.Name.Substring(0, 2); // "C:"
                using (SafeFileHandle h = Native.CreateFile(@"\\.\" + letter, Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    int err = h.IsInvalid ? Marshal.GetLastWin32Error()
                        : (Native.FlushFileBuffers(h) ? 0 : Marshal.GetLastWin32Error());
                    if (err == 0) flushed.Add(letter);
                    else failed.Add(letter + " " + new Win32Exception(err).Message.TrimEnd('.').ToLowerInvariant());
                    if (err != 0 && r.Win32Error == 0) r.Win32Error = err;
                }
            }
            if (flushed.Count == 0 && failed.Count == 0) { Skip(r, "no fixed drives found"); return; }
            r.Ok = flushed.Count > 0;
            r.Message = SummariseVolumes(flushed, failed);
            if (r.Ok) r.Win32Error = 0;
        }

        /// <summary>Null when every drive flushed; otherwise "flushed N of M drives; X: why".</summary>
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

        private static int CombinePages()
        {
            int size = Marshal.SizeOf(typeof(Native.MEMORY_COMBINE_INFORMATION_EX));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(new Native.MEMORY_COMBINE_INFORMATION_EX(), buf, false);
                return Native.NtSetSystemInformation(Native.SystemCombinePhysicalMemoryInformation, buf, size);
            }
            finally { Marshal.FreeHGlobal(buf); }
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
        public static bool ShouldClean(uint loadPercent, int thresholdPercent, DateTime? lastClean, DateTime now, TimeSpan cooldown)
        {
            if (loadPercent < thresholdPercent) return false;
            return lastClean == null || now - lastClean.Value >= cooldown;
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
    }
}
