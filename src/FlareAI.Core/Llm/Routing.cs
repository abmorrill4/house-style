using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Llm
{
    /// <summary>A model + effort for one kind of task.</summary>
    public sealed class Route
    {
        public string Tier { get; set; }
        public string Model { get; set; }
        public string Effort { get; set; }
        public override string ToString() { return Tier + " (" + Model + ", " + Effort + ")"; }
    }

    /// <summary>
    /// Which model and effort each AI task uses. Tasks map to three tiers (fast / standard / deep); tiers map to a model
    /// and effort. Defaults are built in; Settings pick the tier models; a style guide can pin both for the whole team in
    /// FlareAI\models.json:
    /// <code>{ "tiers": { "fast": { "model": "claude-haiku-5-5", "effort": "low" } }, "tasks": { "review": "fast" }, "review_unit": "section" }</code>
    /// </summary>
    public sealed class Routing
    {
        public const string Fast = "fast", Standard = "standard", Deep = "deep";

        /// <summary>Every task the plugin sends to a model, and its default tier.</summary>
        public static readonly Dictionary<string, string> DefaultTasks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "review", Standard },        // per-block style review: judgment + exact citations
            { "rewrite", Standard },       // run-level edits for a block
            { "restructure", Standard },   // XHTML edits that change structure (validated)
            { "refine", Fast },            // one proposal, re-asked
            { "alt", Standard },           // vision: UI labels must be exact
            { "assist", Fast },            // description, keywords, synonyms, micro content, glossary
            { "extract", Standard },       // rules from notes
            { "answer", Standard },        // a question about the text, answered in words
            { "draft", Deep },             // owner-only edits to the shared style guide
        };

        /// <summary>
        /// How long each task's system prompt stays cached (see <see cref="LlmRequest.CacheTtl"/>). Review and rewrite run
        /// all day, minutes apart: an hour. They share one cache entry (same system prompt, same schema: Contracts.EditSchema),
        /// and restructure has the same prefix, so it reads that entry instead of writing its own. Draft (Opus), extract and
        /// refine (Haiku) have their own model or schema and run in short bursts: five minutes. Alt text and topic assists
        /// don't carry the style guide at all, so there is nothing worth caching. Team: "cache": { "task": "5m" }.
        /// </summary>
        public static readonly Dictionary<string, string> DefaultCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "review", "1h" }, { "rewrite", "1h" }, { "restructure", "1h" },
            { "draft", "5m" }, { "extract", "5m" }, { "refine", "5m" },
            { "alt", "none" }, { "assist", "none" }, { "answer", "5m" },
        };

        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(DefaultCache, StringComparer.OrdinalIgnoreCase);

        /// <summary>The cache lifetime for a task's system prompt: "1h", "5m" or "none" (unknown tasks: "5m").</summary>
        public string CacheFor(string task)
        {
            string ttl;
            return task != null && _cache.TryGetValue(task, out ttl) ? ttl : "5m";
        }

        private readonly Dictionary<string, Route> _tiers = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _tasks = new Dictionary<string, string>(DefaultTasks, StringComparer.OrdinalIgnoreCase);

        /// <summary>How the review is split: "section" (the default: a short topic whole, a long one by headings) or "block" (one call per paragraph, item or row).</summary>
        public string ReviewUnit { get; set; }
        /// <summary>
        /// Rule packs sent with every call, in the long-lived cached block, because most sections need them (measured on
        /// 262 sections: Headings 100%, UI 90%, Lists 59%, Steps 58%; every other pack 22% or less). Team: "always_packs".
        /// </summary>
        public List<string> AlwaysPacks { get; set; }
        /// <summary>Where the routing came from, for Diagnostics ("built-in", "settings", "team: …\models.json").</summary>
        public string Source { get; private set; }

        public Routing(string fastModel = null, string standardModel = null, string deepModel = null)
        {
            _tiers[Fast] = new Route { Tier = Fast, Model = fastModel ?? "claude-haiku-5-5", Effort = "medium" };
            _tiers[Standard] = new Route { Tier = Standard, Model = standardModel ?? "claude-sonnet-5-5", Effort = "medium" };
            _tiers[Deep] = new Route { Tier = Deep, Model = deepModel ?? "claude-opus-5-5", Effort = "high" };
            ReviewUnit = "section";
            AlwaysPacks = new List<string> { "Headings", "UI", "Lists", "Steps" };
            Source = fastModel == null && standardModel == null && deepModel == null ? "built-in" : "settings";
        }

        public Route For(string task)
        {
            string tier;
            if (task == null || !_tasks.TryGetValue(task, out tier)) tier = Standard;
            return _tiers[tier];
        }

        /// <summary>The effort values a team file may set. Anything else is ignored, so a shared file can't send a value the API doesn't expect.</summary>
        public static readonly string[] Efforts = { "low", "medium", "high", "xhigh", "max" };

        /// <summary>A model name as the API spells it: letters, digits and . _ : - only, up to 100 characters.</summary>
        public static bool IsModelName(string m) { return !string.IsNullOrEmpty(m) && m.Length <= 100 && System.Text.RegularExpressions.Regex.IsMatch(m, @"^[A-Za-z0-9][A-Za-z0-9._:\-]*$"); }

        /// <summary>Applies the team's FlareAI\models.json from the style guide, if present (team settings win).</summary>
        public Routing WithTeamFile(string guideRoot)
        {
            if (string.IsNullOrEmpty(guideRoot)) return this;
            var path = Path.Combine(guideRoot, Path.Combine("FlareAI", "models.json"));
            if (!File.Exists(path)) return this;
            try
            {
                var o = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                foreach (var t in (o["tiers"] as JObject ?? new JObject()).Properties())
                {
                    Route r;
                    if (!_tiers.TryGetValue(t.Name, out r)) continue;
                    if (t.Value["model"] != null) { var m = (string)t.Value["model"]; if (IsModelName(m)) r.Model = m; else Diagnostics.Log("models.json: ignored model for tier " + t.Name); }
                    if (t.Value["effort"] != null) { var e = (string)t.Value["effort"]; if (Efforts.Contains(e)) r.Effort = e; else Diagnostics.Log("models.json: ignored effort for tier " + t.Name); }
                }
                foreach (var t in (o["tasks"] as JObject ?? new JObject()).Properties())
                    if (_tiers.ContainsKey((string)t.Value)) _tasks[t.Name] = (string)t.Value;
                if (o["review_unit"] != null)
                {
                    var unit = (string)o["review_unit"];
                    if (unit == "section" || unit == "block") ReviewUnit = unit; else Diagnostics.Log("models.json: ignored review_unit");
                }
                foreach (var t in (o["cache"] as JObject ?? new JObject()).Properties())
                    if (new[] { "1h", "5m", "none" }.Contains((string)t.Value)) _cache[t.Name] = (string)t.Value;
                if (o["always_packs"] is JArray ap) AlwaysPacks = ap.Select(x => (string)x).ToList();
                Source = "team: " + path;
            }
            catch (Exception ex) { Diagnostics.Log("models.json", ex); }
            return this;
        }

        public string Describe()
        {
            var sb = new StringBuilder("Routing (" + Source + "), review unit: " + ReviewUnit + ", always-on packs: " + string.Join(", ", AlwaysPacks.ToArray()) + "\n");
            foreach (var t in DefaultTasks.Keys) sb.Append("  ").Append(t).Append(" → ").Append(For(t)).Append(", cache ").Append(CacheFor(t)).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Token use and estimated cost per task and model, for this session (Diagnostics) and for the stress harness.
    /// Prices are list prices per million tokens (input, output, cache read); cache writes are billed at 1.25× input (5-minute) or 2× (1-hour).
    /// </summary>
    public static class Usage
    {
        private static readonly Dictionary<string, double[]> Prices = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "claude-haiku-5-5", new[] { 0.10, 0.50, 0.01 } },
            { "claude-sonnet-5-5", new[] { 2.00, 10.00, 0.20 } },
            { "claude-opus-5-5", new[] { 4.00, 20.00, 0.20 } },
        };

        public sealed class Line { public string Task; public string Model; public int Calls; public long Input, Output, CacheRead, CacheWrite; public double Usd; public double Seconds; }

        private static readonly Dictionary<string, Line> Lines = new Dictionary<string, Line>();
        private static readonly object Gate = new object();

        public static void Record(string task, string model, LlmResponse r, double seconds)
        {
            double[] p;
            if (model == null || !Prices.TryGetValue(model, out p)) p = new[] { 0.0, 0.0, 0.0 };
            var write1h = Math.Min(r.CacheWrite1hTokens, r.CacheWriteTokens);
            var usd = (r.InputTokens * p[0] + (r.CacheWriteTokens - write1h) * p[0] * 1.25 + write1h * p[0] * 2 + r.CacheReadTokens * p[2] + r.OutputTokens * p[1]) / 1e6;
            lock (Gate)
            {
                Line l;
                var key = (task ?? "?") + "|" + model;
                if (!Lines.TryGetValue(key, out l)) Lines[key] = l = new Line { Task = task, Model = model };
                l.Calls++; l.Input += r.InputTokens; l.Output += r.OutputTokens; l.CacheRead += r.CacheReadTokens; l.CacheWrite += r.CacheWriteTokens;
                l.Usd += usd; l.Seconds += seconds;
            }
        }

        /// <summary>Calls and estimated dollars across every task so far this session.</summary>
        public static void Totals(out int calls, out double usd)
        {
            calls = 0; usd = 0;
            lock (Gate) foreach (var l in Lines.Values) { calls += l.Calls; usd += l.Usd; }
        }

        public static List<Line> Snapshot() { lock (Gate) return new List<Line>(Lines.Values); }
        public static void Reset() { lock (Gate) Lines.Clear(); }
    }
}
