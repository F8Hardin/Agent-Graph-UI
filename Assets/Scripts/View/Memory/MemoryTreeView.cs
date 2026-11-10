using System.Collections.Generic;
using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// Draws the session memory tree in the 3D graph: the root hangs off the MCP server that owns the memory tools, and
    /// the tree grows as EVA's memory calls succeed. A reader of <see cref="MemoryTree"/> only; it parses nothing.
    ///
    /// Layout is deterministic and local, not physics. <see cref="MemoryTreeLayout"/> gives each node a level and a
    /// sideways slot; this class maps them onto two world axes chosen ONCE per session: "out" is away from the agent through
    /// the server (leaned toward the screen plane so the tree faces the camera), "side" is perpendicular to it and to the
    /// view. Offsets from the server are recomputed only when the tree changes, and each node is eased toward
    /// (server position + offset) every frame, which is also what makes the whole tree follow the server when it is dragged.
    /// Rejected: adding these nodes to the force layout. The user wants placed things to stay put, and a session tree that
    /// jostled the main graph on every read would break that.
    ///
    /// Per frame it allocates nothing: the loops run over preallocated lists and arrays, the edges go through a second
    /// <see cref="EdgeMesh"/> (one draw call, the same material look as the main graph's), and every rebuild is on a tree event.
    /// </summary>
    internal sealed class MemoryTreeView : MonoBehaviour
    {
        // World units. Slots are wide because the label sits under the node and a file name is wider than its sphere.
        private const float RootGap = 1.4f;
        private const float LevelStep = 3.2f;
        private const float SlotSpacing = 2.9f;
        private const float GrowSeconds = 0.4f;
        private const float FollowK = 6f;
        private const float LabelFollowK = 2.5f;
        // How much of the "out" direction's depth component is removed, so the tree lies nearer the screen plane.
        private const float FaceScreen = 0.6f;

        private readonly List<MemoryNodeView> _views = new List<MemoryNodeView>();
        private readonly MemoryTreeLayout _layout = new MemoryTreeLayout();

        private GraphView _graph;
        private MemoryTracker _tracker;
        private MemoryTree _tree;
        private ThemeController _theme;
        private ThemeTokens _tokens;
        private EdgeMesh _edges;
        private NodeStyleKind _style;
        private float[] _lit = new float[0];
        private int _anchor;
        private string _serverId;
        private bool _frame;
        private Vector3 _out = Vector3.right;
        private Vector3 _side = Vector3.up;
        private bool _geomDirty;
        private bool _edgesLit;

        public int NodeCount => _views.Count;

        /// <summary>Create the view as a child of the graph and bind it to the tracker. The composition root calls this through ConsoleView.</summary>
        public static MemoryTreeView Attach(GraphView graph, MemoryTracker tracker)
        {
            GameObject go = new GameObject("Memory Tree");
            go.transform.SetParent(graph.transform, false);
            MemoryTreeView v = go.AddComponent<MemoryTreeView>();
            v.Bind(graph, tracker);
            return v;
        }

        private void Bind(GraphView graph, MemoryTracker tracker)
        {
            _graph = graph;
            _tracker = tracker;
            _tree = tracker.Tree;
            _theme = graph.Theme != null ? graph.Theme : FindAnyObjectByType<ThemeController>();
            _tokens = _theme != null ? _theme.Tokens : ThemeTokens.Compute(ThemePresets.BaseOf(ThemePresets.Find(null)));
            _style = ConsoleSettings.NodeStyle;
            _edges = new EdgeMesh(transform, graph.EdgeMaterialInstance);
            EdgeTheme.Apply(_edges.Material, _tokens);

            _tree.NodeAdded += OnNodeAdded;
            _tree.NodeTouched += OnNodeTouched;
            _tree.Cleared += OnCleared;
            _tracker.ServerChanged += OnServerChanged;
            _tracker.LiveChanged += OnLiveChanged;
            if (_theme != null) _theme.Changed += OnTheme;
            ConsoleSettings.ViewChanged += OnViewSettings;

            if (_tracker.Server != null) _serverId = GraphModel.ServerId(_tracker.Server);
            // Bound late: pick up whatever the tree already holds.
            IReadOnlyList<MemoryNode> existing = _tree.Nodes;
            for (int i = 0; i < existing.Count; i++) OnNodeAdded(existing[i]);
            ApplyVisibility();
        }

        private void OnDestroy()
        {
            if (_tree != null)
            {
                _tree.NodeAdded -= OnNodeAdded;
                _tree.NodeTouched -= OnNodeTouched;
                _tree.Cleared -= OnCleared;
            }
            if (_tracker != null)
            {
                _tracker.ServerChanged -= OnServerChanged;
                _tracker.LiveChanged -= OnLiveChanged;
            }
            if (_theme != null) _theme.Changed -= OnTheme;
            ConsoleSettings.ViewChanged -= OnViewSettings;
            for (int i = 0; i < _views.Count; i++) _views[i].Destroy();
            _views.Clear();
            _edges?.Destroy();
        }

        // ------------------------------------------------------------------ tree events (discrete)

        private void OnNodeAdded(MemoryNode n)
        {
            ResolveAnchor();
            Vector3 spawn = n.ParentIndex >= 0 && n.ParentIndex < _views.Count ? _views[n.ParentIndex].Pos : AnchorWorld();
            MemoryNodeView v = new MemoryNodeView(
                n, transform, _graph.SphereMesh, _graph.StyleMaterials, _style, ConsoleFonts.Labels, _tokens, spawn,
                _tracker.Server);
            Camera cam = _graph.ViewCamera;
            if (cam != null) v.SnapLabel(cam.transform.rotation);
            _views.Add(v);
            if (_lit.Length < _views.Count) System.Array.Resize(ref _lit, System.Math.Max(16, _views.Count * 2));
            _edges.SetCount(_views.Count);
            _edges.SetMemory(n.Index, true);
            RebuildLayout();
            _geomDirty = true;
        }

        private void OnNodeTouched(MemoryNode n)
        {
            if (n.Index >= _views.Count) return;
            MemoryNodeView v = _views[n.Index];
            v.SetState(n.State);
            float amt = n.State == MemoryAccessKind.Written ? 1f : n.State == MemoryAccessKind.Read ? 0.8f : 0.5f;
            bool written = n.State == MemoryAccessKind.Written;
            // Light the whole route from the root to this note: each ancestor gets a softer pulse, and a bead runs down each edge.
            int k = n.Index;
            float share = amt;
            while (k >= 0 && k < _views.Count)
            {
                MemoryNodeView step = _views[k];
                step.Pulse = Mathf.Min(1f, step.Pulse + share);
                _edges.SetMemory(k, !written);
                _edges.Emit(k, false);
                share *= 0.6f;
                k = step.Node.ParentIndex;
            }
        }

        private void OnCleared()
        {
            for (int i = 0; i < _views.Count; i++) _views[i].Destroy();
            _views.Clear();
            _edges.SetCount(0);
            _edges.CommitGeometry();
            _frame = false;
            _edgesLit = false;
        }

        private void OnServerChanged()
        {
            _serverId = _tracker.Server != null ? GraphModel.ServerId(_tracker.Server) : null;
            ResolveAnchor();
            if (_views.Count > 0) RebuildLayout();
        }

        private void OnLiveChanged(string path, bool on)
        {
            if (!_tree.TryFindNote(path, out MemoryNode note) || note.Index >= _views.Count) return;
            MemoryNodeView v = _views[note.Index];
            if (on) v.Live++;
            else if (v.Live > 0) v.Live--;
        }

        private void OnTheme(ThemeTokens t)
        {
            _tokens = t;
            for (int i = 0; i < _views.Count; i++) _views[i].ApplyTheme(t);
            EdgeTheme.Apply(_edges.Material, t);
        }

        private void OnViewSettings()
        {
            if (ConsoleSettings.NodeStyle != _style)
            {
                _style = ConsoleSettings.NodeStyle;
                for (int i = 0; i < _views.Count; i++) _views[i].SetStyle(_style);
            }
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            if (gameObject.activeSelf != ConsoleSettings.ShowMemoryTree) gameObject.SetActive(ConsoleSettings.ShowMemoryTree);
        }

        // ------------------------------------------------------------------ anchor and layout

        /// <summary>The server node that owns the memory tools, or the agent while that server is not in the graph (yet).</summary>
        private void ResolveAnchor()
        {
            int idx = 0;
            if (_serverId != null && _graph.TryGetNodeIndex(_serverId, out int s)) idx = s;
            _anchor = idx;
        }

        private Vector3 AnchorWorld() => _graph.NodeWorldPosition(_anchor);

        /// <summary>
        /// Pick the two axes once per session, when the first node appears, and keep them. Re-picking on every change
        /// would turn the whole tree whenever the camera had moved between two reads.
        /// </summary>
        private void EnsureFrame()
        {
            if (_frame) return;
            Camera cam = _graph.ViewCamera;
            Vector3 f = cam != null ? cam.transform.forward : Vector3.forward;
            Vector3 right = cam != null ? cam.transform.right : Vector3.right;
            Vector3 up = cam != null ? cam.transform.up : Vector3.up;

            Vector3 d = AnchorWorld() - _graph.NodeWorldPosition(0);
            if (d.sqrMagnitude < 0.01f) d = right; // hanging off the agent itself: fan out sideways
            d.Normalize();
            d -= f * (Vector3.Dot(d, f) * FaceScreen);
            if (d.sqrMagnitude < 0.01f) d = right;
            _out = d.normalized;

            Vector3 w = Vector3.Cross(_out, f);
            if (w.sqrMagnitude < 1e-4f) w = up;
            w.Normalize();
            if (Vector3.Dot(w, up + right * 0.3f) < 0f) w = -w;
            _side = w;
            _frame = true;
        }

        private void RebuildLayout()
        {
            EnsureFrame();
            _layout.Compute(_tree);
            float rootDist = _graph.NodeRadius(_anchor) + MemoryNodeView.RootRadius + RootGap;
            for (int i = 0; i < _views.Count; i++)
            {
                _views[i].Offset = _out * (rootDist + _layout.DepthOf(i) * LevelStep) + _side * (_layout.LateralOf(i) * SlotSpacing);
            }
        }

        // ------------------------------------------------------------------ 60 fps layer

        private void Update()
        {
            int n = _views.Count;
            if (n == 0) return;
            Camera cam = _graph.ViewCamera;
            if (cam == null) return;
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            if (_anchor == 0 && _serverId != null)
            {
                ResolveAnchor();
                if (_anchor != 0) RebuildLayout(); // the server node arrived after the tree did
            }

            Quaternion camRot = cam.transform.rotation;
            Vector3 camPos = cam.transform.position;
            Vector3 anchor = AnchorWorld();
            float follow = 1f - Mathf.Exp(-FollowK * dt);
            bool moving = _geomDirty;

            for (int i = 0; i < n; i++) _lit[i] = 0f;
            for (int i = 0; i < n; i++)
            {
                if (_views[i].Live <= 0) continue;
                int k = i;
                while (k >= 0 && _lit[k] < 1f)
                {
                    _lit[k] = 1f;
                    k = _views[k].Node.ParentIndex;
                }
            }

            for (int i = 0; i < n; i++)
            {
                MemoryNodeView v = _views[i];
                v.Age += dt;
                float t = Mathf.Clamp01(v.Age / GrowSeconds);
                float inv = 1f - t;
                v.Grow = 1f - inv * inv * inv; // ease out

                Vector3 delta = anchor + v.Offset - v.Pos;
                if (delta.sqrMagnitude > 1e-6f)
                {
                    v.Pos += delta * follow;
                    moving = true;
                }
                if (t < 1f) moving = true;
                v.Root.position = v.Pos;
                v.Render(dt, camRot, camPos, LabelFollowK);
            }

            bool anyHot = false;
            float anchorRadius = _graph.NodeRadius(_anchor);
            for (int i = 0; i < n; i++)
            {
                MemoryNodeView v = _views[i];
                Vector3 pa;
                float ra;
                int p = v.Node.ParentIndex;
                if (p >= 0)
                {
                    pa = _views[p].Pos;
                    ra = _views[p].Radius * _views[p].Grow;
                }
                else
                {
                    pa = anchor;
                    ra = anchorRadius;
                }
                Vector3 dir = v.Pos - pa;
                float len = dir.magnitude;
                dir = len > 1e-4f ? dir / len : Vector3.up;
                // Edges start and end on the sphere surfaces, like the main graph's, so no line runs behind a label.
                Vector3 sa = pa + dir * (ra * 0.96f);
                Vector3 sb = v.Pos - dir * (v.Radius * v.Grow * 0.96f);
                _edges.SetEdge(i, transform.InverseTransformPoint(sa), transform.InverseTransformPoint(sb), p >= 0 ? 2 : 1, false);
                float hot = Mathf.Max(_lit[i], v.Pulse * 0.8f);
                _edges.SetHot(i, hot);
                if (hot > 0f) anyHot = true;
            }
            if (moving)
            {
                _edges.CommitGeometry();
                _geomDirty = false;
            }
            bool pulsing = _edges.Advance(dt);
            if (anyHot || pulsing || _edgesLit) _edges.UploadPulses();
            _edgesLit = anyHot || pulsing;
        }
    }
}
