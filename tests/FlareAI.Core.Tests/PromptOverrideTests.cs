using System;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class PromptOverrideTests
    {
        [Fact]
        public void ATeamOverrideReplacesTheBuiltInTemplate()
        {
            var dir = Path.Combine(Path.GetTempPath(), "prompts-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                var builder = new PromptBuilder(null);
                builder.OverrideFolder = dir;
                Assert.Contains("senior technical editor", builder.BuildUser(ActionKind.CheckStyle, "<p>x</p>", null));
                File.WriteAllText(Path.Combine(dir, "action-check.md"), "Be strict about banking terms.\n{{LINT}}\n{{FRAGMENT}}");
                var user = builder.BuildUser(ActionKind.CheckStyle, "<p>x</p>", null, "- lint");
                Assert.StartsWith("Be strict about banking terms.", user);
                Assert.Contains("<p>x</p>", user);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "prompts-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void PresetUsesBuiltInWordingWithoutAFolder()
        {
            var dir = TempDir();
            try
            {
                var builder = new PromptBuilder(null) { OverrideFolder = dir };
                Assert.Contains("plainer words", builder.BuildPreset(RewritePresets.Simplify));
                Assert.Contains("fewer words", builder.BuildPreset(RewritePresets.Shorten));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PresetTeamFileReplacesOnlyTheInstruction()
        {
            var dir = TempDir();
            try
            {
                var builder = new PromptBuilder(null) { OverrideFolder = dir };
                var simplify = builder.BuildPreset(RewritePresets.Simplify);
                var system = builder.BuildSystem();
                File.WriteAllText(Path.Combine(dir, "preset-shorten.md"), "Cut it to one sentence.");
                Assert.Equal("Cut it to one sentence.", builder.BuildPreset(RewritePresets.Shorten));
                Assert.Equal(simplify, builder.BuildPreset(RewritePresets.Simplify));
                Assert.Equal(system, builder.BuildSystem());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void PresetsCarryNoStyleRules()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
            Assert.NotNull(d);
            var guide = Path.Combine(d.FullName, "styleguide");
            var rules = new FlareAI.Core.Style.StyleEngine(new[] { guide }, guide).Rules(null);
            Assert.NotEmpty(rules);

            foreach (var name in new[] { "preset-simplify.md", "preset-shorten.md" })
            {
                Assert.Contains(name, PromptBuilder.Templates);
                var text = PromptBuilder.BuiltIn(name);
                Assert.False(string.IsNullOrWhiteSpace(text), name);
                var sentences = text.Split('.').Select(s => s.Trim()).Where(s => s.Length >= 20).ToList();
                foreach (var rule in rules)
                {
                    foreach (var own in new[] { rule.Text, rule.Display })
                        if (own != null && own.Length >= 20)
                            Assert.False(text.IndexOf(own, StringComparison.OrdinalIgnoreCase) >= 0, name + " contains the text of rule " + rule.Id);
                    if (rule.Text == null) continue;
                    foreach (var s in sentences)
                        Assert.False(rule.Text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0, name + " copies wording from rule " + rule.Id + ": " + s);
                }
            }
        }
    }
}
