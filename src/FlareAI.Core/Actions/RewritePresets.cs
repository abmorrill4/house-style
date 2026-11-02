namespace FlareAI.Core.Actions
{
    /// <summary>A one-click rewrite: a named instruction whose wording is a prompt template a team can override.</summary>
    public sealed class RewritePreset
    {
        public string Id;
        /// <summary>The prompt template file name (under FlareAI\prompts in a team guide).</summary>
        public string Template;
    }

    /// <summary>The presets on Rewrite's menu. Their wording carries no style rules; the guide supplies those.</summary>
    public static class RewritePresets
    {
        public static readonly RewritePreset Simplify = new RewritePreset { Id = "simplify", Template = "preset-simplify.md" };
        public static readonly RewritePreset Shorten = new RewritePreset { Id = "shorten", Template = "preset-shorten.md" };
    }
}
