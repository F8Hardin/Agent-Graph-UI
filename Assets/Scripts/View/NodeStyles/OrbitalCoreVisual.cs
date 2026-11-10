using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// Orbital Core: a dark planet with a hot core and a tilted ring carrying a satellite. The body is
    /// AgentNodeOrbital.shader, the ring is AgentNodeOrbitRing.shader on a second quad.
    ///
    /// One extra draw call per node is accepted for this style. The ring leaves the sphere's silhouette, and a shader
    /// on the sphere mesh cannot draw outside its own triangles (rejected: inflating the sphere mesh, which costs
    /// fill on every pixel of a mostly empty shell; rejected: a LineRenderer or mesh ring per node, which cannot do
    /// the satellite or the analytic back-half occlusion). Hologram Wire is the one-draw-call style for the headset.
    ///
    /// Sorting. Both shaders are ZWrite Off, like every style, and that is deliberate: the label sits a few
    /// centimetres inside the sphere's front surface and is sorted after the body, so a depth-writing body would clip
    /// the very text it is meant to carry (a concrete reason to refuse the "opaque body writes depth" shortcut that
    /// would otherwise sort the ring for free). So the ring is occluded analytically in its own shader (the half of the
    /// ring behind the body is hidden inside the body's silhouette), and sorted by distance: <see cref="Track"/>
    /// nudges the ring quad toward the camera by half the label's nudge, which puts it after its own body and
    /// before its own label without making it jump the queue ahead of other nodes (rejected: a later render queue
    /// for rings, which would draw a far node's ring over a nearer node's body).
    /// </summary>
    internal sealed class OrbitalCoreVisual : NodeVisual
    {
        private static readonly int IdBodyLit = Shader.PropertyToID("_BodyLit");
        private static readonly int IdBodyShade = Shader.PropertyToID("_BodyShade");
        private static readonly int IdOutline = Shader.PropertyToID("_OutlineColor");
        private static readonly int IdOutlineGain = Shader.PropertyToID("_OutlineGain");
        private static readonly int IdCore = Shader.PropertyToID("_CoreColor");
        private static readonly int IdCoreGain = Shader.PropertyToID("_CoreGain");
        private static readonly int IdCoreRest = Shader.PropertyToID("_CoreRest");
        private static readonly int IdCoreLit = Shader.PropertyToID("_CoreLit");
        private static readonly int IdGlow = Shader.PropertyToID("_Glow");
        private static readonly int IdSelected = Shader.PropertyToID("_Selected");
        private static readonly int IdAlpha = Shader.PropertyToID("_Alpha");
        private static readonly int IdPhase = Shader.PropertyToID("_Phase");
        private static readonly int IdRingNormal = Shader.PropertyToID("_RingNormal");
        private static readonly int IdRingColor = Shader.PropertyToID("_RingColor");
        private static readonly int IdRingGain = Shader.PropertyToID("_RingGain");
        private static readonly int IdSatColor = Shader.PropertyToID("_SatColor");
        private static readonly int IdSatGain = Shader.PropertyToID("_SatGain");
        private static readonly int IdSatPhase = Shader.PropertyToID("_SatPhase");
        private static readonly int IdSeed = Shader.PropertyToID("_Seed");

        // Ring quad half-extent in body radii. The ring itself is 1.38 radii (shader constant); the rest is margin for
        // the satellite's halo and the selected-state thickening.
        private const float QuadHalf = 1.75f;
        // Core breathing rate (radians per second) at rest, and what a fully lit node adds.
        private const float PhaseBase = 0.6f;
        private const float PhaseBoost = 1.4f;
        // The satellite travels ~1 radian per second at rest and up to ~5 when running: busy, never a blur.
        private const float SatBase = 1.0f;
        private const float SatBoost = 4.2f;
        private const float SelectK = 14f;

        // The core's alpha at the CENTRE, where the label is. Kept low so the backdrop under the text stays dark enough
        // for light ink on dark themes (and pale enough for dark ink on white ones) even fully lit.
        private const float CoreRestAlpha = 0.15f;
        private const float CoreLitAlpha = 0.27f;
        // The core is a gaussian: under the text it averages about half its peak alpha (the text spans roughly 0.3 body radii out).
        private const float LabelCoverage = 0.5f;

        private readonly GameObject _ring;
        private readonly Material _ringMat;
        private readonly Transform _ringT;
        private readonly bool _down;
        private readonly float _seed;
        private float _phase;
        private float _satPhase;
        private float _selected;
        private float _selectedTarget;
        private float _lastGlow = -1f;
        private float _lastSelected = -1f;
        private Color _bodyCentre = Color.black;
        private Color _coreCol = Color.white;
        private float _coreGain = 1f;

        private static Mesh _quad;

        public OrbitalCoreVisual(GraphNode node, float radius, Transform root, Mesh sphere, Material template, Material ringTemplate)
            : base(node, radius, root, sphere, template)
        {
            _down = node.IsDown;
            _seed = NodeStyleMath.SeedOf(node.Id);
            Mat.SetFloat(IdSeed, _seed);
            _phase = _seed * 0.37f;
            _satPhase = _seed * 1.9f;

            _ring = new GameObject("Ring");
            _ringT = _ring.transform;
            _ringT.SetParent(root, false);
            _ringT.localScale = Vector3.one * (radius * 2f * QuadHalf);
            _ring.AddComponent<MeshFilter>().sharedMesh = Quad();
            MeshRenderer mr = _ring.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _ringMat = new Material(ringTemplate) { name = ringTemplate.name + " " + node.Label };
            mr.sharedMaterial = _ringMat;
            Vector3 n = NodeStyleMath.RingNormal(_seed);
            _ringMat.SetVector(IdRingNormal, new Vector4(n.x, n.y, n.z, 0f));
            _ringMat.SetFloat(IdSeed, _seed);
        }

        /// <summary>One shared unit quad (rejected: Resources.GetBuiltinResource, which is an editor-only path for some built-ins).</summary>
        private static Mesh Quad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "OrbitRingQuad", hideFlags = HideFlags.HideAndDontSave };
            _quad.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            };
            _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _quad.bounds = new Bounds(Vector3.zero, new Vector3(1.5f, 1.5f, 0.2f));
            return _quad;
        }

        public override void ApplyTheme(ThemeTokens t)
        {
            Color tier = NodeStyleMath.BaseOf(t, Node.Tier);
            Color hot = NodeStyleMath.HotOf(t, Node);
            Color bodyLit, bodyShade, outline, ring, sat, core;
            float outlineGain, ringGain, satGain;
            if (t.Dark)
            {
                // A dark planet: close to the stage, lifted just enough on the lit side to read as a sphere. The accent
                // lives in the outline, ring and core, which are HDR so the scene's bloom turns them into a halo.
                bodyLit = ThemeTokens.Mix(t.Surface2, tier, 0.16f);
                bodyShade = ThemeTokens.Mix(t.Stage, tier, 0.03f);
                outline = ThemeTokens.Mix(tier, t.NAgent, 0.5f);
                ring = ThemeTokens.Mix(tier, t.NAgent, 0.5f);
                sat = ThemeTokens.Mix(hot, Color.white, 0.35f);
                core = ThemeTokens.Mix(t.NAgent, hot, 0.5f);
                outlineGain = 1.5f;
                ringGain = 1.25f;
                satGain = 3.0f;
                _coreGain = 2.4f;
            }
            else
            {
                // A pale planet over a grey stage. Additive light is invisible on white, so outline, ring, satellite and
                // core are alpha-blended darker tints, gains of 1.
                bodyLit = ThemeTokens.Mix(t.Bg, tier, 0.06f);
                bodyShade = ThemeTokens.Mix(ThemeTokens.Mix(t.Stage, tier, 0.30f), Color.black, 0.06f);
                // Outline and ring are the accent darkened well past the tier colour: they are 1 px lines that fog
                // pulls toward a grey stage, so a pale tint would be gone at the default distance.
                outline = ThemeTokens.Mix(ThemeTokens.Mix(tier, t.NAgent, 0.8f), Color.black, 0.42f);
                ring = outline;
                sat = ThemeTokens.Mix(hot, Color.black, 0.35f);
                core = ThemeTokens.Mix(t.NAgent, hot, 0.5f);
                outlineGain = 1f;
                ringGain = 1f;
                satGain = 1f;
                _coreGain = 1f;
            }
            // A down node: the outline and ring carry the Bad token even at rest, so "down" is readable without glow.
            if (Node.IsDown)
            {
                outline = ThemeTokens.Mix(outline, t.Bad, 0.65f);
                ring = ThemeTokens.Mix(ring, t.Bad, 0.65f);
            }
            _coreCol = core;
            Mat.SetColor(IdBodyLit, bodyLit);
            Mat.SetColor(IdBodyShade, bodyShade);
            Mat.SetColor(IdOutline, outline);
            Mat.SetFloat(IdOutlineGain, outlineGain);
            Mat.SetColor(IdCore, Node.IsDown || Node.IsMemory ? ThemeTokens.Mix(core, hot, 0.5f) : core);
            Mat.SetFloat(IdCoreGain, _coreGain);
            Mat.SetFloat(IdCoreRest, CoreRestAlpha);
            Mat.SetFloat(IdCoreLit, CoreLitAlpha);
            Mat.SetFloat(IdAlpha, _down ? 0.5f : 1f);
            _ringMat.SetColor(IdRingColor, ring);
            _ringMat.SetFloat(IdRingGain, ringGain);
            _ringMat.SetColor(IdSatColor, sat);
            _ringMat.SetFloat(IdSatGain, satGain);
            _ringMat.SetFloat(IdAlpha, _down ? 0.5f : 1f);
            _bodyCentre = ThemeTokens.Mix(bodyShade, bodyLit, 0.55f);
            _lastGlow = -1f;
        }

        public override Color BackdropFor(ThemeTokens t, bool lit)
        {
            // What the centre of the body really shows: the dark body with the core's soft glow over it. The core's
            // HDR gain is clamped by the display, so it is applied and clamped here too.
            Color hot = Node.IsDown || Node.IsMemory ? ThemeTokens.Mix(_coreCol, NodeStyleMath.HotOf(t, Node), 0.5f) : _coreCol;
            Color c = lit ? Color.Lerp(hot, NodeStyleMath.HotOf(t, Node), 0.4f) : hot;
            float a = (lit ? CoreLitAlpha : CoreRestAlpha) * LabelCoverage;
            float g = lit ? _coreGain : Mathf.Min(_coreGain, 1.2f);
            Color o = Color.Lerp(_bodyCentre,
                new Color(Mathf.Min(1f, c.r * g), Mathf.Min(1f, c.g * g), Mathf.Min(1f, c.b * g), 1f), a);
            o.a = 1f;
            return o;
        }

        public override void SetSelected(bool selected) => _selectedTarget = selected ? 1f : 0f;

        public override void Track(Vector3 camPos, float sortOffset)
        {
            Vector3 toCam = camPos - Root.position;
            float m = toCam.magnitude;
            _ringT.position = m > 1e-4f ? Root.position + toCam * (0.5f * sortOffset / m) : Root.position;
        }

        public override void Render(float glow, float dt)
        {
            _phase = NodeStyleMath.AdvancePhase(_phase, dt, PhaseBase, PhaseBoost, glow);
            _satPhase = NodeStyleMath.AdvancePhase(_satPhase, dt, SatBase, SatBoost, glow);
            Mat.SetFloat(IdPhase, _phase);
            _ringMat.SetFloat(IdSatPhase, _satPhase);

            if (!Mathf.Approximately(glow, _lastGlow))
            {
                _lastGlow = glow;
                Mat.SetFloat(IdGlow, glow);
                _ringMat.SetFloat(IdGlow, glow);
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
                _ringMat.SetFloat(IdSelected, _selected);
            }
        }

        public override void Destroy()
        {
            if (_ringMat != null) Object.Destroy(_ringMat);
            if (_ring != null) Object.Destroy(_ring);
            base.Destroy();
        }
    }
}
