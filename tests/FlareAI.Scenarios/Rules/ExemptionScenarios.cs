using System;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using FlareAI.Scenarios.Support;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Rules
{
    /// <summary>
    /// Exempting one place without turning a rule off: the text stays hidden for that rule in that topic, for every check,
    /// every teammate and every restart, and nothing else is hidden.
    /// </summary>
    public class ExemptionScenarios
    {
        private const string EMail = "google:word-list/251";   // e-mail → email
        private const string Topic = "Content/topic.htm";
        private const string Once = "<p>Send an e-mail to the team.</p>";
        private const string Twice = "<p>Send an e-mail to the team.</p><p>Then read the e-mail we sent back.</p>";

        private static int EMailCount(ReviewSession s) { return s.Findings.Count(f => f.RuleId == EMail); }

        private static void Exempt(ReviewFixture fx, StyleFinding f, string rule = null, string text = null, string topic = "topic.htm")
        {
            Exemptions.Add(fx.Doc.SourcePath, topic, rule ?? f.RuleId, text ?? f.Excerpt, f.Before ?? "", "Intended here.", "pat@example.com", DateTime.UtcNow);
        }

        private static StyleFinding FirstEMail(ReviewFixture fx)
        {
            fx.LintOnlyCheck();
            return fx.Session.Findings.First(f => f.RuleId == EMail);
        }

        [Fact]
        public void ExemptedText_IsGoneFromLintTopicLintProjectAndARestart()
        {
            using (var fx = ReviewFixture.Scratch(Once))
            {
                var f = FirstEMail(fx);
                Exempt(fx, f);

                fx.LintOnlyCheck();
                Assert.Equal(0, EMailCount(fx.Session));

                var project = fx.NewSession(true);
                fx.LintOnlyCheck(project);
                Assert.Equal(0, EMailCount(project));

                fx.Restart();
                fx.LintOnlyCheck();
                Assert.Equal(0, EMailCount(fx.Session));
                Assert.Equal(1, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_OtherRuleSameText_StillShows()
        {
            using (var fx = ReviewFixture.Scratch(Once))
            {
                Exempt(fx, FirstEMail(fx), rule: "google:word-list/999999");
                fx.LintOnlyCheck();
                Assert.Equal(1, EMailCount(fx.Session));
                Assert.Equal(0, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_SameRuleOtherText_StillShows()
        {
            using (var fx = ReviewFixture.Scratch(Once))
            {
                var f = FirstEMail(fx);
                Exempt(fx, f, text: f.Excerpt + " elsewhere");
                fx.LintOnlyCheck();
                Assert.Equal(1, EMailCount(fx.Session));
            }
        }

        [Fact]
        public void ExemptedText_SameWordTwiceInOneTopic_OneCopyStillShows()
        {
            using (var fx = ReviewFixture.Scratch(Twice))
            {
                fx.LintOnlyCheck();
                Assert.Equal(2, EMailCount(fx.Session));
                Exempt(fx, fx.Session.Findings.First(f => f.RuleId == EMail));
                fx.LintOnlyCheck();
                Assert.Equal(1, EMailCount(fx.Session));
                Assert.Equal(1, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_PathCasingDiffersBetweenLintTopicAndLintProject_StillHidden()
        {
            using (var fx = ReviewFixture.Scratch(Once))
            {
                Exempt(fx, FirstEMail(fx), topic: "TOPIC.HTM");
                fx.LintOnlyCheck();
                Assert.Equal(0, EMailCount(fx.Session));
                var project = fx.NewSession(true);
                fx.LintOnlyCheck(project);
                Assert.Equal(0, EMailCount(project));
            }
        }

        [Fact]
        public void ExemptedText_IsHiddenDuringTheLiveCheckAsTheWriterTypes()
        {
            using (var fx = ReviewFixture.Scratch(Once))
            {
                Exempt(fx, FirstEMail(fx));
                fx.LintOnlyCheck();
                fx.Session.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)));
                Assert.Equal(0, EMailCount(fx.Session));
            }
        }

        [Fact]
        public void ATeammatesCopyOfTheProject_HonorsTheExemption()
        {
            var copy = Path.Combine(Path.GetTempPath(), "flareai-mate-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                string name;
                using (var fx = ReviewFixture.Scratch(Once))
                {
                    Exempt(fx, FirstEMail(fx));
                    name = Path.GetFileName(fx.Ws.ProjectDir);
                    CopyDir(fx.Ws.ProjectDir, Path.Combine(copy, name));
                }
                using (var ws = FakeWorkspace.FromFolder(Path.Combine(copy, name)))
                using (var mate = new ReviewFixture(null, Topic, null, false, ws))
                {
                    mate.LintOnlyCheck();
                    Assert.Equal(0, EMailCount(mate.Session));
                }
            }
            finally
            {
                try { Directory.Delete(copy, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void RemovedExemption_ShowsTheIssueAgain()
        {
            using (var fx = ReviewFixture.Scratch(Once))
            {
                Exempt(fx, FirstEMail(fx));
                fx.LintOnlyCheck();
                Assert.Equal(0, EMailCount(fx.Session));

                var entry = Exemptions.Load(fx.Doc.SourcePath).Entries.Single();
                Exemptions.Remove(fx.Doc.SourcePath, entry);
                // Two writes inside one clock tick can share a stamp; a teammate's edit minutes later would not.
                var file = Exemptions.FileFor(StyleEngine.FlareProjectRoot(fx.Doc.SourcePath));
                File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(5));

                fx.LintOnlyCheck();
                Assert.Equal(1, EMailCount(fx.Session));
                Assert.Equal(0, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void AStatusLine_SaysHowManyIssuesAreExempt()
        {
            using (var fx = ReviewFixture.Scratch(Once))
            {
                var s = fx.Session;
                var f = FirstEMail(fx);
                Assert.Equal("", s.ExemptTail);
                Assert.True(s.CanExempt(f));
                s.Select(f);
                s.Exempt(f, "Intended here.");
                Assert.Equal("1 issue is exempt.", s.ExemptTail);
                Assert.Equal(0, EMailCount(s));
                Assert.Equal(1, Exemptions.Load(fx.Doc.SourcePath).Entries.Count);
            }
        }

        [Fact]
        public void RefreshLintTwice_KeepsTheExemptCountAtOne()
        {
            using (var fx = ReviewFixture.Scratch(Once))
            {
                Exempt(fx, FirstEMail(fx));
                fx.LintOnlyCheck();
                Assert.Equal(1, fx.Session.ExemptHidden);
                fx.Session.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)));
                fx.Session.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)));
                Assert.Equal(1, fx.Session.ExemptHidden);
            }
        }

        [Fact]
        public void AnAiFindingWithNoRuleId_DoesNotOfferExempt()
        {
            using (var fx = ReviewFixture.Scratch(Once))
            {
                var ai = new StyleFinding { Layer = "ai", Source = "ai", Rule = "Tone", Excerpt = "e-mail to the team" };
                Assert.False(fx.Session.CanExempt(ai));
            }
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(from)) CopyDir(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
