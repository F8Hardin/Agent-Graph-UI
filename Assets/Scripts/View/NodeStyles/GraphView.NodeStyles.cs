using UnityEngine;

namespace AgentConsole.View
{
    // The node-style half of GraphView, split out so the coordinator does not grow (STANDARDS section 7).
    public sealed partial class GraphView
    {
        [Header("Node styles")]
        [Tooltip("Template for the Plasma Dome body. Filled by Agent Console/Build Scene.")]
        [SerializeField] private Material plasmaMaterial;
        [Tooltip("Template for the Orbital Core body. Filled by Agent Console/Build Scene.")]
        [SerializeField] private Material orbitalMaterial;
        [Tooltip("Template for the Orbital Core ring and satellite quad. Filled by Agent Console/Build Scene.")]
        [SerializeField] private Material orbitRingMaterial;
        [Tooltip("Template for the Hologram Wire body. Filled by Agent Console/Build Scene.")]
        [SerializeField] private Material hologramMaterial;

        private NodeStyleMaterials _styleMats;
        private NodeStyleKind _style = NodeStyles.Default;

        public NodeStyleKind NodeStyle => _style;

        private void InitNodeStyles()
        {
            _styleMats = new NodeStyleMaterials(plasmaMaterial, orbitalMaterial, orbitRingMaterial, hologramMaterial);
            _style = ConsoleSettings.NodeStyle;
        }

        /// <summary>
        /// Swap every node's body to <paramref name="kind"/> now. Does not persist: the Settings panel persists through
        /// ConsoleSettings, which raises ViewChanged, and that is what drives this (the same route the UI scale takes,
        /// so the panel needs no reference to the graph). Rejected: persisting here, which would make a programmatic
        /// swap overwrite the user's saved choice.
        /// </summary>
        public void SetNodeStyle(NodeStyleKind kind)
        {
            if (kind == _style) return;
            _style = kind;
            for (int i = 0; i < _views.Count; i++) _views[i].SetStyle(kind);
        }

        private void OnViewSettingsChanged() => SetNodeStyle(ConsoleSettings.NodeStyle);
    }
}
