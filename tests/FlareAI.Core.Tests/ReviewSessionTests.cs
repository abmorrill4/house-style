using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// The review pane's decisions, driven without the pane: one test per branch of Apply, Apply all fixes, Write all,
    /// Reject, Skip, Edit, staleness and Check this paragraph again, Turn off this rule, Go to, Next and the summary.
    /// </summary>
    public class ReviewSessionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-session-" + Guid.NewGuid().ToString("N"));
        private readonly string _topicPath, _snippetPath, _otherPath;
        private readonly FakeDocumentAdapter _topic, _snippet, _other;
        private readonly FakeStyleService _style = new FakeStyleService();
        private readonly FakeHost _host = new FakeHost();
        private readonly List<string> _feedback = new List<string>();
        private readonly List<string> _guideWrites = new List<string>();
        private readonly List<string> _projectWrites = new List<string>();
        private Team _team;

        public ReviewSessionTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Resources\Snippets"));
            File.WriteAllText(Path.Combine(_root, "Test.flprj"), "<CatapultProject />");
            _topicPath = Write(@"Content\topic.htm",
                "<html><head /><body><h1>Start</h1><p>Please utilize the tool. The user can see it.</p><h2>More</h2><p>Then utilize the menu.</p></body></html>");
            _otherPath = Write(@"Content\other.htm", "<html><head /><body><p>Never utilize jargon.</p></body></html>");
            _snippetPath = Write(@"Content\Resources\Snippets\shared.flsnp", "<html><head /><body><p>Always utilize care.</p></body></html>");
            _topic = new FakeDocumentAdapter(_topicPath, true);
            _snippet = new FakeDocumentAdapter(_snippetPath, true);
            _other = new FakeDocumentAdapter(_otherPath, true);
            _host.Documents[_topicPath] = _topic;
            _host.Documents[_snippetPath] = _snippet;
            _host.Documents[_otherPath] = _other;
            _style.GuideRules.Add(new GuideRule { Id = "google:words/2", Layer = "google", Section = "Word list", Text = "Use use, not utilize." });
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        private string Write(string relative, string xml)
        {
            var path = Path.Combine(_root, relative);
            File.WriteAllText(path, xml);
            return path;
        }

        private ReviewSession Session(bool project = false)
        {
            var runner = new ActionRunner(new FakeLlmClient(r => "[]"), new PromptBuilder(null), 1024, _style);
            var s = new ReviewSession(runner, _topic, _host, Dismissals.Load(Path.Combine(_root, "dismissed.json")), project)
            {
                CurrentTeam = () => _team,
                RecordFeedback = (f, topic, decision, reason) => { _feedback.Add(decision + "|" + reason + "|" + f.Rule); return null; },
                SetGuideRule = (team, id, setting, reason) => { _guideWrites.Add(id + "=" + setting); return Path.Combine(_root, "guide.htm"); },
                SetProjectRule = (path, f, setting, reason, by) => _projectWrites.Add((f.RuleId ?? f.Rule) + "=" + setting + " by " + by),
                IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SnippetUsage = p => 3
            };
            _host.Attach(s);
            return s;
        }

        /// <summary>A lint finding for the <paramref name="occurrence"/>th <paramref name="text"/> in a document.</summary>
        private static StyleFinding Find(FakeDocumentAdapter doc, string text, string replacement, string rule = "Google.WordList", int occurrence = 0,
                                         string ruleId = null, string documentPath = null)
        {
            var seen = 0;
            foreach (var run in TextRuns.Of(new List<XmlNode> { BlockSelector.FindBody(doc.Document) }))
            {
                var at = -1;
                while ((at = run.Value.IndexOf(text, at + 1, StringComparison.Ordinal)) >= 0)
                {
                    if (seen++ < occurrence) continue;
                    var f = FindingLocator.FromLint(new LintHit { Start = at, Length = text.Length, Match = text, Replacement = replacement, Rule = rule, Severity = "warning", Message = "Use plain words.", Layer = "google", RuleId = ruleId }, run);
                    f.DocumentPath = documentPath;
                    return f;
                }
            }
            throw new InvalidOperationException("no " + text);
        }

        private string TopicText() { return BlockSelector.FindBody(_topic.Document).InnerText; }

        // ---- fixed by hand ------------------------------------------------------------------------

        private static XmlText FirstRun(FakeDocumentAdapter doc, int paragraph)
        {
            return (XmlText)BlockSelector.FindBody(doc.Document).SelectNodes("p")[paragraph].FirstChild;
        }

        [Fact]
        public void RecheckEdited_SkipsFindingsWithoutAnExcerpt()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            f.Excerpt = "";
            s.Add(new[] { f });
            FirstRun(_topic, 0).Value = "Please use the tool. The user can see it.";
            Assert.Equal(0, s.RecheckEdited());
            Assert.False(f.IsFixed);
        }

        [Fact]
        public void RecheckEdited_ExcerptNotInTheText_StaysOpen()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            f.Excerpt = "zzz";
            s.Add(new[] { f });
            FirstRun(_topic, 0).Value = "Please use the tool. The user can see it.";
            Assert.Equal(0, s.RecheckEdited());
            Assert.False(f.IsFixed);
        }

        [Fact]
        public void RecheckEdited_ExcerptWithRegexCharacters_CountsLiterally()
        {
            FirstRun(_topic, 0).Value = "Go (see [Note b) now.";
            var s = Session();
            var f = Find(_topic, "(see [Note", null);
            s.Add(new[] { f });
            FirstRun(_topic, 0).Value = "Go now.";
            Assert.Equal(1, s.RecheckEdited());
            Assert.True(f.IsFixed && f.FixedByEdit);
        }

        [Fact]
        public void RecheckEdited_DetachedBlock_ComparesBodyCountWithBodyBaseline()
        {
            var s = Session();
            var first = Find(_topic, "utilize", "use");
            var second = Find(_topic, "utilize", "use", occurrence: 1);
            s.Add(new[] { first, second });
            var body = BlockSelector.FindBody(_topic.Document);
            body.RemoveChild(body.SelectNodes("p")[0]);
            Assert.Equal(1, s.RecheckEdited());
            Assert.True(first.IsFixed);
            Assert.False(second.IsFixed);
        }

        [Fact]
        public void MarkFixed_RecordsNoDismissalOrFeedback()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);
            s.MarkFixed();
            Assert.True(f.IsFixed && f.MarkedFixed);
            Assert.Empty(_feedback);
            Assert.Empty(_projectWrites);
            Assert.False(File.Exists(Path.Combine(_root, "dismissed.json")));
            Assert.Empty(Directory.GetFiles(_root, "*feedback*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(_root, "requests", SearchOption.AllDirectories));
            Assert.EndsWith("Marked as fixed.", s.Status);
        }

        [Fact]
        public void RecheckEdited_NeverReopensAMarkedRow()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });
            s.Select(f);
            s.MarkFixed();
            var original = FirstRun(_topic, 0).Value;
            FirstRun(_topic, 0).Value = original.Replace("utilize", "use");
            s.RecheckEdited();
            FirstRun(_topic, 0).Value = original;
            s.RecheckEdited();
            FirstRun(_topic, 0).Value = original.Replace("utilize", "use");
            s.RecheckEdited();
            FirstRun(_topic, 0).Value = original;
            s.RecheckEdited();
            Assert.True(f.MarkedFixed);
            Assert.True(f.IsFixed);
        }

        [Fact]
        public void WritingEvent_CountsPendingHandFixesFirst_AndDoesNotSortUntilWrote()
        {
            var s = Session();
            var a = Find(_topic, "utilize", "use");
            var b = Find(_topic, "utilize", "use", occurrence: 1);
            s.Add(new[] { a, b });
            var changes = 0;
            s.ListChanged += () => changes++;
            var atWriting = -1; var aFixedAtWriting = false;
            Action<string> probe = path => { atWriting = changes; aFixedAtWriting = a.FixedByEdit; };
            s.WatchWrites(true);
            ActionRunner.Writing += probe;
            try
            {
                FirstRun(_topic, 0).Value = "Please use the tool. The user can see it.";
                var before = changes;
                s.Select(b);
                s.Accept();
                Assert.True(aFixedAtWriting);
                Assert.Equal(before, atWriting);
                Assert.True(a.FixedByEdit);
                Assert.True(changes > before);
            }
            finally { ActionRunner.Writing -= probe; s.WatchWrites(false); }
        }

        [Fact]
        public void ReplacedSession_GetsNoWritingCall()
        {
            var s1 = Session();
            var a = Find(_topic, "utilize", "use");
            s1.Add(new[] { a });
            s1.WatchWrites(true);
            s1.WatchWrites(false);
            var s2 = Session();
            var c = Find(_topic, "utilize", "use", occurrence: 1);
            s2.Add(new[] { c });
            s2.WatchWrites(true);
            try
            {
                FirstRun(_topic, 0).Value = "Please use the tool. The user can see it.";
                s2.Select(c);
                Assert.Equal(AcceptOutcome.Applied, s2.Accept());
                Assert.False(a.IsFixed);
            }
            finally { s2.WatchWrites(false); }
        }

        // ---- Apply -------------------------------------------------------------------------------

        [Fact]
        public void Accept_ExactFix_WritesOneTrackedChange_RecordsIt_AndSelectsTheNext()
        {
            var s = Session();
            var first = Find(_topic, "utilize", "use");
            var second = Find(_topic, "utilize", "use", occurrence: 1);
            s.Add(new[] { first, second });
            s.Select(first);

            Assert.Equal(AcceptOutcome.Applied, s.Accept());

            Assert.Contains("Please use the tool.", TopicText());
            Assert.True(first.IsFixed);
            Assert.Single(_topic.TextOperations);
            Assert.Equal("accepted||Google.WordList", _feedback.Single());
            Assert.Same(second, s.Current);
            Assert.Empty(_host.Confirms);   // the topic itself: no question
        }

        [Fact]
        public void Accept_AlreadyApplied_OrNothingSelected_DoesNothing()
        {
            var s = Session();
            Assert.Equal(AcceptOutcome.None, s.Accept());
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });
            f.IsFixed = true;
            s.Select(f);
            Assert.Equal(AcceptOutcome.None, s.Accept());
            Assert.Empty(_topic.TextOperations);
        }

        [Fact]
        public void Accept_StaleText_IsNotApplied_AndSaysCheckAgain()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });
            ((XmlText)f.Block.FirstChild).Value = "Please employ the tool.";   // the author edited the sentence
            s.Sort();
            s.Current = f;

            Assert.True(f.IsStale);
            Assert.Equal(AcceptOutcome.Stale, s.Accept());
            Assert.Equal("The text changed. Lint the paragraph again.", s.Status);
            Assert.Empty(_topic.TextOperations);
        }

        [Fact]
        public void Accept_TextChangedSinceTheLastSort_WritesNothing_AndMarksItStale()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });
            s.Current = f;
            ((XmlText)f.Block.FirstChild).Value = "Please employ the tool.";

            Assert.Equal(AcceptOutcome.NotApplied, s.Accept());
            Assert.True(f.IsStale);
            Assert.Contains("No changes were made", s.Status);
            Assert.Empty(_feedback);
        }

        [Fact]
        public void Accept_Variable_SelectsTheText_AndSaysInsertTheVariable()
        {
            var s = Session();
            var f = Find(_topic, "the tool", null);
            f.Variable = "General.ProductName";
            s.Add(new[] { f });
            s.Current = f;

            Assert.Equal(AcceptOutcome.WentTo, s.Accept());
            Assert.Equal("the tool", _topic.LastSelected);
            Assert.Equal("The text is selected. On the Insert tab, click Variable and choose General.ProductName.", s.Status);
            Assert.Empty(_topic.TextOperations);
        }

        [Fact]
        public void Accept_ManualFix_SelectsTheText_AndSaysFixItInTheEditor()
        {
            var s = Session();
            var f = Find(_topic, "The user", null, "Structure.Heading");
            f.Source = "structure";
            s.Add(new[] { f });
            s.Current = f;

            Assert.Equal(AcceptOutcome.WentTo, s.Accept());
            Assert.Equal("The user", _topic.LastSelected);
            Assert.Equal("Change it in the topic, then lint the paragraph again.", s.Status);
        }

        [Fact]
        public void Accept_NeedsARewrite_OrAltText_HandsTheFlowToTheView()
        {
            var s = Session();
            var rewrite = Find(_topic, "The user can see it.", null);
            var alt = Find(_topic, "Then", null, "Structure.AltText");
            alt.Source = "structure";
            s.Add(new[] { rewrite, alt });

            s.Current = rewrite;
            Assert.Equal(AcceptOutcome.Rewrite, s.Accept());
            s.Current = alt;
            Assert.Equal(AcceptOutcome.AltText, s.Accept());
            Assert.Empty(_topic.TextOperations);
        }

        [Fact]
        public void Accept_InASnippet_AsksFirst_AndNoMeansNoChange()
        {
            var s = Session();
            var f = Find(_snippet, "utilize", "use", documentPath: _snippetPath);
            s.Add(new[] { f });
            s.Current = f;
            _host.ConfirmAnswer = false;

            Assert.Equal(AcceptOutcome.Declined, s.Accept());
            Assert.Equal("This changes shared.flsnp, used in 3 topics. Make the change?\n\nThe change is tracked.", _host.Confirms.Single());
            Assert.Empty(_snippet.TextOperations);
            Assert.False(f.IsFixed);
        }

        [Fact]
        public void Accept_InASnippet_Confirmed_ChangesTheSnippet_AndBringsTheTopicBack()
        {
            var s = Session();
            var f = Find(_snippet, "utilize", "use", documentPath: _snippetPath);
            s.Add(new[] { f });
            s.Current = f;

            Assert.Equal(AcceptOutcome.Applied, s.Accept());
            Assert.Equal("Always use care.", BlockSelector.FindBody(_snippet.Document).InnerText);
            Assert.Equal(new[] { _snippetPath, _topicPath }, _host.Opened);
            Assert.Contains("The snippet stays open in its own tab, unsaved.", s.Status);
        }

        // ---- Apply all fixes -----------------------------------------------------------------------

        [Fact]
        public void FixAll_InTheTopic_WritesWithoutAPreview_InOneUndoStep_AndPointsAtSnippetFixes()
        {
            var s = Session();
            var a = Find(_topic, "utilize", "use");
            var b = Find(_topic, "utilize", "use", occurrence: 1);
            var inSnippet = Find(_snippet, "utilize", "use", documentPath: _snippetPath);
            var reword = Find(_topic, "The user", "You");
            reword.Source = "ai";
            s.Add(new[] { a, b, inSnippet, reword });

            Assert.Equal(new[] { a, b }, s.FixAllCandidates());
            s.FixAll();

            Assert.Empty(_host.Previewed);
            Assert.Single(_topic.TextOperations);
            Assert.True(a.IsFixed && b.IsFixed);
            Assert.False(inSnippet.IsFixed);
            Assert.False(reword.IsFixed);
            Assert.Equal(2, _feedback.Count(x => x.StartsWith("accepted|")));
            Assert.EndsWith("  1 more are in snippets. Select one to change it there.", s.Status);
            Assert.Same(reword, s.Current);
        }

        [Fact]
        public void FixAll_ProjectReport_PreviewsFirst_AndCancelWritesNothing()
        {
            var s = Session(project: true);
            s.Add(new[] { Find(_other, "utilize", "use", documentPath: _otherPath) });
            _host.PreviewAnswer = false;

            s.FixAll();

            Assert.Single(_host.Previewed);
            Assert.Empty(_other.TextOperations);
            Assert.Empty(_feedback);
        }

        [Fact]
        public void FixAll_PreviewWithEveryRuleUnticked_WritesNothing()
        {
            var s = Session(project: true);
            s.Add(new[] { Find(_other, "utilize", "use", documentPath: _otherPath) });
            _host.OnPreview = plan => plan.Groups.ForEach(g => g.Selected = false);

            s.FixAll();

            Assert.Empty(_other.TextOperations);
            Assert.Null(_host.LastReport);
        }

        [Fact]
        public void FixAll_ProjectReport_SkipsUnwritableFiles_SavesFilesItOpened_LeavesTheTopicOpen()
        {
            var s = Session(project: true);
            s.WhyNotWritable = p => p == _snippetPath ? "read-only" : null;
            var inTopic = Find(_topic, "utilize", "use", documentPath: _topicPath);
            var inOther = Find(_other, "utilize", "use", documentPath: _otherPath);
            var readOnly = Find(_snippet, "utilize", "use", documentPath: _snippetPath);
            s.Add(new[] { inTopic, inOther, readOnly });
            _host.OpenedByPlugin.Add(_other);

            s.FixAll();

            Assert.Single(_host.Previewed);
            Assert.True(inTopic.IsFixed && inOther.IsFixed);
            Assert.False(readOnly.IsFixed);
            Assert.Equal(new[] { _other }, _host.Saved);
            var report = _host.LastReport;
            Assert.Equal(2, report.FixesApplied);
            Assert.True(report.Changed.Single(e => e.Path == _otherPath).Saved);
            Assert.False(report.Changed.Single(e => e.Path == _topicPath).Saved);
            Assert.Equal("read-only", report.Skipped.Single().Reason);
            Assert.DoesNotContain("snippet stays open", s.Status);
            Assert.Contains("Skipped 1 change", s.Status);
        }

        [Fact]
        public void FixAll_SaveFails_TheChangeStillCounts_ButIsNotSaved()
        {
            var s = Session(project: true);
            s.Add(new[] { Find(_other, "utilize", "use", documentPath: _otherPath) });
            _host.OpenedByPlugin.Add(_other);
            _host.SaveThrows = true;

            s.FixAll();

            Assert.False(_host.LastReport.Changed.Single().Saved);
            Assert.Equal(1, _host.LastReport.FixesApplied);
        }

        [Fact]
        public void FixAll_FileThatWontOpen_IsReportedAsFailed()
        {
            var s = Session(project: true);
            _host.Documents.Remove(_otherPath);
            var f = Find(_other, "utilize", "use", documentPath: _otherPath);
            s.Add(new[] { f });

            s.FixAll();

            Assert.Equal("could not open it in Flare", _host.LastReport.Failed.Single().Reason);
            Assert.False(f.IsFixed);
            Assert.Contains("Could not change other.htm (could not open it in Flare)", s.Status);
        }

        // ---- Write all as tracked changes ----------------------------------------------------------

        [Fact]
        public void WriteAll_AsksFirst_AndNoWritesNothing()
        {
            var s = Session();
            s.Add(new[] { Find(_topic, "utilize", "use") });
            _host.ConfirmAnswer = false;

            s.WriteAll();

            Assert.Equal("Change All Issues", _host.ConfirmActions.Single());
            Assert.Empty(_topic.TextOperations);
        }

        [Fact]
        public void WriteAll_WritesFixesAndRewords_LeavesRewritesListed()
        {
            var s = Session();
            var fix = Find(_topic, "utilize", "use");
            var reword = Find(_topic, "The user", "You");
            reword.Source = "ai";
            var rewrite = Find(_topic, "Then", null);
            var inSnippet = Find(_snippet, "utilize", "use", documentPath: _snippetPath);
            s.Add(new[] { fix, reword, rewrite, inSnippet });

            Assert.Equal(2, s.WriteAllCandidates().Count);
            s.WriteAll();

            Assert.True(fix.IsFixed && reword.IsFixed);
            Assert.False(rewrite.IsFixed);
            Assert.False(inSnippet.IsFixed);
            Assert.Single(_topic.TextOperations);
            Assert.Equal(2, _feedback.Count(x => x.StartsWith("proposed|bulk|")));
            Assert.Contains("Made 2 tracked changes", s.Status);
            Assert.StartsWith("Make 2 changes in the topic as tracked changes?", _host.Confirms.Single());
        }

        // ---- Reject and Skip --------------------------------------------------------------------

        [Fact]
        public void Reject_HidesItForThisText_RecordsTheReason_AndSelectsTheNext()
        {
            var s = Session();
            var a = Find(_topic, "utilize", "use");
            var b = Find(_topic, "utilize", "use", occurrence: 1);
            s.Add(new[] { a, b });
            s.Current = a;

            s.Reject("fine here");

            Assert.DoesNotContain(a, s.Findings);
            Assert.Same(b, s.Current);
            Assert.Equal("rejected|fine here|Google.WordList", _feedback.Single());
            Assert.EndsWith("Ignored. It stays hidden for this text.", s.Status);

            s.Add(new[] { Find(_topic, "utilize", "use") });   // the next check finds it again
            Assert.Single(s.Findings);
        }

        [Fact]
        public void Skip_SelectsTheNext_WithoutRecordingAnything_AndWraps()
        {
            var s = Session();
            var a = Find(_topic, "utilize", "use");
            var b = Find(_topic, "utilize", "use", occurrence: 1);
            s.Add(new[] { a, b });
            s.Current = b;

            s.Skip();

            Assert.Same(a, s.Current);
            Assert.Empty(_feedback);
            Assert.Equal(2, s.Findings.Count);
        }

        [Fact]
        public void Next_PassesOverAppliedAndStale_AndClearsWhenNothingIsOpen()
        {
            var s = Session();
            var a = Find(_topic, "utilize", "use");
            var b = Find(_topic, "The user", "You");
            var c = Find(_topic, "utilize", "use", occurrence: 1);
            s.Add(new[] { a, b, c });
            b.IsFixed = true;
            s.Current = a;
            s.Next();
            Assert.Same(c, s.Current);

            a.IsFixed = true; c.IsFixed = true;
            s.Next();
            Assert.Null(s.Current);
            Assert.Null(_host.LastSelectRequest);
        }

        // ---- Edit ---------------------------------------------------------------------------------

        [Fact]
        public void Edit_AppliesTheAuthorsText_AndRecordsItAsEdited()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });
            s.Current = f;
            _host.TextAnswer = "employ";

            s.Edit();

            Assert.Equal("Change \"utilize\" to:", _host.TextLabels.Single());
            Assert.Contains("Please employ the tool.", TopicText());
            Assert.True(f.IsFixed);
            Assert.Equal("edited|use|Google.WordList", _feedback.Single());
        }

        [Fact]
        public void Edit_CancelledOrUnchanged_DoesNothing()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });
            s.Current = f;

            _host.TextAnswer = null;
            s.Edit();
            _host.TextAnswer = "utilize";
            s.Edit();

            Assert.Empty(_topic.TextOperations);
            Assert.False(f.IsFixed);
        }

        [Fact]
        public void Edit_InASnippet_AsksFirst()
        {
            var s = Session();
            var f = Find(_snippet, "utilize", "use", documentPath: _snippetPath);
            s.Add(new[] { f });
            s.Current = f;
            _host.TextAnswer = "employ";
            _host.ConfirmAnswer = false;

            s.Edit();

            Assert.Single(_host.Confirms);
            Assert.Empty(_snippet.TextOperations);
        }

        // ---- staleness, Check this paragraph again, Refine, Rewrite, alt text ----------------------------

        [Fact]
        public void Sort_MarksSuggestionsWhoseTextIsGone_AsStale_AndLeavesThemOutOfTheSummary()
        {
            var s = Session();
            var a = Find(_topic, "utilize", "use");
            var b = Find(_topic, "utilize", "use", occurrence: 1);
            s.Add(new[] { a, b });
            Assert.Equal("2 issues in 2 sections. 2 Auto-fix.", s.Summary());

            ((XmlText)b.Block.FirstChild).Value = "Then open the menu.";
            s.Sort();

            Assert.True(b.IsStale);
            Assert.Equal(new[] { a }, s.Open());
            Assert.Equal("1 issue. 1 Auto-fix.", s.Summary());
        }

        [Fact]
        public void ReanalyzeTarget_SnippetOrMissingParagraph_SaysWhy()
        {
            var s = Session();
            Assert.Null(s.ReanalyzeTarget(Find(_snippet, "utilize", "use", documentPath: _snippetPath)));
            Assert.Equal("Lint Paragraph works on the topic's paragraphs.", s.Status);

            var gone = Find(_topic, "Then", null);
            gone.Block.ParentNode.RemoveChild(gone.Block);
            Assert.Null(s.ReanalyzeTarget(gone));
            Assert.Equal("That paragraph is gone. Click Lint Again to lint the topic.", s.Status);
        }

        [Fact]
        public void Reanalyzed_ReplacesTheParagraphsSuggestions_AndKeepsTheRest()
        {
            var s = Session();
            var old = Find(_topic, "utilize", "use");
            var elsewhere = Find(_topic, "utilize", "use", occurrence: 1);
            var applied = Find(_topic, "The user", "You");
            s.Add(new[] { old, elsewhere, applied });
            applied.IsFixed = true;
            var block = s.ReanalyzeTarget(old);
            var fresh = Find(_topic, "the tool", "it");

            s.Reanalyzed(old, block, new List<StyleFinding> { fresh });

            Assert.DoesNotContain(old, s.Findings);
            Assert.Contains(elsewhere, s.Findings);
            Assert.Contains(applied, s.Findings);
            Assert.Same(fresh, s.Current);
            Assert.EndsWith("Paragraph linted again. 1 issue.", s.Status);
        }

        [Fact]
        public void Refined_KeepIt_RejectsAsFineHere()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });

            s.Refined(f, new StyleFinding { Replacement = "utilize", Suggestion = "keep it" }, "keep it");

            Assert.Empty(s.Findings);
            Assert.Equal(new[] { "refined|keep it|Google.WordList", "rejected|fine here (refined: keep it)|Google.WordList" }, _feedback);
        }

        [Fact]
        public void Refined_NewText_ReplacesTheSuggestion_AndSelectsIt()
        {
            var s = Session();
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });
            var refined = Find(_topic, "utilize", "make use of");

            s.Refined(f, refined, "longer");

            Assert.Same(refined, s.Findings.Single());
            Assert.Same(refined, s.Current);
            Assert.EndsWith("The AI rewrote the suggestion. Change it, edit it, or rewrite it again.", s.Status);
        }

        [Fact]
        public void Rewrite_TakesEveryOpenSuggestionInTheParagraph_AndMarksThemDone()
        {
            var s = Session();
            var a = Find(_topic, "utilize", "use");
            var b = Find(_topic, "The user", null);
            var elsewhere = Find(_topic, "utilize", "use", occurrence: 1);
            s.Add(new[] { a, b, elsewhere });
            List<StyleFinding> related;

            Assert.NotNull(s.RewriteTarget(b, out related));
            Assert.Equal(new[] { a, b }, related.OrderBy(x => x.Order));
            s.Rewritten(related, "Rewrote 1 paragraph.");

            Assert.True(a.IsFixed && b.IsFixed);
            Assert.False(elsewhere.IsFixed);
            Assert.Equal(2, _feedback.Count(x => x.StartsWith("accepted|rewrite|")));
            Assert.EndsWith("  Rewrote 1 paragraph.", s.Status);
            Assert.Same(elsewhere, s.Current);

            Assert.Null(s.RewriteTarget(Find(_snippet, "utilize", null, documentPath: _snippetPath), out related));
            Assert.Equal("Open the snippet and use Rewrite there.", s.Status);
        }

        [Fact]
        public void AltTextApplied_RecordsAcceptedOrEdited()
        {
            var s = Session();
            var a = Find(_topic, "Then", null, "Structure.AltText");
            var b = Find(_topic, "Please", null, "Structure.AltText");
            s.Add(new[] { a, b });

            s.AltTextApplied(a, "A chart", "A chart");
            s.AltTextApplied(b, "A chart", "Sales by month");

            Assert.True(a.IsFixed && b.IsFixed);
            Assert.Equal(new[] { "accepted|A chart|Structure.AltText", "edited|Sales by month|Structure.AltText" }, _feedback);
            Assert.EndsWith("Alt text added as a tracked change.", s.Status);
        }

        // ---- Turn off this rule ----------------------------------------------------------------------

        private Team MakeTeam(bool owner, bool personal = false, string feedbackFolder = null)
        {
            var guide = Path.Combine(_root, "guide");
            Directory.CreateDirectory(Path.Combine(guide, "FlareAI"));
            File.WriteAllText(Path.Combine(guide, @"FlareAI\owners.txt"), "owner@example.com\n");
            var t = FlareAI.Core.Actions.Team.Load(guide, feedbackFolder, owner ? "owner@example.com" : "pat@example.com");
            if (personal) t.SeedFrom = Path.Combine(_root, "seed");
            return t;
        }

        [Fact]
        public void TurnOffRule_NothingSelected_SaysSelectOne()
        {
            var s = Session();
            s.TurnOffRule();
            Assert.Equal("Select an issue whose rule you want to change.", s.Status);
            Assert.Null(_host.RuleQuestion);
        }

        [Fact]
        public void TurnOffRule_Writer_SendsARequest_AndTheFindingStaysMarked()
        {
            _team = MakeTeam(owner: false);
            var sent = new List<GuideRequest>();
            var s = Session();
            s.SendRequest = (t, req) => { sent.Add(req); return null; };
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { f });
            s.Current = f;
            _host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = " We say utilize. " };

            s.TurnOffRule();

            Assert.Null(_host.RuleQuestion);   // nothing is written: no "turn off" dialog
            Assert.StartsWith("This goes to the style guide owners (owner@example.com).", _host.Request.Notice);
            Assert.Contains("owners.txt doesn't list you", _host.Request.Notice);   // the folder is more open than the owners list
            Assert.Null(_host.Request.Wording);   // a Google rule: owners change its level, not its words
            var r = sent.Single();
            Assert.Equal(RequestKinds.TurnOff, r.Kind);
            Assert.Equal("We say utilize.", r.Reason);
            Assert.Equal("google:words/2", r.RuleId);
            Assert.Equal("request|We say utilize.|Google.WordList", _feedback.Single());
            Assert.Contains(f, s.Findings);
            Assert.True(f.RequestSent);
            Assert.Contains("Request sent to the style guide owners (owner@example.com).", s.Status);
            Assert.Empty(_guideWrites);
        }

        [Fact]
        public void TurnOffRule_Writer_RequestNotDelivered_SaysItIsSavedAndSentLater()
        {
            _team = MakeTeam(owner: false, feedbackFolder: Path.Combine(_root, "fb"));
            var s = Session();
            s.SendRequest = (t, r) => @"House Style couldn't write to \\server\feedback\requests";
            var x = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { x });
            s.Current = x;
            _host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "why" };

            s.TurnOffRule();

            Assert.StartsWith("Your request is saved but not sent yet (House Style couldn't write to", _host.Errors.Single());
            Assert.EndsWith("Request saved. It's sent the next time you lint.", s.Status);
        }

        [Fact]
        public void TurnOffRule_Writer_Cancelled_DoesNothing()
        {
            _team = MakeTeam(owner: false);
            var s = Session();
            s.SendRequest = (t, r) => { throw new InvalidOperationException("not sent"); };
            var f = Find(_topic, "utilize", "use");
            s.Add(new[] { f });
            s.Current = f;
            _host.RequestChoice = null;

            s.TurnOffRule();

            Assert.Empty(_feedback);
            Assert.False(f.RequestSent);
        }

        [Fact]
        public void TurnOffRule_Writer_NoReason_SendsNothing()
        {
            _team = MakeTeam(owner: false);
            var s = Session();
            s.SendRequest = (t, r) => { throw new InvalidOperationException("not sent"); };
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { f });
            s.Current = f;
            _host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "  " };

            s.TurnOffRule();

            Assert.EndsWith("Not sent. Add a reason for the owners.", s.Status);
            Assert.Empty(_feedback);
        }

        [Fact]
        public void TurnOffRule_Writer_ForThisProject_SendsAProjectException()
        {
            _team = MakeTeam(owner: false);
            var sent = new List<GuideRequest>();
            var s = Session();
            s.SendRequest = (t, req) => { sent.Add(req); return null; };
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { f });
            s.Current = f;
            _host.RequestChoice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Legal wording." };

            s.TurnOffRule();

            var r = sent.Single();
            Assert.Equal(RequestKinds.ProjectException, r.Kind);
            Assert.Equal(_topicPath, r.ProjectPath);
            Assert.Equal(Path.GetFileName(_root), r.Project);
            Assert.Empty(_projectWrites);   // written only once an owner approves
        }

        [Fact]
        public void TurnOffRule_NoOwnersFile_ChangesDirectly_AsBefore()
        {
            var guide = Path.Combine(_root, "open-guide");
            Directory.CreateDirectory(guide);
            _team = FlareAI.Core.Actions.Team.Load(guide, null, "pat@example.com");
            var s = Session();
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { f });
            s.Current = f;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "We say it." };

            s.TurnOffRule();

            Assert.Null(_host.Request);
            Assert.Equal(new[] { "google:words/2=off" }, _guideWrites);
        }

        [Fact]
        public void TurnOffRule_Owner_WriteFails_OffersARequest_KeepingTheReason()
        {
            _team = MakeTeam(owner: true);
            var sent = new List<GuideRequest>();
            var s = Session();
            s.SetGuideRule = (t, id, setting, reason) => { throw new GuideWriteException(Path.Combine(_root, "guide.htm"), new UnauthorizedAccessException("Access denied.")); };
            s.SendRequest = (t, req) => { sent.Add(req); return null; };
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { f });
            s.Current = f;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "We say it." };
            _host.OnRequest = q => new RuleSettingChoice { ForTeam = true, Setting = q.Setting, Reason = q.Reason };

            s.TurnOffRule();

            Assert.Contains("as a request instead? Your reason is kept.", _host.Confirms.Single());
            Assert.Equal("We say it.", _host.Request.Reason);
            Assert.Equal("We say it.", sent.Single().Reason);
            Assert.Empty(_host.Errors);
        }

        [Fact]
        public void TurnOffRule_Owner_ForTheTeam_WritesTheGuide_AndRemovesEveryFindingOfTheRule()
        {
            _team = MakeTeam(owner: true);
            var s = Session();
            var a = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            var b = Find(_topic, "utilize", "use", occurrence: 1, ruleId: "google:words/2");
            var other = Find(_topic, "The user", "You", "Google.Person", ruleId: "company:terminology/1");
            s.Add(new[] { a, b, other });
            s.Current = a;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "We say it." };

            s.TurnOffRule();

            Assert.Equal("Google · Word list: Use use, not utilize.", _host.RuleQuestion);
            Assert.True(_host.RuleCanTeam);
            Assert.Equal(new[] { "google:words/2=off" }, _guideWrites);
            Assert.Equal(new[] { other }, s.Findings);
            Assert.Equal("rule ignored|We say it.|Google.WordList", _feedback.Single());
            Assert.EndsWith("Rule disabled for the team.", s.Status);   // a shared folder: nothing to commit
        }

        [Fact]
        public void TurnOffRule_PersonalGuide_SaysYourStyleGuide()
        {
            _team = MakeTeam(owner: true, personal: true);
            var s = Session();
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { f });
            s.Current = f;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off" };

            s.TurnOffRule();

            Assert.True(_host.RulePersonal);
            Assert.EndsWith("Rule disabled.", s.Status);
        }

        [Fact]
        public void TurnOffRule_ForThisProject_WritesTheProjectOverrides_WithTheWriter()
        {
            _team = MakeTeam(owner: true);
            var s = Session();
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { f });
            s.Current = f;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = false, Setting = "off" };

            s.TurnOffRule();

            Assert.Equal(new[] { "google:words/2=off by owner@example.com" }, _projectWrites);
            Assert.Empty(_guideWrites);
            Assert.EndsWith("Rule disabled for this project.", s.Status);
        }

        [Fact]
        public void TurnOffRule_NoGuideRule_OnlyTheProjectIsOffered()
        {
            var s = Session();   // no team
            var f = Find(_topic, "utilize", "use", "Vale.Spelling");
            f.Layer = "company";
            s.Add(new[] { f });
            s.Current = f;
            _host.RuleChoice = null;

            s.TurnOffRule();

            Assert.Equal("\"Vale.Spelling\" (Our rules)", _host.RuleQuestion);
            Assert.False(_host.RuleCanTeam);
            Assert.Null(_host.RuleOthers);
            Assert.Contains(f, s.Findings);   // cancelled
        }

        [Fact]
        public void TurnOffRule_AlsoOtherRulesOnTheSameWords_OffersThemByName_AndTurnsThemOff()
        {
            _team = MakeTeam(owner: true);
            var s = Session();
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            f.AlsoRules.Add(new RuleRef { Id = "company:terminology/1", Name = "Company.Terms", Layer = "company" });
            f.AlsoRules.Add(new RuleRef { Id = "google:unknown/9", Name = "Google.Unknown", Layer = "google" });
            var twin = Find(_topic, "utilize", "use", occurrence: 1, ruleId: "company:terminology/1");
            var keep = Find(_topic, "The user", "You", ruleId: "google:other/1");
            s.Add(new[] { f, twin, keep });
            s.Current = f;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", AlsoOthers = true };

            s.TurnOffRule();

            Assert.Equal(new[] { "Our rules · Audience: Say administrators.", "Google.Unknown" }, _host.RuleOthers.Select(r => r.Name));
            Assert.Equal(new[] { "google:words/2=off", "company:terminology/1=off", "google:unknown/9=off" }, _guideWrites);
            Assert.Equal(new[] { keep }, s.Findings);
            Assert.Contains("Rules disabled for the team", s.Status);
        }

        [Fact]
        public void TurnOffRule_OtherRulesNotTicked_OnlyThisOneChanges()
        {
            _team = MakeTeam(owner: true);
            var s = Session();
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            f.AlsoRules.Add(new RuleRef { Id = "company:terminology/1", Name = "Company.Terms", Layer = "company" });
            var twin = Find(_topic, "utilize", "use", occurrence: 1, ruleId: "company:terminology/1");
            s.Add(new[] { f, twin });
            s.Current = f;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", AlsoOthers = false };

            s.TurnOffRule();

            Assert.Equal(new[] { "google:words/2=off" }, _guideWrites);
            Assert.Equal(new[] { twin }, s.Findings);
        }

        [Fact]
        public void TurnOffRule_WriteFails_NamesTheTarget_AndChangesNothing()
        {
            _team = MakeTeam(owner: true);
            var s = Session();
            s.SetProjectRule = (path, x, setting, reason, by) => { throw new GuideWriteException(ProjectRules.OverridesPath(path), new UnauthorizedAccessException("Access denied.")); };
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { f });
            s.Current = f;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = false, Setting = "off" };

            s.TurnOffRule();

            Assert.Equal("House Style couldn't change the rule in " + ProjectRules.OverridesPath(_topicPath) + ".", _host.Errors.Single());
            Assert.StartsWith("House Style couldn't write to", _host.ErrorDetails.Single());
            Assert.EndsWith("Nothing was changed. The rule still applies.", _host.ErrorDetails.Single());
            Assert.EndsWith("The rule was not changed.", s.Status);
            Assert.Contains(f, s.Findings);
            Assert.Empty(_feedback);
        }

        [Fact]
        public void TurnOffRule_AnOtherRuleFails_KeepsTheFirst_AndSaysHowManyWereNotChanged()
        {
            _team = MakeTeam(owner: true);
            var s = Session();
            s.SetGuideRule = (team, id, setting, reason) =>
            {
                if (id != "google:words/2") throw new IOException("locked");
                _guideWrites.Add(id); return "guide.htm";
            };
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            f.AlsoRules.Add(new RuleRef { Id = "company:terminology/1", Name = "Company.Terms", Layer = "company" });
            f.AlsoRules.Add(new RuleRef { Id = "google:unknown/9", Name = "Google.Unknown", Layer = "google" });
            s.Add(new[] { f });
            s.Current = f;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", AlsoOthers = true };

            s.TurnOffRule();

            Assert.Equal(new[] { "google:words/2" }, _guideWrites);
            Assert.Equal("House Style couldn't change \"Our rules · Audience: Say administrators.\" in guide.htm.", _host.Errors.Single());
            Assert.Empty(s.Findings);
            Assert.EndsWith("Rule disabled for the team. 2 could not be changed.", s.Status);
        }

        [Fact]
        public void TurnOffRule_ChangedLevel_KeepsTheFindings()
        {
            _team = MakeTeam(owner: true);
            var s = Session();
            var f = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            s.Add(new[] { f });
            s.Current = f;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "suggestion" };

            s.TurnOffRule();

            Assert.Contains(f, s.Findings);
            Assert.StartsWith("rule changed|", _feedback.Single());
            Assert.Contains("Rule set to Suggestion for the team", s.Status);
        }

        [Fact]
        public void TurnOffRule_UncitedAiJudgment_IsIgnoredForTheRestOfTheSession()
        {
            var s = Session();
            var ai = Find(_topic, "utilize", "use", "Wordiness");
            ai.Source = "ai"; ai.Layer = "ai";
            s.Add(new[] { ai });
            s.Current = ai;
            _host.RuleChoice = new RuleSettingChoice { ForTeam = false, Setting = "off" };

            s.TurnOffRule();
            Assert.Equal("\"Wordiness\" (AI judgment)", _host.RuleQuestion);
            Assert.Equal(new[] { "Wordiness=off by " }, _projectWrites);
            Assert.Empty(s.Findings);

            var again = Find(_topic, "utilize", "use", "Wordiness", occurrence: 1);
            again.Source = "ai"; again.Layer = "ai";
            s.Add(new[] { again });
            Assert.Empty(s.Findings);
        }

        // ---- Go to, list, summary -------------------------------------------------------------------

        [Fact]
        public void OnSelected_SelectsTopicTextQuietly_ButDoesNotOpenSnippets()
        {
            var s = Session();
            var inTopic = Find(_topic, "the tool", "it");
            var inSnippet = Find(_snippet, "utilize", "use", documentPath: _snippetPath);
            s.Add(new[] { inTopic, inSnippet });

            s.OnSelected(inSnippet);
            Assert.Empty(_host.Opened);
            Assert.Null(_snippet.LastSelected);

            s.OnSelected(inTopic);
            Assert.Equal("the tool", _topic.LastSelected);
            Assert.Null(s.Status);
        }

        [Fact]
        public void GoTo_Snippet_OpensItAndSelectsTheText()
        {
            var s = Session();
            var f = Find(_snippet, "utilize", "use", documentPath: _snippetPath);
            s.Add(new[] { f });
            s.Current = f;

            s.GoToCurrent();

            Assert.Equal(new[] { _snippetPath }, _host.Opened);
            Assert.Equal("utilize", _snippet.LastSelected);
        }

        [Fact]
        public void GoTo_CantOpen_OrTextGone_SaysSo_AndAppliedDoesNothing()
        {
            var s = Session();
            _host.Documents.Remove(_snippetPath);
            var f = Find(_snippet, "utilize", "use", documentPath: _snippetPath);
            s.GoTo(f, true);
            Assert.Equal("Could not open " + _snippetPath, s.Status);

            var gone = Find(_topic, "utilize", "use");
            ((XmlText)gone.Block.FirstChild).Value = "Nothing here.";
            s.GoTo(gone, true);
            Assert.Equal("That text is no longer in the topic. Lint the paragraph again.", s.Status);

            var done = Find(_topic, "the menu", "it");
            done.IsFixed = true;
            s.GoTo(done, true);
            Assert.Null(_topic.LastSelected);
        }

        [Fact]
        public void Add_LeavesOutRejectedAndIgnoredAiRules_AndLabelsByGuideSection()
        {
            var s = Session();
            s.IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "passive voice" };
            var cited = Find(_topic, "utilize", "use", ruleId: "google:words/2");
            var ignored = Find(_topic, "The user", "You", "Passive Voice");
            ignored.Layer = "ai";

            s.Add(new[] { cited, ignored });

            Assert.Equal(new[] { cited }, s.Findings);
            Assert.Equal("Word list", cited.RuleSection);
            Assert.Equal("Use use, not utilize.", cited.RuleText);
            Assert.Equal("Start", cited.Section);
        }

        [Fact]
        public void RefreshLint_ReplacesStyleRuleFindings_KeepsAiAndApplied_AndIsOffInAProjectReport()
        {
            var s = Session();
            var lint = Find(_topic, "utilize", "use");
            var applied = Find(_topic, "utilize", "use", occurrence: 1);
            var ai = Find(_topic, "The user", "You");
            ai.Source = "ai";
            s.Add(new[] { lint, applied, ai });
            applied.IsFixed = true;
            var fresh = Find(_topic, "the tool", "it");

            s.RefreshLint(new List<StyleFinding> { fresh });

            Assert.Equal(new[] { applied, ai, fresh }.OrderBy(x => x.Order), s.Findings);

            var project = Session(project: true);
            var x1 = Find(_topic, "utilize", "use", documentPath: _topicPath);
            project.Add(new[] { x1 });
            project.RefreshLint(new List<StyleFinding>());
            Assert.Equal(new[] { x1 }, project.Findings);
        }

        [Fact]
        public void Summary_CountsFixesRewordsAndNeedsYou_BySection()
        {
            var s = Session();
            Assert.Equal("No issues found in this topic.", s.Summary());
            var fix = Find(_topic, "utilize", "use");
            var reword = Find(_topic, "The user", "You");
            reword.Source = "ai";
            var variable = Find(_topic, "Then", null);
            variable.Variable = "General.ProductName";
            s.Add(new[] { fix, reword, variable });

            Assert.Equal("3 issues in 2 sections. 1 Auto-fix, 2 Needs you.", s.Summary());
        }

        [Fact]
        public void ProjectReport_SortsByFile_AndApplyAllReachesEveryFile()
        {
            var s = Session(project: true);
            var b = Find(_topic, "utilize", "use", documentPath: _topicPath);
            var a = Find(_other, "utilize", "use", documentPath: _otherPath);
            s.Add(new[] { b, a });

            Assert.Equal(new[] { a, b }, s.Findings);
            Assert.Equal(2, s.FixAllCandidates().Count);
        }

        /// <summary>Answers the session's questions and records what it asked.</summary>
        private sealed class FakeHost : IReviewHost
        {
            public readonly Dictionary<string, IDocumentAdapter> Documents = new Dictionary<string, IDocumentAdapter>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Opened = new List<string>();
            public readonly List<string> Confirms = new List<string>();
            public readonly List<string> ConfirmActions = new List<string>();
            public readonly List<string> TextLabels = new List<string>();
            public readonly List<BatchPlan> Previewed = new List<BatchPlan>();
            public readonly List<IDocumentAdapter> OpenedByPlugin = new List<IDocumentAdapter>();
            public readonly List<IDocumentAdapter> Saved = new List<IDocumentAdapter>();
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> ErrorDetails = new List<string>();
            public bool ConfirmAnswer = true, PreviewAnswer = true, SaveThrows;
            public string TextAnswer;
            public Action<BatchPlan> OnPreview;
            public RuleSettingChoice RuleChoice;
            public RuleSettingChoice RequestChoice;
            public Func<RequestQuestion, RuleSettingChoice> OnRequest;
            public RequestQuestion Request;
            public string RuleQuestion;
            public bool RuleCanTeam, RulePersonal;
            public IList<RuleRef> RuleOthers;
            public BatchResult LastReport;
            public StyleFinding LastSelectRequest;

            public bool Confirm(string message, string action) { Confirms.Add(message); ConfirmActions.Add(action); return ConfirmAnswer; }

            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules)
            {
                RuleQuestion = ruleText; RuleCanTeam = canChangeTeamGuide; RulePersonal = personalGuide; RuleOthers = otherRules;
                return RuleChoice;
            }

            public RuleSettingChoice ChooseRequest(RequestQuestion question) { Request = question; return OnRequest != null ? OnRequest(question) : RequestChoice; }

            public string AskText(string title, string label, string initial, bool multiline) { TextLabels.Add(label); return TextAnswer; }

            public IDocumentAdapter OpenDocument(string path)
            {
                Opened.Add(path);
                IDocumentAdapter d;
                return Documents.TryGetValue(path, out d) ? d : null;
            }

            public bool PreviewBatch(BatchPlan plan) { Previewed.Add(plan); if (OnPreview != null) OnPreview(plan); return PreviewAnswer; }

            public bool SaveAndClose(IDocumentAdapter doc)
            {
                if (!OpenedByPlugin.Contains(doc)) return false;
                if (SaveThrows) throw new IOException("disk full");
                Saved.Add(doc);
                return true;
            }

            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { Errors.Add(whatHappened); ErrorDetails.Add(whatToDo); }

            public void Attach(ReviewSession s)
            {
                s.BatchReported += r => LastReport = r;
                s.SelectRequested += f => LastSelectRequest = f;
            }
        }
    }
}
