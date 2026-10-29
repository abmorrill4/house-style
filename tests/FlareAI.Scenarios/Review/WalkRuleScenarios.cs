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
using Xunit.Abstractions;

namespace FlareAI.Scenarios.Review
{
    /// <summary>Walking through one rule place by place (#252): the counter, Change, Ignore and Next, and the end of the walk.</summary>
    public class WalkRuleScenarios
    {
        private readonly ITestOutputHelper _out;
        public WalkRuleScenarios(ITestOutputHelper output) { _out = output; }

        private sealed class Check : IDisposable
        {
            public readonly FakeWorkspace Ws = FakeWorkspace.FromCorpus(@"v2\legacy");
            public readonly WalkHost Host;
            public readonly ReviewSession Session;
            public readonly List<StyleFinding> Found;

            public Check()
            {
                Host = new WalkHost(Ws);
                var files = ActionRunner.ProjectFiles(Ws.ProjectFile);
                var style = new StyleEngine(new[] { Repo.StyleGuide }, Repo.StyleGuide);
                var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(style, files[0]), 16000, style, files[0]);
                Found = runner.LintFiles(files);
                var active = Ws.Open(files.First(f => f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) && !Found.Any(x => string.Equals(x.DocumentPath, f, StringComparison.OrdinalIgnoreCase))));
                Session = new ReviewSession(runner, active, Host, null, true)
                {
                    CurrentTeam = () => null,
                    RecordFeedback = (f, topic, decision, reason) => null,
                    IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    ProjectOverridesPath = p => Path.Combine(Ws.ProjectDir, "overrides.ini")
                };
                Session.Add(Found);
            }

            public void Dispose() { Ws.Dispose(); }
        }

        private sealed class WalkHost : IReviewHost
        {
            private readonly FakeWorkspace _ws;
            public WalkHost(FakeWorkspace ws) { _ws = ws; }

            public bool Confirm(string message, string action) { return true; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { return null; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline) { return null; }
            public IDocumentAdapter OpenDocument(string path) { return _ws.OpenForPlugin(path); }
            public bool PreviewBatch(BatchPlan plan) { return true; }
            public bool SaveAndClose(IDocumentAdapter doc)
            {
                var d = (FlareSimDocument)doc;
                if (!d.OpenedByPlugin) return false;
                d.Save();
                d.Close();
                return true;
            }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { }
        }

        [Fact]
        public void Walk_CountsFromOneToTotal_ChangeIgnoreAndNextAdvance_IgnoreKeepsTheTotal()
        {
            using (var c = new Check())
            {
                var s = c.Session;
                var rule = s.Backlog().First(r => r.Automatic.Count == r.Occurrences && r.Occurrences >= 5 && r.Snippets.Count == 0);
                var n = rule.Occurrences;

                s.ReviewRule(rule);
                Assert.True(s.InWalk);
                Assert.False(s.WalkFinished);
                Assert.Equal(n, s.WalkTotal);
                Assert.Equal(1, s.WalkPosition);
                Assert.Contains(n + " places in ", s.Status);

                s.Accept();                                  // Change
                Assert.Equal(2, s.WalkPosition);
                Assert.Equal(n, s.WalkTotal);

                s.Reject("not for this text");               // Ignore keeps its slot and the total
                Assert.Equal(3, s.WalkPosition);
                Assert.Equal(n, s.WalkTotal);

                s.Next();
                Assert.Equal(4, s.WalkPosition);

                while (!s.WalkFinished) s.Next();
                Assert.Null(s.Current);
                Assert.Equal(0, s.WalkPosition);
                Assert.Equal(n, s.WalkTotal);
                Assert.Equal(rule.Key, s.FocusRule);
                Assert.StartsWith("You've been through this rule.", s.WalkFinishedText);
                _out.WriteLine(s.WalkFinishedText);
            }
        }
    }
}
