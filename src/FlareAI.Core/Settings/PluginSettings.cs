using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace FlareAI.Core.Settings
{
    /// <summary>Which backend the plugin talks to.</summary>
    public enum LlmProvider
    {
        AnthropicDirect = 0,
        Bifrost = 1
    }

    /// <summary>
    /// Persisted at %APPDATA%\FlareAI\settings.json. The API key is DPAPI-protected (CurrentUser scope),
    /// so the file is useless on another machine or account. Dev fallback: ANTHROPIC_API_KEY env var
    /// for Anthropic direct, HOUSESTYLE_GATEWAY_KEY for Bifrost.
    /// </summary>
    public sealed class PluginSettings
    {
        public const string DefaultAnthropicBaseUrl = "https://api.anthropic.com";
        public const string DefaultModel = "claude-sonnet-5-5";
        public const string EnvKeyName = "ANTHROPIC_API_KEY";
        public const string GatewayEnvKeyName = "HOUSESTYLE_GATEWAY_KEY";

        public LlmProvider Provider { get; set; }
        /// <summary>
        /// The writer chose "No AI (style rules only)": no AI client is built, even when a key is in the environment.
        /// Style checks work as always. Older settings files don't have it and read as false (AI on), as before.
        /// </summary>
        public bool AiOff { get; set; }
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public int MaxTokens { get; set; }
        public int TimeoutSeconds { get; set; }
        /// <summary>DPAPI-protected, base64. Never the plaintext key.</summary>
        public string ProtectedApiKey { get; set; }
        public bool WriteDiagnostics { get; set; }
        /// <summary>
        /// The team's style guide project: a Flare project with Content\Resources\StyleGuide\Google and \Company. One per
        /// line if there are several (later wins). Empty = the starter guide bundled with the plugin.
        /// </summary>
        public string StyleLayers { get; set; }
        /// <summary>Add the open Flare project's variables, glossary and Resources/StyleGuide folder as the last layer.</summary>
        public bool UseProjectLayer { get; set; }
        /// <summary>After each build of an HTML target, add Markdown per topic + llms.txt to the output.</summary>
        public bool LlmExportAfterBuild { get; set; }
        /// <summary>Mark paragraphs with open proposals in the editor (a margin bar; view only, never saved).</summary>
        public bool ShowMarkers { get; set; }
        /// <summary>Re-lint the topic a moment after typing stops (free: no AI).</summary>
        public bool LiveCheck { get; set; }
        /// <summary>
        /// The checks Lint Topic last ran, "rules" or "rules-ai" (the default). Stored as given: how an unknown value reads
        /// is decided where it is used.
        /// </summary>
        public string LintChecks { get; set; }
        /// <summary>After saving a topic, show how many style issues it has (never blocks the save).</summary>
        public bool CheckOnSave { get; set; }
        /// <summary>When a build starts, lint the project and show a report (never blocks the build).</summary>
        public bool ReportBeforeBuild { get; set; }
        /// <summary>Fast-tier model (refine, topic assists); empty = built-in default. <see cref="Model"/> is the standard tier.</summary>
        public string FastModel { get; set; }
        /// <summary>Deep-tier model (owner edits to the style guide); empty = built-in default.</summary>
        public string DeepModel { get; set; }
        /// <summary>Model calls run at once for per-block work.</summary>
        public int Parallelism { get; set; }
        /// <summary>The project check lists suggestions by file instead of by rule (the default).</summary>
        public bool ProjectViewByFile { get; set; }
        /// <summary>The writer hid the "drag this pane's edge to make it wider" tip in the review pane.</summary>
        public bool PaneWidthTipDismissed { get; set; }
        /// <summary>
        /// Check GitHub once a day for a newer release and offer to install it (issue #101). An IT policy file,
        /// update-policy.json in the plugin folder, wins over this (<see cref="FlareAI.Core.Update.UpdatePolicy"/>).
        /// Settings UI: a "Keep House Style up to date" check box, read-only with "Set by your IT department" when
        /// the policy file decides.
        /// </summary>
        public bool AutoUpdate { get; set; }
        /// <summary>
        /// Writer or style guide owner, from setup's first question or Settings; it decides the ribbon (read at Flare
        /// start). Null in settings from before roles: see <see cref="Roles.Effective"/>.
        /// </summary>
        public UserRole? Role { get; set; }

        // ---- feedback (issue #124) ------------------------------------------------------------------------------

        /// <summary>The House Style feedback service that receives reports.</summary>
        public const string DefaultFeedbackUrl = "https://house-style-feedback.house-style-feedback-worker.workers.dev";
        /// <summary>Points reports at another service (a local test server). Developers and testers only.</summary>
        public const string FeedbackUrlEnvVar = "HOUSESTYLE_FEEDBACK_URL";

        /// <summary>
        /// A random id for this install, sent with reports so the service can rate limit and count repeats. It says nothing
        /// about the writer. Created on first use (<see cref="EnsureClientId"/>).
        /// </summary>
        public string ClientId { get; set; }
        /// <summary>The feedback service's base URL; empty means <see cref="DefaultFeedbackUrl"/>.</summary>
        public string FeedbackUrl { get; set; }
        /// <summary>When House Style hits an error, send the report in the background instead of asking. Off by default.</summary>
        public bool SendErrorReportsAutomatically { get; set; }
        /// <summary>Where a saved report is emailed when the service can't be reached; empty opens the folder instead.</summary>
        public string ReportEmail { get; set; }

        /// <summary>The service in effect: the environment variable, then <see cref="FeedbackUrl"/>, then the default.</summary>
        public string EffectiveFeedbackUrl
        {
            get
            {
                var env = Environment.GetEnvironmentVariable(FeedbackUrlEnvVar);
                if (!string.IsNullOrEmpty(env)) return env.TrimEnd('/');
                return (string.IsNullOrEmpty(FeedbackUrl) ? DefaultFeedbackUrl : FeedbackUrl).TrimEnd('/');
            }
        }

        private static string _sessionClientId;

        /// <summary>
        /// This install's id, created when missing. It is saved only when a settings file exists already (saving one would
        /// count as finishing first-run setup); until then the same id is used for the rest of the session.
        /// </summary>
        public static string EnsureClientId()
        {
            var s = Load();
            Guid g;
            if (!string.IsNullOrEmpty(s.ClientId) && Guid.TryParse(s.ClientId, out g)) return g.ToString("D");
            if (_sessionClientId == null) _sessionClientId = Guid.NewGuid().ToString("D");
            if (File.Exists(SettingsPath))
            {
                s.ClientId = _sessionClientId;
                try { s.Save(); } catch (Exception ex) { Diagnostics.Log("client id", ex); }
            }
            return _sessionClientId;
        }

        /// <summary>The routing in effect: these settings, then the team's FlareAI\models.json in the style guide.</summary>
        public FlareAI.Core.Llm.Routing Routing(string guideRoot)
        {
            return new FlareAI.Core.Llm.Routing(string.IsNullOrEmpty(FastModel) ? null : FastModel, string.IsNullOrEmpty(Model) ? null : Model,
                                                string.IsNullOrEmpty(DeepModel) ? null : DeepModel).WithTeamFile(guideRoot);
        }
        /// <summary>
        /// Shared team feedback folder (a synced OneDrive/SharePoint folder, network share, or repo writers can push to);
        /// each writer appends to their own file there. Empty = decisions stay in the personal log only.
        /// </summary>
        public string FeedbackFolder { get; set; }

        /// <summary>The style guide shipped with the plugin: the starter project, and the Google rules for guides without them.</summary>
        public static string BundledGuide(string pluginDir) { return System.IO.Path.Combine(pluginDir, "StyleGuide"); }

        /// <summary>
        /// The writer's own style guide (%APPDATA%\FlareAI\StyleGuide): where rule changes go when no team guide is
        /// configured, because the plugin folder (Program Files) is read-only for standard users. It holds only the
        /// writer's changes, as a layer after the bundled guide.
        /// </summary>
        public static string UserGuidePath { get { return Path.Combine(SettingsDirectory, "StyleGuide"); } }

        /// <summary>
        /// The style guide projects in effect, in order: the configured ones, or the bundled guide; then the writer's own
        /// guide (their changes only) once it exists, when rule changes go there. The bundled layers always load from the
        /// plugin, so plugin updates reach the writer. A writer's guide in the old layout (a full copy of the bundled one)
        /// is upgraded here first (<see cref="FlareAI.Core.Style.GuideSeed.Upgrade"/>); if that fails it stands in for the
        /// read-only guide as before, so the writer's changes still apply.
        /// </summary>
        /// <param name="userGuide">The writer's own guide (default <see cref="UserGuidePath"/>; tests pass another).</param>
        public System.Collections.Generic.List<string> GuideRoots(string pluginDir, string userGuide = null)
        {
            var list = Configured(pluginDir);
            var readOnly = ReadOnlyGuide(pluginDir);
            if (list.Count == 0) list.Add(readOnly);
            var mine = userGuide ?? UserGuidePath;
            if (readOnly == null || !FlareAI.Core.Style.GuideSeed.IsSeeded(mine)) return list;
            if (FlareAI.Core.Style.GuideSeed.Upgrade(readOnly, mine)) list.Add(mine);
            else list[list.Count - 1] = mine;
            return list;
        }

        /// <summary>
        /// Where rule changes are written: the last configured team guide, or the writer's own guide when none is configured
        /// (or the configured one is inside the plugin folder). Never the plugin folder.
        /// </summary>
        public string GuideRoot(string pluginDir, string userGuide = null)
        {
            var list = Configured(pluginDir);
            return ReadOnlyGuide(pluginDir) == null ? list[list.Count - 1] : userGuide ?? UserGuidePath;
        }

        /// <summary>
        /// The read-only guide the writer's own guide is layered after (and takes its layer name from): the bundled guide,
        /// or a configured guide inside the plugin folder. Null when rule changes go to a team guide.
        /// </summary>
        public string ReadOnlyGuide(string pluginDir)
        {
            var list = Configured(pluginDir);
            if (list.Count == 0) return BundledGuide(pluginDir);
            return IsUnder(list[list.Count - 1], pluginDir) ? list[list.Count - 1] : null;
        }

        /// <summary>True when a team style guide (outside the plugin folder) is configured.</summary>
        public bool HasTeamGuide(string pluginDir) { return ReadOnlyGuide(pluginDir) == null; }

        private System.Collections.Generic.List<string> Configured(string pluginDir)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var line in (StyleLayers ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.Trim().Length > 0) list.Add(line.Trim().Replace("{PluginDir}", pluginDir).TrimEnd('\\'));
            return list;
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
        public static bool IsUnder(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
            try
            {
                var p = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
                var f = Path.GetFullPath(folder).TrimEnd('\\', '/') + "\\";
                return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        public PluginSettings()
        {
            Provider = LlmProvider.AnthropicDirect;
            BaseUrl = DefaultAnthropicBaseUrl;
            Model = DefaultModel;
            MaxTokens = 4096;
            TimeoutSeconds = 90;
            WriteDiagnostics = true;
            StyleLayers = "";
            UseProjectLayer = true;
            ShowMarkers = LiveCheck = CheckOnSave = ReportBeforeBuild = true;
            LintChecks = "rules-ai";
            AutoUpdate = true;
        }

        // ---- key handling -------------------------------------------------------------

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FlareAI.v1");

        public void SetApiKey(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) { ProtectedApiKey = null; return; }
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var enc = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            ProtectedApiKey = Convert.ToBase64String(enc);
        }

        /// <summary>Stored key first, then the environment variable for the provider: ANTHROPIC_API_KEY for Anthropic direct, HOUSESTYLE_GATEWAY_KEY for Bifrost. Null if neither.</summary>
        public string ResolveApiKey()
        {
            if (!string.IsNullOrEmpty(ProtectedApiKey))
            {
                try
                {
                    var dec = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedApiKey), Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(dec);
                }
                catch (CryptographicException) { /* different user/machine — fall through */ }
            }
            string name;
            if (Provider == LlmProvider.AnthropicDirect) name = EnvKeyName;
            else if (Provider == LlmProvider.Bifrost) name = GatewayEnvKeyName;
            else return null;
            var env = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(env) ? null : env;
        }

        public bool HasStoredKey { get { return !string.IsNullOrEmpty(ProtectedApiKey); } }

        // ---- persistence --------------------------------------------------------------

        public static string SettingsDirectory
        {
            get { return FlareAI.Core.Paths.Root; }   // %APPDATA%\FlareAI unless redirected
        }

        public static string SettingsPath { get { return FlareAI.Core.Paths.Settings; } }

        public static PluginSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonConvert.DeserializeObject<PluginSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8));
                    if (s != null) return s;
                }
            }
            catch (Exception) { /* corrupt file → defaults */ }
            return new PluginSettings();
        }

        /// <summary>A separate copy, for previews that must not change these settings.</summary>
        public PluginSettings Clone()
        {
            return JsonConvert.DeserializeObject<PluginSettings>(JsonConvert.SerializeObject(this));
        }

        public void Save()
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
        }
    }
}
