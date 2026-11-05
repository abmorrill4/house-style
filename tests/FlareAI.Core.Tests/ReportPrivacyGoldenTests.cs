using FlareAI.Core;
using FlareAI.Core.Feedback;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>Golden outputs for what may leave the computer in a log line or report: paths, secrets and addresses.</summary>
    public sealed class ReportPrivacyGoldenTests
    {
        [Theory]
        [InlineData(@"C:\Users\jdoe\Documents\My Project\Content\a.htm", @"[project]\a.htm")]
        [InlineData(@"D:\Clients\Acme\Content\b.htm", @"[project]\b.htm")]
        [InlineData("E:/Work/Acme/c.htm", @"[project]\c.htm")]
        [InlineData(@"\\nas01\Projects\Acme\d.htm", @"[project]\d.htm")]
        [InlineData("//nas01/Projects/e.htm", @"[project]\e.htm")]
        [InlineData(@"C:\Users\jdoe\", "[project]")]
        [InlineData(@"Content\Topics\f.htm", @"Content\Topics\f.htm")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void Path_LeavesNoUserDriveOrHost(string path, string expected)
        {
            Assert.Equal(expected, Diagnostics.Path(path));
        }

        [Theory]
        [InlineData(@"C:\Users\jdoe\x.htm")]
        [InlineData(@"\\fileserver\share\x.htm")]
        [InlineData("Z:/jdoe/x.htm")]
        public void Path_NeverKeepsTheOriginalParts(string path)
        {
            var p = Diagnostics.Path(path);
            Assert.DoesNotContain("jdoe", p);
            Assert.DoesNotContain("fileserver", p);
            Assert.DoesNotContain(":", p);
            Assert.DoesNotContain(@"\\", p);
        }

        // Built at run time so no line of this file looks like a real key to a secret scanner.
        public static System.Collections.Generic.IEnumerable<object[]> FakeSecrets()
        {
            System.Func<int, string> fill = n => new string('q', n);
            yield return new object[] { "key sk-ant-api03-" + fill(16) + " failed", fill(16) };
            yield return new object[] { "key sk-" + fill(24) + " failed", fill(24) };
            yield return new object[] { "Authorization: Bearer " + fill(22), fill(22) };
            yield return new object[] { "x-api-" + "key: " + fill(12), fill(12) };
            yield return new object[] { "pass" + "word=" + fill(14), fill(14) };
            yield return new object[] { "token: gh" + "p_" + fill(30), fill(30) };
            yield return new object[] { "github_" + "pat_" + fill(26), fill(26) };
            yield return new object[] { "xox" + "b-" + "1234567890" + "-" + fill(6), "1234567890" };
            yield return new object[] { "AK" + "IA" + new string('A', 16), new string('A', 16) };
            yield return new object[] { "ey" + "J" + fill(12) + ".ey" + "J" + fill(12) + "." + fill(18), fill(12) };
            yield return new object[] { "https://user:" + fill(10) + "@gateway.example.com/v1", fill(10) };
        }

        [Theory]
        [MemberData(nameof(FakeSecrets))]
        public void SecretFamilies_AreRemoved(string text, string secret)
        {
            var clean = ReportBuilder.Redact(ReportBuilder.RedactNames(text), null);
            Assert.Contains("[removed]", clean);
            Assert.DoesNotContain(secret, clean);
        }

        [Fact]
        public void SecretFamilies_KeepTheLabelAndTheSentence()
        {
            Assert.Equal("Request failed: pass" + "word=[removed] for the gateway",
                         ReportBuilder.Redact(ReportBuilder.RedactNames("Request failed: pass" + "word=" + new string('q', 14) + " for the gateway"), null));
        }

        [Fact]
        public void KnownSecret_IsRemovedWhateverItLooksLike()
        {
            Assert.Equal("the gateway said [removed] was refused", ReportBuilder.Redact("the gateway said abc123 was refused", new[] { "abc123" }));
        }

        [Theory]
        [InlineData("Couldn't reach https://llm.acme-internal.example:8443/v1/messages", "Couldn't reach https://[host]/v1/messages")]
        [InlineData("Couldn't reach https://api.anthropic.com/v1/messages", "Couldn't reach https://api.anthropic.com/v1/messages")]
        [InlineData("http://10.0.0.5:4000/v1", "http://[host]/v1")]
        public void GatewayUrls_KeepOnlyTheirHostFree_Form(string text, string expected)
        {
            Assert.Equal(expected, ReportBuilder.Anonymize(text, null));
        }

        [Fact]
        public void Anonymize_CutsDriveAndUncPaths_AndNames()
        {
            Assert.Equal(@"open [project]\a.htm and [project]\b.htm, mail [email]",
                         ReportBuilder.Anonymize(@"open D:\Clients\Acme\a.htm and \\nas01\p\Acme\b.htm, mail jo.doe@acme.com", null));
        }
    }
}
