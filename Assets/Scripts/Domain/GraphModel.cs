using System;
using System.Collections.Generic;

namespace AgentConsole.Domain
{
    public enum NodeTier
    {
        Agent = 0,
        Server = 1,
        Tool = 2
    }

    /// <summary>A tool name split into its server and bare tool parts.</summary>
    public readonly struct ToolRef
    {
        public string Server { get; }
        public string Tool { get; }

        public ToolRef(string server, string tool)
        {
            Server = server;
            Tool = tool;
        }
    }

    public sealed class GraphNode
    {
        public string Id { get; }
        public string Label { get; }
        public NodeTier Tier { get; }

        /// <summary>Owning server name for tool nodes and server nodes; null for the agent.</summary>
        public string Server { get; }

        /// <summary>True for a memory server and for each of its tools. The view colours these differently.</summary>
        public bool IsMemory { get; }

        public bool IsDown { get; }

        /// <summary>True if the node was not in the inventory and was added on first use.</summary>
        public bool Grafted { get; }

        /// <summary>
        /// Mutable, owned by the view's 60fps layer. The domain never writes it, and the view
        /// reads it with no event and no allocation.
        /// </summary>
        public float Glow;

        /// <summary>
        /// A node that belongs to no <see cref="GraphModel"/>: the view-side decoration (the session memory tree) needs the
        /// same node type to reuse the node styles, but must not be in the graph, the force layout or the reducer's links.
        /// Rejected: adding these to the GraphModel, which would put them in the force layout the user wants left alone.
        /// </summary>
        public static GraphNode Detached(string id, string label, NodeTier tier, string server, bool isMemory) =>
            new GraphNode(id, label, tier, server, isMemory, false, false);

        internal GraphNode(string id, string label, NodeTier tier, string server, bool isMemory, bool isDown, bool grafted)
        {
            Id = id;
            Label = label;
            Tier = tier;
            Server = server;
            IsMemory = isMemory;
            IsDown = isDown;
            Grafted = grafted;
        }
    }

    public sealed class GraphLink
    {
        public string SourceId { get; }
        public string TargetId { get; }

        /// <summary>Tier of the target: Server for agent-to-server links, Tool for server-to-tool links.</summary>
        public NodeTier Tier { get; }
        public bool IsDown { get; }

        /// <summary>Mutable, owned by the view's 60fps layer, like <see cref="GraphNode.Glow"/>.</summary>
        public float Hot;

        internal GraphLink(string sourceId, string targetId, NodeTier tier, bool isDown)
        {
            SourceId = sourceId;
            TargetId = targetId;
            Tier = tier;
            IsDown = isDown;
        }

        public static string KeyOf(string sourceId, string targetId) => sourceId + ">" + targetId;
    }

    /// <summary>
    /// Three tiers: agent (0), MCP server (1), tool (2). Built from an inventory, and any node
    /// not in it is grafted on first use. Main-thread only, like the reducer that owns it.
    ///
    /// It shows what the agent can use RIGHT NOW: a server that is connected and not paused, with the tools of it that are
    /// not paused (<see cref="ServerInfo.IsLive"/>). That reverses the rule UNI-20 shipped, which kept paused servers and
    /// drew a full green tree for an MCP that was switched off. The rule lives here, in the one place that builds nodes,
    /// so no caller can hand the graph a paused server by mistake.
    ///
    /// Two ways to change it. <see cref="Build"/> replaces everything and is for the first reading. <see cref="Reconcile"/>
    /// adds and removes only the difference and raises per-node events, so the view keeps every other node where the user
    /// left it. Rejected: rebuilding on every pause toggle, which reset the force layout, every dragged node and the
    /// session memory tree for a one-server change.
    /// </summary>
    public sealed class GraphModel
    {
        public const string AgentId = "agent";
        public const string LocalServer = "local";
        public const string DefaultMemoryServerName = "memory";

        private readonly List<GraphNode> _nodes = new List<GraphNode>();
        private readonly List<GraphLink> _links = new List<GraphLink>();
        private readonly Dictionary<string, GraphNode> _byId = new Dictionary<string, GraphNode>();
        private readonly Dictionary<string, GraphLink> _byKey = new Dictionary<string, GraphLink>();
        private readonly Dictionary<string, string> _serverOfTool = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _kindOfServer = new Dictionary<string, string>();

        public string AgentLabel { get; }
        public IReadOnlyList<GraphNode> Nodes => _nodes;
        public IReadOnlyList<GraphLink> Links => _links;

        /// <summary>Raised when a node is added after Build, i.e. grafted on first use.</summary>
        public event Action<GraphNode> NodeAdded;
        public event Action<GraphLink> LinkAdded;

        /// <summary>
        /// Raised by <see cref="Reconcile"/> for each node it takes out, AFTER the links that touched it were reported with
        /// <see cref="LinkRemoved"/>, so a listener never holds a link to a node that is already gone.
        /// </summary>
        public event Action<GraphNode> NodeRemoved;
        public event Action<GraphLink> LinkRemoved;

        /// <summary>Raised after Build has replaced the whole graph.</summary>
        public event Action Rebuilt;

