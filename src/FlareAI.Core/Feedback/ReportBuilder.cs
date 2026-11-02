using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FlareAI.Core.Settings;
using Newtonsoft.Json;

namespace FlareAI.Core.Feedback
{
    /// <summary>What a report says about this install: the same for every report in a session.</summary>
    public sealed class ReportContext
    {
        public string ClientId { get; set; }
        public string PluginVersion { get; set; }
        public string FlareVersion { get; set; }
        public string Os { get; set; }
        /// <summary>The diagnostics log whose end is attached (null: none).</summary>
        public string DiagLogPath { get; set; }
        /// <summary>The settings in effect. Only the fields in <see cref="ReportBuilder.SafeSettings"/> are read.</summary>
        public PluginSettings Settings { get; set; }

        /// <summary>This install: its client id, the plugin and Flare versions, Windows, diag.log and the saved settings.</summary>
        public static ReportContext Current(string pluginVersion, string flareVersion)
        {
            return new ReportContext
            {
                ClientId = PluginSettings.EnsureClientId(),
                PluginVersion = pluginVersion,
                FlareVersion = flareVersion,
                Os = Environment.OSVersion.VersionString + (Environment.Is64BitProcess ? " (64-bit)" : " (32-bit)") + ", .NET " + Environment.Version,
                DiagLogPath = Diagnostics.LogPath,
                Settings = PluginSettings.Load()
            };
        }
    }

    /// <summary>
    /// Builds the reports House Style sends (issue #124), exactly as the service accepts them: known fields only, every
    /// text cut to the service's limits, control characters removed. Nothing secret goes in: settings are copied field by
    /// field from a short list of harmless ones (never the key, the gateway address or folder paths), and every text is
    /// scrubbed of the API key and anything that looks like a key or token. Topic text is never added here; the writer's
    /// own words (what happened, a note) and the end of diag.log are.
    /// </summary>
    public static class ReportBuilder
    {
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            StringEscapeHandling = StringEscapeHandling.Default
        };

        /// <summary>An error report for an exception in House Style.</summary>
        /// <param name="whatFailed">The plain sentence the writer saw ("House Style couldn't lint the project.").</param>
        /// <param name="userNote">What the writer was doing, if they said; null otherwise.</param>
        public static ReportPayload ForError(ReportContext ctx, Exception ex, string whatFailed, string userNote, bool automatic)
        {
            var secrets = Secrets(ctx);
            ex = ErrorKinds.Unwrap(ex);
            var r = Base(ctx, ReportPayload.KindError, automatic);
            r.WhatHappened = Text(RedactNames(Anonymize(whatFailed, ctx.Settings)), FeedbackLimits.Text, true, secrets);
            r.UserNote = Text(userNote, FeedbackLimits.Text, true, secrets);
            r.Exception = new ExceptionInfo
            {
                Type = Text(ex == null ? "Unknown" : ex.GetType().FullName, FeedbackLimits.ExceptionType, false, secrets) ?? "Unknown",
                Message = Text(ex == null ? "" : RedactNames(Anonymize(Diagnostics.WithoutQuotedText(ex.Message), ctx.Settings)), FeedbackLimits.ExceptionMessage, true, secrets) ?? "",
                Stack = Text(ex == null ? "" : RedactNames(Anonymize(Diagnostics.WithoutQuotedText(ex.ToString()), ctx.Settings)), FeedbackLimits.Stack, true, secrets) ?? ""
            };
            r.DiagLogTail = DiagTail(ctx.DiagLogPath, secrets, ctx.Settings);
            r.Settings = SafeSettings(ctx.Settings);
            return r;
        }

        /// <summary>A report the writer wrote (Report a Problem). Diagnostic details (log end and settings) only when asked.</summary>
        public static ReportPayload ForProblem(ReportContext ctx, string whatHappened, string expected, bool includeDiagnostics)
        {
            var secrets = Secrets(ctx);
            var r = Base(ctx, ReportPayload.KindProblem, false);
            r.WhatHappened = Text(whatHappened, FeedbackLimits.Text, true, secrets);
            r.Expected = Text(expected, FeedbackLimits.Text, true, secrets);
            if (includeDiagnostics)
            {
                r.DiagLogTail = DiagTail(ctx.DiagLogPath, secrets, ctx.Settings);
                r.Settings = SafeSettings(ctx.Settings);
            }
            else
            {
                r.FlareVersion = null;
                r.Os = null;
            }
            return r;
        }

