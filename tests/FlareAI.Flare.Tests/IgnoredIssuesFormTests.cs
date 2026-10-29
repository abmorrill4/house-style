using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Flare.UI;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Llm;
using Xunit;

namespace FlareAI.Flare.Tests
{
    /// <summary>The Ignored Issues dialog: what it lists, when Restore is on, what it says after a restore, and that it stays open.</summary>
    [Collection("Panes")]
    public class IgnoredIssuesFormTests : IDisposable
    {
        private const string Topic = "<html><head /><body><h1>Fees</h1><p>Please utilize the tool. Then utilize the menu.</p></body></html>";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hs-ignored-" + Guid.NewGuid().ToString("N"));
        private readonly string _dismissedPath;

        public IgnoredIssuesFormTests()
        {
            Directory.CreateDirectory(_dir);
            _dismissedPath = Path.Combine(_dir, "dismissed.json");
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(_dismissedPath)) File.SetAttributes(_dismissedPath, FileAttributes.Normal);
                Directory.Delete(_dir, true);
            }
            catch (Exception) { }
        }

        private static StyleFinding Issue(string excerpt, string before)
        {
            return new StyleFinding { Source = "lint", Layer = "google", Rule = "Google.WordList", Severity = "warning", Excerpt = excerpt, Before = before,
                                      Replacement = "use", Suggestion = "Use plain words.", Section = "Fees" };
        }

        /// <summary>A session whose two issues were dismissed earlier, so adding them parks them in Ignored.</summary>
        private ReviewSession SessionWithTwoIgnored()
        {
            var doc = FlareSimDocument.FromXml(Topic);
            var runner = new ActionRunner(new ScriptedLlmClient(), new PromptBuilder(null), 1024);
            var dismissed = Dismissals.Load(_dismissedPath);
            var found = new List<StyleFinding> { Issue("utilize", "Please "), Issue("utilize", "Then ") };
            foreach (var f in found) dismissed.Dismiss(f, doc.SourcePath);
            var session = new ReviewSession(runner, doc, new QuietHost(), dismissed, false)
            {
                RecordFeedback = (f, topic, decision, reason) => null,
                IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
            session.Add(found);
            Assert.Equal(2, session.Ignored.Count);
            return session;
        }

        [Fact]
        public void ListsIssuesWithWhere_RestoreEnabledOnlyWithASelection()
        {
            Sta(() =>
            {
                var session = SessionWithTwoIgnored();
                using (var f = Offscreen(new IgnoredIssuesForm(session)))
                {
                    f.Show();
                    Pump(null, 100);
                    Assert.Equal("House Style — Ignored Issues", f.Text);
                    var list = All(f).OfType<ListView>().Single();
                    var restore = All(f).OfType<Button>().Single(b => b.AccessibleName == "Restore");
                    Assert.Equal(new[] { "Issue", "Where" }, list.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToArray());
                    Assert.True(list.MultiSelect);
                    Assert.Equal(2, list.Items.Count);
                    Assert.Equal("Fees", list.Items[0].SubItems[1].Text);
                    Assert.Contains("utilize", list.Items[0].Text);
                    Assert.Same(session.Ignored[0], list.Items[0].Tag);
                    Assert.False(restore.Enabled, "nothing is selected");
                    Assert.Contains("Restore one to see it in the list again", Intro(f).Text);

                    list.Items[0].Selected = true;
                    Assert.True(restore.Enabled);
                    list.Items[0].Selected = false;
                    Assert.False(restore.Enabled);
                }
            });
        }

        [Fact]
        public void RestoringTheLastRow_ShowsTheEmptyText()
        {
            Sta(() =>
            {
                var session = SessionWithTwoIgnored();
                using (var f = Offscreen(new IgnoredIssuesForm(session)))
                {
                    f.Show();
                    Pump(null, 100);
                    var list = All(f).OfType<ListView>().Single();
                    var restore = All(f).OfType<Button>().Single(b => b.AccessibleName == "Restore");

                    list.Items[0].Selected = true;
                    restore.PerformClick();
                    Assert.True(f.Visible, "the dialog stays open");
                    Assert.Equal(1, list.Items.Count);
                    Assert.StartsWith(ReviewSession.RestoredOneText, Intro(f).Text);
                    Assert.Equal(1, session.Ignored.Count);

                    list.Items[0].Selected = true;
                    restore.PerformClick();
                    Assert.Equal(0, list.Items.Count);
                    Assert.Contains("You haven't ignored any issues that are still in the text.", Intro(f).Text);
                    Assert.False(restore.Enabled);
                    Assert.True(f.Visible);
                }
            });
        }

        [Fact]
        public void SaveFailed_ShowsTheWarningInTheDialog()
        {
            Sta(() =>
            {
                var session = SessionWithTwoIgnored();
                var warned = 0;
                var old = IgnoredIssuesForm.ShowSaveFailed;
                IgnoredIssuesForm.ShowSaveFailed = owner => warned++;
                try
                {
                    File.SetAttributes(_dismissedPath, FileAttributes.ReadOnly);
                    using (var f = Offscreen(new IgnoredIssuesForm(session)))
                    {
                        f.Show();
                        Pump(null, 100);
                        var list = All(f).OfType<ListView>().Single();
                        list.Items[0].Selected = true;
                        All(f).OfType<Button>().Single(b => b.AccessibleName == "Restore").PerformClick();
                        Assert.StartsWith(ReviewSession.SaveFailedText, Intro(f).Text);
                        Assert.Equal(1, warned);
                        Assert.Equal(1, list.Items.Count);
                        Assert.True(f.Visible);
                    }
                }
                finally { IgnoredIssuesForm.ShowSaveFailed = old; }
            });
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static Label Intro(Form f) { return All(f).OfType<LiveLabel>().Single(); }

        private static Form Offscreen(Form f)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-20000, -20000);
            f.ShowInTaskbar = false;
            return f;
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
            if (done != null) throw new TimeoutException("The dialog didn't finish.");
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

        /// <summary>A host that never answers anything: these tests only restore.</summary>
        private sealed class QuietHost : IReviewHost
        {
            public bool Confirm(string message, string action) { return false; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<FlareAI.Core.Style.RuleRef> otherRules) { return null; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { return null; }
            public string AskText(string title, string label, string initial, bool multiline) { return null; }
            public IDocumentAdapter OpenDocument(string path) { return null; }
            public bool PreviewBatch(BatchPlan plan) { return false; }
            public bool SaveAndClose(IDocumentAdapter doc) { return false; }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { }
        }
    }
}
