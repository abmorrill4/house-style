using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using FlareAI.TestKit.Conformance;
using FlareAI.TestKit.Workspace;

namespace FlareAI.TestKit.Editor
{
    /// <summary>
    /// A Flare-faithful fake of the editor behind <see cref="IDocumentAdapter"/>: what <c>FlareDocumentAdapter</c> over
    /// Flare 22 does, including the traps, as verified live and written down in docs/flare-api-notes.md
    /// ("Verified write behavior", "Variables and redo"). Test tests drive real Core flows through it.
    /// <list type="bullet">
    /// <item><see cref="Edit"/> is one undo step (StartOperation/EndOperation); <see cref="ReplaceSpan"/> outside it throws.</item>
    /// <item><see cref="ReplaceSpan"/> writes real <c>MadCap:change</c> markup: one Remove/Add id pair per operation; the ids
    /// reach <c>head/MadCap:changeData</c> only when the operation ends. The edited text node is split: the original
    /// <see cref="XmlText"/> keeps the text before the span (and leaves the tree when that is empty).</item>
    /// <item><see cref="SelectText"/> selects the first match of the span's text in its run, like <c>Select(string)</c>;
    /// with <see cref="WidenSpans"/> (the adapter's behaviour) the span is widened until unique first.</item>
    /// <item><see cref="ReplaceBlocks"/> is tracked and copies the new node's content and attributes into the old element
    /// (references survive; the new node is never attached); a text-node target throws.</item>
    /// <item><see cref="Undo"/>, <see cref="Redo"/>, <see cref="AcceptAll"/>, <see cref="RejectAll"/>; Redo of a node
    /// operation whose content holds a <c>MadCap:variable</c> throws <see cref="FlareSimRedoCrashException"/>.</item>
    /// <item>Writes to the live DOM that bypass the adapter are recorded in <see cref="UntrackedWrites"/>.</item>
    /// </list>
    /// Simplifications: a tracked block replacement marks the whole old content removed and the whole new content added
    /// (Flare diffs words); nested operations join the outer one.
    /// </summary>
    public sealed class FlareSimDocument : IConformanceDocument
    {
        public const string MadCapNs = BlockSelector.MadCapNs;
        public const string DefaultPath = @"C:\FlareSim\Content\Topic.htm";

        private readonly DomJournal _journal;
        private readonly FakeClock _clock;
        private readonly List<Operation> _undo = new List<Operation>();
        private readonly List<Operation> _redo = new List<Operation>();
        private readonly List<string> _untracked = new List<string>();
        private readonly List<string> _log = new List<string>();
        private Operation _current;
        private int _internal;

        public FlareSimDocument(XmlDocument document, string sourcePath, FakeClock clock = null)
        {
            if (document == null) throw new ArgumentNullException("document");
            Document = document;
            SourcePath = sourcePath;
            _clock = clock ?? new FakeClock();
            Variables = new Dictionary<string, string>(StringComparer.Ordinal);
            WidenSpans = true;
            UserName = "FlareSim";
            Initials = "FS";
            SelectionNodes = new List<XmlNode>();
            SelectionText = "";
            _journal = new DomJournal(document);
            _journal.Observed = OnDomChange;
        }

        /// <summary>Loads a topic or snippet from disk (whitespace preserved, as Flare keeps it).</summary>
        public static FlareSimDocument FromFile(string path, FakeClock clock = null)
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(path);
            return new FlareSimDocument(doc, Path.GetFullPath(path), clock);
        }

