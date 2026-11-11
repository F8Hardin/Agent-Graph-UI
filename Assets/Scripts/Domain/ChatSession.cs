using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    public enum ChatListPhase
    {
        /// <summary>No chat store (an agent without chat history). The Chats island says so.</summary>
        Unavailable,
        Idle,
        Loading,
        Failed
    }

    /// <summary>
    /// The conversation layer: the chat list and the selected chat, and the send pipeline (send, queue, stop,
    /// history, persistence). One class because those are one flow. A turn creates the chat if there is none, saves
    /// the user's message, sends with history, and on the run-finished signal saves the reply and starts the next
    /// queued message. The View reads this and calls Submit / Stop / Select; it never sees a transport.
    ///
    /// Same hand-off as <see cref="ModelController"/> (STANDARDS section 2 and 4): requests run on the thread pool,
    /// their results are queued, and the main thread applies them in <see cref="Pump"/>, so the reducer and this
    /// state have exactly one writer. IDs come from the server; none is invented here.
    /// </summary>
    public sealed class ChatSession : IDisposable
    {
        private readonly ConsoleReducer _r;
        private readonly IAgentTransport _transport;
        private readonly IChatStore _store;
        private readonly ConcurrentQueue<Action> _done = new ConcurrentQueue<Action>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly PromptQueue _queue = new PromptQueue();
        private readonly bool _inline;
        private readonly bool _caller; // AsyncMode.CallerContext: no pool hop anywhere, awaits resume on the captured context
        private readonly TaskScheduler _scheduler;
        private readonly object _chainGate = new object();
        private readonly List<ChatSummary> _chats = new List<ChatSummary>();
        private Task _chain = Task.CompletedTask;
        private int _listGen;
        private int _loadGen;
        private string _restoreId;
        private string _turnChatId;
        private volatile bool _stopRequested;
        private bool _stopSent;
        private bool _errorFromList;

        public string SelectedId { get; private set; }
        public IReadOnlyList<ChatSummary> Chats => _chats;
        public ChatListPhase ListPhase { get; private set; }

        /// <summary>Why the last chat operation failed, or null. Shown in the Chats island.</summary>
        public string Error { get; private set; }

        public int QueuedCount => _queue.Count;

        /// <summary>A run is in flight. See <see cref="ConsoleState.RunInFlight"/> for why this is not the phase.</summary>
        public bool IsBusy => _r.State.RunInFlight;

        public bool HasStore => _store != null;

        public string SelectedTitle
        {
            get
            {
                for (int i = 0; i < _chats.Count; i++)
                {
                    if (_chats[i].Id == SelectedId) return _chats[i].Title;
                }
                return null;
            }
        }

        public event Action Changed;

        /// <summary>The selected chat id changed (null = a new, unsaved chat). The host persists it, as the web client does with eva.lastChatId.</summary>
        public event Action<string> SelectionChanged;

        /// <param name="runInline">
        /// Run a turn on the caller's thread instead of the thread pool. For hosts with no threads (Unity WebGL, where
        /// thread-pool work never runs): the transport and the store must then complete synchronously or be pumped by the
        /// host, which the Replay transport in pumped mode and <see cref="MemoryChatStore"/> both do. Off by default;
        /// a store that really awaits I/O (EVA over HTTP) must keep the thread-pool path.
        /// </param>
        /// <param name="asyncMode">
        /// CallerContext is the WebGL mode for stores and transports that really await I/O (UnityWebRequest): the turn
        /// starts on the caller's thread and every continuation resumes on its SynchronizationContext, so a task that
        /// completes later still never touches the thread pool. Construct on the main thread. ThreadPool is the default.
        /// </param>
        public ChatSession(ConsoleReducer reducer, IAgentTransport transport, IChatStore store, bool runInline = false,
            AsyncMode asyncMode = AsyncMode.ThreadPool)
        {
            _inline = runInline;
            _caller = asyncMode == AsyncMode.CallerContext;
            _scheduler = AsyncModes.SchedulerFor(asyncMode);
            _r = reducer ?? throw new ArgumentNullException(nameof(reducer));
            _transport = transport;
            _store = store;
            ListPhase = store == null ? ChatListPhase.Unavailable : ChatListPhase.Idle;
            _r.RunFinished += OnRunFinished;
        }

        // ------------------------------------------------------------------ send, queue, stop

        /// <summary>
        /// Enter. Idle: sends. A run in flight: queues, to send automatically when it finishes. The button never
        /// decides this; while streaming it is always Stop (ChatView.tsx).
        /// </summary>
        public void Submit(string text)
        {
            text = text == null ? null : text.Trim();
            if (string.IsNullOrEmpty(text) || _transport == null) return;
            if (_r.State.RunInFlight)
            {
                _queue.Enqueue(text);
                Changed?.Invoke();
                return;
            }
            StartTurn(text);
        }

        /// <summary>Cancel the run AND clear the queue: one control, as the web client has it.</summary>
        public void Stop()
        {
            _queue.Clear();
            if (_r.State.RunInFlight)
            {
                _stopRequested = true;
                TrySendStop();
            }
            Changed?.Invoke();
        }

        private void StartTurn(string text)
        {
            List<HistoryItem> history = _r.BuildHistory(); // before the new message goes in
            _r.BeginTurn(text);
            _stopRequested = false;
            _stopSent = false;
            _turnChatId = SelectedId;
            string chatId = SelectedId;
            if (_inline || _caller)
            {
                // Everything RunTurnAsync awaits is already complete in this mode, so it finishes before returning.
                Task ignore = RunTurnAsync(text, history, chatId);
            }
            else
            {
                Task.Run(() => RunTurnAsync(text, history, chatId));
            }
            Changed?.Invoke();
        }

        private async Task RunTurnAsync(string text, List<HistoryItem> history, string chatId)
        {
            CancellationToken ct = _cts.Token;
            string id = chatId;
            if (_store != null)
            {
                // The chat must exist before the run starts, and the user's message is written now: it is final the
                // moment it is sent. Answering matters more than the bookkeeping, so a failure here still sends.
                try
                {
                    await Chain(async () =>
                    {
                        if (id == null)
                        {
                            string created = await _store.CreateAsync(TitleOf(text), ct).ConfigureAwait(_caller);
                            id = created;
                            Post(() => Adopt(created));
                        }
                        await _store.SaveMessagesAsync(id, new[] { new StoredMessage("user", text) }, ct).ConfigureAwait(_caller);
                    }).ConfigureAwait(_caller);
                }
                catch (ChatNotFoundException)
                {
                    id = null;
                    Post(DropSelection);
                }
                catch (Exception e)
                {
                    string msg = Describe(e);
                    Post(() => SetError("Could not save this turn: " + msg));
                }
            }

            if (_stopRequested)
            {
                Post(() => _r.Apply(AgentEvent.Error("Stopped.")));
                return;
            }

            try
            {
                await _transport.SendAsync(id, text, history, ct).ConfigureAwait(_caller);
            }
            catch (Exception e)
            {
                string msg = Describe(e);
                Post(() => _r.Apply(AgentEvent.Error(msg)));
            }
        }

        private void OnRunFinished(TurnSummary s)
        {
            if (_store != null && _turnChatId != null && !s.IsEmpty)
            {
                string content = s.Content;
                bool isError = false;
                if (string.IsNullOrEmpty(content) && s.ErrorText != null)
                {
                    content = s.ErrorText;
                    isError = true;
                }
                StoredMessage reply = new StoredMessage("assistant", content, s.Thinking, isError, s.Model, s.Temperature, s.ContextLength);
                string id = _turnChatId;
                Task ignore = Chain(async () =>
                {
                    try
                    {
                        await _store.SaveMessagesAsync(id, new[] { reply }, _cts.Token).ConfigureAwait(_caller);
                    }
                    catch (ChatNotFoundException)
                    {
                        Post(DropSelection);
                    }
                    catch (Exception e)
                    {
                        string msg = Describe(e);
                        Post(() => SetError("Could not save the reply: " + msg));
                    }
                    Post(() => Refresh());
                });
            }

            // Both a clean end and an error land here, so the queue cannot hang behind a failed send.
            if (_queue.TryTake(out string next)) StartTurn(next);
            Changed?.Invoke();
        }

        private void TrySendStop()
        {
            string job = _r.State.JobId;
            if (_stopSent || string.IsNullOrEmpty(job) || _transport == null) return;
            _stopSent = true;
            _transport.StopAsync(job).ContinueWith(
                t => Post(() => _r.Apply(AgentEvent.Error(t.Exception != null ? t.Exception.GetBaseException().Message : "Stop failed."))),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, _scheduler);
        }

        // ------------------------------------------------------------------ chat list

        /// <summary>Fetch the list. The newest call wins. With <paramref name="restoreId"/>, that chat is opened once the list arrives.</summary>
        public void Refresh(string restoreId = null)
        {
            if (_store == null) return;
            if (restoreId != null) _restoreId = restoreId;
            int gen = ++_listGen;
            ListPhase = ChatListPhase.Loading;
            Changed?.Invoke();
            Task<IReadOnlyList<ChatSummary>> task;
            try
            {
                task = _store.ListAsync(_cts.Token);
            }
            catch (Exception e)
            {
                task = Task.FromException<IReadOnlyList<ChatSummary>>(e);
            }
            task.ContinueWith(
                t => Post(() => OnListed(gen, t)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, _scheduler);
        }

        private void OnListed(int gen, Task<IReadOnlyList<ChatSummary>> t)
        {
            if (gen != _listGen) return;
            if (t.IsFaulted || t.IsCanceled || t.Result == null)
            {
                ListPhase = ChatListPhase.Failed;
                Error = t.Exception != null ? Describe(t.Exception) : "Could not read the chat list.";
                _errorFromList = true;
                _restoreId = null;
                Changed?.Invoke();
                return;
            }
            _chats.Clear();
            _chats.AddRange(t.Result);
            ListPhase = ChatListPhase.Idle;
            // A good list clears a list failure, but not an unrelated message (a refused switch, a failed save).
            if (_errorFromList)
            {
                Error = null;
                _errorFromList = false;
            }
            string restore = _restoreId;
            _restoreId = null;
            Changed?.Invoke();
            if (restore != null && SelectedId == null && !_r.State.RunInFlight && Contains(restore)) Select(restore);
        }

        public void Select(string id)
        {
            if (_store == null || string.IsNullOrEmpty(id)) return;
            if (_r.State.RunInFlight)
            {
                // Switching mid-run would send the reply into the wrong chat: the single-writer violation the web
                // client was bitten by. Stop first, or wait.
                SetError("Wait for the run to finish, or press Stop, before switching chats.");
                return;
            }
            int gen = ++_loadGen;
            Task<ChatDetail> task;
            try
            {
                task = _store.LoadAsync(id, _cts.Token);
            }
            catch (Exception e)
            {
                task = Task.FromException<ChatDetail>(e);
            }
            task.ContinueWith(
                t => Post(() => OnLoaded(gen, id, t)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, _scheduler);
        }

        private void OnLoaded(int gen, string id, Task<ChatDetail> t)
        {
            if (gen != _loadGen || _r.State.RunInFlight) return;
            if (t.IsFaulted || t.IsCanceled || t.Result == null)
            {
                Exception e = t.Exception != null ? t.Exception.GetBaseException() : null;
                if (e is ChatNotFoundException)
                {
                    RemoveFromList(id);
                    if (SelectedId == id) DropSelection();
                    SetError("That chat no longer exists.");
                }
                else
                {
                    SetError(t.Exception != null ? Describe(t.Exception) : "Could not load the chat.");
                }
                return;
            }
            _r.LoadTranscript(t.Result.Messages);
            SelectedId = id;
            Error = null;
            SelectionChanged?.Invoke(id);
            Changed?.Invoke();
        }

        /// <summary>A fresh, unsaved chat. The server creates the row on the first send, so there is no id yet.</summary>
        public void NewChat()
        {
            if (_r.State.RunInFlight)
            {
                SetError("Wait for the run to finish, or press Stop, before starting a new chat.");
                return;
            }
            _loadGen++;
            SelectedId = null;
            Error = null;
            _r.ClearConversation();
            SelectionChanged?.Invoke(null);
            Changed?.Invoke();
        }

        public void Delete(string id)
        {
            if (_store == null || string.IsNullOrEmpty(id)) return;
            if (_r.State.RunInFlight && id == SelectedId)
            {
                SetError("Stop the run before deleting the chat it is writing to.");
                return;
            }
            _store.DeleteAsync(id, _cts.Token).ContinueWith(
                t => Post(() => OnDeleted(id, t)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, _scheduler);
        }

        private void OnDeleted(string id, Task t)
        {
            // A 404 means it is already gone, which is what was asked for.
            bool gone = !t.IsFaulted || (t.Exception != null && t.Exception.GetBaseException() is ChatNotFoundException);
            if (!gone)
            {
                SetError(Describe(t.Exception));
                return;
            }
            RemoveFromList(id);
            if (SelectedId == id && !_r.State.RunInFlight) NewChat();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>Apply finished requests. Call from Update() on the main thread.</summary>
        public bool Pump()
        {
            bool any = false;
            while (_done.TryDequeue(out Action apply))
            {
                apply();
                any = true;
            }
            if (_stopRequested && !_stopSent) TrySendStop(); // Stop was pressed before the job id was known
            return any;
        }

        public void Dispose()
        {
            _r.RunFinished -= OnRunFinished;
            _cts.Cancel();
            _cts.Dispose();
        }

        private void Post(Action a) => _done.Enqueue(a);

        /// <summary>
        /// Saves go through one chain, in order. Without it the reply of turn N and the user message of turn N+1
        /// race to the server and can land in the wrong order, which the transcript then shows.
        /// </summary>
        private Task Chain(Func<Task> op)
        {
            lock (_chainGate)
            {
                // Inline: ExecuteSynchronously runs op() right here when the previous link is done, instead of
                // queueing it to a thread pool that does not exist on WebGL.
                _chain = _chain.ContinueWith(
                    _ => op(), CancellationToken.None,
                    _inline || _caller ? TaskContinuationOptions.ExecuteSynchronously : TaskContinuationOptions.None,
                    _scheduler).Unwrap();
                return _chain;
            }
        }

        private void Adopt(string id)
        {
            _turnChatId = id;
            SelectedId = id;
            SelectionChanged?.Invoke(id);
            Refresh();
        }

        private void DropSelection()
        {
            if (SelectedId == null) return;
            SelectedId = null;
            SelectionChanged?.Invoke(null);
            Changed?.Invoke();
        }

        private void SetError(string message)
        {
            Error = message;
            _errorFromList = false;
            Changed?.Invoke();
        }

        private void RemoveFromList(string id)
        {
            for (int i = _chats.Count - 1; i >= 0; i--)
            {
                if (_chats[i].Id == id) _chats.RemoveAt(i);
            }
        }

        private bool Contains(string id)
        {
            for (int i = 0; i < _chats.Count; i++)
            {
                if (_chats[i].Id == id) return true;
            }
            return false;
        }

        private static string TitleOf(string text)
        {
            string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return one.Length > 60 ? one.Substring(0, 60) : one;
        }

        private static string Describe(Exception e)
        {
            Exception b = e is AggregateException a ? a.GetBaseException() : e;
            return string.IsNullOrWhiteSpace(b.Message) ? "Request failed." : b.Message;
        }
    }
}
