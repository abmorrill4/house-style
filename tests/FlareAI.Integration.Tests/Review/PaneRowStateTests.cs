using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using FlareAI.Integration.Tests.Support;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Integration.Tests.Review
{
    /// <summary>A row has one state across the This Topic and Project tabs (#450), Exempt stays listed (#448), Reopen Issue (#447).</summary>
    [Collection("Logging")]
    public class PaneRowStateTests
    {
        private static string Topic(string first)
        {
            return "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head /><body><h1>Start</h1><p>" + first + "</p><p>Other paragraph.</p></body></html>";
        }

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

        private static ReviewSession Session(FlareSimDocument doc, bool project)
        {
            var style = new StyleEngine(new[] { Repo.StyleGuide }, Repo.StyleGuide);
            var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(style, doc.SourcePath), 16000, style, doc.SourcePath);
            return new ReviewSession(runner, doc, new TestHost(), null, project)
            {
                CurrentTeam = () => null,
                RecordFeedback = (f, topic, decision, reason) => null,
                IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SnippetUsage = p => 1
            };
        }

        private static void Pair(ReviewSession a, ReviewSession b) { a.Peer = b; b.Peer = a; }

        private sealed class Tabs
        {
            public FlareSimDocument Doc;
            public ReviewSession Topic, Project;
            public StyleFinding T, P;
        }

        private static Tabs Both(string text = "Please utilize the tool.")
        {
            var doc = FlareSimDocument.FromXml(Topic(text));
            var t = new Tabs { Doc = doc, Topic = Session(doc, false), Project = Session(doc, true) };
            Pair(t.Topic, t.Project);
            t.T = Find(doc, "utilize", "use"); t.P = Find(doc, "utilize", "use");
            t.Topic.Add(new[] { t.T }); t.Project.Add(new[] { t.P });
            return t;
        }

        [Fact]
        public void ChangeInThisTopic_ShowsTheProjectRowFixed_AndTheCountDrops()
        {
            var t = Both();
            Assert.Equal(1, t.Project.Open().Count);
            t.Topic.Select(t.T);
            Assert.Equal(AcceptOutcome.Applied, t.Topic.Accept());
            Assert.True(t.P.IsFixed);
            Assert.Equal(0, t.Project.Open().Count);
        }

        [Fact]
        public void ChangeInProject_ShowsTheTopicRowFixed()
        {
            var t = Both();
            t.Project.Select(t.P);
            t.Project.Accept();
            Assert.True(t.T.IsFixed);
            Assert.Equal(0, t.Topic.Open().Count);
        }

        [Fact]
        public void MarkAsFixed_CarriesOverBothWays_AndReopenCarriesBack()
        {
            var t = Both();
            t.Topic.Select(t.T); t.Topic.MarkFixed();
            Assert.True(t.P.IsFixed && t.P.MarkedFixed);
            t.Project.Select(t.P); t.Project.ReopenCurrent();
            Assert.False(t.T.IsFixed);
            Assert.False(t.T.MarkedFixed);
            Assert.Equal(1, t.Topic.Open().Count);
            t.Project.MarkFixed();
            Assert.True(t.T.IsFixed && t.T.MarkedFixed);
        }

        [Fact]
        public void ATypedFix_InThisTopic_ShowsInTheProjectTab()
        {
            var t = Both();
            var run = (XmlText)BlockSelector.FindBody(t.Doc.Document).SelectNodes("p")[0].FirstChild;
            run.Value = run.Value.Replace("utilize", "use");
            t.Topic.AfterEdit(null);
            Assert.True(t.T.IsFixed && t.T.FixedByEdit);
            Assert.True(t.P.IsFixed);
        }

        [Fact]
        public void TheSameTextTwice_ChangesOneRowAtATime()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool. Then utilize the menu."));
            var topic = Session(doc, false); var project = Session(doc, true);
            Pair(topic, project);
            var t0 = Find(doc, "utilize", "use"); var t1 = Find(doc, "utilize", "use", 1);
            var p0 = Find(doc, "utilize", "use"); var p1 = Find(doc, "utilize", "use", 1);
            topic.Add(new[] { t0, t1 }); project.Add(new[] { p0, p1 });
            topic.Select(t0); topic.MarkFixed();
            Assert.Equal(1, project.Findings.Count(f => f.IsFixed));
            Assert.Equal(1, project.Open().Count);
        }

        [Fact]
        public void AnIssueInOneListOnly_IsUnaffected()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool."));
            var topic = Session(doc, false); var project = Session(doc, true);
            Pair(topic, project);
            var t = Find(doc, "utilize", "use");
            topic.Add(new[] { t });
            topic.Select(t); topic.MarkFixed();
            Assert.Equal(0, project.Findings.Count);
        }

        [Fact]
        public void NewRows_TakeTheStateTheOtherTabAlreadyHas()
        {
            var doc = FlareSimDocument.FromXml(Topic("Please utilize the tool."));
            var topic = Session(doc, false); var project = Session(doc, true);
            Pair(topic, project);
            var p = Find(doc, "utilize", "use");
            project.Add(new[] { p });
            project.Select(p); project.MarkFixed();
            var t = Find(doc, "utilize", "use");
            topic.Add(new[] { t });
            Assert.True(t.IsFixed && t.MarkedFixed);
            Assert.Equal(0, topic.Open().Count);
        }

        [Fact]
        public void Reopen_IsOnlyForRowsMarkedByHand()
        {
            var t = Both();
            Assert.False(t.Topic.CanReopen(t.T));
            t.Topic.Select(t.T); t.Topic.MarkFixed();
            Assert.True(t.Topic.CanReopen(t.T));
            t.Topic.Select(t.T);
            t.Topic.ReopenCurrent();
            Assert.False(t.T.IsFixed);
            Assert.Contains("Reopened.", t.Topic.Status);
            t.Topic.ReopenCurrent();   // twice does nothing
            Assert.False(t.T.IsFixed);
            t.Topic.Select(t.T);
            Assert.Equal(AcceptOutcome.Applied, t.Topic.Accept());
            Assert.False(t.Topic.CanReopen(t.T));
        }

        [Fact]
        public void Reopen_AfterTheTextWasTyped_DropsOutAsFixed()
        {
            var t = Both();
            t.Topic.Select(t.T); t.Topic.MarkFixed();
            var run = (XmlText)BlockSelector.FindBody(t.Doc.Document).SelectNodes("p")[0].FirstChild;
            run.Value = run.Value.Replace("utilize", "use");
            t.Topic.Select(t.T);
            t.Topic.ReopenCurrent();
            Assert.True(t.T.IsFixed && t.T.FixedByEdit);
        }

        [Fact]
        public void Exempt_KeepsTheRowListed_CarriesOver_AndRemoveExemptionReopensBoth()
        {
            using (var fx = ReviewFixture.Scratch("<p>Send an e-mail to the team.</p>"))
            {
                var topic = fx.Session; var project = fx.NewSession(true);
                Pair(topic, project);
                fx.LintOnlyCheck(topic); fx.LintOnlyCheck(project);
                var t = topic.Findings.First(f => f.RuleId == "google:word-list/251");
                var p = project.Findings.First(f => f.RuleId == "google:word-list/251");
                var openBefore = topic.Open().Count;
                topic.Select(t);
                topic.Exempt(t, "Intended here.");
                Assert.Contains(t, topic.Findings);
                Assert.True(t.IsExempt && t.IsFixed);
                Assert.True(p.IsExempt && p.IsFixed);
                Assert.Equal(openBefore - 1, topic.Open().Count);
                Assert.Equal(1, topic.ExemptHidden);
                Assert.Equal(1, project.ExemptHidden);
                Assert.Equal(1, Exemptions.Load(fx.Doc.SourcePath).Entries.Count);
                Assert.False(topic.CanReopen(t));

                project.Select(p);
                project.RemoveExemptionCurrent();
                Assert.False(p.IsExempt || p.IsFixed);
                Assert.False(t.IsExempt || t.IsFixed);
                Assert.Equal(0, Exemptions.Load(fx.Doc.SourcePath).Entries.Count);
                Assert.Equal(openBefore, topic.Open().Count);
            }
        }
    }
}
