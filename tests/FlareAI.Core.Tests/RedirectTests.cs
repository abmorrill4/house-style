using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FlareAI.Core.Llm;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class RedirectTests
    {
        private sealed class Server : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly Thread _thread;
            private readonly string _location;
            private int _count;
            public string Url { get; private set; }
            public int Count { get { return Thread.VolatileRead(ref _count); } }

            /// <summary>Answers every request with 302 to <paramref name="location"/>, or with 200 when it is null.</summary>
            public Server(string location)
            {
                _location = location;
                var port = FreePort();
                Url = "http://127.0.0.1:" + port;
                _listener.Prefixes.Add(Url + "/");
                _listener.Start();
                _thread = new Thread(Serve) { IsBackground = true };
                _thread.Start();
            }

            private static int FreePort()
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }

            private void Serve()
            {
                while (true)
                {
                    HttpListenerContext ctx;
                    try { ctx = _listener.GetContext(); }
                    catch (Exception) { return; }
                    Interlocked.Increment(ref _count);
                    try
                    {
                        if (_location != null)
                        {
                            ctx.Response.StatusCode = 302;
                            ctx.Response.RedirectLocation = _location;
                        }
                        else ctx.Response.StatusCode = 200;
                        ctx.Response.Close();
                    }
                    catch (Exception) { }
                }
            }

            public void Dispose()
            {
                try { _listener.Close(); } catch (Exception) { }
            }
        }

        private static void Run(Action<string> call, string key)
        {
            var b = new Server(null);
            try
            {
                var a = new Server(b.Url + "/v1/target");
                try
                {
                    var ex = Assert.Throws<LlmException>(() => call(a.Url));
                    Assert.Contains("redirects", ex.Message);
                    Assert.DoesNotContain(key, ex.Message);
                    Assert.Equal(302, ex.StatusCode);
                    Assert.Equal(0, b.Count);
                    Assert.Equal(1, a.Count);
                }
                finally { a.Dispose(); }
            }
            finally { b.Dispose(); }
        }

        [Fact]
        public void AnthropicClient_DoesNotFollowARedirect()
        {
            Run(url => new AnthropicClient(url, "sk-secret-1", "claude-sonnet-5-5", 10).Complete(new LlmRequest { User = "hi" }), "sk-secret-1");
        }

        [Fact]
        public void BifrostClient_DoesNotFollowARedirect()
        {
            Run(url => new BifrostClient(url, "vk-secret-1", "claude-sonnet-5-5", 10).Complete(new LlmRequest { User = "hi" }), "vk-secret-1");
        }

        [Fact]
        public void ModelList_DoesNotFollowARedirect()
        {
            Run(url => AnthropicModels.List(url, "sk-secret-1", 10), "sk-secret-1");
        }
    }
}
