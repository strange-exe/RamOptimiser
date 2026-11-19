// Tests for the shell's state machines (package 1D): the elevation handoff (P10) and settings sync with a
// three-way merge and an honest save-failed state (P12). Pure decisions only: no UAC prompt is ever shown,
// no real lock name is used and no real settings.ini is touched.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class ShellStateTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        public static int Run()
        {
            ElevationFlowTests();
            RelaunchAsyncTests();
            AcquireAfterTests();
            MergeTests();
            StampTests();
            TryLoadTests();
            SyncTests();
            DisclosureTests();
            return failed;
        }

        // ---------------------------------------------------------------- P10 elevation handoff

        static void Row(ElevationState from, ElevationEvent e, ElevationState to, ElevationAction action)
        {
            ElevationStep step = ElevationFlow.Next(from, e);
            Check("elevation: " + from + " + " + e + " -> " + to + "/" + action, step != null && step.State == to && step.Action == action,
                step == null ? "null" : step.State + "/" + step.Action);
        }

        static void ElevationFlowTests()
        {
            Row(ElevationState.Idle, ElevationEvent.Requested, ElevationState.Prompting, ElevationAction.StartPrompt);
            Row(ElevationState.Prompting, ElevationEvent.Requested, ElevationState.Prompting, ElevationAction.Ignore);
            Row(ElevationState.Prompting, ElevationEvent.Launched, ElevationState.Exiting, ElevationAction.ExitApp);
            Row(ElevationState.Prompting, ElevationEvent.Cancelled, ElevationState.Idle, ElevationAction.None);
            Row(ElevationState.Prompting, ElevationEvent.Failed, ElevationState.Idle, ElevationAction.ShowError);
            Row(ElevationState.Idle, ElevationEvent.ExitRequested, ElevationState.Exiting, ElevationAction.None);
            Row(ElevationState.Prompting, ElevationEvent.ExitRequested, ElevationState.Exiting, ElevationAction.None);
            foreach (ElevationEvent e in new[] { ElevationEvent.Requested, ElevationEvent.Launched, ElevationEvent.Cancelled,
                         ElevationEvent.Failed, ElevationEvent.ExitRequested })
                Row(ElevationState.Exiting, e, ElevationState.Exiting, ElevationAction.Ignore);
            // A late result (after the prompt was already settled) changes nothing.
            Row(ElevationState.Idle, ElevationEvent.Launched, ElevationState.Idle, ElevationAction.Ignore);
            Row(ElevationState.Idle, ElevationEvent.Cancelled, ElevationState.Idle, ElevationAction.Ignore);
            Row(ElevationState.Idle, ElevationEvent.Failed, ElevationState.Idle, ElevationAction.Ignore);

            Check("handoff argument", Elevation.HandoffArgs(1234) == "--handoff=1234", Elevation.HandoffArgs(1234));
            bool refused = false;
            try { Elevation.HandoffArgs(0); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            Check("handoff argument refuses a pid of 0", refused);
        }

        /// <summary>The worker is driven by a fake launcher: these tests never reach ShellExecute or UAC.</summary>
        static void RelaunchAsyncTests()
        {
            int caller = Thread.CurrentThread.ManagedThreadId;
            int workerId = -1;
            ApartmentState apartment = ApartmentState.Unknown;
            string threadName = null;
            bool background = false;
            bool? ok = null;
            Exception error = null;
            Thread t = Elevation.RelaunchAsync(() =>
            {
                workerId = Thread.CurrentThread.ManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
                threadName = Thread.CurrentThread.Name;
                background = Thread.CurrentThread.IsBackground;
                return true;
            }, (o, ex) => { ok = o; error = ex; });
            Check("relaunch: worker finishes", t.Join(5000));
            Check("relaunch: runs off the calling thread", workerId != -1 && workerId != caller);
            Check("relaunch: STA thread named elevate", apartment == ApartmentState.STA && threadName == "elevate", apartment + " " + threadName);
            Check("relaunch: background thread", background);
            Check("relaunch: launched reports true", ok == true && error == null);

            ok = null; error = null;
            t = Elevation.RelaunchAsync(() => false, (o, ex) => { ok = o; error = ex; });
            Check("relaunch: cancel reports false without an error", t.Join(5000) && ok == false && error == null);

            ok = null; error = null;
            var boom = new InvalidOperationException("boom");
            t = Elevation.RelaunchAsync(() => { throw boom; }, (o, ex) => { ok = o; error = ex; });
            Check("relaunch: failure reports the exception", t.Join(5000) && ok == false && ReferenceEquals(error, boom));

            // A throwing callback must not end the process (it runs on the worker thread).
            t = Elevation.RelaunchAsync(() => true, (o, ex) => { throw new InvalidOperationException("callback"); });
            Check("relaunch: a throwing callback is contained", t.Join(5000));
        }

        static void AcquireAfterTests()
        {
            // (a) The holder is released by another thread after 200 ms: the result is first, well within 2 s.
            string id = "test-" + Guid.NewGuid().ToString("N");
            var holder = new SingleInstance(id);
            using (var released = new ManualResetEvent(false))
            {
                var t = new Thread(() => { Thread.Sleep(200); holder.Dispose(); released.Set(); }) { IsBackground = true };
                var watch = Stopwatch.StartNew();
                t.Start();
                using (SingleInstance got = SingleInstance.AcquireAfter(() => new SingleInstance(id), released, TimeSpan.FromSeconds(10)))
                {
                    watch.Stop();
                    Check("acquire after: first once the holder lets go", holder != null && got.IsFirstInstance);
                    Check("acquire after: within 2 s", watch.ElapsedMilliseconds < 2000, watch.ElapsedMilliseconds + " ms");
                }
                t.Join(2000);
            }

            // (b) Never released, timeout 300 ms: not first, after at least 300 ms.
            string id2 = "test-" + Guid.NewGuid().ToString("N");
            using (var keeper = new SingleInstance(id2))
            using (var never = new ManualResetEvent(false))
            {
                var watch = Stopwatch.StartNew();
                using (SingleInstance got = SingleInstance.AcquireAfter(() => new SingleInstance(id2), never, TimeSpan.FromMilliseconds(300)))
                {
                    watch.Stop();
                    Check("acquire after: still not first when never released", keeper.IsFirstInstance && !got.IsFirstInstance);
                    Check("acquire after: waits the full timeout", watch.ElapsedMilliseconds >= 290, watch.ElapsedMilliseconds + " ms");
                }
            }

            // (c) Already released: immediate.
            string id3 = "test-" + Guid.NewGuid().ToString("N");
            new SingleInstance(id3).Dispose();
            using (var done = new ManualResetEvent(true))
            {
                var watch = Stopwatch.StartNew();
                using (SingleInstance got = SingleInstance.AcquireAfter(() => new SingleInstance(id3), done, TimeSpan.FromSeconds(10)))
                {
                    watch.Stop();
                    Check("acquire after: first at once when free", got.IsFirstInstance);
                    Check("acquire after: immediate", watch.ElapsedMilliseconds < 1000, watch.ElapsedMilliseconds + " ms");
                }
            }

            // (d) Held but the release is already signalled: one more try, no wait, not first.
            string id4 = "test-" + Guid.NewGuid().ToString("N");
            using (var keeper = new SingleInstance(id4))
            using (var done = new ManualResetEvent(true))
            {
                var watch = Stopwatch.StartNew();
                using (SingleInstance got = SingleInstance.AcquireAfter(() => new SingleInstance(id4), done, TimeSpan.FromSeconds(10)))
                {
                    watch.Stop();
                    Check("acquire after: a signalled release does not wait", !got.IsFirstInstance && watch.ElapsedMilliseconds < 1000,
                        watch.ElapsedMilliseconds + " ms");
                }
            }
        }

        // ---------------------------------------------------------------- P12 settings merge, stamp and sync

        static void MergeTests()
        {
            var baseline = new AppSettings();
            var mine = baseline.Clone();
            mine.ThresholdPercent = 90;
            AppSettings merged = SettingsMerge.Merge(baseline, mine, baseline.Clone());
            Check("merge: my changed key wins", merged.ThresholdPercent == 90, merged.ThresholdPercent.ToString());

            var disk = baseline.Clone();
            disk.Theme = ThemeMode.Dark;
            merged = SettingsMerge.Merge(baseline, mine, disk);
            Check("merge: disk edit of another key kept", merged.Theme == ThemeMode.Dark && merged.ThresholdPercent == 90,
                merged.Theme + " " + merged.ThresholdPercent);

            disk = baseline.Clone();
            disk.ThresholdPercent = 70;
            merged = SettingsMerge.Merge(baseline, mine, disk);
            Check("merge: same key changed in both, mine wins", merged.ThresholdPercent == 90, merged.ThresholdPercent.ToString());

            disk = baseline.Clone();
            disk.StartWithWindows = true;
            merged = SettingsMerge.Merge(baseline, baseline.Clone(), disk);
            Check("merge: StartWithWindows always mine", !merged.StartWithWindows);
            var startMine = baseline.Clone();
            startMine.StartWithWindows = true;
            merged = SettingsMerge.Merge(startMine, startMine.Clone(), baseline.Clone());
            Check("merge: StartWithWindows mine even when unchanged", merged.StartWithWindows);

            string a = ServiceCatalog.Rules[0].Name, b = ServiceCatalog.Rules[1].Name;
            var svcBase = new AppSettings { PowerServices = new List<string> { a, b } };
            var svcMine = svcBase.Clone();
            svcMine.PowerServices = new List<string> { b, a };
            var svcDisk = svcBase.Clone();
            svcDisk.PowerServices = new List<string> { a };
            merged = SettingsMerge.Merge(svcBase, svcMine, svcDisk);
            Check("merge: PowerServices reorder is not a change", merged.PowerServices.Count == 1 && merged.PowerServices[0] == a,
                string.Join(",", merged.PowerServices.ToArray()));

            disk = SettingsStore.Parse("CooldownSeconds=5\r\nThresholdPercent=60\r\n");
            merged = SettingsMerge.Merge(baseline, baseline.Clone(), disk);
            Check("merge: result is normalized", merged.CooldownSeconds == AppSettings.MinCooldownSeconds && merged.ThresholdPercent == 60,
                merged.CooldownSeconds + " " + merged.ThresholdPercent);
            var wild = baseline.Clone();
            wild.ThresholdPercent = 5;
            merged = SettingsMerge.Merge(baseline, wild, baseline.Clone());
            Check("merge: an out-of-range value of mine is normalized", merged.ThresholdPercent == AppSettings.DefaultThresholdPercent,
                merged.ThresholdPercent.ToString());

            disk = SettingsStore.Parse("Theme=light\r\nSurprise=yes\r\nPowerServices=NotAService\r\n");
            merged = SettingsMerge.Merge(baseline, baseline.Clone(), disk);
            string text = SettingsStore.Serialize(merged);
            Check("merge: unknown keys in the file are dropped", merged.Theme == ThemeMode.Light && !text.Contains("Surprise")
                && !text.Contains("NotAService") && merged.PowerServices.Count == 0, text);

            Dictionary<string, string> map = SettingsMerge.ToMap(new AppSettings { ThresholdPercent = 77 });
            Check("merge map: every serialized key", map.Count == 14 && map["thresholdpercent"] == "77" && map.ContainsKey("PowerServices"),
                map.Count.ToString());
            Check("merge: inputs are not changed", mine.ThresholdPercent == 90 && baseline.ThresholdPercent == AppSettings.DefaultThresholdPercent);
        }

        static void StampTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ramopt-stamp-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "settings.ini");
            try
            {
                FileStamp missing = SettingsStore.Stamp(path);
                Check("stamp: missing file", !missing.Exists && missing.Equals(SettingsStore.Stamp(path)));
                SettingsStore.Save(new AppSettings { ThresholdPercent = 60 }, path);
                FileStamp first = SettingsStore.Stamp(path);
                Check("stamp: missing file differs from any real stamp", first.Exists && !missing.Equals(first) && !first.Equals(missing));
                Check("stamp: stable without a change", first.Equals(SettingsStore.Stamp(path)));
                Thread.Sleep(20);
                SettingsStore.Save(new AppSettings { ThresholdPercent = 61 }, path);
                FileStamp second = SettingsStore.Stamp(path);
                Check("stamp: changes after Save", !second.Equals(first), first.LastWriteUtc.Ticks + " vs " + second.LastWriteUtc.Ticks);
                Check("stamp: an empty stamp never equals a real one", !FileStamp.Empty.Equals(second) && !second.Equals(FileStamp.Empty)
                    && !FileStamp.Empty.Equals(FileStamp.Empty) && !FileStamp.Empty.Exists);
                Check("stamp: never throws on a bad path", !SettingsStore.Stamp(dir + "\\bad\0name.ini").Exists
                    && !SettingsStore.Stamp(dir + "\\bad\0name.ini").Equals(missing));
                Check("stamp: equals rejects other objects", !first.Equals("x") && !first.Equals(null));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        /// <summary>
        /// A file that could not be read is not a file of defaults: a reload or merge from it would reset every
        /// setting in the running app and then write those defaults over the user's file.
        /// </summary>
        static void TryLoadTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ramopt-tryload-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "settings.ini");
            try
            {
                AppSettings got;
                Check("try load: a missing file is not read", !SettingsStore.TryLoad(path, out got) && got == null);
                SettingsStore.Save(new AppSettings { ThresholdPercent = 61 }, path);
                Check("try load: a good file is read", SettingsStore.TryLoad(path, out got) && got != null && got.ThresholdPercent == 61,
                    got == null ? "null" : got.ThresholdPercent.ToString());
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    Check("try load: a file held open by another program is not read", !SettingsStore.TryLoad(path, out got) && got == null);
                File.WriteAllText(path, "ThresholdPercent=62\n" + new string('#', (int)SettingsStore.MaxFileBytes));
                Check("try load: an oversize file is not read", !SettingsStore.TryLoad(path, out got) && got == null);
                Check("try load: Load still gives defaults for it", SettingsStore.Load(path).ThresholdPercent == AppSettings.DefaultThresholdPercent);
                Check("try load: a bad path is not read", !SettingsStore.TryLoad(dir + "\\bad\0name.ini", out got) && got == null);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        static void DisclosureTests()
        {
            const Areas hidden = Areas.ModifiedList | Areas.CombinePages;
            Check("disclosure: a hidden area newly on opens it", RamOptimiser.App.AreasDisclosure.ShouldReveal(Areas.WorkingSets,
                Areas.WorkingSets | Areas.ModifiedList, hidden));
            Check("disclosure: a hidden area already on leaves it as the user set it", !RamOptimiser.App.AreasDisclosure.ShouldReveal(
                Areas.ModifiedList, Areas.ModifiedList, hidden));
            Check("disclosure: a shown area newly on leaves it", !RamOptimiser.App.AreasDisclosure.ShouldReveal(Areas.ModifiedList,
                Areas.ModifiedList | Areas.WorkingSets, hidden));
            Check("disclosure: a hidden area turned off leaves it", !RamOptimiser.App.AreasDisclosure.ShouldReveal(
                Areas.ModifiedList | Areas.CombinePages, Areas.ModifiedList, hidden));
        }

        static void SyncRow(SettingsSyncState from, SettingsSyncEvent e, SettingsSyncState to)
        {
            SettingsSyncState got = SettingsSync.Next(from, e);
            Check("sync: " + from + " + " + e + " -> " + to, got == to, got.ToString());
        }

        static void SyncTests()
        {
            SyncRow(SettingsSyncState.InSync, SettingsSyncEvent.LocalChange, SettingsSyncState.InSync);
            SyncRow(SettingsSyncState.InSync, SettingsSyncEvent.SaveOk, SettingsSyncState.InSync);
            SyncRow(SettingsSyncState.InSync, SettingsSyncEvent.SaveFailed, SettingsSyncState.SaveFailed);
            SyncRow(SettingsSyncState.InSync, SettingsSyncEvent.DiskChanged, SettingsSyncState.InSync);
            SyncRow(SettingsSyncState.InSync, SettingsSyncEvent.ReloadOk, SettingsSyncState.InSync);
            SyncRow(SettingsSyncState.InSync, SettingsSyncEvent.ReloadFailed, SettingsSyncState.InSync);
            SyncRow(SettingsSyncState.SaveFailed, SettingsSyncEvent.LocalChange, SettingsSyncState.SaveFailed);
            SyncRow(SettingsSyncState.SaveFailed, SettingsSyncEvent.SaveOk, SettingsSyncState.InSync);
            SyncRow(SettingsSyncState.SaveFailed, SettingsSyncEvent.SaveFailed, SettingsSyncState.SaveFailed);
            SyncRow(SettingsSyncState.SaveFailed, SettingsSyncEvent.DiskChanged, SettingsSyncState.SaveFailed);
            SyncRow(SettingsSyncState.SaveFailed, SettingsSyncEvent.ReloadOk, SettingsSyncState.InSync);
            SyncRow(SettingsSyncState.SaveFailed, SettingsSyncEvent.ReloadFailed, SettingsSyncState.SaveFailed);
        }
    }
}
