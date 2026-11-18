// Package 5A: the Memory tab's top apps by private memory (R5), row tooltips (U17), discoverable shortcuts (U16),
// the commit warning (U15), reachable lock explanations (U21), the will-a-clean-help line (R1 wiring), per-area
// timing in the details (E8 wiring), and the info-tinted auto-clean bar that says where a pause came from (2A).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using RamOptimiser.App;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class MemoryTabTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        const ulong MB = 1024UL * 1024, GB = 1024UL * MB;

        public static int Run()
        {
            TopAppTests();
            CommitWarningTests();
            ShortcutTests();
            LockedAreaTests();
            AdviceTests();
            TimingTests();
            PauseOriginTests();
            XamlTests();
            return failed;
        }

        // ---------------------------------------------------------------- R5, U17

        static ProcessSample P(string name, ulong priv, ulong ws)
        {
            return new ProcessSample { Name = name, Private = priv, WorkingSet = ws };
        }

        static void TopAppTests()
        {
            List<AppMemory> ranked = AppMemoryList.Aggregate(new[] { P("a", 100, 900), P("b", 500, 10) }, 5, AppSort.Private);
            Check("top apps: ranked by private bytes", ranked.Count == 2 && ranked[0].Name == "b" && ranked[1].Name == "a");
            List<AppMemory> grouped = AppMemoryList.Aggregate(new[] { P("msedge", 300, 30), P("MSEDGE", 200, 20), P("code", 400, 1) }, 5, AppSort.Private);
            Check("top apps: grouped sums both figures", grouped.Count == 2 && grouped[0].Name == "msedge" && grouped[0].PrivateBytes == 500
                && grouped[0].Bytes == 50 && grouped[0].Processes == 2);
            List<AppMemory> capped = AppMemoryList.Aggregate(new[] { P("Idle", 9999, 9999), P("", 50, 50), P("x", 3, 3), P("y", 2, 2), P("z", 1, 1) }, 2, AppSort.Private);
            Check("top apps: Idle and empty left out, capped at n", capped.Count == 2 && capped[0].Name == "x" && capped[1].Name == "y");

            int self;
            using (System.Diagnostics.Process me = System.Diagnostics.Process.GetCurrentProcess()) self = me.Id;
            ulong selfPrivate = 0;
            foreach (ProcessSample s in ProcessTrimmer.Gather()) if (s.Id == self) selfPrivate = s.Private;
            Check("top apps: live private bytes non-zero for this process", selfPrivate > 0);
            Check("top apps: the window ranks by private bytes", ShellSource().Contains("AppMemoryList.Top(TopAppsCount, AppSort.Private)"));

            var code = new AppMemory { Name = "code", Processes = 1, PrivateBytes = GB, Bytes = 512 * MB };
            Check("top app: one process", AppText.TopAppName(code) == "code: 1.0 GB private, 512.0 MB in RAM", AppText.TopAppName(code));
            var edge = new AppMemory { Name = "msedge", Processes = 24, PrivateBytes = GB, Bytes = GB };
            Check("top app: process count", AppText.TopAppName(edge).EndsWith(", 24 processes"), AppText.TopAppName(edge));
            Check("top app: name is cleaned", AppText.TopAppName(new AppMemory { Name = char.ConvertFromUtf32(0x202E) + "exe", Processes = 1 })
                .StartsWith("exe: "));

            List<string> tip = AppText.TopAppTip(edge);
            Check("tooltip: three lines", tip.Count == 3, string.Join(" / ", tip.ToArray()));
            Check("tooltip: name and processes", tip.Count == 3 && tip[0] == "msedge, 24 processes", tip.Count > 0 ? tip[0] : "");
            Check("tooltip: private explained", tip.Count == 3 && tip[1] == "Private: " + Format.Bytes(GB) + ". Memory only this app can use. A clean does not change it.");
            Check("tooltip: in RAM explained", tip.Count == 3 && tip[2] == "In RAM: " + Format.Bytes(GB)
                + ". The part held in physical memory now. A clean lowers it for a while, then it grows back.");
            Check("tooltip: one process has no count", AppText.TopAppTip(code)[0] == "code");
            Check("top apps note", AppText.TopAppsNote == "Ranked by private memory. Cleaning does not shrink it; closing the app does.");

            string bidi = char.ConvertFromUtf32(0x202E) + "exe";
            Check("display name: strips format characters", AppText.DisplayName(bidi) == "exe", AppText.DisplayName(bidi));
            string mixed = "a" + char.ConvertFromUtf32(0x200B) + "b" + char.ConvertFromUtf32(0x2028) + "c" + (char)7 + "d";
            Check("display name: strips invisible, separators and controls", AppText.DisplayName(mixed) == "abcd", AppText.DisplayName(mixed));
            string longName = new string('x', 200);
            string capped64 = AppText.DisplayName(longName);
            Check("display name: caps length", capped64.Length <= 64 && capped64.EndsWith("..."), capped64.Length.ToString(CultureInfo.InvariantCulture));
            Check("display name: empty", AppText.DisplayName("") == "(no name)" && AppText.DisplayName(null) == "(no name)"
                && AppText.DisplayName("  " + char.ConvertFromUtf32(0x200E) + " ") == "(no name)");
            string pair = new string('x', 60) + char.ConvertFromUtf32(0x1F600) + new string('y', 10);
            string cut = AppText.DisplayName(pair);
            Check("display name: never splits a surrogate pair", !char.IsHighSurrogate(cut[cut.Length - 4]) || char.IsLowSurrogate(cut[cut.Length - 3]), cut);
            Check("display name: keeps ordinary names", AppText.DisplayName("Code") == "Code" && AppText.DisplayName(" ms-teams ") == "ms-teams");
        }

        // ---------------------------------------------------------------- U15

        static MemorySnapshot Commit(ulong total, ulong limit)
        {
            return new MemorySnapshot { CommitTotal = total, CommitLimit = limit };
        }

        static void CommitWarningTests()
        {
            Check("commit warn: off below 90", !AppText.CommitWarnOn(false, Commit(89, 100)));
            Check("commit warn: on at 90", AppText.CommitWarnOn(false, Commit(90, 100)));
            Check("commit warn: stays on at 88 when it was on", AppText.CommitWarnOn(true, Commit(88, 100)));
            Check("commit warn: stays on at 87 when it was on", AppText.CommitWarnOn(true, Commit(87, 100)));
            Check("commit warn: off at 86", !AppText.CommitWarnOn(true, Commit(86, 100)));
            Check("commit warn: 88 does not turn it on", !AppText.CommitWarnOn(false, Commit(88, 100)));
            Check("commit warn: unknown limit never warns", !AppText.CommitWarnOn(true, Commit(95, 0)) && !AppText.CommitWarnOn(false, Commit(95, 0)));
            Check("commit warn: null snapshot", !AppText.CommitWarnOn(true, null));
            Check("commit warn: levels", AppText.CommitWarnPercent == 90 && AppText.CommitClearPercent == 87);
            Check("commit name: warning appended", AppText.CommitName(Commit(92 * GB, 100 * GB), true).EndsWith("percent of the limit. Close to the limit"),
                AppText.CommitName(Commit(92 * GB, 100 * GB), true));
            Check("commit name: plain without warning", AppText.CommitName(Commit(92 * GB, 100 * GB), false) == AppText.CommitName(Commit(92 * GB, 100 * GB)));
            Check("commit name: unknown never warns", AppText.CommitName(Commit(5, 0), true) == "Committed: unknown");
            Check("commit warning wording", AppText.CommitWarning
                == "Close to the limit: apps may fail to start or to save work. Cleaning does not lower this; closing apps does.");
        }

        // ---------------------------------------------------------------- U16

        static void ShortcutTests()
        {
            Check("shortcut hint", AppText.ShortcutHint("Memory", "Ctrl+1") == "Memory (Ctrl+1)");
            Check("escape clears only with text", StartupFilter.EscapeClears("chrome") && !StartupFilter.EscapeClears("") && !StartupFilter.EscapeClears(null));
        }

        // ---------------------------------------------------------------- U21

        static void LockedAreaTests()
        {
            Check("last area reason names the area", AppText.LastAreaReasonFor(Areas.WorkingSets)
                == "At least one area must stay selected, so Process working sets stays on.", AppText.LastAreaReasonFor(Areas.WorkingSets));
            string standby = AppText.LastAreaReasonFor(Areas.StandbyList);
            Check("last area reason: standby short label", standby.Contains("Full standby cache stays on") && !standby.Contains("(slower afterwards)"), standby);
            Check("last area reason: drive cache short label", AppText.LastAreaReasonFor(Areas.VolumeCache).Contains("Drive write cache stays on")
                && !AppText.LastAreaReasonFor(Areas.VolumeCache).Contains("(writes to disk)"));
            foreach (Areas a in new[] { Areas.WorkingSets, Areas.SystemFileCache, Areas.LowPriorityStandby, Areas.StandbyList,
                         Areas.ModifiedList, Areas.VolumeCache, Areas.CombinePages, Areas.RegistryCache })
                Check("last area reason: " + a + " is a sentence", AppText.LastAreaReasonFor(a).EndsWith(" stays on.") && !AppText.LastAreaReasonFor(a).Contains("!"));
            Check("last area status", AppText.MustStaySelected == "Must stay selected");
            Check("area rules unchanged", !AreaRules.CanUntick(Areas.WorkingSets, Areas.WorkingSets)
                && AreaRules.Toggle(Areas.WorkingSets, Areas.WorkingSets, false) == Areas.WorkingSets);

            Check("clean tip: needs admin", AppText.CleanTooltip(false, false, MemoryViewState.Live) == AppText.CleanNeedsAdmin);
            Check("clean tip: waits for data", AppText.CleanTooltip(true, false, MemoryViewState.FailedNoData) == AppText.CleanNeedsData);
            Check("clean tip: none when it works", AppText.CleanTooltip(true, false, MemoryViewState.Live) == null);
            Check("clean tip: none while cleaning", AppText.CleanTooltip(true, true, MemoryViewState.Live) == null);
            Check("disabled tip: reason only while disabled", AppText.DisabledTip(false, "Needs admin.") == "Needs admin."
                && AppText.DisabledTip(true, "Needs admin.") == null && AppText.DisabledTip(false, "") == null && AppText.DisabledTip(false, null) == null);
        }

        // ---------------------------------------------------------------- R1 wiring

        static void AdviceTests()
        {
            var sm = new AdviceSmoother();
            Check("advice smoother: starts hidden", sm.Shown == CleanAdvice.None);
            Check("advice smoother: first sample shows at once", sm.Next(CleanAdvice.Mixed) == CleanAdvice.Mixed);
            Check("advice smoother: one different sample waits", sm.Next(CleanAdvice.NotNeeded) == CleanAdvice.Mixed);
            Check("advice smoother: two in a row change it", sm.Next(CleanAdvice.NotNeeded) == CleanAdvice.NotNeeded);
            sm.Next(CleanAdvice.Mixed);
            Check("advice smoother: a flicker back resets the wait", sm.Next(CleanAdvice.NotNeeded) == CleanAdvice.NotNeeded);
            sm.Next(CleanAdvice.Mixed);
            sm.Next(CleanAdvice.WillHelp);
            Check("advice smoother: two different candidates do not add up", sm.Shown == CleanAdvice.NotNeeded);
            sm.Reset();
            Check("advice smoother: after a reset the next sample shows at once", sm.Shown == CleanAdvice.None && sm.Next(CleanAdvice.CommitBound) == CleanAdvice.CommitBound);

            Check("advice look: low memory warns", AppText.AdviceGlyph(CleanAdvice.WillHelp) == "IconWarn" && AppText.AdviceStroke(CleanAdvice.WillHelp) == "Caution"
                && AppText.AdviceTextBrush(CleanAdvice.WillHelp) == "Text");
            Check("advice look: commit bound warns", AppText.AdviceGlyph(CleanAdvice.CommitBound) == "IconWarn" && AppText.AdviceStroke(CleanAdvice.CommitBound) == "Caution");
            Check("advice look: plenty is quiet", AppText.AdviceGlyph(CleanAdvice.NotNeeded) == "IconInfo" && AppText.AdviceStroke(CleanAdvice.NotNeeded) == "MutedText"
                && AppText.AdviceTextBrush(CleanAdvice.NotNeeded) == "MutedText");
            Check("advice look: mixed is quiet", AppText.AdviceGlyph(CleanAdvice.Mixed) == "IconInfo" && AppText.AdviceTextBrush(CleanAdvice.Mixed) == "MutedText");
            Check("advice look: none has no glyph", AppText.AdviceGlyph(CleanAdvice.None) == null);
            Check("advice: shown only on live figures", AppText.AdviceVisible(MemoryViewState.Live)
                && !AppText.AdviceVisible(MemoryViewState.LiveWithError) && !AppText.AdviceVisible(MemoryViewState.FailedNoData)
                && !AppText.AdviceVisible(MemoryViewState.Initial));
            Check("advice: display only, never in the auto-clean gate", !File.ReadAllText(SrcPath("Gui", "AutoCleanGate.cs")).Contains("Advise")
                && !File.ReadAllText(SrcPath("Gui", "AutoCleanService.cs")).Contains("Advise"));
        }

        // ---------------------------------------------------------------- E8 wiring

        static void TimingTests()
        {
            var ok = new AreaResult { Area = Areas.WorkingSets, Ok = true, Duration = TimeSpan.FromMilliseconds(1840) };
            Check("area time: shown for an area that ran", AppText.Describe(ok).Time == "1.8 s", AppText.Describe(ok).Time);
            var skipped = new AreaResult { Area = Areas.RegistryCache, Skipped = true, Message = "needs Windows 8.1 or later" };
            Check("area time: none for a skipped area", AppText.Describe(skipped).Time == null);
            var failedArea = new AreaResult { Area = Areas.CombinePages, NtStatus = unchecked((int)0xC00000BB), Duration = TimeSpan.FromMilliseconds(40) };
            Check("area time: a failed area still shows its time", AppText.Describe(failedArea).Time == "<0.1 s");
            Check("area name: time read out", AppText.AreaRowName(AppText.Describe(ok)) == "Process working sets: ok, 1.8 s"
                && AppText.AreaRowName(AppText.Describe(skipped)) == "Registry cache: skipped: needs Windows 8.1 or later", AppText.AreaRowName(AppText.Describe(ok)));
            var report = new CleanReport { Before = new MemorySnapshot(), After = new MemorySnapshot(), Duration = TimeSpan.FromMilliseconds(3400) };
            report.Results.Add(ok);
            Check("took line", AppText.TookLine(report) == "Took 3.4 s.", AppText.TookLine(report));
            Check("took line: none without a report", AppText.TookLine(null) == null);
            Check("took line: none for a clean that never ran", AppText.TookLine(new CleanReport { Stopped = CleanStop.Busy }) == null);
        }

        // ---------------------------------------------------------------- 2A carry-over: where a pause came from

        static AutoCleanStatus PausedStatus()
        {
            return new AutoCleanStatus { Paused = true, PausedUntilUtc = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc) };
        }

        static void PauseOriginTests()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var s = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300 };
            Check("pause origin: tray", AppText.PauseOriginLine(PauseOrigin.Tray) == "Paused from the tray for 1 hour.");
            Check("pause origin: settings", AppText.PauseOriginLine(PauseOrigin.Settings) == "Paused from Settings for 1 hour.");
            Check("pause origin: restored", AppText.PauseOriginLine(PauseOrigin.Restored) == "Paused before the app restarted.");
            Check("pause origin: unknown says nothing", AppText.PauseOriginLine(PauseOrigin.Unknown) == null);
            AutoNotice tray = AppText.AutoBar(AutoCleanState.Paused, PausedStatus(), s, inv, PauseOrigin.Tray);
            Check("bar: says where the pause came from", tray != null && tray.Detail == "Paused from the tray for 1 hour. Manual cleans still work.",
                tray == null ? "null" : tray.Detail);
            AutoNotice plain = AppText.AutoBar(AutoCleanState.Paused, PausedStatus(), s, inv, PauseOrigin.Unknown);
            Check("bar: unknown origin keeps the old detail", plain != null && plain.Detail == "Manual cleans still work."
                && AppText.RenderKey(plain) == AppText.RenderKey(AppText.AutoBar(AutoCleanState.Paused, PausedStatus(), s, inv)));
            Check("bar: origin changes what is drawn", AppText.RenderKey(tray) != AppText.RenderKey(plain));
            Check("bar: origin only on a pause", AppText.AutoBar(AutoCleanState.Armed, new AutoCleanStatus(), s, inv, PauseOrigin.Tray) == null);
            Check("pause origin: forgotten when the pause ends", AppText.KeepPauseOrigin(PauseOrigin.Tray, null) == PauseOrigin.Unknown
                && AppText.KeepPauseOrigin(PauseOrigin.Tray, new AutoCleanStatus()) == PauseOrigin.Unknown
                && AppText.KeepPauseOrigin(PauseOrigin.Tray, PausedStatus()) == PauseOrigin.Tray);
        }

        // ---------------------------------------------------------------- XAML and wiring (static checks)

        static string SrcPath(string a, string b)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetFullPath(Path.Combine(dir, Path.Combine("..", Path.Combine("src", Path.Combine(a, b)))));
        }

        static string Xaml(string name) { return File.ReadAllText(SrcPath("App", Path.Combine("Xaml", name))); }

        static string ShellSource() { return File.ReadAllText(SrcPath("App", "AppShell.cs")); }

        static string Token(string xaml, string key)
        {
            Match m = Regex.Match(xaml, "x:Key=\"" + key + "\" Color=\"(#[0-9A-Fa-f]{6})\"");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        static void XamlTests()
        {
            string main = Xaml("MainWindow.xaml"), styles = Xaml("Styles.xaml"), light = Xaml("Light.xaml"), dark = Xaml("Dark.xaml");
            string shell = ShellSource(), themes = File.ReadAllText(SrcPath("App", "ThemeManager.cs"));

            // U17, U16: one ToolTip style with tokens.
            Match tipStyle = Regex.Match(styles, "<Style TargetType=\"ToolTip\">(?:(?!</Style>)[\\s\\S])*</Style>");
            Check("ToolTip style defined with tokens", tipStyle.Success && tipStyle.Value.Contains("Property=\"Background\" Value=\"{DynamicResource Card}\"")
                && tipStyle.Value.Contains("Property=\"Foreground\" Value=\"{DynamicResource Text}\"")
                && tipStyle.Value.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource ControlBorder}\""));
            Check("top app rows get a tooltip", shell.Contains("row.ToolTip = TopAppTip(a);"));
            Check("top app rows are not tab stops", !Regex.IsMatch(shell, "row\\.Focusable = true"));
            Check("top apps: column heads", main.Contains("Text=\"In RAM\"") && main.Contains("Text=\"Private\""));
            Check("top apps: the note under the heading", main.Contains("x:Name=\"TopAppsNote\""));

            // U16
            foreach (string[] t in new[] { new[] { "MemoryTab", "Memory", "1" }, new[] { "StartupTab", "Startup", "2" }, new[] { "SettingsTab", "Settings", "3" } })
                Check("tab " + t[1] + " shows its shortcut", Regex.IsMatch(main, "x:Name=\"" + t[0] + "\"[^>]*ToolTip=\"" + t[1] + " \\(Ctrl\\+" + t[2] + "\\)\"")
                    && Regex.IsMatch(main, "x:Name=\"" + t[0] + "\"[^>]*AutomationProperties.AcceleratorKey=\"Ctrl\\+" + t[2] + "\""));
            Check("search placeholder names Ctrl+F", Regex.IsMatch(main, "x:Name=\"StartupSearch\"[^>]*Tag=\"Search \\(Ctrl\\+F\\)\"")
                && Regex.IsMatch(main, "x:Name=\"StartupSearch\"[^>]*AutomationProperties.AcceleratorKey=\"Ctrl\\+F\""));
            Check("clear search button named", Regex.IsMatch(main, "x:Name=\"ClearSearchButton\"[^>]*AutomationProperties.Name=\"Clear search\""));
            Check("clear search button is a 28 px target", Regex.IsMatch(main, "x:Name=\"ClearSearchButton\"[^>]*Width=\"28\" Height=\"28\""));
            Check("search text clears the button", Regex.IsMatch(styles, "x:Key=\"SearchBox\"(?:(?!</Style>)[\\s\\S])*Value=\"30,0,34,0\""));
            Check("Esc clears the search", shell.Contains("StartupFilter.EscapeClears(startupSearch.Text)"));
            Check("row error dismiss has a tooltip", shell.Contains("dismiss.ToolTip = \"Dismiss\";"));

            // U15
            Check("commit warning is a live region", Regex.IsMatch(main, "<app:LiveRegion x:Name=\"CommitWarning\"[^>]*AutomationProperties.LiveSetting=\"Polite\""));
            Check("commit warning has a caution glyph", Regex.IsMatch(main, "x:Name=\"CommitWarning\"(?:(?!</app:LiveRegion>)[\\s\\S])*Data=\"\\{StaticResource IconWarn\\}\" Stroke=\"\\{DynamicResource Caution\\}\""));
            Check("commit bar turns caution", shell.Contains("commitUsed.SetResourceReference(Border.BackgroundProperty, warn ? \"Caution\" : \"Accent\")"));

            // U21
            Check("area reason is a live region", Regex.IsMatch(main, "<app:LiveRegion x:Name=\"AreaReasonRegion\"[^>]*AutomationProperties.LiveSetting=\"Polite\""));
            Check("the last area stays focusable", !shell.Contains("pair.Key.IsEnabled = !locked;"));
            Check("the last area says it must stay", shell.Contains("AutomationProperties.SetItemStatus(pair.Key, locked ? AppText.MustStaySelected : \"\")"));
            Check("clean button shows its reason when disabled", Regex.IsMatch(main, "x:Name=\"CleanButton\"[^>]*ToolTipService.ShowOnDisabled=\"True\""));
            Check("start with Windows shows its reason when disabled", Regex.IsMatch(main, "x:Name=\"StartupCheck\"[^>]*ToolTipService.ShowOnDisabled=\"True\""));

            // R1 wiring
            Check("advice line under Clean memory", main.Contains("x:Name=\"AdviceLine\"") && main.Contains("x:Name=\"AdviceText\"") && main.Contains("x:Name=\"AdviceGlyph\""));
            Check("info glyph defined", styles.Contains("x:Key=\"IconInfo\""));
            Check("advice comes from the engine", shell.Contains("CleanEstimate.Advise("));

            // 2A carry-over: info-tinted bar tokens, contrast-checked.
            Check("info tokens in light", Token(light, "InfoBackground") == "#EAF2FB" && Token(light, "InfoBorder") == "#B9D2EC");
            Check("info tokens in dark", Token(dark, "InfoBackground") == "#1F2D3A" && Token(dark, "InfoBorder") == "#34506A");
            Check("info tokens in high contrast", themes.Contains("d[\"InfoBackground\"] = SystemColors.WindowBrush;")
                && themes.Contains("d[\"InfoBorder\"] = SystemColors.WindowTextBrush;") && themes.Contains("\"InfoBackground\", \"InfoBorder\""));
            Check("notice bar style uses the info tokens", Regex.IsMatch(styles, "x:Key=\"NoticeBar\"(?:(?!</Style>)[\\s\\S])*\\{DynamicResource InfoBackground\\}(?:(?!</Style>)[\\s\\S])*\\{DynamicResource InfoBorder\\}"));
            Check("auto-clean bar is info-tinted", Regex.IsMatch(main, "x:Name=\"AutoBar\"[^>]*Style=\"\\{StaticResource NoticeBar\\}\""));
            foreach (string[] t in new[] { new[] { "Light", light }, new[] { "Dark", dark } })
            {
                string info = Token(t[1], "InfoBackground");
                if (info == null) { Check(t[0] + " info contrast", false, "no InfoBackground"); continue; }
                Check(t[0] + ": text on info background 4.5:1", Ratio(Token(t[1], "Text"), info) >= 4.5);
                Check(t[0] + ": muted text on info background 4.5:1", Ratio(Token(t[1], "MutedText"), info) >= 4.5);
                Check(t[0] + ": accent glyph on info background 3:1", Ratio(Token(t[1], "Accent"), info) >= 3);
                Check(t[0] + ": caution glyph on info background 3:1", Ratio(Token(t[1], "Caution"), info) >= 3);
                Check(t[0] + ": button edge on info background 3:1", Ratio(Token(t[1], "ControlBorder"), info) >= 3);
                Check(t[0] + ": caution bar on its track 3:1", Ratio(Token(t[1], "Caution"), Token(t[1], "TrackBackground")) >= 3);
                Check(t[0] + ": tooltip text on card 4.5:1", Ratio(Token(t[1], "Text"), Token(t[1], "Card")) >= 4.5);
            }
        }

        static double Lum(string hex)
        {
            double[] c = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double v = int.Parse(hex.Substring(1 + 2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
                c[i] = v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
        }

        static double Ratio(string a, string b)
        {
            if (a == null || b == null) return 0;
            double la = Lum(a), lb = Lum(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
    }
}
