using System;
using System.IO;
using System.Text;
using FlareAI.Core.Update;
using FlareAI.Flare;
using FlareAI.TestKit.Update;
using Xunit;

namespace FlareAI.Core.Tests.Update
{
    public class UpdateStagerTests
    {
        private static ReleaseInfo Release(MockReleaseServer server, string version)
        {
            var state = new UpdateState();
            var r = new UpdateCheck("0.0.1", server.LatestUrl).CheckIfDue(state, DateTime.UtcNow);
            Assert.True(r != null, "the update check returned nothing: " + state.LastCheckResult);
            Assert.Equal(version, r.Version);
            return r;
        }

        private static UpdateStager Stager(TempDir t)
        {
            return new UpdateStager(new UpdateLayout(t["HouseStyle"]), new ZipExtractor()) { CheckSignature = _ => SignatureStatus.Unsigned, PublicKeyXml = MockReleaseServer.PublicKeyXml };
        }

        [Fact]
        public void UnsignedRelease_IsRejected_EvenWithAMatchingChecksum()
        {
            using (var t = new TempDir())
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.9.0", FakePlugin.Zip("0.9.0"), signed: false);
                var ex = Assert.Throws<UpdateException>(() => Stager(t).Stage(Release(server, "0.9.0")));
                Assert.Contains("isn't signed", ex.Message);
                AssertNothingStaged(t);
            }
        }

