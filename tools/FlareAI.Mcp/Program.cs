using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FlareAI.Core;
using FlareAI.Core.Output;
using FlareAI.Core.Style;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlareAI.Mcp
{
    /// <summary>
    /// The plugin's style guide and linter over MCP (stdio, newline-delimited JSON-RPC), built from the same Core code, so
    /// Claude and other AI tools check docs exactly like the plugin does.
    ///
    ///   FlareAI.Mcp.exe [--guide PATH ...] [--allow-root FOLDER ...]     (default: the styleguide project next to the exe or in the repo)
    ///   --allow-root limits the `project` argument to paths inside those folders; without it any readable path is accepted.
    ///   FlareAI.Mcp.exe --llm-export OUTPUT_DIR [--url BASE_URL]
    /// </summary>
    internal static class Program
    {
        private const string Instructions = "Style guide and linter for technical documentation. Layers: the Google developer documentation style guide, then the " +
            "company rules, then (when `project` is given) the open MadCap Flare project; later layers win. Every guide rule has an id like " +
            "'company:terminology/3' — cite it. Use lint for exact, fixable findings; style_lookup before judging style; glossary_lookup for terms; " +
            "validate_guide after editing the guide.";

        private static int Main(string[] args)
        {
            RegexLimit.Apply();
            Diagnostics.Sink = m => Console.Error.WriteLine(m);   // stdout is the protocol channel
            var guides = Enumerable.Range(0, args.Length).Where(i => args[i] == "--guide" && i + 1 < args.Length).Select(i => args[i + 1]).ToList();
            if (guides.Count == 0) guides.Add(DefaultGuide());
            AllowRoots = Enumerable.Range(0, args.Length).Where(i => args[i] == "--allow-root" && i + 1 < args.Length).Select(i => args[i + 1]).ToList();
            var export = Array.IndexOf(args, "--llm-export");
            if (export >= 0)
            {
                var url = Array.IndexOf(args, "--url");
                var r = LlmExport.Export(args[export + 1], null, url >= 0 ? args[url + 1] : null);
                Console.WriteLine(JsonConvert.SerializeObject(new { topics = r.Topics, llms_txt = r.LlmsTxt, markdown = r.Markdown }));
                return 0;
            }
            var engine = new StyleEngine(guides, guides[0]);
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                JObject msg;
                try { msg = JObject.Parse(line); } catch (JsonException) { continue; }
                var id = msg["id"];
                if (id == null) continue;   // notifications need no answer
                JObject reply;
                try { reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Handle(engine, (string)msg["method"], msg["params"] as JObject ?? new JObject()) }; }
                catch (Exception ex) { reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = -32603, ["message"] = ex.Message } }; }
                stdout.WriteLine(reply.ToString(Formatting.None));
            }
            return 0;
        }

        private static string DefaultGuide()
        {
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
                foreach (var name in new[] { "StyleGuide", "styleguide" })
                    if (Directory.Exists(Path.Combine(d.FullName, name, @"Content\Resources\StyleGuide"))) return Path.Combine(d.FullName, name);
            throw new InvalidOperationException("No style guide found; pass --guide PATH.");
        }

        private static JToken Handle(StyleEngine e, string method, JObject p)
        {
            switch (method)
            {
                case "initialize":
                    return new JObject
                    {
                        ["protocolVersion"] = (string)p["protocolVersion"] ?? "2025-06-18",
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "docstyle", ["version"] = "2.0" },
                        ["instructions"] = Instructions
                    };
                case "ping": return new JObject();
                case "tools/list": return new JObject { ["tools"] = Tools() };
                case "tools/call":
                    var result = Call(e, (string)p["name"], p["arguments"] as JObject ?? new JObject());
                    return new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = result.ToString(Formatting.None) }), ["structuredContent"] = result };
                default: throw new InvalidOperationException("Unknown method " + method);
            }
        }

        private static List<string> AllowRoots = new List<string>();

        private static JObject Call(StyleEngine e, string name, JObject a)
        {
            var project = (string)a["project"];
            if (!AllowedRoots.IsAllowed(project, AllowRoots)) throw new InvalidOperationException("That project path is outside the folders this server was started with (--allow-root).");
            switch (name)
            {
                case "lint":
                    var texts = (a["texts"] ?? new JArray()).Select(t => (string)t).ToList();
                    var heads = (a["headings"] ?? new JArray()).Select(t => (int)t).ToList();
                    return new JObject { ["findings"] = JArray.FromObject(e.Lint(texts, heads, project).Select(h => new { index = h.Index, start = h.Start, length = h.Length, match = h.Match, replacement = h.Replacement, rule = h.Rule, rule_id = h.RuleId, layer = h.Layer, variable = h.Variable, severity = h.Severity, message = h.Message, link = h.Link })) };
                case "style_lookup":
                    var query = ((string)a["query"] ?? "").Trim();
                    var rules = e.Rules(project).Select(r => new JObject { ["id"] = r.Id, ["layer"] = r.Layer, ["section"] = r.Section, ["text"] = r.Text, ["link"] = r.Link, ["applies"] = new JArray(r.Applies), ["products"] = new JArray(r.Products) }).ToList();
                    if (query.Length == 0) return new JObject { ["guide"] = e.Guide(project), ["rules"] = new JArray(rules) };
                    var words = System.Text.RegularExpressions.Regex.Matches(query.ToLowerInvariant(), @"\w+").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Where(w => w.Length > 2).ToList();
                    return new JObject { ["rules"] = new JArray(rules.Where(r => words.Any(w => ((string)r["section"] + " " + (string)r["text"]).ToLowerInvariant().Contains(w)))) };
                case "glossary_lookup":
                    var term = ((string)a["term"] ?? "").Trim().ToLowerInvariant();
                    var entries = e.Glossary(project).Where(g => term.Length == 0 || g.Term.ToLowerInvariant() == term || (g.Abbr ?? "").ToLowerInvariant() == term || g.Variants.Any(v => v.ToLowerInvariant() == term));
                    return new JObject { ["entries"] = JArray.FromObject(entries.Select(g => new { term = g.Term, note = g.Note, abbr = g.Abbr, variants = g.Variants, layer = g.Layer })) };
                case "validate_guide":
                    var rep = e.Validate(project);
                    return new JObject
                    {
                        ["issues"] = JArray.FromObject(rep.Issues.Select(i => new { level = i.Level, layer = i.Layer, file = i.File, location = i.Location, message = i.Message })),
                        ["summary"] = new JObject { ["layers"] = new JArray(rep.Layers), ["table_rules"] = rep.TableRules, ["tested_by_example"] = rep.TestedByExample, ["prose_rules"] = rep.ProseRules, ["prose_rules_with_examples"] = rep.ProseRulesWithExamples, ["errors"] = rep.Errors, ["warnings"] = rep.Warnings }
                    };
                case "guide_examples":
                    return new JObject { ["examples"] = JArray.FromObject(e.Examples(project).Select(x => new { id = x.Id, text = x.Text, bad = x.Bad, good = x.Good })) };
                case "mine_style":
                    int files;
                    var mined = e.Mine(project, out files);
                    return new JObject { ["files"] = files, ["candidates"] = JArray.FromObject(mined) };
                default: throw new InvalidOperationException("Unknown tool " + name);
            }
        }

        private static JArray Tools()
        {
            JObject Schema(JObject props, params string[] required) { return new JObject { ["type"] = "object", ["properties"] = props, ["required"] = new JArray(required.Cast<object>().ToArray()) }; }
            var project = new JObject { ["type"] = "string", ["description"] = "Any path inside a MadCap Flare project: adds its variables, glossary and StyleGuide folder as the last layer." };
            Func<string, string, JObject, JObject> tool = (n, d, s) => new JObject { ["name"] = n, ["description"] = d, ["inputSchema"] = s };
            return new JArray(
                tool("lint", "Lint plain-text runs (one per paragraph; never include markup). `headings` = indices of heading runs. Findings: run index, span, matched text, rule, rule_id (citable), layer, link, an exact `replacement` when the fix is mechanical (null otherwise), and `variable` when the text is a typed copy of a project variable.",
                     Schema(new JObject { ["texts"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }, ["headings"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } }, ["project"] = project }, "texts")),
                tool("style_lookup", "The layered guide when `query` is empty (every citable rule: id, layer, section, text, link, applies, products); otherwise the rules matching the keywords.",
                     Schema(new JObject { ["query"] = new JObject { ["type"] = "string" }, ["project"] = project })),
                tool("glossary_lookup", "Look up a term in every layer's glossary; an empty term lists every entry.", Schema(new JObject { ["term"] = new JObject { ["type"] = "string" }, ["project"] = project }, "term")),
                tool("validate_guide", "Test the guide: each rule-table row against its Example, contradictory rows, unreadable topics, bad settings.", Schema(new JObject { ["project"] = project })),
                tool("guide_examples", "Prose rules that carry Bad/Good examples, for checking that an AI reviewer applies them.", Schema(new JObject { ["project"] = project })),
                tool("mine_style", "What a Flare project's own content says its style is: spelling variants, competing words, capitalization, heading case, with counts and sentences, minus what the guide already enforces.", Schema(new JObject { ["project"] = project }, "project")));
        }
    }
}
