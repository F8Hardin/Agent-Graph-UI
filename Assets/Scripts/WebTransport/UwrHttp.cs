using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace AgentConsole.WebTransport
{
    internal struct UwrReply
    {
        public bool Ok;
        public int Status;
        public string Reason;
        public string Body;
    }

    /// <summary>
    /// The one place that turns a UnityWebRequest into a Task. UnityWebRequest is what the browser build can use: Unity maps
    /// it onto the browser's own networking, while HttpClient has no sockets there. A request completes from its
    /// AsyncOperation callback, which Unity raises on the main thread, so there is no thread, no Task.Run and no timer
    /// anywhere in this assembly. Rejected: wrapping each call in a coroutine, which would need a MonoBehaviour to host it.
    /// </summary>
    internal static class UwrHttp
    {
        /// <summary>Start the request and complete when it has finished (any outcome). Cancelling the token aborts it.</summary>
        public static Task<UnityWebRequest> Send(UnityWebRequest req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TaskCompletionSource<UnityWebRequest> tcs = new TaskCompletionSource<UnityWebRequest>();
            CancellationTokenRegistration reg = default(CancellationTokenRegistration);
            if (ct.CanBeCanceled) reg = ct.Register(() => req.Abort());
            UnityWebRequestAsyncOperation op = req.SendWebRequest();
            op.completed += _ =>
            {
                reg.Dispose();
                tcs.TrySetResult(req);
            };
            return tcs.Task;
        }

        /// <summary>A request that buffers its answer. A null body sends none; an empty one sends an empty POST body.</summary>
        public static UnityWebRequest Build(string method, string url, string body)
        {
            UnityWebRequest req = new UnityWebRequest(url, method) { downloadHandler = new DownloadHandlerBuffer() };
            if (body != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body))
                {
                    contentType = body.Length == 0 ? "text/plain; charset=utf-8" : "application/json; charset=utf-8"
                };
            }
            return req;
        }

        /// <summary>
        /// A whole, small request/response with one deadline, mirroring the HttpClient classes' SendBufferedAsync: the same
        /// timeout and unreachable wording, so the UI shows the same text on either stack. A refusal (4xx/5xx) is returned,
        /// not thrown, so the caller can map 404 to ChatNotFoundException as before.
        /// </summary>
        public static async Task<UwrReply> BufferedAsync(
            string method, string url, string body, int timeoutMs, CancellationToken ct, string timeoutText, string unreachablePrefix)
        {
            using (UnityWebRequest req = Build(method, url, body))
            {
                req.timeout = Math.Max(1, (timeoutMs + 999) / 1000);
                await Send(req, ct);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                switch (req.result)
                {
                    case UnityWebRequest.Result.Success:
                        return new UwrReply { Ok = true, Status = (int)req.responseCode, Reason = "OK", Body = req.downloadHandler.text };
                    case UnityWebRequest.Result.ProtocolError:
                        return new UwrReply
                        {
                            Ok = false, Status = (int)req.responseCode,
                            Reason = ReasonOf(req.error, (int)req.responseCode), Body = req.downloadHandler.text
                        };
                    default:
                        if (IsTimeout(req.error)) throw new TimeoutException(timeoutText);
                        throw new InvalidOperationException(unreachablePrefix + req.error);
                }
            }
        }

        /// <summary>UnityWebRequest.error reads like "HTTP/1.1 404 Not Found"; the reason is what follows the code. Empty when absent.</summary>
        public static string ReasonOf(string error, int code)
        {
            if (string.IsNullOrEmpty(error)) return string.Empty;
            int i = error.IndexOf(code.ToString(), StringComparison.Ordinal);
            return i < 0 ? string.Empty : error.Substring(i + code.ToString().Length).Trim();
        }

        private static bool IsTimeout(string error) =>
            error != null && error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
