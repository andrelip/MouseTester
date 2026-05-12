using System;
using System.Diagnostics;

namespace MouseTester.Diagnostics
{
    public static class UsbDeviceManager
    {
        public class ActionResult
        {
            public string InstanceId;
            public bool Success;
            public string Message;
        }

        public static ActionResult Disable(string instanceId) => Run(instanceId, "/disable-device");
        public static ActionResult Enable(string instanceId) => Run(instanceId, "/enable-device");
        public static ActionResult Restart(string instanceId) => Run(instanceId, "/restart-device");

        private static ActionResult Run(string instanceId, string verb)
        {
            var r = new ActionResult { InstanceId = instanceId };
            try
            {
                var psi = new ProcessStartInfo("pnputil.exe", verb + " \"" + instanceId + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    r.Success = p.ExitCode == 0;
                    string combined = (stdout + " " + stderr).Trim();
                    r.Message = string.IsNullOrEmpty(combined) ? ("pnputil exit " + p.ExitCode) : combined;
                }
            }
            catch (Exception ex) { r.Message = ex.Message; }
            return r;
        }
    }
}
