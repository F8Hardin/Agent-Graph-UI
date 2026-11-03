using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The Tool input island: what each live call was given. Calls stack, newest on top, instead of replacing one
    /// another; the newest few show their input and older ones collapse to a header line. A card is held for the
    /// whole life of its call plus a readable dwell after it resolves, and shows the outcome (ok / error, duration)
    /// before it goes, so even a call that returns in 20 ms is legible. The island itself shows only while it has
    /// cards. Reads ConsoleState only.
    /// </summary>
    internal sealed class InputFeedPanel
    {
        private const float FadeIn = 0.18f;
        private const float FadeOut = 0.5f;

        /// <summary>How long a resolved card stays, on top of its call's own duration. Long enough to read a small JSON input.</summary>
        private const float Dwell = 3.2f;

        private const int Expanded = 3;
        private const int MaxCards = 10;

        private sealed class Card
        {
            public string RunId;
            public RectTransform Root;
            public CanvasGroup Group;
            public TextMeshProUGUI Status;
            public GameObject BodyText;
            public Image Bar;
            public float Age;
            public float ResolvedAge = -1f;
            public ToolStatus Last = ToolStatus.Running;
            public bool Collapsed;
            public bool Unknown; // ended without a tool_end: the phase went Idle
        }

        private readonly ThemeBinder _tb;
        private readonly Island _panel;
        private readonly RectTransform _host;
        private readonly List<Card> _cards = new List<Card>(); // newest first
        private ConsoleReducer _r;
        private bool _dirty;

        public Island Island => _panel;

        public InputFeedPanel(Transform parent, ThemeBinder tb, Func<Vector2, Rect> defaultRect)
        {
            _tb = tb;
            _panel = new Island(parent, tb, "toolinput", "Tool input", new Vector2(300f, 120f), defaultRect);
            // The stack sits in a scroll view whose content is sized to the cards. A bare layout group in a fixed-height
            // body squeezes its children toward their minimum heights when they do not fit, so the cards overlap.
            RectTransform sr = UiKit.NewRect("Scroll", _panel.Body);
            UiKit.Stretch(sr);
            ScrollRect scroll = sr.gameObject.AddComponent<ScrollRect>();
            RectTransform viewport = UiKit.NewRect("Viewport", sr);
            UiKit.Stretch(viewport, 8f, 6f, 8f, 4f);
            Image catcher = viewport.gameObject.AddComponent<Image>();
            catcher.color = new Color(0f, 0f, 0f, 0f);
            viewport.gameObject.AddComponent<RectMask2D>();
            _host = UiKit.NewRect("Cards", viewport);
            _host.anchorMin = new Vector2(0f, 1f);
            _host.anchorMax = new Vector2(1f, 1f);
            _host.pivot = new Vector2(0.5f, 1f);
            _host.anchoredPosition = Vector2.zero;
            _host.sizeDelta = Vector2.zero;
            VerticalLayoutGroup vlg = _host.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 8f;
            vlg.padding = new RectOffset(0, 0, 0, 6);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            _host.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport;
            scroll.content = _host;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;
            _panel.Root.gameObject.SetActive(false);
        }

        public void Bind(ConsoleReducer r)
        {
            _r = r;
            r.ToolsChanged += () => _dirty = true;
            // A lost tool_end must not pin a card forever: the run going Idle releases every card still waiting.
            r.PhaseChanged += p =>
            {
                if (p != Phase.Idle) return;
                for (int i = 0; i < _cards.Count; i++)
                {
                    if (_cards[i].ResolvedAge >= 0f) continue;
                    _cards[i].ResolvedAge = 0f;
                    _cards[i].Unknown = true;
                    SetStatus(_cards[i], null);
                }
            };
        }

        public void Tick(float dt)
        {
            if (_r == null) return;
            if (_dirty)
            {
                _dirty = false;
                Sync();
            }

            bool changed = false;
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                Card c = _cards[i];
                c.Age += dt;
                float a = Mathf.Clamp01(c.Age / FadeIn);
                if (c.ResolvedAge >= 0f)
                {
                    c.ResolvedAge += dt;
                    float fade = (c.ResolvedAge - Dwell) / FadeOut;
                    if (fade > 0f) a *= 1f - Mathf.Clamp01(fade);
                    if (c.ResolvedAge >= Dwell + FadeOut)
                    {
                        UnityEngine.Object.Destroy(c.Root.gameObject);
                        _cards.RemoveAt(i);
                        changed = true;
                        continue;
                    }
                }
                c.Group.alpha = a;
            }
            if (changed) Restack();

            bool any = _cards.Count > 0;
            if (_panel.Root.gameObject.activeSelf != any) _panel.Root.gameObject.SetActive(any);
            if (any) _panel.Tick(dt);
        }

        private void Sync()
        {
            IReadOnlyList<ToolCall> calls = _r.State.ToolCalls;
            if (calls.Count == 0) _shown.Clear();

            // Cards whose call vanished (a new turn cleared the rail) go at once; resolved calls start their dwell.
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                Card c = _cards[i];
                ToolCall call = Find(calls, c.RunId);
                if (call == null)
                {
                    UnityEngine.Object.Destroy(c.Root.gameObject);
                    _cards.RemoveAt(i);
                    continue;
                }
                if (call.Status != ToolStatus.Running && c.ResolvedAge < 0f)
                {
                    c.ResolvedAge = 0f;
                    c.Last = call.Status;
                    SetStatus(c, call);
                }
            }

            // New calls get a card on top. Resolved ones that finished before we saw them are shown too: a call that
            // starts and ends inside one frame must still appear, with its outcome.
            for (int i = 0; i < calls.Count; i++)
            {
                ToolCall call = calls[i];
                if (_shown.Contains(call.RunId)) continue;
                Card card = MakeCard(call);
                _cards.Insert(0, card);
                if (call.Status != ToolStatus.Running)
                {
                    card.ResolvedAge = 0f;
                    card.Last = call.Status;
                    SetStatus(card, call);
                }
            }

            // Trim: resolved cards go first, oldest first, when the stack gets long.
            while (_cards.Count > MaxCards)
            {
                int victim = -1;
                for (int i = _cards.Count - 1; i >= 0; i--)
                {
                    if (_cards[i].ResolvedAge >= 0f) { victim = i; break; }
                }
                if (victim < 0) victim = _cards.Count - 1;
                UnityEngine.Object.Destroy(_cards[victim].Root.gameObject);
                _cards.RemoveAt(victim);
            }
            Restack();
        }

        // A card is created at most once per run id: a call that resolves and ages out must not be resurrected by the
        // next Sync, which re-reads the whole ToolCalls list. Cleared when the rail empties (a new turn).
        private readonly HashSet<string> _shown = new HashSet<string>();

        private void Restack()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                _cards[i].Root.SetSiblingIndex(i); // newest first = top of the stack
                bool collapse = i >= Expanded;
                if (_cards[i].Collapsed != collapse)
                {
                    _cards[i].Collapsed = collapse;
                    _cards[i].BodyText.SetActive(!collapse);
                }
            }
        }

        private static ToolCall Find(IReadOnlyList<ToolCall> calls, string runId)
        {
            for (int i = calls.Count - 1; i >= 0; i--)
            {
                if (calls[i].RunId == runId) return calls[i];
            }
            return null;
        }

        private void SetStatus(Card c, ToolCall call)
        {
            ThemeTokens t = _tb.Tokens;
            if (call == null)
            {
                c.Status.text = "ended";
                if (t != null) c.Status.color = t.Muted;
                return;
            }
            bool err = call.Status == ToolStatus.Error;
            c.Status.text = (err ? "error  " : "ok  ") + call.DurationMs + "ms";
            if (t != null)
            {
                c.Status.color = err ? t.Bad : t.Accent;
                c.Bar.color = err ? t.Bad : t.Muted;
            }
        }

        private Card MakeCard(ToolCall c)
        {
            _shown.Add(c.RunId);
            Image bg = UiKit.Panel(_host, "Input " + c.RunId, _tb, Role.Surface2, 0.88f);
            RectTransform root = bg.rectTransform;
            CanvasGroup group = root.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            VerticalLayoutGroup vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(18, 14, 8, 10);
            vlg.spacing = 4f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            UiKit.Outline(root, _tb, c.IsMemory ? Role.Mem : Role.Line, 0.9f).gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            Image bar = UiKit.Panel(root, "Bar", _tb, c.IsMemory ? Role.Mem : Role.Accent, 1f, false);
            bar.raycastTarget = false;
            bar.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            RectTransform brt = bar.rectTransform;
            brt.anchorMin = new Vector2(0f, 0f);
            brt.anchorMax = new Vector2(0f, 1f);
            brt.pivot = new Vector2(0f, 0.5f);
            brt.anchoredPosition = new Vector2(7f, 0f);
            brt.sizeDelta = new Vector2(3f, -16f);

            // Head line: the tool on the left, the outcome on the right.
            RectTransform head = UiKit.NewRect("Head", root);
            head.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            TextMeshProUGUI name = UiKit.Text(head, "Name", c.Server + "__" + c.Tool, 14, _tb, c.IsMemory ? Role.Mem : Role.Accent, FontStyles.Bold, TextAlignmentOptions.MidlineLeft, false);
            name.overflowMode = TextOverflowModes.Ellipsis;
            UiKit.Stretch(name.rectTransform, 0f, 0f, 96f, 0f);
            TextMeshProUGUI status = UiKit.Text(head, "Status", "running", 13, _tb, Role.Muted, FontStyles.Bold, TextAlignmentOptions.MidlineRight, false);
            status.rectTransform.anchorMin = new Vector2(1f, 0f);
            status.rectTransform.anchorMax = new Vector2(1f, 1f);
            status.rectTransform.pivot = new Vector2(1f, 0.5f);
            status.rectTransform.sizeDelta = new Vector2(96f, 0f);
            status.rectTransform.anchoredPosition = Vector2.zero;

            string body = string.IsNullOrEmpty(c.Input) ? "{}" : c.Input;
            if (body.Length > 360) body = body.Substring(0, 360) + " ...";
            TextMeshProUGUI bodyText = UiKit.Text(root, "Body", body, 15, _tb, Role.Fg, FontStyles.Normal, TextAlignmentOptions.TopLeft, true);

            return new Card { RunId = c.RunId, Root = root, Group = group, Status = status, BodyText = bodyText.gameObject, Bar = bar };
        }
    }
}
