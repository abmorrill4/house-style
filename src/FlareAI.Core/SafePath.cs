using System;
using System.IO;
using System.Text.RegularExpressions;

namespace FlareAI.Core
{
    /// <summary>
    /// The one check for an id or a single path segment that came from outside (a shared folder, a file, a message)
    /// before it becomes part of a file name. It never touches the disk.
    /// </summary>
    public static class SafePath
    {
        private static readonly Regex IdPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// An id that is safe as a file name: letters, digits, dots, dashes and underscores, starting with a letter or
        /// digit, at most 100 characters, with no "..". No separator, drive or UNC prefix can pass.
        /// </summary>
        public static bool IsValidId(string id) { return id != null && IdPattern.IsMatch(id) && !id.Contains(".."); }

        /// <summary>
        /// One path segment (a file or folder name): not empty, at most 255 characters, no separator, no drive colon,
        /// no control or other character Windows refuses in a name, no "..", and no trailing dot or space.
        /// </summary>
        public static bool IsSafeSegment(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 255) return false;
            if (name == "." || name.Contains("..")) return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;   // \ / : * ? " < > | and control characters
            var last = name[name.Length - 1];
            return last != '.' && last != ' ';
        }

        /// <summary>A UNC path (<c>\\host\share</c>): looking at it makes Windows sign in to that host.</summary>
        public static bool IsNetworkPath(string path)
        {
            return !string.IsNullOrEmpty(path) && (path.StartsWith(@"\\") || path.StartsWith("//") || path.StartsWith(@"\/") || path.StartsWith(@"/\"));
        }
    }
}
