using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FlareAI.Core.Feedback;

namespace FlareAI.Core.Logging
{
    /// <summary>
    /// The safety filter for the log. Every <see cref="ILog"/> implementation passes each field through
    /// <see cref="Sanitize"/>: only named, typed fields on the list are kept, strings are one line with secrets and user
    /// folder names removed, and a long string (which could be a document sentence) is replaced by a marker.
    /// </summary>
    public static class LogFields
    {
        public const string TooLong = "[too long]";
        public const int MaxText = 300;
        public const int MaxStack = 4000;

        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // counts and times
            "findings", "blocks", "topics", "lines", "snippet_findings", "ai_findings", "skipped_in_variables",
            "in_tokens", "out_tokens", "cache_read_tokens", "ms",
            // ids and names
            "rule_id", "model", "task", "check", "checks", "result", "version", "latest", "setting", "level",
            "error_type", "topic_hash", "key_set", "stack", "path", "command", "guide", "names", "last_start"
        };

        // Refused even if someone adds them to the list.
        private static readonly HashSet<string> Never = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "key", "header", "headers", "token", "text", "prompt", "reply"
        };

        /// <summary>
        /// True and the value to write when the field may be logged; false when it is dropped (a name that is not on the
        /// list, a refused name, or a value that is not a bool, a number or a string).
        /// </summary>
        public static bool Sanitize(string name, object value, out object clean)
        {
            clean = null;
            if (string.IsNullOrEmpty(name) || value == null) return false;
            if (Never.Contains(name) || !Allowed.Contains(name)) return false;
            if (value is bool || value is int || value is long || value is short || value is byte || value is uint)
            {
                clean = value;
                return true;
            }
            if (value is double || value is float)
            {
                var d = Convert.ToDouble(value);
                if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                clean = value;
                return true;
            }
            var s = value as string;
            if (s == null) return false;
            clean = Text(name, s);
            return true;
        }

        /// <summary>A string value as it may be logged: one line, no secrets, no user folder names, never long.</summary>
        public static string Text(string name, string v)
        {
            if (v == null) return "";
            var sb = new StringBuilder(v.Length);
            foreach (var c in v)
            {
                if (c == '\r' || c == '\n' || c == '\t') { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                if (c < 0x20 || c == 0x7f) continue;
                sb.Append(c);
            }
            var r = sb.ToString().Trim();
            r = ReportBuilder.Redact(r, null);
            r = UserFolders(r);
            var max = name == "stack" ? MaxStack : MaxText;
            if (r.Length > max)
            {
                if (name == "stack") return ReportBuilder.Cut(r, max);
                return TooLong;
            }
            return r;
        }

        private static string UserFolders(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            try
            {
                v = ReportBuilder.Anonymize(v, null);
                var user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length > 2)
                    v = Regex.Replace(v, @"(?<=[\\/])" + Regex.Escape(user) + @"(?=[\\/]|$)", "[user]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (Exception) { }
            return v;
        }

        /// <summary>The first 12 hex characters of the SHA-256 of the lowercased full path: tells topics apart without naming them.</summary>
        public static string TopicHash(string path)
        {
            if (string.IsNullOrEmpty(path)) return "none";
            var full = path;
            try { full = System.IO.Path.GetFullPath(path); } catch (Exception) { }
            return HexHash(full.ToLowerInvariant(), 12);
        }

        /// <summary>The first <paramref name="length"/> hex characters (up to 64) of the SHA-256 of the text.</summary>
        public static string HexHash(string text, int length)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString().Substring(0, Math.Min(length, sb.Length));
            }
        }

        /// <summary>"key set: yes" or "key set: no". The key itself is never logged.</summary>
        public static string KeySet(bool set)
        {
            return set ? "key set: yes" : "key set: no";
        }
    }
}
