using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FlareAI.Core.Style
{
    /// <summary>One thing added to the notes box: pasted text, a file read as text, or a PDF sent as it is.</summary>
    public sealed class NoteSource
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public byte[] Pdf { get; set; }
    }

    /// <summary>
    /// Plain text from the files an owner drops into "Add notes or an old guide": .txt/.md as is, Word "Web Page"
    /// HTML (.htm/.html) cleaned of styles, scripts and Office markup, and .docx read straight from the package.
    /// Throws IOException, UnauthorizedAccessException or InvalidDataException with a message fit to show.
    /// </summary>
    public static class NoteText
    {
        internal const int MaxDocxPartBytes = 20 * 1024 * 1024;

        private const string W ="http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        /// <summary>Reads a notes file (shared read, so a copy open in Word still loads).</summary>
        public const int MaxPdfBytes = 20 * 1024 * 1024;

        /// <summary>The bytes of a PDF file, or null when the file is not a PDF. Throws when it is too large to send.</summary>
        public static byte[] ReadPdf(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (!(bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F')) return null;
            if (bytes.Length > MaxPdfBytes) throw new InvalidDataException("This PDF is larger than 20 MB. Split it into smaller files, or save the pages with the rules as a new PDF.");
            return bytes;
        }

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
            if (ext == ".docx") return FromDocx(bytes);
            if (bytes.Length > 3 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4) return FromPackage(bytes);
            if (bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F')
                throw new InvalidDataException("House Style can't read the text of a PDF here. Add it with Add Files in the notes box instead.");
            if (bytes.Length > 5 && bytes[0] == '{' && bytes[1] == '\\' && bytes[2] == 'r' && bytes[3] == 't' && bytes[4] == 'f') return FromRtf(DecodeText(bytes));
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

        /// <summary>Text of any zip-package document House Style reads: Word, Excel, PowerPoint or OpenDocument.</summary>
        public static string FromPackage(byte[] zip)
        {
            if (ZipEntryBytes(zip, "word/document.xml") != null) return FromDocx(zip);
            if (ZipEntryBytes(zip, "xl/workbook.xml") != null) return FromXlsx(zip);
            if (ZipEntryBytes(zip, "ppt/presentation.xml") != null) return FromPptx(zip);
            if (ZipEntryBytes(zip, "content.xml") != null) return FromOdt(zip);
            throw new InvalidDataException("House Style can't read this kind of file. It reads Word (.docx), Excel (.xlsx), PowerPoint (.pptx), OpenDocument (.odt), Rich Text (.rtf), web pages and text files. Save it as one of those, or paste the text.");
        }

        private static XDocument Load(byte[] xml)
        {
            try
            {
                using (var r = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    return XDocument.Load(r);
            }
            catch (XmlException ex) { throw new InvalidDataException("The document's text couldn't be read: " + ex.Message); }
        }

        /// <summary>One line per spreadsheet row, cells separated by " | ", sheets one after another. A rule table in Excel reads as its rows.</summary>
        public static string FromXlsx(byte[] zip)
        {
            const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var shared = new System.Collections.Generic.List<string>();
            var ss = ZipEntryBytes(zip, "xl/sharedStrings.xml");
            if (ss != null)
                foreach (var si in Load(ss).Root.Elements(XName.Get("si", S)))
                    shared.Add(string.Concat(si.Descendants(XName.Get("t", S)).Select(t => t.Value).ToArray()));
            var sb = new StringBuilder();
            for (var n = 1; n <= 30; n++)
            {
                var sheet = ZipEntryBytes(zip, "xl/worksheets/sheet" + n + ".xml");
                if (sheet == null) { if (n == 1) continue; break; }
                foreach (var row in Load(sheet).Descendants(XName.Get("row", S)))
                {
                    var cells = new System.Collections.Generic.List<string>();
                    foreach (var c in row.Elements(XName.Get("c", S)))
                    {
                        var type = (string)c.Attribute("t");
                        var v = c.Element(XName.Get("v", S));
                        string text = null;
                        if (type == "s" && v != null) { int i; if (int.TryParse(v.Value, out i) && i >= 0 && i < shared.Count) text = shared[i]; }
                        else if (type == "inlineStr") text = string.Concat(c.Descendants(XName.Get("t", S)).Select(t => t.Value).ToArray());
                        else if (v != null) text = v.Value;
                        if (!string.IsNullOrWhiteSpace(text)) cells.Add(text.Trim());
                    }
                    if (cells.Count > 0) sb.Append(string.Join(" | ", cells.ToArray())).Append('\n');
                }
                sb.Append('\n');
            }
            return Tidy(sb.ToString());
        }

        /// <summary>The text of each slide, one line per paragraph.</summary>
        public static string FromPptx(byte[] zip)
        {
            const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
            var sb = new StringBuilder();
            for (var n = 1; n <= 300; n++)
            {
                var slide = ZipEntryBytes(zip, "ppt/slides/slide" + n + ".xml");
                if (slide == null) { if (n == 1) continue; break; }
                foreach (var p in Load(slide).Descendants(XName.Get("p", A)))
                    sb.Append(string.Concat(p.Descendants(XName.Get("t", A)).Select(t => t.Value).ToArray())).Append('\n');
                sb.Append('\n');
            }
            return Tidy(sb.ToString());
        }

        /// <summary>Text of content.xml in an OpenDocument text file: one line per paragraph or heading.</summary>
        public static string FromOdt(byte[] zip)
        {
            const string T = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
            var sb = new StringBuilder();
            foreach (var p in Load(ZipEntryBytes(zip, "content.xml")).Descendants().Where(e => e.Name.Namespace.NamespaceName == T && (e.Name.LocalName == "p" || e.Name.LocalName == "h")))
            {
                foreach (var n in p.DescendantNodes())
                {
                    var t = n as XText;
                    if (t != null) sb.Append(t.Value);
                    var e = n as XElement;
                    if (e != null && e.Name.LocalName == "s") sb.Append(' ');
                    if (e != null && e.Name.LocalName == "tab") sb.Append('\t');
                    if (e != null && e.Name.LocalName == "line-break") sb.Append('\n');
                }
                sb.Append('\n');
            }
            return Tidy(sb.ToString());
        }

        /// <summary>Plain text of Rich Text Format: paragraph ends kept, fonts, styles and pictures dropped, \'hh and \uN decoded.</summary>
        public static string FromRtf(string rtf)
        {
            var sb = new StringBuilder();
            var skip = new System.Collections.Generic.Stack<bool>();
            var skipping = false;
            var skipDepth = 0;
            var depth = 0;
            for (var i = 0; i < rtf.Length; i++)
            {
                var c = rtf[i];
                if (c == '{') { depth++; skip.Push(skipping); if (skipping) skipDepth++; continue; }
                if (c == '}') { depth--; if (skip.Count > 0) { skipping = skip.Pop(); } continue; }
                if (c != '\\') { if (!skipping && c != '\r' && c != '\n') sb.Append(c); continue; }
                if (++i >= rtf.Length) break;
                var d = rtf[i];
                if (d == '\'' && i + 2 < rtf.Length)
                {
                    int code;
                    if (int.TryParse(rtf.Substring(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out code) && !skipping) sb.Append(Encoding.Default.GetString(new[] { (byte)code }));
                    i += 2;
                    continue;
                }
                if (d == '\\' || d == '{' || d == '}') { if (!skipping) sb.Append(d); continue; }
                if (d == '*') { skipping = true; continue; }   // an optional destination: skip its group
                var j = i;
                while (j < rtf.Length && char.IsLetter(rtf[j])) j++;
                var word = rtf.Substring(i, j - i);
                var k = j;
                if (k < rtf.Length && (rtf[k] == '-' || char.IsDigit(rtf[k]))) { k++; while (k < rtf.Length && char.IsDigit(rtf[k])) k++; }
                var arg = rtf.Substring(j, k - j);
                if (k < rtf.Length && rtf[k] == ' ') k++;
                i = k - 1;
                if (word == "fonttbl" || word == "colortbl" || word == "stylesheet" || word == "info" || word == "pict" || word == "header" || word == "footer" || word == "listtable" || word == "listoverridetable") skipping = true;
                else if (skipping) { }
                else if (word == "par" || word == "line" || word == "row") sb.Append('\n');
                else if (word == "tab" || word == "cell") sb.Append(word == "tab" ? '\t' : ' ');
                else if (word == "u")
                {
                    int u;
                    if (int.TryParse(arg, out u)) { sb.Append((char)(u < 0 ? u + 65536 : u)); if (i + 1 < rtf.Length && rtf[i + 1] == '?') i++; }
                }
            }
            return Tidy(sb.ToString());
        }

        /// <summary>Text of word/document.xml in a .docx: one line per paragraph (table cells included).</summary>
        public static string FromDocx(byte[] docx)
        {
            if (docx == null) throw BadDocx();
            var xml = ZipEntryBytes(docx,"word/document.xml");
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
                if (z.Length < 22) throw new InvalidDataException("This file isn't a valid .docx (not a zip package).");
                var eocd = -1;
                for (var i = z.Length - 22; i >= Math.Max(0, z.Length - 22 - 65535); i--)
                    if (z[i] == 0x50 && z[i + 1] == 0x4B && z[i + 2] == 5 && z[i + 3] == 6) { eocd = i; break; }
                if (eocd < 0) throw new InvalidDataException("This file isn't a valid .docx (not a zip package).");
                long count = U16(z, eocd + 10), p = U32(z, eocd + 16);
                if (p >= z.Length) throw BadDocx();
                for (var n = 0; n < count && p + 46 <= z.Length && U32(z, p) == 0x02014B50; n++)
                {
                    long method = U16(z, p + 10), csize = U32(z, p + 20), usize = U32(z, p + 24), nl = U16(z, p + 28), el = U16(z, p + 30), cl = U16(z, p + 32), lh = U32(z, p + 42);
                    if (p + 46 + nl > z.Length) throw BadDocx();
                    var entry = Encoding.UTF8.GetString(z, (int)(p + 46), (int)nl);
                    if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (lh + 30 > z.Length || U32(z, lh) != 0x04034B50) throw BadDocx();
                        var data = lh + 30 + U16(z, lh + 26) + U16(z, lh + 28);
                        if (data > z.Length || csize > z.Length - data) throw BadDocx();
                        if (usize > MaxDocxPartBytes || (method == 0 && csize > MaxDocxPartBytes)) throw TooLarge();
                        if (method == 0) { var o = new byte[(int)csize]; Buffer.BlockCopy(z, (int)data, o, 0, (int)csize); return o; }
                        if (method != 8) throw new InvalidDataException("Unsupported compression in the .docx.");
                        var tooBig = false;
                        using (var ds = new DeflateStream(new MemoryStream(z, (int)data, (int)csize), CompressionMode.Decompress))
                        using (var ms = new MemoryStream((int)Math.Min(usize, 1 << 20)))
                        {
                            var buf = new byte[81920]; int r; long total = 0;
                            try
                            {
                                while ((r = ds.Read(buf, 0, buf.Length)) > 0)
                                {
                                    if (total + r > MaxDocxPartBytes) { tooBig = true; break; }
                                    total += r;
                                    ms.Write(buf, 0, r);
                                }
                            }
                            catch (InvalidDataException) { throw BadDocx(); } // the cap is raised below, outside this try
                            if (tooBig) throw TooLarge();
                            return ms.ToArray();
                        }
                    }
                    p += 46 + nl + el + cl;
                }
                return null;
            }
            catch (IndexOutOfRangeException) { throw BadDocx(); }
            catch (ArgumentException) { throw BadDocx(); }
            catch (OverflowException) { throw BadDocx(); }
        }

        private static InvalidDataException BadDocx()
        {
            return new InvalidDataException("This file isn't a valid .docx, or it is damaged. Pick another file.");
        }

        private static InvalidDataException TooLarge()
        {
            return new InvalidDataException("This Word document is too large to read. The text of the document can be at most " + (MaxDocxPartBytes / (1024 * 1024)) + " MB. Save a smaller copy, or paste the notes instead.");
        }

        private static int U16(byte[] b, long i)
        {
            if (i < 0 || i + 2 > b.Length) throw new IndexOutOfRangeException();
            return b[i] | (b[i + 1] << 8);
        }

        private static uint U32(byte[] b, long i)
        {
            if (i < 0 || i + 4 > b.Length) throw new IndexOutOfRangeException();
            return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
        }
    }
}
