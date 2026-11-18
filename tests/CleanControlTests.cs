// Package 2B: a clean that can be stopped (Ctrl+C, an exit) or limited in time (E9), and the cross-process clean
// lock that keeps ramopt clean, ramopt watch and the app from cleaning at the same time (E22). Engine and CLI only;
// the app's use of them is package 3A. Pure code, fakes and test-only mutex names: nothing here cleans memory.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using RamOptimiser.App;

namespace RamOptimiser.Tests
{
    public static class CleanControlTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        const ulong GB = 1UL << 30;

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

        public static int Run()
        {
            StopperTests();
            CancelTests();
            DeadlineTests();
            VolumeTests();
            ServiceTests();
            LockTests();
            CleanLockSeamTests();
            CliTests();
            WatchTests();
            WordingTests();
            return failed;
        }

        // ---------------------------------------------------------------- helpers

        static MemorySnapshot Snap(uint load)
        {
            return new MemorySnapshot { TotalPhys = 16 * GB, AvailablePhys = 8 * GB, LoadPercent = load };
        }

        static string Names(List<Areas> areas)
        {
            var names = new List<string>();
            foreach (Areas a in areas) names.Add(a == Areas.Services ? "services" : AreaNames.NameOf(a));
            return string.Join(",", names.ToArray());
        }

        static List<Areas> NotRun(CleanReport r)
        {
            var list = new List<Areas>();
            foreach (AreaResult a in r.Results) if (a.NotRun) list.Add(a.Area);
            return list;
        }

        /// <summary>The seam with no lock and a service stopper that must not be reached.</summary>
        static CleanReport Seam(CleanRequest req, Func<TimeSpan> clock, Func<MemorySnapshot> snap, Action<CleanReport, AreaResult, CleanStopper> run)
        {
            return Cleaner.Clean(req, clock, snap, () => 0UL, run, null,
                (names, t) => { throw new InvalidOperationException("services reached"); });
        }

        static IEnumerable<KeyValuePair<string, bool>> Drives(params string[] letters)
        {
            foreach (string l in letters) yield return new KeyValuePair<string, bool>(l, true);
        }

        // ---------------------------------------------------------------- the stop condition

        static void StopperTests()
        {
            TimeSpan now = TimeSpan.Zero;
            var cts = new CancellationTokenSource();
            var s = new CleanStopper(cts.Token, TimeSpan.FromSeconds(10), () => now);
            Check("stopper: nothing set is none", s.Check() == CleanStop.None && s.Stopped == CleanStop.None);
            Check("stopper: remaining counts down", s.Remaining == TimeSpan.FromSeconds(10), s.Remaining.ToString());
            now = TimeSpan.FromSeconds(10);
            Check("stopper: the deadline itself is past", s.Check() == CleanStop.TimedOut);
            cts.Cancel();
            Check("deadline: the first cause wins (time limit, then cancel)", s.Check() == CleanStop.TimedOut && s.Stopped == CleanStop.TimedOut);
            Check("stopper: remaining never negative", s.Remaining == TimeSpan.Zero, s.Remaining.ToString());

            now = TimeSpan.Zero;
            var cts2 = new CancellationTokenSource();
            var s2 = new CleanStopper(cts2.Token, TimeSpan.FromSeconds(10), () => now);
            cts2.Cancel();
            Check("stopper: a cancel stops", s2.Check() == CleanStop.Cancelled);
            now = TimeSpan.FromSeconds(60);
            Check("deadline: the first cause wins (cancel, then time limit)", s2.Check() == CleanStop.Cancelled);

            var never = new CleanStopper(default(CancellationToken), null, () => TimeSpan.FromDays(1));
            Check("stopper: no token and no deadline never stops", never.Check() == CleanStop.None && never.Remaining == TimeSpan.MaxValue);
        }

        // ---------------------------------------------------------------- cancel

