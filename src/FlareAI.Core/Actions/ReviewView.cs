using System;

namespace FlareAI.Core.Actions
{
    /// <summary>Severity the review list shows. <c>All</c> is the default.</summary>
    public enum ReviewLevel { All = 0, Errors, Warnings, Suggestions }

    /// <summary>Where a finding came from. <c>RulesAndAi</c> is the default; <c>RulesOnly</c> is lint and structure.</summary>
    public enum ReviewSource { RulesAndAi = 0, RulesOnly, AiOnly }

    /// <summary>
    /// Which findings the review list shows: one severity level and one source. Immutable. A finding must pass both
    /// the level and the source test. Severities and sources compare case-insensitively, as <see cref="RuleBacklog"/> does.
    /// </summary>
    public sealed class ReviewFilter
    {
        public static readonly ReviewFilter All = new ReviewFilter(ReviewLevel.All, ReviewSource.RulesAndAi);

        public ReviewFilter(ReviewLevel level, ReviewSource source)
        {
            Level = level;
            Source = source;
        }

        public ReviewLevel Level { get; private set; }
        public ReviewSource Source { get; private set; }

        public bool IsAll { get { return Level == ReviewLevel.All && Source == ReviewSource.RulesAndAi; } }

        public bool Shows(StyleFinding f)
        {
            return ShowsLevel(f.Severity) && ShowsSource(f.Source);
        }

        private bool ShowsLevel(string severity)
        {
            switch (Level)
            {
                case ReviewLevel.Errors: return string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase);
                case ReviewLevel.Warnings: return string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase);
                case ReviewLevel.Suggestions: return string.Equals(severity, "suggestion", StringComparison.OrdinalIgnoreCase);
                default: return true;
            }
        }

        private bool ShowsSource(string source)
        {
            switch (Source)
            {
                case ReviewSource.RulesOnly: return !string.Equals(source, "ai", StringComparison.OrdinalIgnoreCase);
                case ReviewSource.AiOnly: return string.Equals(source, "ai", StringComparison.OrdinalIgnoreCase);
                default: return true;
            }
        }
    }
}
