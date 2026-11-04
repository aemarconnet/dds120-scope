using System;
using System.Collections.Generic;
using Dds120.Util;

namespace Dds120.Scope
{
    // The automatic measurements a bench scope offers. Phase and Delay compare CH2 with CH1.
    public enum MeasureType
    {
        Frequency, Period, PeakToPeak, Amplitude, Max, Min, High, Low, Mean, CycleMean, Rms, CycleRms,
        RiseTime, FallTime, PosWidth, NegWidth, PosDuty, NegDuty, PosOvershoot, NegOvershoot, Delay, Phase,
    }

    public sealed class ChannelMeasurement
    {
        public double Max, Min, Mean, Rms;
        public double High = double.NaN, Low = double.NaN;   // top and base levels (histogram)
        public double Frequency = double.NaN;                // Hz
        public double Duty = double.NaN;                     // fraction high, 0..1
        public double CycleMean = double.NaN, CycleRms = double.NaN;
        public double RiseTime = double.NaN, FallTime = double.NaN;  // 10 % to 90 %, seconds
        public double PosWidth = double.NaN, NegWidth = double.NaN;  // at 50 %, seconds
        public double Delay = double.NaN, Phase = double.NaN;        // relative to CH1 (CH2 only)

        // Rising and falling 50 % crossings in sample indices, for the cross-channel measurements.
        internal readonly List<double> Rises = new List<double>();

        public double PeakToPeak { get { return Max - Min; } }
        public double Amplitude { get { return High - Low; } }
        public double Period { get { return 1.0 / Frequency; } }
        public double NegDuty { get { return 1 - Duty; } }
        public double PosOvershoot { get { return Amplitude > 0 ? (Max - High) / Amplitude : double.NaN; } }
        public double NegOvershoot { get { return Amplitude > 0 ? (Low - Min) / Amplitude : double.NaN; } }

        public double Get(MeasureType t)
        {
            switch (t)
            {
                case MeasureType.Frequency: return Frequency;
                case MeasureType.Period: return Period;
                case MeasureType.PeakToPeak: return PeakToPeak;
                case MeasureType.Amplitude: return Amplitude;
                case MeasureType.Max: return Max;
                case MeasureType.Min: return Min;
                case MeasureType.High: return High;
                case MeasureType.Low: return Low;
                case MeasureType.Mean: return Mean;
                case MeasureType.CycleMean: return CycleMean;
                case MeasureType.Rms: return Rms;
                case MeasureType.CycleRms: return CycleRms;
                case MeasureType.RiseTime: return RiseTime;
                case MeasureType.FallTime: return FallTime;
                case MeasureType.PosWidth: return PosWidth;
                case MeasureType.NegWidth: return NegWidth;
                case MeasureType.PosDuty: return Duty;
                case MeasureType.NegDuty: return NegDuty;
                case MeasureType.PosOvershoot: return PosOvershoot;
                case MeasureType.NegOvershoot: return NegOvershoot;
                case MeasureType.Delay: return Delay;
                case MeasureType.Phase: return Phase;
                default: return double.NaN;
            }
        }

        // "Freq 1.000 kHz"; unit is the waveform's (V, or V² for a product).
        public string Format(MeasureType t, string unit)
        {
            double v = Get(t);
            string label = Measurements.ShortName(t);
            switch (t)
            {
                case MeasureType.Frequency: return label + " " + Si.Format(v, "Hz", 4);
                case MeasureType.Period:
                case MeasureType.RiseTime:
                case MeasureType.FallTime:
                case MeasureType.PosWidth:
                case MeasureType.NegWidth:
                case MeasureType.Delay: return label + " " + Si.Format(v, "s");
                case MeasureType.PosDuty:
                case MeasureType.NegDuty:
                case MeasureType.PosOvershoot:
                case MeasureType.NegOvershoot: return label + " " + Percent(v);
                case MeasureType.Phase: return label + " " + (double.IsNaN(v) ? "---" : v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "°");
                default: return label + " " + Si.Format(v, unit);
            }
        }

