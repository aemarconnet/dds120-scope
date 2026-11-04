using System;
using System.Collections.Generic;
using Dds120.Scope;
using Dds120.Util;

namespace Dds120.Display
{
    // The oscilloscope screen, shared by the Windows and Android apps: graticule, traces, math
    // waveform, spectrum, XY plot, persistence, cursors, markers, status and measurement readouts,
    // plus dragging of the markers and cursors. All sizes are in dp and scaled by Density.
    public sealed class ScopeRenderer
    {
        public enum Drag { None, Ground0, Ground1, GroundMath, TriggerLevel, TriggerPosition, CursorX1, CursorX2, CursorY1, CursorY2 }

        // Divisions may stretch to fill a wide screen, up to this aspect ratio.
        const float MaxDivisionAspect = 1.6f;
        const float FftDbPerDiv = 10;

        Drag drag = Drag.None;
        float dragOffsetX, dragOffsetY;
        float gridLeft, gridTop, gridRight, gridBottom;
        float[] points = new float[4096];
        float[] segments = new float[8192];
        readonly float[] tag = new float[10];

        // persistence: per-cell intensity for CH1, CH2 and math at ~1 dp resolution
        readonly float[][] glow = new float[3][];
        int glowW, glowH;
        int[] glowPixels;
        Frame glowFrame;
        DateTime glowTime;
        string glowKey;

        public float Density { get; set; } = 1;   // pixels per dp
        public float Width { get; set; }          // pixels
        public float Height { get; set; }

        public Frame Frame { get; set; }
        public Analysis Analysis { get; set; }    // measurements, math and spectrum of Frame
        public ScopeSettings Settings { get; set; }
        public AcqStatus Status { get; set; }
        public string Message { get; set; }
        public string SourceName { get; set; }

        public Drag Dragging { get { return drag; } }

        // Live zoom while a pinch is in progress: >1 magnifies. The UI applies the nearest real
        // time/div or volts/div step when the fingers lift and then clears the preview.
        double previewTime = 1;
        readonly double[] previewVolts = { 1, 1, 1 };   // CH1, CH2, math

        public void SetPreview(double time, double ch1, double ch2, double math)
        {
            previewTime = time;
            previewVolts[0] = ch1;
            previewVolts[1] = ch2;
            previewVolts[2] = math;
        }

        public void ClearPreview()
        {
            SetPreview(1, 1, 1, 1);
        }

        double Vdiv(int ch)
        {
            double v = ch < 2 ? Settings.Channels[ch].VoltsPerDiv : Settings.MathScale;
            return v / previewVolts[ch];
        }

        // The part of the frame's record on screen, in samples. It follows the current settings, so
        // a stopped capture zooms and pans with time/div and the trigger point, and a change shows
        // at once instead of on the next capture.
        void ViewWindow(Frame f, out double start, out double samples)
        {
            samples = f.SampleRate * Settings.SecondsPerDiv * ScopeSettings.Divisions / previewTime;
            double anchor = f.Triggered ? f.TriggerIndex : f.ScreenStart + f.Settings.TriggerPosition * f.ScreenSamples;
            start = anchor - Settings.TriggerPosition * samples;
        }

        public float DivWidth { get { return GridWidth / ScopeSettings.Divisions; } }
        public float DivHeight { get { return DivH; } }

        public bool InGrid(float x, float y)
        {
            return x >= gridLeft && x <= gridRight && y >= gridTop && y <= gridBottom;
        }

        // Screen point to settings units: fraction of the width, divisions from the centre line.
        public double FractionAtX(float x) { return Math.Max(0, Math.Min(1, (x - gridLeft) / GridWidth)); }
        public double DivsAtY(float y) { return Math.Max(-4, Math.Min(4, DivsForY(y))); }

        // The trace (0 = CH1, 1 = CH2, 2 = math) drawn closest to a screen point, judged by where
        // the waveform is at that x, or by its ground marker when there is no capture.
        public int TraceNear(float x, float y)
        {
            int best = Settings.Channels[0].Enabled ? 0 : 1;
            float bestD = float.MaxValue;
            Frame f = Frame;
            Analysis a = CurrentAnalysis(f);
            for (int ch = 0; ch < 3; ch++)
            {
                if (ch < 2 ? !Settings.Channels[ch].Enabled : !Settings.MathTraceActive)
                    continue;
                double pos = ch < 2 ? Settings.Channels[ch].Position : Settings.MathPosition;
                float ty = YForDivs(pos);
                float[] v = f == null ? null : ch < 2 ? f.Volts[ch] : a != null ? a.MathWave : null;
                if (v != null)
                {
                    double start, samples;
                    ViewWindow(f, out start, out samples);
                    double idx = start + FractionAtX(x) * samples;
                    if (idx >= 0 && idx <= v.Length - 1)
                        ty = YForDivs(Interp(v, idx) / Vdiv(ch) + pos);
                }
                float d = Math.Abs(ty - y);
                if (d < bestD)
                {
                    bestD = d;
                    best = ch;
                }
            }
            return best;
        }

        // Graticule rectangle in pixels, as laid out by the last Draw.
        public float GridLeft { get { return gridLeft; } }
        public float GridTop { get { return gridTop; } }
        public float GridRight { get { return gridRight; } }
        public float GridBottom { get { return gridBottom; } }

        public event Action<int, double> GroundDragged;      // channel (2 = math), new position (div)
        public event Action<double> TriggerLevelDragged;     // volts
        public event Action<double> TriggerPositionDragged;  // fraction of screen
        public event Action<Drag, double> CursorDragged;     // X cursors: fraction of width; Y: div

        float Dp(float v) { return v * Density; }

        int BottomLines()
        {
            int lines = 0;
            foreach (ChannelSettings c in Settings.Channels)
                if (c.Enabled) lines++;
            if (Settings.MathTraceActive) lines++;
            if (Settings.FftActive) lines++;
            return Math.Max(1, lines);
        }

        void ComputeGrid()
        {
            float top = Dp(34), bottom = Dp(10 + 20 * BottomLines()), side = Dp(36);
            float w = Math.Max(10, Width - 2 * side);
            float h = Math.Max(10, Height - top - bottom);
            float dx = w / ScopeSettings.Divisions, dy = h / ScopeSettings.VerticalDivisions;
            if (dx > dy * MaxDivisionAspect) dx = dy * MaxDivisionAspect;
            if (dy > dx) dy = dx;
            float gw = dx * ScopeSettings.Divisions, gh = dy * ScopeSettings.VerticalDivisions;
            gridLeft = side + (w - gw) / 2;
            gridTop = top + (h - gh) / 2;
            gridRight = gridLeft + gw;
            gridBottom = gridTop + gh;
        }

