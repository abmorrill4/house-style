using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using B3.PluginAPIKit;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;
using FlareAI.Flare;
using Xunit;

namespace FlareAI.Flare.Tests
{
    // Sets the static Diagnostics.Sink through the lint path, so it shares the "Panes" collection with the other UI tests.
    [Collection("Panes")]
    public class BuiltInChecksTests : IDisposable
    {
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "flareai-builtin-" + Guid.NewGuid().ToString("N").Substring(0, 10));

        public void Dispose()
        {
            try { Directory.Delete(_temp, true); } catch (Exception) { }
        }

        private const string Ns = "xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"";

        // h2 then h4 skips a level (heading-order fires); "Kick off" is in the team word list (other findings remain).
        private const string Topic = "<h2>Intro</h2><h4>Details</h4><p>Kick off the import.</p>";

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
