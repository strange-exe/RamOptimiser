using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace RamOptimiser
{
    /// <summary>
    /// The clean log: one JSON line per clean (and one per rebound sample) in %LOCALAPPDATA%\RamOptimiser\cleans.log,
    /// written by the desktop app and ramopt.exe alike. Format v1, documented in the README; fields are only ever
    /// added, and a breaking change bumps "v".
    ///
    /// The file is append-only and bounded: it never grows past MaxBytes, and the one older generation is
    /// cleans.log.old. The folder is writable without elevation while the writer may be elevated, so every write and
    /// rotation goes through SafeUserFolder first, and the opened file's link count is checked again by handle.
    /// Nothing in this program parses the log back.
    /// </summary>
    public static class CleanLog
    {
        public const string FileName = "cleans.log";
        /// <summary>The cap for cleans.log; one .old generation of the same size is kept.</summary>
        public const long MaxBytes = 256 * 1024;
        /// <summary>The longest record, in UTF-8 bytes: area messages are dropped first, then services, then areas.</summary>
        public const int MaxLineBytes = 8 * 1024;
        /// <summary>Area messages and every other text value are cut to this many characters.</summary>
        public const int MaxTextChars = 200;

        const int Attempts = 3;
        const int RetryDelayMs = 50;
        const int ErrorSharingViolation = 32, ErrorLockViolation = 33;
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string DefaultPath
        {
            get { return Path.Combine(AppPaths.DataFolder, FileName); }
        }

        /// <summary>8 lower-case hex characters, unique enough to pair a rebound line with its clean.</summary>
        public static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>A clean that ran to its end ("stopped":"none"). Pure.</summary>
        public static string FormatClean(CleanReport r, string source, string reason, string id, DateTime utc)
        {
            return FormatClean(r, source, reason, "none", id, utc);
        }

        /// <summary>
        /// One clean record, without the line break. source: gui, cli or watch; reason: manual, threshold, interval or
        /// low-memory; stopped: none, cancelled, timed-out or busy. Pure. If the line would pass MaxLineBytes, area
        /// messages are left out first, then the services, then the areas, and "truncated":true is added.
        /// </summary>
        public static string FormatClean(CleanReport r, string source, string reason, string stopped, string id, DateTime utc)
        {
            if (r == null) throw new ArgumentNullException("r");
            string line = null;
            for (int level = 0; level <= 3; level++)
            {
                line = BuildClean(r, source, reason, stopped, id, utc, level);
                if (Utf8NoBom.GetByteCount(line) <= MaxLineBytes) break;
            }
            return line;
        }

        /// <summary>The load some minutes after the clean with this id. Pure.</summary>
        public static string FormatRebound(string id, DateTime utc, MemorySnapshot s)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("v"); w.Number(1);
            w.Name("type"); w.String("rebound");
            w.Name("ref"); w.String(Cap(id));
            w.Name("time"); w.String(Time(utc));
            w.Name("load"); if (s == null) w.Null(); else w.Number((long)s.LoadPercent);
            w.Name("inUse"); if (s == null) w.Null(); else w.Number(s.InUse);
            w.EndObject();
            return w.ToString();
        }

        // level 0: everything; 1: no area messages; 2: no services either; 3: no areas either.
        static string BuildClean(CleanReport r, string source, string reason, string stopped, string id, DateTime utc, int level)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("v"); w.Number(1);
            w.Name("type"); w.String("clean");
            w.Name("id"); w.String(Cap(id));
            w.Name("time"); w.String(Time(utc));
            w.Name("source"); w.String(Cap(source));
            w.Name("reason"); w.String(Cap(reason));
            w.Name("stopped"); w.String(Cap(stopped));
            w.Name("ms"); w.Number(Ms(r.Duration));
            w.Name("passes"); w.Number(r.Passes);
            w.Name("before"); Snapshot(w, r.Before);
            w.Name("after"); Snapshot(w, r.After);
            w.Name("freed"); if (r.Before == null || r.After == null) w.Null(); else w.Number(r.InUseFreed);
            w.Name("combinedBytes"); w.Number(r.CombinedBytes);
            w.Name("areas");
            w.BeginArray();
            if (level < 3 && r.Results != null)
                foreach (AreaResult a in r.Results)
                {
                    if (a == null) continue;
                    w.BeginObject();
                    w.Name("name"); w.String(AreaName(a.Area));
                    w.Name("outcome"); w.String(OutcomeName(a.Outcome));
                    w.Name("ms"); w.Number(Ms(a.Duration));
                    if (level < 1 && a.Message != null) { w.Name("message"); w.String(Cap(a.Message)); }
                    w.EndObject();
                }
            w.EndArray();
            w.Name("services");
            w.BeginArray();
            if (level < 2 && r.Services != null)
                foreach (ServiceAction s in r.Services)
                {
                    if (s == null) continue;
                    w.BeginObject();
                    w.Name("name"); w.String(Cap(s.Name));
                    w.Name("stopped"); w.Bool(s.Stopped);
                    w.Name("failed"); w.Bool(s.Failed);
                    w.EndObject();
                }
            w.EndArray();
            if (level > 0) { w.Name("truncated"); w.Bool(true); }
            w.EndObject();
            return w.ToString();
        }

        static void Snapshot(JsonWriter w, MemorySnapshot s)
        {
            if (s == null) { w.Null(); return; }
            w.BeginObject();
            w.Name("load"); w.Number((long)s.LoadPercent);
            w.Name("inUse"); w.Number(s.InUse);
            w.Name("commitPercent"); w.Number((long)s.CommitPercent);
            w.Name("condition"); w.String(ConditionName(s.Condition));
            w.EndObject();
        }

        internal static string ConditionName(MemoryCondition c)
        {
            switch (c)
            {
                case MemoryCondition.Low: return "low";
                case MemoryCondition.Neutral: return "neutral";
                case MemoryCondition.High: return "high";
                default: return "unknown";
            }
        }

        internal static string OutcomeName(AreaOutcome o)
        {
            switch (o)
            {
                case AreaOutcome.Ok: return "ok";
                case AreaOutcome.Partial: return "partial";
                case AreaOutcome.Skipped: return "skipped";
                default: return "failed";
            }
        }

        static string AreaName(Areas area)
        {
            foreach (var kv in AreaNames.Known) if (kv.Value == area) return kv.Key;
            return area == Areas.Services ? "services" : ((int)area).ToString(CultureInfo.InvariantCulture);
        }

        static long Ms(TimeSpan t)
        {
            return t <= TimeSpan.Zero ? 0 : (long)Math.Round(t.TotalMilliseconds, MidpointRounding.AwayFromZero);
        }

        static string Time(DateTime utc)
        {
            DateTime u = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
            return u.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>Cuts to MaxTextChars without splitting a surrogate pair.</summary>
        static string Cap(string s)
        {
            if (s == null || s.Length <= MaxTextChars) return s;
            int n = MaxTextChars;
            if (char.IsHighSurrogate(s[n - 1])) n--;
            return s.Substring(0, n);
        }

        /// <summary>
        /// Appends line plus a line feed to folder\cleans.log. Never throws: false when the line is not one line or is
        /// over MaxLineBytes, the folder or a file in it fails the SafeUserFolder checks, the opened file has another
        /// hard link, or the write fails. A file another writer holds is retried a few times (the GUI and ramopt.exe
        /// can both be writing), sleeping up to about 150 ms in all, so the GUI calls this off its UI thread.
        /// </summary>
        public static bool TryAppend(string folder, string line)
        {
            if (string.IsNullOrEmpty(folder) || line == null || line.IndexOf('\n') >= 0 || line.IndexOf('\r') >= 0) return false;
            if (Utf8NoBom.GetByteCount(line) > MaxLineBytes) return false;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    AppendOnce(folder, line);
                    return true;
                }
                catch (IOException ex)
                {
                    if (attempt >= Attempts || !IsSharingViolation(ex)) return false;
                }
                catch (Exception) { return false; }
                Thread.Sleep(RetryDelayMs * attempt);
            }
        }

        static void AppendOnce(string folder, string line)
        {
            string file = Path.Combine(folder, FileName), old = file + ".old";
            byte[] bytes = Utf8NoBom.GetBytes(line + "\n");
            Directory.CreateDirectory(folder);
            SafeUserFolder.Check(folder, file, old);
            var info = new FileInfo(file);
            if (info.Exists && info.Length + bytes.Length > MaxBytes)
            {
                SafeUserFolder.Check(folder, file, old);
                if (File.Exists(old)) File.Delete(old);
                File.Move(file, old);
                SafeUserFolder.Check(folder, file);
            }
            using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                // The path check above can lose a race; the handle cannot. A hard link made in between is refused here.
                SecurityNative.BY_HANDLE_FILE_INFORMATION handleInfo;
                if (!SecurityNative.GetFileInformationByHandle(fs.SafeFileHandle, out handleInfo) || handleInfo.NumberOfLinks > 1)
                    throw new UnauthorizedAccessException("Refusing to write " + file + ": it has more than one hard link.");
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        static bool IsSharingViolation(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code == ErrorSharingViolation || code == ErrorLockViolation;
        }
    }
}
