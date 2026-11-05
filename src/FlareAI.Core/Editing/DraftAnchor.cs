using System.Xml;

namespace FlareAI.Core.Editing
{
    /// <summary>
    /// Where a drafted insert goes: right after the paragraph or heading that holds the caret. Only a paragraph or heading directly under
    /// the body counts, so new text is never put inside a list, table, drop-down or snippet, and an existing paragraph is never split.
    /// </summary>
    public static class DraftAnchor
    {
        public const string NoPlace = "Click in a paragraph or heading to insert new text. House Style can't insert inside a list, table or drop-down.";

        /// <summary>The body-level paragraph or heading holding the caret, or null with the reason in <paramref name="refusal"/>.</summary>
        public static XmlNode For(IDocumentAdapter doc, out string refusal)
        {
            refusal = null;
            var body = doc == null || doc.Document == null ? null : BlockSelector.FindBody(doc.Document);
            var nodes = doc == null ? null : doc.SelectionNodes;
            if (body == null || nodes == null || nodes.Count == 0) { refusal = NoPlace; return null; }
            return Under(body, nodes[0], out refusal);
        }

        /// <summary>The child of <paramref name="body"/> that holds <paramref name="node"/>, when it is a paragraph or heading.</summary>
        public static XmlNode Under(XmlNode body, XmlNode node, out string refusal)
        {
            refusal = null;
            var top = node;
            while (top != null && top.ParentNode != body) top = top.ParentNode;
            if (top == null || !IsPlace(top)) { refusal = NoPlace; return null; }
            return top;
        }

        /// <summary>A paragraph or a heading level 1 to 6, in no namespace.</summary>
        public static bool IsPlace(XmlNode n)
        {
            if (n == null || n.NodeType != XmlNodeType.Element || !string.IsNullOrEmpty(n.NamespaceURI)) return false;
            var name = n.LocalName;
            return name == "p" || (name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6');
        }

        /// <summary>The index in <paramref name="body"/> just after <paramref name="anchor"/>, or -1 when it is no longer there.</summary>
        public static int InsertIndex(XmlNode body, XmlNode anchor)
        {
            if (body == null || anchor == null || anchor.ParentNode != body) return -1;
            var i = 0;
            for (var n = body.FirstChild; n != null; n = n.NextSibling, i++)
                if (n == anchor) return i + 1;
            return -1;
        }
    }
}
