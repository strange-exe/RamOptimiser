using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;

namespace RamOptimiser.Gui
{
    /// <summary>
    /// Power Clean's journal: the services it stopped, kept in HKLM\SOFTWARE\RamOptimiser\PowerClean\&lt;user SID&gt; as
    /// one REG_SZ value per service (the value name is the service name), so a crash, a kill or a sign-out before
    /// the exit restore does not lose them; the next elevated start restores them.
    ///
    /// It used to be stopped-services.txt in the user's data folder, which a non-elevated program of the same user
    /// can write: the elevated app then started whatever catalog services the file named, and deleted or replaced
    /// the file by path in a folder that program controls (audit run-4, records 5 and 14). Only administrators can
    /// write this key, and AppRegistry refuses it when its owner, its permissions or a registry link say otherwise;
    /// such a journal reads as empty (Read reports why) and takes no records (Add returns false, so Power Clean
    /// leaves the service running). The subkey is per user SID, as the old per-user file was, so one administrator's
    /// elevated start never restores services another administrator's session stopped. Names outside ServiceCatalog
    /// are still dropped on read (defence in depth), and only the first StoppedServiceJournal.MaxNames values count.
    /// </summary>
    public sealed class PowerCleanJournal : IStoppedServiceJournal
    {
        public const string KeyName = "PowerClean";
        readonly IMachineRegistry registry;
        readonly string[] path;
        readonly object gate = new object();

        public PowerCleanJournal(IMachineRegistry registry, string userSid)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            if (userSid == null) throw new ArgumentNullException("userSid");
            // The SID becomes a key name: only a well-formed SID, in its canonical text, so it cannot name another key.
            string sid = new SecurityIdentifier(userSid).Value;
            this.registry = registry;
            path = new[] { KeyName, sid };
        }

        /// <summary>The journal of the account this process runs as; null when the token has no user SID.</summary>
        public static PowerCleanJournal ForCurrentUser()
        {
            string sid;
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) sid = id.User == null ? null : id.User.Value;
            return sid == null ? null : new PowerCleanJournal(AppRegistry.Machine, sid);
        }

        /// <summary>HKLM\SOFTWARE\RamOptimiser\PowerClean\&lt;SID&gt;.</summary>
        public string KeyPath { get { return @"HKLM\SOFTWARE\" + AppRegistry.RootName + @"\" + string.Join(@"\", path); } }

        /// <summary>The journalled catalog names; empty when there are none or the key is refused.</summary>
        public List<string> Read()
        {
            string problem;
            return Read(out problem);
        }

        /// <summary>As Read; problem says why the journal could not be read (null when it was, or does not exist).</summary>
        public List<string> Read(out string problem)
        {
            lock (gate)
            {
                try
                {
                    using (IMachineKey key = AppRegistry.Open(registry, path, false, false, out problem))
                        return key == null ? new List<string>() : Names(key);
                }
                catch (UnauthorizedAccessException ex) { problem = KeyPath + " could not be read: " + ex.Message.TrimEnd('.'); }
                catch (IOException ex) { problem = KeyPath + " could not be read: " + ex.Message.TrimEnd('.'); }
                return new List<string>();
            }
        }

        /// <summary>
        /// Records a stopped service. False when the record could not be made (not elevated, a refused key, a full
        /// journal, a registry error); the caller then leaves the service running. A name outside the catalog is
        /// never written and returns true, as before.
        /// </summary>
        public bool Add(string name)
        {
            string n = name == null ? null : name.Trim();
            if (StoppedServiceJournal.Keep(new[] { n }).Count == 0) return true;
            lock (gate)
            {
                try
                {
                    string problem;
                    using (IMachineKey key = AppRegistry.Open(registry, path, true, true, out problem))
                    {
                        if (key == null) return false;
                        if (Names(key).Exists(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) return true;
                        // Read looks at the first MaxNames values only: a record past them could be lost.
                        if (key.ValueNames().Length >= StoppedServiceJournal.MaxNames) return false;
                        key.SetString(n, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                        return true;
                    }
                }
                catch (UnauthorizedAccessException) { return false; }
                catch (IOException) { return false; }
            }
        }

        /// <summary>Forgets a service once it runs again; the subkey goes with its last value. False when it could not be removed.</summary>
        public bool Remove(string name)
        {
            string n = name == null ? null : name.Trim();
            if (string.IsNullOrEmpty(n)) return true;
            lock (gate)
            {
                try
                {
                    string problem;
                    using (IMachineKey key = AppRegistry.Open(registry, path, true, false, out problem))
                    {
                        if (key == null) return problem == null; // no journal: nothing to forget
                        foreach (string v in key.ValueNames())
                            if (string.Equals(v.Trim(), n, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(v);
                        if (key.ValueNames().Length == 0 && key.SubKeyCount == 0) key.DeleteKey();
                        return true;
                    }
                }
                catch (UnauthorizedAccessException) { return false; }
                catch (IOException) { return false; }
            }
        }

        /// <summary>The catalog names among the key's first MaxNames values.</summary>
        static List<string> Names(IMachineKey key)
        {
            string[] all = key.ValueNames();
            var first = new string[Math.Min(all.Length, StoppedServiceJournal.MaxNames)];
            Array.Copy(all, first, first.Length);
            return StoppedServiceJournal.Keep(first);
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
