using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The Settings island. The web client's settings, with the same names and meanings so the two do not drift:
    /// showThinking, showRaw, showTimings, showModel and the theme. Plus what is specific here: the transport and the
    /// EVA base URL, which until now lived only in config.json. A reader and writer of <see cref="ConsoleSettings"/>
    /// and the theme controller; connection edits are handed to the host as an event, because the View cannot know how
    /// the host stores them (and must never see a transport).
    /// </summary>
    internal sealed class SettingsPanel
    {
        private readonly ThemeBinder _tb;
        private readonly ThemeController _theme;
        private readonly Island _panel;
        private readonly RectTransform _content;
        private readonly List<(string key, Image ring)> _swatches = new List<(string, Image)>();
        private readonly List<(Func<bool> get, Image box)> _toggles = new List<(Func<bool>, Image)>();
        private readonly List<(string name, Image bg)> _transportButtons = new List<(string, Image)>();
        private readonly List<(float value, Image bg)> _scaleButtons = new List<(float, Image)>();
        private readonly List<(NodeStyleKind kind, Image bg)> _styleButtons = new List<(NodeStyleKind, Image)>();
        private McpSettingsSection _mcp;
        private TMP_InputField _url;
        private TextMeshProUGUI _saved;
        private string _transport = "EvaSse";

        public Island Island => _panel;

        /// <summary>The user saved the connection: transport name and base URL. The host persists it; it applies on the next launch.</summary>
        public event Action<string, string> ConnectionSaved;

        public SettingsPanel(Transform parent, ThemeBinder tb, ThemeController theme, Func<Vector2, Rect> defaultRect)
        {
            _tb = tb;
            _theme = theme;
            _panel = new Island(parent, tb, "settings", "Settings", new Vector2(300f, 220f), defaultRect);
            _panel.Root.gameObject.SetActive(false); // opened by the cog

            RectTransform sr = UiKit.NewRect("Scroll", _panel.Body);
            UiKit.Stretch(sr);
            ScrollRect scroll = sr.gameObject.AddComponent<ScrollRect>();
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
            vlg.padding = new RectOffset(10, 10, 6, 14);
            vlg.spacing = 8f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            _content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport;
            scroll.content = _content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;

            Header("Connection");
            BuildConnection();
            _mcp = new McpSettingsSection(_content, tb); // the web app's MCP toggles; its own file, this one is long enough
            Header("Display");
            AddToggle("Show thinking", () => ConsoleSettings.ShowThinking, v => ConsoleSettings.SetView(thinking: v));
            AddToggle("Show raw events", () => ConsoleSettings.ShowRaw, v => ConsoleSettings.SetView(raw: v));
            AddToggle("Show timings", () => ConsoleSettings.ShowTimings, v => ConsoleSettings.SetView(timings: v));
            AddToggle("Show model", () => ConsoleSettings.ShowModel, v => ConsoleSettings.SetView(model: v));
            AddToggle("Memory tree", () => ConsoleSettings.ShowMemoryTree, v => ConsoleSettings.SetView(memoryTree: v));
            Header("UI size");
            BuildScaleRow();
            Header("Node style");
            BuildStyleList();
            Header("Theme: terminal / black");
            BuildSwatchRow(0);
            Header("Theme: terminal / white");
            BuildSwatchRow(4);
            TextMeshProUGUI note = UiKit.Text(_content, "Note",
                "Surfaces, hairlines, muted text, the node tiers and the fog are mixed from background and font.",
                12, tb, Role.Muted, FontStyles.Italic, TextAlignmentOptions.TopLeft, true);
            note.gameObject.AddComponent<LayoutElement>().minHeight = 40f;

            ConsoleSettings.ViewChanged += RefreshToggles;
            ConsoleSettings.ViewChanged += RefreshScale;
            ConsoleSettings.ViewChanged += RefreshStyle;
            RefreshToggles();
            RefreshScale();
            RefreshStyle();
        }

        public void Tick(float dt) => _panel.Tick(dt);

        public void SetVisible(bool on)
        {
            bool opening = on && !_panel.Root.gameObject.activeSelf;
            _panel.Root.gameObject.SetActive(on);
            if (opening) _mcp.OnOpened(); // the list should be current when it is looked at
        }

        /// <summary>Give the MCP section its controller. Null leaves it saying the list is not available.</summary>
        public void BindInventory(InventoryController inventory) => _mcp.Bind(inventory);
        public bool Visible => _panel.Root.gameObject.activeSelf;

        /// <summary>Show the connection the host is using. Called once at start.</summary>
        public void SetConnection(string transport, string url)
        {
            _transport = string.Equals(transport, "Replay", StringComparison.OrdinalIgnoreCase) ? "Replay" : "EvaSse";
            if (_url != null) _url.text = url ?? string.Empty;
            RefreshTransport();
        }

        /// <summary>Repaint what depends on the theme: the selected swatch ring and the toggles.</summary>
        public void RefreshTheme()
        {
            if (_theme != null)
            {
                ThemePreset p = ThemePresets.Find(_theme.PresetKey);
                for (int i = 0; i < _swatches.Count; i++) _swatches[i].ring.gameObject.SetActive(_swatches[i].key == p.Key);
            }
            RefreshToggles();
            RefreshTransport();
            RefreshScale();
            RefreshStyle();
        }

        // ------------------------------------------------------------------ build

        private void Header(string text)
        {
            TextMeshProUGUI h = UiKit.Text(_content, "H " + text, text, 13, _tb, Role.Muted, FontStyles.Bold, TextAlignmentOptions.BottomLeft, false);
            h.gameObject.AddComponent<LayoutElement>().minHeight = 24f;
        }

        private void AddToggle(string label, Func<bool> get, Action<bool> set)
        {
            Image row = UiKit.Panel(_content, "Toggle " + label, _tb, Role.Surface2, 0.7f);
            row.gameObject.AddComponent<LayoutElement>().minHeight = 34f;
            Button b = row.gameObject.AddComponent<Button>();
            b.targetGraphic = row;
            ColorBlock cb = ColorBlock.defaultColorBlock;
            cb.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.selectedColor = Color.white;
            b.colors = cb;

            Image box = UiKit.Panel(row.transform, "Box", _tb, Role.Accent, 1f);
            UiKit.SetAnchors(box.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(18f, 18f));
            box.raycastTarget = false;
            TextMeshProUGUI t = UiKit.Text(row.transform, "Label", label, 15, _tb, Role.Fg, FontStyles.Normal, TextAlignmentOptions.MidlineLeft, false);
            UiKit.Stretch(t.rectTransform, 38f, 0f, 8f, 0f);
            _toggles.Add((get, box));
            b.onClick.AddListener(() => set(!get()));
        }

        private void RefreshToggles()
        {
            ThemeTokens t = _tb.Tokens;
            if (t == null) return;
            for (int i = 0; i < _toggles.Count; i++)
            {
                bool on = _toggles[i].get();
                Color c = on ? t.Accent : t.Line;
                c.a = on ? 1f : 0.6f;
                _toggles[i].box.color = c;
            }
        }

        private void BuildConnection()
        {
            RectTransform row = UiKit.NewRect("Transport Row", _content);
            row.gameObject.AddComponent<LayoutElement>().minHeight = 36f;
            HorizontalLayoutGroup h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8f;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = true;
            AddTransportButton(row, "Replay", "Replay (demo)");
            AddTransportButton(row, "EvaSse", "EVA");

            RectTransform fieldRt = UiKit.NewRect("URL Field", _content);
            fieldRt.gameObject.SetActive(false);
            fieldRt.gameObject.AddComponent<LayoutElement>().minHeight = 38f;
            Image bg = fieldRt.gameObject.AddComponent<Image>();
            bg.sprite = UiKit.Rounded;
            bg.type = Image.Type.Sliced;
            bg.pixelsPerUnitMultiplier = 3f;
            _tb.Bind(bg, Role.Bg, 0.55f);
            RectTransform area = UiKit.NewRect("Text Area", fieldRt);
            UiKit.Stretch(area, 10f, 4f, 10f, 4f);
            area.gameObject.AddComponent<RectMask2D>();
            TextMeshProUGUI ph = UiKit.Text(area, "Placeholder", "http://host:8000", 15, _tb, Role.Muted, FontStyles.Italic, TextAlignmentOptions.MidlineLeft, false);
            UiKit.Stretch(ph.rectTransform);
            TextMeshProUGUI text = UiKit.Text(area, "Text", string.Empty, 15, _tb, Role.Fg, FontStyles.Normal, TextAlignmentOptions.MidlineLeft, false);
            UiKit.Stretch(text.rectTransform);
            _url = fieldRt.gameObject.AddComponent<TMP_InputField>();
            _url.textViewport = area;
            _url.textComponent = text;
            _url.placeholder = ph;
            if (UiKit.Font != null) _url.fontAsset = UiKit.Font;
            _url.lineType = TMP_InputField.LineType.SingleLine;
            _url.caretWidth = 2;
            fieldRt.gameObject.SetActive(true);

            TextMeshProUGUI saveLabel;
            Button save = UiKit.TextButton(_content, "Save Connection", "Save connection", 15, _tb, Role.Accent, 0.28f, Role.Fg, out saveLabel);
            save.gameObject.AddComponent<LayoutElement>().minHeight = 36f;
            save.onClick.AddListener(() =>
            {
                ConnectionSaved?.Invoke(_transport, _url.text == null ? string.Empty : _url.text.Trim());
                _saved.text = "Saved. Takes effect on the next launch.";
            });
            _saved = UiKit.Text(_content, "Saved", string.Empty, 12, _tb, Role.Muted, FontStyles.Italic, TextAlignmentOptions.TopLeft, true);
            _saved.gameObject.AddComponent<LayoutElement>().minHeight = 18f;
        }

        private void AddTransportButton(Transform row, string name, string label)
        {
            TextMeshProUGUI l;
            Button b = UiKit.TextButton(row, "Transport " + name, label, 14, _tb, Role.Surface2, 0.8f, Role.Fg, out l);
            _transportButtons.Add((name, (Image)b.targetGraphic));
            b.onClick.AddListener(() =>
            {
                _transport = name;
                RefreshTransport();
            });
        }

        private void RefreshTransport()
        {
            ThemeTokens t = _tb.Tokens;
            if (t == null) return;
            for (int i = 0; i < _transportButtons.Count; i++)
            {
                bool on = _transportButtons[i].name == _transport;
                Color c = on ? t.Accent : t.Surface2;
                c.a = on ? 0.5f : 0.8f;
                _transportButtons[i].bg.color = c;
            }
        }

        private void BuildScaleRow()
        {
            RectTransform row = UiKit.NewRect("Scale Row", _content);
            row.gameObject.AddComponent<LayoutElement>().minHeight = 46f;
            HorizontalLayoutGroup h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 6f;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = true;
            AddScaleButton(row, "Small", 0.75f);
            AddScaleButton(row, "Normal", 1f);
            AddScaleButton(row, "Large", 1.25f);
            AddScaleButton(row, "Huge", 1.5f);
        }

        private void AddScaleButton(Transform row, string name, float value)
        {
            TextMeshProUGUI l;
            Button b = UiKit.TextButton(row, "Scale " + name, name + "\n" + value.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture), 13, _tb, Role.Surface2, 0.8f, Role.Fg, out l);
            _scaleButtons.Add((value, (Image)b.targetGraphic));
            b.onClick.AddListener(() => ConsoleSettings.SetView(uiScale: value));
        }

        private void RefreshScale()
        {
            ThemeTokens t = _tb.Tokens;
            if (t == null) return;
            for (int i = 0; i < _scaleButtons.Count; i++)
            {
                bool on = Mathf.Abs(_scaleButtons[i].value - ConsoleSettings.UiScale) < 0.001f;
                Color c = on ? t.Accent : t.Surface2;
                c.a = on ? 0.5f : 0.8f;
                _scaleButtons[i].bg.color = c;
            }
        }

        // A vertical list, not a row like the scale buttons: "Hologram Wire (soon)" does not fit a third of the panel.
        private void BuildStyleList()
        {
            for (int i = 0; i < NodeStyles.All.Length; i++)
            {
                NodeStyleKind kind = NodeStyles.All[i];
                TextMeshProUGUI l;
                Button b = UiKit.TextButton(_content, "Style " + NodeStyles.Key(kind), NodeStyles.PickerLabel(kind), 14, _tb, Role.Surface2, 0.8f, Role.Fg, out l);
                b.gameObject.AddComponent<LayoutElement>().minHeight = 34f;
                _styleButtons.Add((kind, (Image)b.targetGraphic));
                // Persist through ConsoleSettings; GraphView listens to ViewChanged and swaps the bodies live.
                b.onClick.AddListener(() => ConsoleSettings.SetView(nodeStyle: kind));
            }
        }

        private void RefreshStyle()
        {
            ThemeTokens t = _tb.Tokens;
            if (t == null) return;
            for (int i = 0; i < _styleButtons.Count; i++)
            {
                bool on = _styleButtons[i].kind == ConsoleSettings.NodeStyle;
                Color c = on ? t.Accent : t.Surface2;
                c.a = on ? 0.5f : 0.8f;
                _styleButtons[i].bg.color = c;
            }
        }

        private void BuildSwatchRow(int first)
        {
            RectTransform row = UiKit.NewRect("Swatches " + first, _content);
            row.gameObject.AddComponent<LayoutElement>().minHeight = 58f;
            HorizontalLayoutGroup h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 6f;
            h.childControlWidth = false;
            h.childControlHeight = false;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            for (int i = 0; i < 4; i++)
            {
                ThemePreset p = ThemePresets.All[first + i];
                Image sw = UiKit.Panel(row, "Swatch " + p.Key, null, Role.Bg, 1f);
                sw.color = p.Bg;
                sw.rectTransform.sizeDelta = new Vector2(62f, 56f);
                Button b = sw.gameObject.AddComponent<Button>();
                b.targetGraphic = sw;
                string key = p.Key;
                b.onClick.AddListener(() => { if (_theme != null) _theme.Apply(key); });

                TextMeshProUGUI a = UiKit.Text(sw.transform, "A", "A", 22, null, Role.Fg, FontStyles.Bold, TextAlignmentOptions.Center, false);
                a.color = p.Fg;
                UiKit.SetAnchors(a.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -4f), new Vector2(62f, 28f));
                TextMeshProUGUI n = UiKit.Text(sw.transform, "Name", p.Label, 11, null, Role.Fg, FontStyles.Normal, TextAlignmentOptions.Center, false);
                n.color = p.Fg;
                UiKit.SetAnchors(n.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(62f, 18f));

                Image ring = UiKit.Panel(sw.transform, "Selected", _tb, Role.Fg, 1f);
                ring.sprite = UiKit.Ring;
                ring.raycastTarget = false;
                UiKit.Stretch(ring.rectTransform);
                _swatches.Add((key, ring));
            }
        }
    }
}
