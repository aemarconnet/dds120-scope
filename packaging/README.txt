DDS-120 Scope  -  portable software for the SainSmart DDS-120 USB oscilloscope
==============================================================================

Nothing to install. Copy this folder anywhere (USB stick, Desktop, network share) and run
DDS120Scope.exe. Needs Windows 10 or 11; no driver, no .NET download, no admin rights.

Getting started
---------------
1. Plug in the DDS-120. Windows binds its built-in WinUSB driver automatically.
2. Start DDS120Scope.exe. On first connect it loads the open-source scope firmware into the
   scope's RAM (about one second). The status bar shows each step.
3. Connect a probe and adjust Volts/div and Time/div until the signal fits.

The scope forgets the firmware when it loses power, so unplugging it always returns it to
its factory state. Nothing is written to the scope's EEPROM.

Controls
--------
The side panel has the run buttons at the top (always visible), then Vertical (one column per
channel), Horizontal and Trigger, then tabs for Acquire, Math, Cursors, Display and Tools. Rows
that do not apply to the current choice are hidden.

  RUN/STOP, SINGLE ........ F5 / F6. Single waits for one trigger, then stops.
  AUTOSET ................. F7. Looks at both inputs and sets volts/div, position, time/div (about
                            two periods) and the trigger. Channels without a signal are turned off.
  Force trig .............. F8. Shows the next acquisition even without a trigger (ends a Single).
  Acquire ................. Sample: one raw sample per point. Peak detect: the min/max of the raw
                            samples behind each point, so narrow spikes show at slow time/div.
                            Hi-Res: their mean, less noise (default). Average: 2-128 triggered
                            acquisitions averaged. The modes differ only at 2 ms/div and slower.
  Time/div ................ also the mouse wheel over the screen (or a touchpad pinch).
                            1 us/div to 100 ms/div. While stopped, it zooms into the capture.
  On the screen ........... drag an empty spot to move the waveform: sideways moves the trigger
                            point, up/down moves the trace under the pointer. Shift+wheel changes
                            that trace's volts/div. Double-click runs/stops. Right-click for trigger
                            level/point here, cursors here, Autoset.
  Trigger ................. source, edge, level, mode (Auto shows the signal even without a trigger;
                            Normal only when triggered) and coupling: DC, AC, noise reject (wider
                            hysteresis), HF reject and LF reject (filter around 50 kHz). Drag the
                            "T" marker on the right edge to set the level, or the blue triangle at
                            the top to move the trigger point.
  Level to 50% ............ puts the trigger level halfway between the signal's min and max.
  Channel position ........ drag the "1"/"2" markers on the left edge.
  Probe x1/x10 ............ scales readings to match a 10:1 probe.
  Coupling DC/AC .......... switched in the scope's front end.
  Math .................... CH1+CH2, CH1-CH2 or CH1xCH2 with its own scale; drag the "M" marker.
                            FFT shows the spectrum of CH1 or CH2 in dBV, 10 dB/div, with a choice of
                            window and zoom; the time/div setting picks the sample rate and so the
                            span. The strongest component is marked and listed.
  Cursors ................. time, amplitude or both, on CH1, CH2 or math; drag the dashed lines.
                            Time cursors also read the waveform at each cursor. In FFT they read
                            frequency and dBV.
  Display ................. YT or XY (CH1 horizontal, CH2 vertical), vectors or dots, and
                            persistence (0.5 s to infinite) to see jitter and glitches.
  Measurements... ......... choose what each channel line shows: frequency, period, peak-to-peak,
                            amplitude, high, low, max, min, mean, RMS, cycle mean/RMS, rise and fall
                            time (10-90 %), positive/negative width and duty, overshoot, and on the
                            CH2 line the delay and phase from CH1.
  Cal output .............. frequency of the scope's square-wave calibration output (if present).
  Zero calibration ........ disconnect both inputs first; measures the zero offset at every gain.
  Save CSV / Screenshot ... export the current capture (with the math waveform) or a PNG image.
  Demo signal ............. runs without hardware (simulated square and sine waves). Only offered
                            while no scope is connected, and always off when the app starts.

Sample rates: up to 30 MS/s with only CH1 on, 15 MS/s with both channels. Turning CH2 off
gives CH1 the faster rate. Bandwidth is limited by the DDS-120's analog front end.

Settings
--------
Saved in DDS120Scope.ini next to the exe (or in %APPDATA%\DDS120Scope if this folder is
read-only). Delete it to reset everything. Advanced keys:
  maxrate.single / maxrate.dual   sample-rate limits in S/s (defaults 30000000 / 15000000,
                                  also the highest allowed: faster ones exceed what USB 2.0
                                  delivers and would show a wrong time scale)
  usb.keepstockid=0               let the scope re-enumerate as 04B5:0120 (needs WinUSB bound
                                  to that ID manually; only useful for other software)

Troubleshooting
---------------
"Windows has no working driver for it right now" - unplug the scope, plug it back in, wait a
few seconds. This happens if the scope still holds firmware from another program.

"No DDS-120 found" - check the cable; in Device Manager the scope should appear as "USB102"
(or "fx2lafw" while running) under "Universal Serial Bus devices".

tools\Dds120Probe.exe is a command-line diagnostics tool:
  Dds120Probe list        all USB devices
  Dds120Probe info        the scope's USB descriptors
  Dds120Probe load        load the firmware and report
  Dds120Probe capture     capture raw samples and print statistics
  Dds120Probe msos        query the WinUSB descriptor the firmware patch adds
  Dds120Probe selftest    run autoset, measurements, acquire modes, math, FFT and trigger
                          coupling on the demo signal (add --hw to use the scope)

Credits and licenses: see FIRMWARE-LICENSE.txt.
