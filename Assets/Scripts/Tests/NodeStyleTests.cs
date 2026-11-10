using NUnit.Framework;
using UnityEngine;
using AgentConsole.View;

namespace AgentConsole.Tests
{
    public class NodeStyleTests
    {
        [Test]
        public void Keys_AreStableAndRoundTrip()
        {
            Assert.AreEqual("plasma", NodeStyles.Key(NodeStyleKind.PlasmaDome));
            Assert.AreEqual("orbital", NodeStyles.Key(NodeStyleKind.OrbitalCore));
            Assert.AreEqual("hologram", NodeStyles.Key(NodeStyleKind.HologramWire));
            foreach (NodeStyleKind k in NodeStyles.All) Assert.AreEqual(k, NodeStyles.Parse(NodeStyles.Key(k)));
        }

        [Test]
        public void Parse_UnknownFallsBackToPlasma()
        {
            Assert.AreEqual(NodeStyleKind.PlasmaDome, NodeStyles.Parse(null));
            Assert.AreEqual(NodeStyleKind.PlasmaDome, NodeStyles.Parse(""));
            Assert.AreEqual(NodeStyleKind.PlasmaDome, NodeStyles.Parse("wireframe9000"));
            Assert.AreEqual(NodeStyleKind.PlasmaDome, NodeStyles.Parse("Orbital")); // keys are exact, not display labels
        }

        [Test]
        public void All_CoversEveryEnumValueOnce()
        {
            System.Array values = System.Enum.GetValues(typeof(NodeStyleKind));
            Assert.AreEqual(values.Length, NodeStyles.All.Length);
            foreach (NodeStyleKind k in values) Assert.Contains(k, NodeStyles.All);
        }

        [Test]
        public void Labels_AreDistinctAndPlain()
        {
            System.Collections.Generic.HashSet<string> seen = new System.Collections.Generic.HashSet<string>();
            foreach (NodeStyleKind k in NodeStyles.All)
            {
                string l = NodeStyles.Label(k);
                Assert.IsTrue(seen.Add(l), "duplicate label " + l);
                // The console font atlas covers ASCII and Latin-1 plus a few symbols; a picker label must stay inside ASCII.
                foreach (char c in NodeStyles.PickerLabel(k)) Assert.Less(c, 128, "non-ASCII in " + l);
            }
        }

        [Test]
        public void PickerLabel_MarksOnlyUnimplementedStylesSoon()
        {
            foreach (NodeStyleKind k in NodeStyles.All)
            {
                bool soon = NodeStyles.PickerLabel(k).EndsWith("(soon)");
                Assert.AreEqual(!NodeStyles.IsImplemented(k), soon, NodeStyles.Label(k));
            }
            Assert.IsTrue(NodeStyles.IsImplemented(NodeStyles.Default));
        }

        [Test]
        public void AllShippedStyles_AreImplementedAndShowNoSoon()
        {
            // Every kind has its own visual now, so none may still read "(soon)". Adding a kind without a visual must
            // flip IsImplemented to false for it, and this test then points at the missing case in NodeStyleRegistry.
            foreach (NodeStyleKind k in NodeStyles.All)
            {
                Assert.IsTrue(NodeStyles.IsImplemented(k), NodeStyles.Label(k));
                Assert.AreEqual(NodeStyles.Label(k), NodeStyles.PickerLabel(k));
            }
        }

        [Test]
        public void SeedOf_IsStableDistinctAndInRange()
        {
            Assert.AreEqual(NodeStyleMath.SeedOf("eva"), NodeStyleMath.SeedOf("eva"));
            Assert.AreNotEqual(NodeStyleMath.SeedOf("eva"), NodeStyleMath.SeedOf("eve"));
            Assert.AreEqual(NodeStyleMath.SeedOf(null), NodeStyleMath.SeedOf(null));
            foreach (string id in new[] { "", "a", "tool.read_file", "server__tool" })
            {
                float s = NodeStyleMath.SeedOf(id);
                Assert.GreaterOrEqual(s, 0f);
                Assert.Less(s, 97f);
            }
        }

        [Test]
        public void RingNormal_IsUnitDeterministicAndTiltedWithinBounds()
        {
            for (int i = 0; i < 200; i++)
            {
                float seed = i * 0.4817f;
                Vector3 n = NodeStyleMath.RingNormal(seed);
                Assert.AreEqual(1f, n.magnitude, 1e-4f);
                Assert.AreEqual(n, NodeStyleMath.RingNormal(seed));
                // Lean from world up stays inside the bounds, so no ring is flat or edge-on to a level camera.
                float lean = Mathf.Acos(Mathf.Clamp(n.y, -1f, 1f));
                Assert.GreaterOrEqual(lean, NodeStyleMath.RingTiltMin - 1e-3f);
                Assert.LessOrEqual(lean, NodeStyleMath.RingTiltMax + 1e-3f);
            }
            Assert.AreNotEqual(NodeStyleMath.RingNormal(1f), NodeStyleMath.RingNormal(2f));
        }

        [Test]
        public void AdvancePhase_RunsFasterWithGlowAndNeverBackwards()
        {
            float rest = NodeStyleMath.AdvancePhase(0f, 0.1f, 1f, 4f, 0f);
            float lit = NodeStyleMath.AdvancePhase(0f, 0.1f, 1f, 4f, 1f);
            Assert.AreEqual(0.1f, rest, 1e-5f);
            Assert.AreEqual(0.5f, lit, 1e-5f);
            Assert.Greater(lit, rest);
            Assert.AreEqual(2f, NodeStyleMath.AdvancePhase(2f, 0f, 1f, 4f, 1f));
        }
    }
}
