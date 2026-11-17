// Build-time tool: writes the app icon (.ico) and, optionally, a preview strip PNG for checking the small
// sizes on light and dark backgrounds.  Usage: icongen.exe <out.ico> [preview.png]
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace RamOptimiser.Tools
{
    public static class IconGen
    {
        public static int Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2)
            {
                Console.Error.WriteLine("usage: icongen <out.ico> [preview.png]");
                return 1;
            }
            File.WriteAllBytes(args[0], RamOptimiser.App.AppIcon.BuildDefaultIco());
            if (args.Length == 2) WritePreview(args[1]);
            return 0;
        }

        /// <summary>Two strips (light #F3F3F3, dark #202020): sizes 16/24/32/48 at 1x, then 16 and 32 at 4x.</summary>
        static void WritePreview(string path)
        {
            int[] sizes = { 16, 24, 32, 48 };
            const int pad = 12, zoom = 4, stripH = 32 * zoom + 2 * pad;
            int width = pad;
            foreach (int s in sizes) width += s + pad;
            width += 16 * zoom + pad + 32 * zoom + pad;
            Color[] backgrounds = { Color.FromArgb(0xF3, 0xF3, 0xF3), Color.FromArgb(0x20, 0x20, 0x20) };
            using (var sheet = new Bitmap(width, stripH * backgrounds.Length, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                for (int b = 0; b < backgrounds.Length; b++)
                {
                    int top = b * stripH;
                    using (var bg = new SolidBrush(backgrounds[b])) g.FillRectangle(bg, 0, top, width, stripH);
                    int x = pad;
                    foreach (int s in sizes)
                    {
                        using (Bitmap icon = RamOptimiser.App.AppIcon.Render(s)) g.DrawImage(icon, x, top + pad, s, s);
                        x += s + pad;
                    }
                    foreach (int s in new[] { 16, 32 })
                    {
                        using (Bitmap icon = RamOptimiser.App.AppIcon.Render(s)) g.DrawImage(icon, x, top + pad, s * zoom, s * zoom);
                        x += s * zoom + pad;
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                sheet.Save(path, ImageFormat.Png);
            }
        }
    }
}
