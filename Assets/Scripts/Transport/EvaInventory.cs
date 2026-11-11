using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentConsole.Domain;

namespace AgentConsole.Transport
{
    /// <summary>
    /// EVA's /tools behind <see cref="IInventorySource"/>: GET reads the list of MCP servers, with live status and tool
    /// names, that the web app's settings page reads, and POST pauses or resumes servers and tools (the web app's
    /// toggles). One buffered request with one deadline, like <see cref="EvaChatStore"/>. Nothing here touches a Unity
    /// object; the controller applies the result on the main thread.
    /// </summary>
    public sealed class EvaInventory : IInventorySource, IDisposable
    {
        /// <summary>/tools asks every MCP server for its tool list, so it can be slower than /chats. Generous, but never forever.</summary>
        public const int DefaultTimeoutMs = 20000;

        /// <summary>
        /// A POST can wait for running streams to finish before EVA applies it (it holds a gate for up to about 90 s), so
        /// it gets far longer than a read. Rejected: the read's 20 s, which would report a toggle as failed while EVA was
        /// still about to apply it, and the user would see it flip back and then flip again.
        /// </summary>
        public const int PauseTimeoutMs = 120000;

        private readonly string _base;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly int _timeoutMs;
        private readonly int _pauseTimeoutMs;

        public EvaInventory(
            string baseUrl = EvaSseTransport.DefaultBaseUrl, HttpClient httpClient = null,
            int timeoutMs = DefaultTimeoutMs, int pauseTimeoutMs = PauseTimeoutMs)
        {
            _pauseTimeoutMs = pauseTimeoutMs;
            _base = (string.IsNullOrWhiteSpace(baseUrl) ? EvaSseTransport.DefaultBaseUrl : baseUrl).TrimEnd('/');
            _timeoutMs = timeoutMs;
            if (httpClient == null)
            {
                _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // the deadline below is the only one
                _ownsHttp = true;
            }
            else
            {
                _http = httpClient;
            }
        }

        public Task<InventorySnapshot> FetchAsync(CancellationToken ct) =>
            SendAsync(HttpMethod.Get, null, _timeoutMs, "the tool list", ct);

        public Task<InventorySnapshot> SetPausedAsync(
            IReadOnlyDictionary<string, bool> servers, IReadOnlyDictionary<string, bool> tools, CancellationToken ct) =>
            SendAsync(HttpMethod.Post, ToolsWire.BuildPauseBody(servers, tools), _pauseTimeoutMs, "the pause change", ct);

        private async Task<InventorySnapshot> SendAsync(HttpMethod method, string jsonBody, int timeoutMs, string what, CancellationToken ct)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(method, _base + "/tools"))
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                cts.CancelAfter(timeoutMs);
                string body;
                try
                {
                    using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            throw new InvalidOperationException(ModelsWire.ErrorText((int)resp.StatusCode, resp.ReasonPhrase, body));
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("EVA did not answer " + what + " within " + (timeoutMs / 1000) + " s.");
                }
                catch (HttpRequestException e)
                {
                    throw new InvalidOperationException("Could not reach EVA for " + what + ": " + e.Message, e);
                }

                if (!ToolsWire.TryParse(body, out InventorySnapshot snapshot))
                {
                    throw new InvalidOperationException("EVA's answer to " + what + " was not in the expected form.");
                }
                return snapshot;
            }
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }
    }
}