        float GridWidth { get { return gridRight - gridLeft; } }
        float DivH { get { return (gridBottom - gridTop) / ScopeSettings.VerticalDivisions; } }
        float CenterY { get { return (gridTop + gridBottom) / 2; } }

        float YForDivs(double divs)
        {
            return (float)(CenterY - divs * DivH);
        }

        double DivsForY(float y)
        {
            return (CenterY - y) / DivH;
        }

        float TriggerY()
        {
            ChannelSettings c = Settings.Channels[Settings.TriggerSource];
            return YForDivs(Settings.TriggerLevel / Vdiv(Settings.TriggerSource) + c.Position);
        }

        float TriggerX()
        {
            return gridLeft + (float)(Settings.TriggerPosition * GridWidth);
        }

        Analysis CurrentAnalysis(Frame f)
        {
            Analysis a = Analysis;
            return a != null && a.Frame == f ? a : null;
        }

        public void Draw(IScopeCanvas g)
        {
            g.Clear(Palette.Screen);
            if (Settings == null)
                return;
            ComputeGrid();
            DrawGraticule(g);

            Frame f = Frame;
            Analysis a = CurrentAnalysis(f);
            bool xy = Settings.XYActive, fft = Settings.FftActive;
            if (f != null)
            {
                g.Clip(gridLeft, gridTop, gridRight, gridBottom);
                if (xy)
                {
                    DrawXY(g, f);
                }
                else if (fft)
                {
                    DrawSpectrum(g, a);
                }
                else
                {
                    DrawPersistence(g, f, a);
                    for (int ch = 1; ch >= 0; ch--) // CH1 on top
                    {
                        ChannelSettings c = Settings.Channels[ch];
                        if (f.Volts[ch] != null && c.Enabled)
                            DrawWave(g, f, f.Volts[ch], f.MinEnvelope[ch], f.MaxEnvelope[ch], Vdiv(ch), c.Position, Palette.Channel[ch]);
                    }
                    if (a != null && a.MathWave != null && Settings.MathTraceActive)
                        DrawWave(g, f, a.MathWave, null, null, Vdiv(2), Settings.MathPosition, Palette.Math);
                }
                if (!xy)
                    DrawCursors(g);
                g.Unclip();
            }
            else
            {
                DrawCenteredMessage(g);
            }

            if (!xy && !fft)
                DrawMarkers(g);
            DrawTopBar(g, f);
            DrawBottom(g, f, a);
            if (f != null && !xy)
                DrawCursorReadout(g, f, a);
        }

        void DrawGraticule(IScopeCanvas g)
        {
            float dx = GridWidth / ScopeSettings.Divisions, dy = DivH;
            int n = 0;
            float[] s = Reserve(ref segments, 4 * (ScopeSettings.Divisions + ScopeSettings.VerticalDivisions));
            for (int i = 1; i < ScopeSettings.Divisions; i++)
                n = Put(s, n, gridLeft + i * dx, gridTop, gridLeft + i * dx, gridBottom);
            for (int i = 1; i < ScopeSettings.VerticalDivisions; i++)
                n = Put(s, n, gridLeft, gridTop + i * dy, gridRight, gridTop + i * dy);
            g.Segments(s, n, Palette.Grid, Math.Max(1, Dp(0.6f)), false);

            float cx = (gridLeft + gridRight) / 2, cy = CenterY, tick = Dp(3);
            s = Reserve(ref segments, 4 * (5 * (ScopeSettings.Divisions + ScopeSettings.VerticalDivisions) + 2));
            n = 0;
            for (int i = 0; i <= ScopeSettings.Divisions * 5; i++)
            {
                float x = gridLeft + i * dx / 5;
                n = Put(s, n, x, cy - tick, x, cy + tick);
            }
            for (int i = 0; i <= ScopeSettings.VerticalDivisions * 5; i++)
            {
                float y = gridTop + i * dy / 5;
                n = Put(s, n, cx - tick, y, cx + tick, y);
            }
            g.Segments(s, n, Palette.GridAxis, Math.Max(1, Dp(0.6f)), false);
            g.StrokeRect(gridLeft, gridTop, gridRight, gridBottom, Palette.GridAxis, Math.Max(1, Dp(0.8f)));
        }

        // A waveform across the screen window. mn/mx (peak detect) draw a min/max envelope.
        void DrawWave(IScopeCanvas g, Frame f, float[] v, float[] mn, float[] mx, double vdiv, double pos, int color)
        {
            double start, samples;
            ViewWindow(f, out start, out samples);
            float width = GridWidth;
            double perPixel = samples / width;
            int i0 = Math.Max(0, (int)Math.Floor(start) - 1);
            int i1 = Math.Min(v.Length - 1, (int)Math.Ceiling(start + samples) + 1);
            if (i1 <= i0)
                return;
            float divH = DivH, cy = CenterY;
            float xScale = (float)(width / samples);
            int n = 0; // floats in points
            bool envelope = perPixel > 2 || mn != null;

            if (envelope)
            {
                // More samples than pixels (or peak detect): a min/max envelope per pixel column.
                int cols = (int)Math.Ceiling(width);
                float[] p = Reserve(ref points, 4 * (cols + 1));
                for (int col = 0; col <= cols; col++)
                {
                    int a = (int)Math.Floor(start + col * perPixel);
                    int b = (int)Math.Floor(start + (col + 1) * perPixel);
                    a = Math.Max(a, 0);
                    b = Math.Min(b, v.Length - 1);
                    if (b < a || b < 0 || a > v.Length - 1)
                        continue;
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int i = a; i <= b; i++)
                    {
                        float l = mn != null ? mn[i] : v[i], h = mx != null ? mx[i] : v[i];
                        if (l < lo) lo = l;
                        if (h > hi) hi = h;
                    }
                    float x = gridLeft + col;
                    p[n++] = x;
                    p[n++] = (float)(cy - (lo / vdiv + pos) * divH);
                    p[n++] = x;
                    p[n++] = (float)(cy - (hi / vdiv + pos) * divH);
                }
            }
            else
            {
                float[] p = Reserve(ref points, 2 * (i1 - i0 + 1));
                for (int i = i0; i <= i1; i++)
                {
                    p[n++] = gridLeft + (float)((i - start) * xScale);
                    p[n++] = (float)(cy - (v[i] / vdiv + pos) * divH);
                }
            }

