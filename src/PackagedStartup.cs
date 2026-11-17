using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Microsoft.Win32;

namespace RamOptimiser
{
    /// <summary>
    /// Mirrors Windows.ApplicationModel.StartupTaskState (learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptaskstate):
    /// the same five values, so the registry's State DWORD maps across unchanged.
    /// </summary>
    public enum PackagedState { Disabled = 0, DisabledByUser = 1, Enabled = 2, DisabledByPolicy = 3, EnabledByPolicy = 4 }

    /// <summary>
    /// Startup tasks of packaged (Store / MSIX) apps, listed read-only. The StartupTask WinRT API only answers for
    /// the caller's own package, so the states are read where Windows keeps them for this user:
    /// HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\
    /// {PackageFamilyName}\{TaskId}, DWORD "State". That layout is not documented: observed on this PC on
    /// 2026-11-17 (e.g. WhatsApp State 2, Claude State 0), the same "may change" status as the StartupApproved
    /// format in Startup.cs. If Windows changes it, these rows simply disappear. Nothing here writes, runs or
    /// resolves anything; the manifest path comes from the OS (GetPackagePathByFullName), never from the registry.
    /// </summary>
    public static class PackagedStartup
    {
        const string SystemAppDataPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
        const int MaxFamilies = 2000, MaxTasksPerFamily = 64, MaxNameLength = 260, MaxPackagesPerFamily = 8;
        /// <summary>Package full names are at most 127 characters (PACKAGE_FULL_NAME_MAX_LENGTH); room for 8 with NULs.</summary>
        const int MaxFullNamesChars = MaxPackagesPerFamily * 128;
        const int MaxManifestBytes = 1024 * 1024;
        const int MaxDisplayName = 200;
        const int ErrorSuccess = 0, ErrorInsufficientBuffer = 122;

        public static bool IsOn(PackagedState s) { return s == PackagedState.Enabled || s == PackagedState.EnabledByPolicy; }

        public static bool IsPolicy(PackagedState s) { return s == PackagedState.DisabledByPolicy || s == PackagedState.EnabledByPolicy; }

        /// <summary>A registry DWORD from 0 to 4 is a state; any other value or type means the layout is not what we know. Pure.</summary>
        public static bool TryParseState(object value, out PackagedState state)
        {
            state = PackagedState.Disabled;
            if (!(value is int)) return false;
            int v = (int)value;
            if (v < 0 || v > 4) return false;
            state = (PackagedState)v;
            return true;
        }

        /// <summary>The rows for this user's packaged startup tasks. Never throws for registry, package or file errors.</summary>
        public static List<StartupApp> Read()
        {
            var list = new List<StartupApp>();
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(SystemAppDataPath))
                {
                    if (root == null) return list;
                    string[] families = root.GetSubKeyNames();
                    for (int i = 0; i < families.Length && i < MaxFamilies; i++)
                    {
                        string family = families[i];
                        if (string.IsNullOrEmpty(family) || family.Length > MaxNameLength) continue;
                        List<KeyValuePair<string, PackagedState>> tasks = TasksOf(root, family);
                        if (tasks.Count == 0) continue;
                        AddFamily(list, family, tasks);
                    }
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            catch (EntryPointNotFoundException) { } // GetPackagePathByFullName needs Windows 8.1
            catch (DllNotFoundException) { }
            return list;
        }

