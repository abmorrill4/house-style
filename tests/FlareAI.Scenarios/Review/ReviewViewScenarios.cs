using System;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Scenarios.Support;
using Xunit;

namespace FlareAI.Scenarios.Review
{
    /// <summary>The Issues list narrowed by level and by source.</summary>
    public class ReviewViewScenarios
    {
        private const string Utilize = "company:plain-language/1";

        [Fact]
        public void ErrorsOnly_NextSkipsWarnings_AndFixAllChangesOnlyErrors()
        {
            var body = "<h1>Tools</h1><p>First utilize the list.</p><p>Second utilize the grid.</p><p>Third utilize the menu.</p>";
            using (var fx = ReviewFixture.Scratch(body))
            {
                fx.Check();
                var all = fx.Session.Findings.Where(f => f.RuleId == Utilize).OrderBy(f => f.Order).ToList();
                Assert.Equal(3, all.Count);
                all[0].Severity = "warning";
                all[1].Severity = "error";
                all[2].Severity = "warning";

                var changed = 0;
                fx.Session.ListChanged += () => changed++;
                fx.Session.Select(all[1]);
                fx.Session.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.RulesAndAi);
                Assert.True(changed > 0);
                Assert.Equal(all[1], fx.Session.Current);

                Assert.Equal(all[1], fx.Session.NextAfter(all[0]));
                Assert.Equal(all[1], fx.Session.NextAfter(null));
                Assert.Equal(1, fx.Session.Visible().Count(f => f.RuleId == Utilize));
                Assert.Contains("Showing 1 of ", fx.Session.Summary());
                Assert.StartsWith("Showing 1 of ", fx.Session.Status);   // the status line follows the filter (#249's e2e found it didn't)

                fx.Session.FixAll();

                var xml = fx.Doc.Xml;
                Assert.Contains("Third utilize the menu", xml);
                Assert.Contains("First utilize the list", xml);
                Assert.DoesNotContain("Second utilize the grid", xml);

                fx.Session.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.AiOnly);
                Assert.Null(fx.Session.Current);
                Assert.Equal("No issues match these filters.", fx.Session.Summary());
            }
        }

        private const string Utilize2Sentences = "<h1>Tools</h1><p>First utilize the list.</p><p>Second utilize the grid.</p>";

        [Fact]
        public void AiOnly_ScriptedReview_ListsOnlyAiIssues_MarkersStillShowAll()
        {
            var llm = ScriptedReview.Review(new ScriptedReview.Flag { Excerpt = "the grid", Severity = "suggestion", Replacement = "the table" });
            using (var fx = ReviewFixture.Scratch(Utilize2Sentences, llm))
            {
                fx.Check();
                var s = fx.Session;
                var ai = s.Open().Where(f => f.Source == "ai").ToList();
                Assert.Single(ai);
                Assert.Contains(s.Open(), f => f.Source != "ai");

                s.Filter = new ReviewFilter(ReviewLevel.All, ReviewSource.AiOnly);
                Assert.Equal(ai, s.Visible());
                Assert.All(s.Visible(), f => Assert.Equal("ai", f.Source));
                Assert.Equal(s.Open().Count, s.OpenTopicFindings().Count);      // the editor markers ignore the filter
                Assert.Contains(s.OpenTopicFindings(), f => f.Source != "ai");
            }
        }

        [Fact]
        public void HiddenCurrent_ClearsSelection_AndDoesNotMoveTheEditor()
        {
            using (var fx = ReviewFixture.Scratch(Utilize2Sentences))
            {
                fx.Check();
                var s = fx.Session;
                var all = s.Findings.Where(f => f.RuleId == Utilize).OrderBy(f => f.Order).ToList();
                foreach (var f in all) f.Severity = "warning";
                s.Select(all[0]);
                Assert.Equal(all[0], s.Current);

                var selects = 0;
                s.SelectRequested += f => selects++;
                s.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.RulesAndAi);

                Assert.Null(s.Current);
                Assert.Equal(0, selects);                                      // no row selected, so the editor is not asked to move
            }
        }

        [Fact]
        public void FilteredEmpty_SummaryNeverSaysNoIssuesFound()
        {
            using (var fx = ReviewFixture.Scratch(Utilize2Sentences))
            {
                fx.Check();
                var s = fx.Session;
                foreach (var f in s.Open()) f.Severity = "warning";
                s.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.RulesAndAi);

                Assert.NotEmpty(s.Open());
                var status = s.Summary() + "  Done with the rule.";
                Assert.StartsWith("No issues match these filters.", status);
                Assert.DoesNotContain("No issues found", status);
            }

            using (var p = ProjectFixture())
            {
                var s = p.Session;
                s.Add(p.Runner.LintFiles(ActionRunner.ProjectFiles(p.Ws.ProjectFile)));
                Assert.NotEmpty(s.Open());
                s.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.AiOnly);       // no AI findings in this check: nothing shown

                Assert.Empty(s.Visible());
                var status = s.Summary() + "  Done with the rule.";
                Assert.StartsWith("No issues match these filters.", status);
                Assert.DoesNotContain("No issues found", status);
            }
        }

        [Fact]
        public void Filter_DisabledDuringAWalk_AndWalkKeepsItsRule()
        {
            var body = "<h1>Tools</h1><p>First utilize the list.</p><p>Second utilize the grid.</p><p>Third utilize the menu.</p>";
            using (var fx = ReviewFixture.Scratch(body))
            {
                fx.Check();
                var s = fx.Session;
                s.Filter = new ReviewFilter(ReviewLevel.All, ReviewSource.RulesOnly);
                var rule = s.Backlog().First(r => r.Occurrences >= 3);

                s.ReviewRule(rule);
                Assert.True(s.WalkActive);
                Assert.Equal(rule.Key, s.FocusRule);

                s.Filter = ReviewFilter.All;                                  // a filter change during the walk does not end it
                Assert.True(s.WalkActive);
                Assert.Equal(rule.Key, s.FocusRule);
            }
        }

        [Fact]
        public void AiFindingMarkedError_RulesAndAiErrors_NeverEntersFixAllCandidates()
        {
            var llm = ScriptedReview.Review(new ScriptedReview.Flag { Excerpt = "the grid", Severity = "error", Replacement = "the table" });
            using (var fx = ReviewFixture.Scratch(Utilize2Sentences, llm))
            {
                fx.Check();
                var s = fx.Session;
                var ai = s.Open().Where(f => f.Source == "ai").ToList();
                Assert.Single(ai);
                Assert.Equal("error", ai[0].Severity);

                var shown = s.Visible();
                Assert.Equal(s.Open().Count, shown.Count);
                Assert.DoesNotContain(s.FixAllCandidates(), f => f.Source == "ai");
                Assert.Equal(shown.Count - ai.Count, s.FixAllCandidates().Count);
                Assert.Equal(shown.Count, s.WriteAllCandidates().Count);       // Change All and Write All both count what is shown
            }
        }

        [Fact]
        public void LiveCheck_FreshFindingsGoThroughTheFilter()
        {
            var llm = ScriptedReview.Review(new ScriptedReview.Flag { Excerpt = "the grid", Severity = "error", Replacement = "the table" });
            using (var fx = ReviewFixture.Scratch(Utilize2Sentences, llm))
            {
                var s = fx.Session;
                s.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.RulesAndAi);

                fx.Check();                                                    // a fresh check, after the filter was set

                Assert.Equal(ReviewLevel.Errors, s.Filter.Level);
                Assert.NotEmpty(s.Visible());
                Assert.All(s.Visible(), f => Assert.Equal("error", f.Severity));
                Assert.Equal(s.Open().Count(f => f.Severity == "error"), s.Visible().Count);
                Assert.True(s.Open().Count > s.Visible().Count);
            }
        }

        [Fact]
        public void ClearFilters_RestoresRuleRows()
        {
            var body = "<h1>Tools</h1><p>First utilize the list.</p><p>Second utilize the grid.</p><p>Third utilize the menu.</p>";
            using (var fx = ReviewFixture.Scratch(body))
            {
                fx.Check();
                var s = fx.Session;
                var before = s.Backlog();
                var beforeCount = before.Sum(r => r.Occurrences);
                Assert.NotEmpty(before);

                s.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.AiOnly);
                Assert.Empty(s.Backlog());

                s.Filter = ReviewFilter.All;
                var after = s.Backlog();
                Assert.Equal(before.Count, after.Count);
                Assert.Equal(beforeCount, after.Sum(r => r.Occurrences));
                Assert.Equal(s.Open().Count, after.Sum(r => r.Occurrences));
            }
        }

        [Fact]
        public void PrefsWriteFails_FilterStillApplies()
        {
            var dir = Path.Combine(Path.GetTempPath(), "reviewview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // A file where the folder should be: creating the folder fails.
                var blocker = Path.Combine(dir, "not-a-folder");
                File.WriteAllText(blocker, "x");
                var unwritable = Path.Combine(blocker, "review-view.json");
                var ex = Record.Exception(() => ReviewViewPrefs.Save(unwritable, ReviewLevel.Errors, ReviewSource.AiOnly));
                Assert.Null(ex);

                using (var fx = ReviewFixture.Scratch(Utilize2Sentences))
                {
                    fx.Check();
                    var s = fx.Session;
                    var all = s.Findings.Where(f => f.RuleId == Utilize).OrderBy(f => f.Order).ToList();
                    all[0].Severity = "warning";
                    all[1].Severity = "error";
                    s.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.RulesOnly);

                    Assert.Equal(new[] { all[1] }, s.Visible().Where(f => f.RuleId == Utilize).ToArray());
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        /// <summary>A project check's session over the legacy corpus, with its topic open (as the backlog scenarios do).</summary>
        private static ReviewFixture ProjectFixture()
        {
            var ws = ReviewFixture.Corpus("v2/legacy");
            var topic = ActionRunner.ProjectFiles(ws.ProjectFile).First(f => f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));
            return new ReviewFixture("v2/legacy", topic, null, true, ws);
        }
    }
}
