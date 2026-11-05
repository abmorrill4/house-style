using System.Collections.Generic;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Llm;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>Each extracted rule remembers the pasted text or file it came from.</summary>
    public class NoteSourceTests
    {
        private static string Rule(string avoid)
        {
            return "{\"kind\":\"row\",\"title\":\"Avoid " + avoid + "\",\"avoid\":\"" + avoid + "\",\"use\":\"\",\"severity\":\"warning\",\"why\":\"Our notes say so.\",\"example\":\"We " + avoid + " it.\",\"text\":\"\",\"bad\":\"\",\"good\":\"\",\"quote\":\"" + avoid + "\"}";
        }

        private static ActionRunner Runner()
        {
            // Every source says to avoid "utilize"; each also has one rule of its own.
            var llm = new FakeLlmClient(r =>
                "{\"rules\":[" + Rule("utilize") + "," +
                Rule(r.User.Contains("guide.pdf") ? "leverage" : r.User.Contains("alpha") ? "synergy" : "paradigm") + "]}");
            return new ActionRunner(llm, new PromptBuilder(null), 1024);
        }

        [Fact]
        public void EveryRuleNamesItsSource_AndARepeatStaysUnderTheFirst()
        {
            var found = Runner().ExtractRules(new List<NoteSource>
            {
                new NoteSource { Name = "Pasted text", Text = "alpha notes" },
                new NoteSource { Name = "old-guide.docx", Text = "beta notes" },
                new NoteSource { Name = "guide.pdf", Pdf = new byte[] { 1, 2, 3 } }
            });

            Assert.Equal(4, found.Count);   // utilize once, plus one of its own from each source
            Assert.Equal("Pasted text", found.Single(c => c.Avoid == "utilize").Source);
            Assert.Equal("Pasted text", found.Single(c => c.Avoid == "synergy").Source);
            Assert.Equal("old-guide.docx", found.Single(c => c.Avoid == "paradigm").Source);
            Assert.Equal("guide.pdf", found.Single(c => c.Avoid == "leverage").Source);
        }

        [Fact]
        public void ASourceWithNoTextIsSkipped()
        {
            Assert.Empty(Runner().ExtractRules(new List<NoteSource> { new NoteSource { Name = "empty.txt", Text = "  " } }));
        }
    }
}
