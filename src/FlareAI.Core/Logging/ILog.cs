using System;
using System.Collections.Generic;

namespace FlareAI.Core.Logging
{
    /// <summary>
    /// The plugin's local log. Core code calls this and nothing else; the Flare layer backs it with a file. An event has
    /// a name and named values: only the typed fields on the <see cref="LogFields"/> list are ever kept. A call never
    /// throws and never changes what the plugin does.
    /// </summary>
    public interface ILog
    {
        void Debug(string evt, params KeyValuePair<string, object>[] fields);
        void Info(string evt, params KeyValuePair<string, object>[] fields);
        void Warn(string evt, params KeyValuePair<string, object>[] fields);
        void Error(string evt, Exception ex, params KeyValuePair<string, object>[] fields);
    }

    /// <summary>The default log: does nothing.</summary>
    public sealed class NullLog : ILog
    {
        public void Debug(string evt, params KeyValuePair<string, object>[] fields) { }
        public void Info(string evt, params KeyValuePair<string, object>[] fields) { }
        public void Warn(string evt, params KeyValuePair<string, object>[] fields) { }
        public void Error(string evt, Exception ex, params KeyValuePair<string, object>[] fields) { }
    }

    /// <summary>The log in effect.</summary>
    public static class Log
    {
        private static volatile ILog _current = new NullLog();

        /// <summary>The log Core code writes to. Never null.</summary>
        public static ILog Current
        {
            get { return _current; }
            set { _current = value ?? new NullLog(); }
        }
    }

    /// <summary>Short way to write a field at a call site.</summary>
    public static class F
    {
        public static KeyValuePair<string, object> Of(string name, object value)
        {
            return new KeyValuePair<string, object>(name, value);
        }
    }
}
