using System.Collections.Generic;
using Android.Graphics;
using Dds120.Display;

namespace Dds120.Droid.Screen
{
    // IScopeCanvas on an Android Canvas. Paints and the image bitmap are reused across frames.
    internal sealed class AndroidCanvas : IScopeCanvas
    {
        readonly Paint stroke = new Paint();
        readonly Paint fill = new Paint(PaintFlags.AntiAlias);
        readonly Paint dots = new Paint(PaintFlags.AntiAlias);
        readonly Paint text = new Paint(PaintFlags.AntiAlias | PaintFlags.SubpixelText);
        readonly Paint imagePaint = new Paint(PaintFlags.FilterBitmap);
        readonly Path path = new Path();
        readonly RectF rect = new RectF();
        readonly Rect source = new Rect();
        readonly Dictionary<long, float> baseline = new Dictionary<long, float>();
        Bitmap bitmap;

        public Canvas Canvas { get; set; }

        public AndroidCanvas()
        {
            stroke.SetStyle(Paint.Style.Stroke);
            fill.SetStyle(Paint.Style.Fill);
            dots.SetStyle(Paint.Style.Stroke);
            dots.StrokeCap = Paint.Cap.Round;
        }

        public void Clear(int color)
        {
            Canvas.DrawColor(new Color(color));
        }

        Paint Stroke(int color, float width, bool antialias)
        {
            stroke.Color = new Color(color);
            stroke.StrokeWidth = width;
            stroke.AntiAlias = antialias;
            return stroke;
        }

        public void Line(float x0, float y0, float x1, float y1, int color, float width)
        {
            Canvas.DrawLine(x0, y0, x1, y1, Stroke(color, width, true));
        }

        public void Segments(float[] xy, int count, int color, float width, bool antialias)
        {
            if (count >= 4)
                Canvas.DrawLines(xy, 0, count & ~3, Stroke(color, width, antialias));
        }

        public void Dots(float[] xy, int count, int color, float diameter)
        {
            dots.Color = new Color(color);
            dots.StrokeWidth = diameter;
            if (count >= 2)
                Canvas.DrawPoints(xy, 0, count & ~1, dots);
        }

        public void FillPolygon(float[] xy, int count, int color)
        {
            path.Reset();
            path.MoveTo(xy[0], xy[1]);
            for (int i = 2; i + 1 < count; i += 2)
                path.LineTo(xy[i], xy[i + 1]);
            path.Close();
            fill.Color = new Color(color);
            Canvas.DrawPath(path, fill);
        }

        public void FillRoundRect(float left, float top, float right, float bottom, float radius, int color)
        {
            rect.Set(left, top, right, bottom);
            fill.Color = new Color(color);
            Canvas.DrawRoundRect(rect, radius, radius, fill);
        }

        public void StrokeRect(float left, float top, float right, float bottom, int color, float width)
        {
            Canvas.DrawRect(left, top, right, bottom, Stroke(color, width, false));
        }

        Paint Text(FontKind font, float size)
        {
            text.SetTypeface(font == FontKind.Mono ? Typeface.Monospace : font == FontKind.SansBold ? Typeface.DefaultBold : Typeface.Default);
            text.TextSize = size;
            return text;
        }

        public float TextWidth(string s, FontKind font, float size)
        {
            return Text(font, size).MeasureText(s);
        }

        public void Text(string s, float x, float centerY, FontKind font, float size, int color, TextAlign align)
        {
            Paint p = Text(font, size);
            p.Color = new Color(color);
            p.TextAlign = align == TextAlign.Center ? Paint.Align.Center : align == TextAlign.Right ? Paint.Align.Right : Paint.Align.Left;
            long key = (long)font << 32 | (uint)(size * 16);
            float offset;
            if (!baseline.TryGetValue(key, out offset))
            {
                Paint.FontMetrics fm = p.GetFontMetrics();
                offset = -(fm.Ascent + fm.Descent) / 2;
                baseline[key] = offset;
            }
            Canvas.DrawText(s, x, centerY + offset, p);
        }

        public void Clip(float left, float top, float right, float bottom)
        {
            Canvas.Save();
            Canvas.ClipRect(left, top, right, bottom);
        }

        public void Unclip()
        {
            Canvas.Restore();
        }

        public void Image(int[] argb, int w, int h, float left, float top, float right, float bottom)
        {
            if (bitmap == null || bitmap.Width != w || bitmap.Height != h)
            {
                bitmap?.Recycle();
                bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888);
            }
            bitmap.SetPixels(argb, 0, w, 0, 0, w, h);
            source.Set(0, 0, w, h);
            rect.Set(left, top, right, bottom);
            Canvas.DrawBitmap(bitmap, source, rect, imagePaint);
        }
    }
}
