using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.Tests
{
    /// <summary>The rules that turn a /tools reading into the graph the user sees (UNI-20).</summary>
    public class InventoryGraphTests
    {
        private static GraphModel Live() => new GraphModel("EVA", InventoryFixtures.Snapshot().Connected);

        [Test]
        public void ConnectedServersAreIn_WithAllTheirTools_AndOfflineOnesAreNotDrawnAtAll()
        {
            GraphModel g = Live();
            Assert.IsTrue(g.TryGetNode("srv:fates_desktop", out _));
            Assert.IsTrue(g.TryGetNode("srv:eva", out _), "the built-in eva server is a server like any other");
            Assert.IsFalse(g.TryGetNode("srv:fates_macbook", out _), "an errored server is left out, not drawn red");
            Assert.IsFalse(g.TryGetNode("srv:nostatus", out _));
            foreach (GraphNode n in g.Nodes) Assert.IsFalse(n.IsDown, n.Id + " must not be drawn as down");
            foreach (GraphLink l in g.Links) Assert.IsFalse(l.IsDown);

            // agent + 4 servers + (2 + 3 + 1 + 2) tools
            Assert.AreEqual(1 + 4 + 8, g.Nodes.Count);
            Assert.AreEqual(4 + 8, g.Links.Count);
        }

        [Test]
        public void APausedServerIsStillInTheGraphWithAllItsTools_AndCarriesTheFlag()
        {
            GraphModel g = Live();
            Assert.IsTrue(g.TryGetNode("srv:github", out GraphNode github));
            Assert.IsTrue(github.IsPaused);
            Assert.IsTrue(g.TryGetNode("tool:github__create_issue", out GraphNode t1));
            Assert.IsTrue(g.TryGetNode("tool:github__get_me", out GraphNode t2));
            Assert.IsTrue(t1.IsPaused && t2.IsPaused, "every tool of a paused server is paused");

            Assert.IsTrue(g.TryGetNode("srv:fates_desktop", out GraphNode desktop));
            Assert.IsFalse(desktop.IsPaused);
            Assert.IsTrue(g.TryGetNode("tool:fates_desktop__run_command", out GraphNode paused));
            Assert.IsTrue(paused.IsPaused, "a single paused tool on a live server");
            Assert.IsTrue(g.TryGetNode("tool:fates_desktop__vault_read", out GraphNode live));
            Assert.IsFalse(live.IsPaused);
        }

        [Test]
        public void ToolIdsMatchWhatTheWireEventsResolveTo_SoALiveCallLightsTheBuiltNode()
        {
            ConsoleReducer r = new ConsoleReducer(Live(), () => 0);
            int before = r.Graph.Nodes.Count;
            List<string> fired = new List<string>();
            r.NodeFired += id => fired.Add(id);

            r.Apply(AgentEvent.ToolStart("fates_desktop__vault_read", "{}", "a"));
            r.Apply(AgentEvent.ToolStart("github__get_me", "{}", "b"));      // built from a name with no short_name
            r.Apply(AgentEvent.ToolStart("eva__kv_set", "{}", "c"));          // the built-in server
            r.Apply(AgentEvent.ToolStart("fate_ubuntu_server__run_command", "{}", "d")); // same bare name as the desktop's

            Assert.AreEqual(before, r.Graph.Nodes.Count, "no duplicate was grafted");
            CollectionAssert.AreEqual(
                new[] { "tool:fates_desktop__vault_read", "tool:github__get_me", "tool:eva__kv_set", "tool:fate_ubuntu_server__run_command" },
                fired);
            Assert.IsTrue(r.Graph.TryGetNode("tool:fates_desktop__vault_read", out GraphNode n));
            Assert.IsFalse(n.Grafted);
            Assert.AreEqual("vault_read", n.Label, "the label is the bare name");
        }

        [Test]
        public void ABareWireName_ResolvesToTheFirstServerThatListsIt()
        {
            GraphModel g = Live();
            Assert.AreEqual("eva", g.Split("kv_set").Server);
            Assert.AreEqual("fates_desktop", g.Split("run_command").Server, "the desktop is listed before the ubuntu server");
        }

        [Test]
        public void TheMemoryFlag_FollowsTheServersNameAsItAlwaysDid()
        {
            Assert.IsTrue(ToolsWire.TryParse(
                "{\"servers\":[{\"name\":\"memory\",\"status\":\"connected\",\"tools\":[{\"name\":\"memory__vault_read\",\"short_name\":\"vault_read\"}]},"
                + "{\"name\":\"fates_desktop\",\"status\":\"connected\",\"tools\":[{\"name\":\"fates_desktop__vault_read\",\"short_name\":\"vault_read\"}]}]}",
                out InventorySnapshot snap));
            GraphModel g = new GraphModel("EVA", snap.Connected);

            Assert.IsTrue(g.TryGetNode("srv:memory", out GraphNode srv));
            Assert.IsTrue(srv.IsMemory);
            Assert.IsTrue(g.TryGetNode("tool:memory__vault_read", out GraphNode mem));
            Assert.IsTrue(mem.IsMemory);
            Assert.IsTrue(g.TryGetNode("tool:fates_desktop__vault_read", out GraphNode other));
            Assert.IsFalse(other.IsMemory, "same rule as a grafted node: only a server called memory is the memory server");
            Assert.IsTrue(g.IsMemoryServer("memory"));
            Assert.IsFalse(g.IsMemoryServer("fates_desktop"));
        }

        [Test]
        public void ApplyPaused_UpdatesNodesInPlace_WithoutARebuild()
        {
            GraphModel g = Live();
            int rebuilt = 0;
            g.Rebuilt += () => rebuilt++;
            GraphNode desktopBefore;
            g.TryGetNode("srv:fates_desktop", out desktopBefore);

            ServerInfo unpausedGithub = new ServerInfo("github", null, "connected", new[] { new ToolInfo("create_issue"), new ToolInfo("get_me") }, false, null, null, null);
            ServerInfo pausedDesktop = new ServerInfo("fates_desktop", null, "connected",
                new[] { new ToolInfo("vault_read"), new ToolInfo("vault_search"), new ToolInfo("run_command") }, true, null, null, null);
            Assert.IsTrue(g.ApplyPaused(new[] { unpausedGithub, pausedDesktop }));

            Assert.AreEqual(0, rebuilt);
            g.TryGetNode("srv:github", out GraphNode github);
            Assert.IsFalse(github.IsPaused);
            g.TryGetNode("tool:github__get_me", out GraphNode getMe);
            Assert.IsFalse(getMe.IsPaused);
            g.TryGetNode("srv:fates_desktop", out GraphNode desktop);
            Assert.AreSame(desktopBefore, desktop, "the very same node object, so its layout position is untouched");
            Assert.IsTrue(desktop.IsPaused);
            g.TryGetNode("tool:fates_desktop__vault_read", out GraphNode vault);
            Assert.IsTrue(vault.IsPaused);

            Assert.IsFalse(g.ApplyPaused(new[] { unpausedGithub, pausedDesktop }), "nothing left to change");
            Assert.IsFalse(g.ApplyPaused(null));
        }

        [Test]
        public void TheOldConstructor_StillBuildsTheSameServerWithDefaultFlags()
        {
            ServerInfo s = new ServerInfo("x", null, null, "a", "b");
            Assert.AreEqual(ServerInfo.StatusConnected, s.Status);
            CollectionAssert.AreEqual(new[] { "a", "b" }, s.Tools);
            Assert.AreEqual(2, s.ToolDetails.Count);
            Assert.IsFalse(s.Paused);
            Assert.IsNull(s.Error);
            Assert.IsNull(s.TokensEst);
            Assert.IsTrue(s.IsConnected);
        }
    }

    public class InventoryControllerTests
    {
        private sealed class FakeSource : IInventorySource
        {
            public readonly List<TaskCompletionSource<InventorySnapshot>> Calls = new List<TaskCompletionSource<InventorySnapshot>>();

            public Task<InventorySnapshot> FetchAsync(CancellationToken ct)
            {
                TaskCompletionSource<InventorySnapshot> tcs = new TaskCompletionSource<InventorySnapshot>();
                Calls.Add(tcs);
                return tcs.Task;
            }
        }

        private sealed class Rig
        {
            public readonly FakeSource Source = new FakeSource();
            public readonly GraphModel Graph = new GraphModel("EVA");
            public readonly InventoryController Controller;
            public double Now;
            public int Rebuilds;

            public Rig()
            {
                Controller = new InventoryController(Source, Graph, () => Now);
                Graph.Rebuilt += () => Rebuilds++;
            }

            // The completion queues onto the controller; only Pump applies it.
            public void Finish(int call, InventorySnapshot s) { Source.Calls[call].SetResult(s); Thread.Sleep(20); }
            public void Fail(int call, string message) { Source.Calls[call].SetException(new InvalidOperationException(message)); Thread.Sleep(20); }
        }

        private static InventorySnapshot Snap(params ServerInfo[] servers) => InventorySnapshot.From(servers);

        private static ServerInfo Srv(string name, params string[] tools) => new ServerInfo(name, null, "connected", tools);

        [Test]
        public void NoSource_IsUnavailable_AndEverythingIsANoOp()
        {
            GraphModel g = new GraphModel("EVA", SampleInventory.Eva());
            int nodes = g.Nodes.Count;
            InventoryController c = new InventoryController(null, g);
            Assert.IsFalse(c.IsAvailable);
            Assert.AreEqual(InventoryPhase.Unavailable, c.Phase);
            c.Refresh();
            c.RequestRefresh();
            c.Update(false);
            Assert.IsFalse(c.Pump());
            Assert.AreEqual(nodes, g.Nodes.Count, "the replay keeps its sample graph");
            Assert.AreEqual(0, c.Omitted.Count);
            c.Dispose();
        }

        [Test]
        public void TheResultIsAppliedOnPump_NotWhenTheFetchCompletes()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            Assert.AreEqual(InventoryPhase.Loading, r.Controller.Phase);

            r.Finish(0, Snap(Srv("a", "x", "y")));
            Assert.AreEqual(0, r.Rebuilds, "a pool thread never touches the graph");
            Assert.AreEqual(1, r.Graph.Nodes.Count);

            Assert.IsTrue(r.Controller.Pump());
            Assert.AreEqual(1, r.Rebuilds);
            Assert.AreEqual(1 + 1 + 2, r.Graph.Nodes.Count);
            Assert.AreEqual(InventoryPhase.Ready, r.Controller.Phase);
            Assert.IsNull(r.Controller.Error);
            Assert.IsFalse(r.Controller.Pump(), "nothing more to apply");
        }

        [Test]
        public void AnUnchangedSnapshot_DoesNotRebuild()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            r.Finish(0, Snap(Srv("a", "x", "y"), Srv("b", "z")));
            r.Controller.Pump();
            Assert.AreEqual(1, r.Rebuilds);
            r.Graph.TryGetNode("tool:a__x", out GraphNode before);

            r.Controller.Refresh();
            r.Finish(1, Snap(Srv("b", "z"), Srv("a", "y", "x"))); // same set, different order
            r.Controller.Pump();
            Assert.AreEqual(1, r.Rebuilds, "the layout and hand-placed nodes survive a poll that found nothing new");
            r.Graph.TryGetNode("tool:a__x", out GraphNode after);
            Assert.AreSame(before, after);
            Assert.AreEqual(1, r.Controller.RebuildCount);
        }

        [Test]
        public void AChangedSnapshot_RebuildsOnce()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            r.Finish(0, Snap(Srv("a", "x")));
            r.Controller.Pump();

            r.Controller.Refresh();
            r.Finish(1, Snap(Srv("a", "x", "newtool"), Srv("b", "z")));
            r.Controller.Pump();
            Assert.AreEqual(2, r.Rebuilds);
            Assert.IsTrue(r.Graph.TryGetNode("tool:a__newtool", out _));
            Assert.IsTrue(r.Graph.TryGetNode("srv:b", out _));

            r.Controller.Refresh();
            r.Finish(2, Snap(Srv("b", "z"))); // a went offline
            r.Controller.Pump();
            Assert.AreEqual(3, r.Rebuilds);
            Assert.IsFalse(r.Graph.TryGetNode("srv:a", out _));
        }

        [Test]
        public void ThePausedFlagChanges_ApplyInPlace_WithoutARebuild()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            r.Finish(0, Snap(Srv("a", "x")));
            r.Controller.Pump();
            r.Graph.TryGetNode("tool:a__x", out GraphNode node);
            Assert.IsFalse(node.IsPaused);

            r.Controller.Refresh();
            r.Finish(1, Snap(new ServerInfo("a", null, "connected", new[] { new ToolInfo("x") }, true, null, null, null)));
            r.Controller.Pump();
            Assert.AreEqual(1, r.Rebuilds);
            Assert.IsTrue(node.IsPaused);
        }

        [Test]
        public void AFirstReadingWithNothingConnected_LeavesTheEmptyGraphAlone()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            r.Finish(0, Snap(new ServerInfo("mac", null, "error", new ToolInfo[0], false, "boom", null, null)));
            r.Controller.Pump();
            Assert.AreEqual(0, r.Rebuilds);
            Assert.AreEqual(1, r.Graph.Nodes.Count);
            Assert.AreEqual(1, r.Controller.Omitted.Count);
            Assert.AreEqual("mac", r.Controller.Omitted[0].Name);
            Assert.AreEqual("boom", r.Controller.Omitted[0].Error, "available for a UI later");
        }

        [Test]
        public void AFailedFetch_KeepsTheGraphAndTheLastSnapshot_AndSaysWhy()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            r.Finish(0, Snap(Srv("a", "x")));
            r.Controller.Pump();
            int nodes = r.Graph.Nodes.Count;
            InventorySnapshot good = r.Controller.Snapshot;

            r.Controller.Refresh();
            r.Fail(1, "Could not reach EVA for the tool list: refused");
            Assert.IsTrue(r.Controller.Pump());
            Assert.AreEqual(InventoryPhase.Failed, r.Controller.Phase);
            Assert.AreEqual("Could not reach EVA for the tool list: refused", r.Controller.Error);
            Assert.AreEqual(1, r.Rebuilds, "a failure never rebuilds");
            Assert.AreEqual(nodes, r.Graph.Nodes.Count);
            Assert.AreSame(good, r.Controller.Snapshot);

            r.Controller.Refresh();
            r.Finish(2, Snap(Srv("a", "x")));
            r.Controller.Pump();
            Assert.AreEqual(InventoryPhase.Ready, r.Controller.Phase);
            Assert.IsNull(r.Controller.Error, "a good reading clears the error");
        }

        [Test]
        public void ASourceThatThrowsSynchronously_IsAFailure_NotACrash()
        {
            GraphModel g = new GraphModel("EVA");
            InventoryController c = new InventoryController(new ThrowingSource(), g);
            c.Refresh();
            Thread.Sleep(20);
            c.Pump();
            Assert.AreEqual(InventoryPhase.Failed, c.Phase);
            Assert.AreEqual("boom", c.Error);
        }

        private sealed class ThrowingSource : IInventorySource
        {
            public Task<InventorySnapshot> FetchAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
        }

        [Test]
        public void ARebuildIsHeldBackWhileARunIsInFlight_AndAppliedWhenItEnds()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            r.Finish(0, Snap(Srv("a", "x")));

            r.Controller.Pump(runInFlight: true);
            Assert.AreEqual(0, r.Rebuilds, "no node is pulled out from under a lit tool call");
            Assert.AreEqual(InventoryPhase.Ready, r.Controller.Phase, "the reading itself is accepted");

            r.Controller.Pump(runInFlight: false);
            Assert.AreEqual(1, r.Rebuilds);
        }

        [Test]
        public void Update_FetchesAtOnce_ThenAtMostEverySixtySeconds_NeverWhileARunIsInFlight()
        {
            Rig r = new Rig();
            r.Now = 1000;
            r.Controller.Update(false);
            Assert.AreEqual(1, r.Source.Calls.Count, "the first idle frame fetches");
            r.Controller.Update(false);
            Assert.AreEqual(1, r.Source.Calls.Count, "not while one is in flight");

            r.Finish(0, Snap(Srv("a", "x")));
            r.Now = 1030;
            r.Controller.Update(false);
            Assert.AreEqual(1, r.Source.Calls.Count, "30 s is too soon");
            r.Now = 1061;
            r.Controller.Update(true);
            Assert.AreEqual(1, r.Source.Calls.Count, "no polling while a run is in flight");
            r.Controller.Update(false);
            Assert.AreEqual(2, r.Source.Calls.Count, "due and idle");

            r.Finish(1, Snap(Srv("a", "x")));
            r.Now = 1070;
            r.Controller.Update(false);
            Assert.AreEqual(2, r.Source.Calls.Count);
            r.Controller.RequestRefresh(); // a run just went idle
            r.Controller.Update(true);
            Assert.AreEqual(2, r.Source.Calls.Count, "the request waits for idle");
            r.Controller.Update(false);
            Assert.AreEqual(3, r.Source.Calls.Count, "idle after a run: fetch now, not in 60 s");
        }

        [Test]
        public void AFailedFetch_IsRetriedOnTheSameSixtySecondBeat()
        {
            Rig r = new Rig();
            r.Now = 0;
            r.Controller.Update(false);
            r.Fail(0, "down");
            r.Now = 59;
            r.Controller.Update(false);
            Assert.AreEqual(1, r.Source.Calls.Count);
            r.Now = 60;
            r.Controller.Update(false);
            Assert.AreEqual(2, r.Source.Calls.Count);
        }

        [Test]
        public void ARefreshWhileOneIsRunning_RunsAfterIt_NotInParallel()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            r.Controller.Refresh();
            Assert.AreEqual(1, r.Source.Calls.Count);
            r.Finish(0, Snap(Srv("a", "x")));
            r.Controller.Update(false); // applies, then starts the queued one
            Assert.AreEqual(2, r.Source.Calls.Count);
        }
    }
}
