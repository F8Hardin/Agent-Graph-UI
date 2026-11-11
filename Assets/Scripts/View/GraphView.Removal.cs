using System;
using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The removal half of GraphView, split out of GraphView.cs (STANDARDS section 7). A server or tool that EVA pauses
    /// leaves the graph WITHOUT a rebuild: only its own nodes, links and spheres go, and every other node keeps its layout
    /// position, its drag, its glow and its label. Rejected: calling BuildAll for a removal, which re-seeds the force
    /// layout, drops every hand-placed node and clears the session memory tree for what was a one-server change.
    ///
    /// The view's lists are dense and parallel to the layout's arrays (node index = layout index, link index = layout link
    /// index = edge index), so a removal shifts everything after it down. That is cheap at this size (a few hundred
    /// nodes, once per toggle), and it keeps every per-frame loop a plain array walk. Rejected: tombstones, which would
    /// put an "is this slot alive" test in each of those loops.
    /// </summary>
    public sealed partial class GraphView
    {
        /// <summary>
        /// Raised after a removal renumbered the nodes: any index held outside this class (the memory tree's anchor) is stale.
        /// Look nodes up again by id with <see cref="TryGetNodeIndex"/>.
        /// </summary>
        public event Action NodesReindexed;

        // The model reports a node's links first (LinkRemoved) and the node last (NodeRemoved), so the layout's
        // RemoveNode never meets a link that still points at it.
        private void OnLinkRemoved(GraphLink link)
        {
            if (!_linkIndex.TryGetValue(link, out int e)) return;
            _links.RemoveAt(e);
            _linkSrc.RemoveAt(e);
            _linkDst.RemoveAt(e);
            _layout.RemoveLink(e);
            _linkIndex.Clear();
            for (int i = 0; i < _links.Count; i++) _linkIndex[_links[i]] = i;
            _edges.SetCount(_links.Count);
            AfterRemoval();
        }

        private void OnNodeRemoved(GraphNode node)
        {
            if (!_nodeIndex.TryGetValue(node.Id, out int idx)) return;
            ReleaseDrag(false); // a removal under a grabbed node is not a click
            _views[idx].Destroy();
            _views.RemoveAt(idx);
            _layout.RemoveNode(idx);
            for (int i = 0; i < _links.Count; i++)
            {
                if (_linkSrc[i] > idx) _linkSrc[i]--;
                if (_linkDst[i] > idx) _linkDst[i]--;
            }
            _nodeIndex.Clear();
            for (int i = 0; i < _views.Count; i++) _nodeIndex[_views[i].Node.Id] = i;
            AfterRemoval();
            NodesReindexed?.Invoke();
        }

        /// <summary>
        /// Everything that named a node or an edge by index is renumbered, so what is in flight is dropped (the controller
        /// only changes the graph while no run is live, so nothing the user is watching is lost) and the edges are
        /// written again from the unchanged layout positions.
        /// </summary>
        private void AfterRemoval()
        {
            for (int i = 0; i < _sched.Length; i++) _sched[i].Used = false;
            _edges.ClearPulses();
            for (int e = 0; e < _links.Count; e++)
            {
                _edges.SetHot(e, _links[e].Hot);
                _edges.SetMemory(e, false);
            }
            ApplyLayout();
            _edges.UploadPulses();
        }
    }
}
