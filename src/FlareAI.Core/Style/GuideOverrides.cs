using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;

namespace FlareAI.Core.Style
{
    /// <summary>
    /// Writes a rule change into the team's style guide: a row of a Rule / Setting / Reason / Changed by / Date table in
    /// the guide's own (non-Google) topics. Owners pick the rule from a finding; they never type its id.
    /// </summary>
    public static class GuideOverrides
    {
        public const string DefaultTopic = "Google-overrides.htm";

        /// <summary>
        /// Sets a rule for everyone who uses <paramref name="team"/>'s guide, recording who and when. Only owners may (when the
        /// guide has an owners file); the writer's own guide is created on first use. Returns the topic written.
        /// </summary>
        public static string Set(Team team, string ruleId, string setting, string reason)
        {
            if (team == null) throw new ArgumentNullException("team");
            // A team folder that isn't there (offline, renamed): writing would leave a near-empty guide in its place. The
            // writer's own guide is different: it is created on first use.
            if (!team.IsPersonal && !string.IsNullOrEmpty(team.GuideRoot) && !Directory.Exists(team.GuideRoot))
                throw GuideWriteException.Unreachable(team.GuideRoot);
            team.EnsureOwner();
            return Set(team.WritableGuide(), ruleId, setting, reason, team.Writer, DateTime.Now);
        }

        /// <summary>Sets <paramref name="ruleId"/> to off / suggestion / warning / error for the whole team. Returns the topic written.</summary>
        /// <param name="by">Who made the change (recorded with the date); null records neither.</param>
        public static string Set(string guideRoot, string ruleId, string setting, string reason, string by = null, DateTime? when = null)
        {
            if (string.IsNullOrEmpty(ruleId)) throw new ArgumentException("This issue isn't tied to a rule, so there is nothing to change.");
            var folder = StyleFolder(guideRoot);
            var date = by == null ? null : (when ?? DateTime.Now).ToString("yyyy-MM-dd");
            // The first topic with a Rule/Setting table (the starter's "Changes to the Google guide"), else a new one.
            foreach (var file in Topics(folder))
            {
                var doc = new XmlDocument { PreserveWhitespace = true };
                try { doc.Load(file); } catch (XmlException) { continue; }
                var table = doc.SelectNodes("//*[local-name()='table']").Cast<XmlElement>().FirstOrDefault(IsOverrideTable);
                if (table == null) continue;
                Upsert(table, ruleId, setting, reason, by, date);
                try { GuideLayer.Save(doc, file); }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(file, ex); }
                return file;
            }
            // The writer's own guide gets its own topic: a Company\Google-overrides.htm there would stand in for the bundled one.
            var personal = GuideSeed.IsPersonal(guideRoot);
            var path = personal ? Path.Combine(folder, GuideSeed.PersonalTopic) : Path.Combine(Path.Combine(folder, "Company"), DefaultTopic);
            CreateTopic(path, personal);
            return Set(guideRoot, ruleId, setting, reason, by, when);
        }

        /// <summary>
        /// Writes one row (rule, setting, reason, who, when as recorded) into <paramref name="topic"/>'s first override table,
        /// creating the writer's own rule-changes topic when missing. Used to carry rows over when a guide is upgraded.
        /// </summary>
        internal static void Put(string topic, string ruleId, string setting, string reason, string by, string date)
        {
            if (!File.Exists(topic)) CreateTopic(topic, true);
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(topic);
            var table = doc.SelectNodes("//*[local-name()='table']").Cast<XmlElement>().First(IsOverrideTable);
            Upsert(table, ruleId, setting, reason ?? "", by ?? "", date ?? "");
            try { GuideLayer.Save(doc, topic); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(topic, ex); }
        }

