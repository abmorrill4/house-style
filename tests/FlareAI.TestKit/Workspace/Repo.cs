using System;
using System.IO;

namespace FlareAI.TestKit.Workspace
{
    /// <summary>Well-known folders of the repository the tests run from.</summary>
    public static class Repo
    {
        /// <summary>The folder holding FlareAI.sln, found by walking up from the test binaries.</summary>
        public static string Root
        {
            get
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
                if (d == null) throw new DirectoryNotFoundException("FlareAI.sln not found above " + AppDomain.CurrentDomain.BaseDirectory);
                return d.FullName;
            }
        }

        public static string Corpus { get { return Path.Combine(Root, "corpus"); } }
        public static string StyleGuide { get { return Path.Combine(Root, "styleguide"); } }
        public static string Fixtures { get { return Path.Combine(Root, Path.Combine("tests", "fixtures")); } }

        /// <summary>A value from the git-ignored .env at the repo root (as scripts\Load-DotEnv.ps1 reads it), or null.</summary>
        public static string DotEnv(string name)
        {
            var file = Path.Combine(Root, ".env");
            if (!File.Exists(file)) return null;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("export ")) line = line.Substring(7).Trim();
                var eq = line.IndexOf('=');
                if (eq <= 0 || line.Substring(0, eq).Trim() != name) continue;
                return line.Substring(eq + 1).Trim().Trim('"', '\'');
            }
            return null;
        }
    }
}
