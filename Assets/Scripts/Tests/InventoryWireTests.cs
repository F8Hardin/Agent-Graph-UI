using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.Tests
{
    /// <summary>
    /// The shape of EVA's real GET /tools, trimmed to what matters: namespaced tool names with a short_name (fates_desktop,
    /// github), bare names (eva), a paused tool, a paused server that is still connected (github), an errored one (fates_macbook),
    /// a row with no name, and a server with no status. Single quotes stand in for JSON quotes.
    /// </summary>
    internal static class InventoryFixtures
    {
        public static readonly string ToolsJson = @"{'servers':[
 {'name':'eva','status':'connected','error':null,'paused':false,'tool_count':2,'active_count':2,'tokens_est':174,'active_tokens_est':174,
  'tools':[{'name':'kv_set','short_name':'kv_set','description':'Store an exact value','paused':false,'tokens_est':94},{'name':'kv_get','short_name':'kv_get','paused':false,'tokens_est':80}]},
 {'name':'fates_desktop','status':'connected','error':null,'paused':false,'tool_count':3,'active_count':2,'tokens_est':3703,'active_tokens_est':3476,
  'tools':[{'name':'fates_desktop__vault_read','short_name':'vault_read','paused':false,'tokens_est':120},{'name':'fates_desktop__vault_search','short_name':'vault_search','paused':false},{'name':'fates_desktop__run_command','short_name':'run_command','paused':true,'tokens_est':227}]},
 {'name':'fate_ubuntu_server','status':'connected','error':null,'paused':false,'tool_count':1,
  'tools':[{'name':'fate_ubuntu_server__run_command','short_name':'run_command','paused':false}]},
 {'name':'fates_macbook','status':'error','error':'unhandled errors in a TaskGroup (1 sub-exception)','paused':true,'tool_count':0,'active_count':0,'tokens_est':0,'active_tokens_est':0,'tools':[]},
 {'name':'github','status':'connected','error':null,'paused':true,'tool_count':2,'active_count':0,'tokens_est':14715,'active_tokens_est':0,
  'tools':[{'name':'github__create_issue','short_name':'create_issue','paused':true},{'name':'github__get_me','paused':true}]},
 {'status':'connected','tools':[]},
 {'name':'nostatus','tools':[{'name':'x'}]}
],'totals':{'servers':7},'note':'tokens_est = len(json schema)/4, an estimate'}".Replace('\'', '"');

        public static InventorySnapshot Snapshot()
        {
            Assert.IsTrue(ToolsWire.TryParse(ToolsJson, out InventorySnapshot s));
            return s;
        }
    }

    public class ToolsWireTests
    {
        [Test]
        public void ParsesTheRealShape_ConnectedServersInOrder_OthersOmitted()
        {
            InventorySnapshot s = InventoryFixtures.Snapshot();

            List<string> names = new List<string>();
            foreach (ServerInfo c in s.Connected) names.Add(c.Name);
            CollectionAssert.AreEqual(new[] { "eva", "fates_desktop", "fate_ubuntu_server", "github" }, names, "agent order is kept; the nameless row is skipped");

            Assert.AreEqual(2, s.Omitted.Count);
            Assert.AreEqual("fates_macbook", s.Omitted[0].Name);
            Assert.AreEqual("error", s.Omitted[0].Status);
            Assert.AreEqual("unhandled errors in a TaskGroup (1 sub-exception)", s.Omitted[0].Error);
            Assert.AreEqual("nostatus", s.Omitted[1].Name);
            Assert.AreEqual("unknown", s.Omitted[1].Status, "no status proves nothing, so it is not drawn");
            Assert.IsNull(s.Omitted[1].Error);
        }

        [Test]
        public void ToolsUseTheShortName_AndFallBackToTheNameWithItsServerPrefixStripped()
        {
            InventorySnapshot s = InventoryFixtures.Snapshot();
            ServerInfo desktop = s.Connected[1];
            CollectionAssert.AreEqual(new[] { "vault_read", "vault_search", "run_command" }, desktop.Tools);
            CollectionAssert.AreEqual(new[] { "kv_set", "kv_get" }, s.Connected[0].Tools, "a bare name is already bare");
            CollectionAssert.AreEqual(new[] { "create_issue", "get_me" }, s.Connected[3].Tools, "get_me has no short_name: github__ is stripped");
        }

        [Test]
        public void CarriesPausedFlagsAndTokenEstimates()
        {
            InventorySnapshot s = InventoryFixtures.Snapshot();
            ServerInfo desktop = s.Connected[1];
            Assert.IsFalse(desktop.Paused);
            Assert.AreEqual(3703, desktop.TokensEst);
            Assert.AreEqual(3476, desktop.ActiveTokensEst);
            Assert.AreEqual(120, desktop.ToolDetails[0].TokensEst);
            Assert.IsNull(desktop.ToolDetails[1].TokensEst, "a missing estimate stays null, not zero");
            Assert.IsFalse(desktop.ToolDetails[0].Paused);
            Assert.IsTrue(desktop.ToolDetails[2].Paused);

            ServerInfo github = s.Connected[3];
            Assert.IsTrue(github.Paused);
            Assert.IsTrue(github.IsConnected, "paused is not offline");
            Assert.AreEqual(0, github.ActiveTokensEst);
        }

        [Test]
        public void OnlyAServerNamedMemoryIsTheMemoryKind()
        {
            Assert.IsTrue(ToolsWire.TryParse(
                "{\"servers\":[{\"name\":\"memory\",\"status\":\"connected\",\"tools\":[]},{\"name\":\"fates_desktop\",\"status\":\"connected\",\"tools\":[{\"name\":\"vault_read\"}]}]}",
                out InventorySnapshot s));
            Assert.AreEqual(ServerInfo.KindMemory, s.Connected[0].Kind);
            Assert.AreEqual(ServerInfo.KindMcp, s.Connected[1].Kind, "vault tools on a desktop server do not make it the memory server");
        }

        [TestCase("srv", "srv__foo", null, "foo")]
        [TestCase("srv", "other__bar", null, "other__bar")]
        [TestCase("srv", "kv_set", "kv_set", "kv_set")]
        [TestCase("srv", "kv_set", null, "kv_set")]
        [TestCase("srv", "srv__", null, "srv__")]
        [TestCase("srv", "srv__foo", "foo", "foo")]
        [TestCase("srv", null, null, null)]
        public void ToolLabel_Rules(string server, string name, string shortName, string expected)
        {
            Assert.AreEqual(expected, ToolsWire.ToolLabel(server, name, shortName));
        }

        [Test]
        public void ToolsMayBeBareStrings_AndDuplicatesCollapse()
        {
            Assert.IsTrue(ToolsWire.TryParse(
                "{\"servers\":[{\"name\":\"s\",\"status\":\"connected\",\"tools\":[\"s__a\",\"b\",{\"name\":\"s__a\"},7,null,{\"x\":1}]}]}",
                out InventorySnapshot snap));
            CollectionAssert.AreEqual(new[] { "a", "b" }, snap.Connected[0].Tools);
        }

        [Test]
        public void AnEmptyServerList_IsAValidAnswer()
        {
            Assert.IsTrue(ToolsWire.TryParse("{\"servers\":[]}", out InventorySnapshot s));
            Assert.AreEqual(0, s.Connected.Count);
            Assert.AreEqual(0, s.Omitted.Count);
            Assert.AreEqual(InventorySnapshot.Empty.Signature, s.Signature);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("<html>wrong server</html>")]
        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("{\"servers\":{}}")]
        [TestCase("{\"servers\":\"none\"}")]
        [TestCase("{\"servers\":[")]
        public void MalformedBodies_AreRejected_NotTreatedAsAnEmptyInventory(string body)
        {
            Assert.IsFalse(ToolsWire.TryParse(body, out InventorySnapshot s));
            Assert.IsNull(s);
        }

        [Test]
        public void Signature_IgnoresOrder_PausedFlagsAndEstimates_ButSeesShapeChanges()
        {
            ServerInfo a = new ServerInfo("a", null, "connected", new[] { new ToolInfo("x"), new ToolInfo("y") }, false, null, null, null);
            ServerInfo b = new ServerInfo("b", null, "connected", "z");
            string baseline = InventorySnapshot.From(new[] { a, b }).Signature;

            Assert.AreEqual(baseline, InventorySnapshot.From(new[] { b, a }).Signature, "reordered servers are the same shape");
            ServerInfo aReordered = new ServerInfo("a", null, "connected", new[] { new ToolInfo("y"), new ToolInfo("x") }, false, null, null, null);
            Assert.AreEqual(baseline, InventorySnapshot.From(new[] { aReordered, b }).Signature, "reordered tools are the same shape");
            ServerInfo aPaused = new ServerInfo("a", null, "connected", new[] { new ToolInfo("x", true, 5), new ToolInfo("y") }, true, null, 99, 0);
            Assert.AreEqual(baseline, InventorySnapshot.From(new[] { aPaused, b }).Signature, "a pause toggle must not reset the layout");

            ServerInfo aMore = new ServerInfo("a", null, "connected", "x", "y", "w");
            Assert.AreNotEqual(baseline, InventorySnapshot.From(new[] { aMore, b }).Signature, "a new tool is a change");
            Assert.AreNotEqual(baseline, InventorySnapshot.From(new[] { a }).Signature, "a server leaving is a change");
            ServerInfo bDown = new ServerInfo("b", null, "error", "z");
            Assert.AreNotEqual(baseline, InventorySnapshot.From(new[] { a, bDown }).Signature, "a server going offline removes its nodes");
        }
    }

    public class EvaInventoryTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public readonly List<string> Requests = new List<string>();
            public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.Method + " " + request.RequestUri.AbsoluteUri);
                return Respond(request, cancellationToken);
            }
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode code, string body) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        // On the thread pool, not the caller: Unity runs EditMode tests on the main thread under its own
        // SynchronizationContext, and a stub handler that awaits would post its continuation back to the thread
        // this call is blocking. That deadlocked the whole editor the first time these ran in Unity.
        private static InventorySnapshot Fetch(EvaInventory inv) => Task.Run(() => inv.FetchAsync(CancellationToken.None)).GetAwaiter().GetResult();

        [Test]
        public void Fetch_GetsToolsAndParsesIt()
        {
            StubHandler h = new StubHandler { Respond = (_, __) => Json(HttpStatusCode.OK, InventoryFixtures.ToolsJson) };
            using (EvaInventory inv = new EvaInventory("http://eva.test:8000/", new HttpClient(h)))
            {
                InventorySnapshot s = Fetch(inv);
                Assert.AreEqual("GET http://eva.test:8000/tools", h.Requests[0], "the trailing slash on the base url is trimmed");
                Assert.AreEqual(4, s.Connected.Count);
                Assert.AreEqual(2, s.Omitted.Count);
            }
        }

        [Test]
        public void Fetch_HttpError_SaysWhy()
        {
            StubHandler h = new StubHandler { Respond = (_, __) => Json(HttpStatusCode.InternalServerError, "{\"detail\":\"mcp manager not ready\"}") };
            using (EvaInventory inv = new EvaInventory("http://eva.test:8000", new HttpClient(h)))
            {
                InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Fetch(inv));
                Assert.AreEqual("mcp manager not ready", e.Message);
            }
        }

        [Test]
        public void Fetch_HttpErrorWithNoBody_FallsBackToTheStatusLine()
        {
            StubHandler h = new StubHandler { Respond = (_, __) => Json(HttpStatusCode.BadGateway, "") };
            using (EvaInventory inv = new EvaInventory("http://eva.test:8000", new HttpClient(h)))
            {
                InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Fetch(inv));
                StringAssert.Contains("EVA returned 502", e.Message);
            }
        }

        [Test]
        public void Fetch_Unreachable_IsAReadableError()
        {
            StubHandler h = new StubHandler { Respond = (_, __) => throw new HttpRequestException("No connection could be made") };
            using (EvaInventory inv = new EvaInventory("http://eva.test:8000", new HttpClient(h)))
            {
                InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Fetch(inv));
                StringAssert.Contains("Could not reach EVA", e.Message);
            }
        }

        [TestCase("<html>wrong server</html>")]
        [TestCase("")]
        [TestCase("{\"servers\":7}")]
        public void Fetch_BadBody_IsAnError_NotAnEmptyInventory(string body)
        {
            StubHandler h = new StubHandler { Respond = (_, __) => Json(HttpStatusCode.OK, body) };
            using (EvaInventory inv = new EvaInventory("http://eva.test:8000", new HttpClient(h)))
            {
                InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Fetch(inv));
                StringAssert.Contains("not in the expected form", e.Message);
            }
        }

        [Test]
        public void Fetch_EmptyServerList_IsAnEmptySnapshot()
        {
            StubHandler h = new StubHandler { Respond = (_, __) => Json(HttpStatusCode.OK, "{\"servers\":[]}") };
            using (EvaInventory inv = new EvaInventory("http://eva.test:8000", new HttpClient(h)))
            {
                Assert.AreEqual(0, Fetch(inv).Connected.Count);
            }
        }

        [Test]
        public void Fetch_SlowServer_TimesOut()
        {
            StubHandler h = new StubHandler { Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; } };
            using (EvaInventory inv = new EvaInventory("http://eva.test:8000", new HttpClient(h), timeoutMs: 50))
            {
                TimeoutException e = Assert.Throws<TimeoutException>(() => Fetch(inv));
                StringAssert.Contains("tool list", e.Message);
            }
        }

        [Test]
        public void Fetch_CallerCancellation_IsNotReportedAsATimeout()
        {
            StubHandler h = new StubHandler { Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; } };
            using (EvaInventory inv = new EvaInventory("http://eva.test:8000", new HttpClient(h)))
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                cts.CancelAfter(30);
                Assert.Catch<OperationCanceledException>(() => Task.Run(() => inv.FetchAsync(cts.Token)).GetAwaiter().GetResult());
            }
        }
    }
}
