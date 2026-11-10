using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentConsole.Domain;

namespace AgentConsole.Transport
{
    /// <summary>
    /// EVA's GET /tools behind <see cref="IInventorySource"/>: the same list of MCP servers, with live status and tool
    /// names, that the web app's settings page reads. One small buffered request with one deadline, like
    /// <see cref="EvaChatStore"/>. Nothing here touches a Unity object; the controller applies the result on the main thread.
    /// </summary>
    public sealed class EvaInventory : IInventorySource, IDisposable
    {
        /// <summary>/tools asks every MCP server for its tool list, so it can be slower than /chats. Generous, but never forever.</summary>
        public const int DefaultTimeoutMs = 20000;

        private readonly string _base;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly int _timeoutMs;

        public EvaInventory(string baseUrl = EvaSseTransport.DefaultBaseUrl, HttpClient httpClient = null, int timeoutMs = DefaultTimeoutMs)
        {
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

        public async Task<InventorySnapshot> FetchAsync(CancellationToken ct)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, _base + "/tools"))
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(_timeoutMs);
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
                    throw new TimeoutException("EVA did not answer the tool list within " + (_timeoutMs / 1000) + " s.");
                }
                catch (HttpRequestException e)
                {
                    throw new InvalidOperationException("Could not reach EVA for the tool list: " + e.Message, e);
                }

                if (!ToolsWire.TryParse(body, out InventorySnapshot snapshot))
                {
                    throw new InvalidOperationException("EVA's tool list was not in the expected form.");
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
