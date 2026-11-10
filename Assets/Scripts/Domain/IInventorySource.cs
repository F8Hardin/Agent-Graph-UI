using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    /// <summary>
    /// Where the graph's servers and tools come from when the agent is live. Lives in Domain for the same reason
    /// <see cref="IModelSelector"/> does: the controller and the tests need the shape of it without the assembly that
    /// does the HTTP. Rejected: reading the inventory off the SSE stream. The stream only ever names a tool after it
    /// was called, which is exactly the "graft on first use" behaviour that left the graph wrong.
    /// </summary>
    public interface IInventorySource
    {
        /// <summary>Ask the agent what it can reach right now. Faults with a readable message when it cannot be read.</summary>
        Task<InventorySnapshot> FetchAsync(CancellationToken ct);
    }
}