        /// <summary>A false positive: the rule, the flagged words, the reason, and the sentence around them only when the writer ticks it.</summary>
        public static FalsePositivePayload ForFalsePositive(ReportContext ctx, string ruleId, string ruleName, string reason, string flaggedText, string context, string note)
        {
            var secrets = Secrets(ctx);
            return new FalsePositivePayload
            {
                ClientId = ctx.ClientId,
                PluginVersion = Version(ctx.PluginVersion),
                RuleId = RuleIdOf(ruleId),
                RuleName = Text(ruleName, FeedbackLimits.RuleName, false, secrets) ?? RuleIdOf(ruleId),
                Reason = Array.IndexOf(FalsePositiveReason.All, reason) >= 0 ? reason : FalsePositiveReason.TextCorrect,
                FlaggedText = Text(flaggedText, FeedbackLimits.FlaggedText, true, secrets) ?? "(empty)",
                Context = Text(context, FeedbackLimits.FpContext, true, secrets),
                Note = Text(note, FeedbackLimits.FpNote, true, secrets)
            };
        }

        /// <summary>The exact JSON that is sent: shown in Details and Preview, posted by <see cref="FeedbackClient"/>, saved in a report zip.</summary>
        public static string ToJson(object payload) { return JsonConvert.SerializeObject(payload, Json); }

        private static ReportPayload Base(ReportContext ctx, string kind, bool automatic)
        {
            return new ReportPayload
            {
                Kind = kind,
                ClientId = ctx.ClientId,
                PluginVersion = Version(ctx.PluginVersion),
                FlareVersion = Text(ctx.FlareVersion, FeedbackLimits.FlareVersion, false, null),
                Os = Text(ctx.Os, FeedbackLimits.Os, false, null),
                Automatic = automatic
            };
        }

        // ---- settings: a whitelist, never a blacklist ---------------------------------------------------------------

        /// <summary>
        /// The settings a report carries, copied one by one from fields that can't hold a secret or a person's name: on/off
        /// choices, numbers and model names. The API key, the gateway address, folder paths and the email address are never
        /// read; where they matter only whether they are set is sent. A new setting is left out until it is added here.
        /// </summary>
        public static SortedDictionary<string, object> SafeSettings(PluginSettings s)
        {
            var d = new SortedDictionary<string, object>(StringComparer.Ordinal);
            if (s == null) return d;
            d["provider"] = s.Provider.ToString();
            d["ai_off"] = s.AiOff;
            d["model"] = Text(s.Model, FeedbackLimits.SettingValue, false, null);
            d["fast_model"] = Text(s.FastModel, FeedbackLimits.SettingValue, false, null);
            d["deep_model"] = Text(s.DeepModel, FeedbackLimits.SettingValue, false, null);
            d["max_output"] = s.MaxTokens;   // not "max_tokens": the service blanks any setting named like a token
            d["timeout_seconds"] = s.TimeoutSeconds;
            d["parallelism"] = s.Parallelism;
            d["write_diagnostics"] = s.WriteDiagnostics;
            d["use_project_guide"] = s.UseProjectLayer;
            d["team_guide_count"] = CountLines(s.StyleLayers);
            d["team_feedback_folder_set"] = !string.IsNullOrEmpty(s.FeedbackFolder);
            d["custom_address"] = s.Provider == LlmProvider.Bifrost || !string.Equals((s.BaseUrl ?? "").TrimEnd('/'), PluginSettings.DefaultAnthropicBaseUrl, StringComparison.OrdinalIgnoreCase);
            d["llm_export_after_build"] = s.LlmExportAfterBuild;
            d["show_markers"] = s.ShowMarkers;
            d["live_check"] = s.LiveCheck;
            d["check_on_save"] = s.CheckOnSave;
            d["report_before_build"] = s.ReportBeforeBuild;
            d["project_view_by_file"] = s.ProjectViewByFile;
            d["auto_update"] = s.AutoUpdate;
            d["role"] = s.Role.HasValue ? s.Role.Value.ToString() : null;
            d["send_error_reports_automatically"] = s.SendErrorReportsAutomatically;
            return d;
        }

