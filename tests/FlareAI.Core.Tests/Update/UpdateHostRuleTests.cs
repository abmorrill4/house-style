using System;
using FlareAI.Core.Settings;
using FlareAI.Core.Update;
using Xunit;

namespace FlareAI.Core.Tests.Update
{
    public class UpdateHostRuleTests
    {
        [Theory]
        [InlineData("https://github.com/abmorrill4/house-style/releases/download/v1/x.zip", true)]
        [InlineData("https://api.github.com/repos/abmorrill4/house-style/releases/latest", true)]
        [InlineData("https://release-assets.githubusercontent.com/x", true)]
        [InlineData("https://objects.githubusercontent.com/x", true)]
        [InlineData("http://localhost:8765/x", true)]
        [InlineData("http://127.0.0.1:8765/x", true)]
        [InlineData("https://example.com/z.zip", false)]
        [InlineData("https://github.com.example.com/z.zip", false)]
        [InlineData("https://evilgithubusercontent.com/z.zip", false)]
        [InlineData("http://github.com/z.zip", false)]
        public void UpdateFiles_ComeOnlyFromGitHubOrThisComputer(string url, bool allowed)
        {
            Assert.Equal(allowed, UpdateCheck.IsAllowedUpdateUrl(url));
        }

        [Fact]
        public void ReleaseWithAssetsOnAnotherHost_IsNotOffered()
        {
            var json = "{\"tag_name\":\"v0.9.0\",\"assets\":[{\"name\":\"HouseStyle-0.9.0.zip\",\"browser_download_url\":\"https://example.com/z.zip\"},{\"name\":\"checksums.txt\",\"browser_download_url\":\"https://example.com/c\"}]}";
            Assert.Null(UpdateCheck.Parse(json));
            var ok = json.Replace("https://example.com/", "https://github.com/a/b/releases/download/v0.9.0/");
            Assert.NotNull(UpdateCheck.Parse(ok));
        }

        [Theory]
        [InlineData("https://evil.example.com/releases/latest", false)]
        [InlineData("http://evil.example.com/x", false)]
        [InlineData("not a url", false)]
        [InlineData("", false)]
        [InlineData("http://localhost:8765/x", true)]
        [InlineData("http://127.0.0.1:8765/x", true)]
        public void EnvironmentOverrides_AreHonouredOnlyOnThisComputer(string value, bool honoured)
        {
            Assert.Equal(honoured ? value : null, UpdateCheck.EnvironmentOverride(value));
        }

        [Fact]
        public void RemoteFeedbackOverride_IsIgnored()
        {
            var old = Environment.GetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar, "https://evil.example.com");
                Assert.Equal(PluginSettings.DefaultFeedbackUrl, new PluginSettings().EffectiveFeedbackUrl);
            }
            finally { Environment.SetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar, old); }
        }
    }
}
