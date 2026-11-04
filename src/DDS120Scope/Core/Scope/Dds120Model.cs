namespace Dds120.Scope
{
    public sealed class GainStep
    {
        public double VoltsPerDiv { get; }
        public byte HardwareGain { get; }   // value sent with request 0xE0/0xE1
        public double CountsPerVolt { get; } // ADC counts per volt at the BNC (x1 probe)

        public GainStep(double voltsPerDiv, byte hardwareGain, double countsPerVolt)
        {
            VoltsPerDiv = voltsPerDiv;
            HardwareGain = hardwareGain;
            CountsPerVolt = countsPerVolt;
        }
    }

    public sealed class RateStep
    {
        public double SampleRate { get; }   // effective rate after averaging
        public byte RateCode { get; }       // value sent with request 0xE2
        public int Downsample { get; }      // raw samples averaged into one

        public RateStep(double sampleRate, byte rateCode, int downsample)
        {
            SampleRate = sampleRate;
            RateCode = rateCode;
            Downsample = downsample;
        }

        public double RawRate { get { return SampleRate * Downsample; } }
    }

    // Hardware constants for the SainSmart DDS120 running the sigrok-derived firmware from
    // OpenHantek6022 (modelDDS120.cpp). Counts/volt were measured by the OpenHantek authors.
    public static class Dds120Model
    {
        public const int Channels = 2;
        public const int AdcZero = 0x80;  // binary offset: 0x80 is 0 V

        public static readonly GainStep[] Gains =
        {
            new GainStep(0.020, 20, 400),
            new GainStep(0.050, 20, 400),
            new GainStep(0.100, 10, 200),
            new GainStep(0.200, 5, 96),
            new GainStep(0.500, 2, 43),
            new GainStep(1.000, 1, 21),
            new GainStep(2.000, 1, 21),
            new GainStep(5.000, 1, 21),
        };

        public static readonly byte[] HardwareGainCodes = { 1, 2, 5, 10, 20 };

        // The firmware's rate codes: N = N MS/s, except 11 = 10 MS/s and 10/20/50 = 100/200/500 kS/s.
        public static readonly RateStep[] Rates =
        {
            new RateStep(10e3, 1, 100),
            new RateStep(20e3, 2, 100),
            new RateStep(50e3, 5, 100),
            new RateStep(100e3, 8, 80),
            new RateStep(200e3, 8, 40),
            new RateStep(500e3, 8, 16),
            new RateStep(1e6, 8, 8),
            new RateStep(2e6, 8, 4),
            new RateStep(5e6, 15, 3),
            new RateStep(10e6, 11, 1),
            new RateStep(15e6, 15, 1),
            new RateStep(24e6, 24, 1),
            new RateStep(30e6, 30, 1),
            new RateStep(48e6, 48, 1),
        };

        // Raw samples per second per channel for a rate code.
        public static double RawRate(byte code)
        {
            switch (code)
            {
                case 10: return 100e3;
                case 20: return 200e3;
                case 50: return 500e3;
                case 11: return 10e6;
                default: return code * 1e6;
            }
        }

        // ADC counts per volt at the BNC for a hardware gain code (1, 2, 5, 10, 20).
        public static double CountsPerVolt(byte hardwareGain)
        {
            foreach (GainStep g in Gains)
                if (g.HardwareGain == hardwareGain)
                    return g.CountsPerVolt;
            return Gains[Gains.Length - 1].CountsPerVolt;
        }

        // Calibration square-wave output, request 0xE6.
        public static readonly int[] CalFrequencies = { 50, 60, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };

        public static byte CalFrequencyCode(int hz)
        {
            if (hz == 100)
                return 0; // sigrok firmware: 0 means 100 Hz
            if (hz < 1000)
                return (byte)(100 + hz / 10);
            return (byte)(hz / 1000);
        }
    }
}
