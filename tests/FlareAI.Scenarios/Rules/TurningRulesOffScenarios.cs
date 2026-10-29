using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using FlareAI.Scenarios.Support;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Rules
{
    /// <summary>
    /// Turning rules off: for the team, for one project, with the rules that flag the same words, by someone who is not an owner,
    /// from a read-only install folder, and a company rule that overrides Google. The #34 ("turned off, still flagged"), #54
    /// (same-span twins) and #36 (save under Program Files) regressions live here. Each story names the TEST-PLAN case it automates.
    /// </summary>
    public class TurningRulesOffScenarios
    {
        private const string Pat = "pat@example.com", Lead = "lead@example.com";
        private const string Reason = "Our readers say it this way.";
        private const string EMail = "google:word-list/251";          // e-mail → email
        private const string InOrderTo = "company:plain-language/2";  // in order to → to
        private const string Please = "company:plain-language/5";     // Please click → Click
        private const string PleaseTwin = "google:tone/28";
        private const string LegacyTopic = "Content/Invoices/void-an-invoice.htm";
        private const string WorkOrderTopic = "Content/WorkOrders/WO_ReopenAClosedWorkOrder.htm";   // e-mail, in order to, please

        /// <summary>A writer on a team: a team guide (the bundled Company rules) in a shared folder, optional owners and feedback folder.</summary>
        private sealed class Team_ : IDisposable
        {
            public ReviewFixture Fx;
            public string Guide, Feedback;
            public FakeWorkspace Ws { get { return Fx.Ws; } }
            public string Who;

            public static Team_ Legacy(string who = Pat, string[] owners = null, bool feedbackFolder = false, string topic = LegacyTopic)
            {
                var ws = ReviewFixture.Corpus("v2/legacy");
                return Create(ws, topic, who, owners, feedbackFolder);
            }

            public static Team_ OnTopic(string bodyXml, string who = Pat, string[] owners = null, bool feedbackFolder = false)
            {
                var ws = FakeWorkspace.Empty();
                ws.WriteFile("Content/topic.htm", string.Format(ReviewFixture.Wrapper, bodyXml));
                return Create(ws, "Content/topic.htm", who, owners, feedbackFolder);
            }

            private static Team_ Create(FakeWorkspace ws, string topic, string who, string[] owners, bool feedbackFolder)
            {
                var t = new Team_ { Who = who };
                t.Guide = Files.TeamGuide(ws.Root, null, owners ?? new string[0]);
                if (feedbackFolder) { t.Feedback = Path.Combine(ws.Root, "Feedback"); Directory.CreateDirectory(t.Feedback); }
                var guide = t.Guide;
                t.Fx = new ReviewFixture(null, topic, null, false, ws, () => new StyleEngine(new[] { guide }, Repo.StyleGuide));
                t.Fx.Team = Team.Load(t.Guide, t.Feedback, who);
                return t;
            }

            /// <summary>Close and reopen Flare: everything is read from disk again.</summary>
            public void Restart() { Fx.Restart(() => Team.Load(Guide, Feedback, Who)); }

            public StyleFinding Choose(string ruleId, string excerptStart = null)
            {
                var f = Fx.Find(ruleId, excerptStart);
                Fx.Session.Select(f);
                return f;
            }

            public bool Flags(string ruleId) { Fx.Check(); return Fx.Session.Findings.Any(f => f.RuleId == ruleId); }

            public void Dispose() { Fx.Dispose(); }
        }

        private static string CompanyFolder { get { return Path.Combine(Repo.StyleGuide, @"Content\Resources\StyleGuide\Company"); } }

        // ---- 16 · TEST-PLAN D6 ------------------------------------------------------------------------------------

        [Fact]
        public void Story16_OwnerTurnsARuleOffForTheTeam_ItIsGoneOnReCheck_AndStillGoneAfterARestart()
        {
            // D6 + #34: a row appears in the guide with who and when; the rule stops being flagged, for good.
            var bundledBefore = Files.Snapshot(CompanyFolder);
            using (var t = Team_.Legacy(topic: WorkOrderTopic))
            {
                t.Fx.Check();
                Assert.True(t.Flags(EMail));
                Assert.True(t.Flags(InOrderTo));
                var f = t.Choose(EMail);
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = Reason };

                t.Fx.Session.TurnOffRule();

                var row = GuideOverrides.Read(t.Guide).Single(o => o.RuleId == EMail);
                Assert.Equal("off", row.Setting);
                Assert.Equal(Reason, row.Note);
                Assert.Equal(Pat, row.By);
                Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), row.Date);
                Assert.StartsWith(t.Guide, row.File);                                    // the team's guide, never the plugin's copy
                Assert.DoesNotContain(t.Fx.Session.Findings, x => x.RuleId == EMail);       // gone from the pane at once
                Assert.Contains("for the team", t.Fx.Session.Status);
                Assert.Empty(t.Fx.Host.Errors);

                Assert.False(t.Flags(EMail));                                              // re-check: still gone
                Assert.True(t.Flags(InOrderTo));                                            // only that rule

                t.Restart();                                                               // close and reopen Flare
                Assert.False(t.Flags(EMail));
                Assert.True(t.Flags(InOrderTo));
                Assert.Equal(bundledBefore, Files.Snapshot(CompanyFolder));                // the repo's bundled guide is untouched
                Assert.Contains(File.ReadAllLines(Paths.Feedback), l => l.Contains("\"rule ignored\"") && l.Contains(EMail));
            }
        }

        [Fact]
        public void DisableRule_WhileTheTeamFolderIsMissing_ChangesNothing_AndDoesNotRecreateTheFolder()
        {
            // #235: the team's folder went offline after the check. Writing would leave a near-empty guide in its place.
            using (var t = Team_.Legacy(topic: WorkOrderTopic))
            {
                t.Fx.Check();
                t.Choose(EMail);
                var away = t.Guide + "-offline";
                Directory.Move(t.Guide, away);
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = Reason };

                t.Fx.Session.TurnOffRule();

                Assert.False(Directory.Exists(t.Guide));
                Assert.Contains(t.Fx.Host.Errors, e => e.Contains(GuideWriteException.UnreachableMessage) && e.Contains(t.Guide));
                Assert.Contains("not changed", t.Fx.Session.Status);
                Assert.Contains(t.Fx.Session.Findings, x => x.RuleId == EMail);
                Directory.Move(away, t.Guide);
                Assert.Empty(GuideOverrides.Read(t.Guide).Where(o => o.RuleId == EMail));
            }
        }

        // ---- 17 · TEST-PLAN D6, #54 -------------------------------------------------------------------------------

        [Theory]
        [InlineData("<h1>Tips</h1><p>Please click Save to continue.</p>", Please, "Please", PleaseTwin)]
        [InlineData("<h1>Tips</h1><p>Let’s start with the first step.</p>", "google:word-list/432", "Let", "google:quotation-marks/2")]
        public void Story17a_RuleWithSameSpanTwins_TheDialogOffersThem_AndTurningOffAllLeavesNoHitOnTheSpan(string body, string rule, string span, string twin)
        {
            using (var t = Team_.OnTopic(body))
            {
                t.Fx.Check();
                var f = t.Fx.Session.Findings.Single(x => x.RuleId == rule && x.Excerpt.StartsWith(span));
                Assert.Contains(f.AlsoRules, a => a.Id == twin);
                t.Fx.Session.Select(f);
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = Reason, AlsoOthers = true };

                t.Fx.Session.TurnOffRule();

                Assert.Contains(t.Fx.Host.Offered, o => o.Id == twin && o.Name.StartsWith("Google ·"));   // the writer read the twin's wording before agreeing
                var rows = GuideOverrides.Read(t.Guide).Select(o => o.RuleId).ToList();
                Assert.Contains(rule, rows);
                Assert.Contains(twin, rows);
                t.Fx.Check();
                Assert.DoesNotContain(t.Fx.Session.Findings, x => x.Excerpt.StartsWith(span) || x.RuleId == rule || x.RuleId == twin);
                t.Restart();
                t.Fx.Check();
                Assert.DoesNotContain(t.Fx.Session.Findings, x => x.Excerpt.StartsWith(span) || x.RuleId == rule || x.RuleId == twin);
            }
        }

        [Fact]
        public void Story17b_TurningOffOnlyOneOfTwoRulesOnTheSameWords_ShowsTheOtherOne_TheSameOneEveryTime()
        {
            using (var t = Team_.OnTopic("<h1>Tips</h1><p>Please click Save to continue.</p>"))
            {
                t.Fx.Check();
                t.Fx.Session.Select(t.Fx.Session.Findings.Single(x => x.RuleId == Please));
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = Reason, AlsoOthers = false };

                t.Fx.Session.TurnOffRule();

                var shown = new List<string>();
                for (var i = 0; i < 3; i++)
                {
                    t.Fx.Check();
                    shown.Add(string.Join(",", t.Fx.Session.Findings.Where(x => x.Excerpt.StartsWith("Please")).Select(x => x.RuleId).ToArray()));
                    if (i == 1) t.Restart();
                }
                Assert.Equal(new[] { PleaseTwin, PleaseTwin, PleaseTwin }, shown.ToArray());        // not "whichever came first today"
            }
        }

        // ---- 24 · #127: twin rules and the glossary checker --------------------------------------------------------

        private const string SignOn = "company:terminology/6";   // Single Sign-On → single sign-on; example "Enable Single Sign-On first."
        private const string Example = "<h1>Tips</h1><p>Enable Single Sign-On first.</p>";

        /// <summary>A team whose guide also has a rule that matches only part of the words of /6, and a glossary with these terms.</summary>
        private static Team_ TeamWithTwins(string body, bool partlyOverlappingRule, params string[] glossary)
        {
            var t = Team_.OnTopic(body);
            if (partlyOverlappingRule)
                File.WriteAllText(Path.Combine(t.Guide, @"Content\Resources\StyleGuide\Company\Twinning.htm"),
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html><head><title>Twinning</title></head><body><h1>Twinning</h1>" +
                    "<table><tr><th>Avoid</th><th>Use</th><th>Severity</th><th>Why</th><th>Example</th></tr>" +
                    "<tr><td>Sign-On first</td><td>Sign-On</td><td>warning</td><td>Shorter.</td><td>Enable Single Sign-On first.</td></tr></table></body></html>");
            if (glossary.Length > 0)
            {
                var entries = string.Concat(glossary.Select(g => "<GlossaryEntry><Terms><Term>" + g + "</Term></Terms><Definition>A term.</Definition></GlossaryEntry>").ToArray());
                var file = Path.Combine(t.Guide, @"Project\Glossaries\Main.flglo");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "<CatapultGlossary>" + entries + "</CatapultGlossary>");
            }
            t.Restart();
            t.Fx.Check();
            return t;
        }

        private static bool IsGlossary(StyleFinding f) { return f.RuleId == null && f.Rule != null && f.Rule.EndsWith(".GlossaryTerms", StringComparison.Ordinal); }

        [Fact]
        public void Story24a_PartlyOverlappingTwin_IsOfferedAndTurnedOffTogether()
        {
            // #127: "Single Sign-On" and "Sign-On first" share words without one containing the other.
            using (var t = TeamWithTwins(Example, true))
            {
                var f = t.Fx.Session.Findings.Single(x => x.RuleId == SignOn);
                t.Fx.Session.Select(f);
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = Reason, AlsoOthers = true };

                t.Fx.Session.TurnOffRule();

                var twin = t.Fx.Host.Offered.Single(o => !o.IsChecker && o.Id.StartsWith("company:twinning/", StringComparison.Ordinal));
                var rows = GuideOverrides.Read(t.Guide).Select(o => o.RuleId).ToList();
                Assert.Contains(SignOn, rows);
                Assert.Contains(twin.Id, rows);
                t.Fx.Check();
                Assert.DoesNotContain(t.Fx.Session.Findings, x => x.RuleId == SignOn || x.RuleId == twin.Id);
                t.Restart();
                t.Fx.Check();
                Assert.DoesNotContain(t.Fx.Session.Findings, x => x.RuleId == SignOn || x.RuleId == twin.Id);
            }
        }

        [Fact]
        public void Story24b_RulesBrowserAndPaneOfferTheSameTwins()
        {
            // The glossary also flags words that are not in the rule's example ("Single Sign-On Setup", "Zelle Transfer").
            using (var t = TeamWithTwins("<h1>Tips</h1><p>Enable Single Sign-On first. Open Single Sign-On Setup. Send a Zelle Transfer now.</p>", true,
                                         "single sign-on", "single sign-on setup", "zelle transfer"))
            {
                var merged = t.Fx.Session.Findings.Where(x => x.RuleId != null && x.AlsoRules.Count > 0).ToList();
                Assert.NotEmpty(merged);
                foreach (var f in merged)
                {
                    var twins = t.Fx.Runner.TwinsOf(f.RuleId).Select(r => r.Id).ToList();
                    foreach (var also in f.AlsoRules) Assert.Contains(also.Id, twins);
                }

                t.Fx.Session.Select(t.Fx.Session.Findings.First(x => x.RuleId == SignOn));
                t.Fx.Host.RuleChoice = null;
                t.Fx.Session.TurnOffRule();

                var offered = t.Fx.Host.Offered.Select(o => o.Id).ToList();
                foreach (var twin in t.Fx.Runner.TwinsOf(SignOn)) Assert.Contains(twin.Id, offered);
                Assert.Contains(t.Fx.Host.Offered, o => o.IsChecker);
            }
        }

        [Fact]
        public void Story24c_GlossaryChecker_IsOfferedUnticked_DefaultsLeaveGlossaryIssuesElsewhere()
        {
            using (var t = TeamWithTwins("<h1>Tips</h1><p>Enable Single Sign-On first. Send a Zelle Transfer now.</p>", false, "single sign-on", "zelle transfer"))
            {
                t.Fx.Session.Select(t.Fx.Session.Findings.First(x => x.RuleId == SignOn));
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = Reason, AlsoOthers = true };   // the checker is not ticked

                t.Fx.Session.TurnOffRule();

                var checker = t.Fx.Host.Offered.Single(o => o.IsChecker);
                Assert.EndsWith(".GlossaryTerms", checker.Id);
                Assert.DoesNotContain(GuideOverrides.Read(t.Guide), o => o.RuleId == checker.Id);
                Assert.DoesNotContain(t.Fx.Session.Findings, x => x.RuleId == SignOn);
                Assert.Contains(t.Fx.Session.Findings, x => IsGlossary(x) && x.Excerpt.StartsWith("Zelle"));          // elsewhere
                Assert.Contains(t.Fx.Session.Findings, x => IsGlossary(x) && x.Excerpt.StartsWith("Single Sign-On"));   // the shared words stay flagged
                t.Fx.Check();
                Assert.Contains(t.Fx.Session.Findings, x => IsGlossary(x) && x.Excerpt.StartsWith("Single Sign-On"));
            }
        }

        [Fact]
        public void Story24d_TickedGlossaryChecker_ClearsEveryGlossaryIssueFromThePane()
        {
            using (var t = TeamWithTwins("<h1>Tips</h1><p>Enable Single Sign-On first. Send a Zelle Transfer now.</p>", false, "single sign-on", "zelle transfer"))
            {
                var before = t.Fx.Session.Findings.Count;
                t.Fx.Session.Select(t.Fx.Session.Findings.First(x => x.RuleId == SignOn));
                var checker = t.Fx.Runner.TwinsOf(SignOn).Single(r => r.IsChecker);
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = Reason, AlsoOthers = true, AlsoCheckers = new List<string> { checker.Id } };

                t.Fx.Session.TurnOffRule();

                Assert.Contains(GuideOverrides.Read(t.Guide), o => o.RuleId == checker.Id);
                Assert.DoesNotContain(t.Fx.Session.Findings, x => x.RuleId == SignOn || IsGlossary(x));
                Assert.True(t.Fx.Session.Findings.Count < before);
                Assert.Empty(t.Fx.Host.Errors);
                t.Fx.Check();
                Assert.DoesNotContain(t.Fx.Session.Findings, x => x.RuleId == SignOn || IsGlossary(x));
            }
        }

        // ---- 18 · TEST-PLAN D6 ------------------------------------------------------------------------------------

        [Fact]
        public void Story18_WriterIgnoresARuleForThisProjectOnly_TheRowIsInTheProject_AnotherTopicStopsFlaggingIt_AnotherProjectDoesNot()
        {
            using (var t = Team_.Legacy(topic: WorkOrderTopic))
            {
                var other = Path.Combine(t.Ws.Root, @"Other\legacy");                         // a second project with the same content
                Files.Copy(t.Ws.ProjectDir, other);
                Assert.True(t.Flags(InOrderTo));
                t.Choose(InOrderTo);
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Dispatchers keep in order to." };

                t.Fx.Session.TurnOffRule();

                var ini = t.Ws.PathOf(@"Content\Resources\StyleGuide\overrides.ini");
                var lines = File.ReadAllLines(ini);
                Assert.Contains("# by " + Pat + " on " + DateTime.Now.ToString("yyyy-MM-dd") + ": Dispatchers keep in order to.", lines);
                Assert.Contains(InOrderTo + " = off", lines);
                Assert.Contains("for this project", t.Fx.Session.Status);
                Assert.DoesNotContain(GuideOverrides.Read(t.Guide), o => o.RuleId == InOrderTo);                                   // the team's guide is not touched

                Assert.False(t.Flags(InOrderTo));                                              // this topic
                var sibling = t.Ws.Open("Content/WordImport/ReopenAClosedWorkOrderv5.htm");
                var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(t.Fx.Style, sibling.SourcePath), 16000, t.Fx.Style, sibling.SourcePath) { VariableValue = sibling.VariableValue };
                var inSibling = runner.Lint(runner.PickCheckBlocks(sibling));
                Assert.DoesNotContain(inSibling, f => f.RuleId == InOrderTo);                  // another topic of the project
                Assert.Contains(inSibling, f => f.RuleId == Please);      // …but not every rule

                var elsewhere = t.Ws.Open(Path.Combine(other, @"Content\WordImport\ReopenAClosedWorkOrderv5.htm"));
                var otherRunner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(t.Fx.Style, elsewhere.SourcePath), 16000, t.Fx.Style, elsewhere.SourcePath) { VariableValue = elsewhere.VariableValue };
                Assert.Contains(otherRunner.Lint(otherRunner.PickCheckBlocks(elsewhere)), f => f.RuleId == InOrderTo);   // a different project still has it
            }
        }

        // ---- 19 · TEST-PLAN D6 ------------------------------------------------------------------------------------

        [Fact]
        public void Story19_WriterReviewsTheTurnedOffRules_SeesReasonAndSource_TurnsOneBackOn_AndItIsFlaggedAgain()
        {
            using (var t = Team_.Legacy(topic: WorkOrderTopic))
            {
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Team: " + Reason };
                t.Fx.Check(); t.Fx.Session.Select(t.Fx.Find(EMail)); t.Fx.Session.TurnOffRule();
                t.Fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Project: dispatchers keep in order to." };
                t.Fx.Check(); t.Fx.Session.Select(t.Fx.Find(InOrderTo)); t.Fx.Session.TurnOffRule();

                Func<List<RuleOverride>> view = () =>
                {
                    var rows = RuleOverrides.InEffect(new[] { t.Guide }, null, g => "Team style guide");
                    rows.AddRange(RuleOverrides.Read(t.Ws.ProjectDir, "This project"));
                    return rows.OrderBy(r => r.RuleId, StringComparer.Ordinal).ToList();
                };
                var listed = view().Where(r => r.By == Pat).ToList();   // the guide also lists its shipped defaults

                Assert.Equal(new[] { InOrderTo, EMail }, listed.Select(r => r.RuleId).ToArray());
                Assert.Equal(new[] { "This project", "Team style guide" }, listed.Select(r => r.Scope).ToArray());
                Assert.Equal(new[] { "Project: dispatchers keep in order to.", "Team: " + Reason }, listed.Select(r => r.Note).ToArray());
                Assert.All(listed, r => Assert.Equal(Pat, r.By));
                Assert.All(listed, r => Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), r.Date));
                Assert.False(t.Flags(EMail));
                Assert.False(t.Flags(InOrderTo));

                foreach (var row in listed) RuleOverrides.TurnBackOn(row, Team.Load(t.Guide, null, Pat));

                Assert.DoesNotContain(view(), r => r.By == Pat);
                Assert.False(File.ReadAllText(t.Ws.PathOf(@"Content\Resources\StyleGuide\overrides.ini")).Contains(InOrderTo + " = off"));
                Assert.True(t.Flags(EMail));
                Assert.True(t.Flags(InOrderTo));
            }
        }

        // ---- 20 · TEST-PLAN D5 ------------------------------------------------------------------------------------

        private static string[] RequestFiles(string feedback)
        {
            var dir = Path.Combine(feedback, "requests");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "r-*.json") : new string[0];
        }

        [Fact]
        public void Story20a_WriterWhoIsNotAnOwnerTurnsARuleOff_NothingChanges_AndARequestLandsInTheFeedbackFolder()
        {
            using (var t = Team_.Legacy(Pat, new[] { Lead }, feedbackFolder: true))
            {
                var before = Files.Snapshot(t.Guide);
                t.Fx.Check();
                var f = t.Choose(EMail);
                Assert.Equal("Request Rule Change…", t.Fx.Session.RuleChangeLabel());
                t.Fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Our readers write e-mail." };

                t.Fx.Session.TurnOffRule();

                Assert.Equal(before, Files.Snapshot(t.Guide));                                  // the guide is as it was
                Assert.False(File.Exists(t.Ws.PathOf(@"Content\Resources\StyleGuide\overrides.ini")));
                Assert.Contains(f, t.Fx.Session.Findings);                                       // still listed, marked as asked about
                Assert.True(f.RequestSent);
                var file = Assert.Single(RequestFiles(t.Feedback));
                var request = GuideRequests.Load(Team.Load(t.Guide, t.Feedback, Lead)).Single();
                Assert.Equal(RequestKinds.TurnOff, request.Kind);
                Assert.Equal(EMail, request.RuleId);
                Assert.Equal("Our readers write e-mail.", request.Reason);
                Assert.Contains("Request sent to the style guide owners (" + Lead + ")", t.Fx.Session.Status);
                Assert.Empty(t.Fx.Host.Errors);
                Assert.True(t.Flags(EMail));                                                      // nothing was turned off
            }
        }

        [Fact]
        public void Story20b_NoTeamFeedbackFolder_TheRequestIsKept_AndTheWriterIsToldWhyItWasNotSent()
        {
            using (var t = Team_.Legacy(Pat, new[] { Lead }, feedbackFolder: false))
            {
                t.Fx.Check();
                t.Choose(EMail);
                t.Fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Our readers write e-mail." };

                t.Fx.Session.TurnOffRule();

                Assert.Contains("no team feedback folder is set in Settings", t.Fx.Host.Errors.Single());
                Assert.Contains("Request saved. It's sent the next time you lint.", t.Fx.Session.Status);
                var kept = GuideRequests.Load(Team.Load(t.Guide, null, Pat)).Single();
                Assert.True(kept.Unsent);                                                         // the reason is not lost
                Assert.Equal("Our readers write e-mail.", kept.Reason);
            }
        }

        [Fact]
        public void Story20c_TheFeedbackFolderCannotBeWritten_TheErrorNamesTheFolder_AndTheRequestIsKept()
        {
            using (var t = Team_.Legacy(Pat, new[] { Lead }, feedbackFolder: true))
            {
                t.Ws.MakeReadOnly(t.Feedback);
                t.Fx.Check();
                t.Choose(EMail);
                t.Fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Our readers write e-mail." };

                t.Fx.Session.TurnOffRule();

                Assert.Contains(Path.Combine(t.Feedback, "requests"), t.Fx.Host.Errors.Single());
                Assert.Empty(RequestFiles(t.Feedback));
                Assert.True(GuideRequests.Load(Team.Load(t.Guide, t.Feedback, Pat)).Single().Unsent);
            }
        }

        // ---- 21 · TEST-PLAN D6, #36 -------------------------------------------------------------------------------

        [Fact]
        public void Story21_ReadOnlyInstallFolderAndNoTeamGuide_TheChangeGoesToTheWritersOwnGuide_AndAFailedWriteNamesItsTarget()
        {
            // #36: "Add to guide" / "Turn off" wrote under Program Files and failed for standard users.
            using (var ws = FakeWorkspace.Empty())
            {
                ws.WriteFile("Content/topic.htm", string.Format(ReviewFixture.Wrapper, "<h1>Help</h1><p>You can utilize the data in order to see it.</p>"));
                var pluginDir = Path.Combine(ws.Root, @"Plugins\FlareAI");
                Files.Copy(Repo.StyleGuide, PluginSettings.BundledGuide(pluginDir));
                ws.MakeReadOnly(pluginDir);                                                   // like Program Files for a standard user
                var installed = Files.Snapshot(pluginDir);
                var settings = new PluginSettings();
                var mine = PluginSettings.UserGuidePath;
                Assert.StartsWith(ws.AppData, mine);
                Func<StyleEngine> engine = () => new StyleEngine(() => settings.GuideRoots(pluginDir), PluginSettings.BundledGuide(pluginDir));
                Func<Team> team = () => { var x = Team.Load(settings.GuideRoot(pluginDir), null, Pat); x.SeedFrom = settings.ReadOnlyGuide(pluginDir); return x; };
                using (var fx = new ReviewFixture(null, "Content/topic.htm", null, false, ws, engine))
                {
                    fx.Team = team();
                    fx.Check();
                    fx.Session.Select(fx.Find("company:plain-language/1"));
                    fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Our readers know it." };

                    fx.Session.TurnOffRule();

                    Assert.Empty(fx.Host.Errors);
                    Assert.EndsWith("Rule disabled.", fx.Session.Status);   // where it went is in the log, not the status
                    Assert.True(GuideSeed.IsPersonal(mine));
                    Assert.Equal("company:plain-language/1", GuideOverrides.Read(mine).Single().RuleId);   // in the writer's own guide under AppData
                    Assert.Equal(installed, Files.Snapshot(pluginDir));                                    // the installed guide is untouched
                    fx.Check();
                    Assert.DoesNotContain(fx.Session.Findings, x => x.RuleId == "company:plain-language/1");
                    fx.Restart(team);
                    fx.Check();
                    Assert.DoesNotContain(fx.Session.Findings, x => x.RuleId == "company:plain-language/1");
                    Assert.Contains(fx.Session.Findings, x => x.RuleId == "company:plain-language/2");     // other rules still apply

                    // The writer's guide stops being writable: the error names where it tried, and nothing is reported as changed.
                    ws.MakeReadOnly(mine);
                    var rowsBefore = GuideOverrides.Read(mine).Count;
                    var second = fx.Find("company:plain-language/2");
                    fx.Session.Select(second);
                    fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Also fine." };

                    fx.Session.TurnOffRule();

                    var error = fx.Host.Errors.Single();
                    Assert.Contains(mine, error);
                    Assert.Contains("Nothing was changed", error);
                    Assert.Contains("The rule was not changed.", fx.Session.Status);
                    Assert.Contains(second, fx.Session.Findings);
                    Assert.Equal(rowsBefore, GuideOverrides.Read(mine).Count);
                    fx.Check();
                    Assert.Contains(fx.Session.Findings, x => x.RuleId == "company:plain-language/2");
                    Assert.Equal(installed, Files.Snapshot(pluginDir));
                }
            }
        }

        // ---- 22 · TEST-PLAN D6 ------------------------------------------------------------------------------------

        [Fact]
        public void Story22_CompanyGuideSaysLogInIsFine_NoGoogleHitOnIt_AndThePrecedenceSurvivesARestart()
        {
            // localized-style ships a Company guide whose Google-overrides turn off the "log in" rows (word-list 443, 637, 638).
            var logIn = new[] { "google:word-list/443", "google:word-list/637", "google:word-list/638" };
            const string topic = "Content/getting-started/log-in-to-the-console.htm";
            using (var ws = ReviewFixture.Corpus("v2/localized-style"))
            {
                var companyRules = Path.Combine(ws.ProjectDir, @"Content\Resources\StyleGuide\Company");
                var guide = Files.TeamGuide(ws.Root, companyRules);
                using (var fx = new ReviewFixture(null, topic, null, false, ws, () => new StyleEngine(new[] { guide }, Repo.StyleGuide)))
                {
                    Assert.Contains("log in", fx.Doc.BodyText);

                    // Without the company guide the bundled Google rules flag "log in" (the control).
                    Assert.Contains(LintWith(new StyleEngine(new string[0], Repo.StyleGuide), fx), f => logIn.Contains(f.RuleId));

                    // With it, the company's rows win: no Google hit on "log in".
                    Assert.DoesNotContain(LintWith(fx.Style, fx), f => logIn.Contains(f.RuleId));
                    Assert.True(fx.Style.IsOff(null, "google:word-list/637"));

                    // After a restart nothing changed.
                    fx.Restart();
                    Assert.DoesNotContain(LintWith(fx.Style, fx), f => logIn.Contains(f.RuleId));
                    Assert.True(fx.Style.IsOff(null, "google:word-list/637"));
                    Assert.Contains(fx.Style.Rules(null), r => r.Id.StartsWith("company:terminology/"));   // the company rules sit over Google
                }
            }
        }

        [Fact]
        public void Story16b_OwnerTurnsARuleOffInAGuideFolderThatIsNotAFlareProject_ItTakesEffectWithoutARestart()
        {
            using (var ws = FakeWorkspace.Empty())
            {
                var guide = Path.Combine(ws.Root, "PlainGuide");
                Files.Copy(CompanyFolder, Path.Combine(guide, "Company"));
                var engine = new StyleEngine(new[] { guide }, Repo.StyleGuide);
                Assert.False(engine.IsOff(null, EMail));

                GuideOverrides.Set(guide, EMail, "off", Reason, Pat, DateTime.Now);   // GuideOverrides reads and writes this layout fine

                Assert.True(engine.IsOff(null, EMail));                                // …but the engine in the running plugin never reloads it
            }
        }

        /// <summary>Lint the open topic with these guides alone (no project layer).</summary>
        private static List<StyleFinding> LintWith(StyleEngine engine, ReviewFixture fx)
        {
            var runner = new ActionRunner(fx.Llm, PromptBuilder.For(engine, null), 16000, engine, null) { VariableValue = fx.Doc.VariableValue };
            return runner.Lint(runner.PickCheckBlocks(fx.Doc));
        }
    }
}
