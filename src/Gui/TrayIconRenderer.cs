using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace RamOptimiser.Gui
{
    /// <summary>
    /// Draws the memory load as a number on a rounded square for the notification area. Colours, and
    /// optionally the font family, come from the caller: the visual theme is not decided here.
    /// The returned Icon owns its own handle; dispose it when it is replaced.
    /// </summary>
    public static class TrayIconRenderer
    {
        public const int MinSize = 16, MaxSize = 256;
        const float CornerRatio = 0.22f;      // corner radius as a share of the icon size
        const float TextWidthRatio = 0.94f;   // the label may use this share of the width
        const float TwoDigitHeightRatio = 0.72f;

        // The system UI font family, looked up once (SystemFonts returns a new Font on every call).
        static readonly FontFamily SystemFamily = LoadSystemFamily();

        static FontFamily LoadSystemFamily()
        {
            using (Font f = SystemFonts.MessageBoxFont)
                return new FontFamily(f.FontFamily.Name);
        }

        /// <summary>Uses the system UI font family (Segoe UI on current Windows).</summary>
        public static Icon Render(int percent, Color fill, Color text, int size)
        {
            return Render(percent, fill, text, size, SystemFamily);
        }

        public static Icon Render(int percent, Color fill, Color text, int size, FontFamily family)
        {
            if (size < MinSize || size > MaxSize) throw new ArgumentOutOfRangeException("size");
            if (family == null) throw new ArgumentNullException("family");
            string label = LabelFor(percent);

            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // ClearType needs an opaque background; on a transparent bitmap it leaves coloured fringes.
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    using (GraphicsPath shape = RoundedSquare(size, size * CornerRatio))
                    using (var brush = new SolidBrush(fill))
                        g.FillPath(brush, shape);

                    float px = LabelFontPixels(g, label, size, family);
                    using (var font = new Font(family, px, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(text))
                    using (var format = new StringFormat(StringFormat.GenericTypographic))
                    {
                        format.Alignment = StringAlignment.Center;
                        format.LineAlignment = StringAlignment.Center;
                        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                        g.DrawString(label, font, brush, new RectangleF(0, 0, size, size), format);
                    }
                }
                return ToOwnedIcon(bmp);
            }
        }

        /// <summary>The text drawn for a load: clamped to 0..100, and 100 is written in full.</summary>
        public static string LabelFor(int percent)
        {
            return Math.Max(0, Math.Min(100, percent)).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Font size in pixels for a label: two digits use a fixed share of the height; anything wider is
        /// shrunk until it fits the width, so "100" is drawn smaller rather than clipped.
        /// </summary>
        internal static float LabelFontPixels(Graphics g, string label, int size, FontFamily family)
        {
            float px = size * TwoDigitHeightRatio;
            float maxWidth = size * TextWidthRatio;
            for (int i = 0; i < 8; i++)
            {
                float width = MeasureWidth(g, label, px, family);
                if (width <= maxWidth || width <= 0) break;
                px = Math.Max(1f, px * maxWidth / width * 0.98f);
            }
            return px;
        }

        internal static float LabelFontPixels(string label, int size, FontFamily family)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
                return LabelFontPixels(g, label, size, family);
        }

        static float MeasureWidth(Graphics g, string label, float px, FontFamily family)
        {
            using (var font = new Font(family, px, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                return g.MeasureString(label, font, int.MaxValue, format).Width;
            }
        }

        static GraphicsPath RoundedSquare(int size, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(size - d, 0, d, d, 270, 90);
            path.AddArc(size - d, size - d, d, d, 0, 90);
            path.AddArc(0, size - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Bitmap.GetHicon returns an HICON nobody owns. Icon.FromHandle does not own it either, so the icon
        /// is cloned (the clone copies the image into a handle it owns) and the original HICON is destroyed.
        /// </summary>
        static Icon ToOwnedIcon(Bitmap bmp)
        {
            IntPtr hicon = bmp.GetHicon();
            try
            {
                using (Icon borrowed = Icon.FromHandle(hicon))
                    return (Icon)borrowed.Clone();
            }
            finally
            {
                GuiNative.DestroyIcon(hicon);
            }
        }
    }
}
