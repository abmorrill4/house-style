using System.Collections.Generic;
using System.Linq;
using FlareAI.Core.Llm;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>The history goes before the new message, the last earlier message carries a cache breakpoint, and there are never more than four.</summary>
    public class ConversationBodyTests
    {
        private static LlmRequest Request(string cache = "5m")
        {
            return new LlmRequest
            {
                Task = "converse", System = "system", SystemPacks = "packs", SystemTerms = "terms", User = "second question", CacheTtl = cache,
                History = new List<LlmTurn> { new LlmTurn("user", "first question"), new LlmTurn("assistant", "{\"kind\":\"answer\"}") }
            };
        }

        private static int Breakpoints(JToken t) { return t.SelectTokens("$..cache_control").Count(); }

        [Fact]
        public void Anthropic_SendsTheHistoryFirst_AndCachesTheLastEarlierMessage()
        {
            var body = JObject.Parse(AnthropicClient.BuildBody("claude-sonnet-5-5", Request()));
            var messages = (JArray)body["messages"];
            Assert.Equal(3, messages.Count);
            Assert.Equal(new[] { "user", "assistant", "user" }, messages.Select(m => (string)m["role"]).ToArray());
            Assert.Equal("first question", (string)messages[0]["content"]);                    // earlier messages are plain text
            Assert.Equal("ephemeral", (string)messages[1]["content"][0]["cache_control"]["type"]);
            Assert.Equal("second question", (string)messages[2]["content"]);
            Assert.True(Breakpoints(body) <= 4, "more than four cache breakpoints: " + Breakpoints(body));
        }

        [Fact]
        public void Anthropic_WithNoHistory_IsAsBefore_AndNothingIsCachedWhenCachingIsOff()
        {
            var plain = new LlmRequest { Task = "review", System = "s", User = "u" };
            var one = (JArray)JObject.Parse(AnthropicClient.BuildBody("claude-sonnet-5-5", plain))["messages"];
            Assert.Equal(1, one.Count);
            var off = JObject.Parse(AnthropicClient.BuildBody("claude-sonnet-5-5", Request("none")));
            Assert.Equal(0, Breakpoints(off["messages"]));
        }

        [Fact]
        public void Gateway_SendsTheHistoryBetweenTheSystemPromptAndTheNewMessage()
        {
            var body = JObject.Parse(BifrostClient.BuildBody("claude-sonnet-5-5", Request()));
            var messages = (JArray)body["messages"];
            Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(m => (string)m["role"]).ToArray());
            Assert.Equal("first question", (string)messages[1]["content"]);
            Assert.Equal("second question", (string)messages[3]["content"]);
        }
    }
}
