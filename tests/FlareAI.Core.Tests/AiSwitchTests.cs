using System;
using System.IO;
using FlareAI.Core.Llm;
using FlareAI.Core.Settings;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class AiSwitchTests : IDisposable
    {
        private readonly string _oldRoot, _oldKey, _oldGateway, _dir;

        public AiSwitchTests()
        {
            _oldKey = Environment.GetEnvironmentVariable(PluginSettings.EnvKeyName);
            _oldGateway = Environment.GetEnvironmentVariable(PluginSettings.GatewayEnvKeyName);
            Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, null);
            Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, null);
            _oldRoot = FlareAI.Core.Paths.Root;
            _dir = Path.Combine(Path.GetTempPath(), "flareai-aiswitch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            FlareAI.Core.Paths.Root = _dir;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, _oldKey);
            Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, _oldGateway);
            FlareAI.Core.Paths.Root = _oldRoot;
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private static PluginSettings WithKey()
        {
            var s = new PluginSettings();
            s.SetApiKey("sk-ant-test-123");
            return s;
        }

        [Fact]
        public void Label_ReadsTheState()
        {
            Assert.Equal("AI Off", AiSwitch.Label(true));
            Assert.Equal("AI On", AiSwitch.Label(false));
        }

        [Fact]
        public void TurnOff_KeepsTheKey_AndTheAiTabShowsNone()
        {
            var s = WithKey();
            var key = s.ProtectedApiKey;
            AiSwitch.TurnOff(s);
            Assert.True(s.AiOff);
            Assert.Equal(key, s.ProtectedApiKey);
            Assert.Equal(AiChoice.None, AiChoices.Of(s));
        }

        [Fact]
        public void TryTurnOn_WithAKey_TurnsAiOn_AndTheAiTabShowsTheKeyChoice()
        {
            var s = WithKey();
            s.AiOff = true;
            Assert.True(AiSwitch.TryTurnOn(s));
            Assert.False(s.AiOff);
            Assert.Equal(AiChoice.AnthropicKey, AiChoices.Of(s));
        }

        [Fact]
        public void TryTurnOn_WithNoKey_ChangesNothing()
        {
            var s = new PluginSettings { AiOff = true };
            Assert.False(AiSwitch.TryTurnOn(s));
            Assert.True(s.AiOff);
        }

        [Fact]
        public void TryTurnOn_Gateway_NeedsAnAddress()
        {
            var s = WithKey();
            s.Provider = LlmProvider.Bifrost;
            s.AiOff = true;
            s.BaseUrl = "";
            Assert.False(AiSwitch.TryTurnOn(s));
            Assert.True(s.AiOff);
            s.BaseUrl = "https://gateway.example.com";
            Assert.True(AiSwitch.TryTurnOn(s));
            Assert.False(s.AiOff);
        }

        [Fact]
        public void TheState_SurvivesARestart()
        {
            var s = WithKey();
            AiSwitch.TurnOff(s);
            s.Save();
            Assert.True(PluginSettings.Load().AiOff);

            var again = PluginSettings.Load();
            Assert.True(AiSwitch.TryTurnOn(again));
            again.Save();
            Assert.False(PluginSettings.Load().AiOff);
        }

        [Fact]
        public void WithAiOff_NoClientIsBuilt()
        {
            var s = WithKey();
            AiSwitch.TurnOff(s);
            var ex = Assert.Throws<LlmException>(() => LlmClientFactory.Create(s));
            Assert.Equal(LlmClientFactory.AiOffMessage, ex.Message);
        }
    }
}
