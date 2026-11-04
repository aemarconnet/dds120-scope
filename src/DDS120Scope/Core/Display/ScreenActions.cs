using System;
using Dds120.Scope;

namespace Dds120.Display
{
    // What touch and mouse gestures on the screen do to the settings, shared by both apps. Traces are
    // 0 = CH1, 1 = CH2, 2 = math. Zoom factors above 1 magnify.
    public static class ScreenActions
    {
        // Dragging sideways: the trigger point in YT, CH1 (the X axis) in XY.
        public static void PanHorizontal(ScopeSettings s, double divs)
        {
            if (s.XYActive)
                s.Channels[0].Position = ClampDivs(s.Channels[0].Position + divs);
            else if (!s.FftActive)
                s.TriggerPosition = Math.Round(Math.Max(0, Math.Min(1, s.TriggerPosition + divs / ScopeSettings.Divisions)), 4);
        }

        // Dragging up or down: the trace's position (CH2 in XY).
        public static void PanVertical(ScopeSettings s, int trace, double divs)
        {
            if (s.FftActive)
                return;
            if (s.XYActive)
                trace = 1;
            if (trace == 2)
                s.MathPosition = ClampDivs(s.MathPosition + divs);
            else
                s.Channels[trace].Position = ClampDivs(s.Channels[trace].Position + divs);
        }

        // A horizontal pinch: time/div in YT, CH1 volts/div in XY, the span in FFT.
        public static void ZoomHorizontal(ScopeSettings s, double factor)
        {
            if (s.FftActive)
                s.FftZoom = ScopeSettings.FftZooms[Nearest(ScopeSettings.FftZooms.Length, i => ScopeSettings.FftZooms[i], s.FftZoom * factor)];
            else if (s.XYActive)
                ZoomVolts(s, 0, factor);
            else
                s.TimebaseIndex = Nearest(ScopeSettings.Timebases.Length, i => ScopeSettings.Timebases[i], s.SecondsPerDiv / factor);
        }

        // A vertical pinch on a trace: its volts/div (CH2 in XY).
        public static void ZoomVertical(ScopeSettings s, int trace, double factor)
        {
            if (s.FftActive)
                return;
            ZoomVolts(s, s.XYActive ? 1 : trace, factor);
        }

        static void ZoomVolts(ScopeSettings s, int trace, double factor)
        {
            if (trace == 2)
            {
                s.MathScaleIndex = Nearest(ScopeSettings.MathScales.Length, i => ScopeSettings.MathScales[i], s.MathScale / factor);
                return;
            }
            ChannelSettings c = s.Channels[trace];
            c.GainIndex = Nearest(Dds120Model.Gains.Length, i => Dds120Model.Gains[i].VoltsPerDiv * c.Probe, c.VoltsPerDiv / factor);
        }

        // One step of volts/div, e.g. for a mouse wheel: +1 = more volts/div.
        public static void StepVolts(ScopeSettings s, int trace, int step)
        {
            if (trace == 2)
                s.MathScaleIndex = Math.Max(0, Math.Min(ScopeSettings.MathScales.Length - 1, s.MathScaleIndex + step));
            else
                s.Channels[trace].GainIndex = Math.Max(0, Math.Min(Dds120Model.Gains.Length - 1, s.Channels[trace].GainIndex + step));
        }

        // ---- the long-press / right-click menu ----

        public static void TriggerLevelAt(ScopeSettings s, double div)
        {
            ChannelSettings c = s.Channels[s.TriggerSource];
            double step = c.VoltsPerDiv / 50;
            s.TriggerLevel = Math.Round((div - c.Position) * c.VoltsPerDiv / step) * step;
        }

        public static void TriggerPointAt(ScopeSettings s, double fraction)
        {
            s.TriggerPosition = Math.Round(Math.Max(0, Math.Min(1, fraction)) * 100) / 100;
        }

        // Time cursors one division apart, starting at the point.
        public static void TimeCursorsAt(ScopeSettings s, double fraction)
        {
            double x1 = Math.Max(0, Math.Min(0.9, fraction));
            s.CursorX1 = Math.Round(x1 * 1000) / 1000;
            s.CursorX2 = Math.Round((x1 + 0.1) * 1000) / 1000;
            s.Cursors = s.Cursors == CursorMode.Amplitude || s.Cursors == CursorMode.Both ? CursorMode.Both : CursorMode.Time;
        }

        // Amplitude cursors one division apart, the first at the point, on the trace there.
        public static void LevelCursorsAt(ScopeSettings s, double div, int trace)
        {
            double y1 = Math.Max(-3, Math.Min(4, div));
            s.CursorY1 = Math.Round(y1 * 50) / 50;
            s.CursorY2 = Math.Round((y1 - 1) * 50) / 50;
            s.CursorSource = trace;
            s.Cursors = s.Cursors == CursorMode.Time || s.Cursors == CursorMode.Both ? CursorMode.Both : CursorMode.Amplitude;
        }

        static double ClampDivs(double d)
        {
            return Math.Round(Math.Max(-4, Math.Min(4, d)), 3);
        }

        // Index of the value closest to target on a log scale (the steps are 1-2-5 sequences).
        static int Nearest(int count, Func<int, double> value, double target)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < count; i++)
            {
                double d = Math.Abs(Math.Log(value(i) / target));
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }
    }
}
