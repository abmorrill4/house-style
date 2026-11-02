using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using FlareAI.Core.Actions;
using FlareAI.Core.Logging;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>Keeps each event with the pairs that pass <see cref="LogFields.TryClean"/>, as the file log would write them.</summary>
    public sealed class RecordingLog : ILog
    {
        public sealed class Entry
        {
            public string Level, Event, CommandId;
            public int Thread;
            public Dictionary<string, object> Fields = new Dictionary<string, object>();
        }

        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>The events logged on the calling thread (other tests may log on theirs).</summary>
        public List<Entry> Mine
        {
            get { lock (_gate) return _entries.Where(e => e.Thread == Thread.CurrentThread.ManagedThreadId).ToList(); }
        }

        public void Debug(string eventName, params object[] nameValuePairs) { Add("Debug", eventName, nameValuePairs); }
        public void Info(string eventName, params object[] nameValuePairs) { Add("Info", eventName, nameValuePairs); }
        public void Warn(string eventName, params object[] nameValuePairs) { Add("Warn", eventName, nameValuePairs); }
        public void Error(string eventName, params object[] nameValuePairs) { Add("Error", eventName, nameValuePairs); }

        private void Add(string level, string eventName, object[] pairs)
        {
            var e = new Entry { Level = level, Event = eventName, CommandId = Log.CommandId, Thread = Thread.CurrentThread.ManagedThreadId };
            for (var i = 0; pairs != null && i + 1 < pairs.Length; i += 2)
            {
                object clean;
                if (LogFields.TryClean(pairs[i] as string, pairs[i + 1], out clean)) e.Fields[(string)pairs[i]] = clean;
            }
            lock (_gate) _entries.Add(e);
        }
    }

    public class LoggingTests : IDisposable
    {
        public void Dispose() { Log.Current = NullLog.Instance; }

        [Fact]
        public void Lint_LogsStartAndEndWithSharedCommandId_AndNeverTheTopicText()
        {
            var rec = new RecordingLog();
            Log.Current = rec;
            var runner = new ActionRunner(new FakeLlmClient(r => "[]"), new PromptBuilder(null), 1024, new FakeStyleService());
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            string idDuring;
            using (Log.Command("check"))
            {
                idDuring = Log.CommandId;
                runner.Lint(runner.PickAllBlocks(doc));
            }

            var events = rec.Mine;
            var start = events.Single(e => e.Event == "command_start");
            var end = events.Single(e => e.Event == "command_end");
            var lint = events.Single(e => e.Event == "lint");
            Assert.False(string.IsNullOrEmpty(idDuring));
            Assert.Equal(8, idDuring.Length);
            Assert.Equal("", Log.CommandId);   // restored after the command
            Assert.Equal("check", start.Fields["command"]);
            Assert.Equal("check", end.Fields["command"]);
            Assert.Equal("ok", end.Fields["result"]);
            Assert.True((long)end.Fields["ms"] >= 0);
            Assert.True((int)lint.Fields["findings"] > 0);
            Assert.True((int)lint.Fields["blocks"] > 0);
            Assert.True((long)lint.Fields["ms"] >= 0);
            Assert.Equal("rules", lint.Fields["checks"]);
            Assert.Equal((int)lint.Fields["findings"], (int)lint.Fields["errors"] + (int)lint.Fields["warnings"] + (int)lint.Fields["suggestions"]);

            // The id is the same for every line of the command.
            Assert.Equal(idDuring, start.CommandId);
            Assert.Equal(idDuring, end.CommandId);
            Assert.Equal(idDuring, lint.CommandId);
            var everything = string.Join("\n", events.SelectMany(e => new[] { e.Event }.Concat(e.Fields.Select(f => f.Key + "=" + f.Value))));
            var words = Regex.Split(doc.Document.InnerText, @"\s+").Where(w => w.Length > 0).ToArray();
            for (var i = 0; i + 4 <= words.Length; i++)
                Assert.DoesNotContain(string.Join(" ", words.Skip(i).Take(4).ToArray()), everything);
            Assert.DoesNotContain("utilize", everything);
        }

        [Fact]
        public void Command_FailureIsLoggedAsErrorWithTypeAndNoMessage()
        {
            var rec = new RecordingLog();
            Log.Current = rec;
            using (var scope = (CommandScope)Log.Command("rewrite"))
                scope.Fail(new InvalidOperationException("The user clicks the button now."));

            var events = rec.Mine;
            var error = events.Single(e => e.Event == "error");
            Assert.Equal("InvalidOperationException", error.Fields["error_type"]);
            Assert.Equal("error", events.Single(e => e.Event == "command_end").Fields["result"]);
            Assert.DoesNotContain("clicks the button", string.Join("\n", events.SelectMany(e => e.Fields.Values.Select(v => v.ToString()))));
        }

        [Fact]
        public void LogFields_RefuseOrRedact()
        {
            object clean;
            Assert.False(LogFields.TryClean("key", "sk-ant-abc", out clean));
            Assert.False(LogFields.TryClean("x-api-key", "abc", out clean));
            Assert.False(LogFields.TryClean("header", "Bearer abc", out clean));
            Assert.False(LogFields.TryClean("rule_id", "The user clicks the button now.", out clean));   // a document sentence
            Assert.False(LogFields.TryClean("rule_id", "sk-ant-api03-abcdef", out clean));
            Assert.False(LogFields.TryClean("result", "Bearer", out clean));
            Assert.False(LogFields.TryClean("prompt", "x", out clean));
            Assert.False(LogFields.TryClean("count", null, out clean));
            Assert.False(LogFields.TryClean("count", "3", out clean));

            Assert.True(LogFields.TryClean("count", 3, out clean));
            Assert.Equal(3, clean);
            Assert.True(LogFields.TryClean("key_set", true, out clean));
            Assert.Equal(true, clean);

            Assert.True(LogFields.TryClean("stack", "at A\r\nat B in C:\\Users\\jdoe\\x.cs", out clean));
            var stack = (string)clean;
            Assert.DoesNotContain("\r", stack);
            Assert.DoesNotContain("\n", stack);
            Assert.DoesNotContain("jdoe", stack);

            Assert.True(LogFields.TryClean("topic_hash", @"C:\Users\jdoe\Content\a.htm", out clean));
            Assert.Matches("^[0-9a-f]{12}$", (string)clean);
            Assert.DoesNotContain("jdoe", (string)clean);
            object same;
            LogFields.TryClean("topic_hash", @"c:\users\JDOE\content\A.HTM", out same);
            Assert.Equal(clean, same);   // the hash is of the lower-cased path
        }

        [Fact]
        public void NullLog_AcceptsAllFourCalls()
        {
            ILog log = NullLog.Instance;
            log.Debug("a", "count", 1);
            log.Info("a", "count", 1);
            log.Warn("a");
            log.Error("a", "stack", "x");
            Log.Current = null;
            Assert.Same(NullLog.Instance, Log.Current);
            Log.Caught("test", new Exception("x"));
        }

        private sealed class ThrowingLog : ILog
        {
            public void Debug(string e, params object[] p) { throw new InvalidOperationException(); }
            public void Info(string e, params object[] p) { throw new InvalidOperationException(); }
            public void Warn(string e, params object[] p) { throw new InvalidOperationException(); }
            public void Error(string e, params object[] p) { throw new InvalidOperationException(); }
        }

        [Fact]
        public void ALogThatThrows_NeverReachesTheCommand()
        {
            Log.Current = new ThrowingLog();
            using (Log.Command("check")) { Log.Caught("x", new Exception()); }
            Assert.Equal("", Log.CommandId);
        }
    }
}
