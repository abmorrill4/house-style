using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace FlareAI.Core.Editing
{
    /// <summary>Serialise a set of blocks to a fragment string for the prompt, and parse one back.</summary>
    public static class XhtmlFragment
    {
        public static string Serialize(IList<XmlNode> blocks)
        {
            var sb = new StringBuilder();
            foreach (var b in blocks)
            {
                // A subtree's OuterXml re-declares xmlns:MadCap on every MadCap element; ParseInto supplies it,
                // so drop it: fewer tokens, and the model doesn't copy it into its answer.
                sb.Append(b.OuterXml.Replace(" xmlns:MadCap=\"" + BlockSelector.MadCapNs + "\"", ""));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses a fragment that may contain multiple top-level elements and MadCap-prefixed elements,
        /// and imports the resulting nodes into <paramref name="target"/>. Throws XmlException when malformed.
        /// </summary>
        public static IList<XmlNode> ParseInto(string fragment, XmlDocument target)
        {
            var wrapped = "<root xmlns:MadCap=\"" + BlockSelector.MadCapNs + "\">" + fragment + "</root>";
            var tmp = SafeXml.LoadXml(wrapped, true);

            var result = new List<XmlNode>();
            foreach (XmlNode child in tmp.DocumentElement.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                    result.Add(target.ImportNode(child, true));
                else if (child.NodeType == XmlNodeType.Text && child.Value.Trim().Length > 0)
                    throw new XmlException("Loose text outside a block element: \"" + Truncate(child.Value.Trim(), 40) + "\"");
            }
            return result;
        }

        private static string Truncate(string s, int n) { return s.Length <= n ? s : s.Substring(0, n) + "…"; }
    }
}
