using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RamOptimiser.Gui;

namespace RamOptimiser.App
{
    /// <summary>
    /// Package 7A in the shell: the load history chart, the per-part change after a clean, the clean progress line, the
    /// tray flyout and the Mica setting. Decisions come from Visuals.cs and AppText.Visuals.cs (tested); this wires them.
    /// </summary>
    internal sealed partial class AppShell
    {
        static readonly TimeSpan ProgressFade = TimeSpan.FromMilliseconds(150);
        static readonly TimeSpan SegmentPass = TimeSpan.FromMilliseconds(1600);
        const double SegmentShare = 0.4;

        readonly LoadHistory history = new LoadHistory();
        readonly CleanProgressView progressView = new CleanProgressView();
        string[] partChanges;
        int changeGeneration, progressGeneration;
        readonly TextBlock[] changeTexts = new TextBlock[4];
        TextBlock changeNote, chartFrom, progressText, micaNote;
        LoadChart historyChart;
        FrameworkElement chartAutoKey;
        LiveRegion cleanProgress;
        Border progressTrack, progressSegment, micaRow;
        CheckBox micaCheck;
        DispatcherTimer progressTimer;
        TrayFlyout flyout;

        void FindVisuals()
        {
            changeTexts[(int)MapPart.InUse] = F<TextBlock>("ChgInUse");
            changeTexts[(int)MapPart.Modified] = F<TextBlock>("ChgModified");
            changeTexts[(int)MapPart.Standby] = F<TextBlock>("ChgStandby");
            changeTexts[(int)MapPart.Free] = F<TextBlock>("ChgFree");
            changeNote = F<TextBlock>("ChangeNote");
            historyChart = F<LoadChart>("HistoryChart");
            chartAutoKey = F<FrameworkElement>("ChartAutoKey");
            chartFrom = F<TextBlock>("ChartFrom");
            cleanProgress = F<LiveRegion>("CleanProgress");
            progressTrack = F<Border>("ProgressTrack");
            progressSegment = F<Border>("ProgressSegment");
            progressText = F<TextBlock>("ProgressText");
            micaRow = F<Border>("MicaRow");
            micaCheck = F<CheckBox>("MicaCheck");
            micaNote = F<TextBlock>("MicaNote");
        }

        void WireVisuals()
        {
            // UseMica changes go through ApplySettings, which puts the backdrop on or takes it off. It diffs against the
            // settings handed in, so the snapshot is taken before the change (SaveAndApply() alone would see none).
            OnToggle(micaCheck, on =>
            {
                AppSettings before = settings.Clone();
                settings.UseMica = on;
                SaveAndApply(before);
            });
            progressTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = CleanProgressView.ShowAfter };
            progressTimer.Tick += delegate
            {
                progressTimer.Stop();
                if (progressView.BarVisible(DateTime.UtcNow)) ShowProgress();
            };
            progressTrack.SizeChanged += delegate { if (cleanProgress.Visibility == Visibility.Visible) MoveSegment(); };
        }

        /// <summary>After the tray icon exists: a left click opens the flyout, which acts through the shell's own paths.</summary>
        void CreateFlyout()
        {
            flyout = new TrayFlyout();
            flyout.CleanRequested += delegate { RequestClean(); };
            flyout.PauseRequested += delegate { PauseAutoClean(PauseOrigin.Tray); };
            flyout.ResumeRequested += delegate { ResumeAutoClean(); };
            flyout.OpenRequested += delegate { ShowMemoryTab(); };
            tray.FlyoutRequested += delegate { ToggleFlyout(); };
        }

        void ToggleFlyout()
        {
            if (exiting || flyout == null) return;
            System.Drawing.Point pointer = System.Windows.Forms.Control.MousePosition;
            UpdateFlyout(true);
            flyout.Toggle(pointer.X, pointer.Y);
        }

        void CloseFlyout()
        {
            if (flyout == null) return;
            TrayFlyout f = flyout;
            flyout = null;
            f.Dispose();
        }

        /// <summary>The cleaning thread's progress reaches the window through Post, in order, like every service event.</summary>
        CleanReport CleanWithProgress(CleanRequest req)
        {
            req.Progress = p => Post(() => OnCleanProgress(p));
            return Cleaner.Clean(req);
        }

        /// <summary>Every sample, seen or not, so the chart is full when the window opens.</summary>
        void RecordLoad(MemorySnapshot s)
        {
            history.Add(DateTime.UtcNow, (int)Math.Min(100, s.LoadPercent));
        }

        /// <summary>From UpdateTray, which runs on every sample and every state change: the flyout and the chart follow.</summary>
        void RefreshVisuals()
        {
            UpdateFlyout(false);
            RenderChart();
        }

        // ------------------------------------------------------------------ load history

