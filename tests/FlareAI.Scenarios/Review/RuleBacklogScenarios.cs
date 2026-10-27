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
using Xunit.Abstractions;

namespace FlareAI.Scenarios.Review
{
    /// <summary>
    /// A big, messy project check worked one rule at a time (#20): the 151-file legacy corpus project, linted from disk,
    /// grouped by rule, fixed rule by rule as tracked changes through the simulated editor.
    /// </summary>
    public class RuleBacklogScenarios
    {
        private readonly ITestOutputHelper _out;
        public RuleBacklogScenarios(ITestOutputHelper output) { _out = output; }

        private sealed class Check : IDisposable
        {
            public readonly FakeWorkspace Ws = FakeWorkspace.FromCorpus(@"v2\legacy");
            public readonly BacklogHost Host;
            public readonly ReviewSession Session;
            public readonly List<StyleFinding> Found;
            public readonly List<string> Feedback = new List<string>();
            public readonly List<string> ProjectRuleChanges = new List<string>();
            private readonly List<string> _readOnly = new List<string>();

            public Check(Action<FakeWorkspace> before = null)
            {
                if (before != null) before(Ws);
                Host = new BacklogHost(Ws);
                var files = ActionRunner.ProjectFiles(Ws.ProjectFile);
                var style = new StyleEngine(new[] { Repo.StyleGuide }, Repo.StyleGuide);
                var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(style, files[0]), 16000, style, files[0]);
                Found = runner.LintFiles(files);
                // The topic the writer had open when they checked the project: one without suggestions, so every fix is in a file the plugin opens.
                var active = Ws.Open(files.First(f => f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) && !Found.Any(x => string.Equals(x.DocumentPath, f, StringComparison.OrdinalIgnoreCase))));
                Session = new ReviewSession(runner, active, Host, null, true)
                {
                    CurrentTeam = () => null,
                    RecordFeedback = (f, topic, decision, reason) => { Feedback.Add(decision); return null; },
                    IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    SetProjectRule = (path, f, setting, reason, by) => ProjectRuleChanges.Add(f.RuleId + "=" + setting),
                    ProjectOverridesPath = p => Path.Combine(Ws.ProjectDir, "overrides.ini")
                };
                Session.Add(Found);
            }

