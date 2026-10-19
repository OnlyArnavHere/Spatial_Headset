using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// The strict JSON of the link frames. Reads one UTF-8 JSON object: strict UTF-8, nesting depth at most 64 (the
    /// top-level object is depth 1), no NaN/Infinity (or numbers that overflow to it), duplicate keys last-wins.
    /// Objects come back as Dictionary&lt;string, object&gt;, arrays as List&lt;object&gt;, strings as string, integers as
    /// long (BigInteger when they do not fit) and numbers with a fraction or exponent as double, so <c>1.0</c> stays
    /// distinct from <c>1</c>. Anything that makes parsing fail in any way is "malformed frame".
    /// </summary>
    public static class LinkJson
    {
        public const int MaxDepth = 64;
        public const int MaxIntegerDigits = 4300;
        public const int MaxNumberChars = 4400;

        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static Dictionary<string, object> ParseObject(byte[] body)
        {
            string text;
            try
            {
                text = StrictUtf8.GetString(body);
            }
            catch (Exception)
            {
                throw new LinkException("malformed frame");
            }
            return ParseObject(text);
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            try
            {
                var reader = new Reader(text);
                return reader.ReadDocument();
            }
            catch (Exception)
            {
                throw new LinkException("malformed frame");
            }
        }

        /// <summary>True if the value is the JSON integer <paramref name="expected"/> (not 1.0, "1" or true).</summary>
        public static bool IsInteger(object value, long expected)
        {
            return value is long l && l == expected;
        }

        /// <summary>The string under <paramref name="key"/>, or null when missing or not a string.</summary>
        public static string GetString(Dictionary<string, object> obj, string key)
        {
            return obj.TryGetValue(key, out var v) ? v as string : null;
        }

        // --- Writer ------------------------------------------------------------------------------------------

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        public static byte[] ToBytes(object value)
        {
            return new UTF8Encoding(false).GetBytes(Serialize(value));
        }

        static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: WriteString(sb, s); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case BigInteger bi: sb.Append(bi.ToString(CultureInfo.InvariantCulture)); break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException("NaN/Infinity cannot be written.");
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    break;
                default:
                    throw new ArgumentException("Cannot write " + value.GetType().Name + " as JSON.");
            }
        }

        static void WriteString(StringBuilder sb, string s)
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // --- Reader ------------------------------------------------------------------------------------------

        sealed class Reader
        {
            readonly string s;
            int pos;

            public Reader(string text)
            {
                s = text ?? throw new FormatException();
            }

            public Dictionary<string, object> ReadDocument()
            {
                SkipWhitespace();
                if (pos >= s.Length || s[pos] != '{') throw new FormatException();
                var obj = ReadObject(1);
                SkipWhitespace();
                if (pos != s.Length) throw new FormatException();
                return obj;
            }

            void SkipWhitespace()
            {
                while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r')) pos++;
            }

            object ReadValue(int depth)
            {
                SkipWhitespace();
                if (pos >= s.Length) throw new FormatException();
                char c = s[pos];
                switch (c)
                {
                    case '{': return ReadObject(depth + 1);
                    case '[': return ReadArray(depth + 1);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw new FormatException();
                }
            }

            void Expect(string literal)
            {
                if (string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0) throw new FormatException();
                pos += literal.Length;
            }

            Dictionary<string, object> ReadObject(int depth)
            {
                if (depth > MaxDepth) throw new FormatException();
                pos++; // {
                var obj = new Dictionary<string, object>();
                SkipWhitespace();
                if (pos < s.Length && s[pos] == '}')
                {
                    pos++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (pos >= s.Length || s[pos] != '"') throw new FormatException();
                    string key = ReadString();
                    SkipWhitespace();
                    if (pos >= s.Length || s[pos] != ':') throw new FormatException();
                    pos++;
                    obj[key] = ReadValue(depth);
                    SkipWhitespace();
                    if (pos >= s.Length) throw new FormatException();
                    if (s[pos] == ',') { pos++; continue; }
                    if (s[pos] == '}') { pos++; return obj; }
                    throw new FormatException();
                }
            }

            List<object> ReadArray(int depth)
            {
                if (depth > MaxDepth) throw new FormatException();
                pos++; // [
                var list = new List<object>();
                SkipWhitespace();
                if (pos < s.Length && s[pos] == ']')
                {
                    pos++;
                    return list;
                }
                while (true)
                {
                    list.Add(ReadValue(depth));
                    SkipWhitespace();
                    if (pos >= s.Length) throw new FormatException();
                    if (s[pos] == ',') { pos++; continue; }
                    if (s[pos] == ']') { pos++; return list; }
                    throw new FormatException();
                }
            }

            string ReadString()
            {
                pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (pos >= s.Length) throw new FormatException();
                    char c = s[pos++];
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw new FormatException();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (pos >= s.Length) throw new FormatException();
                    char e = s[pos++];
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
                            if (pos + 4 > s.Length) throw new FormatException();
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int h = HexValue(s[pos + i]);
                                if (h < 0) throw new FormatException();
                                code = (code << 4) | h;
                            }
                            pos += 4;
                            sb.Append((char)code);
                            break;
                        default: throw new FormatException();
                    }
                }
            }

            static int HexValue(char c)
            {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                return -1;
            }

            bool IsDigit() { return pos < s.Length && s[pos] >= '0' && s[pos] <= '9'; }

            object ReadNumber()
            {
                int start = pos;
                bool isFloat = false;
                if (s[pos] == '-') pos++;
                if (pos >= s.Length) throw new FormatException();
                if (s[pos] == '0') pos++;
                else if (s[pos] >= '1' && s[pos] <= '9') { while (IsDigit()) pos++; }
                else throw new FormatException();
                if (pos < s.Length && s[pos] == '.')
                {
                    isFloat = true;
                    pos++;
                    if (!IsDigit()) throw new FormatException();
                    while (IsDigit()) pos++;
                }
                if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
                {
                    isFloat = true;
                    pos++;
                    if (pos < s.Length && (s[pos] == '+' || s[pos] == '-')) pos++;
                    if (!IsDigit()) throw new FormatException();
                    while (IsDigit()) pos++;
                }
                int length = pos - start;
                if (length > MaxNumberChars) throw new FormatException();
                if (!isFloat && length - (s[start] == '-' ? 1 : 0) > MaxIntegerDigits) throw new FormatException(); // as Python's 4300
                string text = s.Substring(start, length);
                if (isFloat)
                {
                    double d = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException();
                    return d;
                }
                var big = BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (big >= long.MinValue && big <= long.MaxValue) return (long)big;
                return big;
            }
        }
    }
}
