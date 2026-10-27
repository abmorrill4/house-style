using System;
using System.Collections.Generic;
using System.Drawing;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// The review pane's sizing rules, as pure functions so they can be tested at 100% and 125%. Flare docks the pane at
    /// about 150 logical pixels and ignores the width the plugin asks for, so the pane must read well that narrow:
    /// list rows wrap (the suggestion on up to 3 lines, then a smaller grey line saying where it comes from or how many),
    /// buttons wrap their text instead of being cut off, and a wide pane (420+ logical px) keeps two one-line columns.
    /// </summary>
    internal static class PaneRows
    {
        /// <summary>Below this width (logical px) rows get 3 lines of text and the "make it wider" tip shows.</summary>
        public const int NarrowWidth = 260;

        /// <summary>From this width (logical px) the list has two one-line columns, as on a wide screen.</summary>
        public const int WideWidth = 420;

        /// <summary>The ellipsis that ends a row's text when it doesn't fit.</summary>
        public const string Ellipsis = "…";

        /// <summary>Pixels at this DPI for a length designed at 96 DPI.</summary>
        public static int S(int px, float scale) { return (int)Math.Round(px * (scale <= 0 ? 1f : scale)); }

        /// <summary>A width in pixels as logical (96 DPI) pixels.</summary>
        public static int Logical(int px, float scale) { return (int)Math.Round(px / (scale <= 0 ? 1f : scale)); }

        /// <summary>True when the list is wide enough for two one-line columns (suggestion | source).</summary>
        public static bool TwoColumns(int listWidth, float scale) { return Logical(listWidth, scale) >= WideWidth; }

        /// <summary>Lines of suggestion text a row shows before the ellipsis: 3 when narrow, 2 in between, 1 when wide.</summary>
        public static int TextLines(int listWidth, float scale)
        {
            var w = Logical(listWidth, scale);
            return w >= WideWidth ? 1 : w < NarrowWidth ? 3 : 2;
        }

        /// <summary>
        /// The height of every row (a ListView's rows are all one height): one line plus padding when two columns,
        /// else <paramref name="textLines"/> lines of text, the smaller meta line, and padding above, between and below.
        /// </summary>
        public static int RowHeight(int textLines, bool twoColumns, int fontHeight, int metaFontHeight, float scale)
        {
            if (twoColumns) return fontHeight + S(6, scale);
            return Math.Max(1, textLines) * fontHeight + metaFontHeight + S(9, scale);
        }

        /// <summary>True when the pane is narrow enough that the "drag this pane's edge" tip helps (and has been laid out).</summary>
        public static bool ShowWidthTip(int paneWidth, float scale)
        {
            return paneWidth > S(40, scale) && Logical(paneWidth, scale) < NarrowWidth;
        }

        /// <summary>
        /// A button's size: its one-line size when that fits <paramref name="available"/>, else the full available width and
        /// tall enough for its wrapped text (<paramref name="wrappedTextHeight"/>), so nothing is cut off in a narrow pane.
        /// </summary>
        public static Size ButtonSize(Size oneLineText, int wrappedTextHeight, int available, float scale)
        {
            var w = Math.Max(oneLineText.Width + S(18, scale), S(44, scale));
            var h = Math.Max(oneLineText.Height + S(10, scale), S(24, scale));
            if (w <= available || available <= 0) return new Size(w, h);
            return new Size(available, Math.Max(h, wrappedTextHeight + S(10, scale)));
        }

        /// <summary>The width to wrap a button's text at when it gets <paramref name="available"/> pixels.</summary>
        public static int ButtonTextWidth(int available, float scale) { return Math.Max(1, available - S(14, scale)); }

        /// <summary>
        /// The height of a wrapped label: its measured text, at least <paramref name="minLines"/> lines, plus a little room;
        /// never more than <paramref name="maxHeight"/> (0 = no limit), where the label ends with an ellipsis instead.
        /// </summary>
        public static int LabelHeight(int measuredText, int fontHeight, int minLines, int maxHeight, float scale)
        {
            var h = Math.Max(measuredText, fontHeight * Math.Max(0, minLines)) + S(4, scale);
            if (maxHeight > 0 && h > maxHeight) h = Math.Max(fontHeight + S(4, scale), maxHeight);
            return h;
        }

        public static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }

        /// <summary>The tallest the why text gets (logical px), so a huge team-guide row can't break the control.</summary>
        public const int WhyCeiling = 30000;

        /// <summary>The ceiling in device pixels at any DPI: Windows controls stop working past about 32,767 px.</summary>
        public const int WhyCeilingMax = 32000;

        /// <summary>
        /// The card's parts in a card <paramref name="cardHeight"/> tall: what changes gets its full height when there is room,
        /// shrinking to <paramref name="diffMin"/> (two lines) before why goes under <paramref name="whyMin"/>. Why takes the rest,
        /// and always at least its whole text (<paramref name="whyContent"/>, up to <paramref name="whyCeiling"/>), so it never
        /// needs a scroll bar of its own. Returns { diff, why, total }: the parts stack heading, diff, buttons, why with
        /// <paramref name="gap"/> between them, so nothing overlaps; a total above <paramref name="cardHeight"/> means the card scrolls.
        /// </summary>
        public static int[] CardLayout(int cardHeight, int top, int head, int buttons, int diffMin, int diffFull, int whyMin, int whyContent, int whyCeiling, int gap)
        {
            var remaining = cardHeight - top - head - buttons - 3 * gap;
            var diff = Clamp(remaining - whyMin, diffMin, Math.Max(diffMin, diffFull));
            var why = Math.Max(Math.Max(whyMin, remaining - diff), Math.Min(whyContent, whyCeiling));
            return new[] { diff, why, top + head + gap + diff + gap + why + gap + buttons };
        }

        /// <summary>The share of the pane a card may grow to on its own before it scrolls, so the list keeps a few rows.</summary>
        public const double CardShare = 0.6;

        /// <summary>
        /// The card's height under the list: what it needs (<paramref name="wanted"/>) or what the writer dragged it to
        /// (<paramref name="byWriter"/>, 0 = never), leaving the list at least <paramref name="listMin"/>; never under 25 px.
        /// A card that grows for a long rule stops at 60% of the pane and scrolls, but never gets smaller than it would be
        /// with its why text held to the old short box (<paramref name="wantedShort"/>), so ordinary cards keep their height.
        /// </summary>
        public static int CardHeight(int wanted, int wantedShort, int byWriter, int splitHeight, int splitterWidth, int listMin)
        {
            var room = splitHeight - splitterWidth - listMin;
            var want = byWriter > 0 ? byWriter : Math.Min(wanted, Math.Max((int)(CardShare * splitHeight), wantedShort));
            return Math.Max(25, Math.Min(want, room));
        }

        /// <summary>
        /// Wraps <paramref name="text"/> into at most <paramref name="maxLines"/> lines no wider than <paramref name="maxWidth"/>
        /// (as <paramref name="measure"/> says): breaks between words, splits a word only when it is wider than a line, and ends
        /// the last line with an ellipsis when the text doesn't fit. Always returns at least one line.
        /// </summary>
        public static List<string> Wrap(string text, Func<string, int> measure, int maxWidth, int maxLines)
        {
            var lines = new List<string>();
            maxLines = Math.Max(1, maxLines);
            maxWidth = Math.Max(1, maxWidth);
            var rest = (text ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (rest.Length > 0)
            {
                if (lines.Count == maxLines - 1)
                {
                    lines.Add(measure(rest) <= maxWidth ? rest : Ellipsize(rest, measure, maxWidth));
                    break;
                }
                var n = FitPrefix(rest, measure, maxWidth);
                lines.Add(rest.Substring(0, n).TrimEnd());
                rest = rest.Substring(n).TrimStart();
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        /// <summary>One line: the text, or as much of it as fits followed by an ellipsis.</summary>
        public static string Ellipsize(string text, Func<string, int> measure, int maxWidth)
        {
            text = text ?? "";
            if (measure(text) <= maxWidth) return text;
            int lo = 0, hi = text.Length - 1;   // the longest prefix that fits with the ellipsis
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (measure(text.Substring(0, mid).TrimEnd() + Ellipsis) <= maxWidth) lo = mid; else hi = mid - 1;
            }
            return text.Substring(0, lo).TrimEnd() + Ellipsis;
        }

        /// <summary>The length of the longest start of <paramref name="text"/> that fits on a line: whole words when possible.</summary>
        private static int FitPrefix(string text, Func<string, int> measure, int maxWidth)
        {
            if (measure(text) <= maxWidth) return text.Length;
            var best = 0;
            for (var i = text.IndexOf(' '); i > 0; i = text.IndexOf(' ', i + 1))
            {
                if (measure(text.Substring(0, i)) <= maxWidth) best = i; else break;
            }
            if (best > 0) return best;
            int lo = 1, hi = text.Length;   // one word wider than the line: as many characters as fit, at least one
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (measure(text.Substring(0, mid)) <= maxWidth) lo = mid; else hi = mid - 1;
            }
            return lo;
        }
    }
}
