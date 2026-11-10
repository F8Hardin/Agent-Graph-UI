using TMPro;
using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// One node of the session memory tree: a small sphere in the CURRENT node style (so the tree restyles with Plasma /
    /// Orbital / Hologram) and a label under it.
    ///
    /// The label sits BELOW the sphere, not inside it as NodeView does. A memory sphere is 0.45 to 0.7 in radius and a
    /// file name is often 15+ characters; inside it the text would be a few pixels high at the default camera distance.
    /// The billboard follow, the damped turn and the "nudge toward the camera so it sorts after its own body" are NodeView's.
    /// Rejected: reusing NodeView itself, which also builds a picking collider (that would swallow the camera-orbit
    /// raycast in GraphView.BeginDrag) and sizes its text to the sphere.
    ///
    /// State reads at a glance without a new shader: HIT is the plain, dim body; READ lights it to a steady Mem-coloured
    /// glow; WRITTEN swaps in a body whose hot colour is the Flow colour (a node that is not "memory" lights with Flow)
    /// and glows brighter. Glow is the same 0..1 the styles already take. A touch adds a pulse on top that decays.
    /// </summary>
    internal sealed class MemoryNodeView
    {
        public const float RootRadius = 0.7f;
        public const float FolderRadius = 0.45f;
        public const float NoteRadius = 0.5f;

        // Resting glow by state (the glow a style takes is 0..1; 1 is the full "a tool is running" look).
        private const float ReadGlow = 0.5f;
        private const float WrittenGlow = 0.85f;
        private const float LiveGlow = 0.95f;
        private const float RootGlow = 0.2f;

        // Same decay as the graph's glow: 0.945 per 60 fps frame, folded into a frame-rate-independent exponent.
        private static readonly float PulseK = 60f * Mathf.Log(0.945f);

        private const float LabelFontSize = 3.3f;
        private const float LabelDrop = 0.32f;
        private const float SortOffset = 0.05f;
        private const int MaxLabelChars = 18;

        private readonly Mesh _sphere;
        private readonly NodeStyleMaterials _mats;
        private readonly GraphNode _faceMem;
        private readonly GraphNode _faceFlow;
        private readonly TextMeshPro _text;
        private readonly Transform _label;
        private NodeVisual _visual;
        private NodeStyleKind _style;
        private bool _flowBody;
        private ThemeTokens _t;
        private Quaternion _labelRot;
        private Color _inkFull = Color.white;
        private Color _inkHit = Color.gray;
        private Color _inkWritten = Color.white;
        private Color _lastInk = new Color(-1f, 0f, 0f, 0f);

        public readonly MemoryNode Node;
        public readonly Transform Root;
        public readonly float Radius;

        /// <summary>Where the layout wants this node, relative to the anchor (the server node the root hangs on).</summary>
        public Vector3 Offset;

        /// <summary>Where the node is now, in world space. Eased toward anchor + Offset.</summary>
        public Vector3 Pos;

        /// <summary>Seconds since the node appeared. Drives the scale-in and the edge growing out of the parent.</summary>
        public float Age;

        /// <summary>0..1 growth, eased out. Written by the tree view each frame so the edge can read it.</summary>
        public float Grow;

        /// <summary>A decaying extra glow from a touch. The tree view also reads it as the heat of the edge into this node.</summary>
        public float Pulse;

        /// <summary>Memory calls running on this note right now (read, write, append name their file up front).</summary>
        public int Live;

        public MemoryNodeView(
            MemoryNode node, Transform parent, Mesh sphere, NodeStyleMaterials mats, NodeStyleKind style,
            LabelFaces faces, ThemeTokens tokens, Vector3 spawn, string server)
        {
            Node = node;
            _sphere = sphere;
            _mats = mats;
            _style = style;
            Radius = node.Kind == MemoryNodeKind.Root ? RootRadius : node.Kind == MemoryNodeKind.Folder ? FolderRadius : NoteRadius;
            Pos = spawn;

            // Structure (root, folders) uses the Server tier's brighter body; notes the Tool tier's dimmer one, so a note
            // that was only a search hit sits visibly below the folders it hangs from.
            NodeTier tier = node.Kind == MemoryNodeKind.Note ? NodeTier.Tool : NodeTier.Server;
            string id = "mem:" + node.Path;
            _faceMem = GraphNode.Detached(id, node.Label, tier, server, true);
            _faceFlow = GraphNode.Detached(id, node.Label, tier, server, false);

            GameObject root = new GameObject("Memory " + node.Label);
            Root = root.transform;
            Root.SetParent(parent, false);
            Root.position = spawn;
            Root.localScale = Vector3.zero;

            _visual = NodeStyleRegistry.Create(style, _faceMem, Radius, Root, sphere, mats);

            GameObject lab = new GameObject("Label");
            lab.transform.SetParent(Root, false);
            _text = lab.AddComponent<TextMeshPro>();
            _label = _text.rectTransform; // adding TMP swaps the Transform for a RectTransform; the old reference is dead
            if (faces.Upright != null) _text.font = faces.Upright;
            _text.alignment = TextAlignmentOptions.Center;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.overflowMode = TextOverflowModes.Overflow;
            _text.fontStyle = faces.UprightStyle;
            _text.enableAutoSizing = false;
            _text.fontSize = node.Kind == MemoryNodeKind.Root ? LabelFontSize * 1.15f : LabelFontSize;
            _text.richText = false;
            _text.extraPadding = false;
            _text.rectTransform.sizeDelta = new Vector2(8f, 1f);
            _text.text = Shorten(node.Label);
            MeshRenderer tmr = lab.GetComponent<MeshRenderer>();
            if (tmr != null)
            {
                tmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                tmr.receiveShadows = false;
            }
            _labelRot = Quaternion.identity;

            SetState(node.State);
            ApplyTheme(tokens);
        }

        /// <summary>Cut a long file name for the label; the full path is in the tree, not on screen.</summary>
        public static string Shorten(string label)
        {
            if (string.IsNullOrEmpty(label) || label.Length <= MaxLabelChars) return label ?? string.Empty;
            return label.Substring(0, MaxLabelChars - 2) + "..";
        }

        public void SnapLabel(Quaternion camRot) => _labelRot = camRot;

        /// <summary>
        /// Take the note's access state. Written swaps the body to the Flow-coloured one; any other state uses the Mem body.
        /// The swap is a style-visual rebuild, so it happens only on a tree event (an upgrade), never per frame.
        /// </summary>
        public void SetState(MemoryAccessKind state)
        {
            if (Node.Kind != MemoryNodeKind.Note) return;
            bool flow = state == MemoryAccessKind.Written;
            if (flow == _flowBody) return;
            _flowBody = flow;
            RebuildBody();
        }

        public void SetStyle(NodeStyleKind kind)
        {
            if (kind == _style) return;
            _style = kind;
            RebuildBody();
        }

        private void RebuildBody()
        {
            _visual.Destroy();
            _visual = NodeStyleRegistry.Create(_style, _flowBody ? _faceFlow : _faceMem, Radius, Root, _sphere, _mats);
            if (_t != null) _visual.ApplyTheme(_t);
        }

        public void ApplyTheme(ThemeTokens t)
        {
            _t = t;
            _visual.ApplyTheme(t);
            // Ink is solved against the stage, the colour the graph is drawn over: the smallest mix from the stage toward
            // the font colour that reaches the contrast. The same rule on a black and a white terminal.
            _inkFull = ThemeTokens.MixForContrast(t.Stage, t.Fg, 7f);
            _inkHit = ThemeTokens.MixForContrast(t.Stage, t.Fg, 3f);
            _inkWritten = ThemeTokens.MixForContrast(t.Stage, t.Flow, 7f);
            _lastInk = new Color(-1f, 0f, 0f, 0f);
        }

        private float Floor()
        {
            if (Node.Kind == MemoryNodeKind.Root) return RootGlow;
            if (Node.Kind != MemoryNodeKind.Note) return 0f;
            return Node.State == MemoryAccessKind.Written ? WrittenGlow : Node.State == MemoryAccessKind.Read ? ReadGlow : 0f;
        }

        /// <summary>Every frame. Allocation-free.</summary>
        public void Render(float dt, Quaternion camRot, Vector3 camPos, float followK)
        {
            if (_t == null) return;
            if (Pulse > 0f)
            {
                Pulse *= Mathf.Exp(PulseK * dt);
                if (Pulse < 0.002f) Pulse = 0f;
            }
            float glow = Floor();
            if (Pulse > glow) glow = Pulse;
            if (Live > 0 && glow < LiveGlow) glow = LiveGlow;

            Root.localScale = Grow >= 1f ? Vector3.one : Vector3.one * Grow;
            _visual.Track(camPos, SortOffset);
            _visual.Render(glow, dt);

            float a = 1f - Mathf.Exp(-followK * dt);
            _labelRot = Quaternion.Slerp(_labelRot, camRot, a);
            _label.rotation = _labelRot;
            // Camera-space down, so the name is under the sphere on screen however the camera is turned.
            _label.position = Root.position + _labelRot * (Vector3.down * (Radius * Grow + LabelDrop) + Vector3.back * SortOffset);

            // Folders and read/written notes always show a full-strength name; a note that was only a search hit is dimmer.
            Color ink = Node.Kind != MemoryNodeKind.Note ? _inkFull
                : Node.State == MemoryAccessKind.Written ? _inkWritten
                : Node.State == MemoryAccessKind.Read ? _inkFull
                : _inkHit;
            ink.a = Grow;
            if (Mathf.Abs(ink.r - _lastInk.r) + Mathf.Abs(ink.g - _lastInk.g) + Mathf.Abs(ink.b - _lastInk.b) + Mathf.Abs(ink.a - _lastInk.a) > 0.004f)
            {
                _lastInk = ink;
                _text.color = ink;
            }
        }

        public void Destroy()
        {
            _visual.Destroy();
            if (Root != null) Object.Destroy(Root.gameObject);
        }
    }
}
