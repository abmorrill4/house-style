using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using FlareAI.TestKit.Editor;
using FlareAI.Core.Llm;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;

namespace FlareAI.Scenarios.Support
{
    /// <summary>
    /// A writer's session: a copy of a corpus project, one topic open in the fake Flare editor, the bundled style guide, a
    /// scripted model (no network) and the review pane's decisions (<see cref="ReviewSession"/>) with the test answering its dialogs.
    /// </summary>
    public sealed class ReviewFixture : IDisposable
    {
        public readonly FakeWorkspace Ws;
        public readonly FlareSimDocument Doc;
        public StyleEngine Style { get; private set; }
        public readonly ILlmClient Llm;
        public ActionRunner Runner { get; private set; }
        public readonly ScenarioHost Host;
        public ReviewSession Session { get; private set; }
        public readonly string Original;
        /// <summary>The team context the pane works under (null: a writer with no team).</summary>
        public Team Team { get; set; }
        private readonly bool _projectReport;
        private readonly Func<StyleEngine> _newStyle;

        /// <param name="project">"DigitalBanking" (corpus/DigitalBanking) or "v2/legacy", "v2/end-user-help", … (corpus/v2/…).</param>
        /// <param name="newStyle">Makes the style engine (again, on <see cref="Restart"/>); default: the bundled guide alone.</param>
        public ReviewFixture(string project, string topic, ILlmClient llm = null, bool projectReport = false, FakeWorkspace workspace = null, Func<StyleEngine> newStyle = null)
        {
            Ws = workspace ?? Corpus(project);
            Llm = llm ?? ScriptedLlmClient.NoFindings();
            Doc = Ws.Open(topic);
            Original = Doc.Xml;
            _projectReport = projectReport;
            _newStyle = newStyle ?? (() => new StyleEngine(new[] { Repo.StyleGuide }, Repo.StyleGuide));
            Host = new ScenarioHost(Ws);
            Build();
        }

        private void Build()
        {
            Style = _newStyle();
            Runner = new ActionRunner(Llm, PromptBuilder.For(Style, Doc.SourcePath), 16000, Style, Doc.SourcePath) { VariableValue = Doc.VariableValue };
            Session = NewSession(_projectReport);
        }

        /// <summary>Close and reopen Flare: a new style engine, runner and review pane that read everything from disk again.</summary>
        public void Restart(Func<Team> newTeam = null)
        {
            if (newTeam != null) Team = newTeam();
            Build();
        }

        public static FakeWorkspace Corpus(string project)
        {
            return project.StartsWith("v2/", StringComparison.Ordinal)
                ? FakeWorkspace.FromFolder(Path.Combine(Repo.Corpus, "v2", project.Substring(3)))
                : FakeWorkspace.FromCorpus(project);
        }

        /// <summary>A review pane over the open topic (a fresh one: what reopening the pane does).</summary>
        public ReviewSession NewSession(bool projectReport = false)
        {
            var s = new ReviewSession(Runner, Doc, Host, Dismissals.Load(Paths.Dismissed), projectReport) { CurrentTeam = () => Team };
            return s;
        }

        /// <summary>Check style: lint, structure and the (scripted) AI review, shown in the pane.</summary>
        public List<StyleFinding> Check(ReviewSession session = null)
        {
            var s = session ?? Session;
            s.Clear();
            var found = Runner.CheckStyle(Doc);
            s.Add(found);
            return found;
        }

        public const string Wrapper = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head><title>Topic</title></head><body>{0}</body></html>";

        /// <summary>A scratch project with one topic (Content/topic.htm) holding <paramref name="bodyXml"/>.</summary>
        public static ReviewFixture Scratch(string bodyXml, ILlmClient llm = null, IDictionary<string, string> variables = null, string topic = "Content/topic.htm")
        {
            var ws = FakeWorkspace.Empty();
            ws.WriteFile(topic, string.Format(Wrapper, bodyXml));
            if (variables != null) foreach (var kv in variables) ws.Variables[kv.Key] = kv.Value;
            return new ReviewFixture(null, topic, llm, false, ws);
        }

        /// <summary>
        /// What the review pane does on Check style (it keeps this logic in the view): lint, structure and snippet findings at
        /// once, then the AI review unit by unit, each answered from <paramref name="cache"/> when the same text was reviewed
        /// before. <paramref name="calls"/> counts the units that went to the model.
        /// </summary>
        public List<StyleFinding> PaneCheck(ReviewCache cache, out int calls, ReviewSession session = null, bool checkAgain = false)
        {
            var s = session ?? Session;
            s.Clear();
            var blocks = Runner.PickCheckBlocks(Doc);
            var lint = Runner.Lint(blocks);
            lint.AddRange(Runner.StructureFindings(blocks));
            lint.AddRange(Runner.LintSnippets(blocks, Doc.SourcePath));
            var all = new List<StyleFinding>(lint);
            var fresh = 0;
            foreach (var unit in Runner.PrepareReview(blocks, lint))
            {
                var raw = cache.Get(unit, checkAgain);   // as the pane: Check again reviews unchanged text afresh
                if (raw == null) { raw = Runner.CallReview(unit); cache.Put(unit, raw); fresh++; }
                all.AddRange(Runner.AnchorReview(raw, unit, lint));
            }
            calls = fresh;
            s.Add(all);
            return all;
        }

        /// <summary>Check style without the AI phase (what the pane shows first, and all it shows without a key): rules, structure, snippets.</summary>
        public List<StyleFinding> LintOnlyCheck(ReviewSession session = null)
        {
            var s = session ?? Session;
            s.Clear();
            var blocks = Runner.PickCheckBlocks(Doc);
            var lint = Runner.Lint(blocks);
            lint.AddRange(Runner.StructureFindings(blocks));
            lint.AddRange(Runner.LintSnippets(blocks, Doc.SourcePath));
            s.Add(lint);
            return lint;
        }

        /// <summary>The first open finding for a rule id and excerpt in the topic itself.</summary>
        public StyleFinding Find(string ruleId, string excerptStart = null, ReviewSession session = null)
        {
            return (session ?? Session).Findings.First(f => f.RuleId == ruleId && f.DocumentPath == null && (excerptStart == null || f.Excerpt.StartsWith(excerptStart)));
        }

        public string Body { get { return Doc.BodyText; } }

        /// <summary>The body text as the writer reads it with every pending tracked change accepted (deleted words gone).</summary>
        public string AcceptedText
        {
            get
            {
                var body = FlareAI.Core.Editing.BlockSelector.FindBody(Doc.Document);
                return string.Concat(FlareAI.Core.Editing.AcceptedView.Of(new List<System.Xml.XmlNode> { body }).Select(n => n.InnerText).ToArray());
            }
        }

        public void Dispose() { Ws.Dispose(); }
    }
}
