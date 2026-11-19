using System;
using System.Diagnostics;
using System.Threading;

namespace RamOptimiser.Gui
{
    /// <summary>What the running copy said to an activation request (SingleInstance.SignalAndWaitForAck).</summary>
    public enum ActivationAnswer
    {
        /// <summary>A handler took the request: the running copy is showing its window.</summary>
        Acknowledged,
        /// <summary>No answer within the wait: the running copy's handler was removed again (it is exiting).</summary>
        NoAnswer,
        /// <summary>
        /// The request could not be confirmed either way: no usable acknowledgement event, the running copy has none
        /// (an older copy), or the signal failed.
        /// </summary>
        Unconfirmed
    }

    /// <summary>How a launch that found the lock held ends.</summary>
    public enum SecondLaunchOutcome
    {
        /// <summary>The running copy took the request: exit 0.</summary>
        Activated,
        /// <summary>No way to tell whether it was taken: exit 0, as before the acknowledgement existed.</summary>
        Unconfirmed,
        /// <summary>The running copy let the lock go within the bound: this launch starts as the first instance.</summary>
        BecameFirst,
        /// <summary>Still held after the bound: log one line and exit 0.</summary>
        StillHeld,
        /// <summary>
        /// Another program took the lock's name while this launch waited: run without single-instance, as at the
        /// first try, so a squatted name cannot stop the app.
        /// </summary>
        NameTaken
    }

    /// <summary>
    /// A launch that finds the single-instance lock held. It asks the running copy to show itself and waits (about
    /// 3 s) for the acknowledgement, which a copy sends when the request will be raised (also while it is still
    /// starting, before its handler is added: the request is kept for it). A copy
    /// that is exiting (its handler removed while it restores the services Power Clean stopped) does not answer, so
    /// this launch waits (about 60 s) for that copy to release the lock, retrying, and then starts as the first
    /// instance. Nothing here starts a process: the exiting copy may be elevated, and a medium-integrity launch can
    /// signal activation, so a relaunch from that side would be an elevation path.
    /// </summary>
    public static class SecondLaunch
    {
        public static readonly TimeSpan AckWait = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(250);

        /// <summary>The ErrorLog line for StillHeld.</summary>
        public const string StillHeldLine =
            "another copy did not answer and still holds the single-instance lock after 60 s; this launch exits";

        /// <summary>
        /// The decision, pure. ask signals the running copy and waits for its answer; an exception there counts as
        /// Unconfirmed (today's behaviour, never a crash). Only NoAnswer leads to the wait: tryAcquire (true when
        /// this launch now holds the lock) is tried at once, then every retry, until elapsed has moved bound past
        /// the moment the answer came; the last try is at the bound. A SingleInstanceNameTakenException from
        /// tryAcquire gives NameTaken; its other exceptions reach the caller.
        /// </summary>
        public static SecondLaunchOutcome Decide(Func<ActivationAnswer> ask, Func<bool> tryAcquire, Func<TimeSpan> elapsed,
            Action<TimeSpan> pause, TimeSpan bound, TimeSpan retry)
        {
            if (ask == null) throw new ArgumentNullException("ask");
            if (tryAcquire == null) throw new ArgumentNullException("tryAcquire");
            if (elapsed == null) throw new ArgumentNullException("elapsed");
            if (pause == null) throw new ArgumentNullException("pause");
            if (retry <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("retry");
            ActivationAnswer answer;
            try { answer = ask(); }
            catch (Exception) { return SecondLaunchOutcome.Unconfirmed; }
            if (answer == ActivationAnswer.Acknowledged) return SecondLaunchOutcome.Activated;
            if (answer != ActivationAnswer.NoAnswer) return SecondLaunchOutcome.Unconfirmed;

            TimeSpan start = elapsed();
            while (true)
            {
                bool acquired;
                try { acquired = tryAcquire(); }
                catch (SingleInstanceNameTakenException) { return SecondLaunchOutcome.NameTaken; }
                if (acquired) return SecondLaunchOutcome.BecameFirst;
                TimeSpan left = bound - (elapsed() - start);
                if (left <= TimeSpan.Zero) return SecondLaunchOutcome.StillHeld;
                pause(left < retry ? left : retry);
            }
        }

        /// <summary>
        /// Decide with real objects: asking (not first) is signalled and then disposed before the first retry (its
        /// own handle would keep the lock's name alive); each retry is create(), kept when first and disposed when
        /// not. taken is the new first instance for BecameFirst, otherwise null. create's exceptions other than
        /// SingleInstanceNameTakenException (NameTaken) reach the caller.
        /// </summary>
        internal static SecondLaunchOutcome Run(SingleInstance asking, Func<SingleInstance> create, TimeSpan ackWait,
            TimeSpan releaseWait, TimeSpan retry, out SingleInstance taken)
        {
            if (asking == null) throw new ArgumentNullException("asking");
            if (create == null) throw new ArgumentNullException("create");
            SingleInstance got = null;
            Stopwatch clock = Stopwatch.StartNew();
            SecondLaunchOutcome outcome;
            try
            {
                outcome = Decide(
                    () => { try { return asking.SignalAndWaitForAck(ackWait); } finally { asking.Dispose(); } },
                    () =>
                    {
                        SingleInstance attempt = create();
                        if (attempt.IsFirstInstance) { got = attempt; return true; }
                        attempt.Dispose();
                        return false;
                    },
                    () => clock.Elapsed, d => Thread.Sleep(d), releaseWait, retry);
            }
            finally { asking.Dispose(); }
            taken = outcome == SecondLaunchOutcome.BecameFirst ? got : null;
            if (taken == null && got != null) got.Dispose();
            return outcome;
        }
    }
}
