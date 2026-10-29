using System;
using System.Collections.Generic;
using System.Linq;

namespace FlareAI.Core.Style
{
    /// <summary>One deterministic lint hit inside one text run.</summary>
    public sealed class LintHit
    {
        public int Index { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }
        public string Match { get; set; }
        /// <summary>Exact replacement for Match; null when the fix needs judgment.</summary>
        public string Replacement { get; set; }
        public string Rule { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
        /// <summary>"google" (baseline) or "company" (layered on top).</summary>
        public string Layer { get; set; }
        /// <summary>Guide page explaining the rule, when known.</summary>
        public string Link { get; set; }
        /// <summary>Set when Match is a typed copy of this project variable's value (fix = insert the variable).</summary>
        public string Variable { get; set; }
        /// <summary>Citable guide id when the rule comes from a guide table (for example "company:terminology/3").</summary>
        public string RuleId { get; set; }
        /// <summary>Other rules that flagged these same words (dropped as duplicates): turning off the shown rule would expose them.</summary>
        public List<RuleRef> AlsoRules = new List<RuleRef>();

        /// <summary>Every rule on these words, the shown one first: turning all of them off leaves no finding here.</summary>
        public List<string> RuleIds
        {
            get
            {
                var ids = new List<string>();
                if ((RuleId ?? Rule) != null) ids.Add(RuleId ?? Rule);
                foreach (var r in AlsoRules) if (!ids.Contains(r.Id)) ids.Add(r.Id);
                return ids;
            }
        }

        /// <summary>
        /// Records the rule of <paramref name="other"/>, which this hit replaces, and the rules <paramref name="other"/> already
        /// stood for, unless they have neither id nor checker name, are this rule or are listed.
        /// </summary>
        internal void AlsoFlaggedBy(LintHit other)
        {
            Also(new RuleRef { Id = other.RuleId ?? other.Rule, Name = other.Rule, Layer = other.Layer, IsChecker = other.RuleId == null });
            foreach (var r in other.AlsoRules) Also(r);
        }

        private void Also(RuleRef r)
        {
            if (r.Id == null || r.Id == (RuleId ?? Rule) || AlsoRules.Any(x => x.Id == r.Id)) return;
            AlsoRules.Add(r);
        }
    }

    /// <summary>A guide rule named by id, for lists of related rules.</summary>
    public sealed class RuleRef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Layer { get; set; }
        /// <summary>True for a generated checker with no guide id (the glossary), named by its checker name.</summary>
        public bool IsChecker { get; set; }
    }

    /// <summary>What a unit of text is, for choosing its rules: its content (Content tags) and its words (Term/Rule rows).</summary>
    public sealed class GuideScope
    {
        public HashSet<string> Content = new HashSet<string>();
        public string Text = "";
        /// <summary>Packs every call carries in the long-lived cached block (Routing.AlwaysPacks).</summary>
        public HashSet<string> Always = new HashSet<string>();
        /// <summary>True when several calls share these packs (one check of a multi-section topic): worth a cache write.</summary>
        public bool SharedPacks;
        public static GuideScope Of(IEnumerable<string> content, string text = "") { return new GuideScope { Content = new HashSet<string>(content ?? new string[0]), Text = text ?? "" }; }
    }

    /// <summary>One citable rule of the layered guide.</summary>
    public sealed class GuideRule
    {
        public string Id { get; set; }
        public string Layer { get; set; }
        public string Section { get; set; }
        /// <summary>The guide page (topic title) the rule is written in.</summary>
        public string Page { get; set; }
        public string Text { get; set; }
        /// <summary>A table row's Example cell (an Avoid/Use or Pattern row), or null.</summary>
        public string Example { get; set; }
        /// <summary>The rule's name for writers: <see cref="Text"/>, but never a raw regular expression (<see cref="RuleWording"/>).</summary>
        public string Display { get { return RuleWording.Display(Text, Example, Bad); } }
        public string Link { get; set; }
        public string Bad { get; set; }
        public string Good { get; set; }
        /// <summary>The guide topic it comes from.</summary>
        public string Source { get; set; }
        /// <summary>Content condition tags: the rule applies only to sections with this content (empty = everywhere).</summary>
        public List<string> Applies = new List<string>();
        /// <summary>Product condition tags: the rule applies only to these products (empty = every product).</summary>
        public List<string> Products = new List<string>();
        /// <summary>True when the rule's id comes from its permanent key (id="rule-…" on its bullet or row), not its position.</summary>
        public bool HasKey { get; set; }
        /// <summary>The bullet or table row the rule is written in, and its position in the topic (for assigning keys).</summary>
        internal System.Xml.XmlElement Element;
        internal int Position;
        /// <summary>A row of an Avoid/Use or Pattern table: the linter enforces it, so it isn't sent to the AI.</summary>
        public bool IsLint { get; set; }
        /// <summary>Words or phrases the rule is about (a Term/Rule table row): it applies only when the text uses one.</summary>
        public List<string> Triggers = new List<string>();

