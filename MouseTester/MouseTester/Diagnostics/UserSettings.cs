using System;
using System.Globalization;
using System.IO;

namespace MouseTester.Diagnostics
{
    public static class UserSettings
    {
        private static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MouseTester");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.txt");
            }
        }

        public static double LoadCpi(double fallback)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return fallback;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key.Equals("Cpi", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double cpi) &&
                        cpi > 0)
                    {
                        return cpi;
                    }
                }
            }
            catch { }
            return fallback;
        }

        public static void SaveCpi(double cpi)
        {
            try
            {
                File.WriteAllText(SettingsPath, "Cpi=" + cpi.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
            }
            catch { }
        }
    }
}
