using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AgentConsole.View
{
    /// <summary>Which theme token a UI graphic takes its colour from. Restyled on every theme change.</summary>
    internal enum Role
    {
        Bg, Fg, Muted, Accent, AccentDim, Surface, Surface2, Line, Mem, Bad, Flow,
        BubbleUser, BubbleEva, BubbleBad, Code, BubbleInk
    }

    /// <summary>Keeps every themed graphic and repaints them all when the theme changes.</summary>
    internal sealed class ThemeBinder
    {
        private struct Entry
        {
            public Graphic G;
            public Role R;
            public float A;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        public ThemeTokens Tokens { get; private set; }

        /// <summary>Raised after repainting, so rich-text builders can re-format with the new palette.</summary>
        public event Action Applied;

        public static Color Pick(ThemeTokens t, Role r)
        {
            switch (r)
            {
                case Role.Bg: return t.Bg;
                case Role.Fg: return t.Fg;
                case Role.Muted: return t.Muted;
                case Role.Accent: return t.Accent;
                case Role.AccentDim: return t.AccentDim;
                case Role.Surface: return t.Surface;
                case Role.Surface2: return t.Surface2;
                case Role.Line: return t.Line;
                case Role.Mem: return t.Mem;
                case Role.Bad: return t.Bad;
                case Role.BubbleUser: return t.BubbleUser;
                case Role.BubbleEva: return t.BubbleEva;
                case Role.BubbleBad: return t.BubbleBad;
                case Role.Code: return t.Code;
                case Role.BubbleInk: return t.BubbleInk;
                default: return t.Flow;
            }
        }

        public T Bind<T>(T g, Role r, float alpha = 1f) where T : Graphic
        {
            _entries.Add(new Entry { G = g, R = r, A = alpha });
            if (Tokens != null) Paint(g, r, alpha);
            return g;
        }

        public void Apply(ThemeTokens t)
        {
            Tokens = t;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry e = _entries[i];
                if (e.G == null)
                {
                    _entries.RemoveAt(i);
                    continue;
                }
                Paint(e.G, e.R, e.A);
            }
            Applied?.Invoke();
        }

        private void Paint(Graphic g, Role r, float a)
        {
            Color c = Pick(Tokens, r);
            c.a = a;
            g.color = c;
        }
    }

    /// <summary>Procedural sprites (so the UI needs no texture assets) and small factory helpers.</summary>
    internal static class UiKit
    {
        public static TMP_FontAsset Font;

        private static Sprite _rounded, _ring, _circle, _gear, _grip, _bubbleUser, _bubbleEva;

        public static Sprite Rounded => _rounded != null ? _rounded : (_rounded = MakeRounded(false));
        public static Sprite Ring => _ring != null ? _ring : (_ring = MakeRounded(true));
        public static Sprite Circle => _circle != null ? _circle : (_circle = MakeCircle());
        public static Sprite Gear => _gear != null ? _gear : (_gear = MakeGear());

        /// <summary>A bubble with a tight bottom-right corner: the user's, which points at the right edge it sits against.</summary>
        public static Sprite BubbleUser => _bubbleUser != null ? _bubbleUser : (_bubbleUser = MakeBubble(false));

        /// <summary>A bubble with a tight bottom-left corner: the agent's.</summary>
        public static Sprite BubbleEva => _bubbleEva != null ? _bubbleEva : (_bubbleEva = MakeBubble(true));

        /// <summary>Six dots, two columns by three rows: the island's move handle.</summary>
        public static Sprite Grip => _grip != null ? _grip : (_grip = MakeGrip());

        private const int N = 64;
        private const float Corner = 22f;

        private static Sprite MakeRounded(bool ring)
        {
            Texture2D tex = NewTex(N);
            Color32[] px = new Color32[N * N];
            float half = N * 0.5f;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - Corner);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - Corner);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                    float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - Corner; // < 0 inside
                    float a = Mathf.Clamp01(0.5f - d);
                    if (ring) a *= Mathf.Clamp01(d + 3.5f); // a 3px band, which is 1.5px on screen at 2x
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(Corner, Corner, Corner, Corner));
        }

        private const float BubbleSmall = 6f;

        /// <summary>
        /// Rounded rectangle whose bottom corner on the speaker's side is tight. Same distance field as MakeRounded,
        /// but the radius is picked per quadrant; the nine-slice border stays at the large radius so the tight corner
        /// is simply a smaller curve inside the same border.
        /// </summary>
        private static Sprite MakeBubble(bool tightLeft)
        {
            Texture2D tex = NewTex(N);
            Color32[] px = new Color32[N * N];
            float half = N * 0.5f;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    bool bottom = y < half;
                    bool left = x < half;
                    float r = bottom && left == tightLeft ? BubbleSmall : Corner;
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - r);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - r);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                    float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(0.5f - d) * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(Corner, Corner, Corner, Corner));
        }

        private static Sprite MakeCircle()
        {
            Texture2D tex = NewTex(N);
            Color32[] px = new Color32[N * N];
            float half = N * 0.5f;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float d = new Vector2(x + 0.5f - half, y + 0.5f - half).magnitude - (half - 1f);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(0.5f - d) * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeGrip()
        {
            Texture2D tex = NewTex(N);
            Color32[] px = new Color32[N * N];
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float a = 0f;
                    for (int cx = 0; cx < 2; cx++)
                    {
                        for (int cy = 0; cy < 3; cy++)
                        {
                            Vector2 c = new Vector2(N * (0.34f + 0.32f * cx), N * (0.2f + 0.3f * cy));
                            float d = new Vector2(x + 0.5f - c.x, y + 0.5f - c.y).magnitude - N * 0.085f;
                            a = Mathf.Max(a, Mathf.Clamp01(0.5f - d));
                        }
                    }
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite MakeGear()
        {
            Texture2D tex = NewTex(N);
            Color32[] px = new Color32[N * N];
            float half = N * 0.5f;
            const int teeth = 8;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f - half, y + 0.5f - half);
                    float r = p.magnitude;
                    float ang = Mathf.Atan2(p.y, p.x);
                    float phase = Mathf.Repeat(ang / (2f * Mathf.PI) * teeth, 1f);
                    float tooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((0.5f - Mathf.Abs(phase - 0.5f)) * 5f - 0.35f));
                    float outer = Mathf.Lerp(0.66f, 0.95f, tooth) * half;
                    float hole = 0.30f * half;
                    float a = Mathf.Clamp01(outer - r + 0.5f) * Mathf.Clamp01(r - hole + 0.5f);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Texture2D NewTex(int n)
        {
            return new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        // ------------------------------------------------------------------ factories

        public static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5; // UI
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        public static void Stretch(RectTransform rt, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        public static Image Panel(Transform parent, string name, ThemeBinder tb, Role role, float alpha, bool rounded = true, float pxMul = 2f)
        {
            RectTransform rt = NewRect(name, parent);
            Image img = rt.gameObject.AddComponent<Image>();
            img.sprite = rounded ? Rounded : null;
            img.type = rounded ? Image.Type.Sliced : Image.Type.Simple;
            img.pixelsPerUnitMultiplier = pxMul;
            tb?.Bind(img, role, alpha);
            return img;
        }

        public static Image Outline(Transform parent, ThemeBinder tb, Role role, float alpha)
        {
            Image ring = Panel(parent, "Outline", tb, role, alpha);
            ring.sprite = Ring;
            ring.raycastTarget = false;
            Stretch(ring.rectTransform);
            return ring;
        }

        public static TextMeshProUGUI Text(
            Transform parent, string name, string text, float size, ThemeBinder tb, Role role,
            FontStyles style = FontStyles.Normal, TextAlignmentOptions align = TextAlignmentOptions.TopLeft,
            bool wrap = true, bool rich = false)
        {
            RectTransform rt = NewRect(name, parent);
            TextMeshProUGUI t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font != null) t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = align;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.richText = rich;
            t.raycastTarget = false;
            if (tb != null) tb.Bind(t, role);
            else t.color = ThemeBinder.Pick(ThemeTokens.Compute(ThemePresets.BaseOf(ThemePresets.Find(null))), role);
            return t;
        }

        /// <summary>A button with a themed rounded background and a centred label.</summary>
        public static Button TextButton(
            Transform parent, string name, string label, float size, ThemeBinder tb,
            Role bg, float bgAlpha, Role fg, out TextMeshProUGUI text)
        {
            Image img = Panel(parent, name, tb, bg, bgAlpha);
            Button b = img.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            ColorBlock cb = ColorBlock.defaultColorBlock;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            cb.selectedColor = Color.white;
            cb.fadeDuration = 0.08f;
            b.colors = cb;
            text = Text(img.transform, "Label", label, size, tb, fg, FontStyles.Bold, TextAlignmentOptions.Center, false);
            Stretch(text.rectTransform);
            return b;
        }

        public static void SetAnchors(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }
    }
}