        static void CancelTests()
        {
            // A token set before the start: no area runs, both snapshots are still taken.
            var cts = new CancellationTokenSource();
            cts.Cancel();
            int snaps = 0;
            var ran = new List<Areas>();
            CleanReport r = Seam(new CleanRequest { Cancel = cts.Token }, () => TimeSpan.Zero,
                () => { snaps++; return Snap(80); }, (rep, a, stop) => { ran.Add(a.Area); a.Ok = true; });
            bool allNotRun = r.Results.Count == 6;
            foreach (AreaResult a in r.Results) allNotRun &= a.NotRun && a.Skipped && a.Message == "not run: stopped";
            Check("cancel: a token set before start runs no area but takes both snapshots", ran.Count == 0 && snaps == 2
                && r.Before != null && r.After != null && allNotRun && r.Stopped == CleanStop.Cancelled && !r.Completed,
                ran.Count + " ran, " + snaps + " snapshots, " + r.Stopped);
            Check("cancel: not-run areas leave AllOk alone", r.AllOk);
            Check("cancel: counts no area run", r.AreasRun == 0 && r.AreasTotal == 6, r.AreasRun + " of " + r.AreasTotal);

            // Set during area 2: area 3 and later do not run.
            cts = new CancellationTokenSource();
            ran.Clear();
            r = Seam(new CleanRequest { Cancel = cts.Token }, () => TimeSpan.Zero, () => Snap(80),
                (rep, a, stop) => { ran.Add(a.Area); a.Ok = true; if (a.Area == Areas.WorkingSets) cts.Cancel(); });
            Check("cancel: set during area 2 stops before area 3", Names(ran) == "combine,workingsets"
                && Names(NotRun(r)) == "filecache,modified,standby-low,registry" && r.Stopped == CleanStop.Cancelled,
                Names(ran) + " / " + Names(NotRun(r)));
            Check("cancel: counts the areas run", r.AreasRun == 2 && r.AreasTotal == 6, r.AreasRun + " of " + r.AreasTotal);

            // Extra passes: a cancel during the first flush stops pass 2. The control run without it takes 3 passes.
            CleanReport control = PassRun(false);
            CleanReport cut = PassRun(true);
            Check("cancel: extra passes run without a stop (control)", control.Passes == 3 && control.Completed, control.Passes + " passes");
            AreaResult trim = cut.Results.Find(a => a.Area == Areas.WorkingSets);
            Check("cancel: extra passes stop", cut.Passes == 1 && cut.Stopped == CleanStop.Cancelled && trim != null
                && trim.Message == "pass 2 not run: stopped", cut.Passes + " passes; " + (trim == null ? "" : trim.Message));

            // A clean with nothing stopping it is complete and says nothing about stops.
            r = Seam(new CleanRequest { Areas = Areas.RegistryCache }, () => TimeSpan.Zero, () => Snap(80), (rep, a, stop) => { a.Ok = true; });
            Check("cancel: an unstopped clean is complete", r.Completed && r.Stopped == CleanStop.None && NotRun(r).Count == 0);

            // Services are a step too: a stop before them stops none.
            cts = new CancellationTokenSource();
            cts.Cancel();
            bool reached = false;
            r = Cleaner.Clean(new CleanRequest { Areas = Areas.Services | Areas.RegistryCache, Services = new[] { ServiceCatalog.Rules[0].Name }, Cancel = cts.Token },
                () => TimeSpan.Zero, () => Snap(80), () => 0UL, (rep, a, stop) => { a.Ok = true; }, null,
                (names, t) => { reached = true; return new List<ServiceAction>(); });
            Check("cancel: a stop before the services stops none", !reached && Names(NotRun(r)) == "services,registry", Names(NotRun(r)));
            Check("cancel: the services row is not counted as an area", r.AreasTotal == 1, r.AreasTotal.ToString());
        }

        /// <summary>Trim and flush where each run frees 1 GB of 16 GB, so passes continue; optionally cancels during the first flush.</summary>
        static CleanReport PassRun(bool cancel)
        {
            var cts = new CancellationTokenSource();
            ulong mem = 10 * GB;
            int flushes = 0;
            return Cleaner.Clean(new CleanRequest { Areas = Areas.WorkingSets | Areas.ModifiedList, Cancel = cts.Token },
                () => TimeSpan.Zero, () => Snap(80), () => mem, (rep, a, stop) =>
                {
                    a.Ok = true;
                    mem -= GB;
                    if (a.Area == Areas.ModifiedList && ++flushes == 1 && cancel) cts.Cancel();
                }, null, (names, t) => { throw new InvalidOperationException("services reached"); });
        }

        // ---------------------------------------------------------------- deadline

        static void DeadlineTests()
        {
            TimeSpan now = TimeSpan.Zero;
            var ran = new List<Areas>();
            CleanReport r = Seam(new CleanRequest { Deadline = TimeSpan.FromSeconds(120) }, () => now, () => Snap(80),
                (rep, a, stop) => { ran.Add(a.Area); a.Ok = true; now += TimeSpan.FromSeconds(50); });
            Check("deadline: fake clock past the deadline reports TimedOut", r.Stopped == CleanStop.TimedOut && ran.Count == 3
                && Names(NotRun(r)) == "modified,standby-low,registry", r.Stopped + " after " + Names(ran));
            bool messages = true;
            foreach (AreaResult a in r.Results) if (a.NotRun) messages &= a.Message == "not run: time limit reached";
            Check("deadline: not-run rows say the time limit was reached", messages);
            Check("deadline: the report keeps the limit and the time", r.Deadline == TimeSpan.FromSeconds(120)
                && r.Duration == TimeSpan.FromSeconds(150), r.Duration.ToString());

            // A deadline that is never reached changes nothing.
            now = TimeSpan.Zero;
            r = Seam(new CleanRequest { Deadline = TimeSpan.FromSeconds(120) }, () => now, () => Snap(80),
                (rep, a, stop) => { a.Ok = true; now += TimeSpan.FromSeconds(1); });
            Check("deadline: a clean inside the limit completes", r.Completed && NotRun(r).Count == 0);

            // The service step gets a token that fires at the deadline, so its waits end too.
            bool fired = false;
            Cleaner.Clean(new CleanRequest { Areas = Areas.Services, Services = new[] { ServiceCatalog.Rules[0].Name }, Deadline = TimeSpan.FromMilliseconds(150) },
                () => TimeSpan.Zero, () => Snap(80), () => 0UL, (rep, a, stop) => { a.Ok = true; }, null,
                (names, t) => { fired = t.WaitHandle.WaitOne(5000); return new List<ServiceAction>(); });
            Check("deadline: the services step is told when the limit passes", fired);
        }

