using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FlareAI.Core.Actions;

namespace FlareAI.Core.Style
{
    /// <summary>Whether a rule applies now: at its own level, not at all, or at a level a guide or the project set.</summary>
    public enum RuleState { On, Off, LevelChanged }

    /// <summary>
    /// One rule of the layered guide as the Style rules view shows it: where it is written, what it says, how it is enforced,
    /// and its state now, with the change that put it there (who, when, why) and any open request about it.
    /// </summary>
    public sealed class CatalogRule
    {
        /// <summary>What a change names: the rule's id, or the name of a generated rule (variables, glossary terms).</summary>
        public string Key { get; internal set; }
        public string Id { get; internal set; }
        /// <summary>The checker's name for a table rule ("Google.WordList221"), or null for a rule only the AI applies.</summary>
        public string Name { get; internal set; }
        /// <summary>The guide's internal name for where the rule comes from ("google", "company", the project's "project").</summary>
        public string Origin { get; internal set; }
        /// <summary>Position of the source in the guide order (Google first, the project last).</summary>
        public int SourceIndex { get; internal set; }
        /// <summary>Where the rule comes from, for people: Google, Company, Team style guide (name), My style guide, Project (name).</summary>
        public string Source { get; internal set; }
        public string Page { get; internal set; }
        public string Section { get; internal set; }
        public string Text { get; internal set; }
        /// <summary>The rule's name for writers: <see cref="Text"/>, but never a raw regular expression (the pattern is shown under "Pattern").</summary>
        public string Title { get; internal set; }
        public string Link { get; internal set; }
        /// <summary>The guide topic it is written in, or null.</summary>
        public string File { get; internal set; }
        /// <summary>A row of an Avoid/Use or Pattern/Use table: the checker enforces it at a level. Otherwise the AI applies it.</summary>
        public bool IsTable { get; internal set; }
        public bool IsPattern { get; internal set; }
        /// <summary>"replace", "remove" or "flag" for a table rule; null for a rule the AI applies.</summary>
        public string Action { get; internal set; }
        /// <summary>True when a finding of this rule carries the fix (replace or delete).</summary>
        public bool HasFix { get { return Action == "replace" || Action == "remove"; } }
        public List<string> Avoid { get; internal set; }
        public string Use { get; internal set; }
        public string Example { get; internal set; }
        public string Bad { get; internal set; }
        public string Good { get; internal set; }
        /// <summary>Words a Term/Rule row is about (the AI gets the rule only when the text uses one).</summary>
        public List<string> Terms { get; internal set; }
        /// <summary>The level the rule is written with (table rules only).</summary>
        public string DefaultLevel { get; internal set; }
        /// <summary>The level findings get now (table rules only); null when off or when the AI decides.</summary>
        public string Level { get; internal set; }
        /// <summary>Content condition tags: the kinds of content the rule is limited to (empty = everywhere).</summary>
        public List<string> Content { get; internal set; }
        /// <summary>Product condition tags (empty = every product).</summary>
        public List<string> Products { get; internal set; }
        /// <summary>False when the rule is for products this guide doesn't cover: it never applies.</summary>
        public bool ForOurProducts { get; internal set; }
        public RuleState State { get; internal set; }
        /// <summary>What the guides and the project say about it: "off", "on", a level, or null.</summary>
        public string Setting { get; internal set; }
        /// <summary>The row or line in effect that changed the rule (who, when, why), or null when nothing changed it.</summary>
        public RuleOverride Change { get; internal set; }
        /// <summary>True when <see cref="Change"/> is the open project's own exception (not a style guide row).</summary>
        public bool ChangeIsProject { get; internal set; }
        /// <summary>True when the change in effect shipped with House Style (a default of the read-only guide).</summary>
        public bool IsDefault { get { return Change != null && Change.IsDefault; } }
        /// <summary>Other rules that flag the same words: turning this one off shows theirs (keys, in guide order).</summary>
        public List<string> Overlaps { get; internal set; }
        /// <summary>The newest open request to the owners about this rule, or null.</summary>
        public GuideRequest OpenRequest { get; internal set; }
        /// <summary>True when <see cref="OpenRequest"/> was sent by this writer.</summary>
        public bool RequestIsMine { get; internal set; }

