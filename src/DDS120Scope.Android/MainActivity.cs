using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Usb;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Dds120.Display;
using Dds120.Droid.Screen;
using Dds120.Droid.UI;
using Dds120.Droid.Usb;
using Dds120.Scope;
using Dds120.Util;

namespace Dds120.Droid
{
    // Scope screen on the left, controls on the right. The acquisition engine (shared Core) runs on
    // its own thread; its events are posted to the UI thread here.
    [Activity(Label = "@string/app_name", MainLauncher = true, Exported = true,
        LaunchMode = LaunchMode.SingleTask,
        ScreenOrientation = ScreenOrientation.SensorLandscape,
        // Plugging in a USB device can change the keyboard/navigation configuration; keep the
        // activity (and the engine) alive instead of recreating it.
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
            ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden |
            ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.Density)]
    [IntentFilter(new[] { UsbManager.ActionUsbDeviceAttached })]
    [MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/device_filter")]
    public sealed class MainActivity : Activity
    {
        const string PrefsName = "settings";
        const float PanelWidthDp = 228;

        readonly Dictionary<string, string> stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly ScopeSettings settings = new ScopeSettings();
        readonly Calibration calibration = new Calibration();
        readonly List<string> logLines = new List<string>();
        readonly object frameLock = new object();
        AcquisitionEngine engine;
        Handler ui;
        ScopeView view;
        volatile bool destroyed;
        Frame pendingFrame;
        Analysis pendingAnalysis;
        int framePosted;
        Frame shownFrame;
        bool syncing;
        bool resumeRunning;
        bool saveDue, saveScheduled;
        DateTime saveAt;

        Button runButton, singleButton, timebaseButton, sourceButton, edgeButton, modeButton, levelButton,
            demoButton, calButton, zeroButton, autosetButton, trigCouplingButton, acquireButton, mathButton,
            mathScaleButton, fftWindowButton, fftZoomButton, cursorButton, cursorSourceButton, xyButton,
            persistButton, dotsButton;
        View demoRow, mathScaleRow, fftRow;
        static readonly string[] TabNames = { "Acq", "Math", "Cursor", "View", "Tools" };
        readonly Button[] tabButtons = new Button[TabNames.Length];
        readonly LinearLayout[] pages = new LinearLayout[TabNames.Length];
        readonly Button[] enableButton = new Button[2];
        readonly Button[] couplingButton = new Button[2];
        readonly Button[] probeButton = new Button[2];
        readonly Button[] invertButton = new Button[2];
        readonly Button[] vdivButton = new Button[2];

        ScopeRenderer Renderer { get { return view.Renderer; } }

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            ui = new Handler(Looper.MainLooper);
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            LoadStored();
            settings.Load(stored);
            calibration.Load(stored);
            // The demo signal always starts off, so a missing scope is reported instead of hidden.
            const bool demo = false;
            stored.Remove("demo");

            AndroidUsb.Init(this);
            AndroidUsb.Log += OnUsbLog;

            SetContentView(BuildUi());

            engine = new AcquisitionEngine(settings, calibration, demo);
            // Android needs no driver binding, so load the unmodified OpenHantek firmware (04B5:0120)
            // rather than the Windows identity patch.
            engine.SetKeepStockIdentity(false);
            engine.FrameReady += OnFrame;
            engine.StatusChanged += (status, text) => Post(() => OnEngineStatus(status, text));
            engine.Log += line => Post(() => AddLog(line));

            SyncControls();
            HandleIntent(Intent);
            engine.Start();
        }

        protected override void OnNewIntent(Intent intent)
        {
            base.OnNewIntent(intent);
            Intent = intent;
            HandleIntent(intent);
        }

        // Started (or brought to front) because a DDS-120 was plugged in.
        void HandleIntent(Intent intent)
        {
            if (intent == null || intent.Action != UsbManager.ActionUsbDeviceAttached)
                return;
            AddLog("USB device attached");
            if (engine.IsDemo)
                SetDemo(false);
            engine.UpdateSettings(settings); // wakes the engine so it connects now
        }

        protected override void OnStart()
        {
            base.OnStart();
            AndroidUsb.Foreground = true;
            if (resumeRunning)
            {
                resumeRunning = false;
                engine.SetRunning(true);
            }
            UpdateRunButton();
        }

        protected override void OnStop()
        {
            // Not visible: stop capturing (USB traffic, battery) and pick up again on return.
            AndroidUsb.Foreground = false;
            resumeRunning = engine.Running;
            if (resumeRunning)
                engine.SetRunning(false);
            SaveNow();
            base.OnStop();
        }

        protected override void OnDestroy()
        {
            destroyed = true;
            engine.Dispose();
            AndroidUsb.Log -= OnUsbLog;
            AndroidUsb.Shutdown();
            SaveNow();
            base.OnDestroy();
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus)
                HideSystemBars();
        }

