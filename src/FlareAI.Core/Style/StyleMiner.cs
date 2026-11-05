using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using FlareAI.Core.Editing;

namespace FlareAI.Core.Style
{
    /// <summary>
    /// What a Flare project's own content says its style is. Reads every topic and snippet (not the StyleGuide folder) and
    /// finds spelling variants of one term, competing words for one concept, inconsistent capitalization and heading case.
    /// Each finding is a candidate rule with counts, real sentences and a ready guide entry; the owner picks the form.
    /// </summary>
    public static class StyleMiner
    {
        public sealed class Result { public int Files, Blocks; public List<RuleCandidate> Candidates = new List<RuleCandidate>(); }

        private static readonly HashSet<string> BlockTags = new HashSet<string> { "p", "li", "h1", "h2", "h3", "h4", "h5", "h6", "td", "th", "dt", "dd", "caption", "title" };
        private static readonly HashSet<string> Headings = new HashSet<string> { "h1", "h2", "h3", "h4", "h5", "h6" };

        // Concepts writers commonly name several ways: the forms, matched as whole words, case-insensitive ("member/members" pairs a plural).
        private static readonly KeyValuePair<string, string[]>[] Synonyms =
        {
            Kv("signing in", "sign in", "log in", "log on", "login"),
            Kv("signing out", "sign out", "log out", "log off", "logout"),
            Kv("UI action verb", "click", "select", "tap", "choose"),
            Kv("the people who use the product", "member/members", "customer/customers", "user/users"),
            Kv("the bank or credit union", "financial institution/financial institutions", "bank/banks", "credit union/credit unions", "FI/FIs"),
            Kv("administrator", "administrator/administrators", "admin/admins"),
            Kv("the product's software", "app/apps", "application/applications"),
            Kv("sign-in name", "username", "user name", "user ID", "login ID"),
            Kv("addressing the reader", "the user", "you"),
        };

        // Closed-up words that aren't variants of their spaced form ("in to" and "into" mean different things).
        private static readonly HashSet<string> NotVariants = new HashSet<string> { "into", "onto", "maybe", "everyday", "anyone", "cannot", "someone", "sometime", "sometimes", "without",
            "within", "another", "already", "altogether", "anyway", "everyone", "nobody", "overall", "upon" };

        private static KeyValuePair<string, string[]> Kv(string k, params string[] v) { return new KeyValuePair<string, string[]>(k, v); }

        private sealed class Block { public string File, Tag, Text; }

