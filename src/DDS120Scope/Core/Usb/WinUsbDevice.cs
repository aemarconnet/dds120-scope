using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using static Dds120.Usb.WinUsbNative;

namespace Dds120.Usb
{
    public sealed class UsbException : Exception
    {
        public int Win32Error { get; }

        public UsbException(string operation, int win32Error)
            : base(operation + " failed: " + new Win32Exception(win32Error).Message + " (0x" + win32Error.ToString("X") + ")")
        {
            Win32Error = win32Error;
        }

        public static UsbException Last(string operation)
        {
            return new UsbException(operation, Marshal.GetLastWin32Error());
        }

        public bool IsTimeout { get { return Win32Error == 121 /* ERROR_SEM_TIMEOUT */; } }

        public bool IsAborted { get { return Win32Error == 995 /* ERROR_OPERATION_ABORTED */; } }

        // The device was unplugged or re-enumerated underneath us.
        public bool IsGone
        {
            get
            {
                return Win32Error == 2 /* FILE_NOT_FOUND */ || Win32Error == 22 /* BAD_COMMAND */ ||
                       Win32Error == 31 /* GEN_FAILURE */ || Win32Error == 1167 /* DEVICE_NOT_CONNECTED */;
            }
        }
    }

    public sealed class UsbDeviceEntry
    {
        public string Path { get; set; }
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }

