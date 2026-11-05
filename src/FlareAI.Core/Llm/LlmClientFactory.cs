using FlareAI.Core.Settings;

namespace FlareAI.Core.Llm
{
    public static class LlmClientFactory
    {
        /// <summary>Shown when an AI command runs while the writer chose "No AI (style rules only)".</summary>
        public const string AiOffMessage = "AI is off. To turn it on, select AI Off on the House Style tab, or change it on the AI tab in Settings.";

        public static ILlmClient Create(PluginSettings s)
        {
            if (s.AiOff) throw new LlmException(AiOffMessage);
            var key = s.ResolveApiKey();
            switch (s.Provider)
            {
                case LlmProvider.Bifrost:
                    return new BifrostClient(s.BaseUrl, key, s.Model, s.TimeoutSeconds);
                default:
                    return new AnthropicClient(s.BaseUrl, key, s.Model, s.TimeoutSeconds);
            }
        }
    }
}
