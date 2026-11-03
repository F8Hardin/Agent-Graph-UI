using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentConsole.Domain;

namespace AgentConsole.Transport
{
    /// <summary>The queue and the OnEvent plumbing shared by every transport.</summary>
    public abstract class AgentTransportBase : IAgentTransport
    {
        private readonly ConcurrentQueue<AgentEvent> _queue = new ConcurrentQueue<AgentEvent>();

        public event Action<AgentEvent> OnEvent;

        public abstract IReadOnlyCollection<string> Capabilities { get; }
        public abstract Task<string> SendAsync(string chatId, string prompt, CancellationToken ct);
        public abstract Task StopAsync(string jobId);

        /// <summary>Transports that can carry history override this; the rest ignore it.</summary>
        public virtual Task<string> SendAsync(string chatId, string prompt, IReadOnlyList<HistoryItem> history, CancellationToken ct)
            => SendAsync(chatId, prompt, ct);

        /// <summary>The last exception thrown by an OnEvent subscriber, if any. A bad subscriber must not kill the stream.</summary>
        public Exception LastSubscriberError { get; private set; }

        public bool TryDequeue(out AgentEvent ev) => _queue.TryDequeue(out ev);

        /// <summary>Queue the event for the main thread, then notify OnEvent subscribers. Callable from any thread.</summary>
        protected void Emit(AgentEvent ev)
        {
            _queue.Enqueue(ev);
            Action<AgentEvent> handler = OnEvent;
            if (handler == null) return;
            try
            {
                handler(ev);
            }
            catch (Exception e)
            {
                LastSubscriberError = e;
            }
        }
    }
}
