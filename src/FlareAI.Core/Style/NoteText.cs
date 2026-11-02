using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FlareAI.Core.Style
{
    /// <summary>
    /// Plain text from the files an owner drops into "Add notes or an old guide": .txt/.md as is, Word "Web Page"
    /// HTML (.htm/.html) cleaned of styles, scripts and Office markup, and .docx read straight from the package.
    /// Throws IOException, UnauthorizedAccessException or InvalidDataException with a message fit to show.
    /// </summary>
    public static class NoteText
    {
        private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const int MaxDocumentBytes = 20 * 1024 * 1024;

        /// <summary>Reads a notes file (shared read, so a copy open in Word still loads).</summary>
        public static string ReadFile(string path)
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                var buf = new byte[81920]; int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                bytes = ms.ToArray();
            }
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".docx" || (bytes.Length > 3 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4))
                return FromDocx(bytes);
            if (bytes.Length > 7 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0)
                throw new InvalidDataException("This is an older binary Word file (.doc) or a protected file. Save it as .docx or as a Web Page (.htm) in Word, then load that.");
            var text = DecodeText(bytes);
            return ext == ".htm" || ext == ".html" ? CleanHtml(text) : text;
        }

        private static string DecodeText(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (ArgumentException) { return Encoding.Default.GetString(b); } // Word "Web Page" files are often windows-1252
        }

        /// <summary>Text of a Word HTML export: no style/script/conditional-comment/o:p noise, entities decoded, block ends kept as newlines.</summary>
        public static string CleanHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var t = Regex.Replace(html, @"<!--\s*\[if\b.*?<!\s*\[endif\]\s*-->", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase); // MSO conditional blocks
            t = Regex.Replace(t, @"<!--.*?-->", " ", RegexOptions.Singleline);
            t = Regex.Replace(t, @"<!\[(if|endif)\b[^>]*>", " ", RegexOptions.IgnoreCase); // downlevel-revealed: keep content, drop markers
            t = Regex.Replace(t, @"<(style|script|title|xml)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s+", " "); // source line wraps are not breaks
            t = Regex.Replace(t, @"<br\b[^>]*>|</?(p|div|li|tr|td|th|h[1-6]|ul|ol|table|blockquote|pre)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<[^>]*>", "");
            t = WebUtility.HtmlDecode(t).Replace(' ', ' ');
            return Tidy(t);
        }

        /// <summary>Text of word/document.xml in a .docx: one line per paragraph (table cells included).</summary>
        public static string FromDocx(byte[] docx)
        {
            if (docx == null) throw Malformed();
            var xml = ZipEntryBytes(docx, "word/document.xml");
            if (xml == null) throw new InvalidDataException("This file isn't a Word .docx document (no word/document.xml). It may be password protected.");
            var sb = new StringBuilder();
            try
            {
                using (var r = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    Walk(XDocument.Load(r).Root, sb);
            }
            catch (XmlException ex) { throw new InvalidDataException("The Word document's text couldn't be read: " + ex.Message); }
            return Tidy(sb.ToString());
        }

        private static void Walk(XElement e, StringBuilder sb)
        {
            if (e == null) return;
            if (e.Name.Namespace.NamespaceName == W)
            {
                var n = e.Name.LocalName;
                if (n == "t") { sb.Append(e.Value); return; }
                if (n == "tab") { sb.Append('\t'); return; }
                if (n == "br" || n == "cr") { sb.Append('\n'); return; }
                if (n == "instrText" || n == "delText") return;
            }
            foreach (var c in e.Elements()) Walk(c, sb);
            if (e.Name.Namespace.NamespaceName == W && e.Name.LocalName == "p") sb.Append('\n');
        }

        private static string Tidy(string t)
        {
            t = t.Replace("\r\n", "\n").Replace('\r', '\n');
            t = Regex.Replace(t, @"[ \t]*\n[ \t]*", "\n");
            t = Regex.Replace(t, @"[ \t]{2,}", " ");
            t = Regex.Replace(t, @"\n{3,}", "\n\n");
            return t.Trim();
        }

        // Minimal zip reader (central directory + deflate): ZipArchive is .NET 4.5+, and Core stays net40 with no WindowsBase.
        private static byte[] ZipEntryBytes(byte[] z, string name)
        {
            try
            {
                var eocd = -1;
                if (z.Length >= 22)
                    for (var i = z.Length - 22; i >= Math.Max(0, z.Length - 22 - 65535); i--)
                        if (z[i] == 0x50 && z[i + 1] == 0x4B && z[i + 2] == 5 && z[i + 3] == 6) { eocd = i; break; }
                if (eocd < 0) throw new InvalidDataException("This file isn't a valid .docx (not a zip package).");
                long count = U16(z, eocd + 10), p = U32(z, eocd + 16);
                if (p < 0 || p >= z.Length) throw Malformed();
                for (long n = 0; n < count; n++)
                {
                    if (p + 46 > z.Length || U32(z, (int)p) != 0x02014B50) throw Malformed();
                    long method = U16(z, (int)p + 10), csize = U32(z, (int)p + 20), usize = U32(z, (int)p + 24),
                         nl = U16(z, (int)p + 28), el = U16(z, (int)p + 30), cl = U16(z, (int)p + 32), lh = U32(z, (int)p + 42);
                    if (p + 46 + nl > z.Length) throw Malformed();
                    var entry = Encoding.UTF8.GetString(z, (int)p + 46, (int)nl);
                    if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (lh + 30 > z.Length) throw Malformed();
                        long data = lh + 30 + U16(z, (int)lh + 26) + U16(z, (int)lh + 28);
                        if (data > z.Length || csize > z.Length - data) throw Malformed();
                        if (method == 0)
                        {
                            if (csize > MaxDocumentBytes) throw TooLarge();
                            var o = new byte[csize];
                            Buffer.BlockCopy(z, (int)data, o, 0, (int)csize);
                            return o;
                        }
                        if (method != 8) throw new InvalidDataException("Unsupported compression in the .docx.");
                        if (usize > MaxDocumentBytes) throw TooLarge();
                        var tooLarge = false;
                        using (var ds = new DeflateStream(new MemoryStream(z, (int)data, (int)csize), CompressionMode.Decompress))
                        using (var ms = new MemoryStream((int)Math.Min(usize > 0 ? usize : 4096, 1024 * 1024)))
                        {
                            try
                            {
                                var buf = new byte[81920]; int r; long total = 0;
                                while ((r = ds.Read(buf, 0, buf.Length)) > 0)
                                {
                                    if (total + r > MaxDocumentBytes) { tooLarge = true; break; }
                                    ms.Write(buf, 0, r);
                                    total += r;
                                }
                            }
                            catch (InvalidDataException) { throw Malformed(); }
                            if (tooLarge) throw TooLarge();
                            return ms.ToArray();
                        }
                    }
                    p += 46 + nl + el + cl;
                }
                return null;
            }
            catch (IndexOutOfRangeException) { throw Malformed(); }
            catch (ArgumentException) { throw Malformed(); }
        }

        private static InvalidDataException Malformed()
        {
            return new InvalidDataException("This file isn't a valid .docx (the Word package is damaged or incomplete).");
        }

        private static InvalidDataException TooLarge()
        {
            return new InvalidDataException("This Word document is too large to load. The text of a document can be at most " + (MaxDocumentBytes / (1024 * 1024)) + " MB. Split it or load part of it.");
        }

        private static int U16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
        private static uint U32(byte[] b, int i) { return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)); }
    }
}
