using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MouseTester.Diagnostics
{
    public static class UsbTopology
    {
        public class UsbController
        {
            public string Name;
            public string InstanceId;
            public List<UsbDevice> Devices = new List<UsbDevice>();
            public bool HostsUserMouse;
        }

        public class UsbDevice
        {
            public string Name;
            public string Description;
            public string InstanceId;
            public string Status;
            public string ClassName;
            public string Manufacturer;
            public string Vid;
            public string Pid;
            public string VendorName;
            public string DriverService;       // kernel service driving the device (HidUsb, USBSTOR, etc.)
            public string DriverServiceState;  // Running / Stopped / etc.
            public uint DriverServicePid;      // 0 if user-mode partner not found
            public List<HookProcessInfo> RelatedProcesses = new List<HookProcessInfo>();
            public bool HookSearchPerformed;
            public bool IsUserMouse;
            public bool IsInputDevice;
            public bool IsHub;
        }

        public static List<UsbController> Enumerate()
        {
            var ctrlMap = new Dictionary<string, UsbController>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_USBController"))
                using (var col = s.Get())
                {
                    foreach (ManagementObject o in col)
                    {
                        try
                        {
                            var c = new UsbController
                            {
                                Name = o["Name"]?.ToString() ?? "USB Controller",
                                InstanceId = o["DeviceID"]?.ToString() ?? "",
                            };
                            if (!string.IsNullOrEmpty(c.InstanceId)) ctrlMap[c.InstanceId] = c;
                        }
                        finally { o.Dispose(); }
                    }
                }
            }
            catch { }

            var devicesByInstanceId = new Dictionary<string, UsbDevice>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_USBControllerDevice"))
                using (var col = s.Get())
                {
                    foreach (ManagementObject o in col)
                    {
                        try
                        {
                            string antecedent = ExtractInstanceId(o["Antecedent"]?.ToString());
                            string dependent = ExtractInstanceId(o["Dependent"]?.ToString());
                            if (string.IsNullOrEmpty(antecedent) || string.IsNullOrEmpty(dependent)) continue;
                            if (!ctrlMap.TryGetValue(antecedent, out var ctrl)) continue;

                            if (!devicesByInstanceId.TryGetValue(dependent, out var dev))
                            {
                                dev = QueryDeviceInfo(dependent);
                                devicesByInstanceId[dependent] = dev;
                            }
                            if (!ctrl.Devices.Contains(dev)) ctrl.Devices.Add(dev);
                        }
                        finally { o.Dispose(); }
                    }
                }
            }
            catch { }

            // Mark the user's mouse(s) by VID/PID match against raw-input device names.
            var mouseVidPids = GetRawInputMouseVidPids();
            foreach (var dev in devicesByInstanceId.Values)
            {
                string vid, pid;
                ExtractVidAndPid(dev.InstanceId, out vid, out pid);
                dev.Vid = vid;
                dev.Pid = pid;
                dev.HookSearchPerformed = true;
                var seenPids = new HashSet<int>();
                if (!string.IsNullOrEmpty(vid))
                {
                    var vendor = VendorRegistry.Lookup(vid);
                    if (vendor != null)
                    {
                        dev.VendorName = vendor.Name;
                        foreach (var hp in VendorRegistry.FindRelatedProcesses(vid))
                            if (seenPids.Add(hp.Pid)) dev.RelatedProcesses.Add(hp);
                    }
                }
                // Manufacturer-keyword fallback: catches vendors not in the curated VID table
                // (e.g. niche peripherals, USB DACs, capture cards). Less precise but broadens coverage.
                if (!string.IsNullOrEmpty(dev.Manufacturer))
                {
                    foreach (var hp in VendorRegistry.FindProcessesByManufacturerKeyword(dev.Manufacturer))
                        if (seenPids.Add(hp.Pid)) dev.RelatedProcesses.Add(hp);
                }
                string vp = (vid != null && pid != null) ? "VID_" + vid + "&PID_" + pid : null;
                if (!string.IsNullOrEmpty(vp) && mouseVidPids.Contains(vp, StringComparer.OrdinalIgnoreCase))
                {
                    dev.IsUserMouse = true;
                }
                if (string.Equals(dev.ClassName, "HIDClass", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dev.ClassName, "Mouse", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dev.ClassName, "Keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    dev.IsInputDevice = true;
                }
                if (!string.IsNullOrEmpty(dev.Name) && dev.Name.IndexOf("Hub", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    dev.IsHub = true;
                }
            }

            foreach (var ctrl in ctrlMap.Values)
            {
                ctrl.Devices = ctrl.Devices.OrderByDescending(d => d.IsUserMouse)
                                           .ThenBy(d => d.Name ?? "").ToList();
                ctrl.HostsUserMouse = ctrl.Devices.Any(d => d.IsUserMouse);
            }

            return ctrlMap.Values
                          .OrderByDescending(c => c.HostsUserMouse)
                          .ThenBy(c => c.Name ?? "")
                          .ToList();
        }

        private static string ExtractInstanceId(string assocPath)
        {
            if (string.IsNullOrEmpty(assocPath)) return null;
            int idx = assocPath.IndexOf("DeviceID=\"", StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += "DeviceID=\"".Length;
            int end = assocPath.IndexOf('"', idx);
            if (end < 0) return null;
            return assocPath.Substring(idx, end - idx).Replace("\\\\", "\\");
        }

        private static UsbDevice QueryDeviceInfo(string instanceId)
        {
            var dev = new UsbDevice { InstanceId = instanceId, Name = "(unknown)" };
            try
            {
                string escaped = instanceId.Replace("\\", "\\\\").Replace("'", "''");
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name, Description, Status, PNPClass, Manufacturer, Service FROM Win32_PnPEntity WHERE DeviceID='" + escaped + "'"))
                using (var col = s.Get())
                {
                    foreach (ManagementObject o in col)
                    {
                        try
                        {
                            dev.Name = o["Name"]?.ToString() ?? dev.Name;
                            dev.Description = o["Description"]?.ToString() ?? "";
                            dev.Status = o["Status"]?.ToString() ?? "";
                            dev.ClassName = o["PNPClass"]?.ToString() ?? "";
                            dev.Manufacturer = o["Manufacturer"]?.ToString() ?? "";
                            dev.DriverService = o["Service"]?.ToString() ?? "";
                        }
                        finally { o.Dispose(); }
                        break;
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(dev.DriverService))
            {
                try
                {
                    string svcEscaped = dev.DriverService.Replace("'", "''");
                    using (var s = new ManagementObjectSearcher(
                        "SELECT State, ProcessId FROM Win32_Service WHERE Name='" + svcEscaped + "'"))
                    using (var col = s.Get())
                    {
                        foreach (ManagementObject o in col)
                        {
                            try
                            {
                                dev.DriverServiceState = o["State"]?.ToString() ?? "";
                                if (o["ProcessId"] != null) dev.DriverServicePid = Convert.ToUInt32(o["ProcessId"]);
                            }
                            finally { o.Dispose(); }
                            break;
                        }
                    }
                }
                catch { }
            }

            return dev;
        }

        private static HashSet<string> GetRawInputMouseVidPids()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                uint num = 0;
                uint structSize = (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICELIST));
                NativeMethods.GetRawInputDeviceList(null, ref num, structSize);
                if (num == 0) return result;
                var list = new NativeMethods.RAWINPUTDEVICELIST[num];
                NativeMethods.GetRawInputDeviceList(list, ref num, structSize);
                foreach (var entry in list)
                {
                    if (entry.dwType != NativeMethods.RIM_TYPEMOUSE_RAW) continue;
                    uint nameLen = 0;
                    NativeMethods.GetRawInputDeviceInfoW(entry.hDevice, NativeMethods.RIDI_DEVICENAME, null, ref nameLen);
                    if (nameLen == 0) continue;
                    var sb = new StringBuilder((int)nameLen + 1);
                    NativeMethods.GetRawInputDeviceInfoW(entry.hDevice, NativeMethods.RIDI_DEVICENAME, sb, ref nameLen);
                    string vp = ExtractVidPid(sb.ToString());
                    if (!string.IsNullOrEmpty(vp)) result.Add(vp);
                }
            }
            catch { }
            return result;
        }

        private static string ExtractVidPid(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var m = Regex.Match(s, @"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase);
            return m.Success ? m.Value : null;
        }

        private static void ExtractVidAndPid(string s, out string vid, out string pid)
        {
            vid = null; pid = null;
            if (string.IsNullOrEmpty(s)) return;
            var m = Regex.Match(s, @"VID_(?<v>[0-9A-F]{4})&PID_(?<p>[0-9A-F]{4})", RegexOptions.IgnoreCase);
            if (m.Success) { vid = m.Groups["v"].Value.ToUpperInvariant(); pid = m.Groups["p"].Value.ToUpperInvariant(); }
        }
    }
}
