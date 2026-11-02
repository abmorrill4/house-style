using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FlareAI.Core.Logging
{
    public enum FieldKind { Int, Bool, Id, Path, PathHash, Version, Stack }

    /// <summary>
    /// The only fields the log may hold, each with a type. A pair that is not on the list, or whose value does not fit
    /// its type, is refused. Document text, prompts, replies, keys and headers can't get in.
    /// </summary>
    public static class LogFields
    {
        public static readonly Dictionary<string, FieldKind> Allowed = Build();

        private static Dictionary<string, FieldKind> Build()
        {
            var d = new Dictionary<string, FieldKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in new[] { "count", "findings", "errors", "warnings", "suggestions", "blocks", "lines", "ms", "input_tokens", "output_tokens", "cached_tokens" }) d[n] = FieldKind.Int;
            foreach (var n in new[] { "ok", "key_set" }) d[n] = FieldKind.Bool;
            foreach (var n in new[] { "component", "command", "task", "model", "effort", "result", "rule_id", "check", "checks", "setting", "level", "layer", "source", "error_type" }) d[n] = FieldKind.Id;
            d["topic_hash"] = FieldKind.PathHash;
            d["stack"] = FieldKind.Stack;
            foreach (var n in new[] { "plugin_version", "flare_version", "rule_set_version" }) d[n] = FieldKind.Version;
            return d;
        }

        private static readonly Regex IdPattern = new Regex(@"^[A-Za-z0-9_.:+\-]{1,80}$", RegexOptions.Compiled);
        private static readonly Regex VersionPattern = new Regex(@"^[0-9A-Za-z.+\-]{1,40}$", RegexOptions.Compiled);
        private static readonly Regex WinUser = new Regex(@"(?i)([A-Z]:\\Users\\)[^\\]+", RegexOptions.Compiled);
        private static readonly Regex NixUser = new Regex(@"(?i)(/Users/|/home/)[^/]+", RegexOptions.Compiled);
        private const int MaxLength = 4000;

        private static readonly string[] Banned = { "header", "authorization", "prompt", "reply", "text" };

        /// <summary>True and the cleaned value when the pair may be logged; false when it is refused.</summary>
        public static bool TryClean(string name, object value, out object clean)
        {
            clean = null;
            try
            {
                if (name == null || value == null) return false;
                FieldKind kind;
                if (!Allowed.TryGetValue(name, out kind)) return false;
                var lower = name.ToLowerInvariant();
                if (lower.Contains("key") && lower != "key_set") return false;
                foreach (var b in Banned) if (lower.Contains(b)) return false;

                switch (kind)
                {
                    case FieldKind.Int:
                        if (value is int || value is long) { clean = value; return true; }
                        return false;
                    case FieldKind.Bool:
                        if (value is bool) { clean = value; return true; }
                        return false;
                    case FieldKind.Id:
                        {
                            var s = value as string;
                            if (s == null || !IdPattern.IsMatch(s)) return false;
                            if (s.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) || s.IndexOf("Bearer", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                            clean = s;
                            return true;
                        }
                    case FieldKind.Version:
                        {
                            var s = value as string;
                            if (s == null) return false;
                            if (s == "unknown" || VersionPattern.IsMatch(s)) { clean = s; return true; }
                            return false;
                        }
                    case FieldKind.PathHash:
                        {
                            var s = value as string;
                            if (string.IsNullOrEmpty(s)) return false;
                            clean = Hash(s);
                            return true;
                        }
                    case FieldKind.Path:
                    case FieldKind.Stack:
                        {
                            var s = value as string;
                            if (s == null) return false;
                            clean = Scrub(s);
                            return true;
                        }
                }
                return false;
            }
            catch (Exception)
            {
                clean = null;
                return false;
            }
        }

        /// <summary>The value on one line, with CR and LF each replaced by a space.</summary>
        public static string OneLine(string s)
        {
            if (s == null) return "";
            return s.Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>The first 12 hex characters of the SHA-256 of the lower-cased text.</summary>
        public static string Hash(string s)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((s ?? "").ToLowerInvariant()));
                var sb = new StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString(0, 12);
            }
        }

        private static string Scrub(string s)
        {
            s = Regex.Replace(s, @"\r\n|\r|\n", " ");
            s = WinUser.Replace(s, "$1<user>");
            s = NixUser.Replace(s, "$1<user>");
            var user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length >= 3)
                s = Regex.Replace(s, Regex.Escape(user), "<user>", RegexOptions.IgnoreCase);
            return s.Length > MaxLength ? s.Substring(0, MaxLength) : s;
        }
    }
}
