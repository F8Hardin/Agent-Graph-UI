using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    /// <summary>Capability names from INTERFACE.md section 2. The UI enables a panel when its capability is present.</summary>
    public static class AgentCapability
    {
        public const string Chat = "chat";
        public const string Stream = "stream";
        public const string Tools = "tools";
        public const string Monitor = "monitor";
        public const string Schedule = "schedule";
        public const string Models = "models";
        public const string Cancel = "cancel";
        public const string Sessions = "sessions";
    }

    /// <summary>
    /// A source of <see cref="AgentEvent"/>. Events reach the main thread through
    /// <see cref="TryDequeue"/>: nothing here may touch a Unity object off-thread.
    /// </summary>
    public interface IAgentTransport
    {
        /// <summary>What this agent supports. Never branch on which agent it is; check this.</summary>
        IReadOnlyCollection<string> Capabilities { get; }

        /// <summary>
        /// Raised on the transport's worker thread, right after the event is queued. Safe for logging
        /// and tests. It is NOT safe for a subscriber to touch a Unity object or the reducer: drain
        /// <see cref="TryDequeue"/> from Update() for that.
        /// </summary>
        event Action<AgentEvent> OnEvent;

        /// <summary>
        /// Start a run and return its server-owned job id. The token governs the whole run, not just
        /// this call: cancelling it abandons the stream.
        /// </summary>
        Task<string> SendAsync(string chatId, string prompt, CancellationToken ct);

        /// <summary>Ask the server to stop a run. Closing the stream alone does not stop it.</summary>
        Task StopAsync(string jobId);

        /// <summary>Non-blocking. The one hand-off from worker threads to the main thread.</summary>
        bool TryDequeue(out AgentEvent ev);
    }

    public static class AgentTransportExtensions
    {
        public static bool Supports(this IAgentTransport transport, string capability)
        {
            if (transport == null || capability == null) return false;
            foreach (string c in transport.Capabilities)
            {
                if (c == capability) return true;
            }
            return false;
        }
    }
}
