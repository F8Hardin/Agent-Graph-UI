using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentConsole.Domain;
using AgentConsole.Transport;

namespace AgentConsole.WebTransport
{
    /// <summary>
    /// <see cref="EvaChatStore"/> on UnityWebRequest, for the browser build. Same endpoints, bodies, parsers (EvaChatStore's
    /// public static ones are reused, not copied) and error text; only the way a request is sent differs.
    /// </summary>
    public sealed class UwrChatStore : IChatStore
    {
        private const int TimeoutMs = 20000;
        private readonly string _base;

        public UwrChatStore(string baseUrl = EvaSseTransport.DefaultBaseUrl)
        {
            _base = (string.IsNullOrWhiteSpace(baseUrl) ? EvaSseTransport.DefaultBaseUrl : baseUrl).TrimEnd('/');
        }

        public async Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken ct)
        {
            UwrReply r = await Send("GET", "/chats", null, "chat list", ct);
            if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
            if (!EvaChatStore.TryParseList(r.Body, out List<ChatSummary> rows))
            {
                throw new InvalidOperationException("EVA's chat list was not in the expected form.");
            }
            return rows;
        }

        public async Task<string> CreateAsync(string title, CancellationToken ct)
        {
            UwrReply r = await Send("POST", "/chats", "{\"title\":" + MiniJson.Quote(title ?? string.Empty) + "}", "new chat", ct);
            if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
            if (MiniJson.TryParse(r.Body, out object root) && root is Dictionary<string, object> obj
                && obj.TryGetValue("id", out object id) && id is string s && s.Length > 0)
            {
                return s;
            }
            throw new InvalidOperationException("EVA did not return an id for the new chat.");
        }

        public async Task<ChatDetail> LoadAsync(string id, CancellationToken ct)
        {
            UwrReply r = await Send("GET", "/chats/" + Uri.EscapeDataString(id), null, "chat load", ct);
            if (r.Status == 404) throw new ChatNotFoundException(id);
            if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
            if (!EvaChatStore.TryParseDetail(r.Body, out ChatDetail detail))
            {
                throw new InvalidOperationException("EVA's chat was not in the expected form.");
            }
            return detail;
        }

        public async Task DeleteAsync(string id, CancellationToken ct)
        {
            UwrReply r = await Send("DELETE", "/chats/" + Uri.EscapeDataString(id), null, "chat delete", ct);
            if (r.Status == 404) throw new ChatNotFoundException(id);
            if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
        }

        public async Task SaveMessagesAsync(string id, IReadOnlyList<StoredMessage> messages, CancellationToken ct)
        {
            UwrReply r = await Send("POST", "/chats/" + Uri.EscapeDataString(id) + "/messages",
                EvaChatStore.MessagesJson(messages), "message save", ct);
            if (r.Status == 404) throw new ChatNotFoundException(id);
            if (!r.Ok) throw new InvalidOperationException(ModelsWire.ErrorText(r.Status, r.Reason, r.Body));
        }

        private Task<UwrReply> Send(string method, string path, string body, string what, CancellationToken ct) =>
            UwrHttp.BufferedAsync(method, _base + path, body, TimeoutMs, ct,
                "EVA did not answer the " + what + " within " + (TimeoutMs / 1000) + " s.",
                "Could not reach EVA for the " + what + ": ");
    }
}
