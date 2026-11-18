// Tests for package 4A: opt-in problem notices (R6), the global Clean memory shortcut (R7), resetting settings
// (P16, reset only) and the live regions that announce status lines (U9). Pure code only: nothing here registers
// a hotkey, shows a balloon or touches the system. The hotkey binding is driven through a fake registrar.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using RamOptimiser.App;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class HotkeyResetTests
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
            HotkeyParseTests();
            HotkeyFromKeysTests();
            RecorderTests();
            HotkeyGateTests();
            HotkeyBindingTests();
            HotkeyWordingTests();
            NoticeTests();
            NoticeGateTests();
            SettingsTests();
            ResetTests();
            AnnounceTests();
            XamlTests();
            return failed;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly DateTime T0 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        static HotkeyGesture P(string text)
        {
            HotkeyGesture g;
            return HotkeyGesture.TryParse(text, out g) ? g : null;
        }

        static bool Parses(string text)
        {
            HotkeyGesture g;
            return HotkeyGesture.TryParse(text, out g);
        }

        // ------------------------------------------------------------------ R7 parsing

        static void HotkeyParseTests()
        {
            HotkeyGesture g = P("Ctrl+Shift+M");
            Check("hotkey: parse ctrl+shift+m", g != null && g.Modifiers == (HotkeyGesture.ModControl | HotkeyGesture.ModShift) && g.Key == 0x4D,
                g == null ? "null" : g.Modifiers + "/" + g.Key);
            g = P("shift+CTRL+m");
            Check("hotkey: canonical format", g != null && g.Format() == "Ctrl+Shift+M", g == null ? "null" : g.Format());
            g = P(" alt + control + shift + f5 ");
            Check("hotkey: spaces, control and order", g != null && g.Format() == "Ctrl+Alt+Shift+F5", g == null ? "null" : g.Format());
            Check("hotkey: one modifier refused", !Parses("Ctrl+M") && !Parses("Shift+F5"));
            Check("hotkey: plain key refused", !Parses("A") && !Parses("F5"));
            Check("hotkey: win refused", !Parses("Win+Ctrl+M") && !Parses("Ctrl+Windows+M") && !Parses("Ctrl+Alt+Win+M"));
            Check("hotkey: f12 refused", !Parses("Ctrl+Alt+F12"));
            g = P("Ctrl+Alt+F24");
            Check("hotkey: f24 ok", g != null && g.Key == 0x87, g == null ? "null" : g.Key.ToString(Inv));
            Check("hotkey: f13 ok", P("Ctrl+Shift+F13") != null && P("Ctrl+Shift+F13").Key == 0x7C);
            Check("hotkey: f11 ok", P("Ctrl+Shift+F11") != null && P("Ctrl+Shift+F11").Key == 0x7A);
            Check("hotkey: f1 ok", P("Ctrl+Shift+F1") != null && P("Ctrl+Shift+F1").Key == 0x70);
            Check("hotkey: f25 refused", !Parses("Ctrl+Alt+F25"));
            Check("hotkey: f0 refused", !Parses("Ctrl+Alt+F0") && !Parses("Ctrl+Alt+F01") && !Parses("Ctrl+Alt+F+1"));
            g = P("Ctrl+Alt+7");
            Check("hotkey: digit ok", g != null && g.Key == 0x37);
            Check("hotkey: tab refused", !Parses("Ctrl+Alt+Tab"));
            Check("hotkey: no key refused", !Parses("Ctrl+Alt"));
            Check("hotkey: two keys refused", !Parses("Ctrl+Alt+M+X"));
            Check("hotkey: empty token refused", !Parses("Ctrl++M") && !Parses("+Ctrl+Alt+M") && !Parses("Ctrl+Alt+M+"));
            Check("hotkey: repeated modifier refused", !Parses("Ctrl+Ctrl+M") && !Parses("Ctrl+Control+M"));
            Check("hotkey: long input refused", !Parses("Ctrl+Alt+" + new string('M', 200)));
            Check("hotkey: unicode letter refused", !Parses("Ctrl+Alt+" + char.ConvertFromUtf32(0xC9)));
            Check("hotkey: lowercase f-key token ok", P("ctrl+alt+f1") != null && P("ctrl+alt+f1").Format() == "Ctrl+Alt+F1");
            HotkeyGesture none;
            Check("hotkey: empty means none", HotkeyGesture.TryParse("", out none) && none == null);
            Check("hotkey: blank means none", HotkeyGesture.TryParse("   ", out none) && none == null);
            Check("hotkey: null means none", HotkeyGesture.TryParse(null, out none) && none == null);
            g = P("Ctrl+Shift+M");
            Check("hotkey: register flags add no-repeat", g.RegisterFlags == (HotkeyGesture.ModControl | HotkeyGesture.ModShift | HotkeyGesture.ModNoRepeat));
            Check("hotkey: modifier values match RegisterHotKey", HotkeyGesture.ModAlt == 1 && HotkeyGesture.ModControl == 2
                && HotkeyGesture.ModShift == 4 && HotkeyGesture.ModNoRepeat == 0x4000);
            Check("hotkey: altgr risk", P("Ctrl+Alt+E").IsAltGrRisk && !P("Ctrl+Shift+E").IsAltGrRisk
                && !P("Ctrl+Alt+Shift+E").IsAltGrRisk && P("Ctrl+Alt+2").IsAltGrRisk && !P("Ctrl+Alt+F2").IsAltGrRisk);
            Check("hotkey: equal gestures", P("Ctrl+Shift+M").Equals(P("shift+ctrl+m")) && !P("Ctrl+Shift+M").Equals(P("Ctrl+Alt+M")));
        }

        static void HotkeyFromKeysTests()
        {
            HotkeyGesture g = HotkeyGesture.FromKeys(true, false, true, false, 0x4D);
            Check("hotkey: from keys matches parse", g != null && g.Equals(P("Ctrl+Shift+M")) && g.Format() == "Ctrl+Shift+M");
            Check("hotkey: from keys with win is null", HotkeyGesture.FromKeys(true, false, true, true, 0x4D) == null);
            Check("hotkey: from keys one modifier is null", HotkeyGesture.FromKeys(true, false, false, false, 0x4D) == null);
            Check("hotkey: from keys f12 is null", HotkeyGesture.FromKeys(true, true, false, false, 0x7B) == null);
            Check("hotkey: from keys tab is null", HotkeyGesture.FromKeys(true, true, false, false, 0x09) == null);
            Check("hotkey: from keys negative is null", HotkeyGesture.FromKeys(true, true, false, false, -1) == null);
            Check("hotkey: from keys three modifiers", HotkeyGesture.FromKeys(true, true, true, false, 0x70).Format() == "Ctrl+Alt+Shift+F1");
            Check("hotkey: from keys round trip", P(HotkeyGesture.FromKeys(false, true, true, false, 0x87).Format()).Equals(HotkeyGesture.FromKeys(false, true, true, false, 0x87)));
        }

        static HotkeyKeyAction Key(bool ctrl, bool alt, bool shift, int vk)
        {
            HotkeyGesture g;
            return HotkeyRecorder.Decide(ctrl, alt, shift, false, vk, out g);
        }

        static void RecorderTests()
        {
            HotkeyGesture g;
            Check("recorder: valid combination records", HotkeyRecorder.Decide(true, false, true, false, 0x4D, out g) == HotkeyKeyAction.Record
                && g != null && g.Format() == "Ctrl+Shift+M");
            Check("recorder: modifier alone waits", Key(true, false, false, 0x11) == HotkeyKeyAction.Ignore && Key(true, false, true, 0x10) == HotkeyKeyAction.Ignore
                && Key(false, true, false, 0xA4) == HotkeyKeyAction.Ignore && Key(false, false, false, 0x5B) == HotkeyKeyAction.Ignore);
            Check("recorder: no key code waits", Key(false, false, false, 0) == HotkeyKeyAction.Ignore);
            Check("recorder: delete removes", Key(false, false, false, 0x2E) == HotkeyKeyAction.Remove);
            Check("recorder: backspace removes", Key(false, false, false, 0x08) == HotkeyKeyAction.Remove);
            Check("recorder: esc cancels", Key(false, false, false, 0x1B) == HotkeyKeyAction.Cancel);
            Check("recorder: tab moves on", Key(false, false, false, 0x09) == HotkeyKeyAction.PassThrough
                && Key(false, false, true, 0x09) == HotkeyKeyAction.PassThrough && Key(true, false, false, 0x09) == HotkeyKeyAction.PassThrough);
            Check("recorder: alt+f4 still closes", Key(false, true, false, 0x73) == HotkeyKeyAction.PassThrough);
            Check("recorder: one modifier is invalid", Key(true, false, false, 0x4D) == HotkeyKeyAction.Invalid);
            Check("recorder: plain letter is invalid", Key(false, false, false, 0x4D) == HotkeyKeyAction.Invalid);
            Check("recorder: f12 is invalid", Key(true, true, false, 0x7B) == HotkeyKeyAction.Invalid);
            Check("recorder: ctrl+delete is invalid, not remove", Key(true, false, false, 0x2E) == HotkeyKeyAction.Invalid);
            Check("recorder: win combination is invalid", HotkeyRecorder.Decide(true, false, true, true, 0x4D, out g) == HotkeyKeyAction.Invalid && g == null);
        }

        static void HotkeyGateTests()
        {
            TimeSpan floor = HotkeyGate.Floor;
            Check("hotkey gate: floor is the settings cooldown floor", floor == TimeSpan.FromSeconds(AppSettings.MinCooldownSeconds) && floor == TimeSpan.FromSeconds(60));
            Check("hotkey gate: no clean yet allows", HotkeyGate.Allow(null, T0, floor));
            Check("hotkey gate: 30 s denies", !HotkeyGate.Allow(T0, T0.AddSeconds(30), floor));
            Check("hotkey gate: 59 s denies", !HotkeyGate.Allow(T0, T0.AddSeconds(59), floor));
            Check("hotkey gate: 60 s allows", HotkeyGate.Allow(T0, T0.AddSeconds(60), floor));
            Check("hotkey gate: clock set back allows", HotkeyGate.Allow(T0.AddMinutes(5), T0, floor));
            Check("hotkey gate: latest of two", HotkeyGate.Latest(T0, T0.AddSeconds(5)) == T0.AddSeconds(5)
                && HotkeyGate.Latest(T0.AddSeconds(5), T0) == T0.AddSeconds(5) && HotkeyGate.Latest(null, T0) == T0
                && HotkeyGate.Latest(T0, null) == T0 && HotkeyGate.Latest(null, null) == null);
            string line = AppText.ShortcutSkipped;
            Check("hotkey gate: skip line shown after a skip", HotkeyGate.SkipLine(T0, T0.AddSeconds(-20), T0.AddSeconds(10)) == line);
            Check("hotkey gate: skip line gone after a minute", HotkeyGate.SkipLine(T0, T0.AddSeconds(-20), T0.AddSeconds(61)) == null);
            Check("hotkey gate: skip line gone after a later clean", HotkeyGate.SkipLine(T0, T0.AddSeconds(5), T0.AddSeconds(10)) == null);
            Check("hotkey gate: no skip, no line", HotkeyGate.SkipLine(null, T0, T0) == null);
        }

        /// <summary>Stands in for RegisterHotKey: a set of taken gestures and a log of calls.</summary>
        sealed class FakeRegistrar : IHotkeyRegistrar
        {
            public readonly List<string> Taken = new List<string>();
            public int ErrorCode = 1409;
            public string Registered;
            public int Registers, Unregisters;

            public bool TryRegister(HotkeyGesture g, out int error)
            {
                Registers++;
                if (Registered != null) { error = 1409; return false; } // the listener holds one id: unregister first
                if (Taken.Contains(g.Format())) { error = ErrorCode; return false; }
                Registered = g.Format();
                error = 0;
                return true;
            }

            public void Unregister() { Unregisters++; Registered = null; }
        }

        static void HotkeyBindingTests()
        {
            var none = new HotkeyBinding(new FakeRegistrar());
            none.Start(null);
            Check("binding: start with none", none.State == HotkeyState.None && none.Current == null && none.Active == null);

            var reg = new FakeRegistrar();
            var b = new HotkeyBinding(reg);
            b.Start(P("Ctrl+Shift+M"));
            Check("binding: start registers", b.State == HotkeyState.Registered && reg.Registered == "Ctrl+Shift+M");
            Check("binding: active gesture", b.Active != null && b.Active.Format() == "Ctrl+Shift+M");
            b.Start(P("Ctrl+Shift+K"));
            Check("binding: start again replaces the registration", b.State == HotkeyState.Registered && reg.Registered == "Ctrl+Shift+K");
            b.Start(P("Ctrl+Shift+M"));

            reg.Taken.Add("Ctrl+Alt+K");
            HotkeyResult r = b.Record(P("Ctrl+Alt+K"));
            Check("binding: taken is not saved", !r.Save && r.Error == 1409);
            Check("binding: taken keeps previous registered", b.State == HotkeyState.Registered && reg.Registered == "Ctrl+Shift+M"
                && b.Current.Format() == "Ctrl+Shift+M", reg.Registered);
            Check("binding: taken message", r.Message == "Ctrl+Alt+K is already used by another app. Choose another shortcut.", r.Message);

            reg.Taken.Add("Ctrl+Alt+J");
            reg.ErrorCode = 5;
            r = b.Record(P("Ctrl+Alt+J"));
            Check("binding: refused message", !r.Save && r.Message == "Windows did not accept this shortcut (error 5).", r.Message);

            r = b.Record(P("Ctrl+Shift+L"));
            Check("binding: new shortcut saved", r.Save && r.Message == null && b.State == HotkeyState.Registered && reg.Registered == "Ctrl+Shift+L");
            r = b.Record(P("Ctrl+Alt+E"));
            Check("binding: altgr warning after success", r.Save && r.Message == AppText.HotkeyAltGrWarning, r.Message);
            int before = reg.Registers;
            r = b.Record(P("Ctrl+Alt+E"));
            Check("binding: same shortcut again changes nothing", r.Save && reg.Registers == before && reg.Registered == "Ctrl+Alt+E");

            r = b.Remove();
            Check("binding: remove unregisters and saves", r.Save && b.State == HotkeyState.None && reg.Registered == null && b.Current == null);
            Check("binding: active null after remove", b.Active == null);

            var taken = new FakeRegistrar();
            taken.Taken.Add("Ctrl+Shift+M");
            var t = new HotkeyBinding(taken);
            t.Start(P("Ctrl+Shift+M"));
            Check("binding: start taken", t.State == HotkeyState.Taken && t.Error == 1409 && t.Active == null && t.Current != null);
            Check("binding: start taken message", t.Message == "Ctrl+Shift+M is already used by another app. Choose another shortcut.", t.Message);
            taken.Taken.Clear();
            r = t.Record(P("Ctrl+Shift+N"));
            Check("binding: record from taken", r.Save && t.State == HotkeyState.Registered && t.Message == null);

            var off = new HotkeyBinding(null);
            off.Start(P("Ctrl+Shift+M"));
            Check("binding: not elevated is inactive", off.State == HotkeyState.Inactive && off.Active == null);
            r = off.Record(P("Ctrl+Shift+N"));
            Check("binding: not elevated still saves", r.Save && off.State == HotkeyState.Inactive && off.Current.Format() == "Ctrl+Shift+N");
            r = off.Remove();
            Check("binding: not elevated remove", r.Save && off.State == HotkeyState.None);

            var stop = new FakeRegistrar();
            var s = new HotkeyBinding(stop);
            s.Start(P("Ctrl+Shift+M"));
            s.Stop();
            Check("binding: stop unregisters", stop.Registered == null && s.Active == null);
            s.Stop();
            Check("binding: stop twice is harmless", stop.Registered == null);
        }

        static void HotkeyWordingTests()
        {
            Check("hotkey text: invalid", AppText.HotkeyInvalid == "Use two of Ctrl, Alt and Shift with a letter, digit or F key (not F12).");
            Check("hotkey text: altgr", AppText.HotkeyAltGrWarning == "On some keyboards Ctrl+Alt is AltGr, used to type characters like the euro sign. Ctrl+Shift avoids that.");
            Check("hotkey text: skipped", AppText.ShortcutSkipped == "Shortcut ignored: the last clean was less than a minute ago.");
            Check("hotkey text: row note elevated", AppText.HotkeyRowNote(true) == "Works anywhere in Windows while RAM Optimiser runs as administrator. Use two of Ctrl, Alt and Shift with a letter, digit or F key.");
            Check("hotkey text: row note not elevated", AppText.HotkeyRowNote(false).EndsWith(" Not active: RAM Optimiser is not running as administrator."));
            Check("hotkey text: error codes", AppText.HotkeyError(P("Ctrl+Shift+M"), 1409) == "Ctrl+Shift+M is already used by another app. Choose another shortcut."
                && AppText.HotkeyError(P("Ctrl+Shift+M"), 87) == "Windows did not accept this shortcut (error 87).");
            Check("clean tip: shortcut when it works", AppText.CleanButtonTip("Clean memory", true, false, MemoryViewState.Live, P("Ctrl+Shift+M")) == "Clean memory (Ctrl+Shift+M)");
            Check("clean tip: no shortcut, no tip", AppText.CleanButtonTip("Clean memory", true, false, MemoryViewState.Live, null) == null);
            Check("clean tip: a disabled reason wins", AppText.CleanButtonTip("Clean memory", false, false, MemoryViewState.Live, P("Ctrl+Shift+M")) == AppText.CleanNeedsAdmin
                && AppText.CleanButtonTip("Clean memory", true, false, MemoryViewState.FailedNoData, P("Ctrl+Shift+M")) == AppText.CleanNeedsData);
            Check("clean tip: none while cleaning", AppText.CleanButtonTip("Cleaning...", true, true, MemoryViewState.Live, P("Ctrl+Shift+M")) == null);
            Check("clean tip: power clean label", AppText.CleanButtonTip("Power Clean", true, false, MemoryViewState.Live, P("Ctrl+Alt+F5")) == "Power Clean (Ctrl+Alt+F5)");
            ActivityEvent e = AppText.ShortcutSkippedEvent();
            Check("hotkey event: skipped", e.Kind == "hotkey-skipped" && e.Text == AppText.ShortcutSkipped);
            e = AppText.ShortcutCleanEvent(P("Ctrl+Shift+M"));
            Check("hotkey event: clean", e.Kind == "hotkey-clean" && e.Text == "Clean started with the shortcut Ctrl+Shift+M.", e.Text);
            Check("hotkey event: record", ActivityLog.EventRecord(T0, e).Contains("\"event\":\"hotkey-clean\""));
        }

        // ------------------------------------------------------------------ R6 notices

        static MemorySnapshot Snap(uint load) { return new MemorySnapshot { TotalPhys = 100, AvailablePhys = 100 - load, LoadPercent = load }; }

        static CleanReport Report(params AreaOutcome[] outcomes)
        {
            var r = new CleanReport { Before = Snap(90), After = Snap(80) };
            foreach (AreaOutcome o in outcomes)
                r.Results.Add(new AreaResult
                {
                    Area = Areas.WorkingSets, Ok = o == AreaOutcome.Ok || o == AreaOutcome.Partial, Partial = o == AreaOutcome.Partial,
                    Skipped = o == AreaOutcome.Skipped, Message = o == AreaOutcome.Ok ? null : "access is denied"
                });
            return r;
        }

        static void NoticeTests()
        {
            DateTime at = new DateTime(2026, 3, 1, 14, 32, 0, DateTimeKind.Local);
            CleanReport bad = Report(AreaOutcome.Ok, AreaOutcome.Failed, AreaOutcome.Failed);
            Check("notice: none when off", Notices.ForClean(false, false, CleanReason.Threshold, bad, null, at, Inv) == null);
            Check("notice: none for a manual clean", Notices.ForClean(true, false, CleanReason.Manual, bad, null, at, Inv) == null);
            Check("notice: none when all areas ok", Notices.ForClean(true, false, CleanReason.Threshold, Report(AreaOutcome.Ok, AreaOutcome.Skipped), null, at, Inv) == null);
            Check("notice: none while window showing", Notices.ForClean(true, true, CleanReason.Interval, bad, null, at, Inv) == null);
            Notice n = Notices.ForClean(true, false, CleanReason.Threshold, bad, null, at, Inv);
            Check("notice: failed areas", n != null && n.Kind == NoticeKind.CleanFailed && n.Title == "Auto-clean had a problem"
                && n.Text == "At 14:32, 2 areas failed. Open RAM Optimiser to see which.", n == null ? "null" : n.Text);
            n = Notices.ForClean(true, false, CleanReason.Interval, Report(AreaOutcome.Ok, AreaOutcome.Partial), null, at, Inv);
            Check("notice: partial counts", n != null && n.Text == "At 14:32, 1 area partly failed. Open RAM Optimiser to see which.", n == null ? "null" : n.Text);
            n = Notices.ForClean(true, false, CleanReason.Threshold, Report(AreaOutcome.Failed), null, at, Inv);
            Check("notice: one area failed", n != null && n.Text == "At 14:32, 1 area failed. Open RAM Optimiser to see which.", n == null ? "null" : n.Text);
            n = Notices.ForClean(true, false, CleanReason.Threshold, null, new InvalidOperationException("x"), at, Inv);
            Check("notice: did not finish", n != null && n.Kind == NoticeKind.CleanFailed && n.Text == "At 14:32 the clean did not finish. Open RAM Optimiser for details.", n == null ? "null" : n.Text);
            Check("notice: an error wins over a report", Notices.ForClean(true, false, CleanReason.Threshold, Report(AreaOutcome.Ok), new IOException("x"), at, Inv) != null);
            CleanReport timed = Report(AreaOutcome.Ok);
            timed.Stopped = CleanStop.TimedOut;
            n = Notices.ForClean(true, false, CleanReason.Threshold, timed, null, at, Inv);
            Check("notice: time limit counts as did not finish", n != null && n.Text.Contains("did not finish"));
            CleanReport busy = new CleanReport { Before = Snap(90), After = Snap(90), Stopped = CleanStop.Busy };
            Check("notice: none for a busy skip", Notices.ForClean(true, false, CleanReason.Threshold, busy, null, at, Inv) == null);
            Check("notice: text has no names or paths", n.Text.IndexOf('\\') < 0 && n.Text.IndexOf("access", StringComparison.Ordinal) < 0
                && Notices.ForClean(true, false, CleanReason.Threshold, bad, null, at, Inv).Text.IndexOf("access", StringComparison.Ordinal) < 0);

            int max = AutoCleanPolicy.MaxBackoffSteps;
            n = Notices.ForStreak(true, false, max - 1, max, 85);
            Check("notice: not helping only at max step", n != null && n.Kind == NoticeKind.NotHelping && n.Title == "Auto-clean is not helping"
                && n.Text == "The last 4 cleans left load at 85% or more. Closing an app will free more memory than cleaning.", n == null ? "null" : n.Text);
            Check("notice: not helping not repeated at max", Notices.ForStreak(true, false, max, max, 85) == null);
            Check("notice: not helping below max", Notices.ForStreak(true, false, 1, 2, 85) == null);
            Check("notice: not helping off", Notices.ForStreak(false, false, max - 1, max, 85) == null);
            Check("notice: not helping window showing", Notices.ForStreak(true, true, max - 1, max, 85) == null);
            Check("notice: re-armed after a reset", Notices.ForStreak(true, false, 0, max, 85) != null);

            ActivityEvent e = Notices.ShownEvent(n);
            Check("notice event: shown", e.Kind == "notice-shown" && e.Text == "Notification shown: Auto-clean is not helping.", e.Text);
            Check("notice event: record", ActivityLog.EventRecord(T0, e).Contains("\"event\":\"notice-shown\""));
        }

        static void NoticeGateTests()
        {
            Check("notice gate: once an hour, null", NoticeGate.Allow(null, T0));
            Check("notice gate: once an hour, 10 min", !NoticeGate.Allow(T0, T0.AddMinutes(10)));
            Check("notice gate: once an hour, 59 min", !NoticeGate.Allow(T0, T0.AddMinutes(59)));
            Check("notice gate: once an hour, 61 min", NoticeGate.Allow(T0, T0.AddMinutes(61)));
            Check("notice gate: once an hour, 60 min", NoticeGate.Allow(T0, T0.AddMinutes(60)));
            Check("notice gate: clock set back", NoticeGate.Allow(T0.AddMinutes(30), T0));
        }

        // ------------------------------------------------------------------ settings

        static void SettingsTests()
        {
            var d = new AppSettings();
            Check("settings: notify off by default", !d.NotifyProblems);
            Check("settings: hotkey default none", d.CleanHotkey == "");
            var s = new AppSettings { NotifyProblems = true, CleanHotkey = "shift+ctrl+m" };
            string text = SettingsStore.Serialize(s);
            Check("settings: notify written", text.Contains("NotifyProblems=true\r\n"));
            Check("settings: hotkey written canonical", text.Contains("CleanHotkey=Ctrl+Shift+M\r\n"), text);
            AppSettings back = SettingsStore.Parse(text);
            Check("settings: notify round trip", back.NotifyProblems);
            Check("settings: hotkey round trip", back.CleanHotkey == "Ctrl+Shift+M", back.CleanHotkey);
            Check("settings: notify bad value keeps default", !SettingsStore.Parse("NotifyProblems=maybe").NotifyProblems);
            foreach (string bad in new[] { "A", "Win+Ctrl+M", "Ctrl+Alt+F12", "Ctrl+M", "Ctrl+Alt+M+X", "Ctrl+Alt+Delete" })
                Check("settings: invalid hotkey loads as none (" + bad + ")", SettingsStore.Parse("CleanHotkey=" + bad).CleanHotkey == "");
            Check("settings: empty hotkey loads as none", SettingsStore.Parse("CleanHotkey=").CleanHotkey == "");
            Check("settings: hotkey read canonical", SettingsStore.Parse("cleanhotkey = alt+ctrl+f5").CleanHotkey == "Ctrl+Alt+F5");
            Check("settings: invalid hotkey in memory normalizes to none", new AppSettings { CleanHotkey = "Win+M" }.Normalized().CleanHotkey == "");
            Check("settings: null hotkey in memory normalizes to none", new AppSettings { CleanHotkey = null }.Normalized().CleanHotkey == "");
            Check("settings: none written empty", SettingsStore.Serialize(d).Contains("CleanHotkey=\r\n"));
            AppSettings merged = SettingsMerge.Merge(d, d.Clone(), SettingsStore.Parse("CleanHotkey=Ctrl+Alt+K\r\nNotifyProblems=yes"));
            Check("settings: hand edit merges hotkey and notify", merged.CleanHotkey == "Ctrl+Alt+K" && merged.NotifyProblems);
        }

        static void ResetTests()
        {
            var current = new AppSettings
            {
                StartWithWindows = true, ThresholdPercent = 70, IntervalMinutes = 30, Theme = ThemeMode.Dark, AutoCleanEnabled = true,
                PowerClean = true, PowerServices = new List<string>(), NotifyProblems = true, CleanHotkey = "Ctrl+Shift+M",
                CloseToTray = false, ShowTrayPercent = false, CooldownSeconds = 900, Areas = Areas.WorkingSets,
                AutoCleanPausedUntilUtc = T0
            };
            AppSettings r = AppSettings.ResetDefaults(current);
            Check("reset: defaults except start with windows", r.StartWithWindows && r.ThresholdPercent == 85 && r.IntervalMinutes == 0
                && r.Theme == ThemeMode.System && r.CooldownSeconds == AppSettings.DefaultCooldownSeconds && r.Areas == Areas.Default
                && r.CloseToTray && r.ShowTrayPercent);
            Check("reset: turns auto-clean and power clean off", !r.AutoCleanEnabled && !r.PowerClean && r.AutoCleanPausedUntilUtc == null);
            Check("reset: clears hotkey and notices", r.CleanHotkey == "" && !r.NotifyProblems);
            Check("reset: power services back to suggested", string.Join(",", r.PowerServices.ToArray()) == string.Join(",", ServiceCatalog.SuggestedNames().ToArray()));
            Check("reset: current left as it was", current.ThresholdPercent == 70 && current.CleanHotkey == "Ctrl+Shift+M" && current.NotifyProblems);
            Check("reset: result is a new object", !ReferenceEquals(r, current) && !ReferenceEquals(r.PowerServices, current.PowerServices));
            AppSettings plain = AppSettings.ResetDefaults(null);
            Check("reset: null current is plain defaults", SettingsStore.Serialize(plain) == SettingsStore.Serialize(new AppSettings()));
            var off = new AppSettings { StartWithWindows = false, ThresholdPercent = 60 };
            Check("reset: start with windows off stays off", !AppSettings.ResetDefaults(off).StartWithWindows);

            Check("reset text: done", AppText.ResetDone(false) == "Settings were reset.");
            Check("reset text: services stay stopped", AppText.ResetDone(true) == "Settings were reset. Services stopped by Power Clean stay stopped until you restore them or quit.");
            Check("reset text: confirm", AppText.ResetConfirm == "Reset all settings? This cannot be undone.");
            Check("reset text: row note", AppText.ResetRowNote == "Puts every setting back to its default and turns auto-clean off. Start with Windows is left as it is.");
            ActivityEvent e = AppText.ResetEvent();
            Check("reset event", e.Kind == "settings-reset" && e.Text == "Settings were reset to their defaults.");
        }

        // ------------------------------------------------------------------ U9 announcements

        static void AnnounceTests()
        {
            Check("announce: first time", AnnounceGate.ShouldAnnounce(null, "Saved."));
            Check("announce: same text silent", !AnnounceGate.ShouldAnnounce("Saved.", "Saved."));
            Check("announce: changed text", AnnounceGate.ShouldAnnounce("Saved.", "Not saved."));
            Check("announce: empty never", !AnnounceGate.ShouldAnnounce(null, "") && !AnnounceGate.ShouldAnnounce("x", null));
            Check("announce: after reset announces again", AnnounceGate.ShouldAnnounce(null, "Saved."));
            Check("announce: case is a change", AnnounceGate.ShouldAnnounce("saved.", "Saved."));
            Check("search count: zero", AppText.SearchCount(0) == "Nothing matches");
            Check("search count: one", AppText.SearchCount(1) == "1 shown");
            Check("search count: many", AppText.SearchCount(12) == "12 shown");
            Check("search count: shown only while searching or filtering", !AppText.SearchCountVisible("", 0) && !AppText.SearchCountVisible(null, 0)
                && AppText.SearchCountVisible("edge", 0) && AppText.SearchCountVisible("", 2) && !AppText.SearchCountVisible("  ", 0));
        }

        // ------------------------------------------------------------------ XAML and wiring (static checks)

        static string AppDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetFullPath(Path.Combine(dir, Path.Combine("..", Path.Combine("src", "App"))));
        }

        const string AppNs = "clr-namespace:RamOptimiser.App;assembly=RamOptimiser";

        static void XamlTests()
        {
            string app = AppDir();
            string xamlPath = Path.Combine(app, Path.Combine("Xaml", "MainWindow.xaml"));
            if (!File.Exists(xamlPath)) { Check("4A: MainWindow.xaml found", false, xamlPath); return; }
            var doc = new XmlDocument();
            doc.Load(xamlPath);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
            foreach (string name in new[] { "SampleError", "RestartError", "SaveError", "StartupError", "StartupEmpty", "SearchCount",
                         "StartupSummary", "PowerStatus", "HotkeyMessage", "ResetStatus" })
            {
                var e = doc.SelectSingleNode("//*[@x:Name='" + name + "']", ns) as XmlElement;
                if (e == null) { Check("status lines are live regions: " + name + " exists", false); continue; }
                XmlElement region = IsLive(e) ? e : e.ParentNode as XmlElement;
                Check("status lines are live regions: " + name, region != null && IsLive(region),
                    region == null ? "no parent" : region.LocalName);
            }
            Check("errors that need action are assertive", Setting(doc, ns, "RestartError") == "Assertive" && Setting(doc, ns, "SaveError") == "Assertive");
            Check("other status lines are polite", Setting(doc, ns, "StartupError") == "Polite" && Setting(doc, ns, "SearchCount") == "Polite"
                && Setting(doc, ns, "SampleError") == "Polite" && Setting(doc, ns, "PowerStatus") == "Polite" && Setting(doc, ns, "HotkeyMessage") == "Polite");
            var sample = doc.SelectSingleNode("//*[@x:Name='SampleError']", ns) as XmlElement;
            Check("sample error keeps the info bar look", sample != null && sample.GetAttribute("Style") == "{StaticResource InfoBar}");

            foreach (string[] c in new[]
            {
                new[] { "NotifyCheck", "Notify me when auto-clean has a problem" }, new[] { "HotkeyBox", "Shortcut for Clean memory" },
                new[] { "HotkeyRemove", "Remove shortcut" }, new[] { "ResetButton", "Reset settings" }, new[] { "ResetYes", "Yes, reset settings" },
                new[] { "ResetNo", "Cancel reset" }
            })
            {
                var e = doc.SelectSingleNode("//*[@x:Name='" + c[0] + "']", ns) as XmlElement;
                Check("4A control " + c[0] + " named", e != null && e.GetAttribute("AutomationProperties.Name") == c[1],
                    e == null ? "missing" : e.GetAttribute("AutomationProperties.Name"));
            }
            var box = doc.SelectSingleNode("//*[@x:Name='HotkeyBox']", ns) as XmlElement;
            Check("hotkey box is read-only with a None placeholder", box != null && box.GetAttribute("IsReadOnly") == "True" && box.GetAttribute("Tag") == "None"
                && box.GetAttribute("AutomationProperties.HelpText") == "Press a key combination. Press Delete to remove the shortcut.");
            var notify = doc.SelectSingleNode("//*[@x:Name='NotifyCheck']", ns) as XmlElement;
            Check("notify switch uses the switch style", notify != null && notify.GetAttribute("Style") == "{StaticResource SwitchStyle}");
            var confirm = doc.SelectSingleNode("//*[@x:Name='ResetConfirm']", ns) as XmlElement;
            Check("reset confirmation starts hidden", confirm != null && confirm.GetAttribute("Visibility") == "Collapsed");

            string tray = File.ReadAllText(Path.Combine(app, "TrayController.cs"));
            Check("tray: old no-balloons rule replaced", !tray.Contains("Never shows balloons.") && tray.Contains("BalloonTipClicked")
                && tray.Contains("ToolTipIcon.Warning") && tray.Contains("ShortcutKeyDisplayString"));
            string shell = File.ReadAllText(Path.Combine(app, "AppShell.cs"));
            Check("shell: window shortcuts skip the recorder", shell.Contains("if (ReferenceEquals(Keyboard.FocusedElement, hotkeyBox)) return;"));
            Check("shell: hotkey gate guards the shortcut", shell.Contains("HotkeyGate.Allow("));
            Check("shell: notices pass the gate", shell.Contains("NoticeGate.Allow("));
            Check("shell: listener only when elevated", shell.Contains("elevated ? CreateHotkeyListener() : null"));
            Check("shell: listener disposed on exit and in emergency cleanup", Count(shell, "StopHotkey()") >= 3);
            string listener = File.ReadAllText(Path.Combine(app, "HotkeyListener.cs"));
            Check("listener: message-only window", listener.Contains("new IntPtr(-3)") && listener.Contains("0x0312"));
            Check("listener: unregisters before dispose", listener.Contains("UnregisterHotKey"));
        }

        static int Count(string s, string part)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
            return n;
        }

        static string Setting(XmlDocument doc, XmlNamespaceManager ns, string name)
        {
            var e = doc.SelectSingleNode("//*[@x:Name='" + name + "']", ns) as XmlElement;
            if (e == null) return null;
            XmlElement region = IsLive(e) ? e : e.ParentNode as XmlElement;
            return region == null ? null : region.GetAttribute("AutomationProperties.LiveSetting");
        }

        static bool IsLive(XmlElement e)
        {
            return e.LocalName == "LiveRegion" && e.NamespaceURI == AppNs && e.HasAttribute("AutomationProperties.LiveSetting");
        }
    }
}
