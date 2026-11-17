using System;

namespace RamOptimiser.Gui
{
    /// <summary>Why automatic cleans are waiting, highest priority first after None.</summary>
    public enum HoldReason { None, Sleeping, Resuming, Busy, Away, OnBattery }

    /// <summary>The user's state as the shell reports it. Unknown counts as present.</summary>
    public enum Presence { Unknown, Present, Away, Busy }

    /// <summary>Where the power comes from. Unknown counts as mains.</summary>
    public enum PowerSource { Unknown, Mains, Battery }

    /// <summary>Which kinds of automatic clean a hold stops, and why.</summary>
    public sealed class GateHold
    {
        public readonly HoldReason Reason;
        /// <summary>Cleans triggered by high load wait.</summary>
        public readonly bool Threshold;
        /// <summary>Scheduled (interval) cleans wait.</summary>
        public readonly bool Interval;

        public static readonly GateHold None = new GateHold(HoldReason.None, false, false);

        public GateHold(HoldReason reason, bool threshold, bool interval)
        {
            Reason = reason;
            Threshold = threshold;
            Interval = interval;
        }
    }

    /// <summary>The gate's timings and policy. Fixed in code: none of it comes from settings.ini.</summary>
    public sealed class GateOptions
    {
        /// <summary>How long automatic cleans wait after the PC wakes, while apps page back in.</summary>
        public readonly TimeSpan ResumeGrace;
        /// <summary>Wall time running ahead of awake time by at least this much between two ticks is a sleep.</summary>
        public readonly TimeSpan SleepGap;
        /// <summary>A suspend with no resume after this much awake time never happened (or its resume was missed).</summary>
        public readonly TimeSpan SuspendTimeout;
        /// <summary>A locked or switched-away session also holds cleans triggered by high load.</summary>
        public readonly bool AwayHoldsThreshold;
        /// <summary>Running on battery also holds cleans triggered by high load.</summary>
        public readonly bool BatteryHoldsThreshold;

        /// <summary>
        /// The owner's choices (2026-11-17): 2 minutes of grace after waking; high load still cleans while the
        /// PC is locked or on battery, because the pressure is real and only the schedule is optional.
        /// </summary>
        public GateOptions()
            : this(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), false, false)
        {
        }

