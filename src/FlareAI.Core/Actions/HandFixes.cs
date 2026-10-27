using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>Counting, keying and comparing helpers for issues the writer may fix by hand in the editor.</summary>
    public static class HandFixes
    {
        /// <summary>How many times <paramref name="excerpt"/> occurs in <paramref name="text"/>, literally and without overlaps.</summary>
        public static int Count(string text, string excerpt)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(excerpt)) return 0;
            var n = 0;
            var i = 0;
            while (true)
            {
                i = text.IndexOf(excerpt, i, StringComparison.Ordinal);
                if (i < 0) return n;
                n++;
                i += excerpt.Length;
            }
        }

        /// <summary>The last 12 characters before the excerpt, then the excerpt: tells two hits of the same text apart.</summary>
        public static string ContextKey(StyleFinding f)
        {
            var before = f.Before ?? "";
            if (before.Length > 12) before = before.Substring(before.Length - 12);
            return before + (f.Excerpt ?? "");
        }

        /// <summary>The text of every editable run under a scope, joined in document order.</summary>
        public static string ScopeText(XmlNode scope)
        {
            if (scope == null) return "";
            var sb = new StringBuilder();
            foreach (var run in TextRuns.Of(new List<XmlNode> { scope })) sb.Append(run.Value);
            return sb.ToString();
        }

        /// <summary>True when the finding's block is still in the document.</summary>
        public static bool IsAttached(StyleFinding f)
        {
            return f.Block != null && TextRuns.BlockOf(f.Block) == f.Block;
        }

        /// <summary>True when two paths name the same file (case and separators ignored).</summary>
        public static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Full(string p)
        {
            try { return Path.GetFullPath(p); }
            catch (Exception) { return p; }
        }
    }
}
