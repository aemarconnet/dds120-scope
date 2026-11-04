using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Dds120.Usb;

namespace Dds120.Scope
{
    public sealed class DeviceNotReadyException : Exception
    {
        public DeviceNotReadyException(string message) : base(message) { }
    }

    // One connected DDS120 with scope firmware running. All methods are blocking USB calls and
    // are meant to be used from a single acquisition thread (AbortRead may be called from any thread).
    public sealed class Dds120Device : ISampleSource
    {
        public const ushort StockVid = 0x8102, StockPid = 0x8102;   // EEPROM identity, firmware not loaded
        public const ushort FwVid = 0x04B5, FwPid = 0x0120;         // identity built into the scope firmware
        public const byte EndpointIn = 0x86;

        const byte ReqGainCh1 = 0xE0;
        const byte ReqGainCh2 = 0xE1;
        const byte ReqSampleRate = 0xE2;
        const byte ReqStartStop = 0xE3;
        const byte ReqNumChannels = 0xE4;
        const byte ReqCoupling = 0xE5;
        const byte ReqCalFreq = 0xE6;

        public const string FirmwareResource = "Dds120.Firmware.dds120-firmware.hex";

        readonly WinUsbDevice usb;

        public string Description { get; }
        public bool IsSimulated { get { return false; } }

        Dds120Device(WinUsbDevice usb, string description)
        {
            this.usb = usb;
            Description = description;
        }

        static bool IsCandidate(UsbDeviceEntry e)
        {
            return e.VendorId == StockVid && e.ProductId == StockPid || e.VendorId == FwVid && e.ProductId == FwPid;
        }

        // The scope firmware identifies as manufacturer "sigrok"; the stock EEPROM code does not.
        static bool RunsScopeFirmware(WinUsbDevice dev, out string description)
        {
            UsbDeviceDescriptor d = dev.GetDeviceDescriptor();
            string manufacturer = dev.GetString(d.ManufacturerIndex) ?? "";
            string product = dev.GetString(d.ProductIndex) ?? "";
            description = string.Format("{0:X4}:{1:X4} rev {2:X4} \"{3}\" \"{4}\"",
                d.VendorId, d.ProductId, d.BcdDevice, manufacturer, product);
            return d.VendorId == FwVid && d.ProductId == FwPid || manufacturer == "sigrok";
        }

        public static Fx2Firmware LoadFirmware()
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(FirmwareResource);
            if (s == null)
                throw new InvalidOperationException("Embedded firmware resource is missing");
            using (var reader = new StreamReader(s))
                return Fx2Firmware.ParseIntelHex(reader);
        }

        // Finds the scope, uploads firmware if it is still running its EEPROM code, and returns it
        // ready for capture. keepStockIdentity makes the loaded firmware re-enumerate as 8102:8102 so
        // Windows keeps the WinUSB binding it already has for the scope; otherwise it comes back as
        // 04B5:0120, which needs WinUSB bound to it once (Device Manager or Zadig).
        public static Dds120Device Connect(bool keepStockIdentity, Action<string> log, CancellationToken cancel)
        {
            List<UsbDeviceEntry> found = WinUsbDevice.Enumerate().FindAll(IsCandidate);
            if (found.Count == 0)
            {
                var nodes = WinUsbDevice.FindDeviceNodes(@"USB\VID_8102&PID_8102");
                nodes.AddRange(WinUsbDevice.FindDeviceNodes(@"USB\VID_04B5&PID_0120"));
                if (nodes.Count > 0)
                    throw new DeviceNotReadyException("The DDS-120 is plugged in, but Windows has no working driver for it right now " +
                        "(Device Manager code " + nodes[0].Value + "; it may still hold firmware from an earlier session). " +
                        "Unplug the scope and plug it back in.");
                throw new DeviceNotReadyException("No DDS-120 found. Plug the scope in (USB ID 8102:8102) and try again.");
            }

            Exception lastError = null;
            foreach (UsbDeviceEntry entry in found)
            {
                WinUsbDevice dev;
                try
                {
                    dev = WinUsbDevice.Open(entry.Path);
                }
                catch (UsbException ex)
                {
                    lastError = ex;
                    log?.Invoke("Cannot open " + entry + ": " + ex.Message);
                    continue;
                }

                string description;
                if (RunsScopeFirmware(dev, out description))
                {
                    log?.Invoke("Scope firmware already running: " + description);
                    return Prepare(dev, description);
                }

                log?.Invoke("Found scope without firmware: " + description);
                Fx2Firmware fw = LoadFirmware();
                if (keepStockIdentity)
                {
                    UsbDeviceDescriptor stock = dev.GetDeviceDescriptor();
                    string refused = Dds120FirmwarePatch.Apply(fw, stock.VendorId, stock.ProductId, stock.BcdDevice);
                    if (refused != null)
                    {
                        log?.Invoke("Cannot keep the stock USB identity (" + refused + "); using 04B5:0120");
                        keepStockIdentity = false;
                    }
                }
                log?.Invoke("Uploading firmware (" + fw.ByteCount + " bytes)...");
                fw.Upload(dev, log);
                dev.Dispose();
                return WaitForFirmware(keepStockIdentity, log, cancel);
            }
            throw new DeviceNotReadyException("The DDS-120 is plugged in, but Windows has no working driver for it right now " +
                "(it may still hold firmware from an earlier session). Unplug the scope and plug it back in." +
                (lastError != null ? " [" + lastError.Message + "]" : ""));
        }

