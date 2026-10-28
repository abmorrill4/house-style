using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// Text the author exempted from one rule on one topic ("this is fine here"). Unlike a rule turned off, the rule still
    /// checks the rest of the topic. Kept in the project (Project\HouseStyle\Exemptions.txt) so the team shares it. One line
    /// per exemption, tab separated: topic, rule, text, before, reason, who, date. Lines starting with # are comments.
    /// </summary>
    public static class Exemptions
    {
        private const long MaxFileBytes = 1048576;
        private const int MaxRuleChars = 200;
        private const int MaxFieldChars = 2000;
        private const string Header = "# Text exempted from a rule. One per line, tab separated.\r\n";
        private static readonly object Gate = new object();

        private static readonly HashSet<string> DeviceNames = new HashSet<string>(
            new[] { "CON", "PRN", "AUX", "NUL" }
                .Concat(Enumerable.Range(1, 9).SelectMany(n => new[] { "COM" + n, "LPT" + n })),
            StringComparer.OrdinalIgnoreCase);

        /// <summary>The exemptions file for the project that holds <paramref name="anyPath"/>; null outside a project.</summary>
        public static string FileFor(string anyPath)
        {
            var root = StyleEngine.FlareProjectRoot(anyPath);
            return root == null ? null : Path.Combine(root, @"Project\HouseStyle\Exemptions.txt");
        }

        /// <summary>The exemptions for the project that holds <paramref name="anyPath"/>; empty when there are none. Bad lines are skipped and counted in the log.</summary>
        public static IList<Exemption> Load(string anyPath)
        {
            var file = FileFor(anyPath);
            if (file == null || !File.Exists(file)) return new List<Exemption>();
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes)
                {
                    Diagnostics.Log("Exemptions.txt: over 1 MB, not loaded");
                    return new List<Exemption>();
                }
                var list = new List<Exemption>();
                var skipped = new List<int>();
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (IsBlankOrComment(lines[i])) continue;
                    Exemption e;
                    if (TryParse(lines[i], out e)) list.Add(e); else skipped.Add(i + 1);
                }
                LogSkipped(skipped);
                return list;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                Diagnostics.Log("Exemptions.txt", ex);
                return new List<Exemption>();
            }
        }

        /// <summary>The topic a finding is exempted on: its file (or the checked topic) relative to Content, with \ separators; null outside a project's Content.</summary>
        public static string TopicKey(StyleFinding f, string sourcePath)
        {
            var path = f.DocumentPath ?? sourcePath;
            if (string.IsNullOrEmpty(path)) return null;
            var entry = LintExclusions.EntryFor(path, false);
            return entry == null ? null : entry.Replace('/', '\\');
        }

        /// <summary>The rule a finding is exempted from: its permanent id when it has one, else its name.</summary>
        public static string RuleKey(StyleFinding f) { return string.IsNullOrEmpty(f.RuleId) ? f.Rule : f.RuleId; }

        /// <summary>Adds an exemption to the project that holds <paramref name="anyPath"/>. Dated today.</summary>
        public static void Add(string anyPath, string topic, string rule, string text, string before, string reason, string who)
        {
            var file = FileFor(anyPath);
            if (file == null) throw new InvalidOperationException("This topic is not inside a Flare project.");
            topic = (topic ?? "").Replace('/', '\\');
            if (!IsSafeTopicPath(topic)) throw new ArgumentException("This topic cannot be exempted: " + topic, "topic");
            if ((rule ?? "").Length > MaxRuleChars || new[] { text, before, reason, who }.Any(s => (s ?? "").Length > MaxFieldChars))
                throw new ArgumentException("An exemption field is too long.");
            var e = new Exemption(topic, rule, text, before, reason, who, DateTime.Now.ToString("yyyy-MM-dd"));
            var line = string.Join("\t", Fields(e)) + "\r\n";
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var existing = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : Header;
                    if (existing.Length > 0 && !existing.EndsWith("\n")) existing += "\r\n";
                    File.WriteAllText(file, existing + line, ProjectRules.NoBom);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(file, ex); }
            }
        }

        /// <summary>Removes the first line equal to <paramref name="e"/>; the other lines stay as they are.</summary>
        public static void Remove(string anyPath, Exemption e)
        {
            var file = FileFor(anyPath);
            if (file == null || e == null || !File.Exists(file)) return;
            lock (Gate)
            {
                try
                {
                    var lines = File.ReadAllLines(file, Encoding.UTF8).ToList();
                    var i = lines.FindIndex(l => SameAs(l, e));
                    if (i < 0) return;
                    lines.RemoveAt(i);
                    File.WriteAllLines(file, lines.ToArray(), ProjectRules.NoBom);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(file, ex); }
            }
        }

        /// <summary>
        /// True when an exemption covers a finding: the same topic (case ignored), rule, text and context (exact).
        /// Only the one copy of a repeated word that the author exempted is covered, because the context differs.
        /// </summary>
        public static bool Covers(IList<Exemption> list, string topic, string rule, string excerpt, string before)
        {
            if (list == null || list.Count == 0) return false;
            var t = (topic ?? "").Replace('/', '\\');
            foreach (var e in list)
                if (string.Equals(e.Topic.Replace('/', '\\'), t, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Rule, rule ?? "", StringComparison.Ordinal)
                    && string.Equals(e.Text, excerpt ?? "", StringComparison.Ordinal)
                    && string.Equals(e.Before, before ?? "", StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// True for a topic path House Style will read and exempt: a .htm, .html or .flsnp file under Content, with no
        /// parent segment, no drive, no UNC or rooted path, no stream, no control characters and no DOS device name.
        /// </summary>
        public static bool IsSafeTopicPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = path.Replace('/', '\\');
            if (!(p.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".flsnp", StringComparison.OrdinalIgnoreCase))) return false;
            if (p.StartsWith("\\") || p.IndexOf(':') >= 0) return false;
            if (p.Any(c => char.IsControl(c))) return false;
            foreach (var seg in p.Split('\\'))
            {
                if (seg == "..") return false;
                if (DeviceNames.Contains(seg.Split('.')[0].TrimEnd(' '))) return false;
            }
            return true;
        }

        private static bool TryParse(string line, out Exemption e)
        {
            e = null;
            var f = line.Split('\t');
            if (f.Length < 7) return false;
            if (f[1].Length > MaxRuleChars) return false;
            for (var i = 0; i < 7; i++)
                if (i != 1 && f[i].Length > MaxFieldChars) return false;
            var topic = f[0].Replace('/', '\\');
            if (!IsSafeTopicPath(topic)) return false;
            e = new Exemption(topic, f[1], f[2], f[3], f[4], f[5], f[6]);
            return true;
        }

        private static bool SameAs(string line, Exemption e)
        {
            if (IsBlankOrComment(line)) return false;
            var f = line.Split('\t');
            if (f.Length < 7) return false;
            f[0] = f[0].Replace('/', '\\');
            var want = Fields(e);
            for (var i = 0; i < want.Length; i++)
                if (!string.Equals(Clean(f[i]), want[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static string[] Fields(Exemption e)
        {
            return new[] { Clean(e.Topic.Replace('/', '\\')), Clean(e.Rule), Clean(e.Text), Clean(e.Before), Clean(e.Reason), Clean(e.Who), Clean(e.When) };
        }

        /// <summary>Tabs, line breaks and other control characters become spaces, so one exemption is one line.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(char.IsControl(c) ? ' ' : c);
            return sb.ToString();
        }

        private static bool IsBlankOrComment(string line) { return string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"); }

        private static void LogSkipped(List<int> skipped)
        {
            if (skipped.Count == 0) return;
            var numbers = string.Join(", ", skipped.Select(n => n.ToString()));
            Diagnostics.Log("Exemptions.txt: " + skipped.Count + (skipped.Count == 1 ? " line skipped (line " : " lines skipped (lines ") + numbers + ")");
        }
    }

    /// <summary>One exemption: the text (<see cref="Text"/>) is exempt from <see cref="Rule"/> on <see cref="Topic"/> in the context <see cref="Before"/>.</summary>
    public sealed class Exemption
    {
        public Exemption(string topic, string rule, string text, string before, string reason, string who, string when)
        {
            Topic = topic ?? "";
            Rule = rule ?? "";
            Text = text ?? "";
            Before = before ?? "";
            Reason = reason ?? "";
            Who = who ?? "";
            When = when ?? "";
        }

        /// <summary>Topic path relative to Content, with \ separators.</summary>
        public string Topic { get; private set; }
        /// <summary>The rule's permanent id, or its name.</summary>
        public string Rule { get; private set; }
        /// <summary>The exempted words.</summary>
        public string Text { get; private set; }
        /// <summary>Up to 40 chars of run text before the words (disambiguates repeats).</summary>
        public string Before { get; private set; }
        public string Reason { get; private set; }
        public string Who { get; private set; }
        /// <summary>Date added, yyyy-MM-dd.</summary>
        public string When { get; private set; }
    }
}
