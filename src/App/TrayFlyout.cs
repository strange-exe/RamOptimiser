using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace RamOptimiser.App
{
    /// <summary>Everything the flyout shows, decided by AppShell from the same state as the window and the tray menu.</summary>
    internal sealed class FlyoutState
    {
        public MemorySnapshot Snapshot;
        public bool CleanEnabled;
        public string CleanLabel;
        public string CleanTip;
        public string AutoLine;
        public bool PauseVisible;
        public bool Paused;
        public string ResultLine;
        /// <summary>Icon and stroke token of the last clean's glyph, or null before the first clean.</summary>
        public string ResultGlyph, ResultBrush;
        /// <summary>The will-a-clean-help line, or null when the window hides it too.</summary>
        public string Verdict, VerdictGlyph, VerdictStroke, VerdictBrush;
    }

    /// <summary>
    /// The tray flyout (7A): a compact window above the tray icon, opened by a left click (the right-click menu is
    /// unchanged). It closes on Esc, on a click elsewhere (focus loss), on a second click of the icon and on Open
    /// window, and stays open while a clean runs. Tab cycles inside it. It fades and rises in (250 ms, ease out) and
    /// fades and sinks out (150 ms, ease in), instantly with Windows animations off (Motion). Placement is decided by
    /// FlyoutPlacement in physical pixels; the race between focus loss and the icon click by FlyoutGate.
    /// </summary>
    internal sealed class TrayFlyout : IDisposable
    {
        const double MarginDip = 12;
        const double EnterRise = 12, ExitSink = 8;
        static readonly TimeSpan EnterTime = TimeSpan.FromMilliseconds(250);
        static readonly TimeSpan ExitTime = TimeSpan.FromMilliseconds(150);

        readonly Window window;
        readonly Border root;
        readonly TranslateTransform rise;
        readonly SummaryPanel load;
        readonly Run loadNumber, inUse;
        readonly MapPanel map;
        readonly UIElement[] parts = new UIElement[4];
        readonly Grid verdict;
        readonly Path verdictGlyph, resultGlyph;
        readonly TextBlock verdictText, autoLine, resultText;
        readonly Button clean, pause, open;
        readonly FlyoutGate gate = new FlyoutGate();
        bool shown, paused, disposed, hasMap;
        int generation, anchorX, anchorY;

        public event Action CleanRequested, PauseRequested, ResumeRequested, OpenRequested;

        public TrayFlyout()
        {
            window = (Window)ThemeManager.LoadXaml("Flyout.xaml");
            root = Find<Border>("FlyRoot");
            rise = (TranslateTransform)root.RenderTransform;
            load = Find<SummaryPanel>("FlyLoad");
            loadNumber = Find<Run>("FlyLoadNumber");
            inUse = Find<Run>("FlyInUse");
            map = Find<MapPanel>("FlyMap");
            for (int i = 0; i < parts.Length; i++) parts[i] = map.Children[i];
            verdict = Find<Grid>("FlyVerdict");
            verdictGlyph = Find<Path>("FlyVerdictGlyph");
            verdictText = Find<TextBlock>("FlyVerdictText");
            clean = Find<Button>("FlyClean");
            pause = Find<Button>("FlyPause");
            autoLine = Find<TextBlock>("FlyAutoLine");
            resultGlyph = Find<Path>("FlyResultGlyph");
            resultText = Find<TextBlock>("FlyResultText");
            open = Find<Button>("FlyOpen");

            clean.Click += delegate { Raise(CleanRequested); };
            pause.Click += delegate { Raise(paused ? ResumeRequested : PauseRequested); };
            open.Click += delegate
            {
                Hide(false);
                Raise(OpenRequested);
            };
            // Clean memory turns off while a clean runs: keep focus in the flyout rather than losing it.
            clean.IsEnabledChanged += delegate
            {
                if (shown && !clean.IsEnabled && !window.IsKeyboardFocusWithin) open.Focus();
            };
            window.Deactivated += delegate { Hide(true); };
            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Hide(false);
            };
            // Alt+F4 hides it like Esc: the window is reused, never closed, until the app exits.
            window.Closing += (s, e) =>
            {
                if (disposed) return;
                e.Cancel = true;
                Hide(false);
            };
            Motion.AddPress(clean);
        }

        public bool IsOpen { get { return shown; } }

        /// <summary>The icon's left click, at the pointer (physical pixels). A click right after a focus-loss close is the
        /// same gesture that closed it, so it does not reopen it.</summary>
        public void Toggle(int pointerX, int pointerY)
        {
            if (disposed) return;
            if (shown) { Hide(false); return; }
            if (!gate.ShouldOpen(DateTime.UtcNow)) return;
            Open(pointerX, pointerY);
        }

        public void Update(FlyoutState st)
        {
            if (disposed || st == null) return;
            MemorySnapshot s = st.Snapshot;
            if (s != null)
            {
                double percent = Math.Min(100, s.LoadPercent);
                loadNumber.Text = percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
                inUse.Text = AppText.InUseLine(s);
                AutomationProperties.SetName(load, AppText.LoadCardName(s));
                MemoryMap m = MemoryMap.From(s);
                foreach (MapPart part in MemoryMap.Parts)
                    Motion.To(parts[(int)part], MapPanel.WeightProperty, m.Fraction(part), Motion.MapDuration, Motion.Spring, shown && hasMap);
                AutomationProperties.SetName(map, AppText.MapName(m, s.TotalPhys));
                hasMap = true;
            }

            verdict.Visibility = st.Verdict == null ? Visibility.Collapsed : Visibility.Visible;
            if (st.Verdict != null)
            {
                verdictText.Text = st.Verdict;
                verdictText.SetResourceReference(TextBlock.ForegroundProperty, st.VerdictBrush ?? "MutedText");
                verdictGlyph.Data = window.TryFindResource(st.VerdictGlyph ?? "IconInfo") as Geometry;
                verdictGlyph.SetResourceReference(Shape.StrokeProperty, st.VerdictStroke ?? "MutedText");
            }

            clean.Content = st.CleanLabel;
            AutomationProperties.SetName(clean, st.CleanLabel ?? "");
            clean.IsEnabled = st.CleanEnabled;
            clean.ToolTip = st.CleanTip;

            autoLine.Text = st.AutoLine ?? "";
            paused = st.Paused;
            pause.Visibility = st.PauseVisible ? Visibility.Visible : Visibility.Collapsed;
            string label = AppText.FlyoutPauseText(paused);
            pause.Content = label;
            AutomationProperties.SetName(pause, AppText.FlyoutPauseName(paused));
            // A hidden button cannot keep focus: hand it to Open window, which is always there.
            if (!st.PauseVisible && pause.IsKeyboardFocused) open.Focus();

            resultText.Text = st.ResultLine ?? AppText.FlyoutNoClean;
            resultGlyph.Visibility = st.ResultGlyph == null ? Visibility.Collapsed : Visibility.Visible;
            if (st.ResultGlyph != null)
            {
                resultGlyph.Data = window.TryFindResource(st.ResultGlyph) as Geometry;
                resultGlyph.SetResourceReference(Shape.StrokeProperty, st.ResultBrush ?? "MutedText");
            }
        }

        void Open(int pointerX, int pointerY)
        {
            shown = true;
            int gen = ++generation;
            anchorX = pointerX;
            anchorY = pointerY;
            root.IsHitTestVisible = true;
            Motion.To(root, UIElement.OpacityProperty, 0, TimeSpan.Zero, null, false);
            Motion.To(rise, TranslateTransform.YProperty, Motion.On ? EnterRise : 0, TimeSpan.Zero, null, false);
            window.Show();
            window.UpdateLayout();
            Place();
            window.Activate();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (gen != generation || !shown) return;
                // Again, now the window has the DPI of the screen it opened on.
                Place();
                if (clean.IsEnabled) clean.Focus();
                else open.Focus();
                Motion.To(root, UIElement.OpacityProperty, 1, EnterTime, Motion.Enter, true);
                Motion.To(rise, TranslateTransform.YProperty, 0, EnterTime, Motion.Enter, true);
            }));
        }

        /// <param name="byDeactivation">It lost focus (a click elsewhere, possibly on the tray icon itself).</param>
        void Hide(bool byDeactivation)
        {
            if (!shown) return;
            shown = false;
            gate.Closed(DateTime.UtcNow, byDeactivation);
            int gen = ++generation;
            root.IsHitTestVisible = false;
            Motion.To(rise, TranslateTransform.YProperty, ExitSink, ExitTime, Motion.Exit, true);
            Motion.To(root, UIElement.OpacityProperty, 0, ExitTime, Motion.Exit, true, () =>
            {
                if (gen == generation && !disposed) window.Hide();
            });
        }

        /// <summary>Next to the taskbar that holds the icon, in the work area of the screen under the pointer.</summary>
        void Place()
        {
            PresentationSource source = PresentationSource.FromVisual(window);
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (source == null || source.CompositionTarget == null || hwnd == IntPtr.Zero) return;
            Matrix toDevice = source.CompositionTarget.TransformToDevice;
            int width = (int)Math.Ceiling(window.ActualWidth * toDevice.M11);
            int height = (int)Math.Ceiling(window.ActualHeight * toDevice.M22);
            int margin = (int)Math.Round(MarginDip * toDevice.M11);
            System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(anchorX, anchorY));
            PixelPoint p = FlyoutPlacement.Place(anchorX, anchorY, Rect(screen.Bounds), Rect(screen.WorkingArea), width, height, margin);
            SetWindowPos(hwnd, HwndTopmost, p.X, p.Y, 0, 0, SwpNoSize | SwpNoActivate);
        }

        static PixelRect Rect(System.Drawing.Rectangle r)
        {
            return new PixelRect(r.Left, r.Top, r.Width, r.Height);
        }

        T Find<T>(string name) where T : class
        {
            T found = window.FindName(name) as T;
            if (found == null) throw new InvalidOperationException("Flyout.xaml has no " + typeof(T).Name + " named " + name + ".");
            return found;
        }

        void Raise(Action a)
        {
            if (a != null && !disposed) a();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            window.Close();
        }

        static readonly IntPtr HwndTopmost = new IntPtr(-1);
        const uint SwpNoSize = 0x0001, SwpNoActivate = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
