using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using FlareAI.Scenarios.Support;
using Xunit;

namespace FlareAI.Scenarios.Rules
{
    /// <summary>Exempting one place from a rule: it is hidden for that text only, on that topic, for everyone on the project.</summary>
    public class ExemptionScenarios
    {
        private const string EMail = "google:word-list/251";          // e-mail → email
        private const string OneEMail = "<h1>Tips</h1><p>Send an e-mail to the team.</p>";
        private const string TwoEMails = "<h1>Tips</h1><p>Send an e-mail to the team.</p><p>Read the e-mail from the lead.</p>";

        private static string TopicOf(ReviewFixture fx, StyleFinding f) { return Exemptions.TopicKey(f, fx.Doc.SourcePath); }

        private static void Exempt(ReviewFixture fx, StyleFinding f, string rule = null, string text = null)
        {
            Exemptions.Add(fx.Doc.SourcePath, TopicOf(fx, f), rule ?? Exemptions.RuleKey(f), text ?? f.Excerpt, f.Before ?? "", "Fine here.", "pat@example.com");
        }

        private static int Count(ReviewFixture fx) { return fx.Session.Findings.Count(f => f.RuleId == EMail); }

        [Fact]
        public void ExemptedText_IsGoneFromLintTopicLintProjectAndARestart()
        {
            using (var fx = ReviewFixture.Scratch(OneEMail))
            {
                fx.Check();
                var f = fx.Find(EMail);
                Exempt(fx, f);

                fx.Check();
                Assert.Equal(0, Count(fx));
                Assert.Equal(1, fx.Session.ExemptHidden);

                var project = fx.NewSession(true);
                fx.Check(project);
                Assert.DoesNotContain(project.Findings, x => x.RuleId == EMail);

                fx.Restart();
                fx.Check();
                Assert.Equal(0, Count(fx));
            }
        }

        [Fact]
        public void ExemptedText_OtherRuleSameText_StillShows()
        {
            using (var fx = ReviewFixture.Scratch(OneEMail))
            {
                fx.Check();
                Exempt(fx, fx.Find(EMail), "google:word-list/1");
                fx.Check();
                Assert.Equal(1, Count(fx));
                Assert.Equal(0, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_SameRuleOtherText_StillShows()
        {
            using (var fx = ReviewFixture.Scratch(OneEMail))
            {
                fx.Check();
                Exempt(fx, fx.Find(EMail), null, "some other words");
                fx.Check();
                Assert.Equal(1, Count(fx));
            }
        }

        [Fact]
        public void ExemptedText_SameWordTwiceInOneTopic_OneCopyStillShows()
        {
            using (var fx = ReviewFixture.Scratch(TwoEMails))
            {
                fx.Check();
                Assert.Equal(2, Count(fx));
                Exempt(fx, fx.Find(EMail, "e-mail"));
                fx.Check();
                Assert.Equal(1, Count(fx));
                Assert.Equal(1, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_PathCasingDiffersBetweenLintTopicAndLintProject_StillHidden()
        {
            using (var fx = ReviewFixture.Scratch(OneEMail))
            {
                fx.Check();
                var f = fx.Find(EMail);
                Exempt(fx, f);

                var project = fx.NewSession(true);
                var again = fx.Runner.CheckStyle(fx.Doc).First(x => x.RuleId == EMail);
                again.DocumentPath = fx.Doc.SourcePath.ToUpperInvariant();
                project.Add(new[] { again });

                Assert.DoesNotContain(project.Findings, x => x.RuleId == EMail);
                Assert.Equal(1, project.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_IsHiddenDuringTheLiveCheckAsTheWriterTypes()
        {
            using (var fx = ReviewFixture.Scratch(OneEMail))
            {
                fx.Check();
                Exempt(fx, fx.Find(EMail));

                fx.Session.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)));

                Assert.Equal(0, Count(fx));
            }
        }

        [Fact]
        public void ATeammatesCopyOfTheProject_HonorsTheExemption()
        {
            string text;
            using (var fx = ReviewFixture.Scratch(OneEMail))
            {
                fx.Check();
                Exempt(fx, fx.Find(EMail));
                text = File.ReadAllText(Exemptions.FileFor(fx.Doc.SourcePath));
            }
            using (var mate = ReviewFixture.Scratch(OneEMail))
            {
                var file = Exemptions.FileFor(mate.Doc.SourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, text);

                mate.Check();

                Assert.Equal(0, Count(mate));
                Assert.Equal(1, mate.Session.ExemptHidden);
            }
        }

        [Fact]
        public void RemovedExemption_ShowsTheIssueAgain()
        {
            using (var fx = ReviewFixture.Scratch(OneEMail))
            {
                fx.Check();
                Exempt(fx, fx.Find(EMail));
                fx.Check();
                Assert.Equal(0, Count(fx));

                Exemptions.Remove(fx.Doc.SourcePath, Exemptions.Load(fx.Doc.SourcePath).Single());
                File.SetLastWriteTimeUtc(Exemptions.FileFor(fx.Doc.SourcePath), System.DateTime.UtcNow.AddMinutes(1));
                fx.Check();

                Assert.Equal(1, Count(fx));
                Assert.Equal(0, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void AStatusLine_SaysHowManyIssuesAreExempt()
        {
            using (var fx = ReviewFixture.Scratch(TwoEMails))
            {
                fx.Check();
                Assert.Equal("", fx.Session.ExemptTail);
                Exempt(fx, fx.Find(EMail, "e-mail"));
                fx.Check();
                Assert.Equal("1 issue is exempt.", fx.Session.ExemptTail);
                foreach (var f in fx.Session.Findings.Where(x => x.RuleId == EMail).ToList()) Exempt(fx, f);
                fx.Check();
                Assert.Equal("2 issues are exempt.", fx.Session.ExemptTail);
            }
        }

        [Fact]
        public void RefreshLintTwice_KeepsTheExemptCountAtOne()
        {
            using (var fx = ReviewFixture.Scratch(OneEMail))
            {
                fx.Check();
                Exempt(fx, fx.Find(EMail));
                fx.Check();

                fx.Session.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)));
                fx.Session.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)));

                Assert.Equal(1, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void AnAiFindingWithNoRuleId_DoesNotOfferExempt()
        {
            using (var fx = ReviewFixture.Scratch(OneEMail))
            {
                var ai = new StyleFinding { Source = "ai", Layer = "ai", Rule = "Tone", Excerpt = "e-mail", Before = "Send an " };
                Assert.False(fx.Session.CanExempt(ai));
                fx.Check();
                Assert.True(fx.Session.CanExempt(fx.Find(EMail)));
            }
        }
    }
}
