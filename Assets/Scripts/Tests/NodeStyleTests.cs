using NUnit.Framework;
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
    }
}
