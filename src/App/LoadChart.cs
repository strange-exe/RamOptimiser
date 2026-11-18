using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;

namespace RamOptimiser.App
{
    /// <summary>
    /// The Memory tab's load history (7A), drawn in code with no chart library: hairlines at 100% and at the floor
    /// (and 50% when the floor is 0), the auto-clean threshold dashed, one accent line for the load, and a short tick
    /// at the foot for each clean. Axis labels sit in a 36 px column on the right. The geometry comes from
    /// LoadChartMath (tested); this only paints it. Colours are theme tokens read at paint time, so AppShell redraws it
    /// when the theme changes. Screen readers get it as an image whose name is AppText.ChartName.
    /// </summary>
    public sealed class LoadChart : FrameworkElement
    {
        public const double LabelColumn = 36;
        const double PadY = 7, TickHeight = 7, LabelGap = 14, LabelSize = 12;
        /// <summary>Readings further apart than this (the PC slept) are not joined by the line.</summary>
        static readonly TimeSpan Gap = TimeSpan.FromSeconds(30);

        List<LoadSample> samples = new List<LoadSample>();
        List<DateTime> cleans = new List<DateTime>();
        int? threshold;
        DateTime nowUtc;

        /// <summary>The readings and clean times (UTC) of the last ten minutes, and the threshold (null when auto-clean is off).</summary>
        public void SetData(List<LoadSample> readings, List<DateTime> cleanTimes, int? autoThreshold, DateTime now)
        {
            samples = readings ?? new List<LoadSample>();
            cleans = cleanTimes ?? new List<DateTime>();
            threshold = autoThreshold;
            nowUtc = now;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth - LabelColumn, height = ActualHeight - 2 * PadY;
            if (width < 10 || height < 10) return;
            Brush hairline = Token("Hairline"), accent = Token("Accent"), muted = Token("MutedText"), text = Token("Text"),
                edge = Token("ControlBorder");
            int floor = LoadChartMath.Floor(samples);

            var levels = new List<int> { 100, floor };
            if (floor == 0) levels.Add(50);
            Pen hair = Frozen(new Pen(hairline, 1));
            foreach (int level in levels) HLine(dc, hair, Snap(PadY + LoadChartMath.Y(level, floor, height)), width);

            if (threshold.HasValue && threshold.Value > floor && threshold.Value < 100)
            {
                var dashed = new Pen(edge, 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                HLine(dc, Frozen(dashed), Snap(PadY + LoadChartMath.Y(threshold.Value, floor, height)), width);
            }

            DrawLine(dc, accent, floor, width, height);

            var tick = Frozen(new Pen(text, 1.5));
            double foot = PadY + height;
            foreach (DateTime c in cleans)
            {
                double x = LoadChartMath.X(c, nowUtc, width);
                dc.DrawLine(tick, new Point(x, foot), new Point(x, foot - TickHeight));
            }

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var face = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            foreach (int label in LoadChartMath.Labels(floor, threshold, height, LabelGap))
            {
                var ft = new FormattedText(label.ToString(CultureInfo.InvariantCulture) + "%", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, LabelSize, muted, pixelsPerDip);
                double y = PadY + LoadChartMath.Y(label, floor, height) - ft.Height / 2;
                dc.DrawText(ft, new Point(width + 6, Math.Max(0, Math.Min(ActualHeight - ft.Height, y))));
            }
        }

        void DrawLine(DrawingContext dc, Brush accent, int floor, double width, double height)
        {
            if (samples.Count == 0) return;
            var pen = Frozen(new Pen(accent, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                DateTime last = DateTime.MinValue;
                foreach (LoadSample s in samples)
                {
                    var p = new Point(LoadChartMath.X(s.Utc, nowUtc, width), PadY + LoadChartMath.Y(s.Load, floor, height));
                    if (last == DateTime.MinValue || s.Utc - last > Gap) g.BeginFigure(p, false, false);
                    else g.LineTo(p, true, true);
                    last = s.Utc;
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            // A lone reading is a dot, not nothing.
            if (samples.Count == 1)
            {
                LoadSample only = samples[0];
                dc.DrawEllipse(accent, null, new Point(LoadChartMath.X(only.Utc, nowUtc, width), PadY + LoadChartMath.Y(only.Load, floor, height)), 1.5, 1.5);
            }
        }

        static void HLine(DrawingContext dc, Pen pen, double y, double width)
        {
            dc.DrawLine(pen, new Point(0, y), new Point(width, y));
        }

        /// <summary>A 1 px line sits on a pixel, not across two.</summary>
        static double Snap(double y) { return Math.Round(y) + 0.5; }

        Brush Token(string key)
        {
            return TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        static Pen Frozen(Pen p)
        {
            p.Freeze();
            return p;
        }

        protected override AutomationPeer OnCreateAutomationPeer() { return new LoadChartPeer(this); }

        sealed class LoadChartPeer : FrameworkElementAutomationPeer
        {
            public LoadChartPeer(FrameworkElement owner) : base(owner) { }
            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.Image; }
            protected override string GetClassNameCore() { return "LoadChart"; }
            protected override bool IsControlElementCore() { return true; }
            protected override bool IsContentElementCore() { return true; }
        }
    }
}
