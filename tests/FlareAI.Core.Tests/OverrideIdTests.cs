using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        [Fact]
        public void TeamGuideRow_UnknownIdIsIgnoredAndLogged()
        {
            const string real = "google:word-list/221";
            var longId = "zz" + new string('q', 1200);
            var unknown = new[] { "xyz:1", @"..\..\x", longId, "ai:clarity", "request:7f3a9c" };
            var dir = Path.Combine(Path.GetTempPath(), "flareai-ovr-" + Guid.NewGuid().ToString("N"));
            var team = Path.Combine(dir, "TeamGuide");
            var style = Path.Combine(team, @"Content\Resources\StyleGuide");
            Directory.CreateDirectory(style);
            File.WriteAllText(Path.Combine(team, "Guide.flprj"), "<CatapultProject />");
            File.WriteAllText(Path.Combine(style, "overrides.ini"), real + " = off\r\n" + string.Join("\r\n", unknown.Select(u => u + " = off").ToArray()) + "\r\n");
            var lines = new List<string>();
            var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
            Diagnostics.Sink = l => { lock (lines) lines.Add(l); };
            Diagnostics.Enabled = true;
            try
            {
                var e = new StyleEngine(new[] { team }, GuideSourceTests.Bundled);
                var comp = e.Compose(null);
                Assert.True(comp.IsOff(real));
                foreach (var u in unknown)
                {
                    Assert.Null(comp.SettingOf(u));
                    Assert.False(comp.IsOff(u));
                }
                e.Compose(null); e.Invalidate(); e.Compose(null);
                List<string> logged;
                lock (lines) logged = lines.Where(l => l.StartsWith("Style guide", StringComparison.Ordinal) && l.Contains("names no rule")).ToList();
                Assert.Equal(unknown.Length, logged.Count);   // once per id, however often it loads
                foreach (var l in logged)
                {
                    Assert.DoesNotContain("\n", l, StringComparison.Ordinal);
                    Assert.DoesNotContain("\r", l, StringComparison.Ordinal);
                    Assert.StartsWith("Style guide \"TeamGuide\" has a rule setting for ", l, StringComparison.Ordinal);
                    Assert.EndsWith(" that names no rule, so it was ignored.", l, StringComparison.Ordinal);
                    var id = l.Substring("Style guide \"TeamGuide\" has a rule setting for ".Length);
                    id = id.Substring(0, id.Length - " that names no rule, so it was ignored.".Length);
                    Assert.True(id.Length <= 82, "id part was " + id.Length + " characters");
                }
                Assert.Contains(logged, l => l.Contains("\"xyz:1\""));
                Assert.Contains(logged, l => l.Contains("\"ai:clarity\""));
                Assert.DoesNotContain(logged, l => l.Contains(real));
            }
            finally
            {
                Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled;
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Fact]
        public void OverridesIni_UnknownIdIsIgnoredAndLogged()
        {
            const string real = "google:word-list/221";
            var longId = "zz" + new string('q', 1200);
            var unknown = new[] { "xyz:1", @"..\..\x", longId, "ai:clarity", "request:7f3a9c" };
            var dir = Path.Combine(Path.GetTempPath(), "flareai-ini-" + Guid.NewGuid().ToString("N"));
            var team = Path.Combine(dir, "TeamGuide");
            var style = Path.Combine(team, @"Content\Resources\StyleGuide");
            Directory.CreateDirectory(style);
            File.WriteAllText(Path.Combine(team, "Guide.flprj"), "<CatapultProject />");
            File.WriteAllText(Path.Combine(style, "overrides.ini"), real + " = off\r\n" + string.Join("\r\n", unknown.Select(u => u + " = off").ToArray()) + "\r\n");
            var lines = new List<string>();
            var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
            Diagnostics.Sink = l => { lock (lines) lines.Add(l); };
            Diagnostics.Enabled = true;
            try
            {
                var comp = new StyleEngine(new[] { team }, GuideSourceTests.Bundled).Compose(null);
                Assert.True(comp.IsOff(real));
                foreach (var u in unknown)
                {
                    Assert.Null(comp.SettingOf(u));
                    Assert.False(comp.IsOff(u));
                }
                List<string> logged;
                lock (lines) logged = lines.Where(l => l.StartsWith("Style guide", StringComparison.Ordinal)).ToList();
                foreach (var u in unknown)
                    Assert.Contains(logged, l => l.StartsWith("Style guide \"TeamGuide\" ", StringComparison.Ordinal) && l.Contains(OverrideIds.ForLog(u)));
                Assert.DoesNotContain(logged, l => l.Contains(real));
            }
            finally
            {
                Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled;
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }
    }
}
