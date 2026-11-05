using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Integration.Tests.Support;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Integration.Tests.Review
{
    /// <summary>Insert at the cursor: a draft goes in after the paragraph the caret is in, as one tracked change that undoes and redoes whole.</summary>
    public class InsertAtCursorTests
    {
        private const string Topic = "Content/deposit/about.htm";

        private static string Reply(string revised, string note = "")
        {
            return new Newtonsoft.Json.Linq.JObject
            {
                ["findings"] = new Newtonsoft.Json.Linq.JArray(), ["changes"] = new Newtonsoft.Json.Linq.JArray(),
                ["needs_restructure"] = false, ["note"] = note, ["revised"] = revised
            }.ToString();
        }

        private static XmlNode FirstParagraph(ReviewFixture fx)
        {
            var body = BlockSelector.FindBody(fx.Doc.Document);
            return body.ChildNodes.Cast<XmlNode>().First(n => n.NodeType == XmlNodeType.Element && n.LocalName == "p");
        }

        [Fact]
        public void ADraft_GoesInAfterTheAnchor_AsOneUndoStep_AndRedoesWhole()
        {
            var ai = new ScriptedLlmClient().WhenTask("compose", Reply("<p>A new paragraph for the writer.</p><ul><li>First</li><li>Second</li></ul>"))
                                            .WhenTask("review", "{\"findings\":[]}");
            using (var fx = new ReviewFixture("DigitalBanking", Topic, ai))
            {
                var anchor = FirstParagraph(fx);
                var proposal = fx.Runner.Draft(fx.Doc, anchor, "Explain the new thing.");
                Assert.True(proposal.Validation.IsValid, proposal.Validation.ToString());
                Assert.Equal(2, proposal.ProposedBlocks.Count);

                var before = fx.Doc.Xml;
                var undoBefore = fx.Doc.UndoCount;
                fx.Runner.ApplyInsert(fx.Doc, proposal, "Insert at the Cursor");
                Assert.Equal(undoBefore + 1, fx.Doc.UndoCount);   // one Ctrl+Z
                Assert.Empty(fx.Doc.UntrackedWrites);
                var next = anchor.NextSibling;
                while (next != null && next.NodeType != XmlNodeType.Element) next = next.NextSibling;
                Assert.Contains("A new paragraph for the writer.", next.OuterXml);

                var inserted = fx.Doc.Xml;
                fx.Doc.Undo();
                Assert.Equal(before, fx.Doc.Xml);             // Undo restores the exact original
                fx.Doc.Redo();
                Assert.Equal(inserted, fx.Doc.Xml);           // Redo restores the draft
            }
        }

        [Theory]
        [InlineData("<p>Use <MadCap:variable name=\"General.ProductName\" /> here.</p>")]
        [InlineData("<p>See <a href=\"https://example.com\">this</a>.</p>")]
        [InlineData("<table><tr><td>No</td></tr></table>")]
        [InlineData("<h2>A heading</h2>")]
        [InlineData("<p class=\"note\">Styled.</p>")]
        public void ADraftWithMarkupWeDoNotInsert_IsRefused_AndChangesNothing(string revised)
        {
            var ai = new ScriptedLlmClient().WhenTask("compose", Reply(revised)).WhenTask("review", "{\"findings\":[]}");
            using (var fx = new ReviewFixture("DigitalBanking", Topic, ai))
            {
                var proposal = fx.Runner.Draft(fx.Doc, FirstParagraph(fx), "Write something.");
                Assert.False(proposal.Validation.IsValid);
                var undoBefore = fx.Doc.UndoCount;
                Assert.Throws<System.InvalidOperationException>(() => fx.Runner.ApplyInsert(fx.Doc, proposal, "Insert at the Cursor"));
                Assert.Equal(undoBefore, fx.Doc.UndoCount);
            }
        }

        [Fact]
        public void AGap_ShowsAsVisibleText_AndIsCounted()
        {
            var ai = new ScriptedLlmClient().WhenTask("compose", Reply("<p>Set the limit to {{GAP}} before you start.</p>", "The limit isn't in the topic."))
                                            .WhenTask("review", "{\"findings\":[]}");
            using (var fx = new ReviewFixture("DigitalBanking", Topic, ai))
            {
                var proposal = fx.Runner.Draft(fx.Doc, FirstParagraph(fx), "Say what the limit is.");
                Assert.Equal(1, proposal.Gaps);
                Assert.Contains("Add details here.", proposal.ProposedXml);
                Assert.DoesNotContain("{{GAP}}", proposal.ProposedXml);
                Assert.Equal("The limit isn't in the topic.", proposal.Missing);
            }
        }

        [Fact]
        public void IfTheAnchorIsGone_NothingIsInserted()
        {
            var ai = new ScriptedLlmClient().WhenTask("compose", Reply("<p>New.</p>")).WhenTask("review", "{\"findings\":[]}");
            using (var fx = new ReviewFixture("DigitalBanking", Topic, ai))
            {
                var anchor = FirstParagraph(fx);
                var proposal = fx.Runner.Draft(fx.Doc, anchor, "Write something.");
                anchor.ParentNode.RemoveChild(anchor);
                var undoBefore = fx.Doc.UndoCount;
                Assert.Throws<System.InvalidOperationException>(() => fx.Runner.ApplyInsert(fx.Doc, proposal, "Insert at the Cursor"));
                Assert.Equal(undoBefore, fx.Doc.UndoCount);
            }
        }
    }
}
