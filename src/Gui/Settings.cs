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
            AppSettings settings;
            return TryLoad(path, out settings) ? settings : new AppSettings();
        }

        /// <summary>
        /// As Load, but says whether the file was read: false (and null) for a missing, oversize, locked, refused or
        /// unreadable file, so a caller merging or reloading never takes a file it could not read for one of
        /// defaults. A file that was read but holds bad values is true, with defaults for those values.
        /// </summary>
        public static bool TryLoad(string path, out AppSettings settings)
        {
            settings = null;
            if (path == null) return false;
            try
            {
                string full = Path.GetFullPath(path);
                SafeUserFolder.Check(Path.GetDirectoryName(full), full);
                var info = new FileInfo(full);
                if (!info.Exists || info.Length > MaxFileBytes) return false;
                settings = Parse(File.ReadAllText(full, Utf8NoBom));
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
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

        /// <summary>
        /// Size and write time of settings.ini, after the same folder checks as Load. Never throws: any failure
        /// gives FileStamp.Empty, which equals no stamp, so a check it fails is never mistaken for "unchanged".
        /// </summary>
        public static FileStamp Stamp(string path)
        {
            if (path == null) return FileStamp.Empty;
            try
            {
                string full = Path.GetFullPath(path);
                SafeUserFolder.Check(Path.GetDirectoryName(full), full);
                var info = new FileInfo(full);
                return info.Exists ? new FileStamp(true, info.Length, info.LastWriteTimeUtc) : new FileStamp(false, 0, DateTime.MinValue);
            }
            catch (IOException) { return FileStamp.Empty; }
            catch (UnauthorizedAccessException) { return FileStamp.Empty; }
            catch (System.Security.SecurityException) { return FileStamp.Empty; }
            catch (ArgumentException) { return FileStamp.Empty; }
            catch (NotSupportedException) { return FileStamp.Empty; }
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

    /// <summary>What settings.ini looked like at one moment. Two stamps are equal only when both were read.</summary>
    public sealed class FileStamp
    {
        /// <summary>The stamp of a failed check: equal to nothing, itself included.</summary>
        public static readonly FileStamp Empty = new FileStamp(false, 0, DateTime.MinValue, false);

        public readonly bool Exists;
        public readonly long Length;
        public readonly DateTime LastWriteUtc;
        readonly bool read;

        public FileStamp(bool exists, long length, DateTime lastWriteUtc) : this(exists, length, lastWriteUtc, true) { }

        FileStamp(bool exists, long length, DateTime lastWriteUtc, bool read)
        {
            Exists = exists;
            Length = length;
            LastWriteUtc = lastWriteUtc;
            this.read = read;
        }

        public override bool Equals(object obj)
        {
            var o = obj as FileStamp;
            return o != null && read && o.read && Exists == o.Exists && Length == o.Length && LastWriteUtc == o.LastWriteUtc;
        }

        public override int GetHashCode()
        {
            return read ? LastWriteUtc.GetHashCode() ^ Length.GetHashCode() ^ (Exists ? 1 : 0) : 0;
        }
    }

    public enum SettingsSyncState { InSync, SaveFailed }

    public enum SettingsSyncEvent { LocalChange, SaveOk, SaveFailed, DiskChanged, ReloadOk, ReloadFailed }

    /// <summary>
    /// Whether the running settings are on disk (P12). A failed save leaves them in force for this run only; the
    /// next change or window activation saves again. A local change or a changed file decides nothing by itself:
    /// the save or reload that follows does. The slider's debounce stays in the UI's timer, outside this machine.
    /// </summary>
    public static class SettingsSync
    {
        public static SettingsSyncState Next(SettingsSyncState state, SettingsSyncEvent e)
        {
            switch (e)
            {
                case SettingsSyncEvent.SaveOk:
                case SettingsSyncEvent.ReloadOk:
                    return SettingsSyncState.InSync;
                case SettingsSyncEvent.SaveFailed:
                    return SettingsSyncState.SaveFailed;
                default:
                    return state;
            }
        }
    }

    /// <summary>
    /// Three-way merge of the app's settings with a settings.ini edited by hand while the app runs (P12): for each
    /// key, the app's value when the app changed it since the last load or save, else the file's. Keys come from
    /// Serialize, so a new setting is covered without code here, and the result goes back through Parse and
    /// Normalized, so a hand edit can never set more than the file could at start-up.
    /// </summary>
    public static class SettingsMerge
    {
        const string StartWithWindowsKey = "StartWithWindows";

        /// <summary>Key to value as Serialize writes them (normalized, PowerServices in catalog order).</summary>
        internal static Dictionary<string, string> ToMap(AppSettings settings)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new StringReader(SettingsStore.Serialize(settings)))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) map[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            return map;
        }

        /// <summary>
        /// baseline: what the app last loaded or saved; mine: the app's settings now; disk: the file now. Start with
        /// Windows is always mine: the logon task is its source of truth and a file must never steer it.
        /// </summary>
        public static AppSettings Merge(AppSettings baseline, AppSettings mine, AppSettings disk)
        {
            if (baseline == null) throw new ArgumentNullException("baseline");
            if (mine == null) throw new ArgumentNullException("mine");
            if (disk == null) throw new ArgumentNullException("disk");
            Dictionary<string, string> b = ToMap(baseline), m = ToMap(mine), d = ToMap(disk);
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in m)
            {
                string before, onDisk, value = kv.Value;
                bool mineChanged = !b.TryGetValue(kv.Key, out before) || !string.Equals(before, kv.Value, StringComparison.Ordinal);
                if (!mineChanged && !string.Equals(kv.Key, StartWithWindowsKey,StringComparison.OrdinalIgnoreCase)
                    && d.TryGetValue(kv.Key, out onDisk))
                    value = onDisk;
                sb.Append(kv.Key).Append('=').Append(value).Append("\r\n");
            }
            return SettingsStore.Parse(sb.ToString()).Normalized();
        }
    }
}
