using NUnit.Framework;
using AgentConsole.Domain;

namespace AgentConsole.Tests
{
    [TestFixture]
    public class WebBackendUrlTests
    {
        private const string Fallback = "http://fallback:8000";

        [TestCase("http://10.0.0.219:8090/", "http://10.0.0.219:8000")]
        [TestCase("http://10.0.0.219:8090/index.html?x=1#y", "http://10.0.0.219:8000")]
        [TestCase("http://10.0.0.219/", "http://10.0.0.219:8000")]
        [TestCase("http://my-box.tail1234.ts.net:8090/", "http://my-box.tail1234.ts.net:8000")]
        [TestCase("http://100.101.102.103:8090/", "http://100.101.102.103:8000")]
        [TestCase("https://example.com/app/", "https://example.com:8000")]
        [TestCase("http://[fd7a::1]:8090/", "http://[fd7a::1]:8000")]
        public void KeepsSchemeAndHost_ReplacesThePort(string page, string expected)
        {
            Assert.AreEqual(expected, WebBackendUrl.FromPageUrl(page, Fallback));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not a url")]
        [TestCase("file:///C:/build/index.html")]
        public void NoUsablePageUrl_FallsBack(string page)
        {
            Assert.AreEqual(Fallback, WebBackendUrl.FromPageUrl(page, Fallback));
        }
    }
}
