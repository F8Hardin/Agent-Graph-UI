using System.Collections.Generic;

namespace AgentConsole.Domain
{
    /// <summary>
    /// Messages typed while a run is live. Mirrors the web client's messageQueueRef: Enter queues, the queue drains
    /// one message per finished run, and Stop clears it. Plain data, so the semantics are testable headlessly.
    /// </summary>
    public sealed class PromptQueue
    {
        private readonly Queue<string> _items = new Queue<string>();

        public int Count => _items.Count;

        public void Enqueue(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) _items.Enqueue(text);
        }

        public bool TryTake(out string text)
        {
            if (_items.Count == 0)
            {
                text = null;
                return false;
            }
            text = _items.Dequeue();
            return true;
        }

        /// <returns>How many messages were dropped.</returns>
        public int Clear()
        {
            int n = _items.Count;
            _items.Clear();
            return n;
        }
    }
}
