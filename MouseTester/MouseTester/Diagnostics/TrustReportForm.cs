using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MouseTester.Diagnostics
{
    public class TrustReportForm : Form
    {
        private readonly Func<TrustReport> rerun;
        private TrustReport report;

        private TextBox reportText;
        private ListView hookList;
        private Label hookLabel;
        private Button btnCloseSelected;
        private Button btnRefresh;
        private Button btnUsbDevices;
        private Button btnFocus;
        private Button btnOk;

        public TrustReportForm(TrustReport initialReport, Func<TrustReport> rerun)
        {
            this.report = initialReport;
            this.rerun = rerun;

            Text = "Pre-flight Trust Report";
            Width = 720;
            Height = 600;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 420);

            BuildUi();
            Render();
        }

        private void BuildUi()
        {
            reportText = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 9),
                Location = new Point(12, 12),
                Size = new Size(680, 220),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(reportText);

            hookLabel = new Label
            {
                Text = "Detected hook / overlay processes:",
                Location = new Point(12, 240),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
            };
            Controls.Add(hookLabel);

            hookList = new ListView
            {
                CheckBoxes = true,
                FullRowSelect = true,
                GridLines = true,
                View = View.Details,
                Location = new Point(12, 260),
                Size = new Size(680, 240),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            hookList.Columns.Add("PID", 60);
            hookList.Columns.Add("Process", 160);
            hookList.Columns.Add("Window", 220);
            hookList.Columns.Add("Reason", 240);
            Controls.Add(hookList);

            btnCloseSelected = new Button
            {
                Text = "Close Selected",
                Location = new Point(12, 510),
                Size = new Size(140, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnCloseSelected.Click += BtnCloseSelected_Click;
            Controls.Add(btnCloseSelected);

            btnRefresh = new Button
            {
                Text = "Re-scan",
                Location = new Point(160, 510),
                Size = new Size(100, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnRefresh.Click += BtnRefresh_Click;
            Controls.Add(btnRefresh);

            btnUsbDevices = new Button
            {
                Text = "USB Devices…",
                Location = new Point(268, 510),
                Size = new Size(140, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnUsbDevices.Click += (s, e) =>
            {
                using (var dlg = new UsbDevicesForm()) dlg.ShowDialog(this);
            };
            Controls.Add(btnUsbDevices);

            btnFocus = new Button
            {
                Text = "Focus MouseTester",
                Location = new Point(416, 510),
                Size = new Size(150, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnFocus.Click += BtnFocus_Click;
            Controls.Add(btnFocus);

            btnOk = new Button
            {
                Text = "OK",
                Location = new Point(600, 510),
                Size = new Size(92, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
            };
            btnOk.Click += (s, e) => Close();
            Controls.Add(btnOk);
            AcceptButton = btnOk;
        }

        private void Render()
        {
            Text = $"Pre-flight Trust Report — Score: {report.Score}/100";

            var sb = new StringBuilder();
            sb.AppendLine($"Trust score: {report.Score}/100");
            sb.AppendLine();
            foreach (var item in report.Items)
            {
                string sym = item.Status == TrustReportItem.Severity.Green ? "[OK] " :
                             item.Status == TrustReportItem.Severity.Yellow ? "[!]  " : "[X]  ";
                sb.AppendLine(sym + item.Title + ": " + item.Detail);
                if (!string.IsNullOrEmpty(item.Recommendation))
                    sb.AppendLine("       -> " + item.Recommendation);
            }
            reportText.Text = sb.ToString();

            hookList.BeginUpdate();
            hookList.Items.Clear();
            foreach (var h in report.DetectedHooks)
            {
                var item = new ListViewItem(h.Pid.ToString());
                item.SubItems.Add(h.Name ?? "");
                item.SubItems.Add(string.IsNullOrEmpty(h.WindowTitle) ? "(no window)" : h.WindowTitle);
                item.SubItems.Add(h.Reason ?? "");
                item.Checked = true;
                item.Tag = h;
                hookList.Items.Add(item);
            }
            hookList.EndUpdate();

            hookLabel.Text = report.DetectedHooks.Count == 0
                ? "Detected hook / overlay processes: none"
                : $"Detected hook / overlay processes ({report.DetectedHooks.Count}):";
            btnCloseSelected.Enabled = report.DetectedHooks.Count > 0;
        }

        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            if (rerun == null) return;
            report = rerun();
            Render();
        }

        private void BtnFocus_Click(object sender, EventArgs e)
        {
            var owner = this.Owner;
            if (owner == null) return;
            try
            {
                if (owner.WindowState == FormWindowState.Minimized)
                    NativeMethods.ShowWindow(owner.Handle, NativeMethods.SW_RESTORE);
                NativeMethods.BringWindowToTop(owner.Handle);
                NativeMethods.SetForegroundWindow(owner.Handle);
                // Close so the dialog doesn't immediately steal focus back; user can re-open
                // with F4 and the foreground check will now pass.
                Close();
            }
            catch { }
        }

        private void BtnCloseSelected_Click(object sender, EventArgs e)
        {
            var selected = hookList.CheckedItems.Cast<ListViewItem>()
                .Select(i => (HookProcessInfo)i.Tag)
                .ToList();

            if (selected.Count == 0)
            {
                MessageBox.Show(this, "No processes selected.", "Close Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var msg = "Close the following process(es)?\r\n\r\n" +
                      string.Join("\r\n", selected.Select(h => $"  PID {h.Pid}  {h.Name}".TrimEnd())) +
                      "\r\n\r\nMouseTester will first ask each process to close gracefully (1 second timeout), " +
                      "then force-kill any that ignore the request.";

            if (MessageBox.Show(this, msg, "Confirm Close", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            UseWaitCursor = true;
            btnCloseSelected.Enabled = false;
            btnRefresh.Enabled = false;

            int closed = 0, alive = 0;
            var details = new StringBuilder();
            try
            {
                foreach (var h in selected)
                {
                    var r = ProcessKiller.Kill(h.Pid, h.Name);
                    if (r.Closed) closed++; else alive++;
                    details.Append(ProcessKiller.Summarize(r));
                    details.AppendLine();
                }
            }
            finally
            {
                UseWaitCursor = false;
                btnCloseSelected.Enabled = true;
                btnRefresh.Enabled = true;
            }

            Thread.Sleep(250);

            string headline = $"Closed: {closed}\r\nStill alive: {alive}\r\n\r\nDetails:\r\n\r\n" + details.ToString();
            ShowScrollableResult(headline, alive > 0);

            if (rerun != null)
            {
                report = rerun();
                Render();
            }
        }

        private void ShowScrollableResult(string text, bool warning)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Close Selected — Result";
                dlg.Width = 720;
                dlg.Height = 480;
                dlg.StartPosition = FormStartPosition.CenterParent;
                var tb = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font(FontFamily.GenericMonospace, 9),
                    Dock = DockStyle.Fill,
                    Text = text,
                };
                var ok = new Button { Text = "OK", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK, Height = 32 };
                dlg.Controls.Add(tb);
                dlg.Controls.Add(ok);
                dlg.AcceptButton = ok;
                dlg.ShowDialog(this);
            }
        }
    }
}
