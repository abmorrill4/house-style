using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Llm
{
    /// <summary>One model the key can use.</summary>
    public sealed class ModelInfo
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        /// <summary>"Claude Sonnet 5.5 — claude-sonnet-5-5"</summary>
        public override string ToString() { return string.IsNullOrEmpty(DisplayName) ? Id : DisplayName + " — " + Id; }
    }

    /// <summary>GET {baseUrl}/v1/models: the models a key can use, so Settings can offer a pick list.</summary>
    public static class AnthropicModels
    {
        /// <summary>Fetches one page of JSON for a URL; injectable so paging and parsing are testable without a network.</summary>
        public delegate string Fetcher(string url, string apiKey, int timeoutSeconds);

        public static IList<ModelInfo> List(string baseUrl, string apiKey, int timeoutSeconds)
        {
            return List(baseUrl, apiKey, timeoutSeconds, Http);
        }

        public static IList<ModelInfo> List(string baseUrl, string apiKey, int timeoutSeconds, Fetcher fetch)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new LlmException("No API key yet.");
            string root, err;
            if (!LlmErrors.TryNormalizeBaseUrl(string.IsNullOrEmpty((baseUrl ?? "").Trim()) ? "https://api.anthropic.com" : baseUrl, out root, out err)) throw new LlmException(err);
            var all = new List<ModelInfo>();
            string after = null;
            for (var page = 0; page < 20; page++)   // a hard stop against a server that never ends
            {
                var url = root + "/v1/models?limit=1000" + (after == null ? "" : "&after_id=" + Uri.EscapeDataString(after));
                string json;
                try { json = fetch(url, apiKey, timeoutSeconds); }
                catch (LlmException) { throw; }
                catch (Exception ex) { throw new LlmException("Couldn't reach Anthropic: " + Scrub(ex.Message, apiKey), 0, null, LlmFailure.Network); }
                bool more;
                after = Parse(json, all, out more);
                if (!more || string.IsNullOrEmpty(after)) break;
            }
            return all;
        }

        /// <summary>Adds the page's models to <paramref name="into"/>; returns the cursor for the next page.</summary>
        public static string Parse(string json, IList<ModelInfo> into, out bool hasMore)
        {
            JObject o;
            try { o = JObject.Parse(json); }
            catch (Exception) { throw new LlmException("Anthropic's model list wasn't in the expected format."); }
            foreach (var m in o["data"] as JArray ?? new JArray())
            {
                var id = (string)m["id"];
                if (string.IsNullOrEmpty(id)) continue;
                into.Add(new ModelInfo { Id = id, DisplayName = (string)m["display_name"] });
            }
            hasMore = (bool?)o["has_more"] ?? false;
            return (string)o["last_id"];
        }

        private static string Http(string url, string apiKey, int timeoutSeconds)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
            var http = (HttpWebRequest)WebRequest.Create(url);
            http.AllowAutoRedirect = false;
            http.Method = "GET";
            http.Accept = "application/json";
            http.Timeout = http.ReadWriteTimeout = Math.Max(10, timeoutSeconds) * 1000;
            http.UserAgent = "FlareAI/0.1 (MadCap Flare plugin)";
            http.Headers["x-api-key"] = apiKey;
            http.Headers["anthropic-version"] = AnthropicClient.ApiVersion;
            try
            {
                using (var r = (HttpWebResponse)http.GetResponse())
                {
                    if (LlmErrors.IsRedirect((int)r.StatusCode)) throw LlmErrors.FromRedirect((int)r.StatusCode, false);
                    using (var s = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        return s.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                var r = wex.Response as HttpWebResponse;
                if (r == null) throw new LlmException("Couldn't reach Anthropic. Check your connection.", 0, null, LlmFailure.Network);
                var code = (int)r.StatusCode;
                if (LlmErrors.IsRedirect(code)) throw LlmErrors.FromRedirect(code, false);
                if (code == 401 || code == 403) throw new LlmException("The key was rejected.", code);
                if (code == 404) throw new LlmException("This address doesn't offer a model list.", code);
                if (code == 429) throw new LlmException("Anthropic is busy. Try again in a moment.", code);
                throw new LlmException("Anthropic couldn't list models (error " + code + ").", code);
            }
        }

        private static string Scrub(string message, string key)
        {
            return string.IsNullOrEmpty(key) || message == null ? message : message.Replace(key, "***");
        }
    }
}
