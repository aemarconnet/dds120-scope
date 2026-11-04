using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Dds120.Display;

namespace Dds120.UI
{
    // IScopeCanvas on GDI+. Pens, brushes, fonts and the image buffer are reused across frames.
    internal sealed class GdiCanvas : IScopeCanvas, IDisposable
    {
        readonly Dictionary<long, Font> fonts = new Dictionary<long, Font>();
        readonly Pen pen = new Pen(Color.White);
        readonly SolidBrush brush = new SolidBrush(Color.White);
        readonly StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone();
        readonly List<PointF> run = new List<PointF>();
        Bitmap image;
        GraphicsState clipState;

        public Graphics Graphics { get; set; }

        public GdiCanvas()
        {
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        }

        static Color C(int argb)
        {
            return Color.FromArgb(argb);
        }

        Pen Pen(int color, float width)
        {
            pen.Color = C(color);
            pen.Width = width;
            return pen;
        }

        public void Clear(int color)
        {
            Graphics.Clear(C(color));
        }

        public void Line(float x0, float y0, float x1, float y1, int color, float width)
        {
            Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Graphics.DrawLine(Pen(color, width), x0, y0, x1, y1);
        }

        // Consecutive segments that share end points are drawn as one polyline, which GDI+ does far
        // faster than separate lines.
        public void Segments(float[] xy, int count, int color, float width, bool antialias)
        {
            Graphics.SmoothingMode = antialias ? SmoothingMode.AntiAlias : SmoothingMode.None;
            Pen p = Pen(color, width);
            int i = 0;
            while (i + 3 < count)
            {
                run.Clear();
                run.Add(new PointF(xy[i], xy[i + 1]));
                run.Add(new PointF(xy[i + 2], xy[i + 3]));
                i += 4;
                while (i + 3 < count && xy[i] == xy[i - 2] && xy[i + 1] == xy[i - 1])
                {
                    run.Add(new PointF(xy[i + 2], xy[i + 3]));
                    i += 4;
                }
                if (run.Count == 2)
                    Graphics.DrawLine(p, run[0], run[1]);
                else
                    Graphics.DrawLines(p, run.ToArray());
            }
        }

        public void Dots(float[] xy, int count, int color, float diameter)
        {
            brush.Color = C(color);
            bool round = diameter > 2.5f;
            Graphics.SmoothingMode = round ? SmoothingMode.AntiAlias : SmoothingMode.None;
            float r = diameter / 2;
            for (int i = 0; i + 1 < count; i += 2)
            {
                if (round)
                    Graphics.FillEllipse(brush, xy[i] - r, xy[i + 1] - r, diameter, diameter);
                else
                    Graphics.FillRectangle(brush, xy[i] - r, xy[i + 1] - r, diameter, diameter);
            }
        }

        public void FillPolygon(float[] xy, int count, int color)
        {
            var pts = new PointF[count / 2];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new PointF(xy[2 * i], xy[2 * i + 1]);
            brush.Color = C(color);
            Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Graphics.FillPolygon(brush, pts);
        }

        public void FillRoundRect(float left, float top, float right, float bottom, float radius, int color)
        {
            brush.Color = C(color);
            Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float d = Math.Min(radius * 2, Math.Min(right - left, bottom - top));
            if (d < 1)
            {
                Graphics.FillRectangle(brush, left, top, right - left, bottom - top);
                return;
            }
            using (var path = new GraphicsPath())
            {
                path.AddArc(left, top, d, d, 180, 90);
                path.AddArc(right - d, top, d, d, 270, 90);
                path.AddArc(right - d, bottom - d, d, d, 0, 90);
                path.AddArc(left, bottom - d, d, d, 90, 90);
                path.CloseFigure();
                Graphics.FillPath(brush, path);
            }
        }

        public void StrokeRect(float left, float top, float right, float bottom, int color, float width)
        {
            Graphics.SmoothingMode = SmoothingMode.None;
            Graphics.DrawRectangle(Pen(color, width), left, top, right - left, bottom - top);
        }

        Font Font(FontKind kind, float size)
        {
            long key = (long)kind << 32 | (uint)(size * 16);
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = kind == FontKind.Mono
                    ? new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Pixel)
                    : new Font("Segoe UI", size, kind == FontKind.SansBold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        public float TextWidth(string text, FontKind font, float size)
        {
            return Graphics.MeasureString(text, Font(font, size), PointF.Empty, format).Width;
        }

        public void Text(string text, float x, float centerY, FontKind font, float size, int color, TextAlign align)
        {
            Font f = Font(font, size);
            Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            brush.Color = C(color);
            float w = align == TextAlign.Left ? 0 : Graphics.MeasureString(text, f, PointF.Empty, format).Width;
            float left = align == TextAlign.Left ? x : align == TextAlign.Center ? x - w / 2 : x - w;
            Graphics.DrawString(text, f, brush, left, centerY - f.GetHeight(Graphics) / 2, format);
        }

        public void Clip(float left, float top, float right, float bottom)
        {
            clipState = Graphics.Save();
            Graphics.SetClip(new RectangleF(left, top, right - left, bottom - top));
        }

        public void Unclip()
        {
            if (clipState != null)
                Graphics.Restore(clipState);
            clipState = null;
        }

        public void Image(int[] argb, int w, int h, float left, float top, float right, float bottom)
        {
            if (image == null || image.Width != w || image.Height != h)
            {
                image?.Dispose();
                image = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            }
            BitmapData d = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (d.Stride == w * 4)
                    Marshal.Copy(argb, 0, d.Scan0, w * h);
                else
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(argb, y * w, d.Scan0 + y * d.Stride, w);
            }
            finally
            {
                image.UnlockBits(d);
            }
            InterpolationMode im = Graphics.InterpolationMode;
            PixelOffsetMode pm = Graphics.PixelOffsetMode;
            Graphics.InterpolationMode = InterpolationMode.Bilinear;
            Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            Graphics.DrawImage(image, new RectangleF(left, top, right - left, bottom - top), new RectangleF(0, 0, w, h), GraphicsUnit.Pixel);
            Graphics.InterpolationMode = im;
            Graphics.PixelOffsetMode = pm;
        }

        public void Dispose()
        {
            foreach (Font f in fonts.Values)
                f.Dispose();
            fonts.Clear();
            pen.Dispose();
            brush.Dispose();
            format.Dispose();
            image?.Dispose();
        }
    }
}
