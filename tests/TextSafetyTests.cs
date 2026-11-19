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
            SoftHyphenAndSpaces();
            DefaultIgnorables();
            Unassigned();
            return failed;
        }

        // ---------------------------------------------------------------- unassigned code points in the identity line

        /// <summary>
        /// .NET Framework 4.8's category table predates format characters added since (the Egyptian hieroglyph format
        /// controls U+13430-1343F, the Arabic pound and piastre marks above U+0890-0891, U+08E2, the Kaithi number sign
        /// U+110CD), so it reads them as unassigned (Cn), not Cf. The identity line marks exactly those (the Cf code
        /// points of Unicode 18.0 the runtime does not know); labels (Visible) are unchanged. Letters and symbols
        /// assigned since Unicode 8.0 read as unassigned too, and stay as they are.
        /// </summary>
        static void Unassigned()
        {
            int[] unassigned = { 0x13430, 0x13438, 0x13439, 0x1343F, 0x0890, 0x0891, 0x08E2, 0x110CD };
            foreach (int cp in unassigned)
            {
                string s = char.ConvertFromUtf32(cp);
                string hex = cp.ToString("X4", CultureInfo.InvariantCulture);
                int width;
                UnicodeCategory cat = SafeText.CategoryAt(s, 0, out width);
                string m = SafeText.Marked(@"C:\Prog" + s + @"ram Files\x.exe", "?");
                Check("unassigned U+" + hex + " (" + cat + "): marked once in the identity line", m == @"C:\Prog?ram Files\x.exe", m);
            }
            // Letters and symbols assigned after Unicode 8.0, which this runtime reads as unassigned: CJK (U+9FD6, 9FEB,
            // 9FEF, 9FFC, 2CEB0, 30000), Georgian Mtavruli, Cyrillic Extended-C, Syriac and Malayalam additions, Latin
            // (U+A7B8), Adlam, Osage, the Reiwa sign, two emoji and the Egyptian mark just past the format controls
            // (U+13440). A user or folder name with one of them is a real path: never marked. Nor is U+E1000, unassigned
            // even now, just past the variation selectors: it draws as a box, not as nothing.
            int[] later = { 0x9FD6, 0x9FEB, 0x9FEF, 0x9FFC, 0x2CEB0, 0x30000, 0x1C90, 0x1C80, 0x0860, 0x0D00, 0xA7B8, 0x1E900,
                0x104B0, 0x32FF, 0x1F923, 0x1F970, 0x13440, 0xE1000 };
            int readAsUnassigned = 0;
            foreach (int cp in later)
            {
                string s = char.ConvertFromUtf32(cp);
                string hex = cp.ToString("X4", CultureInfo.InvariantCulture);
                int width;
                UnicodeCategory cat = SafeText.CategoryAt(s, 0, out width);
                if (cat == UnicodeCategory.OtherNotAssigned) readAsUnassigned++;
                string text = @"C:\Users\" + s + @"\app.exe";
                Check("later U+" + hex + " (" + cat + "): not marked in the identity line", SafeText.Marked(text, "?") == text, SafeText.Marked(text, "?"));
                Check("later U+" + hex + ": kept in labels", SafeText.Visible("a" + s + "b") == "a" + s + "b", SafeText.Visible("a" + s + "b"));
            }
            Check("later code points: this runtime reads most as unassigned (the case under test)", readAsUnassigned >= later.Length / 2,
                readAsUnassigned + " of " + later.Length);

            // Assigned characters beside them stay: an Egyptian hieroglyph (U+13000), an Arabic letter (U+08A0), a Kaithi
            // letter (U+1108D) and a plain accented letter.
            foreach (int cp in new[] { 0x13000, 0x1342E, 0x08A0, 0x1108D, 0x00E9 })
            {
                string s = char.ConvertFromUtf32(cp);
                string hex = cp.ToString("X4", CultureInfo.InvariantCulture);
                int width;
                UnicodeCategory cat = SafeText.CategoryAt(s, 0, out width);
                string text = @"C:\Prog" + s + @"ram Files\x.exe";
                Check("assigned U+" + hex + " (" + cat + "): not marked", cat != UnicodeCategory.OtherNotAssigned && SafeText.Marked(text, "?") == text,
                    SafeText.Marked(text, "?"));
            }

            KnownItem discord = StartupKnowledge.FindApp("Discord.exe");
            var k = new StartupApp { Source = StartupSource.UserRun, Name = "Discord", Known = discord,
                ExePath = @"C:\Prog" + char.ConvertFromUtf32(0x13430) + @"ram Files\Discord\Discord.exe" };
            string d = AppText.AppDetail(k, Win);
            Check("startup detail: an unassigned format character in the path does not read as Program Files",
                !d.Contains(@"Runs C:\Program Files\Discord\Discord.exe") && d.Contains(@"Runs C:\Prog?ram Files\Discord\Discord.exe"), d);
        }

        // ---------------------------------------------------------------- fix-up: invisible characters outside Cf/Cc

        /// <summary>
        /// Every Default_Ignorable_Code_Point range (Unicode 18.0 DerivedCoreProperties.txt) draws nothing, whatever its
        /// category: the combining grapheme joiner and variation selectors are Mn, the Hangul fillers Lo, and the
        /// reserved ones Cn. U+2800 (braille blank) draws as a space. A crafted 'Program Files' with any of them must
        /// not read like the real one.
        /// </summary>
        static void DefaultIgnorables()
        {
            int[] hidden = { 0x034F, 0x061C, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180B, 0x180D, 0x180E, 0x180F, 0x2065, 0x3164,
                0xFE00, 0xFE0F, 0xFFA0, 0xFFF0, 0xFFF8, 0x1BCA0, 0x1D173, 0xE0000, 0xE0002, 0xE0080, 0xE0100, 0xE01EF, 0xE01F0, 0xE0FFF };
            foreach (int cp in hidden)
            {
                string s = char.ConvertFromUtf32(cp);
                string hex = cp.ToString("X4", CultureInfo.InvariantCulture);
                string m = SafeText.Marked(@"C:\Prog" + s + @"ram Files\x.exe", "?");
                Check("ignorable U+" + hex + ": marked once in the identity line", m == @"C:\Prog?ram Files\x.exe", m);
                string v = SafeText.Visible("a" + s + "b");
                Check("ignorable U+" + hex + ": dropped from labels", v == "ab", v);
            }

            string blank = ((char)0x2800).ToString();
            string mb = SafeText.Marked(@"C:\Program" + blank + @"Files\x.exe", "?");
            Check("braille blank: marked in the identity line", mb == @"C:\Program?Files\x.exe", mb);
            string vb = SafeText.Visible("a" + blank + "b");
            Check("braille blank: a label shows an ASCII space", vb == "a b", vb);

            // Visible characters next to those ranges stay as they are: an accent, a Hangul syllable, an emoji, braille
            // with dots, and code points just outside the ignorable ranges.
            int[] shown = { 0x0301, 0x1161, 0x17B6, 0x180A, 0x2801, 0x3165, 0xAC00, 0xFE10, 0xFFA1, 0x1F600 };
            foreach (int cp in shown)
            {
                string s = char.ConvertFromUtf32(cp);
                string hex = cp.ToString("X4", CultureInfo.InvariantCulture);
                string text = "a" + s + "b";
                Check("visible U+" + hex + ": not marked", SafeText.Marked(text, "?") == text, SafeText.Marked(text, "?"));
                Check("visible U+" + hex + ": kept in labels", SafeText.Visible(text) == text, SafeText.Visible(text));
            }

            KnownItem discord = StartupKnowledge.FindApp("Discord.exe");
            var k = new StartupApp { Source = StartupSource.UserRun, Name = "Discord", Known = discord,
                ExePath = @"C:\Prog" + (char)0x034F + @"ram Files\Discord\Discord.exe" };
            string d = AppText.AppDetail(k, Win);
            Check("startup detail: a grapheme joiner in the path does not read as Program Files",
                !d.Contains(@"Runs C:\Program Files\Discord\Discord.exe") && d.Contains(@"Runs C:\Prog?ram Files\Discord\Discord.exe"), d);
        }

        // ---------------------------------------------------------------- final review: soft hyphen, look-alike spaces

        static void SoftHyphenAndSpaces()
        {
            string shy = ((char)0xAD).ToString();
            int width;
            Check("soft hyphen: read as a format character", SafeText.CategoryAt("a" + shy, 1, out width) == UnicodeCategory.Format && width == 1,
                SafeText.CategoryAt("a" + shy, 1, out width).ToString());
            Check("soft hyphen: marked", SafeText.Marked("Disc" + shy + "ord", "?") == "Disc?ord", SafeText.Marked("Disc" + shy + "ord", "?"));
            Check("soft hyphen: dropped from labels", SafeText.Visible("Disc" + shy + "ord") == "Discord", SafeText.Visible("Disc" + shy + "ord"));
            Check("soft hyphen: Line drops it", SafeText.Line("a" + shy + "b", 200) == "ab", SafeText.Line("a" + shy + "b", 200));

            // Spaces other than U+0020 look like one in a path, so the identity line marks them and labels normalise them.
            foreach (int sp in new[] { 0x00A0, 0x2007, 0x202F, 0x3000, 0x2002 })
            {
                string s = ((char)sp).ToString();
                string hex = sp.ToString("X4", CultureInfo.InvariantCulture);
                string m = SafeText.Marked(@"C:\Program" + s + @"Files\x.exe", "?");
                Check("look-alike space U+" + hex + ": marked", m == @"C:\Program?Files\x.exe", m);
                string v = SafeText.Visible("a" + s + "b");
                Check("look-alike space U+" + hex + ": a label shows an ASCII space", v == "a b", v);
            }
            Check("look-alike space: an ASCII space is not marked", SafeText.Marked(@"C:\Program Files\x.exe", "?") == @"C:\Program Files\x.exe");

            KnownItem discord = StartupKnowledge.FindApp("Discord.exe");
            var k = new StartupApp { Source = StartupSource.UserRun, Name = "Discord", Known = discord,
                ExePath = @"C:\Program" + (char)0xA0 + @"Files\Discord\Discord.exe" };
            string d = AppText.AppDetail(k, Win);
            Check("startup detail: a no-break space in the path does not read as Program Files",
                !d.Contains(@"Runs C:\Program Files\Discord\Discord.exe") && d.Contains(@"Runs C:\Program?Files\Discord\Discord.exe"), d);
            string label = AppText.StartupLabel(new StartupApp { Source = StartupSource.UserRun, Name = "Evil" + (char)0x202F + "app" + shy });
            Check("startup label: a narrow no-break space shows as a space, a soft hyphen goes", label == "Evil app", label);
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

            // A valid surrogate pair is written as it is, unless its code point is a format character (a tag
            // character): that is written as two escapes, one per surrogate, which is still valid JSON.
            string smile = char.ConvertFromUtf32(0x1F600), tag = char.ConvertFromUtf32(0xE0041), han = char.ConvertFromUtf32(0x20000);
            Check("json: ordinary surrogate pairs stay as they are", Quote(smile + han) == "\"" + smile + han + "\"", Quote(smile + han));
            string tagQuoted = Quote(smile + tag);
            Check("json: a supplementary format character is escaped as its two surrogates",
                tagQuoted == "\"" + smile + U(tag[0]) + U(tag[1]) + "\"", tagQuoted);
            object tagBack = null;
            try { tagBack = new JavaScriptSerializer().DeserializeObject(tagQuoted); } catch (Exception) { }
            Check("json: the escaped pair reads back as the same character", (tagBack as string) == smile + tag, tagQuoted);
            var rawTags = new List<string>();
            foreach (int cp in new[] { 0xE0001, 0xE0020, 0xE0041, 0xE007F })
            {
                string t = char.ConvertFromUtf32(cp);
                string got = Quote("a" + t + "b");
                if (got != "\"a" + U(t[0]) + U(t[1]) + "b\"" && rawTags.Count < 5) rawTags.Add(cp.ToString("X5", CultureInfo.InvariantCulture));
            }
            Check("json: every tag character is escaped", rawTags.Count == 0, string.Join(",", rawTags.ToArray()));

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
            Check("startup detail: the Runs path shows each hidden character as ?", d.Contains(@"Runs C:\Users\x\a?b?c?d?\Discord.exe") && !HasHidden(d), d);
            Check("startup detail: the stored path is unchanged", k.ExePath == path);

            k.ExePath = null;
            k.Name = "Disc" + Rlo + "ord" + Lsep;
            d = AppText.AppDetail(k, Win);
            Check("startup detail: the name used when there is no path is marked too", d.Contains("Runs Disc?ord?") && !HasHidden(d), d);

            // The 'Runs' line is the row's only identity check (the list matches by file name), so a path that differs
            // from a real one only by hidden characters must not read as that path. '?' cannot be in a Windows path.
            string real = @"C:\Program Files\Discord\Discord.exe";
            k.ExePath = @"C:\Program" + Rlo + @" Files\Discord\Discord.exe";
            d = AppText.AppDetail(k, Win);
            Check("startup detail: a path padded with hidden characters does not read as the real one",
                !d.Contains("Runs " + real) && d.Contains(@"Runs C:\Program? Files\Discord\Discord.exe"), d);
            k.ExePath = real;
            Check("startup detail: a plain path is shown as it is", AppText.AppDetail(k, Win).Contains("Runs " + real));

            // SafeText.Marked: one mark per hidden code point (a pair counts once), visible text untouched.
            string smile = char.ConvertFromUtf32(0x1F600), tag = char.ConvertFromUtf32(0xE0041);
            string marked = SafeText.Marked("a" + Rlo + "b" + Zw + Lsep + Nel + Bel + smile + tag + (char)0xD800 + "z", "?");
            Check("marked: each hidden code point becomes one mark", marked == "a?b????" + smile + "??z", marked);
            Check("marked: null and empty give empty", SafeText.Marked(null, "?") == "" && SafeText.Marked("", "?") == "");
            Check("marked: plain text is unchanged", SafeText.Marked(real, "?") == real);

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
