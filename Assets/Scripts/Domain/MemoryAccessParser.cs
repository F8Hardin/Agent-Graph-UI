using System;
using System.Collections.Generic;
using System.Text;

namespace AgentConsole.Domain
{
    /// <summary>
    /// How strongly a vault file was touched. The numeric order is the upgrade order: a file that was only a search
    /// hit becomes Read when it is opened and Written when it is changed, and never goes back down.
    /// </summary>
    public enum MemoryAccessKind
    {
        Hit = 0,
        Read = 1,
        Written = 2
    }

    /// <summary>One vault file a successful memory tool call touched, and how.</summary>
    public readonly struct MemoryTouch
    {
        public string Path { get; }
        public MemoryAccessKind Kind { get; }

        public MemoryTouch(string path, MemoryAccessKind kind)
        {
            Path = path;
            Kind = kind;
        }
    }

    /// <summary>
    /// Turns one finished tool call into the vault files it touched. Pure, engine-free and tested, because "no view
    /// parses tool names or tool output" (STANDARDS section 3): the memory tree reads the result of this and nothing else.
    ///
    /// The payloads are NOT clean JSON. EVA logs a tool result as the python repr of an MCP result,
    /// <c>content=[{'type': 'text', 'text': '{"path": "Eva/eva.md", ...</c>, so the real JSON sits inside a single-quoted
    /// string, its quotes sometimes backslash-escaped, and the output is cut off mid-string when it is long. A JSON parse
    /// would fail on all three. Rejected: stripping the wrapper and parsing the rest (breaks on the truncation), and a
    /// regex (cannot say "the closing quote is missing, so this value is unusable"). So this is a small scanner for the
    /// one key it needs, <c>"path"</c>, which tolerates all of that and drops a value whose closing quote never came.
    /// </summary>
    public static class MemoryAccessParser
    {
        /// <summary>A path longer than this is not a vault path; it is a runaway value or a truncated blob.</summary>
        public const int MaxPathLength = 240;

        private static readonly IReadOnlyList<MemoryTouch> None = new MemoryTouch[0];

        /// <summary>True for the four vault tools, matched on the bare name after the <c>server__</c> prefix.</summary>
        public static bool IsMemoryTool(string tool) => TryKindOf(tool, out _);

