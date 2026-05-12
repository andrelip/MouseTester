using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

using MouseTester.Diagnostics;
using OxyPlot;

namespace MouseTester
{
    using OxyPlot.Series;

    public partial class Form1 : Form
    {
        private MouseLog mlog = new MouseLog();
        enum state { idle, measure_wait, measure, collect_wait, collect, log, synthetic };
        private state test_state = state.idle;
        private long pFreq;

        private RecordingSession recording;
        private SyntheticGenerator synthGen;
        private System.Windows.Forms.Timer synthTimer;
        private long lastQpcWndProc;
        private int burstRun;

        public Form1()
        {
            InitializeComponent();

            this.Text = $"MouseTester v{Program.version}";

            // Pull the system timer to 1 ms for the entire lifetime of the app, not just during
            // recordings. This keeps the pre-flight check green at all times and makes any
            // ad-hoc latency observation (cold sampler, message pump) more accurate.
            // Reference-counted by Windows; matched by TimeEndPeriod(1) in OnFormClosed.
            try { NativeMethods.TimeBeginPeriod(1); } catch { }

            this.RegisterRawInputMouse(Handle);
            this.mlog.Cpi = UserSettings.LoadCpi(this.mlog.Cpi);
            this.textBoxDesc.Text = this.mlog.Desc.ToString();
            this.textBoxCPI.Text = this.mlog.Cpi.ToString();
            this.textBox1.Text = "Enter the correct CPI" +
                                 "\r\n        or\r\n" +
                                 "Press the Measure button" +
                                 "\r\n        or\r\n" +
                                 "Press the Load button";
            this.toolStripStatusLabel1.Text = "";
            this.KeyPreview = true;
            this.KeyDown += new KeyEventHandler(Form1_KeyDown);

            QueryPerformanceFrequency(out pFreq);
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F1)
            {
                buttonLog.PerformClick();
                e.Handled = true;
            }
            if (e.KeyCode == Keys.F2)
            {
                buttonLog.PerformClick();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F3)
            {
                buttonPlot.PerformClick();
                e.Handled = false;
            }
            else if (e.KeyCode == Keys.F4)
            {
                buttonPreflight.PerformClick();
                e.Handled = false;
            }
            else if (e.KeyCode == Keys.F5)
            {
                buttonSynthetic.PerformClick();
                e.Handled = false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            QueryPerformanceCounter(out long pCounter);
            if (m.Msg == WM_INPUT)
            {
                RAWINPUT raw = new RAWINPUT();
                uint size = (uint)Marshal.SizeOf(typeof(RAWINPUT));
                int outsize = GetRawInputData(m.LParam, RID_INPUT, out raw, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                if (outsize != -1)
                {
                    if (raw.header.dwType == RIM_TYPEMOUSE)
                    {
                        var ev = new MouseEvent(raw.data.mouse.buttonsStr.usButtonFlags, raw.data.mouse.lLastX, -(raw.data.mouse.lLastY), pCounter);
                        ev.diag = CaptureDiag(pCounter);
                        logMouseEvent(ev);
                    }
                }
            }
            base.WndProc(ref m);
        }

        private EventDiagnostics CaptureDiag(long pCounter)
        {
            var diag = new EventDiagnostics();
            diag.QpcAtWndProc = pCounter;

            try { diag.ProcessorNumber = NativeMethods.GetCurrentProcessorNumber(); } catch { }
            try
            {
                ulong cycles;
                if (NativeMethods.QueryThreadCycleTime(NativeMethods.GetCurrentThread(), out cycles))
                    diag.ThreadCycleTime = cycles;
            }
            catch { }

            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                diag.WindowHadFocus = fg == this.Handle;
                diag.WindowMinimized = NativeMethods.IsIconic(this.Handle);
            }
            catch { }

            // Burst detection: <100us between this event and the previous WM_INPUT
            long deltaQpc = lastQpcWndProc == 0 ? long.MaxValue : pCounter - lastQpcWndProc;
            double deltaUs = pFreq > 0 ? deltaQpc * 1e6 / pFreq : double.MaxValue;
            if (deltaUs < 100)
            {
                burstRun++;
                diag.BurstMember = true;
                diag.BurstIndex = burstRun;
            }
            else
            {
                burstRun = 0;
                diag.BurstMember = false;
                diag.BurstIndex = 0;
            }
            lastQpcWndProc = pCounter;
            diag.QpcSincePump = deltaQpc;

            return diag;
        }

