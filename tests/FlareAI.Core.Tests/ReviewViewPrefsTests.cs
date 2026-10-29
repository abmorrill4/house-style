using System;
using System.IO;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class ReviewViewPrefsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "reviewview-" + Guid.NewGuid().ToString("N"));
        private string FilePath { get { return Path.Combine(_dir, "review-view.json"); } }

        public ReviewViewPrefsTests() { Directory.CreateDirectory(_dir); }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

        [Fact]
        public void Missing_LoadsAsAll()
        {
            ReviewLevel level;
            ReviewSource source;
            ReviewViewPrefs.Load(FilePath, out level, out source);
            Assert.Equal(ReviewLevel.All, level);
            Assert.Equal(ReviewSource.RulesAndAi, source);
        }

        [Fact]
        public void SaveThenLoad_RoundTrips()
        {
            ReviewViewPrefs.Save(FilePath, ReviewLevel.Warnings, ReviewSource.AiOnly);
            ReviewLevel level;
            ReviewSource source;
            ReviewViewPrefs.Load(FilePath, out level, out source);
            Assert.Equal(ReviewLevel.Warnings, level);
            Assert.Equal(ReviewSource.AiOnly, source);
        }

        [Theory]
        [InlineData("{\"level\":\"bogus\",\"source\":\"bogus\"}")]
        [InlineData("{\"level\":42,\"source\":true}")]
        [InlineData("{\"level\":{\"nested\":1},\"source\":[1]}")]
        [InlineData("{\"level\":\"ERRORS-ish\"}")]
        [InlineData("{ not json")]
        [InlineData("[\"errors\"]")]
        public void UnknownOrCorrupt_LoadsAsAll(string contents)
        {
            File.WriteAllText(FilePath, contents);
            ReviewLevel level;
            ReviewSource source;
            ReviewViewPrefs.Load(FilePath, out level, out source);
            Assert.Equal(ReviewLevel.All, level);
            Assert.Equal(ReviewSource.RulesAndAi, source);
        }

        [Fact]
        public void OneMissingValue_KeepsTheOther()
        {
            File.WriteAllText(FilePath, "{\"level\":\"suggestions\"}");
            ReviewLevel level;
            ReviewSource source;
            ReviewViewPrefs.Load(FilePath, out level, out source);
            Assert.Equal(ReviewLevel.Suggestions, level);
            Assert.Equal(ReviewSource.RulesAndAi, source);
        }

        [Fact]
        public void FailedWrite_DoesNotThrow()
        {
            // A file where the folder should be: creating the folder fails.
            var blocker = Path.Combine(_dir, "not-a-folder");
            File.WriteAllText(blocker, "x");
            var unwritable = Path.Combine(blocker, "review-view.json");

            var ex = Record.Exception(() => ReviewViewPrefs.Save(unwritable, ReviewLevel.Errors, ReviewSource.RulesOnly));
            Assert.Null(ex);
            Assert.False(File.Exists(unwritable));
        }
    }
}
