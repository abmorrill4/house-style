using System.Collections.Generic;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Integration.Tests.Smoke
{
    /// <summary>
    /// Smoke: a writer checks deposit/about.htm, applies every mechanical fix, then presses Ctrl+Z once.
    /// Drives ActionRunner (lint + AI review + ApplyFixes) through FlareSim, with a scripted review that finds nothing.
    /// </summary>
    public class CheckAcceptAllUndoTest
    {
        [Fact]
        public void Writer_ChecksATopic_AcceptsAllFixes_AndOneUndoRestoresIt()
        {
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                // Given a writer with the topic open
                var doc = ws.Open("Content/deposit/about.htm");
                var original = doc.Xml;
                var style = new StyleEngine(new[] { Repo.StyleGuide }, Repo.StyleGuide);
                var llm = ScriptedLlmClient.NoFindings();
                var runner = new ActionRunner(llm, PromptBuilder.For(style, doc.SourcePath), 16000, style, doc.SourcePath) { VariableValue = doc.VariableValue };

                // When they check the topic
                var findings = runner.CheckStyle(doc);
                var fixes = findings.FindAll(f => f.CanFix && f.Kind == "mechanical" && f.DocumentPath == null);
                Assert.True(llm.Calls > 0, "the AI review ran");
                Assert.NotEmpty(fixes);

                // …and accept all mechanical fixes
                var applied = runner.ApplyFixes(doc, fixes, "House Style: " + fixes.Count + " style fixes");

                // Then every fix is a tracked change in one undo step, made only through the adapter
                Assert.Equal(fixes.Count, applied);
                Assert.Equal(1, doc.UndoCount);
                Assert.Empty(doc.UntrackedWrites);
                Assert.True(TrackedChanges.HasPending(new List<XmlNode> { BlockSelector.FindBody(doc.Document) }));
                var edited = doc.Xml;

                // …and with the changes accepted, the fixed hits are gone
                doc.AcceptAll();
                var after = runner.Lint(runner.PickCheckBlocks(doc));
                foreach (var f in fixes)
                    Assert.DoesNotContain(after, a => a.Rule == f.Rule && a.Excerpt == f.Excerpt && a.Before == f.Before);
                doc.Undo();   // un-accept
                Assert.Equal(edited, doc.Xml);

                // When they press Ctrl+Z once, the topic is back byte for byte; Redo brings the fixes back
                doc.Undo();
                Assert.Equal(original, doc.Xml);
                doc.Redo();
                Assert.Equal(edited, doc.Xml);

                // …and the topic saves cleanly
                doc.Save();
                Assert.Equal(1, doc.SaveCount);
            }
        }
    }
}
