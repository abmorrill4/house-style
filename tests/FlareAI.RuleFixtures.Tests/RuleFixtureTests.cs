using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Style;
using FlareAI.FixtureGen;
using Xunit;
using Xunit.Abstractions;

namespace FlareAI.RuleFixtures.Tests
{
    /// <summary>
    /// Rule coverage fixtures: every lint row of the bundled Google and Company guides, and every structure check, with Bad and
    /// Good cases in realistic contexts. Fixtures are generated (tools/FlareAI.FixtureGen) from the guide's own Example, Avoid and
    /// Use cells; where a rule should fire per context is declared in tools/FlareAI.FixtureGen/expectations.yaml, not in engine code.
    /// </summary>
    public class RuleFixtureTests
    {
        private readonly ITestOutputHelper _out;
        public RuleFixtureTests(ITestOutputHelper output) { _out = output; }

        public static IEnumerable<object[]> RuleIds() { return Repo.LoadRules().Select(r => new object[] { r.Id }); }
        public static IEnumerable<object[]> StructureRules() { return Repo.LoadStructure().Rules.Select(r => new object[] { r.Rule }); }

        // ---- per rule ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Bad fires in the contexts the table says (this rule or its canonical owner), stays quiet where it shouldn't, Good is clean
        /// against all rules, the fix leaves nothing new, turning the rule off (project and team) leaves nothing on the span,
        /// the same rule is shown on every run and after a reload, and the pattern can match plain text.
        /// </summary>
        [Theory]
        [MemberData(nameof(RuleIds))]
        public void Rule_HasNoUnlistedFailures(string ruleId)
        {
            var unlisted = Suite.Result.Failures.Where(f => f.RuleId == ruleId).ToList();
            Assert.True(unlisted.Count == 0, unlisted.Count + " new failure(s) for " + ruleId + " (fix the rule):\n" +
                string.Join("\n", unlisted.Take(25).Select(f => "  " + f).ToArray()));
        }

        [Theory]
        [MemberData(nameof(StructureRules))]
        public void StructureCheck_ReportsBadAndStaysQuietOnGood(string rule)
        {
            var unlisted = Suite.Result.Failures.Where(f => f.RuleId == rule).ToList();
            Assert.True(unlisted.Count == 0, string.Join("\n", unlisted.Select(f => f.ToString()).ToArray()));
        }

        // ---- coverage and freshness ----------------------------------------------------------------------------------

