using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Llm;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// Twin rules (#127): rules that flag the same words, or some of them, are twins; disabling one must show the others. The
    /// glossary checker (no guide id, named by its checker name) is a twin too, and turning it off by that name turns it off.
    /// </summary>
    public class TwinRulesTests : IDisposable
    {
        private const string Six = "company:terminology/6", Seven = "company:terminology/7", Glossary = "Company.GlossaryTerms";
        private const string PartlyOverlapping = "team:team-words/k1";
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "flareai-twins-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        private readonly string _pluginDir, _bundled, _team;

        public TwinRulesTests()
        {
            _pluginDir = Path.Combine(_temp, @"Plugins\FlareAI");
            _bundled = PluginSettings.BundledGuide(_pluginDir);
            foreach (var f in Directory.GetFiles(RepoGuide, "*", SearchOption.AllDirectories))
            {
                var to = _bundled + f.Substring(RepoGuide.Length);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(f, to);
            }

            // A team row whose Avoid ("sign-on first") shares words with company rule 6's match ("Single Sign-On") without containing it.
            _team = Path.Combine(_temp, "TeamGuide");
            Write(Path.Combine(_team, "TeamGuide.flprj"), "<CatapultProject />");
            Write(Path.Combine(_team, @"FlareAI\layer.txt"), "team\n");
            Write(Path.Combine(_team, @"Content\Resources\StyleGuide\Team-words.htm"), Topic("Team words",
                "<table><tr><th>Avoid</th><th>Use</th><th>Severity</th><th>Example</th></tr>" +
                "<tr id=\"rule-k1\"><td>sign-on first</td><td>sign in first</td><td>warning</td><td>Enable Single Sign-On first.</td></tr></table>"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_temp, true); } catch (Exception) { }
        }

        private StyleEngine Engine() { return new StyleEngine(new[] { _bundled, _team }, _bundled); }

        private bool IsDefault(string root) { return PluginSettings.IsUnder(root, _pluginDir); }

        private RuleCatalog Catalog()
        {
            return RuleCatalog.Build(Engine().Compose(null), IsDefault);
        }

        // ---- twins of a rule ---------------------------------------------------------------------------------

        [Fact]
        public void TwinsOf_ListsOverlappingRules_NotAlreadyOff()
        {
            var cat = Catalog();
            Assert.Contains(RuleChanges.TwinsOf(cat, Six), r => r.Key == Glossary);
            Assert.Contains(RuleChanges.TwinsOf(cat, Seven), r => r.Key == Glossary);

            // Turn the glossary checker off by its name: it is no longer a twin of either rule.
            GuideOverrides.Set(_team, Glossary, "off", "Glossary spellings are checked by hand.", "lead@example.com", new DateTime(2026, 10, 1));
            var after = Catalog();
            Assert.DoesNotContain(RuleChanges.TwinsOf(after, Six), r => r.Key == Glossary);
            Assert.DoesNotContain(RuleChanges.TwinsOf(after, Seven), r => r.Key == Glossary);
            Assert.DoesNotContain(RuleChanges.TwinsOf(after, Six), r => r.State == RuleState.Off);
        }

        [Fact]
        public void TwinsOf_PartlyOverlappingMatches_AreTwins()
        {
            var cat = Catalog();
            Assert.Contains(RuleChanges.TwinsOf(cat, PartlyOverlapping), r => r.Id == Six);
            Assert.Contains(RuleChanges.TwinsOf(cat, Six), r => r.Key == PartlyOverlapping);
        }

        [Fact]
        public void TwinsOf_ForAChecker_IsEmpty()
        {
            var cat = Catalog();
            Assert.Empty(RuleChanges.TwinsOf(cat, Glossary));
            Assert.Empty(RuleChanges.TwinsOf(cat, null));
            Assert.Empty(RuleChanges.TwinsOf(null, Six));
        }

        // ---- the checker in lint hits ------------------------------------------------------------------------

        [Fact]
        public void AlsoFlaggedBy_NamesACheckerWithNoId_ByItsCheckerName()
        {
            var hits = Engine().Compose(null).Lint(new[] { "Enable Single Sign-On first." }, null);
            var hit = hits.First(h => h.Match == "Single Sign-On");

            Assert.Equal(Six, hit.RuleId);
            var checker = Assert.Single(hit.AlsoRules, r => r.Id == Glossary);
            Assert.Equal(Glossary, checker.Name);
            Assert.True(checker.IsChecker);
        }

        [Fact]
        public void CheckerNameRow_TurnsTheCheckerOff()
        {
            const string identity = "Ask your Identity Provider for the metadata.";
            var before = Engine().Compose(null).Lint(new[] { identity }, null);
            Assert.Contains(before, h => h.AlsoRules.Any(r => r.Id == Glossary));

            GuideOverrides.Set(_team, Glossary, "off", "Glossary spellings are checked by hand.", "lead@example.com", new DateTime(2026, 10, 1));
            var after = Engine().Compose(null).Lint(new[] { identity }, null);
            Assert.DoesNotContain(after, h => h.AlsoRules.Any(r => r.Id == Glossary));
            Assert.DoesNotContain(after, h => h.RuleId == null);   // the checker's own findings are gone
            Assert.Contains(after, h => h.RuleId == Seven);         // rule 7 still flags the words
        }

        [Fact]
        public void IsChecker_IsTrueOnlyForTheGeneratedGlossaryChecker()
        {
            var comp = Engine().Compose(null);
            var texts = comp.Items.Where(i => !string.IsNullOrEmpty(i.Example)).Select(i => i.Example).ToList();
            var refs = comp.Lint(texts, null).SelectMany(h => h.AlsoRules).ToList();

            Assert.Contains(refs, r => r.IsChecker);
            Assert.All(refs.Where(r => r.IsChecker), r => Assert.Equal(Glossary, r.Id));
            Assert.All(refs.Where(r => r.Id == Glossary), r => Assert.True(r.IsChecker));
            Assert.All(refs.Where(r => !r.IsChecker), r => Assert.NotEqual(Glossary, r.Name));
        }

        // ---- Disable Rule offers the checkers; the ticked ones go -------------------------------------------------

        private const string OtherChecker = "Company.OtherChecker";

        private sealed class Host : IReviewHost
        {
            public RuleSettingChoice RuleChoice;
            public IList<RuleRef> Offered;
            public bool Confirm(string message, string action) { return true; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { Offered = otherRules; return RuleChoice; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline) { return null; }
            public FlareAI.Core.Editing.IDocumentAdapter OpenDocument(string path) { return null; }
            public bool PreviewBatch(BatchPlan plan) { return true; }
            public bool SaveAndClose(FlareAI.Core.Editing.IDocumentAdapter doc) { return false; }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { }
        }

        private sealed class Pane
        {
            public ReviewSession Session;
            public Host Host = new Host();
            public List<string> Writes = new List<string>();
            public StyleFinding Rule, Glossary, Other;
        }

        /// <summary>A pane with one issue of rule 6 on "Single Sign-On", plus its glossary issue and an other-checker issue on the same words.</summary>
        private Pane PaneWith(params string[] mergedCheckers)
        {
            var path = Path.Combine(_temp, "topic.htm");
            Write(path, Topic("Topic", "<p>Enable Single Sign-On first.</p>"));
            var doc = new FakeDocumentAdapter(path, true);
            var style = new FakeStyleService();
            style.GuideRules.Add(new GuideRule { Id = Six, Layer = "company", Section = "Terminology", Text = "Say Single sign-on." });
            var p = new Pane();
            var runner = new ActionRunner(new FakeLlmClient(r => "[]"), new PromptBuilder(null), 1024, style);
            p.Session = new ReviewSession(runner, doc, p.Host, Dismissals.Load(Path.Combine(_temp, "dismissed.json")), false)
            {
                CurrentTeam = () => null,
                RecordFeedback = (f, topic, decision, reason) => null,
                SetGuideRule = (team, id, setting, reason) => { p.Writes.Add(id + "=" + setting); return "guide.htm"; },
                SetProjectRule = (pth, f, setting, reason, by) => p.Writes.Add((f.RuleId ?? f.Rule) + "=" + setting),
                IgnoredAiRules = pr => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SnippetUsage = pr => 0
            };
            var run = TextRuns.Of(new List<System.Xml.XmlNode> { BlockSelector.FindBody(doc.Document) }).First(r => r.Value.Contains("Single Sign-On"));
            Func<string, string, StyleFinding> hit = (rule, id) =>
            {
                var f = FindingLocator.FromLint(new LintHit { Start = run.Value.IndexOf("Single Sign-On"), Length = 14, Match = "Single Sign-On", Rule = rule, Severity = "warning", Message = "Say it right.", Layer = "company", RuleId = id }, run);
                return f;
            };
            p.Rule = hit("Company.Terminology", Six);
            foreach (var c in mergedCheckers) p.Rule.AlsoRules.Add(new RuleRef { Id = c, Name = c, Layer = "company", IsChecker = true });
            p.Glossary = hit(Glossary, null);
            p.Other = hit(OtherChecker, null);
            p.Session.Add(new[] { p.Rule, p.Glossary, p.Other });
            p.Session.Current = p.Rule;
            return p;
        }

        [Fact]
        public void TurnOffRule_AddsOnlyTheTickedCheckers()
        {
            // Ticked.
            var p = PaneWith(Glossary);
            p.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Why.", AlsoCheckers = new List<string> { Glossary } };
            p.Session.TurnOffRule();
            Assert.Contains(p.Host.Offered, r => r.Id == Glossary && r.IsChecker);
            Assert.Equal(new[] { Six + "=off", Glossary + "=off" }, p.Writes);

            // Not ticked.
            p = PaneWith(Glossary);
            p.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Why." };
            p.Session.TurnOffRule();
            Assert.Equal(new[] { Six + "=off" }, p.Writes);

            // Two checkers, one ticked.
            p = PaneWith(Glossary, OtherChecker);
            p.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Why.", AlsoCheckers = new List<string> { OtherChecker } };
            p.Session.TurnOffRule();
            Assert.Equal(new[] { Six + "=off", OtherChecker + "=off" }, p.Writes);
        }

        [Fact]
        public void TurnOffRule_TickedChecker_RemovesItsIssuesFromThePane()
        {
            var p = PaneWith(Glossary);
            p.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Why.", AlsoCheckers = new List<string> { Glossary } };
            p.Session.TurnOffRule();
            Assert.DoesNotContain(p.Session.Findings, f => f.Rule == Glossary);
            Assert.DoesNotContain(p.Session.Findings, f => f.RuleId == Six);
            Assert.Contains(p.Other, p.Session.Findings);
        }

        [Fact]
        public void TurnOffRule_UntickedChecker_KeepsItsIssueOnTheSameWords()
        {
            var p = PaneWith(Glossary);
            p.Session.Findings.ToList().ForEach(f => { });
            p.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Why." };
            p.Session.TurnOffRule();
            Assert.DoesNotContain(p.Session.Findings, f => f.RuleId == Six);
            Assert.Contains(p.Session.Findings, f => f.RuleId == null && f.Rule == Glossary && f.Excerpt == "Single Sign-On");
        }

        [Fact]
        public void TeamScopeCheckerChange_RequiresAnOwner()
        {
            Write(Path.Combine(_team, @"FlareAI\owners.txt"), "owner@example.com\n");
            var notOwner = Team.Load(_team, null, "pat@example.com");
            Assert.ThrowsAny<Exception>(() => GuideOverrides.Set(notOwner, Glossary, "off", "Why."));

            var owner = Team.Load(_team, null, "owner@example.com");
            var cat = Catalog();
            var result = RuleChanges.Change(owner, cat.Find(Six), new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Why.", AlsoCheckers = new List<string> { Glossary } }, new List<CatalogRule>(), null);
            Assert.Equal(new[] { Six, Glossary }, result.Changed);
            Assert.DoesNotContain(RuleChanges.TwinsOf(Catalog(), Seven), r => r.Key == Glossary);
        }

        [Fact]
        public void Build_WithA500TermGlossary_FinishesUnderTwoSeconds()
        {
            var terms = string.Concat(Enumerable.Range(0, 500).Select(i => "<GlossaryEntry><Terms><Term>Zorbterm" + i + " widget</Term></Terms><Definition>Term " + i + ".</Definition></GlossaryEntry>"));
            Write(Path.Combine(_team, @"Project\Glossaries\Main.flglo"), "<CatapultGlossary>" + terms + "</CatapultGlossary>");
            var comp = Engine().Compose(null);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var cat = RuleCatalog.Build(comp, IsDefault);
            RuleChanges.TwinsOf(cat, Six);
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "took " + watch.Elapsed);
        }

        // ---- helpers -------------------------------------------------------------------------------------------

        private static string Topic(string title, string body)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head><title>" + title + "</title></head><body><h1>" + title + "</h1>" + body + "</body></html>";
        }

        private static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private static string RepoGuide
        {
            get
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
                return Path.Combine(d.FullName, "styleguide");
            }
        }
    }
}
