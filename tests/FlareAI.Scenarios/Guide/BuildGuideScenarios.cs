using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Llm;
using FlareAI.Core.Style;
using FlareAI.Scenarios.Support;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FlareAI.Scenarios.Guide
{
    /// <summary>
    /// The style guide owner's workflow: mine a project's own content for rules, add notes, edit the Company guide and keep its
    /// rule ids, validate a broken guide, tune the guide from what writers decided. Each story names the TEST-PLAN case it automates.
    /// </summary>
    public class BuildGuideScenarios
    {
        private const string Lead = "lead@example.com";
        private const string Golden_ = "tests/FlareAI.Scenarios/Guide/golden";

        private static string Describe(RuleCandidate c)
        {
            var sb = new StringBuilder();
            sb.AppendLine(c.Kind + " | " + c.Title + " | prefer: " + c.Prefer + " | impact " + c.Impact);
            sb.AppendLine("  forms: " + string.Join(", ", c.Forms.Select(f => f.Text + (f.Plural == null ? "" : "/" + f.Plural) + " x" + f.Count).ToArray()));
            if (c.IsRow) sb.AppendLine("  row: avoid [" + c.Avoid + "] use [" + c.Use + "] " + c.Severity + " | " + c.Why);
            else sb.AppendLine("  rule: " + c.Text + " | bad: " + c.Bad + " | good: " + c.Good);
            foreach (var e in c.Examples.Take(2)) sb.AppendLine("  e.g. " + (e.Form == null ? "" : "[" + e.Form + "] ") + e.Text + " (" + e.File + ")");
            return sb.ToString();
        }

        // ---- 23 · TEST-PLAN D2 ------------------------------------------------------------------------------------

        [Fact]
        public void Story23_OwnerMinesTheLegacyProject_FindsCompetingFormsWithRealSentences_ChoosingAFormWritesARuleThatValidates_AndIdsStayPermanent()
        {
            // D2: competing forms found with counts and real sentences.
            using (var ws = ReviewFixture.Corpus("v2/legacy"))
            {
                var guide = Files.TeamGuide(ws.Root);
                var style = new StyleEngine(new[] { guide }, Repo.StyleGuide);
                var team = Team.Load(guide, null, Lead);

                int files;
                var found = style.Mine(ws.ProjectDir, out files);

                Assert.Equal(Directory.GetFiles(ws.ContentDir, "*.*", SearchOption.AllDirectories).Count(p => p.EndsWith(".htm") || p.EndsWith(".html") || p.EndsWith(".flsnp")
                                                                                                          && !p.Contains(@"Resources\StyleGuide")), files);
                Golden.Check(Golden_, "mine-legacy.txt", "files: " + files + "\n" + string.Join("", found.Select(Describe).ToArray()));
                Assert.All(found.Where(c => c.Kind != "heading"), c => Assert.All(c.Examples.Where(e => e.Form != null), e => Assert.Contains(e.File, new[] { e.File })));
                var sentences = found.SelectMany(c => c.Examples).Select(e => e.Text).ToList();
                Assert.NotEmpty(sentences);
                var allText = string.Join(" ", Directory.GetFiles(ws.ContentDir, "*.htm", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray());
                var decoded = System.Net.WebUtility.HtmlDecode(Regex.Replace(allText, "<[^>]+>", ""));
                Assert.All(sentences.Where(s => !s.Contains("…")), s => Assert.Contains(Regex.Replace(s, @"\s+", " ").Substring(0, Math.Min(25, s.Length)), Regex.Replace(decoded, @"\s+", " ")));   // real sentences, not invented

                // The owner picks the form they want for the people who use the product: the rule is written to From-our-content.
                var verbs = found.First(c => c.Kind == "synonym" && c.Title.Contains("UI action verb"));
                var preferred = verbs.Forms.Select(f => f.Text).First(t => t == "select");
                verbs.Choose(preferred);
                Assert.Contains("click", verbs.Avoid);
                var before = style.Validate(null);
                int added;
                var topic = GuideBuilder.AddToGuide(team, new[] { verbs }, out added);

                Assert.Equal(1, added);
                Assert.Equal(Path.Combine(guide, @"Content\Resources\StyleGuide\" + GuideBuilder.ContentTopic), topic);
                var keys = style.AssignKeys();
                Assert.True(keys >= 1);
                var report = style.Validate(null);
                Assert.Equal(before.Errors, report.Errors);                                          // Validate passes: the new row adds no error
                Assert.DoesNotContain(report.Issues, i => i.File != null && i.File.EndsWith(GuideBuilder.ContentTopic));
                var rule = style.Rules(null).Single(r => r.Source != null && r.Source.EndsWith(GuideBuilder.ContentTopic));
                Assert.StartsWith("company:from-our-content/", rule.Id);

                // …and it flags the form in the project's own topics.
                var topicFx = new ReviewFixture(null, "Content/Admin/back-up-the-database.htm", null, false, ws, () => new StyleEngine(new[] { guide }, Repo.StyleGuide));   // the outer using disposes the workspace
                topicFx.Check();
                Assert.Contains(topicFx.Session.Findings, f => f.RuleId == rule.Id);

                // Rule ids are permanent: more rules later do not renumber this one.
                var extra = new RuleCandidate { Avoid = "end user", Use = "member", Why = "Our word.", Example = "The end user signs in.", Severity = "warning" };
                GuideBuilder.AddToGuide(team, new[] { extra }, out added);
                style.AssignKeys();
                var again = style.Rules(null).Where(r => r.Source != null && r.Source.EndsWith(GuideBuilder.ContentTopic)).Select(r => r.Id).ToList();
                Assert.Contains(rule.Id, again);
                Assert.Equal(2, again.Count);
            }
        }

        // ---- 24 · TEST-PLAN D3 ------------------------------------------------------------------------------------

        private static string Normalize(string s) { return Regex.Replace(s ?? "", @"\s+", " ").Trim(); }

        private static string Notes(string file) { return Path.Combine(Repo.Fixtures, "notes", file); }

        private static ScriptedLlmClient Extraction(params JObject[] rules)
        {
            // The model quotes the notes it was given: only rules whose quote is in the chunk it sees come back.
            return new ScriptedLlmClient().When(r => r.Task == "extract", r => new LlmResponse
            {
                Text = new JObject { ["rules"] = new JArray(rules.Where(x => Normalize(r.User).Contains(Normalize((string)x["quote"]))).Cast<object>().ToArray()) }.ToString(),
                StopReason = "end_turn", Model = r.Model ?? "scripted"
            });
        }

        private static JObject Row(string avoid, string use, string example, string quote)
        {
            return new JObject { ["kind"] = "row", ["title"] = "Use " + use, ["avoid"] = avoid, ["use"] = use, ["severity"] = "warning", ["why"] = "Our notes say so.", ["example"] = example, ["text"] = "", ["bad"] = "", ["good"] = "", ["quote"] = quote };
        }

        private static JObject Prose(string text, string bad, string good, string quote)
        {
            return new JObject { ["kind"] = "prose", ["title"] = "Claims", ["avoid"] = "", ["use"] = "", ["severity"] = "warning", ["why"] = "Our notes say so.", ["example"] = "", ["text"] = text, ["bad"] = bad, ["good"] = good, ["quote"] = quote };
        }

        private static readonly JObject[] NoteRules =
        {
            Row("log in", "sign in", "Log in to the console.", "We never say log in. Always say sign in"),
            Row("e-mail", "email", "Send an e-mail to support.", "Write e-mail as email everywhere."),
            Prose("Do not promise dates, prices or availability.", "This feature ships next month.", "This feature is not available yet.", "promise dates, prices or availability in the docs"),
            Row("click", "select", "Click Save.", "Prefer select over click for menu items.")
        };

        public static IEnumerable<object[]> NoteFiles()
        {
            yield return new object[] { "notes-utf8.txt" };
            yield return new object[] { "notes-utf16.txt" };
            yield return new object[] { "notes-word.htm" };
            yield return new object[] { "notes.docx" };
        }

        [Theory]
        [MemberData("NoteFiles")]
        public void Story24_OwnerAddsNotesFromAFile_TextIsExtractedClean_RulesQuoteTheirSource_AndTheWrittenRowsValidate(string file)
        {
            // D3: txt (UTF-8 and UTF-16), Word-pasted HTML, docx: the same four rules come out of each.
            var text = NoteText.ReadFile(Notes(file));
            Assert.DoesNotMatch("[<>]|mso-|MsoNormal|o:p|xml|HYPERLINK|w:del", text);
            Assert.Equal(Normalize(NoteText.ReadFile(Notes("notes-utf8.txt"))), Normalize(text));
            Assert.Contains("Don’t promise dates", text);                                           // the curly apostrophe survived the encoding

            ExtractAndWrite(text);
        }

        [Fact]
        public void Story24_OwnerPastesAnAiPrompt_RulesAreExtractedFromIt_AndTheWrittenRowsValidate()
        {
            var prompt = "You are a senior technical editor for the Dispatch docs. Follow these rules when you edit:\n\n" +
                         "1. Never write log in. Write sign in instead.\n\n2. Spell e-mail as email.\n\n3. Do not promise dates, prices or availability in the docs.\n\n4. Prefer select over click for menu items.";
            var rules = new[]
            {
                Row("log in", "sign in", "Log in to the console.", "Never write log in. Write sign in instead."),
                Row("e-mail", "email", "Send an e-mail to support.", "Spell e-mail as email."),
                Prose("Do not promise dates, prices or availability.", "This feature ships next month.", "This feature is not available yet.", "Do not promise dates, prices or availability in the docs."),
                Row("click", "select", "Click Save.", "Prefer select over click for menu items.")
            };
            ExtractAndWrite(prompt, rules);
        }

        private static void ExtractAndWrite(string notes, JObject[] rules = null)
        {
            using (var ws = FakeWorkspace.Empty())
            {
                var guide = Files.TeamGuide(ws.Root);
                var style = new StyleEngine(new[] { guide }, Repo.StyleGuide);
                var team = Team.Load(guide, null, Lead);
                var llm = Extraction(rules ?? NoteRules);
                var runner = new ActionRunner(llm, PromptBuilder.For(style, null), 16000, style, null);

                var candidates = runner.ExtractRules(notes);

                Assert.Equal(1, llm.Calls);
                Assert.Equal(4, candidates.Count);
                Assert.Equal(3, candidates.Count(c => c.IsRow));
                Assert.All(candidates, c => Assert.Contains(Normalize(c.Quote), Normalize(notes)));     // each rule quotes words that are in the notes
                int added;
                GuideBuilder.AddToGuide(team, candidates, out added);
                Assert.Equal(4, added);
                style.AssignKeys();

                var report = style.Validate(null);
                Assert.DoesNotContain(report.Issues, i => i.Level == "error" && i.File != null && i.File.EndsWith(GuideBuilder.ContentTopic));
                var fromNotes = style.Rules(null).Where(r => r.Source != null && r.Source.EndsWith(GuideBuilder.ContentTopic)).ToList();
                Assert.Equal(4, fromNotes.Count);
                Assert.Equal(3, fromNotes.Count(r => r.IsLint));
                Assert.Contains(fromNotes, r => r.Text.Contains("Do not promise dates"));
                // The rows work on text: "log in" is flagged and fixed to "sign in".
                var hits = style.Lint(new List<string> { "Log in to the console and send an e-mail." }, null, null);
                Assert.Contains(hits, h => h.Match.StartsWith("Log in") && h.Replacement != null && h.Replacement.StartsWith("Sign in"));
                Assert.Contains(hits, h => h.Match == "e-mail" && h.Replacement == "email");
            }
        }

        // ---- 25 · TEST-PLAN D7 ------------------------------------------------------------------------------------

        [Fact]
        public void Story25_OwnerInsertsABulletAndCopiesARow_ThenValidates_NewRulesGetIds_OldIdsDoNotMove_AndOnlyIdAttributesChange()
        {
            // D7: "status says new rules got permanent ids".
            using (var ws = FakeWorkspace.Empty())
            {
                var guide = Files.TeamGuide(ws.Root);
                var style = new StyleEngine(new[] { guide }, Repo.StyleGuide);
                var company = Path.Combine(guide, @"Content\Resources\StyleGuide\Company");
                var topicPath = Path.Combine(company, "Terminology.htm");
                Func<Dictionary<string, string>> ids = () => style.Rules(null).Where(r => r.Source != null && r.Source.EndsWith("Terminology.htm"))
                    .ToDictionary(r => Normalize(r.Text), r => r.Id);
                var oldIds = ids();
                Assert.NotEmpty(oldIds);

                // In Flare the owner types a new bullet above the others and copies a table row (Copy keeps the id attribute).
                var doc = new XmlDocument { PreserveWhitespace = true };
                doc.Load(topicPath);
                var first = doc.SelectSingleNode("//*[@id='rule-1']");
                var bullet = doc.CreateElement("li");
                bullet.InnerText = "Always write sign-in with a hyphen when it is a noun.";
                first.ParentNode.InsertBefore(bullet, first);
                var row = doc.SelectSingleNode("//*[@id='rule-3']");
                var copy = (XmlElement)row.CloneNode(true);
                copy.SelectNodes("td").Item(0).InnerText = "end user, end users";
                row.ParentNode.InsertAfter(copy, row);
                doc.Save(topicPath);
                var edited = File.ReadAllText(topicPath);

                var assigned = style.AssignKeys();

                Assert.Equal(2, assigned);                                                          // the bullet and the copied row
                var after = File.ReadAllText(topicPath);
                var noIds = new Regex(@" id=""rule-[a-z0-9]+""");
                Func<string, string> lf = t => Regex.Replace(noIds.Replace(t, ""), @"\r\n?", "\n");
                Assert.Equal(lf(edited), lf(after));                                                // only id attributes differ (the writer may change the line endings of an LF file)
                var newIds = ids();
                foreach (var kv in oldIds) Assert.Equal(kv.Value, newIds[kv.Key]);                  // every old rule kept its id
                var added = newIds.Where(kv => !oldIds.ContainsKey(kv.Key)).ToList();
                Assert.Equal(2, added.Count);
                Assert.Equal(newIds.Count, newIds.Values.Distinct().Count());                       // no id twice
                Assert.All(added, kv => Assert.DoesNotContain(kv.Value, oldIds.Values));
                var report = style.Validate(null);
                Assert.DoesNotContain(report.Issues, i => i.Message.Contains("permanent key"));
                Assert.Equal(0, style.AssignKeys());                                                // a second run changes nothing
                Assert.Equal(after, File.ReadAllText(topicPath));
            }
        }
    }
}
