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
                var lines = PaneRows.TextLines(PaneRows.S(paneLogical, scale), scale);
                Assert.Equal(3, lines);
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
            Assert.Equal(3, PaneRows.TextLines(PaneRows.S(150, scale), scale));
            Assert.Equal(3, PaneRows.TextLines(PaneRows.S(259, scale), scale));
            Assert.Equal(2, PaneRows.TextLines(PaneRows.S(300, scale), scale));
            Assert.False(PaneRows.TwoColumns(PaneRows.S(300, scale), scale));
            Assert.Equal(1, PaneRows.TextLines(PaneRows.S(500, scale), scale));
            Assert.True(PaneRows.TwoColumns(PaneRows.S(500, scale), scale));
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(1.25f)]
        public void ARow_HasRoomForItsLines_AndTheMetaLine(float scale)
        {
            using (var font = FontAt(scale))
            using (var meta = new Font(font.FontFamily, font.SizeInPoints - 1f, FontStyle.Regular, GraphicsUnit.Point))
            {
                var three = PaneRows.RowHeight(3, false, font.Height, meta.Height, scale);
                var two = PaneRows.RowHeight(2, false, font.Height, meta.Height, scale);
                var wide = PaneRows.RowHeight(1, true, font.Height, meta.Height, scale);
                Assert.True(three >= 3 * font.Height + meta.Height + PaneRows.S(6, scale));
                Assert.Equal(font.Height, three - two);
                Assert.True(wide > font.Height && wide < two);
                Assert.True(three <= 256);   // an image list's height limit
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
        public void TheCard_GetsWhatItNeeds_TheListWhatIsLeft_AtLeastARow(float scale)
        {
            var listMin = PaneRows.S(100, scale);
            Assert.Equal(PaneRows.S(300, scale), PaneRows.CardHeight(PaneRows.S(300, scale), PaneRows.S(300, scale), 0, PaneRows.S(800, scale), 6, listMin));
            Assert.Equal(PaneRows.S(400, scale) - 6 - listMin, PaneRows.CardHeight(PaneRows.S(350, scale), PaneRows.S(350, scale), 0, PaneRows.S(400, scale), 6, listMin));
            Assert.Equal(PaneRows.S(500, scale), PaneRows.CardHeight(PaneRows.S(300, scale), PaneRows.S(300, scale), PaneRows.S(500, scale), PaneRows.S(800, scale), 6, listMin));   // the writer's drag wins
            Assert.Equal(25, PaneRows.CardHeight(PaneRows.S(300, scale), PaneRows.S(300, scale), 0, PaneRows.S(100, scale), 6, listMin));
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
        public void CardHeight_CapsOnlyTheNewGrowthAtSixtyPercent_UnlessTheWriterDraggedTheSplitter(float scale)
        {
            int S(int px) { return PaneRows.S(px, scale); }
            var listMin = S(60);
            // A short rule in a 500 px pane: today's card height (the old 160 px box) is kept even above 60%.
            Assert.Equal(S(340), PaneRows.CardHeight(S(340), S(340), 0, S(500), 6, listMin));
            // A long rule: the card grows up to 60% of the pane, then scrolls; the list keeps the rest.
            Assert.Equal((int)(0.6 * S(1000)), PaneRows.CardHeight(S(1400), S(380), 0, S(1000), 6, listMin));
            Assert.Equal(S(520), PaneRows.CardHeight(S(520), S(380), 0, S(1000), 6, listMin));   // under 60%: all of it
            // Never smaller than the card with the old short box, even past 60%.
            Assert.Equal(S(380), PaneRows.CardHeight(S(1400), S(380), 0, S(500), 6, listMin));
            // The writer's drag wins over the 60% cap.
            Assert.Equal(S(800), PaneRows.CardHeight(S(1400), S(380), S(800), S(1000), 6, listMin));
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
