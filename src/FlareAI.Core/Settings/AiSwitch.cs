namespace FlareAI.Core.Settings
{
    /// <summary>
    /// The quick AI on/off switch on the House Style tab. It reads and writes the same <see cref="PluginSettings.AiOff"/>
    /// flag as the "No AI" choice on the AI tab of Settings.
    /// </summary>
    public static class AiSwitch
    {
        public const string OnLabel = "AI On";
        public const string OffLabel = "AI Off";
        public const string Tooltip = "Turns AI on or off. With AI off, linting uses the style rules only, and nothing is sent or charged.";
        public const string OffTip = "AI is off. To turn it on, select AI Off on the House Style tab.";
        public const string OffQuestion = "AI is off, so this can't run. Do you want to turn AI on?";
        public const string NeedKeyNote = "AI needs a key before it can run. Add your key on the AI tab, then select AI Off again.";

        public static string Label(bool off) { return off ? OffLabel : OnLabel; }

        /// <summary>A key is set, and a gateway also has an address.</summary>
        public static bool CanTurnOn(PluginSettings s)
        {
            return !string.IsNullOrEmpty(s.ResolveApiKey())
                && (s.Provider != LlmProvider.Bifrost || !string.IsNullOrWhiteSpace(s.BaseUrl));
        }

        /// <summary>
        /// Turns AI off and changes nothing else. It does not use AiChoices.Apply(None), which deletes the stored key;
        /// a quick switch keeps the key.
        /// </summary>
        public static void TurnOff(PluginSettings s) { s.AiOff = true; }

        /// <summary>Turns AI on. Returns false and changes nothing when there is no key or gateway yet.</summary>
        public static bool TryTurnOn(PluginSettings s)
        {
            if (!CanTurnOn(s)) return false;
            s.AiOff = false;
            return true;
        }
    }
}
