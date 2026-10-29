using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FlareAI.Core.Settings;
using Newtonsoft.Json;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// The review pane's remembered view state, in review-view.json next to the plugin settings. It holds the last rule the
    /// writer walked place by place, and the project or topic it was walked in: only a SHA-256 hash of that path is stored,
    /// never the path, a title or any document text. Missing or unreadable, it reads as empty.
    /// </summary>
    public sealed class ReviewWalkPrefs
    {
        /// <summary>The longest rule key the prefs accept. Longer is not a rule key, so it is dropped on read.</summary>
        public const int MaxKeyLength = 200;

        /// <summary>The rule walked last: the hash of its project or topic, and the rule's key.</summary>
        public sealed class RememberedWalk
        {
            public string Scope { get; set; }
            public string RuleKey { get; set; }
        }

        /// <summary>The last rule walked place by place, or null.</summary>
        public RememberedWalk Walk { get; set; }

        /// <summary>The file the prefs live in: next to the plugin settings.</summary>
        public static string DefaultPath { get { return Path.Combine(PluginSettings.SettingsDirectory, "review-walk.json"); } }

        /// <summary>A lowercase hex SHA-256 of the UTF-8 bytes of the full path: what the prefs keep in place of the path.</summary>
        public static string ScopeOf(string fullPath)
        {
            var hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(fullPath)));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>True for a rule key the prefs can hold: not empty, and at most <see cref="MaxKeyLength"/> characters.</summary>
        public static bool IsValidKey(string key) { return !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength; }

        /// <summary>Reads the prefs at <paramref name="path"/>. Missing, unreadable or invalid: empty prefs, no error.</summary>
        public static ReviewWalkPrefs Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var p = JsonConvert.DeserializeObject<ReviewWalkPrefs>(File.ReadAllText(path, Encoding.UTF8));
                    if (p != null)
                    {
                        if (p.Walk != null && (p.Walk.Scope == null || !IsValidKey(p.Walk.RuleKey))) p.Walk = null;
                        return p;
                    }
                }
            }
            catch (Exception) { /* corrupt file → empty prefs */ }
            return new ReviewWalkPrefs();
        }

        /// <summary>
        /// Writes the prefs to <paramref name="path"/>. A failure is logged with the exception and the path only, and returns
        /// false; the caller carries on without it.
        /// </summary>
        public bool TrySave(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("review view prefs write " + path, ex);
                return false;
            }
        }

        /// <summary>
        /// The rule the walk memory names in <paramref name="backlog"/>, or null. Only a walk in <paramref name="scope"/> counts,
        /// and the key must equal a rule's key exactly (ordinal); the stored text is never shown.
        /// </summary>
        public BacklogRule RuleIn(string scope, IList<BacklogRule> backlog)
        {
            if (Walk == null || !string.Equals(Walk.Scope, scope, StringComparison.Ordinal)) return null;
            foreach (var r in backlog)
                if (string.Equals(r.Key, Walk.RuleKey, StringComparison.Ordinal)) return r;
            return null;
        }
    }
}