        private void logMouseEvent(MouseEvent mevent)
        {
            if (this.test_state == state.idle)
            {
            }
            else if (this.test_state == state.measure_wait)
            {
                if (mevent.buttonflags == 0x0001)
                {
                    this.mlog.Add(mevent);
                    this.toolStripStatusLabel1.Text = "Measuring";
                    this.test_state = state.measure;
                }
            }
            else if (this.test_state == state.measure)
            {
                this.mlog.Add(mevent);
                if (mevent.buttonflags == 0x0002)
                {
                    double x = 0.0;
                    double y = 0.0;
                    foreach (MouseEvent e in this.mlog.Events)
                    {
                        x += (double)e.lastx;
                        y += (double)e.lasty;
                    }
                    tsCalc();
                    this.mlog.Cpi = Math.Round(Math.Sqrt((x * x) + (y * y)) / (10 / 2.54));
                    this.textBoxCPI.Text = this.mlog.Cpi.ToString();
                    UserSettings.SaveCpi(this.mlog.Cpi);
                    this.textBox1.Text = "Press the Collect or Log Start button\r\n";
                    this.toolStripStatusLabel1.Text = "";
                    this.test_state = state.idle;
                }
            }
            else if (this.test_state == state.collect_wait)
            {
                if (mevent.buttonflags == 0x0001)
                {
                    this.mlog.Add(mevent);
                    this.toolStripStatusLabel1.Text = "Collecting";
                    this.test_state = state.collect;
                }
            }
            else if (this.test_state == state.collect)
            {
                this.mlog.Add(mevent);
                if (mevent.buttonflags == 0x0002)
                {
                    FinalizeRecording();
                    tsCalc();
                    SetSummary();
                    this.test_state = state.idle;
                }
            }
            else if (this.test_state == state.log)
            {
                this.mlog.Add(mevent);
            }
        }

        private void buttonMeasure_Click(object sender, EventArgs e)
        {
            if (this.test_state == state.idle) {
                this.textBox1.Text = "1. Press and hold the left mouse button\r\n" +
                                     "2. Move the mouse 10 cm in a straight line\r\n" +
                                     "3. Release the left mouse button\r\n";
                this.toolStripStatusLabel1.Text = "Press the left mouse button";
                this.mlog.Clear();
                this.test_state = state.measure_wait;
            }
        }

        private void buttonCollect_Click(object sender, EventArgs e)
        {
            if (this.test_state == state.idle)
            {
                this.textBox1.Text = "1. Press and hold the left mouse button\r\n" +
                                     "2. Move the mouse\r\n" +
                                     "3. Release the left mouse button\r\n";
                this.toolStripStatusLabel1.Text = "Press the left mouse button";
                this.mlog.Clear();
                StartRecordingSession();
                this.test_state = state.collect_wait;
            }
        }

        private void buttonLog_Click(object sender, EventArgs e)
        {
            if (this.test_state == state.idle)
            {
                this.textBox1.Text = "1. Press the Log Stop button\r\n";
                this.toolStripStatusLabel1.Text = "Logging...";
                this.mlog.Clear();
                StartRecordingSession();
                this.test_state = state.log;
                buttonLog.Text = "Stop (F2)";
            }
            else if (this.test_state == state.log)
            {
                FinalizeRecording();
                tsCalc();
                SetSummary();
                this.test_state = state.idle;
                buttonLog.Text = "Start (F1)";
            }
        }

        private void StartRecordingSession()
        {
            try { recording?.Stop(); } catch { }
            recording = new RecordingSession();
            recording.Start(wantEtw: true);
            mlog.QpcFrequency = pFreq;
            mlog.EtwWasActive = recording.EtwActive;
            mlog.EtwError = recording.EtwError;
            mlog.TimerResolutionDuringRecording100ns = recording.TimerResolutionDuringRecording100ns;
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    mlog.RecordedAsAdmin = new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { mlog.RecordedAsAdmin = false; }
            burstRun = 0;
            lastQpcWndProc = 0;
        }

