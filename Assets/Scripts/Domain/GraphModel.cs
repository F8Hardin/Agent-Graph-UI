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

    /// <summary>One MCP server in the inventory, with the names of its tools.</summary>
    public sealed class ServerInfo
    {
        public const string KindMcp = "mcp";
        public const string KindMemory = "memory";
        public const string StatusConnected = "connected";
        public const string StatusError = "error";

        public string Name { get; }
        public string Kind { get; }
        public string Status { get; }
        public IReadOnlyList<string> Tools { get; }

        public ServerInfo(string name, string kind, string status, params string[] tools)
        {
            Name = name;
            Kind = string.IsNullOrEmpty(kind) ? KindMcp : kind;
            Status = string.IsNullOrEmpty(status) ? StatusConnected : status;
            Tools = tools ?? Array.Empty<string>();
        }
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
                    if (s == null || string.IsNullOrEmpty(s.Name) || _kindOfServer.ContainsKey(s.Name))
                    {
                        continue;
                    }
                    _kindOfServer[s.Name] = s.Kind;
                    bool down = string.Equals(s.Status, ServerInfo.StatusError, StringComparison.OrdinalIgnoreCase);
                    bool mem = s.Kind == ServerInfo.KindMemory;
                    string sid = ServerId(s.Name);
                    AddNode(new GraphNode(sid, s.Name, NodeTier.Server, s.Name, mem, down, false), false);
                    AddLink(new GraphLink(AgentId, sid, NodeTier.Server, down), false);
                    foreach (string t in s.Tools)
                    {
                        if (string.IsNullOrEmpty(t))
                        {
                            continue;
                        }
                        // First server to list a bare tool name wins, as in the prototype's SRV_OF.
                        if (!_serverOfTool.ContainsKey(t))
                        {
                            _serverOfTool[t] = s.Name;
                        }
                        string tid = ToolId(s.Name, t);
                        if (_byId.ContainsKey(tid))
                        {
                            continue;
                        }
                        AddNode(new GraphNode(tid, t, NodeTier.Tool, s.Name, mem, false, false), false);
                        AddLink(new GraphLink(sid, tid, NodeTier.Tool, false), false);
                    }
                }
            }

            Rebuilt?.Invoke();
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
