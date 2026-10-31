using System;
using System.Collections.Generic;
using System.IO;
using FlareAI.Flare;
using Xunit;

namespace FlareAI.Flare.Tests
{
    // Sets the static Diagnostics.Sink, so it shares the "Panes" collection with the other UI tests.
    [Collection("Panes")]
    public class LintChecksChoiceTests
    {
        [Theory]
        [InlineData("rules-ai")]
        [InlineData("RULES-AI")]
        [InlineData("Rules-Ai")]
        [InlineData(null)]
        [InlineData("")]
        public void IsRulesOnly_FalseForRulesAndAi_AndFirstUse(string value)
        {
            Assert.False(LintChecksChoice.IsRulesOnly(value));
        }

        [Theory]
        [InlineData("rules")]
        [InlineData("Rules")]
        [InlineData("typo")]
        [InlineData("v2")]
        public void IsRulesOnly_TrueForRulesAndAnyOtherValue(string value)
        {
            Assert.True(LintChecksChoice.IsRulesOnly(value));
        }

        [Fact]
        public void TrySave_SavesTheValue()
        {
            string saved = null;
            var ok = LintChecksChoice.TrySave(LintChecksChoice.Rules, v => saved = v);
            Assert.True(ok);
            Assert.Equal("rules", saved);
        }

        [Fact]
        public void TrySave_WhenSaveThrows_ReturnsFalse_AndLogs()
        {
            var lines = new List<string>();
            var oldSink = FlareAI.Core.Diagnostics.Sink;
            FlareAI.Core.Diagnostics.Sink = lines.Add;
            try
            {
                bool ok = true;
                Exception error = null;
                try
                {
                    ok = LintChecksChoice.TrySave(LintChecksChoice.Rules,
                        v => { throw new IOException("disk full"); });
                }
                catch (Exception ex) { error = ex; }

                Assert.Null(error);
                Assert.False(ok);
                Assert.Single(lines, l => l.Contains("save lint checks"));
            }
            finally
            {
                FlareAI.Core.Diagnostics.Sink = oldSink;
            }
        }
    }
}
