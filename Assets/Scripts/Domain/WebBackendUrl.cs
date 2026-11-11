using System;

namespace AgentConsole.Domain
{
    /// <summary>
    /// Where the browser build looks for EVA when nobody has set a URL: the host the page itself was served from, on EVA's
    /// port. The page and EVA live on the same machine, so a LAN address or a Tailscale name or IP works with no setting
    /// (http://10.0.0.219:8090/ becomes http://10.0.0.219:8000). Plain logic with no Unity types so it is testable.
    /// </summary>
    public static class WebBackendUrl
    {
        public const int EvaPort = 8000;

        /// <returns>The backend URL, or <paramref name="fallback"/> when the page URL is empty or is not an http(s) address (the editor, a file).</returns>
        public static string FromPageUrl(string pageUrl, string fallback, int port = EvaPort)
        {
            if (string.IsNullOrWhiteSpace(pageUrl)) return fallback;
            if (!Uri.TryCreate(pageUrl.Trim(), UriKind.Absolute, out Uri u)) return fallback;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return fallback;
            string host = u.Host;
            if (host.Length == 0) return fallback;
            if (host.IndexOf(':') >= 0 && host[0] != '[') host = "[" + host + "]"; // an IPv6 literal needs its brackets back
            return u.Scheme + "://" + host + ":" + port;
        }
    }
}