            if (Settings.Dots)
            {
                g.Dots(points, n, color, Dp(envelope ? 1.5f : 3));
                return;
            }
            int count = n / 2;
            if (count >= 2)
            {
                // Polyline as segments between consecutive points.
                float[] s = Reserve(ref segments, 4 * (count - 1));
                int m = 0;
                for (int k = 0; k + 3 < n; k += 2)
                {
                    s[m++] = points[k];
                    s[m++] = points[k + 1];
                    s[m++] = points[k + 2];
                    s[m++] = points[k + 3];
                }
                if (envelope)
                    g.Segments(s, m, color, Math.Max(1, Dp(1)), false);
                else
                    g.Segments(s, m, color, Dp(1.5f), true);
            }

            // Show the actual sample points when they are far apart.
            if (perPixel < 0.12 && !envelope)
                g.Dots(points, n, color, Dp(4));
        }

        // CH1 horizontally, CH2 vertically, each at its own volts/div; positions shift the plot.
        void DrawXY(IScopeCanvas g, Frame f)
        {
            float[] x = f.Volts[0], y = f.Volts[1];
            if (x == null || y == null)
                return;
            ChannelSettings c1 = Settings.Channels[0], c2 = Settings.Channels[1];
            double v1 = Vdiv(0), v2 = Vdiv(1);
            float dx = GridWidth / ScopeSettings.Divisions, dy = DivH;
            float cx = (gridLeft + gridRight) / 2, cy = CenterY;
            int len = Math.Min(x.Length, y.Length);
            int stride = Math.Max(1, len / 8000);
            float[] p = Reserve(ref points, 2 * (len / stride + 2));
            int n = 0;
            for (int i = 0; i < len; i += stride)
            {
                p[n++] = (float)(cx + (x[i] / v1 + c1.Position) * dx);
                p[n++] = (float)(cy - (y[i] / v2 + c2.Position) * dy);
            }
            if (Settings.Dots)
            {
                g.Dots(p, n, Palette.XY, Dp(2));
                return;
            }
            float[] s = Reserve(ref segments, 2 * n);
            int m = 0;
            for (int k = 0; k + 3 < n; k += 2)
            {
                s[m++] = p[k];
                s[m++] = p[k + 1];
                s[m++] = p[k + 2];
                s[m++] = p[k + 3];
            }
            g.Segments(s, m, Palette.XY, Dp(1.2f), true);
        }

        // ---- spectrum -------------------------------------------------------------------------

        // dBV at the top graticule line: a sine filling the 8 divisions of the source reads there.
        double SpectrumTop()
        {
            ChannelSettings c = Settings.Channels[Settings.FftSource];
            double rms = 4 * c.VoltsPerDiv / Math.Sqrt(2);
            return Math.Ceiling(20 * Math.Log10(rms) / FftDbPerDiv) * FftDbPerDiv;
        }

        double SpectrumSpan(Spectrum sp)
        {
            return sp.NyquistHz / Math.Max(1, Settings.FftZoom) / previewTime;
        }

        float YForDb(double db)
        {
            return (float)(gridTop + (SpectrumTop() - db) / FftDbPerDiv * DivH);
        }

        static double DbAt(Spectrum sp, double hz)
        {
            double k = hz / sp.BinHz;
            int i = (int)Math.Floor(k);
            if (i < 0) return sp.Dbv[0];
            if (i >= sp.Dbv.Length - 1) return sp.Dbv[sp.Dbv.Length - 1];
            return sp.Dbv[i] + (sp.Dbv[i + 1] - sp.Dbv[i]) * (k - i);
        }

        void DrawSpectrum(IScopeCanvas g, Analysis a)
        {
            Spectrum sp = a != null ? a.Spectrum : null;
            if (sp == null)
                return;
            double span = SpectrumSpan(sp);
            float w = GridWidth;
            int cols = (int)Math.Ceiling(w);
            float[] p = Reserve(ref points, 2 * (cols + 1));
            int n = 0;
            double hzPerPx = span / w;
            for (int col = 0; col <= cols; col++)
            {
                double f0 = col * hzPerPx, f1 = f0 + hzPerPx;
                int k0 = (int)Math.Floor(f0 / sp.BinHz), k1 = (int)Math.Floor(f1 / sp.BinHz);
                double db;
                if (k1 <= k0)
                {
                    db = DbAt(sp, f0); // fewer bins than pixels: interpolate
                }
                else
                {
                    db = double.MinValue;
                    for (int k = k0; k < k1 && k < sp.Dbv.Length; k++)
                        db = Math.Max(db, sp.Dbv[k]);
                    if (db == double.MinValue)
                        continue;
                }
                p[n++] = gridLeft + col;
                p[n++] = YForDb(db);
            }
            float[] s = Reserve(ref segments, 2 * n);
            int m = 0;
            for (int k = 0; k + 3 < n; k += 2)
            {
                s[m++] = p[k];
                s[m++] = p[k + 1];
                s[m++] = p[k + 2];
                s[m++] = p[k + 3];
            }
            if (Settings.Dots)
                g.Dots(p, n, Palette.Math, Dp(2));
            else
                g.Segments(s, m, Palette.Math, Dp(1.3f), true);

            // marker on the strongest component
            if (!double.IsNaN(sp.PeakHz) && sp.PeakHz <= span)
            {
                float x = gridLeft + (float)(sp.PeakHz / span * w), y = YForDb(sp.PeakDbv);
                float[] tri = { x - Dp(6), y - Dp(14), x + Dp(6), y - Dp(14), x, y - Dp(4) };
                g.FillPolygon(tri, 6, Palette.White);
            }
        }

        // ---- persistence ----------------------------------------------------------------------

