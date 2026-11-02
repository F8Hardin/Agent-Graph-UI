using TMPro;
using UnityEngine;

namespace AgentConsole.View
{
    /// <summary>The faces a node label draws with. See <see cref="ConsoleFonts.Labels"/>.</summary>
    internal readonly struct LabelFaces
    {
        public readonly TMP_FontAsset Upright;
        public readonly TMP_FontAsset Italic;
        public readonly FontStyles UprightStyle;
        public readonly FontStyles ItalicStyle;

        public LabelFaces(TMP_FontAsset upright, TMP_FontAsset italic, FontStyles uprightStyle, FontStyles italicStyle)
        {
            Upright = upright;
            Italic = italic;
            UprightStyle = uprightStyle;
            ItalicStyle = italicStyle;
        }
    }

    /// <summary>
    /// The console's typeface (JetBrains Mono, OFL; the licence sits beside the files), resolved from code.
    /// The assets live under Resources/Fonts/JetBrainsMono and are made by Agent Console/Build Font Assets, so
    /// no scene field has to be wired and the scene can be rebuilt from nothing without losing the font
    /// (STANDARDS section 8). If an asset is missing, everything falls back to TMP's default face rather than
    /// drawing nothing.
    /// </summary>
    internal static class ConsoleFonts
    {
        private const string Dir = "Fonts/JetBrainsMono/JetBrainsMono-";

        private static TMP_FontAsset _regular, _bold, _boldItalic;

        /// <summary>UI body face. Its weight table points at the real Bold, Italic and BoldItalic, so FontStyles work.</summary>
        public static TMP_FontAsset Regular
        {
            get
            {
                if (_regular == null) _regular = Load("Regular") ?? TMP_Settings.defaultFontAsset;
                return _regular;
            }
        }

        /// <summary>
        /// Node labels use the Bold asset directly with no style flag. A weight-table switch would move the glyphs into a
        /// sub-mesh with a different material, and a world-space label tints (fog fade) through its own material instance,
        /// which the sub-mesh would not share. The italic variant is therefore a separate asset swapped in whole.
        /// If the assets are missing this degrades to the default face with the Bold and Italic flags.
        /// </summary>
        public static LabelFaces Labels
        {
            get
            {
                if (_bold == null) _bold = Load("Bold");
                if (_boldItalic == null) _boldItalic = Load("BoldItalic");
                if (_bold != null && _boldItalic != null)
                {
                    return new LabelFaces(_bold, _boldItalic, FontStyles.Normal, FontStyles.Normal);
                }
                return new LabelFaces(TMP_Settings.defaultFontAsset, null, FontStyles.Bold, FontStyles.Bold | FontStyles.Italic);
            }
        }

        private static TMP_FontAsset Load(string face) => Resources.Load<TMP_FontAsset>(Dir + face + " SDF");
    }
}