        static string Percent(double v)
        {
            return double.IsNaN(v) ? "---" : (v * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " %";
        }
    }

    public static class Measurements
    {
        public static readonly MeasureType[] DefaultItems =
        {
            MeasureType.PeakToPeak, MeasureType.Frequency, MeasureType.Rms, MeasureType.Mean,
            MeasureType.PosDuty, MeasureType.Max, MeasureType.Min,
        };

        public static string ShortName(MeasureType t)
        {
            switch (t)
            {
                case MeasureType.Frequency: return "Freq";
                case MeasureType.Period: return "Per";
                case MeasureType.PeakToPeak: return "Pk-Pk";
                case MeasureType.Amplitude: return "Ampl";
                case MeasureType.CycleMean: return "CMean";
                case MeasureType.Rms: return "RMS";
                case MeasureType.CycleRms: return "CRMS";
                case MeasureType.RiseTime: return "Rise";
                case MeasureType.FallTime: return "Fall";
                case MeasureType.PosWidth: return "+Wid";
                case MeasureType.NegWidth: return "−Wid";
                case MeasureType.PosDuty: return "+Duty";
                case MeasureType.NegDuty: return "−Duty";
                case MeasureType.PosOvershoot: return "+Over";
                case MeasureType.NegOvershoot: return "−Over";
                default: return t.ToString();
            }
        }

        public static string LongName(MeasureType t)
        {
            switch (t)
            {
                case MeasureType.Frequency: return "Frequency";
                case MeasureType.Period: return "Period";
                case MeasureType.PeakToPeak: return "Peak-to-peak";
                case MeasureType.Amplitude: return "Amplitude (high − low)";
                case MeasureType.Max: return "Maximum";
                case MeasureType.Min: return "Minimum";
                case MeasureType.High: return "High (top level)";
                case MeasureType.Low: return "Low (base level)";
                case MeasureType.Mean: return "Mean";
                case MeasureType.CycleMean: return "Cycle mean";
                case MeasureType.Rms: return "RMS";
                case MeasureType.CycleRms: return "Cycle RMS";
                case MeasureType.RiseTime: return "Rise time (10–90 %)";
                case MeasureType.FallTime: return "Fall time (90–10 %)";
                case MeasureType.PosWidth: return "Positive width";
                case MeasureType.NegWidth: return "Negative width";
                case MeasureType.PosDuty: return "Positive duty cycle";
                case MeasureType.NegDuty: return "Negative duty cycle";
                case MeasureType.PosOvershoot: return "Positive overshoot";
                case MeasureType.NegOvershoot: return "Negative overshoot";
                case MeasureType.Delay: return "Delay CH1→CH2";
                case MeasureType.Phase: return "Phase CH1→CH2";
                default: return t.ToString();
            }
        }

        // Phase and Delay need both channels; they are shown on the CH2 line only.
        public static bool IsCrossChannel(MeasureType t)
        {
            return t == MeasureType.Delay || t == MeasureType.Phase;
        }

        // Noise floor for timing: below ~6 ADC counts peak-to-peak there is no signal to time.
        public static double NoiseFloor(ChannelSettings cs)
        {
            return 6 * cs.Probe / Dds120Model.Gains[cs.GainIndex].CountsPerVolt;
        }

        public static ChannelMeasurement Measure(Frame f, int ch)
        {
            float[] v = f.Volts[ch];
            if (v == null)
                return null;
            return Measure(v, f.MinEnvelope[ch], f.MaxEnvelope[ch], f.SampleRate, f.ScreenStart, f.ScreenSamples,
                NoiseFloor(f.Settings.Channels[ch]));
        }

