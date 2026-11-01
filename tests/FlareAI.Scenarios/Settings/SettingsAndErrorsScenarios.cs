using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Llm;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using FlareAI.Scenarios.Support;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Settings
{
    /// <summary>
    /// A writer with no key, a gateway that says no, and where the key must never go. The model endpoints are a localhost mock
    /// (HttpListener), so AnthropicClient, BifrostClient, LlmErrors and ConnectionTest run for real, with no network.
    /// </summary>
    public class SettingsAndErrorsScenarios
    {
        private const string Key = "sk-ant-api03-SECRETSECRETSECRET0123456789";
        private const string Model = "claude-sonnet-5-5";

        /// <summary>Runs <paramref name="body"/> with no ANTHROPIC_API_KEY in the environment (a developer machine may have one).</summary>
        private static void WithoutEnvKey(Action body)
        {
            var saved = Environment.GetEnvironmentVariable(PluginSettings.EnvKeyName);
            var savedGateway = Environment.GetEnvironmentVariable(PluginSettings.GatewayEnvKeyName);
            Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, null);
            Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, null);
            try { body(); }
            finally
            {
                Environment.SetEnvironmentVariable(PluginSettings.EnvKeyName, saved);
                Environment.SetEnvironmentVariable(PluginSettings.GatewayEnvKeyName, savedGateway);
            }
        }

        // ---- 28 · TEST-PLAN A1 ------------------------------------------------------------------------------------

        [Fact]
        public void Story28_WriterWithNoKey_CheckStyleAndCheckProjectStillWork_AiActionsSayANeededKey_AndNoRequestIsMade()
        {
            WithoutEnvKey(() =>
            {
                using (var server = new MockLlmServer())
                using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", new UnavailableClient("No API key configured. Open House Style > Settings.")))
                {
                    // The settings: nothing stored, nothing in the environment.
                    var settings = new PluginSettings { BaseUrl = server.BaseUrl };
                    Assert.False(settings.HasStoredKey);
                    Assert.Null(settings.ResolveApiKey());
                    var noKey = Assert.Throws<LlmException>(() => LlmClientFactory.Create(settings));
                    Assert.Equal("No API key configured. Open House Style > Settings.", noKey.Message);                  // what the Settings dialog opens with

                    // Check style: the rules and the structure check run; findings are in the pane.
                    var lint = fx.LintOnlyCheck();
                    Assert.Contains(lint, f => f.RuleId == "company:plain-language/1" && f.CanFix);
                    Assert.NotEmpty(fx.Session.Open());

                    // The AI parts say what is missing, as a result the pane can show.
                    var units = fx.Runner.PrepareReview(fx.Runner.PickCheckBlocks(fx.Doc), lint);
                    Assert.NotEmpty(units);
                    var review = Assert.Throws<LlmException>(() => fx.Runner.CallReview(units[0]));
                    Assert.Equal(LlmFailure.Auth, review.Failure);
                    Assert.Equal("No API key configured. Open House Style > Settings.", review.Message);
                    var rewrite = Assert.Throws<LlmException>(() => fx.Runner.Rewrite(fx.Doc, new List<System.Xml.XmlNode> { FlareAI.Core.Editing.BlockSelector.SelectBlocks(fx.Doc.Document, new List<System.Xml.XmlNode>())[1] }, "Make this plainer."));
                    Assert.Equal("No API key configured. Open House Style > Settings.", rewrite.Message);
                    Assert.Equal(fx.Original, fx.Doc.Xml);

                    // Check project is rules only: it needs no key and finds the same things.
                    var project = fx.Runner.LintFiles(ActionRunner.ProjectFiles(fx.Doc.SourcePath));
                    Assert.NotEmpty(project);
                    Assert.Contains(project, f => f.DocumentPath == fx.Doc.SourcePath && f.RuleId == "company:plain-language/1");

                    Assert.Empty(server.Received);                                                                         // not one request left this machine
                }
            });
        }

        // ---- 29 · TEST-PLAN setup (gateway) -----------------------------------------------------------------------

        [Fact]
        public void Story29c_AGatewayAddressWithATrailingV1_IsStillReachedAtTheRightPath()
        {
            using (var server = new MockLlmServer())
            {
                server.Default = MockReply.Ok("OK");

                var result = ConnectionTest.Run(LlmProvider.Bifrost, server.BaseUrl + "/v1/", Key, Model, 30);

                Assert.True(result.Ok, result.Message);
                var request = server.Received.Single();
                Assert.Equal("/v1/chat/completions", request.Path);                                         // not /v1/v1/chat/completions
                Assert.Equal(Key, request.Headers["x-bf-vk"]);
            }
        }

        public static IEnumerable<object[]> Failures()
        {
            foreach (var provider in new[] { LlmProvider.AnthropicDirect, LlmProvider.Bifrost })
            {
                var gw = provider == LlmProvider.Bifrost;
                var who = gw ? "The gateway" : "Anthropic";
                yield return new object[] { provider, "401", ConnectionOutcome.KeyRejected, who + " rejected the key: check the " + (gw ? "gateway key" : "API key") + " in Settings was pasted in full." };
                yield return new object[] { provider, "403", ConnectionOutcome.ModelNotAllowed, gw ? "The gateway doesn't allow this model for your key. Ask your admin to add the Claude models" : "Your Anthropic key isn't allowed to use this model." };
                yield return new object[] { provider, "429", ConnectionOutcome.BudgetExhausted, gw ? "The gateway says this key's budget or rate limit is used up." : "Anthropic says the rate limit or credit is used up." };
                yield return new object[] { provider, "500", ConnectionOutcome.Other, "HTTP 500: " };
                yield return new object[] { provider, "529", ConnectionOutcome.Other, "HTTP 529: Overloaded" };
                yield return new object[] { provider, "html", ConnectionOutcome.Other, who + " sent back an answer that couldn't be read" };
                yield return new object[] { provider, "dropped", ConnectionOutcome.Unreachable, "dropped before the answer finished" };
                yield return new object[] { provider, "refused", ConnectionOutcome.Unreachable, "the connection was refused" };
            }
        }

        private static MockReply Reply(string kind)
        {
            switch (kind)
            {
                case "401": return MockReply.Unauthorized();
                case "403": return MockReply.ModelDenied();
                case "429": return MockReply.RateLimited();
                case "500": return MockReply.ServerError(500);
                case "529": return MockReply.ServerError(529);
                case "html": return MockReply.Malformed();
                case "dropped": return MockReply.TruncatedBody();
                default: return null;
            }
        }

        private static int ClosedPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        [Theory]
        [MemberData("Failures")]
        public void Story29d_TheModelEndpointSaysNo_TestConnectionReportsItInTheDocumentedWords_AndTheKeyIsNeverInThem(LlmProvider provider, string kind, ConnectionOutcome outcome, string message)
        {
            using (var server = new MockLlmServer())
            {
                var reply = Reply(kind);
                if (reply != null) server.Default = reply;
                var baseUrl = reply == null ? "http://127.0.0.1:" + ClosedPort() : server.BaseUrl;

                var result = ConnectionTest.Run(provider, baseUrl, Key, Model, 30);

                Assert.Equal(outcome, result.Outcome);
                Assert.Contains(message, result.Message);
                Assert.DoesNotContain(Key, result.Message);
                Assert.DoesNotContain("SECRETSECRET", result.Message);
                Assert.Equal(reply == null ? 0 : 1, server.Received.Count);
            }
        }

        [Fact]
        public void Story29f_WhenTheModelDeniesTheKey_TheExceptionTheDiagLogTheSettingsFileNeverHoldTheKey()
        {
            // The key reaches the wire in a header only. Everything the plugin keeps on disk or shows is checked for it.
            using (var ws = FakeWorkspace.Empty())
            using (var server = new MockLlmServer())
            {
                var settings = new PluginSettings { BaseUrl = server.BaseUrl, Model = Model };
                settings.SetApiKey(Key);
                settings.Save();
                Assert.Equal(Key, PluginSettings.Load().ResolveApiKey());                                    // it round-trips…
                Assert.DoesNotContain(Key, File.ReadAllText(Paths.Settings));                                 // …but the file holds it protected, not as typed
                Assert.DoesNotContain("SECRET", File.ReadAllText(Paths.Settings));

                ws.WriteFile("Content/topic.htm", string.Format(ReviewFixture.Wrapper, "<h1>Tools</h1><p>You can utilize the data in order to see it, and Mobile deposit gives members one place.</p>"));
                var client = LlmClientFactory.Create(settings);
                var fx = new ReviewFixture(null, "Content/topic.htm", client, false, ws);   // the workspace using disposes it
                {
                    var unit = fx.Runner.PrepareReview(fx.Runner.PickCheckBlocks(fx.Doc), fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)))[0];
                    var thrown = new List<Exception>();
                    foreach (var reply in new[] { MockReply.Unauthorized(), MockReply.ModelDenied(), MockReply.RateLimited(), MockReply.ServerError(500), MockReply.Malformed(), MockReply.TruncatedBody() })
                    {
                        server.Enqueue(reply);
                        try { fx.Runner.CallReview(unit); Assert.True(false, "the call should fail"); }
                        catch (LlmException ex) { thrown.Add(ex); }
                    }
                    Assert.Equal(6, thrown.Count);
                    foreach (var ex in thrown) { Assert.DoesNotContain(Key, ex.ToString()); Assert.DoesNotContain("SECRETSECRET", ex.ToString()); }
                    Assert.Equal(6, server.Received.Count(r => r.Headers["x-api-key"] == Key));              // the key did go out as the header
                }

                var diag = File.Exists(Paths.DiagLog) ? File.ReadAllText(Paths.DiagLog) : "";
                Assert.DoesNotContain(Key, diag);
                Assert.DoesNotContain("SECRETSECRET", diag);
                foreach (var f in Directory.GetFiles(ws.AppData, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("settings.json")))
                    Assert.DoesNotContain(Key, File.ReadAllText(f));
            }
        }

        // ---- 30 · TEST-PLAN setup ---------------------------------------------------------------------------------

        [Fact]
        public void Story30_WriterSavesSettings_TheKeyIsProtected_ChoicesPersist_AndTheTeamFolderAndGuideFolderApply()
        {
            using (var ws = FakeWorkspace.Empty())
            {
                var guide = Files.TeamGuide(ws.Root, null, "lead@example.com");
                var feedback = Path.Combine(ws.Root, "Feedback");
                Directory.CreateDirectory(feedback);
                var pluginDir = Path.Combine(ws.Root, "Plugins", "FlareAI");
                Files.Copy(Repo.StyleGuide, PluginSettings.BundledGuide(pluginDir));

                var typed = new PluginSettings
                {
                    Provider = LlmProvider.Bifrost, BaseUrl = "https://gateway.example.com", Model = "claude-opus-5-5", TimeoutSeconds = 45,
                    FeedbackFolder = feedback, StyleLayers = "{PluginDir}\\StyleGuide\r\n" + guide, LiveCheck = false
                };
                typed.SetApiKey(Key);
                typed.Save();

                var raw = File.ReadAllText(Paths.Settings);
                Assert.DoesNotContain(Key, raw);
                Assert.Contains("ProtectedApiKey", raw);

                var reopened = PluginSettings.Load();                                                          // close and reopen Flare
                Assert.Equal(LlmProvider.Bifrost, reopened.Provider);
                Assert.Equal("https://gateway.example.com", reopened.BaseUrl);
                Assert.Equal("claude-opus-5-5", reopened.Model);
                Assert.Equal(45, reopened.TimeoutSeconds);
                Assert.False(reopened.LiveCheck);
                Assert.True(reopened.ShowMarkers);                                                             // untouched options keep their defaults
                Assert.True(reopened.HasStoredKey);
                Assert.Equal(Key, reopened.ResolveApiKey());
                var client = LlmClientFactory.Create(reopened);
                Assert.IsType<BifrostClient>(client);                                                          // the provider picks the client

                // The guide folder applies: the team's guide is where changes go; the bundled guide still loads under it.
                Assert.True(reopened.HasTeamGuide(pluginDir));
                Assert.Equal(guide, reopened.GuideRoot(pluginDir));
                Assert.Equal(new[] { PluginSettings.BundledGuide(pluginDir), guide }, reopened.GuideRoots(pluginDir).ToArray());
                // The feedback folder applies: decisions are shared, and requests go there.
                var team = Team.Load(reopened.GuideRoot(pluginDir), reopened.FeedbackFolder, "pat@example.com");
                Assert.Equal(feedback, team.FeedbackFolder);
                Assert.True(team.IsStrict);
                var f = new StyleFinding { Rule = "Google.WordList", RuleId = "google:word-list/251", Layer = "google", Excerpt = "e-mail", Replacement = "email", Source = "lint" };
                Team.Current = team;
                FeedbackLog.Record(f, "C:\\docs\\a.htm", "rejected", "wrong");
                Assert.Single(Directory.GetFiles(feedback, "*.jsonl"));
                Assert.Contains("google:word-list/251", File.ReadAllText(Directory.GetFiles(feedback, "*.jsonl")[0]));

                // Anthropic direct needs no address: an empty one means the default.
                var direct = new PluginSettings { BaseUrl = "" };
                direct.SetApiKey(Key);
                Assert.IsType<AnthropicClient>(LlmClientFactory.Create(direct));
            }
        }
    }
}