        [Fact]
        public void ReleaseSignedWithAnotherKey_IsRejected()
        {
            // What anyone with a GitHub token could publish: a valid zip and checksums, signed with their own key.
            using (var t = new TempDir())
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.9.0", FakePlugin.Zip("0.9.0"));
                var stager = new UpdateStager(new UpdateLayout(t["HouseStyle"]), new ZipExtractor()) { CheckSignature = _ => SignatureStatus.Unsigned };   // House Style's real key
                var ex = Assert.Throws<UpdateException>(() => stager.Stage(Release(server, "0.9.0")));
                Assert.Contains("release key", ex.Message);
                AssertNothingStaged(t);
            }
        }

        [Fact]
        public void ChecksumsChangedAfterSigning_AreRejected()
        {
            using (var t = new TempDir())
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.9.0", FakePlugin.Zip("0.9.0"));
                // The zip and a matching checksums.txt are swapped; the old signature stays.
                var evil = FakePlugin.Zip("0.9.0", s => File.WriteAllText(Path.Combine(s, "evil.txt"), "x"));
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(evil)).Replace("-", "").ToLowerInvariant();
                server.Route("/download/HouseStyle-0.9.0.zip", MockReleaseServer.Bytes(200, "application/zip", evil));
                server.Route("/download/checksums.txt", MockReleaseServer.Bytes(200, "text/plain", Encoding.ASCII.GetBytes(hash + "  HouseStyle-0.9.0.zip\n")));
                var ex = Assert.Throws<UpdateException>(() => Stager(t).Stage(Release(server, "0.9.0")));
                Assert.Contains("release key", ex.Message);
                AssertNothingStaged(t);
            }
        }

        [Fact]
        public void GoodRelease_IsDownloadedVerifiedAndUnpacked()
        {
            using (var t = new TempDir())
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.9.0", FakePlugin.Zip("0.9.0"));
                var stager = Stager(t);
                var payload = stager.Stage(Release(server, "0.9.0"));
                var layout = new UpdateLayout(t["HouseStyle"]);
                Assert.Equal(Path.Combine(layout.StagedDir("0.9.0"), "FlareAI"), payload);
                Assert.Equal("0.9.0", UpdateLayout.VersionIn(payload));
                Assert.Equal(payload, UpdateStager.ReadyPayload(layout, "0.9.0"));
                Assert.Empty(Directory.GetFiles(layout.Staged));   // the zip is gone, only the unpacked folder stays

                // Staging again doesn't download again.
                var hits = server.Hits.Count;
                Assert.Equal(payload, stager.Stage(Release(server, "0.9.0")));
                Assert.Equal(hits + 1, server.Hits.Count);   // just the releases/latest call above
            }
        }

        [Fact]
        public void ChecksumMismatch_IsRejected_AndNothingIsStaged()
        {
            using (var t = new TempDir())
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.9.0", FakePlugin.Zip("0.9.0"), checksumOverride: new string('a', 64));
                var ex = Assert.Throws<UpdateException>(() => Stager(t).Stage(Release(server, "0.9.0")));
                Assert.Contains("checksum", ex.Message);
                AssertNothingStaged(t);
            }
        }

        [Fact]
        public void TamperedDownload_IsRejected()
        {
            using (var t = new TempDir())
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.9.0", FakePlugin.Zip("0.9.0"));
                // Same checksums.txt, different bytes served for the zip (a proxy or mirror changed it).
                server.Route("/download/HouseStyle-0.9.0.zip", MockReleaseServer.Bytes(200, "application/zip", FakePlugin.Zip("0.9.0", s => File.WriteAllText(Path.Combine(s, "evil.txt"), "x"))));
                Assert.Throws<UpdateException>(() => Stager(t).Stage(Release(server, "0.9.0")));
                AssertNothingStaged(t);
            }
        }

        [Fact]
        public void CorruptZip_IsRejected()
        {
            using (var t = new TempDir())
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.9.0", Encoding.ASCII.GetBytes("PK\u0003\u0004 this is not really a zip"));
                var ex = Assert.Throws<UpdateException>(() => Stager(t).Stage(Release(server, "0.9.0")));
                Assert.Contains("damaged", ex.Message);
                AssertNothingStaged(t);
            }
        }

        [Fact]
        public void IncompletePayload_IsRejected()
        {
            using (var t = new TempDir())
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.9.0", FakePlugin.Zip("0.9.0", s => File.Delete(Path.Combine(s, "FlareAI", "FlareAI.Updater.exe"))));
                var ex = Assert.Throws<UpdateException>(() => Stager(t).Stage(Release(server, "0.9.0")));
                Assert.Contains("FlareAI.Updater.exe", ex.Message);
                AssertNothingStaged(t);
            }
        }

        [Fact]
        public void PayloadOfAnotherVersion_IsRejected()
        {
            using (var t = new TempDir())
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.9.0", FakePlugin.Zip("0.8.0"));
                var ex = Assert.Throws<UpdateException>(() => Stager(t).Stage(Release(server, "0.9.0")));
                Assert.Contains("0.8.0", ex.Message);
            }
        }

        [Fact]
        public void EntryOutsideTheFolder_IsRejected()
        {
            using (var t = new TempDir())
            {
                var zip = t["evil.zip"];
                using (var a = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                using (var w = new StreamWriter(a.CreateEntry("../../escaped.txt").Open())) w.Write("x");
                Directory.CreateDirectory(t["out"]);
                Assert.Throws<InvalidDataException>(() => new ZipExtractor().Extract(zip, t["out"]));
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(t.Path), "escaped.txt")));
            }
        }

        [Fact]
        public void Signatures_InvalidIsRejected_AndUnsignedIsRejectedWhenTheInstalledOneIsSigned()
        {
            using (var t = new TempDir())
            {
                FakePlugin.Write(t["new"], "0.9.0");
                FakePlugin.Write(t["installed"], "0.8.0");
                var stager = new UpdateStager(new UpdateLayout(t["HouseStyle"]), new ZipExtractor()) { RunningPluginDir = t["installed"] };

                stager.CheckSignature = p => SignatureStatus.Invalid;
                Assert.Contains("signature", Assert.Throws<UpdateException>(() => stager.Validate(t["new"], "0.9.0")).Message);

                stager.CheckSignature = p => p.StartsWith(t["installed"]) ? SignatureStatus.Valid : SignatureStatus.Unsigned;
                Assert.Contains("not signed", Assert.Throws<UpdateException>(() => stager.Validate(t["new"], "0.9.0")).Message);

                stager.CheckSignature = p => SignatureStatus.Unsigned;   // unsigned builds update unsigned builds
                stager.Validate(t["new"], "0.9.0");
                stager.CheckSignature = p => SignatureStatus.Valid;
                stager.Validate(t["new"], "0.9.0");
            }
        }

        [Fact]
        public void Authenticode_UnsignedFileIsUnsigned()
        {
            using (var t = new TempDir())
            {
                FakePlugin.Write(t.Path, "0.9.0");
                Assert.Equal(SignatureStatus.Unsigned, Authenticode.Check(t["FlareAI.Flare.dll"]));
            }
        }

        [Theory]
        [InlineData("abc", null)]
        [InlineData("{0}  HouseStyle-0.9.0.zip", "{0}")]
        [InlineData("{0} *HouseStyle-0.9.0.zip", "{0}")]
        [InlineData("{0}", "{0}")]
        [InlineData("{0}  HouseStyle-0.8.0.zip", null)]
        public void ExpectedHash_ReadsSha256SumFormat(string line, string expected)
        {
            var h = new string('c', 64);
            Assert.Equal(expected == null ? null : string.Format(expected, h), UpdateStager.ExpectedHash(string.Format(line, h), "HouseStyle-0.9.0.zip"));
        }

        private static void AssertNothingStaged(TempDir t)
        {
            var staged = new UpdateLayout(t["HouseStyle"]).Staged;
            Assert.True(!Directory.Exists(staged) || Directory.GetFileSystemEntries(staged).Length == 0,
                        "left behind: " + (Directory.Exists(staged) ? string.Join(", ", Directory.GetFileSystemEntries(staged)) : ""));
        }
    }
}
