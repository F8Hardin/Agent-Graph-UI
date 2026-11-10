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
        public readonly Material Orbital;
        public readonly Material OrbitRing;
        public readonly Material Hologram;

        public NodeStyleMaterials(Material plasma, Material orbital, Material orbitRing, Material hologram)
        {
            Plasma = OrFallback(plasma, "AgentConsole/NodePlasma", "Plasma Dome");
            Orbital = OrFallback(orbital, "AgentConsole/NodeOrbital", "Orbital Core");
            OrbitRing = OrFallback(orbitRing, "AgentConsole/NodeOrbitRing", "Orbital Core ring");
            Hologram = OrFallback(hologram, "AgentConsole/NodeHologram", "Hologram Wire");
        }

        // A scene built before a style existed has no material for it. Make one from the shader rather than throw on
        // every node; Build Scene fixes the scene itself.
        private static Material OrFallback(Material m, string shaderName, string what)
        {
            if (m != null) return m;
            Shader s = Shader.Find(shaderName);
            if (s != null) return new Material(s) { name = what + " (fallback)" };
            Debug.LogError("[AgentConsole] No material for " + what + " and its shader was not found. Run Agent Console/Build Scene.");
            return null;
        }
    }

    /// <summary>The one place that maps a style kind to its visual. A new style is a case here plus a NodeVisual subclass.</summary>
    internal static class NodeStyleRegistry
    {
        public static NodeVisual Create(NodeStyleKind kind, GraphNode node, float radius, Transform root, Mesh sphere, NodeStyleMaterials mats)
        {
            switch (kind)
            {
                case NodeStyleKind.OrbitalCore:
                    return new OrbitalCoreVisual(node, radius, root, sphere, mats.Orbital, mats.OrbitRing);
                case NodeStyleKind.HologramWire:
                    return new HologramWireVisual(node, radius, root, sphere, mats.Hologram);
                case NodeStyleKind.PlasmaDome:
                default:
                    return new PlasmaDomeVisual(node, radius, root, sphere, mats.Plasma);
            }
        }
    }
}
