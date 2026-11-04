using System;
using System.Collections.Generic;
using System.Globalization;

namespace Dds120.Scope
{
    public enum TriggerMode { Auto, Normal }
    public enum TriggerEdge { Rising, Falling }
    // How the trigger source is conditioned before the edge search (software, like a bench scope's
    // trigger coupling): AC removes the mean, the rejects filter around ~50 kHz, noise reject
    // widens the hysteresis.
    public enum TriggerCoupling { DC, AC, NoiseReject, HFReject, LFReject }
    // How raw samples become one displayed sample when the scope samples faster than the screen
    // needs: first sample, min/max envelope, mean, or the mean plus averaging over acquisitions.
    public enum AcquireMode { Sample, PeakDetect, HiRes, Average }
    public enum MathFunction { Off, Add, Subtract, Multiply, Fft }
    public enum FftWindow { Rectangular, Hann, Blackman, FlatTop }
    public enum CursorMode { Off, Time, Amplitude, Both }

    public sealed class ChannelSettings
    {
        public bool Enabled = true;
        public int GainIndex = 5;        // into Dds120Model.Gains (1 V/div)
        public bool AcCoupling;
        public int Probe = 1;            // 1 or 10
        public double Position;          // ground marker, in divisions from center
        public bool Inverted;

        public double VoltsPerDiv { get { return Dds120Model.Gains[GainIndex].VoltsPerDiv * Probe; } }

        public ChannelSettings Clone() { return (ChannelSettings)MemberwiseClone(); }
    }

    public sealed class ScopeSettings
    {
        public static readonly double[] Timebases =
        {
            1e-6, 2e-6, 5e-6, 10e-6, 20e-6, 50e-6, 100e-6, 200e-6, 500e-6,
            1e-3, 2e-3, 5e-3, 10e-3, 20e-3, 50e-3, 100e-3,
        };

        public const int Divisions = 10;          // horizontal
        public const int VerticalDivisions = 8;

        public static readonly int[] AverageCounts = { 2, 4, 8, 16, 32, 64, 128 };
        // Seconds a trace stays visible; 0 = off, infinity = until something changes.
        public static readonly double[] PersistenceTimes = { 0, 0.5, 1, 2, 5, double.PositiveInfinity };
        // Math channel scale in units/div (V, or V² for CH1×CH2).
        public static readonly double[] MathScales = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50 };
        public static readonly int[] FftZooms = { 1, 2, 5, 10 };

        public ChannelSettings[] Channels = { new ChannelSettings { Position = 2 }, new ChannelSettings { Position = -2 } };
        public int TimebaseIndex = 9;             // 1 ms/div
        public TriggerMode TriggerMode = TriggerMode.Auto;
        public TriggerEdge TriggerEdge = TriggerEdge.Rising;
        public int TriggerSource;                 // 0 = CH1, 1 = CH2
        public double TriggerLevel = 0.5;         // volts at the probe tip
        public double TriggerPosition = 0.5;      // trigger point as a fraction of the screen width
        public int CalFrequency = 1000;
        public double MaxRateSingle = 30e6;       // measured exact on hardware
        public double MaxRateDual = 15e6;         // measured exact on hardware

        // Highest USB data rate (bytes/s, all channels) that is safe. When the PC reads slower than
        // the scope samples, the firmware pauses sampling instead of dropping data, so the real
        // rate is lower and uneven. Measured: 30 MB/s delivered exactly, while every 48 MB/s and
        // faster code saturated at ~44.8 MB/s.
        public const double MaxByteRate = 30e6;

        public AcquireMode Acquire = AcquireMode.HiRes;
        public int AverageCount = 16;
        public TriggerCoupling TriggerCoupling = TriggerCoupling.DC;

        public MathFunction MathMode = MathFunction.Off;
        public int MathScaleIndex = 6;            // into MathScales (1 /div)
        public double MathPosition;               // divisions from center
        public int FftSource;                     // 0 = CH1, 1 = CH2
        public FftWindow FftWindow = FftWindow.Hann;
        public int FftZoom = 1;

