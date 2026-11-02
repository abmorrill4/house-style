using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Style;

namespace FlareAI.Core.Settings
{
    /// <summary>Whether a team style guide is in use, and if not, why.</summary>
    public enum GuideState { InUse, Empty, Offline, Missing, Unreadable, NotProject }

    /// <summary>One team style guide as the writer sees it: its name, how many rules it gave, and why it isn't used.</summary>
    public sealed class GuideInUseInfo
    {
        /// <summary>The guide's folder or project name only, never the full path.</summary>
        public string Name { get; set; }
        /// <summary>The configured folder. For the log and for matching; not shown.</summary>
        public string Folder { get; set; }
        public int Rules { get; set; }
        public GuideState State { get; set; }
        /// <summary>For <see cref="GuideState.Unreadable"/>, why, in plain words.</summary>
        public string Reason { get; set; }
        /// <summary>For <see cref="GuideState.Offline"/>, when the copy in use was saved.</summary>
        public DateTime? CopiedUtc { get; set; }

        public string Line { get { return GuideInUse.Line(this); } }
    }

    /// <summary>
    /// The one-line status of a team style guide for Settings and the Issues pane. <see cref="Describe"/> checks the folder
    /// and belongs on a background worker. The lint path never calls it: it reads the guides the lint already loaded.
    /// </summary>
    public static class GuideInUse
    {
        public static string Line(GuideInUseInfo info)
        {
            if (info == null) return null;
            var name = info.Name;
            switch (info.State)
            {
                case GuideState.InUse:
                    return "Using your team style guide " + name + " with " + Plural.Of(info.Rules, "rule") + ".";
                case GuideState.Empty:
                    return "Your team style guide " + name + " has no rules yet. They go in topics in its Content\\Resources\\StyleGuide folder.";
                case GuideState.Offline:
                    // Date only: the time of day would add a colon.
                    var when = info.CopiedUtc.HasValue ? StyleEngine.CopyDate(info.CopiedUtc.Value, DateTime.MinValue) : "earlier";
                    return "Can't reach " + name + " right now, so House Style is using the copy it saved on " + when + " with " + Plural.Of(info.Rules, "rule") + ".";
                case GuideState.Missing:
                    return "Can't reach " + name + ". Check the spelling, and that the network, VPN or synced drive is connected.";
                case GuideState.Unreadable:
                    return "Can't read " + name + " because " + (string.IsNullOrEmpty(info.Reason) ? "it couldn't be opened" : info.Reason.TrimEnd('.', ' ')) + ". Ask whoever manages the folder for read access.";
                case GuideState.NotProject:
                    return name + " isn't a Flare project. Choose the folder that holds the guide's .flprj file.";
            }
            return null;
        }

        /// <summary>The lines joined with a space, or null for none (a writer with no team guide gets no team line).</summary>
        public static string Lines(IEnumerable<GuideInUseInfo> infos)
        {
            if (infos == null) return null;
            var lines = infos.Select(Line).Where(l => !string.IsNullOrEmpty(l)).ToArray();
            return lines.Length == 0 ? null : string.Join(" ", lines);
        }

        internal static string NameOf(string folder)
        {
            try { return Path.GetFileName((folder ?? "").Trim().TrimEnd('\\', '/')); }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// Checks the writer's own configured folder and says whether it is in use. Slow on network folders: run it on a
        /// background worker. Never throws.
        /// </summary>
        public static GuideInUseInfo Describe(string path, string writer = null, GuideCopy copies = null)
        {
            return Describe(path, null, writer, copies);
        }

        /// <summary>As above, from a check the caller already ran, so a slow share is checked once. Null <paramref name="check"/> runs it.</summary>
        public static GuideInUseInfo Describe(string path, GuideFolderStatus check, string writer = null, GuideCopy copies = null)
        {
            path = (path ?? "").Trim().TrimEnd('\\', '/');
            if (path.Length == 0) return null;
            var info = new GuideInUseInfo { Name = NameOf(path), Folder = path };
            try
            {
                check = check ?? GuideFolderCheck.Check(path, writer);
                if (check.Ok)
                {
                    info.Rules = check.Rules;
                    info.State = check.Rules > 0 ? GuideState.InUse : GuideState.Empty;
                    return info;
                }
                if (check.Reachable) { info.State = GuideState.NotProject; return info; }

                bool permission = false;
                if (Directory.Exists(path))
                {
                    info.State = GuideState.Unreadable;
                    info.Reason = StyleEngine.WhyUnreadable(path, out permission) ?? "it couldn't be opened";
                }
                else info.State = GuideState.Missing;

                if (permission) return info;
                var copy = (copies ?? GuideCopy.ForWriter).Find(path);
                if (copy != null && !string.IsNullOrEmpty(copy.Path) && Directory.Exists(copy.Path))
                {
                    info.State = GuideState.Offline;
                    info.Reason = null;
                    info.Rules = GuideLayer.Load(copy.Path).Items.Count;
                    info.CopiedUtc = copy.CopiedUtc;
                }
                return info;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("guide in use", ex);
                info.State = GuideState.Unreadable;
                info.Reason = "of an error reading it";
                return info;
            }
        }
    }
}
