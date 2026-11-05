using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// The editable text of a unit of work (one block, or a few), numbered r1, r2, …: what the model sees and the only
    /// thing it may refer to or change. The model never writes markup; it names a run and gives text.
    /// </summary>
    public sealed class RunMap
    {
        public List<string> Ids { get; private set; }
        public Dictionary<string, XmlText> Runs { get; private set; }
        /// <summary>Each run's text when the map was made: answers are checked against this, off the UI thread.</summary>
        public Dictionary<string, string> Texts { get; private set; }
        /// <summary>The unit as XHTML with every editable run marked ⟦r3⟧text⟦/r3⟧ — context and addresses in one.</summary>
        public string Marked { get; private set; }

        public static RunMap Of(IList<XmlNode> blocks)
        {
            var map = new RunMap { Ids = new List<string>(), Runs = new Dictionary<string, XmlText>(StringComparer.Ordinal), Texts = new Dictionary<string, string>(StringComparer.Ordinal) };
            var live = TextRuns.Of(blocks).Where(r => r.Value.Trim().Length > 0).ToList();
            // Mark a clone so the live document is untouched: the clone's runs come in the same order.
            var clones = blocks.Select(b => b.CloneNode(true)).ToList();
            var cloneRuns = TextRuns.Of(clones).Where(r => r.Value.Trim().Length > 0).ToList();
            for (var i = 0; i < live.Count && i < cloneRuns.Count; i++)
            {
                var id = "r" + (i + 1);
                map.Ids.Add(id);
                map.Runs[id] = live[i];
                map.Texts[id] = live[i].Value;
                cloneRuns[i].Value = "⟦" + id + "⟧" + cloneRuns[i].Value + "⟦/" + id + "⟧";
            }
            // As the author reads it: pending deletions gone, pending insertions plain text (runs map one-to-one).
            map.Marked = XhtmlFragment.Serialize(AcceptedView.Of(clones));
            return map;
        }
    }

    /// <summary>
    /// Output contracts: one JSON schema per task, enforced by the API (structured outputs), plus the checks a schema
    /// can't express (an excerpt is verbatim in its run, replacement text has no markup). Enums carry the closed sets:
    /// run ids of this unit, rule ids of this guide, severities.
    /// </summary>
    public static class Contracts
    {
        public static readonly string[] Severities = { "error", "warning", "suggestion" };

        // ---- schema helpers ----------------------------------------------------------------------

        private static JObject Obj(params KeyValuePair<string, JObject>[] props)
        {
            var p = new JObject();
            foreach (var kv in props) p[kv.Key] = kv.Value;
            return new JObject
            {
                ["type"] = "object", ["properties"] = p, ["additionalProperties"] = false,
                ["required"] = new JArray(props.Select(kv => (object)kv.Key).ToArray())
            };
        }

        private static KeyValuePair<string, JObject> P(string name, JObject schema) { return new KeyValuePair<string, JObject>(name, schema); }
        private static JObject Str(string description = null) { var o = new JObject { ["type"] = "string" }; if (description != null) o["description"] = description; return o; }
        private static JObject NullableStr(string description) { return new JObject { ["anyOf"] = new JArray(new JObject { ["type"] = "string" }, new JObject { ["type"] = "null" }), ["description"] = description }; }
        private static JObject Bool(string description) { return new JObject { ["type"] = "boolean", ["description"] = description }; }
        private static JObject Enum(IEnumerable<string> values, string description = null) { var o = new JObject { ["type"] = "string", ["enum"] = new JArray(values.Distinct().Select(v => (object)v).ToArray()) }; if (description != null) o["description"] = description; return o; }
        private static JObject Arr(JObject items) { return new JObject { ["type"] = "array", ["items"] = items }; }

        // ---- review ------------------------------------------------------------------------------

        /// <summary>
        /// The one output contract of the editing tasks (review, rewrite, restructure, draft). The structured-output schema is
        /// part of the cached prompt prefix (it sits ahead of the system prompt), so a schema per task meant a 35k-token cache
        /// write per task: measured, review 35,075 tokens, rewrite 34,891, restructure 34,440, each its own entry. With one
        /// schema and the same system prompt, the tasks share one cache entry; what differs (the task's instructions and
        /// which fields to fill) comes after it, in the user message. Each task fills its own fields and leaves the others
        /// empty (a few output tokens). The schema is the same for every call: per-unit enums (run ids, rule ids) would make
        /// every call write the cache instead of reading it, so run and rule ids are checked in code instead
        /// (<see cref="ReadReview"/>, <see cref="KeepCitable"/>, <see cref="ReadRewrite"/>).
        /// </summary>
        public static readonly JObject EditSchema = Obj(
            P("findings", Arr(Obj(
                P("run_id", Str("The run the problem is in, for example r3.")),
                P("excerpt", Str("Exact text copied from that run: the shortest unique span that has the problem.")),
                P("rule_id", Str("The id of the style-guide rule applied, from the [brackets] in the guide; \"general\" only when no rule covers it.")),
                P("rule", Str("2-4 word name of the problem.")),
                P("severity", Enum(Severities)),
                P("replacement", NullableStr("Exact plain text to put in place of the excerpt, or null if it needs a rewrite.")),
                P("why", Str("At most 15 words."))))),
            P("changes", Arr(Obj(
                P("run_id", Str("The run to change, for example r3.")),
                P("text", Str("The complete new text of that run: plain text, no markup, no run markers."))))),
            P("needs_restructure", Bool("True only if the task can't be done by changing run text (for example splitting one step into two).")),
            P("note", Str("Empty, or one sentence: what restructuring is needed, or what information only the author can supply.")),
            P("revised", Str("The revised XHTML fragment, when the request asks for one; otherwise empty.")));

        /// <summary>Which fields of <see cref="EditSchema"/> each task fills: appended to its user message (after the cached prefix).</summary>
        public const string ReviewFields = "Answer in \"findings\" (an empty list when nothing needs changing). Leave \"changes\" empty, \"needs_restructure\" false, and \"note\" and \"revised\" empty.";
        public const string RewriteFields = "Answer in \"changes\", \"needs_restructure\" and \"note\". Leave \"findings\" empty and \"revised\" empty.";
        public const string RestructureFields = "Answer in \"revised\": the whole revised fragment as XHTML (no <revised> wrapper needed). Leave \"findings\" and \"changes\" empty, \"needs_restructure\" false, and \"note\" empty.";

        /// <summary>Findings for one unit (the review's part of <see cref="EditSchema"/>).</summary>
        public static readonly JObject ReviewSchema = EditSchema;

        /// <summary>A rule id the model wasn't given (or made up) is cleared: the finding stays, uncited.</summary>
        public static void KeepCitable(List<StyleFinding> findings, ICollection<string> allowed)
        {
            if (allowed == null) return;
            foreach (var f in findings)
                if (f.RuleId != null && !allowed.Contains(f.RuleId)) { Diagnostics.Log("Review: cleared rule id " + f.RuleId + " (not sent for this text)"); f.RuleId = null; }
        }

        /// <summary>Anchors the model's findings in the unit's runs; drops what breaks the contract (logged).</summary>
        public static List<StyleFinding> ReadReview(JObject json, RunMap map, out int dropped)
        {
            var list = new List<StyleFinding>();
            dropped = 0;
            foreach (var f in json["findings"] ?? new JArray())
            {
                var runId = (string)f["run_id"];
                var excerpt = (string)f["excerpt"] ?? "";
                string text;
                if (excerpt.Length == 0 || runId == null || !map.Texts.TryGetValue(runId, out text)) { dropped++; continue; }
                if (text.IndexOf(excerpt, StringComparison.Ordinal) < 0)
                {
                    // Cited the wrong run but quoted real text: find the run that has it.
                    var other = map.Ids.FirstOrDefault(id => map.Texts[id].IndexOf(excerpt, StringComparison.Ordinal) >= 0);
                    if (other == null) { dropped++; continue; }
                    runId = other;
                }
                var replacement = f["replacement"] == null || f["replacement"].Type == JTokenType.Null ? null : (string)f["replacement"];
                if (replacement != null && (replacement.IndexOf('<') >= 0 || replacement.IndexOf('⟦') >= 0 || replacement == excerpt)) replacement = null;
                var rid = (string)f["rule_id"];
                list.Add(new StyleFinding
                {
                    Source = "ai", Layer = "ai", RunId = runId, RuleId = rid == "general" ? null : rid, Rule = (string)f["rule"] ?? "",
                    Severity = Severity.Normalize((string)f["severity"] ?? "suggestion"), Excerpt = excerpt, Replacement = replacement,
                    Suggestion = (string)f["why"] ?? ""
                });
            }
            return list;
        }

        // ---- rewrite -----------------------------------------------------------------------------

        /// <summary>A rewrite as new text for changed runs only (the rewrite's part of <see cref="EditSchema"/>); structure can't change on this path.</summary>
        public static readonly JObject RewriteSchema = EditSchema;

        /// <summary>The restructured fragment from a <see cref="EditSchema"/> answer ("revised"), or from a plain &lt;revised&gt; answer.</summary>
        public static string ReadRevised(FlareAI.Core.Llm.LlmResponse resp)
        {
            var revised = resp.Json == null ? null : (string)resp.Json["revised"];
            if (!string.IsNullOrEmpty(revised) && revised.Trim().Length > 0)
                return revised.IndexOf("<revised>", StringComparison.OrdinalIgnoreCase) >= 0 ? ResponseParser.ParseRevised(revised) : revised.Trim();
            return ResponseParser.ParseRevised(resp.Text);
        }

        /// <summary>New text per run, or the reasons the answer breaks the contract.</summary>
        public static Dictionary<string, string> ReadRewrite(JObject json, RunMap map, List<string> errors, out bool needsRestructure, out string note)
        {
            var changes = new Dictionary<string, string>(StringComparer.Ordinal);
            needsRestructure = (bool?)json["needs_restructure"] ?? false;
            note = (string)json["note"] ?? "";
            foreach (var c in json["changes"] ?? new JArray())
            {
                var id = (string)c["run_id"];
                var text = (string)c["text"] ?? "";
                string old;
                if (id == null || !map.Texts.TryGetValue(id, out old)) { errors.Add("Unknown run " + id + "."); continue; }
                if (changes.ContainsKey(id)) { errors.Add("Run " + id + " changed twice."); continue; }
                if (text.IndexOf('<') >= 0 || text.IndexOf('>') >= 0 || text.IndexOf('⟦') >= 0) { errors.Add("Run " + id + " contains markup or run markers; give plain text only."); continue; }
                // A run right after inline markup continues its sentence ("<b>Transfers</b> page. Each…"): models tend to
                // read it as a new sentence and drop the words that finish the old one. Bench: Haiku did, every time.
                var head = ContinuationHead(map, id, old);
                if (head != null && !text.TrimStart().StartsWith(head, StringComparison.Ordinal))
                {
                    // The usual slip: the model rewrote only the new sentence and dropped the words that finish the old
                    // one ("Each change…" for " page. Each change…"). Put them back rather than spend a retry.
                    var t = text.TrimStart();
                    if (t.Length > 0 && char.IsUpper(t[0]) && t.IndexOf(head, StringComparison.Ordinal) < 0) text = head + " " + t;
                    else { errors.Add("Run " + id + " must still start with \"" + head + "\": it finishes the sentence begun before it."); continue; }
                }
                // Keep the run's own leading/trailing space: it separates it from the markup around it.
                var lead = old.Substring(0, old.Length - old.TrimStart().Length);
                var trail = old.Substring(old.TrimEnd().Length);
                text = lead + text.Trim() + trail;
                if (text.Trim().Length == 0) text = lead.Length > 0 ? lead : (trail.Length > 0 ? trail : " "); // a deleted run keeps one space
                if (text != old) changes[id] = text;
            }
            return changes;
        }

        /// <summary>
        /// Runs that finish a sentence begun before them (after a bold label, a variable, a code span), with the words they
        /// must keep at their start: told to the model up front, so it doesn't read the run as a new sentence. Measured:
        /// 26 contract retries in 93 rewrite calls on markup-rich text came from this.
        /// </summary>
        public static Dictionary<string, string> ContinuationHeads(RunMap map)
        {
            var heads = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in map.Ids)
            {
                var head = ContinuationHead(map, id, map.Texts[id]);
                if (head != null) heads[id] = head;
            }
            return heads;
        }

        /// <summary>One line per continuing run, for the rewrite prompt ("" when none).</summary>
        public static string DescribeContinuations(RunMap map)
        {
            var heads = ContinuationHeads(map);
            if (heads.Count == 0) return "";
            var sb = new StringBuilder("These runs finish a sentence begun before them (before the markup). If you change one, its new text must start with exactly these words:");
            foreach (var kv in heads) sb.Append("\n- ").Append(kv.Key).Append(": \"").Append(kv.Value).Append('"');
            return sb.ToString();
        }

        /// <summary>The words that finish a sentence begun before this run (up to its first . ! ?), when the run follows inline markup mid-sentence.</summary>
        private static string ContinuationHead(RunMap map, string id, string old)
        {
            XmlText run;
            if (!map.Runs.TryGetValue(id, out run) || !(run.PreviousSibling is XmlElement)) return null;
            var t = old.TrimStart();
            if (t.Length == 0 || char.IsUpper(t[0])) return null;
            var end = t.IndexOfAny(new[] { '.', '!', '?' });
            return end < 0 ? null : t.Substring(0, end + 1);
        }

        // ---- small tasks ---------------------------------------------------------------------------

        public static JObject Refine()
        {
            return Obj(P("applies", Bool("False when the author's instruction doesn't apply to this text (for example it names words the text doesn't contain).")),
                       P("replacement", NullableStr("Exact plain text to put in place of the text to change; the text itself if it should stay.")),
                       P("why", Str("At most 15 words.")));
        }

        /// <summary>The longest alt text we accept (screen readers and the accessibility checks use 125).</summary>
        public const int AltMaxLength = 125;

        /// <summary>
        /// Alt text. The length limit is in the field's description: structured outputs don't accept string constraints
        /// (maxLength is rejected), so <see cref="ActionRunner.SuggestAlt"/> checks the length after the answer.
        /// </summary>
        public static JObject Alt()
        {
            return Obj(P("decorative", Bool("True if the image adds nothing to the text.")),
                       P("alt", Str("One sentence of at most " + AltMaxLength + " characters (count them); empty when decorative.")));
        }

        /// <summary>The answer to a question about the text. Its own schema, so its own cache entry (see Routing.DefaultCache).</summary>
        public static JObject Answer()
        {
            return Obj(P("kind", Enum(new[] { "answer", "change_request" }, "\"change_request\" when the author is asking for an edit, not asking a question.")),
                       P("answer", Str("The answer in plain text, no markup. Empty for a change request.")),
                       P("rule_ids", Arr(Str("The id of a style-guide rule the answer depends on, from the [brackets] in the guide."))));
        }

        public const string ChangeRequestReply = "This is a request for a change. Choose Make a change to apply it.";

        /// <summary>The answer as the author reads it: the text, plus the rules it names (only ids that were sent for this text).</summary>
        public static string ReadAnswer(JObject json, ICollection<string> knownRuleIds)
        {
            var kind = json == null ? null : (string)json["kind"];
            var answer = json == null ? "" : ((string)json["answer"] ?? "").Trim();
            if (kind == "change_request" || answer.Length == 0) return ChangeRequestReply;
            var ids = new List<string>();
            if (knownRuleIds != null && json["rule_ids"] is JArray)
                foreach (var t in (JArray)json["rule_ids"])
                {
                    var id = (string)t;
                    if (id != null && knownRuleIds.Contains(id) && !ids.Contains(id)) ids.Add(id);
                }
            return ids.Count == 0 ? answer : answer + "\n\nRule: " + string.Join(", ", ids.ToArray());
        }

        /// <summary>Alt text cut to <see cref="AltMaxLength"/> at a word boundary, without a dangling comma or "and".</summary>
        public static string FitAlt(string alt)
        {
            alt = (alt ?? "").Trim();
            if (alt.Length <= AltMaxLength) return alt;
            var cut = alt.Substring(0, AltMaxLength + 1);
            var space = cut.LastIndexOf(' ');
            cut = (space > 40 ? cut.Substring(0, space) : alt.Substring(0, AltMaxLength)).TrimEnd();
            cut = System.Text.RegularExpressions.Regex.Replace(cut, @"(?:[,;:\-–—]|\s+(?:and|or|with|of|the|a|an|to|in|on|for))+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).TrimEnd();
            return cut;
        }

        public static JObject AssistMeta()
        {
            return Obj(P("description", Str("What the reader can do or learn here, at most 155 characters.")),
                       P("keywords", Arr(Str("Index term; 'Parent:Child' for a subentry."))));
        }

        public static JObject AssistSynonyms()
        {
            return Obj(P("groups", Arr(Obj(P("term", Str("A term in the topic.")), P("alternatives", Arr(Str("A word readers might search for instead.")))))));
        }

        public static JObject AssistAnswers()
        {
            return Obj(P("micro_content", Arr(Obj(P("phrase", Str("A question or phrase a reader would search for.")), P("response", Str("1-2 sentences taken from the topic."))))),
                       P("glossary", Arr(Obj(P("term", Str()), P("definition", Str("One sentence from the topic's context."))))));
        }

        /// <summary>Rules extracted from notes: a row (linter-enforced) or a prose rule, each quoting its source.</summary>
        public static JObject Extract()
        {
            return Obj(P("rules", Arr(Obj(
                P("kind", Enum(new[] { "row", "prose" }, "row: a word or phrase to replace or avoid; prose: a rule that needs judgment.")),
                P("title", Str("Short name.")),
                P("avoid", Str("row: the words to avoid, comma-separated; prose: empty.")),
                P("use", Str("row: replacements, as many comma-separated as avoid, or \"(delete)\", or empty to only flag; prose: empty.")),
                P("severity", Enum(Severities)),
                P("why", Str("At most 12 words.")),
                P("example", Str("row: a short sentence containing an avoided word; prose: empty.")),
                P("text", Str("prose: the rule as one imperative sentence; row: empty.")),
                P("bad", Str("prose: a short sentence that breaks the rule; row: empty.")),
                P("good", Str("prose: the same sentence fixed; row: empty.")),
                P("quote", Str("The words in the notes this rule comes from, copied exactly."))))));
        }
    }
}
