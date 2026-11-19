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
