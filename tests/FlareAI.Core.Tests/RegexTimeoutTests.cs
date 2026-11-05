using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>Runs in a fresh AppDomain, because .NET reads the default regex timeout once per domain.</summary>
    public sealed class RegexProbe : MarshalByRefObject
    {
        public string DefaultTimeout(bool apply)
        {
            if (apply) RegexLimit.Apply();
            return new System.Text.RegularExpressions.Regex("a").MatchTimeout.ToString();
        }

        public string[] ValidateGuide(string root, string bundled)
        {
            RegexLimit.Apply();
            var report = new StyleEngine(new[] { root }, bundled).Validate(null);
            return report.Issues.Select(i => i.Level + "|" + i.Location + "|" + i.Message).ToArray();
        }
    }

    /// <summary>A guide pattern that never finishes is stopped, and the rest of the lint carries on.</summary>
    public class RegexTimeoutTests
    {
        private static string Repo
        {
            get
            {
                var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
                return d.FullName;
            }
        }

        private static string TempProject(Action<string> fill)
        {
            var root = Path.Combine(Path.GetTempPath(), "flareai-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Content"));
            File.WriteAllText(Path.Combine(root, "Demo.flprj"), "<CatapultProject />");
            fill(root);
            return root;
        }

        private static void Write(string root, string rel, string text)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        private static T InFreshDomain<T>(Func<RegexProbe, T> use)
        {
            // CodeBase is where the assembly was built, next to FlareAI.Core.dll. Location can be a shadow copy that holds only the test assembly.
            var bin = Path.GetDirectoryName(new Uri(typeof(RegexProbe).Assembly.CodeBase).LocalPath);
            var domain = AppDomain.CreateDomain("probe", null, new AppDomainSetup
            {
                ApplicationBase = bin,
                ConfigurationFile = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile
            });
            try
            {
                var probe = (RegexProbe)domain.CreateInstanceAndUnwrap(typeof(RegexProbe).Assembly.FullName, typeof(RegexProbe).FullName);
                return use(probe);
            }
            finally { AppDomain.Unload(domain); }
        }

        [Fact]
        public void DefaultTimeout_IsSetBeforeAnyPatternIsCompiled()
        {
            Assert.Equal(TimeSpan.FromSeconds(2).ToString(), InFreshDomain(p => p.DefaultTimeout(true)));
            Assert.Equal(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout.ToString(), InFreshDomain(p => p.DefaultTimeout(false)));
        }

        [Fact]
        public void SlowPattern_IsSkippedAndReportedWithItsRow()
        {
            var root = TempProject(r =>
                Write(r, @"Content\Resources\StyleGuide\Slow.htm", @"<html xmlns:MadCap=""http://www.madcapsoftware.com/Schemas/MadCap.xsd""><body><h1>Slow</h1>
                    <table><tr><th>Pattern</th><th>Use</th><th>Severity</th><th>Why</th><th>Example</th></tr>
                      <tr><td>(a+)+$</td><td>(rewrite)</td><td>warning</td><td>Slow.</td><td>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!</td></tr></table>
                    </body></html>"));
            try
            {
                var watch = Stopwatch.StartNew();
                var issues = InFreshDomain(p => p.ValidateGuide(root, Path.Combine(Repo, "styleguide")));
                watch.Stop();
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "Took " + watch.Elapsed);
                Assert.Contains(issues, i => i.StartsWith("warning|") && i.Split('|')[1].Contains("table row 2 (") && i.Contains("skipped"));
                Assert.DoesNotContain(issues, i => i.Contains("doesn't trigger"));
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }
}
