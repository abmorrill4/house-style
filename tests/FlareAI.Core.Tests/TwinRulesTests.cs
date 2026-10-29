using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>Two rules that flag the same words (#127): a finding names both, including the glossary checker, which has no id.</summary>
    public class TwinRulesTests : IDisposable
    {
        private const string Html = "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Twin</h1>{0}</body></html>";
        private const string Text = "Send a Zelle Transfer now.";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-twin-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        private readonly string _topic;

        public TwinRulesTests()
        {
            Write("Twin.flprj", "<CatapultProject />");
            Write(@"Project\Glossaries\Main.flglo", "<CatapultGlossary><GlossaryEntry><Terms><Term>Zelle transfer</Term></Terms><Definition>A payment.</Definition></GlossaryEntry></CatapultGlossary>");
            _topic = Write(@"Content\Topic.htm", "<html/>");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        private string Write(string rel, string text)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
            return p;
        }

        private static StyleEngine Engine()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
            var guide = Path.Combine(d.FullName, "styleguide");
            return new StyleEngine(new[] { guide }, guide);
        }

        private LintHit Hit()
        {
            return Engine().Lint(new[] { Text }, null, _topic).Single(h => h.Match == "Zelle Transfer");
        }

        /// <summary>Replaces the project's glossary with these terms.</summary>
        private void Glossary(params string[] terms)
        {
            var entries = string.Concat(terms.Select(t => "<GlossaryEntry><Terms><Term>" + t + "</Term></Terms><Definition>A term.</Definition></GlossaryEntry>").ToArray());
            Write(@"Project\Glossaries\Main.flglo", "<CatapultGlossary>" + entries + "</CatapultGlossary>");
        }

        [Fact]
        public void AlsoFlaggedBy_NamesACheckerWithNoId_ByItsCheckerName()
        {
            Write(@"Content\Resources\StyleGuide\Twin.htm", string.Format(Html,
                "<table><tr><th>Avoid</th><th>Use</th><th>Severity</th><th>Why</th><th>Example</th></tr>" +
                "<tr><td>Zelle Transfer</td><td>Zelle transfer</td><td>warning</td><td>Spelling.</td><td>Send a Zelle Transfer.</td></tr></table>"));

            var hit = Hit();

            var checker = hit.RuleIds.Single(id => !id.Contains(":"));
            Assert.EndsWith("GlossaryTerms", checker);
            Assert.Contains(hit.RuleIds, id => id.StartsWith("project:twin/"));
            Assert.Equal(2, hit.RuleIds.Count);
            Assert.True(hit.RuleId != null ? hit.AlsoRules.Single().Id == checker : hit.AlsoRules.Single().Id.StartsWith("project:twin/"));
        }

        [Fact]
        public void CheckerNameRow_TurnsTheCheckerOff()
        {
            var checker = Hit().Rule;
            Assert.EndsWith("GlossaryTerms", checker);

            Write(@"Content\Resources\StyleGuide\Twin.htm", string.Format(Html,
                "<table><tr><th>Rule</th><th>Setting</th><th>Reason</th></tr><tr><td>" + checker + "</td><td>off</td><td>Our spelling.</td></tr></table>"));

            Assert.DoesNotContain(Engine().Lint(new[] { Text }, null, _topic), h => h.Match == "Zelle Transfer");
        }

        [Fact]
        public void TwinsOf_ListsOverlappingRules_NotAlreadyOff()
        {
            // The glossary checker has no id: its twins are the company rules that flag its spellings (and it has none itself).
            Glossary("single sign-on", "identity provider");
            var cat = RuleCatalog.Build(Engine().Compose(_topic));
            Assert.Contains("Company.GlossaryTerms", RuleChanges.TwinsOf(cat, "company:terminology/6").Select(r => r.Key));
            Assert.Contains("Company.GlossaryTerms", RuleChanges.TwinsOf(cat, "company:terminology/7").Select(r => r.Key));

            Write(@"Content\Resources\StyleGuide\Twin.htm", string.Format(Html,
                "<table><tr><th>Rule</th><th>Setting</th><th>Reason</th></tr><tr><td>Company.GlossaryTerms</td><td>off</td><td>Our spelling.</td></tr></table>"));
            var off = RuleCatalog.Build(Engine().Compose(_topic));
            Assert.Equal(RuleState.Off, off.Find("Company.GlossaryTerms").State);
            Assert.DoesNotContain("Company.GlossaryTerms", RuleChanges.TwinsOf(off, "company:terminology/6").Select(r => r.Key));
            Assert.DoesNotContain("Company.GlossaryTerms", RuleChanges.TwinsOf(off, "company:terminology/7").Select(r => r.Key));
        }

        [Fact]
        public void TwinsOf_PartlyOverlappingMatches_AreTwins()
        {
            // "Transfer now" starts inside "Zelle Transfer" in the example: they share words without one containing the other.
            Write(@"Content\Resources\StyleGuide\Twin.htm", string.Format(Html,
                "<table><tr><th>Avoid</th><th>Use</th><th>Severity</th><th>Why</th><th>Example</th></tr>" +
                "<tr><td>Zelle Transfer</td><td>Zelle transfer</td><td>warning</td><td>Spelling.</td><td>Send a Zelle Transfer now.</td></tr>" +
                "<tr><td>Transfer now</td><td>Transfer</td><td>warning</td><td>Wording.</td><td>Send a Zelle Transfer now.</td></tr></table>"));
            var cat = RuleCatalog.Build(Engine().Compose(_topic));
            var zelle = cat.Rules.Single(r => r.Avoid.Contains("Zelle Transfer"));
            var transfer = cat.Rules.Single(r => r.Avoid.Contains("Transfer now"));

            Assert.Contains(transfer.Key, RuleChanges.TwinsOf(cat, zelle.Key).Select(r => r.Key));
            Assert.Contains(zelle.Key, RuleChanges.TwinsOf(cat, transfer.Key).Select(r => r.Key));
        }

        [Fact]
        public void TwinsOf_ForAChecker_IsEmpty()
        {
            Glossary("single sign-on");
            var cat = RuleCatalog.Build(Engine().Compose(_topic));
            Assert.NotNull(cat.Find("Company.GlossaryTerms"));
            Assert.Empty(RuleChanges.TwinsOf(cat, "Company.GlossaryTerms"));
            Assert.Empty(RuleChanges.TwinsOf(cat, "no:such/1"));
        }

        [Fact]
        public void TeamScopeCheckerChange_RequiresAnOwner()
        {
            Glossary("single sign-on");
            var cat = RuleCatalog.Build(Engine().Compose(_topic));
            var checker = cat.Find("Company.GlossaryTerms");
            Assert.NotNull(checker);
            var guide = Path.Combine(_root, "Guide");
            Write(@"Guide\FlareAI\owners.txt", "lead@example.com\n");
            var writer = Team.Load(guide, null, "pat@example.com");
            Assert.False(writer.IsOwner);

            // A writer who isn't listed can't change the guide's rules, the checker's included: nothing is written.
            Assert.Throws<UnauthorizedAccessException>(() => GuideOverrides.Set(writer, checker.Key, "off", "Our spelling."));
            Assert.Single(Directory.GetFiles(guide, "*", SearchOption.AllDirectories));

            // An owner's change to the checker is written into the guide, by its key.
            var owner = Team.Load(guide, null, "lead@example.com");
            Assert.True(owner.IsOwner);
            var result = RuleChanges.Change(owner, checker, new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Our spelling." }, null, _topic);
            Assert.Equal(new[] { "Company.GlossaryTerms" }, result.Changed);
        }

        [Fact]
        public void ProjectScopeCheckerChange_WritesARowNamedForTheChecker_OnlyWhenTicked()
        {
            Glossary("single sign-on");
            var cat = RuleCatalog.Build(Engine().Compose(_topic));
            var rule = cat.Find("company:terminology/6");
            var twins = RuleChanges.TwinsOf(cat, "company:terminology/6");
            Assert.NotNull(rule);

            // Not ticked: the checker isn't changed, even though it's a twin.
            var none = RuleChanges.Change(null, rule, new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Our spelling." }, twins, _topic);
            Assert.DoesNotContain("Company.GlossaryTerms", none.Changed);

            // Ticked: the checker gets a project row named for the checker, whether or not the other twins are ticked.
            var choice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Our spelling.", AlsoCheckers = new List<string> { "Company.GlossaryTerms" } };
            var ticked = RuleChanges.Change(null, rule, choice, twins, _topic);
            Assert.Contains("Company.GlossaryTerms", ticked.Changed);
            Assert.Contains("Company.GlossaryTerms", File.ReadAllText(ticked.File));
        }

        // ---- Turn off this rule: the pane's twin list ------------------------------------------------

        private const string Checker = "Company.GlossaryTerms";

        private sealed class AnswerHost : IReviewHost
        {
            public RuleSettingChoice Choice;
            public IList<RuleRef> Offered;
            public bool Confirm(string message, string action) { return true; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { Offered = otherRules; return Choice; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline) { return null; }
            public IDocumentAdapter OpenDocument(string path) { return null; }
            public bool PreviewBatch(BatchPlan plan) { return true; }
            public bool SaveAndClose(IDocumentAdapter doc) { return false; }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { throw new InvalidOperationException(whatHappened); }
        }

        private sealed class Pane
        {
            public ReviewSession Session;
            public AnswerHost Host;
            public List<string> Writes = new List<string>();
            public StyleFinding Shown;
            public List<StyleFinding> Others = new List<StyleFinding>();
        }

        /// <summary>A session over the real guide, showing company:terminology/6 on "Zelle Transfer" with the glossary checker (and any extra rules) merged in.</summary>
        private Pane OpenPane(RuleSettingChoice choice, params RuleRef[] also)
        {
            Glossary("single sign-on", "identity provider");
            Write(@"Content\Topic.htm", string.Format(Html, "<p>Send a Zelle Transfer now.</p><p>Send a Zelle Transfer again.</p>"));
            var doc = new FakeDocumentAdapter(_topic, true);
            var pane = new Pane { Host = new AnswerHost { Choice = choice } };
            var runner = new ActionRunner(new FakeLlmClient(r => "[]"), new PromptBuilder(null), 1024, Engine(), _topic);
            pane.Session = new ReviewSession(runner, doc, pane.Host, Dismissals.Load(Path.Combine(_root, "dismissed.json")), false)
            {
                CurrentTeam = () => null,
                RecordFeedback = (f, topic, decision, reason) => null,
                SetGuideRule = (team, id, setting, reason) => { pane.Writes.Add(id + "=" + setting); return Path.Combine(_root, "guide.htm"); },
                SetProjectRule = (path, f, setting, reason, by) => pane.Writes.Add((f.RuleId ?? f.Rule) + "=" + setting),
                IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SnippetUsage = p => 1
            };
            var runs = TextRuns.Of(new List<System.Xml.XmlNode> { BlockSelector.FindBody(doc.Document) }).Where(r => r.Value.Contains("Zelle Transfer")).ToList();
            Func<int, StyleFinding> at = i => FindingLocator.FromLint(new LintHit { Start = runs[i].Value.IndexOf("Zelle Transfer", StringComparison.Ordinal), Length = 14, Match = "Zelle Transfer", Replacement = "Zelle transfer",
                Rule = "Company.Terms", Severity = "warning", Message = "Spelling.", Layer = "company", RuleId = "company:terminology/6" }, runs[i]);
            pane.Shown = at(0);
            pane.Shown.AlsoRules.Add(new RuleRef { Id = Checker, Name = Checker, Layer = "company", IsChecker = true });
            foreach (var r in also) pane.Shown.AlsoRules.Add(r);
            pane.Session.Add(new[] { pane.Shown });
            pane.Session.Current = pane.Shown;
            return pane;
        }

        private static RuleSettingChoice Off(params string[] checkers)
        {
            return new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Our spelling.", AlsoCheckers = checkers.ToList() };
        }

        [Fact]
        public void TurnOffRule_AddsOnlyTheTickedCheckers()
        {
            var other = new RuleRef { Id = "Vale.Spelling", Name = "Vale.Spelling", Layer = "company", IsChecker = true };

            var ticked = OpenPane(Off(Checker));
            ticked.Session.TurnOffRule();
            Assert.Equal(new[] { "company:terminology/6=off", Checker + "=off" }, ticked.Writes);

            var notTicked = OpenPane(Off());
            notTicked.Session.TurnOffRule();
            Assert.Equal(new[] { "company:terminology/6=off" }, notTicked.Writes);

            var one = OpenPane(Off(Checker), other);
            one.Session.TurnOffRule();
            Assert.Equal(new[] { "company:terminology/6=off", Checker + "=off" }, one.Writes);
            Assert.DoesNotContain("Vale.Spelling=off", one.Writes);
        }

        [Fact]
        public void TurnOffRule_OffersTwinsThenLeftoverAlsoRules_WithoutDuplicatesOrItself()
        {
            var pane = OpenPane(null,
                new RuleRef { Id = "company:terminology/6", Name = "itself", Layer = "company" },
                new RuleRef { Id = "Vale.Spelling", Name = "Vale.Spelling", Layer = "company", IsChecker = true });
            pane.Session.TurnOffRule();

            var ids = pane.Host.Offered.Select(r => r.Id).ToList();
            Assert.DoesNotContain("company:terminology/6", ids);
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Equal(1, ids.Count(id => id == Checker));
            Assert.True(ids.IndexOf(Checker) < ids.IndexOf("Vale.Spelling"));
            Assert.Equal("Vale.Spelling", ids.Last());
        }

        [Fact]
        public void TurnOffRule_TickedChecker_RemovesItsIssuesFromThePane()
        {
            var pane = OpenPane(Off(Checker));
            var elsewhere = new StyleFinding { Rule = Checker, RuleId = null, Layer = "company", Source = "lint", Excerpt = "Zelle Transfer" };
            pane.Session.Add(new[] { elsewhere });

            pane.Session.TurnOffRule();

            Assert.DoesNotContain(pane.Session.Findings, x => x.Rule == Checker);
            Assert.DoesNotContain(pane.Shown, pane.Session.Findings);
        }

        [Fact]
        public void TurnOffRule_UntickedChecker_KeepsItsIssueOnTheSameWords()
        {
            var pane = OpenPane(Off());

            pane.Session.TurnOffRule();

            var kept = pane.Session.Findings.Single();
            Assert.Equal(Checker, kept.Rule);
            Assert.Null(kept.RuleId);
            Assert.Equal(pane.Shown.Excerpt, kept.Excerpt);
            Assert.DoesNotContain(pane.Shown, pane.Session.Findings);
        }

        [Fact]
        public void IsChecker_IsTrueOnlyForTheGeneratedGlossaryChecker()
        {
            Glossary("single sign-on", "identity provider");
            var runner = new ActionRunner(new FakeLlmClient(r => "[]"), new PromptBuilder(null), 1024, Engine(), _topic);

            var twins = runner.TwinsOf("company:terminology/6");

            var checkers = twins.Where(t => t.IsChecker).Select(t => t.Id).ToList();
            Assert.Contains(Checker, checkers);
            Assert.All(checkers, id => Assert.EndsWith(".GlossaryTerms", id));   // not the variables checker or any other rule with no id
            Assert.All(twins.Where(t => !t.IsChecker), t => Assert.Contains(":", t.Id));
            Assert.Empty(runner.TwinsOf(Checker));
            Assert.Empty(runner.TwinsOf(null));
            Assert.Empty(new ActionRunner(new FakeLlmClient(r => "[]"), new PromptBuilder(null), 1024, null, _topic).TwinsOf("company:terminology/6"));
        }

        [Fact]
        public void Build_WithA500TermGlossary_FinishesUnderTwoSeconds()
        {
            Glossary(Enumerable.Range(1, 500).Select(i => "term " + i + " word").ToArray());
            var comp = Engine().Compose(_topic);

            var watch = Stopwatch.StartNew();
            var cat = RuleCatalog.Build(comp);
            watch.Stop();

            Assert.NotNull(cat.Find("Company.GlossaryTerms"));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "Build took " + watch.Elapsed.TotalSeconds.ToString("0.00") + " s");
        }
    }
}
