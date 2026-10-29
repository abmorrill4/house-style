using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace FlareAI.Core.Style
{
    /// <summary>
    /// The layered style guide, in process: Google (the Google/ folder of the guide project, or the copy bundled with
    /// the plugin), then the guide project's own rules, then the open Flare project (its variables, glossary and
    /// StyleGuide folder). Later layers win. Lint runs here too: every lint rule is a row of a guide table.
    /// </summary>
    public sealed class StyleEngine : IStyleService
    {
        private readonly Func<IEnumerable<string>> _roots;
        private readonly string _bundled;
        private readonly object _gate = new object();
        private readonly Dictionary<string, KeyValuePair<string, Composition>> _cache = new Dictionary<string, KeyValuePair<string, Composition>>(StringComparer.OrdinalIgnoreCase);

        /// <param name="guideRoots">The team's style guide projects (usually one), in order.</param>
        /// <param name="bundledGuide">The style guide project shipped with the plugin: its Google/ folder is the baseline when a guide has none.</param>
        public StyleEngine(IEnumerable<string> guideRoots, string bundledGuide)
            : this(() => guideRoots, bundledGuide)
        {
        }

        /// <param name="guideRoots">
        /// The guide projects in effect, asked again before each use: a guide created after the engine (the writer's own,
        /// made on their first rule change) becomes a layer without a restart.
        /// </param>
        public StyleEngine(Func<IEnumerable<string>> guideRoots, string bundledGuide)
        {
            _roots = guideRoots ?? (() => new string[0]);
            _bundled = bundledGuide;
        }

        /// <summary>
        /// The guides used in place of a guide that can't be read (a team guide on a share the writer lost access to): the
        /// bundled guide by default; the plugin passes the bundled guide plus the writer's own changes.
        /// </summary>
        public Func<IEnumerable<string>> Fallback { get; set; }

        private readonly HashSet<string> _logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The guide projects that exist now, in order. A guide that can't be read is left out, named in
        /// <paramref name="unreadable"/> with the reason, and the <see cref="Fallback"/> guides stand in for it.
        /// </summary>
        private List<string> Guides(List<string> unreadable, List<string> copied = null, List<string> read = null, Dictionary<string, string> originals = null)
        {
            var list = new List<string>();
            foreach (var g in (_roots() ?? new string[0]).Where(g => !string.IsNullOrEmpty(g)))
            {
                if (!Directory.Exists(g))
                {
                    // A configured folder that isn't there (offline sync, VPN down, drive unplugged, folder renamed): its last
                    // copy stands in, in the same place (#246). With no copy and a fallback (the plugin sets one) it is
                    // unreachable, not silently dropped. Without either (MCP, tools) it's skipped.
                    if (UseCopy(g, list, copied, originals)) continue;
                    if (Fallback == null) continue;
                    unreadable.Add("Can't reach " + g + ".");
                    lock (_gate) if (_logged.Add(g + "|missing")) Diagnostics.Log("Style guide folder " + Path.GetFileName(g.TrimEnd('\\', '/')) + " can't be reached; checking with the fallback guide");
                    continue;
                }
                bool permission;
                var why = WhyUnreadable(g, out permission);
                if (why == null) { list.Add(g); if (read != null && IsTeamGuide(g)) read.Add(g); continue; }
                // A share that dropped mid-check can use its copy; a writer whose access was removed never does.
                if (!permission && UseCopy(g, list, copied, originals)) continue;
                unreadable.Add("Can't read " + g + ": " + why + ".");
                lock (_gate) if (_logged.Add(g + "|" + why)) Diagnostics.Log("Style guide " + g + " can't be read (" + why + "); checking with the fallback guide");
            }
            if (unreadable.Count == 0) return list;
            var fallback = new List<string>();
            foreach (var f in (Fallback != null ? Fallback() : null) ?? new[] { _bundled })
                if (!string.IsNullOrEmpty(f) && Directory.Exists(f) && WhyUnreadable(f) == null && !list.Concat(fallback).Any(x => SamePath(x, f))) fallback.Add(f);
            return fallback.Concat(list).ToList();
        }

        /// <summary>
        /// The last saved copy of a configured team guide (the plugin sets it to <see cref="GuideCopy.Find"/>), or null for
        /// none. Asked only for a team guide that can't be reached; never for the bundled guide or the writer's own.
        /// </summary>
        public Func<string, GuideCopyInfo> CopyOf { get; set; }

        /// <summary>
        /// Called with each configured team guide (not the bundled guide, not the writer's own) that a fresh composition read,
        /// so the plugin can refresh its copy. Never called from inside a lock, and a failure never fails the lint.
        /// </summary>
        public Action<string> GuideRead { get; set; }

        /// <summary>One sentence of the notice per team guide that used its copy: {0} is the configured folder, {1} the date.</summary>
        public const string CopyNotice = "House Style couldn't reach {0}, so it linted with the copy it saved on {1}.";

        /// <summary>The copy's date for the notice: the short date in the writer's culture, plus the time when it's today.</summary>
        public static string CopyDate(DateTime copiedUtc, DateTime now)
        {
            var local = copiedUtc.Kind == DateTimeKind.Local ? copiedUtc : DateTime.SpecifyKind(copiedUtc, DateTimeKind.Utc).ToLocalTime();
            return local.Date == now.Date ? local.ToString("d") + " " + local.ToString("t") : local.ToString("d");
        }

        /// <summary>
        /// The layer name a saved copy loads under: the name the guide gives itself (FlareAI\layer.txt, copied), else the
        /// configured folder's name, as the guide itself would load, so rule ids and changes line up. Null for a guide that isn't a copy.
        /// </summary>
        private static string NameOfCopy(string guide, Dictionary<string, string> originals)
        {
            string original;
            if (originals == null || !originals.TryGetValue(guide, out original)) return null;
            if (File.Exists(Path.Combine(guide, @"FlareAI\layer.txt"))) return null;   // the copy names itself, as the guide does
            return new DirectoryInfo(original.TrimEnd('\\', '/')).Name;
        }

        private bool IsTeamGuide(string g)
        {
            if (_bundled != null && SamePath(g, _bundled)) return false;
            if (!string.IsNullOrEmpty(PersonalGuide) && SamePath(g, PersonalGuide)) return false;
            return !GuideCopy.IsCopy(g);
        }

        /// <summary>Puts the team guide's saved copy in its place, and names the folder and date for the notice. False when there is none.</summary>
        private bool UseCopy(string g, List<string> list, List<string> copied, Dictionary<string, string> originals)
        {
            if (CopyOf == null || !IsTeamGuide(g)) return false;
            GuideCopyInfo copy;
            try { copy = CopyOf(g); }
            catch (Exception ex) { lock (_gate) if (_logged.Add(g + "|copy")) Diagnostics.Log("style guide copy", ex); return false; }
            if (copy == null || string.IsNullOrEmpty(copy.Path) || !Directory.Exists(copy.Path)) return false;
            list.Add(copy.Path);
            if (originals != null) originals[copy.Path] = g;
            if (copied != null) copied.Add(string.Format(CopyNotice, g, CopyDate(copy.CopiedUtc, DateTime.Now)));
            lock (_gate) if (_logged.Add(g + "|copy-used")) Diagnostics.Log("Style guide folder " + Path.GetFileName(g.TrimEnd('\\', '/')) + " can't be reached; checking with its saved copy");
            return true;
        }

        /// <summary>Null when the guide's project file, StyleGuide and FlareAI folders can be listed; else why not, in plain words.</summary>
        internal static string WhyUnreadable(string guide)
        {
            bool permission;
            return WhyUnreadable(guide, out permission);
        }

        /// <param name="permission">True when the reason is a permission failure (the writer's access was removed).</param>
        internal static string WhyUnreadable(string guide, out bool permission)
        {
            permission = false;
            try
            {
                Directory.GetFiles(guide, "*.flprj");
                var style = Path.Combine(guide, @"Content\Resources\StyleGuide");
                if (Directory.Exists(style)) Directory.GetFiles(style, "*", SearchOption.AllDirectories);
                var flareAi = Path.Combine(guide, "FlareAI");
                if (Directory.Exists(flareAi)) Directory.GetFiles(flareAi);
                return null;
            }
            catch (UnauthorizedAccessException) { permission = true; return "you don't have permission to read it"; }
            catch (System.Security.SecurityException) { permission = true; return "you don't have permission to read it"; }
            catch (IOException ex) { return ex.Message.TrimEnd('.', ' ', '\r', '\n'); }
        }

        /// <summary>
        /// Null normally. When a team style guide couldn't be read: which folder and why, and that the check used Google's
        /// guide (and the writer's own changes) instead. Show it with any check result.
        /// </summary>
        public string Notice(string project) { return Compose(project).Notice; }

        /// <summary>The first line of <see cref="Notice"/>.</summary>
        public const string UnreadableNotice = "Your team's style guide couldn't be read, so House Style linted with Google's guide, the starter rules and your own changes.";

        /// <summary>The notice for a team guide with no rules House Style can read; " The folder is {folder}." follows it.</summary>
        public const string EmptyNotice = "Your team's style guide has no rules yet, so House Style linted with Google's guide.";

        /// <summary>
        /// The writer's own guide (%APPDATA%\FlareAI\StyleGuide, set by the plugin): never reported as an empty team guide.
        /// Decided by path, not by the marker file inside the folder, because a shared guide could carry the marker.
        /// </summary>
        public string PersonalGuide { get; set; }

        // ---- composition ---------------------------------------------------------------------------------------

        public Composition Compose(string project)
        {
            var unreadable = new List<string>();
            var copied = new List<string>();
            var read = new List<string>();
            var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var guides = Guides(unreadable, copied, read, originals);
            var root = FlareProjectRoot(project);
            if (root != null && guides.Any(g => SamePath(g, root))) root = null;   // the guide is already a layer
            // One part per guide that couldn't be read: the guides that used their copy first, then the ones that fell back.
            var parts = new List<string>(copied);
            if (unreadable.Count > 0) parts.Add(UnreadableNotice + " " + string.Join(" ", unreadable.ToArray()));
            var notice = parts.Count == 0 ? null : string.Join(" ", parts.ToArray());
            var key = (root ?? "") + "|" + string.Join("|", guides.ToArray()) + "|" + notice;
            // Any change to a guide or project file (a rule turned off, a row added) changes the stamp: the next use reloads.
            var defaults = Defaults(guides);
            var stamp = Stamp(guides.Concat(new[] { GoogleRoot(guides), defaults, root }).Where(p => p != null));
            lock (_gate)
            {
                KeyValuePair<string, Composition> c;
                if (_cache.TryGetValue(key, out c) && c.Key == stamp) return c.Value;
            }
            var layers = new List<GuideLayer> { GuideLayer.Load(GoogleRoot(guides), "google", "Google"), StructureCheck.Layer() };
            if (defaults != null) layers.Add(GuideLayer.LoadOverrides(defaults, DefaultsLayer));
            for (var i = 0; i < guides.Count; i++) layers.Add(LoadTeam(guides[i], NameOfCopy(guides[i], originals), Shadowed(guides[i], guides.Skip(i + 1))));
            if (root != null) layers.Add(GuideLayer.Load(root, "project"));
            var comp = new Composition(layers, notice != null ? (Func<string, bool>)null : k => { lock (_gate) return _logged.Add(k); }) { ProjectRoot = root, Notice = notice ?? EmptyGuideNotice(guides, layers) };
            lock (_gate) _cache[key] = new KeyValuePair<string, Composition>(stamp, comp);
            var onRead = GuideRead;
            if (onRead != null)
                foreach (var g in read)
                    try { if (!IsEmpty(g, layers)) onRead(g); }   // an empty guide never replaces the last good copy
                    catch (Exception ex) { lock (_gate) if (_logged.Add("guide-read|" + ex.GetType().Name)) Diagnostics.Log("style guide copy", ex); }
            return comp;
        }

        /// <summary>
        /// A team guide's layer. A guide named 'structure' would take the built-in checks' name (and style), so it loads
        /// as 'structure-guide' instead; built-in decisions use <see cref="GuideLayer.IsBuiltIn"/>, never the name.
        /// </summary>
        private static GuideLayer LoadTeam(string guide, string name, Func<string, bool> skip)
        {
            var layer = GuideLayer.Load(guide, name, null, "Google", skip);
            if (!string.Equals(layer.Name, "structure", StringComparison.OrdinalIgnoreCase)) return layer;
            return GuideLayer.Load(guide, "structure-guide", null, "Google", skip);
        }

        /// <summary>
        /// <see cref="EmptyNotice"/> and the folder, for a configured team guide (not the bundled guide, not the writer's own)
        /// that loaded with no rules: no rule bullets, lint rows, override rows, glossary terms or variables. Else null.
        /// </summary>
        private string EmptyGuideNotice(List<string> guides, List<GuideLayer> layers)
        {
            foreach (var g in guides)
            {
                if (_bundled != null && SamePath(g, _bundled)) continue;
                if (!string.IsNullOrEmpty(PersonalGuide) && SamePath(g, PersonalGuide)) continue;
                if (GuideCopy.IsCopy(g)) continue;   // a copy never stands for an empty guide: the unreachable notice says it
                if (!IsEmpty(g, layers)) continue;
                return EmptyNotice + " The folder is " + g + ".";
            }
            return null;
        }

        /// <summary>True when guide <paramref name="g"/> loaded with no rule bullets, lint rows, override rows, glossary terms or variables.</summary>
        private static bool IsEmpty(string g, List<GuideLayer> layers)
        {
            var l = layers.FirstOrDefault(x => !x.OverridesOnly && x.Path != null && x.Name != "google" && SamePath(x.Path, g));
            return l != null && l.Items.Count == 0 && l.Rules.Count == 0 && l.Overrides.Count == 0 && l.Glossary.Count == 0 && l.Variables.Count == 0;
        }

        // ---- where a rule comes from ------------------------------------------------------------------------

        /// <summary>What the Issues pane calls the rules that come with House Style (the bundled guide's Company topics).</summary>
        public const string StarterRules = "Starter rules";

        /// <summary>
        /// Where a rule comes from, in the writer's words, for the composition a lint used: <see cref="StarterRules"/> for a
        /// company rule written in the bundled guide's Company folder, else <see cref="Actions.ReviewSession.SourceName"/> of
        /// the layer (Google, Our rules, This project, AI). An id that doesn't resolve (generated rules, AI judgment) gets the
        /// layer's name. Never throws: a label never fails a lint.
        /// </summary>
        public string SourceName(Composition comp, string layer, string ruleId)
        {
            var plain = Actions.ReviewSession.SourceName(layer);
            // Only the company layer: Google's rules also sit under the bundled guide when a team guide has no Google folder.
            if (comp == null || string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(_bundled) || !string.Equals(layer, "company", StringComparison.OrdinalIgnoreCase)) return plain;
            try
            {
                var starter = comp.StarterIds;
                if (starter == null)
                {
                    var folder = Path.GetFullPath(Path.Combine(_bundled, @"Content\Resources\StyleGuide\Company")).TrimEnd('\\') + "\\";
                    var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    // In layer order, so a later layer's rule with the same id (the writer's own copy of a topic) wins. Every
                    // Avoid/Use row is also a guide rule with the topic's full path (LintRule.File is the file name only).
                    foreach (var l in comp.Layers)
                        foreach (var r in l.Items) if (r.Id != null) d[r.Id] = IsUnder(r.Source, folder);
                    comp.StarterIds = starter = d;
                }
                bool yes;
                return starter.TryGetValue(ruleId, out yes) && yes ? StarterRules : plain;
            }
            catch (Exception ex)
            {
                lock (_gate) if (_logged.Add("source|" + ex.GetType().Name)) Diagnostics.Log("rule source", ex);
                return plain;
            }
        }

        private static bool IsUnder(string file, string folder)
        {
            if (string.IsNullOrEmpty(file)) return false;
            try { return Path.GetFullPath(file).StartsWith(folder, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The files of <paramref name="guide"/> that a later writer's own guide (<see cref="GuideSeed.IsPersonal"/>) has its
        /// own version of, by path inside the project: a topic the writer edited stands in for the bundled topic.
        /// </summary>
        private static Func<string, bool> Shadowed(string guide, IEnumerable<string> later)
        {
            var mine = later.Where(GuideSeed.IsPersonal).ToList();
            if (mine.Count == 0) return null;
            var root = Path.GetFullPath(guide).TrimEnd('\\') + "\\";
            return f =>
            {
                var full = Path.GetFullPath(f);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                var rel = full.Substring(root.Length);
                return mine.Any(m => File.Exists(Path.Combine(m, rel)));
            };
        }

        /// <summary>
        /// Layer the bundled guide's default changes to Google's rules before a team guide (the default). False composes
        /// Google's rules as compiled, for tools that check the Google layer itself.
        /// </summary>
        public bool BundledDefaults { get; set; } = true;

        /// <summary>The name of the layer that carries the bundled guide's default changes to Google's rules into a team's guide.</summary>
        public const string DefaultsLayer = "defaults";

        /// <summary>
        /// The bundled guide, when its default changes to Google's rules (its Rule/Setting rows) must be layered in because it
        /// isn't itself one of <paramref name="guides"/>: a team guide (often a copy of the starter project made before a
        /// default was added) gets every shipped default unless it has its own row for that rule. Null otherwise.
        /// </summary>
        private string Defaults(List<string> guides)
        {
            if (!BundledDefaults) return null;
            // No guide at all is Google alone (the guide compiler and tests compose it that way).
            if (guides.Count == 0 || string.IsNullOrEmpty(_bundled) || !Directory.Exists(_bundled)) return null;
            return guides.Any(g => SamePath(g, _bundled)) ? null : _bundled;
        }

        /// <summary>Forgets every composition: the next use reads the guides again.</summary>
        public void Invalidate() { lock (_gate) _cache.Clear(); }

        /// <summary>The project whose Google/ folder is the baseline: the first team guide that has one, else the bundled guide.</summary>
        private string GoogleRoot(List<string> guides)
        {
            foreach (var g in guides)
                if (Directory.Exists(Path.Combine(g, @"Content\Resources\StyleGuide\Google"))) return g;
            return _bundled;
        }

        /// <summary>Changes when any guide file changes (cheap: names, sizes and write times, no reading).</summary>
        private static string Stamp(IEnumerable<string> roots)
        {
            long n = 0, latest = 0;
            foreach (var r in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // A guide that is a plain folder of topics (no .flprj) is read from the root itself, recursively.
                var plain = Directory.Exists(r) && Directory.GetFiles(r, "*.flprj").Length == 0;
                foreach (var dir in new[] { @"Content\Resources\StyleGuide", @"Project\VariableSets", @"Project\Glossaries", "" })
                {
                    var d = dir.Length == 0 ? r : Path.Combine(r, dir);
                    if (!Directory.Exists(d)) continue;
                    string[] files;
                    try { files = dir.Length == 0 && !plain ? Directory.GetFiles(d) : Directory.GetFiles(d, "*", SearchOption.AllDirectories); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { n += 7919; continue; }   // a project folder that can't be listed
                    foreach (var f in files)
                    {
                        var fi = new FileInfo(f);
                        n += fi.Length + f.Length;
                        latest = Math.Max(latest, fi.LastWriteTimeUtc.Ticks);
                    }
                }
            }
            return n + ":" + latest;
        }

        public static string FlareProjectRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var d = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
                for (; d != null; d = d.Parent) if (d.Exists && d.GetFiles("*.flprj").Length > 0) return d.FullName;
            }
            catch (Exception) { }
            return null;
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // ---- IStyleService ---------------------------------------------------------------------------------------

        public IList<LintHit> Lint(IList<string> texts, IList<int> headings, string project)
        {
            return Compose(project).Lint(texts, headings);
        }

        public IList<LintHit> Lint(IList<LintLine> lines, string project)
        {
            return Compose(project).Lint(lines);
        }

        public string Guide(string project) { return Compose(project).Guide(); }

        /// <summary>
        /// [0] the rules that always apply; [1] the rules this unit calls for: packs for its content, and Term/Rule rows
        /// for words it uses. Rules for products the guide doesn't cover are in neither.
        /// </summary>
        public string[] GuideFor(string project, GuideScope scope)
        {
            var comp = Compose(project);
            Func<GuideRule, bool> core, packs;
            Selectors(comp, scope, out core, out packs);
            // [1] content packs repeat across sections (worth caching); [2] word rules are particular to this text.
            return new[] { comp.Guide(core), comp.Guide(r => packs(r) && r.Triggers.Count == 0, packsOnly: true), comp.Guide(r => packs(r) && r.Triggers.Count > 0, packsOnly: true) };
        }

        public IList<string> RuleIdsFor(string project, GuideScope scope)
        {
            var comp = Compose(project);
            Func<GuideRule, bool> core, packs;
            Selectors(comp, scope, out core, out packs);
            return comp.Items.Where(r => core(r) || packs(r)).Select(r => r.Id).ToList();
        }

        /// <summary>
        /// Always-on: untagged prose rules. Packs: rules tagged for content the unit has, and Term rows for words it uses.
        /// Never: lint rows (the linter enforces them), rules for products the guide doesn't cover, and web-markup rules
        /// (Content.WebMarkup) unless the scope asks for them.
        /// </summary>
        private static void Selectors(Composition comp, GuideScope scope, out Func<GuideRule, bool> core, out Func<GuideRule, bool> packs)
        {
            scope = scope ?? new GuideScope();
            var always = scope.Always;
            Func<GuideRule, bool> applies = r => r.Applies.Count == 0 || r.Applies.Any(scope.Content.Contains) || r.Applies.Any(always.Contains);
            // Web-markup rules (hand-written HTML: entities, attributes, elements) only when the scope asks for them: Flare writes the markup.
            Func<GuideRule, bool> eligible = r => !r.IsLint && comp.ForOurProducts(r.Products) && !comp.IsOff(r.Id) &&
                                                  (!r.Applies.Contains(ContentTags.WebMarkup) || scope.Content.Contains(ContentTags.WebMarkup));
            // Always-on: untagged rules plus the packs nearly every section needs; one block, cached for an hour.
            core = r => eligible(r) && r.Triggers.Count == 0 && (r.Applies.Count == 0 || r.Applies.Any(always.Contains));
            packs = r => eligible(r) && (r.Triggers.Count > 0 ? applies(r) && r.TriggeredBy(scope.Text)
                                                              : r.Applies.Count > 0 && !r.Applies.Any(always.Contains) && r.Applies.Any(scope.Content.Contains));
        }

        public IList<GuideRule> Rules(string project) { return Compose(project).Items; }

        public bool IsOff(string project, string ruleId) { return Compose(project).IsOff(ruleId); }

        /// <summary>
        /// The built-in structure checks of a topic (<see cref="StructureCheck.Of"/>), with the guides' and the project's rule
        /// tables applied: a rule turned off drops its findings, a level changes them.
        /// </summary>
        public IList<Actions.StyleFinding> StructureFindings(string project, IList<XmlNode> blocks)
        {
            var comp = Compose(project);
            return StructureCheck.Settle(StructureCheck.Of(blocks), comp.SettingOf);
        }

        public IList<GuideExample> Examples(string project)
        {
            return Compose(project).Layers.Where(l => !l.IsBuiltIn).SelectMany(l => l.Items).Where(i => i.Bad.Length > 0 || i.Good.Length > 0)
                .Select(i => new GuideExample { Id = i.Id, Layer = i.Layer, Text = i.Text, Bad = i.Bad, Good = i.Good }).ToList();
        }

        public GuideReport Validate(string project) { return Compose(project).Validate(); }

        /// <summary>
        /// Writes permanent keys into the team guide's own rules (not the generated Google folder, not docs projects).
        /// For style guide owners: run before Validate. Returns how many rules got keys.
        /// </summary>
        public int AssignKeys()
        {
            var n = 0;
            // Never the guide bundled with the plugin: it is read-only, and plugin updates replace it.
            var guides = (_roots() ?? new string[0]).Where(g => !string.IsNullOrEmpty(g) && Directory.Exists(g) && WhyUnreadable(g) == null);
            foreach (var g in guides.Where(g => _bundled == null || !SamePath(g, _bundled))) n += GuideLayer.Load(g, null, null, "Google").AssignKeys();
            lock (_gate) _cache.Clear();
            return n;
        }

        /// <summary>
        /// The style guide at <paramref name="root"/>, read fresh from disk, for its owner to edit or delete a rule written in
        /// it (<see cref="GuideLayer.EditRule"/>, <see cref="GuideLayer.DeleteRule"/>; then <see cref="Invalidate"/>). Null
        /// unless it is one of the guides in effect, and never the guide bundled with the plugin.
        /// </summary>
        public GuideLayer EditableGuide(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || WhyUnreadable(root) != null) return null;
            if (_bundled != null && SamePath(root, _bundled)) return null;
            if (!(_roots() ?? new string[0]).Any(g => !string.IsNullOrEmpty(g) && SamePath(g, root))) return null;
            return GuideLayer.Load(root, null, null, "Google");
        }

        /// <summary>Terms every layer's glossary defines (for topic assists: don't propose what's already there).</summary>
        public List<string> GlossaryTerms(string project)
        {
            return Compose(project).Layers.SelectMany(l => l.Glossary).Select(g => g.Term).Where(t => !string.IsNullOrEmpty(t)).ToList();
        }

        public List<GlossaryEntry> Glossary(string project) { return Compose(project).Layers.SelectMany(l => l.Glossary).ToList(); }

        /// <summary>Candidate rules from a Flare project's own content, minus what the layered guide already flags.</summary>
        public List<RuleCandidate> Mine(string project, out int files)
        {
            var root = FlareProjectRoot(project);
            if (root == null) throw new InvalidOperationException("Not inside a Flare project: " + project);
            var result = StyleMiner.Mine(root);
            files = result.Files;
            var comp = Compose(project);
            var forms = result.Candidates.SelectMany(c => c.Forms.Select(f => f.Text)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
            var flagged = new HashSet<string>(forms.Count == 0 ? new string[0] : comp.Lint(forms, null).Select(h => forms[h.Index]));
            return result.Candidates.Where(c => c.Kind == "heading" || !c.Forms.Where(f => f.Text != c.Prefer).All(f => flagged.Contains(f.Text))).ToList();
        }
    }

    /// <summary>An ordered set of layers: the guide text, the citable rules and the compiled lint rules.</summary>
    public sealed class Composition
    {
        private static readonly Regex SentenceStart = new Regex(@"(^|[.!?:]\s+)$", RegexOptions.Compiled);
        private static readonly Dictionary<string, int> Rank = new Dictionary<string, int> { { "error", 0 }, { "warning", 1 }, { "suggestion", 2 } };

        public List<GuideLayer> Layers { get; private set; }
        public List<GuideRule> Items { get; private set; }
        public List<LintRule> Rules { get; private set; }
        /// <summary>Every id and rule name here, ignoring case: guide items' ids, rules' ids, checker names and bare names. See <see cref="OverrideIds.IsKnown"/>.</summary>
        internal HashSet<string> KnownNames { get; private set; }
        private readonly Dictionary<string, string> _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private const int MaxIgnoredLines = 20;

        public Composition(List<GuideLayer> layers) : this(layers, null)
        {
        }

        /// <param name="firstTime">
        /// Called with a key (guide|id) before an ignored override row is logged: true when this engine hasn't logged it yet.
        /// Null logs nothing (the rows are still ignored).
        /// </param>
        internal Composition(List<GuideLayer> layers, Func<string, bool> firstTime)
        {
            Layers = layers;
            // First pass: name every layer and know every rule, so a row in an earlier layer may name a later layer's rule.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in layers)
            {
                var b = GuideLayer.Camel(l.Name); var name = b; var i = 2;
                while (!taken.Add(name)) name = b + i++;
                l.Style = name;
                foreach (var r in l.Rules) r.Name = name + "." + r.LocalName;
            }
            Items = layers.SelectMany(l => l.Items).ToList();
            Rules = layers.SelectMany(l => l.Rules).ToList();
            KnownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Items) if (g.Id != null) KnownNames.Add(g.Id);
            foreach (var r in Rules) foreach (var n in new[] { r.Id, r.Name, r.LocalName }) if (n != null) KnownNames.Add(n);
            OurProducts = new HashSet<string>(layers.Skip(1).SelectMany(l => l.ProductTags), StringComparer.OrdinalIgnoreCase);

            // Second pass: only a row that names a rule counts. Later layers win; a built-in check's legacy name is stored as its id.
            var written = 0;
            foreach (var l in layers)
            {
                var guide = OverrideIds.ForLog(GuideName(l));
                var more = 0;
                foreach (var o in l.Overrides)
                {
                    if (OverrideIds.IsKnown(this, o.Key)) { _overrides[StructureCheck.IdOf(OverrideIds.Canonical(o.Key))] = o.Value; continue; }
                    if (firstTime == null) continue;
                    if (written >= MaxIgnoredLines) { more++; continue; }
                    if (!firstTime(guide + "|" + o.Key)) continue;
                    written++;
                    Diagnostics.Log("Style guide " + guide + " has a rule setting for " + OverrideIds.ForLog(o.Key) + " that names no rule, so it was ignored.");
                }
                if (more > 0) Diagnostics.Log("and " + more + " more in " + guide);
            }
        }

        /// <summary>The guide's folder name (never its full path), else the layer's name.</summary>
        private static string GuideName(GuideLayer l)
        {
            var folder = string.IsNullOrEmpty(l.Path) ? null : System.IO.Path.GetFileName(l.Path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(folder) ? l.Name : folder;
        }

        /// <param name="include">Which rules to include (null = all).</param>
        /// <param name="packsOnly">Label the result as the rule packs for this text (the always-on rules come separately).</param>
        public string Guide(Func<GuideRule, bool> include = null, bool packsOnly = false)
        {
            var parts = new List<string>();
            for (var i = 0; i < Layers.Count; i++)
            {
                var g = Layers[i].Guide(include).Trim();
                if (g.Length > 0) parts.Add("# " + (packsOnly ? "Rules for what this text contains, layer: " : "Layer: ") + Layers[i].Name + " (" + (i == 0 ? "baseline" : "overrides the layers above") + ")\n\n" + g);
            }
            return parts.Count == 0 ? "" : string.Join("\n\n---\n\n", parts.ToArray()) + "\n";
        }

        /// <summary>Products the guide covers: the Product condition tags its own projects define (the Google layer defines none).</summary>
        public HashSet<string> OurProducts { get; private set; }

        /// <summary>A rule tagged for products applies only when the guide covers one of them.</summary>
        public bool ForOurProducts(ICollection<string> products)
        {
            return products == null || products.Count == 0 || products.Any(OurProducts.Contains);
        }

        /// <summary>True when a Rule/Setting table (or overrides.ini) turns this rule off: then neither the linter nor the AI uses it.</summary>
        public bool IsOff(string ruleId)
        {
            string v;
            return ruleId != null && _overrides.TryGetValue(StructureCheck.IdOf(ruleId), out v) && v == "off";
        }

        /// <summary>The open Flare project when it is the last of <see cref="Layers"/> (its own rules and exceptions), else null.</summary>
        public string ProjectRoot { get; internal set; }

        /// <summary>Why this composition isn't the configured guide (a team guide couldn't be read), or null. See <see cref="StyleEngine.Notice"/>.</summary>
        public string Notice { get; internal set; }

        /// <summary>Rule id to "written in the bundled starter topics", built once by <see cref="StyleEngine.SourceName"/>.</summary>
        internal Dictionary<string, bool> StarterIds { get; set; }

        /// <summary>
        /// What the guides and the project say about a rule, the later one winning: "off", "on" (back on at its own level), a
        /// severity, or null (nothing). Looked up by id, then by name (a bare name is a Google rule's).
        /// </summary>
        public string SettingOf(string ruleId, string name = null)
        {
            string v;
            if (ruleId != null && _overrides.TryGetValue(StructureCheck.IdOf(ruleId), out v)) return v;
            if (name != null && _overrides.TryGetValue(OverrideIds.Canonical(name), out v)) return v;
            return null;
        }

        /// <summary>The level a table rule fires at now: its own, or the level a later guide or the project gives it; null when it is off.</summary>
        public string LevelOf(LintRule r)
        {
            var o = Override(r);
            return o == "off" ? null : o ?? r.Severity;
        }

        /// <summary>"off", a severity, or null (the rule's own level, also when a later layer turned it back "on"): what the layers say about this rule.</summary>
        private string Override(LintRule r)
        {
            string v;
            if (!(r.Id != null && _overrides.TryGetValue(StructureCheck.IdOf(r.Id), out v)) && !_overrides.TryGetValue(StructureCheck.IdOf(r.Name), out v)) return null;
            return v == "on" ? null : v;
        }

        // ---- lint --------------------------------------------------------------------------------------------------

        /// <summary>
        /// Exact findings in plain-text lines whose context isn't known beyond "heading or not" (MCP, tests, the miner).
        /// Rules scoped to headings (Content.Headings) run on heading lines only; other Content scopes aren't enforced.
        /// </summary>
        /// <param name="all">Every rule's hits, overlapping ones included (validation tests each rule on its own).</param>
        public List<LintHit> Lint(IList<string> texts, IList<int> headings, bool all = false)
        {
            var heads = new HashSet<int>(headings ?? new int[0]);
            var lines = new List<LintLine>(texts.Count);
            for (var i = 0; i < texts.Count; i++) lines.Add(LintLine.Plain(texts[i], heads.Contains(i)));
            return Lint(lines, all);
        }

        /// <summary>
        /// Exact findings in lines that carry their context (from <see cref="TextLine.ToLint"/>): each rule fires only where its
        /// Content scope allows (<see cref="LintScope"/>), and only rules about UI wording fire inside UI labels.
        /// Several rules on the same words make one finding: the owner (later layer, citable, fixable, severity, canonical
        /// owner, then guide order) with every other rule on those words in <see cref="LintHit.AlsoRules"/>.
        /// </summary>
        /// <param name="all">Every rule's hits, overlapping ones included (validation tests each rule on its own).</param>
        public List<LintHit> Lint(IList<LintLine> lines, bool all = false)
        {
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Layers.Count; i++) rank[Layers[i].Name] = i;
            var hits = new List<LintHit>();
            var order = new Dictionary<LintHit, int>();
            var active = Rules.Select((r, n) => new { r, n }).Where(x => ForOurProducts(x.r.Products))
                              .Select(x => new { x.r, o = Override(x.r), scope = LintScope.For(x.r), x.n }).Where(x => x.o != "off").ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i] ?? LintLine.Plain("");
                var raw = line.Text ?? "";
                var text = raw.Replace('\r', ' ').Replace('\n', ' ');
                var breaks = raw.IndexOfAny(LineBreaks) >= 0;
                foreach (var x in active)
                {
                    var r = x.r;
                    foreach (var swap in r.Swaps)
                        foreach (Match m in swap.Key.Matches(text))
                        {
                            if (m.Length == 0) continue;
                            // Spacing made of a line break in the source (a wrapped text node) is formatting, not typed spaces.
                            if (breaks && m.Value.Trim().Length == 0 && raw.IndexOfAny(LineBreaks, m.Index, m.Length) >= 0) continue;
                            if (!x.scope.Allows(line, m.Index, m.Index + m.Length)) continue;
                            var hit = Hit(r, swap.Value, text, m.Index, m.Index + m.Length, x.o);
                            if (hit == null) continue;
                            hit.Index = i;
                            hits.Add(hit);
                            order[hit] = x.n;
                        }
                }
            }
            if (all) return hits;
            // Same span flagged by several rules: one finding. The owner is the latest layer (it overrides), then a citable
            // rule, a fixable one, the most severe, the layer's canonical owner (rule-owners.txt), then guide order: the
            // same input always names the same rule.
            var kept = new List<LintHit>();
            var byKey = new Dictionary<string, LintHit>();
            foreach (var h in hits.OrderBy(h => h.Index).ThenBy(h => h.Start).ThenBy(h => h.Length)
                         .ThenBy(h => -(rank.ContainsKey(h.Layer) ? rank[h.Layer] : 0)).ThenBy(h => h.RuleId == null).ThenBy(h => h.Replacement == null)
                         .ThenBy(h => Rank.ContainsKey(h.Severity) ? Rank[h.Severity] : 3)
                         .ThenBy(h => OwnerRank(h.RuleId)).ThenBy(h => order[h]))
            {
                LintHit first;
                if (byKey.TryGetValue(h.Index + ":" + h.Start + ":" + h.Length, out first)) first.AlsoFlaggedBy(h);   // turning off the shown rule would otherwise expose this one
                else { byKey[h.Index + ":" + h.Start + ":" + h.Length] = h; kept.Add(h); }
            }
            // A hit inside a longer one ("Please" inside "Please note that") is the same problem: show the longer only, and
            // list the inner hit's rules (with the rules it already stood for) on it.
            var shown = new List<LintHit>();
            foreach (var h in kept)
            {
                var outer = kept.Where(o => o != h && o.Index == h.Index && o.Length > h.Length && o.Start <= h.Start && h.Start + h.Length <= o.Start + o.Length)
                                .OrderByDescending(o => o.Length).ThenBy(o => o.Start).FirstOrDefault();
                if (outer == null) shown.Add(h);
                else outer.AlsoFlaggedBy(h);
            }
            return shown;
        }

        private static readonly char[] LineBreaks = { '\r', '\n' };

        /// <summary>The fix a row gives for its i-th Avoid term (one Use per term, or one for all), as the loader builds it.</summary>
        private static string UseOf(LintRule r, int i)
        {
            if (i < r.Swaps.Count && r.Swaps.Count == r.Avoid.Count) return r.Swaps[i].Value ?? "";
            return (r.Use ?? "").Trim();
        }

        private Dictionary<string, int> _owners;

        /// <summary>Position of the rule in its layer's canonical owner list (rule-owners.txt); unlisted rules come after listed ones.</summary>
        private int OwnerRank(string ruleId)
        {
            if (_owners == null)
            {
                var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in Layers) foreach (var id in l.Owners) if (!d.ContainsKey(id)) d[id] = d.Count;
                _owners = d;
            }
            int n;
            return ruleId != null && _owners.TryGetValue(ruleId, out n) ? n : int.MaxValue;
        }

        private static LintHit Hit(LintRule r, string use, string text, int start, int end, string severity)
        {
            var match = text.Substring(start, end - start);
            string replacement = null, message;
            if (r.Variable == null && r.LocalName == "Variables")
            {
                message = "'" + match + "' is the value of variable " + use + ": insert the variable instead of typing it.";
                return new LintHit { Start = start, Length = end - start, Match = match, Rule = r.Name, RuleId = r.Id, Layer = r.Layer, Variable = use, Severity = severity ?? r.Severity, Message = message, Link = r.Link };
            }
            if (r.Action == "replace")
            {
                if (string.Equals(match, use, r.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;   // already right
                replacement = MatchCase(text, start, use);
                // "for example," before a comma or at the end: don't double the punctuation.
                if (replacement.EndsWith(",") && (end >= text.Length || ",;:.".IndexOf(text[end]) >= 0)) replacement = replacement.TrimEnd(',');
                message = "Use '" + replacement + "' instead of '" + match + "'.";
            }
            else if (r.Action == "remove")
            {
                if (end < text.Length && text[end] == ' ') end++;   // take the following space with the words
                if (SentenceStart.IsMatch(text.Substring(0, start)) && end < text.Length)
                {
                    replacement = char.ToUpperInvariant(text[end]).ToString();   // re-capitalize what now starts the sentence
                    end++;
                }
                else replacement = "";
                message = match.Trim().Length == 0 ? "Delete the extra space." : "Delete '" + match.Trim() + "'.";
                match = text.Substring(start, end - start);
            }
            else message = "Avoid '" + match + "'.";
            if (!string.IsNullOrEmpty(r.Why)) message += " " + r.Why;
            return new LintHit { Start = start, Length = end - start, Match = match, Replacement = replacement, Rule = r.Name, RuleId = r.Id, Layer = r.Layer, Severity = severity ?? r.Severity, Message = message, Link = r.Link };
        }

        /// <summary>Capitalize the replacement only where a sentence starts; elsewhere the rule's spelling is the point.</summary>
        private static string MatchCase(string text, int start, string repl)
        {
            if (repl.Length > 0 && char.IsLower(repl[0]) && SentenceStart.IsMatch(text.Substring(0, start))) return char.ToUpperInvariant(repl[0]) + repl.Substring(1);
            return repl;
        }

        // ---- validation --------------------------------------------------------------------------------------------

        /// <summary>
        /// Tests the guide: every row's Example must trigger it and its fix must resolve it without creating another finding,
        /// the Use wording must not trip a rule, rows must not replace each other or give two fixes for the same words (within
        /// a layer too), Good examples must be clean, rows that can only match markup are reported (the loader skips them),
        /// topics must parse. Also counts prose rules and their examples.
        /// </summary>
        public GuideReport Validate()
        {
            var report = new GuideReport { Layers = Layers.Select(l => l.Name).ToList() };
            report.Issues.AddRange(Layers.SelectMany(l => l.Problems));
            // The built-in structure checks have no Example and aren't the guide's to test: they stay out of the tables and counts.
            var guideItems = Layers.Where(l => !l.IsBuiltIn).SelectMany(l => l.Items).ToList();
            var rows = Layers.Where(l => !l.IsBuiltIn).SelectMany(l => l.Rules).Where(r => r.Id != null && Override(r) != "off").ToList();   // a rule the team turned off is not tested: it never fires
            Action<string, LintRule, string> issue = (level, r, msg) => report.Issues.Add(new GuideIssue { Level = level, Layer = r.Layer, File = r.File, Location = "table row " + r.Row + " (" + r.Id + ")", Message = msg });

            foreach (var r in rows.Where(r => r.Action == "replace" && r.Avoid.Any(a => string.Equals(a, r.Use, r.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))
                issue("error", r, "Avoid and Use are the same ('" + r.Use + "'): the rule can never fire.");
            var withEx = rows.Where(r => !string.IsNullOrEmpty(r.Example)).ToList();
            foreach (var r in rows.Where(r => string.IsNullOrEmpty(r.Example))) issue("warning", r, "No Example: add one so the rule can be tested.");
            if (withEx.Count > 0)
            {
                var heads = Enumerable.Range(0, withEx.Count).Where(i => withEx[i].Applies.Contains("Headings")).ToList();
                var hits = Lint(withEx.Select(r => r.Example).ToList(), heads, true);
                var fixedTexts = new List<string>(); var fixedRows = new List<LintRule>(); var fixedFrom = new List<int>();
                var moved = new List<HashSet<string>>();   // rules that flagged the replaced words: their finding moves with the fix, it isn't new
                for (var i = 0; i < withEx.Count; i++)
                {
                    var r = withEx[i];
                    var mine = hits.Where(h => h.Index == i && h.RuleId == r.Id).ToList();
                    // A row for products this guide doesn't cover never lints here, so its Example can't be checked against it.
                    if (mine.Count == 0) { if (ForOurProducts(r.Products)) issue("error", r, "The Example doesn't trigger this rule: '" + r.Example + "'."); continue; }
                    if (mine.All(h => h.Replacement != null))
                    {
                        var t = r.Example;
                        // One rule can match overlapping spans ("please note that", "please"): keep the longest, then apply right to left.
                        var chosen = new List<LintHit>();
                        foreach (var h in mine.OrderByDescending(h => h.Length))
                            if (!chosen.Any(c => h.Start < c.Start + c.Length && c.Start < h.Start + h.Length)) chosen.Add(h);
                        foreach (var h in chosen.OrderByDescending(h => h.Start))
                            t = t.Substring(0, h.Start) + h.Replacement + t.Substring(h.Start + h.Length);
                        fixedTexts.Add(t); fixedRows.Add(r); fixedFrom.Add(i);
                        var idx = i;
                        moved.Add(new HashSet<string>(hits.Where(b => b.Index == idx && b.RuleId != r.Id && chosen.Any(h => b.Start < h.Start + h.Length && h.Start < b.Start + b.Length))
                                                          .Select(b => b.RuleId ?? b.Rule)));
                    }
                }
                if (fixedTexts.Count > 0)
                {
                    var heads2 = Enumerable.Range(0, fixedRows.Count).Where(i => fixedRows[i].Applies.Contains("Headings")).ToList();
                    var again = Lint(fixedTexts, heads2, true);
                    for (var i = 0; i < fixedRows.Count; i++)
                    {
                        if (again.Any(h => h.Index == i && h.RuleId == fixedRows[i].Id)) { issue("error", fixedRows[i], "Applying the fix doesn't resolve the rule: '" + fixedTexts[i] + "'."); continue; }
                        // The fix must leave the text clean of every other rule too (#59, #62): a new finding sends the writer round again.
                        var k = i;
                        var created = again.FirstOrDefault(h => h.Index == k && !moved[k].Contains(h.RuleId ?? h.Rule) &&
                                                                !hits.Any(b => b.Index == fixedFrom[k] && (b.RuleId ?? b.Rule) == (h.RuleId ?? h.Rule) && b.Match == h.Match));
                        if (created != null)
                            issue("error", fixedRows[i], "Applying the fix creates an issue from " + (created.RuleId ?? created.Rule) + " ('" + created.Match + "'): '" + fixedTexts[i] + "'.");
                    }
                }
            }
            var uses = rows.Where(r => r.Action == "replace").ToList();
            if (uses.Count > 0)
            {
                var hits = Lint(uses.Select(r => r.Use).ToList(), null);
                for (var i = 0; i < uses.Count; i++)
                    foreach (var h in hits.Where(h => h.Index == i && h.Replacement != null))
                    {
                        var same = h.RuleId == uses[i].Id;
                        issue(same ? "error" : "warning", uses[i], "The 'Use' wording '" + uses[i].Use + "' is itself flagged by " + (same ? "this same rule" : h.RuleId ?? h.Rule) + ": " + h.Message);
                    }
            }
            // Contradictions: one row's Use is another row's Avoid, and the other way round.
            for (var i = 0; i < rows.Count; i++)
                for (var j = i + 1; j < rows.Count; j++)
                {
                    LintRule a = rows[i], b = rows[j];
                    if (b.Avoid.Any(x => string.Equals(x, a.Use, StringComparison.OrdinalIgnoreCase)) && a.Avoid.Any(x => string.Equals(x, b.Use, StringComparison.OrdinalIgnoreCase)))
                        issue("error", a, "Contradicts " + b.Id + " (" + b.File + " row " + b.Row + "): each replaces the other.");
                }
            // Two rows on the same words. Within a layer: the same fix is a duplicate (turning one off leaves the other), a
            // different fix is a conflict (which one shows depends on rule order). Where a later layer meets an earlier one the
            // later layer wins: redundant with the same fix, worth knowing with a different one.
            for (var i = 0; i < rows.Count; i++)
                for (var j = i + 1; j < rows.Count; j++)
                {
                    LintRule a = rows[i], b = rows[j];
                    // The same words as both rows match them: case-insensitively when either row is, exactly otherwise ("A SAP" at a
                    // sentence start and "a SAP" mid-sentence are different words to two exact-case rows).
                    var cmp = a.IgnoreCase || b.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    var pair = Enumerable.Range(0, b.Avoid.Count).SelectMany(bi => Enumerable.Range(0, a.Avoid.Count).Where(ai => string.Equals(a.Avoid[ai], b.Avoid[bi], cmp)).Select(ai => new[] { ai, bi })).FirstOrDefault();
                    if (pair == null) continue;
                    string term = b.Avoid[pair[1]], useA = UseOf(a, pair[0]), useB = UseOf(b, pair[1]);
                    var same = string.Equals(useA, useB, StringComparison.OrdinalIgnoreCase);
                    if (a.Layer == b.Layer)
                    {
                        if (!a.Products.SequenceEqual(b.Products)) continue;   // rows for different products never meet
                        issue(same ? "warning" : "error", b, same ? "Duplicates " + a.Id + " ('" + term + "'): turning one off leaves the other; remove one."
                                                                 : "Conflicts with " + a.Id + " for '" + term + "': this row says '" + useB + "', that one says '" + useA + "'. Keep one fix.");
                        continue;
                    }
                    issue("warning", b, same ? "Duplicates " + a.Id + " ('" + term + "'): the " + a.Layer + " rule already does this; remove one."
                                             : "Overrides " + a.Id + " for '" + term + "': " + b.Layer + " says '" + useB + "', " + a.Layer + " says '" + useA + "'. The " + b.Layer + " rule wins.");
                }
            // A rule's Good example must be clean: a Good example the guide itself flags teaches the writer two things at once.
            // (Linted as a paragraph; a Good example that is markup or code is a source snippet, not text a writer types.)
            var goods = guideItems.Where(it => it.Good.Length > 0 && !IsOff(it.Id) && ForOurProducts(it.Products) && !LintPatterns.MatchesMarkup(it.Good)).ToList();
            if (goods.Count > 0)
            {
                var lines = goods.Select(it => new LintLine { Text = it.Good, Known = true }).ToList();
                foreach (var h in Lint(lines))
                {
                    var it = goods[h.Index];
                    report.Issues.Add(new GuideIssue { Level = "warning", Layer = it.Layer, File = it.Source == null ? "" : Path.GetFileName(it.Source), Location = "rule " + it.Id,
                                                       Message = "The Good example is flagged by " + (h.RuleId ?? h.Rule) + " ('" + h.Match + "'): '" + it.Good + "'." });
                }
            }
            // Override tables must name rules that exist.
            foreach (var l in Layers.Where(x => !x.OverridesOnly))   // the shipped defaults aren't the guide owner's to fix
                foreach (var o in l.Overrides)
                {
                    if (!OverrideIds.IsKnown(this, o.Key))
                        report.Issues.Add(new GuideIssue { Level = "error", Layer = l.Name, File = "", Location = "override " + o.Key, Message = "No rule '" + o.Key + "': use a rule's reference from the guide, for example google:numbers/3." });
                }
            // Rules without a permanent key: their ids would shift when rules are added or moved.
            foreach (var l in Layers)
            {
                var unkeyed = l.Items.Count(i => i.Element != null && !i.HasKey);
                if (unkeyed > 0)
                    report.Issues.Add(new GuideIssue { Level = "warning", Layer = l.Name, File = "", Location = "keys", Message = Plural.Of(unkeyed, "rule") + " " + (unkeyed == 1 ? "has" : "have") + " no permanent key yet. Validating as a style guide owner assigns them (no other change to the topics)." });
            }
            report.TableRules = rows.Count;
            report.TestedByExample = withEx.Count;
            report.ProseRules = guideItems.Count - rows.Count;
            report.ProseRulesWithExamples = guideItems.Count(i => i.Bad.Length > 0 || i.Good.Length > 0);
            report.Errors = report.Issues.Count(x => x.Level == "error");
            report.Warnings = report.Issues.Count(x => x.Level == "warning");
            return report;
        }
    }
}
