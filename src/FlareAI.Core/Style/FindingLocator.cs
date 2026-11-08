using System;
using System.Collections.Generic;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;

namespace FlareAI.Core.Style
{
    /// <summary>
    /// Ties findings to text in the live DOM. Findings remember text + context, not node references, because
    /// every tracked edit splits or replaces text nodes; we re-locate right before Go to / Fix.
    /// </summary>
    public static class FindingLocator
    {
        private const int ContextChars = 40;

        /// <summary>A lint hit for [start, start+length) of one run (the run's share of a line-level hit).</summary>
        public static StyleFinding FromLint(LintHit h, XmlText run, int start, int length, bool exact)
        {
            var f = FromLint(new LintHit
            {
                Start = start, Length = length, Match = run.Value.Substring(start, length), Replacement = exact ? h.Replacement : null,
                Rule = h.Rule, Severity = h.Severity, Message = h.Message, Layer = h.Layer, Link = h.Link, Variable = exact ? h.Variable : null, RuleId = h.RuleId, AlsoRules = h.AlsoRules
            }, run);
            return f;
        }

        public static StyleFinding FromLint(LintHit h, XmlText run)
        {
            return new StyleFinding
            {
                Source = "lint", Rule = h.Rule, Severity = h.Severity, Suggestion = h.Message,
                Excerpt = h.Match, Replacement = h.Replacement, Layer = h.Layer, Link = h.Link, Variable = h.Variable, RuleId = h.RuleId, AlsoRules = h.AlsoRules,
                Before = Tail(run.Value.Substring(0, h.Start)), Block = TextRuns.BlockOf(run)
            };
        }

        /// <summary>A replacement for an exact span of a run (used for word-level rewrites).</summary>
        public static StyleFinding ForSpan(XmlText run, int start, int length, string replacement, XmlNode liveBlock)
        {
            return new StyleFinding
            {
                Source = "rewrite", Rule = "rewrite", Severity = "suggestion", Layer = "ai",
                Excerpt = run.Value.Substring(start, length), Replacement = replacement,
                Before = Tail(run.Value.Substring(0, start)), Block = liveBlock
            };
        }

        /// <summary>Anchors a model finding to the runs it quotes. False when the excerpt is not verbatim in one run.</summary>
        public static bool Anchor(StyleFinding f, IList<XmlText> runs)
        {
            if (string.IsNullOrEmpty(f.Excerpt)) return false;
            foreach (var run in runs)
            {
                var i = run.Value.IndexOf(f.Excerpt, StringComparison.Ordinal);
                if (i < 0) continue;
                f.Before = Tail(run.Value.Substring(0, i));
                f.Block = TextRuns.BlockOf(run);
                return true;
            }
            return false;
        }

        /// <summary>Finds the finding's text in the current DOM, preferring its original block and context.</summary>
        public static bool TryLocate(XmlDocument doc, StyleFinding f, out XmlText run, out int start)
        {
            bool exact;
            return TryLocate(doc, f, out run, out start, out exact);
        }

        /// <summary>
        /// As the overload without it; <paramref name="exactContext"/> says the text before the excerpt is still what it was
        /// (false: only the excerpt matched, once).
        /// </summary>
        public static bool TryLocate(XmlDocument doc, StyleFinding f, out XmlText run, out int start, out bool exactContext)
        {
            run = null; start = -1; exactContext = false;
            var blocks = new List<XmlNode>();
            if (f.Block != null && TextRuns.BlockOf(f.Block) == f.Block) blocks.Add(f.Block); // still attached
            else
            {
                var body = BlockSelector.FindBody(doc);
                if (body == null) return false;
                foreach (XmlNode c in body.ChildNodes) if (c.NodeType == XmlNodeType.Element) blocks.Add(c);
            }

            XmlText fallback = null; var fallbackAt = -1; var occurrences = 0;
            foreach (var r in TextRuns.Of(blocks))
            {
                for (var i = r.Value.IndexOf(f.Excerpt, StringComparison.Ordinal); i >= 0;
                     i = r.Value.IndexOf(f.Excerpt, i + 1, StringComparison.Ordinal))
                {
                    if (Tail(r.Value.Substring(0, i)) == (f.Before ?? "")) { run = r; start = i; exactContext = true; return true; }
                    occurrences++;
                    if (fallback == null) { fallback = r; fallbackAt = i; }
                }
            }
            // The context may have changed (the writer edited nearby, or an earlier fix in a batch). The text is still
            // safe to act on only when it occurs exactly once: with several occurrences we can't tell which one the
            // finding meant, and applying it to the wrong one is worse than reporting it stale.
            // Rewrite pieces are often single words, so for them only an exact context match is safe.
            if (f.Source == "rewrite" || occurrences != 1) return false;
            run = fallback; start = fallbackAt;
            return run != null;
        }

        /// <summary>Every image in the topic's body, in document order.</summary>
        public static List<XmlElement> ImagesOf(XmlDocument doc)
        {
            var list = new List<XmlElement>();
            var body = doc == null ? null : BlockSelector.FindBody(doc);
            if (body == null) return list;
            foreach (XmlElement e in body.SelectNodes(".//*[local-name()='img']")) list.Add(e);
            return list;
        }

        /// <summary>Finds the image an image finding is about in the current DOM (the same file, the same place among its repeats). False when it is gone.</summary>
        public static bool TryLocateImage(XmlDocument doc, StyleFinding f, out XmlElement image)
        {
            image = null;
            if (f == null || f.ImageSrc == null) return false;
            var seen = 0;
            foreach (var img in ImagesOf(doc))
            {
                if (img.GetAttribute("src") != f.ImageSrc) continue;
                if (seen++ == f.ImageIndex) { image = img; return true; }
            }
            return false;
        }

        /// <summary>The body-level block a finding is in now: the image's for an image finding, else the one holding its text. Null when it is gone.</summary>
        public static XmlNode LocateBlock(XmlDocument doc, StyleFinding f)
        {
            XmlElement img;
            if (f.ImageSrc != null) return TryLocateImage(doc, f, out img) ? TextRuns.BlockOf(img) : null;
            XmlText run; int start;
            return TryLocate(doc, f, out run, out start) ? TextRuns.BlockOf(run) : null;
        }

        private static string Tail(string s)
        {
            return s.Length <= ContextChars ? s : s.Substring(s.Length - ContextChars);
        }
    }
}
