using Dds120.Usb;

namespace Dds120.Scope
{
    // Makes the open DDS120 firmware look like the scope's stock EEPROM firmware to Windows, so the
    // WinUSB binding Windows already gives the stock scope keeps working after the upload.
    //
    // The stock firmware answers Microsoft OS 1.0 descriptor requests with vendor code 0x01 and a
    // "WINUSB" compatible ID; Windows caches that vendor code per VID/PID/revision. The open firmware
    // (fx2lib based) dispatches every bRequest < 12 as a standard request, so vendor request 0x01 lands
    // in CLEAR_FEATURE and the compatible ID is lost, which makes Windows drop the driver (code 28).
    //
    // The patch hooks the start of fx2lib's handle_setupdata() and serves the extended compat ID
    // descriptor through the FX2 setup-data pointer. Addresses are specific to dds120-firmware.hex from
    // OpenHantek6022; every original byte sequence is verified first and the patch is refused otherwise.
    public static class Dds120FirmwarePatch
    {
        const int HandleSetupData = 0x08EF;  // MOV DPTR,#SETUPDAT+1 ; MOVX A,@DPTR ; MOV R7,A ; ADD A,#0xF4
        const int StallAndAck = 0x0A20;      // EP0CS |= bmEPSTALL, then falls into AckAndReturn
        const int AckAndReturn = 0x0A2F;     // EP0CS |= bmHSNAK ; RET
        const int Hook = 0x3A00;             // unused RAM: code ends at 0x0E4C, firmware uses no xdata
        const int CompatIdDescriptor = 0x3B00; // must be word aligned for SUDPTR

        static readonly byte[] SetupDataEntry = { 0x90, 0xE6, 0xB9, 0xE0, 0xFF, 0x24, 0xF4 };
        static readonly byte[] StallAndAckCode = { 0x90, 0xE6, 0xA0, 0xE0, 0xFF, 0x7E, 0x00, 0x43, 0x07, 0x01 };
        static readonly byte[] AckAndReturnCode = { 0x90, 0xE6, 0xA0, 0xE0, 0xFF, 0x43, 0x07, 0x80, 0x90, 0xE6, 0xA0, 0xEF, 0xF0, 0x22 };

        static readonly byte[] HookCode =
        {
            0x90, 0xE6, 0xB9,       // 3A00 MOV  DPTR,#SETUPDAT+1   bRequest
            0xE0,                   // 3A03 MOVX A,@DPTR
            0xB4, 0x01, 0x24,       // 3A04 CJNE A,#0x01,orig
            0x90, 0xE6, 0xB8,       // 3A07 MOV  DPTR,#SETUPDAT+0   bmRequestType
            0xE0,                   // 3A0A MOVX A,@DPTR
            0x54, 0x60,             // 3A0B ANL  A,#0x60
            0xB4, 0x40, 0x1B,       // 3A0D CJNE A,#0x40,orig       not vendor: standard CLEAR_FEATURE
            0xE0,                   // 3A10 MOVX A,@DPTR
            0xB4, 0xC0, 0x14,       // 3A11 CJNE A,#0xC0,stall      device-to-host, device recipient only
            0x90, 0xE6, 0xBC,       // 3A14 MOV  DPTR,#SETUPDAT+4   wIndex
            0xE0,                   // 3A17 MOVX A,@DPTR
            0xB4, 0x04, 0x0D,       // 3A18 CJNE A,#0x04,stall      4 = extended compat ID
            0x90, 0xE6, 0xB3,       // 3A1B MOV  DPTR,#SUDPTRH
            0x74, CompatIdDescriptor >> 8,   // 3A1E MOV A,#hi(desc)
            0xF0,                   // 3A20 MOVX @DPTR,A
            0xA3,                   // 3A21 INC  DPTR               SUDPTRL; writing it starts the IN transfer
            0x74, CompatIdDescriptor & 0xFF, // 3A22 MOV A,#lo(desc)
            0xF0,                   // 3A24 MOVX @DPTR,A
            0x02, AckAndReturn >> 8, AckAndReturn & 0xFF,  // 3A25 LJMP ack+ret
            0x02, StallAndAck >> 8, StallAndAck & 0xFF,    // 3A28 stall: LJMP stall+ack+ret
            0x90, 0xE6, 0xB9,       // 3A2B orig: MOV DPTR,#SETUPDAT+1 (the instruction the hook replaced)
            0x02, (HandleSetupData + 3) >> 8, (HandleSetupData + 3) & 0xFF, // 3A2E LJMP back
        };

        // Microsoft OS 1.0 extended compat ID descriptor: one function, interface 0, "WINUSB".
        static readonly byte[] CompatId =
        {
            0x28, 0x00, 0x00, 0x00, // dwLength = 40
            0x00, 0x01,             // bcdVersion 1.00
            0x04, 0x00,             // wIndex = 4
            0x01,                   // bCount
            0, 0, 0, 0, 0, 0, 0,    // reserved
            0x00,                   // bFirstInterfaceNumber
            0x01,                   // reserved, must be 1
            (byte)'W', (byte)'I', (byte)'N', (byte)'U', (byte)'S', (byte)'B', 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, // subCompatibleID
            0, 0, 0, 0, 0, 0,       // reserved
        };

        public static string Apply(Fx2Firmware fw, ushort stockVid, ushort stockPid, ushort stockBcdDevice)
        {
            int dd = fw.FindDeviceDescriptor(Dds120Device.FwVid, Dds120Device.FwPid);
            if (dd < 0)
                return "device descriptor not found";
            if (!fw.Matches(HandleSetupData, SetupDataEntry) || !fw.Matches(StallAndAck, StallAndAckCode) ||
                !fw.Matches(AckAndReturn, AckAndReturnCode))
                return "firmware code does not match the expected build";
            if (!fw.IsFree(Hook, HookCode.Length) || !fw.IsFree(CompatIdDescriptor, CompatId.Length))
                return "patch area is not free";

            fw.Write(Hook, HookCode);
            fw.Write(CompatIdDescriptor, CompatId);
            fw.Write(HandleSetupData, new byte[] { 0x02, Hook >> 8, Hook & 0xFF }); // LJMP hook
            // Same IDs as the stock EEPROM firmware: class 0 (per interface), no serial number, so the
            // device keeps its device instance and hardware/compatible IDs.
            fw.PatchDeviceDescriptor(dd, 0x00, stockVid, stockPid, stockBcdDevice, 0);
            return null;
        }
    }
}