        /// <summary>"On", "Off", "Level changed", with "(Default)" when the change shipped with House Style.</summary>
        public string StateLabel
        {
            get
            {
                var s = State == RuleState.Off ? "Disabled" : State == RuleState.LevelChanged ? "Level changed" : "Enabled";
                return IsDefault ? s + " (Default)" : s;
            }
        }

        /// <summary>"Error", "Warning", "Tip", "Off", or "AI review" for a rule the AI applies (it judges each finding).</summary>
        public string LevelLabel
        {
            get
            {
                if (State == RuleState.Off) return "Disabled";
                if (!IsTable) return "AI review";
                return FlareAI.Core.Severity.Label(Level ?? DefaultLevel ?? "warning");
            }
        }

        internal string Haystack;
    }

    /// <summary>What the Style rules view shows: a search and filters, all optional.</summary>
    public sealed class CatalogFilter
    {
        /// <summary>Words that must all appear (in the text, id, page, section, Avoid/Use, examples), any order and case.</summary>
        public string Text { get; set; }
        /// <summary>Only rules from this source (<see cref="CatalogRule.Source"/>), or null.</summary>
        public string Source { get; set; }
        public RuleState? State { get; set; }
        /// <summary>True: only rules whose findings carry a fix; false: only rules without one.</summary>
        public bool? HasFix { get; set; }
        /// <summary>Only rules limited to this Content tag; <see cref="RuleCatalog.Everywhere"/> for rules with no Content tag; null for all.</summary>
        public string Content { get; set; }
    }

    /// <summary>
    /// Every rule of the layered guide exactly once (Google, the guides in order, the open project), with its state from
    /// the same overrides the checker uses, the change in effect (who, when, why; "Default" for what ships with House Style),
    /// open requests, and the groups of rules that flag the same words. Search and filter for the Style rules view.
    /// </summary>
    public sealed class RuleCatalog
    {
        /// <summary>The Content filter value for rules that apply everywhere.</summary>
        public const string Everywhere = "(everywhere)";

        public List<CatalogRule> Rules { get; private set; }
        /// <summary>The sources in guide order.</summary>
        public List<string> Sources { get; private set; }
        /// <summary>Every Content tag some rule is limited to, sorted.</summary>
        public List<string> ContentTags { get; private set; }

        private readonly Dictionary<string, CatalogRule> _byKey = new Dictionary<string, CatalogRule>(StringComparer.OrdinalIgnoreCase);

        private RuleCatalog() { }

        /// <summary>The rule with this id (or generated rule name), or null.</summary>
        public CatalogRule Find(string key)
        {
            CatalogRule r;
            return key != null && _byKey.TryGetValue(key, out r) ? r : null;
        }

        /// <summary>The rules matching every part of <paramref name="filter"/>, in catalogue order.</summary>
        public List<CatalogRule> Search(CatalogFilter filter)
        {
            filter = filter ?? new CatalogFilter();
            var terms = (filter.Text ?? "").ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return Rules.Where(r =>
                (filter.Source == null || r.Source == filter.Source) &&
                (filter.State == null || r.State == filter.State) &&
                (filter.HasFix == null || r.HasFix == filter.HasFix) &&
                (filter.Content == null || (filter.Content == Everywhere ? r.Content.Count == 0 : r.Content.Contains(filter.Content, StringComparer.OrdinalIgnoreCase))) &&
                terms.All(t => r.Haystack.Contains(t))).ToList();
        }

        /// <summary>The rules that flag the same words as <paramref name="rule"/> (not itself).</summary>
        public List<CatalogRule> OverlapsOf(CatalogRule rule)
        {
            return rule == null ? new List<CatalogRule>() : rule.Overlaps.Select(Find).Where(r => r != null).ToList();
        }

