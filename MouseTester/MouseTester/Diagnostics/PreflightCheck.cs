using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseTester.Diagnostics
{
    public static class PreflightCheck
    {
        public static TrustReport Run(IntPtr selfHwnd)
        {
            var report = new TrustReport();
            int score = 100;

            // Timer resolution
            try
            {
                NativeMethods.NtQueryTimerResolution(out uint maxRes, out uint minRes, out uint curRes);
                double curMs = curRes / 10000.0;
                if (curMs <= 1.0)
                {
                    report.Items.Add(new TrustReportItem
                    {
                        Title = "System timer resolution",
                        Detail = $"{curMs:0.000} ms",
                        Status = TrustReportItem.Severity.Green
                    });
                }
                else
                {
                    score -= 20;
                    report.Items.Add(new TrustReportItem
                    {
                        Title = "System timer resolution",
                        Detail = $"{curMs:0.000} ms (will be raised to 1 ms during recording)",
                        Status = TrustReportItem.Severity.Yellow,
                        Recommendation = "OK — MouseTester sets timeBeginPeriod(1) for the recording duration."
                    });
                }
            }
            catch
            {
                report.Items.Add(new TrustReportItem { Title = "System timer resolution", Detail = "unknown", Status = TrustReportItem.Severity.Yellow });
            }

            // Power source
            try
            {
                if (NativeMethods.GetSystemPowerStatus(out var pwr))
                {
                    bool ac = pwr.ACLineStatus == 1;
                    report.Items.Add(new TrustReportItem
                    {
                        Title = "Power source",
                        Detail = ac ? "AC" : "Battery",
                        Status = ac ? TrustReportItem.Severity.Green : TrustReportItem.Severity.Yellow,
                        Recommendation = ac ? null : "Plug in to AC. Battery profiles often park cores or throttle clocks."
                    });
                    if (!ac) score -= 15;
                }
            }
            catch { }

            // Foreground — match by process ID so any window owned by MouseTester (main form,
            // trust dialog, plot window, USB devices dialog) counts as "us".
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                uint fgPid = 0;
                if (fg != IntPtr.Zero) NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
                int selfPid = Process.GetCurrentProcess().Id;
                bool isUs = fgPid == (uint)selfPid;
                report.Items.Add(new TrustReportItem
                {
                    Title = "Foreground window",
                    Detail = isUs ? "MouseTester" : "another application",
                    Status = isUs ? TrustReportItem.Severity.Green : TrustReportItem.Severity.Yellow,
                    Recommendation = isUs ? null :
                        "RIDEV_INPUTSINK still captures events while another window has focus, and " +
                        "MouseTester raises its own priority during recording — so this is rarely a real " +
                        "problem. Use the 'Focus MouseTester' button if you want to be safe."
                });
                if (!isUs) score -= 5;
            }
            catch { }

            // Process priority
            try
            {
                var pri = Process.GetCurrentProcess().PriorityClass;
                report.Items.Add(new TrustReportItem
                {
                    Title = "Process priority",
                    Detail = pri.ToString() + " (will be raised to High during recording)",
                    Status = TrustReportItem.Severity.Green
                });
            }
            catch { }

            // EcoQoS / power throttling
            try
            {
                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE { Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
                if (NativeMethods.GetProcessInformation(NativeMethods.GetCurrentProcess(), NativeMethods.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf(state)))
                {
                    bool throttled = (state.StateMask & NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
                    if (throttled)
                    {
                        score -= 25;
                        report.Items.Add(new TrustReportItem
                        {
                            Title = "EcoQoS / process throttling",
                            Detail = "ENABLED — Windows is allowed to slow this process",
                            Status = TrustReportItem.Severity.Red,
                            Recommendation = "MouseTester will disable EcoQoS during recording."
                        });
                    }
                    else
                    {
                        report.Items.Add(new TrustReportItem
                        {
                            Title = "EcoQoS / process throttling",
                            Detail = "disabled",
                            Status = TrustReportItem.Severity.Green
                        });
                    }
                }
            }
            catch { }

            // Admin / ETW
            bool admin = false;
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    admin = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { }
            report.Items.Add(new TrustReportItem
            {
                Title = "Kernel-level diagnostics (ETW)",
                Detail = admin ? "available — DPC/ISR/context-switch events will be captured" : "unavailable — not running as administrator",
                Status = admin ? TrustReportItem.Severity.Green : TrustReportItem.Severity.Yellow,
                Recommendation = admin ? null : "Re-launch as administrator to attribute jitter to kernel-side causes (DPCs, USB retries, scheduler preemption)."
            });
            if (!admin) score -= 15;

            // CPU / cores
            try
            {
                int cpus = Environment.ProcessorCount;
                report.Items.Add(new TrustReportItem
                {
                    Title = "CPU cores available",
                    Detail = cpus.ToString(),
                    Status = cpus >= 2 ? TrustReportItem.Severity.Green : TrustReportItem.Severity.Yellow,
                    Recommendation = cpus < 2 ? "Single-core: affinity pinning has no effect." : null
                });
            }
            catch { }

            // Raw Input registrations on this process (system-wide enumeration is not exposed)
            try
            {
                uint count = 0;
                NativeMethods.GetRegisteredRawInputDevices(IntPtr.Zero, ref count, (uint)Marshal.SizeOf(typeof(uint)) * 4);
                report.Items.Add(new TrustReportItem
                {
                    Title = "Raw Input registrations (this process)",
                    Detail = count + " device(s)",
                    Status = TrustReportItem.Severity.Green
                });
            }
            catch { }

            // Suspicious overlays / known noisy apps - populates DetectedHooks for the Close action
            try
            {
                var rules = new[]
                {
                    new { Pattern = "RTSS",                Reason = "RivaTuner Statistics Server (overlay & hook)" },
                    new { Pattern = "RTSSHooksLoader",     Reason = "RTSS injection hook loader" },
                    new { Pattern = "MSIAfterburner",      Reason = "MSI Afterburner overlay" },
                    new { Pattern = "obs64",               Reason = "OBS Studio (capture hooks)" },
                    new { Pattern = "obs32",               Reason = "OBS Studio (capture hooks)" },
                    new { Pattern = "Discord",             Reason = "Discord overlay" },
                    new { Pattern = "Razer",               Reason = "Razer Synapse / Cortex (input hooks)" },
                    new { Pattern = "iCUE",                Reason = "Corsair iCUE (input hooks)" },
                    new { Pattern = "GHUB",                Reason = "Logitech G HUB (input hooks)" },
                    new { Pattern = "GeForceExperience",   Reason = "NVIDIA GeForce Experience overlay" },
                    new { Pattern = "NVIDIA Overlay",      Reason = "NVIDIA in-game overlay" },
                    new { Pattern = "NVIDIA Share",        Reason = "NVIDIA ShadowPlay overlay" },
                    new { Pattern = "EpicGamesLauncher",   Reason = "Epic Games launcher overlay" },
                    new { Pattern = "SteamOverlay",        Reason = "Steam overlay" },
                    new { Pattern = "GameOverlayUI",       Reason = "Steam GameOverlayUI" },
                    new { Pattern = "Steam.exe",           Reason = "Steam (overlay parent)" },
                    new { Pattern = "WallpaperEngine",     Reason = "Wallpaper Engine" },
                    new { Pattern = "TeamViewer",          Reason = "TeamViewer (input hooks)" },
                    new { Pattern = "AnyDesk",             Reason = "AnyDesk (input hooks)" },
                };

                int currentPid = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == currentPid) continue;
                        string name = p.ProcessName;
                        var hit = rules.FirstOrDefault(r => name.IndexOf(r.Pattern, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (hit == null) continue;
                        string title = "";
                        try { title = p.MainWindowTitle; } catch { }
                        report.DetectedHooks.Add(new HookProcessInfo
                        {
                            Pid = p.Id,
                            Name = name,
                            WindowTitle = title,
                            Reason = hit.Reason,
                        });
                    }
                    catch { }
                }

                if (report.DetectedHooks.Count > 0)
                {
                    score -= report.DetectedHooks.Count * 3;
                    report.Items.Add(new TrustReportItem
                    {
                        Title = "Known overlay / hook software",
                        Detail = report.DetectedHooks.Count + " process(es) detected — see list below",
                        Status = TrustReportItem.Severity.Yellow,
                        Recommendation = "Use the Close button to terminate them for the cleanest measurement."
                    });
                }
                else
                {
                    report.Items.Add(new TrustReportItem
                    {
                        Title = "Known overlay / hook software",
                        Detail = "none detected",
                        Status = TrustReportItem.Severity.Green
                    });
                }
            }
            catch { }

            report.Score = Math.Max(0, Math.Min(100, score));
            report.Summary = $"Trust score: {report.Score}/100";
            return report;
        }
    }
}
