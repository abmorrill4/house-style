using System;
using System.IO;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Integration.Tests.Support
{
    /// <summary>
    /// Golden files: the expected output of a test, checked in next to it and compared after normalising line endings.
    /// To accept a deliberate change, run the tests with FLAREAI_UPDATE_GOLDEN=1 and review the diff.
    /// </summary>
    public static class Golden
    {
        /// <param name="folder">Relative to the repo root, e.g. tests/FlareAI.Integration.Tests/Guide/golden.</param>
        public static void Check(string folder, string name, string actual)
        {
            var path = Path.Combine(Repo.Root, folder.Replace('/', Path.DirectorySeparatorChar), name);
            actual = Normalize(actual);
            if (Environment.GetEnvironmentVariable("FLAREAI_UPDATE_GOLDEN") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, actual, new System.Text.UTF8Encoding(false));
                return;
            }
            Assert.True(File.Exists(path), "Missing golden file " + path + ". Run with FLAREAI_UPDATE_GOLDEN=1 to create it, then review and commit it.");
            Assert.Equal(Normalize(File.ReadAllText(path)), actual);
        }

        private static string Normalize(string s) { return (s ?? "").Replace("\r\n", "\n").TrimEnd() + "\n"; }
    }
}
