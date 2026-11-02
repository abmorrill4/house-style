using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// The cost changes from docs/testing/ai-cost.md, asserted on request shape (what is sent, what is cached), not on
    /// model answers: every client here is scripted.
    /// </summary>
    public class AiCostTests
    {
        internal static string Repo
        {
            get
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
                return d.FullName;
            }
        }

        internal static StyleEngine Bundled()
        {
            var g = Path.Combine(Repo, "styleguide");
            return new StyleEngine(new[] { g }, g);
        }

        // ---- 1. Validate style guide: the team's layers by default, in parallel -------------------------------------------

        [Fact]
        public void ValidateAiCheck_SkipsGoogleExamplesUnlessAsked()
        {
            var all = Bundled().Examples(null);
            Assert.True(all.Count(e => e.Layer == "google") > 500);           // compiled Google pages: validated offline
            var own = ActionRunner.ExamplesToVerify(all, false);
            Assert.NotEmpty(own);
            Assert.All(own, e => Assert.NotEqual("google", e.Layer));
            Assert.Equal(all.Count, ActionRunner.ExamplesToVerify(all, true).Count);
            Assert.True(ActionRunner.VerifyCalls(own) < 20);                   // seconds and cents, not 886 calls
        }

        [Fact]
        public void ValidateAiCheck_RunsCallsInParallel_AndKeepsResultsInOrder()
        {
            var style = new FakeStyleService();
            int now = 0, peak = 0;
            var overlapped = new ManualResetEventSlim(false);
            var llm = new FakeLlmClient(r =>
            {
                var n = Interlocked.Increment(ref now);
                lock (style) peak = Math.Max(peak, n);
                if (n > 1) overlapped.Set();
                // Wait for a second call to be in flight, not a fixed sleep: CI runners have 2 cores and a thread pool that
                // ramps up slowly, so a short sleep can finish before the next call starts. Sequential calls time out here.
                overlapped.Wait(5000);
                Interlocked.Decrement(ref now);
                return r.User.Contains("The end user")
                    ? @"[{""rule_id"":""company:terminology/1"",""rule"":""audience"",""severity"":""warning"",""excerpt"":""The end user"",""replacement"":""Administrators"",""why"":""x""}]"
                    : "[]";
            });
            var runner = new ActionRunner(llm, new PromptBuilder(() => style.Guide(null)), 1024, style) { Parallelism = 4 };
            var examples = Enumerable.Range(0, 6).Select(i => new GuideExample
            {
                Id = "company:terminology/1", Layer = "company", Bad = i % 2 == 0 ? "The end user approves request " + i + "." : "Someone approves request " + i + ".",
                Good = "Administrators approve request " + i + "."
            }).ToList();
            var progress = new List<int>();
            var results = runner.VerifyExamples(examples, (d, t) => { lock (progress) progress.Add(d); });

            Assert.Equal(12, llm.Requests.Count);
            Assert.True(peak > 1, "calls ran one at a time");
            Assert.Equal(12, progress.Max());
            for (var i = 0; i < 6; i++)
            {
                Assert.Same(examples[i], results[i].Example);
                Assert.Equal(i % 2 == 0, results[i].BadCaught);
                Assert.True(results[i].GoodClean);
            }
        }

        // ---- 2. Cache lifetime per task; one shared prefix for review, rewrite and restructure --------------------------

        /// <summary>A runner over the fake guide whose model answers every task with an empty, valid contract.</summary>
        internal static ActionRunner Runner(FakeLlmClient llm, FakeStyleService style = null)
        {
            style = style ?? new FakeStyleService();
            return new ActionRunner(llm, PromptBuilder.For(style, null), 1024, style);
        }

        internal static FakeLlmClient Quiet()
        {
            return new FakeLlmClient(r =>
                r.Task == "restructure" ? "<revised>" + FragmentOf(r.User) + "</revised>"
                : r.Task == "review" ? "[]"
                : r.Task == "alt" ? "{\"decorative\":false,\"alt\":\"The Transfers page.\"}"
                : r.Task == "assist" ? "{\"description\":\"d\",\"keywords\":[],\"groups\":[],\"micro_content\":[],\"glossary\":[]}"
                : r.Task == "extract" ? "{\"rules\":[]}"
                : r.Task == "refine" ? "{\"replacement\":\"Administrators\",\"why\":\"x\",\"applies\":true}"
                : "{\"findings\":[],\"changes\":[],\"needs_restructure\":false,\"note\":\"\",\"revised\":\"\"}");
        }

        private static string FragmentOf(string user)
        {
            var i = user.LastIndexOf("Fragment:", StringComparison.Ordinal);
            return user.Substring(i + "Fragment:".Length).Trim();
        }

        private static Newtonsoft.Json.Linq.JObject Body(FlareAI.Core.Llm.LlmRequest r)
        {
            return Newtonsoft.Json.Linq.JObject.Parse(FlareAI.Core.Llm.AnthropicClient.BuildBody("claude-sonnet-5-5", r));
        }

        [Fact]
        public void ReviewRewriteAndRestructure_ShareOneCachedPrefix_CachedForAnHour()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            var llm = Quiet();
            var runner = Runner(llm);
            var blocks = runner.PickBlocks(doc);
            runner.Review(blocks, runner.Lint(blocks));
            runner.Rewrite(doc, blocks, "Shorten this.");
            runner.Restructure(doc, blocks, "Make this a list.");

            var review = llm.Requests.First(r => r.Task == "review");
            var rewrite = llm.Requests.First(r => r.Task == "rewrite");
            var restructure = llm.Requests.First(r => r.Task == "restructure");
            foreach (var other in new[] { rewrite, restructure })
            {
                // Same model, same schema (it sits ahead of the system prompt in the cache key), same cached system block.
                Assert.Equal(review.Model, other.Model);
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Body(review)["output_config"], Body(other)["output_config"]));
                Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(Body(review)["system"][0], Body(other)["system"][0]));
            }
            Assert.Equal("1h", (string)Body(review)["system"][0]["cache_control"]["ttl"]);
            // What differs (which fields to fill, the task) is in the user message, after the cache.
            Assert.StartsWith(Contracts.ReviewFields, review.User);
            Assert.StartsWith(Contracts.RewriteFields, rewrite.User);
            Assert.StartsWith(Contracts.RestructureFields, restructure.User);
        }

        [Fact]
        public void Restructure_ReadsTheRevisedField()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            doc.SelectXPath("//body/p[2]/text()");
            var llm = new FakeLlmClient(r => "{\"findings\":[],\"changes\":[],\"needs_restructure\":false,\"note\":\"\",\"revised\":\"<ul><li>One.</li><li>Two.</li></ul>\"}");
            var runner = Runner(llm);
            var proposal = runner.Restructure(doc, runner.PickBlocks(doc), "Make this a list.");
            Assert.Equal("<ul><li>One.</li><li>Two.</li></ul>", proposal.ProposedXml);
        }

        [Theory]
        [InlineData("draft", "5m")]
        [InlineData("extract", "5m")]
        [InlineData("refine", "5m")]
        [InlineData("alt", "none")]
        [InlineData("assist", "none")]
        [InlineData("review", "1h")]
        [InlineData("rewrite", "1h")]
        public void RarelyUsedTasks_DontPayForAnHourLongCacheWrite(string task, string ttl)
        {
            Assert.Equal(ttl, new FlareAI.Core.Llm.Routing().CacheFor(task));
            var body = Body(new FlareAI.Core.Llm.LlmRequest { Task = task, System = "system", User = "u", CacheTtl = ttl });
            var control = body["system"][0]["cache_control"];
            if (ttl == "none") Assert.Null(control);
            else if (ttl == "5m") { Assert.Equal("ephemeral", (string)control["type"]); Assert.Null(control["ttl"]); }
            else Assert.Equal("1h", (string)control["ttl"]);
        }

        // ---- 3. No style guide where no style rule is applied --------------------------------------------------------------

        [Fact]
        public void AltTextAndTopicAssists_DontCarryTheStyleGuide()
        {
            var style = new FakeStyleService { GuideText = "- [company:terminology/1] Address the reader as \"you\", not \"the end user\"." };
            var llm = Quiet();
            var runner = Runner(llm, style);
            runner.SuggestAlt(new LlmImageFile { MediaType = "image/png", Base64 = "iVBORw0KGgo=" }, "Transfers");
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            runner.Assist(runner.PickAllBlocks(doc));
            runner.Refine(new StyleFinding { Excerpt = "users", Replacement = "administrators", Suggestion = "x" }, "The users can sign in.", "Shorter");

            var plain = llm.Requests.Where(r => r.Task == "alt" || r.Task == "assist").ToList();
            Assert.Equal(4, plain.Count);
            foreach (var r in plain)
            {
                Assert.DoesNotContain("the end user", r.System);
                Assert.True(r.System.Length < 1000, r.Task + " system prompt is " + r.System.Length + " characters");
                Assert.Null(r.SystemPacks);
                Assert.Null(r.SystemTerms);
                Assert.Null(Body(r)["system"][0]["cache_control"]);
            }
            Assert.Contains("the end user", llm.Requests.Single(r => r.Task == "refine").System);   // refine still applies the rule
        }

        // ---- 4. A one-paragraph rewrite carries the rules wording can act on --------------------------------------------

        [Fact]
        public void Rewrite_SendsTriggeredWordRules_NotFormattingPacks()
        {
            var engine = Bundled();
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = FlareAI.Core.Editing.XhtmlFragment.ParseInto("<p>The <code>listMessages</code> method will return every message in the mailbox, and the client should page through the results with the <code>pageToken</code> field.</p>", doc.Document)[0];
            doc.Document.SelectSingleNode("//body").AppendChild(p);
            var llm = Quiet();
            var runner = new ActionRunner(llm, PromptBuilder.For(engine, null), 1024, engine);
            var blocks = new List<System.Xml.XmlNode> { p };
            runner.Review(blocks, runner.Lint(blocks));
            runner.Rewrite(doc, blocks, null);

            var review = llm.Requests.First(r => r.Task == "review").SystemTerms;
            var rewrite = llm.Requests.First(r => r.Task == "rewrite").SystemTerms;
            Assert.Contains("In ordinary text sentences, use code font", review);   // review still judges formatting
            Assert.DoesNotContain("In ordinary text sentences, use code font", rewrite);   // a run-level rewrite can't change it
            Assert.DoesNotContain("series of x", rewrite);
            Assert.Contains("(will)", rewrite);                   // the text says "will": its word rule comes along
            Assert.DoesNotContain("(click)", rewrite);            // words the text doesn't use: their rules stay home
            Assert.True(rewrite.Length * 3 < review.Length, "rewrite " + rewrite.Length + " vs review " + review.Length);
            Assert.Equal(llm.Requests.First(r => r.Task == "review").System, llm.Requests.First(r => r.Task == "rewrite").System);
        }

        // ---- 5. Re-check after applying the review's own fixes: re-anchor, don't re-review --------------------------------

        private const string TwoFindings = @"[
            {""rule_id"":""general"",""rule"":""plain words"",""severity"":""warning"",""excerpt"":""utilize"",""replacement"":""use"",""why"":""Plain words.""},
            {""rule_id"":""general"",""rule"":""wordy"",""severity"":""suggestion"",""excerpt"":""Please note that the fee"",""replacement"":""The fee"",""why"":""Wordy.""}]";

        private static System.Xml.XmlNode Paragraph(FakeDocumentAdapter doc, string xml)
        {
            var p = FlareAI.Core.Editing.XhtmlFragment.ParseInto(xml, doc.Document)[0];
            var body = doc.Document.SelectSingleNode("//body");
            while (body.FirstChild != null) body.RemoveChild(body.FirstChild);
            body.AppendChild(p);
            return p;
        }

        private static List<StyleFinding> Check(ActionRunner runner, ReviewCache cache, System.Xml.XmlNode p, out ReviewUnit unit)
        {
            var blocks = new List<System.Xml.XmlNode> { p };
            var lint = runner.Lint(blocks);
            unit = runner.PrepareReview(blocks, lint)[0];
            var raw = cache.Get(unit);
            if (raw == null) { raw = runner.CallReview(unit); cache.Put(unit, raw); }
            return runner.AnchorReview(raw, unit, lint);
        }

        [Fact]
        public void RecheckAfterApplyingTheReviewsFix_ReanchorsTheRest_WithoutAModelCall()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = Paragraph(doc, "<p>We utilize the <b>Transfers</b> page to move funds. Please note that the fee is waived for members.</p>");
            var llm = new FakeLlmClient(r => TwoFindings);
            var runner = new ActionRunner(llm, new PromptBuilder(null), 1024);
            var cache = new ReviewCache();
            ReviewUnit unit;
            var first = Check(runner, cache, p, out unit);
            Assert.Equal(2, first.Count);
            Assert.Equal(1, runner.ApplyFixes(doc, first.Where(f => f.Excerpt == "utilize"), "fix"));

            var again = Check(runner, cache, p, out unit);
            Assert.Single(llm.Requests);                                         // no second review
            var left = Assert.Single(again);
            Assert.Equal("Please note that the fee", left.Excerpt);
            Assert.True(left.CanFix);
            Assert.Equal(1, runner.ApplyFixes(doc, again, "fix"));
            Assert.Contains("We use the", p.InnerText);
            Assert.Contains("The fee is waived", p.InnerText);
            Assert.Empty(Check(runner, cache, p, out unit));                     // both applied: nothing left, still no call
            Assert.Single(llm.Requests);
        }

        [Fact]
        public void RecheckAfterAFixSplitsTheRun_AsATrackedChangeDoes_StillReanchors()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = Paragraph(doc, "<p>We utilize the app. Please note that the fee is waived.</p>");
            var llm = new FakeLlmClient(r => TwoFindings);
            var runner = new ActionRunner(llm, new PromptBuilder(null), 1024);
            var cache = new ReviewCache();
            ReviewUnit unit;
            Check(runner, cache, p, out unit);
            // Flare writes the fix as a tracked change: the accepted view reads "We " + "use" + " the app. …" as three runs.
            p.RemoveAll();
            foreach (var t in new[] { "We ", "use", " the app. Please note that the fee is waived." }) p.AppendChild(doc.Document.CreateTextNode(t));

            var again = Check(runner, cache, p, out unit);
            Assert.Single(llm.Requests);
            var left = Assert.Single(again);
            Assert.Equal("r3", left.RunId);                                      // moved to the run that now holds it
        }

        [Fact]
        public void RecheckAfterTheAuthorRewroteText_ReviewsAgain()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = Paragraph(doc, "<p>We utilize the app. Please note that the fee is waived.</p>");
            var llm = new FakeLlmClient(r => TwoFindings);
            var runner = new ActionRunner(llm, new PromptBuilder(null), 1024);
            var cache = new ReviewCache();
            ReviewUnit unit;
            Check(runner, cache, p, out unit);
            ((System.Xml.XmlText)p.FirstChild).Value = "We utilize the app. The fee is waived for new members only.";
            Check(runner, cache, p, out unit);
            Assert.Equal(2, llm.Requests.Count);
        }

        // ---- 7. Quality ------------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("should have reported", "must report", true)]
        [InlineData("Clients should", "Clients must", true)]
        [InlineData("you may", "you must", true)]
        [InlineData("You should click Save", "Click Save", false)]
        [InlineData("Call listMessages to page", "Call the method to page", true)]
        [InlineData("set max_results", "set the maximum", true)]
        [InlineData("Connect GP1 to the board", "Connect the pin to the board", true)]
        [InlineData("utilize", "use", false)]
        [InlineData("e.g. a PIN", "for example, a PIN", false)]
        [InlineData("Please note that the fee", "The fee", false)]
        [InlineData("The feature will be released", "The feature is released", false)]
        public void MeaningGuard_HoldsModalAndCodeTermChanges(string before, string after, bool held)
        {
            Assert.Equal(held, MeaningGuard.Check(before, after) != null);
        }

        [Fact]
        public void Review_AFindingThatChangesAnObligation_KeepsTheFindingButNotTheFix()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = Paragraph(doc, "<p>Clients should retry the request after a short wait, and they should utilize the <code>retryAfter</code> value.</p>");
            var llm = new FakeLlmClient(r => @"[
                {""rule_id"":""general"",""rule"":""normative"",""severity"":""warning"",""excerpt"":""Clients should retry"",""replacement"":""Clients must retry"",""why"":""Be direct.""},
                {""rule_id"":""general"",""rule"":""plain words"",""severity"":""warning"",""excerpt"":""utilize"",""replacement"":""use"",""why"":""Plain words.""},
                {""rule_id"":""general"",""rule"":""code"",""severity"":""warning"",""excerpt"":""retryAfter"",""replacement"":""retry_after"",""why"":""x""}]");
            var runner = new ActionRunner(llm, new PromptBuilder(null), 1024);
            var blocks = new List<System.Xml.XmlNode> { p };
            var found = runner.Review(blocks, new List<StyleFinding>());
            var modal = found.Single(f => f.Rule == "normative");
            Assert.Null(modal.Replacement);
            Assert.Contains("\"should\" to \"must\"", modal.Suggestion);
            Assert.DoesNotContain(found, f => f.Excerpt == "retryAfter" && f.Replacement != null);   // text inside <code> is never fixed
            Assert.Equal("use", found.Single(f => f.Rule == "plain words").Replacement);
        }

        [Theory]
        [InlineData("end users", "administrators", false, null)]
        [InlineData("end users", "administrator", false, null)]
        [InlineData("end users", "managers", true, null)]
        [InlineData("end users", "sysadmins", true, null)]
        [InlineData("end users", "operators", false, "operators")]
        public void RuleWords_FlagsOnlyWordsOutsideTheRuleAndProject(string excerpt, string replacement, bool outside, string project)
        {
            var rule = new GuideRule { Text = "Use the role, such as administrators, not end users", Good = "Administrators must approve the request." };
            var projectText = project == null ? new string[0] : new[] { project };
            Assert.Equal(outside, RuleWords.Outside(excerpt, replacement, rule, projectText).Count > 0);
        }

        [Theory]
        [InlineData("managers", "Choose")]
        [InlineData("administrators", null)]
        [InlineData("operators", null)]
        public void Review_AReplacementUnderAWordRuleOutsideTheRule_KeepsTheFindingAskingTheWriterToChoose(string replacement, string choose)
        {
            var engine = Bundled();
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var text = replacement == "operators"
                ? "<p>The end users must approve the request before the billing cycle closes, and operators watch the queue.</p>"
                : "<p>The end users must approve the request before the billing cycle closes.</p>";
            var p = Paragraph(doc, text);
            var llm = new FakeLlmClient(r => @"[{""rule_id"":""company:terminology/1"",""rule"":""audience"",""severity"":""warning"",""excerpt"":""end users"",""replacement"":""" + replacement + @""",""why"":""Name the role.""}]");
            var runner = new ActionRunner(llm, PromptBuilder.For(engine, null), 1024, engine);
            var found = runner.Review(new List<System.Xml.XmlNode> { p }, new List<StyleFinding>());
            var f = found.Single(x => x.RuleId == "company:terminology/1");
            if (choose != null)
            {
                Assert.Null(f.Replacement);
                Assert.Contains(choose, f.Suggestion);
                Assert.False(f.CanFix);
            }
            else Assert.Equal(replacement, f.Replacement);
        }

        [Fact]
        public void Rewrite_AChangeThatDropsACodeTerm_IsHeldBack_WithTheReason()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = Paragraph(doc, "<p>Please note that you should utilize the pageToken field.</p><p>In order to sign in, open the app.</p>");
            doc.Document.SelectSingleNode("//body").AppendChild(FlareAI.Core.Editing.XhtmlFragment.ParseInto("<p>In order to sign in, open the app.</p>", doc.Document)[0]);
            var llm = new FakeLlmClient(r => FakeLlmClient.EditRuns(r, t => t.Replace("Please note that you should utilize the pageToken field.", "Use the page field.").Replace("In order to", "To")));
            var runner = new ActionRunner(llm, new PromptBuilder(null), 1024);
            var blocks = doc.Document.SelectNodes("//body/p").Cast<System.Xml.XmlNode>().ToList();
            var proposal = runner.Rewrite(doc, blocks, null, false);
            Assert.Contains(proposal.Notes, n => n.Contains("Kept a sentence as it was") && n.Contains("\"pageToken\""));
            runner.Apply(doc, proposal, "x");
            Assert.Equal("Please note that you should utilize the pageToken field.", blocks[0].InnerText);
            Assert.Equal("To sign in, open the app.", blocks[1].InnerText);
        }

        [Fact]
        public void Review_CitingGooglesRule_CitesTheCompanyRuleThatOverridesIt()
        {
            var engine = Bundled();
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = Paragraph(doc, "<p>Our best-in-class transfer experience delights members, and you can choose the members who see your transfers.</p>");
            var second = engine.Rules(null).First(r => r.Page == "Second person" && !r.IsLint && r.Applies.Count == 0 && r.Triggers.Count == 0).Id;
            var llm = new FakeLlmClient(r => (@"[
                {""rule_id"":""google:excessive-claims/5"",""rule"":""marketing claim"",""severity"":""warning"",""excerpt"":""best-in-class"",""replacement"":null,""why"":""Unverifiable claim.""},
                {""rule_id"":""SECOND"",""rule"":""audience"",""severity"":""suggestion"",""excerpt"":""choose the members"",""replacement"":null,""why"":""x""}]").Replace("SECOND", second));
            var runner = new ActionRunner(llm, PromptBuilder.For(engine, null), 1024, engine);
            var found = runner.Review(new List<System.Xml.XmlNode> { p }, new List<StyleFinding>());
            var claim = found.Single(f => f.Rule == "marketing claim");
            Assert.Equal("company:claims/1", claim.RuleId);
            Assert.Equal("company", claim.RuleLayer);
            Assert.Equal(second, found.Single(f => f.Rule == "audience").RuleId);   // no team rule on that subject
        }

        [Fact]
        public void Review_GooglesRuleStays_WhenTheReasonIsNotAboutTheTeamRule()
        {
            var engine = Bundled();
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = Paragraph(doc, "<p>Our best-in-class transfer experience delights members, and you can choose the members who see your transfers.</p>");
            // The reason still names Google's rule ("excessive"), but nothing in the team's Claims rule.
            var llm = new FakeLlmClient(r => @"[{""rule_id"":""google:excessive-claims/5"",""rule"":""marketing claim"",""severity"":""warning"",""excerpt"":""best-in-class"",""replacement"":null,""why"":""This wording is excessive.""}]");
            var runner = new ActionRunner(llm, PromptBuilder.For(engine, null), 1024, engine);
            var found = runner.Review(new List<System.Xml.XmlNode> { p }, new List<StyleFinding>());
            var claim = found.Single(f => f.Rule == "marketing claim");
            Assert.Equal("google:excessive-claims/5", claim.RuleId);
            Assert.Equal("google", claim.RuleLayer);
        }

        [Fact]
        public void Review_ATeamRuleTheWriterTurnedOff_IsNotChosenOverGooglesRule()
        {
            var style = new FakeStyleService();
            style.GuideRules = new List<FlareAI.Core.Style.GuideRule>
            {
                new FlareAI.Core.Style.GuideRule { Id = "google:excessive-claims/5", Layer = "google", Page = "Excessive claims", Section = "Excessive claims", Text = "Avoid unverifiable claims." },
                new FlareAI.Core.Style.GuideRule { Id = "company:claims/1", Layer = "company", Page = "Claims", Section = "Promises", Text = "Do not make claims you can't verify." }
            };
            style.Off.Add("company:claims/1");
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var llm = new FakeLlmClient(r => @"[{""rule_id"":""google:excessive-claims/5"",""rule"":""marketing claim"",""severity"":""warning"",""excerpt"":""appropriate permissions"",""replacement"":null,""why"":""Unverifiable claim.""}]");
            var runner = new ActionRunner(llm, new PromptBuilder(() => style.Guide(null)), 1024, style);
            var f = runner.CheckStyle(doc).Single(x => x.Source == "ai");
            Assert.Equal("google:excessive-claims/5", f.RuleId);
            Assert.Equal("google", f.RuleLayer);
        }

        [Fact]
        public void WordingPass_ThatDeletesTheSentenceItMeantToMove_CantBeApplied()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = Paragraph(doc, "<p>Open the <b>Transfers</b> page to see your transfers. Each transfer shows its date, amount and status in the list.</p>");
            var llm = new FakeLlmClient(r => "{\"changes\":[{\"run_id\":\"r3\",\"text\":\" page to see your transfers.\"}],\"needs_restructure\":true,\"note\":\"Move the second sentence into its own paragraph.\"}");
            var runner = new ActionRunner(llm, new PromptBuilder(null), 1024);
            var proposal = runner.Rewrite(doc, new List<System.Xml.XmlNode> { p }, "Split this into two paragraphs", false);
            Assert.NotNull(proposal.ApplyBlocked);
            Assert.Contains("Each transfer shows its date", proposal.ApplyBlocked);
            Assert.Throws<InvalidOperationException>(() => runner.Apply(doc, proposal, "x"));
            Assert.Contains("Each transfer shows", p.InnerText);                 // nothing was lost
        }

        [Theory]
        [InlineData("Open the page. Each transfer shows its date and amount.", "Open the page. Every transfer lists its date and amount.", false)]
        [InlineData("Open the page. Each transfer shows its date and amount.", "Open the page.", true)]
        [InlineData("Click OK. Done.", "Click OK.", false)]   // too short to judge
        public void LostSentence_FindsADeletedSentence_NotARewordedOne(string before, string after, bool lost)
        {
            Assert.Equal(lost, ActionRunner.LostSentence(before, after) != null);
        }

        [Fact]
        public void WordingPass_ThatNeedsRestructure_ButKeepsEverySentence_CanBeApplied()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var p = Paragraph(doc, "<p>Open the <b>Transfers</b> page to see your transfers. Each transfer shows its date, amount and status in the list.</p>");
            var llm = new FakeLlmClient(r => "{\"changes\":[{\"run_id\":\"r3\",\"text\":\" page to see your transfers. Each transfer lists its date, amount and status.\"}],\"needs_restructure\":true,\"note\":\"Split into two paragraphs.\"}");
            var runner = new ActionRunner(llm, new PromptBuilder(null), 1024);
            var proposal = runner.Rewrite(doc, new List<System.Xml.XmlNode> { p }, "Split this into two paragraphs", false);
            Assert.Null(proposal.ApplyBlocked);
            runner.Apply(doc, proposal, "x");
        }

        [Fact]
        public void AltText_OverTheLimit_IsAskedOnceMore_ThenCut()
        {
            var longAlt = "The Transfers page of the online banking app with the New transfer button highlighted, the account list, the amount field and the date picker open";
            var stillLong = "The Transfers page with the New transfer button highlighted, the account list, the amount field, and the date picker open below it";
            var answers = new Queue<string>(new[] { longAlt, stillLong });
            var llm = new FakeLlmClient(r => "{\"decorative\":false,\"alt\":\"" + answers.Dequeue() + "\"}");
            var alt = Runner(llm).SuggestAlt(new LlmImageFile { MediaType = "image/png", Base64 = "iVBORw0KGgo=" }, "Transfers");
            Assert.Equal(2, llm.Requests.Count);
            Assert.Contains("at most 125 characters", llm.Requests[1].User);
            Assert.True(alt.Length <= Contracts.AltMaxLength, alt.Length + ": " + alt);
            Assert.StartsWith("The Transfers page with the New transfer button highlighted", alt);
            Assert.False(alt.EndsWith(",") || alt.EndsWith(" and"), alt);

            var once = new FakeLlmClient(r => "{\"decorative\":false,\"alt\":\"The Transfers page.\"}");
            Assert.Equal("The Transfers page.", Runner(once).SuggestAlt(new LlmImageFile { MediaType = "image/png", Base64 = "x" }, ""));
            Assert.Single(once.Requests);
            var schema = Contracts.Alt().ToString();
            Assert.Contains("at most 125 characters", schema);
            Assert.DoesNotContain("maxLength", schema);                          // structured outputs reject string constraints
        }

        [Fact]
        public void DocxNotes_AreChunkedLikeTheTxtCopy()
        {
            var dir = Path.Combine(Repo, @"tests\fixtures\notes");
            var txt = GuideBuilder.Chunks(NoteText.ReadFile(Path.Combine(dir, "team-guide.txt")));
            var docx = GuideBuilder.Chunks(NoteText.ReadFile(Path.Combine(dir, "team-guide.docx")));
            Assert.All(docx, c => Assert.True(c.Length <= 3000, c.Length + " characters"));
            Assert.True(docx.Count >= txt.Count * 2 / 3, "docx " + docx.Count + " chunks vs txt " + txt.Count);   // was 3 vs 18
            Assert.True(docx.Max(c => c.Length) < 1500, "largest docx chunk " + docx.Max(c => c.Length));
        }

        [Fact]
        public void Chunks_StartANewPieceAtAHeading()
        {
            var notes = "Voice and tone\n" + string.Join("\n", Enumerable.Range(1, 3).Select(i => "Rule " + i + ": write plainly, in the second person, for the reader in front of you.")) +
                        "\nWord list\n" + string.Join("\n", Enumerable.Range(1, 6).Select(i => "Term " + i + ": use sign in, not log in, when you mean the action of signing in."));
            var chunks = GuideBuilder.Chunks(notes);
            Assert.Contains(chunks, c => c.StartsWith("Word list"));
            Assert.All(chunks, c => Assert.False(c.Contains("Rule ") && c.Contains("Term "), c));
        }

        [Fact]
        public void Extract_ContextDependentWords_BecomeProseRules_PhrasesStayRows()
        {
            var rules = Newtonsoft.Json.Linq.JArray.Parse(@"[
                {""kind"":""row"",""title"":""Link text"",""avoid"":""click here, here, this link"",""use"":"""",""severity"":""warning"",""why"":""Say where the link goes."",""example"":""For details, click here."",""text"":"""",""bad"":"""",""good"":"""",""quote"":""never 'click here'""},
                {""kind"":""row"",""title"":""Once"",""avoid"":""once"",""use"":""after"",""severity"":""warning"",""why"":""Once means one time."",""example"":""Once you sign in, open Settings."",""text"":"""",""bad"":"""",""good"":"""",""quote"":""once -> after""},
                {""kind"":""row"",""title"":""Utilize"",""avoid"":""utilize"",""use"":""use"",""severity"":""warning"",""why"":""Plain words."",""example"":""Utilize the menu."",""text"":"""",""bad"":"""",""good"":"""",""quote"":""utilize""}]");
            var found = GuideBuilder.FromExtracted(rules);

            var link = found.Single(r => r.IsRow && r.Title == "Link text");
            Assert.Equal("click here, this link", link.Avoid);                   // phrases stay linter rows
            var here = found.Single(r => !r.IsRow && r.Text.Contains("\"here\""));
            Assert.Equal("For details, click here.", here.Bad);
            var once = found.Single(r => r.Title == "Once");
            Assert.False(once.IsRow);                                             // no blanket "once" row
            Assert.Contains("means \"after\"", once.Text);
            Assert.Equal("After you sign in, open Settings.", once.Good);
            Assert.True(found.Single(r => r.Title == "Utilize").IsRow);
            Assert.Equal(found.Count, found.Select(r => r.Id).Distinct().Count());
        }

        [Theory]
        [InlineData("{\"applies\":false,\"replacement\":\"Click Save to keep the payee\",\"why\":\"x\"}", "Select Save to keep the payee", "doesn't apply")]
        [InlineData("{\"applies\":true,\"replacement\":null,\"why\":\"Needs a rewrite.\"}", "Select Save to keep the payee", "dropped the fix")]
        [InlineData("{\"applies\":true,\"replacement\":\"You must select Save to keep the payee\",\"why\":\"Firmer.\"}", "Select Save to keep the payee", "\"must\"")]
        [InlineData("{\"applies\":true,\"replacement\":\"Select Save\",\"why\":\"Shorter.\"}", "Select Save", "Shorter.")]
        public void Refine_KeepsTheEarlierProposal_WhenTheInstructionDoesntApply(string answer, string expected, string because)
        {
            var llm = new FakeLlmClient(r => answer);
            var earlier = new StyleFinding { Excerpt = "Click Save to keep the payee", Replacement = "Select Save to keep the payee", Suggestion = "Use select.", Rule = "click" };
            var refined = Runner(llm).Refine(earlier, "Click Save to keep the payee.", "Use tap instead of press");
            Assert.Equal(expected, refined.Replacement);
            Assert.Contains(because, refined.Suggestion);
        }

        [Fact]
        public void TeamCanChangeACacheLifetime()
        {
            var dir = Path.Combine(Path.GetTempPath(), "flareai-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "FlareAI"));
            try
            {
                File.WriteAllText(Path.Combine(dir, @"FlareAI\models.json"), "{\"cache\":{\"restructure\":\"5m\",\"alt\":\"forever\"}}");
                var routing = new FlareAI.Core.Llm.Routing().WithTeamFile(dir);
                Assert.Equal("5m", routing.CacheFor("restructure"));
                Assert.Equal("none", routing.CacheFor("alt"));   // unknown values are ignored
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
