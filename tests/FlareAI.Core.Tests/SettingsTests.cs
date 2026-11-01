using FlareAI.Core.Settings;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class SettingsTests
    {
        [Fact]
        public void ApiKey_RoundTripsThroughDpapi_AndIsNotStoredInPlaintext()
        {
            var s = new PluginSettings();
            s.SetApiKey("sk-ant-test-123");
            Assert.True(s.HasStoredKey);
            Assert.DoesNotContain("sk-ant-test-123", s.ProtectedApiKey);
            Assert.Equal("sk-ant-test-123", s.ResolveApiKey());
        }

        [Fact]
        public void ResolveApiKey_UsesTheEnvironmentKeyForAnthropicDirectOnly()
        {
            var old = System.Environment.GetEnvironmentVariable(PluginSettings.EnvKeyName);
            var oldGateway = System.Environment.GetEnvironmentVariable(PluginSettings.GatewayEnvKeyName);
            System.Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, "sk-env");
            System.Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, "gw-env");
            try
            {
                var s = new PluginSettings();
                s.Provider = LlmProvider.AnthropicDirect;
                Assert.Equal("sk-env", s.ResolveApiKey());
                s.Provider = LlmProvider.Bifrost;
                Assert.Equal("gw-env", s.ResolveApiKey());
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, old);
                System.Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, oldGateway);
            }
        }

        [Fact]
        public void ResolveApiKey_IgnoresTheOtherProvidersVariable()
        {
            var old = System.Environment.GetEnvironmentVariable(PluginSettings.EnvKeyName);
            var oldGateway = System.Environment.GetEnvironmentVariable(PluginSettings.GatewayEnvKeyName);
            try
            {
                System.Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, "sk-env");
                System.Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, null);
                var s = new PluginSettings();
                s.Provider = LlmProvider.Bifrost;
                Assert.Null(s.ResolveApiKey());

                System.Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, null);
                System.Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, "gw-env");
                s.Provider = LlmProvider.AnthropicDirect;
                Assert.Null(s.ResolveApiKey());
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, old);
                System.Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, oldGateway);
            }
        }

        [Fact]
        public void AutoUpdate_IsOnByDefault_TheSettingDecides_UnlessThePolicyFileDoes()
        {
            Assert.True(new PluginSettings().AutoUpdate);
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flareai-policy-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                // No policy file: the Settings check box decides, and it can be changed.
                var off = FlareAI.Core.Update.UpdatePolicy.Resolve(false, dir);
                Assert.False(off.Enabled);
                Assert.False(off.FromPolicyFile);
                Assert.True(FlareAI.Core.Update.UpdatePolicy.Resolve(true, dir).Enabled);
                // A policy file: it decides, and the check box is read-only ("Set by your IT department").
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, FlareAI.Core.Update.UpdateLayout.PolicyFileName), "{\"autoUpdate\": false}");
                var policy = FlareAI.Core.Update.UpdatePolicy.Resolve(true, dir);
                Assert.False(policy.Enabled);
                Assert.True(policy.FromPolicyFile);
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        [Fact]
        public void LintChecks_DefaultsToRulesAndAi_AndRoundTrips()
        {
            Assert.Equal("rules-ai", new PluginSettings().LintChecks);
            var s = new PluginSettings { LintChecks = "rules" };
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(s));
            Assert.Equal("rules", back.LintChecks);
            Assert.Equal("rules", s.Clone().LintChecks);
        }

        [Fact]
        public void LintChecks_MissingFromTheFile_ReadsAsRulesAndAi()
        {
            var s = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginSettings>("{\"AiOff\": true, \"LiveCheck\": false}");
            Assert.Equal("rules-ai", s.LintChecks);
        }

        [Fact]
        public void Defaults_AreSane()
        {
            var s = new PluginSettings();
            Assert.Equal(LlmProvider.AnthropicDirect, s.Provider);
            Assert.Equal(PluginSettings.DefaultAnthropicBaseUrl, s.BaseUrl);
            Assert.True(s.MaxTokens >= 1024);
        }
    }
}
