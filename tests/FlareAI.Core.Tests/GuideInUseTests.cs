using System;
using System.IO;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>Which team style guide is in use, with its rule count, or why it isn't (#348).</summary>
    public class GuideInUseTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "hs-inuse-" + Guid.NewGuid().ToString("N"));

        public GuideInUseTests() { Directory.CreateDirectory(_tmp); }
        public void Dispose() { try { Directory.Delete(_tmp, true); } catch (Exception) { } }

        private string Project(string name, bool withRule)
        {
            var dir = Path.Combine(_tmp, name);
            Directory.CreateDirectory(Path.Combine(dir, @"Content\Resources\StyleGuide\Company"));
            File.WriteAllText(Path.Combine(dir, name + ".flprj"), "<?xml version=\"1.0\" encoding=\"utf-8\"?><CatapultProject />");
            if (withRule) GuideCopyTests.Write(dir, @"Content\Resources\StyleGuide\Company\Words.htm", GuideCopyTests.Topic("frobnicate"));
            return dir;
        }

        private static void NoColonsOrSemicolons(string line)
        {
            Assert.NotNull(line);
            Assert.DoesNotContain(":", line);
            Assert.DoesNotContain(";", line);
            Assert.DoesNotContain("→", line);
        }

        [Fact]
        public void AGuideWithRules_IsInUse_WithItsCount()
        {
            var dir = Project("TeamGuide", true);
            var info = GuideInUse.Describe(dir, "me@example.com");
            Assert.Equal(GuideState.InUse, info.State);
            Assert.Equal("TeamGuide", info.Name);
            Assert.True(info.Rules > 0);
            Assert.Equal(GuideLayer.Load(dir).Items.Count, info.Rules);
            Assert.Equal("Using your team style guide TeamGuide with " + Plural.Of(info.Rules, "rule") + ".", info.Line);
            Assert.DoesNotContain(_tmp, info.Line);   // the name only, never the path
            NoColonsOrSemicolons(info.Line);
        }

        [Fact]
        public void OneRule_ReadsAsOneRule()
        {
            var line = GuideInUse.Line(new GuideInUseInfo { Name = "TeamGuide", State = GuideState.InUse, Rules = 1 });
            Assert.Contains("1 rule.", line);
            Assert.DoesNotContain("1 rules", line);
            Assert.Contains("12 rules", GuideInUse.Line(new GuideInUseInfo { Name = "TeamGuide", State = GuideState.InUse, Rules = 12 }));
        }

        [Fact]
        public void AGuideWithNoRules_SaysSo_AndWhereTheRulesGo()
        {
            var dir = Project("Empty", false);
            var info = GuideInUse.Describe(dir, "me@example.com");
            Assert.Equal(GuideState.Empty, info.State);
            Assert.Equal(0, info.Rules);
            Assert.Contains("Empty has no rules yet", info.Line);
            Assert.Contains(@"Content\Resources\StyleGuide", info.Line);
            NoColonsOrSemicolons(info.Line);
        }

        [Fact]
        public void AMissingFolder_SaysItCantBeReached()
        {
            var info = GuideInUse.Describe(Path.Combine(_tmp, "missing", "Guide"), "me@example.com", new GuideCopy(Path.Combine(_tmp, "copies", GuideCopy.FolderName)));
            Assert.Equal(GuideState.Missing, info.State);
            Assert.Equal("Guide", info.Name);
            Assert.StartsWith("Can't reach Guide", info.Line);
            Assert.Contains("network, VPN or synced drive", info.Line);
            Assert.DoesNotContain(_tmp, info.Line);
            NoColonsOrSemicolons(info.Line);
        }

        [Fact]
        public void AFolderThatIsNotAFlareProject_SaysSo()
        {
            var dir = Path.Combine(_tmp, "plain");
            Directory.CreateDirectory(dir);
            var info = GuideInUse.Describe(dir, "me@example.com");
            Assert.Equal(GuideState.NotProject, info.State);
            Assert.StartsWith("plain isn't a Flare project.", info.Line);
            NoColonsOrSemicolons(info.Line);
        }

        [Fact]
        public void AnUnreadableFolder_SaysWhy()
        {
            // No test helper makes a folder this process can't read, so the wording is checked from the state Describe returns for one.
            var info = new GuideInUseInfo { Name = "Guide", State = GuideState.Unreadable, Reason = "you don't have permission to read it" };
            Assert.Equal("Can't read Guide because you don't have permission to read it. Ask whoever manages the folder for read access.", info.Line);
            NoColonsOrSemicolons(info.Line);
        }

        [Fact]
        public void AMissingFolderWithASavedCopy_IsOffline_WithTheCopysCount()
        {
            var dir = Project("Shared", true);
            var copies = new GuideCopy(Path.Combine(_tmp, "copies", GuideCopy.FolderName));
            Assert.True(copies.Refresh(dir));
            var expected = GuideLayer.Load(copies.Find(dir).Path).Items.Count;
            Directory.Delete(dir, true);

            var info = GuideInUse.Describe(dir, "me@example.com", copies);
            Assert.Equal(GuideState.Offline, info.State);
            Assert.Equal(expected, info.Rules);
            Assert.True(expected > 0);
            Assert.NotNull(info.CopiedUtc);
            Assert.Contains("Can't reach Shared right now", info.Line);
            Assert.Contains("copy", info.Line);
            Assert.Contains(Plural.Of(expected, "rule"), info.Line);
            NoColonsOrSemicolons(info.Line);
        }

        [Fact]
        public void Lines_JoinsWithASpace_AndIsNullForNone()
        {
            Assert.Null(GuideInUse.Lines(new GuideInUseInfo[0]));
            Assert.Null(GuideInUse.Lines(null));
            var both = GuideInUse.Lines(new[]
            {
                new GuideInUseInfo { Name = "A", State = GuideState.InUse, Rules = 2 },
                new GuideInUseInfo { Name = "B", State = GuideState.Missing }
            });
            Assert.Equal("Using your team style guide A with 2 rules. Can't reach B. Check the spelling, and that the network, VPN or synced drive is connected.", both);
        }

        [Fact]
        public void DescribeNeverThrows_ForAnEmptyOrOddPath()
        {
            Assert.Null(GuideInUse.Describe(""));
            Assert.Null(GuideInUse.Describe(null));
            Assert.NotNull(GuideInUse.Describe("::not a path::"));
        }
    }
}
