using System.Net;

namespace FlareAI.Core.Update
{
    /// <summary>
    /// A request to a loopback address (127.0.0.1, localhost, ::1) goes straight there, with no proxy. Only the test servers on this
    /// computer use one, and Windows' proxy auto-detect on the first request of a busy process can stall for about ten seconds, which
    /// is the timeout that made the Core tests that talk to those servers fail now and then. A real address keeps the default proxy.
    /// </summary>
    public static class LoopbackProxy
    {
        /// <summary>Clears the proxy when the request is to this computer. Returns true when it did.</summary>
        public static bool Apply(HttpWebRequest request)
        {
            if (request == null || request.RequestUri == null || !request.RequestUri.IsLoopback) return false;
            request.Proxy = null;
            return true;
        }
    }
}
