using System;
using System.Collections.Generic;
using System.IO;

namespace RamOptimiser.Gui
{
    /// <summary>
    /// Power Clean's journal: the services it stopped, kept in stopped-services.txt next to settings.ini, so a
    /// crash, a kill or a sign-out before the exit restore does not lose them; the next elevated start restores
    /// them. The folder belongs to the non-elevated user, so the elevated app trusts nothing in the file: names
    /// outside ServiceCatalog are dropped (StoppedServiceJournal), a file over MaxFileBytes is not read, and the
    /// folder and file get the SafeUserFolder checks before every read and write.
    /// </summary>
    public sealed class StoppedServiceStore : IStoppedServiceJournal
    {
        /// <summary>A journal is a few hundred bytes; anything far larger is not ours and is not read.</summary>
        internal const long MaxFileBytes = 16 * 1024;
        static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);
        readonly string path;
        readonly object gate = new object();

        public StoppedServiceStore(string path)
        {
            if (path == null) throw new ArgumentNullException("path");
            this.path = Path.GetFullPath(path);
        }

        public static string DefaultPath
        {
            get { return Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath), "stopped-services.txt"); }
        }

        /// <summary>The journalled catalog names. Empty for a missing, empty, oversized, corrupt, unsafe or unreadable file.</summary>
        public static List<string> Load(string path)
        {
            List<string> names;
            TryLoad(path, out names);
            return names;
        }

        /// <summary>
        /// Reads the journal. False when the file could not be read (held open by another process, refused, an unsafe
        /// folder): its names are unknown, so Add and Remove must not write over it. A missing, empty, oversized or
        /// corrupt file reads as true, with whatever catalog names it holds (none of ours are lost: we never write those).
        /// </summary>
        public static bool TryLoad(string path, out List<string> names)
        {
            if (path == null) throw new ArgumentNullException("path");
            names = new List<string>();
            try
            {
                string full = Path.GetFullPath(path);
                SafeUserFolder.Check(Path.GetDirectoryName(full), full);
                var info = new FileInfo(full);
                if (!info.Exists || info.Length > MaxFileBytes) return true;
                names = StoppedServiceJournal.Parse(File.ReadAllText(full, Utf8NoBom));
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        public List<string> Read() { return Load(path); }

        /// <summary>
        /// Records a stopped service. False when the file could not be read or written (held open, an unsafe folder,
        /// a full disk); the caller then leaves the service running.
        /// </summary>
        public bool Add(string name)
        {
            lock (gate)
            {
                List<string> now;
                if (!TryLoad(path, out now)) return false;
                List<string> next = StoppedServiceJournal.With(now, name);
                return next.Count == now.Count || TryWrite(next);
            }
        }

        /// <summary>Forgets a service once it runs again; the file goes with the last name. False when it could not be read or written.</summary>
        public bool Remove(string name)
        {
            lock (gate)
            {
                List<string> now;
                if (!TryLoad(path, out now)) return false;
                List<string> next = StoppedServiceJournal.Without(now, name);
                if (next.Count == now.Count && (next.Count > 0 || !File.Exists(path))) return true;
                return TryWrite(next);
            }
        }

        bool TryWrite(List<string> names)
        {
            try
            {
                if (names.Count == 0) Delete(); else Write(StoppedServiceJournal.Serialize(names));
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        void Delete()
        {
            SafeUserFolder.Check(Path.GetDirectoryName(path), path);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Temp file, then a swap, with the SafeUserFolder checks before each step (as SettingsStore.Save).</summary>
        void Write(string text)
        {
            string dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            SafeUserFolder.Check(dir, path);
            string temp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = Utf8NoBom.GetBytes(text);
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                SafeUserFolder.Check(dir, path, temp);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp) && SafeUserFolder.IsSafe(dir, temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Exit's restore decision, free of WPF.</summary>
    public static class PowerCleanRecovery
    {
        /// <summary>
        /// How long Exit's restore thread waits for a clean in flight. A Power Clean takes the service lock before
        /// its first stop, and Restore waits on that lock for the rest, so this only has to cover the clean's start.
        /// </summary>
        public static readonly TimeSpan ExitCleanWait = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Restore on exit when this run stopped services, or when a clean in flight may still stop some: it is
        /// elevated and its areas include services (Power Clean). A memory-only clean is not waited for.
        /// </summary>
        public static bool NeedsExitRestore(int stoppedByApp, bool cleanRunning, bool cleanStopsServices)
        {
            return stoppedByApp > 0 || (cleanRunning && cleanStopsServices);
        }
    }
}
