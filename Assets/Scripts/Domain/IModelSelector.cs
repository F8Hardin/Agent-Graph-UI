using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    /// <summary>
    /// An agent whose model can be listed and switched. Advertised by <see cref="AgentCapability.Models"/>;
    /// a transport that lacks the capability simply does not implement it, and the UI hides the control.
    /// Lives in Domain for the same reason <see cref="IAgentTransport"/> does: the View needs the shape of this
    /// without ever referencing the assembly that does the HTTP.
    /// </summary>
    public interface IModelSelector
    {
        /// <summary>
        /// List the models and the current one. May take many seconds (the inference server can load on demand),
        /// so callers must never wait for it on the main thread.
        /// </summary>
        Task<ModelCatalog> GetModelsAsync(CancellationToken ct);

        /// <summary>
        /// Make <paramref name="modelId"/> the active model. Completes when the server has finished loading it,
        /// which for a large model is tens of seconds. Faults with the server's own explanation on refusal.
        /// </summary>
        /// <param name="contextLength">Null lets the server choose its default.</param>
        Task SetModelAsync(string modelId, int? contextLength, CancellationToken ct);
    }
}
