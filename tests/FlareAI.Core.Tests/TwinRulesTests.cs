using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
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
