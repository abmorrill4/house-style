using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using FlareAI.Integration.Tests.Support;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Integration.Tests.Review
{
    /// <summary>
    /// Fixes that land outside the open topic: a snippet shared by many topics, a whole project with read-only and open files,
    /// and Go to for findings in every kind of context.
    /// </summary>
    public class SnippetAndProjectTests
    {
        private const string Snippet = "Content/Resources/Snippets/term.flsnp";

        private static string SnippetFile(string bodyXml)
        {
            return string.Format(ReviewFixture.Wrapper, bodyXml).Replace("<head><title>Topic</title></head>", "<head />");
        }

        // ---- 6 · TEST-PLAN A7 -------------------------------------------------------------------------------------

        [Fact]
        public void Story06a_WriterFixesTheBlockSnippetUsedByManyTopics_TheSnippetGetsTheChange_AndEveryTopicLosesTheFinding()
        {
            // A7: the snippet opens and gets the tracked change; the topic comes back to the front.
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm"))
            {
                fx.Check();
                var f = fx.Session.Findings.Single(x => x.DocumentPath != null);
                var uses = fx.Session.SnippetUses(f.DocumentPath);
                Assert.True(uses > 1);
                fx.Session.Select(f);

                Assert.Equal(AcceptOutcome.Applied, fx.Session.Accept());

                Assert.Contains("used in " + uses + " topics", fx.Host.Confirms.Single());   // the writer is told it is shared
                var snippet = (FlareSimDocument)fx.Ws.OpenDocuments.Single(d => d.SourcePath == f.DocumentPath);
                Assert.True(snippet.OpenedByPlugin);
                Assert.Equal(1, snippet.UndoCount);                       // tracked in the snippet…
                Assert.Equal(0, fx.Doc.UndoCount);                        // …not in the topic
                Assert.Equal(fx.Original, fx.Doc.Xml);
                Assert.Equal(fx.Doc.SourcePath, fx.Host.Opened.Last());   // the topic comes back to the front
                Assert.Contains("stays open in its own tab, unsaved", fx.Session.Status);
                Assert.Equal(0, snippet.SaveCount);

                // The writer reviews and saves the snippet; every topic that includes it is clean on its next check.
                snippet.AcceptAll();
                snippet.Save();
                var others = Directory.GetFiles(fx.Ws.ContentDir, "*.htm", SearchOption.AllDirectories)
                    .Where(p => File.ReadAllText(p).Contains("LegacyApp.flsnp") && p != fx.Doc.SourcePath).Take(3).ToList();
                Assert.Equal(3, others.Count);
                foreach (var topic in others.Concat(new[] { fx.Doc.SourcePath }))
                {
                    var doc = fx.Ws.Open(topic);
                    var found = fx.Runner.CheckStyle(doc);
                    Assert.DoesNotContain(found, x => x.DocumentPath != null && x.DocumentPath.EndsWith("LegacyApp.flsnp"));
                }
            }
        }

        [Fact]
        public void Story06b_WriterFixesATextSnippet_TheChangeLandsInTheSnippet_AndTheTopicIsUntouched()
        {
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>Open the console to <MadCap:snippetText src=\"Resources/Snippets/term.flsnp\" /> your settings.</p>"))
            {
                fx.Ws.WriteFile(Snippet, SnippetFile("<p>utilize</p>"));
                fx.Check();
                var f = fx.Session.Findings.Single(x => x.DocumentPath != null && x.RuleId == "company:plain-language/1");
                fx.Session.Select(f);

                Assert.Equal(AcceptOutcome.Applied, fx.Session.Accept());

                var snippet = fx.Ws.OpenDocuments.Single(d => d.SourcePath == f.DocumentPath);
                Assert.Equal(1, snippet.UndoCount);
                snippet.AcceptAll();
                Assert.Equal("Use", snippet.BodyText.Trim());
                Assert.Equal(fx.Original, fx.Doc.Xml);
            }
        }

        [Fact]
        public void Story06c_WriterFixesAConditionalSnippet_TheConditionOnTheTopicIsKept_AndTheFixIsInTheSnippet()
        {
            using (var fx = ReviewFixture.Scratch("<h1>Tools</h1><p>Use the console.</p><MadCap:snippetBlock src=\"Resources/Snippets/term.flsnp\" MadCap:conditions=\"Default.Print\" />"))
            {
                fx.Ws.WriteFile(Snippet, SnippetFile("<p>Always utilize the console.</p>"));
                fx.Check();
                var f = fx.Session.Findings.Single(x => x.DocumentPath != null && x.RuleId == "company:plain-language/1");
                fx.Session.Select(f);

                Assert.Equal(AcceptOutcome.Applied, fx.Session.Accept());

                var snippet = fx.Ws.OpenDocuments.Single(d => d.SourcePath == f.DocumentPath);
                snippet.AcceptAll();
                Assert.Contains("Always use the console.", snippet.BodyText);
                Assert.Equal(fx.Original, fx.Doc.Xml);
                Assert.Contains("MadCap:conditions=\"Default.Print\"", fx.Doc.Xml);
            }
        }

        [Fact]
        public void Story06d_WriterDeclinesToChangeASharedSnippet_NothingIsOpenedOrWritten()
        {
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm"))
            {
                fx.Check();
                var f = fx.Session.Findings.Single(x => x.DocumentPath != null);
                fx.Host.ConfirmAnswer = false;
                fx.Session.Select(f);

                Assert.Equal(AcceptOutcome.Declined, fx.Session.Accept());

                Assert.Empty(fx.Ws.OpenedByPlugin);
                Assert.False(f.IsFixed);
            }
        }

        // ---- 7 · TEST-PLAN C1-C3 ----------------------------------------------------------------------------------

        [Fact]
        public void Story07_WriterChecksTheProject_ThenAcceptsAll_ReadOnlyFilesAreSkipped_OpenedFilesAreSavedAndClosed_OpenOnesStayUnsaved()
        {
            // C1 + C2: every topic and snippet linted once; accept all across the project; per-file result report.
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", projectReport: true))
            {
                var files = ActionRunner.ProjectFiles(fx.Doc.SourcePath);
                Assert.Equal(2, files.Count(p => p.EndsWith(".flsnp")));
                var found = fx.Runner.LintFiles(files);

                Assert.All(found, f => Assert.Contains(f.DocumentPath, files));
                var snippetPath = files.Single(p => p.EndsWith("LegacyApp.flsnp"));
                var alone = fx.Runner.LintFiles(new List<string> { snippetPath });
                Assert.NotEmpty(alone);
                Assert.Equal(alone.Count, found.Count(f => f.DocumentPath == snippetPath));   // not repeated for each of the topics that include it
                fx.Session.Add(found);

                var activePath = fx.Doc.SourcePath;
                var candidates = found.Where(f => f.CanFix && f.DocumentPath != activePath && !f.DocumentPath.EndsWith(".flsnp")).Select(f => f.DocumentPath).Distinct().ToList();
                var authorOpen = fx.Ws.Open(candidates[0]);   // the writer has a second topic open
                var readOnly = candidates[1];
                File.SetAttributes(readOnly, FileAttributes.ReadOnly);
                var readOnlyBefore = File.ReadAllText(readOnly);
                var authorBefore = File.ReadAllText(authorOpen.SourcePath);
                BatchResult result = null;
                fx.Session.BatchReported += r => result = r;
                var expected = fx.Session.FixAllCandidates();

                fx.Session.FixAll();

                // The batch reaches other files, so the writer saw a preview first, with the read-only file listed and why.
                var plan = fx.Host.Previews.Single();
                Assert.Equal("read-only", plan.Skipped.Single().Reason);
                Assert.Equal(readOnly, plan.Skipped.Single().Path);
                Assert.NotNull(result);
                Assert.Equal("read-only", result.Skipped.Single().Reason);
                Assert.Equal(readOnlyBefore, File.ReadAllText(readOnly));
                Assert.Empty(result.Failed);

                // Files the plugin opened were saved with their tracked changes and closed; the writer's own open ones were not.
                var byPlugin = fx.Ws.OpenedByPlugin.Distinct().ToList();
                Assert.NotEmpty(byPlugin);
                Assert.DoesNotContain(authorOpen.SourcePath, byPlugin);
                Assert.Equal(byPlugin.Count, result.SavedFiles);
                Assert.Equal(new[] { activePath, authorOpen.SourcePath }.OrderBy(p => p).ToArray(), fx.Ws.OpenDocuments.Select(d => d.SourcePath).OrderBy(p => p).ToArray());
                foreach (var path in byPlugin) Assert.Contains("MadCap:changes", File.ReadAllText(path));
                Assert.Equal(authorBefore, File.ReadAllText(authorOpen.SourcePath));
                Assert.Equal(0, authorOpen.SaveCount);
                Assert.Equal(1, authorOpen.UndoCount);
                Assert.Equal(1, fx.Doc.UndoCount);
                Assert.Equal(expected.Count - result.Skipped.Sum(s => s.Fixes), result.FixesApplied);
                Assert.Contains("Skipped", result.Report());
                Assert.Contains("tracked and saved", result.Status());
            }
        }

        // ---- 8 · TEST-PLAN C3 -------------------------------------------------------------------------------------

        [Fact]
        public void Story08_GoTo_SelectsExactlyTheFindingsText_ForTwoHundredFindingsAcrossTablesStepsLinksAndDropdowns()
        {
            var sb = new System.Text.StringBuilder("<h1>Tools</h1>");
            sb.Append("<table><tbody>");
            for (var i = 0; i < 50; i++) sb.Append("<tr><td>Cell " + i + "</td><td>Always utilize option " + i + " here.</td></tr>");
            sb.Append("</tbody></table><ol>");
            for (var i = 0; i < 50; i++) sb.Append("<li>Step " + i + ": utilize the <b>Tool " + i + "</b> and utilize it twice.</li>");
            sb.Append("</ol>");
            for (var i = 0; i < 50; i++) sb.Append("<p>See <MadCap:xref href=\"t" + i + ".htm\">utilize the tool " + i + "</MadCap:xref> for more.</p>");
            for (var i = 0; i < 50; i++) sb.Append("<MadCap:dropDown><MadCap:dropDownHead><MadCap:dropDownHotspot>How to utilize item " + i + "</MadCap:dropDownHotspot></MadCap:dropDownHead><MadCap:dropDownBody><p>Body " + i + ".</p></MadCap:dropDownBody></MadCap:dropDown>");
            using (var fx = ReviewFixture.Scratch(sb.ToString()))
            {
                fx.Check();
                var mine = fx.Session.Findings.Where(f => f.RuleId == "company:plain-language/1").ToList();
                Assert.True(mine.Count >= 200, mine.Count + " findings");
                Assert.Equal(new[] { "dropDownHotspot", "li", "td", "xref" }, mine.Select(f => Context(fx, f)).Distinct().OrderBy(c => c).ToArray());   // all four contexts are covered

                foreach (var f in mine)
                {
                    XmlText run; int start;
                    Assert.True(FindingLocator.TryLocate(fx.Doc.Document, f, out run, out start));
                    fx.Session.GoTo(f, true);

                    Assert.Same(run, fx.Doc.SelectedRun);
                    int s, l;
                    TextRuns.UniqueSpan(run.Value, start, f.Excerpt.Length, out s, out l);
                    Assert.Equal(s, fx.Doc.SelectionStart);                        // the right occurrence, not the first "utilize" of the run
                    Assert.Equal(l, fx.Doc.SelectionLength);
                    Assert.Equal(run.Value.Substring(s, l), fx.Doc.SelectionText);
                    Assert.Contains(f.Excerpt, fx.Doc.SelectionText);
                }
                Assert.Empty(fx.Doc.UntrackedWrites);
            }
        }

        private static string Context(ReviewFixture fx, StyleFinding f)
        {
            XmlText run; int start;
            FindingLocator.TryLocate(fx.Doc.Document, f, out run, out start);
            for (XmlNode n = run.ParentNode; n != null; n = n.ParentNode)
                if (n.LocalName == "td" || n.LocalName == "li" || n.LocalName == "xref" || n.LocalName == "dropDownHotspot") return n.LocalName;
            return "";
        }
    }
}
