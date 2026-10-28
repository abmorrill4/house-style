using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Check project, then Apply a suggestion in another file and Ctrl+Z it in the editor (#94 for project findings): the row
    /// is live again when it is clicked, and when that document changes. A file the plugin opened only to write it is saved
    /// and closed, so its fix stays applied.
    /// </summary>
    public class ProjectUndoScenarios
    {
        private const string Topic = "Content/start.htm", Other = "Content/other.htm";

        private sealed class Project : IDisposable
        {
            public readonly FakeWorkspace Ws = FakeWorkspace.Empty();
            public readonly Host Host;
            public readonly ReviewSession Session;
            public readonly List<StyleFinding> Found;

            public Project(bool otherOpenByWriter, bool findOpen)
            {
                Ws.WriteFile(Topic, Xml("Start", "Read the start page."));
                Ws.WriteFile(Other, Xml("Other", "Please utilize the menu to continue."));
                var active = Ws.Open(Topic);
                if (otherOpenByWriter) Ws.Open(Other);
                Host = new Host(Ws);
                var style = new StyleEngine(new[] { Repo.StyleGuide }, Repo.StyleGuide);
                var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(style, active.SourcePath), 16000, style, active.SourcePath);
                Found = runner.LintFiles(new[] { Ws.PathOf(Topic), Ws.PathOf(Other) });
                Session = new ReviewSession(runner, active, Host, null, true)
                {
                    CurrentTeam = () => null,
                    RecordFeedback = (f, topic, decision, reason) => null,
                    IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    SnippetUsage = p => 1,
                    WhyNotWritable = p => null
                };
                if (findOpen)
                    Session.OpenedDocument = p => Ws.OpenDocuments.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.SourcePath), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));
                Session.Add(Found);
            }

            public StyleFinding Utilize { get { return Session.Findings.First(f => f.Excerpt == "utilize" && f.Replacement != null); } }
            public FlareSimDocument OtherDoc { get { return Ws.OpenDocuments.FirstOrDefault(d => d.SourcePath.EndsWith("other.htm", StringComparison.OrdinalIgnoreCase)); } }

            public void Dispose() { Ws.Dispose(); }
        }

        private static string Xml(string title, string p)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head><title>" + title +
                   "</title></head><body><h1>" + title + "</h1><p>" + p + "</p></body></html>";
        }

        [Theory]
        [InlineData(true)]    // the plugin asks the editor for the open document
        [InlineData(false)]   // the document the fix was written in
        public void ApplyInAnotherFile_ThenUndoThere_TheRowIsLiveAgain_WhenClicked(bool findOpen)
        {
            using (var p = new Project(otherOpenByWriter: true, findOpen: findOpen))
            {
                var f = p.Utilize;
                Assert.Equal(p.Ws.PathOf(Other), f.DocumentPath);
                p.Session.Select(f);
                Assert.Equal(AcceptOutcome.Applied, p.Session.Accept());
                Assert.True(f.IsFixed);
                Assert.True(f.Written);
                Assert.Equal(1, p.OtherDoc.UndoCount);   // one tracked change, in that file

                p.OtherDoc.Undo();   // Ctrl+Z in the editor, in that file

                p.Session.OnSelected(f);   // clicking the row
                Assert.False(f.IsFixed);
                Assert.False(f.IsStale);
                Assert.Contains(f, p.Session.Open());
                Assert.Equal("The change was undone. The issue is back.", p.Session.Status);
                Assert.Equal(AcceptOutcome.Applied, p.Session.Accept());   // and Apply works again
                Assert.True(f.IsFixed);
            }
        }

        [Fact]
        public void ApplyInAnotherFile_ThenUndoThere_IsNoticedWhenThatDocumentChanges()
        {
            using (var p = new Project(otherOpenByWriter: true, findOpen: true))
            {
                var f = p.Utilize;
                p.Session.Select(f);
                p.Session.Accept();
                p.OtherDoc.Undo();

                Assert.Equal(1, p.Session.RecheckApplied());   // what the pane runs on the document's change event
                Assert.False(f.IsFixed);
            }
        }

        [Fact]
        public void AFileOpenedOnlyToWriteTheFix_IsSavedAndClosed_AndTheFixStaysApplied()
        {
            using (var p = new Project(otherOpenByWriter: false, findOpen: true))
            {
                var f = p.Utilize;
                p.Session.Select(f);
                p.Session.Accept();
                Assert.True(f.IsFixed);
                Assert.Null(p.OtherDoc);   // saved and closed: no undo history left

                Assert.Equal(0, p.Session.RecheckApplied());
                p.Session.OnSelected(f);
                Assert.True(f.IsFixed);
            }
        }

        private sealed class Host : IReviewHost
        {
            private readonly FakeWorkspace _ws;
            public Host(FakeWorkspace ws) { _ws = ws; }
            public bool Confirm(string message, string action) { return true; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { return null; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline, bool required = false) { return null; }
            public IDocumentAdapter OpenDocument(string path) { return _ws.OpenForPlugin(path); }
            public bool PreviewBatch(BatchPlan plan) { return true; }
            public bool SaveAndClose(IDocumentAdapter doc)
            {
                var d = doc as FlareSimDocument;
                if (d == null || !d.OpenedByPlugin) return false;
                d.Save();
                d.Close();
                return true;
            }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { }
        }
    }
}
