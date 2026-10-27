using System;
using System.Globalization;
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
    }
}
