using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using RamOptimiser.Gui;
using Path = System.Windows.Shapes.Path;

namespace RamOptimiser.App
{
    /// <summary>
    /// Owns the window, the tray icon and the auto-clean service, and keeps them in step with the settings.
    /// All state lives on the UI thread: service events arrive on worker threads and are posted here with
    /// Dispatcher.BeginInvoke. Wording, validation and theme rules live in AppText and friends (tested).
    /// The window has three tabs (design.md, redesign 2 option A): Memory, Startup and Settings.
    /// </summary>
    internal sealed partial class AppShell
    {
        static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
        /// <summary>Top apps are read every third sample (6 s) while the Memory tab is in view.</summary>
        const int TopAppsEvery = 3;
        const int TopAppsCount = 5;
        static readonly TimeSpan ThresholdSaveDelay = TimeSpan.FromMilliseconds(400);

        readonly Application app;
        readonly Dispatcher dispatcher;
        readonly LaunchOptions options;
        readonly string settingsPath;
        readonly bool elevated;
        SingleInstance instance;
        AppSettings settings;
        /// <summary>Restart as administrator: idle, waiting on the UAC prompt, or exiting (P10).</summary>
        ElevationState elevationState = ElevationState.Idle;
        bool restartHadFocus;
        /// <summary>The settings as last loaded or saved, and the file's stamp then: the merge's common ancestor (P12).</summary>
        AppSettings baseline;
        FileStamp stamp;
        SettingsSyncState syncState = SettingsSyncState.InSync;

        ThemeManager theme;
        Window window;
        TrayController tray;
        AutoCleanService service;

        MemorySnapshot lastSnapshot;
        Exception cleanError;
        bool exiting, cleaningUi, syncing, mapReady, taskbarLight;
        bool viewStale, wasShowing, resultShown;
        bool startupBusy, refocusClean, refocusStartup;
        string startupMessage;
        StartupTaskStatus startupStatus;
        /// <summary>DOMAIN\user when this elevated copy runs as another account than the one that started it.</summary>
        string otherAccount;
        int resultCount, sampleCount;
        bool topBusy;
        /// <summary>Initial, live, live with a failing read, or failed before any figure was read.</summary>
        readonly MemoryView memView = new MemoryView();
        /// <summary>The clean the result line shows (null after a start-up restore), so the line can say "yesterday".</summary>
        DateTime? resultAt;
        CleanReport resultReport;
        Exception resultError;
        bool resultAutomatic;

        TabControl tabs;
        TabItem memoryTab, startupTab, settingsTab;

        // Memory tab
        ScrollViewer mainView;
        LiveRegion sampleError, restartErrorRegion;
        StackPanel adminLine, topList;
        TextBlock sampleErrorText, restartError, elevationText, listsNote, freeLabel, powerNote, accountNotice, lastReadNote;
        SummaryPanel loadCard, rowInUse, rowModified, rowStandby, rowFree, commitRow;
        Run loadNumber, inUseRun;
        MapPanel mapBar;
        Border[] segments;
        Border commitUsed, commitRest;
        TextBlock commitValue, commitNote, commitWarningText;
        // U15: the warning near the commit limit (on at 90%, off below 87%).
        LiveRegion commitWarning;
        bool commitWarnOn;
        int commitWarnGeneration;
        // R1: will a clean help now, under Clean memory (display only), steadied over two samples.
        Grid adviceLine;
        Path adviceGlyph;
        TextBlock adviceText;
        readonly AdviceSmoother advice = new AdviceSmoother();
        Button cleanButton, restartButton;
        LiveRegion resultPanel;
        Path resultGlyph;
        TextBlock resultText;
        Expander resultDetails;
        StackPanel resultRows;
        TextBlock valInUse, valModified, valStandby, valFree;
        TextBlock autoDescription, autoStatusText, autoStatusDetail;
        LiveRegion autoStatusRegion;
        Path autoStatusGlyph;
        Button autoResumeButton;
        CheckBox autoSwitch;
        // The bar at the top of the Memory tab while auto-clean is paused or waiting longer (P6, R2).
        LiveRegion autoBar;
        Path autoBarGlyph;
        TextBlock autoBarTitle, autoBarDetail;
        Button autoBarResume, autoBarDismiss;
        /// <summary>The service's pause, back-off and hold as last read (null before the service exists).</summary>
        AutoCleanStatus autoInfo;
        /// <summary>Where the current pause came from (tray, Settings, an earlier run), for the bar's second line.</summary>
        PauseOrigin pauseOrigin;
        AutoNotice barNotice;
        /// <summary>The back-off message the user dismissed; AppText.NextAutoBar forgets it once that message goes.</summary>
        string dismissedBarKey;
        /// <summary>The kind of bar last read out (paused, backoff), so a streak step or a tick is not announced again.</summary>
        string announcedBarKind;
        bool barShown;
        int barGeneration;
        /// <summary>What the auto-clean card, bar and Settings rows last showed: a sample that changes none is skipped.</summary>
        string autoRenderKey;
        /// <summary>What the tray last showed for auto-clean, so the menu is rebuilt only when it changes.</summary>
        string trayAutoKey;

        // Power Clean (settings tab)
        CheckBox settingsPowerSwitch;
        StackPanel powerList;
        Expander powerServicesList;
        /// <summary>Catalog services shown in the list (installed), for the "N of M chosen" header.</summary>
        readonly HashSet<string> powerRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Button restoreButton;
        TextBlock powerStatus;
        bool servicesBusy;
        /// <summary>The chosen services as the clean thread sees them: replaced whole, never changed in place.</summary>
        volatile List<string> powerServices = new List<string>();

        // Startup tab
        ScrollViewer startupView;
        TextBlock startupSummary, startupNote, startupError, startupEmptyTitle, startupEmptyText;
        StackPanel startupList, startupLoadingPanel;
        Border startupCard;
        RadioButton showApps, showServices;
        Run appsCount, servicesCount;
        TextBox startupSearch;
        Button clearSearchButton, openStartupSettings;
        ComboBox verdictBox, sortBox;
        List<StartupApp> startupApps;
        List<StartupService> startupServices;
        /// <summary>Loading, ready, or applying a change (and the reload after it): switches work only when ready.</summary>
        readonly StartupFlow startupFlow = new StartupFlow();
        /// <summary>Errors shown under the row that failed, by row key, until dismissed or the row changes again.</summary>
        /// <summary>The error or note under a Startup row, by row key (E13): shown until dismissed or the next change.</summary>
        readonly Dictionary<string, RowNote> rowErrors = new Dictionary<string, RowNote>();
        /// <summary>Row errors already read out, so a rebuild (each search keystroke) does not repeat them.</summary>
        readonly AnnouncedErrors announcedErrors = new AnnouncedErrors();
        /// <summary>Tag of the control to focus after the list is rebuilt (a row's switch, by key).</summary>
        string refocusTag;
        // U9: the Startup tab's status lines, and the search count read out 300 ms after the last search or filter change.
        LiveRegion startupSummaryRegion, startupErrorRegion, startupEmptyRegion, searchCountRegion;
        TextBlock searchCount;
        DispatcherTimer searchAnnounceTimer;
        static readonly TimeSpan SearchAnnounceDelay = TimeSpan.FromMilliseconds(300);
        int searchShown;
        bool startupSummaryAnnounced;
        /// <summary>The ServiceStartup journal problem last written to error.log (QueryStartup, thread pool).</summary>
        string startupJournalProblem;

        // Settings tab
        CheckBox settingsAutoSwitch, startupCheck, closeToTrayCheck, trayPercentCheck;
        TextBlock saveError, cooldownText, areaReason, startupReason, thresholdValue, themeNote, pauseNote;
        Border pauseRow;
        Button pauseButton;
        LiveRegion areaReasonRegion;
        Slider thresholdSlider;
        DispatcherTimer thresholdTimer;
        /// <summary>A slider value waiting for its 400 ms pause; flushed on exit and before an elevated restart.</summary>
        readonly PendingSave thresholdPending = new PendingSave();
        ComboBox intervalBox, themeBox;
        Expander moreAreas;
        LiveRegion saveErrorRegion, powerStatusRegion;
        /// <summary>What each status line last read out (U9, AnnounceGate); hiding a line forgets it.</summary>
        readonly Dictionary<LiveRegion, string> announced = new Dictionary<LiveRegion, string>();

        // R6: opt-in problem notices, at most one of a kind an hour (NoticeGate).
        CheckBox notifyCheck;
        readonly Dictionary<NoticeKind, DateTime> noticeShownUtc = new Dictionary<NoticeKind, DateTime>();
        /// <summary>The back-off streak the notices last compared with (null before the first status).</summary>
        int? noticeStreak;

        // R7: the global Clean memory shortcut. The listener exists only when elevated; the binding always.
        TextBox hotkeyBox;
        Button hotkeyRemove;
        TextBlock hotkeyNote, hotkeyMessage;
        LiveRegion hotkeyMessageRegion;
        HotkeyListener hotkeyListener;
        HotkeyBinding hotkey = new HotkeyBinding(null);
        /// <summary>When the shortcut last started a clean, and when a press was last ignored by the 60 s floor.</summary>
        DateTime? hotkeyCleanUtc, hotkeySkippedUtc, hotkeySkipLoggedUtc;

        // P16: reset to defaults, confirmed inline.
        Button resetButton, resetYes, resetNo;
        StackPanel resetConfirm;
        LiveRegion resetStatusRegion;
        TextBlock resetStatus;

        // Activity (settings tab, P7 and R4): this session's cleans and auto-clean events, from memory only.
        StackPanel recentCleanRows, activityRows;
        TextBox activityPath;
        TextBlock activityPathNote;
        const int ActivityRowsShown = 20;
        readonly ActivityHistory activity = new ActivityHistory(ActivityRowsShown);
        /// <summary>The last clean, for the tray's tooltip line and status item; null before the first.</summary>
        LastCleanInfo lastClean;
        /// <summary>The auto-clean status the activity list last compared with, and whether the user just pressed Resume.</summary>
        AutoCleanStatus loggedStatus;
        bool userResumed;
        /// <summary>The last sampling failure put in the activity list (ErrorGate), and whether reading is failing now.</summary>
        string loggedReadError;
        DateTime? loggedReadErrorUtc;
        bool readFailing;
        readonly List<KeyValuePair<CheckBox, Areas>> areaBoxes = new List<KeyValuePair<CheckBox, Areas>>();
        static readonly Areas HiddenAreas = Areas.ModifiedList | Areas.VolumeCache | Areas.CombinePages | Areas.RegistryCache;

        public AppShell(Application app, SingleInstance instance, LaunchOptions options)
        {
            this.app = app;
            this.instance = instance;
            this.options = options;
            dispatcher = app.Dispatcher;
            settingsPath = SettingsStore.DefaultPath;
            settings = SettingsStore.Load(settingsPath).Normalized();
            baseline = settings.Clone();
            stamp = SettingsStore.Stamp(settingsPath);
            elevated = Elevation.IsElevated();
            // Over-the-shoulder UAC runs this copy as the administrator who typed the password. Only the name
            // of this account is shown; the launching user's own settings are never read from here.
            if (elevated && options.LauncherSid != null)
            {
                try
                {
                    if (Elevation.IsOtherAccount(options.LauncherSid, StartupTask.CurrentUserSid()))
                        otherAccount = StartupTask.CurrentUserId();
                }
                catch (Exception ex) { ErrorLog.TryWrite(ErrorLog.DefaultPath, "could not compare the launching account", ex); }
            }
        }

        static string ExePath { get { return Assembly.GetEntryAssembly().Location; } }

        // ------------------------------------------------------------------ start-up and shutdown

        public void Start()
        {
            theme = new ThemeManager(app);
            theme.Apply(settings.Theme);
            taskbarLight = theme.TaskbarIsLight();

            window = (Window)ThemeManager.LoadXaml("MainWindow.xaml");
            window.Icon = LoadWindowIcon();
            FindElements();
            FindVisuals();
            WireMain();
            WireStartup();
            WireSettings();
            WireVisuals();
            BuildChoices();
            ApplyElevationState();
            SyncSettingsUi(true);
            ShowThemeNote();
            // A chosen area behind the disclosure is shown, so the list never hides what will be cleaned.
            moreAreas.IsExpanded = (settings.Areas & HiddenAreas) != 0;
            RefreshAutoRow();

            tray = new TrayController();
            tray.OpenRequested += delegate { ShowWindow(); };
            tray.CleanRequested += delegate { RequestClean(); };
            tray.StatusClicked += delegate { ShowMemoryTab(); };
            tray.AutoCleanToggled += delegate { SetAutoClean(!settings.AutoCleanEnabled); };
            tray.PauseRequested += delegate { PauseAutoClean(PauseOrigin.Tray); };
            tray.ResumeRequested += delegate { ResumeAutoClean(); };
            tray.RestartRequested += delegate { RestartElevated(); };
            tray.ExitRequested += delegate { Exit(); };
            tray.NoticeClicked += delegate { ShowMemoryTab(); };
            CreateFlyout();
            UpdateTray();

            powerServices = new List<string>(settings.PowerServices);
            // Services are stopped only when the areas include Areas.Services, i.e. under Power Clean. Automatic cleans
            // wait while the PC sleeps or has just woken, a full-screen app runs, and (scheduled ones) while locked or
            // on battery (P5). Each clean takes the cross-process clean lock without waiting (one running in ramopt makes
            // this one skip and say so) and writes its record to cleans.log under it; automatic cleans stop at the
            // 120 s limit, manual ones have none (E9, E22, R4).
            service = new AutoCleanService(MemorySnapshot.Take,
                job => GuiCleanLog.Run(job, powerServices, CleanWithProgress, AppendLog, () => DateTime.UtcNow),
                () => DateTime.UtcNow, SystemConditions.AwakeTime, SystemConditions.Read, new GateOptions(), Cleaner.AutoCleanDeadline);
            // On the tick's worker thread: the append may wait briefly for another writer.
            service.Rebound += (entry, s) =>
            {
                AppendLog(CleanLog.FormatRebound(entry.Id, DateTime.UtcNow, s));
                Post(ShowHistory);
            };
            service.Sampled += s => Post(() => OnSampled(s));
            service.CleanStarted += r => Post(() => OnCleanStarted(r));
            service.CleanCompleted += (report, r) => Post(() => OnCleanCompleted(report, r));
            service.Failed += (ex, source) => Post(() => OnServiceError(ex, source));
            service.HoldChanged += r => Post(RefreshAutoRow);
            service.Update(ServiceSettings());
            // A pause outlasts a restart, cut to an hour from now: settings.ini is user-writable.
            if (elevated && settings.AutoCleanEnabled && service.RestorePause(settings.AutoCleanPausedUntilUtc))
                pauseOrigin = PauseOrigin.Restored;
            RefreshAutoRow();
            // R7: the shortcut is registered only when elevated (RegisterHotKey on a message-only window of this thread).
            // Not elevated, the saved shortcut stays in Settings, marked not active.
            hotkeyListener = elevated ? CreateHotkeyListener() : null;
            hotkey = new HotkeyBinding(hotkeyListener);
            StartHotkey();
            string logPath = CleanLog.DefaultPath;
            activityPath.Text = logPath ?? "";
            activityPath.Visibility = logPath == null ? Visibility.Collapsed : Visibility.Visible;
            activityPathNote.Text = AppText.CleanLogNote(logPath != null);
            ShowHistory();
            ShowActivity();
            // Before service.Start, so a Power Clean of this run is never mistaken for an earlier run's leftover.
            if (elevated) StartServiceRecovery();

            if (instance != null) instance.ActivationRequested += OnActivationRequested;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            app.SessionEnding += delegate { Exit(); };

            service.Start(SampleInterval);
            if (!options.StartInTray) ShowWindow();
            RefreshStartupState();
            if (settings.PowerClean) RefreshServices();
        }

        /// <summary>Releases everything that outlives the process if left behind: the tray icon above all.</summary>
        public void Exit()
        {
            // First of all: the activation handler goes only at teardown step 8, and a launch answered before then
            // would exit while this copy closes. From here on a launch gets no answer and waits for the lock instead.
            if (instance != null) instance.BeginClosing();
            if (exiting) return;
            // Before anything is torn down: a threshold set in the last 400 ms is saved, not dropped.
            try { FlushThreshold(); }
            catch (Exception ex) { ErrorLog.TryWrite(ErrorLog.DefaultPath, "could not save the threshold on exit", ex); }
            // A prompt still up when the user exits: its result arrives later and is ignored.
            elevationState = ElevationFlow.Next(elevationState, ElevationEvent.ExitRequested).State;
            exiting = true;
            // The restore starts even when a teardown step throws (Exit may run from the UI fault handler, where a
            // second Exit returns at once), and the app still shuts down; exiting keeps both to one start. Each step
            // runs on its own, so one that throws does not skip the rest (auto-clean's timer above all); the first
            // fault still goes on to the caller. The single-instance lock is not a step: it goes after the restore.
            SingleInstance held = instance;
            Action release = () => { if (held != null) held.Dispose(); };
            try
            {
                PowerCleanRecovery.TeardownThenRestore(() => PowerCleanRecovery.RunSteps(
                    () => { SystemEvents.PowerModeChanged -= OnPowerModeChanged; },
                    () => { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; },
                    () => { SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged; },
                    () => StopHotkey(),
                    () => { if (service != null) service.Dispose(); },
                    () => CloseFlyout(),
                    () => { if (tray != null) tray.Dispose(); },
                    () => { if (instance != null) { instance.ActivationRequested -= OnActivationRequested; instance = null; } },
                    () => { if (window != null) window.Close(); }),
                    () => RestoreServicesOnExit(service, elevated, release));
            }
            finally { app.Shutdown(); }
        }

