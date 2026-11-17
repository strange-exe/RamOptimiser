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
    internal sealed class AppShell
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
        Border sampleError;
        StackPanel adminLine, topList;
        TextBlock sampleErrorText, restartError, elevationText, listsNote, freeLabel, powerNote, accountNotice, lastReadNote;
        SummaryPanel loadCard, rowInUse, rowModified, rowStandby, rowFree, commitRow;
        Run loadNumber, inUseRun;
        MapPanel mapBar;
        Border[] segments;
        Border commitUsed, commitRest;
        TextBlock commitValue;
        Button cleanButton, restartButton;
        LiveRegion resultPanel;
        Path resultGlyph;
        TextBlock resultText;
        Expander resultDetails;
        StackPanel resultRows;
        TextBlock valInUse, valModified, valStandby, valFree;
        TextBlock autoDescription, autoPaused;
        CheckBox autoSwitch;

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
        StackPanel startupList, startupLoadingPanel, startupEmpty;
        Border startupCard;
        RadioButton showApps, showServices;
        Run appsCount, servicesCount;
        TextBox startupSearch;
        ComboBox verdictBox, sortBox;
        List<StartupApp> startupApps;
        List<StartupService> startupServices;
        /// <summary>Loading, ready, or applying a change (and the reload after it): switches work only when ready.</summary>
        readonly StartupFlow startupFlow = new StartupFlow();
        /// <summary>Errors shown under the row that failed, by row key, until dismissed or the row changes again.</summary>
        readonly Dictionary<string, KeyValuePair<string, string>> rowErrors = new Dictionary<string, KeyValuePair<string, string>>();
        /// <summary>Row errors already read out, so a rebuild (each search keystroke) does not repeat them.</summary>
        readonly AnnouncedErrors announcedErrors = new AnnouncedErrors();
        /// <summary>Tag of the control to focus after the list is rebuilt (a row's switch, by key).</summary>
        string refocusTag;

        // Settings tab
        CheckBox settingsAutoSwitch, startupCheck, closeToTrayCheck, trayPercentCheck;
        TextBlock saveError, cooldownText, areaReason, startupReason, thresholdValue, themeNote;
        Slider thresholdSlider;
        DispatcherTimer thresholdTimer;
        /// <summary>A slider value waiting for its 400 ms pause; flushed on exit and before an elevated restart.</summary>
        readonly PendingSave thresholdPending = new PendingSave();
        ComboBox intervalBox, themeBox;
        Expander moreAreas;
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
            WireMain();
            WireStartup();
            WireSettings();
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
            tray.AutoCleanToggled += delegate { SetAutoClean(!settings.AutoCleanEnabled); };
            tray.RestartRequested += delegate { RestartElevated(); };
            tray.ExitRequested += delegate { Exit(); };
            UpdateTray();

            powerServices = new List<string>(settings.PowerServices);
            // Services are stopped only when the areas include Areas.Services, i.e. under Power Clean.
            service = new AutoCleanService(MemorySnapshot.Take, areas => Cleaner.Clean(areas, powerServices), () => DateTime.UtcNow);
            service.Sampled += s => Post(() => OnSampled(s));
            service.CleanStarted += r => Post(() => OnCleanStarted(r));
            service.CleanCompleted += (report, r) => Post(() => OnCleanCompleted(report, r));
            service.Failed += (ex, source) => Post(() => OnServiceError(ex, source));
            service.Update(ServiceSettings());
            // Before service.Start, so a Power Clean of this run is never mistaken for an earlier run's leftover.
            if (elevated) StartServiceRecovery();

            if (instance != null) instance.ActivationRequested += OnActivationRequested;
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
            if (exiting) return;
            // Before anything is torn down: a threshold set in the last 400 ms is saved, not dropped.
            try { FlushThreshold(); }
            catch (Exception ex) { ErrorLog.TryWrite(ErrorLog.DefaultPath, "could not save the threshold on exit", ex); }
            // A prompt still up when the user exits: its result arrives later and is ignored.
            elevationState = ElevationFlow.Next(elevationState, ElevationEvent.ExitRequested).State;
            exiting = true;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            if (service != null) service.Dispose();
            if (tray != null) tray.Dispose();
            if (instance != null) { instance.ActivationRequested -= OnActivationRequested; instance.Dispose(); instance = null; }
            if (window != null) window.Close();
            RestoreServicesOnExit(service, elevated);
            app.Shutdown();
        }

        /// <summary>
        /// Services Power Clean stopped are this session's doing, so leaving the app starts them again. A foreground
        /// thread keeps the process alive until Restore finishes (at most 10 s per service) after the window has gone.
        /// A Power Clean still in flight is waited for first (bounded), so a stop it makes after Exit is restored too;
        /// if the thread is cut short anyway, the journal lets the next elevated start finish the job. A memory-only
        /// clean, or any clean without administrator rights, stops no services and does not hold the exit up.
        /// </summary>
        static void RestoreServicesOnExit(AutoCleanService cleaner, bool elevated)
        {
            bool cleaning = cleaner != null && cleaner.IsCleaning;
            bool stopsServices = elevated && cleaner != null && cleaner.CleanMayStopServices;
            if (!PowerCleanRecovery.NeedsExitRestore(ServiceTrimmer.StoppedThisSession().Count, cleaning, stopsServices)) return;
            var t = new Thread(() =>
            {
                try
                {
                    if (cleaner != null) cleaner.WaitUntilIdle(PowerCleanRecovery.ExitCleanWait);
                    ServiceTrimmer.Restore();
                }
                catch (Exception) { }
            }) { IsBackground = false, Name = "restore-services" };
            t.Start();
        }

        /// <summary>
        /// Hooks Power Clean up to its journal, then starts again the services an earlier run stopped and never
        /// restored (it crashed, was ended, or Windows signed out first). The names are read here, before auto-clean
        /// starts; the restore runs off the UI thread and reports in the last-clean line.
        /// </summary>
        void StartServiceRecovery()
        {
            var journal = new StoppedServiceStore(StoppedServiceStore.DefaultPath);
            List<string> leftover = journal.Read();
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

        /// <summary>Last-resort cleanup from the fatal-error path; never throws.</summary>
        public void EmergencyCleanup()
        {
            try { if (service != null) service.Dispose(); } catch (Exception) { }
            try { if (tray != null) tray.Dispose(); } catch (Exception) { }
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
            sampleError = F<Border>("SampleError");
            sampleErrorText = F<TextBlock>("SampleErrorText");
            restartError = F<TextBlock>("RestartError");
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
            autoPaused = F<TextBlock>("AutoPaused");
            autoSwitch = F<CheckBox>("AutoSwitch");

            startupView = F<ScrollViewer>("StartupView");
            startupSummary = F<TextBlock>("StartupSummary");
            startupNote = F<TextBlock>("StartupNote");
            startupError = F<TextBlock>("StartupError");
            startupList = F<StackPanel>("StartupList");
            startupCard = F<Border>("StartupCard");
            startupLoadingPanel = F<StackPanel>("StartupLoading");
            startupEmpty = F<StackPanel>("StartupEmpty");
            startupEmptyTitle = F<TextBlock>("StartupEmptyTitle");
            startupEmptyText = F<TextBlock>("StartupEmptyText");
            showApps = F<RadioButton>("ShowAppsButton");
            showServices = F<RadioButton>("ShowServicesButton");
            appsCount = F<Run>("AppsCount");
            servicesCount = F<Run>("ServicesCount");
            startupSearch = F<TextBox>("StartupSearch");
            verdictBox = F<ComboBox>("VerdictBox");
            sortBox = F<ComboBox>("SortBox");

            saveError = F<TextBlock>("SaveError");
            settingsAutoSwitch = F<CheckBox>("SettingsAutoSwitch");
            cooldownText = F<TextBlock>("CooldownText");
            thresholdSlider = F<Slider>("ThresholdSlider");
            thresholdValue = F<TextBlock>("ThresholdValue");
            intervalBox = F<ComboBox>("IntervalBox");
            areaReason = F<TextBlock>("AreaReason");
            moreAreas = F<Expander>("MoreAreas");
            settingsPowerSwitch = F<CheckBox>("SettingsPowerSwitch");
            powerList = F<StackPanel>("PowerList");
            powerServicesList = F<Expander>("PowerServices");
            restoreButton = F<Button>("RestoreButton");
            powerStatus = F<TextBlock>("PowerStatus");
            themeBox = F<ComboBox>("ThemeBox");
            themeNote = F<TextBlock>("ThemeNote");
            startupCheck = F<CheckBox>("StartupCheck");
            startupReason = F<TextBlock>("StartupReason");
            closeToTrayCheck = F<CheckBox>("CloseToTrayCheck");
            trayPercentCheck = F<CheckBox>("TrayPercentCheck");

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
            startupSearch.TextChanged += delegate { BuildStartupList(); };
            foreach (VerdictFilter f in StartupFilter.Filters)
                verdictBox.Items.Add(new ComboBoxItem { Content = StartupFilter.Label(f, 0), Tag = f });
            verdictBox.SelectedIndex = 0;
            sortBox.Items.Add(new ComboBoxItem { Content = "Verdict", Tag = StartupSort.Verdict });
            sortBox.Items.Add(new ComboBoxItem { Content = "Name", Tag = StartupSort.Name });
            sortBox.SelectedIndex = 0;
            verdictBox.SelectionChanged += delegate { if (!syncing) BuildStartupList(); };
            sortBox.SelectionChanged += delegate { if (!syncing) BuildStartupList(); };
        }

        void WireSettings()
        {
            OnToggle(settingsAutoSwitch, SetAutoClean);
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
                    settings.Areas = AreaRules.Toggle(settings.Areas, area, on);
                    SaveAndApply();
                });
            }
            OnToggle(startupCheck, ToggleStartup);
            OnToggle(closeToTrayCheck, on => { settings.CloseToTray = on; SaveAndApply(); });
            OnToggle(trayPercentCheck, on => { settings.ShowTrayPercent = on; SaveAndApply(); });
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
                try { apps = StartupApps.Query(); }
                catch (Exception ex) { failure = AppText.Plain(ex.Message, "unknown error"); }
                try { services = StartupServices.Query(); }
                catch (Exception ex) { failure = failure ?? AppText.Plain(ex.Message, "unknown error"); }
                Post(() =>
                {
                    startupFlow.Loaded();
                    startupApps = apps ?? new List<StartupApp>();
                    startupServices = services ?? new List<StartupService>();
                    if (failure != null)
                    {
                        startupError.Text = "Some entries could not be read: " + failure + ".";
                        startupError.Visibility = Visibility.Visible;
                    }
                    else startupError.Visibility = Visibility.Collapsed;
                    BuildStartupList();
                });
            });
        }

        /// <summary>One row of the Startup list, apps and services alike.</summary>
        sealed class StartupItem
        {
            public string Key, Label, Name, Detail, LockText;
            public Geometry LockGlyph;
            public Verdict Verdict;
            public bool IsService, On, ByApp;
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
                    var it = new StartupItem
                    {
                        Key = "app|" + a.Name, Name = a.Name, IsService = false, On = startupFlow.ShowsOn("app|" + a.Name, a.Enabled),
                        Verdict = a.Known == null ? Verdict.Unknown : a.Known.Verdict,
                        Label = a.Known != null ? a.Known.Label : a.Name, Detail = AppText.AppDetail(a)
                    };
                    if (it.Verdict == Verdict.Keep) { it.LockText = "Left on"; it.LockGlyph = Icon("IconShield"); }
                    else if (a.NeedsAdmin && !elevated) { it.LockText = "Needs admin"; it.LockGlyph = Icon("IconLock"); }
                    it.Apply = on => SetAppEnabled(target, it, on);
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
                startupEmpty.Visibility = Visibility.Collapsed;
                return;
            }
            startupLoadingPanel.Visibility = Visibility.Collapsed;

            int appsOn = 0, servicesOn = 0, safeOn = 0;
            foreach (StartupApp a in startupApps)
                if (a.Enabled) { appsOn++; if (a.Known != null && a.Known.Verdict == Verdict.Safe) safeOn++; }
            foreach (StartupService s in startupServices)
                if (s.CanDisable && s.Known != null)
                {
                    servicesOn++;
                    if (s.Known.Verdict == Verdict.Safe) safeOn++;
                }
            // While a change applies its line stays, whatever rebuilds the list (search, filter, sort).
            startupSummary.Text = startupFlow.BusyLine ?? AppText.StartupSummary(appsOn, servicesOn, safeOn);
            appsCount.Text = "  " + startupApps.Count.ToString(CultureInfo.InvariantCulture);
            servicesCount.Text = "  " + startupServices.Count.ToString(CultureInfo.InvariantCulture);
            AutomationProperties.SetName(showApps, "Startup apps, " + startupApps.Count.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(showServices, "Services, " + startupServices.Count.ToString(CultureInfo.InvariantCulture));

            bool apps = showApps.IsChecked == true;
            startupNote.Text = apps ? AppText.StartupAppsNote : AppText.StartupServicesNote;
            if (!elevated) startupNote.Text += " " + (apps ? "Apps for every user need administrator rights." : AppText.ServicesNeedAdmin);

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
            startupEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (empty)
            {
                if (all.Count == 0)
                {
                    startupEmptyTitle.Text = apps ? "No apps start with Windows" : "No listed services found";
                    startupEmptyText.Text = apps ? "Apps that open at sign-in will show here." : "Services in the app's list will show here.";
                }
                else
                {
                    startupEmptyTitle.Text = "Nothing matches";
                    startupEmptyText.Text = "Try another name, or show all verdicts.";
                }
            }

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
            string verdict = it.Verdict == Verdict.Unknown && it.IsService ? "Not in the app's list" : AppText.VerdictText(it.Verdict, it.IsService);

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
            TextBlock detail = SmallText(it.Detail + ".");
            detail.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(detail);
            grid.Children.Add(text);

            string switchName = it.Label + ", starts with Windows" + (it.ByApp ? ". Turned off by this app" : "");
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
                AutomationProperties.SetName(lockedRow, AppText.LockedRowName(it.Label, it.LockText, it.On));
                Grid.SetColumn(lockedRow, 1);
                grid.Children.Add(lockedRow);
            }

            KeyValuePair<string, string> error;
            if (rowErrors.TryGetValue(it.Key, out error)) stack.Children.Add(ErrorBar(it.Key, error.Key, error.Value));
            return row;
        }

        /// <summary>The error under the row that failed: icon, title, reason and a dismiss button; announced politely.</summary>
        UIElement ErrorBar(string key, string title, string message)
        {
            var bar = new LiveRegion { Margin = new Thickness(12, 0, 12, 10) };
            bar.SetResourceReference(FrameworkElement.StyleProperty, "InfoBar");
            AutomationProperties.SetLiveSetting(bar, AutomationLiveSetting.Polite);
            AutomationProperties.SetName(bar, title + ". " + message);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Path icon = IconPath("IconError", "Danger");
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 1, 8, 0);
            grid.Children.Add(icon);
            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            words.Children.Add(new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(words, 1);
            grid.Children.Add(words);
            var dismiss = new Button { Content = IconPath("IconClose", "Text"), Tag = key + "|err", Margin = new Thickness(4, -4, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            dismiss.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButton");
            AutomationProperties.SetName(dismiss, "Dismiss error");
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
            bar.Loaded += delegate { if (announcedErrors.ShouldAnnounce(key, title + ". " + message)) bar.Announce(); };
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
            if (failure != null)
                rowErrors[it.Key] = new KeyValuePair<string, string>(AppText.ChangeFailedTitle(it.Label, on), AppText.ChangeFailedText(failure, on));
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
                catch (Exception ex) { r = new StartupChange { Ok = false, Message = AppText.Plain(ex.Message, "unknown error") }; }
                Post(() =>
                {
                    if (!r.Ok) rowErrors[it.Key] = new KeyValuePair<string, string>(AppText.ChangeFailedTitle(it.Label, on), AppText.ChangeFailedText(r.Message, on));
                    QueryStartup(); // still Applying: Loaded() after the reload makes the switches work again
                });
            });
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
            restartError.Visibility = Visibility.Collapsed;
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
                    restartError.Text = AppText.RestartFailed(error == null ? null : error.Message);
                    restartError.Visibility = Visibility.Visible;
                    // Asked from the tray, the window may be hidden or on another tab: bring the error line into view.
                    memoryTab.IsSelected = true;
                    ShowWindow();
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
            MemoryViewState before = memView.State;
            memView.Sampled(DateTime.Now);
            if (before != MemoryViewState.Live)
            {
                lastReadNote.Visibility = Visibility.Collapsed;
                UpdateCleanButton();
            }
            RefreshResultLine();
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
            sampleError.Visibility = Visibility.Collapsed;
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
            AutomationProperties.SetName(commitRow, AppText.CommitName(s));
            mapReady = true;
            UpdateTray();
        }

        void SetRow(SummaryPanel row, TextBlock value, MapPart part, string label, MemoryMap map, bool animate)
        {
            ulong bytes = map.Bytes(part);
            SetFigure(value, bytes, animate);
            AutomationProperties.SetName(row, AppText.RowName(label, null, Format.Bytes(bytes)));
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
            memView.SampleFailed();
            sampleErrorText.Text = AppText.SampleErrorText(ex.Message, (int)SampleInterval.TotalSeconds, memView.HasData);
            sampleError.Visibility = Visibility.Visible;
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
                try { top = AppMemoryList.Top(TopAppsCount); }
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
            ulong max = Math.Max(1UL, top[0].Bytes);
            foreach (AppMemory a in top)
            {
                var row = new SummaryPanel { Padding = new Thickness(0, 3, 0, 3) };
                AutomationProperties.SetName(row, AppText.TopAppName(a));
                var stack = new StackPanel();
                var line = new DockPanel();
                var figure = new TextBlock { Text = Format.Bytes(a.Bytes) };
                figure.SetResourceReference(FrameworkElement.StyleProperty, "FigureText");
                DockPanel.SetDock(figure, Dock.Right);
                line.Children.Add(figure);
                var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                name.Inlines.Add(new Run(a.Name));
                if (a.Processes > 1)
                {
                    var count = new Run("  " + a.Processes.ToString(CultureInfo.InvariantCulture) + " processes") { FontSize = 12 };
                    count.SetResourceReference(TextElement.ForegroundProperty, "MutedText");
                    name.Inlines.Add(count);
                }
                line.Children.Add(name);
                stack.Children.Add(line);

                double share = (double)a.Bytes / max;
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

        // ------------------------------------------------------------------ cleaning

        void RequestClean()
        {
            if (exiting || !AppText.CleanEnabled(elevated, cleaningUi, memView.State)) return;
            bool started;
            try { started = service.CleanNow(); }
            catch (ObjectDisposedException) { return; }
            if (started) SetCleaning(true);
        }

        void OnCleanStarted(CleanReason reason)
        {
            cleanError = null;
            SetCleaning(true);
        }

        void OnCleanCompleted(CleanReport report, CleanReason reason)
        {
            SetCleaning(false);
            ShowResult(report, cleanError, reason != CleanReason.Manual);
            cleanError = null;
            if (report != null && report.After != null) OnSampled(report.After);
            if (report != null && report.Services.Count > 0 && settingsTab.IsSelected) RefreshServices();
            UpdateRestoreUi();
        }

        void SetCleaning(bool on)
        {
            if (on && cleanButton.IsKeyboardFocused) refocusClean = true;
            cleaningUi = on;
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
            return r != null && r.Before != null && r.After != null ? line + " " + AppText.Summary(r) : line;
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
            AutomationProperties.SetName(row, line.Label + ": " + line.Status);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
                Grid.SetColumnSpan(status, 2);
            }
            grid.Children.Add(status);
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
            SaveAndApply();
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
                    RefreshServices();
                });
            });
        }

        void UpdateRestoreUi()
        {
            int stopped = ServiceTrimmer.StoppedThisSession().Count;
            restoreButton.IsEnabled = elevated && !servicesBusy && stopped > 0;
            powerStatus.Text = elevated ? AppText.RestoreStatus(stopped) : AppText.ServicesNeedAdmin;
        }

        /// <summary>
        /// P12: keys edited in settings.ini by hand since the last load or save are kept (three-way merge); the
        /// app's own change wins for the key it changed. A failed save keeps the baseline, so a later merge still
        /// knows what the app changed, and the app runs on the new values until it closes (and says so).
        /// </summary>
        void SaveAndApply()
        {
            AppSettings before = settings.Clone();
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
                saveError.Visibility = Visibility.Collapsed;
            }
            catch (IOException ex) { SaveFailed(ex); }
            catch (UnauthorizedAccessException ex) { SaveFailed(ex); }
            ApplySettings(before);
        }

        void SaveFailed(Exception ex)
        {
            syncState = SettingsSync.Next(syncState, SettingsSyncEvent.SaveFailed);
            ShowSaveError(ex);
        }

        void ShowSaveError(Exception ex)
        {
            saveError.Text = AppText.SaveFailedText(ex.Message);
            saveError.Visibility = Visibility.Visible;
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
            bool servicesChanged = string.Join(",", settings.PowerServices.ToArray()) != string.Join(",", before.PowerServices.ToArray());
            if (servicesChanged && powerRows.Count > 0) RefreshServices();
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

        void RefreshAutoRow()
        {
            autoDescription.Text = AppText.Sentence(AppText.AutoDescription(settings));
            autoPaused.Visibility = !elevated && settings.AutoCleanEnabled ? Visibility.Visible : Visibility.Collapsed;
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
                cooldownText.Text = AppText.Sentence(AppText.CooldownText(settings.CooldownSeconds));
                SelectByTag(intervalBox, settings.IntervalMinutes);
                SelectByTag(themeBox, settings.Theme);
                bool anyLocked = false;
                foreach (KeyValuePair<CheckBox, Areas> pair in areaBoxes)
                {
                    pair.Key.IsChecked = (settings.Areas & pair.Value) != 0;
                    bool locked = !AreaRules.CanUntick(settings.Areas, pair.Value);
                    pair.Key.IsEnabled = !locked;
                    string help = pair.Value == Areas.StandbyList ? AppText.StandbyHelp : "";
                    if (locked) help = (help + " " + AppText.LastAreaReason).Trim();
                    AutomationProperties.SetHelpText(pair.Key, help);
                    anyLocked |= locked;
                }
                areaReason.Visibility = anyLocked ? Visibility.Visible : Visibility.Collapsed;
                closeToTrayCheck.IsChecked = settings.CloseToTray;
                trayPercentCheck.IsChecked = settings.ShowTrayPercent;
            }
            finally { syncing = false; }
            UpdateStartupUi();
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
            taskbarLight = theme.TaskbarIsLight();
            ShowThemeNote();
            UpdateTray();
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
            tray.SetMenuState(TrayMenuState.For(elevated, cleaningUi, settings.AutoCleanEnabled, memView.State != MemoryViewState.FailedNoData),
                syncState == SettingsSyncState.SaveFailed);
            tray.Update(lastSnapshot, settings.ShowTrayPercent, taskbarLight);
        }
    }
}
