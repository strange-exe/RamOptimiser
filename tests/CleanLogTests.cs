// Package 2D (R4+E21 part 1): the JSON writer, the clean log record format and its bounded, append-only writer with
// the SafeUserFolder checks, and the shared data folder. Temp folders only: nothing here touches the real
// %LOCALAPPDATA%\RamOptimiser or cleans memory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RamOptimiser.Tests
{
    public static class CleanLogTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        // One backslash, so expected escapes are built in code rather than typed as escape sequences.
        static readonly string Bs = new string((char)0x5C, 1);

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        public static int Run()
        {
            JsonTests();
            PathTests();
            FormatTests();
            string dir = Path.Combine(Path.GetTempPath(), "ramopt-cleanlog-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                AppendTests(dir);
                LinkTests(dir);
                FailureTests(dir);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return failed;
        }

        // ---------------------------------------------------------------- JsonWriter

        static string Quote(string s)
        {
            var w = new JsonWriter();
            w.String(s);
            return w.ToString();
        }

        static string U(int codePoint) { return Bs + "u" + codePoint.ToString("x4", CultureInfo.InvariantCulture); }

        static void JsonTests()
        {
            string input = "a" + (char)0x22 + (char)0x5C + (char)0x0A + (char)0x0D + (char)0x09 + (char)0x00 + (char)0x1F + (char)0x08 + "z";
            string expected = "\"a" + Bs + "\"" + Bs + Bs + Bs + "n" + Bs + "r" + Bs + "t" + U(0) + U(0x1F) + U(8) + "z\"";
            string got = Quote(input);
            Check("json: escapes quote, backslash and control characters", got == expected, got);
            var all = new StringBuilder();
            for (int c = 0; c < 0x20; c++) all.Append((char)c);
            string allQuoted = Quote(all.ToString());
            bool printable = true;
            foreach (char c in allQuoted) if (c < 0x20) printable = false;
            Check("json: every control character is escaped and reads back", printable
                && (string)new JavaScriptSerializer().DeserializeObject(allQuoted) == all.ToString(), allQuoted);

            string seps = "x" + (char)0x2028 + "y" + (char)0x2029;
            got = Quote(seps);
            Check("json: escapes U+2028 and U+2029", got == "\"x" + U(0x2028) + "y" + U(0x2029) + "\""
                && got.IndexOf((char)0x2028) < 0 && got.IndexOf((char)0x2029) < 0, got);

            string pair = new string(new[] { (char)0xD83D, (char)0xDE00 });
            got = Quote("a" + (char)0xD800 + "b" + (char)0xDC00 + pair + (char)0xDBFF);
            Check("json: a lone surrogate becomes U+FFFD", got == "\"a" + U(0xFFFD) + "b" + U(0xFFFD) + pair + U(0xFFFD) + "\"", got);
            Check("json: a null string is null", Quote(null) == "null");

            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                var odd = (CultureInfo)new CultureInfo("de-DE").Clone();
                odd.NumberFormat.NegativeSign = "~";
                odd.NumberFormat.NumberGroupSeparator = ".";
                Thread.CurrentThread.CurrentCulture = odd;
                var w = new JsonWriter();
                w.BeginArray();
                w.Number(-5L);
                w.Number(-7);
                w.Number(1234567UL);
                w.EndArray();
                Check("json: numbers ignore de-DE culture", w.ToString() == "[-5,-7,1234567]", w.ToString());
            }
            finally { Thread.CurrentThread.CurrentCulture = saved; }

            var big = new JsonWriter();
            big.BeginArray();
            big.Number(ulong.MaxValue);
            big.Number(long.MinValue);
            big.EndArray();
            Check("json: ulong max prints all digits", big.ToString() == "[18446744073709551615,-9223372036854775808]", big.ToString());

            var shape = new JsonWriter();
            shape.BeginObject();
            shape.Name("a"); shape.Number(1);
            shape.Name("b"); shape.BeginArray(); shape.Number(1); shape.Number(2); shape.BeginObject(); shape.EndObject(); shape.EndArray();
            shape.Name("c"); shape.Null();
            shape.Name("d"); shape.Bool(true);
            shape.Name("e"); shape.Bool(false);
            shape.Name("q" + (char)0x22); shape.String("v");
            shape.EndObject();
            Check("json: commas, nesting and escaped names", shape.ToString()
                == "{\"a\":1,\"b\":[1,2,{}],\"c\":null,\"d\":true,\"e\":false,\"q" + Bs + "\"\":\"v\"}", shape.ToString());
        }

        // ---------------------------------------------------------------- shared data folder

        static void PathTests()
        {
            string data = AppPaths.DataFolder;
            Check("paths: the data folder is rooted and named RamOptimiser", Path.IsPathRooted(data)
                && string.Equals(Path.GetFileName(data), "RamOptimiser", StringComparison.OrdinalIgnoreCase), data);
            Check("paths: settings.ini sits in the data folder", string.Equals(Path.GetDirectoryName(Gui.SettingsStore.DefaultPath),
                data, StringComparison.OrdinalIgnoreCase), Gui.SettingsStore.DefaultPath);
            Check("paths: cleans.log sits in the data folder", CleanLog.FileName == "cleans.log"
                && string.Equals(CleanLog.DefaultPath, Path.Combine(data, "cleans.log"), StringComparison.OrdinalIgnoreCase), CleanLog.DefaultPath);

            // An account with no profile folder gets "" from Windows: never a path relative to the current directory
            // (often C:\Windows\System32 for an elevated or scheduled process).
            Check("paths: no local app data means no data folder", AppPaths.DataFolderFor("") == null && AppPaths.DataFolderFor(null) == null);
            Check("paths: a relative base is refused", AppPaths.DataFolderFor("Local") == null && AppPaths.DataFolderFor(@"\Users\a") == null
                && AppPaths.DataFolderFor("C:Local") == null);
            Check("paths: a full base gets the folder", AppPaths.DataFolderFor(@"C:\Users\a\AppData\Local") == @"C:\Users\a\AppData\Local\RamOptimiser");
        }

        // ---------------------------------------------------------------- record format

        static readonly DateTime When = new DateTime(2026, 11, 17, 14, 32, 5, DateTimeKind.Utc);

        static CleanReport SampleReport()
        {
            var r = new CleanReport
            {
                Before = new MemorySnapshot { TotalPhys = 16UL << 30, AvailablePhys = 3UL << 30, LoadPercent = 81,
                    CommitTotal = 71, CommitLimit = 100, Condition = MemoryCondition.Neutral },
                After = new MemorySnapshot { TotalPhys = 16UL << 30, AvailablePhys = 5UL << 30, LoadPercent = 74,
                    CommitTotal = 70, CommitLimit = 100, Condition = MemoryCondition.High },
                Passes = 2,
                Duration = TimeSpan.FromMilliseconds(3210.4),
                CombinedBytes = 125829120,
            };
            r.Results.Add(new AreaResult { Area = Areas.CombinePages, Ok = true, Message = "merged 120.0 MB", Duration = TimeSpan.FromMilliseconds(410) });
            r.Results.Add(new AreaResult { Area = Areas.VolumeCache, Ok = true, Partial = true, Message = "C: flushed", Duration = TimeSpan.FromMilliseconds(5) });
            r.Results.Add(new AreaResult { Area = Areas.StandbyList, Skipped = true, Message = "needs admin" });
            r.Results.Add(new AreaResult { Area = Areas.RegistryCache, Ok = false, Message = "line one\r\nline two" });
            r.Services.Add(new ServiceAction { Name = "SysMain", Outcome = ServiceOutcome.Stopped });
            r.Services.Add(new ServiceAction { Name = "Spooler", Outcome = ServiceOutcome.StopFailed, Message = "access denied" });
            return r;
        }

        static Dictionary<string, object> Parse(string line)
        {
            try { return new JavaScriptSerializer().DeserializeObject(line) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        static object At(Dictionary<string, object> o, string path)
        {
            object cur = o;
            foreach (string part in path.Split('.'))
            {
                int index;
                if (int.TryParse(part, out index))
                {
                    var arr = cur as object[];
                    if (arr == null || index >= arr.Length) return null;
                    cur = arr[index];
                }
                else
                {
                    var d = cur as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey(part)) return "(missing)";
                    cur = d[part];
                }
            }
            return cur;
        }

        static long Num(object v) { try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (Exception) { return long.MinValue; } }

        static bool True(object v) { return v is bool && (bool)v; }

        static bool False(object v) { return v is bool && !(bool)v; }

        static int Length(object v)
        {
            if (v is string) return ((string)v).Length;
            var arr = v as object[];
            return arr == null ? -1 : arr.Length;
        }

        static bool HasNoBreaks(string line)
        {
            return line.IndexOf('\n') < 0 && line.IndexOf('\r') < 0 && line.IndexOf((char)0x2028) < 0 && line.IndexOf((char)0x2029) < 0;
        }

        static void FormatTests()
        {
            string line = CleanLog.FormatClean(SampleReport(), "gui", "threshold", "7f3a9c2e", When);
            var o = Parse(line);
            Check("log: clean record parses", o != null, line);
            if (o != null)
            {
                Check("log: clean header fields", Num(At(o, "v")) == 1 && (string)At(o, "type") == "clean" && (string)At(o, "id") == "7f3a9c2e"
                    && (string)At(o, "time") == "2026-11-17T14:32:05Z" && (string)At(o, "source") == "gui"
                    && (string)At(o, "reason") == "threshold" && (string)At(o, "stopped") == "none"
                    && Num(At(o, "ms")) == 3210 && Num(At(o, "passes")) == 2, line);
                Check("log: before and after", Num(At(o, "before.load")) == 81 && Num(At(o, "before.inUse")) == (13L << 30)
                    && Num(At(o, "before.commitPercent")) == 71 && (string)At(o, "before.condition") == "neutral"
                    && Num(At(o, "after.load")) == 74 && (string)At(o, "after.condition") == "high", line);
                Check("log: freed and combined bytes", Num(At(o, "freed")) == (2L << 30) && Num(At(o, "combinedBytes")) == 125829120, line);
                Check("log: area rows", (string)At(o, "areas.0.name") == "combine" && (string)At(o, "areas.0.outcome") == "ok"
                    && Num(At(o, "areas.0.ms")) == 410 && (string)At(o, "areas.0.message") == "merged 120.0 MB"
                    && (string)At(o, "areas.1.outcome") == "partial" && (string)At(o, "areas.2.outcome") == "skipped"
                    && (string)At(o, "areas.3.name") == "registry" && (string)At(o, "areas.3.outcome") == "failed", line);
                Check("log: service rows", (string)At(o, "services.0.name") == "SysMain" && True(At(o, "services.0.stopped"))
                    && False(At(o, "services.0.failed")) && True(At(o, "services.1.failed")) && At(o, "truncated") as string == "(missing)", line);
            }

            // Messages are cleaned to one line (SafeText.Line): each break becomes a space.
            Check("log: one record is one line", HasNoBreaks(line) && (string)At(o, "areas.3.message") == "line one  line two", line);
            Check("log: the stop overload writes its value", (string)At(Parse(CleanLog.FormatClean(SampleReport(), "watch", "interval",
                "timed-out", "00000001", When)), "stopped") == "timed-out");
            Check("log: a local time is written as UTC", (string)At(Parse(CleanLog.FormatClean(SampleReport(), "cli", "manual", "a",
                When.ToLocalTime())), "time") == "2026-11-17T14:32:05Z");

            // A record whose message tries to forge a second record stays one line and one record.
            CleanReport forged = SampleReport();
            forged.Results[0].Message = "x\"}\n{\"v\":1,\"type\":\"clean\"" + (char)0x2028;
            string forgedLine = CleanLog.FormatClean(forged, "gui", "manual", "b", When);
            Check("log: a message cannot forge a record", HasNoBreaks(forgedLine) && Parse(forgedLine) != null
                && (string)At(Parse(forgedLine), "areas.0.message") == "x\"} {\"v\":1,\"type\":\"clean\" ", forgedLine);

            CleanReport longMessage = SampleReport();
            longMessage.Results[0].Message = new string('m', 500);
            longMessage.Results[1].Message = new string('n', 199) + new string(new[] { (char)0xD83D, (char)0xDE00 });
            string capped = CleanLog.FormatClean(longMessage, "gui", "manual", "c", When);
            var co = Parse(capped);
            Check("log: message capped at 200 chars", co != null && Length(At(co, "areas.0.message")) == 200
                && Length(At(co, "areas.1.message")) <= 200 && Length(At(co, "areas.1.message")) >= 199 && capped.IndexOf(U(0xFFFD), StringComparison.Ordinal) < 0, capped);

            CleanReport manyAreas = SampleReport();
            for (int i = 0; i < 60; i++)
                manyAreas.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = true, Message = new string('w', 200) });
            string dropped = CleanLog.FormatClean(manyAreas, "gui", "manual", "d", When);
            var dob = Parse(dropped);
            Check("log: a line over 8 KB drops messages first", dob != null && Encoding.UTF8.GetByteCount(dropped) <= CleanLog.MaxLineBytes
                && At(dob, "areas.0.message") as string == "(missing)" && (string)At(dob, "areas.63.name") == "workingsets"
                && (string)At(dob, "services.0.name") == "SysMain" && True(At(dob, "truncated")),
                Encoding.UTF8.GetByteCount(dropped) + " bytes");

            CleanReport manyServices = SampleReport();
            for (int i = 0; i < 60; i++) manyServices.Services.Add(new ServiceAction { Name = new string('s', 200), Outcome = ServiceOutcome.Stopped });
            string noServices = CleanLog.FormatClean(manyServices, "gui", "manual", "e", When);
            var so = Parse(noServices);
            Check("log: then services", so != null && Encoding.UTF8.GetByteCount(noServices) <= CleanLog.MaxLineBytes
                && (string)At(so, "areas.0.name") == "combine" && Length(At(so, "services")) == 0 && True(At(so, "truncated")),
                Encoding.UTF8.GetByteCount(noServices) + " bytes");

            CleanReport noAfter = SampleReport();
            noAfter.After = null;
            var na = Parse(CleanLog.FormatClean(noAfter, "gui", "manual", "f", When));
            Check("log: a missing after snapshot is null, freed too", na != null && At(na, "after") == null && At(na, "freed") == null);

            var snap = new MemorySnapshot { TotalPhys = 16UL << 30, AvailablePhys = 4UL << 30, LoadPercent = 79 };
            string rebound = CleanLog.FormatRebound("7f3a9c2e", When.AddMinutes(5).AddSeconds(1), snap);
            Check("log: rebound record", rebound == "{\"v\":1,\"type\":\"rebound\",\"ref\":\"7f3a9c2e\",\"time\":\"2026-11-17T14:37:06Z\",\"load\":79,\"inUse\":"
                + (12UL << 30).ToString(CultureInfo.InvariantCulture) + "}", rebound);

            string id = CleanLog.NewId(), other = CleanLog.NewId();
            bool hex = id.Length == 8;
            foreach (char c in id) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) hex = false;
            Check("log: ids are 8 hex characters and differ", hex && id != other, id + " " + other);
        }

        // ---------------------------------------------------------------- the writer

        static void AppendTests(string dir)
        {
            string folder = Path.Combine(dir, "plain", "RamOptimiser");
            string file = Path.Combine(folder, CleanLog.FileName);
            Check("log: append creates the folder and writes one line", CleanLog.TryAppend(folder, "{\"a\":1}")
                && File.ReadAllText(file) == "{\"a\":1}\n");
            Check("log: a second append adds a line", CleanLog.TryAppend(folder, "{\"a\":2}")
                && File.ReadAllText(file) == "{\"a\":1}\n{\"a\":2}\n");
            byte[] raw = File.ReadAllBytes(file);
            Check("log: UTF-8 without a BOM", raw.Length > 0 && raw[0] == (byte)'{');
            Check("log: a line with a break is refused", !CleanLog.TryAppend(folder, "{}\n{}") && !CleanLog.TryAppend(folder, "{}\r")
                && File.ReadAllText(file) == "{\"a\":1}\n{\"a\":2}\n");
            Check("log: a line over 8 KB is refused", !CleanLog.TryAppend(folder, new string('x', CleanLog.MaxLineBytes + 1))
                && CleanLog.TryAppend(folder, new string('x', CleanLog.MaxLineBytes))
                && new FileInfo(file).Length == 16 + CleanLog.MaxLineBytes + 1);

            string rot = Path.Combine(dir, "rotate");
            Directory.CreateDirectory(rot);
            string log = Path.Combine(rot, CleanLog.FileName), old = log + ".old";
            File.WriteAllBytes(log, Filler(CleanLog.MaxBytes - 10));
            File.WriteAllText(old, "stale");
            string line = "{\"v\":1,\"type\":\"rebound\"}";
            bool appended = CleanLog.TryAppend(rot, line);
            Check("log: rotates past 256 KB keeping one old file", appended && File.ReadAllText(log) == line + "\n"
                && new FileInfo(old).Length == CleanLog.MaxBytes - 10 && Directory.GetFiles(rot).Length == 2,
                string.Join(", ", Directory.GetFiles(rot)));
            File.WriteAllBytes(log, Filler(CleanLog.MaxBytes - 100));
            Check("log: a line that fits does not rotate", CleanLog.TryAppend(rot, "{}") && new FileInfo(log).Length == CleanLog.MaxBytes - 97
                && new FileInfo(old).Length == CleanLog.MaxBytes - 10);

            // A reader holding cleans.log without delete sharing (a tail tool, an editor) blocks the rename: the older
            // generation must survive and the line must still be written, a little past the cap.
            string held = Path.Combine(dir, "rotate-held");
            Directory.CreateDirectory(held);
            string heldLog = Path.Combine(held, CleanLog.FileName), heldOld = heldLog + ".old";
            File.WriteAllBytes(heldLog, Filler(CleanLog.MaxBytes - 10));
            File.WriteAllText(heldOld, "older");
            bool heldAppended;
            using (new FileStream(heldLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                heldAppended = CleanLog.TryAppend(held, line);
            string heldText = File.ReadAllText(heldLog);
            string oldText = File.Exists(heldOld) ? File.ReadAllText(heldOld) : "(deleted)";
            Check("log: a reader blocking rotation keeps the old file and still writes the line", heldAppended
                && oldText == "older" && heldText.EndsWith(line + "\n", StringComparison.Ordinal)
                && new FileInfo(heldLog).Length == CleanLog.MaxBytes - 10 + line.Length + 1,
                heldAppended + " old=" + oldText + " log=" + new FileInfo(heldLog).Length);
            // Once the reader is gone, the next append rotates as usual.
            Check("log: rotation resumes after the reader closes", CleanLog.TryAppend(held, line) && File.ReadAllText(heldLog) == line + "\n"
                && new FileInfo(heldOld).Length == CleanLog.MaxBytes - 10 + line.Length + 1);

            // Rotations back to back: the second append finds a small file and leaves the full generation alone.
            string twice = Path.Combine(dir, "rotate-twice");
            Directory.CreateDirectory(twice);
            string twiceLog = Path.Combine(twice, CleanLog.FileName), twiceOld = twiceLog + ".old";
            File.WriteAllBytes(twiceLog, Filler(CleanLog.MaxBytes));
            bool a1 = CleanLog.TryAppend(twice, line), a2 = CleanLog.TryAppend(twice, line);
            Check("log: back-to-back appends rotate once", a1 && a2 && new FileInfo(twiceOld).Length == CleanLog.MaxBytes
                && File.ReadAllText(twiceLog) == line + "\n" + line + "\n", new FileInfo(twiceOld).Length.ToString());
        }

        static byte[] Filler(long n)
        {
            var b = new byte[n];
            for (long i = 0; i < n; i++) b[i] = (i % 64 == 63) ? (byte)'\n' : (byte)'x';
            return b;
        }

        static void LinkTests(string dir)
        {
            string real = Path.Combine(dir, "real");
            Directory.CreateDirectory(real);
            string junction = Path.Combine(dir, "junction");
            bool madeJunction = Mklink("/J", junction, real);
            Check("test setup: junction created", madeJunction);
            if (madeJunction)
            {
                Check("log: refuses a linked folder", !CleanLog.TryAppend(junction, "{}") && Directory.GetFiles(real).Length == 0,
                    string.Join(", ", Directory.GetFiles(real)));
                Directory.Delete(junction, false); // removes the link only
            }

            string hard = Path.Combine(dir, "hard");
            Directory.CreateDirectory(hard);
            string victim = Path.Combine(dir, "victim.txt");
            File.WriteAllText(victim, "keep");
            bool madeHardLink = Mklink("/H", Path.Combine(hard, CleanLog.FileName), victim);
            Check("test setup: hard link created", madeHardLink);
            if (madeHardLink)
                Check("log: refuses a hard-linked file", !CleanLog.TryAppend(hard, "{}") && File.ReadAllText(victim) == "keep");

            string hardOld = Path.Combine(dir, "hard-old");
            Directory.CreateDirectory(hardOld);
            string victim2 = Path.Combine(dir, "victim2.txt");
            File.WriteAllText(victim2, "keep");
            bool madeOld = Mklink("/H", Path.Combine(hardOld, CleanLog.FileName + ".old"), victim2);
            Check("test setup: hard-linked old file created", madeOld);
            if (madeOld)
                Check("log: refuses a hard-linked old file", !CleanLog.TryAppend(hardOld, "{}") && File.ReadAllText(victim2) == "keep"
                    && !File.Exists(Path.Combine(hardOld, CleanLog.FileName)));
        }

        static void FailureTests(string dir)
        {
            string notFolder = Path.Combine(dir, "a-file");
            File.WriteAllText(notFolder, "x");
            bool threw = false, any = false;
            try
            {
                any |= CleanLog.TryAppend(notFolder, "{}");
                any |= CleanLog.TryAppend(null, "{}");
                any |= CleanLog.TryAppend("", "{}");
                any |= CleanLog.TryAppend(dir + Path.DirectorySeparatorChar + "bad|name", "{}");
                any |= CleanLog.TryAppend(Path.Combine(dir, "ok"), null);
            }
            catch (Exception) { threw = true; }

            string ro = Path.Combine(dir, "readonly");
            Directory.CreateDirectory(ro);
            string roFile = Path.Combine(ro, CleanLog.FileName);
            File.WriteAllText(roFile, "");
            File.SetAttributes(roFile, FileAttributes.ReadOnly);
            try
            {
                try { any |= CleanLog.TryAppend(ro, "{}"); }
                catch (Exception) { threw = true; }
            }
            finally { File.SetAttributes(roFile, FileAttributes.Normal); }
            Check("log: never throws on an unwritable folder", !threw && !any);

            string busy = Path.Combine(dir, "busy");
            Directory.CreateDirectory(busy);
            DateTime start = DateTime.UtcNow;
            bool wrote;
            using (new FileStream(Path.Combine(busy, CleanLog.FileName), FileMode.Create, FileAccess.Write, FileShare.Read))
                wrote = CleanLog.TryAppend(busy, "{}");
            Check("log: a file another writer holds gives up after the retries", !wrote && DateTime.UtcNow - start < TimeSpan.FromSeconds(5));

            // The other writer (the app or ramopt.exe) finishes during the retries: the line gets written.
            string brief = Path.Combine(dir, "brief");
            Directory.CreateDirectory(brief);
            string briefLog = Path.Combine(brief, CleanLog.FileName);
            var holder = new FileStream(briefLog, FileMode.Create, FileAccess.Write, FileShare.Read);
            var release = new System.Threading.Thread(() => { System.Threading.Thread.Sleep(60); holder.Dispose(); });
            release.Start();
            bool retried = CleanLog.TryAppend(brief, "{\"r\":1}");
            release.Join();
            Check("log: a retry succeeds once the other writer lets go", retried && File.ReadAllText(briefLog) == "{\"r\":1}\n",
                retried + " " + (File.Exists(briefLog) ? File.ReadAllText(briefLog) : "missing"));
        }

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
    }
}
