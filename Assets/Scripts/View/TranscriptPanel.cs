using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The Chat island: the transcript of the selected chat. Reads ConsoleState.Transcript and nothing else; it
    /// never sees the wire. The reducer raises TranscriptChanged once per delta, so this only marks itself dirty and
    /// repaints once a frame. Loading another chat replaces the message objects, so a row is matched to its message
    /// by identity, not by position and version alone.
    ///
    /// Each message is a <see cref="ChatBubble"/>. A token stream touches only the open message's bubble, and even
    /// that is repainted at most every <see cref="StreamInterval"/> seconds: the reducer already coalesces deltas to
    /// one change per frame, but re-parsing and re-laying-out a Markdown body at 60+ Hz buys nothing the eye can see.
    /// </summary>
    internal sealed class TranscriptPanel
    {
        private sealed class Row
        {
            public RectTransform Root;
            public ChatBubble Bubble;
            public MessageRole Role;
            public bool Err;
            public ChatMessage Msg;
            public int Version = -1;
            public bool Open;
            public string Model;
            public float PaintedAt;
        }

        private const float StreamInterval = 0.06f;

        private readonly ThemeBinder _tb;
        private readonly Island _panel;
        private readonly ScrollRect _scroll;
        private readonly RectTransform _content;
        private readonly TextMeshProUGUI _empty;
        private readonly List<Row> _rows = new List<Row>();
        private ConsoleReducer _r;
        private ChatSession _session;
        private bool _dirty = true;
        private bool _force;
        private MarkdownStyle _mdStyle = new MarkdownStyle();

        public Island Island => _panel;

        public TranscriptPanel(Transform parent, ThemeBinder tb, Func<Vector2, Rect> defaultRect)
        {
            _tb = tb;
            _panel = new Island(parent, tb, "chat", "Chat", new Vector2(300f, 200f), defaultRect);

            RectTransform sr = UiKit.NewRect("Scroll", _panel.Body);
            UiKit.Stretch(sr);
            _scroll = sr.gameObject.AddComponent<ScrollRect>();

            RectTransform viewport = UiKit.NewRect("Viewport", sr);
            UiKit.Stretch(viewport, 6f, 4f, 6f, 2f);
            Image catcher = viewport.gameObject.AddComponent<Image>();
            catcher.color = new Color(0f, 0f, 0f, 0f);
            viewport.gameObject.AddComponent<RectMask2D>();

            _content = UiKit.NewRect("Content", viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;
            VerticalLayoutGroup vlg = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 6, 10);
            vlg.spacing = 12f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            _content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll.viewport = viewport;
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 28f;

            _empty = UiKit.Text(_panel.Body, "Empty", "Pick a chat on the left, or type below to start one. The reply streams in here.", 15, tb, Role.Muted,
                FontStyles.Italic, TextAlignmentOptions.TopLeft, true);
            UiKit.Stretch(_empty.rectTransform, 16f, 12f, 16f, 8f);
            _empty.raycastTarget = false;

            tb.Applied += () => _force = true;
            ConsoleSettings.ViewChanged += () => _force = true;
        }

        public void Bind(ConsoleReducer r, ChatSession session)
        {
            _r = r;
            _session = session;
            r.TranscriptChanged += () => _dirty = true;
            r.MetaChanged += () => _dirty = true;
            if (session != null) session.Changed += () => _dirty = true;
            _dirty = true;
        }

        public void Tick(float dt)
        {
            _panel.Tick(dt);
            if (_r == null || (!_dirty && !_force)) return;
            _dirty = false;
            bool force = _force;
            _force = false;

            IReadOnlyList<ChatMessage> list = _r.State.Transcript;
            if (force) _mdStyle = BuildStyle(_tb.Tokens);
            bool changed = force;
            bool atBottom = _scroll.verticalNormalizedPosition <= 0.03f || _rows.Count == 0;
            string agent = _r.Graph.AgentLabel.ToLowerInvariant();
            string model = ConsoleSettings.ShowModel ? _r.State.Model : null;
            int lastAssistant = -1;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Role == MessageRole.Assistant && !list[i].IsError)
                {
                    lastAssistant = i;
                    break;
                }
            }

            while (_rows.Count > list.Count)
            {
                UnityEngine.Object.Destroy(_rows[_rows.Count - 1].Root.gameObject);
                _rows.RemoveAt(_rows.Count - 1);
            }

            int chars = 0;
            for (int i = 0; i < list.Count; i++)
            {
                ChatMessage m = list[i];
                chars += m.Length;
                if (i < _rows.Count && (_rows[i].Role != m.Role || _rows[i].Err != m.IsError))
                {
                    UnityEngine.Object.Destroy(_rows[i].Root.gameObject);
                    _rows[i] = MakeRow(m, agent, i);
                    force = true;
                }
                else if (i >= _rows.Count)
                {
                    _rows.Add(MakeRow(m, agent, i));
                    force = true;
                }
                Row row = _rows[i];
                string rowModel = i == lastAssistant ? model : null;
                if (force || row.Msg != m || row.Version != m.Version || row.Open != m.IsOpen || row.Model != rowModel)
                {
                    // Mid-stream repaints are rate limited; the row stays stale and the panel stays dirty, so the
                    // last tokens of a burst still land on the next pass that is allowed through.
                    bool throttled = !force && row.Msg == m && m.IsOpen && row.Open && Time.unscaledTime - row.PaintedAt < StreamInterval;
                    if (throttled)
                    {
                        _dirty = true;
                    }
                    else
                    {
                        row.Msg = m;
                        row.Version = m.Version;
                        row.Open = m.IsOpen;
                        row.Model = rowModel;
                        row.PaintedAt = Time.unscaledTime;
                        row.Bubble.Set(m.IsOpen ? m.Text + " |" : m.Text, _mdStyle);
                        if (m.Role == MessageRole.Assistant && !m.IsError)
                        {
                            row.Bubble.Who.text = rowModel == null ? agent : agent + "  ·  " + rowModel;
                        }
                        changed = true;
                    }
                }
                // The reasoning toggle hides the rows without losing them, so turning it back on needs no reload.
                bool show = m.Role != MessageRole.Reasoning || ConsoleSettings.ShowThinking;
                if (row.Root.gameObject.activeSelf != show) row.Root.gameObject.SetActive(show);
            }

            _empty.gameObject.SetActive(list.Count == 0);
            string title = _session != null ? _session.SelectedTitle : null;
            _panel.Note.text = title != null ? Truncate(title, 22) : "(" + (chars / 5) + " tokens est.)";
            _panel.Right.text = _r.State.RunInFlight ? "running" : string.Empty;

            if (!changed) return;
            Canvas.ForceUpdateCanvases();
            if (atBottom) _scroll.verticalNormalizedPosition = 0f;
        }

        /// <summary>
        /// Link, quote and muted colours are pushed only as far toward black or white as it takes to read on a bubble,
        /// the same rule the ink follows, so a light theme's pale accent does not vanish into its tinted fill.
        /// </summary>
        private static MarkdownStyle BuildStyle(ThemeTokens t)
        {
            Color accent = t.ReadableOn(t.Accent, t.BubbleUser, 4.5f);
            Color muted = t.ReadableOn(t.Muted, t.BubbleUser, 4.5f);
            Color mark = t.Accent;
            mark.a = 0.22f;
            return new MarkdownStyle
            {
                Accent = ThemeTokens.Hex(accent),
                Heading = ThemeTokens.Hex(t.BubbleInk),
                Muted = ThemeTokens.Hex(muted),
                CodeMark = "#" + ColorUtility.ToHtmlStringRGBA(mark)
            };
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        private Row MakeRow(ChatMessage m, string agent, int index)
        {
            BubbleKind kind = m.IsError ? BubbleKind.Error : m.Role == MessageRole.User ? BubbleKind.User
                : m.Role == MessageRole.Reasoning ? BubbleKind.Reasoning : BubbleKind.Agent;
            ChatBubble bubble = new ChatBubble(_content, "Msg " + index, _tb, kind);
            bubble.Root.SetSiblingIndex(index);
            bubble.Who.text = kind == BubbleKind.Error ? "error" : kind == BubbleKind.User ? "you" : kind == BubbleKind.Reasoning ? "reasoning" : agent;
            return new Row { Root = bubble.Root, Bubble = bubble, Role = m.Role, Err = m.IsError };
        }
    }
}
