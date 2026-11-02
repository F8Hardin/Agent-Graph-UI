using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// Screen-space UI (world-space comes with XR). Floating translucent panels over the graph, matching the
    /// prototype: transcript left, tool calls right, the current tool's input bottom centre, a prompt box,
    /// and a settings cog bottom right. Every panel reads ConsoleState through the reducer's change events;
    /// none of them parses wire data.
    ///
    /// The hierarchy is built in code at Awake: the sprites are procedural, so there is nothing to lose on a
    /// scene save and nothing for a designer to break by moving a reference.
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public sealed class ConsoleUI : MonoBehaviour
    {
        /// <summary>True while the prompt box has focus, so a typed 'L' does not toggle the camera lock.</summary>
        public static bool IsTyping { get; private set; }

        [SerializeField] private TMP_FontAsset font;
        [SerializeField] private ThemeController theme;
        [SerializeField] private float panelTopInset = 76f;
        [SerializeField] private float panelHeight = 760f;

        public event Action<string> PromptSubmitted;
        public event Action StopRequested;

        /// <summary>Sent when the prompt box is empty. Set by the host; null means an empty prompt sends nothing.</summary>
        public string DefaultPrompt { get; set; }

        private readonly ThemeBinder _tb = new ThemeBinder();
        private ConsoleReducer _r;
        private CameraRig _rig;

        private TranscriptPanel _transcript;
        private ToolRailPanel _tools;
        private InputFeedPanel _feed;

        private Image _phaseDot;
        private TextMeshProUGUI _phaseText;
        private ModelPanel _model;
        private TextMeshProUGUI _lockText;
        private Image _lockTint;
        private TMP_InputField _input;
        private TextMeshProUGUI _runText;
        private RectTransform _popover;
        private readonly System.Collections.Generic.List<(string key, Image ring)> _swatches =
            new System.Collections.Generic.List<(string, Image)>();

        private void Awake()
        {
            if (theme == null) theme = FindAnyObjectByType<ThemeController>();
            EnsureEventSystem();
            SetupCanvas();
            UiKit.Font = font != null ? font : ConsoleFonts.Regular;
            Build();
            _tb.Applied += RefreshThemeDependents;
            if (theme != null) _tb.Apply(theme.Tokens);
        }

        private void OnEnable()
        {
            if (theme != null) theme.Changed += _tb.Apply;
        }

        private void OnDisable()
        {
            if (theme != null) theme.Changed -= _tb.Apply;
        }

        public void Bind(ConsoleReducer r, CameraRig rig, ModelController models = null)
        {
            _r = r;
            _rig = rig;
            _transcript.Bind(r);
            _tools.Bind(r);
            _feed.Bind(r);
            r.PhaseChanged += _ => RefreshPhase();
            _model.Bind(r, models);
            if (rig != null)
            {
                rig.LockChanged += SetLockVisual;
                SetLockVisual(rig.Locked);
            }
            RefreshPhase();
        }

        // ------------------------------------------------------------------ build

        private static void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null) return;
            GameObject go = new GameObject("EventSystem", typeof(EventSystem));
            InputSystemUIInputModule module = go.AddComponent<InputSystemUIInputModule>();
            module.AssignDefaultActions();
        }

        private void SetupCanvas()
        {
            Canvas canvas = GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();
        }

        private void Build()
        {
            Transform root = transform;
            BuildTopBar(root);

            _transcript = new TranscriptPanel(root, _tb, panelTopInset, panelHeight);
            _tools = new ToolRailPanel(root, _tb, panelTopInset, panelHeight);
            _feed = new InputFeedPanel(root, _tb, 90f, 720f);

            BuildPromptBar(root);
            BuildSettings(root);

            // Last sibling = drawn on top of the transcript and tool rail it overlaps.
            _model.Dropdown.SetAsLastSibling();
        }

        private void BuildTopBar(Transform root)
        {
            RectTransform bar = UiKit.NewRect("Top Bar", root);
            bar.anchorMin = new Vector2(0f, 1f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(0.5f, 1f);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = new Vector2(0f, 56f);

            Image bg = UiKit.Panel(bar, "Bg", _tb, Role.Surface, 0.55f, false);
            UiKit.Stretch(bg.rectTransform);
            Image hair = UiKit.Panel(bar, "Hairline", _tb, Role.Line, 0.9f, false);
            hair.raycastTarget = false;
            hair.rectTransform.anchorMin = new Vector2(0f, 0f);
            hair.rectTransform.anchorMax = new Vector2(1f, 0f);
            hair.rectTransform.pivot = new Vector2(0.5f, 0f);
            hair.rectTransform.sizeDelta = new Vector2(0f, 1f);

            Image dot = UiKit.Panel(bar, "Brand Dot", _tb, Role.Accent, 1f);
            dot.sprite = UiKit.Circle;
            dot.type = Image.Type.Simple;
            dot.raycastTarget = false;
            UiKit.SetAnchors(dot.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(30f, 0f), new Vector2(14f, 14f));

            TextMeshProUGUI brand = UiKit.Text(bar, "Brand", "Agent Graph Console", 21, _tb, Role.Fg, FontStyles.Bold, TextAlignmentOptions.MidlineLeft, false);
            UiKit.SetAnchors(brand.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(48f, 0f), new Vector2(300f, 56f));

            Image pdot = UiKit.Panel(bar, "Phase Dot", _tb, Role.Muted, 1f);
            pdot.sprite = UiKit.Circle;
            pdot.type = Image.Type.Simple;
            pdot.raycastTarget = false;
            _tb.Bind(pdot, Role.Muted); // repainted by RefreshPhase on every theme change
            _phaseDot = pdot;
            UiKit.SetAnchors(pdot.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(380f, 0f), new Vector2(11f, 11f));
            _phaseText = UiKit.Text(bar, "Phase", "idle", 15, _tb, Role.Fg, FontStyles.Bold, TextAlignmentOptions.MidlineLeft, false);
            UiKit.SetAnchors(_phaseText.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(396f, 0f), new Vector2(160f, 56f));

            // Right cluster, laid out from the right edge inward.
            TextMeshProUGUI fitLabel;
            Button fit = UiKit.TextButton(bar, "Fit", "Fit", 15, _tb, Role.Surface2, 0.9f, Role.Fg, out fitLabel);
            UiKit.SetAnchors((RectTransform)fit.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-24f, 0f), new Vector2(64f, 34f));
            fit.onClick.AddListener(() => { if (_rig != null) _rig.Fit(); });

            Button lockBtn = UiKit.TextButton(bar, "Lock", "Lock: on", 15, _tb, Role.Surface2, 0.9f, Role.Fg, out _lockText);
            RectTransform lrt = (RectTransform)lockBtn.transform;
            UiKit.SetAnchors(lrt, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-96f, 0f), new Vector2(150f, 34f));
            _lockTint = UiKit.Panel(lrt, "Lock Tint", _tb, Role.Accent, 0.28f);
            _lockTint.raycastTarget = false;
            UiKit.Stretch(_lockTint.rectTransform);
            _lockTint.rectTransform.SetSiblingIndex(0);
            lockBtn.onClick.AddListener(() => { if (_rig != null) _rig.ToggleLock(); });

            // Model, temperature, context and the picker. Where the plain meta text used to be.
            _model = new ModelPanel(transform, bar, _tb);
        }

        private void BuildPromptBar(Transform root)
        {
            Image bg = UiKit.Panel(root, "Prompt Bar", _tb, Role.Surface, 0.88f);
            RectTransform bar = bg.rectTransform;
            UiKit.SetAnchors(bar, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 22f), new Vector2(720f, 56f));
            UiKit.Outline(bar, _tb, Role.Line, 0.9f);

            // TMP_InputField reads its references in OnEnable, so build it inactive and wire it before it wakes.
            RectTransform fieldRt = UiKit.NewRect("Prompt Field", bar);
            fieldRt.gameObject.SetActive(false);
            fieldRt.anchorMin = Vector2.zero;
            fieldRt.anchorMax = Vector2.one;
            fieldRt.offsetMin = new Vector2(8f, 8f);
            fieldRt.offsetMax = new Vector2(-122f, -8f);
            Image fieldBg = fieldRt.gameObject.AddComponent<Image>();
            fieldBg.sprite = UiKit.Rounded;
            fieldBg.type = Image.Type.Sliced;
            fieldBg.pixelsPerUnitMultiplier = 3f;
            _tb.Bind(fieldBg, Role.Bg, 0.55f);

            RectTransform area = UiKit.NewRect("Text Area", fieldRt);
            UiKit.Stretch(area, 12f, 4f, 12f, 4f);
            area.gameObject.AddComponent<RectMask2D>();
            TextMeshProUGUI placeholder = UiKit.Text(area, "Placeholder", "Message the agent, then press Enter...", 17, _tb, Role.Muted,
                FontStyles.Italic, TextAlignmentOptions.MidlineLeft, false);
            UiKit.Stretch(placeholder.rectTransform);
            TextMeshProUGUI text = UiKit.Text(area, "Text", string.Empty, 17, _tb, Role.Fg, FontStyles.Normal,
                TextAlignmentOptions.MidlineLeft, false);
            UiKit.Stretch(text.rectTransform);

            _input = fieldRt.gameObject.AddComponent<TMP_InputField>();
            _input.textViewport = area;
            _input.textComponent = text;
            _input.placeholder = placeholder;
            if (UiKit.Font != null) _input.fontAsset = UiKit.Font;
            _input.lineType = TMP_InputField.LineType.SingleLine;
            _input.caretWidth = 2;
            _input.customCaretColor = false;
            _input.onSubmit.AddListener(OnEnter);
            fieldRt.gameObject.SetActive(true);

            Button run = UiKit.TextButton(bar, "Run", "Run", 17, _tb, Role.Accent, 0.28f, Role.Fg, out _runText);
            UiKit.SetAnchors((RectTransform)run.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(104f, 40f));
            run.onClick.AddListener(OnRunClicked);
        }

        private void BuildSettings(Transform root)
        {
            Image cogBg = UiKit.Panel(root, "Cog", _tb, Role.Surface2, 0.92f);
            cogBg.sprite = UiKit.Circle;
            cogBg.type = Image.Type.Simple;
            RectTransform cog = cogBg.rectTransform;
            UiKit.SetAnchors(cog, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-24f, 24f), new Vector2(54f, 54f));
            Button cogBtn = cogBg.gameObject.AddComponent<Button>();
            cogBtn.targetGraphic = cogBg;
            Image gear = UiKit.Panel(cog, "Gear", _tb, Role.Fg, 1f);
            gear.sprite = UiKit.Gear;
            gear.type = Image.Type.Simple;
            gear.raycastTarget = false;
            UiKit.Stretch(gear.rectTransform, 11f, 11f, 11f, 11f);

            Image pop = UiKit.Panel(root, "Settings Popover", _tb, Role.Surface, 0.96f);
            _popover = pop.rectTransform;
            UiKit.SetAnchors(_popover, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-24f, 90f), new Vector2(330f, 336f));
            UiKit.Outline(_popover, _tb, Role.Line, 0.9f);
            _popover.gameObject.SetActive(false);
            cogBtn.onClick.AddListener(() => _popover.gameObject.SetActive(!_popover.gameObject.activeSelf));

            float y = -16f;
            TextMeshProUGUI h1 = UiKit.Text(_popover, "H1", "Terminal / black", 14, _tb, Role.Muted, FontStyles.Bold, TextAlignmentOptions.TopLeft, false);
            UiKit.SetAnchors(h1.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(18f, y), new Vector2(280f, 20f));
            BuildSwatchRow(_popover, y - 28f, 0);

            float y2 = y - 28f - 56f - 12f;
            TextMeshProUGUI h2 = UiKit.Text(_popover, "H2", "Terminal / white", 14, _tb, Role.Muted, FontStyles.Bold, TextAlignmentOptions.TopLeft, false);
            UiKit.SetAnchors(h2.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(18f, y2), new Vector2(280f, 20f));
            BuildSwatchRow(_popover, y2 - 28f, 4);

            TextMeshProUGUI note = UiKit.Text(_popover, "Note", "Surfaces, hairlines, muted text, the node tiers and the fog are mixed from background and font.",
                12, _tb, Role.Muted, FontStyles.Italic, TextAlignmentOptions.TopLeft, true);
            UiKit.SetAnchors(note.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(18f, y2 - 28f - 56f - 14f), new Vector2(294f, 52f));
        }

        private void BuildSwatchRow(RectTransform parent, float y, int first)
        {
            for (int i = 0; i < 4; i++)
            {
                ThemePreset p = ThemePresets.All[first + i];
                Image sw = UiKit.Panel(parent, "Swatch " + p.Key, null, Role.Bg, 1f);
                sw.color = p.Bg;
                UiKit.SetAnchors(sw.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(18f + i * 74f, y), new Vector2(66f, 56f));
                Button b = sw.gameObject.AddComponent<Button>();
                b.targetGraphic = sw;
                string key = p.Key;
                b.onClick.AddListener(() => { if (theme != null) theme.Apply(key); });

                TextMeshProUGUI a = UiKit.Text(sw.transform, "A", "A", 22, null, Role.Fg, FontStyles.Bold, TextAlignmentOptions.Center, false);
                a.color = p.Fg;
                UiKit.SetAnchors(a.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -4f), new Vector2(66f, 28f));
                TextMeshProUGUI n = UiKit.Text(sw.transform, "Name", p.Label, 12, null, Role.Fg, FontStyles.Normal, TextAlignmentOptions.Center, false);
                n.color = p.Fg;
                UiKit.SetAnchors(n.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(66f, 18f));

                Image ring = UiKit.Panel(sw.transform, "Selected", _tb, Role.Fg, 1f);
                ring.sprite = UiKit.Ring;
                ring.raycastTarget = false;
                UiKit.Stretch(ring.rectTransform);
                _swatches.Add((key, ring));
            }
        }

        // ------------------------------------------------------------------ behaviour

        private void OnEnter(string _)
        {
            // Enter sends; it never stops a live run (that is the button's job, and a stray Enter must not kill one).
            if (_r != null && _r.State.Phase == Phase.Idle) Send();
        }

        private void OnRunClicked()
        {
            if (_r != null && _r.State.Phase != Phase.Idle) StopRequested?.Invoke();
            else Send();
        }

        private void Send()
        {
            string s = _input.text == null ? string.Empty : _input.text.Trim();
            if (s.Length == 0) s = DefaultPrompt;
            if (string.IsNullOrEmpty(s)) return;
            _input.text = string.Empty;
            PromptSubmitted?.Invoke(s);
        }

        private void RefreshPhase()
        {
            if (_r == null) return;
            Phase p = _r.State.Phase;
            string name = p == Phase.ToolCall ? "tool" : p.ToString().ToLowerInvariant();
            _phaseText.text = name;
            ThemeTokens t = _tb.Tokens;
            if (t != null)
            {
                Color c = p == Phase.Idle ? t.Muted : p == Phase.Memory ? t.Mem : p == Phase.ToolCall ? t.Flow : p == Phase.Talking ? t.Fg : t.Accent;
                _phaseDot.color = c;
            }
            _runText.text = p == Phase.Idle ? "Run" : "Stop";
        }

        private void SetLockVisual(bool locked)
        {
            _lockText.text = locked ? "Lock: on  [L]" : "Lock: off [L]";
            _lockTint.gameObject.SetActive(locked);
        }

        private void RefreshThemeDependents()
        {
            RefreshPhase();
            if (theme != null)
            {
                ThemePreset p = ThemePresets.Find(theme.PresetKey);
                for (int i = 0; i < _swatches.Count; i++)
                {
                    _swatches[i].ring.gameObject.SetActive(_swatches[i].key == p.Key);
                }
            }
        }

        private void Update()
        {
            IsTyping = _input != null && _input.isFocused;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_transcript == null) return;
            _transcript.Tick(dt);
            _tools.Tick(dt);
            _feed.Tick(dt);
        }
    }
}
