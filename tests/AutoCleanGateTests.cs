// Tests for the auto-clean hold gate (P5): the pure sleep/resume, presence and power state machine, the
// Decide overload that honours a hold, the system-condition mappers and the hold wording. Nothing here
// changes any system state; SystemConditions only reads.
using System;
using System.Runtime.InteropServices;
using RamOptimiser.App;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class AutoCleanGateTests
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
            GateTests();
            DecideTests();
            MapperTests();
            WordingTests();
            return failed;
        }

        static readonly DateTime T0 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        static readonly TimeSpan A0 = TimeSpan.FromHours(5);

        static TimeSpan S(double seconds) { return TimeSpan.FromSeconds(seconds); }

        /// <summary>A tick with equal wall and awake progress (no sleep), in the given conditions.</summary>
        static GateStep Tick(AutoCleanGate g, DateTime now, TimeSpan awakeNow, Presence p, PowerSource power, GateOptions o)
        {
            return AutoCleanGate.OnTick(g, now, S(2), S(2), awakeNow, p, power, o);
        }

        static bool Holds(GateHold h, HoldReason r, bool threshold, bool interval)
        {
            return h != null && h.Reason == r && h.Threshold == threshold && h.Interval == interval;
        }

        static string Show(GateHold h)
        {
            return h == null ? "null" : h.Reason + " t=" + h.Threshold + " i=" + h.Interval;
        }

        static void GateTests()
        {
            var o = new GateOptions();
            Check("gate: default options", o.ResumeGrace == TimeSpan.FromMinutes(2) && o.SleepGap == S(30) && o.SuspendTimeout == S(60)
                && !o.AwayHoldsThreshold && !o.BatteryHoldsThreshold);
            Check("gate: initial holds nothing", Holds(AutoCleanGate.HoldAt(AutoCleanGate.Initial, T0, o), HoldReason.None, false, false)
                && GateHold.None.Reason == HoldReason.None && !GateHold.None.Threshold && !GateHold.None.Interval);

            GateStep suspended = AutoCleanGate.OnSuspend(AutoCleanGate.Initial, A0);
            Check("gate: suspend holds both", Holds(suspended.Hold, HoldReason.Sleeping, true, true)
                && Holds(AutoCleanGate.HoldAt(suspended.Gate, T0, o), HoldReason.Sleeping, true, true) && !suspended.Reanchor,
                Show(suspended.Hold));

            GateStep resumed = AutoCleanGate.OnResume(suspended.Gate, T0, o);
            Check("gate: resume reanchors", resumed.Reanchor && Holds(resumed.Hold, HoldReason.Resuming, true, true), Show(resumed.Hold));
            Check("gate: resume grace at 119 s", Holds(AutoCleanGate.HoldAt(resumed.Gate, T0 + S(119), o), HoldReason.Resuming, true, true));
            Check("gate: resume grace over at 120 s", Holds(AutoCleanGate.HoldAt(resumed.Gate, T0 + S(120), o), HoldReason.None, false, false));
            GateStep graceOver = Tick(resumed.Gate, T0 + S(120), A0 + S(120), Presence.Present, PowerSource.Mains, o);
            Check("gate: grace end clears it, and the schedule keeps its wake anchor",
                !graceOver.Gate.GraceUntil.HasValue && !graceOver.Reanchor && graceOver.Hold.Reason == HoldReason.None);

            // Sleep seen only as wall time racing ahead of awake time (the resume message was missed).
            GateStep gap = AutoCleanGate.OnTick(AutoCleanGate.Initial, T0, TimeSpan.FromHours(8), S(2), A0, Presence.Unknown, PowerSource.Unknown, o);
            Check("gate: wall/awake gap of 8 h counts as resume", gap.Reanchor && Holds(gap.Hold, HoldReason.Resuming, true, true)
                && gap.Gate.GraceUntil == T0 + o.ResumeGrace, Show(gap.Hold));

            GateStep busyTimer = AutoCleanGate.OnTick(AutoCleanGate.Initial, T0, S(40), S(40), A0, Presence.Unknown, PowerSource.Unknown, o);
            Check("gate: 40 s timer delay under load is not sleep", !busyTimer.Reanchor && Holds(busyTimer.Hold, HoldReason.None, false, false),
                Show(busyTimer.Hold));

            GateStep under = AutoCleanGate.OnTick(AutoCleanGate.Initial, T0, S(60), S(60) - o.SleepGap + TimeSpan.FromTicks(1), A0,
                Presence.Unknown, PowerSource.Unknown, o);
            GateStep at = AutoCleanGate.OnTick(AutoCleanGate.Initial, T0, S(60), S(60) - o.SleepGap, A0, Presence.Unknown, PowerSource.Unknown, o);
            Check("gate: gap just under 30 s is not sleep", !under.Reanchor && under.Hold.Reason == HoldReason.None, Show(under.Hold));
            Check("gate: gap of 30 s is sleep", at.Reanchor && at.Hold.Reason == HoldReason.Resuming, Show(at.Hold));

            GateStep backwards = AutoCleanGate.OnTick(AutoCleanGate.Initial, T0, TimeSpan.FromHours(-3), S(2), A0, Presence.Unknown, PowerSource.Unknown, o);
            Check("gate: clock set back is not a gap", !backwards.Reanchor && backwards.Hold.Reason == HoldReason.None, Show(backwards.Hold));

            // A suspend whose resume never arrives (or that never happened) stops holding after 60 s of awake time.
            GateStep stillAsleep = Tick(suspended.Gate, T0 + S(59), A0 + S(59), Presence.Present, PowerSource.Mains, o);
            GateStep expired = Tick(suspended.Gate, T0 + S(60), A0 + S(60), Presence.Present, PowerSource.Mains, o);
            Check("gate: suspend still holds at 59 s awake", Holds(stillAsleep.Hold, HoldReason.Sleeping, true, true), Show(stillAsleep.Hold));
            Check("gate: suspend with no resume expires after 60 s awake",
                !expired.Gate.Asleep && !expired.Gate.GraceUntil.HasValue && !expired.Reanchor && expired.Hold.Reason == HoldReason.None,
                Show(expired.Hold));

            // The wall clock set back an hour during the grace: it must not stretch the grace to an hour and 2 minutes.
            DateTime back = T0 - TimeSpan.FromHours(1);
            GateStep clamped = AutoCleanGate.OnTick(resumed.Gate, back, TimeSpan.FromHours(-1), S(2), A0 + S(2), Presence.Unknown, PowerSource.Unknown, o);
            Check("gate: clock set back clamps grace", clamped.Gate.GraceUntil == back + o.ResumeGrace
                && Holds(AutoCleanGate.HoldAt(clamped.Gate, back + o.ResumeGrace, o), HoldReason.None, false, false),
                clamped.Gate.GraceUntil.ToString());

            Check("gate: busy holds both", Holds(Tick(AutoCleanGate.Initial, T0, A0, Presence.Busy, PowerSource.Mains, o).Hold, HoldReason.Busy, true, true));
            Check("gate: away holds interval only", Holds(Tick(AutoCleanGate.Initial, T0, A0, Presence.Away, PowerSource.Mains, o).Hold, HoldReason.Away, false, true));
            Check("gate: battery holds interval only",
                Holds(Tick(AutoCleanGate.Initial, T0, A0, Presence.Present, PowerSource.Battery, o).Hold, HoldReason.OnBattery, false, true));
            Check("gate: unknown presence and power hold nothing",
                Holds(Tick(AutoCleanGate.Initial, T0, A0, Presence.Unknown, PowerSource.Unknown, o).Hold, HoldReason.None, false, false));
            Check("gate: present on mains holds nothing",
                Holds(Tick(AutoCleanGate.Initial, T0, A0, Presence.Present, PowerSource.Mains, o).Hold, HoldReason.None, false, false));
            var strict = new GateOptions(o.ResumeGrace, o.SleepGap, o.SuspendTimeout, true, true);
            Check("gate: policy flags can hold threshold cleans too",
                Holds(Tick(AutoCleanGate.Initial, T0, A0, Presence.Away, PowerSource.Mains, strict).Hold, HoldReason.Away, true, true)
                && Holds(Tick(AutoCleanGate.Initial, T0, A0, Presence.Present, PowerSource.Battery, strict).Hold, HoldReason.OnBattery, true, true));

            GateStep onBattery = Tick(AutoCleanGate.Initial, T0, A0, Presence.Present, PowerSource.Battery, o);
            Check("gate: priority Sleeping over OnBattery", Holds(AutoCleanGate.OnSuspend(onBattery.Gate, A0).Hold, HoldReason.Sleeping, true, true));
            Check("gate: priority Resuming over OnBattery",
                AutoCleanGate.OnResume(onBattery.Gate, T0, o).Hold.Reason == HoldReason.Resuming);
            Check("gate: priority Busy over OnBattery",
                Tick(onBattery.Gate, T0 + S(2), A0 + S(2), Presence.Busy, PowerSource.Battery, o).Hold.Reason == HoldReason.Busy);
            Check("gate: priority Away over OnBattery",
                Tick(onBattery.Gate, T0 + S(2), A0 + S(2), Presence.Away, PowerSource.Battery, o).Hold.Reason == HoldReason.Away);

            // When a hold that blocked the schedule ends, the schedule restarts from that moment.
            GateStep toMains = Tick(onBattery.Gate, T0 + S(2), A0 + S(2), Presence.Present, PowerSource.Mains, o);
            GateStep mainsAgain = Tick(toMains.Gate, T0 + S(4), A0 + S(4), Presence.Present, PowerSource.Mains, o);
            Check("gate: interval hold ending reanchors", toMains.Reanchor && toMains.Hold.Reason == HoldReason.None);
            Check("gate: no hold to end, no reanchor", !mainsAgain.Reanchor && !onBattery.Reanchor);
            GateStep away = Tick(AutoCleanGate.Initial, T0, A0, Presence.Away, PowerSource.Mains, o);
            GateStep back2 = Tick(away.Gate, T0 + S(2), A0 + S(2), Presence.Present, PowerSource.Mains, o);
            GateStep awayToBattery = Tick(away.Gate, T0 + S(2), A0 + S(2), Presence.Present, PowerSource.Battery, o);
            Check("gate: unlocking reanchors", back2.Reanchor);
            Check("gate: one interval hold giving way to another does not reanchor", !awayToBattery.Reanchor);
        }

        static void DecideTests()
        {
            var both = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300, IntervalMinutes = 10 };
            GateHold intervalOnly = new GateHold(HoldReason.OnBattery, false, true);
            GateHold all = new GateHold(HoldReason.Busy, true, true);
            Check("decide: interval held, threshold due -> Threshold",
                AutoCleanService.Decide(both, 95, null, T0, T0.AddHours(1), false, 0, intervalOnly) == CleanReason.Threshold);
            Check("decide: interval held, only the interval due -> null",
                AutoCleanService.Decide(both, 10, null, T0, T0.AddHours(1), false, 0, intervalOnly) == null);
            Check("decide: threshold held -> null",
                AutoCleanService.Decide(both, 95, null, T0, T0.AddHours(1), false, 0, all) == null);
            Check("decide: threshold-only hold leaves the interval",
                AutoCleanService.Decide(both, 95, null, T0, T0.AddHours(1), false, 0, new GateHold(HoldReason.Away, true, false)) == CleanReason.Interval);
            Check("decide: no hold is the old decision",
                AutoCleanService.Decide(both, 95, null, T0, T0.AddHours(1), false, 0, GateHold.None) == CleanReason.Threshold
                && AutoCleanService.Decide(both, 10, null, T0, T0.AddHours(1), false, 0, null) == CleanReason.Interval);
        }

        static void MapperTests()
        {
            Check("presence: not present is away", SystemConditions.MapPresence(0, 1) == Presence.Away);
            Check("presence: busy", SystemConditions.MapPresence(0, 2) == Presence.Busy);
            Check("presence: D3D full screen", SystemConditions.MapPresence(0, 3) == Presence.Busy);
            Check("presence: presentation mode", SystemConditions.MapPresence(0, 4) == Presence.Busy);
            Check("presence: accepts notifications", SystemConditions.MapPresence(0, 5) == Presence.Present);
            Check("presence: quiet time", SystemConditions.MapPresence(0, 6) == Presence.Present);
            Check("presence: store app", SystemConditions.MapPresence(0, 7) == Presence.Present);
            Check("presence: unknown values", SystemConditions.MapPresence(0, 0) == Presence.Unknown
                && SystemConditions.MapPresence(0, 8) == Presence.Unknown && SystemConditions.MapPresence(0, -1) == Presence.Unknown);
            Check("presence: failing HRESULT", SystemConditions.MapPresence(unchecked((int)0x80004005), 2) == Presence.Unknown);

            Check("power: ac offline is battery", SystemConditions.MapPower(0, 1) == PowerSource.Battery);
            Check("power: ac online is mains", SystemConditions.MapPower(1, 8) == PowerSource.Mains);
            Check("power: ac unknown", SystemConditions.MapPower(255, 0) == PowerSource.Unknown);
            Check("power: no system battery is mains", SystemConditions.MapPower(255, 128) == PowerSource.Mains
                && SystemConditions.MapPower(0, 128) == PowerSource.Mains);
            Check("power: unknown battery flag falls back to the ac line", SystemConditions.MapPower(0, 255) == PowerSource.Battery
                && SystemConditions.MapPower(255, 255) == PowerSource.Unknown);

            Check("SYSTEM_POWER_STATUS size", Marshal.SizeOf(typeof(GuiNative.SYSTEM_POWER_STATUS)) == 12);

            // The real readers: read-only calls that must never throw.
            Conditions c = null;
            try { c = SystemConditions.Read(); } catch (Exception ex) { Check("conditions: Read never throws", false, ex.GetType().Name); }
            Check("conditions: Read returns a value", c != null);
            Check("conditions: Unknown is unknown", Conditions.Unknown.Presence == Presence.Unknown && Conditions.Unknown.Power == PowerSource.Unknown);
            try
            {
                TimeSpan a = SystemConditions.AwakeTime(), b = SystemConditions.AwakeTime();
                Check("awake time: positive and not going back", a > TimeSpan.Zero && b >= a, a + " then " + b);
            }
            catch (Exception ex) { Check("awake time: readable", false, ex.GetType().Name); }
        }

        static void WordingTests()
        {
            var noInterval = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 77, IntervalMinutes = 0 };
            var interval = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 77, IntervalMinutes = 30 };
            foreach (HoldReason r in Enum.GetValues(typeof(HoldReason)))
            {
                foreach (AppSettings s in new[] { noInterval, interval })
                {
                    string text = AppText.AutoHoldText(r, s);
                    string name = "hold text: " + r + ", interval " + s.IntervalMinutes;
                    bool shown = r == HoldReason.Resuming || r == HoldReason.Busy
                        || (s.IntervalMinutes > 0 && (r == HoldReason.Away || r == HoldReason.OnBattery));
                    Check(name + (shown ? " shown" : " hidden"), (text != null) == shown, text);
                    if (text == null) continue;
                    Check(name + ": no exclamation mark", text.IndexOf('!') < 0, text);
                    Check(name + ": a full sentence", text.EndsWith(".", StringComparison.Ordinal) && char.IsUpper(text[0]), text);
                }
            }
            Check("hold text: battery names the threshold", AppText.AutoHoldText(HoldReason.OnBattery, interval).Contains("77%"));
            Check("hold text: locked names the threshold", AppText.AutoHoldText(HoldReason.Away, interval).Contains("77%"));
            Check("hold text: grace matches the default", AppText.AutoHoldText(HoldReason.Resuming, interval).Contains("2 minutes")
                && new GateOptions().ResumeGrace == TimeSpan.FromMinutes(2));
            Check("hold text: busy wording", AppText.AutoHoldText(HoldReason.Busy, noInterval)
                == "Paused while a full-screen app or presentation is running.");
        }
    }
}
