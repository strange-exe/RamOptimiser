using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RamOptimiser
{
    /// <summary>
    /// A small forward-only JSON writer (RFC 8259) for the clean log and --json output. Pure: no I/O, no culture.
    /// Strings escape the quote, the backslash, every control character (C0, DEL, C1), every BMP format character
    /// (bidi, zero-width) and U+2028 and U+2029 (line breaks to JavaScript and some log viewers), so a written value
    /// can never break a JSON Lines record or act on a terminal (Hidden). A lone surrogate
    /// becomes U+FFFD, which keeps the output valid UTF-8. Numbers use the invariant culture. The writer does not
    /// check that names and values alternate correctly; its callers are this program's own fixed shapes.
    /// </summary>
    public sealed class JsonWriter
    {
        const char Backslash = (char)0x5C;
        const int Replacement = 0xFFFD;

        readonly StringBuilder sb = new StringBuilder();
        // One entry per open object or array: true until its first member is written.
        readonly Stack<bool> first = new Stack<bool>();
        bool afterName;

        public void BeginObject() { BeforeValue(); sb.Append('{'); first.Push(true); }

        public void EndObject() { first.Pop(); sb.Append('}'); }

        public void BeginArray() { BeforeValue(); sb.Append('['); first.Push(true); }

        public void EndArray() { first.Pop(); sb.Append(']'); }

        public void Name(string name)
        {
            Separate();
            AppendQuoted(sb, name ?? "");
            sb.Append(':');
            afterName = true;
        }

        public void String(string value)
        {
            BeforeValue();
            if (value == null) sb.Append("null");
            else AppendQuoted(sb, value);
        }

        public void Number(long value) { BeforeValue(); sb.Append(value.ToString(CultureInfo.InvariantCulture)); }

        public void Number(ulong value) { BeforeValue(); sb.Append(value.ToString(CultureInfo.InvariantCulture)); }

        public void Number(int value) { BeforeValue(); sb.Append(value.ToString(CultureInfo.InvariantCulture)); }

        public void Bool(bool value) { BeforeValue(); sb.Append(value ? "true" : "false"); }

        public void Null() { BeforeValue(); sb.Append("null"); }

        public override string ToString() { return sb.ToString(); }

        void BeforeValue()
        {
            if (afterName) { afterName = false; return; }
            Separate();
        }

        void Separate()
        {
            if (first.Count == 0) return;
            if (first.Peek()) { first.Pop(); first.Push(false); }
            else sb.Append(',');
        }

        /// <summary>Appends s as a JSON string literal, quotes included.</summary>
        public static void AppendQuoted(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == Backslash) { sb.Append(Backslash).Append(c); continue; }
                if (c == '\n') { sb.Append(Backslash).Append('n'); continue; }
                if (c == '\r') { sb.Append(Backslash).Append('r'); continue; }
                if (c == '\t') { sb.Append(Backslash).Append('t'); continue; }
                if (Hidden(c)) { AppendEscape(sb, c); continue; }
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        // A supplementary format character (the tag characters U+E0001 and U+E0020-U+E007F) is
                        // escaped as its two surrogates, which JSON readers join back into the one character.
                        if (CharUnicodeInfo.GetUnicodeCategory(s, i) == UnicodeCategory.Format) { AppendEscape(sb, c); AppendEscape(sb, s[i + 1]); }
                        else sb.Append(c).Append(s[i + 1]);
                        i++;
                    }
                    else AppendEscape(sb, Replacement);
                    continue;
                }
                if (char.IsLowSurrogate(c)) { AppendEscape(sb, Replacement); continue; }
                sb.Append(c);
            }
            sb.Append('"');
        }

        /// <summary>
        /// A code unit written as a \u escape although RFC 8259 allows most of these raw: every control character (C0,
        /// DEL and the C1 controls, among them NEL, which some log viewers break lines at, and the 8-bit CSI and OSC a
        /// terminal may act on), every BMP format character (bidi overrides and isolates, zero-width marks, the BOM)
        /// and U+2028/U+2029. Values such as process names come from other users, so whatever the console code page or
        /// viewer, nothing written can move the cursor, reorder the text or start a line. A surrogate is never Hidden:
        /// AppendQuoted judges a valid pair by its code point, writing it as it is unless it is a format character
        /// (then as two escapes, one per surrogate). CharUnicodeInfo, not char.GetUnicodeCategory: on .NET Framework the latter
        /// reads the soft hyphen U+00AD as DashPunctuation, though Unicode makes it a format character.
        /// </summary>
        static bool Hidden(char c)
        {
            UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat == UnicodeCategory.Control || cat == UnicodeCategory.Format
                || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator;
        }

        static void AppendEscape(StringBuilder sb, int codeUnit)
        {
            sb.Append(Backslash).Append('u').Append(codeUnit.ToString("x4", CultureInfo.InvariantCulture));
        }
    }
}
