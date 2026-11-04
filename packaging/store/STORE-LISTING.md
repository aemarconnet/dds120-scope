# Microsoft Store submission: DDS-120 Scope

Everything Partner Center asks for, ready to paste. Fields marked *(you)* depend on your account.

## Before the first upload

1. In Partner Center, create the app and **reserve its name** (see *Product name* below).
2. Open **Product management > Product identity** and copy `Package/Identity/Name`,
   `Package/Identity/Publisher` and `Package/Properties/PublisherDisplayName` into
   `packaging/msix/store.json` (`identityName`, `publisher`, `publisherDisplayName`). Set
   `displayName` to the name you reserved.
3. Run `powershell -ExecutionPolicy Bypass -File packaging\msix\build-msix.ps1`. Upload
   `dist\msix\DDS120Scope-<version>.msix` under **Packages**. (Without step 2 the script builds a
   `-localtest` package, which the Store rejects because its identity does not match.)

For every later update, raise `<Version>` in `src/DDS120Scope/DDS120Scope.csproj` and rebuild.

## Pricing and availability

- **Price:** Free
- **Markets:** All
- **Visibility:** Public

## Properties

- **Category:** Utilities & tools
- **Privacy policy URL:** Not required: the app collects no personal information and makes no
  network connections. If Partner Center asks for one anyway, publish `PRIVACY.md` at a public
  address (the GitHub repository is private, so its files are not public).
- **Website:** *(you, optional)*
- **Support contact info:** *(you)* An email address or web page. The EULA's offer of the
  firmware source points people here, so it is needed.
- **System requirements, Hardware:** leave the standard boxes unticked.
- **Additional system requirements (minimum):**
  `SainSmart DDS-120 USB oscilloscope (a built-in demo signal lets you try the app without one). USB 2.0 port.`
- **Product declarations:** leave all unticked (no Xbox, no accessibility claims needed).

## Age ratings (questionnaire)

The app has no user-generated content, no chat, no purchases, no location use, no web access and
no personal data. Answer "No" to every content and interaction question; the expected result is
the lowest rating (IARC 3+ / Everyone).

## Packages

- **Restricted capability `runFullTrust`, justification:**
  `DDS-120 Scope is a Windows Forms desktop application packaged as MSIX. It talks to a USB oscilloscope through the in-box WinUSB driver using the SetupAPI and WinUSB Win32 APIs, which requires running as a full-trust desktop process.`
- **Device families:** Windows 10/11 Desktop only.

## Store listing (English)

### Product name
`DDS-120 Scope`

"DDS-120" is SainSmart's product name. Using it to say what hardware the app works with is
normally accepted, and the description and EULA state that the app is not affiliated with
SainSmart. If Partner Center or certification objects, `Scope for DDS-120` is the fallback.

### Short description
```
Turn your SainSmart DDS-120 USB oscilloscope into a full-featured two-channel scope: Autoset, cursors, 22 automatic measurements, math, FFT, XY mode and persistence. No driver to install.
```

