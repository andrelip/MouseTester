using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;

namespace MouseTester.Diagnostics
{
    public static class ProcessKiller
    {
        public class KillStep
        {
            public string Strategy;
            public bool Success;
            public string Detail;
        }

        public class KillResult
        {
            public int Pid;
            public string Name;
            public bool Closed;
            public bool ServiceStopped;
            public List<string> StoppedServices = new List<string>();
            public List<KillStep> Steps = new List<KillStep>();
            public string FinalNote;
        }

        // SeDebugPrivilege escalation removed: it triggers Smart App Control / Defender heuristics
        // (token-manipulation is a malware indicator). For SYSTEM-owned processes we rely on
        // ServiceController.Stop + taskkill /F /T which work fine when launched as administrator.
        public static bool EnableSeDebugPrivilege() { return false; }

        public static KillResult Kill(int pid, string name)
        {
            var result = new KillResult { Pid = pid, Name = name };

            // Step 0: collect services owned by this PID and stop them so they don't respawn the process.
            try
            {
                var svcs = GetServicesForPid(pid);
                foreach (var svc in svcs)
                {
                    var step = new KillStep { Strategy = "Stop service " + svc };
                    try
                    {
                        DisableServiceAutoRestart(svc);
                        using (var sc = new ServiceController(svc))
                        {
                            if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                            {
                                sc.Stop();
                                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                            }
                            step.Success = true;
                            step.Detail = "stopped";
                            result.StoppedServices.Add(svc);
                            result.ServiceStopped = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        step.Success = false;
                        step.Detail = ex.Message;
                    }
                    result.Steps.Add(step);
                }
            }
            catch (Exception ex)
            {
                result.Steps.Add(new KillStep { Strategy = "WMI service lookup", Success = false, Detail = ex.Message });
            }

            // Step 1: graceful CloseMainWindow.
            try
            {
                var p = SafeGetProcess(pid);
                if (p == null) { result.Closed = true; result.FinalNote = "already exited"; return result; }
                var step = new KillStep { Strategy = "CloseMainWindow" };
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow() && p.WaitForExit(1000))
                    {
                        step.Success = true; step.Detail = "exited";
                        result.Closed = true;
                        result.Steps.Add(step);
                        return result;
                    }
                    step.Detail = "no main window or did not exit in 1s";
                }
                catch (Exception ex) { step.Detail = ex.Message; }
                result.Steps.Add(step);
            }
            catch { }

            // Step 2: managed Process.Kill.
            try
            {
                var p = SafeGetProcess(pid);
                if (p == null) { result.Closed = true; result.FinalNote = "exited after CloseMainWindow"; return result; }
                var step = new KillStep { Strategy = "Process.Kill" };
                try
                {
                    p.Kill();
                    if (p.WaitForExit(1500)) { step.Success = true; step.Detail = "exited"; result.Closed = true; }
                    else step.Detail = "Kill called, did not exit";
                }
                catch (Win32Exception wex) { step.Detail = $"Win32 0x{wex.NativeErrorCode:X}: {wex.Message}"; }
                catch (Exception ex) { step.Detail = ex.Message; }
                result.Steps.Add(step);
                if (result.Closed) return result;
            }
            catch { }

            // Step 3: taskkill /F /T (force, with tree).
            try
            {
                if (SafeGetProcess(pid) == null) { result.Closed = true; return result; }
                var step = new KillStep { Strategy = "taskkill /F /T" };
                try
                {
                    var psi = new ProcessStartInfo("taskkill", "/F /T /PID " + pid)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using (var tk = Process.Start(psi))
                    {
                        string stdout = tk.StandardOutput.ReadToEnd();
                        string stderr = tk.StandardError.ReadToEnd();
                        tk.WaitForExit(5000);
                        if (tk.ExitCode == 0)
                        {
                            // Verify process actually went away.
                            int waited = 0;
                            while (waited < 2000 && SafeGetProcess(pid) != null) { System.Threading.Thread.Sleep(100); waited += 100; }
                            if (SafeGetProcess(pid) == null) { step.Success = true; step.Detail = stdout.Trim(); result.Closed = true; }
                            else step.Detail = "taskkill returned 0 but process still alive";
                        }
                        else
                        {
                            step.Detail = "taskkill exit " + tk.ExitCode + ": " + (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                        }
                    }
                }
                catch (Exception ex) { step.Detail = ex.Message; }
                result.Steps.Add(step);
                if (result.Closed) return result;
            }
            catch { }

            if (!result.Closed && result.ServiceStopped)
                result.FinalNote = "Stopped owning service(s) — process may exit on its own when service quits.";

            return result;
        }

        private static Process SafeGetProcess(int pid)
        {
            try { return Process.GetProcessById(pid); } catch { return null; }
        }

        private static List<string> GetServicesForPid(int pid)
        {
            var services = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Service WHERE ProcessId = " + pid))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject svc in results)
                    {
                        var n = svc["Name"] as string;
                        if (!string.IsNullOrEmpty(n)) services.Add(n);
                    }
                }
            }
            catch { }
            return services;
        }

        private static void DisableServiceAutoRestart(string serviceName)
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", "failure \"" + serviceName + "\" reset= 0 actions= none")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(2000);
                }
            }
            catch { }
        }

        public static string Summarize(KillResult r)
        {
            var sb = new StringBuilder();
            sb.Append("PID ").Append(r.Pid).Append(" ").Append(r.Name).Append(": ");
            sb.Append(r.Closed ? "CLOSED" : "STILL ALIVE");
            if (r.StoppedServices.Count > 0) sb.Append(" (stopped service: ").Append(string.Join(", ", r.StoppedServices)).Append(")");
            if (!string.IsNullOrEmpty(r.FinalNote)) sb.Append(" — ").Append(r.FinalNote);
            sb.AppendLine();
            foreach (var s in r.Steps)
                sb.Append("    ").Append(s.Success ? "[OK]" : "[--]").Append(" ").Append(s.Strategy).Append(": ").AppendLine(s.Detail ?? "");
            return sb.ToString();
        }
    }
}