        public CursorMode Cursors = CursorMode.Off;
        public int CursorSource;                  // 0 = CH1, 1 = CH2, 2 = math
        public double CursorX1 = 0.3, CursorX2 = 0.7;   // fraction of the screen width
        public double CursorY1 = 1, CursorY2 = -1;      // divisions from center

        public bool XY;
        public int PersistenceIndex;              // into PersistenceTimes
        public bool Dots;
        public MeasureType[] MeasureItems = (MeasureType[])Measurements.DefaultItems.Clone();

        public double SecondsPerDiv { get { return Timebases[TimebaseIndex]; } }

        public int ActiveChannelCount { get { return Channels[1].Enabled ? 2 : 1; } }

        public double MathScale { get { return MathScales[MathScaleIndex]; } }

        // The math waveform needs both channels; the FFT only its source.
        public bool MathTraceActive
        {
            get { return MathMode != MathFunction.Off && MathMode != MathFunction.Fft && Channels[0].Enabled && Channels[1].Enabled; }
        }

        public bool FftActive { get { return MathMode == MathFunction.Fft && Channels[FftSource].Enabled; } }

        public bool XYActive { get { return XY && Channels[0].Enabled && Channels[1].Enabled; } }

        public string MathName
        {
            get
            {
                switch (MathMode)
                {
                    case MathFunction.Add: return "CH1+CH2";
                    case MathFunction.Subtract: return "CH1−CH2";
                    case MathFunction.Multiply: return "CH1×CH2";
                    case MathFunction.Fft: return "FFT CH" + (FftSource + 1);
                    default: return "Off";
                }
            }
        }

        public string MathUnit { get { return MathMode == MathFunction.Multiply ? "V²" : "V"; } }

        public ScopeSettings Clone()
        {
            var c = (ScopeSettings)MemberwiseClone();
            c.Channels = new[] { Channels[0].Clone(), Channels[1].Clone() };
            c.MeasureItems = (MeasureType[])MeasureItems.Clone();
            return c;
        }