        // Amplitude values over the visible window; timing over the whole record so that slow signals
        // still get several periods to average. mn/mx (peak detect) may be null.
        public static ChannelMeasurement Measure(float[] v, float[] mn, float[] mx, double rate,
            double screenStart, int screenSamples, double noiseFloor)
        {
            if (v == null || v.Length < 2)
                return null;
            int a = Math.Max(0, (int)Math.Floor(screenStart));
            int b = Math.Min(v.Length, (int)Math.Ceiling(screenStart + screenSamples) + 1);
            if (b - a < 2)
            {
                a = 0;
                b = v.Length;
            }
            var m = new ChannelMeasurement { Max = double.MinValue, Min = double.MaxValue };
            double sum = 0, sq = 0;
            for (int i = a; i < b; i++)
            {
                double x = v[i];
                double lo = mn != null ? mn[i] : x, hi = mx != null ? mx[i] : x;
                if (hi > m.Max) m.Max = hi;
                if (lo < m.Min) m.Min = lo;
                sum += x;
                sq += x * x;
            }
            int n = b - a;
            m.Mean = sum / n;
            m.Rms = Math.Sqrt(sq / n);
            Levels(v, a, b, m, noiseFloor);
            MeasureTiming(v, rate, m, noiseFloor, a, b);
            return m;
        }

        // High and low: the most common value in the upper and lower half (flat tops of a pulse), which
        // for a sine lands at its peaks. Falls back to max/min for a flat line.
        static void Levels(float[] v, int a, int b, ChannelMeasurement m, double noiseFloor)
        {
            double pp = m.Max - m.Min;
            if (pp < noiseFloor)
            {
                m.High = m.Max;
                m.Low = m.Min;
                return;
            }
            const int bins = 128;
            var hist = new int[bins];
            double scale = (bins - 1) / pp;
            for (int i = a; i < b; i++)
            {
                int k = (int)((v[i] - m.Min) * scale + 0.5);
                hist[k < 0 ? 0 : k >= bins ? bins - 1 : k]++;
            }
            int half = bins / 2, hiBin = half, loBin = 0;
            for (int k = half; k < bins; k++)
                if (hist[k] > hist[hiBin]) hiBin = k;
            for (int k = 0; k < half; k++)
                if (hist[k] > hist[loBin]) loBin = k;
            m.High = m.Min + hiBin / scale;
            m.Low = m.Min + loBin / scale;
        }

