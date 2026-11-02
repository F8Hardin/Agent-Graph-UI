using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AgentConsole.View
{
    /// <summary>
    /// A floating translucent panel with a header button and a rotating caret (the prototype's Disc).
    /// Collapsing eases the panel's height down to the header and turns the caret a quarter turn.
    /// </summary>
    internal sealed class CollapsiblePanel
    {
        public const float HeaderHeight = 46f;

        private readonly float _width;
        private readonly float _openHeight;
        private readonly RectTransform _caret;
        private bool _open = true;
        private float _t = 1f;

        public readonly RectTransform Root;
        public readonly RectTransform Body;
        public readonly TextMeshProUGUI Note;
        public readonly TextMeshProUGUI Right;

        public bool IsOpen => _open;

        public CollapsiblePanel(Transform parent, ThemeBinder tb, string title, bool left, float width, float openHeight, float topInset)
        {
            _width = width;
            _openHeight = openHeight;

            Root = UiKit.NewRect(title + " Panel", parent);
            Vector2 anchor = left ? new Vector2(0f, 1f) : new Vector2(1f, 1f);
            UiKit.SetAnchors(Root, anchor, anchor, new Vector2(left ? 16f : -16f, -topInset), new Vector2(width, openHeight));

            Image bg = UiKit.Panel(Root, "Bg", tb, Role.Surface, 0.90f);
            UiKit.Stretch(bg.rectTransform);
            UiKit.Outline(Root, tb, Role.Line, 0.9f);

            RectTransform header = UiKit.NewRect("Header", Root);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.anchoredPosition = Vector2.zero;
            header.sizeDelta = new Vector2(0f, HeaderHeight);
            Image hit = header.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f); // invisible, but it takes the click
            Button btn = header.gameObject.AddComponent<Button>();
            btn.targetGraphic = hit;
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(Toggle);

            TextMeshProUGUI caret = UiKit.Text(header, "Caret", ">", 18, tb, Role.Accent, FontStyles.Bold, TextAlignmentOptions.Center, false);
            _caret = caret.rectTransform;
            UiKit.SetAnchors(_caret, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(26f, 0f), new Vector2(24f, 24f));

            TextMeshProUGUI t = UiKit.Text(header, "Title", title, 18, tb, Role.Fg, FontStyles.Bold, TextAlignmentOptions.MidlineLeft, false);
            UiKit.SetAnchors(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(46f, 0f), new Vector2(150f, HeaderHeight));

            Note = UiKit.Text(header, "Note", string.Empty, 13, tb, Role.Muted, FontStyles.Normal, TextAlignmentOptions.MidlineLeft, false);
            UiKit.SetAnchors(Note.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(46f + MeasureTitle(title), 0f), new Vector2(170f, HeaderHeight));

            Right = UiKit.Text(header, "Right", string.Empty, 13, tb, Role.Accent, FontStyles.Bold, TextAlignmentOptions.MidlineRight, false);
            UiKit.SetAnchors(Right.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-16f, 0f), new Vector2(90f, HeaderHeight));

            Body = UiKit.NewRect("Body", Root);
            Body.anchorMin = Vector2.zero;
            Body.anchorMax = Vector2.one;
            Body.offsetMin = new Vector2(0f, 6f);
            Body.offsetMax = new Vector2(0f, -HeaderHeight);
            Body.gameObject.AddComponent<RectMask2D>();
        }

        private static float MeasureTitle(string title) => 12f + title.Length * 10.5f;

        public void Toggle() => _open = !_open;

        public void Tick(float dt)
        {
            float target = _open ? 1f : 0f;
            _t = Mathf.MoveTowards(_t, target, dt * 4f);
            float e = Mathf.SmoothStep(0f, 1f, _t);
            _caret.localRotation = Quaternion.Euler(0f, 0f, -90f * e);
            Root.sizeDelta = new Vector2(_width, Mathf.Lerp(HeaderHeight, _openHeight, e));
            bool show = _t > 0.001f;
            if (Body.gameObject.activeSelf != show) Body.gameObject.SetActive(show);
        }
    }
}
