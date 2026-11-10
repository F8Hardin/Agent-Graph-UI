using NUnit.Framework;
using UnityEngine;
using AgentConsole.View;

namespace AgentConsole.Tests
{
    /// <summary>The layout's anchor (the agent) can be moved; the tier shells follow it instead of the origin (UNI-31).</summary>
    public class AnchorLayoutTests
    {
        private static void Run(ForceLayout3D l, int maxTicks = 4000)
        {
            l.Heat(1f);
            for (int i = 0; i < maxTicks && l.Step(); i++) { }
        }

        [Test]
        public void AnchorDefaultsToTheFirstNode()
        {
            ForceLayout3D l = new ForceLayout3D(new LayoutSettings());
            Assert.AreEqual(Vector3.zero, l.AnchorPosition, "empty layout: origin");
            l.AddNode(0, 1.8f, new Vector3(1, 2, 3), true);
            Assert.AreEqual(0, l.AnchorIndex);
            Assert.AreEqual(new Vector3(1, 2, 3), l.AnchorPosition);
            l.Clear();
            Assert.AreEqual(0, l.AnchorIndex);
        }

        [Test]
        public void ServerShellIsMeasuredFromTheAnchorNotTheOrigin()
        {
            LayoutSettings s = new LayoutSettings();
            ForceLayout3D l = new ForceLayout3D(s);
            Vector3 anchor = new Vector3(50f, 0f, 0f);
            l.AddNode(0, 1.8f, anchor, true);
            // Four units from the agent but 54 from the origin: only an anchor-relative spring pulls it out to the shell.
            int server = l.AddNode(1, 1.05f, anchor + new Vector3(4f, 0f, 0f), false);
            Run(l);
            float fromAnchor = (l.PositionOf(server) - l.AnchorPosition).magnitude;
            Assert.That(fromAnchor, Is.InRange(s.ServerShell - 1f, s.ServerShell + 2f), "server settles at shell distance from the agent");
            Assert.Greater(l.PositionOf(server).magnitude, 30f, "and has not been dragged back toward the origin");
        }

        [Test]
        public void ToolShellIsMeasuredFromTheAnchorToo()
        {
            LayoutSettings s = new LayoutSettings();
            ForceLayout3D l = new ForceLayout3D(s);
            Vector3 anchor = new Vector3(0f, -40f, 15f);
            l.AddNode(0, 1.8f, anchor, true);
            int tool = l.AddNode(2, 0.8f, anchor + new Vector3(0f, 0f, 6f), false);
            Run(l);
            // Wide on the near side on purpose: the layout cools and freezes, and from 6 units out a lone tool stops a
            // little short of the 20-unit shell (17.4 measured). What matters is which point it headed for. An
            // origin-relative spring would have pulled it IN, toward 20 units from the origin, 23+ from this anchor.
            float fromAnchor = (l.PositionOf(tool) - anchor).magnitude;
            Assert.That(fromAnchor, Is.InRange(15f, s.ToolShell + 2f), "tool moved out toward the shell around the agent");
            Assert.Greater(l.PositionOf(tool).magnitude, 40f, "and was not drawn toward the origin");
        }

        [Test]
        public void AnchorStaysExactlyWhereItWasSet()
        {
            ForceLayout3D l = new ForceLayout3D(new LayoutSettings());
            l.AddNode(0, 1.8f, Vector3.zero, true);
            int a = l.AddNode(1, 1.05f, new Vector3(10f, 0f, 0f), false);
            int b = l.AddNode(1, 1.05f, new Vector3(-10f, 3f, 0f), false);
            l.AddNode(2, 0.8f, new Vector3(0f, 20f, 0f), false);
            l.AddLink(0, a, 10.5f);
            l.AddLink(0, b, 10.5f);
            Vector3 dropped = new Vector3(7f, -3f, 2f);
            l.SetPosition(0, dropped);
            l.SetPinned(0, false); // a release must not let go of the anchor: a dropped node stays put, no home spring
            Assert.IsTrue(l.IsPinned(0));
            for (int i = 0; i < 400; i++)
            {
                l.Heat(0.5f);
                l.Step();
                Assert.AreEqual(dropped, l.PositionOf(0), "tick " + i);
            }
        }

        [Test]
        public void MovedGraphSettlesToTheSameShapeTranslated()
        {
            Vector3 shift = new Vector3(33f, -12f, 21f);
            ForceLayout3D at0 = Build(Vector3.zero);
            ForceLayout3D moved = Build(shift);
            Run(at0);
            Run(moved);
            for (int i = 0; i < at0.NodeCount; i++)
            {
                Assert.That((moved.PositionOf(i) - shift - at0.PositionOf(i)).magnitude, Is.LessThan(0.05f), "node " + i);
            }
        }

        [Test]
        public void ExplicitAnchorIndexIsHonoured()
        {
            LayoutSettings s = new LayoutSettings();
            ForceLayout3D l = new ForceLayout3D(s);
            int server = l.AddNode(1, 1.05f, new Vector3(4f, 0f, 0f), false); // index 0 is NOT the agent here
            int agent = l.AddNode(0, 1.8f, Vector3.zero, true);
            l.AnchorIndex = agent;
            Run(l);
            Assert.That((l.PositionOf(server) - l.PositionOf(agent)).magnitude, Is.InRange(s.ServerShell - 1f, s.ServerShell + 2f));
        }

        private static ForceLayout3D Build(Vector3 anchor)
        {
            ForceLayout3D l = new ForceLayout3D(new LayoutSettings());
            l.AddNode(0, 1.8f, anchor, true);
            int a = l.AddNode(1, 1.05f, anchor + new Vector3(9f, 1f, 0f), false);
            int b = l.AddNode(1, 1.05f, anchor + new Vector3(-8f, 2f, 3f), false);
            int t1 = l.AddNode(2, 0.8f, anchor + new Vector3(15f, 4f, 1f), false);
            int t2 = l.AddNode(2, 0.8f, anchor + new Vector3(-14f, 5f, 2f), false);
            l.AddLink(0, a, 10.5f);
            l.AddLink(0, b, 10.5f);
            l.AddLink(a, t1, 9.5f);
            l.AddLink(b, t2, 9.5f);
            return l;
        }
    }
}