        private static int CountLines(string s)
        {
            var n = 0;
            foreach (var line in (s ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)) if (line.Trim().Length > 0) n++;
            return n;
        }

        // ---- text: limits, control characters, secrets ----------------------------------------------------------------

        /// <summary>The API key in effect, which must never be sent even if it ended up in a log line or an exception.</summary>
        private static List<string> Secrets(ReportContext ctx)
        {
            var list = new List<string>();
            try
            {
                var key = ctx.Settings == null ? null : ctx.Settings.ResolveApiKey();
                if (!string.IsNullOrEmpty(key) && key.Length >= 8) list.Add(key);
            }
            catch (Exception) { }
            var env = Environment.GetEnvironmentVariable(PluginSettings.EnvKeyName);
            if (!string.IsNullOrEmpty(env) && env.Length >= 8 && !list.Contains(env)) list.Add(env);
            var gateway = Environment.GetEnvironmentVariable(PluginSettings.GatewayEnvKeyName);
            if (!string.IsNullOrEmpty(gateway) && gateway.Length >= 8 && !list.Contains(gateway)) list.Add(gateway);
            return list;
        }

        /// <summary>Patterns of keys and tokens, with what each match becomes ($1 keeps a label, never the secret).</summary>
        private static readonly KeyValuePair<Regex, string>[] SecretPatterns =
        {
            Pattern(@"sk-ant-[A-Za-z0-9_\-]{8,}", "[removed]"),                                               // an Anthropic key
            Pattern(@"\bsk-[A-Za-z0-9_\-]{16,}", "[removed]"),                                                // other sk- keys
            Pattern(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]{12,}", "$1 [removed]"),
            Pattern(@"(?i)\b(x-api-key|api[_-]?key|key|authorization|token|secret|password|pwd)(\s*[:=]\s*""?)[^\s"",;&]{4,}", "$1$2[removed]"),
            Pattern(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", "[removed]"),          // a JWT
            Pattern(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16})\b", "[removed]"),
            Pattern(@"(?i)(https?://)[^/\s:@]+:[^/\s@]+@", "$1[removed]@"),                                   // user:password@ in an address
        };

        private static KeyValuePair<Regex, string> Pattern(string regex, string replacement)
        {
            return new KeyValuePair<Regex, string>(new Regex(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant), replacement);
        }

        private static readonly Regex AuthScheme = new Regex(
            @"(?i)\b(authorization(?:""|')?\s*[:=]\s*[""']?(?:token|bearer|basic|apikey)\s+)[^\s"",;&']+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NamedSecret = new Regex(
            @"(?i)((?:(?<![A-Za-z0-9])|(?<=(?-i:[a-z]))(?=(?-i:[A-Z])))(?:api[_\- ]?key|key|secret|token|password|passwd|pwd|vk))(?![A-Za-z0-9])([""']?\s*[:=]\s*[""']?)[^\s"",;&']+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The text with the value after a secret-type name (key, secret, token, password, pwd, api key, virtual key, with
        /// or without a prefix such as client_secret) and the value of an Authorization header replaced by [removed].
        /// The name stays. Covers named secrets in text House Style adds (log lines, exception text), not text the writer
        /// types.
        /// </summary>
        public static string RedactNames(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = AuthScheme.Replace(text, "$1[removed]");
            return NamedSecret.Replace(text, "$1$2[removed]");
        }

        /// <summary>The text with the key and anything that looks like a key, token or password replaced by [removed].</summary>
        public static string Redact(string text, IList<string> secrets)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (secrets != null) foreach (var s in secrets) if (!string.IsNullOrEmpty(s)) text = text.Replace(s, "[removed]");
            foreach (var p in SecretPatterns) text = p.Key.Replace(text, p.Value);
            return text;
        }

