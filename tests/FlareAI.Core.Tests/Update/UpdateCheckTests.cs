using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FlareAI.Core.Update;
using FlareAI.TestKit.Update;
using Xunit;

namespace FlareAI.Core.Tests.Update
{
    [Collection("Core statics")]
    public class SemVerTests
    {
        [Theory]
        [InlineData("0.1.3", "0.2.0", -1)]
        [InlineData("v0.2.0", "0.2.0", 0)]
        [InlineData("0.2.0+abc", "0.2.0", 0)]
        [InlineData("0.10.0", "0.9.9", 1)]
        [InlineData("1.0.0-beta", "1.0.0", -1)]
        [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
        [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
        [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
        [InlineData("1.0.0-rc.1", "1.0.0", -1)]
        [InlineData("1.2", "1.2.0", 0)]
        [InlineData("0.1.0.0", "0.1.0", 0)]
        [InlineData("garbage", "0.0.1", -1)]
        public void Compare_FollowsSemVer(string a, string b, int expected)
        {
            Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));
            Assert.Equal(-expected, Math.Sign(SemVer.Compare(b, a)));
        }

        [Fact]
        public void Parse_RejectsNonVersions()
        {
            Assert.Null(SemVer.Parse("latest"));
            Assert.Null(SemVer.Parse("1.x"));
            Assert.Null(SemVer.Parse("1.0.0-"));
            Assert.Equal("1.2.3-beta.1", SemVer.Parse("v1.2.3-beta.1+build").ToString());
        }
    }

    [Collection("Core statics")]
    public class UpdateSettingTests
    {
        [Fact]
        public void Settings_AutoUpdateIsOnByDefault_AndSurvivesAnOldSettingsFile()
        {
            Assert.True(new FlareAI.Core.Settings.PluginSettings().AutoUpdate);
            var old = Newtonsoft.Json.JsonConvert.DeserializeObject<FlareAI.Core.Settings.PluginSettings>("{\"Model\":\"m\"}");
            Assert.True(old.AutoUpdate);
        }
    }

    [Collection("Core statics")]
    public class UpdateCheckTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void IsDue_AtMostOnceADay()
        {
            Assert.True(UpdateCheck.IsDue(new UpdateState(), Now));
            Assert.False(UpdateCheck.IsDue(new UpdateState { LastCheckUtc = Now.AddHours(-23) }, Now));
            Assert.True(UpdateCheck.IsDue(new UpdateState { LastCheckUtc = Now.AddHours(-24) }, Now));
            Assert.True(UpdateCheck.IsDue(new UpdateState { LastCheckUtc = Now.AddDays(3) }, Now));   // clock went back
        }

        [Fact]
        public void NewerRelease_IsOffered_AndTheCheckIsRecorded()
        {
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.2.0", new byte[] { 1, 2, 3 });
                var state = new UpdateState();
                var r = new UpdateCheck("0.1.3", server.LatestUrl).CheckIfDue(state, Now);
                Assert.NotNull(r);
                Assert.Equal("0.2.0", r.Version);
                Assert.Equal("HouseStyle-0.2.0.zip", r.ZipName);
                Assert.EndsWith("/download/checksums.txt", r.ChecksumsUrl);
                Assert.Equal(Now, state.LastCheckUtc);
                Assert.Equal("ok", state.LastCheckResult);
                Assert.Equal("0.2.0", state.LatestVersion);

                // Second call the same day: no request at all.
                var hits = server.Hits.Count;
                Assert.Null(new UpdateCheck("0.1.3", server.LatestUrl).CheckIfDue(state, Now.AddHours(5)));
                Assert.Equal(hits, server.Hits.Count);
            }
        }

