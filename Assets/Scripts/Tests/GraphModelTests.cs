using System.Collections.Generic;
using NUnit.Framework;
using AgentConsole.Domain;

namespace AgentConsole.Tests
{
    public class GraphModelTests
    {
        private static GraphModel Eva() => new GraphModel("EVA", SampleInventory.Eva());

        [Test]
        public void Split_RecoversServerAndToolFromANamespacedName()
        {
            GraphModel g = Eva();
            ToolRef t = g.Split("agent__foo");
            Assert.AreEqual("agent", t.Server);
            Assert.AreEqual("foo", t.Tool);

            // Only the first "__" separates; the rest belongs to the tool.
            ToolRef u = g.Split("memory__vault__search");
            Assert.AreEqual("memory", u.Server);
            Assert.AreEqual("vault__search", u.Tool);

            ToolRef v = g.Split("fate_ubuntu_server__run_command");
            Assert.AreEqual("fate_ubuntu_server", v.Server);
            Assert.AreEqual("run_command", v.Tool);
        }

        [Test]
        public void Split_BareName_UsesTheInventoryServerThenFallsBackToLocal()
        {
            GraphModel g = Eva();

            // run_command is on two servers; the first one listed wins, as in the prototype.
            ToolRef known = g.Split("run_command");
            Assert.AreEqual("fate_ubuntu_server", known.Server);
            Assert.AreEqual("run_command", known.Tool);

            ToolRef unknown = g.Split("totally_new_tool");
            Assert.AreEqual("local", unknown.Server);
            Assert.AreEqual("totally_new_tool", unknown.Tool);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("__lead")]
        [TestCase("trail__")]
        public void Split_DegenerateNames_FallBackToLocalWithoutThrowing(string raw)
        {
            ToolRef t = Eva().Split(raw);
            Assert.AreEqual("local", t.Server);
            Assert.AreEqual(raw ?? string.Empty, t.Tool);
        }

        [Test]
        public void Build_ProducesThreeTiersWithAMemoryCluster()
        {
            GraphModel g = Eva();
            int servers = 0, tools = 0, agents = 0, expectedTools = 0;
            foreach (ServerInfo s in SampleInventory.Eva()) expectedTools += s.Tools.Count;
            foreach (GraphNode n in g.Nodes)
            {
                if (n.Tier == NodeTier.Agent) agents++;
                else if (n.Tier == NodeTier.Server) servers++;
                else tools++;
            }
            Assert.AreEqual(1, agents);
            Assert.AreEqual(9, servers);
            Assert.AreEqual(expectedTools, tools);
            Assert.AreEqual(servers + tools, g.Links.Count);

            Assert.IsTrue(g.TryGetNode("srv:memory", out GraphNode mem));
            Assert.IsTrue(mem.IsMemory);
            Assert.IsTrue(g.TryGetNode("tool:memory__vault_search", out GraphNode memTool));
            Assert.IsTrue(memTool.IsMemory);
            Assert.IsTrue(g.TryGetNode("tool:robinhood__get_accounts", out GraphNode ordinary));
            Assert.IsFalse(ordinary.IsMemory);

            Assert.IsTrue(g.TryGetNode("srv:github", out GraphNode down));
            Assert.IsTrue(down.IsDown);
            Assert.IsTrue(g.TryGetLink("agent", "srv:github", out GraphLink downLink));
            Assert.IsTrue(downLink.IsDown);
        }

        [Test]
        public void EnsureTool_GraftsAnUnknownToolOnFirstUse_AndOnlyOnce()
        {
            GraphModel g = Eva();
            List<GraphNode> added = new List<GraphNode>();
            List<GraphLink> links = new List<GraphLink>();
            g.NodeAdded += n => added.Add(n);
            g.LinkAdded += l => links.Add(l);
            int before = g.Nodes.Count;

            GraphNode first = g.EnsureTool("robinhood__get_pnl", out ToolRef tref, out bool grafted);
            Assert.IsTrue(grafted);
            Assert.AreEqual("get_pnl", tref.Tool);
            Assert.AreEqual("tool:robinhood__get_pnl", first.Id);
            Assert.IsTrue(first.Grafted);
            Assert.AreEqual(before + 1, g.Nodes.Count);
            Assert.AreEqual(1, added.Count);
            Assert.IsTrue(g.TryGetLink("srv:robinhood", "tool:robinhood__get_pnl", out _));

            GraphNode second = g.EnsureTool("robinhood__get_pnl", out _, out bool graftedAgain);
            Assert.IsFalse(graftedAgain);
            Assert.AreSame(first, second);
            Assert.AreEqual(before + 1, g.Nodes.Count);
            Assert.AreEqual(1, added.Count);
            Assert.AreEqual(1, links.Count);
        }

        [Test]
        public void EnsureTool_GraftsAnUnknownServerWithItsLinkToTheAgent()
        {
            GraphModel g = Eva();
            GraphNode tool = g.EnsureTool("newsrv__do_thing", out _, out bool grafted);
            Assert.IsTrue(grafted);
            Assert.IsTrue(g.TryGetNode("srv:newsrv", out GraphNode srv));
            Assert.IsTrue(srv.Grafted);
            Assert.AreEqual(NodeTier.Server, srv.Tier);
            Assert.IsTrue(g.TryGetLink("agent", "srv:newsrv", out _));
            Assert.IsTrue(g.TryGetLink("srv:newsrv", tool.Id, out _));
            Assert.IsFalse(tool.IsMemory);
        }

        [Test]
        public void EnsureTool_BareNameGoesUnderTheInventoryServer_ElseLocal()
        {
            GraphModel g = Eva();
            GraphNode known = g.EnsureTool("get_datetime", out _, out bool graftedKnown);
            Assert.IsFalse(graftedKnown, "get_datetime is already in the inventory under fate_ubuntu_server");
            Assert.AreEqual("fate_ubuntu_server", known.Server);

            GraphNode unknown = g.EnsureTool("mystery", out _, out bool graftedUnknown);
            Assert.IsTrue(graftedUnknown);
            Assert.AreEqual("local", unknown.Server);
        }

        [Test]
        public void AGraftedMemoryServer_IsStillFlaggedWhenTheInventoryIsEmpty()
        {
            GraphModel g = new GraphModel("EVA");
            GraphNode tool = g.EnsureTool("memory__vault_search", out _, out _);
            Assert.IsTrue(tool.IsMemory);
            Assert.IsTrue(g.TryGetNode("srv:memory", out GraphNode srv));
            Assert.IsTrue(srv.IsMemory);
        }

        [Test]
        public void Build_ReplacesTheGraphAndRaisesRebuilt()
        {
            GraphModel g = Eva();
            int rebuilt = 0;
            g.Rebuilt += () => rebuilt++;
            g.EnsureTool("x__y", out _, out _);
            g.Build(new[] { new ServerInfo("only", null, null, "one") });
            Assert.AreEqual(1, rebuilt);
            Assert.AreEqual(3, g.Nodes.Count);
            Assert.IsFalse(g.TryGetNode("tool:x__y", out _));
        }
    }
}
