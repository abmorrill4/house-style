using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Style
{
    /// <summary>One form of a term and how often the content uses it.</summary>
    public sealed class TermForm
    {
        public string Text { get; set; }
        public int Count { get; set; }
        /// <summary>The plural counted with this form ("members" for "member"), or null.</summary>
        public string Plural { get; set; }
    }

    /// <summary>A sentence from the content (or the author's notes) that shows a candidate rule.</summary>
    public sealed class CandidateExample { public string Text { get; set; } public string File { get; set; } public string Form { get; set; } }

    /// <summary>
    /// A proposed style-guide rule: mined from the project's content (spelling variants, competing words,
    /// capitalization, heading case) or extracted by the AI from notes or an old guide. Becomes a table row
    /// (Avoid/Use/Severity/Why/Example, enforced by the linter) or a bullet with Bad/Good (applied by the AI).
    /// </summary>
    public sealed class RuleCandidate
    {
        public string Id { get; set; }
        /// <summary>spelling, synonym, capitalization, heading, or notes.</summary>
        public string Kind { get; set; }
        public string Title { get; set; }
        public List<TermForm> Forms { get; set; } = new List<TermForm>();
        public List<CandidateExample> Examples { get; set; } = new List<CandidateExample>();
        /// <summary>The preferred form (the most used, until the owner picks another).</summary>
        public string Prefer { get; set; }
        // As a table row
        public string Avoid { get; set; }
        public string Use { get; set; }
        public string Severity { get; set; } = "warning";
        public string Why { get; set; }
        public string Example { get; set; }
        // As a bullet
        public string Text { get; set; }
        public string Bad { get; set; }
        public string Good { get; set; }
        /// <summary>Where a notes-extracted rule came from (quoted from the notes).</summary>
        public string Quote { get; set; }
        public int Impact { get; set; }

        public bool IsRow { get { return !string.IsNullOrEmpty(Avoid); } }

        /// <summary>The owner picked another preferred form: avoid the others, and use an example that breaks the new rule.</summary>
        public void Choose(string form)
        {
            if (Forms.Count == 0 || form == null) return;
            Prefer = form;
            var chosen = Forms.FirstOrDefault(f => f.Text == form);
            var avoid = new List<string>();
            var use = new List<string>();
            foreach (var o in Forms.Where(f => f.Text != form))
            {
                avoid.Add(o.Text); use.Add(form);
                if (o.Plural != null) { avoid.Add(o.Plural); use.Add(chosen != null && chosen.Plural != null ? chosen.Plural : form); }
            }
            var others = Forms.Where(f => f.Text != form).Select(f => f.Text).ToList();
            Avoid = string.Join(", ", avoid.ToArray());
            Use = string.Join(", ", use.ToArray());
            var ex = Examples.FirstOrDefault(e => e.Form != form && others.Contains(e.Form));
            Example = ex == null ? "" : ex.Text;
        }
    }

    public static class GuideBuilder
    {
        /// <summary>Candidates from the docstyle server's mine_style result.</summary>
        public static List<RuleCandidate> ParseMined(JObject result)
        {
            var list = new List<RuleCandidate>();
            foreach (var c in result["candidates"] ?? new JArray())
            {
                var rc = new RuleCandidate
                {
                    Id = (string)c["id"], Kind = (string)c["kind"], Title = (string)c["title"], Prefer = (string)c["prefer"], Impact = (int?)c["impact"] ?? 0
                };
                foreach (var f in c["forms"] ?? new JArray()) rc.Forms.Add(new TermForm { Text = (string)f["text"], Count = (int?)f["count"] ?? 0, Plural = (string)f["plural"] });
                foreach (var e in c["examples"] ?? new JArray()) rc.Examples.Add(new CandidateExample { Text = (string)e["text"], File = (string)e["file"], Form = (string)e["form"] });
                var row = c["row"] as JObject;
                if (row != null) { rc.Avoid = (string)row["avoid"]; rc.Use = (string)row["use"]; rc.Severity = (string)row["severity"] ?? "warning"; rc.Why = (string)row["why"]; rc.Example = (string)row["example"]; }
                var bullet = c["bullet"] as JObject;
                if (bullet != null) { rc.Text = (string)bullet["text"]; rc.Bad = (string)bullet["bad"]; rc.Good = (string)bullet["good"]; }
                list.Add(rc);
            }
            return list;
        }

        /// <summary>
        /// Notes split into paragraph-sized pieces, each read on its own: paragraphs (blank lines), small ones merged, and a
        /// new piece at a heading once the current one has some text. Text with one line per paragraph and no blank lines
        /// (a Word document, see <see cref="NoteText.FromDocx"/>) is split by line the same way: before, a .docx guide came
        /// out as 3 pieces of up to 4,900 characters where its .txt copy gave 18, and lost rules.
        /// </summary>
        public static List<string> Chunks(string notes, int min = 400, int max = 3000)
        {
            var units = new List<string>();
            foreach (var para in System.Text.RegularExpressions.Regex.Split((notes ?? "").Replace("\r\n", "\n"), @"\n\s*\n"))
            {
                var p = para.Trim();
                if (p.Length == 0) continue;
                // A long paragraph of many lines is many paragraphs written one per line.
                if (p.Length > min && p.IndexOf('\n') >= 0) units.AddRange(p.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
                else units.Add(p);
            }
            var chunks = new List<string>();
            var cur = new StringBuilder();
            for (var i = 0; i < units.Count; i++)
            {
                var p = units[i];
                var heading = i + 1 < units.Count && IsHeading(p);
                if (cur.Length > 0 && (cur.Length >= min || cur.Length + p.Length > max || (heading && cur.Length >= min / 2))) { chunks.Add(cur.ToString()); cur.Clear(); }
                if (cur.Length > 0) cur.Append("\n\n");
                cur.Append(p.Length > max ? p.Substring(0, max) : p);
                for (var at = max; at < p.Length; at += max) { chunks.Add(cur.ToString()); cur.Clear(); cur.Append(p.Substring(at, Math.Min(max, p.Length - at))); }
            }
            if (cur.Length > 0) chunks.Add(cur.ToString());
            return chunks;
        }

        /// <summary>A line that reads like a heading: short, no sentence punctuation at the end, not a list item.</summary>
        private static bool IsHeading(string line)
        {
            return line.Length > 0 && line.Length <= 60 && line.IndexOf('\n') < 0 && ".:;,!?".IndexOf(line[line.Length - 1]) < 0 &&
                   !System.Text.RegularExpressions.Regex.IsMatch(line, @"^(?:[-*•]|\d+[.)])\s") && char.IsUpper(line[0]);
        }

        /// <summary>Candidates from the extract contract's rules (or a bare JSON array of them, from older answers).</summary>
        public static List<RuleCandidate> ParseExtracted(string modelText)
        {
            var s = (modelText ?? "").Trim();
            if (s.StartsWith("{")) return FromExtracted((JArray)(JObject.Parse(s)["rules"] ?? new JArray()));
            int start = s.IndexOf('['), end = s.LastIndexOf(']');
            if (start < 0 || end <= start) throw new FormatException("Model did not return a JSON array.");
            return FromExtracted(JArray.Parse(s.Substring(start, end - start + 1)));
        }

        public static List<RuleCandidate> FromExtracted(JArray rules)
        {
            var list = new List<RuleCandidate>();
            var n = 0;
            foreach (var c in rules)
            {
                var avoid = ((string)c["kind"] == "prose" ? "" : ((string)c["avoid"] ?? "")).Trim();
                var rc = new RuleCandidate
                {
                    Id = "notes:" + (++n), Kind = "notes", Title = (string)c["title"] ?? (string)c["text"] ?? avoid,
                    Avoid = avoid.Length > 0 ? avoid : null, Use = (string)c["use"] ?? "", Why = (string)c["why"], Example = (string)c["example"],
                    Text = Empty((string)c["text"]), Bad = Empty((string)c["bad"]), Good = Empty((string)c["good"]), Quote = (string)c["quote"]
                };
                var sev = Severity.Normalize((string)c["severity"] ?? "warning");
                rc.Severity = sev == "error" || sev == "suggestion" ? sev : "warning";
                if (rc.IsRow)
                {
                    foreach (var prose in SplitContextDependent(rc)) { prose.Id = "notes:" + (++n); list.Add(prose); }
                    if (!rc.IsRow) continue;   // every word was context-dependent: only the prose rules remain
                }
                if (rc.IsRow || !string.IsNullOrEmpty(rc.Text)) list.Add(rc);
            }
            return list;
        }

        /// <summary>
        /// Words that are wrong only in some senses or places ("click here" yes, "here" no; "once" meaning "after"; the noun
        /// "setup" is fine, the verb isn't): as an Avoid row the linter would flag every use. Measured: an import turned
        /// "here", "once", "below"/"above", "setup" and "hit" into blanket rows.
        /// </summary>
        public static readonly HashSet<string> ContextDependent = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "here", "there", "this", "that", "these", "those", "once", "below", "above", "following", "later", "earlier", "former", "latter",
            "since", "while", "as", "setup", "login", "logon", "signup", "backup", "checkout", "hit", "check", "via"
        };

        /// <summary>
        /// Takes the context-dependent single words out of a row (<see cref="ContextDependent"/>) and returns a prose rule for
        /// each: the AI reviewer applies a prose rule with judgment; the linter would flag every use. Phrases ("click here")
        /// stay in the row. The row is left with no Avoid text when every word was context-dependent.
        /// </summary>
        private static List<RuleCandidate> SplitContextDependent(RuleCandidate row)
        {
            var avoid = row.Avoid.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            var use = (row.Use ?? "").Split(',').Select(u => u.Trim()).ToList();
            var paired = use.Count == avoid.Count;
            var prose = new List<RuleCandidate>();
            var keepAvoid = new List<string>();
            var keepUse = new List<string>();
            for (var i = 0; i < avoid.Count; i++)
            {
                var replacement = paired ? use[i] : (use.Count == 1 ? use[0] : "");
                if (replacement == "(delete)") replacement = "";
                if (!ContextDependent.Contains(avoid[i])) { keepAvoid.Add(avoid[i]); if (paired) keepUse.Add(use[i]); continue; }
                var w = avoid[i];
                prose.Add(new RuleCandidate
                {
                    Kind = "notes", Title = row.Title, Severity = row.Severity, Why = row.Why, Quote = row.Quote,
                    Text = replacement.Length > 0
                        ? "Where \"" + w + "\" means \"" + replacement + "\", write \"" + replacement + "\"; in its other senses \"" + w + "\" is fine."
                        : "Don't use \"" + w + "\" where it is vague (for example as link text, or to point at a place the reader can't see); where its meaning is clear it is fine.",
                    Bad = Empty(row.Example),
                    Good = replacement.Length > 0 && !string.IsNullOrEmpty(row.Example)
                        ? System.Text.RegularExpressions.Regex.Replace(row.Example, @"\b" + System.Text.RegularExpressions.Regex.Escape(w) + @"\b",
                                                                        m => char.IsUpper(m.Value[0]) ? char.ToUpper(replacement[0]) + replacement.Substring(1) : replacement,
                                                                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        : null
                });
            }
            if (prose.Count == 0) return prose;
            row.Avoid = keepAvoid.Count > 0 ? string.Join(", ", keepAvoid.ToArray()) : null;
            if (paired) row.Use = string.Join(", ", keepUse.ToArray());
            return prose;
        }

        private static string Empty(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        /// <summary>The topic Build style guide adds rules to.</summary>
        public const string ContentTopic = "From-our-content.htm";

        /// <summary>
        /// Adds accepted candidates to the guide rule changes go to (the team's, or the writer's own, created on first use;
        /// never the read-only guide in the plugin folder): <see cref="ContentTopic"/> in its StyleGuide folder.
        /// Returns the topic written; <paramref name="added"/> is how many rules were added.
        /// </summary>
        public static string AddToGuide(FlareAI.Core.Actions.Team team, IList<RuleCandidate> accepted, out int added)
        {
            if (team == null) throw new ArgumentNullException("team");
            GuideOverrides.RefuseUnreachable(team);
            var root = team.WritableGuide();
            var folder = Directory.GetFiles(root, "*.flprj").Length > 0 ? Path.Combine(root, @"Content\Resources\StyleGuide") : root;
            var topic = Path.Combine(folder, ContentTopic);
            added = Write(topic, accepted, "From our content");
            return topic;
        }

        /// <summary>
        /// Writes accepted candidates into the guide: rows go into the Avoid/Use table and bullets into the list of the
        /// topic <paramref name="topicPath"/> (created with both when missing). Returns how many were added.
        /// </summary>
        public static int Write(string topicPath, IList<RuleCandidate> accepted, string title)
        {
            GuideCopy.RefuseWrite(topicPath);
            var doc = new XmlDocument { PreserveWhitespace = false };
            if (File.Exists(topicPath)) doc.Load(topicPath);
            else doc.LoadXml("<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head><title>" +
                             Esc(title) + "</title></head><body><h1>" + Esc(title) + "</h1></body></html>");
            var body = (XmlElement)doc.SelectSingleNode("//*[local-name()='body']");
            var ns = body.NamespaceURI;
            XmlElement table = null, list = null;
            foreach (XmlElement t in body.SelectNodes(".//*[local-name()='table']"))
            {
                var head = t.SelectSingleNode(".//*[local-name()='tr']");
                if (head != null && head.InnerText.Replace(" ", "").StartsWith("AvoidUse", StringComparison.OrdinalIgnoreCase)) { table = t; break; }
            }
            foreach (XmlElement u in body.SelectNodes("./*[local-name()='ul']")) list = u;

            var added = 0;
            foreach (var c in accepted)
            {
                if (c.IsRow)
                {
                    if (table == null)
                    {
                        body.AppendChild(El(doc, ns, "h2", "Terms"));
                        table = El(doc, ns, "table", null);
                        var tr = El(doc, ns, "tr", null);
                        foreach (var h in new[] { "Avoid", "Use", "Severity", "Why", "Example" }) tr.AppendChild(El(doc, ns, "th", h));
                        table.AppendChild(tr);
                        body.AppendChild(table);
                    }
                    var row = El(doc, ns, "tr", null);
                    foreach (var cell in new[] { c.Avoid, c.Use, c.Severity, c.Why, c.Example }) row.AppendChild(El(doc, ns, "td", cell ?? ""));
                    table.AppendChild(row);
                }
                else
                {
                    if (list == null)
                    {
                        body.AppendChild(El(doc, ns, "h2", "Conventions"));
                        list = El(doc, ns, "ul", null);
                        body.AppendChild(list);
                    }
                    var li = El(doc, ns, "li", c.Text);
                    if (!string.IsNullOrEmpty(c.Bad) || !string.IsNullOrEmpty(c.Good))
                    {
                        var sub = El(doc, ns, "ul", null);
                        if (!string.IsNullOrEmpty(c.Bad)) sub.AppendChild(El(doc, ns, "li", "Bad: " + c.Bad));
                        if (!string.IsNullOrEmpty(c.Good)) sub.AppendChild(El(doc, ns, "li", "Good: " + c.Good));
                        li.AppendChild(sub);
                    }
                    list.AppendChild(li);
                }
                added++;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(topicPath));
            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (var w = XmlWriter.Create(topicPath, settings)) doc.Save(w);
            return added;
        }

        private static XmlElement El(XmlDocument doc, string ns, string name, string text)
        {
            var e = doc.CreateElement(name, ns);
            if (text != null) e.InnerText = text;
            return e;
        }

        private static string Esc(string s) { return System.Security.SecurityElement.Escape(s ?? ""); }
    }
}
