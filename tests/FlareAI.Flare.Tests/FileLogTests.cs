using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Logging;
using FlareAI.Flare.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FlareAI.Flare.Tests
{
    public class FileLogTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-log-" + Guid.NewGuid().ToString("N"));

        public FileLogTests() { Directory.CreateDirectory(_root); }

        public void Dispose()
        {
            Log.Current = NullLog.Instance;
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string Folder(string name) { return Path.Combine(_root, name); }

        private static string[] LogFiles(string folder)
        {
            return Directory.GetFiles(folder, "housestyle*.log");
        }

        [Fact]
        public void TheFileRollsAtItsSize_AndKeepsOnlyTheNewest()
        {
            var folder = Folder("roll");
            var log = FileLog.Create(folder, LogLevel.Info, "1.2.3", "22.0.1", maxBytes: 2000, keep: 3);
            for (var i = 0; i < 200; i++) log.Info("lint", "count", i, "result", "ok", "rule_id", "plain-words.utilize");
            log.Dispose();

            var files = LogFiles(folder);
            Assert.True(files.Length > 1, "files: " + files.Length);
            Assert.True(files.Length <= 3, "files: " + files.Length);
            var lines = files.SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToList();
            Assert.NotEmpty(lines);
            foreach (var line in lines)
            {
                var o = JObject.Parse(line);
                Assert.NotNull(o["@t"]);
                Assert.NotNull(o["command_id"]);
                Assert.Equal("1.2.3", (string)o["plugin_version"]);
                Assert.Equal("22.0.1", (string)o["flare_version"]);
                Assert.NotNull(o["rule_set_version"]);
                Assert.Equal("lint", (string)o["event"]);
            }
            Assert.Contains(lines, l => (int?)JObject.Parse(l)["count"] == 199);   // the newest line survived
            Assert.DoesNotContain(lines, l => (int?)JObject.Parse(l)["count"] == 0);   // the oldest did not
        }

        [Fact]
        public void ADebugLine_IsLeftOutAtInfo_AndWrittenAtDebug()
        {
            var info = Folder("info"); var debug = Folder("debug");
            var a = FileLog.Create(info, LogLevel.Info, "1.0.0", "22.0.1");
            a.Debug("rule_override", "rule_id", "plain-words.utilize");
            a.Info("lint", "count", 1);
            a.Dispose();
            var b = FileLog.Create(debug, LogLevel.Debug, "1.0.0", "22.0.1");
            b.Debug("rule_override", "rule_id", "plain-words.utilize");
            b.Info("lint", "count", 1);
            b.Dispose();

            var infoText = string.Join("\n", LogFiles(info).Select(File.ReadAllText));
            var debugText = string.Join("\n", LogFiles(debug).Select(File.ReadAllText));
            Assert.DoesNotContain("rule_override", infoText);
            Assert.Contains("lint", infoText);
            Assert.Contains("rule_override", debugText);
        }

        [Fact]
        public void ValuesThatAreNotAllowed_AreNeverWritten()
        {
            var folder = Folder("refuse");
            var log = FileLog.Create(folder, LogLevel.Debug, "1.0.0", "22.0.1");
            log.Info("ai_call", "prompt", "Click the button now.", "rule_id", "The user clicks the button now.", "key", "sk-ant-abc", "header", "Bearer abc",
                     "stack", "at A in C:\\Users\\jdoe\\x.cs\r\nat B", "count", 5);
            log.Dispose();

            var text = string.Join("\n", LogFiles(folder).Select(File.ReadAllText));
            Assert.DoesNotContain("button", text);
            Assert.DoesNotContain("sk-ant", text);
            Assert.DoesNotContain("Bearer", text);
            Assert.DoesNotContain("jdoe", text);
            Assert.Contains("\"count\":5", text);
        }

        [Fact]
        public void AFolderThatCantBeWritten_NeverThrows()
        {
            var file = Path.Combine(_root, "afile");
            File.WriteAllText(file, "x");
            var log = FileLog.Create(Path.Combine(file, "logs"), LogLevel.Debug, "1.0.0", "22.0.1");
            log.Debug("a", "count", 1);
            log.Info("a", "count", 1);
            log.Warn("a");
            log.Error("a", "stack", "x");
            log.Dispose();
        }

        [Fact]
        public void TheZipHoldsExactlyTheFilesTheDialogLists()
        {
            var folder = Folder("bundle");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "housestyle-.log"), "{}\n");
            File.WriteAllText(Path.Combine(folder, "housestyle-_001.log"), "{}\n");
            File.WriteAllText(Path.Combine(folder, CrashTrail.MarkerName), "2026-11-01T00:00:00Z pid 1");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not part of the zip");
            var zip = Path.Combine(_root, "out", "diag.zip");

            // The newest log is still open for writing, as it is while Flare runs.
            using (new FileStream(Path.Combine(folder, "housestyle-.log"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                DiagnosticsBundle.Build(folder, zip);

            var expected = DiagnosticsBundle.Files(folder);
            Assert.Equal(3, expected.Count);
            Assert.DoesNotContain("notes.txt", expected);
            Assert.Equal(expected.OrderBy(n => n).ToArray(), EntryNames(zip).OrderBy(n => n).ToArray());
        }

        [Fact]
        public void TheCrashMarker_IsWrittenAtStart_AndRemovedOnACleanExit()
        {
            var folder = Folder("crash");
            CrashTrail.Start(folder);
            Assert.True(File.Exists(Path.Combine(folder, CrashTrail.MarkerName)));
            CrashTrail.CleanExit();
            Assert.False(File.Exists(Path.Combine(folder, CrashTrail.MarkerName)));
        }

        /// <summary>The entry names, read from the zip's central directory (the test project doesn't reference System.IO.Compression).</summary>
        private static List<string> EntryNames(string zipPath)
        {
            var bytes = File.ReadAllBytes(zipPath);
            var end = -1;
            for (var i = bytes.Length - 22; i >= 0; i--)
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x05 && bytes[i + 3] == 0x06) { end = i; break; }
            Assert.True(end >= 0, "no end of central directory");
            int count = BitConverter.ToUInt16(bytes, end + 10);
            int pos = (int)BitConverter.ToUInt32(bytes, end + 16);
            var names = new List<string>();
            for (var n = 0; n < count; n++)
            {
                Assert.Equal(0x02014b50u, BitConverter.ToUInt32(bytes, pos));
                int nameLen = BitConverter.ToUInt16(bytes, pos + 28), extraLen = BitConverter.ToUInt16(bytes, pos + 30), commentLen = BitConverter.ToUInt16(bytes, pos + 32);
                names.Add(System.Text.Encoding.UTF8.GetString(bytes, pos + 46, nameLen));
                pos += 46 + nameLen + extraLen + commentLen;
            }
            return names;
        }
    }
}
