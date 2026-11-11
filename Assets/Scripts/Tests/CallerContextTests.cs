using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentConsole.Domain;

namespace AgentConsole.Tests
{
    /// <summary>
    /// AsyncMode.CallerContext: the WebGL mode, where real I/O completes LATER on the main thread and there is no thread
    /// pool to hop through. Every test installs a hand-pumped single-threaded SynchronizationContext (the stand-in for
    /// Unity's), completes the fakes' tasks from the test thread and settles by pumping by hand. Nothing here blocks on
    /// async code, because blocking on it under Unity's context deadlocks the editor.
    /// </summary>
    [TestFixture]
    public class CallerContextTests
    {
        private sealed class ManualContext : SynchronizationContext
        {
            private readonly Queue<KeyValuePair<SendOrPostCallback, object>> _q = new Queue<KeyValuePair<SendOrPostCallback, object>>();

            public override void Post(SendOrPostCallback d, object state)
            {
                lock (_q) _q.Enqueue(new KeyValuePair<SendOrPostCallback, object>(d, state));
            }

            public override void Send(SendOrPostCallback d, object state) => d(state);

            /// <summary>Run what was posted, on the calling thread. True if anything ran.</summary>
            public bool RunAll()
            {
                bool any = false;
                while (true)
                {
                    KeyValuePair<SendOrPostCallback, object> item;
                    lock (_q)
                    {
                        if (_q.Count == 0) return any;
                        item = _q.Dequeue();
                    }
                    item.Key(item.Value);
                    any = true;
                }
            }
        }

        /// <summary>Every call returns a task that stays pending until the test completes it, as a network request does.</summary>
        private sealed class PendingStore : IChatStore
        {
            public readonly Queue<TaskCompletionSource<IReadOnlyList<ChatSummary>>> Lists = new Queue<TaskCompletionSource<IReadOnlyList<ChatSummary>>>();
            public readonly Queue<TaskCompletionSource<string>> Creates = new Queue<TaskCompletionSource<string>>();
            public readonly Queue<TaskCompletionSource<ChatDetail>> Loads = new Queue<TaskCompletionSource<ChatDetail>>();
            public readonly Queue<TaskCompletionSource<bool>> Saves = new Queue<TaskCompletionSource<bool>>();
            public readonly List<int> CallThreads = new List<int>();

            private T Track<T>(Queue<TaskCompletionSource<T>> q, out Task<T> task)
            {
                CallThreads.Add(Thread.CurrentThread.ManagedThreadId);
                TaskCompletionSource<T> tcs = new TaskCompletionSource<T>();
                q.Enqueue(tcs);
                task = tcs.Task;
                return default(T);
            }

            public Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken ct) { Track(Lists, out var t); return t; }
            public Task<string> CreateAsync(string title, CancellationToken ct) { Track(Creates, out var t); return t; }
            public Task<ChatDetail> LoadAsync(string id, CancellationToken ct) { Track(Loads, out var t); return t; }
            public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;

            public Task SaveMessagesAsync(string id, IReadOnlyList<StoredMessage> messages, CancellationToken ct)
            {
                Track(Saves, out var t);
                return t;
            }
        }

        private sealed class PendingTransport : IAgentTransport
        {
            public readonly Queue<TaskCompletionSource<string>> Sends = new Queue<TaskCompletionSource<string>>();
            public readonly ManualResetEventSlim Called = new ManualResetEventSlim(false);
            public int SendThread = -1;

            public IReadOnlyCollection<string> Capabilities => Array.Empty<string>();
            public event Action<AgentEvent> OnEvent { add { } remove { } }
            public Task<string> SendAsync(string chatId, string prompt, CancellationToken ct) => SendAsync(chatId, prompt, null, ct);

            public Task<string> SendAsync(string chatId, string prompt, IReadOnlyList<HistoryItem> history, CancellationToken ct)
            {
                SendThread = Thread.CurrentThread.ManagedThreadId;
                TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
                Sends.Enqueue(tcs);
                Called.Set();
                return tcs.Task;
            }

            public Task StopAsync(string jobId) => Task.CompletedTask;
            public bool TryDequeue(out AgentEvent ev) { ev = default(AgentEvent); return false; }
        }

