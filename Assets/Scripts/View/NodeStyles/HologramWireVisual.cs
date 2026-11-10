using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// Hologram Wire: a terminal-phosphor wireframe globe, the cheap style for the headset. One draw call per node and
    /// minimal overdraw (the shader reads the back hemisphere analytically in the same fragment rather than drawing
    /// the sphere twice), and no bloom dependency: every colour here is at most 1, so it looks finished with
    /// post-processing off. All the drawing is in AgentNodeHologram.shader; this class turns a theme and a glow level
    /// into its parameters.
    /// </summary>
    internal sealed class HologramWireVisual : NodeVisual
    {
        private static readonly int IdLine = Shader.PropertyToID("_LineColor");
        private static readonly int IdOutline = Shader.PropertyToID("_OutlineColor");
        private static readonly int IdFillColor = Shader.PropertyToID("_FillColor");
        private static readonly int IdHot = Shader.PropertyToID("_HotColor");
        private static readonly int IdFill = Shader.PropertyToID("_Fill");
        private static readonly int IdLitFill = Shader.PropertyToID("_LitFill");
        private static readonly int IdGlow = Shader.PropertyToID("_Glow");
        private static readonly int IdSelected = Shader.PropertyToID("_Selected");
        private static readonly int IdAlpha = Shader.PropertyToID("_Alpha");
        private static readonly int IdPhase = Shader.PropertyToID("_Phase");
        private static readonly int IdScan = Shader.PropertyToID("_Scan");

        // Longitude turns slowly at rest (radians per second) and a running node spins noticeably faster.
        private const float PhaseBase = 0.22f;
        private const float PhaseBoost = 0.9f;
        // The scanline's sweep in turns per second: one pass every ~5 s at rest, every ~2 s when running.
        private const float ScanBase = 0.2f;
        private const float ScanBoost = 0.3f;
        private const float SelectK = 14f;

        // Fill rises from almost nothing to a faint wash. Light themes need a little more: a pale stage swallows a
        // very faint tint.
        private const float FillRest = 0.015f;
        private const float FillRestLight = 0.04f;
        private const float FillLit = 0.13f;
        private const float FillLitLight = 0.20f;

        private readonly bool _down;
        private float _fillLit = FillLit;
        private float _fillRest = FillRest;
        private float _phase;
        private float _scan;
        private float _selected;
        private float _selectedTarget;
        private float _lastGlow = -1f;
        private float _lastSelected = -1f;

        public HologramWireVisual(GraphNode node, float radius, Transform root, Mesh sphere, Material template)
            : base(node, radius, root, sphere, template)
        {
            _down = node.IsDown;
            float seed = NodeStyleMath.SeedOf(node.Id);
            // Each node starts at its own longitude and scanline height, so identical tools do not sweep in unison.
            _phase = seed * 0.37f;
            _scan = seed * 0.113f;
        }

        public override void ApplyTheme(ThemeTokens t)
        {
            Color tier = NodeStyleMath.BaseOf(t, Node.Tier);
            Color hot = NodeStyleMath.HotOf(t, Node);
            Color line, outline, fill;
            if (t.Dark)
            {
                // Phosphor: the tier colour pulled toward the accent so a dim tool is still a bright green line, and
                // never above 1 (no bloom to lean on).
                line = ThemeTokens.Mix(tier, t.NAgent, 0.7f);
                outline = ThemeTokens.Mix(line, Color.white, 0.18f);
                fill = t.NAgent;
            }
            else
            {
                // Additive is invisible on white; lines are a darker alpha-blended tint of the accent.
                // Lines are the accent itself, darkened: the tier colours of tools are pale mixes toward the page, which
                // vanish on a grey stage as 1 px lines.
                line = ThemeTokens.Mix(ThemeTokens.Mix(tier, t.NAgent, 0.85f), Color.black, 0.22f);
                outline = ThemeTokens.Mix(line, Color.black, 0.2f);
                fill = t.NAgent;
            }
            // A down node keeps the Bad token on its outline at rest, so it reads as down without any glow.
            if (Node.IsDown) outline = ThemeTokens.Mix(outline, t.Bad, 0.7f);
            _fillRest = t.Dark ? FillRest : FillRestLight;
            _fillLit = t.Dark ? FillLit : FillLitLight;
            Mat.SetColor(IdLine, line);
            Mat.SetColor(IdOutline, outline);
            Mat.SetColor(IdFillColor, fill);
            // Dark themes: the lit colour is the accent with a small lift. The tier hot colour is 60% white, which turned
            // every running globe into a milky grey ball.
            Mat.SetColor(IdHot, t.Dark && !Node.IsDown && !Node.IsMemory ? ThemeTokens.Mix(t.NAgent, Color.white, 0.2f) : hot);
            Mat.SetFloat(IdFill, _fillRest);
            Mat.SetFloat(IdLitFill, _fillLit);
            Mat.SetFloat(IdAlpha, _down ? 0.5f : 1f);
            _lastGlow = -1f;
        }

        public override Color BackdropFor(ThemeTokens t, bool lit)
        {
            // The centre is nearly clear: the stage, plus the fill wash (at its centre strength, 35% of the rim's)
            // and a little of the grid, which is dimmed there on purpose.
            Color hot = NodeStyleMath.HotOf(t, Node);
            Color rest = Color.Lerp(t.Stage, t.NAgent, _fillRest * 0.35f + 0.03f);
            return lit ? Color.Lerp(rest, hot, _fillLit * 0.35f + 0.03f) : rest;
        }

        public override void SetSelected(bool selected) => _selectedTarget = selected ? 1f : 0f;

        public override void Render(float glow, float dt)
        {
            _phase = NodeStyleMath.AdvancePhase(_phase, dt, PhaseBase, PhaseBoost, glow);
            _scan = NodeStyleMath.AdvancePhase(_scan, dt, ScanBase, ScanBoost, glow);
            // Keep the accumulated turns small so a long session never loses float precision in the shader.
            if (_scan > 64f) _scan -= 64f;
            if (_phase > 6283f) _phase -= 6283.185f;
            Mat.SetFloat(IdPhase, _phase);
            Mat.SetFloat(IdScan, _scan);

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
