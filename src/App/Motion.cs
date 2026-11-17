using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RamOptimiser.App
{
    /// <summary>
    /// Motion rules from design.md (redesign B): entrances ease out (QuinticEase), exits ease in (CubicEase),
    /// interface changes spring with a slight overshoot (BackEase, amplitude 0.3); nothing is linear. Every
    /// change is instant when Windows animations are off (SystemParameters.ClientAreaAnimation).
    /// </summary>
    internal static class Motion
    {
        public static readonly TimeSpan MapDuration = TimeSpan.FromMilliseconds(700);
        public static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(320);
        public static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(200);
        public static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(120);
        public const double RiseDistance = 8;
        public const double PressScale = 0.97;

        public static bool On { get { return SystemParameters.ClientAreaAnimation; } }

        public static readonly IEasingFunction Spring = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
        public static readonly IEasingFunction Enter = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
        public static readonly IEasingFunction Exit = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });

        static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

        /// <summary>Animates a double property to a value, or sets it at once when motion is off or not wanted.</summary>
        public static void To(DependencyObject target, DependencyProperty property, double to, TimeSpan duration,
            IEasingFunction ease, bool animate, Action done = null)
        {
            var a = (IAnimatable)target;
            if (animate && On && duration > TimeSpan.Zero)
            {
                var animation = new DoubleAnimation(to, new Duration(duration)) { EasingFunction = ease };
                if (done != null) animation.Completed += delegate { done(); };
                a.BeginAnimation(property, animation);
                return;
            }
            a.BeginAnimation(property, null);
            target.SetValue(property, to);
            if (done != null) done();
        }

        /// <summary>Press feedback: the button scales to 0.97 and springs back, 120 ms each way.</summary>
        public static void AddPress(ButtonBase button)
        {
            var scale = new ScaleTransform(1, 1);
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = scale;
            DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase)).AddValueChanged(button, delegate
            {
                double to = button.IsPressed ? PressScale : 1;
                To(scale, ScaleTransform.ScaleXProperty, to, PressDuration, Spring, true);
                To(scale, ScaleTransform.ScaleYProperty, to, PressDuration, Spring, true);
            });
        }
    }

    /// <summary>
    /// Figures that tween: animating Bytes (or Whole) on a TextBlock or Run rewrites its text every frame,
    /// so "714.2 MB" counts up to "3.4 GB" while the map springs.
    /// </summary>
    internal static class Figure
    {
        public static readonly DependencyProperty BytesProperty = DependencyProperty.RegisterAttached(
            "Bytes", typeof(double), typeof(Figure), new PropertyMetadata(0.0, (d, e) => SetText(d, Format.Bytes((long)Math.Round((double)e.NewValue)))));

        public static readonly DependencyProperty WholeProperty = DependencyProperty.RegisterAttached(
            "Whole", typeof(double), typeof(Figure),
            new PropertyMetadata(0.0, (d, e) => SetText(d, Math.Round((double)e.NewValue).ToString(CultureInfo.InvariantCulture))));

        static void SetText(DependencyObject d, string text)
        {
            var block = d as TextBlock;
            if (block != null) { if (block.Text != text) block.Text = text; return; }
            var run = d as Run;
            if (run != null && run.Text != text) run.Text = text;
        }
    }
}
