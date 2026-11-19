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

            // A name squatted while retrying is not a reason to exit, as at the first try: run without single-instance.
            escaped = null;
            o = SecondLaunchOutcome.StillHeld;
            acquires = 0;
            try
            {
                o = SecondLaunch.Decide(() => ActivationAnswer.NoAnswer,
                    () => { acquires++; if (acquires < 3) return false; throw new SingleInstanceNameTakenException(5, "taken"); },
                    () => now, d => now += d, Bound, Retry);
            }
            catch (Exception ex) { escaped = ex; }
            Check("decide: a name squatted while retrying runs without single-instance",
                escaped == null && o == SecondLaunchOutcome.NameTaken && acquires == 3,
                escaped == null ? o + ", " + acquires : escaped.GetType().Name);

            // Any other acquire error is still the caller's to handle.
            escaped = null;
            try
            {
                SecondLaunch.Decide(() => ActivationAnswer.NoAnswer, () => { throw new UnauthorizedAccessException("lock"); },
                    () => now, d => now += d, Bound, Retry);
            }
            catch (Exception ex) { escaped = ex; }
            Check("decide: another acquire error reaches the caller", escaped is UnauthorizedAccessException,
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

            // No handler yet (the copy is still starting): answered at once, and the request is kept for the first
            // handler, so the launch exits instead of waiting for a lock a live copy holds.
            string startingId = Unique();
            using (var activated = new ManualResetEvent(false))
            using (var first = new SingleInstance(startingId))
            {
                using (var second = new SingleInstance(startingId))
                {
                    var watch = Stopwatch.StartNew();
                    ActivationAnswer a = second.SignalAndWaitForAck(TimeSpan.FromSeconds(2));
                    Check("ack: a copy that is still starting answers", a == ActivationAnswer.Acknowledged && watch.ElapsedMilliseconds < 2000,
                        a + " after " + watch.ElapsedMilliseconds + " ms");
                }
                first.ActivationRequested += (s, e) => activated.Set();
                Check("ack: the kept request reaches the first handler", activated.WaitOne(5000));
            }

            // The handler removed again (the copy is exiting): no answer, and a stale acknowledgement left set does not count.
            string quietId = Unique();
            using (var first = new SingleInstance(quietId))
            {
                Exiting(first);
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

            // Launches asking at the same moment: each gets the answer, even when their signals merge into one wake
            // of the waiter. The first request's handler holds the waiter until both later launches have sent their
            // requests (counted through the Signalled hook), so the two signals always merge into one wake.
            string busyId = Unique();
            using (var gate = new ManualResetEvent(false))
            using (var bothSent = new CountdownEvent(2))
            using (var first = new SingleInstance(busyId))
            {
                int calls = 0;
                first.ActivationRequested += (s, e) => { if (Interlocked.Increment(ref calls) == 1) gate.WaitOne(10000); };
                using (var opener = new SingleInstance(busyId))
                    Check("ack together: the first request is answered", opener.SignalAndWaitForAck(TimeSpan.FromSeconds(3)) == ActivationAnswer.Acknowledged);
                var answers = new ActivationAnswer[2];
                var askers = new Thread[2];
                for (int n = 0; n < askers.Length; n++)
                {
                    int slot = n;
                    askers[n] = new Thread(() =>
                    {
                        using (var asker = new SingleInstance(busyId))
                        {
                            asker.Signalled = () => bothSent.Signal();
                            answers[slot] = asker.SignalAndWaitForAck(TimeSpan.FromSeconds(3));
                        }
                    }) { IsBackground = true };
                    askers[n].Start();
                }
                bool sent = bothSent.Wait(10000);
                gate.Set();
                foreach (Thread t in askers) t.Join(10000);
                Check("ack together: both requests were sent while the waiter was held", sent && Volatile.Read(ref calls) >= 1);
                Check("ack together: every launch is answered", answers[0] == ActivationAnswer.Acknowledged && answers[1] == ActivationAnswer.Acknowledged,
                    answers[0] + " / " + answers[1]);
            }

            ClosingObjectTests();

            // An older running copy (lock and activation event, no acknowledgement event): it never answers, so the
            // launch does not wait for it. The request is still sent, and the answer is Unconfirmed: exit as before.
            string oldId = Unique();
            string own = "O:" + sid + "D:(A;;GA;;;" + sid + ")";
            using (SafeWaitHandle oldMutex = CreateMutexWithSddl(SingleInstance.MutexName(oldId), own))
            using (SafeWaitHandle oldEvent = GuiPlumbingTests.CreateEventWithSddl(SingleInstance.EventName(oldId), own))
            using (EventWaitHandle oldWait = EventWaitHandle.OpenExisting(SingleInstance.EventName(oldId)))
            {
                using (var second = new SingleInstance(oldId))
                {
                    var watch = Stopwatch.StartNew();
                    ActivationAnswer a = second.SignalAndWaitForAck(TimeSpan.FromSeconds(2));
                    Check("old copy: the lock is held", !oldMutex.IsInvalid && !oldEvent.IsInvalid && !second.IsFirstInstance);
                    Check("old copy: not waited on", a == ActivationAnswer.Unconfirmed && watch.ElapsedMilliseconds < 1500,
                        a + " after " + watch.ElapsedMilliseconds + " ms");
                }
                Check("old copy: the request is still sent", oldWait.WaitOne(0));
                Check("old copy: no acknowledgement event is left behind", !EventExists(SingleInstance.AckName(oldId)));
                SingleInstance taken;
                int created = 0;
                SecondLaunchOutcome o = SecondLaunch.Run(new SingleInstance(oldId), () => { created++; return new SingleInstance(oldId); },
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50), out taken);
                Check("old copy: the launch exits at once", o == SecondLaunchOutcome.Unconfirmed && taken == null && created == 0,
                    o + ", " + created + " creates");
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

        // ---------------------------------------------------------------- a copy that began closing, real objects

        static void ClosingObjectTests()
        {
            // Exit has begun (BeginClosing is its first step) but the handler is still subscribed, as it is until
            // teardown step 8: no answer, so the launch waits for the lock, and the request is not raised either.
            string closingId = Unique();
            using (var first = new SingleInstance(closingId))
            {
                int raised = 0;
                first.ActivationRequested += (s, e) => Interlocked.Increment(ref raised);
                first.BeginClosing();
                first.BeginClosing();
                using (var second = new SingleInstance(closingId))
                {
                    var watch = Stopwatch.StartNew();
                    ActivationAnswer a = second.SignalAndWaitForAck(TimeSpan.FromMilliseconds(500));
                    Check("closing: a closing copy with its handler still subscribed does not answer",
                        a == ActivationAnswer.NoAnswer && watch.ElapsedMilliseconds >= 450, a + " after " + watch.ElapsedMilliseconds + " ms");
                }
                Thread.Sleep(300);
                Check("closing: its handler is not raised", Volatile.Read(ref raised) == 0, raised + " call(s)");
            }

            // A copy dying before any handler was added (start-up failed, the error box is up): no answer, and
            // nothing is kept for a handler added later.
            string earlyId = Unique();
            using (var first = new SingleInstance(earlyId))
            {
                first.BeginClosing();
                using (var second = new SingleInstance(earlyId))
                {
                    var watch = Stopwatch.StartNew();
                    ActivationAnswer a = second.SignalAndWaitForAck(TimeSpan.FromMilliseconds(500));
                    Check("closing: a copy closing before any handler does not answer",
                        a == ActivationAnswer.NoAnswer && watch.ElapsedMilliseconds >= 450, a + " after " + watch.ElapsedMilliseconds + " ms");
                }
                int raised = 0;
                first.ActivationRequested += (s, e) => Interlocked.Increment(ref raised);
                Thread.Sleep(300);
                Check("closing: no kept request is raised later", Volatile.Read(ref raised) == 0, raised + " call(s)");
            }

            // The replay of a kept request reaches the first handler but sends no answer: nobody is waiting for one,
            // and a stray answer could reach a later launch just before this copy starts closing.
            string replayId = Unique();
            using (var raised = new ManualResetEvent(false))
            using (var first = new SingleInstance(replayId))
            {
                using (var second = new SingleInstance(replayId))
                    Check("replay: the request before any handler is answered",
                        second.SignalAndWaitForAck(TimeSpan.FromSeconds(2)) == ActivationAnswer.Acknowledged);
                using (EventWaitHandle answer = EventWaitHandle.OpenExisting(SingleInstance.AckName(replayId)))
                {
                    answer.Reset();
                    first.ActivationRequested += (s, e) => raised.Set();
                    Check("replay: the kept request reaches the first handler", raised.WaitOne(5000));
                    Check("replay: the replay sends no answer", !answer.WaitOne(0));
                }
                using (var later = new SingleInstance(replayId))
                    Check("replay: a later launch is still answered",
                        later.SignalAndWaitForAck(TimeSpan.FromSeconds(3)) == ActivationAnswer.Acknowledged);
            }

            // After Dispose (Exit's release ran first, a fatal path follows): still safe, and repeatable.
            var gone = new SingleInstance(Unique());
            gone.Dispose();
            Exception escaped = null;
            try
            {
                gone.BeginClosing();
                gone.BeginClosing();
            }
            catch (Exception ex) { escaped = ex; }
            Check("closing: BeginClosing after Dispose is safe", escaped == null, escaped == null ? null : escaped.GetType().Name);
        }

        /// <summary>A named mutex with the given security descriptor, as an older copy of the app made its lock.</summary>
        static SafeWaitHandle CreateMutexWithSddl(string name, string sddl)
        {
            IntPtr sd;
            if (!GuiNative.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, GuiNative.SDDL_REVISION_1, out sd, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                var sa = new GuiNative.SECURITY_ATTRIBUTES
                {
                    nLength = System.Runtime.InteropServices.Marshal.SizeOf(typeof(GuiNative.SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = sd,
                    bInheritHandle = false
                };
                return GuiNative.CreateMutex(ref sa, false, name);
            }
            finally { GuiNative.LocalFree(sd); }
        }

        static bool EventExists(string name)
        {
            using (SafeWaitHandle h = GuiNative.OpenEvent(SecurityNative.READ_CONTROL, false, name))
                return !h.IsInvalid;
        }

        // ---------------------------------------------------------------- Run: the decision with real objects

        static void RunTests()
        {
            // The holder's handler is gone and it lets go after 400 ms, as an exiting copy does after its restore.
            string id = Unique();
            var holder = new SingleInstance(id);
            Exiting(holder);
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
                Exiting(keeper);
                watch = Stopwatch.StartNew();
                o = SecondLaunch.Run(new SingleInstance(heldId), () => new SingleInstance(heldId),
                    TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50), out taken);
                watch.Stop();
                Check("run: never released gives up", o == SecondLaunchOutcome.StillHeld && taken == null, o.ToString());
                Check("run: within the bound", watch.ElapsedMilliseconds >= 350 && watch.ElapsedMilliseconds < 3000, watch.ElapsedMilliseconds + " ms");
            }
            using (var after = new SingleInstance(heldId))
                Check("run: no retry instance is left holding the name", after.IsFirstInstance);

            // The exiting holder lets go and another program takes the lock's name with another kind of object before
            // the retry: the launch runs without single-instance (NameTaken), nothing taken, nothing thrown.
            string squatId = Unique();
            var leaving = new SingleInstance(squatId);
            Exiting(leaving);
            EventWaitHandle squat = null;
            var taker = new Thread(() =>
            {
                Thread.Sleep(300);
                leaving.Dispose();
                squat = new EventWaitHandle(false, EventResetMode.ManualReset, SingleInstance.MutexName(squatId));
            }) { IsBackground = true };
            Exception thrown = null;
            o = SecondLaunchOutcome.StillHeld;
            taken = null;
            taker.Start();
            try
            {
                o = SecondLaunch.Run(new SingleInstance(squatId), () => new SingleInstance(squatId),
                    TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), out taken);
            }
            catch (Exception ex) { thrown = ex; }
            taker.Join(2000);
            try
            {
                Check("run: a name squatted while waiting runs without single-instance",
                    thrown == null && o == SecondLaunchOutcome.NameTaken && taken == null,
                    thrown == null ? o.ToString() : thrown.GetType().Name);
            }
            finally
            {
                if (taken != null) taken.Dispose();
                if (squat != null) squat.Dispose();
            }
        }

        /// <summary>As AppShell.Exit leaves the instance: its handler added at start-up and removed again.</summary>
        static void Exiting(SingleInstance instance)
        {
            EventHandler h = (s, e) => { };
            instance.ActivationRequested += h;
            instance.ActivationRequested -= h;
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
            // A name squatted while waiting: logged, then start-up goes on without single-instance (no exit before it).
            int squatted = body.IndexOf("if (outcome == SecondLaunchOutcome.NameTaken)", StringComparison.Ordinal);
            int squatEnd = squatted < 0 ? -1 : body.IndexOf("\n                }", squatted, StringComparison.Ordinal);
            string squatBranch = squatEnd < 0 ? "" : body.Substring(squatted, squatEnd - squatted);
            Check("wiring: NameTaken logs and runs without single-instance",
                squatBranch.Contains("ErrorLog.TryWrite(ErrorLog.DefaultPath, NameHeldLine") && squatBranch.Contains("instance = null;")
                && !squatBranch.Contains("return"), body);
            Check("wiring: the first-try squat logs the same line",
                Count(main, "ErrorLog.TryWrite(ErrorLog.DefaultPath, NameHeldLine") == 3 && Count(main, "\"single-instance name held by another program") == 1, main);
            Check("wiring: StillHeld is logged once through ErrorLog",
                body.Contains("outcome == SecondLaunchOutcome.StillHeld") && body.Contains("ErrorLog.TryWrite(ErrorLog.DefaultPath, SecondLaunch.StillHeldLine"), body);
            Check("wiring: a failure while waiting exits as before", body.Contains("catch (Exception ex)") && body.IndexOf("return 0;", StringComparison.Ordinal) > run, body);
            Check("wiring: no relaunch anywhere on this path", !main.Contains("Process.Start") && !body.Contains("Elevation."), body);
            // The shell gets the lock this launch took, not the asking instance Run has already disposed.
            int became = body.IndexOf("if (outcome != SecondLaunchOutcome.BecameFirst)", StringComparison.Ordinal);
            int handed = body.IndexOf("instance = taken;", StringComparison.Ordinal);
            Check("wiring: BecameFirst hands the taken lock to the shell", became >= 0 && handed > became
                && body.IndexOf("instance = taken;", handed + 1, StringComparison.Ordinal) < 0, body);

            ClosingWiring();
        }

        /// <summary>The body of the method whose declaration starts with signature (up to its closing brace at that indent).</summary>
        static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            int end = source.IndexOf("\n        }", at, StringComparison.Ordinal);
            return end < 0 ? source.Substring(at) : source.Substring(at, end - at);
        }

        static int Count(string text, string part)
        {
            return string.IsNullOrEmpty(part) ? 0 : (text.Length - text.Replace(part, "").Length) / part.Length;
        }

        /// <summary>
        /// Every way out of a running copy stops answering before anything else: Exit before exiting = true (its handler
        /// goes only at teardown step 8), EmergencyCleanup first, and Fatal before its log line and error box.
        /// </summary>
        static void ClosingWiring()
        {
            string shell = Source("App", "AppShell.cs");
            string exit = Body(shell, "public void Exit()");
            int closing = exit.IndexOf("instance.BeginClosing();", StringComparison.Ordinal);
            int flush = exit.IndexOf("FlushThreshold()", StringComparison.Ordinal);
            int exiting = exit.IndexOf("exiting = true;", StringComparison.Ordinal);
            Check("closing wiring: Exit begins closing first, before exiting = true", closing >= 0 && flush > closing && exiting > closing, exit);

            string emergency = Body(shell, "public void EmergencyCleanup()");
            int eClosing = emergency.IndexOf("instance.BeginClosing();", StringComparison.Ordinal);
            bool firstStep = eClosing >= 0;
            foreach (string step in new[] { "PowerModeChanged", "StopHotkey()", "service.Dispose()", "CloseFlyout()", "tray.Dispose()",
                "TryRestoreBounded(", "instance.Dispose()" })
            {
                int at = emergency.IndexOf(step, StringComparison.Ordinal);
                if (at < 0 || at < eClosing) firstStep = false;
            }
            Check("closing wiring: EmergencyCleanup begins closing before any other step", firstStep, emergency);

            string main = Source("App", "AppMain.cs");
            string fatal = Body(main, "static void Fatal(string context, Exception ex)");
            int stop = fatal.IndexOf("StopAnswering();", StringComparison.Ordinal);
            int log = fatal.IndexOf("ErrorLog.TryWrite(", StringComparison.Ordinal);
            int box = fatal.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
            Check("closing wiring: Fatal stops answering before it logs or shows its box", stop >= 0 && log > stop && box > stop, fatal);
            string stopBody = Body(main, "static void StopAnswering()");
            Check("closing wiring: StopAnswering begins closing on the lock this copy runs with",
                stopBody.Contains("lockInstance") && stopBody.Contains(".BeginClosing()"), stopBody);
            // Each fatal path reaches the box only through Fatal: the box is shown nowhere else.
            string ui = Body(main, "internal static void FailOnUiThread(");
            string domain = Body(main, "static void OnDomainUnhandledException(");
            string mainBody = Body(main, "public static int Main(");
            Check("closing wiring: every fatal path goes through Fatal, the only error box", Count(main, "MessageBox.Show(") == 1
                && ui.Contains("Fatal(context, ex);") && domain.Contains("Fatal(\"unhandled error\"")
                && mainBody.Contains("Fatal(\"start-up failed\", ex);"), main);
            int taken = mainBody.IndexOf("instance = taken;", StringComparison.Ordinal);
            int kept = mainBody.IndexOf("lockInstance = instance;", StringComparison.Ordinal);
            int made = mainBody.IndexOf("new AppShell(app, instance, options)", StringComparison.Ordinal);
            Check("closing wiring: the lock this copy runs with is kept before the shell starts",
                taken >= 0 && kept > taken && made > kept && Count(main, "lockInstance =") == 1, mainBody);
        }
    }
}