        // Takes over every setting of another instance (e.g. what Autoset proposes) in place.
        public void CopyFrom(ScopeSettings other)
        {
            foreach (System.Reflection.FieldInfo f in typeof(ScopeSettings).GetFields(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                f.SetValue(this, f.GetValue(other));
            Channels = new[] { other.Channels[0].Clone(), other.Channels[1].Clone() };
            MeasureItems = (MeasureType[])other.MeasureItems.Clone();
        }

        // Highest sample rate that keeps one screen within ~10k samples and the USB limits.
        public RateStep ChooseRate()
        {
            double screen = SecondsPerDiv * Divisions;
            double max = Math.Min(ActiveChannelCount == 1 ? MaxRateSingle : MaxRateDual, MaxByteRate / ActiveChannelCount);
            RateStep best = Dds120Model.Rates[0];
            foreach (RateStep r in Dds120Model.Rates)
                if (r.SampleRate <= max && r.SampleRate * screen <= 10000 * 1.0001)
                    best = r;
            return best;
        }

        public void Save(IDictionary<string, string> d)
        {
            for (int i = 0; i < 2; i++)
            {
                ChannelSettings c = Channels[i];
                string p = "ch" + (i + 1) + ".";
                d[p + "enabled"] = c.Enabled ? "1" : "0";
                d[p + "gain"] = c.GainIndex.ToString(CultureInfo.InvariantCulture);
                d[p + "ac"] = c.AcCoupling ? "1" : "0";
                d[p + "probe"] = c.Probe.ToString(CultureInfo.InvariantCulture);
                d[p + "position"] = c.Position.ToString("R", CultureInfo.InvariantCulture);
                d[p + "inverted"] = c.Inverted ? "1" : "0";
            }
            d["timebase"] = TimebaseIndex.ToString(CultureInfo.InvariantCulture);
            d["trigger.mode"] = TriggerMode.ToString();
            d["trigger.edge"] = TriggerEdge.ToString();
            d["trigger.source"] = TriggerSource.ToString(CultureInfo.InvariantCulture);
            d["trigger.level"] = TriggerLevel.ToString("R", CultureInfo.InvariantCulture);
            d["trigger.position"] = TriggerPosition.ToString("R", CultureInfo.InvariantCulture);
            d["calout"] = CalFrequency.ToString(CultureInfo.InvariantCulture);
            d["maxrate.single"] = MaxRateSingle.ToString("R", CultureInfo.InvariantCulture);
            d["maxrate.dual"] = MaxRateDual.ToString("R", CultureInfo.InvariantCulture);
            d["acquire"] = Acquire.ToString();
            d["acquire.average"] = AverageCount.ToString(CultureInfo.InvariantCulture);
            d["trigger.coupling"] = TriggerCoupling.ToString();
            d["math"] = MathMode.ToString();
            d["math.scale"] = MathScaleIndex.ToString(CultureInfo.InvariantCulture);
            d["math.position"] = MathPosition.ToString("R", CultureInfo.InvariantCulture);
            d["fft.source"] = FftSource.ToString(CultureInfo.InvariantCulture);
            d["fft.window"] = FftWindow.ToString();
            d["fft.zoom"] = FftZoom.ToString(CultureInfo.InvariantCulture);
            d["cursor.mode"] = Cursors.ToString();
            d["cursor.source"] = CursorSource.ToString(CultureInfo.InvariantCulture);
            d["cursor.x1"] = CursorX1.ToString("R", CultureInfo.InvariantCulture);
            d["cursor.x2"] = CursorX2.ToString("R", CultureInfo.InvariantCulture);
            d["cursor.y1"] = CursorY1.ToString("R", CultureInfo.InvariantCulture);
            d["cursor.y2"] = CursorY2.ToString("R", CultureInfo.InvariantCulture);
            d["display.xy"] = XY ? "1" : "0";
            d["display.persistence"] = PersistenceIndex.ToString(CultureInfo.InvariantCulture);
            d["display.dots"] = Dots ? "1" : "0";
            d["measure"] = string.Join(",", Array.ConvertAll(MeasureItems, m => m.ToString()));
        }

        public void Load(IDictionary<string, string> d)
        {
            for (int i = 0; i < 2; i++)
            {
                ChannelSettings c = Channels[i];
                string p = "ch" + (i + 1) + ".";
                c.Enabled = Get(d, p + "enabled", c.Enabled ? 1 : 0) != 0;
                c.GainIndex = Clamp((int)Get(d, p + "gain", c.GainIndex), 0, Dds120Model.Gains.Length - 1);
                c.AcCoupling = Get(d, p + "ac", 0) != 0;
                c.Probe = Get(d, p + "probe", 1) == 10 ? 10 : 1;
                c.Position = Math.Max(-4, Math.Min(4, Get(d, p + "position", c.Position)));
                c.Inverted = Get(d, p + "inverted", 0) != 0;
            }
            if (!Channels[0].Enabled && !Channels[1].Enabled)
                Channels[0].Enabled = true;
            TimebaseIndex = Clamp((int)Get(d, "timebase", TimebaseIndex), 0, Timebases.Length - 1);
            string s;
            if (d.TryGetValue("trigger.mode", out s)) Enum.TryParse(s, out TriggerMode);
            if (d.TryGetValue("trigger.edge", out s)) Enum.TryParse(s, out TriggerEdge);
            TriggerSource = Clamp((int)Get(d, "trigger.source", 0), 0, 1);
            TriggerLevel = Get(d, "trigger.level", TriggerLevel);
            TriggerPosition = Math.Max(0, Math.Min(1, Get(d, "trigger.position", TriggerPosition)));
            int cal = (int)Get(d, "calout", CalFrequency);
            CalFrequency = Array.IndexOf(Dds120Model.CalFrequencies, cal) >= 0 ? cal : 1000;
            MaxRateSingle = Get(d, "maxrate.single", MaxRateSingle);
            MaxRateDual = Get(d, "maxrate.dual", MaxRateDual);
            if (d.TryGetValue("acquire", out s)) Enum.TryParse(s, out Acquire);
            int avg = (int)Get(d, "acquire.average", AverageCount);
            AverageCount = Array.IndexOf(AverageCounts, avg) >= 0 ? avg : 16;
            if (d.TryGetValue("trigger.coupling", out s)) Enum.TryParse(s, out TriggerCoupling);
            if (d.TryGetValue("math", out s)) Enum.TryParse(s, out MathMode);
            MathScaleIndex = Clamp((int)Get(d, "math.scale", MathScaleIndex), 0, MathScales.Length - 1);
            MathPosition = Math.Max(-4, Math.Min(4, Get(d, "math.position", MathPosition)));
            FftSource = Clamp((int)Get(d, "fft.source", FftSource), 0, 1);
            if (d.TryGetValue("fft.window", out s)) Enum.TryParse(s, out FftWindow);
            int zoom = (int)Get(d, "fft.zoom", FftZoom);
            FftZoom = Array.IndexOf(FftZooms, zoom) >= 0 ? zoom : 1;
            if (d.TryGetValue("cursor.mode", out s)) Enum.TryParse(s, out Cursors);
            CursorSource = Clamp((int)Get(d, "cursor.source", CursorSource), 0, 2);
            CursorX1 = Math.Max(0, Math.Min(1, Get(d, "cursor.x1", CursorX1)));
            CursorX2 = Math.Max(0, Math.Min(1, Get(d, "cursor.x2", CursorX2)));
            CursorY1 = Math.Max(-4, Math.Min(4, Get(d, "cursor.y1", CursorY1)));
            CursorY2 = Math.Max(-4, Math.Min(4, Get(d, "cursor.y2", CursorY2)));
            XY = Get(d, "display.xy", 0) != 0;
            PersistenceIndex = Clamp((int)Get(d, "display.persistence", 0), 0, PersistenceTimes.Length - 1);
            Dots = Get(d, "display.dots", 0) != 0;
            if (d.TryGetValue("measure", out s))
            {
                var items = new List<MeasureType>();
                foreach (string name in s.Split(','))
                {
                    MeasureType m;
                    if (Enum.TryParse(name.Trim(), out m) && !items.Contains(m))
                        items.Add(m);
                }
                MeasureItems = items.ToArray();
            }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        static double Get(IDictionary<string, string> d, string key, double def)
        {
            string s;
            double v;
            return d.TryGetValue(key, out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }
    }

    // Per-unit corrections; the DDS120 has no calibration EEPROM, so these live in the settings file.
    public sealed class Calibration
    {
        // [channel, index into Dds120Model.HardwareGainCodes] in ADC counts
        public readonly double[,] ZeroOffset = new double[2, 5];
        public readonly double[,] GainFactor = { { 1, 1, 1, 1, 1 }, { 1, 1, 1, 1, 1 } };

        public static int CodeIndex(byte hardwareGain)
        {
            int i = Array.IndexOf(Dds120Model.HardwareGainCodes, hardwareGain);
            return i < 0 ? 0 : i;
        }

        public void Save(IDictionary<string, string> d)
        {
            for (int ch = 0; ch < 2; ch++)
                for (int g = 0; g < 5; g++)
                {
                    string k = "cal.ch" + (ch + 1) + ".x" + Dds120Model.HardwareGainCodes[g];
                    d[k + ".zero"] = ZeroOffset[ch, g].ToString("0.###", CultureInfo.InvariantCulture);
                    d[k + ".gain"] = GainFactor[ch, g].ToString("0.####", CultureInfo.InvariantCulture);
                }
        }

        public void Load(IDictionary<string, string> d)
        {
            for (int ch = 0; ch < 2; ch++)
                for (int g = 0; g < 5; g++)
                {
                    string k = "cal.ch" + (ch + 1) + ".x" + Dds120Model.HardwareGainCodes[g];
                    string s;
                    double v;
                    if (d.TryGetValue(k + ".zero", out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && Math.Abs(v) < 64)
                        ZeroOffset[ch, g] = v;
                    if (d.TryGetValue(k + ".gain", out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0.5 && v < 2)
                        GainFactor[ch, g] = v;
                }
        }
    }
}
