using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AgentConsole.View
{
    internal enum IslandCorner { TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>
    /// Who owns a pointer press. A press over an island belongs to the island, not to the graph; CameraRig asks this
    /// in the same place it already tells a node drag from an orbit, instead of every island guessing.
    /// </summary>
    internal static class IslandRegistry
    {
        private static readonly List<Island> All = new List<Island>();

        /// <summary>True from the press on a grip or corner until its release. A drag that slides off the island stays the island's.</summary>
        public static bool Grabbing { get; internal set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            All.Clear();
            Grabbing = false;
        }

        internal static void Add(Island i) => All.Add(i);

        /// <summary>No allocation: a plain loop over rects, so CameraRig can call it every frame.</summary>
        public static bool PointerOver(Vector2 screen)
        {
            for (int i = All.Count - 1; i >= 0; i--)
            {
                RectTransform rt = All[i].Root;
                if (rt == null)
                {
                    All.RemoveAt(i);
                    continue;
                }
                if (!rt.gameObject.activeInHierarchy) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(rt, screen, All[i].EventCamera)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The frame around every panel: a six-dot grip at the top right moves it, the four corners resize it, and the
    /// header collapses it. Position, size and collapsed are saved per kind through <see cref="ConsoleSettings"/>.
    ///
    /// Positions are canvas units from the canvas's top-left, and all pointer maths goes through
    /// RectTransformUtility against the canvas rect, so nothing here assumes an overlay canvas or snaps to pixels:
    /// these move to world-space canvases in XR unchanged. Only the grip moves an island; dragging the body scrolls.
    /// </summary>
    internal sealed class Island
    {
        public const float HeaderHeight = 46f;
        private const float KeepOnScreen = 56f; // how much of an island must stay inside the canvas

        private readonly string _kind;
        private readonly RectTransform _canvasRect;
        private readonly Canvas _canvas;
        private readonly Vector2 _min;
        private readonly Func<Vector2, Rect> _default;
        private readonly RectTransform _caret;
        private readonly GameObject[] _corners = new GameObject[4];
        private bool _open;
        private float _t;
        private bool _auto;
        private bool _ready;
        private Vector2 _pos;
        private float _w, _openH;
        private Vector2 _lastApplied = new Vector2(float.NaN, float.NaN);
        private Vector2 _lastSize = new Vector2(float.NaN, float.NaN);

        public readonly RectTransform Root;
        public readonly RectTransform Body;
        public readonly TextMeshProUGUI Note;
        public readonly TextMeshProUGUI Right;

        public bool IsOpen => _open;
        public string Kind => _kind;

        /// <summary>The frame in canvas units (top-left origin, y down), at its open height. For tests and other panels.</summary>
        public Rect OpenRect => new Rect(_pos.x, _pos.y, _w, _openH);

        /// <summary>Null for a screen-space overlay canvas, the canvas camera otherwise (world-space in XR).</summary>
        public Camera EventCamera => _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;

        /// <param name="defaultRect">Where the island sits until the user moves it: canvas size in, rect out (top-left origin, y down).</param>
        public Island(
            Transform parent, ThemeBinder tb, string kind, string title, Vector2 minSize,
            Func<Vector2, Rect> defaultRect)
        {
            _kind = kind;
            _min = minSize;
            _default = defaultRect;
            _canvas = parent.GetComponentInParent<Canvas>().rootCanvas;
            _canvasRect = (RectTransform)_canvas.transform;

            IslandLayout saved = ConsoleSettings.LoadLayout(kind);
            _auto = !saved.placed;
            _open = !saved.collapsed;
            _t = _open ? 1f : 0f;
            if (saved.placed)
            {
                _pos = new Vector2(saved.x, saved.y);
                _w = Mathf.Max(saved.w, minSize.x);
                _openH = Mathf.Max(saved.h, minSize.y);
            }
            else
            {
                _w = minSize.x;
                _openH = minSize.y;
            }

            Root = UiKit.NewRect(title + " Island", parent);
            Root.anchorMin = new Vector2(0f, 1f);
            Root.anchorMax = new Vector2(0f, 1f);
            Root.pivot = new Vector2(0f, 1f);
            Root.sizeDelta = new Vector2(_w, _openH);

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
            UiKit.SetAnchors(Note.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(46f + 12f + title.Length * 10.5f, 0f), new Vector2(170f, HeaderHeight));

            Right = UiKit.Text(header, "Right", string.Empty, 13, tb, Role.Accent, FontStyles.Bold, TextAlignmentOptions.MidlineRight, false);
            UiKit.SetAnchors(Right.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-50f, 0f), new Vector2(90f, HeaderHeight));

            Body = UiKit.NewRect("Body", Root);
            Body.anchorMin = Vector2.zero;
            Body.anchorMax = Vector2.one;
            Body.offsetMin = new Vector2(0f, 6f);
            Body.offsetMax = new Vector2(0f, -HeaderHeight);
            Body.gameObject.AddComponent<RectMask2D>();

            // Grip last in the header so it sits above the header's collapse button and takes the press.
            Image grip = UiKit.Panel(header, "Grip", tb, Role.Muted, 0.9f, false);
            grip.sprite = UiKit.Grip;
            grip.type = Image.Type.Simple;
            UiKit.SetAnchors(grip.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-10f, 0f), new Vector2(30f, 34f));
            IslandHandle gh = grip.gameObject.AddComponent<IslandHandle>();
            gh.Init(this, null);

            for (int i = 0; i < 4; i++) _corners[i] = BuildCorner(tb, (IslandCorner)i);

            IslandRegistry.Add(this);
            Apply();
        }

        private GameObject BuildCorner(ThemeBinder tb, IslandCorner c)
        {
            bool left = c == IslandCorner.TopLeft || c == IslandCorner.BottomLeft;
            bool top = c == IslandCorner.TopLeft || c == IslandCorner.TopRight;
            Image img = UiKit.Panel(Root, "Corner " + c, tb, Role.Muted, 0.0f, false);
            img.color = new Color(0f, 0f, 0f, 0f); // a hit area; the visible mark is its child
            Vector2 anchor = new Vector2(left ? 0f : 1f, top ? 1f : 0f);
            UiKit.SetAnchors(img.rectTransform, anchor, anchor, Vector2.zero, new Vector2(26f, 26f));

            Image dot = UiKit.Panel(img.transform, "Mark", tb, Role.Muted, 0.55f);
            dot.sprite = UiKit.Circle;
            dot.type = Image.Type.Simple;
            dot.raycastTarget = false;
            Vector2 inset = new Vector2(left ? 9f : -9f, top ? -9f : 9f);
            UiKit.SetAnchors(dot.rectTransform, anchor, anchor, inset, new Vector2(5f, 5f));

            img.gameObject.AddComponent<IslandHandle>().Init(this, c);
            return img.gameObject;
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>Move by a delta in canvas units (+x right, +y DOWN). The grip's drag calls this; so can a test.</summary>
        public void MoveBy(Vector2 delta)
        {
            Detach();
            _pos += delta;
            Clamp();
            Apply();
        }

        /// <summary>Resize from one corner by a delta in canvas units. The opposite corner stays put, and the minimum size is respected.</summary>
        public void ResizeBy(IslandCorner corner, Vector2 delta)
        {
            if (!_open) return;
            Detach();
            float left = _pos.x, top = _pos.y, right = _pos.x + _w, bottom = _pos.y + _openH;
            bool movesLeft = corner == IslandCorner.TopLeft || corner == IslandCorner.BottomLeft;
            bool movesTop = corner == IslandCorner.TopLeft || corner == IslandCorner.TopRight;
            if (movesLeft) left += delta.x; else right += delta.x;
            if (movesTop) top += delta.y; else bottom += delta.y;
            if (right - left < _min.x)
            {
                if (movesLeft) left = right - _min.x; else right = left + _min.x;
            }
            if (bottom - top < _min.y)
            {
                if (movesTop) top = bottom - _min.y; else bottom = top + _min.y;
            }
            _pos = new Vector2(left, top);
            _w = right - left;
            _openH = bottom - top;
            Clamp();
            Apply();
        }

        /// <summary>Save the frame. Called when a drag ends, not per frame.</summary>
        public void Commit()
        {
            ConsoleSettings.SaveLayout(_kind, new IslandLayout
            {
                placed = !_auto, x = _pos.x, y = _pos.y, w = _w, h = _openH, collapsed = !_open
            });
        }

        public void Toggle()
        {
            _open = !_open;
            Commit();
        }

        /// <summary>Forget the saved frame and go back to the code default.</summary>
        public void ResetLayout()
        {
            _auto = true;
            _ready = false;
            Commit();
        }

        public void Tick(float dt)
        {
            Vector2 cs = _canvasRect.rect.size;
            if (cs.x < 1f || cs.y < 1f) return;
            if (_auto)
            {
                // An unplaced island re-docks with the window, and its default may depend on other panels (the tool
                // input sits above the prompt box as that grows).
                Rect r = _default(cs);
                _pos = r.position;
                _w = Mathf.Max(r.width, _min.x);
                _openH = Mathf.Max(r.height, _min.y);
            }
            else if (!_ready)
            {
                Clamp();
            }
            _ready = true;

            float target = _open ? 1f : 0f;
            _t = Mathf.MoveTowards(_t, target, dt * 4f);
            float e = Mathf.SmoothStep(0f, 1f, _t);
            _caret.localRotation = Quaternion.Euler(0f, 0f, -90f * e);
            bool show = _t > 0.001f;
            if (Body.gameObject.activeSelf != show) Body.gameObject.SetActive(show);
            bool corners = _open && _t > 0.99f;
            for (int i = 0; i < _corners.Length; i++)
            {
                if (_corners[i].activeSelf != corners) _corners[i].SetActive(corners);
            }
            Apply();
        }

        private void Detach()
        {
            if (!_auto) return;
            _auto = false; // from here the user owns the frame
        }

        private void Clamp()
        {
            Vector2 cs = _canvasRect.rect.size;
            if (cs.x < 1f) return;
            _w = Mathf.Min(_w, cs.x);
            _openH = Mathf.Min(_openH, cs.y);
            // Keep the header reachable: some of the island, including the grip's row, must stay on screen.
            _pos.x = Mathf.Clamp(_pos.x, KeepOnScreen - _w, cs.x - KeepOnScreen);
            _pos.y = Mathf.Clamp(_pos.y, 0f, cs.y - HeaderHeight);
        }

        private void Apply()
        {
            float h = Mathf.Lerp(HeaderHeight, _openH, Mathf.SmoothStep(0f, 1f, _t));
            Vector2 p = new Vector2(_pos.x, -_pos.y);
            if (p != _lastApplied)
            {
                Root.anchoredPosition = p;
                _lastApplied = p;
            }
            Vector2 s = new Vector2(_w, h);
            if (s != _lastSize)
            {
                Root.sizeDelta = s;
                _lastSize = s;
            }
        }
    }

    /// <summary>The grip (corner null) or one corner of an island. Turns pointer drags into canvas-unit deltas.</summary>
    internal sealed class IslandHandle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, IEndDragHandler
    {
        private Island _island;
        private IslandCorner? _corner;
        private RectTransform _canvasRect;

        public void Init(Island island, IslandCorner? corner)
        {
            _island = island;
            _corner = corner;
            _canvasRect = (RectTransform)GetComponentInParent<Canvas>().rootCanvas.transform;
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Left) IslandRegistry.Grabbing = true;
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left) return;
            IslandRegistry.Grabbing = false;
            _island.Commit();
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left) return;
            // Screen delta -> canvas delta through the canvas rect, so it holds for world-space canvases too.
            Camera cam = e.pressEventCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, e.position, cam, out Vector2 now)) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, e.position - e.delta, cam, out Vector2 before)) return;
            Vector2 d = new Vector2(now.x - before.x, -(now.y - before.y)); // canvas y is up, ours is down
            if (_corner.HasValue) _island.ResizeBy(_corner.Value, d);
            else _island.MoveBy(d);
        }

        public void OnEndDrag(PointerEventData e)
        {
            IslandRegistry.Grabbing = false;
            _island.Commit();
        }
    }
}
