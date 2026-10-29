using System;
using System.Linq;
using System.Xml;
using FlareAI.Core.Editing;
using FlareAI.Scenarios.Support;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Review
{
    /// <summary>What the writer's ignored suggestions look like: kept aside, once each, and cleaned when their text or file goes.</summary>
    public class IgnoreRestoreScenarios
    {
        private const string Utilize = "company:plain-language/1";
        private const string Start = "Content/start.htm", Other = "Content/other.htm";

        [Fact]
        public void IgnoredEarlier_CountsInIgnored_NotInTheList()
        {
            // A rejected suggestion from an earlier session is still rejected: the next check lists it under Ignored, not among the findings.
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>First utilize the list.</p>"))
            {
                fx.Check();
                fx.Session.Select(fx.Find(Utilize, "utilize"));
                fx.Session.Reject("wrong");

                var again = fx.NewSession();
                fx.Check(again);
                Assert.Single(again.Ignored, f => f.RuleId == Utilize);
                Assert.DoesNotContain(again.Findings, f => f.RuleId == Utilize);
            }
        }

        [Fact]
        public void Ignore_ThenLiveRechecks_KeepsOneEntry()
        {
            // The writer types elsewhere in the topic and the live check runs again: the ignored suggestion is parked once, not per recheck.
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>First utilize the list.</p><p>Second line.</p>"))
            {
                fx.Check();
                fx.Session.Select(fx.Find(Utilize, "utilize"));
                fx.Session.Reject("wrong");
                Assert.Single(fx.Session.Ignored, f => f.RuleId == Utilize);

                var changes = 0;
                fx.Session.IgnoredChanged += () => changes++;

                AddToSecondParagraph(fx, " More.");
                fx.Session.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)));
                AddToSecondParagraph(fx, " Again.");
                fx.Session.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)));

                Assert.Single(fx.Session.Ignored, f => f.RuleId == Utilize);
                Assert.DoesNotContain(fx.Session.Findings, f => f.RuleId == Utilize);
                Assert.True(changes > 0);
            }
        }

        [Fact]
        public void ExcludingAFile_DropsItsIgnoredIssues()
        {
            // Lint Project leaves a file out: a suggestion the writer ignored in that file goes with it.
            var ws = FakeWorkspace.Empty();
            ws.WriteFile(Start, string.Format(ReviewFixture.Wrapper, "<h1>Start</h1><p>Read the start page.</p>"));
            ws.WriteFile(Other, string.Format(ReviewFixture.Wrapper, "<h1>Other</h1><p>Please utilize the menu to continue.</p>"));
            using (var fx = new ReviewFixture(null, Start, null, true, ws))
            {
                fx.Session.Add(fx.Runner.LintFiles(new[] { ws.PathOf(Start), ws.PathOf(Other) }));
                var utilize = fx.Session.Findings.First(f => f.Excerpt == "utilize" && f.DocumentPath != null);
                fx.Session.Select(utilize);
                fx.Session.Reject("wrong");
                Assert.Single(fx.Session.Ignored);

                fx.Session.RemoveFiles(p => p.EndsWith("other.htm", StringComparison.OrdinalIgnoreCase));
                Assert.Empty(fx.Session.Ignored);
            }
        }

        /// <summary>Appends text to the second paragraph's text, as the writer types it.</summary>
        private static void AddToSecondParagraph(ReviewFixture fx, string text)
        {
            var run = (XmlText)BlockSelector.FindBody(fx.Doc.Document).SelectSingleNode("//p[2]/text()");
            fx.Doc.AuthorEdits(() => run.Value = run.Value + text);
        }
    }
}
