using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NOCursorPilot.Helper
{
    // Minimal dependency-free JSON parser. Tokenizer + recursive descent.
    // Returns object/array/string/double/bool/null as System.Object.
    //   - object  -> Dictionary<string, object>
    //   - array   -> List<object>
    //   - string  -> string
    //   - number  -> double
    //   - bool    -> bool
    //   - null    -> null
    // Throws MiniJson.ParseException on syntax error.
    internal static class MiniJson
    {
        public class ParseException : System.Exception
        {
            public ParseException(string msg) : base(msg) { }
        }

        public static object Parse(string src)
        {
            int i = 0;
            object v = ParseValue(src, ref i);
            SkipWs(src, ref i);
            if (i != src.Length) throw new ParseException($"trailing chars at {i}");
            return v;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new ParseException("unexpected eof");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' || c == 'f') return ParseBool(s, ref i);
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new ParseException($"expected string key at {i}");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new ParseException($"expected ':' at {i}");
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new ParseException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new ParseException($"expected ',' or '}}' at {i}");
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new ParseException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new ParseException($"expected ',' or ']' at {i}");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening "
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) throw new ParseException("bad escape");
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
                            if (i + 4 > s.Length) throw new ParseException("bad unicode escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new ParseException($"bad escape \\{e}");
                    }
                }
                else sb.Append(c);
            }
            throw new ParseException("unterminated string");
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            string num = s.Substring(start, i - start);
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new ParseException($"bad number '{num}'");
            return v;
        }

        private static bool ParseBool(string s, ref int i)
        {
            if (s[i] == 't') { Expect(s, ref i, "true"); return true; }
            Expect(s, ref i, "false"); return false;
        }

        private static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || s.Substring(i, lit.Length) != lit)
                throw new ParseException($"expected '{lit}' at {i}");
            i += lit.Length;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        // === Accessor helpers ===
        public static Dictionary<string, object> Obj(object v) => v as Dictionary<string, object>;
        public static double Num(object v, double fallback) => v is double d ? d : fallback;
        public static float NumF(object v, float fallback) => v is double d ? (float)d : fallback;
        public static bool Bool(object v, bool fallback) => v is bool b ? b : fallback;
        public static string Str(object v, string fallback) => v as string ?? fallback;

        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            d.TryGetValue(key, out object v);
            return v;
        }

        // === Writer (compact, with indentation) ===
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteValue(sb, v, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            switch (v)
            {
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string str: WriteString(sb, str); return;
                case double d: sb.Append(d.ToString("0.######", CultureInfo.InvariantCulture)); return;
                case float f: sb.Append(f.ToString("0.######", CultureInfo.InvariantCulture)); return;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); return;
                case Dictionary<string, object> obj: WriteObject(sb, obj, depth); return;
                case List<object> arr: WriteArray(sb, arr, depth); return;
            }
            WriteString(sb, v.ToString());
        }

        private static void WriteObject(StringBuilder sb, Dictionary<string, object> obj, int depth)
        {
            sb.Append('{');
            bool first = true;
            string pad = new string(' ', (depth + 1) * 2);
            foreach (var kv in obj)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('\n').Append(pad);
                WriteString(sb, kv.Key);
                sb.Append(": ");
                WriteValue(sb, kv.Value, depth + 1);
            }
            if (!first) sb.Append('\n').Append(new string(' ', depth * 2));
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, List<object> arr, int depth)
        {
            sb.Append('[');
            for (int i = 0; i < arr.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                WriteValue(sb, arr[i], depth + 1);
            }
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
