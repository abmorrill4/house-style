using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>A team's role names (#346): a Role names table in the team guide, read by the rules marked uses-roles.</summary>
    public class RoleNamesTests
    {
        private static string Team(string tableRows)
        {
            var dir = Path.Combine(Path.GetTempPath(), "flareai-roles-" + Guid.NewGuid().ToString("N"));
            var team = Path.Combine(dir, "TeamGuide");
            var style = Path.Combine(team, @"Content\Resources\StyleGuide");
            Directory.CreateDirectory(style);
            File.WriteAllText(Path.Combine(team, "Guide.flprj"), "<CatapultProject />");
            File.WriteAllText(Path.Combine(style, "Roles.htm"),
                "<html><head><title>Roles</title></head><body><h1>Roles</h1><table><tr><th>Role names</th></tr>" + tableRows + "</table></body></html>");
            return team;
        }

        private static StyleEngine Engine(string team)
        {
            return new StyleEngine(team == null ? new[] { GuideSourceTests.Bundled } : new[] { GuideSourceTests.Bundled, team }, GuideSourceTests.Bundled);
        }

        private static LintHit AudienceHit(StyleEngine e)
        {
            return e.Lint(new[] { "Ask the user to sign in." }, new int[0], null).Single(h => h.RuleId == "company:plain-language/9");
        }

        [Fact]
        public void NoTable_NothingChanges()
        {
            var e = Engine(null);
            Assert.Empty(e.RoleNames(null));
            Assert.DoesNotContain("Your team's role names", AudienceHit(e).Message);
            Assert.DoesNotContain("Your team's role names", e.Guide(null));
        }

        [Fact]
        public void Table_ReadsNames_ValidatesThem_AndDropsRepeats()
        {
            var team = Team("<tr><td>Editors, reviewers</td></tr><tr><td>editors</td></tr><tr><td>Admin&lt;script&gt;</td></tr><tr><td>..\\..\\x</td></tr><tr><td>Billing owners</td></tr>");
            var comp = Engine(team).Compose(null);
            Assert.Equal(new[] { "Editors", "reviewers", "Billing owners" }, comp.RoleNames.ToArray());
            Assert.Equal(2, comp.Layers.SelectMany(l => l.Problems).Count(p => p.Message.Contains("isn't a role name")));
        }

        [Fact]
        public void Table_KeepsAtMostTwentyNames()
        {
            var rows = string.Join("", Enumerable.Range(0, 30).Select(i => "<tr><td>Role" + (char)('a' + i % 26) + (char)('a' + i / 26) + "</td></tr>").ToArray());
            var comp = Engine(Team(rows)).Compose(null);
            Assert.Equal(GuideLayer.MaxRoleNames, comp.RoleNames.Count);
        }

        [Fact]
        public void FlagOnlyRule_ShowsTheTeamsNamesOnItsFinding_AndStillOffersNoReplacement()
        {
            var hit = AudienceHit(Engine(Team("<tr><td>Editors, reviewers</td></tr>")));
            Assert.Contains("Your team's role names: Editors, reviewers.", hit.Message);
            Assert.Null(hit.Replacement);
        }

        [Fact]
        public void TheAiRule_CarriesTheNames_AndOnlyTheMarkedRulesDo()
        {
            var guide = Engine(Team("<tr><td>Editors</td></tr>")).Guide(null);
            var line = guide.Split('\n').Single(l => l.StartsWith("- [company:terminology/1]"));
            Assert.Contains("Your team's role names: Editors.", line);
            Assert.Equal(2, guide.Split(new[] { "Your team's role names" }, StringSplitOptions.None).Length - 1);
        }

        [Theory]
        [InlineData("managers", true)]
        [InlineData("sysadmins", false)]
        public void Review_ATeamRoleName_IsSuggested_AndAnotherWordIsStillHeld(string word, bool kept)
        {
            var e = Engine(Team("<tr><td>Managers</td></tr>"));
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = FlareAI.Core.Editing.XhtmlFragment.ParseInto("<p>The end users must approve the request before the billing cycle closes.</p>", doc.Document)[0];
            var body = doc.Document.SelectSingleNode("//body");
            while (body.FirstChild != null) body.RemoveChild(body.FirstChild);
            body.AppendChild(p);
            var llm = new FakeLlmClient(r => @"[{""rule_id"":""company:terminology/1"",""rule"":""audience"",""severity"":""warning"",""excerpt"":""end users"",""replacement"":""" + word + @""",""why"":""Name the role.""}]");
            var runner = new ActionRunner(llm, PromptBuilder.For(e, null), 1024, e);
            var f = runner.Review(new List<System.Xml.XmlNode> { p }, new List<StyleFinding>()).Single(x => x.RuleId == "company:terminology/1");
            if (kept) Assert.Equal(word, f.Replacement); else Assert.Null(f.Replacement);
        }
    }
}
