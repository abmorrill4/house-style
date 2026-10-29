using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
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
