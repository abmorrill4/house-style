using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Integration.Tests.Support;
using Xunit;
using static FlareAI.Integration.Tests.Review.PaneRowStateTests;

namespace FlareAI.Integration.Tests.Review
{
    /// <summary>Exempt rows in both tabs and across re-lints (#448), and the rest of the shared row state (#450).</summary>
    [Collection("Logging")]
    public class PaneRowStateExemptTests
    {
        private const string EMail = "google:word-list/251";
        private const string Mail = "<p>Send an e-mail to the team.</p>";

        private static void Lint(ReviewFixture fx, ReviewSession s) { fx.LintOnlyCheck(s); }

        private static StyleFinding MailRow(ReviewSession s, int n = 0) { return s.Findings.Where(f => f.RuleId == EMail).ElementAt(n); }

        private static void Relint(ReviewFixture fx, ReviewSession s) { s.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc))); }

        [Fact]
        public void Exempt_SaysHowManyAreExempt_Exactly()
        {
            using (var fx = ReviewFixture.Scratch(Mail))
            {
                var topic = fx.Session;
                Lint(fx, topic);
                var t = MailRow(topic);
                topic.Select(t);
                topic.Exempt(t, "Intended here.");
                Assert.EndsWith("Exempted. 1 issue is exempt.", topic.Status);
                Assert.Equal("1 issue is exempt.", topic.ExemptTail);
            }
        }

        [Fact]
        public void ALiveCheckAfterExempt_KeepsTheRowAndTheCountAtOne_AndRemoveExemptionClearsBoth()
        {
            using (var fx = ReviewFixture.Scratch(Mail))
            {
                var topic = fx.Session; var project = fx.NewSession(true);
                Pair(topic, project);
                Lint(fx, topic); Lint(fx, project);
                var t = MailRow(topic); var p = MailRow(project);
                topic.Select(t);
                topic.Exempt(t, "Intended here.");
                Relint(fx, topic);   // the live check finds the exempt text again and hides it, but the row stays
                Assert.True(MailRow(topic).IsExempt);
                Assert.Equal(1, topic.ExemptHidden);

                project.Select(p);
                project.RemoveExemptionCurrent();
                Assert.Equal("", project.ExemptTail);
                Assert.Equal("", topic.ExemptTail);   // the other tab's memory of hidden text went with it
                Assert.Equal(0, topic.ExemptHidden);
                Assert.False(MailRow(topic).IsFixed);
                Assert.Equal(1, topic.Open().Count(x => x.RuleId == EMail));
            }
        }

        [Fact]
        public void LintAgain_HidesTheExemptRow_AndStillCountsIt()
        {
            using (var fx = ReviewFixture.Scratch(Mail))
            {
                var topic = fx.Session;
                Lint(fx, topic);
                var t = MailRow(topic);
                topic.Select(t);
                topic.Exempt(t, "Intended here.");
                Assert.Equal(1, topic.Findings.Count(f => f.RuleId == EMail));   // the row stays
                Lint(fx, topic);                                                // Lint Again starts over
                Assert.Equal(0, topic.Findings.Count(f => f.RuleId == EMail));   // no row, as before
                Assert.Equal("1 issue is exempt.", topic.ExemptTail);
            }
        }

        [Fact]
        public void SeveralRowsWithTheSameExemptText_AllShowAsExempt_InBothTabs()
        {
            using (var fx = ReviewFixture.Scratch(Mail + Mail))
            {
                var topic = fx.Session; var project = fx.NewSession(true);
                Pair(topic, project);
                Lint(fx, topic); Lint(fx, project);
                Assert.Equal(2, topic.Findings.Count(f => f.RuleId == EMail));
                var t = MailRow(topic);
                topic.Select(t);
                topic.Exempt(t, "Intended here.");
                Assert.All(topic.Findings.Where(f => f.RuleId == EMail), f => Assert.True(f.IsExempt));
                Assert.All(project.Findings.Where(f => f.RuleId == EMail), f => Assert.True(f.IsExempt));
                Assert.Equal(1, topic.ExemptHidden);
                topic.Select(MailRow(topic, 1));
                topic.RemoveExemptionCurrent();
                Assert.All(topic.Findings.Where(f => f.RuleId == EMail), f => Assert.False(f.IsFixed));
                Assert.All(project.Findings.Where(f => f.RuleId == EMail), f => Assert.False(f.IsFixed));
            }
        }

        [Fact]
        public void AnExemptRowWithNoCounterpart_StillShowsExempt_AndLeavesTheOtherRowsAlone()
        {
            using (var fx = ReviewFixture.Scratch("<p>Send an e-mail to the team.</p><p>Then read the e-mail we sent back.</p>"))
            {
                var topic = fx.Session; var project = fx.NewSession(true);
                Pair(topic, project);
                Lint(fx, topic);
                // The project list holds only the first row, so the second has no counterpart there.
                var first = MailRow(topic, 0); var second = MailRow(topic, 1);
                var copy = new StyleFinding { Source = first.Source, Layer = first.Layer, Rule = first.Rule, RuleId = first.RuleId, Severity = first.Severity,
                                              Excerpt = first.Excerpt, Before = first.Before, Replacement = first.Replacement, Suggestion = first.Suggestion };
                project.Add(new[] { copy });
                topic.Select(second);
                topic.Exempt(second, "Intended here.");
                Assert.True(second.IsExempt && second.IsFixed);
                Assert.False(first.IsExempt);
                Assert.False(copy.IsExempt);
                topic.Select(first);
                topic.Exempt(first, "Intended here too.");
                Assert.True(copy.IsExempt);
            }
        }

        [Fact]
        public void ATopicNotInTheProjectList_ChangesOnlyItsOwnTab()
        {
            using (var fx = ReviewFixture.Scratch(Mail))
            {
                var topic = fx.Session; var project = fx.NewSession(true);
                Pair(topic, project);
                Lint(fx, topic);   // the project has no rows at all
                var t = MailRow(topic);
                topic.Select(t);
                topic.Exempt(t, "Intended here.");
                Assert.True(t.IsExempt);
                Assert.Empty(project.Findings);
                topic.Select(t);
                topic.RemoveExemptionCurrent();
                Assert.False(t.IsFixed);
            }
        }

        [Fact]
        public void SyncExemptions_ReopensRowsWhoseExemptionWasRemovedElsewhere()
        {
            using (var fx = ReviewFixture.Scratch(Mail))
            {
                var topic = fx.Session; var project = fx.NewSession(true);
                Pair(topic, project);
                Lint(fx, topic); Lint(fx, project);
                var t = MailRow(topic); var p = MailRow(project);
                topic.Select(t);
                topic.Exempt(t, "Intended here.");
                Assert.True(p.IsExempt);
                var entry = Exemptions.Load(fx.Doc.SourcePath).Entries.Single();
                Exemptions.Remove(fx.Doc.SourcePath, entry);   // the Exemptions dialog's Remove Exemption
                topic.SyncExemptions();
                Assert.False(t.IsExempt || t.IsFixed);
                Assert.False(p.IsExempt || p.IsFixed);
                Assert.Equal(0, topic.ExemptHidden);
            }
        }

        [Fact]
        public void ChangeInThisTopic_ThenAProjectSave_UpdatesTheProjectCount_EvenForThePluginsOwnSave()
        {
            var t = Both();
            t.Topic.Select(t.T);
            Assert.Equal(AcceptOutcome.Applied, t.Topic.Accept());
            SelfSaves.Mark(t.Doc.SourcePath);   // the plugin's own save, which RecheckSaved ignores
            t.Project.RecheckSaved(t.Doc.SourcePath, t.Doc.Document);
            Assert.Equal(0, t.Project.Open().Count);
            Assert.True(t.P.IsFixed);
        }

        [Fact]
        public void AFreshLintRow_DoesNotInheritAChangeTheOtherTabRemembers()
        {
            var t = Both();
            t.Topic.Select(t.T);
            t.Topic.Accept();   // fixed in both tabs
            Assert.True(t.P.IsFixed);
            // The change was undone in the editor and only the Project tab still shows it as changed. A fresh lint of the topic finds the text.
            var again = Session(t.Doc, false);
            Pair(again, t.Project);
            t.Doc.Undo();   // Ctrl+Z in the editor
            var row = Find(t.Doc, "utilize", "use");
            again.Add(new[] { row });
            Assert.False(row.IsFixed);
            Assert.Equal(1, again.Open().Count);
        }

        [Fact]
        public void AFreshLintRow_TakesAMarkedAsFixedState_FromTheOtherTab()
        {
            var t = Both();
            t.Project.Select(t.P); t.Project.MarkFixed();
            var again = Session(t.Doc, false);
            Pair(again, t.Project);
            var row = Find(t.Doc, "utilize", "use");
            again.Add(new[] { row });
            Assert.True(row.IsFixed && row.MarkedFixed);
        }

        [Fact]
        public void AnExemptRow_ExportsAsExempt()
        {
            Assert.Equal("Exempt", FlareAI.Core.Output.IssueExport.StatusOf(true, false, false, true));
            Assert.Equal("Changed", FlareAI.Core.Output.IssueExport.StatusOf(true, false, false));
        }
    }
}
