using System;
using System.Diagnostics;
using System.Threading;
using Dds120.Usb;

namespace Dds120.Scope
{
    public enum AcqStatus { NoDevice, Connecting, Stopped, Waiting, Triggered, Untriggered, Calibrating, Autoset }

    // Owns the scope on a background thread: connects (and reconnects), pushes settings to the
    // hardware, captures, triggers in software and publishes processed frames.
    public sealed class AcquisitionEngine : IDisposable
    {
        // Raw samples per channel thrown away after each start: the FIFO still holds stale data.
        public const int DropRawSamples = 2560;

        readonly object sync = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread thread;
        readonly Calibration calibration;

        ScopeSettings pending;
        bool settingsDirty = true;
        volatile bool disposed;
        volatile bool running = true;
        volatile bool singleShot;
        volatile bool demoRequested;
        volatile bool keepStockIdentity = true;
        volatile bool forceTrigger;
        Action<string> calibrationDone;
        Action<ScopeSettings, string> autosetDone;

        ISampleSource source;
        AppliedState applied;
        DateTime lastTriggered = DateTime.MinValue;
        byte[] rawBuffer = new byte[0];
        readonly Averager averager = new Averager();

        public event Action<Frame> FrameReady;
        public event Action<AcqStatus, string> StatusChanged;
        public event Action<string> Log;

        public AcqStatus Status { get; private set; }
        public string SourceDescription { get; private set; }
        public bool Running { get { return running; } }
        public bool IsDemo { get { return demoRequested; } }

        // A real scope (not the demo signal) is connected and answering.
        public bool HardwareConnected
        {
            get
            {
                ISampleSource s = source;
                return s != null && !s.IsSimulated;
            }
        }

        sealed class AppliedState
        {
            public byte[] Gain = { 0, 0 };
            public int RateCode = -1;
            public int Channels = -1;
            public int Coupling = -1;
            public int CalHz = -1;
        }

        public AcquisitionEngine(ScopeSettings initial, Calibration calibration, bool demo)
        {
            pending = initial.Clone();
            this.calibration = calibration;
            demoRequested = demo;
            thread = new Thread(Run) { IsBackground = true, Name = "DDS120 acquisition" };
        }

        public void Start()
        {
            thread.Start();
        }

        // Changes that alter what the hardware captures abort the capture in flight; display and
        // trigger changes (level, position, probe, inversion) simply apply to the next frame.
        public void UpdateSettings(ScopeSettings s)
        {
            bool hard;
            lock (sync)
            {
                hard = CaptureDiffers(pending, s);
                pending = s.Clone();
                if (hard)
                    settingsDirty = true;
            }
            if (hard)
                AbortCurrentRead();
            wake.Set();
        }

        static bool CaptureDiffers(ScopeSettings a, ScopeSettings b)
        {
            if (a.TimebaseIndex != b.TimebaseIndex || a.CalFrequency != b.CalFrequency ||
                a.MaxRateSingle != b.MaxRateSingle || a.MaxRateDual != b.MaxRateDual)
                return true;
            for (int i = 0; i < 2; i++)
            {
                ChannelSettings x = a.Channels[i], y = b.Channels[i];
                if (x.Enabled != y.Enabled || x.GainIndex != y.GainIndex || x.AcCoupling != y.AcCoupling)
                    return true;
            }
            return false;
        }

        public void SetRunning(bool run)
        {
            running = run;
            singleShot = false;
            if (!run)
                AbortCurrentRead();
            wake.Set();
        }

        public void Single()
        {
            singleShot = true;
            running = true;
            lastTriggered = DateTime.MinValue;
            wake.Set();
        }

        public void SetDemo(bool demo)
        {
            if (demo == demoRequested)
                return;
            demoRequested = demo;
            AbortCurrentRead();
            wake.Set();
        }

        public void SetKeepStockIdentity(bool keep)
        {
            keepStockIdentity = keep;
        }