        private void FinalizeRecording()
        {
            if (recording == null) return;
            try
            {
                recording.Stop();
                if (recording.Cold != null)
                    mlog.ColdSamples.AddRange(recording.Cold.Samples);
                if (recording.Etw != null)
                {
                    mlog.EtwAnomalies.AddRange(recording.Etw.Anomalies);
                    mlog.TopDpcSources = new List<KeyValuePair<string, long>>(recording.Etw.TopDpcSources(10));
                    mlog.TopIsrSources = new List<KeyValuePair<string, long>>(recording.Etw.TopIsrSources(10));
                    mlog.TopDpcByTotalTime = new List<DriverDpcStats>(recording.Etw.TopDpcByTotalTime(10));
                    mlog.TopDpcByMaxLatency = new List<DriverDpcStats>(recording.Etw.TopDpcByMaxLatency(10));
                    mlog.TotalDpc = recording.Etw.TotalDpcCount;
                    mlog.TotalIsr = recording.Etw.TotalIsrCount;
                }

                tsCalc();
                mlog.Attributions = AttributionEngine.Attribute(new AttributionEngine.Inputs
                {
                    Events = mlog.Events,
                    Cold = mlog.ColdSamples,
                    Etw = mlog.EtwAnomalies,
                    QpcFrequency = pFreq,
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("FinalizeRecording: " + ex);
            }
            finally
            {
                recording = null;
            }
        }

        private void SetSummary()
        {
            string causes = "";
            if (mlog.Attributions != null && mlog.Attributions.Count > 0)
            {
                var top = mlog.Attributions
                    .GroupBy(a => a.PrimaryCause)
                    .OrderByDescending(g => g.Count())
                    .Take(3)
                    .Select(g => $"{g.Key} x{g.Count()}");
                causes = "\r\nJitter spikes: " + mlog.Attributions.Count +
                         " (top causes: " + string.Join(", ", top) + ")";

                // Drivers responsible for the most spikes (top per-spike DPC source aggregated).
                var spikeDrivers = mlog.Attributions
                    .Where(a => a.TopDpcSourcesInSpike != null && a.TopDpcSourcesInSpike.Count > 0)
                    .Select(a => a.TopDpcSourcesInSpike[0].Key)
                    .GroupBy(k => k)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => $"{g.Key} x{g.Count()}")
                    .ToList();
                if (spikeDrivers.Count > 0)
                    causes += "\r\nSpikes attributed to drivers: " + string.Join(", ", spikeDrivers);
            }
            string dpcLine = "";
            if (mlog.TopDpcSources != null && mlog.TopDpcSources.Count > 0)
            {
                var top5 = mlog.TopDpcSources.Take(5)
                    .Select(kv => $"{kv.Key} x{kv.Value}");
                dpcLine = "\r\nTotal DPCs: " + mlog.TotalDpc + " (by count: " + string.Join(", ", top5) + ")";
            }
            if (mlog.TopDpcByTotalTime != null && mlog.TopDpcByTotalTime.Count > 0)
            {
                var top5 = mlog.TopDpcByTotalTime.Take(5).Select(s =>
                    $"{s.Module}: {s.TotalMs:0.0}ms total, avg={s.AvgMs * 1000:0}µs, max={s.MaxMs * 1000:0}µs");
                dpcLine += "\r\nDPC time by driver:\r\n  " + string.Join("\r\n  ", top5);
            }
            if (mlog.TopDpcByMaxLatency != null && mlog.TopDpcByMaxLatency.Count > 0)
            {
                var top3 = mlog.TopDpcByMaxLatency.Take(3).Where(s => s.MaxMs > 0.05)
                    .Select(s => $"{s.Module} max={s.MaxMs * 1000:0}µs (avg={s.AvgMs * 1000:0}µs)");
                if (top3.Any())
                    dpcLine += "\r\nWorst-case DPC latency: " + string.Join(", ", top3);
            }
            if (mlog.TopIsrSources != null && mlog.TopIsrSources.Count > 0)
            {
                var top5 = mlog.TopIsrSources.Take(5)
                    .Select(kv => $"{kv.Key} x{kv.Value}");
                dpcLine += "\r\nTotal ISRs: " + mlog.TotalIsr + " (by count: " + string.Join(", ", top5) + ")";
            }
            string etwLine;
            if (mlog.EtwWasActive)
            {
                etwLine = "\r\nETW kernel diagnostics: ACTIVE";
            }
            else
            {
                string reason;
                if (!mlog.RecordedAsAdmin) reason = "process is not elevated";
                else if (!string.IsNullOrEmpty(mlog.EtwError)) reason = mlog.EtwError;
                else reason = "unknown — TraceEvent assembly may not be loaded";
                etwLine = "\r\nETW kernel diagnostics: not available — " + reason +
                          (mlog.RecordedAsAdmin ? " (running as admin: yes)" : " (running as admin: no)");
            }
            this.textBox1.Text = "Press the plot button to view data\r\n" +
                                 "        or\r\n" +
                                 "Press the save button to save log file\r\n" +
                                 "Events: " + this.mlog.Events.Count.ToString() + "\r\n" +
                                 "Sum X: " + this.mlog.deltaX().ToString() + " counts    " + Math.Abs(this.mlog.deltaX() / this.mlog.Cpi * 2.54).ToString("0.0") + " cm\r\n" +
                                 "Sum Y: " + this.mlog.deltaY().ToString() + " counts    " + Math.Abs(this.mlog.deltaY() / this.mlog.Cpi * 2.54).ToString("0.0") + " cm\r\n" +
                                 "Path: " + this.mlog.path().ToString("0") + " counts    " + (this.mlog.path() / this.mlog.Cpi * 2.54).ToString("0.0") + " cm" +
                                 etwLine + causes + dpcLine;
            this.toolStripStatusLabel1.Text = "";
        }

        private void buttonSynthetic_Click(object sender, EventArgs e)
        {
            if (this.test_state != state.idle && this.test_state != state.synthetic) return;

            if (this.test_state == state.synthetic)
            {
                StopSynthetic();
                return;
            }

            const int hz = 1000;
            const int durSec = 5;
            this.mlog.Clear();
            this.textBox1.Text = $"Synthetic self-test running...\r\nGenerating {hz} events/sec for {durSec}s.\r\nIf the resulting plot shows {hz} Hz with no spikes,\r\nthe analysis pipeline is working.";
            this.toolStripStatusLabel1.Text = $"Synthetic {hz}Hz...";
            this.test_state = state.synthetic;
            buttonSynthetic.Text = "Stop synthetic (F5)";
            DisableNonSyntheticButtons();
            StartRecordingSession();

            synthGen = new SyntheticGenerator();
            synthGen.OnComplete = (events) =>
            {
                this.BeginInvoke((Action)(() =>
                {
                    if (this.test_state == state.synthetic) StopSynthetic(events);
                }));
            };
            synthGen.Start(hz, durSec);

            // Hard timeout safety net.
            synthTimer = new System.Windows.Forms.Timer { Interval = (durSec + 2) * 1000 };
            synthTimer.Tick += (s, ev) =>
            {
                synthTimer.Stop();
                if (this.test_state == state.synthetic) StopSynthetic();
            };
            synthTimer.Start();
        }

        private void StopSynthetic(System.Collections.Generic.List<MouseEvent> preCollected = null)
        {
            try
            {
                synthTimer?.Stop();
                synthTimer?.Dispose();
                synthTimer = null;
            }
            catch { }

            var events = preCollected;
            try
            {
                if (synthGen != null)
                {
                    if (events == null) events = synthGen.SnapshotEvents();
                    synthGen.Stop();
                }
            }
            catch { }
            finally { synthGen = null; }

            if (events != null)
                foreach (var ev in events) this.mlog.Add(ev);

            FinalizeRecording();
            tsCalc();
            SetSummary();
            EnableAllButtons();
            buttonSynthetic.Text = "Synthetic 1 kHz Self-Test (F5, 5s)";
            this.test_state = state.idle;
        }

        private void DisableNonSyntheticButtons()
        {
            buttonMeasure.Enabled = false;
            buttonCollect.Enabled = false;
            buttonLog.Enabled = false;
            buttonLoad.Enabled = false;
            buttonSave.Enabled = false;
        }

        private void EnableAllButtons()
        {
            buttonMeasure.Enabled = true;
            buttonCollect.Enabled = true;
            buttonLog.Enabled = true;
            buttonLoad.Enabled = true;
            buttonSave.Enabled = true;
        }

        private void buttonPreflight_Click(object sender, EventArgs e)
        {
            IntPtr hwnd = this.Handle;
            var report = PreflightCheck.Run(hwnd);
            mlog.Trust = report;
            using (var dlg = new TrustReportForm(report, () => PreflightCheck.Run(hwnd)))
            {
                dlg.ShowDialog(this);
                mlog.Trust = report;
            }
        }

        private void buttonPlot_Click(object sender, EventArgs e)
        {
            if (this.mlog.Events.Count > 0 && this.test_state != state.log && this.test_state != state.collect_wait)
            {
                this.mlog.Desc = textBoxDesc.Text;
                MousePlot mousePlot = new MousePlot(this.mlog);
                mousePlot.Show();
            }
        }

        private void buttonLoad_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog1 = new OpenFileDialog();
            openFileDialog1.Filter = "CSV Files (*.csv)|*.csv|All Files(*.*)|*.*";
            openFileDialog1.FilterIndex = 1;
            openFileDialog1.Multiselect = false;
            if (openFileDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                this.mlog.Load(openFileDialog1.FileName);
            }
            this.textBox1.Text = "Events: " + this.mlog.Events.Count.ToString() + "\r\n" +
                                 "Sum X: " + this.mlog.deltaX().ToString() + " counts    " + Math.Abs(this.mlog.deltaX() / this.mlog.Cpi * 2.54).ToString("0.0") + " cm\r\n" +
                                 "Sum Y: " + this.mlog.deltaY().ToString() + " counts    " + Math.Abs(this.mlog.deltaY() / this.mlog.Cpi * 2.54).ToString("0.0") + " cm\r\n" +
                                 "Path: " + this.mlog.path().ToString("0") + " counts    " + (this.mlog.path() / this.mlog.Cpi * 2.54).ToString("0.0") + " cm";
            this.textBoxDesc.Text = this.mlog.Desc.ToString();
            this.textBoxCPI.Text = this.mlog.Cpi.ToString();
            if (this.mlog.Events.Count > 0)
            {
                MousePlot mousePlot = new MousePlot(this.mlog);
                mousePlot.Show();
            }
        }