        /// <summary>
        /// Services Power Clean stopped are this session's doing, so leaving the app starts them again. A foreground
        /// thread keeps the process alive until Restore finishes (at most 10 s per service) after the window has gone.
        /// A Power Clean still in flight is waited for first (bounded), so a stop it makes after Exit is restored too;
        /// if the thread is cut short anyway, the journal lets the next elevated start finish the job. A memory-only
        /// clean, or any clean without administrator rights, stops no services and does not hold the exit up.
        /// release (the single-instance lock) runs once the restore is done, or at once when none is needed
        /// (PowerCleanRecovery.RestoreThenRelease): a copy started while services are still being restored hands
        /// over to this one and exits, rather than restoring the same services from the journal at the same time.
        /// </summary>
        static void RestoreServicesOnExit(AutoCleanService cleaner, bool elevated, Action release)
        {
            PowerCleanRecovery.RestoreThenRelease(
                () =>
                {
                    bool cleaning = cleaner != null && cleaner.IsCleaning;
                    bool stopsServices = elevated && cleaner != null && cleaner.CleanMayStopServices;
                    return PowerCleanRecovery.NeedsExitRestore(ServiceTrimmer.StoppedThisSession().Count, cleaning, stopsServices);
                },
                () =>
                {
                    if (cleaner != null) cleaner.WaitUntilIdle(PowerCleanRecovery.ExitCleanWait);
                    ServiceTrimmer.Restore();
                },
                release,
                body => new Thread(body) { IsBackground = false, Name = "restore-services" }.Start());
        }

