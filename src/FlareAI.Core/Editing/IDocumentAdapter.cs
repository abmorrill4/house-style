using System.Collections.Generic;
using System.Xml;

namespace FlareAI.Core.Editing
{
    /// <summary>
    /// Everything Core needs from the host editor. The Flare implementation wraps IDocument/ISelection;
    /// tests use FakeDocumentAdapter over a fixture file. No Flare types leak through this interface.
    /// </summary>
    public interface IDocumentAdapter
    {
        /// <summary>Absolute path of the open topic, or null.</summary>
        string SourcePath { get; }

        /// <summary>True when the active file is a Flare topic/snippet (XHTML with a body).</summary>
        bool IsEditableTopic { get; }

        /// <summary>The topic DOM. Nodes returned by SelectionNodes belong to this document.</summary>
        XmlDocument Document { get; }

        /// <summary>Plain text of the current selection ("" when caret only).</summary>
        string SelectionText { get; }

        /// <summary>Nodes the selection spans (may be text nodes), in document order; empty when caret only.</summary>
        IList<XmlNode> SelectionNodes { get; }

        /// <summary>
        /// Replace <paramref name="oldNodes"/> (siblings, document order) with <paramref name="newNodes"/> as one
        /// undoable, track-changed operation. newNodes must already be imported into <see cref="Document"/>.
        /// </summary>
        void ReplaceBlocks(string operationName, IList<XmlNode> oldNodes, IList<XmlNode> newNodes);

        /// <summary>
        /// Insert <paramref name="newNodes"/> (already imported into <see cref="Document"/>) as new children of <paramref name="parent"/>
        /// starting at child index <paramref name="index"/>, as one undoable, track-changed operation. Callers check
        /// <see cref="NodeWrites.InsertRefusal"/> first and the Flare adapter refuses again.
        /// </summary>
        void InsertBlocks(string operationName, XmlNode parent, int index, IList<XmlNode> newNodes);

        /// <summary>Select a span of one text node in the editor so the author sees it. False if the editor could not.</summary>
        bool SelectText(XmlText run, int start, int length);

        /// <summary>Runs <paramref name="body"/> as one undoable operation (one Ctrl+Z), then refreshes the view.</summary>
        void Edit(string operationName, System.Action body);

        /// <summary>
        /// Replace Length chars at Start of one text node with Replacement as a word-level tracked change.
        /// Call inside <see cref="Edit"/>. False when the editor could not do it (nothing changed).
        /// </summary>
        bool ReplaceSpan(XmlText run, int start, int length, string replacement);

        /// <summary>Current value of a project variable ("Set.Name"), or null if unknown.</summary>
        string VariableValue(string qualifiedName);
    }
}
