using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The default node style: a glass dome with bright motes drifting inside. All the drawing is in
    /// AgentNodePlasma.shader; this class turns a theme and a glow level into its parameters.
    ///
    /// The motes are procedural in the fragment shader, not ParticleSystems (rejected: one system per node is a draw
    /// call and CPU work per node, which the Quest 3 target cannot afford). Their animation phase is accumulated here
    /// and pushed as a float, so the speed can follow the glow without the motes jumping (rejected: _Time * speed in
    /// the shader, which teleports every mote the instant the speed changes).
    /// </summary>
    internal sealed class PlasmaDomeVisual : NodeVisual
    {
        private static readonly int IdBase = Shader.PropertyToID("_BaseColor");
        private static readonly int IdHot = Shader.PropertyToID("_HotColor");
        private static readonly int IdRim = Shader.PropertyToID("_RimColor");
        private static readonly int IdInner = Shader.PropertyToID("_InnerColor");
        private static readonly int IdMote = Shader.PropertyToID("_MoteColor");
        private static readonly int IdRimGain = Shader.PropertyToID("_RimGain");
        private static readonly int IdInnerGain = Shader.PropertyToID("_InnerGain");
        private static readonly int IdMoteGain = Shader.PropertyToID("_MoteGain");
        private static readonly int IdFill = Shader.PropertyToID("_Fill");
        private static readonly int IdLitFill = Shader.PropertyToID("_LitFill");
        private static readonly int IdAlpha = Shader.PropertyToID("_Alpha");
        private static readonly int IdGlow = Shader.PropertyToID("_Glow");
        private static readonly int IdSelected = Shader.PropertyToID("_Selected");
        private static readonly int IdSeed = Shader.PropertyToID("_Seed");
        private static readonly int IdPhase = Shader.PropertyToID("_MotePhase");
        private static readonly int IdMoteCount = Shader.PropertyToID("_MoteCount");

        // Radians per second of mote phase at rest, and the extra a fully lit node adds. A running node is visibly
        // busier but the motes never blur into streaks.
        private const float PhaseBase = 0.45f;
        private const float PhaseBoost = 2.6f;
        // Selection eases in and out over a few frames instead of snapping when a drag starts.
        private const float SelectK = 14f;

        // How much of the node's own colour the centre of the dome shows. The centre stays the clearest part so the
        // label reads; the shader densifies toward the rim. Light themes get a denser body because their stage is
        // pale and an airy dome would vanish into it.
        private const float FillDim = 0.20f;
        private const float FillBright = 0.16f;
        private const float FillDimLight = 0.30f;
        private const float FillBrightLight = 0.52f;
        private const float LitFillDark = 0.62f;
        private const float LitFillLight = 0.70f;
        // The inner glow adds a little colour to the centre on top of the fill; the ink backdrop includes it.
        private const float CentreGlowBias = 0.10f;

        private readonly bool _down;
        private readonly float _seed;
        private float _fill = FillDim;
        private float _litFill = LitFillDark;
        private float _phase;
        private float _selected;
        private float _selectedTarget;
        private float _lastGlow = -1f;
        private float _lastSelected = -1f;

        public PlasmaDomeVisual(GraphNode node, float radius, Transform root, Mesh sphere, Material template)
            : base(node, radius, root, sphere, template)
        {
            _down = node.IsDown;
            _seed = SeedOf(node.Id);
            Mat.SetFloat(IdSeed, _seed);
            // Fewer motes in a small sphere: ten dots in a 0.8 radius tool would be a noise field.
            Mat.SetFloat(IdMoteCount, node.Tier == NodeTier.Agent ? 10f : node.Tier == NodeTier.Server ? 8f : 6f);
            // Start each node at a different phase so identical tools do not breathe in unison.
            _phase = _seed * 0.37f;
        }

        /// <summary>Stable per-node value in [0, 97). FNV-1a over the id (string.GetHashCode differs between runs).</summary>
        private static float SeedOf(string id)
        {
            uint h = 2166136261u;
            if (id != null)
            {
                for (int i = 0; i < id.Length; i++) h = (h ^ id[i]) * 16777619u;
            }
            return (h % 9700u) * 0.01f;
        }

        public override void ApplyTheme(ThemeTokens t)
        {
            Color b = BaseOf(t);
            Color hot = HotOf(t);
            bool bright = ThemeTokens.Lum(b) > 0.5f;
            _fill = t.Dark ? (bright ? FillBright : FillDim) : (bright ? FillBrightLight : FillDimLight);
            _litFill = t.Dark ? LitFillDark : LitFillLight;

            Color rim, inner, mote;
            float rimGain, innerGain, moteGain;
            if (t.Dark)
            {
                // Additive-looking: pale, HDR (>1) so the existing bloom turns rim and motes into a halo.
                rim = ThemeTokens.Mix(b, Color.white, 0.55f);
                inner = ThemeTokens.Mix(b, Color.white, 0.30f);
                mote = ThemeTokens.Mix(b, Color.white, 0.78f);
                rimGain = 2.3f;
                innerGain = 1.5f;
                moteGain = 2.6f;
            }
            else
            {
                // Additive light is invisible on a pale stage, so everything is an alpha-blended darker tint.
                rim = ThemeTokens.Mix(b, Color.black, 0.42f);
                inner = b;
                mote = ThemeTokens.Mix(b, Color.black, 0.40f);
                rimGain = 1f;
                innerGain = 1f;
                moteGain = 1f;
            }
            Mat.SetColor(IdBase, b);
            Mat.SetColor(IdHot, hot);
            Mat.SetColor(IdRim, rim);
            Mat.SetColor(IdInner, inner);
            Mat.SetColor(IdMote, mote);
            Mat.SetFloat(IdRimGain, rimGain);
            Mat.SetFloat(IdInnerGain, innerGain);
            Mat.SetFloat(IdMoteGain, moteGain);
            Mat.SetFloat(IdFill, _fill);
            Mat.SetFloat(IdLitFill, _litFill);
            // A node that is down stays recognisable by its lower alpha, dim label and down-coloured edges.
            Mat.SetFloat(IdAlpha, _down ? 0.5f : 1f);
            _lastGlow = -1f;
        }

        // The plain tier colour is the base. Memory and error tints live in the HOT colour, which the shader blends
        // in by glow, so they pulse in and decay with the glow instead of sticking to the node.
        private Color BaseOf(ThemeTokens t) =>
            Node.Tier == NodeTier.Agent ? t.NAgent : Node.Tier == NodeTier.Server ? t.NSrv : t.NTool;

        private Color HotOf(ThemeTokens t) =>
            Node.IsDown ? t.Bad : Node.IsMemory ? t.Mem : Node.Tier == NodeTier.Agent ? t.NHot : t.Flow;

        public override Color BackdropFor(ThemeTokens t, bool lit)
        {
            // Over the stage, not the page: on a light theme the graph sits on a greyer ground than the panels do.
            // The centre of the dome, where the label sits, is what matters, not the denser rim.
            if (lit) return Color.Lerp(t.Stage, HotOf(t), Mathf.Min(1f, _litFill + CentreGlowBias + 0.15f));
            return Color.Lerp(t.Stage, BaseOf(t), Mathf.Min(1f, _fill + CentreGlowBias));
        }

        public override void SetSelected(bool selected) => _selectedTarget = selected ? 1f : 0f;

        public override void Render(float glow, float dt)
        {
            _phase += dt * (PhaseBase + PhaseBoost * glow);
            Mat.SetFloat(IdPhase, _phase);

            if (!Mathf.Approximately(glow, _lastGlow))
            {
                _lastGlow = glow;
                Mat.SetFloat(IdGlow, glow);
            }

            if (!Mathf.Approximately(_selected, _selectedTarget))
            {
                _selected += (_selectedTarget - _selected) * (1f - Mathf.Exp(-SelectK * dt));
                if (Mathf.Abs(_selected - _selectedTarget) < 0.002f) _selected = _selectedTarget;
            }
            if (!Mathf.Approximately(_selected, _lastSelected))
            {
                _lastSelected = _selected;
                Mat.SetFloat(IdSelected, _selected);
            }
        }
    }
}
