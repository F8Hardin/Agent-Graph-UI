using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentConsole.Domain
{
    /// <summary>One row of the chat list: what GET /chats returns, minus the fields the console does not show.</summary>
    public sealed class ChatSummary
    {
        public string Id { get; }
        public string Title { get; }

        /// <summary>ISO-8601 time of the last run, or of the last update when the chat never ran. May be null.</summary>
        public string LastRun { get; }

        public int MessageCount { get; }

        public ChatSummary(string id, string title, string lastRun, int messageCount)
        {
            Id = id;
            Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title;
            LastRun = lastRun;
            MessageCount = messageCount;
        }
    }

    /// <summary>A message as the server stores it. <see cref="Role"/> is "user" or "assistant", as on the wire.</summary>
    public sealed class StoredMessage
    {
        public string Role { get; }
        public string Content { get; }
        public string Thinking { get; }
        public bool IsError { get; }
        public string Model { get; }
        public float? Temperature { get; }
        public int? ContextLength { get; }

        public StoredMessage(
            string role, string content, string thinking = null, bool isError = false,
            string model = null, float? temperature = null, int? contextLength = null)
        {
            Role = role;
            Content = content ?? string.Empty;
            Thinking = thinking ?? string.Empty;
            IsError = isError;
            Model = model;
            Temperature = temperature;
            ContextLength = contextLength;
        }
    }

    public sealed class ChatDetail
    {
        public string Id { get; }
        public string Title { get; }
        public IReadOnlyList<StoredMessage> Messages { get; }

        public ChatDetail(string id, string title, IReadOnlyList<StoredMessage> messages)
        {
            Id = id;
            Title = title;
            Messages = messages ?? new List<StoredMessage>();
        }
    }

    /// <summary>One prior turn, in the shape EVA's PromptData.history takes.</summary>
    public readonly struct HistoryItem
    {
        public string Role { get; }
        public string Content { get; }

        public HistoryItem(string role, string content)
        {
            Role = role;
            Content = content ?? string.Empty;
        }
    }

    /// <summary>Thrown by a store when the chat no longer exists (deleted elsewhere), so the caller can drop its stale id.</summary>
    public sealed class ChatNotFoundException : Exception
    {
        public ChatNotFoundException(string id) : base("Chat " + id + " was not found.")
        {
        }
    }

    /// <summary>
    /// Where chats live. Lives in Domain for the reason <see cref="IModelSelector"/> does: the View needs the
    /// shape of the chat list without ever referencing the assembly that does the HTTP. Every call may be slow and
    /// runs off the main thread; <see cref="ChatSession"/> is the only caller.
    /// </summary>
    public interface IChatStore
    {
        Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken ct);

        /// <summary>Create an empty chat and return the SERVER's id for it. Ids are never invented locally.</summary>
        Task<string> CreateAsync(string title, CancellationToken ct);

        /// <exception cref="ChatNotFoundException">The chat does not exist.</exception>
        Task<ChatDetail> LoadAsync(string id, CancellationToken ct);

        Task DeleteAsync(string id, CancellationToken ct);

        /// <summary>
        /// Persist finished messages. EVA stores a message only when a client sends it, so a client that never calls
        /// this leaves chats that list with 0 messages and reload empty.
        /// </summary>
        /// <exception cref="ChatNotFoundException">The chat was deleted while the turn was in flight.</exception>
        Task SaveMessagesAsync(string id, IReadOnlyList<StoredMessage> messages, CancellationToken ct);
    }

    /// <summary>
    /// A store that lives in memory. Used by the Replay transport, which has no server, so the chat islands are
    /// still real there; and by tests as the fake. Safe to call from any thread.
    /// </summary>
    public sealed class MemoryChatStore : IChatStore
    {
        private sealed class Chat
        {
            public string Id;
            public string Title;
            public string Updated;
            public List<StoredMessage> Messages = new List<StoredMessage>();
        }

        private readonly object _gate = new object();
        private readonly List<Chat> _chats = new List<Chat>();
        private int _next;

        /// <summary>Messages saved so far, per chat, in order. For assertions.</summary>
        public IReadOnlyList<StoredMessage> MessagesOf(string id)
        {
            lock (_gate)
            {
                Chat c = Find(id);
                return c == null ? new List<StoredMessage>() : new List<StoredMessage>(c.Messages);
            }
        }

        /// <summary>Seed a chat for the Replay demo. Returns its id.</summary>
        public string Seed(string title, params StoredMessage[] messages)
        {
            lock (_gate)
            {
                Chat c = new Chat { Id = "mem-" + (++_next), Title = title, Updated = DateTime.UtcNow.ToString("o") };
                c.Messages.AddRange(messages);
                _chats.Insert(0, c);
                return c.Id;
            }
        }

        public Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                List<ChatSummary> rows = new List<ChatSummary>();
                for (int i = 0; i < _chats.Count; i++)
                {
                    rows.Add(new ChatSummary(_chats[i].Id, _chats[i].Title, _chats[i].Updated, _chats[i].Messages.Count));
                }
                return Task.FromResult<IReadOnlyList<ChatSummary>>(rows);
            }
        }

        public Task<string> CreateAsync(string title, CancellationToken ct)
        {
            lock (_gate)
            {
                Chat c = new Chat { Id = "mem-" + (++_next), Title = title ?? string.Empty, Updated = DateTime.UtcNow.ToString("o") };
                _chats.Insert(0, c);
                return Task.FromResult(c.Id);
            }
        }

        public Task<ChatDetail> LoadAsync(string id, CancellationToken ct)
        {
            lock (_gate)
            {
                Chat c = Find(id);
                if (c == null) return Task.FromException<ChatDetail>(new ChatNotFoundException(id));
                return Task.FromResult(new ChatDetail(c.Id, c.Title, new List<StoredMessage>(c.Messages)));
            }
        }

        public Task DeleteAsync(string id, CancellationToken ct)
        {
            lock (_gate)
            {
                Chat c = Find(id);
                if (c == null) return Task.FromException(new ChatNotFoundException(id));
                _chats.Remove(c);
                return Task.CompletedTask;
            }
        }

        public Task SaveMessagesAsync(string id, IReadOnlyList<StoredMessage> messages, CancellationToken ct)
        {
            lock (_gate)
            {
                Chat c = Find(id);
                if (c == null) return Task.FromException(new ChatNotFoundException(id));
                c.Messages.AddRange(messages);
                c.Updated = DateTime.UtcNow.ToString("o");
                _chats.Remove(c);
                _chats.Insert(0, c);
                return Task.CompletedTask;
            }
        }

        private Chat Find(string id)
        {
            for (int i = 0; i < _chats.Count; i++)
            {
                if (_chats[i].Id == id) return _chats[i];
            }
            return null;
        }
    }
}
