// Package fx-text (audit run-4 record 8, record 3, hardening 20, 21, 47, 78): lower-trust text on its way out.
// The JSON writer escapes every control, format and separator character, so --json output and cleans.log are safe in
// any viewer or terminal; Startup row labels and the 'Runs <path>' line are shown through SafeText while the stored
// name the switches act on stays raw; clean records clean their area messages and error text before the cap.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using RamOptimiser.App;

namespace RamOptimiser.Tests
{
    public static class TextSafetyTests
    {
        static int failed, passed;

        public static int Passed { get { return passed; } }

        // One backslash, so expected escapes are built in code rather than typed as escape sequences.
        static readonly string Bs = new string((char)0x5C, 1);
        static readonly string Rlo = ((char)0x202E).ToString(), Lsep = ((char)0x2028).ToString(),
            Nel = ((char)0x0085).ToString(), Bel = ((char)7).ToString(), Zw = ((char)0x200B).ToString();

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) { passed++; return; }
            failed++;
            Console.WriteLine("FAIL " + name + (detail == null ? "" : ": " + detail));
        }

        public static int Run()
        {
            JsonEscapeTests();
            StartupDisplayTests();
            StartupWiringTests();
            CleanLogTextTests();
            return failed;
        }

        static string Quote(string s)
        {
            var w = new JsonWriter();
            w.String(s);
            return w.ToString();
        }

        static string U(int codeUnit) { return Bs + "u" + codeUnit.ToString("x4", CultureInfo.InvariantCulture); }

        /// <summary>True when s holds a control, format or separator character, or a lone surrogate.</summary>
        static bool HasHidden(string s)
        {
            if (s == null) return false;
            for (int i = 0; i < s.Length; )
            {
                int width;
                UnicodeCategory cat = SafeText.CategoryAt(s, i, out width);
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.Surrogate
                    || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator) return true;
                i += width;
            }
            return false;
        }

        // ---------------------------------------------------------------- record 8, hardening 20 and 47

        static void JsonEscapeTests()
        {
            foreach (int c in new[] { 0x7F, 0x80, 0x85, 0x9B, 0x9C, 0x9D, 0x9F })
            {
                string got = Quote("a" + (char)c + "b");
                Check("json: escapes DEL and C1 U+" + c.ToString("X4", CultureInfo.InvariantCulture),
                    got == "\"a" + U(c) + "b\"", got);
            }
            foreach (int c in new[] { 0x00AD, 0x061C, 0x200B, 0x200C, 0x200D, 0x200E, 0x200F, 0x202A, 0x202B, 0x202C,
                0x202D, 0x202E, 0x2060, 0x2066, 0x2067, 0x2068, 0x2069, 0xFEFF })
            {
                string got = Quote("a" + (char)c + "b");
                Check("json: escapes format character U+" + c.ToString("X4", CultureInfo.InvariantCulture),
                    got == "\"a" + U(c) + "b\"", got);
            }

            // Every BMP character: hidden ones are escaped, every other one (letters in any script) is written as is.
            var wrong = new List<string>();
            for (int c = 0x20; c <= 0xFFFF; c++)
            {
                char ch = (char)c;
                if (char.IsSurrogate(ch) || ch == '"' || ch == (char)0x5C) continue;
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                bool hidden = cat == UnicodeCategory.Control || cat == UnicodeCategory.Format
                    || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator;
                string got = Quote(ch.ToString());
                bool ok = hidden ? got == "\"" + U(c) + "\"" : got == "\"" + ch + "\"";
                if (!ok && wrong.Count < 5) wrong.Add(c.ToString("X4", CultureInfo.InvariantCulture));
            }
            Check("json: hidden BMP characters escaped, all others unchanged", wrong.Count == 0, string.Join(",", wrong.ToArray()));

            string letters = "caf" + (char)0xE9 + " Stra" + (char)0xDF + "e " + (char)0x416 + (char)0x4E2D + " " + (char)0x928;
            Check("json: ordinary non-ASCII letters unchanged", Quote(letters) == "\"" + letters + "\"", Quote(letters));

            // A valid surrogate pair is written as it is, a supplementary format character (a tag) included.
            string smile = char.ConvertFromUtf32(0x1F600), tag = char.ConvertFromUtf32(0xE0041);
            Check("json: valid surrogate pairs stay as they are", Quote(smile + tag) == "\"" + smile + tag + "\"", Quote(smile + tag));

            string mixed = "x" + Rlo + Nel + (char)0x7F + Zw + (char)0xFEFF + letters + smile + Lsep + "y";
            string quoted = Quote(mixed);
            bool rawHidden = false;
            foreach (char ch in quoted)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format) rawHidden = true;
            }
            object back = null;
            try { back = new JavaScriptSerializer().DeserializeObject(quoted); } catch (Exception) { }
            Check("json: escaped output holds no raw hidden character and reads back unchanged",
                !rawHidden && (back as string) == mixed, quoted);
        }

        // ---------------------------------------------------------------- record 3, hardening 78

        const string Win = @"C:\Windows";

        static void StartupDisplayTests()
        {
            // An unknown row is labelled with its own name, which a same-user process chose: shown cleaned, stored raw.
            string raw = "Evil" + Rlo + "gpj.exe" + Lsep + "Keep on" + Nel + Bel + "x";
            var a = new StartupApp { Source = StartupSource.UserRun, Name = raw };
            string label = AppText.StartupLabel(a);
            Check("startup label: control, bidi and separator characters dropped", label == "Evilgpj.exeKeep onx", label);
            Check("startup label: the stored name the switch acts on is unchanged", a.Name == raw);
            Check("startup label: switch name is clean", !HasHidden(AppText.StartupSwitchName(label, false, AppText.LookCloserText)));
            Check("startup label: locked row name is clean",
                !HasHidden(AppText.StartupLockedName(label, "Needs admin", true, false, AppText.LookCloserText)));
            Check("startup label: unknown row detail is clean", !HasHidden(AppText.AppDetail(a, Win)), AppText.AppDetail(a, Win));

            var longName = new StartupApp { Source = StartupSource.UserRun, Name = new string('n', 100) };
            string longLabel = AppText.StartupLabel(longName);
            Check("startup label: a long name is cut like a process name", longLabel.Length <= 64 && longLabel.EndsWith("..."), longLabel);
            Check("startup label: a name with nothing visible reads (no name)",
                AppText.StartupLabel(new StartupApp { Source = StartupSource.UserRun, Name = Rlo + Zw }) == SafeText.NoName);

            // A known row keeps the list's label; its 'Runs <path>' line is the identity check, so the path is cleaned.
            KnownItem discord = StartupKnowledge.FindApp("Discord.exe");
            string path = @"C:\Users\x\a" + Rlo + "b" + Lsep + "c" + Nel + "d" + Bel + @"\Discord.exe";
            var k = new StartupApp { Source = StartupSource.UserRun, Name = "Discord", ExePath = path, Known = discord };
            Check("startup label: a known row keeps the list's label", discord != null && AppText.StartupLabel(k) == discord.Label);
            string d = AppText.AppDetail(k, Win);
            Check("startup detail: the Runs path is shown cleaned", d.Contains(@"Runs C:\Users\x\abcd\Discord.exe") && !HasHidden(d), d);
            Check("startup detail: the stored path is unchanged", k.ExePath == path);

            k.ExePath = null;
            k.Name = "Disc" + Rlo + "ord" + Lsep;
            d = AppText.AppDetail(k, Win);
            Check("startup detail: the name used when there is no path is cleaned too", d.Contains("Runs Discord") && !HasHidden(d), d);

            string longPath = @"C:\Users\x\" + new string('p', 300) + @"\Discord.exe";
            k.ExePath = longPath;
            d = AppText.AppDetail(k, Win);
            Check("startup detail: a long path is never cut", d.Contains("Runs " + longPath), d);

            // A packaged row's name is the manifest DisplayName, which the package publisher chose.
            string shown = "Contoso" + Rlo + " tray" + Lsep + Nel + Bel + "x";
            var p = new StartupApp { Source = StartupSource.Packaged, Name = PackagedStartup.DisplayNameFor(shown, "Contoso.Tray_abc") };
            string pl = AppText.StartupLabel(p);
            Check("startup label: a packaged DisplayName is shown cleaned", pl == "Contoso trayx", pl);
            Check("startup label: packaged detail is clean", !HasHidden(AppText.AppDetail(p, Win)), AppText.AppDetail(p, Win));
        }

        static string SrcFile(params string[] parts)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..");
            path = Path.Combine(path, "src");
            foreach (string part in parts) path = Path.Combine(path, part);
            return Path.GetFullPath(path);
        }

        static void StartupWiringTests()
        {
            string shellPath = SrcFile("App", "AppShell.cs");
            if (!File.Exists(shellPath)) { Check("startup wiring: AppShell.cs found", false, shellPath); return; }
            string shell = File.ReadAllText(shellPath);
            int at = shell.IndexOf("List<StartupItem> CurrentItems(bool apps)", StringComparison.Ordinal);
            int end = at < 0 ? -1 : shell.IndexOf("\n        }\n", at, StringComparison.Ordinal);
            string items = at < 0 ? "" : (end < 0 ? shell.Substring(at) : shell.Substring(at, end - at));
            Check("startup wiring: app rows take their label from AppText.StartupLabel",
                items.Contains("Label = AppText.StartupLabel(a)") && !items.Contains("a.Known.Label : a.Name"), items);
            Check("startup wiring: app rows take their detail from AppText.AppDetail", items.Contains("Detail = AppText.AppDetail(a)"), items);
        }

        // ---------------------------------------------------------------- hardening 21

        static Dictionary<string, object> Parse(string line)
        {
            try { return new JavaScriptSerializer().DeserializeObject(line) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        static string AreaMessage(Dictionary<string, object> o)
        {
            if (o == null || !o.ContainsKey("areas")) return null;
            var areas = o["areas"] as object[];
            if (areas == null || areas.Length == 0) return null;
            var area = areas[0] as Dictionary<string, object>;
            return area == null || !area.ContainsKey("message") ? null : area["message"] as string;
        }

        static string Field(Dictionary<string, object> o, string name)
        {
            return o == null || !o.ContainsKey(name) ? null : o[name] as string;
        }

        static void CleanLogTextTests()
        {
            DateTime when = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
            string dirty = "a" + Rlo + "b\r\nc" + Lsep + Nel + Zw + "d";
            var r = new CleanReport();
            r.Results.Add(new AreaResult { Area = Areas.WorkingSets, Ok = false, Message = dirty });
            string line = CleanLog.FormatClean(r, "gui", "manual", "failed", "0000000a", when, dirty);
            var o = Parse(line);
            Check("clean log: area message sanitised", AreaMessage(o) == "ab  c d", AreaMessage(o));
            Check("clean log: error text sanitised", Field(o, "error") == "ab  c d", Field(o, "error"));

            // Sanitised before the cap: a dropped character must not cost a kept one.
            string edge = new string('x', 199) + Rlo + "yz";
            r.Results[0].Message = edge;
            line = CleanLog.FormatClean(r, "gui", "manual", "failed", "0000000b", when, edge);
            o = Parse(line);
            string expected = new string('x', 199) + "y";
            Check("clean log: area message sanitised before the cap", AreaMessage(o) == expected, AreaMessage(o));
            Check("clean log: error text sanitised before the cap", Field(o, "error") == expected, Field(o, "error"));

            r.Results[0].Message = null;
            line = CleanLog.FormatClean(r, "gui", "manual", "none", "0000000c", when, null);
            o = Parse(line);
            var area = o == null ? null : ((object[])o["areas"])[0] as Dictionary<string, object>;
            Check("clean log: no message and no error still write no fields", area != null && !area.ContainsKey("message")
                && !o.ContainsKey("error"), line);
        }
    }
}
