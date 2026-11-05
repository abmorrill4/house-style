using System.IO;
using System.Linq;
using FlareAI.Core.Output;
using FlareAI.Integration.Tests.Support;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Integration.Tests.Output
{
    /// <summary>
    /// Export for LLMs over a built HTML5 target. The build output under Output/html5 is a stand-in for one captured in Flare 22
    /// (frame page, skin and resource files, navigation, breadcrumbs, scripts, and topics with variables, snippets and conditions
    /// already resolved, as a build leaves them); replace it with a real capture when one is available.
    /// </summary>
    public class LlmExportTests
    {
        private const string Golden_ = "tests/FlareAI.Integration.Tests/Output/golden";

        private static string Fixture { get { return Path.Combine(Repo.Root, @"tests\FlareAI.Integration.Tests\Output\html5"); } }

        /// <summary>A file's text with LF line ends (a checkout may have converted them).</summary>
        private static string Read(string path) { return File.ReadAllText(path).Replace("\r\n", "\n"); }

        private static string[] Files_(string root) { return Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => f.Substring(root.Length + 1).Replace('\\', '/')).OrderBy(f => f, System.StringComparer.Ordinal).ToArray(); }

        // ---- 31 · TEST-PLAN E2 ------------------------------------------------------------------------------------

        [Fact]
        public void Story31_WriterExportsForLlms_OneMarkdownFilePerTopic_CodeFencedTablesKeptLinksRewritten_NavigationAndFrameFilesLeftOut()
        {
            // E2: llms.txt and one .md per topic in the output folder.
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                var output = Path.Combine(ws.ProjectDir, @"Output\pat\HTML5");
                FlareAI.Integration.Tests.Support.Files.Copy(Fixture, output);

                var result = LlmExport.Export(output);

                Assert.Equal(4, result.Topics);
                var produced = Files_(output).Where(f => f == "llms.txt" || f == "llms-full.txt" || f.StartsWith("llm/")).ToArray();
                Assert.Equal(new[]
                {
                    "llm/Content/Customers/search-for-a-customer-record.md", "llm/Content/deposit/about.md", "llm/Content/reference/send-message.md",
                    "llm/Content/talk/messages.md", "llms-full.txt", "llms.txt"
                }, produced);                                                                       // not the frame page, skins, resources, or the empty topic
                foreach (var f in produced) Golden.Check(Golden_, f, Read(Path.Combine(output, f.Replace('/', '\\'))));

                var index = Read(result.LlmsTxt);
                Assert.StartsWith("# DigitalBanking — HTML5\n", index);                              // named after the project and the target
                Assert.Contains("- [About mobile deposit](llm/Content/deposit/about.md): Mobile deposit gives members", index);

                var send = Read(Path.Combine(output, @"llm\Content\reference\send-message.md"));
                Assert.Contains("```\nimport client\n", send);                                       // code fenced, kept verbatim
                Assert.Contains("\"content\": \"Hello & welcome to the <team>!\"", send);             // entities decoded inside the code
                Assert.Contains("| Argument | Type | Description |\n|---|---|---|\n| `type` | string |", send);   // tables stay tables
                Assert.Contains("[Interacting with messages](../talk/messages.md)", send);            // links to topics point at the Markdown
                var search = Read(Path.Combine(output, @"llm\Content\Customers\search-for-a-customer-record.md"));
                Assert.Contains("| Notes | Free text, up to 255 characters \\| no markup. |", search);   // a pipe in a cell is escaped
                Assert.Contains("1. On the main menu, choose **Customers**.", search);
                Assert.Contains("   - The list shows matching records.", search);                   // nested list
                Assert.Contains("![The Customers screen with the Search box](../Resources/Images/screen-new.png)", search);
                Assert.Contains("[dispatch-support@marlowe-dispatch.example.com](mailto:dispatch-support@marlowe-dispatch.example.com)", search);
                Assert.Contains("[Create a customer record](Create_a_customer_record.md)", search);
                var messages = Read(Path.Combine(output, @"llm\Content\talk\messages.md"));
                Assert.Contains("[Calendar appointments](../groupware/calendar-appointments.md)", messages);
                Assert.Contains("[community forum](https://help.example.com/community)", messages);   // outside links are left alone
                Assert.DoesNotMatch("Skip to main content|You are here|Copyright|Helpful|Search\\)", Read(Path.Combine(output, @"llm\Content\deposit\about.md")));
                Assert.DoesNotContain("analytics", send);                                              // scripts never reach the Markdown

                Assert.Contains("title: About mobile deposit\nsource: Content/deposit/about.htm\n---", Read(Path.Combine(output, @"llm\Content\deposit\about.md")));
            }
        }

        [Fact]
        public void Story31b_WriterExportsAgain_NothingNewIsPickedUpFromTheirOwnExport_AndABaseUrlAddsEachTopicsAddress()
        {
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                var output = Path.Combine(ws.ProjectDir, @"Output\pat\HTML5");
                FlareAI.Integration.Tests.Support.Files.Copy(Fixture, output);
                var first = LlmExport.Export(output);
                var before = Read(first.LlmsTxt);

                var again = LlmExport.Export(output, null, "https://docs.example.com/help/");

                Assert.Equal(first.Topics, again.Topics);                                              // the llm folder is not a topic
                Assert.Equal(before, Read(again.LlmsTxt));
                Assert.Contains("url: https://docs.example.com/help/Content/deposit/about.htm\n", Read(Path.Combine(output, @"llm\Content\deposit\about.md")));

                var elsewhere = Path.Combine(ws.Root, "published");
                var moved = LlmExport.Export(output, elsewhere, null, "Acme help");
                Assert.StartsWith("# Acme help\n", Read(moved.LlmsTxt));
                Assert.Equal(Path.Combine(elsewhere, "llms.txt"), moved.LlmsTxt);
                Assert.True(File.Exists(Path.Combine(elsewhere, @"llm\Content\talk\messages.md")));
            }
        }

        // Things a Flare build contains that the export gets wrong today (a separate fixture, so the golden files above hold only good output).

        private static string ExportQuirks(FakeWorkspace ws, out string output)
        {
            output = Path.Combine(ws.ProjectDir, @"Output\pat\HTML5");
            FlareAI.Integration.Tests.Support.Files.Copy(Path.Combine(Repo.Root, @"tests\FlareAI.Integration.Tests\Output\html5-known-issues"), output);
            LlmExport.Export(output);
            return Read(Path.Combine(output, @"llm\Content\quirks.md"));
        }

        [Fact]
        public void Story31c_ABlockQuoteInATopic_StaysAQuote()
        {
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                string output;
                var md = ExportQuirks(ws, out output);

                Assert.Contains("> Rate limits apply to every client.", md);
            }
        }

        [Fact]
        public void Story31d_DropDownAndTogglerHeadings_AreTextNotLinks()
        {
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                string output;
                var md = ExportQuirks(ws, out output);

                Assert.DoesNotContain("javascript:", md);
                Assert.Contains("What can I do with a message?", md);
                Assert.Contains("Show more details", md);
            }
        }

        [Fact]
        public void Story31e_TheSummaryInLlmsTxt_HasNoLinksThatOnlyWorkInsideTheTopicsFolder()
        {
            using (var ws = FakeWorkspace.FromCorpus("DigitalBanking"))
            {
                string output;
                ExportQuirks(ws, out output);

                var index = Read(Path.Combine(output, "llms.txt"));
                Assert.DoesNotContain("](../", index);
            }
        }
    }
}
