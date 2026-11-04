# DDS-120 Scope for Android

Android version of the DDS-120 oscilloscope app: same scope screen and acquisition engine as the
Windows app, driven by touch, with the scope plugged into the phone or tablet through USB OTG.

> **Status:** tested on a Pixel 10 Pro Fold (Android 17) with a real DDS-120 over USB-C OTG on
> 2026-11-03: firmware upload, re-enumeration, capture, trigger and measurements work (the 1 kHz
> calibration output reads 1.000 kHz, 50 % duty). Other phones are untested; use
> **Device log → Copy** to report what happens.

## Install (sideload)

1. Copy `com.dds120scope.app-Signed.apk` to the phone and open it in a file manager. Android asks
   once to allow installing apps from that file manager.
   Or, with USB debugging on: `adb install -r com.dds120scope.app-Signed.apk`
2. Open **DDS-120 Scope**. With no scope connected, turn on **Demo signal** to try the app.

The APK is signed with the build PC's Android debug key. That is fine for sideloading. An APK built
on another PC has a different key, so Android only installs it after the old app is uninstalled.

## What you need

- Android 8.0 (API 26) or newer, ARM (64- or 32-bit) or x86-64.
- A phone or tablet with **USB host (OTG)** support. Most current phones have it; some need it
  enabled in Settings ("OTG connection"). Without it the app still runs in demo mode.
- An OTG adapter: USB-C (or micro-USB) male to USB-A female, then the scope's normal USB cable.
- Power: the scope runs from the phone's 5 V. If it keeps disconnecting, the phone limits OTG
  current: use a powered USB hub or a USB-C hub with power delivery.

## First connection

1. Plug in the scope. Android asks *"Open DDS-120 Scope to handle USB102?"* (or similar): tap **OK**.
   Tick **Always open** to skip this next time.
2. The app loads the scope firmware into the scope's RAM (the EEPROM is never changed). The scope
   restarts and reappears as a new USB device (`04B5:0120`).
3. Android asks a second time, now for the restarted scope: tap **OK** (tick **Always** again).
4. Capture starts. After unplugging, the scope is back to its stock firmware; the next plug-in
   repeats these steps (without dialogs if both were set to **Always**).

If you tapped **Cancel**, tap the scope screen to be asked again, or replug the scope.

## Using it

- **RUN/STOP**, **SINGLE**, **AUTOSET**, **FORCE** (trigger) stay pinned at the top of the side
  panel. Tap a value to pick from a list.
- Vertical, CH1 on the left and CH2 on the right: **CH1/CH2** on/off, **‹ V/div ›**, **DC/AC**,
  **x1/x10** probe, **INV**.
- Horizontal: **‹ time/div ›**.
- Trigger: source, edge, **Auto/Normal**, **‹ level ›** (tap the level to type it), **50%**,
  **Pos 50%**, coupling (DC, AC, noise reject, HF/LF reject).
- Tabs below that:
  - **Acq**: Sample, Peak detect, Hi-Res or Average (2–128).
  - **Math**: CH1+CH2, CH1−CH2, CH1×CH2 (with **‹ scale ›**) or FFT of CH1/CH2 (window and zoom).
  - **Cursor**: off / time / amplitude / both, on CH1, CH2 or math; drag the dashed lines.
  - **View**: YT/XY, vectors/dots, persistence, and **Measure…** to choose the measurements.
  - **Tools**: demo, calibration output, zero cal, device log, about.
- On the screen:
  - drag the **1**/**2**/**M** tags (ground), the **T** tag (trigger level), the blue triangle
    (trigger position) and the cursors;
  - drag anywhere else to move the waveform: sideways moves the trigger point, up/down moves the
    trace under your finger (the first direction you move in is kept);
  - pinch with your fingers side by side for time/div, one above the other for the volts/div of
    the trace between them, diagonally for both. The screen follows your fingers and snaps to
    the nearest setting when you let go. In XY the pinch scales CH1 (sideways) and CH2 (up/down),
    in FFT it zooms the span. While stopped, the pinch zooms into the captured waveform;
  - while pinching one axis, moving both fingers together drags the other axis;
  - double-tap to run or stop;
  - long-press for: trigger level here, trigger point here, time or level cursors here, Autoset.
