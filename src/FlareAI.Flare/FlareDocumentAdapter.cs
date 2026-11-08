using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using B3.PluginAPIKit;
using FlareAI.Core;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;

namespace FlareAI.Flare
{
    /// <summary>
    /// Thin wrapper over Flare's IDocument/ISelection. Holds no logic beyond marshalling.
    /// Note: GetXmlDocument() is fetched once per adapter instance so the nodes we pick and the nodes we
    /// replace come from the same DOM object (unknown whether Flare returns a live DOM or a copy — see PLAN.md).
    /// </summary>
    public sealed class FlareDocumentAdapter : IDocumentAdapter
    {
        private readonly IDocument _doc;
        private XmlDocument _xml;

        public FlareDocumentAdapter(IDocument doc) { _doc = doc; }

        /// <summary>
        /// Inserts a variable at the caret by running Flare's own Insert Variable command on the editor control, so Flare
        /// itself records it as one tracked, undoable change. Reflection: the types are public but not in the plugin API.
        /// </summary>
        public bool InsertVariable(string name)
        {
            string error;
            if (TryInsertVariable(name, out error)) return true;
            Diagnostics.Log("InsertVariable: " + error);
            return false;
        }

        public bool TryInsertVariable(string name, out string error)
        {
            error = null;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var field = _doc.GetType().GetField("mBoxView", flags);
                var box = field == null ? null : field.GetValue(_doc);
                if (box == null) { error = "the editor control was not found"; return false; }
                Type Find(string n) { foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType(n, false); if (t != null) return t; } return null; }
                var routerType = Find("B3.AppKit.CommandRouter");
                var cmdType = Find("B3.XmlTextApi.TextVariableInsertCommand");
                if (routerType == null || cmdType == null) { error = "Flare's variable command was not found"; return false; }
                var sharedProp = routerType.GetProperty("Shared", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var router = sharedProp != null ? sharedProp.GetValue(null, null) : routerType.GetField("Shared", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public).GetValue(null);
                var cmd = Activator.CreateInstance(cmdType, new object[] { name });
                System.Reflection.MethodInfo try4 = null;
                foreach (var m in routerType.GetMethods()) if (m.Name == "TryInvoke" && m.GetParameters().Length == 4) try4 = m;
                if (try4 == null) { error = "TryInvoke not found"; return false; }
                var tracking = box.GetType().GetProperty("TrackChanges", flags);
                var was = tracking == null ? null : tracking.GetValue(box, null);
                if (tracking == null || !tracking.CanWrite) { error = "tracking could not be turned on"; return false; }
                try
                {
                    tracking.SetValue(box, true, null);
                    var ok = try4.Invoke(router, new object[] { box, null, cmd, name });
                    return !(ok is bool) || (bool)ok;
                }
                finally { try { tracking.SetValue(box, was, null); } catch (Exception) { } }
            }
            catch (Exception ex) { error = ex.GetBaseException().Message; return false; }
        }

        /// <summary>True when the plugin opened this file (it wasn't open in Flare before): bulk edits save and close it.</summary>
        public bool OpenedByPlugin { get; set; }

        /// <summary>
        /// Saves (tracked changes stay tracked: Review &gt; Reject still works) and closes the editor. Bulk edits across a
        /// project must not leave one tab per file open: past ~100 tabs Windows runs out of window handles and Flare crashes.
        /// </summary>
        public void SaveAndClose()
        {
            FlareAI.Core.Editing.SelfSaves.Mark(SourcePath);   // the check-on-save notice is for the writer's own saves
            _doc.Save();
            _doc.Close();
        }

        /// <summary>Saves as the writer's Ctrl+S does: not marked as the plugin's own save, so check-on-save runs (E2E hook only).</summary>
        internal bool SaveAsWriter() { return _doc.Save(); }

        public string SourcePath
        {
            get { try { return _doc == null ? null : _doc.GetSourceUrl(); } catch (Exception) { return null; } }
        }

