namespace FlareAI.Core.Settings
{
    /// <summary>How the writer uses AI, as Settings and first-run setup offer it.</summary>
    public enum AiChoice
    {
        /// <summary>No AI: style rules only. No key is stored.</summary>
        None = 0,
        /// <summary>The writer's own Anthropic API key (<see cref="LlmProvider.AnthropicDirect"/>).</summary>
        AnthropicKey = 1,
        /// <summary>The company's AI gateway (<see cref="LlmProvider.Bifrost"/>): an address and a gateway key.</summary>
        Gateway = 2
    }

    /// <summary>
    /// Which fields each <see cref="AiChoice"/> needs, its wording, and how it maps onto <see cref="PluginSettings"/>.
    /// The one place Settings and the setup steps agree on what to show.
    /// </summary>
    public static class AiChoices
    {
        /// <summary>The choices in display order.</summary>
        public static readonly AiChoice[] All = { AiChoice.None, AiChoice.AnthropicKey, AiChoice.Gateway };

        /// <summary>The choice as a writer sees it.</summary>
        public static string Label(AiChoice c)
        {
            switch (c)
            {
                case AiChoice.None: return "No AI (rules only)";
                case AiChoice.Gateway: return "My company's AI service (gateway)";
                default: return "My Anthropic API key";
            }
        }

        /// <summary>The choice the settings describe.</summary>
        public static AiChoice Of(PluginSettings s)
        {
            if (s == null) return AiChoice.AnthropicKey;
            if (s.AiOff) return AiChoice.None;
            return s.Provider == LlmProvider.Bifrost ? AiChoice.Gateway : AiChoice.AnthropicKey;
        }

        /// <summary>The provider behind an AI choice (<see cref="AiChoice.None"/> keeps Anthropic as the nominal one).</summary>
        public static LlmProvider Provider(AiChoice c) { return c == AiChoice.Gateway ? LlmProvider.Bifrost : LlmProvider.AnthropicDirect; }

        /// <summary>A key field (and Test connection) applies.</summary>
        public static bool UsesKey(AiChoice c) { return c != AiChoice.None; }

        /// <summary>The Base URL field applies (the gateway only; the Anthropic address is fixed).</summary>
        public static bool UsesBaseUrl(AiChoice c) { return c == AiChoice.Gateway; }

        /// <summary>The model pickers can be changed (the gateway's models are fixed; no AI has none).</summary>
        public static bool UsesModelPickers(AiChoice c) { return c == AiChoice.AnthropicKey; }

        /// <summary>The key field's label: "API key" or "Gateway key".</summary>
        public static string KeyLabel(AiChoice c) { return c == AiChoice.Gateway ? "Gateway key" : "API key"; }

        /// <summary>
        /// Sets the provider and the AI switch. <see cref="AiChoice.None"/> also removes the stored key, so nothing
        /// secret stays behind; the provider and address are kept so choosing AI again restores them.
        /// A change of provider also removes the stored key, because a key only works with the provider it came from.
        /// </summary>
        public static void Apply(PluginSettings s, AiChoice c)
        {
            s.AiOff = c == AiChoice.None;
            if (c == AiChoice.None) { s.SetApiKey(null); return; }
            var was = s.Provider;
            s.Provider = Provider(c);
            if (was != s.Provider) s.SetApiKey(null);
            // The address belongs to the provider: switching drops the other one's (the Anthropic address is fixed).
            if (c == AiChoice.AnthropicKey && (was == LlmProvider.Bifrost || string.IsNullOrEmpty(s.BaseUrl) || s.BaseUrl.Contains("/v1")))
                s.BaseUrl = PluginSettings.DefaultAnthropicBaseUrl;
            if (c == AiChoice.Gateway && was != LlmProvider.Bifrost && s.BaseUrl == PluginSettings.DefaultAnthropicBaseUrl)
                s.BaseUrl = "";
        }
    }
}
