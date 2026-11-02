using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The model readout in the top bar and the list that drops from it: current model, temperature, context size
    /// and job from the meta event, plus a picker backed by <see cref="ModelController"/>. This is the first piece of
    /// the agent-detail island (ROADMAP Phase 3).
    ///
    /// It never sees a transport. The controller owns the requests and their pending / failed states; this only
    /// draws them. With no controller, or one that reports Unavailable (a replay has no models), the chip is a plain
    /// readout of whatever the meta event said, and there is no list to open: a missing capability is not an error.
    /// </summary>
    internal sealed class ModelPanel
    {
        private const float RowHeight = 34f;

        private readonly ThemeBinder _tb;
        private readonly Button _chip;
        private readonly TextMeshProUGUI _name;
        private readonly TextMeshProUGUI _detail;
        private readonly RectTransform _drop;
        private readonly TextMeshProUGUI _status;
        private readonly Button _refresh;
        private readonly RectTransform _content;
        private readonly ScrollRect _scroll;

        private ConsoleReducer _r;
        private ModelController _m;
        private bool _open;

        /// <summary>The dropdown, parented to the canvas root so it can be moved above the other panels.</summary>
        public RectTransform Dropdown => _drop;

        public ModelPanel(Transform root, RectTransform bar, ThemeBinder tb)
        {
            _tb = tb;

            // Chip: sits where the old meta text did, left of the Lock button.
            Image chipBg = UiKit.Panel(bar, "Model Chip", tb, Role.Surface2, 0.9f);
            UiKit.SetAnchors(chipBg.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-262f, 0f), new Vector2(380f, 42f));
            _chip = chipBg.gameObject.AddComponent<Button>();
            _chip.targetGraphic = chipBg;
            ColorBlock cb = ColorBlock.defaultColorBlock;
            cb.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.disabledColor = Color.white; // a readout is not "greyed out", it is just not clickable
            cb.fadeDuration = 0.08f;
            _chip.colors = cb;
            _chip.onClick.AddListener(Toggle);

            _name = UiKit.Text(chipBg.transform, "Name", string.Empty, 14, tb, Role.Fg, FontStyles.Bold, TextAlignmentOptions.TopLeft, false);
            _name.overflowMode = TextOverflowModes.Ellipsis;
            // Top 20 px of the 42 px chip. A rect shorter than one line makes TMP's ellipsis swallow the whole text.
            _name.rectTransform.anchorMin = new Vector2(0f, 1f);
            _name.rectTransform.anchorMax = new Vector2(1f, 1f);
            _name.rectTransform.offsetMin = new Vector2(12f, -24f);
            _name.rectTransform.offsetMax = new Vector2(-12f, -4f);
            _name.alignment = TextAlignmentOptions.MidlineLeft;

            _detail = UiKit.Text(chipBg.transform, "Detail", string.Empty, 11, tb, Role.Muted, FontStyles.Normal, TextAlignmentOptions.TopLeft, false);
            _detail.overflowMode = TextOverflowModes.Ellipsis;
            _detail.rectTransform.anchorMin = new Vector2(0f, 0f);
            _detail.rectTransform.anchorMax = new Vector2(1f, 0f);
            _detail.rectTransform.offsetMin = new Vector2(12f, 3f);
            _detail.rectTransform.offsetMax = new Vector2(-12f, 20f);
            _detail.alignment = TextAlignmentOptions.MidlineLeft;

            // Dropdown, below the bar and right-aligned with the chip.
            Image dropBg = UiKit.Panel(root, "Model Dropdown", tb, Role.Surface, 0.97f);
            _drop = dropBg.rectTransform;
            UiKit.SetAnchors(_drop, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-262f, -64f), new Vector2(440f, 470f));
            UiKit.Outline(_drop, tb, Role.Line, 0.9f);

            TextMeshProUGUI title = UiKit.Text(_drop, "Title", "Model", 16, tb, Role.Fg, FontStyles.Bold, TextAlignmentOptions.TopLeft, false);
            UiKit.SetAnchors(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -12f), new Vector2(200f, 24f));

            TextMeshProUGUI refreshLabel;
            _refresh = UiKit.TextButton(_drop, "Refresh", "Refresh", 12, tb, Role.Surface2, 0.9f, Role.Fg, out refreshLabel);
            UiKit.SetAnchors((RectTransform)_refresh.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -10f), new Vector2(84f, 26f));
            _refresh.onClick.AddListener(() => { if (_m != null) _m.Refresh(); });

            _status = UiKit.Text(_drop, "Status", string.Empty, 12, tb, Role.Muted, FontStyles.Normal, TextAlignmentOptions.TopLeft, true);
            _status.overflowMode = TextOverflowModes.Ellipsis;
            UiKit.SetAnchors(_status.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -42f), new Vector2(408f, 52f));

            RectTransform sr = UiKit.NewRect("Scroll", _drop);
            UiKit.Stretch(sr, 8f, 98f, 8f, 8f);
            _scroll = sr.gameObject.AddComponent<ScrollRect>();
            RectTransform viewport = UiKit.NewRect("Viewport", sr);
            UiKit.Stretch(viewport);
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
            vlg.spacing = 4f;
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

            _drop.gameObject.SetActive(false);
            tb.Applied += Refresh;
        }

        public void Bind(ConsoleReducer r, ModelController m)
        {
            _r = r;
            _m = m;
            r.MetaChanged += Refresh;
            if (m != null) m.Changed += Refresh;
            Refresh();
        }

        private bool Available => _m != null && _m.IsAvailable;

        private void Toggle()
        {
            if (!Available) return;
            _open = !_open;
            _drop.gameObject.SetActive(_open);
            // A list that never loaded is retried on open; one that did is left alone (there is a Refresh button).
            if (_open && _m.Catalog == null && _m.Phase != ModelControlPhase.Loading && _m.Phase != ModelControlPhase.Switching)
            {
                _m.Refresh();
            }
            Refresh();
        }

        // ------------------------------------------------------------------ drawing (events only, never per frame)

        private void Refresh()
        {
            if (_r == null) return;
            ModelControlPhase phase = Available ? _m.Phase : ModelControlPhase.Unavailable;
            ModelCatalog cat = Available ? _m.Catalog : null;
            ConsoleState s = _r.State;

            string id = !string.IsNullOrEmpty(cat?.CurrentModelId) ? cat.CurrentModelId : s.Model;
            string name = NameOf(cat, id);
            float? temp = s.Temperature ?? cat?.Temperature;
            int? ctx = s.ContextLength ?? cat?.ActiveContextLength;
            string facts = Facts(temp, ctx, s.JobId);

            Role detailRole = Role.Muted;
            string label;
            string detail;
            switch (phase)
            {
                case ModelControlPhase.Loading:
                    label = id != null ? name : "reading models...";
                    detail = id != null ? facts : "asking the agent which models it has";
                    break;
                case ModelControlPhase.Switching:
                    label = "-> " + NameOf(cat, _m.PendingModelId);
                    detail = "switching: loading can take a minute";
                    break;
                case ModelControlPhase.Failed:
                    label = id != null ? name : "models unavailable";
                    detail = "last request failed: open for details";
                    detailRole = Role.Bad;
                    break;
                case ModelControlPhase.Ready:
                    label = id != null ? name : "no model loaded";
                    detail = facts.Length > 0 ? facts : "click to choose a model";
                    break;
                default:
                    // No model capability: a readout only, and quietly so.
                    label = id != null ? name : "no model info";
                    detail = facts.Length > 0 ? facts : "model switching not offered by this agent";
                    break;
            }

            bool clickable = phase != ModelControlPhase.Unavailable;
            _chip.interactable = clickable;
            _name.text = clickable ? label + "  \u25BE" : label;
            _detail.text = detail;
            if (_tb.Tokens != null) _detail.color = ThemeBinder.Pick(_tb.Tokens, detailRole);

            if (!clickable && _open)
            {
                _open = false;
                _drop.gameObject.SetActive(false);
            }
            if (_open) DrawDropdown(phase, cat);
        }

        private void DrawDropdown(ModelControlPhase phase, ModelCatalog cat)
        {
            Role statusRole = Role.Muted;
            string status;
            switch (phase)
            {
                case ModelControlPhase.Loading:
                    status = "Reading the model list. If the inference server has to wake up first, this can take a while.";
                    break;
                case ModelControlPhase.Switching:
                    status = "Switching to " + NameOf(cat, _m.PendingModelId) + ". The old model is unloaded before the new one loads, " +
                             "so a large model takes about a minute. Prompts sent meanwhile wait for it.";
                    break;
                case ModelControlPhase.Failed:
                    status = _m.Error;
                    statusRole = Role.Bad;
                    break;
                default:
                    status = cat == null ? string.Empty
                        : cat.Models.Count + " models. Choosing one unloads the current model first"
                          + (ContextOf(cat) != null ? " and loads it at " + ContextOf(cat) + " context." : ".");
                    break;
            }
            _status.text = status;
            _status.color = ThemeBinder.Pick(_tb.Tokens, statusRole);
            _refresh.interactable = phase != ModelControlPhase.Loading && phase != ModelControlPhase.Switching;

            for (int i = _content.childCount - 1; i >= 0; i--) Object.Destroy(_content.GetChild(i).gameObject);
            if (cat == null) return;

            bool pickable = phase == ModelControlPhase.Ready || phase == ModelControlPhase.Failed;
            for (int i = 0; i < cat.Models.Count; i++)
            {
                MakeRow(cat.Models[i], cat.CurrentModelId, pickable);
            }
        }

        private void MakeRow(ModelInfo model, string currentId, bool pickable)
        {
            bool current = model.Id == currentId;
            bool pending = _m.Phase == ModelControlPhase.Switching && model.Id == _m.PendingModelId;

            Image bg = UiKit.Panel(_content, "Model " + model.Id, _tb, current || pending ? Role.Accent : Role.Surface2, current || pending ? 0.28f : 0.55f);
            LayoutElement le = bg.gameObject.AddComponent<LayoutElement>();
            le.minHeight = RowHeight;
            le.preferredHeight = RowHeight;
            Button b = bg.gameObject.AddComponent<Button>();
            b.targetGraphic = bg;
            ColorBlock cb = ColorBlock.defaultColorBlock;
            cb.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.disabledColor = Color.white;
            cb.fadeDuration = 0.08f;
            b.colors = cb;
            b.interactable = pickable && !current;
            string id = model.Id;
            b.onClick.AddListener(() => { if (_m != null) _m.Select(id); });

            // A filled dot marks a model that is resident in the inference server right now.
            Image dot = UiKit.Panel(bg.transform, "Loaded", _tb, model.IsLoaded ? Role.Accent : Role.Line, model.IsLoaded ? 1f : 0.6f);
            dot.sprite = UiKit.Circle;
            dot.type = Image.Type.Simple;
            dot.raycastTarget = false;
            UiKit.SetAnchors(dot.rectTransform, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(18f, 0f), new Vector2(9f, 9f));

            TextMeshProUGUI n = UiKit.Text(bg.transform, "Name", model.DisplayName, 14, _tb, Role.Fg,
                current ? FontStyles.Bold : FontStyles.Normal, TextAlignmentOptions.MidlineLeft, false);
            n.overflowMode = TextOverflowModes.Ellipsis;
            n.rectTransform.anchorMin = Vector2.zero;
            n.rectTransform.anchorMax = Vector2.one;
            n.rectTransform.offsetMin = new Vector2(34f, 0f);
            n.rectTransform.offsetMax = new Vector2(-70f, 0f);

            string right = pending ? "loading" : model.MaxContextLength.HasValue ? Compact(model.MaxContextLength.Value) : string.Empty;
            TextMeshProUGUI c = UiKit.Text(bg.transform, "Ctx", right, 12, _tb, pending ? Role.Accent : Role.Muted,
                FontStyles.Normal, TextAlignmentOptions.MidlineRight, false);
            UiKit.SetAnchors(c.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-12f, 0f), new Vector2(62f, RowHeight));
        }

        // ------------------------------------------------------------------ text

        private static string NameOf(ModelCatalog cat, string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            ModelInfo m = cat?.Find(id);
            return m != null ? m.DisplayName : id;
        }

        private static string Facts(float? temp, int? ctx, string job)
        {
            string s = string.Empty;
            if (temp.HasValue) s += "T " + temp.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (ctx.HasValue) s += (s.Length > 0 ? " / " : string.Empty) + "ctx " + ctx.Value.ToString("N0", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(job)) s += (s.Length > 0 ? " / " : string.Empty) + job;
            return s;
        }

        private static string ContextOf(ModelCatalog cat)
        {
            int? c = cat.ConfiguredContextLength ?? cat.ActiveContextLength;
            return c.HasValue ? c.Value.ToString("N0", CultureInfo.InvariantCulture) : null;
        }

        /// <summary>262144 -> "256k". Context windows are powers of two in thousands, so k = 1024 reads right.</summary>
        private static string Compact(int n)
        {
            return n >= 1024 ? (n / 1024).ToString(CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);
        }
    }
}
