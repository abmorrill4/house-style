using System;
using System.Collections.Generic;
using System.IO;
using FlareAI.Core;
using FlareAI.Core.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using CoreLog = FlareAI.Core.Logging.Log;

namespace FlareAI.Flare.Logging
{
    /// <summary>
    /// The plugin's <see cref="ILog"/>, written with Serilog to rolling JSON Lines files. A call never throws and never
    /// changes what the plugin does: if the folder can't be written, the log is simply off.
    /// </summary>
    internal sealed class SerilogLog : ILog, IDisposable
    {
        private readonly LoggingLevelSwitch _level;
        private readonly Serilog.Core.Logger _logger;
        private volatile bool _disposed;

        public SerilogLog(string folder, bool debug, long maxBytes = 5 * 1024 * 1024, int keep = 10)
        {
            _level = new LoggingLevelSwitch(debug ? LogEventLevel.Debug : LogEventLevel.Information);
            try
            {
                _logger = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(_level)
                    .WriteTo.File(new JsonLineFormatter(), Path.Combine(folder, "housestyle-.jsonl"),
                        fileSizeLimitBytes: maxBytes, rollOnFileSizeLimit: true, retainedFileCountLimit: keep,
                        rollingInterval: RollingInterval.Day, shared: true)
                    .CreateLogger();
            }
            catch (Exception) { _logger = null; }
        }

        public void SetDebug(bool on)
        {
            try { _level.MinimumLevel = on ? LogEventLevel.Debug : LogEventLevel.Information; } catch (Exception) { }
        }

        public void Debug(string evt, params KeyValuePair<string, object>[] fields) { Write(LogEventLevel.Debug, evt, null, fields); }
        public void Info(string evt, params KeyValuePair<string, object>[] fields) { Write(LogEventLevel.Information, evt, null, fields); }
        public void Warn(string evt, params KeyValuePair<string, object>[] fields) { Write(LogEventLevel.Warning, evt, null, fields); }
        public void Error(string evt, Exception ex, params KeyValuePair<string, object>[] fields) { Write(LogEventLevel.Error, evt, ex, fields); }

        private void Write(LogEventLevel level, string evt, Exception ex, KeyValuePair<string, object>[] fields)
        {
            try
            {
                var logger = _logger;
                if (logger == null || _disposed || !logger.IsEnabled(level)) return;
                var props = new List<LogEventProperty>();
                props.Add(new LogEventProperty(JsonLineFormatter.EventProperty, new ScalarValue(LogFields.Text("evt", evt ?? ""))));
                if (fields != null)
                    foreach (var f in fields)
                    {
                        object clean;
                        if (!LogFields.Sanitize(f.Key, f.Value, out clean)) continue;
                        props.Add(new LogEventProperty(f.Key, new ScalarValue(clean)));
                    }
                var template = new MessageTemplate("", new MessageTemplateToken[0]);
                logger.Write(new LogEvent(DateTimeOffset.UtcNow, level, ex, template, props));
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            try
            {
                _disposed = true;
                if (_logger != null) _logger.Dispose();
            }
            catch (Exception) { }
        }

        /// <summary>Makes this the plugin's log. Debug level in a development build; Info in a release unless the setting turns debug on.</summary>
        public static void Install(bool debugSetting)
        {
#if DEBUG
            var debug = true;
#else
            var debug = debugSetting;
#endif
            var old = CoreLog.Current as SerilogLog;
            var log = new SerilogLog(Paths.Logs, debug);
            CoreLog.Current = log;
            if (old != null) old.Dispose();
            try
            {
                LogContext.PluginVersion = Plugin.Version;
                LogContext.FlareVersion = FeedbackService.FlareVersion;
            }
            catch (Exception) { }
        }
    }
}
