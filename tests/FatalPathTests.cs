// The fatal and exit paths (audit run-4 hardening 13, 14, 16; record 21): the bounded service restore the crash
// handler runs, Exit's restore surviving a teardown that throws, tray handler faults routed to the fatal path,
// and the test runner's refusal to run elevated. No real service is touched: the lock tests pass their own
// bodies, and the deadline test uses a service name that is not installed.
using System;
using System.Collections.Generic;
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

            public void Dispose()
            {
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