        public bool IsEditableTopic
        {
            get
            {
                if (_doc == null) return false;
                var p = SourcePath ?? "";
                var ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext != ".htm" && ext != ".html" && ext != ".flsnp") return false;
                return BlockSelector.FindBody(Document) != null;
            }
        }

        public XmlDocument Document
        {
            get
            {
                if (_xml == null) _xml = _doc.GetXmlDocument();
                return _xml;
            }
        }

        public string SelectionText
        {
            get { try { return _doc.Selection != null ? (_doc.Selection.GetText() ?? "") : ""; } catch (Exception) { return ""; } }
        }

        public IList<XmlNode> SelectionNodes
        {
            get
            {
                try
                {
                    var sel = _doc.Selection;
                    if (sel == null) return new List<XmlNode>();
                    var list = sel.GetXmlNodeList();
                    return list ?? new List<XmlNode>();
                }
                catch (Exception) { return new List<XmlNode>(); }
            }
        }

        public void ReplaceBlocks(string operationName, IList<XmlNode> oldNodes, IList<XmlNode> newNodes)
        {
            if (oldNodes == null || oldNodes.Count == 0) throw new InvalidOperationException("Nothing to replace.");
            // Last line of defence (callers check first): never a node operation Flare 22 can't take (flare-api-notes.md).
            var refusal = NodeWrites.Refusal(oldNodes, newNodes ?? new List<XmlNode>());
            if (refusal != null) throw new InvalidOperationException(refusal);
            _doc.StartOperation(operationName);
            try
            {
                // Replace the first old block with the first new one, insert the remaining new blocks after it,
                // then remove any remaining old blocks. Every step is a tracked change.
                var parent = oldNodes[0].ParentNode;
                var anchor = oldNodes[0];
                var anchorIndex = IndexOf(parent, anchor);

                if (newNodes.Count > 0)
                {
                    _doc.ReplaceDocumentNode(anchor, newNodes[0], true);
                    for (var i = 1; i < newNodes.Count; i++)
                        _doc.InsertDocumentNode(parent, newNodes[i], anchorIndex + i, true);
                }
                else
                {
                    _doc.RemoveDocumentNode(anchor, true);
                }
                for (var i = 1; i < oldNodes.Count; i++)
                    _doc.RemoveDocumentNode(oldNodes[i], true);
            }
            finally
            {
                _doc.EndOperation();
                try { _doc.UpdateView(); } catch (Exception) { }
            }
        }

        public void InsertBlocks(string operationName, XmlNode parent, int index, IList<XmlNode> newNodes)
        {
            var refusal = NodeWrites.InsertRefusal(newNodes);
            if (refusal != null) throw new InvalidOperationException(refusal);
            _doc.StartOperation(operationName);
            try
            {
                for (var i = 0; i < newNodes.Count; i++) _doc.InsertDocumentNode(parent, newNodes[i], index + i, true);
            }
            finally
            {
                _doc.EndOperation();
                try { _doc.UpdateView(); } catch (Exception) { }
            }
        }

        /// <summary>For the E2E hook only: the same tracked insert without the refusal, to show that a refused insert really does crash Flare.</summary>
        internal void E2EInsertRaw(XmlNode parent, int index, IList<XmlNode> newNodes)
        {
            _doc.StartOperation("E2E raw insert");
            try { for (var i = 0; i < newNodes.Count; i++) _doc.InsertDocumentNode(parent, newNodes[i], index + i, true); }
            finally { _doc.EndOperation(); try { _doc.UpdateView(); } catch (Exception) { } }
        }

        /// <summary>For the E2E hook: what Flare reports for the caret, so a test can see how to find the paragraph it is in.</summary>
        internal object E2ECaret()
        {
            var nodes = SelectionNodes;
            var list = new List<object>();
            foreach (var n in nodes)
                list.Add(new { type = n.NodeType.ToString(), name = n.Name, parent = n.ParentNode == null ? null : n.ParentNode.Name, text = n.InnerText.Length > 60 ? n.InnerText.Substring(0, 60) : n.InnerText });
            return new { selected = SelectionText, nodes = list };
        }

        public bool SelectText(XmlText run, int start, int length)
        {
            try
            {
                if (run.ParentNode == null || start < 0 || start + length > run.Value.Length) return false;
                int us, ul;
                TextRuns.UniqueSpan(run.Value, start, length, out us, out ul);
                start = us; length = ul;
                var target = run.Value.Substring(start, length);
                // Put the caret in the run's element first, then search. We don't rely on whether Flare's
                // Select(string) searches from the caret or from the top: we repeat until the selection is
                // the right occurrence (inside this run), or give up.
                _doc.Select(run.ParentNode);
                for (var attempt = 0; attempt < 25; attempt++)
                {
                    _doc.Select(target);
                    var sel = _doc.Selection;
                    if (sel == null || sel.GetText() != target) return false;
                    var nodes = sel.GetXmlNodeList();
                    if (nodes != null && nodes.Contains(run))
                    {
                        PluginSelection.Remember(SourcePath, target);
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SelectText", ex);
                return false;
            }
        }

        /// <summary>
        /// Select an image. <c>Select(XmlNode)</c> doesn't select one (verified Flare 22), and in Flare's text positions an image takes
        /// no room, so a selection can't hold only the image. This selects the image with the character before it and the one after
        /// it, which Flare reports with the image among the selected nodes. It finds the place by selecting the text beside the image
        /// (<see cref="SelectText"/>), which also gives that text's position. An image at the end of a paragraph, or alone in one, takes
        /// the character on its one side, and failing that Flare's caret is put where the image is.
        /// </summary>
        public bool SelectNode(XmlNode node)
        {
            try
            {
                if (node == null || node.ParentNode == null) return false;
                var next = NeighborRun(node, true);
                var previous = NeighborRun(node, false);
                if (next == null && previous == null) return false;
                _doc.Select(node.ParentNode);   // the caret goes to the start of the paragraph, so the search below finds the text after it first
                int s, l;
                if (previous != null && next != null)
                {
                    // An image alone in its paragraph doesn't move the caret: start from the text before it.
                    int ps, pl;
                    TextRuns.UniqueSpan(previous.Value, Math.Max(0, previous.Value.Length - 40), Math.Min(previous.Value.Length, 40), out ps, out pl);
                    SelectText(previous, ps, pl);
                }
                int at;   // where the image is, in Flare's text positions
                if (next != null)
                {
                    TextRuns.UniqueSpan(next.Value, 0, Math.Min(next.Value.Length, 40), out s, out l);
                    if (!SelectText(next, s, l)) return false;
                    at = _doc.GetCursorPosition() - s;
                }
                else
                {
                    TextRuns.UniqueSpan(previous.Value, Math.Max(0, previous.Value.Length - 40), Math.Min(previous.Value.Length, 40), out s, out l);
                    if (!SelectText(previous, s, l)) return false;
                    at = _doc.GetCursorPosition() - s + previous.Value.Length;
                }
                var tries = new List<int[]>();
                if (previous != null && next != null) tries.Add(new[] { at - 1, 2 });
                if (previous != null) tries.Add(new[] { at - 1, 1 });
                if (next != null) tries.Add(new[] { at, 1 });
                tries.Add(new[] { at, 0 });
                foreach (var t in tries)
                {
                    _doc.SetCursorPosition(t[0]);
                    _doc.Selection.SetSelectionLength(t[1]);
                    if (SelectedImage(node)) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SelectNode", ex);
                return false;
            }
        }

        /// <summary>The nearest text with words in it before or after the node, in document order (the images between have no text).</summary>
        private XmlText NeighborRun(XmlNode node, bool forward)
        {
            var body = BlockSelector.FindBody(Document);
            if (body == null) return null;
            var runs = new HashSet<XmlNode>();
            foreach (var r in TextRuns.Of(new List<XmlNode> { body })) runs.Add(r);
            var order = new List<XmlNode>();
            Flatten(body, order);
            var i = order.IndexOf(node);
            if (i < 0) return null;
            for (var j = i + (forward ? 1 : -1); j >= 0 && j < order.Count; j += forward ? 1 : -1)
            {
                var t = order[j] as XmlText;
                if (t != null && t.Value.Trim().Length > 0 && runs.Contains(t)) return t;
            }
            return null;
        }

        private static void Flatten(XmlNode n, List<XmlNode> order)
        {
            order.Add(n);
            foreach (XmlNode c in n.ChildNodes) Flatten(c, order);
        }

        private bool SelectedImage(XmlNode node)
        {
            var sel = _doc.Selection;
            var nodes = sel == null ? null : sel.GetXmlNodeList();
            if (nodes == null) return false;
            foreach (var n in nodes) if (n == node) return true;
            // Not the image itself: only text or something else is selected, so say so rather than pretend.
            Diagnostics.Log("SelectNode: the selection doesn't hold the image (" + nodes.Count + " nodes)");
            return false;
        }

        public void Edit(string operationName, Action body)
        {
            _doc.StartOperation(operationName);
            try { body(); }
            finally
            {
                _doc.EndOperation();
                RemoveSelectionMarkers();
                try { _doc.UpdateView(); } catch (Exception) { }
            }
        }

        /// <summary>
        /// Native word-level tracked change: select the span and type over it with track changes on, the same
        /// path as an author typing. (Flare's node API can't do sub-element tracked edits that also redo cleanly.)
        /// </summary>
        public bool ReplaceSpan(XmlText run, int start, int length, string replacement)
        {
            // Select(string) cannot target an offset, so widen the span until it is unique in its text node
            // and re-type the extra characters unchanged ("you" in "your … you" → "you " / "members ").
            int s, l;
            TextRuns.UniqueSpan(run.Value, start, length, out s, out l);
            var text = run.Value;
            replacement = text.Substring(s, start - s) + replacement + text.Substring(start + length, s + l - start - length);
            start = s; length = l;
            if (!SelectText(run, start, length)) return false;
            var sel = _doc.Selection;
            var wasTracking = sel.TrackChange;
            try
            {
                sel.TrackChange = true;
                return sel.ReplaceText(replacement);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ReplaceSpan", ex);
                return false;
            }
            finally
            {
                try { sel.TrackChange = wasTracking; } catch (Exception) { }
                RemoveSelectionMarkers();
            }
        }

        public string VariableValue(string qualifiedName)
        {
            try
            {
                var dot = qualifiedName.IndexOf('.');
                if (dot <= 0) return null;
                return _doc.LookupVariableValue(qualifiedName.Substring(0, dot), qualifiedName.Substring(dot + 1));
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Flare 22's ISelection leaves internal &lt;SelectionMarker/&gt; (and Start/End) elements in the DOM after
        /// ReplaceText; they would be saved into the topic and confuse the next selection. Remove them untracked.
        /// </summary>
        private void RemoveSelectionMarkers()
        {
            var markers = new List<XmlNode>();
            foreach (var name in new[] { "SelectionMarker", "SelectionStartMarker", "SelectionEndMarker" })
                foreach (XmlNode m in Document.GetElementsByTagName(name)) markers.Add(m);
            foreach (var m in markers)
            {
                if (m.ParentNode == null) continue;
                try { _doc.RemoveDocumentNode(m, false); }
                catch (Exception ex) { Diagnostics.Log("RemoveSelectionMarkers", ex); }
            }
        }

        private static int IndexOf(XmlNode parent, XmlNode child)
        {
            var i = 0;
            for (var n = parent.FirstChild; n != null; n = n.NextSibling, i++)
                if (n == child) return i;
            return -1;
        }
    }
}
