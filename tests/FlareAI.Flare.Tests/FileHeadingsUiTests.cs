using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
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
    /// <summary>Lint Project, By file: each file has a heading row that collapses and expands its issues (#305).</summary>
    [Collection("Panes")]   // panes share static state (the tour, notices): never two at once
    public class FileHeadingsUiTests : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-heads-" + Guid.NewGuid().ToString("N"));
        private readonly string _prevRoot = FlareAI.Core.Paths.Root;
        public FileHeadingsUiTests() { System.IO.Directory.CreateDirectory(_root); FlareAI.Core.Paths.Root = _root; FlareAI.Core.Settings.GuidedTour.Stop(); }
        public void Dispose() { FlareAI.Core.Paths.Root = _prevRoot; try { System.IO.Directory.Delete(_root, true); } catch (Exception) { } }

        private const string Down = "▾", Right = "▸", Dot = "·";

        private static string TopicOf(int i)
        {
            return "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Transfers " + i + "</h1>" +
                   "<p>Please note that the fee " + i + " is waived for members who move money between their own accounts.</p>" +
                   "<p>Please note that card " + i + " replacements are free when the card is lost or stolen.</p></body></html>";
        }

        private static readonly string SingleTopic = TopicOf(9);

        /// <summary>A project report over three files (t0, t1, t2), done linting, with the By file view on or off.</summary>
        private void WithProject(bool byFile, Action<FindingsPanel, ListView> body)
        {
            var settings = FlareAI.Core.Settings.PluginSettings.Load();
            settings.ProjectViewByFile = byFile;
            settings.Save();
            var proj = System.IO.Path.Combine(_root, "proj");
            var content = System.IO.Path.Combine(proj, "Content", "billpay");
            System.IO.Directory.CreateDirectory(content);
            System.IO.File.WriteAllText(System.IO.Path.Combine(proj, "Billpay.flprj"), "");
            var files = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var f = System.IO.Path.Combine(content, "t" + i + ".htm");
                System.IO.File.WriteAllText(f, TopicOf(i));
                files.Add(f);
            }
            Sta(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r)), new PromptBuilder(null), 1024, new PleaseNoteGuide());
                var panel = FindingsPanel.ForProject(runner, FlareSimDocument.FromXml(SingleTopic), () => runner, p => null, files, "Billpay");
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(300, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    Pump(() => All(panel).OfType<Label>().Any(l => (l.Text ?? "").Contains("Linted ")) && All(panel).OfType<ProgressBar>().All(p => !p.Visible),
                         30000, () => string.Join(" | ", All(panel).OfType<Label>().Select(l => l.Text).Where(t => !string.IsNullOrEmpty(t))));
                    Pump(null, 200);
                    body(panel, All(panel).OfType<ListView>().Single());
                }
            });
        }

        private static ReviewSession SessionOf(FindingsPanel panel)
        {
            return (ReviewSession)typeof(FindingsPanel).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
        }

        private static List<ListViewItem> Items(ListView list) { return list.Items.Cast<ListViewItem>().ToList(); }
        private static bool IsIssue(ListViewItem i) { return i.Tag is StyleFinding; }
        private static List<ListViewItem> Headings(ListView list) { return Items(list).Where(i => !IsIssue(i)).ToList(); }
        private static List<ListViewItem> IssuesIn(ListView list, string path) { return Items(list).Where(i => IsIssue(i) && ((StyleFinding)i.Tag).DocumentPath == path).ToList(); }
        private static object Prop(object o, string name) { return o.GetType().GetProperty(name).GetValue(o); }
        private static string CardHead(FindingsPanel p) { return (string)Prop(Prop(p.E2EState(), "card"), "head"); }

        private static List<string> FilePaths(FindingsPanel panel)
        {
            return SessionOf(panel).Findings.Select(f => f.DocumentPath).Distinct().ToList();
        }

        private static void Select(ListView list, ListViewItem item)
        {
            list.Focus();
            list.SelectedItems.Clear();
            item.Selected = true; item.Focused = true;
            Pump(null, 150);
        }

        private static void Press(ListView list, Keys key)
        {
            typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, new object[] { new KeyEventArgs(key) });
            Pump(null, 150);
        }

        private static Button ButtonStarting(FindingsPanel panel, string text)
        {
            var field = text == "Change" ? "_accept" : "_reject";
            return (Button)typeof(FindingsPanel).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
        }

        [Fact]
        public void ByFile_ShowsAHeadingBeforeEachFilesIssues()
        {
            WithProject(true, (panel, list) =>
            {
                var items = Items(list);
                var heads = Headings(list);
                Assert.Equal(3, heads.Count);
                var paths = FilePaths(panel);
                Assert.Equal(3, paths.Count);
                for (var h = 0; h < 3; h++)
                {
                    var at = items.IndexOf(heads[h]);
                    var own = IssuesIn(list, paths[h]);
                    Assert.NotEmpty(own);
                    for (var k = 0; k < own.Count; k++) Assert.Same(own[k], items[at + 1 + k]);
                    var m = Regex.Match(heads[h].Text, "^" + Down + " billpay\\\\t" + h + "\\.htm " + Dot + " (\\d+) issues?$");
                    Assert.True(m.Success, heads[h].Text);
                    Assert.Equal(own.Count, int.Parse(m.Groups[1].Value));
                }
                Assert.Equal(items.Count, heads.Count + items.Count(IsIssue));
                Assert.Single(list.SelectedItems.Cast<ListViewItem>());
                Assert.True(IsIssue(list.SelectedItems[0]), "the first selected row is an issue");
            });
        }

        [Fact]
        public void Collapsing_TheMiddleFile_LeavesOnlyItsHeading_AndKeepsTheCount()
        {
            WithProject(true, (panel, list) =>
            {
                var paths = FilePaths(panel);
                var before = Items(list).Select(i => i.Tag).ToList();
                var middleHeadText = Headings(list)[1].Text;
                var middle = IssuesIn(list, paths[1]);

                panel.E2EClickHeading("t1.htm");
                Pump(null, 150);

                var heads = Headings(list);
                Assert.Equal(3, heads.Count);
                Assert.Empty(IssuesIn(list, paths[1]));
                Assert.Equal(before.Count - middle.Count, list.Items.Count);
                Assert.Equal(Right + middleHeadText.Substring(1), heads[1].Text);   // only the arrow changes, the count stays
                Assert.Equal(IssuesIn(list, paths[0]).Select(i => i.Tag), before.OfType<StyleFinding>().Where(f => f.DocumentPath == paths[0]).Cast<object>());
                Assert.Equal(IssuesIn(list, paths[2]).Select(i => i.Tag), before.OfType<StyleFinding>().Where(f => f.DocumentPath == paths[2]).Cast<object>());
                Assert.All(SessionOf(panel).Findings, f => Assert.False(f.IsFixed));
                Assert.True(SessionOf(panel).IsCollapsed(paths[1]));

                panel.E2EClickHeading("t1.htm");
                Pump(null, 150);
                Assert.Equal(middle.Count, IssuesIn(list, paths[1]).Count);
                Assert.Equal(middleHeadText, Headings(list)[1].Text);
            });
        }

        [Fact]
        public void ClickRow_WithAnIssuesText_SelectsThatIssue_AndDoesNotCollapse()
        {
            WithProject(true, (panel, list) =>
            {
                var issues = Items(list).Where(IsIssue).ToList();
                var unique = issues.FirstOrDefault(r => Items(list).Count(i => i.Text.IndexOf(r.Text, StringComparison.Ordinal) >= 0) == 1);
                var row = unique ?? issues[1];
                var needle = row.Text;
                var headings = Headings(list).Count;
                var count = list.Items.Count;

                var shown = panel.E2EClickHeading(needle);
                Pump(null, 150);

                Assert.Equal(row.Text, shown);
                Assert.Single(list.SelectedItems.Cast<ListViewItem>());
                Assert.True(IsIssue(list.SelectedItems[0]));
                Assert.Equal(shown, list.SelectedItems[0].Text);
                if (unique != null) Assert.Same(row.Tag, list.SelectedItems[0].Tag);
                Assert.Equal(headings, Headings(list).Count);
                Assert.Equal(count, list.Items.Count);
                foreach (var path in FilePaths(panel)) Assert.False(SessionOf(panel).IsCollapsed(path));
            });
        }

        [Fact]
        public void ClickRow_WithNoMatch_ThrowsNoRow_AndListsRows()
        {
            WithProject(true, (panel, list) =>
            {
                var ex = Assert.Throws<InvalidOperationException>(() => panel.E2EClickHeading("zz-no-such-row"));
                Assert.StartsWith("No row ", ex.Message);
                Assert.Contains(Headings(list)[0].Text, ex.Message);
            });
        }

        [Fact]
        public void Collapsing_TheSelectedIssuesFile_SelectsTheHeading()
        {
            WithProject(true, (panel, list) =>
            {
                var paths = FilePaths(panel);
                Select(list, IssuesIn(list, paths[1])[0]);
                Assert.NotEqual("Select an issue to see what changes and why.", CardHead(panel));
                Assert.True(ButtonStarting(panel, "Change").Enabled || ButtonStarting(panel, "Ignore").Enabled);

                Press(list, Keys.Left);

                Assert.Single(list.SelectedItems.Cast<ListViewItem>());
                var selected = list.SelectedItems[0];
                Assert.False(IsIssue(selected));
                Assert.StartsWith(Right + " billpay\\t1.htm", selected.Text);
                Assert.Equal("Select an issue to see what changes and why.", CardHead(panel));
                Assert.False(ButtonStarting(panel, "Change").Enabled);
                Assert.False(ButtonStarting(panel, "Ignore").Enabled);
                Assert.Contains("collapse with Left and Right", list.AccessibleDescription);
            });
        }

        [Fact]
        public void LeftRightEnter_OnHeadingsAndRows()
        {
            WithProject(true, (panel, list) =>
            {
                var session = SessionOf(panel);
                var paths = FilePaths(panel);
                var total = session.Findings.Count;

                Select(list, Headings(list)[0]);
                Press(list, Keys.Enter);
                Assert.True(session.IsCollapsed(paths[0]), "Enter collapses");
                Assert.False(IsIssue(list.SelectedItems[0]));
                Press(list, Keys.Enter);
                Assert.False(session.IsCollapsed(paths[0]), "Enter expands");

                Select(list, Headings(list)[0]);
                Press(list, Keys.Left);
                Assert.True(session.IsCollapsed(paths[0]), "Left collapses");
                Press(list, Keys.Left);
                Assert.True(session.IsCollapsed(paths[0]), "Left on a collapsed heading keeps it collapsed");
                Press(list, Keys.Right);
                Assert.False(session.IsCollapsed(paths[0]), "Right expands");
                Assert.NotEmpty(IssuesIn(list, paths[0]));

                Select(list, IssuesIn(list, paths[2])[0]);
                Press(list, Keys.Left);
                Assert.True(session.IsCollapsed(paths[2]), "Left on an issue collapses its file");
                Assert.False(IsIssue(list.SelectedItems[0]), "and selects its heading");

                Assert.Equal(total, session.Findings.Count);
                Assert.All(session.Findings, f => Assert.False(f.IsFixed));
            });
        }

        [Fact]
        public void RefreshAfterIgnoringInAnotherFile_KeepsTheFileCollapsed()
        {
            WithProject(true, (panel, list) =>
            {
                var session = SessionOf(panel);
                var paths = FilePaths(panel);
                panel.E2EClickHeading("t1.htm");
                Pump(null, 150);
                var total = session.Findings.Count;

                Select(list, IssuesIn(list, paths[0])[0]);
                session.Reject("It's correct");
                Pump(null, 200);

                Assert.Equal(total - 1, session.Findings.Count);
                Assert.True(session.IsCollapsed(paths[1]));
                Assert.Empty(IssuesIn(list, paths[1]));
                Assert.StartsWith(Right + " billpay\\t1.htm", Headings(list)[1].Text);
                Assert.True(session.Findings.Any(f => f.DocumentPath == paths[1]), "the collapsed file's issues are still in the report");
            });
        }

        [Fact]
        public void ByRule_AndTopicPane_HaveNoHeadingRows()
        {
            WithProject(false, (panel, list) =>
            {
                Assert.NotEmpty(Items(list));
                Assert.DoesNotContain(RowFlags(panel), heading => heading);
            });
            Sta(() =>
            {
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r)), new PromptBuilder(null), 1024, new PleaseNoteGuide());
                var panel = new FindingsPanel(runner, FlareSimDocument.FromXml(SingleTopic), () => runner, p => null, null, false);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(300, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Pump(() => list.Items.Count > 0 && All(panel).OfType<ProgressBar>().All(p => !p.Visible), 15000);
                    Assert.NotEmpty(RowFlags(panel));
                    Assert.DoesNotContain(RowFlags(panel), heading => heading);
                    Assert.DoesNotContain("collapse with Left and Right", list.AccessibleDescription ?? "");
                }
            });
        }

        [Fact]
        public void ByFile_TheListsAccessibleDescription_SaysHeadingsCollapseWithLeftAndRight()
        {
            WithProject(true, (panel, list) =>
            {
                Assert.Contains("collapse with Left and Right", list.AccessibleDescription);   // an issue is selected
                Select(list, Headings(list)[0]);
                Assert.Contains("collapse with Left and Right", list.AccessibleDescription);
                Assert.Contains("Enter changes the issue.", list.AccessibleDescription);
            });
        }

        private static List<bool> RowFlags(FindingsPanel panel)
        {
            var rows = (IEnumerable)Prop(panel.E2EState(), "rows");
            return rows.Cast<object>().Select(r => (bool)Prop(r, "heading")).ToList();
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

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

        private static void Sta(Action body)
        {
            Exception error = null;
            var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(90000)) throw new TimeoutException("The pane kept laying itself out (no idle message loop in 90 s).");
            if (error != null) throw new Exception(error.Message, error);
        }

        private const string RuleId = "company:test/please-note";

        /// <summary>A guide with one rule: it flags "Please note that".</summary>
        private sealed class PleaseNoteGuide : FlareAI.Core.Style.IStyleService
        {
            private static readonly FlareAI.Core.Style.GuideRule Rule = new FlareAI.Core.Style.GuideRule
            {
                Id = RuleId, Layer = "company", Section = "Word list",
                Text = "Don't start a sentence with throat-clearing phrases such as please note. Say the fact directly.",
            };

            public IList<FlareAI.Core.Style.LintHit> Lint(IList<string> texts, IList<int> headings, string project)
            {
                var hits = new List<FlareAI.Core.Style.LintHit>();
                for (var i = 0; i < texts.Count; i++)
                {
                    var at = texts[i].IndexOf("Please note that", StringComparison.Ordinal);
                    if (at >= 0)
                        hits.Add(new FlareAI.Core.Style.LintHit { Index = i, Start = at, Length = 16, Match = "Please note that", Rule = "throat-clearing", RuleId = RuleId,
                                                                  Severity = "warning", Layer = "company", Message = "Say the fact directly, without a lead-in phrase." });
                }
                return hits;
            }
            public IList<FlareAI.Core.Style.LintHit> Lint(IList<FlareAI.Core.Style.LintLine> lines, string project) { return Lint(lines.Select(l => l.Text).ToList(), new List<int>(), project); }
            public string Guide(string project) { return ""; }
            public string[] GuideFor(string project, FlareAI.Core.Style.GuideScope scope) { return new[] { "", "" }; }
            public IList<string> RuleIdsFor(string project, FlareAI.Core.Style.GuideScope scope) { return new List<string> { RuleId }; }
            public IList<FlareAI.Core.Style.GuideRule> Rules(string project) { return new List<FlareAI.Core.Style.GuideRule> { Rule }; }
            public bool IsOff(string project, string ruleId) { return false; }
            public FlareAI.Core.Style.GuideReport Validate(string project) { return new FlareAI.Core.Style.GuideReport(); }
            public IList<FlareAI.Core.Style.GuideExample> Examples(string project)
            {
                return new List<FlareAI.Core.Style.GuideExample> { new FlareAI.Core.Style.GuideExample { Id = RuleId, Layer = "company",
                    Bad = "Please note that the fee is waived.", Good = "The fee is waived." } };
            }
        }
    }
}
