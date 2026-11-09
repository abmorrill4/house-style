using System;
using System.IO;
using FlareAI.Core.Update;
using Xunit;

namespace FlareAI.Core.Tests.Update
{
    /// <summary>
    /// The updater's swap on a temp folder, with real junctions (mklink /J needs no admin rights). The layout Flare sees
    /// must be ONE junction hop: Plugins\FlareAI -> HouseStyle\current, and current a real folder (Flare 2026 crashes on
    /// a junction to a junction).
    /// </summary>
    public class UpdateSwapTests
    {
        private static string Plugins(TempDir t) { return t["Flare.app\\Plugins\\FlareAI"]; }

        /// <summary>HouseStyle\current (a real folder of <paramref name="version"/>) and Plugins\FlareAI -> current, as Install.cmd makes.</summary>
        private static UpdateLayout Installed(TempDir t, string version = "0.8.0")
        {
            var layout = new UpdateLayout(t["HouseStyle"]);
            FakePlugin.Write(layout.Current, version);
            LinkPlugins(t, layout);
            return layout;
        }

        /// <summary>The layout of earlier release candidates: current -> versions\&lt;ver&gt; (a junction), Plugins\FlareAI -> current.</summary>
        private static UpdateLayout InstalledOldLayout(TempDir t, string version = "0.8.0")
        {
            var layout = new UpdateLayout(t["HouseStyle"]);
            FakePlugin.Write(layout.VersionDir(version), version);
            Junction.Create(layout.Current, layout.VersionDir(version));
            LinkPlugins(t, layout);
            return layout;
        }

        private static void LinkPlugins(TempDir t, UpdateLayout layout)
        {
            Directory.CreateDirectory(t["Flare.app\\Plugins"]);
            Junction.Create(Plugins(t), layout.Current);
        }

        private static void StageReady(UpdateLayout layout, string version, string contains = null)
        {
            FakePlugin.Write(Path.Combine(layout.StagedDir(version), "FlareAI"), contains ?? version);
            File.WriteAllText(Path.Combine(layout.StagedDir(version), ".verified"), "x");
            UpdateStager.WriteManifest(layout.StagedDir(version));
        }

        private static UpdateSwap Swap(UpdateLayout layout) { return new UpdateSwap(layout) { LockRetries = 0, LockRetryDelayMs = 0 }; }

        private static void AssertOneHop(TempDir t, UpdateLayout layout)
        {
            Assert.True(Directory.Exists(layout.Current));
            Assert.False(Junction.IsLink(layout.Current), "current must be a real folder (Flare 2026 crashes on a junction to a junction)");
            Assert.True(Junction.IsLink(Plugins(t)));
            Assert.False(Directory.Exists(layout.Current + ".old"));
            Assert.False(Directory.Exists(layout.Current + ".new"));
        }

