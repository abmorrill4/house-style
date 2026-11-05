using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using FlareAI.Core.Editing;
using FlareAI.Core.Llm;

namespace FlareAI.Core.Tests
{
    /// <summary>Loads a fixture topic and lets a test choose the "selection" by XPath.</summary>
    public sealed class FakeDocumentAdapter : IDocumentAdapter
    {
        public FakeDocumentAdapter(string fixtureName)
            : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", fixtureName), true) { }

        /// <summary>Any topic or snippet on disk (for multi-file tests).</summary>
        public FakeDocumentAdapter(string fullPath, bool _)
        {
            SourcePath = fullPath;
            Document = new XmlDocument { PreserveWhitespace = true };
            Document.Load(SourcePath);
            SelectionNodes = new List<XmlNode>();
            SelectionText = "";
            Replacements = new List<Tuple<string, IList<XmlNode>, IList<XmlNode>>>();
        }

        public string SourcePath { get; private set; }
        public bool IsEditableTopic { get { return BlockSelector.FindBody(Document) != null; } }
        public XmlDocument Document { get; private set; }
        public string SelectionText { get; set; }
        public IList<XmlNode> SelectionNodes { get; set; }
        public List<Tuple<string, IList<XmlNode>, IList<XmlNode>>> Replacements { get; private set; }

        public XmlNamespaceManager Ns
        {
            get
            {
                var ns = new XmlNamespaceManager(Document.NameTable);
                ns.AddNamespace("MadCap", BlockSelector.MadCapNs);
                return ns;
            }
        }

        public void SelectXPath(string xpath)
        {
            SelectionNodes = new List<XmlNode>();
            foreach (XmlNode n in Document.SelectNodes(xpath, Ns)) SelectionNodes.Add(n);
            SelectionText = SelectionNodes.Count > 0 ? SelectionNodes[0].InnerText : "";
        }

        public void ReplaceBlocks(string operationName, IList<XmlNode> oldNodes, IList<XmlNode> newNodes)
        {
            Replacements.Add(Tuple.Create(operationName, oldNodes, newNodes));
            var parent = oldNodes[0].ParentNode;
            var anchor = oldNodes[0];
            foreach (var n in newNodes) { parent.InsertBefore(n, anchor); }
            foreach (var o in oldNodes) parent.RemoveChild(o);
        }

        public readonly List<Tuple<string, XmlNode, int, IList<XmlNode>>> Insertions = new List<Tuple<string, XmlNode, int, IList<XmlNode>>>();

        public void InsertBlocks(string operationName, XmlNode parent, int index, IList<XmlNode> newNodes)
        {
            Insertions.Add(Tuple.Create(operationName, parent, index, newNodes));
            for (var i = 0; i < newNodes.Count; i++) parent.InsertBefore(newNodes[i], parent.ChildNodes[index + i]);
        }

        public List<string> TextOperations = new List<string>();
        public string LastSelected;

        public System.Collections.Generic.List<string> InsertedVariables = new System.Collections.Generic.List<string>();
        public bool InsertVariableResult = true;
        public bool InsertVariable(string name) { if (InsertVariableResult) InsertedVariables.Add(name); return InsertVariableResult; }

        public bool SelectText(XmlText run, int start, int length)
        {
            LastSelected = run.Value.Substring(start, length);
            return true;
        }

        public void Edit(string operationName, Action body)
        {
            TextOperations.Add(operationName);
            body();
        }

        public Dictionary<string, string> Variables = new Dictionary<string, string> { { "General.ProductName", "Acme Online Banking" } };

        public string VariableValue(string qualifiedName)
        {
            string v;
            return Variables.TryGetValue(qualifiedName, out v) ? v : null;
        }

        public bool ReplaceSpan(XmlText run, int start, int length, string replacement)
        {
            run.Value = run.Value.Substring(0, start) + replacement + run.Value.Substring(start + length);
            return true;
        }
    }

    /// <summary>A few real docstyle rules, in-process, so Core tests stay fast and offline.</summary>
    public sealed class FakeStyleService : FlareAI.Core.Style.IStyleService
    {
        public string GuideText = "- Address the reader as \"you\", not \"the end user\".";

        public string LastProject;