        static List<KeyValuePair<string, PackagedState>> TasksOf(RegistryKey root, string family)
        {
            var tasks = new List<KeyValuePair<string, PackagedState>>();
            try
            {
                using (RegistryKey fam = root.OpenSubKey(family))
                {
                    if (fam == null) return tasks;
                    string[] ids = fam.GetSubKeyNames();
                    for (int i = 0; i < ids.Length && i < MaxTasksPerFamily; i++)
                    {
                        if (string.IsNullOrEmpty(ids[i]) || ids[i].Length > MaxNameLength) continue;
                        using (RegistryKey task = fam.OpenSubKey(ids[i]))
                        {
                            if (task == null) continue;
                            PackagedState state; // a REG_DWORD reads as int; any other kind is refused by TryParseState
                            if (TryParseState(task.GetValue("State"), out state))
                                tasks.Add(new KeyValuePair<string, PackagedState>(ids[i], state));
                        }
                    }
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return tasks;
        }

        static void AddFamily(List<StartupApp> list, string family, List<KeyValuePair<string, PackagedState>> tasks)
        {
            var manifests = new List<KeyValuePair<string, string>>(); // package folder, manifest text
            foreach (string fullName in PackagesOf(family))
            {
                string folder = PackageFolder(fullName);
                if (folder == null) continue;
                string xml = ReadManifest(folder);
                if (xml != null) manifests.Add(new KeyValuePair<string, string>(folder, xml));
            }
            foreach (KeyValuePair<string, PackagedState> task in tasks)
            {
                foreach (KeyValuePair<string, string> m in manifests)
                {
                    string executable, displayName;
                    if (!FindStartupTask(m.Value, task.Key, out executable, out displayName)) continue;
                    string exe = PackagedExePath(m.Key, executable);
                    list.Add(new StartupApp
                    {
                        Source = StartupSource.Packaged,
                        Name = DisplayNameFor(displayName, family),
                        PackageFamily = family,
                        Packaged = task.Value,
                        PolicySet = IsPolicy(task.Value),
                        ExePath = exe,
                        ExeName = exe == null ? null : Path.GetFileName(exe),
                    });
                    break;
                }
            }
        }

        static string ReadManifest(string folder)
        {
            string file;
            try { file = Path.Combine(folder, "AppxManifest.xml"); }
            catch (ArgumentException) { return null; }
            byte[] bytes = StartupApps.ReadRegularFile(file, MaxManifestBytes);
            if (bytes == null) return null;
            try
            {
                // XmlReader honours the encoding declaration and BOM itself when given a stream.
                using (var ms = new MemoryStream(bytes))
                using (var reader = new StreamReader(ms, Encoding.UTF8, true)) return reader.ReadToEnd();
            }
            catch (DecoderFallbackException) { return null; }
        }

        /// <summary>
        /// Finds the StartupTask element (uap5: or desktop:, matched by local name) with this TaskId and returns the
        /// Executable of its parent Extension and its DisplayName. DTDs are refused and nothing is resolved. Pure.
        /// </summary>
        public static bool FindStartupTask(string manifestXml, string taskId, out string executable, out string displayName)
        {
            executable = null; displayName = null;
            if (string.IsNullOrEmpty(manifestXml) || string.IsNullOrEmpty(taskId)) return false;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 2000000,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            try
            {
                using (var text = new StringReader(manifestXml))
                using (XmlReader r = XmlReader.Create(text, settings))
                {
                    // The Executable of each open Extension element, innermost last, with its depth.
                    var extensions = new List<KeyValuePair<int, string>>();
                    while (r.Read())
                    {
                        if (r.NodeType == XmlNodeType.EndElement)
                        {
                            if (r.LocalName == "Extension" && extensions.Count > 0 && extensions[extensions.Count - 1].Key == r.Depth)
                                extensions.RemoveAt(extensions.Count - 1);
                            continue;
                        }
                        if (r.NodeType != XmlNodeType.Element) continue;
                        if (r.LocalName == "Extension")
                        {
                            if (!r.IsEmptyElement) extensions.Add(new KeyValuePair<int, string>(r.Depth, r.GetAttribute("Executable")));
                            continue;
                        }
                        if (r.LocalName != "StartupTask") continue;
                        if (!string.Equals(r.GetAttribute("TaskId"), taskId, StringComparison.OrdinalIgnoreCase)) continue;
                        // The task belongs to the Extension it sits directly inside.
                        if (extensions.Count == 0 || extensions[extensions.Count - 1].Key != r.Depth - 1) return false;
                        string exe = extensions[extensions.Count - 1].Value;
                        if (string.IsNullOrEmpty(exe)) return false;
                        executable = exe;
                        displayName = r.GetAttribute("DisplayName");
                        return true;
                    }
                }
            }
            catch (XmlException) { }
            catch (ArgumentException) { }
            executable = null; displayName = null;
            return false;
        }

        /// <summary>The package folder joined with the manifest's Executable, or null when that lands outside the folder. Pure.</summary>
        public static string PackagedExePath(string folder, string executable)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(executable)) return null;
            string exe;
            try { exe = Path.Combine(folder, executable); }
            catch (ArgumentException) { return null; }
            if (!StartupApps.IsInside(exe, folder)) return null;
            try { return Path.GetFullPath(exe); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        /// <summary>
        /// The manifest's DisplayName, or for an "ms-resource:" reference (or none) the family name's part between
        /// its first '.' and its '_' (5319275A.WhatsAppDesktop_cv1g1gvanyjgm reads "WhatsAppDesktop"). Pure.
        /// </summary>
        public static string DisplayNameFor(string displayName, string family)
        {
            if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                string d = displayName.Trim();
                return d.Length > MaxDisplayName ? d.Substring(0, MaxDisplayName) : d;
            }
            if (string.IsNullOrEmpty(family)) return family;
            int underscore = family.IndexOf('_');
            string name = underscore > 0 ? family.Substring(0, underscore) : family;
            int dot = name.IndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
            return name;
        }

        // ------------------------------------------------------------------ appmodel.h (kernel32)

        /// <summary>The installed packages of a family for this user (at most 8 full names).</summary>
        static List<string> PackagesOf(string family)
        {
            var names = new List<string>();
            uint count = 0, length = 0;
            int rc = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
            if (rc == ErrorSuccess || rc != ErrorInsufficientBuffer) return names; // success with nothing to fill: none installed
            if (count == 0 || count > MaxPackagesPerFamily || length == 0 || length > MaxFullNamesChars) return names;
            IntPtr pointers = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            IntPtr buffer = Marshal.AllocHGlobal((int)length * 2);
            try
            {
                uint count2 = count, length2 = length;
                if (GetPackagesByPackageFamily(family, ref count2, pointers, ref length2, buffer) != ErrorSuccess) return names;
                long start = buffer.ToInt64(), end = start + (long)length * 2;
                for (int i = 0; i < count2 && i < count; i++)
                {
                    IntPtr p = Marshal.ReadIntPtr(pointers, i * IntPtr.Size);
                    long at = p.ToInt64();
                    if (at < start || at >= end) continue; // only pointers into our own buffer are read
                    // The NUL must lie inside the buffer too: scan for it rather than trusting it is there.
                    int maxChars = (int)((end - at) / 2), chars = -1;
                    for (int k = 0; k < maxChars && k <= MaxNameLength; k++)
                        if (Marshal.ReadInt16(p, k * 2) == 0) { chars = k; break; }
                    if (chars > 0) names.Add(Marshal.PtrToStringUni(p, chars));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(pointers);
            }
            return names;
        }

        static string PackageFolder(string fullName)
        {
            uint length = 0;
            int rc = GetPackagePathByFullName(fullName, ref length, null);
            if (rc != ErrorInsufficientBuffer || length == 0 || length > 32767) return null;
            var sb = new StringBuilder((int)length);
            if (GetPackagePathByFullName(fullName, ref length, sb) != ErrorSuccess) return null;
            string folder = sb.ToString();
            return string.IsNullOrEmpty(folder) ? null : folder;
        }

        // learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagesbypackagefamily (Windows 8)
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, IntPtr packageFullNames,
            ref uint bufferLength, IntPtr buffer);

        // learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagepathbyfullname (Windows 8.1)
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder path);
    }
}
