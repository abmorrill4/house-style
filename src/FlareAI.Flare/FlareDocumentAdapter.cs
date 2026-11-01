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