        void RenderChart()
        {
            if (historyChart == null || !WindowShowing || !memoryTab.IsSelected) return;
            DateTime now = DateTime.UtcNow;
            List<LoadSample> samples = history.Samples(now);
            List<DateTime> cleans = history.Cleans(now);
            int? threshold = settings.AutoCleanEnabled ? settings.ThresholdPercent : (int?)null;
            bool thresholdDrawn = threshold.HasValue && threshold.Value > LoadChartMath.Floor(samples);
            historyChart.SetData(samples, cleans, threshold, now);
            chartAutoKey.Visibility = thresholdDrawn ? Visibility.Visible : Visibility.Collapsed;
            CultureInfo culture = CultureInfo.CurrentCulture;
            chartFrom.Text = AppText.ChartFrom(DateTime.Now, culture);
            List<DateTime> local = cleans.ConvertAll(c => c.ToLocalTime());
            AutomationProperties.SetName(historyChart, AppText.ChartName(samples, local, culture));
        }

        // ------------------------------------------------------------------ per-part change after a clean

        string PartChange(MapPart part)
        {
            return partChanges == null ? null : partChanges[(int)part];
        }

        /// <summary>
        /// The signed figures under the legend and the note naming the clean. They rise 4 px and fade in (320 ms, ease
        /// out), instantly with Windows animations off, and stay until the next clean starts.
        /// </summary>
        void ShowChanges(CleanReport report, DateTime cleanUtc)
        {
            partChanges = PartChanges.From(report);
            changeGeneration++; // a fade-out still running must not collapse the new figures
            if (partChanges == null)
            {
                foreach (TextBlock t in changeTexts) t.Visibility = Visibility.Collapsed;
                changeNote.Visibility = Visibility.Collapsed;
                return;
            }
            bool animate = WindowShowing;
            var shown = new bool[changeTexts.Length];
            for (int i = 0; i < shown.Length; i++) shown[i] = partChanges[i] != null;
            for (int i = 0; i < changeTexts.Length; i++)
            {
                TextBlock t = changeTexts[i];
                string text = partChanges[i];
                if (text == null) { t.Visibility = Visibility.Collapsed; continue; }
                t.Text = text;
                t.Visibility = Visibility.Visible;
                Enter(t, (TranslateTransform)t.RenderTransform, animate, ChangeStagger.Delay(shown, i));
            }
            changeNote.Text = AppText.ChangeNote(cleanUtc.ToLocalTime(), CultureInfo.CurrentCulture);
            changeNote.Visibility = Visibility.Visible;
            Enter(changeNote, null, animate, ChangeStagger.NoteDelay(shown));
        }

        /// <summary>Rise 4 px and fade in after delay (the 30 ms stagger); everything at once with motion off.</summary>
        static void Enter(UIElement e, TranslateTransform rise, bool animate, TimeSpan delay)
        {
            bool moving = animate && Motion.On;
            if (moving)
            {
                Motion.To(e, UIElement.OpacityProperty, 0, TimeSpan.Zero, null, false);
                if (rise != null) Motion.To(rise, TranslateTransform.YProperty, 4, TimeSpan.Zero, null, false);
            }
            Motion.To(e, UIElement.OpacityProperty, 1, Motion.EnterDuration, Motion.Enter, moving, null, delay);
            if (rise != null) Motion.To(rise, TranslateTransform.YProperty, 0, Motion.EnterDuration, Motion.Enter, moving, null, delay);
        }

        /// <summary>A new clean starts: the old figures fade out (200 ms, ease in) and go.</summary>
        void HideChanges()
        {
            if (partChanges == null && changeNote.Visibility != Visibility.Visible) return;
            partChanges = null;
            int gen = ++changeGeneration;
            var shown = new List<UIElement>();
            foreach (TextBlock t in changeTexts) if (t.Visibility == Visibility.Visible) shown.Add(t);
            if (changeNote.Visibility == Visibility.Visible) shown.Add(changeNote);
            foreach (UIElement e in shown)
            {
                UIElement element = e;
                Motion.To(element, UIElement.OpacityProperty, 0, Motion.ExitDuration, Motion.Exit, WindowShowing, () =>
                {
                    if (gen == changeGeneration) element.Visibility = Visibility.Collapsed;
                });
            }
        }

        // ------------------------------------------------------------------ clean progress

        void StartProgress()
        {
            if (progressView.Running || progressTimer == null) return;
            progressView.Start(DateTime.UtcNow);
            progressGeneration++;
            HideChanges();
            progressTimer.Stop();
            progressTimer.Start();
        }

        void OnCleanProgress(CleanProgress p)
        {
            if (progressView.Step(p) && cleanProgress.Visibility == Visibility.Visible) ShowStep();
        }

