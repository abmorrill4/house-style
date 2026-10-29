using System;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    [Collection("Feedback")] // these tests set the static Team.Current / FeedbackLog.PersonalPath
    public class AttributionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "attr-" + Guid.NewGuid());
        private readonly string _guide, _shared;

        public AttributionTests()
        {
            _guide = Path.Combine(_dir, "guide");
            _shared = Path.Combine(_dir, "shared");
            Directory.CreateDirectory(Path.Combine(_guide, "FlareAI"));
            Directory.CreateDirectory(_shared);
            Team.AnonymousIdPath = Path.Combine(_dir, "writer-id.txt");
            FeedbackLog.PersonalPath = Path.Combine(_dir, "personal.jsonl");
        }

        public void Dispose()
        {
            Team.Current = null; Team.AnonymousIdPath = null; FeedbackLog.PersonalPath = null;
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private static StyleFinding Finding() { return new StyleFinding { Source = "lint", Rule = "terms", RuleId = "company:terminology/1", Excerpt = "customers", Replacement = "members" }; }

        private string SharedText() { return string.Concat(Directory.GetFiles(_shared).Select(File.ReadAllText)); }

        [Fact]
        public void DefaultIsCounts_AndWritesNoEmailOrEmailNamedFile()
        {
            Team.Current = Team.Load(_guide, _shared, "pat@example.com");
            Assert.False(Team.Current.ShareNames);
            FeedbackLog.Record(Finding(), "a.htm", "rejected", "fine here");
            var files = Directory.GetFiles(_shared);
            Assert.Single(files);
            Assert.DoesNotContain("pat", Path.GetFileName(files[0]));
            Assert.DoesNotContain("pat@example.com", SharedText());
            Assert.DoesNotContain("pat@example.com", File.ReadAllText(FeedbackLog.PersonalPath));
            Assert.Contains("fine here", SharedText());
        }

        [Fact]
        public void NamesModeWritesTheEmail()
        {
            File.WriteAllText(Path.Combine(_guide, "FlareAI", "attribution.txt"), "# who is named\nattribution = names  # owner opted in\n");
            Team.Current = Team.Load(_guide, _shared, "pat@example.com");
            Assert.True(Team.Current.ShareNames);
            FeedbackLog.Record(Finding(), "a.htm", "rejected");
            Assert.Contains("pat@example.com", SharedText());
            Assert.EndsWith("pat_example.com.jsonl", Directory.GetFiles(_shared).Single());
        }

        [Fact]
        public void UnknownValueFallsBackToCounts()
        {
            File.WriteAllText(Path.Combine(_guide, "FlareAI", "attribution.txt"), "attribution = everyone\n");
            Assert.False(Team.Load(_guide, null, "pat@example.com").ShareNames);
        }

        [Fact]
        public void AnonymousIdIsStableRandomAndNotDerivedFromTheEmail()
        {
            var a = Team.AnonymousId();
            Assert.Equal(a, Team.AnonymousId());
            Assert.DoesNotContain("pat", a);
            Assert.Equal(a, File.ReadAllText(Team.AnonymousIdPath).Trim());
            File.Delete(Team.AnonymousIdPath);
            Assert.NotEqual(a, Team.AnonymousId());
        }

        [Fact]
        public void StatsCountDistinctWritersInBothModes()
        {
            // two machines write in counts mode (different anonymous ids), one in names mode
            var ids = new[] { "writer-aaa", "writer-bbb", "pat@example.com" };
            foreach (var id in ids)
                File.WriteAllText(Path.Combine(_shared, id.Replace('@', '_') + ".jsonl"),
                    "{\"at\":\"2026-10-01T00:00:00Z\",\"writer\":\"" + id + "\",\"decision\":\"rejected\",\"rule_id\":\"company:t/1\"}\n");
            var ds = FeedbackStats.Load(FeedbackStats.FilesIn(_shared));
            Assert.Equal(3, ds.Select(d => d.Writer).Distinct().Count());
            Assert.Equal(3, FeedbackStats.Anonymize(ds).Select(d => d.Writer).Distinct().Count());
            Assert.Equal("3 writers", FeedbackStats.WriterCount(3));
            Assert.Equal("1 writer", FeedbackStats.WriterCount(1));
        }

        [Fact]
        public void CountsViewsShowNoEmailFromLegacyRecords()
        {
            // a legacy record with an email in the field, and one with no writer field in an email-named file
            File.WriteAllText(Path.Combine(_shared, "pat_example.com.jsonl"),
                string.Concat(Enumerable.Range(0, 5).Select(i => "{\"at\":\"2026-10-01T00:00:00Z\",\"writer\":\"pat@example.com\",\"decision\":\"rejected\",\"reason\":\"fine\",\"rule_id\":\"company:t/1\",\"excerpt\":\"x\",\"file\":\"a.htm\"}\n")));
            File.WriteAllText(Path.Combine(_shared, "sam_example.com.jsonl"),
                string.Concat(Enumerable.Range(0, 5).Select(i => "{\"at\":\"2026-10-01T00:00:00Z\",\"decision\":\"accepted\",\"rule_id\":\"company:t/1\",\"excerpt\":\"x\",\"file\":\"a.htm\"}\n"))
                + "{\"at\":\"2026-10-01T00:00:00Z\",\"decision\":\"propose-ignore\",\"reason\":\"no\",\"rule_id\":\"company:t/1\",\"file\":\"a.htm\"}\n");
            var ds = FeedbackStats.Anonymize(FeedbackStats.Load(FeedbackStats.FilesIn(_shared)));
            var items = FeedbackStats.Analyze(ds);
            var text = string.Join("\n", items.SelectMany(i => new[] { i.Title }.Concat(i.Writers).Concat(i.Evidence)));
            Assert.NotEmpty(items);
            Assert.DoesNotContain("pat", text);
            Assert.DoesNotContain("sam", text);
            Assert.DoesNotContain("@", text);
            Assert.Equal(2, items.SelectMany(i => i.Writers).Distinct().Count());
        }

        [Fact]
        public void RestoredAfterRejected_IsNotCountedAsReject()
        {
            Team.Current = Team.Load(_guide, _shared, "pat@example.com");
            FeedbackLog.Record(Finding(), "a.htm", "rejected", "fine here");
            FeedbackLog.Record(Finding(), "a.htm", "restored");
            Assert.Empty(FeedbackStats.Load(FeedbackStats.FilesIn(_shared)));
        }

        [Fact]
        public void RestoreByAnotherWriter_DoesNotCancelTheReject()
        {
            Team.Current = Team.Load(_guide, _shared, "pat@example.com");
            FeedbackLog.Record(Finding(), "a.htm", "rejected", "fine here");
            Team.AnonymousIdPath = Path.Combine(_dir, "other-writer-id.txt");
            FeedbackLog.Record(Finding(), "a.htm", "restored");
            var ds = FeedbackStats.Load(FeedbackStats.FilesIn(_shared));
            Assert.Equal(2, ds.Count);
            Assert.Equal("rejected", ds.Single(d => d.Verdict != "restored").Verdict);
            Assert.Equal(2, ds.Select(d => d.Writer).Distinct().Count());
        }

        [Fact]
        public void RestoreWithNoEarlierReject_DoesNothing()
        {
            Team.Current = Team.Load(_guide, _shared, "pat@example.com");
            FeedbackLog.Record(Finding(), "a.htm", "restored");
            FeedbackLog.Record(Finding(), "a.htm", "rejected", "fine here");
            Assert.Equal(new[] { "restored", "rejected" }, FeedbackStats.Load(FeedbackStats.FilesIn(_shared)).Select(d => d.Verdict).ToArray());
        }
    }
}
