// The update check (owner decision 2026-11-19: check-only notice) and the build version it compares with. Pure code
// and a loopback server only: every fetch here goes to a TcpListener on 127.0.0.1 that the test itself starts, with
// the proxy bypassed, so nothing leaves the machine. Nothing here opens a page, shows a balloon or touches the shell.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using RamOptimiser.App;
using RamOptimiser.Gui;

namespace RamOptimiser.Tests
{
    public static class UpdateCheckTests
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
            VersionTests();
            CompareTests();
            ParseTests();
            ConstantTests();
            ScheduleTests();
            NoticeTests();
            WordingTests();
            SettingsTests();
            BuildTests();
            FetchTests();
            XamlTests();
            WiringTests();
            return failed;
        }

        static UpdateVersion V(string text)
        {
            UpdateVersion v;
            return UpdateVersion.TryParse(text, out v) ? v : null;
        }

        static bool Parses(string text)
        {
            UpdateVersion v;
            return UpdateVersion.TryParse(text, out v);
        }

        static bool TagParses(string tag)
        {
            UpdateVersion v;
            return UpdateVersion.TryParseTag(tag, out v);
        }

        // ---------------------------------------------------------------- version and tag parsing

        static void VersionTests()
        {
            UpdateVersion v = V("2026.11.19");
            Check("version: parse", v != null && v.Year == 2026 && v.Month == 11 && v.Day == 19);
            Check("version: format round trip", v != null && v.Format() == "2026.11.19" && v.ToString() == "2026.11.19");
            Check("version: leap day", Parses("2024.02.29"));
            Check("version: impossible dates refused", !Parses("2026.02.30") && !Parses("2025.02.29") && !Parses("2026.04.31")
                && !Parses("2026.13.01") && !Parses("2026.00.10") && !Parses("2026.01.00") && !Parses("0000.01.01"));
            Check("version: other formats refused", !Parses("2026.1.19") && !Parses("26.11.19") && !Parses("2026-11-19")
                && !Parses("2026.11.19.0") && !Parses("2026.11") && !Parses("20261119") && !Parses("2026.11.1a")
                && !Parses("+026.11.19") && !Parses("2026..11.19"));
            Check("version: a tag is not a version", !Parses("v2026.11.19"));
            Check("version: spaces refused", !Parses(" 2026.11.19") && !Parses("2026.11.19 ") && !Parses("2026.11.19\n")
                && !Parses("2026.11.19\r\n") && !Parses("2026. 11.19"));
            Check("version: null and empty refused", !Parses(null) && !Parses(""));
            // Unicode digits are digits to char.IsDigit and to \d in .NET regex, but not to us: built from code points.
            string arabicNine = char.ConvertFromUtf32(0x0669), fullwidthOne = char.ConvertFromUtf32(0xFF11);
            Check("version: unicode digits refused", !Parses("2026.11.1" + arabicNine) && !Parses(fullwidthOne + "026.11.19")
                && !TagParses("v2026.11.1" + arabicNine));

            Check("tag: parse", TagParses("v2026.11.19"));
            UpdateVersion t;
            Check("tag: value", UpdateVersion.TryParseTag("v2026.12.01", out t) && t.Format() == "2026.12.01");
            Check("tag: bad forms refused", !TagParses("2026.11.19") && !TagParses("V2026.11.19") && !TagParses("v 2026.11.19")
                && !TagParses("vv2026.11.19") && !TagParses("v2026.11.19.1") && !TagParses("v2026.11.19-beta") && !TagParses("v2026.2.3")
                && !TagParses("release-2026.11.19") && !TagParses("v") && !TagParses("") && !TagParses(null));
            Check("tag: spaces refused", !TagParses(" v2026.11.19") && !TagParses("v2026.11.19 ") && !TagParses("v2026.11.19\n"));
            Check("tag: impossible date refused", !TagParses("v2026.02.30") && !TagParses("v2026.13.01"));
        }

        static void CompareTests()
        {
            Check("compare: later day", V("2026.11.20").CompareTo(V("2026.11.19")) > 0);
            Check("compare: later month beats day", V("2026.12.01").CompareTo(V("2026.11.30")) > 0);
            Check("compare: later year beats month", V("2027.01.01").CompareTo(V("2026.12.31")) > 0);
            Check("compare: earlier", V("2026.11.18").CompareTo(V("2026.11.19")) < 0);
            Check("compare: equal", V("2026.11.19").CompareTo(V("2026.11.19")) == 0 && V("2026.11.19").Equals(V("2026.11.19"))
                && V("2026.11.19").GetHashCode() == V("2026.11.19").GetHashCode() && !V("2026.11.19").Equals(V("2026.11.20")));

            UpdateResult r = UpdateCheck.Compare(V("2026.12.01"), V("2026.11.19"));
            Check("compare: newer is an update", r.Outcome == UpdateOutcome.UpdateAvailable && r.Latest.Format() == "2026.12.01"
                && r.Failure == UpdateFailure.None);
            r = UpdateCheck.Compare(V("2026.11.19"), V("2026.11.19"));
            Check("compare: same is up to date", r.Outcome == UpdateOutcome.UpToDate);
            r = UpdateCheck.Compare(V("2026.01.01"), V("2026.11.19"));
            Check("compare: older release is up to date", r.Outcome == UpdateOutcome.UpToDate);
        }

        // ---------------------------------------------------------------- release JSON

        static UpdateFailure Parse(string json, out string version)
        {
            UpdateVersion v;
            UpdateFailure f = UpdateCheck.ParseRelease(json, out v);
            version = v == null ? null : v.Format();
            return f;
        }

        static void ParseTests()
        {
            string ver;
            string good = "{\"url\":\"https://api.github.com/x\",\"id\":123456789012,\"tag_name\":\"v2026.12.01\",\"name\":\"RAM Optimiser\","
                + "\"draft\":false,\"prerelease\":false,\"author\":{\"login\":\"strange-exe\",\"id\":1},"
                + "\"assets\":[{\"name\":\"RamOptimiser.zip\",\"size\":123,\"uploader\":{\"login\":\"strange-exe\"}}],\"body\":\"Notes\"}";
            UpdateFailure f = Parse(good, out ver);
            Check("json: a release like GitHub's parses", f == UpdateFailure.None && ver == "2026.12.01", f + " " + ver);
            f = Parse("{\"tag_name\":\"v2026.12.01\"}", out ver);
            Check("json: draft and prerelease may be missing", f == UpdateFailure.None && ver == "2026.12.01", f.ToString());
            Check("json: draft true is not used", Parse("{\"tag_name\":\"v2026.12.01\",\"draft\":true,\"prerelease\":false}", out ver) == UpdateFailure.NotFinal);
            Check("json: prerelease true is not used", Parse("{\"tag_name\":\"v2026.12.01\",\"draft\":false,\"prerelease\":true}", out ver) == UpdateFailure.NotFinal);
            Check("json: draft not a bool is refused", Parse("{\"tag_name\":\"v2026.12.01\",\"draft\":\"false\"}", out ver) == UpdateFailure.BadResponse
                && Parse("{\"tag_name\":\"v2026.12.01\",\"prerelease\":null}", out ver) == UpdateFailure.BadResponse);
            Check("json: missing tag", Parse("{\"name\":\"v2026.12.01\"}", out ver) == UpdateFailure.BadResponse && ver == null);
            Check("json: tag not a string", Parse("{\"tag_name\":20261201}", out ver) == UpdateFailure.BadResponse
                && Parse("{\"tag_name\":[\"v2026.12.01\"]}", out ver) == UpdateFailure.BadResponse
                && Parse("{\"tag_name\":{\"tag_name\":\"v2026.12.01\"}}", out ver) == UpdateFailure.BadResponse
                && Parse("{\"tag_name\":null}", out ver) == UpdateFailure.BadResponse);
            Check("json: tag in another format", Parse("{\"tag_name\":\"2026.12.01\"}", out ver) == UpdateFailure.BadResponse
                && Parse("{\"tag_name\":\"v2026.02.30\"}", out ver) == UpdateFailure.BadResponse
                && Parse("{\"tag_name\":\" v2026.12.01\"}", out ver) == UpdateFailure.BadResponse);
            Check("json: tag key is case-sensitive", Parse("{\"Tag_Name\":\"v2026.12.01\"}", out ver) == UpdateFailure.BadResponse);
            Check("json: non-object roots refused", Parse("[{\"tag_name\":\"v2026.12.01\"}]", out ver) == UpdateFailure.BadResponse
                && Parse("\"v2026.12.01\"", out ver) == UpdateFailure.BadResponse && Parse("20261201", out ver) == UpdateFailure.BadResponse
                && Parse("null", out ver) == UpdateFailure.BadResponse && Parse("true", out ver) == UpdateFailure.BadResponse);
            Check("json: not json", Parse("<html>v2026.12.01</html>", out ver) == UpdateFailure.BadResponse
                && Parse("{\"tag_name\":\"v2026.12.01\"", out ver) == UpdateFailure.BadResponse
                && Parse("", out ver) == UpdateFailure.BadResponse && Parse(null, out ver) == UpdateFailure.BadResponse);
            f = Parse("{\"author\":{\"tag_name\":\"v2099.01.01\"},\"assets\":[{\"tag_name\":\"v2098.01.01\"}],\"tag_name\":\"v2026.12.01\"}", out ver);
            Check("json: nested tag_name keys do not count", f == UpdateFailure.None && ver == "2026.12.01", f + " " + ver);
            Check("json: only a nested tag_name is missing", Parse("{\"author\":{\"tag_name\":\"v2099.01.01\"}}", out ver) == UpdateFailure.BadResponse
                && Parse("{\"assets\":[{\"tag_name\":\"v2099.01.01\"}]}", out ver) == UpdateFailure.BadResponse);
            f = Parse("{\"body\":\"Fixed: \\\"tag_name\\\": \\\"v2099.01.01\\\"\",\"tag_name\":\"v2026.12.01\"}", out ver);
            Check("json: an escaped tag_name in the body does not count", f == UpdateFailure.None && ver == "2026.12.01", f + " " + ver);
            Check("json: an escaped tag_name alone is no tag", Parse("{\"body\":\"\\\"tag_name\\\":\\\"v2099.01.01\\\"\"}", out ver) == UpdateFailure.BadResponse);

            var big = new StringBuilder("{\"tag_name\":\"v2026.12.01\",\"body\":\"");
            while (big.Length <= UpdateCheck.MaxBodyBytes) big.Append('x', 4096);
            big.Append("\"}");
            f = Parse(big.ToString(), out ver);
            Check("json: over the cap is refused", f == UpdateFailure.TooLarge && ver == null, f.ToString());
            var deep = new StringBuilder();
            for (int i = 0; i < 5000; i++) deep.Append("{\"a\":");
            deep.Append("1");
            for (int i = 0; i < 5000; i++) deep.Append('}');
            Check("json: deep nesting is refused, not a crash", Parse(deep.ToString(), out ver) == UpdateFailure.BadResponse);
            var deepInside = new StringBuilder("{\"tag_name\":\"v2026.12.01\",\"x\":");
            for (int i = 0; i < UpdateCheck.MaxDepth + 2; i++) deepInside.Append('[');
            for (int i = 0; i < UpdateCheck.MaxDepth + 2; i++) deepInside.Append(']');
            deepInside.Append('}');
            Check("json: nesting past the small limit is refused", Parse(deepInside.ToString(), out ver) == UpdateFailure.BadResponse);
            Check("json: depth limit is small", UpdateCheck.MaxDepth >= 4 && UpdateCheck.MaxDepth <= 16, UpdateCheck.MaxDepth.ToString());
        }

        // ---------------------------------------------------------------- fixed constants

        static void ConstantTests()
        {
            Check("const: api uri exact", UpdateCheck.ApiUri == "https://api.github.com/repos/strange-exe/RamOptimiser/releases/latest");
            Check("const: download page exact", UpdateCheck.DownloadPage == "https://github.com/strange-exe/RamOptimiser/releases/latest");
            var api = new Uri(UpdateCheck.ApiUri);
            Check("const: api is https to api.github.com", api.Scheme == Uri.UriSchemeHttps && api.Host == "api.github.com" && api.IsDefaultPort
                && api.AbsolutePath == "/repos/strange-exe/RamOptimiser/releases/latest" && api.Query == "" && api.Fragment == "" && api.UserInfo == "");
            var page = new Uri(UpdateCheck.DownloadPage);
            Check("const: page is https to github.com", page.Scheme == Uri.UriSchemeHttps && page.Host == "github.com" && page.IsDefaultPort
                && page.AbsolutePath == "/strange-exe/RamOptimiser/releases/latest" && page.Query == "" && page.Fragment == "" && page.UserInfo == "");
            Check("const: 512 KiB cap", UpdateCheck.MaxBodyBytes == 512 * 1024);
            Check("const: 15 s timeouts", UpdateCheck.RequestTimeout == TimeSpan.FromSeconds(15));
            Check("const: accept header", UpdateCheck.AcceptHeader == "application/vnd.github+json");
            Check("const: user agent names the version", UpdateCheck.UserAgent("2026.11.19") == "RamOptimiser/2026.11.19");
            Check("const: schedule", UpdateSchedule.FirstDelay == TimeSpan.FromMinutes(2) && UpdateSchedule.Interval == TimeSpan.FromHours(24)
                && UpdateSchedule.RetryAfterFailure == TimeSpan.FromHours(6));
            Check("open route: elevated goes through the Explorer shell", UpdateOpen.Route(true) == UpdateOpenRoute.ExplorerShell);
            Check("open route: not elevated opens directly", UpdateOpen.Route(false) == UpdateOpenRoute.ShellExecute);
        }

        // ---------------------------------------------------------------- policy on a fake clock

        static readonly DateTime T0 = new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);

        static void ScheduleTests()
        {
            var s = new UpdateSchedule(T0);
            Check("schedule: nothing in the first 2 minutes", !s.Due(true, false, T0) && !s.Due(true, false, T0.AddSeconds(119)));
            Check("schedule: first check at 2 minutes", s.Due(true, false, T0.AddMinutes(2)));
            Check("schedule: disabled never checks", !s.Due(false, false, T0.AddMinutes(2)) && !s.TryStart(false, false, T0.AddHours(30)));
            Check("schedule: exiting never checks", !s.Due(true, true, T0.AddMinutes(2)) && !s.TryStart(true, true, T0.AddHours(30)));
            Check("schedule: not in flight before a start", !s.InFlight);
            Check("schedule: start", s.TryStart(true, false, T0.AddMinutes(2)) && s.InFlight);
            Check("schedule: one request in flight", !s.TryStart(true, false, T0.AddMinutes(3)) && !s.Due(true, false, T0.AddHours(30)));
            DateTime done = T0.AddMinutes(2).AddSeconds(5);
            s.Finished(false, done);
            Check("schedule: finished", !s.InFlight);
            Check("schedule: not again within 24 h", !s.Due(true, false, done.AddHours(23).AddMinutes(59)));
            Check("schedule: again after 24 h", s.Due(true, false, done.AddHours(24)));
            Check("schedule: start the daily check", s.TryStart(true, false, done.AddHours(24)));
            DateTime failedAt = done.AddHours(24).AddSeconds(15);
            s.Finished(true, failedAt);
            Check("schedule: a failure retries after 6 h, not before", !s.Due(true, false, failedAt.AddHours(5).AddMinutes(59))
                && s.Due(true, false, failedAt.AddHours(6)));
            s.TryStart(true, false, failedAt.AddHours(6));
            DateTime okAt = failedAt.AddHours(6).AddSeconds(1);
            s.Finished(false, okAt);
            Check("schedule: a success after a failure waits 24 h again", !s.Due(true, false, okAt.AddHours(6)) && s.Due(true, false, okAt.AddHours(24)));
            Check("schedule: clock set back checks once", s.Due(true, false, okAt.AddHours(-1)));
            var late = new UpdateSchedule(T0);
            Check("schedule: turned on later checks at once", late.Due(true, false, T0.AddHours(5)));
            Check("schedule: clock set back before the first check", new UpdateSchedule(T0).Due(true, false, T0.AddMinutes(-10)));
        }

        // ---------------------------------------------------------------- notice once per version, click routing

        static void NoticeTests()
        {
            UpdateResult newer = UpdateCheck.Compare(V("2026.12.01"), V("2026.11.19"));
            Check("notice: first time for a version", UpdateNoticeGate.ShouldNotify(newer, null));
            Check("notice: once per version", !UpdateNoticeGate.ShouldNotify(newer, "2026.12.01"));
            Check("notice: a newer version notifies again", UpdateNoticeGate.ShouldNotify(UpdateCheck.Compare(V("2026.12.05"), V("2026.11.19")), "2026.12.01"));
            Check("notice: none when up to date", !UpdateNoticeGate.ShouldNotify(UpdateCheck.Compare(V("2026.11.19"), V("2026.11.19")), null));
            Check("notice: none on failure", !UpdateNoticeGate.ShouldNotify(UpdateResult.Failed(UpdateFailure.Network, 0), null)
                && !UpdateNoticeGate.ShouldNotify(null, null));

            Notice n = Notices.ForUpdate("2026.12.01", "2026.11.19");
            Check("notice: update kind, title and text", n != null && n.Kind == NoticeKind.UpdateAvailable && n.Title == "Update available"
                && n.Text == "RAM Optimiser 2026.12.01 is available. You have 2026.11.19. Click to open the download page.", n == null ? "null" : n.Text);
            Check("click: the update notice opens the page", NoticeClick.OpensDownloadPage(NoticeKind.UpdateAvailable));
            Check("click: other notices show the Memory tab", !NoticeClick.OpensDownloadPage(NoticeKind.CleanFailed)
                && !NoticeClick.OpensDownloadPage(NoticeKind.NotHelping) && !NoticeClick.OpensDownloadPage(null));
        }

        // ---------------------------------------------------------------- wording

        static void WordingTests()
        {
            Check("text: row", AppText.UpdateRowTitle == "Check for updates"
                && AppText.UpdateRowNote == "Once a day, asks GitHub for the latest version number. Nothing is downloaded or installed.");
            Check("text: not checked yet", AppText.UpdateStatusLine(true, "2026.11.19", null) == "Version 2026.11.19.");
            Check("text: off", AppText.UpdateStatusLine(false, "2026.11.19", UpdateCheck.Compare(V("2026.12.01"), V("2026.11.19"))) == "Version 2026.11.19.");
            Check("text: up to date", AppText.UpdateStatusLine(true, "2026.11.19", UpdateCheck.Compare(V("2026.11.19"), V("2026.11.19")))
                == "Version 2026.11.19. Up to date.");
            Check("text: available", AppText.UpdateStatusLine(true, "2026.11.19", UpdateCheck.Compare(V("2026.12.01"), V("2026.11.19")))
                == "Version 2026.12.01 is available.");
            string failedLine = AppText.UpdateStatusLine(true, "2026.11.19", UpdateResult.Failed(UpdateFailure.HttpStatus, 404));
            Check("text: failed names the status", failedLine == "Couldn't check: GitHub answered 404.", failedLine);
            foreach (UpdateFailure f in Enum.GetValues(typeof(UpdateFailure)))
            {
                if (f == UpdateFailure.None) continue;
                string line = AppText.UpdateStatusLine(true, "2026.11.19", UpdateResult.Failed(f, 500));
                Check("text: failure " + f + " has a short reason", line.StartsWith("Couldn't check: ", StringComparison.Ordinal) && line.EndsWith(".")
                    && line.Length < 70 && line.Length > "Couldn't check: .".Length, line);
            }
            Check("text: buttons", AppText.UpdateOpenLabel == "Open download page" && AppText.UpdateCopyLabel == "Copy link");
            Check("text: open failed", AppText.UpdateOpenFailed == "The download page could not be opened. Copy the link and open it in your browser.");
            Check("text: copied", AppText.UpdateLinkCopied == "Link copied.");

            ActivityEvent e = AppText.UpdateEvent(UpdateCheck.Compare(V("2026.12.01"), V("2026.11.19")));
            Check("event: available", e.Kind == "update-available" && e.Text == "Version 2026.12.01 is available.", e.Text);
            e = AppText.UpdateEvent(UpdateCheck.Compare(V("2026.11.19"), V("2026.11.19")));
            Check("event: up to date", e.Kind == "update-checked" && e.Text == "Checked for updates: up to date.", e.Text);
            e = AppText.UpdateEvent(UpdateResult.Failed(UpdateFailure.TimedOut, 0));
            Check("event: failed", e.Kind == "update-failed" && e.Text.StartsWith("Couldn't check for updates: ", StringComparison.Ordinal), e.Text);
            Check("event: record", ActivityLog.EventRecord(T0, e).Contains("\"event\":\"update-failed\""));
        }

        // ---------------------------------------------------------------- the setting

        static void SettingsTests()
        {
            var d = new AppSettings();
            Check("settings: update check on by default", d.CheckForUpdates);
            Check("settings: written", SettingsStore.Serialize(d).Contains("CheckForUpdates=true\r\n")
                && SettingsStore.Serialize(new AppSettings { CheckForUpdates = false }).Contains("CheckForUpdates=false\r\n"));
            Check("settings: round trip off", !SettingsStore.Parse(SettingsStore.Serialize(new AppSettings { CheckForUpdates = false })).CheckForUpdates);
            Check("settings: read any case", !SettingsStore.Parse("checkforupdates = no").CheckForUpdates);
            Check("settings: bad value keeps the default", SettingsStore.Parse("CheckForUpdates=maybe").CheckForUpdates);
            Check("settings: missing key keeps the default", SettingsStore.Parse("NotifyProblems=true").CheckForUpdates);
            AppSettings merged = SettingsMerge.Merge(d, d.Clone(), SettingsStore.Parse("CheckForUpdates=false"));
            Check("settings: hand edit merges", !merged.CheckForUpdates);
            var mine = d.Clone();
            mine.CheckForUpdates = false;
            Check("settings: my change wins the merge", !SettingsMerge.Merge(d, mine, SettingsStore.Parse("CheckForUpdates=true")).CheckForUpdates);
            Check("settings: reset turns it back on", AppSettings.ResetDefaults(new AppSettings { CheckForUpdates = false }).CheckForUpdates);
            Check("settings: clone keeps it", !new AppSettings { CheckForUpdates = false }.Clone().CheckForUpdates);
        }

        // ---------------------------------------------------------------- build version

        static string Root()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        }

        static void BuildTests()
        {
            UpdateVersion v;
            Check("build: version is in the strict format", UpdateVersion.TryParse(BuildInfo.Version, out v), BuildInfo.Version);
            string txt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
            byte[] bytes = File.Exists(txt) ? File.ReadAllBytes(txt) : new byte[0];
            Check("build: version.txt is the version, LF, no BOM", Encoding.ASCII.GetString(bytes) == BuildInfo.Version + "\n"
                && (bytes.Length == 0 || bytes[0] != 0xEF), txt + ": " + bytes.Length + " bytes");
            Assembly a = typeof(UpdateCheckTests).Assembly;
            var fileVersion = (AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(a, typeof(AssemblyFileVersionAttribute));
            var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(a, typeof(AssemblyInformationalVersionAttribute));
            string expected = v == null ? "?" : v.Year + "." + v.Month + "." + v.Day + ".0";
            Check("build: file version y.M.d.0", fileVersion != null && fileVersion.Version == expected, fileVersion == null ? "none" : fileVersion.Version);
            Check("build: informational version", info != null && info.InformationalVersion == BuildInfo.Version);

            string build = File.ReadAllText(Path.Combine(Root(), "build.ps1"));
            var cscLines = new List<string>();
            // A command continued with a backtick is one line here.
            foreach (string line in Regex.Replace(build, "`\r?\n[ \t]*", " ").Split('\n'))
                if (line.TrimStart().StartsWith("& $csc @common", StringComparison.Ordinal)) cscLines.Add(line);
            int withInfo = 0;
            foreach (string line in cscLines) if (line.Contains("$buildInfo")) withInfo++;
            Check("build: BuildInfo.cs goes into ramopt, the GUI and the tests", withInfo == 3
                && Find(cscLines, "ramopt.exe").Contains("$buildInfo") && Find(cscLines, "RamOptimiser.exe").Contains("$buildInfo")
                && Find(cscLines, "ramopt-tests.exe").Contains("$buildInfo") && !Find(cscLines, "icongen.exe").Contains("$buildInfo"),
                string.Join("\n", cscLines.ToArray()));
            Check("build: System.Web.Extensions where UpdateCheck compiles", Find(cscLines, "RamOptimiser.exe").Contains("$web")
                && Find(cscLines, "ramopt-tests.exe").Contains("$web") && build.Contains("$web = '/r:System.Web.Extensions.dll'"));
            Check("build: version checked with ASCII digits, case-sensitive", build.Contains("-cmatch '^[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}$'")
                && build.Contains("ParseExact("));
            Check("build: version.txt written LF without BOM", build.Contains("version.txt") && build.Contains("UTF8Encoding($false)"));
        }

        static string Find(List<string> lines, string part)
        {
            foreach (string l in lines) if (l.Contains(part)) return l;
            return "";
        }

        // ---------------------------------------------------------------- fetch against a loopback server

        /// <summary>One connection on 127.0.0.1: reads the request headers, then runs respond. Bounded everywhere.</summary>
        sealed class Loopback : IDisposable
        {
            readonly TcpListener listener;
            readonly Thread thread;
            public readonly ManualResetEvent Release = new ManualResetEvent(false);
            public volatile string Request;
            public volatile bool WroteAll;
            public readonly Uri Uri;

            public Loopback(Action<Stream, Loopback> respond)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Uri = new Uri("http://127.0.0.1:" + port + "/repos/x/releases/latest");
                thread = new Thread(() =>
                {
                    try
                    {
                        using (TcpClient c = listener.AcceptTcpClient())
                        {
                            c.ReceiveTimeout = 10000;
                            c.SendTimeout = 10000;
                            Stream s = c.GetStream();
                            Request = ReadHeaders(s);
                            respond(s, this);
                        }
                    }
                    catch (Exception) { }
                }) { IsBackground = true, Name = "loopback-test-server" };
                thread.Start();
            }

            static string ReadHeaders(Stream s)
            {
                var sb = new StringBuilder();
                var one = new byte[1];
                while (sb.Length < 16384 && s.Read(one, 0, 1) == 1)
                {
                    sb.Append((char)one[0]);
                    if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
                }
                return sb.ToString();
            }

            public void Dispose()
            {
                Release.Set();
                try { listener.Stop(); } catch (Exception) { }
                thread.Join(15000);
            }
        }

        static void Write(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
            s.Flush();
        }

        static Action<Stream, Loopback> Respond(string status, string contentType, string body)
        {
            return (s, l) =>
            {
                byte[] b = Encoding.UTF8.GetBytes(body);
                Write(s, "HTTP/1.1 " + status + "\r\n" + (contentType == null ? "" : "Content-Type: " + contentType + "\r\n")
                    + "Content-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
                s.Write(b, 0, b.Length);
                s.Flush();
                l.WroteAll = true;
            };
        }

        const string ReleaseJson = "{\"tag_name\":\"v2026.12.01\",\"draft\":false,\"prerelease\":false,\"assets\":[]}";

        static UpdateResult Fetch(Loopback l, string current, TimeSpan timeout)
        {
            return UpdateCheck.Fetch(l.Uri, current, timeout, UpdateCheck.MaxBodyBytes, true);
        }

        static void FetchTests()
        {
            TimeSpan t = TimeSpan.FromSeconds(10);
            using (var l = new Loopback(Respond("200 OK", "application/json; charset=utf-8", ReleaseJson)))
            {
                UpdateResult r = Fetch(l, "2026.11.19", t);
                Check("fetch: 200 newer is an update", r.Outcome == UpdateOutcome.UpdateAvailable && r.Latest.Format() == "2026.12.01",
                    r.Outcome + " " + r.Failure + " " + r.StatusCode);
                l.Dispose();
                string req = l.Request ?? "";
                Check("fetch: GET of the path, HTTP/1.1", req.StartsWith("GET /repos/x/releases/latest HTTP/1.1\r\n", StringComparison.Ordinal), req);
                Check("fetch: user agent and accept", req.Contains("\r\nUser-Agent: RamOptimiser/2026.11.19\r\n")
                    && req.Contains("\r\nAccept: application/vnd.github+json\r\n"), req);
                string lower = req.ToLowerInvariant();
                Check("fetch: no auth, cookies or compression", !lower.Contains("\r\nauthorization:") && !lower.Contains("\r\ncookie:")
                    && !lower.Contains("\r\naccept-encoding:") && !lower.Contains("\r\nproxy-authorization:"), req);
            }
            using (var l = new Loopback(Respond("200 OK", "application/json", ReleaseJson)))
            {
                UpdateResult r = Fetch(l, "2026.12.01", t);
                Check("fetch: 200 same version is up to date", r.Outcome == UpdateOutcome.UpToDate, r.Outcome + " " + r.Failure);
            }
            using (var l = new Loopback(Respond("404 Not Found", "application/json", "{\"message\":\"Not Found\"}")))
            {
                UpdateResult r = Fetch(l, "2026.11.19", t);
                Check("fetch: 404 fails with its status", r.Outcome == UpdateOutcome.Failed && r.Failure == UpdateFailure.HttpStatus && r.StatusCode == 404,
                    r.Outcome + " " + r.Failure + " " + r.StatusCode);
            }
            using (var l = new Loopback(Respond("500 Internal Server Error", "application/json", ReleaseJson)))
            {
                UpdateResult r = Fetch(l, "2026.11.19", t);
                Check("fetch: 500 fails", r.Failure == UpdateFailure.HttpStatus && r.StatusCode == 500, r.Failure + " " + r.StatusCode);
            }
            using (var l = new Loopback(Respond("203 Non-Authoritative Information", "application/json", ReleaseJson)))
            {
                UpdateResult r = Fetch(l, "2026.11.19", t);
                Check("fetch: only 200 is used", r.Failure == UpdateFailure.HttpStatus && r.StatusCode == 203, r.Failure + " " + r.StatusCode);
            }

            // A redirect is not followed: the target never sees a connection.
            var target = new TcpListener(IPAddress.Loopback, 0);
            target.Start();
            try
            {
                int port = ((IPEndPoint)target.LocalEndpoint).Port;
                Action<Stream, Loopback> redirect = (s, l) => Write(s, "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:" + port
                    + "/repos/x/releases/latest\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                using (var l = new Loopback(redirect))
                {
                    UpdateResult r = Fetch(l, "2026.11.19", t);
                    Thread.Sleep(200);
                    Check("fetch: 302 is not followed", r.Failure == UpdateFailure.HttpStatus && r.StatusCode == 302 && !target.Pending(),
                        r.Failure + " " + r.StatusCode + " pending " + target.Pending());
                }
            }
            finally { target.Stop(); }

            using (var l = new Loopback(Respond("200 OK", "text/html; charset=utf-8", ReleaseJson)))
            {
                UpdateResult r = Fetch(l, "2026.11.19", t);
                Check("fetch: wrong content type refused", r.Failure == UpdateFailure.BadResponse, r.Failure.ToString());
            }
            using (var l = new Loopback(Respond("200 OK", null, ReleaseJson)))
            {
                UpdateResult r = Fetch(l, "2026.11.19", t);
                Check("fetch: missing content type refused", r.Failure == UpdateFailure.BadResponse, r.Failure.ToString());
            }
            using (var l = new Loopback(Respond("200 OK", "application/json", "{\"tag_name\":\"v2026.12.01\",\"draft\":true}")))
            {
                UpdateResult r = Fetch(l, "2026.11.19", t);
                Check("fetch: a draft is not offered", r.Outcome == UpdateOutcome.Failed && r.Failure == UpdateFailure.NotFinal, r.Failure.ToString());
            }
            using (var l = new Loopback((s, lb) =>
            {
                Write(s, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 4\r\nConnection: close\r\n\r\n");
                s.Write(new byte[] { 0x7B, 0xC3, 0x28, 0x7D }, 0, 4); // "{", a broken UTF-8 pair, "}"
                s.Flush();
            }))
            {
                UpdateResult r = Fetch(l, "2026.11.19", t);
                Check("fetch: invalid UTF-8 refused", r.Failure == UpdateFailure.BadResponse, r.Failure.ToString());
            }

            // Oversize: no length given, 16 MiB sent; the read stops at the cap and the connection is dropped.
            Action<Stream, Loopback> flood = (s, l) =>
            {
                Write(s, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"tag_name\":\"v2026.12.01\",\"body\":\"");
                var chunk = Encoding.ASCII.GetBytes(new string('x', 65536));
                for (int i = 0; i < 256; i++) s.Write(chunk, 0, chunk.Length);
                Write(s, "\"}");
                l.WroteAll = true;
            };
            using (var l = new Loopback(flood))
            {
                var clock = Stopwatch.StartNew();
                UpdateResult r = Fetch(l, "2026.11.19", t);
                long ms = clock.ElapsedMilliseconds;
                l.Dispose();
                Check("fetch: oversize body stopped at the cap", r.Failure == UpdateFailure.TooLarge && !l.WroteAll && ms < 8000,
                    r.Failure + " wroteAll " + l.WroteAll + " " + ms + " ms");
            }
            Action<Stream, Loopback> declared = (s, l) =>
            {
                Write(s, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + (UpdateCheck.MaxBodyBytes + 1)
                    + "\r\nConnection: close\r\n\r\n");
                l.Release.WaitOne(10000);
            };
            using (var l = new Loopback(declared))
            {
                var clock = Stopwatch.StartNew();
                UpdateResult r = Fetch(l, "2026.11.19", t);
                Check("fetch: a declared length over the cap is refused before reading", r.Failure == UpdateFailure.TooLarge
                    && clock.ElapsedMilliseconds < 5000, r.Failure + " " + clock.ElapsedMilliseconds + " ms");
            }

            // A server that never answers gets the injected short timeout, not the 15 s default.
            using (var l = new Loopback((s, lb) => lb.Release.WaitOne(20000)))
            {
                var clock = Stopwatch.StartNew();
                UpdateResult r = Fetch(l, "2026.11.19", TimeSpan.FromMilliseconds(700));
                long ms = clock.ElapsedMilliseconds;
                Check("fetch: a stalled server times out", r.Failure == UpdateFailure.TimedOut && ms >= 500 && ms < 6000, r.Failure + " " + ms + " ms");
            }
            // Headers, then a byte at a time: each read is quick, so only the overall deadline stops it.
            Action<Stream, Loopback> drip = (s, l) =>
            {
                Write(s, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n");
                for (int i = 0; i < 300 && !l.Release.WaitOne(100); i++) { s.WriteByte(0x20); s.Flush(); }
            };
            using (var l = new Loopback(drip))
            {
                var clock = Stopwatch.StartNew();
                UpdateResult r = Fetch(l, "2026.11.19", TimeSpan.FromMilliseconds(1000));
                long ms = clock.ElapsedMilliseconds;
                Check("fetch: a dripping server hits the overall deadline", r.Failure == UpdateFailure.TimedOut && ms < 6000, r.Failure + " " + ms + " ms");
            }
            // Nothing listening: a plain failure.
            var closed = new TcpListener(IPAddress.Loopback, 0);
            closed.Start();
            int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
            closed.Stop();
            UpdateResult refused = UpdateCheck.Fetch(new Uri("http://127.0.0.1:" + closedPort + "/"), "2026.11.19", t, UpdateCheck.MaxBodyBytes, true);
            Check("fetch: connection refused fails", refused.Outcome == UpdateOutcome.Failed && refused.Failure == UpdateFailure.Network, refused.Failure.ToString());

            // A build without a valid version never asks.
            Check("fetch: no version, no request", UpdateCheck.Fetch(new Uri("http://127.0.0.1:" + closedPort + "/"), "dev", t, UpdateCheck.MaxBodyBytes, true).Failure
                == UpdateFailure.NoVersion);
            // Only https, or http to a loopback test server, is ever fetched.
            bool threw = false;
            try { UpdateCheck.Fetch(new Uri("ftp://127.0.0.1:" + closedPort + "/"), "2026.11.19", t, UpdateCheck.MaxBodyBytes, true); }
            catch (ArgumentException) { threw = true; }
            Check("fetch: other schemes refused", threw);
        }

        // ---------------------------------------------------------------- Settings row (static XAML)

        static string Source(string folder, string file)
        {
            return File.ReadAllText(Path.Combine(Root(), Path.Combine("src", Path.Combine(folder, file))));
        }

        static void XamlTests()
        {
            string path = Path.Combine(Root(), Path.Combine("src", Path.Combine("App", Path.Combine("Xaml", "MainWindow.xaml"))));
            var doc = new XmlDocument();
            doc.Load(path);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var sw = doc.SelectSingleNode("//*[@x:Name='UpdatesCheck']", ns) as XmlElement;
            Check("row: switch named, styled, with help", sw != null && sw.GetAttribute("Style") == "{StaticResource SwitchStyle}"
                && sw.GetAttribute("AutomationProperties.Name") == AppText.UpdateRowTitle && sw.GetAttribute("AutomationProperties.HelpText") == AppText.UpdateRowNote);
            XmlElement row = sw == null ? null : Ancestor(sw, "Border");
            Check("row: in its own divided row", row != null && row.GetAttribute("Style") == "{StaticResource RowDivider}");
            Check("row: title and note shown", row != null && row.InnerXml.Contains("Text=\"" + AppText.UpdateRowTitle + "\"")
                && row.InnerXml.Contains("Text=\"" + AppText.UpdateRowNote + "\""));
            var region = doc.SelectSingleNode("//*[@x:Name='UpdateStatusRegion']", ns) as XmlElement;
            Check("row: status line is a polite live region in the row", region != null && region.LocalName == "LiveRegion"
                && region.GetAttribute("AutomationProperties.LiveSetting") == "Polite" && row != null && Ancestor(region, "Border") == row
                && region.SelectSingleNode(".//*[@x:Name='UpdateStatus']", ns) != null);
            foreach (string[] b in new[] { new[] { "UpdateOpen", AppText.UpdateOpenLabel }, new[] { "UpdateCopy", AppText.UpdateCopyLabel } })
            {
                var e = doc.SelectSingleNode("//*[@x:Name='" + b[0] + "']", ns) as XmlElement;
                Check("row: " + b[0] + " is a named link button, hidden at first", e != null && e.GetAttribute("Style") == "{StaticResource LinkButton}"
                    && e.GetAttribute("Content") == b[1] && e.GetAttribute("AutomationProperties.Name") == b[1]
                    && e.GetAttribute("Visibility") == "Collapsed" && Ancestor(e, "Border") == row);
            }
            var link = doc.SelectSingleNode("//*[@x:Name='UpdateLink']", ns) as XmlElement;
            Check("row: link box read-only, named, hidden at first", link != null && link.GetAttribute("IsReadOnly") == "True"
                && link.GetAttribute("Visibility") == "Collapsed" && link.GetAttribute("AutomationProperties.Name") == "Download page link"
                && Ancestor(link, "Border") == row);
            string xml = File.ReadAllText(path);
            int notify = xml.IndexOf("x:Name=\"NotifyCheck\"", StringComparison.Ordinal), update = xml.IndexOf("x:Name=\"UpdatesCheck\"", StringComparison.Ordinal),
                hotkey = xml.IndexOf("x:Name=\"HotkeyBox\"", StringComparison.Ordinal);
            Check("row: next to the notify row", notify > 0 && update > notify && hotkey > update);
        }

        static XmlElement Ancestor(XmlElement e, string localName)
        {
            for (XmlNode n = e.ParentNode; n != null; n = n.ParentNode)
                if (n is XmlElement && n.LocalName == localName) return (XmlElement)n;
            return null;
        }

        // ---------------------------------------------------------------- wiring, checked in source

        static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return "";
            int end = source.IndexOf("\n        }", at, StringComparison.Ordinal);
            return end < 0 ? source.Substring(at) : source.Substring(at, end - at);
        }

        static int Count(string text, string part)
        {
            return string.IsNullOrEmpty(part) ? 0 : (text.Length - text.Replace(part, "").Length) / part.Length;
        }

        static void WiringTests()
        {
            string shell = Source("App", "AppShell.cs");
            string open = Body(shell, "void OpenDownloadPage()");
            int route = open.IndexOf("if (UpdateOpen.Route(elevated) == UpdateOpenRoute.ExplorerShell)", StringComparison.Ordinal);
            int ret = route < 0 ? -1 : open.IndexOf("return;", route, StringComparison.Ordinal);
            string elevatedBranch = route < 0 || ret < 0 ? "" : open.Substring(route, ret - route);
            Check("wiring: the elevated path asks Explorer to open the constant", elevatedBranch.Contains("ShellOpen.OpenUnelevated(UpdateCheck.DownloadPage,"), open);
            Check("wiring: the elevated path never calls Process.Start", elevatedBranch.Length > 0 && !elevatedBranch.Contains("Process.Start"), open);
            Check("wiring: the elevated path falls back to the link only", elevatedBranch.Contains("ShowUpdateLink()"), elevatedBranch);
            int start = open.IndexOf("Process.Start(", StringComparison.Ordinal);
            Check("wiring: not elevated opens the constant with the shell", start > ret && ret > 0
                && open.Contains("Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateCheck.DownloadPage) { UseShellExecute = true })"), open);
            Check("wiring: only two Process.Start calls in the shell, both fixed",
                Count(shell, "Process.Start(") == 2 && Count(shell, "ProcessStartInfo(AppText.StartupSettingsUri)") == 1
                && Count(shell, "ProcessStartInfo(UpdateCheck.DownloadPage)") == 1, Count(shell, "Process.Start(").ToString());
            Check("wiring: every ShellOpen call opens the constant", Count(shell, "ShellOpen.") == 1
                && Count(shell, "ShellOpen.OpenUnelevated(UpdateCheck.DownloadPage,") == 1);
            string shellOpen = Source("App", "ShellOpen.cs");
            Check("wiring: ShellOpen starts no process itself", !shellOpen.Contains("Process.Start") && !shellOpen.Contains("ProcessStartInfo")
                && shellOpen.Contains("FindWindowSW(") && shellOpen.Contains("QueryActiveShellView(") && shellOpen.Contains("\"ShellExecute\""));
            Check("wiring: copy puts only the constant on the clipboard", Body(shell, "void CopyDownloadLink()").Contains("Clipboard.SetText(UpdateCheck.DownloadPage)")
                && Count(shell, "Clipboard.SetText(") == 1);
            Check("wiring: the link box shows the constant", shell.Contains("updateLink.Text = UpdateCheck.DownloadPage;"));

            string check = Body(shell, "void MaybeCheckForUpdates()");
            Check("wiring: checks go through the schedule with the setting and exiting", check.Contains("updateSchedule.TryStart(settings.CheckForUpdates, exiting, DateTime.UtcNow)"), check);
            Check("wiring: the request runs off the UI thread and posts back", check.Contains("ThreadPool.QueueUserWorkItem(")
                && check.Contains("UpdateCheck.Check(BuildInfo.Version)") && check.Contains("Post(() => OnUpdateChecked(r))"), check);
            Check("wiring: only the fixed check is called", !shell.Contains("UpdateCheck.Fetch(") && Count(shell, "UpdateCheck.Check(") == 1);
            string gui = Source("Gui", "UpdateCheck.cs");
            string checkBody = Body(gui, "public static UpdateResult Check(string currentVersion)");
            Check("wiring: Check fetches the fixed uri with the fixed limits", checkBody.Contains("Fetch(new Uri(ApiUri), currentVersion, RequestTimeout, MaxBodyBytes, false)"), checkBody);
            Check("wiring: redirects off, both timeouts set", gui.Contains("AllowAutoRedirect = false;") && gui.Contains("req.Timeout = ")
                && gui.Contains("req.ReadWriteTimeout = "));
            string done = Body(shell, "void OnUpdateChecked(UpdateResult r)");
            Check("wiring: a result finishes the schedule, logs once per kind, notifies once per version",
                done.Contains("updateSchedule.Finished(") && done.Contains("updateLogged.Add(") && done.Contains("UpdateNoticeGate.ShouldNotify(r, updateNotified)"), done);
            string notice = Body(shell, "void ShowUpdateNotice(UpdateResult r)");
            Check("wiring: the update notice uses the info icon and remembers its kind", notice.Contains("ToolTipIcon.Info")
                && notice.Contains("lastNoticeKind = NoticeKind.UpdateAvailable;") && notice.Contains("updateNotified = "), notice);
            Check("wiring: other notices remember their kind", Body(shell, "void ShowNotice(Notice n)").Contains("lastNoticeKind = n.Kind;"));
            Check("wiring: a notice click is routed", shell.Contains("tray.NoticeClicked += delegate { OnNoticeClicked(); };")
                && !shell.Contains("tray.NoticeClicked += delegate { ShowMemoryTab(); };"));
            string click = Body(shell, "void OnNoticeClicked()");
            Check("wiring: the route picks the page or the Memory tab", click.Contains("NoticeClick.OpensDownloadPage(lastNoticeKind)")
                && click.Contains("OpenDownloadPage();") && click.Contains("ShowMemoryTab();"), click);
            Check("wiring: the switch saves the setting", shell.Contains("OnToggle(updatesCheck, on => { settings.CheckForUpdates = on;"));

            string tray = Source("App", "TrayController.cs");
            Check("tray: ShowNotice takes the icon", tray.Contains("public bool ShowNotice(string title, string text, ToolTipIcon tipIcon)")
                && tray.Contains("icon.ShowBalloonTip(10000, title ?? \"\", text, tipIcon);") && tray.Contains("return ShowNotice(title, text, ToolTipIcon.Warning);"));

            // TLS stays the system default: nothing in the app assigns ServicePointManager.SecurityProtocol.
            var assign = new Regex(@"SecurityProtocol\s*[|&^]?=(?!=)");
            var hits = new List<string>();
            foreach (string f in Directory.GetFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
                if (assign.IsMatch(File.ReadAllText(f))) hits.Add(Path.GetFileName(f));
            Check("wiring: SecurityProtocol is never assigned", hits.Count == 0, string.Join(", ", hits.ToArray()));
            Check("wiring: the regex catches an assignment", assign.IsMatch("ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;")
                && assign.IsMatch("ServicePointManager.SecurityProtocol |= x;") && !assign.IsMatch("// do not set SecurityProtocol"));
        }
    }
}
