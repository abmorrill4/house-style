using System.Collections.Generic;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class InsertTests
    {
        private static IList<XmlNode> Parse(string xml)
        {
            return XhtmlFragment.ParseInto(xml, new XmlDocument());
        }

        [Theory]
        [InlineData("<p>One paragraph.</p>")]
        [InlineData("<p>With <strong>bold</strong>, <em>italic</em> and <code>code</code>.</p>")]
        [InlineData("<ul><li>One</li><li>Two <strong>bold</strong></li></ul>")]
        [InlineData("<ol><li>First<ul><li>Nested</li></ul></li></ol>")]
        public void PlainParagraphsAndLists_CanBeInserted(string xml)
        {
            Assert.Null(NodeWrites.InsertRefusal(Parse(xml)));
        }

        [Theory]
        [InlineData("<h2>A heading</h2>")]
        [InlineData("<table><tr><td>x</td></tr></table>")]
        [InlineData("<p><a href=\"x\">link</a></p>")]
        [InlineData("<p class=\"a\">attribute</p>")]
        [InlineData("<p id=\"a\">attribute</p>")]
        [InlineData("<p><MadCap:variable name=\"A.B\" /></p>")]
        [InlineData("<MadCap:snippetBlock src=\"a.flsnp\" />")]
        [InlineData("<p><img src=\"a.png\" /></p>")]
        [InlineData("<ul><li><ul><li><ul><li>Too deep</li></ul></li></ul></li></ul>")]
        public void AnythingElse_IsRefused_InTheWritersWords(string xml)
        {
            var refusal = NodeWrites.InsertRefusal(Parse(xml));
            Assert.NotNull(refusal);
            Assert.Contains("nothing was changed", refusal);
        }

        [Fact]
        public void TooManyBlocksOrWords_AreRefused()
        {
            Assert.NotNull(NodeWrites.InsertRefusal(Parse(string.Concat(Enumerable.Repeat("<p>x</p>", 9)))));
            Assert.NotNull(NodeWrites.InsertRefusal(Parse("<p>" + string.Join(" ", Enumerable.Repeat("word", 401)) + "</p>")));
            Assert.Null(NodeWrites.InsertRefusal(Parse("<p>" + string.Join(" ", Enumerable.Repeat("word", 400)) + "</p>")));
            Assert.NotNull(NodeWrites.InsertRefusal(new List<XmlNode>()));
        }

        private static XmlNode Body(string inner)
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml("<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body>" + inner + "</body></html>");
            return BlockSelector.FindBody(doc);
        }

        [Fact]
        public void TheAnchor_IsABodyLevelParagraphOrHeading_AndNothingElse()
        {
            var body = Body("<h1>Title</h1><p>Text <b>bold</b></p><ul><li>Item</li></ul><table><tr><td>Cell</td></tr></table><div><p>In a div</p></div><MadCap:dropDown><p>Hidden</p></MadCap:dropDown>");
            string refusal;
            var bold = body.SelectSingleNode("//*[local-name()='b']").FirstChild;
            Assert.Equal("p", DraftAnchor.Under(body, bold, out refusal).LocalName);
            Assert.Null(refusal);
            Assert.Equal("h1", DraftAnchor.Under(body, body.SelectSingleNode("//*[local-name()='h1']").FirstChild, out refusal).LocalName);
            foreach (var path in new[] { "//*[local-name()='li']", "//*[local-name()='td']", "//*[local-name()='div']/*", "//*[local-name()='dropDown']/*" })
            {
                Assert.Null(DraftAnchor.Under(body, body.SelectSingleNode(path), out refusal));
                Assert.Equal(DraftAnchor.NoPlace, refusal);
            }
        }

        [Fact]
        public void TheInsertIndex_IsRightAfterTheAnchor_AndGoneWhenTheAnchorIsRemoved()
        {
            var body = Body("<p>One</p><p>Two</p>");
            var first = body.SelectSingleNode("*");
            var index = DraftAnchor.InsertIndex(body, first);
            Assert.Equal(body.ChildNodes.Cast<XmlNode>().ToList().IndexOf(first) + 1, index);
            body.RemoveChild(first);
            Assert.Equal(-1, DraftAnchor.InsertIndex(body, first));
        }

        [Fact]
        public void Gaps_AreFilledIn_AndCounted()
        {
            var xml = "<p>Set it to {{GAP}} and {{GAP}}.</p>";
            Assert.Equal(2, DraftFacts.FillGaps(ref xml));
            Assert.DoesNotContain("{{GAP}}", xml);
            Assert.Contains("<strong>Add details here.</strong>", xml);
            var none = "<p>Nothing missing.</p>";
            Assert.Equal(0, DraftFacts.FillGaps(ref none));
        }

        [Fact]
        public void NewTerms_ListNumbersAndNamesTheTopicDoesNotHave()
        {
            var known = "Back up the database before you upgrade. Run the backup tool for the Billing service.";
            var terms = DraftFacts.NewTerms(known, "The backup finishes in 30 seconds. Then restart the Billing service and the Kafka queue.");
            Assert.Contains("30 seconds", terms);
            Assert.Contains("Kafka", terms);
            Assert.DoesNotContain("Billing", terms);
        }
    }
}