- The demo signal is only offered while no scope is connected and is always off when the app
  starts; without a scope the screen says so and points to it. Zero cal needs the inputs
  disconnected; the device log has a **Copy** button.
- Settings are saved on the phone. Capture pauses while the app is in the background.

## Limits

- Sample rates are capped like the Windows app (30 MS/s one channel, 15 MS/s two channels). If a
  phone's USB cannot keep up, the scope pauses sampling, so the time scale is wrong at the fastest
  time/div settings (a known signal then reads too high in frequency); use slower ones.
- No CSV or screenshot export yet.
- Landscape only.
- Android does not let an app keep USB permission for a device that re-enumerates, so the second
  dialog after the firmware upload is unavoidable until **Always** is ticked.

## Build

```powershell
dotnet build src/DDS120Scope.Android -c Release
```

Output: `src/DDS120Scope.Android/bin/Release/net10.0-android/com.dds120scope.app-Signed.apk`.
Needs the .NET 10 SDK with the `android` workload, the Android SDK (platform 36) and a JDK, as
installed by Visual Studio's .NET MAUI / Android option. No NuGet packages are used.

## How it is put together

| Path | What |
|---|---|
| `..\DDS120Scope\Core\**` | Linked unchanged from the Windows app: protocol, model tables, firmware loader and patch, `AcquisitionEngine` (acquire modes, trigger coupling, averaging, autoset), measurements, math and FFT (`Analysis`, `Spectrum`), demo source, and the screen renderer `Core/Display/ScopeRenderer.cs`. The WinUSB files compile but are never called. |
| `Usb/Dds120Device.cs` | Replaces the Windows `Core/Scope/Dds120Device.cs` (excluded from the link) with an Android factory of the same name, so `AcquisitionEngine.TryConnect()` compiles unchanged. Finds the scope, asks for permission, uploads firmware, waits for re-enumeration. |
| `Usb/AndroidDds120Source.cs` | `ISampleSource` over `UsbDeviceConnection`: vendor OUT requests `0xE0`..`0xE6`, bulk IN `0x86` read with 16 queued 16 KB `UsbRequest`s (falls back to `bulkTransfer` if a phone's queued reads misbehave). |
| `Usb/AndroidFx2Loader.cs` | FX2 RAM upload through request `0xA0` (CPUCS halt, 512-byte chunks, run). |
| `Usb/AndroidUsb.cs` | Device discovery and per-device permission requests. |
| `Screen/ScopeView.cs`, `Screen/AndroidCanvas.cs` | The Android `View` and `IScopeCanvas` for the shared renderer, plus pinch and tap. |
| `MainActivity.cs`, `UI/UiKit.cs` | Layout, controls, settings storage, engine events posted to the UI thread. |
| `res/xml/device_filter.xml` | `8102:8102` (stock) and `04B5:0120` (scope firmware) for the USB attach intent. |

The firmware is uploaded **unpatched**: the Windows identity patch (`Dds120FirmwarePatch`) only
exists to keep the WinUSB driver bound, which Android does not need. The app calls
`engine.SetKeepStockIdentity(false)`, so the scope comes back as `04B5:0120`; both IDs are in the
device filter.

## Firmware licence

The APK embeds `firmware/dds120-firmware.hex`, the SainSmart DDS120 firmware from
[OpenHantek6022](https://github.com/OpenHantek/OpenHantek6022), derived from
[sigrok-firmware-fx2lafw](https://sigrok.org), GPL-2.0-or-later. The notice is also shown under
**About** in the app.
