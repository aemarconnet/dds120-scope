using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Dds120.Usb
{
    public sealed class HexSegment
    {
        public int Address { get; set; }
        public byte[] Data { get; set; }
    }

    // Firmware image for the Cypress FX2 (CY7C68013A) loaded into its internal 16 KB RAM.
    public sealed class Fx2Firmware
    {
        public const int RamSize = 0x4000;

        readonly byte[] image = new byte[RamSize];
        readonly bool[] used = new bool[RamSize];

        public static Fx2Firmware ParseIntelHex(TextReader reader)
        {
            var fw = new Fx2Firmware();
            string line;
            int lineNo = 0;
            int upper = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                if (line[0] != ':' || line.Length < 11 || (line.Length - 1) % 2 != 0)
                    throw new FormatException("Intel HEX line " + lineNo + " is malformed");
                var rec = new byte[(line.Length - 1) / 2];
                for (int i = 0; i < rec.Length; i++)
                    rec[i] = byte.Parse(line.Substring(1 + 2 * i, 2), NumberStyles.HexNumber);
                int count = rec[0];
                if (rec.Length != count + 5)
                    throw new FormatException("Intel HEX line " + lineNo + " has a wrong length");
                byte sum = 0;
                foreach (byte b in rec)
                    sum += b;
                if (sum != 0)
                    throw new FormatException("Intel HEX line " + lineNo + " has a bad checksum");
                int address = upper + (rec[1] << 8 | rec[2]);
                switch (rec[3])
                {
                    case 0x00:
                        if (address + count > RamSize)
                            throw new FormatException("Intel HEX line " + lineNo + " is outside FX2 internal RAM");
                        for (int i = 0; i < count; i++)
                        {
                            fw.image[address + i] = rec[4 + i];
                            fw.used[address + i] = true;
                        }
                        break;
                    case 0x01:
                        return fw;
                    case 0x02:
                        upper = (rec[4] << 8 | rec[5]) << 4;
                        break;
                    case 0x04:
                        upper = (rec[4] << 8 | rec[5]) << 16;
                        break;
                    default:
                        break; // start-address records are irrelevant for the FX2
                }
            }
            return fw;
        }

        public int ByteCount
        {
            get
            {
                int n = 0;
                foreach (bool u in used)
                    if (u) n++;
                return n;
            }
        }

        public List<HexSegment> Segments()
        {
            var list = new List<HexSegment>();
            int i = 0;
            while (i < RamSize)
            {
                if (!used[i]) { i++; continue; }
                int start = i;
                while (i < RamSize && used[i]) i++;
                var data = new byte[i - start];
                Array.Copy(image, start, data, 0, data.Length);
                list.Add(new HexSegment { Address = start, Data = data });
            }
            return list;
        }

        // Finds an 18-byte USB device descriptor with the given VID/PID in the image.
        public int FindDeviceDescriptor(ushort vid, ushort pid)
        {
            for (int i = 0; i + 18 <= RamSize; i++)
            {
                if (used[i] && image[i] == 18 && image[i + 1] == 1 &&
                    image[i + 8] == (byte)vid && image[i + 9] == (byte)(vid >> 8) &&
                    image[i + 10] == (byte)pid && image[i + 11] == (byte)(pid >> 8))
                    return i;
            }
            return -1;
        }

        // Rewrites identity fields of the device descriptor at the given address.
        public void PatchDeviceDescriptor(int at, byte deviceClass, ushort vid, ushort pid, ushort bcdDevice, byte serialIndex)
        {
            image[at + 4] = deviceClass;
            image[at + 5] = 0;
            image[at + 6] = 0;
            image[at + 8] = (byte)vid;
            image[at + 9] = (byte)(vid >> 8);
            image[at + 10] = (byte)pid;
            image[at + 11] = (byte)(pid >> 8);
            image[at + 12] = (byte)bcdDevice;
            image[at + 13] = (byte)(bcdDevice >> 8);
            image[at + 16] = serialIndex;
        }

        public bool Matches(int address, byte[] expected)
        {
            if (address < 0 || address + expected.Length > RamSize)
                return false;
            for (int i = 0; i < expected.Length; i++)
                if (!used[address + i] || image[address + i] != expected[i])
                    return false;
            return true;
        }

        public bool IsFree(int address, int length)
        {
            if (address < 0 || address + length > RamSize)
                return false;
            for (int i = 0; i < length; i++)
                if (used[address + i])
                    return false;
            return true;
        }

        public void Write(int address, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                image[address + i] = data[i];
                used[address + i] = true;
            }
        }

        const byte FirmwareLoad = 0xA0;   // handled by the FX2 silicon itself, whatever firmware runs
        const ushort CPUCS = 0xE600;      // bit 0 holds the 8051 in reset

        // Halts the 8051, writes the image to RAM and releases reset. The device then drops off the
        // bus and re-enumerates with the descriptors of the new firmware, so the handle is dead after this.
        public void Upload(WinUsbDevice device, Action<string> log)
        {
            device.ControlOut(FirmwareLoad, CPUCS, 0, new byte[] { 1 });
            int total = 0;
            foreach (HexSegment seg in Segments())
            {
                for (int off = 0; off < seg.Data.Length; off += 512)
                {
                    int n = Math.Min(512, seg.Data.Length - off);
                    var chunk = new byte[n];
                    Array.Copy(seg.Data, off, chunk, 0, n);
                    device.ControlOut(FirmwareLoad, (ushort)(seg.Address + off), 0, chunk);
                    total += n;
                }
            }
            log?.Invoke("Wrote " + total + " bytes of firmware, starting 8051");
            Thread.Sleep(20);
            try
            {
                device.ControlOut(FirmwareLoad, CPUCS, 0, new byte[] { 0 });
            }
            catch (UsbException)
            {
                // Expected on some hosts: the new firmware disconnects before the status stage completes.
            }
        }
    }
}
