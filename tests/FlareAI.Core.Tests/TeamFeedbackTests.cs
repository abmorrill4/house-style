using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class TeamFeedbackTests
    {
        [Fact]
        public void Tune_CountsOnlyDecisionsAboutTheRulesCurrentWording()
        {
            var t0 = new DateTime(2026, 10, 1);
            var old = FeedbackLog.WordingHash("Say administrators.");
            var now = FeedbackLog.WordingHash("Say administrators, not end users or users.");
            var decisions = new List<Decision>();
            for (var i = 0; i < 10; i++) { var d = D(i % 2 == 0 ? "ana" : "raj", "rejected"); d.RuleHash = old; d.At = t0.AddMinutes(i); decisions.Add(d); }
            for (var i = 0; i < 10; i++) { var d = D(i % 2 == 0 ? "ana" : "raj", "accepted"); d.RuleHash = now; d.At = t0.AddDays(1).AddMinutes(i); decisions.Add(d); }
            var items = FeedbackStats.Analyze(decisions);
            Assert.DoesNotContain(items, x => x.Kind == "review");                 // the old rejections don't count against the rewrite
            Assert.Contains(items, x => x.Kind == "working" && x.Accepted == 10 && x.Rejected == 0);
            Assert.Null(FeedbackLog.WordingHash(null));
            Assert.Equal(FeedbackLog.WordingHash("Say  Administrators."), old);           // case and spacing don't change the fingerprint
        }

        private static Decision D(string writer, string verdict, string rule = "company:terminology/1", string reason = null, string excerpt = "end users", string replacement = "administrators")
        {
            return new Decision { Writer = writer, Verdict = verdict, RuleId = rule, Reason = reason, Excerpt = excerpt, Replacement = replacement, File = "a.htm" };
        }

        [Fact]
        public void OwnersFileMakesTheTeamStrict()
        {
            var root = Path.Combine(Path.GetTempPath(), "guide-" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(root, "FlareAI"));
            try
            {
                Assert.False(Team.Load(root, null, "pat@example.com").IsStrict);
                File.WriteAllText(Path.Combine(root, "FlareAI", "owners.txt"), "# style guide owners\nlead@example.com  # docs lead\n");
                File.WriteAllText(Path.Combine(root, "FlareAI", "attribution.txt"), "attribution = names");
                var writer = Team.Load(root, @"\\share\feedback", "pat@example.com");
                Assert.True(writer.IsStrict);
                Assert.False(writer.IsOwner);
                Assert.EndsWith(Path.Combine("feedback", "pat_example.com.jsonl"), writer.SharedFeedbackFile);
                Assert.True(Team.Load(root, null, "LEAD@example.com").IsOwner);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void OneWritersTasteDoesNotRaiseARule()
        {
            var ds = Enumerable.Range(0, 12).Select(i => D("pat", "rejected", reason: "fine here")).ToList();
            Assert.DoesNotContain(FeedbackStats.Analyze(ds), i => i.Kind == "review");
        }

        [Fact]
        public void OftenRejectedAcrossWritersIsRaisedWithReasonsAndExamples()
        {
            var ds = new List<Decision>();
            ds.AddRange(Enumerable.Range(0, 5).Select(i => D("pat", "rejected", reason: "fine here")));
            ds.AddRange(Enumerable.Range(0, 2).Select(i => D("sam", "rejected", reason: "fine here")));
            ds.AddRange(Enumerable.Range(0, 2).Select(i => D("sam", "accepted")));
            var item = FeedbackStats.Analyze(ds).Single(i => i.Kind == "review");
            Assert.Equal("company:terminology/1", item.Rule);
            Assert.Contains(item.Evidence, e => e.Contains("fine here") && e.Contains("7×"));
            Assert.Contains(item.Evidence, e => e.Contains("\"end users\""));
        }

        [Fact]
        public void WritersWhoSplitOnARuleMakeItDisputed()
        {
            var ds = new List<Decision>();
            ds.AddRange(Enumerable.Range(0, 4).Select(i => D("pat", "accepted")));
            ds.AddRange(Enumerable.Range(0, 4).Select(i => D("sam", "rejected", reason: "wrong")));
            var item = FeedbackStats.Analyze(ds).First();
            Assert.Equal("disputed", item.Kind);
            Assert.Contains(item.Evidence, e => e.StartsWith("pat changes"));
            Assert.Contains(item.Evidence, e => e.StartsWith("sam ignores"));
        }

        [Fact]
        public void RepeatedHandEditsBecomeACandidateRow()
        {
            var ds = new List<Decision> { D("pat", "edited", excerpt: "log in", replacement: "sign in"), D("sam", "edited", excerpt: "Log in", replacement: "sign in") };
            var item = FeedbackStats.Analyze(ds).Single(i => i.Kind == "edit");
            Assert.Contains("log in", item.Title);
            Assert.Equal(2, item.Writers.Count);
        }

        [Fact]
        public void SharedFilesLoadPerWriterAndSkipTornLines()
        {
            var dir = Path.Combine(Path.GetTempPath(), "fb-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "pat.jsonl"), "{\"writer\":\"pat\",\"decision\":\"accepted\",\"rule\":\"Google.Will\"}\n{\"writer\":\"pat\",\"deci");
                File.WriteAllText(Path.Combine(dir, "sam.jsonl"), "{\"decision\":\"rejected\",\"rule\":\"Google.Will\",\"reason\":\"wrong\"}\n");
                var ds = FeedbackStats.Load(FeedbackStats.FilesIn(dir));
                Assert.Equal(2, ds.Count);
                Assert.Equal("sam", ds.Single(d => d.IsReject).Writer); // no writer field: named after the file
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void RestoredAfterRejected_IsNotCountedAsReject()
        {
            var ds = Enumerable.Range(0, 8).Select(i => D("pat", "accepted")).ToList();
            ds.Add(D("pat", "rejected"));
            ds.Add(D("pat", "restored"));
            var item = FeedbackStats.Analyze(ds).Single(i => i.Kind == "working");
            Assert.Equal(8, item.Accepted);
            Assert.Equal(0, item.Rejected);
        }

        [Fact]
        public void RestoreByAnotherWriter_DoesNotCancelTheReject()
        {
            var ds = Enumerable.Range(0, 8).Select(i => D("pat", "rejected")).ToList();
            ds.Add(D("sam", "accepted"));
            ds.Add(D("sam", "restored"));
            var item = FeedbackStats.Analyze(ds).Single(i => i.Kind == "review");
            Assert.Equal(8, item.Rejected);
        }

        [Fact]
        public void RestoreWithNoEarlierReject_DoesNothing()
        {
            var ds = Enumerable.Range(0, 8).Select(i => D("pat", "accepted")).ToList();
            ds.Add(D("pat", "restored"));
            var item = FeedbackStats.Analyze(ds).Single(i => i.Kind == "working");
            Assert.Equal(8, item.Accepted);
            Assert.Equal(0, item.Rejected);
        }
    }
}
