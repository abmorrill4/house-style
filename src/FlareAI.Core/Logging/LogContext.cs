using System;
using System.Diagnostics;

namespace FlareAI.Core.Logging
{
    /// <summary>What every log line carries besides its own fields.</summary>
    public static class LogContext
    {
        public static volatile string PluginVersion = "unknown";
        public static volatile string FlareVersion = "unknown";
        public static volatile string RuleSetVersion = "unknown";

        /// <summary>
        /// The id of the command running. A static, not a thread-static: a lint runs its work on Parallel.For threads and
        /// those lines belong to the same command. Commands run one at a time from the UI thread.
        /// </summary>
        public static volatile string CurrentCommandId;
    }

    /// <summary>Wraps one command so its lines share an id and its start, end, duration and result are logged.</summary>
    public static class CommandScope
    {
        public static T Run<T>(string commandName, Func<T> body)
        {
            var previous = LogContext.CurrentCommandId;
            try { LogContext.CurrentCommandId = Guid.NewGuid().ToString("N").Substring(0, 8); } catch (Exception) { }
            Safely(() => Log.Current.Info("command.start", F.Of("command", commandName)));
            var sw = Stopwatch.StartNew();
            var result = "ok";
            try
            {
                return body();
            }
            catch (OperationCanceledException ex)
            {
                result = "cancelled";
                Safely(() => Log.Current.Error("command.error", ex, F.Of("command", commandName)));
                throw;
            }
            catch (Exception ex)
            {
                result = "error";
                Safely(() => Log.Current.Error("command.error", ex, F.Of("command", commandName)));
                throw;
            }
            finally
            {
                var ms = sw.ElapsedMilliseconds;
                var r = result;
                Safely(() => Log.Current.Info("command.end", F.Of("command", commandName), F.Of("ms", ms), F.Of("result", r)));
                LogContext.CurrentCommandId = previous;
            }
        }

        public static void Run(string commandName, Action body)
        {
            Run<object>(commandName, () => { body(); return null; });
        }

        private static void Safely(Action log)
        {
            try { log(); } catch (Exception) { }
        }
    }
}