### Description
```
DDS-120 Scope is free oscilloscope software for the SainSmart DDS-120, the two-channel 8-bit USB oscilloscope also sold as the Rocktech/BUUDAI BM102.

Plug the scope in and it is ready: the app uses the USB driver built into Windows, so there is nothing to install and no administrator rights are needed. When it connects, it loads the open-source DDS120 firmware into the scope's memory. The scope's own firmware is never changed, and unplugging it returns it to its factory state.

MEASURE LIKE A BENCH SCOPE
- Autoset finds your signals and sets volts/div, position, time/div and trigger in one click.
- Run/Stop, Single and Force trigger, with Auto and Normal trigger modes.
- Edge trigger with DC, AC, noise-reject, HF-reject and LF-reject coupling.
- Acquisition modes: Sample, Peak detect, Hi-Res and Average (2 to 128 waveforms).
- 22 automatic measurements per channel, including frequency, period, peak-to-peak, amplitude, RMS, rise and fall time, pulse width, duty cycle, overshoot, and CH1-to-CH2 delay and phase.
- Time and amplitude cursors with readouts, including the waveform value at each time cursor.
- Math channel: CH1+CH2, CH1-CH2 and CH1xCH2 with its own scale.
- FFT spectrum in dBV with Hann, Blackman, flat-top or rectangular window, and zoom.
- XY mode, adjustable persistence, and vectors or dots.

EASY TO DRIVE
- Drag the waveform to move it, use the mouse wheel for time/div and Shift+wheel for volts/div.
- Double-click to run or stop, right-click to set the trigger or place cursors at that point.
- Zoom into a stopped capture.
- Save captures as CSV (including the math channel) or as PNG screenshots.
- Zero calibration for your unit's offsets.

SPEED
Up to 30 MS/s on one channel and 15 MS/s on both, the rates the DDS-120 delivers exactly over USB 2.0.

NO SCOPE YET?
Turn on the demo signal in the Tools tab to try every feature without hardware.

Requires a SainSmart DDS-120 USB oscilloscope for real measurements. DDS-120 Scope is an independent project, not made or endorsed by SainSmart. The app's own code is dedicated to the public domain (CC0 1.0); the scope firmware it loads is open source under the GNU GPL.
```

### What's new in this version
```
1.0.1: The demo signal now always starts off, and the app shows a clear message when no scope is connected, with a pointer to the demo signal. New About box with version and license information.
```

### Product features (one per line, up to 20)
```
Autoset: finds your signals and sets up the scope in one click
22 automatic measurements per channel, including rise time, duty cycle and phase
Time and amplitude cursors with live readouts
FFT spectrum analyzer in dBV with four window types
Math channel: CH1+CH2, CH1-CH2, CH1xCH2
Sample, Peak detect, Hi-Res and Average acquisition
Edge trigger with AC, noise-reject, HF-reject and LF-reject coupling
XY mode and adjustable persistence
Up to 30 MS/s on one channel, 15 MS/s on two
Drag, wheel and right-click control right on the waveform
Zoom into a stopped capture
Export captures to CSV and screenshots to PNG
No driver to install: uses the USB driver built into Windows
Demo signal to try the app without hardware
```

### Search terms (7 maximum, 30 characters each)
```
oscilloscope
DDS-120
SainSmart
USB oscilloscope
FFT spectrum analyzer
electronics
waveform
```

### Copyright and trademark info
```
App code dedicated to the public domain (CC0 1.0). Scope firmware by the sigrok and OpenHantek6022 projects, GPL-2.0-or-later. SainSmart and DDS-120 are trademarks of their respective owners.
```

### Additional license terms
Paste the whole of `packaging/store/EULA.txt`.

### Screenshots (desktop, PNG, 2002 x 1256)
Made by the build in `dist/store/screenshots/` (not kept in git). Add captions:

1. `01-live-measurements-cursors.png`: "Live measurements and time cursors on both channels"
2. `02-live-fft.png`: "FFT of a 1 kHz square wave, showing its odd harmonics"
3. `03-live-rise-time.png`: "Rise time at 30 MS/s with 16-waveform averaging"
4. `04-demo-math-persistence.png`: "Math channel and persistence (demo signal)"

### Store logos
`dist/store/store-logo-300.png`, `-1080.png` and `-2160.png` (1:1, opaque). Upload the 1080 or
2160 one as the 1:1 box art; the 300 one fits the app tile icon slot.

## Submission options: Notes for certification
```
This app is oscilloscope software for the SainSmart DDS-120 USB oscilloscope. Without the scope connected it shows "No scope connected". To test all functions without hardware: in the panel on the right, open the "Tools" tab and tick "Demo signal (no hardware)". The demo signal then drives every feature (Autoset, measurements, cursors, math, FFT, XY, persistence).

The app makes no network connections and collects no data. runFullTrust is needed because it is a Windows Forms desktop app that reaches the scope through the in-box WinUSB driver (SetupAPI/WinUSB Win32 APIs). It installs no driver and needs no administrator rights.
```
