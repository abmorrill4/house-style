using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Flare.UI;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// The review pane at the width Flare docks it (~150 logical px) and wider, at 100% and 125%: rows wrap instead of
    /// being cut off, buttons wrap their text, and a wide pane keeps two one-line columns.
    /// </summary>
    public class PaneRowsTests
    {
        private const string RuleRow = "▲ Avoid 'guaranteed', 'coming soon', 'best-in-class' and other claims you can't back up in product descriptions";
        private const TextFormatFlags OneLine = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        private static Font FontAt(float scale) { var f = SystemFonts.MessageBoxFont; return new Font(f.FontFamily, f.SizeInPoints * scale, f.Style, GraphicsUnit.Point); }

        private static Func<string, int> Measure(Font font) { return s => TextRenderer.MeasureText(s, font, Size.Empty, OneLine).Width; }

        [Theory]
        [InlineData(1f, 150)]
        [InlineData(1.25f, 150)]
        [InlineData(1f, 200)]
        [InlineData(1.25f, 200)]
        public void ARuleRow_WrapsToThePaneWidth_AndEndsWithAnEllipsis(float scale, int paneLogical)
        {
            using (var font = FontAt(scale))
            {
                var width = PaneRows.S(paneLogical - 2 * 6 - 2 * 4 - 4, scale);   // pane padding, row padding, list border
                const int lines = 3;   // Wrap still wraps to a given number of lines; the pane no longer picks it
                var wrapped = PaneRows.Wrap(RuleRow, Measure(font), width, lines);

                Assert.Equal(3, wrapped.Count);
                Assert.All(wrapped, l => Assert.True(Measure(font)(l) <= width, "'" + l + "' is wider than " + width));
                Assert.EndsWith(PaneRows.Ellipsis, wrapped[2]);
                var shown = string.Join(" ", wrapped).TrimEnd('…').Replace(" ", "");
                Assert.StartsWith(shown, RuleRow.Replace(" ", ""));   // nothing reordered or dropped, only the end cut
                Assert.StartsWith("▲ Avoid", wrapped[0]);              // the severity glyph stays first
            }
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void ShortText_StaysOnOneLine_Unchanged(float scale)
        {
            using (var font = FontAt(scale))
            {
                var wrapped = PaneRows.Wrap("● \"utilize\" → \"use\"", Measure(font), PaneRows.S(130, scale), 3);
                Assert.Equal(new[] { "● \"utilize\" → \"use\"" }, wrapped.ToArray());
            }
        }

        [Fact]
        public void AWordWiderThanTheLine_IsSplit_NotCutOff()
        {
            Func<string, int> sevenPx = s => s.Length * 7;
            var wrapped = PaneRows.Wrap("see Content/Resources/Snippets/VeryLongSnippetName.flsnp now", sevenPx, 70, 3);
            Assert.All(wrapped, l => Assert.True(sevenPx(l) <= 70));
            Assert.Equal("see", wrapped[0]);
            Assert.Equal("Content/Re", wrapped[1]);
            Assert.EndsWith("…", wrapped[2]);
        }

        [Fact]
        public void EmptyText_IsOneEmptyLine()
        {
            Assert.Equal(new[] { "" }, PaneRows.Wrap(null, s => s.Length, 50, 3).ToArray());
            Assert.Equal("…", PaneRows.Ellipsize("abcdef", s => s.Length * 10, 5));
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void TheRowLayout_FollowsTheLogicalWidth(float scale)
        {
            Assert.False(PaneRows.TwoColumns(PaneRows.S(150, scale), scale));
            Assert.False(PaneRows.TwoColumns(PaneRows.S(300, scale), scale));
            Assert.False(PaneRows.TwoColumns(PaneRows.S(479, scale), scale));
            Assert.True(PaneRows.TwoColumns(PaneRows.S(480, scale), scale));
            Assert.True(PaneRows.TwoColumns(PaneRows.S(500, scale), scale));
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void ARow_IsOneLine_AndACompactRowIsAtLeastTwentyFourPixels(float scale)
        {
            using (var font = FontAt(scale))
            {
                var compact = PaneRows.RowHeight(false, font.Height, scale);
                var wide = PaneRows.RowHeight(true, font.Height, scale);
                Assert.True(compact >= PaneRows.S(24, scale));
                Assert.Equal(font.Height + PaneRows.S(6, scale), wide);
                Assert.True(compact < 2 * font.Height);   // one line, not two
            }
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void ALongButton_WrapsInsideANarrowPane_AShortOneKeepsItsSize(float scale)
        {
            using (var font = FontAt(scale))
            {
                var available = PaneRows.S(150 - 12 - 4, scale);
                foreach (var text in new[] { "Change All Issues (128)", "Show Skipped Rules (128)" })
                {
                    var one = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPrefix);
                    var wrapped = TextRenderer.MeasureText(text, font, new Size(PaneRows.ButtonTextWidth(available, scale), 0), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Height;
                    var size = PaneRows.ButtonSize(one, wrapped, available, scale);
                    Assert.True(size.Width <= available, text + ": " + size.Width + " > " + available);
                    Assert.True(size.Height >= 2 * font.Height, text + " should wrap onto two lines");
                }
                var skip = TextRenderer.MeasureText("Skip", font, Size.Empty, TextFormatFlags.NoPrefix);
                var s = PaneRows.ButtonSize(skip, skip.Height, available, scale);
                Assert.True(s.Width < available / 2);
                Assert.True(s.Height < 2 * font.Height);
            }
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void TheWidthTip_ShowsOnlyWhenTheLaidOutPaneIsNarrow(float scale)
        {
            Assert.True(PaneRows.ShowWidthTip(PaneRows.S(150, scale), scale));
            Assert.True(PaneRows.ShowWidthTip(PaneRows.S(259, scale), scale));
            Assert.False(PaneRows.ShowWidthTip(PaneRows.S(260, scale), scale));
            Assert.False(PaneRows.ShowWidthTip(PaneRows.S(400, scale), scale));
            Assert.False(PaneRows.ShowWidthTip(0, scale));   // not laid out yet
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void TheCard_GivesUpDiffHeightFirst_NeverGoesUnderTwoLines_AndScrollsWhenShort(float scale)
        {
            int S(int px) { return PaneRows.S(px, scale); }
            var line = PaneRows.S(15, scale);
            int top = S(6), head = 2 * line + S(6), buttons = S(164), gap = S(4), diffMin = 2 * line + S(8), diffFull = S(64), whyMin = 3 * line + S(6);

            var tall = PaneRows.CardLayout(S(600), top, head, buttons, diffMin, diffFull, whyMin, 0, PaneRows.S(PaneRows.WhyCeiling, scale), gap);
            Assert.Equal(diffFull, tall[0]);                           // room: what changes at full size…
            Assert.Equal(S(600), tall[2]);                             // …and why takes the rest, exactly
            var tight = PaneRows.CardLayout(top + head + buttons + diffMin + whyMin + 3 * gap + S(10), top, head, buttons, diffMin, diffFull, whyMin, 0, PaneRows.S(PaneRows.WhyCeiling, scale), gap);
            Assert.Equal(diffMin + S(10), tight[0]);                    // short: the diff shrinks before why does
            Assert.Equal(whyMin, tight[1]);
            var tiny = PaneRows.CardLayout(S(93), top, head, buttons, diffMin, diffFull, whyMin, 0, PaneRows.S(PaneRows.WhyCeiling, scale), gap);
            Assert.Equal(diffMin, tiny[0]);                            // never under two lines
            Assert.Equal(whyMin, tiny[1]);
            Assert.True(tiny[2] > S(93));                              // the card scrolls instead of stacking parts on each other
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void CardLayout_GivesWhyItsWholeText_AndTheTotalExceedsTheCard_WhenItDoesNotFit(float scale)
        {
            int S(int px) { return PaneRows.S(px, scale); }
            var line = S(15);
            int top = S(6), head = 2 * line + S(6), buttons = S(164), gap = S(4), diffMin = 2 * line + S(8), diffFull = S(64), whyMin = 3 * line + S(6);
            var ceiling = S(PaneRows.WhyCeiling);

            var shortRule = PaneRows.CardLayout(S(600), top, head, buttons, diffMin, diffFull, whyMin, 2 * line, ceiling, gap);
            Assert.Equal(S(600), shortRule[2]);                        // a short rule: why takes the rest, as before
            var longRule = 30 * line;
            var tall = PaneRows.CardLayout(S(600), top, head, buttons, diffMin, diffFull, whyMin, longRule, ceiling, gap);
            Assert.Equal(longRule, tall[1]);                           // the whole rule, no box of its own to scroll
            Assert.Equal(diffFull, tall[0]);                           // what changes keeps its full size
            Assert.True(tall[2] > S(600));                             // the card scrolls instead
            Assert.Equal(top + head + gap + tall[0] + gap + tall[1] + gap + buttons, tall[2]);
            var shortPane = PaneRows.CardLayout(S(300), top, head, buttons, diffMin, diffFull, whyMin, longRule, ceiling, gap);
            Assert.Equal(longRule, shortPane[1]);
            Assert.Equal(diffMin, shortPane[0]);                       // a short pane still gives up diff height first
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void CardLayout_LimitsWhyToTheCeiling(float scale)
        {
            int S(int px) { return PaneRows.S(px, scale); }
            var line = S(15);
            var ceiling = S(PaneRows.WhyCeiling);
            var huge = PaneRows.CardLayout(S(600), S(6), 2 * line, S(164), 2 * line, S(64), 3 * line, ceiling * 3, ceiling, S(4));
            Assert.Equal(ceiling, huge[1]);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void DrawerHeight_IsTheBarWhenHidden_TheDefaultShareOrTheWritersHeightWhenOpen(float scale)
        {
            int S(int px) { return PaneRows.S(px, scale); }
            var bar = S(26);
            var listMin = S(60);
            var split = S(400);
            // Hidden: just the bar, whatever the writer chose.
            Assert.Equal(bar, PaneRows.DrawerHeight(false, 0, split, 6, bar, listMin));
            Assert.Equal(bar, PaneRows.DrawerHeight(false, S(200), split, 6, bar, listMin));
            // Open, no writer height: the list gets 60%, the drawer the rest less the divider.
            Assert.Equal(split - (int)Math.Round(0.6 * split) - 6, PaneRows.DrawerHeight(true, 0, split, 6, bar, listMin));
            // A writer's height wins.
            Assert.Equal(S(150), PaneRows.DrawerHeight(true, S(150), split, 6, bar, listMin));
            // Never so tall that the list drops under one row.
            Assert.Equal(split - 6 - listMin, PaneRows.DrawerHeight(true, S(900), split, 6, bar, listMin));
            // Never under the bar.
            Assert.Equal(bar, PaneRows.DrawerHeight(true, 1, split, 6, bar, listMin));
            Assert.Equal(bar, PaneRows.DrawerHeight(true, 0, S(40), 6, bar, listMin));
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void DrawerHeight_KeepsFiveListRows_AndAUsableCard_ByDefault(float scale)
        {
            int S(int px) { return PaneRows.S(px, scale); }
            var bar = S(26);
            var listMin = S(28);
            var five = 5 * S(24) + S(4);
            // A 60% list would show fewer than five rows: the drawer gives way, down to its card minimum.
            var split = S(200);
            var h = PaneRows.DrawerHeight(true, 0, split, 6, bar, listMin, five);
            Assert.Equal(split - 6 - five, h);
            // A tiny area: the list keeps its rows and the drawer shrinks to its bar, never under it.
            var tiny = S(120);
            Assert.Equal(bar, PaneRows.DrawerHeight(true, 0, tiny, 6, bar, listMin, five));
            // The writer's height ignores the preferred rows.
            Assert.Equal(S(150), PaneRows.DrawerHeight(true, S(150), S(400), 6, bar, listMin, five));
        }

        [Fact]
        public void EllipsizeMiddle_KeepsTheStartAndTheEnd_SoTheFileNameSurvives()
        {
            Func<string, int> tenPx = s => s.Length * 10;
            const string path = "▾ Content/Topics/Reference/Administration/Settings.htm · 4 issues";
            Assert.Equal("short", PaneRows.EllipsizeMiddle("short", tenPx, 100, 2));   // fits: unchanged
            var cut = PaneRows.EllipsizeMiddle(path, tenPx, 200, 2);
            Assert.True(tenPx(cut) <= 200);
            Assert.StartsWith("▾ ", cut);                                // the first keepStart characters
            Assert.Contains(PaneRows.Ellipsis, cut);
            Assert.EndsWith(path.Substring(path.Length - 10), cut);        // the end survives
            Assert.Equal(20, cut.Length);                                  // as much of the end as fits
            var fileName = PaneRows.EllipsizeMiddle("▾ Content/A/B/C/D/E/F/Settings.htm", tenPx, 200, 2);
            Assert.EndsWith("Settings.htm", fileName);
            // Even one character of the end is kept when that is all that fits.
            Assert.Equal("ab…z", PaneRows.EllipsizeMiddle("abcdefghijklmnopqrstuvwxyz", tenPx, 40, 2));
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void AStatusLabel_FitsItsText_UpToItsLimit(float scale)
        {
            var fh = PaneRows.S(15, scale);
            Assert.Equal(2 * fh + PaneRows.S(4, scale), PaneRows.LabelHeight(fh, fh, 2, 0, scale));          // short: two lines kept
            Assert.Equal(6 * fh + PaneRows.S(4, scale), PaneRows.LabelHeight(6 * fh, fh, 2, 0, scale));      // long: all of it
            Assert.Equal(PaneRows.S(100, scale), PaneRows.LabelHeight(20 * fh, fh, 2, PaneRows.S(100, scale), scale));   // capped
        }
    }
}
