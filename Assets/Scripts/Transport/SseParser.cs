using System.Text;

namespace AgentConsole.Transport
{
    /// <summary>
    /// Line-fed Server-Sent Events parser. Feed it one line at a time (without the terminator);
    /// it reports when a blank line completes an event. Comments (the keepalives) and the
    /// event/id/retry fields are ignored: EVA carries everything in <c>data:</c>.
    /// </summary>
    public sealed class SseParser
    {
        private readonly StringBuilder _data = new StringBuilder();
        private bool _hasData;

        /// <returns>True when <paramref name="data"/> holds a completed event's payload.</returns>
        public bool Feed(string line, out string data)
        {
            data = null;
            if (line == null) return false;

            if (line.Length == 0)
            {
                if (!_hasData) return false;
                data = _data.ToString();
                _data.Clear();
                _hasData = false;
                return true;
            }

            if (line[0] == ':') return false;

            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line.Substring(0, colon);
            if (field != "data") return false;

            string value = colon < 0 ? string.Empty : line.Substring(colon + 1);
            if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);

            if (_hasData) _data.Append('\n');
            _data.Append(value);
            _hasData = true;
            return false;
        }

        /// <summary>Drop a half-read event, e.g. when the connection is cut mid-event.</summary>
        public void Reset()
        {
            _data.Clear();
            _hasData = false;
        }
    }
}
