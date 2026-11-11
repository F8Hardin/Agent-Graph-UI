using System;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using AgentConsole.Transport;

namespace AgentConsole.WebTransport
{
    /// <summary>
    /// Feeds an SSE response to <see cref="SseParser"/> as the bytes arrive, instead of buffering the whole reply. A reply
    /// runs for minutes, so a download handler that only hands over its data at the end would show nothing until the model
    /// had finished. ReceiveData runs on the main thread, so the payload callback may emit events without any hand-off.
    ///
    /// A chunk can end inside a multi-byte UTF-8 character, so decoding goes through a stateful Decoder rather than
    /// converting each chunk on its own. Line splitting follows StreamReader.ReadLine (LF, CR and CRLF all end a line),
    /// which is what the HttpClient transport relies on.
    /// </summary>
    internal sealed class SseDownloadHandler : DownloadHandlerScript
    {
        private readonly Action<UnityWebRequest> _onHeaders;
        private readonly Action<string> _onPayload;
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private readonly SseParser _sse = new SseParser();
        private readonly StringBuilder _line = new StringBuilder();
        private char[] _chars = new char[8192];
        private bool _skipLf;
        private bool _headersSeen;

        /// <summary>Set by the owner right after construction; the handler needs the status and headers it belongs to.</summary>
        public UnityWebRequest Request;

        /// <summary>Bytes received, so a test or a log can tell a stream that is quiet from one that is dead.</summary>
        public long BytesReceived { get; private set; }

        /// <param name="onHeaders">Called once, with the first chunk, when the response headers are readable.</param>
        /// <param name="onPayload">Called with the data of each completed event.</param>
        public SseDownloadHandler(Action<UnityWebRequest> onHeaders, Action<string> onPayload) : base(new byte[8192])
        {
            _onHeaders = onHeaders;
            _onPayload = onPayload;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength < 1) return true;
            BytesReceived += dataLength;
            try
            {
                if (!_headersSeen)
                {
                    _headersSeen = true;
                    if (Request != null) _onHeaders?.Invoke(Request);
                }

                // An error status carries an error body, not events. The owner reports it from the request's result.
                if (Request != null && Request.responseCode >= 400) return true;

                int max = Encoding.UTF8.GetMaxCharCount(dataLength);
                if (_chars.Length < max) _chars = new char[max];
                int n = _decoder.GetChars(data, 0, dataLength, _chars, 0, false);
                for (int i = 0; i < n; i++)
                {
                    char c = _chars[i];
                    if (_skipLf)
                    {
                        _skipLf = false;
                        if (c == '\n') continue; // the second half of a CRLF split across two chunks
                    }
                    if (c == '\n')
                    {
                        EndLine();
                    }
                    else if (c == '\r')
                    {
                        EndLine();
                        _skipLf = true;
                    }
                    else
                    {
                        _line.Append(c);
                    }
                }
            }
            catch (Exception e)
            {
                // Never let a bad event throw into the engine's download loop: that would drop the connection.
                Debug.LogException(e);
            }
            return true;
        }

        private void EndLine()
        {
            string line = _line.ToString();
            _line.Clear();
            if (_sse.Feed(line, out string payload)) _onPayload?.Invoke(payload);
        }
    }
}
