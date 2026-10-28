using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>One place a rule is exempted: the same four-part identity a dismissal uses, plus who and why.</summary>
    public sealed class ExemptionEntry
    {
        public string Topic { get; private set; }
        public string Rule { get; private set; }
        public string Text { get; private set; }
        public string Before { get; private set; }
        public string Reason { get; private set; }
        public string Who { get; private set; }
        public string When { get; private set; }

        public ExemptionEntry(string topic, string rule, string text, string before, string reason, string who, string when)
        {
            Topic = topic; Rule = rule; Text = text; Before = before; Reason = reason; Who = who; When = when;
        }
    }

    /// <summary>The loaded exemptions: the entries in file order and a hash set so <see cref="Exemptions.Covers"/> is one lookup.</summary>
    public sealed class ExemptionList
    {
        private readonly List<ExemptionEntry> _entries = new List<ExemptionEntry>();
        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);

        public System.Collections.ObjectModel.ReadOnlyCollection<ExemptionEntry> Entries { get { return _entries.AsReadOnly(); } }
        public int Count { get { return _entries.Count; } }

        internal void Add(ExemptionEntry e)
        {
            _entries.Add(e);
            _keys.Add(Exemptions.KeyOf(e.Topic, e.Rule, e.Text, e.Before));
        }

        internal bool Contains(string key) { return _keys.Contains(key); }
    }

    /// <summary>
    /// "Exempt this place": text a rule stops flagging in one topic while the rule stays on everywhere else. Shared with the
    /// team in the project (Project\HouseStyle\Exemptions.txt), one tab-separated line per place:
    /// topic, rule key, text, before, reason, who, date.
    /// </summary>
    public static class Exemptions
    {
        private const int MaxFileBytes = 1024 * 1024;
        private const int MaxTopicBytes = 2 * 1024 * 1024;
        private const int MaxRuleKey = 200;
        private const int MaxField = 2000;
        private const string Header = "# Text exempted from a rule. One per line, tab separated.";

        private static readonly string[] DeviceNames = new[] { "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        public static string FileFor(string projectRoot) { return Path.Combine(projectRoot, @"Project\HouseStyle\Exemptions.txt"); }

        /// <summary>The project's exemptions; empty when the file is missing, over 1 MB or unreadable. Bad lines are skipped.</summary>
        public static ExemptionList Load(string projectRoot)
        {
            var list = new ExemptionList();
            var file = projectRoot == null ? null : FileFor(projectRoot);
            try
            {
                if (file == null || !File.Exists(file)) return list;
                if (new FileInfo(file).Length > MaxFileBytes) { Diagnostics.Log("Exemptions.txt: file over 1 MB skipped"); return list; }
                var skipped = new List<int>();
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.Trim().Length == 0 || line.StartsWith("#")) continue;
                    var e = Parse(line);
                    if (e == null) skipped.Add(i + 1); else list.Add(e);
                }
                if (skipped.Count > 0) Diagnostics.Log(SkippedNote(skipped));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Diagnostics.Log("Exemptions.txt", ex); }
            return list;
        }

        /// <summary>The log line for skipped lines: a count and line numbers, never a field value.</summary>
        public static string SkippedNote(IList<int> lineNumbers)
        {
            return "Exemptions.txt: " + Plural.Of(lineNumbers.Count, "line") + " skipped (lines " + string.Join(", ", lineNumbers.Select(n => n.ToString()).ToArray()) + ")";
        }

        private static ExemptionEntry Parse(string line)
        {
            var f = line.Split('\t');
            if (f.Length != 7) return null;
            var topic = f[0].Replace('/', '\\');
            if (!IsSafeTopicPath(topic) || f[1].Length > MaxRuleKey) return null;
            for (var i = 2; i < 7; i++) if (f[i].Length > MaxField) return null;
            return new ExemptionEntry(topic, f[1], f[2], f[3], f[4], f[5], f[6]);
        }

        /// <summary>The topic path under Content, backslash style; null when the document is not inside a Flare project.</summary>
        public static string TopicKey(string documentPath)
        {
            try
            {
                var rel = LintExclusions.EntryFor(documentPath, false);
                return rel == null ? null : rel.Replace('/', '\\');
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        public static bool Covers(ExemptionList list, string topic, string ruleKey, string excerpt, string before)
        {
            return list != null && list.Count > 0 && list.Contains(KeyOf(topic, ruleKey, excerpt, before));
        }

        internal static string KeyOf(string topic, string rule, string text, string before)
        {
            return (topic ?? "").Replace('/', '\\').ToLowerInvariant() + "\u0001" + Flat(rule) + "\u0001" + Flat(text) + "\u0001" + Flat(before);
        }

        /// <summary>Adds an exemption; null on success, else the message to show.</summary>
        public static string Add(string projectRoot, ExemptionEntry e)
        {
            var c = Clean(e);
            if (!IsSafeTopicPath(c.Topic)) return "This topic can't be exempted: its path isn't inside the project's Content folder.";
            var file = FileFor(projectRoot);
            try
            {
                var lines = ReadLines(file);
                var key = KeyOf(c.Topic, c.Rule, c.Text, c.Before);
                if (lines.Any(l => KeyOfLine(l) == key)) return null;
                lines.Add(ToLine(c));
                return Write(file, lines);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new GuideWriteException(file, ex).Message; }
        }

        /// <summary>Removes the matching line only; null on success, else the message to show.</summary>
        public static string Remove(string projectRoot, ExemptionEntry e)
        {
            var c = Clean(e);
            var file = FileFor(projectRoot);
            try
            {
                if (!File.Exists(file)) return null;
                var key = KeyOf(c.Topic, c.Rule, c.Text, c.Before);
                var lines = ReadLines(file);
                lines.RemoveAll(l => KeyOfLine(l) == key);
                return Write(file, lines);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new GuideWriteException(file, ex).Message; }
        }

        /// <summary>How many exemptions the file holds (its non-comment lines).</summary>
        public static int Count(string projectRoot)
        {
            try
            {
                var file = FileFor(projectRoot);
                return File.Exists(file) ? ReadLines(file).Count(l => l.Trim().Length > 0 && !l.StartsWith("#")) : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        public static string HiddenTail(int n)
        {
            return n == 1 ? "1 issue is exempt." : Plural.Of(n, "issue") + " are exempt.";
        }

        /// <summary>True when a topic path is a plain relative .htm, .html or .flsnp path with no way out of Content.</summary>
        public static bool IsSafeTopicPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = path.Replace('/', '\\');
            var ext = Path.GetExtension(p);
            if (!(ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".flsnp", StringComparison.OrdinalIgnoreCase))) return false;
            if (p.StartsWith("\\") || p.IndexOf(':') >= 0) return false;
            foreach (var ch in p) if (ch < ' ' || ch == '\u007f') return false;
            foreach (var seg in p.Split('\\'))
            {
                if (seg.Length == 0 || seg == ".." || seg.EndsWith(".") || seg.EndsWith(" ")) return false;
                var dot = seg.IndexOf('.');
                var stem = (dot < 0 ? seg : seg.Substring(0, dot)).TrimEnd(' ');
                if (DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>
        /// Null when the topic still holds the text (or is too big to check); "Topic not found" or "Text not found" otherwise.
        /// Never opens a path that is not a safe one under Content, and never writes.
        /// </summary>
        public static string IsStale(string projectRoot, ExemptionEntry e)
        {
            const string noTopic = "Topic not found";
            if (e == null || projectRoot == null || !IsSafeTopicPath(e.Topic)) return noTopic;
            try
            {
                var content = Path.GetFullPath(Path.Combine(projectRoot, "Content")).TrimEnd('\\') + "\\";
                var full = Path.GetFullPath(Path.Combine(content, e.Topic.Replace('/', '\\')));
                if (full.StartsWith("\\\\") || !full.StartsWith(content, StringComparison.OrdinalIgnoreCase)) return noTopic;
                if (!File.Exists(full)) return noTopic;
                if (new FileInfo(full).Length > MaxTopicBytes) return null;
                string html;
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                    html = sr.ReadToEnd();
                return html.IndexOf((e.Before ?? "") + (e.Text ?? ""), StringComparison.Ordinal) >= 0 ? null : "Text not found";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { return noTopic; }
        }

        private static List<string> ReadLines(string file)
        {
            return File.Exists(file) ? File.ReadAllLines(file, Encoding.UTF8).ToList() : new List<string>();
        }

        private static string Write(string file, List<string> lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var all = new List<string>();
            if (!lines.Any(l => l.StartsWith("#"))) all.Add(Header);
            all.AddRange(lines);
            File.WriteAllLines(file, all.ToArray(), ProjectRules.NoBom);
            return null;
        }

        private static string KeyOfLine(string line)
        {
            if (line.StartsWith("#")) return null;
            var f = line.Split('\t');
            return f.Length < 4 ? null : KeyOf(f[0], f[1], f[2], f[3]);
        }

        private static string ToLine(ExemptionEntry e)
        {
            return string.Join("\t", new[] { e.Topic, e.Rule, e.Text, e.Before, e.Reason, e.Who, e.When });
        }

        private static ExemptionEntry Clean(ExemptionEntry e)
        {
            return new ExemptionEntry(Flat(e.Topic).Replace('/', '\\'), Flat(e.Rule), Flat(e.Text), Flat(e.Before), Flat(e.Reason), Flat(e.Who), Flat(e.When));
        }

        private static string Flat(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s) sb.Append(ch < ' ' || ch == '\u007f' ? ' ' : ch);
            return sb.ToString();
        }
    }
}
