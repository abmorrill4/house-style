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
    /// The Style rules catalogue (#99): every rule of every source exactly once (Google, the bundled Company guide, a team
    /// guide, the writer's own guide, the open project), its state from the same overrides the checker uses (Default, team,
    /// personal "on" and level rows, project exceptions, open requests), the rules that share words, and search and filters.
    /// </summary>
    public class RuleCatalogTests : IDisposable
    {
        private const string Team1 = "team:team-words/k1", Kick = "team:team-words/k2", PleaseKindly = "team:team-words/k3", Kindly = "team:team-words/k4";
        private const string ThisPage = "google:word-list/221", Quotes = "google:quotation-marks/2", Nbsp = "google:units-of-measure/1";
        private const string Please = "company:plain-language/5", Email = "project:docs-rules/p1";
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "flareai-catalog-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        private readonly string _pluginDir, _bundled, _team, _mine, _project, _topic;

        public RuleCatalogTests()
        {
            _pluginDir = Path.Combine(_temp, @"Plugins\FlareAI");
            _bundled = PluginSettings.BundledGuide(_pluginDir);
            foreach (var f in Directory.GetFiles(RepoGuide, "*", SearchOption.AllDirectories))
            {
                var to = _bundled + f.Substring(RepoGuide.Length);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(f, to);
            }

            _team = Path.Combine(_temp, "TeamGuide");
            Write(Path.Combine(_team, "TeamGuide.flprj"), "<CatapultProject />");
            Write(Path.Combine(_team, @"FlareAI\layer.txt"), "team\n");
            Write(Path.Combine(_team, @"Content\Resources\StyleGuide\Team-words.htm"), Topic("Team words",
                "<h2>Words</h2><ul><li id=\"rule-k1\" MadCap:conditions=\"Content.Steps\">Say select, not click on.<ul><li>Bad: Click on Save.</li><li>Good: Select Save.</li></ul></li></ul>" +
                "<table><tr><th>Avoid</th><th>Use</th><th>Severity</th><th>Example</th></tr>" +
                "<tr id=\"rule-k2\"><td>kick off</td><td>start</td><td>warning</td><td>Kick off the import.</td></tr>" +
                "<tr id=\"rule-k3\"><td>please kindly</td><td>(delete)</td><td>suggestion</td><td>Please kindly wait for the import.</td></tr>" +
                "<tr id=\"rule-k4\"><td>kindly</td><td>(rewrite)</td><td>warning</td><td>Wait kindly.</td></tr></table>"));
            GuideOverrides.Set(_team, Please, "off", "Our members say please.", "lead@example.com", new DateTime(2026, 10, 1));

            _mine = Path.Combine(_temp, @"AppData\FlareAI\StyleGuide");
            GuideSeed.Ensure(_bundled, _mine);
            Write(Path.Combine(_mine, @"Content\Resources\StyleGuide\Mine-extra.htm"), Topic("My extras", "<ul><li>Write short headings.</li></ul>"));
            GuideOverrides.Set(_mine, Quotes, "warning", "A warning is enough.", "pat@example.com", new DateTime(2026, 10, 2));
            GuideOverrides.Set(_mine, Nbsp, "on", "I want it.", "pat@example.com", new DateTime(2026, 10, 3));

            _project = Path.Combine(_temp, "Docs");
            Write(Path.Combine(_project, "Docs.flprj"), "<CatapultProject />");
            _topic = Path.Combine(_project, @"Content\a.htm");
            Write(_topic, Topic("A", "<p>Hello.</p>"));
            Write(Path.Combine(_project, @"Content\Resources\StyleGuide\Docs-rules.htm"), Topic("Docs rules",
                "<table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr id=\"rule-p1\"><td>e-mail</td><td>email</td><td>Send an e-mail.</td></tr></table>"));
            Write(Path.Combine(_project, @"Project\VariableSets\General.flvar"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><CatapultVariableSet><Variable Name=\"ProductName\">Acme Cloud</Variable></CatapultVariableSet>");
            ProjectRules.Ignore(_topic, new StyleFinding { RuleId = Kick, Rule = "Team.TeamWordsK2", Layer = "team" }, "off", RuleOverrides.WithRequest("Legal copy.", "r-approved"), "lead@example.com");
        }

        public void Dispose()
        {
            try { Directory.Delete(_temp, true); } catch (Exception) { }
        }

        private StyleEngine Engine() { return new StyleEngine(new[] { _bundled, _team, _mine }, _bundled); }

        private bool IsDefault(string root) { return PluginSettings.IsUnder(root, _pluginDir); }

        private RuleCatalog Catalog(IEnumerable<GuideRequest> requests = null)
        {
            return RuleCatalog.Build(Engine().Compose(_topic), IsDefault, requests, r => r.Writer == "pat@example.com");
        }

        // ---- every rule once -------------------------------------------------------------------------------

        [Fact]
        public void EveryRule_OfEverySource_IsListedExactlyOnce()
        {
            var comp = Engine().Compose(_topic);
            var cat = RuleCatalog.Build(comp, IsDefault);

            Assert.Equal(cat.Rules.Count, cat.Rules.Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var ids = comp.Items.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var generated = comp.Rules.Where(r => r.Id == null).Select(r => r.Name).Distinct().ToList();
            Assert.Equal(ids.Count + generated.Count, cat.Rules.Count);
            foreach (var id in ids) Assert.NotNull(cat.Find(id));
            foreach (var l in comp.Rules) Assert.True(cat.Find(l.Id ?? l.Name) != null, (l.Id ?? l.Name) + " is missing");
            Assert.Equal(new[] { "Google", "Starter rules", "Team style guide (TeamGuide)", "My style guide", "Project Docs" }, cat.Sources.ToArray());
            foreach (var s in cat.Sources) Assert.Contains(cat.Rules, r => r.Source == s);

            var k1 = cat.Find(Team1);
            Assert.Equal("Team style guide (TeamGuide)", k1.Source);
            Assert.Equal("Team words", k1.Page);
            Assert.Equal("Words", k1.Section);
            Assert.Equal("Click on Save.", k1.Bad);
            Assert.Equal("Select Save.", k1.Good);
            Assert.Equal(new[] { "Steps" }, k1.Content.ToArray());
            Assert.False(k1.IsTable);
            Assert.Equal("AI review", k1.LevelLabel);

            var kick = cat.Find(Kick);
            Assert.True(kick.IsTable && kick.HasFix);
            Assert.Equal(new[] { "kick off" }, kick.Avoid.ToArray());
            Assert.Equal("start", kick.Use);
            Assert.Equal("Kick off the import.", kick.Example);
            Assert.Equal("warning", kick.DefaultLevel);
            Assert.False(cat.Find(Kindly).HasFix);   // (rewrite): flagged, no fix
            Assert.Equal("remove", cat.Find(PleaseKindly).Action);

            Assert.Equal("My style guide", cat.Find("company:mine-extra/1").Source);
            Assert.Equal("Project Docs", cat.Find(Email).Source);
            var variables = cat.Rules.Single(r => r.Id == null && r.Page == "Variables");
            Assert.Equal("Project Docs", variables.Source);
            Assert.Equal("Project.Variables", variables.Key);
            Assert.Equal("Google", cat.Find(Quotes).Source);
            Assert.Equal("Starter rules", cat.Find(Please).Source);
            Assert.True(cat.Rules.All(r => !string.IsNullOrEmpty(r.Page)), "every rule has a page");
        }

        [Fact]
        public void ATopicOfMyGuide_StandsInForTheBundledOne_AndItsRulesAppearOnce()
        {
            var claims = File.ReadAllText(Path.Combine(_bundled, @"Content\Resources\StyleGuide\Company\Claims.htm")).Replace("Do not promise timelines", "Never promise timelines");
            Write(Path.Combine(_mine, @"Content\Resources\StyleGuide\Company\Claims.htm"), claims);

            var cat = Catalog();

            var rule = cat.Rules.Single(r => r.Key == "company:claims/1");
            Assert.Equal("My style guide", rule.Source);
            Assert.StartsWith("Never promise timelines", rule.Text);
        }

        // ---- state -----------------------------------------------------------------------------------------

        [Fact]
        public void State_ComesFromDefaults_TheTeam_MyGuide_AndTheProject_WithWhoWhenAndWhy()
        {
            var cat = Catalog();

            var def = cat.Find(ThisPage);
            Assert.Equal(RuleState.Off, def.State);
            Assert.True(def.IsDefault);
            Assert.Equal("Disabled (Default)", def.StateLabel);
            Assert.Equal(RuleOverrides.DefaultScope, def.Change.Scope);
            Assert.False(def.ChangeIsProject);

            var level = cat.Find(Quotes);   // a default (Tip) that my guide sets to Warning
            Assert.Equal(RuleState.LevelChanged, level.State);
            Assert.Equal("warning", level.Level);
            Assert.Equal("suggestion", level.DefaultLevel);   // written as a suggestion since the severity calibration (#61)
            Assert.Equal("Warning", level.LevelLabel);
            Assert.False(level.IsDefault);
            Assert.Equal("My style guide", level.Change.Scope);
            Assert.Equal("pat@example.com", level.Change.By);
            Assert.Equal("2026-10-02", level.Change.Date);
            Assert.Equal("A warning is enough.", level.Change.Note);

            var on = cat.Find(Nbsp);         // a default (off) that my guide turns back on
            Assert.Equal(RuleState.On, on.State);
            Assert.True(on.Change.IsTurnOn);
            Assert.Equal("Enabled", on.StateLabel);

            var team = cat.Find(Please);
            Assert.Equal(RuleState.Off, team.State);
            Assert.Equal("Team style guide (TeamGuide)", team.Change.Scope);
            Assert.Equal("lead@example.com", team.Change.By);

            var project = cat.Find(Kick);
            Assert.Equal(RuleState.Off, project.State);
            Assert.True(project.ChangeIsProject);
            Assert.Equal("Project Docs", project.Change.Scope);
            Assert.Equal("r-approved", project.Change.RequestId);
            Assert.Equal("Legal copy.", project.Change.Note);

            var untouched = cat.Find(Email);
            Assert.Equal(RuleState.On, untouched.State);
            Assert.Null(untouched.Change);
            Assert.Equal("warning", untouched.Level);

            // Without the project, its exception doesn't apply: the team rule is on.
            var noProject = RuleCatalog.Build(Engine().Compose(null), IsDefault);
            Assert.Equal(RuleState.On, noProject.Find(Kick).State);
            Assert.Null(noProject.Find(Email));
        }

        [Fact]
        public void State_AgreesWithTheChecker_ForEveryTableRule()
        {
            var comp = Engine().Compose(_topic);
            var cat = RuleCatalog.Build(comp, IsDefault);
            var rows = cat.Rules.Where(r => r.IsTable && !string.IsNullOrEmpty(r.Example)).ToList();
            var heads = Enumerable.Range(0, rows.Count).Where(i => rows[i].Content.Contains("Headings")).ToList();
            var hits = comp.Lint(rows.Select(r => r.Example).ToList(), heads, true);
            var checkedRules = 0;
            foreach (var h in hits.Where(h => h.RuleId != null))
            {
                var r = cat.Find(h.RuleId);
                Assert.NotEqual(RuleState.Off, r.State);
                Assert.Equal(r.Level, h.Severity);
                checkedRules++;
            }
            Assert.True(checkedRules > 100);
            foreach (var off in cat.Rules.Where(r => r.IsTable && r.State == RuleState.Off))
                Assert.DoesNotContain(hits, h => h.RuleId == off.Id);
            Assert.Contains(cat.Rules, r => r.IsTable && r.State == RuleState.Off);
        }

        [Fact]
        public void OpenRequests_ShowOnTheirRule_DecidedOnesDont()
        {
            var open = new GuideRequest { Id = "r-1", Writer = "pat@example.com", Created = DateTime.UtcNow, Kind = RequestKinds.TurnOff, RuleId = Email, Setting = "off", Reason = "x" };
            var others = new GuideRequest { Id = "r-2", Writer = "sam@example.com", Created = DateTime.UtcNow, Kind = RequestKinds.Severity, RuleId = Team1, Setting = "error", Reason = "y" };
            var decided = new GuideRequest { Id = "r-3", Writer = "pat@example.com", Created = DateTime.UtcNow, Kind = RequestKinds.TurnOff, RuleId = Quotes, Reason = "z", Status = RequestStatus.Declined };

            var cat = Catalog(new[] { open, others, decided });

            Assert.Same(open, cat.Find(Email).OpenRequest);
            Assert.True(cat.Find(Email).RequestIsMine);
            Assert.Same(others, cat.Find(Team1).OpenRequest);
            Assert.False(cat.Find(Team1).RequestIsMine);
            Assert.Null(cat.Find(Quotes).OpenRequest);
        }

        // ---- rules on the same words -------------------------------------------------------------------------

        [Fact]
        public void RulesOnTheSameWords_ListEachOther_WithoutChains()
        {
            var cat = Catalog();

            var pk = cat.Find(PleaseKindly);
            Assert.Contains(Kindly, pk.Overlaps);                  // "kindly" inside "please kindly"
            Assert.Contains(Please, pk.Overlaps);                  // "please" inside "please kindly" (another source)
            Assert.Contains(PleaseKindly, cat.Find(Kindly).Overlaps);
            Assert.DoesNotContain(Please, cat.Find(Kindly).Overlaps);   // "kindly" and "please" never flag the same words
            Assert.Empty(cat.Find(Kick).Overlaps);
            Assert.Equal(new[] { Kindly }, cat.OverlapsOf(pk).Where(r => r.Source.StartsWith("Team")).Select(r => r.Key).ToArray());
        }

        [Fact]
        public void EveryFindingThatStandsForSeveralRules_IsOneOverlapGroupInTheCatalogue()
        {
            var comp = Engine().Compose(_topic);
            var cat = RuleCatalog.Build(comp, IsDefault);
            var merged = 0;
            foreach (var r in cat.Rules.Where(x => x.IsTable && !string.IsNullOrEmpty(x.Example)))
                foreach (var h in comp.Lint(new[] { r.Example }, null).Where(h => h.AlsoRules.Count > 0))
                {
                    // The rule the finding names, and each rule it also stands for (what "also turn off" offers), list each other.
                    merged++;
                    foreach (var b in h.AlsoRules)
                    {
                        Assert.True(cat.Find(h.RuleId).Overlaps.Contains(b.Id), h.RuleId + " and " + b.Id + " share '" + h.Match + "'");
                        Assert.Contains(h.RuleId, cat.Find(b.Id).Overlaps);
                    }
                }
            Assert.True(merged > 5);
        }

        // ---- search and filters ------------------------------------------------------------------------------

        [Fact]
        public void Search_ByWordsOrId_AnyCase()
        {
            var cat = Catalog();
            Assert.Contains(cat.Search(new CatalogFilter { Text = "KICK" }), r => r.Key == Kick);
            Assert.Equal(new[] { Kick }, cat.Search(new CatalogFilter { Text = Kick }).Select(r => r.Key).ToArray());
            Assert.Equal(new[] { Kick }, cat.Search(new CatalogFilter { Text = "import kick" }).Select(r => r.Key).ToArray());   // every word, any order
            Assert.Contains(cat.Search(new CatalogFilter { Text = "Select Save" }), r => r.Key == Team1);   // the examples too
            Assert.Contains(cat.Search(new CatalogFilter { Text = "team words" }), r => r.Key == Team1);    // the page
            Assert.Empty(cat.Search(new CatalogFilter { Text = "zzqqxx" }));
            Assert.Equal(cat.Rules.Count, cat.Search(new CatalogFilter { Text = "  " }).Count);
        }

        [Fact]
        public void Filters_BySource_State_Fix_AndContent_Combine()
        {
            var cat = Catalog();
            const string team = "Team style guide (TeamGuide)";

            var project = cat.Search(new CatalogFilter { Source = "Project Docs" });
            Assert.True(project.Count >= 2 && project.All(r => r.Source == "Project Docs"));

            var off = cat.Search(new CatalogFilter { State = RuleState.Off });
            Assert.True(off.All(r => r.State == RuleState.Off));
            Assert.Contains(off, r => r.Key == ThisPage);
            Assert.Contains(off, r => r.Key == Kick);
            Assert.Contains(cat.Search(new CatalogFilter { State = RuleState.LevelChanged }), r => r.Key == Quotes);
            Assert.DoesNotContain(cat.Search(new CatalogFilter { State = RuleState.On }), r => r.Key == Quotes);

            var fix = cat.Search(new CatalogFilter { HasFix = true });
            Assert.True(fix.All(r => r.HasFix));
            Assert.Contains(fix, r => r.Key == Kick);
            Assert.Contains(cat.Search(new CatalogFilter { HasFix = false }), r => r.Key == Kindly);

            Assert.Contains("Steps", cat.ContentTags);
            Assert.Contains(cat.Search(new CatalogFilter { Content = "Steps" }), r => r.Key == Team1);
            var everywhere = cat.Search(new CatalogFilter { Content = RuleCatalog.Everywhere });
            Assert.True(everywhere.All(r => r.Content.Count == 0));
            Assert.Contains(everywhere, r => r.Key == Kick);

            Assert.Equal(new[] { PleaseKindly }, cat.Search(new CatalogFilter { Source = team, HasFix = true, State = RuleState.On }).Select(r => r.Key).ToArray());
            Assert.Equal(new[] { Kick }, cat.Search(new CatalogFilter { Source = team, Text = "kick", State = RuleState.Off }).Select(r => r.Key).ToArray());
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
