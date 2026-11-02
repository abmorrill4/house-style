using System.IO;
using System.IO.Compression;
using System.Text;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class NoteTextTests
    {
        private const string Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        private static byte[] Docx(string bodyXml)
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var e = z.CreateEntry("[Content_Types].xml");
                using (var w = new StreamWriter(e.Open())) w.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
                e = z.CreateEntry("word/document.xml");
                using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                    w.Write("<w:document xmlns:w=\"" + Word + "\"><w:body>" + bodyXml + "</w:body></w:document>");
            }
            return ms.ToArray();
        }

        [Fact]
        public void Docx_paragraphs_runs_tabs_and_table_cells_become_lines()
        {
            var text = NoteText.FromDocx(Docx(
                "<w:p><w:r><w:t>Never say </w:t></w:r><w:r><w:t>utilize</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t>Use</w:t><w:tab/><w:t>use</w:t></w:r></w:p>" +
                "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>A &amp; B</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>C</w:t></w:r></w:p></w:tc></w:tr></w:tbl>"));
            Assert.Equal("Never say utilize\nUse\tuse\nA & B\nC", text);
        }

        [Fact]
        public void Docx_without_document_xml_is_a_friendly_error()
        {
            var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) { using (var w = new StreamWriter(z.CreateEntry("other.txt").Open())) w.Write("x"); }
            Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(ms.ToArray()));
            Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(new byte[] { 1, 2, 3 }));
        }

        private static byte[] BigDocx()
        {
            var unit = "<w:p><w:r><w:t>x</w:t></w:r></w:p>";
            var sb = new StringBuilder();
            while (sb.Length <= 21 * 1024 * 1024) sb.Append(unit);
            return Docx(sb.ToString());
        }

        // Start of the central directory entry for word/document.xml.
        private static int CentralEntry(byte[] z)
        {
            var name = Encoding.ASCII.GetBytes("word/document.xml");
            for (var i = 0; i + 46 + name.Length <= z.Length; i++)
            {
                if (z[i] != 0x50 || z[i + 1] != 0x4B || z[i + 2] != 1 || z[i + 3] != 2) continue;
                var ok = true;
                for (var k = 0; k < name.Length && ok; k++) ok = z[i + 46 + k] == name[k];
                if (ok) return i;
            }
            throw new System.InvalidOperationException("entry not found");
        }

        private static int EndRecord(byte[] z)
        {
            for (var i = z.Length - 22; i >= 0; i--)
                if (z[i] == 0x50 && z[i + 1] == 0x4B && z[i + 2] == 5 && z[i + 3] == 6) return i;
            throw new System.InvalidOperationException("end record not found");
        }

        private static void Put32(byte[] z, int at, uint v)
        {
            z[at] = (byte)v; z[at + 1] = (byte)(v >> 8); z[at + 2] = (byte)(v >> 16); z[at + 3] = (byte)(v >> 24);
        }

        private static void Put16(byte[] z, int at, int v) { z[at] = (byte)v; z[at + 1] = (byte)(v >> 8); }

        [Fact]
        public void Docx_larger_than_the_cap_is_refused_and_names_the_limit()
        {
            var ex = Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(BigDocx()));
            Assert.Contains("20 MB", ex.Message);
        }

        [Fact]
        public void Docx_that_lies_about_its_size_is_still_capped()
        {
            var z = BigDocx();
            Put32(z, CentralEntry(z) + 24, 1000);
            Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(z));
        }

        [Fact]
        public void Docx_with_a_huge_declared_size_does_not_preallocate()
        {
            var z = Docx("<w:p><w:r><w:t>Hello</w:t></w:r></w:p>");
            Put32(z, CentralEntry(z) + 24, 0x7FFFFFF0);
            var ex = Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(z));
            Assert.Contains("20 MB", ex.Message);
        }

        [Fact]
        public void Docx_with_offsets_outside_the_file_is_a_friendly_error()
        {
            var good = Docx("<w:p><w:r><w:t>Hello</w:t></w:r></w:p>");

            var a = (byte[])good.Clone();
            Put32(a, EndRecord(a) + 16, 0xFFFFFFF0);
            Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(a));

            var b = (byte[])good.Clone();
            Put32(b, CentralEntry(b) + 42, 0xFFFFFF00);
            Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(b));

            var c = (byte[])good.Clone();
            Put32(c, CentralEntry(c) + 20, 0x7FFFFFFF);
            Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(c));

            var d = (byte[])good.Clone();
            Put16(d, CentralEntry(d) + 28, 0xFFFF);
            Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(d));
        }

        [Fact]
        public void Docx_truncated_anywhere_never_throws_anything_but_InvalidDataException()
        {
            var good = Docx("<w:p><w:r><w:t>Hello</w:t></w:r></w:p>");
            for (var len = 0; len <= good.Length; len += 7)
            {
                var cut = new byte[len];
                System.Array.Copy(good, cut, len);
                try { NoteText.FromDocx(cut); }
                catch (InvalidDataException) { }
                catch (System.Exception ex) { Assert.True(false, "Length " + len + " threw " + ex.GetType().Name + ": " + ex.Message); }
            }
        }

        [Fact]
        public void ReadFile_reads_a_docx_that_another_program_holds_open()
        {
            var path = Path.Combine(Path.GetTempPath(), "notes-" + System.Guid.NewGuid().ToString("N") + ".docx");
            File.WriteAllBytes(path, Docx("<w:p><w:r><w:t>Hello</w:t></w:r></w:p>"));
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) // like Word
                    Assert.Equal("Hello", NoteText.ReadFile(path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Html_drops_style_script_conditional_comments_and_op_tags()
        {
            var html = "<html><head><title>T</title><style>p {color:red}</style><script>var a=1;</script>" +
                       "<!--[if gte mso 9]><xml><o:x>junk</o:x></xml><![endif]--></head><body>" +
                       "<p class=MsoNormal>Use &ldquo;sign in&rdquo;,&nbsp;not <b>log\r\n in</b>.<o:p></o:p></p>" +
                       "<!--[if !supportLists]--><span>1.</span><!--[endif]-->" +
                       "<ul><li>One &amp; two</li><li>Three</li></ul>" +
                       "<table><tr><td>Cell A</td><td>Cell B</td></tr></table></body></html>";
            var text = NoteText.CleanHtml(html);
            Assert.DoesNotContain("color:red", text);
            Assert.DoesNotContain("var a", text);
            Assert.DoesNotContain("junk", text);
            Assert.DoesNotContain("<", text);
            Assert.StartsWith("Use", text);
            Assert.Contains("Use “sign in”, not log in.", text);
            var lines = text.Split('\n');
            Assert.Contains("One & two", lines);
            Assert.Contains("Three", lines);
            Assert.Contains("Cell A", lines);
            Assert.Contains("Cell B", lines);
        }

        [Fact]
        public void Html_empty_input_is_empty() { Assert.Equal("", NoteText.CleanHtml(null)); }
    }
}
