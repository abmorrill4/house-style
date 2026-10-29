using System;
using System.IO;
using System.Linq;
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
    }
}