        public static Result Mine(string root, int limit = 3000)
        {
            int nfiles;
            var blocks = ProjectBlocks(root, limit, out nfiles);
            var prose = blocks.Where(b => !Headings.Contains(b.Tag)).ToList();
            var candidates = new List<RuleCandidate>();

            // 1. Spelling variants: the same letters with a hyphen, a space, or closed up.
            var grams = new Dictionary<string, int>();
            Action<string> bump = g => { int n; grams.TryGetValue(g, out n); grams[g] = n + 1; };
            foreach (var b in prose)
            {
                var words = Regex.Matches(b.Text, "[A-Za-z]+(?:-[A-Za-z]+)?").Cast<Match>().Select(m => m.Value).ToList();
                for (var i = 0; i < words.Count; i++)
                {
                    bump(words[i].ToLowerInvariant());
                    if (i + 1 < words.Count && (words[i] + words[i + 1]).IndexOf('-') < 0) bump((words[i] + " " + words[i + 1]).ToLowerInvariant());
                }
            }
            var byKey = new Dictionary<string, List<KeyValuePair<string, int>>>();
            foreach (var g in grams)
            {
                var key = Regex.Replace(g.Key, @"[\s-]", "");
                if (g.Value >= 2 && key.Length >= 5 && !NotVariants.Contains(key))
                {
                    List<KeyValuePair<string, int>> l;
                    if (!byKey.TryGetValue(key, out l)) byKey[key] = l = new List<KeyValuePair<string, int>>();
                    l.Add(g);
                }
            }
            foreach (var forms in byKey.Values.Where(f => f.Count >= 2))
                candidates.Add(TermCandidate("spelling", "Spell “" + forms.OrderByDescending(f => f.Value).First().Key + "” one way", forms, prose, false, null));

            // 2. Competing words for one concept.
            foreach (var syn in Synonyms)
            {
                var plurals = syn.Value.ToDictionary(m => m.Split('/')[0], m => m.Contains("/") ? m.Split('/')[1] : null);
                var forms = plurals.Select(p => new KeyValuePair<string, int>(p.Key, Count(FormsRx(p.Key, p.Value), prose, RegexOptions.IgnoreCase).Key)).Where(f => f.Value > 0).ToList();
                if (forms.Count >= 2 && forms.Sum(f => f.Value) >= 5)
                    candidates.Add(TermCandidate("synonym", "One word for " + syn.Key, forms, prose, false, plurals));
            }

            // 3. Capitalization: a term capitalized mid-sentence in some places, lowercase in others.
            const string mid = @"(?<=[a-z,;:] )";
            var caps = new Dictionary<string, int>();
            foreach (var b in prose)
                foreach (Match m in Regex.Matches(b.Text, mid + "([A-Z][a-z]+(?: [A-Z][a-z]+)+)"))
                { int n; caps.TryGetValue(m.Groups[1].Value, out n); caps[m.Groups[1].Value] = n + 1; }
            foreach (var t in caps.Where(c => c.Value >= 2))
            {
                var low = prose.Sum(b => Regex.Matches(b.Text, mid + Regex.Escape(t.Key.ToLowerInvariant()) + @"(?!\w)").Count);
                if (low < 2) continue;
                var forms = new List<KeyValuePair<string, int>> { t, new KeyValuePair<string, int>(t.Key.ToLowerInvariant(), low) };
                candidates.Add(TermCandidate("capitalization", "Capitalize “" + forms.OrderByDescending(f => f.Value).First().Key + "” one way", forms, prose, true, null));
            }

            // 4. Heading case.
            var heads = blocks.Where(b => Headings.Contains(b.Tag) && b.Text.Split(' ').Length >= 3).Select(b => b.Text).ToList();
            if (heads.Count >= 5)
            {
                Func<string, bool> isTitle = h =>
                {
                    var rest = h.Split(' ').Skip(1).Where(w => w.Length > 3).ToList();
                    return rest.Count(w => char.IsUpper(w[0])) >= Math.Max(1, rest.Count * 0.6);
                };
                var title = heads.Where(isTitle).ToList();
                var sentence = heads.Where(h => !title.Contains(h)).ToList();
                var preferTitle = (double)title.Count / heads.Count >= 0.5;
                var good = preferTitle ? title : sentence;
                var bad = preferTitle ? sentence : title;
                if (bad.Count > 0)
                {
                    var rule = preferTitle ? "Use title case for headings." : "Use sentence case for headings: capitalize only the first word and proper nouns.";
                    var c = new RuleCandidate { Id = "heading-case", Kind = "heading", Title = rule.Split(':')[0], Prefer = preferTitle ? "title case" : "sentence case",
                                                Text = rule, Bad = bad[0], Good = good.Count > 0 ? good[0] : "", Impact = bad.Count };
                    c.Forms.Add(new TermForm { Text = "title case", Count = title.Count });
                    c.Forms.Add(new TermForm { Text = "sentence case", Count = sentence.Count });
                    foreach (var h in bad.Take(3)) c.Examples.Add(new CandidateExample { Text = h, File = "" });
                    candidates.Add(c);
                }
            }
            return new Result { Files = nfiles, Blocks = blocks.Count, Candidates = candidates.OrderByDescending(c => c.Impact).Take(40).ToList() };
        }

        private static RuleCandidate TermCandidate(string kind, string title, List<KeyValuePair<string, int>> forms, List<Block> prose, bool exactCase, Dictionary<string, string> plurals)
        {
            plurals = plurals ?? new Dictionary<string, string>();
            Func<string, string> plural = f => { string p; return plurals.TryGetValue(f, out p) ? p : null; };
            var ordered = forms.OrderByDescending(f => f.Value).ToList();
            var prefer = ordered[0].Key;
            var c = new RuleCandidate { Id = kind + ":" + Regex.Replace(prefer.ToLowerInvariant(), @"\W+", "-"), Kind = kind, Title = title, Prefer = prefer,
                                        Severity = "warning", Impact = ordered.Skip(1).Sum(f => f.Value) };
            foreach (var f in ordered) c.Forms.Add(new TermForm { Text = f.Key, Count = f.Value, Plural = plural(f.Key) });
            foreach (var f in ordered)    // every form: the owner may pick a different preferred form
                foreach (var e in Count(FormsRx(f.Key, plural(f.Key)), prose, exactCase ? RegexOptions.None : RegexOptions.IgnoreCase).Value)
                    c.Examples.Add(new CandidateExample { Text = e.Text, File = e.File, Form = f.Key });
            c.Examples = c.Examples.Take(8).ToList();
            var avoid = new List<string>(); var use = new List<string>();
            foreach (var o in ordered.Skip(1))
            {
                avoid.Add(o.Key); use.Add(prefer);
                if (plural(o.Key) != null) { avoid.Add(plural(o.Key)); use.Add(plural(prefer) ?? prefer); }
            }
            c.Avoid = string.Join(", ", avoid.ToArray());
            c.Use = string.Join(", ", use.ToArray());
            c.Why = "Team usage: " + string.Join(", ", ordered.Select(f => "“" + f.Key + "” " + f.Value + "×").ToArray()) + ".";
            var ex = c.Examples.FirstOrDefault(e => e.Form != prefer);
            c.Example = ex == null ? "" : ex.Text;
            return c;
        }

