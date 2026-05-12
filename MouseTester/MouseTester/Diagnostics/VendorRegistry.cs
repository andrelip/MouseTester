using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MouseTester.Diagnostics
{
    public static class VendorRegistry
    {
        public class VendorInfo
        {
            public string Name;
            public string[] ProcessPatterns;
        }

        // Map of USB VID (uppercase, 4 hex chars) → vendor + names of background processes that
        // commonly hook the device. Patterns are matched as case-insensitive substrings against
        // Process.ProcessName, so partial matches work (e.g. "lghub" matches "lghub_updater").
        private static readonly Dictionary<string, VendorInfo> Vendors = new Dictionary<string, VendorInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["046D"] = new VendorInfo { Name = "Logitech",          ProcessPatterns = new[] { "lghub", "lghub_agent", "lghub_updater", "LCore", "LGS", "logioptionsplus", "logi_options" } },
            ["1532"] = new VendorInfo { Name = "Razer",             ProcessPatterns = new[] { "Razer Synapse", "RzSDKService", "Razer Central", "Razer Cortex", "RazerCentralService", "RazerAppEngine" } },
            ["1B1C"] = new VendorInfo { Name = "Corsair",           ProcessPatterns = new[] { "iCUE", "CorsairLLAccess", "CorsairService", "iCUE Service" } },
            ["3434"] = new VendorInfo { Name = "Keychron",          ProcessPatterns = new string[0] },
            ["3938"] = new VendorInfo { Name = "MOUNTAIN",          ProcessPatterns = new[] { "MountainBaseCamp", "Mountain Base Camp" } },
            ["258A"] = new VendorInfo { Name = "SINOWEALTH",        ProcessPatterns = new string[0] },
            ["05AC"] = new VendorInfo { Name = "Apple",             ProcessPatterns = new[] { "AppleMobileDeviceService", "iTunesHelper", "AppleDevice" } },
            ["0B05"] = new VendorInfo { Name = "ASUS",              ProcessPatterns = new[] { "ROGLiveService", "ArmouryCrate", "AsusOptimization", "AsusSystemAnalysis" } },
            ["1A86"] = new VendorInfo { Name = "QinHeng/CH340",     ProcessPatterns = new string[0] },
            ["28DE"] = new VendorInfo { Name = "Valve (Steam)",     ProcessPatterns = new[] { "Steam", "GameOverlayUI", "SteamOverlay" } },
            ["045E"] = new VendorInfo { Name = "Microsoft",         ProcessPatterns = new[] { "GameBar", "Xbox", "MouseAndKeyboardCenter" } },
            ["1038"] = new VendorInfo { Name = "SteelSeries",       ProcessPatterns = new[] { "SteelSeriesEngine", "SteelSeries GG", "SteelSeriesHelper", "SteelSeriesEngineClient" } },
            ["0951"] = new VendorInfo { Name = "Kingston/HyperX",   ProcessPatterns = new[] { "ngenuity", "HyperX NGENUITY" } },
            ["3367"] = new VendorInfo { Name = "Endgame Gear",      ProcessPatterns = new[] { "Endgame Gear", "EGG Configuration Tool" } },
            ["320F"] = new VendorInfo { Name = "Pulsar",            ProcessPatterns = new[] { "Pulsar Fusion" } },
            ["2516"] = new VendorInfo { Name = "Cooler Master",     ProcessPatterns = new[] { "MasterPlus", "CMMasterPlus" } },
            ["1532"] = new VendorInfo { Name = "Razer",             ProcessPatterns = new[] { "Razer", "RzSDK" } },
            ["13D3"] = new VendorInfo { Name = "IMC Networks",      ProcessPatterns = new string[0] },
            ["0BDA"] = new VendorInfo { Name = "Realtek",           ProcessPatterns = new[] { "RtkAudUService", "RAVCpl", "RtkNGUI" } },
            ["8087"] = new VendorInfo { Name = "Intel",             ProcessPatterns = new[] { "BluetoothUserService", "RstMwService", "IntelCpHDCPSvc" } },
            ["04F2"] = new VendorInfo { Name = "Chicony",           ProcessPatterns = new[] { "ChiconyCamera" } },
            ["0C45"] = new VendorInfo { Name = "Microdia",          ProcessPatterns = new string[0] },
            ["0421"] = new VendorInfo { Name = "Nokia",             ProcessPatterns = new string[0] },
        };

        public static VendorInfo Lookup(string vid)
        {
            if (string.IsNullOrEmpty(vid)) return null;
            Vendors.TryGetValue(vid, out var info);
            return info;
        }

        // Manufacturer keywords that appear so often in unrelated processes that matching on them
        // would create huge false-positive noise. Skip these as fallback keywords.
        private static readonly HashSet<string> KeywordStopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "intel", "amd", "nvidia", "realtek", "generic", "standard", "compatible",
            "usb", "hid", "system", "windows", "device", "device.", "inc", "inc.", "ltd", "ltd.",
            "corp", "corp.", "corporation", "co", "co.", "the", "and"
        };

        public static List<HookProcessInfo> FindProcessesByManufacturerKeyword(string manufacturer)
        {
            var list = new List<HookProcessInfo>();
            if (string.IsNullOrWhiteSpace(manufacturer)) return list;

            // Extract candidate keywords (words ≥4 chars, not in stopwords).
            var keywords = manufacturer
                .Split(new[] { ' ', '\t', ',', '.', '-', '/', '\\', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4 && !KeywordStopwords.Contains(w))
                .Select(w => w.ToLowerInvariant())
                .Distinct()
                .ToArray();
            if (keywords.Length == 0) return list;

            int self = 0;
            try { self = Process.GetCurrentProcess().Id; } catch { }
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == self) continue;
                        string n = (p.ProcessName ?? "").ToLowerInvariant();
                        var hit = keywords.FirstOrDefault(k => n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (hit == null) continue;
                        string title = "";
                        try { title = p.MainWindowTitle; } catch { }
                        list.Add(new HookProcessInfo
                        {
                            Pid = p.Id,
                            Name = p.ProcessName,
                            WindowTitle = title,
                            Reason = "manufacturer keyword '" + hit + "' from '" + manufacturer + "'",
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        public static List<HookProcessInfo> FindRelatedProcesses(string vid)
        {
            var list = new List<HookProcessInfo>();
            var info = Lookup(vid);
            if (info == null || info.ProcessPatterns == null || info.ProcessPatterns.Length == 0) return list;

            int self = 0;
            try { self = Process.GetCurrentProcess().Id; } catch { }
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == self) continue;
                        string n = p.ProcessName ?? "";
                        var hit = info.ProcessPatterns.FirstOrDefault(pat => n.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (hit == null) continue;
                        string title = "";
                        try { title = p.MainWindowTitle; } catch { }
                        list.Add(new HookProcessInfo
                        {
                            Pid = p.Id,
                            Name = n,
                            WindowTitle = title,
                            Reason = info.Name + " device hook (matched '" + hit + "')",
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }
    }
}
