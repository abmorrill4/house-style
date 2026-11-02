using System;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Llm
{
    /// <summary>
    /// Bifrost gateway transport: POST {gateway}/v1/chat/completions (OpenAI-compatible) with the virtual key in x-bf-vk.
    /// </summary>
    public sealed class BifrostClient : ILlmClient
    {
        private readonly string _baseUrl;
        private readonly string _virtualKey;
        private readonly string _model;
        private readonly int _timeoutMs;

        /// <summary>The gateway model for a requested model: the Claude 5.5 model of the same family, in Bifrost's provider/model form.</summary>
        // ponytail: hard-coded to the 5.5 family for the trial; pass names through once gateway teams pick their own models.
        public static string GatewayModel(string model)
        {
            var m = (model ?? "").ToLowerInvariant();
            return m.Contains("haiku") ? "anthropic/claude-haiku-5-5" : m.Contains("opus") ? "anthropic/claude-opus-5-5" : "anthropic/claude-sonnet-5-5";
        }

        public BifrostClient(string gatewayBaseUrl, string virtualKey, string model, int timeoutSeconds)
        {
            if (string.IsNullOrEmpty(gatewayBaseUrl)) throw new LlmException("No gateway URL configured. Open House Style > Settings.");
            if (string.IsNullOrEmpty(virtualKey)) throw new LlmException("No gateway key configured. Open House Style > Settings.");
            string url, err;
            if (!LlmErrors.TryNormalizeBaseUrl(gatewayBaseUrl, out url, out err)) throw new LlmException(err);
            _baseUrl = url;
            _virtualKey = virtualKey;
            _model = model;
            _timeoutMs = Math.Max(10, timeoutSeconds) * 1000;
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
            AnthropicClient.AllowParallelCalls();
        }

        public static string BuildBody(string model, LlmRequest req)
        {
            var messages = new JArray();
            if (!string.IsNullOrEmpty(req.System))
                messages.Add(new JObject { ["role"] = "system", ["content"] = req.System + (string.IsNullOrEmpty(req.SystemPacks) ? "" : "\n\n" + req.SystemPacks) + (string.IsNullOrEmpty(req.SystemTerms) ? "" : "\n\n" + req.SystemTerms) });
            JToken content = req.User ?? "";
            if (req.Images != null && req.Images.Count > 0)
            {
                var parts = new JArray();
                foreach (var img in req.Images)
                    parts.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = "data:" + img.MediaType + ";base64," + img.Base64 } });
                parts.Add(new JObject { ["type"] = "text", ["text"] = req.User ?? "" });
                content = parts;
            }
            messages.Add(new JObject { ["role"] = "user", ["content"] = content });
            var body = new JObject
            {
                ["model"] = GatewayModel(string.IsNullOrEmpty(req.Model) ? model : req.Model),
                ["messages"] = messages,
                ["max_tokens"] = req.MaxTokens > 0 ? req.MaxTokens : AnthropicClient.DefaultMaxTokens,
                ["stream"] = false
            };
            if (!string.IsNullOrEmpty(req.Effort)) body["reasoning_effort"] = req.Effort;
            if (req.Schema != null)
                body["response_format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JObject { ["name"] = (req.Task ?? "output").Replace("-", "_"), ["strict"] = true, ["schema"] = req.Schema }
                };
            return body.ToString(Formatting.None);
        }

        public LlmResponse Complete(LlmRequest request)
        {
            var payload = Encoding.UTF8.GetBytes(BuildBody(_model, request));
            var http = (HttpWebRequest)WebRequest.Create(_baseUrl + "/v1/chat/completions");
            http.AllowAutoRedirect = false;
            http.Method = "POST";
            http.ContentType = "application/json";
            http.Timeout = _timeoutMs;
            http.ReadWriteTimeout = _timeoutMs;
            http.UserAgent = "FlareAI/0.1 (MadCap Flare plugin)";
            http.Headers["x-bf-vk"] = _virtualKey;
            http.ContentLength = payload.Length;

            LlmResponse result;
            try
            {
                using (var s = http.GetRequestStream()) s.Write(payload, 0, payload.Length);
                using (var resp = (HttpWebResponse)http.GetResponse())
                {
                    if (LlmErrors.IsRedirect((int)resp.StatusCode)) throw LlmErrors.FromRedirect((int)resp.StatusCode, true);
                    result = Parse(ReadAll(resp));
                }
            }
            catch (JsonException jex) { throw LlmErrors.FromMalformed(jex, true); }
            catch (IOException ioex) { throw LlmErrors.FromDropped(ioex, true, _baseUrl); }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp == null) throw LlmErrors.FromNetwork(wex, true, _baseUrl);
                if (LlmErrors.IsRedirect((int)resp.StatusCode)) throw LlmErrors.FromRedirect((int)resp.StatusCode, true);
                var text = ReadAll(resp);
                var detail = text;
                try { detail = JObject.Parse(text)["error"]["message"].ToString(); } catch (Exception) { }
                throw LlmErrors.FromHttp((int)resp.StatusCode, detail, true, _virtualKey);
            }
            // OpenAI finish reasons: "length" = cut off, "content_filter" = refused.
            if (result.StopReason == "length") result.StopReason = "max_tokens";
            if (result.StopReason == "content_filter") result.StopReason = "refusal";
            return AnthropicClient.Check(result, request);
        }

        public static LlmResponse Parse(string json)
        {
            var o = JObject.Parse(json);
            var choice = o["choices"][0];
            var msg = choice["message"];
            var usage = o["usage"] as JObject;
            return new LlmResponse
            {
                Text = (string)msg["content"] ?? "",
                Model = (string)o["model"],
                StopReason = (string)choice["finish_reason"],
                InputTokens = usage != null ? (int?)usage["prompt_tokens"] ?? 0 : 0,
                OutputTokens = usage != null ? (int?)usage["completion_tokens"] ?? 0 : 0
            };
        }

        private static string ReadAll(HttpWebResponse resp)
        {
            using (var s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }
    }
}