        public GateOptions(TimeSpan resumeGrace, TimeSpan sleepGap, TimeSpan suspendTimeout,
            bool awayHoldsThreshold, bool batteryHoldsThreshold)
        {
            if (resumeGrace < TimeSpan.Zero) throw new ArgumentOutOfRangeException("resumeGrace");
            if (sleepGap <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("sleepGap");
            if (suspendTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException("suspendTimeout");
            ResumeGrace = resumeGrace;
            SleepGap = sleepGap;
            SuspendTimeout = suspendTimeout;
            AwayHoldsThreshold = awayHoldsThreshold;
            BatteryHoldsThreshold = batteryHoldsThreshold;
        }
    }

    /// <summary>The gate after an event, the hold it now imposes, and whether the schedule restarts now.</summary>
    public sealed class GateStep
    {
        public readonly AutoCleanGate Gate;
        public readonly GateHold Hold;
        /// <summary>The interval schedule restarts from this moment (a wake, or the end of a hold that blocked it).</summary>
        public readonly bool Reanchor;

        public GateStep(AutoCleanGate gate, GateHold hold, bool reanchor)
        {
            Gate = gate;
            Hold = hold;
            Reanchor = reanchor;
        }
    }

    /// <summary>
    /// When automatic cleans must wait: asleep, just woken, a full-screen app, a locked session, on battery.
    /// Immutable and pure: the service feeds it events and two clocks and stores the gate each step returns.
    /// Time bases: now is the wall clock (UTC); awake is time the system spent awake, which stops during sleep
    /// and hibernation (QueryUnbiasedInterruptTime). Sleep shows as wall time racing ahead of awake time, so it
    /// is caught even when the suspend and resume messages never arrive. A timer late because the CPU is busy
    /// moves both clocks alike and is not mistaken for sleep.
    /// </summary>
    public sealed class AutoCleanGate
    {
        public readonly bool Asleep;
        /// <summary>The awake clock when the suspend message came.</summary>
        public readonly TimeSpan SuspendedAtAwake;
        /// <summary>Automatic cleans wait until this wall time after a wake; null when there is no grace.</summary>
        public readonly DateTime? GraceUntil;
        public readonly Presence Presence;
        public readonly PowerSource Power;

        /// <summary>Awake, no grace, presence and power not yet known.</summary>
        public static readonly AutoCleanGate Initial = new AutoCleanGate(false, TimeSpan.Zero, null, Presence.Unknown, PowerSource.Unknown);

        AutoCleanGate(bool asleep, TimeSpan suspendedAtAwake, DateTime? graceUntil, Presence presence, PowerSource power)
        {
            Asleep = asleep;
            SuspendedAtAwake = suspendedAtAwake;
            GraceUntil = graceUntil;
            Presence = presence;
            Power = power;
        }

        static readonly GateHold SleepingHold = new GateHold(HoldReason.Sleeping, true, true);

        /// <summary>The PC is about to sleep: hold everything until it wakes.</summary>
        public static GateStep OnSuspend(AutoCleanGate g, TimeSpan awakeNow)
        {
            if (g == null) throw new ArgumentNullException("g");
            return new GateStep(new AutoCleanGate(true, awakeNow, g.GraceUntil, g.Presence, g.Power), SleepingHold, false);
        }

        /// <summary>The PC woke: start the grace and restart the schedule from now.</summary>
        public static GateStep OnResume(AutoCleanGate g, DateTime now, GateOptions o)
        {
            if (g == null) throw new ArgumentNullException("g");
            if (o == null) throw new ArgumentNullException("o");
            AutoCleanGate next = new AutoCleanGate(false, g.SuspendedAtAwake, now + o.ResumeGrace, g.Presence, g.Power);
            return new GateStep(next, HoldAt(next, now, o), true);
        }

        /// <summary>
        /// One sampling tick. wallSinceLast and awakeSinceLast are both clocks' progress since the previous tick
        /// (zero on the first); p and power are what the system reports now.
        /// </summary>
        public static GateStep OnTick(AutoCleanGate g, DateTime now, TimeSpan wallSinceLast, TimeSpan awakeSinceLast,
            TimeSpan awakeNow, Presence p, PowerSource power, GateOptions o)
        {
            return OnTick(g, now, wallSinceLast, awakeSinceLast, awakeNow, p, power, o, false);
        }

        /// <summary>
        /// As above. awakeIsWall says the awake figures are the wall clock standing in for a missing awake clock:
        /// then no gap can show, and the suspend timeout would count the hours slept, so a wall jump of at least
        /// SleepGap while marked asleep is taken as the wake (grace and a fresh schedule), whichever of this tick
        /// and the resume message comes first.
        /// </summary>
        public static GateStep OnTick(AutoCleanGate g, DateTime now, TimeSpan wallSinceLast, TimeSpan awakeSinceLast,
            TimeSpan awakeNow, Presence p, PowerSource power, GateOptions o, bool awakeIsWall)
        {
            if (g == null) throw new ArgumentNullException("g");
            if (o == null) throw new ArgumentNullException("o");

            // A wall clock set back, or an awake clock going back (which it never should), is no gap.
            bool slept = wallSinceLast > TimeSpan.Zero && awakeSinceLast >= TimeSpan.Zero
                && wallSinceLast - awakeSinceLast >= o.SleepGap;
            if (awakeIsWall && g.Asleep && wallSinceLast >= o.SleepGap) slept = true;
            if (slept)
            {
                AutoCleanGate woke = new AutoCleanGate(false, g.SuspendedAtAwake, now + o.ResumeGrace, p, power);
                return new GateStep(woke, HoldAt(woke, now, o), true);
            }

            GateHold before = HoldAt(g, now, o);
            bool asleep = g.Asleep;
            bool suspendExpired = false;
            // No gap, yet still marked asleep after a minute awake: the suspend never happened or its resume was
            // missed. Stop holding, without a grace. An awake clock behind the suspend counts as expired too.
            if (asleep && (awakeNow - g.SuspendedAtAwake >= o.SuspendTimeout || awakeNow < g.SuspendedAtAwake))
            {
                asleep = false;
                suspendExpired = true;
            }

            DateTime? grace = g.GraceUntil;
            if (grace.HasValue && grace.Value > now + o.ResumeGrace) grace = now + o.ResumeGrace; // clock set back
            if (grace.HasValue && now >= grace.Value) grace = null;

            AutoCleanGate next = new AutoCleanGate(asleep, g.SuspendedAtAwake, grace, p, power);
            GateHold after = HoldAt(next, now, o);
            bool reanchor = !suspendExpired && before.Interval && !after.Interval;
            return new GateStep(next, after, reanchor);
        }

        /// <summary>
        /// The hold in force at now. The first reason that applies is reported, in this order: Sleeping,
        /// Resuming, Busy (both kinds held), Away, OnBattery (the schedule held; high load per the options).
        /// </summary>
        public static GateHold HoldAt(AutoCleanGate g, DateTime now, GateOptions o)
        {
            if (g == null) throw new ArgumentNullException("g");
            if (o == null) throw new ArgumentNullException("o");
            if (g.Asleep) return SleepingHold;
            if (g.GraceUntil.HasValue && g.GraceUntil.Value > now) return new GateHold(HoldReason.Resuming, true, true);
            if (g.Presence == Presence.Busy) return new GateHold(HoldReason.Busy, true, true);
            if (g.Presence == Presence.Away) return new GateHold(HoldReason.Away, o.AwayHoldsThreshold, true);
            if (g.Power == PowerSource.Battery) return new GateHold(HoldReason.OnBattery, o.BatteryHoldsThreshold, true);
            return GateHold.None;
        }
    }
}
