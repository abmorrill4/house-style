using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FlareAI.Core.Output
{
    /// <summary>
    /// Export Issues (#238): the rows and words for a CSV of a project report's issues. Flare-free, so the tests can run
    /// without the plugin.
    /// </summary>
    public static class IssueExport
    {
        /// <summary>One issue, as one CSV row. Path is the full topic path, used only for the link.</summary>
        public sealed class Row
        {
            public string Topic { get; set; }
            public string Level { get; set; }
            public string Issue { get; set; }
            public string Rule { get; set; }
            public string Source { get; set; }
            public string Text { get; set; }
            public string SuggestedText { get; set; }
            public string Status { get; set; }
            public string Path { get; set; }
        }

        public const string Open = "Open", Changed = "Changed", TextChanged = "Text changed", Skipped = "Skipped";

        public static readonly string[] Columns = { "Topic", "Level", "Issue", "Rule", "Source", "Text", "Suggested text", "Status", "Link" };

        public const string LinkText = "Open file";
        public const string NothingToExport = "There are no issues to export.";

        /// <summary>Changed when fixed, Text changed when stale, Skipped when skipped, else Open.</summary>
        public static string StatusOf(bool isFixed, bool isStale, bool skipped)
        {
            if (isFixed) return Changed;
            if (isStale) return TextChanged;
            if (skipped) return Skipped;
            return Open;
        }

        /// <summary>The default file name, with characters that Windows refuses removed from the project name.</summary>
        public static string SuggestedName(string projectName, DateTime today)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string((projectName ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim();
            var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return name.Length == 0
                ? "House Style issues - " + date + ".csv"
                : "House Style issues - " + name + " - " + date + ".csv";
        }

        /// <summary>The CSV text for the rows, with a header line. Every line ends with CRLF. Does no IO.</summary>
        public static string ToCsv(IEnumerable<Row> rows)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", Columns.Select(Cell))).Append("\r\n");
            if (rows == null) return sb.ToString();
            foreach (var r in rows)
            {
                sb.Append(Cell(r.Topic)).Append(',')
                  .Append(Cell(r.Level)).Append(',')
                  .Append(Cell(r.Issue)).Append(',')
                  .Append(Cell(r.Rule)).Append(',')
                  .Append(Cell(r.Source)).Append(',')
                  .Append(Cell(r.Text)).Append(',')
                  .Append(Cell(r.SuggestedText)).Append(',')
                  .Append(Cell(r.Status)).Append(',')
                  .Append(LinkField(r.Path)).Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>One CSV cell. A leading = + - @ tab or CR gets a quote mark so Excel reads it as text, then CSV quoting applies.</summary>
        internal static string Cell(string s)
        {
            s = s ?? "";
            if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s;
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        /// <summary>The Link field. Empty until order 3 adds the formula.</summary>
        private static string LinkField(string path)
        {
            return "";
        }

        /// <summary>The status line after a save. The count is grouped the way the writer's culture groups digits.</summary>
        public static string Saved(int count, string path)
        {
            return "Exported " + count.ToString("N0", CultureInfo.CurrentCulture) + (count == 1 ? " issue" : " issues") + " to " + path + ".";
        }
    }
}
