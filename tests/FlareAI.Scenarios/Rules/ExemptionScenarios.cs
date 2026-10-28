using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using FlareAI.Scenarios.Support;
using Xunit;

namespace FlareAI.Scenarios.Rules
{
    /// <summary>
    /// Exempting one place without turning the rule off: the exempt text is left out of Lint Topic, Lint Project, the live check
    /// and a restart, while the same rule on other text, and other rules on the same text, still show.
    /// </summary>
    public class ExemptionScenarios
    {
        private const string EMail = "google:word-list/251";   // e-mail → email
        private const string Body = "<p>Send the e-mail now.</p><p>Read the e-mail later.</p><p>Please utilize the tool.</p>";

        private static string Root(ReviewFixture fx) { return fx.Ws.ProjectDir; }

        private static List<StyleFinding> Lint(ReviewFixture fx)
        {
            return fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc));
        }

        private static void Exempt(ReviewFixture fx, StyleFinding f)
        {
            Exemptions.Add(Root(fx), fx.Doc.SourcePath, f, "Intended here.", "pat@example.com", new DateTime(2026, 10, 28));
        }

        private static int Count(ReviewSession s, string ruleId) { return s.Findings.Count(f => f.RuleId == ruleId); }

        /// <summary>Any finding of another rule than e-mail (the body has more than one kind of issue).</summary>
        private static bool IsOther(StyleFinding f) { return Exemptions.RuleKey(f) != EMail; }

        [Fact]
        public void ExemptedText_IsGoneFromLintTopicLintProjectAndARestart()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                var first = Lint(fx).First(f => f.RuleId == EMail);
                Exempt(fx, first);

                fx.LintOnlyCheck();
                Assert.Equal(1, Count(fx.Session, EMail));
                Assert.Equal(1, fx.Session.ExemptHidden);

                var project = fx.NewSession(true);
                var all = Lint(fx);
                foreach (var f in all) f.DocumentPath = fx.Doc.SourcePath;
                project.Add(all);
                Assert.Equal(1, Count(project, EMail));

                fx.Restart();
                fx.LintOnlyCheck();
                Assert.Equal(1, Count(fx.Session, EMail));
                Assert.Equal(1, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_OtherRuleSameText_StillShows()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                var first = Lint(fx).First(f => f.RuleId == EMail);
                first.RuleId = "google:other/1";
                Exempt(fx, first);

                fx.LintOnlyCheck();

                Assert.Equal(2, Count(fx.Session, EMail));
                Assert.Equal(0, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_SameRuleOtherText_StillShows()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                var other = Lint(fx).First(IsOther);
                Exempt(fx, other);

                fx.LintOnlyCheck();

                Assert.DoesNotContain(fx.Session.Findings, f => IsOther(f) && f.Excerpt == other.Excerpt && f.Before == other.Before);
                Assert.Equal(2, Count(fx.Session, EMail));
            }
        }

        [Fact]
        public void ExemptedText_SameWordTwiceInOneTopic_OneCopyStillShows()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, Lint(fx).First(f => f.RuleId == EMail));

                fx.LintOnlyCheck();

                Assert.Equal(1, Count(fx.Session, EMail));
                Assert.Equal(1, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_PathCasingDiffersBetweenLintTopicAndLintProject_StillHidden()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, Lint(fx).First(f => f.RuleId == EMail));

                var project = fx.NewSession(true);
                var all = Lint(fx);
                foreach (var f in all) f.DocumentPath = fx.Doc.SourcePath.ToUpperInvariant();
                project.Add(all);

                Assert.Equal(1, Count(project, EMail));
            }
        }

        [Fact]
        public void ExemptedText_IsHiddenDuringTheLiveCheckAsTheWriterTypes()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, Lint(fx).First(f => f.RuleId == EMail));
                fx.LintOnlyCheck();

                fx.Session.RefreshLint(Lint(fx));

                Assert.Equal(1, Count(fx.Session, EMail));
            }
        }

        [Fact]
        public void ATeammatesCopyOfTheProject_HonorsTheExemption()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                var f = Lint(fx).First(x => x.RuleId == EMail);
                var line = string.Join("\t", new[] { Exemptions.TopicFor(fx.Doc.SourcePath), f.RuleId, f.Excerpt, f.Before ?? "", "Their reason.", "lee@example.com", "2026-10-01" });
                fx.Ws.WriteFile(@"Project\HouseStyle\Exemptions.txt", line + "\r\n");

                fx.LintOnlyCheck();

                Assert.Equal(1, Count(fx.Session, EMail));
                Assert.Equal(1, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void AStatusLine_SaysHowManyIssuesAreExempt()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                fx.LintOnlyCheck();
                var s = fx.Session;
                s.Select(s.Findings.First(IsOther));
                s.Reject("Not needed.");
                Assert.DoesNotContain("exempt", s.Status);
            }
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, Lint(fx).First(f => f.RuleId == EMail));
                fx.LintOnlyCheck();
                var s = fx.Session;
                s.Select(s.Findings.First(IsOther));
                s.Reject("Not needed.");
                Assert.EndsWith(" 1 issue is exempt.", s.Status);
            }
            using (var fx = ReviewFixture.Scratch(Body))
            {
                foreach (var f in Lint(fx).Where(x => x.RuleId == EMail)) Exempt(fx, f);
                fx.LintOnlyCheck();
                var s = fx.Session;
                s.Select(s.Findings.First(IsOther));
                s.Reject("Not needed.");
                Assert.EndsWith(" 2 issues are exempt.", s.Status);
            }
        }

        [Fact]
        public void RefreshLintTwice_KeepsTheExemptCountAtOne()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, Lint(fx).First(f => f.RuleId == EMail));
                fx.LintOnlyCheck();

                fx.Session.RefreshLint(Lint(fx));
                fx.Session.RefreshLint(Lint(fx));

                Assert.Equal(1, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void Exempt_WithAReason_HidesTheIssueAtOnceAndWritesTheLine()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                fx.LintOnlyCheck();
                var s = fx.Session;
                var f = s.Findings.First(x => x.RuleId == EMail);
                fx.Host.AskTextAnswer = "  Brand name.  ";

                s.Exempt(f);

                Assert.Equal(1, Count(s, EMail));
                Assert.Equal(1, s.ExemptHidden);
                Assert.EndsWith(" 1 issue is exempt.", s.Status);
                var entry = Exemptions.Current(Root(fx)).Entries.Single();
                Assert.Equal("Brand name.", entry.Reason);
                Assert.Equal(f.Excerpt, entry.Text);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Exempt_WithAnEmptyReason_WritesNothing(string answer)
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                fx.LintOnlyCheck();
                var s = fx.Session;
                fx.Host.AskTextAnswer = answer;

                s.Exempt(s.Findings.First(x => x.RuleId == EMail));

                Assert.False(File.Exists(Exemptions.FileFor(Root(fx))));
                Assert.Equal(2, Count(s, EMail));
                Assert.Equal(0, s.ExemptHidden);
            }
        }

        [Fact]
        public void RemovedExemption_ShowsTheIssueAgain()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, Lint(fx).First(f => f.RuleId == EMail));
                fx.LintOnlyCheck();
                Assert.Equal(1, fx.Session.ExemptHidden);

                Exemptions.Remove(Root(fx), Exemptions.Current(Root(fx)).Entries.Single());

                fx.Restart();
                fx.LintOnlyCheck();
                Assert.Equal(2, Count(fx.Session, EMail));
                Assert.Equal(0, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void AGuideRuleFinding_OffersExempt()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Assert.True(fx.Session.CanExempt(Lint(fx).First(f => f.RuleId == EMail)));
            }
        }

        [Fact]
        public void AnAiFindingWithNoRuleId_DoesNotOfferExempt()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                var ai = new StyleFinding { Source = "ai", Layer = "ai", Rule = "Clarity", Excerpt = "e-mail", Before = "Send the " };

                Assert.False(fx.Session.CanExempt(ai));
            }
        }
    }
}
