using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.WebTransport
{
    /// <summary>
    /// <see cref="EvaInventory"/> on UnityWebRequest, for the browser build: GET /tools reads the MCP servers, POST /tools
    /// pauses or resumes them. Same deadlines, body builder, parser and error text as the HttpClient class.
    /// </summary>
    public sealed class UwrInventory : IInventorySource
    {
        private readonly string _base;
        private readonly int _timeoutMs;
        private readonly int _pauseTimeoutMs;

        public UwrInventory(
            string baseUrl = EvaSseTransport.DefaultBaseUrl,
            int timeoutMs = EvaInventory.DefaultTimeoutMs, int pauseTimeoutMs = EvaInventory.PauseTimeoutMs)
        {
            _base = (string.IsNullOrWhiteSpace(baseUrl) ? EvaSseTransport.DefaultBaseUrl : baseUrl).TrimEnd('/');
            _timeoutMs = timeoutMs;
            _pauseTimeoutMs = pauseTimeoutMs;
        }

        public Task<InventorySnapshot> FetchAsync(CancellationToken ct) =>
            SendAsync("GET", null, _timeoutMs, "the tool list", ct);

        public Task<InventorySnapshot> SetPausedAsync(
            IReadOnlyDictionary<string, bool> servers, IReadOnlyDictionary<string, bool> tools, CancellationToken ct) =>
            SendAsync("POST", ToolsWire.BuildPauseBody(servers, tools), _pauseTimeoutMs, "the pause change", ct);

        private async Task<InventorySnapshot> SendAsync(string method, string body, int timeoutMs, string what, CancellationToken ct)
        {
            UwrReply r = await UwrHttp.BufferedAsync(method, _base + "/tools", body, timeoutMs, ct,
                "EVA did not answer " + what + " within " + (timeoutMs / 1000) + " s.",
                "Could not reach EVA for " + what + ": ");
            if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
            if (!ToolsWire.TryParse(r.Body, out InventorySnapshot snapshot))
            {
                throw new InvalidOperationException("EVA's answer to " + what + " was not in the expected form.");
            }
            return snapshot;
        }
    }
}
