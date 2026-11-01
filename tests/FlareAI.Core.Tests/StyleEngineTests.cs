using System;
using System.IO;
using System.Linq;
using FlareAI.Core.Output;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>The in-process style guide and linter, against the style guide project shipped with the plugin.</summary>
    public class StyleEngineTests
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

        private static string Guide { get { return Path.Combine(Repo, "styleguide"); } }
        private static StyleEngine Engine() { return new StyleEngine(new[] { Guide }, Guide); }

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

        [Fact]
        public void CompanyTables_LintWithExactFixes_AndCitableIds()
        {
            var hits = Engine().Lint(new[] { "Configure Single Sign-On", "In order to be able to utilize SSO, simply ask the user. Please note that we whitelist it!",
                                             "It supports SAML 2.0 and OIDC.", "Tenants can open the admin panel." }, new[] { 0 }, null);
            Func<int, string, LintHit> by = (i, m) => hits.FirstOrDefault(h => h.Index == i && h.Match == m);
            Assert.Equal("single sign-on", by(0, "Single Sign-On").Replacement);
            Assert.Equal("To", by(1, "In order to").Replacement);
            Assert.Equal("company", by(1, "In order to").Layer);
            Assert.Equal("use", by(1, "utilize").Replacement);
            Assert.Equal("", by(1, "simply ").Replacement);
            Assert.Equal("W", by(1, "Please note that w").Replacement);           // deleting a sentence start re-capitalizes
            Assert.Equal("allowlist", by(1, "whitelist").Replacement);
            Assert.Null(by(1, "the user").Replacement);                           // flag only
            Assert.Equal("Organizations", by(3, "Tenants").Replacement);          // capitalized at a sentence start
            Assert.DoesNotContain(hits, h => h.Index == 2);
            Assert.StartsWith("company:terminology/", by(3, "Tenants").RuleId);
            Assert.EndsWith("Terminology.htm", by(3, "Tenants").Link);
            Assert.DoesNotContain(Engine().Lint(new[] { "Adds a note that appears in the history." }, null, null), h => h.Match.StartsWith("note that"));
        }

        [Fact]
        public void PlainLanguage_TheUser_IgnoresCompoundsLikeUserInterface()
        {
            var hits = Engine().Lint(new[] { "Open the user interface.", "The user experience improves.", "Open the user-defined list.",
                                             "Ask the user to sign in.", "Ask the end user to sign in." }, null, null);
            Assert.DoesNotContain(hits, h => h.Index <= 2 && h.RuleId == "company:plain-language/9");
            Assert.Contains(hits, h => h.Index == 3 && h.Match == "the user" && h.RuleId == "company:plain-language/9");
            Assert.Contains(hits, h => h.Index == 4 && h.Match == "the end user" && h.RuleId == "company:plain-language/9");
        }

        [Fact]
        public void BundledGuide_PassesItsOwnValidation_AndServesTheLayeredGuide()
        {
            var e = Engine();
            var report = e.Validate(null);
            Assert.True(report.Errors == 0, string.Join("\n", report.Issues.Where(i => i.Level == "error").Select(i => i.File + " " + i.Location + ": " + i.Message).ToArray()));
            Assert.True(report.TableRules >= 8);
            Assert.Equal(report.TableRules, report.TestedByExample);
            var guide = e.Guide(null);
            Assert.Contains("# Layer: google (baseline)", guide);
            Assert.Contains("[company:", guide);
            Assert.Contains("overrides the layers above", guide);
            var rules = e.Rules(null);
            Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
            Assert.Contains(rules, r => r.Id == "company:terminology/1" && r.Section == "Audience");
            Assert.NotEmpty(e.Examples(null));
        }

        [Fact]
        public void ProjectLayer_TablesExamplesOverridesVariablesGlossaryAndConditions()
        {
            var root = TempProject(r =>
            {
                Write(r, @"Project\VariableSets\General.flvar", "<CatapultVariableSet><Variable Name=\"ProductName\">Acme Online Banking</Variable><Variable Name=\"Empty\" /></CatapultVariableSet>");
                Write(r, @"Project\Glossaries\Main.flglo", "<CatapultGlossary><GlossaryEntry><Terms><Term>Zelle transfer</Term></Terms><Definition>A person-to-person payment.</Definition></GlossaryEntry></CatapultGlossary>");
                Write(r, @"Content\Resources\StyleGuide\Mobile.htm", @"<html xmlns:MadCap=""http://www.madcapsoftware.com/Schemas/MadCap.xsd""><body><h1>Mobile</h1>
                    <h2>Gestures</h2>
                    <ul><li>Say ""tap"", not ""click"", in mobile app topics.<ul><li>Bad: Click the button.</li><li>Good: Tap the button.</li></ul></li></ul>
                    <table><tr><th>Avoid</th><th>Use</th><th>Severity</th><th>Why</th><th>Example</th></tr>
                      <tr><td>click the button</td><td>tap the button</td><td>warning</td><td>Mobile.</td><td>Then click the button.</td></tr>
                      <tr><td>hit</td><td>(needs rewrite)</td><td>suggestion</td><td>Violent.</td><td>Hit Save.</td></tr>
                      <tr><td>swipe</td><td>swipe</td><td>bogus</td><td></td><td>Swipe left.</td></tr>
                      <tr MadCap:conditions=""Content.Headings""><td>How To</td><td>how to</td><td>warning</td><td>Sentence case.</td><td>How To Pay</td></tr>
                      <tr MadCap:conditions=""Product.Android""><td>tablet</td><td>device</td><td>warning</td><td></td><td>Use a tablet.</td></tr>
                      <tr><td>prior to</td><td>before</td><td>warning</td><td></td><td>Do it prior to noon.</td></tr>
                    </table>
                    <table><tr><th>Pattern</th><th>Use</th><th>Severity</th><th>Why</th><th>Example</th></tr>
                      <tr><td>\d+MB\b</td><td>(rewrite)</td><td>suggestion</td><td>Space before the unit.</td><td>Files up to 10MB.</td></tr></table>
                    <table><tr><th>Rule</th><th>Setting</th><th>Reason</th></tr><tr><td>Company.PlainLanguage1</td><td>off</td><td>Release notes.</td></tr><tr><td>google:nothing/9</td><td>off</td><td>Typo.</td></tr></table>
                    </body></html>");
                Write(r, @"Content\Topic.htm", "<html/>");
            });
            try
            {
                var topic = Path.Combine(root, @"Content\Topic.htm");
                var e = Engine();
                var hits = e.Lint(new[] { "Open Acme Online Banking and click the button.", "Send a Zelle Transfer now.", "How To Pay", "Learn How To pay.", "Up to 10MB of data.", "We utilize it." }, new[] { 2 }, topic);
                Assert.Contains(hits, h => h.Match == "Acme Online Banking" && h.Variable == "General.ProductName" && h.Layer == "project");
                Assert.Contains(hits, h => h.Match == "click the button" && h.Replacement == "tap the button" && h.RuleId == "project:mobile/2");
                Assert.Contains(hits, h => h.Match == "Zelle Transfer" && h.Replacement == "Zelle transfer");
                Assert.Contains(hits, h => h.Index == 2 && h.Match == "How To");          // heading-scoped row: on the heading…
                Assert.DoesNotContain(hits, h => h.Index == 3 && h.Match == "How To");    // …not in body text
                Assert.Contains(hits, h => h.Index == 4 && h.Match == "10MB" && h.Replacement == null);   // pattern row
                // override table turned the company rule off (Google's own utilize row, #64, may still flag it)
                Assert.DoesNotContain(hits, h => h.Index == 5 && h.Match == "utilize" && h.RuleIds.Any(id => id.StartsWith("company:")));
                Assert.DoesNotContain(e.Lint(new[] { "Open Acme Online Banking." }, null, null), h => h.Layer == "project");

                var rules = e.Rules(topic);
                Assert.Contains(rules, r => r.Id == "project:mobile/5" && r.Applies.Contains("Headings"));
                Assert.Contains(rules, r => r.Id == "project:mobile/6" && r.Products.Contains("Android"));
                Assert.Contains(rules, r => r.Id == "project:mobile/1" && r.Bad == "Click the button." && r.Good == "Tap the button.");
                var guide = e.Guide(topic);
                Assert.Contains("[project:mobile/1] Say \"tap\"", guide);
                Assert.Contains("General.ProductName", guide);
                Assert.Contains("(applies to: Headings)", guide);

                var msgs = e.Validate(topic).Issues.Where(i => i.Layer == "project").Select(i => i.Message).ToList();
                Assert.Contains(msgs, m => m.Contains("Avoid and Use are the same"));   // swipe → swipe
                Assert.Contains(msgs, m => m.Contains("Unknown severity"));
                Assert.Contains(msgs, m => m.StartsWith("Duplicates company:plain-language/"));   // same word, same fix as a company rule
                Assert.Contains(msgs, m => m.StartsWith("No rule 'google:nothing/9'"));
                Assert.Equal(new[] { "google", "structure", "company", "project" }, e.Compose(topic).Layers.Select(l => l.Name).ToArray());
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void GuideFor_SendsAlwaysOnRules_PlusOnlyThePacksTheTextNeeds_AndOnlyOurProducts()
        {
            var root = TempProject(r =>
            {
                Write(r, @"Content\Resources\StyleGuide\Google\Rules.htm", @"<html xmlns:MadCap=""http://www.madcapsoftware.com/Schemas/MadCap.xsd""><body><h1>Rules</h1>
                    <ul><li>Use active voice.</li>
                        <li MadCap:conditions=""Content.Steps"">Start each step with a verb.</li>
                        <li MadCap:conditions=""Content.Code"">Put code in code font.</li>
                        <li MadCap:conditions=""Product.Android"">Say tap, not click.</li>
                        <li MadCap:conditions=""Product.Payments"">Say payee, not recipient.</li></ul>
                    <table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr MadCap:conditions=""Product.Android""><td>click</td><td>tap</td><td>Click OK.</td></tr></table>
                    <table><tr><th>Term</th><th>Rule</th><th>Bad</th><th>Good</th></tr><tr><td>admin</td><td>Write out administrator unless admin is a UI label.</td><td>Ask your admin.</td><td>Ask your administrator.</td></tr></table>
                    </body></html>");
                Write(r, @"Project\ConditionTagSets\Product.flcts", "<CatapultConditionTagSet><ConditionTag Name=\"Payments\" /></CatapultConditionTagSet>");
            });
            try
            {
                var e = new StyleEngine(new[] { root }, root);
                var plain = e.GuideFor(null, GuideScope.Of(new string[0], "Save the file."));
                Assert.Contains("Use active voice.", plain[0]);
                Assert.DoesNotContain("Start each step", plain[0] + plain[1]);
                Assert.Contains("Say payee", plain[0]);              // our product: always on
                Assert.DoesNotContain("Say tap", plain[0] + plain[1]); // not our product: never loaded
                var steps = e.GuideFor(null, GuideScope.Of(new[] { "Steps" }, "Ask your admin to save it."));
                Assert.Contains("Start each step with a verb.", steps[1]);
                Assert.DoesNotContain("code font", steps[1]);
                Assert.DoesNotContain("Start each step", steps[0]);  // packs travel separately (their own cache block)
                Assert.DoesNotContain("Write out administrator", plain[0] + plain[1] + plain[2]);   // word-list rule: only when the text uses the word
                Assert.Contains("(admin) Write out administrator", steps[2]);   // word rules travel in their own, uncached block
                Assert.DoesNotContain(e.Lint(new[] { "Click OK." }, null, null), h => h.Match == "Click");   // product-gated lint row is off
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void ContentTags_DescribeWhatASectionContains()
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml("<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h2>Set up</h2><ol><li>Select <b>Save</b>.</li></ol>" +
                        "<p>Run <code>setup.exe</code> with the API key parameter. See <MadCap:xref href=\"x.htm\">Keys</MadCap:xref>. It takes 5 minutes.</p></body></html>");
            var tags = ContentTags.Of(doc.DocumentElement["body"].ChildNodes.Cast<System.Xml.XmlNode>().ToList());
            foreach (var t in new[] { "Headings", "Steps", "Lists", "UI", "Code", "API", "Links", "Numbers" }) Assert.Contains(t, tags);
            Assert.DoesNotContain("Tables", tags);
            Assert.DoesNotContain("Images", tags);
        }

        [Fact]
        public void Validate_RowWithOverlappingMatches_DoesNotCrash()
        {
            // Flare: "Could not validate: startIndex cannot be larger than length of string" (one row matched overlapping spans).
            var root = TempProject(r => Write(r, @"Content\Resources\StyleGuide\Words.htm",
                "<html><body><h1>Words</h1><table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr>" +
                "<tr><td>please note that, note that, please</td><td>(delete)</td><td>Please note that it works.</td></tr></table></body></html>"));
            try { Assert.DoesNotContain(new StyleEngine(new[] { root }, Guide).Validate(null).Issues, i => i.Message.Contains("doesn't resolve")); }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void RuleKeys_FreezeTodaysIds_ThenSurviveInsertsMovesAndCopies()
        {
            const string head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html><body><h1>Words</h1>\n";
            var root = TempProject(r => Write(r, @"Content\Resources\StyleGuide\Words.htm", head +
                "<ul><li>Say members.</li><li>Say sign in.</li></ul>\n<table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr><td>utilise</td><td>use</td><td>We utilise it.</td></tr></table>\n</body></html>"));
            var file = Path.Combine(root, @"Content\Resources\StyleGuide\Words.htm");
            try
            {
                Func<StyleEngine> engine = () => new StyleEngine(new[] { root }, Guide);
                Func<string, string> idOf = text => engine().Rules(null).First(x => x.Layer != "google" && x.Text.Contains(text)).Id;
                var before = new[] { idOf("Say members"), idOf("Say sign in"), idOf("utilise") };
                Assert.Equal(new[] { "words/1", "words/2", "words/3" }, before.Select(i => i.Substring(i.IndexOf(':') + 1)).ToArray());
                Assert.Contains(engine().Validate(null).Issues, i => i.Message.Contains("no permanent key"));

                // An owner validates: today's ids become permanent keys, nothing else in the topic changes.
                Assert.Equal(3, engine().AssignKeys());
                Assert.Equal(before, new[] { idOf("Say members"), idOf("Say sign in"), idOf("utilise") });
                Assert.Contains("<li id=\"rule-1\">Say members.</li>", File.ReadAllText(file));
                Assert.DoesNotContain(engine().Validate(null).Issues, i => i.Message.Contains("no permanent key"));

                // A new rule at the top, a copied bullet, rules reordered: the old ids stay put.
                var s = File.ReadAllText(file).Replace("<ul><li id=\"rule-1\">Say members.</li><li id=\"rule-2\">Say sign in.</li></ul>",
                    "<ul><li>Say select, not click.</li><li id=\"rule-2\">Say sign in.</li><li id=\"rule-1\">Say members.</li><li id=\"rule-1\">Say members (copy).</li></ul>");
                File.WriteAllText(file, s);
                Assert.Equal(before, new[] { idOf("Say members."), idOf("Say sign in"), idOf("utilise") });
                Assert.Contains("~", idOf("Say select"));                                       // provisional until an owner validates
                Assert.Contains(engine().Validate(null).Issues, i => i.Message.Contains("share the key"));
                Assert.Equal(2, engine().AssignKeys());                                          // the new rule and the copy
                var fresh = new[] { idOf("Say select"), idOf("Say members (copy)") };
                Assert.All(fresh, id => Assert.Matches(@"words/k[0-9a-z]{4}$", id));
                Assert.Equal(before, new[] { idOf("Say members."), idOf("Say sign in"), idOf("utilise") });
                Assert.Equal(0, engine().AssignKeys());                                          // stable: nothing left to do
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void OwnerEditsAndDeletesRules_InTheGuideTopic_KeepingTheirIds()
        {
            const string head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html><body><h1>Words</h1>\n";
            var root = TempProject(r =>
            {
                Write(r, @"Content\Resources\StyleGuide\Words.htm", head +
                    "<ul><li>Say <b>members</b>.<ul><li>Bad: Our users.</li></ul></li><li>Say sign in.</li><li>Spell out numbers.</li></ul>\n<p>Bad: There are 3 steps.</p>\n" +
                    "<table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr><td>utilise</td><td>use</td><td>We <code>utilise</code> it.</td></tr><tr><td>leverage</td><td>use</td><td>Leverage it.</td></tr></table>\n</body></html>");
                // Same file name in another folder: its rules get the same ids as Words.htm's.
                Write(r, @"Content\Resources\StyleGuide\Product\Words.htm", head + "<ul><li>Say workspace.</li></ul>\n</body></html>");
            });
            var file = Path.Combine(root, @"Content\Resources\StyleGuide\Words.htm");
            var other = Path.Combine(root, @"Content\Resources\StyleGuide\Product\Words.htm");
            try
            {
                Func<StyleEngine> engine = () => new StyleEngine(new[] { root }, Guide);
                Func<string, GuideRule> rule = text => engine().Rules(null).FirstOrDefault(x => x.Layer != "google" && x.Text.Contains(text));
                Func<GuideLayer> guide = () => engine().EditableGuide(root);
                var members = rule("Say members");
                var utilise = rule("utilise");
                var numbers = rule("Spell out numbers");

                // Only a guide in effect, never the bundled one.
                Assert.Null(engine().EditableGuide(Guide));
                Assert.Null(engine().EditableGuide(Path.GetTempPath()));

                // The rule is found in its own topic only, and only while it still reads the same.
                Assert.Null(guide().RuleFields(members.Id, other, members.Text));
                Assert.Null(guide().RuleFields(members.Id, file, "Say clients."));

                // A bullet: change only Good. The wording keeps its formatting, the Bad example stays. Same id.
                var fields = guide().RuleFields(members.Id, file, members.Text);
                Assert.Equal(new[] { "Rule", "Bad", "Good" }, fields.Select(f => f.Key).ToArray());
                Assert.Equal("Say members.", fields[0].Value);
                Assert.Equal("Our users.", fields[1].Value);
                Assert.EndsWith("Words.htm", guide().EditRule(members.Id, file, members.Text, new[] { Field("Rule", "Say members."), Field("Bad", "Our users."), Field("Good", "Our members.") }));
                Assert.Contains("<b>members</b>", File.ReadAllText(file));
                var edited = rule("Say members");
                Assert.Equal(members.Id, edited.Id);
                Assert.Equal("Our users.", edited.Bad);
                Assert.Equal("Our members.", edited.Good);
                Assert.Contains("Say workspace.", File.ReadAllText(other));   // the other Words.htm is untouched

                // New wording replaces the formatting of the wording only.
                guide().EditRule(members.Id, file, edited.Text, new[] { Field("Rule", "Say members, not users."), Field("Bad", "Our users."), Field("Good", "Our members.") });
                Assert.Equal(members.Id, rule("Say members, not users.").Id);
                Assert.Equal("Our members.", rule("Say members, not users.").Good);

                // An example written as a paragraph after the list belongs to the last bullet: Edit shows and changes it there.
                Assert.Equal("There are 3 steps.", guide().RuleFields(numbers.Id, file, numbers.Text)[1].Value);
                guide().EditRule(numbers.Id, file, numbers.Text, new[] { Field("Rule", numbers.Text), Field("Bad", "There are 4 steps."), Field("Good", "") });
                Assert.Equal("There are 4 steps.", rule("Spell out numbers").Bad);
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "Bad: There are").Count);

                // A table row: only the changed cell is rewritten; the Example keeps its code formatting.
                fields = guide().RuleFields(utilise.Id, file, utilise.Text);
                Assert.Equal(new[] { "Avoid", "Use", "Example" }, fields.Select(f => f.Key).ToArray());
                guide().EditRule(utilise.Id, file, utilise.Text, new[] { Field("Avoid", "utilise, utilize"), Field("Use", "use"), Field("Example", fields[2].Value) });
                Assert.Contains("<code>utilise</code>", File.ReadAllText(file));
                Assert.Contains(engine().Lint(new[] { "We utilize it." }, null, null), h => h.RuleId == utilise.Id);

                // Delete: the row and the bullets are gone (the last one with its example paragraph); the others keep their ids.
                var leverage = rule("leverage").Id;
                var signIn = rule("Say sign in").Id;
                guide().DeleteRule(utilise.Id, file, rule("utilise").Text);
                guide().DeleteRule(numbers.Id, file, rule("Spell out numbers").Text);
                Assert.Null(rule("utilise"));
                Assert.Null(rule("Spell out numbers"));
                Assert.DoesNotContain("There are 4 steps", File.ReadAllText(file));
                Assert.Equal(leverage, rule("leverage").Id);
                Assert.Equal(signIn, rule("Say sign in").Id);
                Assert.Equal("", rule("Say sign in").Bad);   // the deleted bullet's example didn't move to the one before it
                Assert.Null(guide().DeleteRule(numbers.Id, file, numbers.Text));   // already gone: nothing else is deleted
                var keep = rule("Say members, not users.");
                Assert.Throws<ArgumentException>(() => guide().EditRule(keep.Id, file, keep.Text, new[] { Field("Rule", " "), Field("Bad", "Our users."), Field("Good", "Our members.") }));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void EditRule_LeavesTheLevelToChangeLevel_AndRefusesARowTheGuideCantRead()
        {
            var root = TempProject(r => Write(r, @"Content\Resources\StyleGuide\Terms.htm",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html><body><h1>Terms</h1>\n<table><tr><td></td><td></td></tr>" +
                "<tr><th>Avoid</th><th>Use</th><th>Severity</th><th>Why</th><th>Example</th></tr><tr><td>utilise</td><td>use</td><td>Warning</td><td>Plain words.</td><td>We utilise it.</td></tr></table>\n" +
                "<table><tr><th>Pattern</th><th>Use</th><th>Example</th></tr><tr><td>\\bdon't\\b</td><td>do not</td><td>We don't.</td></tr></table>\n" +
                "<ul><li>Say members.<ul><li>Good: Our members.</li></ul></li></ul>\n</body></html>"));
            var file = Path.Combine(root, @"Content\Resources\StyleGuide\Terms.htm");
            try
            {
                Func<StyleEngine> engine = () => new StyleEngine(new[] { root }, Guide);
                Func<string, GuideRule> rule = text => engine().Rules(null).FirstOrDefault(x => x.Layer != "google" && x.Text.Contains(text));
                var guide = engine().EditableGuide(root);
                var utilise = rule("utilise");

                // The headings come from the first row with text; the level column isn't offered.
                var fields = guide.RuleFields(utilise.Id, file, utilise.Text);
                Assert.Equal(new[] { "Avoid", "Use", "Why", "Example" }, fields.Select(f => f.Key).ToArray());
                guide.EditRule(utilise.Id, file, utilise.Text, new[] { Field("Avoid", "utilise"), Field("Use", "use"), Field("Why", "Plain words help."), Field("Example", "We utilise it.") });
                Assert.Contains("<td>Warning</td>", File.ReadAllText(file));   // untouched, as written

                var row = rule("utilise");
                Assert.Throws<ArgumentException>(() => engine().EditableGuide(root).EditRule(row.Id, file, row.Text, new[] { Field("Avoid", ""), Field("Use", "use"), Field("Why", "x"), Field("Example", "y") }));
                var pattern = engine().Rules(null).First(x => x.Layer != "google" && x.Example == "We don't.");
                var pf = engine().EditableGuide(root).RuleFields(pattern.Id, file, pattern.Text);
                Assert.Equal("Pattern", pf[0].Key);
                Assert.Throws<ArgumentException>(() => engine().EditableGuide(root).EditRule(pattern.Id, file, pattern.Text, new[] { Field("Pattern", "(unclosed"), pf[1], pf[2] }));

                // Clearing a bullet's only example leaves no empty list behind.
                var members = rule("Say members");
                engine().EditableGuide(root).EditRule(members.Id, file, members.Text, new[] { Field("Rule", "Say members."), Field("Bad", ""), Field("Good", "") });
                Assert.Equal("", rule("Say members").Good);
                Assert.DoesNotContain("<ul />", File.ReadAllText(file));
                Assert.DoesNotContain("<ul></ul>", File.ReadAllText(file));
            }
            finally { Directory.Delete(root, true); }
        }

        private static System.Collections.Generic.KeyValuePair<string, string> Field(string k, string v) { return new System.Collections.Generic.KeyValuePair<string, string>(k, v); }

        [Fact]
        public void TurningOffAGoogleRuleForTheTeam_WritesTheGuide_AndStopsLintAndAi()
        {
            var root = TempProject(r => Write(r, @"Content\Resources\StyleGuide\Company\Changes.htm",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html><body><h1>Changes to the Google guide</h1>\n        <table>\n            <tr><th>Rule</th><th>Setting</th><th>Reason</th></tr>\n        </table>\n</body></html>"));
            try
            {
                var e = new StyleEngine(new[] { root }, Guide);
                var prose = e.Rules(null).First(r => r.Layer == "google" && !r.IsLint && r.Applies.Count == 0 && r.Triggers.Count == 0);
                var lint = e.Rules(null).First(r => r.Layer == "google" && r.IsLint && r.Products.Count == 0);
                Assert.Contains(prose.Text, e.GuideFor(null, new GuideScope())[0]);
                var file = GuideOverrides.Set(root, prose.Id, "off", "Not for our audience.");
                GuideOverrides.Set(root, lint.Id, "off", "Too noisy.");
                GuideOverrides.Set(root, prose.Id, "off", "Not for our audience, per review.");   // same rule again: row updated, not duplicated
                Assert.EndsWith("Changes.htm", file);
                var text = File.ReadAllText(file);
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(prose.Id)).Count);
                Assert.Contains("per review", text);
                var after = new StyleEngine(new[] { root }, Guide);
                Assert.DoesNotContain("[" + prose.Id + "]", after.GuideFor(null, new GuideScope())[0]);   // the AI no longer gets it
                Assert.DoesNotContain(prose.Id, after.RuleIdsFor(null, new GuideScope()));
                var example = after.Rules(null).Where(r => r.Id == lint.Id).Select(r => r.Text).First();
                Assert.DoesNotContain(after.Validate(null).Issues, i => i.Message.StartsWith("No rule"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void EditingTheGuide_IsPickedUpWithoutRestart()
        {
            var root = TempProject(r => Write(r, @"Content\Resources\StyleGuide\Words.htm",
                "<html><body><h1>Words</h1><table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr><td>wibble</td><td>wobble</td><td>A wibble.</td></tr></table></body></html>"));
            try
            {
                var e = new StyleEngine(new[] { root }, Guide);
                Assert.Contains(e.Lint(new[] { "A wibble and a zonk." }, null, null), h => h.Match == "wibble");
                System.Threading.Thread.Sleep(20);
                Write(root, @"Content\Resources\StyleGuide\Words.htm",
                    "<html><body><h1>Words</h1><table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr><td>zonk</td><td>plonk</td><td>A zonk.</td></tr></table></body></html>");
                var hits = e.Lint(new[] { "A wibble and a zonk." }, null, null);
                Assert.Contains(hits, h => h.Match == "zonk");
                Assert.DoesNotContain(hits, h => h.Match == "wibble");
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void AGuideClonedUnderAnotherFolderName_KeepsItsRuleIds()
        {
            var clone = Path.Combine(Path.GetTempPath(), "Some-Writers-Clone-" + Guid.NewGuid().ToString("N"));
            foreach (var f in Directory.GetFiles(Guide, "*", SearchOption.AllDirectories))
            {
                var target = clone + f.Substring(Guide.Length);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(f, target);
            }
            try { Assert.Contains(new StyleEngine(new[] { clone }, Guide).Rules(null), r => r.Id.StartsWith("company:")); }
            finally { Directory.Delete(clone, true); }
        }

        [Fact]
        public void Mining_FindsTheProjectsOwnConventions_WithVariablesReadAsValues()
        {
            var root = TempProject(r =>
            {
                Write(r, @"Project\VariableSets\General.flvar", "<CatapultVariableSet><Variable Name=\"Bank\">Acme Financial</Variable></CatapultVariableSet>");
                Write(r, @"Content\Alerts.htm", "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><body><h1>Set up alerts for your account</h1>" +
                    "<p>Turn on autopay for bills. Autopay runs monthly. Autopay stops when you cancel.</p>" +
                    "<p>At <MadCap:variable name=\"General.Bank\" /> you can also set up auto-pay here. Edit auto-pay later.</p>" +
                    "<p>To start, sign in to the app. Then sign in again and log in to the console. Sign in daily. Log in to see statements.</p>" +
                    "<p>Open the app and go to Online Banking now. From there, go to Online Banking settings and then online banking help, and online banking alerts.</p>" +
                    "<h2>Change Your Alert Settings</h2><h2>View Your Account History</h2><h2>Delete an alert from the list</h2><h2>Reset Your Online Password</h2></body></html>");
            });
            try
            {
                int files;
                var mined = Engine().Mine(Path.Combine(root, @"Content\Alerts.htm"), out files);
                Assert.Equal(1, files);
                var spelling = mined.First(c => c.Kind == "spelling" && c.Prefer.Contains("autopay"));
                Assert.Equal("autopay", spelling.Prefer);
                Assert.Equal("auto-pay", spelling.Avoid);
                Assert.StartsWith("At Acme Financial you", spelling.Example);
                // "log in" vs "sign in": the Google word list already enforces it, so it isn't proposed again.
                Assert.DoesNotContain(mined, c => c.Kind == "synonym" && c.Forms.Any(f => f.Text == "log in"));
                Assert.Contains(mined, c => c.Kind == "capitalization");
                Assert.Contains(mined, c => c.Kind == "heading" && c.Prefer == "title case");
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void LlmExport_TurnsBuiltHtmlIntoMarkdownAndLlmsTxt()
        {
            var output = Path.Combine(Path.GetTempPath(), "flareai-out-" + Guid.NewGuid().ToString("N"), "HTML5");
            Write(output, @"Skins\nav.htm", "<html><body>skin</body></html>");
            Write(output, @"Content\pay.htm", "<html><head><title>Pay a bill</title><script>var x=1; if (a<b) {}</script><meta charset=\"utf-8\"></head><body>" +
                "<nav class=\"sidenav\"><a href=\"other.htm\">Menu item</a></nav>" +
                "<div id=\"mc-main-content\"><h1>Pay a bill</h1><p>To pay a bill, select <b>Pay</b> in Digital Banking.<br>Then confirm.</p>" +
                "<ol><li>Choose the payee.</li><li>Enter the amount. See <a href=\"limits.htm#daily\">Payment limits</a>.</li></ol>" +
                "<p><img src=\"x.png\"> <img src=\"y.png\" alt=\"The payee list\"></p>" +
                "<table><tr><th>Limit</th><th>Amount</th></tr><tr><td>Daily</td><td>$5,000</td></tr></table></div>" +
                "<footer>Copyright</footer></body></html>");
            try
            {
                var res = LlmExport.Export(output, null, "https://docs.example.com");
                var md = File.ReadAllText(Path.Combine(output, @"llm\Content\pay.md"));
                Assert.Equal(1, res.Topics);
                Assert.Contains("title: Pay a bill", md);
                Assert.Contains("url: https://docs.example.com/Content/pay.htm", md);
                Assert.Contains("# Pay a bill", md);
                Assert.Contains("select **Pay** in Digital Banking", md);
                Assert.Contains("1. Choose the payee.", md);
                Assert.Contains("2. Enter the amount. See [Payment limits](limits.md)", md);
                Assert.Contains("![The payee list](y.png)", md);
                Assert.Contains("| Limit | Amount |", md);
                Assert.Contains("| Daily | $5,000 |", md);
                Assert.DoesNotContain("Menu item", md);
                Assert.DoesNotContain("Copyright", md);
                Assert.DoesNotContain("var x", md);
                Assert.Contains("- [Pay a bill](llm/Content/pay.md)", File.ReadAllText(Path.Combine(output, "llms.txt")));
                Assert.Contains("Pay a bill", File.ReadAllText(Path.Combine(output, "llms-full.txt")));
            }
            finally { Directory.Delete(Path.GetDirectoryName(output), true); }
        }

        [Fact]
        public void Settings_GuideRoots_DefaultToTheBundledGuide()
        {
            var s = new FlareAI.Core.Settings.PluginSettings();
            var mine = @"C:\no-such-user\FlareAI\StyleGuide";
            Assert.Equal(new[] { @"C:\p\StyleGuide" }, s.GuideRoots(@"C:\p", mine).ToArray());
            Assert.Equal(mine, s.GuideRoot(@"C:\p", mine));                       // writes never go to the plugin folder
            s.StyleLayers = "\\\\share\\style\r\n\r\n{PluginDir}\\team\\";
            Assert.Equal(new[] { @"\\share\style", @"C:\p\team" }, s.GuideRoots(@"C:\p", mine).ToArray());
            Assert.Equal(mine, s.GuideRoot(@"C:\p", mine));                       // a guide shipped in the plugin folder is read-only too
            s.StyleLayers = @"C:\p\team" + "\r\n" + @"\\share\style";
            Assert.Equal(@"\\share\style", s.GuideRoot(@"C:\p", mine));
            Assert.Null(s.ReadOnlyGuide(@"C:\p"));
        }
    }
}