        // Each new frame fades the older traces and stamps itself into a per-channel intensity map,
        // drawn under the live traces. Anything that moves the traces clears it.
        void DrawPersistence(IScopeCanvas g, Frame f, Analysis a)
        {
            double keep = ScopeSettings.PersistenceTimes[Settings.PersistenceIndex];
            if (keep <= 0)
            {
                glowKey = null;
                return;
            }
            int w = Math.Max(1, (int)(GridWidth / Density)), h = Math.Max(1, (int)((gridBottom - gridTop) / Density));
            string key = PersistenceKey(w, h);
            if (key != glowKey)
            {
                glowKey = key;
                glowW = w;
                glowH = h;
                for (int i = 0; i < glow.Length; i++)
                    glow[i] = new float[w * h];
                glowPixels = new int[w * h];
                glowFrame = null;
            }
            if (f != glowFrame)
            {
                DateTime now = DateTime.UtcNow;
                if (!double.IsInfinity(keep) && glowFrame != null)
                {
                    float k = (float)Math.Exp(-(now - glowTime).TotalSeconds / (keep / 3));
                    foreach (float[] buf in glow)
                        for (int i = 0; i < buf.Length; i++)
                            buf[i] *= k;
                }
                glowFrame = f;
                glowTime = now;
                for (int ch = 0; ch < 2; ch++)
                {
                    ChannelSettings c = Settings.Channels[ch];
                    if (c.Enabled && f.Volts[ch] != null)
                        Stamp(glow[ch], f, f.Volts[ch], f.MinEnvelope[ch], f.MaxEnvelope[ch], Vdiv(ch), c.Position);
                }
                if (a != null && a.MathWave != null && Settings.MathTraceActive)
                    Stamp(glow[2], f, a.MathWave, null, null, Vdiv(2), Settings.MathPosition);
                Compose();
            }
            g.Image(glowPixels, glowW, glowH, gridLeft, gridTop, gridRight, gridBottom);
        }

        string PersistenceKey(int w, int h)
        {
            ScopeSettings s = Settings;
            var sb = new System.Text.StringBuilder();
            sb.Append(w).Append('x').Append(h).Append('|').Append(s.TimebaseIndex).Append('|').Append(s.TriggerPosition)
              .Append('|').Append(previewTime).Append(previewVolts[0]).Append(previewVolts[1]).Append(previewVolts[2])
              .Append('|').Append(s.Acquire).Append('|').Append(s.MathTraceActive ? s.MathMode.ToString() : "-")
              .Append('|').Append(s.MathScaleIndex).Append('|').Append(s.MathPosition);
            foreach (ChannelSettings c in s.Channels)
                sb.Append('|').Append(c.Enabled).Append(c.GainIndex).Append(c.Probe).Append(c.Position).Append(c.Inverted).Append(c.AcCoupling);
            return sb.ToString();
        }

        void Stamp(float[] buf, Frame f, float[] v, float[] mn, float[] mx, double vdiv, double pos)
        {
            int w = glowW, h = glowH;
            double start, samples;
            ViewWindow(f, out start, out samples);
            double perCol = samples / w;
            double cellsPerDiv = h / (double)ScopeSettings.VerticalDivisions, cy = h / 2.0;
            for (int col = 0; col < w; col++)
            {
                double t0 = start + col * perCol, t1 = t0 + perCol;
                if (t1 < 0 || t0 > v.Length - 1)
                    continue;
                double lo = Interp(v, t0), hi = lo, e = Interp(v, t1);
                lo = Math.Min(lo, e);
                hi = Math.Max(hi, e);
                int a = Math.Max(0, (int)Math.Ceiling(t0)), b = Math.Min(v.Length - 1, (int)Math.Floor(t1));
                for (int i = a; i <= b; i++)
                {
                    lo = Math.Min(lo, mn != null ? mn[i] : v[i]);
                    hi = Math.Max(hi, mx != null ? mx[i] : v[i]);
                }
                int y0 = (int)Math.Floor(cy - (hi / vdiv + pos) * cellsPerDiv);
                int y1 = (int)Math.Floor(cy - (lo / vdiv + pos) * cellsPerDiv);
                if (y1 < 0 || y0 >= h)
                    continue;
                y0 = Math.Max(0, y0);
                y1 = Math.Min(h - 1, y1);
                for (int y = y0; y <= y1; y++)
                    buf[y * w + col] = 1;
            }
        }

        static double Interp(float[] v, double t)
        {
            if (t <= 0) return v[0];
            int i = (int)t;
            if (i >= v.Length - 1) return v[v.Length - 1];
            return v[i] + (v[i + 1] - v[i]) * (t - i);
        }

        void Compose()
        {
            int[] colors = { Palette.Channel[0], Palette.Channel[1], Palette.Math };
            int n = glowW * glowH;
            for (int i = 0; i < n; i++)
            {
                float r = 0, gr = 0, b = 0, sum = 0, peak = 0;
                for (int s = 0; s < 3; s++)
                {
                    float v = glow[s][i];
                    if (v < 0.004f)
                        continue;
                    int c = colors[s];
                    r += (c >> 16 & 255) * v;
                    gr += (c >> 8 & 255) * v;
                    b += (c & 255) * v;
                    sum += v;
                    if (v > peak) peak = v;
                }
                if (sum <= 0)
                {
                    glowPixels[i] = 0;
                    continue;
                }
                int alpha = (int)(Math.Min(1f, peak) * 170);
                glowPixels[i] = alpha << 24 | (int)(r / sum) << 16 | (int)(gr / sum) << 8 | (int)(b / sum);
            }
        }

        public void ClearPersistence()
        {
            glowKey = null;
        }

        // ---- cursors --------------------------------------------------------------------------

        bool CursorsShown { get { return Settings.Cursors != CursorMode.Off && !Settings.XYActive; } }
        bool TimeCursors { get { return Settings.Cursors == CursorMode.Time || Settings.Cursors == CursorMode.Both; } }
        bool LevelCursors { get { return Settings.Cursors == CursorMode.Amplitude || Settings.Cursors == CursorMode.Both; } }

        int CursorColor
        {
            get
            {
                if (Settings.FftActive || Settings.CursorSource == 2)
                    return Palette.Math;
                return Palette.Channel[Settings.CursorSource];
            }
        }

        float CursorX(double frac) { return gridLeft + (float)(frac * GridWidth); }