        /// <summary>From 400 ms into a clean: the bar fades in (150 ms, ease out) and is read out once, by its name.</summary>
        void ShowProgress()
        {
            if (!progressView.Running) return;
            ShowStep();
            cleanProgress.Visibility = Visibility.Visible;
            cleanProgress.UpdateLayout();
            bool animate = WindowShowing && Motion.On;
            if (animate) Motion.To(cleanProgress, UIElement.OpacityProperty, 0, TimeSpan.Zero, null, false);
            Motion.To(cleanProgress, UIElement.OpacityProperty, 1, ProgressFade, Motion.Enter, animate);
            MoveSegment();
            int gen = progressGeneration;
            if (WindowShowing)
                dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (gen == progressGeneration && progressView.Running) cleanProgress.Announce();
                }));
        }

        void ShowStep()
        {
            string line = progressView.Line;
            progressText.Text = line ?? "";
            progressText.Visibility = line == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// The segment sweeps the track (1600 ms a pass, SineEase in and out, until the clean ends); with Windows
        /// animations off it stands still over the first 40%, so the bar still says "working" without moving.
        /// </summary>
        void MoveSegment()
        {
            double track = progressTrack.ActualWidth;
            double width = Math.Max(24, track * SegmentShare);
            progressSegment.Width = width;
            var slide = (TranslateTransform)progressSegment.RenderTransform;
            if (!Motion.On || track <= 0)
            {
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                slide.X = 0;
                return;
            }
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            ease.Freeze();
            var sweep = new DoubleAnimation(-width, track, new Duration(SegmentPass))
            {
                EasingFunction = ease,
                RepeatBehavior = RepeatBehavior.Forever
            };
            slide.BeginAnimation(TranslateTransform.XProperty, sweep);
        }

        void FinishProgress()
        {
            progressView.Finish();
            if (progressTimer == null) return;
            progressTimer.Stop();
            int gen = ++progressGeneration;
            if (cleanProgress.Visibility != Visibility.Visible) return;
            Motion.To(cleanProgress, UIElement.OpacityProperty, 0, Motion.ExitDuration, Motion.Exit, WindowShowing, () =>
            {
                if (gen != progressGeneration) return;
                cleanProgress.Visibility = Visibility.Collapsed;
                ((TranslateTransform)progressSegment.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
            });
        }

        // ------------------------------------------------------------------ tray flyout

        void UpdateFlyout(bool force)
        {
            if (flyout == null || (!force && !flyout.IsOpen)) return;
            AutoCleanState auto = AppText.AutoState(settings.AutoCleanEnabled, elevated, autoInfo);
            CultureInfo culture = CultureInfo.CurrentCulture;
            var st = new FlyoutState
            {
                Snapshot = lastSnapshot,
                CleanEnabled = AppText.CleanEnabled(elevated, cleaningUi, memView.State),
                CleanLabel = AppText.CleanLabel(settings.PowerClean, cleaningUi),
                CleanTip = AppText.CleanTooltip(elevated, cleaningUi, memView.State),
                AutoLine = AppText.FlyoutAutoLine(auto, autoInfo, settings, culture),
                PauseVisible = AppText.CanPause(auto) || auto == AutoCleanState.Paused,
                Paused = auto == AutoCleanState.Paused,
                ResultLine = TrayStatus.MenuLine(lastClean, DateTime.Now, culture)
            };
            if (lastClean != null)
            {
                st.ResultGlyph = AppText.ResultGlyphKey(lastClean.Report, lastClean.Error);
                st.ResultBrush = AppText.ResultGlyphBrush(lastClean.Report, lastClean.Error);
            }
            CleanAdvice a = AppText.AdviceVisible(memView.State) ? advice.Shown : CleanAdvice.None;
            string glyph = AppText.AdviceGlyph(a);
            if (a != CleanAdvice.None && glyph != null && lastSnapshot != null)
            {
                st.Verdict = AppText.CleanAdvice(a, lastSnapshot);
                st.VerdictGlyph = glyph;
                st.VerdictStroke = AppText.AdviceStroke(a);
                st.VerdictBrush = AppText.AdviceTextBrush(a);
            }
            flyout.Update(st);
        }

        // ------------------------------------------------------------------ Mica

        void ApplyBackdrop()
        {
            if (theme == null || window == null) return;
            theme.ApplyBackdrop(window, settings.UseMica);
        }

        /// <summary>The row shows on Windows 11; the switch works from 22H2 and not under high contrast, and the note says why.</summary>
        void SyncMica()
        {
            int build = ThemeManager.OsBuild;
            bool highContrast = theme != null && theme.Current == ResolvedTheme.HighContrast;
            bool usable = MicaPolicy.SwitchEnabled(build, highContrast);
            string note = AppText.MicaNote(build, highContrast);
            bool was = syncing;
            syncing = true;
            try
            {
                micaRow.Visibility = MicaPolicy.SettingShown(build) ? Visibility.Visible : Visibility.Collapsed;
                micaCheck.IsChecked = settings.UseMica;
                micaCheck.IsEnabled = usable;
                micaCheck.ToolTip = usable ? null : note;
                micaNote.Text = note;
                AutomationProperties.SetHelpText(micaCheck, note);
            }
            finally { syncing = was; }
        }
    }
}
