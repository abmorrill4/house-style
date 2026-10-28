using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// Files and folders a Flare project leaves out of Lint Project and the report before a build. One entry per line,
    /// relative to the project's Content folder; a folder ends with a backslash; lines starting with # are comments.
    /// The list lives in the project (Project\HouseStyle\LintExclusions.txt), so everyone on the project shares it.
    /// Lint Topic still lints an excluded topic when the writer asks for it.
    /// </summary>
    public static class LintExclusions
    {
        /// <summary>The list's file for a project root (the folder with the .flprj).</summary>
        public static string FileFor(string projectRoot) { return Path.Combine(projectRoot, @"Project\HouseStyle\LintExclusions.txt"); }

        /// <summary>The entries for the project that holds <paramref name="anyPath"/>; empty when there are none.</summary>
        public static List<string> Load(string anyPath)
        {
            var root = StyleEngine.FlareProjectRoot(anyPath);
            var file = root == null ? null : FileFor(root);
            if (file == null || !File.Exists(file)) return new List<string>();
            return File.ReadAllLines(file).Select(l => l.Trim().Replace('/', '\\')).Where(l => l.Length > 0 && !l.StartsWith("#")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The entry for a file, or for its folder when <paramref name="folder"/> is true; null outside a project's Content.</summary>
        public static string EntryFor(string path, bool folder)
        {
            var content = ContentOf(path);
            if (content == null) return null;
            var full = Path.GetFullPath(folder ? Path.GetDirectoryName(path) : path);
            if (!full.StartsWith(content, StringComparison.OrdinalIgnoreCase)) return null;
            var rel = full.Substring(content.Length);
            return folder ? (rel.Length == 0 ? null : rel + "\\") : rel;
        }

        /// <summary>True when an entry covers the file (the file itself, or a folder above it).</summary>
        public static bool IsExcluded(string path, IList<string> entries)
        {
            if (entries == null || entries.Count == 0) return false;
            var rel = EntryFor(path, false);
            return rel != null && entries.Any(e => e.EndsWith("\\") ? rel.StartsWith(e, StringComparison.OrdinalIgnoreCase) : string.Equals(rel, e, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Adds an entry to the list of the project that holds <paramref name="anyPath"/>.</summary>
        public static void Add(string anyPath, string entry) { Save(anyPath, Load(anyPath).Concat(new[] { entry })); }

        /// <summary>Removes an entry; the files it covered are linted again.</summary>
        public static void Remove(string anyPath, string entry) { Save(anyPath, Load(anyPath).Where(e => !string.Equals(e, entry, StringComparison.OrdinalIgnoreCase))); }

        private static void Save(string anyPath, IEnumerable<string> entries)
        {
            var root = StyleEngine.FlareProjectRoot(anyPath);
            if (root == null) throw new InvalidOperationException("Not in a Flare project: " + anyPath);
            var file = FileFor(root);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var lines = new List<string> { "# Left out of Lint Project. One path per line, relative to the Content folder; a folder ends with \\." };
            lines.AddRange(entries.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e, StringComparer.OrdinalIgnoreCase));
            File.WriteAllLines(file, lines.ToArray());
        }

        internal static string ContentOf(string path)
        {
            var root = StyleEngine.FlareProjectRoot(path);
            return root == null ? null : Path.Combine(root, "Content") + "\\";
        }
    }
}
