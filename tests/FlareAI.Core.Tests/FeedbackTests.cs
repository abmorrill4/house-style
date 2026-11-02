using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using FlareAI.Core.Feedback;
using FlareAI.Core.Settings;
using FlareAI.TestKit.Feedback;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// In-app feedback (issue #124): what a report holds (never the key, a gateway secret or a folder path), the service's
    /// size limits and schema, the client against a local mock of the service (success, 429, 502, no network, then the
    /// zip fallback), and one report per error per session. Nothing here contacts the real service.
    /// </summary>
    public class FeedbackTests : IDisposable
    {
        private const string Key = "sk-ant-api03-SECRETSECRETSECRET-abcdefghijklmnop1234567890";
        private const string GatewaySecret = "gw-pass-9f8e7d6c5b4a";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hs-feedback-" + Guid.NewGuid().ToString("N"));

        public FeedbackTests() { Directory.CreateDirectory(_dir); }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

        /// <summary>The service's accepted fields (tools/feedback-worker/src/validate.ts): anything else is a 400.</summary>
        private static readonly string[] ReportKeys = { "kind", "client_id", "plugin_version", "flare_version", "os", "automatic", "what_happened", "expected", "user_note", "exception", "diag_log_tail", "settings" };
        private static readonly string[] FpKeys = { "client_id", "plugin_version", "rule_id", "rule_name", "reason", "flagged_text", "context", "note" };

        private ReportContext Context(string log = null)
        {
            var s = new PluginSettings
            {
                Provider = LlmProvider.Bifrost,
                BaseUrl = "https://writer:" + GatewaySecret + "@gateway.example.com/v1?token=" + GatewaySecret,
                StyleLayers = @"\\fileserver\Docs\Jane Doe\Team Guide",
                FeedbackFolder = @"C:\Users\jane.doe\OneDrive\Decisions",
                ReportEmail = "jane.doe@example.com",
                Model = "claude-sonnet-5-5"
            };
            s.SetApiKey(Key);
            var path = Path.Combine(_dir, "diag.log");
            File.WriteAllText(path, log ?? "2026-10-22 10:00:00.000  House Style starting\n", Encoding.UTF8);
            return new ReportContext { ClientId = "0f8fad5b-d9cb-469f-a165-70867728950e", PluginVersion = "0.9.1+abc123", FlareVersion = "MadCap Flare 22.1.0", Os = "Windows 11", DiagLogPath = path, Settings = s };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception Thrown(string message)
        {
            try { throw new InvalidOperationException(message); }
            catch (Exception ex) { return ex; }
        }

        // ---- what a report holds ------------------------------------------------------------------------------------

        [Fact]
        public void AnErrorReport_NeverHoldsTheKey_GatewaySecrets_FolderPaths_OrTheEmailAddress()
        {
            var log = "10:00  Request to https://writer:" + GatewaySecret + "@gateway.example.com failed\n" +
                      "10:01  x-api-key: " + Key + "\n" +
                      "10:02  Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789\n" +
                      "10:03  retrying with key=" + Key.Substring(7) + "\n" +
                      "10:04  password=hunter2hunter2\n" +
                      "10:05  Lint topic :: NullReferenceException\n";
            var ctx = Context(log);
            var ex = Thrown("Request failed for " + Key + " at gateway token=" + GatewaySecret);
            var json = ReportBuilder.ToJson(ReportBuilder.ForError(ctx, ex, "House Style couldn't lint the topic.", "I pasted my key " + Key + " here", false));

            Assert.DoesNotContain(Key, json);
            Assert.DoesNotContain(Key.Substring(7, 20), json);
            Assert.DoesNotContain(GatewaySecret, json);
            Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789", json);
            Assert.DoesNotContain("hunter2", json);
            Assert.DoesNotContain("gateway.example.com/v1", json);
            Assert.DoesNotContain("Jane Doe", json);
            Assert.DoesNotContain("jane.doe", json);
            Assert.DoesNotContain(ctx.Settings.ProtectedApiKey, json);
            Assert.Contains("[removed]", json);
            Assert.Contains("Lint topic :: NullReferenceException", json);   // the rest of the log is there

            var o = JObject.Parse(json);
            Assert.Equal("error", (string)o["kind"]);
            Assert.Equal("System.InvalidOperationException", (string)o["exception"]["type"]);
            Assert.Contains("FeedbackTests.Thrown", (string)o["exception"]["stack"]);
            Assert.Equal("0.9.1+abc123", (string)o["plugin_version"]);
            Assert.False((bool)o["automatic"]);
        }

        [Fact]
        public void AReport_HasNoEmailAddress_WriterName_OrFolderPath()
        {
            const string Email = "sentinel.writer@example-corp.com";
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var ctx = Context(
                "2026-10-22 10:00:00.000  Team: writer=" + Email + " guide=" + @"\\fileserver\Docs\Jane Doe\Team Guide" + " feedback=" + @"C:\Users\jane.doe\OneDrive\Decisions" + "\n" +
                "2026-10-22 10:00:01.000  Rule set to off in " + roaming + @"\FlareAI\StyleGuide\Content\Resources\StyleGuide\Company\Terms.htm" + "\n" +
                "2026-10-22 10:00:02.000  Markers in " + local + @"\Temp\FlareAI-markers\m1.css; practice project in " + docs + @"\House Style Practice" + "\n" +
                "2026-10-22 10:00:03.000  Opened " + profile + @"\Projects\Bank\Content\Transfers.htm and C:\Users\otherperson\Desktop\x.htm (writer: jdoe-sentinel)" + "\n" +
                "2026-10-22 10:00:04.000  Lint topic done\n");
            Exception ex;
            try { throw new IOException("Couldn't write " + profile + @"\Projects\Bank\Content\Transfers.htm for " + Email); }
            catch (Exception e) { ex = e; }
            var reports = new[]
            {
                ReportBuilder.ToJson(ReportBuilder.ForError(ctx, ex, "House Style couldn't write to " + profile + @"\Projects\Bank.", null, false)),
                ReportBuilder.ToJson(ReportBuilder.ForProblem(ctx, "The pane went blank.", null, true))
            };
            foreach (var json in reports)
            {
                // Compare the values as sent, not their JSON escaping.
                var text = string.Join("\n", JObject.Parse(json).Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v).ToArray());
                foreach (var gone in new[] { Email, "sentinel.writer", profile, roaming, local, docs, @"\\fileserver", "Jane Doe", "jane.doe", "otherperson", "jdoe-sentinel", Environment.UserName + @"\" })
                    Assert.True(text.IndexOf(gone, StringComparison.OrdinalIgnoreCase) < 0, "\"" + gone + "\" is in the report:\n" + text);
                Assert.Contains("Lint topic done", text);
            }
            var log = (string)JObject.Parse(reports[1])["diag_log_tail"];
            Assert.Contains("[team guide]", log);
            Assert.Contains("[feedback folder]", log);
            Assert.Contains(@"%APPDATA%\FlareAI\StyleGuide", log);
            Assert.Contains(@"%LOCALAPPDATA%\Temp", log);
            Assert.Contains(@"%DOCUMENTS%\House Style Practice", log);
            Assert.Contains(@"%USERPROFILE%\Projects\Bank", log);
            Assert.Contains(@"C:\Users\[user]\Desktop", log);
            Assert.Contains("writer=[removed]", log);
            Assert.Contains("[email]", (string)JObject.Parse(reports[0])["exception"]["message"]);
            // What the writer types is theirs to send.
            Assert.Contains(Email, ReportBuilder.ToJson(ReportBuilder.ForProblem(ctx, "Write to " + Email + ".", null, false)));
        }

        [Fact]
        public void Settings_AreAWhitelist_OfHarmlessValues()
        {
            var s = Context().Settings;
            var settings = ReportBuilder.SafeSettings(s);
            Assert.Equal(new[]
            {
                "ai_off", "auto_update", "check_on_save", "custom_address", "deep_model", "fast_model", "live_check", "llm_export_after_build",
                "max_output", "model", "parallelism", "project_view_by_file", "provider", "report_before_build", "role",
                "send_error_reports_automatically", "show_markers", "team_feedback_folder_set", "team_guide_count", "timeout_seconds",
                "use_project_guide", "write_diagnostics"
            }, settings.Keys.ToArray());
            foreach (var kv in settings)
            {
                Assert.True(kv.Value == null || kv.Value is string || kv.Value is bool || kv.Value is int, kv.Key + " is a " + (kv.Value == null ? "null" : kv.Value.GetType().Name));
                Assert.DoesNotMatch("(?i)(key|token|secret|password|passwd|pwd|credential|auth|cookie|session|signature|cert)", kv.Key);   // names the service would blank
                Assert.Matches("^[A-Za-z0-9_.-]{1,64}$", kv.Key);
            }
            Assert.Equal(true, settings["custom_address"]);
            Assert.Equal(1, settings["team_guide_count"]);
            Assert.Equal(true, settings["team_feedback_folder_set"]);
            // Every PluginSettings property is either sent (renamed) or deliberately left out: a new one is a decision.
            var reviewed = new[] { "Provider", "AiOff", "BaseUrl", "Model", "MaxTokens", "TimeoutSeconds", "ProtectedApiKey", "WriteDiagnostics", "DebugLogging", "StyleLayers",
                                   "UseProjectLayer", "LlmExportAfterBuild", "ShowMarkers", "LiveCheck", "CheckOnSave", "ReportBeforeBuild", "FastModel", "DeepModel",
                                   "Parallelism", "ProjectViewByFile", "PaneWidthTipDismissed", "LintChecks", "AutoUpdate", "Role", "FeedbackFolder", "ClientId", "FeedbackUrl",
                                   "SendErrorReportsAutomatically", "ReportEmail", "EffectiveFeedbackUrl", "HasStoredKey" };
            var props = typeof(PluginSettings).GetProperties().Where(p => p.DeclaringType == typeof(PluginSettings) && !p.GetGetMethod().IsStatic).Select(p => p.Name).ToList();
            Assert.Empty(props.Except(reviewed));
        }

        [Fact]
        public void AProblemReport_WithoutDiagnostics_HasOnlyTheWritersWords()
        {
            var ctx = Context("10:00  DIAG Selection text: \"Confidential paragraph\"\n");
            var o = JObject.Parse(ReportBuilder.ToJson(ReportBuilder.ForProblem(ctx, "The pane went blank.", "My issues.", false)));
            Assert.Equal(new[] { "automatic", "client_id", "expected", "kind", "plugin_version", "what_happened" }, o.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            var full = JObject.Parse(ReportBuilder.ToJson(ReportBuilder.ForProblem(ctx, "The pane went blank.", null, true)));
            Assert.NotNull(full["diag_log_tail"]);
            Assert.NotNull(full["settings"]);
            Assert.Null(full["expected"]);   // empty fields are left out
        }

        [Fact]
        public void Reports_UseOnlyTheServicesFields()
        {
            var ctx = Context();
            foreach (var r in new object[] { ReportBuilder.ForError(ctx, Thrown("x"), "x", "note", true), ReportBuilder.ForProblem(ctx, "x", "y", true) })
                foreach (var p in JObject.Parse(ReportBuilder.ToJson(r)).Properties()) Assert.Contains(p.Name, ReportKeys);
            var fp = JObject.Parse(ReportBuilder.ToJson(ReportBuilder.ForFalsePositive(ctx, "google:quotation-marks/2", "Put periods inside quotation marks", FalsePositiveReason.ChangeWrong, "feeding.", "Care and feeding.", "n")));
            foreach (var p in fp.Properties()) Assert.Contains(p.Name, FpKeys);
            Assert.Equal("change_wrong", (string)fp["reason"]);
            Assert.Equal("0.9.1+abc123", (string)fp["plugin_version"]);
            var noContext = JObject.Parse(ReportBuilder.ToJson(ReportBuilder.ForFalsePositive(ctx, null, "General", FalsePositiveReason.TextCorrect, "x", null, "")));
            Assert.Null(noContext["context"]);
            Assert.Null(noContext["note"]);
            Assert.Equal("ai:unlisted", (string)noContext["rule_id"]);
        }

        // ---- size limits --------------------------------------------------------------------------------------------

        [Fact]
        public void EveryText_IsCutToTheServicesLimits()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 6000; i++) sb.Append("2026-10-22 10:00:00.000  line " + i + " of the log\n");
            sb.Append("THE LAST LINE\n");
            var ctx = Context(sb.ToString());
            ctx.FlareVersion = new string('9', 200);
            ctx.Os = "Windows\r\n11 " + new string('x', 300);
            var long1 = new string('a', 10000);
            var ex = Thrown(new string('m', 5000));
            var o = JObject.Parse(ReportBuilder.ToJson(ReportBuilder.ForError(ctx, ex, long1, long1 + "\u0001\u0007", true)));
            Assert.Equal(FeedbackLimits.Text, ((string)o["user_note"]).Length);
            Assert.Equal(FeedbackLimits.Text, ((string)o["what_happened"]).Length);
            Assert.Equal(FeedbackLimits.ExceptionMessage, ((string)o["exception"]["message"]).Length);
            Assert.True(((string)o["exception"]["stack"]).Length <= FeedbackLimits.Stack);
            Assert.Equal(FeedbackLimits.FlareVersion, ((string)o["flare_version"]).Length);
            var os = (string)o["os"];
            Assert.True(os.Length <= FeedbackLimits.Os);
            Assert.DoesNotContain("\n", os);
            var log = (string)o["diag_log_tail"];
            Assert.True(log.Length <= FeedbackLimits.DiagLog, log.Length + " characters");
            Assert.True(log.Length > FeedbackLimits.DiagLog - 2000);
            Assert.EndsWith("THE LAST LINE\n", log);
            Assert.StartsWith("2026-10-22", log);   // starts at a whole line
            Assert.DoesNotContain("\u0001", (string)o["user_note"] + log);

            var fp = ReportBuilder.ForFalsePositive(ctx, "company:terms/" + new string('z', 300), new string('n', 500) + "\nsecond line", FalsePositiveReason.RuleDoesntFit,
                                                     new string('f', 500), new string('c', 5000), new string('o', 5000));
            Assert.Equal(FeedbackLimits.RuleId, fp.RuleId.Length);
            Assert.Equal(FeedbackLimits.RuleName, fp.RuleName.Length);
            Assert.DoesNotContain("\n", fp.RuleName);
            Assert.Equal(FeedbackLimits.FlaggedText, fp.FlaggedText.Length);
            Assert.Equal(FeedbackLimits.FpContext, fp.Context.Length);
            Assert.Equal(FeedbackLimits.FpNote, fp.Note.Length);
        }

        [Fact]
        public void ACut_NeverSplitsASurrogatePair()
        {
            var s = new string('a', 199) + "\U0001F600";   // the emoji straddles the 200 limit
            Assert.Equal(199, ReportBuilder.Cut(s, 200).Length);
            Assert.False(char.IsHighSurrogate(ReportBuilder.Cut(s, 200).Last()));
        }

        [Fact]
        public void TheSentenceAroundTheFlaggedWords_IsJustThatSentence()
        {
            Assert.Equal("Care and feeding of the pump.", ReportBuilder.SentenceAround("Read this first. Care and feeding of the pump. Then stop.", "feeding"));
            Assert.Equal("Whole text", ReportBuilder.SentenceAround("Whole text", "absent"));
        }

        // ---- the client against a local mock --------------------------------------------------------------------------

        [Fact]
        public void TheClient_PostsJsonWithTheHeader_AndReadsTheReference()
        {
            using (var server = new MockFeedbackServer())
            {
                var ctx = Context();
                var report = ReportBuilder.ForProblem(ctx, "The pane went blank.", null, false);
                var r = new FeedbackClient(server.BaseUrl + "/").SendReport(report);
                Assert.True(r.Ok, r.ToString());
                Assert.Equal("#1", r.Reference);
                Assert.False(r.Duplicate);
                var req = server.Requests.Single();
                Assert.Equal("POST", req.Method);
                Assert.Equal("/v1/report", req.Path);
                Assert.Equal("1", req.HouseStyleHeader);
                Assert.StartsWith("application/json", req.ContentType);
                Assert.Null(req.ContentEncoding);   // small: not compressed
                Assert.Equal(ReportBuilder.ToJson(report), req.Body);

                var fp = new FeedbackClient(server.BaseUrl).SendFalsePositive(ReportBuilder.ForFalsePositive(ctx, "google:x/1", "X", FalsePositiveReason.TextCorrect, "words", null, null));
                Assert.True(fp.Ok);
                Assert.Equal("/v1/false-positive", server.Requests.Last().Path);
            }
        }

        [Fact]
        public void ABigReport_IsGzipped_AndArrivesWhole()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 2000; i++) sb.Append("10:00  line " + i + "\n");
            using (var server = new MockFeedbackServer())
            {
                var report = ReportBuilder.ForError(Context(sb.ToString()), Thrown("boom"), "House Style couldn't lint the topic.", null, true);
                var r = new FeedbackClient(server.BaseUrl).SendReport(report);
                Assert.True(r.Ok, r.ToString());
                var req = server.Requests.Single();
                Assert.Equal("gzip", req.ContentEncoding);
                Assert.Equal(ReportBuilder.ToJson(report), req.Body);
            }
        }

        [Fact]
        public void RateLimited_ServerErrors_AndRejections_AreTold_Apart()
        {
            using (var server = new MockFeedbackServer())
            {
                var client = new FeedbackClient(server.BaseUrl);
                var report = ReportBuilder.ForProblem(Context(), "x", null, false);

                server.Respond = q => MockFeedbackServer.Answer(429, "{\"ok\":false,\"error\":\"Too many reports.\",\"retry_after\":60}", "Retry-After", "60");
                var r = client.SendReport(report);
                Assert.False(r.Ok);
                Assert.Equal(FeedbackFailure.RateLimited, r.Failure);
                Assert.Equal(60, r.RetryAfterSeconds);
                Assert.True(r.ShouldSaveInstead);

                server.Respond = q => MockFeedbackServer.Answer(502, "{\"ok\":false,\"error\":\"GitHub couldn't be reached.\"}");
                r = client.SendReport(report);
                Assert.Equal(FeedbackFailure.Server, r.Failure);
                Assert.Equal("GitHub couldn't be reached.", r.Error);
                Assert.True(r.ShouldSaveInstead);

                server.Respond = q => MockFeedbackServer.Answer(400, "{\"ok\":false,\"error\":\"Unknown field \\\"x\\\".\"}");
                r = client.SendReport(report);
                Assert.Equal(FeedbackFailure.Rejected, r.Failure);
                Assert.False(r.ShouldSaveInstead);
            }
        }

        [Fact]
        public void NoNetwork_FallsBackToAZip_WithTheExactReport()
        {
            var ctx = Context("10:00  House Style starting\n10:01  Lint topic :: boom\n");
            var json = ReportBuilder.ToJson(ReportBuilder.ForError(ctx, Thrown("boom"), "House Style couldn't lint the topic.", null, false));
            var r = new FeedbackClient(MockFeedbackServer.DeadUrl()) { TimeoutMs = 3000 }.Post("/v1/report", json);
            Assert.False(r.Ok);
            Assert.Equal(FeedbackFailure.Network, r.Failure);
            Assert.True(r.ShouldSaveInstead);

            var zip = FlareAI.Flare.ReportZip.Save(json, Path.Combine(_dir, "House Style Reports"));
            Assert.True(File.Exists(zip));
            Assert.StartsWith(Path.Combine(_dir, "House Style Reports"), zip);
            using (var a = ZipFile.OpenRead(zip))
            {
                Assert.Equal(new[] { "diag-log-tail.txt", "report.json" }, a.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray());
                using (var s = new StreamReader(a.GetEntry("report.json").Open())) Assert.Equal(json, s.ReadToEnd());
                using (var s = new StreamReader(a.GetEntry("diag-log-tail.txt").Open())) Assert.Contains("Lint topic :: boom", s.ReadToEnd());
            }
            Assert.NotEqual(zip, FlareAI.Flare.ReportZip.Save(json, Path.Combine(_dir, "House Style Reports")));   // a second save doesn't overwrite

            Assert.Null(FlareAI.Flare.ReportZip.MailtoLink("", zip));   // no address: the folder opens instead
            var mail = FlareAI.Flare.ReportZip.MailtoLink("docs-tools@example.com", zip);
            Assert.StartsWith("mailto:docs-tools@example.com?subject=House%20Style%20report&body=", mail);
            Assert.Contains(Uri.EscapeDataString(zip), mail);
        }

        [Fact]
        public void ANonHttpsAddress_IsRefused_WithoutSending()
        {
            var r = new FeedbackClient("http://example.com").Post("/v1/report", "{}");
            Assert.False(r.Ok);
            Assert.Equal(FeedbackFailure.Network, r.Failure);
            Assert.Contains("non-HTTPS", r.Error);
        }

        // ---- one report per error per session ---------------------------------------------------------------------

        [Fact]
        public void TheSameError_IsOfferedOncePerSession()
        {
            var session = new SessionReports();
            var first = Thrown("one");
            var again = Thrown("two, a different message");
            Exception other;
            try { int.Parse("x"); other = null; } catch (Exception ex) { other = ex; }
            Assert.Equal(ErrorKinds.Signature(first), ErrorKinds.Signature(again));
            Assert.True(session.TryClaim(first));
            Assert.False(session.TryClaim(again));
            Assert.True(session.TryClaim(other));
            Assert.Contains("FlareAI.Core.Tests.FeedbackTests.Thrown", ErrorKinds.Signature(first));
        }

        [Fact]
        public void LockedFiles_Network_AndAiErrors_AreNotBugs()
        {
            Assert.True(ErrorKinds.IsExpected(new IOException("locked")));
            Assert.True(ErrorKinds.IsExpected(new UnauthorizedAccessException()));
            Assert.True(ErrorKinds.IsExpected(new System.Net.WebException("offline")));
            Assert.True(ErrorKinds.IsExpected(new FlareAI.Core.Style.GuideWriteException(@"C:\guide", new IOException("read-only"))));
            Assert.True(ErrorKinds.IsExpected(new System.Reflection.TargetInvocationException(new IOException("locked"))));
            Assert.True(ErrorKinds.IsBug(new NullReferenceException()));
            Assert.True(ErrorKinds.IsBug(new InvalidOperationException()));
            Assert.True(ErrorKinds.ThrownByUs(Thrown("ours")));
            Assert.False(ErrorKinds.ThrownByUs(new InvalidOperationException("never thrown")));
        }

        [Fact]
        public void TheClientId_IsAGuid_KeptForTheSession_AndSavedOnceSettingsExist()
        {
            var old = Paths.Root;
            Paths.Root = Path.Combine(_dir, "data");
            try
            {
                var id = PluginSettings.EnsureClientId();
                Guid g;
                Assert.True(Guid.TryParseExact(id, "D", out g));
                Assert.Equal(id, PluginSettings.EnsureClientId());
                Assert.False(File.Exists(PluginSettings.SettingsPath));   // no settings file yet: first-run setup still shows
                new PluginSettings().Save();
                Assert.Equal(id, PluginSettings.EnsureClientId());
                Assert.Equal(id, PluginSettings.Load().ClientId);
            }
            finally { Paths.Root = old; }
        }

        [Fact]
        public void TheServiceAddress_IsTheLiveOne_UnlessTheEnvironmentPointsElsewhere()
        {
            var old = Environment.GetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar, null);
                Assert.Equal(PluginSettings.DefaultFeedbackUrl, new PluginSettings().EffectiveFeedbackUrl);
                Assert.StartsWith("https://", PluginSettings.DefaultFeedbackUrl);
                Environment.SetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar, "http://localhost:1234/");
                Assert.Equal("http://localhost:1234", new PluginSettings().EffectiveFeedbackUrl);
            }
            finally { Environment.SetEnvironmentVariable(PluginSettings.FeedbackUrlEnvVar, old); }
        }
    }

    /// <summary>The sequential collection: these tests change process-wide logging state (Paths.Root, Diagnostics).</summary>
    [CollectionDefinition("Logging", DisableParallelization = true)]
    public class LoggingCollection { }

    /// <summary>
    /// Issue #124 promises a report never carries topic text the writer didn't type: whatever House Style logs that
    /// quotes a topic goes to diag-content.log, and the diag.log tail that goes with a report has none of it.
    /// </summary>
    [Collection("Logging")]
    public class TopicTextNeverSentTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hs-notext-" + Guid.NewGuid().ToString("N"));

        public TopicTextNeverSentTests() { Directory.CreateDirectory(_dir); }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

        // ---- topic text never goes out ------------------------------------------------------------------------------

        [Fact]
        public void TopicText_LoggedOnTheWay_NeverReachesAReport()
        {
            const string Sentinel = "zebraquartz";   // lower case: it finishes the sentence begun before the bold label
            var oldRoot = Paths.Root; var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
            Paths.Root = Path.Combine(_dir, "data"); Diagnostics.Sink = null; Diagnostics.Enabled = true;
            try
            {
                // A rewrite whose AI answer breaks the format: the breach quotes the topic's words.
                var doc = new FakeDocumentAdapter("sample-topic.htm");
                var p = FlareAI.Core.Editing.XhtmlFragment.ParseInto("<p>Remove a transfer from the <b>Transfers</b> " + Sentinel + ". Your transfer is ready to go!</p>", doc.Document)[0];
                doc.Document.SelectSingleNode("//body").AppendChild(p);
                doc.SelectXPath("//body/p[last()]/text()[2]");
                var llm = new FakeLlmClient(r => FakeLlmClient.EditRuns(r, t => t.Replace(" " + Sentinel + ". Your transfer is ready to go!", "screen. Your transfer is ready.")));
                Diagnostics.Enabled = true;   // FakeLlmClient turns logging off
                new FlareAI.Core.Actions.ActionRunner(llm, new FlareAI.Core.Actions.PromptBuilder(null), 1024).Rewrite(doc, null);
                Assert.Equal(2, llm.Requests.Count);   // the breach happened (and was retried)

                // An exception whose message quotes the text, logged and then reported.
                Exception quoting = null;
                try { FlareAI.Core.Editing.XhtmlFragment.ParseInto(Sentinel + " loose <p>x</p>", doc.Document); }
                catch (Exception ex) { quoting = ex; Diagnostics.Log("Rewrite", ex); }
                Assert.NotNull(quoting);

                // What the Diagnostics command writes (selection text and block XML).
                Diagnostics.LogContent("DIAG Selection text: \"" + Sentinel + "\"");
                Diagnostics.LogContent("DIAG first block XML: " + p.OuterXml);

                Assert.Contains(Sentinel, File.ReadAllText(Paths.DiagContentLog));     // kept for support, on this computer only
                Assert.DoesNotContain(Sentinel, File.ReadAllText(Paths.DiagLog));       // diag.log has events, counts and stacks only

                // A diag.log written by an earlier version, with the Diagnostics command's text in it.
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                File.AppendAllText(Paths.DiagLog, stamp + "  DIAG\nSelection text: \"" + Sentinel + "\"\n  <p> " + Sentinel + " page\n" +
                                                  stamp + "  DIAG first block XML: <p>" + Sentinel + "</p>\n" +
                                                  stamp + "  rewrite: contract breaches, retrying: Run r3 must still start with \"" + Sentinel + ".\"\n" +
                                                  stamp + "  Lint topic done\n");

                var ctx = new ReportContext { ClientId = "0f8fad5b-d9cb-469f-a165-70867728950e", PluginVersion = "0.9.1", Settings = new PluginSettings() };
                ctx.DiagLogPath = Paths.DiagLog;
                var error = ReportBuilder.ToJson(ReportBuilder.ForError(ctx, quoting, "House Style couldn't rewrite the text.", null, true));
                var problem = ReportBuilder.ToJson(ReportBuilder.ForProblem(ctx, "The rewrite failed.", null, true));
                Assert.DoesNotContain(Sentinel, error);
                Assert.DoesNotContain(Sentinel, problem);
                Assert.Contains("Lint topic done", error);                                  // the rest of the log is there
                Assert.Contains("XmlException", error);
                // The writer's own words do go, even when they quote the topic.
                Assert.Contains(Sentinel, ReportBuilder.ToJson(ReportBuilder.ForProblem(ctx, "It flagged " + Sentinel + ".", null, false)));
            }
            finally { Paths.Root = oldRoot; Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled; }
        }

    }
}