using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.Tests
{
    /// <summary>Builders shared by the inventory tests. Tools carry wire names (server__tool) and 10 tokens each unless said otherwise.</summary>
    internal static class Inv
    {
        public static ServerInfo Srv(string name, params string[] tools)
        {
            ToolInfo[] infos = new ToolInfo[tools.Length];
            for (int i = 0; i < tools.Length; i++) infos[i] = new ToolInfo(tools[i], false, 10, name + "__" + tools[i]);
            return new ServerInfo(name, null, "connected", infos, false, null, null, null);
        }

        /// <summary>Server a with tools x (10 tokens) and y (20 tokens); the totals are the sum of the tools.</summary>
        public static ServerInfo A(bool paused = false, bool xPaused = false) =>
            new ServerInfo("a", null, "connected",
                new[] { new ToolInfo("x", xPaused, 10, "a__x"), new ToolInfo("y", false, 20, "a__y") }, paused, null, 30, 30);

        public static ServerInfo B(bool paused = false) => Srv("b", "z").WithPaused(paused);

        public static ServerInfo Offline(string name, string error) =>
            new ServerInfo(name, null, ServerInfo.StatusError, new ToolInfo[0], false, error, null, null);

        public static InventorySnapshot Snap(params ServerInfo[] servers) => InventorySnapshot.From(servers);
    }

    /// <summary>
    /// The rules that turn a /tools reading into the graph the user sees (UNI-12): a server is drawn only when it is connected
    /// AND not paused, a tool only when it is not paused. Paused and offline things are in the Settings list, never the graph.
    /// </summary>
    public class InventoryGraphTests
    {
        private static GraphModel Live() => new GraphModel("EVA", InventoryFixtures.Snapshot().All);

        [Test]
        public void OnlyConnectedUnpausedServersAreIn_WithTheirUnpausedTools()
        {
            GraphModel g = Live();
            Assert.IsTrue(g.TryGetNode("srv:fates_desktop", out _));
            Assert.IsTrue(g.TryGetNode("srv:eva", out _), "the built-in eva server is a server like any other");
            Assert.IsTrue(g.TryGetNode("srv:fate_ubuntu_server", out _));
            foreach (GraphNode n in g.Nodes) Assert.IsFalse(n.IsDown, n.Id + " must not be drawn as down");
            foreach (GraphLink l in g.Links) Assert.IsFalse(l.IsDown);

            // agent + 3 servers + (2 + 2 + 1) tools; the desktop's run_command is paused
            Assert.AreEqual(1 + 3 + 5, g.Nodes.Count);
            Assert.AreEqual(3 + 5, g.Links.Count);
        }

        [Test]
        public void APausedServerIsAbsentFromTheGraph_WithAllItsTools()
        {
            GraphModel g = Live();
            Assert.IsFalse(g.TryGetNode("srv:github", out _), "github is connected but paused");
            Assert.IsFalse(g.TryGetNode("tool:github__create_issue", out _));
            Assert.IsFalse(g.TryGetNode("tool:github__get_me", out _));
            Assert.IsFalse(g.TryGetLink("agent", "srv:github", out _));
        }

        [Test]
        public void ADisconnectedServerIsAbsentFromTheGraph_AndSoIsOneWithNoStatus()
        {
            GraphModel g = Live();
            Assert.IsFalse(g.TryGetNode("srv:fates_macbook", out _), "an errored server is left out, not drawn red");
            Assert.IsFalse(g.TryGetNode("srv:nostatus", out _));
            Assert.IsFalse(g.TryGetNode("tool:nostatus__x", out _));
        }

        [Test]
        public void APausedToolIsAbsent_WhileItsSiblingsAndSameNamedToolsElsewhereStay()
        {
            GraphModel g = Live();
            Assert.IsFalse(g.TryGetNode("tool:fates_desktop__run_command", out _));
            Assert.IsTrue(g.TryGetNode("tool:fates_desktop__vault_read", out _));
            Assert.IsTrue(g.TryGetNode("tool:fates_desktop__vault_search", out _));
            Assert.IsTrue(g.TryGetNode("tool:fate_ubuntu_server__run_command", out _), "same bare name, other server, not paused");
            Assert.IsTrue(g.TryGetNode("srv:fates_desktop", out _), "the server itself is not paused");
        }

        [Test]
        public void UnpausingAddsTheServerAndItsToolsBack_ThroughReconcile_WithoutARebuild()
        {
            GraphModel g = Live();
            int rebuilt = 0;
            List<string> added = new List<string>();
            g.Rebuilt += () => rebuilt++;
            g.NodeAdded += n => added.Add(n.Id);
            InventorySnapshot s = InventoryFixtures.Snapshot();

            // The desktop's paused tool comes back.
            List<ServerInfo> inv = new List<ServerInfo>(s.All);
            inv[1] = inv[1].WithToolPaused("fates_desktop__run_command", false);
            Assert.IsTrue(g.Reconcile(inv));
            CollectionAssert.AreEqual(new[] { "tool:fates_desktop__run_command" }, added);
            Assert.IsTrue(g.TryGetLink("srv:fates_desktop", "tool:fates_desktop__run_command", out _));

            // Resuming github brings the server back; its own tools are still paused, so it comes without them.
            added.Clear();
            inv[4] = inv[4].WithPaused(false);
            Assert.IsTrue(g.Reconcile(inv));
            CollectionAssert.AreEqual(new[] { "srv:github" }, added);
            Assert.IsTrue(g.TryGetLink("agent", "srv:github", out _));
            Assert.IsFalse(g.TryGetNode("tool:github__get_me", out _));

            // And its tools.
            added.Clear();
            inv[4] = inv[4].WithToolPaused("github__create_issue", false).WithToolPaused("github__get_me", false);
            Assert.IsTrue(g.Reconcile(inv));
            CollectionAssert.AreEqual(new[] { "tool:github__create_issue", "tool:github__get_me" }, added);

            Assert.AreEqual(0, rebuilt);
            Assert.IsFalse(g.Reconcile(inv), "nothing left to change");
        }

        [Test]
        public void AServerThatGoesOfflineLeavesTheGraphWithItsTools_AndComesBackWhenReconnected()
        {
            GraphModel g = new GraphModel("EVA", new[] { Inv.A(), Inv.B() });
            Assert.IsTrue(g.Reconcile(new[] { Inv.A(), Inv.Offline("b", "boom") }));
            Assert.IsFalse(g.TryGetNode("srv:b", out _));
            Assert.IsFalse(g.TryGetNode("tool:b__z", out _));
            Assert.IsTrue(g.TryGetNode("srv:a", out _));

            Assert.IsTrue(g.Reconcile(new[] { Inv.A(), Inv.B() }));
            Assert.IsTrue(g.TryGetNode("srv:b", out _));
            Assert.IsTrue(g.TryGetNode("tool:b__z", out _));
        }

        [Test]
        public void EveryLiveToolsWireNameResolvesToTheNodeBuiltFromTheInventory()
        {
            GraphModel g = Live();
            int checkedTools = 0;
            foreach (ServerInfo s in InventoryFixtures.Snapshot().All)
            {
                if (!s.IsLive) continue;
                foreach (ToolInfo t in s.ToolDetails)
                {
                    if (t.Paused) continue;
                    ToolRef r = g.Split(t.WireName);
                    Assert.AreEqual(s.Name, r.Server, t.WireName);
                    Assert.IsTrue(g.TryGetNode(GraphModel.ToolId(r.Server, r.Tool), out GraphNode n), t.WireName + " has no node");
                    Assert.IsFalse(n.Grafted, t.WireName);
                    Assert.AreEqual(t.Name, n.Label, "the label is the bare name");
                    checkedTools++;
                }
            }
            Assert.AreEqual(5, checkedTools);
        }

        [Test]
        public void ALiveCallToAnInventoryTool_LightsThePrebuiltNode_AndGraftsNothing()
        {
            ConsoleReducer r = new ConsoleReducer(Live(), () => 0);
            int before = r.Graph.Nodes.Count;
            List<string> fired = new List<string>();
            r.NodeFired += id => fired.Add(id);

            r.Apply(AgentEvent.ToolStart("fates_desktop__vault_read", "{}", "a"));
            r.Apply(AgentEvent.ToolStart("eva__kv_set", "{}", "c"));
            r.Apply(AgentEvent.ToolStart("fate_ubuntu_server__run_command", "{}", "d")); // same bare name as the desktop's

            Assert.AreEqual(before, r.Graph.Nodes.Count, "no duplicate was grafted");
            CollectionAssert.AreEqual(
                new[] { "tool:fates_desktop__vault_read", "tool:eva__kv_set", "tool:fate_ubuntu_server__run_command" }, fired);
            Assert.IsTrue(r.Graph.TryGetNode("tool:fates_desktop__vault_read", out GraphNode n));
            Assert.IsFalse(n.Grafted);
            Assert.AreEqual("vault_read", n.Label, "the label is the bare name");
        }

        [Test]
        public void ABareWireName_ResolvesToTheFirstLiveServerThatListsIt()
        {
            GraphModel g = Live();
            Assert.AreEqual("eva", g.Split("kv_set").Server);
            Assert.AreEqual("fate_ubuntu_server", g.Split("run_command").Server, "the desktop lists it first, but its run_command is paused");
        }

        [Test]
        public void TheMemoryFlag_FollowsTheServersNameAsItAlwaysDid()
        {
            Assert.IsTrue(ToolsWire.TryParse(
                "{\"servers\":[{\"name\":\"memory\",\"status\":\"connected\",\"tools\":[{\"name\":\"memory__vault_read\",\"short_name\":\"vault_read\"}]},"
                + "{\"name\":\"fates_desktop\",\"status\":\"connected\",\"tools\":[{\"name\":\"fates_desktop__vault_read\",\"short_name\":\"vault_read\"}]}]}",
                out InventorySnapshot snap));
            GraphModel g = new GraphModel("EVA", snap.All);

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
        public void ThePausedState_NeverChangesTheIdentityOfTheNodesThatStay()
        {
            GraphModel g = new GraphModel("EVA", new[] { Inv.A(), Inv.B() });
            g.TryGetNode("srv:a", out GraphNode aBefore);
            g.TryGetNode("tool:a__y", out GraphNode yBefore);
            Assert.IsTrue(g.Reconcile(new[] { Inv.A(xPaused: true), Inv.B(paused: true) }));
            g.TryGetNode("srv:a", out GraphNode aAfter);
            g.TryGetNode("tool:a__y", out GraphNode yAfter);
            Assert.AreSame(aBefore, aAfter, "the very same node object, so its layout position is untouched");
            Assert.AreSame(yBefore, yAfter);
            Assert.IsFalse(g.TryGetNode("tool:a__x", out _));
            Assert.IsFalse(g.TryGetNode("srv:b", out _));
        }
    }

    public class ServerInfoTests
    {
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
            Assert.IsTrue(s.IsLive);
        }

        [Test]
        public void IsLive_NeedsConnectedAndNotPaused()
        {
            Assert.IsTrue(Inv.A().IsLive);
            Assert.IsFalse(Inv.A(paused: true).IsLive);
            Assert.IsFalse(Inv.Offline("m", "boom").IsLive);
            Assert.IsFalse(Inv.Offline("m", "boom").WithPaused(true).IsLive);
        }

        [Test]
        public void ActiveCountsAndTokens_FollowTheToolsAndTheServersState()
        {
            ServerInfo a = Inv.A();
            Assert.AreEqual(2, a.ActiveToolCount);
            Assert.AreEqual(30, a.TotalTokens);
            Assert.AreEqual(30, a.ActiveTokens);

            ServerInfo xOff = a.WithToolPaused("a__x", true);
            Assert.AreEqual(1, xOff.ActiveToolCount);
            Assert.AreEqual(20, xOff.ActiveTokens, "an optimistic tool toggle moves the number at once");
            Assert.AreEqual(30, xOff.TotalTokens, "the total does not");

            ServerInfo off = a.WithPaused(true);
            Assert.AreEqual(0, off.ActiveToolCount);
            Assert.AreEqual(0, off.ActiveTokens);
            Assert.AreEqual(2, off.ToolCount, "the tools are still listed");
        }

        [Test]
        public void WithToolPaused_ReturnsTheSameInstanceWhenNothingChanges_AndMatchesByWireName()
        {
            ServerInfo a = Inv.A();
            Assert.AreSame(a, a.WithToolPaused("a__x", false), "already that way");
            Assert.AreSame(a, a.WithToolPaused("x", true), "a bare label is not the wire name");
            Assert.AreSame(a, a.WithToolPaused("nope", true));
            Assert.IsTrue(a.WithToolPaused("a__x", true).ToolDetails[0].Paused);
            Assert.IsFalse(a.ToolDetails[0].Paused, "the original is untouched");
        }
    }

    public class InventoryFormatTests
    {
        [Test]
        public void Tokens_AreCompact_AndEmptyWhenNotReported()
        {
            Assert.AreEqual("~94 tok", InventoryFormat.Tokens(94));
            Assert.AreEqual("~14.7k tok", InventoryFormat.Tokens(14715));
            Assert.AreEqual(string.Empty, InventoryFormat.Tokens(null));
        }

        [Test]
        public void StatusWord_SaysOnPausedOrOffline()
        {
            Assert.AreEqual("on", InventoryFormat.StatusWord(Inv.A()));
            Assert.AreEqual("paused", InventoryFormat.StatusWord(Inv.A(paused: true)));
            Assert.AreEqual("offline", InventoryFormat.StatusWord(Inv.Offline("m", "boom")));
        }
    }

    public class InMemoryInventoryTests
    {
        private static ServerInfo Find(InventorySnapshot s, string name)
        {
            foreach (ServerInfo x in s.All)
            {
                if (x.Name == name) return x;
            }
            return null;
        }

        // The tasks below are already completed (that is the point of the class), so reading .Result cannot block or deadlock;
        // every assertion on IsCompleted comes first.
        [Test]
        public void Fetch_CompletesSynchronously_WithEveryServer()
        {
            InMemoryInventory inv = new InMemoryInventory(SampleInventory.EvaWithSettings());
            Task<InventorySnapshot> t = inv.FetchAsync(CancellationToken.None);
            Assert.IsTrue(t.IsCompleted, "no thread pool in the browser build");
            Assert.AreEqual(9, t.Result.All.Count, "7 live + paused github + offline fates_macbook");
            Assert.IsTrue(Find(t.Result, "github").Paused);
            Assert.IsFalse(Find(t.Result, "fates_macbook").IsConnected);
        }

        [Test]
        public void PauseAndUnpauseAServer_RoundTrips_AndCompletesSynchronously()
        {
            InMemoryInventory inv = new InMemoryInventory(SampleInventory.EvaWithSettings());

            Task<InventorySnapshot> pause = inv.SetPausedAsync(new Dictionary<string, bool> { { "robinhood", true } }, null, CancellationToken.None);
            Assert.IsTrue(pause.IsCompleted);
            Assert.IsTrue(Find(pause.Result, "robinhood").Paused);
            Assert.AreEqual(0, pause.Result.Unknown.Count);
            Assert.IsTrue(Find(inv.FetchAsync(CancellationToken.None).Result, "robinhood").Paused, "the next reading remembers it");

            Task<InventorySnapshot> resume = inv.SetPausedAsync(new Dictionary<string, bool> { { "robinhood", false } }, null, CancellationToken.None);
            Assert.IsTrue(resume.IsCompleted);
            Assert.IsFalse(Find(resume.Result, "robinhood").Paused);
            Assert.IsTrue(Find(resume.Result, "robinhood").IsLive);
        }

        [Test]
        public void PausingATool_ByWireName_TouchesOnlyThatServersTool()
        {
            InMemoryInventory inv = new InMemoryInventory(SampleInventory.EvaWithSettings());
            Task<InventorySnapshot> t = inv.SetPausedAsync(null, new Dictionary<string, bool> { { "fates_desktop__run_command", true } }, CancellationToken.None);
            Assert.IsTrue(t.IsCompleted);
            ServerInfo desktop = Find(t.Result, "fates_desktop");
            ServerInfo ubuntu = Find(t.Result, "fate_ubuntu_server");
            Assert.IsTrue(desktop.ToolDetails[0].Paused);
            Assert.AreEqual("run_command", desktop.ToolDetails[0].Name);
            Assert.IsFalse(ubuntu.ToolDetails[0].Paused, "both servers list a run_command; only the desktop's was named");
            Assert.AreEqual(0, t.Result.Unknown.Count);
        }

        [Test]
        public void NamesItDoesNotKnow_AreReportedAsUnknown_AndChangeNothing()
        {
            InMemoryInventory inv = new InMemoryInventory(SampleInventory.EvaWithSettings());
            Task<InventorySnapshot> t = inv.SetPausedAsync(
                new Dictionary<string, bool> { { "nope", true } },
                new Dictionary<string, bool> { { "run_command", true }, { "nope__x", true } }, CancellationToken.None);
            Assert.IsTrue(t.IsCompleted);
            CollectionAssert.AreEquivalent(new[] { "nope", "run_command", "nope__x" }, t.Result.Unknown, "a bare name is not a wire name here");
            Assert.AreEqual(InventorySnapshot.From(SampleInventory.EvaWithSettings()).Fingerprint, t.Result.Fingerprint);
        }

        [Test]
        public void WithTheController_AToggleIsAppliedOnTheNextPump_WithNoWaiting()
        {
            GraphModel g = new GraphModel("EVA", SampleInventory.Eva());
            int rebuilt = 0;
            g.Rebuilt += () => rebuilt++;
            using (InventoryController c = new InventoryController(new InMemoryInventory(SampleInventory.EvaWithSettings()), g, () => 0))
            {
                c.Update(false); // starts the first reading; the task is already complete
                c.Pump();
                Assert.AreEqual(InventoryPhase.Ready, c.Phase);
                Assert.AreEqual(9, c.Servers.Count);
                Assert.IsTrue(g.TryGetNode("srv:robinhood", out _));

                c.SetServerPaused("robinhood", true);
                c.Pump();
                Assert.IsFalse(g.TryGetNode("srv:robinhood", out _));
                Assert.IsFalse(g.TryGetNode("tool:robinhood__get_accounts", out _));
                Assert.IsTrue(c.FindServer("robinhood").Paused);
                Assert.IsFalse(c.HasPending);

                c.SetServerPaused("robinhood", false);
                c.Pump();
                Assert.IsTrue(g.TryGetNode("srv:robinhood", out _));
                Assert.IsTrue(g.TryGetNode("tool:robinhood__get_accounts", out _));
                Assert.AreEqual(0, rebuilt, "toggling never rebuilds the graph");
            }
        }
    }

    public class InventoryControllerTests
    {
        /// <summary>Hands out one explicitly completed task per call, and remembers what each pause call asked for.</summary>
        private sealed class FakeSource : IInventorySource
        {
            public readonly List<TaskCompletionSource<InventorySnapshot>> Fetches = new List<TaskCompletionSource<InventorySnapshot>>();
            public readonly List<TaskCompletionSource<InventorySnapshot>> Sets = new List<TaskCompletionSource<InventorySnapshot>>();
            public readonly List<Dictionary<string, bool>> SetServers = new List<Dictionary<string, bool>>();
            public readonly List<Dictionary<string, bool>> SetTools = new List<Dictionary<string, bool>>();
            public int OutstandingSets;
            public int MaxOutstandingSets;

            public Task<InventorySnapshot> FetchAsync(CancellationToken ct)
            {
                TaskCompletionSource<InventorySnapshot> tcs = new TaskCompletionSource<InventorySnapshot>();
                Fetches.Add(tcs);
                return tcs.Task;
            }

            public Task<InventorySnapshot> SetPausedAsync(
                IReadOnlyDictionary<string, bool> servers, IReadOnlyDictionary<string, bool> tools, CancellationToken ct)
            {
                // Copies: the controller reuses nothing, but a test must see what was asked at the time of the call.
                SetServers.Add(new Dictionary<string, bool>(servers ?? new Dictionary<string, bool>()));
                SetTools.Add(new Dictionary<string, bool>(tools ?? new Dictionary<string, bool>()));
                OutstandingSets++;
                if (OutstandingSets > MaxOutstandingSets) MaxOutstandingSets = OutstandingSets;
                TaskCompletionSource<InventorySnapshot> tcs = new TaskCompletionSource<InventorySnapshot>();
                Sets.Add(tcs);
                return tcs.Task;
            }
        }

        private sealed class Rig
        {
            public readonly FakeSource Source = new FakeSource();
            public readonly GraphModel Graph = new GraphModel("EVA");
            public readonly InventoryController Controller;
            public readonly List<string> Added = new List<string>();
            public readonly List<string> Removed = new List<string>();
            public double Now;
            public int Rebuilds;

            public Rig()
            {
                Controller = new InventoryController(Source, Graph, () => Now);
                Graph.Rebuilt += () => Rebuilds++;
                Graph.NodeAdded += n => Added.Add(n.Id);
                Graph.NodeRemoved += n => Removed.Add(n.Id);
            }

            // The completion queues onto the controller; only Pump applies it. The short sleep covers a continuation
            // that was scheduled rather than run inline.
            public void FinishFetch(int call, InventorySnapshot s) { Source.Fetches[call].SetResult(s); Thread.Sleep(10); }
            public void FailFetch(int call, string message) { Source.Fetches[call].SetException(new InvalidOperationException(message)); Thread.Sleep(10); }

            public void FinishSet(int call, InventorySnapshot s)
            {
                Source.OutstandingSets--;
                Source.Sets[call].SetResult(s);
                Thread.Sleep(10);
            }

            public void FailSet(int call, string message)
            {
                Source.OutstandingSets--;
                Source.Sets[call].SetException(new InvalidOperationException(message));
                Thread.Sleep(10);
            }

            /// <summary>The first (or next) reading, applied: starts a fetch, completes it, pumps.</summary>
            public void Load(params ServerInfo[] servers)
            {
                Controller.Refresh();
                FinishFetch(Source.Fetches.Count - 1, Inv.Snap(servers));
                Controller.Pump();
            }
        }

        private static InventorySnapshot Snap(params ServerInfo[] servers) => Inv.Snap(servers);

        private static ServerInfo Srv(string name, params string[] tools) => Inv.Srv(name, tools);

        private static bool HasNode(Rig r, string id) => r.Graph.TryGetNode(id, out _);

        // ------------------------------------------------------------------ reading

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
            c.SetServerPaused("robinhood", true);
            c.SetToolPaused("robinhood__get_accounts", true);
            Assert.IsFalse(c.Pump());
            Assert.IsFalse(c.HasPending);
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

            r.FinishFetch(0, Snap(Srv("a", "x", "y")));
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
        public void AnUnchangedSnapshot_ChangesNothing()
        {
            Rig r = new Rig();
            r.Load(Srv("a", "x", "y"), Srv("b", "z"));
            Assert.AreEqual(1, r.Rebuilds);
            r.Graph.TryGetNode("tool:a__x", out GraphNode before);

            r.Load(Srv("b", "z"), Srv("a", "y", "x")); // same set, different order
            Assert.AreEqual(1, r.Rebuilds, "the layout and hand-placed nodes survive a poll that found nothing new");
            Assert.AreEqual(0, r.Added.Count);
            Assert.AreEqual(0, r.Removed.Count);
            r.Graph.TryGetNode("tool:a__x", out GraphNode after);
            Assert.AreSame(before, after);
            Assert.AreEqual(1, r.Controller.RebuildCount);
        }

        [Test]
        public void AChangedSnapshot_IsAppliedIncrementally_NeverAsARebuild()
        {
            Rig r = new Rig();
            r.Load(Srv("a", "x"));
            r.Graph.TryGetNode("srv:a", out GraphNode aBefore);

            r.Load(Srv("a", "x", "newtool"), Srv("b", "z"));
            Assert.AreEqual(1, r.Rebuilds);
            CollectionAssert.AreEqual(new[] { "tool:a__newtool", "srv:b", "tool:b__z" }, r.Added);
            Assert.IsTrue(r.Graph.TryGetNode("srv:a", out GraphNode aAfter));
            Assert.AreSame(aBefore, aAfter);

            r.Load(Srv("b", "z")); // a went offline
            Assert.AreEqual(1, r.Rebuilds);
            Assert.IsFalse(HasNode(r, "srv:a"));
            Assert.IsFalse(HasNode(r, "tool:a__x"));
            Assert.AreEqual(1, r.Controller.RebuildCount);
        }

        [Test]
        public void AFirstReadingWithNothingLive_LeavesTheEmptyGraphAlone_AndTheListHasEverything()
        {
            Rig r = new Rig();
            r.Load(Inv.Offline("mac", "boom"), Srv("gh", "get_me").WithPaused(true));
            Assert.AreEqual(0, r.Rebuilds);
            Assert.AreEqual(1, r.Graph.Nodes.Count);
            Assert.AreEqual(1, r.Controller.Omitted.Count);
            Assert.AreEqual("mac", r.Controller.Omitted[0].Name);
            Assert.AreEqual("boom", r.Controller.Omitted[0].Error);
            Assert.AreEqual(2, r.Controller.Servers.Count, "Settings lists the paused one too");

            r.Load(Inv.Offline("mac", "boom"), Srv("gh", "get_me")); // resumed elsewhere
            Assert.AreEqual(1, r.Rebuilds, "the first live server builds the graph");
            Assert.IsTrue(HasNode(r, "srv:gh"));
        }

        [Test]
        public void AFailedFetch_KeepsTheGraphAndTheLastSnapshot_AndSaysWhy()
        {
            Rig r = new Rig();
            r.Load(Srv("a", "x"));
            int nodes = r.Graph.Nodes.Count;
            InventorySnapshot good = r.Controller.Snapshot;

            r.Controller.Refresh();
            r.FailFetch(1, "Could not reach EVA for the tool list: refused");
            Assert.IsTrue(r.Controller.Pump());
            Assert.AreEqual(InventoryPhase.Failed, r.Controller.Phase);
            Assert.AreEqual("Could not reach EVA for the tool list: refused", r.Controller.Error);
            Assert.AreEqual(1, r.Rebuilds, "a failure never rebuilds");
            Assert.AreEqual(nodes, r.Graph.Nodes.Count);
            Assert.AreSame(good, r.Controller.Snapshot);

            r.Load(Srv("a", "x"));
            Assert.AreEqual(InventoryPhase.Ready, r.Controller.Phase);
            Assert.IsNull(r.Controller.Error, "a good reading clears the error");
        }

        private sealed class ThrowingSource : IInventorySource
        {
            public Task<InventorySnapshot> FetchAsync(CancellationToken ct) => throw new InvalidOperationException("boom");

            public Task<InventorySnapshot> SetPausedAsync(
                IReadOnlyDictionary<string, bool> servers, IReadOnlyDictionary<string, bool> tools, CancellationToken ct) =>
                throw new InvalidOperationException("no pause");
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

        [Test]
        public void ARebuildIsHeldBackWhileARunIsInFlight_AndAppliedWhenItEnds()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            r.FinishFetch(0, Snap(Srv("a", "x")));

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
            Assert.AreEqual(1, r.Source.Fetches.Count, "the first idle frame fetches");
            r.Controller.Update(false);
            Assert.AreEqual(1, r.Source.Fetches.Count, "not while one is in flight");

            r.FinishFetch(0, Snap(Srv("a", "x")));
            r.Now = 1030;
            r.Controller.Update(false);
            Assert.AreEqual(1, r.Source.Fetches.Count, "30 s is too soon");
            r.Now = 1061;
            r.Controller.Update(true);
            Assert.AreEqual(1, r.Source.Fetches.Count, "no polling while a run is in flight");
            r.Controller.Update(false);
            Assert.AreEqual(2, r.Source.Fetches.Count, "due and idle");

            r.FinishFetch(1, Snap(Srv("a", "x")));
            r.Now = 1070;
            r.Controller.Update(false);
            Assert.AreEqual(2, r.Source.Fetches.Count);
            r.Controller.RequestRefresh(); // a run just went idle
            r.Controller.Update(true);
            Assert.AreEqual(2, r.Source.Fetches.Count, "the request waits for idle");
            r.Controller.Update(false);
            Assert.AreEqual(3, r.Source.Fetches.Count, "idle after a run: fetch now, not in 60 s");
        }

        [Test]
        public void AFailedFetch_IsRetriedOnTheSameSixtySecondBeat()
        {
            Rig r = new Rig();
            r.Now = 0;
            r.Controller.Update(false);
            r.FailFetch(0, "down");
            r.Now = 59;
            r.Controller.Update(false);
            Assert.AreEqual(1, r.Source.Fetches.Count);
            r.Now = 60;
            r.Controller.Update(false);
            Assert.AreEqual(2, r.Source.Fetches.Count);
        }

        [Test]
        public void ARefreshWhileOneIsRunning_RunsAfterIt_NotInParallel()
        {
            Rig r = new Rig();
            r.Controller.Refresh();
            r.Controller.Refresh();
            Assert.AreEqual(1, r.Source.Fetches.Count);
            r.FinishFetch(0, Snap(Srv("a", "x")));
            r.Controller.Update(false); // applies, then starts the queued one
            Assert.AreEqual(2, r.Source.Fetches.Count);
        }

        // ------------------------------------------------------------------ the server list for Settings

        [Test]
        public void TheServerList_HasConnectedPausedAndOfflineServers_WithToolCountsAndTokenEstimates()
        {
            Rig r = new Rig();
            ServerInfo gh = Srv("github", "create_issue", "get_me").WithPaused(true);
            r.Load(Inv.A(), gh, Inv.Offline("mac", "boom"));

            Assert.AreEqual(3, r.Controller.Servers.Count);
            ServerInfo a = r.Controller.Servers[0];
            ServerInfo paused = r.Controller.Servers[1];
            ServerInfo off = r.Controller.Servers[2];
            CollectionAssert.AreEqual(new[] { "a", "github", "mac" }, new[] { a.Name, paused.Name, off.Name }, "the agent's order");
            Assert.AreEqual("on", InventoryFormat.StatusWord(a));
            Assert.AreEqual("paused", InventoryFormat.StatusWord(paused));
            Assert.AreEqual("offline", InventoryFormat.StatusWord(off));

            Assert.AreEqual(2, a.ToolCount);
            Assert.AreEqual(30, a.TotalTokens);
            Assert.AreEqual(30, a.ActiveTokens);
            Assert.AreEqual(2, paused.ToolCount, "a paused server keeps its tools in the list");
            Assert.AreEqual(20, paused.TotalTokens);
            Assert.AreEqual(0, paused.ActiveTokens);
            Assert.AreEqual(0, off.ToolCount);
            Assert.AreEqual("boom", off.Error);

            InventoryTotals totals = InventoryTotals.Of(r.Controller.Servers);
            Assert.AreEqual(2, totals.ActiveTools);
            Assert.AreEqual(4, totals.TotalTools);
            Assert.AreEqual(30, totals.ActiveTokens);
            Assert.AreEqual(50, totals.TotalTokens);

            Assert.IsTrue(HasNode(r, "srv:a"));
            Assert.IsFalse(HasNode(r, "srv:github"), "the list shows it, the graph does not");
            Assert.IsFalse(HasNode(r, "srv:mac"));
        }

        [Test]
        public void ThePendingToggleMovesTheTotals_BeforeTheAgentAnswers()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Srv("b", "z"));
            r.Controller.SetToolPaused("a__x", true);
            InventoryTotals t = InventoryTotals.Of(r.Controller.Servers);
            Assert.AreEqual(2, t.ActiveTools, "a.y and b.z");
            Assert.AreEqual(3, t.TotalTools);
            Assert.AreEqual(30, t.ActiveTokens, "a.y 20 + b.z 10");
        }

        // ------------------------------------------------------------------ toggles

        [Test]
        public void ASetToggle_IsShownAtOnce_ThenConfirmed_AndTheGraphFollowsOnlyOnPump()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());
            int version = r.Controller.ListVersion;

            r.Controller.SetServerPaused("a", true);
            Assert.IsTrue(r.Controller.FindServer("a").Paused, "shown at once");
            Assert.IsTrue(r.Controller.IsServerPending("a"));
            Assert.IsTrue(r.Controller.HasPending);
            Assert.Greater(r.Controller.ListVersion, version, "the list knows it must redraw");
            Assert.AreEqual(1, r.Source.Sets.Count, "the request went out");
            Assert.IsTrue(r.Source.SetServers[0]["a"], "true means paused");
            Assert.AreEqual(0, r.Source.SetTools[0].Count);
            Assert.IsTrue(HasNode(r, "srv:a"), "the graph shows what the agent can use, and it has not answered yet");

            r.FinishSet(0, Snap(Inv.A(paused: true), Inv.B()));
            Assert.IsTrue(r.Controller.IsServerPending("a"), "an answer on a pool thread is only queued");
            Assert.IsTrue(HasNode(r, "srv:a"));
            Assert.AreEqual(0, r.Removed.Count);

            Assert.IsTrue(r.Controller.Pump());
            Assert.IsFalse(r.Controller.IsServerPending("a"));
            Assert.IsFalse(r.Controller.HasPending);
            Assert.IsTrue(r.Controller.FindServer("a").Paused);
            Assert.IsNull(r.Controller.ToggleError);
            Assert.IsFalse(HasNode(r, "srv:a"));
            Assert.IsFalse(HasNode(r, "tool:a__x"));
            Assert.IsFalse(HasNode(r, "tool:a__y"));
            Assert.IsTrue(HasNode(r, "srv:b"));
            Assert.AreEqual(1, r.Rebuilds, "pausing removes nodes one by one, it does not rebuild");
            Assert.AreEqual(1, r.Controller.RebuildCount);
            CollectionAssert.AreEquivalent(new[] { "srv:a", "tool:a__x", "tool:a__y" }, r.Removed);
        }

        [Test]
        public void AFailedToggle_RevertsAndExposesTheError_AndLooksAgain()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());

            r.Controller.SetServerPaused("a", true);
            Assert.IsTrue(r.Controller.FindServer("a").Paused);
            r.FailSet(0, "EVA is busy");
            Assert.IsTrue(r.Controller.Pump());

            Assert.IsFalse(r.Controller.FindServer("a").Paused, "back to what the agent last said");
            Assert.IsFalse(r.Controller.IsServerPending("a"));
            Assert.AreEqual("Change not applied: EVA is busy", r.Controller.ToggleError);
            Assert.IsTrue(HasNode(r, "srv:a"), "the graph never moved");
            Assert.AreEqual(0, r.Removed.Count);

            r.Controller.Update(false);
            Assert.AreEqual(2, r.Source.Fetches.Count, "a failed request may have half applied, so it reads again");

            r.Controller.SetServerPaused("a", true);
            Assert.IsNull(r.Controller.ToggleError, "the next toggle clears the message");
        }

        [Test]
        public void AToggleBeforeAnyReading_IsIgnored_BecauseThereIsNothingToToggle()
        {
            Rig r = new Rig();
            r.Controller.SetServerPaused("a", true);
            r.Controller.SetToolPaused("a__x", true);
            Assert.IsFalse(r.Controller.HasPending);
            Assert.AreEqual(0, r.Source.Sets.Count);
        }

        [Test]
        public void ASourceThatThrowsOnThePauseCall_IsAFailureWithAMessage_NotACrash()
        {
            // A source whose reading works but whose pause call throws before returning a task.
            GraphModel g = new GraphModel("EVA");
            InventoryController c = new InventoryController(new ThrowOnPauseSource(), g, () => 0);
            c.Refresh();
            c.Pump();
            c.SetServerPaused("a", true);
            Thread.Sleep(10);
            c.Pump();
            Assert.IsFalse(c.HasPending, "the overlay is dropped: that is the revert");
            Assert.AreEqual("Change not applied: no pause", c.ToggleError);
            Assert.IsFalse(c.FindServer("a").Paused);
        }

        private sealed class ThrowOnPauseSource : IInventorySource
        {
            public Task<InventorySnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(Inv.Snap(Inv.A()));

            public Task<InventorySnapshot> SetPausedAsync(
                IReadOnlyDictionary<string, bool> servers, IReadOnlyDictionary<string, bool> tools, CancellationToken ct) =>
                throw new InvalidOperationException("no pause");
        }

        [Test]
        public void ARefreshThatArrivesWhileAChangeIsPending_DoesNotOverwriteTheOptimisticValue()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());
            r.Controller.SetServerPaused("a", true);       // POST in flight
            r.Controller.Refresh();                        // a periodic read starts meanwhile
            Assert.AreEqual(2, r.Source.Fetches.Count);

            r.FinishFetch(1, Snap(Inv.A(), Inv.B()));      // it still says "a is on"
            r.Controller.Pump();
            Assert.IsTrue(r.Controller.FindServer("a").Paused, "the pending value sits on top of every reading");
            Assert.IsTrue(r.Controller.IsServerPending("a"));
            Assert.IsTrue(HasNode(r, "srv:a"), "and the graph does not flicker either");

            r.FinishSet(0, Snap(Inv.A(paused: true), Inv.B()));
            r.Controller.Pump();
            Assert.IsTrue(r.Controller.FindServer("a").Paused);
            Assert.IsFalse(r.Controller.IsServerPending("a"));
            Assert.IsFalse(HasNode(r, "srv:a"));
        }

        [Test]
        public void ARefreshThatBeganBeforeTheAnswer_AndEndsAfterIt_IsDiscarded_AndAskedForAgain()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());
            r.Controller.Refresh();                        // read #1 starts, will report the OLD state
            r.Controller.SetServerPaused("a", true);
            r.FinishSet(0, Snap(Inv.A(paused: true), Inv.B()));
            r.Controller.Pump();
            Assert.IsFalse(HasNode(r, "srv:a"));
            Assert.AreEqual(3, r.Removed.Count);

            r.FinishFetch(1, Snap(Inv.A(), Inv.B()));      // late, and wrong by now
            r.Controller.Pump();
            Assert.IsTrue(r.Controller.FindServer("a").Paused, "the stale reading must not undo the change");
            Assert.IsFalse(HasNode(r, "srv:a"));
            Assert.AreEqual(0, r.Added.Count, "and the graph never re-added it");

            r.Controller.Update(false);
            Assert.AreEqual(3, r.Source.Fetches.Count, "its newer twin is requested");
        }

        [Test]
        public void TwoQuickToggles_AreSerialised_OneRequestAtATime_InOrder()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());

            r.Controller.SetServerPaused("a", true);
            r.Controller.SetServerPaused("b", true);
            Assert.AreEqual(1, r.Source.Sets.Count, "the second waits for the first");
            CollectionAssert.AreEqual(new[] { "a" }, r.Source.SetServers[0].Keys);
            Assert.IsTrue(r.Controller.IsServerPending("b"));
            Assert.IsTrue(r.Controller.FindServer("b").Paused, "but it is already shown");

            r.FinishSet(0, Snap(Inv.A(paused: true), Inv.B()));
            r.Controller.Pump();
            Assert.AreEqual(2, r.Source.Sets.Count, "sent when the first was answered");
            CollectionAssert.AreEqual(new[] { "b" }, r.Source.SetServers[1].Keys);
            Assert.IsTrue(r.Source.SetServers[1]["b"]);
            Assert.IsTrue(r.Controller.IsServerPending("b"), "still pending until its own answer");
            Assert.IsTrue(r.Controller.FindServer("b").Paused, "no flicker back in between");
            Assert.IsFalse(r.Controller.IsServerPending("a"));

            r.FinishSet(1, Snap(Inv.A(paused: true), Inv.B(paused: true)));
            r.Controller.Pump();
            Assert.AreEqual(2, r.Source.Sets.Count);
            Assert.AreEqual(1, r.Source.MaxOutstandingSets, "never two requests on the wire");
            Assert.IsFalse(r.Controller.HasPending);
            Assert.IsFalse(HasNode(r, "srv:a"));
            Assert.IsFalse(HasNode(r, "srv:b"));
        }

        [Test]
        public void TogglesMadeWhileOneIsInFlight_AreCoalesced_LastWritePerItemWins()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B(), Srv("c", "w"));

            r.Controller.SetServerPaused("a", true);   // in flight
            r.Controller.SetServerPaused("a", false);  // changed her mind
            r.Controller.SetServerPaused("b", true);
            r.Controller.SetServerPaused("c", true);
            r.Controller.SetServerPaused("c", false);
            Assert.AreEqual(1, r.Source.Sets.Count);

            r.FinishSet(0, Snap(Inv.A(paused: true), Inv.B(), Srv("c", "w")));
            r.Controller.Pump();
            Assert.AreEqual(2, r.Source.Sets.Count);
            Dictionary<string, bool> second = r.Source.SetServers[1];
            Assert.AreEqual(3, second.Count);
            Assert.IsFalse(second["a"]);
            Assert.IsTrue(second["b"]);
            Assert.IsFalse(second["c"], "c was only ever asked to stay on, last write wins");
            Assert.IsFalse(r.Controller.FindServer("a").Paused, "the later choice is what the list shows while it is pending");
            Assert.AreEqual(1, r.Source.MaxOutstandingSets);
        }

        [Test]
        public void ToggleToTheStateItAlreadyHas_SendsNothing()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());
            r.Controller.SetServerPaused("a", false);
            Assert.AreEqual(0, r.Source.Sets.Count);
            r.Controller.SetServerPaused("a", true);
            r.Controller.SetServerPaused("a", true);
            Assert.AreEqual(1, r.Source.Sets.Count, "the second one is already shown, so it is a no-op");
        }

        [Test]
        public void OfflineUnknownAndNameLessTargets_AreIgnored()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.Offline("mac", "boom"));
            r.Controller.SetServerPaused("mac", false);
            r.Controller.SetServerPaused("nope", true);
            r.Controller.SetServerPaused(null, true);
            r.Controller.SetToolPaused("nope__x", true);
            r.Controller.SetToolPaused("", true);
            Assert.AreEqual(0, r.Source.Sets.Count);
            Assert.IsFalse(r.Controller.HasPending);
        }

        [Test]
        public void AToolToggle_IsKeyedByWireName_AndRemovesAndRestoresOnlyThatNode()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());

            r.Controller.SetToolPaused("a__x", true);
            Assert.IsTrue(r.Controller.IsToolPending("a__x"));
            Assert.AreEqual(0, r.Source.SetServers[0].Count);
            Assert.IsTrue(r.Source.SetTools[0]["a__x"]);
            Assert.IsTrue(r.Controller.FindServer("a").ToolDetails[0].Paused);

            r.FinishSet(0, Snap(Inv.A(xPaused: true), Inv.B()));
            r.Controller.Pump();
            Assert.IsFalse(r.Controller.IsToolPending("a__x"));
            Assert.IsFalse(HasNode(r, "tool:a__x"));
            Assert.IsTrue(HasNode(r, "tool:a__y"));
            Assert.IsTrue(HasNode(r, "srv:a"));
            CollectionAssert.AreEqual(new[] { "tool:a__x" }, r.Removed);

            r.Controller.SetToolPaused("a__x", false);
            Assert.IsFalse(r.Source.SetTools[1]["a__x"]);
            r.FinishSet(1, Snap(Inv.A(), Inv.B()));
            r.Controller.Pump();
            Assert.IsTrue(HasNode(r, "tool:a__x"));
            CollectionAssert.AreEqual(new[] { "tool:a__x" }, r.Added);
            Assert.AreEqual(1, r.Rebuilds);
        }

        [Test]
        public void NamesTheAgentDidNotRecognise_AreExposed_WithoutRevertingTheRest()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());
            r.Controller.SetServerPaused("a", true);
            r.FinishSet(0, InventorySnapshot.From(new[] { Inv.A(paused: true), Inv.B() }, new[] { "zzz" }));
            r.Controller.Pump();
            Assert.AreEqual("EVA did not recognise: zzz", r.Controller.ToggleError);
            Assert.IsTrue(r.Controller.FindServer("a").Paused);
            Assert.IsFalse(HasNode(r, "srv:a"));
        }

        [Test]
        public void TheGraphChange_IsHeldBackWhileARunIsInFlight_ButTheListUpdatesAtOnce()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());
            r.Controller.SetServerPaused("a", true);
            r.FinishSet(0, Snap(Inv.A(paused: true), Inv.B()));

            r.Controller.Pump(runInFlight: true);
            Assert.IsFalse(r.Controller.IsServerPending("a"), "the list has the answer");
            Assert.IsTrue(r.Controller.FindServer("a").Paused);
            Assert.IsTrue(HasNode(r, "srv:a"), "no node is pulled out from under a lit tool call");

            r.Controller.Pump(runInFlight: false);
            Assert.IsFalse(HasNode(r, "srv:a"));
        }

        [Test]
        public void AToggleThatTheAgentAnswersWithADifferentReading_ShowsTheAgentsTruth()
        {
            Rig r = new Rig();
            r.Load(Inv.A(), Inv.B());
            r.Controller.SetServerPaused("a", true);
            r.FinishSet(0, Snap(Inv.A(), Inv.B())); // the agent says it is still on
            r.Controller.Pump();
            Assert.IsFalse(r.Controller.FindServer("a").Paused, "the server owns the truth");
            Assert.IsFalse(r.Controller.IsServerPending("a"));
            Assert.IsTrue(HasNode(r, "srv:a"));
        }
    }
}
