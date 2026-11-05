using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FlareAI.Core.Settings;

namespace FlareAI.Core
{
    /// <summary>
    /// Two append-only logs in %APPDATA%\FlareAI. diag.log (<see cref="Paths.DiagLog"/>) holds events, ids, counts and
    /// exception stacks, never document text: its end can go out with a problem report (issue #124).
    /// diag-content.log (<see cref="Paths.DiagContentLog"/>) holds what does quote a topic (the Diagnostics command's
    /// selection and block XML, AI contract breaches, exception messages that quote text) and never leaves the computer.
    /// Never log secrets or full prompts in either.
    /// </summary>
    public static class Diagnostics
    {
        private static readonly object Gate = new object();
        public static bool Enabled = true;

        public static string LogPath { get { return Paths.DiagLog; } }

        /// <summary>The log for document text; nothing reads it to send.</summary>
        public static string ContentLogPath { get { return Paths.DiagContentLog; } }

        /// <summary>When set, receives every line instead of the log files (the stress harness counts them).</summary>
        public static Action<string> Sink;

        /// <summary>An event for diag.log. Never pass document text here: use <see cref="LogContent"/>.</summary>
        public static void Log(string message) { Write(LogPath, message); }

        /// <summary>
        /// An exception for diag.log: its type, its message with any quoted text taken out, and its stack. When the message
        /// quoted something (the words of a topic or of an AI answer), the full message goes to diag-content.log.
        /// </summary>
        public static void Log(string context, Exception ex)
        {
            var message = ex.Message ?? "";
            var safe = WithoutQuotedText(message);
            Log(context + " :: " + ex.GetType().Name + ": " + safe + Environment.NewLine + ex.StackTrace);
            if (safe != message) LogContent(context + " :: " + ex.GetType().Name + ": " + message);
        }

        /// <summary>Text that can hold a topic's words: diag-content.log only, which is never sent.</summary>
        public static void LogContent(string message) { Write(ContentLogPath, message); }

        private static readonly Regex Quoted = new Regex("\"[^\"\\r\\n]*\"|“[^”\\r\\n]*”", RegexOptions.CultureInvariant);

        /// <summary>The text with whatever is in quotation marks replaced by "…" (messages quote the words they are about).</summary>
        public static string WithoutQuotedText(string s)
        {
            return string.IsNullOrEmpty(s) ? s : Quoted.Replace(s, m => m.Value[0] == '"' ? "\"…\"" : "“…”");
        }

        /// <summary>
        /// A path as it may appear in a log or report: a drive or network path (which holds a user name, a drive or a host)
        /// becomes <c>[project]\</c> and its last part, the same form a report uses. A relative path stays as it is.
        /// </summary>
        public static string Path(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var rooted = path[0] == '\\' || path[0] == '/' || (path.Length >= 2 && path[1] == ':');
            if (!rooted) return path;
            var last = path.Substring(path.LastIndexOfAny(new[] { '\\', '/' }) + 1);
            return last.Length == 0 ? "[project]" : "[project]\\" + last;
        }

        private static void Write(string path, string message)
        {
            var sink = Sink;
            if (sink != null) { sink(message); return; }
            if (!Enabled) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.Root);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception) { /* diagnostics must never throw */ }
        }
    }
}
