using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core;
using FlareAI.Core.Logging;
using FlareAI.Flare.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FlareAI.Flare.Tests
{
    // Touches the static Log.Current and Paths.Root, so it shares the "Panes" collection with the other tests that do.
    [Collection("Panes")]
    public class SerilogLogTests
    {
        private static string TempFolder()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hs-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static List<JObject> Read(string folder)
        {
            return Directory.GetFiles(folder, "*.jsonl")
                .SelectMany(f => File.ReadAllLines(f)).Where(l => l.Length > 0).Select(l => JObject.Parse(l)).ToList();
        }

        [Fact]
        public void LogFile_RollsAtItsSize_AndKeepsOnlyTheNewest()
        {
            var folder = TempFolder();
            try
            {
                var padding = new string('x', 40);
                using (var log = new SerilogLog(folder, true, 2048, 3))
                {
                    for (var i = 0; i < 200; i++) log.Info("test.line", F.Of("ms", i), F.Of("path", padding));
                }

                var files = Directory.GetFiles(folder, "*.jsonl");
                Assert.True(files.Length > 1, "the file never rolled");
                Assert.True(files.Length <= 3, files.Length + " files kept");
                var longest = Read(folder).Max(o => o.ToString(Newtonsoft.Json.Formatting.None).Length) + 1;
                foreach (var f in files) Assert.True(new FileInfo(f).Length <= 2048 + longest, f + " is " + new FileInfo(f).Length + " bytes");

                var lines = Read(folder);
                foreach (var o in lines)
                    foreach (var name in new[] { "t", "level", "cmd", "evt", "plugin", "flare", "rules" })
                        Assert.True(o.Property(name) != null, "missing " + name);
                Assert.Contains(lines, o => (int?)o["ms"] == 199);
                Assert.DoesNotContain(lines, o => (int?)o["ms"] == 0);
            }
            finally { try { Directory.Delete(folder, true); } catch (Exception) { } }
        }

        [Fact]
        public void LogCall_WhenFolderCannotBeWritten_DoesNotThrow()
        {
            var folder = TempFolder();
            var notAFolder = Path.Combine(folder, "file.txt");
            File.WriteAllText(notAFolder, "x");
            try
            {
                using (var log = new SerilogLog(notAFolder, true))
                {
                    log.Info("test.line", F.Of("ms", 1));
                    log.Error("test.error", new InvalidOperationException("x"));
                    log.SetDebug(false);
                }
            }
            finally { try { Directory.Delete(folder, true); } catch (Exception) { } }
        }

        [Fact]
        public void Info_Level_DropsDebugLines()
        {
            var folder = TempFolder();
            try
            {
                using (var log = new SerilogLog(folder, false))
                {
                    log.Debug("test.debug", F.Of("ms", 1));
                    log.Info("test.info", F.Of("ms", 2));
                    log.SetDebug(true);
                    log.Debug("test.debug2", F.Of("ms", 3));
                }
                var events = Read(folder).Select(o => (string)o["evt"]).ToList();
                Assert.DoesNotContain("test.debug", events);
                Assert.Contains("test.info", events);
                Assert.Contains("test.debug2", events);
            }
            finally { try { Directory.Delete(folder, true); } catch (Exception) { } }
        }

        [Fact]
        public void Lines_NeverHoldRefusedFieldsOrErrorMessages()
        {
            var folder = TempFolder();
            try
            {
                using (var log = new SerilogLog(folder, true))
                {
                    log.Info("test.line", F.Of("text", "A sentence from a topic."), F.Of("key", "sk-ant-api03-abcdef0123456789abcdef"));
                    log.Error("test.error", new InvalidOperationException("A sentence from a topic."));
                }
                var all = string.Join("\n", Directory.GetFiles(folder, "*.jsonl").Select(File.ReadAllText));
                Assert.DoesNotContain("A sentence from a topic.", all);
                Assert.DoesNotContain("abcdef0123456789", all);
                Assert.Contains("InvalidOperationException", all);
            }
            finally { try { Directory.Delete(folder, true); } catch (Exception) { } }
        }

        [Fact]
        public void CrashTrail_MarkerIsWrittenAtStartAndRemovedAtStop()
        {
            var folder = TempFolder();
            var before = Paths.IsRedirected ? Paths.Root : null;
            try
            {
                Paths.Root = folder;
                Assert.False(File.Exists(Paths.CrashMarker));
                CrashTrail.Start();
                Assert.True(File.Exists(Paths.CrashMarker));
                CrashTrail.Stop();
                Assert.False(File.Exists(Paths.CrashMarker));
            }
            finally
            {
                Paths.Root = before;
                try { Directory.Delete(folder, true); } catch (Exception) { }
            }
        }
    }
}
