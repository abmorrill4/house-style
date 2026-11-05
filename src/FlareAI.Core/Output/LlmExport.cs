using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FlareAI.Core.Output
{
    /// <summary>
    /// LLM-ready output: built Flare HTML (HTML5 or Clean XHTML) → one Markdown file per topic + llms.txt + llms-full.txt.
    /// Built output already has snippets, variables and conditions resolved, so the conversion is deterministic. By
    /// default llms.txt goes to the output root and the Markdown under llm/, so it publishes with the site.
    /// </summary>
    public static class LlmExport
    {
        public sealed class Result { public int Topics; public string LlmsTxt, Markdown; }

        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skins", "resources", "data", "llm", "search", "fonts", "scripts", "stylesheets" };
        private static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "default.htm", "index.htm", "index.html", "search.htm" };

        public static Result Export(string outputDir, string dest = null, string baseUrl = null, string name = null)
        {
            var output = Path.GetFullPath(outputDir).TrimEnd('\\');
            dest = dest == null ? output : Path.GetFullPath(dest);
            var mdRoot = Path.Combine(dest, "llm");
            var pages = new List<Page>();
            foreach (var path in Directory.GetFiles(output, "*.htm*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var rel = path.Substring(output.Length + 1);
                var parts = rel.Split('\\');
                if (parts.Take(parts.Length - 1).Any(SkipDirs.Contains)) continue;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if ((ext != ".htm" && ext != ".html") || SkipNames.Contains(Path.GetFileName(path))) continue;
                string raw;
                try { raw = File.ReadAllText(path, Encoding.UTF8); } catch (IOException) { continue; }
                var conv = new HtmlToMarkdown(LinkToMarkdown);
                conv.Feed(raw);
                var body = conv.Markdown();
                if (body.Trim().Length < 20) continue;
                var relUrl = rel.Replace('\\', '/');
                var title = WebUtility.HtmlDecode(conv.Title).Trim();
                if (title.Length == 0) title = Path.GetFileNameWithoutExtension(path);
                var mdRel = Path.ChangeExtension(relUrl, ".md");
                var url = baseUrl != null ? baseUrl.TrimEnd('/') + "/" + relUrl : null;
                var front = "---\ntitle: " + title.Replace("\r", " ").Replace("\n", " ") + "\nsource: " + relUrl + (url != null ? "\nurl: " + url : "") + "\n---\n\n";
                var target = Path.Combine(mdRoot, mdRel.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, front + body, new UTF8Encoding(false));
                var first = Regex.Replace(body.Split('\n').FirstOrDefault(l => l.Length > 30 && "#|-!>".IndexOf(l[0]) < 0) ?? "", @"\[([^\]]*)\]\([^)]*\)", "$1");   // links only work relative to the topic, not to llms.txt
                pages.Add(new Page { Title = title, Md = "llm/" + mdRel, Summary = first.Length > 160 ? first.Substring(0, 160) : first, Body = body });
            }
            var project = new DirectoryInfo(output).Parent;
            while (project != null && project.GetFiles("*.flprj").Length == 0) project = project.Parent;
            var site = name ?? (project != null ? project.Name + " — " + Path.GetFileName(output) : Path.GetFileName(output));
            var lines = new List<string> { "# " + site, "", "> Product documentation from MadCap Flare, as Markdown for LLMs and AI assistants.", "", "## Topics", "" };
            lines.AddRange(pages.Select(p => "- [" + p.Title + "](" + p.Md + ")" + (p.Summary.Length > 0 ? ": " + p.Summary : "")));
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "llms.txt"), string.Join("\n", lines.ToArray()) + "\n", new UTF8Encoding(false));
            var full = new StringBuilder("# " + site + "\n");
            foreach (var p in pages) full.Append("\n\n---\n\n<!-- " + p.Md + " -->\n\n" + p.Body);
            File.WriteAllText(Path.Combine(dest, "llms-full.txt"), full.ToString(), new UTF8Encoding(false));
            return new Result { Topics = pages.Count, LlmsTxt = Path.Combine(dest, "llms.txt"), Markdown = mdRoot };
        }

        private sealed class Page { public string Title, Md, Summary, Body; }

        private static string LinkToMarkdown(string href)
        {
            if (string.IsNullOrEmpty(href) || href.StartsWith("#") || href.StartsWith("mailto:") || href.StartsWith("javascript:") || Regex.IsMatch(href, "^[a-z]+://")) return href;
            var target = href.Split('#')[0];
            return target.Length > 0 ? Regex.Replace(target, @"\.html?$", ".md") : href;
        }
    }

    /// <summary>Small HTML → Markdown converter for documentation topics: headings, paragraphs, lists, tables, links, code.</summary>
    internal sealed class HtmlToMarkdown
    {
        private static readonly HashSet<string> Drop = new HashSet<string> { "script", "style", "noscript", "nav", "header", "footer", "button", "form", "svg", "iframe" };
        private static readonly string[] DropClasses = { "breadcrumb", "toolbar", "sidenav", "side-menu", "search", "navigation", "nav-", "topnav", "menu", "skip", "feedback" };
        private static readonly string[] MainIds = { "mc-main-content", "main-content", "contentBody" };
        private static readonly HashSet<string> Void = new HashSet<string> { "br", "img", "hr", "meta", "link", "input", "area", "base", "col", "embed", "source", "track", "wbr" };
        private static readonly Regex Token = new Regex(@"<!--.*?-->|<!\[CDATA\[.*?\]\]>|<![^>]*>|<\?[^>]*>|<(/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>|[^<]+|<", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Attr = new Regex(@"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*(?:=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?", RegexOptions.Compiled);

        private readonly Func<string, string> _link;
        private readonly List<string> _out = new List<string>();
        private readonly List<int[]> _lists = new List<int[]>();   // [ordered?1:0, counter]
        private readonly Stack<string> _open = new Stack<string>();
        private int _skip, _depth;
        private int? _mainDepth;
        private bool _hasMain, _pre, _inTitle, _bareLink;
        private int _quote;
        private List<List<string>> _table;
        private List<string> _cell;
        private string _href;
        public string Title = "";

        public HtmlToMarkdown(Func<string, string> link) { _link = link; }

        public void Feed(string html)
        {
            foreach (Match m in Token.Matches(html))
            {
                var tok = m.Value;
                if (m.Groups[2].Success)
                {
                    var tag = m.Groups[2].Value.ToLowerInvariant();
                    if (m.Groups[1].Value == "/") End(tag);
                    else
                    {
                        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (Match a in Attr.Matches(m.Groups[3].Value))
                            attrs[a.Groups[1].Value] = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
                        Start(tag, attrs);
                        if (Void.Contains(tag) || m.Groups[3].Value.TrimEnd().EndsWith("/")) End(tag);
                    }
                }
                else if (tok.StartsWith("<!") || tok.StartsWith("<?")) continue;
                else Data(WebUtility.HtmlDecode(tok));
            }
        }

        private void Start(string tag, Dictionary<string, string> a)
        {
            _depth++;
            _open.Push(tag);
            string cls, role, id;
            a.TryGetValue("class", out cls); a.TryGetValue("role", out role); a.TryGetValue("id", out id);
            cls = (cls ?? "").ToLowerInvariant();
            if (_skip > 0 || Drop.Contains(tag) || DropClasses.Any(cls.Contains) || role == "navigation" || role == "search" || role == "banner") { _skip++; return; }
            if (MainIds.Contains(id) || role == "main" || tag == "main") { _hasMain = true; if (_mainDepth == null) _mainDepth = _depth; }
            switch (tag)
            {
                case "title": _inTitle = true; break;
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6": Block(new string('#', tag[1] - '0') + " "); break;
                case "p": case "div": case "dt": Block(_quote > 0 && tag == "p" ? "> " : ""); break;
                case "blockquote": _quote++; Block("> "); break;
                case "dd": Block(": "); break;
                case "ul": _lists.Add(new[] { 0, 0 }); break;
                case "ol": _lists.Add(new[] { 1, 0 }); break;
                case "li":
                    var kind = _lists.Count > 0 ? _lists[_lists.Count - 1] : new[] { 0, 0 };
                    kind[1]++;
                    Block(new string(' ', 3 * Math.Max(0, _lists.Count - 1)) + (kind[0] == 1 ? kind[1] + ". " : "- "));
                    break;
                case "br": Emit("  \n"); break;
                case "b": case "strong": Emit("**"); break;
                case "i": case "em": Emit("*"); break;
                case "code": if (!_pre) Emit("`"); break;
                case "pre": _pre = true; Block("```\n"); break;
                case "a":
                    string href; a.TryGetValue("href", out href);
                    // Drop-down heads, togglers and popups point at javascript:void(0): keep their text, not a dead link.
                    _bareLink = (href ?? "").TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
                    if (_bareLink) { _href = null; break; }
                    _href = _link(href ?? ""); Emit("["); break;
                case "img":
                    string alt, src; a.TryGetValue("alt", out alt); a.TryGetValue("src", out src);
                    if (!string.IsNullOrEmpty((alt ?? "").Trim())) Emit("![" + alt.Trim() + "](" + (src ?? "") + ")");
                    break;
                case "table": _table = new List<List<string>>(); break;
                case "tr": if (_table != null) _table.Add(new List<string>()); break;
                case "td": case "th": if (_table != null) _cell = new List<string>(); break;
            }
        }

        private void End(string tag)
        {
            if (_open.Count == 0 || !_open.Contains(tag)) return;   // stray end tag
            while (_open.Count > 0 && _open.Peek() != tag) Close(_open.Pop());   // implicitly closed (unclosed <li>, <p>)
            Close(_open.Pop());
        }

        private void Close(string tag)
        {
            _depth--;
            if (_skip > 0) { _skip--; return; }
            if (_mainDepth != null && _mainDepth > 0 && _depth < _mainDepth) _mainDepth = -1;   // left the main element: ignore the rest of the page
            switch (tag)
            {
                case "title": _inTitle = false; break;
                case "ul": case "ol": if (_lists.Count > 0) _lists.RemoveAt(_lists.Count - 1); Block(""); break;
                case "b": case "strong": Emit("**"); break;
                case "i": case "em": Emit("*"); break;
                case "code": if (!_pre) Emit("`"); break;
                case "pre": Emit("\n```"); _pre = false; Block(""); break;
                case "a": if (_bareLink) { _bareLink = false; break; } Emit(_href != null && _href.Length > 0 ? "](" + _href + ")" : "]"); _href = null; break;
                case "td": case "th":
                    if (_table != null && _cell != null && _table.Count > 0) _table[_table.Count - 1].Add(Regex.Replace(string.Concat(_cell.ToArray()), @"\s+", " ").Trim().Replace("|", "\\|"));
                    _cell = null;
                    break;
                case "table":
                    var rows = (_table ?? new List<List<string>>()).Where(r => r.Count > 0).ToList();
                    _table = null;
                    if (rows.Count > 0)
                    {
                        var width = rows.Max(r => r.Count);
                        foreach (var r in rows) while (r.Count < width) r.Add("");
                        var lines = new List<string> { "| " + string.Join(" | ", rows[0].ToArray()) + " |", "|" + string.Concat(Enumerable.Repeat("---|", width).ToArray()) };
                        lines.AddRange(rows.Skip(1).Select(r => "| " + string.Join(" | ", r.ToArray()) + " |"));
                        Block(string.Join("\n", lines.ToArray()));
                        Block("");
                    }
                    break;
                case "p": case "div": case "h1": case "h2": case "h3": case "h4": case "h5": case "h6": case "li": case "dt": case "dd": case "blockquote": Block(""); break;
            }
        }

        private void Data(string data)
        {
            if (_inTitle) { Title += data; return; }
            if (_skip > 0) return;
            Emit(_pre ? data : Regex.Replace(data, @"\s+", " "));
        }

        private bool Active { get { return !_hasMain || (_mainDepth != null && _mainDepth > 0); } }

        private void Emit(string s)
        {
            if (!Active) return;
            if (_cell != null) _cell.Add(s); else _out.Add(s);
        }

        private void Block(string prefix)
        {
            if (!Active || _cell != null) return;
            _out.Add("\n\n" + prefix);
        }

        public string Markdown()
        {
            var text = string.Concat(_out.ToArray());
            text = Regex.Replace(text, @"[ \t]+\n", "\n");
            text = Regex.Replace(text, @"(?m)^> *\n\n(?=> )", "> ");   // a quote's paragraph continues the quote line
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            text = Regex.Replace(text, @"\[\s*\]\([^)]*\)", "");   // empty links (icons)
            return text.Trim() + "\n";
        }
    }
}