        void DrawCursors(IScopeCanvas g)
        {
            if (!CursorsShown)
                return;
            int color = Palette.WithAlpha(CursorColor, 230);
            float size = Dp(11);
            if (TimeCursors)
            {
                float x1 = CursorX(Settings.CursorX1), x2 = CursorX(Settings.CursorX2);
                DashedLine(g, x1, gridTop, x1, gridBottom, color);
                DashedLine(g, x2, gridTop, x2, gridBottom, color);
                g.Text("1", x1 + Dp(4), gridBottom - Dp(9), FontKind.SansBold, size, color, TextAlign.Left);
                g.Text("2", x2 + Dp(4), gridBottom - Dp(9), FontKind.SansBold, size, color, TextAlign.Left);
            }
            if (LevelCursors)
            {
                float y1 = YForDivs(Settings.CursorY1), y2 = YForDivs(Settings.CursorY2);
                DashedLine(g, gridLeft, y1, gridRight, y1, color);
                DashedLine(g, gridLeft, y2, gridRight, y2, color);
                g.Text("1", gridRight - Dp(6), y1 - Dp(8), FontKind.SansBold, size, color, TextAlign.Right);
                g.Text("2", gridRight - Dp(6), y2 - Dp(8), FontKind.SansBold, size, color, TextAlign.Right);
            }
        }

        // The cursor source waveform (null if that channel is off) and its unit.
        float[] CursorWave(Frame f, Analysis a, out string unit)
        {
            unit = "V";
            if (Settings.CursorSource == 2)
            {
                unit = Settings.MathUnit;
                return a != null ? a.MathWave : null;
            }
            return f.Volts[Settings.CursorSource];
        }

        double CursorLevel(double div, out string unit)
        {
            unit = "V";
            if (Settings.CursorSource == 2)
            {
                unit = Settings.MathUnit;
                return (div - Settings.MathPosition) * Vdiv(2);
            }
            ChannelSettings c = Settings.Channels[Settings.CursorSource];
            return (div - c.Position) * Vdiv(Settings.CursorSource);
        }

        void DrawCursorReadout(IScopeCanvas g, Frame f, Analysis a)
        {
            if (!CursorsShown)
                return;
            var lines = new List<string>();
            if (Settings.FftActive)
            {
                Spectrum sp = a != null ? a.Spectrum : null;
                if (sp == null)
                    return;
                double span = SpectrumSpan(sp), topDb = SpectrumTop();
                if (TimeCursors)
                {
                    double f1 = Settings.CursorX1 * span, f2 = Settings.CursorX2 * span;
                    lines.Add(string.Format("f1 {0}   f2 {1}   Δf {2}", Si.Format(f1, "Hz"), Si.Format(f2, "Hz"), Si.Format(f2 - f1, "Hz")));
                    lines.Add(string.Format("@f1 {0}   @f2 {1}", Db(DbAt(sp, f1)), Db(DbAt(sp, f2))));
                }
                if (LevelCursors)
                {
                    double l1 = topDb - (4 - Settings.CursorY1) * FftDbPerDiv, l2 = topDb - (4 - Settings.CursorY2) * FftDbPerDiv;
                    lines.Add(string.Format("L1 {0}   L2 {1}   ΔL {2:F1} dB", Db(l1), Db(l2), l2 - l1));
                }
            }
            else
            {
                if (TimeCursors)
                {
                    double start, samples;
                    ViewWindow(f, out start, out samples);
                    double screenTime = samples / f.SampleRate;
                    double t1 = (Settings.CursorX1 - Settings.TriggerPosition) * screenTime;
                    double t2 = (Settings.CursorX2 - Settings.TriggerPosition) * screenTime;
                    double dt = t2 - t1;
                    lines.Add(string.Format("t1 {0}   t2 {1}", Si.Format(t1, "s"), Si.Format(t2, "s")));
                    lines.Add(string.Format("Δt {0}   1/Δt {1}", Si.Format(dt, "s"), Si.Format(dt == 0 ? double.NaN : 1 / Math.Abs(dt), "Hz")));
                    string unit;
                    float[] w = CursorWave(f, a, out unit);
                    if (w != null)
                    {
                        double v1 = Interp(w, start + Settings.CursorX1 * samples);
                        double v2 = Interp(w, start + Settings.CursorX2 * samples);
                        lines.Add(string.Format("@t1 {0}   @t2 {1}   Δ {2}", Si.Format(v1, unit), Si.Format(v2, unit), Si.Format(v2 - v1, unit)));
                    }
                }
                if (LevelCursors)
                {
                    string unit;
                    double v1 = CursorLevel(Settings.CursorY1, out unit), v2 = CursorLevel(Settings.CursorY2, out unit);
                    lines.Add(string.Format("V1 {0}   V2 {1}   ΔV {2}", Si.Format(v1, unit), Si.Format(v2, unit), Si.Format(v2 - v1, unit)));
                }
            }
            if (lines.Count == 0)
                return;
            float size = Dp(11.5f), lineH = Dp(16), pad = Dp(6);
            float widest = 0;
            foreach (string line in lines)
                widest = Math.Max(widest, g.TextWidth(line, FontKind.Mono, size));
            float left = gridLeft + Dp(6), top = gridTop + Dp(6);
            g.FillRoundRect(left, top, left + widest + 2 * pad, top + lines.Count * lineH + pad, Dp(4), Palette.WithAlpha(Palette.Panel, 225));
            float y = top + pad / 2 + lineH / 2;
            foreach (string line in lines)
            {
                g.Text(line, left + pad, y, FontKind.Mono, size, CursorColor, TextAlign.Left);
                y += lineH;
            }
        }

        static string Db(double db)
        {
            return double.IsNaN(db) ? "--- dBV" : db.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " dBV";
        }

        // ---- text and markers -----------------------------------------------------------------

        void DrawCenteredMessage(IScopeCanvas g)
        {
            if (Status == AcqStatus.NoDevice)
            {
                DrawNoScope(g);
                return;
            }
            string text = Message ?? "";
            if (Status == AcqStatus.Waiting)
                text = "Waiting for trigger...";
            float size = Dp(15);
            List<string> lines = Wrap(g, text, FontKind.Sans, size, GridWidth - Dp(32));
            if (text.Length == 0)
                return;
            float lineH = size * 1.4f;
            float y = CenterY - (lines.Count - 1) * lineH / 2;
            // a dark backdrop keeps the text readable over the graticule
            float widest = 0;
            foreach (string line in lines)
                widest = Math.Max(widest, g.TextWidth(line, FontKind.Sans, size));
            float cx = (gridLeft + gridRight) / 2, pad = Dp(12);
            g.FillRoundRect(cx - widest / 2 - pad, y - lineH / 2 - pad / 2, cx + widest / 2 + pad,
                y + (lines.Count - 0.5f) * lineH + pad / 2, Dp(6), Palette.WithAlpha(Palette.Screen, 235));
            foreach (string line in lines)
            {
                g.Text(line, (gridLeft + gridRight) / 2, y, FontKind.Sans, size, Palette.TextDim, TextAlign.Center);
                y += lineH;
            }
        }

