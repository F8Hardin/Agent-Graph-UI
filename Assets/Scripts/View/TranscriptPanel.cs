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
    /// </summary>
    internal sealed class TranscriptPanel
    {
        private sealed class Row
        {
            public RectTransform Root;
            public TextMeshProUGUI Who;
            public TextMeshProUGUI Body;
            public MessageRole Role;
            public bool Err;
            public ChatMessage Msg;
            public int Version = -1;
            public bool Open;
            public string Model;
        }

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
                    row.Msg = m;
                    row.Version = m.Version;
                    row.Open = m.IsOpen;
                    row.Model = rowModel;
                    row.Body.text = m.IsOpen ? m.Text + " |" : m.Text;
                    if (m.Role == MessageRole.Assistant && !m.IsError)
                    {
                        row.Who.text = rowModel == null ? agent : agent + "  ·  " + rowModel;
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

            Canvas.ForceUpdateCanvases();
            if (atBottom) _scroll.verticalNormalizedPosition = 0f;
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        private Row MakeRow(ChatMessage m, string agent, int index)
        {
            RectTransform root = UiKit.NewRect("Msg " + index, _content);
            root.SetSiblingIndex(index);
            VerticalLayoutGroup vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 2f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            string who = m.IsError ? "error" : m.Role == MessageRole.User ? "you" : m.Role == MessageRole.Reasoning ? "reasoning" : agent;
            Role whoRole = m.IsError ? Role.Bad : m.Role == MessageRole.User ? Role.Accent : Role.Muted;
            TextMeshProUGUI w = UiKit.Text(root, "Who", who, 12, _tb, whoRole, FontStyles.Bold, TextAlignmentOptions.TopLeft, false);
            w.characterSpacing = 2f;

            Role bodyRole = m.IsError ? Role.Bad : m.Role == MessageRole.Reasoning ? Role.Muted : Role.Fg;
            TextMeshProUGUI body = UiKit.Text(root, "Body", string.Empty, 16, _tb, bodyRole,
                m.Role == MessageRole.Reasoning ? FontStyles.Italic : FontStyles.Normal, TextAlignmentOptions.TopLeft, true);

            return new Row { Root = root, Who = w, Body = body, Role = m.Role, Err = m.IsError };
        }
    }
}