        /// <summary>
        /// Text as the service accepts it: control characters (other than tab and line breaks) removed, line breaks only
        /// where allowed, cut to <paramref name="max"/> (never inside a surrogate pair), secrets removed. Null or blank
        /// gives null (left out).
        /// </summary>
        public static string Text(string s, int max, bool multiline, IList<string> secrets)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = Redact(Strip(s, multiline), secrets);
            if (s.Trim().Length == 0) return null;
            return Cut(s, max);
        }

        private static string Strip(string s, bool multiline)
        {
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '\r' || c == '\n')
                {
                    if (multiline) sb.Append(c);
                    else if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    continue;
                }
                if (c == '\t') { sb.Append(c); continue; }
                if (c < 0x20 || c == 0x7f) continue;
                sb.Append(c);
            }
            return multiline ? sb.ToString() : sb.ToString().Trim();
        }

        /// <summary>The first <paramref name="max"/> characters, without splitting a surrogate pair.</summary>
        public static string Cut(string s, int max)
        {
            if (s == null || s.Length <= max) return s;
            var n = max;
            if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
            return s.Substring(0, n);
        }

        /// <summary>The last <paramref name="max"/> characters, starting at a line where possible, without splitting a surrogate pair.</summary>
        public static string Tail(string s, int max)
        {
            if (s == null || s.Length <= max) return s;
            var start = s.Length - max;
            var nl = s.IndexOf('\n', start);
            if (nl >= 0 && nl < s.Length - 1 && nl - start < 2000) start = nl + 1;
            if (start < s.Length && char.IsLowSurrogate(s[start])) start++;
            return s.Substring(start);
        }

        private static string Version(string v)
        {
            v = Text(v, FeedbackLimits.Version, false, null);
            if (v == null) return "0.0.0";
            var sb = new StringBuilder();
            foreach (var c in v) if (char.IsLetterOrDigit(c) && c < 128 || c == '.' || c == '+' || c == '-') sb.Append(c);
            var r = sb.ToString().TrimStart('.', '+', '-');
            return r.Length == 0 ? "0.0.0" : r;
        }

        /// <summary>A rule id as the service accepts it; findings not tied to a rule are sent as "ai:unlisted".</summary>
        public static string RuleIdOf(string ruleId)
        {
            var sb = new StringBuilder();
            foreach (var c in ruleId ?? "")
                if (c < 128 && (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == ':' || c == '/' || c == '-')) sb.Append(c);
            var r = sb.ToString().TrimStart('.', '_', ':', '/', '-');
            return r.Length == 0 ? "ai:unlisted" : Cut(r, FeedbackLimits.RuleId);
        }

        /// <summary>The end of diag.log (it is being written to, so it is read shared), cut to the service's limit and scrubbed.</summary>
        public static string DiagTail(string path, IList<string> secrets, PluginSettings settings = null)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var take = (int)Math.Min(fs.Length, FeedbackLimits.DiagLog * 3L);
                    fs.Seek(-take, SeekOrigin.End);
                    var buf = new byte[take];
                    var read = 0;
                    while (read < take) { var n = fs.Read(buf, read, take - read); if (n <= 0) break; read += n; }
                    // Skip a partial UTF-8 character at the cut.
                    var skip = 0;
                    while (skip < read && skip < 4 && (buf[skip] & 0xC0) == 0x80) skip++;
                    text = new UTF8Encoding(false, false).GetString(buf, skip, read - skip);
                }
                text = text.TrimStart((char)0xFEFF);   // the log file starts with a byte order mark
                text = Anonymize(Redact(RedactNames(WithoutContent(Strip(text, true))), secrets), settings);
                return Tail(text, FeedbackLimits.DiagLog);
            }
            catch (Exception) { return null; }
        }

        // ---- who and where: no names, addresses or folder paths ------------------------------------------------------

        private static readonly Regex Email = new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex WriterValue = new Regex(@"(?i)\b(writer\s*[=:]\s*)[^\s,;)]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex AnyProfile = new Regex(@"(?i)\b[A-Z]:\\Users\\[^\\/\s""]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The text without the writer's identity or folder paths: email addresses become [email], writer= values are
        /// removed, the configured team guide and feedback folders become [team guide] and [feedback folder], and the
        /// writer's own folders become %LOCALAPPDATA%, %APPDATA%, %DOCUMENTS% and %USERPROFILE% (any other C:\Users\name
        /// becomes C:\Users\[user]). Applied to everything House Style adds to a report, not to what the writer types.
        /// </summary>
        public static string Anonymize(string text, PluginSettings settings)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var places = new List<KeyValuePair<string, string>>();
            if (settings != null)
            {
                foreach (var line in (settings.StyleLayers ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    Place(places, line.Trim().TrimEnd('\\', '/'), "[team guide]");
                Place(places, (settings.FeedbackFolder ?? "").Trim().TrimEnd('\\', '/'), "[feedback folder]");
            }
            Place(places, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
            Place(places, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
            Place(places, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "%DOCUMENTS%");
            Place(places, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
            places.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));   // the longest (most specific) first
            foreach (var p in places)
                text = Regex.Replace(text, Regex.Escape(p.Key), p.Value.Replace("$", "$$"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            text = AnyProfile.Replace(text, m => m.Value.Substring(0, m.Value.IndexOf("\\Users\\", StringComparison.OrdinalIgnoreCase)) + "\\Users\\[user]");
            text = WriterValue.Replace(text, "$1[removed]");
            text = Email.Replace(text, "[email]");
            return text;
        }

        private static void Place(List<KeyValuePair<string, string>> places, string path, string placeholder)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 4 || path.IndexOf('{') >= 0) return;
            places.Add(new KeyValuePair<string, string>(path, placeholder));
            var forward = path.Replace('\\', '/');
            if (forward != path) places.Add(new KeyValuePair<string, string>(forward, placeholder));
        }

        private static readonly Regex EntryStart = new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  ", RegexOptions.CultureInvariant);

        /// <summary>
        /// A diag.log tail without document text. diag.log no longer gets any (it goes to diag-content.log), but a log
        /// written by an earlier version can still hold Diagnostics output (DIAG entries, with their continuation lines)
        /// and AI contract breaches that quote the text: those entries are dropped, and anything in quotation marks is
        /// replaced by "…".
        /// </summary>
        public static string WithoutContent(string log)
        {
            if (string.IsNullOrEmpty(log)) return log;
            var kept = new List<string>();
            var lines = log.Split('\n');
            // A tail starts mid-entry: lines before its first dated entry are dropped (they may continue a DIAG entry).
            var skipping = false;
            foreach (var l in lines) if (EntryStart.IsMatch(l)) { skipping = true; break; }
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var m = EntryStart.Match(line);
                if (m.Success)
                {
                    var message = line.Substring(m.Length);
                    skipping = message.StartsWith("DIAG", StringComparison.Ordinal) || message.Contains("contract breaches, retrying:");
                }
                if (skipping && !(i == lines.Length - 1 && line.Length == 0)) continue;
                kept.Add(Diagnostics.WithoutQuotedText(line));
            }
            return string.Join("\n", kept.ToArray());
        }

        /// <summary>The sentence of <paramref name="text"/> that holds <paramref name="excerpt"/> (for a false positive's opt-in context).</summary>
        public static string SentenceAround(string text, string excerpt)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = Regex.Replace(text, @"\s+", " ").Trim();
            var at = string.IsNullOrEmpty(excerpt) ? -1 : text.IndexOf(Regex.Replace(excerpt, @"\s+", " ").Trim(), StringComparison.Ordinal);
            if (at < 0) return Cut(text, FeedbackLimits.FpContext);
            var start = at;
            while (start > 0 && !IsSentenceEnd(text, start - 1)) start--;
            var end = at + excerpt.Length;
            while (end < text.Length && !IsSentenceEnd(text, end - 1)) end++;
            var s = text.Substring(start, Math.Min(text.Length, end) - start).Trim();
            return Cut(s, FeedbackLimits.FpContext);
        }

        private static bool IsSentenceEnd(string t, int i)
        {
            var c = t[i];
            return (c == '.' || c == '!' || c == '?') && (i + 1 >= t.Length || t[i + 1] == ' ');
        }

        /// <summary>The plugin's own version, for tests and tools: the informational version without "+commit".</summary>
        public static string VersionOf(Assembly a)
        {
            var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(a, typeof(AssemblyInformationalVersionAttribute));
            var v = attr != null ? attr.InformationalVersion : a.GetName().Version.ToString();
            var plus = v.IndexOf('+');
            return plus > 0 ? v.Substring(0, plus) : v;
        }
    }
}
