using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Feedback;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// Where a rule comes from (#235): the starter rules that come with House Style are labeled Starter rules, a team's
    /// guide and the writer's own changes are Our rules, and a team guide that is missing or has no rules is named.
    /// </summary>
    public class GuideSourceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "flareai-src-" + Guid.NewGuid().ToString("N"));

        public GuideSourceTests() { Directory.CreateDirectory(_dir); }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

        internal static string Repo
        {
            get
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
                return d.FullName;
            }
        }

        internal static string Bundled { get { return Path.Combine(Repo, "styleguide"); } }

        private const string Lines = "We utilize it. Click here to learn more.";

        /// <summary>A guide project named like the starter (layer "company"), with one Avoid/Use row of its own.</summary>
        private string OwnGuide(string name, string avoid = "frobnicate", bool marker = false)
        {
            var root = Path.Combine(_dir, name);
            Write(root, "Guide.flprj", "<CatapultProject />");
            Write(root, @"FlareAI\layer.txt", "company");
            if (marker) Write(root, @"FlareAI\personal.txt", "personal");
            if (avoid != null)
                Write(root, @"Content\Resources\StyleGuide\Mine.htm", "<html><body><h1>Mine</h1><table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr>" +
                                                                      "<tr><td>" + avoid + "</td><td>adjust</td><td>We " + avoid + " it.</td></tr></table></body></html>");
            return root;
        }

        /// <summary>A team guide made the documented way: a copy of the starter project without its Google folder.</summary>
        private string StarterCopy(string name)
        {
            var root = Path.Combine(_dir, name);
            foreach (var f in Directory.GetFiles(Bundled, "*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(Bundled.Length + 1);
                if (rel.StartsWith(@"Content\Resources\StyleGuide\Google\", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel)));
                File.Copy(f, Path.Combine(root, rel));
            }
            return root;
        }

        private static void Write(string root, string rel, string text)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        private static string Label(StyleEngine e, LintHit h) { return e.SourceName(e.Compose(null), h.Layer, h.RuleId); }

        private static IList<LintHit> Lint(StyleEngine e, params string[] lines) { return e.Lint(lines, null, null); }

        [Fact]
        public void SourceName_IsStarterRules_ForABundledRule_AndOurRules_ForAPersonalRuleWithTheSameLayerName()
        {
            var mine = OwnGuide("Mine", marker: true);
            var e = new StyleEngine(new[] { Bundled, mine }, Bundled) { PersonalGuide = mine };
            var hits = Lint(e, "We utilize it.", "We frobnicate it.");
            var starter = hits.First(h => h.Match == "utilize");
            var own = hits.First(h => h.Match == "frobnicate");
            Assert.Equal("company", starter.Layer);
            Assert.Equal("company", own.Layer);
            Assert.Equal(StyleEngine.StarterRules, Label(e, starter));
            Assert.Equal("Our rules", Label(e, own));
        }

        [Fact]
        public void SourceName_IsOurRules_ForATeamGuideCopiedFromTheStarter()
        {
            var team = StarterCopy("Team");
            var e = new StyleEngine(new[] { team }, Bundled) { Fallback = () => new[] { Bundled } };
            var hits = Lint(e, "We utilize it.");
            var h = hits.First(x => x.Match == "utilize");
            Assert.Equal("company", h.Layer);
            Assert.Equal("Our rules", Label(e, h));
            Assert.DoesNotContain(hits, x => Label(e, x) == StyleEngine.StarterRules);
            Assert.Null(e.Notice(null));
        }

        [Fact]
        public void SourceName_FallsBackToTheLayerName_WithNoRuleId()
        {
            var e = new StyleEngine(new[] { Bundled }, Bundled);
            var comp = e.Compose(null);
            Assert.Equal("Our rules", e.SourceName(comp, "company", null));
            Assert.Equal("Our rules", e.SourceName(comp, "company", "company:no-such/1"));
            Assert.Equal("This project", e.SourceName(comp, "project", null));
            Assert.Equal("AI", e.SourceName(comp, "ai", null));
            Assert.Equal("Our rules", e.SourceName(null, "company", "company:terminology/1"));
        }

        [Fact]
        public void SourceName_IsGoogle_ForAGoogleFinding_WhenTheBundledGuideIsGoogleRoot()
        {
            var team = OwnGuide("NoGoogle");   // a team guide with no Google folder: Google's rules load from the bundled guide
            foreach (var e in new[] { new StyleEngine(new[] { Bundled }, Bundled), new StyleEngine(new[] { team }, Bundled) })
            {
                var google = Lint(e, "Click here to learn more.", "Please note that it's easy.").Where(h => h.Layer == "google").ToList();
                Assert.NotEmpty(google);
                Assert.All(google, h => Assert.Equal("Google", Label(e, h)));
            }
        }

        [Fact]
        public void MissingRoot_WithNoFallback_IsSkippedAsToday()
        {
            var missing = Path.Combine(_dir, "Gone");
            var e = new StyleEngine(new[] { missing, Bundled }, Bundled);
            Assert.Null(e.Notice(null));
            Assert.Contains(Lint(e, "We utilize it."), h => h.Match == "utilize");
        }

        [Fact]
        public void MissingTeamGuide_UsesTheFallback_AndTheNoticeSaysCantReach()
        {
            var missing = Path.Combine(_dir, "Share", "TeamGuide");
            var e = new StyleEngine(new[] { missing }, Bundled) { Fallback = () => new[] { Bundled } };
            var notice = e.Notice(null);
            Assert.NotNull(notice);
            Assert.StartsWith(StyleEngine.UnreadableNotice, notice);
            Assert.Contains("Can't reach " + missing + ".", notice);
            // The starter rules stand in, and say so.
            var h = Lint(e, "We utilize it.").First(x => x.Match == "utilize");
            Assert.Equal(StyleEngine.StarterRules, Label(e, h));
            // A bundled default row (a Google rule the starter turns off) still applies.
            Assert.True(e.IsOff(null, "google:word-list/221"));

            // The folder comes back: no notice, the team's rules again.
            Directory.CreateDirectory(Path.GetDirectoryName(missing));
            Directory.Move(StarterCopy("Moved"), missing);
            Assert.Null(e.Notice(null));
            Assert.Equal("Our rules", Label(e, Lint(e, "We utilize it.").First(x => x.Match == "utilize")));
        }

        [Fact]
        public void GlossaryOnlyTeamGuide_IsNotEmpty()
        {
            var team = OwnGuide("Glossary", avoid: null);
            Write(team, @"Content\Resources\StyleGuide\glossary.json", "{\"terms\":[{\"term\":\"Acme Cloud\"}]}");
            var e = new StyleEngine(new[] { team }, Bundled);
            Assert.Null(e.Notice(null));
        }

        [Fact]
        public void PlainFolderGuide_WithRules_IsNotEmpty()
        {
            var plain = Path.Combine(_dir, "Plain");
            Write(plain, "Words.htm", "<html><body><h1>Words</h1><table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr><td>frobnicate</td><td>adjust</td><td>We frobnicate it.</td></tr></table></body></html>");
            var e = new StyleEngine(new[] { plain }, Bundled);
            Assert.Null(e.Notice(null));
            Assert.Contains(Lint(e, "We frobnicate it."), h => h.Match == "frobnicate");
        }

        [Fact]
        public void EmptyTeamGuide_HasTheEmptyNotice_AndARuleAddedClearsIt()
        {
            var team = OwnGuide("Empty", avoid: null);
            var e = new StyleEngine(new[] { team }, Bundled) { Fallback = () => new[] { Bundled } };
            Assert.Equal(StyleEngine.EmptyNotice + " The folder is " + team + ".", e.Notice(null));
            Assert.NotEmpty(Lint(e, "Click here to learn more."));   // Google's guide still lints

            Write(team, @"Content\Resources\StyleGuide\Mine.htm", "<html><body><h1>Mine</h1><ul><li>Write for the reader.</li></ul></body></html>");
            Assert.Null(e.Notice(null));
        }

        [Fact]
        public void PersonalGuide_IsDecidedByPath_NotByTheMarkerFile()
        {
            // A shared guide carrying the personal marker is still a team guide: reported empty.
            var shared = OwnGuide("Shared", avoid: null, marker: true);
            var mine = OwnGuide("Mine", avoid: null);
            var e = new StyleEngine(new[] { shared }, Bundled) { PersonalGuide = mine };
            Assert.Equal(StyleEngine.EmptyNotice + " The folder is " + shared + ".", e.Notice(null));
        }

        [Fact]
        public void PersonalGuide_IsNeverReportedEmpty()
        {
            var mine = OwnGuide("Mine", avoid: null, marker: true);
            var e = new StyleEngine(new[] { Bundled, mine }, Bundled) { PersonalGuide = mine };
            Assert.Null(e.Notice(null));
        }
    }

    [Collection("Logging")]
    public class GuideSourceLoggingTests
    {
        [Fact]
        public void MissingTeamGuide_LogsOncePerFolder()
        {
            var missing = Path.Combine(Path.GetTempPath(), "flareai-gone-" + Guid.NewGuid().ToString("N"), "TeamGuide");
            var lines = new List<string>();
            var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
            Diagnostics.Sink = l => { lock (lines) lines.Add(l); };
            Diagnostics.Enabled = true;
            try
            {
                var e = new StyleEngine(new[] { missing }, GuideSourceTests.Bundled) { Fallback = () => new[] { GuideSourceTests.Bundled } };
                e.Notice(null); e.Notice(null); e.Invalidate(); e.Notice(null);
                var logged = lines.Where(l => l.Contains("can't be reached")).ToList();
                Assert.Single(logged);
                var anonymized = ReportBuilder.Anonymize(logged[0], new PluginSettings { StyleLayers = missing });
                Assert.DoesNotContain(missing, anonymized);
                Assert.Contains("[team guide]", anonymized);
            }
            finally { Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled; }
        }
    }
}
