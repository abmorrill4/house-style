using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// The review list's level and source choice, kept between sessions in <c>review-view.json</c> beside settings.json.
    /// Its own file, so PluginSettings does not grow. A missing, unknown or corrupt value loads as the default; a failed
    /// save is logged and never thrown.
    /// </summary>
    public static class ReviewViewPrefs
    {
        public static string DefaultPath { get { return Paths.In("review-view.json"); } }

        /// <summary>Loads the saved choice from <see cref="DefaultPath"/>; the defaults when there is none.</summary>
        public static void Load(out ReviewLevel level, out ReviewSource source)
        {
            Load(DefaultPath, out level, out source);
        }

        public static void Load(string path, out ReviewLevel level, out ReviewSource source)
        {
            ReviewLevel l = ReviewLevel.All;
            ReviewSource s = ReviewSource.RulesAndAi;
            try
            {
                if (File.Exists(path))
                {
                    var o = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    l = ParseLevel(Text(o, "level"));
                    s = ParseSource(Text(o, "source"));
                }
            }
            catch (Exception) { /* corrupt file → defaults */ }
            level = l;
            source = s;
        }

        /// <summary>Saves the choice to <see cref="DefaultPath"/>.</summary>
        public static void Save(ReviewLevel level, ReviewSource source)
        {
            Save(DefaultPath, level, source);
        }

        public static void Save(string path, ReviewLevel level, ReviewSource source)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var o = new JObject();
                o["level"] = LevelText(level);
                o["source"] = SourceText(source);
                File.WriteAllText(path, o.ToString(Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // Type and path only: the message can quote the file's contents.
                Diagnostics.Log("ReviewViewPrefs.Save failed: " + ex.GetType().Name + " writing " + path);
            }
        }

        /// <summary>The string value of a key, or null when it is missing or not a string.</summary>
        private static string Text(JObject o, string key)
        {
            var v = o[key] as JValue;
            return v == null ? null : v.Value as string;
        }

        private static ReviewLevel ParseLevel(string text)
        {
            switch ((text ?? "").ToLowerInvariant())
            {
                case "errors": return ReviewLevel.Errors;
                case "warnings": return ReviewLevel.Warnings;
                case "suggestions": return ReviewLevel.Suggestions;
                default: return ReviewLevel.All;
            }
        }

        private static ReviewSource ParseSource(string text)
        {
            switch ((text ?? "").ToLowerInvariant())
            {
                case "rulesonly": return ReviewSource.RulesOnly;
                case "aionly": return ReviewSource.AiOnly;
                default: return ReviewSource.RulesAndAi;
            }
        }

        private static string LevelText(ReviewLevel level)
        {
            switch (level)
            {
                case ReviewLevel.Errors: return "errors";
                case ReviewLevel.Warnings: return "warnings";
                case ReviewLevel.Suggestions: return "suggestions";
                default: return "all";
            }
        }

        private static string SourceText(ReviewSource source)
        {
            switch (source)
            {
                case ReviewSource.RulesOnly: return "rulesOnly";
                case ReviewSource.AiOnly: return "aiOnly";
                default: return "rulesAndAi";
            }
        }
    }
}
