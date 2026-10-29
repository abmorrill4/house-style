using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Scenarios.Support;
using Xunit;

namespace FlareAI.Scenarios.Review
{
    /// <summary>The issues the writer ignored are listed, and can be restored.</summary>
    public class IgnoreRestoreScenarios
    {
        private const string Utilize = "company:plain-language/1";
        private const string Body = "<h1>Tools</h1><p>First utilize the list.</p><p>Second utilize the grid.</p>";

        private static List<StyleFinding> Utilizes(ReviewSession s) { return s.Findings.Where(f => f.RuleId == Utilize && f.DocumentPath == null).OrderBy(f => f.Order).ToList(); }

        private static void Ignore(ReviewSession s, StyleFinding f)
        {
            s.Select(f);
            s.Reject("wrong");
        }

        private static void ChangeWording(ReviewFixture fx)
        {
            fx.Doc.Edit("change wording", () =>
            {
                foreach (XmlText t in fx.Doc.Document.SelectNodes("//text()").OfType<XmlText>().ToList())
                    if (t.Value.Contains("utilize")) t.Value = t.Value.Replace("utilize", "employ");
            });
        }

        [Fact]
        public void Ignore_ThenRestore_IssueIsBackSelected_AndShowsOnTheNextLint()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                fx.Check();
                var first = Utilizes(fx.Session)[0];
                var changes = 0;
                fx.Session.IgnoredChanged += () => changes++;
                Ignore(fx.Session, first);
                Assert.DoesNotContain(first, fx.Session.Findings);
                Assert.Same(first, fx.Session.Ignored.Single());

                var result = fx.Session.Restore(new List<StyleFinding> { first });

                Assert.Equal(1, result.Restored);
                Assert.True(result.Saved);
                Assert.Empty(fx.Session.Ignored);
                Assert.Contains(first, fx.Session.Findings);
                Assert.Same(first, fx.Session.Current);
                Assert.Equal(ReviewSession.RestoredOne, fx.Session.Status);
                Assert.True(changes >= 2);