        static void MeasureTiming(float[] v, double rate, ChannelMeasurement m, double noiseFloor, int a, int b)
        {
            double max = double.MinValue, min = double.MaxValue;
            foreach (float x in v)
            {
                if (x > max) max = x;
                if (x < min) min = x;
            }
            if (max - min < noiseFloor)
                return;
            // 50 % reference between the levels; hysteresis keeps noise from adding crossings.
            double high = !double.IsNaN(m.High) && m.High - m.Low >= noiseFloor ? m.High : max;
            double low = !double.IsNaN(m.Low) && m.High - m.Low >= noiseFloor ? m.Low : min;
            double ampl = high - low;
            double mid = (high + low) / 2, hyst = ampl * 0.1;
            var falls = new List<double>();
            bool armedHigh = false, armedLow = false;
            for (int i = 1; i < v.Length; i++)
            {
                double x0 = v[i - 1], x1 = v[i];
                if (x1 < mid - hyst) armedHigh = true;
                if (x1 > mid + hyst) armedLow = true;
                if (armedHigh && x0 < mid && x1 >= mid)
                {
                    m.Rises.Add(i - 1 + (mid - x0) / (x1 - x0));
                    armedHigh = false;
                }
                else if (armedLow && x0 > mid && x1 <= mid)
                {
                    falls.Add(i - 1 + (x0 - mid) / (x0 - x1));
                    armedLow = false;
                }
            }
            List<double> rises = m.Rises;
            if (rises.Count >= 2)
                m.Frequency = (rises.Count - 1) * rate / (rises[rises.Count - 1] - rises[0]);

            // widths: each rise to the next fall, each fall to the next rise
            double posSum = 0, negSum = 0;
            int posN = 0, negN = 0, fi = 0, ri = 0;
            foreach (double r in rises)
            {
                while (fi < falls.Count && falls[fi] <= r) fi++;
                if (fi < falls.Count) { posSum += falls[fi] - r; posN++; }
            }
            foreach (double f in falls)
            {
                while (ri < rises.Count && rises[ri] <= f) ri++;
                if (ri < rises.Count) { negSum += rises[ri] - f; negN++; }
            }
            if (posN > 0) m.PosWidth = posSum / posN / rate;
            if (negN > 0) m.NegWidth = negSum / negN / rate;
            if (posN > 0 && negN > 0)
                m.Duty = m.PosWidth / (m.PosWidth + m.NegWidth);

            // 10-90 % transition times around each 50 % crossing
            double lo10 = low + 0.1 * ampl, hi90 = low + 0.9 * ampl;
            m.RiseTime = Transition(v, rises, lo10, hi90, true, rate);
            m.FallTime = Transition(v, falls, lo10, hi90, false, rate);

            // cycle mean / RMS over whole periods inside the visible window
            int first = -1, last = -1;
            foreach (double r in rises)
            {
                if (r < a) continue;
                if (r > b - 1) break;
                if (first < 0) first = (int)Math.Ceiling(r);
                last = (int)Math.Ceiling(r);
            }
            if (first >= 0 && last > first)
            {
                double sum = 0, sq = 0;
                for (int i = first; i < last; i++)
                {
                    sum += v[i];
                    sq += v[i] * (double)v[i];
                }
                m.CycleMean = sum / (last - first);
                m.CycleRms = Math.Sqrt(sq / (last - first));
            }
        }

        static double Transition(float[] v, List<double> crossings, double lo, double hi, bool rising, double rate)
        {
            double sum = 0;
            int n = 0;
            foreach (double c in crossings)
            {
                int i = (int)c;
                // walk back to the start level and forward to the end level
                int s = i;
                while (s > 0 && (rising ? v[s] > lo : v[s] < hi)) s--;
                int e = i + 1;
                while (e < v.Length - 1 && (rising ? v[e] < hi : v[e] > lo)) e++;
                if (s <= 0 || e >= v.Length - 1)
                    continue;
                double t0 = Cross(v, s, rising ? lo : hi);
                double t1 = Cross(v, e - 1, rising ? hi : lo);
                if (t1 > t0)
                {
                    sum += t1 - t0;
                    n++;
                }
            }
            return n > 0 ? sum / n / rate : double.NaN;
        }

        // Interpolated position where v crosses level between samples i and i + 1.
        static double Cross(float[] v, int i, double level)
        {
            double d = v[i + 1] - v[i];
            if (Math.Abs(d) < 1e-12)
                return i;
            double t = (level - v[i]) / d;
            return i + Math.Max(0, Math.Min(1, t));
        }

        // CH1 to CH2: time from a CH1 rising edge in the window to the next CH2 rising edge, and that
        // as a phase angle of the CH1 period.
        public static void MeasureDelay(ChannelMeasurement ch1, ChannelMeasurement ch2, double rate, double screenStart)
        {
            if (ch1 == null || ch2 == null || ch1.Rises.Count == 0 || ch2.Rises.Count == 0)
                return;
            double r1 = double.NaN;
            foreach (double r in ch1.Rises)
                if (r >= screenStart) { r1 = r; break; }
            if (double.IsNaN(r1))
                return;
            foreach (double r2 in ch2.Rises)
            {
                if (r2 < r1) continue;
                double delay = (r2 - r1) / rate;
                ch2.Delay = delay;
                if (!double.IsNaN(ch1.Frequency))
                {
                    double ph = delay * ch1.Frequency * 360 % 360;
                    ch2.Phase = ph > 180 ? ph - 360 : ph;
                }
                return;
            }
        }
    }
}
