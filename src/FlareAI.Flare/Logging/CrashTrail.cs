using System;
using System.Globalization;
using System.IO;
using FlareAI.Core;
using FlareAI.Core.Logging;

namespace FlareAI.Flare.Logging
{
    /// <summary>
    /// A marker file written at start and removed on a clean exit, so the next start can tell the last run ended badly,
    /// plus a handler that writes an unhandled error to the log before Flare closes. The handler only observes. No
    /// Application.ThreadException handler is added (see Safe.cs for why).
    /// </summary>
    internal static class CrashTrail
    {
        private static bool _hooked;

        public static void Start()
        {
            try
            {
                var marker = Paths.CrashMarker;
                if (File.Exists(marker))
                {
                    // Only the file's timestamp is kept: the last run's start, as the marker was written.
                    var when = File.GetLastWriteTimeUtc(marker).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                    Log.Current.Warn("crash.previous", F.Of("result", "did not exit cleanly"), F.Of("last_start", when));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(marker));
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n" + LogContext.PluginVersion + "\n");
            }
            catch (Exception) { }
        }

        public static void Stop()
        {
            try
            {
                var marker = Paths.CrashMarker;
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch (Exception) { }
        }

        public static void Hook()
        {
            try
            {
                if (_hooked) return;
                _hooked = true;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            }
            catch (Exception) { }
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                Log.Current.Error("crash.unhandled", ex ?? new Exception("unhandled"));
                var log = Log.Current as SerilogLog;
                if (log != null) log.Dispose();   // flushes the file before the process ends
            }
            catch (Exception) { }
        }
    }
}
