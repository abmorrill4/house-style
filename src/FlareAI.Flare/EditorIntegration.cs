using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using B3.PluginAPIKit;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using FlareAI.Flare.UI;

namespace FlareAI.Flare
{
    /// <summary>
    /// The plugin inside Flare's editor, not only in its pane:
    ///   - right-click items (the commands in <see cref="Commands.Context"/>) in Flare's own context menu;
    ///   - a margin bar on paragraphs with open suggestions (a stylesheet on the editor view: never saved into the topic);
    ///   - live checking: a moment after typing stops, the topic is checked again (style rules only, no AI) and the pane and markers refresh;
    ///   - check on save: a quiet notice with the topic's suggestion count;
    ///   - report before build: when a build starts, the project is checked and a notice offers the report.
    /// Each feature has its own setting. None of them ever blocks typing, saving or building.
    /// </summary>
    internal sealed class EditorIntegration
    {
        private readonly IHost _host;
        private readonly Func<FlareDocumentAdapter, ActionRunner> _lintRunner;
        private readonly Action _lintTopic, _rewrite, _ask, _lintProject;
        private readonly Func<Form> _owner;
        // Keyed by file path: Flare hands out a new IDocument wrapper for the same document on each call.
        private readonly Dictionary<string, DocState> _docs = new Dictionary<string, DocState>(StringComparer.OrdinalIgnoreCase);
        private static readonly string MarkerDir = Path.Combine(Path.GetTempPath(), "FlareAI-markers");

        private sealed class DocState
        {
            public Timer Debounce;
            public string Css;          // the stylesheet applied now (path)
            public int Version;
            public List<StyleFinding> Lint;   // latest live lint, for markers when the pane shows another topic
            public IDocument Doc;             // the wrapper we subscribed to
        }

        public EditorIntegration(IHost host, Func<FlareDocumentAdapter, ActionRunner> lintRunner, Func<Form> owner,
                                 Action lintTopic, Action rewrite, Action ask, Action lintProject)
        {
            _host = host; _lintRunner = lintRunner; _owner = owner;
            _lintTopic = lintTopic; _rewrite = rewrite; _ask = ask; _lintProject = lintProject;
            FindingsPanel.Changed += panel => { var d = DocFor(panel.TopicPath); if (d != null) UpdateMarkers(d); };
            try { Directory.CreateDirectory(MarkerDir); foreach (var f in Directory.GetFiles(MarkerDir, "*.css")) File.Delete(f); } catch (Exception) { }
        }

        /// <summary>Hooks every open document (call on activation and whenever the active document changes).</summary>
        public void AttachAll()
        {
            try
            {
                var ctx = _host.GetEditorContext();
                foreach (var o in ctx.GetDocuments()) { var d = o as IDocument; if (d != null) Attach(d); }
                var active = ctx.GetActiveDocument();
                if (active != null) Attach(active);
            }
            catch (Exception ex) { Diagnostics.Log("editor attach", ex); }
        }

        private void Attach(IDocument doc)
        {
            var path = SafePath(doc);
            if (path == null || _docs.ContainsKey(path)) return;
            if (!(path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".flsnp", StringComparison.OrdinalIgnoreCase))) return;
            var state = new DocState { Debounce = new Timer { Interval = 1200 }, Doc = doc };
            _docs[path] = state;

            // Right-click: Flare adds these to its own context menu for this document.
            try
            {
                var items = doc.GetUserPendingContextMenuItems();
                if (items != null && !items.Any(i => i.Text == Commands.Context[0].ContextLabel))
                {
                    items.Add(new ToolStripSeparator());
                    foreach (var c in Commands.Context) items.Add(Item(c, c == Commands.LintTopic ? _lintTopic : c == Commands.Rewrite ? _rewrite : _ask));
                }
            }
            catch (Exception ex) { Diagnostics.Log("context menu", ex); }

