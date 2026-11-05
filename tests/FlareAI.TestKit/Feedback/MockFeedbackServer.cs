using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FlareAI.TestKit.Feedback
{
    /// <summary>One request the mock received: path, headers and the body (unzipped when it came gzip-compressed).</summary>
    public sealed class FeedbackRequest
    {
        public string Method, Path, ContentType, HouseStyleHeader, ContentEncoding, Body;
    }

    /// <summary>
    /// A localhost stand-in for the House Style feedback Worker (tools/feedback-worker): <c>POST /v1/report</c> and
    /// <c>POST /v1/false-positive</c> answer <c>{"ok":true,"reference":"#n","duplicate":false}</c> by default. Tests set
    /// <see cref="Respond"/> to answer 429, 502 or anything else. Nothing here contacts the real service.
    /// </summary>
    public sealed class MockFeedbackServer : IDisposable
    {
        private HttpListener _listener;
        private readonly Thread _thread;
        private readonly List<FeedbackRequest> _requests = new List<FeedbackRequest>();
        private readonly object _gate = new object();
        private int _next = 1;

        /// <summary>Status code, JSON body and extra headers for a request; null uses the default success.</summary>
        public Func<FeedbackRequest, Tuple<int, string, Dictionary<string, string>>> Respond;

        public MockFeedbackServer()
        {
            FlareAI.TestKit.PoolFloor.Raise();
            for (var attempt = 0; ; attempt++)
            {
                Port = FreePort();
                BaseUrl = "http://127.0.0.1:" + Port;
                _listener = new HttpListener();
                _listener.Prefixes.Add(BaseUrl + "/");
                try { _listener.Start(); break; }
                catch (HttpListenerException) { try { _listener.Close(); } catch (Exception) { } if (attempt >= 20) throw; }
            }
            _thread = new Thread(Loop) { IsBackground = true, Name = "MockFeedbackServer" };
            _thread.Start();
        }

        public int Port { get; private set; }
        public string BaseUrl { get; private set; }

        public IList<FeedbackRequest> Requests { get { lock (_gate) return _requests.ToArray(); } }

        /// <summary>An answer: status, body and optional headers.</summary>
        public static Tuple<int, string, Dictionary<string, string>> Answer(int status, string json, string header = null, string value = null)
        {
            var h = new Dictionary<string, string>();
            if (header != null) h[header] = value;
            return Tuple.Create(status, json, h);
        }

        /// <summary>A port nothing listens on: for "the service can't be reached".</summary>
        public static string DeadUrl()
        {
            return "http://127.0.0.1:" + FreePort();
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
            _thread.Join(2000);
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { return; }
                new Thread(() => Serve(ctx)) { IsBackground = true, Name = "mock server request" }.Start();   // not the thread pool: a starved pool delayed answers past the client's timeout (#396)
            }
        }

        private void Serve(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                byte[] raw;
                using (var ms = new MemoryStream()) { req.InputStream.CopyTo(ms); raw = ms.ToArray(); }
                var encoding = req.Headers["Content-Encoding"];
                if (string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase))
                    using (var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress))
                    using (var ms = new MemoryStream()) { gz.CopyTo(ms); raw = ms.ToArray(); }
                var r = new FeedbackRequest
                {
                    Method = req.HttpMethod, Path = req.Url.AbsolutePath, ContentType = req.ContentType,
                    HouseStyleHeader = req.Headers["X-House-Style"], ContentEncoding = encoding, Body = Encoding.UTF8.GetString(raw)
                };
                int n;
                lock (_gate) { _requests.Add(r); n = _next++; }
                var respond = Respond;
                var answer = respond != null ? respond(r) : null;
                if (answer == null) answer = Answer(200, "{\"ok\":true,\"reference\":\"#" + n + "\",\"duplicate\":false}");
                var bytes = Encoding.UTF8.GetBytes(answer.Item2 ?? "");
                ctx.Response.StatusCode = answer.Item1;
                ctx.Response.ContentType = "application/json";
                foreach (var h in answer.Item3) ctx.Response.Headers[h.Key] = h.Value;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
