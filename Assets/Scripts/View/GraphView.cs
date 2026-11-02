using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The 3D graph: spheres, edges, layout and the two-hop pulse choreography. It reads Domain only.
    ///
    /// Two layers, kept apart on purpose:
    ///  - discrete: the reducer's events (NodeFired, NodeResolved, PhaseChanged) arrive a few times a second;
    ///  - 60 fps: glow, link heat, pulses, label billboards and layout ticks all run in Update() on mutable
    ///    floats (GraphNode.Glow, GraphLink.Hot). Nothing in Update() goes through the reducer or allocates.
    /// </summary>
    public sealed class GraphView : MonoBehaviour
    {
        private enum Op : byte { Pulse, Heat, Packet, Focus, Fit }

        private struct Scheduled
        {
            public bool Used;
            public float At;
            public Op Op;
            public int A;
            public float Amt;
            public bool Flag;
        }

        // Prototype decay per 60 fps frame, folded into frame-rate-independent exponents:
        // glow *= 0.945 (0.996 for the agent while a run is live); link heat *= 0.94.
        private static readonly float KDecay = 60f * Mathf.Log(0.945f);
        private static readonly float KHold = 60f * Mathf.Log(0.996f);
        private static readonly float KHot = 60f * Mathf.Log(0.94f);

        [Header("Assets")]
        [SerializeField] private Material sphereMaterial;
        [SerializeField] private Material edgeMaterial;
        [SerializeField] private TMP_FontAsset labelFont;
        [SerializeField] private ThemeController theme;
        [SerializeField] private Camera viewCamera;

        [Header("Sphere radius by tier")]
        [SerializeField] private float agentRadius = 1.8f;
        [SerializeField] private float serverRadius = 1.05f;
        [SerializeField] private float toolRadius = 0.8f;

        [Header("Labels")]
        [Tooltip("Damping of the label's turn toward the camera. Small = weighty and lazy.")]
        [SerializeField, Min(0.1f)] private float labelFollowK = 2.5f;
        [SerializeField] private float labelSortOffset = 0.05f;

        [Header("Layout")]
        [SerializeField] private LayoutSettings layout = new LayoutSettings();
        [SerializeField] private int warmStartTicks = 100;

        [Header("Pulses")]
        [SerializeField] private float serverHopDelay = 0.17f;
        [SerializeField] private float toolHopDelay = 0.34f;
        [SerializeField] private float returnHopDelay = 0.19f;
        [SerializeField] private float packetSeconds = 0.5f;

        private readonly List<NodeView> _views = new List<NodeView>();
        private readonly Dictionary<string, int> _nodeIndex = new Dictionary<string, int>();
        private readonly List<GraphLink> _links = new List<GraphLink>();
        private readonly Dictionary<GraphLink, int> _linkIndex = new Dictionary<GraphLink, int>();
        private readonly List<int> _linkSrc = new List<int>();
        private readonly List<int> _linkDst = new List<int>();
        private readonly Scheduled[] _sched = new Scheduled[128];

        private ConsoleReducer _reducer;
        private GraphModel _graph;
        private ForceLayout3D _layout;
        private EdgeMesh _edges;
        private Mesh _sphereMesh;
        private ThemeTokens _tokens;
        private Phase _phase;
        private float _acc;
        private float _boundRadius = 10f;
        private bool _edgesLit;
        private bool _firstFrame = true;

        /// <summary>(agent, node) in graph-local space plus the node radius. The camera rig frames the path between them.</summary>
        public event Action<Vector3, Vector3, float> PathFocused;
        public event Action FitRequested;
        public event Action LayoutSettled;

        public float BoundingRadius => _boundRadius;
        public int NodeCount => _views.Count;
        public bool IsLayoutSettled => _layout != null && _layout.IsSettled;
        public string AgentLabel => _graph != null ? _graph.AgentLabel : "EVA";

        private void Awake()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            if (theme == null) theme = FindAnyObjectByType<ThemeController>();
            if (labelFont == null) labelFont = TMP_Settings.defaultFontAsset;

            // One shared unit sphere mesh.
            GameObject prim = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = prim.GetComponent<MeshFilter>().sharedMesh;
            Destroy(prim);

            _layout = new ForceLayout3D(layout);
            _edges = new EdgeMesh(transform, edgeMaterial) { PacketSeconds = packetSeconds };
        }

        private void OnEnable()
        {
            if (theme != null) theme.Changed += OnThemeChanged;
        }

        private void OnDisable()
        {
            if (theme != null) theme.Changed -= OnThemeChanged;
        }

        private void OnDestroy()
        {
            Unbind();
            for (int i = 0; i < _views.Count; i++) _views[i].Destroy();
            _views.Clear();
            _edges?.Destroy();
        }

        // ------------------------------------------------------------------ binding

        public void Bind(ConsoleReducer reducer)
        {
            Unbind();
            _reducer = reducer;
            _graph = reducer.Graph;
            _reducer.NodeFired += OnNodeFired;
            _reducer.NodeResolved += OnNodeResolved;
            _reducer.PhaseChanged += OnPhaseChanged;
            _reducer.TranscriptChanged += OnTranscriptChanged;
            _graph.NodeAdded += OnNodeAdded;
            _graph.LinkAdded += OnLinkAdded;
            _graph.Rebuilt += BuildAll;
            _phase = reducer.State.Phase;
            _tokens = theme != null ? theme.Tokens : ThemeTokens.Compute(ThemePresets.BaseOf(ThemePresets.Find(null)));
            BuildAll();
        }

        private void Unbind()
        {
            if (_reducer != null)
            {
                _reducer.NodeFired -= OnNodeFired;
                _reducer.NodeResolved -= OnNodeResolved;
                _reducer.PhaseChanged -= OnPhaseChanged;
                _reducer.TranscriptChanged -= OnTranscriptChanged;
            }
            if (_graph != null)
            {
                _graph.NodeAdded -= OnNodeAdded;
                _graph.LinkAdded -= OnLinkAdded;
                _graph.Rebuilt -= BuildAll;
            }
            _reducer = null;
            _graph = null;
        }

        private void OnThemeChanged(ThemeTokens t)
        {
            _tokens = t;
            ApplyThemeToAll();
        }

        // ------------------------------------------------------------------ building

        private float RadiusOf(NodeTier tier) =>
            tier == NodeTier.Agent ? agentRadius : tier == NodeTier.Server ? serverRadius : toolRadius;

        private void BuildAll()
        {
            for (int i = 0; i < _views.Count; i++) _views[i].Destroy();
            _views.Clear();
            _nodeIndex.Clear();
            _links.Clear();
            _linkIndex.Clear();
            _linkSrc.Clear();
            _linkDst.Clear();
            _layout.Clear();
            for (int i = 0; i < _sched.Length; i++) _sched[i].Used = false;

            IReadOnlyList<GraphNode> nodes = _graph.Nodes;
            Vector3[] seeds = ComputeSeeds(nodes);
            for (int i = 0; i < nodes.Count; i++)
            {
                AddNodeView(nodes[i], seeds[i]);
            }
            IReadOnlyList<GraphLink> links = _graph.Links;
            for (int i = 0; i < links.Count; i++)
            {
                AddLinkView(links[i]);
            }

            for (int t = 0; t < warmStartTicks; t++) _layout.Step();
            _layout.Heat(0.7f); // the rest of the settle is visible, and cheap
            _acc = 0f;
            ApplyThemeToAll();
            ApplyLayout();
            _firstFrame = true;
        }

        /// <summary>
        /// Fibonacci sphere per tier. Each server takes as many slots as it has tools (at least one) on the
        /// tool shell, and sits at the mid-slot of its own tools on the server shell, so every cluster starts
        /// on the correct side instead of scrambled.
        /// </summary>
        private Vector3[] ComputeSeeds(IReadOnlyList<GraphNode> nodes)
        {
            Vector3[] seeds = new Vector3[nodes.Count];
            Dictionary<string, int> start = new Dictionary<string, int>();
            Dictionary<string, int> count = new Dictionary<string, int>();
            Dictionary<string, int> used = new Dictionary<string, int>();
            int total = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Tier != NodeTier.Server) continue;
                int tools = 0;
                for (int j = 0; j < nodes.Count; j++)
                {
                    if (nodes[j].Tier == NodeTier.Tool && nodes[j].Server == nodes[i].Server) tools++;
                }
                int slots = Mathf.Max(1, tools);
                start[nodes[i].Server] = total;
                count[nodes[i].Server] = slots;
                total += slots;
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                GraphNode n = nodes[i];
                if (n.Tier == NodeTier.Agent)
                {
                    seeds[i] = Vector3.zero;
                }
                else if (n.Tier == NodeTier.Server)
                {
                    float mid = start[n.Server] + (count[n.Server] - 1) * 0.5f;
                    seeds[i] = ForceLayout3D.Fibonacci(mid, total) * layout.ServerShell;
                }
                else
                {
                    used.TryGetValue(n.Server, out int j);
                    used[n.Server] = j + 1;
                    int s = start.TryGetValue(n.Server, out int st) ? st : 0;
                    seeds[i] = ForceLayout3D.Fibonacci(s + j, total) * layout.ToolShell;
                }
            }
            return seeds;
        }

        private void AddNodeView(GraphNode node, Vector3 seed)
        {
            float r = RadiusOf(node.Tier);
            NodeView v = new NodeView(node, r, transform, _sphereMesh, sphereMaterial, labelFont);
            v.Root.localPosition = seed;
            if (viewCamera != null) v.SnapLabel(viewCamera.transform.rotation);
            _nodeIndex[node.Id] = _views.Count;
            _views.Add(v);
            _layout.AddNode((int)node.Tier, r, seed, node.Tier == NodeTier.Agent);
            if (_tokens != null) v.ApplyTheme(_tokens);
        }

        private void AddLinkView(GraphLink link)
        {
            if (!_nodeIndex.TryGetValue(link.SourceId, out int a) || !_nodeIndex.TryGetValue(link.TargetId, out int b)) return;
            _linkIndex[link] = _links.Count;
            _links.Add(link);
            _linkSrc.Add(a);
            _linkDst.Add(b);
            float rest = link.Tier == NodeTier.Server ? layout.ServerShell : Mathf.Max(1f, layout.ToolShell - layout.ServerShell);
            _layout.AddLink(a, b, rest);
            _edges.SetCount(_links.Count);
            _edges.SetMemory(_links.Count - 1, false);
        }

        private void OnNodeAdded(GraphNode node)
        {
            Vector3 seed;
            if (node.Tier == NodeTier.Tool && _nodeIndex.TryGetValue(GraphModel.ServerId(node.Server), out int si))
            {
                Vector3 dir = _layout.PositionOf(si).normalized;
                if (dir.sqrMagnitude < 0.01f) dir = UnityEngine.Random.onUnitSphere;
                seed = (dir + UnityEngine.Random.insideUnitSphere * 0.25f).normalized * layout.ToolShell;
            }
            else
            {
                seed = UnityEngine.Random.onUnitSphere * (node.Tier == NodeTier.Server ? layout.ServerShell : layout.ToolShell);
            }
            AddNodeView(node, seed);
            _layout.Heat(0.55f); // a graft re-heats the layout; it settles and freezes again
            _acc = 0f;
        }

        private void OnLinkAdded(GraphLink link)
        {
            AddLinkView(link);
            _layout.Heat(0.55f);
        }

        private void ApplyThemeToAll()
        {
            if (_tokens == null) return;
            for (int i = 0; i < _views.Count; i++) _views[i].ApplyTheme(_tokens);
            Material m = edgeMaterial;
            if (_edges != null) m = _edges.Material;
            if (m != null)
            {
                Color l1 = _tokens.Link;
                l1.a = 0.9f;
                Color l2 = _tokens.Link2;
                l2.a = 0.8f;
                Color down = _tokens.Bad;
                down.a = 0.7f;
                m.SetColor("_LinkColor1", l1);
                m.SetColor("_LinkColor2", l2);
                m.SetColor("_FlowColor", _tokens.Flow);
                m.SetColor("_MemColor", _tokens.Mem);
                m.SetColor("_DownColor", down);
            }
        }

        /// <summary>Copy layout positions to the spheres and rebuild the edge ribbons. Runs only while the layout is moving.</summary>
        private void ApplyLayout()
        {
            float bound = 1f;
            for (int i = 0; i < _views.Count; i++)
            {
                Vector3 p = _layout.PositionOf(i);
                _views[i].Root.localPosition = p;
                bound = Mathf.Max(bound, p.magnitude + _views[i].Radius);
            }
            _boundRadius = bound;

            for (int e = 0; e < _links.Count; e++)
            {
                int a = _linkSrc[e];
                int b = _linkDst[e];
                Vector3 pa = _layout.PositionOf(a);
                Vector3 pb = _layout.PositionOf(b);
                Vector3 dir = pb - pa;
                float len = dir.magnitude;
                dir = len > 1e-4f ? dir / len : Vector3.up;
                // Edges start and end on the sphere surfaces, so no line runs behind a label.
                Vector3 sa = pa + dir * (_views[a].Radius * 0.96f);
                Vector3 sb = pb - dir * (_views[b].Radius * 0.96f);
                _edges.SetEdge(e, sa, sb, (int)_links[e].Tier, _links[e].IsDown);
            }
            _edges.CommitGeometry();
        }

        // ------------------------------------------------------------------ reducer events (discrete)

        private int LinkIdx(string src, string dst)
        {
            return _graph.TryGetLink(src, dst, out GraphLink l) && _linkIndex.TryGetValue(l, out int i) ? i : -1;
        }

        private void OnNodeFired(string toolId)
        {
            if (!_nodeIndex.TryGetValue(toolId, out int ti)) return;
            GraphNode tool = _views[ti].Node;
            string sid = GraphModel.ServerId(tool.Server);
            if (!_nodeIndex.TryGetValue(sid, out int si)) return;
            int l1 = LinkIdx(GraphModel.AgentId, sid);
            int l2 = LinkIdx(sid, toolId);
            bool mem = tool.IsMemory;

            // Two hops: agent -> server now, server -> tool a beat later, tool glow and camera after that.
            Schedule(0f, Op.Pulse, 0, 0.55f, false);
            Schedule(0f, Op.Heat, l1, 1f, mem);
            Schedule(0f, Op.Packet, l1, 0f, false);
            Schedule(serverHopDelay, Op.Pulse, si, 0.95f, false);
            Schedule(serverHopDelay, Op.Heat, l2, 1f, mem);
            Schedule(serverHopDelay, Op.Packet, l2, 0f, false);
            Schedule(toolHopDelay, Op.Pulse, ti, 1f, false);
            Schedule(toolHopDelay, Op.Focus, ti, 0f, false);
        }

        private void OnNodeResolved(string toolId)
        {
            if (!_nodeIndex.TryGetValue(toolId, out int ti)) return;
            GraphNode tool = _views[ti].Node;
            string sid = GraphModel.ServerId(tool.Server);
            int l1 = LinkIdx(GraphModel.AgentId, sid);
            int l2 = LinkIdx(sid, toolId);
            bool mem = tool.IsMemory;

            // The return trip reverses the route: tool -> server now, server -> agent a beat later.
            Schedule(0f, Op.Heat, l2, 0.8f, mem);
            Schedule(0f, Op.Packet, l2, 0f, true);
            Schedule(returnHopDelay, Op.Heat, l1, 0.8f, mem);
            Schedule(returnHopDelay, Op.Pulse, 0, 0.45f, false);
            Schedule(returnHopDelay, Op.Packet, l1, 0f, true);
        }

        private void OnPhaseChanged(Phase p)
        {
            _phase = p;
            if (_views.Count > 0)
            {
                _views[0].SetLabel(p == Phase.Thinking ? "thinking" : _graph.AgentLabel);
            }
            if (p == Phase.Thinking || p == Phase.Talking)
            {
                Schedule(0f, Op.Pulse, 0, 0.4f, false);
                Schedule(0f, Op.Focus, 0, 0f, false);
            }
            else if (p == Phase.Idle)
            {
                Schedule(0.4f, Op.Fit, 0, 0f, false);
            }
        }

        private void OnTranscriptChanged()
        {
            if (_reducer != null && _reducer.State.Phase == Phase.Talking && _views.Count > 0)
            {
                Node0Glow(0.12f);
            }
        }

        private void Node0Glow(float amt)
        {
            GraphNode n = _views[0].Node;
            n.Glow = Mathf.Min(1f, n.Glow + amt);
        }

        // ------------------------------------------------------------------ choreography queue

        private void Schedule(float delay, Op op, int a, float amt, bool flag)
        {
            if (delay <= 0f)
            {
                Run(op, a, amt, flag);
                return;
            }
            for (int i = 0; i < _sched.Length; i++)
            {
                if (_sched[i].Used) continue;
                _sched[i] = new Scheduled { Used = true, At = Time.time + delay, Op = op, A = a, Amt = amt, Flag = flag };
                return;
            }
            Run(op, a, amt, flag); // queue full: run now rather than drop it
        }

        private void RunScheduled()
        {
            float now = Time.time;
            for (int i = 0; i < _sched.Length; i++)
            {
                if (!_sched[i].Used || _sched[i].At > now) continue;
                Scheduled s = _sched[i];
                _sched[i].Used = false;
                Run(s.Op, s.A, s.Amt, s.Flag);
            }
        }

        private void Run(Op op, int a, float amt, bool flag)
        {
            switch (op)
            {
                case Op.Pulse:
                    if (a >= 0 && a < _views.Count)
                    {
                        GraphNode n = _views[a].Node;
                        n.Glow = Mathf.Min(1f, n.Glow + amt);
                    }
                    break;
                case Op.Heat:
                    if (a >= 0 && a < _links.Count)
                    {
                        GraphLink l = _links[a];
                        l.Hot = Mathf.Min(1f, l.Hot + amt);
                        _edges.SetMemory(a, flag);
                    }
                    break;
                case Op.Packet:
                    if (a >= 0) _edges.Emit(a, flag);
                    break;
                case Op.Focus:
                    if (a >= 0 && a < _views.Count)
                    {
                        PathFocused?.Invoke(_views[0].Root.localPosition, _views[a].Root.localPosition, _views[a].Radius);
                    }
                    break;
                case Op.Fit:
                    FitRequested?.Invoke();
                    break;
            }
        }

        // ------------------------------------------------------------------ 60 fps layer

        private void Update()
        {
            if (_reducer == null) return;
            float dt = Mathf.Min(Time.deltaTime, 0.1f);

            if (_firstFrame)
            {
                _firstFrame = false;
                FitRequested?.Invoke();
            }

            StepLayout(dt);
            RunScheduled();

            Camera cam = viewCamera != null ? viewCamera : Camera.main;
            if (cam == null) return;
            Quaternion camRot = cam.transform.rotation;
            Vector3 camPos = cam.transform.position;
            bool fogOn = RenderSettings.fog;
            float density = RenderSettings.fogDensity;
            Color fogColor = RenderSettings.fogColor;

            for (int i = 0; i < _views.Count; i++)
            {
                NodeView v = _views[i];
                GraphNode node = v.Node;
                float g = node.Glow;
                if (g > 0f)
                {
                    float k = node.Tier == NodeTier.Agent && _phase != Phase.Idle ? KHold : KDecay;
                    g *= Mathf.Exp(k * dt);
                    if (g < 0.002f) g = 0f;
                    node.Glow = g;
                }
                v.Render(g, dt, camRot, camPos, labelFollowK, labelSortOffset, fogOn, density, fogColor);
            }

            bool anyHot = false;
            for (int e = 0; e < _links.Count; e++)
            {
                GraphLink l = _links[e];
                float h = l.Hot;
                if (h > 0f)
                {
                    h *= Mathf.Exp(KHot * dt);
                    if (h < 0.004f) h = 0f;
                    l.Hot = h;
                    anyHot = true;
                }
                _edges.SetHot(e, h);
            }
            bool pulsing = _edges.Advance(dt);
            if (anyHot || pulsing || _edgesLit)
            {
                _edges.UploadPulses();
            }
            _edgesLit = anyHot || pulsing;
        }

        private void StepLayout(float dt)
        {
            if (_layout.IsSettled) return;
            _acc += dt;
            float tick = layout.Tick;
            int ticks = 0;
            while (_acc >= tick && ticks < 4)
            {
                _layout.Step();
                _acc -= tick;
                ticks++;
            }
            if (_acc > tick * 4f) _acc = 0f;
            if (ticks > 0) ApplyLayout();
            if (_layout.IsSettled)
            {
                _acc = 0f;
                ApplyLayout();
                LayoutSettled?.Invoke();
            }
        }
    }
}