        /// <summary>
        /// The catalogue of <paramref name="comp"/> (from <see cref="StyleEngine.Compose"/>: the same guide the checker uses).
        /// </summary>
        /// <param name="isDefault">True for a read-only guide shipped with House Style: its rows are labelled Default.</param>
        /// <param name="requests">Requests to the owners (<see cref="GuideRequests.Load"/>); the open ones are shown on their rules.</param>
        /// <param name="isMine">True for a request this writer sent.</param>
        public static RuleCatalog Build(Composition comp, Func<string, bool> isDefault = null, IEnumerable<GuideRequest> requests = null, Func<GuideRequest, bool> isMine = null)
        {
            if (comp == null) throw new ArgumentNullException("comp");
            var cat = new RuleCatalog { Rules = new List<CatalogRule>() };
            var layers = comp.Layers;
            var projectIndex = comp.ProjectRoot != null ? layers.Count - 1 : -1;
            var sources = new string[layers.Count];
            for (var i = 0; i < layers.Count; i++) sources[i] = SourceOf(layers[i], i, i == projectIndex, isDefault);

            // The changes in effect, in guide order (a later one wins, as in the checker): each guide's rows, then the project's.
            var changes = new List<KeyValuePair<RuleOverride, bool>>();
            for (var i = 1; i < layers.Count; i++)
            {
                if (layers[i].IsBuiltIn) continue;   // no folder, so no changes to read
                var root = layers[i].Path;
                var project = i == projectIndex;
                var def = !project && (layers[i].OverridesOnly || isDefault != null && isDefault(root));
                List<RuleOverride> rows;
                try { rows = RuleOverrides.Read(root, project ? sources[i] : def ? RuleOverrides.DefaultScope : sources[i]); }
                catch (Exception ex) { Diagnostics.Log("catalogue: changes in " + root, ex); continue; }
                foreach (var o in rows)
                {
                    if (o.IsAiName) continue;
                    o.IsDefault = def;
                    changes.Add(new KeyValuePair<RuleOverride, bool>(o, project));
                }
            }

            var open = (requests ?? Enumerable.Empty<GuideRequest>()).Where(r => r.IsOpen).OrderBy(r => r.Created).ToList();
            var lintById = new Dictionary<string, LintRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in comp.Rules) if (l.Id != null) lintById[l.Id] = l;
            var layerIndex = new Dictionary<GuideLayer, int>();
            for (var i = 0; i < layers.Count; i++) layerIndex[layers[i]] = i;

            // Each citable rule once: a later guide's rule with the same id stands for the earlier one (it is the one cited).
            var items = new Dictionary<string, KeyValuePair<int, GuideRule>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            for (var i = 0; i < layers.Count; i++)
                foreach (var g in layers[i].Items)
                {
                    if (!items.ContainsKey(g.Id)) order.Add(g.Id);
                    items[g.Id] = new KeyValuePair<int, GuideRule>(i, g);
                }
            foreach (var id in order)
            {
                var g = items[id].Value;
                LintRule l;
                lintById.TryGetValue(id, out l);
                cat.Add(Entry(comp, items[id].Key, sources[items[id].Key], g, l));
            }
            // Generated rules (project variables, glossary terms) have no id: they are named.
            for (var i = 0; i < layers.Count; i++)
                foreach (var l in layers[i].Rules.Where(x => x.Id == null))
                    if (cat.Find(l.Name) == null) cat.Add(Entry(comp, i, sources[i], null, l));

            foreach (var r in cat.Rules)
            {
                var setting = comp.SettingOf(r.Id, r.Name);
                r.Setting = setting;
                if (r.IsTable)
                {
                    var lint = r.Id != null ? lintById[r.Id] : comp.Rules.First(x => x.Name == r.Name);
                    r.Level = comp.LevelOf(lint);
                    r.State = r.Level == null ? RuleState.Off : string.Equals(r.Level, r.DefaultLevel, StringComparison.OrdinalIgnoreCase) ? RuleState.On : RuleState.LevelChanged;
                }
                else r.State = setting == "off" ? RuleState.Off : setting != null && setting != "on" ? RuleState.LevelChanged : RuleState.On;
                var change = changes.LastOrDefault(c => Names(c.Key.RuleId, r));
                if (change.Key != null && !(change.Key.IsDefault && change.Key.IsTurnOn))   // a default "on" row changes nothing
                {
                    r.Change = change.Key;
                    r.ChangeIsProject = change.Value;
                }
                var req = open.LastOrDefault(q => Names(q.RuleId, r) || Names(q.Rule, r));
                if (req != null) { r.OpenRequest = req; r.RequestIsMine = isMine != null && isMine(req); }
            }

            Group(cat, r => { LintRule l; return r.Id != null && lintById.TryGetValue(r.Id, out l) ? l : null; });
            foreach (var r in cat.Rules) r.Haystack = Haystack(r);
            cat.Sources = cat.Rules.OrderBy(r => r.SourceIndex).Select(r => r.Source).Distinct().ToList();
            cat.ContentTags = cat.Rules.SelectMany(r => r.Content).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            return cat;
        }

