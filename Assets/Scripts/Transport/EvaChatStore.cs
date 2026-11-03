using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentConsole.Domain;

namespace AgentConsole.Transport
{
    /// <summary>
    /// EVA's chat endpoints (GET/POST /chats, GET/DELETE /chats/{id}, POST /chats/{id}/messages) behind
    /// <see cref="IChatStore"/>. Every call is a small buffered request with one deadline. Nothing here touches a
    /// Unity object; the session applies the results on the main thread.
    /// </summary>
    public sealed class EvaChatStore : IChatStore, IDisposable
    {
        private const int TimeoutMs = 20000;

        private readonly string _base;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        public EvaChatStore(string baseUrl = EvaSseTransport.DefaultBaseUrl, HttpClient httpClient = null)
        {
            _base = (string.IsNullOrWhiteSpace(baseUrl) ? EvaSseTransport.DefaultBaseUrl : baseUrl).TrimEnd('/');
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

        public async Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken ct)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, _base + "/chats"))
            {
                Reply r = await SendAsync(req, ct, "chat list").ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
                if (!TryParseList(r.Body, out List<ChatSummary> rows))
                {
                    throw new InvalidOperationException("EVA's chat list was not in the expected form.");
                }
                return rows;
            }
        }

        public async Task<string> CreateAsync(string title, CancellationToken ct)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _base + "/chats")
            {
                Content = new StringContent("{\"title\":" + MiniJson.Quote(title ?? string.Empty) + "}", Encoding.UTF8, "application/json")
            })
            {
                Reply r = await SendAsync(req, ct, "new chat").ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
                if (MiniJson.TryParse(r.Body, out object root) && root is Dictionary<string, object> obj
                    && obj.TryGetValue("id", out object id) && id is string s && s.Length > 0)
                {
                    return s;
                }
                throw new InvalidOperationException("EVA did not return an id for the new chat.");
            }
        }

        public async Task<ChatDetail> LoadAsync(string id, CancellationToken ct)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, _base + "/chats/" + Uri.EscapeDataString(id)))
            {
                Reply r = await SendAsync(req, ct, "chat load").ConfigureAwait(false);
                if (r.Status == (int)HttpStatusCode.NotFound) throw new ChatNotFoundException(id);
                if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
                if (!TryParseDetail(r.Body, out ChatDetail detail))
                {
                    throw new InvalidOperationException("EVA's chat was not in the expected form.");
                }
                return detail;
            }
        }

        public async Task DeleteAsync(string id, CancellationToken ct)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Delete, _base + "/chats/" + Uri.EscapeDataString(id)))
            {
                Reply r = await SendAsync(req, ct, "chat delete").ConfigureAwait(false);
                if (r.Status == (int)HttpStatusCode.NotFound) throw new ChatNotFoundException(id);
                if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
            }
        }

        public async Task SaveMessagesAsync(string id, IReadOnlyList<StoredMessage> messages, CancellationToken ct)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _base + "/chats/" + Uri.EscapeDataString(id) + "/messages")
            {
                Content = new StringContent(MessagesJson(messages), Encoding.UTF8, "application/json")
            })
            {
                Reply r = await SendAsync(req, ct, "message save").ConfigureAwait(false);
                if (r.Status == (int)HttpStatusCode.NotFound) throw new ChatNotFoundException(id);
                if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
            }
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }

        // ------------------------------------------------------------------ wire

        /// <summary>The body of POST /chats/{id}/messages. steps and timing are not tracked here, so they are sent empty.</summary>
        public static string MessagesJson(IReadOnlyList<StoredMessage> messages)
        {
            StringBuilder sb = new StringBuilder("{\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                StoredMessage m = messages[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"role\":").Append(MiniJson.Quote(m.Role));
                sb.Append(",\"content\":").Append(MiniJson.Quote(m.Content));
                sb.Append(",\"thinking\":").Append(MiniJson.Quote(m.Thinking));
                sb.Append(",\"steps\":[],\"timing\":{}");
                sb.Append(",\"is_error\":").Append(m.IsError ? "true" : "false");
                sb.Append(",\"model\":").Append(m.Model == null ? "null" : MiniJson.Quote(m.Model));
                sb.Append(",\"temperature\":").Append(m.Temperature.HasValue ? m.Temperature.Value.ToString("R", CultureInfo.InvariantCulture) : "null");
                sb.Append(",\"context_length\":").Append(m.ContextLength.HasValue ? m.ContextLength.Value.ToString(CultureInfo.InvariantCulture) : "null");
                sb.Append('}');
            }
            return sb.Append("]}").ToString();
        }

        public static bool TryParseList(string json, out List<ChatSummary> rows)
        {
            rows = null;
            if (!MiniJson.TryParse(json, out object root) || !(root is Dictionary<string, object> obj)) return false;
            if (!obj.TryGetValue("chats", out object list) || !(list is List<object> items)) return false;
            rows = new List<ChatSummary>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                if (!(items[i] is Dictionary<string, object> c)) continue;
                string id = Str(c, "id");
                if (string.IsNullOrEmpty(id)) continue;
                int count = c.TryGetValue("message_count", out object n) && n is double d ? (int)d : 0;
                rows.Add(new ChatSummary(id, Str(c, "title"), Str(c, "last_run_at") ?? Str(c, "updated_at"), count));
            }
            return true;
        }

        public static bool TryParseDetail(string json, out ChatDetail detail)
        {
            detail = null;
            if (!MiniJson.TryParse(json, out object root) || !(root is Dictionary<string, object> obj)) return false;
            string id = Str(obj, "id");
            if (string.IsNullOrEmpty(id)) return false;
            List<StoredMessage> msgs = new List<StoredMessage>();
            if (obj.TryGetValue("messages", out object list) && list is List<object> items)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (!(items[i] is Dictionary<string, object> m)) continue;
                    float? temp = m.TryGetValue("temperature", out object t) && t is double td ? (float?)td : null;
                    int? ctx = m.TryGetValue("context_length", out object c) && c is double cd ? (int?)cd : null;
                    bool err = m.TryGetValue("is_error", out object e) && e is bool eb && eb;
                    msgs.Add(new StoredMessage(Str(m, "role"), Str(m, "content"), Str(m, "thinking"), err, Str(m, "model"), temp, ctx));
                }
            }
            detail = new ChatDetail(id, Str(obj, "title"), msgs);
            return true;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            return d.TryGetValue(key, out object v) ? v as string : null;
        }

        private struct Reply
        {
            public bool Ok;
            public int Status;
            public string Reason;
            public string Body;
        }

        private async Task<Reply> SendAsync(HttpRequestMessage req, CancellationToken ct, string what)
        {
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeoutMs);
                try
                {
                    using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return new Reply { Ok = resp.IsSuccessStatusCode, Status = (int)resp.StatusCode, Reason = resp.ReasonPhrase, Body = body };
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("EVA did not answer the " + what + " within " + (TimeoutMs / 1000) + " s.");
                }
                catch (HttpRequestException e)
                {
                    throw new InvalidOperationException("Could not reach EVA for the " + what + ": " + e.Message, e);
                }
            }
        }
    }
}
