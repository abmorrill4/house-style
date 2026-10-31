using System.IO;
using FlareAI.Core.Llm;
using FlareAI.Core.Settings;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Smoke
{
    /// <summary>
    /// First-run setup end to end, as the setup window drives it: no settings file, answers for each AI path and guide
    /// choice, Finish; then the saved file loads into settings the plugin can use, and setup doesn't show again.
    /// </summary>
    public class FirstRunScenario
    {
        [Fact]
        public void Gateway_WithTeamGuide_CompletesIntoAGatewayClientAndTheTeamGuide()
        {
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                var check = GuideFolderCheck.Check(ws.ProjectDir, "writer@example.com");
                Assert.True(check.Ok, check.Message);
                var saved = Complete(new SetupAnswers
                {
                    Ai = AiChoice.Gateway, BaseUrl = "http://localhost:8080/v1", Key = "vk-scenario",
                    UseTeamGuide = true, TeamGuideFolder = ws.ProjectDir
                });
                Assert.Equal(LlmProvider.Bifrost, saved.Provider);
                Assert.Equal("http://localhost:8080", saved.BaseUrl);
                Assert.IsType<BifrostClient>(LlmClientFactory.Create(saved));
                Assert.Equal(ws.ProjectDir.TrimEnd('\\'), saved.StyleLayers);
                Assert.True(saved.HasTeamGuide(Path.Combine(ws.AppData, "plugin")));
            }
        }

        [Fact]
        public void SkippedSetup_SavesDefaults_AndDoesNotShowAgain()
        {
            using (var ws = FakeWorkspace.Empty())
            {
                Assert.True(FirstRun.IsNeeded());
                string error;
                Assert.True(FirstRun.Finish(PluginSettings.Load(), null, out error));
                Assert.False(FirstRun.IsNeeded());
                var saved = PluginSettings.Load();
                Assert.Equal(AiChoice.AnthropicKey, AiChoices.Of(saved));
                Assert.True(saved.LiveCheck && saved.CheckOnSave && saved.ReportBeforeBuild && saved.ShowMarkers);
            }
        }

        [Fact]
        public void AnInvalidAnswer_SavesNothing_AndSetupStaysDue()
        {
            using (var ws = FakeWorkspace.Empty())
            {
                string error;
                Assert.False(FirstRun.Finish(PluginSettings.Load(), new SetupAnswers { Ai = AiChoice.Gateway, BaseUrl = "not a url", Key = "vk" }, out error));
                Assert.NotNull(error);
                Assert.True(FirstRun.IsNeeded());
            }
        }

        /// <summary>Runs setup from a fresh install to Finish and returns the settings as the plugin next loads them.</summary>
        private static PluginSettings Complete(SetupAnswers answers)
        {
            Assert.True(FirstRun.IsNeeded());
            var start = PluginSettings.Load();
            string error;
            Assert.True(FirstRun.Finish(start, answers, out error), error);
            Assert.False(FirstRun.IsNeeded());
            return PluginSettings.Load();
        }
    }
}