        private static void CreateTopic(string path, bool personal)
        {
            var title = personal ? "My changes to the style guide" : "Changes to the Google guide";
            var intro = personal
                ? "Rows in this table turn rules off, change their level, or turn a default back on (Setting: on), for you only. They apply after the guide bundled with House Style, so its updates still reach you."
                : "Rows in this table turn rules off, or change their level, for the whole team.";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\">\n    <head><title>" + title + "</title></head>\n    <body>\n" +
                    "        <h1>" + title + "</h1>\n        <p>" + intro + "</p>\n" +
                    "        <table>\n            <tr><th>Rule</th><th>Setting</th><th>Reason</th><th>Changed by</th><th>Date</th></tr>\n        </table>\n    </body>\n</html>\n", new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(path, ex); }
        }

        /// <summary>Removes the rule's rows from the override tables in <paramref name="file"/>. Returns how many were removed.</summary>
        public static int Remove(string file, string ruleId)
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(file);
            var removed = 0;
            foreach (var table in doc.SelectNodes("//*[local-name()='table']").Cast<XmlElement>().Where(IsOverrideTable).ToList())
            {
                var rows = Rows(table);
                var ruleC = Columns(rows[0]).FindIndex(IsRule);
                foreach (var row in rows.Skip(1).Where(r => string.Equals(Cell(r, ruleC), ruleId, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    var ws = row.PreviousSibling;
                    if (ws != null && (ws.NodeType == XmlNodeType.Whitespace || ws.NodeType == XmlNodeType.SignificantWhitespace || (ws.NodeType == XmlNodeType.Text && ws.Value.Trim().Length == 0)))
                        ws.ParentNode.RemoveChild(ws);
                    row.ParentNode.RemoveChild(row);
                    removed++;
                }
            }
            if (removed == 0) return 0;
            try { GuideLayer.Save(doc, file); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) { throw new GuideWriteException(file, ex); }
            return removed;
        }

        /// <summary>Every row of every override table in the guide's own topics (not the generated Google folder).</summary>
        public static List<RuleOverride> Read(string guideRoot)
        {
            var list = new List<RuleOverride>();
            foreach (var file in Topics(StyleFolder(guideRoot)))
            {
                var doc = new XmlDocument { PreserveWhitespace = true };
                try { doc.Load(file); } catch (XmlException) { continue; } catch (IOException) { continue; }
                list.AddRange(Read(file, doc));
            }
            return list;
        }

        /// <summary>The rows of the override tables in one loaded topic.</summary>
        internal static List<RuleOverride> Read(string file, XmlDocument doc)
        {
            var list = new List<RuleOverride>();
            {
                foreach (var table in doc.SelectNodes("//*[local-name()='table']").Cast<XmlElement>().Where(IsOverrideTable))
                {
                    var rows = Rows(table);
                    var cols = Columns(rows[0]);
                    Func<XmlElement, Func<string, bool>, string> cell = (r, is_) => Cell(r, cols.FindIndex(c => is_(c)));
                    foreach (var r in rows.Skip(1))
                    {
                        var id = cell(r, IsRule);
                        if (string.IsNullOrEmpty(id)) continue;
                        list.Add(new RuleOverride
                        {
                            RuleId = id, Setting = GuideLayer.Setting((cell(r, IsSetting) ?? "").ToLowerInvariant()) ?? cell(r, IsSetting),
                            Note = cell(r, IsReason) ?? "", By = cell(r, IsBy) ?? "", Date = cell(r, IsDate) ?? "", File = file
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>Where a guide keeps its topics: Content\Resources\StyleGuide of a Flare project, else the folder itself.</summary>
        internal static string StyleFolder(string root)
        {
            return Directory.Exists(root) && Directory.GetFiles(root, "*.flprj").Length > 0 ? Path.Combine(root, @"Content\Resources\StyleGuide") : root;
        }

        private static IEnumerable<string> Topics(string folder)
        {
            var google = Path.Combine(folder, "Google") + Path.DirectorySeparatorChar;
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.htm", SearchOption.AllDirectories).Where(f => !f.StartsWith(google, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f)
                : Enumerable.Empty<string>();
        }

        private static bool IsRule(string h) { return h.StartsWith("rule"); }
        private static bool IsSetting(string h) { return h.StartsWith("setting") || h.StartsWith("level") || h.StartsWith("status"); }
        private static bool IsReason(string h) { return h.StartsWith("reason") || h.StartsWith("why") || h.StartsWith("note"); }
        private static bool IsBy(string h) { return h.StartsWith("changed by") || h == "by" || h.StartsWith("who"); }
        private static bool IsDate(string h) { return h.StartsWith("date") || h.StartsWith("changed on"); }

        internal static bool IsOverrideTable(XmlElement table)
        {
            var rows = Rows(table);
            if (rows.Count == 0) return false;
            var head = Columns(rows[0]);
            return head.Any(IsRule) && head.Any(IsSetting);
        }

        private static List<XmlElement> Rows(XmlElement table) { return table.SelectNodes(".//*[local-name()='tr']").Cast<XmlElement>().ToList(); }

        private static List<XmlElement> Cells(XmlElement row)
        {
            return row.ChildNodes.OfType<XmlElement>().Where(c => c.LocalName == "th" || c.LocalName == "td").ToList();
        }

        private static List<string> Columns(XmlElement header) { return Cells(header).Select(c => c.InnerText.Trim().ToLowerInvariant()).ToList(); }

        private static string Cell(XmlElement row, int column)
        {
            var cells = Cells(row);
            return column >= 0 && column < cells.Count ? GuideLayer.TextOf(cells[column]) : null;
        }

        private static void Upsert(XmlElement table, string ruleId, string setting, string reason, string by, string date)
        {
            var doc = table.OwnerDocument;
            var ns = table.NamespaceURI;   // match the topic's namespace (plain XHTML or the default xmlns)
            var rows = Rows(table);
            if (by != null)
            {
                // Older tables have Rule / Setting / Reason only: add the who and when columns (empty for earlier rows).
                AddColumn(rows, IsBy, "Changed by");
                AddColumn(rows, IsDate, "Date");
            }
            var cols = Columns(rows[0]);
            var ruleC = cols.FindIndex(IsRule);
            var row = doc.CreateElement("tr", ns);
            foreach (var c in cols)
            {
                var td = doc.CreateElement("td", ns);
                td.InnerText = (IsRule(c) ? ruleId : IsSetting(c) ? setting : IsReason(c) ? reason : IsBy(c) ? by : IsDate(c) ? date : null) ?? "";
                row.AppendChild(td);
            }
            var existing = rows.Skip(1).FirstOrDefault(r => string.Equals(Cell(r, ruleC), ruleId, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.ParentNode.ReplaceChild(row, existing); return; }
            var parent = rows[rows.Count - 1].ParentNode;
            parent.AppendChild(doc.CreateTextNode("\n            "));
            parent.AppendChild(row);
            parent.AppendChild(doc.CreateTextNode("\n        "));
        }

        private static void AddColumn(List<XmlElement> rows, Func<string, bool> has, string name)
        {
            if (Columns(rows[0]).Any(has)) return;
            for (var i = 0; i < rows.Count; i++)
            {
                var cell = rows[i].OwnerDocument.CreateElement(i == 0 ? "th" : "td", rows[i].NamespaceURI);
                if (i == 0) cell.InnerText = name;
                rows[i].AppendChild(cell);
            }
        }
    }
}
