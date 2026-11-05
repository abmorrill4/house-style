using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Llm
{
    /// <summary>A single-turn completion request. Provider-agnostic so Bifrost can replace Anthropic-direct.</summary>
    public sealed class LlmRequest
    {
        /// <summary>Which task this is ("review", "rewrite", …): selects the route and labels logs and usage.</summary>
        public string Task { get; set; }
        public string System { get; set; }
        /// <summary>
        /// Optional second part of the system prompt: the rule packs for this unit's content. Sent as its own cached block
        /// after <see cref="System"/>, so the always-on part stays cached across units whose packs differ.
        /// </summary>
        public string SystemPacks { get; set; }
        /// <summary>Optional last part of the system prompt, particular to this text (rules for words it uses): not cached.</summary>
        public string SystemTerms { get; set; }
        public string User { get; set; }
        /// <summary>
        /// Earlier turns of a conversation, oldest first, sent before <see cref="User"/>. The last one is cached, so each new turn pays
        /// for the whole history once. Null or empty for a single-turn request.
        /// </summary>
        public List<LlmTurn> History { get; set; }
        /// <summary>A safety ceiling, not the control: output size is governed by <see cref="Schema"/>. 0 = route default.</summary>
        public int MaxTokens { get; set; }
        /// <summary>Model for this request; null = the client's default.</summary>
        public string Model { get; set; }
        /// <summary>low / medium / high / xhigh / max; null = the model's default.</summary>
        public string Effort { get; set; }
        /// <summary>The output contract: a JSON schema the response must match (structured outputs). Null = free text.</summary>
        public JObject Schema { get; set; }
        /// <summary>
        /// How long the system prompt's cache entry lives: "1h" (written at 2x input), "5m" (1.25x) or "none" (not cached).
        /// Null = "1h". Chosen per task by <see cref="Routing.CacheFor"/>.
        /// </summary>
        public string CacheTtl { get; set; }
        /// <summary>Images sent before the text (vision): media type ("image/png") and base64 data.</summary>
        public List<LlmImage> Images { get; set; }
    }

    /// <summary>One earlier message of a conversation: "user" or "assistant", and its text (an assistant turn is the reply JSON as sent back).</summary>
    public sealed class LlmTurn
    {
        public string Role { get; set; }
        public string Text { get; set; }
        public LlmTurn(string role, string text) { Role = role; Text = text; }
    }

    public sealed class LlmImage
    {
        public string MediaType { get; set; }
        public string Base64 { get; set; }
    }

    public sealed class LlmResponse
    {
        public string Text { get; set; }
        public string Model { get; set; }
        public string StopReason { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int CacheWriteTokens { get; set; }
        /// <summary>The part of <see cref="CacheWriteTokens"/> written to the 1-hour cache (billed at 2x input, 5-minute writes at 1.25x).</summary>
        public int CacheWrite1hTokens { get; set; }
        /// <summary>The response parsed as JSON when the request had a schema.</summary>
        public JObject Json { get; set; }
    }

    /// <summary>Why a request failed, so callers can react (retry with more room, tell the author, fall back).</summary>
    public enum LlmFailure { Http, Network, Truncated, Refusal, Contract, Auth, ModelDenied, Budget }

    public sealed class LlmException : Exception
    {
        public int StatusCode { get; private set; }
        public LlmFailure Failure { get; private set; }
        public LlmException(string message, int statusCode = 0, Exception inner = null, LlmFailure failure = LlmFailure.Http) : base(message, inner)
        {
            StatusCode = statusCode;
            Failure = failure;
        }
    }

    /// <summary>Synchronous by design (net40, no async). Callers run it on a background thread.</summary>
    public interface ILlmClient
    {
        LlmResponse Complete(LlmRequest request);
    }

    /// <summary>Stands in when no client can be made (no key): lint-only work runs, and any AI call fails with the reason.</summary>
    public sealed class UnavailableClient : ILlmClient
    {
        private readonly string _reason;
        public UnavailableClient(string reason) { _reason = reason; }
        public LlmResponse Complete(LlmRequest request) { throw new LlmException(_reason, 0, null, LlmFailure.Auth); }
    }
}
