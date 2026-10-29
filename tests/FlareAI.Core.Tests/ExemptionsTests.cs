using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class ExemptionsTests : IDisposable
    {
        private const string Rule = "plain-words.utilize";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-exempt-" + Guid.NewGuid().ToString("N"));
        private readonly string _b;
        private readonly DateTime _when = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        private readonly List<string> _logged = new List<string>();

        public ExemptionsTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Sub"));
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Resources\StyleGuide"));
            File.WriteAllText(Path.Combine(_root, "Demo.flprj"), "<CatapultProject />");
            File.WriteAllText(Path.Combine(_root, @"Content\A.htm"), "<html><body><p>We utilize it.</p></body></html>");
            _b = Path.Combine(_root, @"Content\Sub\B.htm");
            File.WriteAllText(_b, "<html><body><p>We utilize it. The cat sat. A cat ran.</p></body></html>");
            Exemptions.LogSink = _logged.Add;
        }

        public void Dispose()
        {
            Exemptions.LogSink = Diagnostics.Log;
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string ListFile { get { return Exemptions.FileFor(_root); } }

        private void WriteLines(params string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile));
            File.WriteAllLines(ListFile, lines);
        }

        [Fact]
        public void Covers_MatchesTopicRuleTextAndBeforeExactly()
        {
            Exemptions.Add(_b, Exemptions.TopicPathFor(_b), Rule, "utilize", "We ", "Matches the guide's wording", "ana", _when);
            var list = Exemptions.Load(_b);
            Assert.True(Exemptions.Covers(list, @"Sub\B.htm", Rule, "utilize", "We "));
        }

        [Fact]
        public void Covers_DoesNotMatchOtherRuleOtherTextOrOtherTopic()
        {
            Exemptions.Add(_b, Exemptions.TopicPathFor(_b), Rule, "utilize", "We ", "Reason", "ana", _when);
            var list = Exemptions.Load(_b);
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.use", "utilize", "We "));
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", Rule, "utilise", "We "));
            Assert.False(Exemptions.Covers(list, @"A.htm", Rule, "utilize", "We "));
        }

        [Fact]
        public void Covers_SameWordTwiceInOneTopic_HidesOnlyTheExemptedCopy()
        {
            Exemptions.Add(_b, Exemptions.TopicPathFor(_b), "plain-words.cat", "cat", "The ", "Reason", "ana", _when);
            var list = Exemptions.Load(_b);
            Assert.True(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.cat", "cat", "The "));
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", "plain-words.cat", "cat", "A "));
        }

        [Fact]
        public void Covers_TopicPathIgnoresCase_ButTextAndBeforeDoNot()
        {
            Exemptions.Add(_b, Exemptions.TopicPathFor(_b), Rule, "utilize", "We ", "Reason", "ana", _when);
            var list = Exemptions.Load(_b);
            Assert.True(Exemptions.Covers(list, @"SUB\b.HTM", Rule, "utilize", "We "));
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", Rule, "UTILIZE", "We "));
            Assert.False(Exemptions.Covers(list, @"Sub\B.htm", Rule, "utilize", "WE "));
        }

        [Fact]
        public void Add_UsesTheSamePathForLintTopicSourcePathAndLintProjectDocumentPath()
        {
            var lintTopic = Exemptions.TopicPathFor(_b);
            var lintProject = Exemptions.TopicPathFor(_b.ToUpperInvariant());
            Assert.Equal(lintTopic, lintProject, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(@"Sub\B.htm", lintTopic);

            Exemptions.Add(_b, lintProject, Rule, "utilize", "We ", "Reason", "ana", _when);
            var list = Exemptions.Load(_b);
            Assert.True(Exemptions.Covers(list, lintTopic, Rule, "utilize", "We "));
        }

        [Fact]
        public void RoundTrip_KeepsReasonWhoAndDate_AndStripsTabsAndLineBreaks()
        {
            Exemptions.Add(_b, @"Sub\B.htm", Rule, "We\tutilize\r\nit", "Ok\t", "Brand\tvoice", "ana", _when);
            var list = Exemptions.Load(_b);
            var e = list.Entries.Single();
            Assert.Equal(@"Sub\B.htm", e.Topic);
            Assert.Equal("We utilize  it", e.Text);
            Assert.Equal("Ok ", e.Before);
            Assert.Equal("Brand voice", e.Reason);
            Assert.Equal("ana", e.Who);
            Assert.Equal("2026-10-28", e.When);
            Assert.True(Exemptions.Covers(list, @"Sub\B.htm", Rule, "We\tutilize\r\nit", "Ok\t"));
        }

        [Fact]
        public void Load_SkipsUnsafeLines_ParentPathsUncDriveRootedDeviceStreamAndLongKeys()
        {
            var tail = "\t" + Rule + "\tutilize\t\tReason\tana\t2026-10-28";
            WriteLines(
                "# A comment",
                "Sub\\B.htm" + tail,
                "..\\Secret.htm" + tail,
                "\\\\server\\share\\B.htm" + tail,
                "C:\\Users\\B.htm" + tail,
                "CON.htm" + tail,
                "Sub\\B.htm:stream.htm" + tail,
                "Sub\\B.htm\t" + new string('r', 201) + "\tutilize\t\tReason\tana\t2026-10-28",
                "Sub\\B.htm\t" + Rule + "\tutilize\t\tReason\tana",
                "Sub\\B.htm\t" + Rule + "\t\t\tReason\tana\t2026-10-28",
                "");

            var list = Exemptions.Load(_b);

            var e = list.Entries.Single();
            Assert.Equal(@"Sub\B.htm", e.Topic);
            Assert.Equal("Exemptions.txt: 8 lines skipped (lines 3, 4, 5, 6, 7, 8, 9, 10)", _logged.Single());
        }

        [Fact]
        public void Load_LogsOnlyCountAndLineNumbers()
        {
            WriteLines("# comment", "..\\Secret words.htm\t" + Rule + "\tsecret words\t\tr\tana\t2026-10-28");

            Exemptions.Load(_b);

            Assert.Equal("Exemptions.txt: 1 lines skipped (lines 2)", _logged.Single());
            Assert.DoesNotContain("secret", _logged.Single());
        }

        [Fact]
        public void Load_SkipsAFileOver1MB_AndFieldsOver2000()
        {
            WriteLines(
                "Sub\\B.htm\t" + Rule + "\t" + new string('t', 2001) + "\t\tReason\tana\t2026-10-28",
                "Sub\\B.htm\t" + Rule + "\t" + new string('t', 2000) + "\t\tReason\tana\t2026-10-28");

            var list = Exemptions.Load(_b);

            Assert.Equal(2000, Assert.Single(list.Entries).Text.Length);

            _logged.Clear();
            WriteLines("Sub\\B.htm\t" + Rule + "\tutilize\t\tReason\tana\t2026-10-28", "# " + new string('x', 1100 * 1024));

            Assert.Empty(Exemptions.Load(_b).Entries);
            Assert.Contains("1 MB", _logged.Single());
        }

        [Fact]
        public void Remove_DropsOnlyThatLine()
        {
            Exemptions.Add(_b, @"Sub\B.htm", Rule, "utilize", "We ", "Reason", "ana", _when);
            Exemptions.Add(_b, @"Sub\B.htm", Rule, "utilize", "It ", "Reason", "ana", _when);
            File.AppendAllText(ListFile, "# keep me" + Environment.NewLine);

            var first = Exemptions.Load(_b).Entries.First();
            Exemptions.Remove(_b, first);

            var list = Exemptions.Load(_b);
            Assert.Equal("It ", list.Entries.Single().Before);
            Assert.Contains("# keep me", File.ReadAllLines(ListFile));
        }

        [Fact]
        public void IsStale_WhenTheTopicNoLongerHoldsTheText()
        {
            Exemptions.Add(_b, @"Sub\B.htm", Rule, "utilize", "We ", "Reason", "ana", _when);
            var entries = Exemptions.Load(_b).Entries;
            Assert.Equal(StaleNote.None, Exemptions.StaleNotes(_root, entries).Single());

            File.WriteAllText(_b, "<html><body><p>We use it.</p></body></html>");

            Assert.Equal(StaleNote.TextNotFound, Exemptions.StaleNotes(_root, entries).Single());
        }

        [Fact]
        public void IsStale_ShowsTopicNotFound_ForAMissingMovedOrUnsafeTopic()
        {
            var entries = new List<Exemptions.Entry>
            {
                new Exemptions.Entry(@"Sub\Gone.htm", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
                new Exemptions.Entry(@"..\A.htm", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
                new Exemptions.Entry(@"CON.htm", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
                new Exemptions.Entry(@"Sub\B.htm:stream.htm", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
            };

            Assert.Equal(new[] { StaleNote.TopicNotFound, StaleNote.TopicNotFound, StaleNote.TopicNotFound, StaleNote.TopicNotFound }, Exemptions.StaleNotes(_root, entries));
        }

        [Fact]
        public void IsStale_NeverOpensARootedDeviceOrAlternateStreamPath()
        {
            var entries = new List<Exemptions.Entry>
            {
                new Exemptions.Entry(@"C:\Windows\win.htm", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
                new Exemptions.Entry(@"\\?\C:\x.htm", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
                new Exemptions.Entry(@"\\.\CON", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
                new Exemptions.Entry(@"Sub\B.htm:stream", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
            };

            Assert.Equal(new[] { StaleNote.TopicNotFound, StaleNote.TopicNotFound, StaleNote.TopicNotFound, StaleNote.TopicNotFound }, Exemptions.StaleNotes(_root, entries));
        }

        [Fact]
        public void IsStale_NeverReadsOutsideContent()
        {
            File.WriteAllText(Path.Combine(_root, "Outside.htm"), "<p>We utilize it.</p>");
            var entries = new List<Exemptions.Entry>
            {
                new Exemptions.Entry(@"..\Outside.htm", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
                new Exemptions.Entry(@"Sub\..\..\Outside.htm", Rule, "utilize", "", "Reason", "ana", "2026-10-28"),
            };

            Assert.Equal(new[] { StaleNote.TopicNotFound, StaleNote.TopicNotFound }, Exemptions.StaleNotes(_root, entries));
        }
    }
}
