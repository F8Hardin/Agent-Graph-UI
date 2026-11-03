using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// What each live tool call was given, as a bare list floating directly above the prompt box: no island, frame,
    /// header or scroll view, not movable. It is parented to the prompt bar itself, so it matches the bar's width and
    /// sits on its top edge however the bar grows (the prompt grows to seven lines) or moves; nothing here knows the
    /// bar's height. The rejected alternative was a screen-space rect recomputed from the bar's size each frame, which
    /// is a second copy of a number the bar already owns.
    ///
    /// The list grows upward with the newest card at the bottom. Opacity follows stack position, not age: the newest
    /// card is fully opaque and each one above it is a constant factor fainter, so the stack dissolves into the
    /// graph as it rises. A card is held for the whole life of its call plus a readable dwell after it resolves, shows
    /// the outcome (ok / error, duration) before it goes, and then fades out in place. Nothing in it takes a raycast, so
    /// the graph underneath keeps its clicks and orbit. Reads ConsoleState only.
    /// </summary>
    internal sealed class InputFeedPanel
    {
        private const float FadeIn = 0.18f;
        private const float FadeOut = 0.5f;

        /// <summary>How long a resolved card stays, on top of its call's own duration. Long enough to read a small JSON input.</summary>
        private const float Dwell = 3.2f;

        /// <summary>Gap between the prompt bar's top edge and the bottom card: clears the queue status line that sits there.</summary>
        private const float GapAboveBar = 30f;

        private const float CardSpacing = 6f;

        /// <summary>
        /// Opacity of the card at stack position n (0 = newest, at the bottom) is StackFalloff^n: 1, 0.70, 0.49, 0.34,
        /// 0.24, 0.17. A constant ratio reads as an even fade (equal perceived steps) where a linear ramp would hit
        /// zero abruptly and leave the top cards looking the same as the middle ones.
        /// </summary>
        private const float StackFalloff = 0.70f;

        /// <summary>Rate (1/s) at which a card's stack opacity eases to its target, so older cards fade smoothly as a new one pushes them up.</summary>
        private const float StackEase = 9f;

        /// <summary>Only this many newest cards show their input; older ones collapse to a header line.</summary>
        private const int Expanded = 2;

        /// <summary>
        /// The most cards on screen at once. This is the climb cap: with two expanded cards the stack tops out near 400
        /// canvas units, short of the islands' bottom edge on a default layout. Beyond it the oldest resolved card goes.
        /// </summary>
        /// <summary>Horizontal inset from each side of the prompt bar, so a tall stack clears the islands beside it.</summary>
        private const float SideInset = 110f;

        private const int MaxCards = 6;

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
            public float Stack; // eased stack-position opacity, 0..1
            public ToolStatus Last = ToolStatus.Running;
            public bool Collapsed;
            public bool Unknown; // ended without a tool_end: the phase went Idle
        }

        private readonly ThemeBinder _tb;
        private readonly RectTransform _host;
        private readonly List<Card> _cards = new List<Card>(); // newest first = stack position 0 at the bottom
        private ConsoleReducer _r;
        private bool _dirty;

        /// <param name="promptBar">The prompt box's rect. The list is its child, anchored to its top edge and full width.</param>
        public InputFeedPanel(RectTransform promptBar, ThemeBinder tb)
        {
            _tb = tb;
            // This used to be an Island; its saved frame would otherwise sit in PlayerPrefs for good.
            ConsoleSettings.ForgetLayout("toolinput");

            _host = UiKit.NewRect("Tool Input Feed", promptBar);
            // Narrower than the bar and centred on it. Full bar width ran the stack under the
            // Chat island's lower corner; the cards draw on top of the islands, so a tall stack
            // was painting over a panel the user is reading. Inset keeps it clear at the default
            // layout without pinning the feed to any particular island position.
            _host.anchorMin = new Vector2(0f, 1f);
            _host.anchorMax = new Vector2(1f, 1f);
            _host.pivot = new Vector2(0.5f, 0f); // bottom edge fixed to the bar, so height grows upward
            _host.anchoredPosition = new Vector2(0f, GapAboveBar);
            _host.sizeDelta = new Vector2(-SideInset * 2f, 0f);
            VerticalLayoutGroup vlg = _host.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = CardSpacing;
            vlg.childAlignment = TextAnchor.LowerCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            _host.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
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

            float ease = 1f - Mathf.Exp(-StackEase * dt);
            bool changed = false;
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                Card c = _cards[i];
                c.Age += dt;
                float life = Mathf.Clamp01(c.Age / FadeIn);
                if (c.ResolvedAge >= 0f)
                {
                    c.ResolvedAge += dt;
                    float fade = (c.ResolvedAge - Dwell) / FadeOut;
                    if (fade > 0f) life *= 1f - Mathf.Clamp01(fade);
                    if (c.ResolvedAge >= Dwell + FadeOut)
                    {
                        UnityEngine.Object.Destroy(c.Root.gameObject);
                        _cards.RemoveAt(i);
                        changed = true;
                        continue;
                    }
                }
                c.Stack += (Mathf.Pow(StackFalloff, i) - c.Stack) * ease;
                c.Group.alpha = life * c.Stack;
            }
            if (changed) Restack();
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

            // New calls get a card at the bottom. Resolved ones that finished before we saw them are shown too: a call that
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
                _cards[i].Root.SetSiblingIndex(_cards.Count - 1 - i); // layout runs top to bottom: newest last
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
            bg.raycastTarget = false; // the graph behind the stack keeps its clicks and orbit
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

            return new Card { RunId = c.RunId, Root = root, Group = group, Status = status, BodyText = bodyText.gameObject, Bar = bar, Stack = 1f };
        }
    }
}
