using UnityEngine;

namespace AgentConsole.View
{
    /// <summary>The five values a theme is made of. Everything else is mixed from bg and fg.</summary>
    public struct ThemeBase
    {
        public Color Bg;
        public Color Fg;
        public Color Accent;
        public Color Mem;
        public Color Bad;
    }

    public sealed class ThemePreset
    {
        public string Key { get; }
        public string Label { get; }
        public bool Dark { get; }
        public Color Bg { get; }
        public Color Fg { get; }

        public ThemePreset(string key, string label, string bg, string fg)
        {
            Key = key;
            Label = label;
            Bg = ThemeTokens.Parse(bg);
            Fg = ThemeTokens.Parse(fg);
            Dark = ThemeTokens.Lum(Bg) < 0.5f;
        }
    }

    /// <summary>The prototype's PRESETS, ported verbatim, and its baseOf().</summary>
    public static class ThemePresets
    {
        public const string DefaultKey = "green-k";

        public static readonly ThemePreset[] All =
        {
            new ThemePreset("green-k", "Green", "#05060a", "#3df07a"),
            new ThemePreset("pink-k", "Pink", "#07050a", "#ff5fc8"),
            new ThemePreset("purple-k", "Purple", "#06050c", "#b57bff"),
            new ThemePreset("blue-k", "Blue", "#04070c", "#58b6ff"),
            new ThemePreset("green-w", "Green", "#f6f8f6", "#0a7a3c"),
            new ThemePreset("pink-w", "Pink", "#f9f6f8", "#b81d7c"),
            new ThemePreset("purple-w", "Purple", "#f7f6fa", "#5f26c4"),
            new ThemePreset("blue-w", "Blue", "#f5f7fa", "#0a5aa8")
        };

        public static ThemePreset Find(string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                for (int i = 0; i < All.Length; i++)
                {
                    if (All[i].Key == key) return All[i];
                }
            }
            return All[0];
        }

