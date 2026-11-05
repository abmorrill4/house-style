using System;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using Xunit;

namespace FlareAI.Core.Tests
{
    public class ProjectCheckTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "flareai-proj-" + Guid.NewGuid().ToString("N"));

        public ProjectCheckTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Sub"));
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Resources\StyleGuide"));
            File.WriteAllText(Path.Combine(_root, "Demo.flprj"), "<CatapultProject />");
            const string ns = "xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"";
            File.WriteAllText(Path.Combine(_root, @"Content\A.htm"), "<html " + ns + "><body><p>We utilize it.</p></body></html>");
            File.WriteAllText(Path.Combine(_root, @"Content\Sub\B.htm"), "<html " + ns + "><body><ul><li>Fine.</li><li>Ask the user.</li></ul></body></html>");
            File.WriteAllText(Path.Combine(_root, @"Content\Resources\StyleGuide\Guide.htm"), "<html><body><p>Never utilize jargon.</p></body></html>");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        [Fact]
        public void ProjectFiles_FindsTopicsAndSnippets_ButNotTheStyleGuideFolder()
        {
            var files = ActionRunner.ProjectFiles(Path.Combine(_root, @"Content\Sub\B.htm"));
            Assert.Equal(new[] { "A.htm", "B.htm" }, files.Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public void FolderScope_ListsFoldersWithFiles_AndLintsOnlyThePickedOnesWithTheirSubfolders()
        {
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Sub\Deep"));
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Other"));
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Empty"));
            File.WriteAllText(Path.Combine(_root, @"Content\Sub\Deep\C.htm"), "<html><body><p>Deep.</p></body></html>");
            File.WriteAllText(Path.Combine(_root, @"Content\Other\D.htm"), "<html><body><p>Other.</p></body></html>");
            var content = Path.Combine(_root, "Content");
            var files = ActionRunner.ProjectFiles(Path.Combine(content, "A.htm"));

            // The style guide folder and the folder with no files are not offered.
            Assert.Equal(new[] { @"Other\", @"Sub\", @"Sub\Deep\" }, FolderScope.Folders(files, content).ToArray());

            string[] Names(params string[] picked) { return FolderScope.Filter(files, content, picked).Select(Path.GetFileName).ToArray(); }
            Assert.Equal(new[] { "B.htm", "C.htm" }, Names(@"Sub\"));              // subfolders come with the folder
            Assert.Equal(new[] { "C.htm" }, Names(@"Sub\Deep\"));
            Assert.Equal(new[] { "B.htm", "C.htm", "D.htm" }, Names(@"Other\", @"Sub\").OrderBy(n => n).ToArray());   // several folders
            Assert.Equal(new[] { "B.htm", "C.htm" }, Names("Sub"));                // no trailing backslash is fine
            Assert.Empty(Names());                                                  // nothing picked, nothing linted
            Assert.Empty(Names(@"Sub\Dee"));                                        // a folder name is not a prefix match

            // Exclusions still win.
            LintExclusions.Add(Path.Combine(content, "A.htm"), @"Sub\Deep\");
            files = ActionRunner.ProjectFiles(Path.Combine(content, "A.htm"));
            Assert.Equal(new[] { @"Other\", @"Sub\" }, FolderScope.Folders(files, content).ToArray());
            Assert.Equal(new[] { "B.htm" }, Names(@"Sub\"));

            // A sibling that shares a prefix, a different case, a file outside Content, and a folder picked inside another picked one.
            Directory.CreateDirectory(Path.Combine(_root, @"Content\Subway"));
            File.WriteAllText(Path.Combine(_root, @"Content\Subway\E.htm"), "<html><body><p>Subway.</p></body></html>");
            files = ActionRunner.ProjectFiles(Path.Combine(content, "A.htm"));
            Assert.Equal(new[] { "B.htm" }, Names(@"Sub\"));
            Assert.Equal(new[] { "E.htm" }, Names("subway"));
            Assert.Empty(FolderScope.Folders(new[] { @"E:\elsewhere\X.htm" }, content));
            Assert.Equal("Sub", FolderScope.Describe(new[] { @"Sub\", @"Sub\Deep\" }));

            Assert.Equal("Sub", FolderScope.Describe(new[] { @"Sub\" }));
            Assert.Equal(@"Other, Sub", FolderScope.Describe(new[] { @"Other\", @"Sub\" }));
            Assert.Equal(@"Other, Sub and 1 more", FolderScope.Describe(new[] { @"Other\", @"Sub\", @"Zed\" }));
        }

        [Fact]
        public void ProjectFiles_LeavesOutExcludedFilesAndFolders_UntilIncludedAgain()
        {
            var b = Path.Combine(_root, @"Content\Sub\B.htm");
            Assert.Equal(@"Sub\", LintExclusions.EntryFor(b, true));
            LintExclusions.Add(b, LintExclusions.EntryFor(b, true));
            Assert.Equal(new[] { "A.htm" }, ActionRunner.ProjectFiles(b).Select(Path.GetFileName).ToArray());
            Assert.Equal(2, ActionRunner.ProjectFiles(b, true).Count);   // snippet usage still counts excluded topics
            LintExclusions.Add(b, "A.htm");
            Assert.Empty(ActionRunner.ProjectFiles(b));
            LintExclusions.Remove(b, @"Sub\");
            Assert.Equal(new[] { "B.htm" }, ActionRunner.ProjectFiles(b).Select(Path.GetFileName).ToArray());
            Assert.True(File.Exists(Path.Combine(_root, @"Project\HouseStyle\LintExclusions.txt")));
        }

        [Fact]
        public void LintFiles_OneBatch_FindingsCarryTheirFile_AndFixInThatFile()
        {
            var runner = new ActionRunner(new FakeLlmClient(r => "[]"), new PromptBuilder(null), 1024, new FakeStyleService());
            var files = ActionRunner.ProjectFiles(Path.Combine(_root, @"Content\A.htm"));
            var findings = runner.LintFiles(files);
            var utilize = findings.Single(f => f.Rule == "plain-words.utilize");
            Assert.EndsWith(@"Content\A.htm", utilize.DocumentPath);
            Assert.EndsWith(@"Sub\B.htm", findings.Single(f => f.Rule == "person.the-user").DocumentPath);

            var a = new FakeDocumentAdapter(utilize.DocumentPath, true);   // what Flare opens
            Assert.Equal(1, runner.ApplyFixes(a, new[] { utilize }, "fix"));
            Assert.Contains("We use it.", a.Document.OuterXml);
        }
    }
}
