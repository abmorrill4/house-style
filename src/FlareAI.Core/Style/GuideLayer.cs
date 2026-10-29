using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FlareAI.Core.Editing;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Style
{
    /// <summary>One row of a guide table, as a lint rule: words or a pattern to avoid, and what to do instead.</summary>
    public sealed class LintRule
    {
        public string Id { get; set; }            // citable guide id, e.g. "company:terminology/3" (null for generated rules)
        public string Name { get; set; }          // display/override name, e.g. "Company.Terminology3" (set by the composition)
        public string LocalName { get; set; }     // the part after the style name: "Terminology3"
        public string Layer { get; set; }
        public string Severity { get; set; }
        /// <summary>"replace", "remove" or "flag".</summary>
        public string Action { get; set; }
        public string Why { get; set; }
        public string Link { get; set; }
        public string Variable { get; set; }       // generated variable rule: the variable this value belongs to
        public bool IgnoreCase { get; set; }
        /// <summary>True for a Pattern/Use row: <see cref="Avoid"/> holds one regular expression, not words.</summary>
        public bool IsPattern { get; set; }
        /// <summary>Each alternative: the regex that finds it and its replacement (null when Action isn't "replace").</summary>
        public List<KeyValuePair<Regex, string>> Swaps = new List<KeyValuePair<Regex, string>>();
        public List<string> Applies = new List<string>();   // Content condition tags (empty = everywhere)
        public List<string> Products = new List<string>();  // Product condition tags (empty = every product)
        // For validation: where the row is and what it says.
        public string File, Use, Example;
        public int Row;
        public List<string> Avoid = new List<string>();
    }

    /// <summary>
    /// One layer of the style guide: a folder, or a Flare project whose Content/Resources/StyleGuide holds the guide
    /// topics. Topics are ordinary Flare topics: bullets are rules (each gets a citable id), "Bad:"/"Good:" lines are their
    /// examples, an Avoid/Use (or Pattern/Use) table is lint rules, a Rule/Setting table turns earlier rules off or changes
    /// their severity. Condition tags scope a rule: Content.* (what a section must contain), Product.* (which products).
    /// </summary>
    public sealed class GuideLayer
    {
        public static readonly string[] Severities = { "error", "warning", "suggestion" };
        private static readonly HashSet<string> FlagOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "-", "—", "(needs rewrite)", "needs rewrite", "(rewrite)", "rewrite", "n/a" };
        private static readonly HashSet<string> Delete = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "(delete)", "delete", "(remove)", "remove" };

        public string Name { get; private set; }
        /// <summary>True for a layer of override rows only: a read-only guide's defaults (<see cref="LoadOverrides"/>).</summary>
        public bool OverridesOnly { get; private set; }
        public string Path { get; private set; }
        /// <summary>True for a layer built in code (<see cref="InCode"/>): the built-in structure checks, which have no folder.</summary>
        public bool IsBuiltIn { get; private set; }
        /// <summary>The style name used in rule names ("Google", "Company"), unique within a composition.</summary>
        public string Style { get; set; }
        public List<GuideRule> Items = new List<GuideRule>();
        public List<LintRule> Rules = new List<LintRule>();
        public List<KeyValuePair<string, string>> Overrides = new List<KeyValuePair<string, string>>();   // rule name or id → "off" / severity
        public List<GlossaryEntry> Glossary = new List<GlossaryEntry>();
        public List<KeyValuePair<string, string>> Variables = new List<KeyValuePair<string, string>>();   // "Set.Name" → value
        public List<GuideIssue> Problems = new List<GuideIssue>();
        /// <summary>The products this guide covers: the tags of its Product condition tag set (Project\ConditionTagSets\Product.flcts).</summary>
        public List<string> ProductTags = new List<string>();
        /// <summary>
        /// Canonical owners (rule-owners.txt in the layer folder, one rule id per line, first = strongest): when several rules
        /// of one layer flag the same words, the finding names the listed rule. Written by the guide compiler (#56).
        /// </summary>
        public List<string> Owners = new List<string>();
        private readonly List<Section> _sections = new List<Section>();

        private sealed class Section { public string Title, Source; public List<string> Lines = new List<string>(); }

        /// <summary>How Validate and the guide load report a row whose pattern can only match markup (#55).</summary>
        public const string MarkupOnly = "Matches markup, but topics are checked as text";

        public static bool IsDelete(string use) { return Delete.Contains((use ?? "").Trim()); }
        public static bool IsFlagOnly(string use) { return FlagOnly.Contains((use ?? "").Trim()); }

        /// <summary>Loads a folder or a Flare project (the folder containing a .flprj). A guide names itself in FlareAI\layer.txt.</summary>
        /// <param name="skip">Files of this layer not to load (full paths): a later layer has its own version of them.</param>
        public static GuideLayer Load(string path, string name = null, string onlySubfolder = null, string exceptSubfolder = null, Func<string, bool> skip = null)
        {
            var isProject = Directory.Exists(path) && Directory.GetFiles(path, "*.flprj").Length > 0;
            if (name == null)
            {
                var declared = System.IO.Path.Combine(path, @"FlareAI\layer.txt");
                if (File.Exists(declared))
                    name = File.ReadAllLines(declared).Select(l => l.Split('#')[0].Trim()).FirstOrDefault(l => l.Length > 0);
            }
            var layer = new GuideLayer { Name = name ?? new DirectoryInfo(path).Name, Path = path, _skip = skip ?? (f => false) };
            var folder = isProject ? System.IO.Path.Combine(path, @"Content\Resources\StyleGuide") : path;
            if (onlySubfolder != null) folder = System.IO.Path.Combine(folder, onlySubfolder);
            if (Directory.Exists(folder)) layer.LoadFolder(folder, exceptSubfolder == null ? null : System.IO.Path.Combine(folder, exceptSubfolder));
            if (isProject && onlySubfolder == null) layer.LoadFlareProject(path);
            return layer;
        }

        // ---- loading ---------------------------------------------------------------------------------------

        private Func<string, bool> _skip = f => false;

        private bool Has(string file) { return File.Exists(file) && !_skip(file); }

        private void LoadFolder(string folder, string except)
        {
            var glossary = System.IO.Path.Combine(folder, "glossary.json");
            if (Has(glossary))
                foreach (var t in JObject.Parse(Read(glossary))["terms"] ?? new JArray())
                    Glossary.Add(new GlossaryEntry { Term = (string)t["term"], Note = (string)t["note"], Abbr = (string)t["abbr"], Variants = (t["variants"] ?? new JArray()).Select(v => (string)v).ToList(), Layer = Name });
            var ini = System.IO.Path.Combine(folder, "overrides.ini");
            // Override rows never stand in for each other: a shadowed file's rows still apply (a later layer's row for the
            // same rule wins in the composition), so the defaults shipped with House Style keep reaching every guide.
            if (File.Exists(ini))
                foreach (var o in RuleOverrides.ReadIni(ini)) if (!o.IsAiName) Overrides.Add(new KeyValuePair<string, string>(o.RuleId, o.Setting));
            var owners = System.IO.Path.Combine(folder, "rule-owners.txt");
            if (Has(owners))
                Owners.AddRange(File.ReadAllLines(owners).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0));
            var md = System.IO.Path.Combine(folder, "guide.md");
            if (Has(md)) LoadMarkdown(md);
            var topics = Directory.GetFiles(folder, "*.htm", SearchOption.AllDirectories).Concat(Directory.GetFiles(folder, "*.html", SearchOption.AllDirectories))
                .Where(f => except == null || !f.StartsWith(except + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var t in topics)
                if (!_skip(t)) LoadTopic(t);
                else Overrides.AddRange(OverrideRows(t, Name));   // its rule content is shadowed, its override rows are not
        }

        /// <summary>The Rule/Setting rows of one topic (nothing else of it is loaded).</summary>
        internal static List<KeyValuePair<string, string>> OverrideRows(string topic, string layerName)
        {
            var scratch = new GuideLayer { Name = layerName, Path = System.IO.Path.GetDirectoryName(topic) };
            scratch.LoadTopic(topic);
            return scratch.Overrides;
        }

        /// <summary>
        /// Only the override rows (Rule/Setting tables and overrides.ini) of a guide project's StyleGuide folder, outside its
        /// Google folder: the defaults a read-only guide (the one bundled with House Style) sets for Google's rules. Rule content
        /// is not loaded. The layer is named <paramref name="name"/>.
        /// </summary>
        public static GuideLayer LoadOverrides(string path, string name)
        {
            var layer = new GuideLayer { Name = name, Path = path, OverridesOnly = true };
            var folder = System.IO.Path.Combine(path, @"Content\Resources\StyleGuide");
            if (!Directory.Exists(folder)) return layer;
            var google = System.IO.Path.Combine(folder, "Google") + System.IO.Path.DirectorySeparatorChar;
            var ini = System.IO.Path.Combine(folder, "overrides.ini");
            if (File.Exists(ini))
                foreach (var o in RuleOverrides.ReadIni(ini)) if (!o.IsAiName) layer.Overrides.Add(new KeyValuePair<string, string>(o.RuleId, o.Setting));
            foreach (var t in Directory.GetFiles(folder, "*.htm", SearchOption.AllDirectories)
                         .Where(f => !f.StartsWith(google, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                layer.Overrides.AddRange(OverrideRows(t, name));
            return layer;
        }

        /// <summary>
        /// A layer built in code, with no folder and no guide text (the built-in structure checks): exactly the rules given,
        /// so Guide() prints none of them.
        /// </summary>
        public static GuideLayer InCode(string name, IEnumerable<GuideRule> items, IEnumerable<LintRule> rules)
        {
            var layer = new GuideLayer { Name = name, IsBuiltIn = true };
            layer.Items.AddRange(items);
            layer.Rules.AddRange(rules);
            return layer;
        }

        private void LoadMarkdown(string path)
        {
            Section section = null;
            var n = new Dictionary<string, int>();
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.StartsWith("## ")) { section = new Section { Title = raw.Substring(3).Trim(), Source = System.IO.Path.GetFileName(path) }; _sections.Add(section); continue; }
                if (section == null) continue;
                var bullet = Regex.Match(raw, @"^- (.*)$");
                var example = Regex.Match(raw, @"^\s+- (Bad|Good):\s*(.*)$", RegexOptions.IgnoreCase);
                if (bullet.Success)
                {
                    int k; n.TryGetValue(section.Title, out k); n[section.Title] = ++k;
                    var text = bullet.Groups[1].Value.Trim();
                    var link = Regex.Match(text, @"https?://\S+");
                    var item = AddItem(Slug(section.Title) + "/" + k, section.Title, Regex.Replace(text, @"\s*https?://\S+", "").Trim(), link.Success ? link.Value : null, path, "", "");
                    item.Page = section.Title;
                    section.Lines.Add("- [" + item.Id + "] " + item.Text);
                }
                else if (example.Success && Items.Count > 0)
                {
                    SetExample(Items[Items.Count - 1], example.Groups[1].Value, example.Groups[2].Value.Trim());
                    section.Lines.Add("    " + Cap(example.Groups[1].Value) + ": " + example.Groups[2].Value.Trim());
                }
                else if (raw.Trim().Length > 0) section.Lines.Add(raw);
            }
        }

        private readonly Dictionary<string, XmlDocument> _docs = new Dictionary<string, XmlDocument>(StringComparer.OrdinalIgnoreCase);

        private void LoadTopic(string path)
        {
            var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };   // shared folder: no external entities
            try { doc.Load(path); }
            catch (XmlException e) { Problems.Add(new GuideIssue { Level = "error", Layer = Name, File = path, Location = "", Message = "Not well-formed XML: " + e.Message }); return; }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
            {
                Problems.Add(new GuideIssue { Level = "error", Layer = Name, File = path, Location = "", Message = "Can't read this topic: " + e.Message });
                Diagnostics.Log("Style guide topic " + path + " can't be read", e);
                return;
            }
            var body = BlockSelector.FindBody(doc);
            if (body == null) return;
            var h1 = body.SelectSingleNode(".//*[local-name()='h1']");
            var title = h1 != null ? TextOf(h1) : System.IO.Path.GetFileNameWithoutExtension(path);
            var topic = Slug(System.IO.Path.GetFileNameWithoutExtension(path));
            var state = new TopicState { Topic = topic, Section = new Section { Title = title, Source = System.IO.Path.GetFileName(path) }, Heading = title, Link = new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri, Path = path };
            // A topic whose rules carry keys uses them; a topic with none yet keeps its positional ids until keys are
            // assigned (frozen to those same ids), so feedback already recorded stays attached.
            state.Keyed = doc.SelectNodes("//*[starts-with(@id,'" + KeyPrefix + "')]").Count > 0;
            _docs[path] = doc;
            _sections.Add(state.Section);
            Walk(body, state);
        }

        private sealed class TopicState
        {
            public string Topic, Heading, Link, Path;
            public int N;
            public bool Keyed;
            public HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Section Section;
            public List<string> HeadingConditions = new List<string>();
        }

        private void Walk(XmlNode el, TopicState s)
        {
            foreach (XmlNode child in el.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                var tag = child.LocalName;
                if (tag == "h1" || tag == "h2" || tag == "h3" || tag == "h4")
                {
                    if (tag == "h1") continue;
                    s.Heading = TextOf(child);
                    s.HeadingConditions = Conditions(child);   // a tagged heading scopes every rule under it
                    s.Section.Lines.Add("### " + s.Heading + Scope(s.HeadingConditions));
                }
                else if (tag == "ul" || tag == "ol")
                {
                    foreach (XmlNode li in child.ChildNodes)
                    {
                        if (li.NodeType != XmlNodeType.Element || li.LocalName != "li") continue;
                        string own, bad, good;
                        BulletParts(li, out own, out bad, out good);
                        var item = AddItem(s, (XmlElement)li, s.Heading, own, s.Link, bad, good);
                        Scope(item, s.HeadingConditions.Concat(Conditions(child)).Concat(Conditions(li)));
                        s.Section.Lines.Add("- [" + item.Id + "] " + own + Scope(item.Applies.Select(a => "Content." + a).Concat(item.Products.Select(p => "Product." + p)).ToList()) +
                                            (bad.Length > 0 ? "\n    Bad: " + bad : "") + (good.Length > 0 ? "\n    Good: " + good : ""));
                    }
                }
                else if (tag == "p")
                {
                    var t = TextOf(child);
                    var m = Regex.Match(t, @"^(Bad|Good):\s*(.*)$", RegexOptions.IgnoreCase);
                    if (m.Success && Items.Count > 0 && Items[Items.Count - 1].Source == s.Path)
                    {
                        SetExample(Items[Items.Count - 1], m.Groups[1].Value, m.Groups[2].Value.Trim());
                        s.Section.Lines.Add("    " + Cap(m.Groups[1].Value) + ": " + m.Groups[2].Value.Trim());
                    }
                    else if (t.Length > 0) s.Section.Lines.Add(t);
                }
                else if (tag == "table") LoadTable((XmlElement)child, s);
                else Walk(child, s);   // div, dropDown, …
            }
        }

        private void LoadTable(XmlElement table, TopicState s)
        {
            var trs = table.SelectNodes(".//*[local-name()='tr']").Cast<XmlNode>().ToList();
            var rows = trs.Select(tr => tr.ChildNodes.Cast<XmlNode>().Where(c => c.LocalName == "th" || c.LocalName == "td").Select(TextOf).ToList()).ToList();
            var keep = rows.Select((r, i) => new { r, tr = trs[i] }).Where(x => x.r.Any(c => c.Length > 0)).ToList();
            if (keep.Count == 0) return;
            var header = keep[0].r.Select(h => h.ToLowerInvariant()).ToList();
            Func<string[], int> col = names => { for (var i = 0; i < header.Count; i++) foreach (var n in names) if (header[i].StartsWith(n)) return i; return -1; };
            int avoidC = col(new[] { "avoid" }), patternC = col(new[] { "pattern" }), useC = col(new[] { "use", "prefer", "write" });
            int ruleC = col(new[] { "rule" }), settingC = col(new[] { "setting", "level", "status" });
            var tableConditions = s.HeadingConditions.Concat(Conditions(table)).ToList();
            var file = System.IO.Path.GetFileName(s.Path);

            int termC = col(new[] { "term", "when" }), proseC = col(new[] { "rule", "guidance" });
            if (termC >= 0 && proseC >= 0 && termC != proseC)
            {
                // Term / Rule tables: prose rules about one word or phrase, sent to the AI only when the text uses it.
                int badC = col(new[] { "bad" }), goodC = col(new[] { "good" });
                for (var r = 1; r < keep.Count; r++)
                {
                    var row = keep[r].r;
                    Func<int, string> cell = c => c >= 0 && c < row.Count ? row[c].Trim() : "";
                    var terms = Regex.Split(cell(termC), @"\s*[,;]\s*").Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                    if (terms.Count == 0 || cell(proseC).Length == 0) { Problem("error", file, "table row " + (r + 1), "Row needs a Term and a Rule."); continue; }
                    var item = AddItem(s, (XmlElement)keep[r].tr, s.Heading, cell(proseC), s.Link, cell(badC), cell(goodC));
                    item.Triggers.AddRange(terms);
                    Scope(item, tableConditions.Concat(Conditions(keep[r].tr)));
                    s.Section.Lines.Add("- [" + item.Id + "] (" + string.Join(", ", terms.ToArray()) + ") " + item.Text +
                                        Scope(item.Applies.Select(a => "Content." + a).Concat(item.Products.Select(p => "Product." + p)).ToList()) +
                                        (item.Bad.Length > 0 ? "\n    Bad: " + item.Bad : "") + (item.Good.Length > 0 ? "\n    Good: " + item.Good : ""));
                }
            }
            else if ((avoidC >= 0 || patternC >= 0) && useC >= 0)
            {
                int sevC = col(new[] { "severity", "level" }), whyC = col(new[] { "why", "reason", "note" }), exC = col(new[] { "example" });
                for (var r = 1; r < keep.Count; r++)
                {
                    var row = keep[r].r;
                    Func<int, string> cell = c => c >= 0 && c < row.Count ? row[c].Trim() : "";
                    var isPattern = avoidC < 0;
                    var avoid = isPattern ? new List<string> { cell(patternC) }.Where(a => a.Length > 0).ToList()
                                          : Regex.Split(cell(avoidC), @"[,;\n]+").Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
                    var use = cell(useC);
                    var rowNo = r + 1;
                    if (avoid.Count == 0) { Problem("error", file, "table row " + rowNo, "Row has no " + (isPattern ? "Pattern" : "Avoid") + " text."); continue; }
                    var sev = Severity.Normalize(cell(sevC).Length > 0 ? cell(sevC) : "warning");
                    if (!Severities.Contains(sev)) { Problem("warning", file, "table row " + rowNo, "Unknown severity '" + sev + "'; using warning."); sev = "warning"; }
                    var why = cell(whyC);
                    var shown = isPattern ? "the pattern " + avoid[0] : string.Join(", ", avoid.Select(a => "'" + a + "'").ToArray());
                    var text = "Avoid " + shown + (IsDelete(use) ? " (delete it)." : IsFlagOnly(use) ? " (rewrite)." : " → use '" + use + "'.") + (why.Length > 0 ? " " + why : "");
                    var item = AddItem(s, (XmlElement)keep[r].tr, s.Heading, text, s.Link, "", "");
                    item.IsLint = true;
                    item.Example = cell(exC);
                    Scope(item, tableConditions.Concat(Conditions(keep[r].tr)));
                    LintRule rule;
                    try { rule = BuildRule(avoid, use, sev, why, isPattern); }
                    catch (ArgumentException e) { Problem("error", file, "table row " + rowNo, "Pattern isn't a valid regular expression: " + e.Message); continue; }
                    rule.Id = item.Id; rule.LocalName = Camel(s.Topic) + Camel(item.Id.Substring(item.Id.LastIndexOf('/') + 1)); rule.Layer = Name; rule.Link = s.Link;
                    rule.Applies = item.Applies; rule.Products = item.Products;
                    rule.File = file; rule.Row = rowNo; rule.Avoid = avoid; rule.IsPattern = isPattern; rule.Use = use; rule.Example = cell(exC);
                    var markup = rule.Swaps.Select(x => x.Key.ToString()).FirstOrDefault(LintPatterns.MatchesMarkup);
                    if (markup != null)
                    {
                        // Lint reads a topic's text with the markup flattened away: this row could never fire. Report it, don't load it.
                        Problem("warning", file, "table row " + rowNo + " (" + item.Id + ")", MarkupOnly + " ('" + (isPattern ? avoid[0] : string.Join(", ", avoid.ToArray())) +
                                "'): the row is skipped. Write it as a rule bullet for the reviewer instead.");
                        continue;
                    }
                    Rules.Add(rule);
                    s.Section.Lines.Add("- [" + item.Id + "] " + text + Scope(item.Applies.Select(a => "Content." + a).Concat(item.Products.Select(p => "Product." + p)).ToList()));
                }
            }
            else if (ruleC >= 0 && settingC >= 0)
            {
                for (var r = 1; r < keep.Count; r++)
                {
                    var row = keep[r].r;
                    var rule = ruleC < row.Count ? row[ruleC].Trim() : "";
                    var setting = settingC < row.Count ? row[settingC].Trim().ToLowerInvariant() : "";
                    if (rule.Length == 0) continue;
                    var value = Setting(setting);
                    if (value == null) { Problem("error", file, "table row " + (r + 1), "Setting '" + setting + "' should be off, on, error, warning, or suggestion."); continue; }
                    Overrides.Add(new KeyValuePair<string, string>(rule, value));
                    s.Section.Lines.Add("- " + rule + ": " + value + " in this guide");
                }
            }
        }

        /// <summary>"off", "on" (back on at the rule's own level, over an earlier layer's off or level), or a severity; null when it's none.</summary>
        internal static string Setting(string s)
        {
            s = Severity.Normalize(s);
            if (s == "off" || s == "no" || s == "disabled" || s == "disable" || s == "false") return "off";
            if (s == "on" || s == "yes" || s == "enabled" || s == "enable" || s == "true" || s == "default") return "on";
            return Severities.Contains(s) ? s : null;
        }

        private void LoadFlareProject(string root)
        {
            var products = System.IO.Path.Combine(root, @"Project\ConditionTagSets\Product.flcts");
            if (Has(products))
                try
                {
                    var x = new XmlDocument(); x.Load(products);
                    foreach (XmlElement t in x.GetElementsByTagName("ConditionTag"))
                        if (t.GetAttribute("Name").Length > 0) ProductTags.Add(t.GetAttribute("Name"));
                }
                catch (XmlException e) { Problem("error", "Product.flcts", "", "Not well-formed XML: " + e.Message); }
            foreach (var f in Files(System.IO.Path.Combine(root, @"Project\VariableSets"), "*.flvar"))
            {
                try
                {
                    var x = new XmlDocument(); x.Load(f);
                    foreach (XmlElement v in x.GetElementsByTagName("Variable"))
                    {
                        var value = v.InnerText.Trim();
                        if (value.Length >= 3 && Regex.IsMatch(value, "[A-Za-z]"))
                            Variables.Add(new KeyValuePair<string, string>(System.IO.Path.GetFileNameWithoutExtension(f) + "." + v.GetAttribute("Name"), value));
                    }
                }
                catch (XmlException) { }
            }
            foreach (var f in Files(System.IO.Path.Combine(root, @"Project\Glossaries"), "*.flglo"))
            {
                try
                {
                    var x = new XmlDocument(); x.Load(f);
                    foreach (XmlElement e in x.GetElementsByTagName("GlossaryEntry"))
                    {
                        var d = e.GetElementsByTagName("Definition");
                        var definition = d.Count > 0 ? d[0].InnerText.Trim() : "";
                        foreach (XmlElement t in e.GetElementsByTagName("Term"))
                            if (t.InnerText.Trim().Length > 0) Glossary.Add(new GlossaryEntry { Term = t.InnerText.Trim(), Note = definition, Layer = Name, Variants = new List<string>() });
                    }
                }
                catch (XmlException) { }
            }
            if (Variables.Count > 0)
            {
                // Typed copies of variable values: insert the variable instead (exact case, anywhere in the text).
                var rule = new LintRule { LocalName = "Variables", Layer = Name, Severity = "warning", Action = "flag", Why = "" };
                foreach (var v in Variables)
                    rule.Swaps.Add(new KeyValuePair<Regex, string>(new Regex(Regex.Escape(v.Value), RegexOptions.CultureInvariant), v.Key));
                Rules.Add(rule);
                _sections.Add(new Section { Title = "Variables (type the variable, not the text)", Source = "VariableSets", Lines = Variables.Select(v => "- " + v.Key + ": " + v.Value).ToList() });
            }
            var swaps = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var g in Glossary)
            {
                var c = g.Term;
                foreach (var variant in new[] { TitleCase(c), c.ToUpperInvariant(), c.ToLowerInvariant() }.Distinct())
                    // Skip variants that differ only by a capital first letter: that is just a sentence start.
                    if (variant != c && variant.Length > 3 && UpperFirst(variant) != UpperFirst(c)) swaps[variant] = c;
            }
            if (swaps.Count > 0)
            {
                var rule = new LintRule { LocalName = "GlossaryTerms", Layer = Name, Severity = "warning", Action = "replace", Why = "Glossary term." };
                foreach (var kv in swaps) rule.Swaps.Add(new KeyValuePair<Regex, string>(new Regex(Bounded(Regex.Escape(kv.Key), kv.Key), RegexOptions.CultureInvariant), kv.Value));
                Rules.Add(rule);
            }
        }

        // ---- rules -------------------------------------------------------------------------------------------

        /// <summary>One table row as a lint rule: replace, delete, or flag only. Literal words match as whole words.</summary>
        internal static LintRule BuildRule(List<string> avoid, string use, string severity, string why, bool isPattern)
        {
            var exactCase = isPattern || avoid.Any(a => a != a.ToLowerInvariant());
            var options = RegexOptions.CultureInvariant | (exactCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            var rule = new LintRule { Severity = severity, Why = why, IgnoreCase = !exactCase };
            Func<string, Regex> rx = a => new Regex(isPattern ? a : Bounded(Phrase(a), a), options);
            if (IsDelete(use)) { rule.Action = "remove"; foreach (var a in avoid) rule.Swaps.Add(new KeyValuePair<Regex, string>(rx(a), "")); return rule; }
            if (IsFlagOnly(use)) { rule.Action = "flag"; foreach (var a in avoid) rule.Swaps.Add(new KeyValuePair<Regex, string>(rx(a), null)); return rule; }
            rule.Action = "replace";
            var uses = Regex.Split(use, @"[,;\n]+").Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
            // A replacement that itself ends in a comma ("for example,") is one use, not a list.
            if (use.TrimEnd().EndsWith(",") && uses.Count == 1) uses = new List<string> { use.Trim() };
            for (var i = 0; i < avoid.Count; i++)
                rule.Swaps.Add(new KeyValuePair<Regex, string>(rx(avoid[i]), uses.Count == avoid.Count ? uses[i] : use.Trim()));
            return rule;
        }

        /// <summary>Whitespace in a phrase matches any run of whitespace.</summary>
        private static string Phrase(string a) { return string.Join(@"\s+", a.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape).ToArray()); }

        /// <summary>Whole-word boundaries on the sides that are word characters ("e.g." needs none after the dot).</summary>
        internal static string Bounded(string pattern, string literal)
        {
            if (literal.Length == 0) return pattern;
            return (char.IsLetterOrDigit(literal[0]) ? @"(?<![\w])" : "") + "(?:" + pattern + ")" + (char.IsLetterOrDigit(literal[literal.Length - 1]) ? @"(?![\w])" : "");
        }

        // ---- items and conditions ------------------------------------------------------------------------------

        public const string KeyPrefix = "rule-";

        /// <summary>
        /// A rule written in a topic: its id is "layer:topic/key" from the element's id="rule-key" (permanent), or, until
        /// keys are assigned, "layer:topic/N" by position ("layer:topic/~N" in a topic that already has keys, so a
        /// provisional id can't collide with a permanent one).
        /// </summary>
        private GuideRule AddItem(TopicState s, XmlElement el, string section, string text, string link, string bad, string good)
        {
            var n = ++s.N;
            var attr = el.GetAttribute("id");
            string key = null;
            if (attr.StartsWith(KeyPrefix, StringComparison.Ordinal) && attr.Length > KeyPrefix.Length)
            {
                key = attr.Substring(KeyPrefix.Length);
                if (!s.Keys.Add(key)) { Problem("warning", System.IO.Path.GetFileName(s.Path), "rule " + n, "Two rules share the key '" + key + "' (a copied bullet or row?): Validate gives the copy a new key."); key = null; }
            }
            var item = AddItem(s.Topic + "/" + (key ?? (s.Keyed ? "~" + n : n.ToString())), section, text, link, s.Path, bad, good);
            item.HasKey = key != null;
            item.Page = s.Section.Title;
            item.Element = el;
            item.Position = n;
            return item;
        }

        /// <summary>
        /// Gives every rule without a key a permanent one, written into its topic as id="rule-…". A topic with no keys yet
        /// is frozen to its current positional ids (so recorded feedback stays attached); otherwise new rules get a new
        /// short key. Returns how many rules got keys. Only topics that change are saved.
        /// </summary>
        /// <param name="onlyTopic">Only this topic's rules (before one of them is edited); null for every topic.</param>
        public int AssignKeys(string onlyTopic = null)
        {
            var assigned = 0;
            var rnd = new Random();
            foreach (var topic in Items.Where(i => i.Element != null && (onlyTopic == null || i.Source == onlyTopic)).GroupBy(i => i.Source))
            {
                XmlDocument doc;
                if (!_docs.TryGetValue(topic.Key, out doc)) continue;
                var freeze = !topic.Any(i => i.HasKey) && doc.SelectNodes("//*[starts-with(@id,'" + KeyPrefix + "')]").Count == 0;
                var used = new HashSet<string>(topic.Where(i => i.HasKey).Select(i => i.Id.Substring(i.Id.LastIndexOf('/') + 1)), StringComparer.OrdinalIgnoreCase);
                var changed = 0;
                foreach (var item in topic.Where(i => !i.HasKey))
                {
                    string key;
                    if (freeze) key = item.Position.ToString();
                    else do key = "k" + Base36(rnd.Next(36 * 36 * 36, 36 * 36 * 36 * 36)); while (!used.Add(key));
                    item.Element.SetAttribute("id", KeyPrefix + key);
                    changed++;
                }
                if (changed == 0) continue;
                try { Save(doc, topic.Key); assigned += changed; }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { Diagnostics.Log("keys: can't write " + topic.Key, ex); }   // read-only (bundled guide, locked file)
            }
            return assigned;
        }

        /// <summary>
        /// The parts of a rule an owner can edit, as written: a table row's cells under their headings (Avoid, Use, Why,
        /// Example, or whatever the table names them; not its level, which Change Level sets and records), or a bullet's
        /// wording with its Bad and Good examples. The rule is
        /// the one with this id in topic <paramref name="file"/> whose wording is still <paramref name="text"/>; null when the
        /// topic no longer has it there (the guide changed since the caller read it).
        /// </summary>
        public List<KeyValuePair<string, string>> RuleFields(string id, string file, string text)
        {
            var item = Find(id, file, text);
            return item == null ? null : Fields(item.Element);
        }

        private static List<KeyValuePair<string, string>> Fields(XmlElement el)
        {
            var fields = new List<KeyValuePair<string, string>>();
            if (el.LocalName == "tr")
            {
                var heads = HeadsOf(el);
                var cells = Cells(el);
                for (var i = 0; i < cells.Count; i++)
                    if (!IsLevelHeading(Head(heads, i))) fields.Add(new KeyValuePair<string, string>(Head(heads, i), TextOf(cells[i])));
                return fields;
            }
            var examples = ExampleNodes(el);
            fields.Add(new KeyValuePair<string, string>("Rule", OwnWords(el)));
            foreach (var kind in new[] { "Bad", "Good" })
                fields.Add(new KeyValuePair<string, string>(kind, examples.ContainsKey(kind) ? ExampleText(examples[kind]) : ""));
            return fields;
        }

        /// <summary>
        /// Writes the fields the owner changed (<paramref name="fields"/>, from <see cref="RuleFields"/>) back into the rule's
        /// row or bullet and saves its topic; the rule keeps its id. Fields that didn't change keep their formatting; a
        /// changed one becomes plain text. Returns the topic written, or null when the rule isn't there any more.
        /// </summary>
        public string EditRule(string id, string file, string text, IList<KeyValuePair<string, string>> fields)
        {
            var item = Find(id, file, text);
            if (item == null || fields == null) return null;
            var el = item.Element;
            var before = Fields(el);
            Func<string, string> now = k => fields.Where(f => f.Key == k).Select(f => (f.Value ?? "").Trim()).FirstOrDefault();
            Func<string, bool> changed = k => { var v = now(k); return v != null && v != before.First(f => f.Key == k).Value; };
            if (el.LocalName == "li" && changed("Rule") && now("Rule").Length == 0) throw new ArgumentException("A rule needs its wording.");
            if (el.LocalName == "tr")
                foreach (var f in before.Where(f => changed(f.Key)))
                {
                    var key = f.Key.ToLowerInvariant();
                    if ((key.StartsWith("avoid") || key.StartsWith("pattern") || key.StartsWith("rule")) && now(f.Key).Length == 0)
                        throw new ArgumentException(f.Key + " can't be empty. To remove the rule, use Delete Rule.");
                    if (key.StartsWith("pattern"))
                        try { new Regex(now(f.Key)); }
                        catch (ArgumentException ex) { throw new ArgumentException("The pattern isn't valid. " + ex.Message); }
                }
            return SaveRule(item, () =>
            {
                if (el.LocalName == "tr")
                {
                    var cells = Cells(el);
                    var heads = HeadsOf(el);
                    for (var i = 0; i < cells.Count; i++)
                        if (before.Any(f => f.Key == Head(heads, i)) && changed(Head(heads, i))) cells[i].InnerText = now(Head(heads, i));
                    return;
                }
                if (changed("Rule"))
                {
                    var examples = new HashSet<XmlNode>(ExampleNodes(el).Values);
                    foreach (var c in el.ChildNodes.Cast<XmlNode>().Where(c => !IsList(c) && !examples.Contains(c)).ToList()) el.RemoveChild(c);
                    el.InsertBefore(el.OwnerDocument.CreateTextNode(now("Rule")), el.FirstChild);
                }
                foreach (var kind in new[] { "Bad", "Good" })
                {
                    if (!changed(kind)) continue;
                    XmlElement node;
                    ExampleNodes(el).TryGetValue(kind, out node);
                    if (now(kind).Length == 0) { if (node != null) RemoveWithEmptyList(node); continue; }
                    if (node == null)
                    {
                        var nested = el.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().FirstOrDefault(IsList);
                        if (nested == null) { nested = el.OwnerDocument.CreateElement("ul", el.NamespaceURI); el.AppendChild(nested); }
                        node = el.OwnerDocument.CreateElement("li", el.NamespaceURI);
                        nested.AppendChild(node);
                    }
                    node.InnerText = kind + ": " + now(kind);
                }
            });
        }

        /// <summary>
        /// Removes the rule's row or bullet from its topic (with Bad/Good paragraphs that belong to it) and saves the topic.
        /// Returns the topic written, or null when the rule isn't there any more.
        /// </summary>
        public string DeleteRule(string id, string file, string text)
        {
            var item = Find(id, file, text);
            if (item == null) return null;
            var el = item.Element;
            return SaveRule(item, () =>
            {
                if (el.LocalName == "li")
                    foreach (var n in ExampleNodes(el).Values.Where(n => n.ParentNode == el.ParentNode.ParentNode)) n.ParentNode.RemoveChild(n);
                RemoveWithEmptyList(el);
            });
        }

        /// <summary>The rule with this id (its part after the layer name, so a writer's own guide matches whatever layer name
        /// it loads under) in exactly this topic, still worded as <paramref name="text"/>.</summary>
        private GuideRule Find(string id, string file, string text)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(file)) return null;
            string full;
            try { full = System.IO.Path.GetFullPath(file); } catch (Exception) { return null; }
            return Items.FirstOrDefault(i => i.Element != null && _docs.ContainsKey(i.Source) &&
                                             string.Equals(Local(i.Id), Local(id), StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(System.IO.Path.GetFullPath(i.Source), full, StringComparison.OrdinalIgnoreCase) &&
                                             (text == null || i.Text == text));
        }

        private static string Local(string id) { return id.Substring(id.IndexOf(':') + 1); }

        /// <summary>
        /// Gives the rules of the item's topic permanent keys first, so removing or rewording one never moves another rule's
        /// id (and with it the team's settings and feedback for that rule); then applies <paramref name="change"/> and saves.
        /// </summary>
        private string SaveRule(GuideRule item, Action change)
        {
            AssignKeys(item.Source);
            change();
            try { Save(_docs[item.Source], item.Source); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(item.Source, ex); }
            return item.Source;
        }

        /// <summary>Removes a list item, and its list when that leaves the list empty.</summary>
        private static void RemoveWithEmptyList(XmlNode n)
        {
            var parent = n.ParentNode;
            parent.RemoveChild(n);
            if (IsList(parent) && !parent.ChildNodes.Cast<XmlNode>().Any(c => c.NodeType == XmlNodeType.Element)) parent.ParentNode.RemoveChild(parent);
        }

        private static string Head(List<string> heads, int i) { return i < heads.Count && heads[i].Length > 0 ? heads[i] : "Column " + (i + 1); }

        private static bool IsLevelHeading(string h)
        {
            var k = h.ToLowerInvariant();
            return k.StartsWith("severity") || k.StartsWith("level") || k.StartsWith("setting") || k.StartsWith("status");
        }

        private static bool IsList(XmlNode n) { return n.NodeType == XmlNodeType.Element && (n.LocalName == "ul" || n.LocalName == "ol"); }

        private static List<XmlElement> Cells(XmlElement tr)
        {
            return tr.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().Where(c => c.LocalName == "td" || c.LocalName == "th").ToList();
        }

        /// <summary>The headings of the row's table: its first row with any text, as the guide reads it.</summary>
        private static List<string> HeadsOf(XmlElement tr)
        {
            for (XmlNode t = tr.ParentNode; t != null; t = t.ParentNode)
                if (t.LocalName == "table")
                {
                    var first = t.SelectNodes(".//*[local-name()='tr']").Cast<XmlElement>().FirstOrDefault(r => Cells(r).Any(c => TextOf(c).Length > 0));
                    return first == null || first == tr ? new List<string>() : Cells(first).Select(c => TextOf(c)).ToList();
                }
            return new List<string>();
        }

        private static readonly Regex ExampleLine = new Regex(@"^(Bad|Good):\s*", RegexOptions.IgnoreCase);

        /// <summary>
        /// The elements holding a bullet's Bad and Good examples, as the guide reads them: a nested list item, an inline
        /// paragraph, or, for the last bullet of a list, a "Bad:"/"Good:" paragraph right after the list. A later one wins.
        /// </summary>
        private static Dictionary<string, XmlElement> ExampleNodes(XmlElement li)
        {
            var map = new Dictionary<string, XmlElement>(StringComparer.OrdinalIgnoreCase);
            Func<XmlElement, bool> add = e =>
            {
                var m = ExampleLine.Match(TextOf(e));
                if (!m.Success) return false;
                var kind = m.Groups[1].Value;
                map[char.ToUpperInvariant(kind[0]) + kind.Substring(1).ToLowerInvariant()] = e;
                return true;
            };
            foreach (var c in li.ChildNodes.Cast<XmlNode>().OfType<XmlElement>())
            {
                if (IsList(c)) { foreach (var sub in c.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().Where(x => x.LocalName == "li")) add(sub); }
                else add(c);
            }
            var list = li.ParentNode;
            if (list != null && li == list.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().LastOrDefault(e => e.LocalName == "li"))
                for (var n = list.NextSibling; n != null; n = n.NextSibling)
                {
                    if (n.NodeType != XmlNodeType.Element) continue;
                    if (n.LocalName != "p" || !add((XmlElement)n)) break;
                }
            return map;
        }

        private static string ExampleText(XmlElement e) { return ExampleLine.Replace(TextOf(e), "").Trim(); }

        /// <summary>A bullet's wording without its examples (nested lists and "Bad:"/"Good:" lines).</summary>
        private static string OwnWords(XmlElement li)
        {
            var sb = new StringBuilder();
            foreach (XmlNode c in li.ChildNodes)
            {
                if (IsList(c) || c.NodeType == XmlNodeType.Element && ExampleLine.IsMatch(TextOf(c))) continue;
                sb.Append(c.InnerText);
            }
            return Regex.Replace(sb.ToString(), @"[ \t\r\n\f\v]+", " ").Trim();
        }

        private static string Base36(int v)
        {
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            var sb = new StringBuilder();
            do { sb.Insert(0, digits[v % 36]); v /= 36; } while (v > 0);
            return sb.ToString();
        }

        /// <summary>Saves a topic as it was read: same declaration, same byte-order mark, whitespace untouched.</summary>
        internal static void Save(XmlDocument doc, string path)
        {
            GuideCopy.RefuseWrite(path);   // a saved copy of a team guide is never written (#246)
            var bom = true;   // a new topic gets a BOM, as Flare writes them
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            }
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(bom), OmitXmlDeclaration = !(doc.FirstChild is XmlDeclaration) };
            using (var w = XmlWriter.Create(path, settings)) doc.Save(w);
        }

        private GuideRule AddItem(string localId, string section, string text, string link, string source, string bad, string good)
        {
            var item = new GuideRule { Id = Name + ":" + localId, Layer = Name, Section = section, Text = text, Link = link, Source = source, Bad = bad ?? "", Good = good ?? "" };
            Items.Add(item);
            return item;
        }

        private static void SetExample(GuideRule item, string kind, string text)
        {
            if (kind.Equals("bad", StringComparison.OrdinalIgnoreCase)) item.Bad = text; else item.Good = text;
        }

        /// <summary>The element's own Flare condition tags (MadCap:conditions="Content.Code,Product.Android").</summary>
        private static List<string> Conditions(XmlNode el)
        {
            var e = el as XmlElement;
            var raw = e == null ? "" : e.GetAttribute("conditions", BlockSelector.MadCapNs);
            return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        }

        private void Scope(GuideRule item, IEnumerable<string> conditions)
        {
            foreach (var c in conditions.Distinct())
            {
                var dot = c.IndexOf('.');
                var set = dot > 0 ? c.Substring(0, dot) : "";
                var tag = dot > 0 ? c.Substring(dot + 1) : c;
                if (set.Equals("Content", StringComparison.OrdinalIgnoreCase) && !item.Applies.Contains(tag)) item.Applies.Add(tag);
                else if (set.Equals("Product", StringComparison.OrdinalIgnoreCase) && !item.Products.Contains(tag)) item.Products.Add(tag);
                // Other condition sets (Default.Print, …) are the guide's own output conditions, not rule scope.
            }
        }

        private static string Scope(List<string> conditions)
        {
            var c = conditions.Where(x => x.StartsWith("Content.", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Product.", StringComparison.OrdinalIgnoreCase)).ToList();
            return c.Count == 0 ? "" : "  (applies to: " + string.Join(", ", c.Select(x => x.Substring(x.IndexOf('.') + 1)).ToArray()) + ")";
        }

        // ---- output ----------------------------------------------------------------------------------------------

        /// <summary>The layer as guide text for the AI: sections with [id] bullets and their examples.</summary>
        public string Guide(Func<GuideRule, bool> include = null)
        {
            var parts = new List<string>();
            foreach (var s in _sections)
            {
                // Filtered (for the AI): rules and the headings above them only; context paragraphs stay in the topics.
                var lines = include == null ? s.Lines : s.Lines.Where(l => (l.StartsWith("- [") || l.StartsWith("### ") || l.StartsWith("    ")) && Included(l, include)).ToList();
                if (include != null)   // drop headings left with no rule under them
                    lines = lines.Where((l, i) => !l.StartsWith("### ") || (i + 1 < lines.Count && !lines[i + 1].StartsWith("### "))).ToList();
                var body = string.Join("\n", lines.ToArray()).Trim();
                if (body.Length > 0 && (include == null || lines.Any(l => l.StartsWith("- [")))) parts.Add("## " + s.Title + "  (" + s.Source + ")\n" + body);
            }
            return string.Join("\n\n", parts.ToArray());
        }

        private bool Included(string line, Func<GuideRule, bool> include)
        {
            var m = Regex.Match(line, @"^- \[([^\]]+)\]");
            if (!m.Success) return true;
            var item = Items.FirstOrDefault(i => i.Id == m.Groups[1].Value);
            return item == null || include(item);
        }

        private void Problem(string level, string file, string location, string message)
        {
            Problems.Add(new GuideIssue { Level = level, Layer = Name, File = file, Location = location, Message = message });
        }

        // ---- text helpers --------------------------------------------------------------------------------------

        /// <summary>The element's text with source whitespace collapsed. A nonbreaking space is content (an Example can need one), so it stays.</summary>
        internal static string TextOf(XmlNode n) { return Regex.Replace(n.InnerText, @"[ \t\r\n\f\v]+", " ").Trim(' ', '\t', '\r', '\n'); }

        /// <summary>(own text, bad, good) of a list item: nested "Bad:"/"Good:" lines are its examples.</summary>
        private static void BulletParts(XmlNode li, out string own, out string bad, out string good)
        {
            bad = good = "";
            var o = new StringBuilder();
            var extra = new List<string>();
            foreach (XmlNode c in li.ChildNodes)
            {
                if (c.NodeType == XmlNodeType.Text || c.NodeType == XmlNodeType.SignificantWhitespace || c.NodeType == XmlNodeType.Whitespace) { o.Append(c.Value); continue; }
                if (c.NodeType != XmlNodeType.Element) continue;
                if (c.LocalName == "ul" || c.LocalName == "ol")
                {
                    foreach (XmlNode sub in c.ChildNodes)
                    {
                        if (sub.LocalName != "li") continue;
                        var st = TextOf(sub);
                        var m = Regex.Match(st, @"^(Bad|Good):\s*(.*)$", RegexOptions.IgnoreCase);
                        if (m.Success) { if (m.Groups[1].Value.ToLowerInvariant() == "bad") bad = m.Groups[2].Value.Trim(); else good = m.Groups[2].Value.Trim(); }
                        else if (st.Length > 0) extra.Add(st);
                    }
                    continue;
                }
                var t = TextOf(c);
                var mm = Regex.Match(t, @"^(Bad|Good):\s*(.*)$", RegexOptions.IgnoreCase);
                if (mm.Success) { if (mm.Groups[1].Value.ToLowerInvariant() == "bad") bad = mm.Groups[2].Value.Trim(); else good = mm.Groups[2].Value.Trim(); }
                else o.Append(c.InnerText);
            }
            own = Regex.Replace(o.ToString(), @"[ \t\r\n\f\v]+", " ").Trim(' ', '\t', '\r', '\n');
            if (extra.Count > 0) own += " (" + string.Join("; ", extra.ToArray()) + ")";
        }

        internal static string Slug(string s)
        {
            var r = Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return r.Length == 0 ? "x" : r;
        }

        internal static string Camel(string s)
        {
            var r = string.Concat(Regex.Matches(s ?? "", "[A-Za-z0-9]+").Cast<Match>().Select(m => char.ToUpperInvariant(m.Value[0]) + m.Value.Substring(1).ToLowerInvariant()).ToArray());
            return r.Length == 0 ? "X" : r;
        }

        private static string Cap(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant(); }
        private static string UpperFirst(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }
        private static string TitleCase(string s) { return Regex.Replace(s.ToLowerInvariant(), @"\b[a-z]", m => m.Value.ToUpperInvariant()); }
        private static string Read(string path) { return File.ReadAllText(path, Encoding.UTF8); }
        private IEnumerable<string> Files(string dir, string pattern) { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Where(f => !_skip(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>(); }
    }

    /// <summary>Facts about a lint row's regular expression that don't depend on the text it runs on.</summary>
    public static class LintPatterns
    {
        private static readonly Regex Tag = new Regex(@"(?<!\(\?)<\s*/?\s*[A-Za-z!?]", RegexOptions.Compiled);
        private static readonly Regex Entity = new Regex(@"&(?:[A-Za-z]+|#\d+|#x[0-9A-Fa-f]+);", RegexOptions.Compiled);
        // An attribute name (also right after a \b of the pattern itself), then an equals sign close by.
        private static readonly Regex Attribute = new Regex(@"(?:(?<![A-Za-z])|(?<=\\b))(?:colspan|rowspan|alt|src|href|class|style|width|height)\b[^=\r\n]{0,12}=", RegexOptions.Compiled);

        /// <summary>
        /// True when the pattern contains a tag (&lt;br&gt;, &lt;/th&gt;), an entity (&amp;ndash;) or an attribute shape (colspan=):
        /// lint sees the text of a topic, never its markup, so such a row can never fire (#55).
        /// </summary>
        public static bool MatchesMarkup(string pattern)
        {
            return !string.IsNullOrEmpty(pattern) && (Tag.IsMatch(pattern) || Entity.IsMatch(pattern) || Attribute.IsMatch(pattern));
        }
    }

    public sealed class GlossaryEntry
    {
        public string Term, Note, Abbr, Layer;
        public List<string> Variants = new List<string>();
    }
}
