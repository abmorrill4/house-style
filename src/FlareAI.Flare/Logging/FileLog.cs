using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FlareAI.Core.Logging;
using CoreLog = FlareAI.Core.Logging.Log;

namespace FlareAI.Flare.Logging
{
    /// <summary>
    /// The debug log on disk: JSON Lines in one current file (housestyle.log) that rolls at a size and keeps the newest few.
    /// Only fields on the <see cref="LogFields"/> list are written. Writing a line never throws and never changes what the
    /// plugin does. It uses plain System.IO, so no third-party assembly has to load inside Flare.
    /// </summary>
    internal sealed class FileLog : ILog, IDisposable
    {
        public const long DefaultMaxBytes = 5L * 1024 * 1024;
        public const int DefaultKeep = 10;
        private const string CurrentName = "housestyle.log";

        /// <summary>The rule set version stamped on every line; set when the style guide is read.</summary>
        public static string RuleSetVersion = "unknown";

        private readonly object _gate = new object();
        private readonly string _folder;
        private readonly LogLevel _min;
        private readonly long _maxBytes;
        private readonly int _keep;
        private readonly string _pluginVersion;
        private readonly string _flareVersion;
        private bool _reported;

        private FileLog(string folder, LogLevel min, string pluginVersion, string flareVersion, long maxBytes, int keep)
        {
            _folder = folder;
            _min = min;
            _maxBytes = maxBytes < 200 ? 200 : maxBytes;
            _keep = keep < 1 ? 1 : keep;
            _pluginVersion = Clean("plugin_version", pluginVersion);
            _flareVersion = Clean("flare_version", flareVersion);
        }

        /// <summary>True when the log file couldn't be opened or written. The reason is in the diagnostics file.</summary>
        public bool Unavailable { get; private set; }

        public static string DefaultFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HouseStyle", "logs"); }
        }

        public static LogLevel DefaultLevel(bool debugSetting)
        {
#if DEBUG
            return LogLevel.Debug;
#else
            return debugSetting ? LogLevel.Debug : LogLevel.Info;
#endif
        }

        /// <summary>Opens the log. If the folder can't be written, the result logs nothing and says so in <see cref="Unavailable"/>.</summary>
        public static FileLog Create(string folder, LogLevel min, string pluginVersion, string flareVersion, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
        {
            var log = new FileLog(folder, min, pluginVersion, flareVersion, maxBytes, keep);
            try
            {
                Directory.CreateDirectory(folder);
                // Fail now, and visibly, rather than on the first line.
                using (new FileStream(Path.Combine(folder, CurrentName), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
            }
            catch (Exception ex) { log.Fail("open", ex); }
            return log;
        }

        public void Debug(string eventName, params object[] nameValuePairs) { Write(LogLevel.Debug, eventName, nameValuePairs); }
        public void Info(string eventName, params object[] nameValuePairs) { Write(LogLevel.Info, eventName, nameValuePairs); }
        public void Warn(string eventName, params object[] nameValuePairs) { Write(LogLevel.Warn, eventName, nameValuePairs); }
        public void Error(string eventName, params object[] nameValuePairs) { Write(LogLevel.Error, eventName, nameValuePairs); }

        /// <summary>Nothing is held open or buffered, so there is nothing to flush.</summary>
        public void Dispose() { }

        private void Fail(string what, Exception ex)
        {
            Unavailable = true;
            if (_reported) return;
            _reported = true;
            try { FlareAI.Core.Diagnostics.Log("debug log " + what, ex); } catch (Exception) { }
        }

        private void Write(LogLevel level, string eventName, object[] pairs)
        {
            try
            {
                if (level < _min) return;
                var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (pairs != null)
                {
                    for (var i = 0; i + 1 < pairs.Length; i += 2)
                    {
                        var key = pairs[i] as string;
                        object clean;
                        if (key == null || !LogFields.TryClean(key, pairs[i + 1], out clean)) continue;
                        values[key.ToLowerInvariant()] = clean;
                    }
                }
                var name = Clean("component", eventName == null ? null : eventName.Replace(' ', '_'));

                var sb = new StringBuilder(256);
                sb.Append("{\"@t\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)).Append('"');
                sb.Append(",\"@l\":\"").Append(level).Append('"');
                sb.Append(",\"event\":"); AppendValue(sb, name == "unknown" ? "event" : name);
                AppendPair(sb, "component", values.ContainsKey("component") ? values["component"] : "plugin");
                AppendPair(sb, "command_id", Clean("command_id_raw", CoreLog.CommandId));
                AppendPair(sb, "plugin_version", _pluginVersion);
                AppendPair(sb, "flare_version", _flareVersion);
                AppendPair(sb, "rule_set_version", Clean("rule_set_version", RuleSetVersion));
                var fixedNames = new[] { "component", "command_id", "plugin_version", "flare_version", "rule_set_version", "event" };
                foreach (var kv in values)
                    if (!fixedNames.Contains(kv.Key)) AppendPair(sb, kv.Key, kv.Value);
                sb.Append("}\n");

                lock (_gate)
                {
                    var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                    var path = Path.Combine(_folder, CurrentName);
                    Directory.CreateDirectory(_folder);
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 0 && info.Length + bytes.Length > _maxBytes) Roll(path);
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex) { Fail("write", ex); }
        }

        /// <summary>Moves the current file aside under a name that sorts by age, then removes all but the newest few.</summary>
        private void Roll(string current)
        {
            var archive = Path.Combine(_folder, "housestyle-" + DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + ".log");
            var n = 0;
            while (File.Exists(archive)) archive = Path.Combine(_folder, "housestyle-" + (DateTime.UtcNow.Ticks + ++n).ToString("D19", CultureInfo.InvariantCulture) + ".log");
            File.Move(current, archive);
            var old = Directory.GetFiles(_folder, "housestyle-*.log").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(_keep - 1);
            foreach (var f in old)
            {
                try { File.Delete(f); } catch (Exception) { }
            }
        }

        private static void AppendPair(StringBuilder sb, string key, object value)
        {
            sb.Append(',');
            AppendValue(sb, key);
            sb.Append(':');
            AppendValue(sb, value);
        }

        private static void AppendValue(StringBuilder sb, object value)
        {
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is int || value is long) { sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            sb.Append('"');
            foreach (var c in Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static string Clean(string field, string value)
        {
            if (field == "command_id_raw") return string.IsNullOrEmpty(value) ? "" : new string(value.Where(char.IsLetterOrDigit).Take(32).ToArray());
            object clean;
            return LogFields.TryClean(field, value, out clean) ? (string)clean : "unknown";
        }
    }
}
