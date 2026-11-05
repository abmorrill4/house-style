using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlareAI.TestKit.Llm
{
    /// <summary>What the mock server does with one request.</summary>
    public sealed class MockReply
    {
        public enum Kinds { Ok, Error, Malformed, TruncatedBody, Hang, Redirect }

        public Kinds Kind { get; private set; }
        public int Status { get; private set; }
        public string Text { get; private set; }
        public string StopReason { get; private set; }
        public string ErrorType { get; private set; }
        public string Message { get; private set; }
        /// <summary>Delay before answering (a slow model or gateway).</summary>
        public int DelayMs { get; private set; }
        /// <summary>Where a redirect points.</summary>
        public string Location { get; private set; }

        /// <summary>A 3xx answer that points at <paramref name="location"/>.</summary>
        public static MockReply Redirect(string location, int status = 302) { return new MockReply { Kind = Kinds.Redirect, Status = status, Location = location }; }

        /// <summary>A completion with this text, shaped for the endpoint called (Anthropic messages or gateway chat completions).</summary>
        public static MockReply Ok(string text, string stopReason = "end_turn") { return new MockReply { Kind = Kinds.Ok, Status = 200, Text = text, StopReason = stopReason }; }

        /// <summary>An error body in the endpoint's shape: <c>{"type":"error","error":{"type","message"}}</c> or <c>{"error":{"type","message"}}</c>.</summary>
        public static MockReply Error(int status, string type, string message) { return new MockReply { Kind = Kinds.Error, Status = status, ErrorType = type, Message = message }; }

        public static MockReply Unauthorized() { return Error(401, "authentication_error", "invalid x-api-key"); }
        public static MockReply ModelDenied() { return Error(403, "permission_error", "model not allowed for this key"); }
        public static MockReply RateLimited() { return Error(429, "rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit"); }
        public static MockReply ServerError(int status = 500) { return Error(status, status == 529 ? "overloaded_error" : "api_error", status == 529 ? "Overloaded" : "Internal server error"); }

        /// <summary>HTTP 200 whose body is not JSON.</summary>
        public static MockReply Malformed() { return new MockReply { Kind = Kinds.Malformed, Status = 200 }; }

        /// <summary>HTTP 200 that announces the full body length, sends half of it, and drops the connection.</summary>
        public static MockReply TruncatedBody(string text = "{\"findings\":[]}") { return new MockReply { Kind = Kinds.TruncatedBody, Status = 200, Text = text, StopReason = "end_turn" }; }

        /// <summary>Never answers until the server stops (exercises client timeouts).</summary>
        public static MockReply Hang() { return new MockReply { Kind = Kinds.Hang }; }

        /// <summary>The same reply after <paramref name="ms"/> milliseconds.</summary>
        public MockReply After(int ms)
        {
            return new MockReply { Kind = Kind, Status = Status, Text = Text, StopReason = StopReason, ErrorType = ErrorType, Message = Message, Location = Location, DelayMs = ms };
        }
    }

    /// <summary>One request the mock received. Header values are kept in memory only, for assertions.</summary>
    public sealed class MockRequest
    {
        public string Path { get; set; }
        public string Method { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public string Body { get; set; }
        public bool IsGateway { get { return Path.EndsWith("/chat/completions", StringComparison.Ordinal); } }
    }

    /// <summary>
    /// A localhost <see cref="HttpListener"/> that answers like the Anthropic Messages API (<c>/v1/messages</c>) or the
    /// Bifrost gateway (<c>/v1/chat/completions</c>, OpenAI-shaped), so AnthropicClient and BifrostClient run for real
    /// against 401, 403 model denied, 429, 5xx, malformed JSON, a truncated body and slow answers. Replies queued with
    /// <see cref="Enqueue"/> are used in order, then <see cref="Default"/>.
    /// </summary>
    public sealed class MockLlmServer : IDisposable
    {
        private HttpListener _listener;
        private readonly Queue<MockReply> _queue = new Queue<MockReply>();
        private readonly List<MockRequest> _received = new List<MockRequest>();
        private readonly object _gate = new object();
        private readonly ManualResetEvent _stopping = new ManualResetEvent(false);
        private readonly Thread _thread;

        public MockLlmServer()
        {
            for (var attempt = 0; ; attempt++)
            {
                Port = FreePort();
                BaseUrl = "http://127.0.0.1:" + Port;
                _listener = new HttpListener();   // a failed Start() disposes the listener: use a fresh one per attempt
                _listener.Prefixes.Add(BaseUrl + "/");
                try { _listener.Start(); break; }
                catch (HttpListenerException) { try { _listener.Close(); } catch (Exception) { } if (attempt >= 20) throw; }   // another test process took the port
            }
            Default = MockReply.Ok("{}");
            _thread = new Thread(Loop) { IsBackground = true, Name = "MockLlmServer" };
            _thread.Start();
        }

        public int Port { get; private set; }
        /// <summary>Base URL for AnthropicClient (it appends /v1/messages) or BifrostClient (it appends /v1/chat/completions).</summary>
        public string BaseUrl { get; private set; }
        public MockReply Default { get; set; }

        public IList<MockRequest> Received { get { lock (_gate) return _received.ToArray(); } }

        public MockLlmServer Enqueue(params MockReply[] replies)
        {
            lock (_gate) foreach (var r in replies) _queue.Enqueue(r);
            return this;
        }

        public void Dispose()
        {
            _stopping.Set();
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
                ThreadPool.QueueUserWorkItem(_ => Serve(ctx));
            }
        }

        private void Serve(HttpListenerContext ctx)
        {
            try
            {
                var req = new MockRequest { Path = ctx.Request.Url.AbsolutePath, Method = ctx.Request.HttpMethod, Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
                foreach (string h in ctx.Request.Headers) req.Headers[h] = ctx.Request.Headers[h];
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) req.Body = r.ReadToEnd();
                MockReply reply;
                lock (_gate)
                {
                    _received.Add(req);
                    reply = _queue.Count > 0 ? _queue.Dequeue() : Default;
                }
                if (reply.Kind == MockReply.Kinds.Hang) { _stopping.WaitOne(); ctx.Response.Abort(); return; }
                if (reply.DelayMs > 0 && _stopping.WaitOne(reply.DelayMs)) { ctx.Response.Abort(); return; }

                if (reply.Kind == MockReply.Kinds.Redirect)
                {
                    ctx.Response.StatusCode = reply.Status;
                    ctx.Response.RedirectLocation = reply.Location;
                    ctx.Response.Close();
                    return;
                }

                var model = ModelOf(req.Body);
                string body;
                switch (reply.Kind)
                {
                    case MockReply.Kinds.Error: body = ErrorBody(req.IsGateway, reply); break;
                    case MockReply.Kinds.Malformed: body = "<html><body>502 Bad Gateway</body></html>"; break;
                    default: body = OkBody(req.IsGateway, model, reply); break;
                }
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = reply.Status;
                ctx.Response.ContentType = reply.Kind == MockReply.Kinds.Malformed ? "text/html" : "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                if (reply.Kind == MockReply.Kinds.TruncatedBody)
                {
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length / 2);
                    ctx.Response.OutputStream.Flush();
                    ctx.Response.Abort();
                    return;
                }
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception)
            {
                try { ctx.Response.Abort(); } catch (Exception) { }
            }
        }

        private static string ModelOf(string body)
        {
            try { return (string)JObject.Parse(body)["model"]; } catch (Exception) { return null; }
        }

        private static string OkBody(bool gateway, string model, MockReply reply)
        {
            if (gateway)
            {
                var finish = reply.StopReason == "max_tokens" ? "length" : reply.StopReason == "refusal" ? "content_filter" : "stop";
                return new JObject
                {
                    ["id"] = "chatcmpl-mock",
                    ["object"] = "chat.completion",
                    ["model"] = model,
                    ["choices"] = new JArray(new JObject { ["index"] = 0, ["message"] = new JObject { ["role"] = "assistant", ["content"] = reply.Text }, ["finish_reason"] = finish }),
                    ["usage"] = new JObject { ["prompt_tokens"] = 120, ["completion_tokens"] = 30, ["total_tokens"] = 150 }
                }.ToString(Formatting.None);
            }
            return new JObject
            {
                ["id"] = "msg_mock",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = reply.Text }),
                ["stop_reason"] = reply.StopReason,
                ["usage"] = new JObject { ["input_tokens"] = 120, ["output_tokens"] = 30, ["cache_read_input_tokens"] = 0, ["cache_creation_input_tokens"] = 0 }
            }.ToString(Formatting.None);
        }

        private static string ErrorBody(bool gateway, MockReply reply)
        {
            var error = new JObject { ["type"] = reply.ErrorType, ["message"] = reply.Message };
            if (gateway) return new JObject { ["error"] = error, ["is_bifrost_error"] = true, ["status_code"] = reply.Status }.ToString(Formatting.None);
            return new JObject { ["type"] = "error", ["error"] = error }.ToString(Formatting.None);
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
