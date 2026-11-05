using FlareAI.Core.Settings;
using Xunit;

namespace FlareAI.Core.Tests
{
    [Collection("Core statics")]
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
        public void ResolveApiKey_NeverUsesAVariable_OnlyTheKeySavedInSettings()
        {
            var old = System.Environment.GetEnvironmentVariable(PluginSettings.EnvKeyName);
            var oldGateway = System.Environment.GetEnvironmentVariable(PluginSettings.GatewayEnvKeyName);
            System.Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, "sk-ant-from-a-variable-1234");
            System.Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, "gw-from-a-variable-1234");
            try
            {
                var s = new PluginSettings();
                s.Provider = LlmProvider.AnthropicDirect;
                Assert.Null(s.ResolveApiKey());
                Assert.False(s.HasStoredKey);
                s.Provider = LlmProvider.Bifrost;
                Assert.Null(s.ResolveApiKey());
                s.SetApiKey("sk-ant-saved-in-settings-5678");
                Assert.Equal("sk-ant-saved-in-settings-5678", s.ResolveApiKey());
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, old);
                System.Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, oldGateway);
            }
        }

        [Fact]
        public void SavedKey_IsNotInThePlainTextOfTheSettingsFile_AndIsUselessWithoutTheUsersDpapi()
        {
            var old = FlareAI.Core.Paths.Root;
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flareai-keyfile-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            FlareAI.Core.Paths.Root = dir;
            try
            {
                const string key = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
                var s = new PluginSettings();
                s.SetApiKey(key);
                s.Save();
                var file = System.IO.File.ReadAllText(PluginSettings.SettingsPath, System.Text.Encoding.UTF8);
                Assert.DoesNotContain(key, file);
                Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ", file);
                Assert.Contains("ProtectedApiKey", file);
                // The stored form is DPAPI output, not the key's own bytes in any encoding.
                var bytes = System.Convert.FromBase64String(PluginSettings.Load().ProtectedApiKey);
                Assert.DoesNotContain("ABCDEFGHIJKLMNOP", System.Text.Encoding.UTF8.GetString(bytes));
                Assert.DoesNotContain("ABCDEFGHIJKLMNOP", System.Text.Encoding.Unicode.GetString(bytes));
                Assert.Equal(key, PluginSettings.Load().ResolveApiKey());
            }
            finally
            {
                FlareAI.Core.Paths.Root = old;
                try { System.IO.Directory.Delete(dir, true); } catch (System.Exception) { }
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
