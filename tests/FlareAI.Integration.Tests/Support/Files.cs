using System.IO;
using System.Linq;

namespace FlareAI.Integration.Tests.Support
{
    /// <summary>Small file helpers for tests.</summary>
    public static class Files
    {
        public static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
        }

        /// <summary>A comparable picture of a folder: every file's relative path and content.</summary>
        public static string Snapshot(string folder)
        {
            return string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p)
                .Select(p => p.Substring(folder.Length) + ":" + File.ReadAllText(p).GetHashCode()).ToArray());
        }

        /// <summary>
        /// The team guide a shared folder would hold: a style guide Flare project (<c>TeamGuide.flprj</c>, rules in
        /// Content\Resources\StyleGuide\Company) whose Company rules are a copy of <paramref name="companyRules"/> (default: the
        /// bundled ones), plus an owners list when owners are given. Its layer is named "company" (FlareAI\layer.txt), so the rule ids are the bundled guide's ("company:terminology/3").
        /// </summary>
        public static string TeamGuide(string root, string companyRules = null, params string[] owners)
        {
            var guide = Path.Combine(root, "TeamGuide");
            Directory.CreateDirectory(guide);
            File.WriteAllText(Path.Combine(guide, "TeamGuide.flprj"), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<CatapultProject />");
            Copy(companyRules ?? Path.Combine(FlareAI.TestKit.Workspace.Repo.StyleGuide, @"Content\Resources\StyleGuide\Company"), Path.Combine(guide, @"Content\Resources\StyleGuide\Company"));
            Directory.CreateDirectory(Path.Combine(guide, "FlareAI"));
            File.WriteAllText(Path.Combine(guide, @"FlareAI\layer.txt"), "company\n");
            if (owners.Length > 0) File.WriteAllText(Path.Combine(guide, @"FlareAI\owners.txt"), "# style guide owners\n" + string.Join("\n", owners) + "\n");
            return guide;
        }
    }
}
