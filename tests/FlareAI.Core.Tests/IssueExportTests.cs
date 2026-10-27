using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using FlareAI.Core.Output;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>Export Issues (#238): the row model, the status words, the file name and the messages.</summary>
    public class IssueExportTests
    {
        private static readonly DateTime Day = new DateTime(2026, 10, 26);

        [Theory]
        [InlineData(true, true, true, "Changed")]
        [InlineData(true, true, false, "Changed")]
        [InlineData(true, false, true, "Changed")]
        [InlineData(true, false, false, "Changed")]
        [InlineData(false, true, true, "Text changed")]
        [InlineData(false, true, false, "Text changed")]
        [InlineData(false, false, true, "Skipped")]
        [InlineData(false, false, false, "Open")]
        public void StatusOf_FollowsThePrecedence(bool isFixed, bool isStale, bool skipped, string expected)
        {
            Assert.Equal(expected, IssueExport.StatusOf(isFixed, isStale, skipped));
        }

        [Theory]
        [InlineData("bill:pay?<1>", "House Style issues - billpay1 - 2026-10-26.csv")]
        [InlineData("billpay", "House Style issues - billpay - 2026-10-26.csv")]
        [InlineData(null, "House Style issues - 2026-10-26.csv")]
        [InlineData(":?", "House Style issues - 2026-10-26.csv")]
        public void SuggestedName_RemovesInvalidCharacters(string projectName, string expected)
        {
            Assert.Equal(expected, IssueExport.SuggestedName(projectName, Day));
        }

        [Fact]
        public void Saved_GroupsTheCount_AndSaysIssueForOne()
        {
            var before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
                Assert.Equal("Exported 2,300 issues to C:\\x\\a.csv.", IssueExport.Saved(2300, @"C:\x\a.csv"));
                Assert.Equal("Exported 1 issue to C:\\x\\a.csv.", IssueExport.Saved(1, @"C:\x\a.csv"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }
        }

        private static IssueExport.Row Blank(string topic, string level, string issue, string status)
        {
            return new IssueExport.Row
            {
                Topic = topic, Level = level, Issue = issue, Rule = "Rule", Source = "Our rules",
                Text = "text", SuggestedText = "fix", Status = status, Path = null
            };
        }

        [Fact]
        public void ToCsv_WritesTheColumns_OneRowPerRow_WithQuotedCommasAndQuotes()
        {
            var a = Blank("a.htm", "Error", "Issue1", "Open");
            a.Text = "a, \"b\"";
            var b = Blank("b.htm", "Warning", "Issue2", "Changed");
            b.SuggestedText = "x\ny";
            var csv = IssueExport.ToCsv(new[] { a, b });

            var expected = "Topic,Level,Issue,Rule,Source,Text,Suggested text,Status,Link\r\n"
                + "a.htm,Error,Issue1,Rule,Our rules,\"a, \"\"b\"\"\",fix,Open,\r\n"
                + "b.htm,Warning,Issue2,Rule,Our rules,text,\"x\ny\",Changed,\r\n";
            Assert.Equal(expected, csv);
            Assert.Equal("Topic,Level,Issue,Rule,Source,Text,Suggested text,Status,Link\r\n", IssueExport.ToCsv(null));
        }

        [Theory]
        [InlineData("=", "'=x")]
        [InlineData("+", "'+x")]
        [InlineData("-", "'-x")]
        [InlineData("@", "'@x")]
        [InlineData("\t", "'\tx")]
        [InlineData("\r", "\"'\rx\"")]
        public void ToCsv_NeutralisesFormulaStarts_InEveryCellAndHeader(string prefix, string expectedCell)
        {
            var row = Blank(prefix + "x", prefix + "x", prefix + "x", "Open");
            row.Rule = prefix + "x";
            row.Source = prefix + "x";
            row.Text = prefix + "x";
            row.SuggestedText = prefix + "x";
            var lines = IssueExport.ToCsv(new[] { row }).Split(new[] { "\r\n" }, StringSplitOptions.None);

            Assert.Equal("Topic,Level,Issue,Rule,Source,Text,Suggested text,Status,Link", lines[0]);
            var expectedLine = string.Join(",", Enumerable.Repeat(expectedCell, 7)) + ",Open,";
            Assert.Equal(expectedLine, lines[1]);
        }

        [Fact]
        public void ToCsv_QuotesASemicolon_SoASemicolonLocaleCantStartAFormulaCell()
        {
            var row = Blank("t.htm", "Error", "i", "Open");
            row.Rule = "x;=WEBSERVICE(\"https://e/\")";
            var line = IssueExport.ToCsv(new[] { row }).Split(new[] { "\r\n" }, StringSplitOptions.None)[1];
            Assert.Contains("\"x;=WEBSERVICE(\"\"https://e/\"\")\"", line);
        }

        [Fact]
        public void ToCsv_WritesTheStatusValues()
        {
            var rows = new[]
            {
                Blank("1.htm", "Error", "i", IssueExport.Open),
                Blank("2.htm", "Error", "i", IssueExport.Changed),
                Blank("3.htm", "Error", "i", IssueExport.TextChanged),
                Blank("4.htm", "Error", "i", IssueExport.Skipped)
            };
            var lines = IssueExport.ToCsv(rows).Split(new[] { "\r\n" }, StringSplitOptions.None);

            Assert.Equal("Open", lines[1].Split(',')[7]);
            Assert.Equal("Changed", lines[2].Split(',')[7]);
            Assert.Equal("Text changed", lines[3].Split(',')[7]);
            Assert.Equal("Skipped", lines[4].Split(',')[7]);
        }

        [Fact]
        public void ToCsv_FiftyThousandRows_IsFast()
        {
            var rows = new IssueExport.Row[50000];
            for (var i = 0; i < rows.Length; i++)
                rows[i] = Blank("billpay\\faq" + i + ".htm", "Warning", "Please note that the user should be told", "Open");

            var watch = Stopwatch.StartNew();
            var csv = IssueExport.ToCsv(rows);
            watch.Stop();

            Assert.True(watch.Elapsed.TotalSeconds < 10, "took " + watch.Elapsed.TotalSeconds + " s");
            Assert.Equal(50001, csv.Count(c => c == '\n'));
        }
        private static string LastLine(string csv)
        {
            var lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.None);
            return lines[1];
        }

        [Fact]
        public void ToCsv_LinkIsAHyperlinkFormula_ToTheEscapedFileUrl()
        {
            var row = Blank("a.htm", "Error", "i", "Open");
            row.Path = @"C:\Docs\My Project\Content\a#b\100% done.htm";
            var csv = IssueExport.ToCsv(new[] { row });

            Assert.True(LastLine(csv).EndsWith(
                ",Open,\"=HYPERLINK(\"\"file:///C:/Docs/My%20Project/Content/a%23b/100%25%20done.htm\"\",\"\"Open file\"\")\"",
                StringComparison.Ordinal), LastLine(csv));
        }

        [Fact]
        public void ToCsv_LinkForAUncPath_IsAFileServerUrl()
        {
            var row = Blank("a.htm", "Error", "i", "Open");
            row.Path = @"\\server\share\P\Content\x y.htm";
            var csv = IssueExport.ToCsv(new[] { row });

            Assert.True(LastLine(csv).EndsWith(
                ",Open,\"=HYPERLINK(\"\"file://server/share/P/Content/x%20y.htm\"\",\"\"Open file\"\")\"",
                StringComparison.Ordinal), LastLine(csv));
        }

        public static System.Collections.Generic.IEnumerable<object[]> PathsWithoutALink()
        {
            yield return new object[] { null };
            yield return new object[] { "" };
            yield return new object[] { @"billpay\faq.htm" };
            yield return new object[] { @"\Content\a.htm" };
            yield return new object[] { @"C:foo\a.htm" };
            yield return new object[] { @"C:\P\a""b.htm" };
            yield return new object[] { "C:\\" + new string('a', 240) + ".htm" };
        }

        [Theory]
        [MemberData(nameof(PathsWithoutALink))]
        public void ToCsv_LeavesTheLinkEmpty_ForAQuoteARelativePathOrAFormulaOver255Characters(string path)
        {
            var row = Blank("a.htm", "Error", "i", "Open");
            row.Path = path;
            var csv = IssueExport.ToCsv(new[] { row });

            Assert.True(LastLine(csv).EndsWith(",Open,", StringComparison.Ordinal), LastLine(csv));
        }
    }
}
