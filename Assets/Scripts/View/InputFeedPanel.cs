using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The current tool's INPUT, pinned bottom centre (the prototype's feed). At most two cards are live;
    /// a card fades in on tool_start and fades out 0.44 s after its tool resolves. Reads ConsoleState only.
    /// </summary>
    internal sealed class InputFeedPanel
    {
        private const float FadeIn = 0.18f;
        private const float FadeOut = 0.44f;
        private const int MaxLive = 2;

        private sealed class Card
        {
            public string RunId;
            public RectTransform Root;
            public CanvasGroup Group;
            public float Age;
            public float RetireAge = -1f;
        }

        private readonly ThemeBinder _tb;
        private readonly RectTransform _host;
        private readonly List<Card> _cards = new List<Card>();
        private ConsoleReducer _r;
        private bool _dirty;

        public InputFeedPanel(Transform parent, ThemeBinder tb, float bottomInset, float width)
        {
            _tb = tb;
            _host = UiKit.NewRect("Input Feed", parent);
            UiKit.SetAnchors(_host, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, bottomInset), new Vector2(width, 10f));
            VerticalLayoutGroup vlg = _host.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 8f;
            vlg.childAlignment = TextAnchor.LowerCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            ContentSizeFitter fit = _host.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        public void Bind(ConsoleReducer r)
        {
            _r = r;
            r.ToolsChanged += () => _dirty = true;
        }

        public void Tick(float dt)
        {
            if (_r == null) return;
            if (_dirty)
            {
                _dirty = false;
                Sync();
            }

            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                Card c = _cards[i];
                c.Age += dt;
                float a = Mathf.Clamp01(c.Age / FadeIn);
                if (c.RetireAge >= 0f)
                {
                    c.RetireAge += dt;
                    a *= 1f - Mathf.Clamp01(c.RetireAge / FadeOut);
                    if (c.RetireAge >= FadeOut)
                    {
                        Object.Destroy(c.Root.gameObject);
                        _cards.RemoveAt(i);
                        continue;
                    }
                }
                c.Group.alpha = a;
            }
        }

        private void Sync()
        {
            IReadOnlyList<ToolCall> calls = _r.State.ToolCalls;

            // Retire cards whose call has resolved (or vanished in a Reset).
            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i].RetireAge >= 0f) continue;
                ToolCall call = Find(calls, _cards[i].RunId);
                if (call == null || call.Status != ToolStatus.Running) _cards[i].RetireAge = 0f;
            }

            // Show the newest running calls that have no card yet.
            for (int i = calls.Count - 1; i >= 0; i--)
            {
                ToolCall c = calls[i];
                if (c.Status != ToolStatus.Running || HasCard(c.RunId)) continue;
                if (LiveCount() >= MaxLive) RetireOldest();
                _cards.Add(MakeCard(c));
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

        private bool HasCard(string runId)
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i].RunId == runId) return true;
            }
            return false;
        }

        private int LiveCount()
        {
            int n = 0;
            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i].RetireAge < 0f) n++;
            }
            return n;
        }

        private void RetireOldest()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i].RetireAge < 0f)
                {
                    _cards[i].RetireAge = 0f;
                    return;
                }
            }
        }

        private Card MakeCard(ToolCall c)
        {
            Image bg = UiKit.Panel(_host, "Input " + c.RunId, _tb, Role.Surface2, 0.88f);
            RectTransform root = bg.rectTransform;
            CanvasGroup group = root.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            VerticalLayoutGroup vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(18, 14, 10, 12);
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
            brt.sizeDelta = new Vector2(3f, -18f);

            string head = "input ->  " + c.Server + "__" + c.Tool;
            UiKit.Text(root, "Head", head, 14, _tb, c.IsMemory ? Role.Mem : Role.Accent, FontStyles.Bold, TextAlignmentOptions.TopLeft, false);
            string body = string.IsNullOrEmpty(c.Input) ? "{}" : c.Input;
            if (body.Length > 360) body = body.Substring(0, 360) + " ...";
            UiKit.Text(root, "Body", body, 15, _tb, Role.Fg, FontStyles.Normal, TextAlignmentOptions.TopLeft, true);

            return new Card { RunId = c.RunId, Root = root, Group = group };
        }
    }
}
