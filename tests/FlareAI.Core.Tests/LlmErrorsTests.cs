using System;
using FlareAI.Core.Llm;
using FlareAI.Core.Settings;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class LlmErrorsTests
    {
        [Theory]
        [InlineData("https://gw.example.com", "https://gw.example.com")]
        [InlineData("  https://gw.example.com/  ", "https://gw.example.com")]
        [InlineData("https://gw.example.com/v1", "https://gw.example.com")]
        [InlineData("https://gw.example.com/v1/", "https://gw.example.com")]
        [InlineData("https://gw.example.com/V1", "https://gw.example.com")]
        [InlineData("gw.example.com", "https://gw.example.com")]
        [InlineData("http://gw.example.com/", "https://gw.example.com")]
        [InlineData("http://gw.example.com:8080/v1", "https://gw.example.com:8080")]
        [InlineData("https://gw.example.com/bifrost/v1/", "https://gw.example.com/bifrost")]
        [InlineData("http://localhost:8080/", "http://localhost:8080")]
        [InlineData("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080")]
        [InlineData("localhost:8080", "https://localhost:8080")]
        [InlineData("https://api.anthropic.com", "https://api.anthropic.com")]
        public void NormalizesBaseUrl(string input, string expected)
        {
            string url, err;
            Assert.True(LlmErrors.TryNormalizeBaseUrl(input, out url, out err), err);
            Assert.Equal(expected, url);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("https://gw example.com")]
        [InlineData("ftp://gw.example.com")]
        [InlineData("https://")]
        public void RejectsUnusableBaseUrl(string input)
        {
            string url, err;
            Assert.False(LlmErrors.TryNormalizeBaseUrl(input, out url, out err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void KeyRejected_IsAuth_AndNeverEchoesTheKey()
        {
            var ex = LlmErrors.FromHttp(401, "bad key vk-secret-123", true, "vk-secret-123");
            Assert.Equal(LlmFailure.Auth, ex.Failure);
            Assert.Contains("pasted in full", ex.Message);
            Assert.DoesNotContain("vk-secret-123", ex.Message);
        }

        [Theory]
        [InlineData(403, "forbidden")]
        [InlineData(400, "no keys found for provider anthropic")]
        [InlineData(404, "model is not allowed for this virtual key")]
        public void ModelNotAllowed_MentionsAllowList(int status, string detail)
        {
            var ex = LlmErrors.FromHttp(status, detail, true, "k");
            Assert.Equal(LlmFailure.ModelDenied, ex.Failure);
            Assert.Contains("allow-list", ex.Message);
        }

        [Theory]
        [InlineData(429, "slow down")]
        [InlineData(402, "")]
        [InlineData(400, "virtual key budget exceeded")]
        public void BudgetOrRate_IsBudget(int status, string detail)
        {
            Assert.Equal(LlmFailure.Budget, LlmErrors.FromHttp(status, detail, true, "k").Failure);
        }

        [Fact]
        public void OtherStatus_KeepsTheCode()
        {
            var ex = LlmErrors.FromHttp(500, "oops", true, "k");
            Assert.Equal(LlmFailure.Http, ex.Failure);
            Assert.Contains("500", ex.Message);
        }

        [Fact]
        public void FromRedirect_AsksForTheFinalAddress_AndKeepsTheStatus()
        {
            var gw = LlmErrors.FromRedirect(302, true);
            Assert.Contains("redirects", gw.Message);
            Assert.Contains("final address", gw.Message);
            Assert.Equal(302, gw.StatusCode);
            Assert.DoesNotContain("vk-secret-1", gw.Message);
            var an = LlmErrors.FromRedirect(301, false);
            Assert.Contains("redirects", an.Message);
            Assert.Contains("final address", an.Message);
            Assert.Equal(301, an.StatusCode);
            Assert.DoesNotContain("sk-secret-1", an.Message);
        }

        [Fact]
        public void IsRedirect_IsTrueOnlyFor3xx()
        {
            foreach (var s in new[] { 301, 302, 307, 308 }) Assert.True(LlmErrors.IsRedirect(s));
            foreach (var s in new[] { 200, 299, 400, 401 }) Assert.False(LlmErrors.IsRedirect(s));
        }

        private sealed class Throwing : ILlmClient
        {
            private readonly Exception _ex;
            public Throwing(Exception ex) { _ex = ex; }
            public LlmResponse Complete(LlmRequest request) { throw _ex; }
        }

        [Fact]
        public void ConnectionTest_NeedsAKey_AndNeverEchoesIt()
        {
            Assert.Equal(ConnectionOutcome.Other, ConnectionTest.Run(LlmProvider.Bifrost, "https://gw.example.com", "", "m", 30).Outcome);
            var r = ConnectionTest.Run(LlmProvider.Bifrost, "https://gw.example.com", "vk-1", "m", 30, new Throwing(new InvalidOperationException("boom vk-1")));
            Assert.DoesNotContain("vk-1", r.Message);
        }

        [Fact]
        public void ConnectionTest_BadUrlIsReportedBeforeAnyRequest()
        {
            var r = ConnectionTest.Run(LlmProvider.Bifrost, "ftp://x", "vk-1", "m", 30);
            Assert.False(r.Ok);
        }

        [Fact]
        public void Clients_NormalizeTheBaseUrl_AndRejectGarbage()
        {
            Assert.NotNull(new BifrostClient("gw.example.com/v1/", "vk", "claude-sonnet-5-5", 30));
            Assert.Throws<LlmException>(() => new BifrostClient("ftp://x", "vk", "claude-sonnet-5-5", 30));
        }
    }
}
