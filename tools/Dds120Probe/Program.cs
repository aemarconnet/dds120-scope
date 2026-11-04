using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Dds120.Scope;
using Dds120.Usb;

// Command-line bring-up and diagnostics tool for the DDS-120.
//   Dds120Probe list                      all USB devices Windows reports
//   Dds120Probe info                      descriptors of the scope as it is now (no upload)
//   Dds120Probe load [--native-id]        upload firmware if needed, show the resulting device
//   Dds120Probe capture [options]         capture raw samples and print statistics
//        --rate <code>   firmware rate code (default 8 = 8 MS/s)
//        --ch <1|2>      channel count (default 2)
//        --gain <g>      hardware gain code for both channels (1,2,5,10,20; default 1)
//        --bytes <n>     bytes to read (default 1048576)
//        --frames <n>    repeat count (default 3)
//        --ac            AC coupling (default DC)
//        --cal <hz>      calibration output frequency
//        --gain2 <g>     CH2 gain if different, --dump <file> writes the raw bytes
//   Dds120Probe selftest [--hw]            run the acquisition engine (demo signal, or the scope with
//                                          --hw) through autoset, measurements, acquire modes, math,
//                                          FFT and trigger couplings, printing the results
internal static class Program
{
    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "info";
        try
        {
            switch (cmd)
            {
                case "list": return List();
                case "info": return Info();
                case "load": return Load(args.Contains("--native-id"));
                case "capture": return Capture(args);
                case "msos": return MsOs();
                case "icon": return WriteIcon(args.Length > 1 ? args[1] : "app.ico");
                case "patchdump": return PatchDump();
                case "selftest": return SelfTest(!args.Contains("--hw"));
                default:
                    Console.Error.WriteLine("Unknown command " + cmd);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    static string Opt(string[] args, string name, string def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }

    static void Log(string s)
    {
        Console.WriteLine("  [" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s);
    }

    static int List()
    {
        foreach (UsbDeviceEntry e in WinUsbDevice.Enumerate())
            Console.WriteLine(e);
        return 0;
    }

    static int Info()
    {
        var found = WinUsbDevice.Enumerate().Where(e =>
            e.VendorId == Dds120Device.StockVid && e.ProductId == Dds120Device.StockPid ||
            e.VendorId == Dds120Device.FwVid && e.ProductId == Dds120Device.FwPid).ToList();
        if (found.Count == 0)
        {
            Console.WriteLine("No DDS-120 candidates (8102:8102 or 04B5:0120) present.");
            return 1;
        }
        foreach (UsbDeviceEntry e in found)
        {
            Console.WriteLine(e);
            using (WinUsbDevice dev = WinUsbDevice.Open(e.Path))
            {
                UsbDeviceDescriptor d = dev.GetDeviceDescriptor();
                Console.WriteLine("  " + d);
                Console.WriteLine("  manufacturer: " + dev.GetString(d.ManufacturerIndex));
                Console.WriteLine("  product:      " + dev.GetString(d.ProductIndex));
                Console.WriteLine("  serial:       " + dev.GetString(d.SerialIndex));
                byte[] cfg = dev.GetConfigurationDescriptor();
                Console.WriteLine("  config descriptor (" + cfg.Length + " bytes):");
                DumpDescriptors(cfg);
                foreach (UsbPipe p in dev.QueryPipes())
                    Console.WriteLine("  pipe " + p);
            }
        }
        return 0;
    }

    static void DumpDescriptors(byte[] cfg)
    {
        int i = 0;
        while (i + 2 <= cfg.Length && cfg[i] > 0)
        {
            int len = Math.Min(cfg[i], cfg.Length - i);
            var sb = new StringBuilder("    ");
            for (int k = 0; k < len; k++)
                sb.Append(cfg[i + k].ToString("X2")).Append(' ');
            Console.WriteLine(sb.ToString().TrimEnd());
            i += len;
        }
    }

    // Renders the app icon at several sizes into one .ico (PNG-compressed entries, Vista+).
    static int WriteIcon(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 256 };
        var images = new System.Collections.Generic.List<byte[]>();
        foreach (int size in sizes)
            using (var bmp = Dds120.UI.AppIcon.Render(size))
            using (var ms = new System.IO.MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                images.Add(ms.ToArray());
            }
        using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images)
                w.Write(img);
        }
        Console.WriteLine("Wrote " + path);
        return 0;
    }

    // Issues the request Windows sends for the MS OS extended compat ID (vendor code 0x01, wIndex 4).
    static int MsOs()
    {
        foreach (UsbDeviceEntry e in WinUsbDevice.Enumerate().Where(x => x.VendorId == Dds120Device.StockVid && x.ProductId == Dds120Device.StockPid))
        {
            using (WinUsbDevice dev = WinUsbDevice.Open(e.Path))
            {
                Console.WriteLine(e);
                Console.WriteLine("  product: " + dev.GetString(dev.GetDeviceDescriptor().ProductIndex));
                foreach (int len in new[] { 16, 40 })
                {
                    byte[] d = dev.ControlIn(0x01, 0, 4, len);
                    Console.WriteLine("  compat ID (" + len + "): " + BitConverter.ToString(d));
                }
            }
        }
        return 0;
    }

    // Writes the patched firmware image as Intel HEX-free binary for inspection.
    static int PatchDump()
    {
        Fx2Firmware fw = Dds120Device.LoadFirmware();
        string refused = Dds120FirmwarePatch.Apply(fw, Dds120Device.StockVid, Dds120Device.StockPid, 0);
        Console.WriteLine(refused ?? "patch applied");
        var image = new byte[Fx2Firmware.RamSize];
        foreach (HexSegment s in fw.Segments())
        {
            Console.WriteLine(string.Format("  0x{0:X4}-0x{1:X4}", s.Address, s.Address + s.Data.Length - 1));
            Array.Copy(s.Data, 0, image, s.Address, s.Data.Length);
        }
        System.IO.File.WriteAllBytes("patched-firmware.bin", image);
        return refused == null ? 0 : 1;
    }

    // ---- selftest -------------------------------------------------------------------------

    sealed class EngineProbe : IDisposable
    {
        public readonly ScopeSettings Settings = new ScopeSettings();
        public readonly AcquisitionEngine Engine;
        readonly object gate = new object();
        Frame last;
        int count;

        public EngineProbe(bool demo)
        {
            Settings.CalFrequency = 1000;
            Engine = new AcquisitionEngine(Settings, new Calibration(), demo);
            Engine.FrameReady += f => { lock (gate) { last = f; count++; Monitor.PulseAll(gate); } };
            Engine.Log += s => Log(s);
            Engine.Start();
        }

        public void Apply()
        {
            Engine.UpdateSettings(Settings);
        }

        // Waits for 'skip' more frames, then returns the next one (null on timeout).
        public Frame Next(int skip = 2, int timeoutMs = 8000)
        {
            lock (gate)
            {
                int target = count + skip + 1;
                var sw = Stopwatch.StartNew();
                while (count < target)
                {
                    int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (left <= 0 || !Monitor.Wait(gate, left))
                        return null;
                }
                return last;
            }
        }

        public void Dispose()
        {
            Engine.Dispose();
        }
    }

    static void PrintMeasurements(string label, ChannelMeasurement m, string unit)
    {
        if (m == null)
        {
            Console.WriteLine("  " + label + ": (none)");
            return;
        }
        var parts = ((MeasureType[])Enum.GetValues(typeof(MeasureType))).Select(t => m.Format(t, unit));
        Console.WriteLine("  " + label + ": " + string.Join(" | ", parts));
    }

    static int SelfTest(bool demo)
    {
        int failures = 0;
        Action<bool, string> check = (ok, what) =>
        {
            Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
            if (!ok) failures++;
        };
        using (var p = new EngineProbe(demo))
        {
            Console.WriteLine("Autoset");
            var done = new ManualResetEvent(false);
            ScopeSettings proposed = null;
            string summary = null;
            if (p.Next(0, 15000) == null)
            {
                Console.WriteLine("  no frames from the source");
                return 1;
            }
            p.Engine.RequestAutoset((s, text) => { proposed = s; summary = text; done.Set(); });
            check(done.WaitOne(20000), "autoset finished");
            Console.WriteLine("  " + summary);
            if (proposed != null)
            {
                Console.WriteLine(string.Format("  timebase {0}/div, trigger CH{1} {2} V; CH1 {3} {4}/div pos {5}; CH2 {6} {7}/div pos {8}",
                    Dds120.Util.Si.Short(proposed.SecondsPerDiv, "s"), proposed.TriggerSource + 1, proposed.TriggerLevel,
                    proposed.Channels[0].Enabled ? "on" : "off", proposed.Channels[0].VoltsPerDiv, proposed.Channels[0].Position,
                    proposed.Channels[1].Enabled ? "on" : "off", proposed.Channels[1].VoltsPerDiv, proposed.Channels[1].Position));
                p.Settings.CopyFrom(proposed);
                p.Apply();
            }

            Console.WriteLine("Measurements (Hi-Res)");
            Frame f = p.Next();
            check(f != null && f.Triggered, "triggered frame after autoset");
            if (f == null)
                return 1;
            Analysis a = Analysis.Of(f, p.Settings);
            Console.WriteLine(string.Format("  {0} S/s, {1} samples, screen {2}", f.SampleRate, f.SampleCount, f.ScreenSamples));
            PrintMeasurements("CH1", a.Channels[0], "V");
            PrintMeasurements("CH2", a.Channels[1], "V");
            if (demo)
            {
                check(Math.Abs(a.Channels[0].Frequency - 1000) < 5, "CH1 frequency 1 kHz");
                check(Math.Abs(a.Channels[1].Frequency - 2200) < 10, "CH2 frequency 2.2 kHz");
                check(Math.Abs(a.Channels[0].Amplitude - 2.0) < 0.15, "CH1 amplitude 2 V");
                check(Math.Abs(a.Channels[0].Duty - 0.5) < 0.02, "CH1 duty 50 %");
            }

            foreach (AcquireMode mode in new[] { AcquireMode.Sample, AcquireMode.PeakDetect, AcquireMode.HiRes })
            {
                p.Settings.Acquire = mode;
                p.Settings.TimebaseIndex = 12; // 10 ms/div: 1 MS/s raw averaged by 100
                p.Apply();
                f = p.Next();
                if (f == null) { check(false, mode + " frame"); continue; }
                a = Analysis.Of(f, p.Settings);
                Console.WriteLine(string.Format("  {0,-10} CH1 Pk-Pk {1:F3} V, CH2 RMS {2:F3} V, envelope {3}", mode,
                    a.Channels[0].PeakToPeak, a.Channels[1].Rms, f.MinEnvelope[0] != null ? "yes" : "no"));
            }
            p.Settings.CopyFrom(proposed ?? p.Settings);

            Console.WriteLine("Average");
            p.Settings.Acquire = AcquireMode.Average;
            p.Settings.AverageCount = 16;
            p.Apply();
            f = p.Next(18);
            check(f != null && f.Averaged == 16, "16 acquisitions averaged");
            if (f != null)
            {
                a = Analysis.Of(f, p.Settings);
                PrintMeasurements("CH1 avg", a.Channels[0], "V");
            }
            p.Settings.Acquire = AcquireMode.HiRes;

            Console.WriteLine("Math");
            p.Settings.MathMode = MathFunction.Subtract;
            p.Apply();
            f = p.Next();
            a = Analysis.Of(f, p.Settings);
            check(a.MathWave != null, "CH1−CH2 waveform");
            PrintMeasurements("CH1−CH2", a.MathMeasurement, "V");
            p.Settings.MathMode = MathFunction.Fft;
            p.Settings.FftSource = 1;
            foreach (FftWindow w in Enum.GetValues(typeof(FftWindow)))
            {
                p.Settings.FftWindow = w;
                a = Analysis.Of(f, p.Settings);
                Spectrum sp = a.Spectrum;
                Console.WriteLine(string.Format("  FFT CH2 {0,-11} N={1} bin {2:F1} Hz, peak {3:F1} Hz {4:F2} dBV",
                    w, sp.Points, sp.BinHz, sp.PeakHz, sp.PeakDbv));
                if (demo && w == FftWindow.FlatTop)
                    check(Math.Abs(sp.PeakHz - 2200) < 2 * sp.BinHz && Math.Abs(sp.PeakDbv - 20 * Math.Log10(1.5 / Math.Sqrt(2))) < 0.5,
                        "FFT peak 2.2 kHz at 0.51 dBV (flat top)");
            }
            p.Settings.MathMode = MathFunction.Off;

            Console.WriteLine("Trigger coupling");
            foreach (TriggerCoupling c in Enum.GetValues(typeof(TriggerCoupling)))
            {
                p.Settings.TriggerCoupling = c;
                if (c == TriggerCoupling.AC || c == TriggerCoupling.LFReject)
                    p.Settings.TriggerLevel = 0;  // relative to the filtered signal
                else if (proposed != null)
                    p.Settings.TriggerLevel = proposed.TriggerLevel;
                p.Settings.TriggerMode = TriggerMode.Normal;
                p.Apply();
                f = p.Next(1, 6000);
                check(f != null && f.Triggered, "triggers with " + c);
            }
        }
        Console.WriteLine(failures == 0 ? "All checks passed" : failures + " check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    static int Load(bool nativeId)
    {
        using (Dds120Device scope = Dds120Device.Connect(!nativeId, Log, CancellationToken.None))
        {
            Console.WriteLine("Connected: " + scope.Description);
            foreach (UsbPipe p in scope.Pipes())
                Console.WriteLine("  pipe " + p);
            Console.WriteLine("  max transfer size: " + scope.MaxTransferSize());
        }
        return 0;
    }

    static int Capture(string[] args)
    {
        byte rate = byte.Parse(Opt(args, "--rate", "8"));
        int channels = int.Parse(Opt(args, "--ch", "2"));
        byte gain = byte.Parse(Opt(args, "--gain", "1"));
        int bytes = int.Parse(Opt(args, "--bytes", "1048576"));
        int frames = int.Parse(Opt(args, "--frames", "3"));
        bool ac = args.Contains("--ac");
        string cal = Opt(args, "--cal", null);
        byte gain2 = byte.Parse(Opt(args, "--gain2", gain.ToString()));
        string dump = Opt(args, "--dump", null);

        using (Dds120Device scope = Dds120Device.Connect(true, Log, CancellationToken.None))
        {
            Console.WriteLine("Connected: " + scope.Description);
            scope.SetGain(0, gain);
            scope.SetGain(1, gain2);
            scope.SetCoupling(!ac, !ac);
            scope.SetChannelCount(channels);
            scope.SetSampleRate(rate);
            if (cal != null)
                scope.SetCalibrationFrequency(int.Parse(cal));

            var buf = new byte[bytes];
            for (int f = 0; f < frames; f++)
            {
                var sw = Stopwatch.StartNew();
                scope.Start();
                int got = scope.Read(buf, bytes, 5000);
                double ms = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine(string.Format("frame {0}: {1} of {2} bytes in {3:F1} ms = {4:F2} MB/s",
                    f, got, bytes, ms, got / ms / 1000.0));
                for (int ch = 0; ch < channels; ch++)
                    Stats(buf, got, channels, ch, 0);
                // The first few KB after a start are known to be stale FIFO content.
                if (f == 0)
                    for (int ch = 0; ch < channels; ch++)
                        Blocks(buf, Math.Min(got, 16384), channels, ch);
                if (dump != null)
                {
                    string path = frames > 1 ? dump + "." + f : dump;
                    using (var fs = System.IO.File.Create(path))
                        fs.Write(buf, 0, got);
                }
            }
            scope.Stop();
        }
        return 0;
    }

    static void Stats(byte[] buf, int len, int channels, int ch, int skip)
    {
        int n = 0, min = 255, max = 0;
        long sum = 0;
        var hist = new int[256];
        for (int i = skip * channels + ch; i < len; i += channels)
        {
            int v = buf[i];
            hist[v]++;
            sum += v;
            n++;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (n == 0) return;
        int mode = Array.IndexOf(hist, hist.Max());
        Console.WriteLine(string.Format("  CH{0}: n={1} min={2} max={3} mean={4:F2} mode={5} (0x80=0V)",
            ch + 1, n, min, max, (double)sum / n, mode));
    }

    static void Blocks(byte[] buf, int len, int channels, int ch)
    {
        var sb = new StringBuilder("  CH" + (ch + 1) + " mean per 512 samples from start: ");
        int block = 512;
        for (int s = 0; s * channels < len; s += block)
        {
            long sum = 0;
            int n = 0;
            for (int i = s; i < s + block && i * channels + ch < len; i++)
            {
                sum += buf[i * channels + ch];
                n++;
            }
            if (n > 0)
                sb.Append(((double)sum / n).ToString("F1")).Append(' ');
        }
        Console.WriteLine(sb.ToString());
    }
}
