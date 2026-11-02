using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The transcript. Reads ConsoleState.Transcript and nothing else; it never sees the wire. The reducer
    /// raises TranscriptChanged once per delta, so this only marks itself dirty and repaints once a frame.
    /// </summary>
    internal sealed class TranscriptPanel
    {
        private sealed class Row
        {
            public RectTransform Root;
            public TextMeshProUGUI Body;
            public MessageRole Role;
            public bool Err;
            public int Version = -1;
            public bool Open;
        }

        private readonly ThemeBinder _tb;
        private readonly CollapsiblePanel _panel;
        private readonly ScrollRect _scroll;
        private readonly RectTransform _content;
        private readonly TextMeshProUGUI _empty;
        private readonly List<Row> _rows = new List<Row>();
        private ConsoleReducer _r;
        private bool _dirty = true;
        private bool _force;

        public TranscriptPanel(Transform parent, ThemeBinder tb, float topInset, float height)
        {
            _tb = tb;
            _panel = new CollapsiblePanel(parent, tb, "Transcript", true, 430f, height, topInset);

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
            ContentSizeFitter fit = _content.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll.viewport = viewport;
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 28f;

            _empty = UiKit.Text(_panel.Body, "Empty", "The transcript appears here as the run streams in.", 15, tb, Role.Muted,
                FontStyles.Italic, TextAlignmentOptions.TopLeft, true);
            UiKit.Stretch(_empty.rectTransform, 16f, 12f, 16f, 8f);
            _empty.raycastTarget = false;

            tb.Applied += () => _force = true;
        }

        public void Bind(ConsoleReducer r)
        {
            _r = r;
            r.TranscriptChanged += () => _dirty = true;
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

            while (_rows.Count > list.Count)
            {
                Object.Destroy(_rows[_rows.Count - 1].Root.gameObject);
                _rows.RemoveAt(_rows.Count - 1);
            }

            int chars = 0;
            for (int i = 0; i < list.Count; i++)
            {
                ChatMessage m = list[i];
                chars += m.Length;
                if (i < _rows.Count && (_rows[i].Role != m.Role || _rows[i].Err != m.IsError))
                {
                    Object.Destroy(_rows[i].Root.gameObject);
                    _rows[i] = MakeRow(m, agent, i);
                    force = true;
                }
                else if (i >= _rows.Count)
                {
                    _rows.Add(MakeRow(m, agent, i));
                    force = true;
                }
                Row row = _rows[i];
                if (force || row.Version != m.Version || row.Open != m.IsOpen)
                {
                    row.Version = m.Version;
                    row.Open = m.IsOpen;
                    row.Body.text = m.IsOpen ? m.Text + " |" : m.Text;
                }
            }

            _empty.gameObject.SetActive(list.Count == 0);
            _panel.Note.text = "(" + (chars / 5) + " tokens est.)";

            Canvas.ForceUpdateCanvases();
            if (atBottom) _scroll.verticalNormalizedPosition = 0f;
        }

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

            return new Row { Root = root, Body = body, Role = m.Role, Err = m.IsError };
        }
    }
}