        [Theory]
        [InlineData("0.2.0")]
        [InlineData("0.3.0")]
        public void SameOrOlderRelease_IsNotOffered(string running)
        {
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.2.0", new byte[] { 1 });
                Assert.Null(new UpdateCheck(running, server.LatestUrl).CheckIfDue(new UpdateState(), Now));
            }
        }

        [Fact]
        public void PreReleaseRunning_IsOfferedTheFinalRelease()
        {
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.2.0", new byte[] { 1 });
                Assert.NotNull(new UpdateCheck("0.2.0-beta.3", server.LatestUrl).CheckIfDue(new UpdateState(), Now));
            }
        }

        [Fact]
        public void PreReleaseOrDraftAnswer_IsIgnored()
        {
            using (var server = new MockReleaseServer())
            {
                server.Publish("0.3.0-rc.1", new byte[] { 1 }, prerelease: true);
                Assert.Null(new UpdateCheck("0.2.0", server.LatestUrl).CheckIfDue(new UpdateState(), Now));
            }
        }

        [Fact]
        public void Offline_IsSilent_AndStillCountsAsTheDailyCheck()
        {
            // A bound-but-never-accepting port: nobody else (another test, another test process) can claim it meanwhile,
            // and a request to it still never gets an answer.
            var held = new TcpListener(IPAddress.Loopback, 0);
            held.Start(1);
            var port = ((IPEndPoint)held.LocalEndpoint).Port;
            try
            {
            var state = new UpdateState();
            var check = new UpdateCheck("0.1.0", "http://localhost:" + port + "/releases/latest") { TimeoutMs = 3000 };
            Assert.Null(check.CheckIfDue(state, Now));
            Assert.Equal(Now, state.LastCheckUtc);
            Assert.NotEqual("ok", state.LastCheckResult);
            }
            finally { held.Stop(); }
        }

        [Fact]
        public void ServerErrorsAndJunk_AreSilent()
        {
            using (var server = new MockReleaseServer())
            {
                server.Route("/repos/abmorrill4/house-style/releases/latest", MockReleaseServer.Bytes(403, "application/json", Encoding.UTF8.GetBytes("{\"message\":\"API rate limit exceeded\"}")));
                Assert.Null(new UpdateCheck("0.1.0", server.LatestUrl).CheckIfDue(new UpdateState(), Now));
                server.Route("/repos/abmorrill4/house-style/releases/latest", MockReleaseServer.Bytes(200, "text/html", Encoding.UTF8.GetBytes("<html>captive portal</html>")));
                var state = new UpdateState();
                Assert.Null(new UpdateCheck("0.1.0", server.LatestUrl).CheckIfDue(state, Now));
                Assert.NotEqual("ok", state.LastCheckResult);
            }
        }

        [Fact]
        public void ReleaseWithoutChecksums_IsNotOffered()
        {
            var json = "{\"tag_name\":\"v0.9.0\",\"assets\":[{\"name\":\"HouseStyle-0.9.0.zip\",\"browser_download_url\":\"https://example.com/z.zip\"}]}";
            Assert.Null(UpdateCheck.Parse(json));
        }

        [Fact]
        public void PlainHttp_OnlyToThisComputer()
        {
            Assert.True(UpdateCheck.IsAllowedUrl("https://github.com/x"));
            Assert.True(UpdateCheck.IsAllowedUrl("http://localhost:8080/x"));
            Assert.True(UpdateCheck.IsAllowedUrl("http://127.0.0.1/x"));
            Assert.False(UpdateCheck.IsAllowedUrl("http://evil.example.com/x"));
            Assert.False(UpdateCheck.IsAllowedUrl("file:///c:/x.zip"));
            var json = "{\"tag_name\":\"v0.9.0\",\"assets\":[{\"name\":\"HouseStyle-0.9.0.zip\",\"browser_download_url\":\"http://evil.example.com/z.zip\"},{\"name\":\"checksums.txt\",\"browser_download_url\":\"https://example.com/c\"}]}";
            Assert.Null(UpdateCheck.Parse(json));
        }

        [Fact]
        public void State_RoundTrips()
        {
            using (var t = new TempDir())
            {
                new UpdateState { LastCheckUtc = Now, LatestVersion = "0.2.0", WhatsNewSeen = "0.1.3" }.Save(t["s.json"]);
                var s = UpdateState.Load(t["s.json"]);
                Assert.Equal(Now, s.LastCheckUtc.Value.ToUniversalTime());
                Assert.Equal("0.1.3", s.WhatsNewSeen);
                File.WriteAllText(t["s.json"], "not json");
                Assert.Null(UpdateState.Load(t["s.json"]).LastCheckUtc);
            }
        }
    }
}
