using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>The shape of EVA's real GET /models, trimmed. One loaded model, one never loaded (null context), one junk row.</summary>
    internal static class ModelFixtures
    {
        public const string ModelsJson =
            "{\"models\":[" +
            "{\"id\":\"google/gemma-4-26b-a4b\",\"display_name\":\"Gemma 4 26B A4B\",\"state\":\"loaded\",\"max_context_length\":262144,\"loaded_context_length\":60160,\"arch\":\"gemma4\"}," +
            "{\"id\":\"google/gemma-3-4b\",\"display_name\":\"Gemma 3 4B\",\"state\":\"not-loaded\",\"max_context_length\":131072,\"loaded_context_length\":null}," +
            "{\"id\":\"qwen3.8-27b\",\"display_name\":\"Qwen3.8 27B\",\"state\":\"not-loaded\",\"max_context_length\":null,\"loaded_context_length\":null}," +
            "{\"display_name\":\"no id, skipped\"}" +
            "],\"current_model\":\"google/gemma-4-26b-a4b\",\"configured_model\":\"google/gemma-4-26b-a4b\"," +
            "\"active_context_length\":60160,\"configured_context_length\":60160,\"model_in_sync\":true,\"temperature\":0.6}";

        public static ModelCatalog Catalog()
        {
            Assert.IsTrue(ModelsWire.TryParseCatalog(ModelsJson, out ModelCatalog c));
            return c;
        }
    }

    public class ModelsWireTests
    {
        [Test]
        public void ParsesTheRealShape()
        {
            ModelCatalog c = ModelFixtures.Catalog();
            Assert.AreEqual(3, c.Models.Count, "the row without an id is skipped");
            Assert.AreEqual("google/gemma-4-26b-a4b", c.CurrentModelId);
            Assert.AreEqual(60160, c.ActiveContextLength);
            Assert.AreEqual(60160, c.ConfiguredContextLength);
            Assert.AreEqual(0.6f, c.Temperature.Value, 1e-6f);

            ModelInfo loaded = c.Find("google/gemma-4-26b-a4b");
            Assert.IsTrue(loaded.IsLoaded);
            Assert.AreEqual("Gemma 4 26B A4B", loaded.DisplayName);
            Assert.AreEqual(262144, loaded.MaxContextLength);
            Assert.IsFalse(c.Find("google/gemma-3-4b").IsLoaded);
            Assert.IsNull(c.Find("qwen3.8-27b").MaxContextLength, "a never-loaded model has no known context");
            Assert.IsNull(c.Find("nope"));
        }

        [Test]
        public void MissingDisplayName_FallsBackToTheId()
        {
            Assert.IsTrue(ModelsWire.TryParseCatalog("{\"models\":[{\"id\":\"a/b\",\"state\":\"loaded\"}]}", out ModelCatalog c));
            Assert.AreEqual("a/b", c.Models[0].DisplayName);
            Assert.IsNull(c.CurrentModelId);
            Assert.IsNull(c.Temperature);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("[1,2]")]
        [TestCase("{\"models\":\"x\"}")]
        [TestCase("{\"current_model\":\"x\"}")]
        public void MalformedBodies_ReturnFalseWithoutThrowing(string json)
        {
            Assert.IsFalse(ModelsWire.TryParseCatalog(json, out ModelCatalog c));
            Assert.IsNull(c);
        }

        [Test]
        public void SetModelJson_OmitsContextWhenUnknown()
        {
            Assert.AreEqual("{\"model\":\"a/b\"}", ModelsWire.SetModelJson("a/b", null));
            Assert.AreEqual("{\"model\":\"a/b\",\"context_length\":32000}", ModelsWire.SetModelJson("a/b", 32000));
            Assert.AreEqual("{\"model\":\"a/b\"}", ModelsWire.SetModelJson("a/b", 0));
        }

        [Test]
        public void ErrorText_PrefersFastApiDetail()
        {
            Assert.AreEqual("Saved x, but loading it now failed: boom", ModelsWire.ErrorText(500, "Internal Server Error", "{\"detail\":\"Saved x, but loading it now failed: boom\"}"));
            Assert.AreEqual("EVA returned 502 Bad Gateway.", ModelsWire.ErrorText(502, "Bad Gateway", "<html>nginx</html>"));
            Assert.AreEqual("EVA returned 500.", ModelsWire.ErrorText(500, null, ""));
        }

        [Test]
        public void MetaWire_CarriesContextLength()
        {
            Assert.IsTrue(WireParser.TryParse("{\"type\":\"meta\",\"data\":{\"model\":\"m\",\"temperature\":0.4,\"job_id\":\"j\",\"context_length\":32000}}", out AgentEvent ev));
            Assert.AreEqual(32000, ev.ContextLength);

            Assert.IsTrue(WireParser.TryParse("{\"type\":\"meta\",\"data\":{\"model\":\"m\",\"context_length\":null}}", out AgentEvent none));
            Assert.IsNull(none.ContextLength);
        }
    }

    public class ModelStateTests
    {
        [Test]
        public void Reducer_KeepsContextLengthFromMeta_AndClearsItOnReset()
        {
            ConsoleReducer r = new ConsoleReducer(new GraphModel("EVA", SampleInventory.Eva()));
            r.Apply(AgentEvent.Meta("m", 0.4f, "j1", 32000));
            Assert.AreEqual(32000, r.State.ContextLength);

            // A later meta that does not mention context must not erase it.
            r.Apply(AgentEvent.Meta("m", 0.4f, "j1"));
            Assert.AreEqual(32000, r.State.ContextLength);

            r.Reset();
            Assert.IsNull(r.State.ContextLength);
        }

        [Test]
        public void ChooseContext_KeepsTheServersContext_CappedByTheTargetModel()
        {
            ModelCatalog c = ModelFixtures.Catalog();
            Assert.AreEqual(60160, ModelController.ChooseContext(c, "google/gemma-4-26b-a4b"));
            Assert.AreEqual(60160, ModelController.ChooseContext(c, "qwen3.8-27b"), "unknown max: keep the server's context");
            Assert.AreEqual(60160, ModelController.ChooseContext(c, "not-in-catalog"));

            ModelCatalog small = new ModelCatalog(new[] { new ModelInfo("s", "S", false, 4096) }, "s", 60000, 60000, null);
            Assert.AreEqual(4096, ModelController.ChooseContext(small, "s"));

            Assert.IsNull(ModelController.ChooseContext(null, "x"));
            Assert.IsNull(ModelController.ChooseContext(new ModelCatalog(null, null, null, null, null), "x"));
        }
    }

    public class ModelControllerTests
    {
        private sealed class FakeSelector : IModelSelector
        {
            public readonly List<TaskCompletionSource<ModelCatalog>> Lists = new List<TaskCompletionSource<ModelCatalog>>();
            public readonly List<TaskCompletionSource<bool>> Sets = new List<TaskCompletionSource<bool>>();
            public readonly List<string> SetIds = new List<string>();
            public readonly List<int?> SetContexts = new List<int?>();

            public Task<ModelCatalog> GetModelsAsync(CancellationToken ct)
            {
                TaskCompletionSource<ModelCatalog> tcs = new TaskCompletionSource<ModelCatalog>();
                Lists.Add(tcs);
                return tcs.Task;
            }

            public Task SetModelAsync(string modelId, int? contextLength, CancellationToken ct)
            {
                TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
                Sets.Add(tcs);
                SetIds.Add(modelId);
                SetContexts.Add(contextLength);
                return tcs.Task;
            }
        }

        // Completion queues onto the controller from a pool thread; the main thread applies it in Pump().
        private static void PumpUntil(ModelController c, Func<bool> done)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (!done() && sw.ElapsedMilliseconds < 3000)
            {
                c.Pump();
                Thread.Sleep(1);
            }
            Assert.IsTrue(done(), "timed out waiting for the controller");
        }

        [Test]
        public void NoSelector_IsUnavailable_AndEverythingIsANoOp()
        {
            ModelController c = new ModelController(null);
            int changes = 0;
            c.Changed += () => changes++;

            Assert.IsFalse(c.IsAvailable);
            Assert.AreEqual(ModelControlPhase.Unavailable, c.Phase);
            c.Refresh();
            c.Select("anything");
            Assert.IsFalse(c.Pump());
            Assert.AreEqual(ModelControlPhase.Unavailable, c.Phase);
            Assert.AreEqual(0, changes);
            Assert.IsNull(c.Error);
            c.Dispose();
        }

        [Test]
        public void Refresh_IsPendingUntilPumped_ThenReady()
        {
            FakeSelector s = new FakeSelector();
            ModelController c = new ModelController(s);
            c.Refresh();
            Assert.AreEqual(ModelControlPhase.Loading, c.Phase, "the UI sees a pending state immediately");
            Assert.IsNull(c.Catalog);

            s.Lists[0].SetResult(ModelFixtures.Catalog());
            Assert.AreEqual(ModelControlPhase.Loading, c.Phase, "a finished request does nothing until the main thread pumps");
            PumpUntil(c, () => c.Phase == ModelControlPhase.Ready);
            Assert.AreEqual(3, c.Catalog.Models.Count);
            c.Dispose();
        }

        [Test]
        public void Refresh_Failure_ShowsTheMessage_AndKeepsTheOldCatalog()
        {
            FakeSelector s = new FakeSelector();
            ModelController c = new ModelController(s);
            c.Refresh();
            s.Lists[0].SetResult(ModelFixtures.Catalog());
            PumpUntil(c, () => c.Phase == ModelControlPhase.Ready);

            c.Refresh();
            s.Lists[1].SetException(new InvalidOperationException("Could not reach EVA for the model list: refused"));
            PumpUntil(c, () => c.Phase == ModelControlPhase.Failed);
            Assert.AreEqual("Could not reach EVA for the model list: refused", c.Error);
            Assert.AreEqual(3, c.Catalog.Models.Count);
            c.Dispose();
        }

        [Test]
        public void Select_GoesPending_AsksForTheServersContext_ThenRefreshes()
        {
            FakeSelector s = new FakeSelector();
            ModelController c = new ModelController(s);
            c.Refresh();
            s.Lists[0].SetResult(ModelFixtures.Catalog());
            PumpUntil(c, () => c.Phase == ModelControlPhase.Ready);

            c.Select("google/gemma-3-4b");
            Assert.AreEqual(ModelControlPhase.Switching, c.Phase);
            Assert.AreEqual("google/gemma-3-4b", c.PendingModelId);
            Assert.AreEqual("google/gemma-3-4b", s.SetIds[0]);
            Assert.AreEqual(60160, s.SetContexts[0]);

            c.Select("qwen3.8-27b");
            Assert.AreEqual(1, s.Sets.Count, "a second pick during a switch is ignored");
            c.Refresh();
            Assert.AreEqual(1, s.Lists.Count, "so is a refresh: it would drop the switch's result");

            s.Sets[0].SetResult(true);
            PumpUntil(c, () => s.Lists.Count == 2);
            Assert.AreEqual(ModelControlPhase.Loading, c.Phase);
            Assert.IsNull(c.PendingModelId);

            s.Lists[1].SetResult(ModelFixtures.Catalog());
            PumpUntil(c, () => c.Phase == ModelControlPhase.Ready);
            c.Dispose();
        }

        [Test]
        public void Select_Failure_KeepsTheMessageOnScreen_WhileTheListCatchesUp()
        {
            FakeSelector s = new FakeSelector();
            ModelController c = new ModelController(s);
            c.Refresh();
            s.Lists[0].SetResult(ModelFixtures.Catalog());
            PumpUntil(c, () => c.Phase == ModelControlPhase.Ready);

            c.Select("google/gemma-3-4b");
            s.Sets[0].SetException(new InvalidOperationException("Saved google/gemma-3-4b, but loading it now failed: out of memory"));
            PumpUntil(c, () => c.Phase == ModelControlPhase.Failed);
            Assert.AreEqual("Saved google/gemma-3-4b, but loading it now failed: out of memory", c.Error);
            Assert.IsNull(c.PendingModelId);

            // The server may have restored the old model, so the list is re-read, but quietly: the error stays up.
            PumpUntil(c, () => s.Lists.Count == 2);
            ModelCatalog restored = new ModelCatalog(new[] { new ModelInfo("old", "Old", true, 1024) }, "old", 1024, 1024, null);
            s.Lists[1].SetResult(restored);
            PumpUntil(c, () => c.Catalog.CurrentModelId == "old");
            Assert.AreEqual(ModelControlPhase.Failed, c.Phase, "the catch-up refresh must not clear the failure");
            Assert.IsNotNull(c.Error);
            c.Dispose();
        }

        [Test]
        public void Select_OfTheCurrentModel_DoesNothing()
        {
            FakeSelector s = new FakeSelector();
            ModelController c = new ModelController(s);
            c.Refresh();
            s.Lists[0].SetResult(ModelFixtures.Catalog());
            PumpUntil(c, () => c.Phase == ModelControlPhase.Ready);

            c.Select("google/gemma-4-26b-a4b");
            Assert.AreEqual(0, s.Sets.Count);
            Assert.AreEqual(ModelControlPhase.Ready, c.Phase);
            c.Dispose();
        }

        [Test]
        public void StaleListResult_DoesNotOverwriteANewerRequest()
        {
            FakeSelector s = new FakeSelector();
            ModelController c = new ModelController(s);
            c.Refresh();                       // list 0: slow
            c.Refresh();                       // list 1: the one that counts
            ModelCatalog newer = new ModelCatalog(new[] { new ModelInfo("new", "New", true, 1024) }, "new", 1024, 1024, null);
            s.Lists[1].SetResult(newer);
            PumpUntil(c, () => c.Phase == ModelControlPhase.Ready);

            s.Lists[0].SetResult(ModelFixtures.Catalog()); // arrives late
            Thread.Sleep(30);
            c.Pump();
            Assert.AreEqual("new", c.Catalog.CurrentModelId);
            c.Dispose();
        }

        [Test]
        public void ASelectorThatThrowsSynchronously_BecomesAFailure_NotACrash()
        {
            ModelController c = new ModelController(new ThrowingSelector());
            c.Refresh();
            PumpUntil(c, () => c.Phase == ModelControlPhase.Failed);
            Assert.AreEqual("sync boom", c.Error);
            c.Dispose();
        }

        private sealed class ThrowingSelector : IModelSelector
        {
            public Task<ModelCatalog> GetModelsAsync(CancellationToken ct) => throw new InvalidOperationException("sync boom");
            public Task SetModelAsync(string modelId, int? contextLength, CancellationToken ct) => throw new InvalidOperationException("sync boom");
        }
    }

    public class EvaModelEndpointTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public readonly List<string> Requests = new List<string>();
            public readonly List<string> Bodies = new List<string>();
            public Func<HttpRequestMessage, HttpResponseMessage> Respond;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.Method + " " + request.RequestUri.PathAndQuery);
                Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync());
                return Respond(request);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body)
        {
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private static EvaSseTransport Transport(StubHandler h) =>
            new EvaSseTransport("http://eva.test:8000", new HttpClient(h), null, new ReconnectPolicy { ModelListTimeoutMs = 2000, ModelSwitchTimeoutMs = 2000 });

        [Test]
        public void GetModels_ReadsTheCatalog()
        {
            StubHandler h = new StubHandler { Respond = _ => Json(HttpStatusCode.OK, ModelFixtures.ModelsJson) };
            using (EvaSseTransport t = Transport(h))
            {
                ModelCatalog c = t.GetModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual("GET /models", h.Requests[0]);
                Assert.AreEqual(3, c.Models.Count);
                Assert.AreEqual("google/gemma-4-26b-a4b", c.CurrentModelId);
            }
        }

        [Test]
        public void SetModel_PostsTheModelAndContext()
        {
            StubHandler h = new StubHandler { Respond = _ => Json(HttpStatusCode.OK, "{\"ok\":true}") };
            using (EvaSseTransport t = Transport(h))
            {
                t.SetModelAsync("google/gemma-3-4b", 60160, CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual("POST /set-model", h.Requests[0]);
                Assert.AreEqual("{\"model\":\"google/gemma-3-4b\",\"context_length\":60160}", h.Bodies[0]);
            }
        }

        [Test]
        public void SetModel_Refusal_SurfacesEvasOwnExplanation()
        {
            StubHandler h = new StubHandler
            {
                Respond = _ => Json(HttpStatusCode.InternalServerError, "{\"detail\":\"Saved x, but loading it now failed: oom - the saved model loads on your next message.\"}")
            };
            using (EvaSseTransport t = Transport(h))
            {
                InvalidOperationException e = Assert.Throws<InvalidOperationException>(
                    () => t.SetModelAsync("x", null, CancellationToken.None).GetAwaiter().GetResult());
                StringAssert.StartsWith("Saved x, but loading it now failed", e.Message);
            }
        }

        [Test]
        public void GetModels_UnreachableServer_IsAReadableError()
        {
            StubHandler h = new StubHandler { Respond = _ => throw new HttpRequestException("No connection could be made") };
            using (EvaSseTransport t = Transport(h))
            {
                InvalidOperationException e = Assert.Throws<InvalidOperationException>(
                    () => t.GetModelsAsync(CancellationToken.None).GetAwaiter().GetResult());
                StringAssert.Contains("Could not reach EVA", e.Message);
            }
        }

        [Test]
        public void GetModels_BadBody_IsAnError_NotAnEmptyList()
        {
            StubHandler h = new StubHandler { Respond = _ => Json(HttpStatusCode.OK, "<html>wrong server</html>") };
            using (EvaSseTransport t = Transport(h))
            {
                Assert.Throws<InvalidOperationException>(() => t.GetModelsAsync(CancellationToken.None).GetAwaiter().GetResult());
            }
        }

        [Test]
        public void ModelsCapability_IsOnTheEvaTransport_AndAbsentFromReplay()
        {
            using (EvaSseTransport eva = new EvaSseTransport())
            {
                Assert.IsTrue(eva.Supports(AgentCapability.Models));
                Assert.IsInstanceOf<IModelSelector>(eva);
            }
            using (ReplayTransport replay = new ReplayTransport())
            {
                Assert.IsFalse(replay.Supports(AgentCapability.Models));
                Assert.IsNotInstanceOf<IModelSelector>(replay);
            }
        }
    }
}
