using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FlareAI.Core.Llm;
using FlareAI.TestKit.Llm;
using Newtonsoft.Json.Linq;

namespace FlareAI.Integration.Tests.Support
{
    /// <summary>
    /// Model answers for tests that are scripted rather than recorded: they read the request the way the model would
    /// (the numbered runs in the prompt) and answer with the review/rewrite contract, so the run ids are always right.
    /// </summary>
    public static class ScriptedReview
    {
        private static readonly Regex Run = new Regex("⟦(r\\d+)⟧(.*?)⟦/\\1⟧", RegexOptions.Singleline);

        /// <summary>One finding for the first run that contains <paramref name="excerpt"/>; nothing when no run does.</summary>
        public sealed class Flag
        {
            public string Excerpt, Rule = "Wording", RuleId = "general", Severity = "suggestion", Replacement, Why = "Say it more plainly.";
        }

        /// <summary>Answers every review request with these findings (each only in the unit that holds its text).</summary>
        public static ScriptedLlmClient Review(params Flag[] flags)
        {
            return new ScriptedLlmClient().When(r => r.Task == "review", r => new LlmResponse { Text = ReviewJson(r, flags), StopReason = "end_turn", Model = r.Model ?? "scripted" });
        }

        public static string ReviewJson(LlmRequest request, IEnumerable<Flag> flags)
        {
            var findings = new JArray();
            foreach (var flag in flags)
            {
                var run = RunWith(request.User, flag.Excerpt);
                if (run == null) continue;
                findings.Add(new JObject
                {
                    ["run_id"] = run, ["excerpt"] = flag.Excerpt, ["rule_id"] = flag.RuleId, ["rule"] = flag.Rule, ["severity"] = flag.Severity,
                    ["replacement"] = flag.Replacement == null ? JValue.CreateNull() : (JToken)flag.Replacement, ["why"] = flag.Why
                });
            }
            return new JObject { ["findings"] = findings }.ToString();
        }

        /// <summary>The id of the first marked run in the prompt that contains the text, or null.</summary>
        public static string RunWith(string prompt, string text)
        {
            foreach (Match m in Run.Matches(Strip(prompt)))
                if (m.Groups[2].Value.IndexOf(text, StringComparison.Ordinal) >= 0) return m.Groups[1].Value;
            return null;
        }

        private const string Example = "⟦r1⟧like this⟦/r1⟧";   // the prompt explains the markers with this example: it is not the text

        private static string Strip(string prompt) { return (prompt ?? "").Replace(Example, ""); }

        /// <summary>A rewrite answer: new text for the run that holds <paramref name="oldText"/> (everything else unchanged).</summary>
        public static Func<LlmRequest, LlmResponse> RewriteRun(string oldText, string newText)
        {
            return r =>
            {
                var changes = new JArray();
                var run = RunWith(r.User, oldText);
                if (run != null)
                {
                    var text = Regex.Match(Strip(r.User), "⟦" + run + "⟧(.*?)⟦/" + run + "⟧", RegexOptions.Singleline).Groups[1].Value;
                    changes.Add(new JObject { ["run_id"] = run, ["text"] = System.Net.WebUtility.HtmlDecode(text).Replace(oldText, newText) });
                }
                return new LlmResponse
                {
                    Text = new JObject { ["changes"] = changes, ["needs_restructure"] = false, ["note"] = "" }.ToString(), StopReason = "end_turn", Model = r.Model ?? "scripted"
                };
            };
        }

        /// <summary>A rewrite answer that names run ids and gives text, whatever the prompt says (for answers that break the contract).</summary>
        public static Func<LlmRequest, LlmResponse> RewriteWith(string runId, string text)
        {
            return r => new LlmResponse
            {
                Text = new JObject { ["changes"] = new JArray(new JObject { ["run_id"] = runId, ["text"] = text }), ["needs_restructure"] = false, ["note"] = "" }.ToString(),
                StopReason = "end_turn", Model = r.Model ?? "scripted"
            };
        }

        /// <summary>A restructure answer: the shared edit schema, with the XHTML in its "revised" field (structured outputs always return JSON).</summary>
        public static Func<LlmRequest, LlmResponse> Revised(string xhtml)
        {
            return r => new LlmResponse
            {
                Text = new JObject { ["findings"] = new JArray(), ["changes"] = new JArray(), ["needs_restructure"] = false, ["note"] = "", ["revised"] = xhtml }.ToString(),
                StopReason = "end_turn", Model = r.Model ?? "scripted"
            };
        }
    }
}