        private void Add(CatalogRule r)
        {
            Rules.Add(r);
            _byKey[r.Key] = r;
        }

        private static CatalogRule Entry(Composition comp, int index, string source, GuideRule g, LintRule l)
        {
            var r = new CatalogRule
            {
                Key = g != null ? g.Id : l.Name, Id = g != null ? g.Id : null, Name = l != null ? l.Name : null,
                Origin = g != null ? g.Layer : l.Layer, SourceIndex = index, Source = source,
                Page = g != null ? g.Page ?? g.Section : Generated(l), Section = g != null && g.Section != g.Page ? g.Section : "",
                Text = g != null ? g.Text : GeneratedText(l), Title = g != null ? RuleWording.Display(g.Text, l != null ? l.Example : g.Example, g.Bad) : GeneratedText(l), Link = g != null ? g.Link : l.Link, File = g != null ? g.Source : null,
                IsTable = l != null, IsPattern = l != null && l.IsPattern, Action = l != null ? l.Action : null,
                Avoid = l != null ? new List<string>(l.Avoid) : new List<string>(), Use = l != null ? l.Use : null, Example = l != null ? l.Example : null,
                Bad = g != null ? g.Bad : "", Good = g != null ? g.Good : "", Terms = g != null ? new List<string>(g.Triggers) : new List<string>(),
                DefaultLevel = l != null ? l.Severity : null,
                Content = new List<string>(g != null ? g.Applies : l.Applies), Products = new List<string>(g != null ? g.Products : l.Products),
                Overlaps = new List<string>()
            };
            r.ForOurProducts = comp.ForOurProducts(r.Products);
            return r;
        }

        private static string Generated(LintRule l) { return l.LocalName == "Variables" ? "Variables" : l.LocalName == "GlossaryTerms" ? "Glossary terms" : l.LocalName; }

        private static string GeneratedText(LintRule l)
        {
            if (l.LocalName == "Variables") return "Insert the variable instead of typing its value (" + l.Swaps.Count + " values).";
            if (l.LocalName == "GlossaryTerms") return "Write glossary terms as the glossary spells them (" + l.Swaps.Count + " spellings).";
            return l.Why ?? "";
        }

