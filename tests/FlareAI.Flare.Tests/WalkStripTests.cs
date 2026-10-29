using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using FlareAI.Flare.UI;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>The strip above the list while one rule is walked: counter, clamped reason, buttons, tip, finished state.</summary>
    [Collection("Panes")]
    public class WalkStripTests
    {
        private const string LongReason = "Contractions read as friendly and spoken, which is the tone this guide asks for. Spelling out every word makes " +
                                          "the sentence stiff, slower to read and harder to scan, so use the short form unless the sentence needs to stress a word " +
                                          "or the reader may be unfamiliar with the short form, and then say why in a note nearby.";

        [Fact]
        public void Strip_ShowsRuleCounterAndTip_WrapsAt150px_ClampsTheReason_KeepsListRows()
        {
            Sta(() =>
            {
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(150, 600), Font = SystemFonts.MessageBoxFont })
                {
                    var list = new ListView { Dock = DockStyle.Fill, View = View.Details };
                    var strip = new WalkStrip();
                    form.Controls.Add(list);
                    form.Controls.Add(strip);
                    form.Show();
                    strip.ShowWalk("Use contractions", "warning", LongReason, "do not use\nuse don't", 12, 48, 36, null);
                    Application.DoEvents();

                    var labels = All(strip).OfType<Label>().ToList();
                    Assert.Equal("Rule", strip.AccessibleName);
                    Assert.Contains(labels, l => l.Text == "Use contractions");
                    Assert.Contains(labels, l => l.Text == "▲ Warning");
                    Assert.Contains(labels, l => l.Text == "12 of 48");
                    Assert.Contains(labels, l => l.Text == "Press Enter to change, I to ignore, or N for the next one.");
                    Assert.Contains(labels, l => l.Text == "✗ do not use\n✓ use don't");
                    Assert.Contains(All(strip).OfType<Button>(), b => b.Visible && b.Text == "Change All (36)…");
                    Assert.Contains(All(strip).OfType<Button>(), b => b.Visible && b.Text == "All Rules");
                    Assert.DoesNotContain(All(strip).OfType<Button>(), b => b.Visible && b.Text == "Next Rule");

                    var reason = labels.Single(l => l.Text == LongReason);
                    Assert.True(reason.Height <= reason.Font.Height * 3 + 1, "The reason shows at most 3 lines.");
                    var more = All(strip).OfType<LinkLabel>().Single(l => l.Visible && l.Text == "More");
                    var closed = strip.Height;
                    Assert.True(closed < 450, "The strip is " + closed + " px tall.");
                    Assert.False(strip.AutoScroll);
                    Assert.True(list.Height >= 100, "The list keeps rows.");
                    Assert.All(All(strip).Where(c => c.Visible), c => Assert.True(c.Right <= strip.ClientSize.Width && c.Bottom <= strip.Height, c.Text));

                    typeof(LinkLabel).GetMethod("OnLinkClicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(more, new object[] { new LinkLabelLinkClickedEventArgs(new LinkLabel.Link()) });
                    Application.DoEvents();
                    Assert.True(reason.Height > reason.Font.Height * 3);
                    Assert.Contains(All(strip).OfType<LinkLabel>(), l => l.Visible && l.Text == "Less");

                    strip.SetChangeAllCount(0);
                    Assert.DoesNotContain(All(strip).OfType<Button>(), b => b.Visible && b.Text.StartsWith("Change All"));

                    strip.ShowFinished("Every place in this rule is done.", true);
                    Assert.Contains(All(strip).OfType<Button>(), b => b.Visible && b.Text == "Next Rule");
                    Assert.Contains(All(strip).OfType<Label>(), l => l.Visible && l.Text == "Every place in this rule is done.");
                }
            });
        }

        [Fact]
        public void CounterIsALiveRegion()
        {
            Sta(() =>
            {
                var strip = new WalkStrip();
                strip.ShowWalk("Rule", "error", "Why.", "", 12, 48, 0, null);
                var counter = All(strip).OfType<LiveLabel>().Single();
                Assert.Equal("12 of 48", counter.Text);
                Assert.Equal("12 of 48", counter.AccessibilityObject.Name ?? counter.Text);
                strip.SetCounter(13, 48);
                Assert.Equal("13 of 48", counter.Text);
            });
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

        private static void Sta(Action body)
        {
            Exception error = null;
            var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(60000)) throw new TimeoutException("The UI test didn't finish in 60 s.");
            if (error != null) throw new Exception(error.Message, error);
        }
    }
}