        // The warning card while no scope is connected: what is wrong, what to do, and that the demo
        // signal is there to try the app without one.
        void DrawNoScope(IScopeCanvas g)
        {
            const string title = "No scope connected";
            const string hint = "To try the app without one, turn on the demo signal in the Tools tab.";
            float titleSize = Dp(17), size = Dp(14), pad = Dp(18), icon = Dp(30), lineH = size * 1.45f;
            float width = Math.Min(GridWidth - Dp(32), Dp(560));
            float textW = width - 2 * pad;
            List<string> body = string.IsNullOrEmpty(Message) ? new List<string>() : Wrap(g, Message, FontKind.Sans, size, textW);
            List<string> hints = Wrap(g, hint, FontKind.Sans, size, textW);
            float height = pad + icon + Dp(12) + titleSize * 1.4f + Dp(6) + body.Count * lineH + Dp(10) + hints.Count * lineH + pad;
            float cx = (gridLeft + gridRight) / 2, left = cx - width / 2, top = CenterY - height / 2;
            g.FillRoundRect(left, top, left + width, top + height, Dp(10), Palette.WithAlpha(Palette.Panel, 245));

            float iy = top + pad;
            float[] tri = { cx, iy, cx + icon * 0.58f, iy + icon, cx - icon * 0.58f, iy + icon };
            g.FillPolygon(tri, 6, Palette.Wait);
            g.Text("!", cx, iy + icon * 0.62f, FontKind.SansBold, icon * 0.6f, Palette.Black, TextAlign.Center);

            float y = iy + icon + Dp(12) + titleSize * 0.7f;
            g.Text(title, cx, y, FontKind.SansBold, titleSize, Palette.Text, TextAlign.Center);
            y += titleSize * 0.7f + Dp(6) + lineH / 2;
            foreach (string line in body)
            {
                g.Text(line, cx, y, FontKind.Sans, size, Palette.Text, TextAlign.Center);
                y += lineH;
            }
            y += Dp(10);
            foreach (string line in hints)
            {
                g.Text(line, cx, y, FontKind.Sans, size, Palette.TextDim, TextAlign.Center);
                y += lineH;
            }
        }

