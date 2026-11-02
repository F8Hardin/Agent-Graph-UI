using System;

namespace AgentConsole.Domain
{
    /// <summary>The five wire types of INTERFACE.md section 4.</summary>
    public enum AgentEventKind
    {
        Meta,
        Thought,
        Content,
        ToolCallback,
        Error,

        /// <summary>
        /// Not a wire type. The transport raises it when the stream ends cleanly: the run is over.
        /// llm_end cannot mark that, because EVA emits it after every model turn.
        /// </summary>
        RunEnded
    }

    /// <summary>The sub-event of an <c>ai_callback</c>. <c>None</c> for every other kind.</summary>
    public enum CallbackKind
    {
        None,
        ChatModelStart,
        ToolStart,
        ToolEnd,
        ToolError,
        LlmEnd
    }

    /// <summary>
    /// One normalised event. Transport produces these, the reducer consumes them; neither knows
    /// the other. Immutable, so it can cross the thread boundary through a ConcurrentQueue untouched.
    /// </summary>
    public readonly struct AgentEvent
    {
        public AgentEventKind Kind { get; }
        public CallbackKind Callback { get; }

        /// <summary>Delta text for Thought and Content.</summary>
        public string Text { get; }

        /// <summary>Raw tool name as sent, usually namespaced <c>server__tool</c>.</summary>
        public string Tool { get; }
        public string Input { get; }
        public string Output { get; }

        /// <summary>Error text for Error and ToolError.</summary>
        public string ErrorText { get; }

        /// <summary>Pairs a ToolEnd/ToolError with its ToolStart. EVA run_id, Claude tool_use.id.</summary>
        public string RunId { get; }

        public string JobId { get; }
        public string Model { get; }
        public float? Temperature { get; }

        /// <summary>Context window the server loaded the model at, from the meta event. Null when not reported.</summary>
        public int? ContextLength { get; }

        private AgentEvent(
            AgentEventKind kind, CallbackKind callback, string text, string tool, string input,
            string output, string errorText, string runId, string jobId, string model, float? temperature,
            int? contextLength)
        {
            Kind = kind;
            Callback = callback;
            Text = text;
            Tool = tool;
            Input = input;
            Output = output;
            ErrorText = errorText;
            RunId = runId;
            JobId = jobId;
            Model = model;
            Temperature = temperature;
            ContextLength = contextLength;
        }

        public static AgentEvent Meta(string model, float? temperature, string jobId, int? contextLength = null) =>
            new AgentEvent(AgentEventKind.Meta, CallbackKind.None, null, null, null, null, null, null, jobId, model, temperature, contextLength);

        public static AgentEvent Thought(string text) =>
            new AgentEvent(AgentEventKind.Thought, CallbackKind.None, text, null, null, null, null, null, null, null, null, null);

        public static AgentEvent Content(string text) =>
            new AgentEvent(AgentEventKind.Content, CallbackKind.None, text, null, null, null, null, null, null, null, null, null);

        public static AgentEvent Error(string message) =>
            new AgentEvent(AgentEventKind.Error, CallbackKind.None, null, null, null, null, message, null, null, null, null, null);

        /// <summary>The run is over (clean end of stream). The only event that settles the phase to Idle, besides Error.</summary>
        public static AgentEvent RunEnded() =>
            new AgentEvent(AgentEventKind.RunEnded, CallbackKind.None, null, null, null, null, null, null, null, null, null, null);

        public static AgentEvent ChatModelStart() =>
            new AgentEvent(AgentEventKind.ToolCallback, CallbackKind.ChatModelStart, null, null, null, null, null, null, null, null, null, null);

        public static AgentEvent ToolStart(string tool, string input, string runId) =>
            new AgentEvent(AgentEventKind.ToolCallback, CallbackKind.ToolStart, null, tool, input, null, null, runId, null, null, null, null);

        public static AgentEvent ToolEnd(string tool, string output, string runId) =>
            new AgentEvent(AgentEventKind.ToolCallback, CallbackKind.ToolEnd, null, tool, null, output, null, runId, null, null, null, null);

        public static AgentEvent ToolError(string tool, string error, string runId) =>
            new AgentEvent(AgentEventKind.ToolCallback, CallbackKind.ToolError, null, tool, null, null, error, runId, null, null, null, null);

        public static AgentEvent LlmEnd() =>
            new AgentEvent(AgentEventKind.ToolCallback, CallbackKind.LlmEnd, null, null, null, null, null, null, null, null, null, null);

        public override string ToString()
        {
            if (Kind == AgentEventKind.ToolCallback)
            {
                return "ToolCallback/" + Callback + (Tool != null ? " " + Tool : "") + (RunId != null ? " #" + RunId : "");
            }
            return Kind.ToString();
        }
    }
}
