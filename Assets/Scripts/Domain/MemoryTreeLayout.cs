using System;
using System.Collections.Generic;

namespace AgentConsole.Domain
{
    /// <summary>
    /// A deterministic, local layout for the memory tree, in abstract units: how many levels out each node is, and how
    /// many leaf slots it sits to the side. The view maps those onto world axes; nothing here knows about an engine.
    ///
    /// A classic tidy tree: every leaf gets one slot, a node's span is the sum of its children's, and a parent is centred
    /// over its span, so branches never overlap and a wide branch gets the room it needs. Children keep the order they
    /// first appeared in, so adding a note moves its siblings but never reorders them.
    /// Rejected: feeding these nodes to the force layout. The user wants placed things to stay put, and a session tree
    /// that jostled the main graph every time EVA read a note would break that; this layout is also repeatable, so the
    /// same session always draws the same tree.
    ///
    /// Reuses its arrays, so recomputing on every tree change allocates only when the tree outgrows them.
    /// </summary>
    public sealed class MemoryTreeLayout
    {
        private float[] _lateral = new float[0];
        private int[] _depth = new int[0];
        private int[] _leaves = new int[0];
        private float[] _cursor = new float[0];
        private int _count;

        /// <summary>Levels out from the root: 0 for the root, 1 for its children, and so on.</summary>
        public IReadOnlyList<int> Depth => new ArraySegment<int>(_depth, 0, _count);

        /// <summary>Slots sideways from the root's centre line. Negative and positive; the root is 0.</summary>
        public IReadOnlyList<float> Lateral => new ArraySegment<float>(_lateral, 0, _count);

        public int Count => _count;

        public int DepthOf(int index) => _depth[index];
        public float LateralOf(int index) => _lateral[index];

        /// <summary>The number of leaf slots under a node (1 for a leaf).</summary>
        public int LeavesOf(int index) => _leaves[index];

        /// <summary>The deepest level in the tree, 0 when it is just a root or empty.</summary>
        public int MaxDepth { get; private set; }

        public void Compute(MemoryTree tree)
        {
            IReadOnlyList<MemoryNode> nodes = tree.Nodes;
            int n = nodes.Count;
            _count = n;
            MaxDepth = 0;
            if (n == 0) return;
            if (_lateral.Length < n)
            {
                int cap = Math.Max(n, _lateral.Length * 2);
                Array.Resize(ref _lateral, cap);
                Array.Resize(ref _depth, cap);
                Array.Resize(ref _leaves, cap);
                Array.Resize(ref _cursor, cap);
            }

            // Parents precede children, so one reverse pass sums the leaves and one forward pass places the nodes.
            for (int i = 0; i < n; i++) _leaves[i] = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                if (_leaves[i] == 0) _leaves[i] = 1;
                int p = nodes[i].ParentIndex;
                if (p >= 0) _leaves[p] += _leaves[i];
            }

            _depth[0] = 0;
            _cursor[0] = 0f;
            _lateral[0] = _leaves[0] * 0.5f;
            for (int i = 1; i < n; i++)
            {
                int p = nodes[i].ParentIndex;
                float start = _cursor[p];
                _cursor[p] += _leaves[i];
                _cursor[i] = start;
                _depth[i] = _depth[p] + 1;
                if (_depth[i] > MaxDepth) MaxDepth = _depth[i];
                _lateral[i] = start + _leaves[i] * 0.5f;
            }

            // Centre on the root so it sits on the axis and the tree fans out to both sides.
            float centre = _lateral[0];
            for (int i = 0; i < n; i++) _lateral[i] -= centre;
        }
    }
}
