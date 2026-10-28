using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Scenarios.Support;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Rules
{
    /// <summary>
    /// Exempt one place without turning a rule off: the review list leaves out the exempted text for that rule in that topic, in the
    /// topic check, the live check, a project check and after a restart, and says how many issues are exempt.
    /// </summary>
    public class ExemptionScenarios
    {
        private const string Body = "<h1>Help</h1><p>Please utilize the tool.</p><p>Then utilize the menu.</p>";
        private const string Topic = "Content/topic.htm";
        private const string Rule = "google:word-list/1";
        private const string OtherRule = "google:word-list/2";

        private static string Root(ReviewFixture fx) { return fx.Ws.ProjectDir; }

        private static string KeyOf(ReviewFixture fx) { return Exemptions.TopicKey(fx.Doc.SourcePath); }

        private static StyleFinding Utilize(string before, string ruleId = Rule, string text = "utilize", string documentPath = null)
        {
            return new StyleFinding
            {
                Source = "lint", Rule = "Google.WordList", RuleId = ruleId, Layer = "google", Severity = "warning",
                Excerpt = text, Before = before, Replacement = "use", DocumentPath = documentPath
            };
        }

        private static List<StyleFinding> Lint(string ruleId = Rule, string text = "utilize")
        {
            return new List<StyleFinding> { Utilize("Please ", ruleId, text), Utilize("Then ", ruleId, text) };
        }

        private static void Exempt(ReviewFixture fx, string before, string ruleId = Rule, string text = "utilize")
        {
            Assert.Null(Exemptions.Add(Root(fx), new ExemptionEntry(KeyOf(fx), ruleId, text, before, "Quoted.", "pat@example.com", "2026-10-28")));
        }

        private static void Unexempt(ReviewFixture fx, string before)
        {
            Assert.Null(Exemptions.Remove(Root(fx), new ExemptionEntry(KeyOf(fx), Rule, "utilize", before, "", "", "")));
            // a rewrite inside the same clock tick must still look changed
            var file = Exemptions.FileFor(Root(fx));
            if (File.Exists(file)) File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(5));
        }

        private static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyFolder(d, Path.Combine(to, Path.GetFileName(d)));
        }

        [Fact]
        public void ExemptedText_IsGoneFromLintTopicLintProjectAndARestart()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, "Please ");

                var topic = fx.NewSession();
                topic.Add(Lint());
                Assert.Equal(new[] { "Then " }, topic.Findings.Select(f => f.Before).ToArray());
                Assert.Equal(1, topic.ExemptHidden);

                var project = fx.NewSession(true);
                project.Add(new List<StyleFinding> { Utilize("Please ", documentPath: fx.Doc.SourcePath), Utilize("Then ", documentPath: fx.Doc.SourcePath) });
                Assert.Equal(new[] { "Then " }, project.Findings.Select(f => f.Before).ToArray());

                var restarted = fx.NewSession();
                restarted.Add(Lint());
                Assert.Equal(new[] { "Then " }, restarted.Findings.Select(f => f.Before).ToArray());
            }
        }

        [Fact]
        public void ExemptedText_OtherRuleSameText_StillShows()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, "Please ");
                var s = fx.NewSession();
                s.Add(Lint(OtherRule));
                Assert.Equal(2, s.Findings.Count);
                Assert.Equal(0, s.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_SameRuleOtherText_StillShows()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, "Please ");
                var s = fx.NewSession();
                s.Add(Lint(Rule, "tool"));
                Assert.Equal(2, s.Findings.Count);
                Assert.Equal(0, s.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_SameWordTwiceInOneTopic_OneCopyStillShows()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, "Then ");
                var s = fx.NewSession();
                s.Add(Lint());
                Assert.Equal(1, s.Findings.Count);
                Assert.Equal("Please ", s.Findings[0].Before);
                Assert.Equal(1, s.ExemptHidden);
            }
        }

        [Fact]
        public void ExemptedText_PathCasingDiffersBetweenLintTopicAndLintProject_StillHidden()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, "Please ");
                var upper = fx.Doc.SourcePath.ToUpperInvariant();
                var s = fx.NewSession(true);
                s.Add(new List<StyleFinding> { Utilize("Please ", documentPath: upper), Utilize("Please ", documentPath: fx.Doc.SourcePath.ToLowerInvariant()) });
                Assert.Equal(0, s.Findings.Count);
            }
        }

        [Fact]
        public void ExemptedText_IsHiddenDuringTheLiveCheckAsTheWriterTypes()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, "Please ");
                var s = fx.NewSession();
                s.RefreshLint(Lint());
                Assert.Equal(new[] { "Then " }, s.Findings.Select(f => f.Before).ToArray());
            }
        }

        [Fact]
        public void ATeammatesCopyOfTheProject_HonorsTheExemption()
        {
            var copy = Path.Combine(Path.GetTempPath(), "flareai-exempt-" + Guid.NewGuid().ToString("N").Substring(0, 12), "Scratch");
            try
            {
                using (var fx = ReviewFixture.Scratch(Body))
                {
                    Exempt(fx, "Please ");
                    CopyFolder(Root(fx), copy);
                }
                using (var mate = new ReviewFixture(null, Topic, null, false, FakeWorkspace.FromFolder(copy)))
                {
                    var s = mate.NewSession();
                    s.Add(Lint());
                    Assert.Equal(new[] { "Then " }, s.Findings.Select(f => f.Before).ToArray());
                }
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(copy), true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        [Fact]
        public void RemovedExemption_ShowsTheIssueAgain()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, "Please ");
                var s = fx.NewSession();
                s.Add(Lint());
                Assert.Equal(1, s.Findings.Count);

                Unexempt(fx, "Please ");
                s.Clear();
                s.Add(Lint());
                Assert.Equal(2, s.Findings.Count);
                Assert.Equal(0, s.ExemptHidden);
            }
        }

        [Fact]
        public void AStatusLine_SaysHowManyIssuesAreExempt()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                var none = fx.NewSession();
                none.Add(Lint());
                Assert.DoesNotContain("exempt", none.Summary());

                Exempt(fx, "Please ");
                var one = fx.NewSession();
                one.Add(Lint());
                Assert.EndsWith(" 1 issue is exempt.", one.Summary());

                Exempt(fx, "Then ");
                var two = fx.NewSession();
                two.Add(Lint());
                Assert.EndsWith(" 2 issues are exempt.", two.Summary());
            }
        }

        [Fact]
        public void RefreshLintTwice_KeepsTheExemptCountAtOne()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                Exempt(fx, "Please ");
                var s = fx.NewSession();
                s.RefreshLint(Lint());
                s.RefreshLint(Lint());
                s.Add(Lint());   // a repeated batch can't raise it either
                Assert.Equal(1, s.ExemptHidden);
            }
        }

        [Fact]
        public void AnAiFindingWithNoRuleId_DoesNotOfferExempt()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                var s = fx.NewSession();
                var ai = new StyleFinding { Source = "ai", Layer = "ai", Rule = "Plain words", Severity = "suggestion", Excerpt = "utilize", Before = "Please ", Replacement = "use" };
                var structure = new StyleFinding { Source = "structure", Layer = "structure", Rule = "Structure.HeadingLevel", Severity = "warning", Excerpt = "Help", Before = "" };
                Assert.False(s.CanExempt(ai));
                Assert.True(s.CanExempt(Utilize("Please ")));
                Assert.True(s.CanExempt(structure));
            }
        }

        [Fact]
        public void ExemptALintFinding_RemovesItWritesTheLineAndCountsIt()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                var s = fx.NewSession();
                s.Add(Lint());
                var please = s.Findings.First(f => f.Before == "Please ");

                Assert.Null(s.Exempt(please, "Product name spelling."));

                Assert.Equal(new[] { "Then " }, s.Findings.Select(f => f.Before).ToArray());
                Assert.Equal(1, s.ExemptHidden);
                Assert.EndsWith("  Exempted. It stays hidden for this text for everyone on this project.", s.Status);
                var entry = Exemptions.Load(Root(fx)).Entries.Single();
                Assert.Equal(KeyOf(fx), entry.Topic);
                Assert.Equal(Rule, entry.Rule);
                Assert.Equal("Please ", entry.Before);
                Assert.Equal("Product name spelling.", entry.Reason);
            }
        }

        [Fact]
        public void ExemptWhenTheProjectCannotBeWritten_ReturnsTheErrorAndKeepsTheFinding()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                var file = Exemptions.FileFor(Root(fx));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, "# read-only for this test\n");
                File.SetAttributes(file, FileAttributes.ReadOnly);
                try
                {
                    var s = fx.NewSession();
                    s.Add(Lint());
                    var please = s.Findings.First(f => f.Before == "Please ");

                    Assert.NotNull(s.Exempt(please, "Product name spelling."));

                    Assert.Equal(2, s.Findings.Count);
                    Assert.Contains(please, s.Findings);
                    Assert.Equal(0, s.ExemptHidden);
                    Assert.Empty(Exemptions.Load(Root(fx)).Entries);
                }
                finally { File.SetAttributes(file, FileAttributes.Normal); }
            }
        }
    }
}
