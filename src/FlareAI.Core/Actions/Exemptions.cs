using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// Text a writer exempted from one rule in one topic ("this is intended here"), so the rule stays on everywhere else.
    /// Unlike <see cref="LintExclusions"/>, nothing is turned off: an entry hides one exact finding and no other.
    /// One entry per line in Project\HouseStyle\Exemptions.txt, tab separated: Topic, Rule, Text, Before, Reason, Who, When.
    /// A line that fails <see cref="Load"/> is skipped; the log gets a count and line numbers, never the line itself.
    /// </summary>
    public static class Exemptions
    {
        private const string Header = "# Text exempted from a rule. One per line, tab separated.";
        private const int MaxFileBytes = 1024 * 1024;
        private const int MaxRuleChars = 200;
        private const int MaxFieldChars = 2000;
        private const int MaxStaleTopics = 200;
        private const int MaxTopicBytes = 2 * 1024 * 1024;

        /// <summary>Receives the one-line notes from <see cref="Load"/>. Only counts and line numbers go to it.</summary>
        public static Action<string> LogSink = Diagnostics.Log;

        /// <summary>The list's file for a project root (the folder with the .flprj).</summary>
        public static string FileFor(string projectRoot) { return Path.Combine(projectRoot, @"Project\HouseStyle\Exemptions.txt"); }

        /// <summary>One exempted text. Topic is relative to Content with backslashes; When is yyyy-MM-dd.</summary>
        public sealed class Entry
        {
            public Entry(string topic, string rule, string text, string before, string reason, string who, string when)
            {
                Topic = topic; Rule = rule; Text = text; Before = before; Reason = reason; Who = who; When = when;
            }

            public string Topic { get; private set; }
            public string Rule { get; private set; }
            public string Text { get; private set; }
            public string Before { get; private set; }
            public string Reason { get; private set; }
            public string Who { get; private set; }
            public string When { get; private set; }
        }

        /// <summary>The topic path an exemption stores: the file relative to Content, with backslashes; null outside Content.</summary>
        public static string TopicPathFor(string path) { return NormalizeTopic(LintExclusions.EntryFor(path, false)); }

        /// <summary>The entries for the project that holds <paramref name="anyPath"/>, with the file's stamp; empty when there are none.</summary>
        public static ExemptionList Load(string anyPath)
        {
            var root = StyleEngine.FlareProjectRoot(anyPath);
            var file = root == null ? null : FileFor(root);
            if (file == null || !File.Exists(file)) return new ExemptionList(new List<Entry>(), 0);
            var info = new FileInfo(file);
            var stamp = info.LastWriteTimeUtc.Ticks;
            if (info.Length > MaxFileBytes)
            {
                LogSink("Exemptions.txt: file over 1 MB, not read");
                return new ExemptionList(new List<Entry>(), stamp);
            }
            var entries = new List<Entry>();
            var skipped = new List<int>();
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("#", StringComparison.Ordinal) || line.Trim().Length == 0) continue;
                var entry = Parse(line);
                if (entry == null) skipped.Add(i + 1); else entries.Add(entry);
            }
            if (skipped.Count > 0)
                LogSink("Exemptions.txt: " + skipped.Count + " lines skipped (lines " + string.Join(", ", skipped.Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray()) + ")");
            return new ExemptionList(entries, stamp);
        }

        /// <summary>True when the list exempts this exact finding: the same topic, rule, text and context. Nothing else matches.</summary>
        public static bool Covers(ExemptionList list, string relPath, string rule, string excerpt, string before)
        {
            return list != null && relPath != null && list.Has(relPath, rule, Clean(excerpt), Clean(before));
        }

        /// <summary>Adds an exemption to the list of the project that holds <paramref name="anyPath"/>.</summary>
        public static void Add(string anyPath, string topicRel, string rule, string text, string before, string reason, string who, DateTime whenUtc)
        {
            var e = new Entry(Clean(NormalizeTopic(topicRel)), Clean(rule), Clean(text), Clean(before), Clean(reason), Clean(who), whenUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!Fits(e)) throw new ArgumentException("An exemption needs a safe topic path, a rule and text, within the length limits.");
            var file = FileOfProject(anyPath);
            try
            {
                var lines = Existing(file);
                lines.Add(Line(e));
                WriteAll(file, lines);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(file, ex); }
        }

        /// <summary>Removes the first line that matches the entry's seven fields; comments and other lines stay.</summary>
        public static void Remove(string anyPath, Entry entry)
        {
            var file = FileOfProject(anyPath);
            if (!File.Exists(file)) return;
            var target = Line(entry);
            try
            {
                var lines = Existing(file);
                var i = lines.FindIndex(l => !l.StartsWith("#", StringComparison.Ordinal) && SameLine(l, target));
                if (i < 0) return;
                lines.RemoveAt(i);
                WriteAll(file, lines);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(file, ex); }
        }

        /// <summary>Strips tabs, line breaks and other control characters to spaces, so one entry stays one line.</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var chars = s.ToCharArray();
            for (var i = 0; i < chars.Length; i++) if (char.IsControl(chars[i])) chars[i] = ' ';
            return new string(chars);
        }

        /// <summary>
        /// The one validator for a topic path from the list: a .htm, .html or .flsnp file under Content, with no parent
        /// segment, no rooted, UNC or device prefix, no drive or alternate-stream colon, no control character and no DOS device name.
        /// </summary>
        public static bool IsSafeTopicPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            var s = p.Replace('/', '\\');
            var lower = s.ToLowerInvariant();
            if (!(lower.EndsWith(".htm", StringComparison.Ordinal) || lower.EndsWith(".html", StringComparison.Ordinal) || lower.EndsWith(".flsnp", StringComparison.Ordinal))) return false;
            if (s.StartsWith("\\", StringComparison.Ordinal) || s.IndexOf(':') >= 0) return false;
            if (s.Any(c => char.IsControl(c))) return false;
            foreach (var seg in s.Split('\\'))
            {
                if (seg == "..") return false;
                var dot = seg.IndexOf('.');
                if (IsDeviceName(dot < 0 ? seg : seg.Substring(0, dot))) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether each entry's text is still in its topic. Read-only: never writes, never opens a path that fails
        /// <see cref="IsSafeTopicPath"/>, never reads outside Content, and checks at most 200 distinct topics.
        /// </summary>
        public static List<StaleNote> StaleNotes(string projectRoot, IList<Entry> entries)
        {
            var content = projectRoot == null ? null : Path.Combine(projectRoot, "Content") + "\\";
            var topics = new Dictionary<string, TopicText>(StringComparer.OrdinalIgnoreCase);
            var notes = new List<StaleNote>();
            foreach (var e in entries)
            {
                var topic = NormalizeTopic(e.Topic) ?? "";
                TopicText t;
                if (!topics.TryGetValue(topic, out t))
                {
                    t = topics.Count >= MaxStaleTopics ? new TopicText { Note = StaleNote.None } : ReadTopic(content, topic);
                    topics[topic] = t;
                }
                notes.Add(t.Text == null ? t.Note : (Holds(t.Text, e) ? StaleNote.None : StaleNote.TextNotFound));
            }
            return notes;
        }

        private sealed class TopicText
        {
            public StaleNote Note;
            public string Text;
        }

        private static TopicText ReadTopic(string content, string topic)
        {
            var missing = new TopicText { Note = StaleNote.TopicNotFound };
            if (content == null || !IsSafeTopicPath(topic)) return missing;
            try
            {
                var full = Path.GetFullPath(Path.Combine(content, topic));
                if (!full.StartsWith(content, StringComparison.OrdinalIgnoreCase) || full.StartsWith(@"\\", StringComparison.Ordinal)) return missing;
                if (!File.Exists(full)) return missing;
                using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var bytes = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int n;
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        bytes.Write(buffer, 0, n);
                        if (bytes.Length > MaxTopicBytes) return new TopicText { Note = StaleNote.None };
                    }
                    return new TopicText { Note = StaleNote.None, Text = Encoding.UTF8.GetString(bytes.ToArray()) };
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException) { return missing; }
        }

        // Fallback: matches the raw topic markup, not the plain text the lint checks, so markup between words can hide a match.
        private static bool Holds(string text, Entry e)
        {
            var words = e.Text ?? "";
            return text.IndexOf((e.Before ?? "") + words, StringComparison.Ordinal) >= 0 || text.IndexOf(words, StringComparison.Ordinal) >= 0;
        }

        private static Entry Parse(string line)
        {
            var f = line.Split('\t');
            if (f.Length != 7) return null;
            var e = new Entry(NormalizeTopic(f[0]), f[1], f[2], f[3], f[4], f[5], f[6]);
            return Fits(e) ? e : null;
        }

        private static bool Fits(Entry e)
        {
            return IsSafeTopicPath(e.Topic) && e.Rule.Length > 0 && e.Text.Length > 0 && e.Rule.Length <= MaxRuleChars
                && new[] { e.Topic, e.Text, e.Before, e.Reason, e.Who, e.When }.All(x => x.Length <= MaxFieldChars);
        }

        private static bool IsDeviceName(string name)
        {
            var n = name.TrimEnd(' ').ToUpperInvariant();
            if (n == "CON" || n == "PRN" || n == "AUX" || n == "NUL") return true;
            return n.Length == 4 && (n.StartsWith("COM", StringComparison.Ordinal) || n.StartsWith("LPT", StringComparison.Ordinal)) && n[3] >= '1' && n[3] <= '9';
        }

        private static string NormalizeTopic(string s) { return s == null ? null : s.Replace('/', '\\'); }

        private static string Line(Entry e)
        {
            return string.Join("\t", new[] { NormalizeTopic(e.Topic), e.Rule, e.Text, e.Before, e.Reason, e.Who, e.When });
        }

        private static bool SameLine(string line, string target)
        {
            var f = line.Split('\t');
            if (f.Length != 7) return false;
            f[0] = NormalizeTopic(f[0]);
            return string.Join("\t", f) == target;
        }

        private static string FileOfProject(string anyPath)
        {
            var root = StyleEngine.FlareProjectRoot(anyPath);
            if (root == null) throw new InvalidOperationException("Not in a Flare project: " + anyPath);
            return FileFor(root);
        }

        private static List<string> Existing(string file)
        {
            return File.Exists(file) ? File.ReadAllLines(file).Where(l => l != Header).ToList() : new List<string>();
        }

        private static void WriteAll(string file, IList<string> lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var all = new List<string> { Header };
            all.AddRange(lines);
            File.WriteAllLines(file, all.ToArray());
        }
    }

    /// <summary>The loaded exemptions: the entries in file order, the file's stamp (0 without a file), and the lookup keys.</summary>
    public sealed class ExemptionList
    {
        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);

        internal ExemptionList(IList<Exemptions.Entry> entries, long stamp)
        {
            Entries = entries;
            Stamp = stamp;
            foreach (var e in entries) _keys.Add(KeyOf(e.Topic, e.Rule, e.Text, e.Before));
        }

        public IList<Exemptions.Entry> Entries { get; private set; }

        public long Stamp { get; private set; }

        internal bool Has(string topic, string rule, string text, string before)
        {
            return _keys.Contains(KeyOf(topic, rule, text, before));
        }

        // The topic is the only field compared without case: "\t" cannot occur in any field (Clean and Parse keep it out).
        private static string KeyOf(string topic, string rule, string text, string before)
        {
            return (topic ?? "").Replace('/', '\\').ToLowerInvariant() + "\t" + rule + "\t" + text + "\t" + before;
        }
    }

    /// <summary>Why an exemption no longer finds its text. None means the text is still there, or it could not be checked.</summary>
    public enum StaleNote { None, TextNotFound, TopicNotFound }
}
