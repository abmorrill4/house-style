using System;
using System.IO;
using System.Linq;
using FlareAI.Core.Output;
using FlareAI.Core.Update;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class HardeningTests
    {
        private static string NewDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "hs-hard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        [Fact]
        public void Llm_export_front_matter_title_has_no_carriage_return()
        {
            var output = Path.Combine(NewDir(), "HTML5");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "a.htm"),
                "<html><head><title>First\r\nSecond\rThird</title></head><body><h1>x</h1><p>This paragraph is long enough to be kept as a topic.</p></body></html>");
            var result = LlmExport.Export(output);
            var md = Directory.GetFiles(result.Markdown, "*.md", SearchOption.AllDirectories).Single();
            var text = File.ReadAllText(md);
            var front = text.Substring(0, text.IndexOf("\n---\n", 4, StringComparison.Ordinal));
            Assert.DoesNotContain("\r", front);
            Assert.Contains("title: ", front);
        }

        [Fact]
        public void Junction_refuses_paths_that_cmd_would_expand()
        {
            var dir = NewDir();
            var target = Path.Combine(dir, "t");
            Directory.CreateDirectory(target);
            Assert.Throws<IOException>(() => Junction.Create(Path.Combine(dir, "a%PATH%b"), target));
            Assert.Throws<IOException>(() => Junction.Create(Path.Combine(dir, "ok"), target + "\" & echo x & \""));
            Assert.False(Directory.Exists(Path.Combine(dir, "ok")));
        }
    }
}
