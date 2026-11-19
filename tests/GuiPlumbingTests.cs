// Tests for the GUI's non-visual plumbing (settings, auto-clean service, startup task arguments,
// single instance, tray icon rendering). No window is created and nothing is cleaned for real.
using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class GuiPlumbingTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        static void Throws<T>(string name, Action a) where T : Exception
        {
            try { a(); Check(name, false, "no exception"); }
            catch (T) { Check(name, true); }
            catch (Exception ex) { Check(name, false, "threw " + ex.GetType().Name); }
        }

        /// <summary>Runs every GUI plumbing test and returns the number of failures.</summary>
        public static int Run()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ramopt-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                SettingsTests(dir);
                SafeFolderTests(dir);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
            ExitRestoreTests();
            DecideTests();
            ServiceTests();
            SnapshotTests();
            HoldTests();
            IdleWaitTests();
            StartupTaskTests();
            ElevationTests();
            SingleInstanceTests();
            TrayIconTests();
            return failed;
        }

        // ---------------------------------------------------------------- settings

        static void SettingsTests(string dir)
        {
            var d = new AppSettings();
            Check("settings defaults", !d.AutoCleanEnabled && d.ThresholdPercent == 85 && d.IntervalMinutes == 0
                && d.CooldownSeconds == 300 && d.Areas == Areas.Default && d.Theme == ThemeMode.System
                && !d.StartWithWindows && d.CloseToTray && d.ShowTrayPercent);
            Check("default path", SettingsStore.DefaultPath.EndsWith(Path.Combine("RamOptimiser", "settings.ini"), StringComparison.OrdinalIgnoreCase)
                && Path.IsPathRooted(SettingsStore.DefaultPath), SettingsStore.DefaultPath);

            string path = Path.Combine(dir, "settings.ini");
            Check("missing file -> defaults", Same(SettingsStore.Load(path), d));

            var custom = new AppSettings
            {
                AutoCleanEnabled = true, ThresholdPercent = 70, IntervalMinutes = 30, CooldownSeconds = 120,
                Areas = Areas.WorkingSets | Areas.StandbyList, Theme = ThemeMode.Dark, StartWithWindows = true,
                CloseToTray = false, ShowTrayPercent = false
            };
            SettingsStore.Save(custom, path);
            Check("settings round-trip", Same(SettingsStore.Load(path), custom), File.ReadAllText(path));
            Check("areas stored as names", File.ReadAllText(path).Contains("Areas=workingsets,standby\r\n"));
            Check("default areas list", SettingsStore.Serialize(d).Contains("Areas=combine,workingsets,filecache,modified,standby-low,registry\r\n"));

            // Second save replaces the first; neither may leave its temp file behind.
            custom.ThresholdPercent = 60;
            SettingsStore.Save(custom, path);
            string[] files = Directory.GetFiles(dir);
            Check("atomic save leaves no temp file", files.Length == 1 && Path.GetFileName(files[0]) == "settings.ini",
                string.Join(", ", files));
            Check("second save replaced the value", SettingsStore.Load(path).ThresholdPercent == 60);

            var junk = new byte[4096];
            new Random(7).NextBytes(junk);
            File.WriteAllBytes(path, junk);
            Check("corrupt file -> defaults", Same(SettingsStore.Load(path), d));

            File.WriteAllText(path, "ThresholdPercent=120\nIntervalMinutes=-1\nCooldownSeconds=999999\nAreas=ram\n"
                + "Theme=purple\nAutoCleanEnabled=maybe\nCloseToTray=\n=5\nnot a setting\n");
            Check("out-of-range -> defaults", Same(SettingsStore.Load(path), d));
            Check("threshold bounds", SettingsStore.Parse("ThresholdPercent=49").ThresholdPercent == 85
                && SettingsStore.Parse("ThresholdPercent=50").ThresholdPercent == 50
                && SettingsStore.Parse("ThresholdPercent=99").ThresholdPercent == 99
                && SettingsStore.Parse("ThresholdPercent=100").ThresholdPercent == 85);
            Check("interval bounds", SettingsStore.Parse("IntervalMinutes=1440").IntervalMinutes == 1440
                && SettingsStore.Parse("IntervalMinutes=1441").IntervalMinutes == 0);
            Check("number with junk -> default", SettingsStore.Parse("ThresholdPercent=7O").ThresholdPercent == 85
                && SettingsStore.Parse("ThresholdPercent=70.5").ThresholdPercent == 85);

            // A file the medium user can write must not make an elevated instance clean back to back.
            int minCooldown = AppSettings.MinCooldownSeconds, defaultCooldown = AppSettings.DefaultCooldownSeconds;
            Check("cooldown: minimum is 60 s, default stays 300 s", minCooldown == 60 && defaultCooldown == 300
                && new AppSettings().CooldownSeconds == 300);
            Check("cooldown floor: file values below 60 become 60", SettingsStore.Parse("CooldownSeconds=0").CooldownSeconds == 60
                && SettingsStore.Parse("CooldownSeconds=59").CooldownSeconds == 60
                && SettingsStore.Parse("CooldownSeconds=-5").CooldownSeconds == 60
                && SettingsStore.Parse("CooldownSeconds=60").CooldownSeconds == 60
                && SettingsStore.Parse("CooldownSeconds=61").CooldownSeconds == 61,
                "0 -> " + SettingsStore.Parse("CooldownSeconds=0").CooldownSeconds);
            Check("cooldown floor: Normalized raises low values to 60", new AppSettings { CooldownSeconds = 0 }.Normalized().CooldownSeconds == 60
                && new AppSettings { CooldownSeconds = -1 }.Normalized().CooldownSeconds == 60
                && new AppSettings { CooldownSeconds = 86400 }.Normalized().CooldownSeconds == 86400
                && new AppSettings { CooldownSeconds = 86401 }.Normalized().CooldownSeconds == 300);
            Check("cooldown floor: a 0 in memory is saved as 60", SettingsStore.Serialize(new AppSettings { CooldownSeconds = 0 }).Contains("CooldownSeconds=60\r\n"));

            AppSettings mixed = SettingsStore.Parse("# comment\r\nFoo=bar\r\n  thresholdpercent = 70  \r\nTheme=LIGHT\r\n");
            Check("unknown key ignored, known keys kept", mixed.ThresholdPercent == 70 && mixed.Theme == ThemeMode.Light
                && mixed.CooldownSeconds == 300);
            Check("empty areas -> default", SettingsStore.Parse("Areas=").Areas == Areas.Default
                && SettingsStore.Parse("Areas= , ").Areas == Areas.Default);
            Check("areas none in memory saves as default", SettingsStore.Parse(SettingsStore.Serialize(new AppSettings { Areas = Areas.None })).Areas == Areas.Default);

            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                // Turkish lower-cases I to a dotless i, which breaks culture-sensitive key matching.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
                Check("invariant parse under tr-TR", SettingsStore.Parse("INTERVALMINUTES=15").IntervalMinutes == 15);
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
                Check("invariant serialize under ar-SA", SettingsStore.Serialize(custom).Contains("ThresholdPercent=60\r\n"));
            }
            finally { Thread.CurrentThread.CurrentCulture = saved; }

            Throws<ArgumentNullException>("save null settings", () => SettingsStore.Save(null, path));
        }

        static bool Same(AppSettings a, AppSettings b)
        {
            return a.AutoCleanEnabled == b.AutoCleanEnabled && a.ThresholdPercent == b.ThresholdPercent
                && a.IntervalMinutes == b.IntervalMinutes && a.CooldownSeconds == b.CooldownSeconds
                && a.Areas == b.Areas && a.Theme == b.Theme && a.StartWithWindows == b.StartWithWindows
                && a.CloseToTray == b.CloseToTray && a.ShowTrayPercent == b.ShowTrayPercent;
        }

        // ---------------------------------------------------------------- SafeUserFolder (links in the settings folder)

        static void SafeFolderTests(string dir)
        {
            string user = SingleInstance.CurrentUserSid();
            Check("safe folder: the user, Administrators and SYSTEM are trusted owners",
                SafeUserFolder.IsTrustedOwner(user, user) && SafeUserFolder.IsTrustedOwner("S-1-5-32-544", user)
                && SafeUserFolder.IsTrustedOwner("S-1-5-18", user));
            Check("safe folder: other owners are not trusted", !SafeUserFolder.IsTrustedOwner("S-1-1-0", user)
                && !SafeUserFolder.IsTrustedOwner("S-1-5-11", user) && !SafeUserFolder.IsTrustedOwner("S-1-5-21-1-2-3-1001", user)
                && !SafeUserFolder.IsTrustedOwner(null, user) && !SafeUserFolder.IsTrustedOwner(user, null));

            string real = Path.Combine(dir, "real");
            Directory.CreateDirectory(real);
            Exception plain = Catch(() => SafeUserFolder.Check(real, Path.Combine(real, "missing.ini")));
            Check("safe folder: a plain user folder and a missing file pass", plain == null, plain == null ? null : plain.Message);
            Check("safe folder: a missing folder passes", Catch(() => SafeUserFolder.Check(Path.Combine(dir, "not-there"))) == null);

            // A junction stands in for the medium-integrity swap of %LOCALAPPDATA%\RamOptimiser.
            string junction = Path.Combine(dir, "junction");
            bool madeJunction = Mklink("/J", junction, real);
            Check("test setup: junction created", madeJunction);
            if (madeJunction)
            {
                Throws<IOException>("safe folder: a junction is refused", () => SafeUserFolder.Check(junction));
                Check("safe folder: IsSafe is false for a junction", !SafeUserFolder.IsSafe(junction));
                string realIni = Path.Combine(real, "settings.ini");
                File.WriteAllText(realIni, "ThresholdPercent=70\r\n");
                Throws<IOException>("settings save refuses a junction folder",
                    () => SettingsStore.Save(new AppSettings { ThresholdPercent = 60 }, Path.Combine(junction, "settings.ini")));
                Check("settings save through a junction wrote nothing", File.ReadAllText(realIni) == "ThresholdPercent=70\r\n"
                    && Directory.GetFiles(real).Length == 1, string.Join(", ", Directory.GetFiles(real)));
                Check("settings load ignores a junction folder", SettingsStore.Load(Path.Combine(junction, "settings.ini")).ThresholdPercent == 85);
                Check("error log refuses a junction folder and writes nothing",
                    !RamOptimiser.App.ErrorLog.TryWrite(Path.Combine(junction, "error.log"), "x", new Exception("planted"))
                    && Directory.GetFiles(real).Length == 1, string.Join(", ", Directory.GetFiles(real)));
                Directory.Delete(junction, false); // removes the link only
                File.Delete(realIni);
            }

            // A hard link stands in for a settings file that points at a file elsewhere.
            string hard = Path.Combine(dir, "hard");
            Directory.CreateDirectory(hard);
            string victim = Path.Combine(dir, "victim.txt");
            File.WriteAllText(victim, "ThresholdPercent=70\r\n");
            string linked = Path.Combine(hard, "settings.ini");
            bool madeHardLink = Mklink("/H", linked, victim);
            Check("test setup: hard link created", madeHardLink);
            if (madeHardLink)
            {
                Throws<IOException>("safe folder: a hard-linked file is refused", () => SafeUserFolder.Check(hard, linked));
                Throws<IOException>("settings save refuses a hard-linked settings file", () => SettingsStore.Save(new AppSettings(), linked));
                Check("settings load ignores a hard-linked settings file", SettingsStore.Load(linked).ThresholdPercent == 85);
                Check("hard link target left unchanged", File.ReadAllText(victim) == "ThresholdPercent=70\r\n");
            }
        }

        // ---------------------------------------------------------------- Power Clean exit restore (P3, E10)
        // The journal itself is tested in MachineKeyTests (it moved from a file to HKLM).

        static void ExitRestoreTests()
        {
            Check("exit restore: services stopped", PowerCleanRecovery.NeedsExitRestore(2, false, false));
            Check("exit restore: a clean in flight may still stop some", PowerCleanRecovery.NeedsExitRestore(0, true, true));
            Check("exit restore: a clean in flight that cannot stop services is not waited for", !PowerCleanRecovery.NeedsExitRestore(0, true, false));
            Check("exit restore: nothing to do", !PowerCleanRecovery.NeedsExitRestore(0, false, false));
        }

        static Exception Catch(Action a)
        {
            try { a(); return null; }
            catch (Exception ex) { return ex; }
        }

        /// <summary>cmd.exe's built-in mklink: junctions (/J) and hard links (/H) need no admin rights.</summary>
        static bool Mklink(string kind, string link, string target)
        {
            string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(cmd, "/d /c mklink " + kind + " \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(10000)) { try { p.Kill(); } catch (Exception) { } return false; }
                err.Wait(5000);
                return p.ExitCode == 0;
            }
        }

        // ---------------------------------------------------------------- Decide()

        static void DecideTests()
        {
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var auto = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300 };

            Check("decide: above threshold", AutoCleanService.Decide(auto, 90, null, t0, t0, false) == CleanReason.Threshold);
            Check("decide: at threshold", AutoCleanService.Decide(auto, 85, null, t0, t0, false) == CleanReason.Threshold);
            Check("decide: below threshold", AutoCleanService.Decide(auto, 84, null, t0, t0, false) == null);
            Check("decide: inside cooldown", AutoCleanService.Decide(auto, 95, t0, t0, t0.AddSeconds(299), false) == null);
            Check("decide: cooldown elapsed", AutoCleanService.Decide(auto, 95, t0, t0, t0.AddSeconds(300), false) == CleanReason.Threshold);
            Check("decide: clean already running", AutoCleanService.Decide(auto, 99, null, t0, t0, true) == null);
            Check("decide: clock moved back", AutoCleanService.Decide(auto, 95, t0.AddHours(1), t0, t0, false) == CleanReason.Threshold);

            var off = new AppSettings { AutoCleanEnabled = false };
            Check("decide: disabled", AutoCleanService.Decide(off, 99, null, t0, t0.AddDays(1), false) == null);

            // Threshold 99 with a load of 10: only the interval can fire.
            var interval = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 99, IntervalMinutes = 10 };
            Check("decide: interval not yet", AutoCleanService.Decide(interval, 10, null, t0, t0.AddMinutes(9), false) == null);
            Check("decide: interval due", AutoCleanService.Decide(interval, 10, null, t0, t0.AddMinutes(10), false) == CleanReason.Interval);
            Check("decide: interval while running", AutoCleanService.Decide(interval, 10, null, t0, t0.AddMinutes(30), true) == null);

            // The master switch gates the interval clean too.
            var intervalOff = new AppSettings { AutoCleanEnabled = false, ThresholdPercent = 99, IntervalMinutes = 10 };
            Check("decide: no interval clean when auto-clean is off",
                AutoCleanService.Decide(intervalOff, 10, null, t0, t0.AddMinutes(10), false) == null
                && AutoCleanService.Decide(intervalOff, 10, null, t0, t0.AddDays(3), false) == null);
            Check("decide: no threshold clean when auto-clean is off",
                AutoCleanService.Decide(new AppSettings { AutoCleanEnabled = false, ThresholdPercent = 50 }, 99, null, t0, t0, false) == null);

            var both = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 600, IntervalMinutes = 1 };
            Check("decide: threshold wins over interval", AutoCleanService.Decide(both, 95, null, t0, t0.AddMinutes(5), false) == CleanReason.Threshold);
            Check("decide: interval while threshold cools down", AutoCleanService.Decide(both, 95, t0, t0, t0.AddMinutes(2), false) == CleanReason.Interval);
            Throws<ArgumentNullException>("decide: null settings", () => AutoCleanService.Decide(null, 0, null, t0, t0, false));

            // Back-off: each futile clean in a row doubles the threshold cooldown, capped at 2^4.
            Check("backoff: none", AutoCleanService.ThresholdCooldown(auto, 0) == TimeSpan.FromSeconds(300));
            Check("backoff: one step", AutoCleanService.ThresholdCooldown(auto, 1) == TimeSpan.FromSeconds(600));
            Check("backoff: capped", AutoCleanService.ThresholdCooldown(auto, 9) == TimeSpan.FromSeconds(300 * 16));
            Check("backoff: inside doubled cooldown", AutoCleanService.Decide(auto, 95, t0, t0, t0.AddSeconds(599), false, 1) == null);
            Check("backoff: doubled cooldown elapsed", AutoCleanService.Decide(auto, 95, t0, t0, t0.AddSeconds(600), false, 1) == CleanReason.Threshold);
            Check("streak: futile grows", AutoCleanService.NextStreak(0, 90, 85) == 1 && AutoCleanService.NextStreak(1, 85, 85) == 2);
            Check("streak: capped", AutoCleanService.NextStreak(AutoCleanService.MaxBackoffSteps, 99, 85) == AutoCleanService.MaxBackoffSteps);
            Check("streak: a clean that helps resets", AutoCleanService.NextStreak(3, 84, 85) == 0);
        }

        // ---------------------------------------------------------------- AutoCleanService

        static MemorySnapshot Snap(uint load)
        {
            return new MemorySnapshot { TotalPhys = 100, AvailablePhys = 100 - load, LoadPercent = load };
        }

        static CleanReport FakeReport()
        {
            return new CleanReport { Before = Snap(95), After = Snap(60) };
        }

        /// <summary>Exit waits for a clean in flight (P3): Dispose leaves it running, WaitUntilIdle waits for it.</summary>
        static void IdleWaitTests()
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            // Not disposed: the clean thread and the releasing thread may still touch it after the checks.
            var release = new ManualResetEvent(false);
            using (var svc = new AutoCleanService(() => Snap(10), a => { release.WaitOne(10000); return FakeReport(); }, () => now))
            {
                Check("idle wait: nothing running", svc.WaitUntilIdle(TimeSpan.Zero));
                Check("idle wait: test clean started", svc.CleanNow());
                Check("idle wait: times out while a clean runs", !svc.WaitUntilIdle(TimeSpan.FromMilliseconds(100)));
                svc.Dispose();
                ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(100); release.Set(); });
                Check("idle wait: returns when the clean finishes, after Dispose", svc.WaitUntilIdle(TimeSpan.FromSeconds(10)) && !svc.IsCleaning);
                release.Set();
            }

            // Only a clean whose areas include services (Power Clean) can stop any, so only that one holds Exit up.
            foreach (bool power in new[] { false, true })
            {
                var hold = new ManualResetEvent(false);
                var entered = new ManualResetEvent(false);
                using (var svc = new AutoCleanService(() => Snap(10), a => { entered.Set(); hold.WaitOne(10000); return FakeReport(); }, () => now))
                {
                    svc.Update(new AppSettings { PowerClean = power });
                    Check("services wait: nothing running", !svc.CleanMayStopServices);
                    svc.CleanNow();
                    entered.WaitOne(10000);
                    Check(power ? "services wait: a Power Clean in flight may stop services" : "services wait: a memory-only clean in flight stops none",
                        svc.CleanMayStopServices == power);
                    hold.Set();
                    svc.WaitUntilIdle(TimeSpan.FromSeconds(10));
                    Check("services wait: none after the clean (" + power + ")", !svc.CleanMayStopServices);
                }
            }
        }

        static void ServiceTests()
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            int cleans = 0, samples = 0;
            CleanReason? lastReason = null;
            CleanReport lastReport = null;
            using (var svc = new AutoCleanService(() => Snap(95), a => { Interlocked.Increment(ref cleans); return FakeReport(); }, () => now))
            {
                svc.Update(new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300 });
                svc.Sampled += s => samples++;
                svc.CleanCompleted += (r, why) => { lastReport = r; lastReason = why; };
                svc.RunTick();
                Check("tick: sampled and cleaned", samples == 1 && cleans == 1 && lastReason == CleanReason.Threshold && lastReport != null);
                svc.RunTick();
                Check("tick: cooldown stops a second clean", cleans == 1 && samples == 2);
                now = now.AddSeconds(300);
                svc.RunTick();
                Check("tick: cleans again after cooldown", cleans == 2);
            }

            // Cleans that leave load above the threshold back off; easing pressure resets the streak.
            {
                DateTime t = new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);
                uint load = 95;
                int futile = 0;
                using (var svc = new AutoCleanService(() => Snap(load),
                    a => { Interlocked.Increment(ref futile); return new CleanReport { Before = Snap(95), After = Snap(92) }; }, () => t))
                {
                    svc.Update(new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300 });
                    svc.RunTick();
                    Check("futile: first clean, streak 1", futile == 1 && svc.FutileStreak == 1);
                    t = t.AddSeconds(300);
                    svc.RunTick();
                    Check("futile: plain cooldown is not enough", futile == 1);
                    t = t.AddSeconds(300);
                    svc.RunTick();
                    Check("futile: cleans after the doubled cooldown", futile == 2 && svc.FutileStreak == 2);
                    load = 50;
                    svc.RunTick();
                    Check("futile: pressure eased, streak reset", svc.FutileStreak == 0);
                }
            }

            // A clock set back must not stop interval cleans until it catches up with the last clean.
            {
                DateTime clockNow = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
                int intervalCleans = 0;
                using (var svc = new AutoCleanService(() => Snap(10),
                    a => { Interlocked.Increment(ref intervalCleans); return FakeReport(); }, () => clockNow))
                {
                    svc.Update(new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 99, IntervalMinutes = 10 });
                    clockNow = clockNow.AddMinutes(10);
                    svc.RunTick();
                    Check("interval: first clean", intervalCleans == 1);
                    clockNow = clockNow.AddDays(-1);
                    svc.RunTick();
                    Check("interval: no clean right after the clock moved back", intervalCleans == 1);
                    clockNow = clockNow.AddMinutes(10);
                    svc.RunTick();
                    Check("interval: resumes one interval after the clock moved back", intervalCleans == 2,
                        "cleans " + intervalCleans);
                }
            }

            // The master switch off: an interval that is long overdue still does not clean.
            {
                DateTime offNow = new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);
                int offCleans = 0;
                using (var svc = new AutoCleanService(() => Snap(10),
                    a => { Interlocked.Increment(ref offCleans); return FakeReport(); }, () => offNow))
                {
                    svc.Update(new AppSettings { AutoCleanEnabled = false, IntervalMinutes = 10 });
                    offNow = offNow.AddMinutes(30);
                    svc.RunTick();
                    offNow = offNow.AddHours(5);
                    svc.RunTick();
                    Check("interval: no clean while auto-clean is off", offCleans == 0, "cleans " + offCleans);
                }
            }

            // Overlap guard: a clean that blocks keeps every other clean out, manual or automatic.
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var done = new ManualResetEvent(false))
            {
                int blockingCleans = 0;
                Func<Areas, CleanReport> blocking = a =>
                {
                    Interlocked.Increment(ref blockingCleans);
                    entered.Set();
                    release.WaitOne(10000);
                    return FakeReport();
                };
                using (var svc = new AutoCleanService(() => Snap(99), blocking, () => DateTime.UtcNow))
                {
                    svc.Update(new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 50, CooldownSeconds = 0 });
                    svc.CleanCompleted += (r, why) => done.Set();
                    Check("CleanNow starts a clean", svc.CleanNow());
                    Check("clean entered", entered.WaitOne(5000));
                    Check("CleanNow while cleaning is refused", !svc.CleanNow());
                    Check("IsCleaning while blocked", svc.IsCleaning);
                    svc.RunTick(); // threshold is due, but a clean is running
                    Check("tick during a clean does not clean", Volatile.Read(ref blockingCleans) == 1);
                    release.Set();
                    Check("blocked clean completes", done.WaitOne(5000));
                    Check("guard released after the clean", WaitUntil(() => !svc.IsCleaning, 5000));
                    done.Reset();
                    Check("CleanNow allowed again", svc.CleanNow());
                    Check("second clean completes", done.WaitOne(5000) && Volatile.Read(ref blockingCleans) == 2);
                }
            }

            // A clean that throws reports an error, still completes (null report) and frees the guard.
            {
                Exception seen = null;
                bool completedWithNull = false;
                using (var completed = new ManualResetEvent(false))
                using (var svc = new AutoCleanService(() => Snap(10), a => { throw new InvalidOperationException("boom"); }, () => DateTime.UtcNow))
                {
                    svc.Error += ex => seen = ex;
                    svc.CleanCompleted += (r, why) => { completedWithNull = r == null && why == CleanReason.Manual; completed.Set(); };
                    Check("throwing clean: started", svc.CleanNow());
                    Check("throwing clean: completed", completed.WaitOne(5000) && completedWithNull);
                    Check("throwing clean: error raised", seen is InvalidOperationException);
                    Check("throwing clean: guard freed", WaitUntil(() => !svc.IsCleaning, 5000));
                }
            }

            // P13: every failure says where it came from, so a sampling error during a clean is not the clean's.
            {
                ServiceErrorSource? cleanSource = null;
                using (var completed = new ManualResetEvent(false))
                using (var svc = new AutoCleanService(() => Snap(10), a => { throw new InvalidOperationException("boom"); }, () => DateTime.UtcNow))
                {
                    svc.Failed += (ex, source) => cleanSource = source;
                    svc.CleanCompleted += (r, why) => completed.Set();
                    svc.CleanNow();
                    Check("failure source: a throwing clean is the clean's", completed.WaitOne(5000) && cleanSource == ServiceErrorSource.Clean,
                        cleanSource.ToString());
                    WaitUntil(() => !svc.IsCleaning, 5000);
                }
            }
            {
                ServiceErrorSource? sampleSource = null;
                using (var release = new ManualResetEvent(false))
                using (var svc = new AutoCleanService(() => { throw new IOException("sample failed"); },
                    a => { release.WaitOne(5000); return FakeReport(); }, () => DateTime.UtcNow))
                {
                    svc.Failed += (ex, source) => sampleSource = source;
                    Check("failure source: manual clean running", svc.CleanNow());
                    svc.RunTick(); // a sample fails while the manual clean runs
                    Check("failure source: a sampling error during a clean is the sample's", sampleSource == ServiceErrorSource.Sample,
                        sampleSource.ToString());
                    release.Set();
                    WaitUntil(() => !svc.IsCleaning, 5000);
                }
            }

            // The real timer survives a sample function that throws, and a handler that throws.
            {
                int calls = 0, errors = 0;
                using (var sampledOk = new ManualResetEvent(false))
                using (var svc = new AutoCleanService(() =>
                    {
                        if (Interlocked.Increment(ref calls) <= 3) throw new IOException("sample failed");
                        return Snap(10);
                    }, a => FakeReport(), () => DateTime.UtcNow))
                {
                    svc.Error += ex => Interlocked.Increment(ref errors);
                    svc.Sampled += s => { sampledOk.Set(); throw new InvalidOperationException("bad handler"); };
                    svc.Start(TimeSpan.FromMilliseconds(20));
                    bool ok = sampledOk.WaitOne(5000);
                    Check("timer survives a throwing sample", ok && Volatile.Read(ref errors) >= 3, "errors " + errors + ", calls " + calls);
                    int before = Volatile.Read(ref calls);
                    Check("timer keeps ticking after a throwing handler", WaitUntil(() => Volatile.Read(ref calls) > before + 2, 5000));
                    svc.Stop();
                    Thread.Sleep(100);
                    int stopped = Volatile.Read(ref calls);
                    Thread.Sleep(200);
                    Check("Stop stops sampling", Volatile.Read(ref calls) == stopped);
                }
            }

            Throws<ArgumentOutOfRangeException>("Start needs a positive interval",
                () => new AutoCleanService(() => Snap(1), a => null, () => DateTime.UtcNow).Start(TimeSpan.Zero));
            var disposedSvc = new AutoCleanService(() => Snap(1), a => null, () => DateTime.UtcNow);
            disposedSvc.Dispose();
            Throws<ObjectDisposedException>("CleanNow after Dispose", () => disposedSvc.CleanNow());
        }

        /// <summary>P15: one settings snapshot per decision, used for the whole clean; policy changes reset the backoff.</summary>
        static void SnapshotTests()
        {
            AppSettings auto85 = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300, Areas = Areas.WorkingSets };

            // The futility verdict uses the threshold the clean started under, not one saved while it ran.
            {
                DateTime t = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
                using (var svc = new AutoCleanService(() => Snap(95),
                    a => new CleanReport { Before = Snap(95), After = Snap(90) }, () => t))
                {
                    svc.Update(auto85);
                    AppSettings raised = auto85.Clone();
                    raised.ThresholdPercent = 95;
                    svc.CleanStarted += why => svc.Update(raised);
                    svc.RunTick();
                    Check("p15: futility judged against the threshold the clean started under", svc.FutileStreak == 1,
                        "streak " + svc.FutileStreak);
                }
            }

            // The areas come from the decision's snapshot. The clock is read after the decision snapshot and
            // before the clean starts, so a save from there stands in for one racing the tick.
            {
                DateTime t = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
                Areas seen = Areas.None;
                AutoCleanService svc = null;
                bool armed = false;
                AppSettings standby = auto85.Clone();
                standby.Areas = Areas.StandbyList;
                Func<DateTime> clock = () =>
                {
                    if (armed) { armed = false; svc.Update(standby); }
                    return t;
                };
                using (svc = new AutoCleanService(() => Snap(95), a => { seen = a; return FakeReport(); }, clock))
                {
                    svc.Update(auto85);
                    svc.CleanStarted += why => svc.Update(standby);
                    armed = true;
                    svc.RunTick();
                    Check("p15: areas come from the snapshot", seen == Areas.WorkingSets, seen.ToString());
                }
            }

            // A manual clean uses the settings in force at the click; saves after it are too late.
            {
                Areas seen = Areas.None;
                AutoCleanService svc = null;
                AppSettings standby = auto85.Clone();
                standby.Areas = Areas.StandbyList;
                using (var done = new ManualResetEvent(false))
                using (svc = new AutoCleanService(() => Snap(10), a => { seen = a; svc.Update(standby); return FakeReport(); }, () => DateTime.UtcNow))
                {
                    svc.Update(auto85);
                    svc.CleanCompleted += (r, why) => done.Set();
                    Check("p15: manual clean started", svc.CleanNow());
                    svc.Update(standby);
                    Check("p15: manual clean uses settings at the click", done.WaitOne(5000) && seen == Areas.WorkingSets, seen.ToString());
                    WaitUntil(() => !svc.IsCleaning, 5000);
                }
            }

            // A new policy (threshold, cooldown, master switch) starts from the normal cooldown; a theme change does not.
            foreach (string change in new[] { "threshold", "theme" })
            {
                DateTime t = new DateTime(2026, 2, 2, 12, 0, 0, DateTimeKind.Utc);
                using (var svc = new AutoCleanService(() => Snap(95),
                    a => new CleanReport { Before = Snap(95), After = Snap(92) }, () => t))
                {
                    svc.Update(auto85);
                    svc.RunTick();
                    t = t.AddSeconds(600);
                    svc.RunTick();
                    bool atTwo = svc.FutileStreak == 2;
                    AppSettings next = auto85.Clone();
                    if (change == "threshold") next.ThresholdPercent = 90;
                    else next.Theme = ThemeMode.Dark;
                    svc.Update(next);
                    if (change == "threshold")
                        Check("p15: threshold change resets streak", atTwo && svc.FutileStreak == 0, "streak " + svc.FutileStreak);
                    else
                        Check("p15: theme change keeps streak", atTwo && svc.FutileStreak == 2, "streak " + svc.FutileStreak);
                }
            }

            // PolicyChanged: only the three fields that decide when a threshold clean may run.
            AppSettings b = new AppSettings();
            Func<Action<AppSettings>, bool> changed = edit =>
            {
                AppSettings n = b.Clone();
                edit(n);
                return AutoCleanService.PolicyChanged(b, n);
            };
            Check("p15: policy same settings", !AutoCleanService.PolicyChanged(b, b.Clone()));
            Check("p15: policy threshold", changed(n => n.ThresholdPercent = 90));
            Check("p15: policy cooldown", changed(n => n.CooldownSeconds = 600));
            Check("p15: policy master switch", changed(n => n.AutoCleanEnabled = !n.AutoCleanEnabled));
            Check("p15: policy not areas", !changed(n => n.Areas = Areas.StandbyList));
            Check("p15: policy not theme", !changed(n => n.Theme = ThemeMode.Dark));
            Check("p15: policy not interval", !changed(n => n.IntervalMinutes = 30));
        }

        /// <summary>
        /// P5: a service on fake wall and awake clocks and fake conditions. Wall time running far ahead of awake
        /// time is a sleep; the two clocks move together otherwise.
        /// </summary>
        sealed class HoldRig : IDisposable
        {
            public DateTime Wall = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            public TimeSpan Awake = TimeSpan.FromHours(5);
            public Conditions Now = Conditions.Unknown;
            public uint Load = 10;
            public int Cleans, Errors, AwakeReads;
            public bool AwakeThrows, ConditionsThrow;
            public readonly AutoCleanService Svc;

            public HoldRig(AppSettings s)
            {
                Svc = new AutoCleanService(() => Snap(Load), a => { Interlocked.Increment(ref Cleans); return FakeReport(); }, () => Wall,
                    () => { AwakeReads++; if (AwakeThrows) throw new InvalidOperationException("no awake clock"); return Awake; },
                    () => { if (ConditionsThrow) throw new InvalidOperationException("no conditions"); return Now; },
                    new GateOptions());
                Svc.Error += ex => Interlocked.Increment(ref Errors);
                Svc.Update(s);
            }

            /// <summary>Both clocks move on together: the PC was awake throughout.</summary>
            public void Pass(TimeSpan t) { Wall += t; Awake += t; }

            public void Dispose() { Svc.Dispose(); }
        }

        static void HoldTests()
        {
            var interval10 = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 99, IntervalMinutes = 10 };
            var threshold85 = new AppSettings { AutoCleanEnabled = true, ThresholdPercent = 85, CooldownSeconds = 300 };

            // No clean on the first tick after sleep, and the schedule restarts from the wake.
            using (var rig = new HoldRig(interval10))
            {
                rig.Svc.RunTick();
                rig.Wall += TimeSpan.FromHours(8);
                rig.Awake += TimeSpan.FromSeconds(2);
                rig.Svc.RunTick();
                Check("service: no clean on the first tick after sleep", rig.Cleans == 0 && rig.Svc.Hold == HoldReason.Resuming,
                    rig.Cleans + " cleans, " + rig.Svc.Hold);
                rig.Pass(TimeSpan.FromSeconds(121));
                rig.Svc.RunTick();
                Check("service: grace over, the schedule counts from the wake", rig.Cleans == 0, rig.Cleans + " cleans");
                rig.Pass(TimeSpan.FromMinutes(10));
                rig.Svc.RunTick();
                Check("service: interval cleans one interval after waking", rig.Cleans == 1, rig.Cleans + " cleans");
            }

            // A threshold clean waits out the grace, then runs.
            using (var rig = new HoldRig(new AppSettings()))
            {
                rig.Load = 95;
                rig.Svc.RunTick();
                rig.Svc.Update(threshold85);
                rig.Wall += TimeSpan.FromHours(8);
                rig.Awake += TimeSpan.FromSeconds(2);
                rig.Svc.RunTick();
                rig.Pass(TimeSpan.FromSeconds(119));
                rig.Svc.RunTick();
                Check("service: threshold clean waits out the grace", rig.Cleans == 0, rig.Cleans + " cleans");
                rig.Pass(TimeSpan.FromSeconds(1));
                rig.Svc.RunTick();
                Check("service: threshold clean after the grace", rig.Cleans == 1, rig.Cleans + " cleans");
            }

            // The suspend message holds a due clean; the resume message starts the grace.
            using (var rig = new HoldRig(threshold85))
            {
                rig.Load = 95;
                rig.Svc.NotifySuspend();
                rig.Svc.RunTick();
                Check("service: NotifySuspend blocks a due clean", rig.Cleans == 0 && rig.Svc.Hold == HoldReason.Sleeping,
                    rig.Cleans + " cleans, " + rig.Svc.Hold);
                rig.Svc.NotifyResume();
                rig.Pass(TimeSpan.FromSeconds(2));
                rig.Svc.RunTick();
                Check("service: NotifyResume holds for the grace", rig.Cleans == 0 && rig.Svc.Hold == HoldReason.Resuming,
                    rig.Cleans + " cleans, " + rig.Svc.Hold);
                rig.Pass(TimeSpan.FromSeconds(120));
                rig.Svc.RunTick();
                Check("service: cleans after the resume grace", rig.Cleans == 1 && rig.Svc.Hold == HoldReason.None, rig.Cleans + " cleans");
                Check("service: notify raises no error", rig.Errors == 0);
            }

            // The resume message alone (both clocks moved together, so no gap shows) restarts the schedule from the wake.
            using (var rig = new HoldRig(interval10))
            {
                rig.Svc.RunTick();
                rig.Svc.NotifySuspend();
                rig.Pass(TimeSpan.FromHours(8));
                rig.Svc.NotifyResume();
                rig.Pass(TimeSpan.FromSeconds(121));
                rig.Svc.RunTick();
                Check("service: NotifyResume restarts the schedule", rig.Cleans == 0, rig.Cleans + " cleans");
                rig.Pass(TimeSpan.FromMinutes(10));
                rig.Svc.RunTick();
                Check("service: interval cleans one interval after the resume message", rig.Cleans == 1, rig.Cleans + " cleans");
            }

            // Failing system readers: no Error every tick, nothing held, and a broken awake clock is not read again.
            using (var rig = new HoldRig(threshold85))
            {
                rig.Load = 95;
                rig.ConditionsThrow = true;
                rig.AwakeThrows = true;
                rig.Svc.RunTick();
                Check("service: conditions func throws -> no Error raised, clean proceeds", rig.Cleans == 1 && rig.Errors == 0,
                    rig.Cleans + " cleans, " + rig.Errors + " errors");
                rig.Pass(TimeSpan.FromMinutes(10));
                rig.Svc.RunTick();
                rig.Svc.RunTick();
                Check("service: a failing awake clock is dropped for the service's life", rig.AwakeReads == 1 && rig.Errors == 0,
                    rig.AwakeReads + " reads");
            }

            // No awake clock: the first tick after a real sleep may beat the resume message. The suspend must not
            // simply expire on the hours slept and let an overdue clean fire; it is a wake, with the grace.
            using (var rig = new HoldRig(threshold85))
            {
                rig.AwakeThrows = true;
                rig.Svc.RunTick();
                rig.Svc.NotifySuspend();
                rig.Wall += TimeSpan.FromHours(8);
                rig.Load = 95;
                rig.Svc.RunTick();
                Check("service: no awake clock, the first tick after sleep holds for the grace", rig.Cleans == 0 && rig.Svc.Hold == HoldReason.Resuming,
                    rig.Cleans + " cleans, " + rig.Svc.Hold);
                rig.Wall += TimeSpan.FromSeconds(121);
                rig.Svc.RunTick();
                Check("service: no awake clock, cleans after the grace", rig.Cleans == 1 && rig.Svc.Hold == HoldReason.None,
                    rig.Cleans + " cleans, " + rig.Svc.Hold);
            }

            // Start forgets the last tick: time the service was stopped is not a sleep. The timer's first tick runs at
            // once on a pool thread; a due clean there proves no gap was seen (a gap would hold it for 2 minutes).
            using (var rig = new HoldRig(threshold85))
            {
                rig.Svc.RunTick();
                rig.Wall += TimeSpan.FromHours(8);
                rig.Awake += TimeSpan.FromSeconds(2);
                rig.Load = 95;
                rig.Svc.Start(TimeSpan.FromHours(1));
                DateTime until = DateTime.UtcNow.AddSeconds(10);
                while (Volatile.Read(ref rig.Cleans) == 0 && DateTime.UtcNow < until) Thread.Sleep(10);
                rig.Svc.Stop();
                Check("service: Start forgets the last tick, so a stop is not taken for sleep", Volatile.Read(ref rig.Cleans) == 1
                    && rig.Svc.Hold == HoldReason.None, rig.Cleans + " cleans, " + rig.Svc.Hold);
            }

            // HoldChanged: once per change, never per tick.
            using (var rig = new HoldRig(interval10))
            {
                var seen = new System.Collections.Generic.List<HoldReason>();
                rig.Svc.HoldChanged += r => seen.Add(r);
                rig.Now = new Conditions(Presence.Busy, PowerSource.Mains);
                rig.Svc.RunTick();
                rig.Pass(TimeSpan.FromSeconds(2));
                rig.Svc.RunTick();
                rig.Now = new Conditions(Presence.Present, PowerSource.Mains);
                rig.Pass(TimeSpan.FromSeconds(2));
                rig.Svc.RunTick();
                rig.Pass(TimeSpan.FromSeconds(2));
                rig.Svc.RunTick();
                Check("service: HoldChanged raised once per change",
                    seen.Count == 2 && seen[0] == HoldReason.Busy && seen[1] == HoldReason.None && rig.Svc.Hold == HoldReason.None,
                    string.Join(",", seen));
            }

            // An interval due while on battery waits; plugging in restarts the schedule instead of cleaning at once.
            using (var rig = new HoldRig(interval10))
            {
                rig.Now = new Conditions(Presence.Present, PowerSource.Battery);
                rig.Svc.RunTick();
                rig.Pass(TimeSpan.FromMinutes(30));
                rig.Svc.RunTick();
                Check("service: battery holds the schedule", rig.Cleans == 0);
                rig.Now = new Conditions(Presence.Present, PowerSource.Mains);
                rig.Pass(TimeSpan.FromSeconds(2));
                rig.Svc.RunTick();
                Check("service: plugging in does not fire the overdue clean", rig.Cleans == 0);
                rig.Pass(TimeSpan.FromMinutes(10));
                rig.Svc.RunTick();
                Check("service: schedule counts from plugging in", rig.Cleans == 1, rig.Cleans + " cleans");
            }

            // A manual clean ignores every hold.
            using (var rig = new HoldRig(threshold85))
            using (var done = new ManualResetEvent(false))
            {
                rig.Now = new Conditions(Presence.Busy, PowerSource.Battery);
                rig.Svc.RunTick();
                rig.Svc.CleanCompleted += (r, why) => done.Set();
                Check("service: manual CleanNow ignores Busy", rig.Svc.Hold == HoldReason.Busy && rig.Svc.CleanNow() && done.WaitOne(5000)
                    && rig.Cleans == 1);
                WaitUntil(() => !rig.Svc.IsCleaning, 5000);
            }
        }

        static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        // ---------------------------------------------------------------- StartupTask

        // The fixture's own user (the principal RegisteredXml writes) and another account on the same PC.
        const string MySid = "S-1-5-21-1-2-3-1001", OtherSid = "S-1-5-21-1-2-3-1002", MyName = @"PC\me";

        static void StartupTaskTests()
        {
            // P1: one task per user, named by SID, so two accounts never share or overwrite one task.
            string mine = StartupTask.TaskNameFor(MySid);
            Check("task name: per user, by SID", mine == "RamOptimiser-" + MySid, mine);
            Check("task name: two users get two tasks", mine != StartupTask.TaskNameFor(OtherSid));
            Check("task name: the old shared name", StartupTask.LegacyTaskName == "RamOptimiser");
            Throws<ArgumentException>("task name: refuses a malformed SID", () => StartupTask.TaskNameFor("S-1-5-21-1\" /F"));
            Throws<ArgumentException>("task name: refuses no SID", () => StartupTask.TaskNameFor(null));
            Check("schtasks delete args", StartupTask.BuildDeleteArgs(mine) == "/Delete /TN \"RamOptimiser-" + MySid + "\" /F", StartupTask.BuildDeleteArgs(mine));
            Check("schtasks delete args: the old name", StartupTask.BuildDeleteArgs(StartupTask.LegacyTaskName) == @"/Delete /TN ""RamOptimiser"" /F");
            Throws<ArgumentException>("schtasks delete args: only our task names", () => StartupTask.BuildDeleteArgs("Other\" /TN \"x"));
            // The definition goes to Task Scheduler as a string over COM; no file in the user's TEMP is involved.
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            Check("task registration has no temp-file path", typeof(StartupTask).GetMethod("BuildCreateArgs", flags) == null
                && typeof(StartupTask).GetMethod("TaskXmlBytes", flags) == null && typeof(StartupTask).GetMethod("SameBytes", flags) == null);
            object[] reg = StartupTask.BuildRegisterArgs(mine, "<Task/>");
            Check("COM RegisterTask arguments", reg != null && reg.Length == 7 && (string)reg[0] == mine
                && (string)reg[1] == "<Task/>" && reg[2] is int && (int)reg[2] == 6 && reg[3] == null && reg[4] == null
                && reg[5] is int && (int)reg[5] == 3 && reg[6] == null);
            int createOrUpdate = StartupTask.TASK_CREATE_OR_UPDATE, interactiveToken = StartupTask.TASK_LOGON_INTERACTIVE_TOKEN;
            Check("COM constants", createOrUpdate == 6 && interactiveToken == 3);

            Check("exe path: drive absolute", StartupTask.IsValidExePath(@"D:\Tools\ramoptimiser.EXE"));
            Check("exe path: rejects UNC and device paths", !StartupTask.IsValidExePath(@"\\server\share\RamOptimiser.exe")
                && !StartupTask.IsValidExePath(@"//server/share/RamOptimiser.exe") && !StartupTask.IsValidExePath(@"\\?\C:\Apps\RamOptimiser.exe")
                && !StartupTask.IsValidExePath(@"\\.\C:\Apps\RamOptimiser.exe"));
            Check("exe path: rejects %, which Task Scheduler expands", !StartupTask.IsValidExePath(@"C:\%APPDATA%\RamOptimiser.exe")
                && !StartupTask.IsValidExePath(@"C:\Apps\Ram%Optimiser.exe"));
            Check("exe path: rejects relative", !StartupTask.IsValidExePath("RamOptimiser.exe")
                && !StartupTask.IsValidExePath(@"bin\RamOptimiser.exe") && !StartupTask.IsValidExePath(@"..\RamOptimiser.exe"));
            Check("exe path: rejects drive-relative", !StartupTask.IsValidExePath(@"C:RamOptimiser.exe"));
            Check("exe path: rejects root-relative", !StartupTask.IsValidExePath(@"\Apps\RamOptimiser.exe"));
            Check("exe path: rejects a quote", !StartupTask.IsValidExePath(@"C:\Apps\Ram""Optimiser.exe")
                && !StartupTask.IsValidExePath(@"C:\Apps\RamOptimiser.exe"" --evil"""));
            Check("exe path: rejects non-exe", !StartupTask.IsValidExePath(@"C:\Apps\RamOptimiser.bat")
                && !StartupTask.IsValidExePath(@"C:\Apps\RamOptimiser.exe ") && !StartupTask.IsValidExePath(@"C:\Apps\exe"));
            Check("exe path: rejects empty", !StartupTask.IsValidExePath("") && !StartupTask.IsValidExePath(null));
            Check("exe path: rejects control chars", !StartupTask.IsValidExePath("C:\\Apps\\Ram\nOptimiser.exe"));
            Check("exe path: long paths are fine in XML", StartupTask.IsValidExePath(@"C:\" + new string('a', 300) + ".exe")
                && !StartupTask.IsValidExePath(@"C:\" + new string('a', 32800) + ".exe"));
            Throws<ArgumentException>("task XML validates the path", () => StartupTask.BuildTaskXml("RamOptimiser.exe", @"PC\me"));
            Throws<ArgumentException>("task XML needs a user", () => StartupTask.BuildTaskXml(@"C:\a.exe", ""));
            TaskXmlTests();
            TaskStateTests();
        }

        static void TaskXmlTests()
        {
            // A path with spaces, an ampersand and quotes, and a user with an ampersand, must survive escaping.
            string exe = @"C:\Program Files\R&D Tools\Ram 'Optimiser'\RamOptimiser.exe";
            string user = @"DESKTOP-1\Tom & Jerry";
            string xml = StartupTask.BuildTaskXml(exe, user);
            var doc = new System.Xml.XmlDocument();
            bool parsed = true;
            try { doc.LoadXml(xml); }
            catch (System.Xml.XmlException ex) { parsed = false; Check("task XML parses", false, ex.Message); }
            if (!parsed) return;
            var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", StartupTask.TaskNamespace);
            Func<string, string> text = xp =>
            {
                System.Xml.XmlNode n = doc.SelectSingleNode(xp, ns);
                return n == null ? null : n.InnerText;
            };
            Check("task XML: version 1.2", text("/t:Task/@version") == "1.2");
            Check("task XML: logon trigger for this user", text("/t:Task/t:Triggers/t:LogonTrigger/t:UserId") == user
                && text("/t:Task/t:Triggers/t:LogonTrigger/t:Enabled") == "true", text("/t:Task/t:Triggers/t:LogonTrigger/t:UserId"));
            Check("task XML: interactive token", text("/t:Task/t:Principals/t:Principal/t:LogonType") == "InteractiveToken");
            Check("task XML: highest available", text("/t:Task/t:Principals/t:Principal/t:RunLevel") == "HighestAvailable");
            Check("task XML: principal is this user", text("/t:Task/t:Principals/t:Principal/t:UserId") == user);
            Check("task XML: no time limit", text("/t:Task/t:Settings/t:ExecutionTimeLimit") == "PT0S");
            Check("task XML: runs on battery", text("/t:Task/t:Settings/t:DisallowStartIfOnBatteries") == "false"
                && text("/t:Task/t:Settings/t:StopIfGoingOnBatteries") == "false");
            Check("task XML: one instance", text("/t:Task/t:Settings/t:MultipleInstancesPolicy") == "IgnoreNew");
            Check("task XML: not startable on demand", text("/t:Task/t:Settings/t:AllowStartOnDemand") == "false",
                text("/t:Task/t:Settings/t:AllowStartOnDemand"));
            Check("task XML: no security descriptor", text("/t:Task/t:RegistrationInfo/t:SecurityDescriptor") == null);
            Check("task XML: normal priority", text("/t:Task/t:Settings/t:Priority") == "5");
            Check("task XML: path with & and spaces round-trips", text("/t:Task/t:Actions/t:Exec/t:Command") == exe,
                text("/t:Task/t:Actions/t:Exec/t:Command"));
            Check("task XML: escaped in the text", xml.Contains("R&amp;D Tools") && !xml.Contains("R&D Tools"));
            Check("task XML: --tray argument", text("/t:Task/t:Actions/t:Exec/t:Arguments") == "--tray");
            Check("task XML: action context is the principal", text("/t:Task/t:Actions/@Context") == "Author"
                && text("/t:Task/t:Principals/t:Principal/@id") == "Author");
            Check("task XML: declares utf-16", xml.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>"), xml.Substring(0, Math.Min(60, xml.Length)));

            Check("current user id is DOMAIN\\user", StartupTask.CurrentUserId().IndexOf('\\') > 0, StartupTask.CurrentUserId());
            Check("current user SID is well formed", Elevation.IsWellFormedSid(StartupTask.CurrentUserSid()), StartupTask.CurrentUserSid());
            RegisteredTaskTests(exe, xml, user);
        }

        /// <summary>The shape Task Scheduler returns for a registered task (extra elements, its own layout).</summary>
        static string RegisteredXml(string command, string arguments, string runLevel, string extraAction)
        {
            return RegisteredXml(command, arguments, runLevel, extraAction, MySid);
        }

        /// <summary>principal is the Principal UserId (a SID or DOMAIN\user); null leaves it out.</summary>
        static string RegisteredXml(string command, string arguments, string runLevel, string extraAction, string principal)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
                + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
                + "  <RegistrationInfo>\r\n    <Description>Starts RAM Optimiser.</Description>\r\n    <URI>\\RamOptimiser</URI>\r\n  </RegistrationInfo>\r\n"
                + "  <Triggers>\r\n    <LogonTrigger>\r\n      <Enabled>true</Enabled>\r\n      <UserId>PC\\me</UserId>\r\n    </LogonTrigger>\r\n  </Triggers>\r\n"
                + "  <Principals>\r\n    <Principal id=\"Author\">\r\n"
                + (principal == null ? "" : "      <UserId>" + principal + "</UserId>\r\n")
                + "      <LogonType>InteractiveToken</LogonType>\r\n"
                + (runLevel == null ? "" : "      <RunLevel>" + runLevel + "</RunLevel>\r\n")
                + "    </Principal>\r\n  </Principals>\r\n"
                + "  <Settings>\r\n    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n"
                + "    <AllowStartOnDemand>false</AllowStartOnDemand>\r\n    <Enabled>true</Enabled>\r\n  </Settings>\r\n"
                + "  <Actions Context=\"Author\">\r\n    <Exec>\r\n      <Command>" + command + "</Command>\r\n"
                + (arguments == null ? "" : "      <Arguments>" + arguments + "</Arguments>\r\n")
                + "    </Exec>\r\n" + (extraAction ?? "") + "  </Actions>\r\n</Task>\r\n";
        }

        /// <summary>The matcher as the fixture's user (MySid, PC\me) sees it.</summary>
        static bool Ours(string xml, string path)
        {
            return StartupTask.MatchesRegisteredTask(xml, path, MySid, MyName);
        }

        static void RegisteredTaskTests(string exe, string ownXml, string ownUser)
        {
            string path = @"C:\Program Files\RamOptimiser\RamOptimiser.exe";
            Check("registered task: our own definition matches", StartupTask.MatchesRegisteredTask(ownXml, exe, MySid, ownUser));
            Check("registered task: scheduler layout matches", Ours(RegisteredXml(path, "--tray", "HighestAvailable", null), path));
            Check("registered task: path case is ignored", Ours(RegisteredXml(path.ToUpperInvariant(), "--tray", "HighestAvailable", null), path));
            Check("registered task: another program is not ours", !Ours(
                RegisteredXml(@"C:\Users\me\Downloads\RamOptimiser.exe", "--tray", "HighestAvailable", null), path));
            Check("registered task: other arguments are not ours", !Ours(RegisteredXml(path, "--tray --evil", "HighestAvailable", null), path)
                && !Ours(RegisteredXml(path, null, "HighestAvailable", null), path)
                && !Ours(RegisteredXml(path, "", "HighestAvailable", null), path));
            Check("registered task: needs HighestAvailable", !Ours(RegisteredXml(path, "--tray", "LeastPrivilege", null), path)
                && !Ours(RegisteredXml(path, "--tray", null, null), path));
            Check("registered task: a second action is not ours", !Ours(RegisteredXml(path, "--tray", "HighestAvailable",
                "    <Exec>\r\n      <Command>C:\\x\\other.exe</Command>\r\n    </Exec>\r\n"), path));
            Check("registered task: wrong namespace, junk and empty are not ours",
                !Ours(RegisteredXml(path, "--tray", "HighestAvailable", null)
                    .Replace("http://schemas.microsoft.com/windows/2004/02/mit/task", "urn:other"), path)
                && !Ours("<Task", path) && !Ours("", path) && !Ours(null, path));
            string body = RegisteredXml("&p;", "--tray", "HighestAvailable", null);
            string withDtd = "<?xml version=\"1.0\"?>\r\n<!DOCTYPE Task [<!ENTITY p \"" + path + "\">]>\r\n"
                + body.Substring(body.IndexOf("<Task ", StringComparison.Ordinal));
            Check("registered task: a DTD is refused", !Ours(withDtd, path));
            Check("registered task: the expected path must itself be valid",
                !Ours(RegisteredXml(@"\\srv\s\RamOptimiser.exe", "--tray", "HighestAvailable", null), @"\\srv\s\RamOptimiser.exe"));

            // P1: the same program for another account is that account's task, not this user's.
            Check("registered task: another user's SID is not ours", !Ours(RegisteredXml(path, "--tray", "HighestAvailable", null, OtherSid), path));
            Check("registered task: the SID is compared whole", !Ours(RegisteredXml(path, "--tray", "HighestAvailable", null, MySid + "0"), path));
            Check("registered task: principal by account name, any case", Ours(RegisteredXml(path, "--tray", "HighestAvailable", null, @"pc\ME"), path));
            Check("registered task: another account name is not ours", !Ours(RegisteredXml(path, "--tray", "HighestAvailable", null, @"PC\other"), path));
            Check("registered task: no principal user is not ours", !Ours(RegisteredXml(path, "--tray", "HighestAvailable", null, null), path));
            Check("registered task: no current user matches nothing",
                !StartupTask.MatchesRegisteredTask(RegisteredXml(path, "--tray", "HighestAvailable", null), path, null, null));
        }

        /// <summary>P1 migration and P2's three states, decided from what the two task reads returned.</summary>
        static void TaskStateTests()
        {
            string path = @"C:\Program Files\RamOptimiser\RamOptimiser.exe", old = @"C:\Users\me\bin\RamOptimiser.exe";
            TaskRead missing = TaskRead.Failed(StartupTask.HResultFileNotFound, "The system cannot find the file specified.");
            TaskRead denied = TaskRead.Failed(unchecked((int)0x80070005), "Access is denied.");
            TaskRead mine = TaskRead.Found(RegisteredXml(path, "--tray", "HighestAvailable", null));
            TaskRead mineOld = TaskRead.Found(RegisteredXml(old, "--tray", "HighestAvailable", null));
            TaskRead mineLow = TaskRead.Found(RegisteredXml(path, "--tray", "LeastPrivilege", null));
            TaskRead theirs = TaskRead.Found(RegisteredXml(old, "--tray", "HighestAvailable", null, OtherSid));
            Func<TaskRead, TaskRead, StartupTaskStatus> classify = (u, l) => StartupTask.Classify(u, l, path, MySid, MyName);

            StartupTaskStatus s = classify(missing, missing);
            Check("task state: no task is absent", s.State == StartupTaskState.Absent);
            s = classify(mine, missing);
            Check("task state: this user's task for this program is ours", s.State == StartupTaskState.Ours && !s.Legacy);
            s = classify(mineOld, missing);
            Check("task state: a task for another copy is stale and names its path", s.State == StartupTaskState.Other
                && s.Command == old && !s.SameProgram && !s.Legacy, s.State + " " + s.Command);
            s = classify(mineLow, missing);
            Check("task state: this program with other settings is stale", s.State == StartupTaskState.Other && s.SameProgram);
            s = classify(denied, missing);
            Check("task state: a denied read is unreadable, not absent", s.State == StartupTaskState.Unreadable && s.Error == "Access is denied.");
            s = classify(TaskRead.Found(RegisteredXml(path, "--tray", "HighestAvailable", null, OtherSid)), missing);
            Check("task state: this user's task name held by another principal is stale", s.State == StartupTaskState.Other);

            s = classify(missing, mine);
            Check("migration: this user's old-named task counts as ours", s.State == StartupTaskState.Ours && s.Legacy);
            s = classify(missing, mineOld);
            Check("migration: this user's old-named task for another copy is stale", s.State == StartupTaskState.Other
                && s.Legacy && s.Command == old);
            s = classify(missing, theirs);
            Check("migration: another user's old-named task is not shown", s.State == StartupTaskState.Absent);
            // Its owner can always read their own task, so an old-named task this user is denied is another account's.
            s = classify(missing, denied);
            Check("migration: an old-named task this user is denied is another account's, not shown", s.State == StartupTaskState.Absent, s.State.ToString());
            s = classify(missing, TaskRead.Failed(unchecked((int)0x80070422), "The service cannot be started."));
            Check("migration: an old-named task unreadable for another reason is unreadable", s.State == StartupTaskState.Unreadable && s.Legacy);
            s = classify(denied, missing);
            Check("migration: this user's own task denied is still unreadable", s.State == StartupTaskState.Unreadable && !s.Legacy);
            s = classify(mine, mineOld);
            Check("migration: a stale old-named task of this user still shows", s.State == StartupTaskState.Other
                && s.Legacy && s.Command == old);
            s = classify(mine, theirs);
            Check("migration: another user's old-named task leaves ours alone", s.State == StartupTaskState.Ours && !s.Legacy);

            string me = StartupTask.TaskNameFor(MySid);
            Func<TaskRead, TaskRead, bool, string> remove = (u, l, enabling)
                => string.Join(",", StartupTask.TasksToRemove(u, l, MySid, MyName, enabling).ToArray());
            Check("remove on enable: this user's old-named task", remove(null, mineOld, true) == StartupTask.LegacyTaskName);
            Check("remove on enable: never another user's old-named task", remove(null, theirs, true) == "");
            Check("remove on enable: nothing else", remove(mine, missing, true) == "" && remove(null, denied, true) == "");
            Check("remove on disable: this user's task and old-named task", remove(mine, mine, false) == me + "," + StartupTask.LegacyTaskName,
                remove(mine, mine, false));
            Check("remove on disable: never another user's old-named task", remove(mine, theirs, false) == me && remove(missing, theirs, false) == "");
            Check("remove on disable: an unreadable old-named task is left alone", remove(mine, denied, false) == me);
        }

        // ---------------------------------------------------------------- Elevation

        static void ElevationTests()
        {
            Check("UAC cancel is recognised", Elevation.IsUacCancel(new Win32Exception(1223)));
            Check("other launch errors are not a cancel", !Elevation.IsUacCancel(new Win32Exception(2)) && !Elevation.IsUacCancel(null));
            Throws<ArgumentException>("relaunch needs a path", () => Elevation.TryRelaunchElevated("", null));

            // P9: the launching user's SID crosses the UAC boundary on the command line, so it is checked strictly.
            Check("SID format: well formed", Elevation.IsWellFormedSid("S-1-5-18") && Elevation.IsWellFormedSid(MySid)
                && Elevation.IsWellFormedSid("S-1-5-21-4294967295-1-2-500"));
            string[] bad = { null, "", "BA", "S-1", "S-1-5", "s-1-5-18", "S-1-5-018", "S-1-5-18-", "S-1-5--18", "S-2-5-18",
                "S-1-5-4294967296", "S-1-5-18 x", " S-1-5-18", "S-1-5-18\" /F", "S-1-0x5-18", "S-1-5-1-2-3-4-5-6-7-8-9-10-11-12-13-14-15-16" };
            foreach (string b in bad)
                Check("SID format: refuses " + (b ?? "null"), !Elevation.IsWellFormedSid(b));
            Check("other account: none without a launcher", !Elevation.IsOtherAccount(null, MySid));
            Check("other account: the same user", !Elevation.IsOtherAccount(MySid, MySid));
            Check("other account: another SID", Elevation.IsOtherAccount(OtherSid, MySid));
            Check("other account: a malformed launcher is ignored", !Elevation.IsOtherAccount("junk", MySid));
        }

        // ---------------------------------------------------------------- SingleInstance

        static void SingleInstanceTests()
        {
            string sid = SingleInstance.CurrentUserSid();
            Check("mutex name uses the user's SID", SingleInstance.MutexName(sid) == "Local\\RamOptimiser-" + sid && sid.StartsWith("S-1-"));

            string id = "test-" + Guid.NewGuid().ToString("N");
            using (var activated = new ManualResetEvent(false))
            {
                var first = new SingleInstance(id);
                try
                {
                    first.ActivationRequested += (s, e) => activated.Set();
                    Check("first instance", first.IsFirstInstance);
                    Check("first instance does not signal itself", !first.SignalFirstInstance());
                    using (var second = new SingleInstance(id))
                    {
                        Check("second instance detected", !second.IsFirstInstance);
                        Check("second instance signals", second.SignalFirstInstance());
                        Check("first instance is asked to activate", activated.WaitOne(5000));
                    }
                }
                finally { first.Dispose(); }
                first.Dispose(); // twice is harmless
            }
            using (var again = new SingleInstance(id))
                Check("name is free after dispose", again.IsFirstInstance);

            // Ownership: only objects owned by this user are trusted; ours are created with the user as owner.
            Check("single instance: only the current user is a trusted owner", SingleInstance.IsTrustedOwner(sid, sid)
                && !SingleInstance.IsTrustedOwner("S-1-5-32-544", sid) && !SingleInstance.IsTrustedOwner("S-1-1-0", sid)
                && !SingleInstance.IsTrustedOwner(null, sid) && !SingleInstance.IsTrustedOwner(sid, null));
            string ownId = "test-" + Guid.NewGuid().ToString("N");
            using (var mine = new SingleInstance(ownId))
            using (SafeWaitHandle m = SecurityNative.OpenMutex(SecurityNative.READ_CONTROL, false, SingleInstance.MutexName(ownId)))
            using (SafeWaitHandle e = GuiNative.OpenEvent(SecurityNative.READ_CONTROL, false, SingleInstance.EventName(ownId)))
                Check("single instance: its mutex and event are owned by the user", mine.IsFirstInstance
                    && SingleInstance.OwnerSidOf(m) == sid && SingleInstance.OwnerSidOf(e) == sid,
                    SingleInstance.OwnerSidOf(m) + " / " + SingleInstance.OwnerSidOf(e));

            // A name squatted by another object type: refused as hostile (a Win32Exception, so callers run on without it).
            string typeId = "test-" + Guid.NewGuid().ToString("N");
            using (SafeWaitHandle squat = CreateEventWithSddl(SingleInstance.MutexName(typeId), "D:(A;;GA;;;" + sid + ")"))
            {
                Exception ex = Catch(() => new SingleInstance(typeId).Dispose());
                Check("squatted mutex name: refused as hostile", !squat.IsInvalid && ex is SingleInstanceNameTakenException,
                    ex == null ? "no exception" : ex.GetType().Name);
            }
            // An activation event that grants EVENT_MODIFY_STATE but not SYNCHRONIZE: WaitAny could not wait on it.
            string syncId = "test-" + Guid.NewGuid().ToString("N");
            using (SafeWaitHandle squat = CreateEventWithSddl(SingleInstance.EventName(syncId), "D:(A;;0x2;;;" + sid + ")"))
            {
                Exception ex = Catch(() => new SingleInstance(syncId).Dispose());
                Check("activation event without SYNCHRONIZE: refused as hostile", !squat.IsInvalid && ex is SingleInstanceNameTakenException,
                    ex == null ? "no exception" : ex.GetType().Name);
            }
            Check("hostile name error is a Win32Exception", typeof(Win32Exception).IsAssignableFrom(typeof(SingleInstanceNameTakenException)));
            using (var after = new SingleInstance(syncId + "-x"))
                Check("a normal instance still works after a refused one", after.IsFirstInstance);

            // A second launch during the first one's start-up (before the GUI subscribes) must not be lost.
            string earlyId = "test-" + Guid.NewGuid().ToString("N");
            using (var late = new ManualResetEvent(false))
            using (var first = new SingleInstance(earlyId))
            {
                using (var second = new SingleInstance(earlyId))
                    Check("early signal sent", second.SignalFirstInstance());
                Thread.Sleep(300); // the waiter thread consumes the auto-reset event before anyone listens
                first.ActivationRequested += (s, e) => late.Set();
                Check("signal sent before subscribing is delivered", late.WaitOne(5000));
            }
        }

        /// <summary>Creates a named event with the given DACL, as a squatting process would.</summary>
        internal static SafeWaitHandle CreateEventWithSddl(string name, string sddl)
        {
            IntPtr sd;
            if (!GuiNative.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, GuiNative.SDDL_REVISION_1, out sd, IntPtr.Zero))
                throw new Win32Exception();
            try
            {
                var sa = new GuiNative.SECURITY_ATTRIBUTES
                {
                    nLength = System.Runtime.InteropServices.Marshal.SizeOf(typeof(GuiNative.SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = sd,
                    bInheritHandle = false
                };
                return GuiNative.CreateEvent(ref sa, false, false, name);
            }
            finally { GuiNative.LocalFree(sd); }
        }

        // ---------------------------------------------------------------- TrayIconRenderer

        static void TrayIconTests()
        {
            Check("label clamps high", TrayIconRenderer.LabelFor(150) == "100");
            Check("label clamps low", TrayIconRenderer.LabelFor(-3) == "0");
            Check("label 100 in full", TrayIconRenderer.LabelFor(100) == "100");
            using (var family = new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif))
            {
                float two = TrayIconRenderer.LabelFontPixels("99", 16, family);
                float three = TrayIconRenderer.LabelFontPixels("100", 16, family);
                Check("100 is drawn smaller than 99", three < two, two + " vs " + three);
            }
            Throws<ArgumentOutOfRangeException>("icon size too small", () => TrayIconRenderer.Render(50, Color.Black, Color.White, 8));

            using (Icon i16 = TrayIconRenderer.Render(42, Color.Black, Color.White, 16))
            using (Icon i32 = TrayIconRenderer.Render(100, Color.Black, Color.White, 32))
                Check("icon size matches", i16.Width == 16 && i16.Height == 16 && i32.Width == 32 && i32.Height == 32,
                    i16.Size + " " + i32.Size);

            // Warm up (font caches, GDI+ start-up) before counting handles.
            using (TrayIconRenderer.Render(1, Color.Black, Color.White, 16)) { }
            Collect();
            uint before = GdiObjects();
            for (int i = 0; i < 300; i++)
                using (TrayIconRenderer.Render(i % 101, Color.FromArgb(255, 30, 30, 30), Color.White, 16)) { }
            Collect();
            uint after = GdiObjects();
            Check("300 renders keep GDI objects within +5", after <= before + 5, before + " -> " + after);
        }

        static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        static uint GdiObjects()
        {
            return GuiNative.GetGuiResources(Native.GetCurrentProcess(), GuiNative.GR_GDIOBJECTS);
        }
    }
}
