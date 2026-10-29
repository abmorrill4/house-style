using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using FlareAI.Core.Actions;
using FlareAI.Flare.UI;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>The strip above the list while one rule is walked: counter, clamped reason, buttons, tip, finished state.</summary>
    [Collection("Panes")]   // panes share static state (the tour, notices): never two at once
    public class WalkStripTests : IDisposable
    {
        // The writer's data folder (onboarding notes, checklist) is a temp folder: a pane test never writes to %APPDATA%.
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-strip-" + Guid.NewGuid().ToString("N"));
        public WalkStripTests() { System.IO.Directory.CreateDirectory(_root); FlareAI.Core.Paths.Root = _root; FlareAI.Core.Settings.GuidedTour.Stop(); }
        public void Dispose() { FlareAI.Core.Paths.Root = null; try { System.IO.Directory.Delete(_root, true); } catch (Exception) { } }

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

        // ---- the finished strip: Next Rule and All Rules ----------------------------------------------------------------

        private const string TwoSentences = "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Transfers</h1>" +
                                            "<p>Please note that the fee is waived for members who move money between their own accounts.</p>" +
                                            "<p>In order to move money, open the Transfers page.</p></body></html>";

        [Fact]
        public void Finished_ShowsMessageAndNextRule()
        {
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(TwoSentences);
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r)), new PromptBuilder(null), 1024, new TwoRuleGuide());
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, false);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(260, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Pump(() => list.Items.Count == 2 && All(panel).OfType<ProgressBar>().All(p => !p.Visible), 15000);
                    var strip = All(panel).OfType<WalkStrip>().Single();
                    var session = (ReviewSession)typeof(FindingsPanel).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);

                    // Two rules, one place each: walk the first, past its last place.
                    var first = session.Backlog()[0];
                    session.ReviewRule(first);
                    Pump(null, 200);
                    Assert.True(strip.Visible, "the walk shows the strip");
                    Assert.Equal("1 of 1", CounterOf(strip));
                    session.Next();
                    Pump(null, 200);
                    Assert.True(session.WalkFinished);
                    Assert.True(strip.Visible, "the finished walk keeps the strip");
                    Assert.Contains(session.WalkFinishedText, Shown(strip));
                    Assert.True(ButtonNamed(strip, "Next Rule").Visible, "another rule is left, so Next Rule is shown");
                    Assert.True(ButtonNamed(strip, "All Rules").Visible);

                    // Next Rule walks the second rule; past its last place there is none left.
                    ButtonNamed(strip, "Next Rule").PerformClick();
                    Pump(null, 200);
                    Assert.NotEqual(first.Key, session.FocusRule);
                    Assert.Equal("1 of 1", CounterOf(strip));
                    session.Next();
                    Pump(null, 200);
                    Assert.True(session.WalkFinished);
                    Assert.False(ButtonNamed(strip, "Next Rule").Visible, "no rule is left, so Next Rule is hidden");
                    Assert.True(Shown(strip).Any(t => t.EndsWith("You've been through every rule.", StringComparison.Ordinal)), "the finished strip says every rule is done");
                    Assert.True(ButtonNamed(strip, "All Rules").Visible);
                }
            });
        }

        // ---- keys and focus in the walk ---------------------------------------------------------------------------------

        private const string ThreePlaces = "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body>" +
                                           "<p>In order to save, press the button.</p><p>In order to load, press the other button.</p>" +
                                           "<p>In order to quit, close the window.</p></body></html>";

        [DesktopFact]   // keyboard focus needs an interactive desktop (as the other focus tests in the file)
        public void ListHasFocusAfterEachWalkKey_AndAfterAnOpen()
        {
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(ThreePlaces);
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r)), new PromptBuilder(null), 1024, new TwoRuleGuide());
                var editor = new TextBox();
                var opened = new List<string>();
                // The host takes the focus into the editor when a file is opened, as Flare does; the walk must give the list the focus back.
                var panel = new FindingsPanel(runner, doc, () => runner, path => { opened.Add(path); editor.Focus(); return doc; }, null, false);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(260, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Controls.Add(editor);
                    form.Show();
                    form.Activate();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Pump(() => list.Items.Count == 3 && All(panel).OfType<ProgressBar>().All(p => !p.Visible), 15000);
                    var strip = All(panel).OfType<WalkStrip>().Single();
                    var session = (ReviewSession)typeof(FindingsPanel).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);

                    // One rule with three places: walk it from the first.
                    var rule = session.Backlog().Single();
                    list.Focus();
                    session.ReviewRule(rule);
                    Pump(null, 200);
                    Assert.Equal("1 of 3", CounterOf(strip));

                    // I with no reason yet opens the reasons menu; the reason picked ignores place 1 and gives the list the focus back.
                    Press(list, Keys.I);
                    panel.E2EMenuPick("It's correct");
                    Pump(null, 100);
                    Assert.Equal("2 of 3", CounterOf(strip));
                    AssertListFocused(panel, rule.Key, 2, 3, false);

                    // N goes to the next place (place 2 is passed over, not ignored).
                    Press(list, Keys.N);
                    Assert.Equal("3 of 3", CounterOf(strip));
                    AssertListFocused(panel, rule.Key, 3, 3, false);

                    // Shift+Enter opens the file through the host, which takes the focus; the list gets it back.
                    Press(list, Keys.Enter | Keys.Shift);
                    Assert.Single(opened);
                    AssertListFocused(panel, rule.Key, 3, 3, false);

                    // I again uses the last reason: place 3 is the last, so the walk is finished.
                    Press(list, Keys.I);
                    AssertListFocused(panel, rule.Key, 0, 3, true);   // no place is current once the walk is finished
                    Assert.True(session.WalkFinished);

                    // Enter on the finished strip: no rule is left, so it is All Rules, and the walk ends.
                    Assert.False(session.HasNextRule);
                    Press(list, Keys.Enter);
                    Assert.Null(WalkState(panel));
                    Assert.True(list.Focused, "the list has the focus after All Rules");
                }
            });
        }

        /// <summary>Checks the E2E walk object: the rule key, the place, the total, finished, and that the list has the focus.</summary>
        private static void AssertListFocused(FindingsPanel panel, string rule, int position, int total, bool finished)
        {
            var walk = WalkState(panel);
            Assert.NotNull(walk);
            Assert.Equal(new[] { "finished", "listFocused", "position", "rule", "total" },
                         walk.GetType().GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(rule, Prop(walk, "rule"));
            Assert.Equal(position, Prop(walk, "position"));
            Assert.Equal(total, Prop(walk, "total"));
            Assert.Equal(finished, Prop(walk, "finished"));
            Assert.True((bool)Prop(walk, "listFocused"), "the list has the focus after the key");
        }

        /// <summary>The walk object of the pane's E2E state (null when no rule is walked).</summary>
        private static object WalkState(FindingsPanel panel) { return Prop(panel.E2EState(), "walk"); }

        private static object Prop(object o, string name) { return o.GetType().GetProperty(name).GetValue(o); }

        /// <summary>Presses a key in the control as the keyboard does: its KeyDown runs, which is where the walk's keys are handled.</summary>
        private static void Press(Control c, Keys key)
        {
            typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, new object[] { new KeyEventArgs(key) });
            Pump(null, 100);
        }

        /// <summary>A guide with two rules, one per sentence of <see cref="TwoSentences"/>: "Please note that" and "In order to".</summary>
        private sealed class TwoRuleGuide : FlareAI.Core.Style.IStyleService
        {
            private const string FirstId = "company:test/throat-clearing", SecondId = "company:test/in-order";
            private static readonly FlareAI.Core.Style.GuideRule First = new FlareAI.Core.Style.GuideRule
            {
                Id = FirstId, Layer = "company", Section = "Word list",
                Text = "Don't start a sentence with throat-clearing phrases such as please note that. Say the fact directly.",
            };
            private static readonly FlareAI.Core.Style.GuideRule Second = new FlareAI.Core.Style.GuideRule
            {
                Id = SecondId, Layer = "company", Section = "Word list",
                Text = "Use 'to' rather than 'in order to'.",
            };

            public IList<FlareAI.Core.Style.LintHit> Lint(IList<string> texts, IList<int> headings, string project)
            {
                var hits = new List<FlareAI.Core.Style.LintHit>();
                for (var i = 0; i < texts.Count; i++)
                {
                    Hit(hits, texts[i], i, "Please note that", FirstId, "throat-clearing");
                    Hit(hits, texts[i], i, "In order to", SecondId, "wordy");
                }
                return hits;
            }
            private static void Hit(List<FlareAI.Core.Style.LintHit> hits, string text, int index, string phrase, string ruleId, string rule)
            {
                var at = text.IndexOf(phrase, StringComparison.Ordinal);
                if (at < 0) return;
                hits.Add(new FlareAI.Core.Style.LintHit { Index = index, Start = at, Length = phrase.Length, Match = phrase, Rule = rule, RuleId = ruleId,
                                                          Severity = "warning", Layer = "company", Message = "Say the fact directly, without a lead-in phrase." });
            }
            public IList<FlareAI.Core.Style.LintHit> Lint(IList<FlareAI.Core.Style.LintLine> lines, string project) { return Lint(lines.Select(l => l.Text).ToList(), new List<int>(), project); }
            public string Guide(string project) { return ""; }
            public string[] GuideFor(string project, FlareAI.Core.Style.GuideScope scope) { return new[] { "", "" }; }
            public IList<string> RuleIdsFor(string project, FlareAI.Core.Style.GuideScope scope) { return new List<string> { FirstId, SecondId }; }
            public IList<FlareAI.Core.Style.GuideRule> Rules(string project) { return new List<FlareAI.Core.Style.GuideRule> { First, Second }; }
            public bool IsOff(string project, string ruleId) { return false; }
            public FlareAI.Core.Style.GuideReport Validate(string project) { return new FlareAI.Core.Style.GuideReport(); }
            public IList<FlareAI.Core.Style.GuideExample> Examples(string project) { return new List<FlareAI.Core.Style.GuideExample>(); }
        }

        /// <summary>The counter's text ("12 of 48").</summary>
        private static string CounterOf(Control strip) { return All(strip).OfType<LiveLabel>().Single().Text; }

        /// <summary>The strip's visible labels' text.</summary>
        private static List<string> Shown(Control strip) { return All(strip).OfType<Label>().Where(l => l.Visible).Select(l => l.Text).ToList(); }

        private static Button ButtonNamed(Control strip, string text) { return All(strip).OfType<Button>().Single(b => b.Text == text); }

        /// <summary>Runs the message loop until <paramref name="done"/> (or for <paramref name="ms"/> when null).</summary>
        private static void Pump(Func<bool> done, int ms = 15000, Func<string> state = null)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (done != null && done()) { Application.DoEvents(); return; }
                Thread.Sleep(10);
            }
            if (done != null) throw new TimeoutException("The check didn't finish: " + (state == null ? "" : state()));
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
