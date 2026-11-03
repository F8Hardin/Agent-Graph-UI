using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.Tests
{
    /// <summary>The reducer's run-finished signal, begin-turn and history; the queue; and the chat session's send pipeline.</summary>
    public class ChatTests
    {
        private sealed class FakeTransport : IAgentTransport
        {
            public readonly List<(string chat, string prompt, List<HistoryItem> history)> Sent = new List<(string, string, List<HistoryItem>)>();
            public readonly List<string> Stopped = new List<string>();
            public Exception FailNextSend;
            public IReadOnlyCollection<string> Capabilities => new string[0];
            public event Action<AgentEvent> OnEvent { add { } remove { } }

            public Task<string> SendAsync(string chatId, string prompt, CancellationToken ct) => SendAsync(chatId, prompt, null, ct);

            public Task<string> SendAsync(string chatId, string prompt, IReadOnlyList<HistoryItem> history, CancellationToken ct)
            {
                lock (Sent)
                {
                    Sent.Add((chatId, prompt, history == null ? null : new List<HistoryItem>(history)));
                }
                if (FailNextSend != null)
                {
                    Exception e = FailNextSend;
                    FailNextSend = null;
                    return Task.FromException<string>(e);
                }
                return Task.FromResult("job-" + Sent.Count);
            }

            public Task StopAsync(string jobId)
            {
                lock (Stopped) { Stopped.Add(jobId); }
                return Task.CompletedTask;
            }

            public bool TryDequeue(out AgentEvent ev)
            {
                ev = default;
                return false;
            }

            public int SentCount { get { lock (Sent) { return Sent.Count; } } }
        }

        private static ConsoleReducer NewReducer() => new ConsoleReducer(new GraphModel("EVA", SampleInventory.Eva()), () => 0);

        private static void Reply(ConsoleReducer r, string text)
        {
            r.Apply(AgentEvent.Content(text));
            r.Apply(AgentEvent.RunEnded());
        }

        /// <summary>Pump the session until the condition holds; the pipeline hops threads, so a test has to wait for it.</summary>
        private static void WaitFor(ChatSession s, Func<bool> cond, string what)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < end)
            {
                s.Pump();
                if (cond()) return;
                Thread.Sleep(5);
            }
            s.Pump();
            Assert.IsTrue(cond(), "timed out waiting for " + what);
        }

        // ------------------------------------------------------------------ reducer

        [Test]
        public void BeginTurn_KeepsTheTranscript_AndMarksARunInFlightWhileThePhaseIsStillIdle()
        {
            ConsoleReducer r = NewReducer();
            r.BeginTurn("first");
            Reply(r, "answer one");
            r.BeginTurn("second");

            Assert.AreEqual(3, r.State.Transcript.Count); // first, answer one, second: nothing was wiped
            Assert.AreEqual(Phase.Idle, r.State.Phase);   // no thought or tool event yet...
            Assert.IsTrue(r.State.RunInFlight);           // ...but a run is out, which is what the queue must read
        }

        [Test]
        public void RunFinished_FiresOnRunEnded_AndOnError_AndOnlyOncePerRun()
        {
            ConsoleReducer r = NewReducer();
            int n = 0;
            r.RunFinished += _ => n++;

            r.BeginTurn("a");
            r.Apply(AgentEvent.RunEnded());
            Assert.AreEqual(1, n);
            Assert.IsFalse(r.State.RunInFlight);

            r.Apply(AgentEvent.Error("late")); // nothing in flight: an error message, but no second finish
            Assert.AreEqual(1, n);

            r.BeginTurn("b");
            r.Apply(AgentEvent.Error("Stopped."));
            Assert.AreEqual(2, n);
            Assert.IsFalse(r.State.RunInFlight);
        }

        [Test]
        public void RunFinished_FiresOnAnErrorEvenThoughThePhaseWasAlreadyIdle()
        {
            ConsoleReducer r = NewReducer();
            int phaseChanges = 0;
            r.PhaseChanged += _ => phaseChanges++;
            int finished = 0;
            r.RunFinished += _ => finished++;

            r.BeginTurn("a"); // a send that fails before any event: the phase never leaves Idle
            r.Apply(AgentEvent.Error("EVA rejected the prompt: 500"));

            Assert.AreEqual(0, phaseChanges);
            Assert.AreEqual(1, finished);
        }

        [Test]
        public void RunFinished_SummarisesTheTurn_ForPersisting()
        {
            ConsoleReducer r = NewReducer();
            TurnSummary got = default;
            r.RunFinished += s => got = s;
            r.Apply(AgentEvent.Meta("m1", 0.5f, "j", 4096));
            r.BeginTurn("q");
            r.Apply(AgentEvent.Meta("m1", 0.5f, "j", 4096));
            r.Apply(AgentEvent.Thought("hm"));
            r.Apply(AgentEvent.Content("hello "));
            r.Apply(AgentEvent.Content("world"));
            r.Apply(AgentEvent.RunEnded());

            Assert.AreEqual("hello world", got.Content);
            Assert.AreEqual("hm", got.Thinking);
            Assert.IsNull(got.ErrorText);
            Assert.AreEqual("m1", got.Model);
            Assert.AreEqual(4096, got.ContextLength);
        }

        [Test]
        public void BuildHistory_HasCompletedUserAndAssistantTurns_AndSkipsErrorsAndReasoning()
        {
            ConsoleReducer r = NewReducer();
            r.BeginTurn("one");
            r.Apply(AgentEvent.Thought("thinking"));
            Reply(r, "reply one");
            r.BeginTurn("two");
            r.Apply(AgentEvent.Error("boom"));
            r.Apply(AgentEvent.Content("streaming")); // still open

            List<HistoryItem> h = r.BuildHistory();

            Assert.AreEqual(3, h.Count);
            Assert.AreEqual("user", h[0].Role);
            Assert.AreEqual("one", h[0].Content);
            Assert.AreEqual("assistant", h[1].Role);
            Assert.AreEqual("reply one", h[1].Content);
            Assert.AreEqual("two", h[2].Content); // its error reply is left out, as the web client does
        }

        [Test]
        public void LoadTranscript_ReplacesTheConversation_WithNothingLeftOpen()
        {
            ConsoleReducer r = NewReducer();
            r.BeginTurn("old");
            r.LoadTranscript(new List<StoredMessage>
            {
                new StoredMessage("user", "hi"),
                new StoredMessage("assistant", "hello", "pondering"),
                new StoredMessage("assistant", "bad", null, true)
            });

            Assert.AreEqual(4, r.State.Transcript.Count);
            Assert.AreEqual(MessageRole.User, r.State.Transcript[0].Role);
            Assert.AreEqual(MessageRole.Reasoning, r.State.Transcript[1].Role);
            Assert.AreEqual("hello", r.State.Transcript[2].Text);
            Assert.IsTrue(r.State.Transcript[3].IsError);
            foreach (ChatMessage m in r.State.Transcript) Assert.IsFalse(m.IsOpen);
            Assert.IsFalse(r.State.RunInFlight);

            r.ClearConversation();
            Assert.AreEqual(0, r.State.Transcript.Count);
        }

        [Test]
        public void OpeningAnotherChat_KeepsTheServersModelReadout_ButANewTurnClearsIt()
        {
            ConsoleReducer r = NewReducer();
            r.Apply(AgentEvent.Meta("qwen", 0.6f, "j1", 8192));

            r.LoadTranscript(new List<StoredMessage> { new StoredMessage("user", "hi") });
            Assert.AreEqual("qwen", r.State.Model);
            Assert.AreEqual(8192, r.State.ContextLength);

            r.ClearConversation();
            Assert.AreEqual("qwen", r.State.Model);

            r.BeginTurn("go"); // a new run re-reports its meta, so the stale one goes
            Assert.IsNull(r.State.Model);
        }

        // ------------------------------------------------------------------ queue

        [Test]
        public void PromptQueue_IsFirstInFirstOut_IgnoresBlanks_AndClears()
        {
            PromptQueue q = new PromptQueue();
            q.Enqueue("a");
            q.Enqueue("  ");
            q.Enqueue("b");
            Assert.AreEqual(2, q.Count);
            Assert.IsTrue(q.TryTake(out string x));
            Assert.AreEqual("a", x);
            Assert.AreEqual(1, q.Clear());
            Assert.IsFalse(q.TryTake(out _));
        }

        // ------------------------------------------------------------------ session

        [Test]
        public void Session_FirstSend_CreatesTheChat_SavesTheUserMessage_AndSendsWithTheServersChatId()
        {
            ConsoleReducer r = NewReducer();
            FakeTransport t = new FakeTransport();
            MemoryChatStore store = new MemoryChatStore();
            ChatSession s = new ChatSession(r, t, store);
            string selected = "unset";
            s.SelectionChanged += id => selected = id;

            s.Submit("hello there");
            WaitFor(s, () => t.SentCount == 1 && s.SelectedId != null, "the send and the adopted chat id");

            Assert.IsNotNull(t.Sent[0].chat);
            Assert.AreEqual(t.Sent[0].chat, s.SelectedId);
            Assert.AreEqual(s.SelectedId, selected);
            IReadOnlyList<StoredMessage> saved = store.MessagesOf(s.SelectedId);
            Assert.AreEqual(1, saved.Count);
            Assert.AreEqual("user", saved[0].Role);
            Assert.AreEqual("hello there", saved[0].Content);
            s.Dispose();
        }

        [Test]
        public void Session_SendsTheEarlierTurnsAsHistory_NotJustThePrompt()
        {
            ConsoleReducer r = NewReducer();
            FakeTransport t = new FakeTransport();
            ChatSession s = new ChatSession(r, t, new MemoryChatStore());

            s.Submit("one");
            WaitFor(s, () => t.SentCount == 1, "first send");
            Assert.AreEqual(0, t.Sent[0].history.Count);
            Reply(r, "reply one");

            s.Submit("two");
            WaitFor(s, () => t.SentCount == 2, "second send");

            List<HistoryItem> h = t.Sent[1].history;
            Assert.AreEqual(2, h.Count);
            Assert.AreEqual("one", h[0].Content);
            Assert.AreEqual("reply one", h[1].Content);
            s.Dispose();
        }

        [Test]
        public void Session_ASecondSendBeforeAnyEventArrives_IsQueued_NotStarted()
        {
            ConsoleReducer r = NewReducer();
            FakeTransport t = new FakeTransport();
            ChatSession s = new ChatSession(r, t, new MemoryChatStore());

            s.Submit("first");
            Assert.AreEqual(Phase.Idle, r.State.Phase); // the gap: no event yet, so the phase looks idle
            s.Submit("second");

            Assert.AreEqual(1, s.QueuedCount);
            WaitFor(s, () => t.SentCount == 1, "first send");
            Thread.Sleep(50);
            s.Pump();
            Assert.AreEqual(1, t.SentCount); // the second did not jump the run
            s.Dispose();
        }

        [Test]
        public void Session_QueuedMessage_SendsAutomaticallyWhenTheRunEnds()
        {
            ConsoleReducer r = NewReducer();
            FakeTransport t = new FakeTransport();
            ChatSession s = new ChatSession(r, t, new MemoryChatStore());

            s.Submit("first");
            s.Submit("second");
            WaitFor(s, () => t.SentCount == 1, "first send");
            Reply(r, "done");

            WaitFor(s, () => t.SentCount == 2, "the queued send");
            Assert.AreEqual("second", t.Sent[1].prompt);
            Assert.AreEqual(0, s.QueuedCount);
            s.Dispose();
        }

        [Test]
        public void Session_AFailedSend_RaisesRunFinished_SoTheQueueDoesNotHang()
        {
            ConsoleReducer r = NewReducer();
            FakeTransport t = new FakeTransport { FailNextSend = new InvalidOperationException("EVA rejected the prompt: 500") };
            ChatSession s = new ChatSession(r, t, new MemoryChatStore());

            s.Submit("first");
            s.Submit("second");

            // The failure becomes an Error event, which ends the run, which releases the queue.
            WaitFor(s, () => t.SentCount == 2, "the queue to move on after the failure");
            Assert.AreEqual("second", t.Sent[1].prompt);
            s.Dispose();
        }

        [Test]
        public void Session_Stop_ClearsTheQueue_AndAsksTheServerToStopTheRun()
        {
            ConsoleReducer r = NewReducer();
            FakeTransport t = new FakeTransport();
            ChatSession s = new ChatSession(r, t, new MemoryChatStore());

            s.Submit("first");
            s.Submit("second");
            s.Submit("third");
            WaitFor(s, () => t.SentCount == 1, "first send");
            r.Apply(AgentEvent.Meta("m", 0.5f, "job-1", null));

            s.Stop();

            Assert.AreEqual(0, s.QueuedCount);
            WaitFor(s, () => t.Stopped.Count == 1, "the stop request");
            Assert.AreEqual("job-1", t.Stopped[0]);
            r.Apply(AgentEvent.Error("Stopped.")); // what EVA then sends
            Thread.Sleep(50);
            s.Pump();
            Assert.AreEqual(1, t.SentCount); // the cleared messages never went out
            s.Dispose();
        }

        [Test]
        public void Session_PersistsTheAssistantReply_AfterTheRunFinishes()
        {
            ConsoleReducer r = NewReducer();
            FakeTransport t = new FakeTransport();
            MemoryChatStore store = new MemoryChatStore();
            ChatSession s = new ChatSession(r, t, store);

            s.Submit("ask");
            WaitFor(s, () => t.SentCount == 1 && s.SelectedId != null, "send and the adopted chat id");
            r.Apply(AgentEvent.Thought("considering"));
            Reply(r, "the answer");

            string id = s.SelectedId;
            WaitFor(s, () => store.MessagesOf(id).Count == 2, "the reply to be saved");
            IReadOnlyList<StoredMessage> saved = store.MessagesOf(id);
            Assert.AreEqual("user", saved[0].Role);
            Assert.AreEqual("assistant", saved[1].Role);
            Assert.AreEqual("the answer", saved[1].Content);
            Assert.AreEqual("considering", saved[1].Thinking);
            s.Dispose();
        }

        [Test]
        public void Session_SelectLoadsTheStoredChat_AndNewChatEmptiesIt()
        {
            ConsoleReducer r = NewReducer();
            MemoryChatStore store = new MemoryChatStore();
            string id = store.Seed("Old", new StoredMessage("user", "q"), new StoredMessage("assistant", "a"));
            ChatSession s = new ChatSession(r, new FakeTransport(), store);

            s.Refresh();
            WaitFor(s, () => s.Chats.Count == 1, "the list");
            Assert.AreEqual(2, s.Chats[0].MessageCount);

            s.Select(id);
            WaitFor(s, () => s.SelectedId == id, "the chat to load");
            Assert.AreEqual(2, r.State.Transcript.Count);

            s.NewChat();
            Assert.IsNull(s.SelectedId);
            Assert.AreEqual(0, r.State.Transcript.Count);
            s.Dispose();
        }

        [Test]
        public void Session_RefusesToSwitchChats_WhileARunIsInFlight()
        {
            ConsoleReducer r = NewReducer();
            MemoryChatStore store = new MemoryChatStore();
            string other = store.Seed("Other", new StoredMessage("user", "q"));
            ChatSession s = new ChatSession(r, new FakeTransport(), store);

            s.Submit("busy");
            s.Select(other);
            Thread.Sleep(50);
            s.Pump();

            Assert.AreNotEqual(other, s.SelectedId);
            Assert.IsNotNull(s.Error);
            s.Dispose();
        }

        [Test]
        public void Session_RestoresTheRememberedChat_OnceTheListArrives()
        {
            ConsoleReducer r = NewReducer();
            MemoryChatStore store = new MemoryChatStore();
            string id = store.Seed("Remembered", new StoredMessage("user", "q"));
            ChatSession s = new ChatSession(r, new FakeTransport(), store);

            s.Refresh(id);

            WaitFor(s, () => s.SelectedId == id, "the remembered chat to open");
            s.Dispose();
        }

        // ------------------------------------------------------------------ wire

        [Test]
        public void SubmitPromptRequest_CarriesHistory_AsEvasPromptDataExpectsIt()
        {
            string json = new SubmitPromptRequest("now", "c1", new[]
            {
                new HistoryItem("user", "say \"hi\""),
                new HistoryItem("assistant", "hi")
            }).ToJson();

            Assert.AreEqual(
                "{\"prompt\":\"now\",\"chat_id\":\"c1\",\"history\":[{\"role\":\"user\",\"content\":\"say \\\"hi\\\"\"},{\"role\":\"assistant\",\"content\":\"hi\"}]}",
                json);
            Assert.AreEqual("{\"prompt\":\"x\"}", new SubmitPromptRequest("x", null, new List<HistoryItem>()).ToJson());
        }

        [Test]
        public void EvaChatStore_ParsesTheChatList_AndAChatWithItsMessages()
        {
            Assert.IsTrue(EvaChatStore.TryParseList(
                "{\"chats\":[{\"id\":\"a\",\"title\":\"T\",\"last_run_at\":null,\"updated_at\":\"2026-01-01T00:00:00\",\"message_count\":3},{\"title\":\"no id\"}]}",
                out List<ChatSummary> rows));
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("T", rows[0].Title);
            Assert.AreEqual(3, rows[0].MessageCount);
            Assert.AreEqual("2026-01-01T00:00:00", rows[0].LastRun);

            Assert.IsTrue(EvaChatStore.TryParseDetail(
                "{\"id\":\"a\",\"title\":\"T\",\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\",\"thinking\":\"t\",\"is_error\":false,\"model\":\"m\",\"temperature\":0.6,\"context_length\":8192}]}",
                out ChatDetail d));
            Assert.AreEqual(2, d.Messages.Count);
            Assert.AreEqual("t", d.Messages[1].Thinking);
            Assert.AreEqual(8192, d.Messages[1].ContextLength);
        }

        [Test]
        public void EvaChatStore_MessagesJson_MatchesTheShapeThePostEndpointTakes()
        {
            string json = EvaChatStore.MessagesJson(new[] { new StoredMessage("user", "hi \"x\"") });
            Assert.IsTrue(MiniJson.TryParse(json, out object root));
            Dictionary<string, object> obj = (Dictionary<string, object>)root;
            Dictionary<string, object> m = (Dictionary<string, object>)((List<object>)obj["messages"])[0];
            Assert.AreEqual("user", m["role"]);
            Assert.AreEqual("hi \"x\"", m["content"]);
            Assert.AreEqual(false, m["is_error"]);
            Assert.IsNull(m["model"]);
        }
    }
}
