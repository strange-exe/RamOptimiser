// The fatal and exit paths (audit run-4 hardening 13, 14, 16; record 21): the bounded service restore the crash
// handler runs, Exit's restore surviving a teardown that throws, tray handler faults routed to the fatal path,
// and the test runner's refusal to run elevated. No real service is touched: the lock tests pass their own
// bodies, and the deadline test uses a service name that is not installed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using RamOptimiser.App;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class FatalPathTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        public static int Run()
        {
            BoundedRestore();
            ExitRestore();
            TrayFaults();
            ElevatedRunner();
            return failed;
        }

        // ---------------------------------------------------------------- hardening 13: bounded restore

        static void BoundedRestore()
        {
            // Lock free: the body runs while the service lock is held, and the lock is released afterwards.
            bool heldInside = false;
            List<ServiceAction> done = ServiceTrimmer.TryLocked(ServiceTrimmer.Operation, TimeSpan.FromSeconds(1), () =>
            {
                heldInside = Monitor.IsEntered(ServiceTrimmer.Operation);
                return new List<ServiceAction> { new ServiceAction { Name = "x" } };
            });
            Check("bounded restore: a free lock runs the restore under it", done != null && done.Count == 1 && heldInside);
            Check("bounded restore: the lock is released afterwards", !Monitor.IsEntered(ServiceTrimmer.Operation)
                && FreeOnOtherThread(ServiceTrimmer.Operation));

            // Lock held by another thread (a Power Clean stuck on a stop): give up after the wait, run nothing.
            using (var holder = new LockHolder(ServiceTrimmer.Operation))
            {
                bool ran = false;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                done = ServiceTrimmer.TryLocked(ServiceTrimmer.Operation, TimeSpan.FromMilliseconds(200), () =>
                {
                    ran = true;
                    return new List<ServiceAction>();
                });
                clock.Stop();
                Check("bounded restore: a held lock gives up and runs nothing", done == null && !ran);
                Check("bounded restore: gives up after about the wait, not later", clock.Elapsed >= TimeSpan.FromMilliseconds(150)
                    && clock.Elapsed < TimeSpan.FromSeconds(3), clock.ElapsedMilliseconds + " ms");

                // Nothing stopped this run: no wait on the lock at all, and nothing to report.
                clock = System.Diagnostics.Stopwatch.StartNew();
                List<ServiceAction> none = ServiceTrimmer.TryRestoreBounded(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
                clock.Stop();
                Check("bounded restore: nothing stopped, no wait", ServiceTrimmer.StoppedThisSession().Count == 0
                    && none != null && none.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(1), clock.ElapsedMilliseconds + " ms");
            }

            // The time budget is spent: no service is opened, so its name stays recorded for the next elevated start.
            done = ServiceTrimmer.RestoreLocked(new List<string> { "RamOptimiserTestNoSuchService" }, () => TimeSpan.Zero);
            Check("bounded restore: an exhausted budget starts nothing", done.Count == 0, done.Count + " action(s)");
            done = ServiceTrimmer.RestoreLocked(new List<string>(), null);
            Check("bounded restore: no names, no actions", done.Count == 0);

            BoundedRestoreWiring();
            EmergencyDeadline();
        }

        /// <summary>A fake clock the fake services below spend: each wait costs the whole time it is given.</summary>
        sealed class FakeClock { public TimeSpan Now; }

        /// <summary>
        /// A service still stopping that stops only at the very end of its wait, takes StartCost to start, and then
        /// never reaches Running: every wait it is given is spent in full, so the waits add up as slowly as they can.
        /// </summary>
        sealed class SlowService : ServiceTrimmer.IServiceControl
        {
            readonly FakeClock clock;
            readonly List<TimeSpan> waits;
            public TimeSpan StartCost;
            public System.ServiceProcess.ServiceControllerStatus Status { get; set; }

            public SlowService(FakeClock clock, List<TimeSpan> waits)
            {
                this.clock = clock;
                this.waits = waits;
                Status = System.ServiceProcess.ServiceControllerStatus.StopPending;
            }

            public void Start() { clock.Now += StartCost; Status = System.ServiceProcess.ServiceControllerStatus.StartPending; }
            public void Continue() { }

            public void WaitForStatus(System.ServiceProcess.ServiceControllerStatus want, TimeSpan timeout)
            {
                waits.Add(timeout);
                if (timeout > TimeSpan.Zero) clock.Now += timeout;
                if (want == System.ServiceProcess.ServiceControllerStatus.Stopped && Status == System.ServiceProcess.ServiceControllerStatus.StopPending)
                {
                    Status = want;
                    return;
                }
                if (Status != want) throw new System.ServiceProcess.TimeoutException("timed out");
            }
        }

        /// <summary>
        /// The emergency restore's waits all come out of one deadline, re-read before each wait: a service that is
        /// still stopping and then never starts cannot take StopTimeout twice, and Start's own time counts too.
        /// </summary>
        static void EmergencyDeadline()
        {
            foreach (TimeSpan startCost in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3) })
            {
                TimeSpan budget = PowerCleanRecovery.EmergencyBudget;
                var clock = new FakeClock();
                var waits = new List<TimeSpan>();
                var opened = new List<string>();
                var names = new List<string> { "RamOptimiserTestA", "RamOptimiserTestB", "RamOptimiserTestC" };
                List<ServiceAction> done = ServiceTrimmer.RestoreLocked(names, () => budget - clock.Now, n =>
                {
                    opened.Add(n);
                    return new SlowService(clock, waits) { StartCost = startCost };
                });
                TimeSpan asked = TimeSpan.Zero;
                foreach (TimeSpan w in waits) asked += w;
                string trace = "start " + startCost.TotalSeconds + " s: waits " + string.Join(",", waits.ConvertAll(w => w.TotalSeconds.ToString()).ToArray())
                    + "; spent " + clock.Now.TotalSeconds + " s; opened " + opened.Count;
                Check("emergency deadline: the time spent stays within the budget (start " + startCost.TotalSeconds + " s)",
                    clock.Now <= budget, trace);
                Check("emergency deadline: no wait is longer than the time left (start " + startCost.TotalSeconds + " s)",
                    asked + TimeSpan.FromTicks(startCost.Ticks * opened.Count) <= budget && waits.TrueForAll(w => w >= TimeSpan.Zero), trace);
                Check("emergency deadline: services not reached stay recorded (start " + startCost.TotalSeconds + " s)",
                    opened.Count == 1 && done.Count == 1 && done[0].Failed && !done[0].Started, trace);
            }
        }

        /// <summary>
        /// TryRestoreBounded itself with names to restore, over its own lock and a fake restore (no service): the
        /// lock wait is lockWait, not the whole budget, and the budget's clock includes the lock wait.
        /// </summary>
        static void BoundedRestoreWiring()
        {
            var gate = new object();
            var names = new List<string> { "RamOptimiserTestNoSuchService" };
            bool ran = false;
            using (new LockHolder(gate))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                List<ServiceAction> done = ServiceTrimmer.TryRestoreBounded(gate, () => names, TimeSpan.FromMilliseconds(200),
                    TimeSpan.FromSeconds(6), (n, left) => { ran = true; return new List<ServiceAction>(); });
                clock.Stop();
                Check("bounded restore: a held lock is waited for lockWait, not the budget", done == null && !ran
                    && clock.Elapsed < TimeSpan.FromSeconds(3), clock.ElapsedMilliseconds + " ms");
            }

            // The lock comes after about 500 ms: the time left the restore sees has already lost that wait.
            TimeSpan seen = TimeSpan.MaxValue;
            List<string> given = null;
            using (var holder = new LockHolder(gate))
            {
                var later = new Thread(() => { Thread.Sleep(500); holder.Dispose(); }) { IsBackground = true };
                later.Start();
                List<ServiceAction> done = ServiceTrimmer.TryRestoreBounded(gate, () => names, TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(20), (n, left) => { given = n; seen = left(); return new List<ServiceAction>(); });
                later.Join();
                Check("bounded restore: the restore runs once the lock comes", done != null && given != null && given.Count == 1);
            }
            Check("bounded restore: the budget's clock includes the lock wait", seen <= TimeSpan.FromSeconds(20) - TimeSpan.FromMilliseconds(400),
                seen.TotalMilliseconds + " ms left");
        }

        static bool FreeOnOtherThread(object gate)
        {
            bool free = false;
            var t = new Thread(() =>
            {
                if (Monitor.TryEnter(gate, TimeSpan.FromSeconds(1))) { free = true; Monitor.Exit(gate); }
            });
            t.Start();
            t.Join();
            return free;
        }

        /// <summary>Holds a lock on its own thread until disposed, like a clean stuck inside a stop.</summary>
        sealed class LockHolder : IDisposable
        {
            readonly ManualResetEvent taken = new ManualResetEvent(false), release = new ManualResetEvent(false);
            readonly Thread thread;

            public LockHolder(object gate)
            {
                thread = new Thread(() =>
                {
                    lock (gate)
                    {
                        taken.Set();
                        release.WaitOne(TimeSpan.FromSeconds(30));
                    }
                }) { IsBackground = true };
                thread.Start();
                taken.WaitOne(TimeSpan.FromSeconds(10));
            }

            int disposed;

            /// <summary>Releases the lock; a second call (another thread already released it) does nothing.</summary>
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                release.Set();
                thread.Join(TimeSpan.FromSeconds(10));
                taken.Close();
                release.Close();
            }
        }

        // ---------------------------------------------------------------- hardening 16: Exit's restore

        static void ExitRestore()
        {
            int restores = 0;
            Exception escaped = null;
            try
            {
                PowerCleanRecovery.TeardownThenRestore(() => { throw new InvalidOperationException("tray dispose failed"); },
                    () => restores++);
            }
            catch (Exception ex) { escaped = ex; }
            Check("exit restore: starts even when a teardown step throws", restores == 1, restores + " restore(s)");
            Check("exit restore: the teardown fault still reaches the fatal handler", escaped is InvalidOperationException);

            restores = 0;
            string order = "";
            PowerCleanRecovery.TeardownThenRestore(() => order += "teardown,", () => { order += "restore,"; restores++; });
            Check("exit restore: after a clean teardown, once, last", restores == 1 && order == "teardown,restore,", order);

            // Each teardown step runs on its own: one that throws early (the hotkey, say) no longer skips the later
            // ones (auto-clean's timer above all); the first fault still reaches the caller once all have run.
            order = "";
            restores = 0;
            escaped = null;
            var first = new InvalidOperationException("hotkey failed");
            try
            {
                PowerCleanRecovery.TeardownThenRestore(() => PowerCleanRecovery.RunSteps(
                    () => order += "a,",
                    () => { order += "b,"; throw first; },
                    () => order += "service,",
                    () => { order += "c,"; throw new InvalidOperationException("tray failed"); },
                    () => order += "window,"),
                    () => { order += "restore,"; restores++; });
            }
            catch (Exception ex) { escaped = ex; }
            Check("exit teardown: a throwing step does not skip the later ones", order == "a,b,service,c,window,restore,", order);
            Check("exit teardown: the first fault reaches the caller, after the restore started",
                ReferenceEquals(escaped, first) && restores == 1, escaped == null ? "nothing escaped" : escaped.Message);
            order = "";
            PowerCleanRecovery.RunSteps(() => order += "a,", () => order += "b,");
            Check("exit teardown: steps that work run once each, in order", order == "a,b,", order);

            ShellWiring();
        }

        // ---------------------------------------------------------------- wiring of the fatal and exit paths

        static string Source(params string[] parts)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.Combine("..", "src"));
            foreach (string part in parts) path = Path.Combine(path, part);
            path = Path.GetFullPath(path);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        /// <summary>The body of the method whose declaration starts with signature (up to its closing brace at that indent).</summary>
        static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            int end = source.IndexOf("\n        }", at, StringComparison.Ordinal);
            return end < 0 ? source.Substring(at) : source.Substring(at, end - at);
        }

        static void ShellWiring()
        {
            string raise = Body(Source("App", "TrayController.cs"), "void Raise(EventHandler h)");
            int guard = raise.IndexOf("FaultRoute.Guard(", StringComparison.Ordinal), call = raise.IndexOf("h(this, EventArgs.Empty)", StringComparison.Ordinal);
            int calls = raise.Length - raise.Replace("h(this, EventArgs.Empty)", "").Length;
            Check("wiring: TrayController.Raise runs the handler only inside FaultRoute.Guard", guard >= 0 && call > guard
                && calls == "h(this, EventArgs.Empty)".Length && raise.Contains("AppMain.FailOnUiThread("), raise);

            string shell = Source("App", "AppShell.cs");
            string exit = Body(shell, "public void Exit()");
            int teardown = exit.IndexOf("PowerCleanRecovery.TeardownThenRestore(", StringComparison.Ordinal);
            int steps = exit.IndexOf("PowerCleanRecovery.RunSteps(", StringComparison.Ordinal);
            int dispose = exit.IndexOf("service.Dispose()", StringComparison.Ordinal);
            int restore = exit.IndexOf("RestoreServicesOnExit(", StringComparison.Ordinal);
            Check("wiring: Exit tears down through TeardownThenRestore, step by step, then restores",
                teardown >= 0 && steps > teardown && dispose > steps && restore > dispose, exit);

            string emergency = Body(shell, "public void EmergencyCleanup()");
            int bounded = emergency.IndexOf("ServiceTrimmer.TryRestoreBounded(PowerCleanRecovery.EmergencyLockWait, PowerCleanRecovery.EmergencyBudget)",
                StringComparison.Ordinal);
            int svc = emergency.IndexOf("service.Dispose()", StringComparison.Ordinal), inst = emergency.IndexOf("instance.Dispose()", StringComparison.Ordinal);
            Check("wiring: EmergencyCleanup restores bounded, after auto-clean stops and before the instance lock goes",
                bounded >= 0 && svc >= 0 && svc < bounded && inst > bounded, emergency);
        }

        // ---------------------------------------------------------------- hardening 14: tray faults

        static void TrayFaults()
        {
            Exception routed = null;
            var thrown = new InvalidOperationException("handler broke");
            bool completed = FaultRoute.Guard(() => { throw thrown; }, ex => routed = ex);
            Check("tray fault: a handler exception goes to the fatal route, not WinForms", !completed && ReferenceEquals(routed, thrown));

            routed = null;
            int ran = 0;
            completed = FaultRoute.Guard(() => ran++, ex => routed = ex);
            Check("tray fault: a handler that works runs once and is not routed", completed && ran == 1 && routed == null);

            // The fatal route's own Exit may throw (TeardownThenRestore rethrows a teardown fault after starting the
            // restore). Escaping there would reach WinForms' dialog from a tray handler, or the AppDomain handler
            // from the dispatcher one, so it is logged and dropped.
            Exception logged = null, escaped = null;
            var exitFault = new InvalidOperationException("window close failed");
            try { FaultRoute.Quietly(() => { throw exitFault; }, ex => logged = ex); }
            catch (Exception ex) { escaped = ex; }
            Check("fatal route: a throwing exit is logged, not rethrown", escaped == null && ReferenceEquals(logged, exitFault),
                escaped == null ? null : escaped.Message);
            escaped = null;
            try { FaultRoute.Quietly(() => { throw exitFault; }, ex => { throw new InvalidOperationException("log failed"); }); }
            catch (Exception ex) { escaped = ex; }
            Check("fatal route: a throwing log does not escape either", escaped == null, escaped == null ? null : escaped.Message);
            ran = 0;
            logged = null;
            FaultRoute.Quietly(() => ran++, ex => logged = ex);
            Check("fatal route: a working exit runs once, nothing logged", ran == 1 && logged == null);

            string main = File.ReadAllText(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine("..", Path.Combine("src", Path.Combine("App", "AppMain.cs"))))));
            int at = main.IndexOf("static void FailOnUiThread(", StringComparison.Ordinal);
            int end = at < 0 ? -1 : main.IndexOf("\n        }", at, StringComparison.Ordinal);
            string body = end < 0 ? "" : main.Substring(at, end - at);
            int quiet = body.IndexOf("FaultRoute.Quietly(", StringComparison.Ordinal), exit = body.IndexOf("shell.Exit()", StringComparison.Ordinal);
            Check("fatal route: FailOnUiThread runs Exit inside FaultRoute.Quietly", quiet >= 0 && exit > quiet, body);
        }

        // ---------------------------------------------------------------- record 21: elevated test runner

        static void ElevatedRunner()
        {
            Check("runner: refuses an elevated token", TestMain.RefuseElevated(true, null));
            Check("runner: the override must be exactly 1", TestMain.RefuseElevated(true, "0") && TestMain.RefuseElevated(true, "true")
                && TestMain.RefuseElevated(true, " 1") && TestMain.RefuseElevated(true, ""));
            Check("runner: RAMOPT_TESTS_ALLOW_ELEVATED=1 lets an elevated run go on", !TestMain.RefuseElevated(true, "1"));
            Check("runner: a non-elevated run is never refused", !TestMain.RefuseElevated(false, null) && !TestMain.RefuseElevated(false, "1"));
            Check("runner: refusal exit code is 3", TestMain.ExitElevated == 3);
            Check("runner: override variable name", TestMain.AllowElevatedVariable == "RAMOPT_TESTS_ALLOW_ELEVATED");
        }
    }
}
