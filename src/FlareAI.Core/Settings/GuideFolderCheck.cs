using System;
using System.IO;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;

namespace FlareAI.Core.Settings
{
    /// <summary>What a style guide folder is, in words a writer can act on.</summary>
    public sealed class GuideFolderStatus
    {
        /// <summary>The folder can be used as a style guide (a reachable Flare project).</summary>
        public bool Ok { get; set; }
        /// <summary>The folder exists and could be read (false for a missing or offline folder).</summary>
        public bool Reachable { get; set; }
        /// <summary>This writer may change the guide's rules (an owner, or the guide has no owners list).</summary>
        public bool CanChangeRules { get; set; }
        /// <summary>Files can be written in the folder.</summary>
        public bool Writable { get; set; }
        /// <summary>How many House Style rules the guide has (0 for a new guide, which is still usable).</summary>
        public int Rules { get; set; }
        /// <summary>One or two plain sentences: what the folder is, and what this writer can do with it.</summary>
        public string Message { get; set; }
        public override string ToString() { return Message; }
    }

    /// <summary>
    /// Checks a team style guide folder for Settings and first-run setup: a reachable Flare project, how many rules it
    /// has, and whether this writer can change them (owner, read-only). Can be slow on network or synced folders: call it
    /// off the UI thread.
    /// </summary>
    public static class GuideFolderCheck
    {
        /// <param name="path">The folder the writer chose.</param>
        /// <param name="writer">This writer's id (null: the git email or Windows user name, as <see cref="Team"/> uses).</param>
        public static GuideFolderStatus Check(string path, string writer = null)
        {
            path = (path ?? "").Trim().TrimEnd('\\', '/');
            if (path.Length == 0) return Fail(false, "Choose the folder that holds your team's style guide.");
            if (path.Contains("{PluginDir}")) return new GuideFolderStatus { Ok = true, Reachable = true, Message = "The guide in the plugin folder: " + path };
            try
            {
                if (!Directory.Exists(path))
                    return Fail(false, "Can't reach this folder: " + path + ". Check the spelling, and that the network, VPN or synced drive (OneDrive, SharePoint, Google Drive) is connected.");
                var unreadable = StyleEngine.WhyUnreadable(path);
                if (unreadable != null)
                    return Fail(false, "Can't read this style guide folder: " + path + " (" + unreadable + "). Ask whoever manages the folder for read access.");
                var proj = Directory.GetFiles(path, "*.flprj");
                if (proj.Length == 0)
                    return Fail(true, "This folder isn't a Flare project: " + path + ". Choose the folder that holds the guide's .flprj file.");
                if (HasGoogleLayer(path)) return Fail(true, GoogleLayerMessage);
                var rules = GuideLayer.Load(path).Items.Count;
                var team = Team.Load(path, null, writer);
                var writable = FolderAccess.CanWrite(path);
                var name = Path.GetFileNameWithoutExtension(proj[0]);
                // No check mark for a guide with no rules: it works, but it changes nothing until rules are added.
                var head = rules == 0
                    ? name + " has no House Style rules yet. They go in topics in its Content\\Resources\\StyleGuide folder, and the guide's owner can add them with Import Rules. "
                    : "✓ " + name + " (" + rules + (rules == 1 ? " rule" : " rules") + "). ";
                string role;
                if (team.OwnersProblem != null)
                    role = "Read-only for you: House Style can't read the guide's owners list (FlareAI\\owners.txt: " + team.OwnersProblem +
                           "), so it treats the guide as owner-only and your changes go to its owners as requests.";
                else if (!team.IsOwner)
                    role = "Read-only for you: only its owners (" + string.Join(", ", new System.Collections.Generic.List<string>(team.Owners).ToArray()) +
                           ") change its rules. Your suggested changes go to them.";
                else if (!writable)
                    role = "Read-only for you: you can't save files in this folder, so rule changes can't be saved here. Ask whoever manages the folder for write access.";
                else if (team.IsStrict)
                    role = "You're one of its owners, so you can change its rules.";
                else
                    role = "You can use it and change its rules.";
                return new GuideFolderStatus { Ok = true, Reachable = true, CanChangeRules = team.IsOwner && writable, Writable = writable, Rules = rules, Message = head + role };
            }
            catch (Exception ex)
            {
                Diagnostics.Log("guide folder check", ex);
                return Fail(false, "Couldn't read this folder: " + path + ". Check that you have access to it.");
            }
        }

        /// <summary>Why a folder holding Google's guide is refused as a team guide.</summary>
        public const string GoogleLayerMessage =
            "This folder holds Google's guide (Content\\Resources\\StyleGuide\\Google), like the style guide that comes with House Style. " +
            "House Style already uses Google's guide, so it would be loaded twice. Choose your team's own guide folder, " +
            "or leave this empty to use Google's guide plus your own changes.";

        /// <summary>
        /// True when the folder contains the Google layer (Content\Resources\StyleGuide\Google): the plugin's bundled guide
        /// or a full copy of it, which can't be a team guide because Google's rules would load twice.
        /// </summary>
        public static bool HasGoogleLayer(string path)
        {
            try { return !string.IsNullOrEmpty(path) && Directory.Exists(Path.Combine(path.Trim(), @"Content\Resources\StyleGuide\Google")); }
            catch (Exception) { return false; }
        }

        private static GuideFolderStatus Fail(bool reachable, string message) { return new GuideFolderStatus { Ok = false, Reachable = reachable, Message = message }; }
    }

    /// <summary>Folder access checks for shared folders.</summary>
    public static class FolderAccess
    {
        /// <summary>True when a file can be created and deleted in the folder (the only reliable test on shares and synced folders).</summary>
        public static bool CanWrite(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
                var f = Path.Combine(folder, ".house-style-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(f, "test");
                File.Delete(f);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