            /// <summary>Marks a file read-only (the attribute the batch checks), undone on dispose.</summary>
            public void ReadOnly(string path)
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                _readOnly.Add(path);
            }

            public void Dispose()
            {
                foreach (var p in _readOnly) File.SetAttributes(p, File.GetAttributes(p) & ~FileAttributes.ReadOnly);
                Ws.Dispose();
            }
        }

        /// <summary>The pane's host over the workspace: files open as the plugin opens them, saved and closed after a batch.</summary>
        private sealed class BacklogHost : IReviewHost
        {
            private readonly FakeWorkspace _ws;
            public BacklogHost(FakeWorkspace ws) { _ws = ws; }

            public readonly List<BatchPlan> Previewed = new List<BatchPlan>();
            public bool PreviewAnswer = true;
            /// <summary>What each saved file looked like when the batch closed it: path → (undo steps, untracked writes).</summary>
            public readonly Dictionary<string, Tuple<int, int>> Closed = new Dictionary<string, Tuple<int, int>>(StringComparer.OrdinalIgnoreCase);
            public RuleSettingChoice RuleChoice;

            public bool Confirm(string message, string action) { return true; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { return RuleChoice; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline) { return null; }
            public IDocumentAdapter OpenDocument(string path) { return _ws.OpenForPlugin(path); }
            public bool PreviewBatch(BatchPlan plan) { Previewed.Add(plan); return PreviewAnswer; }
            public bool SaveAndClose(IDocumentAdapter doc)
            {
                var d = (FlareSimDocument)doc;
                if (!d.OpenedByPlugin) return false;
                Closed[d.SourcePath] = Tuple.Create(d.UndoCount, d.UntrackedWrites.Count);
                d.Save();
                d.Close();
                return true;
            }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { }
        }

        private static BacklogRule BiggestAutomatic(ReviewSession s, int minFiles = 3)
        {
            return s.Backlog().First(r => r.Automatic.Count == r.Occurrences && r.Files.Count >= minFiles && r.Snippets.Count == 0);
        }

        [Fact]
        public void Backlog_GroupsTheLegacyProject_ByRule_MostFrequentFirst_WithCountsAndAPlainReason()
        {
            using (var c = new Check())
            {
                var s = c.Session;
                var rules = s.Backlog();
                var open = s.Open();
                foreach (var r in rules) _out.WriteLine(r.Occurrences + "\t" + r.Files.Count + "\t" + r.Automatic.Count + "\t" + r.Key + "\t" + r.Title + "\t" + r.Reason);
                _out.WriteLine(s.Summary());

                Assert.True(open.Count > 100, "a big backlog: " + open.Count);
                Assert.True(rules.Count > 5);
                Assert.Equal(open.Count, rules.Sum(r => r.Occurrences));                      // every open suggestion is under exactly one rule
                Assert.Equal(open.Count, rules.SelectMany(r => r.Findings).Distinct().Count());
                for (var i = 1; i < rules.Count; i++) Assert.True(rules[i - 1].Occurrences >= rules[i].Occurrences, "sorted by count");
                foreach (var r in rules)
                {
                    Assert.All(r.Findings, f => Assert.Equal(r.Key, RuleBacklog.KeyOf(f)));
                    Assert.Equal(r.Findings.Select(f => f.DocumentPath.ToLowerInvariant()).Distinct().Count(), r.Files.Count);
                    Assert.False(string.IsNullOrWhiteSpace(r.Reason), r.Key + " has no reason");
                    Assert.False(string.IsNullOrWhiteSpace(r.Title));
                    Assert.DoesNotContain("Google.", r.Title);                                // a heading, not a rule reference
                }

                // The headline counts what the list shows.
                var files = open.Select(f => f.DocumentPath.ToLowerInvariant()).Distinct().Count();
                var auto = open.Where(RuleBacklog.IsAutomatic).ToList();
                var autoRules = auto.Select(RuleBacklog.KeyOf).Distinct().Count();
                Assert.Equal(open.Count + " issues in " + files + " files. " + auto.Count + " can be changed automatically across " + autoRules + " rules.", s.Summary());
                Assert.Equal(auto.Count, s.AutomaticCandidates().Count);
            }
        }

        [Fact]
        public void FixAll_ForOneRule_WritesTrackedChangesInEveryFile_OneUndoStepEach_SkippingReadOnlyAndChangedFiles()
        {
            using (var c = new Check())
            {
                var s = c.Session;
                var rule = BiggestAutomatic(s);
                var before = s.Summary();
                var files = rule.Files.ToList();
                var readOnly = files[0];
                var changed = files[1];
                c.ReadOnly(readOnly);
                var readOnlyBytes = File.ReadAllBytes(readOnly);
                // The writer has one of the files open and edited the flagged words since the check.
                var open = c.Ws.Open(changed);
                var excerpts = rule.Findings.Where(x => string.Equals(x.DocumentPath, changed, StringComparison.OrdinalIgnoreCase)).Select(x => x.Excerpt).ToList();
                foreach (var run in TextRuns.Of(new List<XmlNode> { BlockSelector.FindBody(open.Document) }))
                    if (excerpts.Any(e => run.Value.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0)) run.Value = "Rewritten since the check.";

                s.FixRule(rule);

                // One preview, listing this rule's files and counts, with the read-only file already set aside.
                var plan = Assert.Single(c.Host.Previewed);
                var group = Assert.Single(plan.Groups);
                Assert.Equal(rule.Occurrences - rule.Findings.Count(f => f.DocumentPath == readOnly), group.Findings.Count);
                Assert.Equal(files.Count - 1, group.Files.Count);
                Assert.Equal("read-only", Assert.Single(plan.Skipped).Reason);

                // Every other file: saved with tracked changes, written in one undo step, nothing untracked.
                var written = files.Where(p => p != readOnly && p != changed).ToList();
                Assert.NotEmpty(written);
                foreach (var p in written)
                {
                    Assert.True(c.Host.Closed.ContainsKey(p), "saved and closed: " + p);
                    Assert.Equal(1, c.Host.Closed[p].Item1);
                    Assert.Equal(0, c.Host.Closed[p].Item2);
                    Assert.Contains("MadCap:change", File.ReadAllText(p));
                }
                Assert.Equal(readOnlyBytes, File.ReadAllBytes(readOnly));                       // read-only: not touched
                Assert.Equal(0, open.UndoCount);                                                  // changed since the check: not touched
                Assert.True(rule.Findings.Where(f => f.DocumentPath == changed).All(f => f.IsStale));
                Assert.True(rule.Findings.Where(f => written.Contains(f.DocumentPath)).All(f => f.IsFixed));

                // The status says what happened; the rule is down to the read-only file.
                Assert.Contains("Skipped", s.Status);
                Assert.Contains("read-only", plan.Skipped[0].Reason);
                var left = s.FindBacklogRule(rule.Key);
                Assert.NotNull(left);
                Assert.Equal(new[] { readOnly }, left.Files.ToArray());
                Assert.NotEqual(before, s.Summary());
                Assert.All(c.Feedback, d => Assert.Equal("accepted", d));
            }
        }

        [Fact]
        public void FixAll_CancelledPreview_WritesNothing()
        {
            using (var c = new Check())
            {
                var s = c.Session;
                c.Host.PreviewAnswer = false;
                var rule = BiggestAutomatic(s);

                s.FixRule(rule);

                Assert.Single(c.Host.Previewed);
                Assert.Empty(c.Ws.OpenedByPlugin);
                Assert.Empty(c.Host.Closed);
                Assert.EndsWith("Nothing was changed.", s.Status);
                Assert.Equal(rule.Occurrences, s.FindBacklogRule(rule.Key).Occurrences);
            }
        }

        [Fact]
        public void FixAllAutomatic_PreviewsEveryRule_AndStopLeavesTheRemainingFilesUntouched()
        {
            using (var c = new Check())
            {
                var s = c.Session;
                var auto = s.AutomaticCandidates();
                var autoFiles = auto.Select(f => f.DocumentPath.ToLowerInvariant()).Distinct().Count();
                BatchRun seen = null;
                s.RunBatch = run => { seen = run; run.Step(); run.Step(); run.Cancel(); run.Step(); };   // Stop after two files

                s.FixAllAutomatic();

                var plan = Assert.Single(c.Host.Previewed);
                Assert.Equal(auto.Count, plan.SelectedCount);
                Assert.Equal(autoFiles, plan.SelectedFileCount);
                Assert.Equal(auto.Select(RuleBacklog.KeyOf).Distinct().Count(), plan.Groups.Count);
                Assert.True(seen.IsFinished && seen.IsCancelled);
                Assert.Equal(2, seen.FilesDone);
                Assert.Equal(autoFiles - 2, seen.Result.NotStarted.Count);
                Assert.Equal(2, c.Host.Closed.Count);
                Assert.Equal(2, c.Ws.OpenedByPlugin.Count);                                       // nothing past the stop was opened
                Assert.Contains("Stopped. " + (autoFiles - 2) + " files were not changed.", s.Status);
                Assert.True(s.AutomaticCandidates().Count < auto.Count);
                Assert.True(s.AutomaticCandidates().Count > 0);                                   // the rest is still there to fix
            }
        }

        [Fact]
        public void FixAll_ThatReachesASharedSnippet_FlagsItInThePreview()
        {
            using (var c = new Check(ws =>
            {
                // The legacy project's warning snippet, included by 30 topics, gets a word the guide fixes.
                var path = ws.PathOf(@"Content\Resources\Snippets\BackupWarning.flsnp");
                File.WriteAllText(path, File.ReadAllText(path).Replace("before you continue.", "before you continue. Please utilize the backup tool."));
            }))
            {
                var s = c.Session;
                var snippet = c.Ws.PathOf(@"Content\Resources\Snippets\BackupWarning.flsnp");
                var rule = s.Backlog().FirstOrDefault(r => r.Snippets.Any(p => string.Equals(p, snippet, StringComparison.OrdinalIgnoreCase)) && r.Automatic.Count > 0);
                Assert.NotNull(rule);

                s.FixRule(rule);

                var plan = Assert.Single(c.Host.Previewed);
                var shared = Assert.Single(plan.SharedSnippets);
                var includers = ActionRunner.ProjectFiles(snippet).Count(f => f != snippet && File.ReadAllText(f).Contains("BackupWarning.flsnp"));
                Assert.True(includers >= 30);
                Assert.Equal(includers, shared.Uses);
                Assert.Contains("BackupWarning.flsnp (used in " + includers + " topics)", plan.SnippetNote());
                Assert.Contains("changes every topic that includes it", plan.SnippetNote());
                Assert.Contains(plan.Groups[0].Files, f => f.Contains("snippet, used in " + includers + " topics"));
                Assert.Contains("MadCap:change", File.ReadAllText(snippet));                      // written, tracked, once
                Assert.Equal(1, c.Host.Closed[snippet].Item1);
            }
        }

        [Fact]
        public void TurnOffRule_ForAGroup_RemovesTheGroup()
        {
            using (var c = new Check())
            {
                var s = c.Session;
                var rule = s.Backlog()[0];
                c.Host.RuleChoice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "legacy content, not ours" };

                s.TurnOffRule(rule);

                Assert.Null(s.FindBacklogRule(rule.Key));
                Assert.DoesNotContain(s.Findings, f => RuleBacklog.KeyOf(f) == rule.Key);
                Assert.Contains(rule.Findings[0].RuleId + "=off", c.ProjectRuleChanges);
                Assert.Contains("Rule disabled for this project", s.Status);
                Assert.StartsWith((s.Open().Count) + " issues in ", s.Summary());
            }
        }

        [Fact]
        public void Skip_HidesTheGroupForTheSession_AndKeepsItOutOfFixAllAutomatic()
        {
            using (var c = new Check())
            {
                var s = c.Session;
                var rule = BiggestAutomatic(s, 1);
                var auto = s.AutomaticCandidates().Count;

                Assert.False(s.IsSkipped(rule.Findings[0]));
                s.SkipRule(rule);
                Assert.True(s.IsSkipped(rule.Findings[0]));

                Assert.Null(s.FindBacklogRule(rule.Key));
                Assert.Equal(auto - rule.Automatic.Count, s.AutomaticCandidates().Count);
                Assert.EndsWith("1 rule skipped for now.", s.Summary());
                Assert.Contains("Skipped " + rule.Title + " for now.", s.Status);
                Assert.Empty(c.Feedback);                                                         // nothing recorded

                s.ShowSkippedRules();
                Assert.False(s.IsSkipped(rule.Findings[0]));
                Assert.False(s.IsSkipped(null));
                Assert.NotNull(s.FindBacklogRule(rule.Key));
            }
        }

        [Fact]
        public void ReviewOneByOne_StaysWithTheRule_ThenGoesBackToAllRules()
        {
            using (var c = new Check())
            {
                var s = c.Session;
                var rule = s.Backlog().First(r => r.Occurrences >= 3);
                var seen = new List<StyleFinding>();

                s.ReviewRule(rule);
                Assert.Equal(rule.Key, s.FocusRule);
                while (s.Current != null && seen.Count <= rule.Occurrences)
                {
                    Assert.Equal(rule.Key, RuleBacklog.KeyOf(s.Current));
                    seen.Add(s.Current);
                    s.Skip();
                    if (s.Current == seen[0]) break;   // wrapped: every one seen
                }
                Assert.Equal(rule.Occurrences, seen.Distinct().Count());

                // Applying them one by one ends the review after the last.
                foreach (var f in rule.Findings.Skip(1)) f.IsFixed = true;
                s.Select(rule.Findings[0]);
                s.Skip();
                Assert.Equal(rule.Findings[0], s.Current);   // the only one left
                rule.Findings[0].IsFixed = true;
                s.Next();
                Assert.Null(s.FocusRule);
                Assert.Null(s.Current);
                Assert.Contains("Done with " + rule.Title + ".", s.Status);
            }
        }
    }
}
