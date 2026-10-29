using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Scenarios.Support;
using Xunit;

namespace FlareAI.Scenarios.Review
{
    /// <summary>The Issues list narrowed by level and by source.</summary>
    public class ReviewViewScenarios
    {
        private const string Utilize = "company:plain-language/1";

        [Fact]
        public void ErrorsOnly_NextSkipsWarnings_AndFixAllChangesOnlyErrors()
        {
            var body = "<h1>Tools</h1><p>First utilize the list.</p><p>Second utilize the grid.</p><p>Third utilize the menu.</p>";
            using (var fx = ReviewFixture.Scratch(body))
            {
                fx.Check();
                var all = fx.Session.Findings.Where(f => f.RuleId == Utilize).OrderBy(f => f.Order).ToList();
                Assert.Equal(3, all.Count);
                all[0].Severity = "warning";
                all[1].Severity = "error";
                all[2].Severity = "warning";

                var changed = 0;
                fx.Session.ListChanged += () => changed++;
                fx.Session.Select(all[1]);
                fx.Session.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.RulesAndAi);
                Assert.True(changed > 0);
                Assert.Equal(all[1], fx.Session.Current);

                Assert.Equal(all[1], fx.Session.NextAfter(all[0]));
                Assert.Equal(all[1], fx.Session.NextAfter(null));
                Assert.Equal(1, fx.Session.Visible().Count(f => f.RuleId == Utilize));
                Assert.Contains("Showing 1 of ", fx.Session.Summary());

                fx.Session.FixAll();

                var xml = fx.Doc.Xml;
                Assert.Contains("Third utilize the menu", xml);
                Assert.Contains("First utilize the list", xml);
                Assert.DoesNotContain("Second utilize the grid", xml);

                fx.Session.Filter = new ReviewFilter(ReviewLevel.Errors, ReviewSource.AiOnly);
                Assert.Null(fx.Session.Current);
                Assert.Equal("No issues match these filters.", fx.Session.Summary());
            }
        }
    }
}