        public static ThemeBase BaseOf(ThemePreset p)
        {
            bool dark = p.Dark;
            return new ThemeBase
            {
                Bg = p.Bg,
                Fg = p.Fg,
                Accent = p.Fg,
                Mem = dark ? Color.Lerp(p.Fg, Color.white, 0.55f) : Color.Lerp(p.Fg, Color.black, 0.35f),
                Bad = ThemeTokens.Parse(dark ? "#ff5f56" : "#c01d14")
            };
        }
    }

    /// <summary>
    /// Port of the prototype's tokens(): one formula serves a black terminal and a white one, because
    /// every mix is "x% of the way from the background toward the font colour".
    /// </summary>
    public sealed class ThemeTokens
    {
        public bool Dark;
        public Color Bg, Fg, Accent, Mem, Bad;
        public Color Surface, Surface2, Line, Muted, AccentDim, Flow;
        public Color NAgent, NSrv, NTool, NHot, Link, Link2;

        /// <summary>
        /// What the graph is drawn over: the camera clear colour, the fog and the ambient. Equal to Bg on a dark theme
        /// and a soft grey on a light one, so the page (panels, bars) can stay white while the spheres have something to
        /// read against.
        /// </summary>
        public Color Stage;

        // The stage is the background multiplied down by this fraction of its own luminance. The weighting is the
        // whole trick: black has no luminance to give up, so dark themes are untouched, while a near-white page
        // loses about an eighth of its brightness (the ambient particles take a little more once drawn). There is no light/dark branch anywhere in it.
        private const float StageDim = 0.12f;

        // Contrast ratios (WCAG relative luminance) an edge must reach against the stage. The edge is the smallest
        // mix toward the font colour that gets there, so it is as faint as the theme allows but never invisible.
        // 2.2 / 1.6 reproduce the old fixed 26% / 16% mix on the dark themes to within a few percent.
        private const float LinkContrast = 2.2f;
        private const float Link2Contrast = 1.6f;
        private const float MaxLinkMix = 0.8f;

        public static ThemeTokens Compute(ThemeBase b)
        {
            bool dark = Lum(b.Bg) < 0.5f;
            Color white = Color.white;
            Color black = Color.black;
            Color stage = StageOf(b.Bg);
            return new ThemeTokens
            {
                Dark = dark,
                Bg = b.Bg,
                Fg = b.Fg,
                Accent = b.Accent,
                Mem = b.Mem,
                Bad = b.Bad,
                Surface = Mix(b.Bg, b.Fg, 0.07f),
                Surface2 = Mix(b.Bg, b.Fg, 0.12f),
                Line = Mix(b.Bg, b.Fg, 0.24f),
                Muted = Mix(b.Bg, b.Fg, 0.60f),
                AccentDim = Mix(b.Bg, b.Accent, 0.45f),
                Flow = dark ? Mix(b.Accent, white, 0.45f) : Mix(b.Accent, black, 0.25f),
                NAgent = b.Accent,
                NSrv = Mix(b.Bg, b.Fg, 0.62f),
                NTool = Mix(b.Bg, b.Fg, 0.38f),
                NHot = dark ? Mix(b.Accent, white, 0.6f) : Mix(b.Accent, black, 0.35f),
                Stage = stage,
                Link = MixForContrast(stage, b.Fg, LinkContrast),
                Link2 = MixForContrast(stage, b.Fg, Link2Contrast)
            };
        }

        public static Color StageOf(Color bg)
        {
            float k = 1f - StageDim * Lum(bg);
            return new Color(bg.r * k, bg.g * k, bg.b * k, 1f);
        }

        /// <summary>
        /// The smallest mix from <paramref name="backdrop"/> toward <paramref name="toward"/> whose contrast ratio against
        /// the backdrop reaches <paramref name="ratio"/>. The old edge colour was a fixed percentage, which is a different
        /// amount of contrast on every background: ample on black, almost none on white. Asking for the contrast and
        /// solving for the mix gives one rule for both. Bisection over a monotonic range; runs on a theme change only.
        /// </summary>
        public static Color MixForContrast(Color backdrop, Color toward, float ratio) =>
            MixForContrast(backdrop, toward, backdrop, ratio);

        /// <summary>
        /// The same search from any starting colour, measured against another: "how far must <paramref name="from"/> move
        /// toward <paramref name="toward"/> to read at <paramref name="ratio"/> on <paramref name="against"/>". Text ink
        /// uses it with from = the theme's font colour, so a themed ink is kept where it already reads and only pushed
        /// toward black or white where it does not.
        /// </summary>
        public static Color MixForContrast(Color from, Color toward, Color against, float ratio)
        {
            float ya = RelativeLuminance(against);
            float lo = 0f;
            float hi = MaxLinkMix;
            if (Contrast(ya, RelativeLuminance(from)) >= ratio) return Mix(from, toward, 0f);
            if (Contrast(ya, RelativeLuminance(Mix(from, toward, hi))) < ratio) return Mix(from, toward, hi);
            for (int i = 0; i < 16; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Contrast(ya, RelativeLuminance(Mix(from, toward, mid))) >= ratio) hi = mid;
                else lo = mid;
            }
            return Mix(from, toward, hi);
        }

        public static float Contrast(float y1, float y2)
        {
            float hi = Mathf.Max(y1, y2);
            float lo = Mathf.Min(y1, y2);
            return (hi + 0.05f) / (lo + 0.05f);
        }

        /// <summary>WCAG relative luminance of an sRGB colour (linearised, then weighted).</summary>
        public static float RelativeLuminance(Color srgb)
        {
            Color c = srgb.linear;
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        /// <summary>Ink is chosen against the fill it sits on, not against the page.</summary>
        public Color Ink(Color fill) => Lum(fill) > 0.5f ? Bg : Fg;

        public static Color Mix(Color a, Color b, float t)
        {
            Color c = Color.Lerp(a, b, t);
            c.a = 1f;
            return c;
        }

        public static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        public static Color Parse(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out Color c) ? c : new Color(0.5f, 0.5f, 0.5f, 1f);
        }

        public static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}
