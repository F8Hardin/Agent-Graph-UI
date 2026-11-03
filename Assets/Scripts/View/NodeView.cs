using System.Text;
using TMPro;
using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// One node: a translucent sphere with its name INSIDE it.
    ///
    /// Text inside a sphere, how it works: the sphere shader has ZWrite Off, so the sphere never enters
    /// the depth buffer and the text cannot be clipped by it or z-fight with it. Both are transparent,
    /// so order is by distance to the camera; the label is nudged a few centimetres toward the camera
    /// every frame, which sorts it after its own sphere (drawn on top of it, legible through it) but
    /// still before any nearer node. The label is a world-space TextMeshPro that follows the camera
    /// with a damped slerp, so it trails a moving camera instead of snapping.
    /// </summary>
    internal sealed class NodeView
    {
        private static readonly int IdBase = Shader.PropertyToID("_BaseColor");
        private static readonly int IdHot = Shader.PropertyToID("_HotColor");
        private static readonly int IdGlow = Shader.PropertyToID("_Glow");
        private static readonly int IdAlpha = Shader.PropertyToID("_Alpha");
        private static readonly int IdFill = Shader.PropertyToID("_Fill");
        private static readonly int IdFace = Shader.PropertyToID("_FaceColor");
        private static readonly int IdBorder = Shader.PropertyToID("_BorderColor");
        private static readonly int IdBorderHot = Shader.PropertyToID("_BorderHotColor");

        // How far the fill is mixed toward the page background to get the border colour. Mid-range of 45-60%.
        private const float BorderMix = 0.52f;
        // Light themes have a pale background, so mixing toward it would LIGHTEN the border. There the same
        // fraction is mixed toward black instead, so the border is a darker shade of the node on both.
        private const float BorderMixLight = 0.45f;

        private readonly Transform _label;
        private readonly TextMeshPro _text;
        private readonly Material _bodyMat;
        private readonly LabelFaces _faces;
        private Material _textMat;
        private bool _italic;
        private readonly int _wrapChars;
        private readonly bool _down;

        private ThemeTokens _t;
        private Quaternion _labelRot;
        private float _lastGlow = -1f;
        private Color _ink = Color.white;
        private Color _inkRest = Color.white;
        private Color _inkGlow = Color.black;
        private Color _lastFace = new Color(-1f, 0f, 0f, 0f);

        public readonly GraphNode Node;
        public readonly Transform Root;
        public readonly float Radius;

        /// <summary>Picking target for drags. Unit sphere collider on the body, so the body's scale gives it the node radius.</summary>
        public readonly SphereCollider Collider;

        /// <summary>Calls in flight on this tool node. A counter, not a flag: two concurrent calls of one tool share the node.</summary>
        public int Live;

        /// <summary>
        /// The glow is held while true, instead of decaying. Armed one hop after the call starts (so the glow does not
        /// pop before the pulse arrives) and released when the last call on the node returns, or when the run goes Idle.
        /// </summary>
        public bool Pinned;

        public NodeView(GraphNode node, float radius, Transform parent, Mesh sphere, Material template, LabelFaces faces)
        {
            Node = node;
            Radius = radius;
            _faces = faces;
            _down = node.IsDown;

            GameObject root = new GameObject("Node " + node.Label);
            Root = root.transform;
            Root.SetParent(parent, false);

            GameObject body = new GameObject("Sphere");
            body.transform.SetParent(Root, false);
            body.transform.localScale = Vector3.one * (radius * 2f);
            body.AddComponent<MeshFilter>().sharedMesh = sphere;
            MeshRenderer mr = body.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _bodyMat = new Material(template) { name = "NodeSphere " + node.Label };
            mr.sharedMaterial = _bodyMat;
            Collider = body.AddComponent<SphereCollider>();
            Collider.radius = 0.5f; // the unit sphere mesh has radius 0.5; the body's scale makes it `radius`

            GameObject lab = new GameObject("Label");
            _label = lab.transform;
            _label.SetParent(Root, false);
            _text = lab.AddComponent<TextMeshPro>();
            _label = _text.rectTransform; // adding TMP swaps the Transform for a RectTransform; the old reference is dead
            if (faces.Upright != null) _text.font = faces.Upright;
            _text.alignment = TextAlignmentOptions.Center;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.overflowMode = TextOverflowModes.Overflow;
            _text.fontStyle = faces.UprightStyle;
            _text.enableAutoSizing = true;
            _text.richText = false;
            _text.color = Color.white;
            _text.extraPadding = false;

            float w = radius * 1.5f;
            float h = radius * 1.0f;
            _text.rectTransform.sizeDelta = new Vector2(w, h);
            _text.fontSizeMin = 0.5f;
            _text.fontSizeMax = node.Tier == NodeTier.Agent ? 6f : node.Tier == NodeTier.Server ? 3.6f : 3.0f;
            _wrapChars = node.Tier == NodeTier.Agent ? 8 : node.Tier == NodeTier.Server ? 10 : 9;
            _text.text = Wrap(node.Label, _wrapChars);
            _text.margin = Vector4.zero;

            MeshRenderer tmr = lab.GetComponent<MeshRenderer>();
            if (tmr != null)
            {
                tmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                tmr.receiveShadows = false;
            }
            GrabTextMaterial();
            _labelRot = Quaternion.identity;
        }

        /// <summary>A per-label material instance, so the fog fade can tint each label. Redone whenever the face changes.</summary>
        private void GrabTextMaterial()
        {
            _textMat = _text.fontMaterial;
            // The agent's name always draws last, so a node that happens to sit between the camera and the
            // agent can never hide it. (Queue order beats distance sorting.)
            if (Node.Tier == NodeTier.Agent) _textMat.renderQueue = 3100;
            _lastFace = new Color(-1f, 0f, 0f, 0f); // force the next Render to push the colour into the new material
        }

        /// <summary>
        /// Set the label text. Italic is a real italic face swapped in whole (see ConsoleFonts.Labels), not a skew,
        /// and it is used for the "thinking" state so it reads as a state rather than a name.
        /// </summary>
        public void SetLabel(string label, bool italic = false)
        {
            if (italic != _italic)
            {
                _italic = italic;
                TMP_FontAsset face = italic && _faces.Italic != null ? _faces.Italic : _faces.Upright;
                if (face != null && _text.font != face)
                {
                    Material old = _textMat;
                    _text.font = face;
                    GrabTextMaterial();
                    if (old != null && old != _textMat) Object.Destroy(old);
                }
                _text.fontStyle = italic ? _faces.ItalicStyle : _faces.UprightStyle;
            }
            _text.text = Wrap(label, _wrapChars);
        }

        /// <summary>Break after '_' and '-' so long tool names wrap inside the sphere instead of spilling out.</summary>
        public static string Wrap(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
            StringBuilder sb = new StringBuilder(s.Length + 4);
            int lineLen = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                bool brk = s[i] == '_' || s[i] == '-' || i == s.Length - 1;
                if (!brk) continue;
                int len = i - start + 1;
                if (lineLen > 0 && lineLen + len > max)
                {
                    sb.Append('\n');
                    lineLen = 0;
                }
                sb.Append(s, start, len);
                lineLen += len;
                start = i + 1;
            }
            return sb.ToString();
        }

