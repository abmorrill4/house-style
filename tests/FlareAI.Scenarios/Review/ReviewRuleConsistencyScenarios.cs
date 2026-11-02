using System;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using FlareAI.Scenarios.Support;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Scenarios.Review
{
    /// <summary>An AI issue's reason, rule and example describe the same rule (#345).</summary>
    public class ReviewRuleConsistencyScenarios
    {
        private const string Body = "<p>Our best-in-class transfer experience delights members.</p>";

        private static string Reply(string ruleId, string why)
        {
            return "{\"findings\":[{\"run_id\":\"r1\",\"excerpt\":\"best-in-class\",\"rule_id\":\"" + ruleId + "\",\"rule\":\"marketing claim\",\"severity\":\"warning\",\"replacement\":null,\"why\":\"" + why + "\"}]}";
        }

        [Fact]
        public void Check_AGoogleRuleAndATeamRuleOnTheSamePoint_NamesTheTeamRule()
        {
            var llm = new ScriptedLlmClient().WhenTask("review", Reply("google:excessive-claims/5", "Unverifiable claim."));
            using (var fx = ReviewFixture.Scratch(Body, llm))
            {
                fx.Check();
                var f = fx.Session.Findings.Single(x => x.Source == "ai" && x.Excerpt == "best-in-class");
                Assert.Equal("company:claims/1", f.RuleId);
                Assert.Equal("company", f.RuleLayer);
                Assert.Equal("Unverifiable claim.", f.Suggestion);
            }
        }

        [Fact]
        public void Check_AReasonAboutADifferentRule_IsNeverShownWithTheCitedRule()
        {
            var llm = new ScriptedLlmClient().WhenTask("review", Reply("google:excessive-claims/5", "Capitalize the heading after the colon."));
            using (var fx = ReviewFixture.Scratch(Body, llm))
            {
                fx.Check();
                var f = fx.Session.Findings.Single(x => x.Source == "ai" && x.Excerpt == "best-in-class");
                Assert.NotEqual("google:excessive-claims/5", f.RuleId);
                Assert.Equal("Capitalize the heading after the colon.", f.Suggestion);
                if (f.RuleId != null)
                {
                    var rule = fx.Style.Rules(null).Single(r => r.Id == f.RuleId);
                    var words = (rule.Page + " " + rule.Section + " " + rule.Text + " " + rule.Bad + " " + rule.Good).ToLowerInvariant();
                    Assert.True(words.Contains("capitaliz") || words.Contains("heading") || words.Contains("after") || words.Contains("colon"), "the rule shown is about the reason");
                }
                else Assert.Null(f.RuleLayer);
            }
        }
    }
}
