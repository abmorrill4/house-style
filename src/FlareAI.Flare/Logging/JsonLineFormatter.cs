using System;
using System.IO;
using FlareAI.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using Serilog.Formatting;

namespace FlareAI.Flare.Logging
{
    /// <summary>
    /// One JSON object per line: time (UTC), level, command id, component, event, duration, the three versions, then the
    /// event's fields. The fields were already through <see cref="LogFields"/> when the event was made; an exception is
    /// written as its type and stack only (never its message, which can quote text).
    /// </summary>
    internal sealed class JsonLineFormatter : ITextFormatter
    {
        public const string EventProperty = "evt";

        public void Format(LogEvent logEvent, TextWriter output)
        {
            var o = new JObject();
            o["t"] = logEvent.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            o["level"] = Level(logEvent.Level);
            o["cmd"] = LogContext.CurrentCommandId;

            var evt = Scalar(logEvent, EventProperty) as string ?? "";
            var dot = evt.IndexOf('.');
            o["component"] = dot > 0 ? evt.Substring(0, dot) : evt;
            o["evt"] = evt;

            object ms;
            if (Has(logEvent, "ms", out ms)) o["ms"] = JToken.FromObject(ms);

            o["plugin"] = LogContext.PluginVersion;
            o["flare"] = LogContext.FlareVersion;
            o["rules"] = LogContext.RuleSetVersion;

            foreach (var p in logEvent.Properties)
            {
                if (p.Key == EventProperty || p.Key == "ms") continue;
                var s = p.Value as ScalarValue;
                if (s == null || s.Value == null) continue;
                object clean;
                if (!LogFields.Sanitize(p.Key, s.Value, out clean)) continue;
                o[p.Key] = JToken.FromObject(clean);
            }

            if (logEvent.Exception != null)
            {
                o["error_type"] = LogFields.Text("error_type", logEvent.Exception.GetType().FullName ?? logEvent.Exception.GetType().Name);
                o["stack"] = LogFields.Text("stack", logEvent.Exception.StackTrace ?? "");
            }

            output.Write(o.ToString(Formatting.None));
            output.Write('\n');
        }

        private static string Level(LogEventLevel l)
        {
            switch (l)
            {
                case LogEventLevel.Verbose:
                case LogEventLevel.Debug: return "debug";
                case LogEventLevel.Information: return "info";
                case LogEventLevel.Warning: return "warn";
                default: return "error";
            }
        }

        private static object Scalar(LogEvent e, string name)
        {
            LogEventPropertyValue v;
            var s = e.Properties.TryGetValue(name, out v) ? v as ScalarValue : null;
            return s == null ? null : s.Value;
        }

        private static bool Has(LogEvent e, string name, out object value)
        {
            value = Scalar(e, name);
            return value != null && (value is int || value is long || value is double);
        }
    }
}
