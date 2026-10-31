using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Scenarios.Support;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Scenarios.Safety
{
    /// <summary>
    /// The safety net around AI edits: variables, conditions, cross-references and snippets survive a rewrite untouched, an
    /// answer that would break them is refused before it reaches the topic, and no edit takes a path that crashes Flare.
    /// Model answers are scripted; the editor is FlareSim (one undo step, the variable redo crash).
    /// </summary>
    public class SafetyNetScenarios
    {
        private static readonly Dictionary<string, string> Vars = new Dictionary<string, string> { { "General.ProductName", "Acme" } };

        private const string Rich =
            "<h1>Setup</h1><p>In order to start, open <MadCap:variable name=\"General.ProductName\" /> and choose <b>Settings</b>. Then see <MadCap:xref href=\"guide.htm\">the setup guide</MadCap:xref> for more.</p>";

        private static XmlNode Block(ReviewFixture fx, int index) { return BlockSelector.SelectBlocks(fx.Doc.Document, new List<XmlNode>())[index]; }

        private static IList<XmlNode> Blocks(ReviewFixture fx, params int[] indexes)
        {
            var all = BlockSelector.SelectBlocks(fx.Doc.Document, new List<XmlNode>());
            return indexes.Select(i => all[i]).ToList();
        }

        /// <summary>The identity of everything Flare-specific in the blocks: variables, xrefs, snippets, bold labels and every condition.</summary>
        private static string[] Pieces(ReviewFixture fx)
        {
            var ns = new XmlNamespaceManager(fx.Doc.Document.NameTable);
            ns.AddNamespace("MadCap", BlockSelector.MadCapNs);
            var pieces = new List<string>();
            foreach (XmlNode n in fx.Doc.Document.SelectNodes("//MadCap:variable | //MadCap:xref | //MadCap:snippetText | //MadCap:snippetBlock | //b | //*[@MadCap:conditions]", ns))
                pieces.Add(n.LocalName + "|" + n.OuterXml.Substring(0, n.OuterXml.IndexOf('>') + 1));
            return pieces.ToArray();
        }

        // ---- 11 · TEST-PLAN B1 + B2 -------------------------------------------------------------------------------

        [Fact]
        public void Story11_WriterRewritesAParagraphWithAVariableABoldLabelAndAnXref_TheyComeBackUntouched_AsWordLevelEdits()
        {
            // B1: word-level tracked changes. B2: the variable and the bold label are untouched (and the xref).
            var llm = new ScriptedLlmClient().When(r => r.Task == "rewrite", ScriptedReview.RewriteRun("In order to start", "To start"));
            using (var fx = ReviewFixture.Scratch(Rich, llm, Vars))
            {
                var before = Pieces(fx);
                Assert.Equal(3, before.Length);

                var proposal = fx.Runner.Rewrite(fx.Doc, Blocks(fx, 1), "Make this plainer.");

                Assert.True(proposal.Validation.IsValid);
                Assert.Contains("open", llm.Requests.Last(r => r.Task == "rewrite").User);
                Assert.Contains("⟦r1⟧", llm.Requests.Last(r => r.Task == "rewrite").User);   // the model edits numbered runs; it never writes markup
                Assert.NotNull(proposal.WordPlan);
                Assert.NotEmpty(proposal.WordPlan);

                var summary = fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite");

                Assert.StartsWith("Applied as", summary);
                Assert.Contains("word-level", summary);
                Assert.Equal(1, fx.Doc.UndoCount);
                Assert.Empty(fx.Doc.UntrackedWrites);
                Assert.Contains("To start, open", fx.AcceptedText);
                Assert.Equal(before, Pieces(fx));    // variable, xref, condition and bold label attributes: identical
                fx.Doc.AcceptAll();
                Assert.Equal(before, Pieces(fx));
                Assert.Contains("To start, open", fx.Doc.BodyText);
            }
        }

        // ---- 12 · TEST-PLAN B3 / B4 -------------------------------------------------------------------------------

        private const string Fragile =
            "<h1>Setup</h1><p MadCap:conditions=\"Default.Print\">Open <MadCap:variable name=\"General.ProductName\" /> and see <MadCap:xref href=\"guide.htm\">the guide</MadCap:xref> or <MadCap:snippetText src=\"Resources/Snippets/term.flsnp\" /> today.</p>";

        private static void Refused(string revised, string expectedError)
        {
            var llm = new ScriptedLlmClient().When(r => r.Task == "restructure", ScriptedReview.Revised(revised));
            using (var fx = ReviewFixture.Scratch(Fragile, llm, Vars))
            {
                var proposal = fx.Runner.Restructure(fx.Doc, Blocks(fx, 1), "Split this into two sentences.");

                // The reason is reported to the writer…
                Assert.False(proposal.Validation.IsValid);
                Assert.Contains(proposal.Validation.Errors, e => e.Contains(expectedError));
                // …Apply refuses, and the topic is exactly as it was.
                Assert.Throws<InvalidOperationException>(() => fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite"));
                Assert.Equal(fx.Original, fx.Doc.Xml);
                Assert.Equal(0, fx.Doc.UndoCount);
                Assert.Empty(fx.Doc.UntrackedWrites);
            }
        }

        [Theory]
        [InlineData("<p MadCap:conditions=\"Default.Print\">Open the product and see <MadCap:xref href=\"guide.htm\">the guide</MadCap:xref> or <MadCap:snippetText src=\"Resources/Snippets/term.flsnp\" /> today.</p>", "Dropped or altered: MadCap:variable")]
        [InlineData("<p MadCap:conditions=\"Default.Online\">Open <MadCap:variable name=\"General.ProductName\" /> and see <MadCap:xref href=\"guide.htm\">the guide</MadCap:xref> or <MadCap:snippetText src=\"Resources/Snippets/term.flsnp\" /> today.</p>", "MadCap:conditions=\"Default.Print\"")]
        [InlineData("<p MadCap:conditions=\"Default.Print\">Open <MadCap:variable name=\"General.ProductName\" /> and see <MadCap:xref href=\"guide-2.htm\">the guide</MadCap:xref> or <MadCap:snippetText src=\"Resources/Snippets/term.flsnp\" /> today.</p>", "Dropped or altered: MadCap:xref")]
        [InlineData("<p MadCap:conditions=\"Default.Print\">Open <MadCap:variable name=\"General.ProductName\" /> and see <MadCap:xref href=\"guide.htm\">the guide</MadCap:xref> or the console today.</p>", "Dropped or altered: MadCap:snippetText")]
        [InlineData("<p MadCap:conditions=\"Default.Print\">Open <b>the product and see the guide today.</p>", "not well-formed")]
        public void Story12_AnAnswerThatBreaksTheMarkup_IsRefused_AndTheTopicIsUnchanged(string body, string message)
        {
            Refused(body, message);
        }

        [Fact]
        public void Story12f_AWordLevelRewriteThatSmugglesInMarkup_IsRetriedOnce_ThenKeepsNothing_AndTheTopicIsUnchanged()
        {
            // The run-based rewrite path: the model may only give plain text for numbered runs.
            var llm = new ScriptedLlmClient().When(r => r.Task == "rewrite", ScriptedReview.RewriteWith("r1", "To start, open <MadCap:variable name=\"General.Other\" /> now"));
            using (var fx = ReviewFixture.Scratch(Rich, llm, Vars))
            {
                var proposal = fx.Runner.Rewrite(fx.Doc, Blocks(fx, 1), "Make this plainer.", lint: false);

                Assert.Equal(2, llm.Requests.Count(r => r.Task == "rewrite"));          // asked again, with the breach spelled out
                Assert.Contains("contains markup", llm.Requests.Last(r => r.Task == "rewrite").User);
                Assert.Empty(proposal.WordPlan);                                          // nothing from the bad answer is kept
                Assert.Equal("No wording changes to apply.", fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite"));
                Assert.Equal(fx.Original, fx.Doc.Xml);
                Assert.Equal(0, fx.Doc.UndoCount);
            }
        }

        // ---- 13 · TEST-PLAN B4 ------------------------------------------------------------------------------------

        private const string ConditionalList = "<h1>Steps</h1><ul><li>Alpha step</li><li MadCap:conditions=\"Default.Print\">Beta step</li><li>Gamma step</li></ul>";

        [Fact]
        public void Story13_WriterMakesAListWithAConditionalItemATable_TheResultIsValidated_AndTheConditionStaysOnItsRow()
        {
            // B4: either a valid restructure or a clear refusal; never a broken topic.
            var table = "<table><tbody><tr><td>Alpha step</td></tr><tr MadCap:conditions=\"Default.Print\"><td>Beta step</td></tr><tr><td>Gamma step</td></tr></tbody></table>";
            var llm = new ScriptedLlmClient().When(r => r.Task == "restructure", ScriptedReview.Revised(table));
            using (var fx = ReviewFixture.Scratch(ConditionalList, llm))
            {
                var proposal = fx.Runner.Restructure(fx.Doc, Blocks(fx, 1), "Make this a table.");

                Assert.True(proposal.Validation.IsValid);                           // the condition stays on its row (#109)
                Assert.Null(proposal.WordPlan);   // the structure changed: a paragraph-level change, not word edits
                var rows = proposal.ProposedBlocks[0].SelectNodes(".//tr").Cast<XmlElement>().ToList();
                Assert.Equal(new[] { "", "Default.Print", "" }, rows.Select(r => r.GetAttribute("conditions", BlockSelector.MadCapNs)).ToArray());
                Assert.Equal("Beta step", rows[1].InnerText);

                // …but Flare can't turn a list into a table as a tracked change (#111): a clear refusal, never a broken topic.
                Assert.Equal("House Style can't apply this edit: it turns a bulleted list into a table, and Flare can't make that change as a tracked change. " +
                             "Make this change yourself, or ask for an edit that keeps a bulleted list.", proposal.ApplyBlocked);
                Assert.Throws<InvalidOperationException>(() => fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite"));
                Assert.Equal(fx.Original, fx.Doc.Xml);
                Assert.Equal(0, fx.Doc.UndoCount);
            }
        }

        [Theory]
        [InlineData("<h1>Steps</h1><ul><li>Alpha step</li><li>Beta step</li></ul>", "<table><tbody><tr><td>Alpha step</td></tr><tr><td>Beta step</td></tr></tbody></table>", "Make this a table.")]
        [InlineData("<h1>Steps</h1><p>Open the app, then choose Settings.</p>", "<ul><li>Open the app.</li><li>Choose Settings.</li></ul>", "Make this a list.")]
        [InlineData("<h1>Steps</h1><p>Open the app, then choose Settings.</p>", "<p>Do this:</p><ul><li>Open the app.</li><li>Choose Settings.</li></ul>", "Make this a list.")]
        public void Story13c_ARestructureThatChangesTheElement_NeverLeavesTheOldTagAroundTheNewContent(string body, string answer, string instruction)
        {
            // #111: ReplaceDocumentNode keeps the target's element name (flare-api-notes). A changed first element is refused;
            // a restructure that keeps it (p → p + ul) is applied, and every new block has its own element.
            var llm = new ScriptedLlmClient().When(r => r.Task == "restructure", ScriptedReview.Revised(answer));
            using (var fx = ReviewFixture.Scratch(body, llm))
            {
                var proposal = fx.Runner.Restructure(fx.Doc, Blocks(fx, 1), instruction);
                Assert.True(proposal.Validation.IsValid);
                var keepsElement = proposal.ProposedBlocks[0].LocalName == Block(fx, 1).LocalName;

                if (!keepsElement)
                {
                    Assert.Contains("Flare can't make that change as a tracked change", proposal.ApplyBlocked);
                    Assert.Throws<InvalidOperationException>(() => fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite"));
                    Assert.Equal(fx.Original, fx.Doc.Xml);
                    return;
                }
                Assert.Null(proposal.ApplyBlocked);
                fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite");
                fx.Doc.AcceptAll();
                var top = BlockSelector.FindBody(fx.Doc.Document).ChildNodes.OfType<XmlElement>().Select(e => e.LocalName).ToArray();
                Assert.Equal(new[] { "h1", "p", "ul" }, top);
                Assert.Empty(fx.Doc.Document.SelectNodes("//p//ul").Cast<XmlNode>());
            }
        }

        [Fact]
        public void Story13b_AnAnswerThatMovesTheConditionToAnotherRow_IsRefused()
        {
            var wrong = "<table><tbody><tr><td>Alpha step</td></tr><tr><td>Beta step</td></tr><tr MadCap:conditions=\"Default.Print\"><td>Gamma step</td></tr></tbody></table>";
            var llm = new ScriptedLlmClient().When(r => r.Task == "restructure", ScriptedReview.Revised(wrong));
            using (var fx = ReviewFixture.Scratch(ConditionalList, llm))
            {
                var proposal = fx.Runner.Restructure(fx.Doc, Blocks(fx, 1), "Make this a table.");

                Assert.False(proposal.Validation.IsValid);
                Assert.Contains(proposal.Validation.Errors, e => e.Contains("Moved a condition") && e.Contains("\"Beta step\"") && e.Contains("\"Gamma step\""));
                Assert.Throws<InvalidOperationException>(() => fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite"));
                Assert.Equal(fx.Original, fx.Doc.Xml);
            }
        }

        // ---- 14 · TEST-PLAN B2 ------------------------------------------------------------------------------------

        [Fact]
        public void Story14a_ARewriteOfTextWithAVariable_IsWordLevel_AndUndoThenRedoNeverTakesTheCrashPath()
        {
            var llm = new ScriptedLlmClient().When(r => r.Task == "rewrite", ScriptedReview.RewriteRun("In order to start", "To start"));
            using (var fx = ReviewFixture.Scratch(Rich, llm, Vars))
            {
                var proposal = fx.Runner.Rewrite(fx.Doc, Blocks(fx, 1), "Make this plainer.");
                fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite");
                var edited = fx.Doc.Xml;

                fx.Doc.Undo();
                Assert.Equal(fx.Original, fx.Doc.Xml);
                fx.Doc.Redo();   // FlareSim throws FlareSimRedoCrashException if the edit replaced a node that holds the variable
                Assert.Equal(edited, fx.Doc.Xml);
            }
        }

        [Fact]
        public void Story14b_ARestructureOfTextWithAVariable_NeverReplacesTheNodeThatHoldsIt()
        {
            // #110: the answer is valid, but a node replacement holding a variable crashes Flare on Redo: refused, with the reason.
            var split = "<p>In order to start, open <MadCap:variable name=\"General.ProductName\" />.</p><p>Choose <b>Settings</b>. Then see <MadCap:xref href=\"guide.htm\">the setup guide</MadCap:xref> for more.</p>";
            var llm = new ScriptedLlmClient().When(r => r.Task == "restructure", ScriptedReview.Revised(split));
            using (var fx = ReviewFixture.Scratch(Rich, llm, Vars))
            {
                var proposal = fx.Runner.Restructure(fx.Doc, Blocks(fx, 1), "Split this into two paragraphs.");
                Assert.True(proposal.Validation.IsValid);
                Assert.NotNull(proposal.ApplyBlocked);                               // the result window disables Apply and says why
                Assert.Contains("variable (General.ProductName)", proposal.ApplyBlocked);
                Assert.Contains("wording-only edit", proposal.ApplyBlocked);

                var refused = Assert.Throws<InvalidOperationException>(() => fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite"));
                Assert.Equal(proposal.ApplyBlocked, refused.Message);
                Assert.Equal(fx.Original, fx.Doc.Xml);
                Assert.Equal(0, fx.Doc.UndoCount);

                // Even a proposal that skipped the check (the word plan could not be located) never reaches the node write.
                proposal.ApplyBlocked = null;
                Assert.Throws<InvalidOperationException>(() => fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite"));
                Assert.Equal(0, fx.Doc.UndoCount);
            }
        }

        [Fact]
        public void Story14c_ARestructureOfTextWithoutAVariable_IsStillApplied_AndRedoes()
        {
            var plain = "<h1>Setup</h1><p>In order to start, open the app and choose <b>Settings</b>. Then see <MadCap:xref href=\"guide.htm\">the setup guide</MadCap:xref> for more.</p>";
            var split = "<p>To start, open the app and choose <b>Settings</b>.</p><p>Then see <MadCap:xref href=\"guide.htm\">the setup guide</MadCap:xref> for more.</p>";
            var llm = new ScriptedLlmClient().When(r => r.Task == "restructure", ScriptedReview.Revised(split));
            using (var fx = ReviewFixture.Scratch(plain, llm))
            {
                var proposal = fx.Runner.Restructure(fx.Doc, Blocks(fx, 1), "Split this into two paragraphs.");
                Assert.Null(proposal.ApplyBlocked);
                Assert.Contains("paragraph-level", fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite"));
                var edited = fx.Doc.Xml;
                fx.Doc.Undo();
                fx.Doc.Redo();
                Assert.Equal(edited, fx.Doc.Xml);
            }
        }

        // ---- 15 · TEST-PLAN B5 ------------------------------------------------------------------------------------

        [Fact]
        public void Story15_WriterRewritesOverAnUnacceptedTrackedChange_TheAiSeesTheAcceptedText_AndEditsLandAroundTheChange()
        {
            // B5: the AI sees the current text (the insertion, not the deleted text); the edit lands around the pending change.
            var llm = ScriptedLlmClient.NoFindings().When(r => r.Task == "rewrite", ScriptedReview.RewriteRun("In order to", "To"));
            using (var fx = ReviewFixture.Scratch("<h1>Setup</h1><p>In order to utilize the tool, open the console.</p>", llm))
            {
                // The writer (or an earlier Accept) already has a tracked change in the paragraph: utilize → use.
                fx.Check();
                var utilize = fx.Session.Findings.Single(f => f.RuleId == "company:plain-language/1");
                fx.Runner.ApplyFixes(fx.Doc, new[] { utilize }, "House Style: tracked");
                Assert.True(TrackedChanges.HasPending(Blocks(fx, 1)));
                Assert.Contains("utilize", fx.Doc.BodyText);                       // the deleted word is still in the markup
                Assert.Contains("In order to use the tool", fx.AcceptedText);

                var proposal = fx.Runner.Rewrite(fx.Doc, Blocks(fx, 1), "Make this plainer.", lint: false);

                var prompt = llm.Requests.Last(r => r.Task == "rewrite").User;
                Assert.Matches("⟦r\\d+⟧use⟦/r\\d+⟧", prompt);                       // the insertion, as a run of its own
                Assert.DoesNotContain("utilize", prompt);                          // not the deleted text
                Assert.DoesNotContain("MadCap:change", prompt);

                fx.Runner.Apply(fx.Doc, proposal, "House Style: rewrite");

                Assert.Equal(2, fx.Doc.UndoCount);
                Assert.Contains("To use the tool, open the console.", fx.AcceptedText);
                Assert.True(TrackedChanges.HasPending(Blocks(fx, 1)));            // the first change is still pending, not swallowed
                fx.Doc.AcceptAll();
                Assert.Contains("To use the tool, open the console.", fx.Doc.BodyText);
                Assert.DoesNotContain("utilize", fx.Doc.BodyText);
            }
        }

        [Fact]
        public void Story15b_ARestructureOverAnUnacceptedTrackedChange_IsRefusedWithAClearReason()
        {
            var split = "<p>In order to use the tool.</p><p>Open the console.</p>";
            var llm = ScriptedLlmClient.NoFindings().When(r => r.Task == "restructure", ScriptedReview.Revised(split));
            using (var fx = ReviewFixture.Scratch("<h1>Setup</h1><p>In order to utilize the tool, open the console.</p>", llm))
            {
                fx.Check();
                fx.Runner.ApplyFixes(fx.Doc, new[] { fx.Session.Findings.Single(f => f.RuleId == "company:plain-language/1") }, "House Style: tracked");
                var before = fx.Doc.Xml;

                var proposal = fx.Runner.Restructure(fx.Doc, Blocks(fx, 1), "Split this into two paragraphs.");

                Assert.False(proposal.Validation.IsValid);
                Assert.Contains(proposal.Validation.Errors, e => e.Contains("pending tracked changes"));
                Assert.Equal(before, fx.Doc.Xml);
            }
        }
    }
}
