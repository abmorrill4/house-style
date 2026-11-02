using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Llm
{
    /// <summary>
    /// POST {baseUrl}/v1/messages with x-api-key. Built on HttpWebRequest because the plugin must target .NET 4.0.
    /// Per request: model, effort and an output schema (structured outputs). The response is checked before it is
    /// read: an answer cut off at the ceiling or a safety refusal becomes a typed <see cref="LlmException"/>.
    /// Sonnet 5.5 / Opus 5.5 / Fable requests opt into server-side refusal fallbacks.
    /// </summary>
    public sealed class AnthropicClient : ILlmClient
    {
        public const string ApiVersion = "2023-06-01";
        public const string FallbackBeta = "server-side-fallback-2026-07-01";
        public const int DefaultMaxTokens = 16000;

        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly int _timeoutMs;

        public AnthropicClient(string baseUrl, string apiKey, string model, int timeoutSeconds)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new LlmException("No API key configured. Open House Style > Settings.");
            string url, err;
            if (!LlmErrors.TryNormalizeBaseUrl(string.IsNullOrEmpty((baseUrl ?? "").Trim()) ? "https://api.anthropic.com" : baseUrl, out url, out err)) throw new LlmException(err);
            _baseUrl = url;
            _apiKey = apiKey;
            _model = model;
            _timeoutMs = Math.Max(10, timeoutSeconds) * 1000;
            EnsureTls12();
        }

        /// <summary>Models that accept the server-side "default" refusal fallback (Claude API).</summary>
        public static bool UsesFallback(string model)
        {
            return model != null && (model.StartsWith("claude-sonnet-5-5") || model.StartsWith("claude-opus-5") || model.StartsWith("claude-fable-5"));
        }

        /// <summary>Exposed for tests: the exact JSON body we send.</summary>
        public static string BuildBody(string model, LlmRequest req)
        {
            model = string.IsNullOrEmpty(req.Model) ? model : req.Model;
            var body = new JObject
            {
                ["model"] = model,
                ["max_tokens"] = req.MaxTokens > 0 ? req.MaxTokens : DefaultMaxTokens,
                ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = UserContent(req) })
            };
            // The system prompt (instructions + the always-on rules) is identical across calls: cache it, so per-section
            // reviews and repeat checks pay for it once. The rule packs for the unit's content are a second cached block.
            if (!string.IsNullOrEmpty(req.System))
            {
                // How long it lives is the task's choice (Routing.CacheFor): an hour for review and rewrite, which writers
                // use every few minutes all day (2x once, then 0.1x per read); five minutes or nothing for tasks used a few
                // times a week, where an hour-long write would rarely be read.
                var ttl = req.CacheTtl ?? "1h";
                var cached = ttl != "none";
                var block = new JObject { ["type"] = "text", ["text"] = req.System };
                if (cached) block["cache_control"] = ttl == "1h" ? new JObject { ["type"] = "ephemeral", ["ttl"] = "1h" } : new JObject { ["type"] = "ephemeral" };
                var system = new JArray(block);
                if (!string.IsNullOrEmpty(req.SystemPacks))
                {
                    // Always 5 minutes: shared by the units of one check (a shorter TTL may follow a 1-hour one).
                    var packs = new JObject { ["type"] = "text", ["text"] = req.SystemPacks };
                    if (cached) packs["cache_control"] = new JObject { ["type"] = "ephemeral" };
                    system.Add(packs);
                }
                if (!string.IsNullOrEmpty(req.SystemTerms))
                    system.Add(new JObject { ["type"] = "text", ["text"] = req.SystemTerms });   // unique per text: caching it would only cost
                body["system"] = system;
            }
            var output = new JObject();
            if (!string.IsNullOrEmpty(req.Effort)) output["effort"] = req.Effort;
            if (req.Schema != null) output["format"] = new JObject { ["type"] = "json_schema", ["schema"] = req.Schema };
            if (output.Count > 0) body["output_config"] = output;
            if (UsesFallback(model)) body["fallbacks"] = "default";
            return body.ToString(Formatting.None);
        }

        /// <summary>Plain text, or image blocks followed by the text when the request carries images.</summary>
        private static JToken UserContent(LlmRequest req)
        {
            if (req.Images == null || req.Images.Count == 0) return req.User ?? "";
            var parts = new JArray();
            foreach (var img in req.Images)
                parts.Add(new JObject { ["type"] = "image", ["source"] = new JObject { ["type"] = "base64", ["media_type"] = img.MediaType, ["data"] = img.Base64 } });
            parts.Add(new JObject { ["type"] = "text", ["text"] = req.User ?? "" });
            return parts;
        }

        public LlmResponse Complete(LlmRequest request)
        {
            var model = string.IsNullOrEmpty(request.Model) ? _model : request.Model;
            var payload = Encoding.UTF8.GetBytes(BuildBody(_model, request));

            var http = (HttpWebRequest)WebRequest.Create(_baseUrl + "/v1/messages");
            http.AllowAutoRedirect = false;
            http.Method = "POST";
            http.ContentType = "application/json";
            http.Accept = "application/json";
            http.Timeout = _timeoutMs;
            http.ReadWriteTimeout = _timeoutMs;
            http.UserAgent = "FlareAI/0.1 (MadCap Flare plugin)";
            http.Headers["x-api-key"] = _apiKey;
            http.Headers["anthropic-version"] = ApiVersion;
            if (UsesFallback(model)) http.Headers["anthropic-beta"] = FallbackBeta;
            http.ContentLength = payload.Length;

            LlmResponse resp;
            try
            {
                using (var s = http.GetRequestStream()) s.Write(payload, 0, payload.Length);
                using (var r = (HttpWebResponse)http.GetResponse())
                {
                    if (LlmErrors.IsRedirect((int)r.StatusCode)) throw LlmErrors.FromRedirect((int)r.StatusCode, false);
                    resp = Parse(ReadAll(r));
                }
            }
            catch (JsonException jex) { throw LlmErrors.FromMalformed(jex, false); }
            catch (IOException ioex) { throw LlmErrors.FromDropped(ioex, false, _baseUrl); }
            catch (WebException wex)
            {
                var r = wex.Response as HttpWebResponse;
                if (r == null) throw LlmErrors.FromNetwork(wex, false, _baseUrl);
                if (LlmErrors.IsRedirect((int)r.StatusCode)) throw LlmErrors.FromRedirect((int)r.StatusCode, false);
                var text = ReadAll(r);
                var detail = text;
                try { detail = JObject.Parse(text)["error"]["message"].ToString(); } catch (Exception) { }
                throw LlmErrors.FromHttp((int)r.StatusCode, detail, false, _apiKey);
            }
            return Check(resp, request);
        }

        /// <summary>
        /// Stop reason first, then the contract: a cut-off answer, a refusal, or JSON that doesn't parse never reaches
        /// a caller as if it were a result.
        /// </summary>
        public static LlmResponse Check(LlmResponse resp, LlmRequest request)
        {
            if (resp.StopReason == "max_tokens")
                throw new LlmException("The AI's answer was cut off (" + resp.OutputTokens + " tokens). Try a smaller selection.", 0, null, LlmFailure.Truncated);
            if (resp.StopReason == "refusal")
                throw new LlmException("The AI declined this request (safety filter). Try rewording or a smaller selection.", 0, null, LlmFailure.Refusal);
            if (request.Schema != null)
            {
                try { resp.Json = JObject.Parse(resp.Text); }
                catch (JsonException ex) { throw new LlmException("The AI's answer didn't match the expected format.", 0, ex, LlmFailure.Contract); }
            }
            return resp;
        }

        /// <summary>Exposed for tests.</summary>
        public static LlmResponse Parse(string json)
        {
            var o = JObject.Parse(json);
            var sb = new StringBuilder();
            var content = o["content"] as JArray;
            if (content != null)
                foreach (var block in content)
                    if ((string)block["type"] == "text") sb.Append((string)block["text"]);

            var usage = o["usage"] as JObject;
            return new LlmResponse
            {
                Text = sb.ToString(),
                Model = (string)o["model"],
                StopReason = (string)o["stop_reason"],
                InputTokens = usage != null ? (int?)usage["input_tokens"] ?? 0 : 0,
                OutputTokens = usage != null ? (int?)usage["output_tokens"] ?? 0 : 0,
                CacheReadTokens = usage != null ? (int?)usage["cache_read_input_tokens"] ?? 0 : 0,
                CacheWriteTokens = usage != null ? (int?)usage["cache_creation_input_tokens"] ?? 0 : 0,
                CacheWrite1hTokens = usage != null && usage["cache_creation"] is JObject cc ? (int?)cc["ephemeral_1h_input_tokens"] ?? 0 : 0
            };
        }

        private static string ReadAll(HttpWebResponse resp)
        {
            using (var s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static void EnsureTls12()
        {
            // net40 has no SecurityProtocolType.Tls12 constant; the runtime (4.5+) honours the raw value.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
            AllowParallelCalls();
        }

        /// <summary>
        /// .NET Framework allows 2 connections per host by default, which silently serializes parallel model calls
        /// (6 per-section reviews ran 2 at a time). Raise it once for the process.
        /// </summary>
        public static void AllowParallelCalls()
        {
            if (ServicePointManager.DefaultConnectionLimit < 32) ServicePointManager.DefaultConnectionLimit = 32;
        }
    }
}
