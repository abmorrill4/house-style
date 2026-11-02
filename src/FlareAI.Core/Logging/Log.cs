using System;
using System.Diagnostics;

namespace FlareAI.Core.Logging
{
    /// <summary>Where Core code finds the log. Logging never throws into the caller.</summary>
    public static class Log
    {
        private static volatile ILog _current = NullLog.Instance;
        private static volatile string _commandId = "";

        public static ILog Current
        {
            get { return _current; }
            set { _current = value ?? NullLog.Instance; }
        }

        /// <summary>The id shared by every line of the running command; "" when none.</summary>
        public static string CommandId
        {
            get { return _commandId; }
        }

        /// <summary>Starts a command: logs its start, and its end with the duration and result when the scope is disposed.</summary>
        public static IDisposable Command(string name)
        {
            var scope = new CommandScope(name, _commandId);
            _commandId = Guid.NewGuid().ToString("N").Substring(0, 8);
            try { Current.Info("command_start", "command", name); } catch (Exception) { }
            return scope;
        }

        /// <summary>Logs a caught error: its type and stack, never its message (it can quote topic text).</summary>
        public static void Caught(string component, Exception ex)
        {
            try
            {
                if (ex == null) return;
                var name = System.Text.RegularExpressions.Regex.Replace(component ?? "", @"[^A-Za-z0-9_.:+\-]+", "_");
                if (name.Length > 80) name = name.Substring(0, 80);
                Current.Error("error", "component", name,"error_type", ex.GetType().Name, "stack", ex.StackTrace);
            }
            catch (Exception) { }
        }

        internal static void Restore(string id) { _commandId = id ?? ""; }

        internal static void Clear() { _commandId = ""; }
    }

    public sealed class CommandScope : IDisposable
    {
        private readonly string _name;
        private readonly string _previous;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private bool _done;

        public string Result = "ok";

        internal CommandScope(string name, string previous)
        {
            _name = name;
            _previous = previous;
        }

        public void Fail(Exception ex)
        {
            Result = "error";
            Log.Caught(_name, ex);
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _watch.Stop();
            try { Log.Current.Info("command_end", "command", _name, "ms", _watch.ElapsedMilliseconds, "result", Result); }
            catch (Exception) { }
            Log.Restore(_previous);
        }
    }
}