        void Post(Action a)
        {
            if (!destroyed)
                ui.Post(() =>
                {
                    if (!destroyed)
                        a();
                });
        }

        int Dp(float dp)
        {
            return UiKit.Dp(this, dp);
        }

        // ---- layout -------------------------------------------------------------------------

        View BuildUi()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            root.SetBackgroundColor(new Color(Palette.Window));
            root.SetOnApplyWindowInsetsListener(new InsetsPadding());

            view = new ScopeView(this);
            Renderer.Settings = settings;
            Renderer.GroundDragged += (ch, divs) => Change(() =>
            {
                double p = Math.Max(-4, Math.Min(4, divs));
                if (ch < 2) settings.Channels[ch].Position = p;
                else settings.MathPosition = p;
            });
            Renderer.TriggerLevelDragged += volts => Change(() => settings.TriggerLevel = volts);
            Renderer.TriggerPositionDragged += frac => Change(() => settings.TriggerPosition = frac);
            Renderer.CursorDragged += (which, value) => Change(() => MoveCursor(which, value));
            view.PannedSideways += divs => Change(() => ScreenActions.PanHorizontal(settings, divs));
            view.PannedVertically += (trace, divs) => Change(() => ScreenActions.PanVertical(settings, trace, divs));
            view.ZoomedHorizontally += factor => Change(() => ScreenActions.ZoomHorizontal(settings, factor));
            view.ZoomedVertically += (trace, factor) => Change(() => ScreenActions.ZoomVertical(settings, trace, factor));
            view.DoubleTapped += () => SetRunning(!engine.Running);
            view.LongPressed += ShowScreenMenu;
            view.Tapped += OnScreenTapped;
            root.AddView(view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));

            // Right column: run buttons pinned at the top, the rest scrolls if it has to.
            var side = new LinearLayout(this) { Orientation = Orientation.Vertical };
            side.SetBackgroundColor(new Color(Palette.Panel));
            side.SetPadding(Dp(4), Dp(4), Dp(4), 0);
            runButton = UiKit.Button(this, "RUN", () => SetRunning(!engine.Running));
            singleButton = UiKit.Button(this, "SINGLE", () => { engine.Single(); UpdateRunButton(); });
            side.AddView(UiKit.Row(this, runButton, singleButton));
            autosetButton = UiKit.Button(this, "AUTOSET", Autoset);
            UiKit.Style(autosetButton, Palette.Accent, Palette.White);
            side.AddView(UiKit.Row(this, autosetButton, UiKit.Button(this, "FORCE", () => engine.ForceTrigger())));

            var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
            panel.SetPadding(0, 0, 0, Dp(8));
            var scroll = new ScrollView(this) { FillViewport = true };
            scroll.AddView(panel);
            side.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            root.AddView(side, new LinearLayout.LayoutParams(Dp(PanelWidthDp), ViewGroup.LayoutParams.MatchParent));

            float[] stepper = { 1f, 2.6f, 1f };

            // Vertical: CH1 on the left half, CH2 on the right
            panel.AddView(UiKit.Header(this, "VERTICAL", Palette.TextDim));
            for (int ch = 0; ch < 2; ch++)
                BuildChannel(ch);
            panel.AddView(UiKit.Row(this, enableButton[0], enableButton[1]));
            panel.AddView(UiKit.WeightedRow(this, new[] { 1f, 2.2f, 1f, 1f, 2.2f, 1f },
                UiKit.Button(this, "‹", () => StepVoltsPerDiv(0, -1)), vdivButton[0], UiKit.Button(this, "›", () => StepVoltsPerDiv(0, 1)),
                UiKit.Button(this, "‹", () => StepVoltsPerDiv(1, -1)), vdivButton[1], UiKit.Button(this, "›", () => StepVoltsPerDiv(1, 1))));
            panel.AddView(UiKit.Row(this, couplingButton[0], probeButton[0], invertButton[0], couplingButton[1], probeButton[1], invertButton[1]));

            // Horizontal
            panel.AddView(UiKit.Header(this, "HORIZONTAL", Palette.Accent));
            timebaseButton = UiKit.Button(this, "", PickTimebase);
            panel.AddView(UiKit.WeightedRow(this, stepper,
                UiKit.Button(this, "‹", () => StepTimebase(-1)), timebaseButton, UiKit.Button(this, "›", () => StepTimebase(1))));

            // Trigger
            panel.AddView(UiKit.Header(this, "TRIGGER", Palette.Accent));
            sourceButton = UiKit.Button(this, "", () => Change(() => settings.TriggerSource = 1 - settings.TriggerSource));
            edgeButton = UiKit.Button(this, "", () => Change(() =>
                settings.TriggerEdge = settings.TriggerEdge == TriggerEdge.Rising ? TriggerEdge.Falling : TriggerEdge.Rising));
            modeButton = UiKit.Button(this, "", () => Change(() =>
                settings.TriggerMode = settings.TriggerMode == TriggerMode.Auto ? TriggerMode.Normal : TriggerMode.Auto));
            panel.AddView(UiKit.Row(this, sourceButton, edgeButton, modeButton));
            levelButton = UiKit.Button(this, "", EnterTriggerLevel);
            panel.AddView(UiKit.WeightedRow(this, stepper,
                UiKit.Button(this, "‹", () => StepTriggerLevel(-1)), levelButton, UiKit.Button(this, "›", () => StepTriggerLevel(1))));
            trigCouplingButton = UiKit.Button(this, "", PickTriggerCoupling);
            panel.AddView(UiKit.Row(this,
                UiKit.Button(this, "50%", TriggerTo50),
                UiKit.Button(this, "Pos 50%", () => Change(() => settings.TriggerPosition = 0.5)),
                trigCouplingButton));

            // Tabs for everything else
            var tabRow = new View[TabNames.Length];
            for (int i = 0; i < TabNames.Length; i++)
            {
                int index = i;
                tabButtons[i] = UiKit.Button(this, TabNames[i], () => SelectTab(index, true));
                tabButtons[i].SetTextSize(Android.Util.ComplexUnitType.Sp, 12);
                tabButtons[i].SetPadding(Dp(1), 0, Dp(1), 0);
                tabRow[i] = tabButtons[i];
            }
            LinearLayout tabs = UiKit.Row(this, tabRow);
            tabs.SetPadding(0, Dp(10), 0, Dp(2));
            panel.AddView(tabs);
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = new LinearLayout(this) { Orientation = Orientation.Vertical };
                panel.AddView(pages[i]);
            }

            // Acquire
            acquireButton = UiKit.Button(this, "", PickAcquire);
            pages[0].AddView(UiKit.Row(this, acquireButton));

            // Math
            mathButton = UiKit.Button(this, "", PickMath);
            pages[1].AddView(UiKit.Row(this, mathButton));
            mathScaleButton = UiKit.Button(this, "", () => { });
            mathScaleRow = UiKit.WeightedRow(this, stepper,
                UiKit.Button(this, "‹", () => StepMathScale(-1)), mathScaleButton, UiKit.Button(this, "›", () => StepMathScale(1)));
            pages[1].AddView(mathScaleRow);
            fftWindowButton = UiKit.Button(this, "", () => Change(() =>
                settings.FftWindow = (FftWindow)(((int)settings.FftWindow + 1) % Enum.GetValues(typeof(FftWindow)).Length)));
            fftZoomButton = UiKit.Button(this, "", () => Change(() =>
                settings.FftZoom = ScopeSettings.FftZooms[(Array.IndexOf(ScopeSettings.FftZooms, settings.FftZoom) + 1) % ScopeSettings.FftZooms.Length]));
            fftRow = UiKit.Row(this, fftWindowButton, fftZoomButton);
            pages[1].AddView(fftRow);

            // Cursors
            cursorButton = UiKit.Button(this, "", () => Change(() => settings.Cursors = (CursorMode)(((int)settings.Cursors + 1) % 4)));
            cursorSourceButton = UiKit.Button(this, "", () => Change(() => settings.CursorSource = (settings.CursorSource + 1) % 3));
            pages[2].AddView(UiKit.Row(this, cursorButton, cursorSourceButton));

            // View (display)
            xyButton = UiKit.Button(this, "", () => Change(() => settings.XY = !settings.XY));
            persistButton = UiKit.Button(this, "", PickPersistence);
            dotsButton = UiKit.Button(this, "", () => Change(() => settings.Dots = !settings.Dots));
            pages[3].AddView(UiKit.Row(this, xyButton, dotsButton));
            pages[3].AddView(UiKit.Row(this, persistButton, UiKit.Button(this, "Measure…", PickMeasurements)));

            // Tools
            demoButton = UiKit.Button(this, "", () => SetDemo(!engine.IsDemo));
            demoRow = UiKit.Row(this, demoButton);
            pages[4].AddView(demoRow);
            calButton = UiKit.Button(this, "", PickCalFrequency);
            zeroButton = UiKit.Button(this, "Zero cal", ZeroCalibration);
            pages[4].AddView(UiKit.Row(this, calButton, zeroButton));
            pages[4].AddView(UiKit.Row(this, UiKit.Button(this, "Device log", ShowLog), UiKit.Button(this, "About", ShowAbout)));

            string v;
            int tab;
            if (!stored.TryGetValue("ui.tab", out v) || !int.TryParse(v, out tab) || tab < 0 || tab >= TabNames.Length)
                tab = 0;
            SelectTab(tab, false);
            return root;
        }

        void SelectTab(int index, bool remember)
        {
            for (int i = 0; i < pages.Length; i++)
            {
                bool on = i == index;
                UiKit.Style(tabButtons[i], on ? Palette.Accent : Palette.Control, on ? Palette.White : Palette.Text);
                pages[i].Visibility = on ? ViewStates.Visible : ViewStates.Gone;
            }
            if (remember)
            {
                stored["ui.tab"] = index.ToString(CultureInfo.InvariantCulture);
                RequestSave();
            }
        }

        void BuildChannel(int ch)
        {
            enableButton[ch] = UiKit.Button(this, "", () => Change(() =>
            {
                settings.Channels[ch].Enabled = !settings.Channels[ch].Enabled;
                if (!settings.Channels[0].Enabled && !settings.Channels[1].Enabled)
                    settings.Channels[1 - ch].Enabled = true; // keep at least one channel
            }));
            couplingButton[ch] = UiKit.Button(this, "", () => Change(() => settings.Channels[ch].AcCoupling = !settings.Channels[ch].AcCoupling));
            probeButton[ch] = UiKit.Button(this, "", () => Change(() => settings.Channels[ch].Probe = settings.Channels[ch].Probe == 10 ? 1 : 10));
            invertButton[ch] = UiKit.Button(this, "INV", () => Change(() => settings.Channels[ch].Inverted = !settings.Channels[ch].Inverted));
            vdivButton[ch] = UiKit.Button(this, "", () => PickVoltsPerDiv(ch));
            foreach (Button b in new[] { couplingButton[ch], probeButton[ch], invertButton[ch], vdivButton[ch] })
            {
                b.SetTextSize(Android.Util.ComplexUnitType.Sp, 12);
                b.SetPadding(Dp(1), 0, Dp(1), 0);
            }
        }

        void HideSystemBars()
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(35)) // always edge-to-edge from 35 on
                    Window.SetDecorFitsSystemWindows(false);
                IWindowInsetsController c = Window.InsetsController;
                if (c != null)
                {
                    c.Hide(WindowInsets.Type.SystemBars());
                    c.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                }
            }
            else
            {
#pragma warning disable CA1422 // the replacement above needs API 30
                Window.DecorView.SystemUiFlags = SystemUiFlags.ImmersiveSticky |
                    SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation | SystemUiFlags.LayoutStable |
                    SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen;
#pragma warning restore CA1422
            }
        }

        // Keeps the content clear of display cutouts (and of the system bars while they show).
        sealed class InsetsPadding : Java.Lang.Object, View.IOnApplyWindowInsetsListener
        {
            public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    Android.Graphics.Insets i = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                    v.SetPadding(i.Left, i.Top, i.Right, i.Bottom);
                }
                else if (OperatingSystem.IsAndroidVersionAtLeast(28) && insets.DisplayCutout != null)
                {
                    DisplayCutout d = insets.DisplayCutout;
                    v.SetPadding(d.SafeInsetLeft, d.SafeInsetTop, d.SafeInsetRight, d.SafeInsetBottom);
                }
                return insets;
            }
        }

        // ---- settings <-> controls ----------------------------------------------------------

        // Applies a change made through a control or the screen, then refreshes everything.
        void Change(Action apply)
        {
            if (syncing)
                return;
            apply();
            SyncControls();
            engine?.UpdateSettings(settings);
            if (shownFrame != null)
                Renderer.Analysis = Analysis.Of(shownFrame, settings); // math or measurements may have changed
            view.Invalidate();
            RequestSave();
        }

        void MoveCursor(ScopeRenderer.Drag which, double value)
        {
            switch (which)
            {
                case ScopeRenderer.Drag.CursorX1: settings.CursorX1 = value; break;
                case ScopeRenderer.Drag.CursorX2: settings.CursorX2 = value; break;
                case ScopeRenderer.Drag.CursorY1: settings.CursorY1 = value; break;
                case ScopeRenderer.Drag.CursorY2: settings.CursorY2 = value; break;
            }
        }

        static string AcquireText(ScopeSettings s)
        {
            switch (s.Acquire)
            {
                case AcquireMode.Sample: return "Sample";
                case AcquireMode.PeakDetect: return "Peak detect";
                case AcquireMode.Average: return "Average " + s.AverageCount;
                default: return "Hi-Res";
            }
        }

        static string CouplingText(TriggerCoupling c)
        {
            switch (c)
            {
                case TriggerCoupling.AC: return "AC";
                case TriggerCoupling.NoiseReject: return "Noise reject";
                case TriggerCoupling.HFReject: return "HF reject";
                case TriggerCoupling.LFReject: return "LF reject";
                default: return "DC";
            }
        }

        static string PersistenceText(int index)
        {
            double p = ScopeSettings.PersistenceTimes[index];
            return p == 0 ? "Persist off" : double.IsInfinity(p) ? "Persist ∞" : "Persist " + Si.Short(p, "s");
        }

        void SyncControls()
        {
            syncing = true;
            try
            {
                timebaseButton.Text = Si.Short(settings.SecondsPerDiv, "s") + "/div";
                int src = settings.TriggerSource;
                sourceButton.Text = "CH" + (src + 1);
                UiKit.Style(sourceButton, Palette.Control, Palette.Channel[src]);
                edgeButton.Text = settings.TriggerEdge == TriggerEdge.Rising ? "↑ Rise" : "↓ Fall";
                modeButton.Text = settings.TriggerMode == TriggerMode.Auto ? "Auto" : "Normal";
                levelButton.Text = "Level " + Si.Format(settings.TriggerLevel, "V");
                calButton.Text = "Cal " + Si.Short(settings.CalFrequency, "Hz");
                bool demo = engine != null && engine.IsDemo;
                demoButton.Text = demo ? "Demo signal: ON" : "Demo signal: off";
                UiKit.Style(demoButton, demo ? Palette.Accent : Palette.Control, demo ? Palette.White : Palette.Text);
                // The demo signal is only offered while no scope is connected (or the demo is running).
                demoRow.Visibility = engine != null && engine.HardwareConnected ? ViewStates.Gone : ViewStates.Visible;

                trigCouplingButton.Text = CouplingText(settings.TriggerCoupling).Replace("reject", "rej");
                acquireButton.Text = AcquireText(settings);
                mathButton.Text = settings.MathMode == MathFunction.Off ? "Math: off" : "Math: " + settings.MathName;
                UiKit.Style(mathButton, settings.MathMode == MathFunction.Off ? Palette.Control : Palette.Math,
                    settings.MathMode == MathFunction.Off ? Palette.Text : Palette.Black);
                bool wave = settings.MathMode != MathFunction.Off && settings.MathMode != MathFunction.Fft;
                mathScaleRow.Visibility = wave ? ViewStates.Visible : ViewStates.Gone;
                fftRow.Visibility = settings.MathMode == MathFunction.Fft ? ViewStates.Visible : ViewStates.Gone;
                mathScaleButton.Text = Si.Short(settings.MathScale, settings.MathUnit) + "/div";
                fftWindowButton.Text = settings.FftWindow == FftWindow.FlatTop ? "Flat top" : settings.FftWindow.ToString();
                fftZoomButton.Text = "Zoom ×" + settings.FftZoom;
                string[] cursorNames = { "Cursors off", "Time", "Amplitude", "Time+Ampl" };
                cursorButton.Text = cursorNames[(int)settings.Cursors];
                cursorSourceButton.Text = settings.CursorSource == 2 ? "on Math" : "on CH" + (settings.CursorSource + 1);
                cursorSourceButton.Enabled = settings.Cursors != CursorMode.Off;
                cursorSourceButton.Alpha = settings.Cursors != CursorMode.Off ? 1f : 0.45f;
                xyButton.Text = settings.XY ? "XY" : "YT";
                UiKit.Style(xyButton, settings.XY ? Palette.XY : Palette.Control, settings.XY ? Palette.Black : Palette.Text);
                persistButton.Text = PersistenceText(settings.PersistenceIndex);
                dotsButton.Text = settings.Dots ? "Dots" : "Vectors";
                for (int ch = 0; ch < 2; ch++)
                {
                    ChannelSettings c = settings.Channels[ch];
                    enableButton[ch].Text = c.Enabled ? "CH" + (ch + 1) : "CH" + (ch + 1) + " off";
                    UiKit.Style(enableButton[ch], c.Enabled ? Palette.Channel[ch] : Palette.Control, c.Enabled ? Palette.Black : Palette.TextDim);
                    couplingButton[ch].Text = c.AcCoupling ? "AC" : "DC";
                    probeButton[ch].Text = c.Probe == 10 ? "x10" : "x1";
                    UiKit.Style(invertButton[ch], c.Inverted ? Palette.Accent : Palette.Control, c.Inverted ? Palette.White : Palette.TextDim);
                    vdivButton[ch].Text = Si.Short(c.VoltsPerDiv, "V");
                    couplingButton[ch].Enabled = probeButton[ch].Enabled = invertButton[ch].Enabled = vdivButton[ch].Enabled = c.Enabled;
                    couplingButton[ch].Alpha = probeButton[ch].Alpha = vdivButton[ch].Alpha = c.Enabled ? 1f : 0.45f;
                }
            }
            finally
            {
                syncing = false;
            }
        }

        void StepTimebase(int step)
        {
            Change(() => settings.TimebaseIndex = Math.Max(0, Math.Min(ScopeSettings.Timebases.Length - 1, settings.TimebaseIndex + step)));
        }

        void StepVoltsPerDiv(int ch, int step)
        {
            Change(() => settings.Channels[ch].GainIndex = Math.Max(0, Math.Min(Dds120Model.Gains.Length - 1, settings.Channels[ch].GainIndex + step)));
        }

        void StepTriggerLevel(int direction)
        {
            double step = settings.Channels[settings.TriggerSource].VoltsPerDiv / 10;
            Change(() => settings.TriggerLevel = Math.Round((settings.TriggerLevel + direction * step) / (step / 5)) * (step / 5));
        }

        void PickTimebase()
        {
            var items = new string[ScopeSettings.Timebases.Length];
            for (int i = 0; i < items.Length; i++)
                items[i] = Si.Short(ScopeSettings.Timebases[i], "s") + "/div";
            Pick("Time/div", items, settings.TimebaseIndex, i => Change(() => settings.TimebaseIndex = i));
        }

        void PickVoltsPerDiv(int ch)
        {
            ChannelSettings c = settings.Channels[ch];
            var items = new string[Dds120Model.Gains.Length];
            for (int i = 0; i < items.Length; i++)
                items[i] = Si.Short(Dds120Model.Gains[i].VoltsPerDiv * c.Probe, "V") + "/div";
            Pick("CH" + (ch + 1) + " Volts/div", items, c.GainIndex, i => Change(() => settings.Channels[ch].GainIndex = i));
        }

        void StepMathScale(int step)
        {
            Change(() => settings.MathScaleIndex = Math.Max(0, Math.Min(ScopeSettings.MathScales.Length - 1, settings.MathScaleIndex + step)));
        }

        void PickTriggerCoupling()
        {
            var values = (TriggerCoupling[])Enum.GetValues(typeof(TriggerCoupling));
            Pick("Trigger coupling", Array.ConvertAll(values, CouplingText), (int)settings.TriggerCoupling,
                i => Change(() => settings.TriggerCoupling = values[i]));
        }

        // One list for the mode and the number of averages.
        void PickAcquire()
        {
            var items = new List<string> { "Sample", "Peak detect", "Hi-Res" };
            foreach (int n in ScopeSettings.AverageCounts)
                items.Add("Average " + n);
            int selected = settings.Acquire == AcquireMode.Average
                ? 3 + Array.IndexOf(ScopeSettings.AverageCounts, settings.AverageCount)
                : (int)settings.Acquire;
            Pick("Acquire", items.ToArray(), selected, i => Change(() =>
            {
                if (i < 3)
                {
                    settings.Acquire = (AcquireMode)i;
                }
                else
                {
                    settings.Acquire = AcquireMode.Average;
                    settings.AverageCount = ScopeSettings.AverageCounts[i - 3];
                }
            }));
        }

        void PickMath()
        {
            string[] items = { "Off", "CH1 + CH2", "CH1 − CH2", "CH1 × CH2", "FFT of CH1", "FFT of CH2" };
            int selected = settings.MathMode == MathFunction.Fft ? 4 + settings.FftSource : (int)settings.MathMode;
            Pick("Math", items, selected, i => Change(() =>
            {
                settings.MathMode = i >= 4 ? MathFunction.Fft : (MathFunction)i;
                if (i >= 4)
                    settings.FftSource = i - 4;
            }));
        }

        void PickPersistence()
        {
            var items = new string[ScopeSettings.PersistenceTimes.Length];
            for (int i = 0; i < items.Length; i++)
                items[i] = PersistenceText(i);
            Pick("Persistence", items, settings.PersistenceIndex, i => Change(() => settings.PersistenceIndex = i));
        }

        void PickMeasurements()
        {
            var types = (MeasureType[])Enum.GetValues(typeof(MeasureType));
            var names = Array.ConvertAll(types, Measurements.LongName);
            var chosen = new bool[types.Length];
            for (int i = 0; i < types.Length; i++)
                chosen[i] = Array.IndexOf(settings.MeasureItems, types[i]) >= 0;
            new AlertDialog.Builder(this)
                .SetTitle("Measurements")
                .SetMultiChoiceItems(names, chosen, (s, e) => chosen[e.Which] = e.IsChecked)
                .SetPositiveButton("OK", (s, e) =>
                {
                    var items = new List<MeasureType>();
                    for (int i = 0; i < types.Length; i++)
                        if (chosen[i])
                            items.Add(types[i]);
                    Change(() => settings.MeasureItems = items.ToArray());
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Show();
        }

        void PickCalFrequency()
        {
            var items = new string[Dds120Model.CalFrequencies.Length];
            for (int i = 0; i < items.Length; i++)
                items[i] = Si.Short(Dds120Model.CalFrequencies[i], "Hz");
            Pick("Calibration output", items, Array.IndexOf(Dds120Model.CalFrequencies, settings.CalFrequency),
                i => Change(() => settings.CalFrequency = Dds120Model.CalFrequencies[i]));
        }

        void Pick(string title, string[] items, int selected, Action<int> chosen)
        {
            new AlertDialog.Builder(this)
                .SetTitle(title)
                .SetSingleChoiceItems(items, selected, (s, e) =>
                {
                    (s as IDialogInterface)?.Dismiss();
                    chosen(e.Which);
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Show();
        }

        void EnterTriggerLevel()
        {
            var input = new EditText(this)
            {
                InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned,
                Text = settings.TriggerLevel.ToString("0.###", CultureInfo.InvariantCulture),
            };
            input.SetSelectAllOnFocus(true);
            var box = new FrameLayout(this);
            box.SetPadding(Dp(20), Dp(8), Dp(20), 0);
            box.AddView(input);
            new AlertDialog.Builder(this)
                .SetTitle("Trigger level (volts)")
                .SetView(box)
                .SetPositiveButton("OK", (s, e) =>
                {
                    double volts;
                    if (double.TryParse(input.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out volts) &&
                        Math.Abs(volts) <= 200)
                        Change(() => settings.TriggerLevel = volts);
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Show();
        }

        void LoadStored()
        {
            ISharedPreferences prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private);
            foreach (KeyValuePair<string, object> kv in prefs.All)
                if (kv.Value != null)
                    stored[kv.Key] = kv.Value.ToString();
        }

        void RequestSave()
        {
            saveDue = true;
            saveAt = DateTime.UtcNow.AddSeconds(2);
            if (!saveScheduled)
            {
                saveScheduled = true;
                ui.PostDelayed(SaveTick, 2100);
            }
        }

        void SaveTick()
        {
            saveScheduled = false;
            if (!saveDue || destroyed)
                return;
            if (DateTime.UtcNow >= saveAt)
                SaveNow();
            else
                RequestSave();
        }

        void SaveNow()
        {
            saveDue = false;
            settings.Save(stored);
            calibration.Save(stored);
            ISharedPreferencesEditor editor = GetSharedPreferences(PrefsName, FileCreationMode.Private).Edit();
            foreach (KeyValuePair<string, string> kv in stored)
                editor.PutString(kv.Key, kv.Value);
            editor.Apply();
        }

        // ---- engine events ------------------------------------------------------------------

        // Engine thread: measure there, then let the UI thread show only the newest frame.
        void OnFrame(Frame f)
        {
            Analysis a = Analysis.Of(f, f.Settings);
            lock (frameLock)
            {
                pendingFrame = f;
                pendingAnalysis = a;
            }
            if (Interlocked.Exchange(ref framePosted, 1) == 0)
                Post(ShowPendingFrame);
        }

        void ShowPendingFrame()
        {
            Interlocked.Exchange(ref framePosted, 0);
            Frame f;
            Analysis a;
            lock (frameLock)
            {
                f = pendingFrame;
                a = pendingAnalysis;
                pendingFrame = null;
            }
            if (f == null || engine.Status == AcqStatus.NoDevice)
                return;
            shownFrame = f;
            Renderer.Frame = f;
            Renderer.Analysis = a;
            view.Invalidate();
        }

        void OnEngineStatus(AcqStatus status, string text)
        {
            Renderer.Status = status;
            Renderer.SourceName = engine.SourceDescription;
            if (text != null && (status == AcqStatus.NoDevice || status == AcqStatus.Connecting))
                Renderer.Message = text;
            if (status == AcqStatus.NoDevice)
            {
                Renderer.Frame = null;
                shownFrame = null;
            }
            demoRow.Visibility = engine.HardwareConnected ? ViewStates.Gone : ViewStates.Visible;
            UpdateRunButton();
            view.Invalidate();
        }

        void OnUsbLog(string line)
        {
            Post(() => AddLog(line));
        }

        void SetRunning(bool run)
        {
            engine.SetRunning(run);
            UpdateRunButton();
        }

        void UpdateRunButton()
        {
            if (engine == null)
                return;
            bool run = engine.Running;
            runButton.Text = run ? "RUNNING" : "STOPPED";
            UiKit.Style(runButton, run ? Palette.Run : Palette.Stop, Palette.White);
        }

        void SetDemo(bool demo)
        {
            engine.SetDemo(demo);
            shownFrame = null;
            Renderer.Frame = null;
            SyncControls();
            view.Invalidate();
        }

        void AddLog(string line)
        {
            logLines.Add(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line);
            if (logLines.Count > 500)
                logLines.RemoveRange(0, logLines.Count - 500);
        }

        // A tap on the screen while there is no scope asks for USB permission again (after "Deny").
        void OnScreenTapped()
        {
            if (engine.Status != AcqStatus.NoDevice || engine.IsDemo)
                return;
            AndroidUsb.ForgetRequests();
            engine.UpdateSettings(settings);
        }

        // ---- actions ------------------------------------------------------------------------

        // Long press on the screen: things to do at that point.
        void ShowScreenMenu(float x, float y)
        {
            double frac = Renderer.FractionAtX(x), div = Renderer.DivsAtY(y);
            int trace = Renderer.TraceNear(x, y);
            bool timeDomain = !settings.FftActive && !settings.XYActive;
            var items = new List<string>();
            var actions = new List<Action>();
            if (timeDomain)
            {
                items.Add("Trigger level here");
                actions.Add(() => Change(() => ScreenActions.TriggerLevelAt(settings, div)));
                items.Add("Trigger point here");
                actions.Add(() => Change(() => ScreenActions.TriggerPointAt(settings, frac)));
                items.Add("Time cursors here");
                actions.Add(() => Change(() => ScreenActions.TimeCursorsAt(settings, frac)));
                items.Add("Level cursors here");
                actions.Add(() => Change(() => ScreenActions.LevelCursorsAt(settings, div, trace)));
            }
            if (settings.Cursors != CursorMode.Off)
            {
                items.Add("Cursors off");
                actions.Add(() => Change(() => settings.Cursors = CursorMode.Off));
            }
            items.Add("Autoset");
            actions.Add(Autoset);
            items.Add(engine.Running ? "Stop" : "Run");
            actions.Add(() => SetRunning(!engine.Running));
            new AlertDialog.Builder(this)
                .SetItems(items.ToArray(), (s, e) => actions[e.Which]())
                .Show();
        }

        void Autoset()
        {
            if (engine.SourceDescription == null)
            {
                Toast.MakeText(this, "Autoset needs the scope (or the demo signal).", ToastLength.Short).Show();
                return;
            }
            autosetButton.Enabled = false;
            engine.RequestAutoset((proposed, summary) => Post(() =>
            {
                autosetButton.Enabled = true;
                AddLog(summary);
                Toast.MakeText(this, summary, ToastLength.Long).Show();
                if (proposed == null)
                    return;
                Change(() => settings.CopyFrom(proposed));
                SetRunning(true);
            }));
        }

        void TriggerTo50()
        {
            Frame f = shownFrame;
            int ch = settings.TriggerSource;
            ChannelMeasurement m = f != null ? Measurements.Measure(f, ch) : null;
            if (m == null)
                return;
            double step = settings.Channels[ch].VoltsPerDiv / 50;
            Change(() => settings.TriggerLevel = Math.Round((m.Max + m.Min) / 2 / step) * step);
        }

        void ZeroCalibration()
        {
            if (engine.IsDemo || engine.SourceDescription == null)
            {
                ShowMessage("Zero calibration", "Connect the DDS-120 first (and turn off the demo signal).");
                return;
            }
            new AlertDialog.Builder(this)
                .SetTitle("Zero calibration")
                .SetMessage("Disconnect both probes from the inputs (or short the inputs to ground), then press OK.\n\n" +
                    "The ADC zero offset of both channels is measured at every gain and saved.")
                .SetPositiveButton("OK", (s, e) =>
                {
                    zeroButton.Enabled = false;
                    engine.RequestZeroCalibration(error => Post(() =>
                    {
                        zeroButton.Enabled = true;
                        if (error == null)
                        {
                            SaveNow();
                            ShowMessage("Zero calibration", "Zero calibration saved.");
                        }
                        else
                        {
                            ShowMessage("Zero calibration", error);
                        }
                    }));
                })
                .SetNegativeButton("Cancel", (s, e) => { })
                .Show();
        }

        void ShowMessage(string title, string text)
        {
            new AlertDialog.Builder(this).SetTitle(title).SetMessage(text).SetPositiveButton("OK", (s, e) => { }).Show();
        }

        void ShowLog()
        {
            string text = string.Join("\n", logLines);
            var tv = new TextView(this) { Text = text.Length > 0 ? text : "(empty)" };
            tv.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
            tv.SetTextSize(Android.Util.ComplexUnitType.Sp, 11);
            tv.SetTextIsSelectable(true);
            tv.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));
            var scroll = new ScrollView(this);
            scroll.AddView(tv);
            scroll.Post(() => scroll.FullScroll(FocusSearchDirection.Down));
            new AlertDialog.Builder(this)
                .SetTitle("Device log")
                .SetView(scroll)
                .SetPositiveButton("Close", (s, e) => { })
                .SetNeutralButton("Copy", (s, e) =>
                {
                    var clipboard = (Android.Content.ClipboardManager)GetSystemService(ClipboardService);
                    clipboard.PrimaryClip = ClipData.NewPlainText("DDS-120 log", text);
                    Toast.MakeText(this, "Log copied", ToastLength.Short).Show();
                })
                .Show();
        }

        // The APK embeds GPL firmware, so the notice travels with the app.
        void ShowAbout()
        {
            string version = typeof(MainActivity).Assembly.GetName().Version.ToString(3);
            ShowMessage("DDS-120 Scope " + version,
                "Oscilloscope app for the SainSmart DDS-120 over USB OTG.\n\n" +
                "Scope firmware: on connect the app loads dds120-firmware.hex into the scope's RAM " +
                "(the scope's EEPROM is never modified). It is the SainSmart DDS120 firmware of the " +
                "OpenHantek6022 project (https://github.com/OpenHantek/OpenHantek6022), derived from " +
                "sigrok-firmware-fx2lafw (https://sigrok.org), licensed under the GNU General Public " +
                "License, version 2 or later. This app uploads it unmodified.");
        }
    }
}
