using System;
using System.IO;
using FlareAI.Core.Llm;
using FlareAI.Core.Settings;
using Newtonsoft.Json;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>The AI choices, first-run setup answers and team guide folder check behind Settings and setup (#13, #39, #91).</summary>
    public class FirstRunSetupTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "hs-setup-" + Guid.NewGuid().ToString("N"));

        public FirstRunSetupTests() { Directory.CreateDirectory(_tmp); }
        public void Dispose() { try { Directory.Delete(_tmp, true); } catch (Exception) { } }

        // ---- AI choices ----------------------------------------------------------------------------------------

        [Fact]
        public void EachChoiceShowsOnlyTheFieldsItNeeds()
        {
            Assert.False(AiChoices.UsesKey(AiChoice.None));
            Assert.False(AiChoices.UsesBaseUrl(AiChoice.None));
            Assert.False(AiChoices.UsesModelPickers(AiChoice.None));

            Assert.True(AiChoices.UsesKey(AiChoice.AnthropicKey));
            Assert.False(AiChoices.UsesBaseUrl(AiChoice.AnthropicKey));
            Assert.True(AiChoices.UsesModelPickers(AiChoice.AnthropicKey));
            Assert.Equal("API key", AiChoices.KeyLabel(AiChoice.AnthropicKey));

            Assert.True(AiChoices.UsesKey(AiChoice.Gateway));
            Assert.True(AiChoices.UsesBaseUrl(AiChoice.Gateway));
            Assert.False(AiChoices.UsesModelPickers(AiChoice.Gateway));
            Assert.Equal("Gateway key", AiChoices.KeyLabel(AiChoice.Gateway));
        }

        [Fact]
        public void ChoiceLabelsArePlainWords()
        {
            Assert.Equal("No AI (rules only)", AiChoices.Label(AiChoice.None));
            Assert.Equal("My Anthropic API key", AiChoices.Label(AiChoice.AnthropicKey));
            Assert.Equal("My company's AI service (gateway)", AiChoices.Label(AiChoice.Gateway));
        }

        [Fact]
        public void ChoiceRoundTripsThroughSettings()
        {
            foreach (var c in AiChoices.All)
            {
                var s = new PluginSettings();
                AiChoices.Apply(s, c);
                Assert.Equal(c, AiChoices.Of(s));
            }
        }

        [Fact]
        public void NoAi_RemovesTheStoredKey_AndStopsTheClientEvenWithAnEnvironmentKey()
        {
            var s = new PluginSettings();
            s.SetApiKey("sk-ant-test-1");
            AiChoices.Apply(s, AiChoice.None);
            Assert.False(s.HasStoredKey);
            Assert.True(s.AiOff);
            var ex = Assert.Throws<LlmException>(() => LlmClientFactory.Create(s));
            Assert.Contains("Settings", ex.Message);
        }

        [Fact]
        public void SwitchingProvider_DropsTheOtherProvidersAddress()
        {
            var s = new PluginSettings();
            AiChoices.Apply(s, AiChoice.Gateway);
            Assert.Equal("", s.BaseUrl);
            s.BaseUrl = "https://gw.example.com";
            AiChoices.Apply(s, AiChoice.AnthropicKey);
            Assert.Equal(PluginSettings.DefaultAnthropicBaseUrl, s.BaseUrl);
        }

        [Fact]
        public void OldSettingsFiles_ReadAsAiOn()
        {
            // A file written before AiOff existed: the property is missing, so AI stays on with the same provider.
            var s = JsonConvert.DeserializeObject<PluginSettings>("{\"Provider\":1,\"BaseUrl\":\"https://gw.example.com\",\"Model\":\"claude-sonnet-5-5\"}");
            Assert.False(s.AiOff);
            Assert.Equal(AiChoice.Gateway, AiChoices.Of(s));
            // And the new field is only added, never renamed.
            Assert.Contains("\"AiOff\"", JsonConvert.SerializeObject(new PluginSettings()));
            Assert.Contains("\"Provider\"", JsonConvert.SerializeObject(new PluginSettings()));
        }

        // ---- first run -----------------------------------------------------------------------------------------

        [Fact]
        public void FirstRun_IsNeededOnlyWithoutASettingsFile()
        {
            var path = Path.Combine(_tmp, "settings.json");
            Assert.True(FirstRun.IsNeeded(path));
            File.WriteAllText(path, "{}");
            Assert.False(FirstRun.IsNeeded(path));
        }

        [Fact]
        public void Answers_NoAi_SavesNoKey()
        {
            var s = new PluginSettings();
            s.SetApiKey("sk-ant-old");
            var a = new SetupAnswers { Ai = AiChoice.None, Key = "sk-typed-but-ignored" };
            string error;
            Assert.True(a.ApplyTo(s, out error), error);
            Assert.True(s.AiOff);
            Assert.False(s.HasStoredKey);
        }

        [Fact]
        public void Answers_AnthropicKey_NeedsAKey()
        {
            var s = new PluginSettings();
            var a = new SetupAnswers { Ai = AiChoice.AnthropicKey, Key = "" };
            string error;
            if (Environment.GetEnvironmentVariable(PluginSettings.EnvKeyName) == null)
            {
                Assert.False(a.ApplyTo(s, out error));
                Assert.Contains("API key", error);
                Assert.Contains("Skip", error);
            }
            a.Key = "sk-ant-new";
            Assert.True(a.ApplyTo(s, out error), error);
            Assert.Equal("sk-ant-new", s.ResolveApiKey());
            Assert.Equal(LlmProvider.AnthropicDirect, s.Provider);
            Assert.Equal(PluginSettings.DefaultAnthropicBaseUrl, s.BaseUrl);
        }

        [Fact]
        public void Answers_Gateway_NormalizesTheAddress_AndRejectsABadOne()
        {
            var s = new PluginSettings();
            var a = new SetupAnswers { Ai = AiChoice.Gateway, Key = "vk-1", BaseUrl = "gw.example.com/v1/" };
            string error;
            Assert.True(a.ApplyTo(s, out error), error);
            Assert.Equal("https://gw.example.com", s.BaseUrl);
            Assert.Equal(LlmProvider.Bifrost, s.Provider);
            Assert.False(s.AiOff);

            var bad = new SetupAnswers { Ai = AiChoice.Gateway, Key = "vk-1", BaseUrl = "" };
            var before = s.BaseUrl;
            Assert.False(bad.ApplyTo(s, out error));
            Assert.Contains("gateway address", error);
            Assert.Equal(before, s.BaseUrl);   // nothing changed
        }

        [Fact]
        public void Answers_SwitchingProviderWithoutAKey_NeverReusesTheOtherProvidersKey()
        {
            var s = new PluginSettings();
            s.SetApiKey("sk-ant-direct");
            // A stored key counts for the same choice only.
            var same = new SetupAnswers { Ai = AiChoice.AnthropicKey, Key = "" };
            Assert.Null(same.AiProblem(s));
            var other = new SetupAnswers { Ai = AiChoice.Gateway, BaseUrl = "https://gw.example.com", Key = "" };
            Assert.NotNull(other.AiProblem(s));
        }

        [Fact]
        public void Answers_Skipped_LeaveSettingsAsTheyAre()
        {
            var s = new PluginSettings { StyleLayers = @"\\server\guide" };
            s.SetApiKey("sk-ant-keep");
            var a = new SetupAnswers { AiSkipped = true, GuideSkipped = true, Ai = AiChoice.None, UseTeamGuide = false };
            string error;
            Assert.True(a.ApplyTo(s, out error), error);
            Assert.False(s.AiOff);
            Assert.Equal("sk-ant-keep", s.ResolveApiKey());
            Assert.Equal(@"\\server\guide", s.StyleLayers);
        }

        [Fact]
        public void Answers_GuideChoice_SetsOrClearsTheTeamGuide()
        {
            var s = new PluginSettings { StyleLayers = @"C:\a" + "\n" + @"C:\b" };
            string error;
            // A folder already in the list keeps the whole list.
            Assert.True(new SetupAnswers { AiSkipped = true, UseTeamGuide = true, TeamGuideFolder = @"C:\b\" }.ApplyTo(s, out error), error);
            Assert.Equal(@"C:\a" + "\n" + @"C:\b", s.StyleLayers);
            Assert.True(new SetupAnswers { AiSkipped = true, UseTeamGuide = true, TeamGuideFolder = @"D:\team" }.ApplyTo(s, out error), error);
            Assert.Equal(@"D:\team", s.StyleLayers);
            Assert.True(new SetupAnswers { AiSkipped = true, UseTeamGuide = false }.ApplyTo(s, out error), error);
            Assert.Equal("", s.StyleLayers);
            Assert.False(new SetupAnswers { AiSkipped = true, UseTeamGuide = true, TeamGuideFolder = " " }.ApplyTo(s, out error));
            Assert.Contains("folder", error);
        }

        [Fact]
        public void Answers_From_StartFromTheCurrentSettings()
        {
            var s = new PluginSettings { Provider = LlmProvider.Bifrost, BaseUrl = "https://gw.example.com", StyleLayers = @"D:\team" };
            var a = SetupAnswers.From(s);
            Assert.Equal(AiChoice.Gateway, a.Ai);
            Assert.Equal("https://gw.example.com", a.BaseUrl);
            Assert.True(a.UseTeamGuide);
            Assert.Equal(@"D:\team", a.TeamGuideFolder);
            var fresh = SetupAnswers.From(new PluginSettings());
            Assert.Equal(AiChoice.AnthropicKey, fresh.Ai);
            Assert.False(fresh.UseTeamGuide);
        }

        [Fact]
        public void Summary_SaysWhatWasSetUp()
        {
            var s = new PluginSettings();
            AiChoices.Apply(s, AiChoice.None);
            Assert.Contains("AI: off", FirstRun.Summary(s));
            Assert.Contains("Google's guide", FirstRun.Summary(s));
            s.StyleLayers = @"D:\team";
            Assert.Contains(@"D:\team", FirstRun.Summary(s));
        }

        // ---- team feedback folder (#257, for #91) -----------------------------------------------------------------

        [Fact]
        public void ApplyTo_TeamGuideWithFeedbackFolder_SavesBoth()
        {
            var s = new PluginSettings();
            string error;
            Assert.True(new SetupAnswers { AiSkipped = true, UseTeamGuide = true, TeamGuideFolder = @"D:\team", FeedbackFolder = @" \\server\feedback\ " }.ApplyTo(s, out error), error);
            Assert.Equal(@"D:\team", s.StyleLayers);
            Assert.Equal(@"\\server\feedback", s.FeedbackFolder);
        }

        [Fact]
        public void ApplyTo_BlankFeedbackFolder_KeepsTheCurrentOne()
        {
            var s = new PluginSettings { FeedbackFolder = @"\\server\feedback" };
            string error;
            Assert.True(new SetupAnswers { AiSkipped = true, UseTeamGuide = true, TeamGuideFolder = @"D:\team", FeedbackFolder = "  " }.ApplyTo(s, out error), error);
            Assert.Equal(@"\\server\feedback", s.FeedbackFolder);
            // Run again from the saved settings: the folder is already set, so it's kept and not asked for.
            var again = SetupAnswers.From(s);
            again.AiSkipped = true;
            Assert.True(again.ApplyTo(s, out error), error);
            Assert.Equal(@"\\server\feedback", s.FeedbackFolder);
        }

        [Fact]
        public void ApplyTo_GoogleGuideChoice_LeavesTheFeedbackFolderAlone()
        {
            var s = new PluginSettings { StyleLayers = @"D:\team", FeedbackFolder = @"\\server\feedback" };
            string error;
            Assert.True(new SetupAnswers { AiSkipped = true, UseTeamGuide = false, FeedbackFolder = @"E:\other" }.ApplyTo(s, out error), error);
            Assert.Equal("", s.StyleLayers);
            Assert.Equal(@"\\server\feedback", s.FeedbackFolder);
            // A skipped guide step leaves it alone too.
            Assert.True(new SetupAnswers { AiSkipped = true, GuideSkipped = true, UseTeamGuide = true, TeamGuideFolder = @"D:\x", FeedbackFolder = @"E:\other" }.ApplyTo(s, out error), error);
            Assert.Equal(@"\\server\feedback", s.FeedbackFolder);
        }

        [Fact]
        public void From_SetsHadFeedbackFolder()
        {
            var a = SetupAnswers.From(new PluginSettings { StyleLayers = @"D:\team", FeedbackFolder = @"\\server\feedback" });
            Assert.True(a.HadFeedbackFolder);
            Assert.Equal(@"\\server\feedback", a.FeedbackFolder);
            Assert.False(SetupAnswers.From(new PluginSettings()).HadFeedbackFolder);
            Assert.False(SetupAnswers.From(new PluginSettings { FeedbackFolder = " " }).HadFeedbackFolder);
        }

        [Fact]
        public void FeedbackProblem_RefusesARelativePath_AndKeepsADriveRoot()
        {
            Assert.NotNull(new SetupAnswers { UseTeamGuide = true, TeamGuideFolder = @"D:\team", FeedbackFolder = "feedback" }.FeedbackProblem());
            Assert.NotNull(new SetupAnswers { UseTeamGuide = true, TeamGuideFolder = @"D:\team", FeedbackFolder = @"D:feedback" }.FeedbackProblem());
            Assert.NotNull(new SetupAnswers { UseTeamGuide = true, TeamGuideFolder = @"D:\team", FeedbackFolder = "D:\\fe<ed" + "\u0001" }.FeedbackProblem());
            Assert.Null(new SetupAnswers { UseTeamGuide = true, TeamGuideFolder = @"D:\team", FeedbackFolder = "" }.FeedbackProblem());
            Assert.Null(new SetupAnswers { UseTeamGuide = false, FeedbackFolder = "feedback" }.FeedbackProblem());
            var s = new PluginSettings();
            string error;
            Assert.False(new SetupAnswers { AiSkipped = true, UseTeamGuide = true, TeamGuideFolder = @"D:\team", FeedbackFolder = "feedback" }.ApplyTo(s, out error));
            Assert.Contains("full path", error);
            Assert.True(s.StyleLayers.Length == 0, "nothing saved when the folder is refused");
            Assert.True(new SetupAnswers { AiSkipped = true, UseTeamGuide = true, TeamGuideFolder = @"D:\team", FeedbackFolder = @"F:\" }.ApplyTo(s, out error), error);
            Assert.Equal(@"F:\", s.FeedbackFolder);
        }

        [Fact]
        public void Summary_SaysWhereRequestsGo_OnlyWithATeamGuide()
        {
            var s = new PluginSettings { FeedbackFolder = @"\\server\feedback" };
            Assert.DoesNotContain("Requests:", FirstRun.Summary(s));
            s.StyleLayers = @"D:\team";
            Assert.Contains(@"Requests: sent to your style guide owners through \\server\feedback.", FirstRun.Summary(s));
            s.FeedbackFolder = "";
            Assert.Contains("Requests: kept on this computer until you choose a team feedback folder in Settings.", FirstRun.Summary(s));
        }

        [Fact]
        public void Summary_FolderNotWritable_SaysRequestsWaitUntilItCanBeReached()
        {
            var s = new PluginSettings { StyleLayers = @"D:\team", FeedbackFolder = @"\\server\feedback" };
            Assert.Contains(@"Requests: kept on this computer until \\server\feedback can be reached.", FirstRun.Summary(s, false));
            Assert.True(new SetupAnswers().FeedbackWritable);
        }

        // ---- team guide folder ---------------------------------------------------------------------------------

        [Fact]
        public void GuideFolder_NoRules_SaysWhereRulesGo_WithoutACheckMark()
        {
            var dir = Project("Empty");
            var r = GuideFolderCheck.Check(dir, "me@example.com");
            Assert.True(r.Ok, r.Message);
            Assert.Equal(0, r.Rules);
            Assert.DoesNotContain("✓", r.Message);
            Assert.StartsWith(@"Empty has no House Style rules yet. They go in topics in its Content\Resources\StyleGuide folder, and the guide's owner can add them with Import Rules.", r.Message);
            Assert.Contains("change its rules", r.Message);   // the role sentence still follows
        }

        [Fact]
        public void GuideFolder_Valid_SaysYouCanChangeTheRules()
        {
            var dir = Project("Guide");
            var r = GuideFolderCheck.Check(dir, "me@example.com");
            Assert.True(r.Ok, r.Message);
            Assert.True(r.Reachable);
            Assert.True(r.CanChangeRules);
            Assert.Contains("Guide", r.Message);
            Assert.Contains("change its rules", r.Message);
        }

        [Fact]
        public void GuideFolder_WithOwners_IsReadOnlyForOthers_AndEditableForOwners()
        {
            var dir = Project("Owned");
            Directory.CreateDirectory(Path.Combine(dir, "FlareAI"));
            File.WriteAllText(Path.Combine(dir, "FlareAI", "owners.txt"), "lead@example.com\n");
            var writer = GuideFolderCheck.Check(dir, "me@example.com");
            Assert.True(writer.Ok);
            Assert.False(writer.CanChangeRules);
            Assert.Contains("Read-only for you", writer.Message);
            Assert.Contains("lead@example.com", writer.Message);
            var owner = GuideFolderCheck.Check(dir, "lead@example.com");
            Assert.True(owner.CanChangeRules);
            Assert.Contains("one of its owners", owner.Message);
        }

        [Fact]
        public void GuideFolder_NotAFlareProject_SaysWhatIsWrong()
        {
            var dir = Path.Combine(_tmp, "plain");
            Directory.CreateDirectory(dir);
            var r = GuideFolderCheck.Check(dir, "me@example.com");
            Assert.False(r.Ok);
            Assert.True(r.Reachable);
            Assert.Contains("isn't a Flare project", r.Message);
        }

        [Fact]
        public void GuideFolder_Unreachable_SaysSoWithoutThrowing()
        {
            var r = GuideFolderCheck.Check(Path.Combine(_tmp, "missing", "guide"), "me@example.com");
            Assert.False(r.Ok);
            Assert.False(r.Reachable);
            Assert.Contains("Can't reach", r.Message);
            Assert.False(GuideFolderCheck.Check("", null).Ok);
        }

        private string Project(string name)
        {
            var dir = Path.Combine(_tmp, name);
            Directory.CreateDirectory(Path.Combine(dir, "Content"));
            File.WriteAllText(Path.Combine(dir, name + ".flprj"), "<?xml version=\"1.0\" encoding=\"utf-8\"?><CatapultProject />");
            return dir;
        }
    }
}
