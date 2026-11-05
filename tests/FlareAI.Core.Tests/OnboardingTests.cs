using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// Onboarding (#123): the role and its default, setup's role answer, the getting-started checklist, the first-time
    /// notes, the guided first lint, the focused first Lint Project and the practice project.
    /// </summary>
    [Collection("Core statics")]
    public class OnboardingTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "hs-onboarding-" + Guid.NewGuid().ToString("N"));

        public OnboardingTests() { Directory.CreateDirectory(_tmp); GuidedTour.Stop(); }
        public void Dispose() { GuidedTour.Stop(); try { Directory.Delete(_tmp, true); } catch (Exception) { } }

        private static string Repo
        {
            get
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
                return d.FullName;
            }
        }

        // ---- role ----------------------------------------------------------------------------------------------

        [Fact]
        public void Role_DefaultsToWriter_ListedOwnersAreAlwaysOwners_OtherwiseTheChoiceWins()
        {
            var s = new PluginSettings();
            Assert.Null(s.Role);
            Assert.Equal(UserRole.Writer, Roles.Effective(s, false));
            Assert.Equal(UserRole.Owner, Roles.Effective(s, true));        // owners.txt lists them: an owner before they choose
            s.Role = UserRole.Writer;
            Assert.Equal(UserRole.Owner, Roles.Effective(s, true));         // a listed owner keeps the Style Guide group, whatever they chose
            s.Role = UserRole.Owner;
            Assert.Equal(UserRole.Owner, Roles.Effective(s, false));
            Assert.Equal(UserRole.Writer, Roles.Effective(null, false));
        }

        [Fact]
        public void Role_RoundTripsThroughTheSettingsFile_AndOldFilesHaveNone()
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new PluginSettings { Role = UserRole.Owner });
            Assert.Equal(UserRole.Owner, Newtonsoft.Json.JsonConvert.DeserializeObject<PluginSettings>(json).Role);
            Assert.Null(Newtonsoft.Json.JsonConvert.DeserializeObject<PluginSettings>("{\"Model\":\"x\"}").Role);
        }

        [Fact]
        public void Setup_FirstQuestion_IsTheRole_InPlainWords_AndTheAnswerIsSaved()
        {
            Assert.Equal("How will you use House Style?", Roles.Question);
            Assert.Equal(new[] { "To check my own writing", "To set up the style guide for my team" }, Roles.Answers);
            Assert.Equal(SetupStep.Role, (SetupStep)0);

            var s = new PluginSettings();
            var answers = SetupAnswers.From(s);
            Assert.Equal(UserRole.Writer, answers.Role);
            answers.Role = UserRole.Owner;
            answers.AiSkipped = true; answers.GuideSkipped = true;
            string error;
            Assert.True(answers.ApplyTo(s, out error), error);
            Assert.Equal(UserRole.Owner, s.Role);
            Assert.Equal(UserRole.Owner, SetupAnswers.From(s).Role);   // running setup again starts from it
            Assert.StartsWith("Role: style guide owner.", FirstRun.Summary(s));
        }

        // ---- checklist -----------------------------------------------------------------------------------------

        [Fact]
        public void Checklist_ListsSixItems_OrFiveWithoutAi()
        {
            Assert.Equal(new[] { "Lint a topic", "Change an issue", "Accept a change on the Review tab", "Disable a rule", "Lint the project", "Try an AI rewrite" },
                         Onboarding.Items(true).Select(Onboarding.Label).ToArray());
            Assert.DoesNotContain(ChecklistItem.AiRewrite, Onboarding.Items(false));
            Assert.Equal(5, Onboarding.Items(false).Count);
        }

        [Fact]
        public void Checklist_TicksOnce_ShowsForAWeek_UntilDone_OrHidden()
        {
            var path = Path.Combine(_tmp, "onboarding.json");
            var start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
            var o = Onboarding.Load(path);
            Assert.True(o.Started(start));
            Assert.False(o.Started(start.AddDays(1)));                    // the first start is remembered once
            Assert.True(o.ChecklistVisible(start.AddDays(6), true));
            Assert.False(o.ChecklistVisible(start.AddDays(8), true));     // after the first week

            Assert.True(o.Mark(ChecklistItem.LintTopic));
            Assert.False(o.Mark(ChecklistItem.LintTopic));                // already ticked
            o.Save();
            var again = Onboarding.Load(path);
            Assert.True(again.IsDone(ChecklistItem.LintTopic));
            Assert.Equal(1, again.DoneCount(true));

            // Done without AI: the AI item isn't listed, so five ticks finish it.
            foreach (var i in Onboarding.Items(false)) again.Mark(i);
            Assert.False(again.ChecklistVisible(start.AddDays(1), false));
            Assert.True(again.ChecklistVisible(start.AddDays(1), true));   // with AI on, one more to go

            again.ChecklistHidden = true;
            Assert.False(again.ChecklistVisible(start.AddDays(1), true));
        }

        [Fact]
        public void Onboarding_ADamagedFile_StartsOver()
        {
            var path = Path.Combine(_tmp, "onboarding.json");
            File.WriteAllText(path, "{ not json");
            var o = Onboarding.Load(path);
            Assert.Empty(o.Done);
            Assert.False(o.TourOffered);
            o.Mark(ChecklistItem.DisableRule);
            o.Save();
            Assert.True(Onboarding.Load(path).IsDone(ChecklistItem.DisableRule));
        }

        // ---- first-time notes ----------------------------------------------------------------------------------

        [Fact]
        public void FirstTimeNotes_AreTheApprovedText_AndShowOnceEach()
        {
            var path = Path.Combine(_tmp, "onboarding.json");
            var o = Onboarding.Load(path);
            Assert.Equal("The change is in your topic as a tracked change. Accept it on the Review tab to keep it.", o.TakeNote(FirstTimeNote.FirstChange));
            Assert.Null(o.TakeNote(FirstTimeNote.FirstChange));
            Assert.Equal("Each AI rewrite costs a few cents. The running total is at the bottom of this pane.", o.TakeNote(FirstTimeNote.FirstAiRewrite));
            o.Save();
            var again = Onboarding.Load(path);
            Assert.Null(again.TakeNote(FirstTimeNote.FirstChange));
            Assert.Null(again.TakeNote(FirstTimeNote.FirstAiRewrite));
        }

        // ---- guided first lint ---------------------------------------------------------------------------------

        [Fact]
        public void Tour_MovesOnOnlyWhenTheWriterDoesTheStep_AndEndsWithTheReadyLine()
        {
            var changes = 0;
            Action count = () => changes++;
            GuidedTour.Changed += count;
            try
            {
                var t = GuidedTour.Start(true);
                Assert.Equal(TourStep.LintTopic, t.Step);
                Assert.Equal("Step 1 of 5", t.Progress);
                Assert.Equal("Click Lint Topic on the House Style tab.", t.Hint);

                Assert.False(GuidedTour.Notify(TourEvent.Changed));          // out of order: nothing happens
                Assert.True(GuidedTour.Notify(TourEvent.TopicLinted));
                Assert.Equal("Select an issue and click Change.", t.Hint);
                Assert.True(GuidedTour.Notify(TourEvent.Changed));
                Assert.Equal("Press Ctrl+Z in the topic to undo the change.", t.Hint);
                Assert.False(t.NeedsNext);
                Assert.True(GuidedTour.Notify(TourEvent.Undone));
                Assert.Equal("Select an issue you don't want and click Disable Rule.", t.Hint);
                Assert.True(GuidedTour.Notify(TourEvent.RuleDisabled));
                Assert.Equal(TourStep.ReviewTab, t.Step);
                Assert.True(t.NeedsNext);                                    // House Style can't see the Review tab
                Assert.False(GuidedTour.Notify(TourEvent.TopicLinted));
                Assert.True(GuidedTour.Notify(TourEvent.Next));
                Assert.Equal(TourStep.Finished, t.Step);
                Assert.Equal("You're ready. Lint Project lints every topic.", t.Hint);
                Assert.Equal("", t.Progress);
                Assert.False(GuidedTour.Notify(TourEvent.Next));

                GuidedTour.Stop();
                Assert.Null(GuidedTour.Current);
                Assert.False(GuidedTour.Notify(TourEvent.TopicLinted));     // no tour: nothing to do
                Assert.Equal(7, changes);                                    // start, five steps, stop
            }
            finally { GuidedTour.Changed -= count; }
        }

        [Fact]
        public void TourHints_AreOnePlainSentence_WithoutColonsArrowsOrSemicolons()
        {
            foreach (TourStep s in Enum.GetValues(typeof(TourStep)))
            {
                var h = GuidedTour.HintFor(s);
                Assert.DoesNotContain(":", h);
                Assert.DoesNotContain(";", h);
                Assert.DoesNotContain("→", h);
                Assert.DoesNotContain("—", h);
                Assert.EndsWith(".", h);
            }
        }

        // ---- first Lint Project: the top rules first ------------------------------------------------------------

        private static List<BacklogRule> Rules(params int[] counts)
        {
            var list = new List<BacklogRule>();
            for (var i = 0; i < counts.Length; i++)
            {
                var r = new BacklogRule { Key = "r" + i, Title = "Rule " + i };
                for (var n = 0; n < counts[i]; n++) r.Findings.Add(new StyleFinding { RuleId = r.Key });
                list.Add(r);
            }
            return list;
        }

        [Fact]
        public void FirstProjectLint_ShowsTheTopThreeRules_AndSaysHowManyIssuesTheyCover()
        {
            var f = RuleFocus.Of(Rules(20, 12, 8, 5, 3, 1));
            Assert.True(f.Focused);
            Assert.Equal(new[] { "r0", "r1", "r2" }, f.Shown.Select(r => r.Key).ToArray());
            Assert.Equal(40, f.Covered);
            Assert.Equal(49, f.Total);
            Assert.Equal("Start with these 3 rules. They cover 40 of the 49 issues.", f.Line);
        }

        [Fact]
        public void FirstProjectLint_WithThreeRulesOrFewer_ShowsThemAll_WithoutTheLine()
        {
            var f = RuleFocus.Of(Rules(4, 2, 1));
            Assert.False(f.Focused);
            Assert.Equal(3, f.Shown.Count);
            Assert.Equal(7, f.Covered);
            Assert.Equal("", f.Line);
            Assert.False(RuleFocus.Of(new List<BacklogRule>()).Focused);
        }

        // ---- the practice project -------------------------------------------------------------------------------

        private static string PluginSource { get { return Path.Combine(Repo, "src", "FlareAI.Flare"); } }

        [Fact]
        public void PracticeProject_IsCopiedOnce_AndKeepsTheWritersEdits()
        {
            var dest = Path.Combine(_tmp, "House Style Practice");
            var topic = PracticeProject.Prepare(PluginSource, dest);
            Assert.Equal(Path.Combine(dest, @"Content\Send money.htm"), topic);
            Assert.True(File.Exists(topic));
            Assert.True(File.Exists(Path.Combine(dest, "House Style Practice.flprj")));
            Assert.True(File.Exists(Path.Combine(dest, @"Content\Transfers.htm")));

            File.WriteAllText(topic, "<html><body><p>Edited.</p></body></html>");
            PracticeProject.Prepare(PluginSource, dest);
            Assert.Contains("Edited.", File.ReadAllText(topic));
            Assert.Throws<DirectoryNotFoundException>(() => PracticeProject.Prepare(_tmp, Path.Combine(_tmp, "x")));
        }

        [Fact]
        public void PracticeProject_HasPlantedIssues_SomeAutomatic_AndOneThatNeedsTheWriter()
        {
            var dest = Path.Combine(_tmp, "Practice");
            var topic = PracticeProject.Prepare(PluginSource, dest);
            var guide = Path.Combine(Repo, "styleguide");
            var engine = new StyleEngine(new[] { guide }, guide);
            var runner = new ActionRunner(null, PromptBuilder.For(engine, null), 0, engine);
            var files = ActionRunner.ProjectFiles(topic);
            Assert.Equal(2, files.Count);
            var found = runner.LintFiles(files);
            var inTopic = found.Where(f => string.Equals(f.DocumentPath, topic, StringComparison.OrdinalIgnoreCase)).ToList();
            var described = string.Join("\n", inTopic.Select(f => f.Rule + " [" + f.Kind + (f.CanFix ? ", fix" : "") + "] " + f.Excerpt).ToArray());
            // Enough automatic changes for Change, Change All and Ctrl+Z, one rule with several, and one for the writer.
            Assert.True(inTopic.Count(RuleBacklog.IsAutomatic) >= 3, described);
            Assert.True(inTopic.Where(RuleBacklog.IsAutomatic).GroupBy(RuleBacklog.KeyOf).Any(g => g.Count() >= 2) ||
                        found.Where(RuleBacklog.IsAutomatic).GroupBy(RuleBacklog.KeyOf).Any(g => g.Count() >= 2), described);
            Assert.True(inTopic.Any(f => !RuleBacklog.IsAutomatic(f)), described);
            // The project has more than three rules, so the first Lint Project starts focused on the top three.
            Assert.True(RuleBacklog.Group(found, null, topic).Count > 3, described);
        }
    }
}
