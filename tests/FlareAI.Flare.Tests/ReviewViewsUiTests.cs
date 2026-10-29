using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using FlareAI.Core.Actions;
using FlareAI.Flare.UI;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>The review pane's Level and Source filters (#249): the combos, Clear Filters, the remembered choice.</summary>
    [Collection("Panes")]   // panes share static state (the tour, notices): never two at once
    public class ReviewViewsUiTests : IDisposable
    {
        // The remembered choice is a file under the data folder: a temp folder, never the real settings.
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-views-" + Guid.NewGuid().ToString("N"));
        public ReviewViewsUiTests() { System.IO.Directory.CreateDirectory(_root); FlareAI.Core.Paths.Root = _root; FlareAI.Core.Settings.GuidedTour.Stop(); }
        public void Dispose() { FlareAI.Core.Paths.Root = null; try { System.IO.Directory.Delete(_root, true); } catch (Exception) { } }

        private const string TwoParagraphs = "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Transfers</h1>" +
                                             "<p>Please note that the fee is waived for members who move money between their own accounts.</p>" +
                                             "<p>In order to move money, open the Transfers page.</p></body></html>";

        private static string Rewords(FlareAI.Core.Llm.LlmRequest r)
        {
            var found = new List<string>();
            foreach (var pair in new[] { new[] { "Please note that the fee", "The fee" }, new[] { "In order to", "To" } })
            {
                var run = Regex.Matches(r.User ?? "", @"⟦(r\d+)⟧(.*?)⟦/\1⟧", RegexOptions.Singleline).Cast<Match>().FirstOrDefault(m => m.Groups[2].Value.Contains(pair[0]));
                if (run != null)
                    found.Add("{\"run_id\":\"" + run.Groups[1].Value + "\",\"excerpt\":\"" + pair[0] + "\",\"rule_id\":\"general\",\"rule\":\"wordy\"," +
                              "\"severity\":\"suggestion\",\"replacement\":\"" + pair[1] + "\",\"why\":\"Say it directly.\"}");
            }
            return "{\"findings\":[" + string.Join(",", found) + "]}";
        }

        /// <summary>A pane over the two-reword topic, done checking, on an off-screen form.</summary>
        private static void WithPane(int width, Action<FindingsPanel, Form, ListView> body)
        {
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(TwoParagraphs);
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text(Rewords(r))(r)), new PromptBuilder(null), 1024);
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, true);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(width, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    // A narrowed list may show no rows, so wait for the check to finish (the status leaves "Linting…").
                    Pump(() => All(panel).OfType<ProgressBar>().All(p => !p.Visible) && !Status(panel).Contains("Linting"), 15000);
                    body(panel, form, list);
                }
            });
        }

        private static ComboBox Combo(Control panel, string name) { return All(panel).OfType<ComboBox>().Single(c => c.AccessibleName == name); }

        private static string Status(FindingsPanel p)
        {
            var state = p.E2EState();
            return (string)state.GetType().GetProperty("status").GetValue(state, null);
        }

        [Theory]
        [InlineData(150)]
        [InlineData(260)]
        public void Filters_WrapAt150px_AndHaveAccessibleNames(int width)
        {
            WithPane(width, (panel, form, list) =>
            {
                var level = Combo(panel, "Level");
                var source = Combo(panel, "Source");
                Assert.True(level.Width <= 140 && source.Width <= 140);
                Assert.True(level.Visible && source.Visible);
                Assert.Equal(new[] { "All Levels", "Errors", "Warnings", "Suggestions" }, level.Items.Cast<object>().Select(i => i.ToString()).ToArray());
                Assert.Equal(new[] { "Rules and AI", "Rules Only", "AI Only" }, source.Items.Cast<object>().Select(i => i.ToString()).ToArray());
                Assert.True(level.Right <= level.Parent.ClientSize.Width && source.Right <= source.Parent.ClientSize.Width);
                if (width == 150) Assert.True(source.Top >= level.Bottom, "Source should be on its own line below Level");
            });
        }

        [Fact]
        public void StatusSaysShowingNOfM_AndClearFiltersAppears()
        {
            FlareAI.Core.Actions.ReviewViewPrefs.Save(ReviewLevel.Suggestions, ReviewSource.RulesAndAi);
            WithPane(260, (panel, form, list) =>
            {
                Assert.StartsWith("Showing 2 of 2 issues.", Status(panel));
                var clear = All(panel).OfType<Button>().Single(b => b.Text == "Clear Filters");
                Assert.True(clear.Visible);
                clear.PerformClick();
                Pump(null, 200);
                Assert.False(clear.Visible);
                Assert.Equal("All Levels", Combo(panel, "Level").Text);
            });
        }

        [Fact]
        public void FilterIsRemembered()
        {
            WithPane(260, (panel, form, list) =>
            {
                Assert.False(All(panel).OfType<Button>().Single(b => b.Text == "Clear Filters").Visible);
                Combo(panel, "Level").SelectedIndex = 2;
                Combo(panel, "Source").SelectedIndex = 1;
            });
            WithPane(260, (panel, form, list) =>
            {
                Assert.Equal("Warnings", Combo(panel, "Level").Text);
                Assert.Equal("Rules Only", Combo(panel, "Source").Text);
            });
        }

        [Fact]
        public void TabOrder_LevelSourceClearFilters()
        {
            WithPane(260, (panel, form, list) =>
            {
                var level = Combo(panel, "Level");
                var source = Combo(panel, "Source");
                var clear = All(panel).OfType<Button>().Single(b => b.Text == "Clear Filters");
                Assert.True(level.TabIndex < source.TabIndex);
                Assert.True(source.TabIndex < clear.TabIndex);
            });
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

        private static void Pump(Func<bool> done, int ms = 15000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (done != null && done()) { Application.DoEvents(); return; }
                Thread.Sleep(10);
            }
            if (done != null) throw new TimeoutException("The check didn't finish.");
        }

        private static void Sta(Action body)
        {
            Exception error = null;
            var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(60000)) throw new TimeoutException("The pane kept laying itself out (no idle message loop in 60 s).");
            if (error != null) throw new Exception(error.Message, error);
        }
    }
}