        public GraphModel(string agentLabel = "EVA", IEnumerable<ServerInfo> inventory = null)
        {
            AgentLabel = agentLabel ?? "EVA";
            Build(inventory);
        }

        public static string ServerId(string server) => "srv:" + server;
        public static string ToolId(string server, string tool) => "tool:" + server + "__" + tool;

        /// <summary>Replace the graph with one built from the inventory. Null builds just the agent.</summary>
        public void Build(IEnumerable<ServerInfo> inventory)
        {
            _nodes.Clear();
            _links.Clear();
            _byId.Clear();
            _byKey.Clear();
            _serverOfTool.Clear();
            _kindOfServer.Clear();

            AddNode(new GraphNode(AgentId, AgentLabel, NodeTier.Agent, null, false, false, false), false);

            if (inventory != null)
            {
                foreach (ServerInfo s in inventory)
                {
                    if (s == null || string.IsNullOrEmpty(s.Name) || !s.IsLive || _kindOfServer.ContainsKey(s.Name))
                    {
                        continue;
                    }
                    RegisterServer(s);
                    AddServer(s, false);
                }
            }

            Rebuilt?.Invoke();
        }

        /// <summary>True once a server from an inventory is in the graph (as opposed to only the agent and grafted nodes).</summary>
        public bool HasInventoryNodes
        {
            get
            {
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (_nodes[i].Tier == NodeTier.Server && !_nodes[i].Grafted) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Bring the graph in line with a newer inventory by adding and removing only what differs, with the add and
        /// remove events a view needs to leave everything else alone. Returns true if anything changed.
        ///
        /// Grafted nodes (first use of something the inventory did not list) are left alone while their server stays: a
        /// call that is lighting a node must not lose it to a poll. They go when their whole server does. Rejected:
        /// removing every node the new reading does not list, which would make a tool call EVA made to a tool its
        /// own list missed flicker in and out on each poll.
        /// </summary>
        public bool Reconcile(IEnumerable<ServerInfo> inventory)
        {
            List<ServerInfo> live = new List<ServerInfo>();
            HashSet<string> liveServers = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> liveIds = new HashSet<string>(StringComparer.Ordinal);
            if (inventory != null)
            {
                foreach (ServerInfo s in inventory)
                {
                    if (s == null || string.IsNullOrEmpty(s.Name) || !s.IsLive || !liveServers.Add(s.Name)) continue;
                    live.Add(s);
                    foreach (ToolInfo t in s.ToolDetails)
                    {
                        if (!t.Paused) liveIds.Add(ToolId(s.Name, t.Name));
                    }
                }
            }

            // Decide first, then remove: removing while walking _nodes would skip entries.
            List<GraphNode> goneServers = new List<GraphNode>();
            List<GraphNode> goneTools = new List<GraphNode>();
            HashSet<string> managed = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _nodes.Count; i++)
            {
                GraphNode n = _nodes[i];
                if (n.Tier != NodeTier.Server || n.Grafted) continue;
                managed.Add(n.Server);
                if (!liveServers.Contains(n.Server)) goneServers.Add(n);
            }
            for (int i = 0; i < _nodes.Count; i++)
            {
                GraphNode n = _nodes[i];
                if (n.Tier != NodeTier.Tool || n.Grafted || !managed.Contains(n.Server) || !liveServers.Contains(n.Server)) continue;
                if (!liveIds.Contains(n.Id)) goneTools.Add(n);
            }

            bool changed = false;
            for (int i = 0; i < goneTools.Count; i++) changed |= RemoveWithLinks(goneTools[i]);
            for (int i = 0; i < goneServers.Count; i++)
            {
                GraphNode srv = goneServers[i];
                List<GraphNode> tools = new List<GraphNode>();
                for (int j = 0; j < _nodes.Count; j++)
                {
                    if (_nodes[j].Tier == NodeTier.Tool && _nodes[j].Server == srv.Server) tools.Add(_nodes[j]);
                }
                for (int j = 0; j < tools.Count; j++) RemoveWithLinks(tools[j]);
                changed |= RemoveWithLinks(srv);
            }

            _serverOfTool.Clear();
            _kindOfServer.Clear();
            for (int i = 0; i < live.Count; i++) RegisterServer(live[i]);

            for (int i = 0; i < live.Count; i++)
            {
                ServerInfo s = live[i];
                string sid = ServerId(s.Name);
                if (!_byId.ContainsKey(sid))
                {
                    AddNode(new GraphNode(sid, s.Name, NodeTier.Server, s.Name, s.Kind == ServerInfo.KindMemory, false, false), true);
                    AddLink(new GraphLink(AgentId, sid, NodeTier.Server, false), true);
                    changed = true;
                }
                foreach (ToolInfo info in s.ToolDetails)
                {
                    if (info.Paused) continue;
                    string tid = ToolId(s.Name, info.Name);
                    if (_byId.ContainsKey(tid)) continue;
                    AddNode(new GraphNode(tid, info.Name, NodeTier.Tool, s.Name, s.Kind == ServerInfo.KindMemory, false, false), true);
                    AddLink(new GraphLink(sid, tid, NodeTier.Tool, false), true);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// Port of the prototype's split(). EVA namespaces MCP tools as <c>server__tool</c>, so the
        /// server is recoverable from the event itself. A bare name falls back to the inventory's
        /// server for that tool, then to "local".
        /// </summary>
        public ToolRef Split(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return new ToolRef(LocalServer, raw ?? string.Empty);
            }
            int i = raw.IndexOf("__", StringComparison.Ordinal);
            if (i > 0 && i + 2 < raw.Length)
            {
                return new ToolRef(raw.Substring(0, i), raw.Substring(i + 2));
            }
            return new ToolRef(_serverOfTool.TryGetValue(raw, out string srv) ? srv : LocalServer, raw);
        }

        /// <summary>
        /// A server is memory if the inventory says so. A server not in the inventory counts as
        /// memory only if it is literally named "memory", so a missing inventory does not demote it.
        /// </summary>
        public bool IsMemoryServer(string server)
        {
            if (string.IsNullOrEmpty(server))
            {
                return false;
            }
            return _kindOfServer.TryGetValue(server, out string kind)
                ? kind == ServerInfo.KindMemory
                : server == DefaultMemoryServerName;
        }

        public bool TryGetNode(string id, out GraphNode node) => _byId.TryGetValue(id ?? string.Empty, out node);

        public bool TryGetLink(string sourceId, string targetId, out GraphLink link) =>
            _byKey.TryGetValue(GraphLink.KeyOf(sourceId, targetId), out link);

        /// <summary>
        /// Resolve the tool node for a raw tool name, grafting the tool (and its server) if unseen.
        /// <paramref name="grafted"/> is true only on the call that added something.
        /// </summary>
        public GraphNode EnsureTool(string raw, out ToolRef tref, out bool grafted)
        {
            tref = Split(raw);
            grafted = false;
            string tid = ToolId(tref.Server, tref.Tool);
            if (_byId.TryGetValue(tid, out GraphNode existing))
            {
                return existing;
            }

            grafted = true;
            string sid = ServerId(tref.Server);
            bool mem = IsMemoryServer(tref.Server);
            if (!_byId.ContainsKey(sid))
            {
                AddNode(new GraphNode(sid, tref.Server, NodeTier.Server, tref.Server, mem, false, true), true);
                AddLink(new GraphLink(AgentId, sid, NodeTier.Server, false), true);
            }

            GraphNode tool = new GraphNode(tid, tref.Tool, NodeTier.Tool, tref.Server, mem, false, true);
            AddNode(tool, true);
            AddLink(new GraphLink(sid, tid, NodeTier.Tool, false), true);
            return tool;
        }

        private void RegisterServer(ServerInfo s)
        {
            if (_kindOfServer.ContainsKey(s.Name)) return;
            _kindOfServer[s.Name] = s.Kind;
            foreach (ToolInfo info in s.ToolDetails)
            {
                // First server to list a bare tool name wins, as in the prototype's SRV_OF.
                if (!info.Paused && !_serverOfTool.ContainsKey(info.Name)) _serverOfTool[info.Name] = s.Name;
            }
        }

        private void AddServer(ServerInfo s, bool raise)
        {
            bool mem = s.Kind == ServerInfo.KindMemory;
            string sid = ServerId(s.Name);
            AddNode(new GraphNode(sid, s.Name, NodeTier.Server, s.Name, mem, false, false), raise);
            AddLink(new GraphLink(AgentId, sid, NodeTier.Server, false), raise);
            foreach (ToolInfo info in s.ToolDetails)
            {
                if (info.Paused) continue; // a paused tool is not something the agent can use, so it is not drawn
                string tid = ToolId(s.Name, info.Name);
                if (_byId.ContainsKey(tid)) continue;
                AddNode(new GraphNode(tid, info.Name, NodeTier.Tool, s.Name, mem, false, false), raise);
                AddLink(new GraphLink(sid, tid, NodeTier.Tool, false), raise);
            }
        }

        /// <summary>Take a node out with every link that touches it, links first (see <see cref="NodeRemoved"/>).</summary>
        private bool RemoveWithLinks(GraphNode n)
        {
            if (!_byId.ContainsKey(n.Id)) return false;
            for (int i = _links.Count - 1; i >= 0; i--)
            {
                GraphLink l = _links[i];
                if (l.SourceId != n.Id && l.TargetId != n.Id) continue;
                _links.RemoveAt(i);
                _byKey.Remove(GraphLink.KeyOf(l.SourceId, l.TargetId));
                LinkRemoved?.Invoke(l);
            }
            _nodes.Remove(n);
            _byId.Remove(n.Id);
            NodeRemoved?.Invoke(n);
            return true;
        }

        private void AddNode(GraphNode n, bool raise)
        {
            _nodes.Add(n);
            _byId[n.Id] = n;
            if (raise)
            {
                NodeAdded?.Invoke(n);
            }
        }

        private void AddLink(GraphLink l, bool raise)
        {
            _links.Add(l);
            _byKey[GraphLink.KeyOf(l.SourceId, l.TargetId)] = l;
            if (raise)
            {
                LinkAdded?.Invoke(l);
            }
        }
    }
}
