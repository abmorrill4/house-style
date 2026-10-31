using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Settings;
using FlareAI.Flare.UI;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>
    /// Onboarding UI (#123) laid out for real: setup's role step, the guided first lint's hint strip in the issues pane at
    /// Flare's narrow docked width, and the getting-started checklist in the empty pane. The writer's data folder is a
    /// temp folder, so no real settings or onboarding state are read or written.
    /// </summary>
    [Collection("Panes")]
    public class OnboardingUiTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hs-ui-" + Guid.NewGuid().ToString("N"));
        private readonly string _prevRoot = FlareAI.Core.Paths.Root;   // the test run's redirected folder, put back on Dispose

        public OnboardingUiTests()
        {
            Directory.CreateDirectory(_root);
            Paths.Root = _root;
            GuidedTour.Stop();
        }

        public void Dispose()
        {
            GuidedTour.Stop();
            Paths.Root = _prevRoot;
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        private const string Ns = "xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"";

        [Fact]
        public void Setup_StartsWithTheRoleQuestion_InFourSteps_AndTheOwnerAnswerLeadsWithTheTeamFolder()
        {
            Sta(() =>
            {
                using (var f = new SetupForm { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false })
                {
                    f.Show();
                    Pump(null, 200);
                    var labels = All(f).OfType<Label>().Where(l => l.Visible).Select(l => l.Text).ToList();
                    Assert.Contains("Step 1 of 4", labels);
                    Assert.Contains("How will you use House Style?", labels);
                    var roles = All(f).OfType<RadioButton>().Where(r => r.Visible).ToList();
                    Assert.Equal(new[] { "To check my own writing", "To set up the style guide for my team" }, roles.Select(r => r.Text).ToArray());
                    Assert.True(roles[0].Checked, "a new writer starts as a writer");
                    var next = All(f).OfType<Button>().Single(b => b.Text == "Next");
                    foreach (var r in roles)
                    {
                        var at = f.RectangleToClient(r.RectangleToScreen(r.ClientRectangle));
                        Assert.True(at.Bottom <= next.Top, "'" + r.Text + "' overlaps the buttons");
                        Assert.True(at.Right <= f.ClientSize.Width, "'" + r.Text + "' is cut off");
                    }
                    Assert.False(All(f).OfType<Button>().Single(b => b.Text == "Back").Visible);

                    roles[1].Checked = true;   // a style guide owner
                    next.PerformClick();       // to AI
                    Pump(null, 100);
                    Assert.Contains("Step 2 of 4", All(f).OfType<Label>().Select(l => l.Text));
                    All(f).OfType<Button>().Single(b => b.Text == "Skip this step").PerformClick();   // to the guide step
                    Pump(null, 100);
                    var visible = All(f).OfType<Label>().Where(l => l.Visible && l.Parent.Visible).Select(l => l.Text).ToList();
                    Assert.Contains("Where is your team's style guide?", visible);
                    Assert.True(All(f).OfType<RadioButton>().Single(r => r.Text == "My team's shared style guide folder").Checked);
                }
            });
        }

        // ---- team feedback folder in setup and Settings (#91) -------------------------------------------------------

        [Fact]
        public void Setup_TeamGuideChoice_ShowsTheFeedbackFolder_OnlyWhenNoneIsSet()
        {
            SaveSettings(new PluginSettings { StyleLayers = "", FeedbackFolder = "" });
            Sta(() =>
            {
                using (var f = NewSetup())
                {
                    ToGuideStep(f);
                    Choose(f, "My team's shared style guide folder");
                    Assert.True(Named(f, "Team feedback folder").Visible, "no feedback folder yet: setup asks for one");
                    Assert.True(Named(f, "Browse for the team feedback folder").Visible);
                    Assert.Contains(VisibleLabels(f), t => t.StartsWith("Your requests to your style guide owners go here.", StringComparison.Ordinal));
                }
            });

            var existing = Path.Combine(_root, "feedback");
            Directory.CreateDirectory(existing);
            SaveSettings(new PluginSettings { StyleLayers = "", FeedbackFolder = existing });
            Sta(() =>
            {
                using (var f = NewSetup())
                {
                    ToGuideStep(f);
                    Choose(f, "My team's shared style guide folder");
                    Assert.False(Named(f, "Team feedback folder").Visible, "a folder is already set: setup doesn't ask again");
                }
            });
        }

        [Fact]
        public void Setup_GoogleChoice_HidesTheFeedbackFolder()
        {
            SaveSettings(new PluginSettings { StyleLayers = "", FeedbackFolder = "" });
            Sta(() =>
            {
                using (var f = NewSetup())
                {
                    ToGuideStep(f);
                    Choose(f, "My team's shared style guide folder");
                    Assert.True(Named(f, "Team feedback folder").Visible);
                    Choose(f, "Google's guide plus my own changes");
                    Assert.False(Named(f, "Team feedback folder").Visible);
                    Assert.False(Named(f, "Browse for the team feedback folder").Visible);
                }
            });
        }

        [Fact]
        public void Setup_FeedbackStatus_ChecksAWritableAndAnUnwritableFolder()
        {
            var guide = NewGuide();
            var writable = Path.Combine(_root, "shared");
            Directory.CreateDirectory(writable);
            var missing = Path.Combine(_root, "not-there");
            SaveSettings(new PluginSettings { StyleLayers = "", FeedbackFolder = "" });
            Sta(() =>
            {
                var f = NewSetup();
                try
                {
                    ToGuideStep(f);
                    Choose(f, "My team's shared style guide folder");
                    var box = (TextBox)Named(f, "Team feedback folder");
                    var status = Named(f, "Team feedback folder status");

                    box.Text = writable; Leave(box);
                    Pump(() => status.Text != "Checking…" && status.Text.Length > 0, 10000);
                    Assert.Equal("✓ Shared with your team", status.Text);

                    box.Text = missing; Leave(box);
                    Pump(() => status.Text != "Checking…" && status.Text != "✓ Shared with your team", 10000);
                    Assert.Equal("Can't write to this folder", status.Text);

                    box.Text = ""; Leave(box);
                    Pump(null, 100);
                    Assert.Equal("", status.Text);

                    // An unwritable folder doesn't stop setup, and the last step says requests wait.
                    box.Text = missing; Leave(box);
                    Pump(() => status.Text == "Can't write to this folder", 10000);
                    ((TextBox)Named(f, "Team style guide folder")).Text = guide;
                    Button(f, "Next").PerformClick();   // checks the guide folder
                    Pump(() => !VisibleLabels(f).Any(t => t.StartsWith("Checking", StringComparison.Ordinal)), 10000);
                    Button(f, "Next").PerformClick();
                    Pump(null, 100);
                    var summary = Named(f, "What was set up").Text;
                    Assert.Contains("Requests: kept on this computer until " + missing + " can be reached.", summary);
                }
                finally { f.Dispose(); }
            });
        }

        [Fact]
        public void Setup_GuideStep_TabOrderAndAccessibleNames()
        {
            SaveSettings(new PluginSettings { StyleLayers = "", FeedbackFolder = "" });
            Sta(() =>
            {
                using (var f = NewSetup())
                {
                    ToGuideStep(f);
                    Choose(f, "My team's shared style guide folder");
                    var order = new[] { "Team style guide folder", "Browse for the team style guide folder", "Team feedback folder",
                                        "Browse for the team feedback folder", "Skip this step", "Back", "Next" };
                    var stops = TabStops(f, All(f).OfType<RadioButton>().Single(r => r.Text == "My team's shared style guide folder"));
                    var at = order.Select(n => stops.IndexOf(n)).ToArray();
                    for (var i = 0; i < order.Length; i++) Assert.True(at[i] >= 0, order[i] + " isn't in the tab order (" + string.Join(", ", stops) + ")");
                    for (var i = 1; i < order.Length; i++) Assert.True(at[i - 1] < at[i], order[i - 1] + " should come before " + order[i] + " (" + string.Join(", ", stops) + ")");
                    Assert.Equal("Team feedback folder status", Named(f, "Team feedback folder status").AccessibleName);
                }
            });
        }

        [Fact]
        public void Settings_FeedbackFolder_IsOnTheStyleGuideTab()
        {
            SaveSettings(new PluginSettings { StyleLayers = NewGuide(), FeedbackFolder = "" });
            Sta(() =>
            {
                using (var f = NewSettings())
                {
                    foreach (var name in new[] { "Team feedback folder", "Browse for the team feedback folder", "Team feedback folder status" })
                    {
                        Control c = Named(f, name);
                        while (!(c is TabPage)) c = c.Parent;
                        Assert.Equal("Style guide", c.Text);
                    }
                    // Below the project check box, and inside the page.
                    var page = (TabPage)Named(f, "Team feedback folder").Parent;
                    var project = All(page).OfType<CheckBox>().Single(b => b.Text.StartsWith("Also use this project's", StringComparison.Ordinal));
                    Assert.True(Named(f, "Team feedback folder").Top > project.Bottom);
                    var status = Named(f, "Team feedback folder status");
                    Pump(() => status.Text.Length > 0 && status.Text != "Checking…", 10000);
                    var lastNote = page.Controls.OfType<Label>().OrderBy(l => l.Bottom).Last();
                    Assert.True(lastNote.Bottom <= page.ClientSize.Height, "the page cuts off its last note (" + lastNote.Bottom + " > " + page.ClientSize.Height + ")");
                }
            });
        }

        [Fact]
        public void Settings_FeedbackStatus_SaysWhereRequestsGo_OnlyWithATeamGuide()
        {
            var guide = NewGuide();
            var writable = Path.Combine(_root, "shared");
            Directory.CreateDirectory(writable);
            var missing = Path.Combine(_root, "not-there");
            var cases = new[]
            {
                new { Layers = guide, Feedback = writable, Want = "✓ Shared with your team. Your requests to your style guide owners go here too." },
                new { Layers = guide, Feedback = missing, Want = "Can't write to this folder. Requests to your style guide owners wait on this computer until it can be reached." },
                new { Layers = guide, Feedback = "", Want = "Personal only. Requests to your style guide owners stay on this computer until you choose a folder." },
                new { Layers = @"{PluginDir}\styleguide", Feedback = writable, Want = "✓ Shared with your team" },
                new { Layers = "", Feedback = writable, Want = "✓ Shared with your team" },
                new { Layers = "", Feedback = missing, Want = "Can't write to this folder" },
                new { Layers = "", Feedback = "", Want = "Personal only" },
            };
            foreach (var c in cases)
            {
                SaveSettings(new PluginSettings { StyleLayers = c.Layers, FeedbackFolder = c.Feedback });
                Sta(() =>
                {
                    using (var f = NewSettings())
                    {
                        var status = Named(f, "Team feedback folder status");
                        Pump(() => status.Text == c.Want, 10000, false);
                        Assert.Equal(c.Want, status.Text);
                        var page = (TabPage)status.Parent;
                        Assert.True(status.Bottom <= page.ClientSize.Height, "the status runs past the page");
                    }
                });
            }

            // Removing the team guide takes the requests sentence away.
            SaveSettings(new PluginSettings { StyleLayers = guide, FeedbackFolder = writable });
            Sta(() =>
            {
                using (var f = NewSettings())
                {
                    var status = Named(f, "Team feedback folder status");
                    Pump(() => status.Text == cases[0].Want, 10000, false);
                    var tabs = All(f).OfType<TabControl>().Single();
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "Style guide");
                    Pump(null, 100);
                    var list = All(f).OfType<ListBox>().Single();
                    list.SelectedIndex = 0;
                    Button(f, "Remove").PerformClick();
                    Pump(() => status.Text == "✓ Shared with your team", 10000, false);
                    Assert.Equal("✓ Shared with your team", status.Text);
                }
            });
        }

        private void SaveSettings(PluginSettings s) { s.Save(); }

        /// <summary>A Flare project folder with no rules: a usable team guide.</summary>
        private string NewGuide()
        {
            var guide = Path.Combine(_root, "guide-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(guide, "Content"));
            File.WriteAllText(Path.Combine(guide, "Team.flprj"), "<CatapultProject />");
            return guide;
        }

        private static SetupForm NewSetup()
        {
            var f = new SetupForm { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
            f.Show();
            Pump(null, 200);
            return f;
        }

        private static SettingsForm NewSettings()
        {
            var f = new SettingsForm { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
            f.Show();
            Pump(null, 200);
            return f;
        }

        private static void ToGuideStep(Form f)
        {
            Button(f, "Next").PerformClick();             // role
            Pump(null, 100);
            Button(f, "Skip this step").PerformClick();   // AI
            Pump(null, 100);
        }

        private static void Choose(Form f, string text)
        {
            All(f).OfType<RadioButton>().Single(r => r.Text == text).Checked = true;
            Pump(null, 100);
        }

        private static Button Button(Form f, string text) { return All(f).OfType<Button>().Single(b => b.Visible && b.Text == text); }

        private static Control Named(Form f, string name) { return All(f).Single(c => c.AccessibleName == name); }

        private static List<string> VisibleLabels(Form f) { return All(f).OfType<Label>().Where(l => l.Visible && l.Parent.Visible).Select(l => l.Text).ToList(); }

        private static void Leave(Control c)
        {
            typeof(Control).GetMethod("OnLeave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(c, new object[] { EventArgs.Empty });
        }

        /// <summary>The names of the controls Tab reaches from <paramref name="start"/>, in order, once round the form.</summary>
        private static List<string> TabStops(Form f, Control start)
        {
            var names = new List<string>();
            var c = start;
            for (var i = 0; i < 200; i++)
            {
                c = f.GetNextControl(c, true);
                if (c == null) { c = f; continue; }
                if (c == start) break;
                if (c.CanSelect && c.TabStop && !(c is Label)) names.Add(!string.IsNullOrEmpty(c.AccessibleName) ? c.AccessibleName : c.Text);
            }
            return names;
        }

        [Theory]
        [InlineData(150, 640)]
        [InlineData(180, 500)]
        public void TourHintStrip_FitsANarrowPane_AboveEverythingElse_WithSkipTour(int width, int height)
        {
            Sta(() =>
            {
                var tour = GuidedTour.Start(true);
                GuidedTour.Notify(TourEvent.TopicLinted);   // step 2 of 5
                using (var form = Host(width, height))
                {
                    var panel = Pane("<p>In order to move money, open the Transfers page.</p>");
                    Show(form, panel);
                    var strip = All(panel).Single(c => c.AccessibleName == "Practice");
                    var text = strip.Controls.OfType<Label>().Single();
                    Func<string, Button> button = t => strip.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => b.Text == t);
                    Assert.True(strip.Visible);
                    Assert.Equal("Step 2 of 5. Select an issue and click Change.", text.Text);
                    Assert.True(button("Skip Tour").Visible);
                    Assert.False(button("Next").Visible);                  // House Style sees this step happen
                    Assert.False(button("Done").Visible);
                    CheckStrip(strip, text, panel, width + "x" + height + " step 2");
                    var status = All(panel).OfType<Label>().First(l => l.Dock == DockStyle.Top && !(l is LinkLabel) && l.Parent == panel);
                    Assert.True(strip.Bottom <= status.Top, "the strip overlaps the status line");

                    // The Review tab step: House Style can't see it, so the strip offers Next.
                    GuidedTour.Notify(TourEvent.Changed); GuidedTour.Notify(TourEvent.Undone); GuidedTour.Notify(TourEvent.RuleDisabled);
                    Pump(null, 100);
                    Assert.Equal(TourStep.ReviewTab, tour.Step);
                    Assert.StartsWith("Step 5 of 5.", text.Text);
                    Assert.True(button("Next").Visible);
                    Assert.True(button("Skip Tour").Visible);
                    CheckStrip(strip, text, panel, width + "x" + height + " step 5");

                    // Next, clicked as the writer clicks it: the finish line with Done, and it stays.
                    button("Next").PerformClick();
                    Pump(null, 300);
                    Assert.Equal(TourStep.Finished, tour.Step);
                    Assert.Same(tour, GuidedTour.Current);
                    Assert.True(strip.Visible, "the strip should stay for the finish line");
                    Assert.Equal("You're ready. Lint Project lints every topic.", text.Text);
                    Assert.True(button("Done").Visible);
                    Assert.False(button("Next").Visible);
                    Assert.False(button("Skip Tour").Visible);
                    CheckStrip(strip, text, panel, width + "x" + height + " finished");

                    // Done ends the tour and hides the strip.
                    button("Done").PerformClick();
                    Pump(null, 300);
                    Assert.Null(GuidedTour.Current);
                    Assert.False(strip.Visible);
                    Assert.True(Onboarding.Load().TourDone);
                    Assert.True(Onboarding.Load().IsDone(ChecklistItem.AcceptOnReviewTab));
                }
            });
        }

        /// <summary>The strip's sentence shows in full and its buttons stay inside the pane.</summary>
        private static void CheckStrip(Control strip, Label text, Control pane, string where)
        {
            where += ": " + strip.Bounds + " in " + pane.ClientSize + " ";
            var at = pane.RectangleToClient(strip.RectangleToScreen(strip.ClientRectangle));
            Assert.True(at.Left >= 0 && at.Right <= pane.ClientSize.Width, where + "runs past the pane's edge");
            var w = text.Width - text.Padding.Horizontal;
            var need = TextRenderer.MeasureText(text.Text, text.Font, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            Assert.True(text.Height - text.Padding.Vertical >= need, where + "the sentence is cut off (" + need + " px of text)");
            foreach (var b in All(strip).OfType<Button>().Where(b => b.Visible))
            {
                var r = strip.RectangleToClient(b.RectangleToScreen(b.ClientRectangle));
                Assert.True(r.Right <= strip.ClientSize.Width && r.Bottom <= strip.ClientSize.Height, where + "'" + b.Text + "' is cut off");
            }
        }

        [Theory]
        [InlineData(150, 640)]
        [InlineData(260, 800)]
        public void EmptyPane_ShowsTheGettingStartedChecklist_ThatFits_AndHideHidesIt(int width, int height)
        {
            Sta(() =>
            {
                using (var form = Host(width, height))
                {
                    var panel = Pane("<p>Open the Transfers page.</p>");
                    Show(form, panel);
                    var list = All(panel).OfType<ListView>().Single();
                    Assert.Equal(0, list.Items.Count);
                    var checklist = All(panel).OfType<LinkLabel>().Single(l => l.AccessibleName == "Getting started");
                    Assert.True(checklist.Visible, "the empty pane should show the checklist");
                    Assert.StartsWith("Getting started", checklist.Text);
                    Assert.Contains("✓ Lint a topic", checklist.Text);              // this lint ticked it
                    Assert.Contains("○ Change an issue", checklist.Text);
                    Assert.Contains("Try an AI rewrite", checklist.Text);
                    CheckFits(checklist, panel, width + "x" + height);

                    Click(checklist, "hide");
                    Pump(null, 100);
                    Assert.False(checklist.Visible);
                    Assert.True(Onboarding.Load().ChecklistHidden);
                }
            });
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        private static Form Host(int width, int height)
        {
            return new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                              FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(width, height), Font = SystemFonts.MessageBoxFont };
        }

        private static FindingsPanel Pane(string body)
        {
            var doc = FlareSimDocument.FromXml("<html " + Ns + "><body><h1>Transfers</h1>" + body + "</body></html>");
            var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r)), new PromptBuilder(null), 1024);
            return new FindingsPanel(runner, doc, () => runner, p => null, null, true);
        }

        private static void Show(Form form, FindingsPanel panel)
        {
            panel.Dock = DockStyle.Fill;
            form.Controls.Add(panel);
            form.Show();
            panel.Begin();
            Pump(() => All(panel).OfType<ProgressBar>().All(p => !p.Visible) && !All(panel).OfType<Label>().Any(l => l.Text == "Linting…"), 15000);
            Pump(null, 200);
        }

        /// <summary>The label shows all of its wrapped text inside the pane: as tall as its text, never wider than the pane.</summary>
        private static void CheckFits(LinkLabel label, Control pane, string where)
        {
            where += ": " + label.Bounds + " in " + pane.ClientSize + " ";
            var screen = pane.RectangleToClient(label.RectangleToScreen(label.ClientRectangle));
            Assert.True(screen.Left >= 0 && screen.Right <= pane.ClientSize.Width, where + "runs past the pane's edge");
            var w = label.Width - label.Padding.Horizontal;
            var text = TextRenderer.MeasureText(label.Text, label.Font, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            Assert.True(label.Height - label.Padding.Vertical >= text, where + "is cut off (" + text + " px of text)");
        }

        /// <summary>Clicks the link whose data is <paramref name="data"/>, as a mouse or keyboard user would.</summary>
        private static void Click(LinkLabel label, string data)
        {
            var link = label.Links.Cast<LinkLabel.Link>().Single(l => (l.LinkData as string) == data);
            typeof(LinkLabel).GetMethod("OnLinkClicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(label, new object[] { new LinkLabelLinkClickedEventArgs(link) });
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

        /// <param name="mustFinish">False: a timeout returns, so the assertion after it says what was there instead.</param>
        private static void Pump(Func<bool> done, int ms = 15000, bool mustFinish = true)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (done != null && done()) { Application.DoEvents(); return; }
                Thread.Sleep(10);
            }
            if (done != null && mustFinish) throw new TimeoutException("The pane didn't finish linting.");
        }

        private static void Sta(Action body)
        {
            Exception error = null;
            // A failed step can leave a form unable to dispose, and that error then hides the step's: keep the first
            // assertion or timeout raised on the test's thread too.
            Exception first = null;
            Thread ui = null;
            var raised = new List<string>();
            var other = false;   // anything raised on the test's thread besides the dispose error below
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> seen = (s, e) =>
            {
                if (Thread.CurrentThread != ui) return;
                if (!IsDisposeRace(e.Exception)) other = true;
                if (raised.Count < 8) raised.Add(e.Exception.GetType().Name + ": " + e.Exception.Message + " at " + (e.Exception.StackTrace ?? "").Split('\n').FirstOrDefault());
                if (first == null && (e.Exception is Xunit.Sdk.XunitException || e.Exception is TimeoutException)) first = e.Exception;
            };
            var t = ui = new Thread(() =>
            {
                // The dialogs' background checks finish through this thread's context: without one they would end on a
                // pool thread and touch the dialog from there.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                try { body(); } catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            AppDomain.CurrentDomain.FirstChanceException += seen;
            try
            {
                t.Start();
                if (!t.Join(180000)) throw new TimeoutException("The UI test didn't finish in 180 s.");   // a loaded CI runner took the suite from 50 s to 2 min (2026-10-29)
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= seen; }
            if (error != null && first != null && first != error) throw new Exception(first.Message + " (then: " + error.Message + ")", first);
            // On the CI runner (no interactive desktop), disposing a dialog at the end of a test sometimes throws this with
            // every assertion passed and nothing else raised. Only then (no other exception at all on the test's thread, so
            // the body ran to its end) it doesn't fail the test.
            if (error != null && !other && IsDisposeRace(error)) return;
            if (error != null) throw new Exception(error.Message + " Raised on the test's thread: " + string.Join(" | ", raised.ToArray()), error);
        }

        private static bool IsDisposeRace(Exception ex)
        {
            return ex is InvalidOperationException && ex.Message.Contains("while doing CreateHandle") &&
                   (ex.StackTrace ?? Environment.StackTrace).Contains("Control.Dispose");
        }
    }
}
