// Tests for the GUI's pure logic (wording, validation, theme rules, tray text, error log, app icon) and
// static checks of the XAML (well-formed, no raw colours, every token defined in both themes).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using RamOptimiser.App;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class AppTests
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
            StatusTests();
            ReportTests();
            ThresholdTests();
            AreaTests();
            ThemeTests();
            TextTests();
            MapTests();
            LaunchTests();
            ErrorLogTests();
            IconTests();
            XamlTests();
            PowerCleanTests();
            RecoveryTests();
            StartupTests();
            UiStateTests();
            SourceTests();
            return failed;
        }

        // ---------------------------------------------------------------- UI state (U1, U2, U8, U12, U13, U19, P4, P13)

        static void UiStateTests()
        {
            var inv = CultureInfo.InvariantCulture;

            // U2: loading and applying are separate; while a change applies, no switch can show an unapplied state.
            var flow = new StartupFlow();
            Check("startup flow: starts not loaded", flow.Phase == StartupPhase.NotLoaded && !flow.SwitchesEnabled);
            Check("startup flow: first load starts", flow.TryStartLoad() && flow.Phase == StartupPhase.Loading);
            Check("startup flow: a second load waits", !flow.TryStartLoad());
            Check("startup flow: no change while loading", !flow.TryStartApply("svc|A", "Service A", false));
            flow.Loaded();
            Check("startup flow: ready after load", flow.Phase == StartupPhase.Ready && flow.SwitchesEnabled && flow.BusyLine == null);
            Check("startup flow: a change starts", flow.TryStartApply("svc|A", "Service A", false) && flow.Phase == StartupPhase.Applying);
            Check("startup flow: switches wait while applying", !flow.SwitchesEnabled);
            Check("startup flow: a second change is refused", !flow.TryStartApply("svc|B", "Service B", false) && flow.ApplyingKey == "svc|A");
            Check("startup flow: no fresh load while applying", !flow.TryStartLoad() && flow.Phase == StartupPhase.Applying);
            Check("startup flow: the changing row shows the state asked for", !flow.ShowsOn("svc|A", true)
                && flow.ShowsOn("svc|B", true) && !flow.ShowsOn("svc|B", false));
            Check("startup flow: busy line", flow.BusyLine == "Turning off Service A... Other switches wait until this finishes.", flow.BusyLine);
            flow.Loaded();
            Check("startup flow: ready after the reload", flow.Phase == StartupPhase.Ready && flow.ApplyingKey == null && flow.ShowsOn("svc|A", true));
            Check("startup flow: turning on", flow.TryStartApply("svc|B", "Service B", true)
                && flow.BusyLine == "Turning on Service B... Other switches wait until this finishes.", flow.BusyLine);

            // U1: a row's error is announced once, not on every rebuild of the list.
            var said = new AnnouncedErrors();
            Check("error announce: first time", said.ShouldAnnounce("app|x", "Could not turn off X. access is denied"));
            Check("error announce: not again on a rebuild", !said.ShouldAnnounce("app|x", "Could not turn off X. access is denied"));
            Check("error announce: another row is its own", said.ShouldAnnounce("app|y", "Could not turn off Y. access is denied"));
            Check("error announce: a new error on the row is said", said.ShouldAnnounce("app|x", "Could not turn on X. busy"));
            said.Forget("app|x");
            Check("error announce: said again after it was cleared", said.ShouldAnnounce("app|x", "Could not turn on X. busy"));

            // U12: one view state for the Memory tab.
            var view = new MemoryView();
            Check("memory view: initial", view.State == MemoryViewState.Initial && !view.HasData
                && AppText.MemoryCaption(MemoryViewState.Initial) == "Reading memory...");
            view.SampleFailed();
            Check("memory view: first sample failed", view.State == MemoryViewState.FailedNoData
                && AppText.MemoryCaption(MemoryViewState.FailedNoData) == "Unavailable");
            Check("memory view: clean waits for data", !AppText.CleanEnabled(true, false, MemoryViewState.FailedNoData)
                && AppText.CleanEnabled(true, false, MemoryViewState.Initial) && AppText.CleanEnabled(true, false, MemoryViewState.LiveWithError)
                && !AppText.CleanEnabled(false, false, MemoryViewState.Live) && !AppText.CleanEnabled(true, true, MemoryViewState.Live));
            Check("memory view: clean help says why", AppText.CleanHelp(true, MemoryViewState.FailedNoData, null) == AppText.CleanNeedsData
                && AppText.CleanHelp(false, MemoryViewState.FailedNoData, null) == AppText.CleanNeedsAdmin
                && AppText.CleanHelp(true, MemoryViewState.Live, "note") == "note" && AppText.CleanHelp(true, MemoryViewState.Live, null) == "");
            string noData = AppText.SampleErrorText("Access is denied.", 2, false);
            Check("sample error: without data says Clean waits", noData
                == "Memory status could not be read: access is denied. Trying again every 2 seconds. Clean memory waits until it can be read.", noData);
            Check("sample error: with data", AppText.SampleErrorText("Access is denied.", 2, true)
                == "Memory status could not be read: access is denied. Trying again every 2 seconds.");
            var read = new DateTime(2026, 11, 17, 14, 32, 5);
            view.Sampled(read);
            Check("memory view: live", view.State == MemoryViewState.Live && view.HasData && AppText.MemoryCaption(MemoryViewState.Live) == null);
            view.SampleFailed();
            Check("memory view: live with error keeps when it was read", view.State == MemoryViewState.LiveWithError && view.LastRead == read);
            string lastRead = AppText.LastReadLine(read, new DateTime(2026, 11, 17, 14, 40, 0), inv);
            Check("memory view: last read line", lastRead == "Last read at 14:32.", lastRead);
            view.Sampled(read.AddSeconds(2));
            Check("memory view: live again", view.State == MemoryViewState.Live);

            // U13: the last-clean line says the day when it was not today.
            var report = new CleanReport { Before = Snap(16 * GB, 4 * GB, 75), After = Snap(16 * GB, 6 * GB, 62) };
            report.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            var cleanAt = new DateTime(2026, 11, 16, 14, 32, 0);
            string today = AppText.LastClean(cleanAt, cleanAt.AddHours(2), report, false, inv);
            Check("last clean: today has the time only", today.StartsWith("Last clean at 14:32 freed "), today);
            string yesterday = AppText.LastClean(cleanAt, new DateTime(2026, 11, 17, 0, 1, 0), report, false, inv);
            Check("last clean: yesterday says so", yesterday.StartsWith("Last clean yesterday at 14:32 freed "), yesterday);
            string older = AppText.LastClean(cleanAt, new DateTime(2026, 11, 19, 9, 0, 0), null, true, inv);
            Check("last clean: older shows the date", older == "Auto-clean on 11/16/2026 at 14:32 did not finish.", older);
            string failedLine = AppText.ResultLine(cleanAt, cleanAt, null, new InvalidOperationException("Boom."), false, inv);
            Check("result line: the clean's failure is appended", failedLine == "Last clean at 14:32 did not finish. The clean stopped: boom.", failedLine);
            Check("result line: a finished clean has no failure text", AppText.ResultLine(cleanAt, cleanAt, report, new InvalidOperationException("x"), false, inv)
                == AppText.LastClean(cleanAt, cleanAt, report, false, inv));

            // U8: the tray menu says when auto-clean is paused and why Clean is unavailable.
            TrayMenuState m = TrayMenuState.For(false, false, true);
            Check("tray: not admin, auto-clean says paused", m.AutoText == "Auto-clean (paused: needs admin)" && m.AutoChecked, m.AutoText);
            Check("tray: not admin, clean says why", !m.CleanEnabled && m.CleanText == "Clean memory (needs admin)", m.CleanText);
            Check("tray: not admin offers restart", m.ShowRestart && TrayMenuState.RestartText == "Restart as administrator");
            m = TrayMenuState.For(false, false, false);
            Check("tray: not admin, auto-clean off is just off", m.AutoText == "Auto-clean" && !m.AutoChecked);
            m = TrayMenuState.For(true, false, true);
            Check("tray: admin", m.CleanEnabled && m.CleanText == "Clean memory" && m.AutoText == "Auto-clean" && m.AutoChecked && !m.ShowRestart);
            m = TrayMenuState.For(true, true, false);
            Check("tray: cleaning", !m.CleanEnabled && m.CleanText == "Cleaning..." && !m.AutoChecked);
            m = TrayMenuState.For(true, false, true, false);
            Check("tray: memory unreadable, clean says why", !m.CleanEnabled && m.CleanText == "Clean memory (memory status unavailable)", m.CleanText);
            m = TrayMenuState.For(false, false, true, false);
            Check("tray: not admin is said before unreadable", m.CleanText == "Clean memory (needs admin)", m.CleanText);

            // P4: a threshold change waiting for its 400 ms save is flushed, once.
            var save = new PendingSave();
            Check("pending save: nothing to flush", !save.Pending && !save.Take());
            save.Changed();
            Check("pending save: a change is flushed once", save.Pending && save.Take() && !save.Take());
            save.Changed();
            save.Cancel();
            Check("pending save: cancel drops it", !save.Take());

            // P13: a sampling error during a clean stays a sampling error.
            Check("errors: a clean's failure is the clean's", ServiceErrorRoute.IsCleanFailure(ServiceErrorSource.Clean, false)
                && ServiceErrorRoute.IsCleanFailure(ServiceErrorSource.Clean, true));
            Check("errors: a sample's failure during a clean is the sample's", !ServiceErrorRoute.IsCleanFailure(ServiceErrorSource.Sample, true)
                && !ServiceErrorRoute.IsCleanFailure(ServiceErrorSource.Sample, false));
            Check("errors: another failure during a clean", ServiceErrorRoute.IsCleanFailure(ServiceErrorSource.Other, true)
                && !ServiceErrorRoute.IsCleanFailure(ServiceErrorSource.Other, false));

            // U19: only broadcasts that can change colours re-read the theme; high contrast says it overrides the choice.
            Check("theme: colour broadcasts re-read the theme", ThemeResolver.AffectsTheme(Microsoft.Win32.UserPreferenceCategory.General)
                && ThemeResolver.AffectsTheme(Microsoft.Win32.UserPreferenceCategory.Color)
                && ThemeResolver.AffectsTheme(Microsoft.Win32.UserPreferenceCategory.VisualStyle)
                && ThemeResolver.AffectsTheme(Microsoft.Win32.UserPreferenceCategory.Accessibility));
            Check("theme: unrelated broadcasts do not", !ThemeResolver.AffectsTheme(Microsoft.Win32.UserPreferenceCategory.Desktop)
                && !ThemeResolver.AffectsTheme(Microsoft.Win32.UserPreferenceCategory.Power)
                && !ThemeResolver.AffectsTheme(Microsoft.Win32.UserPreferenceCategory.Locale)
                && !ThemeResolver.AffectsTheme(Microsoft.Win32.UserPreferenceCategory.Mouse)
                && !ThemeResolver.AffectsTheme(Microsoft.Win32.UserPreferenceCategory.Keyboard));
            Check("theme: high contrast note", ThemeResolver.ThemeNote(true) == "High contrast is on, so Windows' own colours are used."
                && ThemeResolver.ThemeNote(false) == null);

            // P10, P12, U10: shell wording.
            Check("waiting for approval", AppText.WaitingForApproval == "Waiting for approval..."
                && AppText.WaitingForApprovalHelp == "Waiting for the administrator prompt.");
            Check("restart failed wording", AppText.RestartFailed("Access is denied.") == "Could not restart as administrator: access is denied."
                && AppText.RestartFailed(null) == "Could not restart as administrator: unknown error.", AppText.RestartFailed("Access is denied."));
            string saveFailed = AppText.SaveFailedText("The disk is full.");
            Check("save failed wording", saveFailed == "Settings could not be saved: the disk is full. They apply until RAM Optimiser closes."
                && saveFailed.EndsWith("closes.") && !saveFailed.Contains("!"), saveFailed);
            Check("save failed wording without a reason", AppText.SaveFailedText("") == "Settings could not be saved: unknown error. They apply until RAM Optimiser closes.",
                AppText.SaveFailedText(""));
            Check("tray settings not saved", AppText.SettingsNotSaved == "Settings not saved");
            Check("locked startup row says on", AppText.LockedRowName("Defender", "Protected by Windows", true) == "Defender: Protected by Windows, starts with Windows");
            Check("locked startup row says off", AppText.LockedRowName("Defender", "Protected by Windows", false) == "Defender: Protected by Windows, does not start with Windows");

            string main = File.ReadAllText(Path.Combine(XamlDir(), "MainWindow.xaml"));
            Check("memory tab has a last-read note", main.Contains("x:Name=\"LastReadNote\""));
            Check("settings has a theme note", main.Contains("x:Name=\"ThemeNote\""));
        }

        // ---------------------------------------------------------------- WPF-only code paths, checked in source (P4, P14)

        static void SourceTests()
        {
            string dir = Path.GetFullPath(Path.Combine(XamlDir(), ".."));
            string main = File.ReadAllText(Path.Combine(dir, "AppMain.cs"));
            Match startCatch = Regex.Match(main, @"shell\.Start\(\);\s*\}\s*catch \(Exception ex\)\s*\{(?<body>[^}]*)\}");
            Check("start-up failure removes the tray icon", startCatch.Success && startCatch.Groups["body"].Value.Contains("EmergencyCleanup()"),
                startCatch.Success ? startCatch.Groups["body"].Value : "catch not found");
            // Before the modal error box: its message loop would otherwise run the live tray menu and timer work.
            string body = startCatch.Success ? startCatch.Groups["body"].Value : "";
            int cleanup = body.IndexOf("EmergencyCleanup()", StringComparison.Ordinal), fatal = body.IndexOf("Fatal(", StringComparison.Ordinal);
            Check("start-up failure removes the tray icon before the error box", cleanup >= 0 && fatal > cleanup, body);

            string shell = File.ReadAllText(Path.Combine(dir, "AppShell.cs"));
            int exit = shell.IndexOf("public void Exit()", StringComparison.Ordinal);
            int flush = exit < 0 ? -1 : shell.IndexOf("FlushThreshold();", exit, StringComparison.Ordinal);
            int mark = exit < 0 ? -1 : shell.IndexOf("exiting = true;", exit, StringComparison.Ordinal);
            Check("exit saves a pending threshold first", exit >= 0 && flush > exit && flush < mark);
            int restart = shell.IndexOf("void RestartElevated()", StringComparison.Ordinal);
            int restartEnd = restart < 0 ? -1 : shell.IndexOf("\n        }", restart, StringComparison.Ordinal);
            string restartBody = restart < 0 || restartEnd < 0 ? "" : shell.Substring(restart, restartEnd - restart);
            int relaunch = restartBody.IndexOf("Elevation.RelaunchAsync(", StringComparison.Ordinal);
            int flush2 = restartBody.IndexOf("FlushThreshold();", StringComparison.Ordinal);
            Check("restart as administrator saves a pending threshold first", relaunch > 0 && flush2 > 0 && flush2 < relaunch, restartBody);
            // P10: the prompt runs off the UI thread through the state machine, and the lock is kept while it is up.
            int gate = restartBody.IndexOf("ElevationFlow.Next(elevationState, ElevationEvent.Requested)", StringComparison.Ordinal);
            Check("restart as administrator goes through the state machine first", gate > 0 && gate < flush2, restartBody);
            Check("restart as administrator keeps the single-instance lock", !restartBody.Contains("instance.Dispose()")
                && !restartBody.Contains("new SingleInstance(") && !restartBody.Contains("TryRelaunchElevated"), restartBody);
            Check("restart as administrator hands over its pid", restartBody.Contains("Elevation.HandoffArgs(") && restartBody.Contains("WindowInteropHelper(window).Handle"));
            // Started from the tray with the window hidden, a failed restart shows the window, so its error line is seen.
            Match showError = Regex.Match(shell, @"case ElevationAction\.ShowError:(?<body>(?:(?!case |default:)[\s\S])*)");
            Check("a failed restart as administrator shows its error", showError.Success && showError.Groups["body"].Value.Contains("ShowWindow()")
                && showError.Groups["body"].Value.Contains("memoryTab.IsSelected = true") && showError.Groups["body"].Value.Contains("AppText.RestartFailed("),
                showError.Success ? showError.Groups["body"].Value : "ShowError case not found");
            int exitApplies = exit < 0 ? -1 : shell.IndexOf("ElevationEvent.ExitRequested", exit, StringComparison.Ordinal);
            Check("exit settles the elevation state", exitApplies > exit && exitApplies < mark);

            // P12: hand edits are picked up when the window is activated, never while a slider value waits.
            Check("window activation checks settings.ini", Regex.IsMatch(shell, @"window\.Activated \+= delegate \{ CheckSettingsFile\(\); \};"));
            int check = shell.IndexOf("void CheckSettingsFile()", StringComparison.Ordinal);
            int checkEnd = check < 0 ? -1 : shell.IndexOf("\n        }", check, StringComparison.Ordinal);
            string checkBody = check < 0 || checkEnd < 0 ? "" : shell.Substring(check, checkEnd - check);
            Check("settings reload waits for a pending slider value", checkBody.Contains("thresholdTimer.IsEnabled"), checkBody);
            Check("settings reload keeps Start with Windows", checkBody.Contains("StartWithWindows = settings.StartWithWindows"), checkBody);
            Check("settings reload retries a failed save", checkBody.Contains("SettingsSyncState.SaveFailed") && checkBody.Contains("SaveAndApply();"), checkBody);
            Check("save merges hand edits", shell.Contains("SettingsMerge.Merge(baseline, settings,"));
            Check("tray is told when settings are not saved", shell.Contains("syncState == SettingsSyncState.SaveFailed"));
            // A file that could not be read (locked, too big, refused) is never taken for a file of defaults.
            Check("settings reload only applies a file it could read", checkBody.Contains("SettingsStore.TryLoad(settingsPath, out disk)")
                && checkBody.Contains("SettingsSyncEvent.ReloadFailed") && !checkBody.Contains("SettingsStore.Load("), checkBody);
            int save = shell.IndexOf("void SaveAndApply()", StringComparison.Ordinal);
            int saveEnd = save < 0 ? -1 : shell.IndexOf("\n        }", save, StringComparison.Ordinal);
            string saveBody = save < 0 || saveEnd < 0 ? "" : shell.Substring(save, saveEnd - save);
            Check("save merges only a file it could read", saveBody.Contains("SettingsStore.TryLoad(settingsPath, out disk)")
                && !saveBody.Contains("SettingsStore.Load("), saveBody);
            // The hidden-areas disclosure opens only for an area newly on, not on every save.
            Check("a save leaves a closed disclosure closed", shell.Contains("AreasDisclosure.ShouldReveal(before.Areas, settings.Areas, HiddenAreas)")
                && !shell.Contains("if ((settings.Areas & HiddenAreas) != 0) moreAreas.IsExpanded = true;"));
            // P10: keyboard focus goes back to the restart link after a cancelled or failed prompt.
            Check("restart as administrator notes keyboard focus before disabling the link",
                restartBody.IndexOf("restartHadFocus = restartButton.IsKeyboardFocused;", StringComparison.Ordinal) >= 0
                && restartBody.IndexOf("restartHadFocus = restartButton.IsKeyboardFocused;", StringComparison.Ordinal)
                    < restartBody.IndexOf("ShowRestartLink();", StringComparison.Ordinal), restartBody);
            Check("a failed restart gives focus back", showError.Success && showError.Groups["body"].Value.Contains("RestoreRestartFocus();"));
            Match cancelled = Regex.Match(shell, @"// Cancelled: the link comes back(?<body>(?:(?!\n        }\n)[\s\S])*)");
            Check("a cancelled restart gives focus back", cancelled.Success && cancelled.Groups["body"].Value.Contains("RestoreRestartFocus();"),
                cancelled.Success ? cancelled.Groups["body"].Value : "Cancelled branch not found");

            // P10: the elevated copy waits for the copy that asked for it before taking the lock.
            Check("start-up takes over from the copy that asked for elevation", main.Contains("options.HandoffFrom > 0")
                && main.Contains("SingleInstance.AcquireAfter("));
        }

        // ---------------------------------------------------------------- services restored at start-up (P3, E10)

        static ServiceAction Done(string label, bool started, string message)
        {
            return new ServiceAction { Name = label, Label = label, Started = started, Failed = !started && message != "no longer installed", Message = message };
        }

        static void RecoveryTests()
        {
            const string Retry = " Try Restore services in Settings, or restart Windows.";
            var one = new List<ServiceAction> { Done("Print Spooler", true, null) };
            string line = AppText.RecoveryLine(one, null);
            Check("recovery: one restored", line == "Power Clean left 1 service stopped when the app last closed. It is running again.", line);
            Check("recovery: one restored went well", AppText.RecoveryWentWell(one, null));
            List<AreaLine> rows = AppText.RecoveryLines(one);
            Check("recovery: one ok row", rows.Count == 1 && rows[0].Label == "Print Spooler" && rows[0].State == AreaState.Ok
                && rows[0].Status == "ok" && rows[0].Area == Areas.Services);

            var two = new List<ServiceAction> { Done("Print Spooler", true, null), Done("SysMain", true, null) };
            line = AppText.RecoveryLine(two, null);
            Check("recovery: two restored", line == "Power Clean left 2 services stopped when the app last closed. They are running again.", line);

            var mixed = new List<ServiceAction> { Done("Print Spooler", true, null), Done("SysMain", false, "Access is denied") };
            line = AppText.RecoveryLine(mixed, null);
            Check("recovery: one of two failed", line == "Power Clean left 2 services stopped when the app last closed. 1 is running again; 1 could not be started." + Retry, line);
            Check("recovery: a failure gets the warning", !AppText.RecoveryWentWell(mixed, null));
            rows = AppText.RecoveryLines(mixed);
            Check("recovery: failed row says why", rows.Count == 2 && rows[1].State == AreaState.Failed && rows[1].Status == "failed: access is denied",
                rows.Count > 1 ? rows[1].Status : rows.Count.ToString());

            var failedOne = new List<ServiceAction> { Done("Print Spooler", false, "still starting after 10 s") };
            line = AppText.RecoveryLine(failedOne, null);
            Check("recovery: the only one failed", line == "Power Clean left 1 service stopped when the app last closed. It could not be started again." + Retry, line);

            var gone = new List<ServiceAction> { Done("Connected Devices Platform", false, "no longer installed") };
            Check("recovery: a service no longer installed says nothing", AppText.RecoveryLine(gone, null) == null && AppText.RecoveryLines(gone).Count == 0);
            var withGone = new List<ServiceAction> { Done("Print Spooler", true, null), Done("Connected Devices Platform", false, "no longer installed") };
            line = AppText.RecoveryLine(withGone, null);
            Check("recovery: gone ones are not counted", line == "Power Clean left 1 service stopped when the app last closed. It is running again."
                && AppText.RecoveryWentWell(withGone, null), line);
            Check("recovery: nothing left, nothing said", AppText.RecoveryLine(new List<ServiceAction>(), null) == null && AppText.RecoveryLine(null, null) == null);

            line = AppText.RecoveryLine(null, "Access is denied.");
            Check("recovery: the restore itself failed", line == "Services an earlier Power Clean stopped could not be started again: access is denied." + Retry, line);
            Check("recovery: that gets the warning", !AppText.RecoveryWentWell(null, "x"));
        }

        // ---------------------------------------------------------------- Startup view

        static void StartupTests()
        {
            // StartupApproved bytes (Task Manager's format, observed 02/03/06).
            Check("approved: missing is on", StartupApps.IsApprovedEnabled(null));
            Check("approved: 02 on", StartupApps.IsApprovedEnabled(new byte[] { 2, 0, 0, 0 }));
            Check("approved: 06 on", StartupApps.IsApprovedEnabled(new byte[] { 6, 0, 0, 0 }));
            Check("approved: 03 off", !StartupApps.IsApprovedEnabled(new byte[] { 3, 0, 0, 0 }));
            var t = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
            byte[] off = StartupApps.ApprovedBytes(false, t);
            Check("approved bytes: off, 12 bytes, filetime", off.Length == 12 && off[0] == 3
                && BitConverter.ToInt64(off, 4) == t.ToFileTimeUtc());
            byte[] on = StartupApps.ApprovedBytes(true, t);
            Check("approved bytes: on is 02 then zeros", on.Length == 12 && on[0] == 2 && Array.TrueForAll(on, b => b == 0 || b == 2));
            Check("approved: round trip", !StartupApps.IsApprovedEnabled(off) && StartupApps.IsApprovedEnabled(on));

            // Command parsing.
            Check("exe: quoted", StartupApps.ExePathOf("\"C:\\A B\\x.exe\" --tray") == "C:\\A B\\x.exe");
            Check("exe: unquoted with args", StartupApps.ExePathOf("C:\\A B\\x.exe -min") == "C:\\A B\\x.exe");
            Check("exe: empty", StartupApps.ExePathOf("  ") == null);
            Check("launched: squirrel", StartupApps.LaunchedExe("\"C:\\u\\Discord\\Update.exe\" --processStart Discord.exe") == "Discord.exe");
            Check("launched: quoted arg", StartupApps.LaunchedExe("C:\\u\\Update.exe --processStart \"Slack.exe\" --x") == "Slack.exe");
            Check("launched: other exe", StartupApps.LaunchedExe("C:\\x\\steam.exe -silent") == null);
            Check("launched: null", StartupApps.LaunchedExe(null) == null);

            // Knowledge base safety: protected and hardware services are never offered.
            foreach (string keep in new[] { "WinDefend", "WdNisSvc", "MDCoreSvc" })
            {
                KnownItem k = StartupKnowledge.FindService(keep);
                Check("kb: " + keep + " is keep", k != null && k.Verdict == Verdict.Keep);
                Check("kb: " + keep + " cannot be disabled", !StartupKnowledge.CanDisableService(keep));
            }
            Check("kb: security tray is keep", StartupKnowledge.FindApp("securityhealthsystray.exe").Verdict == Verdict.Keep);
            Check("kb: unknown service locked", !StartupKnowledge.CanDisableService("SomeVendorService"));
            Check("kb: unknown name null", StartupKnowledge.FindService("wuauserv") == null);
            Check("kb: armoury crate allowed", StartupKnowledge.CanDisableService("ArmouryCrateService"));
            Check("kb: catalog fallback", StartupKnowledge.FindService("SysMain") != null
                && StartupKnowledge.FindService("SysMain").Verdict == Verdict.Caution);
            foreach (KnownItem k in StartupKnowledge.Services())
                Check("kb: no Windows-core name offered: " + k.Match,
                    !(k.Verdict != Verdict.Keep && Array.IndexOf(new[] { "WinDefend", "wuauserv", "mpssvc", "BFE", "Audiosrv", "RpcSs" }, k.Match) >= 0));

            // Wording.
            Check("verdict words", AppText.VerdictText(Verdict.Safe, false) == "Safe to turn off"
                && AppText.VerdictText(Verdict.Keep, true) == "Keep on"
                && AppText.VerdictText(Verdict.Unknown, true).StartsWith("Not in the app's list"));
            var app = new StartupApp { Source = StartupSource.MachineRun, Name = "X", Publisher = "Contoso" };
            Check("app detail: unknown publisher line", AppText.AppDetail(app) == "From Contoso. Starts for every user");
            app.Known = StartupKnowledge.FindApp("Discord.exe");
            Check("app detail: known", AppText.AppDetail(app).StartsWith("Opens the Discord") && AppText.AppDetail(app).Contains("If off: "));
            var svc = new StartupService { Name = "ArmouryCrateService", Mode = StartMode.Automatic, Running = true, SharedWith = 1,
                MemoryBytes = 74L * 1024 * 1024, Known = StartupKnowledge.FindService("ArmouryCrateService") };
            Check("service detail", AppText.ServiceDetail(svc).EndsWith("Starts with Windows. Uses 74.0 MB"), AppText.ServiceDetail(svc));
            svc.Protected = true;
            Check("service detail: protected says why", AppText.ServiceDetail(svc).Contains("Windows protects this service"));
            svc.Protected = false;
            svc.DisabledByApp = true;
            Check("service detail: turned off", AppText.ServiceDetail(svc).Contains("Turned off by this app"));

            // Journal entries only count while the service is still at the Manual start this app set.
            Check("journal: manual holds", StartupServices.JournalStillHolds(true, StartMode.Manual));
            Check("journal: disabled since does not", !StartupServices.JournalStillHolds(true, StartMode.Disabled));
            Check("journal: automatic (failed change) does not", !StartupServices.JournalStillHolds(true, StartMode.Automatic));
            Check("journal: no record", !StartupServices.JournalStillHolds(false, StartMode.Manual));

            // A Keep match by file name locks the switch only for programs in admin-only folders.
            var roots = new[] { @"C:\Windows", @"C:\Program Files" };
            KnownItem tray = StartupKnowledge.FindApp("SecurityHealthSystray.exe");
            Check("trust: keep in Windows kept", StartupApps.TrustedKnown(tray, @"C:\Windows\System32\SecurityHealthSystray.exe", roots) == tray);
            Check("trust: keep in user folder dropped", StartupApps.TrustedKnown(tray, @"C:\Users\x\AppData\Local\SecurityHealthSystray.exe", roots) == null);
            Check("trust: keep via .. dropped", StartupApps.TrustedKnown(tray, @"C:\Windows\..\Users\x\SecurityHealthSystray.exe", roots) == null);
            Check("trust: keep on shortcut dropped", StartupApps.TrustedKnown(tray, null, roots) == null);
            Check("trust: prefix sibling dropped", !StartupApps.IsInside(@"C:\WindowsApps\x.exe", @"C:\Windows"));
            KnownItem discord = StartupKnowledge.FindApp("Discord.exe");
            Check("trust: safe match kept anywhere", StartupApps.TrustedKnown(discord, @"C:\Users\x\Discord.exe", roots) == discord);
            app = new StartupApp { Source = StartupSource.UserRun, Name = "Discord", ExePath = @"C:\Users\x\Discord.exe", Known = discord };
            Check("app detail: known shows the real file", AppText.AppDetail(app).EndsWith("Runs C:\\Users\\x\\Discord.exe"), AppText.AppDetail(app));

            // Per-user approval writes must land in this user's own hive.
            const string sub = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
            Check("hive: own key", StartupApps.IsOwnUserKey(@"\REGISTRY\USER\S-1-5-21-1\" + sub, "S-1-5-21-1", sub));
            Check("hive: machine key refused", !StartupApps.IsOwnUserKey(@"\REGISTRY\MACHINE\" + sub, "S-1-5-21-1", sub));
            Check("hive: other user refused", !StartupApps.IsOwnUserKey(@"\REGISTRY\USER\S-1-5-21-2\" + sub, "S-1-5-21-1", sub));
            Check("hive: unknown path refused", !StartupApps.IsOwnUserKey(null, "S-1-5-21-1", sub));
            using (Microsoft.Win32.RegistryKey sw = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software"))
            {
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                string real = StartupApps.KernelPath(sw);
                Check("hive: kernel path of HKCU\\Software", StartupApps.IsOwnUserKey(real, sid, "Software"), real);
            }
            Check("summary", AppText.StartupSummary(4, 24, 9) == "4 apps and 24 listed services start with Windows. 9 are safe to turn off.",
                AppText.StartupSummary(4, 24, 9));
            Check("summary: one, none safe", AppText.StartupSummary(1, 1, 0) == "1 app and 1 listed service start with Windows.");
        }

        // ---------------------------------------------------------------- Power Clean

        static ServiceState Svc(string name, bool running, bool canStop, params string[] dependents)
        {
            return new ServiceState { Name = name, DisplayName = name, Rule = ServiceCatalog.Find(name), Running = running,
                CanStop = canStop, RunningDependents = new List<string>(dependents) };
        }

        static void PowerCleanTests()
        {
            // Catalog guards: only listed names, never security or update services.
            Check("catalog: maps listed", ServiceCatalog.Find("MapsBroker") != null);
            foreach (string never in new[] { "WinDefend", "wuauserv", "BITS", "WerSvc", "dmwappushservice", "Audiosrv", "mpssvc", "EventLog" })
                Check("catalog: never " + never, ServiceCatalog.Find(never) == null);
            Check("catalog: per-user instance matches", ServiceCatalog.Find("CDPUserSvc_22d3e0") != null);
            Check("catalog: per-user needs hex suffix", ServiceCatalog.Find("CDPUserSvc_zz") == null);
            Check("catalog: suffix only for per-user", ServiceCatalog.Find("MapsBroker_1a") == null);
            Check("catalog: SysMain not ticked by default", !ServiceCatalog.SuggestedNames().Contains("SysMain"));
            var filtered = ServiceCatalog.Filter(new[] { " mapsbroker", "WinDefend", "MapsBroker", null, "Fax" });
            Check("filter: catalog spelling, order, no dupes", string.Join(",", filtered.ToArray()) == "MapsBroker,Fax",
                string.Join(",", filtered.ToArray()));

            // Plan: skips with reasons, never a non-catalog service even if chosen.
            var states = new List<ServiceState>
            {
                Svc("MapsBroker", true, true), Svc("Fax", false, true), Svc("WalletService", true, false),
                Svc("Spooler", true, true, "PrintNotify"), Svc("XblAuthManager", true, true),
                new ServiceState { Name = "WinDefend", Rule = null, Running = true, CanStop = true },
            };
            var plan = ServiceTrimmer.Plan(states, new[] { "MapsBroker", "Fax", "WalletService", "Spooler", "WinDefend" });
            Check("plan: four catalog rows", plan.Count == 4, plan.Count.ToString(CultureInfo.InvariantCulture));
            Check("plan: unchosen left out", !plan.Exists(a => a.Name == "XblAuthManager"));
            Check("plan: WinDefend left out", !plan.Exists(a => a.Name == "WinDefend"));
            Check("plan: running one stops", plan.Find(a => a.Name == "MapsBroker").Message == null);
            Check("plan: stopped one skipped", plan.Find(a => a.Name == "Fax").Message == "not running");
            Check("plan: no-stop skipped", plan.Find(a => a.Name == "WalletService").Message == "does not accept a stop right now");
            Check("plan: dependents skip", plan.Find(a => a.Name == "Spooler").Message == "needed by PrintNotify");
            var other = Svc("CDPUserSvc_22d3e0", true, true);
            other.OtherSession = true;
            var mine = Svc("CDPUserSvc_4f1a2b", true, true);
            var perUser = ServiceTrimmer.Plan(new[] { other, mine }, new[] { "CDPUserSvc" });
            Check("plan: other user's instance skipped", perUser.Find(a => a.Name == "CDPUserSvc_22d3e0").Message == "belongs to another signed-in user");
            Check("plan: own instance stops", perUser.Find(a => a.Name == "CDPUserSvc_4f1a2b").Message == null);
            Check("plan: rule/name mismatch ignored", ServiceTrimmer.Plan(new[] {
                new ServiceState { Name = "WinDefend", Rule = ServiceCatalog.Find("MapsBroker"), Running = true, CanStop = true } },
                new[] { "MapsBroker" }).Count == 0);
            Check("summary: one stopped plus skip", ServiceTrimmer.Summarise(new List<ServiceAction> {
                new ServiceAction { Label = "A", Stopped = true }, new ServiceAction { Label = "B", Message = "not running" },
                new ServiceAction { Label = "C", Message = "needed by X" } }) == "stopped 1 service; C: needed by X");

            // Settings: round trip, defaults, tampered file cannot widen the list.
            var s = new AppSettings();
            Check("settings: power off by default", !s.PowerClean && s.CleanAreas == s.Areas);
            s.PowerClean = true;
            s.PowerServices = new List<string> { "Fax", "SysMain" };
            Check("settings: power areas", s.CleanAreas == (Areas.All | Areas.Services));
            AppSettings back = SettingsStore.Parse(SettingsStore.Serialize(s));
            Check("settings: round trip", back.PowerClean && string.Join(",", back.PowerServices.ToArray()) == "Fax,SysMain",
                string.Join(",", back.PowerServices.ToArray()));
            AppSettings tampered = SettingsStore.Parse("PowerClean=true\r\nPowerServices=WinDefend,wuauserv,Fax,..\\x");
            Check("settings: tampered list narrowed", string.Join(",", tampered.PowerServices.ToArray()) == "Fax");
            Check("settings: empty list kept empty", SettingsStore.Parse("PowerServices=").PowerServices.Count == 0);
            Check("settings: services flag not saved in areas", !SettingsStore.Serialize(s).Contains("services"));
            AppSettings copy = s.Clone();
            copy.PowerServices.Add("MapsBroker");
            Check("settings: clone copies the list", s.PowerServices.Count == 2);
            s.PowerServices = new List<string> { "WinDefend" };
            Check("settings: normalized filters", s.Normalized().PowerServices.Count == 0);

            // Wording.
            Check("label: one button name", AppText.CleanLabel(true, false) == "Clean memory" && AppText.CleanLabel(false, false) == "Clean memory"
                && AppText.CleanLabel(false, true) == "Cleaning...");
            Check("power note: off is null", AppText.PowerNote(false, 3) == null);
            Check("power note: no services", AppText.PowerNote(true, 0) == "Power Clean is on: cleans every area.");
            Check("power note: one service", AppText.PowerNote(true, 1) == "Power Clean is on: cleans every area and stops 1 chosen service.");
            Check("power note: services counted", AppText.PowerNote(true, 2) == "Power Clean is on: cleans every area and stops 2 chosen services.");
            Check("services header", AppText.ServicesHeader(4, 26) == "Services to stop: 4 of 26 chosen" && AppText.ServicesHeader(0, 0) == "Services to stop");
            Check("sentence: capitalises", AppText.Sentence("waits 5 minutes") == "Waits 5 minutes" && AppText.Sentence("") == "");

            var inv = CultureInfo.InvariantCulture;
            var at = new DateTime(2026, 11, 11, 14, 32, 0);
            var good = new CleanReport { Before = Snap(16 * GB, 4 * GB, 75), After = Snap(16 * GB, 6 * GB, 62) };
            good.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            Check("last clean: freed and load", AppText.LastClean(at, good, false, inv)
                == "Last clean at 14:32 freed " + Format.Bytes(good.InUseFreed) + ". Load went from 75% to 62%.");
            Check("last clean: automatic", AppText.LastClean(at, good, true, inv).StartsWith("Auto-clean at 14:32 freed "));
            Check("last clean: went well", AppText.CleanWentWell(good));
            Check("last clean: unfinished", AppText.LastClean(at, null, false, inv) == "Last clean at 14:32 did not finish."
                && !AppText.CleanWentWell(null));
            var partial = new CleanReport { Before = Snap(16 * GB, 4 * GB, 75), After = Snap(16 * GB, 4 * GB, 75) };
            partial.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            partial.Results.Add(new AreaResult { Area = Areas.CombinePages, NtStatus = unchecked((int)0xC00000BB) });
            Check("last clean: failures counted", AppText.LastClean(at, partial, false, inv)
                == "Last clean at 14:32 freed nothing. Load went from 75% to 75%. 1 area failed." && !AppText.CleanWentWell(partial));
            var someDrives = new CleanReport { Before = Snap(16 * GB, 4 * GB, 75), After = Snap(16 * GB, 4 * GB, 75) };
            someDrives.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            someDrives.Results.Add(new AreaResult { Area = Areas.VolumeCache, Ok = true, Partial = true, Message = "flushed 1 of 2 drives; E: access is denied" });
            string someLine = AppText.LastClean(at, someDrives, false, inv);
            Check("last clean: partial area counted, not a full success",
                someLine == "Last clean at 14:32 freed nothing. Load went from 75% to 75%. 1 area partly failed." && !AppText.CleanWentWell(someDrives), someLine);
            Check("last auto-clean: partial area counted", AppText.LastAutoClean(at, someDrives, inv)
                == "Last auto-clean at 14:32: nothing freed, 1 area partly failed", AppText.LastAutoClean(at, someDrives, inv));

            Check("commit: unknown without a limit", AppText.CommitText(new MemorySnapshot()) == "unknown"
                && AppText.CommitName(new MemorySnapshot()) == "Committed: unknown");
            var commit = new MemorySnapshot { CommitTotal = 12 * GB, CommitLimit = 24 * GB };
            Check("commit: of limit", AppText.CommitText(commit) == Format.Bytes(12 * GB) + " of " + Format.Bytes(24 * GB)
                && AppText.CommitName(commit).EndsWith(", 50 percent of the limit"));

            Check("top app: one process", AppText.TopAppName(new AppMemory { Name = "code", Processes = 1, Bytes = GB })
                == "code: " + Format.Bytes(GB));
            Check("top app: process count", AppText.TopAppName(new AppMemory { Name = "msedge", Processes = 24, Bytes = GB })
                == "msedge: " + Format.Bytes(GB) + ", 24 processes");
            Check("change failed: title", AppText.ChangeFailedTitle("Xbox", false) == "Couldn't turn off Xbox"
                && AppText.ChangeFailedTitle("Xbox", true) == "Couldn't turn on Xbox");
            Check("change failed: text keeps state", AppText.ChangeFailedText("access is denied", false) == "Access is denied. It is still on."
                && AppText.ChangeFailedText("access is denied", true) == "Access is denied. It is still off.");

            // Startup list filter and sort.
            Check("filter: all matches every verdict", StartupFilter.Matches(VerdictFilter.All, Verdict.Keep) && StartupFilter.Matches(VerdictFilter.All, Verdict.Unknown));
            Check("filter: safe only safe", StartupFilter.Matches(VerdictFilter.Safe, Verdict.Safe) && !StartupFilter.Matches(VerdictFilter.Safe, Verdict.Caution));
            Check("filter: unknown only unknown", StartupFilter.Matches(VerdictFilter.Unknown, Verdict.Unknown) && !StartupFilter.Matches(VerdictFilter.Unknown, Verdict.Keep));
            Check("search: blank matches", StartupFilter.Matches("  ", "Xbox", "XblGameSave") && StartupFilter.Matches(null, "Xbox", "x"));
            Check("search: label or name, any case", StartupFilter.Matches("xbox", "Xbox Live", "a") && StartupFilter.Matches("gamesave", "Xbox", "XblGameSave")
                && !StartupFilter.Matches("teams", "Xbox", "XblGameSave"));
            Check("filter label: count", StartupFilter.Label(VerdictFilter.Safe, 9) == "Safe to turn off (9)" && StartupFilter.Label(VerdictFilter.All, 46) == "All verdicts (46)");
            Check("sort: verdict ranks safe first, keep last", StartupFilter.Compare(StartupSort.Verdict, Verdict.Safe, "z", Verdict.Keep, "a") < 0
                && StartupFilter.Compare(StartupSort.Verdict, Verdict.Unknown, "a", Verdict.Caution, "z") > 0);
            Check("sort: name ignores verdict", StartupFilter.Compare(StartupSort.Name, Verdict.Keep, "alpha", Verdict.Safe, "Beta") < 0);
            Check("sort: same verdict by name", StartupFilter.Compare(StartupSort.Verdict, Verdict.Safe, "beta", Verdict.Safe, "Alpha") > 0);
            Check("power description", AppText.PowerDescription(back) == "every area, plus 2 chosen services if running, until restart");
            Check("status: services message", AppText.Status(new AreaResult { Area = Areas.Services, Ok = true,
                Message = "stopped 2 services" }) == "ok: stopped 2 services");
            ServiceRule maps = ServiceCatalog.Find("MapsBroker");
            Check("detail: shared process", AppText.ServiceDetail(maps, new ServiceState { Running = true, SharedWith = 3, MemoryBytes = 5 })
                .Contains("Shares a process"));
            Check("detail: not running", AppText.ServiceDetail(maps, null).EndsWith("Not running"));
            Check("detail: trigger note", AppText.ServiceDetail(ServiceCatalog.Find("lfsvc"), null).Contains("start it again by itself"));
        }

        const ulong GB = 1024UL * 1024 * 1024;

        static MemorySnapshot Snap(ulong total, ulong available, uint load)
        {
            return new MemorySnapshot { TotalPhys = total, AvailablePhys = available, LoadPercent = load };
        }

        // ---------------------------------------------------------------- area status words

        static void StatusTests()
        {
            Check("status: ok", AppText.Status(new AreaResult { Area = Areas.WorkingSets, Ok = true }) == "ok");
            Check("status: passes shown", AppText.Status(new AreaResult { Area = Areas.WorkingSets, Ok = true, Message = "3 passes" }) == "ok: 3 passes");
            Check("status: partial volume flush shown", AppText.Status(new AreaResult { Area = Areas.VolumeCache, Ok = true,
                Message = "flushed 1 of 2 drives; E: access is denied" }) == "ok: flushed 1 of 2 drives; E: access is denied");
            Check("status: privilege -> admin words", AppText.Status(new AreaResult
                {
                    Area = Areas.StandbyList, NtStatus = Native.STATUS_PRIVILEGE_NOT_HELD,
                    Message = Cleaner.DescribeStatus(Native.STATUS_PRIVILEGE_NOT_HELD)
                }) == "failed: needs administrator rights");
            Check("status: access denied -> admin words",
                AppText.Status(new AreaResult { Area = Areas.ModifiedList, NtStatus = Native.STATUS_ACCESS_DENIED }) == "failed: needs administrator rights");
            Check("status: Win32 privilege error -> admin words",
                AppText.Status(new AreaResult { Area = Areas.SystemFileCache, Win32Error = 1314, Message = "A required privilege is not held by the client." })
                == "failed: needs administrator rights");
            string other = AppText.Status(new AreaResult
            {
                Area = Areas.CombinePages, NtStatus = unchecked((int)0xC00000BB), Message = Cleaner.DescribeStatus(unchecked((int)0xC00000BB))
            });
            Check("status: other NTSTATUS -> not supported (0x...)", other == "failed: not supported on this PC (0xC00000BB)", other);
            string win32 = AppText.Status(new AreaResult { Area = Areas.SystemFileCache, Win32Error = 87, Message = "The parameter is incorrect." });
            Check("status: Win32 failure keeps its words and code", win32 == "failed: the parameter is incorrect (error 87)", win32);
            string thrown = AppText.Status(new AreaResult { Area = Areas.WorkingSets, Message = "Out of memory." });
            Check("status: exception message in plain words", thrown == "failed: out of memory", thrown);
            Check("status: skipped keeps the version reason",
                AppText.Status(new AreaResult { Area = Areas.RegistryCache, Skipped = true, Message = "needs Windows 8.1 or later" })
                == "skipped: needs Windows 8.1 or later");
            Check("status: skipped without reason", AppText.Status(new AreaResult { Skipped = true }) == "skipped: not available on this PC");
            Check("plain: keeps acronyms", AppText.Plain("NTFS volume is dirty.", "x") == "NTFS volume is dirty");

            AreaLine line = AppText.Describe(new AreaResult { Area = Areas.CombinePages, NtStatus = unchecked((int)0xC00000BB) });
            Check("describe: label, state, words", line.Label == "Combine identical pages" && line.State == AreaState.Failed
                && line.Status.StartsWith("failed: "));
            Check("describe: skipped state", AppText.Describe(new AreaResult { Area = Areas.RegistryCache, Skipped = true }).State == AreaState.Skipped);
            // A partial area keeps its "ok: ..." words, but its row gets the caution state, matching the summary's warning.
            line = AppText.Describe(new AreaResult { Area = Areas.VolumeCache, Ok = true, Partial = true, Message = "Flushed 1 of 2 drives; E: access is denied" });
            Check("describe: a partial area has its own state and keeps its ok words", line.State == AreaState.Partial
                && line.Status == "ok: flushed 1 of 2 drives; E: access is denied", line.State + " " + line.Status);
            Check("describe: a full ok stays ok", AppText.Describe(new AreaResult { Area = Areas.WorkingSets, Ok = true }).State == AreaState.Ok);
        }

        // ---------------------------------------------------------------- clean report wording

        static void ReportTests()
        {
            var report = new CleanReport
            {
                Before = Snap(152 * GB / 10, 15 * GB / 10, 90),
                After = Snap(152 * GB / 10, 27 * GB / 10, 82)
            };
            for (int i = 0; i < 5; i++) report.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            report.Results.Add(new AreaResult { Area = Areas.CombinePages, NtStatus = unchecked((int)0xC00000BB) });
            Check("headline: freed", AppText.Headline(report) == "Freed 1.2 GB", AppText.Headline(report));
            string summary = AppText.Summary(report);
            Check("summary: in use from/to, load, count", summary == "In use went from 13.7 GB to 12.5 GB (82% load). 5 of 6 areas cleaned.", summary);

            var worse = new CleanReport { Before = Snap(100, 60, 40), After = Snap(100, 50, 50) };
            worse.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            Check("headline: nothing freed when in use grew", AppText.Headline(worse) == "Nothing freed");
            Check("summary: singular area", AppText.Summary(worse).EndsWith("1 of 1 area cleaned."), AppText.Summary(worse));
            Check("headline: no report", AppText.Headline(null) == "Clean did not finish");
            Check("failed summary names the error", AppText.FailedSummary(new InvalidOperationException("Boom.")) == "The clean stopped: boom.");

            var culture = CultureInfo.InvariantCulture;
            var at = new DateTime(2026, 11, 11, 14, 32, 0);
            Check("last auto-clean: freed", AppText.LastAutoClean(at, report, culture) == "Last auto-clean at 14:32: freed 1.2 GB, 1 area failed",
                AppText.LastAutoClean(at, report, culture));
            Check("last auto-clean: no report", AppText.LastAutoClean(at, null, culture) == "Last auto-clean at 14:32 did not finish");
        }

        // ---------------------------------------------------------------- threshold box

        static void ThresholdTests()
        {
            int v;
            Check("threshold: 50 and 99 are the bounds", ThresholdInput.TryParse("50", out v) && v == 50
                && ThresholdInput.TryParse("99", out v) && v == 99);
            Check("threshold: spaces trimmed", ThresholdInput.TryParse(" 85 ", out v) && v == 85);
            foreach (string bad in new[] { "49", "100", "120", "", " ", "8.5", "85%", "-60", "+60", "abc", "0085", null })
                Check("threshold: rejects '" + bad + "'", !ThresholdInput.TryParse(bad, out v));
            Check("threshold: Arabic-Indic digits rejected", !ThresholdInput.TryParse(new string(new[] { (char)0x0668, (char)0x0665 }), out v));
            Check("threshold message matches the range", AppText.ThresholdError == "Enter a whole number from 50 to 99.", AppText.ThresholdError);
        }

        // ---------------------------------------------------------------- areas

        static void AreaTests()
        {
            var labels = new List<string>();
            foreach (var kv in AreaNames.Known) labels.Add(AppText.AreaLabel(kv.Value));
            Check("area labels in run order", string.Join("|", labels.ToArray()) == "Combine identical pages|Process working sets|System file cache|Drive write cache (writes to disk)|Modified pages|"
                + "Low-priority standby|Full standby cache (slower afterwards)|Registry cache", string.Join("|", labels.ToArray()));
            Check("standby help text", AppText.StandbyHelp == "Standby is Windows' cache of recent files. It already counts as available, "
                + "so clearing it frees nothing you lack and apps load slower until it refills.");

            Check("areas: last one cannot be unticked", !AreaRules.CanUntick(Areas.RegistryCache, Areas.RegistryCache));
            Check("areas: one of two can be unticked", AreaRules.CanUntick(Areas.RegistryCache | Areas.WorkingSets, Areas.RegistryCache));
            Check("areas: an unticked one is always free", AreaRules.CanUntick(Areas.WorkingSets, Areas.StandbyList));
            Check("areas: toggle refuses to empty the list", AreaRules.Toggle(Areas.WorkingSets, Areas.WorkingSets, false) == Areas.WorkingSets);
            Check("areas: toggle on and off", AreaRules.Toggle(Areas.Default, Areas.StandbyList, true) == (Areas.Default | Areas.StandbyList)
                && AreaRules.Toggle(Areas.All, Areas.StandbyList, false) == (Areas.All & ~Areas.StandbyList));
        }

        // ---------------------------------------------------------------- theme

        static void ThemeTests()
        {
            Check("theme: system follows apps light", ThemeResolver.Resolve(ThemeMode.System, 1, false) == ResolvedTheme.Light);
            Check("theme: system follows apps dark", ThemeResolver.Resolve(ThemeMode.System, 0, false) == ResolvedTheme.Dark);
            Check("theme: missing value means light", ThemeResolver.Resolve(ThemeMode.System, null, false) == ResolvedTheme.Light);
            Check("theme: override beats Windows", ThemeResolver.Resolve(ThemeMode.Dark, 1, false) == ResolvedTheme.Dark
                && ThemeResolver.Resolve(ThemeMode.Light, 0, false) == ResolvedTheme.Light);
            Check("theme: high contrast beats everything", ThemeResolver.Resolve(ThemeMode.Dark, 0, true) == ResolvedTheme.HighContrast);
            Check("taskbar: missing value is dark", !ThemeResolver.TaskbarIsLight(null) && ThemeResolver.TaskbarIsLight(1) && !ThemeResolver.TaskbarIsLight(0));
            Check("tray digits: white on dark, #1A1A1A on light",
                Color.FromArgb(ThemeResolver.TrayTextArgb(false)).ToArgb() == Color.White.ToArgb()
                && Color.FromArgb(ThemeResolver.TrayTextArgb(true)).ToArgb() == Color.FromArgb(0x1A, 0x1A, 0x1A).ToArgb());
            Check("theme labels", ThemeResolver.ThemeLabel(ThemeMode.System) == "Match Windows" && ThemeResolver.ThemeLabel(ThemeMode.Dark) == "Dark");
        }

        // ---------------------------------------------------------------- other wording

        static void TextTests()
        {
            MemorySnapshot s = Snap(152 * GB / 10, 15 * GB / 10, 89);
            Check("in-use line", AppText.InUseLine(s) == "13.7 GB of 15.2 GB in use", AppText.InUseLine(s));
            Check("load card name joins number and line", AppText.LoadCardName(s) == "Memory load 89 percent, 13.7 GB of 15.2 GB in use", AppText.LoadCardName(s));
            Check("tooltip", AppText.TrayTooltip(s) == "RAM Optimiser: 89% (13.7 GB of 15.2 GB)", AppText.TrayTooltip(s));
            Check("tooltip when unavailable", AppText.TrayTooltip(null) == "RAM Optimiser: memory status unavailable");
            MemorySnapshot huge = Snap(ulong.MaxValue / 2, 1, 100);
            Check("tooltip fits NotifyIcon (63)", AppText.TrayTooltip(huge).Length <= AppText.MaxTooltipLength, AppText.TrayTooltip(huge));
            Check("notice mentions the lists only when they are refused",
                !AppText.ElevationNotice(true).Contains("standby") && AppText.ElevationNotice(false).Contains("standby and modified lists"));
            Check("row name spells out +", AppText.RowName("Available", "free + standby cache", "1.6 GB") == "Available, free plus standby cache: 1.6 GB");

            Check("auto description", AppText.AutoDescription(new AppSettings { ThresholdPercent = 85 }) == "when load reaches 85%");
            Check("auto description with interval", AppText.AutoDescription(new AppSettings { ThresholdPercent = 70, IntervalMinutes = 60 })
                == "when load reaches 70%, and every hour");
            Check("cooldown words", AppText.CooldownText(300) == "at most every 5 minutes" && AppText.CooldownText(60) == "at most once a minute"
                && AppText.CooldownText(0) == "no minimum wait between cleans" && AppText.CooldownText(90) == "at most every 90 seconds");
            Check("interval labels", AppText.IntervalLabel(0) == "Off" && AppText.IntervalLabel(15) == "Every 15 minutes"
                && AppText.IntervalLabel(60) == "Every hour" && AppText.IntervalLabel(120) == "Every 2 hours");
            Check("interval choices", string.Join(",", Array.ConvertAll(AppText.IntervalChoices(0), i => i.ToString())) == "0,15,30,60,120");
            Check("interval choices keep a custom value", string.Join(",", Array.ConvertAll(AppText.IntervalChoices(45), i => i.ToString())) == "0,15,30,45,60,120");
            Check("interval choices ignore out-of-range", AppText.IntervalChoices(5000).Length == 5);
        }

        // ---------------------------------------------------------------- memory map (redesign B)

        static MemorySnapshot ListSnap(double totalGb, double availableGb, double freeGb, double standbyGb, double modifiedGb)
        {
            return new MemorySnapshot
            {
                TotalPhys = (ulong)(totalGb * GB), AvailablePhys = (ulong)(availableGb * GB), HasLists = true,
                FreeAndZero = (ulong)(freeGb * GB), Standby = (ulong)(standbyGb * GB), Modified = (ulong)(modifiedGb * GB),
                LoadPercent = (uint)Math.Round((totalGb - availableGb) * 100 / totalGb)
            };
        }

        static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-9; }

        static void MapTests()
        {
            // In use on the map leaves out modified pages, so the four parts add up to the whole (as Task Manager does).
            MemoryMap m = MemoryMap.From(ListSnap(16, 4, 1, 3, 0.5));
            Check("map: detailed when lists are readable", m.Detailed);
            Check("map: in use excludes modified", m.Bytes(MapPart.InUse) == (ulong)(11.5 * GB), m.Bytes(MapPart.InUse).ToString());
            Check("map: modified, standby, free from the lists", m.Bytes(MapPart.Modified) == (ulong)(0.5 * GB)
                && m.Bytes(MapPart.Standby) == 3 * GB && m.Bytes(MapPart.Free) == GB);
            Check("map: total is the sum of the parts", m.Total == 16 * GB, m.Total.ToString());
            Check("map: fractions by bytes", Near(m.Fraction(MapPart.InUse), 11.5 / 16) && Near(m.Fraction(MapPart.Modified), 0.5 / 16)
                && Near(m.Fraction(MapPart.Standby), 3.0 / 16) && Near(m.Fraction(MapPart.Free), 1.0 / 16), m.Fraction(MapPart.InUse).ToString());
            double sum = 0;
            foreach (MapPart p in MemoryMap.Parts) sum += m.Fraction(p);
            Check("map: fractions add up to 1", Near(sum, 1), sum.ToString(CultureInfo.InvariantCulture));
            Check("map: parts in map order", string.Join(",", Array.ConvertAll(MemoryMap.Parts, p => p.ToString())) == "InUse,Modified,Standby,Free");

            // Lists and the load are read a moment apart: modified can briefly exceed in use. Never wraps around.
            MemorySnapshot oddSnap = ListSnap(16, 15.9, 15, 0.9, 0.5);
            MemoryMap odd = MemoryMap.From(oddSnap);
            Check("map: modified larger than in use is clamped", odd.Bytes(MapPart.InUse) == 0 && odd.Bytes(MapPart.Modified) == oddSnap.InUse,
                odd.Bytes(MapPart.InUse) + " / " + odd.Bytes(MapPart.Modified));

            MemoryMap plain = MemoryMap.From(Snap(16 * GB, 4 * GB, 75));
            Check("map: without lists only in use and available", !plain.Detailed && plain.Bytes(MapPart.InUse) == 12 * GB
                && plain.Bytes(MapPart.Free) == 4 * GB && plain.Bytes(MapPart.Modified) == 0 && plain.Bytes(MapPart.Standby) == 0);
            Check("map: without lists in use is 3/4", Near(plain.Fraction(MapPart.InUse), 0.75));

            MemoryMap empty = MemoryMap.From(new MemorySnapshot());
            bool finite = true;
            foreach (MapPart p in MemoryMap.Parts) finite &= empty.Fraction(p) == 0;
            Check("map: empty snapshot gives zero fractions, never NaN", finite);
            Check("map: null snapshot is refused", Throws(() => MemoryMap.From(null)));

            MemoryMap before = MemoryMap.From(ListSnap(15.2, 0.8, 0.083, 0.714, 0.026));
            MemoryMap after = MemoryMap.From(ListSnap(15.2, 3.6, 0.2, 3.4, 0.004));
            long inUseChange = MemoryMap.Change(before, after, MapPart.InUse);
            Check("map change: in use dropped", inUseChange < 0 && AppText.Change(inUseChange) == "-2.8 GB", AppText.Change(inUseChange));
            Check("map change: standby grew", AppText.Change(MemoryMap.Change(before, after, MapPart.Standby)) == "+2.7 GB",
                AppText.Change(MemoryMap.Change(before, after, MapPart.Standby)));
            Check("map change: free grew in MB", AppText.Change(MemoryMap.Change(before, after, MapPart.Free)) == "+119.8 MB",
                AppText.Change(MemoryMap.Change(before, after, MapPart.Free)));
            Check("map change: modified fell", AppText.Change(MemoryMap.Change(before, after, MapPart.Modified)) == "-22.5 MB",
                AppText.Change(MemoryMap.Change(before, after, MapPart.Modified)));

            Check("change: under 1 MB reads as no change", AppText.Change(0) == "0 MB" && AppText.Change(512 * 1024) == "0 MB"
                && AppText.Change(-1000) == "0 MB" && AppText.IsNoChange(512 * 1024) && !AppText.IsNoChange(2 * 1024 * 1024));
            Check("change: always signed", AppText.Change(3 * 1024 * 1024) == "+3.0 MB" && AppText.Change(-(long)GB) == "-1.0 GB");

            Check("headline caption", AppText.HeadlineCaption(Snap(152 * GB / 10, 15 * GB / 10, 89)) == "of 15.2 GB in use",
                AppText.HeadlineCaption(Snap(152 * GB / 10, 15 * GB / 10, 89)));
            string name = AppText.MapName(MemoryMap.From(ListSnap(16, 4, 1, 3, 0.5)), 16 * GB);
            Check("map name lists every part as text", name == "Memory map of 16.0 GB: in use 11.5 GB, modified 512.0 MB, standby 3.0 GB, free 1.0 GB", name);
            string plainName = AppText.MapName(plain, 16 * GB);
            Check("map name without lists", plainName == "Memory map of 16.0 GB: in use 12.0 GB, available 4.0 GB", plainName);
            Check("lists note says why parts are missing", AppText.ListsNeedAdmin == "Modified, standby and free need administrator rights to read.");
            Check("lists note by elevation", AppText.ListsNote(false) == AppText.ListsNeedAdmin
                && AppText.ListsNote(true) == "Modified, standby and free could not be read.");
            Check("admin line", AppText.AdminLine == "Needs administrator rights to clean.");
            Check("clean help points below", AppText.CleanNeedsAdmin.Contains("below") && !AppText.CleanNeedsAdmin.Contains("above"), AppText.CleanNeedsAdmin);

            Check("row name with change", AppText.RowName("In use", null, "11.6 GB", "-2.8 GB") == "In use: 11.6 GB, changed by -2.8 GB",
                AppText.RowName("In use", null, "11.6 GB", "-2.8 GB"));
            Check("row name with no change", AppText.RowName("Free", null, "82.6 MB", null) == "Free: 82.6 MB");

            var report = new CleanReport { Before = ListSnap(15.2, 0.8, 0.083, 0.714, 0.026), After = ListSnap(15.2, 3.6, 0.2, 3.4, 0.004) };
            for (int i = 0; i < 5; i++) report.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            report.Results.Add(new AreaResult { Area = Areas.CombinePages, NtStatus = unchecked((int)0xC00000BB) });
            report.Results.Add(new AreaResult { Area = Areas.RegistryCache, Skipped = true });
            Check("result sentence: one sentence with counts and load", AppText.ResultSentence(report) == "5 of 7 areas cleaned, 1 failed; load is now 76%.",
                AppText.ResultSentence(report));
            var clean = new CleanReport { Before = report.Before, After = report.After };
            clean.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true });
            Check("result sentence: singular, no failures", AppText.ResultSentence(clean) == "1 of 1 area cleaned; load is now 76%.", AppText.ResultSentence(clean));
            Check("result sentence: no report", AppText.ResultSentence(null) == "The clean stopped before it could report a result.");
            Check("details label", AppText.DetailsLabel(false, 7) == "Show 7 areas" && AppText.DetailsLabel(true, 7) == "Hide areas"
                && AppText.DetailsLabel(false, 1) == "Show 1 area");
        }

        static bool Throws(Action a)
        {
            try { a(); return false; }
            catch (ArgumentNullException) { return true; }
        }

        static void LaunchTests()
        {
            Check("launch: no args shows the window", !LaunchOptions.Parse(new string[0]).StartInTray && !LaunchOptions.Parse(null).StartInTray);
            Check("launch: --tray", LaunchOptions.Parse(new[] { "--tray" }).StartInTray && LaunchOptions.Parse(new[] { "x", "--TRAY" }).StartInTray);
            Check("launch: unknown args ignored", !LaunchOptions.Parse(new[] { "-tray", "tray" }).StartInTray);

            // P9: the elevated copy is told who launched it; only a well-formed SID is kept.
            const string Sid = "S-1-5-21-1-2-3-1001";
            LaunchOptions lo = LaunchOptions.Parse(new[] { Elevation.LauncherArgument, Sid });
            Check("launch: launcher SID", lo.LauncherSid == Sid && !lo.StartInTray, lo.LauncherSid);
            Check("launch: no launcher by default", LaunchOptions.Parse(new string[0]).LauncherSid == null
                && LaunchOptions.Parse(null).LauncherSid == null);
            Check("launch: a malformed launcher SID is dropped", LaunchOptions.Parse(new[] { Elevation.LauncherArgument, "S-1-5-18\" x" }).LauncherSid == null
                && LaunchOptions.Parse(new[] { Elevation.LauncherArgument }).LauncherSid == null
                && LaunchOptions.Parse(new[] { Elevation.LauncherArgument, "s-1-5-18" }).LauncherSid == null);
            LaunchOptions both = LaunchOptions.Parse(new[] { Elevation.LauncherArgument, "--tray" });
            Check("launch: --tray after an empty launcher still counts", both.StartInTray && both.LauncherSid == null);
            Check("relaunch arguments carry the launcher SID", Elevation.RelaunchArgs(Sid) == "--launched-by " + Sid, Elevation.RelaunchArgs(Sid));
            bool refused = false;
            try { Elevation.RelaunchArgs("S-1-5-18 --tray"); }
            catch (ArgumentException) { refused = true; }
            Check("relaunch arguments refuse a malformed SID", refused);

            // P10: the elevated copy is told which pid to wait for; only a single strict token counts.
            Check("handoff: none by default", LaunchOptions.Parse(new string[0]).HandoffFrom == 0 && LaunchOptions.Parse(null).HandoffFrom == 0);
            Check("handoff: --handoff=1234", LaunchOptions.Parse(new[] { "--handoff=1234" }).HandoffFrom == 1234);
            Check("handoff: case-insensitive", LaunchOptions.Parse(new[] { "--HANDOFF=1234" }).HandoffFrom == 1234);
            Check("handoff: empty value", LaunchOptions.Parse(new[] { "--handoff=" }).HandoffFrom == 0);
            Check("handoff: negative", LaunchOptions.Parse(new[] { "--handoff=-5" }).HandoffFrom == 0);
            Check("handoff: not digits", LaunchOptions.Parse(new[] { "--handoff=12a" }).HandoffFrom == 0
                && LaunchOptions.Parse(new[] { "--handoff= 12" }).HandoffFrom == 0 && LaunchOptions.Parse(new[] { "--handoff=+12" }).HandoffFrom == 0);
            Check("handoff: overflow", LaunchOptions.Parse(new[] { "--handoff=99999999999" }).HandoffFrom == 0);
            Check("handoff: zero", LaunchOptions.Parse(new[] { "--handoff=0" }).HandoffFrom == 0);
            Check("handoff: two tokens do not count", LaunchOptions.Parse(new[] { "--handoff", "1234" }).HandoffFrom == 0);
            LaunchOptions tray = LaunchOptions.Parse(new[] { "--tray", "--handoff=7" });
            Check("handoff: with --tray", tray.StartInTray && tray.HandoffFrom == 7);
            Check("handoff: the first valid one wins", LaunchOptions.Parse(new[] { "--handoff=x", "--handoff=8", "--handoff=9" }).HandoffFrom == 8);
            LaunchOptions all = LaunchOptions.Parse(new[] { Elevation.LauncherArgument, Sid, Elevation.HandoffArgs(42) });
            Check("handoff: alongside the launcher SID", all.LauncherSid == Sid && all.HandoffFrom == 42);
            StartWithWindowsTextTests();
        }

        /// <summary>P2 and P9: what the Settings tab says next to Start with Windows, decided without WPF.</summary>
        static void StartWithWindowsTextTests()
        {
            var absent = new StartupTaskStatus { State = StartupTaskState.Absent };
            var ours = new StartupTaskStatus { State = StartupTaskState.Ours };
            var copy = new StartupTaskStatus { State = StartupTaskState.Other, Command = @"C:\Users\me\bin\RamOptimiser.exe" };
            var same = new StartupTaskStatus { State = StartupTaskState.Other, Command = @"C:\Apps\RamOptimiser.exe", SameProgram = true };
            var odd = new StartupTaskStatus { State = StartupTaskState.Other };
            var denied = new StartupTaskStatus { State = StartupTaskState.Unreadable, Error = "Access is denied." };

            Check("startup line: none when absent or ours", AppText.StartupStatusLine(absent) == null && AppText.StartupStatusLine(ours) == null
                && AppText.StartupStatusLine(null) == null);
            string line = AppText.StartupStatusLine(copy);
            Check("startup line: a stale task names the other copy", line == @"A start-up task exists but starts another copy: C:\Users\me\bin\RamOptimiser.exe. Turning this on replaces it.", line);
            line = AppText.StartupStatusLine(same);
            Check("startup line: this program with other settings", line == "A start-up task for this program exists with other settings. Turning this on replaces it.", line);
            line = AppText.StartupStatusLine(odd);
            Check("startup line: a task that starts something else", line == "A start-up task exists but does not start this program. Turning this on replaces it.", line);
            line = AppText.StartupStatusLine(denied);
            Check("startup line: an unreadable task is not shown as off", line == "Could not read the start-up task: Access is denied.", line);

            Check("startup reason: none when ours and elevated", AppText.StartupReason(ours, true, null, null) == null);
            Check("startup reason: needs admin", AppText.StartupReason(absent, false, null, null) == AppText.StartupNeedsAdmin);
            string reason = AppText.StartupReason(copy, false, null, null);
            Check("startup reason: a stale task shows without admin rights too", reason == AppText.StartupStatusLine(copy) + " " + AppText.StartupNeedsAdmin, reason);
            reason = AppText.StartupReason(copy, true, null, "Could not change the startup task: boom");
            Check("startup reason: a failure comes first", reason == "Could not change the startup task: boom " + AppText.StartupStatusLine(copy), reason);
            reason = AppText.StartupReason(copy, true, @"PC\admin", null);
            Check("startup reason: another account says only that", reason == AppText.StartupOtherAccount(@"PC\admin"), reason);
            Check("startup other account names it", AppText.StartupOtherAccount(@"PC\admin")
                == @"Can't change this here: this copy runs as PC\admin, so the task would start for that account, not yours.", AppText.StartupOtherAccount(@"PC\admin"));
            Check("account notice names the account", AppText.OtherAccountNotice(@"PC\admin")
                == @"Running as PC\admin, the account that approved the administrator prompt. This copy uses that account's settings and start-up task, not yours.",
                AppText.OtherAccountNotice(@"PC\admin"));

            Check("startup switch: enabled only elevated, idle and as yourself", AppText.StartupSwitchEnabled(true, false, null)
                && !AppText.StartupSwitchEnabled(false, false, null) && !AppText.StartupSwitchEnabled(true, true, null)
                && !AppText.StartupSwitchEnabled(true, false, @"PC\admin"));
        }

        // ---------------------------------------------------------------- error log

        static void ErrorLogTests()
        {
            string entry = ErrorLog.FormatEntry(new DateTime(2026, 11, 11, 9, 5, 7, DateTimeKind.Utc), "test", new InvalidOperationException("boom"));
            Check("log entry: time, context, exception", entry.StartsWith("2026-11-11 09:05:07Z test\r\n") && entry.Contains("InvalidOperationException: boom"), entry);
            Check("default log path", ErrorLog.DefaultPath.EndsWith(Path.Combine("RamOptimiser", "error.log"), StringComparison.OrdinalIgnoreCase));

            string dir = Path.Combine(Path.GetTempPath(), "ramopt-log-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(Path.Combine(dir, "sub"), "error.log");
            try
            {
                Check("log: creates the folder and writes", ErrorLog.TryWrite(path, "a", new Exception("first")) && File.ReadAllText(path).Contains("first"));
                ErrorLog.TryWrite(path, "b", new Exception("second"));
                string text = File.ReadAllText(path);
                Check("log: appends", text.Contains("first") && text.Contains("second"));
                File.WriteAllBytes(path, new byte[ErrorLog.MaxBytes + 10]);
                ErrorLog.TryWrite(path, "c", new Exception("third"));
                Check("log: rolls over above the cap", new FileInfo(path).Length < 10000 && File.Exists(path + ".old"));
                string blocker = Path.Combine(dir, "blocker");
                File.WriteAllText(blocker, "a file where the log folder should be");
                Check("log: never throws on an unusable path", !ErrorLog.TryWrite(Path.Combine(blocker, "error.log"), "d", null)
                    && !ErrorLog.TryWrite(dir + "\\bad\0name.log", "e", null));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        // ---------------------------------------------------------------- app icon

        static void IconTests()
        {
            byte[] ico = AppIcon.BuildDefaultIco();
            Check("ico header", ico.Length > 6 && ico[0] == 0 && ico[1] == 0 && ico[2] == 1 && ico[4] == 5, "count " + (ico.Length > 4 ? ico[4] : -1));
            var sizes = new List<int>();
            for (int i = 0; i < 5; i++) sizes.Add(ico[6 + i * 16] == 0 ? 256 : ico[6 + i * 16]);
            Check("ico sizes 16 24 32 48 256", string.Join(",", Array.ConvertAll(sizes.ToArray(), i => i.ToString())) == "16,24,32,48,256");
            foreach (int size in new[] { 16, 32, 48 })
            {
                using (var ms = new MemoryStream(ico))
                using (var icon = new Icon(ms, size, size))
                    Check("ico loads at " + size, icon.Width == size && icon.Height == size, icon.Size.ToString());
            }
            using (Bitmap b = AppIcon.Render(16))
            {
                Color corner = b.GetPixel(0, 0), body = b.GetPixel(8, 3), bar = b.GetPixel(5, 7);
                Check("icon: transparent corner", corner.A == 0);
                Check("icon: accent body", body.ToArgb() == AppIcon.Accent.ToArgb(), body.ToString());
                Check("icon: white bar", bar.ToArgb() == Color.White.ToArgb(), bar.ToString());
            }
        }

        // ---------------------------------------------------------------- XAML (static checks)

        static string XamlDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetFullPath(Path.Combine(dir, Path.Combine("..", Path.Combine("src", Path.Combine("App", "Xaml")))));
        }

        static void XamlTests()
        {
            string dir = XamlDir();
            if (!Directory.Exists(dir)) { Check("XAML folder found", false, dir); return; }
            var docs = new Dictionary<string, XmlDocument>();
            foreach (string name in new[] { "Light.xaml", "Dark.xaml", "Styles.xaml", "MainWindow.xaml" })
            {
                var d = new XmlDocument();
                try { d.Load(Path.Combine(dir, name)); docs[name] = d; Check(name + " is well-formed", true); }
                catch (Exception ex) { Check(name + " is well-formed", false, ex.Message); }
            }
            if (docs.Count != 4) return;

            List<string> light = Keys(docs["Light.xaml"]), dark = Keys(docs["Dark.xaml"]);
            Check("light and dark define the same tokens", string.Join(",", light.ToArray()) == string.Join(",", dark.ToArray()),
                string.Join(",", light.ToArray()) + " vs " + string.Join(",", dark.ToArray()));

            foreach (string key in new[] { "MapInUse", "MapModified", "MapStandby", "MapFree", "Caution", "DangerBackground" })
                Check("redesign A token " + key + " defined", light.Contains(key));
            string main = File.ReadAllText(Path.Combine(dir, "MainWindow.xaml"));
            string styles = File.ReadAllText(Path.Combine(dir, "Styles.xaml"));
            // U19: high contrast maps the track fill to the card's colour, so tracks and skeletons need an edge there.
            string themes = File.ReadAllText(Path.Combine(dir, Path.Combine("..", "ThemeManager.cs")));
            string shellCode = File.ReadAllText(Path.Combine(dir, Path.Combine("..", "AppShell.cs")));
            Check("track edge token in light and dark", light.Contains("TrackEdge") && TokenValue(docs["Light.xaml"], "TrackEdge") == TokenValue(docs["Light.xaml"], "TrackBackground")
                && TokenValue(docs["Dark.xaml"], "TrackEdge") == TokenValue(docs["Dark.xaml"], "TrackBackground"));
            Check("track edge is window text in high contrast", themes.Contains("d[\"TrackEdge\"] = SystemColors.WindowTextBrush;"));
            Check("skeletons have a track edge", Regex.IsMatch(styles, "x:Key=\"Skeleton\"(?:(?!</Style>)[\\s\\S])*?<Setter Property=\"BorderBrush\" Value=\"\\{DynamicResource TrackEdge\\}\"/>"));
            Check("the commit bar's rest has a track edge", Regex.IsMatch(main, "x:Name=\"CommitRest\"[^>]*BorderBrush=\"\\{DynamicResource TrackEdge\\}\""));
            Check("top app tracks have a track edge", shellCode.Contains("track.SetResourceReference(Border.BorderBrushProperty, \"TrackEdge\");"));
            Check("three tabs", main.Contains("x:Name=\"MemoryTab\"") && main.Contains("x:Name=\"StartupTab\"") && main.Contains("x:Name=\"SettingsTab\""));
            Check("commit charge shown", main.Contains("x:Name=\"CommitRow\""));
            Check("top apps shown", main.Contains("x:Name=\"TopList\""));
            Check("startup search and filter", main.Contains("x:Name=\"StartupSearch\"") && main.Contains("x:Name=\"VerdictBox\""));

            // One admin line: the old banner card and the second message under the button are gone.
            Check("no duplicate admin message under the button", !main.Contains("x:Name=\"CleanReason\""));
            Check("no admin banner card", !main.Contains("x:Name=\"ElevationNotice\""));
            Check("another-account notice exists", main.Contains("x:Name=\"AccountNotice\""));
            Check("figures use tabular numerals", styles.Contains("Typography.NumeralAlignment\" Value=\"Tabular\"") && main.Contains("FigureText"));

            // Styles and tokens share the application's resources: a shared key would make
            // {DynamicResource Card} resolve to a Style instead of a brush.
            foreach (string key in Keys(docs["Styles.xaml"]))
                Check("style key " + key + " does not shadow a token", !light.Contains(key));

            var colour = new Regex("#[0-9A-Fa-f]{3,8}\\b");
            foreach (string name in new[] { "Styles.xaml", "MainWindow.xaml" })
            {
                string text = Regex.Replace(File.ReadAllText(Path.Combine(dir, name)), "<!--.*?-->", "", RegexOptions.Singleline);
                Match m = colour.Match(text);
                Check(name + " has no raw colours", !m.Success, m.Success ? m.Value : null);
                foreach (Match r in new Regex("DynamicResource ([A-Za-z]+)").Matches(text))
                {
                    string key = r.Groups[1].Value;
                    if (key.EndsWith("Font")) continue;
                    Check(name + " token " + key + " exists", light.Contains(key));
                }
            }

            // Every interactive element in the window has an accessible name.
            var ns = new XmlNamespaceManager(docs["MainWindow.xaml"].NameTable);
            ns.AddNamespace("w", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
            foreach (string tag in new[] { "Button", "CheckBox", "TextBox", "ComboBox", "Expander", "ToggleButton" })
                foreach (XmlElement e in docs["MainWindow.xaml"].SelectNodes("//w:" + tag, ns))
                    Check(tag + " " + e.GetAttribute("x:Name") + " has AutomationProperties.Name", e.HasAttribute("AutomationProperties.Name"));

            // U10: a plain StackPanel has no automation peer, so a name on it never reaches UI Automation.
            XmlNodeList namedStacks = docs["MainWindow.xaml"].SelectNodes("//w:StackPanel[@AutomationProperties.Name]", ns);
            Check("no named StackPanel", namedStacks.Count == 0, namedStacks.Count > 0 ? ((XmlElement)namedStacks[0]).GetAttribute("AutomationProperties.Name") : null);
            ns.AddNamespace("app", "clr-namespace:RamOptimiser.App;assembly=RamOptimiser");
            ns.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
            foreach (string[] g in new[] { new[] { "TopList", "Apps using the most memory" }, new[] { "PowerList", "Services to stop" },
                         new[] { "StartupLoading", "Loading startup items" }, new[] { "StartupList", "Startup items" } })
            {
                var e = docs["MainWindow.xaml"].SelectSingleNode("//app:GroupPanel[@x:Name='" + g[0] + "']", ns) as XmlElement;
                Check("named group " + g[0] + " is a GroupPanel", e != null && e.GetAttribute("AutomationProperties.Name") == g[1],
                    e == null ? "not a GroupPanel" : e.GetAttribute("AutomationProperties.Name"));
            }
            var show = docs["MainWindow.xaml"].SelectSingleNode("//app:GroupPanel[@AutomationProperties.Name='Show']", ns) as XmlElement;
            Check("startup segment group is a GroupPanel", show != null && show.GetAttribute("Orientation") == "Horizontal");
            string automation = File.ReadAllText(Path.Combine(dir, Path.Combine("..", "Automation.cs")));
            Check("GroupPanel exposes a Group peer", Regex.IsMatch(automation, @"class GroupPanel : StackPanel")
                && automation.Contains("return new GroupPanelPeer(this);") && automation.Contains("AutomationControlType.Group; }"));
            // A locked startup row reads as one Text element whose name says whether it starts with Windows.
            Check("locked startup rows are SummaryPanels", shellCode.Contains("var lockedRow = new SummaryPanel")
                && shellCode.Contains("AutomationProperties.SetName(lockedRow, AppText.LockedRowName(it.Label, it.LockText, it.On));"));
        }

        /// <summary>A SolidColorBrush token's Color, or null.</summary>
        static string TokenValue(XmlDocument d, string key)
        {
            foreach (XmlNode n in d.DocumentElement.ChildNodes)
            {
                var e = n as XmlElement;
                if (e != null && e.GetAttribute("Key", "http://schemas.microsoft.com/winfx/2006/xaml") == key) return e.GetAttribute("Color");
            }
            return null;
        }

        static List<string> Keys(XmlDocument d)
        {
            var keys = new List<string>();
            foreach (XmlNode n in d.DocumentElement.ChildNodes)
            {
                var e = n as XmlElement;
                if (e != null) keys.Add(e.GetAttribute("Key", "http://schemas.microsoft.com/winfx/2006/xaml"));
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }
}
