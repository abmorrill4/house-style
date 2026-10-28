using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    [Collection("Logging")]   // these tests swap the static Diagnostics.Sink
    public class ExemptionsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-exm-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _logged = new List<string>();
        private readonly Action<string> _oldSink;

        public ExemptionsTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Sub"));
            Directory.CreateDirectory(Path.Combine(_root, @"Project\HouseStyle"));
            File.WriteAllText(Path.Combine(_root, "Demo.flprj"), "<CatapultProject />");
            _oldSink = Diagnostics.Sink;
            Diagnostics.Sink = m => { lock (_logged) _logged.Add(m); };
        }

        public void Dispose()
        {
            Diagnostics.Sink = _oldSink;
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string B { get { return Path.Combine(_root, @"Content\Sub\B.htm"); } }
        private string ExemptionsFile { get { return Path.Combine(_root, @"Project\HouseStyle\Exemptions.txt"); } }

        private static string Line(string topic, string rule, string text)
        {
            return string.Join("\t", new[] { topic, rule, text, "We ", "reason", "Pat", "2026-10-28" });
        }

        private void WriteLines(params string[] lines) { File.WriteAllLines(ExemptionsFile, lines); }

        private List<string> ExemptionsLog()
        {
            lock (_logged) return _logged.Where(m => m.StartsWith("Exemptions.txt")).ToList();
        }

        [Fact]
        public void Covers_MatchesTopicRuleTextAndBeforeExactly()
        {
            var list = new[] { new Exemption(@"Sub\B.htm", "plain-words.utilize", "utilize", "We ", "why", "me", "2026-10-28") };
            Assert.True(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.utilize", "utilize", "We "));
        }

        [Fact]
        public void Covers_DoesNotMatchOtherRuleOtherTextOrOtherTopic()
        {
            var list = new[] { new Exemption(@"Sub\B.htm", "plain-words.utilize", "utilize", "We ", "why", "me", "2026-10-28") };
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.other", "utilize", "We "));
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.utilize", "utilise", "We "));
            Assert.False(Exemptions.Covers(list, @"Sub\A.htm", "plain-words.utilize", "utilize", "We "));
        }

        [Fact]
        public void Covers_SameWordTwiceInOneTopic_HidesOnlyTheExemptedCopy()
        {
            var list = new[] { new Exemption(@"Sub\B.htm", "plain-words.utilize", "utilize", "We ", "why", "me", "2026-10-28") };
            Assert.True(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.utilize", "utilize", "We "));
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.utilize", "utilize", "Then we "));
        }

        [Fact]
        public void Covers_TopicPathIgnoresCase_ButTextAndBeforeDoNot()
        {
            var list = new[] { new Exemption(@"Sub\B.htm", "plain-words.utilize", "utilize", "We ", "why", "me", "2026-10-28") };
            Assert.True(Exemptions.Covers(list, @"SUB\b.HTM", "plain-words.utilize", "utilize", "We "));
            Assert.True(Exemptions.Covers(list, "Sub/B.htm", "plain-words.utilize", "utilize", "We "));
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.utilize", "UTILIZE", "We "));
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.utilize", "utilize", "we "));
        }

        [Fact]
        public void Add_UsesTheSamePathForLintTopicSourcePathAndLintProjectDocumentPath()
        {
            var lint = new StyleFinding { Source = "lint", Rule = "plain-words.utilize", Excerpt = "utilize", Before = "We " };
            var fromDocument = new StyleFinding { Source = "lint", Rule = "plain-words.utilize", Excerpt = "utilize", Before = "We ", DocumentPath = B };

            var topic = Exemptions.TopicKey(lint, B);
            Assert.Equal(@"Sub\B.htm", topic);
            Assert.Equal(topic, Exemptions.TopicKey(fromDocument, Path.Combine(_root, @"Content\Sub\A.htm")));

            Exemptions.Add(B, topic, Exemptions.RuleKey(lint), "utilize", "We ", "why", "me");
            Assert.True(Exemptions.Covers(Exemptions.Load(B), Exemptions.TopicKey(fromDocument, B), "plain-words.utilize", "utilize", "We "));
        }

        [Fact]
        public void RoundTrip_KeepsReasonWhoAndDate_AndStripsTabsAndLineBreaks()
        {
            Exemptions.Add(B, @"Sub\B.htm", "plain-words.utilize", "utilize", "We ", "Brand\tname\r\nnote", "Pat");

            var data = File.ReadAllLines(ExemptionsFile).Single(l => !l.StartsWith("#"));
            Assert.Equal(7, data.Split('\t').Length);

            var e = Assert.Single(Exemptions.Load(B));
            Assert.Equal(@"Sub\B.htm", e.Topic);
            Assert.Equal("plain-words.utilize", e.Rule);
            Assert.Equal("utilize", e.Text);
            Assert.Equal("We ", e.Before);
            Assert.Equal("Brand name  note", e.Reason);
            Assert.Equal("Pat", e.Who);
            Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), e.When);
        }

        [Fact]
        public void Load_SkipsUnsafeLines_ParentPathsUncDriveRootedDeviceStreamAndLongKeys()
        {
            WriteLines(
                "# Exempted text. A comment, not a skipped line.",          // 1
                Line(@"Sub\B.htm", "plain-words.utilize", "utilize"),         // 2 safe
                Line(@"..\Secret.htm", "rule", "secret"),                     // 3 parent segment
                Line(@"\\server\share\B.htm", "rule", "secret"),              // 4 UNC
                Line(@"C:\Sub\B.htm", "rule", "secret"),                     // 5 drive
                Line(@"\Sub\B.htm", "rule", "secret"),                        // 6 rooted
                Line(@"\\?\C:\B.htm", "rule", "secret"),                     // 7 device path
                Line(@"Sub\CON.htm", "rule", "secret"),                      // 8 DOS device name
                Line(@"Sub\B.htm:stream.htm", "rule", "secret"),             // 9 alternate stream
                Line(@"Sub\B.txt", "rule", "secret"),                        // 10 not a topic type
                "Sub\\B.htm\trule\tsecret\tb\tr\tw",                         // 11 six fields
                Line(@"Sub\B.htm", new string('r', 201), "secret"),          // 12 rule over 200 chars
                Line("Sub/C.html", "plain-words.utilize", "utilize"));       // 13 safe, forward slashes

            var loaded = Exemptions.Load(B);
            Assert.Equal(new[] { @"Sub\B.htm", @"Sub\C.html" }, loaded.Select(e => e.Topic).ToArray());

            var log = Assert.Single(ExemptionsLog());
            Assert.Equal("Exemptions.txt: 10 lines skipped (lines 3, 4, 5, 6, 7, 8, 9, 10, 11, 12)", log);
            Assert.DoesNotContain("secret", log);
        }

        [Fact]
        public void Load_SkipsAFileOver1MB_AndFieldsOver2000()
        {
            WriteLines(Line(@"Sub\B.htm", "plain-words.utilize", "utilize"));
            File.AppendAllText(ExemptionsFile, "# " + new string('x', 1048576) + "\r\n");
            Assert.Empty(Exemptions.Load(B));
            lock (_logged) _logged.Clear();

            WriteLines(
                Line(@"Sub\B.htm", "plain-words.utilize", new string('a', 2001)),   // 1 field over 2000: skipped
                Line(@"Sub\B.htm", "plain-words.utilize", new string('a', 2000)));  // 2 exactly 2000: kept
            var loaded = Exemptions.Load(B);
            var kept = Assert.Single(loaded);
            Assert.Equal(2000, kept.Text.Length);
            Assert.Equal("Exemptions.txt: 1 line skipped (line 1)", Assert.Single(ExemptionsLog()));
        }

        [Fact]
        public void Remove_DropsOnlyThatLine()
        {
            Exemptions.Add(B, @"Sub\B.htm", "plain-words.utilize", "a", "We ", "r", "me");
            Exemptions.Add(B, @"Sub\B.htm", "plain-words.utilize", "b", "We ", "r", "me");
            Exemptions.Add(B, @"Sub\B.htm", "plain-words.utilize", "a", "We ", "r", "me");

            var loaded = Exemptions.Load(B);
            Assert.Equal(3, loaded.Count);
            Exemptions.Remove(B, loaded[0]);

            Assert.Equal(new[] { "b", "a" }, Exemptions.Load(B).Select(e => e.Text).ToArray());
            Assert.Contains("# Text exempted from a rule.", File.ReadAllText(ExemptionsFile));
        }
    }
}
