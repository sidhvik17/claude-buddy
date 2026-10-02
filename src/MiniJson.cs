using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClaudeBuddy
{
    // Minimal JSON reader. Objects become Dictionary<string, object>, arrays List<object>,
    // numbers double, plus string / bool / null. Enough for Claude Code's state files
    // without loading System.Web.Extensions into a process that runs all day.
    internal static class MiniJson
    {
        const int MaxDepth = 256;

        public static object Parse(string s)
        {
            int i = 0;
            object v = ReadValue(s, ref i, 0);
            SkipWs(s, ref i);
            if (i != s.Length) throw new FormatException("trailing data at " + i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string s)
        {
            return Parse(s) as Dictionary<string, object>;
        }

        public static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
            string str = v as string;
            if (str != null) return str;
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is bool) return ((bool)v) ? "true" : "false";
            return "";
        }

        public static long GetLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            if (v is double)
            {
                double dv = (double)v;
                if (double.IsNaN(dv) || dv > long.MaxValue || dv < long.MinValue) return 0;
                return (long)dv;
            }
            string str = v as string;
            long parsed;
            if (str != null && long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return 0;
        }

        public static bool GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return false;
            return (v is bool) && (bool)v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        static object ReadValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("nesting too deep");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i, depth);
            if (c == '[') return ReadArray(s, ref i, depth);
            if (c == '"') return ReadString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ReadNumber(s, ref i);
        }

        static Dictionary<string, object> ReadObject(string s, ref int i, int depth)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected key at " + i);
                string key = ReadString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                d[key] = ReadValue(s, ref i, depth + 1);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end in object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        static List<object> ReadArray(string s, ref int i, int depth)
        {
            List<object> list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ReadValue(s, ref i, depth + 1));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end in array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        static string ReadString(string s, ref int i)
        {
            i++; // opening quote
            int start = i;
            // Fast path: no escapes.
            while (i < s.Length && s[i] != '"' && s[i] != '\\') i++;
            if (i >= s.Length) throw new FormatException("unterminated string");
            if (s[i] == '"')
            {
                string plain = s.Substring(start, i - start);
                i++;
                return plain;
            }

            StringBuilder sb = new StringBuilder(s, start, i - start, Math.Max(16, (i - start) * 2));
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
                        int code;
                        if (!int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                            throw new FormatException("bad \\u escape");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new FormatException("bad escape \\" + e);
                }
            }
        }

        static object ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i == start) throw new FormatException("unexpected character '" + s[start] + "' at " + start);
            double d;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("bad number at " + start);
            return d;
        }

        static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("expected " + word + " at " + i);
            i += word.Length;
        }
    }
}
