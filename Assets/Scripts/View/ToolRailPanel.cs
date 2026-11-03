using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The tool rail: one card per ToolCall, resolved by RunId in the reducer. Reads ConsoleState.ToolCalls only.
    /// </summary>
    internal sealed class ToolRailPanel
    {
        private sealed class Row
        {
            public RectTransform Root;
            public Image Bar;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Foot;
            public ToolStatus Status = (ToolStatus)(-1);
            public long Ms = -1;
        }

        private readonly ThemeBinder _tb;
        private readonly Island _panel;
        private readonly ScrollRect _scroll;
        private readonly RectTransform _content;
        private readonly TextMeshProUGUI _empty;
        private readonly List<Row> _rows = new List<Row>();
        private ConsoleReducer _r;
        private bool _dirty = true;
        private bool _force;

        public ToolRailPanel(Transform parent, ThemeBinder tb, Func<Vector2, Rect> defaultRect)
        {
            _tb = tb;
            _panel = new Island(parent, tb, "tools", "Tool calls", new Vector2(280f, 160f), defaultRect);

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
            vlg.padding = new RectOffset(6, 6, 6, 10);
            vlg.spacing = 8f;
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

            _empty = UiKit.Text(_panel.Body, "Empty",
                "Each tool_start grafts its node onto the graph, pulses the path, shows its input under the graph and appends a card here. The matching tool_end resolves that card by run id.",
                14, tb, Role.Muted, FontStyles.Italic, TextAlignmentOptions.TopLeft, true);
            UiKit.Stretch(_empty.rectTransform, 16f, 12f, 16f, 8f);

            tb.Applied += () => _force = true;
            ConsoleSettings.ViewChanged += () => _force = true;
        }

        public void Bind(ConsoleReducer r)
        {
            _r = r;
            r.ToolsChanged += () => _dirty = true;
            _dirty = true;
        }

        public void Tick(float dt)
        {
            _panel.Tick(dt);
            if (_r == null) return;

            if (_dirty || _force) Rebuild();

            // A running card breathes; nothing else here animates.
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
            IReadOnlyList<ToolCall> calls = _r.State.ToolCalls;
            for (int i = 0; i < _rows.Count && i < calls.Count; i++)
            {
                if (calls[i].Status != ToolStatus.Running) continue;
                Color c = ThemeBinder.Pick(_tb.Tokens, calls[i].IsMemory ? Role.Mem : Role.Accent);
                c.a = 0.35f + 0.65f * pulse;
                _rows[i].Bar.color = c;
            }
        }

        private void Rebuild()
        {
            bool force = _force;
            _dirty = false;
            _force = false;

            IReadOnlyList<ToolCall> calls = _r.State.ToolCalls;
            bool atBottom = _scroll.verticalNormalizedPosition <= 0.03f || _rows.Count == 0;

            while (_rows.Count > calls.Count)
            {
                UnityEngine.Object.Destroy(_rows[_rows.Count - 1].Root.gameObject);
                _rows.RemoveAt(_rows.Count - 1);
            }

            ThemeTokens t = _tb.Tokens;
            string muted = ThemeTokens.Hex(t.Muted);
            string bad = ThemeTokens.Hex(t.Bad);

            for (int i = 0; i < calls.Count; i++)
            {
                ToolCall c = calls[i];
                if (i >= _rows.Count)
                {
                    _rows.Add(MakeRow(c, i));
                    force = true;
                }
                Row row = _rows[i];
                if (!force && row.Status == c.Status && row.Ms == c.DurationMs) continue;
                row.Status = c.Status;
                row.Ms = c.DurationMs;

                Color bar = c.Status == ToolStatus.Error ? t.Bad : c.IsMemory ? t.Mem : c.Status == ToolStatus.Done ? t.Muted : t.Accent;
                row.Bar.color = bar;

                string state = c.Status == ToolStatus.Running ? "running" : ConsoleSettings.ShowTimings ? c.DurationMs + "ms" : "done";
                string result = string.Empty;
                if (!string.IsNullOrEmpty(c.Output))
                {
                    string o = c.Output.Length > 90 ? c.Output.Substring(0, 90) + "..." : c.Output;
                    result = "  <color=" + (c.Status == ToolStatus.Error ? bad : muted) + ">" + Escape(o) + "</color>";
                }
                row.Foot.text = "<color=" + muted + ">" + Escape(c.Server) + "  ·  " + state + "</color>" + result;
            }

            _empty.gameObject.SetActive(calls.Count == 0);
            _panel.Note.text = calls.Count == 0 ? "(none yet)" : "(" + calls.Count + " calls)";
            _panel.Right.text = _r.State.RunningToolCount > 0 ? _r.State.RunningToolCount + " live" : string.Empty;

            Canvas.ForceUpdateCanvases();
            if (atBottom) _scroll.verticalNormalizedPosition = 0f;
        }

        /// <summary>Tool output is untrusted text going into a rich-text label: defuse any markup in it.</summary>
        private static string Escape(string s) => s.Replace("<", "<​");

        private Row MakeRow(ToolCall c, int index)
        {
            Image card = UiKit.Panel(_content, "Tool " + index, _tb, Role.Surface2, 0.9f);
            RectTransform root = card.rectTransform;
            VerticalLayoutGroup vlg = root.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(16, 10, 8, 9);
            vlg.spacing = 3f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            Image bar = UiKit.Panel(root, "Bar", _tb, Role.Accent, 1f, false);
            bar.raycastTarget = false;
            LayoutElement le = bar.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            RectTransform brt = bar.rectTransform;
            brt.anchorMin = new Vector2(0f, 0f);
            brt.anchorMax = new Vector2(0f, 1f);
            brt.pivot = new Vector2(0f, 0.5f);
            brt.anchoredPosition = new Vector2(6f, 0f);
            brt.sizeDelta = new Vector2(3f, -16f);

            TextMeshProUGUI name = UiKit.Text(root, "Name", c.Tool, 17, _tb, Role.Fg, FontStyles.Bold, TextAlignmentOptions.TopLeft, true);
            TextMeshProUGUI foot = UiKit.Text(root, "Foot", string.Empty, 13, _tb, Role.Muted, FontStyles.Normal, TextAlignmentOptions.TopLeft, true, true);
            return new Row { Root = root, Bar = bar, Name = name, Foot = foot };
        }
    }
}
