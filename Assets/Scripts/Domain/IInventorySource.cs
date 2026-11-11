using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    /// <summary>
    /// Where the graph's servers and tools come from when the agent is live, and where a pause toggle is sent. Lives in
    /// Domain for the same reason <see cref="IModelSelector"/> does: the controller and the tests need the shape of it
    /// without the assembly that does the HTTP. Rejected: reading the inventory off the SSE stream. The stream only ever
    /// names a tool after it was called, which is exactly the "graft on first use" behaviour that left the graph wrong.
    /// </summary>
    public interface IInventorySource
    {
        /// <summary>Ask the agent what it can reach right now. Faults with a readable message when it cannot be read.</summary>
        Task<InventorySnapshot> FetchAsync(CancellationToken ct);

        /// <summary>
        /// Pause or resume servers and tools (true = paused), keyed by server name and by the tool's wire name
        /// (<see cref="ToolInfo.WireName"/>). Answers with the agent's reading AFTER the change, so the caller never has to
        /// guess what stuck. Either map may be null or empty. Slow by design: the agent waits for running streams to
        /// finish before it applies a change, which can take about a minute and a half. Faults with a readable message.
        /// </summary>
        Task<InventorySnapshot> SetPausedAsync(
            IReadOnlyDictionary<string, bool> servers, IReadOnlyDictionary<string, bool> tools, CancellationToken ct);
    }
}
