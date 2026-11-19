// A launch while the running copy is exiting (it holds the single-instance lock until Power Clean's restore ends,
// with its activation handler already removed). The running copy acknowledges an activation only when a handler
// took it; without an answer the new launch waits, bounded, for the lock and then starts as the first instance.
// Unique test-only names throughout: the app's real lock is never touched.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class RelaunchTests
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
            DecideTests();
            AckObjectTests();
            RunTests();
            WiringTests();
            return failed;
        }

        static string Unique() { return "test-" + Guid.NewGuid().ToString("N"); }

        static readonly TimeSpan Bound = TimeSpan.FromSeconds(60), Retry = TimeSpan.FromMilliseconds(250),
            AckTime = TimeSpan.FromSeconds(3);

        // ---------------------------------------------------------------- the pure decision, on a fake clock

        static void DecideTests()
        {
            Check("bounds: about 3 s for the answer, about 60 s for the lock", SecondLaunch.AckWait == AckTime
                && SecondLaunch.ReleaseWait == Bound && SecondLaunch.RetryEvery > TimeSpan.Zero && SecondLaunch.RetryEvery <= TimeSpan.FromSeconds(1));

            // Acknowledged: exit 0 without touching the lock.
            TimeSpan now = TimeSpan.Zero;
            int acquires = 0, pauses = 0;
            SecondLaunchOutcome o = SecondLaunch.Decide(() => ActivationAnswer.Acknowledged, () => { acquires++; return true; },
                () => now, d => { pauses++; now += d; }, Bound, Retry);
            Check("decide: acknowledged exits without acquiring", o == SecondLaunchOutcome.Activated && acquires == 0 && pauses == 0,
                o + ", " + acquires + " acquires");

            // Unconfirmed (no acknowledgement event, or the signal failed): today's behaviour, exit 0.
            acquires = 0;
            o = SecondLaunch.Decide(() => ActivationAnswer.Unconfirmed, () => { acquires++; return true; }, () => now, d => now += d, Bound, Retry);
            Check("decide: no acknowledgement channel falls back to exit 0", o == SecondLaunchOutcome.Unconfirmed && acquires == 0, o.ToString());

            // The ack wait itself failing (an event that cannot be waited on): today's behaviour, never a crash.
            acquires = 0;
            Exception escaped = null;
            try
            {
                o = SecondLaunch.Decide(() => { throw new UnauthorizedAccessException("ack"); }, () => { acquires++; return true; },
                    () => now, d => now += d, Bound, Retry);
            }
            catch (Exception ex) { escaped = ex; }
            Check("decide: a failing ack wait falls back to exit 0", escaped == null && o == SecondLaunchOutcome.Unconfirmed && acquires == 0,
                escaped == null ? o.ToString() : escaped.GetType().Name);

            // No answer, the lock freed 10 s after the answer wait began: starts as the first instance at once.
            now = TimeSpan.Zero;
            acquires = 0;
            TimeSpan freedAt = AckTime + TimeSpan.FromSeconds(10);
            o = SecondLaunch.Decide(() => { now += AckTime; return ActivationAnswer.NoAnswer; },
                () => { acquires++; return now >= freedAt; }, () => now, d => now += d, Bound, Retry);
            Check("decide: not acknowledged, lock freed later: starts as first", o == SecondLaunchOutcome.BecameFirst, o.ToString());
            Check("decide: takes the lock on the first retry after it is freed", now >= freedAt && now < freedAt + Retry, now.ToString());

            // No answer, the lock is free already: no pause at all.
            now = TimeSpan.Zero;
            pauses = 0;
            o = SecondLaunch.Decide(() => ActivationAnswer.NoAnswer, () => true, () => now, d => { pauses++; now += d; }, Bound, Retry);
            Check("decide: a free lock is taken without waiting", o == SecondLaunchOutcome.BecameFirst && pauses == 0, o + ", " + pauses);

            // No answer, never freed: gives up at the bound, never beyond it; the last try is at the bound.
            now = TimeSpan.Zero;
            acquires = 0;
            TimeSpan lastTry = TimeSpan.MinValue, longest = TimeSpan.Zero;
            o = SecondLaunch.Decide(() => { now += AckTime; return ActivationAnswer.NoAnswer; },
                () => { acquires++; lastTry = now; return false; }, () => now,
                d => { if (d > longest) longest = d; now += d; }, Bound, Retry);
            Check("decide: never freed: gives up", o == SecondLaunchOutcome.StillHeld, o.ToString());
            Check("decide: gives up within the bound", now == AckTime + Bound && lastTry == AckTime + Bound, now + " / " + lastTry);
            Check("decide: retries every interval", acquires == (int)(Bound.Ticks / Retry.Ticks) + 1 && longest == Retry,
                acquires + " tries, longest pause " + longest);

            // A bound that is not a multiple of the retry: the last pause is shortened to land on it.
            now = TimeSpan.Zero;
            TimeSpan odd = TimeSpan.FromMilliseconds(600);
            o = SecondLaunch.Decide(() => ActivationAnswer.NoAnswer, () => false, () => now, d => now += d, odd, Retry);
            Check("decide: the last pause lands on the bound", o == SecondLaunchOutcome.StillHeld && now == odd, now.ToString());

            // The lock's own errors (a squatted name met while retrying) are the caller's to handle.
            escaped = null;
            try
            {
                SecondLaunch.Decide(() => ActivationAnswer.NoAnswer, () => { throw new SingleInstanceNameTakenException(5, "taken"); },
                    () => now, d => now += d, Bound, Retry);
            }
            catch (Exception ex) { escaped = ex; }
            Check("decide: an acquire error reaches the caller", escaped is SingleInstanceNameTakenException,
                escaped == null ? "none" : escaped.GetType().Name);
        }

        // ---------------------------------------------------------------- the acknowledgement event, real objects

        static void AckObjectTests()
        {
            string sid = SingleInstance.CurrentUserSid();
            string id = Unique();
            Check("ack name: next to the activation event", SingleInstance.AckName(id) == SingleInstance.NamePrefix + id + "-ack"
                && SingleInstance.AckName(id) != SingleInstance.EventName(id));

            // A handler is present: the request is acknowledged.
            using (var activated = new ManualResetEvent(false))
            using (var first = new SingleInstance(id))
            {
                first.ActivationRequested += (s, e) => activated.Set();
                using (var second = new SingleInstance(id))
                {
                    var watch = Stopwatch.StartNew();
                    ActivationAnswer a = second.SignalAndWaitForAck(TimeSpan.FromSeconds(3));
                    Check("ack: a copy with a handler acknowledges", a == ActivationAnswer.Acknowledged && watch.ElapsedMilliseconds < 3000,
                        a + " after " + watch.ElapsedMilliseconds + " ms");
                    Check("ack: the handler ran", activated.WaitOne(5000));
                }
                using (SafeWaitHandle ackHandle = GuiNative.OpenEvent(SecurityNative.READ_CONTROL, false, SingleInstance.AckName(id)))
                    Check("ack: the event is owned by the user", !ackHandle.IsInvalid && SingleInstance.OwnerSidOf(ackHandle) == sid,
                        SingleInstance.OwnerSidOf(ackHandle));
            }

            // No handler (the copy is exiting): no answer, and a stale acknowledgement left set does not count.
            string quietId = Unique();
            using (var first = new SingleInstance(quietId))
            {
                using (SafeWaitHandle stale = GuiNative.OpenEvent(GuiNative.EVENT_MODIFY_STATE, false, SingleInstance.AckName(quietId)))
                    Check("ack: stale answer set for the test", !stale.IsInvalid && GuiNative.SetEvent(stale));
                using (var second = new SingleInstance(quietId))
                {
                    var watch = Stopwatch.StartNew();
                    ActivationAnswer a = second.SignalAndWaitForAck(TimeSpan.FromMilliseconds(300));
                    Check("ack: a copy without a handler does not answer", a == ActivationAnswer.NoAnswer && watch.ElapsedMilliseconds >= 250,
                        a + " after " + watch.ElapsedMilliseconds + " ms");
                }
                Check("ack: the first instance does not ask itself", first.SignalAndWaitForAck(TimeSpan.FromMilliseconds(50)) == ActivationAnswer.Unconfirmed);
            }

            // The acknowledgement name squatted (no SYNCHRONIZE): the lock still works, activation still goes through,
            // and the answer is Unconfirmed, so the launch exits as it always did.
            string squatId = Unique();
            using (SafeWaitHandle squat = GuiPlumbingTests.CreateEventWithSddl(SingleInstance.AckName(squatId), "D:(A;;0x2;;;" + sid + ")"))
            using (var activated = new ManualResetEvent(false))
            {
                SingleInstance first = null;
                Exception ex = null;
                try { first = new SingleInstance(squatId); }
                catch (Exception err) { ex = err; }
                try
                {
                    Check("ack squatted: the instance is still made", !squat.IsInvalid && first != null && first.IsFirstInstance,
                        ex == null ? null : ex.GetType().Name);
                    if (first != null)
                    {
                        first.ActivationRequested += (s, e) => activated.Set();
                        using (var second = new SingleInstance(squatId))
                        {
                            ActivationAnswer a = second.SignalAndWaitForAck(TimeSpan.FromMilliseconds(300));
                            Check("ack squatted: unconfirmed", a == ActivationAnswer.Unconfirmed, a.ToString());
                        }
                        Check("ack squatted: activation still delivered", activated.WaitOne(5000));
                    }
                }
                finally { if (first != null) first.Dispose(); }
            }
        }

        // ---------------------------------------------------------------- Run: the decision with real objects

        static void RunTests()
        {
            // The holder has no handler and lets go after 400 ms, as an exiting copy does after its restore.
            string id = Unique();
            var holder = new SingleInstance(id);
            var second = new SingleInstance(id);
            var t = new Thread(() => { Thread.Sleep(400); holder.Dispose(); }) { IsBackground = true };
            int created = 0;
            SingleInstance taken;
            var watch = Stopwatch.StartNew();
            t.Start();
            SecondLaunchOutcome o = SecondLaunch.Run(second, () => { created++; return new SingleInstance(id); },
                TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50), out taken);
            watch.Stop();
            try
            {
                Check("run: an exiting holder hands the lock over", o == SecondLaunchOutcome.BecameFirst && taken != null && taken.IsFirstInstance,
                    o + (taken == null ? ", none" : ""));
                Check("run: soon after the holder lets go", watch.ElapsedMilliseconds < 5000 && created >= 1, watch.ElapsedMilliseconds + " ms");
                Check("run: the asking instance is disposed", !second.SignalFirstInstance());
            }
            finally { if (taken != null) taken.Dispose(); }
            t.Join(2000);

            // A holder that answers: exit at once, the lock is never retried.
            string ackId = Unique();
            using (var keeper = new SingleInstance(ackId))
            {
                keeper.ActivationRequested += (s, e) => { };
                created = 0;
                o = SecondLaunch.Run(new SingleInstance(ackId), () => { created++; return new SingleInstance(ackId); },
                    TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50), out taken);
                Check("run: acknowledged exits without retrying", o == SecondLaunchOutcome.Activated && taken == null && created == 0,
                    o + ", " + created + " creates");
                Check("run: the holder keeps the lock", keeper.IsFirstInstance);
            }

            // A holder that never lets go: gives up at the bound with nothing taken.
            string heldId = Unique();
            using (var keeper = new SingleInstance(heldId))
            {
                watch = Stopwatch.StartNew();
                o = SecondLaunch.Run(new SingleInstance(heldId), () => new SingleInstance(heldId),
                    TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50), out taken);
                watch.Stop();
                Check("run: never released gives up", o == SecondLaunchOutcome.StillHeld && taken == null, o.ToString());
                Check("run: within the bound", watch.ElapsedMilliseconds >= 350 && watch.ElapsedMilliseconds < 3000, watch.ElapsedMilliseconds + " ms");
            }
            using (var after = new SingleInstance(heldId))
                Check("run: no retry instance is left holding the name", after.IsFirstInstance);
        }

        // ---------------------------------------------------------------- AppMain and the waiter, checked in source

        static string Source(string folder, string file)
        {
            return File.ReadAllText(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine("..", Path.Combine("src", Path.Combine(folder, file))))));
        }

        static void WiringTests()
        {
            string main = Source("App", "AppMain.cs");
            int branch = main.IndexOf("if (instance != null && !instance.IsFirstInstance)\n", StringComparison.Ordinal);
            int app = main.IndexOf("var app = new Application", StringComparison.Ordinal);
            string body = branch < 0 || app < branch ? "" : main.Substring(branch, app - branch);
            int run = body.IndexOf("SecondLaunch.Run(instance, () => new SingleInstance(), SecondLaunch.AckWait, SecondLaunch.ReleaseWait, SecondLaunch.RetryEvery, out ",
                StringComparison.Ordinal);
            Check("wiring: a launch that finds the lock held goes through SecondLaunch.Run", run >= 0, body);
            Check("wiring: only BecameFirst goes on to start up", body.Contains("if (outcome != SecondLaunchOutcome.BecameFirst)")
                && body.Contains("return 0;"), body);
            Check("wiring: StillHeld is logged once through ErrorLog",
                body.Contains("outcome == SecondLaunchOutcome.StillHeld") && body.Contains("ErrorLog.TryWrite(ErrorLog.DefaultPath, SecondLaunch.StillHeldLine"), body);
            Check("wiring: a failure while waiting exits as before", body.Contains("catch (Exception ex)") && body.IndexOf("return 0;", StringComparison.Ordinal) > run, body);
            Check("wiring: no relaunch anywhere on this path", !main.Contains("Process.Start") && !body.Contains("Elevation."), body);

            string inst = Source("Gui", "SingleInstance.cs");
            int loop = inst.IndexOf("void WaitLoop()", StringComparison.Ordinal);
            int loopEnd = loop < 0 ? -1 : inst.IndexOf("\n        }", loop, StringComparison.Ordinal);
            string waitLoop = loopEnd < 0 ? "" : inst.Substring(loop, loopEnd - loop);
            int none = waitLoop.IndexOf("if (handler == null)", StringComparison.Ordinal);
            int ack = waitLoop.IndexOf("GuiNative.SetEvent(", StringComparison.Ordinal);
            Check("wiring: the waiter acknowledges only after it has found a handler", none >= 0 && ack > none, waitLoop);
        }
    }
}
