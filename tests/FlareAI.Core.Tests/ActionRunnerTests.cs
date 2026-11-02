using System;
using FlareAI.Core.Actions;
using FlareAI.Core.Llm;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class ActionRunnerTests
    {
        private static ActionRunner Runner(FakeLlmClient llm) { return new ActionRunner(llm, new PromptBuilder(null), 1024); }

        [Fact]
        public void Rewrite_WithEchoModel_IsNoOpAndValid()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[1]/text()[1]");
            var llm = new FakeLlmClient(FakeLlmClient.EchoRevised);
            var proposal = Runner(llm).Rewrite(doc, null);

            Assert.True(proposal.Validation.IsValid, proposal.Validation.ToString());
            Assert.Single(proposal.ProposedBlocks);
            Assert.Equal(proposal.OriginalBlocks[0].OuterXml, proposal.ProposedBlocks[0].OuterXml);
            Assert.Contains("⟦r1⟧", llm.Requests[0].User);   // runs are addressed, never markup
            Assert.Contains("MadCap:", llm.Requests[0].System);
        }

        [Fact]
        public void Rewrite_ThenApply_IsWordLevelInOneOperation()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            var llm = new FakeLlmClient(r => "<revised><p MadCap:conditions=\"Default.Print\">Print only.</p></revised>");
            var runner = Runner(llm);
            var proposal = runner.Rewrite(doc, null);
            Assert.True(proposal.Validation.IsValid, proposal.Validation.ToString());

            Assert.Contains("word-level", runner.Apply(doc, proposal, "AI rewrite"));
            Assert.Empty(doc.Replacements);          // same structure: no block replacement
            Assert.Single(doc.TextOperations);       // one undo step
            Assert.Equal("Print only.", doc.Document.SelectSingleNode("//body/p[2]").InnerText);
        }

        [Fact]
        public void Rewrite_ModelReturnsProse_FailsValidationInsteadOfThrowing()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            var llm = new FakeLlmClient(r => "Sure! Here is a better version of your paragraph.");
            // Prose instead of the contract: a typed failure the UI can explain, never a half-parsed edit.
            var ex = Assert.Throws<LlmException>(() => Runner(llm).Rewrite(doc, null));
            Assert.Equal(LlmFailure.Contract, ex.Failure);
        }

        [Fact]
        public void Rewrite_OnSnippetBlock_Refuses()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//MadCap:snippetBlock");
            var llm = new FakeLlmClient(FakeLlmClient.EchoRevised);
            Assert.Throws<InvalidOperationException>(() => Runner(llm).Rewrite(doc, null));
        }

        [Fact]
        public void Ask_PutsInstructionInPrompt()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            var llm = new FakeLlmClient(FakeLlmClient.EchoRevised);
            Runner(llm).Rewrite(doc, "make it a bulleted list");
            Assert.Contains("make it a bulleted list", llm.Requests[0].User);
        }

        private const string PlainAnswer = "{\"answer\":\"The second sentence is long.\",\"is_edit_request\":false,\"rule_ids\":[]}";

        [Fact]
        public void Answer_ChangesNothing()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            var llm = new FakeLlmClient(r => PlainAnswer);
            var runner = Runner(llm);
            var before = doc.Document.OuterXml;

            var result = runner.Answer(doc, runner.PickBlocks(doc), "What is unclear?");

            Assert.Equal(before, doc.Document.OuterXml);
            Assert.Empty(doc.Replacements);
            Assert.Empty(doc.TextOperations);
            Assert.False(result.IsEditRequest);
            Assert.Contains("The second sentence is long.", result.Text);
            Assert.Contains("No style rule applies.", result.Text);
            Assert.Single(llm.Requests);
            Assert.Equal("answer", llm.Requests[0].Task);
            Assert.NotNull(llm.Requests[0].Schema);
            Assert.Contains("What is unclear?", llm.Requests[0].User);
        }

        [Fact]
        public void Answer_EditRequest_ReturnsFixedCopyAndChangesNothing()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            var llm = new FakeLlmClient(r => "{\"answer\":\"Here is a rewrite\",\"is_edit_request\":true,\"rule_ids\":[]}");
            var runner = Runner(llm);
            var before = doc.Document.OuterXml;

            var result = runner.Answer(doc, runner.PickBlocks(doc), "Rewrite this");

            Assert.Equal(ActionRunner.EditRequestAnswer, result.Text);
            Assert.True(result.IsEditRequest);
            Assert.Equal(before, doc.Document.OuterXml);
            Assert.Empty(doc.Replacements);
            Assert.Empty(doc.TextOperations);
        }

        [Fact]
        public void Answer_NamesTheRule()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            var llm = new FakeLlmClient(r => "{\"answer\":\"Say administrators [company:terminology/1].\",\"is_edit_request\":false,\"rule_ids\":[\"company:terminology/1\",\"made-up\"]}");
            var runner = new ActionRunner(llm, new PromptBuilder(null), 1024, new FakeStyleService());

            var result = runner.Answer(doc, runner.PickBlocks(doc), "Does this match the glossary?");

            Assert.Single(result.Rules);
            Assert.Contains("company:terminology/1", result.Rules[0]);
            Assert.Contains("Rule:", result.Text);
            Assert.DoesNotContain("made-up", result.Text);
            Assert.DoesNotContain("No style rule applies.", result.Text);
        }

        [Fact]
        public void Answer_EmptyTopic_NoCall()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.Document.SelectSingleNode("//body").RemoveAll();
            var llm = new FakeLlmClient(r => PlainAnswer);
            var runner = Runner(llm);

            var ex = Assert.Throws<InvalidOperationException>(() => runner.Answer(doc, runner.PickBlocks(doc), "What is unclear?"));

            Assert.Contains("Nothing to work on", ex.Message);
            Assert.Empty(llm.Requests);
        }

        [Fact]
        public void CheckStyle_ParsesFindings()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var llm = new FakeLlmClient(r => "```json\n[{\"rule\":\"filler\",\"severity\":\"warning\",\"excerpt\":\"simply\",\"suggestion\":\"Remove it.\"}]\n```");
            var findings = Runner(llm).CheckStyle(doc);
            Assert.Single(findings);
            Assert.Equal("filler", findings[0].Rule);
        }

        [Fact]
        public void CheckStyle_NoFindings_ReturnsEmpty()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var llm = new FakeLlmClient(r => "[]");
            Assert.Empty(Runner(llm).CheckStyle(doc));
        }

        [Fact]
        public void AnthropicBody_HasSystemAndUser()
        {
            var body = AnthropicClient.BuildBody("m", new LlmRequest { System = "S", User = "U", MaxTokens = 10 });
            Assert.Contains("\"system\":[{\"type\":\"text\",\"text\":\"S\",\"cache_control\":{\"type\":\"ephemeral\",\"ttl\":\"1h\"}}]", body);
            // Packs: their own 5-minute cache block; word rules: last, uncached.
            var three = AnthropicClient.BuildBody("m", new LlmRequest { System = "S", SystemPacks = "P", SystemTerms = "T", User = "U" });
            Assert.Contains("{\"type\":\"text\",\"text\":\"P\",\"cache_control\":{\"type\":\"ephemeral\"}},{\"type\":\"text\",\"text\":\"T\"}]", three);
            Assert.Contains("\"role\":\"user\"", body);
            Assert.Contains("\"max_tokens\":10", body);
        }

        [Fact]
        public void AnthropicParse_ConcatenatesTextBlocks()
        {
            var r = AnthropicClient.Parse("{\"model\":\"m\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}],\"usage\":{\"input_tokens\":3,\"output_tokens\":4}}");
            Assert.Equal("ab", r.Text);
            Assert.Equal(3, r.InputTokens);
        }

        [Fact]
        public void CheckStyle_OmitsPendingDeletions()
        {
            // Saved by Flare 22 after an applied AI rewrite: old text is a RemoveChange, new text an AddChange.
            var doc = new FakeDocumentAdapter("tracked-changes-topic.htm");
            doc.SelectXPath("//body/p[1]");
            var llm = new FakeLlmClient(r => "");
            Runner(llm).CheckStyle(doc);
            Assert.Contains("To use SSO", llm.Requests[0].User);
            Assert.DoesNotContain("In order to be able to utilize", llm.Requests[0].User);
            Assert.Contains("In order to be able to utilize", doc.Document.OuterXml); // document untouched
        }


        [Fact]
        public void CheckStyle_CaretOnly_ChecksWholeTopic()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            doc.SelectionText = ""; // caret, nothing selected
            var llm = new FakeLlmClient(r => "");
            Runner(llm).CheckStyle(doc);
            // Per-block review: every block of the topic is sent, each with the topic title as context.
            Assert.All(llm.Requests, q => Assert.Contains("Configure single sign-on", q.User));
            Assert.Contains(llm.Requests, q => q.User.Contains("Whitelist the IdP"));
        }

        [Fact]
        public void CheckStyle_WithSelectedText_ChecksOnlyThoseBlocks()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            doc.SelectionText = "only in print";
            var llm = new FakeLlmClient(r => "");
            Runner(llm).CheckStyle(doc);
            Assert.DoesNotContain("Whitelist the IdP", llm.Requests[0].User);
        }
    }
}
