using UnityEngine;

namespace AgentConsole.View
{
    // What the session memory tree needs from the graph, split out so GraphView.cs does not grow (STANDARDS section 7).
    public sealed partial class GraphView
    {
        internal NodeStyleMaterials StyleMaterials => _styleMats;
        internal Mesh SphereMesh => _sphereMesh;
        internal Camera ViewCamera => viewCamera != null ? viewCamera : Camera.main;
        internal ThemeController Theme => theme;

        /// <summary>The edge material the graph itself draws with (its per-graph instance), so the tree's edges start from the same look.</summary>
        internal Material EdgeMaterialInstance => _edges != null ? _edges.Material : edgeMaterial;

        internal float NodeRadius(int index) => _views[index].Radius;
    }
}
