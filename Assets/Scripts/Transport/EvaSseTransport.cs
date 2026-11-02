using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentConsole.Domain;

namespace AgentConsole.Transport
{
    /// <summary>Paths of EVA's FastAPI surface. Overridable so the same transport can point at the bridge's /v1 later.</summary>
    public sealed class EvaEndpoints
    {
        public string SubmitPrompt = "/submit_prompt";

        /// <summary>{0} = job id, {1} = number of data chunks already received. EVA replays from that cursor.</summary>
        public string StreamFormat = "/jobs/{0}/stream?start={1}";

        public string StopFormat = "/jobs/{0}/stop";
        public string ActiveJob = "/jobs/active";

        public string Models = "/models";
        public string SetModel = "/set-model";

        /// <summary>Response header on /submit_prompt carrying the job id.</summary>
        public string JobIdHeader = "X-Job-Id";
    }

    /// <summary>
    /// POSTs to EVA's /submit_prompt and reads the SSE stream on a worker thread. Parsed events go
    /// onto a ConcurrentQueue; the main thread drains it with TryDequeue. Nothing in here touches a
    /// Unity object, and this assembly cannot even reference one.
    ///
    /// EVA detaches a run from its HTTP response, so a dropped connection does not stop the run.
    /// This transport therefore reconnects with backoff to GET /jobs/{id}/stream?start=N, where N is
    /// the number of data chunks already consumed, so a resume neither misses nor repeats a chunk.
    /// Stopping is explicit: <see cref="StopAsync"/>.
    /// </summary>
    public sealed class EvaSseTransport : AgentTransportBase, IModelSelector, IDisposable
    {
        public const string DefaultBaseUrl = "http://10.0.0.219:8000";

        // Only what this class implements. EVA itself also has monitor and schedule; those are REST
        // resources this client does not use yet. Models is here because this class implements IModelSelector.
        private static readonly string[] CapabilityList =
        {
            AgentCapability.Chat, AgentCapability.Stream, AgentCapability.Tools, AgentCapability.Cancel,
            AgentCapability.Models
        };

        private sealed class RunState
        {
            public volatile string JobId;
            public int Cursor;
            public volatile bool Terminal;
            public volatile bool Finished;
            public CancellationTokenSource Cts;

            public readonly TaskCompletionSource<string> JobIdReady =
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly string _base;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly EvaEndpoints _endpoints;
        private readonly ReconnectPolicy _policy;
        private readonly Random _rng = new Random();
        private readonly object _gate = new object();
        private RunState _run;
        private int _malformed;
        private int _reconnects;

        public override IReadOnlyCollection<string> Capabilities => CapabilityList;
        public string BaseUrl => _base;

