using System;

namespace FlareAI.Flare
{
    /// <summary>Remembers the last text House Style selected in the editor, so a later check can tell it from the writer's own selection.</summary>
    internal static class PluginSelection
    {
        private static readonly object _lock = new object();
        private static string _path;
        private static string _text;

        public static void Remember(string path, string text)
        {
            lock (_lock)
            {
                _path = path;
                _text = text;
            }
        }

        public static bool Is(string path, string text)
        {
            lock (_lock)
            {
                if (_text == null) return false;
                return string.Equals(text, _text, StringComparison.Ordinal)
                    && string.Equals(path ?? "", _path ?? "", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
