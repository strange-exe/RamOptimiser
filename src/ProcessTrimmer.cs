using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace RamOptimiser
{
    /// <summary>One process as top and per-process trim see it: pid, name and two memory figures. Never a path or command line.</summary>
    public sealed class ProcessSample
    {
        public int Id;
        public string Name;
        /// <summary>Working set: RAM the process holds now, shared pages included.</summary>
        public ulong WorkingSet;
        /// <summary>Private bytes: memory only this process uses (Process.PrivateMemorySize64).</summary>
        public ulong Private;
    }

    /// <summary>Why per-process trim leaves a process alone; None means it is trimmed.</summary>
    public enum SpareReason { None, Self, System, Foreground, Excluded }

    /// <summary>
    /// Per-process trim (ramopt clean/watch --trim each): trims app by app instead of emptying every working set at
    /// once, and spares the app in front, this process, the system's own processes and a user list. It opens each
    /// process with only PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION and never enables SeDebugPrivilege,
    /// so protected and SYSTEM processes refuse, which is expected. That, and leaving the Memory Compression store
    /// alone, is why it frees less than the system-wide trim. The plan, the summary and the list parsing are pure.
    /// </summary>
    public static class ProcessTrimmer
    {
        /// <summary>The most names --spare (or a later settings key) may hold; the rest are dropped.</summary>
        public const int MaxExclusions = 32;
        /// <summary>The longest name accepted in the list.</summary>
        public const int MaxExclusionLength = 100;
        /// <summary>The stop condition (Ctrl+C, the time limit) is checked after this many processes.</summary>
        public const int StopCheckEvery = 32;

        /// <summary>Always spared: the kernel's own processes, which a trim can't help and should not touch.</summary>
        static readonly string[] SystemNames = { "Idle", "System", "Registry", "Secure System", "Memory Compression" };

        /// <summary>The name without a trailing ".exe" (any case), trimmed. Null stays empty.</summary>
        static string Bare(string name)
        {
            string n = (name ?? "").Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4).Trim();
            return n;
        }

        static bool IsSystem(ProcessSample p)
        {
            if (p.Id == 0 || p.Id == 4) return true;
            foreach (string s in SystemNames)
                if (string.Equals(p.Name, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// What happens to each process, in the given order: Self (selfPid), System (pid 0 or 4, or a kernel name),
        /// Foreground (every process named like the one with foregroundPid, since browsers and games run many; nothing
        /// when that pid is 0 or not listed), Excluded (named in exclusions, ".exe" ignored on both sides, any case),
        /// else None. Pure.
        /// </summary>
        public static List<KeyValuePair<ProcessSample, SpareReason>> Plan(IList<ProcessSample> procs, ICollection<string> exclusions,
            int foregroundPid, int selfPid)
        {
            string foreground = null;
            if (foregroundPid > 0)
                foreach (ProcessSample p in procs)
                    if (p.Id == foregroundPid && !string.IsNullOrEmpty(Bare(p.Name))) { foreground = Bare(p.Name); break; }
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exclusions != null)
                foreach (string e in exclusions) { string b = Bare(e); if (b.Length > 0) excluded.Add(b); }

            var plan = new List<KeyValuePair<ProcessSample, SpareReason>>(procs.Count);
            foreach (ProcessSample p in procs)
            {
                string name = Bare(p.Name);
                SpareReason why = SpareReason.None;
                if (p.Id == selfPid) why = SpareReason.Self;
                else if (IsSystem(p)) why = SpareReason.System;
                else if (foreground != null && string.Equals(name, foreground, StringComparison.OrdinalIgnoreCase)) why = SpareReason.Foreground;
                else if (excluded.Contains(name)) why = SpareReason.Excluded;
                plan.Add(new KeyValuePair<ProcessSample, SpareReason>(p, why));
            }
            return plan;
        }

        /// <summary>
        /// The workingsets row: "trimmed 142 processes; spared msedge (in front) and obs64 (your list); 9 refused".
        /// Groups with no names and a zero refusal count are left out. Pure.
        /// </summary>
        public static string Summarise(int trimmed, IList<string> sparedForeground, IList<string> sparedExcluded, int refused)
        {
            string s = "trimmed " + trimmed + (trimmed == 1 ? " process" : " processes");
            var groups = new List<string>();
            if (sparedForeground != null && sparedForeground.Count > 0) groups.Add(string.Join(", ", sparedForeground) + " (in front)");
            if (sparedExcluded != null && sparedExcluded.Count > 0) groups.Add(string.Join(", ", sparedExcluded) + " (your list)");
            if (groups.Count > 0) s += "; spared " + string.Join(" and ", groups);
            if (refused > 0) s += "; " + refused + " refused";
            return s;
        }

        /// <summary>
        /// True when one list entry is an acceptable exe name: 1-100 of letters, digits, space and . _ ( ) + -, so no
        /// path separator, wildcard or quote.
        /// </summary>
        static bool ValidEntry(string entry)
        {
            if (entry.Length < 1 || entry.Length > MaxExclusionLength) return false;
            foreach (char c in entry)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == ' ' || c == '.' || c == '_' || c == '(' || c == ')' || c == '+' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// A comma-separated list of exe names, each trimmed, without ".exe": invalid entries dropped, duplicates
        /// (any case, with or without ".exe") removed, at most MaxExclusions kept, first ones first. Pure.
        /// </summary>
        public static List<string> ParseExclusions(string list)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(list)) return names;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in list.Split(','))
            {
                string entry = raw.Trim();
                if (!ValidEntry(entry)) continue;
                string bare = Bare(entry);
                if (bare.Length == 0 || !seen.Add(bare)) continue;
                if (names.Count >= MaxExclusions) break;
                names.Add(bare);
            }
            return names;
        }

        /// <summary>The first non-empty entry ParseExclusions would drop as invalid, or null. Pure.</summary>
        public static string FirstInvalidExclusion(string list)
        {
            if (string.IsNullOrEmpty(list)) return null;
            foreach (string raw in list.Split(','))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;
                if (!ValidEntry(entry) || Bare(entry).Length == 0) return entry;
            }
            return null;
        }

        /// <summary>Distinct non-empty entries in a list (any case, ".exe" ignored), counting invalid ones too. Pure.</summary>
        public static int CountEntries(string list)
        {
            if (string.IsNullOrEmpty(list)) return 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in list.Split(','))
            {
                string bare = Bare(raw);
                if (bare.Length > 0) seen.Add(bare);
            }
            return seen.Count;
        }

        /// <summary>Every process's pid, name and memory figures. Each Process is disposed; one that exited meanwhile is skipped.</summary>
        public static List<ProcessSample> Gather()
        {
            var list = new List<ProcessSample>();
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        list.Add(new ProcessSample
                        {
                            Id = p.Id,
                            Name = p.ProcessName,
                            WorkingSet = (ulong)Math.Max(0L, p.WorkingSet64),
                            Private = (ulong)Math.Max(0L, p.PrivateMemorySize64)
                        });
                    }
                    catch (InvalidOperationException) { } // exited meanwhile
                    catch (Win32Exception) { }            // figures could not be read
                }
            }
            return list;
        }

        /// <summary>The pid that owns the foreground window, or 0 when there is none (session 0, a locked screen).</summary>
        public static int ForegroundPid()
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            int pid;
            if (Native.GetWindowThreadProcessId(hwnd, out pid) == 0) return 0;
            return pid;
        }

        static int SelfPid()
        {
            using (Process self = Process.GetCurrentProcess()) return self.Id;
        }

        /// <summary>Empties one process's working set. False when it can't be opened or refuses (expected for protected ones).</summary>
        static bool TrimOne(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return Native.EmptyWorkingSet(h); }
            finally { Native.CloseHandle(h); }
        }

        /// <summary>The workingsets area in per-process mode, stopping on req.Cancel only.</summary>
        public static void TrimEach(CleanRequest req, AreaResult r)
        {
            TrimEach(req, r, () => req.Cancel.IsCancellationRequested ? CleanStop.Cancelled : CleanStop.None);
        }

        /// <summary>The workingsets area in per-process mode, on the live process list, with the clean's stop condition.</summary>
        internal static void TrimEach(CleanRequest req, AreaResult r, Func<CleanStop> stop)
        {
            int fg = req.SpareForeground ? ForegroundPid() : 0;
            TrimEach(req, r, Gather(), fg, SelfPid(), TrimOne, stop);
        }

        /// <summary>
        /// Plans, then trims each process the plan does not spare with trim (true: trimmed; false or a throw: refused).
        /// stop is checked every StopCheckEvery processes; the ones left are noted ("38 not trimmed: stopped"). The
        /// area is ok when at least one process was trimmed or none was tried, and failed only when every attempt was
        /// refused. Refusals are expected and never make the area partial. Tests pass fakes.
        /// </summary>
        internal static void TrimEach(CleanRequest req, AreaResult r, IList<ProcessSample> procs, int foregroundPid, int selfPid,
            Func<int, bool> trim, Func<CleanStop> stop)
        {
            List<KeyValuePair<ProcessSample, SpareReason>> plan =
                Plan(procs, req.TrimExclusions, req.SpareForeground ? foregroundPid : 0, selfPid);
            var sparedForeground = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var sparedExcluded = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var todo = new List<int>();
            foreach (var kv in plan)
            {
                if (kv.Value == SpareReason.None) todo.Add(kv.Key.Id);
                else if (kv.Value == SpareReason.Foreground) sparedForeground.Add(Bare(kv.Key.Name));
                else if (kv.Value == SpareReason.Excluded) sparedExcluded.Add(Bare(kv.Key.Name));
            }
            int trimmed = 0, refused = 0, notRun = 0;
            CleanStop stopped = CleanStop.None;
            for (int i = 0; i < todo.Count; i++)
            {
                if (i > 0 && i % StopCheckEvery == 0)
                {
                    stopped = stop();
                    if (stopped != CleanStop.None) { notRun = todo.Count - i; break; }
                }
                bool ok;
                try { ok = trim(todo[i]); }
                catch (Exception) { ok = false; }
                if (ok) trimmed++; else refused++;
            }
            r.Ok = trimmed > 0 || refused == 0;
            r.Message = Summarise(trimmed, new List<string>(sparedForeground), new List<string>(sparedExcluded), refused);
            if (notRun > 0) r.Message += "; " + notRun + " not trimmed: " + Cleaner.StopReason(stopped);
        }
    }
}
