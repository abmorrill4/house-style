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
        private readonly Action<CommandInfo> _rewritePreset;
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
                                 Action lintTopic, Action rewrite, Action ask, Action lintProject, Action<CommandInfo> rewritePreset = null)
        {
            _host = host; _lintRunner = lintRunner; _owner = owner; _rewritePreset = rewritePreset;
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
                    foreach (var c in Commands.Context)
                        items.Add(c == Commands.Rewrite ? RewriteItem() : Item(c, c == Commands.LintTopic ? _lintTopic : _ask));
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
            doc.Closing += (s, e) => { ClearTopicWhenGone(path); Detach(path); };
            // First markers for a topic the plugin already knows about (pane open on it).
            UpdateMarkers(doc);
        }

        /// <summary>The right-click Rewrite item: it opens a submenu with Rewrite itself, then the one-click rewrites.</summary>
        private ToolStripMenuItem RewriteItem()
        {
            var parent = new ToolStripMenuItem(Commands.Rewrite.ContextLabel) { ToolTipText = Commands.Rewrite.Tooltip };
            // Named for what it does, so the submenu doesn't open with the same word as the item above it (#372).
            var plain = new ToolStripMenuItem("Rewrite Selection") { ToolTipText = Commands.Rewrite.Tooltip };
            plain.Click += (s, e) => Safe.Run(Commands.Rewrite.Label, _rewrite);
            parent.DropDownItems.Add(plain);
            foreach (var c in Commands.RewriteMenu)
            {
                var preset = c;
                var mi = new ToolStripMenuItem(preset.Label) { ToolTipText = preset.Tooltip };
                mi.Click += (s, e) => Safe.Run(preset.Label, () => { if (_rewritePreset != null) _rewritePreset(preset); });
                parent.DropDownItems.Add(mi);
            }
            return parent;
        }

        private static ToolStripMenuItem Item(CommandInfo command, Action action)
        {
            var mi = new ToolStripMenuItem(command.ContextLabel) { ToolTipText = command.Tooltip };
            mi.Click += (s, e) => Safe.Run(command.Label, action);
            return mi;
        }

        /// <summary>
        /// Drops the topic's tab once the topic is really closed. Flare also raises Closing when it reloads a topic that a
        /// Change from Lint Project edited, and the topic is open again a moment later.
        /// </summary>
        private void ClearTopicWhenGone(string path)
        {
            var wait = new Timer { Interval = 1500 };
            int checks = 0;
            wait.Tick += (s, e) =>
            {
                wait.Stop();
                bool done = true;
                try
                {
                    if (IsOpenNow(path)) return;
                    if (++checks < 2) { done = false; wait.Start(); return; }
                    wait.Dispose();
                    done = true;
                    PanelHost.ClearTopic(path);
                }
                catch (Exception ex) { Diagnostics.Log("clear topic", ex); }
                finally { if (done) wait.Dispose(); }
            };
            wait.Start();
        }

        private bool IsOpenNow(string path)
        {
            foreach (var o in _host.GetEditorContext().GetDocuments())
            {
                var d = o as IDocument;
                if (d != null && SamePath(SafePath(d), path)) return true;
            }
            return false;
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
            // An empty topic has nothing to lint, so the background checks report no issues and no error.
            if (BlockSelector.SelectBlocks(adapter.Document, new List<XmlNode>()).Count == 0) return new List<StyleFinding>();
            var blocks = runner.PickAllBlocks(adapter);
            var lint = runner.Lint(blocks);
            lint.AddRange(runner.StructureFindings(blocks));
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
                // What the pane will list: ignored and exempt issues are not counted. Nothing left means nothing to say.
                var topicPanel = PanelHost.TopicPanel;
                var same = topicPanel != null && topicPanel.HasResults && SamePath(topicPanel.TopicPath, adapter.SourcePath);
                // The fresh check, less what the pane leaves out (ignored, exempt, fixed by hand), plus the AI issues the pane already holds.
                var listed = ReportCount.Listed(lint, adapter.SourcePath, FindingsPanel.SharedDismissals, same ? topicPanel.MarkedFixed() : null);
                var ai = same ? topicPanel.OpenIssues().Count(f => f.Layer == "ai") : 0;
                var total = listed.Count + ai;
                if (total == 0) return;
                var errors = listed.Count(f => f.Severity == "error");
                var topicPath = adapter.SourcePath;
                Toast.Show(_owner(), Plural.Of(total, "issue") + " in " + Path.GetFileName(adapter.SourcePath) + (errors > 0 ? " (" + Plural.Of(errors, "error") + ")" : "") + ". Saved anyway.",
                           "Show Issues", () => ShowIssues(PaneSlot.Topic, topicPath, total));
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
                var open = PanelHost.ProjectPanel;
                if (open != null && open.IsBusy)
                {
                    // The pane is already checking the project: wait for it, and report its results. No second check.
                    var waited = 0;
                    var wait = new Timer { Interval = 500 };
                    wait.Tick += (s, e) =>
                    {
                        waited += 500;
                        if (open.IsDisposed || waited > 600000) { wait.Stop(); wait.Dispose(); return; }
                        if (open.IsBusy) return;
                        wait.Stop(); wait.Dispose();
                        ReportBuild(owner, open.OpenIssues());
                    };
                    wait.Start();
                    return;
                }
                var worker = new System.ComponentModel.BackgroundWorker();
                worker.DoWork += (s, e) => e.Result = runner.LintFiles(files);
                worker.RunWorkerCompleted += (s, e) =>
                {
                    if (e.Error != null) { Diagnostics.Log("report before build", e.Error); ErrorReporter.Background(owner, "House Style couldn't lint the project before the build.", e.Error); return; }
                    var panel = PanelHost.ProjectPanel;
                    var found = ReportCount.Listed((List<StyleFinding>)e.Result, anyProjectPath, FindingsPanel.SharedDismissals,
                                                   panel != null && panel.HasResults ? panel.MarkedFixed() : null);
                    ReportBuild(owner, found);
                };
                worker.RunWorkerAsync();
            }
            catch (Exception ex) { Diagnostics.Log("report before build", ex); ErrorReporter.Background(_owner(), "House Style couldn't lint the project before the build.", ex); }
        }

        /// <summary>The build notice for these issues (the ones the pane will list). With none left it says nothing.</summary>
        private void ReportBuild(Form owner, List<StyleFinding> found)
        {
            if (found.Count == 0) return;
            var inFiles = found.Select(f => f.DocumentPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var errors = found.Count(f => f.Severity == "error");
            var count = found.Count;
            Toast.Show(owner, "Building. " + Plural.Of(count, "issue") + " in " + Plural.Of(inFiles, "file") + (errors > 0 ? ", " + Plural.Of(errors, "error") : "") + ".",
                       "Show Issues", () => ShowIssues(PaneSlot.Project, null, count));
        }

        /// <summary>
        /// A toast's Show Issues: the results already in the pane for that scope, at the right tab, with the filters cleared so the
        /// toast's count is the list's. Checks only when the pane has no results for it, shows another topic, or would list a
        /// different number of issues than the toast said.
        /// </summary>
        private void ShowIssues(PaneSlot slot, string topicPath, int expected)
        {
            try
            {
                var panel = slot == PaneSlot.Topic ? PanelHost.TopicPanel : PanelHost.ProjectPanel;
                var current = panel != null && panel.HasResults && (slot == PaneSlot.Project || SamePath(panel.TopicPath, topicPath));
                if (current && (panel.IsBusy || panel.OpenIssues().Count == expected) && PanelHost.ShowExisting(slot, _owner())) return;
            }
            catch (Exception ex) { Diagnostics.Log("show issues", ex); }
            if (slot == PaneSlot.Topic) _lintTopic(); else _lintProject();
        }

        private static string SafePath(IDocument d) { try { return d.GetSourceUrl(); } catch (Exception) { return null; } }

        private static bool SamePath(string a, string b)
        {
            return a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
