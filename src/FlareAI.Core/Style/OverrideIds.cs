using System;
using System.Text;

namespace FlareAI.Core.Style
{
    /// <summary>
    /// The one way a rule's name in an override, setting or request becomes the key the checker stores it under: a built-in
    /// check's legacy name becomes its id, a bare name is Google's, anything else is kept as written.
    /// </summary>
    public static class OverrideIds
    {
        private const int LogLength = 80;

        /// <summary>
        /// The key a name is stored under: a built-in check's id (its legacy name, e.g. Structure.AltText, maps to it), a bare
        /// rule name as Google's (Headings becomes Google.Headings), else the name unchanged. Null for null.
        /// </summary>
        public static string Canonical(string key)
        {
            if (key == null) return null;
            var built = StructureCheck.Find(key);
            if (built != null) return built.Id;
            return key.IndexOf('.') < 0 && key.IndexOf(':') < 0 ? "Google." + key : key;
        }

        /// <summary>
        /// True when the name is a rule this composition (or a built-in check) knows: its id, its checker name, a bare rule name
        /// or a guide item's id, any of them under <see cref="Canonical"/>. Everything else is false (AI ids, paths, made-up names).
        /// </summary>
        public static bool IsKnown(Composition comp, string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (StructureCheck.Find(id) != null) return true;
            if (comp == null) return false;
            return comp.KnownNames.Contains(id) || comp.KnownNames.Contains(Canonical(id));
        }

        /// <summary>
        /// A rule id or name for a log line: one line of at most 80 characters, in double quotes. Anything that isn't a letter,
        /// digit or one of _ : . / - (spaces, line breaks, control and bidi characters, backslashes) becomes '?'.
        /// </summary>
        public static string ForLog(string id)
        {
            if (id == null) return "\"\"";
            var sb = new StringBuilder();
            for (var i = 0; i < id.Length && i < LogLength; i++)
            {
                var c = id[i];
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || ":./-".IndexOf(c) >= 0 ? c : '?');
            }
            return "\"" + sb + "\"";
        }
    }
}
