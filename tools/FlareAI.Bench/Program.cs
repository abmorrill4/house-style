using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Llm;
using FlareAI.Core.Style;
using FlareAI.Scoring;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlareAI.Bench
{
    /// <summary>
    /// Runs the plugin's review (lint + structure + AI) over a corpus made by scripts/make-corpus.py, scores it against
    /// the seeded ground truth, simulates each author accepting or rejecting what they're shown (per-author feedback
    /// files, then the Tune analysis), and tries run-level rewrites on blocks with known defects.
    ///
    ///   FlareAI.Bench [--corpus corpus] [--model claude-sonnet-5-5] [--effort medium] [--unit block|section]
    ///                 [--sample 40] [--no-ai] [--rewrites 10] [--parallel 3] [--out bench-out\name]
    ///                 [--gateway http://host:port]   (Bifrost; virtual key from BIFROST_VK)
    ///   FlareAI.Bench summarize bench-out
    ///
    /// Label scoring (Bench v2, docs/testing/BENCH.md): when the corpus has labels.jsonl the findings are scored per rule, layer,
    /// context, project and split. Lint-only runs can be gated against the committed baseline:
    ///   FlareAI.Bench --no-ai --check              compare with tests/baselines/bench-lint.json, exit 1 on a regression
    ///   FlareAI.Bench --no-ai --update-baseline    rewrite the baseline from this run
    ///   FlareAI.Bench baseline-diff old.json new.json   rule rows that differ (for the PR-body check)
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            RegexLimit.Apply();
            try
            {
                if (args.Length > 0 && args[0] == "summarize") { Console.Write(Summarize(args.Length > 1 ? args[1] : "bench-out")); return 0; }
                if (args.Length > 0 && args[0] == "baseline-diff")
                {
                    foreach (var r in LabelScoring.ChangedRules(JObject.Parse(File.ReadAllText(args[1])), JObject.Parse(File.ReadAllText(args[2])))) Console.WriteLine(r);
                    return 0;
                }
                return new Bench(Options.Parse(args)).Run();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        /// <summary>One table row per run under <paramref name="dir"/> (each run's metrics.json).</summary>
        private static string Summarize(string dir)
        {
            var sb = new StringBuilder("| Run | Model | Effort | Unit | Topics | Recall all | Recall AI-only | Unseeded AI | Citation ok | Drops | Errors | $ | $/topic | s/topic |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (var f in Directory.GetFiles(dir, "metrics.json", SearchOption.AllDirectories).OrderBy(x => x))
            {
                var m = JObject.Parse(File.ReadAllText(f));
                sb.AppendFormat("| {0} | {1} | {2} | {3} | {4} | {5:P0} | {6:P0} | {7} | {8:P0} | {9} | {10} | {11:0.00} | {12:0.000} | {13:0.0} |\n",
                    Path.GetFileName(Path.GetDirectoryName(f)), m["model"], m["effort"], m["unit"], m["topics"], (double)m["recall"], (double)m["recall_ai_kinds"],
                    m["unseeded_ai"], (double)m["citation_accuracy"], m["contract_drops"], m["errors"], (double)m["usd"], (double)m["usd"] / Math.Max(1, (int)m["topics"]), (double)m["seconds_per_topic"]);
            }
            return sb.ToString();
        }
    }

    internal sealed class Options
    {
        public string Corpus = "corpus", Model = "claude-sonnet-5-5", Effort = "medium", Unit = "section", Out, Guide, Gateway;
        public int Sample, Rewrites, Parallel = 3, Examples;
        public string ExamplesFrom;
        public bool NoAi, Check, UpdateBaseline;
        public string Baseline, GateSplit = "all";
        public readonly List<string> Labels = new List<string>();

        public static Options Parse(string[] a)
        {
            var o = new Options();
            for (var i = 0; i < a.Length; i++)
            {
                Func<string> next = () => a[++i];
                switch (a[i])
                {
                    case "--corpus": o.Corpus = next(); break;
                    case "--model": o.Model = next(); break;
                    case "--effort": o.Effort = next(); break;
                    case "--unit": o.Unit = next(); break;
                    case "--sample": o.Sample = int.Parse(next()); break;
                    case "--rewrites": o.Rewrites = int.Parse(next()); break;
                    case "--parallel": o.Parallel = int.Parse(next()); break;
                    case "--no-ai": o.NoAi = true; break;
                    case "--examples": o.Examples = int.Parse(next()); break;
                    case "--examples-from": o.ExamplesFrom = next(); break;
                    case "--out": o.Out = next(); break;
                    case "--guide": o.Guide = next(); break;
                    case "--gateway": o.Gateway = next(); break;
                    case "--labels": o.Labels.Add(next()); break;
                    case "--check": o.Check = true; break;
                    case "--update-baseline": o.UpdateBaseline = true; break;
                    case "--baseline": o.Baseline = next(); break;
                    case "--gate-split": o.GateSplit = next(); break;
                    default: throw new ArgumentException("Unknown option " + a[i]);
                }
            }
            if ((o.Check || o.UpdateBaseline) && (!o.NoAi || o.Sample > 0 || o.Examples > 0 || o.Rewrites > 0))
                throw new ArgumentException("--check / --update-baseline need a full lint-only run (--no-ai, no --sample, --examples or --rewrites): the baseline is deterministic and offline.");
            if (o.Out == null) o.Out = Path.Combine("bench-out", (o.NoAi ? "lint" : o.Model.Replace("claude-", "") + "-" + o.Effort) + "-" + o.Unit + (o.Sample > 0 ? "-s" + o.Sample : ""));
            return o;
        }
    }

    /// <summary>A topic file on disk as the runner sees a document (no selection: the whole topic).</summary>
    internal sealed class FileDoc : IDocumentAdapter
    {
        private readonly Dictionary<string, string> _vars;
        public FileDoc(string path, Dictionary<string, string> vars)
        {
            SourcePath = path; _vars = vars;
            Document = new XmlDocument { PreserveWhitespace = true };
            Document.Load(path);
            SelectionNodes = new List<XmlNode>();
            SelectionText = "";
        }
        public string SourcePath { get; private set; }
        public bool IsEditableTopic { get { return BlockSelector.FindBody(Document) != null; } }
        public XmlDocument Document { get; private set; }
        public string SelectionText { get; set; }
        public IList<XmlNode> SelectionNodes { get; set; }
        public void ReplaceBlocks(string op, IList<XmlNode> oldNodes, IList<XmlNode> newNodes)
        {
            var parent = oldNodes[0].ParentNode;
            foreach (var n in newNodes) parent.InsertBefore(n, oldNodes[0]);
            foreach (var o in oldNodes) parent.RemoveChild(o);
        }
        public void InsertBlocks(string op, XmlNode parent, int index, IList<XmlNode> newNodes)
        {
            for (var i = 0; i < newNodes.Count; i++) parent.InsertBefore(newNodes[i], parent.ChildNodes[index + i]);
        }
        public bool SelectText(XmlText run, int start, int length) { return true; }
        public void Edit(string op, Action body) { body(); }
        public bool ReplaceSpan(XmlText run, int start, int length, string replacement)
        {
            run.Value = run.Value.Substring(0, start) + replacement + run.Value.Substring(start + length);
            return true;
        }
        public string VariableValue(string name) { string v; return _vars.TryGetValue(name, out v) ? v : null; }
    }

    internal sealed class Defect
    {
        public string Id, File, Author, Kind, Source, Rule, Excerpt, Sentence;
        public bool ByLint, ByAi, ByStructure;
        public bool Caught { get { return ByLint || ByAi || ByStructure; } }
    }

    internal sealed class Found
    {
        public string File, Author, Origin, Rule, RuleId, Excerpt, Replacement, Kind, Severity, Why;
        public Defect Match;
        public string Decision;
    }

    /// <summary>How a simulated author decides: accept real problems, reject noise, and their own pet disputes.</summary>
    internal sealed class Persona
    {
        public double AcceptTrue, AcceptUnseeded, RejectGeneral;
        public string[] Disputes = new string[0];

        public static readonly Dictionary<string, Persona> All = new Dictionary<string, Persona>
        {
            { "priya",  new Persona { AcceptTrue = 0.95, AcceptUnseeded = 0.15 } },
            { "marcus", new Persona { AcceptTrue = 0.85, AcceptUnseeded = 0.40, Disputes = new[] { "claims", "coming soon", "free of charge", "guaranteed", "best-in-class" } } },
            { "dana",   new Persona { AcceptTrue = 0.75, AcceptUnseeded = 0.30, Disputes = new[] { "bank" } } },
            { "tom",    new Persona { AcceptTrue = 0.98, AcceptUnseeded = 0.70 } },
            { "lee",    new Persona { AcceptTrue = 0.90, AcceptUnseeded = 0.20, RejectGeneral = 0.8 } },
        };
    }

    internal sealed class Bench
    {
        private readonly Options _o;
        private readonly string _root, _project;
        private readonly List<Defect> _defects = new List<Defect>();
        private readonly Dictionary<string, string> _authorOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Found> _found = new List<Found>();
        private readonly List<string> _errors = new List<string>();
        private readonly List<double> _topicSeconds = new List<double>();
        private int _drops, _breaches, _truncations;

        public Bench(Options o)
        {
            _o = o;
            _root = Path.GetFullPath(o.Corpus);
            // The Flare project under --corpus; without a ground-truth.json every finding is listed as "not seeded" (lint any project).
            // Shallowest project first (corpus/DigitalBanking before corpus/v2/*), then shortest path.
            _project = Path.GetDirectoryName(Directory.GetFiles(_root, "*.flprj", SearchOption.AllDirectories)
                .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(f => f.Length).First());
            var truthFile = Path.Combine(_root, "ground-truth.json");
            var truth = File.Exists(truthFile) ? JObject.Parse(File.ReadAllText(truthFile))
                : new JObject { ["files"] = new JObject(Directory.GetFiles(Path.Combine(_project, "Content"), "*.htm", SearchOption.AllDirectories)
                      .Select(f => new JProperty(f.Substring(_project.Length + 1).Replace(Path.DirectorySeparatorChar, '/'), "writer"))), ["defects"] = new JArray() };
            foreach (var p in ((JObject)truth["files"]).Properties()) _authorOf[p.Name] = (string)p.Value;
            foreach (var d in truth["defects"])
                _defects.Add(new Defect { Id = (string)d["id"], File = (string)d["file"], Author = (string)d["author"], Kind = (string)d["kind"], Source = (string)d["source"], Rule = (string)d["rule"], Excerpt = (string)d["excerpt"], Sentence = (string)d["sentence"] });
        }

        public int Run()
        {
            Directory.CreateDirectory(_o.Out);
            var log = new StreamWriter(Path.Combine(_o.Out, "diag.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            Diagnostics.Sink = line =>
            {
                lock (log) log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);
                if (line.Contains("dropped")) Interlocked.Add(ref _drops, Count(line, "dropped "));
                if (line.Contains("contract breaches")) Interlocked.Increment(ref _breaches);
                if (line.Contains("cut off at")) Interlocked.Increment(ref _truncations);
            };
            Usage.Reset();

            var repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
            var guide = _o.Guide != null ? Path.GetFullPath(_o.Guide) : Path.Combine(repo, "styleguide");
            var style = new StyleEngine(new[] { guide }, guide);
            ILlmClient llm = null;
            if (!_o.NoAi)
            {
                var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? DotEnv(repo, "ANTHROPIC_API_KEY");
                llm = _o.Gateway != null
                    ? (ILlmClient)new BifrostClient(_o.Gateway, Environment.GetEnvironmentVariable("BIFROST_VK"), _o.Model, 300)
                    : new AnthropicClient(null, key, _o.Model, 300);
            }
            var routing = new Routing(null, _o.Model, null) { ReviewUnit = _o.Unit };
            routing.For("review").Effort = _o.Effort;
            var vars = Variables();

            if (_o.Examples > 0) return GuideExamples(llm, style, routing);
            var files = _authorOf.Keys.OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (_o.Sample > 0)
            {
                // Same sample for every config: every k-th topic of each author.
                files = files.GroupBy(f => _authorOf[f]).SelectMany(g => { var l = g.ToList(); var step = Math.Max(1, l.Count * _authorOf.Values.Distinct().Count() / _o.Sample); return l.Where((x, i) => i % step == 0); }).OrderBy(f => f).ToList();
            }
            var inScope = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine("Bench: " + files.Count + " topics, " + (_o.NoAi ? "lint only" : _o.Model + "/" + _o.Effort + "/" + _o.Unit) + " -> " + _o.Out);

            var snippetSeen = new HashSet<string>();
            var done = 0;
            var all = Stopwatch.StartNew();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = _o.Parallel }, rel =>
            {
                var sw = Stopwatch.StartNew();
                var path = Path.Combine(_project, rel.Replace('/', '\\'));
                try
                {
                    var doc = new FileDoc(path, vars);
                    var runner = new ActionRunner(llm, PromptBuilder.For(style, path), 16000, style, path, routing) { Parallelism = 6, VariableValue = doc.VariableValue };
                    var blocks = runner.PickCheckBlocks(doc);
                    var lint = runner.Lint(blocks);
                    var snippets = runner.LintSnippets(blocks, path);
                    var structure = StructureCheck.Of(blocks);
                    var ai = llm == null ? new List<StyleFinding>() : runner.Review(blocks, lint);
                    lock (_found)
                    {
                        foreach (var f in lint) Add(rel, "lint", f);
                        foreach (var f in snippets)
                            if (snippetSeen.Add(f.DocumentPath + "|" + f.Rule + "|" + f.Excerpt)) Add(Rel(f.DocumentPath), "lint", f);
                        foreach (var f in structure) Add(rel, "structure", f);
                        foreach (var f in ai) Add(rel, "ai", f);
                    }
                }
                catch (Exception ex)
                {
                    lock (_errors) _errors.Add(rel + ": " + ex.GetType().Name + ": " + ex.Message);
                    Diagnostics.Log("bench " + rel + " :: " + ex);
                }
                lock (_topicSeconds) _topicSeconds.Add(sw.Elapsed.TotalSeconds);
                var n = Interlocked.Increment(ref done);
                if (n % 10 == 0 || n == files.Count) Console.WriteLine("  " + n + "/" + files.Count + "  " + all.Elapsed.ToString(@"mm\:ss") + "  $" + Usage.Snapshot().Sum(u => u.Usd).ToString("0.00"));
            });

            var scope = _defects.Where(d => inScope.Contains(d.File) || d.File.Contains("/Snippets/")).ToList();
            Score(scope);
            var tune = Decide();
            var rewrites = _o.Rewrites > 0 && llm != null ? Rewrites(scope, vars, llm, style, routing) : new List<JObject>();
            Write(scope, files.Count, all.Elapsed.TotalSeconds, tune, rewrites);
            Diagnostics.Sink = null;
            log.Dispose();
            Console.WriteLine(File.ReadAllText(Path.Combine(_o.Out, "report.md")).Split(new[] { "\n## " }, StringSplitOptions.None)[0]);
            return ScoreLabels(repo, vars, _o.Sample > 0 ? inScope : null, files.Count);
        }

        // ---- label scoring, baseline and gate ----------------------------------------------------------------------

        private int ScoreLabels(string repo, Dictionary<string, string> vars, ISet<string> onlyFiles, int topics)
        {
            var paths = _o.Labels.Count > 0 ? _o.Labels.Select(Path.GetFullPath).ToList()
                : new[] { Path.Combine(_project, "labels.jsonl"), Path.Combine(_root, "labels.jsonl") }.Where(File.Exists).Distinct().ToList();
            if (paths.Count == 0)
            {
                if (_o.Check || _o.UpdateBaseline) { Console.Error.WriteLine("No labels.jsonl under " + _root + "; pass --labels."); return 2; }
                return 0;
            }
            var labels = LabelScoring.LoadLabels(paths, onlyFiles);
            var findings = new List<ScoreFinding>();
            var n = 0;
            foreach (var f in _found)   // same property names as the v1 findings.jsonl; FindingIO maps Structure.X / Project.X to rule ids
                findings.Add(FindingIO.FromJson(new JObject { ["file"] = f.File, ["origin"] = f.Origin, ["rule"] = f.Rule, ["ruleId"] = f.RuleId, ["excerpt"] = f.Excerpt, ["kind"] = f.Kind }, ++n));
            var reports = LabelScoring.ScoreAll(labels, findings, new CorpusBlockTextSource(_project, name => { string v; return vars.TryGetValue(name ?? "", out v) ? v : null; }));
            var doc = LabelScoring.BaselineJson(reports, Path.GetFileName(_project), topics);
            File.WriteAllText(Path.Combine(_o.Out, "score.json"), doc.ToString(), new UTF8Encoding(false));
            File.WriteAllLines(Path.Combine(_o.Out, "scored-findings.jsonl"), reports["all"].Findings.Select(r => JsonConvert.SerializeObject(new
            {
                id = r.Finding.Id, file = r.Finding.File, rule = r.Finding.RuleId, origin = r.Finding.Origin, excerpt = r.Finding.Excerpt,
                outcome = r.Outcome.ToString(), label = r.Label == null ? null : r.Label.Id, reason = r.Reason
            })), new UTF8Encoding(false));
            File.AppendAllText(Path.Combine(_o.Out, "report.md"), LabelScoring.ReportSection(reports), new UTF8Encoding(false));
            var t = reports["all"].Overall;
            Console.WriteLine("Labels: " + labels.Count + " (" + paths.Count + " file(s)). Findings TP " + t.Tp + ", FP " + t.Fp + ", wrong rule " + t.WrongRule + ", unscored " + t.Unscored +
                "; violations " + t.Support + ", detected " + t.Detected + "; precision " + t.Precision.ToString("P1") + ", recall " + t.Recall.ToString("P1") + ", coverage " + reports["all"].Coverage.ToString("P1"));
            foreach (var kv in reports["all"].By[ScoreReport.Layer].Where(k => k.Key == "google" || k.Key == "company" || k.Key == "project" || k.Key == "structure"))
                Console.WriteLine("  " + kv.Key.PadRight(10) + " TP " + kv.Value.Tp + "/" + kv.Value.ScoredFindings + " scored (precision " + kv.Value.Precision.ToString("P1") + "), detected " + kv.Value.Detected + "/" + kv.Value.Support);

            var baseline = _o.Baseline ?? Path.Combine(repo, "tests", "baselines", "bench-lint.json");
            if (_o.UpdateBaseline)
            {
                var old = File.Exists(baseline) ? JObject.Parse(File.ReadAllText(baseline)) : null;
                Directory.CreateDirectory(Path.GetDirectoryName(baseline));
                File.WriteAllText(baseline, doc.ToString() + "\n", new UTF8Encoding(false));
                Console.WriteLine("Baseline written: " + baseline);
                if (old != null)
                {
                    var changed = LabelScoring.ChangedRules(old, doc);
                    Console.WriteLine(changed.Count == 0 ? "No rule rows changed." : "Rule rows changed (list each in the PR body):\n  " + string.Join("\n  ", changed));
                }
                return 0;
            }
            if (_o.Check)
            {
                if (!File.Exists(baseline)) { Console.Error.WriteLine("No baseline at " + baseline + "; create it with --update-baseline."); return 2; }
                var gate = LabelScoring.Compare(JObject.Parse(File.ReadAllText(baseline)), doc, _o.GateSplit);
                var text = LabelScoring.GateText(gate, baseline.Replace(repo + Path.DirectorySeparatorChar, ""), _o.GateSplit);
                File.WriteAllText(Path.Combine(_o.Out, "delta.md"), "```\n" + text + "```\n", new UTF8Encoding(false));
                Console.WriteLine(text);
                return gate.Passed ? 0 : 1;
            }
            return 0;
        }

        // ---- Google's own examples ----------------------------------------------------------------------------

        /// <summary>
        /// Does the reviewer apply the whole Google guide? Takes N prose rules with a Bad example from the compiled Google
        /// layer (spread across pages), reviews each Bad and Good sentence with the guide under test (--guide), and counts:
        /// Bad flagged (by lint or AI), Bad flagged citing that exact rule, Good left clean.
        /// </summary>
        private int GuideExamples(ILlmClient llm, StyleEngine style, Routing routing)
        {
            var source = new StyleEngine(new[] { Path.GetFullPath(_o.ExamplesFrom ?? Path.Combine(_root, @"..\styleguide")) }, Path.GetFullPath(_o.ExamplesFrom ?? Path.Combine(_root, @"..\styleguide")));
            var all = source.Rules(null).Where(r => r.Layer == "google" && !r.IsLint && r.Bad.Length > 0 && r.Products.Count == 0).ToList();
            var byPage = all.GroupBy(r => r.Id.Split('/')[0]).ToList();
            var picks = new List<GuideRule>();
            for (var round = 0; picks.Count < _o.Examples && round < 50; round++)   // round-robin over pages: every page gets a say
                foreach (var g in byPage) { var l = g.ToList(); if (round < l.Count && picks.Count < _o.Examples) picks.Add(l[(round * 7) % l.Count]); }
            Console.WriteLine("Examples: " + picks.Count + " Google rules from " + picks.Select(p => p.Id.Split('/')[0]).Distinct().Count() + " pages, guide " + (_o.Guide ?? "styleguide"));
            var rows = new List<JObject>();
            Parallel.ForEach(picks, new ParallelOptions { MaxDegreeOfParallelism = _o.Parallel * 2 }, rule =>
            {
                var r = new JObject { ["rule"] = rule.Id, ["text"] = rule.Text, ["bad"] = rule.Bad, ["good"] = rule.Good };
                try
                {
                    var bad = Review(rule, rule.Bad, llm, style, routing);
                    r["bad_flagged"] = bad.Count > 0;
                    r["bad_cited"] = bad.Any(f => string.Equals(f.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase));
                    r["bad_findings"] = new JArray(bad.Select(f => (f.RuleId ?? f.Rule) + ": " + f.Excerpt).ToArray());
                    if (rule.Good.Length > 0)
                    {
                        var good = Review(rule, rule.Good, llm, style, routing);
                        r["good_clean"] = good.Count == 0;
                        r["good_findings"] = new JArray(good.Select(f => (f.RuleId ?? f.Rule) + ": " + f.Excerpt).ToArray());
                    }
                }
                catch (Exception ex) { r["error"] = ex.Message; }
                lock (rows) rows.Add(r);
            });
            Directory.CreateDirectory(_o.Out);
            int n = rows.Count, flagged = rows.Count(x => (bool?)x["bad_flagged"] == true), cited = rows.Count(x => (bool?)x["bad_cited"] == true);
            var withGood = rows.Where(x => x["good_clean"] != null).ToList();
            var clean = withGood.Count(x => (bool)x["good_clean"]);
            var usd = Usage.Snapshot().Sum(u => u.Usd);
            var summary = string.Format("{0} Google rules: Bad example flagged {1:P0}, flagged citing that rule {2:P0}; Good example left clean {3:P0} (of {4}). Errors {5}. ${6:0.00}.",
                n, (double)flagged / n, (double)cited / n, withGood.Count == 0 ? 0 : (double)clean / withGood.Count, withGood.Count, rows.Count(x => x["error"] != null), usd);
            File.WriteAllText(Path.Combine(_o.Out, "examples.json"), new JArray(rows.OrderBy(x => (string)x["rule"])).ToString());
            File.WriteAllText(Path.Combine(_o.Out, "metrics.json"), new JObject { ["examples"] = n, ["bad_flagged"] = Ratio(flagged, n), ["bad_cited"] = Ratio(cited, n), ["good_clean"] = Ratio(clean, withGood.Count), ["usd"] = Math.Round(usd, 4) }.ToString());
            Console.WriteLine(summary);
            return 0;
        }

        /// <summary>Lint + AI review of one sentence, as if it sat in a section with the content the rule is about.</summary>
        private List<StyleFinding> Review(GuideRule rule, string text, ILlmClient llm, StyleEngine style, Routing routing)
        {
            var doc = new XmlDocument();
            doc.LoadXml("<html><body><p>" + System.Security.SecurityElement.Escape(text) + "</p></body></html>");
            var blocks = new List<XmlNode> { doc.DocumentElement["body"].FirstChild };
            var runner = new ActionRunner(llm, PromptBuilder.For(style, null), 16000, style, null, routing) { Parallelism = 1 };
            var lint = runner.Lint(blocks);
            var units = runner.PrepareReview(blocks, lint, 1);
            var found = new List<StyleFinding>(lint);
            if (units.Count > 0)
            {
                units[0].Scope.Content.UnionWith(rule.Applies);
                found.AddRange(runner.AnchorReview(runner.CallReview(units[0]), units[0], lint));
            }
            return found;
        }

        private void Add(string rel, string origin, StyleFinding f)
        {
            _found.Add(new Found
            {
                File = rel, Author = _authorOf.ContainsKey(rel) ? _authorOf[rel] : "shared", Origin = origin, Rule = f.Rule, RuleId = f.RuleId,
                Excerpt = f.Excerpt, Replacement = f.Replacement, Kind = f.Kind, Severity = f.Severity, Why = f.Suggestion
            });
        }

        private string Rel(string path)
        {
            return path.Substring(_project.Length + 1).Replace('\\', '/');
        }

        // ---- scoring ------------------------------------------------------------------------------------------

        private static string Norm(string s) { return System.Text.RegularExpressions.Regex.Replace((s ?? "").ToLowerInvariant(), @"\s+", " ").Trim(); }

        private static bool Matches(Found f, Defect d)
        {
            if (!string.Equals(f.File, d.File, StringComparison.OrdinalIgnoreCase)) return false;
            if (d.Source == "structure" && f.Origin == "structure") return f.Rule == d.Rule && (d.Excerpt == null || Norm(f.Excerpt).Contains(Norm(d.Excerpt)) || Norm(d.Excerpt).Contains(Norm(f.Excerpt)));
            if (d.Sentence == null) return false;
            string fe = Norm(f.Excerpt), de = Norm(d.Excerpt), se = Norm(d.Sentence);
            if (fe.Length == 0) return false;
            return fe.Contains(de) || (de.Contains(fe) && (fe.Length >= 2 || !char.IsLetterOrDigit(fe[0]))) || (fe.Length >= 6 && se.Contains(fe));
        }

        private void Score(List<Defect> scope)
        {
            foreach (var f in _found)
            {
                // Several defects can share a paragraph: credit the one whose own excerpt the finding covers.
                var d = scope.Where(x => Matches(f, x)).OrderByDescending(x => Norm(f.Excerpt).Contains(Norm(x.Excerpt)) ? (x.Excerpt ?? "").Length : -1).FirstOrDefault();
                if (d == null) continue;
                f.Match = d;
                if (f.Origin == "lint") d.ByLint = true; else if (f.Origin == "ai") d.ByAi = true; else d.ByStructure = true;
            }
        }

        // ---- simulated authors ----------------------------------------------------------------------------------

        private List<TuneItem> Decide()
        {
            var rnd = new Random(7);
            var dir = Path.Combine(_o.Out, "feedback");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var start = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
            var i = 0;
            foreach (var f in _found.Where(x => x.Origin != "structure" && Persona.All.ContainsKey(x.Author)).OrderBy(x => x.File).ThenBy(x => x.Excerpt))
            {
                var p = Persona.All[f.Author];
                var about = Norm(f.Rule + " " + f.RuleId + " " + f.Excerpt);
                bool accept;
                if (p.Disputes.Any(about.Contains)) accept = false;
                else if (f.Origin == "ai" && f.RuleId == null && rnd.NextDouble() < p.RejectGeneral) accept = false;
                else accept = rnd.NextDouble() < (f.Match != null ? p.AcceptTrue : p.AcceptUnseeded);
                f.Decision = accept ? "accepted" : "rejected";
                var line = JsonConvert.SerializeObject(new
                {
                    at = start.AddMinutes(i++).ToString("o"), writer = f.Author, decision = f.Decision, reason = (string)null, kind = f.Kind, source = f.Origin,
                    layer = f.Origin == "ai" ? "ai" : (f.RuleId ?? "").Split(':')[0], rule = f.Rule, rule_id = f.RuleId, excerpt = f.Excerpt, replacement = f.Replacement, file = f.File
                }, Newtonsoft.Json.Formatting.None);
                File.AppendAllText(Path.Combine(dir, f.Author + ".jsonl"), line + "\n", new UTF8Encoding(false));
            }
            return FeedbackStats.Analyze(FeedbackStats.Load(FeedbackStats.FilesIn(dir)));
        }

        // ---- rewrites -------------------------------------------------------------------------------------------

        private List<JObject> Rewrites(List<Defect> scope, Dictionary<string, string> vars, ILlmClient llm, StyleEngine style, Routing routing)
        {
            var results = new List<JObject>();
            var picks = scope.Where(d => d.Sentence != null && !d.File.Contains("/Snippets/")).GroupBy(d => d.Kind).Select(g => g.First()).Take(_o.Rewrites).ToList();
            Parallel.ForEach(picks, new ParallelOptions { MaxDegreeOfParallelism = _o.Parallel }, d =>
            {
                var r = new JObject { ["defect"] = d.Id, ["kind"] = d.Kind, ["excerpt"] = d.Excerpt };
                try
                {
                    var path = Path.Combine(_project, d.File.Replace('/', '\\'));
                    var doc = new FileDoc(path, vars);
                    var runner = new ActionRunner(llm, PromptBuilder.For(style, path), 16000, style, path, routing) { Parallelism = 6, VariableValue = doc.VariableValue };
                    var block = TextRuns.Of(new List<XmlNode> { BlockSelector.FindBody(doc.Document) }).Select(TextRuns.BlockOf)
                        .First(b => b != null && Norm(b.InnerText).Contains(Norm(d.Sentence).Substring(0, Math.Min(20, d.Sentence.Length))));
                    var before = block.InnerText;
                    var p = runner.Rewrite(doc, new List<XmlNode> { block }, null, true);
                    r["valid"] = p.Validation.IsValid;
                    r["needs_restructure"] = p.NeedsRestructure;
                    r["word_edits"] = p.WordPlan == null ? -1 : p.WordPlan.Count;
                    r["remaining_lint"] = p.RemainingLint.Count;
                    r["applied"] = runner.Apply(doc, p, "bench");
                    var after = block.ParentNode != null ? block.InnerText : string.Concat(p.ProposedBlocks.Select(b => b.InnerText));
                    r["before"] = before;
                    r["after"] = after;
                    r["fixed"] = after != null && after.IndexOf(d.Excerpt, StringComparison.Ordinal) < 0;
                    r["well_formed"] = Reparses(doc.Document);
                }
                catch (Exception ex) { r["error"] = ex.GetType().Name + ": " + ex.Message; }
                lock (results) results.Add(r);
            });
            return results;
        }

        private static bool Reparses(XmlDocument doc)
        {
            try { new XmlDocument().LoadXml(doc.OuterXml); return true; } catch (XmlException) { return false; }
        }

        // ---- output -----------------------------------------------------------------------------------------------

        private void Write(List<Defect> scope, int topics, double seconds, List<TuneItem> tune, List<JObject> rewrites)
        {
            var usage = Usage.Snapshot();
            var usd = usage.Sum(u => u.Usd);
            var ai = _found.Where(f => f.Origin == "ai").ToList();
            var aiMatched = ai.Where(f => f.Match != null).ToList();
            var cited = aiMatched.Where(f => f.Match.Rule.Contains(":") && f.Match.Rule != "general").ToList();
            var citedOk = cited.Count(f => f.RuleId != null && (f.RuleId.StartsWith(f.Match.Rule + "/", StringComparison.OrdinalIgnoreCase) || (f.Match.Rule == "google" && f.RuleId.StartsWith("google:"))));
            var aiKinds = scope.Where(d => d.Source == "ai").ToList();
            var sorted = _topicSeconds.OrderBy(x => x).ToList();

            var metrics = new JObject
            {
                ["model"] = _o.NoAi ? "none" : _o.Model, ["effort"] = _o.NoAi ? "-" : _o.Effort, ["unit"] = _o.Unit, ["topics"] = topics, ["defects"] = scope.Count,
                ["recall"] = Ratio(scope.Count(d => d.Caught), scope.Count),
                ["recall_lint_kinds"] = Ratio(scope.Count(d => d.Source == "lint" && d.Caught), scope.Count(d => d.Source == "lint")),
                ["recall_structure_kinds"] = Ratio(scope.Count(d => d.Source == "structure" && d.Caught), scope.Count(d => d.Source == "structure")),
                ["recall_ai_kinds"] = Ratio(aiKinds.Count(d => d.Caught), aiKinds.Count),
                ["ai_findings"] = ai.Count, ["unseeded_ai"] = ai.Count(f => f.Match == null), ["unseeded_lint"] = _found.Count(f => f.Origin == "lint" && f.Match == null),
                ["citation_accuracy"] = Ratio(citedOk, cited.Count), ["contract_drops"] = _drops, ["contract_retries"] = _breaches, ["truncations"] = _truncations,
                ["errors"] = _errors.Count, ["usd"] = Math.Round(usd, 4), ["seconds"] = Math.Round(seconds, 1),
                ["seconds_per_topic"] = sorted.Count == 0 ? 0 : Math.Round(sorted.Average(), 2), ["p95_topic_seconds"] = sorted.Count == 0 ? 0 : Math.Round(sorted[(int)(sorted.Count * 0.95)], 2),
                ["rewrites"] = rewrites.Count, ["rewrites_fixed"] = rewrites.Count(r => (bool?)r["fixed"] == true), ["rewrites_errors"] = rewrites.Count(r => r["error"] != null)
            };
            File.WriteAllText(Path.Combine(_o.Out, "metrics.json"), metrics.ToString());
            File.WriteAllLines(Path.Combine(_o.Out, "findings.jsonl"), _found.Select(f => JsonConvert.SerializeObject(new { f.File, f.Author, f.Origin, f.Rule, f.RuleId, f.Excerpt, f.Replacement, f.Severity, f.Why, match = f.Match == null ? null : f.Match.Id, f.Decision })));

            var sb = new StringBuilder();
            sb.AppendLine("# Bench: " + (_o.NoAi ? "lint + structure only" : _o.Model + ", effort " + _o.Effort + ", per " + _o.Unit));
            sb.AppendLine();
            sb.AppendFormat("{0} topics, {1} seeded defects. Caught {2:P0} (lint-type {3:P0}, structure {4:P0}, judgment {5:P0}). {6} AI findings, {7} not seeded. Citation accuracy {8:P0}. " +
                            "{9} errors, {10} contract drops, {11} retries, {12} truncations. ${13:0.00}, {14:0.0} s/topic (p95 {15:0.0} s), {16:0} s total.\n",
                topics, scope.Count, (double)metrics["recall"], (double)metrics["recall_lint_kinds"], (double)metrics["recall_structure_kinds"], (double)metrics["recall_ai_kinds"],
                ai.Count, (int)metrics["unseeded_ai"], (double)metrics["citation_accuracy"], _errors.Count, _drops, _breaches, _truncations, usd, (double)metrics["seconds_per_topic"], (double)metrics["p95_topic_seconds"], seconds);

            sb.AppendLine("\n## Recall by defect kind\n\n| Kind | Expected from | Seeded | Lint | Structure | AI | Missed |\n|---|---|---|---|---|---|---|");
            foreach (var g in scope.GroupBy(d => d.Kind).OrderBy(g => g.First().Source).ThenBy(g => g.Key))
                sb.AppendFormat("| {0} | {1} | {2} | {3} | {4} | {5} | {6} |\n", g.Key, g.First().Source, g.Count(), g.Count(d => d.ByLint), g.Count(d => d.ByStructure), g.Count(d => d.ByAi), g.Count(d => !d.Caught));

            sb.AppendLine("\n## Missed (first 25)\n");
            foreach (var d in scope.Where(d => !d.Caught).Take(25)) sb.AppendLine("- " + d.Kind + " · " + d.File + " · \"" + (d.Excerpt ?? d.Rule) + "\"");

            sb.AppendLine("\n## Not seeded: AI (by rule)\n\nRead these: some are real problems in the template text, some are noise.\n");
            foreach (var g in ai.Where(f => f.Match == null).GroupBy(f => f.RuleId ?? "general: " + f.Rule).OrderByDescending(g => g.Count()).Take(15))
                sb.AppendLine("- " + g.Count() + " × " + g.Key + " — e.g. \"" + g.First().Excerpt + "\" (" + g.First().File + "): " + g.First().Why);
            sb.AppendLine("\n## Not seeded: lint (by rule)\n");
            foreach (var g in _found.Where(f => f.Origin == "lint" && f.Match == null).GroupBy(f => f.RuleId ?? f.Rule).OrderByDescending(g => g.Count()).Take(15))
                sb.AppendLine("- " + g.Count() + " × " + g.Key + " — e.g. \"" + g.First().Excerpt + "\" (" + g.First().File + ")");

            sb.AppendLine("\n## Citations (AI findings on seeded defects with a known rule)\n");
            foreach (var f in cited.Where(f => !(f.RuleId != null && (f.RuleId.StartsWith(f.Match.Rule + "/", StringComparison.OrdinalIgnoreCase) || (f.Match.Rule == "google" && f.RuleId.StartsWith("google:"))))).Take(15))
                sb.AppendLine("- " + f.Match.Kind + ": expected " + f.Match.Rule + ", cited " + (f.RuleId ?? "nothing") + " — \"" + f.Excerpt + "\"");

            sb.AppendLine("\n## Simulated authors → Tune report\n\n| Author | Shown | Accepted | Rejected |\n|---|---|---|---|");
            foreach (var g in _found.Where(f => f.Decision != null).GroupBy(f => f.Author).OrderBy(g => g.Key))
                sb.AppendFormat("| {0} | {1} | {2} | {3} |\n", g.Key, g.Count(), g.Count(f => f.Decision == "accepted"), g.Count(f => f.Decision == "rejected"));
            sb.AppendLine();
            foreach (var t in tune) sb.AppendLine("- **" + t.Kind + "** " + t.Title + " (" + t.Rule + "): " + t.Accepted + " accepted / " + t.Rejected + " rejected by " + string.Join(", ", t.Writers ?? new List<string>()));
            if (tune.Count == 0) sb.AppendLine("(nothing raised)");

            if (rewrites.Count > 0)
            {
                sb.AppendLine("\n## Rewrites (one block per defect kind)\n\n| Kind | Valid | Restructure? | Word edits | Fixed | Lint left | Result |\n|---|---|---|---|---|---|---|");
                foreach (var r in rewrites.OrderBy(r => (string)r["kind"]))
                    sb.AppendFormat("| {0} | {1} | {2} | {3} | {4} | {5} | {6} |\n", r["kind"], r["valid"], r["needs_restructure"], r["word_edits"], r["fixed"], r["remaining_lint"], ((string)r["error"] ?? (string)r["applied"] ?? "").Replace("|", "/"));
                sb.AppendLine();
                foreach (var r in rewrites.Where(r => r["after"] != null).OrderBy(r => (string)r["kind"]))
                    sb.AppendLine("- " + r["kind"] + ": \"" + r["before"] + "\" → \"" + r["after"] + "\"");
            }

            sb.AppendLine("\n## Cost\n\n| Task | Model | Calls | Input | Cached | Output | $ |\n|---|---|---|---|---|---|---|");
            foreach (var u in usage) sb.AppendFormat("| {0} | {1} | {2} | {3} | {4} | {5} | {6:0.000} |\n", u.Task, u.Model, u.Calls, u.Input, u.CacheRead, u.Output, u.Usd);

            if (_errors.Count > 0) { sb.AppendLine("\n## Errors\n"); foreach (var e in _errors.Take(30)) sb.AppendLine("- " + e); }
            File.WriteAllText(Path.Combine(_o.Out, "report.md"), sb.ToString(), new UTF8Encoding(false));
        }

        private static double Ratio(int a, int b) { return b == 0 ? 0 : Math.Round((double)a / b, 4); }

        private static int Count(string line, string after)
        {
            var i = line.IndexOf(after, StringComparison.Ordinal);
            int n;
            return i >= 0 && int.TryParse(new string(line.Substring(i + after.Length).TakeWhile(char.IsDigit).ToArray()), out n) ? n : 0;
        }

        private Dictionary<string, string> Variables()
        {
            var vars = new Dictionary<string, string>();
            foreach (var f in Directory.GetFiles(Path.Combine(_project, @"Project\VariableSets"), "*.flvar"))
            {
                var x = new XmlDocument();
                x.Load(f);
                foreach (XmlElement v in x.GetElementsByTagName("Variable")) vars[Path.GetFileNameWithoutExtension(f) + "." + v.GetAttribute("Name")] = v.InnerText;
            }
            return vars;
        }

        private static string DotEnv(string repo, string name)
        {
            var path = Path.Combine(repo, ".env");
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadAllLines(path))
                if (line.StartsWith(name + "=")) return line.Substring(name.Length + 1).Trim().Trim('"');
            return null;
        }
    }
}
