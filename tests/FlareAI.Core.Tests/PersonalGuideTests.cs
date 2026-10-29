using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// The writer's own guide (no team guide configured) is a layer after the bundled guide holding only their changes:
    /// bundled defaults keep arriving with plugin updates, personal rows still win (and can turn a default back on), and
    /// an old full copy is reduced to what the writer changed.
    /// </summary>
    public class PersonalGuideTests : IDisposable
    {
        private const string ThisPage = "google:word-list/221";       // turned off by the bundled Company/Google-overrides.htm
        private const string ThisPageText = "Read this page for details.";
        private const string QuotesRule = "google:quotation-marks/2";  // set to suggestion by the bundled guide
        private const string Curly = "The section’s title is “Care and feeding of the emu.”";
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "flareai-personal-" + Guid.NewGuid().ToString("N"));
        private readonly string _pluginDir, _userGuide, _bundled;

        public PersonalGuideTests()
        {
            _pluginDir = Path.Combine(_temp, @"Program Files\Flare\Plugins\FlareAI");
            _userGuide = Path.Combine(_temp, @"AppData\FlareAI\StyleGuide");
            _bundled = PluginSettings.BundledGuide(_pluginDir);
            foreach (var f in Directory.GetFiles(RepoGuide, "*", SearchOption.AllDirectories))
            {
                var to = _bundled + f.Substring(RepoGuide.Length);
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

        private string Style(string rel) { return Path.Combine(_userGuide, @"Content\Resources\StyleGuide\" + rel); }

        private StyleEngine Engine(PluginSettings s) { return new StyleEngine(() => s.GuideRoots(_pluginDir, _userGuide), _bundled); }

        private Team TeamFor(PluginSettings s)
        {
            var t = Team.Load(s.GuideRoot(_pluginDir, _userGuide), null, "pat@example.com");
            t.SeedFrom = s.ReadOnlyGuide(_pluginDir);
            return t;
        }

        private static List<LintHit> Hits(StyleEngine e, string text, string rule) { return e.Lint(new[] { text }, null, null).Where(h => h.RuleId == rule).ToList(); }

        private List<RuleOverride> TurnedOffView(PluginSettings s)
        {
            return RuleOverrides.InEffect(s.GuideRoots(_pluginDir, _userGuide), r => PluginSettings.IsUnder(r, _pluginDir), r => "My style guide");
        }

        private List<string> Files(string root)
        {
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => f.Substring(root.Length + 1)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        [Fact]
        public void TheGoogleRule_ForThisPage_FiresWithoutTheBundledDefault()
        {
            Assert.NotEmpty(Hits(new StyleEngine(new string[0], _bundled), ThisPageText, ThisPage));   // Google alone flags it
        }

        [Fact]
        public void AFreshPersonalGuide_IsAMinimalProject_WithNoBundledTopics()
        {
            var team = TeamFor(new PluginSettings());
            Assert.Equal(_userGuide, team.WritableGuide());

            Assert.Equal(new[] { @"FlareAI\layer.txt", @"FlareAI\personal.txt", "StyleGuide.flprj" }, Files(_userGuide).ToArray());
            Assert.True(Directory.Exists(Path.Combine(_userGuide, @"Content\Resources\StyleGuide")));
            Assert.True(GuideSeed.IsSeeded(_userGuide));
            Assert.True(GuideSeed.IsPersonal(_userGuide));
            var flprj = new XmlDocument();
            flprj.Load(Path.Combine(_userGuide, "StyleGuide.flprj"));
            Assert.Equal("CatapultProject", flprj.DocumentElement.Name);
        }

        [Fact]
        public void TheBundledDefault_TurningOffThisPage_AppliesWithAPersonalGuide()
        {
            var s = new PluginSettings();
            var team = TeamFor(s);
            GuideOverrides.Set(team, QuotesRule, "off", "Curly quotes are fine.");
            var engine = Engine(s);

            Assert.Equal(new[] { _bundled, _userGuide }, s.GuideRoots(_pluginDir, _userGuide).ToArray());
            Assert.True(engine.IsOff(null, ThisPage));
            Assert.Empty(Hits(engine, ThisPageText, ThisPage));
            Assert.True(engine.IsOff(null, QuotesRule));                                              // my change applies too
            Assert.Equal(1, engine.Rules(null).Count(r => r.Id == "company:terminology/1"));          // bundled Company rules, once
        }

        [Fact]
        public void APersonalRow_WinsOverTheBundledSetting()
        {
            var s = new PluginSettings();
            var engine = Engine(s);
            Assert.All(Hits(engine, Curly, QuotesRule), h => Assert.Equal("suggestion", h.Severity));   // the bundled default
            GuideOverrides.Set(TeamFor(s), QuotesRule, "error", "We publish code samples.");
            var hits = Hits(engine, Curly, QuotesRule);
            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.Equal("error", h.Severity));
        }

        [Fact]
        public void TurningABundledDefaultBackOn_WritesAnOnRow_AndRemovingItRestoresTheDefault()
        {
            var s = new PluginSettings();
            var team = TeamFor(s);
            var engine = Engine(s);
            GuideOverrides.Set(team, QuotesRule, "off", "Curly quotes are fine.");

            var view = TurnedOffView(s);
            var mine = view.Single(o => o.RuleId == QuotesRule);
            Assert.False(mine.IsDefault);
            Assert.Equal("My style guide", mine.Scope);
            Assert.Equal("pat@example.com", mine.By);
            var def = view.Single(o => o.RuleId == ThisPage);
            Assert.True(def.IsDefault);
            Assert.Equal(RuleOverrides.DefaultScope, def.Scope);
            Assert.Equal("off", def.Setting);
            Assert.DoesNotContain(view, o => o.IsDefault && o.RuleId == QuotesRule);                 // my row replaces the default row

            var file = RuleOverrides.TurnBackOn(def, team);
            Assert.Equal(Style(GuideSeed.PersonalTopic), file);
            Assert.False(engine.IsOff(null, ThisPage));
            var hits = Hits(engine, ThisPageText, ThisPage);
            Assert.NotEmpty(hits);
            Assert.Equal(Hits(new StyleEngine(new string[0], _bundled), ThisPageText, ThisPage)[0].Severity, hits[0].Severity);   // the rule's own level
            var on = TurnedOffView(s).Single(o => o.RuleId == ThisPage);
            Assert.False(on.IsDefault);
            Assert.True(on.IsTurnOn);
            Assert.Equal("On", Severity.Label(on.Setting));

            RuleOverrides.TurnBackOn(on, team);                                                      // "Use the default"
            Assert.True(engine.IsOff(null, ThisPage));
            Assert.True(TurnedOffView(s).Single(o => o.RuleId == ThisPage).IsDefault);
        }

        [Fact]
        public void BuildStyleGuide_Add_WritesToMyFromOurContentTopic()
        {
            var s = new PluginSettings();
            var team = TeamFor(s);
            int added;
            var topic = GuideBuilder.AddToGuide(team, new List<RuleCandidate> { new RuleCandidate { Avoid = "click on", Use = "click", Why = "Shorter.", Example = "Click on Save." } }, out added);

            Assert.Equal(1, added);
            Assert.Equal(Style(GuideBuilder.ContentTopic), topic);
            var engine = Engine(s);
            Assert.Contains(engine.Lint(new[] { "Click on Save." }, null, null), h => h.RuleId != null && h.RuleId.StartsWith("company:from-our-content/"));
            Assert.True(engine.IsOff(null, ThisPage));                                                 // bundled defaults still apply
            Assert.False(File.Exists(Style(@"Company\Terminology.htm")));
        }

        // ---- upgrade of an old full copy ------------------------------------------------------------------------------

        /// <summary>The old seed: the bundled guide minus Google, copied to the writer's guide, then changed by the writer.</summary>
        private void OldFullCopy()
        {
            foreach (var f in Directory.GetFiles(_bundled, "*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(_bundled.Length + 1);
                if (rel.StartsWith(@"Content\Resources\StyleGuide\Google\", StringComparison.OrdinalIgnoreCase)) continue;
                var to = Path.Combine(_userGuide, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(f, to);
                File.SetAttributes(to, FileAttributes.Normal);
            }
            // The copy predates the bundled defaults (its table was empty then; "this page" is one of them now).
            Edit(Style(@"Company\Google-overrides.htm"), x => { foreach (XmlNode tr in x.SelectNodes("//*[local-name()='tr'][*[local-name()='td']]")) tr.ParentNode.RemoveChild(tr); });
            // Rule changes the writer made (with who and when), and one typed in by hand.
            GuideOverrides.Set(_userGuide, "google:word-list/6", "off", "We say utilize.", "pat@example.com", new DateTime(2026, 9, 1));
            GuideOverrides.Set(_userGuide, "google:word-list/5", "error", "Hand edit.");
            // A rule added with Build style guide, and a rule the writer added to a bundled topic.
            GuideBuilder.Write(Style("From-our-content.htm"), new List<RuleCandidate> { new RuleCandidate { Avoid = "click on", Use = "click", Why = "Shorter.", Example = "Click on Save." } }, "From our content");
            Edit(Style(@"Company\Terminology.htm"), x =>
            {
                var ul = (XmlElement)x.SelectSingleNode("//*[local-name()='ul']");
                var li = x.CreateElement("li", ul.NamespaceURI);
                li.InnerText = "Call the mobile app \"the app\".";
                ul.AppendChild(li);
            });
            // A copy made before the bundled topic had keys, and one older than a bundled row: both are bundled copies.
            Edit(Style(@"Company\Claims.htm"), x => { foreach (XmlElement e in x.SelectNodes("//*[@id]")) e.RemoveAttribute("id"); });
            Edit(Style(@"Company\Plain-language.htm"), x => Remove(x, "(//*[local-name()='tr'][@id])[last()]"));
        }

        private static void Edit(string path, Action<XmlDocument> change)
        {
            var x = new XmlDocument { PreserveWhitespace = true };
            x.Load(path);
            change(x);
            x.Save(path);
        }

        private static void Remove(XmlDocument x, string xpath)
        {
            var n = x.SelectSingleNode(xpath);
            Assert.NotNull(n);
            n.ParentNode.RemoveChild(n);
        }

        private Dictionary<string, string> Snapshot()
        {
            return Files(_userGuide).ToDictionary(f => f, f => File.ReadAllText(Path.Combine(_userGuide, f)));
        }

        private string[] Backups() { return Directory.GetDirectories(Path.GetDirectoryName(_userGuide), "StyleGuide.backup-*"); }

        [Fact]
        public void AnOldFullCopy_IsReducedToTheWritersChanges_WithABackup()
        {
            OldFullCopy();
            var before = Snapshot();
            var s = new PluginSettings();

            Assert.Equal(new[] { _bundled, _userGuide }, s.GuideRoots(_pluginDir, _userGuide).ToArray());

            Assert.True(GuideSeed.IsPersonal(_userGuide));
            var backup = Backups().Single();
            Assert.Equal(before.Keys.ToArray(), Files(backup).ToArray());
            Assert.Contains("google:word-list/6", File.ReadAllText(Path.Combine(backup, @"Content\Resources\StyleGuide\Company\Google-overrides.htm")));

            Assert.Equal(new[]
            {
                @"Content\Resources\StyleGuide\Company\Terminology.htm",
                @"Content\Resources\StyleGuide\From-our-content.htm",
                @"Content\Resources\StyleGuide\" + GuideSeed.PersonalTopic,
                @"FlareAI\layer.txt",
                @"FlareAI\personal.txt",
                "StyleGuide.flprj"
            }, Files(_userGuide).ToArray());

            var rows = RuleOverrides.Read(_userGuide, "My style guide");
            Assert.Equal(new[] { "google:word-list/5", "google:word-list/6" }, rows.Select(r => r.RuleId).OrderBy(r => r).ToArray());
            var mine = rows.Single(r => r.RuleId == "google:word-list/6");
            Assert.Equal("off", mine.Setting);
            Assert.Equal("pat@example.com", mine.By);
            Assert.Equal("2026-09-01", mine.Date);
            Assert.Equal("We say utilize.", mine.Note);
            Assert.Equal("error", rows.Single(r => r.RuleId == "google:word-list/5").Setting);

            var engine = Engine(s);
            Assert.True(engine.IsOff(null, ThisPage));                                                  // the new bundled default arrives
            Assert.Empty(Hits(engine, ThisPageText, ThisPage));
            Assert.True(engine.IsOff(null, "google:word-list/6"));                                          // my changes still apply
            var rules = engine.Rules(null);
            Assert.Equal(1, rules.Count(r => r.Id == "company:terminology/1"));                         // my Terminology stands in for the bundled one
            Assert.Contains(rules, r => r.Text.Contains("the mobile app"));
            Assert.Equal(1, rules.Count(r => r.Id == "company:claims/1"));
            Assert.Equal(rules.Count(r => r.Id.StartsWith("company:plain-language/")), GuideLayer.Load(_bundled, null, null, "Google").Items.Count(r => r.Id.StartsWith("company:plain-language/")));
            Assert.Contains(engine.Lint(new[] { "Click on Save." }, null, null), h => h.RuleId != null && h.RuleId.StartsWith("company:from-our-content/"));
            Assert.Equal(1, engine.Lint(new[] { "Tenants can open the admin panel." }, null, null).Count(h => h.Match == "Tenants"));
        }

        [Fact]
        public void AnOverridesTopicWithMyOwnRules_IsKeptAsMine_NotLost()
        {
            // Seen on a real old copy: Google-overrides.htm had rules the bundled topic lacks, so the upgrade saves the
            // rest as Google-overrides-mine.htm, a file that doesn't exist yet (saving it used to throw).
            OldFullCopy();
            var file = Path.Combine(_userGuide, @"Content\Resources\StyleGuide\Company\Google-overrides.htm");
            File.WriteAllText(file, File.ReadAllText(file).Replace("</body>", "<ul><li>Spell out em dashes in UI text.</li></ul></body>"));

            Assert.True(GuideSeed.Upgrade(_bundled, _userGuide));

            var mine = Path.Combine(_userGuide, @"Content\Resources\StyleGuide\Company\Google-overrides-mine.htm");
            Assert.True(File.Exists(mine));
            Assert.Contains("Spell out em dashes", File.ReadAllText(mine));
            Assert.False(File.Exists(file));
            Assert.True(GuideSeed.IsPersonal(_userGuide));
            Assert.Contains(Engine(new PluginSettings()).Rules(null), r => r.Text.Contains("Spell out em dashes"));
        }

        [Fact]
        public void TheUpgrade_IsIdempotent()
        {
            OldFullCopy();
            var s = new PluginSettings();
            s.GuideRoots(_pluginDir, _userGuide);
            var after = Snapshot();

            s.GuideRoots(_pluginDir, _userGuide);
            Assert.True(GuideSeed.Upgrade(_bundled, _userGuide));
            GuideSeed.Ensure(_bundled, _userGuide);

            Assert.Single(Backups());
            Assert.Equal(after, Snapshot());
        }
    }
}
