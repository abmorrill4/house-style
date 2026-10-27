using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Review
{
    /// <summary>Issues the writer fixes by typing show as fixed (#237), without lint or AI.</summary>
    [Collection("Logging")]
    public class FixedByHandScenarios
    {
        private static string Topic(string first, string second = "Other paragraph.")
        {
            return "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head /><body><h1>Start</h1><p>" + first + "</p><p>" + second + "</p></body></html>";
        }

        private const string TwoWords = "Please utilize the tool. Then utilize the menu.";

        private static StyleFinding Find(FlareSimDocument doc, string text, string replacement, int occurrence = 0)
        {
            var seen = 0;
            foreach (var run in TextRuns.Of(new List<XmlNode> { BlockSelector.FindBody(doc.Document) }))
            {
                var at = -1;
                while ((at = run.Value.IndexOf(text, at + 1, StringComparison.Ordinal)) >= 0)
                {
                    if (seen++ < occurrence) continue;
                    return FindingLocator.FromLint(new LintHit { Start = at, Length = text.Length, Match = text, Replacement = replacement, Rule = "Google.WordList", RuleId = "google:words/2", Severity = "warning", Message = "Use plain words.", Layer = "google" }, run);
                }
            }
            throw new InvalidOperationException("no " + text);
        }

        private static ReviewSession Session(FlareSimDocument doc, bool project = false)
        {
            var style = new StyleEngine(new[] { Repo.StyleGuide }, Repo.StyleGuide);
            var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(style, doc.SourcePath), 16000, style, doc.SourcePath);
            return new ReviewSession(runner, doc, new ScenarioHost(), null, project)
            {
                CurrentTeam = () => null,
                RecordFeedback = (f, topic, decision, reason) => null,
                IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SnippetUsage = p => 1
            };
        }

        private static XmlText RunOf(FlareSimDocument doc, int paragraph)
        {
            return (XmlText)BlockSelector.FindBody(doc.Document).SelectNodes("p")[paragraph].FirstChild;
        }

        [Fact]
        public void HandEdit_RemovesTheFlaggedText_MarksTheIssueFixed_AndNextSkipsIt()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            RunOf(doc, 0).Value = RunOf(doc, 0).Value.Replace("Please utilize", "Please use");
            s.AfterEdit(null);
            Assert.True(a.IsFixed && a.FixedByEdit);
            Assert.False(b.IsFixed);
            Assert.EndsWith("You fixed 1 issue.", s.Status);
            Assert.NotSame(a, s.NextAfter(null));
        }

        [Fact]
        public void HandEdit_TwoIssues_StatusSaysYouFixed2Issues()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            s.Add(new[] { Find(doc, "utilize", "use"), Find(doc, "utilize", "use", 1) });
            RunOf(doc, 0).Value = "Please use the tool. Then use the menu.";
            s.AfterEdit(null);
            Assert.EndsWith("You fixed 2 issues.", s.Status);
        }

        [Fact]
        public void HandEdit_ThenUndo_BringsTheIssueBack()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            RunOf(doc, 0).Value = "Please use the tool. Then utilize the menu.";
            s.AfterEdit(null);
            Assert.True(a.IsFixed);
            RunOf(doc, 0).Value = TwoWords;
            s.AfterEdit(null);
            Assert.Equal("The change was undone. The issue is back.", s.Status);
            Assert.False(a.IsFixed);
            Assert.Equal(2, s.Findings.Count(f => !f.IsFixed && f.Excerpt == "utilize"));
        }

        [Fact]
        public void HandEdit_OfOneOfTwoSameWords_MarksOnlyThatOne()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            RunOf(doc, 0).Value = "Please utilize the tool. Then use the menu.";
            s.AfterEdit(null);
            Assert.True(b.IsFixed);
            Assert.False(a.IsFixed);
        }

        [Fact]
        public void TwoSameWordsThatCantBeToldApart_FixingOne_LeavesBothOpen()
        {
            var doc = FlareSimDocument.FromXml(Topic("AAAAAAAAAAAA utilize it. AAAAAAAAAAAA utilize it."));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            RunOf(doc, 0).Value = "AAAAAAAAAAAA utilize it.";
            s.AfterEdit(null);
            Assert.False(a.IsFixed);
            Assert.False(b.IsFixed);
        }

        [Fact]
        public void DeleteWholeParagraph_WithTheTermElsewhere_MarksItsIssueFixed_AndLeavesTheOtherOpen()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool.", "Then utilize the menu."));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            var body = BlockSelector.FindBody(doc.Document);
            body.RemoveChild(body.SelectNodes("p")[0]);
            s.AfterEdit(null);
            Assert.True(a.IsFixed && a.FixedByEdit);
            Assert.False(b.IsFixed);
        }

        [Fact]
        public void AiIssue_WhoseTextWasEdited_ShowsFixed_NotStale()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool. Other words."));
            var s = Session(doc);
            var f = Find(doc, "Please utilize the tool", null);
            f.Source = "ai"; f.Layer = "ai"; f.Replacement = null;
            s.Add(new[] { f });
            RunOf(doc, 0).Value = "Use the tool. Other words.";
            s.AfterEdit(null);
            Assert.True(f.IsFixed);
            Assert.False(f.IsStale);
        }

        [Fact]
        public void BoldingPartOfTheFlaggedText_LeavesItOpen()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool."));
            var s = Session(doc);
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            var p = BlockSelector.FindBody(doc.Document).SelectNodes("p")[0];
            p.InnerXml = "Please <b>utilize</b> the tool.";
            s.AfterEdit(null);
            Assert.False(f.IsFixed);
        }

        [Fact]
        public void PauseWhileALintRuns_IsSkipped_AndTheNextPauseCatchesUp()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            RunOf(doc, 0).Value = "Please use the tool. Then utilize the menu.";
            RunOf(doc, 0).Value = "Please use the tool. Then use the menu.";
            s.AfterEdit(null);
            Assert.True(a.IsFixed);
            Assert.True(b.IsFixed);
        }

        [Fact]
        public void UnreadableDom_SkipsTheFile_LogsTypeNameOnly_AndMarksNothing()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc, true);
            s.OpenedDocument = p => { throw new IOException("secret words"); };
            var f = Find(doc, "utilize", "use");
            f.DocumentPath = Path.Combine(Path.GetTempPath(), "other.htm");
            var lines = new List<string>();
            var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
            Diagnostics.Sink = l => { lock (lines) lines.Add(l); };
            Diagnostics.Enabled = true;
            try
            {
                s.Add(new[] { f });
                Assert.Equal(0, s.RecheckEdited());
                Assert.False(f.IsFixed);
                Assert.Contains(lines, l => l.Contains("IOException"));
                Assert.DoesNotContain(lines, l => l.Contains("secret words"));
            }
            finally { Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled; }
        }

        private static List<StyleFinding> Lint(FlareSimDocument doc)
        {
            var style = new StyleEngine(new[] { Repo.StyleGuide }, Repo.StyleGuide);
            var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(style, doc.SourcePath), 16000, style, doc.SourcePath);
            return runner.Lint(runner.PickAllBlocks(doc));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HandEdit_ThenUndo_BringsTheIssueBack_WithAndWithoutLiveCheck(bool live)
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool."));
            var s = Session(doc);
            s.Add(Lint(doc));
            Assert.Single(s.Findings, f => f.Excerpt == "utilize");
            RunOf(doc, 0).Value = "Please use the tool.";
            s.AfterEdit(live ? Lint(doc) : null);
            RunOf(doc, 0).Value = "Please utilize the tool.";
            s.AfterEdit(live ? Lint(doc) : null);
            Assert.Equal("The change was undone. The issue is back.", s.Status);
            Assert.Single(s.Findings, f => f.Excerpt == "utilize" && !f.IsFixed);
            Assert.Single(s.Findings, f => f.Excerpt == "utilize");
        }

        [Fact]
        public void HandEdit_RuleIssue_WithLiveCheckOn_IsStruckThrough_NotRemoved()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool."));
            var s = Session(doc);
            s.Add(Lint(doc));
            RunOf(doc, 0).Value = "Please use the tool.";
            s.AfterEdit(Lint(doc));
            var f = s.Findings.Single(x => x.Excerpt == "utilize");
            Assert.True(f.IsFixed);
            Assert.EndsWith("You fixed 1 issue.", s.Status);
        }

        [Fact]
        public void JoinTwoParagraphs_LeavesTheirIssuesOpen_ThenFixingTheMovedOneMarksIt()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool.", "Then utilize the menu."));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            var body = BlockSelector.FindBody(doc.Document);
            RunOf(doc, 0).Value = "Please utilize the tool. Then utilize the menu.";
            body.RemoveChild(body.SelectNodes("p")[1]);
            s.AfterEdit(null);
            Assert.False(a.IsFixed);
            Assert.False(b.IsFixed);
            RunOf(doc, 0).Value = "Please utilize the tool. Then use the menu.";
            s.AfterEdit(null);
            Assert.True(b.IsFixed);
            Assert.False(a.IsFixed);
        }

        [Fact]
        public void MoveSentenceToAnotherParagraph_MarksTheAiIssueFixed_UndoBringsItBack()
        {
            var doc = FlareSimDocument.FromXml(Topic("Intro text here. Please utilize the tool now.", "Other."));
            var s = Session(doc);
            var f = Find(doc, "Please utilize the tool now", null);
            f.Source = "ai"; f.Layer = "ai"; f.Replacement = null;
            s.Add(new[] { f });
            RunOf(doc, 0).Value = "Intro text here.";
            RunOf(doc, 1).Value = "Other. Please utilize the tool now.";
            s.AfterEdit(null);
            Assert.True(f.IsFixed && f.FixedByEdit);
            RunOf(doc, 0).Value = "Intro text here. Please utilize the tool now.";
            RunOf(doc, 1).Value = "Other.";
            s.AfterEdit(null);
            Assert.False(f.IsFixed);
            Assert.Equal("The change was undone. The issue is back.", s.Status);
        }

        [Fact]
        public void UndoneChangeWithChangedSurroundings_KeepsItsTextChangedRow()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);
            s.Accept();
            doc.Undo();
            var run = TextRuns.Of(new List<XmlNode> { f.Block }).First(r => r.Value.Contains("utilize"));
            run.Value = run.Value.Replace("Please", "Kindly").Replace("Then utilize", "Then employ");
            Assert.Equal(1, s.RecheckApplied());
            s.AfterEdit(null);
            Assert.True(f.IsStale);
            Assert.False(f.FixedByEdit);
        }

        // ---- Mark as Fixed, the write events ------------------------------------------------------

        private static void Watching(ReviewSession s, Action body)
        {
            s.WatchWrites(true);
            try { body(); }
            finally { s.WatchWrites(false); }
        }

        [Fact]
        public void ChangeOnAWordInsideAnAiExcerpt_DoesNotMarkTheAiIssueFixed()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool. Other words."));
            var s = Session(doc);
            var ai = Find(doc, "Please utilize the tool", null);
            ai.Source = "ai"; ai.Layer = "ai"; ai.Replacement = null;
            var rule = Find(doc, "utilize", "use");
            s.Add(new[] { ai, rule });
            Watching(s, () =>
            {
                s.Select(rule);
                Assert.Equal(AcceptOutcome.Applied, s.Accept());
                s.AfterEdit(null);
            });
            Assert.False(ai.IsFixed);
            Assert.True(ai.IsStale);
            Assert.DoesNotContain("You fixed", s.Status);
        }

        [Fact]
        public void AltTextAssist_DoesNotMarkOtherIssuesInTheParagraphFixed()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize it <img src=\"a.png\" alt=\"\" />"));
            var s = Session(doc);
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            Watching(s, () =>
            {
                var block = BlockSelector.FindBody(doc.Document).SelectNodes("p")[0];
                AltText.Apply(doc, block, "a.png", "A chart");
                s.AfterEdit(null);
            });
            Assert.False(f.IsFixed);
        }

        [Fact]
        public void TypedFixThenChangeBeforeThePause_CountsTheTypedFixAsTheWriters()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            Watching(s, () =>
            {
                RunOf(doc, 0).Value = "Please use the tool. Then utilize the menu.";
                s.Select(b);
                s.Accept();
            });
            Assert.True(a.FixedByEdit);
            Assert.False(b.FixedByEdit);
            Assert.True(b.IsFixed);
        }

        [Fact]
        public void TypedFixThenChangeOnTheSameIssue_SkipsTheWrite_AndShowsFixed()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            AcceptOutcome outcome = AcceptOutcome.Applied;
            Watching(s, () =>
            {
                RunOf(doc, 0).Value = "Please use the tool. Then utilize the menu.";
                var undo = doc.UndoCount;
                s.Select(a);
                outcome = s.Accept();
                Assert.Equal(undo, doc.UndoCount);
            });
            Assert.Equal(AcceptOutcome.None, outcome);
            Assert.True(a.FixedByEdit);
            Assert.EndsWith("You fixed 1 issue.", s.Status);
        }

        [Fact]
        public void ChangeAll_WithATypedFixPending_ChangesTheRest_AndDoesNotThrow()
        {
            var doc = FlareSimDocument.FromXml(Topic("Alpha utilize one.", "Beta utilize two. Gamma utilize three."));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1); var c = Find(doc, "utilize", "use", 2);
            s.Add(new[] { a, b, c });
            Watching(s, () =>
            {
                RunOf(doc, 0).Value = "Alpha use one.";
                s.Select(b);
                s.ChangeAll();
            });
            Assert.True(b.Written);
            Assert.True(c.Written);
            Assert.False(a.Written);
            Assert.True(a.FixedByEdit);
            Assert.DoesNotContain("Skipped", s.Status);
        }

        [Fact]
        public void MarkAsFixed_MarksAnyIssue_RecordsNothing_AndSelectsTheNext()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            s.Select(a);
            s.MarkFixed();
            Assert.True(a.IsFixed && a.MarkedFixed);
            Assert.Same(b, s.Current);
            Assert.EndsWith("Marked as fixed.", s.Status);
        }

        [Fact]
        public void MarkAsFixed_OnATextChangedRow_StrikesItThrough()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool."));
            var s = Session(doc);
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);
            s.Accept();
            doc.Undo();
            var run = TextRuns.Of(new List<XmlNode> { f.Block }).First(r => r.Value.Contains("utilize"));
            run.Value = run.Value.Replace("Please", "Kindly").Replace("the tool", "a tool");
            Assert.Equal(1, s.RecheckApplied());
            Assert.True(f.IsStale);
            s.Select(f);
            s.MarkFixed();
            Assert.True(f.IsFixed);
            Assert.False(f.IsStale);
        }

        [Fact]
        public void MarkAsFixed_ThenEditAnotherParagraph_StaysFixed()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool.", "Second paragraph."));
            var s = Session(doc);
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);
            s.MarkFixed();
            RunOf(doc, 1).Value = "Second paragraph, edited.";
            s.AfterEdit(null);
            Assert.True(f.IsFixed);
            Assert.True(f.MarkedFixed);
        }

        [Fact]
        public void MarkAsFixed_WithLiveCheckOn_ThenTypeRightBeforeIt_StaysFixed_AndNoSecondRow()
        {
            var doc = FlareSimDocument.FromXml(Topic("Remember to always carefully utilize the tool."));
            var s = Session(doc);
            s.Add(Lint(doc));
            var f = s.Findings.Single(x => x.Excerpt == "utilize");
            s.Select(f);
            s.MarkFixed();
            RunOf(doc, 0).Value = "Oh. Remember to always carefully utilize the tool.";
            s.AfterEdit(Lint(doc));
            Assert.Single(s.Findings, x => x.Excerpt == "utilize");
            Assert.True(s.Findings.Single(x => x.Excerpt == "utilize").MarkedFixed);
        }

        [Fact]
        public void MarkAsFixed_OnAnIssueWithoutAnExcerpt_DoesNotHideALaterHitOfThatRule()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool."));
            var s = Session(doc);
            var structure = Find(doc, "utilize", "use");
            structure.Excerpt = ""; structure.RuleId = "x";
            s.Add(new[] { structure });
            s.Select(structure);
            s.MarkFixed();
            var later = Find(doc, "utilize", "use");
            later.RuleId = "x";
            s.Add(new[] { later });
            Assert.Contains(later, s.Findings);
        }

        [Fact]
        public void MarkAsFixed_LintAgainBringsItBack_WhenTheTextStillHasIt()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool."));
            var s = Session(doc);
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);
            s.MarkFixed();
            s.Clear();
            var fresh = Find(doc, "utilize", "use");
            s.Add(new[] { fresh });
            Assert.Contains(fresh, s.Findings);
            Assert.False(fresh.IsFixed);
        }

        [Fact]
        public void SelectedIssueFixedByEdit_StaysSelected_WithChangeDisabled()
        {
            var doc = FlareSimDocument.FromXml(Topic(TwoWords));
            var s = Session(doc);
            var a = Find(doc, "utilize", "use"); var b = Find(doc, "utilize", "use", 1);
            s.Add(new[] { a, b });
            s.Select(a);
            RunOf(doc, 0).Value = "Please use the tool. Then utilize the menu.";
            s.AfterEdit(null);
            Assert.Same(a, s.Current);
            Assert.False(a.CanFix);
        }

        private sealed class ScenarioHost : IReviewHost
        {
            public bool Confirm(string message, string action) { return true; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { return null; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline) { return null; }
            public IDocumentAdapter OpenDocument(string path) { return null; }
            public bool PreviewBatch(BatchPlan plan) { return true; }
            public bool SaveAndClose(IDocumentAdapter doc) { return false; }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { }
        }
    }
}
