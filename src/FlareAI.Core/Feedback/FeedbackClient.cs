using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Feedback
{
    /// <summary>Why a report didn't arrive.</summary>
    public enum FeedbackFailure
    {
        None = 0,
        /// <summary>The service couldn't be reached (offline, proxy, timeout).</summary>
        Network,
        /// <summary>Too many reports from this computer for now (429).</summary>
        RateLimited,
        /// <summary>The service refused the report as it is (400, 413, 415): a bug in House Style, not worth retrying.</summary>
        Rejected,
        /// <summary>The service had a problem or couldn't reach GitHub (5xx).</summary>
        Server
    }

    /// <summary>What happened to one report.</summary>
    public sealed class FeedbackResult
    {
        public bool Ok { get; set; }
        /// <summary>The service's reference for it ("r-1a2b3c4d"), when it gives one.</summary>
        public string Reference { get; set; }
        /// <summary>The same error was already reported (the service added to the existing report).</summary>
        public bool Duplicate { get; set; }
        public FeedbackFailure Failure { get; set; }
        public int StatusCode { get; set; }
        /// <summary>The service's own plain message, or the network error (for the log, not the writer).</summary>
        public string Error { get; set; }
        public int? RetryAfterSeconds { get; set; }

        /// <summary>True when saving the report as a zip is the way forward: the service can't take it now.</summary>
        public bool ShouldSaveInstead { get { return !Ok && (Failure == FeedbackFailure.Network || Failure == FeedbackFailure.Server || Failure == FeedbackFailure.RateLimited); } }

        public override string ToString()
        {
            return Ok ? "sent" + (Reference == null ? "" : " " + Reference) + (Duplicate ? " (duplicate)" : "")
                      : Failure + (StatusCode > 0 ? " " + StatusCode : "") + (Error == null ? "" : ": " + Error);
        }
    }

    /// <summary>
    /// Sends reports to the House Style feedback service (tools/feedback-worker on the main branch): <c>POST /v1/report</c>
    /// and <c>POST /v1/false-positive</c>, JSON with the <c>X-House-Style: 1</c> header, TLS 1.2, a 10 second timeout.
    /// Large bodies are gzip-compressed. Never throws: every outcome is a <see cref="FeedbackResult"/>.
    /// Blocking, so callers run it on a background thread.
    /// </summary>
    public sealed class FeedbackClient
    {
        /// <summary>Bodies bigger than this are gzip-compressed (the log tail makes most error reports bigger).</summary>
        public const int GzipThreshold = 8 * 1024;

        public FeedbackClient(string baseUrl)
        {
            BaseUrl = (baseUrl ?? FlareAI.Core.Settings.PluginSettings.DefaultFeedbackUrl).TrimEnd('/');
            TimeoutMs = 10000;
            Gzip = true;
        }

        public string BaseUrl { get; private set; }
        public int TimeoutMs { get; set; }
        public bool Gzip { get; set; }

        public FeedbackResult SendReport(ReportPayload report) { return Post("/v1/report", ReportBuilder.ToJson(report)); }

        public FeedbackResult SendFalsePositive(FalsePositivePayload report) { return Post("/v1/false-positive", ReportBuilder.ToJson(report)); }

        /// <summary>Posts a JSON body (exactly what the writer saw in Details) to a path of the service.</summary>
        public FeedbackResult Post(string path, string json)
        {
            var url = BaseUrl + path;
            if (!FlareAI.Core.Update.UpdateCheck.IsAllowedUrl(url))
                return new FeedbackResult { Failure = FeedbackFailure.Network, Error = "Refusing a non-HTTPS address: " + url };
            try
            {
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }   // TLS 1.2 on net40
                var body = Encoding.UTF8.GetBytes(json);
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "application/json";
                req.UserAgent = "HouseStyle-Feedback";
                req.Headers["X-House-Style"] = "1";
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                req.AllowAutoRedirect = false;   // a POST must not be replayed to another address
                if (!FlareAI.Core.Update.LoopbackProxy.Apply(req))   // a test server on this computer: no proxy
                {
                    try
                    {
                        var proxy = WebRequest.GetSystemWebProxy();
                        proxy.Credentials = CredentialCache.DefaultCredentials;
                        req.Proxy = proxy;
                    }
                    catch (Exception) { }
                }
                if (Gzip && body.Length > GzipThreshold)
                {
                    body = Compress(body);
                    req.Headers["Content-Encoding"] = "gzip";
                }
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return Read(resp);
            }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp == null) return new FeedbackResult { Failure = FeedbackFailure.Network, Error = wex.Status + ": " + wex.Message };
                using (resp) return Read(resp);
            }
            catch (Exception ex)
            {
                return new FeedbackResult { Failure = FeedbackFailure.Network, Error = ex.GetType().Name + ": " + ex.Message };
            }
        }

        private static byte[] Compress(byte[] body)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(body, 0, body.Length);
                return ms.ToArray();
            }
        }

        private static FeedbackResult Read(HttpWebResponse resp)
        {
            var status = (int)resp.StatusCode;
            string text = null;
            try { using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd(); } catch (Exception) { }
            JObject o = null;
            try { if (!string.IsNullOrEmpty(text)) o = JObject.Parse(text); } catch (Exception) { }
            var result = new FeedbackResult { StatusCode = status };
            if (status >= 200 && status < 300 && o != null && (bool?)o["ok"] == true)
            {
                result.Ok = true;
                result.Reference = (string)o["reference"];
                result.Duplicate = (bool?)o["duplicate"] ?? false;
                return result;
            }
            result.Error = o != null ? (string)o["error"] : (text == null ? null : text.Length > 300 ? text.Substring(0, 300) : text);
            int retry;
            var header = resp.Headers["Retry-After"];
            if (o != null && o["retry_after"] != null && o["retry_after"].Type == JTokenType.Integer) result.RetryAfterSeconds = (int)o["retry_after"];
            else if (!string.IsNullOrEmpty(header) && int.TryParse(header, out retry)) result.RetryAfterSeconds = retry;
            if (status == 429) result.Failure = FeedbackFailure.RateLimited;
            else if (status >= 500) result.Failure = FeedbackFailure.Server;
            else if (status >= 200 && status < 300) { result.Failure = FeedbackFailure.Server; result.Error = result.Error ?? "The answer wasn't a report reference."; }
            else result.Failure = FeedbackFailure.Rejected;
            return result;
        }
    }
}
