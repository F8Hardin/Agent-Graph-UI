using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.WebTransport
{
    /// <summary>
    /// <see cref="EvaSseTransport"/> on UnityWebRequest, for the browser build, where HttpClient has no sockets and the thread
    /// pool does not exist. Same wire contract: POST /submit_prompt with the SSE stream in its response, the job id from
    /// X-Job-Id (or the stream's own meta event), resume at GET /jobs/{id}/stream?start=N with the same cursor rule, an
    /// explicit stop, and the same reconnect policy. What differs is only the plumbing: everything runs on the main thread,
    /// a request completes from its AsyncOperation callback, bytes reach <see cref="SseParser"/> from a
    /// DownloadHandlerScript as they arrive, and the reconnect back-off waits on <see cref="Awaitable"/> (Unity's frame
    /// timer) instead of Task.Delay, which would never fire on WebGL.
    ///
    /// Not carried over, on purpose: the 60 s idle watchdog and the 15 s wait for a job id. Neither can be enforced without
    /// a timer, and a reply is long. A cancelled token or Dispose aborts the request.
    /// </summary>
    public sealed class UwrEvaTransport : AgentTransportBase, IModelSelector, IDisposable
    {
        private static readonly string[] CapabilityList =
        {
            AgentCapability.Chat, AgentCapability.Stream, AgentCapability.Tools, AgentCapability.Cancel,
            AgentCapability.Models
        };

        /// <summary>A failure worth reconnecting after (a drop, a 5xx), as opposed to a refusal.</summary>
        private sealed class TransientException : Exception
        {
            public TransientException(string message) : base(message)
            {
            }
        }

        private sealed class RunState
        {
            public string JobId;
            public int Cursor;
            public bool Terminal;
            public bool Finished;
            public CancellationTokenSource Cts;
            public readonly TaskCompletionSource<string> JobIdReady = new TaskCompletionSource<string>();
        }

        private readonly string _base;
        private readonly EvaEndpoints _endpoints;
        private readonly ReconnectPolicy _policy;
        private readonly System.Random _rng = new System.Random();
        private RunState _run;
        private int _malformed;
        private int _reconnects;

        public override IReadOnlyCollection<string> Capabilities => CapabilityList;
        public string BaseUrl => _base;
        public bool IsRunning => _run != null && !_run.Finished;
        public int MalformedCount => _malformed;
        public int ReconnectCount => _reconnects;

        public UwrEvaTransport(string baseUrl = EvaSseTransport.DefaultBaseUrl, EvaEndpoints endpoints = null, ReconnectPolicy policy = null)
        {
            _base = (string.IsNullOrWhiteSpace(baseUrl) ? EvaSseTransport.DefaultBaseUrl : baseUrl).TrimEnd('/');
            _endpoints = endpoints ?? new EvaEndpoints();
            _policy = policy ?? new ReconnectPolicy();
        }

        public override Task<string> SendAsync(string chatId, string prompt, CancellationToken ct)
            => SendAsync(chatId, prompt, null, ct);

        /// <summary>
        /// POST the prompt and return the job id. Not retried, like the HttpClient transport: a lost response may still have
        /// started a run. A refusal or an unreachable server faults this task with the same text as before; once the job id is
        /// known, a later drop is handled by reconnecting.
        /// </summary>
        public override async Task<string> SendAsync(string chatId, string prompt, IReadOnlyList<HistoryItem> history, CancellationToken ct)
        {
            if (prompt == null) throw new ArgumentNullException(nameof(prompt));
            RunState run = BeginRun(null, 0, ct);
            string body = new SubmitPromptRequest(prompt, chatId, history).ToJson();
            _ = PumpAsync(run, body); // runs on to its first await (the request is sent) before this returns
            try
            {
                string id = await run.JobIdReady.Task;
                if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("The stream ended before EVA reported a job id.");
                return id;
            }
            catch
            {
                EndRun(run);
                throw;
            }
        }

        public override async Task StopAsync(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) throw new ArgumentException("A job id is required.", nameof(jobId));
            string path = string.Format(CultureInfo.InvariantCulture, _endpoints.StopFormat, Uri.EscapeDataString(jobId));
            UwrReply r = await UwrHttp.BufferedAsync("POST", _base + path, string.Empty, _policy.ConnectTimeoutMs, CancellationToken.None,
                "Timed out waiting for EVA's response headers.", "Could not reach EVA to stop the run: ");
            if (r.Status == 404) return; // already gone counts as stopped
            if (!r.Ok) throw new InvalidOperationException("EVA refused the stop: " + r.Status + " " + r.Reason);
        }

        /// <summary>Attach to a run already in progress. Startchunk 0 replays the whole buffered run, so reset the reducer first.</summary>
        public Task RejoinAsync(string jobId, int startChunk = 0, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(jobId)) throw new ArgumentException("A job id is required.", nameof(jobId));
            RunState run = BeginRun(jobId, Math.Max(0, startChunk), ct);
            _ = PumpAsync(run, null);
            return Task.CompletedTask;
        }

        public async Task<string> GetActiveJobIdAsync(CancellationToken ct = default)
        {
            UwrReply r = await UwrHttp.BufferedAsync("GET", _base + _endpoints.ActiveJob, null, _policy.ConnectTimeoutMs, ct,
                "Timed out waiting for EVA's response headers.", "Could not reach EVA: ");
            if (!r.Ok) throw new InvalidOperationException("EVA answered " + r.Status + " " + r.Reason);
            if (!MiniJson.TryParse(r.Body, out object root) || !(root is Dictionary<string, object> obj)) return null;
            obj.TryGetValue("active", out object active);
            if (active is Dictionary<string, object> job && job.TryGetValue("job_id", out object id)) return id as string;
            return null;
        }

        public async Task<ModelCatalog> GetModelsAsync(CancellationToken ct)
        {
            UwrReply r = await UwrHttp.BufferedAsync("GET", _base + _endpoints.Models, null, _policy.ModelListTimeoutMs, ct,
                "EVA did not finish the model list within " + (_policy.ModelListTimeoutMs / 1000) + " s.",
                "Could not reach EVA for the model list: ");
            if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
            if (!ModelsWire.TryParseCatalog(r.Body, out ModelCatalog catalog))
            {
                throw new InvalidOperationException("EVA's model list was not in the expected form.");
            }
            return catalog;
        }

        public async Task SetModelAsync(string modelId, int? contextLength, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(modelId)) throw new ArgumentException("A model id is required.", nameof(modelId));
            UwrReply r = await UwrHttp.BufferedAsync("POST", _base + _endpoints.SetModel, ModelsWire.SetModelJson(modelId, contextLength),
                _policy.ModelSwitchTimeoutMs, ct,
                "EVA did not finish the model switch within " + (_policy.ModelSwitchTimeoutMs / 1000) + " s.",
                "Could not reach EVA for the model switch: ");
            if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
        }

        public void Dispose()
        {
            if (_run != null) EndRun(_run);
        }

        // ------------------------------------------------------------------ run lifecycle

        private RunState BeginRun(string jobId, int cursor, CancellationToken ct)
        {
            if (_run != null && !_run.Finished) throw new InvalidOperationException("A run is already active on this transport.");
            RunState run = new RunState { Cursor = cursor, Cts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
            if (!string.IsNullOrEmpty(jobId))
            {
                run.JobId = jobId;
                run.JobIdReady.TrySetResult(jobId);
            }
            _run = run;
            return run;
        }

        /// <summary>Cancelling the run's token aborts the request in flight (see UwrHttp.Send).</summary>
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

        // ------------------------------------------------------------------ the pump

        /// <summary>The reconnect loop of EvaSseTransport.PumpAsync, with the first connection being the POST when submitBody is set.</summary>
        private async Task PumpAsync(RunState run, string submitBody)
        {
            CancellationToken ct = run.Cts.Token;
            bool first = submitBody != null;
            int attempt = 0;
            bool confirming = false; // a reconnect made only to check that a clean EOF was the real end
            bool afterDrop = false;  // the last connection died rather than ended
            bool cleanEnd = false;   // the stream ended normally: raise RunEnded, the only thing that sets Idle
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int before = run.Cursor;
                    bool submitting = first;
                    first = false;
                    try
                    {
                        UnityWebRequest req = submitting ? BuildSubmit(submitBody) : BuildResume(run);
                        bool found = await ReadConnectionAsync(run, req, submitting, ct);
                        if (!found)
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
                        afterDrop = false;
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
                    catch (Exception ex) when (submitting && string.IsNullOrEmpty(run.JobId) && run.Cursor == 0)
                    {
                        // The POST itself failed before anything came back: SendAsync reports it, nothing is retried.
                        run.JobIdReady.TrySetException(ex);
                        break;
                    }
                    catch (Exception ex) when (ex is TransientException || ex is TimeoutException)
                    {
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
                        _reconnects++;
                        await Awaitable.WaitForSecondsAsync((float)_policy.DelayFor(attempt, _rng).TotalSeconds, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Dispose or the caller's token. Not an error.
                run.JobIdReady.TrySetCanceled();
            }
            catch (Exception ex)
            {
                Emit(AgentEvent.Error("EVA stream failed: " + ex.Message));
            }
            finally
            {
                if (cleanEnd) Emit(AgentEvent.RunEnded());
                run.Finished = true;
                run.JobIdReady.TrySetResult(null);
                run.Cts.Dispose();
            }
        }

        private UnityWebRequest BuildSubmit(string body)
        {
            UnityWebRequest req = UwrHttp.Build("POST", _base + _endpoints.SubmitPrompt, body);
            req.SetRequestHeader("Accept", "text/event-stream");
            return req;
        }

        private UnityWebRequest BuildResume(RunState run)
        {
            string path = string.Format(
                CultureInfo.InvariantCulture, _endpoints.StreamFormat, Uri.EscapeDataString(run.JobId), run.Cursor);
            UnityWebRequest req = UwrHttp.Build("GET", _base + path, null);
            req.SetRequestHeader("Accept", "text/event-stream");
            return req;
        }

        /// <summary>
        /// Read one connection to its end. Returns true on a clean end, false on a 404 from a resume; throws on a drop or a
        /// refusal. The body of the request's own download handler is replaced by the streaming one, so nothing buffers.
        /// </summary>
        private async Task<bool> ReadConnectionAsync(RunState run, UnityWebRequest req, bool isSubmit, CancellationToken ct)
        {
            SseDownloadHandler handler = new SseDownloadHandler(
                r =>
                {
                    // Readable from the browser only if EVA exposes it (Access-Control-Expose-Headers); the stream's own
                    // meta event carries the id too, so a missing header costs nothing.
                    if (!isSubmit || !string.IsNullOrEmpty(run.JobId)) return;
                    string v = r.GetResponseHeader(_endpoints.JobIdHeader);
                    if (string.IsNullOrEmpty(v)) return;
                    run.JobId = v;
                    run.JobIdReady.TrySetResult(v);
                },
                payload =>
                {
                    // The cursor counts chunks consumed, parsed or not, because it is the server's index.
                    run.Cursor++;
                    Handle(run, payload);
                });
            handler.Request = req;
            req.downloadHandler = handler;
            try
            {
                await UwrHttp.Send(req, ct);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                switch (req.result)
                {
                    case UnityWebRequest.Result.Success:
                        return true;
                    case UnityWebRequest.Result.ProtocolError:
                    {
                        int code = (int)req.responseCode;
                        string reason = UwrHttp.ReasonOf(req.error, code);
                        if (isSubmit) throw new InvalidOperationException("EVA rejected the prompt: " + code + " " + reason);
                        if (code == 404) return false;
                        if (code >= 500 || code == 408 || code == 429) throw new TransientException("EVA stream returned " + code + " " + reason);
                        throw new InvalidOperationException("EVA refused the stream: " + code + " " + reason);
                    }
                    default:
                        if (isSubmit && string.IsNullOrEmpty(run.JobId) && run.Cursor == 0)
                        {
                            throw new InvalidOperationException("Could not reach EVA to send the prompt: " + req.error);
                        }
                        throw new TransientException(req.error);
                }
            }
            finally
            {
                req.Dispose();
            }
        }

        private void Handle(RunState run, string payload)
        {
            if (!WireParser.TryParse(payload, out AgentEvent ev))
            {
                _malformed++;
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
    }
}