        private sealed class PendingModels : IModelSelector
        {
            public readonly Queue<TaskCompletionSource<ModelCatalog>> Lists = new Queue<TaskCompletionSource<ModelCatalog>>();
            public Task<ModelCatalog> GetModelsAsync(CancellationToken ct) { var t = new TaskCompletionSource<ModelCatalog>(); Lists.Enqueue(t); return t.Task; }
            public Task SetModelAsync(string modelId, int? contextLength, CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class PendingInventory : IInventorySource
        {
            public readonly Queue<TaskCompletionSource<InventorySnapshot>> Fetches = new Queue<TaskCompletionSource<InventorySnapshot>>();
            public readonly Queue<TaskCompletionSource<InventorySnapshot>> Sets = new Queue<TaskCompletionSource<InventorySnapshot>>();
            public Task<InventorySnapshot> FetchAsync(CancellationToken ct) { var t = new TaskCompletionSource<InventorySnapshot>(); Fetches.Enqueue(t); return t.Task; }

            public Task<InventorySnapshot> SetPausedAsync(
                IReadOnlyDictionary<string, bool> servers, IReadOnlyDictionary<string, bool> tools, CancellationToken ct)
            {
                var t = new TaskCompletionSource<InventorySnapshot>();
                Sets.Enqueue(t);
                return t.Task;
            }
        }

        private ManualContext _ctx;
        private SynchronizationContext _saved;
        private int _me;

        [SetUp]
        public void SetUp()
        {
            _saved = SynchronizationContext.Current;
            _ctx = new ManualContext();
            SynchronizationContext.SetSynchronizationContext(_ctx);
            _me = Thread.CurrentThread.ManagedThreadId;
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(_saved);

        /// <summary>Pump the context and the session until nothing moves, as frames would.</summary>
        private void Settle(ChatSession s = null)
        {
            for (int i = 0; i < 20; i++)
            {
                bool a = _ctx.RunAll();
                bool b = s != null && s.Pump();
                if (!a && !b) return;
            }
            Assert.Fail("did not settle");
        }

        private static ServerInfo Server(string name) => new ServerInfo(name, "mcp", ServerInfo.StatusConnected, "t1", "t2");

        [Test]
        public void ChatTurn_CompletesLaterWithNoPoolHop()
        {
            ConsoleReducer r = new ConsoleReducer(new GraphModel("EVA"));
            PendingStore store = new PendingStore();
            PendingTransport transport = new PendingTransport();
            ChatSession s = new ChatSession(r, transport, store, asyncMode: AsyncMode.CallerContext);

            s.Submit("hello");
            Assert.AreEqual(1, store.Creates.Count, "the turn starts on the caller's thread, not the pool");
            store.Creates.Dequeue().SetResult("chat-1");
            Settle(s);
            Assert.AreEqual("chat-1", s.SelectedId);
            Assert.AreEqual(1, store.Saves.Count);
            store.Saves.Dequeue().SetResult(true); // the user message
            Settle(s);
            Assert.AreEqual(1, transport.Sends.Count, "the send follows the save, still without the pool");
            Assert.AreEqual(_me, transport.SendThread);
            transport.Sends.Dequeue().SetResult("job-1");
            r.Apply(AgentEvent.Meta("m", 0.5f, "job-1"));
            r.Apply(AgentEvent.Content("hi there"));
            r.Apply(AgentEvent.RunEnded());
            Settle(s);
            Assert.AreEqual(1, store.Saves.Count, "the reply is saved after the run");
            store.Saves.Dequeue().SetResult(true);
            Settle(s);
            Assert.GreaterOrEqual(store.Lists.Count, 1, "a list refresh follows the save");
            // Adopting the new chat and saving the reply each asked for a list; the newest one wins, so answer them all.
            while (store.Lists.Count > 0) store.Lists.Dequeue().SetResult(new List<ChatSummary> { new ChatSummary("chat-1", "hello", null, 2) });
            Settle(s);
            Assert.AreEqual(1, s.Chats.Count);
            foreach (int t in store.CallThreads) Assert.AreEqual(_me, t);
            Assert.IsFalse(r.State.RunInFlight);
        }

        [Test]
        public void ChatList_RefreshAndSelect_AppliedOnPump()
        {
            ConsoleReducer r = new ConsoleReducer(new GraphModel("EVA"));
            PendingStore store = new PendingStore();
            ChatSession s = new ChatSession(r, new PendingTransport(), store, asyncMode: AsyncMode.CallerContext);

            s.Refresh();
            Assert.AreEqual(ChatListPhase.Loading, s.ListPhase);
            store.Lists.Dequeue().SetResult(new List<ChatSummary> { new ChatSummary("a", "A", null, 1), new ChatSummary("b", "B", null, 0) });
            Assert.IsTrue(s.Pump(), "the result is already queued: nothing hopped through a pool");
            Assert.AreEqual(ChatListPhase.Idle, s.ListPhase);
            Assert.AreEqual(2, s.Chats.Count);

            s.Select("a");
            store.Loads.Dequeue().SetResult(new ChatDetail("a", "A", new List<StoredMessage> { new StoredMessage("user", "q"), new StoredMessage("assistant", "r") }));
            Assert.IsTrue(s.Pump());
            Assert.AreEqual("a", s.SelectedId);
        }

        [Test]
        public void ChatList_Failure_IsReported()
        {
            PendingStore store = new PendingStore();
            ChatSession s = new ChatSession(new ConsoleReducer(new GraphModel("EVA")), new PendingTransport(), store, asyncMode: AsyncMode.CallerContext);
            s.Refresh();
            store.Lists.Dequeue().SetException(new InvalidOperationException("no route to EVA"));
            s.Pump();
            Assert.AreEqual(ChatListPhase.Failed, s.ListPhase);
            StringAssert.Contains("no route to EVA", s.Error);
        }

        [Test]
        public void ModelRefresh_CompletesLater()
        {
            PendingModels sel = new PendingModels();
            ModelController m = new ModelController(sel, AsyncMode.CallerContext);
            m.Refresh();
            Assert.AreEqual(ModelControlPhase.Loading, m.Phase);
            sel.Lists.Dequeue().SetResult(new ModelCatalog(new[] { new ModelInfo("s", "S", true, 4096) }, "s", 4096, 4096, null));
            Assert.IsTrue(m.Pump());
            Assert.AreEqual(ModelControlPhase.Ready, m.Phase);
            Assert.AreEqual("s", m.Catalog.CurrentModelId);
        }

        [Test]
        public void InventoryRefreshAndPauseToggle_CompleteLater()
        {
            PendingInventory src = new PendingInventory();
            GraphModel g = new GraphModel("EVA");
            double now = 0;
            InventoryController inv = new InventoryController(src, g, () => now, AsyncMode.CallerContext);

            inv.Update(false); // first idle frame starts the fetch
            Assert.AreEqual(1, src.Fetches.Count);
            Assert.AreEqual(InventoryPhase.Loading, inv.Phase);
            src.Fetches.Dequeue().SetResult(InventorySnapshot.From(new[] { Server("a"), Server("b") }));
            inv.Update(false);
            Assert.AreEqual(InventoryPhase.Ready, inv.Phase);
            Assert.AreEqual(2, inv.Servers.Count);
            Assert.AreEqual(1, inv.RebuildCount);

            inv.SetServerPaused("a", true);
            Assert.IsTrue(inv.IsServerPending("a"));
            Assert.AreEqual(1, src.Sets.Count);
            src.Sets.Dequeue().SetResult(InventorySnapshot.From(new[] { Server("a").WithPaused(true), Server("b") }));
            inv.Update(false);
            Assert.IsFalse(inv.IsServerPending("a"));
            Assert.IsTrue(inv.FindServer("a").Paused);
            Assert.IsNull(inv.ToggleError);
        }

        [Test]
        public void ThreadPoolMode_StillHopsToThePool()
        {
            // Desktop is unchanged: the default turn runs on the thread pool, not on the caller.
            ConsoleReducer r = new ConsoleReducer(new GraphModel("EVA"));
            PendingTransport transport = new PendingTransport();
            ChatSession s = new ChatSession(r, transport, new MemoryChatStore());
            s.Submit("hello");
            Assert.IsTrue(transport.Called.Wait(5000), "the send never started");
            Assert.AreNotEqual(_me, transport.SendThread);
            s.Dispose();
        }
    }
}
