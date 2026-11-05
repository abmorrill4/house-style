using System.Collections.Generic;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Integration.Tests.Support;
using FlareAI.TestKit.Llm;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FlareAI.Integration.Tests.Review
{
    /// <summary>Ask AI as a conversation: answers change nothing, any proposed change can be applied, and the history goes with each turn.</summary>
    public class ConversationTests
    {
        private const string Topic = "Content/deposit/about.htm";

        private static string Reply(string kind, string answer, string runId = null, string text = null)
        {
            var changes = new JArray();
            if (runId != null) changes.Add(new JObject { ["run_id"] = runId, ["text"] = text });
            return new JObject
            {
                ["kind"] = kind, ["answer"] = answer, ["rule_ids"] = new JArray(), ["changes"] = changes, ["needs_restructure"] = false, ["note"] = ""
            }.ToString();
        }

        private static XmlNode FirstParagraph(ReviewFixture fx)
        {
            var body = BlockSelector.FindBody(fx.Doc.Document);
            return body.ChildNodes.Cast<XmlNode>().First(n => n.NodeType == XmlNodeType.Element && n.LocalName == "p");
        }

        private static System.Func<FlareAI.Core.Llm.LlmRequest, FlareAI.Core.Llm.LlmResponse> Reply_(string json) { return FlareAI.TestKit.Llm.ScriptedLlmClient.Reply.Text(json); }

        private static string FirstRunText(IList<XmlNode> blocks) { var m = RunMap.Of(blocks); return m.Texts[m.Ids[0]]; }

        [Fact]
        public void AnAnswer_ChangesNothing_AndTheNextTurnCarriesTheHistory()
        {
            var ai = new ScriptedLlmClient().ThenText(Reply("answer", "It names the product.")).ThenText(Reply("answer", "Yes, twice."));
            using (var fx = new ReviewFixture("DigitalBanking", Topic, ai))
            {
                var conv = new Conversation(new List<XmlNode> { FirstParagraph(fx) });
                var before = fx.Doc.Xml;
                var first = fx.Runner.Converse(fx.Doc, conv, "What does this say about the product?");
                Assert.Equal("answer", first.Kind);
                Assert.False(first.IsChange);
                Assert.Equal(before, fx.Doc.Xml);
                Assert.Throws<System.InvalidOperationException>(() => fx.Runner.ApplyTurn(fx.Doc, conv, first, "House Style: Ask AI"));

                var second = fx.Runner.Converse(fx.Doc, conv, "Does it say it twice?");
                Assert.Equal(2, conv.Turns.Count);
                var requests = ai.Requests.Where(r => r.Task == "converse").ToList();
                Assert.Equal(2, requests.Count);
                Assert.Null(requests[0].History == null || requests[0].History.Count == 0 ? null : "history on the first turn");
                Assert.Equal(2, requests[1].History.Count);                       // the first question and its reply
                Assert.Equal("user", requests[1].History[0].Role);
                Assert.Equal("assistant", requests[1].History[1].Role);
                Assert.DoesNotContain("⟦", requests[1].User);                      // the text isn't sent again while it is unchanged
                Assert.Equal(before, fx.Doc.Xml);
            }
        }

        [Fact]
        public void AChange_AppliesAsOneUndoStep_AndUndoRestoresTheTopic()
        {
            // The fake workspace allows one session at a time, so the reply is built once the topic is open.
            string original = null;
            var ai = new ScriptedLlmClient().Then(r => Reply_(Reply("change", "Adds a word.", "r1", original + " today"))(r));
            using (var fx = new ReviewFixture("DigitalBanking", Topic, ai))
            {
                original = FirstRunText(new List<XmlNode> { FirstParagraph(fx) }).TrimEnd();
                var conv = new Conversation(new List<XmlNode> { FirstParagraph(fx) });
                var turn = fx.Runner.Converse(fx.Doc, conv, "Add the word today.");
                Assert.True(turn.IsChange, turn.Answer);
                var before = fx.Doc.Xml;
                var undo = fx.Doc.UndoCount;
                fx.Runner.ApplyTurn(fx.Doc, conv, turn, "House Style: Ask AI");
                Assert.Equal(undo + 1, fx.Doc.UndoCount);
                Assert.True(turn.Applied);
                Assert.Empty(fx.Doc.UntrackedWrites);
                Assert.Contains("today", fx.Doc.Xml);
                fx.Doc.Undo();
                Assert.Equal(before, fx.Doc.Xml);
            }
        }

        [Fact]
        public void AnEarlierReply_CanStillBeApplied_AfterALaterOne()
        {
            string original = null;
            var ai = new ScriptedLlmClient()
                .Then(r => Reply_(Reply("change", "First.", "r1", original + " alpha"))(r))
                .Then(r => Reply_(Reply("change", "Second.", "r1", original + " beta"))(r));
            using (var fx = new ReviewFixture("DigitalBanking", Topic, ai))
            {
                original = FirstRunText(new List<XmlNode> { FirstParagraph(fx) }).TrimEnd();
                var conv = new Conversation(new List<XmlNode> { FirstParagraph(fx) });
                var one = fx.Runner.Converse(fx.Doc, conv, "Add alpha.");
                var two = fx.Runner.Converse(fx.Doc, conv, "Make it beta instead.");
                Assert.True(one.IsChange && two.IsChange);

                fx.Runner.ApplyTurn(fx.Doc, conv, two, "House Style: Ask AI");
                Assert.Contains("beta", fx.Doc.Xml);
                var undo = fx.Doc.UndoCount;
                // The text changed since reply one was written: its wording is planned again against the text as it is now.
                var said = fx.Runner.ApplyTurn(fx.Doc, conv, one, "House Style: Ask AI");
                Assert.Contains("text changed after this reply", said);
                Assert.Equal(undo + 1, fx.Doc.UndoCount);
                Assert.Empty(fx.Doc.UntrackedWrites);
                var accepted = AcceptedView.Of(new List<XmlNode> { FirstParagraph(fx) })[0].InnerText;
                Assert.Contains("alpha", accepted);
                Assert.DoesNotContain("beta", accepted);
            }
        }

        [Fact]
        public void IfTheTextIsGone_NothingIsSent_AndNothingIsApplied()
        {
            var ai = new ScriptedLlmClient().ThenText(Reply("answer", "Fine."));
            using (var fx = new ReviewFixture("DigitalBanking", Topic, ai))
            {
                var para = FirstParagraph(fx);
                var conv = new Conversation(new List<XmlNode> { para });
                para.ParentNode.RemoveChild(para);
                var ex = Assert.Throws<System.InvalidOperationException>(() => fx.Runner.Converse(fx.Doc, conv, "Hello?"));
                Assert.Contains("no longer in the topic", ex.Message);
                Assert.Empty(ai.Requests);
            }
        }
    }
}
