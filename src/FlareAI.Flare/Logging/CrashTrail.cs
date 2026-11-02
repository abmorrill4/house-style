using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FlareAI.Core.Logging;
using CoreLog = FlareAI.Core.Logging.Log;

namespace FlareAI.Flare.Logging
{
    /// <summary>
    /// A marker file written at start and removed on a clean exit, so the next start can tell the last session ended badly,
    /// and handlers that write an unhandled error to the log before Flare closes. The handlers only observe.
    /// </summary>
    internal static class CrashTrail
    {
        public const string MarkerName = "running.marker";

        private static string _marker;
        private static bool _hooked;

        public static void Start(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                _marker = Path.Combine(folder, MarkerName);
                if (File.Exists(_marker)) CoreLog.Current.Warn("previous_session_ended_unclean");
                File.WriteAllText(_marker, DateTime.UtcNow.ToString("o") + " pid " + Process.GetCurrentProcess().Id);
            }
            catch (Exception) { }
        }

        public static void CleanExit()
        {
            try { if (_marker != null && File.Exists(_marker)) File.Delete(_marker); }
            catch (Exception) { }
        }

        public static void Hook()
        {
            try
            {
                if (_hooked) return;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                TaskScheduler.UnobservedTaskException += OnUnobserved;
                _hooked = true;
            }
            catch (Exception) { }
        }

        public static void Unhook()
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
                _hooked = false;
            }
            catch (Exception) { }
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            Record(e.ExceptionObject as Exception);
        }

        private static void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Record(e.Exception);
        }

        private static void Record(Exception ex)
        {
            try
            {
                if (ex == null) return;
                // The file sink isn't buffered, so the line is on disk when this returns.
                CoreLog.Caught("unhandled", ex);
            }
            catch (Exception) { }
        }
    }
}
