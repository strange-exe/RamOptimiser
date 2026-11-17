// Tests for the auto-clean pause (P6), the futile-clean back-off shown to the user (R2+U6) and the wording of
// the auto-clean status in the Memory tab, Settings and the tray. Services run on fake clocks; nothing here
// cleans memory or touches the system.
using System;
using System.Globalization;
using System.Threading;
using RamOptimiser.App;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class AutoCleanPauseTests
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
            PauseServiceTests();
            PausePureTests();
            StatusTests();
            ManualStreakTests();
            SettingsTests();
            StateTests();
            BackoffWordingTests();
            NoticeTests();
            BarStepTests();
            LabelInNameTests();
            ClockBackLateTests();
            return failed;
        }

        static readonly DateTime T0 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        static readonly TimeSpan Hour = TimeSpan.FromHours(1);

        static MemorySnapshot Snap(uint load)
        {
            return new MemorySnapshot { TotalPhys = 100, AvailablePhys = 100 - load, LoadPercent = load };
        }

        /// <summary>A service on a fake wall clock that counts its cleans; each clean leaves load at After.</summary>
        sealed class Rig : IDisposable
        {
            public DateTime Now = T0;
            public uint Load = 10, After = 60;
            public int Cleans;
            public readonly AutoCleanService Svc;

            public Rig(AppSettings s)
            {
                Svc = new AutoCleanService(() => Snap(Load), a =>
                {
                    Interlocked.Increment(ref Cleans);
                    return new CleanReport { Before = Snap(Load), After = Snap(After) };
                }, () => Now);
                Svc.Update(s);
            }

            public void Dispose() { Svc.Dispose(); }
        }

        static AppSettings Threshold85() { return new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300 }; }
        static AppSettings Interval10() { return new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 99, IntervalMinutes = 10 }; }

        static void PauseServiceTests()
        {
            using (var rig = new Rig(Threshold85()))
            {
                rig.Load = 95;
                Check("pause: accepted while auto-clean is on", rig.Svc.Pause(Hour));
                rig.Svc.RunTick();
                Check("pause: no threshold clean while paused", rig.Cleans == 0, rig.Cleans + " cleans");
                AutoCleanStatus st = rig.Svc.GetStatus();
                Check("pause: status says paused until an hour on", st.Paused && st.PausedUntilUtc == T0 + Hour,
                    st.Paused + " " + st.PausedUntilUtc);
                rig.Now = T0 + Hour;
                rig.Svc.RunTick();
                Check("pause: threshold cleans resume after an hour", rig.Cleans == 1 && !rig.Svc.GetStatus().Paused, rig.Cleans + " cleans");
            }

            using (var rig = new Rig(Interval10()))
            {
                rig.Svc.Pause(Hour);
                rig.Now = T0 + TimeSpan.FromMinutes(30);
                rig.Svc.RunTick();
                Check("pause: no interval clean while paused", rig.Cleans == 0, rig.Cleans + " cleans");
                rig.Now = T0 + Hour;
                rig.Svc.RunTick();
                Check("pause: expiry does not fire an overdue interval clean", rig.Cleans == 0, rig.Cleans + " cleans");
                rig.Now = T0 + Hour + TimeSpan.FromMinutes(10);
                rig.Svc.RunTick();
                Check("pause: expiry re-anchors the interval", rig.Cleans == 1, rig.Cleans + " cleans");
            }

            using (var rig = new Rig(Interval10()))
            {
                rig.Svc.Pause(Hour);
                rig.Now = T0 + TimeSpan.FromMinutes(20);
                rig.Svc.Resume();
                Check("pause: resume clears it", !rig.Svc.GetStatus().Paused);
                rig.Svc.RunTick();
                rig.Now = T0 + TimeSpan.FromMinutes(29);
                rig.Svc.RunTick();
                Check("pause: no clean right after resume", rig.Cleans == 0, rig.Cleans + " cleans");
                rig.Now = T0 + TimeSpan.FromMinutes(30);
                rig.Svc.RunTick();
                Check("pause: resume re-anchors the interval", rig.Cleans == 1, rig.Cleans + " cleans");
            }

            using (var rig = new Rig(Threshold85()))
            {
                rig.Load = 95;
                rig.Svc.Pause(Hour);
                rig.Now = T0 - TimeSpan.FromDays(1);
                rig.Svc.RunTick();
                Check("pause: clock set back clamps the end", rig.Svc.GetStatus().PausedUntilUtc == rig.Now + Hour,
                    rig.Svc.GetStatus().PausedUntilUtc + "");
                rig.Now = rig.Now + Hour;
                rig.Svc.RunTick();
                Check("pause: clock set back cannot stretch it", !rig.Svc.GetStatus().Paused && rig.Cleans == 1, rig.Cleans + " cleans");
            }

            using (var rig = new Rig(Threshold85()))
            {
                rig.Svc.Pause(Hour);
                AppSettings off = Threshold85();
                off.AutoCleanEnabled = false;
                rig.Svc.Update(off);
                rig.Svc.Update(Threshold85());
                Check("pause: switching auto-clean off clears it", !rig.Svc.GetStatus().Paused);
                rig.Svc.Update(off);
                Check("pause: ignored while auto-clean is off", !rig.Svc.Pause(Hour) && !rig.Svc.GetStatus().Paused);
                Check("pause: restore ignored while auto-clean is off", !rig.Svc.RestorePause(T0 + Hour) && !rig.Svc.GetStatus().Paused);
            }

            using (var rig = new Rig(Threshold85()))
            {
                Check("pause: rejects zero length", Throws<ArgumentOutOfRangeException>(() => rig.Svc.Pause(TimeSpan.Zero)));
                Check("pause: rejects more than an hour", Throws<ArgumentOutOfRangeException>(() => rig.Svc.Pause(Hour + TimeSpan.FromSeconds(1))));
                Check("pause: restore clamps a far end to an hour", rig.Svc.RestorePause(T0 + TimeSpan.FromHours(5))
                    && rig.Svc.GetStatus().PausedUntilUtc == T0 + Hour, rig.Svc.GetStatus().PausedUntilUtc + "");
                Check("pause: restore keeps an end inside the hour", rig.Svc.RestorePause(T0 + TimeSpan.FromMinutes(15))
                    && rig.Svc.GetStatus().PausedUntilUtc == T0 + TimeSpan.FromMinutes(15));
                rig.Svc.Resume();
                Check("pause: restore of an ended pause does nothing", !rig.Svc.RestorePause(T0 - TimeSpan.FromMinutes(1)) && !rig.Svc.GetStatus().Paused);
                Check("pause: restore of none does nothing", !rig.Svc.RestorePause(null) && !rig.Svc.GetStatus().Paused);
            }

            using (var rig = new Rig(Threshold85()))
            {
                rig.Load = 95;
                rig.Svc.Pause(Hour);
                Check("pause: a manual clean still runs", rig.Svc.RunManualCleanForTests() && rig.Cleans == 1, rig.Cleans + " cleans");
            }

            var gone = new AutoCleanService(() => Snap(10), a => null, () => T0);
            gone.Update(Threshold85());
            gone.Dispose();
            Check("pause: disposed throws", Throws<ObjectDisposedException>(() => gone.Pause(Hour)));
        }

        static void PausePureTests()
        {
            AppSettings s = Threshold85();
            Check("pause: decide returns null while paused",
                AutoCleanService.Decide(s, 99, null, T0, T0 + Hour, false, 0, GateHold.None, true) == null);
            Check("pause: decide unpaused still cleans",
                AutoCleanService.Decide(s, 99, null, T0, T0 + Hour, false, 0, GateHold.None, false) == CleanReason.Threshold);
            Check("pause: clamp none", AutoCleanService.ClampPause(null, T0) == null);
            Check("pause: clamp ended", AutoCleanService.ClampPause(T0, T0) == null);
            Check("pause: clamp far", AutoCleanService.ClampPause(T0 + TimeSpan.FromDays(3), T0) == T0 + Hour);
            Check("pause: clamp inside", AutoCleanService.ClampPause(T0 + TimeSpan.FromMinutes(5), T0) == T0 + TimeSpan.FromMinutes(5));
            Check("pause: the one length offered is an hour", AutoCleanService.PauseLength == Hour);
        }

        static void StatusTests()
        {
            using (var rig = new Rig(Threshold85()))
            {
                rig.Load = 95;
                rig.After = 92;
                rig.Svc.RunTick();
                Check("status: futile clean counted", rig.Svc.GetStatus().FutileStreak == 1);
                AutoCleanStatus st = rig.Svc.GetStatus();
                Check("status: next threshold clean is the doubled cooldown on", st.NextThresholdCleanUtc == T0 + TimeSpan.FromSeconds(600)
                    && st.LastCleanUtc == T0, st.NextThresholdCleanUtc + "");
                int seen = -1;
                rig.Svc.Sampled += snap => { seen = rig.Svc.GetStatus().FutileStreak; };
                rig.Load = 50;
                rig.Now = T0 + TimeSpan.FromSeconds(2);
                rig.Svc.RunTick();
                Check("status: streak reset is visible to Sampled handlers", seen == 0, "saw " + seen);
            }
            using (var rig = new Rig(Threshold85()))
            {
                AutoCleanStatus st = rig.Svc.GetStatus();
                Check("status: nothing cleaned yet", st.NextThresholdCleanUtc == null && st.LastCleanUtc == null && st.FutileStreak == 0 && !st.Paused);
            }
        }

        static void ManualStreakTests()
        {
            using (var rig = new Rig(Threshold85()))
            {
                rig.After = 95;
                rig.Svc.RunManualCleanForTests();
                Check("manual: futile manual clean does not lengthen the wait", rig.Svc.FutileStreak == 0, "streak " + rig.Svc.FutileStreak);
            }
            using (var rig = new Rig(Threshold85()))
            {
                rig.Load = 95;
                rig.After = 92;
                rig.Svc.RunTick();
                rig.Now = T0 + TimeSpan.FromSeconds(600);
                rig.Svc.RunTick();
                Check("manual: two futile automatic cleans, streak 2", rig.Svc.FutileStreak == 2, "streak " + rig.Svc.FutileStreak);
                rig.Svc.RunManualCleanForTests();
                Check("manual: futile manual clean keeps the streak", rig.Svc.FutileStreak == 2, "streak " + rig.Svc.FutileStreak);
                rig.After = 50;
                rig.Svc.RunManualCleanForTests();
                Check("manual: clean that brings load under resets the streak", rig.Svc.FutileStreak == 0, "streak " + rig.Svc.FutileStreak);
            }
        }

        static void SettingsTests()
        {
            var s = new AppSettings { AutoCleanEnabled = true, AutoCleanPausedUntilUtc = new DateTime(2026, 3, 1, 15, 41, 0, DateTimeKind.Utc) };
            string text = SettingsStore.Serialize(s);
            AppSettings back = SettingsStore.Parse(text);
            Check("settings: pause end round-trips in UTC", back.AutoCleanPausedUntilUtc == s.AutoCleanPausedUntilUtc
                && back.AutoCleanPausedUntilUtc.Value.Kind == DateTimeKind.Utc, text);
            Check("settings: no pause writes no key", !SettingsStore.Serialize(new AppSettings()).Contains("AutoCleanPausedUntil"));
            Check("settings: garbage pause dropped", SettingsStore.Parse("AutoCleanPausedUntil=soon\n").AutoCleanPausedUntilUtc == null);
            Check("settings: local-time pause dropped", SettingsStore.Parse("AutoCleanPausedUntil=2026-03-01T15:41:00.0000000\n").AutoCleanPausedUntilUtc == null);
            Check("settings: offset pause dropped", SettingsStore.Parse("AutoCleanPausedUntil=2026-03-01T15:41:00.0000000+05:30\n").AutoCleanPausedUntilUtc == null);
            Check("settings: clone keeps the pause", s.Clone().AutoCleanPausedUntilUtc == s.AutoCleanPausedUntilUtc);
        }

        static AutoCleanStatus Status(bool paused, int streak)
        {
            return new AutoCleanStatus { Paused = paused, PausedUntilUtc = paused ? (DateTime?)Local1530() : null, FutileStreak = streak };
        }

        /// <summary>15:30 local time as UTC, so the expected "15:30" holds in any time zone.</summary>
        static DateTime Local1530() { return new DateTime(2026, 3, 1, 15, 30, 0, DateTimeKind.Local).ToUniversalTime(); }

        static void StateTests()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            Check("auto state: off wins over everything", AppText.AutoState(false, false, Status(true, 2)) == AutoCleanState.Off);
            Check("auto state: needs admin", AppText.AutoState(true, false, Status(true, 2)) == AutoCleanState.NeedsAdmin);
            Check("auto state: paused", AppText.AutoState(true, true, Status(true, 2)) == AutoCleanState.Paused);
            Check("auto state: backing off", AppText.AutoState(true, true, Status(false, 2)) == AutoCleanState.BackingOff);
            Check("auto state: armed", AppText.AutoState(true, true, Status(false, 0)) == AutoCleanState.Armed);
            Check("auto state: null status is armed", AppText.AutoState(true, true, null) == AutoCleanState.Armed);
            Check("can pause: armed and backing off only", AppText.CanPause(AutoCleanState.Armed) && AppText.CanPause(AutoCleanState.BackingOff)
                && !AppText.CanPause(AutoCleanState.Paused) && !AppText.CanPause(AutoCleanState.Off) && !AppText.CanPause(AutoCleanState.NeedsAdmin));

            Check("paused line: time", AppText.PausedLine(Status(true, 0), inv) == "Paused until 15:30", AppText.PausedLine(Status(true, 0), inv));
            Check("paused line: none", AppText.PausedLine(Status(false, 0), inv) == null && AppText.PausedLine(null, inv) == null);
            Check("tray auto label: paused", AppText.TrayAutoLabel(AutoCleanState.Paused, Status(true, 0), inv) == "Auto-clean (paused until 15:30)");
            Check("tray auto label: armed", AppText.TrayAutoLabel(AutoCleanState.Armed, Status(false, 0), inv) == "Auto-clean");
            Check("tray auto label: backing off", AppText.TrayAutoLabel(AutoCleanState.BackingOff, Status(false, 2), inv) == "Auto-clean");
            Check("tray auto label: needs admin", AppText.TrayAutoLabel(AutoCleanState.NeedsAdmin, null, inv) == "Auto-clean (paused: needs admin)");
            Check("tray items", AppText.PauseMenuText == "Pause auto-clean for 1 hour" && AppText.ResumeMenuText == "Resume auto-clean");
            Check("pause note: not paused", AppText.PauseNote(Status(false, 0), inv) == "Manual cleans still work.");
            Check("pause note: paused", AppText.PauseNote(Status(true, 0), inv) == "Paused until 15:30. Manual cleans still work.",
                AppText.PauseNote(Status(true, 0), inv));
        }

        static void BackoffWordingTests()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            DateTime now = Local1530() - TimeSpan.FromMinutes(30);
            DateTime next = Local1530() - TimeSpan.FromMinutes(10); // 15:20 local
            Check("backoff line: none", AppText.BackoffLine(0, 85) == null);
            Check("backoff line: one clean", AppText.BackoffLine(1, 85) == "Waiting longer: the last clean left load at 85% or more.");
            Check("backoff line: three cleans", AppText.BackoffLine(3, 85) == "Waiting longer: the last 3 cleans left load at 85% or more.");
            Check("backoff advice: with time", AppText.BackoffAdvice(next, now, inv)
                == "Next clean for high load after 15:20. Closing an app frees more memory than cleaning.", AppText.BackoffAdvice(next, now, inv));
            Check("backoff advice: past time omitted", AppText.BackoffAdvice(now, next, inv) == "Closing an app frees more memory than cleaning.");
            Check("backoff advice: no time omitted", AppText.BackoffAdvice(null, now, inv) == "Closing an app frees more memory than cleaning.");
            Check("cooldown: backing off", AppText.CooldownText(300, 2)
                == "at most every 5 minutes; now every 20 minutes because recent cleans did not help", AppText.CooldownText(300, 2));
            Check("cooldown: streak 0 unchanged", AppText.CooldownText(300, 0) == AppText.CooldownText(300));
            Check("duration phrase: 1 minute", AppText.DurationPhrase(TimeSpan.FromMinutes(1)) == "1 minute");
            Check("duration phrase: 20 minutes", AppText.DurationPhrase(TimeSpan.FromMinutes(20)) == "20 minutes");
            Check("duration phrase: 1 hour", AppText.DurationPhrase(TimeSpan.FromMinutes(60)) == "1 hour");
            Check("duration phrase: 1 hour 20 minutes", AppText.DurationPhrase(TimeSpan.FromMinutes(80)) == "1 hour 20 minutes");
            Check("duration phrase: 2 hours", AppText.DurationPhrase(TimeSpan.FromMinutes(120)) == "2 hours");
            Check("duration phrase: seconds", AppText.DurationPhrase(TimeSpan.FromSeconds(90)) == "90 seconds", AppText.DurationPhrase(TimeSpan.FromSeconds(90)));
            Check("backoff title", AppText.BackoffTitle(TimeSpan.FromMinutes(20))
                == "Auto-clean now waits 20 minutes between cleans for high load, because recent cleans did not help.");

            // The words can never disagree with the policy: the minutes shown are the service's own cooldown.
            var s = new AppSettings { CooldownSeconds = 300 };
            for (int n = 0; n <= 5; n++)
            {
                double minutes = AutoCleanService.ThresholdCooldown(s, n).TotalMinutes;
                string text = AppText.CooldownText(300, n);
                string want = n == 0 ? "every 5 minutes" : "now every " + AppText.DurationPhrase(TimeSpan.FromMinutes(minutes));
                Check("backoff: text matches the policy, streak " + n, text.Contains(want) && minutes <= 5 * 16, text);
            }
        }

        static void NoticeTests()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            AppSettings s = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300, IntervalMinutes = 30 };
            DateTime now = Local1530() - TimeSpan.FromMinutes(30);

            Check("bar: armed shows nothing", AppText.AutoBar(AutoCleanState.Armed, Status(false, 0), s, inv) == null);
            Check("bar: off shows nothing", AppText.AutoBar(AutoCleanState.Off, Status(true, 0), s, inv) == null);
            Check("bar: needs admin shows nothing (the admin line says it)", AppText.AutoBar(AutoCleanState.NeedsAdmin, Status(true, 0), s, inv) == null);
            AutoNotice paused = AppText.AutoBar(AutoCleanState.Paused, Status(true, 0), s, inv);
            Check("bar: paused", paused != null && paused.Title == "Auto-clean is paused until 15:30." && paused.Detail == "Manual cleans still work."
                && paused.Resume && !paused.Dismiss && paused.Glyph == "IconPause", paused == null ? "null" : paused.Title);
            AutoNotice back = AppText.AutoBar(AutoCleanState.BackingOff, Status(false, 2), s, inv);
            Check("bar: backing off", back != null && back.Title == AppText.BackoffTitle(TimeSpan.FromMinutes(20)) && back.Dismiss && !back.Resume
                && back.Glyph == "IconWarn" && back.Stroke == "Caution", back == null ? "null" : back.Title);
            Check("bar: dismissal is per streak", back != null && back.Key != AppText.AutoBar(AutoCleanState.BackingOff, Status(false, 3), s, inv).Key);

            Check("card: off shows nothing", AppText.AutoCard(AutoCleanState.Off, Status(true, 2), s, now, inv) == null);
            Check("card: armed shows nothing", AppText.AutoCard(AutoCleanState.Armed, Status(false, 0), s, now, inv) == null);
            AutoNotice admin = AppText.AutoCard(AutoCleanState.NeedsAdmin, null, s, now, inv);
            Check("card: needs admin", admin != null && admin.Title == "Paused: needs administrator rights" && !admin.Resume);
            AutoNotice cardPaused = AppText.AutoCard(AutoCleanState.Paused, Status(true, 0), s, now, inv);
            Check("card: paused", cardPaused != null && cardPaused.Title == "Paused until 15:30." && cardPaused.Resume);
            AutoCleanStatus held = Status(false, 0);
            held.Hold = HoldReason.Busy;
            AutoNotice hold = AppText.AutoCard(AutoCleanState.Armed, held, s, now, inv);
            Check("card: hold reason shown", hold != null && hold.Title == AppText.AutoHoldText(HoldReason.Busy, s));
            AutoCleanStatus pausedHeld = Status(true, 0);
            pausedHeld.Hold = HoldReason.Busy;
            Check("card: pause wins over a hold", AppText.AutoCard(AutoCleanState.Paused, pausedHeld, s, now, inv).Title == "Paused until 15:30.");
            AutoCleanStatus backing = Status(false, 2);
            backing.NextThresholdCleanUtc = Local1530() - TimeSpan.FromMinutes(10);
            AutoNotice cardBack = AppText.AutoCard(AutoCleanState.BackingOff, backing, s, now, inv);
            Check("card: backing off, two lines and a warning glyph", cardBack != null && cardBack.Title == AppText.BackoffLine(2, 85)
                && cardBack.Detail == AppText.BackoffAdvice(backing.NextThresholdCleanUtc, now, inv) && cardBack.Glyph == "IconWarn"
                && cardBack.Stroke == "Caution", cardBack == null ? "null" : cardBack.Title);
            Check("card: the accessible name has both lines", cardBack != null && cardBack.Name == cardBack.Title + " " + cardBack.Detail);

            // No exclamation marks anywhere in the new wording.
            var texts = new[]
            {
                paused.Title, paused.Detail, back.Title, admin.Title, cardPaused.Title, cardBack.Title, cardBack.Detail,
                AppText.PauseNote(Status(true, 0), inv), AppText.TrayAutoLabel(AutoCleanState.Paused, Status(true, 0), inv),
                AppText.CooldownText(300, 3), AppText.PauseMenuText, AppText.ResumeMenuText, AppText.PauseButtonText
            };
            foreach (string t in texts) Check("no exclamation: " + t, t != null && t.IndexOf('!') < 0);
        }

        /// <summary>The bar's per-refresh decision: dismissals last one back-off episode, the error slot wins.</summary>
        static void BarStepTests()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            AppSettings s = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300 };
            AutoNotice back1 = AppText.AutoBar(AutoCleanState.BackingOff, Status(false, 1), s, inv);
            AutoNotice back2 = AppText.AutoBar(AutoCleanState.BackingOff, Status(false, 2), s, inv);
            AutoNotice paused = AppText.AutoBar(AutoCleanState.Paused, Status(true, 0), s, inv);

            AutoBarStep first = AppText.NextAutoBar(back1, false, null, null);
            Check("bar step: a back-off shows and is read out", first.Show && first.Announce && first.AnnouncedKind == "backoff");
            AutoBarStep dismissed = AppText.NextAutoBar(back1, false, back1.Key, first.AnnouncedKind);
            Check("bar step: a dismissed back-off stays hidden", !dismissed.Show && dismissed.DismissedKey == back1.Key);
            AutoBarStep worse = AppText.NextAutoBar(back2, false, dismissed.DismissedKey, dismissed.AnnouncedKind);
            Check("bar step: a new streak shows again, not read out twice", worse.Show && !worse.Announce && worse.DismissedKey == null);

            // Dismiss at streak 1, load eases (streak 0, nothing to say), a later futile clean makes streak 1 again.
            AutoBarStep eased = AppText.NextAutoBar(null, false, back1.Key, "backoff");
            Check("bar step: the episode ending forgets the dismissal", !eased.Show && eased.DismissedKey == null && eased.AnnouncedKind == null);
            AutoBarStep again = AppText.NextAutoBar(back1, false, eased.DismissedKey, eased.AnnouncedKind);
            Check("bar step: the next episode shows and is read out", again.Show && again.Announce);

            AutoBarStep errorWins = AppText.NextAutoBar(back1, true, null, "backoff");
            Check("bar step: the sample error takes the slot", !errorWins.Show && !errorWins.Announce && errorWins.AnnouncedKind == "backoff");
            AutoBarStep errorKeeps = AppText.NextAutoBar(back1, true, back1.Key, "backoff");
            Check("bar step: an error does not forget the dismissal", errorKeeps.DismissedKey == back1.Key);
            AutoBarStep pausedOver = AppText.NextAutoBar(paused, false, back1.Key, "backoff");
            Check("bar step: a pause over a dismissed back-off shows", pausedOver.Show && pausedOver.Announce
                && pausedOver.AnnouncedKind == "paused" && pausedOver.DismissedKey == null);
        }

        /// <summary>WCAG 2.5.3: each auto-clean button's accessible name contains its visible label.</summary>
        static void LabelInNameTests()
        {
            foreach (bool paused in new[] { false, true })
            {
                string label = AppText.PauseRowButtonText(paused), name = AppText.PauseRowButtonName(paused);
                Check("label in name: pause row (paused " + paused + ")", name.StartsWith(label, StringComparison.Ordinal), label + " / " + name);
            }
        }

        /// <summary>The clock set back late in a pause keeps the time that was left, never a fresh hour.</summary>
        static void ClockBackLateTests()
        {
            using (var rig = new Rig(Threshold85()))
            {
                rig.Load = 95;
                rig.Svc.Pause(Hour);
                rig.Now = T0 + TimeSpan.FromMinutes(59);
                rig.Svc.RunTick();
                rig.Now = rig.Now - TimeSpan.FromDays(1);
                rig.Svc.RunTick();
                AutoCleanStatus st = rig.Svc.GetStatus();
                Check("pause: clock set back at 59 min keeps 1 minute", st.Paused && st.PausedUntilUtc == rig.Now + TimeSpan.FromMinutes(1),
                    st.PausedUntilUtc + " vs " + (rig.Now + TimeSpan.FromMinutes(1)));
                rig.Now = rig.Now + TimeSpan.FromMinutes(1);
                rig.Svc.RunTick();
                Check("pause: and then ends on time", !rig.Svc.GetStatus().Paused && rig.Cleans == 1, rig.Cleans + " cleans");
            }
            DateTime last = T0 + TimeSpan.FromMinutes(30), back = T0 - TimeSpan.FromDays(1);
            Check("pause: clock back pure, time left kept", AutoCleanService.PauseAfterClockBack(T0 + Hour, last, back) == back + TimeSpan.FromMinutes(30));
            Check("pause: clock back pure, ended pause stays ended", AutoCleanService.PauseAfterClockBack(T0, last, back) == null);
            Check("pause: clock back pure, capped at an hour", AutoCleanService.PauseAfterClockBack(T0 + TimeSpan.FromDays(2), last, back) == back + Hour);
        }

        static bool Throws<T>(Action a) where T : Exception
        {
            try { a(); }
            catch (T) { return true; }
            catch (Exception) { return false; }
            return false;
        }
    }
}
