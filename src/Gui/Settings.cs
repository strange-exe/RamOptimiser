using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RamOptimiser.Gui
{
    public enum ThemeMode { System, Light, Dark }

    /// <summary>Everything the GUI persists. Plain values: validation lives in Normalized() and SettingsStore.</summary>
    public sealed class AppSettings
    {
        public const int DefaultThresholdPercent = 85, MinThresholdPercent = 50, MaxThresholdPercent = 99;
        public const int DefaultIntervalMinutes = 0, MinIntervalMinutes = 0, MaxIntervalMinutes = 1440;
        /// <summary>
        /// The cooldown floor is 60 s: settings.ini sits in a folder the non-elevated user can write, and an
        /// elevated instance must not be talked into cleaning the whole system back to back.
        /// </summary>
        public const int DefaultCooldownSeconds = 300, MinCooldownSeconds = 60, MaxCooldownSeconds = 86400;

        public bool AutoCleanEnabled { get; set; }
        public int ThresholdPercent { get; set; }
        /// <summary>Periodic clean every N minutes; 0 turns it off.</summary>
        public int IntervalMinutes { get; set; }
        public int CooldownSeconds { get; set; }
        public Areas Areas { get; set; }
        public ThemeMode Theme { get; set; }
        public bool StartWithWindows { get; set; }
        public bool CloseToTray { get; set; }
        public bool ShowTrayPercent { get; set; }
        /// <summary>Power Clean: every memory area plus stopping the chosen services for this session.</summary>
        public bool PowerClean { get; set; }
        /// <summary>Services Power Clean stops. Always a subset of ServiceCatalog, whatever settings.ini says.</summary>
        public List<string> PowerServices { get; set; }

        /// <summary>What a clean actually runs: the chosen areas, or everything plus services under Power Clean.</summary>
        public Areas CleanAreas
        {
            get { return PowerClean ? Areas.All | Areas.Services : Areas; }
        }

        public AppSettings()
        {
            AutoCleanEnabled = false;
            ThresholdPercent = DefaultThresholdPercent;
            IntervalMinutes = DefaultIntervalMinutes;
            CooldownSeconds = DefaultCooldownSeconds;
            Areas = Areas.Default;
            Theme = ThemeMode.System;
            StartWithWindows = false;
            CloseToTray = true;
            ShowTrayPercent = true;
            PowerClean = false;
            PowerServices = ServiceCatalog.SuggestedNames();
        }

        public AppSettings Clone()
        {
            var s = (AppSettings)MemberwiseClone();
            s.PowerServices = new List<string>(PowerServices ?? new List<string>());
            return s;
        }

        /// <summary>
        /// A copy in which every out-of-range value is replaced by its default, except a cooldown below the
        /// minimum, which is raised to the minimum.
        /// </summary>
        public AppSettings Normalized()
        {
            AppSettings s = Clone();
            if (!InRange(s.ThresholdPercent, MinThresholdPercent, MaxThresholdPercent)) s.ThresholdPercent = DefaultThresholdPercent;
            if (!InRange(s.IntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes)) s.IntervalMinutes = DefaultIntervalMinutes;
            s.CooldownSeconds = FloorCooldown(s.CooldownSeconds);
            s.Areas &= Areas.All;
            if (s.Areas == Areas.None) s.Areas = Areas.Default;
            if (s.Theme != ThemeMode.System && s.Theme != ThemeMode.Light && s.Theme != ThemeMode.Dark) s.Theme = ThemeMode.System;
            s.PowerServices = ServiceCatalog.Filter(s.PowerServices);
            return s;
        }

        internal static bool InRange(int v, int min, int max) { return v >= min && v <= max; }

        /// <summary>Below the minimum becomes the minimum; above the maximum becomes the default.</summary>
        internal static int FloorCooldown(int seconds)
        {
            if (seconds < MinCooldownSeconds) return MinCooldownSeconds;
            return seconds > MaxCooldownSeconds ? DefaultCooldownSeconds : seconds;
        }
    }

    /// <summary>
    /// Reads and writes settings as a key=value text file (invariant culture). Loading never throws on bad
    /// content: unknown keys are ignored and any missing, malformed or out-of-range value keeps its default.
    /// </summary>
    public static class SettingsStore
    {
        // A settings file is a few hundred bytes; anything far larger is not ours and is not read.
        internal const long MaxFileBytes = 64 * 1024;
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string DefaultPath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(Path.Combine(local, "RamOptimiser"), "settings.ini");
            }
        }

        /// <summary>
        /// Missing, unreadable or corrupt file: returns defaults (for the values it could not read). So does a
        /// folder or file that is a link, or a folder with an unexpected owner (see SafeUserFolder).
        /// </summary>
        public static AppSettings Load(string path)
        {
            if (path == null) throw new ArgumentNullException("path");
            try
            {
                string full = Path.GetFullPath(path);
                SafeUserFolder.Check(Path.GetDirectoryName(full), full);
                var info = new FileInfo(full);
                if (!info.Exists || info.Length > MaxFileBytes) return new AppSettings();
                return Parse(File.ReadAllText(full, Utf8NoBom));
            }
            catch (IOException) { return new AppSettings(); }
            catch (UnauthorizedAccessException) { return new AppSettings(); }
            catch (System.Security.SecurityException) { return new AppSettings(); }
        }

        /// <summary>
        /// Writes a temp file in the target folder, then swaps it in, so a crash never leaves a half file.
        /// Throws IOException, writing nothing, when the folder or a file is a link or the folder has an
        /// unexpected owner: an elevated instance must not follow links planted in this user-writable folder.
        /// The checks run again just before the swap; they narrow the window for a swap, they cannot close it.
        /// </summary>
        public static void Save(AppSettings settings, string path)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (path == null) throw new ArgumentNullException("path");
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            Directory.CreateDirectory(dir);
            SafeUserFolder.Check(dir, full);
            string temp = Path.Combine(dir, Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = Utf8NoBom.GetBytes(Serialize(settings));
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                SafeUserFolder.Check(dir, full, temp);
                if (File.Exists(full)) File.Replace(temp, full, null);
                else File.Move(temp, full);
            }
            finally
            {
                // Clean up only while the folder still checks out, so a swapped folder never steers the delete.
                try { if (File.Exists(temp) && SafeUserFolder.IsSafe(dir, temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static string Serialize(AppSettings settings)
        {
            AppSettings s = settings.Normalized();
            var sb = new StringBuilder();
            sb.Append("# RamOptimiser settings. Unknown or invalid lines are ignored.\r\n");
            Line(sb, "AutoCleanEnabled", Bool(s.AutoCleanEnabled));
            Line(sb, "ThresholdPercent", Int(s.ThresholdPercent));
            Line(sb, "IntervalMinutes", Int(s.IntervalMinutes));
            Line(sb, "CooldownSeconds", Int(s.CooldownSeconds));
            Line(sb, "Areas", AreasToString(s.Areas));
            Line(sb, "Theme", s.Theme.ToString().ToLowerInvariant());
            Line(sb, "StartWithWindows", Bool(s.StartWithWindows));
            Line(sb, "CloseToTray", Bool(s.CloseToTray));
            Line(sb, "ShowTrayPercent", Bool(s.ShowTrayPercent));
            Line(sb, "PowerClean", Bool(s.PowerClean));
            Line(sb, "PowerServices", string.Join(",", s.PowerServices.ToArray()));
            return sb.ToString();
        }

        public static AppSettings Parse(string text)
        {
            var s = new AppSettings();
            if (text == null) return s;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    Apply(s, line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim());
                }
            }
            return s;
        }

        static void Apply(AppSettings s, string key, string value)
        {
            bool b; int n; Areas a; ThemeMode t;
            switch (key)
            {
                case "autocleanenabled": if (TryBool(value, out b)) s.AutoCleanEnabled = b; break;
                case "thresholdpercent":
                    if (TryInt(value, AppSettings.MinThresholdPercent, AppSettings.MaxThresholdPercent, out n)) s.ThresholdPercent = n;
                    break;
                case "intervalminutes":
                    if (TryInt(value, AppSettings.MinIntervalMinutes, AppSettings.MaxIntervalMinutes, out n)) s.IntervalMinutes = n;
                    break;
                case "cooldownseconds":
                    // Any whole number up to the maximum is read; below the 60 s floor it becomes 60.
                    if (TryInt(value, int.MinValue, AppSettings.MaxCooldownSeconds, out n)) s.CooldownSeconds = AppSettings.FloorCooldown(n);
                    break;
                case "areas": if (TryAreas(value, out a)) s.Areas = a; break;
                case "theme": if (TryTheme(value, out t)) s.Theme = t; break;
                case "startwithwindows": if (TryBool(value, out b)) s.StartWithWindows = b; break;
                case "closetotray": if (TryBool(value, out b)) s.CloseToTray = b; break;
                case "showtraypercent": if (TryBool(value, out b)) s.ShowTrayPercent = b; break;
                case "powerclean": if (TryBool(value, out b)) s.PowerClean = b; break;
                // Names outside the catalog are dropped here: the file is user-writable, the clean is elevated.
                case "powerservices": s.PowerServices = ServiceCatalog.Filter(value.Split(',')); break;
            }
        }

        /// <summary>Comma list of the AreaNames keys, in execution order.</summary>
        public static string AreasToString(Areas areas)
        {
            var names = new List<string>();
            foreach (var kv in AreaNames.Known)
                if ((areas & kv.Value) != 0) names.Add(kv.Key);
            return string.Join(",", names.ToArray());
        }

        internal static bool TryAreas(string value, out Areas areas)
        {
            areas = Areas.None;
            try { areas = AreaNames.Parse(value); }
            catch (ArgumentException) { return false; }
            return areas != Areas.None;
        }

        internal static bool TryBool(string value, out bool result)
        {
            string v = value.ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes") { result = true; return true; }
            if (v == "false" || v == "0" || v == "no") { result = false; return true; }
            result = false;
            return false;
        }

        internal static bool TryInt(string value, int min, int max, out int result)
        {
            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result)) return false;
            return AppSettings.InRange(result, min, max);
        }

        internal static bool TryTheme(string value, out ThemeMode theme)
        {
            switch (value.ToLowerInvariant())
            {
                case "system": theme = ThemeMode.System; return true;
                case "light": theme = ThemeMode.Light; return true;
                case "dark": theme = ThemeMode.Dark; return true;
                default: theme = ThemeMode.System; return false;
            }
        }

        static void Line(StringBuilder sb, string key, string value) { sb.Append(key).Append('=').Append(value).Append("\r\n"); }
        static string Bool(bool b) { return b ? "true" : "false"; }
        static string Int(int n) { return n.ToString(CultureInfo.InvariantCulture); }
    }
}
