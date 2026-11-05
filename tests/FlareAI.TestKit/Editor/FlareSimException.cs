using System;

namespace FlareAI.TestKit.Editor
{
    /// <summary>
    /// Raised by <see cref="FlareSimDocument"/> where Flare 22 would throw, crash, or corrupt the topic. A test that
    /// sees one has taken a path the plugin must never take.
    /// </summary>
    public class FlareSimException : InvalidOperationException
    {
        public FlareSimException(string message) : base(message) { }
    }

    /// <summary>
    /// Flare 22 crashes on Redo of a tracked node operation whose content holds a <c>MadCap:variable</c>
    /// ("specified node cannot be inserted… wrong type"). See docs/flare-api-notes.md, "Variables and redo".
    /// </summary>
    public sealed class FlareSimRedoCrashException : FlareSimException
    {
        public FlareSimRedoCrashException(string operation)
            : base("Flare crashes here: Redo of node operation '" + operation + "' that contains a MadCap:variable " +
                   "(\"The specified node cannot be inserted as the valid child of this node, because the specified node is the wrong type.\")") { }
    }
}
