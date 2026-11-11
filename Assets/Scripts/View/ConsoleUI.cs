using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
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
        private const float PanelTop = 76f;       // below the top bar
        private const float PanelBottomReserve = 170f; // room for the prompt box and its status line
        private const float PromptWidth = 720f;
        private const float PromptBottom = 22f;
        private const float PromptMinHeight = 56f;
        private const int PromptMaxLines = 7;

        /// <summary>The user saved a connection in Settings: transport and base URL. The host stores it; it applies next launch.</summary>
        public event Action<string, string> ConnectionSaved;

        /// <summary>Tell Settings which connection is live. Called by the host once at start.</summary>
        public void SetConnection(string transport, string url) => _settings?.SetConnection(transport, url);

        /// <summary>Sent when the prompt box is empty. Set by the host; null means an empty prompt sends nothing.</summary>
        public string DefaultPrompt { get; set; }

        private readonly ThemeBinder _tb = new ThemeBinder();
        private ConsoleReducer _r;
        private CameraRig _rig;

        private ChatSession _session;
        private ChatsPanel _chats;
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
        private Image _runBg;
        private Button _runBtn;
        private RectTransform _promptBar;
        private TextMeshProUGUI _text;
        private TextMeshProUGUI _queueText;
        private TextMeshProUGUI _willQueueText;
        private float _promptH = PromptMinHeight;
        private float _lineH;
        private bool _textDirty = true;
        private bool _enterPending;
        private bool _cBusy;
        private bool _cDraft;
        private int _cQueued = -1;
        private SettingsPanel _settings;
        private CanvasScaler _scaler;

        private void Awake()
        {
            if (theme == null) theme = FindAnyObjectByType<ThemeController>();
            EnsureEventSystem();
            ConsoleSettings.LoadView();
            SetupCanvas();
            UiKit.Font = font != null ? font : ConsoleFonts.Regular;
            Build();
            _tb.Applied += RefreshThemeDependents;
            if (theme != null) _tb.Apply(theme.Tokens);
        }

        private void OnEnable()
        {
            if (theme != null) theme.Changed += _tb.Apply;
            ConsoleSettings.ViewChanged += ApplyUiScale;
        }

        private void OnDisable()
        {
            if (theme != null) theme.Changed -= _tb.Apply;
            ConsoleSettings.ViewChanged -= ApplyUiScale;
        }

        public void Bind(ConsoleReducer r, CameraRig rig, ModelController models = null, ChatSession session = null, InventoryController inventory = null)
        {
            _r = r;
            _rig = rig;
            _session = session;
            if (session != null) _chats.Bind(session);
            _transcript.Bind(r, session);
            _tools.Bind(r);
            _feed.Bind(r);
            r.PhaseChanged += _ => RefreshPhase();
            _model.Bind(r, models);
            _settings.BindInventory(inventory);
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
            _scaler = scaler;
            ApplyUiScale();
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();
        }

        /// <summary>A larger reference resolution makes the UI smaller, so the scale divides it.</summary>
        private void ApplyUiScale()
        {
            if (_scaler != null) _scaler.referenceResolution = new Vector2(1920f, 1080f) / ConsoleSettings.UiScale;
        }

        private void Build()
        {
            Transform root = transform;
            BuildTopBar(root);

            // Three islands plus Settings, each in the same frame (Island). The rects are only the defaults: a moved island
            // keeps the user's rect, saved per kind.
            _chats = new ChatsPanel(root, _tb, cs => new Rect(16f, PanelTop, 270f, Mathf.Max(300f, cs.y - PanelTop - PanelBottomReserve)));
            _transcript = new TranscriptPanel(root, _tb, cs => new Rect(298f, PanelTop, 500f, Mathf.Max(300f, cs.y - PanelTop - PanelBottomReserve)));
            _tools = new ToolRailPanel(root, _tb, cs => new Rect(cs.x - 16f - 380f, PanelTop, 380f, Mathf.Max(300f, cs.y - PanelTop - PanelBottomReserve)));

            BuildPromptBar(root);
            // The tool input list hangs off the prompt bar so the two stay together when the bar grows.
            _feed = new InputFeedPanel(_promptBar, _tb);
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
            _promptBar = bg.rectTransform;
            UiKit.SetAnchors(_promptBar, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, PromptBottom), new Vector2(PromptWidth, PromptMinHeight));
            UiKit.Outline(_promptBar, _tb, Role.Line, 0.9f);

            // TMP_InputField reads its references in OnEnable, so build it inactive and wire it before it wakes.
            RectTransform fieldRt = UiKit.NewRect("Prompt Field", _promptBar);
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
            TextMeshProUGUI placeholder = UiKit.Text(area, "Placeholder", "Message the agent. Shift+Enter: new line", 17, _tb, Role.Muted,
                FontStyles.Italic, TextAlignmentOptions.TopLeft, true);
            UiKit.Stretch(placeholder.rectTransform);
            _text = UiKit.Text(area, "Text", string.Empty, 17, _tb, Role.Fg, FontStyles.Normal,
                TextAlignmentOptions.TopLeft, true);
            UiKit.Stretch(_text.rectTransform);

            _input = fieldRt.gameObject.AddComponent<TMP_InputField>();
            _input.textViewport = area;
            _input.textComponent = _text;
            _input.placeholder = placeholder;
            if (UiKit.Font != null) _input.fontAsset = UiKit.Font;
            // Multi-line so it wraps and grows. Enter is intercepted in ValidateChar rather than using MultiLineSubmit,
            // which would also drop focus on every send and leave no way to type the next, queued, message.
            _input.lineType = TMP_InputField.LineType.MultiLineNewline;
            _input.caretWidth = 2;
            _input.customCaretColor = false;
            _input.onValidateInput = ValidateChar;
            _input.onValueChanged.AddListener(_ => _textDirty = true);
            fieldRt.gameObject.SetActive(true);

            _runBtn = UiKit.TextButton(_promptBar, "Run", "Send", 17, _tb, Role.Accent, 0.28f, Role.Fg, out _runText);
            _runBg = (Image)_runBtn.targetGraphic;
            UiKit.SetAnchors((RectTransform)_runBtn.transform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-8f, 8f), new Vector2(104f, 40f));
            _runBtn.onClick.AddListener(OnRunClicked);

            // One status line above the box, as the web client has under its textarea.
            _queueText = UiKit.Text(root, "Queue Status", string.Empty, 13, _tb, Role.Mem, FontStyles.Bold, TextAlignmentOptions.BottomLeft, false);
            UiKit.SetAnchors(_queueText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, PromptBottom + PromptMinHeight + 4f), new Vector2(PromptWidth - 16f, 22f));
            _willQueueText = UiKit.Text(root, "Will Queue", string.Empty, 13, _tb, Role.Mem, FontStyles.Bold, TextAlignmentOptions.BottomRight, false);
            UiKit.SetAnchors(_willQueueText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, PromptBottom + PromptMinHeight + 4f), new Vector2(PromptWidth - 16f, 22f));
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

            // Settings is an island like the rest: movable, resizable, remembered. The cog opens and closes it.
            _settings = new SettingsPanel(root, _tb, theme, cs => new Rect(cs.x - 16f - 340f, Mathf.Max(PanelTop, cs.y - 96f - 700f), 340f, Mathf.Min(700f, cs.y - 96f - PanelTop)));
            _settings.ConnectionSaved += (t, u) => ConnectionSaved?.Invoke(t, u);
            cogBtn.onClick.AddListener(() => _settings.SetVisible(!_settings.Visible));
        }

        // ------------------------------------------------------------------ behaviour

        private char ValidateChar(string text, int pos, char c)
        {
            if (c != '\n' && c != '\r') return c;
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.shiftKey.isPressed) return '\n'; // Shift+Enter: a new line
            _enterPending = true; // Enter: send, or queue if a run is live. Done in Update, not inside the edit.
            return '\0';
        }

        private bool Busy => _session != null && _session.IsBusy;

        private void OnRunClicked()
        {
            // While a run is live the button is always Stop (it also clears the queue). Enter is what queues.
            if (Busy) _session.Stop();
            else Send();
        }

        private void Send()
        {
            string s = _input.text == null ? string.Empty : _input.text.Trim();
            if (s.Length == 0 && !Busy) s = DefaultPrompt;
            if (string.IsNullOrEmpty(s) || _session == null) return;
            _input.text = string.Empty;
            _session.Submit(s);
        }

        /// <summary>Grow upward with the text to a cap, then scroll inside the field. The islands above ride up with it.</summary>
        private void RelayoutPrompt()
        {
            if (_lineH <= 0f) _lineH = Mathf.Max(18f, _text.GetPreferredValues("A", 1000f, 0f).y);
            float w = Mathf.Max(120f, _text.rectTransform.rect.width);
            float ph = _text.GetPreferredValues((_input.text ?? string.Empty) + "\u200B", w, 0f).y;
            float max = _lineH * PromptMaxLines + 24f;
            float h = Mathf.Clamp(ph + 24f, PromptMinHeight, max);
            if (Mathf.Abs(h - _promptH) < 0.5f) return;
            _promptH = h;
            _promptBar.sizeDelta = new Vector2(PromptWidth, h);
            float y = PromptBottom + h + 4f;
            _queueText.rectTransform.anchoredPosition = new Vector2(0f, y);
            _willQueueText.rectTransform.anchoredPosition = new Vector2(0f, y);
        }

        /// <summary>The send button and the queue line, from the session. Cheap: it only touches text when something changed.</summary>
        private void RefreshComposer()
        {
            bool busy = Busy;
            bool draft = !string.IsNullOrWhiteSpace(_input.text);
            int queued = _session != null ? _session.QueuedCount : 0;
            if (busy == _cBusy && draft == _cDraft && queued == _cQueued && _tb.Tokens != null && !_composerForce) return;
            _composerForce = false;
            _cBusy = busy;
            _cDraft = draft;
            _cQueued = queued;

            _runText.text = busy ? "Stop" : "Send";
            _runBtn.interactable = busy || draft || !string.IsNullOrEmpty(DefaultPrompt);
            ThemeTokens t = _tb.Tokens;
            if (t != null)
            {
                Color c = busy ? t.Bad : t.Accent;
                c.a = busy ? 0.7f : 0.28f;
                _runBg.color = c;
            }
            _queueText.text = queued > 0
                ? queued + (queued == 1 ? " message queued" : " messages queued") + " - will send automatically (Stop clears)"
                : string.Empty;
            _willQueueText.text = busy && draft ? "Enter will queue" : string.Empty;
        }

        private bool _composerForce;

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
        }

        private void SetLockVisual(bool locked)
        {
            _lockText.text = locked ? "Lock: on  [L]" : "Lock: off [L]";
            _lockTint.gameObject.SetActive(locked);
        }

        private void RefreshThemeDependents()
        {
            _composerForce = true;
            RefreshPhase();
            _settings?.RefreshTheme();
        }

        private void Update()
        {
            IsTyping = _input != null && _input.isFocused;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_transcript == null) return;
            if (_enterPending)
            {
                _enterPending = false;
                Send();
            }
            if (_textDirty)
            {
                _textDirty = false;
                RelayoutPrompt();
            }
            RefreshComposer();
            _chats.Tick(dt);
            _settings.Tick(dt);
            _transcript.Tick(dt);
            _tools.Tick(dt);
            _feed.Tick(dt);
        }
    }
}