        void DrawMarkers(IScopeCanvas g)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                ChannelSettings c = Settings.Channels[ch];
                if (!c.Enabled)
                    continue;
                float y = Clamp(YForDivs(c.Position), gridTop, gridBottom);
                DrawTag(g, (ch + 1).ToString(), Palette.Channel[ch], gridLeft - Dp(2), y, true);
            }
            if (Settings.MathTraceActive)
                DrawTag(g, "M", Palette.Math, gridLeft - Dp(2), Clamp(YForDivs(Settings.MathPosition), gridTop, gridBottom), true);
            if (Settings.Channels[Settings.TriggerSource].Enabled)
            {
                float ty = Clamp(TriggerY(), gridTop, gridBottom);
                int color = Palette.Channel[Settings.TriggerSource];
                DrawTag(g, "T", color, gridRight + Dp(2), ty, false);
                if (drag == Drag.TriggerLevel)
                    DashedLine(g, gridLeft, ty, gridRight, ty, Palette.WithAlpha(color, 170));
            }
            // trigger position: small triangle above the grid
            float tx = TriggerX();
            float[] tri = { tx - Dp(7), gridTop - Dp(10), tx + Dp(7), gridTop - Dp(10), tx, gridTop - 1 };
            g.FillPolygon(tri, 6, Palette.Accent);
            if (drag == Drag.TriggerPosition)
                DashedLine(g, tx, gridTop, tx, gridBottom, Palette.WithAlpha(Palette.Accent, 170));
        }

        // A pointed label: on the left edge pointing right (ground markers) or on the right edge pointing left.
        void DrawTag(IScopeCanvas g, string text, int color, float x, float y, bool pointRight)
        {
            float w = Dp(22), h = Dp(18), tip = Dp(7);
            float d = pointRight ? -1 : 1;
            float[] t = tag;
            t[0] = x + d * (w + tip); t[1] = y - h / 2;
            t[2] = x + d * tip; t[3] = y - h / 2;
            t[4] = x; t[5] = y;
            t[6] = x + d * tip; t[7] = y + h / 2;
            t[8] = x + d * (w + tip); t[9] = y + h / 2;
            g.FillPolygon(t, 10, color);
            g.Text(text, x + d * (tip + w / 2), y, FontKind.SansBold, Dp(12), Palette.Black, TextAlign.Center);
        }

        void DashedLine(IScopeCanvas g, float x0, float y0, float x1, float y1, int color)
        {
            float len = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            if (len < 1)
                return;
            float on = Dp(5), period = Dp(9);
            int dashes = (int)(len / period) + 1;
            float[] s = Reserve(ref segments, 4 * dashes);
            int n = 0;
            for (float d = 0; d < len; d += period)
            {
                float e = Math.Min(len, d + on);
                n = Put(s, n, x0 + (x1 - x0) * d / len, y0 + (y1 - y0) * d / len, x0 + (x1 - x0) * e / len, y0 + (y1 - y0) * e / len);
            }
            g.Segments(s, n, color, Math.Max(1, Dp(1)), false);
        }

        string AcquireLabel(Frame f)
        {
            switch (Settings.Acquire)
            {
                case AcquireMode.Sample: return "Sample";
                case AcquireMode.PeakDetect: return "Peak";
                case AcquireMode.Average:
                    return f != null && f.Averaged > 0 ? "Avg " + f.Averaged + "/" + Settings.AverageCount : "Avg " + Settings.AverageCount;
                default: return "Hi-Res";
            }
        }

        static string CouplingLabel(TriggerCoupling c)
        {
            switch (c)
            {
                case TriggerCoupling.AC: return " AC";
                case TriggerCoupling.NoiseReject: return " NR";
                case TriggerCoupling.HFReject: return " HFR";
                case TriggerCoupling.LFReject: return " LFR";
                default: return "";
            }
        }

        void DrawTopBar(IScopeCanvas g, Frame f)
        {
            string badge;
            int badgeColor;
            switch (Status)
            {
                case AcqStatus.Triggered: badge = "TRIG'D"; badgeColor = Palette.Run; break;
                case AcqStatus.Untriggered: badge = "AUTO"; badgeColor = Palette.Run; break;
                case AcqStatus.Waiting: badge = "WAIT"; badgeColor = Palette.Wait; break;
                case AcqStatus.Stopped: badge = "STOP"; badgeColor = Palette.Stop; break;
                case AcqStatus.Calibrating: badge = "CAL"; badgeColor = Palette.Accent; break;
                case AcqStatus.Autoset: badge = "AUTOSET"; badgeColor = Palette.Accent; break;
                case AcqStatus.Connecting: badge = "CONNECT"; badgeColor = Palette.Wait; break;
                default: badge = "NO SCOPE"; badgeColor = Palette.Stop; break;
            }
            float size = Dp(12);
            float top = Dp(5), h = Dp(20), cy = top + h / 2;
            float bw = g.TextWidth(badge, FontKind.SansBold, size) + Dp(14);
            g.FillRoundRect(gridLeft, top, gridLeft + bw, top + h, Dp(4), badgeColor);
            g.Text(badge, gridLeft + Dp(7), cy, FontKind.SansBold, size, Palette.White, TextAlign.Left);

            double rate = f != null ? f.SampleRate : Settings.ChooseRate().SampleRate;
            string info = string.Format("{0}/div   {1}   T: CH{2} {3} {4}{5}   {6}   {7}",
                Si.Short(Settings.SecondsPerDiv, "s"), Si.Format(rate, "S/s"),
                Settings.TriggerSource + 1, Settings.TriggerEdge == TriggerEdge.Rising ? "↑" : "↓",
                Si.Format(Settings.TriggerLevel, "V"), CouplingLabel(Settings.TriggerCoupling),
                Settings.TriggerMode == TriggerMode.Auto ? "Auto" : "Normal", AcquireLabel(f));
            if (Settings.XYActive)
                info += "   XY";
            float ix = gridLeft + bw + Dp(10);
            g.Text(info, ix, cy, FontKind.Sans, size, Palette.Text, TextAlign.Left);

            float room = gridRight - (ix + g.TextWidth(info, FontKind.Sans, size) + Dp(16));
            if (!string.IsNullOrEmpty(SourceName) && room > Dp(48))
                g.Text(Ellipsize(g, SourceName, FontKind.Sans, size, room), gridRight, cy, FontKind.Sans, size, Palette.TextDim, TextAlign.Right);
        }

        void DrawBottom(IScopeCanvas g, Frame f, Analysis a)
        {
            float size = Dp(12);
            var heads = new string[4];
            var colors = new int[4];
            var meas = new ChannelMeasurement[4];
            var units = new string[4];
            for (int ch = 0; ch < 2; ch++)
            {
                ChannelSettings c = Settings.Channels[ch];
                if (!c.Enabled)
                    continue;
                heads[ch] = string.Format("CH{0} {1}/div {2}{3}{4}", ch + 1, Si.Short(c.VoltsPerDiv, "V"),
                    c.AcCoupling ? "AC" : "DC", c.Probe == 10 ? " x10" : "", c.Inverted ? " INV" : "");
                colors[ch] = Palette.Channel[ch];
                meas[ch] = a != null && f != null && f.Volts[ch] != null ? a.Channels[ch] : null;
                units[ch] = "V";
            }
            if (Settings.MathTraceActive)
            {
                heads[2] = string.Format("M {0} {1}/div", Settings.MathName, Si.Short(Settings.MathScale, Settings.MathUnit));
                colors[2] = Palette.Math;
                meas[2] = a != null ? a.MathMeasurement : null;
                units[2] = Settings.MathUnit;
            }
            if (Settings.FftActive)
            {
                heads[3] = string.Format("{0} {1}{2}", Settings.MathName, Settings.FftWindow,
                    Settings.FftZoom > 1 ? " ×" + Settings.FftZoom : "");
                colors[3] = Palette.Math;
            }

            float headW = 0;
            foreach (string h in heads)
                if (h != null)
                    headW = Math.Max(headW, g.TextWidth(h, FontKind.SansBold, size));

            float y = gridBottom + Dp(18);
            for (int line = 0; line < 4; line++)
            {
                if (heads[line] == null)
                    continue;
                g.Text(heads[line], gridLeft, y, FontKind.SansBold, size, colors[line], TextAlign.Left);
                float limit = gridRight;
                if (line < 2 && f != null && f.Clipped[line])
                {
                    g.Text("CLIPPED", gridRight, y, FontKind.SansBold, size, Palette.Warning, TextAlign.Right);
                    limit -= g.TextWidth("CLIPPED", FontKind.SansBold, size) + Dp(12);
                }
                var items = new List<string>();
                if (line == 3)
                {
                    Spectrum sp = a != null ? a.Spectrum : null;
                    if (sp != null)
                    {
                        items.Add("Peak " + Si.Format(sp.PeakHz, "Hz", 4) + " " + Db(sp.PeakDbv));
                        items.Add("Span " + Si.Format(SpectrumSpan(sp), "Hz"));
                        items.Add("Top " + Db(SpectrumTop()));
                        items.Add("10 dB/div");
                        items.Add("RBW " + Si.Format(sp.BinHz, "Hz"));
                    }
                }
                else if (meas[line] != null)
                {
                    foreach (MeasureType t in Settings.MeasureItems)
                        if (!Measurements.IsCrossChannel(t) || line == 1)
                            items.Add(meas[line].Format(t, units[line]));
                }
                // most important first; whatever does not fit the width is left out
                float x = gridLeft + headW + Dp(16), gap = Dp(14);
                foreach (string item in items)
                {
                    float w = g.TextWidth(item, FontKind.Mono, size);
                    if (x + w > limit)
                        break;
                    g.Text(item, x, y, FontKind.Mono, size, Palette.TextDim, TextAlign.Left);
                    x += w + gap;
                }
                y += Dp(20);
            }
        }

        // ---- touch and mouse ------------------------------------------------------------------

        Drag HitTest(float x, float y, out float markerX, out float markerY)
        {
            markerX = x;
            markerY = y;
            if (Settings == null)
                return Drag.None;
            bool xy = Settings.XYActive, fft = Settings.FftActive;
            float tol = Dp(24);
            if (!xy && !fft && x < gridLeft + Dp(20) && x > gridLeft - Dp(44))
            {
                // prefer the marker nearest to the finger
                Drag best = Drag.None;
                float bestD = tol;
                for (int ch = 0; ch < 3; ch++)
                {
                    if (ch < 2 ? !Settings.Channels[ch].Enabled : !Settings.MathTraceActive)
                        continue;
                    double pos = ch < 2 ? Settings.Channels[ch].Position : Settings.MathPosition;
                    float my = Clamp(YForDivs(pos), gridTop, gridBottom);
                    float d = Math.Abs(y - my);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = ch == 0 ? Drag.Ground0 : ch == 1 ? Drag.Ground1 : Drag.GroundMath;
                        markerY = my;
                    }
                }
                if (best != Drag.None)
                    return best;
            }
            if (!xy && !fft && Settings.Channels[Settings.TriggerSource].Enabled &&
                x > gridRight - Dp(20) && x < gridRight + Dp(44))
            {
                float ty = Clamp(TriggerY(), gridTop, gridBottom);
                if (Math.Abs(y - ty) < tol)
                {
                    markerY = ty;
                    return Drag.TriggerLevel;
                }
            }
            float tx = TriggerX();
            if (!xy && !fft && y < gridTop + Dp(20) && y > gridTop - Dp(34) && Math.Abs(x - tx) < tol)
            {
                markerX = tx;
                return Drag.TriggerPosition;
            }
            if (CursorsShown && x > gridLeft - Dp(8) && x < gridRight + Dp(8) && y > gridTop - Dp(8) && y < gridBottom + Dp(8))
            {
                Drag best = Drag.None;
                float bestD = Dp(16);
                if (TimeCursors)
                {
                    float x1 = CursorX(Settings.CursorX1), x2 = CursorX(Settings.CursorX2);
                    if (Math.Abs(x - x2) < bestD) { bestD = Math.Abs(x - x2); best = Drag.CursorX2; markerX = x2; }
                    if (Math.Abs(x - x1) < bestD) { bestD = Math.Abs(x - x1); best = Drag.CursorX1; markerX = x1; }
                }
                if (LevelCursors)
                {
                    float y1 = YForDivs(Settings.CursorY1), y2 = YForDivs(Settings.CursorY2);
                    if (Math.Abs(y - y2) < bestD) { bestD = Math.Abs(y - y2); best = Drag.CursorY2; markerX = x; markerY = y2; }
                    if (Math.Abs(y - y1) < bestD) { best = Drag.CursorY1; markerX = x; markerY = y1; }
                }
                return best;
            }
            return Drag.None;
        }

        // What a press at this point would drag (for hover feedback).
        public Drag HitTestAt(float x, float y)
        {
            float mx, my;
            return HitTest(x, y, out mx, out my);
        }

        // Starts dragging a marker or cursor under the finger; returns false if there is none.
        public bool TouchDown(float x, float y)
        {
            float mx, my;
            drag = HitTest(x, y, out mx, out my);
            // Keep the marker where it is relative to the finger instead of jumping under it.
            dragOffsetX = mx - x;
            dragOffsetY = my - y;
            return drag != Drag.None;
        }

        public void TouchMove(float x, float y)
        {
            if (drag == Drag.None)
                return;
            float py = Clamp(y + dragOffsetY, gridTop, gridBottom);
            float px = Clamp(x + dragOffsetX, gridLeft, gridRight);
            switch (drag)
            {
                case Drag.Ground0:
                case Drag.Ground1:
                case Drag.GroundMath:
                    // snap to 0.1 div
                    double d = Math.Round(DivsForY(py) * 10) / 10;
                    GroundDragged?.Invoke(drag == Drag.Ground0 ? 0 : drag == Drag.Ground1 ? 1 : 2, d);
                    break;
                case Drag.TriggerLevel:
                    {
                        ChannelSettings c = Settings.Channels[Settings.TriggerSource];
                        double volts = (DivsForY(py) - c.Position) * c.VoltsPerDiv;
                        double step = c.VoltsPerDiv / 50;
                        TriggerLevelDragged?.Invoke(Math.Round(volts / step) * step);
                        break;
                    }
                case Drag.TriggerPosition:
                    TriggerPositionDragged?.Invoke(Math.Round((px - gridLeft) / GridWidth * 100) / 100);
                    break;
                case Drag.CursorX1:
                case Drag.CursorX2:
                    CursorDragged?.Invoke(drag, Math.Round((px - gridLeft) / GridWidth * 1000) / 1000);
                    break;
                case Drag.CursorY1:
                case Drag.CursorY2:
                    CursorDragged?.Invoke(drag, Math.Round(DivsForY(py) * 50) / 50);
                    break;
            }
        }

        public void TouchUp()
        {
            drag = Drag.None;
        }

        // ---- helpers --------------------------------------------------------------------------

        static float[] Reserve(ref float[] buffer, int floats)
        {
            if (buffer.Length < floats)
                buffer = new float[Math.Max(floats, buffer.Length * 2)];
            return buffer;
        }

        static int Put(float[] s, int n, float x0, float y0, float x1, float y1)
        {
            s[n++] = x0;
            s[n++] = y0;
            s[n++] = x1;
            s[n++] = y1;
            return n;
        }

        static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        static string Ellipsize(IScopeCanvas g, string text, FontKind font, float size, float width)
        {
            if (g.TextWidth(text, font, size) <= width)
                return text;
            for (int n = text.Length - 1; n > 0; n--)
            {
                string s = text.Substring(0, n).TrimEnd() + "…";
                if (g.TextWidth(s, font, size) <= width)
                    return s;
            }
            return "";
        }

        static List<string> Wrap(IScopeCanvas g, string text, FontKind font, float size, float width)
        {
            var lines = new List<string>();
            foreach (string paragraph in text.Split('\n'))
            {
                string line = "";
                foreach (string word in paragraph.Split(' '))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && g.TextWidth(candidate, font, size) > width)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else
                    {
                        line = candidate;
                    }
                }
                lines.Add(line);
            }
            return lines;
        }
    }
}
