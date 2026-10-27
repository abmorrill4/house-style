using System;

namespace FlareAI.Flare
{
    /// <summary>Lint Topic's remembered checks (PluginSettings.LintChecks): "rules" or "rules-ai".</summary>
    internal static class LintChecksChoice
    {
        public const string Rules = "rules", RulesAndAi = "rules-ai";
        public const string StatusEnding = "These issues come from the rules only. To add the AI review, choose Rules and AI Review on Lint Topic.";
        public const string LintParagraphTip = "Lint Paragraph needs the AI review. Choose Rules and AI Review on Lint Topic.";
        public const string RewriteTip = "Rewrite needs the AI review. Choose Rules and AI Review on Lint Topic.";
        public const string ChangeNeedsAi = "This change needs the AI review. Choose Rules and AI Review on Lint Topic.";

        /// <summary>False only for "rules-ai" (any case) and null or empty (first use). Anything else is Rules Only.</summary>
        public static bool IsRulesOnly(string value)
        {
            return !string.IsNullOrEmpty(value) && !string.Equals(value, RulesAndAi, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Saves the choice. A failed save is logged and reported as false, never thrown.</summary>
        public static bool TrySave(string value, Action<string> save)
        {
            try { save(value); return true; }
            catch (Exception ex) { FlareAI.Core.Diagnostics.Log("save lint checks", ex); return false; }
        }
    }
}
