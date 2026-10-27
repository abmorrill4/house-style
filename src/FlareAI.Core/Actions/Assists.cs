using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using FlareAI.Core.Editing;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Actions
{
    /// <summary>Metadata and companion content proposed for a topic (copied into Flare by the author).</summary>
    public sealed class TopicAssist
    {
        public string Description { get; set; }
        public List<string> Keywords { get; set; } = new List<string>();
        public List<List<string>> Synonyms { get; set; } = new List<List<string>>();
        public List<KeyValuePair<string, string>> MicroContent { get; set; } = new List<KeyValuePair<string, string>>();
        public List<KeyValuePair<string, string>> Glossary { get; set; } = new List<KeyValuePair<string, string>>();

        public static TopicAssist Parse(string text)
        {
            var s = (text ?? "").Trim();
            int a = s.IndexOf('{'), b = s.LastIndexOf('}');
            if (a < 0 || b <= a) throw new FormatException("Model did not return a JSON object.");
            var o = JObject.Parse(s.Substring(a, b - a + 1));
            var t = new TopicAssist { Description = (string)o["description"] };
            foreach (var k in o["keywords"] ?? new JArray()) t.Keywords.Add((string)k);
            foreach (var g in o["synonyms"] ?? new JArray())
            {
                var group = new List<string>();
                foreach (var w in g) group.Add((string)w);
                if (group.Count > 1) t.Synonyms.Add(group);
            }
            foreach (var m in o["micro_content"] ?? new JArray()) t.MicroContent.Add(new KeyValuePair<string, string>((string)m["phrase"], (string)m["response"]));
            foreach (var g in o["glossary"] ?? new JArray()) t.Glossary.Add(new KeyValuePair<string, string>((string)g["term"], (string)g["definition"]));
            return t;
        }
    }

    /// <summary>Alt text for images: find the image, read it for the model, and write the alt as a validated tracked change.</summary>
    public static class AltText
    {
        private static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".gif", "image/gif" }, { ".webp", "image/webp" } };

        /// <summary>The first image in the block that has no alt text, or null.</summary>
        public static XmlElement MissingIn(XmlNode block)
        {
            if (block == null || block.NodeType != XmlNodeType.Element) return null;
            // An image alone in its paragraph has no text, so its finding sits on the text next to it: look there too.
            foreach (var b in new[] { block, Sibling(block, true), Sibling(block, false) })
            {
                if (b == null) continue;
                foreach (XmlElement img in b.SelectNodes("descendant-or-self::*[local-name()='img']"))
                    if (img.GetAttribute("alt").Trim().Length == 0) return img;
            }
            return null;
        }

        private static XmlNode Sibling(XmlNode n, bool next)
        {
            do n = next ? n.NextSibling : n.PreviousSibling; while (n != null && n.NodeType != XmlNodeType.Element);
            return n;
        }

        /// <summary>The image file and its media type for the model; throws with a reason the author can act on.</summary>
        public static LlmImageFile Load(string topicPath, string src)
        {
            if (string.IsNullOrEmpty(src)) throw new InvalidOperationException("The image has no src.");
            var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(topicPath) ?? "", Uri.UnescapeDataString(src).Replace('/', Path.DirectorySeparatorChar)));
            string type;
            if (!Types.TryGetValue(Path.GetExtension(path), out type)) throw new InvalidOperationException("The AI can read PNG, JPEG, GIF and WebP images, not " + Path.GetExtension(path) + ".");
            if (!File.Exists(path)) throw new InvalidOperationException("Image not found: " + path);
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 4800000) throw new InvalidOperationException("The image is over 5 MB.");
            return new LlmImageFile { Path = path, MediaType = type, Base64 = Convert.ToBase64String(bytes) };
        }

        /// <summary>Text around the block (nearest heading + the block's text), as context for the model.</summary>
        public static string Context(XmlNode block)
        {
            var heading = "";
            for (var n = block; n != null; n = n.PreviousSibling)
                if (n.NodeType == XmlNodeType.Element && ReviewOrder.IsHeading(n)) { heading = n.InnerText.Trim(); break; }
            return (heading.Length > 0 ? "Heading: " + heading + "\n" : "") + "Text: " + block.InnerText.Trim();
        }

        /// <summary>Sets the image's alt in a copy of its block and replaces the block as one tracked change (validated).</summary>
        public static void Apply(IDocumentAdapter doc, XmlNode block, string src, string alt)
        {
            var copy = block.CloneNode(true);
            XmlElement target = null;
            foreach (XmlElement img in copy.SelectNodes("descendant-or-self::*[local-name()='img']"))
                if (img.GetAttribute("src") == src && img.GetAttribute("alt").Trim().Length == 0) { target = img; break; }
            if (target == null) throw new InvalidOperationException("The image is no longer in the paragraph.");
            target.SetAttribute("alt", alt);
            var check = XhtmlValidator.Validate(new List<XmlNode> { block }, new List<XmlNode> { copy });
            if (!check.IsValid) throw new InvalidOperationException("Rejected: " + check);
            if (NodeWrites.VariableRefusal(new List<XmlNode> { block }, null) != null)
                throw new InvalidOperationException("House Style can't add this alt text: the image's paragraph contains a variable, and Flare crashes if you redo (Ctrl+Y) a change to that paragraph. " +
                                                    "Add the alt text yourself in the image's properties: \"" + alt + "\"");
            ActionRunner.WriteTracked(doc.SourcePath, () => doc.ReplaceBlocks("House Style: alt text", new List<XmlNode> { block }, new List<XmlNode> { copy }));
        }
    }

    public sealed class LlmImageFile
    {
        public string Path { get; set; }
        public string MediaType { get; set; }
        public string Base64 { get; set; }
    }
}