        /// <summary>True when the text uses one of the rule's terms (whole words; case-sensitive for terms with capitals after the first letter).</summary>
        public bool TriggeredBy(string text)
        {
            if (Triggers.Count == 0 || string.IsNullOrEmpty(text)) return false;
            foreach (var t in Triggers)
            {
                var term = System.Text.RegularExpressions.Regex.Replace(t, @"\s*\([^)]*\)\s*", " ").Trim();   // "& (ampersand)" → "&"
                if (term.Length == 0) continue;
                var exact = term.Skip(1).Any(char.IsUpper);
                var pattern = (char.IsLetterOrDigit(term[0]) ? @"(?<![\w-])" : "") + System.Text.RegularExpressions.Regex.Escape(term).Replace(@"\ ", @"\s+") +
                              (char.IsLetterOrDigit(term[term.Length - 1]) ? @"(?![\w-])" : "");
                if (System.Text.RegularExpressions.Regex.IsMatch(text, pattern, exact ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>A prose rule with Bad/Good examples, used to check that the AI applies it.</summary>
    public sealed class GuideExample
    {
        public string Id { get; set; }
        /// <summary>The layer the rule is written in ("google", "company", a team or personal layer).</summary>
        public string Layer { get; set; }
        public string Text { get; set; }
        public string Bad { get; set; }
        public string Good { get; set; }
    }

    public sealed class GuideIssue
    {
        public string Level { get; set; }
        public string Layer { get; set; }
        public string File { get; set; }
        public string Location { get; set; }
        public string Message { get; set; }
    }

    public sealed class GuideReport
    {
        public List<GuideIssue> Issues = new List<GuideIssue>();
        public int TableRules, TestedByExample, ProseRules, ProseRulesWithExamples, Errors, Warnings;
        public List<string> Layers = new List<string>();
    }

    /// <summary>The style guide and linter. Implemented in process by <see cref="StyleEngine"/>; faked in tests.</summary>
    public interface IStyleService
    {
        /// <param name="headings">Indices of runs that are headings (heading rules apply only to them).</param>
        /// <param name="project">Path inside a Flare project to add its project layer (variables, glossary, StyleGuide), or null.</param>
        IList<LintHit> Lint(IList<string> texts, IList<int> headings, string project);
        /// <summary>
        /// Lint lines that carry their context (<see cref="TextLine.ToLint"/>): rules fire only where their Content scope
        /// allows, and word rules stay out of UI labels.
        /// </summary>
        IList<LintHit> Lint(IList<LintLine> lines, string project);
        /// <summary>The layered style guide (Google → configured layers → project) for the system prompt.</summary>
        string Guide(string project);
        /// <summary>
        /// The guide for one unit of text: [0] the rules that always apply, [1] the packs for what the unit contains
        /// (Content condition tags such as Steps, Code), [2] the Term rows for words it uses. Rules for products this
        /// guide doesn't cover, and lint rows (the linter enforces them), are in none.
        /// </summary>
        string[] GuideFor(string project, GuideScope scope);
        /// <summary>The ids of the rules <see cref="GuideFor"/> sends for this scope: the only ones the AI may cite.</summary>
        IList<string> RuleIdsFor(string project, GuideScope scope);
        /// <summary>Every citable rule of the layered guide (same call as <see cref="Guide"/>, cached with it).</summary>
        IList<GuideRule> Rules(string project);
        /// <summary>True when the guide (or the project) turns this rule off: its findings, cached ones included, are not shown.</summary>
        bool IsOff(string project, string ruleId);
        /// <summary>Tests the guide itself (table rows against their examples, contradictions, unreadable topics).</summary>
        GuideReport Validate(string project);
        /// <summary>Prose rules that carry Bad/Good examples.</summary>
        IList<GuideExample> Examples(string project);
    }
}
