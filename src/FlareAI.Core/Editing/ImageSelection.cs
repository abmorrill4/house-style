using System;
using System.Collections.Generic;
using System.Xml;
using FlareAI.Core.Style;

namespace FlareAI.Core.Editing
{
    /// <summary>
    /// What the editor adapter needs to select an image. Flare's text positions give an image no room, so a selection can't hold only
    /// the image. The adapter finds the text beside it, works out where the image is from that text's position, then tries a few
    /// selections at and around that place until Flare reports the image among the selected nodes. The positions can be off by a few
    /// characters (deleted text, spaces and breaks Flare counts differently), so the tries spread outwards from the expected place.
    /// </summary>
    public static class ImageSelection
    {
        /// <summary>How far from the expected place the tries reach.</summary>
        public const int Reach = 24;

        /// <summary>
        /// The nearest text with words in it before or after the node, in document order. Images between have no text, and so do
        /// text in code, pending deletions and opaque MadCap elements, which <see cref="TextRuns"/> leaves out.
        /// </summary>
        public static XmlText NeighborRun(XmlDocument doc, XmlNode node, bool forward)
        {
            var body = doc == null ? null : BlockSelector.FindBody(doc);
            if (body == null) return null;
            var runs = new HashSet<XmlNode>();
            foreach (var r in TextRuns.Of(new List<XmlNode> { body })) runs.Add(r);
            var order = new List<XmlNode>();
            Flatten(body, order);
            var i = order.IndexOf(node);
            if (i < 0) return null;
            for (var j = i + (forward ? 1 : -1); j >= 0 && j < order.Count; j += forward ? 1 : -1)
            {
                var t = order[j] as XmlText;
                if (t != null && runs.Contains(t)) return t;
            }
            return null;
        }

        private static void Flatten(XmlNode n, List<XmlNode> order)
        {
            order.Add(n);
            foreach (XmlNode c in n.ChildNodes) Flatten(c, order);
        }

        /// <summary>
        /// The (start, length) selections to try, best first, for an image that is expected at position <paramref name="at"/>. With
        /// text on both sides the best one is the character before the image through the one after it. With text on one side only
        /// (the image is first or last, or alone in its paragraph) it is the character on that side, and last a caret at the image.
        /// </summary>
        public static List<int[]> Candidates(bool hasPrevious, bool hasNext, int at)
        {
            var tries = new List<int[]>();
            for (var d = 0; d <= Reach; d++)
            {
                foreach (var shift in d == 0 ? new[] { 0 } : new[] { -d, d })
                {
                    var p = at + shift;
                    if (hasPrevious && hasNext) Add(tries, p - 1, 2);
                    else if (hasPrevious) Add(tries, p - 1, 1);
                    else if (hasNext) Add(tries, p, 1);
                }
            }
            for (var d = 0; d <= Reach; d++)
                foreach (var shift in d == 0 ? new[] { 0 } : new[] { -d, d })
                    Add(tries, at + shift, 0);   // Flare reports the image for a caret placed at it
            return tries;
        }

        private static void Add(List<int[]> tries, int start, int length)
        {
            if (start >= 0) tries.Add(new[] { start, length });
        }

        public enum Result { None, Image, Beside }

        /// <summary>
        /// Runs the tries until the editor reports the image (<see cref="Result.Image"/>). When none does, as for an image first or last in
        /// the topic, <paramref name="selectBeside"/> selects the text next to it (<see cref="Result.Beside"/>), else <see cref="Result.None"/>.
        /// </summary>
        public static Result Select(IList<int[]> tries, Func<int, int, bool> selects, Func<bool> selectBeside)
        {
            if (TrySelect(tries, selects)) return Result.Image;
            return selectBeside() ? Result.Beside : Result.None;
        }

        /// <summary>Runs the tries in order until <paramref name="selects"/> says the editor reports the image. False when none does.</summary>
        public static bool TrySelect(IList<int[]> tries, Func<int, int, bool> selects)
        {
            foreach (var t in tries)
                if (selects(t[0], t[1])) return true;
            return false;
        }
    }
}
