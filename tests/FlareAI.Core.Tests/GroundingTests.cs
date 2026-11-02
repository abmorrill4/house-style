using System.Collections.Generic;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class GroundingTests
    {
        [Fact]
        public void AiFindings_KeepRealCitations_DropMadeUpOnes()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var llm = new FakeLlmClient(r => @"[
              {""rule_id"":""company:terminology/1"",""rule"":""audience"",""severity"":""warning"",""excerpt"":""the user should simply ensure"",""replacement"":null,""why"":""Say administrators.""},
              {""rule_id"":""company:invented/9"",""rule"":""made up"",""severity"":""warning"",""excerpt"":""appropriate permissions"",""replacement"":null,""why"":""x""}
            ]");
            var style = new FakeStyleService();
            var runner = new ActionRunner(llm, new PromptBuilder(() => style.Guide(null)), 1024, style);
            var ai = runner.CheckStyle(doc).Where(f => f.Source == "ai").ToList();

            Assert.Contains("rule_id", llm.Requests[0].User);                    // the prompt asks for citations
            var cited = ai.Single(f => f.Rule == "audience");
            Assert.Equal("company:terminology/1", cited.RuleId);
            Assert.Equal("Audience", cited.RuleSection);
            Assert.Equal("file:///C:/guide/Terminology.htm", cited.Link);
            Assert.Null(ai.Single(f => f.Rule == "made up").RuleId);              // unknown id -> "general"
        }

        private static FakeStyleService ColonAndProductStyle()
        {
            var style = new FakeStyleService();
            style.GuideRules = new List<GuideRule>
            {
                new GuideRule { Id = "google:colons/1", Layer = "google", Page = "Colons", Section = "Colons", Text = "Don't capitalize the text before a colon.", Bad = "Note: the file", Good = "Note: The file" },
                new GuideRule { Id = "company:terminology/2", Layer = "company", Page = "Terminology", Section = "Audience", Text = "Insert the product name variable, not the product name as text.", Link = "file:///C:/guide/Terminology.htm" }
            };
            return style;
        }

        private static StyleFinding ReviewOne(FakeStyleService style, string ruleId, string why)
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var llm = new FakeLlmClient(r => @"[{""rule_id"":""" + ruleId + @""",""rule"":""x"",""severity"":""warning"",""excerpt"":""appropriate permissions"",""replacement"":null,""why"":""" + why + @"""}]");
            var runner = new ActionRunner(llm, new PromptBuilder(() => style.Guide(null)), 1024, style);
            return runner.CheckStyle(doc).Single(f => f.Source == "ai");
        }

        [Fact]
        public void Review_ReasonAboutOneRuleWithAnotherRuleId_ShowsOneConsistentRule()
        {
            var f = ReviewOne(ColonAndProductStyle(), "google:colons/1", "Use the product name variable.");
            Assert.NotEqual("google:colons/1", f.RuleId);
            Assert.Equal("company:terminology/2", f.RuleId);
            Assert.Equal("company", f.RuleLayer);
            Assert.Equal("Use the product name variable.", f.Suggestion);
        }

        [Fact]
        public void Review_ReasonThatMatchesNoRule_ShowsNoRule_AndKeepsTheReason()
        {
            var f = ReviewOne(ColonAndProductStyle(), "google:colons/1", "Rewrite for a calmer tone.");
            Assert.Null(f.RuleId);
            Assert.Null(f.RuleLayer);
            Assert.Equal("Rewrite for a calmer tone.", f.Suggestion);
        }

        [Fact]
        public void Review_AMadeUpRuleId_ShowsTheReasonWithNoRule()
        {
            var f = ReviewOne(ColonAndProductStyle(), "company:invented/9", "Use the product name variable.");
            Assert.Null(f.RuleId);
            Assert.Equal("Use the product name variable.", f.Suggestion);
        }

        [Fact]
        public void Review_ARuleTheWriterTurnedOff_IsNotChosenForTheReason()
        {
            var style = ColonAndProductStyle();
            style.Off.Add("company:terminology/2");
            var f = ReviewOne(style, "google:colons/1", "Use the product name variable.");
            Assert.Null(f.RuleId);
        }

        [Fact]
        public void Review_AReasonThatOnlyMentionsTheRulesWordsInPassing_ShowsNoRule()
        {
            var style = new FakeStyleService();
            style.GuideRules = new List<GuideRule>
            {
                new GuideRule { Id = "company:terminology/3", Layer = "company", Page = "Terminology", Section = "Audience", Text = "Call the bank the financial institution.", Bad = "the bank", Good = "the financial institution" }
            };
            const string why = "Say who can't restore it, for example administrators or the financial institution; author must confirm.";
            var f = ReviewOne(style, "company:terminology/3", why);
            Assert.Null(f.RuleId);
            Assert.Null(f.RuleLayer);
            Assert.Equal(why, f.Suggestion);
        }

        private static FakeStyleService ClaimsStyle()
        {
            var style = new FakeStyleService();
            style.GuideRules = new List<GuideRule>
            {
                new GuideRule { Id = "google:excessive-claims/5", Layer = "google", Page = "Excessive claims", Section = "Excessive claims", Text = "Don't make claims you can't back up." },
                new GuideRule { Id = "company:claims/1", Layer = "company", Page = "Claims", Section = "Claims", Text = "Back every claim with a source." }
            };
            return style;
        }

        [Fact]
        public void Review_ATeamRuleOnTheSamePointAsAGoogleRule_IsShownWhenTheReasonIsAboutIt()
        {
            var f = ReviewOne(ClaimsStyle(), "google:excessive-claims/5", "Unverifiable claim.");
            Assert.Equal("company:claims/1", f.RuleId);
            Assert.Equal("company", f.RuleLayer);
        }

        [Fact]
        public void Review_ATeamRuleOnTheSamePointAsAGoogleRule_IsNotShownWhenTheReasonIsAboutSomethingElse()
        {
            var f = ReviewOne(ClaimsStyle(), "google:excessive-claims/5", "Rewrite for a calmer tone.");
            Assert.Null(f.RuleId);
            Assert.Equal("Rewrite for a calmer tone.", f.Suggestion);
        }

        [Fact]
        public void VerifyExamples_PassesWhenBadIsCitedAndGoodIsClean()
        {
            var style = new FakeStyleService();
            var llm = new FakeLlmClient(r => r.User.Contains("The end user")
                ? @"[{""rule_id"":""company:terminology/1"",""rule"":""audience"",""severity"":""warning"",""excerpt"":""The end user"",""replacement"":""Administrators"",""why"":""x""}]"
                : "[]");
            var runner = new ActionRunner(llm, new PromptBuilder(() => style.Guide(null)), 1024, style);
            var results = runner.VerifyExamples(new List<GuideExample>
            {
                new GuideExample { Id = "company:terminology/1", Bad = "The end user approves the request.", Good = "Administrators approve the request." },
                new GuideExample { Id = "company:terminology/1", Bad = "Someone approves it.", Good = "" }
            });
            Assert.True(results[0].Passed);
            Assert.False(results[1].BadCaught);
            Assert.Null(results[1].GoodClean);
            Assert.Equal(4 - 1, llm.Requests.Count);
        }
    }
}
