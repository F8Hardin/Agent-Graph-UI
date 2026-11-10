using System.Text;
using TMPro;
using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// One node: a swappable body (see NodeVisual) with its name INSIDE it.
    ///
    /// Text inside a sphere, how it works: the body shaders have ZWrite Off, so the sphere never enters
    /// the depth buffer and the text cannot be clipped by it or z-fight with it. Both are transparent,
    /// so order is by distance to the camera; the label is nudged a few centimetres toward the camera
    /// every frame, which sorts it after its own sphere (drawn on top of it, legible through it) but
    /// still before any nearer node. The label is a world-space TextMeshPro that follows the camera
    /// with a damped slerp, so it trails a moving camera instead of snapping.
    /// </summary>
    internal sealed class NodeView
    {
        private static readonly int IdFace = Shader.PropertyToID("_FaceColor");

        private readonly Transform _label;
        private readonly TextMeshPro _text;
        private readonly Mesh _sphere;
        private readonly NodeStyleMaterials _mats;
        private NodeVisual _visual;
        private bool _selected;
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

        public NodeView(GraphNode node, float radius, Transform parent, Mesh sphere, NodeStyleMaterials mats, NodeStyleKind style, LabelFaces faces)
        {
            Node = node;
            Radius = radius;
            _faces = faces;
            _down = node.IsDown;
            _sphere = sphere;
            _mats = mats;

            GameObject root = new GameObject("Node " + node.Label);
            Root = root.transform;
            Root.SetParent(parent, false);

            // The collider lives on its own object, not on the body, so swapping the body's style never touches
            // picking. Unit sphere collider: the scale gives it the node radius.
            GameObject pick = new GameObject("Pick");
            pick.transform.SetParent(Root, false);
            pick.transform.localScale = Vector3.one * (radius * 2f);
            Collider = pick.AddComponent<SphereCollider>();
            Collider.radius = 0.5f;

            _visual = NodeStyleRegistry.Create(style, node, radius, Root, sphere, mats);

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

        /// <summary>Swap the body to another style. The label, collider and position are untouched.</summary>
        public void SetStyle(NodeStyleKind kind)
        {
            _visual.Destroy();
            _visual = NodeStyleRegistry.Create(kind, Node, Radius, Root, _sphere, _mats);
            _visual.SetSelected(_selected);
            if (_t != null) ApplyTheme(_t);
        }

        /// <summary>True while the node is dragged: the body shows a brighter rim.</summary>
        public void SetSelected(bool selected)
        {
            _selected = selected;
            _visual.SetSelected(selected);
        }

        public void ApplyTheme(ThemeTokens t)
        {
            _t = t;
            _visual.ApplyTheme(t);
            // Ink is whichever of fg/bg contrasts more with the body's EFFECTIVE backdrop at the label (what the
            // style says its centre looks like, blended over the stage), so one rule serves black and white terminals
            // and every style.
            _inkRest = BestInk(t, _visual.BackdropFor(t, false));
            _inkGlow = BestInk(t, _visual.BackdropFor(t, true));
            _lastGlow = -1f;
        }

        // WCAG's bar for body text. A label is small, so it gets the strict one.
        private const float InkContrast = 4.5f;

        /// <summary>The least of its ink a label keeps under fog (see Render).</summary>
        private const float LabelFogFloor = 0.8f;

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
                _ink = Color.Lerp(_inkRest, _inkGlow, Mathf.SmoothStep(0.08f, 0.4f, glow));
            }

            _visual.Track(camPos, sortOffset);
            _visual.Render(glow, dt);

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
                // Only part of the way: at the default framing the graph is ~65 units away and the raw factor leaves a
                // label at about a quarter of its ink, which is a dark smudge on every style (reviewed in play mode).
                // The body still fades fully, so depth still reads; the name stays legible. Rejected: no fog on text
                // at all, which floats far labels over bodies that have faded away. Set the floor to 0 for the old look.
                vis = LabelFogFloor + (1f - LabelFogFloor) * vis;
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
            _visual.Destroy();
            if (_textMat != null) Object.Destroy(_textMat);
            if (Root != null) Object.Destroy(Root.gameObject);
        }
    }
}
