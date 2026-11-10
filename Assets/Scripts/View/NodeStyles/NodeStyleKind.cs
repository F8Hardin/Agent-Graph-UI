namespace AgentConsole.View
{
    /// <summary>The look of a node's body. The label, collider and layout are the same for every style.</summary>
    public enum NodeStyleKind
    {
        PlasmaDome = 0,
        OrbitalCore = 1,
        HologramWire = 2
    }

    /// <summary>
    /// Keys, labels and parsing for <see cref="NodeStyleKind"/>, kept free of anything that touches a scene so the
    /// EditMode tests can reach it. Public on purpose: the test assembly sees View through its public surface only
    /// (rejected: InternalsVisibleTo, which would quietly widen what every View type promises to its callers).
    /// </summary>
    public static class NodeStyles
    {
        public const NodeStyleKind Default = NodeStyleKind.PlasmaDome;

        /// <summary>Every kind, in picker order.</summary>
        public static readonly NodeStyleKind[] All =
        {
            NodeStyleKind.PlasmaDome, NodeStyleKind.OrbitalCore, NodeStyleKind.HologramWire
        };

        /// <summary>The persisted name. Never rename one: it is what a saved preference is read back against.</summary>
        public static string Key(NodeStyleKind kind)
        {
            switch (kind)
            {
                case NodeStyleKind.OrbitalCore: return "orbital";
                case NodeStyleKind.HologramWire: return "hologram";
                default: return "plasma";
            }
        }

        public static string Label(NodeStyleKind kind)
        {
            switch (kind)
            {
                case NodeStyleKind.OrbitalCore: return "Orbital Core";
                case NodeStyleKind.HologramWire: return "Hologram Wire";
                default: return "Plasma Dome";
            }
        }

        /// <summary>
        /// False while a kind still maps to another style's visual (see NodeStyleRegistry). The picker marks those
        /// "(soon)" rather than hiding them, so the saved key and the menu are already final when they land.
        /// All three styles have their own visual now; a future kind starts at false (switch on the kind) and flips
        /// in the same change that gives it its own NodeVisual.
        /// </summary>
        public static bool IsImplemented(NodeStyleKind kind)
        {
            switch (kind)
            {
                case NodeStyleKind.PlasmaDome:
                case NodeStyleKind.OrbitalCore:
                case NodeStyleKind.HologramWire:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The text on the picker button.</summary>
        public static string PickerLabel(NodeStyleKind kind) =>
            IsImplemented(kind) ? Label(kind) : Label(kind) + " (soon)";

        /// <summary>The kind for a saved key. Anything unknown (empty, null, a key from a newer build) is the default.</summary>
        public static NodeStyleKind Parse(string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                for (int i = 0; i < All.Length; i++)
                {
                    if (Key(All[i]) == key) return All[i];
                }
            }
            return Default;
        }
    }
}
