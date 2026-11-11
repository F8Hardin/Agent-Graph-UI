using System;
using System.Collections.Generic;

namespace AgentConsole.Domain
{
    /// <summary>
    /// Connects the reducer to the memory tree: when a memory tool call SUCCEEDS, its result is parsed and the files it
    /// touched are added to the tree. A failed call grows nothing, because the user's rule is that the tree relies on
    /// successful memory tool calls only.
    ///
    /// It listens to two additive reducer events (<see cref="ConsoleReducer.ToolStarted"/>, <see cref="ConsoleReducer.ToolResolved"/>)
    /// that carry the whole call. The existing NodeFired/NodeResolved carry only a node id, which cannot tell two
    /// concurrent calls of the same tool apart, and the state's tool list would have to be searched for "the one that
    /// just ended". Rejected: a view that scans <c>State.ToolCalls</c> and parses it, which is exactly what the
    /// one-reducer rule forbids.
    ///
    /// The tree is cleared when the conversation changes (see <see cref="ConsoleReducer.ConversationCleared"/>) and when the graph is rebuilt.
    /// </summary>
    public sealed class MemoryTracker : IDisposable
    {
        private readonly ConsoleReducer _reducer;
        private readonly GraphModel _graph;
        private readonly Dictionary<string, string> _liveByRun = new Dictionary<string, string>();

        public MemoryTree Tree { get; } = new MemoryTree();

        /// <summary>
        /// The MCP server that owns the memory tools, taken from the <c>server__tool</c> prefix of the last successful
        /// memory call. Null until one happened. The view hangs the tree's root on this server's node.
        /// </summary>
        public string Server { get; private set; }

        /// <summary>The owning server changed (including the first time it became known).</summary>
        public event Action ServerChanged;

        /// <summary>
        /// A memory call that names its file up front (read, write, append) started (true) or finished (false), with the
        /// path as normalised. A search has no path until it ends, so it never raises this. The view keeps the path to
        /// that note lit while the call runs. Rejected for search: guessing the path from the query.
        /// </summary>
        public event Action<string, bool> LiveChanged;

        public MemoryTracker(ConsoleReducer reducer)
        {
            _reducer = reducer ?? throw new ArgumentNullException(nameof(reducer));
            _graph = reducer.Graph;
            _reducer.ToolStarted += OnStarted;
            _reducer.ToolResolved += OnResolved;
            _reducer.ConversationCleared += Reset;
            _graph.Rebuilt += Reset;
            _graph.NodeRemoved += OnGraphNodeRemoved;
        }

        public void Dispose()
        {
            _reducer.ToolStarted -= OnStarted;
            _reducer.ToolResolved -= OnResolved;
            _reducer.ConversationCleared -= Reset;
            _graph.Rebuilt -= Reset;
            _graph.NodeRemoved -= OnGraphNodeRemoved;
        }

        /// <summary>
        /// The tree hangs off the memory server's node. When that server is switched off (or goes offline) its node leaves
        /// the graph, and a tree with no host would float anchored to the agent, which reads as memory belonging to EVA
        /// itself. So the tree goes with it, and the next successful memory call, once the server is back, grows it again.
        /// Rejected: keeping the tree and re-anchoring it, which shows files of a server the agent can no longer reach.
        /// </summary>
        private void OnGraphNodeRemoved(GraphNode node)
        {
            if (Server == null || node.Tier != NodeTier.Server || node.Id != GraphModel.ServerId(Server)) return;
            Reset();
            Server = null;
            ServerChanged?.Invoke();
        }

        private void Reset()
        {
            _liveByRun.Clear();
            Tree.Clear();
        }

        private void OnStarted(ToolCall call)
        {
            if (!MemoryAccessParser.TryKindOf(call.RawTool, out MemoryAccessKind kind) || kind == MemoryAccessKind.Hit) return;
            string path = MemoryAccessParser.FirstPath(call.Input);
            if (path == null || string.IsNullOrEmpty(call.RunId)) return;
            _liveByRun[call.RunId] = path;
            LiveChanged?.Invoke(path, true);
        }

        private void OnResolved(ToolCall call)
        {
            IReadOnlyList<MemoryTouch> touches = MemoryAccessParser.Parse(
                call.RawTool, call.Input, call.Output, call.Status == ToolStatus.Done);
            if (touches.Count > 0)
            {
                if (!string.Equals(Server, call.Server, StringComparison.Ordinal))
                {
                    Server = call.Server;
                    ServerChanged?.Invoke();
                }
                for (int i = 0; i < touches.Count; i++) Tree.Touch(touches[i].Path, touches[i].Kind);
            }
            // After the touches, so a note created by this very call exists when the view un-lights it.
            if (!string.IsNullOrEmpty(call.RunId) && _liveByRun.TryGetValue(call.RunId, out string live))
            {
                _liveByRun.Remove(call.RunId);
                LiveChanged?.Invoke(live, false);
            }
        }
    }
}
