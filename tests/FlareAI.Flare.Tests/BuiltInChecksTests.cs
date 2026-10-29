using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using B3.PluginAPIKit;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using FlareAI.Flare;
using FlareAI.Flare.UI;
using FlareAI.TestKit.Editor;
using Xunit;

namespace FlareAI.Flare.Tests
{
    // Sets the static Diagnostics.Sink through the lint path, so it shares the "Panes" collection with the other UI tests.
    [Collection("Panes")]
    public class BuiltInChecksTests : IDisposable
    {
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "flareai-builtin-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        private readonly string _prevRoot = FlareAI.Core.Paths.Root;   // the test run's redirected folder, put back on Dispose

        // The writer's data folder (onboarding notes, UI state) is a temp folder: these tests never write to %APPDATA%.
        public BuiltInChecksTests() { FlareAI.Core.Paths.Root = Path.Combine(_temp, "root"); }

        public void Dispose()
        {
            FlareAI.Core.Paths.Root = _prevRoot;
            try { Directory.Delete(_temp, true); } catch (Exception) { }
        }

        private const string Ns = "xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"";

        // h2 then h4 skips a level (heading-order fires); "Kick off" is in the team word list (other findings remain).
        private const string Topic = "<h2>Intro</h2><h4>Details</h4><p>Kick off the import.</p>";

        // The built-in checks' section in Style rules: its source is "Built-in checks", its page "Structure and accessibility".
        private const string BuiltInGroup = "Built-in checks › Structure and accessibility";

        [Fact]
        public void StyleRules_ListsBuiltInChecks_UnderTheirOwnSection_WithNoAvoidLine()
        {
            Sta(() =>
            {
                var bundled = Path.Combine(Repo, "styleguide");
                var engine = new StyleEngine(new[] { bundled }, bundled);
                using (var form = new StyleRulesForm(null, engine, null, null, p => false))
                {
                    form.Show();
                    var list = All(form).OfType<ListView>().Single();
                    var detail = (TextBox)typeof(StyleRulesForm).GetField("_detail", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                    Pump(() => list.Items.Count > 0, 15000);
                    var rows = list.Items.Cast<ListViewItem>().Where(i => i.Group != null && i.Group.Header == BuiltInGroup).ToList();
                    Assert.Equal(6, rows.Count);
                    foreach (var row in rows)
                    {
                        row.Selected = true;
                        Pump(() => detail.Text.StartsWith(row.Text, StringComparison.Ordinal), 5000);   // the details follow the selection
                        Assert.Contains("Source: Built-in checks", detail.Text);
                        Assert.Contains("Use: rewrite it", detail.Text);   // the rest of the details is unchanged
                        Assert.DoesNotContain("Avoid", detail.Text);
                    }
                }
            });
        }

        [Fact]
        public void DisableRule_IsEnabled_ForAStructureIssue()
        {
            Sta(() =>
            {
                var bundled = Path.Combine(Repo, "styleguide");
                var engine = new StyleEngine(new[] { bundled }, bundled);
                // A heading jump (h1 to h3) is a built-in structure finding; the topic has no other issues.
                var doc = FlareSimDocument.FromXml("<html " + Ns + "><body><h1>Intro</h1><h3>Details</h3><p>Plain text.</p></body></html>");
                var runner = new ActionRunner(null, new PromptBuilder(null), 0, engine);
                var panel = new FindingsPanel(runner, doc, () => runner, p => null, null, false);
                using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false,
                                             FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(500, 800), Font = SystemFonts.MessageBoxFont })
                {
                    panel.Dock = DockStyle.Fill;
                    form.Controls.Add(panel);
                    form.Show();
                    panel.Begin();
                    var list = All(panel).OfType<ListView>().Single();
                    Func<ListViewItem, bool> structure = i => i.Tag is StyleFinding && ((StyleFinding)i.Tag).Source == "structure";
                    Pump(() => list.Items.Cast<ListViewItem>().Any(structure), 15000);
                    var item = list.Items.Cast<ListViewItem>().First(structure);
                    item.Selected = true;
                    var ignore = (Button)typeof(FindingsPanel).GetField("_ignore", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                    Assert.True(ignore.Enabled);
                }
            });
        }

