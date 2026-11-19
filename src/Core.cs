using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
        /// <summary>Set together with Skipped when the clean stopped before this area (Message says why).</summary>
        public bool NotRun;
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

        /// <summary>Why the clean ended early, or None. The first cause seen wins.</summary>
        public CleanStop Stopped;
        /// <summary>True when nothing stopped the clean (AllOk still says how its areas went).</summary>
        public bool Completed { get { return Stopped == CleanStop.None; } }
        /// <summary>The time limit the clean ran under, or null for none.</summary>
        public TimeSpan? Deadline;
        /// <summary>Why the clean ran without the clean lock, or that the last holder ended without finishing; else null.</summary>
        public string LockNote;

        /// <summary>Areas chosen for this clean (the Power Clean services row is not an area).</summary>
        public int AreasTotal
        {
            get
            {
                int n = 0;
                foreach (var r in Results) if (r.Area != Areas.Services) n++;
                return n;
            }
        }

        /// <summary>Areas that ran before any stop.</summary>
        public int AreasRun
        {
            get
            {
                int n = 0;
                foreach (var r in Results) if (r.Area != Areas.Services && !r.NotRun) n++;
                return n;
            }
        }
    }

    /// <summary>Why a clean ended early. Busy: another clean held the clean lock, so this one ran no area.</summary>
    public enum CleanStop { None, Cancelled, TimedOut, Busy }

    /// <summary>Everything one clean needs, so the Clean overloads stop multiplying.</summary>
    public sealed class CleanRequest
    {
        public Areas Areas = Areas.Default;
        /// <summary>Catalog services to stop first when Areas has Services (Power Clean).</summary>
        public ICollection<string> Services;
        public int MaxPasses = Cleaner.DefaultMaxPasses;
        /// <summary>Stops the clean at its next safe point. default(CancellationToken) never fires.</summary>
        public CancellationToken Cancel;
        /// <summary>Stops the clean at the first safe point after this long; null for no limit.</summary>
        public TimeSpan? Deadline;
        /// <summary>How long to wait for a clean in another process to finish before skipping (Busy).</summary>
        public TimeSpan LockWait = TimeSpan.Zero;
        /// <summary>Runs after the after snapshot, before the lock is released, so log appends never interleave.</summary>
        public Action<CleanReport> UnderLock;
        /// <summary>How the workingsets area trims: all at once (the default), or app by app (ProcessTrimmer).</summary>
        public TrimMode Trim;
        /// <summary>EachProcess only: exe names to leave alone (from ProcessTrimmer.ParseExclusions); null for none.</summary>
        public ICollection<string> TrimExclusions;
        /// <summary>EachProcess only: leave the app in front alone, with all its processes.</summary>
        public bool SpareForeground = true;
        /// <summary>
        /// Called on the cleaning thread as each step starts (services first, then each area once, in clean order);
        /// extra trim-and-flush passes are not new steps. Null for none. An exception it throws is ignored.
        /// </summary>
        public Action<CleanProgress> Progress;
    }

    /// <summary>One step of a running clean: Step of Total, about to clean Area (Areas.Services for stopping services).</summary>
    public sealed class CleanProgress
    {
        public int Step;
        public int Total;
        public Areas Area;
    }

    /// <summary>
    /// System: one call empties every working set and the compression store (frees most). EachProcess: app by app,
    /// sparing the app in front and a list, without touching services or protected processes (frees less).
    /// </summary>
    public enum TrimMode { System, EachProcess }

    /// <summary>
    /// The stop condition of one clean: the token, or the clock past the deadline. Checked only at safe points (between
    /// steps), and latched, so the first cause seen wins. Used on the cleaning thread only.
    /// </summary>
    internal sealed class CleanStopper
    {
        readonly CancellationToken cancel;
        readonly TimeSpan? deadline;
        readonly Func<TimeSpan> clock;
        readonly TimeSpan start;
        CleanStop stopped;

        internal CleanStopper(CancellationToken cancel, TimeSpan? deadline, Func<TimeSpan> clock)
        {
            this.cancel = cancel;
            this.deadline = deadline;
            this.clock = clock;
            start = clock();
        }

        /// <summary>What the clean should do now: go on (None), or stop and why.</summary>
        public CleanStop Check()
        {
            if (stopped != CleanStop.None) return stopped;
            if (cancel.IsCancellationRequested) stopped = CleanStop.Cancelled;
            else if (deadline != null && clock() - start >= deadline.Value) stopped = CleanStop.TimedOut;
            return stopped;
        }

        /// <summary>The cause seen so far, without checking again.</summary>
        public CleanStop Stopped { get { return stopped; } }

        /// <summary>Time left before the deadline (zero once past it); TimeSpan.MaxValue with no deadline.</summary>
        public TimeSpan Remaining
        {
            get
            {
                if (deadline == null) return TimeSpan.MaxValue;
                TimeSpan left = deadline.Value - (clock() - start);
                return left < TimeSpan.Zero ? TimeSpan.Zero : left;
            }
        }
    }

    /// <summary>Takes the clean lock (CleanLock.TryAcquire in a clean; a fake in tests). held is released after the clean.</summary>
    internal delegate LockState LockTaker(TimeSpan wait, out IDisposable held, out string note);

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

        /// <summary>
        /// As below, without the clean lock, so it never reports Busy. Kept for callers that do not want the lock
        /// (the tests); the app and ramopt clean through Clean(CleanRequest), which takes it.
        /// </summary>
        public static CleanReport Clean(Areas areas, ICollection<string> services, int maxPasses)
        {
            return Clean(new CleanRequest { Areas = areas, Services = services, MaxPasses = maxPasses }, false);
        }

        /// <summary>
        /// The primary clean. Takes the cross-process clean lock first (waiting up to req.LockWait), so two front ends
        /// never clean at once: if another clean holds it, nothing runs and the report says Busy. The clean stops at
        /// the next safe point when req.Cancel fires or req.Deadline passes; the areas left are reported as not run,
        /// and the after snapshot is always taken, so the freed figure stays honest.
        /// </summary>
        public static CleanReport Clean(CleanRequest req)
        {
            return Clean(req, true);
        }

        static CleanReport Clean(CleanRequest req, bool takeLock)
        {
            if (req == null) throw new ArgumentNullException("req");
            Privileges.Enable(SeProfileSingleProcess);
            Privileges.Enable(SeIncreaseQuota);
            Version os = Environment.OSVersion.Version;
            // Monotonic: DST and clock changes do not move it.
            Stopwatch watch = Stopwatch.StartNew();
            // Extra trim-and-flush passes come through here too, so a per-process trim spares the same apps every pass.
            return Clean(req, () => watch.Elapsed, MemorySnapshot.Take, SafeInUse,
                (report, r, stop) =>
                {
                    if (TrimsEachProcess(req, r.Area)) ProcessTrimmer.TrimEach(req, r, stop.Check);
                    else Run(r.Area, os, r, report, stop);
                }, takeLock ? new LockTaker(TakeCleanLock) : null, ServiceTrimmer.Stop);
        }

        /// <summary>True when this area is the working-set trim and the request asks for it app by app. Pure.</summary>
        internal static bool TrimsEachProcess(CleanRequest req, Areas area)
        {
            return area == Areas.WorkingSets && req != null && req.Trim == TrimMode.EachProcess;
        }

        static LockState TakeCleanLock(TimeSpan wait, out IDisposable held, out string note)
        {
            CleanLock l;
            LockState state = CleanLock.TryAcquire(wait, out l, out note);
            held = l;
            return state;
        }

        /// <summary>
        /// The clean with its clock, snapshots, in-use reader and area runner passed in, so tests can time it with
        /// fakes. Duration runs from before the first snapshot to after the last. Takes no lock and cannot be stopped.
        /// </summary>
        internal static CleanReport Clean(Areas areas, ICollection<string> services, int maxPasses, Func<TimeSpan> clock,
            Func<MemorySnapshot> snapshot, Func<ulong> inUse, Action<CleanReport, AreaResult> run)
        {
            return Clean(new CleanRequest { Areas = areas, Services = services, MaxPasses = maxPasses }, clock, snapshot, inUse,
                (report, r, stop) => run(report, r), null, ServiceTrimmer.Stop);
        }

        /// <summary>
        /// The whole clean with every outside part passed in: the clock (which also times the deadline), snapshots,
        /// the in-use reader, the area runner (given the stop condition, for steps inside an area), the lock taker
        /// (null: run unguarded) and the service stopper. The lock is held from before the first snapshot until
        /// UnderLock has run, and released even when the clean throws.
        /// </summary>
        internal static CleanReport Clean(CleanRequest req, Func<TimeSpan> clock, Func<MemorySnapshot> snapshot, Func<ulong> inUse,
            Action<CleanReport, AreaResult, CleanStopper> run, LockTaker takeLock,
            Func<ICollection<string>, CancellationToken, List<ServiceAction>> stopServices)
        {
            IDisposable held = null;
            string lockNote = null;
            if (takeLock != null && takeLock(req.LockWait, out held, out lockNote) == LockState.Busy)
            {
                if (held != null) held.Dispose();
                // Nothing ran: one snapshot is both, so the report claims nothing freed and nothing lost.
                MemorySnapshot now = snapshot();
                return new CleanReport { Before = now, After = now, Stopped = CleanStop.Busy, Deadline = req.Deadline, LockNote = lockNote };
            }
            try
            {
                var report = new CleanReport { Deadline = req.Deadline, LockNote = lockNote };
                TimeSpan start = clock();
                var stopper = new CleanStopper(req.Cancel, req.Deadline, clock);
                report.Before = snapshot();
                var progress = new ProgressReporter(req);

                if ((req.Areas & Areas.Services) != 0)
                {
                    var r = new AreaResult { Area = Areas.Services };
                    CleanStop stop = stopper.Check();
                    if (stop != CleanStop.None) MarkNotRun(r, stop);
                    else
                    {
                        progress.Starting(Areas.Services);
                        TimeSpan t0 = clock();
                        List<string> chosen = ServiceCatalog.Filter(req.Services);
                        if (chosen.Count == 0) Skip(r, "no services chosen");
                        else
                        {
                            try
                            {
                                report.Services = StopServices(chosen, req.Cancel, stopper.Remaining, stopServices);
                                r.Ok = true;
                                foreach (ServiceAction a in report.Services) if (a.Failed) r.Ok = false;
                                r.Message = ServiceTrimmer.Summarise(report.Services);
                            }
                            catch (Exception ex) { r.Ok = false; r.Message = ex.Message; }
                        }
                        r.Duration += clock() - t0;
                    }
                    report.Results.Add(r);
                }

                RunAreas(req.Areas, report, req.MaxPasses, inUse, r => { progress.Starting(r.Area); run(report, r, stopper); },
                    clock, stopper.Check);

                report.After = snapshot();
                report.Duration = clock() - start;
                report.Stopped = stopper.Stopped;
                if (req.UnderLock != null) req.UnderLock(report);
                return report;
            }
            finally
            {
                if (held != null) held.Dispose();
            }
        }

        /// <summary>
        /// Stops the services with a token that fires on a cancel or when the time left runs out, so a slow stop's
        /// wait ends too. Each stop already accepted stays recorded for Restore (see ServiceTrimmer.StopPlanned).
        /// </summary>
        static List<ServiceAction> StopServices(List<string> chosen, CancellationToken cancel, TimeSpan left,
            Func<ICollection<string>, CancellationToken, List<ServiceAction>> stopServices)
        {
            using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                if (left != TimeSpan.MaxValue)
                    limit.CancelAfter(left > MaxTimerWait ? MaxTimerWait : left);
                return stopServices(chosen, limit.Token);
            }
        }

        static readonly TimeSpan MaxTimerWait = TimeSpan.FromMilliseconds(int.MaxValue);

        /// <summary>
        /// Numbers the steps of one clean for req.Progress: the services step, then each chosen area the first time it
        /// runs (later trim-and-flush passes reuse the area, so they are not counted again). Cleaning thread only.
        /// </summary>
        sealed class ProgressReporter
        {
            readonly Action<CleanProgress> report;
            readonly int total;
            Areas seen;
            int step;

            public ProgressReporter(CleanRequest req)
            {
                report = req.Progress;
                if ((req.Areas & Areas.Services) != 0) total++;
                foreach (var kv in AreaNames.Known)
                    if ((req.Areas & kv.Value) != 0) total++;
            }

            public void Starting(Areas area)
            {
                if (report == null || (seen & area) != 0) return;
                seen |= area;
                step++;
                // The window's progress line must never stop or fail a clean.
                try { report(new CleanProgress { Step = step, Total = total, Area = area }); }
                catch (Exception) { }
            }
        }

        /// <summary>The time limit for automatic cleans (the app's threshold and interval cleans); manual ones have none.</summary>
        public static readonly TimeSpan AutoCleanDeadline = TimeSpan.FromSeconds(120);

        /// <summary>
        /// How long one drive's flush may take before the clean goes on without it. A sleeping external disk can report
        /// Fixed and block in FlushFileBuffers for a long time.
        /// </summary>
        public static readonly TimeSpan MaxFlushWait = TimeSpan.FromSeconds(15);

        /// <summary>"not run: stopped" or "not run: time limit reached".</summary>
        internal static string NotRunMessage(CleanStop why)
        {
            return "not run: " + StopReason(why);
        }

        internal static string StopReason(CleanStop why)
        {
            return why == CleanStop.TimedOut ? "time limit reached" : "stopped";
        }

        static void MarkNotRun(AreaResult r, CleanStop why)
        {
            r.Skipped = true;
            r.NotRun = true;
            r.Message = NotRunMessage(why);
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
            RunAreas(areas, report, maxPasses, inUse, run, clock, () => CleanStop.None);
        }

        /// <summary>
        /// As above, checking stop before each area and each extra pass. Once it says stop, every area left is added
        /// as not run (Skipped, NotRun, "not run: why"); an area already under way finishes, as its system call cannot
        /// be interrupted.
        /// </summary>
        internal static void RunAreas(Areas areas, CleanReport report, int maxPasses, Func<ulong> inUse, Action<AreaResult> run,
            Func<TimeSpan> clock, Func<CleanStop> stop)
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
                CleanStop stopNow = stop();
                if (stopNow != CleanStop.None)
                {
                    MarkNotRun(r, stopNow);
                    report.Results.Add(r);
                    continue;
                }
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
                    report.Passes = RepeatPasses(report.Before.TotalPhys, passStart, freedBetween, maxPasses, inUse, run, trim, flush, clock, stop);
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
        /// Each pass's time is added to the pass-1 trim and flush results. A stop before a pass that would have run is
        /// noted on the trim result ("pass 2 not run: stopped").
        /// </summary>
        private static int RepeatPasses(ulong total, ulong passStart, long freedBetween, int maxPasses, Func<ulong> inUse,
            Action<AreaResult> run, AreaResult trimResult, AreaResult flushResult, Func<TimeSpan> clock, Func<CleanStop> stop)
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
                CleanStop stopNow = stop();
                if (stopNow != CleanStop.None)
                {
                    trimResult.Message = Note(trimResult.Message, "pass " + (passes + 1) + " " + NotRunMessage(stopNow));
                    return passes;
                }
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

        private static void Run(Areas area, Version os, AreaResult r, CleanReport report, CleanStopper stop)
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
                case Areas.VolumeCache: FlushVolumes(r, stop); break;
            }
        }

        /// <summary>
        /// FlushFileBuffers on a volume handle writes every open file's cached data on that volume
        /// (documented; needs administrator rights). Drives that refuse are reported, not fatal. Each drive's flush may
        /// take MaxFlushWait (or the time the clean has left, if less); the stop condition is checked between drives.
        /// </summary>
        private static void FlushVolumes(AreaResult r, CleanStopper stop)
        {
            var drives = new List<KeyValuePair<string, bool>>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                drives.Add(new KeyValuePair<string, bool>(d.Name.Substring(0, 2), d.IsReady)); // "C:", ready
            }
            FlushVolumes(r, drives, FlushVolume, stop.Check, () =>
            {
                TimeSpan left = stop.Remaining;
                return left < MaxFlushWait ? left : MaxFlushWait;
            });
        }

        /// <summary>Drives whose flush, left to finish by an earlier clean, has not returned yet.</summary>
        static readonly HashSet<string> flushesRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True while a flush of this drive, left to finish by an earlier clean, is still running.</summary>
        internal static bool FlushStillRunning(string letter)
        {
            lock (flushesRunning) return flushesRunning.Contains(letter);
        }

        /// <summary>As below, with no stop and each flush run inline (no time limit).</summary>
        internal static void FlushVolumes(AreaResult r, IEnumerable<KeyValuePair<string, bool>> drives, Func<string, int> flush)
        {
            FlushVolumes(r, drives, flush, () => CleanStop.None, null);
        }

        /// <summary>
        /// Flushes each fixed drive (letter, ready) with flush, which returns 0 or a Win32 error. Tests pass fakes.
        /// A drive that is not ready (locked, offline, no media) is skipped, not flushed and not a failure. Some drives
        /// refusing makes the area partial; all of them refusing makes it fail. stop is checked before each drive; once
        /// it says stop, the drives left are noted as not flushed. With flushLimit set, each flush runs on a thread of
        /// its own and is left to finish when it takes longer ("E: still writing; left to finish"), which counts like a
        /// refusal, since the flush was not confirmed.
        /// </summary>
        internal static void FlushVolumes(AreaResult r, IEnumerable<KeyValuePair<string, bool>> drives, Func<string, int> flush,
            Func<CleanStop> stop, Func<TimeSpan> flushLimit)
        {
            var flushed = new List<string>();
            var failed = new List<string>();
            var notReady = new List<string>();
            var notRun = new List<string>();
            foreach (var d in drives)
            {
                string letter = d.Key;
                CleanStop stopNow = stop();
                if (stopNow != CleanStop.None) { notRun.Add(letter + " not flushed: " + StopReason(stopNow)); continue; }
                if (!d.Value) { notReady.Add(letter + " not ready"); continue; }
                int err;
                if (flushLimit == null) err = flush(letter);
                else
                {
                    // Read once: the deadline can pass between the stop check and here, and a zero wait would start a
                    // thread only to give up on it and call a healthy drive hung.
                    TimeSpan limit = flushLimit();
                    if (limit <= TimeSpan.Zero) { notRun.Add(letter + " not flushed: " + StopReason(CleanStop.TimedOut)); continue; }
                    // A drive still hung from an earlier clean gets no second thread: each one holds a volume handle,
                    // and a long-running watch or app would pile them up while the disk sleeps.
                    lock (flushesRunning)
                    {
                        if (!flushesRunning.Add(letter)) { failed.Add(letter + " still writing from an earlier clean"); continue; }
                    }
                    bool hung;
                    try
                    {
                        err = RunWithTimeout(() =>
                        {
                            try { return flush(letter); }
                            finally { lock (flushesRunning) flushesRunning.Remove(letter); }
                        }, limit, out hung);
                    }
                    catch (Exception)
                    {
                        lock (flushesRunning) flushesRunning.Remove(letter); // the thread could not start
                        throw;
                    }
                    if (hung) { failed.Add(letter + " still writing; left to finish"); continue; }
                }
                if (err == 0) flushed.Add(letter);
                else failed.Add(letter + " " + new Win32Exception(err).Message.TrimEnd('.').ToLowerInvariant());
                if (err != 0 && r.Win32Error == 0) r.Win32Error = err;
            }
            if (flushed.Count == 0 && failed.Count == 0)
            {
                if (notRun.Count > 0)
                {
                    var left = new List<string>(notRun);
                    left.AddRange(notReady);
                    Skip(r, "no drive flushed; " + string.Join("; ", left));
                }
                else Skip(r, notReady.Count == 0 ? "no fixed drives found" : "no fixed drive ready; " + string.Join("; ", notReady));
                return;
            }
            r.Ok = flushed.Count > 0;
            r.Partial = r.Ok && failed.Count > 0;
            var notes = new List<string>(failed);
            notes.AddRange(notReady);
            notes.AddRange(notRun);
            r.Message = SummariseVolumes(flushed, notes);
            if (r.Ok && !r.Partial) r.Win32Error = 0;
        }

        /// <summary>
        /// Runs work on a background thread of its own and waits up to limit. On time it returns work's result (or
        /// throws its exception); late, it sets timedOut and returns 0, and the work is left to finish by itself.
        /// That is the price of not blocking the clean on a drive that will not answer; FlushVolumes keeps it to one
        /// thread per hung drive across cleans. The thread holds its own volume handle and closes it when the call
        /// returns; as a background thread it never keeps the process alive. The event is not disposed here when the
        /// work is late, because the thread still sets it.
        /// </summary>
        internal static int RunWithTimeout(Func<int> work, TimeSpan limit, out bool timedOut)
        {
            if (limit < TimeSpan.Zero) limit = TimeSpan.Zero;
            if (limit > MaxTimerWait) limit = MaxTimerWait;
            var done = new ManualResetEvent(false);
            int result = 0;
            Exception error = null;
            var worker = new Thread(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            worker.IsBackground = true;
            worker.Name = "RamOptimiser drive flush";
            worker.Start();
            if (!done.WaitOne(limit))
            {
                timedOut = true;
                return 0;
            }
            done.Close();
            timedOut = false;
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result;
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
        /// <summary>Private bytes: memory only these processes use. Unlike the working set, a clean does not lower it.</summary>
        public ulong PrivateBytes;
    }

    /// <summary>How top ranks apps: by private bytes (the default for ramopt top) or by working set.</summary>
    public enum AppSort { Private, WorkingSet }

    public static class AppMemoryList
    {
        /// <summary>The n apps holding the most RAM right now. Needs no elevation; processes it cannot read are skipped.</summary>
        public static List<AppMemory> Top(int n)
        {
            return Top(n, AppSort.WorkingSet);
        }

        /// <summary>The n apps using the most memory by the chosen figure, private bytes included. Needs no elevation.</summary>
        public static List<AppMemory> Top(int n, AppSort by)
        {
            return Aggregate(ProcessTrimmer.Gather(), n, by);
        }

        /// <summary>
        /// Groups by name (case-insensitive), adding up processes, working sets and private bytes; largest first by the
        /// chosen figure, ties by name; Idle and empty names left out; capped at n. Pure.
        /// </summary>
        public static List<AppMemory> Aggregate(IEnumerable<ProcessSample> samples, int n, AppSort by)
        {
            var map = new Dictionary<string, AppMemory>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessSample p in samples)
            {
                if (p == null || string.IsNullOrEmpty(p.Name) || p.Name == "Idle") continue;
                AppMemory a;
                if (!map.TryGetValue(p.Name, out a)) map[p.Name] = a = new AppMemory { Name = p.Name };
                a.Processes++;
                a.Bytes += p.WorkingSet;
                a.PrivateBytes += p.Private;
            }
            var list = new List<AppMemory>(map.Values);
            list.Sort((x, y) =>
            {
                ulong xv = by == AppSort.Private ? x.PrivateBytes : x.Bytes;
                ulong yv = by == AppSort.Private ? y.PrivateBytes : y.Bytes;
                return xv != yv ? yv.CompareTo(xv) : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
            if (n >= 0 && list.Count > n) list.RemoveRange(n, list.Count - n);
            return list;
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

    /// <summary>Names other processes chose (exe names), made safe to show on one line in the app and the terminal.</summary>
    public static class SafeText
    {
        /// <summary>Shown for a name with nothing visible left.</summary>
        public const string NoName = "(no name)";

        /// <summary>
        /// A name as shown: Visible, trimmed, and cut to max characters ending in ellipsis without splitting a
        /// surrogate pair; NoName when nothing is left. Plain text for a TextBlock or a terminal, never markup. Pure.
        /// </summary>
        public static string Name(string raw, int max, string ellipsis)
        {
            string name = Visible(raw).Trim();
            if (name.Length == 0) return NoName;
            if (name.Length <= max) return name;
            int keep = Math.Max(0, max - ellipsis.Length);
            if (keep > 0 && char.IsHighSurrogate(name[keep - 1])) keep--;
            return name.Substring(0, keep).TrimEnd() + ellipsis;
        }

        /// <summary>
        /// raw without control characters (terminal escape sequences start with one), format characters (bidi
        /// overrides, zero-width marks, and the supplementary ones such as tag characters), line and paragraph
        /// separators and unpaired surrogates. Walks code points, so a surrogate pair is judged as one character.
        /// Null gives "". Pure.
        /// </summary>
        public static string Visible(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new System.Text.StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                int width;
                UnicodeCategory cat = CategoryAt(raw, i, out width);
                if (!Hidden(cat)) sb.Append(raw, i, width);
                i += width;
            }
            return sb.ToString();
        }

        /// <summary>
        /// One safe line of free text (an error message, an activity row): CR, LF, tab and the Unicode line and
        /// paragraph separators become spaces, so words either side stay apart; other control and format characters
        /// (bidi overrides, zero-width marks) are dropped by category, lone surrogates are dropped, and the text is cut
        /// at max characters without splitting a pair. Null gives "". It lives here rather than in the app's
        /// ActivityLog so that CleanLog, which ramopt.exe compiles too, can clean its messages the same way. Pure.
        /// </summary>
        public static string Line(string text, int max)
        {
            if (text == null) return "";
            var sb = new System.Text.StringBuilder(Math.Min(text.Length, max));
            for (int i = 0; i < text.Length && sb.Length < max; i++)
            {
                char ch = text[i];
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) continue;
                    UnicodeCategory pair = CharUnicodeInfo.GetUnicodeCategory(text, i);
                    i++;
                    if (pair == UnicodeCategory.Format || pair == UnicodeCategory.Control) continue;
                    if (sb.Length + 2 > max) break;
                    sb.Append(ch).Append(text[i]);
                    continue;
                }
                if (char.IsLowSurrogate(ch)) continue;
                UnicodeCategory cat = char.GetUnicodeCategory(ch);
                if (ch == '\r' || ch == '\n' || ch == '\t' || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator)
                    sb.Append(' ');
                else if (cat != UnicodeCategory.Control && cat != UnicodeCategory.Format)
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The category of the code point at i. A surrogate pair is read as one character (width 2); a surrogate
        /// without its partner reads Surrogate (width 1).
        /// </summary>
        public static UnicodeCategory CategoryAt(string s, int i, out int width)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                width = 2;
                return CharUnicodeInfo.GetUnicodeCategory(s, i);
            }
            width = 1;
            return char.GetUnicodeCategory(c);
        }

        static bool Hidden(UnicodeCategory cat)
        {
            return cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.Surrogate
                || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator;
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
                case CleanAdvice.Mixed: return "Windows is not short of memory. A clean frees some now, and apps read part of it back.";
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
            return ForCondition(s.Condition);
        }

        /// <summary>Windows' own signal alone: low means a clean helps, high means it is not needed, in between is mixed.</summary>
        public static CleanAdvice ForCondition(MemoryCondition c)
        {
            switch (c)
            {
                case MemoryCondition.Low: return CleanAdvice.WillHelp;
                case MemoryCondition.High: return CleanAdvice.NotNeeded;
                case MemoryCondition.Neutral: return CleanAdvice.Mixed;
                default: return CleanAdvice.None;
            }
        }
    }
}
