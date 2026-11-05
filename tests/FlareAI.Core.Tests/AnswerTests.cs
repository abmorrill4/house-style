using System;
using System.IO;
using FlareAI.Core.Actions;
using FlareAI.Core.Llm;
using Xunit;

namespace FlareAI.Core.Tests
{
    [CollectionDefinition("Answer usage", DisableParallelization = true)]
    public class AnswerUsageCollection { }

    /// <summary>Ask a question: the answer is text, and the topic is never written to.</summary>
    [Collection("Answer usage")]   // reads the session's Usage totals, which every other AI call writes to
    public class AnswerTests
    {
        private const string Reply = "{\"kind\":\"answer\",\"answer\":\"The second sentence is unclear.\",\"rule_ids\":[]}";

        private static ActionRunner Runner(FakeLlmClient llm, FakeStyleService style = null)
        {
            return style == null
                ? new ActionRunner(llm, new PromptBuilder(null), 1024)
                : new ActionRunner(llm, PromptBuilder.For(style, null), 1024, style);
        }

        private static FakeDocumentAdapter Selected()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[1]");
            return doc;
        }

        private static void AssertNoWrites(FakeDocumentAdapter doc)
        {
            Assert.Empty(doc.Replacements);
            Assert.Empty(doc.TextOperations);
        }

        [Fact]
        public void Answer_ReturnsTheText_AndWritesNothing()
        {
            var doc = Selected();
            var before = doc.Document.OuterXml;
            var llm = new FakeLlmClient(r => Reply);
            var runner = Runner(llm);

            var text = runner.Answer(doc, runner.PickBlocks(doc), "What's unclear here?");

            Assert.Equal("The second sentence is unclear.", text);
            Assert.Equal(before, doc.Document.OuterXml);
            AssertNoWrites(doc);
            Assert.Single(llm.Requests);
            Assert.Contains("What's unclear here?", llm.Requests[0].User);
            Assert.Equal("answer", llm.Requests[0].Task);
        }

        [Fact]
        public void Answer_ForAnEditRequest_SaysSoAndWritesNothing()
        {
            var doc = Selected();
            var before = doc.Document.OuterXml;
            var llm = new FakeLlmClient(r => "{\"kind\":\"change_request\",\"answer\":\"Here you go.\",\"rule_ids\":[]}");
            var runner = Runner(llm);

            var text = runner.Answer(doc, runner.PickBlocks(doc), "Shorten this.");

            Assert.Equal(Contracts.ChangeRequestReply, text);
            Assert.Equal(before, doc.Document.OuterXml);
            AssertNoWrites(doc);
        }

        [Fact]
        public void Answer_NamesKnownRules_AndDropsUnknownOnes()
        {
            var style = new FakeStyleService();
            var known = style.GuideRules[0].Id;
            var doc = Selected();
            var llm = new FakeLlmClient(r => "{\"kind\":\"answer\",\"answer\":\"Say administrators.\",\"rule_ids\":[\"" + known + "\",\"made-up/9\"]}");
            var runner = Runner(llm, style);

            var text = runner.Answer(doc, runner.PickBlocks(doc), "Does this match the guide?");

            Assert.Equal("Say administrators.\n\nRule: " + known, text);
            Assert.DoesNotContain("made-up/9", text);
            AssertNoWrites(doc);
        }

        [Fact]
        public void ReadAnswer_WithoutKnownRules_DropsEveryId_AndEmptyAnswerIsAChangeRequest()
        {
            var json = Newtonsoft.Json.Linq.JObject.Parse("{\"kind\":\"answer\",\"answer\":\" No rule applies. \",\"rule_ids\":[\"a/1\"]}");
            Assert.Equal("No rule applies.", Contracts.ReadAnswer(json, null));
            var empty = Newtonsoft.Json.Linq.JObject.Parse("{\"kind\":\"answer\",\"answer\":\"  \",\"rule_ids\":[]}");
            Assert.Equal(Contracts.ChangeRequestReply, Contracts.ReadAnswer(empty, null));
        }

        [Fact]
        public void Answer_WithNothingSelected_AppliesToTheWholeTopic()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var llm = new FakeLlmClient(r => Reply);
            var runner = Runner(llm);

            runner.Answer(doc, runner.PickBlocks(doc), "What's unclear here?");

            var user = llm.Requests[0].User;
            Assert.Contains("In order to be able", user);
            Assert.Contains("Navigate to", user);
            Assert.Contains("Advanced options", user);
        }

        [Fact]
        public void EmptyTopic_MakesNoCall()
        {
            var path = Path.Combine(Path.GetTempPath(), "hs-empty-" + Guid.NewGuid().ToString("N") + ".htm");
            File.WriteAllText(path, "<html><head /><body></body></html>");
            try
            {
                var doc = new FakeDocumentAdapter(path, true);
                var calls = 0;
                var llm = new FakeLlmClient(r => { calls++; return Reply; });
                var runner = Runner(llm);

                var ex = Assert.Throws<InvalidOperationException>(() => runner.Answer(doc, runner.PickBlocks(doc), "What's unclear here?"));

                Assert.Contains("Nothing to work on", ex.Message);
                Assert.Equal(0, calls);
                Assert.Empty(llm.Requests);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Answer_WhenTheCallFails_PropagatesAndLeavesTheTopicUnchanged()
        {
            var doc = Selected();
            var before = doc.Document.OuterXml;
            var llm = new FakeLlmClient(r => { throw new LlmException("Nothing answered.", 0); });
            var runner = Runner(llm);

            Assert.Throws<LlmException>(() => runner.Answer(doc, runner.PickBlocks(doc), "What's unclear here?"));

            Assert.Equal(before, doc.Document.OuterXml);
            AssertNoWrites(doc);
        }

        [Fact]
        public void Answer_CountsAsOneAiCall()
        {
            var doc = Selected();
            var runner = Runner(new FakeLlmClient(r => Reply));
            Usage.Reset();
            int before; double usd;
            Usage.Totals(out before, out usd);

            runner.Answer(doc, runner.PickBlocks(doc), "What's unclear here?");

            int after;
            Usage.Totals(out after, out usd);
            Assert.Equal(before + 1, after);
        }

        [Fact]
        public void BuildQuestion_CarriesTheQuestionAndTheText_AndLeavesNoPlaceholders()
        {
            var prompt = new PromptBuilder(null).BuildQuestion("<p>Click Save.</p>", "  Is this clear?  ");
            Assert.Contains("Is this clear?", prompt);
            Assert.Contains("<p>Click Save.</p>", prompt);
            Assert.DoesNotContain("{{", prompt);
            Assert.Contains("action-question.md", PromptBuilder.Templates);
        }
    }
}