        static Dds120Device WaitForFirmware(bool keepStockIdentity, Action<string> log, CancellationToken cancel)
        {
            var sw = Stopwatch.StartNew();
            Thread.Sleep(500);
            bool sawNewIdWithoutDriver = false;
            while (sw.ElapsedMilliseconds < 12000)
            {
                cancel.ThrowIfCancellationRequested();
                foreach (UsbDeviceEntry entry in WinUsbDevice.Enumerate().FindAll(IsCandidate))
                {
                    WinUsbDevice dev;
                    try
                    {
                        dev = WinUsbDevice.Open(entry.Path);
                    }
                    catch (UsbException)
                    {
                        if (entry.VendorId == FwVid)
                            sawNewIdWithoutDriver = true;
                        continue;
                    }
                    string description;
                    bool ready;
                    try
                    {
                        ready = RunsScopeFirmware(dev, out description);
                    }
                    catch (UsbException)
                    {
                        dev.Dispose();
                        continue;
                    }
                    if (ready)
                    {
                        log?.Invoke("Scope firmware running after " + sw.ElapsedMilliseconds + " ms: " + description);
                        return Prepare(dev, description);
                    }
                    dev.Dispose();
                }
                Thread.Sleep(250);
            }
            if (sawNewIdWithoutDriver || !keepStockIdentity)
                throw new DeviceNotReadyException("Firmware loaded, but Windows has no WinUSB driver for USB ID 04B5:0120. " +
                    "Bind WinUSB to it once (Device Manager or Zadig), or unplug/replug the scope and use the default identity mode.");
            throw new DeviceNotReadyException("The scope did not come back after the firmware upload. Unplug and replug it, then retry.");
        }

        static Dds120Device Prepare(WinUsbDevice dev, string description)
        {
            var scope = new Dds120Device(dev, description);
            try
            {
                dev.SetPipePolicy(EndpointIn, WinUsbNative.AUTO_CLEAR_STALL, true);
                scope.Stop();
                dev.FlushPipe(EndpointIn);
            }
            catch
            {
                dev.Dispose();
                throw;
            }
            return scope;
        }

        void Write(byte request, byte value)
        {
            usb.ControlOut(request, 0, 0, new[] { value });
        }

        public void SetGain(int channel, byte hardwareGain)
        {
            Write(channel == 0 ? ReqGainCh1 : ReqGainCh2, hardwareGain);
        }

        public void SetSampleRate(byte rateCode)
        {
            Write(ReqSampleRate, rateCode);
        }

        public void SetChannelCount(int channels)
        {
            Write(ReqNumChannels, (byte)channels);
        }

        public void SetCoupling(bool ch1Dc, bool ch2Dc)
        {
            Write(ReqCoupling, (byte)((ch1Dc ? 0x01 : 0) | (ch2Dc ? 0x10 : 0)));
        }

        public void SetCalibrationFrequency(int hz)
        {
            Write(ReqCalFreq, Dds120Model.CalFrequencyCode(hz));
        }

        public void Start()
        {
            Write(ReqStartStop, 1);
        }

        public void Stop()
        {
            Write(ReqStartStop, 0);
        }

        public List<UsbPipe> Pipes()
        {
            return usb.QueryPipes();
        }

        public uint MaxTransferSize()
        {
            return usb.GetMaxTransferSize(EndpointIn);
        }

        // Reads exactly count bytes (or fewer on timeout) from the sample endpoint.
        public int Read(byte[] buffer, int count, uint timeoutMs)
        {
            usb.SetPipeTimeout(EndpointIn, timeoutMs);
            int total = 0;
            while (total < count)
            {
                int got;
                try
                {
                    got = usb.ReadPipe(EndpointIn, buffer, total, count - total);
                }
                catch (UsbException ex)
                {
                    if (ex.IsTimeout)
                        break;
                    if (ex.IsAborted)
                    {
                        usb.FlushPipe(EndpointIn);
                        break;
                    }
                    throw;
                }
                if (got <= 0)
                    break;
                total += got;
            }
            return total;
        }

        public void AbortRead()
        {
            usb.AbortPipe(EndpointIn);
        }

        public void Dispose()
        {
            try
            {
                Stop();
            }
            catch (Exception)
            {
                // device may already be gone
            }
            usb.Dispose();
        }
    }
}