        /// <summary>True when <paramref name="name"/> (an override or request's rule) names the rule: its id, or its checker name.</summary>
        private static bool Names(string name, CatalogRule r)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (r.Id != null && string.Equals(name, r.Id, StringComparison.OrdinalIgnoreCase)) return true;
            if (r.Name == null) return false;
            var full = name.IndexOf('.') < 0 && name.IndexOf(':') < 0 ? "Google." + name : name;
            return string.Equals(full, r.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static string SourceOf(GuideLayer layer, int index, bool project, Func<string, bool> isDefault)
        {
            if (index == 0) return "Google";
            if (layer.IsBuiltIn) return "Built-in checks";
            var folder = Path.GetFileName((layer.Path ?? "").TrimEnd('\\', '/'));
            if (project) return "Project " + folder;
            if (layer.OverridesOnly) return RuleOverrides.DefaultScope;
            if (GuideSeed.IsPersonal(layer.Path)) return "My style guide";
            if (isDefault != null && isDefault(layer.Path)) return string.Equals(layer.Name, "company", StringComparison.OrdinalIgnoreCase) ? StyleEngine.StarterRules : Cap(layer.Name);
            return "Team style guide (" + folder + ")";
        }

        private static string Cap(string s) { return string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        private static string Haystack(CatalogRule r)
        {
            var parts = new List<string> { r.Key, r.Name, r.Source, r.Page, r.Section, r.Text, r.Title, r.Use, r.Example, r.Bad, r.Good };
            parts.AddRange(r.Avoid);
            parts.AddRange(r.Terms);
            parts.AddRange(r.Content);
            return string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)).ToArray()).ToLowerInvariant();
        }

        // ---- rules that flag the same words ------------------------------------------------------------------

        /// <summary>
        /// Groups the table rules that flag the same words, as the checker merges them into one finding: the same word or
        /// phrase, a phrase inside a longer one ("please" in "please note that"), or a pattern that matches inside a rule's
        /// words (or whose own match holds them). Each rule lists the rules it can share a finding with in <see cref="CatalogRule.Overlaps"/>.
        /// </summary>
        private static void Group(RuleCatalog cat, Func<CatalogRule, LintRule> lint)
        {
            var table = cat.Rules.Where(r => r.IsTable && r.Id != null).ToList();
            // Direct neighbours only (what one finding can stand for), not chains: "please" and "please note that" share
            // words, "note that" and "please note that" too, but "please" and "note that" never flag the same words.
            var near = table.ToDictionary(r => r, r => new HashSet<CatalogRule>());
            Action<CatalogRule, CatalogRule> union = (a, b) => { if (a == b) return; near[a].Add(b); near[b].Add(a); };

            // Each literal phrase, normalized, and the rules that avoid it.
            var phrases = new Dictionary<string, List<CatalogRule>>(StringComparer.Ordinal);
            foreach (var r in table.Where(r => !r.IsPattern))
                foreach (var a in r.Avoid)
                {
                    var p = Norm(a);
                    if (p.Length == 0) continue;
                    List<CatalogRule> list;
                    if (!phrases.TryGetValue(p, out list)) phrases[p] = list = new List<CatalogRule>();
                    if (!list.Contains(r)) list.Add(r);
                }
            foreach (var list in phrases.Values) foreach (var a in list) foreach (var b in list) union(a, b);

            // A phrase inside a longer one, whole words.
            var byWord = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var p in phrases.Keys)
                foreach (var w in p.Split(' ').Distinct())
                {
                    List<string> list;
                    if (!byWord.TryGetValue(w, out list)) byWord[w] = list = new List<string>();
                    list.Add(p);
                }
            Action<string, IList<CatalogRule>> within = (text, owners) =>
            {
                var padded = " " + text + " ";
                foreach (var w in text.Split(' ').Distinct())
                {
                    List<string> candidates;
                    if (!byWord.TryGetValue(w, out candidates)) continue;
                    foreach (var p in candidates)
                        if (padded.Contains(" " + p + " ")) foreach (var r in phrases[p]) foreach (var o in owners) union(o, r);
                }
            };
            foreach (var p in phrases.Keys) within(p, phrases[p]);

            // Patterns: one that matches inside a phrase, or whose match on its own example holds another rule's match
            // (a phrase, "Ctrl" in "Ctrl+C", or another pattern's), found with that rule's own expressions.
            var swaps = new Dictionary<CatalogRule, List<Regex>>();
            foreach (var r in table)
            {
                var l = lint(r);
                swaps[r] = l == null ? new List<Regex>() : l.Swaps.Select(s => s.Key).ToList();
            }
            foreach (var r in table.Where(r => r.IsPattern && r.Avoid.Count > 0))
            {
                Regex rx;
                try { rx = new Regex(r.Avoid[0], RegexOptions.CultureInvariant | RegexOptions.IgnoreCase); }
                catch (ArgumentException) { continue; }
                foreach (var p in phrases.Keys)
                    if (rx.Match(p).Length > 0) foreach (var o in phrases[p]) union(r, o);
            }
            // Each rule's example, as the checker sees it: another rule's match on the same words, inside them, or around them.
            Func<Match, Match, bool> overlap = (a, b) => (b.Index >= a.Index && b.Index + b.Length <= a.Index + a.Length) || (a.Index >= b.Index && a.Index + a.Length <= b.Index + b.Length);
            foreach (var r in table.Where(r => !string.IsNullOrEmpty(r.Example)))
            {
                var mine = swaps[r].SelectMany(x => x.Matches(r.Example).Cast<Match>()).Where(m => m.Length > 0).ToList();
                if (mine.Count == 0) continue;
                foreach (var o in table)
                    if (o != r && swaps[o].Any(x => x.Matches(r.Example).Cast<Match>().Any(n => n.Length > 0 && mine.Any(m => overlap(m, n))))) union(r, o);
            }

            var position = new Dictionary<CatalogRule, int>();
            for (var i = 0; i < cat.Rules.Count; i++) position[cat.Rules[i]] = i;
            foreach (var kv in near.Where(kv => kv.Value.Count > 0))
                kv.Key.Overlaps = kv.Value.OrderBy(o => position[o]).Select(o => o.Key).ToList();
        }

        private static string Norm(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), @"\s+", " ").Trim(); }
    }
}
