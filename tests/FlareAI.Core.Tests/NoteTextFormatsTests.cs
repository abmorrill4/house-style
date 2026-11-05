using System.IO;
using System.IO.Compression;
using System.Text;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>The other file kinds a team's old style guide may be in: Excel, PowerPoint, OpenDocument and Rich Text. PDF gets a clear message.</summary>
    public class NoteTextFormatsTests
    {
        private static byte[] Zip(params string[][] entries)
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var e in entries)
                    using (var w = new StreamWriter(z.CreateEntry(e[0]).Open(), new UTF8Encoding(false))) w.Write(e[1]);
            return ms.ToArray();
        }

        private const string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";

        private static byte[] Xlsx()
        {
            return Zip(
                new[] { "xl/workbook.xml", "<workbook " + S + "/>" },
                new[] { "xl/sharedStrings.xml", "<sst " + S + "><si><t>Avoid</t></si><si><t>Use</t></si><si><t>utilize</t></si><si><t>use</t></si><si><r><t>sign </t></r><r><t>in</t></r></si></sst>" },
                new[] { "xl/worksheets/sheet1.xml", "<worksheet " + S + "><sheetData>" +
                    "<row><c t=\"s\"><v>0</v></c><c t=\"s\"><v>1</v></c></row>" +
                    "<row><c t=\"s\"><v>2</v></c><c t=\"s\"><v>3</v></c></row>" +
                    "<row><c t=\"s\"><v>4</v></c><c><v>42</v></c><c t=\"inlineStr\"><is><t>inline</t></is></c></row></sheetData></worksheet>" });
        }

        [Fact]
        public void Excel_rows_become_lines_with_cells_separated_by_bars()
        {
            Assert.Equal("Avoid | Use\nutilize | use\nsign in | 42 | inline", NoteText.FromXlsx(Xlsx()));
        }

        [Fact]
        public void ReadFile_recognises_each_package_by_what_is_inside_it_not_its_extension()
        {
            var path = Path.Combine(Path.GetTempPath(), "notes-" + System.Guid.NewGuid().ToString("N") + ".dat");
            File.WriteAllBytes(path, Xlsx());
            try { Assert.StartsWith("Avoid | Use", NoteText.ReadFile(path)); }
            finally { File.Delete(path); }
        }

        [Fact]
        public void PowerPoint_slide_text_is_read_one_paragraph_per_line()
        {
            const string A = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
            var pptx = Zip(
                new[] { "ppt/presentation.xml", "<p/>" },
                new[] { "ppt/slides/slide1.xml", "<s " + A + "><a:p><a:r><a:t>Write </a:t></a:r><a:r><a:t>short sentences</a:t></a:r></a:p><a:p><a:r><a:t>Use active voice</a:t></a:r></a:p></s>" },
                new[] { "ppt/slides/slide2.xml", "<s " + A + "><a:p><a:r><a:t>Second slide</a:t></a:r></a:p></s>" });
            Assert.Equal("Write short sentences\nUse active voice\n\nSecond slide", NoteText.FromPptx(pptx));
        }

        [Fact]
        public void OpenDocument_paragraphs_headings_and_spaces_are_read()
        {
            const string T = "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
            var odt = Zip(new[] { "content.xml", "<doc " + T + "><text:h>Voice</text:h><text:p>Use<text:s/>active voice.</text:p><text:p>Avoid<text:tab/>passive.</text:p></doc>" });
            Assert.Equal("Voice\nUse active voice.\nAvoid\tpassive.", NoteText.FromOdt(odt));
        }

        [Fact]
        public void RichText_keeps_paragraphs_and_text_and_drops_fonts_and_styles()
        {
            var rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red0\\green0\\blue0;}\\f0\\fs24 Never say {\\b utilize}.\\par Use \\'e9 and \\u8364? and a \\{brace\\}.\\par}";
            Assert.Equal("Never say utilize.\nUse é and € and a {brace}.", NoteText.FromRtf(rtf));
        }

        [Fact]
        public void APdf_GetsAClearMessageThatSaysWhatToDoInstead()
        {
            var path = Path.Combine(Path.GetTempPath(), "notes-" + System.Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n"));
            try
            {
                var ex = Assert.Throws<InvalidDataException>(() => NoteText.ReadFile(path));
                Assert.Contains("PDF", ex.Message);
                Assert.Contains("paste", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ARuleWordedDifferentlyOnASecondImport_IsTheSameRule_ButADifferentRuleIsNot()
        {
            var row1 = new RuleCandidate { Avoid = "it is recommended that", Use = "(flag only)", Title = "Avoid \"it is recommended that\"" };
            var row2 = new RuleCandidate { Avoid = "It is recommended that", Use = "", Title = "It is recommended that" };
            Assert.True(RuleCandidate.SameRule(row1, row2));
            Assert.False(RuleCandidate.SameRule(row1, new RuleCandidate { Avoid = "please note that", Use = "" }));

            var p1 = new RuleCandidate { Title = "Spell out numbers below ten", Text = "Spell out numbers below ten." };
            var p2 = new RuleCandidate { Title = "Spell out numbers below ten", Text = "Always spell out a number below ten, and use digits from 10 up." };
            var p3 = new RuleCandidate { Title = "Write numbers under ten as words", Text = "Spell out any number below ten." };
            Assert.True(RuleCandidate.SameRule(p1, p2));      // the same title
            Assert.True(RuleCandidate.SameRule(p1, p3));      // reworded, most of the meaningful words shared
            Assert.False(RuleCandidate.SameRule(p1, new RuleCandidate { Title = "Keep sentences under 25 words", Text = "Keep every sentence under 25 words." }));
            Assert.False(RuleCandidate.SameRule(p1, row1));   // a row is never a prose rule
        }

        [Fact]
        public void AnUnknownPackage_NamesTheKindsHouseStyleReads()
        {
            var ex = Assert.Throws<InvalidDataException>(() => NoteText.FromPackage(Zip(new[] { "other.bin", "x" })));
            Assert.Contains("Excel", ex.Message);
            Assert.Contains(".rtf", ex.Message);
        }
    }
}
