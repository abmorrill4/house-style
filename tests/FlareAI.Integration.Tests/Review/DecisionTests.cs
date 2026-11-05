using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Integration.Tests.Support;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FlareAI.Integration.Tests.Review
{
    /// <summary>What happens to a suggestion the writer turns down, and to a review the writer has already paid for.</summary>
    public class DecisionTests
    {
        private const string Utilize = "company:plain-language/1";

        private static List<JObject> FeedbackLines()
        {
            return File.ReadAllLines(Paths.Feedback).Where(l => l.Trim().Length > 0).Select(l => JObject.Parse(l)).ToList();
        }

        // ---- 9 · TEST-PLAN A4 -------------------------------------------------------------------------------------

        [Fact]
        public void Story09_WriterRejectsWithEachReason_ItStaysHiddenForThatTextOnly_AndTheReasonIsRecorded()
        {
            // A4: the suggestion disappears; feedback.jsonl gets a rejected line with the reason.
            var body = "<h1>Tools</h1><p>First utilize the list.</p><p>Second utilize the grid.</p><p>Third utilize the menu.</p><p>Fourth utilize the log.</p>";
            using (var fx = ReviewFixture.Scratch(body))
            {
                fx.Check();
                var all = fx.Session.Findings.Where(f => f.RuleId == Utilize).OrderBy(f => f.Order).ToList();
                Assert.Equal(4, all.Count);
                var reasons = new[] { "wrong", "fine here", "rule doesn't apply" };
                for (var i = 0; i < reasons.Length; i++)
                {
                    fx.Session.Select(all[i]);
                    fx.Session.Reject(reasons[i]);
                    Assert.DoesNotContain(all[i], fx.Session.Findings);                                  // gone at once
                    Assert.Contains("Ignored. It stays hidden for this text.", fx.Session.Status);
                    Assert.NotNull(fx.Session.Current);                                                  // the next one is selected
                }

                var lines = FeedbackLines().Where(l => (string)l["decision"] == "rejected").ToList();
                Assert.Equal(reasons, lines.Select(l => (string)l["reason"]).ToArray());
                Assert.All(lines, l => Assert.Equal(Utilize, (string)l["rule_id"]));
                Assert.Equal(new[] { "utilize", "utilize", "utilize" }, lines.Select(l => (string)l["excerpt"]).ToArray());

                // Next check, a new pane (a restart reads the same dismissals): only the fourth is offered. Edits to the text don't matter, the others stay hidden.
                var again = fx.NewSession();
                fx.Check(again);
                var left = again.Findings.Where(f => f.RuleId == Utilize).ToList();
                Assert.Single(left);
                Assert.Equal("Fourth ", left[0].Before);

                // The same wording in another topic is a different place: it is offered.
                fx.Ws.WriteFile("Content/other.htm", string.Format(ReviewFixture.Wrapper, "<h1>Other</h1><p>First utilize the list.</p>"));
                var other = fx.Ws.Open("Content/other.htm");
                var otherSession = new ReviewSession(fx.Runner, other, fx.Host, Dismissals.Load(Paths.Dismissed), false);
                otherSession.Add(fx.Runner.Lint(fx.Runner.PickCheckBlocks(other)));
                Assert.Contains(otherSession.Findings, f => f.RuleId == Utilize);
            }
        }

        // ---- 10 · TEST-PLAN A10 -----------------------------------------------------------------------------------

        [Fact]
        public void Story10_WriterReopensAReviewedTopic_CachedAiFindingsAppearWithoutACall_AndEditedTextIsReviewedAgain()
        {
            // A10: saved proposals appear at once; only changed text goes to the model.
            var ai = ScriptedReview.Review(new ScriptedReview.Flag { Excerpt = "can be out of date", Rule = "Timeless wording", Replacement = "may be out of date" });
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", ai))
            {
                int calls;
                var first = fx.PaneCheck(new ReviewCache(300, Paths.Reviews), out calls);
                Assert.True(calls > 0);
                Assert.Equal(calls, ai.Calls);
                Assert.Contains(first, f => f.Source == "ai" && f.Excerpt == "can be out of date");
                var paid = ai.Calls;
                Assert.True(File.Exists(Paths.Reviews));

                // Close and reopen (a restart): the cache is read from disk.
                var reopened = fx.NewSession();
                var second = fx.PaneCheck(new ReviewCache(300, Paths.Reviews), out calls, reopened);
                Assert.Equal(0, calls);
                Assert.Equal(paid, ai.Calls);                                       // not a single new call
                Assert.Contains(second, f => f.Source == "ai" && f.Excerpt == "can be out of date");
                Assert.Equal(first.Count, second.Count);
                Assert.Equal(first.Select(f => f.Excerpt).ToArray(), second.Select(f => f.Excerpt).ToArray());

                // The writer rewords the sentence: its section has new text, so it is reviewed again; the rest is not asked twice.
                var run = (XmlText)BlockSelector.FindBody(fx.Doc.Document).SelectSingleNode("//p[@MadCap:conditions]/text()", Ns(fx.Doc.Document));
                fx.Doc.AuthorEdits(() => run.Value = run.Value.Replace("can be out of date", "might be out of date"));
                fx.PaneCheck(new ReviewCache(300, Paths.Reviews), out calls, fx.NewSession());
                Assert.True(calls > 0);
                Assert.Equal(paid + calls, ai.Calls);
            }
        }

        [Fact]
        public void Story10b_WriterPressesCheckAgainOnAnUnchangedTopic_AFreshReviewIsMade()
        {
            // A10 (#108): reopening serves the saved review; Check again on unchanged text asks the AI again, once per paragraph.
            var ai = ScriptedReview.Review(new ScriptedReview.Flag { Excerpt = "can be out of date", Rule = "Timeless wording", Replacement = "may be out of date" });
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", ai))
            {
                var cache = new ReviewCache(300, Paths.Reviews);
                int first, reopened, again;
                fx.PaneCheck(cache, out first);
                Assert.True(first > 0);
                fx.PaneCheck(cache, out reopened, fx.NewSession());
                Assert.Equal(0, reopened);                                                    // opening the pane: the saved review

                var fresh = fx.PaneCheck(cache, out again, fx.NewSession(), checkAgain: true);
                Assert.Equal(first, again);                                                   // Check again: every paragraph reviewed afresh
                Assert.Equal(first * 2, ai.Calls);
                Assert.Contains(fresh, f => f.Source == "ai" && f.Excerpt == "can be out of date");

                fx.PaneCheck(cache, out reopened, fx.NewSession());
                Assert.Equal(0, reopened);                                                    // the fresh review is what is saved now
            }
        }

        [Fact]
        public void Story10c_WriterAppliesAFixThenPressesCheckAgain_TheFixedParagraphIsReanchored_NotReviewedAgain()
        {
            // #108 keeps the re-anchoring: a paragraph changed only by an applied fix keeps the rest of its review without a call.
            var ai = ScriptedReview.Review(new ScriptedReview.Flag { Excerpt = "can be out of date", Rule = "Timeless wording", Replacement = "is sometimes out of date" });
            using (var fx = new ReviewFixture("DigitalBanking", "Content/deposit/about.htm", ai))
            {
                var cache = new ReviewCache(300, Paths.Reviews);
                int first, again;
                var found = fx.PaneCheck(cache, out first);
                var fix = found.First(f => f.Source == "ai" && f.Excerpt == "can be out of date");
                fx.Session.Select(fix);
                Assert.Equal(AcceptOutcome.Applied, fx.Session.Accept());

                fx.PaneCheck(cache, out again, fx.NewSession(), checkAgain: true);
                Assert.Equal(first - 1, again);                                               // every paragraph but the fixed one
            }
        }

        private static XmlNamespaceManager Ns(XmlDocument doc)
        {
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("MadCap", BlockSelector.MadCapNs);
            return ns;
        }
    }
}