        /// <summary>The access kind a successful call of this tool produces. Search is Hit, read is Read, write and append are Written.</summary>
        public static bool TryKindOf(string tool, out MemoryAccessKind kind)
        {
            kind = MemoryAccessKind.Hit;
            switch (BareName(tool))
            {
                case "vault_search":
                    return true;
                case "vault_read":
                    kind = MemoryAccessKind.Read;
                    return true;
                case "vault_write_note":
                case "vault_append":
                    kind = MemoryAccessKind.Written;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The tool name without its <c>server__</c> prefix, split the same way GraphModel.Split does (first "__").</summary>
        public static string BareName(string tool)
        {
            if (string.IsNullOrEmpty(tool)) return string.Empty;
            int i = tool.IndexOf("__", StringComparison.Ordinal);
            return i > 0 && i + 2 < tool.Length ? tool.Substring(i + 2) : tool;
        }

        /// <summary>
        /// The vault files a finished call touched. A failed call, or any tool that is not a vault tool, yields nothing.
        /// A search yields every path in its output; read, write and append yield one, preferring the path the call was
        /// ASKED for (the input) over the one echoed back (the output), because the output may be cut off before it.
        /// </summary>
        public static IReadOnlyList<MemoryTouch> Parse(string tool, string input, string output, bool succeeded)
        {
            if (!succeeded || !TryKindOf(tool, out MemoryAccessKind kind)) return None;

            List<MemoryTouch> touches = new List<MemoryTouch>();
            if (kind == MemoryAccessKind.Hit)
            {
                List<string> paths = new List<string>();
                ExtractPaths(output, paths);
                for (int i = 0; i < paths.Count; i++)
                {
                    if (!ContainsPath(touches, paths[i])) touches.Add(new MemoryTouch(paths[i], kind));
                }
            }
            else
            {
                string path = FirstPath(input) ?? FirstPath(output);
                if (path != null) touches.Add(new MemoryTouch(path, kind));
            }
            return touches.Count == 0 ? None : touches;
        }

        /// <summary>The first usable <c>"path"</c> value in the text, or null.</summary>
        public static string FirstPath(string text)
        {
            List<string> found = new List<string>(1);
            ExtractPaths(text, found, 1);
            return found.Count > 0 ? found[0] : null;
        }

        /// <summary>
        /// Every usable <c>"path": "value"</c> in the text, in order, normalised. The quotes around the key and the value
        /// may each carry backslashes (the repr escaping); a value with no closing quote (truncated output) is skipped.
        /// </summary>
        public static void ExtractPaths(string text, List<string> into, int max = int.MaxValue)
        {
            if (string.IsNullOrEmpty(text) || into == null) return;
            int n = text.Length;
            int i = 0;
            while (i < n && into.Count < max)
            {
                int q = text.IndexOf("path", i, StringComparison.Ordinal);
                if (q < 0) break;
                i = q + 4;
                if (q == 0 || text[q - 1] != '"') continue; // "abs_path" and prose containing the word are not the key

                int j = i;
                while (j < n && text[j] == '\\') j++;
                if (j >= n || text[j] != '"') continue;
                j++;
                j = SkipSpace(text, j);
                if (j >= n || text[j] != ':') continue;
                j = SkipSpace(text, j + 1);

                int slashes = 0;
                while (j < n && text[j] == '\\')
                {
                    slashes++;
                    j++;
                }
                if (j >= n || text[j] != '"') continue;
                j++;

                string value = ReadValue(text, j, slashes > 0, out int end);
                if (value == null)
                {
                    // Truncated or malformed: nothing after this point in the same value can be trusted, but the scan
                    // may still find a later well-formed key, so carry on from just past the opening quote.
                    i = j;
                    continue;
                }
                i = end;
                string clean = Normalize(value);
                if (clean != null && !into.Contains(clean)) into.Add(clean);
            }
        }

        /// <summary>
        /// A vault path as the tree wants it: forward slashes, no leading "./" or "/", no empty or ".." segments. Null
        /// when it is not a plausible path (empty, too long, a control character, a quote).
        /// </summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxPathLength) return null;
            string[] parts = raw.Trim().Replace('\\', '/').Split('/');
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int p = 0; p < parts.Length; p++)
            {
                string s = parts[p].Trim();
                if (s.Length == 0 || s == ".") continue;
                if (s == "..") return null;
                for (int c = 0; c < s.Length; c++)
                {
                    char ch = s[c];
                    if (ch < 0x20 || ch == 0x7f || ch == '"') return null;
                }
                if (sb.Length > 0) sb.Append('/');
                sb.Append(s);
            }
            return sb.Length == 0 || sb.Length > MaxPathLength ? null : sb.ToString();
        }

        private static int SkipSpace(string text, int j)
        {
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            return j;
        }

        private static bool ContainsPath(List<MemoryTouch> list, string path)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Path, path, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Read a value up to its closing quote. In plain JSON a backslash starts an escape; inside the repr-wrapped form
        /// (<paramref name="escaped"/>) the closing quote is itself preceded by a backslash, which is not part of the value.
        /// Returns null if the quote never comes or a control character shows up first.
        /// </summary>
        private static string ReadValue(string text, int start, bool escaped, out int end)
        {
            StringBuilder sb = new StringBuilder();
            int n = text.Length;
            for (int k = start; k < n; k++)
            {
                char c = text[k];
                if (c == '"')
                {
                    end = k + 1;
                    return sb.ToString();
                }
                if (c < 0x20)
                {
                    end = k;
                    return null;
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (k + 1 >= n) break;
                char e = text[k + 1];
                if (escaped)
                {
                    if (e == '"') continue; // the backslash of the escaped closing quote; the quote ends the value next turn
                    // The repr doubles the backslash of a JSON \uXXXX escape.
                    if (e == '\\' && k + 5 < n && text[k + 2] == 'u' && TryHex(text, k + 3, out char u))
                    {
                        sb.Append(u);
                        k += 6;
                        continue;
                    }
                    sb.Append('\\');
                    continue;
                }
                if (e == '/')
                {
                    sb.Append('/');
                    k++;
                }
                else if (e == '\\')
                {
                    sb.Append('\\');
                    k++;
                }
                else if (e == 'u' && TryHex(text, k + 2, out char uc))
                {
                    sb.Append(uc);
                    k += 5;
                }
                else
                {
                    // \" inside a plain value is a literal quote, \n a newline: neither belongs in a vault path.
                    end = k;
                    return null;
                }
            }
            end = n;
            return null;
        }

        private static bool TryHex(string s, int at, out char c)
        {
            c = '\0';
            if (at + 4 > s.Length) return false;
            int v = 0;
            for (int i = 0; i < 4; i++)
            {
                char h = s[at + i];
                int d = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                if (d < 0) return false;
                v = v * 16 + d;
            }
            c = (char)v;
            return true;
        }
    }
}
