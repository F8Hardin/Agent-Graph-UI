using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The Chats island: the list from GET /chats with title, last-run time and message count. Select one to load
    /// it into the Chat island; New chat starts an empty one; the cross deletes. A reader of <see cref="ChatSession"/>:
    /// it holds no list of its own beyond the rows it draws, and it never sees a transport.
    /// </summary>
    internal sealed class ChatsPanel
    {
        private const int MaxRows = 100;

        private readonly ThemeBinder _tb;
        private readonly Island _panel;
        private readonly RectTransform _content;
        private readonly ScrollRect _scroll;
        private readonly TextMeshProUGUI _status;
        private readonly TextMeshProUGUI _error;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private ChatSession _session;
        private bool _dirty = true;
        private bool _force;

        public Island Island => _panel;

        public ChatsPanel(Transform parent, ThemeBinder tb, Func<Vector2, Rect> defaultRect)
        {
            _tb = tb;
            _panel = new Island(parent, tb, "chats", "Chats", new Vector2(220f, 160f), defaultRect);

            TextMeshProUGUI newLabel;
            Button newBtn = UiKit.TextButton(_panel.Body, "New Chat", "+ New chat", 15, tb, Role.Accent, 0.28f, Role.Fg, out newLabel);
            RectTransform nrt = (RectTransform)newBtn.transform;
            nrt.anchorMin = new Vector2(0f, 1f);
            nrt.anchorMax = new Vector2(1f, 1f);
            nrt.pivot = new Vector2(0.5f, 1f);
            nrt.offsetMin = new Vector2(10f, -42f);
            nrt.offsetMax = new Vector2(-10f, -6f);
            newBtn.onClick.AddListener(() => _session?.NewChat());

            RectTransform sr = UiKit.NewRect("Scroll", _panel.Body);
            UiKit.Stretch(sr, 0f, 48f, 0f, 26f);
            _scroll = sr.gameObject.AddComponent<ScrollRect>();
            RectTransform viewport = UiKit.NewRect("Viewport", sr);
            UiKit.Stretch(viewport, 6f, 0f, 6f, 0f);
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
            vlg.padding = new RectOffset(4, 4, 4, 8);
            vlg.spacing = 6f;
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

            _status = UiKit.Text(_panel.Body, "Status", string.Empty, 14, tb, Role.Muted, FontStyles.Italic, TextAlignmentOptions.TopLeft, true);
            UiKit.Stretch(_status.rectTransform, 16f, 56f, 16f, 30f);

            _error = UiKit.Text(_panel.Body, "Error", string.Empty, 12, tb, Role.Bad, FontStyles.Normal, TextAlignmentOptions.BottomLeft, true);
            _error.rectTransform.anchorMin = new Vector2(0f, 0f);
            _error.rectTransform.anchorMax = new Vector2(1f, 0f);
            _error.rectTransform.pivot = new Vector2(0.5f, 0f);
            _error.rectTransform.offsetMin = new Vector2(12f, 2f);
            _error.rectTransform.offsetMax = new Vector2(-12f, 26f);
            _error.overflowMode = TextOverflowModes.Ellipsis;

            tb.Applied += () => { _force = true; };
        }

        public void Bind(ChatSession session)
        {
            _session = session;
            session.Changed += () => _dirty = true;
            _dirty = true;
        }

        public void Tick(float dt)
        {
            _panel.Tick(dt);
            if (_session == null || (!_dirty && !_force)) return;
            _dirty = false;
            _force = false;
            Rebuild();
        }

        private void Rebuild()
        {
            for (int i = 0; i < _rows.Count; i++) UnityEngine.Object.Destroy(_rows[i]);
            _rows.Clear();

            IReadOnlyList<ChatSummary> chats = _session.Chats;
            _error.text = _session.Error ?? string.Empty;

            switch (_session.ListPhase)
            {
                case ChatListPhase.Unavailable:
                    _status.text = "This agent keeps no chat history.";
                    break;
                case ChatListPhase.Failed:
                    _status.text = chats.Count == 0 ? "Could not load the chat list." : string.Empty;
                    break;
                case ChatListPhase.Loading:
                    _status.text = chats.Count == 0 ? "Loading chats..." : string.Empty;
                    break;
                default:
                    _status.text = chats.Count == 0 ? "No chats yet. Type below to start one." : string.Empty;
                    break;
            }
            _panel.Note.text = string.Empty;
            _panel.Right.text = _session.ListPhase == ChatListPhase.Loading ? "..." : chats.Count > 0 ? chats.Count.ToString(CultureInfo.InvariantCulture) : string.Empty;

            int n = Math.Min(chats.Count, MaxRows);
            for (int i = 0; i < n; i++) _rows.Add(MakeRow(chats[i]));
        }

        private GameObject MakeRow(ChatSummary c)
        {
            bool selected = c.Id == _session.SelectedId;
            Image card = UiKit.Panel(_content, "Chat " + c.Id, _tb, selected ? Role.AccentDim : Role.Surface2, selected ? 0.9f : 0.75f);
            RectTransform root = card.rectTransform;
            LayoutElement le = card.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 54f;
            le.preferredHeight = 54f;
            Button b = card.gameObject.AddComponent<Button>();
            b.targetGraphic = card;
            ColorBlock cb = ColorBlock.defaultColorBlock;
            cb.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.selectedColor = Color.white;
            b.colors = cb;
            string id = c.Id;
            b.onClick.AddListener(() => _session.Select(id));
            if (selected) UiKit.Outline(root, _tb, Role.Accent, 0.9f);

            TextMeshProUGUI title = UiKit.Text(root, "Title", c.Title, 15, _tb, Role.Fg, FontStyles.Bold, TextAlignmentOptions.TopLeft, false);
            title.overflowMode = TextOverflowModes.Ellipsis;
            title.rectTransform.anchorMin = new Vector2(0f, 1f);
            title.rectTransform.anchorMax = new Vector2(1f, 1f);
            title.rectTransform.pivot = new Vector2(0f, 1f);
            title.rectTransform.offsetMin = new Vector2(12f, -28f);
            title.rectTransform.offsetMax = new Vector2(-34f, -6f);

            string sub = c.MessageCount + (c.MessageCount == 1 ? " message" : " messages");
            string when = Ago(c.LastRun);
            if (when != null) sub += "  ·  " + when;
            TextMeshProUGUI s = UiKit.Text(root, "Sub", sub, 12, _tb, Role.Muted, FontStyles.Normal, TextAlignmentOptions.TopLeft, false);
            s.rectTransform.anchorMin = new Vector2(0f, 0f);
            s.rectTransform.anchorMax = new Vector2(1f, 0f);
            s.rectTransform.pivot = new Vector2(0f, 0f);
            s.rectTransform.offsetMin = new Vector2(12f, 6f);
            s.rectTransform.offsetMax = new Vector2(-12f, 24f);

            TextMeshProUGUI xl;
            Button del = UiKit.TextButton(root, "Delete", "x", 14, _tb, Role.Bad, 0.0f, Role.Muted, out xl);
            RectTransform drt = (RectTransform)del.transform;
            UiKit.SetAnchors(drt, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-4f, -4f), new Vector2(26f, 26f));
            del.onClick.AddListener(() => _session.Delete(id));
            return card.gameObject;
        }

        /// <summary>"5m ago", "3h ago", "2d ago", else the date. Null when the server gave no time.</summary>
        public static string Ago(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset t)) return null;
            TimeSpan d = DateTimeOffset.UtcNow - t;
            if (d.TotalSeconds < 0) return "just now";
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + "h ago";
            if (d.TotalDays < 7) return (int)d.TotalDays + "d ago";
            return t.LocalDateTime.ToString("MMM d", CultureInfo.InvariantCulture);
        }
    }
}