        /// <summary>Every control under <paramref name="c"/>, at any depth.</summary>
        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; }
        }

        /// <summary>Runs the message loop until <paramref name="done"/>, or fails after <paramref name="ms"/>.</summary>
        private static void Pump(Func<bool> done, int ms)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (done()) { Application.DoEvents(); return; }
                Thread.Sleep(10);
            }
            throw new TimeoutException("The window didn't finish loading.");
        }

        /// <summary>Runs the body on an STA thread (Windows Forms needs one), and rethrows its failure.</summary>
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

        [Fact]
        public void EditorIntegration_LintList_DropsAnOffStructureRule()
        {
            var on = EngineWithOverrides(Path.Combine(_temp, "On"), "");
            var off = EngineWithOverrides(Path.Combine(_temp, "Off"), "structure:heading-order = off\n");

            // The control: without the override, the live check reports the heading jump.
            var control = LintTopic(on, Topic);
            Assert.Contains(control, f => f.Rule == "Structure.HeadingOrder");

            var lint = LintTopic(off, Topic);
            Assert.DoesNotContain(lint, f => f.Rule == "Structure.HeadingOrder" || f.RuleId == "structure:heading-order");
            Assert.NotEmpty(lint);
        }

        /// <summary>
        /// Runs EditorIntegration's live check (its private Lint(IDocument) call, which holds the StructureFindings call) on a
        /// topic. Lint is not public and the editor's IDocument is Flare's, so the test calls it by reflection.
        /// </summary>
        private static List<StyleFinding> LintTopic(StyleEngine engine, string body)
        {
            var editor = new EditorIntegration(null, adapter => new ActionRunner(null, new PromptBuilder(null), 0, engine), () => null,
                                               null, null, null, null);
            var lint = typeof(EditorIntegration).GetMethod("Lint", BindingFlags.NonPublic | BindingFlags.Instance);
            var args = new object[] { new FakeDocument("<html " + Ns + "><body>" + body + "</body></html>"), null };
            return (List<StyleFinding>)lint.Invoke(editor, args);
        }

        /// <summary>A project folder with an overrides.ini (the team guide shape the other structure tests use), read by the engine.</summary>
        private static StyleEngine EngineWithOverrides(string folder, string ini)
        {
            var team = TeamGuide(folder, ini);
            return new StyleEngine(new[] { team }, Path.Combine(Repo, "styleguide"));
        }

        private static string TeamGuide(string folder, string ini)
        {
            Write(folder, "Team.flprj", "<CatapultProject />");
            Write(folder, @"Content\Resources\StyleGuide\Words.htm", "<html " + Ns + "><body><h1>Words</h1>" +
                "<table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr><td>kick off</td><td>start</td><td>Kick off the import.</td></tr></table></body></html>");
            Write(folder, @"Content\Resources\StyleGuide\overrides.ini", ini);
            return folder;
        }

        private static void Write(string root, string rel, string text)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        private static string Repo
        {
            get
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
                return d.FullName;
            }
        }

        /// <summary>The editor's document: only the DOM and the source path, which is all the live check reads.</summary>
        private sealed class FakeDocument : IDocument
        {
            private readonly XmlDocument _xml = new XmlDocument();

            public FakeDocument(string xhtml)
            {
                _xml.LoadXml(xhtml);
            }

            public string GetSourceUrl() { return "Content/Topic.htm"; }
            public XmlDocument GetXmlDocument() { return _xml; }
            public ISelection Selection { get { return null; } }
            public string LookupVariableValue(string set, string name) { return null; }

            public bool ApplyStylesheet(string url) { throw new NotSupportedException(); }
            public void Close() { throw new NotSupportedException(); }
            public event FormClosingEventHandler Closing { add { } remove { } }
            public event EventHandler ContentChanged { add { } remove { } }
            public void EndOperation() { throw new NotSupportedException(); }
            public List<ToolStripItem> GetUserPendingContextMenuItems() { throw new NotSupportedException(); }
            public void InsertDocumentNode(XmlNode parentNode, XmlNode newNode, int pos, bool trackChange) { throw new NotSupportedException(); }
            public void RemoveDocumentNode(XmlNode targetNode, bool trackChange) { throw new NotSupportedException(); }
            public bool RemoveStylesheet(string url) { throw new NotSupportedException(); }
            public void ReplaceDocumentNode(XmlNode targetNode, XmlNode newNode, bool trackChange) { throw new NotSupportedException(); }
            public bool Save() { throw new NotSupportedException(); }
            public event EventHandler Saved { add { } remove { } }
            public void Select(string searchString) { throw new NotSupportedException(); }
            public void Select(XmlNode node) { throw new NotSupportedException(); }
            public void StartOperation(string name = null) { throw new NotSupportedException(); }
            public void UpdateView() { throw new NotSupportedException(); }
        }
    }
}
