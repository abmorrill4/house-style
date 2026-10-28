using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FlareAI.Core;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    [Collection("Logging")] // the log tests set the static Diagnostics.Sink
    public class ExemptionsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-exm-" + Guid.NewGuid().ToString("N"));
        private static readonly DateTime Day = new DateTime(2026, 10, 28);

        public ExemptionsTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Sub"));
            File.WriteAllText(Path.Combine(_root, "Demo.flprj"), "<CatapultProject />");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private string Topic(string rel) { return Path.Combine(_root, @"Content\" + rel); }

        private static StyleFinding Finding(string rule, string excerpt, string before, string documentPath = null, string ruleId = null)
        {
            return new StyleFinding { Rule = rule, RuleId = ruleId, Excerpt = excerpt, Before = before, DocumentPath = documentPath };
        }

        private void WriteFile(params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Exemptions.FileFor(_root)));
            File.WriteAllLines(Exemptions.FileFor(_root), lines);
        }

        private static List<string> CaptureLog(Action act)
        {
            var lines = new List<string>();
            var old = Diagnostics.Sink;
            Diagnostics.Sink = m => lines.Add(m);
            try { act(); }
            finally { Diagnostics.Sink = old; }
            return lines.Where(l => l.StartsWith("Exemptions.txt", StringComparison.Ordinal)).ToList();
        }

        [Fact]
        public void Covers_MatchesTopicRuleTextAndBeforeExactly()
        {
            var topic = Topic(@"Sub\T.htm");
            var f = Finding("google.Will", "will", "It ", ruleId: "company:terminology/3");
            Exemptions.Add(_root, topic, f, "Brand", "ana", Day);
            var set = Exemptions.Load(_root);
            Assert.True(Exemptions.Covers(set, Exemptions.TopicFor(topic), Exemptions.RuleKey(f), "will", "It "));
        }

        [Fact]
        public void Covers_DoesNotMatchOtherRuleOtherTextOrOtherTopic()
        {
            var topic = Topic(@"Sub\T.htm");
            Exemptions.Add(_root, topic, Finding("google.Will", "will", "It "), "Brand", "ana", Day);
            var set = Exemptions.Load(_root);
            var t = Exemptions.TopicFor(topic);
            Assert.False(Exemptions.Covers(set, t, "google.Other", "will", "It "));
            Assert.False(Exemptions.Covers(set, t, "google.Will", "won't", "It "));
            Assert.False(Exemptions.Covers(set, Exemptions.TopicFor(Topic(@"Sub\Other.htm")), "google.Will", "will", "It "));
        }

        [Fact]
        public void Covers_SameWordTwiceInOneTopic_HidesOnlyTheExemptedCopy()
        {
            var topic = Topic(@"Sub\T.htm");
            var first = Finding("google.Will", "will", "It ");
            var second = Finding("google.Will", "will", "They ");
            Exemptions.Add(_root, topic, first, "Brand", "ana", Day);
            var set = Exemptions.Load(_root);
            var t = Exemptions.TopicFor(topic);
            Assert.True(Exemptions.Covers(set, t, Exemptions.RuleKey(first), first.Excerpt, first.Before));
            Assert.False(Exemptions.Covers(set, t, Exemptions.RuleKey(second), second.Excerpt, second.Before));
        }

        [Fact]
        public void Covers_TopicPathIgnoresCase_ButTextAndBeforeDoNot()
        {
            var topic = Topic(@"Sub\T.htm");
            Exemptions.Add(_root, topic, Finding("google.Will", "will", "It "), "Brand", "ana", Day);
            var set = Exemptions.Load(_root);
            Assert.True(Exemptions.Covers(set, @"SUB\t.HTM", "google.Will", "will", "It "));
            Assert.False(Exemptions.Covers(set, @"Sub\T.htm", "google.Will", "WILL", "It "));
            Assert.False(Exemptions.Covers(set, @"Sub\T.htm", "google.Will", "will", "it "));
        }

        [Fact]
        public void Add_UsesTheSamePathForLintTopicSourcePathAndLintProjectDocumentPath()
        {
            var topic = Topic(@"Sub\T.htm");
            // Lint Topic: the checked topic is the document.
            Exemptions.Add(_root, topic, Finding("google.Will", "will", "It "), "Brand", "ana", Day);
            // Source path: the same file, written with forward slashes.
            Exemptions.Add(_root, topic.Replace('\\', '/'), Finding("google.Will", "will", "It "), "Brand", "ana", Day);
            // Lint Project: the finding names its own document; the checked path is something else.
            Exemptions.Add(_root, Topic(@"Sub\Other.htm"), Finding("google.Will", "will", "It ", documentPath: topic), "Brand", "ana", Day);
            var set = Exemptions.Load(_root);
            Assert.Equal(1, set.Count);
            Assert.Equal(@"Sub\T.htm", set.Entries[0].Topic);
        }

        [Fact]
        public void RoundTrip_KeepsReasonWhoAndDate_AndStripsTabsAndLineBreaks()
        {
            Exemptions.Add(_root, Topic(@"Sub\T.htm"), Finding("google.Will", "will", "It "), "Brand\tname,\r\nkeep", "ana\tm", Day);
            var e = Exemptions.Load(_root).Entries.Single();
            Assert.Equal("Brand name,  keep", e.Reason);
            Assert.Equal("ana m", e.Who);
            Assert.Equal("2026-10-28", e.When);
            Assert.Equal("It ", e.Before);
            Assert.Equal("will", e.Text);
        }

        [Fact]
        public void Load_SkipsUnsafeLines_ParentPathsUncDriveRootedDeviceStreamAndLongKeys()
        {
            const string tail = "\tr\tw\t2026-10-28";
            WriteFile(
                "# Exemptions for the review",
                "Sub\\Good.htm\tgoogle.Will\twill\tIt " + tail,
                "..\\Up.htm\tgoogle.Will\twill\t" + tail,                              // parent path
                "\\\\server\\share\\A.htm\tgoogle.Will\twill\t" + tail,                // UNC
                "C:\\A.htm\tgoogle.Will\twill\t" + tail,                               // drive rooted
                "Sub\\A.htm:stream\tgoogle.Will\twill\t" + tail,                       // stream
                "\\\\?\\C:\\A.htm\tgoogle.Will\twill\t" + tail,                        // device path
                "Sub\\NUL.htm\tgoogle.Will\twill\t" + tail,                            // device name
                "Sub\\A.htm\t" + new string('r', 201) + "\twill\t" + tail,              // rule key over 200
                "Sub\\A.htm\tgoogle.Will\twill\tonly four fields",                    // wrong field count
                "Sub\\A.txt\tgoogle.Will\twill\t" + tail);                             // not a topic type
            var set = Exemptions.Load(_root);
            Assert.Equal(1, set.Count);
            Assert.Equal(@"Sub\Good.htm", set.Entries[0].Topic);
        }

        [Fact]
        public void Load_LogsOnlyTheCountAndLineNumbers()
        {
            WriteFile(
                "Sub\\SECRETTOPIC.txt\tSECRETRULE\tSECRETTEXT\tSECRET\tr\tw\t2026-10-28",
                "C:\\SECRET.htm\tSECRETRULE\tSECRETTEXT\tSECRET\tr\tw\t2026-10-28");
            var logged = CaptureLog(() => Exemptions.Load(_root));
            Assert.Single(logged);
            Assert.True(Regex.IsMatch(logged[0], @"^Exemptions\.txt: \d+ lines skipped \(lines \d+(, \d+)*\)$"), logged[0]);
            Assert.Equal("Exemptions.txt: 2 lines skipped (lines 1, 2)", logged[0]);
        }

        [Fact]
        public void Load_SkipsAFileOver1MB_AndFieldsOver2000()
        {
            const string tail = "\tr\tw\t2026-10-28";
            WriteFile(
                "Sub\\Good.htm\tgoogle.Will\twill\tIt " + tail,
                "Sub\\A.htm\tgoogle.Will\t" + new string('x', 2001) + "\t" + tail,      // text over 2,000: skipped
                "Sub\\B.htm\tgoogle.Will\t" + new string('y', 2000) + "\t" + tail,      // text at 2,000: kept
                "Sub\\C.htm\t" + new string('r', 201) + "\tx\t" + tail,                 // rule over 200: skipped
                "Sub\\D.htm\t" + new string('r', 200) + "\tx\t" + tail);                // rule at 200: kept
            var logged = CaptureLog(() => { Assert.Equal(3, Exemptions.Load(_root).Count); });
            Assert.Equal(new[] { "Exemptions.txt: 2 lines skipped (lines 2, 4)" }, logged);

            WriteFile(new[] { "Sub\\Good.htm\tgoogle.Will\twill\tSECRET" + tail }
                .Concat(Enumerable.Repeat("# " + new string('z', 1000), 1100)).ToArray());
            ExemptionSet big = null;
            var overLimit = CaptureLog(() => { big = Exemptions.Load(_root); });
            Assert.Equal(0, big.Count);
            Assert.Equal(new[] { "Exemptions.txt: over 1 MB, not loaded" }, overLimit);
        }

        [Fact]
        public void Remove_DropsOnlyThatLine()
        {
            var topic = Topic(@"Sub\T.htm");
            Exemptions.Add(_root, topic, Finding("google.Will", "will", "It "), "Brand", "ana", Day);
            Exemptions.Add(_root, topic, Finding("google.Will", "will", "They "), "Brand", "ana", Day);
            Exemptions.Add(_root, Topic(@"Sub\Other.htm"), Finding("google.Will", "will", "It "), "Brand", "ana", Day);

            var second = Exemptions.Load(_root).Entries.First(e => e.Before == "They ");
            Exemptions.Remove(_root, second);

            var set = Exemptions.Load(_root);
            Assert.Equal(2, set.Count);
            Assert.DoesNotContain(set.Entries, e => e.Before == "They ");
            Assert.Contains("# Text exempted from a rule. One per line, tab separated.", File.ReadAllLines(Exemptions.FileFor(_root)));
        }

        private static ExemptionEntry Entry(string topic, string text = "will", string before = "It ")
        {
            return new ExemptionEntry { Topic = topic, Rule = "google.Will", Text = text, Before = before, Reason = "r", Who = "w", When = "2026-10-28" };
        }

        [Fact]
        public void IsStale_WhenTheTopicNoLongerHoldsTheText()
        {
            File.WriteAllText(Topic("a.htm"), "<html><body><p>It <b>will</b> work.</p></body></html>");
            var s = Exemptions.Stale(_root, new[] { Entry("a.htm"), Entry("A.HTM", "won't"), Entry("a.htm", "will", "They ") });
            Assert.Equal(new[] { StaleState.Ok, StaleState.TextNotFound, StaleState.TextNotFound }, s.ToArray());
        }

        [Fact]
        public void IsStale_ShowsTopicNotFound_ForAMissingMovedOrUnsafeTopic()
        {
            File.WriteAllText(Topic("a.htm"), "<p>It will</p>");
            var s = Exemptions.Stale(_root, new[] { Entry("gone.htm"), Entry(@"Sub\a.htm"), Entry("..\\a.htm"), Entry("a.txt") });
            Assert.All(s, x => Assert.Equal(StaleState.TopicNotFound, x));
        }

        [Fact]
        public void IsStale_NeverOpensARootedDeviceOrAlternateStreamPath()
        {
            File.WriteAllText(Topic("a.htm"), "<p>It will</p>");
            var s = Exemptions.Stale(_root, new[]
            {
                Entry(@"C:\Windows\a.htm"), Entry(@"\\server\share\a.htm"), Entry(@"\\?\C:\a.htm"), Entry("NUL.htm"), Entry("a.htm:stream.htm")
            });
            Assert.All(s, x => Assert.Equal(StaleState.TopicNotFound, x));
        }

        [Fact]
        public void IsStale_NeverReadsOutsideContent()
        {
            File.WriteAllText(Path.Combine(_root, "Outside.htm"), "<p>It will</p>");
            var s = Exemptions.Stale(_root, new[] { Entry(@"..\Outside.htm"), Entry(@"Sub\..\..\Outside.htm") });
            Assert.All(s, x => Assert.Equal(StaleState.TopicNotFound, x));
        }

        [Fact]
        public void Current_ReloadsAfterAWrite()
        {
            var f = Finding("google.Will", "will", "It ");
            Assert.Equal(0, Exemptions.Current(_root).Count);
            Exemptions.Add(_root, Topic(@"Sub\T.htm"), f, "Brand", "ana", Day);
            Assert.Equal(1, Exemptions.Current(_root).Count);
            Exemptions.Remove(_root, Exemptions.Current(_root).Entries[0]);
            Assert.Equal(0, Exemptions.Current(_root).Count);
        }
    }
}
