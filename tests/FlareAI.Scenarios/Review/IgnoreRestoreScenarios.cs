using System;
using System.Linq;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
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

        [Fact]
        public void Ignore_ThenRestore_IssueIsBackSelected_AndShowsOnTheNextLint()
        {
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>First utilize the list.</p>"))
            {
                fx.Check();
                fx.Session.Select(fx.Find(Utilize, "utilize"));
                fx.Session.Reject("wrong");
                var ignored = fx.Session.Ignored.Single(f => f.RuleId == Utilize);

                var result = fx.Session.Restore(new[] { ignored });

                Assert.Equal(1, result.Restored);
                Assert.True(result.Saved);
                Assert.Empty(fx.Session.Ignored);
                Assert.Same(ignored, fx.Session.Current);
                Assert.Contains(ignored, fx.Session.Findings);
                Assert.False(ignored.IsStale);
                Assert.Equal(ReviewSession.RestoredOneText, fx.Session.Status);

                var again = fx.NewSession();
                fx.Check(again);
                Assert.Contains(again.Findings, f => f.RuleId == Utilize);
                Assert.Empty(again.Ignored);
            }
        }

        [Fact]
        public void Restore_AfterTheTextChanged_ComesBackStale()
        {
            // Topic report.
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>First utilize the list.</p>"))
            {
                fx.Check();
                fx.Session.Select(fx.Find(Utilize, "utilize"));
                fx.Session.Reject("wrong");
                ChangeUtilize(fx);

                fx.Session.Restore(fx.Session.Ignored.ToList());

                Assert.True(fx.Session.Findings.Single(f => f.RuleId == Utilize).IsStale);
            }
            // Project report, for the topic that was checked.
            var ws = FakeWorkspace.Empty();
            ws.WriteFile(Start, string.Format(ReviewFixture.Wrapper, "<h1>Start</h1><p>Please utilize the menu.</p>"));
            using (var fx = new ReviewFixture(null, Start, null, true, ws))
            {
                fx.Session.Add(fx.Runner.LintFiles(new[] { ws.PathOf(Start) }));
                fx.Session.Select(fx.Session.Findings.First(f => f.Excerpt == "utilize"));
                fx.Session.Reject("wrong");
                ChangeUtilize(fx);

                fx.Session.Restore(fx.Session.Ignored.ToList());

                Assert.True(fx.Session.Findings.First(f => f.Excerpt == "utilize").IsStale);
            }
        }

        [Fact]
        public void Restore_TwoIdenticalIssues_BringsBackBoth()
        {
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>First utilize the list.</p>"))
            {
                fx.Check();
                var one = fx.Find(Utilize, "utilize");
                var twin = new StyleFinding { Rule = one.Rule, RuleId = one.RuleId, Excerpt = one.Excerpt, Before = one.Before, Layer = one.Layer, Source = one.Source };
                fx.Session.Select(one);
                fx.Session.Reject("wrong");
                // Ignoring parks one entry per key, so put the twin aside by hand to test that both come back.
                var ignoredField = typeof(ReviewSession).GetField("_ignored", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ((System.Collections.Generic.List<StyleFinding>)ignoredField.GetValue(fx.Session)).Add(twin);

                var result = fx.Session.Restore(new[] { one });

                Assert.Equal(2, result.Restored);
                Assert.Equal(2, fx.Session.Findings.Count(f => f.RuleId == Utilize));
                Assert.Equal(ReviewSession.RestoredManyText(2), fx.Session.Status);
            }
        }

        [Fact]
        public void ProjectReport_IgnoreAndRestore_AcrossFiles()
        {
            var ws = FakeWorkspace.Empty();
            ws.WriteFile(Start, string.Format(ReviewFixture.Wrapper, "<h1>Start</h1><p>Read the start page.</p>"));
            ws.WriteFile(Other, string.Format(ReviewFixture.Wrapper, "<h1>Other</h1><p>Please utilize the menu to continue.</p>"));
            using (var fx = new ReviewFixture(null, Start, null, true, ws))
            {
                fx.Session.Add(fx.Runner.LintFiles(new[] { ws.PathOf(Start), ws.PathOf(Other) }));
                fx.Session.Select(fx.Session.Findings.First(f => f.Excerpt == "utilize" && f.DocumentPath != null));
                fx.Session.Reject("wrong");
                Assert.Single(fx.Session.Ignored);

                var result = fx.Session.Restore(fx.Session.Ignored.ToList());

                Assert.Equal(1, result.Restored);
                Assert.Empty(fx.Session.Ignored);
                Assert.Contains(fx.Session.Findings, f => f.Excerpt == "utilize" && f.DocumentPath != null);
                Assert.Same(fx.Session.Current, fx.Session.Findings.First(f => f.Excerpt == "utilize" && f.DocumentPath != null));
            }
        }

        [Fact]
        public void Restore_WhenSaveFails_StillComesBack_AndReportsIt()
        {
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>First utilize the list.</p>"))
            {
                fx.Check();
                fx.Session.Select(fx.Find(Utilize, "utilize"));
                fx.Session.Reject("wrong");
                System.IO.File.Delete(Paths.Dismissed);
                System.IO.Directory.CreateDirectory(Paths.Dismissed);   // a folder where the file should be: the write throws

                var result = fx.Session.Restore(fx.Session.Ignored.ToList());

                Assert.Equal(1, result.Restored);
                Assert.False(result.Saved);
                Assert.Contains(fx.Session.Findings, f => f.RuleId == Utilize);
                Assert.Equal(ReviewSession.SaveFailedText, fx.Session.Status);
            }
        }

        [Fact]
        public void DisabledAiIssue_StaysOff_WhenTheAiWordsItDifferently_ButADifferentProblemStillShows()
        {
            var timeless = new ScriptedReview.Flag { Excerpt = "can be out of date", Rule = "Timeless wording" };
            var wordy = new ScriptedReview.Flag { Excerpt = "in order to save", Rule = "Wordy phrase" };
            var answer = new[] { timeless, wordy };
            var ai = new FlareAI.TestKit.Llm.ScriptedLlmClient().When(r => r.Task == "review",
                r => new FlareAI.Core.Llm.LlmResponse { Text = ScriptedReview.ReviewJson(r, answer), StopReason = "end_turn", Model = r.Model ?? "scripted" });
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>This page can be out of date.</p><p>Click Save in order to save.</p>", ai))
            {
                fx.Check();
                fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Fine here." };
                fx.Session.Select(fx.Session.Findings.First(f => f.Source == "ai" && f.Excerpt == "can be out of date"));
                fx.Session.TurnOffRule();
                Assert.DoesNotContain(fx.Session.Findings, f => f.Source == "ai" && f.Excerpt == "can be out of date");

                // The AI words the same problem differently on the next review.
                answer = new[] { new ScriptedReview.Flag { Excerpt = "can be out of date", Rule = "Timeless phrasing" }, wordy };
                var again = fx.NewSession();
                fx.Check(again);

                Assert.DoesNotContain(again.Findings, f => f.Excerpt == "can be out of date");
                Assert.Contains(again.Findings, f => f.Source == "ai" && f.Excerpt == "in order to save");
            }
        }

        private static void ChangeUtilize(ReviewFixture fx)
        {
            var run = (XmlText)BlockSelector.FindBody(fx.Doc.Document).SelectSingleNode("//p/text()");
            fx.Doc.AuthorEdits(() => run.Value = run.Value.Replace("utilize", "use"));
        }

        /// <summary>Appends text to the second paragraph's text, as the writer types it.</summary>
        private static void AddToSecondParagraph(ReviewFixture fx, string text)
        {
            var run = (XmlText)BlockSelector.FindBody(fx.Doc.Document).SelectSingleNode("//p[2]/text()");
            fx.Doc.AuthorEdits(() => run.Value = run.Value + text);
        }
    }
}
