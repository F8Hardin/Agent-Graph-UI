using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace AgentConsole.Editor
{
    /// <summary>
    /// Builds the four TextMeshPro SDF font assets from the bundled JetBrains Mono TTFs, so the font is
    /// reproducible from this one file and its constants rather than from a dialog somebody clicked once.
    /// Run it from Agent Console/Build Font Assets after changing a constant or replacing a TTF.
    ///
    /// Why these numbers. The labels are small and sit in a 3D scene, so they are minified by perspective.
    /// An SDF stays crisp under magnification regardless; what protects it under minification is a wide
    /// distance gradient (padding) and a generous source size, hence a 96 pt sampling size with 12 px of
    /// padding in a 2048 atlas rather than TMP's 90/9 in 1024. SDFAA rather than plain SDF so edges are
    /// anti-aliased at the sizes the UI actually draws at.
    ///
    /// Why four faces and not synthetic bold. The Regular asset's weight table points at the other three,
    /// so UI text asking for Bold or Italic gets the real design. Node labels are the exception: they use
    /// the Bold asset directly with no style flag, because a world-space TMP label tints through its own
    /// material instance and a weight-table switch would put the glyphs in a sub-mesh with a different
    /// material, which the fog fade could not reach.
    /// </summary>
    public static class FontAssetBuilder
    {
        public const string Dir = "Assets/Resources/Fonts/JetBrainsMono";

        private const int SamplingPointSize = 96;
        private const int AtlasPadding = 12;
        private const int AtlasSize = 2048;

        // ASCII, Latin-1 and the punctuation the UI strings use. Everything else falls back to Liberation Sans.
        private const string Extra = "\u2018\u2019\u201C\u201D\u2013\u2014\u2026\u2022\u2192\u25B2\u25BC\u25BE\u25B8\u2713";

        [MenuItem("Agent Console/Build Font Assets")]
        public static void Build()
        {
            uint[] unicodes = Charset();
            TMP_FontAsset regular = Make("Regular", unicodes);
            TMP_FontAsset bold = Make("Bold", unicodes);
            TMP_FontAsset italic = Make("Italic", unicodes);
            TMP_FontAsset boldItalic = Make("BoldItalic", unicodes);
            if (regular == null || bold == null || italic == null || boldItalic == null)
            {
                Debug.LogError("[AgentConsole] Font build failed; see the errors above.");
                return;
            }

            TMP_FontWeightPair[] table = regular.fontWeightTable;
            table[4] = new TMP_FontWeightPair { regularTypeface = regular, italicTypeface = italic };
            table[7] = new TMP_FontWeightPair { regularTypeface = bold, italicTypeface = boldItalic };
            EditorUtility.SetDirty(regular);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[AgentConsole] Built JetBrains Mono font assets in " + Dir);
        }

        private static uint[] Charset()
        {
            List<uint> list = new List<uint>();
            for (uint c = 32; c <= 126; c++) list.Add(c);
            for (uint c = 160; c <= 255; c++) list.Add(c);
            foreach (char ch in Extra) list.Add(ch);
            return list.ToArray();
        }

        private static TMP_FontAsset Make(string face, uint[] unicodes)
        {
            string ttf = Dir + "/JetBrainsMono-" + face + ".ttf";
            string path = Dir + "/JetBrainsMono-" + face + " SDF.asset";
            Font font = AssetDatabase.LoadAssetAtPath<Font>(ttf);
            if (font == null)
            {
                Debug.LogError("[AgentConsole] Missing font file " + ttf);
                return null;
            }

            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path) != null) AssetDatabase.DeleteAsset(path);

            // Dynamic only so TryAddCharacters will run; it is frozen to Static below, so a build never
            // needs the source font and the atlas cannot grow at runtime.
            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(
                font, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA, AtlasSize, AtlasSize,
                AtlasPopulationMode.Dynamic, false);
            if (asset == null)
            {
                Debug.LogError("[AgentConsole] CreateFontAsset returned null for " + face);
                return null;
            }
            asset.name = "JetBrainsMono-" + face + " SDF";

            asset.TryAddCharacters(unicodes, out uint[] missing);
            if (missing != null && missing.Length > 0)
            {
                Debug.LogWarning("[AgentConsole] " + face + ": " + missing.Length + " characters did not fit or are not in the font.");
            }

            if (TMP_Settings.defaultFontAsset != null)
            {
                asset.fallbackFontAssetTable = new List<TMP_FontAsset> { TMP_Settings.defaultFontAsset };
            }
            asset.atlasPopulationMode = AtlasPopulationMode.Static;
            // Frozen, so the CPU copy of the atlas is dead weight in a player: drop it.
            for (int i = 0; i < asset.atlasTextures.Length; i++) asset.atlasTextures[i].Apply(false, true);

            AssetDatabase.CreateAsset(asset, path);
            asset.material.name = asset.name + " Material";
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            for (int i = 0; i < asset.atlasTextures.Length; i++)
            {
                asset.atlasTextures[i].name = asset.name + " Atlas" + (i == 0 ? string.Empty : " " + i);
                AssetDatabase.AddObjectToAsset(asset.atlasTextures[i], asset);
            }
            EditorUtility.SetDirty(asset);
            return asset;
        }
    }
}
