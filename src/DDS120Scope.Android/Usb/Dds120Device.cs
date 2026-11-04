using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Android.Hardware.Usb;
using Dds120.Droid.Usb;
using Dds120.Usb;

namespace Dds120.Scope
{
    public sealed class DeviceNotReadyException : Exception
    {
        public DeviceNotReadyException(string message) : base(message) { }
    }

    // Android replacement for Core/Scope/Dds120Device.cs (excluded from this project). It keeps the
    // static surface the shared Core uses: AcquisitionEngine.TryConnect calls Connect(), the firmware
    // patch uses FwVid/FwPid. Connect returns the Android source instead of a WinUSB device.
    // Connect runs on the acquisition thread and never waits for the user: while Android's permission
    // dialog is open it throws DeviceNotReadyException, and the engine simply retries.
    public static class Dds120Device
    {
        public const ushort StockVid = 0x8102, StockPid = 0x8102;   // EEPROM identity, firmware not loaded
        public const ushort FwVid = 0x04B5, FwPid = 0x0120;         // identity built into the scope firmware
        public const byte EndpointIn = 0x86;

        public const string FirmwareResource = "Dds120.Firmware.dds120-firmware.hex";

        // Devices that already got the firmware but have not re-enumerated yet (by device name).
        static readonly HashSet<string> uploaded = new HashSet<string>();

        public static Fx2Firmware LoadFirmware()
        {
            Stream s = typeof(Dds120Device).Assembly.GetManifestResourceStream(FirmwareResource);
            if (s == null)
                throw new InvalidOperationException("Embedded firmware resource is missing");
            using (var reader = new StreamReader(s))
                return Fx2Firmware.ParseIntelHex(reader);
        }

        // Finds the scope, uploads firmware if it is still running its EEPROM code, and returns it
        // ready for capture. keepStockIdentity applies the Windows patch so the firmware re-enumerates
        // as 8102:8102; Android needs no driver binding, so the app passes false and gets the plain
        // OpenHantek firmware, which comes back as 04B5:0120. Both IDs are in device_filter.xml.
        public static AndroidDds120Source Connect(bool keepStockIdentity, Action<string> log, CancellationToken cancel)
        {
            List<UsbDevice> found = AndroidUsb.FindCandidates();
            lock (uploaded)
                uploaded.RemoveWhere(name => found.TrueForAll(d => d.DeviceName != name));
            if (found.Count == 0)
            {
                if (!AndroidUsb.HasUsbHost)
                    throw new DeviceNotReadyException("This phone or tablet does not report USB host (OTG) support. " +
                        "Some devices still work: connect the DDS-120 through an OTG adapter. Or turn on the demo signal.");
                throw new DeviceNotReadyException("No DDS-120 found. Connect the scope through a USB OTG adapter " +
                    "(USB ID 8102:8102), or turn on the demo signal.");
            }

            bool waiting = false, denied = false;
            foreach (UsbDevice dev in found)
            {
                if (!AndroidUsb.HasPermission(dev))
                {
                    if (AndroidUsb.WasDenied(dev))
                        denied = true;
                    else
                        waiting |= AndroidUsb.RequestPermission(dev);
                    continue;
                }
                UsbDeviceConnection conn = AndroidUsb.Manager.OpenDevice(dev);
                if (conn == null)
                {
                    log?.Invoke("Cannot open " + AndroidUsb.Describe(dev));
                    continue;
                }

                string description;
                ushort bcdDevice;
                bool ready;
                try
                {
                    ready = RunsScopeFirmware(dev, conn, out description, out bcdDevice);
                }
                catch (Exception)
                {
                    conn.Close();
                    throw;
                }
                if (ready)
                {
                    log?.Invoke("Scope firmware already running: " + description);
                    return AndroidDds120Source.Open(dev, conn, description, log);
                }
                lock (uploaded)
                {
                    if (uploaded.Contains(dev.DeviceName))
                    {
                        conn.Close();
                        throw new DeviceNotReadyException("The scope did not restart with the new firmware. Unplug it and plug it back in.");
                    }
                }

                log?.Invoke("Found scope without firmware: " + description);
                Fx2Firmware fw = LoadFirmware();
                if (keepStockIdentity)
                {
                    string refused = Dds120FirmwarePatch.Apply(fw, (ushort)dev.VendorId, (ushort)dev.ProductId, bcdDevice);
                    if (refused != null)
                    {
                        log?.Invoke("Cannot keep the stock USB identity (" + refused + "); using 04B5:0120");
                        keepStockIdentity = false;
                    }
                }
                log?.Invoke("Uploading firmware (" + fw.ByteCount + " bytes)...");
                try
                {
                    AndroidFx2Loader.Upload(conn, fw, log);
                }
                finally
                {
                    conn.Close();
                }
                lock (uploaded)
                    uploaded.Add(dev.DeviceName);
                return WaitForFirmware(dev.DeviceName, log, cancel);
            }
            if (denied && !waiting)
                throw new DeviceNotReadyException("USB access to the DDS-120 was denied. Tap the screen to ask again, " +
                    "or unplug and replug the scope.");
            throw new DeviceNotReadyException("DDS-120 found. Allow USB access in the dialog to continue.");
        }

