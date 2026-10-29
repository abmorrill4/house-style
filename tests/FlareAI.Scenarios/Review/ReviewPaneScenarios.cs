using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Review
{
    /// <summary>
    /// The review pane against the simulated editor: a fix that Ctrl+Z took back is live again (#94), and a whole file
    /// can be opened from the project list without selecting text (#40).
    /// </summary>
    public class ReviewPaneScenarios
    {
        private const string TopicXml = "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head /><body><h1>Start</h1>" +
            "<p>Please utilize the tool. Then utilize the menu.</p><p>Other paragraph.</p></body></html>";

        private static StyleFinding Find(FlareSimDocument doc, string text, string replacement, int occurrence = 0)
        {
            var seen = 0;
            foreach (var run in TextRuns.Of(new List<XmlNode> { BlockSelector.FindBody(doc.Document) }))
            {
                var at = -1;
                while ((at = run.Value.IndexOf(text, at + 1, StringComparison.Ordinal)) >= 0)
                {
                    if (seen++ < occurrence) continue;
                    return FindingLocator.FromLint(new LintHit { Start = at, Length = text.Length, Match = text, Replacement = replacement, Rule = "Google.WordList", Severity = "warning", Message = "Use plain words.", Layer = "google" }, run);
                }
            }
            throw new InvalidOperationException("no " + text);
        }

        private static ReviewSession Session(FlareSimDocument doc, ScenarioHost host, bool project = false)
        {
            var style = new StyleEngine(new[] { Repo.StyleGuide }, Repo.StyleGuide);
            var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(style, doc.SourcePath), 16000, style, doc.SourcePath);
            return new ReviewSession(runner, doc, host, null, project)
            {
                CurrentTeam = () => null,
                RecordFeedback = (f, topic, decision, reason) => null,
                IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SnippetUsage = p => 1
            };
        }

        [Fact]
        public void Apply_ThenUndo_MakesTheFindingLiveAgain_AndApplyWorksAgain()
        {
            var doc = FlareSimDocument.FromXml(TopicXml);
            var s = Session(doc, new ScenarioHost());
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);

            Assert.Equal(AcceptOutcome.Applied, s.Accept());
            Assert.True(f.IsFixed);

            doc.Undo();   // Ctrl+Z in the editor
            Assert.Equal(1, s.RecheckApplied());

            Assert.False(f.IsFixed);
            Assert.False(f.IsStale);
            Assert.Contains(f, s.Open());
            s.Select(f);
            Assert.Equal(AcceptOutcome.Applied, s.Accept());   // and it can be applied again
            Assert.True(f.IsFixed);
        }

        [Fact]
        public void Undo_IsNoticedWhenTheRowIsSelected_AndWhenTheLiveCheckRuns()
        {
            var doc = FlareSimDocument.FromXml(TopicXml);
            var s = Session(doc, new ScenarioHost());
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);
            s.Accept();
            doc.Undo();

            s.OnSelected(f);   // clicking the row is enough
            Assert.False(f.IsFixed);

            s.Select(f);
            s.Accept();
            doc.Undo();
            s.RefreshLint(new List<StyleFinding>());   // so is the next live lint (the editor changed)
            Assert.DoesNotContain(f, s.Findings);      // a fresh lint finding takes its place: no duplicate of the revived one
        }

        [Fact]
        public void Apply_ThenEditElsewhere_StaysApplied()
        {
            var doc = FlareSimDocument.FromXml(TopicXml);
            var s = Session(doc, new ScenarioHost());
            var first = Find(doc, "utilize", "use");
            var second = Find(doc, "utilize", "use", 1);
            s.Add(new[] { first, second });
            s.Select(second);
            Assert.Equal(AcceptOutcome.Applied, s.Accept());

            // the writer types in another paragraph, and earlier in the same one
            var other = (XmlText)BlockSelector.FindBody(doc.Document).LastChild.FirstChild;
            other.Value = "Other paragraph, edited.";
            var head = TextRuns.Of(new List<XmlNode> { second.Block }).First();
            head.Value = "Hey. " + head.Value;

            Assert.Equal(0, s.RecheckApplied());
            Assert.True(second.IsFixed);
            Assert.False(second.IsStale);
        }

        [Fact]
        public void Apply_Undo_AndTheTextAroundItChanged_IsStale_CheckThisParagraphAgain()
        {
            var doc = FlareSimDocument.FromXml(TopicXml);
            var s = Session(doc, new ScenarioHost());
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);
            s.Accept();
            doc.Undo();
            // the original word is back, but the sentence before it was rewritten as well
            var run = TextRuns.Of(new List<XmlNode> { f.Block }).First(r => r.Value.Contains("utilize"));
            run.Value = run.Value.Replace("Please", "Kindly").Replace("Then utilize", "Then employ");

            Assert.Equal(1, s.RecheckApplied());

            Assert.False(f.IsFixed);
            Assert.True(f.IsStale);
            Assert.Equal("The text changed. Lint the paragraph again.", s.Status);
            s.Current = f;
            Assert.Equal(AcceptOutcome.Stale, s.Accept());
        }

        [Fact]
        public void Apply_ThenAcceptTheTrackedChange_StaysApplied()
        {
            var doc = FlareSimDocument.FromXml(TopicXml);
            var s = Session(doc, new ScenarioHost());
            var f = Find(doc, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);
            s.Accept();

            doc.AcceptAll();

            Assert.Equal(0, s.RecheckApplied());
            Assert.True(f.IsFixed);
        }

        [Fact]
        public void OpenFile_OpensTheFindingsFile_WithoutSelectingText()
        {
            // A project report opens only files under a project folder it finds on disk, so this one has a .flprj.
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flareai-openfile-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "Content", "Resources", "Snippets"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "P.flprj"), "<Project />");
            try
            {
            var topic = FlareSimDocument.FromXml(TopicXml, System.IO.Path.Combine(root, "Content", "topic.htm"));
            var snippet = FlareSimDocument.FromXml(TopicXml, System.IO.Path.Combine(root, "Content", "Resources", "Snippets", "shared.flsnp"));
            var host = new ScenarioHost();
            host.Documents[topic.SourcePath] = topic;
            host.Documents[snippet.SourcePath] = snippet;
            var s = Session(topic, host, project: true);
            var inSnippet = Find(snippet, "utilize", "use");
            inSnippet.DocumentPath = snippet.SourcePath;
            var inTopic = Find(topic, "utilize", "use");
            inTopic.DocumentPath = topic.SourcePath;
            s.Add(new[] { inTopic, inSnippet });

            s.Select(inSnippet);
            Assert.Same(snippet, s.OpenFileOfCurrent());
            Assert.Equal(new[] { snippet.SourcePath }, host.Opened.ToArray());
            Assert.Null(snippet.SelectedRun);
            Assert.Equal(snippet.SourcePath, s.FilePathOf(inSnippet));

            Assert.Same(topic, s.OpenFile(inTopic));
            Assert.Equal(topic.SourcePath, host.Opened.Last());
            Assert.Null(topic.SelectedRun);
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch (Exception) { } }
        }

        [Fact]
        public void OpenFile_ATopicFinding_OpensTheCheckedTopic_AndSaysWhenItCannot()
        {
            var topic = FlareSimDocument.FromXml(TopicXml, @"C:\P\Content\topic.htm");
            var host = new ScenarioHost();
            var s = Session(topic, host);
            var f = Find(topic, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);

            Assert.Null(s.OpenFileOfCurrent());   // the host can't open it
            Assert.Equal(new[] { topic.SourcePath }, host.Opened.ToArray());
            Assert.StartsWith("Could not open", s.Status);
            Assert.Null(s.OpenFile(null));
        }

        private sealed class ScenarioHost : IReviewHost
        {
            public readonly Dictionary<string, IDocumentAdapter> Documents = new Dictionary<string, IDocumentAdapter>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Opened = new List<string>();

            public bool Confirm(string message, string action) { return true; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { return null; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline) { return null; }
            public IDocumentAdapter OpenDocument(string path)
            {
                Opened.Add(path);
                IDocumentAdapter d;
                return Documents.TryGetValue(path, out d) ? d : null;
            }
            public bool PreviewBatch(BatchPlan plan) { return true; }
            public bool SaveAndClose(IDocumentAdapter doc) { return false; }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { }
        }
    }
}
