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

        ThemeManager theme;
        Window window;
        TrayController tray;
        AutoCleanService service;

        MemorySnapshot lastSnapshot;
        Exception cleanError;
        bool exiting, cleaningUi, syncing, mapReady, taskbarLight;
        bool viewStale, wasShowing, resultShown;
        bool startupBusy, startupEnabled, refocusClean, refocusStartup;
        string startupMessage;
        int resultCount, sampleCount;
        bool topBusy;

        TabControl tabs;
        TabItem memoryTab, startupTab, settingsTab;

        // Memory tab
        ScrollViewer mainView;
        Border sampleError;
        StackPanel adminLine, topList;
        TextBlock sampleErrorText, restartError, elevationText, listsNote, freeLabel, powerNote;
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
        bool startupLoading;
        /// <summary>Errors shown under the row that failed, by row key, until dismissed or the row changes again.</summary>
        readonly Dictionary<string, KeyValuePair<string, string>> rowErrors = new Dictionary<string, KeyValuePair<string, string>>();
        /// <summary>Tag of the control to focus after the list is rebuilt (a row's switch, by key).</summary>
        string refocusTag;

        // Settings tab
        CheckBox settingsAutoSwitch, startupCheck, closeToTrayCheck, trayPercentCheck;
        TextBlock saveError, cooldownText, areaReason, startupReason, thresholdValue;
        Slider thresholdSlider;
        DispatcherTimer thresholdTimer;
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
            elevated = Elevation.IsElevated();
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
            // A chosen area behind the disclosure is shown, so the list never hides what will be cleaned.
            moreAreas.IsExpanded = (settings.Areas & HiddenAreas) != 0;
            RefreshAutoRow();

            tray = new TrayController();
            tray.OpenRequested += delegate { ShowWindow(); };
            tray.CleanRequested += delegate { RequestClean(); };
            tray.AutoCleanToggled += delegate { SetAutoClean(!settings.AutoCleanEnabled); };
            tray.ExitRequested += delegate { Exit(); };
            UpdateTray();

            powerServices = new List<string>(settings.PowerServices);
            // Services are stopped only when the areas include Areas.Services, i.e. under Power Clean.
            service = new AutoCleanService(MemorySnapshot.Take, areas => Cleaner.Clean(areas, powerServices), () => DateTime.UtcNow);
            service.Sampled += s => Post(() => OnSampled(s));
            service.CleanStarted += r => Post(() => OnCleanStarted(r));
            service.CleanCompleted += (report, r) => Post(() => OnCleanCompleted(report, r));
            service.Error += ex => Post(() => OnServiceError(ex));
            service.Update(ServiceSettings());

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
            exiting = true;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            if (thresholdTimer != null) thresholdTimer.Stop();
            if (service != null) service.Dispose();
            if (tray != null) tray.Dispose();
            if (instance != null) { instance.ActivationRequested -= OnActivationRequested; instance.Dispose(); instance = null; }
            if (window != null) window.Close();
            RestoreServicesOnExit();
            app.Shutdown();
        }

        /// <summary>
        /// Services Power Clean stopped are this session's doing, so leaving the app starts them again. A foreground
        /// thread keeps the process alive until Restore finishes (at most 10 s per service) after the window has gone.
        /// </summary>
        static void RestoreServicesOnExit()
        {
            if (ServiceTrimmer.StoppedThisSession().Count == 0) return;
            var t = new Thread(() => { try { ServiceTrimmer.Restore(); } catch (Exception) { } }) { IsBackground = false, Name = "restore-services" };
            t.Start();
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
            thresholdTimer.Tick += delegate { thresholdTimer.Stop(); SaveThreshold(); };
            thresholdSlider.ValueChanged += delegate
            {
                ShowThreshold();
                if (syncing) return;
                // Dragging changes the value many times a second: save once it rests.
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

        /// <summary>Reads apps (registry, fast) and services (WMI, about a second) off the UI thread.</summary>
        void LoadStartup()
        {
            if (startupLoading) return;
            startupLoading = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<StartupApp> apps = null; List<StartupService> services = null; string failure = null;
                try { apps = StartupApps.Query(); }
                catch (Exception ex) { failure = AppText.Plain(ex.Message, "unknown error"); }
                try { services = StartupServices.Query(); }
                catch (Exception ex) { failure = failure ?? AppText.Plain(ex.Message, "unknown error"); }
                Post(() =>
                {
                    startupLoading = false;
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
                        Key = "app|" + a.Name, Name = a.Name, IsService = false, On = a.Enabled,
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
                        On = s.CanDisable && !s.DisabledByApp,
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
            startupSummary.Text = AppText.StartupSummary(appsOn, servicesOn, safeOn);
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
                    if (target != null && target.IsVisible) target.Focus();
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
                OnToggle(box, it.Apply);
                box.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(box, 1);
                grid.Children.Add(box);
            }
            else
            {
                var state = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
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
                AutomationProperties.SetName(state, it.Label + ": " + it.LockText + (it.On ? ", starts with Windows" : ""));
                Grid.SetColumn(state, 1);
                grid.Children.Add(state);
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
                rowErrors.Remove(key);
                refocusTag = key + (startupList.IsKeyboardFocusWithin ? "|sw" : "");
                BuildStartupList();
            };
            Grid.SetColumn(dismiss, 2);
            grid.Children.Add(dismiss);
            bar.Child = grid;
            bar.Loaded += delegate { bar.Announce(); };
            return bar;
        }

        void SetAppEnabled(StartupApp a, StartupItem it, bool on)
        {
            rowErrors.Remove(it.Key);
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
            if (startupLoading) return;
            startupLoading = true;
            rowErrors.Remove(it.Key);
            refocusTag = it.Key + "|sw";
            startupSummary.Text = (on ? "Turning on " : "Turning off ") + it.Label + "...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                StartupChange r;
                try { r = on ? StartupServices.Reenable(svc.Name) : StartupServices.Disable(svc.Name); }
                catch (Exception ex) { r = new StartupChange { Ok = false, Message = AppText.Plain(ex.Message, "unknown error") }; }
                Post(() =>
                {
                    startupLoading = false;
                    if (!r.Ok) rowErrors[it.Key] = new KeyValuePair<string, string>(AppText.ChangeFailedTitle(it.Label, on), AppText.ChangeFailedText(r.Message, on));
                    LoadStartup();
                });
            });
        }

        // ------------------------------------------------------------------ elevation

        void ApplyElevationState()
        {
            adminLine.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
            elevationText.Text = AppText.AdminLine;
            cleanButton.IsEnabled = elevated && !cleaningUi;
            AutomationProperties.SetHelpText(cleanButton, elevated ? "" : AppText.CleanNeedsAdmin);
        }

        /// <summary>
        /// The single-instance lock is released first so the elevated copy can take it; on success this copy
        /// exits, and when the UAC prompt is cancelled it takes the lock back and carries on.
        /// </summary>
        void RestartElevated()
        {
            restartError.Visibility = Visibility.Collapsed;
            if (instance != null)
            {
                instance.ActivationRequested -= OnActivationRequested;
                instance.Dispose();
                instance = null;
            }
            bool started = false;
            try { started = Elevation.TryRelaunchElevated(ExePath, ""); }
            catch (Exception ex)
            {
                restartError.Text = "Could not restart as administrator: " + AppText.Plain(ex.Message, "unknown error") + ".";
                restartError.Visibility = Visibility.Visible;
            }
            if (started) { Exit(); return; }

            try
            {
                var again = new SingleInstance();
                if (again.IsFirstInstance)
                {
                    instance = again;
                    instance.ActivationRequested += OnActivationRequested;
                }
                else
                {
                    // Another copy took the lock while it was free: hand over to it.
                    again.SignalFirstInstance();
                    again.Dispose();
                    Exit();
                }
            }
            catch (Win32Exception) { } // keep running without the lock rather than not at all
        }

        // ------------------------------------------------------------------ sampling

        /// <summary>The window can be seen: shown and not minimized. Hidden in the tray, only the tray icon updates.</summary>
        bool WindowShowing { get { return window.IsVisible && window.WindowState != WindowState.Minimized; } }

        void OnSampled(MemorySnapshot s)
        {
            lastSnapshot = s;
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
            // The admin line stays one short line; its longer reason is the link's help text.
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

        void OnServiceError(Exception ex)
        {
            if (cleaningUi) { cleanError = ex; return; }
            sampleErrorText.Text = "Memory status could not be read: " + AppText.Plain(ex.Message, "unknown error")
                + ". Trying again every " + SampleInterval.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.";
            sampleError.Visibility = Visibility.Visible;
            if (lastSnapshot == null) UpdateTray();
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
            if (!elevated || cleaningUi || exiting) return;
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
            cleanButton.IsEnabled = elevated && !on;
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
            string line = AppText.LastClean(DateTime.Now, report, automatic, CultureInfo.CurrentCulture);
            if (report == null && error != null) line += " " + AppText.FailedSummary(error);
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

            // Screen readers get the in-use figures too, which the line leaves to the legend.
            string spoken = report != null && report.Before != null && report.After != null ? line + " " + AppText.Summary(report) : line;
            AutomationProperties.SetName(resultPanel, spoken);
            if (!resultShown) EnterResult();
            if (!automatic) dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => resultPanel.Announce()));
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

        static Geometry Freeze(Geometry g) { g.Freeze(); return g; }

        /// <summary>Glyph plus words: ok (check), failed: reason (cross), skipped: reason (dash).</summary>
        static UIElement BuildResultRow(AreaLine line, bool first)
        {
            string brush = line.State == AreaState.Ok ? "Ok" : line.State == AreaState.Failed ? "Danger" : "MutedText";
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
                Data = line.State == AreaState.Ok ? CheckGlyph : line.State == AreaState.Failed ? CrossGlyph : DashGlyph
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

        void SaveAndApply()
        {
            try
            {
                SettingsStore.Save(settings, settingsPath);
                saveError.Visibility = Visibility.Collapsed;
            }
            catch (IOException ex) { ShowSaveError(ex); }
            catch (UnauthorizedAccessException ex) { ShowSaveError(ex); }
            powerServices = ServiceCatalog.Filter(settings.PowerServices);
            if (service != null) service.Update(ServiceSettings());
            SyncSettingsUi(false);
            RefreshAutoRow();
            UpdateTray();
        }

        void ShowSaveError(Exception ex)
        {
            saveError.Text = "Settings could not be saved: " + AppText.Plain(ex.Message, "unknown error") + ".";
            saveError.Visibility = Visibility.Visible;
        }

        void RefreshAutoRow()
        {
            autoDescription.Text = AppText.Sentence(AppText.AutoDescription(settings));
            autoPaused.Visibility = !elevated && settings.AutoCleanEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        void SyncSettingsUi(bool includeThreshold)
        {
            syncing = true;
            try
            {
                autoSwitch.IsChecked = settings.AutoCleanEnabled;
                settingsAutoSwitch.IsChecked = settings.AutoCleanEnabled;
                settingsPowerSwitch.IsChecked = settings.PowerClean;
                string note = AppText.PowerNote(settings.PowerClean, ChosenServiceCount());
                powerNote.Text = note ?? "";
                powerNote.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
                AutomationProperties.SetHelpText(cleanButton, elevated ? (note ?? "") : AppText.CleanNeedsAdmin);
                SetCleanLabel();
                if (includeThreshold)
                {
                    thresholdTimer.Stop();
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
                bool on = false;
                try { on = StartupTask.IsEnabled(); }
                catch (Exception) { on = false; }
                Post(() => FinishStartupChange(on, null));
            });
        }

        void ToggleStartup(bool want)
        {
            if (!elevated || startupBusy) { UpdateStartupUi(); return; }
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
                bool on = false;
                try { on = StartupTask.IsEnabled(); }
                catch (Exception) { on = false; }
                Post(() => FinishStartupChange(on, failure == null ? null : "Could not change the startup task: " + failure));
            });
        }

        void FinishStartupChange(bool on, string message)
        {
            startupBusy = false;
            startupEnabled = on;
            startupMessage = message;
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
            startupCheck.IsChecked = startupEnabled;
            syncing = wasSyncing;
            startupCheck.IsEnabled = elevated && !startupBusy;
            string reason = !elevated ? AppText.StartupNeedsAdmin : startupMessage;
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
            // so the re-read runs later, at background priority.
            if (exiting) return;
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
            UpdateTray();
        }

        void UpdateTray()
        {
            if (tray == null) return;
            tray.SetMenuState(elevated, cleaningUi, settings.AutoCleanEnabled);
            tray.Update(lastSnapshot, settings.ShowTrayPercent, taskbarLight);
        }
    }
}
