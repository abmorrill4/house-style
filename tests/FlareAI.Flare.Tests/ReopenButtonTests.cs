using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>The details card's Reopen Issue button: on a row marked as fixed by hand (#447), and nowhere else.</summary>
    [Collection("Panes")]
    public class ReopenButtonTests : IDisposable
    {
        private const string Topic = "<html><head /><body><h1>Fees</h1><p>Please utilize the tool.</p></body></html>";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hs-reopen-" + Guid.NewGuid().ToString("N"));
        private readonly string _prevRoot = FlareAI.Core.Paths.Root;

        public ReopenButtonTests()
        {
            Directory.CreateDirectory(_dir);
            FlareAI.Core.Paths.Root = _dir;
            FlareAI.Core.Settings.GuidedTour.Stop();
        }

        public void Dispose()
        {
            FlareAI.Core.Paths.Root = _prevRoot;
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

        [Fact]
        public void TheCardOffersReopenIssue_OnlyOnARowMarkedAsFixed()
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var doc = FlareSimDocument.FromXml(Topic);
                    var runner = new ActionRunner(new ScriptedLlmClient(), new PromptBuilder(null), 1024);
                    using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-20000, -20000), ShowInTaskbar = false, ClientSize = new System.Drawing.Size(420, 700) })
                    using (var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, false))
                    {
                        panel.Dock = DockStyle.Fill; form.Controls.Add(panel); form.Show();
                        var session = (ReviewSession)typeof(FindingsPanel).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                        session.RecordFeedback = (f, topic, decision, reason) => null;
                        session.IgnoredAiRules = p => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var issue = new StyleFinding { Source = "lint", Layer = "google", Rule = "Google.WordList", Severity = "warning", Excerpt = "utilize",
                                                       Before = "Please ", Replacement = "use", Suggestion = "Use plain words.", Section = "Fees" };
                        session.Add(new List<StyleFinding> { issue });
                        var list = All(panel).OfType<ListView>().Single();
                        var button = All(panel).OfType<Button>().Single(b => b.Parent.AccessibleName == "Decision for the selected issue" && (b.Text == "Reopen Issue" || b.Text == "Remove Exemption"));
                        list.Items[0].Selected = true;
                        Application.DoEvents();
                        Assert.False(button.Visible, "an open row has no Reopen Issue");

                        session.Current = issue;
                        session.MarkFixed();
                        list.Items[0].Selected = true;
                        Application.DoEvents();
                        Assert.True(issue.IsFixed && issue.MarkedFixed);
                        Assert.True(button.Visible, "a row marked as fixed offers Reopen Issue");
                        Assert.Equal("Reopen Issue", button.Text);

                        button.PerformClick();
                        Application.DoEvents();
                        Assert.False(issue.IsFixed);
                        Assert.False(button.Visible, "the button goes away once the row is open");
                        Assert.Contains("Reopened.", session.Status);
                    }
                }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(180000)) throw new TimeoutException("The UI test didn't finish in 180 s.");
            if (error != null) throw new Exception(error.Message, error);
        }
    }
}
