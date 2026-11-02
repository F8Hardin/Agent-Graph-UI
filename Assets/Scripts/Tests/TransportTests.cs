using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    public class WireParserTests
    {
        private static AgentEvent Parse(string json)
        {
            Assert.IsTrue(WireParser.TryParse(json, out AgentEvent ev), "should parse: " + json);
            return ev;
        }

        [Test]
        public void Meta()
        {
            AgentEvent ev = Parse("{\"type\":\"meta\",\"data\":{\"model\":\"qwen\",\"temperature\":0.4,\"job_id\":\"j1\",\"context_length\":32000}}");
            Assert.AreEqual(AgentEventKind.Meta, ev.Kind);
            Assert.AreEqual("qwen", ev.Model);
            Assert.AreEqual(0.4f, ev.Temperature.Value, 1e-6f);
            Assert.AreEqual("j1", ev.JobId);
        }

        [Test]
        public void ThoughtAndContent()
        {
            AgentEvent t = Parse("{\"type\":\"thought\",\"data\":\"hm \\u2014 ok\\n\"}");
            Assert.AreEqual(AgentEventKind.Thought, t.Kind);
            Assert.AreEqual("hm \u2014 ok\n", t.Text);

            AgentEvent c = Parse("{\"type\":\"content\",\"data\":\"answer\"}");
            Assert.AreEqual(AgentEventKind.Content, c.Kind);
            Assert.AreEqual("answer", c.Text);
        }

        [Test]
        public void ToolCallbacks_CarryRunIdToolInputOutputAndError()
        {
            AgentEvent s = Parse("{\"type\":\"ai_callback\",\"data\":{\"event\":\"tool_start\",\"tool\":\"memory__vault_search\",\"input\":\"{\\\"q\\\":1}\",\"run_id\":\"r1\"}}");
            Assert.AreEqual(AgentEventKind.ToolCallback, s.Kind);
            Assert.AreEqual(CallbackKind.ToolStart, s.Callback);
            Assert.AreEqual("memory__vault_search", s.Tool);
            Assert.AreEqual("{\"q\":1}", s.Input);
            Assert.AreEqual("r1", s.RunId);

            AgentEvent e = Parse("{\"type\":\"ai_callback\",\"data\":{\"event\":\"tool_end\",\"tool\":\"t\",\"output\":\"4 notes\",\"run_id\":\"r1\"}}");
            Assert.AreEqual(CallbackKind.ToolEnd, e.Callback);
            Assert.AreEqual("4 notes", e.Output);

            AgentEvent x = Parse("{\"type\":\"ai_callback\",\"data\":{\"event\":\"tool_error\",\"tool\":\"t\",\"error\":\"wedged\",\"run_id\":\"r2\"}}");
            Assert.AreEqual(CallbackKind.ToolError, x.Callback);
            Assert.AreEqual("wedged", x.ErrorText);
            Assert.AreEqual("r2", x.RunId);
        }

        [Test]
        public void CallbackKinds_ChatModelStartLlmStartAndLlmEnd()
        {
            Assert.AreEqual(CallbackKind.ChatModelStart, Parse("{\"type\":\"ai_callback\",\"data\":{\"event\":\"chat_model_start\",\"prompt\":[{\"role\":\"user\"}]}}").Callback);
            Assert.AreEqual(CallbackKind.ChatModelStart, Parse("{\"type\":\"ai_callback\",\"data\":{\"event\":\"llm_start\",\"prompt\":\"p\"}}").Callback);
            Assert.AreEqual(CallbackKind.LlmEnd, Parse("{\"type\":\"ai_callback\",\"data\":{\"event\":\"llm_end\"}}").Callback);
        }

        [Test]
        public void NullToolAndRunId_AreAccepted()
        {
            // EVA's handler emits null when its run_id->name map misses.
            AgentEvent ev = Parse("{\"type\":\"ai_callback\",\"data\":{\"event\":\"tool_end\",\"tool\":null,\"output\":\"x\",\"run_id\":null}}");
            Assert.IsNull(ev.Tool);
            Assert.IsNull(ev.RunId);
            Assert.AreEqual("x", ev.Output);
        }

        [Test]
        public void ErrorEvent_StringAndObjectForms()
        {
            Assert.AreEqual("Stopped.", Parse("{\"type\":\"error\",\"data\":\"Stopped.\"}").ErrorText);
            Assert.AreEqual("bad", Parse("{\"type\":\"error\",\"data\":{\"message\":\"bad\"}}").ErrorText);
        }

        [Test]
        public void ObjectInput_IsKeptAsJsonText()
        {
            AgentEvent ev = Parse("{\"type\":\"ai_callback\",\"data\":{\"event\":\"tool_start\",\"tool\":\"t\",\"input\":{\"a\":1,\"b\":[true,null]},\"run_id\":\"r\"}}");
            Assert.AreEqual("{\"a\":1,\"b\":[true,null]}", ev.Input);
        }

        [Test]
        public void UnknownFieldsAreIgnored_UnknownTypesAndEventsAreRejected()
        {
            Parse("{\"type\":\"content\",\"data\":\"x\",\"future_field\":{\"y\":1}}");
            Assert.IsFalse(WireParser.TryParse("{\"type\":\"telemetry\",\"data\":1}", out _));
            Assert.IsFalse(WireParser.TryParse("{\"type\":\"ai_callback\",\"data\":{\"event\":\"future_event\"}}", out _));
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{\"type\":\"content\",\"data\":\"unterminated")]
        [TestCase("[1,2]")]
        [TestCase("{\"type\":\"content\"}")]
        public void MalformedPayloads_ReturnFalseWithoutThrowing(string json)
        {
            Assert.IsFalse(WireParser.TryParse(json, out _));
        }

        [Test]
        public void MiniJson_RoundTripsEscapesAndSurrogatePairs()
        {
            const string json = "{\"a\":\"\\u00e9\\ud83d\\ude00\\n\\\"q\\\"\",\"b\":[1,2.5,true,null],\"c\":{\"d\":\"x\"}}";
            Assert.IsTrue(MiniJson.TryParse(json, out object v));
            string again = MiniJson.Serialize(v);
            Assert.IsTrue(MiniJson.TryParse(again, out object v2));
            Assert.AreEqual(again, MiniJson.Serialize(v2));
            Dictionary<string, object> d = (Dictionary<string, object>)v;
            Assert.AreEqual("\u00e9\ud83d\ude00\n\"q\"", d["a"]);
        }

        [Test]
        public void MiniJson_RejectsDeepNestingInsteadOfOverflowingTheStack()
        {
            string deep = new string('[', 5000) + new string(']', 5000);
            Assert.IsFalse(MiniJson.TryParse(deep, out _));
        }

        [Test]
        public void SubmitPromptRequest_EscapesAndOmitsAnEmptyChatId()
        {
            Assert.AreEqual("{\"prompt\":\"say \\\"hi\\\"\\nnow\",\"chat_id\":\"c1\"}",
                new SubmitPromptRequest("say \"hi\"\nnow", "c1").ToJson());
            Assert.AreEqual("{\"prompt\":\"x\"}", new SubmitPromptRequest("x", null).ToJson());
        }
    }

    public class SseAndPolicyTests
    {
        [Test]
        public void SseParser_DispatchesOnBlankLine_IgnoresCommentsAndOtherFields()
        {
            SseParser p = new SseParser();
            Assert.IsFalse(p.Feed(": keepalive", out _));
            Assert.IsFalse(p.Feed("", out _), "a blank line with no data dispatches nothing");
            Assert.IsFalse(p.Feed("event: message", out _));
            Assert.IsFalse(p.Feed("id: 7", out _));
            Assert.IsFalse(p.Feed("data: {\"a\": 1}", out _));
            Assert.IsTrue(p.Feed("", out string data));
            Assert.AreEqual("{\"a\": 1}", data);
        }

        [Test]
        public void SseParser_JoinsMultipleDataLines_AndStripsOnlyOneLeadingSpace()
        {
            SseParser p = new SseParser();
            p.Feed("data:  two spaces", out _);
            p.Feed("data:no-space", out _);
            p.Feed("data", out _);
            Assert.IsTrue(p.Feed("", out string data));
            Assert.AreEqual(" two spaces\nno-space\n", data);
        }

        [Test]
        public void SseParser_Reset_DropsAHalfReadEvent()
        {
            SseParser p = new SseParser();
            p.Feed("data: half", out _);
            p.Reset();
            Assert.IsFalse(p.Feed("", out _));
        }

        [Test]
        public void ReconnectPolicy_BacksOffExponentiallyAndCaps()
        {
            ReconnectPolicy p = new ReconnectPolicy { InitialDelayMs = 500, MaxDelayMs = 15000, Factor = 2 };
            Assert.AreEqual(500, p.DelayFor(1).TotalMilliseconds, 0.001);
            Assert.AreEqual(1000, p.DelayFor(2).TotalMilliseconds, 0.001);
            Assert.AreEqual(2000, p.DelayFor(3).TotalMilliseconds, 0.001);
            Assert.AreEqual(15000, p.DelayFor(20).TotalMilliseconds, 0.001);
            Assert.AreEqual(15000, p.DelayFor(5000).TotalMilliseconds, 0.001);
        }

        [Test]
        public void ReconnectPolicy_JitterStaysWithinItsBand()
        {
            ReconnectPolicy p = new ReconnectPolicy { InitialDelayMs = 1000, Factor = 1, Jitter = 0.2 };
            Random rng = new Random(1);
            for (int i = 0; i < 200; i++)
            {
                double ms = p.DelayFor(1, rng).TotalMilliseconds;
                Assert.GreaterOrEqual(ms, 800);
                Assert.LessOrEqual(ms, 1200);
            }
        }
    }

    public class ReplayTransportTests
    {
        private static List<AgentEvent> Drain(IAgentTransport t)
        {
            List<AgentEvent> list = new List<AgentEvent>();
            while (t.TryDequeue(out AgentEvent ev)) list.Add(ev);
            return list;
        }

        [Test]
        public void ScriptIsThePrototypesScript()
        {
            Assert.AreEqual(20, ReplayTransport.Script.Count);
            Assert.AreEqual(13000, ReplayTransport.Script[ReplayTransport.Script.Count - 1].AtMs);
            int starts = 0, errors = 0;
            foreach (ScriptStep s in ReplayTransport.Script)
            {
                if (s.Event.Callback == CallbackKind.ToolStart) starts++;
                if (s.Event.Callback == CallbackKind.ToolError) errors++;
            }
            Assert.AreEqual(6, starts);
            Assert.AreEqual(1, errors);
        }

        [Test]
        public void Capabilities_AreAdvertisedByNameNotByAgent()
        {
            using (ReplayTransport t = new ReplayTransport())
            {
                Assert.IsTrue(t.Supports(AgentCapability.Chat));
                Assert.IsTrue(t.Supports(AgentCapability.Cancel));
                Assert.IsFalse(t.Supports(AgentCapability.Schedule));
            }
        }

        [Test]
        public void PlaysTheWholeScriptInOrder()
        {
            using (ReplayTransport t = new ReplayTransport(speed: 1000))
            {
                string job = Task.Run(() => t.SendAsync("chat", "go", CancellationToken.None)).GetAwaiter().GetResult();
                Assert.AreEqual(ReplayTransport.ReplayJobId, job);

                List<AgentEvent> got = new List<AgentEvent>();
                Stopwatch sw = Stopwatch.StartNew();
                // The script, then the transport's own RunEnded on the clean end of the stream.
                while (got.Count < ReplayTransport.Script.Count + 1 && sw.ElapsedMilliseconds < 10000)
                {
                    got.AddRange(Drain(t));
                    Thread.Sleep(2);
                }

                Assert.AreEqual(ReplayTransport.Script.Count + 1, got.Count);
                Assert.AreEqual(AgentEventKind.RunEnded, got[got.Count - 1].Kind);
                for (int i = 0; i < ReplayTransport.Script.Count; i++)
                {
                    Assert.AreEqual(ReplayTransport.Script[i].Event.ToString(), got[i].ToString(), "event " + i);
                }
            }
        }

        [Test]
        public void Stop_EndsTheRunWithStoppedAndNothingAfterIt()
        {
            using (ReplayTransport t = new ReplayTransport())
            {
                Task.Run(() => t.SendAsync("chat", "go", CancellationToken.None)).GetAwaiter().GetResult();
                Thread.Sleep(50);
                t.StopAsync(ReplayTransport.ReplayJobId).GetAwaiter().GetResult();
                Thread.Sleep(400);

                List<AgentEvent> got = Drain(t);
                Assert.Less(got.Count, ReplayTransport.Script.Count);
                AgentEvent last = got[got.Count - 1];
                Assert.AreEqual(AgentEventKind.Error, last.Kind);
                Assert.AreEqual("Stopped.", last.ErrorText);
            }
        }
    }

    public class EvaSseTransportTests
    {
        private sealed class FakeHandler : HttpMessageHandler
        {
            public readonly List<string> Requests = new List<string>();
            public Func<HttpRequestMessage, int, HttpResponseMessage> Respond;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                int n;
                lock (Requests)
                {
                    Requests.Add(request.Method + " " + request.RequestUri.PathAndQuery);
                    n = Requests.Count;
                }
                return Task.FromResult(Respond(request, n));
            }

            public string[] Snapshot()
            {
                lock (Requests) return Requests.ToArray();
            }
        }

        private sealed class ScriptedStream : Stream
        {
            private readonly byte[] _bytes;
            private readonly bool _throwAtEnd;
            private int _pos;

            public ScriptedStream(string body, bool throwAtEnd)
            {
                _bytes = Encoding.UTF8.GetBytes(body);
                _throwAtEnd = throwAtEnd;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos >= _bytes.Length)
                {
                    if (_throwAtEnd) throw new IOException("connection reset");
                    return 0;
                }
                int n = Math.Min(count, _bytes.Length - _pos);
                Array.Copy(_bytes, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static HttpResponseMessage Sse(string body, string jobId = null, bool dropAtEnd = false)
        {
            HttpResponseMessage resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ScriptedStream(body, dropAtEnd))
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            if (jobId != null) resp.Headers.Add("X-Job-Id", jobId);
            return resp;
        }

        private static string Chunk(string json) => "data: " + json + "\n\n";

        private const string MetaChunk = "{\"type\":\"meta\",\"data\":{\"model\":\"m\",\"temperature\":0.5,\"job_id\":\"j1\"}}";
        private const string ThoughtChunk = "{\"type\":\"thought\",\"data\":\"hmm\"}";
        private const string StartChunk = "{\"type\":\"ai_callback\",\"data\":{\"event\":\"tool_start\",\"tool\":\"memory__vault_search\",\"input\":\"{}\",\"run_id\":\"r1\"}}";
        private const string EndChunk = "{\"type\":\"ai_callback\",\"data\":{\"event\":\"tool_end\",\"tool\":\"memory__vault_search\",\"output\":\"ok\",\"run_id\":\"r1\"}}";
        private const string ContentChunk = "{\"type\":\"content\",\"data\":\"done\"}";

        private static EvaSseTransport NewTransport(FakeHandler handler)
        {
            ReconnectPolicy fast = new ReconnectPolicy { InitialDelayMs = 1, MaxDelayMs = 5, Jitter = 0, MaxAttempts = 3, IdleTimeoutMs = 5000 };
            return new EvaSseTransport("http://eva.test:8000", new HttpClient(handler), null, fast);
        }

        private static string Send(EvaSseTransport t, string chatId = "chat-1") =>
            Task.Run(() => t.SendAsync(chatId, "hello", CancellationToken.None)).GetAwaiter().GetResult();

        private static void WaitUntilIdle(EvaSseTransport t)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (t.IsRunning)
            {
                if (sw.ElapsedMilliseconds > 10000) Assert.Fail("transport still running after 10s");
                Thread.Sleep(5);
            }
        }

        private static List<AgentEvent> Drain(IAgentTransport t)
        {
            List<AgentEvent> list = new List<AgentEvent>();
            while (t.TryDequeue(out AgentEvent ev)) list.Add(ev);
            return list;
        }

        [Test]
        public void ReadsTheStream_ParsesEvents_SkipsKeepalivesAndMalformedChunks_AndConfirmsTheEnd()
        {
            FakeHandler h = new FakeHandler();
            h.Respond = (req, n) => n == 1
                ? Sse(Chunk(MetaChunk) + ": keepalive\n\n" + Chunk(ThoughtChunk) + Chunk("{oops") + Chunk(StartChunk) + Chunk(EndChunk) + Chunk(ContentChunk), "j1")
                : Sse(string.Empty);

            using (EvaSseTransport t = NewTransport(h))
            {
                Assert.AreEqual("j1", Send(t));
                WaitUntilIdle(t);

                List<AgentEvent> got = Drain(t);
                // Five wire events, then RunEnded raised by the transport once the confirming reconnect found nothing new.
                Assert.AreEqual(6, got.Count);
                Assert.AreEqual(AgentEventKind.RunEnded, got[5].Kind);
                Assert.AreEqual(AgentEventKind.Meta, got[0].Kind);
                Assert.AreEqual(CallbackKind.ToolStart, got[2].Callback);
                Assert.AreEqual("r1", got[2].RunId);
                Assert.AreEqual(CallbackKind.ToolEnd, got[3].Callback);
                Assert.AreEqual(1, t.MalformedCount);

                string[] reqs = h.Snapshot();
                Assert.AreEqual("POST /submit_prompt", reqs[0]);
                // Six data chunks were consumed (the malformed one counts: it is the server's index).
                Assert.AreEqual("GET /jobs/j1/stream?start=6", reqs[1]);
                Assert.AreEqual(2, reqs.Length);
            }
        }

        [Test]
        public void ADroppedConnection_ResumesFromTheCursor_WithoutMissingOrRepeatingAChunk()
        {
            FakeHandler h = new FakeHandler();
            h.Respond = (req, n) =>
            {
                if (n == 1) return Sse(Chunk(MetaChunk) + Chunk(ThoughtChunk), "j1", dropAtEnd: true);
                if (n == 2) return Sse(Chunk(StartChunk) + Chunk(EndChunk) + Chunk(ContentChunk));
                return Sse(string.Empty);
            };

            using (EvaSseTransport t = NewTransport(h))
            {
                Send(t);
                WaitUntilIdle(t);

                List<AgentEvent> got = Drain(t);
                Assert.AreEqual(6, got.Count, "no chunk lost, none duplicated (5 wire events + RunEnded)");
                Assert.AreEqual(AgentEventKind.RunEnded, got[5].Kind);
                Assert.AreEqual(AgentEventKind.Meta, got[0].Kind);
                Assert.AreEqual(AgentEventKind.Thought, got[1].Kind);
                Assert.AreEqual(CallbackKind.ToolStart, got[2].Callback);
                Assert.AreEqual(CallbackKind.ToolEnd, got[3].Callback);
                Assert.AreEqual(AgentEventKind.Content, got[4].Kind);

                string[] reqs = h.Snapshot();
                Assert.AreEqual("GET /jobs/j1/stream?start=2", reqs[1]);
                Assert.AreEqual("GET /jobs/j1/stream?start=5", reqs[2]);
                Assert.GreaterOrEqual(t.ReconnectCount, 1);
            }
        }

        [Test]
        public void AnErrorEvent_EndsTheRunWithoutAConfirmingReconnect()
        {
            FakeHandler h = new FakeHandler();
            h.Respond = (req, n) => Sse(Chunk(MetaChunk) + Chunk("{\"type\":\"error\",\"data\":\"Stopped.\"}"), "j1");

            using (EvaSseTransport t = NewTransport(h))
            {
                Send(t);
                WaitUntilIdle(t);
                List<AgentEvent> got = Drain(t);
                Assert.AreEqual(AgentEventKind.Error, got[got.Count - 1].Kind);
                Assert.AreEqual(1, h.Snapshot().Length);
            }
        }

        [Test]
        public void GivesUpAfterMaxAttempts_WithAnErrorEventNamingTheJob()
        {
            FakeHandler h = new FakeHandler();
            h.Respond = (req, n) => n == 1
                ? Sse(Chunk(MetaChunk), "j1", dropAtEnd: true)
                : new HttpResponseMessage(HttpStatusCode.BadGateway);

            using (EvaSseTransport t = NewTransport(h))
            {
                Send(t);
                WaitUntilIdle(t);
                List<AgentEvent> got = Drain(t);
                AgentEvent last = got[got.Count - 1];
                Assert.AreEqual(AgentEventKind.Error, last.Kind);
                StringAssert.Contains("j1", last.ErrorText);
            }
        }

        [Test]
        public void TheJobIdFallsBackToTheMetaEvent_WhenTheHeaderIsMissing()
        {
            FakeHandler h = new FakeHandler();
            h.Respond = (req, n) => n == 1 ? Sse(Chunk(MetaChunk) + Chunk(ContentChunk)) : Sse(string.Empty);

            using (EvaSseTransport t = NewTransport(h))
            {
                Assert.AreEqual("j1", Send(t));
                WaitUntilIdle(t);
            }
        }

        [Test]
        public void ARejectedPrompt_ThrowsAndLeavesTheTransportReusable()
        {
            FakeHandler h = new FakeHandler();
            h.Respond = (req, n) => n == 1
                ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                : n == 2 ? Sse(Chunk(MetaChunk), "j1") : Sse(string.Empty);

            using (EvaSseTransport t = NewTransport(h))
            {
                Assert.Throws<HttpRequestException>(() => Send(t));
                Assert.IsFalse(t.IsRunning);
                Assert.AreEqual("j1", Send(t));
                WaitUntilIdle(t);
            }
        }

        [Test]
        public void StopAsync_PostsToTheStopEndpoint_AndTreatsAMissingRunAsStopped()
        {
            FakeHandler h = new FakeHandler();
            h.Respond = (req, n) => new HttpResponseMessage(n == 1 ? HttpStatusCode.OK : HttpStatusCode.NotFound);

            using (EvaSseTransport t = NewTransport(h))
            {
                t.StopAsync("j 1").GetAwaiter().GetResult();
                t.StopAsync("gone").GetAwaiter().GetResult();
                string[] reqs = h.Snapshot();
                Assert.AreEqual("POST /jobs/j%201/stop", reqs[0]);
                Assert.AreEqual("POST /jobs/gone/stop", reqs[1]);
            }
        }

        [Test]
        public void GetActiveJobIdAsync_ReadsTheActiveJob()
        {
            FakeHandler h = new FakeHandler();
            h.Respond = (req, n) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(n == 1 ? "{\"active\":{\"job_id\":\"j9\",\"chunks\":3}}" : "{\"active\":null}")
            };

            using (EvaSseTransport t = NewTransport(h))
            {
                Assert.AreEqual("j9", t.GetActiveJobIdAsync().GetAwaiter().GetResult());
                Assert.IsNull(t.GetActiveJobIdAsync().GetAwaiter().GetResult());
            }
        }

        [Test]
        public void Capabilities_ListWhatTheTransportImplements()
        {
            using (EvaSseTransport t = new EvaSseTransport())
            {
                Assert.AreEqual("http://10.0.0.219:8000", t.BaseUrl);
                Assert.IsTrue(t.Supports(AgentCapability.Stream));
                Assert.IsTrue(t.Supports(AgentCapability.Cancel));
                Assert.IsFalse(t.Supports(AgentCapability.Schedule));
            }
        }
    }
}
