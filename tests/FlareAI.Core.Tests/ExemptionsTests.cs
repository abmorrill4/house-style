using System;
using System.IO;
using System.Text;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class ExemptionsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-ex-" + Guid.NewGuid().ToString("N"));

        public ExemptionsTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Sub"));
            File.WriteAllText(Path.Combine(_root, "Demo.flprj"), "<CatapultProject />");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private static ExemptionEntry E(string topic = @"Sub\A.htm", string rule = "Google.Will", string text = "will", string before = "It ", string reason = "quoted", string who = "ann", string when = "2026-10-28")
        {
            return new ExemptionEntry(topic, rule, text, before, reason, who, when);
        }

        private ExemptionList Reload() { return Exemptions.Load(_root); }

        private void WriteTopic(string rel, string html)
        {
            var p = Path.Combine(_root, "Content", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, html);
        }

        [Fact]
        public void Covers_MatchesTopicRuleTextAndBeforeExactly()
        {
            Assert.Null(Exemptions.Add(_root, E()));
            Assert.True(Exemptions.Covers(Reload(), @"Sub\A.htm", "Google.Will", "will", "It "));
        }

        [Fact]
        public void Covers_DoesNotMatchOtherRuleOtherTextOrOtherTopic()
        {
            Exemptions.Add(_root, E());
            var list = Reload();
            Assert.False(Exemptions.Covers(list, @"Sub\A.htm", "Google.Passive", "will", "It "));
            Assert.False(Exemptions.Covers(list, @"Sub\A.htm", "Google.Will", "shall", "It "));
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", "Google.Will", "will", "It "));
        }

        [Fact]
        public void Covers_SameWordTwiceInOneTopic_HidesOnlyTheExemptedCopy()
        {
            Exemptions.Add(_root, E(before: "It "));
            var list = Reload();
            Assert.True(Exemptions.Covers(list, @"Sub\A.htm", "Google.Will", "will", "It "));
            Assert.False(Exemptions.Covers(list, @"Sub\A.htm", "Google.Will", "will", "They "));
        }

        [Fact]
        public void Covers_TopicPathIgnoresCase_ButTextAndBeforeDoNot()
        {
            Exemptions.Add(_root, E());
            var list = Reload();
            Assert.True(Exemptions.Covers(list, @"sub/a.HTM", "Google.Will", "will", "It "));
            Assert.False(Exemptions.Covers(list, @"Sub\A.htm", "Google.Will", "Will", "It "));
            Assert.False(Exemptions.Covers(list, @"Sub\A.htm", "Google.Will", "will", "it "));
            Assert.False(Exemptions.Covers(list, @"Sub\A.htm", "google.will", "will", "It "));
        }

        [Fact]
        public void Add_UsesTheSamePathForLintTopicSourcePathAndLintProjectDocumentPath()
        {
            var fromTopic = Exemptions.TopicKey(Path.Combine(_root, @"Content\Sub\A.htm"));
            var fromProject = Exemptions.TopicKey(Path.Combine(_root, "content") + "/SUB/a.htm");
            Assert.NotNull(fromTopic);
            Assert.Equal(fromTopic, fromProject, StringComparer.OrdinalIgnoreCase);
            Assert.Null(Exemptions.TopicKey(Path.Combine(Path.GetTempPath(), "nowhere-" + Guid.NewGuid().ToString("N") + ".htm")));
        }

        [Fact]
        public void RoundTrip_KeepsReasonWhoAndDate_AndStripsTabsAndLineBreaks()
        {
            Assert.Null(Exemptions.Add(_root, E(reason: "a\tb\r\nc", who: "Ann\tLee", text: "wi\tll")));
            var e = Reload().Entries[0];
            Assert.Equal("a b  c", e.Reason);
            Assert.Equal("Ann Lee", e.Who);
            Assert.Equal("2026-10-28", e.When);
            Assert.Equal("wi ll", e.Text);
            Assert.Equal(1, Exemptions.Count(_root));
            Assert.StartsWith("# Text exempted from a rule.", File.ReadAllLines(Exemptions.FileFor(_root))[0]);
        }

        [Fact]
        public void Load_SkipsUnsafeLines_ParentPathsUncDriveRootedDeviceStreamAndLongKeys()
        {
            var bad = new[] { @"..\x.htm", @"Sub\..\x.htm", @"\\srv\share\x.htm", @"C:\x.htm", @"\x.htm", @"\\?\C:\x.htm", @"\\.\C:\x.htm", @"Sub\CON.htm", @"nul.htm", @"Sub\com1.x.htm", "a.htm:s.htm", "a.txt", "a<b.htm", "a>b.htm", "a|b.htm", "a\"b.htm", "a\u0001b.htm" };
            var sb = new StringBuilder("# header\r\n");
            foreach (var t in bad) sb.Append(t + "\tR\ttext\tb\tr\tw\td\r\n");
            sb.Append(@"Sub\ok.htm" + "\t" + new string('r', 201) + "\ttext\tb\tr\tw\td\r\n");
            sb.Append(@"Sub\ok.htm" + "\tR\ttext\tb\tr\r\n");
            sb.Append(@"Sub\ok.htm" + "\tR\ttext\tb\tr\tw\td\r\n");
            var file = Exemptions.FileFor(_root);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, sb.ToString());
            var list = Reload();
            Assert.Equal(1, list.Count);
            Assert.Equal(@"Sub\ok.htm", list.Entries[0].Topic);
            Assert.True(Exemptions.IsSafeTopicPath("Sub/ok.FLSNP"));
            Assert.False(Exemptions.IsSafeTopicPath(null));
            Assert.False(Exemptions.IsSafeTopicPath("a<b.htm"));
            Assert.False(Exemptions.IsSafeTopicPath("a|b.htm"));
            Assert.False(Exemptions.IsSafeTopicPath("a\u0001b.htm"));
        }

        [Fact]
        public void SkippedNote_HoldsOnlyACountAndLineNumbers()
        {
            Assert.Equal("Exemptions.txt: 3 lines skipped (lines 4, 9, 12)", Exemptions.SkippedNote(new[] { 4, 9, 12 }));
            Assert.Equal("Exemptions.txt: 1 line skipped (lines 2)", Exemptions.SkippedNote(new[] { 2 }));
        }

        [Fact]
        public void Load_SkipsAFileOver1MB_AndFieldsOver2000()
        {
            var file = Exemptions.FileFor(_root);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, @"Sub\A.htm" + "\tR\t" + new string('x', 2001) + "\tb\tr\tw\td\r\n" + @"Sub\A.htm" + "\tR\tt\tb\tr\tw\td\r\n");
            Assert.Equal(1, Reload().Count);
            File.WriteAllText(file, @"Sub\A.htm" + "\tR\tt\tb\t" + new string('y', 1024 * 1024) + "\tw\td\r\n");
            Assert.Equal(0, Reload().Count);
        }

        [Fact]
        public void Remove_DropsOnlyThatLine()
        {
            Exemptions.Add(_root, E(text: "will"));
            Exemptions.Add(_root, E(text: "shall"));
            Assert.Null(Exemptions.Remove(_root, E(text: "will")));
            var list = Reload();
            Assert.Equal(1, list.Count);
            Assert.Equal("shall", list.Entries[0].Text);
        }

        [Fact]
        public void IsStale_WhenTheTopicNoLongerHoldsTheText()
        {
            WriteTopic(@"Sub\A.htm", "<p>It will rain.</p>");
            Assert.Null(Exemptions.IsStale(_root, E()));
            Assert.Equal("Text not found", Exemptions.IsStale(_root, E(text: "shall")));
            Assert.Equal("Text not found", Exemptions.IsStale(_root, E(before: "They ")));
        }

        [Fact]
        public void IsStale_ShowsTopicNotFound_ForAMissingMovedOrUnsafeTopic()
        {
            Assert.Equal("Topic not found", Exemptions.IsStale(_root, E(topic: @"Sub\Missing.htm")));
            Assert.Equal("Topic not found", Exemptions.IsStale(_root, E(topic: @"..\A.htm")));
        }

        [Fact]
        public void IsStale_NeverOpensARootedDeviceOrAlternateStreamPath()
        {
            foreach (var t in new[] { @"C:\Windows\win.ini.htm", @"\\?\C:\x.htm", @"\\.\pipe\x.htm", @"Sub\A.htm:evil.htm", @"CON.htm", @"\\srv\share\x.htm" })
                Assert.Equal("Topic not found", Exemptions.IsStale(_root, E(topic: t)));
        }

        [Fact]
        public void IsStale_NeverReadsOutsideContent()
        {
            File.WriteAllText(Path.Combine(_root, "Outside.htm"), "It will");
            Assert.Equal("Topic not found", Exemptions.IsStale(_root, E(topic: @"..\Outside.htm")));
            Assert.Equal("Topic not found", Exemptions.IsStale(_root, E(topic: @"Sub\..\..\Outside.htm")));
        }

        [Fact]
        public void HiddenTail_UsesPlural()
        {
            Assert.Equal("1 issue is exempt.", Exemptions.HiddenTail(1));
            Assert.Equal("2 issues are exempt.", Exemptions.HiddenTail(2));
        }
    }
}
