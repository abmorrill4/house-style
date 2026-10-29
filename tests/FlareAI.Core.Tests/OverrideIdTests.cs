using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
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

        [Fact]
        public void KnownIds_AreHonored_FromBothSources()
        {
            const string real = "google:word-list/221";
            // The test guide's own names for a Google rule: a bare name is the rule's LocalName, a qualified one its Name.
            var guide = new StyleEngine(new[] { GuideSourceTests.Bundled }, GuideSourceTests.Bundled).Compose(null);
            var word = guide.Rules.First(r => r.Id == real);
            var contractions = guide.Rules.First(r => r.Name.StartsWith("Google.Contractions", StringComparison.OrdinalIgnoreCase));
            var keys = new List<string> { real, word.LocalName, word.Name, contractions.Name.ToUpperInvariant(), "Company.GlossaryTerms", "company.glossaryterms", "Structure.headingorder" };
            foreach (var r in StructureCheck.Rules) { keys.Add(r.Id); keys.Add(r.Name); }
            var dir = Path.Combine(Path.GetTempPath(), "flareai-known-" + Guid.NewGuid().ToString("N"));
            // Named Company: a team layer's rules are named for its folder, and its glossary gives Company.GlossaryTerms.
            var team = Path.Combine(dir, "Company");
            var style = Path.Combine(team, @"Content\Resources\StyleGuide");
            Directory.CreateDirectory(style);
            File.WriteAllText(Path.Combine(team, "Guide.flprj"), "<CatapultProject />");
            Directory.CreateDirectory(Path.Combine(team, @"Project\Glossaries"));
            File.WriteAllText(Path.Combine(team, @"Project\Glossaries\Terms.flglo"), "<CatapultGlossary><GlossaryEntry><Terms><Term>single sign-on</Term></Terms><Definition>One sign-in for several applications.</Definition></GlossaryEntry></CatapultGlossary>");
            File.WriteAllText(Path.Combine(style, "overrides.ini"), string.Join("\r\n", keys.Select(k => k + " = off").ToArray()) + "\r\n");
            var lines = new List<string>();
            var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
            Diagnostics.Sink = l => { lock (lines) lines.Add(l); };
            Diagnostics.Enabled = true;
            try
            {
                var comp = new StyleEngine(new[] { team }, GuideSourceTests.Bundled).Compose(null);
                foreach (var k in keys)
                {
                    Assert.True(comp.SettingOf(k, k) == "off", k);
                    // IsOff takes a rule id; a bare rule name is found by SettingOf.
                    if (k != word.LocalName) Assert.True(comp.IsOff(k), k);
                }

                var bodies = new Dictionary<string, string>
                {
                    { "structure:alt-text", "<p>Logo <img src=\"logo.png\"/></p>" },
                    { "structure:one-h1", "<h1>A</h1><h1>B</h1>" },
                    { "structure:heading-order", "<h1>A</h1><h3>B</h3>" },
                    { "structure:table-header", "<table><tr><td>x</td></tr></table>" },
                    { "structure:link-text", "<p><a href=\"x.htm\">here</a></p>" },
                    { "structure:hard-coded", "<p>Call 555-123-4567</p>" },
                };
                foreach (var r in StructureCheck.Rules)
                {
                    var found = StructureCheck.Of(Body(bodies[r.Id]));
                    Assert.Contains(found, f => f.Rule == r.Name);   // the control: the check fires with no setting
                    Assert.DoesNotContain(StructureCheck.Settle(found, (id, name) => comp.SettingOf(id, name)), f => f.RuleId == r.Id);
                }

                List<string> logged;
                lock (lines) logged = lines.Where(l => l.StartsWith("Style guide", StringComparison.Ordinal)).ToList();
                Assert.DoesNotContain(logged, l => l.Contains("names no rule"));
            }
            finally
            {
                Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled;
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Fact]
        public void RowInEarlierLayerNamingLaterLayerRule_IsKept()
        {
            var dir = Path.Combine(Path.GetTempPath(), "flareai-later-" + Guid.NewGuid().ToString("N"));
            var alpha = Path.Combine(dir, "Alpha");
            var beta = Path.Combine(dir, "Beta");
            try
            {
                // Beta, the later layer, defines the rule; Alpha, the earlier layer, turns it off.
                var betaStyle = Path.Combine(beta, @"Content\Resources\StyleGuide");
                Directory.CreateDirectory(betaStyle);
                File.WriteAllText(Path.Combine(beta, "Guide.flprj"), "<CatapultProject />");
                File.WriteAllText(Path.Combine(betaStyle, "Words.htm"), "<html><body><h1>Words</h1><table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr>" +
                                                                       "<tr><td>frobnicate</td><td>adjust</td><td>We frobnicate it.</td></tr></table></body></html>");
                var id = new StyleEngine(new[] { beta }, GuideSourceTests.Bundled).Compose(null).Rules.First(r => r.Avoid.Contains("frobnicate")).Id;
                var alphaStyle = Path.Combine(alpha, @"Content\Resources\StyleGuide");
                Directory.CreateDirectory(alphaStyle);
                File.WriteAllText(Path.Combine(alpha, "Guide.flprj"), "<CatapultProject />");
                File.WriteAllText(Path.Combine(alphaStyle, "overrides.ini"), id + " = off\r\n");
                var lines = new List<string>();
                var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
                Diagnostics.Sink = l => { lock (lines) lines.Add(l); };
                Diagnostics.Enabled = true;
                try
                {
                    var comp = new StyleEngine(new[] { alpha, beta }, GuideSourceTests.Bundled).Compose(null);
                    Assert.Equal("off", comp.SettingOf(id));
                    Assert.True(comp.IsOff(id));
                    List<string> logged;
                    lock (lines) logged = lines.Where(l => l.StartsWith("Style guide", StringComparison.Ordinal) && l.Contains(id)).ToList();
                    Assert.Empty(logged);
                }
                finally
                {
                    Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled;
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Fact]
        public void UnknownIdIsLoggedOncePerEngine_AndCappedPerLoad()
        {
            var dir = Path.Combine(Path.GetTempPath(), "flareai-cap-" + Guid.NewGuid().ToString("N"));
            var team = Path.Combine(dir, "TeamGuide");
            var style = Path.Combine(team, @"Content\Resources\StyleGuide");
            var ini = Path.Combine(style, "overrides.ini");
            try
            {
                Directory.CreateDirectory(style);
                File.WriteAllText(Path.Combine(team, "Guide.flprj"), "<CatapultProject />");
                File.WriteAllText(ini, "nope:xyz = off\r\n");
                var lines = new List<string>();
                var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
                Diagnostics.Sink = l => { lock (lines) lines.Add(l); };
                Diagnostics.Enabled = true;
                try
                {
                    // Once per engine: the second load misses the cache (the file's write time moved), but the engine has logged this id already.
                    var e = new StyleEngine(new[] { team }, GuideSourceTests.Bundled);
                    e.Compose(null);
                    File.SetLastWriteTimeUtc(ini, DateTime.UtcNow.AddMinutes(1));
                    e.Compose(null);
                    List<string> once;
                    lock (lines) once = lines.Where(l => l.StartsWith("Style guide", StringComparison.Ordinal) && l.Contains("\"nope:xyz\"")).ToList();
                    Assert.Single(once);

                    // Capped per load: 30 distinct unknown ids give 20 lines and one 'and N more' line.
                    var ids = Enumerable.Range(1, 30).Select(i => "nope:" + i.ToString("00")).ToList();
                    File.WriteAllText(ini, string.Join("\r\n", ids.Select(u => u + " = off").ToArray()) + "\r\n");
                    lock (lines) lines.Clear();
                    new StyleEngine(new[] { team }, GuideSourceTests.Bundled).Compose(null);
                    List<string> rows, more;
                    lock (lines)
                    {
                        rows = lines.Where(l => l.StartsWith("Style guide \"TeamGuide\" has a rule setting for ", StringComparison.Ordinal) && l.Contains("\"nope:")).ToList();
                        more = lines.Where(l => l.StartsWith("and ", StringComparison.Ordinal) && l.Contains(" more in \"TeamGuide\"")).ToList();
                    }
                    Assert.True(rows.Count <= 20, "logged " + rows.Count + " rows");
                    Assert.Single(more);
                    Assert.StartsWith("and ", more[0], StringComparison.Ordinal);
                    Assert.Contains("more in", more[0], StringComparison.Ordinal);
                }
                finally
                {
                    Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled;
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Fact]
        public void FallbackGuide_LogsNothingWhileTeamGuideUnreadable()
        {
            const string unknown = "xyz:1";
            var dir = Path.Combine(Path.GetTempPath(), "flareai-fallback-" + Guid.NewGuid().ToString("N"));
            var missing = Path.Combine(dir, "Gone", "TeamGuide");
            var readable = Path.Combine(dir, "Share", "Readable");
            var style = Path.Combine(readable, @"Content\Resources\StyleGuide");
            try
            {
                // The team guide can't be reached, so the bundled fallback stands in; the readable layer still loads and has an unknown row.
                Directory.CreateDirectory(style);
                File.WriteAllText(Path.Combine(readable, "Guide.flprj"), "<CatapultProject />");
                File.WriteAllText(Path.Combine(style, "overrides.ini"), unknown + " = off\r\n");
                var lines = new List<string>();
                var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
                Diagnostics.Sink = l => { lock (lines) lines.Add(l); };
                Diagnostics.Enabled = true;
                try
                {
                    var e = new StyleEngine(new[] { missing, readable }, GuideSourceTests.Bundled) { Fallback = () => new[] { GuideSourceTests.Bundled } };
                    var comp = e.Compose(null);
                    Assert.NotNull(comp.Notice);
                    Assert.StartsWith(StyleEngine.UnreadableNotice, comp.Notice, StringComparison.Ordinal);
                    Assert.Null(comp.SettingOf(unknown));
                    Assert.False(comp.IsOff(unknown));
                    List<string> logged;
                    lock (lines) logged = lines.Where(l => l.StartsWith("Style guide", StringComparison.Ordinal)).ToList();
                    Assert.DoesNotContain(logged, l => l.Contains(unknown));
                    Assert.DoesNotContain(logged, l => l.Contains("names no rule"));
                }
                finally
                {
                    Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled;
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Fact]
        public void BundledGuide_HasNoIgnoredRows()
        {
            // The shipped guide (the same files PluginSettings.BundledGuide copies) must name only rules that exist.
            var lines = new List<string>();
            var oldSink = Diagnostics.Sink; var oldEnabled = Diagnostics.Enabled;
            Diagnostics.Sink = l => { lock (lines) lines.Add(l); };
            Diagnostics.Enabled = true;
            try
            {
                var comp = new StyleEngine(new[] { GuideSourceTests.Bundled }, GuideSourceTests.Bundled).Compose(null);
                foreach (var layer in comp.Layers)
                    foreach (var o in layer.Overrides)
                        Assert.True(OverrideIds.IsKnown(comp, o.Key), layer.Name + " names no rule for " + OverrideIds.ForLog(o.Key));
                List<string> logged;
                lock (lines) logged = lines.Where(l => l.StartsWith("Style guide", StringComparison.Ordinal) && l.Contains("names no rule")).ToList();
                Assert.Empty(logged);
            }
            finally
            {
                Diagnostics.Sink = oldSink; Diagnostics.Enabled = oldEnabled;
            }
        }

        [Fact]
        public void Validate_AcceptsStructureIdRows_AndReportsUnknownOnes()
        {
            var dir = Path.Combine(Path.GetTempPath(), "flareai-validate-" + Guid.NewGuid().ToString("N"));
            var team = Path.Combine(dir, "TeamGuide");
            var style = Path.Combine(team, @"Content\Resources\StyleGuide");
            try
            {
                Directory.CreateDirectory(style);
                File.WriteAllText(Path.Combine(team, "Guide.flprj"), "<CatapultProject />");
                File.WriteAllText(Path.Combine(style, "overrides.ini"), "structure:heading-order = off\r\nStructure.HeadingOrder = NO\r\nxyz:1 = off\r\n");
                var report = new StyleEngine(new[] { team }, GuideSourceTests.Bundled).Validate(null);
                var noRule = report.Issues.Where(i => i.Level == "error" && i.Message.StartsWith("No rule", StringComparison.Ordinal)).ToList();
                Assert.Single(noRule);
                Assert.Contains("xyz:1", noRule[0].Message, StringComparison.Ordinal);
                Assert.DoesNotContain(report.Issues, i => i.Level == "error" && (i.Location == "override structure:heading-order" || i.Location == "override Structure.HeadingOrder"));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        private static List<XmlNode> Body(string html)
        {
            var doc = new XmlDocument();
            doc.LoadXml("<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body>" + html + "</body></html>");
            return doc.DocumentElement["body"].ChildNodes.Cast<XmlNode>().Where(n => n.NodeType == XmlNodeType.Element).ToList();
        }
    }
}
