using System;
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
            Assert.AreEqual(7, servers);
            Assert.AreEqual(expectedTools, tools);
            Assert.AreEqual(servers + tools, g.Links.Count);

            Assert.IsTrue(g.TryGetNode("srv:memory", out GraphNode mem));
            Assert.IsTrue(mem.IsMemory);
            Assert.IsTrue(g.TryGetNode("tool:memory__vault_search", out GraphNode memTool));
            Assert.IsTrue(memTool.IsMemory);
            Assert.IsTrue(g.TryGetNode("tool:robinhood__get_accounts", out GraphNode ordinary));
            Assert.IsFalse(ordinary.IsMemory);
        }

        // UNI-20: the demo graph used to show fates_desktop and github red and fates_macbook healthy, the reverse of the
        // user's real machines, which read as a bug. The offline sample is now an "everything is up" picture.
        [Test]
        public void SampleInventory_ShowsNoRedNodes_AndNoMacbook_AndAnOnlineDesktop()
        {
            GraphModel g = Eva();
            foreach (GraphNode n in g.Nodes) Assert.IsFalse(n.IsDown, n.Id);
            foreach (GraphLink l in g.Links) Assert.IsFalse(l.IsDown, l.TargetId);
            Assert.IsFalse(g.TryGetNode("srv:fates_macbook", out _));
            Assert.IsFalse(g.TryGetNode("srv:github", out _));
            Assert.IsTrue(g.TryGetNode("srv:fates_desktop", out GraphNode desktop));
            Assert.IsFalse(desktop.IsDown);
            Assert.IsTrue(g.TryGetNode("tool:fates_desktop__screenshot", out _), "the desktop has tools, not an empty cluster");
            foreach (ServerInfo s in SampleInventory.Eva()) Assert.IsTrue(s.IsConnected, s.Name);
        }

        [Test]
        public void SampleInventory_ServersTheReplayScriptCallAreAllInIt()
        {
            GraphModel g = Eva();
            Assert.IsTrue(g.TryGetNode("tool:memory__vault_search", out _));
            Assert.IsTrue(g.TryGetNode("tool:robinhood__get_equity_positions", out _));
            Assert.IsTrue(g.TryGetNode("tool:fate_ubuntu_server__run_command", out _));
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

        // ------------------------------------------------------------------ incremental change (UNI-12)

        private static ServerInfo[] EvaWith(string server, bool paused)
        {
            ServerInfo[] inv = SampleInventory.Eva();
            for (int i = 0; i < inv.Length; i++)
            {
                if (inv[i].Name == server) inv[i] = inv[i].WithPaused(paused);
            }
            return inv;
        }

        [Test]
        public void Build_LeavesOutAPausedServerAndAPausedTool()
        {
            ServerInfo[] inv = EvaWith("robinhood", true);
            inv[2] = inv[2].WithToolPaused("fates_desktop__screenshot", true);
            GraphModel g = new GraphModel("EVA", inv);
            Assert.IsFalse(g.TryGetNode("srv:robinhood", out _));
            Assert.IsFalse(g.TryGetNode("tool:robinhood__get_accounts", out _));
            Assert.IsTrue(g.TryGetNode("srv:fates_desktop", out _));
            Assert.IsFalse(g.TryGetNode("tool:fates_desktop__screenshot", out _));
            Assert.IsTrue(g.TryGetNode("tool:fates_desktop__run_command", out _));
        }

        [Test]
        public void Reconcile_PausingAServer_RemovesOnlyItsNodes_LeavingTheOthersIdsAndOrderIntact_AndRaisesEachRemovalOnce()
        {
            GraphModel g = Eva();
            List<GraphNode> before = new List<GraphNode>(g.Nodes);
            int linksBefore = g.Links.Count;
            List<string> log = new List<string>();
            int rebuilt = 0, added = 0;
            g.NodeRemoved += n => log.Add("node:" + n.Id);
            g.LinkRemoved += l => log.Add("link:" + l.TargetId);
            g.Rebuilt += () => rebuilt++;
            g.NodeAdded += n => added++;

            Assert.IsTrue(g.Reconcile(EvaWith("robinhood", true)));

            string[] gone =
            {
                "srv:robinhood", "tool:robinhood__get_equity_positions", "tool:robinhood__get_equity_quotes",
                "tool:robinhood__get_equity_orders", "tool:robinhood__get_accounts", "tool:robinhood__get_realized_pnl"
            };
            Assert.AreEqual(0, rebuilt, "incremental, not a rebuild");
            Assert.AreEqual(0, added);
            Assert.AreEqual(gone.Length * 2, log.Count, "one link event and one node event per removed node, no repeats");
            foreach (string id in gone)
            {
                int node = log.IndexOf("node:" + id);
                int link = log.IndexOf("link:" + id);
                Assert.GreaterOrEqual(node, 0, id);
                Assert.GreaterOrEqual(link, 0, id);
                Assert.Less(link, node, id + ": the link goes before its node, so no listener holds a link to a missing node");
                Assert.AreEqual(node, log.LastIndexOf("node:" + id), id + " reported once");
                Assert.IsFalse(g.TryGetNode(id, out _));
            }

            List<GraphNode> expected = new List<GraphNode>();
            foreach (GraphNode n in before)
            {
                if (Array.IndexOf(gone, n.Id) < 0) expected.Add(n);
            }
            Assert.AreEqual(expected.Count, g.Nodes.Count);
            for (int i = 0; i < expected.Count; i++) Assert.AreSame(expected[i], g.Nodes[i], "node " + i + " keeps its identity and its place in the order");
            Assert.AreEqual(linksBefore - gone.Length, g.Links.Count);
            foreach (GraphLink l in g.Links)
            {
                Assert.IsTrue(g.TryGetNode(l.SourceId, out _) && g.TryGetNode(l.TargetId, out _), "no link dangles");
            }

            Assert.IsFalse(g.Reconcile(EvaWith("robinhood", true)), "the same reading changes nothing");
            Assert.AreEqual(gone.Length * 2, log.Count);
        }

        [Test]
        public void Reconcile_PausingOneTool_RemovesOnlyThatNodeAndItsLink()
        {
            GraphModel g = Eva();
            int nodes = g.Nodes.Count, links = g.Links.Count;
            List<string> removed = new List<string>();
            g.NodeRemoved += n => removed.Add(n.Id);
            ServerInfo[] inv = SampleInventory.Eva();
            inv[2] = inv[2].WithToolPaused("fates_desktop__screenshot", true);

            Assert.IsTrue(g.Reconcile(inv));
            CollectionAssert.AreEqual(new[] { "tool:fates_desktop__screenshot" }, removed);
            Assert.AreEqual(nodes - 1, g.Nodes.Count);
            Assert.AreEqual(links - 1, g.Links.Count);
            Assert.IsTrue(g.TryGetNode("srv:fates_desktop", out _));
            Assert.IsFalse(g.TryGetLink("srv:fates_desktop", "tool:fates_desktop__screenshot", out _));
        }

        [Test]
        public void Reconcile_ResumingAServer_AddsItBackOnceWithItsLinks()
        {
            GraphModel g = new GraphModel("EVA", EvaWith("robinhood", true));
            List<string> added = new List<string>();
            List<string> links = new List<string>();
            g.NodeAdded += n => added.Add(n.Id);
            g.LinkAdded += l => links.Add(l.TargetId);

            Assert.IsTrue(g.Reconcile(SampleInventory.Eva()));
            Assert.AreEqual(6, added.Count);
            Assert.AreEqual("srv:robinhood", added[0], "the server before its tools");
            Assert.AreEqual(6, links.Count);
            Assert.IsTrue(g.TryGetLink("agent", "srv:robinhood", out _));
            Assert.IsTrue(g.TryGetLink("srv:robinhood", "tool:robinhood__get_accounts", out _));
            Assert.IsFalse(g.Reconcile(SampleInventory.Eva()));
        }

        [Test]
        public void Reconcile_AGraftedNodeSurvivesWhileItsServerStays_AndGoesWithTheServer()
        {
            GraphModel g = Eva();
            g.EnsureTool("robinhood__get_pnl", out _, out _);
            Assert.IsTrue(g.Reconcile(SampleInventory.Eva()) == false, "a poll that finds nothing new leaves the grafted tool alone");
            Assert.IsTrue(g.TryGetNode("tool:robinhood__get_pnl", out _));

            ServerInfo[] inv = SampleInventory.Eva();
            inv[3] = inv[3].WithToolPaused("robinhood__get_accounts", true);
            Assert.IsTrue(g.Reconcile(inv));
            Assert.IsTrue(g.TryGetNode("tool:robinhood__get_pnl", out _), "a call that is lighting a node must not lose it to a poll");

            Assert.IsTrue(g.Reconcile(EvaWith("robinhood", true)));
            Assert.IsFalse(g.TryGetNode("tool:robinhood__get_pnl", out _), "it leaves with its whole server");
            Assert.IsFalse(g.TryGetNode("srv:robinhood", out _));
        }

        [Test]
        public void Reconcile_AServerThatGoesOffline_LeavesLikeAPausedOne()
        {
            GraphModel g = Eva();
            ServerInfo[] inv = SampleInventory.Eva();
            inv[3] = new ServerInfo("robinhood", null, ServerInfo.StatusError, new ToolInfo[0], false, "down", null, null);
            Assert.IsTrue(g.Reconcile(inv));
            Assert.IsFalse(g.TryGetNode("srv:robinhood", out _));
            Assert.IsFalse(g.TryGetNode("tool:robinhood__get_accounts", out _));
        }

        [Test]
        public void SampleInventory_WithSettings_HasASamePausedGithubAndOfflineMacbook_ThatTheGraphDoesNotDraw()
        {
            ServerInfo[] all = SampleInventory.EvaWithSettings();
            Assert.AreEqual(9, all.Length);
            GraphModel withSettings = new GraphModel("EVA", all);
            Assert.AreEqual(Eva().Nodes.Count, withSettings.Nodes.Count, "paused github and offline macbook are list-only");
            Assert.IsFalse(withSettings.TryGetNode("srv:github", out _));
            Assert.IsFalse(withSettings.TryGetNode("srv:fates_macbook", out _));
        }

        [Test]
        public void SampleInventory_ToolWireNamesAreUniqueAcrossServers_SoAPauseRequestNamesExactlyOneTool()
        {
            HashSet<string> seen = new HashSet<string>();
            foreach (ServerInfo s in SampleInventory.EvaWithSettings())
            {
                foreach (ToolInfo t in s.ToolDetails) Assert.IsTrue(seen.Add(t.WireName), "duplicate wire name " + t.WireName);
            }
        }
    }
}
