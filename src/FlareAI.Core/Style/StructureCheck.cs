using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;

namespace FlareAI.Core.Style
{
    /// <summary>One built-in structure check as a rule: its citable id, its legacy name (what <see cref="StructureCheck.Of"/> sets on a finding), its text and its default level.</summary>
    public sealed class StructureRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Statement { get; set; }
        public string Level { get; set; }
        /// <summary>Plain lowercase phrases that name the check in ordinary words, matched against an AI issue's name and explanation.</summary>
        public string[] Terms { get; set; }
    }

    /// <summary>
    /// Accessibility, structure and white-label checks a text linter can't see: images without alt text, skipped heading
    /// levels, a second h1, tables without a header row, link text that doesn't say where it goes, and hard-coded
    /// phone numbers, email addresses and URLs (in white-label docs these belong in variables). Each finding is
    /// anchored on nearby text so Go to can select it; fixing it is the author's call (Kind "manual").
    /// </summary>
    public static class StructureCheck
    {
        private static readonly Regex Phone = new Regex(@"(?<!\d)(?:\+?1[ .-]?)?(?:\(\d{3}\)\s?|\d{3}[ .-])\d{3}[ .-]\d{4}(?!\d)", RegexOptions.Compiled);
        private static readonly Regex Email = new Regex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.Compiled);
        private static readonly Regex Url = new Regex(@"\b(?:https?://|www\.)[^\s<>""]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> VagueLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "here", "click here", "this link", "link", "more", "read more", "learn more", "this page", "this", "go", "details" };

        /// <summary>The built-in checks, in the order <see cref="Of"/> makes them. Their levels are the defaults Of sets.</summary>
        public static readonly ReadOnlyCollection<StructureRule> Rules = new ReadOnlyCollection<StructureRule>(new List<StructureRule>
        {
            new StructureRule { Id = "structure:alt-text", Name = "Structure.AltText", Level = "error", Statement = "Images have alt text. Describe what each one shows, or mark it decorative.", Terms = new[] { "alt text", "alternative text", "alt attribute" } },
            new StructureRule { Id = "structure:one-h1", Name = "Structure.OneH1", Level = "warning", Statement = "A topic has one h1, its title. Other headings are h2 or lower.", Terms = new[] { "one h1", "second h1", "more than one h1", "multiple h1", "single h1" } },
            new StructureRule { Id = "structure:heading-order", Name = "Structure.HeadingOrder", Level = "warning", Statement = "Headings go down one level at a time.", Terms = new[] { "heading order", "heading level", "heading jump", "heading hierarchy", "skipped heading", "skips a level", "skipped a level", "skips from h" } },
            new StructureRule { Id = "structure:table-header", Name = "Structure.TableHeader", Level = "warning", Statement = "Tables have a header row of th cells.", Terms = new[] { "table header", "header row", "header cell", "th cell" } },
            new StructureRule { Id = "structure:link-text", Name = "Structure.LinkText", Level = "warning", Statement = "Link text says where the link goes.", Terms = new[] { "link text", "vague link", "descriptive link", "click here" } },
            new StructureRule { Id = "structure:hard-coded", Name = "Structure.HardCoded", Level = "suggestion", Statement = "Phone numbers, email addresses and URLs are variables in white-label docs, not typed in.", Terms = new[] { "hard coded", "hardcoded" } },
        });

        /// <summary>The built-in rule with this id or name (matched exactly, ignoring case), or null.</summary>
        public static StructureRule Find(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            return Rules.FirstOrDefault(r => string.Equals(r.Id, idOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The id of the built-in rule with this id or name; the input unchanged when no built-in rule matches it.</summary>
        public static string IdOf(string idOrName)
        {
            var rule = Find(idOrName);
            return rule == null ? idOrName : rule.Id;
        }

        /// <summary>True when an AI issue repeats this built-in check: it cites the rule by id or name, or its name and explanation use the check's own words.</summary>
        public static bool Names(StructureRule rule, StyleFinding ai)
        {
            if (rule == null || ai == null) return false;
            var cited = Find(ai.RuleId) ?? Find(ai.Rule);
            if (cited != null && string.Equals(cited.Id, rule.Id, StringComparison.OrdinalIgnoreCase)) return true;
            if (rule.Terms == null) return false;
            var text = " " + Regex.Replace((ai.Rule + " " + ai.Suggestion).ToLowerInvariant(), "[^a-z0-9]+", " ") + " ";
            foreach (var term in rule.Terms)
            {
                var t = Regex.Replace((term ?? "").ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
                if (t.Length > 0 && text.Contains(" " + t + " ")) return true;
            }
            return false;
        }

        /// <summary>
        /// Gives each built-in finding its rule id, then applies the Rule/Setting tables: "off" drops the finding, a level
        /// changes its level. Other findings (AI findings, or a name no built-in rule has) pass through unchanged.
        /// </summary>
        /// <param name="settingOf">What the guides and the project say about a rule, by id and name (Composition.SettingOf).</param>
        public static List<StyleFinding> Settle(IEnumerable<StyleFinding> found, Func<string, string, string> settingOf)
        {
            var kept = new List<StyleFinding>();
            foreach (var f in found)
            {
                var rule = f.Source == "structure" ? Find(f.Rule) : null;
                if (rule == null) { kept.Add(f); continue; }
                f.RuleId = rule.Id;
                var setting = settingOf(rule.Id, rule.Name);
                if (setting == "off") continue;
                if (setting != null && GuideLayer.Severities.Contains(setting)) f.Severity = setting;
                kept.Add(f);
            }
            return kept;
        }

        /// <summary>The built-in checks as a guide layer named "structure": one rule per check, a lint row that never fires on text, so the tables can turn it off or re-level it.</summary>
        public static GuideLayer Layer()
        {
            var items = new List<GuideRule>();
            var rules = new List<LintRule>();
            foreach (var r in Rules)
            {
                items.Add(new GuideRule { Id = r.Id, Layer = "structure", Section = "Structure and accessibility", Page = "Structure and accessibility", Text = r.Statement, IsLint = true });
                rules.Add(new LintRule { Id = r.Id, LocalName = r.Name.Substring("Structure.".Length), Layer = "structure", Severity = r.Level, Action = "flag" });
            }
            return GuideLayer.InCode("structure", items, rules);
        }

        public static List<StyleFinding> Of(IList<XmlNode> blocks)
        {
            var found = new List<StyleFinding>();
            var lastLevel = 0;
            var h1s = 0;
            foreach (var block in blocks)
            {
                foreach (var el in Elements(block))
                {
                    var name = el.LocalName.ToLowerInvariant();
                    if (name == "img")
                    {
                        var alt = el.GetAttribute("alt");
                        if (alt.Trim().Length == 0 && el.GetAttribute("role") != "presentation")
                            Add(found, el, "Structure.AltText", "error", "This image has no alt text: describe what it shows (or mark it decorative) for screen readers.");
                    }
                    else if (name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1]))
                    {
                        var level = name[1] - '0';
                        if (level == 1 && ++h1s > 1) Add(found, el, "Structure.OneH1", "warning", "A topic has one h1 (its title); make this an h2.");
                        if (lastLevel > 0 && level > lastLevel + 1)
                            Add(found, el, "Structure.HeadingOrder", "warning", "Heading jumps from h" + lastLevel + " to h" + level + ": use h" + (lastLevel + 1) + " so screen readers and the TOC keep the outline.");
                        lastLevel = level;
                    }
                    else if (name == "table")
                    {
                        if (el.SelectSingleNode(".//*[local-name()='th']") == null)
                            Add(found, el, "Structure.TableHeader", "warning", "This table has no header row: use th cells for column headings so screen readers can announce them.");
                    }
                    else if (name == "a" || (name == "xref" && el.NamespaceURI == BlockSelector.MadCapNs))
                    {
                        var text = Regex.Replace(el.InnerText, @"\s+", " ").Trim().TrimEnd('.', '!');
                        if (VagueLinks.Contains(text))
                            Add(found, el, "Structure.LinkText", "warning", "Link text \"" + text + "\" doesn't say where it goes: name the destination.");
                    }
                }
                foreach (var run in TextRuns.Of(new List<XmlNode> { block }))
                {
                    if (InsideLink(run)) continue;
                    foreach (var pair in new[] { new KeyValuePair<Regex, string>(Phone, "phone number"), new KeyValuePair<Regex, string>(Email, "email address"), new KeyValuePair<Regex, string>(Url, "URL") })
                        foreach (Match m in pair.Key.Matches(run.Value))
                        {
                            var f = FindingLocator.ForSpan(run, m.Index, m.Length, null, TextRuns.BlockOf(run));
                            Set(f, "Structure.HardCoded", "suggestion", "Hard-coded " + pair.Value + ": in white-label docs, insert a variable so each client's value shows.");
                            found.Add(f);
                        }
                }
            }
            return found;
        }

        private static IEnumerable<XmlElement> Elements(XmlNode block)
        {
            if (block is XmlElement) yield return (XmlElement)block;
            if (block.NodeType != XmlNodeType.Element) yield break;
            foreach (XmlElement e in block.SelectNodes(".//*")) yield return e;
        }

        private static bool InsideLink(XmlNode n)
        {
            for (var p = n.ParentNode; p != null; p = p.ParentNode)
                if (p.LocalName == "a" || p.LocalName == "xref") return true;
            return false;
        }

        /// <summary>Anchors on the element's own text, or the nearest text before it (an image has none).</summary>
        private static void Add(List<StyleFinding> found, XmlElement el, string rule, string severity, string message)
        {
            var runs = TextRuns.Of(new List<XmlNode> { el });
            XmlText run = runs.Count > 0 ? runs[0] : null;
            if (run == null)
            {
                // The last text before the element in its block, else the block's first text. A block with no text at
                // all (an image in its own paragraph, the usual Flare pattern) anchors on the nearest text in the topic.
                var block = TextRuns.BlockOf(el) ?? el;
                if (TextRuns.Of(new List<XmlNode> { block }).TrueForAll(t => t.Value.Trim().Length == 0))
                    block = BlockSelector.FindBody(el.OwnerDocument) ?? block;
                var order = new List<XmlNode>();
                Walk(block, order);
                var at = order.IndexOf(el);
                foreach (var n in order)
                {
                    var t = n as XmlText;
                    if (t == null || t.Value.Trim().Length == 0) continue;
                    if (order.IndexOf(n) < at || run == null) run = t;
                    if (order.IndexOf(n) > at) break;
                }
            }
            if (run == null) return;
            var text = run.Value;
            var start = text.Length - text.TrimStart().Length;
            var length = Math.Min(60, text.Trim().Length);
            if (length == 0) return;
            var f = FindingLocator.ForSpan(run, start, length, null, TextRuns.BlockOf(run));
            Set(f, rule, severity, message);
            found.Add(f);
        }

        private static void Set(StyleFinding f, string rule, string severity, string message)
        {
            f.Source = "structure"; f.Layer = "structure"; f.Rule = rule; f.Severity = severity; f.Suggestion = message; f.Replacement = null;
        }

        private static void Walk(XmlNode n, List<XmlNode> order)
        {
            order.Add(n);
            foreach (XmlNode c in n.ChildNodes) Walk(c, order);
        }
    }
}
