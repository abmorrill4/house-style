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

        /// <summary>One CSV cell. A leading = + - @ tab or CR gets a quote mark so Excel reads it as text, then CSV quoting
        /// applies. A semicolon is quoted too: Excel splits on it where that's the list separator (German, French), and an
        /// unquoted "x;=..." would start a new cell with a formula.</summary>
        internal static string Cell(string s)
        {
            s = s ?? "";
            if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s;
            if (s.IndexOfAny(new[] { ',', ';', '"', '\r', '\n' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        /// <summary>The longest HYPERLINK formula Excel takes in a cell's worth of text that we write here.</summary>
        public const int MaxFormula = 255;

        /// <summary>The HYPERLINK formula that opens the topic file, or "" when the path can't make a file link.</summary>
        internal static string Link(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "";
            if (!Path.IsPathRooted(path)) return "";

            Uri uri;
            try
            {
                uri = new Uri(path);
            }
            catch (UriFormatException)
            {
                return "";
            }
            if (!uri.IsFile) return "";

            var url = uri.AbsoluteUri;
            if (url.Contains("\"")) return "";

            var formula = "=HYPERLINK(\"" + url + "\",\"" + LinkText + "\")";
            return formula.Length > MaxFormula ? "" : formula;
        }

        /// <summary>The Link field: the formula, CSV-quoted, or "". It's the only cell that isn't neutralised.</summary>
        private static string LinkField(string path)
        {
            var link = Link(path);
            return link.Length == 0 ? "" : "\"" + link.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>The status line after a save. The count is grouped the way the writer's culture groups digits.</summary>
        public static string Saved(int count, string path)
        {
            return "Exported " + count.ToString("N0", CultureInfo.CurrentCulture) + (count == 1 ? " issue" : " issues") + " to " + path + ".";
        }
    }
}
