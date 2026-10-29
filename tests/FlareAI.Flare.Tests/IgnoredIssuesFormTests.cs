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
    /// <summary>"Ignored Issues": the pane button with its count, and the dialog that lists the ignored issues and restores them.</summary>
    [Collection("Panes")]   // panes share static state (the tour, notices): never two at once
    public class IgnoredIssuesFormTests : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-ignored-" + Guid.NewGuid().ToString("N"));
        public IgnoredIssuesFormTests() { System.IO.Directory.CreateDirectory(_root); FlareAI.Core.Paths.Root = _root; FlareAI.Core.Settings.GuidedTour.Stop(); }
        public void Dispose() { FlareAI.Core.Paths.Root = null; try { System.IO.Directory.Delete(_root, true); } catch (Exception) { } }

        private const string Topic = "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Transfers</h1>" +
                                     "<p>Please note that the fee is waived for members who move money between their own accounts.</p></body></html>";

        private static string OneReword(FlareAI.Core.Llm.LlmRequest r)
        {
            const string Excerpt = "Please note that the fee";
            var run = Regex.Matches(r.User ?? "", @"⟦(r\d+)⟧(.*?)⟦/\1⟧", RegexOptions.Singleline).Cast<Match>().FirstOrDefault(m => m.Groups[2].Value.Contains(Excerpt));
            if (run == null) return "{\"findings\":[]}";
            return "{\"findings\":[{\"run_id\":\"" + run.Groups[1].Value + "\",\"excerpt\":\"" + Excerpt + "\",\"rule_id\":\"general\",\"rule\":\"wordy\"," +
                   "\"severity\":\"suggestion\",\"replacement\":\"The fee\",\"why\":\"Say it directly: cut the throat-clearing so the reader gets the fact first.\"}]}";
        }

        private static StyleFinding Finding(string excerpt)
        {
            return new StyleFinding { Source = "ai", Layer = "ai", Rule = "wordy " + excerpt, Severity = "suggestion", Excerpt = excerpt, Suggestion = "Say it directly.", Replacement = "The" };
        }

        /// <summary>A session that ignored one finding per excerpt, with its dismissed list at <paramref name="dismissedPath"/>.</summary>
        private static ReviewSession IgnoredSession(string dismissedPath, params string[] excerpts)
        {
            var doc = FlareSimDocument.FromXml(Topic);
            var runner = new ActionRunner(new ScriptedLlmClient(), new PromptBuilder(null), 1024);
            var session = new ReviewSession(runner, doc, null, Dismissals.Load(dismissedPath), false)
            {
                IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                RecordFeedback = (f, topic, decision, reason) => null
            };
            var found = excerpts.Select(Finding).ToList();
            session.Add(found);
            foreach (var f in found) { session.Current = f; session.Reject("It's correct"); }
            return session;
        }

        private static void WithForm(ReviewSession session, Action<IgnoredIssuesForm, ListView, Button, Label> body)
        {
            Sta(() =>
            {
                using (var form = new IgnoredIssuesForm(session))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-20000, -20000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Pump(null, 200);
                    var list = All(form).OfType<ListView>().Single();
                    var restore = All(form).OfType<Button>().Single(b => b.Text == "&Restore");
                    var head = All(form).OfType<LiveLabel>().Single();
                    body(form, list, restore, head);
                }
            });
        }

        [DesktopFact]
        public void ListsIssuesWithWhere_RestoreEnabledOnlyWithASelection()
        {
            var session = IgnoredSession(System.IO.Path.Combine(_root, "dismissed.json"), "Please note", "the fee");
            WithForm(session, (form, list, restore, head) =>
            {
                Assert.Equal(new[] { "Issue", "Where" }, list.Columns.Cast<ColumnHeader>().Select(c => c.Text));
                Assert.Equal(2, list.Items.Count);
                Assert.All(list.Items.Cast<ListViewItem>(), i => { Assert.Equal(2, i.SubItems.Count); Assert.IsType<StyleFinding>(i.Tag); });
                Assert.Contains("Please note", list.Items[0].Text);
                Assert.True(list.MultiSelect);
                Assert.Equal(IgnoredIssuesForm.NormalText, head.Text);
                list.SelectedItems.Clear();
                Pump(null, 50);
                Assert.False(restore.Enabled);
                list.Items[0].Selected = true;
                Pump(null, 50);
                Assert.True(restore.Enabled);
            });
        }

        [DesktopFact]
        public void IgnoredButton_ShowsCount_HiddenAtZero()
        {
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(Topic);
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text(OneReword(r))(r)), new PromptBuilder(null), 1024);
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, true);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(500, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Pump(() => list.Items.Count == 1 && All(panel).OfType<ProgressBar>().All(p => !p.Visible), 15000);
                    Assert.DoesNotContain(All(panel).OfType<Button>(), b => b.Visible && b.Text.StartsWith("Ignored Issues", StringComparison.Ordinal));
                    list.Items[0].Selected = true;
                    Pump(null, 200);
                    All(panel).OfType<Button>().Single(b => b.Text.StartsWith("Ignore", StringComparison.Ordinal) && b.Visible).PerformClick();
                    panel.E2EMenuPick("It's correct");
                    Pump(null, 300);
                    Assert.Single(All(panel).OfType<Button>(), b => b.Visible && b.Text == "Ignored Issues (1)");
                }
            });
        }

        [DesktopFact]
        public void RestoringTheLastRow_ShowsTheEmptyText()
        {
            var session = IgnoredSession(System.IO.Path.Combine(_root, "dismissed.json"), "Please note");
            WithForm(session, (form, list, restore, head) =>
            {
                list.Items[0].Selected = true;
                Pump(null, 50);
                restore.PerformClick();
                Pump(null, 100);
                Assert.Empty(list.Items.Cast<ListViewItem>());
                Assert.Contains(IgnoredIssuesForm.EmptyText, head.Text);
                Assert.Contains(ReviewSession.RestoredOne, head.Text);
                Assert.False(restore.Enabled);
                Assert.Empty(session.Ignored);
            });
        }

        [DesktopFact]
        public void SaveFailed_ShowsTheWarningInTheDialog()
        {
            // The dismissed file's folder is a file, so the restore can't be written.
            var blocker = System.IO.Path.Combine(_root, "blocker");
            System.IO.File.WriteAllText(blocker, "x");
            var session = IgnoredSession(System.IO.Path.Combine(blocker, "dismissed.json"), "Please note");
            var old = IgnoredIssuesForm.ReportFailure;
            var reported = new List<string>();
            IgnoredIssuesForm.ReportFailure = (owner, what, ex, action) => reported.Add(what);
            try
            {
                WithForm(session, (form, list, restore, head) =>
                {
                    list.Items[0].Selected = true;
                    Pump(null, 50);
                    restore.PerformClick();
                    Pump(null, 100);
                    Assert.Contains(ReviewSession.RestoredNotSaved, head.Text);
                    Assert.Single(reported);
                });
            }
            finally { IgnoredIssuesForm.ReportFailure = old; }
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

        private static void Pump(Func<bool> done, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (done != null && done()) { Application.DoEvents(); return; }
                Thread.Sleep(10);
            }
            if (done != null) throw new TimeoutException("The pane didn't finish checking.");
        }

        private static void Sta(Action body)
        {
            Exception error = null;
            var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(60000)) throw new TimeoutException("The test kept running (no idle message loop in 60 s).");
            if (error != null) throw new Exception(error.Message, error);
        }
    }
}
