using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgentConsole.Transport
{
    /// <summary>
    /// A small JSON reader/writer with no UnityEngine dependency. JsonUtility cannot be used here
    /// (the Transport assembly references no engine code, and EVA's <c>data</c> field is a string
    /// for some events and an object for others). Parses to Dictionary&lt;string,object&gt;,
    /// List&lt;object&gt;, string, double, bool and null.
    /// </summary>
    public static class MiniJson
    {
        private const int MaxDepth = 64;

        public static bool TryParse(string json, out object value)
        {
            value = null;
            if (json == null) return false;
            try
            {
                Reader r = new Reader(json);
                r.SkipWhitespace();
                value = r.ReadValue(0);
                r.SkipWhitespace();
                if (!r.AtEnd)
                {
                    value = null;
                    return false;
                }
                return true;
            }
            catch (FormatException)
            {
                value = null;
                return false;
            }
        }

        /// <summary>Compact JSON for a value built from the types TryParse produces, plus the other numeric types.</summary>
        public static string Serialize(object value)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        /// <summary>A JSON string literal, quotes included.</summary>
        public static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder(s == null ? 4 : s.Length + 2);
            WriteString(sb, s);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON nesting too deep");
            switch (v)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case float f:
                    sb.Append(((double)f).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    bool firstKey = true;
                    foreach (KeyValuePair<string, object> kv in dict)
                    {
                        if (!firstKey) sb.Append(',');
                        firstKey = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (object item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Write(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    break;
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
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
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _i;

            public Reader(string s)
            {
                _s = s;
            }

            public bool AtEnd => _i >= _s.Length;

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                    else break;
                }
            }

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw new FormatException("JSON nesting too deep");
                if (AtEnd) throw new FormatException("Unexpected end of JSON");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': ExpectLiteral("true"); return true;
                    case 'f': ExpectLiteral("false"); return false;
                    case 'n': ExpectLiteral("null"); return null;
                    default: return ReadNumber();
                }
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                _i++; // {
                SkipWhitespace();
                if (!AtEnd && _s[_i] == '}')
                {
                    _i++;
                    return result;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != '"') throw new FormatException("Expected object key");
                    string key = ReadString();
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != ':') throw new FormatException("Expected ':'");
                    _i++;
                    SkipWhitespace();
                    result[key] = ReadValue(depth + 1);
                    SkipWhitespace();
                    if (AtEnd) throw new FormatException("Unterminated object");
                    char c = _s[_i++];
                    if (c == ',') continue;
                    if (c == '}') return result;
                    throw new FormatException("Expected ',' or '}'");
                }
            }

            private List<object> ReadArray(int depth)
            {
                List<object> result = new List<object>();
                _i++; // [
                SkipWhitespace();
                if (!AtEnd && _s[_i] == ']')
                {
                    _i++;
                    return result;
                }
                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd) throw new FormatException("Unterminated array");
                    char c = _s[_i++];
                    if (c == ',') continue;
                    if (c == ']') return result;
                    throw new FormatException("Expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                _i++; // opening quote
                int start = _i;
                // Fast path: no escapes, so the result is a plain substring.
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == '"')
                    {
                        string plain = _s.Substring(start, _i - start);
                        _i++;
                        return plain;
                    }
                    if (c == '\\') break;
                    _i++;
                }
                if (_i >= _s.Length) throw new FormatException("Unterminated string");

                StringBuilder sb = new StringBuilder(_s, start, _i - start, (_i - start) + 16);
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (_i >= _s.Length) break;
                    char e = _s[_i++];
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
                            if (_i + 4 > _s.Length) throw new FormatException("Bad \\u escape");
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            {
                                throw new FormatException("Bad \\u escape");
                            }
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default:
                            throw new FormatException("Bad escape");
                    }
                }
                throw new FormatException("Unterminated string");
            }

            private object ReadNumber()
            {
                int start = _i;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _i++;
                    else break;
                }
                if (_i == start) throw new FormatException("Unexpected character");
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    throw new FormatException("Bad number");
                }
                return d;
            }

            private void ExpectLiteral(string literal)
            {
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException("Bad literal");
                }
                _i += literal.Length;
            }
        }
    }
}
