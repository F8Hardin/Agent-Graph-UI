using UnityEngine;
using AgentConsole.Domain;

namespace AgentConsole.View
{
    /// <summary>
    /// The small pure pieces every node style needs: a stable per-node seed, the tier and hot colours, and the ring tilt.
    /// Public so the EditMode tests can reach them (the test assembly sees View through its public surface only).
    /// Lived inside PlasmaDomeVisual until a second and third style wanted the same helpers (rejected: each
    /// style keeping its own copy, which is how two styles end up colouring the same node differently).
    /// </summary>
    public static class NodeStyleMath
    {
        /// <summary>Stable per-node value in [0, 97). FNV-1a over the id (string.GetHashCode differs between runs).</summary>
        public static float SeedOf(string id)
        {
            uint h = 2166136261u;
            if (id != null)
            {
                for (int i = 0; i < id.Length; i++) h = (h ^ id[i]) * 16777619u;
            }
            return (h % 9700u) * 0.01f;
        }

        // The ring normal leans away from world up by this much (radians). Never flat to up and never past the upper
        // bound, so a ring is not seen edge-on from the usual camera heights, which would collapse it to a hair line.
        public const float RingTiltMin = 0.30f;
        public const float RingTiltMax = 0.95f;

        /// <summary>
        /// Unit normal of a node's ring plane, from its seed. Two decorrelated hashes of the seed give the lean and the
        /// heading, so neighbouring seeds do not get neighbouring tilts. Deterministic: the same node tilts the same way
        /// in every run and after every style switch.
        /// </summary>
        public static Vector3 RingNormal(float seed)
        {
            float a = Frac(Mathf.Sin(seed * 12.9898f + 1.3f) * 43758.5453f);
            float b = Frac(Mathf.Sin(seed * 78.233f + 5.1f) * 24634.6345f);
            float lean = Mathf.Lerp(RingTiltMin, RingTiltMax, a);
            float head = b * Mathf.PI * 2f;
            float s = Mathf.Sin(lean);
            return new Vector3(s * Mathf.Cos(head), Mathf.Cos(lean), s * Mathf.Sin(head));
        }

        private static float Frac(float x) => x - Mathf.Floor(x);

        /// <summary>The plain tier colour: the agent is the accent, servers and tools step down toward the background.</summary>
        public static Color BaseOf(ThemeTokens t, NodeTier tier) =>
            tier == NodeTier.Agent ? t.NAgent : tier == NodeTier.Server ? t.NSrv : t.NTool;

        /// <summary>
        /// The colour a node lights up with: Bad when down, Mem when it is a memory node, else the tier's hot colour.
        /// Memory and error tints are blended in by glow, so they pulse in and decay with it instead of sticking.
        /// </summary>
        public static Color HotOf(ThemeTokens t, GraphNode node) =>
            node.IsDown ? t.Bad : node.IsMemory ? t.Mem : node.Tier == NodeTier.Agent ? t.NHot : t.Flow;

        /// <summary>One step of an animation phase in radians: a resting rate plus a boost that follows glow.</summary>
        public static float AdvancePhase(float phase, float dt, float baseRate, float boostRate, float glow) =>
            phase + dt * (baseRate + boostRate * glow);
    }
}