        // ---------------------------------------------------------------- volumes

        static void VolumeTests()
        {
            int checks = 0;
            var flushed = new List<string>();
            var vol = new AreaResult { Area = Areas.VolumeCache };
            Cleaner.FlushVolumes(vol, Drives("C:", "D:", "E:"), l => { flushed.Add(l); return 0; },
                () => ++checks > 1 ? CleanStop.Cancelled : CleanStop.None, () => TimeSpan.FromSeconds(5));
            Check("volumes: a stop between drives lists the rest as not flushed", flushed.Count == 1 && flushed[0] == "C:"
                && vol.Message == "flushed 1 of 3 drives; D: not flushed: stopped; E: not flushed: stopped" && vol.Outcome == AreaOutcome.Ok,
                vol.Outcome + " " + vol.Message);

            checks = 0;
            vol = new AreaResult { Area = Areas.VolumeCache };
            Cleaner.FlushVolumes(vol, Drives("C:", "D:"), l => 0, () => ++checks > 1 ? CleanStop.TimedOut : CleanStop.None, null);
            Check("volumes: a time limit names itself", vol.Message == "flushed 1 of 2 drives; D: not flushed: time limit reached", vol.Message);

            // A sleeping disk that blocks in FlushFileBuffers: left to finish after the limit, and the clean goes on.
            using (var hang = new ManualResetEvent(false))
            {
                vol = new AreaResult { Area = Areas.VolumeCache };
                Stopwatch sw = Stopwatch.StartNew();
                Cleaner.FlushVolumes(vol, Drives("C:", "E:"), l => { if (l == "E:") hang.WaitOne(); return 0; },
                    () => CleanStop.None, () => TimeSpan.FromMilliseconds(100));
                sw.Stop();
                Check("volumes: a hung flush is left to finish", vol.Message != null && vol.Message.Contains("E: still writing; left to finish")
                    && vol.Outcome == AreaOutcome.Partial && sw.Elapsed < TimeSpan.FromSeconds(5), vol.Outcome + " " + vol.Message + " in " + sw.Elapsed);
                hang.Set(); // lets the worker thread end
            }

            bool timedOut;
            Check("run with timeout: returns the result", Cleaner.RunWithTimeout(() => 21, TimeSpan.FromSeconds(5), out timedOut) == 21 && !timedOut);
            Throws<InvalidOperationException>("run with timeout: passes the error on",
                () => Cleaner.RunWithTimeout(() => { throw new InvalidOperationException("x"); }, TimeSpan.FromSeconds(5), out timedOut));
            int threadId = Thread.CurrentThread.ManagedThreadId, workerId = threadId;
            Cleaner.RunWithTimeout(() => { workerId = Thread.CurrentThread.ManagedThreadId; return 0; }, TimeSpan.FromSeconds(5), out timedOut);
            Check("run with timeout: the work runs on its own thread", workerId != threadId);

            // The old overload keeps its behaviour: no stop, no thread.
            vol = new AreaResult { Area = Areas.VolumeCache };
            int callerThread = Thread.CurrentThread.ManagedThreadId, flushThread = -1;
            Cleaner.FlushVolumes(vol, Drives("C:"), l => { flushThread = Thread.CurrentThread.ManagedThreadId; return 0; });
            Check("volumes: the old overload flushes inline", vol.Outcome == AreaOutcome.Ok && flushThread == callerThread);

            // A drive still hung from an earlier clean gets no second thread (each holds a volume handle).
            using (var hang = new ManualResetEvent(false))
            {
                int starts = 0;
                Func<string, int> blocked = l => { Interlocked.Increment(ref starts); hang.WaitOne(); return 0; };
                var v1 = new AreaResult { Area = Areas.VolumeCache };
                var v2 = new AreaResult { Area = Areas.VolumeCache };
                Cleaner.FlushVolumes(v1, Drives("Q:"), blocked, () => CleanStop.None, () => TimeSpan.FromMilliseconds(50));
                Cleaner.FlushVolumes(v2, Drives("Q:"), blocked, () => CleanStop.None, () => TimeSpan.FromMilliseconds(50));
                Thread.Sleep(50); // a second worker, if one were started, would have counted by now
                Check("volumes: a hung drive is not flushed again by the next clean", Volatile.Read(ref starts) == 1
                    && v2.Message != null && v2.Message.Contains("Q: still writing from an earlier clean"), starts + " starts; " + v2.Message);
                hang.Set();
                Stopwatch wait = Stopwatch.StartNew();
                while (Cleaner.FlushStillRunning("Q:") && wait.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
                var v3 = new AreaResult { Area = Areas.VolumeCache };
                Cleaner.FlushVolumes(v3, Drives("Q:"), l => 0, () => CleanStop.None, () => TimeSpan.FromSeconds(5));
                Check("volumes: once the old flush ends the drive is flushed again", v3.Outcome == AreaOutcome.Ok, v3.Outcome + " " + v3.Message);
            }

            // The deadline passing between the stop check and the limit: no thread started for a zero wait.
            bool called = false;
            vol = new AreaResult { Area = Areas.VolumeCache };
            Cleaner.FlushVolumes(vol, Drives("C:"), l => { called = true; return 0; }, () => CleanStop.None, () => TimeSpan.Zero);
            Check("volumes: no time left means not flushed, not a hang", !called && vol.Outcome != AreaOutcome.Failed && vol.Message != null
                && vol.Message.Contains("C: not flushed: time limit reached") && !vol.Message.Contains("still writing"), vol.Outcome + " " + vol.Message);
        }

        // ---------------------------------------------------------------- services

        static void ServiceTests()
        {
            var plan = new List<ServiceAction>
            {
                new ServiceAction { Name = "A", Label = "Alpha" },
                new ServiceAction { Name = "S", Label = "Skipped", Outcome = ServiceOutcome.SkippedNotRunning, Message = "not running" },
                new ServiceAction { Name = "B", Label = "Beta" },
                new ServiceAction { Name = "C", Label = "Gamma" },
            };
            var cts = new CancellationTokenSource();
            var stopCalls = new List<string>();
            var accepted = new List<string>();
            TimeSpan slice = TimeSpan.Zero;
            ServiceTrimmer.StopPlanned(plan, n => { stopCalls.Add(n); return 0; }, (n, t) =>
            {
                slice = t;
                if (n == "B") { cts.Cancel(); return false; }
                return true;
            }, cts.Token, accepted.Add);
            Check("services: a stop between services keeps the stopped list", string.Join(",", accepted.ToArray()) == "A,B"
                && string.Join(",", stopCalls.ToArray()) == "A,B", string.Join(",", accepted.ToArray()) + " / " + string.Join(",", stopCalls.ToArray()));
            Check("services: the first one stopped", plan[0].Stopped && !plan[0].Failed && plan[0].Message == null);
            Check("services: a planned skip is left alone", !plan[1].Stopped && !plan[1].Failed && plan[1].Message == "not running");
            Check("services: a cancelled wait is reported and kept", !plan[2].Stopped && plan[2].Failed
                && plan[2].Message == "still stopping; clean was stopped", plan[2].Message);
            Check("services: the rest are left running", !plan[3].Stopped && !plan[3].Failed && plan[3].Message == "left running: clean was stopped", plan[3].Message);
            Check("services: waits in short slices", slice == ServiceTrimmer.StopSlice && ServiceTrimmer.StopSlice == TimeSpan.FromMilliseconds(250), slice.ToString());

            // With no cancel, the slices add up to the old 10 s wait and its message.
            // A fake clock: each wait takes exactly the slice it was given.
            plan = new List<ServiceAction> { new ServiceAction { Name = "A", Label = "Alpha" } };
            int waits = 0;
            TimeSpan fake = TimeSpan.Zero;
            accepted.Clear();
            ServiceTrimmer.StopPlanned(plan, n => 0, (n, t) => { waits++; fake += t; return false; }, CancellationToken.None, accepted.Add,
                () => fake);
            Check("services: a slow stop still times out at 10 s", plan[0].Failed && plan[0].Message == "still stopping after 10 s"
                && waits == 40 && accepted.Count == 1, waits + " waits; " + plan[0].Message);

            // Each real wait overruns its slice (WaitForStatus sleeps 250 ms after its check): the time that passed
            // counts, not the slices asked for, so the whole wait stays near 10 s instead of 20 s.
            plan = new List<ServiceAction> { new ServiceAction { Name = "A", Label = "Alpha" } };
            waits = 0;
            fake = TimeSpan.Zero;
            TimeSpan longest = TimeSpan.Zero;
            ServiceTrimmer.StopPlanned(plan, n => 0, (n, t) =>
            {
                waits++;
                if (t > longest) longest = t;
                fake += t + t;
                return false;
            }, CancellationToken.None, n => { }, () => fake);
            Check("services: the stop wait counts real time, not slices", plan[0].Failed && fake <= ServiceTrimmer.StopTimeout + TimeSpan.FromMilliseconds(500)
                && waits <= 21 && longest <= ServiceTrimmer.StopSlice, waits + " waits, " + fake + " taken");

            // A refused stop is not recorded as stopped.
            plan = new List<ServiceAction> { new ServiceAction { Name = "A", Label = "Alpha" } };
            accepted.Clear();
            ServiceTrimmer.StopPlanned(plan, n => 5, (n, t) => true, CancellationToken.None, accepted.Add);
            Check("services: a refused stop is a failure and not recorded", plan[0].Failed && accepted.Count == 0, plan[0].Message);

            // The wait throwing (service gone) is reported, and the stop stays recorded for Restore.
            plan = new List<ServiceAction> { new ServiceAction { Name = "A", Label = "Alpha" } };
            accepted.Clear();
            ServiceTrimmer.StopPlanned(plan, n => 0, (n, t) => { throw new InvalidOperationException("gone"); }, CancellationToken.None, accepted.Add);
            Check("services: a wait that throws is a failure, still recorded", plan[0].Failed && plan[0].Message == "gone" && accepted.Count == 1, plan[0].Message);
        }

        // ---------------------------------------------------------------- the lock itself

        static MutexSecurity UserOwned()
        {
            SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
            var sec = new MutexSecurity();
            sec.SetOwner(me);
            sec.AddAccessRule(new MutexAccessRule(me, MutexRights.FullControl, AccessControlType.Allow));
            return sec;
        }

        static string Me() { return WindowsIdentity.GetCurrent().User.Value; }

        static string TestName() { return @"Local\RamOptimiser-Test-" + Guid.NewGuid().ToString("N"); }

        /// <summary>TryAcquire on a thread of its own (a mutex is re-entrant on its owner's thread).</summary>
        static LockState OnOtherThread(string name, Func<string, bool> trusted, TimeSpan wait, out string note, bool keep)
        {
            LockState state = LockState.Unavailable;
            string n = null;
            CleanLock held = null;
            var t = new Thread(() =>
            {
                state = CleanLock.TryAcquire(name, UserOwned(), trusted, wait, out held, out n);
                if (held != null && !keep) held.Dispose();
            });
            t.Start();
            t.Join();
            note = n;
            if (keep) lastAbandoned = held;
            return state;
        }

        static CleanLock lastAbandoned;

        static void LockTests()
        {
            string sidAdmins = "S-1-5-32-544", sidSystem = "S-1-5-18";
            Check("lock: Administrators and SYSTEM are trusted owners", CleanLock.TrustedOwner(sidAdmins) && CleanLock.TrustedOwner(sidSystem)
                && !CleanLock.TrustedOwner(Me()) && !CleanLock.TrustedOwner(null));
            Check("lock: created new is trusted", CleanLock.Classify(true, null) == LockState.Held && CleanLock.Classify(true, Me()) == LockState.Held);
            Check("lock: an owner other than Administrators or SYSTEM is unavailable", CleanLock.Classify(false, Me()) == LockState.Unavailable);
            Check("lock: an Administrators-owned lock may be waited for", CleanLock.Classify(false, sidAdmins) == LockState.Held
                && CleanLock.Classify(false, sidSystem) == LockState.Held);
            Check("lock: unreadable owner is unavailable", CleanLock.Classify(false, null) == LockState.Unavailable);
            Check("lock: the real name is in the Global namespace", CleanLock.Name == @"Global\RamOptimiser-Clean");

            string me = Me();
            Func<string, bool> trustMe = sid => sid == me;
            string name = TestName(), note;
            CleanLock held;
            LockState first = CleanLock.TryAcquire(name, UserOwned(), trustMe, TimeSpan.Zero, out held, out note);
            Check("lock: the first taker holds it", first == LockState.Held && held != null && note == null, first + " " + note);
            LockState second = OnOtherThread(name, trustMe, TimeSpan.Zero, out note, false);
            Check("lock: a second taker is busy", second == LockState.Busy && note == null, second + " " + note);
            // Mutation guard: with the real owner rule, a lock the current user created is a squatter, not a clean.
            LockState squatted = OnOtherThread(name, CleanLock.TrustedOwner, TimeSpan.Zero, out note, false);
            Check("lock: a name held by an untrusted owner is unavailable, not busy", squatted == LockState.Unavailable && note != null, squatted + " " + note);
            if (held != null) held.Dispose();
            LockState third = OnOtherThread(name, trustMe, TimeSpan.Zero, out note, false);
            Check("lock: a released lock can be taken", third == LockState.Held, third.ToString());

            // A holder whose thread ends without releasing it: the next taker gets it, with a note.
            string abandonedName = TestName();
            LockState a = OnOtherThread(abandonedName, trustMe, TimeSpan.Zero, out note, true);
            CleanLock b;
            LockState after = CleanLock.TryAcquire(abandonedName, UserOwned(), trustMe, TimeSpan.FromSeconds(2), out b, out note);
            Check("lock: an abandoned lock is held with a note", a == LockState.Held && after == LockState.Held
                && note == "the last clean ended without finishing", a + " " + after + " " + note);
            if (b != null) b.Dispose();
            if (lastAbandoned != null) lastAbandoned.Dispose(); // not this thread's: Dispose only closes it

            // A name taken by another kind of object cannot be a lock: run unguarded with a note.
            string eventName = TestName();
            using (var squat = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
            {
                CleanLock none;
                LockState wrongType = CleanLock.TryAcquire(eventName, UserOwned(), trustMe, TimeSpan.Zero, out none, out note);
                Check("lock: a name used by another kind of object is unavailable", wrongType == LockState.Unavailable && none == null
                    && note != null, wrongType + " " + note);
            }

            // Dispose twice is harmless.
            CleanLock twice;
            CleanLock.TryAcquire(TestName(), UserOwned(), trustMe, TimeSpan.Zero, out twice, out note);
            bool ok = true;
            try { twice.Dispose(); twice.Dispose(); } catch (Exception) { ok = false; }
            Check("lock: dispose twice is harmless", ok);

            // The real descriptor: owned by Administrators, full control for Administrators and SYSTEM only.
            MutexSecurity real = CleanLock.AdminOnly();
            IdentityReference owner = real.GetOwner(typeof(SecurityIdentifier));
            Check("lock: the real lock is owned by Administrators", owner != null && owner.Value == sidAdmins, owner == null ? "null" : owner.Value);
            var allowed = new List<string>();
            bool onlyFullAllow = true;
            foreach (MutexAccessRule rule in real.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || rule.MutexRights != MutexRights.FullControl) onlyFullAllow = false;
                allowed.Add(rule.IdentityReference.Value);
            }
            allowed.Sort(StringComparer.Ordinal);
            Check("lock: only Administrators and SYSTEM may open the real lock", onlyFullAllow
                && string.Join(",", allowed.ToArray()) == sidSystem + "," + sidAdmins, string.Join(",", allowed.ToArray()));
        }

        // ---------------------------------------------------------------- the lock inside a clean

        sealed class FakeLock : IDisposable
        {
            public List<string> Events;
            public void Dispose() { Events.Add("release"); }
        }

        static void CleanLockSeamTests()
        {
            // Busy: no area runs, one snapshot serves as both, and nothing runs under the lock.
            int snaps = 0, runs = 0, under = 0;
            TimeSpan askedWait = TimeSpan.MinValue;
            CleanReport r = Cleaner.Clean(new CleanRequest { LockWait = TimeSpan.FromSeconds(2), UnderLock = rep => under++ },
                () => TimeSpan.Zero, () => { snaps++; return Snap(90); }, () => 0UL, (rep, a, stop) => { runs++; },
                (TimeSpan wait, out IDisposable h, out string n) => { askedWait = wait; h = null; n = null; return LockState.Busy; },
                (names, t) => { throw new InvalidOperationException("services reached"); });
            Check("clean: busy runs no area and keeps before equal to after", runs == 0 && snaps == 1 && r.Before != null
                && ReferenceEquals(r.Before, r.After) && r.Stopped == CleanStop.Busy && r.Results.Count == 0 && under == 0 && !r.Completed,
                runs + " runs, " + snaps + " snapshots, " + r.Stopped);
            Check("clean: the lock wait comes from the request", askedWait == TimeSpan.FromSeconds(2), askedWait.ToString());

            // Held: the after snapshot and UnderLock come before the release.
            var events = new List<string>();
            r = Cleaner.Clean(new CleanRequest { Areas = Areas.RegistryCache, UnderLock = rep => events.Add("under") },
                () => TimeSpan.Zero, () => { events.Add("snapshot"); return Snap(80); }, () => 0UL, (rep, a, stop) => { events.Add("area"); a.Ok = true; },
                (TimeSpan wait, out IDisposable h, out string n) => { events.Add("take"); h = new FakeLock { Events = events }; n = null; return LockState.Held; },
                (names, t) => { throw new InvalidOperationException("services reached"); });
            Check("clean: the lock covers the snapshots and UnderLock", string.Join(",", events.ToArray()) == "take,snapshot,area,snapshot,under,release"
                && r.Completed, string.Join(",", events.ToArray()));

            // Released even when the clean throws.
            events.Clear();
            bool threw = false;
            try
            {
                Cleaner.Clean(new CleanRequest { Areas = Areas.RegistryCache }, () => TimeSpan.Zero,
                    () => { throw new System.ComponentModel.Win32Exception(5); }, () => 0UL, (rep, a, stop) => { },
                    (TimeSpan wait, out IDisposable h, out string n) => { h = new FakeLock { Events = events }; n = null; return LockState.Held; },
                    (names, t) => new List<ServiceAction>());
            }
            catch (System.ComponentModel.Win32Exception) { threw = true; }
            Check("clean: the lock is released when the clean throws", threw && events.Contains("release"), string.Join(",", events.ToArray()));

            // Unavailable: the clean runs unguarded and keeps the reason; an abandoned lock's note is kept too.
            r = Cleaner.Clean(new CleanRequest { Areas = Areas.RegistryCache }, () => TimeSpan.Zero, () => Snap(80), () => 0UL,
                (rep, a, stop) => { a.Ok = true; },
                (TimeSpan wait, out IDisposable h, out string n) => { h = null; n = "could not check"; return LockState.Unavailable; },
                (names, t) => new List<ServiceAction>());
            Check("clean: an unavailable lock runs unguarded with a note", r.Completed && r.Results.Count == 1 && r.LockNote == "could not check", r.LockNote);

            // The old overloads' seam takes no lock (the app keeps today's behaviour until package 3A moves it).
            r = Cleaner.Clean(Areas.RegistryCache, null, 1, () => TimeSpan.Zero, () => Snap(80), () => 0UL, (rep, a) => { a.Ok = true; });
            Check("clean: the old seam still works", r.Completed && r.Results.Count == 1 && r.LockNote == null);
        }

        // ---------------------------------------------------------------- CLI

        static CleanReport Stopped(CleanStop why, int ran, int notRun, TimeSpan? deadline)
        {
            var r = new CleanReport { Before = Snap(81), After = Snap(79), Stopped = why, Deadline = deadline };
            int i = 0;
            foreach (var kv in AreaNames.Known)
            {
                if ((Areas.Default & kv.Value) == 0) continue;
                if (i < ran) r.Results.Add(new AreaResult { Area = kv.Value, Ok = true });
                else if (i < ran + notRun) r.Results.Add(new AreaResult { Area = kv.Value, Skipped = true, NotRun = true, Message = Cleaner.NotRunMessage(why) });
                i++;
            }
            return r;
        }

        static void CliTests()
        {
            CleanReport ok = Stopped(CleanStop.None, 6, 0, null);
            CleanReport cancelled = Stopped(CleanStop.Cancelled, 2, 4, null);
            CleanReport timedOut = Stopped(CleanStop.TimedOut, 3, 3, TimeSpan.FromSeconds(120));
            CleanReport busy = new CleanReport { Before = Snap(90), Stopped = CleanStop.Busy };
            busy.After = busy.Before;

            Check("cli: exit code 0 for a complete clean", Program.ExitCodeFor(ok) == Program.ExitOk);
            Check("cli: exit code 2 when stopped", Program.ExitCodeFor(cancelled) == Program.ExitPartial && Program.ExitCodeFor(timedOut) == Program.ExitPartial);
            Check("cli: exit code 5 when busy", Program.ExitCodeFor(busy) == Program.ExitBusy && Program.ExitBusy == 5);

            Check("cli: stop line names the areas not run", Program.StopLine(cancelled)
                == "Stopped after workingsets; not run: filecache, modified, standby-low, registry.", Program.StopLine(cancelled));
            Check("cli: time-limit line counts the areas", Program.StopLine(timedOut)
                == "Hit the 120 s limit after 3 of 6 areas; not run: modified, standby-low, registry.", Program.StopLine(timedOut));
            Check("cli: stop line before any area", Program.StopLine(Stopped(CleanStop.Cancelled, 0, 6, null)).StartsWith("Stopped before any area ran; not run: combine, "));
            Check("cli: no stop line for a complete or busy clean", Program.StopLine(ok) == null && Program.StopLine(busy) == null);

            var outW = new StringWriter();
            var errW = new StringWriter();
            int code = Program.ReportClean(busy, outW, errW);
            Check("cli: busy exits 5 with the message", code == 5 && outW.ToString() == ""
                && errW.ToString().TrimEnd() == "error: another clean is running (ramopt or RamOptimiser). Try again when it finishes.", errW.ToString());

            outW = new StringWriter();
            code = Program.ReportClean(cancelled, outW, new StringWriter());
            string text = outW.ToString();
            Check("cli: a stopped clean prints its report and the stop line", code == 2 && text.Contains("In use: ")
                && text.TrimEnd().EndsWith("Stopped after workingsets; not run: filecache, modified, standby-low, registry."), text);
            Check("cli: not-run rows read 'not run'", text.Contains("  modified     not run" + Environment.NewLine), text);

            outW = new StringWriter();
            ok.LockNote = "the last clean ended without finishing";
            Program.PrintReport(outW, ok);
            Check("cli: the lock note is printed", outW.ToString().TrimEnd().EndsWith("note: the last clean ended without finishing"), outW.ToString());

            // --timeout
            Check("options: --timeout for clean", Options.Parse(new[] { "clean", "--timeout", "120" }).TimeoutSeconds == 120);
            Check("options: --timeout for watch", Options.Parse(new[] { "watch", "--timeout", "30" }).TimeoutSeconds == 30);
            Check("options: no timeout by default", Options.Parse(new[] { "clean" }).TimeoutSeconds == 0);
            Throws<ArgumentException>("options: --timeout 0 rejected", () => Options.Parse(new[] { "clean", "--timeout", "0" }));
            Throws<ArgumentException>("options: --timeout over an hour rejected", () => Options.Parse(new[] { "clean", "--timeout", "3601" }));
            Throws<ArgumentException>("options: --timeout must be a number", () => Options.Parse(new[] { "clean", "--timeout", "2m" }));
            Throws<ArgumentException>("options: --timeout not with status", () => Options.Parse(new[] { "status", "--timeout", "5" }));

            var cts = new CancellationTokenSource();
            CleanRequest req = Program.RequestFor(Options.Parse(new[] { "clean", "--timeout", "90", "--areas", "registry" }), Areas.RegistryCache,
                cts.Token, Program.CliLockWait);
            Check("cli: the request carries areas, limit, token and lock wait", req.Areas == Areas.RegistryCache && req.Deadline == TimeSpan.FromSeconds(90)
                && req.Cancel == cts.Token && req.LockWait == TimeSpan.FromSeconds(2) && req.MaxPasses == Cleaner.DefaultMaxPasses);
            req = Program.RequestFor(Options.Parse(new[] { "clean" }), Areas.Default, cts.Token, TimeSpan.Zero);
            Check("cli: no --timeout means no limit", req.Deadline == null);

            // Ctrl+C: the first press stops at the next safe point; a second one ends the process.
            var said = new StringWriter();
            bool firstKeep = Program.OnCancelKey(cts, said);
            bool secondKeep = Program.OnCancelKey(cts, said);
            Check("cli: the first Ctrl+C stops the clean and keeps the process", firstKeep && cts.IsCancellationRequested
                && said.ToString().TrimEnd() == "Stopping after the current step...", said.ToString());
            Check("cli: a second Ctrl+C ends the process", !secondKeep);

            // Watch waiting between checks: Ctrl+C ends the wait without claiming a step is running.
            var idle = new CancellationTokenSource();
            var idleSaid = new StringWriter();
            bool idleKeep = Program.OnCancelKey(idle, idleSaid, () => false);
            Check("cli: Ctrl+C while idle stops without the step line", idleKeep && idle.IsCancellationRequested && idleSaid.ToString() == "", idleSaid.ToString());
            var busyCts = new CancellationTokenSource();
            var busySaid = new StringWriter();
            Program.OnCancelKey(busyCts, busySaid, () => true);
            Check("cli: Ctrl+C during a watch clean says it stops after the step", busySaid.ToString().TrimEnd() == "Stopping after the current step...", busySaid.ToString());

            // One area: singular, as AppText.StoppedCleanLine says it.
            var single = new CleanReport { Before = Snap(81), After = Snap(80), Stopped = CleanStop.TimedOut, Deadline = TimeSpan.FromSeconds(30) };
            single.Results.Add(new AreaResult { Area = Areas.VolumeCache, Ok = true, Partial = true, Message = "flushed 1 of 2 drives; D: not flushed: time limit reached" });
            Check("cli: time-limit line for one area is singular", Program.StopLine(single) == "Hit the 30 s limit after 1 of 1 area.", Program.StopLine(single));

            string help = Program.HelpText();
            Check("help: documents --timeout", help.Contains("--timeout S"));
            Check("help: documents the busy exit code", help.Contains(Program.ExitBusy + " another clean is running"));
        }

        // ---------------------------------------------------------------- watch

        static void WatchTests()
        {
            int cleans = 0, tick = 0;
            var o = new Options { Command = Command.Watch, ThresholdPercent = 85, IntervalSeconds = 10, CooldownSeconds = 10 };
            var outW = new StringWriter();
            var errW = new StringWriter();
            var t0 = new DateTime(2026, 11, 1, 12, 0, 0);
            var w = new Watcher
            {
                Sample = () => Snap(95),
                Clean = a =>
                {
                    cleans++;
                    MemorySnapshot s = Snap(95);
                    return new CleanReport { Before = s, After = s, Stopped = CleanStop.Busy };
                },
                Elapsed = () => TimeSpan.FromSeconds(tick * 10),
                Now = () => t0.AddSeconds(tick * 10),
                WaitStop = t => ++tick >= 6
            };
            int code = w.Run(o, outW, errW);
            // Counted as futile, the load left at 95% would back off to 2 cleans in 6 checks.
            Check("watch: busy is not an error and not futile", code == Program.ExitOk && cleans == 6 && errW.ToString() == ""
                && outW.ToString().Contains("[12:00:00] another clean is running: skipped this one") && !outW.ToString().Contains("In use:"),
                cleans + " cleans; " + errW + outW);

            tick = 0;
            outW = new StringWriter();
            w.Clean = a => Stopped(CleanStop.TimedOut, 3, 3, TimeSpan.FromSeconds(60));
            w.WaitStop = t => ++tick >= 1;
            w.Run(o, outW, new StringWriter());
            Check("watch: a clean that hit its limit says so", outW.ToString().Contains("Hit the 60 s limit after 3 of 6 areas"), outW.ToString());
        }

        // ---------------------------------------------------------------- app wording (shown by package 3A)

        static void WordingTests()
        {
            var at = new DateTime(2026, 11, 17, 14, 32, 0);
            CultureInfo c = CultureInfo.InvariantCulture;
            Check("wording: a stopped clean", AppText.StoppedCleanLine(at, at, Stopped(CleanStop.Cancelled, 3, 3, null), false, c)
                == "Last clean at 14:32 was stopped after 3 of 6 areas. Load went from 81% to 79%.",
                AppText.StoppedCleanLine(at, at, Stopped(CleanStop.Cancelled, 3, 3, null), false, c));
            Check("wording: an auto-clean that hit its limit", AppText.StoppedCleanLine(at, at, Stopped(CleanStop.TimedOut, 3, 3, TimeSpan.FromSeconds(120)), true, c)
                == "Auto-clean at 14:32 hit its 2-minute limit after 3 of 6 areas. Load went from 81% to 79%.",
                AppText.StoppedCleanLine(at, at, Stopped(CleanStop.TimedOut, 3, 3, TimeSpan.FromSeconds(120)), true, c));
            Check("wording: stopped before any area", AppText.StoppedCleanLine(at, at, Stopped(CleanStop.Cancelled, 0, 6, null), false, c)
                == "Last clean at 14:32 was stopped before any area ran. Load went from 81% to 79%.");
            CleanReport busy = new CleanReport { Before = Snap(90), Stopped = CleanStop.Busy };
            busy.After = busy.Before;
            Check("wording: a skipped clean", AppText.StoppedCleanLine(at, at, busy, false, c)
                == "Last clean at 14:32 was skipped: another RamOptimiser clean was running.");
            Check("wording: a skipped auto-clean", AppText.StoppedCleanLine(at, at, busy, true, c)
                == "Auto-clean at 14:32 was skipped: another RamOptimiser clean was running.");
            Check("wording: a yesterday clean keeps the dated head", AppText.StoppedCleanLine(at, at.AddDays(1), busy, true, c)
                == "Auto-clean yesterday at 14:32 was skipped: another RamOptimiser clean was running.");
            Check("wording: none for a complete clean", AppText.StoppedCleanLine(at, at, Stopped(CleanStop.None, 6, 0, null), false, c) == null
                && AppText.StoppedCleanLine(at, at, null, false, c) == null);
            Check("wording: limits in minutes or seconds", AppText.LimitText(TimeSpan.FromSeconds(120)) == "2-minute"
                && AppText.LimitText(TimeSpan.FromSeconds(60)) == "1-minute" && AppText.LimitText(TimeSpan.FromSeconds(90)) == "90-second"
                && AppText.LimitText(null) == "time");
            Check("wording: the auto-clean limit is 2 minutes", AppText.LimitText(Cleaner.AutoCleanDeadline) == "2-minute");
        }
    }
}
