using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace AgentConsole.Domain
{
    /// <summary>
    /// The one reducer over the five wire types. The transcript, the tool rail and the graph are
    /// three readers of the state it owns; none of them parses the wire.
    ///
    /// Main-thread only. A transport hands events across threads through its queue; whoever drains
    /// that queue (the view's Update) calls <see cref="Apply"/>.
    /// </summary>
    public sealed class ConsoleReducer
    {
        private const string UnknownTool = "unknown_tool";

        private readonly Func<long> _clock;
        private readonly Dictionary<string, ToolCall> _byRun = new Dictionary<string, ToolCall>();
        private int _autoRunId;

        public ConsoleState State { get; }
        public GraphModel Graph => State.Graph;

        /// <summary>A tool started. Argument is the tool node id; the server is <c>Graph</c> link-reachable from it.</summary>
        public event Action<string> NodeFired;

        /// <summary>A tool ended or errored. Argument is the tool node id.</summary>
        public event Action<string> NodeResolved;

        public event Action<Phase> PhaseChanged;
        public event Action TranscriptChanged;
        public event Action ToolsChanged;
        public event Action MetaChanged;

        /// <summary>
        /// The run this client started is over: raised once, on RunEnded or on Error, whichever comes first. Both are
        /// needed. A send failure or a "Stopped." error does not change the phase (it may already be Idle), so nothing
        /// phase-based can tell the send queue to move on.
        /// </summary>
        public event Action<TurnSummary> RunFinished;

        private int _turnStart;

        /// <param name="graph">The graph to read and graft onto.</param>
        /// <param name="clockMs">Millisecond clock for tool durations. Injectable so tests need no sleeping.</param>
        public ConsoleReducer(GraphModel graph, Func<long> clockMs = null)
        {
            State = new ConsoleState(graph ?? new GraphModel());
            _clock = clockMs ?? DefaultClock;
        }

        private static long DefaultClock() => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

        public void Apply(in AgentEvent ev)
        {
            switch (ev.Kind)
            {
                case AgentEventKind.Meta:
                    if (ev.Model != null) State.Model = ev.Model;
                    if (ev.Temperature.HasValue) State.Temperature = ev.Temperature;
                    if (ev.ContextLength.HasValue) State.ContextLength = ev.ContextLength;
                    if (!string.IsNullOrEmpty(ev.JobId)) State.JobId = ev.JobId;
                    MetaChanged?.Invoke();
                    break;

                case AgentEventKind.Thought:
                    if (string.IsNullOrEmpty(ev.Text)) return;
                    SetPhase(Phase.Thinking);
                    AppendDelta(MessageRole.Reasoning, ev.Text);
                    break;

                case AgentEventKind.Content:
                    if (string.IsNullOrEmpty(ev.Text)) return;
                    SetPhase(Phase.Talking);
                    AppendDelta(MessageRole.Assistant, ev.Text);
                    break;

                case AgentEventKind.Error:
                    CloseOpenMessage();
                    ChatMessage err = new ChatMessage(MessageRole.Assistant, false) { IsError = true };
                    err.Append(ev.ErrorText ?? string.Empty);
                    State.MessageList.Add(err);
                    SetPhase(Phase.Idle);
                    TranscriptChanged?.Invoke();
                    FinishRun();
                    break;

                case AgentEventKind.RunEnded:
                    // The one clean end-of-run signal. llm_end fires after every model turn and must not do this.
                    bool endedOpen = CloseOpenMessage();
                    SetPhase(Phase.Idle);
                    if (endedOpen) TranscriptChanged?.Invoke();
                    FinishRun();
                    break;

                case AgentEventKind.ToolCallback:
                    ApplyCallback(ev);
                    break;
            }
        }

        /// <summary>Add the user's own message. Not a wire event: the client sends it, the server never echoes it.</summary>
        public void AddUserMessage(string text)
        {
            CloseOpenMessage();
            ChatMessage m = new ChatMessage(MessageRole.User, false);
            m.Append(text ?? string.Empty);
            State.MessageList.Add(m);
            TranscriptChanged?.Invoke();
        }

        /// <summary>
        /// Start a turn in the current conversation: the history stays, the tool rail and per-run meta clear.
        /// This replaces Reset() on the send path, which wiped the transcript and made a real chat impossible.
        /// </summary>
        public void BeginTurn(string text)
        {
            ClearRunState();
            State.RunInFlight = true;
            AddUserMessage(text);
            _turnStart = State.MessageList.Count;
            MetaChanged?.Invoke();
            ToolsChanged?.Invoke();
        }

        /// <summary>The completed user and assistant turns, in the shape EVA's history field takes. Errors and open messages are left out.</summary>
        public List<HistoryItem> BuildHistory()
        {
            List<HistoryItem> h = new List<HistoryItem>();
            List<ChatMessage> list = State.MessageList;
            for (int i = 0; i < list.Count; i++)
            {
                ChatMessage m = list[i];
                if (m.IsOpen || m.IsError || m.Length == 0) continue;
                if (m.Role == MessageRole.User) h.Add(new HistoryItem("user", m.Text));
                else if (m.Role == MessageRole.Assistant) h.Add(new HistoryItem("assistant", m.Text));
            }
            return h;
        }

        /// <summary>Replace the conversation with a stored chat. Stored messages are complete, so none is left open.</summary>
        public void LoadTranscript(IReadOnlyList<StoredMessage> stored)
        {
            ClearConversationCore();
            for (int i = 0; i < stored.Count; i++)
            {
                StoredMessage s = stored[i];
                if (s.Role == "user")
                {
                    ChatMessage u = new ChatMessage(MessageRole.User, false);
                    u.Append(s.Content);
                    State.MessageList.Add(u);
                    continue;
                }
                if (!string.IsNullOrEmpty(s.Thinking))
                {
                    ChatMessage t = new ChatMessage(MessageRole.Reasoning, false);
                    t.Append(s.Thinking);
                    State.MessageList.Add(t);
                }
                ChatMessage a = new ChatMessage(MessageRole.Assistant, false) { IsError = s.IsError };
                a.Append(s.Content);
                State.MessageList.Add(a);
            }
            TranscriptChanged?.Invoke();
        }

        /// <summary>An empty conversation (New chat). The caller guards against doing this mid-run.</summary>
        public void ClearConversation()
        {
            ClearConversationCore();
            TranscriptChanged?.Invoke();
        }

        private void ClearConversationCore()
        {
            State.MessageList.Clear();
            // Opening another chat is not a new run: the model, temperature and context describe the server, not the
            // chat, so they stay on screen instead of blanking until the next meta event.
            ClearRunState(keepMeta: true);
            State.RunInFlight = false;
            ToolsChanged?.Invoke();
        }

        private void ClearRunState(bool keepMeta = false)
        {
            State.ToolList.Clear();
            _byRun.Clear();
            State.RunningToolCount = 0;
            if (!keepMeta)
            {
                State.Model = null;
                State.Temperature = null;
                State.ContextLength = null;
            }
            State.JobId = null;
            SetPhase(Phase.Idle);
        }

        private void FinishRun()
        {
            if (!State.RunInFlight) return;
            State.RunInFlight = false;
            System.Text.StringBuilder content = new System.Text.StringBuilder();
            System.Text.StringBuilder thinking = new System.Text.StringBuilder();
            string error = null;
            List<ChatMessage> list = State.MessageList;
            for (int i = Math.Min(_turnStart, list.Count); i < list.Count; i++)
            {
                ChatMessage m = list[i];
                if (m.IsError) error = m.Text;
                else if (m.Role == MessageRole.Assistant) content.Append(m.Text);
                else if (m.Role == MessageRole.Reasoning) thinking.Append(m.Text);
            }
            RunFinished?.Invoke(new TurnSummary(
                content.ToString(), thinking.ToString(), error, State.Model, State.Temperature, State.ContextLength));
        }

        /// <summary>Clear the transcript, tool cards and meta for a new run. The graph is kept, grafted nodes included.</summary>
        public void Reset()
        {
            State.MessageList.Clear();
            State.ToolList.Clear();
            _byRun.Clear();
            State.RunningToolCount = 0;
            State.Model = null;
            State.Temperature = null;
            State.ContextLength = null;
            State.JobId = null;
            SetPhase(Phase.Idle);
            MetaChanged?.Invoke();
            TranscriptChanged?.Invoke();
            ToolsChanged?.Invoke();
        }

        private void ApplyCallback(in AgentEvent ev)
        {
            switch (ev.Callback)
            {
                case CallbackKind.ChatModelStart:
                    SetPhase(Phase.Thinking);
                    break;

                case CallbackKind.ToolStart:
                    OnToolStart(ev);
                    break;

                case CallbackKind.ToolEnd:
                case CallbackKind.ToolError:
                    OnToolResolved(ev);
                    break;

                case CallbackKind.LlmEnd:
                    // One model turn ended, not the run: more turns may follow. Settle to Thinking, or keep
                    // the phase of any call still in flight. Only RunEnded (or Error) goes Idle.
                    bool closed = CloseOpenMessage();
                    SetPhase(PhaseForRunningTools());
                    if (closed) TranscriptChanged?.Invoke();
                    break;
            }
        }

        private void OnToolStart(in AgentEvent ev)
        {
            string raw = string.IsNullOrEmpty(ev.Tool) ? UnknownTool : ev.Tool;
            string runId = ev.RunId;
            if (string.IsNullOrEmpty(runId))
            {
                runId = NextLocalRunId();
            }
            else if (_byRun.ContainsKey(runId))
            {
                // A replayed or duplicated start. The card already exists; do not add a second.
                return;
            }

            GraphNode node = State.Graph.EnsureTool(raw, out ToolRef tref, out _);
            ToolCall call = new ToolCall(
                runId, tref.Server, tref.Tool, raw, ev.Input, node.IsMemory,
                node.Id, GraphModel.ServerId(tref.Server), _clock());
            State.ToolList.Add(call);
            _byRun[runId] = call;
            State.RunningToolCount++;

            SetPhase(node.IsMemory ? Phase.Memory : Phase.ToolCall);
            NodeFired?.Invoke(node.Id);
            ToolsChanged?.Invoke();
        }

        private void OnToolResolved(in AgentEvent ev)
        {
            bool isError = ev.Callback == CallbackKind.ToolError;
            long now = _clock();
            ToolCall call = FindCall(ev);

            if (call == null)
            {
                // An end whose start we never saw (joined mid-run, or the start was lost). Keep the
                // result rather than drop it, but only if the event says which tool it was.
                if (string.IsNullOrEmpty(ev.Tool)) return;
                GraphNode node = State.Graph.EnsureTool(ev.Tool, out ToolRef tref, out _);
                string runId = string.IsNullOrEmpty(ev.RunId) ? NextLocalRunId() : ev.RunId;
                call = new ToolCall(
                    runId, tref.Server, tref.Tool, ev.Tool, null, node.IsMemory,
                    node.Id, GraphModel.ServerId(tref.Server), now);
                State.ToolList.Add(call);
                _byRun[runId] = call;
            }
            else if (call.Status != ToolStatus.Running)
            {
                // A second end for a card already resolved.
                return;
            }
            else
            {
                State.RunningToolCount--;
            }

            call.Status = isError ? ToolStatus.Error : ToolStatus.Done;
            call.Output = isError ? (ev.ErrorText ?? ev.Output) : ev.Output;
            call.DurationMs = Math.Max(0L, now - call.StartedMs);

            SetPhase(PhaseForRunningTools());
            NodeResolved?.Invoke(call.NodeId);
            ToolsChanged?.Invoke();
        }

        /// <summary>
        /// RunId is the key. Only an end with no RunId at all falls back to the oldest Running call
        /// of the same raw tool name.
        /// </summary>
        private ToolCall FindCall(in AgentEvent ev)
        {
            if (!string.IsNullOrEmpty(ev.RunId))
            {
                _byRun.TryGetValue(ev.RunId, out ToolCall byId);
                return byId;
            }
            if (string.IsNullOrEmpty(ev.Tool)) return null;
            List<ToolCall> calls = State.ToolList;
            for (int i = 0; i < calls.Count; i++)
            {
                if (calls[i].Status == ToolStatus.Running && calls[i].RawTool == ev.Tool) return calls[i];
            }
            return null;
        }

        /// <summary>
        /// Back to Thinking when the last tool resolves. If another call is still in flight, stay in
        /// that call's phase instead of claiming the agent is thinking.
        /// </summary>
        private Phase PhaseForRunningTools()
        {
            List<ToolCall> calls = State.ToolList;
            for (int i = calls.Count - 1; i >= 0; i--)
            {
                if (calls[i].Status == ToolStatus.Running)
                {
                    return calls[i].IsMemory ? Phase.Memory : Phase.ToolCall;
                }
            }
            return Phase.Thinking;
        }

        private string NextLocalRunId() => "~local-" + (++_autoRunId).ToString(CultureInfo.InvariantCulture);

        private void AppendDelta(MessageRole role, string text)
        {
            List<ChatMessage> list = State.MessageList;
            ChatMessage last = list.Count > 0 ? list[list.Count - 1] : null;
            if (last != null && last.IsOpen && last.Role == role)
            {
                last.Append(text);
            }
            else
            {
                CloseOpenMessage();
                ChatMessage m = new ChatMessage(role, true);
                m.Append(text);
                list.Add(m);
            }
            TranscriptChanged?.Invoke();
        }

        private bool CloseOpenMessage()
        {
            List<ChatMessage> list = State.MessageList;
            if (list.Count == 0) return false;
            ChatMessage last = list[list.Count - 1];
            if (!last.IsOpen) return false;
            last.IsOpen = false;
            return true;
        }

        private void SetPhase(Phase next)
        {
            if (State.Phase == next) return;
            State.Phase = next;
            PhaseChanged?.Invoke(next);
        }
    }
}