        [Fact]
        public void EveryLintRowHasFixtures()
        {
            var fixtures = Repo.LoadRules();
            var guide = GuideSet.Load(Repo.Guide);
            var ids = guide.Rows.Select(r => r.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Empty(ids.Except(fixtures.Select(f => f.Id)));
            Assert.Empty(fixtures.Select(f => f.Id).Except(ids));
            Assert.Contains(fixtures, f => f.Layer == "google");
            Assert.Contains(fixtures, f => f.Layer == "company");
            foreach (var f in fixtures.Where(f => !f.MarkupOnly))
            {
                Assert.True(f.Bad.Count > 0, f.Id + " has no Bad case");
                Assert.True(f.Bad.Any(b => b.ByContext().Any(x => x.Value == Outcome.Fires)), f.Id + " expects to fire nowhere");
                Assert.True(f.Bad.SelectMany(b => b.ByContext()).Any(x => x.Key == "paragraph"), f.Id + " has no paragraph case");
                if (f.Action == "replace" && f.Use.Trim().Length > 0 && !GuideLayer.IsFlagOnly(f.Use) && !GuideLayer.IsDelete(f.Use))
                    Assert.True(f.Good.Count > 0, f.Id + " has no Good case");
            }
            foreach (var f in fixtures.Where(f => f.MarkupOnly)) Assert.Empty(f.Bad);
        }

        [Fact]
        public void EveryContextOfTheTableCoversProseAndLiteralText()
        {
            var manifest = Repo.LoadManifest();
            foreach (var needed in new[] { "paragraph", "step", "step-p", "list", "heading-h2", "heading-h4", "table-header", "table-cell", "note", "caption", "dropdown-head",
                                           "link-a", "link-xref", "ui-b", "ui-span", "code-inline", "pre", "kbd", "variable-adjacent", "conditional-span" })
                Assert.Contains(needed, manifest.Contexts);
        }

        [Fact]
        public void StructureChecks_AreAllCovered()
        {
            var rules = Repo.LoadStructure().Rules;
            foreach (var name in new[] { "Structure.AltText", "Structure.OneH1", "Structure.HeadingOrder", "Structure.TableHeader", "Structure.LinkText", "Structure.HardCoded" })
            {
                var r = rules.SingleOrDefault(x => x.Rule == name);
                Assert.True(r != null, name + " has no cases");
                Assert.NotEmpty(r.Bad);
                Assert.NotEmpty(r.Good);
            }
        }

        /// <summary>The fixtures on disk are what the generator writes from today's guide, templates and expectations.</summary>
        [Fact]
        public void Fixtures_AreRegeneratedWhenTheGuideChanges()
        {
            var files = Generator.Generate(Repo.Guide, Path.Combine(Repo.GenDir, "expectations.yaml"), Path.Combine(Repo.GenDir, "structure-cases.json"), Path.Combine(Repo.GenDir, "templates"));
            var stale = Program.Stale(Repo.FixtureDir, files);
            Assert.True(stale.Length == 0, "tests/fixtures/rules is out of date with the style guide, templates or expectations.yaml. Run " +
                @"tools\FlareAI.FixtureGen\bin\<config>\net48\FlareAI.FixtureGen.exe and commit the result. Stale: " + string.Join(", ", stale.Take(10).ToArray()));
            Assert.Equal(GuideSet.Stamp(Repo.Guide), Repo.LoadManifest().GuideStamp);
        }

        // ---- the harness itself --------------------------------------------------------------------------------------

        [Fact]
        public void Templates_PlaceTheTextWhereTheContextSays()
        {
            var ctx = ContextSet.Load(Path.Combine(Repo.GenDir, "templates"));
            var expect = Expectations.Load(Path.Combine(Repo.GenDir, "expectations.yaml"));
            foreach (var name in expect.Contexts)
            {
                var frame = Suite.FrameOf(ctx, name);
                if (expect.ClassOf(name) == "literal") Assert.True(frame == null, name + ": code-like text must not reach the linter");
                else
                {
                    Assert.True(frame != null, name + ": the text must be linted");
                    Assert.Equal(name.StartsWith("heading-"), frame.Heading);
                }
            }
            Assert.Contains("Acme", Suite.FrameOf(ctx, "variable-adjacent").Suffix);   // the variable resolves to its value
            Assert.Contains("Click", Suite.FrameOf(ctx, "ui-b").Prefix);
        }

        [Fact]
        public void Expectations_FollowTheTags()
        {
            var e = Expectations.Load(Path.Combine(Repo.GenDir, "expectations.yaml"));
            Func<string[], string, string> at = (tags, c) => e.For("x:y/1", tags, c);
            Assert.Equal(Outcome.Fires, at(new string[0], "paragraph"));
            Assert.Equal(Outcome.Clean, at(new string[0], "ui-b"));
            Assert.Equal(Outcome.Clean, at(new string[0], "pre"));
            Assert.Equal(Outcome.Fires, at(new[] { "Steps" }, "step"));
            Assert.Equal(Outcome.Clean, at(new[] { "Steps" }, "paragraph"));
            Assert.Equal(Outcome.Fires, at(new[] { "Lists" }, "step"));
            Assert.Equal(Outcome.Any, at(new[] { "UI" }, "ui-b"));
            Assert.Equal(Outcome.Fires, at(new[] { "Numbers" }, "table-cell"));
            Assert.Equal(Outcome.Fires, e.For("google:tables/22", new[] { "Tables" }, "paragraph"));
            Assert.Equal(Outcome.Clean, e.For("google:tables/21", new[] { "Tables" }, "paragraph"));
        }

        /// <summary>
        /// The bulk checks turn a rule off by adding an override to a layer in memory. This proves the real paths agree: a project's
        /// overrides.ini and the team's Rule/Setting table (written by GuideOverrides, as the Turn off dialog does) both silence the row.
        /// </summary>
        [Fact]
        public void TurnOff_ThroughTheFilesOnDisk_SilencesTheRule()
        {
            var guide = Repo.Guide;
            var sample = "Tenants can open the admin panel.";
            var baseline = new StyleEngine(new[] { guide }, guide).Lint(new[] { sample }, null, null);
            var id = baseline.First(h => h.Match == "Tenants").RuleId;
            Assert.NotNull(id);

            var tmp = Path.Combine(Path.GetTempPath(), "flareai-rulefixtures-off-" + Guid.NewGuid().ToString("N"));
            try
            {
                // project: Content\Resources\StyleGuide\overrides.ini in a Flare project
                var project = Path.Combine(tmp, "docs");
                Directory.CreateDirectory(Path.Combine(project, @"Content\Resources\StyleGuide"));
                File.WriteAllText(Path.Combine(project, "Docs.flprj"), "<CatapultProject />");
                File.WriteAllText(Path.Combine(project, @"Content\Resources\StyleGuide\overrides.ini"), id + "=off\n");
                Assert.DoesNotContain(new StyleEngine(new[] { guide }, guide).Lint(new[] { sample }, null, project), h => h.RuleId == id);
                Assert.Contains(new StyleEngine(new[] { guide }, guide).Lint(new[] { sample }, null, null), h => h.RuleId == id);   // other projects still see it

                // team: a copy of the company guide, with the row written by GuideOverrides
                var team = Path.Combine(tmp, "team");
                CopyDirectory(Path.Combine(guide, @"Content\Resources\StyleGuide\Company"), Path.Combine(team, @"Content\Resources\StyleGuide\Company"));
                CopyDirectory(Path.Combine(guide, "FlareAI"), Path.Combine(team, "FlareAI"));
                File.WriteAllText(Path.Combine(team, "Team.flprj"), "<CatapultProject />");
                Assert.Contains(new StyleEngine(new[] { team }, guide).Lint(new[] { sample }, null, null), h => h.RuleId == id);
                GuideOverrides.Set(team, id, "off", "fixture check");
                Assert.DoesNotContain(new StyleEngine(new[] { team }, guide).Lint(new[] { sample }, null, null), h => h.RuleId == id);
            }
            finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
