using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The material templates a style can clone from. GraphView builds one from its serialized material fields, which
    /// the scene builder fills, so adding a style's material means one field there and one line in the builder
    /// (rejected: Resources.Load by name, which would hide the dependency from the scene and from the build's shader stripping).
    /// </summary>
    internal sealed class NodeStyleMaterials
    {
        public readonly Material Plasma;

        public NodeStyleMaterials(Material plasma)
        {
            if (plasma == null)
            {
                // A scene built before this style existed has no material for it. Make one from the shader rather
                // than throw on every node; Build Scene fixes the scene itself.
                Shader s = Shader.Find("AgentConsole/NodePlasma");
                if (s != null) plasma = new Material(s) { name = "AgentNodePlasma (fallback)" };
                else Debug.LogError("[AgentConsole] No material for the Plasma Dome style and its shader was not found. Run Agent Console/Build Scene.");
            }
            Plasma = plasma;
        }
    }

    /// <summary>The one place that maps a style kind to its visual. A new style is a case here plus a NodeVisual subclass.</summary>
    internal static class NodeStyleRegistry
    {
        public static NodeVisual Create(NodeStyleKind kind, GraphNode node, float radius, Transform root, Mesh sphere, NodeStyleMaterials mats)
        {
            switch (kind)
            {
                // OrbitalCore and HologramWire are placeholders until their own visuals exist. They fall through to
                // Plasma on purpose (rejected: throwing, or a flat fallback sphere) so a saved preference or a picker
                // click on them is harmless. NodeStyles.IsImplemented marks them "(soon)" in the picker.
                case NodeStyleKind.OrbitalCore:
                case NodeStyleKind.HologramWire:
                case NodeStyleKind.PlasmaDome:
                default:
                    return new PlasmaDomeVisual(node, radius, root, sphere, mats.Plasma);
            }
        }
    }
}
