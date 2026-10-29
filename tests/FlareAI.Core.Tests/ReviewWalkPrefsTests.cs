using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>The review view prefs: the rule walked last is kept as a hash of its project or topic, and a bad key is dropped.</summary>
    public class ReviewWalkPrefsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-prefs-" + Guid.NewGuid().ToString("N"));
        private readonly string _file;

        public ReviewWalkPrefsTests()
        {
            Directory.CreateDirectory(_root);
            _file = Path.Combine(_root, "review-view.json");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        private static BacklogRule Rule(string key, string title) { return new BacklogRule { Key = key, Title = title }; }

        [Fact]
        public void Walk_StoresAHashNotAPath()
        {
            var topic = Path.Combine(_root, "Secret Project", "Content", "topic.htm");
            var scope = ReviewWalkPrefs.ScopeOf(topic);
            Assert.Matches("^[0-9a-f]{64}$", scope);

            new ReviewWalkPrefs { Walk = new ReviewWalkPrefs.RememberedWalk { Scope = scope, RuleKey = "Google.WordList" } }.TrySave(_file);

            var text = File.ReadAllText(_file);
            Assert.Contains(scope, text);
            Assert.DoesNotContain("Secret Project", text);
            Assert.DoesNotContain(Path.GetFullPath(topic), text);
            Assert.DoesNotContain(JsonConvert.ToString(Path.GetFullPath(topic)).Trim('"'), text);
            Assert.Equal(scope, ReviewWalkPrefs.Load(_file).Walk.Scope);
        }

        [Fact]
        public void Walk_UnknownOrOversizedKey_IsDropped()
        {
            var scope = ReviewWalkPrefs.ScopeOf(Path.Combine(_root, "Project"));
            var backlog = new List<BacklogRule> { Rule("Google.WordList", "Use use, not utilize") };

            // Empty and 201 characters: dropped when the prefs are read, with no error.
            foreach (var key in new[] { "", new string('k', ReviewWalkPrefs.MaxKeyLength + 1) })
            {
                new ReviewWalkPrefs { Walk = new ReviewWalkPrefs.RememberedWalk { Scope = scope, RuleKey = key } }.TrySave(_file);
                Assert.Null(ReviewWalkPrefs.Load(_file).Walk);
            }

            // Exactly 200 characters is a valid key; it is kept on read.
            new ReviewWalkPrefs { Walk = new ReviewWalkPrefs.RememberedWalk { Scope = scope, RuleKey = new string('k', ReviewWalkPrefs.MaxKeyLength) } }.TrySave(_file);
            Assert.NotNull(ReviewWalkPrefs.Load(_file).Walk);

            // A key that matches no rule in the backlog names no rule (the session then drops it).
            var unknown = new ReviewWalkPrefs { Walk = new ReviewWalkPrefs.RememberedWalk { Scope = scope, RuleKey = "No.Such.Rule" } };
            Assert.Null(unknown.RuleIn(scope, backlog));
        }

        [Fact]
        public void RuleIn_NeedsTheSameScopeAndTheExactKey()
        {
            var scope = ReviewWalkPrefs.ScopeOf(Path.Combine(_root, "Project"));
            var backlog = new List<BacklogRule> { Rule("Google.WordList", "Use use, not utilize") };
            var walk = new ReviewWalkPrefs { Walk = new ReviewWalkPrefs.RememberedWalk { Scope = scope, RuleKey = "Google.WordList" } };

            Assert.Same(backlog[0], walk.RuleIn(scope, backlog));
            Assert.Null(walk.RuleIn(ReviewWalkPrefs.ScopeOf(Path.Combine(_root, "Other")), backlog));
            Assert.Null(walk.RuleIn(scope, new List<BacklogRule> { Rule("google.wordlist", "Case differs") }));
        }

        [Fact]
        public void NeverStoresDocumentText()
        {
            new ReviewWalkPrefs { Walk = new ReviewWalkPrefs.RememberedWalk { Scope = ReviewWalkPrefs.ScopeOf(_root), RuleKey = "Google.WordList" } }.TrySave(_file);

            var json = JObject.Parse(File.ReadAllText(_file));
            Assert.Equal(new[] { "Walk" }, json.Properties().Select(p => p.Name));
            Assert.Equal(new[] { "Scope", "RuleKey" }, ((JObject)json["Walk"]).Properties().Select(p => p.Name));
        }
    }
}