        private static string Phrase(string form) { return string.Join(@"\s+", form.Split(' ').Select(Regex.Escape).ToArray()); }
        private static string FormsRx(string word, string plural) { return plural != null ? "(?:" + Phrase(plural) + "|" + Phrase(word) + ")" : Phrase(word); }

        private static KeyValuePair<int, List<CandidateExample>> Count(string pattern, List<Block> texts, RegexOptions flags)
        {
            var rx = new Regex(@"(?<![\w-])" + pattern + @"(?![\w-])", flags | RegexOptions.CultureInvariant);
            var n = 0;
            var examples = new List<CandidateExample>();
            foreach (var b in texts)
            {
                var found = rx.Matches(b.Text);
                if (found.Count == 0) continue;
                n += found.Count;
                if (examples.Count < 2) examples.Add(new CandidateExample { Text = Sentence(b.Text, found[0]), File = b.File });
            }
            return new KeyValuePair<int, List<CandidateExample>>(n, examples);
        }

        /// <summary>The sentence around the match; a window around it when the sentence is long.</summary>
        private static string Sentence(string text, Match m)
        {
            var dot = m.Index > 0 ? text.LastIndexOf(". ", m.Index - 1, m.Index, StringComparison.Ordinal) : -1;
            var start = dot >= 0 ? dot + 2 : 0;
            var end = text.IndexOf(". ", m.Index + m.Length, StringComparison.Ordinal);
            var s = text.Substring(start, (end > 0 ? end + 1 : text.Length) - start);
            if (s.Length <= 220) return s;
            var at = m.Index - start;
            var lo = Math.Max(0, at - 100);
            return (lo > 0 ? "…" : "") + s.Substring(lo, Math.Min(210, s.Length - lo)).Trim() + "…";
        }

        /// <summary>Every text block of the project's topics and snippets, with variables read as their values.</summary>
        private static List<Block> ProjectBlocks(string root, int limit, out int nfiles)
        {
            var content = Path.Combine(root, "Content");
            var guide = Path.Combine(content, @"Resources\StyleGuide") + Path.DirectorySeparatorChar;
            var files = !Directory.Exists(content) ? new List<string>() :
                new[] { "*.htm", "*.html", "*.flsnp" }.SelectMany(p => Directory.GetFiles(content, p, SearchOption.AllDirectories))
                .Where(f => !f.StartsWith(guide, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            nfiles = files.Count;
            var variables = new Dictionary<string, string>();
            var vs = Path.Combine(root, @"Project\VariableSets");
            if (Directory.Exists(vs))
                foreach (var f in Directory.GetFiles(vs, "*.flvar"))
                    try { var x = FlareAI.Core.Editing.SafeXml.Load(f); foreach (XmlElement v in x.GetElementsByTagName("Variable")) variables[Path.GetFileNameWithoutExtension(f) + "." + v.GetAttribute("Name")] = v.InnerText.Trim(); }
                    catch (XmlException) { }
            var outList = new List<Block>();
            foreach (var f in files.Take(limit))
            {
                XmlDocument doc;
                try { doc = FlareAI.Core.Editing.SafeXml.Load(f); } catch (Exception) { continue; }
                var body = BlockSelector.FindBody(doc);
                if (body == null) continue;
                foreach (XmlElement el in body.SelectNodes(".//*"))
                    if (BlockTags.Contains(el.LocalName))
                    {
                        var t = Regex.Replace(BlockText(el, variables), @"\s+", " ").Trim();
                        if (t.Length > 0) outList.Add(new Block { File = Path.GetFileName(f), Tag = el.LocalName, Text = t });
                    }
            }
            return outList;
        }

        /// <summary>Text of a block without the text of nested blocks (a list inside a list item is its own block).</summary>
        private static string BlockText(XmlNode el, Dictionary<string, string> variables)
        {
            var sb = new System.Text.StringBuilder();
            foreach (XmlNode c in el.ChildNodes)
            {
                if (c.NodeType == XmlNodeType.Text || c.NodeType == XmlNodeType.Whitespace || c.NodeType == XmlNodeType.SignificantWhitespace) { sb.Append(c.Value); continue; }
                if (c.NodeType != XmlNodeType.Element) continue;
                if (c.LocalName == "variable") { string v; variables.TryGetValue(((XmlElement)c).GetAttribute("name"), out v); sb.Append(v ?? ""); }
                else if (!BlockTags.Contains(c.LocalName) && c.LocalName != "ul" && c.LocalName != "ol" && c.LocalName != "table") sb.Append(BlockText(c, variables));
            }
            return sb.ToString();
        }
    }
}