public void ApplyTheme(ThemeTokens t)
        {
            _t = t;
            // The BASE colour is the plain tier colour. Memory and error tints live in the HOT colour, which the
            // shader blends in by Glow, so they pulse in and decay with the glow instead of sticking to the node.
            // A node that is down stays recognisable by its lower alpha, dim label and down-coloured edges.
            Color b = Node.Tier == NodeTier.Agent ? t.NAgent
                : Node.Tier == NodeTier.Server ? t.NSrv
                : t.NTool;
            Color hot = Node.IsDown ? t.Bad : Node.IsMemory ? t.Mem : Node.Tier == NodeTier.Agent ? t.NHot : t.Flow;
            // Bright base colours (the agent) get a denser fill so dark ink can read on them, like the
            // prototype's solid pills; dim ones stay glassy with light ink. Ink is whichever of fg/bg contrasts
            // more with the sphere's EFFECTIVE backdrop (its fill blended over the page), so one rule serves
            // black and white terminals.
            bool bright = ThemeTokens.Lum(b) > 0.5f;
            float fill = bright ? 0.66f : 0.30f;
            // Over the stage, not the page: on a light theme the graph sits on a greyer ground than the panels do.
            Color backdrop = Color.Lerp(t.Stage, b, fill);
            Color litBackdrop = Color.Lerp(t.Stage, hot, 0.92f);
            _inkRest = BestInk(t, backdrop);
            _inkGlow = BestInk(t, litBackdrop);
            _bodyMat.SetColor(IdBase, b);
            _bodyMat.SetColor(IdHot, hot);
            _bodyMat.SetColor(IdBorder, BorderOf(t, b));
            _bodyMat.SetColor(IdBorderHot, BorderOf(t, hot));
            _bodyMat.SetFloat(IdFill, fill);
            _bodyMat.SetFloat(IdAlpha, _down ? 0.5f : 1f);
            _lastGlow = -1f;
        }

        /// <summary>A darker shade of the node's own colour: the fill mixed toward the background (dark themes) or toward black (light themes).</summary>
        private static Color BorderOf(ThemeTokens t, Color fill)
        {
            Color c = t.Dark ? Color.Lerp(fill, t.Bg, BorderMix) : Color.Lerp(fill, Color.black, BorderMixLight);
            c.a = 1f;
            return c;
        }

        // WCAG's bar for body text. A label is small, so it gets the strict one.
        private const float InkContrast = 4.5f;

        /// <summary>
        /// Whichever of font/background reads better on the backdrop, pushed toward black or white only as far as it
        /// takes to reach <see cref="InkContrast"/>. On a mid-tone backdrop (a light theme's translucent sphere over a
        /// grey stage) neither theme colour reads well, and the old pick-the-better-of-two left pale ink on grey.
        /// Dark themes already clear the bar with their font colour, so nothing changes there.
        /// </summary>
        private static Color BestInk(ThemeTokens t, Color backdrop)
        {
            float yb = ThemeTokens.RelativeLuminance(backdrop);
            Color pick = ThemeTokens.Contrast(ThemeTokens.RelativeLuminance(t.Fg), yb) >= ThemeTokens.Contrast(ThemeTokens.RelativeLuminance(t.Bg), yb)
                ? t.Fg : t.Bg;
            // 0.179 is where black and white contrast equally with a backdrop.
            Color extreme = yb > 0.179f ? Color.black : Color.white;
            return ThemeTokens.MixForContrast(pick, extreme, backdrop, InkContrast);
        }

        public void Render(
            float glow, float dt, Quaternion camRot, Vector3 camPos, float followK, float sortOffset,
            bool fogOn, float fogDensity, Color fogColor)
        {
            if (_t == null) return;

            if (!Mathf.Approximately(glow, _lastGlow))
            {
                _lastGlow = glow;
                _bodyMat.SetFloat(IdGlow, glow);
                _ink = Color.Lerp(_inkRest, _inkGlow, Mathf.SmoothStep(0.08f, 0.4f, glow));
            }

            // Lazy, damped billboard: frame-rate independent, and visibly trails a moving camera.
            float a = 1f - Mathf.Exp(-followK * dt);
            _labelRot = Quaternion.Slerp(_labelRot, camRot, a);
            _label.rotation = _labelRot;
            _label.position = Root.position + _labelRot * (Vector3.back * sortOffset);

            // TMP's shader has no fog, so apply the same exponential factor to the text colour by hand.
            float vis = 1f;
            if (fogOn)
            {
                vis = Mathf.Exp(-fogDensity * Vector3.Distance(Root.position, camPos));
            }
            Color face = Color.Lerp(fogColor, _ink, vis);
            face.a = _down ? 0.55f : 1f;
            if (Mathf.Abs(face.r - _lastFace.r) + Mathf.Abs(face.g - _lastFace.g)
                + Mathf.Abs(face.b - _lastFace.b) + Mathf.Abs(face.a - _lastFace.a) > 0.004f)
            {
                _lastFace = face;
                _textMat.SetColor(IdFace, face);
            }
        }

        /// <summary>Snap the label to face the camera now (first frame), so it does not sweep in from identity.</summary>
        public void SnapLabel(Quaternion camRot)
        {
            _labelRot = camRot;
        }

        public void Destroy()
        {
            if (_bodyMat != null) Object.Destroy(_bodyMat);
            if (_textMat != null) Object.Destroy(_textMat);
            if (Root != null) Object.Destroy(Root.gameObject);
        }
    }
}
