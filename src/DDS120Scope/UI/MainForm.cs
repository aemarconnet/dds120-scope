using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Dds120.Display;
using Dds120.Scope;
using Dds120.Util;

namespace Dds120.UI
{
    internal sealed class MainForm : Form
    {
        const int PanelWidth = 300;
        static readonly string[] TabNames = { "Acquire", "Math", "Cursors", "Display", "Tools" };

        readonly Dictionary<string, string> stored;
        readonly ScopeSettings settings = new ScopeSettings();
        readonly Calibration calibration = new Calibration();
        readonly AcquisitionEngine engine;
        readonly ScopeView view = new ScopeView();
        readonly Timer uiTimer = new Timer { Interval = 30 };
        readonly List<string> logLines = new List<string>();
        volatile Frame pendingFrame;
        Frame shownFrame;
        bool syncing;
        bool saveDue;
        DateTime saveAt;

        Button runButton, singleButton, zeroButton, autosetButton, forceButton;
        ComboBox acquireCombo, averageCombo, modeCombo, timebaseCombo, sourceCombo, edgeCombo, trigCouplingCombo, calCombo;
        ComboBox mathCombo, mathScaleCombo, fftSourceCombo, fftWindowCombo, fftZoomCombo;
        ComboBox cursorCombo, cursorSourceCombo, formatCombo, drawCombo, persistCombo;
        NumericUpDown levelNum, trigPosNum, mathPosNum;
        readonly CheckBox[] enableCheck = new CheckBox[2];
        readonly CheckBox[] invertCheck = new CheckBox[2];
        readonly ComboBox[] vdivCombo = new ComboBox[2];
        readonly ComboBox[] couplingCombo = new ComboBox[2];
        readonly ComboBox[] probeCombo = new ComboBox[2];
        readonly NumericUpDown[] positionNum = new NumericUpDown[2];
        CheckBox demoCheck;
        ToolStripStatusLabel statusLabel, pathLabel;

        Button[] tabButtons;
        readonly Control[] pages = new Control[TabNames.Length];
        Label averageLabel, mathScaleLabel, fftOfLabel, fftSpanLabel;
        Control mathScaleRow, fftOfRow;

        ScopeRenderer Renderer { get { return view.Renderer; } }

        public MainForm(Dictionary<string, string> stored)
        {
            this.stored = stored;
            settings.Load(stored);
            calibration.Load(stored);
            string v;
            // The demo signal always starts off, so a missing scope is reported instead of hidden.
            const bool demo = false;
            stored.Remove("demo");
            bool keepStockId = !(stored.TryGetValue("usb.keepstockid", out v) && v == "0");
            int tab;
            if (!stored.TryGetValue("ui.tab", out v) || !int.TryParse(v, out tab) || tab < 0 || tab >= TabNames.Length)
                tab = 0;

            BuildUi();
            syncing = true;
            demoCheck.Checked = demo;
            syncing = false;
            UiKit.SelectTab(tabButtons, pages, tab);
            SyncControls();

            engine = new AcquisitionEngine(settings, calibration, demo);
            engine.SetKeepStockIdentity(keepStockId);
            engine.FrameReady += f => pendingFrame = f;
            engine.StatusChanged += (status, text) => Post(() => OnEngineStatus(status, text));
            engine.Log += line => Post(() => AddLog(line));

            uiTimer.Tick += OnUiTick;
        }

        void Post(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(a);
            }
            catch (InvalidOperationException)
            {
                // window is closing
            }
        }

        // ---- layout -------------------------------------------------------------------------