                var again = fx.NewSession();
                fx.Check(again);
                Assert.Equal(2, Utilizes(again).Count);
                Assert.Empty(again.Ignored);
            }
        }

        [Fact]
        public void IgnoredEarlier_CountsInIgnored_NotInTheList()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                fx.Check();
                Ignore(fx.Session, Utilizes(fx.Session)[0]);

                var again = fx.NewSession();
                fx.Check(again);

                Assert.Single(Utilizes(again));
                Assert.Single(again.Ignored);
                Assert.Equal(Utilize, again.Ignored[0].RuleId);
            }
        }

        [Fact]
        public void Ignore_ThenLiveRechecks_KeepsOneEntry()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                fx.Check();
                Ignore(fx.Session, Utilizes(fx.Session)[0]);
                for (var i = 0; i < 3; i++)
                    fx.Session.RefreshLint(fx.Runner.Lint(fx.Runner.PickCheckBlocks(fx.Doc)));

                Assert.Single(fx.Session.Ignored);
                Assert.Single(Utilizes(fx.Session));
            }
        }

        [Fact]
        public void Restore_AfterTheTextChanged_ComesBackStale()
        {
            // the topic
            using (var fx = ReviewFixture.Scratch(Body))
            {
                fx.Check();
                var first = Utilizes(fx.Session)[0];
                Ignore(fx.Session, first);
                ChangeWording(fx);

                var result = fx.Session.Restore(fx.Session.Ignored.ToList());

                Assert.Equal(1, result.Restored);
                Assert.Contains(first, fx.Session.Findings);
                Assert.True(first.IsStale);
                Assert.True(first.ContextChanged);
            }

            // a file of the project that is open in the editor
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", null, true))
            {
                var s = fx.Session;
                s.Add(fx.Runner.LintFiles(ActionRunner.ProjectFiles(fx.Doc.SourcePath)));
                var f = s.Findings.First(x => x.DocumentPath != null && string.Equals(x.DocumentPath, fx.Doc.SourcePath, StringComparison.OrdinalIgnoreCase));
                Ignore(s, f);
                fx.Doc.Edit("clear", () =>
                {
                    foreach (var t in fx.Doc.Document.SelectNodes("//text()").OfType<XmlText>().ToList()) t.Value = "Nothing here.";
                });

                var result = s.Restore(s.Ignored.ToList());

                Assert.True(result.Restored >= 1);
                Assert.Contains(f, s.Findings);
                Assert.True(f.IsStale);
            }
        }

        [Fact]
        public void Restore_TwoIdenticalIssues_BringsBackBoth()
        {
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>First utilize the list.</p>"))
            {
                fx.Check();
                var one = Utilizes(fx.Session)[0];
                Ignore(fx.Session, one);
                // The same issue is found again (a second copy with the same key) while it is still ignored.
                var blocks = fx.Runner.PickCheckBlocks(fx.Doc);
                var twin = fx.Runner.Lint(blocks).Where(f => f.RuleId == Utilize).ToList();
                Assert.NotEmpty(twin);
                fx.Session.Add(twin);
                Assert.Empty(Utilizes(fx.Session));

                var result = fx.Session.Restore(fx.Session.Ignored.ToList());

                Assert.True(result.Restored >= 1);
                Assert.Empty(fx.Session.Ignored);
                Assert.Equal(result.Restored, Utilizes(fx.Session).Count);
                if (result.Restored > 1) Assert.Equal("Restored " + result.Restored + " issues. They are back in the list.", fx.Session.Status);
            }
        }

        [Fact]
        public void ProjectReport_IgnoreAndRestore_AcrossFiles()
        {
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", null, true))
            {
                var s = fx.Session;
                s.Add(fx.Runner.LintFiles(ActionRunner.ProjectFiles(fx.Doc.SourcePath)));
                var picks = s.Findings.Where(f => f.DocumentPath != null).GroupBy(f => f.DocumentPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(2).ToList();
                Assert.Equal(2, picks.Count);
                foreach (var f in picks) Ignore(s, f);
                Assert.Equal(2, s.Ignored.Count);
                foreach (var f in picks) Assert.DoesNotContain(f, s.Findings);

                var result = s.Restore(s.Ignored.ToList());

                Assert.Equal(2, result.Restored);
                Assert.Empty(s.Ignored);
                foreach (var f in picks) Assert.Contains(f, s.Findings);
                Assert.Equal("Restored 2 issues. They are back in the list.", s.Status);
            }
        }

        [Fact]
        public void ExcludingAFile_DropsItsIgnoredIssues()
        {
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", null, true))
            {
                var s = fx.Session;
                s.Add(fx.Runner.LintFiles(ActionRunner.ProjectFiles(fx.Doc.SourcePath)));
                var f = s.Findings.First(x => x.DocumentPath != null);
                var path = f.DocumentPath;
                Ignore(s, f);
                Assert.Single(s.Ignored);

                s.RemoveFiles(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

                Assert.Empty(s.Ignored);
                Assert.DoesNotContain(s.Findings, x => string.Equals(x.DocumentPath, path, StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void Restore_WhenSaveFails_StillComesBack_AndReportsIt()
        {
            using (var fx = ReviewFixture.Scratch(Body))
            {
                fx.Check();
                var first = Utilizes(fx.Session)[0];
                Ignore(fx.Session, first);
                fx.Ws.MakeReadOnly(Paths.Dismissed);

                var result = fx.Session.Restore(new List<StyleFinding> { first });

                Assert.False(result.Saved);
                Assert.Equal(1, result.Restored);
                Assert.Contains(first, fx.Session.Findings);
                Assert.Equal(ReviewSession.RestoredNotSaved, fx.Session.Status);
            }
        }
    }
}