        /// <summary>A topic from markup; <paramref name="path"/> only names it (nothing is read).</summary>
        public static FlareSimDocument FromXml(string xml, string path = DefaultPath, FakeClock clock = null)
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml(xml);
            return new FlareSimDocument(doc, path, clock);
        }

        // ---- IDocumentAdapter -----------------------------------------------------------------

        public string SourcePath { get; private set; }

        /// <summary>As the adapter: a .htm/.html/.flsnp file with a body.</summary>
        public bool IsEditableTopic
        {
            get
            {
                var ext = Path.GetExtension(SourcePath ?? "").ToLowerInvariant();
                if (ext != ".htm" && ext != ".html" && ext != ".flsnp") return false;
                return BlockSelector.FindBody(Document) != null;
            }
        }

        public XmlDocument Document { get; private set; }
        public string SelectionText { get; private set; }
        public IList<XmlNode> SelectionNodes { get; private set; }

        /// <summary>Project variables ("Set.Name" → value) for <see cref="VariableValue"/>.</summary>
        public Dictionary<string, string> Variables { get; private set; }

        public string VariableValue(string qualifiedName)
        {
            string v;
            return qualifiedName != null && Variables.TryGetValue(qualifiedName, out v) ? v : null;
        }

        public void Edit(string operationName, Action body)
        {
            if (_current != null) { body(); return; }   // joins the outer operation
            Begin(operationName);
            try { body(); }
            finally { End(); }
        }

        public bool ReplaceSpan(XmlText run, int start, int length, string replacement)
        {
            if (_current == null)
                throw new FlareSimException("ReplaceSpan outside Edit: every word-level change must be part of one operation (one Ctrl+Z).");
            if (run == null || !Usable(run, start, length)) return false;
            replacement = replacement ?? "";
            var text = run.Value;
            if (WidenSpans)
            {
                // What FlareDocumentAdapter does: Select(string) can't target an offset, so widen until unique and re-type the extra.
                int s, l;
                TextRuns.UniqueSpan(text, start, length, out s, out l);
                replacement = text.Substring(s, start - s) + replacement + text.Substring(start + length, s + l - start - length);
                start = s; length = l;
            }
            if (!SelectText(run, start, length)) return false;
            ReplaceSelection(replacement);
            return true;
        }

        public bool SelectText(XmlText run, int start, int length)
        {
            if (run == null || !Usable(run, start, length)) return false;
            if (WidenSpans)
            {
                int s, l;
                TextRuns.UniqueSpan(run.Value, start, length, out s, out l);
                start = s; length = l;
            }
            var target = run.Value.Substring(start, length);
            if (target.Length == 0) return false;
            var at = run.Value.IndexOf(target, StringComparison.Ordinal);   // Select(string): the first match wins
            SelectedRun = run;
            SelectionStart = at;
            SelectionLength = length;
            SelectionText = target;
            SelectionNodes = new List<XmlNode> { run };
            return true;
        }

        public void ReplaceBlocks(string operationName, IList<XmlNode> oldNodes, IList<XmlNode> newNodes)
        {
            if (oldNodes == null || oldNodes.Count == 0) throw new InvalidOperationException("Nothing to replace.");
            newNodes = newNodes ?? new List<XmlNode>();
            foreach (var n in newNodes)
                if (n.OwnerDocument != Document) throw new ArgumentException("The node to be inserted is from a different document context.");
            var joined = _current != null;
            if (!joined) Begin(operationName);
            try
            {
                var anchor = oldNodes[0];
                var parent = anchor.ParentNode;
                if (newNodes.Count > 0)
                {
                    ReplaceDocumentNode(anchor, newNodes[0]);
                    var after = anchor;
                    for (var i = 1; i < newNodes.Count; i++)
                    {
                        InsertDocumentNode(parent, newNodes[i], after);
                        after = newNodes[i];
                    }
                }
                else RemoveDocumentNode(anchor);
                for (var i = 1; i < oldNodes.Count; i++) RemoveDocumentNode(oldNodes[i]);
            }
            finally { if (!joined) End(); }
        }

        // ---- editor controls (IConformanceDocument) -------------------------------------------

        /// <summary>Widen spans until unique before selecting, as FlareDocumentAdapter does. False = raw Select(string).</summary>
        public bool WidenSpans { get; set; }

        /// <summary>Change author written into <c>MadCap:changeData</c>.</summary>
        public string UserName { get; set; }
        public string Initials { get; set; }

        /// <summary>The run, offset and length of the current selection (null run when nothing selected).</summary>
        public XmlText SelectedRun { get; private set; }
        public int SelectionStart { get; private set; }
        public int SelectionLength { get; private set; }

        public bool InOperation { get { return _current != null; } }
        public int UndoCount { get { return _undo.Count; } }
        public int RedoCount { get { return _redo.Count; } }

        /// <summary>Names of the operations on the undo stack, oldest first.</summary>
        public IList<string> UndoNames { get { return _undo.ConvertAll(o => o.Name); } }

        /// <summary>Every operation completed, undone and redone, in order ("Undo: …", "Redo: …").</summary>
        public IList<string> OperationLog { get { return _log.AsReadOnly(); } }

        /// <summary>DOM writes that did not go through the adapter (Core must never write the live DOM directly).</summary>
        public IList<string> UntrackedWrites { get { return _untracked.AsReadOnly(); } }

        /// <summary>Opened by the plugin through the host (bulk edits save and close these), not by the author.</summary>
        public bool OpenedByPlugin { get; set; }
        public bool IsDirty { get; private set; }
        public int SaveCount { get; private set; }
        public bool IsClosed { get; private set; }

        /// <summary>Raised by <see cref="Close"/> (the workspace drops the document from its open list).</summary>
        public event EventHandler Closed;

        /// <summary>The topic as it would be saved.</summary>
        public string Xml { get { return Document.OuterXml; } }

        /// <summary>Selects nodes by XPath (prefix <c>MadCap</c> is bound), as if the author had selected them.</summary>
        public void SelectXPath(string xpath)
        {
            var ns = new XmlNamespaceManager(Document.NameTable);
            ns.AddNamespace("MadCap", MadCapNs);
            var nodes = new List<XmlNode>();
            foreach (XmlNode n in Document.SelectNodes(xpath, ns)) nodes.Add(n);
            SelectionNodes = nodes;
            SelectionText = nodes.Count > 0 ? nodes[0].InnerText : "";
            SelectedRun = null;
        }

        public void Undo()
        {
            if (_current != null) throw new FlareSimException("Undo during an operation.");
            if (_undo.Count == 0) return;
            var op = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _journal.Undo(op.Changes);
            _redo.Add(op);
            _log.Add("Undo: " + op.Name);
            IsDirty = true;
        }

        public void Redo()
        {
            if (_current != null) throw new FlareSimException("Redo during an operation.");
            if (_redo.Count == 0) return;
            var op = _redo[_redo.Count - 1];
            if (op.NodeOperationWithVariable) throw new FlareSimRedoCrashException(op.Name);
            _redo.RemoveAt(_redo.Count - 1);
            _journal.Redo(op.Changes);
            _undo.Add(op);
            _log.Add("Redo: " + op.Name);
            IsDirty = true;
        }

        /// <summary>Review &gt; Accept All Changes: one undo step; leaves no change markup and no changeData.</summary>
        public void AcceptAll() { Resolve("Accept All Changes", true); }

        /// <summary>Review &gt; Reject All Changes: one undo step; restores the text as it was before the tracked changes.</summary>
        public void RejectAll() { Resolve("Reject All Changes", false); }

        /// <summary>Saves to <see cref="SourcePath"/> (or <paramref name="path"/>). Fails if editor markers leaked into the DOM.</summary>
        public void Save(string path = null)
        {
            if (_current != null) throw new FlareSimException("Save during an operation.");
            foreach (var name in new[] { "SelectionMarker", "SelectionStartMarker", "SelectionEndMarker" })
                if (Document.GetElementsByTagName(name).Count > 0)
                    throw new FlareSimException("<" + name + "/> would be saved into the topic: remove editor markers after each edit.");
            var target = path ?? SourcePath;
            if (string.IsNullOrEmpty(target)) throw new InvalidOperationException("No path to save to.");
            Document.Save(target);
            IsDirty = false;
            SaveCount++;
        }

        public void Close()
        {
            if (IsClosed) return;
            IsClosed = true;
            _journal.Dispose();
            var h = Closed;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ---- operations ------------------------------------------------------------------------

        private sealed class Operation
        {
            public string Name;
            public readonly List<DomChange> Changes = new List<DomChange>();
            public string RemoveId, AddId;
            public bool NodeOperationWithVariable;
        }

        private void Begin(string name)
        {
            _current = new Operation { Name = name ?? "" };
            _journal.RecordInto(_current.Changes);
        }

        private void End()
        {
            var op = _current;
            try
            {
                // Flare writes the change elements at once but adds their ids to the head only now.
                if (op.RemoveId != null || op.AddId != null) Internal(() => WriteChangeData(op));
            }
            finally
            {
                _journal.RecordInto(null);
                _current = null;
            }
            if (op.Changes.Count == 0) return;
            _undo.Add(op);
            _redo.Clear();
            _log.Add(op.Name);
            IsDirty = true;
        }

        private void OnDomChange(string what, bool recorded)
        {
            if (_internal > 0) return;
            _untracked.Add(what);
        }

        /// <summary>
        /// The author types or edits in the editor (not the plugin): the DOM changes, nothing is listed in
        /// <see cref="UntrackedWrites"/>, and it is not an undo step of the plugin's operations.
        /// </summary>
        public void AuthorEdits(Action edit)
        {
            if (_current != null) throw new FlareSimException("The author cannot type during a plugin operation.");
            Internal(edit);
            IsDirty = true;
        }

        private void Internal(Action a)
        {
            _internal++;
            try { a(); }
            finally { _internal--; }
        }

        private bool Usable(XmlText run, int start, int length)
        {
            return run.OwnerDocument == Document && DomJournal.IsAttached(run) && start >= 0 && length >= 0 && start + length <= run.Value.Length;
        }

        // ---- word-level tracked change (ISelection.ReplaceText with TrackChange = true) ----------

        private void ReplaceSelection(string replacement)
        {
            var run = SelectedRun;
            var text = run.Value;
            var at = SelectionStart;
            var deleted = text.Substring(at, SelectionLength);
            var before = text.Substring(0, at);
            var after = text.Substring(at + SelectionLength);
            Internal(() =>
            {
                var parent = run.ParentNode;
                var next = run.NextSibling;
                var pieces = new List<XmlNode>();
                if (deleted.Length > 0) pieces.Add(ChangeElement(RemoveId(), deleted));
                if (replacement.Length > 0) pieces.Add(ChangeElement(AddId(), replacement));
                if (after.Length > 0) pieces.Add(Document.CreateTextNode(after));
                foreach (var p in pieces) parent.InsertBefore(p, next);
                if (before.Length > 0) run.Value = before;   // the original node keeps the text before the span
                else parent.RemoveChild(run);                // …and leaves the tree when there is none
            });
            SelectedRun = null;
            SelectionNodes = new List<XmlNode>();
            SelectionText = "";
        }

        private XmlElement ChangeElement(string id, string text)
        {
            var e = Document.CreateElement("MadCap", "change", MadCapNs);
            e.SetAttribute("changes", MadCapNs, id);
            e.AppendChild(Document.CreateTextNode(text));
            return e;
        }

        // ---- node operations (IDocument.ReplaceDocumentNode / InsertDocumentNode / RemoveDocumentNode, tracked) ----

        private void ReplaceDocumentNode(XmlNode target, XmlNode replacement)
        {
            if (!(target is XmlElement))
                throw new FlareSimException("Cannot set the 'InnerXml' for the current node because it is either read-only or cannot have children.");
            if (HasVariable(target) || HasVariable(replacement)) _current.NodeOperationWithVariable = true;
            var element = (XmlElement)target;
            Internal(() =>
            {
                var old = new List<XmlNode>();
                foreach (XmlNode c in element.ChildNodes) old.Add(c);
                foreach (var c in old) MarkRemoved(c);
                CopyAttributes(replacement, element);
                foreach (XmlNode c in replacement.ChildNodes) element.AppendChild(MarkAdded(c.CloneNode(true)));
            });
        }

        private void InsertDocumentNode(XmlNode parent, XmlNode node, XmlNode after)
        {
            if (HasVariable(node)) _current.NodeOperationWithVariable = true;
            Internal(() =>
            {
                if (node.ParentNode != null) node.ParentNode.RemoveChild(node);
                parent.InsertAfter(MarkAdded(node), after);
            });
        }

        private void RemoveDocumentNode(XmlNode node)
        {
            Internal(() => MarkRemoved(node));
        }

        /// <summary>Elements carry the remove id; text is wrapped in a change element. Whitespace-only text is left alone.</summary>
        private void MarkRemoved(XmlNode n)
        {
            if (n.NodeType == XmlNodeType.Element) { AddChangeId((XmlElement)n, RemoveId()); return; }
            if (n.NodeType != XmlNodeType.Text || n.Value.Trim().Length == 0) return;
            var wrap = Document.CreateElement("MadCap", "change", MadCapNs);
            wrap.SetAttribute("changes", MadCapNs, RemoveId());
            n.ParentNode.ReplaceChild(wrap, n);
            wrap.AppendChild(n);
        }

        /// <summary>Marks a detached node as a pending insertion; returns what to insert.</summary>
        private XmlNode MarkAdded(XmlNode n)
        {
            if (n.NodeType == XmlNodeType.Element) { AddChangeId((XmlElement)n, AddId()); return n; }
            if (n.NodeType != XmlNodeType.Text || n.Value.Trim().Length == 0) return n;
            return ChangeElement(AddId(), n.Value);
        }

        private static void AddChangeId(XmlElement e, string id)
        {
            var existing = e.GetAttribute("changes", MadCapNs);
            e.SetAttribute("changes", MadCapNs, existing.Length == 0 ? id : existing + " " + id);
        }

        private static void CopyAttributes(XmlNode from, XmlElement to)
        {
            var keep = new List<XmlAttribute>();
            foreach (XmlAttribute a in to.Attributes)
                if (!IsChangesAttr(a) && (from.Attributes == null || from.Attributes[a.LocalName, a.NamespaceURI] == null)) keep.Add(a);
            foreach (var a in keep) to.Attributes.Remove(a);
            if (from.Attributes == null) return;
            foreach (XmlAttribute a in from.Attributes)
                if (!IsChangesAttr(a)) to.SetAttribute(a.LocalName, a.NamespaceURI, a.Value);
        }

        private static bool IsChangesAttr(XmlAttribute a) { return a.LocalName == "changes" && a.NamespaceURI == MadCapNs; }

        private static bool HasVariable(XmlNode n)
        {
            if (n == null || n.NodeType != XmlNodeType.Element) return false;
            if (n.LocalName == "variable" && n.NamespaceURI == MadCapNs) return true;
            var ns = new XmlNamespaceManager(n.OwnerDocument.NameTable);
            ns.AddNamespace("MadCap", MadCapNs);
            return n.SelectSingleNode(".//MadCap:variable", ns) != null;
        }

        // ---- change ids and head changeData -----------------------------------------------------

        private string RemoveId() { AllocatePair(); return _current.RemoveId; }
        private string AddId() { AllocatePair(); return _current.AddId; }

        /// <summary>One Remove/Add pair per operation, after every id already in the document.</summary>
        private void AllocatePair()
        {
            if (_current.RemoveId != null) return;
            var max = 0;
            foreach (XmlNode a in Document.SelectNodes("//@*[local-name()='id' or local-name()='changes']"))
            {
                if (a.NamespaceURI != MadCapNs) continue;
                foreach (var part in a.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int v;
                    if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > max) max = v;
                }
            }
            _current.RemoveId = (max + 1).ToString(CultureInfo.InvariantCulture);
            _current.AddId = (max + 2).ToString(CultureInfo.InvariantCulture);
        }

        private void WriteChangeData(Operation op)
        {
            var data = ChangeData(true);
            var stamp = _clock.Tick();
            data.AppendChild(ChangeEntry("RemoveChange", op.RemoveId, stamp));
            data.AppendChild(ChangeEntry("AddChange", op.AddId, stamp));
        }

        private XmlElement ChangeEntry(string kind, string id, DateTime stamp)
        {
            var e = Document.CreateElement("MadCap", kind, MadCapNs);
            e.SetAttribute("userName", MadCapNs, UserName);
            e.SetAttribute("initials", MadCapNs, Initials);
            e.SetAttribute("id", MadCapNs, id);
            e.SetAttribute("timestamp", MadCapNs, stamp.ToString("o", CultureInfo.InvariantCulture));
            return e;
        }

        private XmlElement ChangeData(bool create)
        {
            var ns = new XmlNamespaceManager(Document.NameTable);
            ns.AddNamespace("MadCap", MadCapNs);
            var existing = Document.SelectSingleNode("//MadCap:changeData", ns) as XmlElement;
            if (existing != null || !create) return existing;
            var html = Document.DocumentElement;
            XmlElement head = null;
            foreach (XmlNode c in html.ChildNodes)
                if (c.NodeType == XmlNodeType.Element && c.LocalName == "head") { head = (XmlElement)c; break; }
            if (head == null)
            {
                head = Document.CreateElement("head", html.NamespaceURI);
                html.InsertBefore(head, html.FirstChild);
            }
            var data = Document.CreateElement("MadCap", "changeData", MadCapNs);
            head.InsertBefore(data, head.FirstChild);
            return data;
        }

        // ---- accept / reject ---------------------------------------------------------------------

        private void Resolve(string name, bool accept)
        {
            if (_current != null) throw new FlareSimException(name + " during an operation.");
            var removed = new HashSet<string>(StringComparer.Ordinal);
            var added = new HashSet<string>(StringComparer.Ordinal);
            var data = ChangeData(false);
            if (data != null)
                foreach (XmlNode e in data.ChildNodes)
                {
                    if (e.NodeType != XmlNodeType.Element) continue;
                    var id = ((XmlElement)e).GetAttribute("id", MadCapNs);
                    if (e.LocalName == "RemoveChange") removed.Add(id);
                    else if (e.LocalName == "AddChange") added.Add(id);
                }
            var drop = accept ? removed : added;
            var keep = accept ? added : removed;
            Begin(name);
            try
            {
                Internal(() =>
                {
                    var ns = new XmlNamespaceManager(Document.NameTable);
                    ns.AddNamespace("MadCap", MadCapNs);
                    var marked = new List<XmlElement>();
                    foreach (XmlElement e in Document.SelectNodes("//*[@MadCap:changes]", ns)) marked.Add(e);
                    marked.Reverse();   // innermost first
                    foreach (var e in marked)
                    {
                        if (!DomJournal.IsAttached(e)) continue;
                        var ids = e.GetAttribute("changes", MadCapNs).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        var isDropped = Array.Exists(ids, drop.Contains);
                        var isKept = Array.Exists(ids, keep.Contains);
                        if (!isDropped && !isKept) continue;   // unknown id: leave it, as Flare does with orphans
                        if (isDropped) { e.ParentNode.RemoveChild(e); continue; }
                        if (e.LocalName == "change" && e.NamespaceURI == MadCapNs)
                        {
                            var parent = e.ParentNode;
                            while (e.FirstChild != null) parent.InsertBefore(e.FirstChild, e);
                            parent.RemoveChild(e);
                        }
                        else e.RemoveAttribute("changes", MadCapNs);
                    }
                    if (data != null && data.ParentNode != null) data.ParentNode.RemoveChild(data);
                });
            }
            finally { End(); }
        }

        /// <summary>Plain text of the body (for quick assertions).</summary>
        public string BodyText
        {
            get
            {
                var body = BlockSelector.FindBody(Document);
                return body == null ? "" : body.InnerText;
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder("FlareSimDocument ").Append(SourcePath ?? "(unnamed)");
            sb.Append(" undo=").Append(UndoCount).Append(" redo=").Append(RedoCount);
            return sb.ToString();
        }
    }
}
