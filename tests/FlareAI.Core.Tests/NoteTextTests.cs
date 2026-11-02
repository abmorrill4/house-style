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

        private const string HelloBody = "<w:p><w:r><w:t>Hello</w:t></w:r></w:p>";

        private static void Put(byte[] b, int at, uint value, int size)
        {
            for (var i = 0; i < size; i++) b[at + i] = (byte)(value >> (8 * i));
        }

        private static int FindCentralEntry(byte[] z, string name)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            for (var i = 0; i + 46 + nameBytes.Length <= z.Length; i++)
            {
                if (z[i] != 0x50 || z[i + 1] != 0x4B || z[i + 2] != 1 || z[i + 3] != 2) continue;
                var ok = true;
                for (var k = 0; k < nameBytes.Length && ok; k++) ok = z[i + 46 + k] == nameBytes[k];
                if (ok) return i;
            }
            throw new System.InvalidOperationException("entry not found");
        }

        private static int FindEocd(byte[] z)
        {
            for (var i = z.Length - 22; i >= 0; i--)
                if (z[i] == 0x50 && z[i + 1] == 0x4B && z[i + 2] == 5 && z[i + 3] == 6) return i;
            throw new System.InvalidOperationException("eocd not found");
        }

        private static byte[] Hello() { return Docx(HelloBody); }

        private static void AssertFriendly(byte[] z)
        {
            var ex = Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(z));
            Assert.False(string.IsNullOrEmpty(ex.Message));
        }

        [Fact]
        public void Docx_over_the_size_limit_is_refused_and_names_the_limit()
        {
            var para = "<w:p><w:r><w:t>" + new string('a', 1000) + "</w:t></w:r></w:p>";
            var sb = new StringBuilder(22000 * para.Length);
            for (var i = 0; i < 22000; i++) sb.Append(para); // about 22 MB of XML
            var ex = Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(Docx(sb.ToString())));
            Assert.Contains("20 MB", ex.Message);
        }

        [Fact]
        public void Docx_with_a_huge_declared_size_but_small_content_does_not_overallocate()
        {
            var z = Hello();
            Put(z, FindCentralEntry(z, "word/document.xml") + 24, 0x7FFFFFFF, 4);
            var ex = Assert.Throws<InvalidDataException>(() => NoteText.FromDocx(z));
            Assert.Contains("20 MB", ex.Message);

            var z2 = Hello();
            Put(z2, FindCentralEntry(z2, "word/document.xml") + 24, 10u * 1024 * 1024, 4);
            Assert.Equal("Hello", NoteText.FromDocx(z2));
        }

        [Fact]
        public void Docx_with_central_directory_offset_past_the_end_is_a_friendly_error()
        {
            var z = Hello();
            Put(z, FindEocd(z) + 16, 0xFFFFFFF0, 4);
            AssertFriendly(z);
        }

        [Fact]
        public void Docx_with_local_header_offset_past_the_end_is_a_friendly_error()
        {
            var z = Hello();
            Put(z, FindCentralEntry(z, "word/document.xml") + 42, 0xFFFFFFF0, 4);
            AssertFriendly(z);
        }

        [Fact]
        public void Docx_with_compressed_size_past_the_end_is_a_friendly_error()
        {
            var z = Hello();
            Put(z, FindCentralEntry(z, "word/document.xml") + 20, 0xFFFFFF00, 4);
            AssertFriendly(z);
        }

        [Fact]
        public void Docx_with_name_length_past_the_end_is_a_friendly_error()
        {
            var z = Hello();
            Put(z, FindCentralEntry(z, "word/document.xml") + 28, 0xFFFF, 2);
            AssertFriendly(z);
        }

        [Fact]
        public void Docx_with_corrupt_deflate_data_is_a_friendly_error()
        {
            var z = Hello();
            var cd = FindCentralEntry(z, "word/document.xml");
            var lh = (int)System.BitConverter.ToUInt32(z, cd + 42);
            var data = lh + 30 + System.BitConverter.ToUInt16(z, lh + 26) + System.BitConverter.ToUInt16(z, lh + 28);
            for (var i = 0; i < 4; i++) z[data + i] = 0xFF; // reserved block type
            AssertFriendly(z);
        }

        [Fact]
        public void Docx_truncated_zip_is_a_friendly_error()
        {
            var z = Hello();
            var half = new byte[z.Length / 2];
            System.Array.Copy(z, half, half.Length);
            AssertFriendly(half);
        }

        [Fact]
        public void Docx_null_input_is_a_friendly_error() { AssertFriendly(null); }
    }
}
