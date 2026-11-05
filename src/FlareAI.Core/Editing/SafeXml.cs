using System.IO;
using System.Xml;

namespace FlareAI.Core.Editing
{
    /// <summary>
    /// The one place Core reads XML from disk or from a string. Project, guide and shared-folder files are untrusted
    /// input, so DTD processing is off (a DOCTYPE is skipped, never expanded) and no resolver is ever used.
    /// </summary>
    public static class SafeXml
    {
        public static XmlReaderSettings Settings()
        {
            return new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        }

        /// <summary>Loads a file into <paramref name="doc"/>, keeping its PreserveWhitespace setting.</summary>
        public static XmlDocument Load(XmlDocument doc, string path)
        {
            using (var r = XmlReader.Create(path, Settings())) doc.Load(r);
            return doc;
        }

        public static XmlDocument Load(string path, bool preserveWhitespace = false)
        {
            return Load(New(preserveWhitespace), path);
        }

        public static XmlDocument LoadXml(XmlDocument doc, string xml)
        {
            using (var sr = new StringReader(xml))
            using (var r = XmlReader.Create(sr, Settings())) doc.Load(r);
            return doc;
        }

        public static XmlDocument LoadXml(string xml, bool preserveWhitespace = false)
        {
            return LoadXml(New(preserveWhitespace), xml);
        }

        private static XmlDocument New(bool preserveWhitespace)
        {
            return new XmlDocument { PreserveWhitespace = preserveWhitespace, XmlResolver = null };
        }
    }
}
