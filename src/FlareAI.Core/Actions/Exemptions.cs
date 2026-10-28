using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>One exempted piece of text: the rule it is exempt from, the exact words and the words before them.</summary>
    public sealed class ExemptionEntry
    {
        /// <summary>The topic relative to Content, with backslashes (Sub\Topic.htm).</summary>
        public string Topic { get; set; }
        /// <summary>The rule key: the guide rule id when there is one, otherwise the rule name.</summary>
        public string Rule { get; set; }
        public string Text { get; set; }
        public string Before { get; set; }
        public string Reason { get; set; }
        public string Who { get; set; }
        /// <summary>The date it was added, yyyy-MM-dd.</summary>
        public string When { get; set; }
    }

    /// <summary>The exemptions of one project, with a lookup by topic, rule, text and context.</summary>
    public sealed class ExemptionSet
    {
        private readonly HashSet<string> _identities;

        internal ExemptionSet(IList<ExemptionEntry> entries)
        {
            Entries = new ReadOnlyCollection<ExemptionEntry>(entries);
            _identities = new HashSet<string>(entries.Select(e => Exemptions.Identity(e.Topic, e.Rule, e.Text, e.Before)), StringComparer.Ordinal);
        }

        public IList<ExemptionEntry> Entries { get; private set; }
        public int Count { get { return Entries.Count; } }

        internal bool Contains(string identity) { return _identities.Contains(identity); }
    }

    /// <summary>
    /// "Exempt this text": hides one finding in one place, where the rule itself stays on. Stored in the project
    /// (Project\HouseStyle\Exemptions.txt), one tab-separated line per exemption, so the team shares it.
    /// </summary>
    public static class Exemptions
    {
        private const string Header = "# Text exempted from a rule. One per line, tab separated.";
        private const long MaxFileBytes = 1024 * 1024;
        private const int MaxRuleLength = 200;
        private const int MaxFieldLength = 2000;

        private static readonly HashSet<string> DeviceNames = new HashSet<string>(new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        }, StringComparer.OrdinalIgnoreCase);

        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, Cached> Cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);

        private sealed class Cached
        {
            public ExemptionSet Set;
            public DateTime Written;
            public long Length;
        }

        /// <summary>The exemptions file for a project root (the folder with the .flprj).</summary>
        public static string FileFor(string root) { return Path.Combine(root, @"Project\HouseStyle\Exemptions.txt"); }

        /// <summary>The project's exemptions; empty when there is no file. Skips bad lines and logs only their count and line numbers.</summary>
        public static ExemptionSet Load(string root)
        {
            var file = root == null ? null : FileFor(root);
            if (file == null || !File.Exists(file)) return Empty();
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes)
                {
                    Diagnostics.Log("Exemptions.txt: over 1 MB, not loaded");
                    return Empty();
                }
                var all = File.ReadAllLines(file, Encoding.UTF8);
                var entries = new List<ExemptionEntry>();
                var skipped = new List<int>();
                for (var i = 0; i < all.Length; i++)
                {
                    var line = all[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var e = ParseLine(line);
                    if (e != null && Valid(e)) entries.Add(e);
                    else skipped.Add(i + 1);
                }
                if (skipped.Count > 0) Diagnostics.Log("Exemptions.txt: " + skipped.Count + " lines skipped (lines " + string.Join(", ", skipped) + ")");
                return new ExemptionSet(entries);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Diagnostics.Log("Exemptions.txt: could not be read (" + ex.GetType().Name + ")");
                return Empty();
            }
        }

        /// <summary>
        /// The project's exemptions, re-read only when the file's write time or length changes. Add and Remove
        /// also drop the cached copy, so a write is visible at once.
        /// </summary>
        public static ExemptionSet Current(string root)
        {
            if (root == null) return Load(null);
            var info = new FileInfo(FileFor(root));
            var written = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            var length = info.Exists ? info.Length : -1;
            var key = Path.GetFullPath(root);
            lock (CacheGate)
            {
                Cached cached;
                if (Cache.TryGetValue(key, out cached) && cached.Written == written && cached.Length == length) return cached.Set;
                var set = Load(root);
                Cache[key] = new Cached { Set = set, Written = written, Length = length };
                return set;
            }
        }

        /// <summary>True only for a .htm, .html or .flsnp path inside the project with no way out of it, no device name and no stream.</summary>
        public static bool IsSafeTopicPath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            var p = rel.Replace('/', '\\');
            if (!p.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".flsnp", StringComparison.OrdinalIgnoreCase)) return false;
            // A leading backslash also rules out UNC (\\server), \\?\ and \\.\ paths.
            if (p.StartsWith("\\", StringComparison.Ordinal) || p.IndexOf(':') >= 0) return false;
            foreach (var c in p) if (char.IsControl(c)) return false;
            foreach (var seg in p.Split('\\'))
            {
                if (seg == "..") return false;
                if (IsDeviceName(seg)) return false;
            }
            return true;
        }

        /// <summary>The topic for a file: its path relative to the project's Content folder, or null outside a project.</summary>
        public static string TopicFor(string path)
        {
            var rel = LintExclusions.EntryFor(path, false);
            return rel == null ? null : rel.Replace('/', '\\');
        }

        /// <summary>The rule key for a finding: its guide rule id when it has one, otherwise its rule name.</summary>
        public static string RuleKey(StyleFinding f)
        {
            return string.IsNullOrEmpty(f.RuleId) ? f.Rule : f.RuleId;
        }

        /// <summary>The identity of an exemption. The topic ignores case; rule, text and before compare exactly.</summary>
        public static string Identity(string topic, string rule, string text, string before)
        {
            return (topic ?? "").ToLowerInvariant() + "\t" + (rule ?? "") + "\t" + (text ?? "") + "\t" + (before ?? "");
        }

        /// <summary>True when the set exempts this finding in this topic. Only a lookup; it never opens a file.</summary>
        public static bool Covers(ExemptionSet set, string topic, string key, string excerpt, string before)
        {
            return set != null && set.Contains(Identity(topic, key, excerpt, before));
        }

        /// <summary>Exempts a finding in the topic it is in (its own document, or the topic that was checked).</summary>
        public static void Add(string root, string docPath, StyleFinding f, string reason, string who, DateTime when)
        {
            var topic = TopicFor(f.DocumentPath ?? docPath);
            if (topic == null) throw new InvalidOperationException("This topic is not inside a Flare project.");
            if (!IsSafeTopicPath(topic)) throw new InvalidOperationException("Exemptions can only be kept for .htm, .html and .flsnp topics.");
            var entry = new ExemptionEntry
            {
                Topic = topic,
                Rule = Clean(RuleKey(f)),
                Text = Clean(f.Excerpt),
                Before = Clean(f.Before),
                Reason = Clean(reason),
                Who = Clean(who),
                When = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
            var file = FileFor(root);
            var lines = File.Exists(file) ? File.ReadAllLines(file, Encoding.UTF8).ToList() : new List<string> { Header };
            var id = Identity(entry.Topic, entry.Rule, entry.Text, entry.Before);
            if (lines.Any(l => { var p = ParseLine(l); return p != null && Identity(p.Topic, p.Rule, p.Text, p.Before) == id; })) return;
            lines.Add(string.Join("\t", new[] { entry.Topic, entry.Rule, entry.Text, entry.Before, entry.Reason, entry.Who, entry.When }));
            Save(root, lines);
        }

        /// <summary>Removes one exemption. The other lines, comments included, are kept as they were.</summary>
        public static void Remove(string root, ExemptionEntry e)
        {
            var file = FileFor(root);
            if (!File.Exists(file)) return;
            var id = Identity(e.Topic, e.Rule, e.Text, e.Before);
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            var kept = lines.Where(l => { var p = ParseLine(l); return p == null || Identity(p.Topic, p.Rule, p.Text, p.Before) != id; }).ToList();
            if (kept.Count == lines.Length) return;
            Save(root, kept);
        }

        private static ExemptionSet Empty() { return new ExemptionSet(new List<ExemptionEntry>()); }

        /// <summary>Seven tab-separated fields, or null when the line doesn't have seven.</summary>
        private static ExemptionEntry ParseLine(string line)
        {
            var f = line.Split('\t');
            if (f.Length != 7) return null;
            return new ExemptionEntry { Topic = f[0].Replace('/', '\\'), Rule = f[1], Text = f[2], Before = f[3], Reason = f[4], Who = f[5], When = f[6] };
        }

        private static bool Valid(ExemptionEntry e)
        {
            return IsSafeTopicPath(e.Topic)
                && e.Rule.Length <= MaxRuleLength
                && new[] { e.Topic, e.Text, e.Before, e.Reason, e.Who, e.When }.All(f => f.Length <= MaxFieldLength);
        }

        private static bool IsDeviceName(string seg)
        {
            var name = seg;
            var dot = name.IndexOf('.');
            if (dot >= 0) name = name.Substring(0, dot);
            return DeviceNames.Contains(name.TrimEnd(' '));
        }

        /// <summary>Replaces every control character (tabs and line breaks included) with a space, so a field can't break its line.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var chars = s.ToCharArray();
            for (var i = 0; i < chars.Length; i++) if (char.IsControl(chars[i])) chars[i] = ' ';
            return new string(chars);
        }

        private static void Save(string root, IList<string> lines)
        {
            var file = FileFor(root);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllLines(file, lines.ToArray(), ProjectRules.NoBom);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(file, ex); }
            lock (CacheGate) Cache.Remove(Path.GetFullPath(root));
        }
    }
}
