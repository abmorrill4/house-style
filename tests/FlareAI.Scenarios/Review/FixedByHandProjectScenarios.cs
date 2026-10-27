using System;
using System.Collections.Generic;
using System.IO;
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
    /// Issues the writer fixes by hand in a project or folder report (#237): in another open file, in a closed file, and
    /// in a file the writer saves. Uses the same project setup as ProjectUndoScenarios.
    /// </summary>
    public class FixedByHandProjectScenarios
    {
        private const string Topic = "Content/start.htm", Other = "Content/other.htm";
        private const string OtherText = "Please utilize the menu to continue.";

        private sealed class Project : IDisposable
        {
            public readonly FakeWorkspace Ws = FakeWorkspace.Empty();
            public readonly Host Host;
            public readonly ReviewSession Session;
            public readonly List<StyleFinding> Found;

            public Project(bool otherOpenByWriter, bool findOpen, string otherText = OtherText)
            {
                Ws.WriteFile(Topic, Xml("Start", "Read the start page."));
                Ws.WriteFile(Other, Xml("Other", otherText));
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

            public StyleFinding Utilize { get { return Session.Findings.First(f => f.Excerpt == "utilize" && f.Replacement != null && f.DocumentPath != null && f.DocumentPath.EndsWith("other.htm", StringComparison.OrdinalIgnoreCase)); } }
            public FlareSimDocument OtherDoc { get { return Ws.OpenDocuments.FirstOrDefault(d => d.SourcePath.EndsWith("other.htm", StringComparison.OrdinalIgnoreCase)); } }

            public void Dispose() { Ws.Dispose(); }
        }

        private static string Xml(string title, string p)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head><title>" + title +
                   "</title></head><body><h1>" + title + "</h1><p>" + p + "</p></body></html>";
        }

        private static XmlText FirstParagraphText(FlareSimDocument doc)
        {
            return (XmlText)BlockSelector.FindBody(doc.Document).SelectNodes("p")[0].FirstChild;
        }

        [Fact]
        public void HandEdit_InAnotherOpenFile_MarksTheProjectIssueFixed()
        {
            using (var p = new Project(otherOpenByWriter: true, findOpen: true))
            {
                var before = p.Session.BacklogSummary();
                var u = p.Utilize;
                var text = FirstParagraphText(p.OtherDoc);
                text.Value = text.Value.Replace("utilize", "use");
                p.Session.AfterEdit(null);
                Assert.True(u.IsFixed && u.FixedByEdit);
                Assert.NotEqual(before, p.Session.BacklogSummary());
            }
        }

        [Fact]
        public void HandFix_OfOneOfThreeSameWords_InAProjectFile_MarksOnlyThatOne()
        {
            using (var p = new Project(otherOpenByWriter: true, findOpen: true, otherText: "First utilize this. Second utilize that. Third utilize more."))
            {
                var text = FirstParagraphText(p.OtherDoc);
                text.Value = text.Value.Replace("Second utilize", "Second use");
                p.Session.AfterEdit(null);
                var utilize = p.Session.Findings.Where(f => f.Excerpt == "utilize" && f.DocumentPath != null && f.DocumentPath.EndsWith("other.htm", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert.Equal(3, utilize.Count);
                var fixedOnes = utilize.Where(f => f.IsFixed).ToList();
                Assert.Single(fixedOnes);
                Assert.EndsWith("Second ", fixedOnes[0].Before);
            }
        }

        [Fact]
        public void HandEdit_InAClosedFile_ChangesNothing()
        {
            using (var p = new Project(otherOpenByWriter: false, findOpen: false))
            {
                var u = p.Utilize;
                File.WriteAllText(p.Ws.PathOf(Other), Xml("Other", "Please use the menu to continue."));
                p.Session.AfterEdit(null);
                Assert.False(u.IsFixed);
                Assert.False(u.FixedByEdit);
            }
        }

        [Fact]
        public void SavedFile_AfterAHandFix_MarksItsIssuesFixed_OtherFilesUntouched()
        {
            using (var p = new Project(otherOpenByWriter: true, findOpen: false))
            {
                var u = p.Utilize;
                var startBefore = p.Session.Findings.Where(f => f.DocumentPath != null && f.DocumentPath.EndsWith("start.htm", StringComparison.OrdinalIgnoreCase))
                                                    .Select(f => f.IsFixed).ToList();
                var text = FirstParagraphText(p.OtherDoc);
                text.Value = text.Value.Replace("utilize", "use");
                var path = p.Ws.PathOf(Other).ToUpperInvariant().Replace('\\', '/');
                Assert.Equal(1, p.Session.RecheckSaved(path, p.OtherDoc.Document));
                Assert.True(u.IsFixed);
                var startAfter = p.Session.Findings.Where(f => f.DocumentPath != null && f.DocumentPath.EndsWith("start.htm", StringComparison.OrdinalIgnoreCase))
                                                   .Select(f => f.IsFixed).ToList();
                Assert.Equal(startBefore, startAfter);
            }
        }

        [Fact]
        public void PluginOwnSave_DoesNotRunTheSaveCheck()
        {
            using (var p = new Project(otherOpenByWriter: true, findOpen: false))
            {
                var u = p.Utilize;
                var text = FirstParagraphText(p.OtherDoc);
                text.Value = text.Value.Replace("utilize", "use");
                var path = p.Ws.PathOf(Other).ToUpperInvariant().Replace('\\', '/');
                SelfSaves.Mark(path);
                Assert.Equal(0, p.Session.RecheckSaved(path, p.OtherDoc.Document));
                Assert.False(u.IsFixed);
            }
        }

        [Fact]
        public void ChangeInAProjectFile_DoesNotMarkAnOverlappingIssueFixed()
        {
            using (var p = new Project(otherOpenByWriter: true, findOpen: true))
            {
                var u = p.Utilize;
                var longer = new StyleFinding
                {
                    DocumentPath = u.DocumentPath,
                    Excerpt = "Please utilize the menu",
                    Before = "",
                    Replacement = null,
                    RuleId = u.RuleId,
                    Source = u.Source,
                    Layer = u.Layer,
                    Seen = -1,
                    SeenInBody = -1
                };
                p.Session.Add(new[] { longer });
                p.Session.WatchWrites(true);
                try
                {
                    p.Session.Select(u);
                    p.Session.Accept();
                    p.Session.AfterEdit(null);
                    Assert.False(longer.IsFixed);
                    Assert.DoesNotContain("You fixed", p.Session.Status);
                }
                finally { p.Session.WatchWrites(false); }
            }
        }

        private sealed class Host : IReviewHost
        {
            private readonly FakeWorkspace _ws;
            public Host(FakeWorkspace ws) { _ws = ws; }
            public bool Confirm(string message, string action) { return true; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { return null; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline) { return null; }
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
