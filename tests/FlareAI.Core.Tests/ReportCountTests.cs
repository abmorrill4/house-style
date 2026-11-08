using System;
using System.Collections.Generic;
using System.IO;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>A toast counts what the pane will list (#449): ignored and exempt issues are left out.</summary>
    public class ReportCountTests : IDisposable
    {
        private const string Rule = "plain-words.utilize";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-count-" + Guid.NewGuid().ToString("N"));
        private readonly string _topic;

        public ReportCountTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Resources\StyleGuide"));
            File.WriteAllText(Path.Combine(_root, "Demo.flprj"), "<CatapultProject />");
            _topic = Path.Combine(_root, @"Content\A.htm");
            File.WriteAllText(_topic, "<html><body><p>We utilize it and utilize that.</p></body></html>");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private StyleFinding Finding(string excerpt, string ruleId = Rule)
        {
            return new StyleFinding { RuleId = ruleId, Rule = "x", Excerpt = excerpt, Before = "We ", Severity = "suggestion", DocumentPath = _topic };
        }

        [Fact]
        public void EveryIssueCounts_WhenNothingIsExemptOrIgnored()
        {
            var listed = ReportCount.Listed(new[] { Finding("utilize"), Finding("use", "other.rule") }, _topic, null);
            Assert.Equal(2, listed.Count);
        }

        [Fact]
        public void ExemptIssues_AreNotCounted()
        {
            Exemptions.Add(_topic, Exemptions.TopicPathFor(_topic), Rule, "utilize", "We ", "Matches the guide", "ana", new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc));
            var listed = ReportCount.Listed(new[] { Finding("utilize"), Finding("use", "other.rule") }, _topic, null);
            Assert.Single(listed);
            Assert.Equal("other.rule", listed[0].RuleId);
        }

        [Fact]
        public void IgnoredIssues_AreNotCounted()
        {
            var store = Path.Combine(_root, "dismissed.json");
            var f = Finding("utilize");
            var dismissed = Dismissals.Load(store);
            dismissed.Dismiss(f, _topic);
            var listed = ReportCount.Listed(new[] { f, Finding("use", "other.rule") }, _topic, dismissed);
            Assert.Single(listed);
        }

        [Fact]
        public void NothingLeft_ListsNothing()
        {
            Exemptions.Add(_topic, Exemptions.TopicPathFor(_topic), Rule, "utilize", "We ", "Reason", "ana", new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc));
            Assert.Empty(ReportCount.Listed(new[] { Finding("utilize") }, _topic, null));
        }
    }
}
