using System;

namespace Dds120.Scope
{
    // Everything the screen shows besides the raw traces, derived from one frame: the measurements
    // of each channel and of the math waveform, the math waveform itself and the spectrum.
    public sealed class Analysis
    {
        public Frame Frame;
        public readonly ChannelMeasurement[] Channels = new ChannelMeasurement[2];
        public float[] MathWave;                  // when a +, − or × function is on
        public ChannelMeasurement MathMeasurement;
        public Spectrum Spectrum;                 // when the FFT is on

        // 'display' supplies the math and FFT choices, which can change after the capture (e.g. while
        // stopped); the frame's own settings snapshot describes how it was captured.
        public static Analysis Of(Frame f, ScopeSettings display)
        {
            var a = new Analysis { Frame = f };
            if (f == null)
                return a;
            for (int ch = 0; ch < 2; ch++)
                a.Channels[ch] = Measurements.Measure(f, ch);
            Measurements.MeasureDelay(a.Channels[0], a.Channels[1], f.SampleRate, f.ScreenStart);

            float[] v0 = f.Volts[0], v1 = f.Volts[1];
            if (display.MathTraceActive && v0 != null && v1 != null)
            {
                a.MathWave = Compute(display.MathMode, v0, v1);
                double n0 = Measurements.NoiseFloor(f.Settings.Channels[0]);
                double n1 = Measurements.NoiseFloor(f.Settings.Channels[1]);
                double floor = display.MathMode == MathFunction.Multiply
                    ? n0 * PeakAbs(v1) + n1 * PeakAbs(v0)
                    : n0 + n1;
                a.MathMeasurement = Measurements.Measure(a.MathWave, null, null, f.SampleRate, f.ScreenStart, f.ScreenSamples, floor);
            }
            if (display.MathMode == MathFunction.Fft && f.Volts[display.FftSource] != null)
                a.Spectrum = Spectrum.Compute(f.Volts[display.FftSource], f.SampleRate, f.ScreenStart, display.FftWindow);
            return a;
        }

        static float[] Compute(MathFunction fn, float[] a, float[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            var m = new float[n];
            for (int i = 0; i < n; i++)
            {
                switch (fn)
                {
                    case MathFunction.Add: m[i] = a[i] + b[i]; break;
                    case MathFunction.Subtract: m[i] = a[i] - b[i]; break;
                    default: m[i] = a[i] * b[i]; break;
                }
            }
            return m;
        }

        static double PeakAbs(float[] v)
        {
            double p = 0;
            foreach (float x in v)
                p = Math.Max(p, Math.Abs(x));
            return p;
        }
    }
}
