# DDS-120 Scope

Portable Windows oscilloscope software for the SainSmart DDS-120 (Rocktech/BUUDAI BM102),
a two-channel 8-bit USB scope built around a Cypress FX2 (CY7C68013A).

- Single `DDS120Scope.exe`, .NET Framework 4.8 (part of Windows 10/11): no installer, no runtime,
  no driver, no admin rights. Settings live in `DDS120Scope.ini` next to the exe.
- Talks to the in-box `winusb.dll` directly (no libusb).
- Loads the open-source DDS120 firmware into the scope's RAM on connect; the EEPROM is untouched.
- Bench-scope features within what the hardware allows: autoset, Sample / Peak detect / Hi-Res /
  Average acquisition, trigger coupling (AC, noise reject, HF/LF reject) and force trigger, 22
  automatic measurements, time and amplitude cursors, math (CH1±CH2, CH1×CH2) and FFT, XY mode,
  persistence. `tools\Dds120Probe selftest [--hw]` exercises all of them through the engine.

User documentation: [packaging/README.txt](packaging/README.txt).

## Build

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Needs the .NET SDK (any recent version) and the .NET Framework 4.8 targeting pack (installed with
Visual Studio). The output is `dist\DDS120Scope\` and `dist\DDS120Scope-portable.zip`.

## Layout

| Path | What |
|---|---|
| `src/DDS120Scope/Core/Usb` | WinUSB P/Invoke, device enumeration, Intel HEX loader and FX2 RAM upload |
| `src/DDS120Scope/Core/Scope` | DDS120 protocol, model tables, firmware patch, acquisition engine (acquire modes, trigger coupling, averaging, autoset), measurements, math and FFT, demo source |
| `src/DDS120Scope/Core/Display` | The scope screen (traces, math, FFT, XY, persistence, cursors, readouts, marker and cursor dragging), drawn through `IScopeCanvas`; shared by the Windows and Android apps |
| `src/DDS120Scope/UI` | WinForms main window, the screen control and its GDI+ `IScopeCanvas` |
| `src/DDS120Scope.Android` | Android app (USB OTG), see its README |
| `tools/Dds120Probe` | Command-line bring-up and diagnostics tool (shares the Core sources) |
| `tools/dds_diag.py` | Python (ctypes + WinUSB) timing, gain and coupling tests; needs no compiled exe |
| `firmware/dds120-firmware.hex` | Scope firmware from OpenHantek6022 (GPL-2.0-or-later), embedded into the exe |
| `packaging/` | Files shipped in the portable folder |

## How it talks to the scope

| Step | USB |
|---|---|
| Stock scope (EEPROM firmware "BUUDAI USB102") | `8102:8102`, WinUSB bound automatically via its MS OS descriptor (vendor code `0x01`) |
| Firmware upload | vendor OUT `0xA0` to `0xE600` = 1 (halt 8051), RAM writes, `0xE600` = 0 (run) |
| CH1 / CH2 gain | vendor OUT `0xE0` / `0xE1`, 1 byte: 1, 2, 5, 10, 20 |
| Sample rate | vendor OUT `0xE2`, 1 byte: N = N MS/s (1, 2, 5, 8, 15, 24, 30, 48), 11 = 10 MS/s, 10/20/50 = 100/200/500 kS/s |
| Start / stop | vendor OUT `0xE3`, 1 = start, 0 = stop |
| Channel count | vendor OUT `0xE4`, 1 or 2 |
| Coupling | vendor OUT `0xE5`, bit 0 = CH1 DC, bit 4 = CH2 DC |
| Calibration output | vendor OUT `0xE6`, 0 = 100 Hz, 1..100 = kHz, 105/106/120/150 = 50/60/200/500 Hz |
| Samples | bulk IN `0x86`, unsigned bytes (0x80 = 0 V), CH1/CH2 interleaved |

Each capture sends start and reads one block; the first 2560 samples per channel are discarded
(stale FIFO contents). Slower rates average raw samples (e.g. 100 kS/s = 8 MS/s / 80).
Counts per volt per gain (x1: 21, x2: 43, x5: 96, x10: 200, x20: 400) are from OpenHantek6022.

Measured on hardware (2026-11-03, via the scope's 1 kHz/10 kHz calibration output):

- The stale part of each read is exactly one FX2 FIFO, 2048 bytes (1024 samples per channel with
  both channels on): after a gain change the old amplitude ends at that byte. The 2560-sample drop
  covers it in both modes.
- The limit is USB data rate. Everything up to 30 MB/s is exact: 1 channel up to 30 MS/s and
  2 channels up to 15 MS/s deliver 99.5-99.9% of nominal (`tools/dds_diag.py throughput`), and with
  2 channels the cal signal period is exact with no phase jumps. Every faster code (1 ch 48, 2 ch
  24/30/48) saturates at ~44.8 MB/s on the test PC: the firmware pauses sampling when its FIFO is
  full, so the real rate is lower and uneven (2 ch codes 24/30/48 all measure ~22.5 MS/s per
  channel). `ScopeSettings.MaxByteRate` caps captures at 30 MB/s.
- Byte 0 of each pair is CH1 (gain request `0xE0`); 1-channel mode streams CH1 only.
- AC coupling works (the cal square wave centres on 0 V). Measured with the cal signal, in counts
  peak-to-peak: x1 31, x2 64-65, x5 150; x10/x20 clip. On the model's counts-per-volt scale x1 and
  x2 agree (1.48 / 1.49 V) and x5 reads ~5% high (1.56 V). The cal output is therefore ~1.5 V.
- CH1 of the test unit sits +7 counts above 0x80 at every gain (an ADC offset; Zero calibration
  removes it). Settling after a gain change is not visible.
- Not yet measured: gains x10/x20 (need a smaller signal).

### Why the firmware is patched

The open firmware re-enumerates with its own descriptors. If Windows sees different hardware or
compatible IDs it re-runs driver matching and, finding nothing for the new IDs, leaves the scope
with error code 28. The stock firmware gets WinUSB through an MS OS 1.0 descriptor, and Windows
caches its vendor code (`0x01`) per VID/PID/revision. The patch in
[Dds120FirmwarePatch.cs](src/DDS120Scope/Core/Scope/Dds120FirmwarePatch.cs):

1. makes the device descriptor identical in identity to the stock one (`8102:8102`, rev `0000`,
   class 0, no serial), so the device keeps its devnode;
2. hooks fx2lib's `handle_setupdata()` (0x08EF) with an `LJMP` to 49 bytes of 8051 code at 0x3A00
   that answers vendor IN request `0x01`/wIndex 4 with a 40-byte "WINUSB" extended compat ID
   descriptor at 0x3B00 via the FX2 setup-data pointer, and otherwise falls through to the original
   code (fx2lib would treat bRequest 1 as CLEAR_FEATURE).

The patch verifies the original bytes first and is refused for any other firmware build.