        public override string ToString()
        {
            return VendorId.ToString("X4") + ":" + ProductId.ToString("X4") + "  " + Path;
        }
    }

    public sealed class UsbPipe
    {
        public byte Id { get; set; }
        public int Type { get; set; }          // 0 control, 1 iso, 2 bulk, 3 interrupt
        public int MaxPacketSize { get; set; }

        public override string ToString()
        {
            string[] names = { "control", "isochronous", "bulk", "interrupt" };
            return "0x" + Id.ToString("X2") + " " + names[Type & 3] + " max " + MaxPacketSize;
        }
    }

    // A USB device opened through the Microsoft WinUSB driver. The device is found through the
    // generic USB device interface, which the hub driver registers for every USB device, so this
    // works whether or not the device's INF/MS OS descriptors registered a custom interface GUID.
    public sealed class WinUsbDevice : IDisposable
    {
        static readonly Regex VidPid = new Regex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);

        SafeFileHandle file;
        IntPtr winusb;

        public string Path { get; }

        WinUsbDevice(string path, SafeFileHandle file, IntPtr winusb)
        {
            Path = path;
            this.file = file;
            this.winusb = winusb;
        }

        public static List<UsbDeviceEntry> Enumerate()
        {
            var result = new List<UsbDeviceEntry>();
            Guid guid = GUID_DEVINTERFACE_USB_DEVICE;
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1))
                throw UsbException.Last("SetupDiGetClassDevs");
            try
            {
                for (uint i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                        break;
                    uint size;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
                    if (size == 0)
                        continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is the fixed part only: 8 on x64, 6 on x86.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out size, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(detail + 4);
                        Match m = VidPid.Match(path ?? "");
                        if (!m.Success)
                            continue;
                        result.Add(new UsbDeviceEntry
                        {
                            Path = path,
                            VendorId = ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber),
                            ProductId = ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber),
                        });
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        // Present USB device nodes whose instance ID starts with the given prefix (e.g.
        // "USB\VID_8102&PID_8102"), with their Device Manager problem code (0 = working). Unlike
        // Enumerate() this also sees devices that have no working driver and so expose no interface.
        public static List<KeyValuePair<string, int>> FindDeviceNodes(string instancePrefix)
        {
            var result = new List<KeyValuePair<string, int>>();
            IntPtr set = SetupDiGetClassDevsAll(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1))
                return result;
            try
            {
                var id = new StringBuilder(512);
                for (uint i = 0; ; i++)
                {
                    var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                    if (!SetupDiEnumDeviceInfo(set, i, ref info))
                        break;
                    int needed;
                    if (!SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out needed))
                        continue;
                    string instance = id.ToString();
                    if (!instance.StartsWith(instancePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    uint status, problem;
                    int cr = CM_Get_DevNode_Status(out status, out problem, info.DevInst, 0);
                    result.Add(new KeyValuePair<string, int>(instance, cr == 0 ? (int)problem : -1));
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        public static WinUsbDevice Open(string path)
        {
            SafeFileHandle file = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (file.IsInvalid)
                throw UsbException.Last("Opening the USB device");
            IntPtr handle;
            if (!WinUsb_Initialize(file, out handle))
            {
                var ex = UsbException.Last("WinUsb_Initialize (is the WinUSB driver bound to this device?)");
                file.Dispose();
                throw ex;
            }
            return new WinUsbDevice(path, file, handle);
        }

        void CheckOpen()
        {
            if (winusb == IntPtr.Zero)
                throw new ObjectDisposedException("WinUsbDevice");
        }

        public byte[] GetDescriptor(byte type, byte index, ushort languageId, int maxLength)
        {
            CheckOpen();
            var buffer = new byte[maxLength];
            uint got;
            if (!WinUsb_GetDescriptor(winusb, type, index, languageId, buffer, (uint)buffer.Length, out got))
                throw UsbException.Last("GetDescriptor");
            Array.Resize(ref buffer, (int)got);
            return buffer;
        }

        public UsbDeviceDescriptor GetDeviceDescriptor()
        {
            return new UsbDeviceDescriptor(GetDescriptor(USB_DEVICE_DESCRIPTOR_TYPE, 0, 0, 18));
        }

        public byte[] GetConfigurationDescriptor()
        {
            byte[] head = GetDescriptor(USB_CONFIGURATION_DESCRIPTOR_TYPE, 0, 0, 9);
            int total = head.Length >= 4 ? head[2] | head[3] << 8 : 9;
            return GetDescriptor(USB_CONFIGURATION_DESCRIPTOR_TYPE, 0, 0, Math.Max(total, 9));
        }

        public string GetString(byte index)
        {
            if (index == 0)
                return null;
            try
            {
                byte[] d = GetDescriptor(USB_STRING_DESCRIPTOR_TYPE, index, 0x0409, 255);
                if (d.Length < 2 || d[1] != USB_STRING_DESCRIPTOR_TYPE)
                    return null;
                int len = Math.Min(d[0], d.Length);
                return Encoding.Unicode.GetString(d, 2, Math.Max(0, len - 2));
            }
            catch (UsbException)
            {
                return null;
            }
        }

        public List<UsbPipe> QueryPipes(byte alternateSetting = 0)
        {
            CheckOpen();
            USB_INTERFACE_DESCRIPTOR desc;
            if (!WinUsb_QueryInterfaceSettings(winusb, alternateSetting, out desc))
                throw UsbException.Last("QueryInterfaceSettings");
            var pipes = new List<UsbPipe>();
            for (byte i = 0; i < desc.bNumEndpoints; i++)
            {
                WINUSB_PIPE_INFORMATION info;
                if (WinUsb_QueryPipe(winusb, alternateSetting, i, out info))
                    pipes.Add(new UsbPipe { Id = info.PipeId, Type = info.PipeType, MaxPacketSize = info.MaximumPacketSize });
            }
            return pipes;
        }

        public void ControlOut(byte request, ushort value, ushort index, byte[] data)
        {
            CheckOpen();
            data = data ?? new byte[0];
            var setup = new WINUSB_SETUP_PACKET
            {
                RequestType = 0x40, // host-to-device, vendor, device
                Request = request,
                Value = value,
                Index = index,
                Length = (ushort)data.Length,
            };
            uint done;
            if (!WinUsb_ControlTransfer(winusb, setup, data, (uint)data.Length, out done, IntPtr.Zero))
                throw UsbException.Last("Control request 0x" + request.ToString("X2"));
        }

        public byte[] ControlIn(byte request, ushort value, ushort index, int length)
        {
            CheckOpen();
            var data = new byte[length];
            var setup = new WINUSB_SETUP_PACKET
            {
                RequestType = 0xC0, // device-to-host, vendor, device
                Request = request,
                Value = value,
                Index = index,
                Length = (ushort)length,
            };
            uint done;
            if (!WinUsb_ControlTransfer(winusb, setup, data, (uint)length, out done, IntPtr.Zero))
                throw UsbException.Last("Control request 0x" + request.ToString("X2"));
            Array.Resize(ref data, (int)done);
            return data;
        }

        // Synchronous bulk/interrupt read into buffer[offset..offset+count). Returns bytes received.
        public int ReadPipe(byte pipeId, byte[] buffer, int offset, int count)
        {
            CheckOpen();
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException("count");
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                uint got;
                if (!WinUsb_ReadPipe(winusb, pipeId, pin.AddrOfPinnedObject() + offset, (uint)count, out got, IntPtr.Zero))
                    throw UsbException.Last("Bulk read");
                return (int)got;
            }
            finally
            {
                pin.Free();
            }
        }

        public void SetPipeTimeout(byte pipeId, uint milliseconds)
        {
            CheckOpen();
            uint v = milliseconds;
            if (!WinUsb_SetPipePolicy(winusb, pipeId, PIPE_TRANSFER_TIMEOUT, 4, ref v))
                throw UsbException.Last("SetPipePolicy(timeout)");
        }

        public void SetPipePolicy(byte pipeId, uint policy, bool enabled)
        {
            CheckOpen();
            byte v = (byte)(enabled ? 1 : 0);
            if (!WinUsb_SetPipePolicyBool(winusb, pipeId, policy, 1, ref v))
                throw UsbException.Last("SetPipePolicy(" + policy + ")");
        }

        public uint GetMaxTransferSize(byte pipeId)
        {
            CheckOpen();
            uint len = 4, v;
            return WinUsb_GetPipePolicy(winusb, pipeId, MAXIMUM_TRANSFER_SIZE, ref len, out v) ? v : 0;
        }

        // Safe to call from another thread to unblock a pending ReadPipe.
        public void AbortPipe(byte pipeId)
        {
            if (winusb != IntPtr.Zero)
                WinUsb_AbortPipe(winusb, pipeId);
        }

        public void ResetPipe(byte pipeId)
        {
            if (winusb != IntPtr.Zero)
                WinUsb_ResetPipe(winusb, pipeId);
        }

        public void FlushPipe(byte pipeId)
        {
            if (winusb != IntPtr.Zero)
                WinUsb_FlushPipe(winusb, pipeId);
        }

        public void Dispose()
        {
            if (winusb != IntPtr.Zero)
            {
                WinUsb_Free(winusb);
                winusb = IntPtr.Zero;
            }
            if (file != null)
            {
                file.Dispose();
                file = null;
            }
        }
    }

    public sealed class UsbDeviceDescriptor
    {
        public byte[] Raw { get; }
        public ushort BcdUsb { get { return U16(2); } }
        public byte DeviceClass { get { return Raw[4]; } }
        public byte MaxPacketSize0 { get { return Raw[7]; } }
        public ushort VendorId { get { return U16(8); } }
        public ushort ProductId { get { return U16(10); } }
        public ushort BcdDevice { get { return U16(12); } }
        public byte ManufacturerIndex { get { return Raw[14]; } }
        public byte ProductIndex { get { return Raw[15]; } }
        public byte SerialIndex { get { return Raw[16]; } }

        public UsbDeviceDescriptor(byte[] raw)
        {
            if (raw == null || raw.Length < 18)
                throw new ArgumentException("Short device descriptor");
            Raw = raw;
        }

        ushort U16(int i)
        {
            return (ushort)(Raw[i] | Raw[i + 1] << 8);
        }

        public override string ToString()
        {
            return string.Format("USB {0:X}.{1:X2} class 0x{2:X2} VID {3:X4} PID {4:X4} rev {5:X4} strings m{6}/p{7}/s{8}",
                BcdUsb >> 8, BcdUsb & 0xFF, DeviceClass, VendorId, ProductId, BcdDevice,
                ManufacturerIndex, ProductIndex, SerialIndex);
        }
    }
}
