using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Integration.Tests.Support;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Integration.Tests.Review
{
    /// <summary>
    /// A writer checks a topic and decides on what they see: accept one fix, accept all mechanical fixes, write everything as
    /// tracked changes, edit a sentence that has a finding. Each story names the TEST-PLAN case (docs/testing/TEST-PLAN.md)
    /// it automates. Model answers are scripted; the editor is FlareSim.
    /// </summary>
    public class CheckAndAcceptTests
    {
        private const string Utilize = "company:plain-language/1";

        private static IEnumerable<StyleFinding> Ordered(ReviewSession s, string ruleId)
        {
            return s.Findings.Where(f => f.RuleId == ruleId && f.DocumentPath == null).OrderBy(f => f.Order);
        }

        // ---- 1 · TEST-PLAN A1 -------------------------------------------------------------------------------------

        [Fact]
        public void Story01_WriterChecksOneTopic_SeesRuleAndAiFindingsInReadingOrder_AndSnippetFindingsSayHowManyTopicsUseIt()
        {
            // A1: lint and structure findings at once, AI findings appended, every finding shown at its place.
            var ai = ScriptedReview.Review(new ScriptedReview.Flag { Excerpt = "can be out of date", Rule = "Timeless wording", Replacement = "is sometimes out of date" });
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", ai))
            {
                var found = fx.Check();

                Assert.True(ai.Calls > 0);
                var topic = fx.Session.Findings.Where(f => f.DocumentPath == null).ToList();
                Assert.Equal(new[] { "utilize", "can be out of date", "Please c", "Simply s" }, topic.Select(f => f.Excerpt).ToArray());   // reading order
                Assert.Equal(new[] { "lint", "ai", "lint", "lint" }, topic.Select(f => f.Source).ToArray());
                Assert.All(topic, f => Assert.Equal("About mobile deposit", f.Section));
                Assert.All(topic, f => Assert.NotNull(fx.Runner.LocateBlock(fx.Doc, f)));   // every finding can be shown in place, the AI ones too
                Assert.NotNull(topic[0].RuleSection);

                // A finding inside the snippet comes last, under the snippet's name, with how many topics share it.
                var inSnippet = fx.Session.Findings.Single(f => f.DocumentPath != null);
                Assert.Equal(fx.Session.Findings.Last(), inSnippet);
                Assert.Equal("Snippet: LegacyApp", inSnippet.Section);
                var users = Directory.GetFiles(fx.Ws.ContentDir, "*.htm", SearchOption.AllDirectories).Count(p => File.ReadAllText(p).Contains("LegacyApp.flsnp"));
                Assert.True(users > 1, "the corpus snippet is shared");
                Assert.Equal(users, fx.Session.SnippetUses(inSnippet.DocumentPath));
                Assert.Contains("5 issues", fx.Session.Summary());
            }
        }

        // ---- 2 · TEST-PLAN A3 -------------------------------------------------------------------------------------

        [Fact]
        public void Story02_WriterAcceptsOneFix_TheRightWordChanges_InOneUndoStep_AndUndoRedoWork()
        {
            // A3: a tracked word-level change in the right place (the second "utilize", not the first); Ctrl+Z removes it.
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>Never utilize the old report. Always utilize the new list.</p>"))
            {
                fx.Check();
                var second = Ordered(fx.Session, Utilize).Skip(1).First();
                fx.Session.Select(second);

                Assert.Equal(AcceptOutcome.Applied, fx.Session.Accept());

                Assert.Equal("Tools" + "Never utilize the old report. Always use the new list.", fx.AcceptedText);
                Assert.Equal(1, fx.Doc.UndoCount);
                Assert.Empty(fx.Doc.UntrackedWrites);
                Assert.True(TrackedChanges.HasPending(new List<XmlNode> { BlockSelector.FindBody(fx.Doc.Document) }));
                var edited = fx.Doc.Xml;

                fx.Doc.Undo();
                Assert.Equal(fx.Original, fx.Doc.Xml);
                fx.Doc.Redo();
                Assert.Equal(edited, fx.Doc.Xml);

                var next = fx.Session.Current;   // the next suggestion is selected, the first "utilize"
                Assert.Equal("utilize", next.Excerpt);
                Assert.NotSame(second, next);
            }
        }

        // ---- 3 · TEST-PLAN A8 -------------------------------------------------------------------------------------

        [Fact]
        public void Story03_WriterAcceptsAllMechanicalFixes_FifteenOfThem_InOneUndoStep()
        {
            // A8: adjacent hits and several hits in one text node; right-most first; one undo step.
            var body = "<h1>Tools</h1>" +
                       "<p>We utilize the tool and utilize the list. Utilize utilize the menu.</p>" +
                       "<p>Always utilize the form, utilize the report, utilize the grid and utilize the log.</p>" +
                       "<ul><li>First utilize one.</li><li>Then utilize two.</li><li>Next utilize three.</li><li>Last utilize four.</li></ul>" +
                       "<p>We utilize it. They utilize it. You utilize it.</p>";
            using (var fx = ReviewFixture.Scratch(body))
            {
                fx.Check();
                var fixes = fx.Session.FixAllCandidates();
                Assert.Equal(15, fixes.Count(f => f.RuleId == Utilize));
                var before = fx.Doc.BodyText;

                fx.Session.FixAll();

                Assert.All(fixes, f => Assert.True(f.IsFixed));
                Assert.Equal(1, fx.Doc.UndoCount);
                Assert.Empty(fx.Doc.UntrackedWrites);
                Assert.Equal(1, fx.Doc.UndoNames.Count);
                Assert.Empty(fx.Host.Previews);   // only the open topic: no preview, a single Ctrl+Z

                fx.Doc.AcceptAll();
                Assert.DoesNotContain("tilize", fx.Doc.BodyText.Replace("Utilize", "").Replace("utilize", ""));   // sanity: only the word itself was replaced
                Assert.DoesNotContain("utilize", fx.Doc.BodyText);
                Assert.DoesNotContain(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)), a => a.RuleId == Utilize);
                fx.Doc.Undo();   // un-accept

                fx.Doc.RejectAll();   // reject every tracked change: the original wording is back
                Assert.Equal(before, fx.Doc.BodyText);
                fx.Doc.Undo();
                fx.Doc.Undo();
                Assert.Equal(fx.Original, fx.Doc.Xml);
            }
        }

        // ---- 4 · TEST-PLAN A9 -------------------------------------------------------------------------------------

        [Fact]
        public void Story04_WriterWritesAllAsTrackedChanges_FixesAndWordingBecomeChanges_RewritesStayListed()
        {
            // A9: every exact proposal is a tracked change; what needs a rewrite stays in the list.
            var ai = ScriptedReview.Review(
                new ScriptedReview.Flag { Excerpt = "can be out of date", Rule = "Timeless wording", Replacement = "is sometimes out of date" },
                new ScriptedReview.Flag { Excerpt = "Mobile deposit gives members one place", Rule = "Too abstract" });
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", ai))
            {
                fx.Check();
                var todo = fx.Session.WriteAllCandidates();
                Assert.Equal(new[] { "mechanical", "wording", "mechanical", "mechanical" }, todo.Select(f => f.Kind).ToArray());
                var needsRewrite = fx.Session.Findings.Single(f => f.Kind == "restructure");

                fx.Session.WriteAll();

                Assert.Contains("Make 4 changes in the topic as tracked changes?", fx.Host.Confirms.Single());
                Assert.All(todo, f => Assert.True(f.IsFixed));
                Assert.Equal(1, fx.Doc.UndoCount);
                Assert.True(TrackedChanges.HasPending(new List<XmlNode> { BlockSelector.FindBody(fx.Doc.Document) }));
                Assert.Contains("is sometimes out of date", fx.AcceptedText);
                Assert.Contains("Made 4 tracked changes", fx.Session.Status);
                Assert.DoesNotContain(needsRewrite, fx.Session.Open().Where(f => f.IsFixed));
                Assert.Contains(needsRewrite, fx.Session.Open());                       // still listed
                Assert.Contains(fx.Session.Open(), f => f.DocumentPath != null);           // the snippet finding is untouched
                var log = File.ReadAllText(Paths.Feedback);
                Assert.Equal(4, log.Split('\n').Count(l => l.Contains("\"proposed\"")));      // recorded as proposed, not accepted

                fx.Doc.Undo();
                Assert.Equal(fx.Original, fx.Doc.Xml);
            }
        }

        // ---- 5 · TEST-PLAN A5 -------------------------------------------------------------------------------------

        [Fact]
        public void Story05_WriterEditsASentence_ItsFindingIsStale_CannotBeApplied_AndReAnalyzeRefreshesIt()
        {
            // A5: marked stale, can't be applied; Re-analyze refreshes it.
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>Always utilize the new list.</p><p>Then utilize the menu.</p>"))
            {
                fx.Check();
                var f = Ordered(fx.Session, Utilize).First();
                var other = Ordered(fx.Session, Utilize).Last();
                var run = (XmlText)BlockSelector.FindBody(fx.Doc.Document).SelectSingleNode("(.//p)[1]/text()");
                fx.Doc.AuthorEdits(() => run.Value = "Always employ the new list.");
                fx.Session.Sort();
                fx.Session.Select(f);

                Assert.True(f.IsStale);
                Assert.False(other.IsStale);
                Assert.Equal(AcceptOutcome.Stale, fx.Session.Accept());
                Assert.Equal("The text changed. Lint the paragraph again.", fx.Session.Status);
                Assert.Equal(0, fx.Doc.UndoCount);

                // Re-analyze checks that paragraph again: the stale card gives way to what is there now.
                var block = fx.Session.ReanalyzeTarget(f);
                Assert.NotNull(block);
                var fresh = fx.Runner.Lint(new List<XmlNode> { block });
                fx.Session.Reanalyzed(f, block, fresh);

                Assert.DoesNotContain(f, fx.Session.Findings);
                Assert.DoesNotContain(fx.Session.Findings, x => x.IsStale);
                Assert.Single(fx.Session.Findings.Where(x => x.RuleId == Utilize));
                fx.Session.Select(fx.Session.Findings.First(x => x.RuleId == Utilize));
                Assert.Equal(AcceptOutcome.Applied, fx.Session.Accept());
                Assert.Contains("Then use the menu.", fx.AcceptedText);
            }
        }
    }
}
