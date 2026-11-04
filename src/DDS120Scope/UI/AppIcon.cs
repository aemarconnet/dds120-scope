using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Dds120.UI
{
    // The window icon, drawn at runtime: a small scope screen with a yellow and a cyan trace.
    internal static class AppIcon
    {
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        public static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = size / 32f;
                using (var body = new SolidBrush(Color.FromArgb(40, 44, 52)))
                using (var path = Rounded(new RectangleF(1 * s, 3 * s, 30 * s, 26 * s), 4 * s))
                    g.FillPath(body, path);
                using (var screen = new SolidBrush(Theme.Screen))
                    g.FillRectangle(screen, 4 * s, 6 * s, 24 * s, 20 * s);
                using (var grid = new Pen(Theme.Grid, Math.Max(1, s * 0.6f)))
                {
                    g.DrawLine(grid, 16 * s, 6 * s, 16 * s, 26 * s);
                    g.DrawLine(grid, 4 * s, 16 * s, 28 * s, 16 * s);
                }
                var sq = new[]
                {
                    new PointF(4 * s, 20 * s), new PointF(9 * s, 20 * s), new PointF(9 * s, 10 * s), new PointF(16 * s, 10 * s),
                    new PointF(16 * s, 20 * s), new PointF(23 * s, 20 * s), new PointF(23 * s, 10 * s), new PointF(28 * s, 10 * s),
                };
                using (var p1 = new Pen(Theme.Channel[0], 1.8f * s))
                    g.DrawLines(p1, sq);
                var sine = new PointF[25];
                for (int i = 0; i < sine.Length; i++)
                    sine[i] = new PointF((4 + i) * s, (float)(16 - 4.5 * Math.Sin(i / 24.0 * 2 * Math.PI)) * s);
                using (var p2 = new Pen(Theme.Channel[1], 1.6f * s))
                    g.DrawLines(p2, sine);
            }
            return bmp;
        }

        public static Icon Create()
        {
            using (Bitmap bmp = Render(64))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(h).Clone();
                }
                finally
                {
                    DestroyIcon(h);
                }
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
