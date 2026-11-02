using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FlareAI.Core.Logging;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>An ILog that keeps what an implementation would write: every field already through the safety filter.</summary>
    public sealed class RecordingLog : ILog
    {
        public sealed class Line
        {
            public string Level, Evt, Cmd;
            public Dictionary<string, object> Fields = new Dictionary<string, object>();
        }

        public readonly List<Line> Lines = new List<Line>();

        public void Debug(string evt, params KeyValuePair<string, object>[] fields) { Add("debug", evt, null, fields); }
        public void Info(string evt, params KeyValuePair<string, object>[] fields) { Add("info", evt, null, fields); }
        public void Warn(string evt, params KeyValuePair<string, object>[] fields) { Add("warn", evt, null, fields); }
        public void Error(string evt, Exception ex, params KeyValuePair<string, object>[] fields) { Add("error", evt, ex, fields); }

        private void Add(string level, string evt, Exception ex, KeyValuePair<string, object>[] fields)
        {
            var line = new Line { Level = level, Evt = evt, Cmd = LogContext.CurrentCommandId };
            foreach (var f in fields ?? new KeyValuePair<string, object>[0])
            {
                object clean;
                if (LogFields.Sanitize(f.Key, f.Value, out clean)) line.Fields[f.Key] = clean;
            }
            if (ex != null)
            {
                line.Fields["error_type"] = ex.GetType().Name;
                line.Fields["stack"] = LogFields.Text("stack", ex.StackTrace ?? "");
            }
            lock (Lines) Lines.Add(line);
        }

        public List<Line> Named(string evt)
        {
            lock (Lines) return Lines.Where(l => l.Evt == evt).ToList();
        }

        /// <summary>Everything recorded, as one string.</summary>
        public string Text()
        {
            var sb = new StringBuilder();
            lock (Lines)
                foreach (var l in Lines)
                {
                    sb.Append("{\"level\":\"").Append(l.Level).Append("\",\"cmd\":\"").Append(l.Cmd).Append("\",\"evt\":\"").Append(l.Evt).Append('"');
                    foreach (var f in l.Fields) sb.Append(",\"").Append(f.Key).Append("\":\"").Append(f.Value).Append('"');
                    sb.AppendLine("}");
                }
            return sb.ToString();
        }
    }

    public class LogTests
    {
        [Fact]
        public void LintTopic_LogsStartEndAndCounts_NeverTheText()
        {
            var doc = new FakeDocumentAdapter("sample-topic.htm");
            var runner = AiCostTests.Runner(AiCostTests.Quiet());
            var log = new RecordingLog();
            Log.Current = log;
            try
            {
                CommandScope.Run("Lint Topic", () => runner.CheckStyle(doc));
            }
            finally { Log.Current = new NullLog(); }

            var start = log.Named("command.start").Single();
            var end = log.Named("command.end").Single();
            Assert.False(string.IsNullOrEmpty(start.Cmd));
            Assert.Equal(start.Cmd, end.Cmd);
            Assert.Equal("ok", end.Fields["result"]);
            Assert.True(end.Fields.ContainsKey("ms"));

            // One topic check runs Lint more than once (the topic, then each snippet), so expect one or more lines.
            var lints = log.Named("lint");
            Assert.NotEmpty(lints);
            foreach (var l in lints) Assert.Equal(start.Cmd, l.Cmd);
            Assert.Contains(lints, l => l.Fields.ContainsKey("findings") && l.Fields.ContainsKey("blocks") && l.Fields.ContainsKey("ms"));
            Assert.Single(log.Named("lint.topic"));

            var all = log.Text();
            var sentences = doc.Document.DocumentElement.InnerText
                .Split(new[] { '.', '\r', '\n', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 20).ToList();
            Assert.NotEmpty(sentences);
            foreach (var s in sentences) Assert.DoesNotContain(s, all);
        }

        [Fact]
        public void Fields_WithKeysHeadersOrSentences_AreRefusedOrRedacted()
        {
            object clean;
            foreach (var name in new[] { "key", "header", "text", "prompt", "reply", "not_on_the_list" })
                Assert.False(LogFields.Sanitize(name, "value", out clean), name);

            Assert.True(LogFields.Sanitize("path", "see sk-ant-api03-abcdef0123456789abcdef here", out clean));
            Assert.DoesNotContain("abcdef0123456789", (string)clean);
            Assert.Contains("[removed]", (string)clean);

            Assert.True(LogFields.Sanitize("path", "Bearer abcdef0123456789abcdef", out clean));
            Assert.DoesNotContain("abcdef0123456789", (string)clean);

            Assert.True(LogFields.Sanitize("path", "x-api-key: abcdef0123456789", out clean));
            Assert.DoesNotContain("abcdef0123456789", (string)clean);
            Assert.Contains("[removed]", (string)clean);

            Assert.True(LogFields.Sanitize("path", "one\r\ntwo\nthree", out clean));
            Assert.DoesNotContain("\r", (string)clean);
            Assert.DoesNotContain("\n", (string)clean);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.True(LogFields.Sanitize("path", home + @"\Docs\a.htm", out clean));
            Assert.StartsWith("%USERPROFILE%", (string)clean);
            Assert.DoesNotContain(home, (string)clean, StringComparison.OrdinalIgnoreCase);

            var sentence = string.Join(" ", Enumerable.Repeat("This is a sentence from a topic.", 13));
            Assert.True(sentence.Length > 300);
            Assert.True(LogFields.Sanitize("path", sentence, out clean));
            Assert.Equal("[too long]", clean);

            Assert.Equal("key set: yes", LogFields.KeySet(true));
            Assert.Equal("key set: no", LogFields.KeySet(false));
            Assert.Equal(12, LogFields.TopicHash(@"C:\Docs\A.htm").Length);
            Assert.Equal(LogFields.TopicHash(@"C:\Docs\A.htm"), LogFields.TopicHash(@"c:\docs\a.HTM"));
        }

        [Fact]
        public void NullLog_DoesNothingAndNeverThrows()
        {
            var log = new NullLog();
            log.Debug("a.b", F.Of("ms", 1));
            log.Info("a.b");
            log.Warn("a.b", F.Of("key", "secret"));
            log.Error("a.b", new InvalidOperationException("x"), F.Of("ms", 1));
            Log.Current = null;   // never null
            Assert.NotNull(Log.Current);
            Log.Current.Info("a.b");
        }

        [Fact]
        public void CommandScope_WhenBodyThrows_LogsErrorAndRethrows()
        {
            var log = new RecordingLog();
            Log.Current = log;
            try
            {
                Assert.Throws<InvalidOperationException>(() => CommandScope.Run<int>("Boom", () => { throw new InvalidOperationException("secret text"); }));
            }
            finally { Log.Current = new NullLog(); }

            var err = log.Named("command.error").Single();
            Assert.Equal("InvalidOperationException", err.Fields["error_type"]);
            Assert.DoesNotContain("secret text", log.Text());
            Assert.Equal("error", log.Named("command.end").Single().Fields["result"]);
            Assert.Null(LogContext.CurrentCommandId);
        }
    }
}
