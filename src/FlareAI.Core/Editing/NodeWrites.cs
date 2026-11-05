using System.Collections.Generic;
using System.Xml;

namespace FlareAI.Core.Editing
{
    /// <summary>
    /// The block replacements Flare 22 can't take as a tracked node operation (<see cref="IDocumentAdapter.ReplaceBlocks"/>),
    /// per docs/flare-api-notes.md, with the reason in the writer's words. Every caller of ReplaceBlocks checks this first, and
    /// the Flare adapter refuses again as a last line of defence.
    /// <list type="bullet">
    /// <item>Content that holds a <c>MadCap:variable</c>: the operation undoes fine but crashes Flare on Redo (#110).</item>
    /// <item>A different element in place of the first old block (list to table, paragraph to list): ReplaceDocumentNode keeps
    /// the target element and copies only content and attributes, so the old tag would stay around the new content. Replacing
    /// it by insert-new + remove-old is unverified in Flare 22, so it is refused (#111).</item>
    /// </list>
    /// </summary>
    public static class NodeWrites
    {
        /// <summary>Why <paramref name="oldNodes"/> can't be replaced by <paramref name="newNodes"/> as a node operation; null when it can.</summary>
        public static string Refusal(IList<XmlNode> oldNodes, IList<XmlNode> newNodes)
        {
            return VariableRefusal(oldNodes, newNodes) ?? ElementChangeRefusal(oldNodes, newNodes);
        }

        /// <summary>The elements a drafted insert may use: paragraphs, lists and plain inline emphasis.</summary>
        private static readonly string[] InsertAllowed = { "p", "ul", "ol", "li", "strong", "em", "code" };
        private const int InsertMaxBlocks = 8, InsertMaxWords = 400;

        /// <summary>
        /// Why <paramref name="newNodes"/> can't be inserted as new blocks; null when they can. Only paragraphs and lists (nested at
        /// most two deep) with strong, em and code inside, no attributes, no MadCap element (a variable crashes Flare on Redo, and
        /// links and conditions would be invented), no bare text at the top, and a size limit.
        /// </summary>
        public static string InsertRefusal(IList<XmlNode> newNodes)
        {
            const string tail = " Try again and ask for paragraphs and lists.";
            if (newNodes == null || newNodes.Count == 0) return "There is nothing to insert.";
            if (newNodes.Count > InsertMaxBlocks) return "The draft has more than " + InsertMaxBlocks + " blocks, so nothing was changed." + tail;
            var words = 0;
            foreach (var n in newNodes)
            {
                if (n.NodeType == XmlNodeType.Whitespace || n.NodeType == XmlNodeType.SignificantWhitespace) continue;
                if (n.NodeType != XmlNodeType.Element || (n.LocalName != "p" && n.LocalName != "ul" && n.LocalName != "ol"))
                    return "The draft used markup House Style can't insert, so nothing was changed." + tail;
                var bad = InsertProblem(n, 0);
                if (bad != null) return bad + tail;
                words += CountWords(n.InnerText);
            }
            if (words > InsertMaxWords) return "The draft is longer than " + InsertMaxWords + " words, so nothing was changed.";
            return null;
        }

        private static string InsertProblem(XmlNode n, int listDepth)
        {
            const string markup = "The draft used markup House Style can't insert, so nothing was changed.";
            if (n.NodeType == XmlNodeType.Text || n.NodeType == XmlNodeType.Whitespace || n.NodeType == XmlNodeType.SignificantWhitespace) return null;
            if (n.NodeType != XmlNodeType.Element) return markup;
            if (!string.IsNullOrEmpty(n.NamespaceURI) || System.Array.IndexOf(InsertAllowed, n.LocalName) < 0) return markup;
            if (n.Attributes != null && n.Attributes.Count > 0) return markup;
            var depth = listDepth + (n.LocalName == "ul" || n.LocalName == "ol" ? 1 : 0);
            if (depth > 2) return markup;
            foreach (XmlNode c in n.ChildNodes)
            {
                var bad = InsertProblem(c, depth);
                if (bad != null) return bad;
            }
            return null;
        }

        private static int CountWords(string text)
        {
            return string.IsNullOrEmpty(text) ? 0 : text.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries).Length;
        }

        /// <summary>The refusal when any old or new block holds a variable; null otherwise.</summary>
        public static string VariableRefusal(IList<XmlNode> oldNodes, IList<XmlNode> newNodes)
        {
            var name = FirstVariable(oldNodes) ?? FirstVariable(newNodes);
            if (name == null) return null;
            return "House Style can't apply this edit: it would replace a whole paragraph that contains a variable" +
                   (name.Length > 0 ? " (" + name + ")" : "") + ", and Flare crashes if you then redo the change (Ctrl+Y). " +
                   "Ask for a wording-only edit (Rewrite), or make this change yourself.";
        }

        /// <summary>The refusal when the first new block is a different element from the block it would replace; null otherwise.</summary>
        public static string ElementChangeRefusal(IList<XmlNode> oldNodes, IList<XmlNode> newNodes)
        {
            if (oldNodes == null || newNodes == null || oldNodes.Count == 0 || newNodes.Count == 0) return null;
            XmlNode was = oldNodes[0], now = newNodes[0];
            if (was.NodeType != XmlNodeType.Element || now.NodeType != XmlNodeType.Element) return null;
            if (was.LocalName == now.LocalName && (was.NamespaceURI ?? "") == (now.NamespaceURI ?? "")) return null;
            return "House Style can't apply this edit: it turns " + Describe(was) + " into " + Describe(now) +
                   ", and Flare can't make that change as a tracked change. Make this change yourself, or ask for an edit that keeps " +
                   Describe(was) + ".";
        }

        /// <summary>The name of the first variable in the nodes ("" when unnamed), or null when there is none.</summary>
        private static string FirstVariable(IList<XmlNode> nodes)
        {
            if (nodes == null) return null;
            foreach (var n in nodes)
            {
                var v = Find(n);
                if (v != null) return v.Attributes == null || v.Attributes["name"] == null ? "" : v.Attributes["name"].Value;
            }
            return null;
        }

        private static XmlNode Find(XmlNode n)
        {
            if (n == null || n.NodeType != XmlNodeType.Element) return null;
            if (n.LocalName == "variable" && n.NamespaceURI == BlockSelector.MadCapNs) return n;
            foreach (XmlNode c in n.ChildNodes)
            {
                var hit = Find(c);
                if (hit != null) return hit;
            }
            return null;
        }

        private static string Describe(XmlNode n)
        {
            switch (n.LocalName)
            {
                case "p": return "a paragraph";
                case "ul": return "a bulleted list";
                case "ol": return "a numbered list";
                case "li": return "a list item";
                case "table": return "a table";
                case "dl": return "a definition list";
                case "blockquote": return "a quotation";
                case "pre": return "a code block";
                case "div": return "a section (div)";
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6": return "a heading (" + n.LocalName + ")";
                default: return "a <" + n.Name + ">";
            }
        }
    }
}