        /// <summary>True while a run's stream is being read, including time spent reconnecting.</summary>
        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _run != null && !_run.Finished;
                }
            }
        }

        /// <summary>Payloads that were not valid JSON or not a known event. They are counted and skipped.</summary>
        public int MalformedCount => Volatile.Read(ref _malformed);

        /// <summary>How many times the stream was re-opened after a drop.</summary>
        public int ReconnectCount => Volatile.Read(ref _reconnects);

        /// <param name="httpClient">
        /// Optional. If you pass one, give it an infinite Timeout: the default 100 seconds would cut
        /// a long run. The transport applies its own connect and idle timeouts.
        /// </param>
        public EvaSseTransport(
            string baseUrl = DefaultBaseUrl, HttpClient httpClient = null,
            EvaEndpoints endpoints = null, ReconnectPolicy policy = null)
        {
            _base = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
            _endpoints = endpoints ?? new EvaEndpoints();
            _policy = policy ?? new ReconnectPolicy();
            if (httpClient == null)
            {
                _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                _ownsHttp = true;
            }
            else
            {
                _http = httpClient;
            }
        }

        /// <summary>
        /// POST the prompt and return the job id. The POST is not retried: it is not idempotent, and
        /// a lost response may still have started a run (see <see cref="GetActiveJobIdAsync"/>).
        /// The token governs the whole run, not just this call.
        /// </summary>
        public override async Task<string> SendAsync(string chatId, string prompt, CancellationToken ct)
        {
            if (prompt == null) throw new ArgumentNullException(nameof(prompt));

            RunState run = BeginRun(null, 0, ct);
            HttpResponseMessage resp = null;
            bool pumpStarted = false;
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, Url(_endpoints.SubmitPrompt))
                {
                    Content = new StringContent(new SubmitPromptRequest(prompt, chatId).ToJson(), Encoding.UTF8, "application/json")
                };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                resp = await SendWithTimeoutAsync(req, run.Cts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        "EVA rejected the prompt: " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
                }

                if (resp.Headers.TryGetValues(_endpoints.JobIdHeader, out IEnumerable<string> values))
                {
                    foreach (string v in values)
                    {
                        if (!string.IsNullOrEmpty(v))
                        {
                            run.JobId = v;
                            run.JobIdReady.TrySetResult(v);
                            break;
                        }
                    }
                }

                HttpResponseMessage owned = resp;
                pumpStarted = true;
                _ = Task.Run(() => PumpAsync(run, owned));

                return string.IsNullOrEmpty(run.JobId) ? await WaitForJobIdAsync(run).ConfigureAwait(false) : run.JobId;
            }
            catch
            {
                if (!pumpStarted)
                {
                    resp?.Dispose();
                    run.Finished = true;
                    run.Cts.Dispose();
                }
                else
                {
                    EndRun(run);
                }
                throw;
            }
        }

        /// <summary>
        /// Ask EVA to stop a run. EVA then ends the stream with an error event reading "Stopped.",
        /// which flows through the normal path. A run that is already gone counts as stopped.
        /// </summary>
        public override async Task StopAsync(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) throw new ArgumentException("A job id is required.", nameof(jobId));
            string path = string.Format(CultureInfo.InvariantCulture, _endpoints.StopFormat, Uri.EscapeDataString(jobId));
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = new StringContent(string.Empty) })
            using (HttpResponseMessage resp = await SendWithTimeoutAsync(req, CancellationToken.None).ConfigureAwait(false))
            {
                if (resp.StatusCode == HttpStatusCode.NotFound) return;
                resp.EnsureSuccessStatusCode();
            }
        }

        /// <summary>
        /// Attach to a run already in progress, e.g. after the app restarted or a headset woke up.
        /// <paramref name="startChunk"/> 0 replays the whole buffered run, so reset the reducer first.
        /// </summary>
        public Task RejoinAsync(string jobId, int startChunk = 0, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(jobId)) throw new ArgumentException("A job id is required.", nameof(jobId));
            RunState run = BeginRun(jobId, Math.Max(0, startChunk), ct);
            _ = Task.Run(() => PumpAsync(run, null));
            return Task.CompletedTask;
        }

        /// <summary>The job id EVA reports as running now, or null. For rejoining after a restart.</summary>
        public async Task<string> GetActiveJobIdAsync(CancellationToken ct = default)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, Url(_endpoints.ActiveJob)))
            using (HttpResponseMessage resp = await SendWithTimeoutAsync(req, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!MiniJson.TryParse(body, out object root) || !(root is Dictionary<string, object> obj)) return null;
                obj.TryGetValue("active", out object active);
                if (active is Dictionary<string, object> job && job.TryGetValue("job_id", out object id)) return id as string;
                return null;
            }
        }

        /// <summary>GET /models. Slow when the server has to wake the inference engine; never call it from the main thread.</summary>
        public async Task<ModelCatalog> GetModelsAsync(CancellationToken ct)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, Url(_endpoints.Models)))
            {
                BufferedResponse r = await SendBufferedAsync(req, ct, _policy.ModelListTimeoutMs, "model list").ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
                if (!ModelsWire.TryParseCatalog(r.Body, out ModelCatalog catalog))
                {
                    throw new InvalidOperationException("EVA's model list was not in the expected form.");
                }
                return catalog;
            }
        }

        /// <summary>
        /// POST /set-model. EVA answers only once the model is loaded (or the load has failed), so this can take
        /// minutes. A failure carries EVA's own explanation, which says whether the previous model is still resident.
        /// </summary>
        public async Task SetModelAsync(string modelId, int? contextLength, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(modelId)) throw new ArgumentException("A model id is required.", nameof(modelId));
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, Url(_endpoints.SetModel))
            {
                Content = new StringContent(ModelsWire.SetModelJson(modelId, contextLength), Encoding.UTF8, "application/json")
            })
            {
                BufferedResponse r = await SendBufferedAsync(req, ct, _policy.ModelSwitchTimeoutMs, "model switch").ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
            }
        }

        public void Dispose()
        {
            RunState run;
            lock (_gate)
            {
                run = _run;
            }
            if (run != null) EndRun(run);
            if (_ownsHttp) _http.Dispose();
        }

        // ------------------------------------------------------------------ run lifecycle

        private RunState BeginRun(string jobId, int cursor, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_run != null && !_run.Finished)
                {
                    throw new InvalidOperationException("A run is already active on this transport.");
                }
                RunState run = new RunState { Cursor = cursor, Cts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
                if (!string.IsNullOrEmpty(jobId))
                {
                    run.JobId = jobId;
                    run.JobIdReady.TrySetResult(jobId);
                }
                _run = run;
                return run;
            }
        }

        private static void EndRun(RunState run)
        {
            try
            {
                run.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

private static async Task<string> WaitForJobIdAsync(RunState run)
        {
            // Not run.Cts: a fast run can finish, and dispose it, before this line executes.
            Task<string> ready = run.JobIdReady.Task;
            if (!ready.IsCompleted)
            {
                using (CancellationTokenSource timerCts = new CancellationTokenSource())
                {
                    Task finished = await Task.WhenAny((Task)ready, Task.Delay(15000, timerCts.Token)).ConfigureAwait(false);
                    timerCts.Cancel();
                    if (finished != ready)
                    {
                        EndRun(run);
                        throw new TimeoutException("EVA did not report a job id for this run.");
                    }
                }
            }
            string id = await ready.ConfigureAwait(false);
            if (string.IsNullOrEmpty(id))
            {
                EndRun(run);
                throw new InvalidOperationException("The stream ended before EVA reported a job id.");
            }
            return id;
        }

        // ------------------------------------------------------------------ the pump

        private async Task PumpAsync(RunState run, HttpResponseMessage first)
        {
            CancellationToken ct = run.Cts.Token;
            HttpResponseMessage resp = first;
            int attempt = 0;
            bool confirming = false; // a reconnect made only to check that a clean EOF was the real end
            bool afterDrop = false;  // the last connection died rather than ending
            bool cleanEnd = false;   // the stream ended normally: raise RunEnded, the only thing that sets Idle
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int before = run.Cursor;
                    try
                    {
                        if (resp == null)
                        {
                            resp = await OpenStreamAsync(run, ct).ConfigureAwait(false);
                            if (resp == null)
                            {
                                // 404: the run is not buffered. Fine if we only came to confirm the end;
                                // not fine if we lost a live stream and cannot get it back.
                                if (afterDrop)
                                {
                                    Emit(AgentEvent.Error("Lost the stream for job " + run.JobId +
                                        ": EVA no longer has that run buffered."));
                                }
                                else
                                {
                                    cleanEnd = true;
                                }
                                break;
                            }
                        }

                        await ReadStreamAsync(run, resp, ct).ConfigureAwait(false);
                        resp.Dispose();
                        resp = null;
                        afterDrop = false;

                        // EVA closes the stream only once the run is done, but llm_end fires per model
                        // turn and cannot mark the end. A clean EOF is therefore confirmed by one more
                        // connection from the cursor: if that yields nothing new, the run is over.
                        if (run.Terminal) break;
                        if (confirming && run.Cursor == before)
                        {
                            cleanEnd = true;
                            break;
                        }
                        confirming = true;
                        attempt = 0;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (IsTransient(ex))
                    {
                        resp?.Dispose();
                        resp = null;
                        afterDrop = true;
                        confirming = false;
                        if (run.Cursor > before) attempt = 0;
                        attempt++;
                        if (attempt > _policy.MaxAttempts)
                        {
                            Emit(AgentEvent.Error("Connection to EVA lost (" + ex.GetType().Name + ": " + ex.Message +
                                "). Gave up after " + _policy.MaxAttempts + " attempts. Job " + run.JobId +
                                " may still be running; rejoin it from chunk " + run.Cursor + "."));
                            break;
                        }
                        Interlocked.Increment(ref _reconnects);
                        await Task.Delay(_policy.DelayFor(attempt, _rng), ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Dispose or the caller's token. Not an error.
            }
            catch (Exception ex)
            {
                Emit(AgentEvent.Error("EVA stream failed: " + ex.Message));
            }
            finally
            {
                resp?.Dispose();
                if (cleanEnd) Emit(AgentEvent.RunEnded());
                run.Finished = true;
                run.JobIdReady.TrySetResult(null);
                run.Cts.Dispose();
            }
        }

        private async Task<HttpResponseMessage> OpenStreamAsync(RunState run, CancellationToken ct)
        {
            string path = string.Format(
                CultureInfo.InvariantCulture, _endpoints.StreamFormat, Uri.EscapeDataString(run.JobId), run.Cursor);
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, Url(path));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            HttpResponseMessage resp = await SendWithTimeoutAsync(req, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                string reason = resp.ReasonPhrase;
                resp.Dispose();
                if (code >= 500 || code == 408 || code == 429)
                {
                    throw new HttpRequestException("EVA stream returned " + code + " " + reason);
                }
                throw new InvalidOperationException("EVA refused the stream: " + code + " " + reason);
            }
            return resp;
        }

        /// <summary>Read one connection to its end. Returns on a clean EOF; throws on a drop or an idle timeout.</summary>
        private async Task ReadStreamAsync(RunState run, HttpResponseMessage resp, CancellationToken ct)
        {
            using (Stream stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096))
            using (CancellationTokenSource idle = new CancellationTokenSource())
            using (ct.Register(() => resp.Dispose()))
            using (idle.Token.Register(() => resp.Dispose()))
            {
                SseParser sse = new SseParser();
                idle.CancelAfter(_policy.IdleTimeoutMs);
                while (true)
                {
                    string line;
                    try
                    {
                        // StreamReader has no token overload; disposing the response is what unblocks it.
                        line = await reader.ReadLineAsync().ConfigureAwait(false);
                    }
                    catch (Exception) when (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }
                    catch (Exception) when (idle.IsCancellationRequested)
                    {
                        throw new TimeoutException("No data or keepalive from EVA for " + _policy.IdleTimeoutMs + " ms.");
                    }

                    if (line == null) return; // clean EOF; a half-read event is dropped and re-fetched on resume

                    idle.CancelAfter(_policy.IdleTimeoutMs);
                    if (sse.Feed(line, out string payload))
                    {
                        // The cursor counts chunks consumed, parsed or not, because it is the server's index.
                        run.Cursor++;
                        Handle(run, payload);
                    }
                }
            }
        }

        private void Handle(RunState run, string payload)
        {
            if (!WireParser.TryParse(payload, out AgentEvent ev))
            {
                Interlocked.Increment(ref _malformed);
                return;
            }
            if (ev.Kind == AgentEventKind.Error)
            {
                run.Terminal = true;
            }
            else if (ev.Kind == AgentEventKind.Meta && !string.IsNullOrEmpty(ev.JobId))
            {
                if (string.IsNullOrEmpty(run.JobId)) run.JobId = ev.JobId;
                run.JobIdReady.TrySetResult(ev.JobId);
            }
            Emit(ev);
        }

        // ------------------------------------------------------------------ helpers

        private string Url(string path) => _base + path;

        private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage req, CancellationToken ct)
        {
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(_policy.ConnectTimeoutMs);
                try
                {
                    return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out waiting for EVA's response headers.");
                }
            }
        }

        private struct BufferedResponse
        {
            public bool Ok;
            public int Status;
            public string Reason;
            public string Body;
        }

        /// <summary>
        /// A whole, small request/response with one deadline over headers AND body. The stream helper above only
        /// bounds the headers, which is right for an SSE stream and wrong here: /set-model's headers arrive after
        /// the load, and a model list is read in full. Connection failures are reworded so the UI can show them as is.
        /// </summary>
        private async Task<BufferedResponse> SendBufferedAsync(HttpRequestMessage req, CancellationToken ct, int timeoutMs, string what)
        {
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeoutMs);
                try
                {
                    using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return new BufferedResponse
                        {
                            Ok = resp.IsSuccessStatusCode,
                            Status = (int)resp.StatusCode,
                            Reason = resp.ReasonPhrase,
                            Body = body
                        };
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("EVA did not finish the " + what + " within " + (timeoutMs / 1000) + " s.");
                }
                catch (HttpRequestException e)
                {
                    throw new InvalidOperationException("Could not reach EVA for the " + what + ": " + e.Message, e);
                }
            }
        }

        private static bool IsTransient(Exception ex)
        {
            return ex is HttpRequestException || ex is IOException || ex is TimeoutException
                || ex is ObjectDisposedException || ex is SocketException || ex is WebException
                || ex is OperationCanceledException;
        }
    }
}
