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
