// Draws the app icon and writes it as a multi-size .ico. Used at build time by tools/IconGen.cs and by the
// tests; it depends only on System.Drawing.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace RamOptimiser.App
{
    public static class AppIcon
    {
        public static readonly int[] Sizes = { 16, 24, 32, 48, 256 };

        /// <summary>design.md accent (light): #005FB8.</summary>
        public static readonly Color Accent = Color.FromArgb(0x00, 0x5F, 0xB8);

        /// <summary>
        /// A memory chip: a rounded square body in the accent colour with pins on both sides, and a white
        /// usage bar (an outlined track, filled most of the way). Coordinates are on a 16-unit grid and are
        /// snapped to whole pixels so the small sizes stay crisp.
        /// </summary>
        public static Bitmap Render(int size)
        {
            if (size < 16 || size > 256) throw new ArgumentOutOfRangeException("size");
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            float u = size / 16f;
            using (Graphics g = Graphics.FromImage(bmp))
            using (var accent = new SolidBrush(Accent))
            using (var white = new SolidBrush(Color.White))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Pins: 2 per side up to 24 px, 3 from 32 px.
                float[] pinRows = size < 32 ? new[] { 5f, 9f } : new[] { 3.5f, 7f, 10.5f };
                foreach (float y in pinRows)
                {
                    g.FillRectangle(accent, Snap(0, u), Snap(y, u), Snap(2, u), Snap(2, u));
                    g.FillRectangle(accent, Snap(14, u), Snap(y, u), Snap(2, u), Snap(2, u));
                }

                // Body.
                using (GraphicsPath body = Rounded(Snap(2, u), Snap(1, u), Snap(12, u), Snap(14, u), Math.Max(1.5f, 2.2f * u)))
                    g.FillPath(accent, body);

                // Usage bar: white outline track with a solid white fill over about two thirds.
                float stroke = Math.Max(1f, (float)Math.Round(u * 1.1f));
                RectangleF track = new RectangleF(Snap(4, u), Snap(6, u), Snap(8, u), Snap(4, u));
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.None;
                using (var pen = new Pen(Color.White, stroke) { Alignment = PenAlignment.Inset })
                    g.DrawRectangle(pen, track.X, track.Y, track.Width, track.Height);
                g.FillRectangle(white, track.X, track.Y, (float)Math.Round(track.Width * 0.62f), track.Height);
            }
            return bmp;
        }

        static float Snap(float v, float u) { return (float)Math.Round(v * u); }

        static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            float d = Math.Min(r * 2, Math.Min(w, h));
            var p = new GraphicsPath();
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// A .ico with one entry per image: 32-bit DIB entries below 256 px (read by every API, including
        /// System.Drawing.Icon), and a PNG entry at 256 px to keep the file small (Windows Vista+ format).
        /// </summary>
        public static byte[] BuildIco(IList<Bitmap> images)
        {
            if (images == null || images.Count == 0) throw new ArgumentException("No images.", "images");
            var payloads = new List<byte[]>();
            foreach (Bitmap b in images)
            {
                if (b.Width != b.Height || b.Width < 1 || b.Width > 256) throw new ArgumentException("Icon images must be square, 1 to 256 px.");
                payloads.Add(b.Width >= 256 ? Png(b) : Dib(b));
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // type: icon
                w.Write((ushort)images.Count);
                int offset = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    int s = images[i].Width;
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);            // palette colours
                    w.Write((byte)0);            // reserved
                    w.Write((ushort)1);          // planes
                    w.Write((ushort)32);         // bits per pixel
                    w.Write(payloads[i].Length);
                    w.Write(offset);
                    offset += payloads[i].Length;
                }
                foreach (byte[] p in payloads) w.Write(p);
                w.Flush();
                return ms.ToArray();
            }
        }

        static byte[] Png(Bitmap b)
        {
            using (var ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>BITMAPINFOHEADER (height doubled for the mask), bottom-up BGRA pixels, then the 1-bit AND mask.</summary>
        static byte[] Dib(Bitmap b)
        {
            int s = b.Width;
            int maskStride = ((s + 31) / 32) * 4;
            var pixels = new byte[s * s * 4];
            BitmapData data = b.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < s; row++)
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * s * 4, s * 4);
            }
            finally { b.UnlockBits(data); }

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);                     // biSize
                w.Write(s);                      // biWidth
                w.Write(s * 2);                  // biHeight: colour plus mask
                w.Write((ushort)1);              // biPlanes
                w.Write((ushort)32);             // biBitCount
                w.Write(0);                      // BI_RGB
                w.Write(s * s * 4 + maskStride * s);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int row = s - 1; row >= 0; row--) w.Write(pixels, row * s * 4, s * 4);
                var mask = new byte[maskStride];
                for (int row = s - 1; row >= 0; row--)
                {
                    Array.Clear(mask, 0, mask.Length);
                    for (int x = 0; x < s; x++)
                        if (pixels[(row * s + x) * 4 + 3] == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                    w.Write(mask);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static byte[] BuildDefaultIco()
        {
            var images = new List<Bitmap>();
            try
            {
                foreach (int s in Sizes) images.Add(Render(s));
                return BuildIco(images);
            }
            finally { foreach (Bitmap b in images) b.Dispose(); }
        }
    }
}