        // Side panel, top to bottom: the run buttons (always visible), vertical as a CH1 | CH2
        // table, horizontal, trigger, then tabs for the settings changed less often.
        void BuildUi()
        {
            SuspendLayout();
            Text = "DDS-120 Scope";
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = UiKit.Normal;
            ClientSize = new Size(1320, 800);
            MinimumSize = new Size(960, 640);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            Icon = AppIcon.Create();

            var side = new Panel { Dock = DockStyle.Right, Width = PanelWidth, BackColor = Theme.Window };
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Window, Padding = new Padding(6, 0, 6, 6) };
            var flow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Window,
                Margin = new Padding(0),
            };
            flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroller.Controls.Add(flow);

            var actions = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6, 6, 6, 0),
                BackColor = Theme.Window,
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            runButton = UiKit.Button("RUN");
            runButton.Click += (s, e) => SetRunning(!engine.Running);
            singleButton = UiKit.Button("SINGLE");
            singleButton.Click += (s, e) => { engine.Single(); UpdateRunButton(); };
            autosetButton = UiKit.Button("AUTOSET", Theme.Accent);
            autosetButton.Click += (s, e) => Autoset();
            forceButton = UiKit.Button("FORCE TRIG");
            forceButton.Click += (s, e) => engine.ForceTrigger();
            Button[] grid = { runButton, singleButton, autosetButton, forceButton };
            for (int i = 0; i < grid.Length; i++)
            {
                grid[i].Height = 32;
                grid[i].Margin = new Padding(i % 2 == 0 ? 0 : 3, 2, i % 2 == 0 ? 3 : 0, 2);
                actions.Controls.Add(grid[i], i % 2, i / 2);
            }
            side.Controls.Add(scroller);
            side.Controls.Add(actions);

            // Vertical: one table, a column per channel
            TableLayoutPanel t = UiKit.Section(flow, "Vertical", Theme.TextDim, 3);
            for (int ch = 0; ch < 2; ch++)
                BuildChannel(ch);
            UiKit.Row3(t, "Channel", enableCheck[0], enableCheck[1]);
            UiKit.Row3(t, "Volts/div", vdivCombo[0], vdivCombo[1]);
            UiKit.Row3(t, "Coupling", couplingCombo[0], couplingCombo[1]);
            UiKit.Row3(t, "Probe", probeCombo[0], probeCombo[1]);
            UiKit.Row3(t, "Position", positionNum[0], positionNum[1]);
            UiKit.Row3(t, "Invert", invertCheck[0], invertCheck[1]);

            // Horizontal
            t = UiKit.Section(flow, "Horizontal", Theme.Accent);
            var tbItems = new List<string>();
            foreach (double tb in ScopeSettings.Timebases)
                tbItems.Add(Si.Short(tb, "s") + "/div");
            timebaseCombo = UiKit.Combo(tbItems.ToArray());
            timebaseCombo.SelectedIndexChanged += (s, e) => Change(() => settings.TimebaseIndex = timebaseCombo.SelectedIndex);
            UiKit.Row(t, "Time/div", timebaseCombo);
            trigPosNum = UiKit.Number(0, 100, 0, 5);
            trigPosNum.ValueChanged += (s, e) => Change(() => settings.TriggerPosition = (double)trigPosNum.Value / 100);
            UiKit.Row(t, "Trigger at %", trigPosNum);

            // Trigger
            t = UiKit.Section(flow, "Trigger", Theme.Accent);
            sourceCombo = UiKit.Combo("CH1", "CH2");
            sourceCombo.SelectedIndexChanged += (s, e) => Change(() => settings.TriggerSource = sourceCombo.SelectedIndex);
            edgeCombo = UiKit.Combo("Rise ↑", "Fall ↓");
            edgeCombo.SelectedIndexChanged += (s, e) => Change(() => settings.TriggerEdge = (TriggerEdge)edgeCombo.SelectedIndex);
            UiKit.Row(t, "Source/edge", UiKit.Pair(sourceCombo, edgeCombo));
            modeCombo = UiKit.Combo("Auto", "Normal");
            modeCombo.SelectedIndexChanged += (s, e) => Change(() => settings.TriggerMode = (TriggerMode)modeCombo.SelectedIndex);
            UiKit.Row(t, "Mode", modeCombo);
            trigCouplingCombo = UiKit.Combo("DC", "AC", "Noise reject", "HF reject", "LF reject");
            trigCouplingCombo.SelectedIndexChanged += (s, e) => Change(() => settings.TriggerCoupling = (TriggerCoupling)trigCouplingCombo.SelectedIndex);
            UiKit.Row(t, "Coupling", trigCouplingCombo);
            levelNum = UiKit.Number(-200, 200, 3, 0.05m);
            levelNum.ValueChanged += (s, e) => Change(() => settings.TriggerLevel = (double)levelNum.Value);
            var fifty = UiKit.Button("50 %");
            fifty.Click += (s, e) => TriggerTo50();
            UiKit.Row(t, "Level (V)", UiKit.Pair(levelNum, fifty));

            // Tabs for everything else
            tabButtons = UiKit.Tabs(flow, TabNames, i =>
            {
                UiKit.SelectTab(tabButtons, pages, i);
                stored["ui.tab"] = i.ToString(CultureInfo.InvariantCulture);
                RequestSave();
            });

            // Acquire
            t = UiKit.Page(flow);
            pages[0] = t;
            acquireCombo = UiKit.Combo("Sample", "Peak detect", "Hi-Res", "Average");
            acquireCombo.SelectedIndexChanged += (s, e) => Change(() => settings.Acquire = (AcquireMode)acquireCombo.SelectedIndex);
            UiKit.Row(t, "Acquire", acquireCombo);
            averageCombo = UiKit.Combo(Array.ConvertAll(ScopeSettings.AverageCounts, n => n + " waveforms"));
            averageCombo.SelectedIndexChanged += (s, e) => Change(() => settings.AverageCount = ScopeSettings.AverageCounts[averageCombo.SelectedIndex]);
            averageLabel = UiKit.Row(t, "Average", averageCombo);

            // Math
            t = UiKit.Page(flow);
            pages[1] = t;
            mathCombo = UiKit.Combo("Off", "CH1 + CH2", "CH1 − CH2", "CH1 × CH2", "FFT");
            mathCombo.SelectedIndexChanged += (s, e) => Change(() => settings.MathMode = (MathFunction)mathCombo.SelectedIndex);
            UiKit.Row(t, "Function", mathCombo);
            mathScaleCombo = UiKit.Combo();
            mathScaleCombo.SelectedIndexChanged += (s, e) => Change(() => settings.MathScaleIndex = mathScaleCombo.SelectedIndex);
            mathPosNum = UiKit.Number(-4, 4, 1, 0.1m);
            mathPosNum.ValueChanged += (s, e) => Change(() => settings.MathPosition = (double)mathPosNum.Value);
            mathScaleRow = UiKit.Pair(mathScaleCombo, mathPosNum);
            mathScaleLabel = UiKit.Row(t, "Scale/pos", mathScaleRow);
            fftSourceCombo = UiKit.Combo("CH1", "CH2");
            fftSourceCombo.SelectedIndexChanged += (s, e) => Change(() => settings.FftSource = fftSourceCombo.SelectedIndex);
            fftWindowCombo = UiKit.Combo("Rectangular", "Hann", "Blackman", "Flat top");
            fftWindowCombo.SelectedIndexChanged += (s, e) => Change(() => settings.FftWindow = (FftWindow)fftWindowCombo.SelectedIndex);
            fftOfRow = UiKit.Pair(fftSourceCombo, fftWindowCombo);
            fftOfLabel = UiKit.Row(t, "FFT of", fftOfRow);
            fftZoomCombo = UiKit.Combo(Array.ConvertAll(ScopeSettings.FftZooms, z => "Zoom ×" + z));
            fftZoomCombo.SelectedIndexChanged += (s, e) => Change(() => settings.FftZoom = ScopeSettings.FftZooms[fftZoomCombo.SelectedIndex]);
            fftSpanLabel = UiKit.Row(t, "FFT span", fftZoomCombo);

            // Cursors
            t = UiKit.Page(flow);
            pages[2] = t;
            cursorCombo = UiKit.Combo("Off", "Time", "Amplitude", "Both");
            cursorCombo.SelectedIndexChanged += (s, e) => Change(() => settings.Cursors = (CursorMode)cursorCombo.SelectedIndex);
            UiKit.Row(t, "Cursors", cursorCombo);
            cursorSourceCombo = UiKit.Combo("CH1", "CH2", "Math");
            cursorSourceCombo.SelectedIndexChanged += (s, e) => Change(() => settings.CursorSource = cursorSourceCombo.SelectedIndex);
            UiKit.Row(t, "Source", cursorSourceCombo);

            // Display
            t = UiKit.Page(flow);
            pages[3] = t;
            formatCombo = UiKit.Combo("YT", "XY");
            formatCombo.SelectedIndexChanged += (s, e) => Change(() => settings.XY = formatCombo.SelectedIndex == 1);
            drawCombo = UiKit.Combo("Vectors", "Dots");
            drawCombo.SelectedIndexChanged += (s, e) => Change(() => settings.Dots = drawCombo.SelectedIndex == 1);
            UiKit.Row(t, "Format", UiKit.Pair(formatCombo, drawCombo));
            persistCombo = UiKit.Combo(Array.ConvertAll(ScopeSettings.PersistenceTimes,
                p => p == 0 ? "Off" : double.IsInfinity(p) ? "Infinite" : Si.Short(p, "s")));
            persistCombo.SelectedIndexChanged += (s, e) => Change(() => settings.PersistenceIndex = persistCombo.SelectedIndex);
            UiKit.Row(t, "Persistence", persistCombo);
            var measureButton = UiKit.Button("Measurements...");
            measureButton.Click += (s, e) => PickMeasurements();
            UiKit.Wide(t, measureButton);

            // Tools
            t = UiKit.Page(flow);
            pages[4] = t;
            var calItems = new List<string>();
            foreach (int hz in Dds120Model.CalFrequencies)
                calItems.Add(Si.Short(hz, "Hz"));
            calCombo = UiKit.Combo(calItems.ToArray());
            calCombo.SelectedIndexChanged += (s, e) => Change(() => settings.CalFrequency = Dds120Model.CalFrequencies[calCombo.SelectedIndex]);
            UiKit.Row(t, "Cal output", calCombo);
            zeroButton = UiKit.Button("Zero calibration...");
            zeroButton.Click += (s, e) => ZeroCalibration();
            UiKit.Wide(t, zeroButton);
            var csv = UiKit.Button("Save CSV...");
            csv.Click += (s, e) => SaveCsv();
            var png = UiKit.Button("Screenshot...");
            png.Click += (s, e) => SaveScreenshot();
            UiKit.Wide(t, UiKit.Pair(csv, png));
            var logButton = UiKit.Button("Device log");
            logButton.Click += (s, e) => ShowLog();
            var aboutButton = UiKit.Button("About");
            aboutButton.Click += (s, e) => ShowAbout();
            UiKit.Wide(t, UiKit.Pair(logButton, aboutButton));
            demoCheck = UiKit.Check("Demo signal (no hardware)");
            demoCheck.CheckedChanged += (s, e) =>
            {
                if (syncing) return;
                engine.SetDemo(demoCheck.Checked);
                shownFrame = null;
                Renderer.Frame = null;
            };
            UiKit.Wide(t, demoCheck);

            var status = new StatusStrip { BackColor = Theme.PanelHeader, ForeColor = Theme.TextDim, SizingGrip = false, Renderer = new ToolStripProfessionalRenderer(new DarkColors()) };
            statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text };
            pathLabel = new ToolStripStatusLabel { ForeColor = Theme.TextDim, Text = "F5 run/stop   F6 single   F7 autoset   F8 force   wheel: time/div   Shift+wheel: V/div" };
            status.Items.Add(statusLabel);
            status.Items.Add(pathLabel);

            view.Dock = DockStyle.Fill;
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
            view.TimebaseWheel += step => Change(() =>
                settings.TimebaseIndex = Math.Max(0, Math.Min(ScopeSettings.Timebases.Length - 1, settings.TimebaseIndex + step)));
            view.VoltsWheel += (trace, step) => Change(() => ScreenActions.StepVolts(settings, trace, step));
            view.PannedSideways += divs => Change(() => ScreenActions.PanHorizontal(settings, divs));
            view.PannedVertically += (trace, divs) => Change(() => ScreenActions.PanVertical(settings, trace, divs));
            view.DoubleClicked += () => SetRunning(!engine.Running);
            view.MenuRequested += ShowScreenMenu;

            Controls.Add(view);
            Controls.Add(side);
            Controls.Add(status);

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
        }

        void BuildChannel(int ch)
        {
            enableCheck[ch] = UiKit.Check("CH" + (ch + 1));
            enableCheck[ch].ForeColor = Theme.Channel[ch];
            enableCheck[ch].Font = UiKit.Bold;
            enableCheck[ch].CheckedChanged += (s, e) => Change(() =>
            {
                settings.Channels[ch].Enabled = enableCheck[ch].Checked;
                if (!settings.Channels[0].Enabled && !settings.Channels[1].Enabled)
                    settings.Channels[1 - ch].Enabled = true; // keep at least one channel
            });
            vdivCombo[ch] = UiKit.Combo();
            vdivCombo[ch].SelectedIndexChanged += (s, e) => Change(() => settings.Channels[ch].GainIndex = vdivCombo[ch].SelectedIndex);
            couplingCombo[ch] = UiKit.Combo("DC", "AC");
            couplingCombo[ch].SelectedIndexChanged += (s, e) => Change(() => settings.Channels[ch].AcCoupling = couplingCombo[ch].SelectedIndex == 1);
            probeCombo[ch] = UiKit.Combo("x1", "x10");
            probeCombo[ch].SelectedIndexChanged += (s, e) => Change(() => settings.Channels[ch].Probe = probeCombo[ch].SelectedIndex == 1 ? 10 : 1);
            positionNum[ch] = UiKit.Number(-4, 4, 1, 0.1m);
            positionNum[ch].ValueChanged += (s, e) => Change(() => settings.Channels[ch].Position = (double)positionNum[ch].Value);
            invertCheck[ch] = UiKit.Check("");
            invertCheck[ch].CheckedChanged += (s, e) => Change(() => settings.Channels[ch].Inverted = invertCheck[ch].Checked);
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color StatusStripGradientBegin { get { return Theme.PanelHeader; } }
            public override Color StatusStripGradientEnd { get { return Theme.PanelHeader; } }
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

        static void SetItems(ComboBox combo, List<string> labels)
        {
            bool same = combo.Items.Count == labels.Count;
            for (int i = 0; same && i < labels.Count; i++)
                same = (string)combo.Items[i] == labels[i];
            if (!same)
            {
                combo.Items.Clear();
                combo.Items.AddRange(labels.ToArray());
            }
        }

        void SyncControls()
        {
            syncing = true;
            try
            {
                acquireCombo.SelectedIndex = (int)settings.Acquire;
                averageCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ScopeSettings.AverageCounts, settings.AverageCount));
                UiKit.ShowRow(averageLabel, averageCombo, settings.Acquire == AcquireMode.Average);
                modeCombo.SelectedIndex = (int)settings.TriggerMode;
                trigCouplingCombo.SelectedIndex = (int)settings.TriggerCoupling;
                timebaseCombo.SelectedIndex = settings.TimebaseIndex;
                trigPosNum.Value = (decimal)Math.Round(settings.TriggerPosition * 100);
                sourceCombo.SelectedIndex = settings.TriggerSource;
                edgeCombo.SelectedIndex = (int)settings.TriggerEdge;
                levelNum.Value = Clamp((decimal)Math.Round(settings.TriggerLevel, 3), levelNum);
                levelNum.Increment = (decimal)(settings.Channels[settings.TriggerSource].VoltsPerDiv / 10);
                calCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Dds120Model.CalFrequencies, settings.CalFrequency));
                for (int ch = 0; ch < 2; ch++)
                {
                    ChannelSettings c = settings.Channels[ch];
                    enableCheck[ch].Checked = c.Enabled;
                    invertCheck[ch].Checked = c.Inverted;
                    var labels = new List<string>();
                    foreach (GainStep g in Dds120Model.Gains)
                        labels.Add(Si.Short(g.VoltsPerDiv * c.Probe, "V"));
                    SetItems(vdivCombo[ch], labels);
                    vdivCombo[ch].SelectedIndex = c.GainIndex;
                    couplingCombo[ch].SelectedIndex = c.AcCoupling ? 1 : 0;
                    probeCombo[ch].SelectedIndex = c.Probe == 10 ? 1 : 0;
                    positionNum[ch].Value = Clamp((decimal)Math.Round(c.Position, 1), positionNum[ch]);
                    vdivCombo[ch].Enabled = couplingCombo[ch].Enabled = probeCombo[ch].Enabled =
                        positionNum[ch].Enabled = invertCheck[ch].Enabled = c.Enabled;
                }

                mathCombo.SelectedIndex = (int)settings.MathMode;
                var scales = new List<string>();
                foreach (double sc in ScopeSettings.MathScales)
                    scales.Add(Si.Short(sc, settings.MathUnit) + "/div");
                SetItems(mathScaleCombo, scales);
                mathScaleCombo.SelectedIndex = settings.MathScaleIndex;
                mathPosNum.Value = Clamp((decimal)Math.Round(settings.MathPosition, 1), mathPosNum);
                bool wave = settings.MathMode != MathFunction.Off && settings.MathMode != MathFunction.Fft;
                bool fft = settings.MathMode == MathFunction.Fft;
                UiKit.ShowRow(mathScaleLabel, mathScaleRow, wave);
                fftSourceCombo.SelectedIndex = settings.FftSource;
                fftWindowCombo.SelectedIndex = (int)settings.FftWindow;
                fftZoomCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ScopeSettings.FftZooms, settings.FftZoom));
                UiKit.ShowRow(fftOfLabel, fftOfRow, fft);
                UiKit.ShowRow(fftSpanLabel, fftZoomCombo, fft);

                cursorCombo.SelectedIndex = (int)settings.Cursors;
                cursorSourceCombo.SelectedIndex = settings.CursorSource;
                cursorSourceCombo.Enabled = settings.Cursors != CursorMode.Off;
                formatCombo.SelectedIndex = settings.XY ? 1 : 0;
                drawCombo.SelectedIndex = settings.Dots ? 1 : 0;
                persistCombo.SelectedIndex = settings.PersistenceIndex;
                // The demo signal is only offered while no scope is connected (or the demo is running).
                demoCheck.Visible = engine == null || !engine.HardwareConnected;
            }
            finally
            {
                syncing = false;
            }
        }

        static decimal Clamp(decimal v, NumericUpDown n)
        {
            return v < n.Minimum ? n.Minimum : v > n.Maximum ? n.Maximum : v;
        }

        void RequestSave()
        {
            saveDue = true;
            saveAt = DateTime.UtcNow.AddSeconds(2);
        }

        void SaveNow()
        {
            saveDue = false;
            settings.Save(stored);
            calibration.Save(stored);
            SettingsFile.Save(stored);
        }

        // ---- engine events ------------------------------------------------------------------

        void OnUiTick(object sender, EventArgs e)
        {
            Frame f = pendingFrame;
            if (f != null && f != shownFrame)
            {
                shownFrame = f;
                Renderer.Frame = f;
                Renderer.Analysis = Analysis.Of(f, settings);
                view.Invalidate();
            }
            if (saveDue && DateTime.UtcNow >= saveAt)
                SaveNow();
        }

        void OnEngineStatus(AcqStatus status, string text)
        {
            Renderer.Status = status;
            Renderer.SourceName = engine.SourceDescription;
            if (text != null)
            {
                statusLabel.Text = text;
                if (status == AcqStatus.NoDevice || status == AcqStatus.Connecting)
                    Renderer.Message = text;
            }
            if (status == AcqStatus.NoDevice)
            {
                Renderer.Frame = null;
                shownFrame = null;
            }
            demoCheck.Visible = !engine.HardwareConnected;
            UpdateRunButton();
            view.Invalidate();
        }

        void SetRunning(bool run)
        {
            engine.SetRunning(run);
            UpdateRunButton();
        }

        void UpdateRunButton()
        {
            bool run = engine.Running;
            runButton.Text = run ? "RUNNING" : "STOPPED";
            runButton.BackColor = run ? Theme.Run : Theme.Stop;
        }

        void AddLog(string line)
        {
            logLines.Add(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line);
            if (logLines.Count > 500)
                logLines.RemoveRange(0, logLines.Count - 500);
            statusLabel.Text = line;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateRunButton();
            engine.Start();
            uiTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            uiTimer.Stop();
            engine.Dispose();
            SaveNow();
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F5:
                    SetRunning(!engine.Running);
                    return true;
                case Keys.F6:
                    engine.Single();
                    UpdateRunButton();
                    return true;
                case Keys.F7:
                    Autoset();
                    return true;
                case Keys.F8:
                    engine.ForceTrigger();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- actions ------------------------------------------------------------------------

        // Right-click on the screen: things to do at that point.
        void ShowScreenMenu(int x, int y)
        {
            double frac = Renderer.FractionAtX(x), div = Renderer.DivsAtY(y);
            int trace = Renderer.TraceNear(x, y);
            var menu = new ContextMenuStrip { ShowImageMargin = false, Font = UiKit.Normal };
            if (!settings.FftActive && !settings.XYActive)
            {
                menu.Items.Add("Trigger level here", null, (s, e) => Change(() => ScreenActions.TriggerLevelAt(settings, div)));
                menu.Items.Add("Trigger point here", null, (s, e) => Change(() => ScreenActions.TriggerPointAt(settings, frac)));
                menu.Items.Add("Time cursors here", null, (s, e) => Change(() => ScreenActions.TimeCursorsAt(settings, frac)));
                menu.Items.Add("Level cursors here", null, (s, e) => Change(() => ScreenActions.LevelCursorsAt(settings, div, trace)));
            }
            if (settings.Cursors != CursorMode.Off)
                menu.Items.Add("Cursors off", null, (s, e) => Change(() => settings.Cursors = CursorMode.Off));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Autoset", null, (s, e) => Autoset());
            menu.Items.Add(engine.Running ? "Stop" : "Run", null, (s, e) => SetRunning(!engine.Running));
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(view, new Point(x, y));
        }

        void Autoset()
        {
            if (engine.SourceDescription == null)
            {
                statusLabel.Text = "Autoset needs the scope (or the demo signal).";
                return;
            }
            autosetButton.Enabled = false;
            engine.RequestAutoset((proposed, summary) => Post(() =>
            {
                autosetButton.Enabled = true;
                AddLog(summary);
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

        void PickMeasurements()
        {
            using (var form = new Form
            {
                Text = "Measurements",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(300, 560),
                BackColor = Theme.Window,
                ForeColor = Theme.Text,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                Icon = Icon,
            })
            {
                var types = (MeasureType[])Enum.GetValues(typeof(MeasureType));
                var list = new CheckedListBox
                {
                    Dock = DockStyle.Fill,
                    CheckOnClick = true,
                    BackColor = Theme.Panel,
                    ForeColor = Theme.Text,
                    BorderStyle = BorderStyle.None,
                    Font = UiKit.Normal,
                    IntegralHeight = false,
                };
                foreach (MeasureType m in types)
                    list.Items.Add(Measurements.LongName(m), Array.IndexOf(settings.MeasureItems, m) >= 0);
                var hint = UiKit.Label("Shown on each channel's line, in this order, as far as they fit. " +
                    "Phase and delay compare CH2 with CH1.", true);
                hint.AutoSize = false;
                hint.Dock = DockStyle.Top;
                hint.Height = 44;
                hint.Padding = new Padding(8, 4, 8, 0);
                var ok = UiKit.Button("OK");
                ok.Dock = DockStyle.Bottom;
                ok.Height = 34;
                ok.DialogResult = DialogResult.OK;
                form.Controls.Add(list);
                form.Controls.Add(hint);
                form.Controls.Add(ok);
                form.AcceptButton = ok;
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
                var items = new List<MeasureType>();
                for (int i = 0; i < types.Length; i++)
                    if (list.GetItemChecked(i))
                        items.Add(types[i]);
                Change(() => settings.MeasureItems = items.ToArray());
            }
        }

        void ZeroCalibration()
        {
            if (engine.IsDemo || engine.SourceDescription == null)
            {
                MessageBox.Show(this, "Connect the DDS-120 first (and turn off the demo signal).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this,
                    "Disconnect both probes from the inputs (or short the inputs to ground), then press OK.\n\n" +
                    "The ADC zero offset of both channels is measured at every gain and saved in the settings file.",
                    "Zero calibration", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;
            zeroButton.Enabled = false;
            engine.RequestZeroCalibration(error => Post(() =>
            {
                zeroButton.Enabled = true;
                if (error == null)
                {
                    SaveNow();
                    MessageBox.Show(this, "Zero calibration saved.", "Zero calibration", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, error, "Zero calibration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }));
        }

        void SaveCsv()
        {
            Frame f = shownFrame;
            if (f == null)
            {
                MessageBox.Show(this, "There is no capture to save yet.", Text);
                return;
            }
            Analysis a = Renderer.Analysis;
            float[] math = a != null && a.Frame == f ? a.MathWave : null;
            using (var dlg = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = "dds120-" + f.Time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine("# SainSmart DDS-120 capture " + f.Time.ToString("yyyy-MM-dd HH:mm:ss", inv));
                sb.AppendLine("# sample rate " + f.SampleRate.ToString("R", inv) + " S/s, " +
                    (f.Triggered ? "time 0 = trigger point" : "untriggered, time 0 = left screen edge"));
                for (int ch = 0; ch < 2; ch++)
                    if (f.Volts[ch] != null)
                    {
                        ChannelSettings c = f.Settings.Channels[ch];
                        sb.AppendLine(string.Format(inv, "# CH{0}: {1} V/div, {2}, probe x{3}", ch + 1, c.VoltsPerDiv, c.AcCoupling ? "AC" : "DC", c.Probe));
                    }
                if (math != null)
                    sb.AppendLine("# math: " + settings.MathName);
                sb.Append("time_s");
                for (int ch = 0; ch < 2; ch++)
                    if (f.Volts[ch] != null)
                        sb.Append(",ch" + (ch + 1) + "_V");
                if (math != null)
                    sb.Append(",math_" + (settings.MathUnit == "V" ? "V" : "V2"));
                sb.AppendLine();
                double zero = f.Triggered ? f.TriggerIndex : f.ScreenStart;
                for (int i = 0; i < f.SampleCount; i++)
                {
                    sb.Append(((i - zero) / f.SampleRate).ToString("G9", inv));
                    for (int ch = 0; ch < 2; ch++)
                        if (f.Volts[ch] != null)
                            sb.Append(',').Append(f.Volts[ch][i].ToString("G6", inv));
                    if (math != null)
                        sb.Append(',').Append(math[i].ToString("G6", inv));
                    sb.AppendLine();
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
                statusLabel.Text = "Saved " + f.SampleCount + " samples to " + dlg.FileName;
            }
        }

        void SaveScreenshot()
        {
            using (var bmp = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(bmp, new Rectangle(0, 0, view.Width, view.Height));
                using (var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = "dds120-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;
                    bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
                    statusLabel.Text = "Saved screenshot to " + dlg.FileName;
                }
            }
        }

        // The app ships GPL firmware, so the notice travels with it.
        void ShowAbout()
        {
            string version = typeof(MainForm).Assembly.GetName().Version.ToString(3);
            MessageBox.Show(this,
                "DDS-120 Scope " + version + "\n\n" +
                "Oscilloscope software for the SainSmart DDS-120 USB oscilloscope.\n\n" +
                "The app's own code is dedicated to the public domain under CC0 1.0 (LICENSE.txt).\n\n" +
                "On connect it loads the SainSmart DDS120 firmware of the OpenHantek6022 project, derived " +
                "from sigrok-firmware-fx2lafw, into the scope's RAM (the EEPROM is never changed). That " +
                "firmware is licensed under the GNU GPL, version 2 or later: see FIRMWARE-LICENSE.txt and " +
                "GPL-2.0.txt next to the app.\n\n" +
                "Not affiliated with SainSmart, OpenHantek6022 or sigrok.",
                "About DDS-120 Scope", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ShowLog()
        {
            var form = new Form
            {
                Text = "Device log",
                Size = new Size(760, 420),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Theme.Window,
                Icon = Icon,
            };
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Theme.Screen,
                ForeColor = Theme.Text,
                Font = new Font("Consolas", 9f),
                Text = string.Join(Environment.NewLine, logLines) + Environment.NewLine +
                       Environment.NewLine + "Settings file: " + SettingsFile.ActivePath,
            };
            form.Controls.Add(box);
            form.Shown += (s, e) => { box.SelectionStart = box.TextLength; box.ScrollToCaret(); };
            form.Show(this);
        }
    }
}
