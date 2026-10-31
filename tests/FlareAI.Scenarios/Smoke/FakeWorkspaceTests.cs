using System;
using System.IO;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Settings;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Smoke
{
    /// <summary>The scenario sandbox: corpus copy, open documents, read-only folders, AppData redirect, statics reset.</summary>
    [Collection("Core statics")]   // TeamCurrent_… sets Team.Current outside a workspace: keep other workspace-heavy classes from running alongside
    public class FakeWorkspaceTests
    {
        [Fact]
        public void FromCorpus_CopiesTheProject_WithVariables()
        {
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                Assert.True(File.Exists(ws.PathOf("Content/transfers/about.htm")));
                Assert.True(File.Exists(ws.PathOf("Content/reports/about.htm")));       // a content folder named like a build folder is kept
                Assert.EndsWith("DigitalBanking.flprj", ws.ProjectFile);
                Assert.StartsWith(Path.GetTempPath(), ws.ProjectDir, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("Acme Online Banking", ws.Variables["General.ProductName"]);
            }
        }

        [Fact]
        public void AppData_IsRedirected_AndRestored()
        {
            var before = Paths.Root;
            string appData;
            using (var ws = FakeWorkspace.Empty())
            {
                appData = ws.AppData;
                Assert.Equal(ws.AppData, Paths.Root);
                Assert.Equal(ws.AppData, PluginSettings.SettingsDirectory);
                new PluginSettings().Save();
                Assert.True(File.Exists(Path.Combine(ws.AppData, "settings.json")));
                Diagnostics.Log("workspace test line");
                Assert.Contains("workspace test line", File.ReadAllText(ws.DiagLog));
                Assert.NotEmpty(Team.AnonymousId());
                Assert.True(File.Exists(Path.Combine(ws.AppData, "writer-id.txt")));
                Assert.Equal(Path.Combine(ws.AppData, "feedback.jsonl"), FeedbackLog.PersonalPath);
            }
            Assert.Equal(before, Paths.Root);
            Assert.False(Directory.Exists(appData));
        }

        [Fact]
        public void TeamCurrent_IsResetOnEntry_AndRestoredOnDispose()
        {
            var outer = Team.Load(null, null, "outer@example.com");
            var saved = Team.Current;
            Team.Current = outer;
            try
            {
                using (var ws = FakeWorkspace.Empty())
                {
                    Assert.Null(Team.Current);
                    Team.Current = Team.Load(null, null, "inner@example.com");
                    FakeWorkspace.ResetTeam();
                    Assert.Null(Team.Current);
                    Team.Current = Team.Load(null, null, "inner@example.com");
                }
                Assert.Same(outer, Team.Current);
            }
            finally { Team.Current = saved; }
        }

        [Fact]
        public void MakeReadOnly_DeniesWrites_LikeProgramFiles()
        {
            using (var ws = FakeWorkspace.Empty())
            {
                ws.WriteFile("Content/Resources/StyleGuide/Guide.htm", "<html><body><p>x</p></body></html>");
                ws.MakeReadOnly("Content/Resources");
                var existing = ws.PathOf("Content/Resources/StyleGuide/Guide.htm");
                Assert.Equal("<html><body><p>x</p></body></html>", File.ReadAllText(existing));   // still readable
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(existing, "changed"));
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(ws.PathOf("Content/Resources/new.txt"), "new"));
                Assert.Throws<UnauthorizedAccessException>(() => File.Delete(existing));
                File.WriteAllText(ws.PathOf("Content/elsewhere.txt"), "fine");                     // the rest of the project is writable
                ws.ClearReadOnly();
                File.WriteAllText(existing, "changed");
            }
        }

        [Fact]
        public void ReadOnlyFile_RefusesSave()
        {
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                var doc = ws.Open("Content/transfers/about.htm");
                ws.MakeReadOnly("Content/transfers/about.htm");
                Assert.Throws<UnauthorizedAccessException>(() => doc.Save());
            }
        }

        [Fact]
        public void OpenRegistry_TracksAuthorAndPluginOpens()
        {
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                var topic = ws.Open("Content/transfers/about.htm");
                Assert.False(topic.OpenedByPlugin);
                Assert.Same(topic, ws.HostOpen(ws.PathOf("Content/transfers/about.htm")));   // already open: same document, still the author's
                Assert.False(topic.OpenedByPlugin);

                var snippet = (FlareAI.TestKit.Editor.FlareSimDocument)ws.HostOpen("Content/Resources/Snippets/SupportContact.flsnp");
                Assert.True(snippet.OpenedByPlugin);
                Assert.Equal("Acme Online Banking", snippet.VariableValue("General.ProductName"));
                Assert.Equal(2, ws.OpenDocuments.Count);
                Assert.Single(ws.OpenedByPlugin);

                snippet.Close();
                Assert.False(ws.IsOpen("Content/Resources/Snippets/SupportContact.flsnp"));
                Assert.True(ws.IsOpen("Content/transfers/about.htm"));
            }
        }
    }
}
