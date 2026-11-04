// Json.cs - the project's own JSON writer + recursive-descent parser.
//
// Moved verbatim out of ZwcHelper.cs (plan 5.1 batch 1: Json + PipeIo extraction). The class body
// below is a pure move: no logic, no escaping rule, no number format and no error message changed.
//
// Contract that must not change:
//   * output has no spaces, object keys keep insertion order (JMap.Keys);
//   * string escaping is minimal JSON: \" \\ \b \f \n \r \t, other control chars as \uXXXX
//     (lower-case hex, 4 digits); every other character is emitted raw;
//   * doubles use the round-trip "R" format, NaN/Infinity become null, integers stay integral;
//   * parser returns long for integer tokens (no '.'/'e') and double otherwise;
//   * malformed input throws FormatException instead of returning a half-parsed value.
//
// JMap (the ordered map type the writer walks) stays in ZwcHelper.cs because the whole command
// layer uses it; the offline suite in tests\jsonself\ compiles against a minimal JMap stand-in.
//
// ParseFile reads UTF-8 without a BOM (UTF8Encoding(false)) and tolerates a BOM at the start.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ZwcHelper
{
    internal static class Json
    {
        // ---------------------------------------------------------------- writer
        public static string Write(object v)
        {
            StringBuilder sb = new StringBuilder(4096);
            WriteValue(sb, v);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            string s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is JMap) { WriteMap(sb, (JMap)v); return; }
            List<object> list = v as List<object>;
            if (list != null) { WriteList(sb, list); return; }
            if (v is int || v is long) { sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double)
            {
                double d = (double)v;
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is float) { sb.Append(((float)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is decimal) { sb.Append(((decimal)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is Array)
            {
                List<object> tmp = new List<object>();
                foreach (object o in (Array)v) tmp.Add(o);
                WriteList(sb, tmp);
                return;
            }
            System.Collections.IEnumerable en = v as System.Collections.IEnumerable;
            if (en != null)
            {
                List<object> tmp = new List<object>();
                foreach (object o in en) tmp.Add(o);
                WriteList(sb, tmp);
                return;
            }
            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static void WriteList(StringBuilder sb, List<object> list)
        {
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteValue(sb, list[i]);
            }
            sb.Append(']');
        }

        private static void WriteMap(StringBuilder sb, JMap m)
        {
            sb.Append('{');
            for (int i = 0; i < m.Keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteString(sb, m.Keys[i]);
                sb.Append(':');
                WriteValue(sb, m.Get(m.Keys[i]));
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------------------------------------------------------- parser
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("empty JSON");
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i < text.Length) throw new FormatException("trailing garbage at " + i);
            return v;
        }

        public static object ParseFile(string path)
        {
            return Parse(File.ReadAllText(path, new UTF8Encoding(false)));
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0)
                throw new FormatException("expected " + lit + " at " + i);
            i += lit.Length;
        }

        private static JMap ParseObject(string s, ref int i)
        {
            JMap m = new JMap();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return m; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected key at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected : at " + i);
                i++;
                object val = ParseValue(s, ref i);
                m.Add(key, val);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return m; }
                throw new FormatException("expected , or } at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> l = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("expected , or ] at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("bad escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape \\" + e);
                }
            }
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i == start) throw new FormatException("expected value at " + start);
            string tok = s.Substring(start, i - start);
            long li;
            if (tok.IndexOf('.') < 0 && tok.IndexOf('e') < 0 && tok.IndexOf('E') < 0 && long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out li))
                return li;
            double d;
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("bad number " + tok);
        }
    }
}
