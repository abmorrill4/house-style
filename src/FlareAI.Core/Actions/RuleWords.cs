using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// Keeps an AI replacement under a rule to words the rule names or the project already uses. Model output is data:
    /// a word from neither is held back, and the writer is asked to choose.
    /// </summary>
    public static class RuleWords
    {
        private static readonly Regex Word = new Regex("[A-Za-z]+", RegexOptions.Compiled);

        /// <summary>The words of the replacement that the rule and the project don't contain (empty when the rule is null).</summary>
        public static List<string> Outside(string excerpt, string replacement, GuideRule rule, IEnumerable<string> projectText)
        {
            var outside = new List<string>();
            if (rule == null || string.IsNullOrEmpty(replacement)) return outside;
            var allowed = new HashSet<string>();
            Add(allowed, excerpt);
            Add(allowed, rule.Text);
            Add(allowed, rule.Good);
            Add(allowed, rule.Bad);
            Add(allowed, rule.Example);
            if (projectText != null) foreach (var t in projectText) Add(allowed, t);
            var seen = new HashSet<string>();
            foreach (Match m in Word.Matches(replacement))
            {
                if (m.Value.Length <= 3) continue;
                var stem = Stem(m.Value);
                var known = false;
                foreach (var f in Forms(m.Value)) if (allowed.Contains(f)) known = true;
                if (known || !seen.Add(stem)) continue;
                outside.Add(m.Value);
            }
            return outside;
        }

        /// <summary>The sentence for the card when the writer should pick the word.</summary>
        public static string Choose(List<string> outside, GuideRule rule)
        {
            return "Choose the word that fits. \"" + string.Join(", ", (outside ?? new List<string>()).ToArray()) + "\" is not in the rule or your project.";
        }

        private static void Add(HashSet<string> set, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in Word.Matches(text)) foreach (var f in Forms(m.Value)) set.Add(f);
        }

        private static string Stem(string word)
        {
            var w = word.ToLowerInvariant();
            if (w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal)) return w.Substring(0, w.Length - 2);
            if (w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal)) return w.Substring(0, w.Length - 1);
            return w;
        }

        /// <summary>The word as written and without a trailing "s", so "roles" matches "role" and "role" matches "roles".</summary>
        private static IEnumerable<string> Forms(string word)
        {
            var w = word.ToLowerInvariant();
            yield return Stem(w);
            yield return w;
            if (w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal)) yield return w.Substring(0, w.Length - 1);
        }
    }
}