        private void buttonSave_Click(object sender, EventArgs e)
        {
            SaveFileDialog saveFileDialog1 = new SaveFileDialog();
            saveFileDialog1.Filter = "CSV Files (*.csv)|*.csv|All Files(*.*)|*.*";
            saveFileDialog1.FilterIndex = 1;
            if (saveFileDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                this.mlog.Desc = textBoxDesc.Text;
                this.mlog.Save(saveFileDialog1.FileName);
            }
        }

        private void textBoxCPI_Validated(object sender, EventArgs e)
        {
            try
            {
                this.mlog.Cpi = double.Parse(this.textBoxCPI.Text);
                UserSettings.SaveCpi(this.mlog.Cpi);
            }
            catch
            {
                MessageBox.Show("Invalid CPI, resetting to previous value");
                this.textBoxCPI.Text = this.mlog.Cpi.ToString();
            }
            this.textBox1.Text = "Press the Collect or Log Start button\r\n";
        }

        private void tsCalc()
        {
            if (this.mlog.Events.Count == 0) return;
            long pcounter_min = this.mlog.Events[0].pcounter;
            foreach (MouseEvent me in this.mlog.Events)
            {
                me.ts = (me.pcounter - pcounter_min) * 1000.0 / pFreq;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { recording?.Stop(); } catch { }
            try { NativeMethods.TimeEndPeriod(1); } catch { }
            base.OnFormClosed(e);
        }
    }
}
