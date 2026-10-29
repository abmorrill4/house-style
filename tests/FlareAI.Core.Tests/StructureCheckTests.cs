using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class StructureCheckTests : IDisposable
    {
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "flareai-structure-" + Guid.NewGuid().ToString("N").Substring(0, 10));

        public void Dispose()
        {
            try { Directory.Delete(_temp, true); } catch (Exception) { }
        }

        private static string Repo
        {
            get
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
                return d.FullName;
            }
        }

        private static string Guide { get { return Path.Combine(Repo, "styleguide"); } }

        private static void Write(string root, string rel, string text)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        /// <summary>A team guide folder (a Flare project shape) with one table row, so it has a rule of its own.</summary>
        private static string TeamGuide(string folder)
        {
            Write(folder, "Team.flprj", "<CatapultProject />");
            Write(folder, @"Content\Resources\StyleGuide\Words.htm", "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Words</h1>" +
                "<table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr><td>kick off</td><td>start</td><td>Kick off the import.</td></tr></table></body></html>");
            return folder;
        }

        /// <summary>The built-in checks are listed once, in order, as the second source (after Google), and nowhere else.</summary>
        private static void AssertBuiltInListed(StyleEngine engine)
        {
            var cat = RuleCatalog.Build(engine.Compose(null), r => false);
            Assert.Equal("Built-in checks", cat.Sources[1]);
            Assert.Equal(StructureCheck.Rules.Select(r => r.Id).ToList(), cat.Rules.Where(r => r.Source == "Built-in checks").Select(r => r.Id).ToList());
        }

        private static List<XmlNode> Body(string html)
        {
            var doc = new XmlDocument();
            doc.LoadXml("<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body>" + html + "</body></html>");
            return doc.DocumentElement["body"].ChildNodes.Cast<XmlNode>().Where(n => n.NodeType == XmlNodeType.Element).ToList();
        }

        [Fact]
        public void FindsAccessibilityAndStructureProblems()
        {
            var found = StructureCheck.Of(Body(
                "<h1>Pay bills</h1><p>The payee list: <img src=\"a.png\" /></p><h3>Skipped a level</h3>" +
                "<h1>Second title</h1><table><tr><td>Name</td><td>Amount</td></tr></table>" +
                "<p>For details, <a href=\"x.htm\">click here</a>. Or see <a href=\"y.htm\">Payee limits</a>.</p>" +
                "<p>Call 800-555-0100 or write to help@bank.example, or visit www.bank.example/help.</p>"));
            var rules = found.Select(f => f.Rule).ToList();
            Assert.Contains("Structure.AltText", rules);
            Assert.Contains("Structure.HeadingOrder", rules);
            Assert.Contains("Structure.OneH1", rules);
            Assert.Contains("Structure.TableHeader", rules);
            Assert.Single(found, f => f.Rule == "Structure.LinkText");
            Assert.Equal(3, found.Count(f => f.Rule == "Structure.HardCoded"));
            Assert.All(found, f => Assert.Equal("manual", f.Kind));
            Assert.Equal("The payee list:", found.Single(f => f.Rule == "Structure.AltText").Excerpt.Trim()); // anchored on text before the image
            Assert.Equal("800-555-0100", found.First(f => f.Rule == "Structure.HardCoded").Excerpt);
        }

        [Fact]
        public void ImageAloneInItsParagraph_IsFlagged_AndFoundFromTheFinding()
        {
            // Flare's usual pattern: the image has a paragraph of its own, with no text to anchor on (bench: 10/10 missed).
            var blocks = Body("<h1>Pay bills</h1><p>Open the payee list.</p><p><img src=\"a.png\" /></p>");
            var f = Assert.Single(StructureCheck.Of(blocks), x => x.Rule == "Structure.AltText");
            Assert.Equal("Open the payee list.", f.Excerpt);
            Assert.NotNull(FlareAI.Core.Actions.AltText.MissingIn(f.Block));
        }

        [Fact]
        public void CodeIsLiteral_NotLinted_NotEditable()
        {
            var blocks = Body("<p>Edit <code>www.bank.example/FlareAI/owners.txt</code> to add owners.</p><pre>call 800-555-0100</pre>");
            Assert.Empty(StructureCheck.Of(blocks));                                        // no "hard-coded URL / phone"
            Assert.DoesNotContain(TextRuns.Of(blocks), r => r.Value.Contains("owners.txt")); // no AI run can touch it
            var line = Assert.Single(TextLines.Of(blocks, null), l => l.Text.Contains("to add owners"));
            Assert.DoesNotContain("owners.txt", line.Text);
        }

        [Fact]
        public void MarkerCss_AddressesEachBlockBySiblingChain_ColoredByWorstSeverity()
        {
            var blocks = Body("<h1>T</h1><p>One</p><ol><li>A</li><li>B</li></ol><MadCap:dropDown><p>In</p></MadCap:dropDown>");
            var li2 = blocks[2].ChildNodes[1];
            Assert.Equal("body > h1 + p", FlareAI.Core.Editing.MarkerCss.SelectorFor(blocks[1]));
            Assert.Equal("body > h1 + p + ol > li + li", FlareAI.Core.Editing.MarkerCss.SelectorFor(li2));
            Assert.Equal("body > h1 + p + ol + MadCap|dropDown > p", FlareAI.Core.Editing.MarkerCss.SelectorFor(blocks[3].FirstChild));
            var css = FlareAI.Core.Editing.MarkerCss.Css(new[] { new KeyValuePair<XmlNode, string>(blocks[1], "suggestion"), new KeyValuePair<XmlNode, string>(blocks[1], "error") });
            Assert.Contains("body > h1 + p { border-left: 4px solid #c62828;", css);   // worst severity wins
            Assert.Single(css.Split('\n'), l => l.StartsWith("body"));
        }

        [Fact]
        public void MarkerCss_SeverityHasNonColourBorderStyle()
        {
            var blocks = Body("<p>One</p><p>Two</p><p>Three</p>");
            var css = FlareAI.Core.Editing.MarkerCss.Css(new[]
            {
                new KeyValuePair<XmlNode, string>(blocks[0], "error"),
                new KeyValuePair<XmlNode, string>(blocks[1], "warning"),
                new KeyValuePair<XmlNode, string>(blocks[2], "suggestion")
            });
            Assert.Contains("border-left: 4px solid #c62828;", css);
            Assert.Contains("border-left: 4px dashed #e8a33d;", css);
            Assert.Contains("border-left: 4px dotted #5b8def;", css);
        }

        [Fact]
        public void CleanTopicHasNoFindings()
        {
            Assert.Empty(StructureCheck.Of(Body(
                "<h1>Pay bills</h1><p><img src=\"a.png\" alt=\"The Pay Bills page\" /></p><h2>Steps</h2>" +
                "<table><tr><th>Name</th></tr><tr><td>A</td></tr></table><p>See <a href=\"y.htm\">Payee limits</a>.</p>")));
        }

        [Fact]
        public void Findings_CarryAStableRuleId_ForEachOfTheSixChecks()
        {
            var found = StructureCheck.Settle(StructureCheck.Of(Body(
                "<h1>Pay bills</h1><p><img src=\"a.png\" /></p><h3>Skipped a level</h3><h1>Second title</h1>" +
                "<table><tr><td>Name</td></tr></table><p><a href=\"x.htm\">click here</a> Call 800-555-0100.</p>")), (id, name) => null);
            Assert.All(found, f => Assert.NotNull(f.RuleId));
            Assert.Equal(new[] { "structure:alt-text", "structure:hard-coded", "structure:heading-order", "structure:link-text", "structure:one-h1", "structure:table-header" },
                found.Select(f => f.RuleId).Distinct().OrderBy(id => id, System.StringComparer.Ordinal));
        }

        [Fact]
        public void Find_AcceptsIdOrNameInAnyCase()
        {
            Assert.Equal("structure:heading-order", StructureCheck.Find("STRUCTURE:HEADING-ORDER").Id);
            Assert.Equal("structure:heading-order", StructureCheck.Find("structure.headingorder").Id);
            Assert.Equal("structure:heading-order", StructureCheck.Find("Structure.HeadingOrder").Id);
            Assert.Null(StructureCheck.Find("structure:alt-text-x"));
            Assert.Null(StructureCheck.Find("Structure.AltTextFoo"));
            Assert.Null(StructureCheck.Find(null));
            Assert.Equal("structure:heading-order", StructureCheck.IdOf("Structure.HeadingOrder"));
            Assert.Equal("structure:alt-text-x", StructureCheck.IdOf("structure:alt-text-x"));
            Assert.Equal("Structure.AltTextFoo", StructureCheck.IdOf("Structure.AltTextFoo"));
        }

        [Fact]
        public void Of_LeavesRuleIdNull()
        {
            var found = StructureCheck.Of(Body("<h1>A</h1><h3>B</h3><img src=\"a.png\" />"));
            Assert.NotEmpty(found);
            Assert.All(found, f => Assert.Null(f.RuleId));
        }

        [Fact]
        public void Settle_LeavesAiFindingsAlone()
        {
            var ai = new FlareAI.Core.Actions.StyleFinding { Source = "ai", Rule = "Structure.HeadingOrder", Severity = "warning", Excerpt = "B" };
            var kept = StructureCheck.Settle(new List<FlareAI.Core.Actions.StyleFinding> { ai }, (id, name) => "off");
            Assert.Single(kept);
            Assert.Same(ai, kept[0]);
            Assert.Null(ai.RuleId);
            Assert.Equal("warning", ai.Severity);
        }

        [Fact]
        public void Rules_ListsTheSixBuiltInChecks_InEveryComposition()
        {
            var mine = Path.Combine(_temp, @"AppData\FlareAI\StyleGuide");
            GuideSeed.Ensure(Guide, mine);
            Assert.True(GuideSeed.IsPersonal(mine));
            AssertBuiltInListed(new StyleEngine(new[] { Guide, mine }, Guide));                                    // a personal guide
            AssertBuiltInListed(new StyleEngine(new[] { TeamGuide(Path.Combine(_temp, "TeamGuide")) }, Guide));   // a team guide only
            AssertBuiltInListed(new StyleEngine(new[] { Path.Combine(_temp, "Unreachable") }, Guide) { Fallback = () => new[] { Guide } });   // an unreadable guide folder
        }

        [Fact]
        public void Compose_RenamesATeamLayerNamedStructure()
        {
            var team = TeamGuide(Path.Combine(_temp, "structure"));
            var comp = new StyleEngine(new[] { team }, Guide).Compose(null);
            Assert.Contains(comp.Layers, l => l.Name == "structure-guide");
            Assert.DoesNotContain(comp.Layers, l => !l.IsBuiltIn && l.Name == "structure");

            var cat = RuleCatalog.Build(comp, r => false);
            Assert.Equal(cat.Rules.Count, cat.Rules.Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());   // no id collision
            Assert.Equal("Structure.HeadingOrder", cat.Find("structure:heading-order").Name);
            var teamRules = cat.Rules.Where(r => r.Source == "Team style guide (structure)").ToList();
            Assert.NotEmpty(teamRules);
            Assert.All(teamRules, r => Assert.StartsWith("StructureGuide.", r.Name));
            Assert.Equal(6, cat.Rules.Count(r => r.Source == "Built-in checks"));
        }

        [Fact]
        public void Settle_OffDropsTheFinding_ALevelChangesIt()
        {
            var found = StructureCheck.Of(Body("<h1>A</h1><h3>B</h3>"));
            Assert.Empty(StructureCheck.Settle(found, (id, name) => id == "structure:heading-order" ? "off" : null));

            var levelled = StructureCheck.Settle(StructureCheck.Of(Body("<h1>A</h1><h3>B</h3>")), (id, name) => id == "structure:heading-order" ? "error" : null);
            var f = Assert.Single(levelled);
            Assert.Equal("structure:heading-order", f.RuleId);
            Assert.Equal("error", f.Severity);
        }
    }
}
