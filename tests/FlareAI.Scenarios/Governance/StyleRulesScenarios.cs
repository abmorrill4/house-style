using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Governance
{
    /// <summary>
    /// The Style rules view (#99), driven through the same Core paths its buttons use (<see cref="RuleCatalog"/> and
    /// <see cref="RuleChanges"/>): owners turn rules off, change their level and turn them back on; a writer on a read-only
    /// team guide sends requests; a shipped default shows as Default and is turned back on; a project exception goes to the
    /// owners; everything survives a restart. After every change, Check style (the review pane) and Check project must
    /// agree with what the catalogue says.
    /// </summary>
    [Collection("Core statics")]
    public class StyleRulesScenarios
    {
        private const string Lead = "lead@example.com", Pat = "pat@example.com";
        private const string Topic = "Content/rules.htm";
        private const string PleaseText = "Please note that the link expires.";

        private sealed class Fixture : IDisposable
        {
            public readonly FakeWorkspace Ws = FakeWorkspace.Empty("Docs");
            public readonly string Guide, Feedback;
            public CatalogRule Target;   // a Google rule that fires on its own in the topic

            public Fixture()
            {
                Guide = Path.Combine(Ws.Root, "TeamGuide");
                Feedback = Path.Combine(Ws.Root, "Feedback");
                Directory.CreateDirectory(Feedback);
                Copy(Path.Combine(Repo.StyleGuide, @"Content\Resources\StyleGuide\Company"), Path.Combine(Guide, "Company"));
                Directory.CreateDirectory(Path.Combine(Guide, "FlareAI"));
                File.WriteAllText(Path.Combine(Guide, @"FlareAI\owners.txt"), Lead + "\nbackup@example.com\n");
                var style = Style();
                var comp = style.Compose(null);
                Target = RuleCatalog.Build(comp).Rules
                    .Where(r => r.Source == "Google" && r.IsTable && !r.IsPattern && r.Action == "replace" && r.State == RuleState.On && r.DefaultLevel == "warning" &&
                                r.Content.Count == 0 && r.Products.Count == 0 && r.Overlaps.Count == 0 && !string.IsNullOrEmpty(r.Example) && r.Example.IndexOf('<') < 0 && r.Example.IndexOf('&') < 0)
                    .First(r => comp.Lint(new[] { r.Example }, null).Any(h => h.RuleId == r.Id && h.AlsoRules.Count == 0));
                Ws.WriteFile(Topic, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head><title>Rules</title></head>" +
                                    "<body><h1>Rules</h1><p>" + Target.Example + "</p><p>" + PleaseText + "</p></body></html>");
            }

            public Team As(string who) { return Team.Load(Guide, Feedback, who); }

            /// <summary>A new engine each time, as after a restart (the cache would reload on a file change anyway).</summary>
            public StyleEngine Style() { return new StyleEngine(new[] { Guide }, Repo.StyleGuide); }

            public string TopicPath { get { return Ws.PathOf(Topic); } }

            public string Overrides { get { return Ws.PathOf(@"Content\Resources\StyleGuide\overrides.ini"); } }

            /// <summary>The Style rules view as <paramref name="team"/> opens it: the guide with the open project, and the requests.</summary>
            public RuleCatalog Catalog(Team team, bool withProject = true)
            {
                return RuleCatalog.Build(Style().Compose(withProject ? TopicPath : null), null, GuideRequests.Load(team), r => GuideRequests.IsMine(team, r));
            }

            private ActionRunner Runner(StyleEngine style) { return new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(style, TopicPath), 16000, style, TopicPath); }

            /// <summary>Check style on the open topic, as the review pane runs it.</summary>
            public List<StyleFinding> CheckStyle() { var style = Style(); return Runner(style).CheckStyle(Ws.Open(Topic)); }

            /// <summary>Check project: every topic of the project, no AI.</summary>
            public List<StyleFinding> CheckProject() { var style = Style(); return Runner(style).LintFiles(ActionRunner.ProjectFiles(TopicPath)); }

            /// <summary>The review pane over the open topic, for this writer, with these findings.</summary>
            public ReviewSession Pane(Team team, List<StyleFinding> findings)
            {
                var style = Style();
                var s = new ReviewSession(Runner(style), Ws.Open(Topic), new NoDialogs(), null, false) { CurrentTeam = () => team };
                s.Add(findings);
                return s;
            }

            public string[] RequestFiles() { return Directory.Exists(Path.Combine(Feedback, "requests")) ? Directory.GetFiles(Path.Combine(Feedback, "requests"), "r-*.json").Where(f => !f.EndsWith(".decision.json")).ToArray() : new string[0]; }

            public void Dispose() { Ws.Dispose(); }
        }

        /// <summary>
        /// The review pane, Check project and the catalogue agree: every finding's rule is listed and not off, at the level
        /// the catalogue shows; <paramref name="key"/> is flagged exactly when the catalogue says it applies.
        /// </summary>
        private static void Agree(Fixture fx, Team team, string key, bool flagged, string level = null)
        {
            var cat = fx.Catalog(team);
            foreach (var findings in new[] { fx.CheckStyle(), fx.CheckProject() })
            {
                foreach (var f in findings.Where(f => f.RuleId != null))
                {
                    var r = cat.Find(f.RuleId);
                    Assert.True(r != null, f.RuleId + " is not in the catalogue");
                    Assert.NotEqual(RuleState.Off, r.State);
                    if (f.Source == "lint" && r.IsTable) Assert.Equal(r.Level, f.Severity);
                }
                Assert.Equal(flagged, findings.Any(f => f.RuleId == key));
                if (flagged && level != null) Assert.Equal(level, findings.First(f => f.RuleId == key).Severity);
            }
            Assert.Equal(flagged, cat.Find(key).State != RuleState.Off);
        }

        private static RuleSettingChoice Choice(bool team, string setting, string reason, bool others = false)
        {
            return new RuleSettingChoice { ForTeam = team, Setting = setting, Reason = reason, AlsoOthers = others };
        }

        // ---- owners --------------------------------------------------------------------------------------------

        [Fact]
        public void Owner_TurnsARuleOff_ChangesItsLevel_AndTurnsItBackOn_AndTheChecksAgreeEachTime()
        {
            using (var fx = new Fixture())
            {
                var owner = fx.As(Lead);
                var key = fx.Target.Key;
                Agree(fx, owner, key, true, "warning");
                Assert.False(RuleChanges.WillRequest(owner, fx.Target, false));

                var off = RuleChanges.Change(owner, fx.Catalog(owner).Find(key), Choice(true, "off", "Not for our members."), null, fx.TopicPath);
                Assert.False(off.Requested);
                Assert.StartsWith(fx.Guide, off.File);
                Assert.Empty(fx.RequestFiles());
                var rule = fx.Catalog(owner).Find(key);
                Assert.Equal(RuleState.Off, rule.State);
                Assert.Equal(Lead, rule.Change.By);
                Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), rule.Change.Date);
                Assert.Equal("Not for our members.", rule.Change.Note);
                Assert.Equal("Team style guide (TeamGuide)", rule.Change.Scope);
                Agree(fx, owner, key, false);

                RuleChanges.Change(owner, rule, Choice(true, "error", "Stricter."), null, fx.TopicPath);
                rule = fx.Catalog(owner).Find(key);
                Assert.Equal(RuleState.LevelChanged, rule.State);
                Assert.Equal("Error", rule.LevelLabel);
                Agree(fx, owner, key, true, "error");

                var back = RuleChanges.Restore(owner, rule, null);
                Assert.False(back.Requested);
                rule = fx.Catalog(owner).Find(key);
                Assert.Equal(RuleState.On, rule.State);
                Assert.Null(rule.Change);
                Agree(fx, owner, key, true, "warning");
            }
        }

        [Fact]
        public void Owner_DisablesARuleInTheRulesBrowser_WhileTheTeamFolderIsMissing_NothingIsWritten_AndTheFolderIsNotRecreated()
        {
            // #235: the team's folder went offline after the rules browser opened. Writing would leave a near-empty guide in its place.
            using (var fx = new Fixture())
            {
                var owner = fx.As(Lead);
                var rule = fx.Catalog(owner).Find(fx.Target.Key);
                var away = fx.Guide + "-offline";
                Directory.Move(fx.Guide, away);

                var ex = Assert.Throws<GuideWriteException>(() => RuleChanges.Change(owner, rule, Choice(true, "off", "Not for our members."), null, fx.TopicPath));
                Assert.StartsWith(GuideWriteException.UnreachableMessage, ex.Message);
                Assert.Contains(fx.Guide, ex.Message);
                Assert.False(Directory.Exists(fx.Guide));
                Assert.Empty(fx.RequestFiles());
                Directory.Move(away, fx.Guide);
                Assert.Equal(RuleState.On, fx.Catalog(owner).Find(fx.Target.Key).State);
            }
        }

        [Fact]
        public void Owner_TurnsADefaultBackOn_WhileTheTeamFolderIsMissing_NothingIsWritten_AndTheFolderIsNotRecreated()
        {
            using (var fx = new Fixture())
            {
                // The team's copy has no rows of its own, so a shipped default (a Google rule off by default) is in effect.
                File.Delete(Path.Combine(fx.Guide, @"Company\Google-overrides.htm"));
                var owner = fx.As(Lead);
                var rule = fx.Catalog(owner).Rules.First(r => r.State == RuleState.Off && r.Change != null && r.Change.IsDefault);
                var away = fx.Guide + "-offline";
                Directory.Move(fx.Guide, away);

                var ex = Assert.Throws<GuideWriteException>(() => RuleChanges.Restore(owner, rule, null));
                Assert.StartsWith(GuideWriteException.UnreachableMessage, ex.Message);
                Assert.False(Directory.Exists(fx.Guide));
                Directory.Move(away, fx.Guide);
                Assert.Equal(RuleState.Off, fx.Catalog(owner).Find(rule.Key).State);
            }
        }

        [Fact]
        public void Owner_TurnsOffARule_WithTheRulesOnTheSameWords_AndNoneOfThemFlagsThoseWords()
        {
            using (var fx = new Fixture())
            {
                var owner = fx.As(Lead);
                var cat = fx.Catalog(owner);
                var shown = fx.CheckStyle().Single(f => f.Excerpt != null && PleaseText.StartsWith(f.Excerpt, StringComparison.OrdinalIgnoreCase) && f.Excerpt.Length > 6);
                Assert.NotEmpty(shown.AlsoRules);
                var rule = cat.Find(shown.RuleId);
                var others = cat.OverlapsOf(rule);
                foreach (var also in shown.AlsoRules) Assert.Contains(others, o => o.Key == also.Id);   // the view offers what the pane offers

                var result = RuleChanges.Change(owner, rule, Choice(true, "off", "We say please.", others: true), others, fx.TopicPath);

                Assert.Equal(1 + others.Count, result.Changed.Count);
                Assert.Null(result.Incomplete);
                cat = fx.Catalog(owner);
                Assert.Equal(RuleState.Off, cat.Find(rule.Key).State);
                foreach (var o in others) Assert.Equal(RuleState.Off, cat.Find(o.Key).State);
                foreach (var findings in new[] { fx.CheckStyle(), fx.CheckProject() })
                    Assert.DoesNotContain(findings, f => f.RuleId == rule.Key || others.Any(o => f.RuleId == o.Key || f.AlsoRules.Any(a => a.Id == o.Key)));
                Agree(fx, owner, rule.Key, false);
            }
        }

        [Fact]
        public void Owner_TurnsBackOn_ARuleWithTheRulesTurnedOffWithIt()
        {
            using (var fx = new Fixture())
            {
                var owner = fx.As(Lead);
                var cat = fx.Catalog(owner);
                var shown = fx.CheckStyle().Single(f => f.Excerpt != null && PleaseText.StartsWith(f.Excerpt, StringComparison.OrdinalIgnoreCase) && f.Excerpt.Length > 6);
                var rule = cat.Find(shown.RuleId);
                var others = cat.OverlapsOf(rule).Where(o => o.State != RuleState.Off).ToList();
                Assert.NotEmpty(others);
                RuleChanges.Change(owner, rule, Choice(true, "off", "We say please.", others: true), others, fx.TopicPath);

                cat = fx.Catalog(owner);
                var companions = RuleChanges.CompanionsOf(cat, cat.Find(rule.Key));
                Assert.Equal(others.Select(o => o.Key).OrderBy(k => k).ToArray(), companions.Select(o => o.Key).OrderBy(k => k).ToArray());

                var back = RuleChanges.Restore(owner, cat.Find(rule.Key), companions, null);

                Assert.Equal(1 + others.Count, back.Changed.Count);
                cat = fx.Catalog(owner);
                Assert.NotEqual(RuleState.Off, cat.Find(rule.Key).State);
                foreach (var o in others) Assert.NotEqual(RuleState.Off, cat.Find(o.Key).State);
            }
        }

        // ---- writers on a read-only guide ----------------------------------------------------------------------

        [Fact]
        public void Writer_OnAReadOnlyGuide_SendsRequests_NothingIsWritten_UntilAnOwnerApproves()
        {
            using (var fx = new Fixture())
            {
                fx.Ws.MakeReadOnly(fx.Guide);
                var before = Snapshot(fx.Guide);
                var writer = fx.As(Pat);
                var key = fx.Target.Key;
                Assert.True(RuleChanges.WillRequest(writer, fx.Target, false));

                Assert.Throws<ArgumentException>(() => RuleChanges.Change(writer, fx.Catalog(writer).Find(key), Choice(true, "off", "  "), null, fx.TopicPath));
                Assert.Empty(fx.RequestFiles());

                var sent = RuleChanges.Change(writer, fx.Catalog(writer).Find(key), Choice(true, "off", "Members say it."), null, fx.TopicPath);

                Assert.True(sent.Requested);
                Assert.Null(sent.Problem);
                Assert.Equal(RequestKinds.TurnOff, sent.Request.Kind);
                Assert.Single(fx.RequestFiles());
                Assert.Equal(before, Snapshot(fx.Guide));   // no write was attempted
                var rule = fx.Catalog(writer).Find(key);
                Assert.Equal(RuleState.On, rule.State);       // still on while the owners decide…
                Assert.Equal(sent.Request.Id, rule.OpenRequest.Id);
                Assert.True(rule.RequestIsMine);
                Agree(fx, writer, key, true, "warning");      // …and still flagged, marked "Request sent" in the pane
                var findings = fx.CheckStyle();
                var pane = fx.Pane(writer, findings);
                Assert.Null(pane.CheckRequests());
                Assert.True(findings.Single(f => f.RuleId == key).RequestSent);

                // A level change is a request too.
                var level = RuleChanges.Change(writer, rule, Choice(true, "suggestion", "Make it a tip."), null, fx.TopicPath);
                Assert.Equal(RequestKinds.Severity, level.Request.Kind);
                Assert.Equal(before, Snapshot(fx.Guide));

                // The owner (read-write) approves the turn-off and declines the level.
                fx.Ws.ClearReadOnly();
                var owner = fx.As(Lead);
                var requests = GuideRequests.Load(owner);
                Assert.Null(GuideRequests.Approve(owner, requests.Single(r => r.Id == sent.Request.Id), null, (r, d) => GuideRequests.ApplyToGuide(owner, r, d)));
                Assert.Null(GuideRequests.Decline(owner, requests.Single(r => r.Id == level.Request.Id), "Off is enough."));

                rule = fx.Catalog(writer).Find(key);
                Assert.Equal(RuleState.Off, rule.State);
                Assert.Equal(Lead, rule.Change.By);
                Assert.Contains(sent.Request.Id, rule.Change.Note);
                Assert.Null(rule.OpenRequest);
                Agree(fx, writer, key, false);

                // Turning it back on is a request as well; approved, the rule applies again.
                fx.Ws.MakeReadOnly(fx.Guide);
                var on = RuleChanges.Restore(writer, rule, "We need it after all.");
                Assert.True(on.Requested);
                Assert.Equal(RequestKinds.TurnOn, on.Request.Kind);
                Assert.Equal(RuleState.Off, fx.Catalog(writer).Find(key).State);
                fx.Ws.ClearReadOnly();
                owner = fx.As(Lead);
                Assert.Null(GuideRequests.Approve(owner, GuideRequests.Load(owner).Single(r => r.Id == on.Request.Id), null, (r, d) => GuideRequests.ApplyToGuide(owner, r, d)));
                Assert.Equal(RuleState.On, fx.Catalog(writer).Find(key).State);
                Agree(fx, writer, key, true, "warning");
            }
        }

        [Fact]
        public void ProjectException_FromAWriter_GoesToTheOwners_AndLandsInTheProjectWhenApproved()
        {
            using (var fx = new Fixture())
            {
                fx.Ws.MakeReadOnly(fx.Guide);
                var writer = fx.As(Pat);
                var key = fx.Target.Key;

                var sent = RuleChanges.Change(writer, fx.Catalog(writer).Find(key), Choice(false, "off", "Legal copy."), null, fx.TopicPath);

                Assert.True(sent.Requested);
                Assert.Equal(RequestKinds.ProjectException, sent.Request.Kind);
                Assert.Equal("Docs", sent.Request.Project);
                Assert.False(File.Exists(fx.Overrides));
                Agree(fx, writer, key, true);

                fx.Ws.ClearReadOnly();
                var owner = fx.As(Lead);
                var guideBefore = Snapshot(fx.Guide);
                Assert.Null(GuideRequests.Approve(owner, GuideRequests.Load(owner).Single(), new RequestDecision { Note = "Fine for legal." }, (r, d) => GuideRequests.ApplyToGuide(owner, r, d)));
                Assert.Equal(guideBefore, Snapshot(fx.Guide));   // the team rule stays on

                // The writer's next check (the review pane) writes the exception into their project.
                Assert.Contains("approved", fx.Pane(writer, fx.CheckStyle()).CheckRequests());
                var rule = fx.Catalog(writer).Find(key);
                Assert.Equal(RuleState.Off, rule.State);
                Assert.True(rule.ChangeIsProject);
                Assert.Equal("Project Docs", rule.Change.Scope);
                Assert.Equal(sent.Request.Id, rule.Change.RequestId);
                Assert.Equal(Lead, rule.Change.By);
                Agree(fx, writer, key, false);
                Assert.Equal(RuleState.On, fx.Catalog(writer, withProject: false).Find(key).State);

                // The project's own exception: the writer can lift it without asking (as in Turned-off rules).
                Assert.False(RuleChanges.WillRequest(writer, rule, true));
                var lifted = RuleChanges.Restore(writer, rule, null);
                Assert.False(lifted.Requested);
                Agree(fx, writer, key, true, "warning");
            }
        }

        [Fact]
        public void Owner_WritesAProjectException_Directly_AndCanChangeItsLevelLater()
        {
            using (var fx = new Fixture())
            {
                var owner = fx.As(Lead);
                var key = fx.Target.Key;

                RuleChanges.Change(owner, fx.Catalog(owner).Find(key), Choice(false, "off", "Legal copy."), null, fx.TopicPath);
                Assert.Contains(key + " = off", File.ReadAllText(fx.Overrides));
                Agree(fx, owner, key, false);

                // Off, then a level, then off again in the same project: the line in effect is always the last choice.
                RuleChanges.Change(owner, fx.Catalog(owner).Find(key), Choice(false, "error", "Stricter here."), null, fx.TopicPath);
                Agree(fx, owner, key, true, "error");
                RuleChanges.Change(owner, fx.Catalog(owner).Find(key), Choice(false, "off", "Off again."), null, fx.TopicPath);
                Agree(fx, owner, key, false);
                Assert.Single(RuleOverrides.ReadIni(fx.Overrides), o => o.RuleId == key);
                Assert.Equal(RuleState.On, fx.Catalog(owner, withProject: false).Find(key).State);
            }
        }

        // ---- defaults ------------------------------------------------------------------------------------------

        [Fact]
        public void AShippedDefault_ShowsAsDefault_TurnsBackOn_AndReturnsToTheDefault()
        {
            const string ThisPage = "google:word-list/221";
            using (var ws = FakeWorkspace.Empty())
            {
                ws.WriteFile(Topic, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head><title>Help</title></head>" +
                                    "<body><h1>Help</h1><p>Read this page for details.</p></body></html>");
                var pluginDir = Path.Combine(ws.Root, @"Plugins\FlareAI");
                var bundled = PluginSettings.BundledGuide(pluginDir);
                foreach (var f in Directory.GetFiles(Repo.StyleGuide, "*", SearchOption.AllDirectories))
                {
                    var to = bundled + f.Substring(Repo.StyleGuide.Length);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(f, to);
                }
                ws.MakeReadOnly(pluginDir);   // as installed for a standard user
                var settings = new PluginSettings();
                var engine = new StyleEngine(() => settings.GuideRoots(pluginDir), bundled);
                Func<string, bool> isDefault = r => PluginSettings.IsUnder(r, pluginDir);
                var team = Team.Load(settings.GuideRoot(pluginDir), null, Pat);
                team.SeedFrom = settings.ReadOnlyGuide(pluginDir);
                var path = ws.PathOf(Topic);
                Func<RuleCatalog> catalog = () => RuleCatalog.Build(engine.Compose(path), isDefault);
                Func<bool> flagged = () =>
                {
                    var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(engine, path), 16000, engine, path);
                    var style = runner.CheckStyle(ws.Open(Topic)).Any(f => f.RuleId == ThisPage);
                    Assert.Equal(style, runner.LintFiles(ActionRunner.ProjectFiles(path)).Any(f => f.RuleId == ThisPage));
                    return style;
                };

                var rule = catalog().Find(ThisPage);
                Assert.Equal(RuleState.Off, rule.State);
                Assert.True(rule.IsDefault);
                Assert.Equal("Disabled (Default)", rule.StateLabel);
                Assert.Equal(RuleOverrides.DefaultScope, rule.Change.Scope);
                Assert.False(flagged());
                Assert.False(RuleChanges.WillRequest(team, rule, true));

                var on = RuleChanges.Restore(team, rule, null);   // Turn back on: an "on" row in the writer's own guide
                Assert.StartsWith(PluginSettings.UserGuidePath, on.File);
                rule = catalog().Find(ThisPage);
                Assert.Equal(RuleState.On, rule.State);
                Assert.True(rule.Change.IsTurnOn);
                Assert.Equal("My style guide", rule.Change.Scope);
                Assert.True(flagged());

                RuleChanges.Restore(team, rule, null);           // Use the default: the row goes, the default applies
                rule = catalog().Find(ThisPage);
                Assert.Equal(RuleState.Off, rule.State);
                Assert.True(rule.IsDefault);
                Assert.False(flagged());
            }
        }

        // ---- restart ---------------------------------------------------------------------------------------------

        [Fact]
        public void Changes_AndRequests_SurviveARestart()
        {
            using (var fx = new Fixture())
            {
                var key = fx.Target.Key;
                var owner = fx.As(Lead);
                RuleChanges.Change(owner, fx.Catalog(owner).Find(key), Choice(true, "suggestion", "A tip is enough."), null, fx.TopicPath);
                fx.Ws.MakeReadOnly(fx.Guide);
                fx.Ws.MakeReadOnly(fx.Feedback);   // the requests folder is offline: the request waits on this machine
                var writer = fx.As(Pat);
                var please = fx.Catalog(writer).Rules.First(r => r.Avoid.Contains("please note that"));
                var waiting = RuleChanges.Change(writer, please, Choice(true, "off", "We say please."), null, fx.TopicPath);
                Assert.True(waiting.Requested);
                Assert.NotNull(waiting.Problem);

                // Restart: new engine, new team context, nothing in memory.
                var again = fx.As(Pat);
                var cat = fx.Catalog(again);
                var rule = cat.Find(key);
                Assert.Equal(RuleState.LevelChanged, rule.State);
                Assert.Equal("suggestion", rule.Level);
                Assert.Equal(Lead, rule.Change.By);
                Assert.Equal("A tip is enough.", rule.Change.Note);
                Assert.Equal(waiting.Request.Id, cat.Find(please.Key).OpenRequest.Id);
                Assert.True(cat.Find(please.Key).RequestIsMine);
                Agree(fx, again, key, true, "suggestion");

                fx.Ws.ClearReadOnly();
                GuideRequests.Check(again);   // the next check sends it
                Assert.Single(fx.RequestFiles());
                Assert.Equal(waiting.Request.Id, fx.Catalog(fx.As(Lead)).Find(please.Key).OpenRequest.Id);
            }
        }

        // ---- helpers -------------------------------------------------------------------------------------------

        private static string Snapshot(string folder)
        {
            return string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p)
                .Select(p => p.Substring(folder.Length) + ":" + File.ReadAllText(p).GetHashCode()).ToArray());
        }

        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
        }

        /// <summary>The pane's dialogs are not used here: every change goes through the Style rules paths.</summary>
        private sealed class NoDialogs : IReviewHost
        {
            public bool Confirm(string message, string action) { return false; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { return null; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline) { return null; }
            public IDocumentAdapter OpenDocument(string path) { return null; }
            public bool PreviewBatch(BatchPlan plan) { return false; }
            public bool SaveAndClose(IDocumentAdapter doc) { return false; }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { throw new InvalidOperationException(whatHappened + " " + whatToDo); }
        }
    }
}
