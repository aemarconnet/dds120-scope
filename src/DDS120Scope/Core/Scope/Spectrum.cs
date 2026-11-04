using System;

namespace Dds120.Scope
{
    // Magnitude spectrum of one channel, in dBV RMS per bin: a sine of amplitude A reads
    // 20·log10(A/√2) at its frequency whatever window is used.
    public sealed class Spectrum
    {
        public const int MaxPoints = 16384;

        public float[] Dbv;          // bins 0 .. N/2
        public double BinHz;
        public int Points;           // FFT length N
        public double PeakHz = double.NaN, PeakDbv = double.NaN;

        public double NyquistHz { get { return BinHz * (Dbv.Length - 1); } }

        // Transforms the largest power of two of samples (up to MaxPoints) starting near 'from'.
        public static Spectrum Compute(float[] v, double rate, double from, FftWindow window)
        {
            if (v == null || v.Length < 64)
                return null;
            int n = 64;
            while (n * 2 <= Math.Min(v.Length, MaxPoints)) n *= 2;
            int start = Math.Max(0, Math.Min(v.Length - n, (int)from));

            var re = new double[n];
            var im = new double[n];
            double mean = 0;
            for (int i = 0; i < n; i++) mean += v[start + i];
            mean /= n;
            double gain = 0;
            for (int i = 0; i < n; i++)
            {
                double w = Window(window, i, n);
                gain += w;
                re[i] = (v[start + i] - mean) * w;
            }
            gain /= n; // coherent gain
            Transform(re, im);

            var s = new Spectrum { Dbv = new float[n / 2 + 1], BinHz = rate / n, Points = n };
            double peak = 0;
            int peakBin = -1;
            for (int k = 0; k <= n / 2; k++)
            {
                double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / (n * gain);
                if (k > 0 && k < n / 2) mag *= 2;        // single-sided amplitude
                if (k == 0) mag = Math.Abs(mean);         // DC was removed before the window
                double rms = k == 0 ? mag : mag / Math.Sqrt(2);
                s.Dbv[k] = (float)(20 * Math.Log10(Math.Max(rms, 1e-9)));
                if (k >= 2 && mag > peak)
                {
                    peak = mag;
                    peakBin = k;
                }
            }
            if (peakBin > 0)
            {
                // parabolic interpolation of the log magnitude around the peak bin
                double offset = 0;
                if (peakBin < n / 2)
                {
                    double l = s.Dbv[peakBin - 1], c = s.Dbv[peakBin], r = s.Dbv[peakBin + 1];
                    double d = l - 2 * c + r;
                    if (Math.Abs(d) > 1e-9)
                        offset = Math.Max(-0.5, Math.Min(0.5, 0.5 * (l - r) / d));
                }
                s.PeakHz = (peakBin + offset) * s.BinHz;
                s.PeakDbv = s.Dbv[peakBin];
            }
            return s;
        }

        static double Window(FftWindow w, int i, int n)
        {
            double x = 2 * Math.PI * i / (n - 1);
            switch (w)
            {
                case FftWindow.Hann: return 0.5 - 0.5 * Math.Cos(x);
                case FftWindow.Blackman: return 0.42 - 0.5 * Math.Cos(x) + 0.08 * Math.Cos(2 * x);
                case FftWindow.FlatTop:
                    return 0.21557895 - 0.41663158 * Math.Cos(x) + 0.277263158 * Math.Cos(2 * x)
                        - 0.083578947 * Math.Cos(3 * x) + 0.006947368 * Math.Cos(4 * x);
                default: return 1;
            }
        }

        // In-place iterative radix-2 FFT; n must be a power of two.
        static void Transform(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int p = i + k, q = p + len / 2;
                        double xr = re[q] * cr - im[q] * ci;
                        double xi = re[q] * ci + im[q] * cr;
                        re[q] = re[p] - xr;
                        im[q] = im[p] - xi;
                        re[p] += xr;
                        im[p] += xi;
                        double nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }
    }
}
