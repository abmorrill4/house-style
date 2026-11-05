using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// Lint Folders: the folders of a project's Content a writer can pick, and the project files inside the picked ones.
    /// Folders are relative to Content and end with a backslash, the same form as <see cref="LintExclusions"/>. A folder
    /// covers its subfolders. The project's exclusions still apply, because the files come from <see cref="ActionRunner.ProjectFiles"/>.
    /// </summary>
    public static class FolderScope
    {
        /// <summary>The folders under Content that hold at least one file Lint Project would lint, sorted, including nested ones.</summary>
        public static List<string> Folders(IEnumerable<string> projectFiles, string content)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in projectFiles)
            {
                var rel = Relative(f, content);
                if (rel == null) continue;
                for (var i = rel.IndexOf('\\'); i >= 0; i = rel.IndexOf('\\', i + 1)) folders.Add(rel.Substring(0, i + 1));
            }
            var list = folders.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>The files inside any of the folders, subfolders included. No folders means no files.</summary>
        public static List<string> Filter(IEnumerable<string> projectFiles, string content, IEnumerable<string> folders)
        {
            var picked = folders.Select(f => f.Replace('/', '\\')).Select(f => f.EndsWith("\\") ? f : f + "\\").ToList();
            return projectFiles.Where(f => { var rel = Relative(f, content); return rel != null && picked.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase)); }).ToList();
        }

        /// <summary>What the report calls the part of the project it covers: the folder, or the first two and a count.</summary>
        public static string Describe(IList<string> folders)
        {
            // A folder inside another picked folder adds nothing to the name.
            var names = folders.Where(f => !folders.Any(o => o != f && f.StartsWith(o, StringComparison.OrdinalIgnoreCase))).Select(f => f.TrimEnd('\\')).ToList();
            if (names.Count <= 2) return string.Join(", ", names.ToArray());
            return names[0] + ", " + names[1] + " and " + (names.Count - 2) + " more";
        }

        private static string Relative(string file, string content)
        {
            var root = content.EndsWith("\\") ? content : content + "\\";
            var full = Path.GetFullPath(file);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : null;
        }
    }
}
