using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace RamOptimiser.App
{
    /// <summary>
    /// The memory map: lays its children side by side, each as wide as its share of the attached Weight, with a
    /// small gap between parts and rounded ends. Weight is a plain double, so a DoubleAnimation can spring it.
    /// Read by screen readers as one image with its AutomationProperties.Name (the parts as text).
    /// </summary>
    public sealed class MapPanel : Panel
    {
        /// <summary>A part's share of the bar. Any scale: the panel divides by the sum. Below zero counts as zero
        /// (a spring can overshoot past it).</summary>
        public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
            "Weight", typeof(double), typeof(MapPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static double GetWeight(UIElement e) { return (double)e.GetValue(WeightProperty); }
        public static void SetWeight(UIElement e, double value) { e.SetValue(WeightProperty, value); }

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            "Gap", typeof(double), typeof(MapPanel), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
            "Radius", typeof(double), typeof(MapPanel), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsArrange));

        /// <summary>Space between two parts, in the panel's background colour.</summary>
        public double Gap { get { return (double)GetValue(GapProperty); } set { SetValue(GapProperty, value); } }

        public double Radius { get { return (double)GetValue(RadiusProperty); } set { SetValue(RadiusProperty, value); } }

        /// <summary>A part that is there at all stays visible, however small.</summary>
        const double MinPartWidth = 2;

        protected override Size MeasureOverride(Size available)
        {
            var child = new Size(double.PositiveInfinity, double.IsInfinity(available.Height) ? 0 : available.Height);
            foreach (UIElement e in InternalChildren) e.Measure(child);
            return new Size(0, 0);
        }

        protected override Size ArrangeOverride(Size size)
        {
            var shown = new List<UIElement>();
            var weights = new List<double>();
            double sum = 0;
            foreach (UIElement e in InternalChildren)
            {
                double w = Math.Max(0, GetWeight(e));
                if (e.Visibility == Visibility.Collapsed || w <= 0 || double.IsNaN(w)) { e.Arrange(new Rect(0, 0, 0, size.Height)); continue; }
                shown.Add(e);
                weights.Add(w);
                sum += w;
            }

            double room = Math.Max(0, size.Width - Gap * Math.Max(0, shown.Count - 1));
            var widths = new double[shown.Count];
            double small = 0, largeSum = 0;
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] = weights[i] / sum * room;
                if (widths[i] < MinPartWidth) small += MinPartWidth;
                else largeSum += widths[i];
            }
            double scale = largeSum > 0 ? Math.Max(0, room - small) / largeSum : 0;
            for (int i = 0; i < widths.Length; i++) widths[i] = widths[i] < MinPartWidth ? MinPartWidth : widths[i] * scale;

            double x = 0;
            for (int i = 0; i < shown.Count; i++)
            {
                double left = Math.Round(x);
                double right = i == shown.Count - 1 ? size.Width : Math.Round(x + widths[i]);
                shown[i].Arrange(new Rect(left, 0, Math.Max(0, right - left), size.Height));
                x += widths[i] + Gap;
            }

            var clip = new RectangleGeometry(new Rect(size), Radius, Radius);
            clip.Freeze();
            Clip = clip;
            return size;
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new MapPanelPeer(this);
        }

        sealed class MapPanelPeer : FrameworkElementAutomationPeer
        {
            public MapPanelPeer(FrameworkElement owner) : base(owner) { }
            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.Image; }
            protected override string GetClassNameCore() { return "MemoryMap"; }
            protected override List<AutomationPeer> GetChildrenCore() { return null; }
            protected override bool IsControlElementCore() { return true; }
            protected override bool IsContentElementCore() { return true; }
        }
    }
}
