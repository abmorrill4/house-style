using System;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// Turning rules off without a team guide (the writer's own guide in %APPDATA%, never the plugin folder), the engine
    /// picking the change up before the next check, and the turned-off rules list with who/when/why and Turn back on.
    /// </summary>
    [Collection("Core statics")]
    public class RuleOverridesTests : IDisposable
    {
        private const string Curly = "The section’s title is “Care and feeding of the emu.”";
        private const string QuotesRule = "google:quotation-marks/2";   // Google: Straight and curly quotation marks (table row)
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "flareai-overrides-" + Guid.NewGuid().ToString("N"));
        private readonly string _pluginDir, _userGuide;

        public RuleOverridesTests()
        {
            _pluginDir = Path.Combine(_temp, @"Program Files\Flare\Plugins\FlareAI");
            _userGuide = Path.Combine(_temp, @"AppData\FlareAI\StyleGuide");
            // The bundled guide as installed: read-only, like Program Files for a standard user.
            var bundled = PluginSettings.BundledGuide(_pluginDir);
            foreach (var f in Directory.GetFiles(RepoGuide, "*", SearchOption.AllDirectories))
            {
                var to = bundled + f.Substring(RepoGuide.Length);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(f, to);
                File.SetAttributes(to, FileAttributes.ReadOnly);
            }
        }

        public void Dispose()
        {
            if (!Directory.Exists(_temp)) return;
            foreach (var f in Directory.GetFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_temp, true);
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

        /// <summary>The plugin's wiring with no team guide configured: an engine over the settings' roots, and the team context.</summary>
        private StyleEngine Engine(PluginSettings s) { return new StyleEngine(() => s.GuideRoots(_pluginDir, _userGuide), PluginSettings.BundledGuide(_pluginDir)); }

        private Team TeamFor(PluginSettings s, string writer = "pat@example.com")
        {
            var t = Team.Load(s.GuideRoot(_pluginDir, _userGuide), null, writer);
            t.SeedFrom = s.ReadOnlyGuide(_pluginDir);
            return t;
        }

        private static bool Flags(StyleEngine e, string rule) { return e.Lint(new[] { Curly }, null, null).Any(h => h.RuleId == rule); }

        [Fact]
        public void TurningOffAGoogleRule_WithNoTeamGuide_GoesToMyGuide_AndTheNextCheckNoLongerFlagsIt()
        {
            var s = new PluginSettings();
            var engine = Engine(s);
            var team = TeamFor(s);
            Assert.True(Flags(engine, QuotesRule));
            Assert.True(team.IsPersonal);
            Assert.Equal(_userGuide, team.GuideRoot);

            var file = GuideOverrides.Set(team, QuotesRule, "off", "Our UI strings use curly quotes.");

            Assert.StartsWith(_userGuide, file);
            Assert.False(PluginSettings.IsUnder(file, _pluginDir));
            Assert.False(Directory.Exists(Path.Combine(_userGuide, @"Content\Resources\StyleGuide\Google")));   // never copied
            Assert.False(File.Exists(Path.Combine(_userGuide, @"Content\Resources\StyleGuide\Company\Terminology.htm")));   // nor the Company layer
            Assert.Equal(Path.Combine(_userGuide, @"Content\Resources\StyleGuide\" + GuideSeed.PersonalTopic), file);
            Assert.Equal("company", File.ReadAllLines(Path.Combine(_userGuide, @"FlareAI\layer.txt")).Last(l => !l.StartsWith("#")).Trim());
            Assert.Equal(new[] { PluginSettings.BundledGuide(_pluginDir), _userGuide }, s.GuideRoots(_pluginDir, _userGuide).ToArray());   // my changes after the bundled guide
            // Same engine (the findings panel keeps its runner): the next check reads the change.
            Assert.False(Flags(engine, QuotesRule));
            Assert.True(engine.IsOff(null, QuotesRule));
            Assert.Contains(engine.Rules(null), r => r.Id.StartsWith("company:terminology/"));       // company rules still apply, once
            Assert.Equal(1, engine.Lint(new[] { "Tenants can open the admin panel." }, null, null).Count(h => h.Match == "Tenants"));
            // A fresh plugin session reads it too.
            Assert.False(Flags(Engine(new PluginSettings()), QuotesRule));
        }

        [Fact]
        public void TheTurnedOffList_ShowsWhoWhenAndWhy_AndTurnBackOnRestoresTheRule()
        {
            var s = new PluginSettings();
            var engine = Engine(s);
            var team = TeamFor(s);
            GuideOverrides.Set(team, QuotesRule, "off", "Our UI strings use curly quotes.");
            Assert.False(Flags(engine, QuotesRule));

            var listed = RuleOverrides.Read(_userGuide, "My style guide").Single(o => o.RuleId == QuotesRule);
            Assert.Equal("off", listed.Setting);
            Assert.Equal("pat@example.com", listed.By);
            Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), listed.Date);
            Assert.Equal("Our UI strings use curly quotes.", listed.Note);
            Assert.Equal("My style guide", listed.Scope);

            RuleOverrides.TurnBackOn(listed, team);
            Assert.DoesNotContain(RuleOverrides.Read(_userGuide, "My style guide"), o => o.RuleId == QuotesRule);
            Assert.True(Flags(engine, QuotesRule));
        }

        [Fact]
        public void OnlyOwnersChangeASharedGuideWithAnOwnersFile()
        {
            var guide = Path.Combine(_temp, "TeamGuide");
            Directory.CreateDirectory(Path.Combine(guide, "FlareAI"));
            File.WriteAllText(Path.Combine(guide, "Team.flprj"), "<CatapultProject />");
            File.WriteAllText(Path.Combine(guide, @"FlareAI\owners.txt"), "owner@example.com\n");
            var s = new PluginSettings { StyleLayers = guide };
            Assert.True(s.HasTeamGuide(_pluginDir));
            var writer = TeamFor(s, "pat@example.com");
            var owner = TeamFor(s, "owner@example.com");
            Assert.False(writer.IsPersonal);

            Assert.Throws<UnauthorizedAccessException>(() => GuideOverrides.Set(writer, QuotesRule, "off", "please"));
            Assert.Empty(RuleOverrides.Read(guide, "Team"));
            GuideOverrides.Set(owner, QuotesRule, "off", "Decided at review.");
            var listed = RuleOverrides.Read(guide, "Team").Single();
            Assert.Equal("owner@example.com", listed.By);
            Assert.Throws<UnauthorizedAccessException>(() => RuleOverrides.TurnBackOn(listed, writer));
            Assert.Single(RuleOverrides.Read(guide, "Team"));
            Assert.False(Directory.Exists(_userGuide));   // a team guide is the target: no personal copy
        }

        [Fact]
        public void OlderOverrideTables_GainWhoAndWhenColumns_AndKeepTheirRows()
        {
            var guide = Path.Combine(_temp, "Older");
            Directory.CreateDirectory(Path.Combine(guide, @"Content\Resources\StyleGuide\Company"));
            File.WriteAllText(Path.Combine(guide, "Older.flprj"), "<CatapultProject />");
            File.WriteAllText(Path.Combine(guide, @"Content\Resources\StyleGuide\Company\Changes.htm"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html><body><h1>Changes</h1>\n<table>\n<tr><th>Rule</th><th>Setting</th><th>Reason</th></tr>\n<tr><td>google:will/1</td><td>off</td><td>Release notes.</td></tr>\n</table>\n</body></html>");
            GuideOverrides.Set(guide, QuotesRule, "warning", "Softer.", "sam", new DateTime(2026, 10, 1));
            var all = RuleOverrides.Read(guide, "Team");
            Assert.Equal(2, all.Count);
            Assert.Equal("", all.Single(o => o.RuleId == "google:will/1").By);
            var added = all.Single(o => o.RuleId == QuotesRule);
            Assert.Equal("warning", added.Setting);
            Assert.Equal("sam", added.By);
            Assert.Equal("2026-10-01", added.Date);
            var e = new StyleEngine(new[] { guide }, RepoGuide);
            Assert.Equal("warning", e.Lint(new[] { Curly }, null, null).First(h => h.RuleId == QuotesRule).Severity);
            Assert.DoesNotContain(e.Validate(null).Issues, i => i.File == "Changes.htm");
        }

        [Fact]
        public void ProjectOnlyChanges_RecordWhoAndWhy_InOverridesIni_AndTurnBackOnRemovesThem()
        {
            var project = Path.Combine(_temp, "Docs");
            Directory.CreateDirectory(Path.Combine(project, "Content"));
            File.WriteAllText(Path.Combine(project, "Docs.flprj"), "<CatapultProject />");
            var topic = Path.Combine(project, @"Content\a.htm");
            ProjectRules.Ignore(topic, new StyleFinding { RuleId = QuotesRule, Rule = "Google.QuotationMarks2", Layer = "google" }, "off", "Legacy\nstrings.", "pat@example.com");
            ProjectRules.Ignore(topic, new StyleFinding { Rule = "Too formal", Layer = "ai" }, "off");
            var e = new StyleEngine(new[] { RepoGuide }, RepoGuide);
            Assert.DoesNotContain(e.Lint(new[] { Curly }, null, topic), h => h.RuleId == QuotesRule);

            var listed = RuleOverrides.Read(project, "Project Docs");
            var quotes = listed.Single(o => o.RuleId == QuotesRule);
            Assert.Equal("pat@example.com", quotes.By);
            Assert.Equal("Legacy strings.", quotes.Note);
            Assert.True(listed.Single(o => o.RuleId == "Too formal").IsAiName);

            RuleOverrides.TurnBackOn(quotes, null);
            Assert.DoesNotContain("pat@example.com", File.ReadAllText(ProjectRules.OverridesPath(topic)));
            Assert.Contains(e.Lint(new[] { Curly }, null, topic), h => h.RuleId == QuotesRule);
            Assert.Single(RuleOverrides.Read(project, "Project Docs"));
        }

        [Fact]
        public void ASpanFlaggedByTwoRules_ListsTheOtherOnTheKeptHit_AndTurningOffBothClearsIt()
        {
            // Two Google rules on different pages still flag these words (procedures/42 and ui-elements/69: different scopes, so
            // the compiler's canonical pass keeps both). "e.g." no longer works here: the Word-List owns it alone (#56); nor does
            // "Control+s": procedures/42 leaves Google's own Control+ form alone since #60.
            const string text = "To save, press Ctrl+s now.";
            const string span = "Ctrl+s";
            var s = new PluginSettings();
            var engine = Engine(s);
            var team = TeamFor(s);
            var hit = engine.Lint(new[] { text }, null, null).Single(h => h.Match == span);
            Assert.NotEmpty(hit.AlsoRules);
            Assert.DoesNotContain(hit.AlsoRules, r => r.Id == hit.RuleId);
            Assert.All(hit.AlsoRules, r => Assert.False(string.IsNullOrEmpty(r.Name)));

            // Turning off only the shown rule exposes the next one on the same words (the bug).
            GuideOverrides.Set(team, hit.RuleId, "off", "Fine in our docs.");
            Assert.Contains(engine.Lint(new[] { text }, null, null), h => h.Match == span);

            foreach (var r in hit.AlsoRules) GuideOverrides.Set(team, r.Id, "off", "Fine in our docs.");
            Assert.DoesNotContain(engine.Lint(new[] { text }, null, null), h => h.Match == span);
            Assert.Equal(1 + hit.AlsoRules.Count, RuleOverrides.Read(_userGuide, "My style guide").Count(o => o.By == "pat@example.com" && o.Note == "Fine in our docs."));
        }

        [Fact]
        public void TheKeptHit_ForQuotationMarks_ListsEveryOtherRuleOnTheSameQuotes()
        {
            var s = new PluginSettings();
            var engine = Engine(s);
            var team = TeamFor(s);
            var hits = engine.Lint(new[] { Curly }, null, null);
            var shown = hits.Where(h => h.Match.Contains("“") || h.Match.Contains("’")).ToList();
            Assert.NotEmpty(shown);
            foreach (var h in shown)
            {
                GuideOverrides.Set(team, h.RuleId, "off", "x");
                foreach (var r in h.AlsoRules) GuideOverrides.Set(team, r.Id, "off", "x");
            }
            Assert.DoesNotContain(engine.Lint(new[] { Curly }, null, null), h => h.Match.Contains("“") || h.Match.Contains("’"));
        }

        [Fact]
        public void AFailedWrite_NamesTheFile()
        {
            var s = new PluginSettings();
            var team = TeamFor(s);
            var topic = GuideOverrides.Set(team, "google:word-list/5", "off", "first change");   // creates my rule-changes topic
            File.SetAttributes(topic, FileAttributes.ReadOnly);
            var ex = Assert.Throws<GuideWriteException>(() => GuideOverrides.Set(team, QuotesRule, "off", "x"));
            Assert.Equal(topic, ex.Target);
            Assert.Contains(topic, ex.Message);
            Assert.True(Flags(Engine(s), QuotesRule));   // nothing changed
        }

        [Fact]
        public void ACachedAiFinding_ForARuleTurnedOffSince_IsNotShown()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var llm = new FakeLlmClient(r => @"[{""rule_id"":""company:terminology/1"",""rule"":""audience"",""severity"":""warning"",""excerpt"":""the user should simply ensure"",""replacement"":null,""why"":""Say members.""}]");
            var style = new FakeStyleService();
            style.Off.Add("company:terminology/1");
            var runner = new ActionRunner(llm, new PromptBuilder(() => style.Guide(null)), 1024, style);
            Assert.DoesNotContain(runner.CheckStyle(doc), f => f.Source == "ai" && f.Rule == "audience");
        }
    }
}