        public void RequestZeroCalibration(Action<string> done)
        {
            lock (sync)
                calibrationDone = done;
            AbortCurrentRead();
            wake.Set();
        }

        // Shows the next acquisition even if it does not trigger (completes a Single, too).
        public void ForceTrigger()
        {
            forceTrigger = true;
            wake.Set();
        }

        // Looks at the inputs and works out vertical, horizontal and trigger settings for them. 'done'
        // gets the proposed settings (null if it could not run) and a one-line summary.
        public void RequestAutoset(Action<ScopeSettings, string> done)
        {
            lock (sync)
                autosetDone = done;
            AbortCurrentRead();
            wake.Set();
        }

        void AbortCurrentRead()
        {
            ISampleSource s = source;
            if (s != null)
            {
                try { s.AbortRead(); } catch (Exception) { }
            }
        }

        string lastStatusText;

        void SetStatus(AcqStatus status, string text)
        {
            if (Status == status && (text == null || text == lastStatusText))
                return;
            Status = status;
            if (text != null)
                lastStatusText = text;
            StatusChanged?.Invoke(status, text);
        }

        void Say(string line)
        {
            Log?.Invoke(line);
        }

        void Run()
        {
            while (!disposed)
            {
                try
                {
                    if (source != null && source.IsSimulated != demoRequested)
                        DropSource(null);
                    if (source == null && !TryConnect())
                    {
                        wake.WaitOne(1500);
                        continue;
                    }

                    Action<string> cal;
                    lock (sync)
                    {
                        cal = calibrationDone;
                        calibrationDone = null;
                    }
                    if (cal != null)
                    {
                        string result = RunZeroCalibration();
                        cal(result);
                        continue;
                    }

                    Action<ScopeSettings, string> auto;
                    ScopeSettings current;
                    lock (sync)
                    {
                        auto = autosetDone;
                        autosetDone = null;
                        current = pending.Clone();
                    }
                    if (auto != null)
                    {
                        string summary;
                        ScopeSettings proposed = RunAutoset(current, out summary);
                        auto(proposed, summary);
                        continue;
                    }

                    if (!running)
                    {
                        SetStatus(AcqStatus.Stopped, null);
                        wake.WaitOne(200);
                        continue;
                    }
                    CaptureOnce();
                }
                catch (UsbException ex)
                {
                    if (disposed)
                        break;
                    DropSource("Scope disconnected: " + ex.Message);
                    wake.WaitOne(1000);
                }
                catch (Exception ex)
                {
                    if (disposed)
                        break;
                    Say("Error: " + ex.Message);
                    DropSource("Error: " + ex.Message);
                    wake.WaitOne(1500);
                }
            }
            DropSource(null);
        }

        bool TryConnect()
        {
            if (demoRequested)
            {
                source = new SimulatedSource();
            }
            else
            {
                try
                {
                    source = Dds120Device.Connect(keepStockIdentity, line =>
                    {
                        Say(line);
                        SetStatus(AcqStatus.Connecting, line);
                    }, CancellationToken.None);
                }
                catch (DeviceNotReadyException ex)
                {
                    SetStatus(AcqStatus.NoDevice, ex.Message);
                    return false;
                }
                catch (UsbException ex)
                {
                    SetStatus(AcqStatus.NoDevice, ex.Message);
                    return false;
                }
            }
            applied = new AppliedState();
            SourceDescription = source.Description;
            Say("Connected: " + source.Description);
            SetStatus(running ? AcqStatus.Waiting : AcqStatus.Stopped, "Connected: " + source.Description);
            return true;
        }

        void DropSource(string reason)
        {
            ISampleSource s = source;
            source = null;
            SourceDescription = null;
            if (s != null)
            {
                try { s.Dispose(); } catch (Exception) { }
            }
            if (reason != null)
            {
                Say(reason);
                SetStatus(AcqStatus.NoDevice, reason);
            }
        }