        static AndroidDds120Source WaitForFirmware(string oldName, Action<string> log, CancellationToken cancel)
        {
            var sw = Stopwatch.StartNew();
            bool gone = false;
            Thread.Sleep(300);
            while (sw.ElapsedMilliseconds < 12000)
            {
                cancel.ThrowIfCancellationRequested();
                List<UsbDevice> found = AndroidUsb.FindCandidates();
                if (!gone && found.TrueForAll(d => d.DeviceName != oldName))
                {
                    gone = true;
                    lock (uploaded)
                        uploaded.Remove(oldName);
                    log?.Invoke("Scope left the bus after " + sw.ElapsedMilliseconds + " ms, waiting for it to return");
                }
                if (gone)
                {
                    foreach (UsbDevice dev in found)
                    {
                        if (!AndroidUsb.HasPermission(dev))
                        {
                            // A new device needs a new grant. Do not block the engine while the user decides.
                            AndroidUsb.RequestPermission(dev);
                            log?.Invoke("Scope is back as " + AndroidUsb.Describe(dev) + " after " + sw.ElapsedMilliseconds + " ms");
                            throw new DeviceNotReadyException("Firmware loaded. Android asks once more for the restarted scope: " +
                                "allow USB access to continue.");
                        }
                        UsbDeviceConnection conn = AndroidUsb.Manager.OpenDevice(dev);
                        if (conn == null)
                            continue;
                        string description;
                        ushort bcd;
                        bool ready;
                        try
                        {
                            ready = RunsScopeFirmware(dev, conn, out description, out bcd);
                        }
                        catch (Exception)
                        {
                            conn.Close();
                            continue;
                        }
                        if (ready)
                        {
                            log?.Invoke("Scope firmware running after " + sw.ElapsedMilliseconds + " ms: " + description);
                            return AndroidDds120Source.Open(dev, conn, description, log);
                        }
                        conn.Close();
                    }
                }
                Thread.Sleep(200);
            }
            if (!gone)
                throw new DeviceNotReadyException("The scope did not restart after the firmware upload. Unplug and replug it, then try again.");
            throw new DeviceNotReadyException("The scope did not come back after the firmware upload. Unplug and replug it, then try again.");
        }

        // The scope firmware identifies as manufacturer "sigrok"; the stock EEPROM code does not.
        static bool RunsScopeFirmware(UsbDevice dev, UsbDeviceConnection conn, out string description, out ushort bcdDevice)
        {
            byte[] raw = conn.GetRawDescriptors();
            bcdDevice = raw != null && raw.Length >= 18 ? (ushort)(raw[12] | raw[13] << 8) : (ushort)0;
            string manufacturer = (raw != null && raw.Length >= 18 ? GetString(conn, raw[14]) : null) ?? dev.ManufacturerName ?? "";
            string product = (raw != null && raw.Length >= 18 ? GetString(conn, raw[15]) : null) ?? dev.ProductName ?? "";
            description = string.Format("{0:X4}:{1:X4} rev {2:X4} \"{3}\" \"{4}\"",
                dev.VendorId, dev.ProductId, bcdDevice, manufacturer, product);
            return dev.VendorId == FwVid && dev.ProductId == FwPid || manufacturer == "sigrok";
        }

        // GET_DESCRIPTOR(STRING) in US English; null if the index is 0 or the request fails.
        static string GetString(UsbDeviceConnection conn, byte index)
        {
            if (index == 0)
                return null;
            var buf = new byte[255];
            int n = conn.ControlTransfer((UsbAddressing)0x80, 6, 0x0300 | index, 0x0409, buf, buf.Length, 500);
            if (n < 2 || buf[1] != 3)
                return null;
            int len = Math.Min(n, buf[0]);
            return Encoding.Unicode.GetString(buf, 2, Math.Max(0, len - 2) & ~1);
        }
    }
}
