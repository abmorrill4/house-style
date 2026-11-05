using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using FlareAI.Integration.Tests.Support;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Integration.Tests.Guide
{
    /// <summary>The owner keeps the guide healthy: Validate finds broken rows, Tune shows what writers disputed, Draft proposes the change.</summary>
    public class MaintainGuideTests
    {
        private const string GoldenFolder = "tests/FlareAI.Integration.Tests/Guide/golden";

        private const string BrokenTopic =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\">\n<head><title>Broken</title></head>\n<body>\n<h1>Broken</h1>\n<table>\n" +
            "<tr><th>Avoid</th><th>Use</th><th>Severity</th><th>Why</th><th>Example</th></tr>\n" +
            "<tr id=\"rule-1\"><td>utilise</td><td>utilise it</td><td>warning</td><td>Plain words.</td><td>We utilise the tool.</td></tr>\n" +
            "<tr id=\"rule-2\"><td>&lt;b&gt;</td><td>strong</td><td>warning</td><td>Use semantic tags.</td><td>Press &lt;b&gt;Save&lt;/b&gt;.</td></tr>\n" +
            "<tr id=\"rule-3\"><td>colour</td><td>color</td><td>warning</td><td>US spelling.</td><td>The colour is red.</td></tr>\n" +
            "<tr id=\"rule-4\"><td>colour</td><td>hue</td><td>warning</td><td>Say hue.</td><td>The colour is red.</td></tr>\n" +
            "<tr id=\"rule-5\"><td>cancel</td><td>stop</td><td>warning</td><td>Plain words.</td><td>Now we begin.</td></tr>\n" +
            "</table>\n</body>\n</html>\n";

        private static GuideReport ValidateBroken(out List<GuideIssue> mine)
        {
            using (var ws = FakeWorkspace.Empty())
            {
                var rules = Path.Combine(ws.Root, "BrokenRules");
                Directory.CreateDirectory(rules);
                File.WriteAllText(Path.Combine(rules, "Broken.htm"), BrokenTopic, new UTF8Encoding(false));
                var guide = Files.TeamGuide(ws.Root, rules);
                var report = new StyleEngine(new[] { guide }, Repo.StyleGuide).Validate(null);
                mine = report.Issues.Where(i => i.File != null && i.File.EndsWith("Broken.htm")).ToList();
                return report;
            }
        }

        // ---- 26 · TEST-PLAN D1 ------------------------------------------------------------------------------------

        [Fact]
        public void Story26a_OwnerValidatesABrokenGuide_ARowWhoseFixBringsTheRuleBack_AndAnExampleThatNeverTriggers_AreReportedWithTheirRow()
        {
            // D1: every rule table row checked against its example; failures listed with the row.
            List<GuideIssue> mine;
            var report = ValidateBroken(out mine);

            Golden.Check(GoldenFolder, "validate-broken.txt", string.Join("\n", mine.Select(i => i.Level + " | " + i.Location + " | " + i.Message).ToArray()));
            Assert.Contains(mine, i => i.Level == "error" && i.Location == "table row 2 (company:broken/1)" && i.Message.StartsWith("Applying the fix doesn't resolve the rule"));
            Assert.Contains(mine, i => i.Level == "error" && i.Location == "table row 2 (company:broken/1)" && i.Message.Contains("itself flagged by this same rule"));
            Assert.Contains(mine, i => i.Level == "error" && i.Location == "table row 6 (company:broken/5)" && i.Message.StartsWith("The Example doesn't trigger this rule"));
            Assert.All(mine, i => Assert.Equal("Broken.htm", i.File));
            Assert.True(report.Errors >= 3);
        }

        [Fact]
        public void Story26b_ARowWhosePatternIsMarkupOnly_IsReportedWithItsRow()
        {
            List<GuideIssue> mine;
            ValidateBroken(out mine);

            Assert.Contains(mine, i => i.Location.Contains("(company:broken/2)"));
        }

        [Fact]
        public void Story26c_TwoRowsForTheSameWordsInOneTopic_AreReportedWithTheirRows()
        {
            List<GuideIssue> mine;
            ValidateBroken(out mine);

            Assert.Contains(mine, i => i.Location.Contains("(company:broken/4)") && i.Message.Contains("company:broken/3"));
        }

        // ---- 27 · TEST-PLAN D4 ------------------------------------------------------------------------------------

        private static string Feedback { get { return Path.Combine(Repo.Fixtures, "feedback"); } }

        private static List<TuneItem> Tune()
        {
            var files = FeedbackStats.FilesIn(Feedback).ToList();
            Assert.Equal(5, files.Count);                                                       // one file per writer
            return FeedbackStats.Analyze(FeedbackStats.Anonymize(FeedbackStats.Load(files)));   // attribution = counts: nobody is named
        }

        [Fact]
        public void Story27a_OwnerTunesTheGuideFromAFiveWriterFeedbackFolder_DisputedOftenRejectedEditedAndTurnOffRequestsAreListedWithEvidence()
        {
            // D4: the disputed rule shows as disputed, with who accepts and who rejects.
            var items = Tune();

            Golden.Check(GoldenFolder, "tune-five-writers.txt", string.Join("\n", items.Select(i =>
                i.Kind + " | " + i.Rule + " | " + i.Title + " | +" + i.Accepted + " -" + i.Rejected + " | " + FeedbackStats.WriterCount(i.Writers.Count) + "\n    " + string.Join("\n    ", i.Evidence.ToArray())).ToArray()));
            var byKind = items.ToDictionary(i => i.Kind);
            Assert.Equal(new[] { "disputed", "review", "edit", "ignore", "working" }, items.Select(i => i.Kind).ToArray());   // most urgent first
            Assert.Equal("company:terminology/3", byKind["disputed"].Rule);
            Assert.Contains("Writer 1 changes it (100% of 5)", byKind["disputed"].Evidence);
            Assert.Contains("Writer 3 ignores it (100% of 5)", byKind["disputed"].Evidence);
            Assert.Equal("company:plain-language/5", byKind["review"].Rule);
            Assert.Equal(5, byKind["review"].Writers.Count);
            Assert.Equal("company:plain-language/1", byKind["edit"].Rule);
            Assert.Contains("Candidate Avoid/Use row: avoid utilize, use make use of", byKind["edit"].Evidence);
            Assert.Equal("google:word-list/251", byKind["ignore"].Rule);
            Assert.Equal(2, byKind["ignore"].Writers.Count);
            Assert.All(items, i => Assert.All(i.Evidence.Concat(i.Writers), e => Assert.DoesNotContain("writer-", e)));
        }

        [Fact]
        public void Story27b_OwnerDraftsAChangeWithAi_TheReplayedWordingIsWrittenIntoTheGuideTopicAsTrackedChanges()
        {
            using (var ws = FakeWorkspace.Empty())
            {
                var guide = Files.TeamGuide(ws.Root);
                var style = new StyleEngine(new[] { guide }, Repo.StyleGuide);
                var item = Tune().Single(i => i.Kind == "disputed");
                var rule = style.Rules(null).Single(r => r.Id == item.Rule);
                var topicPath = Path.Combine(guide, @"Content\Resources\StyleGuide\Company\Terminology.htm");
                var before = File.ReadAllText(topicPath);
                var doc = ws.Open(topicPath);

                var block = GuideEdits.Locate(doc.Document, rule.Text);
                Assert.NotNull(block);
                Assert.Equal("tr", block.LocalName);                                             // the Avoid/Use row, not the bullet next to it
                var instruction = GuideEdits.Instruction(item, rule, true);
                Assert.Contains("Writers disagree", instruction);
                Assert.Contains("Writer 3 ignores it", instruction);
                var llm = new ScriptedLlmClient().When(r => r.Task == "draft", ScriptedReview.RewriteRun("The product calls the account that owns users an organization.", "The product calls the account that owns users an organization. Keep tenant when it quotes a screen."));
                var runner = new ActionRunner(llm, PromptBuilder.For(style, null), 16000, style, null);

                var proposal = runner.Rewrite(doc, new List<XmlNode> { block }, instruction, false, "draft");   // lint off: a guide quotes the words it bans
                Assert.True(proposal.Validation.IsValid);
                runner.Apply(doc, proposal, "House Style: draft guide change");

                Assert.Equal(1, llm.Requests.Count(r => r.Task == "draft"));
                Assert.Equal(1, doc.UndoCount);
                Assert.Empty(doc.UntrackedWrites);
                Assert.True(TrackedChanges.HasPending(new List<XmlNode> { block }));
                Assert.Contains("Keep tenant when it quotes a screen.", doc.Document.SelectSingleNode("//tr[@id='rule-3']").InnerText.Replace("\n", " "));
                Assert.Equal(before, File.ReadAllText(topicPath));                              // nothing is on disk until the owner reviews and saves

                doc.AcceptAll();                                                                // the owner reviews it in Flare, then saves
                doc.Save();
                var again = new StyleEngine(new[] { guide }, Repo.StyleGuide);
                var changed = again.Rules(null).Single(r => r.Id == item.Rule);
                Assert.Contains("Keep tenant when it quotes a screen.", changed.Text);
                Assert.Equal(rule.Id, changed.Id);                                               // same rule, same id
                Assert.Equal(0, again.Validate(null).Errors);
            }
        }
    }
}