        ScopeSettings Snapshot()
        {
            lock (sync)
            {
                settingsDirty = false;
                return pending.Clone();
            }
        }

        bool SettingsChanged
        {
            get { lock (sync) return settingsDirty; }
        }

        // A capture cut short for one of these is expected, not worth a log line.
        bool RequestPending
        {
            get { lock (sync) return settingsDirty || calibrationDone != null || autosetDone != null; }
        }

        void Apply(ScopeSettings s, RateStep rate)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                byte g = Dds120Model.Gains[s.Channels[ch].GainIndex].HardwareGain;
                if (applied.Gain[ch] != g)
                {
                    source.SetGain(ch, g);
                    applied.Gain[ch] = g;
                }
            }
            int coupling = (s.Channels[0].AcCoupling ? 0 : 1) | (s.Channels[1].AcCoupling ? 0 : 2);
            if (applied.Coupling != coupling)
            {
                source.SetCoupling(!s.Channels[0].AcCoupling, !s.Channels[1].AcCoupling);
                applied.Coupling = coupling;
            }
            if (applied.Channels != s.ActiveChannelCount)
            {
                source.SetChannelCount(s.ActiveChannelCount);
                applied.Channels = s.ActiveChannelCount;
            }
            if (applied.RateCode != rate.RateCode)
            {
                source.SetSampleRate(rate.RateCode);
                applied.RateCode = rate.RateCode;
            }
            if (applied.CalHz != s.CalFrequency)
            {
                source.SetCalibrationFrequency(s.CalFrequency);
                applied.CalHz = s.CalFrequency;
            }
        }

        void CaptureOnce()
        {
            ScopeSettings s = Snapshot();
            RateStep rate = s.ChooseRate();
            Apply(s, rate);

            int nch = s.ActiveChannelCount;
            int screen = Math.Max(2, (int)Math.Round(rate.SampleRate * s.SecondsPerDiv * ScopeSettings.Divisions));
            int record = screen <= 5000 ? 2 * screen : screen + screen / 2;
            record = Math.Max(record, 4096);
            int rawPerChannel = record * rate.Downsample + DropRawSamples;
            int bytes = (rawPerChannel * nch + 511) / 512 * 512;
            if (rawBuffer.Length < bytes)
                rawBuffer = new byte[bytes];

            double seconds = bytes / (rate.RawRate * nch);
            uint timeout = (uint)(seconds * 1500 + 1000);
            var sw = Stopwatch.StartNew();
            source.Start();
            int got = source.Read(rawBuffer, bytes, timeout);
            if (got < bytes)
            {
                // Aborted for a settings change, or the device stalled; just go again.
                if (!RequestPending && running && !disposed)
                    Say("Short read: " + got + " of " + bytes + " bytes after " + sw.ElapsedMilliseconds + " ms");
                return;
            }
            lock (sync)
            {
                if (settingsDirty)
                    return; // captured with stale hardware settings
                s = pending.Clone(); // pick up trigger/display changes made during the capture
            }

            Frame f = Process(s, rate, nch, record, screen);
            if (s.Acquire == AcquireMode.Average)
            {
                Frame avg = averager.Add(f, s);
                if (avg != null)
                    f = avg;
            }
            else
            {
                averager.Reset();
            }
            bool forced = forceTrigger;
            forceTrigger = false;
            bool show;
            if (f.Triggered)
            {
                lastTriggered = DateTime.UtcNow;
                show = true;
            }
            else
            {
                show = forced || s.TriggerMode == TriggerMode.Auto && !singleShot &&
                       (DateTime.UtcNow - lastTriggered).TotalSeconds > Math.Max(0.3, 2.5 * seconds);
            }
            if (show)
            {
                SetStatus(f.Triggered ? AcqStatus.Triggered : AcqStatus.Untriggered, null);
                FrameReady?.Invoke(f);
                if (singleShot && (f.Triggered || forced))
                {
                    singleShot = false;
                    running = false;
                    SetStatus(AcqStatus.Stopped, null);
                }
            }
            else
            {
                SetStatus(AcqStatus.Waiting, null);
            }
        }

        Frame Process(ScopeSettings s, RateStep rate, int nch, int record, int screen)
        {
            var f = new Frame
            {
                Settings = s,
                SampleRate = rate.SampleRate,
                SampleCount = record,
                ScreenSamples = screen,
                Time = DateTime.Now,
            };
            int ds = rate.Downsample;
            AcquireMode mode = s.Acquire;
            bool peak = mode == AcquireMode.PeakDetect && ds > 1;
            for (int ch = 0; ch < nch; ch++)
            {
                ChannelSettings cs = s.Channels[ch];
                if (!cs.Enabled)
                    continue;
                GainStep gain = Dds120Model.Gains[cs.GainIndex];
                int gi = Calibration.CodeIndex(gain.HardwareGain);
                double zero = Dds120Model.AdcZero + calibration.ZeroOffset[ch, gi];
                double perRaw = calibration.GainFactor[ch, gi] / gain.CountsPerVolt * cs.Probe * (cs.Inverted ? -1 : 1);
                double scale = perRaw / ds;
                var v = new float[record];
                float[] mn = peak ? new float[record] : null, mx = peak ? new float[record] : null;
                int idx = DropRawSamples * nch + ch;
                int step = nch;
                bool clipped = false;
                byte[] raw = rawBuffer;
                for (int k = 0; k < record; k++)
                {
                    int first = raw[idx], sum = 0, lo = 255, hi = 0;
                    for (int j = 0; j < ds; j++)
                    {
                        int b = raw[idx];
                        if (b == 0 || b == 255)
                            clipped = true;
                        sum += b;
                        if (b < lo) lo = b;
                        if (b > hi) hi = b;
                        idx += step;
                    }
                    v[k] = mode == AcquireMode.Sample
                        ? (float)((first - zero) * perRaw)
                        : (float)((sum - zero * ds) * scale);
                    if (peak)
                    {
                        float a = (float)((lo - zero) * perRaw), b = (float)((hi - zero) * perRaw);
                        mn[k] = Math.Min(a, b); // inverted channels swap them
                        mx[k] = Math.Max(a, b);
                    }
                }
                f.Volts[ch] = v;
                f.MinEnvelope[ch] = mn;
                f.MaxEnvelope[ch] = mx;
                f.Clipped[ch] = clipped;
            }

            int src = s.TriggerSource;
            if (f.Volts[src] == null)
                src = f.Volts[0] != null ? 0 : 1;
            int pre = (int)Math.Round(s.TriggerPosition * screen);
            float[] tv = TriggerSignal(f.Volts[src], s.TriggerCoupling, f.SampleRate);
            double trig = FindTrigger(tv, s, src, pre, record - (screen - pre));
            if (!double.IsNaN(trig))
            {
                f.Triggered = true;
                f.TriggerIndex = trig;
                f.ScreenStart = trig - s.TriggerPosition * screen;
            }
            else
            {
                f.ScreenStart = record - screen;
            }
            return f;
        }

        // The trigger source as the trigger circuit sees it. The rejects use a centred moving average
        // over ~20 µs (a ~50 kHz corner), so they do not shift the trigger point in time.
        static float[] TriggerSignal(float[] v, TriggerCoupling coupling, double rate)
        {
            if (v == null || coupling == TriggerCoupling.DC || coupling == TriggerCoupling.NoiseReject)
                return v;
            if (coupling == TriggerCoupling.AC)
            {
                double mean = 0;
                foreach (float x in v) mean += x;
                mean /= v.Length;
                var ac = new float[v.Length];
                for (int i = 0; i < v.Length; i++) ac[i] = (float)(v[i] - mean);
                return ac;
            }
            int half = (int)Math.Min(v.Length / 4, Math.Round(rate / 50e3 / 2));
            if (half < 1)
                return coupling == TriggerCoupling.HFReject ? v : new float[v.Length]; // nothing above 50 kHz at this rate
            var prefix = new double[v.Length + 1];
            for (int i = 0; i < v.Length; i++) prefix[i + 1] = prefix[i] + v[i];
            var r = new float[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                int a = Math.Max(0, i - half), b = Math.Min(v.Length, i + half + 1);
                double avg = (prefix[b] - prefix[a]) / (b - a);
                r[i] = coupling == TriggerCoupling.HFReject ? (float)avg : (float)(v[i] - avg);
            }
            return r;
        }

        // Edge trigger with hysteresis; returns the interpolated crossing index in [from, to) or NaN.
        static double FindTrigger(float[] v, ScopeSettings s, int ch, int from, int to)
        {
            if (v == null)
                return double.NaN;
            ChannelSettings cs = s.Channels[ch];
            GainStep g = Dds120Model.Gains[cs.GainIndex];
            double hyst = Math.Max(0.1 * cs.VoltsPerDiv, 1.5 / g.CountsPerVolt * cs.Probe);
            if (s.TriggerCoupling == TriggerCoupling.NoiseReject)
                hyst = Math.Max(0.4 * cs.VoltsPerDiv, 4 * hyst);
            double level = s.TriggerLevel;
            bool rising = s.TriggerEdge == TriggerEdge.Rising;
            bool armed = false;
            to = Math.Min(to, v.Length);
            for (int i = 1; i < to; i++)
            {
                double a = v[i - 1], b = v[i];
                if (rising)
                {
                    if (b < level - hyst)
                        armed = true;
                    else if (armed && b >= level && a < level)
                    {
                        if (i >= from)
                            return i - 1 + (level - a) / (b - a);
                        armed = false;
                    }
                }
                else
                {
                    if (b > level + hyst)
                        armed = true;
                    else if (armed && b <= level && a > level)
                    {
                        if (i >= from)
                            return i - 1 + (a - level) / (a - b);
                        armed = false;
                    }
                }
            }
            return double.NaN;
        }

        // With the inputs disconnected (or shorted), measure the ADC offset of both channels at every
        // hardware gain. Returns null on success, otherwise the reason.
        string RunZeroCalibration()
        {
            SetStatus(AcqStatus.Calibrating, "Calibrating zero offsets...");
            const int samples = 65536;
            int bytes = ((samples + DropRawSamples) * 2 + 511) / 512 * 512;
            var buf = new byte[bytes];
            var result = new double[2, 5];
            try
            {
                source.SetCoupling(true, true);
                source.SetChannelCount(2);
                source.SetSampleRate(1);
                for (int gi = 0; gi < Dds120Model.HardwareGainCodes.Length; gi++)
                {
                    byte code = Dds120Model.HardwareGainCodes[gi];
                    source.SetGain(0, code);
                    source.SetGain(1, code);
                    Thread.Sleep(30); // let the front-end relays and amplifiers settle
                    source.Start();
                    if (source.Read(buf, bytes, 3000) < bytes)
                        return "The scope did not deliver data.";
                    for (int ch = 0; ch < 2; ch++)
                    {
                        double sum = 0, sq = 0;
                        int n = 0;
                        for (int i = DropRawSamples * 2 + ch; i < bytes; i += 2)
                        {
                            sum += buf[i];
                            sq += buf[i] * (double)buf[i];
                            n++;
                        }
                        double mean = sum / n;
                        double sd = Math.Sqrt(Math.Max(0, sq / n - mean * mean));
                        if (sd > 6 || Math.Abs(mean - Dds120Model.AdcZero) > 40)
                            return string.Format("CH{0} sees a signal (x{1}: mean {2:F1}, noise {3:F1} counts). " +
                                "Disconnect both inputs and try again.", ch + 1, code, mean, sd);
                        result[ch, gi] = mean - Dds120Model.AdcZero;
                    }
                }
            }
            finally
            {
                applied = new AppliedState();
            }
            for (int ch = 0; ch < 2; ch++)
                for (int gi = 0; gi < 5; gi++)
                    calibration.ZeroOffset[ch, gi] = result[ch, gi];
            Say(string.Format("Zero offsets CH1: {0:F1} {1:F1} {2:F1} {3:F1} {4:F1}  CH2: {5:F1} {6:F1} {7:F1} {8:F1} {9:F1}",
                result[0, 0], result[0, 1], result[0, 2], result[0, 3], result[0, 4],
                result[1, 0], result[1, 1], result[1, 2], result[1, 3], result[1, 4]));
            SetStatus(running ? AcqStatus.Waiting : AcqStatus.Stopped, "Zero calibration done");
            return null;
        }

        // ---- autoset ------------------------------------------------------------------------

        // Captures both channels DC coupled: first at 1 MS/s, stepping each channel's gain until the
        // signal fills the ADC without clipping, then at 15 MS/s, 1 MS/s and 100 kS/s until a period is
        // found. Volts/div, position, timebase and trigger follow from those numbers.
        ScopeSettings RunAutoset(ScopeSettings current, out string summary)
        {
            SetStatus(AcqStatus.Autoset, "Autoset...");
            ScopeSettings s = current.Clone();
            byte[] codes = Dds120Model.HardwareGainCodes;
            try
            {
                source.SetCoupling(true, true);
                source.SetChannelCount(2);
                int[] gi = { 0, 0 };
                var lo = new double[2];
                var hi = new double[2];
                var overRange = new bool[2];
                for (int pass = 0; pass < 6; pass++)
                {
                    for (int ch = 0; ch < 2; ch++)
                        source.SetGain(ch, codes[gi[ch]]);
                    Thread.Sleep(30); // relays and amplifiers settle
                    bool[] clipped;
                    float[][] v = CaptureVolts(1, 16384, gi, out clipped);
                    if (v == null)
                    {
                        summary = "Autoset: the scope did not deliver data.";
                        return null;
                    }
                    bool changed = false;
                    for (int ch = 0; ch < 2; ch++)
                    {
                        lo[ch] = double.MaxValue;
                        hi[ch] = double.MinValue;
                        foreach (float x in v[ch])
                        {
                            if (x < lo[ch]) lo[ch] = x;
                            if (x > hi[ch]) hi[ch] = x;
                        }
                        double peakV = Math.Max(Math.Abs(lo[ch]), Math.Abs(hi[ch]));
                        int best = gi[ch];
                        overRange[ch] = clipped[ch] && best == 0;
                        if (clipped[ch])
                            best = Math.Max(0, best - 1);
                        else
                            while (best + 1 < codes.Length && peakV * Dds120Model.CountsPerVolt(codes[best + 1]) < 100)
                                best++;
                        if (best != gi[ch])
                        {
                            gi[ch] = best;
                            changed = true;
                        }
                    }
                    if (!changed)
                        break;
                }

                var signal = new bool[2];
                for (int ch = 0; ch < 2; ch++)
                    signal[ch] = (hi[ch] - lo[ch]) * Dds120Model.CountsPerVolt(codes[gi[ch]]) >= 8;

                var freq = new[] { double.NaN, double.NaN };
                byte[] rateCodes = { 15, 1, 10 };
                int[] lengths = { 32768, 65536, 65536 };
                for (int r = 0; r < rateCodes.Length; r++)
                {
                    if ((!signal[0] || !double.IsNaN(freq[0])) && (!signal[1] || !double.IsNaN(freq[1])))
                        break;
                    bool[] clipped;
                    float[][] v = CaptureVolts(rateCodes[r], lengths[r], gi, out clipped);
                    if (v == null)
                        break;
                    for (int ch = 0; ch < 2; ch++)
                    {
                        if (!signal[ch] || !double.IsNaN(freq[ch]))
                            continue;
                        ChannelMeasurement m = Measurements.Measure(v[ch], null, null, Dds120Model.RawRate(rateCodes[r]),
                            0, lengths[r], 6 / Dds120Model.CountsPerVolt(codes[gi[ch]]));
                        if (m != null && m.Rises.Count >= 3)
                            freq[ch] = m.Frequency;
                    }
                }

                if (!signal[0] && !signal[1])
                {
                    summary = "Autoset: no signal found on CH1 or CH2.";
                    s.TriggerMode = TriggerMode.Auto;
                    return s;
                }

                int on = (signal[0] ? 1 : 0) + (signal[1] ? 1 : 0);
                double target = on == 2 ? 3.6 : 6;   // divisions of peak-to-peak
                var parts = new System.Collections.Generic.List<string>();
                for (int ch = 0; ch < 2; ch++)
                {
                    ChannelSettings c = s.Channels[ch];
                    c.Enabled = signal[ch];
                    if (!signal[ch])
                        continue;
                    c.AcCoupling = false;
                    double vpp = hi[ch] - lo[ch], mid = (hi[ch] + lo[ch]) / 2;
                    double peakV = Math.Max(Math.Abs(lo[ch]), Math.Abs(hi[ch]));
                    int pick = Dds120Model.Gains.Length - 1;
                    for (int i = 0; i < Dds120Model.Gains.Length; i++)
                    {
                        GainStep g = Dds120Model.Gains[i];
                        if (vpp / g.VoltsPerDiv <= target && peakV * g.CountsPerVolt <= 118)
                        {
                            pick = i;
                            break;
                        }
                    }
                    c.GainIndex = pick;
                    double center = on == 2 ? (ch == 0 ? 2 : -2) : 0;
                    double pos = center - mid / Dds120Model.Gains[pick].VoltsPerDiv * (c.Inverted ? -1 : 1);
                    c.Position = Math.Round(Math.Max(-4, Math.Min(4, pos)) * 10) / 10;
                    parts.Add(string.Format("CH{0} {1} {2}", ch + 1,
                        double.IsNaN(freq[ch]) ? "DC" : Dds120.Util.Si.Format(freq[ch], "Hz", 4),
                        overRange[ch] ? "over range" : Dds120.Util.Si.Format(vpp * c.Probe, "V") + " p-p"));
                }
                s.XY = false;

                int trig = !double.IsNaN(freq[0]) && signal[0] ? 0 : !double.IsNaN(freq[1]) && signal[1] ? 1 : signal[0] ? 0 : 1;
                ChannelSettings tc = s.Channels[trig];
                s.TriggerSource = trig;
                s.TriggerEdge = TriggerEdge.Rising;
                s.TriggerMode = TriggerMode.Auto;
                s.TriggerCoupling = TriggerCoupling.DC;
                s.TriggerPosition = 0.5;
                double step = tc.VoltsPerDiv / 50;
                s.TriggerLevel = Math.Round((hi[trig] + lo[trig]) / 2 * tc.Probe * (tc.Inverted ? -1 : 1) / step) * step;
                if (!double.IsNaN(freq[trig]))
                {
                    // the fastest timebase that still shows two periods
                    s.TimebaseIndex = ScopeSettings.Timebases.Length - 1;
                    for (int i = 0; i < ScopeSettings.Timebases.Length; i++)
                        if (ScopeSettings.Timebases[i] * ScopeSettings.Divisions >= 2 / freq[trig])
                        {
                            s.TimebaseIndex = i;
                            break;
                        }
                }
                summary = "Autoset: " + string.Join(", ", parts);
                return s;
            }
            finally
            {
                applied = new AppliedState();
                SetStatus(running ? AcqStatus.Waiting : AcqStatus.Stopped, null);
            }
        }

        // One capture of both channels at a rate code, in volts at the BNC (no probe factor).
        float[][] CaptureVolts(byte rateCode, int samples, int[] gainIndex, out bool[] clipped)
        {
            clipped = new bool[2];
            source.SetSampleRate(rateCode);
            int bytes = ((samples + DropRawSamples) * 2 + 511) / 512 * 512;
            var buf = new byte[bytes];
            double seconds = bytes / (Dds120Model.RawRate(rateCode) * 2);
            source.Start();
            if (source.Read(buf, bytes, (uint)(seconds * 1500 + 1000)) < bytes)
                return null;
            var v = new float[2][];
            for (int ch = 0; ch < 2; ch++)
            {
                int g = gainIndex[ch];
                double zero = Dds120Model.AdcZero + calibration.ZeroOffset[ch, g];
                double scale = calibration.GainFactor[ch, g] / Dds120Model.CountsPerVolt(Dds120Model.HardwareGainCodes[g]);
                var x = new float[samples];
                for (int k = 0; k < samples; k++)
                {
                    int b = buf[(DropRawSamples + k) * 2 + ch];
                    if (b == 0 || b == 255)
                        clipped[ch] = true;
                    x[k] = (float)((b - zero) * scale);
                }
                v[ch] = x;
            }
            return v;
        }

        public void Dispose()
        {
            disposed = true;
            AbortCurrentRead();
            wake.Set();
            if (thread.IsAlive && !thread.Join(3000))
                Say("Acquisition thread did not stop in time");
        }

        // Average mode: a running average of triggered acquisitions, aligned on the trigger point with
        // linear interpolation (the trigger falls between samples). It starts over whenever the
        // waveform's scale or timing changes.
        sealed class Averager
        {
            readonly float[][] acc = new float[2][];
            int count, length;
            double pre;
            string key;

            public void Reset()
            {
                count = 0;
                key = null;
            }

            public Frame Add(Frame f, ScopeSettings s)
            {
                if (!f.Triggered)
                    return null;
                string k = Key(f, s);
                if (k != key)
                {
                    key = k;
                    count = 0;
                    length = f.SampleCount;
                    pre = Math.Round(f.TriggerIndex);
                    for (int ch = 0; ch < 2; ch++)
                        acc[ch] = f.Volts[ch] != null ? new float[length] : null;
                }
                count = Math.Min(count + 1, s.AverageCount);
                float w = 1f / count;
                double shift = f.TriggerIndex - pre;
                var result = new Frame
                {
                    Settings = f.Settings,
                    SampleRate = f.SampleRate,
                    SampleCount = length,
                    ScreenSamples = f.ScreenSamples,
                    Time = f.Time,
                    Triggered = true,
                    TriggerIndex = pre,
                    ScreenStart = pre - s.TriggerPosition * f.ScreenSamples,
                    Averaged = count,
                };
                for (int ch = 0; ch < 2; ch++)
                {
                    float[] v = f.Volts[ch], a = acc[ch];
                    if (v == null || a == null)
                        continue;
                    for (int i = 0; i < length; i++)
                    {
                        double t = i + shift;
                        int j = (int)Math.Floor(t);
                        if (j < 0 || j >= v.Length)
                            continue;
                        float x = j + 1 < v.Length ? (float)(v[j] + (v[j + 1] - v[j]) * (t - j)) : v[j];
                        a[i] += (x - a[i]) * w;
                    }
                    result.Volts[ch] = (float[])a.Clone();
                    result.Clipped[ch] = f.Clipped[ch];
                }
                return result;
            }

            static string Key(Frame f, ScopeSettings s)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(f.SampleRate).Append('|').Append(f.SampleCount).Append('|').Append(f.ScreenSamples)
                  .Append('|').Append(s.TriggerPosition).Append('|').Append(s.TriggerSource);
                foreach (ChannelSettings c in f.Settings.Channels)
                    sb.Append('|').Append(c.Enabled).Append(c.GainIndex).Append(c.AcCoupling).Append(c.Probe).Append(c.Inverted);
                return sb.ToString();
            }
        }
    }
}
