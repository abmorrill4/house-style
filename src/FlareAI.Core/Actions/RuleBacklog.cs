using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// One rule's share of a project's open suggestions: how often it fires, in how many files, how many of those can
    /// be fixed automatically, and why the rule exists, in the guide's own words.
    /// </summary>
    public sealed class BacklogRule
    {
        /// <summary>The rule's reference (its guide id, or its name when it has none): what Skip and Turn off act on.</summary>
        public string Key { get; set; }
        /// <summary>Where the rule comes from and its guide section: "Our rules · Word choice".</summary>
        public string Title { get; set; }
        /// <summary>Why it fires, in plain language: the rule as written in the guide, or the message it shows.</summary>
        public string Reason { get; set; }
        /// <summary>The most serious level among its suggestions: error, warning or suggestion.</summary>
        public string Severity { get; set; }
        /// <summary>Its open suggestions, by file.</summary>
        public List<StyleFinding> Findings { get; } = new List<StyleFinding>();
        /// <summary>The distinct files (topics and snippets) it fires in.</summary>
        public List<string> Files { get; } = new List<string>();
        /// <summary>The snippets among <see cref="Files"/>: a change there reaches every topic that includes them.</summary>
        public List<string> Snippets { get; } = new List<string>();

        public int Occurrences { get { return Findings.Count; } }

        /// <summary>The suggestions Fix all writes: exact rule fixes.</summary>
        public List<StyleFinding> Automatic { get { return Findings.FindAll(RuleBacklog.IsAutomatic); } }

        /// <summary>"23 in 14 files · 20 Auto-fix" (the issue types the card shows: Auto-fix and Needs you).</summary>
        public string Counts()
        {
            var auto = Automatic.Count;
            return Occurrences + " in " + Plural.Of(Files.Count, "file") +
                   (auto == 0 ? " · Needs you" : auto == Occurrences ? " · Auto-fix" : " · " + auto + " Auto-fix");
        }
    }

    /// <summary>
    /// The first Lint Project starts focused: the few rules that cover most issues, then Show all rules. Pure maths, so
    /// the pane only shows it.
    /// </summary>
    public sealed class RuleFocus
    {
        /// <summary>The rules to list now (the top ones, or every rule when there are few).</summary>
        public List<BacklogRule> Shown { get; } = new List<BacklogRule>();
        /// <summary>True when rules were left out (the list is shorter than the backlog).</summary>
        public bool Focused { get; set; }
        /// <summary>Issues the shown rules cover, and issues in all.</summary>
        public int Covered { get; set; }
        public int Total { get; set; }

        /// <summary>"Start with these 3 rules. They cover 40 of the 52 issues." Empty when not focused.</summary>
        public string Line
        {
            get
            {
                if (!Focused) return "";
                return "Start with these " + Shown.Count + " rules. They cover " + Covered + " of the " + Total + " issues.";
            }
        }

        /// <summary>The top <paramref name="top"/> rules of <paramref name="rules"/> (already most frequent first).</summary>
        public static RuleFocus Of(IList<BacklogRule> rules, int top = 3)
        {
            var f = new RuleFocus();
            foreach (var r in rules) f.Total += r.Occurrences;
            f.Focused = rules.Count > top;
            for (var i = 0; i < rules.Count && (!f.Focused || i < top); i++)
            {
                f.Shown.Add(rules[i]);
                f.Covered += rules[i].Occurrences;
            }
            return f;
        }
    }

    /// <summary>
    /// A big project check, worked one rule at a time: the open suggestions grouped under their rule (most frequent
    /// first), and the one-line summary that heads the pane. Pure: no files, no editor.
    /// </summary>
    public static class RuleBacklog
    {
        /// <summary>What a suggestion is grouped by: its guide rule, or the rule's name for rules without one.</summary>
        public static string KeyOf(StyleFinding f) { return f.RuleId ?? f.Rule ?? ""; }

        /// <summary>An exact rule fix: Fix all and "Fix all automatic suggestions" write these, and only these.</summary>
        public static bool IsAutomatic(StyleFinding f) { return f.CanFix && f.Kind == "mechanical"; }

        /// <summary>True for a Flare snippet.</summary>
        public static bool IsSnippet(string path) { return path != null && string.Equals(Path.GetExtension(path), ".flsnp", StringComparison.OrdinalIgnoreCase); }

        /// <param name="open">Open suggestions (not applied, text still there).</param>
        /// <param name="ruleFor">The guide rule for a reference, or null.</param>
        /// <param name="openTopicPath">The file of suggestions that carry no path of their own.</param>
        /// <param name="sourceName">Where a rule comes from (layer, rule id), as the Issues pane says it; null for the layer's name.</param>
        public static List<BacklogRule> Group(IEnumerable<StyleFinding> open, Func<string, GuideRule> ruleFor, string openTopicPath, Func<string, string, string> sourceName = null)
        {
            sourceName = sourceName ?? ((layer, id) => ReviewSession.SourceName(layer));
            var rules = new List<BacklogRule>();
            var byKey = new Dictionary<string, BacklogRule>(StringComparer.Ordinal);
            var messages = new Dictionary<BacklogRule, Dictionary<string, int>>();
            foreach (var f in open)
            {
                var key = KeyOf(f);
                BacklogRule r;
                if (!byKey.TryGetValue(key, out r))
                {
                    var rule = ruleFor == null || f.RuleId == null ? null : ruleFor(f.RuleId);
                    r = new BacklogRule { Key = key, Title = TitleOf(f, rule, sourceName), Reason = rule != null ? rule.Text : null, Severity = f.Severity };
                    byKey[key] = r;
                    rules.Add(r);
                    messages[r] = new Dictionary<string, int>(StringComparer.Ordinal);
                }
                r.Findings.Add(f);
                if (Rank(f.Severity) > Rank(r.Severity)) r.Severity = f.Severity;
                var path = f.DocumentPath ?? openTopicPath ?? "";
                if (!r.Files.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                {
                    r.Files.Add(path);
                    if (IsSnippet(path)) r.Snippets.Add(path);
                }
                if (!string.IsNullOrEmpty(f.Suggestion))
                {
                    int n;
                    messages[r].TryGetValue(f.Suggestion, out n);
                    messages[r][f.Suggestion] = n + 1;
                }
            }
            foreach (var r in rules)
            {
                r.Reason = PlainReason(r.Reason);
                if (string.IsNullOrEmpty(r.Reason)) r.Reason = MostCommon(messages[r]) ?? r.Title;
            }
            rules.Sort((a, b) =>
            {
                var c = b.Occurrences.CompareTo(a.Occurrences);
                if (c == 0) c = b.Files.Count.CompareTo(a.Files.Count);
                return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });
            return rules;
        }

        /// <summary>
        /// "117 suggestions in 42 files. 63 can be fixed automatically across 9 rules." — the line that heads a project
        /// check. Rules skipped for this session are left out and mentioned at the end.
        /// </summary>
        public static string Summary(IList<StyleFinding> open, string openTopicPath, int skippedRules = 0, int unfilteredOpen = -1)
        {
            var skipped = skippedRules > 0 ? " " + Plural.Of(skippedRules, "rule") + " skipped for now." : "";
            if (open.Count == 0) return (unfilteredOpen > 0 ? "No issues match these filters." : "No issues found in this project.") + skipped;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var autoRules = new HashSet<string>(StringComparer.Ordinal);
            var auto = 0;
            foreach (var f in open)
            {
                files.Add(f.DocumentPath ?? openTopicPath ?? "");
                if (!IsAutomatic(f)) continue;
                auto++;
                autoRules.Add(KeyOf(f));
            }
            var head = (unfilteredOpen >= 0 ? "Showing " + open.Count + " of " + unfilteredOpen + " issues" : Plural.Of(open.Count, "issue")) + " in " + Plural.Of(files.Count, "file") + ". ";
            var tail = auto == 0 ? "None can be changed automatically. Review them one rule at a time."
                     : (auto == open.Count ? (auto == 1 ? "It" : "All") : auto.ToString()) + " can be changed automatically" +
                       (autoRules.Count > 1 ? " across " + autoRules.Count + " rules." : " by one rule.");
            return head + tail + skipped;
        }

        private static readonly Regex PatternRule = new Regex(@"^Avoid the pattern .+? (?:\((?:rewrite|delete it)\)|→ use '[^']*')\.\s*", RegexOptions.Singleline);

        /// <summary>
        /// A rule's reason without the matching pattern a compiled rule starts with ("Avoid the pattern [“”‘’] (rewrite).
        /// Code requires straight marks…" → "Code requires straight marks…"); null when nothing readable is left.
        /// </summary>
        public static string PlainReason(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = PatternRule.Match(text);
            if (!m.Success) return RuleWording.Plain(text.Trim());   // "Use 'to' instead of 'in order to'.", never an arrow
            var rest = text.Substring(m.Length).Trim();
            return rest.Length == 0 ? null : rest;
        }

        private static string TitleOf(StyleFinding f, GuideRule rule, Func<string, string, string> sourceName)
        {
            if (rule != null) return sourceName(rule.Layer, rule.Id) + " · " + rule.Section;
            var source = string.IsNullOrEmpty(f.Layer) || f.Layer == "ai" ? "AI" : sourceName(f.Layer, f.RuleId);
            if (!string.IsNullOrEmpty(f.RuleSection)) return source + " · " + f.RuleSection;
            var name = f.Rule ?? "";
            return source + (name.Length == 0 ? "" : " · " + Words(name.Substring(name.IndexOf('.') + 1)));
        }

        /// <summary>"HardCoded" → "Hard coded", "AltText" → "Alt text", "OneH1" → "One h1".</summary>
        private static string Words(string name)
        {
            var spaced = Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])", " ");
            return spaced.Length == 0 ? spaced : spaced.Substring(0, 1) + spaced.Substring(1).ToLowerInvariant();
        }

        private static int Rank(string severity)
        {
            switch ((severity ?? "").ToLowerInvariant())
            {
                case "error": return 3;
                case "warning": return 2;
                case "suggestion": return 1;
                default: return 0;
            }
        }

        private static string MostCommon(Dictionary<string, int> counts)
        {
            string best = null; var most = 0;
            foreach (var kv in counts) if (kv.Value > most) { best = kv.Key; most = kv.Value; }
            return best;
        }
    }
}
