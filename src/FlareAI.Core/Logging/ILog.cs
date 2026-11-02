namespace FlareAI.Core.Logging
{
    public enum LogLevel { Debug, Info, Warn, Error }

    /// <summary>
    /// The plugin's debug log. Each call takes an event name and alternating field names and values.
    /// Only fields on the allowed list in <see cref="LogFields"/> are ever written.
    /// </summary>
    public interface ILog
    {
        void Debug(string eventName, params object[] nameValuePairs);
        void Info(string eventName, params object[] nameValuePairs);
        void Warn(string eventName, params object[] nameValuePairs);
        void Error(string eventName, params object[] nameValuePairs);
    }

    /// <summary>The default log: does nothing.</summary>
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();

        public void Debug(string eventName, params object[] nameValuePairs) { }
        public void Info(string eventName, params object[] nameValuePairs) { }
        public void Warn(string eventName, params object[] nameValuePairs) { }
        public void Error(string eventName, params object[] nameValuePairs) { }
    }
}
