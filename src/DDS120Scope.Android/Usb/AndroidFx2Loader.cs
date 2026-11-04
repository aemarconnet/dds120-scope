using System;
using System.Threading;
using Android.Hardware.Usb;
using Dds120.Usb;

namespace Dds120.Droid.Usb
{
    // Android counterpart of Fx2Firmware.Upload (which takes a WinUsbDevice): loads the image into the
    // FX2's internal RAM through the 0xA0 "firmware load" request that the FX2 silicon handles itself.
    internal static class AndroidFx2Loader
    {
        const int VendorOut = 0x40;        // bmRequestType: host-to-device, vendor, device
        const int FirmwareLoad = 0xA0;
        const int CPUCS = 0xE600;          // bit 0 holds the 8051 in reset
        const int ChunkSize = 512;
        const int TimeoutMs = 1000;
        const int EIO = 5;

        // Halts the 8051, writes the image and releases reset. The device then drops off the bus and
        // re-enumerates with the descriptors of the new firmware, so the connection is dead after this.
        public static void Upload(UsbDeviceConnection conn, Fx2Firmware fw, Action<string> log)
        {
            Write(conn, CPUCS, new byte[] { 1 }, 1);
            int total = 0;
            var chunk = new byte[ChunkSize];
            foreach (HexSegment seg in fw.Segments())
            {
                for (int off = 0; off < seg.Data.Length; off += ChunkSize)
                {
                    int n = Math.Min(ChunkSize, seg.Data.Length - off);
                    Array.Copy(seg.Data, off, chunk, 0, n);
                    Write(conn, seg.Address + off, chunk, n);
                    total += n;
                }
            }
            log?.Invoke("Wrote " + total + " bytes of firmware, starting 8051");
            Thread.Sleep(20);
            // May fail on some hosts: the new firmware disconnects before the status stage completes.
            conn.ControlTransfer((UsbAddressing)VendorOut, FirmwareLoad, CPUCS, 0, new byte[] { 0 }, 1, TimeoutMs);
        }

        static void Write(UsbDeviceConnection conn, int address, byte[] data, int length)
        {
            int r = conn.ControlTransfer((UsbAddressing)VendorOut, FirmwareLoad, address, 0, data, length, TimeoutMs);
            if (r != length)
                throw new UsbException("Firmware write at 0x" + address.ToString("X4"), EIO);
        }
    }
}
