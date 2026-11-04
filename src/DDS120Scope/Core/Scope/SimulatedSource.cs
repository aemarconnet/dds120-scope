using System;
using System.Diagnostics;
using System.Threading;

namespace Dds120.Scope
{
    // Produces raw data shaped exactly like the DDS120's (binary-offset bytes, interleaved channels,
    // per-gain counts/volt), so the whole pipeline can run without hardware.
    // CH1: the calibration square wave (0..2 V) at the selected cal frequency.
    // CH2: a 1.5 V sine at 2.2 kHz with a little 3rd harmonic and noise.
    public sealed class SimulatedSource : ISampleSource
    {
        readonly byte[] gain = { 1, 1 };
        readonly bool[] dc = { true, true };
        readonly Random noise = new Random(1234);
        double rawRate = 8e6;
        int channels = 2;
        int calHz = 1000;
        double t;            // simulated time of the next sample
        volatile bool aborted;

        public string Description { get { return "Demo signal (no hardware)"; } }
        public bool IsSimulated { get { return true; } }

        public void SetGain(int channel, byte hardwareGain) { gain[channel] = hardwareGain; }
        public void SetChannelCount(int count) { channels = count; }
        public void SetCalibrationFrequency(int hz) { calHz = hz; }
        public void Start() { aborted = false; }
        public void Stop() { }
        public void AbortRead() { aborted = true; }
        public void Dispose() { }

        public void SetCoupling(bool ch1Dc, bool ch2Dc)
        {
            dc[0] = ch1Dc;
            dc[1] = ch2Dc;
        }

        public void SetSampleRate(byte code)
        {
            rawRate = Dds120Model.RawRate(code);
        }

        static double CountsPerVolt(byte g)
        {
            return Dds120Model.CountsPerVolt(g);
        }

        double Signal(int ch, double time)
        {
            if (ch == 0)
            {
                double phase = time * calHz % 1.0;
                double v = phase < 0.5 ? 2.0 : 0.0;
                return dc[0] ? v : v - 1.0;
            }
            double w = 2 * Math.PI * 2200 * time;
            double s = 1.5 * Math.Sin(w) + 0.15 * Math.Sin(3 * w + 0.4) + (dc[1] ? 0.25 : 0.0);
            return s;
        }

        public int Read(byte[] buffer, int count, uint timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            double dt = 1.0 / rawRate;
            // A random start phase per frame, like a free-running real signal.
            t += noise.NextDouble() * 0.01;
            double[] cpv = { CountsPerVolt(gain[0]), CountsPerVolt(gain[1]) };
            int i = 0;
            while (i < count)
            {
                for (int ch = 0; ch < channels && i < count; ch++)
                {
                    double v = Signal(ch, t) + (noise.NextDouble() - 0.5) * 0.04;
                    int c = (int)Math.Round(Dds120Model.AdcZero + v * cpv[ch] + (noise.NextDouble() - 0.5) * 1.5);
                    buffer[i++] = (byte)(c < 0 ? 0 : c > 255 ? 255 : c);
                }
                t += dt;
            }
            // Pace like the real device would (bounded so the demo stays responsive).
            double ms = count / (double)channels / rawRate * 1000.0;
            int remaining = (int)Math.Min(ms, 400) - (int)sw.ElapsedMilliseconds;
            while (remaining > 0 && !aborted)
            {
                Thread.Sleep(Math.Min(remaining, 20));
                remaining -= 20;
            }
            return aborted ? 0 : count;
        }
    }
}
