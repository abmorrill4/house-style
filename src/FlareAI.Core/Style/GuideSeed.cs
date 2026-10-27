using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace FlareAI.Core.Style
{
    /// <summary>
    /// The writer's own style guide (%APPDATA%\FlareAI\StyleGuide): a layer after the bundled guide that holds only the
    /// writer's changes (override rows with who/when/why, rules added with Build style guide, topics they edited). The
    /// bundled Google and Company layers always load from the plugin, so plugin updates reach every writer.
    /// Earlier versions copied the bundled Company layer here and used the copy in its place; <see cref="Upgrade"/>
    /// reduces such a copy to what the writer changed.
    /// </summary>
    public static class GuideSeed
    {
        private static readonly string StyleFolderRel = Path.Combine(Path.Combine("Content", "Resources"), "StyleGuide");
        private static readonly string GoogleFolder = Path.Combine(StyleFolderRel, "Google");
        private static readonly string MarkerRel = Path.Combine("FlareAI", "personal.txt");
        private static readonly string LayerRel = @"FlareAI\layer.txt";
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The writer's own rule changes (Rule / Setting / Reason / Changed by / Date), under Content\Resources\StyleGuide.</summary>
        public const string PersonalTopic = "My-rule-changes.htm";

        /// <summary>True when the folder is a style guide project (it has a .flprj).</summary>
        public static bool IsSeeded(string root)
        {
            try { return !string.IsNullOrEmpty(root) && Directory.Exists(root) && Directory.GetFiles(root, "*.flprj").Length > 0; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// True when the guide is a writer's own guide in the current layout: a layer after the bundled guide, holding only
        /// the writer's changes. Its topics stand in for the bundled topics of the same name.
        /// </summary>
        public static bool IsPersonal(string root)
        {
            try { return !string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, MarkerRel)); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Creates <paramref name="target"/> as an empty personal guide (a .flprj, the StyleGuide folder, FlareAI\layer.txt
        /// named like <paramref name="source"/>'s layer, and the personal marker) unless it already is a guide project, in
        /// which case an older full copy is upgraded. No bundled rule topics are copied.
        /// Throws <see cref="GuideWriteException"/> naming the folder when it can't be written.
        /// </summary>
        public static void Ensure(string source, string target)
        {
            lock (Gate)
            {
                if (IsSeeded(target)) { Upgrade(source, target); return; }
                try
                {
                    Directory.CreateDirectory(Path.Combine(target, StyleFolderRel));
                    WriteLayerName(source, target);
                    WriteMarker(target);
                    // The .flprj last: a seed that fails part way is completed next time.
                    File.WriteAllText(Path.Combine(target, "StyleGuide.flprj"), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<CatapultProject Version=\"1\" />\r\n");
                    Diagnostics.Log("Created your style guide at " + target + " (your changes only; the bundled guide stays in the plugin)");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is System.Security.SecurityException)
                {
                    throw new GuideWriteException(target, ex);
                }
            }
        }

        // ---- upgrade of a full copy ------------------------------------------------------------------------------------

        /// <summary>
        /// Makes <paramref name="target"/> a personal guide in the current layout. An older full copy of
        /// <paramref name="source"/> (no personal marker) is first backed up to <c>&lt;target&gt;.backup-&lt;date&gt;</c>, then reduced
        /// to what the writer changed: override rows with a Changed by / Date (or not in the bundled tables) move to
        /// <see cref="PersonalTopic"/>; files with no bundled counterpart (From-our-content.htm, prompts) stay; a topic or
        /// project file the bundled guide also has stays only when it holds rows or ids the bundled one lacks. Idempotent.
        /// Returns true when the guide is (now) personal; false when it isn't a guide, there is no bundled guide to compare
        /// with, or the upgrade failed (logged once; the backup holds the copy as it was, and it isn't tried again this session).
        /// </summary>
        public static bool Upgrade(string source, string target)
        {
            if (IsPersonal(target)) return true;
            if (!IsSeeded(target) || string.IsNullOrEmpty(source) || !Directory.Exists(source)) return false;
            lock (Gate)
            {
                if (IsPersonal(target)) return true;
                if (Failed.Contains(Path.GetFullPath(target))) return false;
                try
                {
                    var backup = Backup(target);
                    var log = new List<string>();
                    Reduce(source, target, log);
                    WriteMarker(target);
                    Diagnostics.Log("Upgraded your style guide at " + target + " to hold only your changes (backup: " + backup + ")" +
                                    (log.Count == 0 ? "; nothing was yours to keep." : ":\n  " + string.Join("\n  ", log.ToArray())));
                    return true;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("Upgrading your style guide at " + target, ex);
                    Failed.Add(Path.GetFullPath(target));
                    return false;
                }
            }
        }

        private static string Backup(string target)
        {
            var root = Path.GetFullPath(target).TrimEnd('\\', '/');
            var backup = root + ".backup-" + DateTime.Now.ToString("yyyy-MM-dd");
            for (var i = 2; Directory.Exists(backup); i++) backup = root + ".backup-" + DateTime.Now.ToString("yyyy-MM-dd") + "-" + i;
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var to = backup + file.Substring(root.Length);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(file, to);
            }
            return backup;
        }

        private static void Reduce(string source, string target, List<string> log)
        {
            var root = Path.GetFullPath(target).TrimEnd('\\', '/');
            var bundled = string.IsNullOrEmpty(source) || !Directory.Exists(source) ? null : Path.GetFullPath(source).TrimEnd('\\', '/');
            var defaults = bundled == null ? new HashSet<string>() : BundledOverrideRows(bundled);
            var keptRows = new List<RuleOverride>();
            var drop = new List<Action>();   // rewrites and deletions run after the kept rows are written: a failure part way loses nothing
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = file.Substring(root.Length + 1);
                if (rel.IndexOf(Path.DirectorySeparatorChar) < 0 && rel.EndsWith(".flprj", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.Equals(LayerRel, StringComparison.OrdinalIgnoreCase)) continue;
                if (Under(rel, GoogleFolder)) continue;   // never copied; leave anything there alone
                var theirs = bundled == null ? null : Path.Combine(bundled, rel);
                var isTopic = Under(rel, StyleFolderRel) && (rel.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
                if (isTopic) ReduceTopic(file, rel, theirs != null && File.Exists(theirs) ? theirs : null, defaults, keptRows, drop, log);
                else if (theirs == null || !File.Exists(theirs)) log.Add("kept " + rel + " (yours)");
                else if (SameFile(file, theirs)) { var f = file; drop.Add(() => Delete(f)); }
                else log.Add("kept " + rel + " (differs from the bundled one)");
            }
            if (keptRows.Count > 0)
            {
                var topic = Path.Combine(Path.Combine(root, StyleFolderRel), PersonalTopic);
                foreach (var r in keptRows) GuideOverrides.Put(topic, r.RuleId, r.Setting, r.Note, r.By, r.Date);
                log.Add("kept " + keptRows.Count + " rule change(s) in " + Path.Combine(StyleFolderRel, PersonalTopic) + ": " + string.Join(", ", keptRows.Select(r => r.RuleId + "=" + r.Setting).ToArray()));
            }
            foreach (var step in drop) step();
            RemoveEmptyFolders(root, root);
            Directory.CreateDirectory(Path.Combine(root, StyleFolderRel));
            if (!File.Exists(Path.Combine(root, LayerRel))) WriteLayerName(source, root);
        }

        /// <summary>
        /// One topic of the old copy: its override rows that the writer made move to the personal topic; what is left stays
        /// only when the bundled topic lacks some of it (never as the same name as a bundled topic with override rows,
        /// which it would hide).
        /// </summary>
        private static void ReduceTopic(string file, string rel, string bundled, HashSet<string> defaults, List<RuleOverride> keptRows, List<Action> drop, List<string> log)
        {
            if (bundled == null) { log.Add("kept " + rel + " (yours)"); return; }
            XmlDocument mine;
            try { mine = LoadXml(file); }
            catch (XmlException) { log.Add("kept " + rel + " (not well-formed XML)"); return; }

            foreach (var r in GuideOverrides.Read(file, mine))
                if (!string.IsNullOrEmpty(r.By) || !string.IsNullOrEmpty(r.Date) || !defaults.Contains(Key(r.RuleId, r.Setting)))
                    keptRows.Add(r);
            var hadTables = RemoveOverrideTables(mine);
            XmlDocument theirs;
            try { theirs = LoadXml(bundled); }
            catch (XmlException) { log.Add("kept " + rel + " (the bundled topic isn't readable)"); return; }
            var bundledHasTables = RemoveOverrideTables(theirs);
            if (Normalize(mine) == Normalize(theirs) || !HasOwn(mine, theirs)) { drop.Add(() => Delete(file)); return; }
            if (bundledHasTables)
            {
                var to = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + "-mine" + Path.GetExtension(file));
                drop.Add(() => { Save(mine, to); Delete(file); });
                log.Add("kept " + rel + " as " + Path.GetFileName(to) + " (it has rules the bundled topic lacks)");
                return;
            }
            if (hadTables) drop.Add(() => Save(mine, file));
            log.Add("kept " + rel + " (it has rules the bundled topic lacks; it stands in for the bundled topic)");
        }

        /// <summary>"rule|setting" of every override row in the bundled guide's own topics.</summary>
        private static HashSet<string> BundledOverrideRows(string bundled)
        {
            return new HashSet<string>(GuideOverrides.Read(bundled).Select(o => Key(o.RuleId, o.Setting)), StringComparer.OrdinalIgnoreCase);
        }

        private static string Key(string rule, string setting) { return (rule ?? "").Trim().ToLowerInvariant() + "|" + (setting ?? "").Trim().ToLowerInvariant(); }

        private static IEnumerable<XmlElement> OverrideTables(XmlDocument doc)
        {
            return doc.SelectNodes("//*[local-name()='table']").Cast<XmlElement>().Where(GuideOverrides.IsOverrideTable).ToList();
        }

        private static bool RemoveOverrideTables(XmlDocument doc)
        {
            var tables = OverrideTables(doc).ToList();
            foreach (var t in tables) t.ParentNode.RemoveChild(t);
            return tables.Count > 0;
        }

        /// <summary>True when <paramref name="mine"/> has a rule element, or a rule key, that <paramref name="theirs"/> lacks.</summary>
        private static bool HasOwn(XmlDocument mine, XmlDocument theirs)
        {
            var their = new HashSet<string>(Units(theirs, false));
            if (Units(mine, false).Any(u => !their.Contains(u))) return true;
            // Keys the plugin gave a copy of an unkeyed bundled topic are not the writer's; keys a keyed topic lacks are.
            var theirKeys = new HashSet<string>(Keys(theirs), StringComparer.OrdinalIgnoreCase);
            return theirKeys.Count > 0 && Keys(mine).Any(k => !theirKeys.Contains(k));
        }

        private static readonly HashSet<string> TopicUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "li", "tr", "p", "h1", "h2", "h3", "h4", "dt", "dd" };

        /// <summary>The comparable pieces of a document: rule-like elements of a topic, or leaf elements of other XML.</summary>
        private static List<string> Units(XmlDocument doc, bool leaves)
        {
            var isTopic = doc.SelectSingleNode("//*[local-name()='body']") != null;
            return doc.SelectNodes("//*").Cast<XmlElement>()
                      .Where(e => isTopic && !leaves ? TopicUnits.Contains(e.LocalName) : !e.ChildNodes.OfType<XmlElement>().Any())
                      .Select(e => NormalizeXml(e.OuterXml)).ToList();
        }

        private static IEnumerable<string> Keys(XmlDocument doc)
        {
            return doc.SelectNodes("//*[starts-with(@id,'" + GuideLayer.KeyPrefix + "')]").Cast<XmlElement>().Select(e => e.GetAttribute("id"));
        }

        /// <summary>Equal after normalising whitespace and the rule keys the plugin assigns; an XML file whose pieces all appear in the bundled one counts as a copy.</summary>
        private static bool SameFile(string mine, string theirs)
        {
            string a = File.ReadAllText(mine, Encoding.UTF8), b = File.ReadAllText(theirs, Encoding.UTF8);
            if (NormalizeText(a) == NormalizeText(b)) return true;
            try
            {
                XmlDocument x = LoadXml(mine), y = LoadXml(theirs);
                if (Normalize(x) == Normalize(y)) return true;
                var their = new HashSet<string>(Units(y, true));
                return Units(x, true).All(their.Contains);
            }
            catch (XmlException) { return false; }
        }

        private static XmlDocument LoadXml(string path)
        {
            var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            doc.Load(path);
            return doc;
        }

        private static string Normalize(XmlDocument doc) { return NormalizeXml(doc.DocumentElement == null ? "" : doc.DocumentElement.OuterXml); }

        private static readonly Regex KeyAttr = new Regex("\\s+id=\"" + GuideLayer.KeyPrefix + "[^\"]*\"", RegexOptions.Compiled);

        private static string NormalizeXml(string xml)
        {
            var s = KeyAttr.Replace(xml, "");
            s = Regex.Replace(s, @">\s+<", "><");
            return NormalizeText(s);
        }

        private static string NormalizeText(string s) { return Regex.Replace((s ?? "").TrimStart('\uFEFF'), @"\s+", " ").Trim(); }

        private static void Save(XmlDocument doc, string path)
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            GuideLayer.Save(doc, path);
        }

        private static void Delete(string path)
        {
            File.SetAttributes(path, FileAttributes.Normal);   // an install folder may have handed out read-only copies
            File.Delete(path);
        }

        private static bool Under(string rel, string folder)
        {
            return rel.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void RemoveEmptyFolders(string dir, string root)
        {
            foreach (var d in Directory.GetDirectories(dir)) RemoveEmptyFolders(d, root);
            if (dir != root && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
        }

        // ---- files of a personal guide --------------------------------------------------------------------------------

        /// <summary>Names the writer's layer like the bundled one (FlareAI\layer.txt): their topics keep the same rule ids.</summary>
        private static void WriteLayerName(string source, string target)
        {
            var name = "company";
            var declared = string.IsNullOrEmpty(source) ? null : Path.Combine(source, LayerRel);
            if (declared != null && File.Exists(declared))
                name = File.ReadAllLines(declared).Select(l => l.Split('#')[0].Trim()).FirstOrDefault(l => l.Length > 0) ?? name;
            Directory.CreateDirectory(Path.Combine(target, "FlareAI"));
            File.WriteAllText(Path.Combine(target, LayerRel), "# The name in front of your rules' keys. The same as the bundled guide's, so your topics and changes line up with it.\r\n" + name + "\r\n");
        }

        private static void WriteMarker(string target)
        {
            Directory.CreateDirectory(Path.Combine(target, "FlareAI"));
            File.WriteAllText(Path.Combine(target, MarkerRel),
                "# Your own style guide. It holds only your changes and applies after the guide bundled with House Style\r\n" +
                "# (rules you turned off or changed, rules you added, topics you edited). The bundled guide loads from the\r\n" +
                "# plugin, so plugin updates reach you. A topic here with the same name as a bundled topic replaces it.\r\n");
        }
    }

    /// <summary>A style guide change that couldn't be written. The message names the file or folder, so the writer can fix it.</summary>
    public sealed class GuideWriteException : IOException
    {
        /// <summary>The file or folder that couldn't be written.</summary>
        public string Target { get; private set; }

        public GuideWriteException(string target, Exception inner)
            : base("House Style couldn't write to " + target + ": " + (inner == null ? "" : inner.Message), inner)
        {
            Target = target;
        }

        private GuideWriteException(string target, string message) : base(message) { Target = target; }

        /// <summary>The first sentence of <see cref="Unreachable"/>'s message.</summary>
        public const string UnreachableMessage = "House Style can't reach your team's style guide.";

        /// <summary>The team's guide folder isn't there (offline, renamed): nothing is written.</summary>
        public static GuideWriteException Unreachable(string folder)
        {
            return new GuideWriteException(folder, UnreachableMessage + " Check that " + folder + " is available, then try again.");
        }
    }
}