        public IList<FlareAI.Core.Style.LintHit> Lint(IList<string> texts, IList<int> headings, string project)
        {
            LastProject = project;
            var hits = new List<FlareAI.Core.Style.LintHit>();
            for (var i = 0; i < texts.Count; i++)
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(texts[i], @"Acme Online Banking"))
                    hits.Add(new FlareAI.Core.Style.LintHit { Index = i, Start = m.Index, Length = m.Length, Match = m.Value, Rule = "Project.Variables", Severity = "warning", Layer = "project", Message = "Insert the variable.", Variable = "General.ProductName" });
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(texts[i], @"to Settings"))
                    hits.Add(new FlareAI.Core.Style.LintHit { Index = i, Start = m.Index, Length = m.Length, Match = m.Value, Replacement = "to the Settings", Rule = "Test.Across", Severity = "suggestion", Layer = "company", Message = "Spans markup." });
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(texts[i], @"\butilize\b|\bthe user\b"))
                    hits.Add(new FlareAI.Core.Style.LintHit
                    {
                        Index = i, Start = m.Index, Length = m.Length, Match = m.Value,
                        Replacement = m.Value == "utilize" ? "use" : null,
                        Rule = m.Value == "utilize" ? "plain-words.utilize" : "person.the-user",
                        Severity = "warning", Layer = "company", Message = m.Value == "utilize" ? "Use \"use\"." : "Address the reader as \"you\"."
                    });
            }
            return hits;
        }

        public IList<FlareAI.Core.Style.LintHit> Lint(IList<FlareAI.Core.Style.LintLine> lines, string project)
        {
            var texts = new List<string>(); var heads = new List<int>();
            for (var i = 0; i < lines.Count; i++) { texts.Add(lines[i].Text); if ((lines[i].Context & FlareAI.Core.Style.TextContext.Heading) != 0) heads.Add(i); }
            return Lint(texts, heads, project);
        }

        public string Guide(string project) { return GuideText; }
        public string[] GuideFor(string project, FlareAI.Core.Style.GuideScope scope) { return new[] { GuideText, "" }; }
        public IList<string> RuleIdsFor(string project, FlareAI.Core.Style.GuideScope scope) { return GuideRules.ConvertAll(r => r.Id); }

        public List<FlareAI.Core.Style.GuideRule> GuideRules = new List<FlareAI.Core.Style.GuideRule>
        {
            new FlareAI.Core.Style.GuideRule { Id = "company:terminology/1", Layer = "company", Section = "Audience", Text = "Say administrators.", Link = "file:///C:/guide/Terminology.htm" }
        };
        public IList<FlareAI.Core.Style.GuideRule> Rules(string project) { return GuideRules; }
        public HashSet<string> Off = new HashSet<string>();
        public bool IsOff(string project, string ruleId) { return ruleId != null && Off.Contains(ruleId); }
        public FlareAI.Core.Style.GuideReport Validate(string project) { return new FlareAI.Core.Style.GuideReport(); }
        public IList<FlareAI.Core.Style.GuideExample> Examples(string project) { return new List<FlareAI.Core.Style.GuideExample>(); }
    }

    /// <summary>Returns canned text, or transforms the user prompt; records every request.</summary>
    public sealed class FakeLlmClient : ILlmClient
    {
        private readonly Func<LlmRequest, string> _answer;
        public List<LlmRequest> Requests { get; private set; }

        public FakeLlmClient(Func<LlmRequest, string> answer)
        {
            _answer = answer;
            Requests = new List<LlmRequest>();
            Diagnostics.Enabled = false; // keep test runs out of the real %APPDATA%\FlareAI\diag.log
        }

        public LlmResponse Complete(LlmRequest request)
        {
            lock (Requests) Requests.Add(request);
            return new LlmResponse { Text = Adapt(request, _answer(request)), Model = "fake", StopReason = "end_turn" };
        }

        /// <summary>Echoes the fragment back unchanged — a rewrite must be a no-op (both the run and the XHTML contract).</summary>
        public static string EchoRevised(LlmRequest r)
        {
            var i = r.User.LastIndexOf("Fragment:", StringComparison.Ordinal);
            if (r.Schema != null && r.Task != "restructure") return "{\"changes\":[],\"needs_restructure\":false,\"note\":\"\"}";
            var frag = r.User.Substring(i + "Fragment:".Length).Trim();
            return "<revised>\n" + frag + "\n</revised>";
        }

        /// <summary>A run-contract answer that edits every run's text with <paramref name="edit"/>.</summary>
        public static string EditRuns(LlmRequest r, Func<string, string> edit)
        {
            var changes = new Newtonsoft.Json.Linq.JArray();
            foreach (var run in MarkedRuns(r.User))
            {
                var t = edit(run.Value);
                if (t != run.Value) changes.Add(new Newtonsoft.Json.Linq.JObject { ["run_id"] = run.Key, ["text"] = t });
            }
            return new Newtonsoft.Json.Linq.JObject { ["changes"] = changes, ["needs_restructure"] = false, ["note"] = "" }.ToString();
        }

        /// <summary>The runs of a run-addressed prompt, in order: ⟦r1⟧text⟦/r1⟧.</summary>
        public static List<KeyValuePair<string, string>> MarkedRuns(string prompt)
        {
            var list = new List<KeyValuePair<string, string>>();
            var at = prompt.LastIndexOf("Text:", StringComparison.Ordinal);   // skip the template's own ⟦r1⟧ example
            if (at >= 0) prompt = prompt.Substring(at);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(prompt, "⟦(r\\d+)⟧(.*?)⟦/\\1⟧", System.Text.RegularExpressions.RegexOptions.Singleline))
                list.Add(new KeyValuePair<string, string>(m.Groups[1].Value, System.Net.WebUtility.HtmlDecode(m.Groups[2].Value)));
            return list;
        }

        /// <summary>
        /// Lets tests keep answering in the old shapes: a JSON array of findings becomes the review contract (each finding
        /// placed in the run that holds its excerpt); a &lt;revised&gt; fragment becomes run changes when its text pieces line
        /// up with the runs, or a restructure request when they don't. Real contract-shaped answers pass through.
        /// </summary>
        private static string Adapt(LlmRequest r, string text)
        {
            if (r.Schema == null || text == null) return text;
            var t = text.Trim();
            if (t.StartsWith("```")) { t = t.Substring(t.IndexOf('\n') + 1); if (t.EndsWith("```")) t = t.Substring(0, t.Length - 3); t = t.Trim(); }
            if (t.StartsWith("{")) return t;
            var runs = MarkedRuns(r.User);
            if (r.Task == "review")
            {
                var findings = new Newtonsoft.Json.Linq.JArray();
                if (t.StartsWith("["))
                    foreach (var f in Newtonsoft.Json.Linq.JArray.Parse(t))
                    {
                        var excerpt = (string)f["excerpt"] ?? "";
                        var run = runs.Find(x => excerpt.Length > 0 && x.Value.Contains(excerpt));
                        findings.Add(new Newtonsoft.Json.Linq.JObject
                        {
                            ["run_id"] = run.Key ?? "r999", ["excerpt"] = excerpt, ["rule_id"] = (string)f["rule_id"] ?? "general", ["rule"] = (string)f["rule"] ?? "",
                            ["severity"] = (string)f["severity"] ?? "suggestion", ["replacement"] = f["replacement"] ?? Newtonsoft.Json.Linq.JValue.CreateNull(),
                            ["why"] = (string)f["why"] ?? (string)f["suggestion"] ?? ""
                        });
                    }
                return new Newtonsoft.Json.Linq.JObject { ["findings"] = findings }.ToString();
            }
            var open = t.IndexOf("<revised>", StringComparison.Ordinal);
            var close = t.LastIndexOf("</revised>", StringComparison.Ordinal);
            if (open < 0 || close <= open) return t;   // prose: breaks the contract, as a real model would
            var frag = t.Substring(open + 9, close - open - 9);
            if (r.Task == "restructure")
                return new Newtonsoft.Json.Linq.JObject { ["findings"] = new Newtonsoft.Json.Linq.JArray(), ["changes"] = new Newtonsoft.Json.Linq.JArray(), ["needs_restructure"] = false, ["note"] = "", ["revised"] = frag }.ToString();
            var doc = new XmlDocument();
            doc.LoadXml("<x xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\">" + frag + "</x>");
            var texts = new List<string>();
            foreach (XmlNode n in doc.SelectNodes("//text()")) if (n.Value.Trim().Length > 0) texts.Add(n.Value);
            var changes = new Newtonsoft.Json.Linq.JArray();
            var restructure = texts.Count != runs.Count;
            if (!restructure)
                for (var i = 0; i < runs.Count; i++)
                    if (texts[i].Trim() != runs[i].Value.Trim())
                        changes.Add(new Newtonsoft.Json.Linq.JObject { ["run_id"] = runs[i].Key, ["text"] = texts[i] });
            return new Newtonsoft.Json.Linq.JObject { ["changes"] = changes, ["needs_restructure"] = restructure, ["note"] = restructure ? "structure differs" : "" }.ToString();
        }
    }
}
