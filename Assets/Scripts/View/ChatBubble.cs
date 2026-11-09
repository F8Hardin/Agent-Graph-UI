using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    internal enum BubbleKind
    {
        User,
        Agent,
        Error,
        Reasoning
    }

    /// <summary>
    /// One transcript message drawn as a bubble: a small speaker label over a rounded fill, the user's on the right and
    /// the agent's on the left. Only the agent's reply is read as Markdown. The user's words and an error are shown
    /// exactly as typed (a literal asterisk in a prompt must stay an asterisk), and reasoning is plain italic.
    ///
    /// A Markdown body is a short stack of segments, text runs and code panels, matched to the parser's blocks by
    /// position and kind. A segment whose text did not change is not touched, so while a reply streams only the
    /// last, growing segment re-lays out its glyphs; the finished paragraphs above it stay as they are.
    /// </summary>
    internal sealed class ChatBubble
    {
        private sealed class Seg
        {
            public RectTransform Root;
            public TextMeshProUGUI Text;
            public TextMeshProUGUI Lang;
            public bool Code;
            public string Cached;
        }

        private const float BodySize = 16f;
        private const float CodeSize = 14f;

        private readonly ThemeBinder _tb;
        private readonly bool _markdown;
        private readonly FontStyles _style;
        private readonly Role _ink;
        private readonly RectTransform _bubble;
        private readonly List<Seg> _segs = new List<Seg>();
        private readonly List<MdBlock> _blocks = new List<MdBlock>();

        public readonly RectTransform Root;
        public readonly TextMeshProUGUI Who;

        public ChatBubble(Transform parent, string name, ThemeBinder tb, BubbleKind kind)
        {
            _tb = tb;
            bool right = kind == BubbleKind.User;
            _markdown = kind == BubbleKind.Agent;
            _style = kind == BubbleKind.Reasoning ? FontStyles.Italic : FontStyles.Normal;
            _ink = kind == BubbleKind.Reasoning ? Role.Muted : Role.BubbleInk;

            Root = UiKit.NewRect(name, parent);
            VerticalLayoutGroup col = Root.gameObject.AddComponent<VerticalLayoutGroup>();
            col.spacing = 3f;
            col.childControlWidth = true;
            col.childControlHeight = true;
            col.childForceExpandWidth = true;
            col.childForceExpandHeight = false;

            Role whoRole = kind == BubbleKind.Error ? Role.Bad : kind == BubbleKind.User ? Role.Accent : Role.Muted;
            Who = UiKit.Text(Root, "Who", string.Empty, 12, tb, whoRole, FontStyles.Bold,
                right ? TextAlignmentOptions.TopRight : TextAlignmentOptions.TopLeft, false);
            Who.characterSpacing = 2f;

            RectTransform row = UiKit.NewRect("Row", Root);
            BubbleRowLayout place = row.gameObject.AddComponent<BubbleRowLayout>();
            place.AlignRight = right;
            place.MaxFraction = 0.75f;

            Role fill = kind == BubbleKind.User ? Role.BubbleUser : kind == BubbleKind.Error ? Role.BubbleBad
                : kind == BubbleKind.Reasoning ? Role.Surface : Role.BubbleEva;
            Image bg = UiKit.Panel(row, "Bubble", tb, fill, kind == BubbleKind.Reasoning ? 0.6f : 1f);
            if (kind == BubbleKind.User || kind == BubbleKind.Agent)
            {
                bg.sprite = kind == BubbleKind.User ? UiKit.BubbleUser : UiKit.BubbleEva;
            }
            bg.pixelsPerUnitMultiplier = 1.5f;
            _bubble = bg.rectTransform;
            VerticalLayoutGroup stack = bg.gameObject.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(14, 14, 10, 10);
            stack.spacing = 8f;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
        }

        /// <summary>Show <paramref name="text"/>: parsed as Markdown for an agent bubble, verbatim otherwise.</summary>
        public void Set(string text, MarkdownStyle style)
        {
            if (!_markdown)
            {
                Seg only = Ensure(0, false);
                Assign(only.Text, only, text);
                Trim(1);
                return;
            }

            MarkdownTmp.Parse(text, style, _blocks);
            if (_blocks.Count == 0)
            {
                Assign(Ensure(0, false).Text, _segs[0], string.Empty);
                Trim(1);
                return;
            }
            for (int i = 0; i < _blocks.Count; i++)
            {
                MdBlock b = _blocks[i];
                bool code = b.Kind == MdBlockKind.Code;
                Seg seg = Ensure(i, code);
                if (code)
                {
                    bool hasLang = b.Lang.Length > 0;
                    if (seg.Lang.gameObject.activeSelf != hasLang) seg.Lang.gameObject.SetActive(hasLang);
                    if (hasLang && seg.Lang.text != b.Lang) seg.Lang.text = b.Lang;
                }
                Assign(seg.Text, seg, b.Text);
            }
            Trim(_blocks.Count);
        }

        private static void Assign(TextMeshProUGUI target, Seg seg, string value)
        {
            if (seg.Cached == value) return;
            seg.Cached = value;
            target.text = value;
        }

        /// <summary>The segment at <paramref name="index"/>, of the wanted kind. A kind change replaces it and everything after it, since order is the layout.</summary>
        private Seg Ensure(int index, bool code)
        {
            if (index < _segs.Count && _segs[index].Code == code) return _segs[index];
            Trim(index);
            Seg s = code ? NewCode() : NewText();
            _segs.Add(s);
            return s;
        }

        private void Trim(int keep)
        {
            for (int i = _segs.Count - 1; i >= keep; i--)
            {
                Object.Destroy(_segs[i].Root.gameObject);
                _segs.RemoveAt(i);
            }
        }

        private Seg NewText()
        {
            TextMeshProUGUI t = UiKit.Text(_bubble, "Text", string.Empty, BodySize, _tb, _ink, _style,
                TextAlignmentOptions.TopLeft, true, _markdown);
            return new Seg { Root = t.rectTransform, Text = t, Code = false, Cached = string.Empty };
        }

        private Seg NewCode()
        {
            Image panel = UiKit.Panel(_bubble, "Code", _tb, Role.Code, 1f);
            panel.pixelsPerUnitMultiplier = 3f;
            VerticalLayoutGroup v = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(12, 12, 9, 9);
            v.spacing = 4f;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;

            TextMeshProUGUI lang = UiKit.Text(panel.transform, "Lang", string.Empty, 12, _tb, Role.Muted, FontStyles.Bold,
                TextAlignmentOptions.TopLeft, false);
            lang.characterSpacing = 2f;
            lang.gameObject.SetActive(false);
            // Wraps rather than clips: a clipped line is lost output, and a wrapped one keeps its indentation.
            TextMeshProUGUI body = UiKit.Text(panel.transform, "Body", string.Empty, CodeSize, _tb, Role.BubbleInk,
                FontStyles.Normal, TextAlignmentOptions.TopLeft, true, true);
            return new Seg { Root = panel.rectTransform, Text = body, Lang = lang, Code = true, Cached = string.Empty };
        }
    }
}
