using FlareAI.Core.Llm;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class RedirectTests
    {
        [Fact]
        public void AnthropicClient_DoesNotFollowARedirect_AndDoesNotLeakTheKey()
        {
            using (var target = new MockLlmServer())
            using (var server = new MockLlmServer())
            {
                server.Enqueue(MockReply.Redirect(target.BaseUrl + "/v1/messages"));
                var client = new AnthropicClient(server.BaseUrl, "sk-secret-1", "claude-sonnet-5-5", 30);
                var ex = Assert.Throws<LlmException>(() => client.Complete(new LlmRequest { User = "hi" }));
                Assert.Contains("redirects", ex.Message);
                Assert.DoesNotContain("sk-secret-1", ex.Message);
                Assert.Equal(1, server.Received.Count);
                Assert.Empty(target.Received);
            }
        }

        [Fact]
        public void BifrostClient_DoesNotFollowARedirect_AndDoesNotLeakTheKey()
        {
            using (var target = new MockLlmServer())
            using (var server = new MockLlmServer())
            {
                server.Enqueue(MockReply.Redirect(target.BaseUrl + "/v1/chat/completions"));
                var client = new BifrostClient(server.BaseUrl, "vk-secret-1", "claude-sonnet-5-5", 30);
                var ex = Assert.Throws<LlmException>(() => client.Complete(new LlmRequest { User = "hi" }));
                Assert.Contains("redirects", ex.Message);
                Assert.DoesNotContain("vk-secret-1", ex.Message);
                Assert.Equal(1, server.Received.Count);
                Assert.Empty(target.Received);
            }
        }

        [Fact]
        public void ModelList_DoesNotFollowARedirect_AndDoesNotLeakTheKey()
        {
            using (var target = new MockLlmServer())
            using (var server = new MockLlmServer())
            {
                server.Enqueue(MockReply.Redirect(target.BaseUrl + "/v1/models"));
                var ex = Assert.Throws<LlmException>(() => AnthropicModels.List(server.BaseUrl, "sk-secret-1", 30));
                Assert.Contains("redirects", ex.Message);
                Assert.DoesNotContain("sk-secret-1", ex.Message);
                Assert.Equal(1, server.Received.Count);
                Assert.Empty(target.Received);
            }
        }

        [Fact]
        public void AnthropicClient_StillWorksOnLoopbackHttp()
        {
            using (var server = new MockLlmServer())
            {
                server.Enqueue(MockReply.Ok("hello"));
                var resp = new AnthropicClient(server.BaseUrl, "sk-secret-1", "claude-sonnet-5-5", 30).Complete(new LlmRequest { User = "hi" });
                Assert.Equal("hello", resp.Text);
            }
        }
    }
}
