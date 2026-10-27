using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Newtonsoft.Json.Linq;
using FlareAI.Core.Editing;
using FlareAI.Core.Llm;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>Outcome of a Rewrite/Ask: the proposed nodes (already imported into the document) plus validation.</summary>
    public sealed class RewriteProposal
    {
        public IList<XmlNode> OriginalBlocks { get; set; }
        public IList<XmlNode> ProposedBlocks { get; set; }
        public string ProposedXml { get; set; }
        public ValidationResult Validation { get; set; }
        public LlmResponse Raw { get; set; }
        /// <summary>Lint findings still present in the proposal after the self-check round.</summary>
        public List<StyleFinding> RemainingLint { get; set; }
        /// <summary>Non-fatal problems worth showing (for example: linter unavailable).</summary>
        public List<string> Notes { get; set; }
        /// <summary>The blocks as the model saw them (pending changes resolved to what the author reads).</summary>
        public IList<XmlNode> View { get; set; }
        /// <summary>
        /// Word-level replacements equivalent to the proposal, when it kept the structure; null means the
        /// proposal restructures and applies as a block replacement.
        /// </summary>
        public List<StyleFinding> WordPlan { get; set; }
        /// <summary>The model could only do part of the task by changing wording; the rest needs a structural edit.</summary>
        public bool NeedsRestructure { get; set; }
        public string RestructureNote { get; set; }
        /// <summary>
        /// Why this proposal must not be applied as it stands (null = it may): a wording-only pass that leaves out a whole
        /// sentence while the AI says the task needs restructuring has deleted what it meant to move. Apply refuses it.
        /// </summary>
        public string ApplyBlocked { get; set; }
        /// <summary>Changes the model proposed but that only the author may make (a modal verb, a code term): left out, with the reason.</summary>
        public List<string> Held { get; set; } = new List<string>();
    }

    /// <summary>One unit of AI review: its blocks, their numbered runs, and the prompt.</summary>
    public sealed class ReviewUnit
    {
        public List<XmlNode> Blocks { get; set; }
        public RunMap Runs { get; set; }
        public string Prompt { get; set; }
        /// <summary>What the unit contains and the words it uses: selects the rules sent with it.</summary>
        public GuideScope Scope { get; set; }
        /// <summary>The ids of the rules sent with the unit (the only ones it may cite); null = not checked.</summary>
        public HashSet<string> Allowed { get; set; }
        /// <summary>The lint findings in the unit when it was prepared: their fixes, once applied, don't call for a new review (<see cref="ReviewCache"/>).</summary>
        public List<StyleFinding> Lint { get; set; }
    }

    /// <summary>
    /// Orchestrates one action end to end: pick blocks → lint (docstyle) → model → parse → validate.
    /// Applying the result to the document is a separate, explicit step so the UI can show a preview.
    /// All methods are synchronous; the caller runs them off the UI thread.
    /// </summary>
    public sealed class ActionRunner
    {
        private readonly ILlmClient _llm;
        private readonly PromptBuilder _prompts;
        private readonly int _maxTokens;
        private readonly IStyleService _style;

        private readonly string _project;

        /// <summary>Raised on the writing thread just before House Style writes to a document (its path, may be null).</summary>
        public static event Action<string> Writing;
        /// <summary>Raised just after that write, even when it failed.</summary>
        public static event Action<string> Wrote;

        /// <summary>Every text write House Style makes goes through here, so the writer's own edits are told apart from ours.</summary>
        public static void WriteTracked(string path, Action write)
        {
            RaiseWrite(Writing, path);
            try { write(); }
            finally { RaiseWrite(Wrote, path); }
        }

        private static void RaiseWrite(Action<string> e, string path)
        {
            if (e == null) return;
            foreach (Action<string> h in e.GetInvocationList())
                try { h(path); } catch (Exception ex) { Diagnostics.Log("write event: " + path + " " + ex.GetType().Name); }
        }

        /// <param name="project">Path of the topic (any path inside its Flare project) for the project style layer; null for none.</param>
        /// <param name="routing">Model and effort per task; null = built-in defaults.</param>
        public ActionRunner(ILlmClient llm, PromptBuilder prompts, int maxTokens, IStyleService style = null, string project = null, Routing routing = null)
        {
            _llm = llm;
            _prompts = prompts;
            _maxTokens = maxTokens;
            _style = style;
            _project = project;
            Routing = routing ?? new Routing();
        }

        public Routing Routing { get; private set; }

        /// <summary>How many model calls run at once for per-block work.</summary>
        public int Parallelism { get; set; } = 6;

        /// <summary>
        /// One model call for a task: its route (model, effort), the system prompt, and the output contract. The token
        /// ceiling is a safety net only; a cut-off answer is retried once with more room.
        /// </summary>
        /// <param name="scope">The text's content and words: the rules to send besides the always-on ones (null = none).</param>
        /// <param name="guide">False for tasks that apply no style rule (alt text, topic assists): a short system prompt, no guide.</param>
        private LlmResponse Ask(string task, string user, JObject schema, List<LlmImage> images = null, GuideScope scope = null, bool guide = true)
        {
            var route = Routing.For(task);
            // The cached prefix is (model, output schema, system prompt): the schema sits ahead of the system prompt, and
            // caches are per model. So tasks share a cache entry only when all three match: review, rewrite and restructure
            // do (Sonnet, Contracts.EditSchema, the same always-on block); refine (Haiku), draft (Opus) and extract (its own
            // schema) each have their own, so they cache for five minutes, not an hour (Routing.CacheFor).
            scope = scope ?? new GuideScope();
            if (scope.Always.Count == 0) scope.Always = new HashSet<string>(Routing.AlwaysPacks ?? new List<string>());
            var system = guide ? _prompts.BuildSystemParts(scope) : new[] { _prompts.BuildPlainSystem(), null, null };
            var req = new LlmRequest
            {
                Task = task, System = system[0], SystemPacks = system[1], SystemTerms = system[2], User = user, Schema = schema, Model = route.Model, Effort = route.Effort,
                CacheTtl = Routing.CacheFor(task),
                Images = images, MaxTokens = Math.Max(_maxTokens, AnthropicClient.DefaultMaxTokens)
            };
            var sw = Stopwatch.StartNew();
            LlmResponse resp;
            try { resp = _llm.Complete(req); }
            catch (LlmException ex) when (ex.Failure == LlmFailure.Truncated)
            {
                Diagnostics.Log(task + ": cut off at " + req.MaxTokens + " tokens; retrying with more room");
                req.MaxTokens *= 2;
                resp = _llm.Complete(req);
            }
            Usage.Record(task, route.Model, resp, sw.Elapsed.TotalSeconds);
            Diagnostics.Log(task + ": " + route.Model + "/" + route.Effort + " in=" + resp.InputTokens + " cached=" + resp.CacheReadTokens +
                            " out=" + resp.OutputTokens + " stop=" + resp.StopReason + " " + sw.ElapsedMilliseconds + "ms");
            if (schema != null && resp.Json == null)
            {
                try { resp.Json = JObject.Parse(resp.Text ?? ""); }
                catch (Newtonsoft.Json.JsonException ex) { throw new LlmException("The AI's answer didn't match the expected format.", 0, ex, LlmFailure.Contract); }
            }
            return resp;
        }

        /// <summary>Runs one job per item, a few at a time; the first failure is rethrown as itself.</summary>
        private void Each(int count, Action<int> job)
        {
            WarmThenParallel(count, Parallelism, job);
        }

        /// <summary>
        /// Runs job 0 alone, then the rest in parallel. The first call writes the prompt cache (the guide's always-on
        /// rules); the others then read it at a tenth of the price instead of each writing it at 1.25×.
        /// </summary>
        public static void WarmThenParallel(int count, int parallelism, Action<int> job)
        {
            if (count == 0) return;
            job(0);
            try { Parallel.For(1, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, job); }
            catch (AggregateException ae) { throw ae.Flatten().InnerExceptions[0]; }
        }

        public IList<XmlNode> PickBlocks(IDocumentAdapter doc)
        {
            return PickBlocks(doc, doc == null ? null : doc.SelectionNodes);
        }

        /// <summary>Flare always has a caret; a caret with no selected text means "check the whole topic".</summary>
        public IList<XmlNode> PickCheckBlocks(IDocumentAdapter doc)
        {
            return string.IsNullOrEmpty(doc == null ? null : doc.SelectionText) ? PickBlocks(doc, new List<XmlNode>()) : PickBlocks(doc);
        }

        /// <summary>Every block of the topic, regardless of selection.</summary>
        public IList<XmlNode> PickAllBlocks(IDocumentAdapter doc)
        {
            return PickBlocks(doc, new List<XmlNode>());
        }

        private static IList<XmlNode> PickBlocks(IDocumentAdapter doc, IList<XmlNode> selection)
        {
            if (doc == null || !doc.IsEditableTopic)
                throw new InvalidOperationException("Open a topic in the XML Editor first.");
            var blocks = BlockSelector.SelectBlocks(doc.Document, selection);
            if (blocks.Count == 0) throw new InvalidOperationException("Nothing to work on — the topic body is empty.");
            return blocks;
        }

        // ---- check style -----------------------------------------------------------------------

        /// <summary>Deterministic findings from docstyle, in document order. Empty when no style service is configured.</summary>
        public List<StyleFinding> Lint(IList<XmlNode> blocks)
        {
            var findings = new List<StyleFinding>();
            if (_style == null) return findings;
            _comp = null;   // the labels follow the guide this lint uses
            // Lint whole lines (inline markup flattened, variables resolved), then map each hit back to the run it
            // falls in. Hits inside a variable's value belong to the variable set, not this topic: dropped here.
            // Each line carries where it sits (step, cell, note, …) and its inline spans (UI labels, links), so rules fire
            // only in their Content scope and word rules leave UI labels alone.
            var lines = TextLines.Of(blocks, VariableValue);
            var inputs = lines.ConvertAll(l => l.ToLint());
            var inVariables = 0;
            foreach (var h in _style.Lint(inputs, _project))
            {
                var f = MapToRun(lines[h.Index], h);
                if (f != null) findings.Add(f); else inVariables++;
            }
            if (inVariables > 0) Diagnostics.Log("Lint: " + Plural.Of(inVariables, "finding") + " inside variable values skipped");
            return findings;
        }

        /// <summary>
        /// Lint findings inside the snippets these blocks include (and snippets those include), read from disk.
        /// Each finding carries the snippet's path; fixes are applied in the snippet (the caller opens it).
        /// </summary>
        public List<StyleFinding> LintSnippets(IList<XmlNode> blocks, string topicPath)
        {
            var findings = new List<StyleFinding>();
            if (_style == null || string.IsNullOrEmpty(topicPath)) return findings;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { System.IO.Path.GetFullPath(topicPath) };
            LintSnippetsOf(blocks, topicPath, seen, findings, 0);
            return findings;
        }

        private void LintSnippetsOf(IList<XmlNode> blocks, string hostPath, HashSet<string> seen, List<StyleFinding> findings, int depth)
        {
            if (depth > 4) return;
            foreach (var path in SnippetPaths(blocks, hostPath))
            {
                if (!seen.Add(path) || !System.IO.File.Exists(path)) continue;
                XmlDocument snippet;
                try { snippet = new XmlDocument { PreserveWhitespace = true }; snippet.Load(path); }
                catch (Exception ex) { Diagnostics.Log("snippet " + path, ex); continue; }
                var body = BlockSelector.FindBody(snippet);
                if (body == null) continue;
                var inner = new List<XmlNode>();
                foreach (XmlNode c in body.ChildNodes) if (c.NodeType == XmlNodeType.Element) inner.Add(c);
                foreach (var f in Lint(inner))
                {
                    f.DocumentPath = path;
                    f.Block = null; // located again in the snippet as Flare opens it
                    findings.Add(f);
                }
                LintSnippetsOf(inner, path, seen, findings, depth + 1);
            }
        }

        private static IEnumerable<string> SnippetPaths(IList<XmlNode> blocks, string hostPath)
        {
            var dir = System.IO.Path.GetDirectoryName(hostPath) ?? "";
            var removed = TrackedChanges.RemoveIds(blocks.Count > 0 ? blocks[0].OwnerDocument : null);
            foreach (var b in blocks)
            {
                var ns = new XmlNamespaceManager(b.OwnerDocument.NameTable);
                ns.AddNamespace("MadCap", BlockSelector.MadCapNs);
                foreach (XmlElement s in b.SelectNodes("descendant-or-self::MadCap:snippetBlock | descendant-or-self::MadCap:snippetText", ns))
                {
                    var src = s.GetAttribute("src");
                    if (src.Length == 0 || TrackedChanges.IsDeleted(s, removed)) continue;
                    string full;
                    try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, src.Replace('/', System.IO.Path.DirectorySeparatorChar))); }
                    catch (Exception) { continue; }
                    yield return full;
                }
            }
        }

        /// <summary>Topics and snippets of the Flare project containing <paramref name="anyPath"/> (Content folder), or empty.</summary>
        public static List<string> ProjectFiles(string anyPath, bool includeExcluded = false)
        {
            var files = new List<string>();
            if (string.IsNullOrEmpty(anyPath)) return files;
            var dir = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(anyPath)));
            while (dir != null && dir.GetFiles("*.flprj").Length == 0) dir = dir.Parent;
            if (dir == null) return files;
            var content = System.IO.Path.Combine(dir.FullName, "Content");
            if (!System.IO.Directory.Exists(content)) return files;
            var guide = System.IO.Path.Combine(content, @"Resources\StyleGuide") + System.IO.Path.DirectorySeparatorChar;
            var excluded = includeExcluded ? null : LintExclusions.Load(dir.FullName);
            foreach (var pattern in new[] { "*.htm", "*.html", "*.flsnp" })
                foreach (var f in System.IO.Directory.GetFiles(content, pattern, System.IO.SearchOption.AllDirectories))
                    if (!f.StartsWith(guide, StringComparison.OrdinalIgnoreCase) && new System.IO.FileInfo(f).Length < 2000000 && !LintExclusions.IsExcluded(f, excluded)) files.Add(f);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        /// <summary>
        /// Lints whole files from disk (no AI): every topic/snippet in one linter call per batch. Findings carry the
        /// file path; fixes are applied after opening the file in the editor.
        /// </summary>
        /// <summary>
        /// Null normally; when the team's style guide couldn't be read, which folder and why, and that checks used Google's
        /// guide instead (<see cref="StyleEngine.Notice"/>). Show it with the check's result.
        /// </summary>
        public string GuideNotice
        {
            get
            {
                var engine = _style as StyleEngine;
                if (engine == null) return null;
                try { return engine.Notice(_project); }
                catch (Exception ex) { Diagnostics.Log("guide notice", ex); return null; }
            }
        }

        private Composition _comp;

        /// <summary>
        /// Where a rule comes from, in the writer's words (Google, Our rules, Starter rules, This project, AI), for the guide
        /// the last lint used. Composed once per lint, never per row.
        /// </summary>
        public string SourceName(string layer, string ruleId)
        {
            var engine = _style as StyleEngine;
            if (engine == null) return ReviewSession.SourceName(layer);
            try
            {
                var comp = _comp ?? (_comp = engine.Compose(_project));
                return engine.SourceName(comp, layer, ruleId);
            }
            catch (Exception ex) { Diagnostics.Log("rule source", ex); return ReviewSession.SourceName(layer); }
        }

        /// <summary>The source of a finding's rule; for an AI issue that cites a guide rule, the source of that rule.</summary>
        public string SourceName(StyleFinding f)
        {
            if (f == null) return "";
            return f.RuleLayer != null ? SourceName(f.RuleLayer, f.RuleId) : SourceName(f.Layer, f.RuleId);
        }

        public List<StyleFinding> LintFiles(IList<string> paths, Action<int, int> progress = null)
        {
            var findings = new List<StyleFinding>();
            if (_style == null) return findings;
            _comp = null;   // the labels follow the guide this lint uses
            var lines = new List<TextLine>();
            var owners = new List<string>();
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var done = 0;
            foreach (var path in paths)
            {
                done++;
                try
                {
                    var xml = new XmlDocument { PreserveWhitespace = true };
                    xml.Load(path);
                    var body = BlockSelector.FindBody(xml);
                    if (body == null) continue;
                    texts[path] = HandFixes.ScopeText(body);
                    var blocks = new List<XmlNode>();
                    foreach (XmlNode c in body.ChildNodes) if (c.NodeType == XmlNodeType.Element) blocks.Add(c);
                    foreach (var l in TextLines.Of(blocks, VariableValue)) { lines.Add(l); owners.Add(path); }
                    foreach (var s in StructureCheck.Of(blocks)) { s.DocumentPath = path; s.Block = null; findings.Add(s); }
                }
                catch (Exception ex) { Diagnostics.Log("project lint: " + path, ex); }
                if (progress != null && (done % 25 == 0 || done == paths.Count)) progress(done, paths.Count);
            }
            const int Batch = 1500;
            for (var from = 0; from < lines.Count; from += Batch)
            {
                var count = Math.Min(Batch, lines.Count - from);
                var inputs = lines.GetRange(from, count).ConvertAll(l => l.ToLint());
                foreach (var h in _style.Lint(inputs, _project))
                {
                    var f = MapToRun(lines[from + h.Index], h);
                    if (f == null) continue;
                    f.DocumentPath = owners[from + h.Index];
                    f.Block = null;
                    findings.Add(f);
                }
            }
            foreach (var f in findings)
            {
                string t;
                if (string.IsNullOrEmpty(f.Excerpt) || f.DocumentPath == null || !texts.TryGetValue(f.DocumentPath, out t)) continue;
                f.SeenInBody = f.Seen = HandFixes.Count(t, f.Excerpt);   // project issues have no block: both are body counts
            }
            Diagnostics.Log("Project lint: " + paths.Count + " files, " + lines.Count + " lines, " + findings.Count + " findings");
            return findings;
        }

        /// <summary>Resolves "Set.Name" to the variable's value for linting; null leaves the variable out.</summary>
        public Func<string, string> VariableValue { get; set; }

        private static StyleFinding MapToRun(TextLine line, LintHit h)
        {
            int start = h.Start, end = h.Start + h.Length;
            var pieces = new List<Segment>();
            foreach (var s in line.Segments)
                if (s.Offset < end && start < s.Offset + s.Text.Length && s.Text.Length > 0) pieces.Add(s);
            var first = pieces.Find(s => s.Run != null && s.Run.Value.Trim().Length > 0);
            if (first == null) return null; // entirely inside variable values (or whitespace)
            var from = Math.Max(start, first.Offset) - first.Offset;
            var to = Math.Min(end, first.Offset + first.Text.Length) - first.Offset;
            // Only a hit wholly inside one run can be fixed mechanically; one that spans markup or a variable
            // is shown on its first run and needs a rewrite.
            return FindingLocator.FromLint(h, first.Run, from, to - from, pieces.Count == 1);
        }

        /// <summary>
        /// Model review for what a linter cannot judge, unit by unit. Only findings that quote a run of their unit
        /// verbatim survive, so every finding can be shown in place; repeats of lint findings are dropped.
        /// </summary>
        public List<StyleFinding> Review(IList<XmlNode> blocks, IList<StyleFinding> lint)
        {
            var units = PrepareReview(blocks, lint);
            var raw = new List<StyleFinding>[units.Count];
            Each(units.Count, i => raw[i] = CallReview(units[i]));
            var kept = new List<StyleFinding>();
            for (var i = 0; i < units.Count; i++) kept.AddRange(AnchorReview(raw[i], units[i], lint));
            return kept;
        }

        /// <summary>
        /// Step 1 (reads the DOM): splits the blocks into review units — one section each ("section", the default), or one block each ("block"), or one
        /// section each — with each unit's prompt: the marked runs, the topic and section it's in, and the lint already
        /// found there. Units with fewer than <paramref name="minWords"/> words (a short heading) are skipped.
        /// </summary>
        public List<ReviewUnit> PrepareReview(IList<XmlNode> blocks, IList<StyleFinding> lint, int minWords = 4)
        {
            var units = new List<ReviewUnit>();
            if (blocks.Count == 0) return units;
            var groups = Routing.ReviewUnit == "section"
                ? ReviewOrder.Sections(blocks)
                : blocks.Select(b => new List<XmlNode> { b }).ToList();
            var title = TopicTitle(blocks[0].OwnerDocument);
            foreach (var g in groups)
            {
                var map = RunMap.Of(g);
                if (map.Ids.Count == 0 || WordCount(map) < minWords) continue;
                var unitLint = (lint ?? new List<StyleFinding>()).Where(f => f.DocumentPath == null && g.Exists(b => Contains(b, f))).ToList();
                var context = (title.Length > 0 ? "Topic: " + title + "\n" : "") + (SectionOf(g[0]).Length > 0 ? "Section: " + SectionOf(g[0]) : "");
                units.Add(new ReviewUnit { Blocks = g, Runs = map, Prompt = _prompts.BuildReview(context, map.Marked, Summarize(unitLint)), Scope = ScopeOf(g), Lint = unitLint });
            }
            ShareScope(units.Select(u => u.Scope).ToList());
            return units;
        }

        /// <summary>
        /// The units of one check carry the same packs (the union of what they contain) and the team's always-on packs, so
        /// the pack block is written to the cache once and read by the rest. Each unit keeps its own text for word rules.
        /// </summary>
        private void ShareScope(List<GuideScope> scopes)
        {
            var union = new HashSet<string>(scopes.SelectMany(s => s.Content));
            foreach (var s in scopes)
            {
                s.Content = new HashSet<string>(union);
                s.Always = new HashSet<string>(Routing.AlwaysPacks ?? new List<string>());
                s.SharedPacks = scopes.Count > 1;
            }
        }

        /// <summary>Step 2 (no DOM access; runs off the UI thread, several at once): the model's findings for one unit.</summary>
        public List<StyleFinding> CallReview(ReviewUnit unit)
        {
            var resp = Ask("review", unit.Prompt, Contracts.ReviewSchema, null, unit.Scope);
            int dropped;
            var raw = Contracts.ReadReview(resp.Json, unit.Runs, out dropped);
            if (dropped > 0) Diagnostics.Log("Review: dropped " + Plural.Of(dropped, "finding") + " that broke the contract");
            // The model may cite only the rules it was given for this unit.
            HashSet<string> allowed = null;
            if (_style != null) try { allowed = new HashSet<string>(_style.RuleIdsFor(_project, unit.Scope), StringComparer.OrdinalIgnoreCase); } catch (Exception ex) { Diagnostics.Log("rule ids", ex); }
            unit.Allowed = allowed;
            Contracts.KeepCitable(raw, allowed);
            return raw;
        }

        /// <summary>
        /// Step 3 (reads the DOM, on the thread that edits): anchor each finding in its live run; drop text edited since
        /// the review and repeats of lint findings.
        /// </summary>
        public List<StyleFinding> AnchorReview(List<StyleFinding> raw, ReviewUnit unit, IList<StyleFinding> lint)
        {
            var kept = new List<StyleFinding>();
            var rules = GuideRules();
            foreach (var f in raw)
            {
                if (IsTurnedOff(f.RuleId)) continue;   // turned off since the review was cached
                XmlText run;
                if (f.RunId == null || !unit.Runs.Runs.TryGetValue(f.RunId, out run) || run.ParentNode == null) continue;
                var at = run.Value.IndexOf(f.Excerpt, StringComparison.Ordinal);
                if (at < 0) continue;
                if (f.Replacement != null)
                {
                    // A replacement that changes a modal verb or drops a code term needs the author: keep the finding, not the fix.
                    var held = MeaningGuard.Check(f.Excerpt, f.Replacement, MeaningGuard.InCode(run));
                    if (held != null) { f.Replacement = null; f.Suggestion = ((f.Suggestion ?? "").Trim() + " " + held).Trim(); }
                }
                var anchor = FindingLocator.ForSpan(run, at, f.Excerpt.Length, f.Replacement, TextRuns.BlockOf(run));
                f.Before = anchor.Before;
                f.Block = anchor.Block;
                Ground(f, rules);
                PreferOwnLayer(f, unit.Allowed);
                if (!Duplicates(f, lint ?? new List<StyleFinding>())) kept.Add(f);
                else Diagnostics.Log("AI finding dropped: it repeats a lint row");   // no document text: diag.log goes with reports
            }
            return kept;
        }

        /// <summary>
        /// The rules a run-level (wording-only) edit can act on. Word rules come only for words the text uses, as for review;
        /// content packs about formatting (code font, command syntax, placeholders, images, tables, footnotes, emphasis) are
        /// left out, because a run-level edit changes text, never markup. Measured: a one-paragraph rewrite in API docs
        /// carried ~25k characters of rules, 23k of them the Code pack; review and restructure still get every pack.
        /// </summary>
        internal static GuideScope WordingScope(IList<XmlNode> blocks)
        {
            var scope = ScopeOf(blocks);
            scope.Content.ExceptWith(ContentTags.Formatting);
            return scope;
        }

        /// <summary>The unit's content tags and its visible text (reads the DOM: call on the thread that owns it).</summary>
        private static GuideScope ScopeOf(IList<XmlNode> blocks)
        {
            return GuideScope.Of(ContentTags.Of(blocks), string.Join(" ", AcceptedView.Of(blocks).Select(b => b.InnerText).ToArray()));
        }

        private static bool Contains(XmlNode block, StyleFinding f)
        {
            return (block == f.Block || TextRuns.BlockOf(block) == f.Block) && block.InnerText.IndexOf(f.Excerpt ?? "", StringComparison.Ordinal) >= 0;
        }

        private static int WordCount(RunMap map)
        {
            var n = 0;
            foreach (var id in map.Ids) n += map.Texts[id].Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            return n;
        }

        private static string TopicTitle(XmlDocument doc)
        {
            var body = BlockSelector.FindBody(doc);
            var h1 = body == null ? null : body.SelectSingleNode(".//*[local-name()='h1']");
            if (h1 != null) return System.Text.RegularExpressions.Regex.Replace(h1.InnerText, @"\s+", " ").Trim();
            var t = doc == null ? null : doc.SelectSingleNode("//*[local-name()='title']");
            return t == null ? "" : t.InnerText.Trim();
        }

        /// <summary>The heading the node falls under ("" before the first heading).</summary>
        private static string SectionOf(XmlNode node)
        {
            var block = TextRuns.BlockOf(node) ?? node;
            for (var n = block; n != null; n = n.PreviousSibling)
                if (n.NodeType == XmlNodeType.Element && ReviewOrder.IsHeading(n))
                    return System.Text.RegularExpressions.Regex.Replace(n.InnerText, @"\s+", " ").Trim();
            return "";
        }

        private Dictionary<string, GuideRule> _rules;
        private Dictionary<string, GuideExample> _examples;

        private List<GuideRule> _ruleList = new List<GuideRule>();

        private Dictionary<string, GuideRule> GuideRules()
        {
            if (_rules != null) return _rules;
            var map = new Dictionary<string, GuideRule>(StringComparer.OrdinalIgnoreCase);
            if (_style == null) return map;
            try { var list = _style.Rules(_project).ToList(); foreach (var r in list) map[r.Id] = r; _ruleList = list; _rules = map; }
            catch (Exception ex) { Diagnostics.Log("guide rules", ex); }
            return map;
        }

        private bool IsTurnedOff(string ruleId)
        {
            if (ruleId == null || _style == null) return false;
            try { return _style.IsOff(_project, ruleId); }
            catch (Exception ex) { Diagnostics.Log("rule off?", ex); return false; }
        }

        /// <summary>The guide rule with this id (for the proposal card), or null.</summary>
        public GuideRule RuleFor(string id)
        {
            GuideRule r;
            return id != null && GuideRules().TryGetValue(id, out r) ? r : null;
        }

        /// <summary>The rule's Bad/Good example, or null. Loaded once per runner.</summary>
        public GuideExample ExampleFor(string id)
        {
            if (id == null || _style == null) return null;
            if (_examples == null)
            {
                _examples = new Dictionary<string, GuideExample>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var e in _style.Examples(_project)) _examples[e.Id] = e; }
                catch (Exception ex) { Diagnostics.Log("guide examples", ex); }
            }
            GuideExample g;
            return _examples.TryGetValue(id, out g) ? g : null;
        }

        /// <summary>
        /// Re-asks the model for one proposal with the author's instruction. Returns a new finding (same rule, text
        /// and anchor; new replacement and reason). No DOM access: pass the paragraph text as context.
        /// </summary>
        public StyleFinding Refine(StyleFinding f, string context, string instruction)
        {
            var proposal = (f.Replacement == null ? "(no exact text proposed — needs a rewrite)" : "\"" + f.Replacement + "\"") + " — " + f.Suggestion;
            GuideRule rule = null;
            var known = f.RuleId != null && GuideRules().TryGetValue(f.RuleId, out rule);   // the rule's own pack and words come along
            var json = Ask("refine", _prompts.BuildRefine(context, f.Excerpt, proposal, instruction), Contracts.Refine(), null,
                           GuideScope.Of(known ? rule.Applies : null, context)).Json;
            var r = json["replacement"];
            var replacement = r == null || r.Type == JTokenType.Null ? null : (string)r;
            if (replacement != null && replacement.IndexOf('<') >= 0) replacement = null;
            var why = (string)json["why"];
            // The earlier proposal stands when the instruction doesn't apply ("Use select instead of click" on text without
            // "click"), when the answer lost the fix, or when it changes a modal verb or a code term. Measured: 3 of 4
            // non-applying instructions returned the original text or nothing, discarding a better proposal.
            if (f.Replacement != null)
            {
                var held = replacement == null ? null : MeaningGuard.Check(f.Excerpt, replacement, false, "The refined wording");
                string because = (bool?)json["applies"] == false ? "The instruction doesn't apply to this text"
                               : replacement == null || replacement == f.Excerpt && (bool?)json["applies"] != true ? "The refined answer dropped the fix"
                               : held != null ? held.Replace(" Edit it yourself if that is meant.", "") : null;
                if (because != null)
                {
                    replacement = f.Replacement;
                    why = because.TrimEnd('.') + ", so the earlier suggestion stands. " + f.Suggestion;
                }
            }
            return new StyleFinding
            {
                Source = "ai", Layer = "ai", Rule = f.Rule, RuleId = f.RuleId, RuleLayer = f.RuleLayer, RuleSection = f.RuleSection, Link = f.Link,
                Severity = f.Severity, Excerpt = f.Excerpt, Before = f.Before, Block = f.Block, DocumentPath = f.DocumentPath, RunId = f.RunId,
                Replacement = replacement, Suggestion = string.IsNullOrEmpty(why) ? f.Suggestion : why, Section = f.Section, Order = f.Order
            };
        }

        /// <summary>Alt text for an image (vision). Null when the model judges it decorative.</summary>
        public string SuggestAlt(LlmImageFile image, string context)
        {
            var images = new List<LlmImage> { new LlmImage { MediaType = image.MediaType, Base64 = image.Base64 } };
            var json = Ask("alt", _prompts.BuildAlt(context), Contracts.Alt(), images, null, false).Json;
            var alt = ((string)json["alt"] ?? "").Trim();
            if ((bool?)json["decorative"] == true || alt.Length == 0) return null;
            if (alt.Length > Contracts.AltMaxLength)
            {
                // Measured: 13 of 20 came back over the limit (average 128). One short retry, then cut at a word boundary.
                Diagnostics.Log("alt: " + alt.Length + " characters; asking for a shorter one");
                var retry = Ask("alt", _prompts.BuildAlt(context) + "\n\nYour first answer was " + alt.Length + " characters: \"" + alt + "\". Say the same in at most " +
                                       Contracts.AltMaxLength + " characters.", Contracts.Alt(), images, null, false).Json;
                var shorter = ((string)retry["alt"] ?? "").Trim();
                if (shorter.Length > 0 && shorter.Length < alt.Length) alt = shorter;
            }
            return Contracts.FitAlt(alt);
        }

        /// <summary>
        /// Meta description, index keywords, search synonyms, micro content and glossary candidates for the blocks: three
        /// small calls, each with its own contract, run at once.
        /// </summary>
        public TopicAssist Assist(IList<XmlNode> blocks)
        {
            var glossary = "";
            if (_style is StyleEngine)
                try { foreach (var g in ((StyleEngine)_style).GlossaryTerms(_project)) glossary += g + "\n"; } catch (Exception ex) { Diagnostics.Log("assist: glossary", ex); }
            var fragment = XhtmlFragment.Serialize(TrackedChanges.WithoutDeletions(blocks));
            var parts = new[]
            {
                new KeyValuePair<string, JObject>("Write its meta description and its index keywords (3-8; 'Parent:Child' for a subentry).", Contracts.AssistMeta()),
                new KeyValuePair<string, JObject>("List search synonym groups: up to 5 terms the topic uses, each with the words readers might search for instead.", Contracts.AssistSynonyms()),
                new KeyValuePair<string, JObject>("Write up to 5 micro content entries (a question or phrase readers search for, and a 1-2 sentence answer from the topic) and up to 5 glossary candidates (terms the topic uses but doesn't define).", Contracts.AssistAnswers()),
            };
            var answers = new JObject[parts.Length];
            Each(parts.Length, i => answers[i] = Ask("assist", _prompts.BuildAssist(fragment, glossary.Trim(), parts[i].Key), parts[i].Value, null, null, false).Json);
            var t = new TopicAssist { Description = (string)answers[0]["description"] };
            foreach (var k in answers[0]["keywords"] ?? new JArray()) t.Keywords.Add((string)k);
            foreach (var g in answers[1]["groups"] ?? new JArray())
            {
                var group = new List<string> { (string)g["term"] };
                foreach (var a in g["alternatives"] ?? new JArray()) group.Add((string)a);
                if (group.Count > 1) t.Synonyms.Add(group);
            }
            foreach (var m in answers[2]["micro_content"] ?? new JArray()) t.MicroContent.Add(new KeyValuePair<string, string>((string)m["phrase"], (string)m["response"]));
            foreach (var g in answers[2]["glossary"] ?? new JArray()) t.Glossary.Add(new KeyValuePair<string, string>((string)g["term"], (string)g["definition"]));
            return t;
        }

        /// <summary>
        /// Candidate style-guide rules from an author's notes, an old guide, or another tool's prompt: the notes are split
        /// into paragraphs-sized chunks, each read on its own (small, focused jobs).
        /// </summary>
        public List<RuleCandidate> ExtractRules(string notes)
        {
            var chunks = GuideBuilder.Chunks(notes);
            var answers = new JArray[chunks.Count];
            Each(chunks.Count, i => answers[i] = (JArray)(Ask("extract", _prompts.BuildExtract(chunks[i]), Contracts.Extract()).Json["rules"] ?? new JArray()));
            var all = new JArray();
            foreach (var a in answers) foreach (var r in a) all.Add(r);
            return GuideBuilder.FromExtracted(all);
        }

        private static readonly Dictionary<string, int> SnippetUses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many topics and snippets in the project include this snippet (by file name). Cached per session.</summary>
        public static int SnippetUsage(string snippetPath)
        {
            lock (SnippetUses)
            {
                int n;
                if (SnippetUses.TryGetValue(snippetPath, out n)) return n;
                var name = System.IO.Path.GetFileName(snippetPath);
                foreach (var file in ProjectFiles(snippetPath, true))   // an excluded topic still uses the snippet
                {
                    if (string.Equals(file, snippetPath, StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (System.IO.File.ReadAllText(file).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) n++; }
                    catch (Exception) { }
                }
                SnippetUses[snippetPath] = n;
                return n;
            }
        }

        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rule", "rules", "guide", "style", "google", "company", "team", "word", "words", "list", "general", "writing", "write", "overrides",
            "changes", "text", "content", "about", "other", "avoid", "using", "instead", "don't", "make", "when", "that", "this", "with", "your",
            "from", "they", "them", "their", "have", "more", "than", "only", "such", "also", "each", "which", "what", "language", "documentation", "docs"
        };

        /// <summary>Content words of a title or rule, crudely stemmed ("claims" → "claim"), generic words left out.</summary>
        private static HashSet<string> Stems(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches((text ?? "").ToLowerInvariant(), @"[a-z][a-z']{3,}"))
            {
                var w = m.Value;
                if (Generic.Contains(w)) continue;
                if (w.EndsWith("ies") && w.Length > 5) w = w.Substring(0, w.Length - 3) + "y";
                else if (w.EndsWith("es") && w.Length > 5 && !w.EndsWith("ses")) w = w.Substring(0, w.Length - 2);
                else if (w.EndsWith("s") && !w.EndsWith("ss")) w = w.Substring(0, w.Length - 1);
                set.Add(w);
            }
            return set;
        }

        /// <summary>
        /// A finding that cites a Google rule cites the team's rule instead when a later layer (company, team, personal,
        /// project) has a rule on the same subject that was sent with the unit: that rule overrides Google's, and it is the
        /// one the team wants named. Measured: 15 of 15 marketing claims cited Google's excessive-claims rule, not the team's
        /// Claims rule. "Same subject": the two rules' pages or sections share a content word ("Excessive claims" / "Claims").
        /// </summary>
        private void PreferOwnLayer(StyleFinding f, ICollection<string> allowed)
        {
            GuideRule cited;
            if (f.RuleId == null || !string.Equals(f.RuleLayer, GoogleLayer, StringComparison.OrdinalIgnoreCase) || !GuideRules().TryGetValue(f.RuleId, out cited)) return;
            var subject = Stems(cited.Page + " " + cited.Section);
            if (subject.Count == 0) return;
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _ruleList) if (r.Layer != null && !rank.ContainsKey(r.Layer)) rank[r.Layer] = rank.Count;
            GuideRule best = null;
            foreach (var r in _ruleList)
            {
                if (r.IsLint || string.Equals(r.Layer, GoogleLayer, StringComparison.OrdinalIgnoreCase)) continue;
                if (allowed != null && !allowed.Contains(r.Id)) continue;
                if (!Stems(r.Page + " " + r.Section).Overlaps(subject)) continue;
                if (IsTurnedOff(r.Id)) continue;
                if (best == null || rank[r.Layer] > rank[best.Layer]) best = r;
            }
            if (best == null) return;
            Diagnostics.Log("Review: cites " + best.Id + " (overrides " + f.RuleId + ")");
            f.RuleId = best.Id; f.RuleLayer = best.Layer; f.RuleSection = best.Section; f.Link = best.Link;
        }

        /// <summary>Keeps a cited rule only if it exists in the guide; attaches its layer, section, and link.</summary>
        private static void Ground(StyleFinding f, Dictionary<string, GuideRule> rules)
        {
            GuideRule rule;
            if (f.RuleId != null && rules.TryGetValue(f.RuleId.Trim(), out rule))
            {
                f.RuleId = rule.Id; f.RuleLayer = rule.Layer; f.RuleSection = rule.Section;
                if (string.IsNullOrEmpty(f.Link)) f.Link = rule.Link;
            }
            else f.RuleId = null; // "general", or a made-up id
        }

        /// <summary>The layer of the generated Google guide: its examples are compiled and validated by the maintainers.</summary>
        public const string GoogleLayer = "google";

        /// <summary>
        /// The examples Validate's AI check runs: the team's own layers (company, team, personal, project) by default.
        /// Google's ~620 examples are validated offline when the guide is compiled; checking them again costs about $15
        /// and half an hour, so they are included only when the owner asks.
        /// </summary>
        public static List<GuideExample> ExamplesToVerify(IEnumerable<GuideExample> examples, bool includeGoogle)
        {
            return (examples ?? new GuideExample[0])
                .Where(e => includeGoogle || !string.Equals(e.Layer, GoogleLayer, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>Model calls <see cref="VerifyExamples"/> makes for these examples (one per Bad, one per Good).</summary>
        public static int VerifyCalls(IEnumerable<GuideExample> examples)
        {
            return (examples ?? new GuideExample[0]).Sum(e => (string.IsNullOrEmpty(e.Bad) ? 0 : 1) + (string.IsNullOrEmpty(e.Good) ? 0 : 1));
        }

        /// <summary>
        /// Checks that the AI applies prose rules: each Bad example must produce a finding citing that rule, each
        /// Good example must not. One model call per example sentence, <see cref="Parallelism"/> at a time (the first
        /// alone, to write the prompt cache the rest read). Results come back in the examples' order.
        /// </summary>
        /// <param name="progress">(calls done, calls in all); called from worker threads.</param>
        public List<ExampleResult> VerifyExamples(IList<GuideExample> examples, Action<int, int> progress = null)
        {
            var rules = GuideRules();
            var results = examples.Select(ex => new ExampleResult { Example = ex }).ToList();
            var jobs = new List<KeyValuePair<int, bool>>();   // (example, bad?)
            for (var i = 0; i < examples.Count; i++)
            {
                if (!string.IsNullOrEmpty(examples[i].Bad)) jobs.Add(new KeyValuePair<int, bool>(i, true));
                if (!string.IsNullOrEmpty(examples[i].Good)) jobs.Add(new KeyValuePair<int, bool>(i, false));
            }
            var done = 0;
            Each(jobs.Count, j =>
            {
                var ex = examples[jobs[j].Key];
                if (jobs[j].Value) results[jobs[j].Key].BadCaught = Cites(ex.Bad, ex.Id, rules);
                else results[jobs[j].Key].GoodClean = !Cites(ex.Good, ex.Id, rules);
                var n = System.Threading.Interlocked.Increment(ref done);
                if (progress != null) progress(n, jobs.Count);
            });
            return results;
        }

        private bool Cites(string text, string id, Dictionary<string, GuideRule> rules)
        {
            var doc = new XmlDocument();
            doc.LoadXml("<html><body><p>" + System.Security.SecurityElement.Escape(text) + "</p></body></html>");
            var units = PrepareReview(new List<XmlNode> { doc.DocumentElement["body"].FirstChild }, null, 1);
            if (units.Count == 0) return false;
            GuideRule rule;
            if (rules.TryGetValue(id, out rule)) units[0].Scope.Content.UnionWith(rule.Applies);   // the rule under test must be in the prompt
            foreach (var f in CallReview(units[0]))
            {
                Ground(f, rules);
                if (string.Equals(f.RuleId, id, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The block that currently holds the finding's text (blocks are replaced by edits), or null.</summary>
        public XmlNode LocateBlock(IDocumentAdapter doc, StyleFinding f)
        {
            XmlText run; int start;
            return FindingLocator.TryLocate(doc.Document, f, out run, out start) ? TextRuns.BlockOf(run) : null;
        }

        /// <summary>Lint + review in one call (the UI runs the two phases separately to show lint results first).</summary>
        public List<StyleFinding> CheckStyle(IDocumentAdapter doc)
        {
            var blocks = PickCheckBlocks(doc);
            var lint = Lint(blocks);
            lint.AddRange(LintSnippets(blocks, doc.SourcePath));
            var all = new List<StyleFinding>(lint);
            all.AddRange(Review(blocks, lint));
            return all;
        }

        /// <summary>Selects the finding's text in the editor. False when the text is no longer in the topic.</summary>
        public bool GoTo(IDocumentAdapter doc, StyleFinding f)
        {
            XmlText run; int start;
            return FindingLocator.TryLocate(doc.Document, f, out run, out start) && doc.SelectText(run, start, f.Excerpt.Length);
        }

        /// <summary>
        /// Applies the replacements of fixable findings as word-level tracked changes in one undoable operation.
        /// Every finding is re-located right before its edit (each edit changes the text nodes), right-most first.
        /// Returns how many were applied.
        /// </summary>
        public int ApplyFixes(IDocumentAdapter doc, IEnumerable<StyleFinding> findings, string operationName)
        {
            var n = 0;
            WriteTracked(doc.SourcePath, () => n = ApplyFixesNow(doc, findings, operationName));
            return n;
        }

        private int ApplyFixesNow(IDocumentAdapter doc, IEnumerable<StyleFinding> findings, string operationName)
        {
            var order = new Dictionary<XmlNode, int>();
            var body = BlockSelector.FindBody(doc.Document);
            if (body == null) return 0;
            var all = TextRuns.Of(new List<XmlNode> { body });
            for (var i = 0; i < all.Count; i++) order[all[i]] = i;

            var todo = new List<KeyValuePair<StyleFinding, long>>();
            foreach (var f in findings)
            {
                XmlText run; int start; int pos;
                if (!f.CanFix) continue;
                if (FindingLocator.TryLocate(doc.Document, f, out run, out start) && order.TryGetValue(run, out pos))
                    todo.Add(new KeyValuePair<StyleFinding, long>(f, (long)pos * 1000000 + start));
                else f.IsStale = true; // can't be placed safely: surface "Text changed", don't skip silently
            }
            todo.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (todo.Count == 0) return 0;

            var applied = 0;
            var text = todo;
            if (text.Count > 0)
                doc.Edit(operationName, () =>
                {
                    foreach (var item in text)
                    {
                        var f = item.Key;
                        XmlText run; int start;
                        if (!FindingLocator.TryLocate(doc.Document, f, out run, out start)) { f.IsStale = true; continue; }
                        if (doc.ReplaceSpan(run, start, f.Excerpt.Length, f.Replacement)) { f.IsFixed = true; f.Written = true; applied++; }
                    }
                });
            Diagnostics.Log("Applied " + operationName + ": " + applied + "/" + todo.Count + " text fix(es)");
            return applied;
        }

        // ---- rewrite / ask ---------------------------------------------------------------------

        public RewriteProposal Rewrite(IDocumentAdapter doc, string instruction)
        {
            return Rewrite(doc, PickBlocks(doc), instruction);
        }

        public RewriteProposal Rewrite(IDocumentAdapter doc, IList<XmlNode> blocks, string instruction)
        {
            return Rewrite(doc, blocks, instruction, true);
        }

        /// <summary>
        /// Rewrite by wording only: each block is its own job (run at once), the model returns new text for the runs it
        /// changes — never markup — so the structure can't break. Applied as word-level tracked changes. If the task needs
        /// structural edits, the proposal says so (<see cref="RewriteProposal.NeedsRestructure"/>); see <see cref="Restructure"/>.
        /// </summary>
        /// <param name="lint">False for style-guide topics: they quote the very words their rules ban.</param>
        /// <param name="task">Route: "rewrite", or "draft" for owner edits to the style guide.</param>
        public RewriteProposal Rewrite(IDocumentAdapter doc, IList<XmlNode> blocks, string instruction, bool lint, string task = "rewrite")
        {
            foreach (var b in blocks)
                if (BlockSelector.IsOpaque(b))
                    throw new InvalidOperationException("The selection includes a " + b.Name + ", which cannot be rewritten in place. Select text inside a paragraph instead.");
            var notes = new List<string>();
            var found = lint ? SafeLint(blocks, notes) : new List<StyleFinding>();
            var proposal = RewriteRuns(blocks, instruction, found, task);
            proposal.Notes = notes;
            if (!lint || _style == null || notes.Count > 0) return WithHeld(proposal);

            // Self-check: lint our own result and give the model one chance to fix what it left behind.
            var left = Significant(SafeLint(proposal.ProposedBlocks, notes));
            if (left.Count > 0)
            {
                var repaired = RewriteRuns(blocks, (string.IsNullOrEmpty(instruction) ? "" : instruction + "\n") + "Also fix these, which your first version left in:\n" + Summarize(left), found, task);
                var leftAfter = Significant(SafeLint(repaired.ProposedBlocks, notes));
                if (leftAfter.Count < left.Count) { repaired.Notes = notes; proposal = repaired; left = leftAfter; }
            }
            proposal.RemainingLint = left;
            return WithHeld(proposal);
        }

        /// <summary>The proposal's notes plus why any change was held back for the author.</summary>
        private static RewriteProposal WithHeld(RewriteProposal p)
        {
            if (p.Held.Count > 0) p.Notes = (p.Notes ?? new List<string>()).Concat(p.Held).ToList();
            return p;
        }

        private RewriteProposal RewriteRuns(IList<XmlNode> blocks, string instruction, List<StyleFinding> lint, string task)
        {
            var proposal = new RewriteProposal
            {
                OriginalBlocks = blocks, View = AcceptedView.Of(blocks), Validation = new ValidationResult(), Notes = new List<string>(),
                RemainingLint = new List<StyleFinding>(), WordPlan = new List<StyleFinding>()
            };
            var units = blocks.Select(b => new { Block = b, Map = RunMap.Of(new List<XmlNode> { b }), Scope = WordingScope(new List<XmlNode> { b }) }).ToList();
            ShareScope(units.Select(u => u.Scope).ToList());
            var prompts = units.Select(u => _prompts.BuildRunRewrite(instruction, u.Map.Marked, Summarize(lint.Where(f => f.DocumentPath == null && Contains(u.Block, f)).ToList()),
                                                                     Contracts.DescribeContinuations(u.Map))).ToList();
            var changes = new Dictionary<string, string>[units.Count];
            var restructure = new bool[units.Count];
            var restructureNotes = new string[units.Count];
            Each(units.Count, i =>
            {
                if (units[i].Map.Ids.Count == 0) { changes[i] = new Dictionary<string, string>(); return; }
                var schema = Contracts.RewriteSchema;
                var errors = new List<string>();
                changes[i] = Contracts.ReadRewrite(Ask(task, prompts[i], schema, null, units[i].Scope).Json, units[i].Map, errors, out restructure[i], out restructureNotes[i]);
                if (errors.Count > 0)
                {
                    // One retry with the contract breaches spelled out; then keep what was valid.
                    Diagnostics.Log(task + ": " + errors.Count + " contract breach(es), retrying");
                    Diagnostics.LogContent(task + ": contract breaches: " + string.Join(" ", errors.ToArray()));   // they quote the text
                    var retryErrors = new List<string>();
                    var retry = Contracts.ReadRewrite(Ask(task, prompts[i] + "\n\nYour previous answer broke the format: " + string.Join(" ", errors.ToArray()) + " Answer again.", schema, null, units[i].Scope).Json,
                                                      units[i].Map, retryErrors, out restructure[i], out restructureNotes[i]);
                    if (retryErrors.Count <= errors.Count) changes[i] = retry;
                    if (retryErrors.Count > 0) Diagnostics.Log(task + ": still " + retryErrors.Count + " breach(es); kept the valid changes");
                }
            });

            // Changes only the author may make (a modal verb, a code term, text inside code) are held back, with the reason.
            for (var i = 0; i < units.Count; i++)
                foreach (var id in changes[i].Keys.ToList())
                {
                    var held = MeaningGuard.Check(units[i].Map.Texts[id], changes[i][id], MeaningGuard.InCode(units[i].Map.Runs[id]), "Kept a sentence as it was: the suggested wording");
                    if (held == null) continue;
                    changes[i].Remove(id);
                    proposal.Held.Add(held);
                }

            var proposed = new List<XmlNode>();
            for (var i = 0; i < units.Count; i++)
            {
                var map = units[i].Map;
                var clone = units[i].Block.CloneNode(true);
                var cloneRuns = TextRuns.Of(new List<XmlNode> { clone }).Where(r => r.Value.Trim().Length > 0).ToList();
                for (var k = 0; k < map.Ids.Count && k < cloneRuns.Count; k++)
                {
                    string text;
                    if (!changes[i].TryGetValue(map.Ids[k], out text)) continue;
                    cloneRuns[k].Value = text;
                    var run = map.Runs[map.Ids[k]];
                    var block = TextRuns.BlockOf(run) ?? units[i].Block;
                    if (!WordDiff.PlanRun(run, text, block, proposal.WordPlan))
                        proposal.WordPlan.Add(FindingLocator.ForSpan(run, 0, run.Value.Length, text, block));
                }
                proposed.Add(clone);
                if (restructure[i]) proposal.NeedsRestructure = true;
                // The note says what restructuring is needed, or what only the author can supply.
                if (!string.IsNullOrEmpty(restructureNotes[i])) proposal.RestructureNote = (proposal.RestructureNote == null ? "" : proposal.RestructureNote + " ") + restructureNotes[i];
            }
            proposal.ProposedBlocks = AcceptedView.Of(proposed);
            proposal.ProposedXml = XhtmlFragment.Serialize(proposal.ProposedBlocks);
            if (proposal.NeedsRestructure)
            {
                var lost = LostSentence(string.Join(" ", proposal.View.Select(b => b.InnerText).ToArray()), string.Join(" ", proposal.ProposedBlocks.Select(b => b.InnerText).ToArray()));
                if (lost != null)
                    proposal.ApplyBlocked = "The wording-only version leaves out \"" + (lost.Length > 60 ? lost.Substring(0, 57) + "…" : lost) +
                                            "\", which the task needs moved, not deleted. Use Restructure instead.";
            }
            return proposal;
        }

        /// <summary>
        /// Structural rewrite (split a step, make a list): the model returns XHTML, which must pass the validator. Applied
        /// word-level when the structure turns out unchanged, otherwise as a block replacement.
        /// </summary>
        public RewriteProposal Restructure(IDocumentAdapter doc, IList<XmlNode> blocks, string instruction)
        {
            foreach (var b in blocks)
                if (BlockSelector.IsOpaque(b))
                    throw new InvalidOperationException("The selection includes a " + b.Name + ", which cannot be rewritten in place. Select text inside a paragraph instead.");
            var view = AcceptedView.Of(blocks);
            var notes = new List<string>();
            var lint = SafeLint(blocks, notes);
            var kind = string.IsNullOrEmpty(instruction) ? ActionKind.Rewrite : ActionKind.Ask;
            var proposal = Propose(doc, blocks, view, _prompts.BuildUser(kind, XhtmlFragment.Serialize(view), instruction, Summarize(lint)), "restructure");
            proposal.Notes = notes;
            proposal.RemainingLint = proposal.Validation.IsValid ? Significant(SafeLint(proposal.ProposedBlocks, notes)) : new List<StyleFinding>();
            return proposal;
        }

        private RewriteProposal Propose(IDocumentAdapter doc, IList<XmlNode> blocks, IList<XmlNode> view, string user, string task)
        {
            // The edit-family schema, so restructure reads the review/rewrite cache entry instead of writing its own.
            var resp = Ask(task, Contracts.RestructureFields + "\n\n" + user, Contracts.EditSchema, null, ScopeOf(blocks));
            var proposal = new RewriteProposal { OriginalBlocks = blocks, View = view, Raw = resp, Validation = new ValidationResult(), Notes = new List<string>(), RemainingLint = new List<StyleFinding>() };
            try
            {
                proposal.ProposedXml = Contracts.ReadRevised(resp);
                proposal.ProposedBlocks = XhtmlFragment.ParseInto(proposal.ProposedXml, doc.Document);
                proposal.Validation = XhtmlValidator.Validate(view, proposal.ProposedBlocks);
                if (proposal.Validation.IsValid)
                {
                    var held = MeaningGuard.Check(string.Join(" ", view.Select(b => b.InnerText).ToArray()), string.Join(" ", proposal.ProposedBlocks.Select(b => b.InnerText).ToArray()), false, "Check this edit: it");
                    if (held != null) proposal.Validation.Warnings.Add(held.Replace(" Edit it yourself if that is meant.", ""));
                    proposal.WordPlan = WordDiff.Plan(view, proposal.ProposedBlocks, blocks);
                    if (proposal.WordPlan == null && TrackedChanges.HasPending(blocks))
                        proposal.Validation.Errors.Add("This edit restructures text that has pending tracked changes. Accept or reject those changes (Review tab) first, or ask for a wording-only edit.");
                    else if (proposal.WordPlan == null)
                        proposal.ApplyBlocked = NodeWrites.Refusal(blocks, proposal.ProposedBlocks);   // a node replacement Flare can't take safely
                }
            }
            catch (XmlException xe)
            {
                proposal.Validation.Errors.Add("Response is not well-formed XML: " + xe.Message);
            }
            catch (FormatException fe)
            {
                proposal.Validation.Errors.Add(fe.Message);
            }
            return proposal;
        }

        /// <summary>
        /// Applies a validated proposal as one undoable tracked operation: word-level replacements when the
        /// proposal kept the structure, otherwise a block replacement. Refuses if validation failed — the UI must
        /// not bypass this. Returns a one-line description of what was applied.
        /// </summary>
        public string Apply(IDocumentAdapter doc, RewriteProposal proposal, string operationName)
        {
            if (proposal == null || !proposal.Validation.IsValid)
                throw new InvalidOperationException("Cannot apply: the suggestion failed validation.");
            if (proposal.ApplyBlocked != null) throw new InvalidOperationException(proposal.ApplyBlocked);

            var plan = proposal.WordPlan;
            if (plan != null)
            {
                if (plan.Count == 0) return "No wording changes to apply.";
                // All-or-nothing: only go word-level when every piece can be found right now.
                var locatable = plan.TrueForAll(f => { XmlText r; int s; return FindingLocator.TryLocate(doc.Document, f, out r, out s); });
                if (locatable)
                {
                    var n = ApplyFixes(doc, plan, operationName);
                    if (n < plan.Count) Diagnostics.Log(operationName + ": only " + n + "/" + plan.Count + " word edits applied");
                    return n == plan.Count
                        ? "Applied as " + Plural.Of(n, "word-level tracked change") + "."
                        : "Applied " + n + " of " + plan.Count + " word-level changes — review them, or Ctrl+Z to undo all.";
                }
                Diagnostics.Log(operationName + ": word plan not locatable, falling back to block replacement");
            }
            if (TrackedChanges.HasPending(proposal.OriginalBlocks))
                throw new InvalidOperationException("The paragraph has pending tracked changes and this edit cannot be applied word by word. Accept or reject them (Review tab) first.");
            var refusal = NodeWrites.Refusal(proposal.OriginalBlocks, proposal.ProposedBlocks);
            if (refusal != null) throw new InvalidOperationException(refusal);   // never the Redo crash path (variables)
            WriteTracked(doc.SourcePath, () => doc.ReplaceBlocks(operationName, proposal.OriginalBlocks, proposal.ProposedBlocks));
            Diagnostics.Log("Applied " + operationName + ": " + proposal.OriginalBlocks.Count + " → " + proposal.ProposedBlocks.Count + " blocks");
            return "Applied as a paragraph-level tracked change (the edit restructures the text).";
        }

        // ---- helpers -----------------------------------------------------------------------------

        /// <summary>
        /// The first sentence of <paramref name="before"/> (4+ words) that is gone from <paramref name="after"/>: fewer than
        /// half of its content words are still there. Null when every sentence survives, reworded or not.
        /// </summary>
        public static string LostSentence(string before, string after)
        {
            var words = new HashSet<string>(System.Text.RegularExpressions.Regex.Matches((after ?? "").ToLowerInvariant(), @"[a-z0-9']+").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value));
            foreach (var s in System.Text.RegularExpressions.Regex.Split((before ?? "").Trim(), @"(?<=[.!?])\s+"))
            {
                var content = System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9']+").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).ToList();
                if (content.Count < 4) continue;
                var key = content.Where(w => w.Length > 3).ToList();
                if (key.Count == 0) continue;
                if (key.Count(words.Contains) * 2 < key.Count) return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            }
            return null;
        }

        private List<StyleFinding> SafeLint(IList<XmlNode> blocks, List<string> notes)
        {
            try { return Lint(blocks); }
            catch (Exception ex)
            {
                Diagnostics.Log("docstyle", ex);
                notes.Add("The rules couldn't be loaded. " + ex.Message);
                return new List<StyleFinding>();
            }
        }

        private static List<StyleFinding> Significant(List<StyleFinding> findings)
        {
            return findings.FindAll(f => f.Severity != "suggestion");
        }

        /// <summary>Compact list the model can act on: one line per finding.</summary>
        public static string Summarize(IList<StyleFinding> findings)
        {
            if (findings == null || findings.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var f in findings)
            {
                sb.Append("- \"").Append(f.Excerpt).Append('"');
                if (f.Replacement != null) sb.Append(" to \"").Append(f.Replacement).Append('"');
                sb.Append(" (").Append(f.Rule).Append(": ").Append(f.Suggestion).AppendLine(")");
            }
            return sb.ToString().TrimEnd();
        }

        private static bool Duplicates(StyleFinding ai, IList<StyleFinding> lint)
        {
            foreach (var l in lint)
            {
                if (l.Block != ai.Block && l.Before != ai.Before) continue;
                // Same or narrower text than a lint hit = a repeat.
                if (l.Excerpt.IndexOf(ai.Excerpt, StringComparison.Ordinal) >= 0) return true;
                // Wider text whose fix rewrites the lint hit's words = the same problem quoted with its sentence (tester
                // report: one issue listed twice). A broader AI finding that leaves those words alone (a passive sentence) stays.
                if (ai.Replacement != null && l.Excerpt.Length > 0 && ai.Excerpt.IndexOf(l.Excerpt, StringComparison.Ordinal) >= 0
                    && ai.Replacement.IndexOf(l.Excerpt, StringComparison.Ordinal) < 0)
                    return true;
            }
            return false;
        }
    }
}
