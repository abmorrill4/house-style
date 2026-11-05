using System;

namespace FlareAI.Core.Style
{
    /// <summary>The longest one pattern may take to match. A guide's Pattern rows are not trusted to finish.</summary>
    public static class RegexLimit
    {
        /// <summary>How long one match may run before the rule is skipped.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Sets the process-wide default match timeout. Call it once at start-up, before the first Regex in the process is
        /// created or used, because .NET reads the default only once.
        /// </summary>
        public static void Apply()
        {
            AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", Timeout);
        }
    }
}
