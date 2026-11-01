using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// Findings the author dismissed ("not an issue here"), remembered per file by rule + text + context so they
    /// stay hidden on later checks and across sessions. Stored in %APPDATA%\FlareAI\dismissed.json (personal, <see cref="Paths.Dismissed"/>).
    /// </summary>
    public sealed class Dismissals
    {
        private readonly string _path;
        private readonly HashSet<string> _keys;
        private readonly object _gate = new object();

        private Dismissals(string path, IEnumerable<string> keys)
        {
            _path = path;
            _keys = new HashSet<string>(keys ?? new string[0], StringComparer.Ordinal);
        }

        public static Dismissals Load(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                    return new Dismissals(path, JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path, Encoding.UTF8)));
            }
            catch (Exception ex) { Diagnostics.Log("dismissed.json", ex); }
            return new Dismissals(path, null);
        }

        public bool IsDismissed(StyleFinding f, string topicPath)
        {
            lock (_gate) return _keys.Contains(Key(f, topicPath));
        }

        public void Dismiss(StyleFinding f, string topicPath)
        {
            lock (_gate)
            {
                if (!_keys.Add(Key(f, topicPath)) || _path == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, JsonConvert.SerializeObject(new List<string>(_keys), Formatting.Indented), ProjectRules.NoBom);
                }
                catch (Exception ex) { Diagnostics.Log("dismissed.json", ex); }
            }
        }

        /// <summary>Brings a dismissed finding back. False when the file couldn't be written (the finding stays restored for this session).</summary>
        public bool Restore(StyleFinding f, string topicPath)
        {
            lock (_gate)
            {
                if (!_keys.Remove(Key(f, topicPath)) || _path == null) return true;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, JsonConvert.SerializeObject(new List<string>(_keys), Formatting.Indented), ProjectRules.NoBom);
                    return true;
                }
                catch (Exception ex) { Diagnostics.Log("dismissed.json", ex); return false; }
            }
        }

        internal static string Key(StyleFinding f, string topicPath)
        {
            var file = (f.DocumentPath ?? topicPath ?? "").ToLowerInvariant();
            return file + "|" + f.Rule + "|" + f.Excerpt + "|" + (f.Before ?? "");
        }
    }

    /// <summary>
    /// "Ignore this rule in this project": the project's own style layer (Content/Resources/StyleGuide/overrides.ini),
    /// so the decision is shared with the team through source control. Linter rules become Vale "Rule = NO" lines;
    /// AI rule names (free text) become "# ai-ignore: name" lines that the plugin filters on.
    /// </summary>
    public static class ProjectRules
    {
        private const string AiTextPrefix = "# ai-ignore-text:";

        public static string OverridesPath(string anyProjectPath)
        {
            var dir = string.IsNullOrEmpty(anyProjectPath) ? null : new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(anyProjectPath)));
            while (dir != null && (!dir.Exists || dir.GetFiles("*.flprj").Length == 0)) dir = dir.Parent;   // a project not on this machine: null
            return dir == null ? null : Path.Combine(dir.FullName, @"Content\Resources\StyleGuide\overrides.ini");
        }

        internal static readonly Encoding NoBom = new UTF8Encoding(false);

        public static bool IsLinterRule(StyleFinding f) { return f.Layer != "ai" && f.Rule != null && f.Rule.Contains("."); }

        /// <param name="setting">off, suggestion, warning or error.</param>
        /// <param name="reason">Why (recorded with <paramref name="by"/> and today's date above the line, for the turned-off rules list).</param>
        public static void Ignore(string anyProjectPath, StyleFinding f, string setting = "off", string reason = null, string by = null)
        {
            var path = OverridesPath(anyProjectPath);
            if (path == null) throw new InvalidOperationException("This topic is not inside a Flare project.");
            // A guide rule by its permanent id (lint and AI alike); an uncited AI judgment by its name. One line each, always.
            var line = f.RuleId != null ? OneLine(f.RuleId) + " = " + OneLine(setting)
                     : IsLinterRule(f) ? OneLine(f.Rule) + " = " + (setting == "off" ? "NO" : OneLine(setting))
                     : "# ai-ignore: " + OneLine(f.Rule);
            // An AI judgment with no rule also remembers what it flagged, so a reworded version stays off.
            var textLine = f.RuleId == null && !IsLinterRule(f) && !string.IsNullOrEmpty(f.Excerpt)
                ? AiTextPrefix + " " + OneLine(f.Rule) + " | " + OneLine(f.Excerpt) : null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿') : "# Rules disabled for this project (House Style, \"Disable Rule\"). Vale syntax: Style.Rule = NO\r\n";
                var addLine = existing.IndexOf(line, StringComparison.OrdinalIgnoreCase) < 0;
                var addText = textLine != null && existing.IndexOf(textLine, StringComparison.OrdinalIgnoreCase) < 0;
                if (!addLine && !addText) return;
                var meta = by == null && string.IsNullOrEmpty(reason) ? "" : FlareAI.Core.Style.RuleOverrides.MetaLine(by, DateTime.Now, reason) + "\r\n";
                var add = (addLine ? meta + line + "\r\n" : "") + (addText ? textLine + "\r\n" : "");
                File.WriteAllText(path, existing.TrimEnd() + "\r\n" + add, NoBom); // ini readers choke on a BOM
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new FlareAI.Core.Style.GuideWriteException(path, ex); }
        }

        private static string OneLine(string s) { return (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(); }

        /// <summary>AI rule names this project ignores (lower case).</summary>
        public static HashSet<string> IgnoredAiRules(string anyProjectPath)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = OverridesPath(anyProjectPath);
            if (path == null || !File.Exists(path)) return set;
            foreach (var l in File.ReadAllLines(path, Encoding.UTF8))
                if (l.StartsWith("# ai-ignore:", StringComparison.OrdinalIgnoreCase)) set.Add(l.Substring("# ai-ignore:".Length).Trim());
            return set;
        }

        /// <summary>What disabled AI issues flagged: (rule name, excerpt) pairs.</summary>
        public static List<KeyValuePair<string, string>> IgnoredAiTexts(string anyProjectPath)
        {
            var list = new List<KeyValuePair<string, string>>();
            var path = OverridesPath(anyProjectPath);
            if (path == null || !File.Exists(path)) return list;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var l = raw.Trim().TrimStart('﻿');
                if (!l.StartsWith(AiTextPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = l.Substring(AiTextPrefix.Length);
                var bar = rest.IndexOf(" | ", StringComparison.Ordinal);
                if (bar < 0) continue;
                var name = rest.Substring(0, bar).Trim();
                var excerpt = rest.Substring(bar + 3).Trim();
                if (name.Length == 0 || excerpt.Length == 0) continue;
                list.Add(new KeyValuePair<string, string>(name, excerpt));
            }
            return list;
        }

        /// <summary>True when an AI issue with no rule is about the same thing, in the same place, as one the project disabled.</summary>
        public static bool IsIgnoredAiTopic(IEnumerable<KeyValuePair<string, string>> ignored, StyleFinding f)
        {
            if (ignored == null || f == null || f.Layer != "ai" || f.RuleId != null || string.IsNullOrEmpty(f.Excerpt)) return false;
            foreach (var p in ignored)
                if (SameCategory(p.Key, f.Rule) && SameText(p.Value, f.Excerpt)) return true;
            return false;
        }

        internal static bool SameCategory(string a, string b)
        {
            var sa = Stems(a);
            foreach (var s in Stems(b)) if (sa.Contains(s)) return true;
            return false;
        }

        private static HashSet<string> Stems(string s)
        {
            var set = new HashSet<string>();
            foreach (var w in System.Text.RegularExpressions.Regex.Split((s ?? "").ToLowerInvariant(), @"[^\p{L}]+"))
                if (w.Length >= 3) set.Add(w.Length > 5 ? w.Substring(0, 5) : w);
            return set;
        }

        internal static bool SameText(string a, string b)
        {
            a = (a ?? "").Trim(); b = (b ?? "").Trim();
            if (a.Length == 0 || b.Length == 0) return false;
            return a.Equals(b, StringComparison.OrdinalIgnoreCase)
                || a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0
                || b.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
