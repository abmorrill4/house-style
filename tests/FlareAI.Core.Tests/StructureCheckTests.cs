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

        private static XmlDocument Page(string body, string head = "")
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml("<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head><title>t</title>" + head + "</head><body>" + body + "</body></html>");
            return doc;
        }

        private static List<XmlNode> BlocksOf(XmlDocument doc)
        {
            return doc.DocumentElement["body"].ChildNodes.Cast<XmlNode>().Where(n => n.NodeType == XmlNodeType.Element).ToList();
        }

        [Fact]
        public void ImageWithNoAltText_IsNamedByItsFileName_AndLocatedOnTheImage()
        {
            var doc = Page("<h1>Pay bills</h1><p>Before the picture. <img src=\"../Images/Logo.png\" title=\"Company logo\" /> After.</p>");
            var f = Assert.Single(StructureCheck.Of(BlocksOf(doc)), x => x.Rule == "Structure.AltText");
            Assert.Equal("Alt text for \"Logo.png\"", f.Title);
            Assert.Equal("Company logo", f.ImageDetail);
            XmlElement img;
            Assert.True(FindingLocator.TryLocateImage(doc, f, out img));
            Assert.Equal("img", img.LocalName);
            Directory.CreateDirectory(_temp);
            var path = Path.Combine(_temp, "t.htm");
            doc.Save(path);
            var adapter = new FakeDocumentAdapter(path, true);
            Assert.True(new FlareAI.Core.Actions.ActionRunner(null, null, 1024).GoTo(adapter, f));
            Assert.Equal("img", adapter.LastSelectedNode.LocalName);
            Assert.Equal("../Images/Logo.png", ((XmlElement)adapter.LastSelectedNode).GetAttribute("src"));
        }

        [Fact]
        public void ImageAloneInItsParagraph_IsLocatedOnTheImage_WithItsCaption()
        {
            var doc = Page("<h1>Pay bills</h1><p>Open the list.</p><p><img src=\"Images/payee%20list.png\" /></p><p class=\"Caption\">The payee list</p>");
            var f = Assert.Single(StructureCheck.Of(BlocksOf(doc)), x => x.Rule == "Structure.AltText");
            Assert.Equal("Alt text for \"payee list.png\"", f.Title);
            Assert.Equal("The payee list", f.ImageDetail);
            XmlElement img;
            Assert.True(FindingLocator.TryLocateImage(doc, f, out img));
            Assert.Equal("p", img.ParentNode.LocalName);
            Assert.Equal(img, doc.SelectSingleNode("//*[local-name()='img']"));
            Assert.Same(img.ParentNode, FindingLocator.LocateBlock(doc, f));
        }

        [Fact]
        public void ImageInATopicWithNoText_HasNoIssue_AsBefore()
        {
            // There is no text for the issue to anchor on, and the counts must not change with this wording and location fix.
            var doc = Page("<p><img src=\"only.png\" /></p>");
            Assert.DoesNotContain(StructureCheck.Of(BlocksOf(doc)), x => x.Rule == "Structure.AltText");
        }

        [Fact]
        public void ImageInsideALink_IsLocatedWhereItAppears()
        {
            var doc = Page("<p>Open <a href=\"x.htm\"><img src=\"Images/open.png\" /></a> to start.</p>");
            var f = Assert.Single(StructureCheck.Of(BlocksOf(doc)), x => x.Rule == "Structure.AltText");
            Assert.Equal("Alt text for \"open.png\"", f.Title);
            XmlElement img;
            Assert.True(FindingLocator.TryLocateImage(doc, f, out img));
            Assert.Equal("a", img.ParentNode.LocalName);
        }

        [Fact]
        public void ImageWithNoPathOrAnEmptyOne_IsStillNamedSomehow()
        {
            var doc = Page("<p>Here <img /> and here <img src=\"\" /> and a broken one <img src=\"..\\..\\missing\\gone%20now.png?v=2\" />.</p>");
            var found = StructureCheck.Of(BlocksOf(doc)).Where(x => x.Rule == "Structure.AltText").ToList();
            Assert.Equal(3, found.Count);
            Assert.StartsWith("Alt text for an image", found[0].Title);
            Assert.StartsWith("Alt text for an image", found[1].Title);
            Assert.Equal("Alt text for \"gone now.png\"", found[2].Title);
        }

        [Fact]
        public void TwoImagesInOneParagraph_EachIsLocated_AndKeepsItsOwnTitle()
        {
            var doc = Page("<p>Click <img src=\"a.png\" /> then <img src=\"b.png\" title=\"Save\" /> to finish.</p>");
            var found = StructureCheck.Of(BlocksOf(doc)).Where(x => x.Rule == "Structure.AltText").ToList();
            Assert.Equal(2, found.Count);
            Assert.Equal("Alt text for \"a.png\"", found[0].Title);
            Assert.Equal("Alt text for \"b.png\"", found[1].Title);
            Assert.Equal("Save", found[1].ImageDetail);
            XmlElement img;
            Assert.True(FindingLocator.TryLocateImage(doc, found[1], out img));
            Assert.Equal("b.png", img.GetAttribute("src"));
        }

        [Fact]
        public void TrackedChangesAroundTheImage_DontMoveTheIssue_AndDeletedTextIsNotAnAnchor()
        {
            var head = "<MadCap:changeData><MadCap:RemoveChange MadCap:id=\"7\" /></MadCap:changeData>";
            var doc = Page("<p>Kept words <span MadCap:changes=\"7\">deleted words</span> <img src=\"t.png\" /> <span MadCap:changes=\"7\">more deleted</span> after.</p>", head);
            var f = Assert.Single(StructureCheck.Of(BlocksOf(doc)), x => x.Rule == "Structure.AltText");
            Assert.Equal("Alt text for \"t.png\"", f.Title);
            Assert.Equal("Kept words", f.Excerpt.Trim());
            var img = doc.SelectSingleNode("//*[local-name()='img']");
            Assert.Equal("after.", FlareAI.Core.Editing.ImageSelection.NeighborRun(doc, img, true).Value.Trim());
            Assert.Equal("Kept words", FlareAI.Core.Editing.ImageSelection.NeighborRun(doc, img, false).Value.Trim());
        }

        [Fact]
        public void ImageIssueIsListedWhereItsAnchorTextWas()
        {
            var doc = Page("<h1>T</h1><p>Hello there. <img src=\"i.png\" /> Then more words.</p>");
            var image = Assert.Single(StructureCheck.Of(BlocksOf(doc)), x => x.Rule == "Structure.AltText");
            var textOnly = new FlareAI.Core.Actions.StyleFinding { Source = image.Source, Rule = "x", Excerpt = image.Excerpt, Before = image.Before, Block = image.Block };
            FlareAI.Core.Actions.ReviewOrder.Annotate(doc, new[] { image, textOnly });
            Assert.Equal(textOnly.Order, image.Order);
            Assert.False(image.IsStale);
        }

        [Fact]
        public void ImageIssue_IsStaleOnlyWhenTheImageIsGone()
        {
            var doc = Page("<p>Hello there. <img src=\"i.png\" /> Then more words.</p>");
            var image = Assert.Single(StructureCheck.Of(BlocksOf(doc)), x => x.Rule == "Structure.AltText");
            var img = doc.SelectSingleNode("//*[local-name()='img']");
            img.ParentNode.RemoveChild(img);
            FlareAI.Core.Actions.ReviewOrder.Annotate(doc, new[] { image });
            Assert.True(image.IsStale);
        }

        /// <summary>A model of Flare: an image takes no room in its positions, and it reports the image for a selection that touches it.</summary>
        private static Func<int, int, bool> FlareAt(int imageAt, List<int[]> asked)
        {
            return (start, length) =>
            {
                asked.Add(new[] { start, length });
                return length == 0 ? start == imageAt : start <= imageAt && imageAt <= start + length;
            };
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]   // the image is last
        [InlineData(false, true)]   // the image is first
        public void SelectingAnImage_FindsItEvenWhenFlaresPositionsAreOff(bool hasPrevious, bool hasNext)
        {
            for (var error = -ImageSelectionReach; error <= ImageSelectionReach; error++)
            {
                var asked = new List<int[]>();
                var flare = FlareAt(100 + error, asked);
                Assert.True(FlareAI.Core.Editing.ImageSelection.TrySelect(FlareAI.Core.Editing.ImageSelection.Candidates(hasPrevious, hasNext, 100), flare), "error " + error);
            }
        }

        private const int ImageSelectionReach = FlareAI.Core.Editing.ImageSelection.Reach;

        [Fact]
        public void SelectingAnImage_FallsBackToTheTextBesideIt_WhenFlareNeverReportsTheImage()
        {
            var besideAsked = 0;
            var r = FlareAI.Core.Editing.ImageSelection.Select(FlareAI.Core.Editing.ImageSelection.Candidates(false, true, 0), (s, l) => false, () => { besideAsked++; return true; });
            Assert.Equal(FlareAI.Core.Editing.ImageSelection.Result.Beside, r);
            Assert.Equal(1, besideAsked);
            Assert.Equal(FlareAI.Core.Editing.ImageSelection.Result.None, FlareAI.Core.Editing.ImageSelection.Select(FlareAI.Core.Editing.ImageSelection.Candidates(true, false, 5), (s, l) => false, () => false));
            // When Flare does report it, the text beside it is left alone.
            besideAsked = 0;
            Assert.Equal(FlareAI.Core.Editing.ImageSelection.Result.Image, FlareAI.Core.Editing.ImageSelection.Select(FlareAI.Core.Editing.ImageSelection.Candidates(true, true, 9), (s, l) => true, () => { besideAsked++; return true; }));
            Assert.Equal(0, besideAsked);
        }

        [Fact]
        public void SelectingAnImage_TriesTheBestSelectionFirst_AndGivesUpWhenFlareNeverReportsIt()
        {
            var asked = new List<int[]>();
            Assert.True(FlareAI.Core.Editing.ImageSelection.TrySelect(FlareAI.Core.Editing.ImageSelection.Candidates(true, true, 100), FlareAt(100, asked)));
            Assert.Equal(new[] { 99, 2 }, asked.Single());   // the character before it through the one after it
            asked.Clear();
            Assert.True(FlareAI.Core.Editing.ImageSelection.TrySelect(FlareAI.Core.Editing.ImageSelection.Candidates(false, true, 100), FlareAt(100, asked)));
            Assert.Equal(new[] { 100, 1 }, asked.Single());   // first in the topic
            Assert.False(FlareAI.Core.Editing.ImageSelection.TrySelect(FlareAI.Core.Editing.ImageSelection.Candidates(true, true, 100), (s, l) => false));
            Assert.DoesNotContain(FlareAI.Core.Editing.ImageSelection.Candidates(true, true, 0), t => t[0] < 0);
        }

        [Fact]
        public void TwoImagesWithTheSameFileName_AreToldApart_AndEachIsLocated()
        {
            var doc = Page("<h1>Pay bills</h1><p>First, open the payee list from the home page. <img src=\"a/step.png\" /></p><p>Then add the payee. <img src=\"b/step.png\" /></p>");
            var found = StructureCheck.Of(BlocksOf(doc)).Where(x => x.Rule == "Structure.AltText").ToList();
            Assert.Equal(2, found.Count);
            Assert.NotEqual(found[0].Title, found[1].Title);
            Assert.StartsWith("Alt text for \"step.png\" in \"First, open the payee list", found[0].Title);
            Assert.StartsWith("Alt text for \"step.png\" in \"Then add the payee", found[1].Title);
            XmlElement second;
            Assert.True(FindingLocator.TryLocateImage(doc, found[1], out second));
            Assert.Equal("b/step.png", second.GetAttribute("src"));
        }

        [Fact]
        public void SameFileTwice_IsLocatedByPlace()
        {
            var doc = Page("<p>One <img src=\"x.png\" /></p><p>Two <img src=\"x.png\" /></p>");
            var found = StructureCheck.Of(BlocksOf(doc)).Where(x => x.Rule == "Structure.AltText").ToList();
            Assert.Equal(2, found.Count);
            Assert.Equal("Alt text for \"x.png\" in \"One\"", found[0].Title);
            XmlElement img;
            Assert.True(FindingLocator.TryLocateImage(doc, found[1], out img));
            Assert.Same(doc.SelectNodes("//*[local-name()='img']")[1], img);
        }

        [Fact]
        public void DecorativeImage_IsNotFlagged_AndAMissingFileIsNamedAsWritten()
        {
            var doc = Page("<p>Hello <img src=\"d.png\" role=\"presentation\" /> <img src=\"..\\Images\\gone.png\" /></p>");
            var f = Assert.Single(StructureCheck.Of(BlocksOf(doc)), x => x.Rule == "Structure.AltText");
            Assert.Equal("Alt text for \"gone.png\"", f.Title);
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
        public void Names_MatchesAnAiIssueByRuleIdOrByItsOwnWords()
        {
            var order = StructureCheck.Find("structure:heading-order");
            var byName = new FlareAI.Core.Actions.StyleFinding { Source = "ai", Rule = "Heading order", Suggestion = "" };
            var byWords = new FlareAI.Core.Actions.StyleFinding { Source = "ai", Rule = "Skipped level", Suggestion = "The heading skips a level." };
            var byId = new FlareAI.Core.Actions.StyleFinding { Source = "ai", RuleId = "structure:heading-order", Rule = "Other", Suggestion = "" };
            var other = new FlareAI.Core.Actions.StyleFinding { Source = "ai", Rule = "Passive voice", Suggestion = "Use active voice." };
            Assert.True(StructureCheck.Names(order, byName));
            Assert.True(StructureCheck.Names(order, byWords));
            Assert.True(StructureCheck.Names(order, byId));
            Assert.False(StructureCheck.Names(order, other));
            Assert.False(StructureCheck.Names(StructureCheck.Find("structure:alt-text"), byName));
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

        /// <summary>A team guide whose overrides.ini holds these lines, and the engine that reads it.</summary>
        private StyleEngine EngineWithOverrides(string folder, string ini)
        {
            var team = TeamGuide(folder);
            Write(team, @"Content\Resources\StyleGuide\overrides.ini", ini);
            return new StyleEngine(new[] { team }, Guide);
        }

        [Fact]
        public void StructureFindings_DropsAnOffRule_ByIdAndByLegacyName()
        {
            var headings = "<h1>A</h1><h3>B</h3>";
            var legacy = EngineWithOverrides(Path.Combine(_temp, "Legacy"), "Structure.HeadingOrder = NO\n");
            var byId = EngineWithOverrides(Path.Combine(_temp, "ById"), "structure:heading-order = off\n");
            var none = EngineWithOverrides(Path.Combine(_temp, "None"), "");

            Assert.Single(none.StructureFindings(null, Body(headings)));   // the control: the rule fires without an override
            Assert.Empty(legacy.StructureFindings(null, Body(headings)));
            Assert.Empty(byId.StructureFindings(null, Body(headings)));
        }

        [Fact]
        public void StructureFindings_TakeTheOverrideLevel()
        {
            var byId = EngineWithOverrides(Path.Combine(_temp, "LevelById"), "structure:heading-order = suggestion\n");
            var f = Assert.Single(byId.StructureFindings(null, Body("<h1>A</h1><h3>B</h3>")));
            Assert.Equal("structure:heading-order", f.RuleId);
            Assert.Equal("suggestion", f.Severity);

            var legacy = EngineWithOverrides(Path.Combine(_temp, "LevelLegacy"), "Structure.HeadingOrder = suggestion\n");
            Assert.Equal("suggestion", Assert.Single(legacy.StructureFindings(null, Body("<h1>A</h1><h3>B</h3>"))).Severity);
        }
    }
}