        /// <summary>
        /// Hooks Power Clean up to its journal, then starts again the services an earlier run stopped and never
        /// restored (it crashed, was ended, or Windows signed out first). The names are read here, before auto-clean
        /// starts; the restore runs off the UI thread and reports in the last-clean line. The journal is this
        /// account's HKLM key (PowerCleanJournal); the old stopped-services.txt in the data folder is never read,
        /// because any program of the user could write it. A journal key that fails its checks is logged and
        /// restores nothing, and Power Clean then stops nothing (each stop is recorded first; Add fails there).
        /// </summary>
        void StartServiceRecovery()
        {
            PowerCleanJournal journal = PowerCleanJournal.ForCurrentUser();
            if (journal == null) return;
            string problem;
            List<string> leftover = journal.Read(out problem);
            if (problem != null) ErrorLog.TryWrite(ErrorLog.DefaultPath, "Power Clean's journal was not used: " + problem, null);
            ServiceTrimmer.Journal = journal;
            if (leftover.Count == 0) return;
            List<string> adopted = ServiceTrimmer.Adopt(leftover);
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ServiceAction> done = null;
                string failure = null;
                try { done = ServiceTrimmer.Restore(adopted); }
                catch (Exception ex) { failure = AppText.Plain(ex.Message, "unknown error"); }
                Post(() => ShowRecovery(done, failure));
            });
        }

        /// <summary>The start-up restore's outcome in the result line, with a row per service behind the disclosure.</summary>
        void ShowRecovery(List<ServiceAction> done, string failure)
        {
            UpdateRestoreUi();
            string line = AppText.RecoveryLine(done, failure);
            if (line == null) return;
            resultAt = null; // not a clean: nothing for RefreshResultLine to redate
            bool good = AppText.RecoveryWentWell(done, failure);
            resultText.Text = line;
            resultGlyph.Data = Icon(good ? "IconCheck" : "IconWarn");
            resultGlyph.SetResourceReference(Shape.StrokeProperty, good ? "Ok" : "Caution");
            resultRows.Children.Clear();
            List<AreaLine> rows = AppText.RecoveryLines(done);
            for (int i = 0; i < rows.Count; i++) resultRows.Children.Add(BuildResultRow(rows[i], i == 0));
            resultCount = rows.Count;
            resultDetails.IsExpanded = false;
            resultDetails.Header = AppText.DetailsLabel(false, resultCount);
            resultDetails.Visibility = resultCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(resultPanel, line);
            if (!resultShown) EnterResult();
            if (!good) dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => resultPanel.Announce()));
        }

        /// <summary>
        /// Last-resort cleanup from the fatal-error path; never throws. The process ends right after, so Exit's
        /// restore thread cannot be used: services this run stopped are started here, synchronously and bounded
        /// (PowerCleanRecovery.EmergencyLockWait, EmergencyBudget). If the service lock does not come, it gives up
        /// and the journal lets the next elevated start finish. The restore runs after auto-clean is disposed (no
        /// new clean) and before the single-instance lock goes, so a new copy does not restore the same services.
        /// </summary>
        public void EmergencyCleanup()
        {
            // First: a dying copy answers no launch (one may ask while the bounded restore below runs).
            try { if (instance != null) instance.BeginClosing(); } catch (Exception) { }
            // SystemEvents is static: a handler left attached would outlive the shell.
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch (Exception) { }
            try { StopHotkey(); } catch (Exception) { }
            try { if (service != null) service.Dispose(); } catch (Exception) { }
            try { CloseFlyout(); } catch (Exception) { }
            try { if (tray != null) tray.Dispose(); } catch (Exception) { }
            try { ServiceTrimmer.TryRestoreBounded(PowerCleanRecovery.EmergencyLockWait, PowerCleanRecovery.EmergencyBudget); }
            catch (Exception) { }
            try { if (instance != null) instance.Dispose(); } catch (Exception) { }
        }

        void Post(Action action)
        {
            if (exiting) return;
            dispatcher.BeginInvoke(new Action(() => { if (!exiting) action(); }));
        }

        static ImageSource LoadWindowIcon()
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RamOptimiser.AppIcon.ico");
            if (s == null) return null;
            var copy = new MemoryStream();
            using (s) s.CopyTo(copy);
            copy.Position = 0;
            // The icon decoder keeps every size, so Windows can pick the small and large icons it needs.
            return BitmapFrame.Create(copy, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        T F<T>(string name) where T : class
        {
            T found = window.FindName(name) as T;
            if (found == null) throw new InvalidOperationException("MainWindow.xaml has no " + typeof(T).Name + " named " + name + ".");
            return found;
        }

        void FindElements()
        {
            tabs = F<TabControl>("Tabs");
            memoryTab = F<TabItem>("MemoryTab");
            startupTab = F<TabItem>("StartupTab");
            settingsTab = F<TabItem>("SettingsTab");

            mainView = F<ScrollViewer>("MainView");
            adminLine = F<StackPanel>("AdminLine");
            elevationText = F<TextBlock>("ElevationText");
            sampleError = F<LiveRegion>("SampleError");
            sampleErrorText = F<TextBlock>("SampleErrorText");
            restartError = F<TextBlock>("RestartError");
            restartErrorRegion = F<LiveRegion>("RestartErrorRegion");
            accountNotice = F<TextBlock>("AccountNotice");
            lastReadNote = F<TextBlock>("LastReadNote");
            loadCard = F<SummaryPanel>("LoadCard");
            loadNumber = F<Run>("LoadNumber");
            inUseRun = F<Run>("InUseRun");
            mapBar = F<MapPanel>("MapBar");
            // In MemoryMap.Parts order: in use, modified, standby, free.
            segments = new[] { F<Border>("SegInUse"), F<Border>("SegModified"), F<Border>("SegStandby"), F<Border>("SegFree") };
            listsNote = F<TextBlock>("ListsNote");
            freeLabel = F<TextBlock>("FreeLabel");
            rowInUse = F<SummaryPanel>("RowInUse");
            rowModified = F<SummaryPanel>("RowModified");
            rowStandby = F<SummaryPanel>("RowStandby");
            rowFree = F<SummaryPanel>("RowFree");
            valInUse = F<TextBlock>("ValInUse");
            valModified = F<TextBlock>("ValModified");
            valStandby = F<TextBlock>("ValStandby");
            valFree = F<TextBlock>("ValFree");
            commitRow = F<SummaryPanel>("CommitRow");
            commitValue = F<TextBlock>("CommitValue");
            commitUsed = F<Border>("CommitUsed");
            commitRest = F<Border>("CommitRest");
            commitNote = F<TextBlock>("CommitNote");
            commitWarning = F<LiveRegion>("CommitWarning");
            commitWarningText = F<TextBlock>("CommitWarningText");
            adviceLine = F<Grid>("AdviceLine");
            adviceGlyph = F<Path>("AdviceGlyph");
            adviceText = F<TextBlock>("AdviceText");
            cleanButton = F<Button>("CleanButton");
            powerNote = F<TextBlock>("PowerNote");
            restartButton = F<Button>("RestartButton");
            resultPanel = F<LiveRegion>("ResultPanel");
            resultGlyph = F<Path>("ResultGlyph");
            resultText = F<TextBlock>("ResultText");
            resultDetails = F<Expander>("ResultDetails");
            resultRows = F<StackPanel>("ResultRows");
            topList = F<StackPanel>("TopList");
            autoDescription = F<TextBlock>("AutoDescription");
            autoStatusRegion = F<LiveRegion>("AutoStatusRegion");
            autoStatusGlyph = F<Path>("AutoStatusGlyph");
            autoStatusText = F<TextBlock>("AutoStatus");
            autoStatusDetail = F<TextBlock>("AutoStatusDetail");
            autoResumeButton = F<Button>("AutoResumeButton");
            autoSwitch = F<CheckBox>("AutoSwitch");
            autoBar = F<LiveRegion>("AutoBar");
            autoBarGlyph = F<Path>("AutoBarGlyph");
            autoBarTitle = F<TextBlock>("AutoBarTitle");
            autoBarDetail = F<TextBlock>("AutoBarDetail");
            autoBarResume = F<Button>("AutoBarResume");
            autoBarDismiss = F<Button>("AutoBarDismiss");

            startupView = F<ScrollViewer>("StartupView");
            startupSummary = F<TextBlock>("StartupSummary");
            startupSummaryRegion = F<LiveRegion>("StartupSummaryRegion");
            startupNote = F<TextBlock>("StartupNote");
            openStartupSettings = F<Button>("OpenStartupSettings");
            startupError = F<TextBlock>("StartupError");
            startupErrorRegion = F<LiveRegion>("StartupErrorRegion");
            startupEmptyRegion = F<LiveRegion>("StartupEmptyRegion");
            searchCountRegion = F<LiveRegion>("SearchCountRegion");
            searchCount = F<TextBlock>("SearchCount");
            startupList = F<StackPanel>("StartupList");
            startupCard = F<Border>("StartupCard");
            startupLoadingPanel = F<StackPanel>("StartupLoading");
            startupEmptyTitle = F<TextBlock>("StartupEmptyTitle");
            startupEmptyText = F<TextBlock>("StartupEmptyText");
            showApps = F<RadioButton>("ShowAppsButton");
            showServices = F<RadioButton>("ShowServicesButton");
            appsCount = F<Run>("AppsCount");
            servicesCount = F<Run>("ServicesCount");
            startupSearch = F<TextBox>("StartupSearch");
            clearSearchButton = F<Button>("ClearSearchButton");
            verdictBox = F<ComboBox>("VerdictBox");
            sortBox = F<ComboBox>("SortBox");

            saveError = F<TextBlock>("SaveError");
            saveErrorRegion = F<LiveRegion>("SaveErrorRegion");
            settingsAutoSwitch = F<CheckBox>("SettingsAutoSwitch");
            cooldownText = F<TextBlock>("CooldownText");
            pauseRow = F<Border>("PauseRow");
            pauseNote = F<TextBlock>("PauseNote");
            pauseButton = F<Button>("PauseButton");
            thresholdSlider = F<Slider>("ThresholdSlider");
            thresholdValue = F<TextBlock>("ThresholdValue");
            intervalBox = F<ComboBox>("IntervalBox");
            areaReason = F<TextBlock>("AreaReason");
            areaReasonRegion = F<LiveRegion>("AreaReasonRegion");
            moreAreas = F<Expander>("MoreAreas");
            settingsPowerSwitch = F<CheckBox>("SettingsPowerSwitch");
            powerList = F<StackPanel>("PowerList");
            powerServicesList = F<Expander>("PowerServices");
            restoreButton = F<Button>("RestoreButton");
            powerStatus = F<TextBlock>("PowerStatus");
            powerStatusRegion = F<LiveRegion>("PowerStatusRegion");
            themeBox = F<ComboBox>("ThemeBox");
            themeNote = F<TextBlock>("ThemeNote");
            startupCheck = F<CheckBox>("StartupCheck");
            startupReason = F<TextBlock>("StartupReason");
            closeToTrayCheck = F<CheckBox>("CloseToTrayCheck");
            trayPercentCheck = F<CheckBox>("TrayPercentCheck");
            notifyCheck = F<CheckBox>("NotifyCheck");
            hotkeyBox = F<TextBox>("HotkeyBox");
            hotkeyRemove = F<Button>("HotkeyRemove");
            hotkeyNote = F<TextBlock>("HotkeyNote");
            hotkeyMessage = F<TextBlock>("HotkeyMessage");
            hotkeyMessageRegion = F<LiveRegion>("HotkeyMessageRegion");
            resetButton = F<Button>("ResetButton");
            resetConfirm = F<StackPanel>("ResetConfirm");
            resetYes = F<Button>("ResetYes");
            resetNo = F<Button>("ResetNo");
            resetStatus = F<TextBlock>("ResetStatus");
            resetStatusRegion = F<LiveRegion>("ResetStatusRegion");
            recentCleanRows = F<StackPanel>("RecentCleanRows");
            activityRows = F<StackPanel>("ActivityRows");
            activityPath = F<TextBox>("ActivityPath");
            activityPathNote = F<TextBlock>("ActivityPathNote");

            areaBoxes.Add(new KeyValuePair<CheckBox, Areas>(F<CheckBox>("AreaWorkingSets"), Areas.WorkingSets));
            areaBoxes.Add(new KeyValuePair<CheckBox, Areas>(F<CheckBox>("AreaFileCache"), Areas.SystemFileCache));
            areaBoxes.Add(new KeyValuePair<CheckBox, Areas>(F<CheckBox>("AreaStandbyLow"), Areas.LowPriorityStandby));
            areaBoxes.Add(new KeyValuePair<CheckBox, Areas>(F<CheckBox>("AreaStandby"), Areas.StandbyList));
            areaBoxes.Add(new KeyValuePair<CheckBox, Areas>(F<CheckBox>("AreaModified"), Areas.ModifiedList));
            areaBoxes.Add(new KeyValuePair<CheckBox, Areas>(F<CheckBox>("AreaVolumes"), Areas.VolumeCache));
            areaBoxes.Add(new KeyValuePair<CheckBox, Areas>(F<CheckBox>("AreaCombine"), Areas.CombinePages));
            areaBoxes.Add(new KeyValuePair<CheckBox, Areas>(F<CheckBox>("AreaRegistry"), Areas.RegistryCache));
        }

        // ------------------------------------------------------------------ wiring

        void WireMain()
        {
            window.SourceInitialized += delegate
            {
                AppNative.RemoveMaximizeBox(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                theme.ApplyWindowFrame(window);
                ApplyBackdrop();
            };
            window.Closing += OnClosing;
            window.Activated += delegate { CheckSettingsFile(); };
            window.PreviewKeyDown += OnKeyDown;
            window.IsVisibleChanged += delegate { OnShowingChanged(); };
            window.StateChanged += delegate { OnShowingChanged(); };
            tabs.SelectionChanged += (s, e) => { if (ReferenceEquals(e.OriginalSource, tabs)) OnTabChanged(); };
            cleanButton.Click += delegate { RequestClean(); };
            restartButton.Click += delegate { RestartElevated(); };
            resultDetails.Expanded += delegate { resultDetails.Header = AppText.DetailsLabel(true, resultCount); };
            resultDetails.Collapsed += delegate { resultDetails.Header = AppText.DetailsLabel(false, resultCount); };
            autoResumeButton.Click += delegate { ResumeAutoClean(); };
            autoBarResume.Click += delegate { ResumeAutoClean(); };
            autoBarDismiss.Click += delegate { DismissAutoBar(); };
            OnToggle(autoSwitch, SetAutoClean);
            Motion.AddPress(cleanButton);
        }

        /// <summary>
        /// Checked/Unchecked, not Click: UI Automation's Toggle (screen readers, voice control) changes
        /// IsChecked without raising Click, which left the box showing a state that was never applied.
        /// Changes made by SyncSettingsUi/UpdateStartupUi run with syncing set and are ignored.
        /// </summary>
        void OnToggle(ToggleButton box, Action<bool> apply)
        {
            RoutedEventHandler handler = delegate { if (!syncing) apply(box.IsChecked == true); };
            box.Checked += handler;
            box.Unchecked += handler;
        }

        void WireStartup()
        {
            showApps.Checked += delegate { if (!syncing) BuildStartupList(); };
            showServices.Checked += delegate { if (!syncing) BuildStartupList(); };
            // U9: the count (or "Nothing matches") is read out once typing or filtering rests, never per keystroke.
            searchAnnounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = SearchAnnounceDelay };
            searchAnnounceTimer.Tick += delegate { searchAnnounceTimer.Stop(); AnnounceSearch(); };
            startupSearch.TextChanged += delegate
            {
                clearSearchButton.Visibility = string.IsNullOrEmpty(startupSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
                BuildStartupList();
                RestartSearchAnnounce();
            };
            clearSearchButton.Click += delegate
            {
                startupSearch.Text = "";
                startupSearch.Focus();
            };
            openStartupSettings.Click += delegate { OpenStartupSettings(); };
            // U16: Esc clears a search that has text and keeps focus in the box; on an empty box the key is left alone.
            startupSearch.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && StartupFilter.EscapeClears(startupSearch.Text))
                {
                    startupSearch.Text = "";
                    e.Handled = true;
                }
            };
            foreach (VerdictFilter f in StartupFilter.Filters)
                verdictBox.Items.Add(new ComboBoxItem { Content = StartupFilter.Label(f, 0), Tag = f });
            verdictBox.SelectedIndex = 0;
            sortBox.Items.Add(new ComboBoxItem { Content = "Verdict", Tag = StartupSort.Verdict });
            sortBox.Items.Add(new ComboBoxItem { Content = "Name", Tag = StartupSort.Name });
            sortBox.SelectedIndex = 0;
            verdictBox.SelectionChanged += delegate { if (!syncing) { BuildStartupList(); RestartSearchAnnounce(); } };
            sortBox.SelectionChanged += delegate { if (!syncing) BuildStartupList(); };
        }

        void WireSettings()
        {
            OnToggle(settingsAutoSwitch, SetAutoClean);
            pauseButton.Click += delegate
            {
                if (autoInfo != null && autoInfo.Paused) ResumeAutoClean();
                else PauseAutoClean(PauseOrigin.Settings);
            };
            OnToggle(settingsPowerSwitch, SetPowerClean);
            restoreButton.Click += delegate { RestoreServices(); };
            thresholdTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = ThresholdSaveDelay };
            thresholdTimer.Tick += delegate { FlushThreshold(); };
            thresholdSlider.ValueChanged += delegate
            {
                ShowThreshold();
                if (syncing) return;
                // Dragging changes the value many times a second: save once it rests.
                thresholdPending.Changed();
                thresholdTimer.Stop();
                thresholdTimer.Start();
            };
            intervalBox.SelectionChanged += delegate
            {
                if (syncing) return;
                var item = intervalBox.SelectedItem as ComboBoxItem;
                if (item == null) return;
                settings.IntervalMinutes = (int)item.Tag;
                SaveAndApply();
            };
            themeBox.SelectionChanged += delegate
            {
                if (syncing) return;
                var item = themeBox.SelectedItem as ComboBoxItem;
                if (item == null) return;
                settings.Theme = (ThemeMode)item.Tag;
                SaveAndApply();
                ReapplyTheme();
            };
            moreAreas.Expanded += delegate { moreAreas.Header = "Show fewer areas"; };
            moreAreas.Collapsed += delegate { moreAreas.Header = "Show 4 more areas"; };
            foreach (KeyValuePair<CheckBox, Areas> pair in areaBoxes)
            {
                CheckBox box = pair.Key;
                Areas area = pair.Value;
                OnToggle(box, on =>
                {
                    // U21: the last area stays focusable, so unticking it is refused here, with the reason read out.
                    AreaToggle t = AreaRules.Decide(settings.Areas, area, on);
                    if (t.Refused) { RefuseLastArea(box, t.Reason); return; }
                    if (!t.Save) return;
                    settings.Areas = t.Selected;
                    SaveAndApply();
                });
            }
            OnToggle(startupCheck, ToggleStartup);
            OnToggle(closeToTrayCheck, on => { settings.CloseToTray = on; SaveAndApply(); });
            OnToggle(trayPercentCheck, on => { settings.ShowTrayPercent = on; SaveAndApply(); });
            OnToggle(notifyCheck, on => { settings.NotifyProblems = on; SaveAndApply(); });
            hotkeyBox.PreviewKeyDown += OnHotkeyBoxKey;
            // Windows takes a registered combination before the box sees it, so the shortcut is let go while the box
            // has focus: pressing it there records it rather than cleaning.
            hotkeyBox.GotKeyboardFocus += delegate { hotkey.Suspend(); };
            hotkeyBox.LostKeyboardFocus += delegate { ResumeHotkey(); };
            hotkeyRemove.Click += delegate
            {
                ApplyHotkeyResult(hotkey.Remove());
                hotkeyBox.Focus(); // the link collapses: focus stays in the row
            };
            resetButton.Click += delegate { ShowResetConfirm(true); };
            resetNo.Click += delegate { ShowResetConfirm(false); resetButton.Focus(); };
            resetYes.Click += delegate { ResetSettings(); };
            resetConfirm.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                ShowResetConfirm(false);
                resetButton.Focus();
                e.Handled = true;
            };
            // Focus leaving the confirmation (Tab past Cancel, a click elsewhere) cancels it.
            resetConfirm.IsKeyboardFocusWithinChanged += delegate
            {
                if (!resetConfirm.IsKeyboardFocusWithin && resetConfirm.Visibility == Visibility.Visible)
                    dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        if (!resetConfirm.IsKeyboardFocusWithin) ShowResetConfirm(false);
                    }));
            };
        }

        void BuildChoices()
        {
            foreach (int minutes in AppText.IntervalChoices(settings.IntervalMinutes))
                intervalBox.Items.Add(new ComboBoxItem { Content = AppText.IntervalLabel(minutes), Tag = minutes });
            foreach (ThemeMode mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
                themeBox.Items.Add(new ComboBoxItem { Content = ThemeResolver.ThemeLabel(mode), Tag = mode });
            thresholdSlider.Minimum = AppSettings.MinThresholdPercent;
            thresholdSlider.Maximum = AppSettings.MaxThresholdPercent;
        }

        // ------------------------------------------------------------------ window and tabs

        public void ShowWindow()
        {
            if (exiting) return;
            if (!window.IsLoaded)
            {
                // The window is a fixed 720 px tall unless the screen is shorter.
                Rect area = SystemParameters.WorkArea;
                if (window.Height > area.Height) window.Height = area.Height;
            }
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        void OnActivationRequested(object sender, EventArgs e)
        {
            Post(ShowWindow);
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (exiting) return;
            e.Cancel = true;
            if (settings.CloseToTray) window.Hide();
            else Exit();
        }

        /// <summary>Ctrl+1, 2, 3 pick a tab (Ctrl+Tab also works, from TabControl); Ctrl+F searches the Startup list.</summary>
        void OnKeyDown(object sender, KeyEventArgs e)
        {
            // The shortcut box records every combination, Ctrl+1 to Ctrl+3 and Ctrl+F included (R7); this preview
            // handler runs first, so it stands aside while the box has focus.
            if (ReferenceEquals(Keyboard.FocusedElement, hotkeyBox)) return;
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            TabItem target = e.Key == Key.D1 || e.Key == Key.NumPad1 ? memoryTab
                : e.Key == Key.D2 || e.Key == Key.NumPad2 ? startupTab
                : e.Key == Key.D3 || e.Key == Key.NumPad3 ? settingsTab : null;
            if (target != null)
            {
                target.IsSelected = true;
                target.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                startupTab.IsSelected = true;
                dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => startupSearch.Focus()));
                e.Handled = true;
            }
        }

        void OnTabChanged()
        {
            if (startupTab.IsSelected)
            {
                if (startupApps == null) LoadStartup();
            }
            else if (settingsTab.IsSelected)
            {
                SyncSettingsUi(true);
                RefreshStartupState();
                RefreshServices();
            }
            else if (memoryTab.IsSelected)
            {
                if (viewStale && lastSnapshot != null) RenderSample(lastSnapshot, false);
                RefreshTopApps();
            }
        }

        // ------------------------------------------------------------------ Startup tab

        void LoadStartup()
        {
            if (!startupFlow.TryStartLoad()) return;
            QueryStartup();
        }

        /// <summary>
        /// Reads apps (registry, fast) and services (WMI, about a second) off the UI thread: the first load, or the
        /// reload that ends a change (the flow stays Applying until then, so no switch works in between).
        /// </summary>
        void QueryStartup()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<StartupApp> apps = null; List<StartupService> services = null; string failure = null;
                string journalProblem = null;
                // E24: packaged and run-once rows too, shown read-only.
                try { apps = StartupApps.Query(true); }
                catch (Exception ex) { failure = AppText.Plain(ex.Message, "unknown error"); }
                try { services = StartupServices.Query(out journalProblem); }
                catch (Exception ex) { failure = failure ?? AppText.Plain(ex.Message, "unknown error"); }
                // A refused ServiceStartup key hides every "turned off by this app" row and its Re-enable, so say why
                // in error.log: once per distinct reason, not at every reload of the tab.
                string loggedBefore = Interlocked.Exchange(ref startupJournalProblem, journalProblem);
                if (journalProblem != null && journalProblem != loggedBefore)
                    ErrorLog.TryWrite(ErrorLog.DefaultPath, "the Startup tab's service journal was not used: " + journalProblem, null);
                Post(() =>
                {
                    startupFlow.Loaded();
                    startupApps = apps ?? new List<StartupApp>();
                    startupServices = services ?? new List<StartupService>();
                    if (failure != null) ShowStatus(startupErrorRegion, startupError, "Some entries could not be read: " + failure + ".");
                    else HideStatus(startupErrorRegion);
                    BuildStartupList();
                    // U9: the summary is read out once, when the first load finishes.
                    if (!startupSummaryAnnounced)
                    {
                        startupSummaryAnnounced = true;
                        Announce(startupSummaryRegion, startupSummary.Text);
                    }
                });
                // E12: after the rows were read and handed over, so this one load still shows "turned back on" or
                // "turned off elsewhere"; then the records the app can no longer act on are removed. Only after a
                // read that worked (a failed one showed nothing), elevated only, the count not shown. Its own work
                // item: the tidy waits on ServiceTrimmer.Operation, which a Power Clean holds while it stops services.
                if (elevated && services != null)
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { StartupServices.TidyJournal(); }
                        catch (Exception ex) { ErrorLog.TryWrite(ErrorLog.DefaultPath, "could not tidy the service startup journal", ex); }
                    });
                }
            });
        }

        /// <summary>One row of the Startup list, apps and services alike.</summary>
        sealed class StartupItem
        {
            public string Key, Label, Name, Detail, LockText;
            /// <summary>"Look closer" when the program's signature does not back it (E26); else null.</summary>
            public string Marker;
            /// <summary>The verdict line's words when they are not VerdictText's (run-once, policy-set); else null.</summary>
            public string VerdictWords;
            public Geometry LockGlyph;
            public Verdict Verdict;
            public bool IsService, On, ByApp, RunsOnce;
            public Action<bool> Apply;
        }

        List<StartupItem> CurrentItems(bool apps)
        {
            var items = new List<StartupItem>();
            if (apps)
            {
                foreach (StartupApp a in startupApps)
                {
                    StartupApp target = a;
                    string key = AppText.StartupAppKey(a);
                    var it = new StartupItem
                    {
                        Key = key, Name = a.Name, IsService = false, On = startupFlow.ShowsOn(key, a.Enabled),
                        Verdict = AppText.AppVerdict(a), RunsOnce = StartupApps.IsRunOnce(a.Source),
                        VerdictWords = AppText.AppVerdictWords(a),
                        Label = AppText.StartupLabel(a), Detail = AppText.AppDetail(a),
                        Marker = AppText.LookCloser(a),
                        LockText = AppText.AppLock(a, elevated)
                    };
                    string glyph = AppText.AppLockGlyph(a, elevated);
                    if (glyph != null) it.LockGlyph = Icon(glyph);
                    // E24: a packaged or run-once row is read-only. AppLock always locks it, and it gets no switch
                    // action either, so nothing here can reach SetEnabled's refusal.
                    it.Apply = a.CanToggle ? (Action<bool>)(on => SetAppEnabled(target, it, on)) : null;
                    items.Add(it);
                }
            }
            else
            {
                foreach (StartupService s in startupServices)
                {
                    StartupService target = s;
                    var it = new StartupItem
                    {
                        Key = "svc|" + s.Name, Name = s.Name, IsService = true, ByApp = s.DisabledByApp,
                        On = startupFlow.ShowsOn("svc|" + s.Name, s.CanDisable && !s.DisabledByApp),
                        Verdict = s.Known == null ? Verdict.Unknown : s.Known.Verdict,
                        Label = s.Known != null ? s.Known.Label : s.DisplayName, Detail = AppText.ServiceDetail(s)
                    };
                    if (!StartupKnowledge.CanDisableService(s.Name)) it.LockText = "Left alone";
                    else if (s.Protected) { it.LockText = "Protected by Windows"; it.LockGlyph = Icon("IconLock"); }
                    else if (!s.CanDisable && !s.DisabledByApp) it.LockText = "Not at startup";
                    else if (!elevated) { it.LockText = "Needs admin"; it.LockGlyph = Icon("IconLock"); }
                    it.Apply = on => SetServiceStartup(target, it, on);
                    items.Add(it);
                }
            }
            return items;
        }

        void BuildStartupList()
        {
            if (startupApps == null || startupServices == null)
            {
                startupLoadingPanel.Visibility = Visibility.Visible;
                startupCard.Visibility = Visibility.Collapsed;
                startupEmptyRegion.Visibility = Visibility.Collapsed;
                return;
            }
            startupLoadingPanel.Visibility = Visibility.Collapsed;

            int appsOn, servicesOn = 0, safeOn;
            // E24: packaged rows count as apps on, run-once rows never do.
            AppText.CountStartupApps(startupApps, out appsOn, out safeOn);
            foreach (StartupService s in startupServices)
                if (s.CanDisable && s.Known != null)
                {
                    servicesOn++;
                    if (s.Known.Verdict == Verdict.Safe) safeOn++;
                }
            // While a change applies its line stays, whatever rebuilds the list (search, filter, sort).
            startupSummary.Text = startupFlow.BusyLine ?? AppText.StartupSummary(appsOn, servicesOn, safeOn);
            AutomationProperties.SetName(startupSummaryRegion, startupSummary.Text);
            appsCount.Text = "  " + startupApps.Count.ToString(CultureInfo.InvariantCulture);
            servicesCount.Text = "  " + startupServices.Count.ToString(CultureInfo.InvariantCulture);
            AutomationProperties.SetName(showApps, "Startup apps, " + startupApps.Count.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(showServices, "Services, " + startupServices.Count.ToString(CultureInfo.InvariantCulture));

            bool apps = showApps.IsChecked == true;
            startupNote.Text = apps ? AppText.StartupAppsNote : AppText.StartupServicesNote;
            if (!elevated) startupNote.Text += " " + (apps ? "Apps for every user need administrator rights." : AppText.ServicesNeedAdmin);
            openStartupSettings.Visibility = AppText.ShowsStartupSettings(apps, startupApps) ? Visibility.Visible : Visibility.Collapsed;

            List<StartupItem> all = CurrentItems(apps);
            var filter = (VerdictFilter)((ComboBoxItem)verdictBox.SelectedItem).Tag;
            var sort = (StartupSort)((ComboBoxItem)sortBox.SelectedItem).Tag;
            UpdateVerdictCounts(all);

            var shown = new List<StartupItem>();
            foreach (StartupItem it in all)
                if (StartupFilter.Matches(filter, it.Verdict) && StartupFilter.Matches(startupSearch.Text, it.Label, it.Name)) shown.Add(it);
            shown.Sort((a, b) => StartupFilter.Compare(sort, a.Verdict, a.Label, b.Verdict, b.Label));

            // Keep keyboard focus on the same control across the rebuild (a background reload must not drop it).
            var focused = Keyboard.FocusedElement as FrameworkElement;
            bool focusInList = focused != null && startupList.IsKeyboardFocusWithin;
            string focusTag = refocusTag ?? (focusInList ? focused.Tag as string : null);
            refocusTag = null;

            startupList.Children.Clear();
            syncing = true;
            try
            {
                bool first = true;
                foreach (StartupItem it in shown)
                {
                    startupList.Children.Add(StartupRow(it, first));
                    first = false;
                }
            }
            finally { syncing = false; }

            bool empty = shown.Count == 0;
            startupCard.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            // The region itself collapses, so no empty group stays in the UIA tree while the list has rows.
            startupEmptyRegion.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty)
            {
                if (all.Count == 0)
                {
                    startupEmptyTitle.Text = apps ? "No apps start with Windows" : "No listed services found";
                    startupEmptyText.Text = apps ? "Apps that open at sign-in will show here." : "Services in the app's list will show here.";
                }
                else
                {
                    startupEmptyTitle.Text = AppText.SearchCount(0);
                    startupEmptyText.Text = "Try another name, or show all verdicts.";
                }
                AutomationProperties.SetName(startupEmptyRegion, startupEmptyTitle.Text + ". " + startupEmptyText.Text);
            }
            // U9: "12 shown" while the list is narrowed; read out only by the search debounce.
            searchShown = shown.Count;
            bool counting = AppText.SearchCountVisible(startupSearch.Text, verdictBox.SelectedIndex);
            searchCount.Text = AppText.SearchCount(shown.Count);
            AutomationProperties.SetName(searchCountRegion, searchCount.Text);
            searchCountRegion.Visibility = counting && !empty ? Visibility.Visible : Visibility.Collapsed;

            if (focusTag != null)
            {
                FrameworkElement target = FindByTag(startupList, focusTag);
                dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (target != null && target.IsVisible && target.IsEnabled) target.Focus();
                    else if (focusInList) startupSearch.Focus();
                }));
            }
        }

        void UpdateVerdictCounts(List<StartupItem> all)
        {
            var counts = new Dictionary<VerdictFilter, int>();
            foreach (VerdictFilter f in StartupFilter.Filters) counts[f] = 0;
            foreach (StartupItem it in all)
                foreach (VerdictFilter f in StartupFilter.Filters)
                    if (StartupFilter.Matches(f, it.Verdict)) counts[f]++;
            int selected = verdictBox.SelectedIndex;
            bool changed = false;
            foreach (ComboBoxItem item in verdictBox.Items)
            {
                string label = StartupFilter.Label((VerdictFilter)item.Tag, counts[(VerdictFilter)item.Tag]);
                if (!Equals(item.Content, label)) { item.Content = label; changed = true; }
            }
            if (!changed || verdictBox.IsDropDownOpen) return;
            // The closed box shows a copy of the selected item's text: reselect so it shows the new count.
            syncing = true;
            try { verdictBox.SelectedIndex = -1; verdictBox.SelectedIndex = selected; }
            finally { syncing = false; }
        }

        static FrameworkElement FindByTag(DependencyObject root, string tag)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var fe = child as FrameworkElement;
                if (fe != null && tag.Equals(fe.Tag as string)) return fe;
                FrameworkElement found = FindByTag(child, tag);
                if (found != null) return found;
            }
            foreach (object o in LogicalTreeHelper.GetChildren(root))
            {
                var fe = o as FrameworkElement;
                if (fe == null) continue;
                if (tag.Equals(fe.Tag as string)) return fe;
                FrameworkElement found = FindByTag(fe, tag);
                if (found != null) return found;
            }
            return null;
        }

        Geometry Icon(string key)
        {
            return window.TryFindResource(key) as Geometry;
        }

        Path IconPath(string key, string brush)
        {
            var p = new Path { Data = Icon(key) };
            p.SetResourceReference(FrameworkElement.StyleProperty, "IconPath");
            p.SetResourceReference(Shape.StrokeProperty, brush);
            return p;
        }

        static TextBlock SmallText(string text)
        {
            var t = new TextBlock { Text = text };
            t.SetResourceReference(FrameworkElement.StyleProperty, "Small");
            return t;
        }

        /// <summary>
        /// Chevron, name (with "Off by this app" when it is), verdict as glyph plus words, and on the right a
        /// switch meaning "starts with Windows" or the reason it can't be changed. Details open under the row;
        /// an error appears under the row that failed.
        /// </summary>
        UIElement StartupRow(StartupItem it, bool first)
        {
            string brush = it.Verdict == Verdict.Safe ? "Ok" : it.Verdict == Verdict.Caution ? "Caution" : "MutedText";
            string glyph = it.Verdict == Verdict.Safe ? "IconCheck" : it.Verdict == Verdict.Caution ? "IconWarn"
                : it.Verdict == Verdict.Keep ? "IconShield" : "IconUnknown";
            string verdict = it.VerdictWords
                ?? (it.Verdict == Verdict.Unknown && it.IsService ? "Not in the app's list" : AppText.VerdictText(it.Verdict, it.IsService));

            var row = new Border();
            if (!first)
            {
                row.BorderThickness = new Thickness(0, 1, 0, 0);
                row.SetResourceReference(Border.BorderBrushProperty, "Hairline");
            }
            var stack = new StackPanel();
            row.Child = stack;

            var grid = new Grid { Margin = new Thickness(12, 10, 12, 10), MinHeight = 36 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stack.Children.Add(grid);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) };
            var nameLine = new WrapPanel();
            nameLine.Children.Add(new TextBlock { Text = it.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            if (it.ByApp)
            {
                var tag = new Border { Child = new TextBlock { Text = "Off by this app", FontSize = 12 } };
                tag.SetResourceReference(FrameworkElement.StyleProperty, "Tag");
                nameLine.Children.Add(tag);
            }
            if (it.Marker != null)
            {
                // E26: a glyph plus words, never colour alone; the detail line under it says what was found.
                var markerLine = new StackPanel { Orientation = Orientation.Horizontal };
                Path markerGlyph = IconPath("IconWarn", "Caution");
                markerGlyph.Margin = new Thickness(0, 0, 4, 0);
                markerLine.Children.Add(markerGlyph);
                markerLine.Children.Add(new TextBlock { Text = it.Marker, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var marker = new Border { Child = markerLine };
                marker.SetResourceReference(FrameworkElement.StyleProperty, "Tag");
                nameLine.Children.Add(marker);
            }
            text.Children.Add(nameLine);
            var verdictLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            Path vGlyph = IconPath(glyph, brush);
            vGlyph.Margin = new Thickness(0, 0, 4, 0);
            verdictLine.Children.Add(vGlyph);
            var verdictText = new TextBlock { Text = verdict, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            verdictText.SetResourceReference(TextBlock.ForegroundProperty, brush);
            verdictLine.Children.Add(verdictText);
            text.Children.Add(verdictLine);
            // What it is for and what turning it off breaks, always in view: the decision needs it.
            TextBlock detail = SmallText(AppText.DetailLine(it.Detail));
            detail.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(detail);
            grid.Children.Add(text);

            string switchName = AppText.StartupSwitchName(it.Label, it.ByApp, it.Marker);
            if (it.LockText == null)
            {
                var box = new CheckBox { IsChecked = it.On, Tag = it.Key + "|sw", VerticalAlignment = VerticalAlignment.Center };
                box.SetResourceReference(FrameworkElement.StyleProperty, "SwitchStyle");
                AutomationProperties.SetName(box, switchName);
                AutomationProperties.SetHelpText(box, verdict + ". " + it.Detail);
                // While a change applies, the other switches wait (the summary says why); the one changing stays
                // focusable and keeps showing the state being applied.
                if (!startupFlow.SwitchesEnabled && it.Key != startupFlow.ApplyingKey)
                {
                    box.IsEnabled = false;
                    AutomationProperties.SetHelpText(box, startupFlow.BusyLine ?? "");
                }
                OnToggle(box, it.Apply);
                box.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(box, 1);
                grid.Children.Add(box);
            }
            else
            {
                var state = new StackPanel { Orientation = Orientation.Horizontal };
                if (it.LockGlyph != null)
                {
                    var lockPath = new Path { Data = it.LockGlyph, Margin = new Thickness(0, 0, 4, 0) };
                    lockPath.SetResourceReference(FrameworkElement.StyleProperty, "IconPath");
                    lockPath.SetResourceReference(Shape.StrokeProperty, "MutedText");
                    state.Children.Add(lockPath);
                }
                TextBlock lockText = SmallText(it.LockText);
                lockText.VerticalAlignment = VerticalAlignment.Center;
                state.Children.Add(lockText);
                // U10: one Text element (a plain StackPanel has no peer, so its name was never exposed) that always
                // says whether the item starts with Windows; the glyph and words inside are hidden from UIA.
                var lockedRow = new SummaryPanel { Child = state, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                AutomationProperties.SetName(lockedRow, AppText.StartupLockedName(it.Label, it.LockText, it.On, it.RunsOnce, it.Marker));
                Grid.SetColumn(lockedRow, 1);
                grid.Children.Add(lockedRow);
            }

            RowNote note;
            if (rowErrors.TryGetValue(it.Key, out note)) stack.Children.Add(ErrorBar(it.Key, note));
            return row;
        }

        /// <summary>
        /// Under the row that changed: an error (nothing changed) or a note (it changed only in part), each with its
        /// glyph, title, words and a dismiss button; announced politely, once.
        /// </summary>
        UIElement ErrorBar(string key, RowNote note)
        {
            var bar = new LiveRegion { Margin = new Thickness(12, 0, 12, 10) };
            bar.SetResourceReference(FrameworkElement.StyleProperty, note.BarStyle);
            AutomationProperties.SetLiveSetting(bar, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(bar, note.Spoken);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Path icon = IconPath(note.Glyph, note.Stroke);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 1, 8, 0);
            grid.Children.Add(icon);
            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = note.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            words.Children.Add(new TextBlock { Text = note.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(words, 1);
            grid.Children.Add(words);
            var dismiss = new Button { Content = IconPath("IconClose", "Text"), Tag = key + "|err", Margin = new Thickness(4, -4, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            dismiss.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButton");
            AutomationProperties.SetName(dismiss, note.DismissName);
            dismiss.ToolTip = "Dismiss";
            dismiss.Click += delegate
            {
                ClearRowError(key);
                refocusTag = key + (startupList.IsKeyboardFocusWithin ? "|sw" : "");
                BuildStartupList();
            };
            Grid.SetColumn(dismiss, 2);
            grid.Children.Add(dismiss);
            bar.Child = grid;
            // Read out when the error first shows, not again on each rebuild of the list.
            bar.Loaded += delegate { if (announcedErrors.ShouldAnnounce(key, note.Spoken)) bar.Announce(); };
            return bar;
        }

        void ClearRowError(string key)
        {
            rowErrors.Remove(key);
            announcedErrors.Forget(key);
        }

        void SetAppEnabled(StartupApp a, StartupItem it, bool on)
        {
            if (!startupFlow.SwitchesEnabled)
            {
                // A change is applying and its reload would overwrite this one: the switch goes back.
                refocusTag = it.Key + "|sw";
                BuildStartupList();
                return;
            }
            ClearRowError(it.Key);
            string failure = null;
            try
            {
                StartupApps.SetEnabled(a, on);
                a.Enabled = on;
            }
            catch (UnauthorizedAccessException) { failure = AppText.NeedsAdmin; }
            catch (System.Security.SecurityException) { failure = AppText.NeedsAdmin; }
            catch (IOException ex) { failure = AppText.Plain(ex.Message, "could not be changed"); }
            // A packaged or run-once row has no switch; the engine refuses one anyway.
            catch (InvalidOperationException ex) { failure = AppText.Plain(ex.Message, "could not be changed"); }
            if (failure != null) rowErrors[it.Key] = AppText.AppChangeError(it.Label, on, failure);
            refocusTag = it.Key + "|sw";
            BuildStartupList();
        }

        void SetServiceStartup(StartupService svc, StartupItem it, bool on)
        {
            refocusTag = it.Key + "|sw";
            if (!startupFlow.TryStartApply(it.Key, it.Label, on))
            {
                // Another change is still applying: the switch goes back to the state that holds.
                BuildStartupList();
                return;
            }
            ClearRowError(it.Key);
            BuildStartupList(); // the busy line, and the other switches wait
            ThreadPool.QueueUserWorkItem(delegate
            {
                StartupChange r;
                try { r = on ? StartupServices.Reenable(svc.Name) : StartupServices.Disable(svc.Name); }
                catch (Exception ex) { r = new StartupChange { Kind = StartupChangeKind.Failed, Detail = ex.Message }; }
                Post(() =>
                {
                    // E13: an error when nothing changed, a note when it changed only in part, nothing when it went as asked.
                    RowNote note = AppText.ServiceChangeNote(r, it.Label, on);
                    if (note != null) rowErrors[it.Key] = note;
                    QueryStartup(); // still Applying: Loaded() after the reload makes the switches work again
                });
            });
        }

        /// <summary>
        /// Opens Settings, Apps, Startup, where packaged apps are turned on or off (E24). The URI is a fixed constant
        /// with no user data in it, so nothing from a row reaches the shell.
        /// </summary>
        void OpenStartupSettings()
        {
            try
            {
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppText.StartupSettingsUri) { UseShellExecute = true })) { }
            }
            catch (Exception ex)
            {
                ShowStatus(startupErrorRegion, startupError, "Settings could not be opened: " + AppText.Plain(ex.Message, "unknown error") + ".");
            }
        }

        // ------------------------------------------------------------------ elevation

        void ApplyElevationState()
        {
            adminLine.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
            elevationText.Text = AppText.AdminLine;
            accountNotice.Text = otherAccount == null ? "" : AppText.OtherAccountNotice(otherAccount);
            accountNotice.Visibility = otherAccount == null ? Visibility.Collapsed : Visibility.Visible;
            UpdateCleanButton();
        }

        /// <summary>Enabled when elevated, idle and memory was read; the help text says why not, else the Power Clean note.</summary>
        void UpdateCleanButton()
        {
            cleanButton.IsEnabled = AppText.CleanEnabled(elevated, cleaningUi, memView.State);
            string note = AppText.PowerNote(settings.PowerClean, ChosenServiceCount());
            AutomationProperties.SetHelpText(cleanButton, AppText.CleanHelp(elevated, memView.State, note));
            // U21: a disabled button still shows why on hover (ToolTipService.ShowOnDisabled in MainWindow.xaml).
            // With a registered shortcut and nothing in the way, the tooltip names it: "Clean memory (Ctrl+Shift+M)".
            cleanButton.ToolTip = AppText.CleanButtonTip(AppText.CleanLabel(settings.PowerClean, cleaningUi), elevated, cleaningUi,
                memView.State, hotkey == null ? null : hotkey.Active);
            AutomationProperties.SetAcceleratorKey(cleanButton, hotkey == null || hotkey.Active == null ? "" : hotkey.Active.Format());
        }

        /// <summary>
        /// P10: the UAC prompt runs on its own thread (ElevationFlow), so the window keeps painting and a second
        /// click is ignored. This copy keeps the single-instance lock while the prompt is up, so a launch in the
        /// meantime hands over to this copy rather than taking the lock; after a successful launch this copy exits,
        /// and the elevated copy (told this pid) waits for that exit before taking the lock.
        /// </summary>
        void RestartElevated()
        {
            ElevationStep step = ElevationFlow.Next(elevationState, ElevationEvent.Requested);
            if (step.Action != ElevationAction.StartPrompt) return;
            // The elevated copy reads settings.ini at start: a threshold still waiting for its save goes in first.
            FlushThreshold();
            elevationState = step.State;
            HideStatus(restartErrorRegion);
            // Disabling the link drops keyboard focus; it goes back there if the prompt is cancelled or fails.
            restartHadFocus = restartButton.IsKeyboardFocused;
            ShowRestartLink();
            string args;
            int pid;
            using (System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess()) pid = self.Id;
            try
            {
                // The elevated copy compares this SID with its own to notice it runs as another account.
                string sid = StartupTask.CurrentUserSid();
                args = (Elevation.IsWellFormedSid(sid) ? Elevation.RelaunchArgs(sid) + " " : "") + Elevation.HandoffArgs(pid);
            }
            catch (Exception ex)
            {
                OnElevationResult(false, ex);
                return;
            }
            Elevation.RelaunchAsync(ExePath, args, new System.Windows.Interop.WindowInteropHelper(window).Handle,
                (ok, ex) => Post(() => OnElevationResult(ok, ex)));
        }

        /// <summary>The prompt's outcome, back on the UI thread. Dropped by Post once the app is exiting.</summary>
        void OnElevationResult(bool launched, Exception error)
        {
            ElevationEvent e = launched ? ElevationEvent.Launched : error == null ? ElevationEvent.Cancelled : ElevationEvent.Failed;
            ElevationStep step = ElevationFlow.Next(elevationState, e);
            if (step.Action == ElevationAction.Ignore) return;
            elevationState = step.State;
            switch (step.Action)
            {
                case ElevationAction.ExitApp:
                    Exit();
                    return;
                case ElevationAction.ShowError:
                    ShowRestartLink();
                    // Asked from the tray, the window may be hidden or on another tab: bring the error line into view
                    // first, so the assertive announcement (U9) comes from a line that can be seen.
                    memoryTab.IsSelected = true;
                    ShowWindow();
                    ShowStatus(restartErrorRegion, restartError, AppText.RestartFailed(error == null ? null : error.Message));
                    RestoreRestartFocus();
                    return;
                default:
                    // Cancelled: the link comes back, with no message, as before.
                    ShowRestartLink();
                    RestoreRestartFocus();
                    return;
            }
        }

        /// <summary>Puts keyboard focus back on the restart link when it had it before the prompt.</summary>
        void RestoreRestartFocus()
        {
            if (restartHadFocus && restartButton.IsEnabled && restartButton.IsVisible) restartButton.Focus();
            restartHadFocus = false;
        }

        /// <summary>
        /// The link reads "Waiting for approval..." and is disabled while the prompt is up. Its accessible name
        /// stays "Restart as administrator"; the help text says what it waits for.
        /// </summary>
        void ShowRestartLink()
        {
            bool prompting = elevationState == ElevationState.Prompting;
            restartButton.IsEnabled = !prompting;
            restartButton.Content = prompting ? AppText.WaitingForApproval : TrayMenuState.RestartText;
            AutomationProperties.SetName(restartButton, TrayMenuState.RestartText);
            AutomationProperties.SetHelpText(restartButton, prompting ? AppText.WaitingForApprovalHelp
                : lastSnapshot == null ? "" : AppText.ElevationNotice(lastSnapshot.HasLists));
        }

        // ------------------------------------------------------------------ sampling

        /// <summary>The window can be seen: shown and not minimized. Hidden in the tray, only the tray icon updates.</summary>
        bool WindowShowing { get { return window.IsVisible && window.WindowState != WindowState.Minimized; } }

        void OnSampled(MemorySnapshot s)
        {
            lastSnapshot = s;
            RecordLoad(s);
            MemoryViewState before = memView.State;
            memView.Sampled(DateTime.Now);
            if (readFailing)
            {
                readFailing = false;
                AddActivity(ActivityEvents.ReadRecovered());
            }
            if (before != MemoryViewState.Live)
            {
                lastReadNote.Visibility = Visibility.Collapsed;
                UpdateCleanButton();
            }
            RefreshResultLine();
            // Every sample, seen or not, so the two-sample rule counts real samples (R1; display only).
            advice.Next(AppText.AdviceFor(s));
            // Every sample: catches a pause ending and the back-off streak changing (cheap when nothing changed).
            RefreshAutoRow();
            if (!WindowShowing || !memoryTab.IsSelected)
            {
                // Nobody can see the view: skip its animations and redraw it from the newest sample when it shows.
                viewStale = true;
                UpdateTray();
                return;
            }
            RenderSample(s, mapReady);
            if (sampleCount++ % TopAppsEvery == 0) RefreshTopApps();
        }

        void OnShowingChanged()
        {
            if (exiting) return;
            if (WindowShowing)
            {
                wasShowing = true;
                if (viewStale && lastSnapshot != null && memoryTab.IsSelected) RenderSample(lastSnapshot, false);
                return;
            }
            if (!wasShowing) return;
            wasShowing = false;
            // Once per hide, after the view has gone: hand back the pages the window's visuals were using. They
            // come back (a few page faults) the next time it opens. Not repeated while hidden.
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ReleaseHiddenMemory));
        }

        void ReleaseHiddenMemory()
        {
            if (exiting || WindowShowing) return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            RamOptimiser.Gui.GuiNative.SetProcessWorkingSetSize(RamOptimiser.Gui.GuiNative.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }

        void RenderSample(MemorySnapshot s, bool animate)
        {
            viewStale = false;
            bool hadError = sampleError.Visibility == Visibility.Visible;
            if (hadError) HideStatus(sampleError);
            if (hadError) ShowAutoBar(); // the slot is free again (RefreshAutoRow draws the bar on other samples)
            // The admin line stays one short line; its longer reason is the link's help text (not while prompting).
            if (elevationState != ElevationState.Prompting)
                AutomationProperties.SetHelpText(restartButton, AppText.ElevationNotice(s.HasLists));

            double load = Math.Min(100, s.LoadPercent);
            Motion.To(loadNumber, Figure.WholeProperty, load, Motion.MapDuration, Motion.Enter, animate);
            if (!animate) loadNumber.Text = load.ToString(CultureInfo.InvariantCulture);
            inUseRun.Text = AppText.InUseLine(s);
            AutomationProperties.SetName(loadCard, AppText.LoadCardName(s));

            MemoryMap map = MemoryMap.From(s);
            foreach (MapPart part in MemoryMap.Parts)
                Motion.To(segments[(int)part], MapPanel.WeightProperty, map.Fraction(part), Motion.MapDuration, Motion.Spring, animate);
            AutomationProperties.SetName(mapBar, AppText.MapName(map, s.TotalPhys));

            // Without the page lists the map has two parts, in use and available, and says why.
            Visibility detail = map.Detailed ? Visibility.Visible : Visibility.Collapsed;
            rowModified.Visibility = detail;
            rowStandby.Visibility = detail;
            listsNote.Visibility = map.Detailed ? Visibility.Collapsed : Visibility.Visible;
            listsNote.Text = AppText.ListsNote(elevated);
            string freeName = map.Detailed ? "Free" : "Available";
            freeLabel.Text = freeName;
            // Two parts: available sits under in use, in the left column.
            Grid.SetRow(rowFree, map.Detailed ? 1 : 1);
            Grid.SetColumn(rowFree, map.Detailed ? 2 : 0);

            SetRow(rowInUse, valInUse, MapPart.InUse, "In use", map, animate);
            SetRow(rowModified, valModified, MapPart.Modified, "Modified, waiting to be written", map, animate);
            SetRow(rowStandby, valStandby, MapPart.Standby, "Standby, given back on demand", map, animate);
            SetRow(rowFree, valFree, MapPart.Free, freeName, map, animate);

            commitValue.Text = AppText.CommitText(s);
            double pct = s.CommitLimit == 0 ? 0 : Math.Min(100, s.CommitPercent);
            Motion.To(commitUsed, MapPanel.WeightProperty, pct, Motion.MapDuration, Motion.Spring, animate);
            Motion.To(commitRest, MapPanel.WeightProperty, 100 - pct, Motion.MapDuration, Motion.Spring, animate);
            ShowCommitWarning(AppText.CommitWarnOn(commitWarnOn, s), s);
            ShowAdvice(s);
            mapReady = true;
            UpdateTray();
        }

        /// <summary>
        /// U15: near the commit limit the bar's fill turns Caution (fill and glyph change together, at once) and the
        /// warning line takes the note's place: it fades in (320 ms, ease out) and out (200 ms, ease in), instantly with
        /// Windows animations off. Read out only on Normal to Warning while the window shows.
        /// </summary>
        void ShowCommitWarning(bool warn, MemorySnapshot s)
        {
            bool entering = warn && !commitWarnOn;
            bool leaving = !warn && commitWarnOn;
            commitWarnOn = warn;
            commitUsed.SetResourceReference(Border.BackgroundProperty, warn ? "Caution" : "Accent");
            AutomationProperties.SetName(commitRow, AppText.CommitName(s, warn));
            if (entering)
            {
                int gen = ++commitWarnGeneration;
                commitWarningText.Text = AppText.CommitWarning;
                AutomationProperties.SetName(commitWarning, AppText.CommitWarning);
                commitNote.Visibility = Visibility.Collapsed;
                commitWarning.Visibility = Visibility.Visible;
                bool animate = WindowShowing && Motion.On;
                if (animate) Motion.To(commitWarning, UIElement.OpacityProperty, 0, TimeSpan.Zero, null, false);
                Motion.To(commitWarning, UIElement.OpacityProperty, 1, Motion.EnterDuration, Motion.Enter, animate);
                if (WindowShowing)
                    dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (gen == commitWarnGeneration && commitWarnOn) commitWarning.Announce(); }));
            }
            else if (leaving)
            {
                int gen = ++commitWarnGeneration;
                Motion.To(commitWarning, UIElement.OpacityProperty, 0, Motion.ExitDuration, Motion.Exit, WindowShowing, () =>
                {
                    if (gen != commitWarnGeneration) return;
                    commitWarning.Visibility = Visibility.Collapsed;
                    commitNote.Visibility = Visibility.Visible;
                });
            }
        }

        /// <summary>
        /// R1: the line under Clean memory, from Windows' low/high memory signal and the commit level, steadied over two
        /// samples. Display only (it never gates auto-clean, owner 2026-11-17), and hidden unless the figures are live.
        /// </summary>
        void ShowAdvice(MemorySnapshot s)
        {
            CleanAdvice a = AppText.AdviceVisible(memView.State) ? advice.Shown : CleanAdvice.None;
            string text = a == CleanAdvice.None ? null : AppText.CleanAdvice(a, s);
            string glyph = AppText.AdviceGlyph(a);
            if (text == null || glyph == null)
            {
                adviceLine.Visibility = Visibility.Collapsed;
                return;
            }
            adviceText.Text = text;
            adviceText.SetResourceReference(TextBlock.ForegroundProperty, AppText.AdviceTextBrush(a));
            adviceGlyph.Data = Icon(glyph);
            adviceGlyph.SetResourceReference(Shape.StrokeProperty, AppText.AdviceStroke(a));
            adviceLine.Visibility = Visibility.Visible;
        }

        void SetRow(SummaryPanel row, TextBlock value, MapPart part, string label, MemoryMap map, bool animate)
        {
            ulong bytes = map.Bytes(part);
            SetFigure(value, bytes, animate);
            // After a clean the row's name carries its change too (7A), as the line under the figure shows it.
            AutomationProperties.SetName(row, AppText.RowName(label, null, Format.Bytes(bytes), PartChange(part)));
        }

        /// <summary>Figures tween (QuinticEase, 700 ms) to their new value, or jump on the first reading and
        /// when Windows animations are off.</summary>
        static void SetFigure(TextBlock value, ulong bytes, bool animate)
        {
            double target = bytes;
            Motion.To(value, Figure.BytesProperty, target, Motion.MapDuration, Motion.Enter, animate);
            if (!animate || !Motion.On) value.Text = Format.Bytes(bytes);
        }

        /// <summary>
        /// A clean's failure goes to its result line; a sampling failure (even one during a manual clean) to the
        /// banner, and moves the view to LiveWithError or FailedNoData.
        /// </summary>
        void OnServiceError(Exception ex, ServiceErrorSource source)
        {
            if (ServiceErrorRoute.IsCleanFailure(source, cleaningUi)) { cleanError = ex; return; }
            // Once per new message, or the same one every 10 minutes: a failure every 2 s must not fill the list.
            DateTime nowUtc = DateTime.UtcNow;
            if (ErrorGate.ShouldLog(loggedReadError, loggedReadErrorUtc, ex.Message, nowUtc))
            {
                loggedReadError = ex.Message;
                loggedReadErrorUtc = nowUtc;
                // Only a sampling failure is "could not be read"; a handler's own error goes to the error log.
                if (ActivityEvents.IsReadFailure(source)) AddActivity(ActivityEvents.ReadFailed(ex.Message));
                else ErrorLog.TryWrite(ErrorLog.DefaultPath, "auto-clean error", ex);
            }
            if (ActivityEvents.IsReadFailure(source)) readFailing = true;
            memView.SampleFailed();
            // U9: read out when it appears or its message changes, never again on each failing 2-second sample.
            ShowStatus(sampleError, sampleErrorText, AppText.SampleErrorText(ex.Message, (int)SampleInterval.TotalSeconds, memView.HasData));
            ShowAutoBar(); // the error takes the slot
            ShowMemoryState();
            if (lastSnapshot == null) UpdateTray();
        }

        /// <summary>
        /// FailedNoData: "Unavailable" instead of "Reading memory...", dashes in the legend, Clean waits.
        /// LiveWithError: the last figures stay, with when they were read under them.
        /// </summary>
        void ShowMemoryState()
        {
            if (memView.State == MemoryViewState.FailedNoData)
            {
                inUseRun.Text = AppText.MemoryCaption(memView.State);
                foreach (TextBlock value in new[] { valInUse, valModified, valStandby, valFree }) value.Text = "--";
                AutomationProperties.SetName(loadCard, "Memory load unavailable");
            }
            bool stale = memView.State == MemoryViewState.LiveWithError && lastSnapshot != null;
            string lastRead = stale ? AppText.LastReadLine(memView.LastRead.Value, DateTime.Now, CultureInfo.CurrentCulture) : null;
            lastReadNote.Text = lastRead ?? "";
            lastReadNote.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
            if (stale) AutomationProperties.SetName(loadCard, AppText.LoadCardName(lastSnapshot) + ". " + lastRead);
            // The advice speaks about now: a failing read hides it, and the next good read shows its own at once.
            advice.Reset();
            adviceLine.Visibility = Visibility.Collapsed;
            if (memView.State == MemoryViewState.FailedNoData) ShowCommitWarning(false, null);
            UpdateCleanButton();
        }

        // ------------------------------------------------------------------ top apps

        /// <summary>Process.GetProcesses off the UI thread; one read at a time.</summary>
        void RefreshTopApps()
        {
            if (topBusy || !WindowShowing || !memoryTab.IsSelected) return;
            topBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<AppMemory> top = null;
                // R5: by private bytes, which a clean does not change, so the order holds while pages fault back in.
                try { top = AppMemoryList.Top(TopAppsCount, AppSort.Private); }
                catch (Exception) { top = null; }
                Post(() => { topBusy = false; BuildTopList(top); });
            });
        }

        void BuildTopList(List<AppMemory> top)
        {
            topList.Children.Clear();
            if (top == null || top.Count == 0)
            {
                var none = new TextBlock { Text = "Could not read the running apps." };
                none.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
                topList.Children.Add(none);
                return;
            }
            ulong max = Math.Max(1UL, top[0].PrivateBytes);
            foreach (AppMemory a in top)
            {
                // Transparent, not null: the tooltip then shows on hover anywhere on the row (U17). Rows stay out of the
                // tab order (read-only data); the name below is the keyboard and screen-reader path.
                var row = new SummaryPanel { Padding = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
                AutomationProperties.SetName(row, AppText.TopAppName(a));
                row.ToolTip = TopAppTip(a);
                var stack = new StackPanel();
                // Same columns as the heads in MainWindow.xaml: name, In RAM (muted), Private (the ranking figure).
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                name.Inlines.Add(new Run(AppText.DisplayName(a.Name)));
                if (a.Processes > 1)
                {
                    var count = new Run("  " + a.Processes.ToString(CultureInfo.InvariantCulture) + " processes") { FontSize = 12 };
                    count.SetResourceReference(TextElement.ForegroundProperty, "MutedText");
                    name.Inlines.Add(count);
                }
                line.Children.Add(name);
                var resident = new TextBlock { Text = Format.Bytes(a.Bytes), FontSize = 13 };
                resident.SetResourceReference(FrameworkElement.StyleProperty, "FigureText");
                resident.SetResourceReference(TextBlock.ForegroundProperty, "MutedText");
                Grid.SetColumn(resident, 1);
                line.Children.Add(resident);
                var figure = new TextBlock { Text = Format.Bytes(a.PrivateBytes) };
                figure.SetResourceReference(FrameworkElement.StyleProperty, "FigureText");
                Grid.SetColumn(figure, 2);
                line.Children.Add(figure);
                stack.Children.Add(line);

                double share = (double)a.PrivateBytes / max;
                var bar = new Grid { Height = 3, Margin = new Thickness(0, 4, 0, 0) };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, share), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0, 1 - share), GridUnitType.Star) });
                var track = new Border { CornerRadius = new CornerRadius(1.5) };
                track.SetResourceReference(Border.BackgroundProperty, "TrackBackground");
                track.BorderThickness = new Thickness(1);
                track.SetResourceReference(Border.BorderBrushProperty, "TrackEdge");
                Grid.SetColumnSpan(track, 2);
                bar.Children.Add(track);
                var fill = new Border { CornerRadius = new CornerRadius(1.5) };
                fill.SetResourceReference(Border.BackgroundProperty, "MutedText");
                bar.Children.Add(fill);
                stack.Children.Add(bar);
                row.Child = stack;
                topList.Children.Add(row);
            }
        }

        /// <summary>
        /// U17: the full name, then what each figure means, one wrapping line each; plain text only (process names
        /// are chosen by whoever names the exe, so they go through AppText.DisplayName and never into markup).
        /// </summary>
        static UIElement TopAppTip(AppMemory a)
        {
            var panel = new StackPanel { MaxWidth = 300 };
            List<string> lines = AppText.TopAppTip(a);
            for (int i = 0; i < lines.Count; i++)
            {
                var t = new TextBlock { Text = lines[i], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0) };
                if (i == 0) t.FontWeight = FontWeights.SemiBold;
                panel.Children.Add(t);
            }
            return panel;
        }

        // ------------------------------------------------------------------ cleaning

        void RequestClean()
        {
            TryRequestClean();
        }

        /// <summary>The Clean button's path, for the button, the tray and the shortcut; false when no clean started.</summary>
        bool TryRequestClean()
        {
            if (exiting || !AppText.CleanEnabled(elevated, cleaningUi, memView.State)) return false;
            bool started;
            try { started = service.CleanNow(); }
            catch (ObjectDisposedException) { return false; }
            if (started) SetCleaning(true);
            return started;
        }

        void OnCleanStarted(CleanReason reason)
        {
            cleanError = null;
            SetCleaning(true);
        }

        void OnCleanCompleted(CleanReport report, CleanReason reason)
        {
            // Before SetCleaning, whose tray update shows it.
            lastClean = new LastCleanInfo { Utc = DateTime.UtcNow, Reason = reason, Report = report, Error = cleanError };
            history.MarkClean(lastClean.Utc);
            // Before the after-sample below, whose rows then carry the change in their names.
            ShowChanges(report, lastClean.Utc);
            SetCleaning(false);
            ShowResult(report, cleanError, reason != CleanReason.Manual);
            cleanError = null;
            ShowHistory();
            if (report != null && report.After != null) OnSampled(report.After);
            if (report != null && report.Services.Count > 0 && settingsTab.IsSelected) RefreshServices();
            UpdateRestoreUi();
            RefreshAutoRow(); // the clean moved the back-off streak and the next threshold time
            ShowNotice(Notices.ForClean(settings.NotifyProblems, WindowShowing, reason, report, lastClean.Error, DateTime.Now, CultureInfo.CurrentCulture));
        }

        /// <summary>
        /// R6: a problem notice from the tray icon, at most one of a kind an hour (NoticeGate), logged to Recent activity
        /// and cleans.log. Notices decided whether there is one at all (setting on, window not showing, automatic clean).
        /// </summary>
        void ShowNotice(Notice n)
        {
            if (n == null || tray == null || exiting) return;
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (!NoticeGate.Allow(noticeShownUtc.TryGetValue(n.Kind, out last) ? last : (DateTime?)null, now)) return;
            if (!tray.ShowNotice(n.Title, n.Text)) return;
            noticeShownUtc[n.Kind] = now;
            AddActivity(Notices.ShownEvent(n));
        }

        void SetCleaning(bool on)
        {
            if (on && cleanButton.IsKeyboardFocused) refocusClean = true;
            cleaningUi = on;
            if (on) StartProgress();
            else FinishProgress();
            SetCleanLabel();
            UpdateCleanButton();
            if (!on && refocusClean && cleanButton.IsEnabled)
            {
                refocusClean = false;
                cleanButton.Focus();
            }
            UpdateTray();
        }

        /// <summary>
        /// The line under Clean memory: "Last clean at 14:32 freed 2.8 GB. Load went from 81% to 74%." with a
        /// check (all went well), a warning (something failed) or a dash (nothing freed), and the per-area results
        /// behind a disclosure. Manual cleans are announced; automatic ones update the line quietly.
        /// </summary>
        void ShowResult(CleanReport report, Exception error, bool automatic)
        {
            resultAt = DateTime.Now;
            resultReport = report;
            resultError = error;
            resultAutomatic = automatic;
            string line = AppText.ResultLine(resultAt.Value, resultAt.Value, report, error, automatic, CultureInfo.CurrentCulture);
            resultText.Text = line;
            bool good = AppText.CleanWentWell(report);
            resultGlyph.Data = Icon(!good ? "IconWarn" : report.InUseFreed > 0 ? "IconCheck" : "IconDash");
            resultGlyph.SetResourceReference(Shape.StrokeProperty, !good ? "Caution" : report.InUseFreed > 0 ? "Ok" : "MutedText");

            resultRows.Children.Clear();
            if (report != null)
            {
                bool first = true;
                foreach (AreaResult r in report.Results)
                {
                    resultRows.Children.Add(BuildResultRow(AppText.Describe(r), first));
                    first = false;
                }
            }
            resultCount = resultRows.Children.Count;
            // E8: each area's time is in its row; the whole clean's under them ("Took 3.4 s.").
            string took = resultCount > 0 ? AppText.TookLine(report) : null;
            if (took != null)
            {
                var tookText = new TextBlock { Text = took, FontSize = 12, Margin = new Thickness(0, 4, 0, 6) };
                tookText.SetResourceReference(TextBlock.ForegroundProperty, "MutedText");
                resultRows.Children.Add(tookText);
            }
            resultDetails.IsExpanded = false;
            resultDetails.Header = AppText.DetailsLabel(false, resultCount);
            resultDetails.Visibility = resultCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            AutomationProperties.SetName(resultPanel, SpokenResult(line));
            if (!resultShown) EnterResult();
            if (!automatic) dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => resultPanel.Announce()));
        }

        /// <summary>Screen readers get the in-use figures too, which the line leaves to the legend.</summary>
        string SpokenResult(string line)
        {
            CleanReport r = resultReport;
            // A busy skip ran nothing: its figures would only repeat the load.
            return r != null && r.Before != null && r.After != null && r.Stopped != CleanStop.Busy ? line + " " + AppText.Summary(r) : line;
        }

        /// <summary>
        /// Run on each sample: once the clean is no longer from today the line says "yesterday" or the date, so a
        /// result from days ago in the tray never reads as current. Changed quietly (not announced).
        /// </summary>
        void RefreshResultLine()
        {
            if (resultAt == null) return;
            string line = AppText.ResultLine(resultAt.Value, DateTime.Now, resultReport, resultError, resultAutomatic, CultureInfo.CurrentCulture);
            if (line == resultText.Text) return;
            resultText.Text = line;
            AutomationProperties.SetName(resultPanel, SpokenResult(line));
        }

        /// <summary>The first result enters with a fade and an 8 px rise, 320 ms QuinticEase out; later ones replace it in place.</summary>
        void EnterResult()
        {
            resultShown = true;
            var rise = (TranslateTransform)resultPanel.RenderTransform;
            resultPanel.Visibility = Visibility.Visible;
            if (Motion.On)
            {
                Motion.To(resultPanel, UIElement.OpacityProperty, 0, TimeSpan.Zero, null, false);
                Motion.To(rise, TranslateTransform.YProperty, Motion.RiseDistance, TimeSpan.Zero, null, false);
            }
            Motion.To(resultPanel, UIElement.OpacityProperty, 1, Motion.EnterDuration, Motion.Enter, true);
            Motion.To(rise, TranslateTransform.YProperty, 0, Motion.EnterDuration, Motion.Enter, true);
        }

        static readonly Geometry CheckGlyph = Freeze(Geometry.Parse("M3.5,8.5 L6.5,11.5 L12.5,4.5"));
        static readonly Geometry CrossGlyph = Freeze(Geometry.Parse("M4.5,4.5 L11.5,11.5 M11.5,4.5 L4.5,11.5"));
        static readonly Geometry DashGlyph = Freeze(Geometry.Parse("M4.5,8 L11.5,8"));
        /// <summary>The IconWarn shape from Styles.xaml, for a row that only partly worked.</summary>
        static readonly Geometry WarnGlyph = Freeze(Geometry.Parse("M8,2.5 L14,13.5 H2 Z M8,6.5 V9.5 M8,11.75 V11.8"));

        static Geometry Freeze(Geometry g) { g.Freeze(); return g; }

        /// <summary>Glyph plus words: ok (check), ok: partly (warning), failed: reason (cross), skipped: reason (dash).</summary>
        static UIElement BuildResultRow(AreaLine line, bool first)
        {
            string brush = line.State == AreaState.Ok ? "Ok" : line.State == AreaState.Failed ? "Danger"
                : line.State == AreaState.Partial ? "Caution" : "MutedText";
            var row = new SummaryPanel { Padding = new Thickness(0, 6, 0, 6) };
            if (!first)
            {
                row.BorderThickness = new Thickness(0, 1, 0, 0);
                row.SetResourceReference(Border.BorderBrushProperty, "Hairline");
            }
            AutomationProperties.SetName(row, AppText.AreaRowName(line));

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var glyph = new Path
            {
                Width = 16, Height = 16, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = line.State == AreaState.Ok ? CheckGlyph : line.State == AreaState.Failed ? CrossGlyph
                    : line.State == AreaState.Partial ? WarnGlyph : DashGlyph
            };
            glyph.SetResourceReference(Shape.StrokeProperty, brush);
            grid.Children.Add(glyph);

            var label = new TextBlock { Text = line.Label, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var status = new TextBlock { Text = line.Status, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            status.SetResourceReference(TextBlock.ForegroundProperty, brush);
            if (line.State == AreaState.Ok)
            {
                status.Margin = new Thickness(10, 0, 0, 0);
                status.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(status, 2);
            }
            else
            {
                status.Margin = new Thickness(10, 2, 0, 0);
                status.FontSize = 12;
                if (line.State == AreaState.Failed) status.FontWeight = FontWeights.SemiBold;
                Grid.SetRow(status, 1);
                Grid.SetColumn(status, 1);
                Grid.SetColumnSpan(status, 3);
            }
            grid.Children.Add(status);
            // E8: how long the area took, muted at the row's end; a skipped area ran nothing and shows no time.
            if (line.Time != null)
            {
                var time = new TextBlock { Text = line.Time, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                time.SetResourceReference(FrameworkElement.StyleProperty, "FigureText");
                time.SetResourceReference(TextBlock.ForegroundProperty, "MutedText");
                Grid.SetColumn(time, 3);
                grid.Children.Add(time);
            }
            row.Child = grid;
            return row;
        }

        // ------------------------------------------------------------------ settings

        AppSettings ServiceSettings()
        {
            AppSettings s = settings.Clone();
            // Not elevated: every clean would fail, so automatic cleans wait until the app runs as administrator.
            if (!elevated) s.AutoCleanEnabled = false;
            return s;
        }

        void SetAutoClean(bool on)
        {
            settings.AutoCleanEnabled = on;
            // Turning it off ends a pause (the service drops it too), so turning it on again never finds an old one.
            if (!on) settings.AutoCleanPausedUntilUtc = null;
            SaveAndApply();
        }

        /// <summary>Holds automatic cleans for an hour (tray, Settings). Only offered when it changes something.</summary>
        void PauseAutoClean(PauseOrigin origin)
        {
            if (exiting || service == null || !AppText.CanPause(AppText.AutoState(settings.AutoCleanEnabled, elevated, service.GetStatus())))
                return;
            try { service.Pause(AutoCleanService.PauseLength); }
            catch (ObjectDisposedException) { return; }
            pauseOrigin = origin; // the bar's second line says where it came from
            RefreshAutoRow();
        }

        void ResumeAutoClean()
        {
            if (exiting || service == null) return;
            service.Resume();
            userResumed = true; // the activity list says "resumed", not "ended"
            RefreshAutoRow();
        }

        /// <summary>Hides the back-off bar until the streak changes; the auto-clean card keeps the details.</summary>
        void DismissAutoBar()
        {
            if (barNotice == null) return;
            dismissedBarKey = barNotice.Key;
            ShowAutoBar();
        }

        /// <summary>The saved pause follows the service's, so it outlasts a restart (owner decision); elevated only.</summary>
        void PersistPause()
        {
            if (!elevated || service == null || exiting) return;
            DateTime? want = autoInfo != null && autoInfo.Paused ? autoInfo.PausedUntilUtc : null;
            if (settings.AutoCleanPausedUntilUtc == want) return;
            settings.AutoCleanPausedUntilUtc = want;
            SaveAndApply();
        }

        /// <summary>
        /// Sleep and wake (P5). Runs on the SystemEvents thread, where an exception would end the process: the service
        /// calls never throw, and everything else is caught. StatusChange is ignored: each tick reads the power line.
        /// </summary>
        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                AutoCleanService s = service;
                if (s == null || exiting) return;
                if (e.Mode == PowerModes.Suspend) s.NotifySuspend();
                else if (e.Mode == PowerModes.Resume) s.NotifyResume();
            }
            catch (Exception) { }
        }

        void SetPowerClean(bool on)
        {
            settings.PowerClean = on;
            SaveAndApply();
        }

        void SetCleanLabel()
        {
            string label = AppText.CleanLabel(settings.PowerClean, cleaningUi);
            cleanButton.Content = label;
            AutomationProperties.SetName(cleanButton, label);
        }

        void ShowThreshold()
        {
            thresholdValue.Text = ((int)Math.Round(thresholdSlider.Value)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>Saves a threshold still waiting for its 400 ms pause: on the timer, and before exit, an elevated restart or a resync.</summary>
        void FlushThreshold()
        {
            if (thresholdTimer != null) thresholdTimer.Stop();
            if (thresholdPending.Take()) SaveThreshold();
        }

        void SaveThreshold()
        {
            int value = (int)Math.Round(thresholdSlider.Value);
            if (value == settings.ThresholdPercent || !AppSettings.InRange(value, AppSettings.MinThresholdPercent, AppSettings.MaxThresholdPercent)) return;
            settings.ThresholdPercent = value;
            SaveAndApply();
        }

        // ------------------------------------------------------------------ Power Clean services

        /// <summary>Reads the catalog services (WMI, about a second) off the UI thread, then rebuilds the list.</summary>
        void RefreshServices()
        {
            if (servicesBusy) return;
            servicesBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ServiceState> states = null;
                string failure = null;
                try { states = ServiceTrimmer.Query(); }
                catch (Exception ex) { failure = AppText.Plain(ex.Message, "unknown error"); }
                Post(() =>
                {
                    servicesBusy = false;
                    BuildServiceList(states, failure);
                });
            });
        }

        /// <summary>One checkbox per installed catalog service (per-user instances share their rule's row).</summary>
        void BuildServiceList(List<ServiceState> states, string failure)
        {
            powerList.Children.Clear();
            if (states == null)
            {
                powerList.Children.Add(MutedLine("Services could not be read: " + failure + "."));
                UpdateRestoreUi();
                return;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<KeyValuePair<ServiceRule, ServiceState>>();
            foreach (ServiceState s in states)
            {
                if (!seen.Add(s.Rule.Name))
                {
                    // A second per-user instance: prefer the running one for the memory figure.
                    for (int i = 0; i < rows.Count; i++)
                        if (rows[i].Key == s.Rule && !rows[i].Value.Running && s.Running)
                            rows[i] = new KeyValuePair<ServiceRule, ServiceState>(s.Rule, s);
                    continue;
                }
                rows.Add(new KeyValuePair<ServiceRule, ServiceState>(s.Rule, s));
            }
            if (rows.Count == 0) powerList.Children.Add(MutedLine("None of the services Power Clean knows are installed."));
            powerRows.Clear();
            foreach (KeyValuePair<ServiceRule, ServiceState> row in rows) powerRows.Add(row.Key.Name);
            UpdateServicesHeader();
            SyncSettingsUi(false); // the Power Clean note under Clean memory counts installed services too

            bool first = true;
            syncing = true;
            try
            {
                foreach (KeyValuePair<ServiceRule, ServiceState> row in rows)
                {
                    ServiceRule rule = row.Key;
                    string detail = AppText.ServiceDetail(rule, row.Value);
                    var content = new StackPanel();
                    content.Children.Add(new TextBlock { Text = rule.Label, TextWrapping = TextWrapping.Wrap });
                    content.Children.Add(SmallText(detail + "."));
                    var box = new CheckBox { Content = content, IsChecked = settings.PowerServices.Contains(rule.Name),
                        VerticalContentAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 8) };
                    AutomationProperties.SetName(box, rule.Label);
                    AutomationProperties.SetHelpText(box, detail);
                    OnToggle(box, on => ToggleService(rule.Name, on));
                    var border = new Border { Child = box };
                    if (!first)
                    {
                        border.BorderThickness = new Thickness(0, 1, 0, 0);
                        border.SetResourceReference(Border.BorderBrushProperty, "Hairline");
                    }
                    first = false;
                    powerList.Children.Add(border);
                }
            }
            finally { syncing = false; }
            UpdateRestoreUi();
        }

        static TextBlock MutedLine(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
            t.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
            return t;
        }

        void ToggleService(string name, bool on)
        {
            var list = new List<string>(settings.PowerServices);
            list.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (on) list.Add(name);
            settings.PowerServices = ServiceCatalog.Filter(list);
            SaveAndApply();
            UpdateServicesHeader();
        }

        /// <summary>Chosen services that are installed, once the list has been read; until then, every chosen name.</summary>
        int ChosenServiceCount()
        {
            if (powerRows.Count == 0) return settings.PowerServices.Count;
            int chosen = 0;
            foreach (string n in settings.PowerServices) if (powerRows.Contains(n)) chosen++;
            return chosen;
        }

        void UpdateServicesHeader()
        {
            string header = AppText.ServicesHeader(ChosenServiceCount(), powerRows.Count);
            powerServicesList.Header = header;
            AutomationProperties.SetName(powerServicesList, header);
        }

        void RestoreServices()
        {
            if (!elevated || servicesBusy) return;
            servicesBusy = true;
            restoreButton.IsEnabled = false;
            powerStatus.Text = "Starting services...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ServiceAction> done = null;
                string failure = null;
                try { done = ServiceTrimmer.Restore(); }
                catch (Exception ex) { failure = AppText.Plain(ex.Message, "unknown error"); }
                Post(() =>
                {
                    servicesBusy = false;
                    UpdateRestoreUi();
                    var notes = new List<string>();
                    if (failure != null) notes.Add("Could not restore services: " + failure + ".");
                    if (done != null)
                        foreach (ServiceAction a in done)
                            if (a.Failed) notes.Add(a.Label + ": " + a.Message + ".");
                    if (notes.Count > 0) powerStatus.Text = string.Join(" ", notes.ToArray()) + " " + powerStatus.Text;
                    // The result of the user's Restore is read out even when it matches the last one read.
                    announced.Remove(powerStatusRegion);
                    Announce(powerStatusRegion, powerStatus.Text);
                    RefreshServices();
                });
            });
        }

        void UpdateRestoreUi()
        {
            int stopped = ServiceTrimmer.StoppedThisSession().Count;
            restoreButton.IsEnabled = elevated && !servicesBusy && stopped > 0;
            powerStatus.Text = elevated ? AppText.RestoreStatus(stopped) : AppText.ServicesNeedAdmin;
            // The region is a named group in the UIA tree: its name follows the text (only Restore reads it out).
            AutomationProperties.SetName(powerStatusRegion, powerStatus.Text);
        }

        /// <summary>
        /// P12: keys edited in settings.ini by hand since the last load or save are kept (three-way merge); the
        /// app's own change wins for the key it changed. A failed save keeps the baseline, so a later merge still
        /// knows what the app changed, and the app runs on the new values until it closes (and says so).
        /// </summary>
        bool SaveAndApply()
        {
            return SaveAndApply(null);
        }

        /// <param name="before">The settings ahead of the change, for ApplySettings to diff against; null for the
        /// current ones. A reset passes the settings it replaced, so the theme and the service rows follow it.</param>
        /// <returns>False when the save failed (the SaveError line is showing).</returns>
        bool SaveAndApply(AppSettings before)
        {
            if (before == null) before = settings.Clone();
            bool saved = false;
            FileStamp now = SettingsStore.Stamp(settingsPath);
            // Only a file that was read and exists is merged: an unreadable one must not reset other settings, so the
            // app's own settings are saved as they are.
            AppSettings disk;
            if (now.Exists && !now.Equals(stamp) && SettingsStore.TryLoad(settingsPath, out disk))
                settings = SettingsMerge.Merge(baseline, settings, disk.Normalized());
            try
            {
                SettingsStore.Save(settings, settingsPath);
                baseline = settings.Clone();
                stamp = SettingsStore.Stamp(settingsPath);
                syncState = SettingsSync.Next(syncState, SettingsSyncEvent.SaveOk);
                HideStatus(saveErrorRegion);
                saved = true;
            }
            catch (IOException ex) { SaveFailed(ex); }
            catch (UnauthorizedAccessException ex) { SaveFailed(ex); }
            ApplySettings(before);
            return saved;
        }

        void SaveFailed(Exception ex)
        {
            syncState = SettingsSync.Next(syncState, SettingsSyncEvent.SaveFailed);
            ShowSaveError(ex);
        }

        void ShowSaveError(Exception ex)
        {
            ShowStatus(saveErrorRegion, saveError, AppText.SaveFailedText(ex.Message));
        }

        /// <summary>
        /// Puts the current settings to work: auto-clean, the Power Clean list, the UI, the tray and the theme.
        /// before is the settings ahead of the change, so values that came from the file (a merge or reload) also
        /// move the slider, the service checkboxes and the theme, which a change made in the UI already shows.
        /// </summary>
        void ApplySettings(AppSettings before)
        {
            powerServices = ServiceCatalog.Filter(settings.PowerServices);
            if (service != null) service.Update(ServiceSettings());
            EnsureIntervalChoice();
            // A new threshold moves the slider too, unless a slider value of the user's own still waits.
            SyncSettingsUi(settings.ThresholdPercent != before.ThresholdPercent && !thresholdPending.Pending);
            // An area newly chosen behind the disclosure (from the file) is shown, so the list never hides what will be
            // cleaned; a disclosure the user closed stays closed on later saves.
            if (AreasDisclosure.ShouldReveal(before.Areas, settings.Areas, HiddenAreas)) moreAreas.IsExpanded = true;
            RefreshAutoRow();
            UpdateTray();
            if (settings.Theme != before.Theme) ReapplyTheme();
            else if (settings.UseMica != before.UseMica) ApplyBackdrop();
            bool servicesChanged = string.Join(",", settings.PowerServices.ToArray()) != string.Join(",", before.PowerServices.ToArray());
            if (servicesChanged && powerRows.Count > 0) RefreshServices();
            // A shortcut changed in settings.ini by hand (or by a reset) is registered again.
            string shortcut = hotkey == null || hotkey.Current == null ? "" : hotkey.Current.Format();
            if (shortcut != (settings.CleanHotkey ?? "")) StartHotkey();
        }

        /// <summary>An interval read from the file that the box does not list yet (a hand-edited 7 minutes) gets its own choice.</summary>
        void EnsureIntervalChoice()
        {
            foreach (object o in intervalBox.Items)
            {
                var item = o as ComboBoxItem;
                if (item != null && Equals(item.Tag, settings.IntervalMinutes)) return;
            }
            bool wasSyncing = syncing;
            syncing = true;
            try
            {
                intervalBox.Items.Clear();
                foreach (int minutes in AppText.IntervalChoices(settings.IntervalMinutes))
                    intervalBox.Items.Add(new ComboBoxItem { Content = AppText.IntervalLabel(minutes), Tag = minutes });
            }
            finally { syncing = wasSyncing; }
        }

        /// <summary>
        /// On window activation (P12, owner decision b): retries a failed save, else reloads settings.ini when its
        /// stamp changed. One stat per activation; no FileSystemWatcher. Everything still goes through Parse and
        /// Normalized, and Start with Windows never follows the file (the logon task is its source of truth).
        /// </summary>
        void CheckSettingsFile()
        {
            if (exiting) return;
            if (syncState == SettingsSyncState.SaveFailed) { SaveAndApply(); return; }
            // A slider value still waiting for its save would be overwritten: check on a later activation.
            if (thresholdTimer != null && thresholdTimer.IsEnabled) return;
            FileStamp now = SettingsStore.Stamp(settingsPath);
            // Unchanged, unreadable or deleted: nothing to apply (the next save writes the file again).
            if (!now.Exists || now.Equals(stamp)) return;
            syncState = SettingsSync.Next(syncState, SettingsSyncEvent.DiskChanged);
            AppSettings disk;
            if (!SettingsStore.TryLoad(settingsPath, out disk))
            {
                // Locked, too big or refused: keep running on the current settings and try on a later activation.
                syncState = SettingsSync.Next(syncState, SettingsSyncEvent.ReloadFailed);
                return;
            }
            disk = disk.Normalized();
            disk.StartWithWindows = settings.StartWithWindows;
            AppSettings before = settings;
            settings = disk;
            baseline = disk.Clone();
            stamp = now;
            syncState = SettingsSync.Next(syncState, SettingsSyncEvent.ReloadOk);
            ApplySettings(before);
        }

        /// <summary>
        /// Auto-clean's state everywhere it shows: the card's line, the bar at the top of the Memory tab, the Settings
        /// pause row and cooldown line, and the tray. Runs on every sample, so it only touches what changed; it also
        /// keeps the saved pause in step with the service's.
        /// </summary>
        void RefreshAutoRow()
        {
            autoInfo = service == null ? null : service.GetStatus();
            pauseOrigin = AppText.KeepPauseOrigin(pauseOrigin, autoInfo);
            AutoCleanState state = AppText.AutoState(settings.AutoCleanEnabled, elevated, autoInfo);
            CultureInfo culture = CultureInfo.CurrentCulture;
            string description = AppText.Sentence(AppText.AutoDescription(settings));
            AutoNotice card = AppText.AutoCard(state, autoInfo, settings, DateTime.UtcNow, culture);
            barNotice = AppText.AutoBar(state, autoInfo, settings, culture, pauseOrigin);
            string pauseNote = AppText.PauseNote(autoInfo, culture);
            string cooldown = AppText.Sentence(AppText.CooldownText(settings.CooldownSeconds, Streak));
            // Runs on every 2-second sample, tray-hidden too: the visual tree is touched only when something it shows changed.
            string renderKey = state + "#" + description + "#" + AppText.RenderKey(card) + "#" + AppText.RenderKey(barNotice)
                + "#" + pauseNote + "#" + cooldown;
            if (renderKey != autoRenderKey)
            {
                autoRenderKey = renderKey;
                autoDescription.Text = description;
                ShowAutoCard(card);
                ShowAutoBar();
                ShowPauseRow(state, pauseNote);
                cooldownText.Text = cooldown;
            }
            string trayKey = state + "|" + AppText.TrayAutoLabel(state, autoInfo, culture);
            if (trayKey != trayAutoKey) UpdateTray();
            PersistPause();
            LogStatusChanges(culture);
        }

        /// <summary>A pause starting or ending and the back-off starting or ending go in Recent activity and cleans.log.</summary>
        void LogStatusChanges(CultureInfo culture)
        {
            if (autoInfo == null) return;
            List<ActivityEvent> events = ActivityEvents.FromStatus(loggedStatus, autoInfo, userResumed, settings.ThresholdPercent, culture);
            loggedStatus = autoInfo;
            userResumed = false;
            foreach (ActivityEvent e in events) AddActivity(e);
            // R6: the back-off reaching its last step means cleaning is not helping; one notice when it gets there.
            int now = autoInfo.FutileStreak;
            int old = noticeStreak ?? now;
            noticeStreak = now;
            ShowNotice(Notices.ForStreak(settings.NotifyProblems, WindowShowing, old, now, settings.ThresholdPercent));
        }

        int Streak { get { return autoInfo == null ? 0 : autoInfo.FutileStreak; } }

        /// <summary>The line under the card's description. Focus on a Resume that goes away moves to the switch.</summary>
        void ShowAutoCard(AutoNotice n)
        {
            bool hadFocus = autoResumeButton.IsKeyboardFocused;
            if (n == null) HideStatus(autoStatusRegion);
            else
            {
                autoStatusRegion.Visibility = Visibility.Visible;
                autoStatusText.Text = n.Title;
                // U9: both back-off sentences in one name, so a screen reader that lands on the line hears the advice
                // too. It is read out when the reason (Key) changes, not when the advice's countdown moves, and only
                // when the bar above it on the same tab has nothing to say: the bar reads its own news (U1).
                Announce(autoStatusRegion, n.Name, n.Key, barNotice == null);
                autoStatusDetail.Text = n.Detail ?? "";
                autoStatusDetail.Visibility = n.Detail == null ? Visibility.Collapsed : Visibility.Visible;
                SetGlyph(autoStatusGlyph, n);
                // The second line starts under the first line's words, not under the glyph (16 px plus its 6 px gap).
                autoStatusDetail.Margin = new Thickness(n.Glyph == null ? 0 : 22, 0, 0, 0);
                autoResumeButton.Visibility = n.Resume ? Visibility.Visible : Visibility.Collapsed;
            }
            if (hadFocus && autoResumeButton.Visibility != Visibility.Visible) autoSwitch.Focus();
        }

        void SetGlyph(Path glyph, AutoNotice n)
        {
            glyph.Visibility = n.Glyph == null ? Visibility.Collapsed : Visibility.Visible;
            if (n.Glyph == null) return;
            glyph.Data = Icon(n.Glyph);
            glyph.SetResourceReference(Shape.StrokeProperty, n.Stroke ?? "Text");
        }

        /// <summary>
        /// The bar shares its slot with the sample-error bar, which wins. It enters with a fade and an 8 px drop (320 ms,
        /// ease out) and leaves with a fade (200 ms, ease in), at once when Windows animations are off. It is read out
        /// once on entering the paused or waiting state, not on every streak step.
        /// </summary>
        void ShowAutoBar()
        {
            AutoNotice n = barNotice;
            AutoBarStep step = AppText.NextAutoBar(n, sampleError.Visibility == Visibility.Visible, dismissedBarKey, announcedBarKind);
            dismissedBarKey = step.DismissedKey;
            announcedBarKind = step.AnnouncedKind;
            var drop = (TranslateTransform)autoBar.RenderTransform;
            if (!step.Show)
            {
                if (!barShown) return;
                barShown = false;
                bool hadFocus = autoBar.IsKeyboardFocusWithin;
                int gen = ++barGeneration;
                Motion.To(drop, TranslateTransform.YProperty, -Motion.RiseDistance / 2, Motion.ExitDuration, Motion.Exit, WindowShowing);
                Motion.To(autoBar, UIElement.OpacityProperty, 0, Motion.ExitDuration, Motion.Exit, WindowShowing,
                    () => { if (gen == barGeneration) autoBar.Visibility = Visibility.Collapsed; });
                if (hadFocus) autoSwitch.Focus();
                return;
            }

            autoBarTitle.Text = n.Title;
            autoBarDetail.Text = n.Detail ?? "";
            autoBarDetail.Visibility = n.Detail == null ? Visibility.Collapsed : Visibility.Visible;
            SetGlyph(autoBarGlyph, n);
            // The bar can change in place (Resume ends a pause while a back-off stands): focus on a button that goes
            // moves to the one that stays, or to the switch, never back to the window.
            bool lostFocus = (autoBarResume.IsKeyboardFocused && !n.Resume) || (autoBarDismiss.IsKeyboardFocused && !n.Dismiss);
            autoBarResume.Visibility = n.Resume ? Visibility.Visible : Visibility.Collapsed;
            autoBarDismiss.Visibility = n.Dismiss ? Visibility.Visible : Visibility.Collapsed;
            if (lostFocus)
            {
                if (n.Resume) autoBarResume.Focus();
                else if (n.Dismiss) autoBarDismiss.Focus();
                else autoSwitch.Focus();
            }
            AutomationProperties.SetName(autoBar, n.Name);
            bool announce = step.Announce;
            if (!barShown)
            {
                barShown = true;
                ++barGeneration;
                autoBar.Visibility = Visibility.Visible;
                bool animate = WindowShowing && Motion.On;
                if (animate)
                {
                    Motion.To(autoBar, UIElement.OpacityProperty, 0, TimeSpan.Zero, null, false);
                    Motion.To(drop, TranslateTransform.YProperty, -Motion.RiseDistance, TimeSpan.Zero, null, false);
                }
                Motion.To(autoBar, UIElement.OpacityProperty, 1, Motion.EnterDuration, Motion.Enter, animate);
                Motion.To(drop, TranslateTransform.YProperty, 0, Motion.EnterDuration, Motion.Enter, animate);
            }
            if (announce && WindowShowing) dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => autoBar.Announce()));
        }

        /// <summary>Settings: Pause for 1 hour, or the pause's end and Resume, on one button so focus stays put.</summary>
        void ShowPauseRow(AutoCleanState state, string note)
        {
            bool paused = state == AutoCleanState.Paused;
            pauseRow.Visibility = paused || AppText.CanPause(state) ? Visibility.Visible : Visibility.Collapsed;
            pauseNote.Text = note;
            pauseButton.Content = AppText.PauseRowButtonText(paused);
            AutomationProperties.SetName(pauseButton, AppText.PauseRowButtonName(paused));
            AutomationProperties.SetHelpText(pauseButton, "Pause automatic cleans. " + note);
        }

        void SyncSettingsUi(bool includeThreshold)
        {
            // The slider is about to be reset from the settings: a value still waiting for its save goes in first.
            if (includeThreshold) FlushThreshold();
            syncing = true;
            try
            {
                autoSwitch.IsChecked = settings.AutoCleanEnabled;
                settingsAutoSwitch.IsChecked = settings.AutoCleanEnabled;
                settingsPowerSwitch.IsChecked = settings.PowerClean;
                notifyCheck.IsChecked = settings.NotifyProblems;
                SyncMica();
                string note = AppText.PowerNote(settings.PowerClean, ChosenServiceCount());
                powerNote.Text = note ?? "";
                powerNote.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
                UpdateCleanButton();
                SetCleanLabel();
                if (includeThreshold)
                {
                    thresholdSlider.Value = settings.ThresholdPercent;
                    ShowThreshold();
                }
                cooldownText.Text = AppText.Sentence(AppText.CooldownText(settings.CooldownSeconds, Streak));
                SelectByTag(intervalBox, settings.IntervalMinutes);
                SelectByTag(themeBox, settings.Theme);
                // U21: the last selected area stays enabled and focusable (a disabled box leaves the Tab order and
                // hides its reason); its status and help say it must stay, and unticking it is refused.
                string lockedReason = null;
                foreach (KeyValuePair<CheckBox, Areas> pair in areaBoxes)
                {
                    pair.Key.IsChecked = (settings.Areas & pair.Value) != 0;
                    bool locked = !AreaRules.CanUntick(settings.Areas, pair.Value);
                    AutomationProperties.SetItemStatus(pair.Key, locked ? AppText.MustStaySelected : "");
                    string help = pair.Value == Areas.StandbyList ? AppText.StandbyHelp : "";
                    if (locked)
                    {
                        lockedReason = AppText.LastAreaReasonFor(pair.Value);
                        help = (help + " " + lockedReason).Trim();
                    }
                    AutomationProperties.SetHelpText(pair.Key, help);
                }
                ShowAreaReason(lockedReason);
                closeToTrayCheck.IsChecked = settings.CloseToTray;
                trayPercentCheck.IsChecked = settings.ShowTrayPercent;
            }
            finally { syncing = false; }
            UpdateStartupUi();
        }

        /// <summary>The line under the areas list, shown while one area is locked on; null hides it.</summary>
        void ShowAreaReason(string reason)
        {
            areaReason.Text = reason ?? "";
            AutomationProperties.SetName(areaReasonRegion, reason ?? "");
            areaReasonRegion.Visibility = reason == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// U21: unticking the last area is refused: the box is ticked again without saving, and the reason is shown and
        /// read out on every attempt, so a box that looks enabled never refuses silently.
        /// </summary>
        void RefuseLastArea(CheckBox box, string reason)
        {
            bool wasSyncing = syncing;
            syncing = true;
            try { box.IsChecked = true; }
            finally { syncing = wasSyncing; }
            ShowAreaReason(reason);
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => areaReasonRegion.Announce()));
        }

        static void SelectByTag(ComboBox box, object tag)
        {
            foreach (object o in box.Items)
            {
                var item = o as ComboBoxItem;
                if (item != null && Equals(item.Tag, tag)) { box.SelectedItem = item; return; }
            }
        }

        // ------------------------------------------------------------------ start with Windows

        /// <summary>The task itself is the source of truth; the setting only mirrors it.</summary>
        void RefreshStartupState()
        {
            if (startupBusy) return;
            startupBusy = true;
            UpdateStartupUi();
            ThreadPool.QueueUserWorkItem(delegate
            {
                StartupTaskStatus status = ReadStartupStatus();
                Post(() => FinishStartupChange(status, null));
            });
        }

        /// <summary>Absent, ours, stale or unreadable; a failure to ask at all reads as unreadable, never as off.</summary>
        static StartupTaskStatus ReadStartupStatus()
        {
            try { return StartupTask.ReadStatus(); }
            catch (Exception ex)
            {
                return new StartupTaskStatus { State = StartupTaskState.Unreadable, Error = AppText.Plain(ex.Message, "unknown error") };
            }
        }

        void ToggleStartup(bool want)
        {
            if (!AppText.StartupSwitchEnabled(elevated, startupBusy, otherAccount)) { UpdateStartupUi(); return; }
            refocusStartup = startupCheck.IsKeyboardFocused;
            startupBusy = true;
            startupMessage = null;
            UpdateStartupUi();
            string exe = ExePath;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string failure = null;
                try
                {
                    SchtasksResult r = want ? StartupTask.Enable(exe) : StartupTask.Disable();
                    if (!r.Success) failure = FirstLine(r.StdErr, "schtasks exit code " + r.ExitCode.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex) { failure = AppText.Plain(ex.Message, "unknown error"); }
                StartupTaskStatus status = ReadStartupStatus();
                Post(() => FinishStartupChange(status, failure == null ? null : "Could not change the startup task: " + failure));
            });
        }

        void FinishStartupChange(StartupTaskStatus status, string message)
        {
            startupBusy = false;
            startupStatus = status;
            startupMessage = message;
            bool on = status != null && status.State == StartupTaskState.Ours;
            if (settings.StartWithWindows != on)
            {
                settings.StartWithWindows = on;
                SaveAndApply();
            }
            UpdateStartupUi();
            if (refocusStartup && startupCheck.IsEnabled) { refocusStartup = false; startupCheck.Focus(); }
        }

        void UpdateStartupUi()
        {
            if (startupCheck == null) return;
            bool wasSyncing = syncing;
            syncing = true;
            startupCheck.IsChecked = startupStatus != null && startupStatus.State == StartupTaskState.Ours;
            syncing = wasSyncing;
            startupCheck.IsEnabled = AppText.StartupSwitchEnabled(elevated, startupBusy, otherAccount);
            string reason = AppText.StartupReason(startupStatus, elevated, otherAccount, startupMessage);
            startupReason.Text = reason ?? "";
            startupReason.Visibility = string.IsNullOrEmpty(reason) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(startupCheck, reason ?? "");
            // U21: disabled, the switch still shows its reason on hover (ShowOnDisabled in MainWindow.xaml).
            startupCheck.ToolTip = AppText.DisabledTip(startupCheck.IsEnabled, reason);
        }

        static string FirstLine(string text, string fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            foreach (string line in text.Split('\r', '\n'))
                if (line.Trim().Length > 0) return line.Trim();
            return fallback;
        }

        // ------------------------------------------------------------------ theme and tray

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // Raised on the SystemEvents thread; WPF refreshes SystemParameters on its own broadcast handler,
            // so the re-read runs later, at background priority. Wallpaper, power, locale and the like change no
            // colour: they are ignored, so the tokens and window frame are not re-applied for nothing.
            if (exiting || !ThemeResolver.AffectsTheme(e.Category)) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ReapplyTheme));
        }

        void OnSystemParameterChanged(object sender, PropertyChangedEventArgs e)
        {
            if (exiting || e.PropertyName != "HighContrast") return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ReapplyTheme));
        }

        void ReapplyTheme()
        {
            if (exiting) return;
            theme.Apply(settings.Theme);
            theme.ApplyWindowFrame(window);
            // Mica follows the theme (dark Mica under dark mode) and steps aside under high contrast.
            ApplyBackdrop();
            SyncMica();
            taskbarLight = theme.TaskbarIsLight();
            ShowThemeNote();
            UpdateTray(); // also repaints the chart, whose colours are tokens read at paint time
        }

        /// <summary>Under high contrast the Theme box's choice has no effect: a line under it says so.</summary>
        void ShowThemeNote()
        {
            string note = ThemeResolver.ThemeNote(theme.Current == ResolvedTheme.HighContrast);
            themeNote.Text = note ?? "";
            themeNote.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(themeBox, note ?? "");
        }

        void UpdateTray()
        {
            if (tray == null) return;
            TrayMenuState menuState = TrayMenuState.For(elevated, cleaningUi, settings.AutoCleanEnabled, memView.State != MemoryViewState.FailedNoData);
            AutoCleanState auto = AppText.AutoState(settings.AutoCleanEnabled, elevated, autoInfo);
            CultureInfo culture = CultureInfo.CurrentCulture;
            DateTime localNow = DateTime.Now;
            menuState.AutoText = AppText.TrayAutoLabel(auto, autoInfo, culture);
            trayAutoKey = auto + "|" + menuState.AutoText;
            tray.SetMenuState(menuState, syncState == SettingsSyncState.SaveFailed, AppText.CanPause(auto), auto == AutoCleanState.Paused);
            // A shortcut press ignored by the 60 s floor says so for a minute, instead of the last clean's line.
            string skipped = HotkeyGate.SkipLine(hotkeySkippedUtc, service == null ? null : service.LastClean, DateTime.UtcNow);
            tray.SetStatusLine(skipped ?? TrayStatus.MenuLine(lastClean, localNow, culture));
            tray.SetCleanShortcut(hotkey == null || hotkey.Active == null ? null : hotkey.Active.Format());
            tray.Update(lastSnapshot, settings.ShowTrayPercent, taskbarLight, TrayStatus.TooltipLines(auto, autoInfo, lastClean, localNow, culture));
            RefreshVisuals();
        }

        /// <summary>The tray's status item: the window, on the Memory tab, where the result line and its areas are.</summary>
        void ShowMemoryTab()
        {
            ShowWindow();
            if (exiting) return;
            memoryTab.IsSelected = true;
        }

        // ------------------------------------------------------------------ status lines read out (U9)

        /// <summary>
        /// Shows a status line in its live region and reads it out once: the same text again (a sample error on every
        /// tick, a save that keeps failing) is not repeated until the line has been hidden (AnnounceGate).
        /// </summary>
        void ShowStatus(LiveRegion region, TextBlock text, string message)
        {
            if (region == null) return;
            if (text != null) text.Text = message ?? "";
            region.Visibility = Visibility.Visible;
            Announce(region, message);
        }

        void HideStatus(LiveRegion region)
        {
            if (region == null) return;
            region.Visibility = Visibility.Collapsed;
            announced.Remove(region);
        }

        /// <summary>Names the region and reads it out after layout, unless that text was the last one it read.</summary>
        void Announce(LiveRegion region, string message)
        {
            Announce(region, message, message, true);
        }

        /// <summary>
        /// Names the region; key decides whether this is news (AnnounceGate). speak false records the key without
        /// reading it out, so the same news is not read later when its view comes to the front.
        /// </summary>
        void Announce(LiveRegion region, string name, string key, bool speak)
        {
            if (region == null) return;
            AutomationProperties.SetName(region, name ?? "");
            string last;
            if (!AnnounceGate.ShouldAnnounce(announced.TryGetValue(region, out last) ? last : null, key)) return;
            announced[region] = key;
            if (!speak) return;
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!exiting) region.Announce(); }));
        }

        /// <summary>Typing in the search box reads the count once the user pauses (300 ms), not on every key.</summary>
        void RestartSearchAnnounce()
        {
            if (searchAnnounceTimer == null) return;
            searchAnnounceTimer.Stop();
            searchAnnounceTimer.Start();
        }

        /// <summary>The count, or the empty state when nothing matches; each pause in typing reads the current one.</summary>
        void AnnounceSearch()
        {
            if (exiting) return;
            LiveRegion region = null;
            if (searchShown == 0 && startupEmptyRegion.Visibility == Visibility.Visible) region = startupEmptyRegion;
            else if (searchCountRegion.Visibility == Visibility.Visible) region = searchCountRegion;
            if (region != null) region.Announce();
        }

        // ------------------------------------------------------------------ problem notices and shortcut (R6, R7)

        /// <summary>The message-only window for the shortcut; null (logged) when Windows will not make one.</summary>
        HotkeyListener CreateHotkeyListener()
        {
            try
            {
                var l = new HotkeyListener();
                l.Pressed += delegate { OnHotkey(); };
                return l;
            }
            catch (Exception ex)
            {
                ErrorLog.TryWrite(ErrorLog.DefaultPath, "could not create the shortcut window", ex);
                return null;
            }
        }

        /// <summary>Registers the saved shortcut (start, reload, reset) and shows its row.</summary>
        void StartHotkey()
        {
            if (hotkey == null) return;
            HotkeyGesture g;
            if (!HotkeyGesture.TryParse(settings.CleanHotkey, out g)) g = null;
            hotkey.Start(g);
            ShowHotkey();
            if (hotkey.Message != null) ShowStatus(hotkeyMessageRegion, hotkeyMessage, hotkey.Message);
            else HideStatus(hotkeyMessageRegion);
        }

        /// <summary>Exit: the shortcut goes back to Windows and the window is destroyed. Safe to call twice.</summary>
        void StopHotkey()
        {
            if (hotkey != null) hotkey.Stop();
            if (hotkeyListener == null) return;
            HotkeyListener l = hotkeyListener;
            hotkeyListener = null;
            l.Dispose();
        }

        /// <summary>Focus left the shortcut box: the saved shortcut is registered again, and a refusal is shown.</summary>
        void ResumeHotkey()
        {
            if (hotkey == null) return;
            hotkey.Resume();
            ShowHotkey();
            if (hotkey.Message != null) ShowStatus(hotkeyMessageRegion, hotkeyMessage, hotkey.Message);
        }

        /// <summary>The row shows the saved shortcut ("None" when empty, from the box's Tag), its note and the Remove link.</summary>
        void ShowHotkey()
        {
            if (hotkeyBox == null || hotkey == null) return;
            string keys = hotkey.Current == null ? "" : hotkey.Current.Format();
            hotkeyBox.Text = keys;
            AutomationProperties.SetItemStatus(hotkeyBox, keys.Length == 0 ? "None" : keys);
            hotkeyRemove.Visibility = keys.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            hotkeyNote.Text = AppText.HotkeyRowNote(elevated);
            UpdateCleanButton();
            UpdateTray();
        }

        /// <summary>The recorder: a combination held down is the new shortcut; Delete removes it, Esc leaves it.</summary>
        void OnHotkeyBoxKey(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.ImeProcessed || key == Key.DeadCharProcessed || key == Key.None) return;
            ModifierKeys m = Keyboard.Modifiers;
            HotkeyGesture g;
            HotkeyKeyAction action = HotkeyRecorder.Decide((m & ModifierKeys.Control) != 0, (m & ModifierKeys.Alt) != 0,
                (m & ModifierKeys.Shift) != 0, (m & ModifierKeys.Windows) != 0, KeyInterop.VirtualKeyFromKey(key), out g);
            switch (action)
            {
                case HotkeyKeyAction.PassThrough:
                    return;
                case HotkeyKeyAction.Ignore:
                    break;
                case HotkeyKeyAction.Cancel:
                    ShowHotkey();
                    if (hotkey.Message == null) HideStatus(hotkeyMessageRegion);
                    break;
                case HotkeyKeyAction.Invalid:
                    ShowStatus(hotkeyMessageRegion, hotkeyMessage, AppText.HotkeyInvalid);
                    break;
                case HotkeyKeyAction.Record:
                    ApplyHotkeyResult(hotkey.Record(g));
                    break;
                case HotkeyKeyAction.Remove:
                    ApplyHotkeyResult(hotkey.Remove());
                    break;
            }
            e.Handled = true;
        }

        /// <summary>Saves the shortcut when the binding says so, then shows the row and its message (or hides it).</summary>
        void ApplyHotkeyResult(HotkeyResult r)
        {
            if (r == null) return;
            if (r.Save)
            {
                settings.CleanHotkey = hotkey.Current == null ? "" : hotkey.Current.Format();
                SaveAndApply();
            }
            ShowHotkey();
            string message = r.Message ?? hotkey.Message;
            if (message != null)
            {
                // The same refusal twice in a row is read out again: the user pressed the keys again.
                announced.Remove(hotkeyMessageRegion);
                ShowStatus(hotkeyMessageRegion, hotkeyMessage, message);
            }
            else HideStatus(hotkeyMessageRegion);
        }

        /// <summary>
        /// A press of the shortcut (UI thread). Any program can send keys, so presses within a minute of the last clean
        /// are ignored, logged and shown in the tray; otherwise it is the Clean memory button.
        /// </summary>
        void OnHotkey()
        {
            if (exiting || !elevated || service == null || hotkey == null || hotkey.Active == null) return;
            DateTime now = DateTime.UtcNow;
            if (!HotkeyGate.Allow(HotkeyGate.Latest(service.LastClean, hotkeyCleanUtc), now, HotkeyGate.Floor))
            {
                hotkeySkippedUtc = now;
                // One record a minute at most, so a program sending the keys in a loop cannot push real cleans out of
                // cleans.log; the tray line follows every press.
                if (HotkeyGate.ShouldLogSkip(hotkeySkipLoggedUtc, now))
                {
                    hotkeySkipLoggedUtc = now;
                    AddActivity(AppText.ShortcutSkippedEvent());
                }
                UpdateTray();
                return;
            }
            HotkeyGesture used = hotkey.Active;
            if (!TryRequestClean()) return;
            hotkeyCleanUtc = now;
            hotkeySkippedUtc = null;
            hotkeySkipLoggedUtc = null;
            AddActivity(AppText.ShortcutCleanEvent(used));
        }

        // ------------------------------------------------------------------ reset (P16)

        /// <summary>The inline confirmation under Reset; focus goes to Cancel, the safe choice.</summary>
        void ShowResetConfirm(bool on)
        {
            if (resetConfirm == null) return;
            resetConfirm.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on)
            {
                HideStatus(resetStatusRegion);
                dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (resetConfirm.IsVisible) resetNo.Focus(); }));
            }
        }

        /// <summary>
        /// Every setting back to its default (auto-clean off, which also ends a pause; no shortcut), except Start with
        /// Windows, whose logon task is its own record. Services Power Clean stopped stay stopped, and the line says so.
        /// </summary>
        void ResetSettings()
        {
            ShowResetConfirm(false);
            // A slider value still waiting for its save goes in now, so it cannot land on top of the reset later.
            FlushThreshold();
            bool stopped = ServiceTrimmer.StoppedThisSession().Count > 0;
            AppSettings old = settings.Clone();
            settings = AppSettings.ResetDefaults(settings);
            // ApplySettings diffs against the settings the reset replaced, so it re-applies the theme, rebuilds the
            // Power Clean service rows and registers the (now empty) shortcut again.
            bool saved = SaveAndApply(old);
            SyncSettingsUi(true);
            moreAreas.IsExpanded = false;
            // A failed save leaves the SaveError line as the only message: the app runs on the defaults until it
            // closes, and the reset is not logged as done.
            if (saved)
            {
                AddActivity(AppText.ResetEvent());
                announced.Remove(resetStatusRegion);
                ShowStatus(resetStatusRegion, resetStatus, AppText.ResetDone(stopped));
            }
            resetButton.Focus();
        }

        // ------------------------------------------------------------------ activity (P7, R4)

        /// <summary>
        /// The app's one writer to cleans.log, in the data folder of the account the app runs as. Its writes never
        /// overlap, events keep their order, and nothing throws. A write may wait about 150 ms for ramopt, so it runs
        /// off the UI thread: WriteNow from the clean and tick threads, Post (thread pool) from the UI.
        /// </summary>
        static readonly SerialLog cleanLog = new SerialLog(line =>
        {
            string folder = AppPaths.DataFolder;
            if (folder != null) CleanLog.TryAppend(folder, line);
        }, work => ThreadPool.QueueUserWorkItem(delegate { work(); }));

        static void AppendLog(string line)
        {
            cleanLog.WriteNow(line);
        }

        /// <summary>Adds an event to Recent activity and, off the UI thread, to cleans.log.</summary>
        void AddActivity(ActivityEvent e)
        {
            if (e == null) return;
            DateTime now = DateTime.Now;
            activity.Add(ActivityLog.Row(now, now, e.Text, CultureInfo.CurrentCulture));
            cleanLog.Post(ActivityLog.EventRecord(DateTime.UtcNow, e));
            ShowActivity();
        }

        void ShowActivity()
        {
            IList<string> rows = activity.Newest();
            activityRows.Children.Clear();
            if (rows.Count == 0) activityRows.Children.Add(ActivityRow("Nothing yet this session.", true));
            foreach (string row in rows) activityRows.Children.Add(ActivityRow(row, false));
        }

        /// <summary>Recent cleans, from the service's in-memory history (with the load five minutes later once read).</summary>
        void ShowHistory()
        {
            if (service == null) return;
            List<CleanHistoryEntry> history = service.History;
            recentCleanRows.Children.Clear();
            if (history.Count == 0) recentCleanRows.Children.Add(ActivityRow("No cleans yet this session.", true));
            DateTime now = DateTime.Now;
            foreach (CleanHistoryEntry e in history)
                recentCleanRows.Children.Add(ActivityRow(ActivityLog.CleanRow(e, now, CultureInfo.CurrentCulture), false));
        }

        /// <summary>A wrapping row: the time muted, two spaces, then the text; the whole row is its accessible name.</summary>
        static TextBlock ActivityRow(string row, bool empty)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 3, 0, 3) };
            AutomationProperties.SetName(text, row);
            int gap = empty ? -1 : row.IndexOf("  ", StringComparison.Ordinal);
            if (gap <= 0)
            {
                text.Text = row;
                text.SetResourceReference(TextBlock.ForegroundProperty, empty ? "MutedText" : "Text");
                return text;
            }
            var time = new Run(row.Substring(0, gap + 2));
            time.SetResourceReference(TextElement.ForegroundProperty, "MutedText");
            text.Inlines.Add(time);
            text.Inlines.Add(new Run(row.Substring(gap + 2)));
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            return text;
        }
    }
}