        [Fact]
        public void Apply_RefusesAStagedPayloadThatChangedAfterItWasVerified()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t, "0.7.0");
                StageReady(layout, "0.8.0");
                File.AppendAllText(Path.Combine(layout.StagedDir("0.8.0"), "FlareAI", "FlareAI.Flare.dll"), "tampered");
                var r = Swap(layout).Apply("0.8.0");
                Assert.False(r.Ok);
                Assert.Contains("changed after it was checked", r.Error);
                Assert.Equal("0.7.0", UpdateLayout.VersionIn(layout.Current));
                Assert.False(Directory.Exists(layout.StagedDir("0.8.0")));
            }
        }

        [Fact]
        public void Apply_RefusesAStagedPayloadWithoutAManifest()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t, "0.7.0");
                StageReady(layout, "0.8.0");
                File.Delete(Path.Combine(layout.StagedDir("0.8.0"), UpdateStager.ManifestName));
                var r = Swap(layout).Apply("0.8.0");
                Assert.False(r.Ok);
                Assert.Equal("0.7.0", UpdateLayout.VersionIn(layout.Current));
            }
        }

        [Fact]
        public void Apply_SwapsCurrentByRenaming_KeepsThePreviousVersion_AndWritesTheResult()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t);
                StageReady(layout, "0.9.0");

                var r = Swap(layout).Apply("0.9.0");

                Assert.True(r.Ok, r.Error);
                Assert.Equal("0.8.0", r.From);
                Assert.Equal("0.9.0", r.To);
                AssertOneHop(t, layout);
                Assert.Equal("0.9.0", UpdateLayout.VersionIn(layout.Current));
                Assert.Equal("0.9.0", UpdateLayout.VersionIn(Plugins(t)));   // Flare sees it through its one link
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(layout.VersionDir("0.8.0")));   // rollback copy kept
                Assert.False(Directory.Exists(layout.StagedDir("0.9.0")));
                string why;
                Assert.True(layout.Manages(Plugins(t), out why), why);
                var saved = UpdateResult.Read(layout.ResultPath);
                Assert.True(saved.Ok);
                Assert.Equal("0.9.0", saved.To);
            }
        }

        [Fact]
        public void Apply_WhenTheNewVersionFailsItsCheck_RollsBackByRenaming()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t);
                StageReady(layout, "0.9.0");
                var swap = Swap(layout);
                swap.AfterSwap = _ => { throw new UpdateException("Simulated failure after the swap."); };

                var r = swap.Apply("0.9.0");

                Assert.False(r.Ok);
                Assert.Equal("Simulated failure after the swap.", r.Error);
                AssertOneHop(t, layout);
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(layout.Current));
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(Plugins(t)));
                Assert.False(UpdateResult.Read(layout.ResultPath).Ok);
            }
        }

        [Fact]
        public void Apply_WithAPayloadOfTheWrongVersion_RollsBack_AndDropsThePayload()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t);
                StageReady(layout, "0.9.0", contains: "0.7.0");   // says 0.9.0, holds 0.7.0
                var r = Swap(layout).Apply("0.9.0");
                Assert.False(r.Ok);
                Assert.Contains("holds 0.7.0", r.Error);
                AssertOneHop(t, layout);
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(layout.Current));
                Assert.Null(UpdateStager.ReadyPayload(layout, "0.9.0"));
            }
        }

        [Fact]
        public void Apply_WithNothingStaged_FailsAndChangesNothing()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t);
                var r = Swap(layout).Apply("0.9.0");
                Assert.False(r.Ok);
                Assert.Contains("No downloaded update", r.Error);
                AssertOneHop(t, layout);
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(layout.Current));
            }
        }

        [Fact]
        public void Apply_WithoutAPerUserInstall_Refuses()
        {
            using (var t = new TempDir())
            {
                var layout = new UpdateLayout(t["HouseStyle"]);
                StageReady(layout, "0.9.0");
                var r = Swap(layout).Apply("0.9.0");
                Assert.False(r.Ok);
                Assert.Contains("isn't installed", r.Error);
                Assert.False(Directory.Exists(layout.Current));
            }
        }

        [Fact]
        public void Apply_WhileFlareHoldsTheFiles_RefusesAndKeepsTheDownload()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t);
                StageReady(layout, "0.9.0");
                UpdateResult r;
                using (new FileStream(Path.Combine(layout.Current, "FlareAI.Core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))   // as a loaded DLL
                    r = Swap(layout).Apply("0.9.0");

                Assert.False(r.Ok);
                Assert.Contains("still in use", r.Error);
                AssertOneHop(t, layout);
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(layout.Current));
                Assert.NotNull(UpdateStager.ReadyPayload(layout, "0.9.0"));   // offered again on the next start

                Assert.True(Swap(layout).Apply("0.9.0").Ok);   // and installs once the files are free
                Assert.Equal("0.9.0", UpdateLayout.VersionIn(layout.Current));
            }
        }

        [Fact]
        public void Apply_KeepsOnlyThePreviousVersion()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t, "0.8.0");
                FakePlugin.Write(layout.VersionDir("0.7.0"), "0.7.0");
                StageReady(layout, "0.9.0");
                Assert.True(Swap(layout).Apply("0.9.0").Ok);
                Assert.False(Directory.Exists(layout.VersionDir("0.7.0")));
                Assert.True(Directory.Exists(layout.VersionDir("0.8.0")));
                Assert.False(Directory.Exists(layout.VersionDir("0.9.0")));   // the active version lives in current\

                StageReady(layout, "1.0.0");
                Assert.True(Swap(layout).Apply("1.0.0").Ok);
                Assert.False(Directory.Exists(layout.VersionDir("0.8.0")));
                Assert.Equal("0.9.0", UpdateLayout.VersionIn(layout.VersionDir("0.9.0")));
                Assert.Equal("1.0.0", UpdateLayout.VersionIn(Plugins(t)));
            }
        }

        [Fact]
        public void Apply_ToAKeptVersion_RollsBackWithoutADownload()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t, "0.8.0");
                StageReady(layout, "0.9.0");
                Assert.True(Swap(layout).Apply("0.9.0").Ok);
                var r = Swap(layout).Apply("0.8.0");
                Assert.True(r.Ok, r.Error);
                AssertOneHop(t, layout);
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(Plugins(t)));
                Assert.Equal("0.9.0", UpdateLayout.VersionIn(layout.VersionDir("0.9.0")));
            }
        }

        [Fact]
        public void Apply_OnTheOldJunctionLayout_ReplacesCurrentWithARealFolder()
        {
            using (var t = new TempDir())
            {
                var layout = InstalledOldLayout(t, "0.8.0");
                Assert.True(layout.NeedsRepair);
                StageReady(layout, "0.9.0");

                var r = Swap(layout).Apply("0.9.0");

                Assert.True(r.Ok, r.Error);
                Assert.Equal("0.8.0", r.From);
                AssertOneHop(t, layout);
                Assert.False(layout.NeedsRepair);
                Assert.Equal("0.9.0", UpdateLayout.VersionIn(Plugins(t)));
                Assert.Equal("0.8.0", UpdateLayout.VersionIn(layout.VersionDir("0.8.0")));   // the old link's target, kept
                string why;
                Assert.True(layout.Manages(Plugins(t), out why), why);
            }
        }

        [Fact]
        public void Apply_AfterASwapStoppedBetweenItsRenames_RestoresCurrentFirst()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t, "0.8.0");
                Directory.Move(layout.Current, layout.Current + ".old");   // power cut after the first rename
                FakePlugin.Write(layout.Current + ".new", "0.9.0");
                StageReady(layout, "0.9.0");

                var r = Swap(layout).Apply("0.9.0");

                Assert.True(r.Ok, r.Error);
                Assert.Equal("0.8.0", r.From);
                AssertOneHop(t, layout);
                Assert.Equal("0.9.0", UpdateLayout.VersionIn(Plugins(t)));
            }
        }

        [Fact]
        public void Apply_TheSameVersion_Refuses()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t, "0.8.0");
                StageReady(layout, "0.8.0");
                var r = Swap(layout).Apply("0.8.0");
                Assert.False(r.Ok);
                Assert.Contains("already installed", r.Error);
                AssertOneHop(t, layout);
            }
        }

        [Fact]
        public void JunctionRemoval_NeverDeletesTheTarget()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t);
                Junction.Remove(Plugins(t));
                Assert.False(Directory.Exists(Plugins(t)));
                Assert.True(File.Exists(Path.Combine(layout.Current, "FlareAI.Flare.dll")));
                Assert.Throws<IOException>(() => Junction.Remove(layout.Current));   // a real folder is refused
            }
        }

        [Fact]
        public void Manages_OnlyThePerUserLayoutWithARealCurrent()
        {
            using (var t = new TempDir())
            {
                var layout = Installed(t);
                string why;
                Assert.True(layout.Manages(Plugins(t), out why), why);
                Assert.True(layout.Manages(layout.Current, out why), why);
                Assert.False(layout.NeedsRepair);

                FakePlugin.Write(t["ProgramFiles\\Plugins\\FlareAI"], "0.8.0");   // an old all-users copy
                Assert.False(layout.Manages(t["ProgramFiles\\Plugins\\FlareAI"], out why));
                Assert.False(new UpdateLayout(t["Nobody"]).Manages(Plugins(t), out why));

                // Plugins\FlareAI linked straight at a kept version (a hand-made workaround) isn't current.
                FakePlugin.Write(layout.VersionDir("0.7.0"), "0.7.0");
                Junction.Create(t["Direct"], layout.VersionDir("0.7.0"));
                Assert.False(layout.Manages(t["Direct"], out why));
            }
        }

        [Fact]
        public void Manages_RefusesACurrentThatIsAJunction()
        {
            using (var t = new TempDir())
            {
                var layout = InstalledOldLayout(t);
                string why;
                Assert.True(layout.NeedsRepair);
                Assert.False(layout.Manages(Plugins(t), out why));
                Assert.Contains("installer", why);
            }
        }
    }
}