            state.Debounce.Tick += (s, e) =>
            {
                state.Debounce.Stop();
                var topic = PanelHost.TopicPanel;
                var project = PanelHost.ProjectPanel;
                List<StyleFinding> lint = null;
                var mine = false;
                if (PluginSettings.Load().LiveCheck)
                {
                    try
                    {
                        FlareDocumentAdapter adapter;
                        lint = Lint(doc, out adapter);
                        if (lint != null)
                        {
                            state.Lint = lint;
                            mine = topic != null && SamePath(topic.TopicPath, adapter.SourcePath);
                        }
                    }
                    catch (Exception ex) { lint = null; Diagnostics.Log("live check", ex); ErrorReporter.Background(_owner(), "House Style couldn't lint the topic as you type.", ex); }
                }
                // Undone changes, issues fixed by hand, then Live check's lint: one order, in the pane (it raises Changed, so the markers follow).
                if (topic != null) try { topic.AfterEdit(mine ? lint : null); } catch (Exception ex) { Diagnostics.Log("after edit: " + ex.GetType().Name); }
                if (project != null) try { project.AfterEdit(null); } catch (Exception ex) { Diagnostics.Log("after edit: " + ex.GetType().Name); }
                if (lint != null && !mine) UpdateMarkers(doc);
            };
            doc.ContentChanged += (s, e) =>
            {
                var watch = PanelHost.ProjectPanel != null || (PanelHost.TopicPanel != null && SamePath(PanelHost.TopicPanel.TopicPath, path));
                if (!watch && !PluginSettings.Load().LiveCheck) return;
                state.Debounce.Stop(); state.Debounce.Start();   // a moment after typing stops
            };
            doc.Saved += (s, e) => OnSaved(doc, state);
            doc.Closing += (s, e) => { PanelHost.ClearTopic(path); Detach(path); };
            // First markers for a topic the plugin already knows about (pane open on it).
            UpdateMarkers(doc);
        }

        private static ToolStripMenuItem Item(CommandInfo command, Action action)
        {
            var mi = new ToolStripMenuItem(command.ContextLabel) { ToolTipText = command.Tooltip };
            mi.Click += (s, e) => Safe.Run(command.Label, action);
            return mi;
        }

        private void Detach(string path)
        {
            DocState state;
            if (!_docs.TryGetValue(path, out state)) return;
            state.Debounce.Stop(); state.Debounce.Dispose();
            if (state.Css != null) try { File.Delete(state.Css); } catch (Exception) { }
            _docs.Remove(path);
        }

        // ---- live check and save ------------------------------------------------------------------------------

        /// <summary>Style rules + structure check of the whole topic, from the editor's current DOM. Free and fast (no AI).</summary>
        private List<StyleFinding> Lint(IDocument doc, out FlareDocumentAdapter adapter)
        {
            adapter = new FlareDocumentAdapter(doc);
            if (!adapter.IsEditableTopic) return null;
            var runner = _lintRunner(adapter);
            if (runner == null) return null;
            var blocks = runner.PickAllBlocks(adapter);
            var lint = runner.Lint(blocks);
            lint.AddRange(StructureCheck.Of(blocks));
            return lint;
        }

        private void OnSaved(IDocument doc, DocState state)
        {
            try
            {
                var saved = SafePath(doc);
                var report = PanelHost.ProjectPanel;
                if (report != null && saved != null && report.Covers(saved))
                    try { report.RecheckSaved(saved, new FlareDocumentAdapter(doc).Document); }   // the saved document's own DOM; nothing read from disk
                    catch (Exception ex) { Diagnostics.Log("recheck saved: " + saved + " " + ex.GetType().Name); }
                var settings = PluginSettings.Load();
                if (!settings.CheckOnSave) return;
                if (FlareAI.Core.Editing.SelfSaves.IsOwn(SafePath(doc))) return;   // the plugin's own save (Apply in Check project): no notice
                FlareDocumentAdapter adapter;
                var lint = Lint(doc, out adapter);
                if (lint == null) return;
                state.Lint = lint;
                if (lint.Count == 0) return;   // nothing to say
                var errors = lint.Count(f => f.Severity == "error");
                Toast.Show(_owner(), Plural.Of(lint.Count, "issue") + " in " + Path.GetFileName(adapter.SourcePath) + (errors > 0 ? " (" + Plural.Of(errors, "error") + ")" : "") + ". Saved anyway.",
                           "Show Issues", _lintTopic);
                UpdateMarkers(doc);
            }
            catch (Exception ex) { Diagnostics.Log("check on save", ex); ErrorReporter.Background(_owner(), "House Style couldn't lint the topic you saved.", ex); }
        }

