using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class ReviewViewTests
    {
        private static StyleFinding Finding(string severity, string source)
        {
            return new StyleFinding { Source = source, Severity = severity, Rule = "r", Excerpt = "x" };
        }

        [Theory]
        [InlineData(ReviewLevel.All, ReviewSource.RulesAndAi, "error", "lint", true)]
        [InlineData(ReviewLevel.All, ReviewSource.RulesAndAi, "suggestion", "ai", true)]
        [InlineData(ReviewLevel.Errors, ReviewSource.RulesAndAi, "error", "ai", true)]
        [InlineData(ReviewLevel.Errors, ReviewSource.RulesAndAi, "warning", "lint", false)]
        [InlineData(ReviewLevel.Errors, ReviewSource.RulesAndAi, "suggestion", "structure", false)]
        [InlineData(ReviewLevel.Warnings, ReviewSource.RulesAndAi, "warning", "lint", true)]
        [InlineData(ReviewLevel.Warnings, ReviewSource.RulesAndAi, "error", "ai", false)]
        [InlineData(ReviewLevel.Suggestions, ReviewSource.RulesAndAi, "suggestion", "ai", true)]
        [InlineData(ReviewLevel.Suggestions, ReviewSource.RulesAndAi, "warning", "lint", false)]
        [InlineData(ReviewLevel.All, ReviewSource.RulesOnly, "error", "lint", true)]
        [InlineData(ReviewLevel.All, ReviewSource.RulesOnly, "suggestion", "structure", true)]
        [InlineData(ReviewLevel.All, ReviewSource.RulesOnly, "warning", "ai", false)]
        [InlineData(ReviewLevel.All, ReviewSource.AiOnly, "suggestion", "ai", true)]
        [InlineData(ReviewLevel.All, ReviewSource.AiOnly, "error", "lint", false)]
        [InlineData(ReviewLevel.All, ReviewSource.AiOnly, "warning", "structure", false)]
        [InlineData(ReviewLevel.Errors, ReviewSource.RulesOnly, "error", "structure", true)]
        [InlineData(ReviewLevel.Errors, ReviewSource.RulesOnly, "error", "ai", false)]
        [InlineData(ReviewLevel.Warnings, ReviewSource.RulesOnly, "warning", "lint", true)]
        [InlineData(ReviewLevel.Warnings, ReviewSource.AiOnly, "warning", "lint", false)]
        [InlineData(ReviewLevel.Suggestions, ReviewSource.AiOnly, "suggestion", "ai", true)]
        [InlineData(ReviewLevel.Suggestions, ReviewSource.AiOnly, "suggestion", "structure", false)]
        public void Filter_Shows(ReviewLevel level, ReviewSource source, string severity, string findingSource, bool expected)
        {
            var filter = new ReviewFilter(level, source);
            Assert.Equal(expected, filter.Shows(Finding(severity, findingSource)));
        }

        [Fact]
        public void All_IsTheOnlyFilterThatIsAll_AndShowsEverything()
        {
            Assert.True(ReviewFilter.All.IsAll);
            Assert.False(new ReviewFilter(ReviewLevel.Errors, ReviewSource.RulesAndAi).IsAll);
            Assert.False(new ReviewFilter(ReviewLevel.All, ReviewSource.AiOnly).IsAll);
            Assert.True(ReviewFilter.All.Shows(Finding("suggestion", "structure")));
        }
    }
}
