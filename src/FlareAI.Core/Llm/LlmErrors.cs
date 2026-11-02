using System;
using System.IO;
using System.Net;
using Newtonsoft.Json;

namespace FlareAI.Core.Llm
{
    /// <summary>
    /// The one place AI failures become messages an author can act on, and where a typed Base URL is cleaned up.
    /// Messages never contain the key.
    /// </summary>
    public static class LlmErrors
    {
        /// <summary>
        /// Cleans a typed address: trims, adds https:// when there is no scheme, upgrades http:// to https:// except for a
        /// local gateway, and drops a trailing slash or /v1 (the clients add their own path). False with a message when
        /// it can't be an address.
        /// </summary>
        public static bool TryNormalizeBaseUrl(string input, out string url, out string error)
        {
            url = null; error = null;
            var s = (input ?? "").Trim();
            if (s.Length == 0) { error = "Enter the gateway address, for example https://gateway.example.com."; return false; }
            if (s.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0) { error = "The address contains spaces. Paste it again, for example https://gateway.example.com."; return false; }
            if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "https://" + s;
            Uri uri;
            if (!Uri.TryCreate(s, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Host))
            {
                error = "That doesn't look like a web address. Use the form https://gateway.example.com.";
                return false;
            }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                error = "The address must start with https:// (or http:// for a gateway on this computer).";
                return false;
            }
            var scheme = uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback ? Uri.UriSchemeHttps : uri.Scheme;
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 3).TrimEnd('/');
            // An explicit port survives, except that upgrading http to https drops http's own default (80).
            var port = uri.IsDefaultPort || uri.Scheme != scheme && uri.Port == 80 ? "" : ":" + uri.Port;
            var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host.Trim('[', ']') + "]" : uri.Host;
            url = scheme + "://" + host + port + path;
            return true;
        }

        /// <summary>An HTTP error from the provider, as a typed exception with a plain-language message.</summary>
        public static LlmException FromHttp(int status, string detail, bool gateway, string key)
        {
            var d = (detail ?? "").ToLowerInvariant();   // classify on the raw text; only the shown text is scrubbed
            detail = Scrub(detail, key);
            var who = gateway ? "The gateway" : "Anthropic";
            var keyName = gateway ? "gateway key" : "API key";
            if (status == 401)
                return new LlmException(who + " rejected the key: check the " + keyName + " in Settings was pasted in full.", status, null, LlmFailure.Auth);
            if (d.Contains("no keys found") || d.Contains("not allowed") || d.Contains("not permitted") || (status == 403 && !d.Contains("budget")))
                return new LlmException(gateway
                    ? "The gateway doesn't allow this model for your key. Ask your admin to add the Claude models (Haiku, Sonnet, Opus 5.5) to the virtual key's allow-list."
                    : "Your Anthropic key isn't allowed to use this model. Pick another model in Settings or check the key's permissions.", status, null, LlmFailure.ModelDenied);
            if (status == 402 || status == 429 || d.Contains("budget") || d.Contains("rate limit") || d.Contains("quota") || d.Contains("credit balance"))
                return new LlmException(gateway
                    ? "The gateway says this key's budget or rate limit is used up. Wait a while, or ask your admin to raise the limit."
                    : "Anthropic says the rate limit or credit is used up. Wait a moment, or check billing for this key.", status, null, LlmFailure.Budget);
            if (status == 404 && gateway)
                return new LlmException("Nothing answered at that gateway address. Check the Base URL in Settings.", status);
            return new LlmException("HTTP " + status + ": " + Shorten(detail), status);
        }

        /// <summary>True for a 3xx answer. Requests that carry a key never follow one.</summary>
        public static bool IsRedirect(int status) { return status >= 300 && status < 400; }

        /// <summary>The address answered with a redirect. The message has neither the key nor the Location header.</summary>
        public static LlmException FromRedirect(int status, bool gateway)
        {
            return new LlmException((gateway ? "The gateway" : "Anthropic") + " address redirects. Change the Base URL in Settings to the final address.", status);
        }

        /// <summary>A failure with no HTTP response: DNS, connection, TLS, timeout.</summary>
        public static LlmException FromNetwork(WebException ex, bool gateway, string baseUrl)
        {
            var where = gateway ? "the gateway" : "Anthropic";
            string why;
            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure: why = "its name wasn't found (check the spelling of the address)"; break;
                case WebExceptionStatus.Timeout: why = "it didn't answer in time (check the address, your network or VPN, or raise the timeout in Settings)"; break;
                case WebExceptionStatus.SecureChannelFailure:
                case WebExceptionStatus.TrustFailure: why = "the secure (TLS) connection failed (its certificate may not be trusted, or the address needs http:// for a gateway on this computer)"; break;
                case WebExceptionStatus.ConnectFailure: why = "the connection was refused (check the address and port, and your network or VPN)"; break;
                default: why = ex.Message; break;
            }
            return new LlmException("Can't reach " + where + (string.IsNullOrEmpty(baseUrl) ? "" : " at " + baseUrl) + ": " + why + ".", 0, ex, LlmFailure.Network);
        }

        /// <summary>A 200 answer whose body isn't the JSON the provider promised (a proxy's HTML page, a garbled reply).</summary>
        public static LlmException FromMalformed(JsonException ex, bool gateway)
        {
            var who = gateway ? "The gateway" : "Anthropic";
            return new LlmException(who + " sent back an answer that couldn't be read" + (gateway ? " (a proxy or firewall may be in the way; check the Base URL in Settings)" : "") + ". Try again.", 0, ex, LlmFailure.Contract);
        }

        /// <summary>The connection dropped while the answer was still arriving.</summary>
        public static LlmException FromDropped(IOException ex, bool gateway, string baseUrl)
        {
            var where = gateway ? "the gateway" : "Anthropic";
            return new LlmException("The connection to " + where + (string.IsNullOrEmpty(baseUrl) ? "" : " at " + baseUrl) + " dropped before the answer finished. Check your network or VPN and try again.", 0, ex, LlmFailure.Network);
        }

        public static string Scrub(string text, string key)
        {
            return string.IsNullOrEmpty(key) || text == null ? text : text.Replace(key, "***");
        }

        private static string Shorten(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }
}
