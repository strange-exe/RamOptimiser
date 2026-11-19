// The update check (owner decision 2026-11-19: "Yes, check-only notice"). Once a day the app asks GitHub for the
// latest release's tag and compares its date with this build's. Nothing is downloaded or installed, and nothing
// from the answer is shown except the version, parsed and formatted again here. The app may run elevated, so this
// stays small: two fixed URLs, one GET, no redirects, a byte cap, a deadline and a strict parse.
//   GitHub "Get the latest release": GET /repos/{owner}/{repo}/releases/latest, drafts and prereleases left out,
//   404 when there is none; a User-Agent is required, Accept application/vnd.github+json recommended.
//   https://docs.github.com/en/rest/releases/releases
//   https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api
//   TLS: .NET Framework 4.8 uses the system default protocols when the app does not set ServicePointManager's
//   protocol list, so it is never set here. https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace RamOptimiser.Gui
{
    /// <summary>A release version, yyyy.MM.dd: a real date written with ASCII digits, nothing before or after it.</summary>
    public sealed class UpdateVersion : IComparable<UpdateVersion>
    {
        public readonly int Year, Month, Day;

        UpdateVersion(int year, int month, int day)
        {
            Year = year;
            Month = month;
            Day = day;
        }

        /// <summary>
        /// "2026.11.19". Checked by hand rather than with \d: in .NET regex \d matches every script's digits and $
        /// matches before a final newline, and neither belongs in a version.
        /// </summary>
        public static bool TryParse(string text, out UpdateVersion version)
        {
            version = null;
            if (text == null || text.Length != 10 || text[4] != '.' || text[7] != '.') return false;
            int year, month, day;
            if (!Digits(text, 0, 4, out year) || !Digits(text, 5, 2, out month) || !Digits(text, 8, 2, out day)) return false;
            if (year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
            version = new UpdateVersion(year, month, day);
            return true;
        }

        /// <summary>A release tag: "v" and a version, "v2026.11.19".</summary>
        public static bool TryParseTag(string tag, out UpdateVersion version)
        {
            version = null;
            if (tag == null || tag.Length != 11 || tag[0] != 'v') return false;
            return TryParse(tag.Substring(1), out version);
        }

        static bool Digits(string s, int start, int count, out int value)
        {
            value = 0;
            for (int i = start; i < start + count; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }

        public string Format()
        {
            return Year.ToString("D4", CultureInfo.InvariantCulture) + "." + Month.ToString("D2", CultureInfo.InvariantCulture)
                + "." + Day.ToString("D2", CultureInfo.InvariantCulture);
        }

        public override string ToString() { return Format(); }

        public int CompareTo(UpdateVersion other)
        {
            if (other == null) return 1;
            if (Year != other.Year) return Year.CompareTo(other.Year);
            if (Month != other.Month) return Month.CompareTo(other.Month);
            return Day.CompareTo(other.Day);
        }

        public override bool Equals(object obj)
        {
            var o = obj as UpdateVersion;
            return o != null && CompareTo(o) == 0;
        }

        public override int GetHashCode() { return (Year * 13 + Month) * 32 + Day; }
    }

    public enum UpdateOutcome { UpToDate, UpdateAvailable, Failed }

    /// <summary>Why a check failed. The app words each one itself (AppText); no text from the answer is ever kept.</summary>
    public enum UpdateFailure { None, NoVersion, Network, TimedOut, HttpStatus, TooLarge, BadResponse, NotFinal }

    public sealed class UpdateResult
    {
        public UpdateOutcome Outcome;
        /// <summary>The latest release, when the check worked.</summary>
        public UpdateVersion Latest;
        public UpdateFailure Failure;
        /// <summary>The HTTP status for HttpStatus, else 0.</summary>
        public int StatusCode;

        public static UpdateResult Failed(UpdateFailure failure, int statusCode)
        {
            return new UpdateResult { Outcome = UpdateOutcome.Failed, Failure = failure, StatusCode = statusCode };
        }
    }

    public static class UpdateCheck
    {
        /// <summary>The only address asked. Never taken from settings or from an answer.</summary>
        public const string ApiUri = "https://api.github.com/repos/strange-exe/RamOptimiser/releases/latest";
        /// <summary>The only page opened (GitHub's documented latest-release link form).</summary>
        public const string DownloadPage = "https://github.com/strange-exe/RamOptimiser/releases/latest";
        public const string AcceptHeader = "application/vnd.github+json";
        /// <summary>A release answer is a few KB; anything past 512 KiB is not read further.</summary>
        public const int MaxBodyBytes = 512 * 1024;
        /// <summary>
        /// Object and array levels the parser follows. GitHub's release goes four deep (release, assets, asset,
        /// uploader); JavaScriptSerializer's default is 100.
        /// </summary>
        public const int MaxDepth = 10;
        /// <summary>For the response and for each read (HttpWebRequest's defaults are 100 s and 300 s), and the whole body.</summary>
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string UserAgent(string version) { return "RamOptimiser/" + version; }

        /// <summary>Asks GitHub about the latest release, comparing it with currentVersion. Blocks; never throws.</summary>
        public static UpdateResult Check(string currentVersion)
        {
            try { return Fetch(new Uri(ApiUri), currentVersion, RequestTimeout, MaxBodyBytes, false); }
            catch (Exception) { return UpdateResult.Failed(UpdateFailure.Network, 0); }
        }

        /// <summary>
        /// The check against any https address, or a loopback http one (the tests' server). direct skips the proxy, so a
        /// test's request never leaves the machine; the app always goes through the system proxy like a browser would.
        /// </summary>
        internal static UpdateResult Fetch(Uri uri, string currentVersion, TimeSpan timeout, int maxBytes, bool direct)
        {
            UpdateVersion current;
            if (!UpdateVersion.TryParse(currentVersion, out current)) return UpdateResult.Failed(UpdateFailure.NoVersion, 0);
            if (uri == null || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                throw new ArgumentException("Only https, or http to this machine, is fetched.", "uri");
            int ms = (int)Math.Max(1, Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            HttpWebRequest req = null;
            HttpWebResponse resp = null;
            try
            {
                // https://learn.microsoft.com/en-us/dotnet/api/system.net.httpwebrequest : redirects are followed and the
                // timeouts are 100 s and 300 s unless set. No credentials, no cookie container (so no cookies), and no
                // decompression, so the cap counts the bytes that arrive.
                req = (HttpWebRequest)WebRequest.Create(uri);
                req.Method = "GET";
                req.AllowAutoRedirect = false;
                req.Timeout = ms;
                req.ReadWriteTimeout = ms;
                req.UserAgent = UserAgent(currentVersion);
                req.Accept = AcceptHeader;
                req.KeepAlive = false;
                req.Credentials = null;
                req.UseDefaultCredentials = false;
                req.PreAuthenticate = false;
                req.CookieContainer = null;
                req.AutomaticDecompression = DecompressionMethods.None;
                if (direct) req.Proxy = null;
                Stopwatch clock = Stopwatch.StartNew();
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException ex)
                {
                    var error = ex.Response as HttpWebResponse;
                    if (error == null) return UpdateResult.Failed(Classify(ex), 0);
                    int code = (int)error.StatusCode;
                    req.Abort(); // before Close, which would otherwise read the rest of the body
                    error.Close();
                    return UpdateResult.Failed(UpdateFailure.HttpStatus, code);
                }
                int status = (int)resp.StatusCode;
                if (status != 200) return UpdateResult.Failed(UpdateFailure.HttpStatus, status);
                if (!IsJson(resp.ContentType)) return UpdateResult.Failed(UpdateFailure.BadResponse, 0);
                if (resp.ContentLength > maxBytes) return UpdateResult.Failed(UpdateFailure.TooLarge, 0);
                byte[] body;
                UpdateFailure read = ReadCapped(resp.GetResponseStream(), maxBytes, clock, timeout, out body);
                if (read != UpdateFailure.None) return UpdateResult.Failed(read, 0);
                string text;
                try { text = StrictUtf8.GetString(body); }
                catch (DecoderFallbackException) { return UpdateResult.Failed(UpdateFailure.BadResponse, 0); }
                UpdateVersion latest;
                UpdateFailure parsed = ParseRelease(text, out latest);
                return parsed == UpdateFailure.None ? Compare(latest, current) : UpdateResult.Failed(parsed, 0);
            }
            catch (WebException ex) { return UpdateResult.Failed(Classify(ex), 0); }
            catch (IOException ex)
            {
                var inner = ex.InnerException as WebException;
                return UpdateResult.Failed(inner != null ? Classify(inner) : UpdateFailure.Network, 0);
            }
            catch (Exception) { return UpdateResult.Failed(UpdateFailure.Network, 0); }
            finally
            {
                // Abort first: closing a response with unread body waits to drain it, and this one may be endless.
                if (req != null) req.Abort();
                if (resp != null) resp.Close();
            }
        }

        static UpdateFailure Classify(WebException ex)
        {
            return ex.Status == WebExceptionStatus.Timeout ? UpdateFailure.TimedOut : UpdateFailure.Network;
        }

        /// <summary>application/json, with or without parameters (GitHub sends "; charset=utf-8").</summary>
        internal static bool IsJson(string contentType)
        {
            if (contentType == null) return false;
            int semi = contentType.IndexOf(';');
            string media = (semi < 0 ? contentType : contentType.Substring(0, semi)).Trim();
            return string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads at most maxBytes, stopping with TooLarge past it. Each read waits at most ReadWriteTimeout; the whole
        /// body must also arrive within timeout of the request's start, so a server sending a byte at a time cannot
        /// hold the one check in flight for days.
        /// </summary>
        static UpdateFailure ReadCapped(Stream s, int maxBytes, Stopwatch clock, TimeSpan timeout, out byte[] body)
        {
            body = null;
            var buffer = new byte[8192];
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    if (clock.Elapsed > timeout) return UpdateFailure.TimedOut;
                    int n = s.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    if (ms.Length + n > maxBytes) return UpdateFailure.TooLarge;
                    ms.Write(buffer, 0, n);
                }
                body = ms.ToArray();
                return UpdateFailure.None;
            }
        }

        /// <summary>
        /// The release's tag from GitHub's JSON: a top-level object whose own tag_name is a "v" + version string, with
        /// draft and prerelease (when present) false. Keys inside nested objects and text inside strings never count:
        /// the JSON is parsed, not searched. Anything else is BadResponse; a draft or prerelease is NotFinal.
        /// JavaScriptSerializer (https://learn.microsoft.com/en-us/dotnet/api/system.web.script.serialization.javascriptserializer):
        /// RecursionLimit defaults to 100 levels and MaxJsonLength to 2097152 characters; past either, and on bad
        /// input, DeserializeObject throws ArgumentException (InvalidOperationException for "__type" cases). It does not
        /// cast the root; the page names no concrete types, so only a root that is an IDictionary of string to object
        /// is used and anything else is refused.
        /// </summary>
        internal static UpdateFailure ParseRelease(string json, out UpdateVersion latest)
        {
            latest = null;
            if (string.IsNullOrEmpty(json)) return UpdateFailure.BadResponse;
            if (json.Length > MaxBodyBytes) return UpdateFailure.TooLarge;
            object root;
            try
            {
                var serializer = new JavaScriptSerializer { RecursionLimit = MaxDepth, MaxJsonLength = MaxBodyBytes };
                root = serializer.DeserializeObject(json);
            }
            catch (Exception) { return UpdateFailure.BadResponse; } // ArgumentException and InvalidOperationException, per the page above
            var release = root as IDictionary<string, object>;
            if (release == null) return UpdateFailure.BadResponse;
            object tag;
            UpdateVersion version;
            if (!release.TryGetValue("tag_name", out tag) || !UpdateVersion.TryParseTag(tag as string, out version)) return UpdateFailure.BadResponse;
            bool draft, prerelease;
            if (!OptionalBool(release, "draft", out draft) || !OptionalBool(release, "prerelease", out prerelease)) return UpdateFailure.BadResponse;
            if (draft || prerelease) return UpdateFailure.NotFinal;
            latest = version;
            return UpdateFailure.None;
        }

        static bool OptionalBool(IDictionary<string, object> o, string key, out bool value)
        {
            value = false;
            object v;
            if (!o.TryGetValue(key, out v)) return true;
            if (!(v is bool)) return false;
            value = (bool)v;
            return true;
        }

        public static UpdateResult Compare(UpdateVersion latest, UpdateVersion current)
        {
            if (latest == null || current == null) return UpdateResult.Failed(UpdateFailure.NoVersion, 0);
            return new UpdateResult
            {
                Outcome = latest.CompareTo(current) > 0 ? UpdateOutcome.UpdateAvailable : UpdateOutcome.UpToDate,
                Latest = latest
            };
        }
    }

    /// <summary>
    /// When to check: 2 minutes after start-up, then every 24 hours while the app runs, 6 hours after a failure; never
    /// while the setting is off or the app is exiting, and never with a request still in flight. UTC; a clock set back
    /// allows one check (as NoticeGate does). Pure: the caller passes the time.
    /// </summary>
    public sealed class UpdateSchedule
    {
        public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
        public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

        readonly DateTime startedUtc;
        DateTime? lastUtc;
        bool lastFailed, inFlight;

        public UpdateSchedule(DateTime startedUtc)
        {
            this.startedUtc = startedUtc;
        }

        public bool InFlight { get { return inFlight; } }

        public bool Due(bool enabled, bool exiting, DateTime nowUtc)
        {
            if (!enabled || exiting || inFlight) return false;
            TimeSpan since = nowUtc - (lastUtc ?? startedUtc);
            TimeSpan wait = lastUtc == null ? FirstDelay : lastFailed ? RetryAfterFailure : Interval;
            return since < TimeSpan.Zero || since >= wait;
        }

        /// <summary>True when a check is due; it is then in flight until Finished.</summary>
        public bool TryStart(bool enabled, bool exiting, DateTime nowUtc)
        {
            if (!Due(enabled, exiting, nowUtc)) return false;
            inFlight = true;
            return true;
        }

        /// <summary>The next wait counts from when the answer came back.</summary>
        public void Finished(bool failed, DateTime nowUtc)
        {
            inFlight = false;
            lastUtc = nowUtc;
            lastFailed = failed;
        }
    }

    /// <summary>The balloon for a new version shows once per version per run. Pure.</summary>
    public static class UpdateNoticeGate
    {
        /// <param name="lastNotified">The version last shown in a balloon this run (Format), or null.</param>
        public static bool ShouldNotify(UpdateResult r, string lastNotified)
        {
            return r != null && r.Outcome == UpdateOutcome.UpdateAvailable && r.Latest != null
                && !string.Equals(r.Latest.Format(), lastNotified, StringComparison.Ordinal);
        }
    }

    public enum UpdateOpenRoute { ShellExecute, ExplorerShell }

    /// <summary>
    /// How the download page opens (owner decision 2026-11-19): elevated, the normal Explorer shell opens it, so the
    /// browser never runs as administrator; not elevated, the app opens it itself.
    /// </summary>
    public static class UpdateOpen
    {
        public static UpdateOpenRoute Route(bool elevated)
        {
            return elevated ? UpdateOpenRoute.ExplorerShell : UpdateOpenRoute.ShellExecute;
        }
    }
}
