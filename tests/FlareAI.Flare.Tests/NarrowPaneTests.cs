using System;
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
    /// <summary>
    /// The review pane as Flare docks it: narrow (150–260 px) and 500–1000 px tall. A single-topic check with one AI reword:
    /// once the row is selected, the card shows its heading, what changes (at least two lines), why and every decision
    /// button, stacked without overlapping; the list keeps at least a row; a card too short for all of it scrolls.
    /// </summary>
    [Collection("Panes")]   // panes share static state (the tour, notices): never two at once
    public class NarrowPaneTests : IDisposable
    {
        // The writer's data folder (onboarding notes, checklist) is a temp folder: a pane test never writes to %APPDATA%.
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-pane-" + Guid.NewGuid().ToString("N"));
        private readonly string _prevRoot = FlareAI.Core.Paths.Root;   // the test run's redirected folder, put back on Dispose
        public NarrowPaneTests() { System.IO.Directory.CreateDirectory(_root); FlareAI.Core.Paths.Root = _root; FlareAI.Core.Settings.GuidedTour.Stop(); FindingsPanel.ResetDrawerForTests(); }
        public void Dispose() { FindingsPanel.ResetDrawerForTests(); FlareAI.Core.Paths.Root = _prevRoot; try { System.IO.Directory.Delete(_root, true); } catch (Exception) { } }

        private const string Topic = "<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Transfers</h1>" +
                                     "<p>Please note that the fee is waived for members who move money between their own accounts.</p></body></html>";

        /// <summary>The model's answer to a review request: one reword, on the run (⟦r1⟧…⟦/r1⟧) that holds its text.</summary>
        private static string OneReword(FlareAI.Core.Llm.LlmRequest r)
        {
            const string Excerpt = "Please note that the fee";
            var run = Regex.Matches(r.User ?? "", @"⟦(r\d+)⟧(.*?)⟦/\1⟧", RegexOptions.Singleline).Cast<Match>().FirstOrDefault(m => m.Groups[2].Value.Contains(Excerpt));
            if (run == null) return "{\"findings\":[]}";
            return "{\"findings\":[{\"run_id\":\"" + run.Groups[1].Value + "\",\"excerpt\":\"" + Excerpt + "\",\"rule_id\":\"general\",\"rule\":\"wordy\"," +
                   "\"severity\":\"suggestion\",\"replacement\":\"The fee\",\"why\":\"Say it directly: cut the throat-clearing so the reader gets the fact first.\"}]}";
        }

        [Theory]
        [InlineData(150, 500)]
        [InlineData(180, 800)]
        [InlineData(260, 1000)]
        public void OneAiReword_InASingleTopic_TheCardShowsEverything_WithoutOverlap(int width, int height)
        {
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(Topic);
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text(OneReword(r))(r)), new PromptBuilder(null), 1024);
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, true);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(width, height), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Pump(() => list.Items.Count > 0 && All(panel).OfType<ProgressBar>().All(p => !p.Visible),
                         15000, () => string.Join(" | ", All(panel).OfType<Label>().Select(l => l.Text).Where(t => !string.IsNullOrEmpty(t))));
                    Assert.Equal(1, list.Items.Count);
                    list.Focus();
                    list.Items[0].Selected = true;
                    list.Items[0].Focused = true;
                    Pump(null, 300);

                    var boxes = All(panel).OfType<RichTextBox>().ToList();
                    var diff = boxes[0]; var why = boxes[1];
                    var card = (Panel)diff.Parent;
                    var head = card.Controls.OfType<Label>().Single();
                    var decision = card.Controls.OfType<FlowLayoutPanel>().Single(f => f.AccessibleName == "Decision for the selected issue");
                    var split = All(panel).OfType<SplitContainer>().Single();
                    Check(width + "x" + height + ": ", height, list, card, head, diff, why, decision, split);

                    // Wider, then back: the card is laid out again each time (wide panes keep the two-column list).
                    form.ClientSize = new Size(500, height); Pump(null, 200);
                    Check("500x" + height + ": ", height, list, card, head, diff, why, decision, split);
                    form.ClientSize = new Size(width, height); Pump(null, 200);
                    Check("back to " + width + "x" + height + ": ", height, list, card, head, diff, why, decision, split);
                }
            });
        }

        [Theory]
        [InlineData(180, 640)]
        [InlineData(500, 800)]
        public void ApplyingTheReword_MovesToTheNext_KeepsTheRowStruckThrough_AndCtrlZ_BringsItBack(int width, int height)
        {
            Sta(() =>
            {
                // Two rewords; the editor answers every Go to with a document event that rebuilds the list (Flare's live
                // check / undo recheck), as Flare does while the pane is still selecting the next row after Apply.
                var sim = FlareSimDocument.FromXml(TwoParagraphs);
                var doc = new ReactingEditor(sim);
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text(Rewords(r))(r)), new PromptBuilder(null), 1024);
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, true);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(width, height), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Func<string> labels = () => string.Join(" | ", All(panel).OfType<Label>().Select(l => l.Text).Where(t => !string.IsNullOrEmpty(t)));
                    Pump(() => list.Items.Count == 2 && All(panel).OfType<ProgressBar>().All(p => !p.Visible), 15000, labels);
                    doc.OnSelect = () => panel.RefreshLint(new List<StyleFinding>());
                    list.Focus();
                    list.Items[0].Selected = true;
                    list.Items[0].Focused = true;
                    Pump(null, 200);

                    var apply = All(panel).OfType<Button>().Single(b => b.Parent.AccessibleName == "Decision for the selected issue" && b.Text == "Change");
                    apply.PerformClick();   // the button's own path: ReviewSession.Accept → Apply → FinishBatch → Sort → Rebuild, then Next
                    Pump(null, 300);

                    var where = width + "x" + height + ": " + labels() + " ";
                    Assert.Contains("The fee is waived", sim.Document.InnerText);
                    Assert.DoesNotContain("Could not", labels());
                    Assert.True(list.Items.Count == 2, where + "after Apply the list has " + list.Items.Count + " rows");
                    Assert.True(list.Items[0].Font.Strikeout, where + "the applied row isn't struck through");
                    Assert.True(list.Items[1].Selected, where + "Change should move to the next issue");
                    Assert.True(apply.Enabled, where + "the next suggestion can be applied");
                    Assert.Contains("In order to", list.Items[1].Text);

                    sim.Undo();                                                     // Ctrl+Z in the editor
                    panel.RecheckApplied();
                    Pump(null, 300);
                    Assert.Contains("Please note that the fee", sim.Document.InnerText);
                    Assert.Equal(2, list.Items.Count);
                    Assert.False(list.Items[0].Font.Strikeout, where + "the undone fix should be live again");
                    list.Items[0].Selected = true;
                    Pump(null, 200);
                    Assert.True(apply.Enabled, where + "Change works again after Ctrl+Z");
                    apply.PerformClick();
                    Pump(null, 300);
                    Assert.Contains("The fee is waived", sim.Document.InnerText);
                    Assert.True(list.Items[0].Font.Strikeout, where + "applied again");
                    Assert.DoesNotContain("Could not", labels());
                }
            });
        }

        // ---- Export Issues (#238) ----------------------------------------------------------------------------------

        [Fact]
        public void RowsFrom_LeavesOutAiFindings_AndMapsLevelStatusAndTopic()
        {
            WithPane((panel, form, list) =>
            {
                const string path = @"C:\Proj\Content\billpay\faq.htm";
                StyleFinding Company(bool fixedNow, bool stale) => new StyleFinding
                {
                    Layer = "company", Severity = "warning", Rule = "Company.WordList",
                    Excerpt = "utilize", Replacement = "use", DocumentPath = path, IsFixed = fixedNow, IsStale = stale
                };
                var a = Company(true, false);
                var b = Company(false, true);
                var c = Company(false, false);
                var d = Company(false, false); d.Layer = "ai";
                var e = Company(false, false); e.RuleLayer = "google";

                var rows = panel.RowsFrom(new[] { a, b, c, d, e });

                Assert.Equal(3, rows.Count);
                Assert.Equal(@"billpay\faq.htm", rows[0].Topic);
                Assert.Equal("Warning", rows[0].Level);
                Assert.Equal("Changed", rows[0].Status);
                Assert.Equal("Change \"utilize\" to \"use\"", rows[0].Issue);
                Assert.Equal(path, rows[0].Path);
                Assert.Equal("Text changed", rows[1].Status);
                Assert.Equal("Open", rows[2].Status);
                Assert.DoesNotContain("✓", rows[0].Issue + rows[1].Issue + rows[2].Issue);
                Assert.DoesNotContain("⚠", rows[0].Issue + rows[1].Issue + rows[2].Issue);
                Assert.All(rows, r => Assert.StartsWith("Our rules", r.Source));
            });
        }

        private static FlareAI.Core.Output.IssueExport.Row ExportRow()
        {
            return new FlareAI.Core.Output.IssueExport.Row
            {
                Topic = @"billpay\faq.htm", Level = "Warning", Issue = "Change \"utilize\" to \"use\"", Rule = "Use use",
                Source = "Our rules", Text = "ZQX-excerpt", SuggestedText = "use", Status = "Open",
                Path = @"C:\Proj\Content\billpay\faq.htm"
            };
        }

        [Fact]
        public void ExportTo_WritesUtf8WithBom_AndTheRow()
        {
            var dir = System.IO.Path.Combine(_root, "export-bom");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "issues.csv");
            WithPane((panel, form, list) =>
            {
                Exception error;
                Assert.True(panel.ExportTo(path, new[] { ExportRow() }, out error));
                Assert.Null(error);
                var bytes = System.IO.File.ReadAllBytes(path);
                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
                var lines = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8).Split(new[] { "\r\n" }, StringSplitOptions.None);
                Assert.StartsWith(@"billpay\faq.htm,Warning,", lines[1]);
                Assert.EndsWith(",Open,\"=HYPERLINK(\"\"file:///C:/Proj/Content/billpay/faq.htm\"\",\"\"Open file\"\")\"", lines[1]);
                Assert.Contains(FlareAI.Core.Output.IssueExport.Saved(1, path), All(panel).OfType<Label>().Select(l => l.Text));
            });
        }

        [Fact]
        public void ExportTo_OverAnExistingFile_ReplacesIt()
        {
            var dir = System.IO.Path.Combine(_root, "export-replace");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "issues.csv");
            System.IO.File.WriteAllText(path, "old");
            WithPane((panel, form, list) =>
            {
                Exception error;
                Assert.True(panel.ExportTo(path, new[] { ExportRow() }, out error));
                Assert.Null(error);
                var bytes = System.IO.File.ReadAllBytes(path);
                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
                Assert.NotEqual("old", System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
                Assert.StartsWith("\uFEFFTopic,", System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
                Assert.Single(System.IO.Directory.GetFiles(dir));
            });
        }

        [Fact]
        public void ExportTo_OverALockedFile_FailsAndLeavesItAsItWas()
        {
            var dir = System.IO.Path.Combine(_root, "export-locked");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "issues.csv");
            System.IO.File.WriteAllText(path, "old");
            WithPane((panel, form, list) =>
            {
                bool ok;
                Exception error;
                using (new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                    ok = panel.ExportTo(path, new[] { ExportRow() }, out error);
                Assert.False(ok);
                Assert.IsType<System.IO.IOException>(error);
                Assert.Equal("old", System.IO.File.ReadAllText(path));
                Assert.Equal(new[] { path }, System.IO.Directory.GetFiles(dir));
                Assert.Contains("House Style couldn't export the issues to issues.csv.", All(panel).OfType<Label>().Select(l => l.Text));
                if (System.IO.File.Exists(FlareAI.Core.Diagnostics.LogPath))
                    Assert.DoesNotContain("ZQX-excerpt", System.IO.File.ReadAllText(FlareAI.Core.Diagnostics.LogPath));
            });
        }

        /// <summary>A project report over the given topics (written under _root), done linting; runs <paramref name="body"/>.</summary>
        private void WithProject(string[] topicsXml, int width, int height, Action<FindingsPanel, Form> body)
        {
            var proj = System.IO.Path.Combine(_root, "proj");
            var content = System.IO.Path.Combine(proj, "Content", "billpay");
            System.IO.Directory.CreateDirectory(content);
            System.IO.File.WriteAllText(System.IO.Path.Combine(proj, "Billpay.flprj"), "");
            var files = new List<string>();
            for (var i = 0; i < topicsXml.Length; i++)
            {
                var f = System.IO.Path.Combine(content, "t" + i + ".htm");
                System.IO.File.WriteAllText(f, topicsXml[i]);
                files.Add(f);
            }
            Sta(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r)), new PromptBuilder(null), 1024, new LongRuleGuide());
                var panel = FindingsPanel.ForProject(runner, FlareSimDocument.FromXml(Topic), () => runner, p => null, files, "Billpay");
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(width, height), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    Pump(() => All(panel).OfType<Label>().Any(l => (l.Text ?? "").Contains("Linted ")) && All(panel).OfType<ProgressBar>().All(p => !p.Visible),
                         30000, () => string.Join(" | ", All(panel).OfType<Label>().Select(l => l.Text).Where(t => !string.IsNullOrEmpty(t))));
                    body(panel, form);
                }
            });
        }

        [Theory]
        [InlineData(150, 640)]
        [InlineData(180, 800)]
        [InlineData(260, 1000)]
        public void ExportButton_IsOnlyInAProjectReport_AndTheBarIsNamedProjectReport(int width, int height)
        {
            WithProject(new[] { Topic }, width, height, (panel, form) =>
            {
                var b = All(panel).OfType<Button>().Single(x => x.Text == "Export Issues…");
                Assert.True(b.Visible);
                Assert.True(b.Enabled);
                Assert.True(b.Right <= b.Parent.ClientSize.Width, "right " + b.Right + " of " + b.Parent.ClientSize.Width);
                Assert.Equal("Project report", b.Parent.AccessibleName);
                Assert.EndsWith("Opens a dialog.", b.AccessibleDescription);
            });
            WithPane((panel, form, list) =>
            {
                var b = All(panel).OfType<Button>().Single(x => x.Text == "Export Issues…");
                Assert.False(b.Visible);
            });
        }

        [Fact]
        public void ExportIssues_WithNoIssues_SaysSo_AndOpensNoDialog()
        {
            var old = Environment.GetEnvironmentVariable("HOUSESTYLE_E2E_DIR");
            Environment.SetEnvironmentVariable("HOUSESTYLE_E2E_DIR", null);
            try
            {
                WithProject(new[] { "<html><body><p>The fee is waived.</p></body></html>" }, 260, 800, (panel, form) =>
                {
                    var forms = Application.OpenForms.Count;
                    All(panel).OfType<Button>().Single(x => x.Text == "Export Issues…").PerformClick();
                    Pump(null, 200);
                    Assert.Contains("There are no issues to export.", All(panel).OfType<Label>().Select(l => l.Text));
                    Assert.Equal(forms, Application.OpenForms.Count);
                });
                Assert.Empty(System.IO.Directory.GetFiles(_root, "*.csv", System.IO.SearchOption.AllDirectories));
            }
            finally { Environment.SetEnvironmentVariable("HOUSESTYLE_E2E_DIR", old); }
        }

        // ---- the e2e hook's helpers (#257) -------------------------------------------------------------------------

        /// <summary>A pane with the two-reword topic, done checking, on an off-screen form; runs <paramref name="body"/> with it.</summary>
        private static void WithPane(Action<FindingsPanel, Form, ListView> body, int width = 500, int height = 800)
        {
            WithPaneDoc((doc, panel, form, list) => body(panel, form, list), width, height);
        }

        /// <summary>As <see cref="WithPane(Action{FindingsPanel, Form, ListView}, int, int)"/>, and hands the body the fake document too.</summary>
        private static void WithPaneDoc(Action<FlareSimDocument, FindingsPanel, Form, ListView> body, int width, int height)
        {
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(TwoParagraphs);
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text(Rewords(r))(r)), new PromptBuilder(null), 1024);
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, true);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(width, height), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Pump(() => list.Items.Count == 2 && All(panel).OfType<ProgressBar>().All(p => !p.Visible),
                         15000, () => list.Items.Count + " rows: " + string.Join(" | ", All(panel).OfType<Label>().Select(l => l.Text).Where(t => !string.IsNullOrEmpty(t))));
                    body(doc, panel, form, list);
                }
            });
        }

        [DesktopFact]
        public void E2EFocusList_FocusesTheList_AndSelectsTheFirstRow()
        {
            WithPane((panel, form, list) =>
            {
                var other = All(panel).OfType<Button>().First(b => b.Visible && b.CanFocus);
                other.Focus();
                list.SelectedItems.Clear();
                Pump(null, 100);
                Assert.True(panel.E2EFocusList(), "the list should have the focus");
                Assert.True(list.Focused);
                Assert.Single(list.SelectedItems.Cast<ListViewItem>());
                Assert.True(list.Items[0].Selected);
                // A disabled window (a modal dialog over Flare) can't take the focus: the scenario sees false.
                other.Focus();
                form.Enabled = false;
                Assert.False(panel.E2EFocusList());
                form.Enabled = true;
            });
        }

        [Fact]
        public void E2EMenuPick_ClicksTheReasonByName_AndFailsWithNoOpenMenu()
        {
            WithPane((panel, form, list) =>
            {
                list.Items[0].Selected = true;
                Pump(null, 200);
                Assert.Equal("No open menu", Assert.Throws<InvalidOperationException>(() => panel.E2EMenuPick("It's correct")).Message);
                var ignore = All(panel).OfType<Button>().Single(b => b.Text.StartsWith("Ignore", StringComparison.Ordinal) && b.Visible);
                ignore.PerformClick();   // opens the reasons menu, as Delete in the list does
                var noItem = Assert.Throws<InvalidOperationException>(() => panel.E2EMenuPick("Nope")).Message;
                Assert.StartsWith("No menu item Nope", noItem);
                Assert.Contains("Skip", noItem);
                panel.E2EMenuPick("Skip");   // by name (Skip for Now records nothing another test could see)
                Pump(null, 300);
                Assert.Equal("No open menu", Assert.Throws<InvalidOperationException>(() => panel.E2EMenuPick("It's correct")).Message);
                Assert.True(list.Items[1].Selected, "Skip for Now should move to the next issue");
            });
        }

        [Fact]
        public void TakeSavePath_IsNull_WhenTheHookIsOff_AndOneShotWhenSet()
        {
            var old = Environment.GetEnvironmentVariable(E2EHook.EnvVar);
            var run = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hs-e2e-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable(E2EHook.EnvVar, null);
                Assert.Null(E2EHook.TakeSavePath());
                Assert.Throws<InvalidOperationException>(() => E2EHook.SetSavePath(System.IO.Path.Combine(run, "issues.csv")));   // hook off

                Environment.SetEnvironmentVariable(E2EHook.EnvVar, run);
                var inside = System.IO.Path.Combine(run, "out", "issues.csv");
                E2EHook.SetSavePath(inside);
                Assert.Equal(inside, E2EHook.TakeSavePath());
                Assert.Null(E2EHook.TakeSavePath());   // one-shot

                foreach (var bad in new[] { @"C:\Windows\issues.csv", "issues.csv", System.IO.Path.Combine(run, @"..\issues.csv"), run, run + "-other\\x.csv" })
                    Assert.Equal("Save path must be inside the run folder", Assert.Throws<InvalidOperationException>(() => E2EHook.SetSavePath(bad)).Message);
                Assert.Null(E2EHook.TakeSavePath());
            }
            finally { Environment.SetEnvironmentVariable(E2EHook.EnvVar, old); }
        }

        // ---- Mark as Fixed (#237): the menus, the card head and the hand-fix strike-through -----------------------

        private static object Prop(object o, string name) { return o.GetType().GetProperty(name).GetValue(o, null); }
        private static string Head(FindingsPanel p) { return (string)Prop(Prop(p.E2EState(), "card"), "head"); }
        private static string StatusOf(FindingsPanel p) { return (string)Prop(p.E2EState(), "status"); }
        private static ContextMenuStrip RejectMenu(FindingsPanel p)
        {
            return (ContextMenuStrip)typeof(FindingsPanel).GetField("_rejectMenu", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(p);
        }
        private static ToolStripItem MarkItem(ListView list) { return list.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(i => i.Text == "Mark as Fixed"); }
        private static Button ChangeButton(Control panel)
        {
            return All(panel).OfType<Button>().Single(b => b.Parent.AccessibleName == "Decision for the selected issue" && b.Text == "Change");
        }
        /// <summary>The writer's edit: the run that holds <paramref name="contains"/> gets <paramref name="text"/>, as the DOM changes in Flare.</summary>
        private static void EditRun(FlareSimDocument sim, string contains, string text)
        {
            var run = sim.Document.SelectNodes("//text()").Cast<System.Xml.XmlNode>().OfType<System.Xml.XmlText>().First(t => t.Value.Contains(contains));
            run.Value = text;
        }

        [Theory]
        [InlineData(180, 640)]
        [InlineData(500, 800)]
        public void HandFix_StrikesTheRow_AndTheCardSaysFixed(int width, int height)
        {
            WithPaneDoc((sim, panel, form, list) =>
            {
                list.Items[0].Selected = true;
                Pump(null, 200);
                EditRun(sim, "Please note that the fee", "The fee is waived for members who move money between their own accounts.");
                panel.AfterEdit(null);
                Pump(null, 300);

                Assert.True(list.Items[0].Font.Strikeout, width + "x" + height + ": the hand-fixed row isn't struck through");
                Assert.Contains("✓", list.Items[0].Text);
                Assert.True(list.Items[0].Selected, "the fixed row should stay selected");
                Assert.Contains("fixed", Head(panel));
                Assert.DoesNotContain("changed", Head(panel));
                Assert.Contains("fixed", list.AccessibleDescription);
                Assert.False(ChangeButton(panel).Enabled, "Change should be disabled on a fixed row");
            }, width, height);
        }

        [Theory]
        [InlineData(150, 640)]
        [InlineData(500, 800)]
        public void RowMenu_MarkAsFixed_AfterGoTo_EnabledOnStale_DisabledOnChanged(int width, int height)
        {
            WithPaneDoc((sim, panel, form, list) =>
            {
                var items = panel.E2ERowMenu(0, null);
                Assert.Equal(items.IndexOf("Go To") + 1, items.IndexOf("Mark as Fixed"));

                EditRun(sim, "Please note that the fee", "The fee is waived for members who move money between their own accounts.");
                panel.RefreshLint(new List<StyleFinding>());
                Pump(null, 300);
                panel.E2ERowMenu(0, null);
                Assert.True(MarkItem(list).Enabled, "a text-changed row can be marked fixed");
                Assert.Equal("Mark as Fixed", MarkItem(list).AccessibleName);

                // The writer fixes the second issue by hand (a Change click here left the layout tests stale in the same run).
                EditRun(sim, "In order to move money", "To move money");
                panel.AfterEdit(null);
                Pump(null, 300);
                var fixedRow = list.Items.Cast<ListViewItem>().ToList().FindIndex(i => i.Text.Contains("✓"));
                Assert.True(fixedRow >= 0, "the Change should have fixed a row");
                panel.E2ERowMenu(fixedRow, null);
                Assert.False(MarkItem(list).Enabled, "a fixed row can't be marked again");
            }, width, height);
        }

        [DesktopTheory]
        [InlineData(150, 640)]
        [InlineData(500, 800)]
        public void IgnoreMenu_KeepsTheFirstReason_AndMarkAsFixedSitsBetweenSeparatorsAfterSkip(int width, int height)
        {
            WithPane((panel, form, list) =>
            {
                var menu = RejectMenu(panel).Items.Cast<ToolStripItem>().ToList();
                Assert.Equal("The suggestion is wrong", menu[0].Text);
                var skip = menu.FindIndex(i => i.Text == "Skip for Now");
                Assert.True(skip > 0, "Skip for Now is in the menu");
                Assert.IsType<ToolStripSeparator>(menu[skip + 1]);
                Assert.Equal("Mark as Fixed", menu[skip + 2].Text);
                Assert.IsType<ToolStripSeparator>(menu[skip + 3]);
                Assert.Equal("Report False Positive…", menu[skip + 4].Text);
            }, width, height);
        }

        [Theory]
        [InlineData(150, 640)]
        [InlineData(500, 800)]
        public void MarkAsFixed_FromTheIgnoreMenu_StrikesTheRow(int width, int height)
        {
            WithPane((panel, form, list) =>
            {
                list.Items[0].Selected = true;
                Pump(null, 200);
                var ignore = All(panel).OfType<Button>().Single(b => b.Text.StartsWith("Ignore", StringComparison.Ordinal) && b.Visible);
                ignore.PerformClick();
                panel.E2EMenuPick("Mark as Fixed");
                Pump(null, 300);

                Assert.True(list.Items[0].Font.Strikeout, "the row isn't struck through");
                Assert.EndsWith("Marked as fixed.", StatusOf(panel));
            }, width, height);
        }

        private const string TwoParagraphs ="<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Transfers</h1>" +
                                             "<p>Please note that the fee is waived for members who move money between their own accounts.</p>" +
                                             "<p>In order to move money, open the Transfers page.</p></body></html>";

        /// <summary>The model's answer: a reword for each paragraph, on the run that holds its text.</summary>
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

        /// <summary>The fake Flare editor, which (like Flare) raises a document event when the plugin selects text in it.</summary>
        private sealed class ReactingEditor : FlareAI.Core.Editing.IDocumentAdapter
        {
            private readonly FlareSimDocument _inner;
            public Action OnSelect;
            public ReactingEditor(FlareSimDocument inner) { _inner = inner; }
            public string SourcePath { get { return _inner.SourcePath; } }
            public bool IsEditableTopic { get { return _inner.IsEditableTopic; } }
            public System.Xml.XmlDocument Document { get { return _inner.Document; } }
            public string SelectionText { get { return _inner.SelectionText; } }
            public IList<System.Xml.XmlNode> SelectionNodes { get { return _inner.SelectionNodes; } }
            public void ReplaceBlocks(string operationName, IList<System.Xml.XmlNode> oldNodes, IList<System.Xml.XmlNode> newNodes) { _inner.ReplaceBlocks(operationName, oldNodes, newNodes); }
            public bool SelectText(System.Xml.XmlText run, int start, int length)
            {
                var ok = _inner.SelectText(run, start, length);
                var h = OnSelect;
                if (h != null) h();
                return ok;
            }
            public void Edit(string operationName, Action body) { _inner.Edit(operationName, body); }
            public bool ReplaceSpan(System.Xml.XmlText run, int start, int length, string replacement) { return _inner.ReplaceSpan(run, start, length, replacement); }
            public string VariableValue(string qualifiedName) { return _inner.VariableValue(qualifiedName); }
        }

        // ---- #247: the whole rule in the card ---------------------------------------------------------------

        private const string LongRuleId = "company:test/long-rule";

        /// <summary>
        /// A guide with one long rule: 8+ lines of text at 150 px, an italic rule line, a ✗/✓ example and one long unbroken
        /// path. The linter flags "Please note that" with it.
        /// </summary>
        private sealed class LongRuleGuide : FlareAI.Core.Style.IStyleService
        {
            public static readonly FlareAI.Core.Style.GuideRule Rule = new FlareAI.Core.Style.GuideRule
            {
                Id = LongRuleId, Layer = "company", Section = "Word list",
                Text = "Don't start a sentence with throat-clearing phrases such as please note, note that, it is important to note, " +
                       "keep in mind or be aware that. Say the fact directly so the reader gets it first, and put a real warning in a " +
                       "Note or Caution block instead, which the reader can scan for. This also applies to snippets in " +
                       "Content/Resources/Snippets/Shared/VeryLongSnippetFolderName/AnotherLongFolder/Disclaimers.flsnp and to " +
                       "every topic that uses them, so check those too before you change the wording.",
            };

            public IList<FlareAI.Core.Style.LintHit> Lint(IList<string> texts, IList<int> headings, string project)
            {
                var hits = new List<FlareAI.Core.Style.LintHit>();
                for (var i = 0; i < texts.Count; i++)
                {
                    var at = texts[i].IndexOf("Please note that", StringComparison.Ordinal);
                    if (at >= 0)
                        hits.Add(new FlareAI.Core.Style.LintHit { Index = i, Start = at, Length = 16, Match = "Please note that", Rule = "throat-clearing", RuleId = LongRuleId,
                                                                  Severity = "warning", Layer = "company", Message = "Say the fact directly, without a lead-in phrase." });
                }
                return hits;
            }
            public IList<FlareAI.Core.Style.LintHit> Lint(IList<FlareAI.Core.Style.LintLine> lines, string project) { return Lint(lines.Select(l => l.Text).ToList(), new List<int>(), project); }
            public string Guide(string project) { return ""; }
            public string[] GuideFor(string project, FlareAI.Core.Style.GuideScope scope) { return new[] { "", "" }; }
            public IList<string> RuleIdsFor(string project, FlareAI.Core.Style.GuideScope scope) { return new List<string> { LongRuleId }; }
            public IList<FlareAI.Core.Style.GuideRule> Rules(string project) { return new List<FlareAI.Core.Style.GuideRule> { Rule }; }
            public bool IsOff(string project, string ruleId) { return false; }
            public FlareAI.Core.Style.GuideReport Validate(string project) { return new FlareAI.Core.Style.GuideReport(); }
            public IList<FlareAI.Core.Style.GuideExample> Examples(string project)
            {
                return new List<FlareAI.Core.Style.GuideExample> { new FlareAI.Core.Style.GuideExample { Id = LongRuleId, Layer = "company",
                    Bad = "Please note that the fee is waived for members who move money between their own accounts.",
                    Good = "The fee is waived for members who move money between their own accounts." } };
            }
        }

        /// <summary>Opens a pane w×h on the long-rule topic, lints it (no AI) and selects the issue; then runs <paramref name="body"/>.</summary>
        private static void WithLongRuleCard(int width, int height, Action<Form, FindingsPanel, Panel, CardTextBox, RichTextBox, FlowLayoutPanel> body)
        {
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(Topic);
                var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r)), new PromptBuilder(null), 1024, new LongRuleGuide());
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, false);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(width, height), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Pump(() => list.Items.Count > 0 && All(panel).OfType<ProgressBar>().All(p => !p.Visible),
                         15000, () => string.Join(" | ", All(panel).OfType<Label>().Select(l => l.Text).Where(t => !string.IsNullOrEmpty(t))));
                    list.Focus();
                    list.Items[0].Selected = true;
                    list.Items[0].Focused = true;
                    Pump(null, 300);
                    var why = All(panel).OfType<CardTextBox>().Single();
                    var card = (Panel)why.Parent;
                    var diff = card.Controls.OfType<RichTextBox>().Single(b => !(b is CardTextBox));
                    var decision = card.Controls.OfType<FlowLayoutPanel>().Single(f => f.AccessibleName == "Decision for the selected issue");
                    Assert.Contains(LongRuleGuide.Rule.Display, why.Text);
                    body(form, panel, card, why, diff, decision);
                }
            });
        }

        /// <summary>The why box shows all its text: no scroll bar of its own, and its last line inside the box.</summary>
        private static void AssertWhole(string where, Panel card, CardTextBox why, RichTextBox diff, FlowLayoutPanel decision)
        {
            var content = why.ContentHeight();   // raises ContentsResized: the box's own answer
            var lastBottom = why.GetPositionFromCharIndex(why.TextLength - 1).Y + why.Font.Height;
            where += "(card " + card.ClientSize + ", why " + why.Bounds + ", contents " + why.RequestedHeight + ", last line bottom " + lastBottom + ") ";
            Assert.True(why.RequestedHeight > 0, where + "ContentsResized didn't report a height");
            Assert.True(why.RequestedHeight <= why.ClientSize.Height, where + "the why text is taller than its box");
            Assert.True(lastBottom <= why.ClientSize.Height, where + "the last line is cut off");
            Assert.True(content <= why.ClientSize.Height, where + "the measured height is more than the box");
            Assert.Equal(RichTextBoxScrollBars.None, why.ScrollBars);
            Assert.False(why.DetectUrls);
            Assert.True(diff.Bottom <= decision.Top && decision.Top - diff.Bottom <= 8, where + "the buttons aren't right under what changes");
            Assert.True(decision.Bottom <= why.Top, where + "the buttons overlap why");
            Assert.True(why.Bottom <= card.ClientSize.Height || card.VerticalScroll.Visible, where + "the card neither fits nor scrolls");
        }

        [Theory]
        [InlineData(150, 500)]
        [InlineData(180, 640)]
        [InlineData(260, 1000)]
        public void LongRule_TheWhyTextIsNeverClipped_AndTheCardScrollsInstead(int width, int height)
        {
            WithLongRuleCard(width, height, (form, panel, card, why, diff, decision) =>
            {
                AssertWhole(width + "x" + height + ": ", card, why, diff, decision);
                if (width == 150 && height == 500) Assert.True(card.VerticalScroll.Visible, "a long rule in a 150x500 pane scrolls the card");
            });
        }

        [Fact]
        public void LongRule_IsMeasuredAtTheWidthItIsDrawnAt_AsThePaneCrossesTheScrollBarThreshold()
        {
            // Wider and wider panes at one height (CI screens are small, so not taller): somewhere the card stops needing its
            // scroll bar, and its text gets wider.
            WithLongRuleCard(150, 700, (form, panel, card, why, diff, decision) =>
            {
                bool sawScroll = false;
                for (var w = 150; w <= 700; w += 10)
                {
                    form.ClientSize = new Size(w, 700); Pump(null, 120);
                    AssertWhole(w + "x700: ", card, why, diff, decision);
                    if (card.VerticalScroll.Visible) sawScroll = true;
                }
                Assert.True(sawScroll, "the card never scrolled");
            });
        }

        private const int WM_MOUSEWHEEL = 0x020A, WM_KEYDOWN = 0x0100, VK_NEXT = 0x22, EM_GETFIRSTVISIBLELINE = 0xCE;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [Fact]
        public void WheelOverWhyText_ScrollsTheCard_AndTheBoxKeepsItsFirstVisibleCharacter()
        {
            WithLongRuleCard(150, 500, (form, panel, card, why, diff, decision) =>
            {
                Assert.True(card.VerticalScroll.Visible);
                var before = card.AutoScrollPosition.Y;
                var at = why.PointToScreen(new Point(10, 10));
                SendMessage(why.Handle, WM_MOUSEWHEEL, (IntPtr)((-120) << 16), (IntPtr)((at.Y << 16) | (at.X & 0xFFFF)));
                Pump(null, 100);
                Assert.True(card.AutoScrollPosition.Y < before, "the wheel didn't scroll the card (" + before + " → " + card.AutoScrollPosition.Y + ")");
                Assert.Equal(0, why.GetCharIndexFromPosition(new Point(1, 1)));
                Assert.Equal(IntPtr.Zero, SendMessage(why.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero));
            });
        }

        /// <summary>The drawer is small by default (the list keeps five rows); a test that reads a long card drags it taller first.</summary>
        private static void OpenDrawerTall(FindingsPanel panel)
        {
            var split = All(panel).OfType<SplitContainer>().Single();
            panel.E2ESetDrawerHeight(split.Height * 60 / 100);
            Pump(null, 300);
        }

        [DesktopFact]
        public void PageDownInWhyText_KeepsTheCaretInView()
        {
            WithLongRuleCard(150, 500, (form, panel, card, why, diff, decision) =>
            {
                form.Activate();
                OpenDrawerTall(panel);
                why.Focus();
                Pump(null, 50);
                Assert.True(why.Focused, "the why text didn't take focus");
                why.Select(0, 0);
                for (var i = 0; i < 6; i++) { SendMessage(why.Handle, WM_KEYDOWN, (IntPtr)VK_NEXT, (IntPtr)1); Pump(null, 30); }
                Assert.True(why.SelectionStart > 0, "Page Down didn't move the caret");
                var caretTop = why.Top + why.GetPositionFromCharIndex(why.SelectionStart).Y;
                Assert.True(caretTop >= 0 && caretTop + why.Font.Height <= card.ClientSize.Height,
                            "the caret's line is out of view (line top " + caretTop + ", card " + card.ClientSize.Height + ", scroll " + card.AutoScrollPosition.Y + ")");
                Assert.Equal(IntPtr.Zero, SendMessage(why.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero));   // the card scrolled, not the box
            });
        }

        [Fact]
        public void TabbingIntoAWhyBoxBelowTheFold_BringsItsTopIntoView()
        {
            WithLongRuleCard(150, 500, (form, panel, card, why, diff, decision) =>
            {
                form.Activate();
                OpenDrawerTall(panel);
                Assert.True(card.VerticalScroll.Visible);
                diff.Focus();
                card.AutoScrollPosition = new Point(0, 0);
                Pump(null, 50);
                Assert.True(why.Top >= card.ClientSize.Height, "the why box should start below the fold (top " + why.Top + ", card " + card.ClientSize.Height + ")");
                why.Select();   // what Tab does when it reaches the box (Select, then the card scrolls it into view)
                Pump(null, 50);
                Assert.True(why.Focused, "Tab didn't reach the why text");
                Assert.True(why.Top >= 0 && why.Top < card.ClientSize.Height,
                            "the focused why text is off screen (top " + why.Top + ", card " + card.ClientSize.Height + ")");
            });
        }

        [Fact]
        public void ClickingIntoATallWhyBox_DoesNotMoveTheCard()
        {
            WithLongRuleCard(150, 500, (form, panel, card, why, diff, decision) =>
            {
                form.Activate();
                Assert.True(card.VerticalScroll.Visible);
                // Scrolled so the why text starts halfway down the card: part of it is on screen.
                card.AutoScrollPosition = new Point(0, Math.Max(10, why.Top - card.AutoScrollPosition.Y - card.ClientSize.Height / 2));
                Pump(null, 50);
                var before = card.AutoScrollPosition.Y;
                Assert.True(before < 0);
                why.Select();   // as a click or Tab gives it focus
                Pump(null, 50);
                Assert.True(why.Focused);
                Assert.Equal(before, card.AutoScrollPosition.Y);
            });
        }

        /// <summary>The card's parts stack without overlapping, nothing is cut off, and what doesn't fit scrolls.</summary>
        private static void Check(string where, int height, ListView list, Panel card, Label head, RichTextBox diff, RichTextBox why, FlowLayoutPanel decision, SplitContainer split)
        {
            where += "(card " + card.ClientSize + ", split " + split.Height + ", list " + list.Height + ", head " + head.Bounds + ", diff " + diff.Bounds +
                     ", buttons " + decision.Bounds + ", why " + why.Bounds + ") ";
            Assert.Contains("Needs you", head.Text);
            Assert.True(head.Top >= 0 && head.Height >= head.Font.Height, where + "the heading is hidden");
            Assert.True(head.Bottom <= diff.Top, where + "the heading overlaps what changes");
            Assert.True(diff.Height >= 2 * diff.Font.Height, where + "what changes is " + diff.Height + " px");
            Assert.True(diff.Bottom <= decision.Top, where + "what changes overlaps the buttons");
            Assert.True(decision.Bottom <= why.Top, where + "the buttons overlap why");
            Assert.True(why.Height >= 3 * why.Font.Height, where + "why is " + why.Height + " px");
            foreach (var b in decision.Controls.OfType<Button>().Where(b => b.Visible))
            {
                Assert.True(b.Right <= decision.ClientSize.Width && b.Bottom <= decision.ClientSize.Height, where + "'" + b.Text + "' is cut off");
                Assert.True(b.Width <= card.ClientSize.Width, where + "'" + b.Text + "' is wider than the card");
            }
            var fits = why.Bottom <= card.ClientSize.Height;
            Assert.True(fits || card.VerticalScroll.Visible, where + "the card neither fits nor scrolls");
            Assert.True(list.ClientSize.Height >= list.Items[0].Bounds.Height, where + "the list keeps at least one row");
            Assert.True(split.Panel2.Top >= list.Parent.Bottom, where + "the card starts above the list's bottom");
        }

        // ---- One-line rows and the details drawer (#324) -----------------------------------------------------------

        /// <summary>A project report over six copies of Topic, in the By file view, at the given size; runs <paramref name="body"/>.</summary>
        private void WithByFile(Action<FindingsPanel, Form, ListView> body, int width = 320, int height = 463)
        {
            WithProject(Enumerable.Repeat(Topic, 6).ToArray(), width, height, (panel, form) =>
            {
                var list = All(panel).OfType<ListView>().Single();
                var byFile = All(panel).OfType<RadioButton>().First(r => r.Text.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0);
                byFile.PerformClick();
                Pump(() => list.Items.Count > 0, 15000);
                Pump(null, 300);
                body(panel, form, list);
            });
        }

        private static int RowsIn(ListView list)
        {
            return list.Items.Cast<ListViewItem>().Count(i => list.ClientRectangle.Contains(i.Bounds));
        }

        private static SplitContainer SplitOf(FindingsPanel panel) { return All(panel).OfType<SplitContainer>().Single(); }

        private static Panel CardOf(FindingsPanel panel) { return SplitOf(panel).Panel2.Controls.OfType<Panel>().Single(p => p.AutoScroll); }

        private static Label BarTitle(FindingsPanel panel) { return All(panel).OfType<Label>().Single(l => l.AccessibleName == "Selected issue"); }

        private static Button DrawerToggle(FindingsPanel panel)
        {
            return All(panel).OfType<Button>().Single(b => b.AccessibleName == "Hide details" || b.AccessibleName == "Show details");
        }

        private static bool IsHeading(ListViewItem i) { return i.Text.StartsWith("▾") || i.Text.StartsWith("▸"); }

        [Fact]
        public void TheOpenDrawer_LeavesFiveOneLineRows_AndTheButtonsReachable_At320x463()
        {
            WithByFile((panel, form, list) =>
            {
                var scale = form.DeviceDpi / 96f;
                var card = CardOf(panel);
                var decision = card.Controls.OfType<FlowLayoutPanel>().First(f => f.Controls.OfType<Button>().Any(b => b.Text == "Go To"));
                var split = SplitOf(panel);
                Assert.True(RowsIn(list) >= 5, "only " + RowsIn(list) + " rows are visible; split " + split.Height + ", list client " + list.ClientSize.Height + ", row " + list.Items[0].Bounds.Height + ", drawer " + split.Panel2.Height);
                Assert.All(list.Items.Cast<ListViewItem>(), i => Assert.True(i.Bounds.Height <= (int)Math.Round(26 * scale), "a row is " + i.Bounds.Height + " px tall"));
                Assert.True(card.Visible);
                Assert.True(card.VerticalScroll.Visible || decision.Bottom <= card.ClientSize.Height, "the buttons can't be reached");
                Assert.Equal("Hide details", DrawerToggle(panel).AccessibleName);
            });
        }

        [Fact]
        public void TheDrawerToggleGlyph_IsLargerThanTheListFont()
        {
            WithByFile((panel, form, list) =>
            {
                var toggle = DrawerToggle(panel);
                Assert.True(toggle.Font.Size > panel.Font.Size, "the glyph is " + toggle.Font.Size + " pt");
                Assert.True(toggle.Height >= toggle.Font.Height, "the glyph is cut off");
                toggle.PerformClick();
                Pump(null, 300);
                Assert.Equal("▴", toggle.Text);
                Assert.True(toggle.Font.Size > panel.Font.Size);
                // Restore the drawer, because its state is static and later tests rely on it.
                toggle.PerformClick();
                Pump(null, 300);
            });
        }

        [Fact]
        public void HidingTheDrawer_ShowsEightRows_AndTheBarKeepsTheSelectedTitle_UntilItIsShownAgain()
        {
            WithByFile((panel, form, list) =>
            {
                var card = CardOf(panel);
                var bar = BarTitle(panel);
                var toggle = DrawerToggle(panel);
                var before = RowsIn(list);
                toggle.PerformClick();
                Pump(null, 300);
                Assert.False(card.Visible);
                Assert.Equal("Show details", toggle.AccessibleName);
                Assert.True(RowsIn(list) >= 8, "only " + RowsIn(list) + " rows are visible");
                Assert.True(RowsIn(list) > before);
                Assert.Equal(1, list.SelectedItems.Count);
                Assert.EndsWith(bar.Text, list.SelectedItems[0].Text);
                Assert.False(string.IsNullOrEmpty(bar.Text));

                // Selecting another row changes the bar and doesn't open the drawer.
                var other = list.Items.Cast<ListViewItem>().First(IsHeading);
                var was = bar.Text;
                other.Selected = true;
                Pump(() => bar.Text != was, 3000);
                Assert.Equal(other.Text, bar.Text);
                Assert.False(card.Visible);

                // Showing it again restores the card and its buttons.
                toggle.PerformClick();
                Pump(null, 300);
                Assert.True(card.Visible);
                Assert.Equal("Hide details", toggle.AccessibleName);
                Assert.True(All(card).OfType<Button>().Any(b => b.Text == "Go To" && b.Visible));
                Assert.True(RowsIn(list) < 8);
            });
        }

        [Fact]
        public void TheDrawersHeight_ChangesTheRows_AndNeitherPartGoesUnderItsMinimum()
        {
            WithByFile((panel, form, list) =>
            {
                var split = SplitOf(panel);
                var bar = BarTitle(panel).Parent;
                panel.E2ESetDrawerHeight(split.Height / 4); Pump(null, 200);
                var small = split.Panel2.Height;
                var many = RowsIn(list);
                panel.E2ESetDrawerHeight(split.Height * 55 / 100); Pump(null, 200);
                var big = split.Panel2.Height;
                var few = RowsIn(list);
                Assert.True(big > small, "the drawer didn't grow (" + small + " → " + big + ")");
                Assert.True(few < many, "the list still shows " + few + " rows (was " + many + ")");

                panel.E2ESetDrawerHeight(1); Pump(null, 200);
                Assert.True(split.Panel2.Height >= bar.Height, "the drawer is under its bar");
                panel.E2ESetDrawerHeight(5000); Pump(null, 200);
                var where = "split " + split.Height + ", list client " + list.ClientSize.Height + ", row " + list.Items[0].Bounds.Height + ", drawer " + split.Panel2.Height;
                Assert.True(split.Panel1.Height >= list.Items[0].Bounds.Height, "the list is under one row: " + where);
                Assert.True(RowsIn(list) >= 1, "no whole row is visible: " + where);
            });
        }

        [Fact]
        public void AHiddenDrawer_StaysHidden_InTheNextPane()
        {
            WithByFile((panel, form, list) =>
            {
                DrawerToggle(panel).PerformClick();
                Pump(null, 200);
                Assert.False(CardOf(panel).Visible);
            });
            WithByFile((panel, form, list) =>
            {
                Assert.False(CardOf(panel).Visible);
                Assert.Equal("Show details", DrawerToggle(panel).AccessibleName);
            });
        }

        [Fact]
        public void ATooltip_CarriesTheFullText_OfAHeadingAndAnIssue()
        {
            WithByFile((panel, form, list) =>
            {
                var head = list.Items.Cast<ListViewItem>().First(IsHeading);
                Assert.Contains(".htm", head.ToolTipText);
                Assert.Equal(head.Text, head.ToolTipText);
                var issue = list.Items.Cast<ListViewItem>().First(i => !IsHeading(i));
                Assert.False(string.IsNullOrEmpty(issue.ToolTipText));
                Assert.Contains(" · ", issue.ToolTipText);
            });
        }

        [Fact]
        public void AWidePane_KeepsTwoColumns()
        {
            WithByFile((panel, form, list) =>
            {
                Assert.True(list.Columns[1].Width > 0, "the second column is gone at " + list.Width + " px");
            }, 500, 463);
        }

        [Fact]
        public void ACompactPane_HasOneColumn()
        {
            WithByFile((panel, form, list) =>
            {
                Assert.Equal(0, list.Columns[1].Width);
                Assert.True(list.Columns[0].Width > 0);
            });
        }

        // ---- Choose checks (#253) ----------------------------------------------------------------------------------

        /// <summary>A pane over TwoParagraphs with LongRuleGuide (one rule issue in paragraph 1) and the client given, done
        /// linting, on an off-screen form.</summary>
        private static void WithChecks(bool rulesOnly, bool aiOnStart, ReviewCache cache, ScriptedLlmClient client,
                                       Action<FindingsPanel, Form, ListView> body)
        {
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(TwoParagraphs);
                var runner = new ActionRunner(client, new PromptBuilder(null), 1024, new LongRuleGuide());
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, cache, aiOnStart) { RulesOnly = rulesOnly };
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(500, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Pump(() => list.Items.Count > 0 && All(panel).OfType<ProgressBar>().All(p => !p.Visible),
                         15000, () => string.Join(" | ", All(panel).OfType<Label>().Select(l => l.Text).Where(t => !string.IsNullOrEmpty(t))));
                    body(panel, form, list);
                }
            });
        }

        /// <summary>The status line as the pane shows it, read through the e2e state.</summary>
        private static string Status(FindingsPanel p)
        {
            var state = p.E2EState();
            return (string)state.GetType().GetProperty("status").GetValue(state);
        }

        /// <summary>Every cell of every row, as one string per row.</summary>
        private static IList<string> RowTexts(ListView list)
        {
            return list.Items.Cast<ListViewItem>()
                .Select(i => string.Join(" ", i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)))
                .ToList();
        }

        private static ScriptedLlmClient RewordClient()
        {
            return new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text(Rewords(r))(r));
        }

        [Fact]
        public void RulesAndAi_RunsTheReview_AsToday()
        {
            var client = RewordClient();
            WithChecks(false, true, null, client, (panel, form, list) =>
            {
                Assert.True(client.Calls >= 1, "the AI review should call the model");
                Assert.True(RowTexts(list).Any(t => t.Contains("In order to")), "the AI reword should be a row");
                Assert.DoesNotContain(LintChecksChoice.StatusEnding, Status(panel));
            });
        }

        [Fact]
        public void RulesOnly_MakesNoAiCall_AndShowsNoCachedAiIssue()
        {
            var cache = new ReviewCache(300, null);
            var client1 = RewordClient();
            WithChecks(false, true, cache, client1, (panel, form, list) => Assert.True(client1.Calls >= 1));

            // The cache now holds both paragraphs' AI rewords. Rules Only must not show them, or call the model.
            var client2 = RewordClient();
            WithChecks(true, false, cache, client2, (panel, form, list) =>
            {
                Assert.Equal(0, client2.Calls);
                Assert.False(RowTexts(list).Any(t => t.Contains("In order to")), "a cached AI reword is shown under Rules Only");
                Assert.True(list.Items.Count >= 1, "the rule issue should still be listed");
                Assert.EndsWith(LintChecksChoice.StatusEnding, Status(panel));
                Assert.False(All(panel).OfType<LinkLabel>().Any(l => l.Text.StartsWith(ReviewCache.AiOffNote) && l.Visible));
            });
        }

        [Fact]
        public void RulesOnly_LintAgain_StaysRulesOnly()
        {
            var client = RewordClient();
            WithChecks(true, true, null, client, (panel, form, list) =>
            {
                // The click runs on the pane's own thread, as Flare's does, so the worker reports back in order.
                var again = All(panel).OfType<Button>().Single(b => b.Text == "Lint Again");
                panel.BeginInvoke((Action)again.PerformClick);
                Pump(null, 300);   // the click runs, the check starts and its worker runs
                Pump(() => All(panel).OfType<ProgressBar>().All(p => !p.Visible), 15000);
                Pump(null, 300);   // let the list finish laying out before the form is disposed
                Assert.Equal(0, client.Calls);
                Assert.False(RowTexts(list).Any(t => t.Contains("In order to")));
                Assert.True(Status(panel).EndsWith(LintChecksChoice.StatusEnding), "status: " + Status(panel));
            });
        }

        [Fact]
        public void RulesOnly_WithNoOpenIssues_ShowsOnlyTheSummary()
        {
            const string clean = "<html><body><h1>Fees</h1><p>The fee is waived.</p></body></html>";
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(clean);
                var client = new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r));
                var runner = new ActionRunner(client, new PromptBuilder(null), 1024, new LongRuleGuide());
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, true) { RulesOnly = true };
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(500, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    // The status starts as "Linting…", so waiting for it to change also waits for the check to start and finish.
                    Pump(() => All(panel).OfType<ProgressBar>().All(p => !p.Visible) && !Status(panel).Contains("Linting"), 15000);
                    Assert.False(Status(panel).Contains(LintChecksChoice.StatusEnding));
                }
            });
        }

        private static Button ButtonNamed(FindingsPanel panel, string text)
        {
            return All(panel).OfType<Button>().Single(b => b.Text == text);
        }

        [Fact]
        public void RulesOnly_LintParagraphAndRewrite_AreDisabled_AndMakeNoCall()
        {
            var client = RewordClient();
            WithChecks(true, true, null, client, (panel, form, list) =>
            {
                list.Items[0].Selected = true;
                Pump(null, 200);
                Assert.False(ButtonNamed(panel, "Lint Paragraph").Enabled);
                Assert.False(ButtonNamed(panel, "Rewrite…").Enabled);
                Assert.Equal(0, client.Calls);
            });
        }

        [Fact]
        public void RulesAndAi_LintParagraph_IsEnabled()
        {
            var client = RewordClient();
            WithChecks(false, true, null, client, (panel, form, list) =>
            {
                list.Items[0].Selected = true;
                Pump(null, 200);
                Assert.True(ButtonNamed(panel, "Lint Paragraph").Enabled);
            });
        }

        [Fact]
        public void RulesOnly_ChangeOnAltText_ShowsTheMessage_AndMakesNoCall()
        {
            const string logo = "<html><body><h1>Logo</h1><p>Our logo is below.</p><p><img src=\"logo.png\" /></p></body></html>";
            var client = new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text("{\"findings\":[]}")(r));
            Sta(() =>
            {
                var doc = FlareSimDocument.FromXml(logo);
                var runner = new ActionRunner(client, new PromptBuilder(null), 1024);
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, true) { RulesOnly = true };
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(500, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Pump(() => list.Items.Count > 0 && All(panel).OfType<ProgressBar>().All(p => !p.Visible), 15000);
                    var row = list.Items.Cast<ListViewItem>()
                        .Single(i => i.Tag is StyleFinding f && f.Rule == "Structure.AltText");
                    row.Selected = true;
                    Pump(null, 200);
                    ButtonNamed(panel, "Add Alt Text…").PerformClick();
                    Pump(null, 200);
                    Assert.Equal(LintChecksChoice.ChangeNeedsAi, Status(panel));
                    Assert.Equal(0, client.Calls);
                }
            });
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

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

        // ---- Order 2: Tab bar for This Topic / Project (pane-tabs.md) -------------------------------------------------

        [Fact]
        public void TabBar_HiddenWithOneList_ShownWithBoth_WrapsAt150px_WithCounts()
        {
            WithPane((panel, form, list) =>
            {
                var tabs = All(panel).OfType<FlowLayoutPanel>().Single(f => f.AccessibleName == "Issues for");
                Assert.False(tabs.Visible, "tab bar hidden with only topic");

                Assert.Equal(PaneSlot.Topic, panel.Slot);
                panel.SetTabs(7, 183);
                Assert.True(tabs.Visible, "tab bar shown with both counts");

                var buttons = tabs.Controls.OfType<RadioButton>().ToList();
                Assert.Equal(2, buttons.Count);
                var topic = buttons.First(b => b.Text.StartsWith("This &Topic", StringComparison.Ordinal));
                var project = buttons.First(b => b.Text.StartsWith("&Project", StringComparison.Ordinal));
                Assert.Equal("This &Topic (7)", topic.Text);
                Assert.Equal("&Project (183)", project.Text);
                Assert.True(topic.Checked, "topic tab checked when slot is topic");
                Assert.False(project.Checked);

                Assert.Equal("This Topic, 7 issues", topic.AccessibleName);
                Assert.Equal("Project, 183 issues", project.AccessibleName);

                var wrapped = false;
                for (var i = 0; i < buttons.Count; i++)
                {
                    for (var j = i + 1; j < buttons.Count; j++)
                    {
                        if (buttons[i].Bottom < buttons[j].Top) wrapped = true;
                    }
                }
                Assert.True(wrapped || tabs.Height > 25, "buttons wrapped or tab bar is tall");
            });
        }

        [Fact]
        public void SwitchingTabs_KeepsSelectionAndRuleReview()
        {
            WithProject(new[] { Topic, Topic }, 260, 800, (panel, form) =>
            {
                var topicPanel = new FindingsPanel(new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text(OneReword(r))(r)), new PromptBuilder(null), 1024),
                    FlareSimDocument.FromXml(Topic), () => new ActionRunner(new ScriptedLlmClient(), new PromptBuilder(null), 1024), p => null, null, true);
                var topicForm = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(260, 800), Font = SystemFonts.MessageBoxFont };
                topicPanel.Dock = DockStyle.Fill;
                topicForm.Controls.Add(topicPanel);
                topicForm.Show();
                topicPanel.Begin();
                var topicList = All(topicPanel).OfType<ListView>().Single();
                Pump(() => topicList.Items.Count > 0 && All(topicPanel).OfType<ProgressBar>().All(p => !p.Visible), 15000);

                var projList = All(panel).OfType<ListView>().Single();
                var projTabs = All(panel).OfType<FlowLayoutPanel>().Single(f => f.AccessibleName == "Issues for");
                var projButtons = projTabs.Controls.OfType<RadioButton>().ToList();
                var topicRadio = projButtons.First(b => b.Text.StartsWith("This &Topic"));

                topicList.Items[0].Selected = true;
                projList.Items[0].Selected = true;
                var projRuleReview = All(panel).OfType<Button>().Any(b => b.Text == "All Rules" && b.Visible);
                Pump(null, 200);

                topicRadio.PerformClick();
                Pump(null, 200);
                Assert.True(topicList.Items[0].Selected, "topic list selection kept");
                Assert.True(projList.Items[0].Selected, "project list selection kept");
                Assert.Equal(projRuleReview, All(panel).OfType<Button>().Any(b => b.Text == "All Rules" && b.Visible));

                topicForm.Dispose();
            });
        }

        [DesktopFact]
        public void SwitchingTabs_MovesFocus_AndAltP_SwitchesWhenFocusIsInThePane()
        {
            WithPane((panel, form, list) =>
            {
                list.Items[0].Selected = true;
                list.Focus();
                Pump(null, 200);

                Assert.True(list.Focused, "list should have focus");

                var mnemonic = (bool)typeof(Control).GetMethod("ProcessMnemonic", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(char) }, null).Invoke(panel, new object[] { 'p' });
                Assert.False(mnemonic, "no mnemonic 'p' when topic panel has project not shown");
            });
        }

        [DesktopFact]
        public void ArrowKeys_SwitchTabs()
        {
            WithPane((panel, form, list) =>
            {
                panel.SetTabs(1, 1);
                PaneSlot? got = null;
                panel.TabRequested += s => got = s;
                Pump(null, 200);

                var tabs = All(panel).OfType<FlowLayoutPanel>().Single(f => f.AccessibleName == "Issues for");
                var topicRadio = tabs.Controls.OfType<RadioButton>().First(b => b.Text.StartsWith("This &Topic"));

                topicRadio.Focus();
                Pump(null, 200);
                Assert.Null(got);   // focus alone doesn't switch tabs

                var method = typeof(Control).GetMethod("ProcessDialogKey", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Keys) }, null);
                method.Invoke(topicRadio, new object[] { Keys.Right });
                Pump(null, 200);
                Assert.Equal(PaneSlot.Project, got);
            });
        }

        [Fact]
        public void E2EState_ReportsTabAndCaptions()
        {
            WithPane((panel, form, list) =>
            {
                var state = panel.E2EState();
                var tab = (string)state.GetType().GetProperty("tab").GetValue(state);
                var tabs = ((System.Collections.IEnumerable)state.GetType().GetProperty("tabs").GetValue(state)).Cast<string>().ToList();

                Assert.Equal("topic", tab);
                Assert.Empty(tabs);

                panel.SetTabs(7, 183);
                Pump(null, 100);
                state = panel.E2EState();
                tab = (string)state.GetType().GetProperty("tab").GetValue(state);
                tabs = ((System.Collections.IEnumerable)state.GetType().GetProperty("tabs").GetValue(state)).Cast<string>().ToList();

                Assert.Equal("topic", tab);
                Assert.Equal(2, tabs.Count);
                Assert.Equal("This Topic (7)", tabs[0]);
                Assert.Equal("Project (183)", tabs[1]);
            });
        }

        [DesktopFact]
        public void Switching_InFloatingMode_ReusesOneForm_AndUpdatesItsTitle()
        {
            PanelHost.ForceFloating = true;
            PanelHost.ResetForTests();
            try
            {
                Sta(() =>
                {
                    var doc = FlareSimDocument.FromXml(Topic);
                    var runner = new ActionRunner(new ScriptedLlmClient().When(r => true, r => ScriptedLlmClient.Reply.Text(OneReword(r))(r)), new PromptBuilder(null), 1024);
                    var topicPanel = new FindingsPanel(runner, doc, () => runner, p => null, null, true);
                    var projectPanel = FindingsPanel.ForProject(runner, doc, () => runner, p => null, new List<string> { "a.htm" }, "Project");

                    PanelHost.Show(topicPanel, null);
                    var form = topicPanel.FindForm();
                    Assert.NotNull(form);
                    PanelHost.Show(projectPanel, null);
                    Assert.Same(form, projectPanel.FindForm());
                    Assert.Same(projectPanel, PanelHost.Current);
                    Assert.Equal(projectPanel.Title, form.Text);
                    Assert.Equal(projectPanel.Title, form.AccessibleName);

                    // The Topic tab on the project panel shows the topic panel in the same form.
                    var radio = All(projectPanel).OfType<RadioButton>().First(b => b.Text.StartsWith("This &Topic", StringComparison.Ordinal));
                    radio.PerformClick();
                    Assert.Same(topicPanel, PanelHost.Current);
                    Assert.Equal(topicPanel.Title, form.Text);
                    Assert.Equal(topicPanel.Title, form.AccessibleName);
                    Assert.Same(form, topicPanel.FindForm());

                    // A topic switch while Project is shown replaces only the Topic slot.
                    All(topicPanel).OfType<RadioButton>().First(b => b.Text.StartsWith("&Project", StringComparison.Ordinal)).PerformClick();
                    Assert.Same(projectPanel, PanelHost.Current);
                    var newTopic = new FindingsPanel(runner, doc, () => runner, p => null, null, true);
                    PanelHost.Show(newTopic, null, false);
                    Assert.Same(projectPanel, PanelHost.Current);
                    Assert.Same(newTopic, PanelHost.TopicPanel);
                    Assert.True(topicPanel.IsDisposed);
                    Assert.False(projectPanel.IsDisposed);

                    // Close keeps both panels alive and unparented, and Reopen brings back the shown tab.
                    form.Close();
                    Assert.Null(PanelHost.Current);
                    Assert.Null(projectPanel.Parent);
                    Assert.Null(newTopic.Parent);
                    Assert.False(projectPanel.IsDisposed);
                    Assert.False(newTopic.IsDisposed);
                    Assert.True(PanelHost.Reopen(null));
                    Assert.Same(projectPanel, PanelHost.Current);
                    var again = projectPanel.FindForm();
                    Assert.NotSame(form, again);
                    again.Close();
                    newTopic.Dispose();
                    projectPanel.Dispose();
                });
            }
            finally
            {
                PanelHost.ForceFloating = false;
            }
        }
    }
}