        // ---- markers ------------------------------------------------------------------------------------------

        private void UpdateMarkers(IDocument doc)
        {
            DocState state;
            var path = doc == null ? null : SafePath(doc);
            if (path == null || !_docs.TryGetValue(path, out state)) return;
            try
            {
                var adapter = new FlareDocumentAdapter(doc);
                var show = PluginSettings.Load().ShowMarkers;
                List<StyleFinding> findings = null;
                var panel = PanelHost.TopicPanel;
                if (show && panel != null && SamePath(panel.TopicPath, adapter.SourcePath)) findings = panel.OpenTopicFindings();   // style rules + AI
                else if (show) findings = state.Lint;
                var marks = new List<KeyValuePair<XmlNode, string>>();
                foreach (var f in findings ?? new List<StyleFinding>())
                {
                    XmlText run; int start;
                    var block = FindingLocator.TryLocate(adapter.Document, f, out run, out start) ? TextRuns.BlockOf(run) : f.Block;
                    if (block != null && block.OwnerDocument == adapter.Document) marks.Add(new KeyValuePair<XmlNode, string>(block, f.Severity));
                }
                var css = marks.Count == 0 ? null : MarkerCss.Css(marks);
                // Flare caches a stylesheet by path: write each version under a new name, then swap.
                var previous = state.Css;
                state.Css = null;
                if (css != null)
                {
                    state.Css = Path.Combine(MarkerDir, "m" + doc.GetHashCode().ToString("x") + "-" + (++state.Version) + ".css");
                    File.WriteAllText(state.Css, css);
                    doc.ApplyStylesheet(state.Css);
                }
                if (previous != null) { try { doc.RemoveStylesheet(previous); } catch (Exception) { } try { File.Delete(previous); } catch (Exception) { } }
                if (css != null || previous != null) doc.UpdateView();
            }
            catch (Exception ex) { Diagnostics.Log("markers", ex); ErrorReporter.Background(_owner(), "House Style couldn't mark the paragraphs that have issues.", ex); }
        }

        private IDocument DocFor(string path)
        {
            var key = _docs.Keys.FirstOrDefault(k => SamePath(k, path));
            return key == null ? null : _docs[key].Doc;
        }

        // ---- report before build -------------------------------------------------------------------------------

        /// <summary>When a build starts: check the project in the background and offer the report. The build isn't held up.</summary>
        public void OnCompileStart(string anyProjectPath)
        {
            try
            {
                if (!PluginSettings.Load().ReportBeforeBuild || string.IsNullOrEmpty(anyProjectPath)) return;
                var files = ActionRunner.ProjectFiles(anyProjectPath);
                if (files.Count == 0) return;
                var activeDoc = _host.GetEditorContext().GetActiveDocument();
                var runner = activeDoc == null ? null : _lintRunner(new FlareDocumentAdapter(activeDoc));
                if (runner == null) return;
                var owner = _owner();
                var worker = new System.ComponentModel.BackgroundWorker();
                worker.DoWork += (s, e) => e.Result = runner.LintFiles(files);
                worker.RunWorkerCompleted += (s, e) =>
                {
                    if (e.Error != null) { Diagnostics.Log("report before build", e.Error); ErrorReporter.Background(owner, "House Style couldn't lint the project before the build.", e.Error); return; }
                    var found = (List<StyleFinding>)e.Result;
                    if (found.Count == 0) { Toast.Show(owner, "Building. No issues found in the project's " + Plural.Of(files.Count, "file") + "."); return; }
                    var inFiles = found.Select(f => f.DocumentPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    var errors = found.Count(f => f.Severity == "error");
                    Toast.Show(owner, "Building. " + Plural.Of(found.Count, "issue") + " in " + Plural.Of(inFiles, "file") + (errors > 0 ? ", " + Plural.Of(errors, "error") : "") + ".",
                               "Show Issues", _lintProject);
                };
                worker.RunWorkerAsync();
            }
            catch (Exception ex) { Diagnostics.Log("report before build", ex); ErrorReporter.Background(_owner(), "House Style couldn't lint the project before the build.", ex); }
        }

        private static string SafePath(IDocument d) { try { return d.GetSourceUrl(); } catch (Exception) { return null; } }

        private static bool SamePath(string a, string b)
        {
            return a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
