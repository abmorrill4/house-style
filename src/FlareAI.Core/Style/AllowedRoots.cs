using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlareAI.Core.Style
{
    /// <summary>
    /// An optional allow-list of folders for a path an outside caller supplies (the MCP server's <c>project</c> argument).
    /// With no roots set everything is allowed, as before.
    /// </summary>
    public static class AllowedRoots
    {
        /// <summary>True when <paramref name="path"/> is empty, no roots are set, or the full path lies inside one of the roots.</summary>
        public static bool IsAllowed(string path, IEnumerable<string> roots)
        {
            var list = (roots ?? Enumerable.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            if (list.Count == 0 || string.IsNullOrWhiteSpace(path)) return true;
            string full;
            try { full = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\"; }
            catch (Exception) { return false; }
            foreach (var r in list)
            {
                string root;
                try { root = Path.GetFullPath(r).TrimEnd('\\', '/') + "\\"; }
                catch (Exception) { continue; }
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
