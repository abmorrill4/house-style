using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Actions
{
    public sealed class StyleFinding
    {
        /// <summary>"lint" (deterministic, docstyle) or "ai" (model judgment).</summary>
        public string Source { get; set; }
        public string Rule { get; set; }
        public string Severity { get; set; }
        /// <summary>Verbatim text the finding is about; always inside one editable text run.</summary>
        public string Excerpt { get; set; }
        /// <summary>Why, in one short sentence.</summary>
        public string Suggestion { get; set; }
        /// <summary>Exact text to put in place of Excerpt; null when the fix needs a rewrite.</summary>
        public string Replacement { get; set; }
        /// <summary>Up to 40 chars of run text before the excerpt — disambiguates repeats when re-locating.</summary>
        public string Before { get; set; }
        /// <summary>The body-level block the finding was found in (may be replaced by later edits).</summary>
        [Newtonsoft.Json.JsonIgnore]
        public System.Xml.XmlNode Block { get; set; }
        /// <summary>"google", "company", or "ai".</summary>
        public string Layer { get; set; }
        /// <summary>Guide page for the rule, when known.</summary>
        public string Link { get; set; }
        /// <summary>The guide rule this finding applies ("company:terminology/3"); null = not tied to a guide rule.</summary>
        public string RuleId { get; set; }
        /// <summary>Other rules that flagged the same words (lint only): "Turn off this rule" offers to turn them off too.</summary>
        public System.Collections.Generic.List<FlareAI.Core.Style.RuleRef> AlsoRules = new System.Collections.Generic.List<FlareAI.Core.Style.RuleRef>();
        /// <summary>The run (r1, r2, …) of the reviewed unit the finding quotes, when it came from a run-addressed review.</summary>
        public string RunId { get; set; }
        /// <summary>Layer and section of <see cref="RuleId"/>, for display ("Company: Terminology").</summary>
        public string RuleLayer { get; set; }
        public string RuleSection { get; set; }
        /// <summary>The cited rule's wording when the finding was shown: feedback records a fingerprint of it.</summary>
        public string RuleText { get; set; }
        /// <summary>File the finding lives in when it is not the checked topic (a snippet); null = the topic.</summary>
        public string DocumentPath { get; set; }
        public bool IsFixed { get; set; }
        /// <summary>
        /// Set when the text is a typed copy of this project variable's value. Not one-click: Flare 22 cannot redo a
        /// plugin-inserted variable (it crashes), so the author selects the text (Go to) and uses Insert ▸ Variable.
        /// </summary>
        public string Variable { get; set; }
        public bool CanFix { get { return Replacement != null && !IsFixed && !IsStale; } }

        /// <summary>Heading of the section the finding is in ("" before the first heading); set by <see cref="ReviewOrder"/>.</summary>
        [Newtonsoft.Json.JsonIgnore] public string Section { get; set; }
        /// <summary>Document order (sections, then position); set by <see cref="ReviewOrder"/>.</summary>
        [Newtonsoft.Json.JsonIgnore] public long Order { get; set; }
        /// <summary>The text the finding quotes is no longer in the document (edited since the review).</summary>
        [Newtonsoft.Json.JsonIgnore] public bool IsStale { get; set; }
        /// <summary>An applied fix was taken back (Ctrl+Z) but the text around it changed too: stays stale until the paragraph is checked again.</summary>
        [Newtonsoft.Json.JsonIgnore] public bool ContextChanged { get; set; }
        /// <summary>Its replacement was written into the document by Apply (so the document can take it back).</summary>
        [Newtonsoft.Json.JsonIgnore] public bool Written { get; set; }
        /// <summary>The writer's edit removed this text (a check found it). A later check can take it back (Ctrl+Z).</summary>
        [Newtonsoft.Json.JsonIgnore] public bool FixedByEdit { get; set; }
        /// <summary>The writer chose Mark as Fixed. No check takes it back; only Lint Again does.</summary>
        [Newtonsoft.Json.JsonIgnore] public bool MarkedFixed { get; set; }
        /// <summary>How often the excerpt was in its paragraph at the last check; -1 when not recorded.</summary>
        [Newtonsoft.Json.JsonIgnore] public int Seen { get; set; } = -1;
        /// <summary>How often the excerpt was in the file's body at the last check; -1 when not recorded.</summary>
        [Newtonsoft.Json.JsonIgnore] public int SeenInBody { get; set; } = -1;
        /// <summary>The writer asked the style guide owners to change this finding's rule and is waiting for an answer ("Request sent").</summary>
        [Newtonsoft.Json.JsonIgnore] public bool RequestSent { get; set; }

        /// <summary>
        /// What deciding on it takes: "mechanical" (exact rule fix), "wording" (AI-proposed exact text), "restructure"
        /// (needs a rewrite), "variable" (insert a variable), or "manual" (a structure/accessibility fix in the editor).
        /// </summary>
        public string Kind
        {
            get
            {
                if (Source == "structure") return "manual";
                if (Variable != null) return "variable";
                if (Replacement == null) return "restructure";
                return Source == "ai" ? "wording" : "mechanical";
            }
        }
    }

    /// <summary>Tolerant extraction of our two response contracts: a JSON findings array, or a &lt;revised&gt; block.</summary>
    public static class ResponseParser
    {
        public static List<StyleFinding> ParseFindings(string text)
        {
            var list = new List<StyleFinding>();
            if (string.IsNullOrEmpty(text)) return list;
            var s = StripFences(text).Trim();
            var start = s.IndexOf('[');
            var end = s.LastIndexOf(']');
            if (start < 0 || end <= start) throw new FormatException("Model did not return a JSON array.");
            var arr = JArray.Parse(s.Substring(start, end - start + 1));
            foreach (var item in arr)
            {
                var replacement = item["replacement"];
                list.Add(new StyleFinding
                {
                    Source = "ai", Layer = "ai", RuleId = (string)item["rule_id"],
                    Rule = (string)item["rule"] ?? "",
                    Severity = Severity.Normalize((string)item["severity"] ?? "suggestion"),
                    Excerpt = (string)item["excerpt"] ?? "",
                    Suggestion = (string)item["why"] ?? (string)item["suggestion"] ?? "",
                    Replacement = replacement == null || replacement.Type == JTokenType.Null ? null : (string)replacement
                });
            }
            return list;
        }

        /// <summary>The refine contract: {"replacement": "...", "why": "..."}. Key = replacement (null allowed), Value = why.</summary>
        public static KeyValuePair<string, string> ParseRefined(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new FormatException("Empty response.");
            var s = StripFences(text).Trim();
            var start = s.IndexOf('{');
            var end = s.LastIndexOf('}');
            if (start < 0 || end <= start) throw new FormatException("Model did not return a JSON object.");
            var o = JObject.Parse(s.Substring(start, end - start + 1));
            var r = o["replacement"];
            return new KeyValuePair<string, string>(r == null || r.Type == JTokenType.Null ? null : (string)r, (string)o["why"] ?? "");
        }

        public static string ParseRevised(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new FormatException("Empty response.");
            var s = StripFences(text);
            var open = s.IndexOf("<revised>", StringComparison.OrdinalIgnoreCase);
            var close = s.LastIndexOf("</revised>", StringComparison.OrdinalIgnoreCase);
            if (open < 0 || close <= open) throw new FormatException("Model did not return a <revised> block.");
            return s.Substring(open + "<revised>".Length, close - open - "<revised>".Length).Trim();
        }

        private static string StripFences(string s)
        {
            // models sometimes wrap output in ```xml ... ``` despite instructions
            var t = s.Trim();
            if (t.StartsWith("```"))
            {
                var nl = t.IndexOf('\n');
                if (nl > 0) t = t.Substring(nl + 1);
                if (t.EndsWith("```")) t = t.Substring(0, t.Length - 3);
            }
            return t;
        }
    }
}
