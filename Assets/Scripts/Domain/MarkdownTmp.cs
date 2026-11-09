using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgentConsole.Domain
{
    /// <summary>
    /// Colours the converter writes into the rich text, as "#RRGGBB" (or "#RRGGBBAA" for the highlight). Plain strings
    /// so Domain stays free of engine types; the View fills them from the theme and rebuilds on a theme change.
    /// </summary>
    public sealed class MarkdownStyle
    {
        public string Accent = "#3df07a";
        public string Heading = "#ffffff";
        public string Muted = "#888888";
        public string CodeMark = "#3df07a38";
    }

    public enum MdBlockKind
    {
        Text,
        Code
    }

    /// <summary>One run of output: TMP rich text, or the body of a fenced code block (already escaped).</summary>
    public readonly struct MdBlock
    {
        public MdBlockKind Kind { get; }
        public string Text { get; }

        /// <summary>The info string after the opening fence ("csharp"), or empty.</summary>
        public string Lang { get; }

        public MdBlock(MdBlockKind kind, string text, string lang)
        {
            Kind = kind;
            Text = text;
            Lang = lang ?? string.Empty;
        }
    }

    /// <summary>
    /// Markdown subset to TextMeshPro rich text: bold, italic, strike, inline code, links, headings, bullet and
    /// numbered lists, blockquotes, rules and fenced code. Pure and engine-free so it is tested headlessly.
    ///
    /// Fenced code comes back as its own block rather than inline markup. A text component can highlight a run
    /// (&lt;mark&gt;) but not paint a rectangle behind a paragraph, and the darker block is what makes code read as code,
    /// so the View gives each one its own panel. Rejected alternative: a mark per line, which draws a ragged strip
    /// only as wide as each line's text.
    ///
    /// Model output is data, not markup: every literal '&lt;' is wrapped in a noparse pair, so a reply containing
    /// "&lt;b&gt;" or "&lt;size=900&gt;" is shown, never obeyed. Escaping each '&lt;' on its own (rather than wrapping a whole
    /// span in one noparse) means no content can contain the closing tag and break out.
    ///
    /// An unclosed marker stays literal until its partner arrives, so a half-streamed reply may show a stray "**"
    /// for a moment and then flip to bold. Lists align their wrapped lines by a hanging indent measured in em; the
    /// console font is monospaced, so a marker plus its space is a whole number of 0.6 em cells.
    /// </summary>
    public static class MarkdownTmp
    {
        /// <summary>A blank source line: an empty line at under half height, so paragraphs breathe without a full blank row.</summary>
        public const string GapTag = "<size=45%>\n</size>";

        /// <summary>
        /// TMP keeps a first-line indent past the end of its paragraph, so without this the negative hang that pulls a
        /// bullet left would also pull the next paragraph left, out past the bubble's padding.
        /// </summary>
        private const string ResetLineIndent = "<line-indent=0>";

        private const float CellEm = 0.6f;
        private const int MaxDepth = 6;

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s ?? string.Empty;
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++) AppendChar(sb, s[i]);
            return sb.ToString();
        }

        public static void Parse(string markdown, MarkdownStyle style, List<MdBlock> into)
        {
            into.Clear();
            if (string.IsNullOrEmpty(markdown)) return;
            style = style ?? new MarkdownStyle();
            string src = markdown.IndexOf('\r') >= 0 ? markdown.Replace("\r\n", "\n").Replace('\r', '\n') : markdown;
            string[] lines = src.Split('\n');
            StringBuilder text = new StringBuilder(src.Length + 32);
            bool gap = false;

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                string t = line.TrimStart();

                if (t.StartsWith("```") || t.StartsWith("~~~"))
                {
                    string fence = t.Substring(0, 3);
                    string lang = t.Substring(3).Trim();
                    int j = i + 1;
                    while (j < lines.Length && !lines[j].TrimStart().StartsWith(fence)) j++;
                    string code = string.Join("\n", lines, i + 1, j - (i + 1));
                    Flush(text, into);
                    gap = false;
                    into.Add(new MdBlock(MdBlockKind.Code, Escape(code), lang));
                    i = j < lines.Length ? j + 1 : j; // an unclosed fence runs to the end: it is still being streamed
                    continue;
                }

                if (t.Length == 0)
                {
                    gap = text.Length > 0;
                    i++;
                    continue;
                }

                int lead = Indent(line);

                int h = 0;
                while (h < t.Length && t[h] == '#') h++;
                if (lead < 4 && h >= 1 && h <= 6 && (h == t.Length || t[h] == ' '))
                {
                    string content = t.Substring(h).Trim();
                    if (content.EndsWith("#")) content = content.TrimEnd('#').TrimEnd();
                    if (text.Length > 0) gap = true; // a heading always opens a paragraph
                    NewLine(text, ref gap);
                    text.Append("<size=").Append(HeadingPercent(h)).Append("%><b><color=").Append(style.Heading).Append('>');
                    Inline(content, 0, content.Length, text, style, 0);
                    text.Append("</color></b></size>");
                    i++;
                    continue;
                }

                if (IsRule(t))
                {
                    NewLine(text, ref gap);
                    text.Append("<color=").Append(style.Muted).Append(">————————————</color>");
                    i++;
                    continue;
                }

                if (t[0] == '>')
                {
                    int k = 0;
                    while (k < t.Length && (t[k] == '>' || t[k] == ' ')) k++;
                    string content = t.Substring(k);
                    NewLine(text, ref gap);
                    float w = 2 * CellEm;
                    text.Append("<indent=").Append(Em(w)).Append("em><line-indent=").Append(Em(-w)).Append("em><color=")
                        .Append(style.Accent).Append("><b>|</b></color> <color=").Append(style.Muted).Append("><i>");
                    Inline(content, 0, content.Length, text, style, 0);
                    text.Append("</i></color></indent>").Append(ResetLineIndent);
                    i++;
                    continue;
                }

                string marker = null;
                int bodyAt = 0;
                if (t.Length >= 2 && (t[0] == '-' || t[0] == '*' || t[0] == '+') && t[1] == ' ')
                {
                    marker = "•";
                    bodyAt = 2;
                }
                else
                {
                    int d = 0;
                    while (d < t.Length && d < 9 && t[d] >= '0' && t[d] <= '9') d++;
                    if (d > 0 && d + 1 < t.Length && (t[d] == '.' || t[d] == ')') && t[d + 1] == ' ')
                    {
                        marker = t.Substring(0, d) + ".";
                        bodyAt = d + 2;
                    }
                }

                if (marker != null)
                {
                    string content = t.Substring(bodyAt).TrimStart();
                    NewLine(text, ref gap);
                    float w = (marker.Length + 1) * CellEm;
                    float off = (lead / 2) * 2 * CellEm;
                    text.Append("<indent=").Append(Em(off + w)).Append("em><line-indent=").Append(Em(-w)).Append("em><color=")
                        .Append(style.Accent).Append('>').Append(marker).Append("</color> ");
                    Inline(content, 0, content.Length, text, style, 0);
                    text.Append("</indent>").Append(ResetLineIndent);
                    i++;
                    continue;
                }

                NewLine(text, ref gap);
                string para = line.TrimEnd();
                Inline(para, 0, para.Length, text, style, 0);
                i++;
            }

            Flush(text, into);
        }

        private static void Flush(StringBuilder text, List<MdBlock> into)
        {
            if (text.Length == 0) return;
            into.Add(new MdBlock(MdBlockKind.Text, text.ToString(), null));
            text.Clear();
        }

        private static void NewLine(StringBuilder text, ref bool gap)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
                if (gap) text.Append(GapTag);
            }
            gap = false;
        }

        private static int Indent(string line)
        {
            int n = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ') n++;
                else if (line[i] == '\t') n += 4;
                else break;
            }
            return n;
        }

        private static int HeadingPercent(int level) => level == 1 ? 140 : level == 2 ? 125 : level == 3 ? 112 : 100;

        private static bool IsRule(string t)
        {
            char c = '\0';
            int n = 0;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == ' ') continue;
                if (t[i] != '-' && t[i] != '*' && t[i] != '_') return false;
                if (c == '\0') c = t[i];
                else if (t[i] != c) return false;
                n++;
            }
            return n >= 3;
        }

        private static string Em(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static void AppendChar(StringBuilder sb, char c)
        {
            if (c == '<') sb.Append("<noparse><</noparse>");
            else sb.Append(c);
        }

        private static bool IsAsciiPunct(char c) => c > ' ' && c < 127 && !char.IsLetterOrDigit(c);

        private static void Inline(string s, int start, int end, StringBuilder sb, MarkdownStyle st, int depth)
        {
            int i = start;
            while (i < end)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < end && IsAsciiPunct(s[i + 1]))
                {
                    AppendChar(sb, s[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '`')
                {
                    int run = Run(s, i, end, '`');
                    int close = FindRun(s, i + run, end, '`', run);
                    if (close >= 0)
                    {
                        string inner = s.Substring(i + run, close - (i + run));
                        if (inner.Length >= 2 && inner[0] == ' ' && inner[inner.Length - 1] == ' ' && inner.Trim().Length > 0)
                        {
                            inner = inner.Substring(1, inner.Length - 2);
                        }
                        sb.Append("<mark=").Append(st.CodeMark).Append("> ").Append(Escape(inner)).Append(" </mark>");
                        i = close + run;
                    }
                    else
                    {
                        sb.Append('`', run);
                        i += run;
                    }
                    continue;
                }
                if (c == '[' && depth < MaxDepth && TryLink(s, ref i, end, sb, st, depth)) continue;
                if ((c == '*' || c == '_' || c == '~') && depth < MaxDepth && TryEmphasis(s, start, ref i, end, sb, st, depth)) continue;
                AppendChar(sb, c);
                i++;
            }
        }

        private static int Run(string s, int at, int end, char c)
        {
            int n = 0;
            while (at + n < end && s[at + n] == c) n++;
            return n;
        }

        /// <summary>Start of the first run of exactly <paramref name="len"/> copies of <paramref name="c"/> at or after <paramref name="from"/>, or -1.</summary>
        private static int FindRun(string s, int from, int end, char c, int len)
        {
            int q = from;
            while (q < end)
            {
                if (s[q] == c)
                {
                    int r = Run(s, q, end, c);
                    if (r == len) return q;
                    q += r;
                }
                else q++;
            }
            return -1;
        }

        private static bool TryLink(string s, ref int i, int end, StringBuilder sb, MarkdownStyle st, int depth)
        {
            int nest = 0;
            int k = i;
            for (; k < end; k++)
            {
                if (s[k] == '\\') k++;
                else if (s[k] == '[') nest++;
                else if (s[k] == ']' && --nest == 0) break;
            }
            if (k + 1 >= end || s[k] != ']' || s[k + 1] != '(') return false;

            int u = k + 2;
            int paren = 1;
            int q = u;
            for (; q < end; q++)
            {
                if (s[q] == '(') paren++;
                else if (s[q] == ')' && --paren == 0) break;
            }
            if (q >= end) return false;

            string url = s.Substring(u, q - u).Trim();
            int sp = url.IndexOf(' ');
            if (sp > 0) url = url.Substring(0, sp); // drop an optional "title"
            url = url.Replace("\"", "%22").Replace("<", "%3C").Replace(">", "%3E");
            sb.Append("<link=\"").Append(url).Append("\"><u><color=").Append(st.Accent).Append('>');
            Inline(s, i + 1, k, sb, st, depth + 1);
            sb.Append("</color></u></link>");
            i = q + 1;
            return true;
        }

        private static bool TryEmphasis(string s, int start, ref int i, int end, StringBuilder sb, MarkdownStyle st, int depth)
        {
            char d = s[i];
            int run = Run(s, i, end, d);
            int m;
            if (d == '~')
            {
                if (run < 2) return false;
                m = 2;
            }
            else m = run >= 3 ? 3 : run;

            int after = i + m;
            if (after >= end || char.IsWhiteSpace(s[after])) return false;
            // Underscore inside a word is a name, not emphasis: snake_case_tool must survive.
            if (d == '_' && i > start && char.IsLetterOrDigit(s[i - 1])) return false;

            int closer = -1;
            int j = after;
            while (j < end)
            {
                char x = s[j];
                if (x == '\\')
                {
                    j += 2;
                    continue;
                }
                if (x == '`')
                {
                    int r = Run(s, j, end, '`');
                    int close = FindRun(s, j + r, end, '`', r);
                    j = close < 0 ? j + r : close + r; // a code span hides any marker inside it
                    continue;
                }
                if (x == d)
                {
                    int r = Run(s, j, end, d);
                    bool prevOk = !char.IsWhiteSpace(s[j - 1]);
                    int nx = j + r;
                    bool nextOk = d != '_' || nx >= end || !char.IsLetterOrDigit(s[nx]);
                    // A single marker skips doubled runs (they belong to a nested bold); longer markers take the last cells of the run.
                    bool fits = m == 1 ? r == 1 : r >= m;
                    if (prevOk && nextOk && fits)
                    {
                        closer = j + r - m;
                        break;
                    }
                    j += r;
                    continue;
                }
                j++;
            }
            if (closer < 0) return false;

            sb.Append(d == '~' ? "<s>" : m == 1 ? "<i>" : m == 2 ? "<b>" : "<b><i>");
            Inline(s, after, closer, sb, st, depth + 1);
            sb.Append(d == '~' ? "</s>" : m == 1 ? "</i>" : m == 2 ? "</b>" : "</i></b>");
            i = closer + m;
            return true;
        }
    }
}
