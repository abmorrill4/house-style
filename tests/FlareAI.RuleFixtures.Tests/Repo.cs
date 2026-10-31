using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.FixtureGen;
using Newtonsoft.Json;

namespace FlareAI.RuleFixtures.Tests
{
    /// <summary>Where things are in the repository, and the checked-in fixtures read back.</summary>
    internal static class Repo
    {
        public static readonly string Root = FindRoot();
        public static string Guide { get { return Path.Combine(Root, "styleguide"); } }
        public static string FixtureDir { get { return Path.Combine(Root, @"tests\fixtures\rules"); } }
        public static string GenDir { get { return Path.Combine(Root, @"tools\FlareAI.FixtureGen"); } }

        private static string FindRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
            if (d == null) throw new InvalidOperationException("FlareAI.sln not found above " + AppDomain.CurrentDomain.BaseDirectory);
            return d.FullName;
        }

        public static List<RuleFixture> LoadRules()
        {
            var all = new List<RuleFixture>();
            foreach (var layer in new[] { "google", "company" })
            {
                var dir = Path.Combine(FixtureDir, layer);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
                    all.AddRange(JsonConvert.DeserializeObject<List<RuleFixture>>(File.ReadAllText(f)));
            }
            return all;
        }

        public static StructureFile LoadStructure()
        {
            return JsonConvert.DeserializeObject<StructureFile>(File.ReadAllText(Path.Combine(FixtureDir, @"structure\structure.json")));
        }

        public static Manifest LoadManifest()
        {
            return JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(Path.Combine(FixtureDir, "manifest.json")));
        }
    }
}
