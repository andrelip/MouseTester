using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MouseTester.Diagnostics
{
    public class UsbDevicesForm : Form
    {
        private TreeView tree;
        private Label info;
        private Button btnDisable;
        private Button btnEnable;
        private Button btnRestart;
        private Button btnCloseApps;
        private Button btnRefresh;
        private Button btnClose;
        private CheckBox cbOnlyMouseController;
        private List<UsbTopology.UsbController> controllers;

        public UsbDevicesForm()
        {
            Text = "USB Devices on Mouse Controller";
            Width = 820;
            Height = 600;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(620, 420);

            BuildUi();
            Reload();
        }

        private void BuildUi()
        {
            info = new Label
            {
                Location = new Point(12, 10),
                Size = new Size(780, 36),
                Text = "Devices sharing your mouse's USB host controller compete for interrupt servicing — " +
                       "a noisy device (DAC, webcam, mass storage) can add jitter to mouse polling.",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(info);

            cbOnlyMouseController = new CheckBox
            {
                Text = "Only show controller(s) that host my mouse",
                Location = new Point(12, 50),
                Size = new Size(360, 22),
                Checked = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
            };
            cbOnlyMouseController.CheckedChanged += (s, e) => RenderTree();
            Controls.Add(cbOnlyMouseController);

            tree = new TreeView
            {
                CheckBoxes = true,
                FullRowSelect = true,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                HideSelection = false,
                Font = new Font(FontFamily.GenericSansSerif, 9),
                Location = new Point(12, 78),
                Size = new Size(780, 430),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            tree.AfterCheck += Tree_AfterCheck;
            tree.AfterSelect += (s, e) => UpdateButtonState();
            Controls.Add(tree);

            btnDisable = new Button
            {
                Text = "Disable Selected",
                Location = new Point(12, 520),
                Size = new Size(140, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnDisable.Click += (s, e) => RunOnSelected("Disable", UsbDeviceManager.Disable);
            Controls.Add(btnDisable);

            btnEnable = new Button
            {
                Text = "Enable Selected",
                Location = new Point(160, 520),
                Size = new Size(140, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnEnable.Click += (s, e) => RunOnSelected("Enable", UsbDeviceManager.Enable);
            Controls.Add(btnEnable);

            btnRestart = new Button
            {
                Text = "Restart Device",
                Location = new Point(308, 520),
                Size = new Size(120, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnRestart.Click += (s, e) => RunOnSelected("Restart", UsbDeviceManager.Restart);
            Controls.Add(btnRestart);

            btnCloseApps = new Button
            {
                Text = "Close Selected Apps",
                Location = new Point(436, 520),
                Size = new Size(150, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnCloseApps.Click += BtnCloseApps_Click;
            Controls.Add(btnCloseApps);

            btnRefresh = new Button
            {
                Text = "Re-scan",
                Location = new Point(594, 520),
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnRefresh.Click += (s, e) => Reload();
            Controls.Add(btnRefresh);

            btnClose = new Button
            {
                Text = "Close",
                Location = new Point(700, 520),
                Size = new Size(92, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
            };
            btnClose.Click += (s, e) => Close();
            Controls.Add(btnClose);
            AcceptButton = btnClose;
        }

        private void Reload()
        {
            UseWaitCursor = true;
            try
            {
                controllers = UsbTopology.Enumerate();
            }
            finally { UseWaitCursor = false; }
            RenderTree();
        }

        private void RenderTree()
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            if (controllers == null || controllers.Count == 0)
            {
                tree.Nodes.Add("(no USB controllers found via WMI)");
                tree.EndUpdate();
                return;
            }

            bool onlyMouse = cbOnlyMouseController.Checked;
            foreach (var ctrl in controllers)
            {
                if (onlyMouse && !ctrl.HostsUserMouse) continue;
                string ctrlLabel = ctrl.Name + (ctrl.HostsUserMouse ? "  ← hosts your mouse" : "");
                var ctrlNode = new TreeNode(ctrlLabel)
                {
                    Tag = ctrl,
                    ForeColor = ctrl.HostsUserMouse ? Color.DarkGreen : Color.Black,
                    NodeFont = new Font(tree.Font, ctrl.HostsUserMouse ? FontStyle.Bold : FontStyle.Regular),
                };
                foreach (var dev in ctrl.Devices)
                {
                    string label = FormatDeviceLabel(dev);
                    var devNode = new TreeNode(label) { Tag = dev };
                    if (dev.IsUserMouse)
                    {
                        devNode.ForeColor = Color.DarkRed;
                        devNode.NodeFont = new Font(tree.Font, FontStyle.Bold);
                    }
                    else if (dev.IsInputDevice)
                    {
                        devNode.ForeColor = Color.DarkGoldenrod;
                    }
                    else if (dev.IsHub)
                    {
                        devNode.ForeColor = Color.Gray;
                    }
                    if (!string.IsNullOrEmpty(dev.DriverService))
                    {
                        var svcNode = new TreeNode(
                            "driver service: " + dev.DriverService +
                            (string.IsNullOrEmpty(dev.DriverServiceState) ? "" : "  [" + dev.DriverServiceState + "]") +
                            (dev.DriverServicePid > 0 ? "  PID " + dev.DriverServicePid : ""))
                        {
                            ForeColor = Color.DarkSlateGray,
                        };
                        devNode.Nodes.Add(svcNode);
                    }
                    if (dev.RelatedProcesses != null && dev.RelatedProcesses.Count > 0)
                    {
                        foreach (var hp in dev.RelatedProcesses)
                        {
                            var procNode = new TreeNode(
                                "PID " + hp.Pid + "  " + hp.Name +
                                (string.IsNullOrEmpty(hp.WindowTitle) ? "  (no window)" : "  '" + hp.WindowTitle + "'") +
                                "  — " + hp.Reason)
                            {
                                Tag = hp,
                                ForeColor = Color.DarkBlue,
                            };
                            devNode.Nodes.Add(procNode);
                        }
                    }
                    else if (dev.HookSearchPerformed)
                    {
                        var noneNode = new TreeNode("(no related user-mode processes found)") { ForeColor = Color.Gray };
                        devNode.Nodes.Add(noneNode);
                    }
                    ctrlNode.Nodes.Add(devNode);
                }
                tree.Nodes.Add(ctrlNode);
            }
            foreach (TreeNode n in tree.Nodes) n.Expand();
            tree.EndUpdate();
            UpdateButtonState();
        }

        private string FormatDeviceLabel(UsbTopology.UsbDevice d)
        {
            var sb = new StringBuilder();
            sb.Append(d.Name ?? "(unknown)");
            if (!string.IsNullOrEmpty(d.ClassName)) sb.Append("  [").Append(d.ClassName).Append(']');
            if (!string.IsNullOrEmpty(d.VendorName)) sb.Append("  vendor=").Append(d.VendorName);
            else if (!string.IsNullOrEmpty(d.Vid)) sb.Append("  VID_").Append(d.Vid);
            if (d.IsUserMouse) sb.Append("  ← your mouse");
            else if (d.IsInputDevice) sb.Append("  ← input device (be careful)");
            else if (d.IsHub) sb.Append("  (hub)");
            if (!string.IsNullOrEmpty(d.Status) && !d.Status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                sb.Append("  status=").Append(d.Status);
            return sb.ToString();
        }

        private void Tree_AfterCheck(object sender, TreeViewEventArgs e)
        {
            // Don't allow checking the controller node itself.
            if (e.Node.Tag is UsbTopology.UsbController && e.Node.Checked)
                e.Node.Checked = false;
            UpdateButtonState();
        }

        private void UpdateButtonState()
        {
            int devCount = CollectCheckedDevices().Count;
            int procCount = CollectCheckedProcesses().Count;
            btnDisable.Enabled = btnEnable.Enabled = btnRestart.Enabled = devCount > 0;
            btnCloseApps.Enabled = procCount > 0;
        }

        private List<UsbTopology.UsbDevice> CollectCheckedDevices()
        {
            var list = new List<UsbTopology.UsbDevice>();
            WalkChecked(tree.Nodes, n => { if (n.Tag is UsbTopology.UsbDevice d) list.Add(d); });
            return list;
        }

        private List<HookProcessInfo> CollectCheckedProcesses()
        {
            var seen = new HashSet<int>();
            var list = new List<HookProcessInfo>();
            WalkChecked(tree.Nodes, n =>
            {
                if (n.Tag is HookProcessInfo p && seen.Add(p.Pid)) list.Add(p);
            });
            return list;
        }

        private void WalkChecked(TreeNodeCollection nodes, Action<TreeNode> visit)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Checked) visit(n);
                if (n.Nodes.Count > 0) WalkChecked(n.Nodes, visit);
            }
        }

        private void RunOnSelected(string verb, Func<string, UsbDeviceManager.ActionResult> action)
        {
            var devices = CollectCheckedDevices();
            if (devices.Count == 0) return;

            var mouseSelected = devices.Where(d => d.IsUserMouse).ToList();
            var inputSelected = devices.Where(d => d.IsInputDevice && !d.IsUserMouse).ToList();
            if (mouseSelected.Count > 0 && verb == "Disable")
            {
                if (MessageBox.Show(this,
                        "You're about to DISABLE your own mouse:\r\n  " +
                        string.Join("\r\n  ", mouseSelected.Select(d => d.Name)) +
                        "\r\n\r\nYou'll lose mouse input until you re-enable it via keyboard or unplug/replug. Are you sure?",
                        "Confirm — disabling your mouse", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            else if (inputSelected.Count > 0 && verb == "Disable")
            {
                if (MessageBox.Show(this,
                        "Selection includes input device(s):\r\n  " +
                        string.Join("\r\n  ", inputSelected.Select(d => d.Name)) +
                        "\r\n\r\nDisabling these may lock you out of your keyboard or mouse. Continue?",
                        "Confirm — input devices selected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            else
            {
                if (MessageBox.Show(this,
                        verb + " " + devices.Count + " device(s)?",
                        "Confirm " + verb, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return;
            }

            UseWaitCursor = true;
            btnDisable.Enabled = btnEnable.Enabled = btnRestart.Enabled = btnRefresh.Enabled = false;
            int ok = 0, fail = 0;
            var details = new StringBuilder();
            try
            {
                foreach (var d in devices)
                {
                    var r = action(d.InstanceId);
                    if (r.Success) ok++; else fail++;
                    details.Append(r.Success ? "[OK] " : "[--] ")
                           .Append(d.Name).Append(" (").Append(d.InstanceId).AppendLine(")")
                           .Append("    ").AppendLine(r.Message ?? "");
                }
            }
            finally
            {
                UseWaitCursor = false;
                btnRefresh.Enabled = true;
            }

            Thread.Sleep(250);
            ShowResult(verb + " result — OK: " + ok + ", failed: " + fail + "\r\n\r\n" + details.ToString(), fail > 0);
            Reload();
        }

        private void BtnCloseApps_Click(object sender, EventArgs e)
        {
            var procs = CollectCheckedProcesses();
            if (procs.Count == 0) return;

            var msg = "Close the following process(es)?\r\n\r\n" +
                      string.Join("\r\n", procs.Select(p => "  PID " + p.Pid + "  " + p.Name)) +
                      "\r\n\r\nMouseTester will try graceful close → kill → taskkill, and stop owning services if needed.";
            if (MessageBox.Show(this, msg, "Confirm Close Apps", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            UseWaitCursor = true;
            btnCloseApps.Enabled = btnDisable.Enabled = btnEnable.Enabled = btnRestart.Enabled = btnRefresh.Enabled = false;
            int closed = 0, alive = 0;
            var details = new StringBuilder();
            try
            {
                foreach (var p in procs)
                {
                    var r = ProcessKiller.Kill(p.Pid, p.Name);
                    if (r.Closed) closed++; else alive++;
                    details.Append(ProcessKiller.Summarize(r)).AppendLine();
                }
            }
            finally
            {
                UseWaitCursor = false;
                btnRefresh.Enabled = true;
            }

            Thread.Sleep(250);
            ShowResult("Close result — closed: " + closed + ", still alive: " + alive + "\r\n\r\n" + details.ToString(), alive > 0);
            Reload();
        }

        private void ShowResult(string text, bool warning)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "USB action result";
                dlg.Width = 720;
                dlg.Height = 480;
                dlg.StartPosition = FormStartPosition.CenterParent;
                var tb = new TextBox
                {
                    Multiline = true, ReadOnly = true,
                    ScrollBars = ScrollBars.Both, WordWrap = false,
                    Font = new Font(FontFamily.GenericMonospace, 9),
                    Dock = DockStyle.Fill, Text = text,
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
