using System.Collections.Generic;
using System.Text;

namespace AgentConsole.Domain
{
    public enum Phase
    {
        Idle,
        Thinking,
        ToolCall,
        Memory,
        Talking
    }

    public enum MessageRole
    {
        User,
        Reasoning,
        Assistant
    }

    public enum ToolStatus
    {
        Running,
        Done,
        Error
    }

    /// <summary>
    /// One transcript entry. Deltas append into a StringBuilder, so a token stream costs no
    /// per-delta string concatenation; <see cref="Text"/> is materialised lazily and cached until
    /// the next append. Only the reducer mutates it.
    /// </summary>
    public sealed class ChatMessage
    {
        private readonly StringBuilder _text = new StringBuilder();
        private string _cached;

        public MessageRole Role { get; }

        /// <summary>True while deltas may still append. Only the last message in the transcript can be open.</summary>
        public bool IsOpen { get; internal set; }

        public bool IsError { get; internal set; }

        /// <summary>Bumped on every append, so a view can tell cheaply that its copy is stale.</summary>
        public int Version { get; private set; }

        public int Length => _text.Length;

        /// <summary>Read-only by convention: hand it to a text component that accepts a StringBuilder.</summary>
        public StringBuilder Builder => _text;

        public string Text => _cached ?? (_cached = _text.ToString());

        internal ChatMessage(MessageRole role, bool isOpen)
        {
            Role = role;
            IsOpen = isOpen;
        }

        internal void Append(string s)
        {
            _text.Append(s);
            _cached = null;
            Version++;
        }
    }

    /// <summary>One tool call card, keyed by <see cref="RunId"/>.</summary>
    public sealed class ToolCall
    {
        public string RunId { get; }

        /// <summary>Server recovered from the event itself (or the inventory, or "local").</summary>
        public string Server { get; }

        /// <summary>Bare tool name with the server prefix removed.</summary>
        public string Tool { get; }

        /// <summary>The name exactly as the wire sent it.</summary>
        public string RawTool { get; }

        public string Input { get; }
        public bool IsMemory { get; }
        public string NodeId { get; }
        public string ServerNodeId { get; }

        public ToolStatus Status { get; internal set; }

        /// <summary>Tool output, or the error text when Status is Error.</summary>
        public string Output { get; internal set; }

        public long DurationMs { get; internal set; }

        internal long StartedMs { get; }

        internal ToolCall(
            string runId, string server, string tool, string rawTool, string input,
            bool isMemory, string nodeId, string serverNodeId, long startedMs)
        {
            RunId = runId;
            Server = server;
            Tool = tool;
            RawTool = rawTool;
            Input = input;
            IsMemory = isMemory;
            NodeId = nodeId;
            ServerNodeId = serverNodeId;
            StartedMs = startedMs;
            Status = ToolStatus.Running;
        }
    }

    /// <summary>
    /// Everything the transcript, tool rail and graph read. Written only by <see cref="ConsoleReducer"/>.
    /// Main-thread only.
    /// </summary>
    public sealed class ConsoleState
    {
        internal readonly List<ChatMessage> MessageList = new List<ChatMessage>();
        internal readonly List<ToolCall> ToolList = new List<ToolCall>();

        public IReadOnlyList<ChatMessage> Transcript => MessageList;
        public IReadOnlyList<ToolCall> ToolCalls => ToolList;
        public GraphModel Graph { get; }

        public Phase Phase { get; internal set; }
        public string Model { get; internal set; }
        public float? Temperature { get; internal set; }
        public int? ContextLength { get; internal set; }
        public string JobId { get; internal set; }

        /// <summary>Tool calls currently Running.</summary>
        public int RunningToolCount { get; internal set; }

        public ConsoleState(GraphModel graph)
        {
            Graph = graph;
        }
    }
}
