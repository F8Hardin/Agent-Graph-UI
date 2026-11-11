using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    /// <summary>
    /// An <see cref="IInventorySource"/> with no server behind it, for the offline replay and the browser build: it holds a
    /// list of servers and applies pause requests to it, so the Settings toggles and the graph behave exactly as they do
    /// against EVA. Every task it returns is already completed. That matters in the WebGL player, which has no thread
    /// pool: nothing here waits, schedules or blocks, and the controller applies the result in its next Pump.
    /// Rejected: Task.Run or Task.Delay to imitate latency, which would never complete in the browser.
    /// </summary>
    public sealed class InMemoryInventory : IInventorySource
    {
        private readonly List<ServerInfo> _servers;

        public InMemoryInventory(IEnumerable<ServerInfo> servers)
        {
            _servers = new List<ServerInfo>(servers ?? new ServerInfo[0]);
        }

        public Task<InventorySnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(InventorySnapshot.From(_servers));

        public Task<InventorySnapshot> SetPausedAsync(
            IReadOnlyDictionary<string, bool> servers, IReadOnlyDictionary<string, bool> tools, CancellationToken ct)
        {
            List<string> unknown = new List<string>();
            if (servers != null)
            {
                foreach (KeyValuePair<string, bool> kv in servers)
                {
                    int i = IndexOf(kv.Key);
                    if (i < 0) unknown.Add(kv.Key);
                    else _servers[i] = _servers[i].WithPaused(kv.Value);
                }
            }
            if (tools != null)
            {
                foreach (KeyValuePair<string, bool> kv in tools)
                {
                    bool found = false;
                    for (int i = 0; i < _servers.Count; i++)
                    {
                        if (!_servers[i].HasTool(kv.Key)) continue;
                        _servers[i] = _servers[i].WithToolPaused(kv.Key, kv.Value);
                        found = true;
                    }
                    if (!found) unknown.Add(kv.Key);
                }
            }
            return Task.FromResult(InventorySnapshot.From(_servers, unknown));
        }

        private int IndexOf(string name)
        {
            for (int i = 0; i < _servers.Count; i++)
            {
                if (_servers[i].Name == name) return i;
            }
            return -1;
        }
    }
}
