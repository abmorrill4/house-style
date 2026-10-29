using System;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    [Collection("Logging")]
    public class OverrideIdTests
    {
        [Fact]
        public void ForLog_IsOneShortLine()
        {
            var inputs = new[]
            {
                "A sentence with spaces in it",
                "first line\r\nsecond line",
                "right-to-left ‮override",
                new string('x', 5000),
                @"..\..\x",
            };
            foreach (var input in inputs)
            {
                var logged = OverrideIds.ForLog(input);
                // Ordinal: culture-sensitive matching ignores U+202E, so it would "find" it in any string.
                Assert.DoesNotContain("\r", logged, StringComparison.Ordinal);
                Assert.DoesNotContain("\n", logged, StringComparison.Ordinal);
                Assert.DoesNotContain("‮", logged, StringComparison.Ordinal);
                Assert.StartsWith("\"", logged, StringComparison.Ordinal);
                Assert.EndsWith("\"", logged, StringComparison.Ordinal);
                Assert.True(logged.Length <= 82, "ForLog gave " + logged.Length + " characters");
            }
            Assert.Contains("structure:heading-order", OverrideIds.ForLog("structure:heading-order"), StringComparison.Ordinal);
            Assert.Contains("Google.Headings", OverrideIds.ForLog("Google.Headings"), StringComparison.Ordinal);
        }
    }
}
