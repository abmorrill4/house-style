using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// The UI consistency check (issues #6, #9 and #123). The Flare project can't be referenced from tests, so this scans
    /// the source text: every string literal in src/FlareAI.Flare and src/FlareAI.Core (comments, Diagnostics.Log calls and
    /// identifiers excluded) must be free of the terms the glossary retired (issue #6).
    /// A legitimate non-UI use (AI prompt text, internal keys, file names) goes in <see cref="Allowed"/> with a reason.
    /// "Lint" was retired once and is the word again since #123 (Flare and docs-linting vocabulary).
    /// </summary>
    public class UiVocabularyTests
    {
        private static readonly string[][] Banned =
        {
            new[] { "mechanical", @"\bmechanical" },
            new[] { "linter", @"\blinters?\b" },
            new[] { "layer", @"\blayers?\b" },
            new[] { "rule id", @"\brule ids?\b" },
            new[] { "XHTML", @"\bXHTML\b" },
            new[] { "proposal", @"\bpropos(?:al|als|e all|ed)\b" },
            new[] { "Re-analyze", @"\bre-?(?:analy[sz]e|check)\b" },
            new[] { "AI >", @"\bAI\s*>" },
            new[] { "FlareAI (as a name)", @"\bFlareAI(?![\\/.\-_A-Za-z0-9])" },
        };

        /// <summary>file-name regex, literal regex, reason. A literal matching both is skipped.</summary>
        private static readonly string[][] Allowed =
        {
            new[] { @"PromptBuilder\.cs$", ".*", "text sent to the AI model, not shown to writers" },
            new[] { @"Contracts\.cs$", ".*", "JSON schema descriptions sent to the AI model" },
            new[] { @"UiVocabularyTests\.cs$", ".*", "this file" },
            new[] { @"\.cs$", @"^(mechanical|layer|lint|proposed|FlareAI|FlareAI\\layer\.txt)$", "stored tokens, JSON keys and the FlareAI folder name inside a guide project; never shown" },
            new[] { @"StyleEngine\.cs$", @"^Rules for what this text contains|^Layer: |overrides the layers above", "guide text sent to the AI model" },
            new[] { @"ActionRunner\.cs$", @"no exact text proposed", "sent to the AI model" },
        };

        [Fact]
        public void UiStringsUseOnlyGlossaryTerms()
        {
            var root = RepoRoot();
            var violations = new List<string>();
            foreach (var dir in new[] { "src/FlareAI.Flare", "src/FlareAI.Core" })
                foreach (var file in Directory.GetFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                {
                    var rel = file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
                    foreach (var lit in Literals(File.ReadAllText(file)))
                    {
                        if (Allowed.Any(a => Regex.IsMatch(rel, a[0]) && Regex.IsMatch(lit.Text, a[1], RegexOptions.Singleline))) continue;
                        foreach (var b in Banned)
                            if (Regex.IsMatch(lit.Text, b[1], RegexOptions.IgnoreCase))
                                violations.Add(rel + ":" + lit.Line + "  [" + b[0] + "]  " + Shorten(lit.Text));
                    }
                }
            Assert.True(violations.Count == 0, "Banned UI terms (use the glossary, issue #6):\n" + string.Join("\n", violations.ToArray()));
        }

        // ---- the #123 vocabulary: Flare and docs-linting words, no "Claude-speak" ------------------------------

        /// <summary>
        /// Words #123 retired from the plugin's own UI: lint finds issues (not findings), Disable Rule (not turn off), and
        /// Accept/Reject belong to Flare's tracked changes. Arrows don't join text.
        /// </summary>
        private static readonly string[][] Retired =
        {
            new[] { "Check style", @"\bcheck style\b" },
            new[] { "finding", @"\bfindings?\b" },
            new[] { "Turn off", @"\bturn(?:ed|ing|s)? (?:\S+ )?off\b" },
            new[] { "arrow", @" → " },
            new[] { "Reject", @"\breject(?:ed|s|ing)?\b" },
        };

        /// <summary>file-name regex, literal regex, reason. A literal matching both is skipped.</summary>
        private static readonly string[][] RetiredAllowed =
        {
            new[] { @"PanelHost\.cs$", @"^FlareAI\.Findings$", "the pane's internal name in Flare's layout file, never shown" },
            new[] { @"UpdateFlow\.cs$|SettingsForm\.cs$", @"updates|update-policy", "automatic updates are turned off, not a rule" },
            new[] { @"\.cs$", @"tracked change|Review tab", "Accept and Reject of Flare's tracked changes" },
        };

        [Fact]
        public void UiStringsUseTheFlareLintingVocabulary()
        {
            var root = RepoRoot();
            var files = Directory.GetFiles(Path.Combine(root, "src/FlareAI.Flare/UI"), "*.cs", SearchOption.AllDirectories).ToList();
            files.Add(Path.Combine(root, "src/FlareAI.Flare/Commands.cs"));
            var violations = new List<string>();
            foreach (var file in files)
            {
                var rel = file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                foreach (var lit in Literals(File.ReadAllText(file)))
                {
                    if (RetiredAllowed.Any(a => Regex.IsMatch(rel, a[0]) && Regex.IsMatch(lit.Text, a[1], RegexOptions.Singleline | RegexOptions.IgnoreCase))) continue;
                    foreach (var b in Retired)
                        if (Regex.IsMatch(lit.Text, b[1], RegexOptions.IgnoreCase))
                            violations.Add(rel + ":" + lit.Line + "  [" + b[0] + "]  " + Shorten(lit.Text));
                }
            }
            Assert.True(violations.Count == 0, "Retired UI words (#123: lint, issues, Change, Ignore, Disable Rule):\n" + string.Join("\n", violations.ToArray()));
        }

        [Fact]
        public void TheScannerCatchesRetiredWords()
        {
            var lits = Literals("var a = \"Check style\"; var b = \"Turn off this rule\"; var c = \"x → y\"; var d = \"3 findings\"; var e = \"Reject\";").ToList();
            foreach (var l in lits) Assert.Contains(Retired, b => Regex.IsMatch(l.Text, b[1], RegexOptions.IgnoreCase));
            Assert.DoesNotContain(Retired, b => Regex.IsMatch("Disable Rule", b[1], RegexOptions.IgnoreCase));
            Assert.DoesNotContain(Retired, b => Regex.IsMatch("Accept them on the Review tab", b[1], RegexOptions.IgnoreCase));
        }

        /// <summary>The tooltips the product owner approved in #123, verbatim.</summary>
        [Fact]
        public void RibbonTooltipsAreTheApprovedOnes()
        {
            var expected = new Dictionary<string, string>
            {
                { "Lint Topic", "Lint this topic against your style guide." },
                { "Lint Project", "Lint every topic and snippet in this project." },
                { "Reopen Issues", "Reopen the issues from your last lint." },
                { "Rewrite", "Rewrite the selected text with AI." },
                { "Ask AI", "Tell the AI what to change in the selected text." },
                { "Rules", "View the style guide rules and enable or disable them." },
                { "Topic Metadata", "Suggest a description, keywords, and other metadata for this topic." },
                { "Settings", "Change your AI and style guide settings." },
                { "Help", "Open the House Style user guide." },
                { "Rule Decisions", "See how writers handle each rule, and review change requests." },
                { "Import Rules", "Add rules to your style guide from an existing document." },
                { "Test Rules", "Check that each rule catches its own examples." },
            };
            foreach (var e in expected)
            {
                var c = FlareAI.Flare.Commands.All.Single(x => x.Label == e.Key);
                Assert.Equal(e.Value, c.Tooltip);
            }
        }

        [Fact]
        public void TheRibbonFollowsTheRole()
        {
            var writer = FlareAI.Flare.Commands.Ribbon(FlareAI.Core.Settings.UserRole.Writer).Select(c => c.Label).ToArray();
            Assert.Equal(new[] { "Lint Topic", "Lint Project", "Reopen Issues", "Rules", "Rewrite", "Ask AI", "Topic Metadata", "Settings", "Help" }, writer);
            var owner = FlareAI.Flare.Commands.Ribbon(FlareAI.Core.Settings.UserRole.Owner);
            Assert.Equal(writer.Length + 3, owner.Count);
            var guide = owner.Where(c => c.Group == FlareAI.Flare.Commands.GroupGuide).Select(c => c.Label).ToArray();
            Assert.Equal(new[] { "Rule Decisions", "Import Rules", "Test Rules" }, guide);
            Assert.Equal("Style Guide", FlareAI.Flare.Commands.GroupGuide);
            // Export for LLMs and Diagnostics live in Settings; What's New, the tour and Report a Problem under Help.
            foreach (var role in new[] { FlareAI.Core.Settings.UserRole.Writer, FlareAI.Core.Settings.UserRole.Owner })
            {
                Assert.DoesNotContain(FlareAI.Flare.Commands.ExportLlm, FlareAI.Flare.Commands.Ribbon(role));
                Assert.DoesNotContain(FlareAI.Flare.Commands.Diagnostics, FlareAI.Flare.Commands.Ribbon(role));
            }
            Assert.Equal(new[] { FlareAI.Flare.Commands.WhatsNew, FlareAI.Flare.Commands.Tour, FlareAI.Flare.Commands.ReportProblem }, FlareAI.Flare.Commands.HelpMenu.ToArray());
            // A writer on a team guide gets My Requests after Rules, with the approved tooltip; an owner doesn't need it.
            var teamWriter = FlareAI.Flare.Commands.Ribbon(FlareAI.Core.Settings.UserRole.Writer, true).Select(c => c.Label).ToArray();
            Assert.Equal(new[] { "Lint Topic", "Lint Project", "Reopen Issues", "Rules", "My Requests", "Rewrite", "Ask AI", "Topic Metadata", "Settings", "Help" }, teamWriter);
            Assert.Equal("See your rule change requests and the owners' answers.", FlareAI.Flare.Commands.MyRequests.Tooltip);
            Assert.DoesNotContain(FlareAI.Flare.Commands.MyRequests, FlareAI.Flare.Commands.Ribbon(FlareAI.Core.Settings.UserRole.Owner, true));
            Assert.StartsWith("https://gethousestyle.com", FlareAI.Flare.Commands.UserGuideUrl);
        }

        [Fact]
        public void LintTopicMenu_IsRulesOnlyThenRulesAndAi()
        {
            var menu = FlareAI.Flare.Commands.LintMenu.ToArray();
            Assert.Equal(new[] { FlareAI.Flare.Commands.LintRulesOnly, FlareAI.Flare.Commands.LintRulesAndAi }, menu);
            Assert.Equal("Rules Only", FlareAI.Flare.Commands.LintRulesOnly.Label);
            Assert.Equal("Rules and AI Review", FlareAI.Flare.Commands.LintRulesAndAi.Label);
            Assert.Equal("rulesonly", FlareAI.Flare.Commands.LintRulesOnly.Id);
            Assert.Equal("rulesai", FlareAI.Flare.Commands.LintRulesAndAi.Id);
            Assert.Equal(FlareAI.Flare.Commands.GroupLint, FlareAI.Flare.Commands.LintRulesOnly.Group);
            Assert.Equal(FlareAI.Flare.Commands.GroupLint, FlareAI.Flare.Commands.LintRulesAndAi.Group);
            Assert.Equal("Lint this topic against your style guide rules only. No AI is used.", FlareAI.Flare.Commands.LintRulesOnly.Tooltip);
            Assert.Equal("Lint this topic against your style guide rules and the AI review.", FlareAI.Flare.Commands.LintRulesAndAi.Tooltip);
            Assert.Equal("Rules Only is on. Select the arrow to add the AI review.", FlareAI.Flare.Commands.RulesOnlyNote);
        }

        [Fact]
        public void SplitMenus_MapLintTopicAndHelp()
        {
            var map = FlareAI.Flare.Commands.SplitMenus;
            Assert.Equal(2, map.Count);
            Assert.Same(FlareAI.Flare.Commands.LintMenu, map[FlareAI.Flare.Commands.LintTopic]);
            Assert.Same(FlareAI.Flare.Commands.HelpMenu, map[FlareAI.Flare.Commands.Help]);
        }

        [Fact]
        public void ToolStripMenu_ListsTheChecksRightAfterLintTopic()
        {
            foreach (var role in new[] { FlareAI.Core.Settings.UserRole.Writer, FlareAI.Core.Settings.UserRole.Owner })
            {
                var menu = FlareAI.Flare.Commands.Menu(role);
                Assert.Same(FlareAI.Flare.Commands.LintTopic, menu[0]);
                Assert.Same(FlareAI.Flare.Commands.LintRulesOnly, menu[1]);
                Assert.Same(FlareAI.Flare.Commands.LintRulesAndAi, menu[2]);
            }
        }

        // ---- the command table (issue #9) ----------------------------------------------------------------------

        [Fact]
        public void EveryCommandHasOneLabelTooltipKeyTipAndIcon()
        {
            var all = FlareAI.Flare.Commands.All;
            Assert.Equal(all.Count, all.Select(c => c.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(all.Count, all.Select(c => c.KeyTip).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var c in all)
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Label), c.Id + " has no label");
                Assert.False(string.IsNullOrWhiteSpace(c.Tooltip), c.Id + " has no tooltip");
                Assert.False(string.IsNullOrWhiteSpace(c.KeyTip), c.Id + " has no key tip");
                Assert.False(string.IsNullOrWhiteSpace(c.Icon), c.Id + " has no icon");
                Assert.False(string.IsNullOrWhiteSpace(c.Group), c.Id + " has no group");
            }
            // Every menu, toolbar and right-click entry is a command from the table.
            var ownerMenu = FlareAI.Flare.Commands.Menu(FlareAI.Core.Settings.UserRole.Owner);
            foreach (var c in ownerMenu.Concat(FlareAI.Flare.Commands.Toolbar).Concat(FlareAI.Flare.Commands.Context))
                Assert.True(c == null || all.Contains(c));
            // Every command is reachable from the owner's Tool Strip menu, except the two that moved into Settings.
            foreach (var c in all)
                if (c != FlareAI.Flare.Commands.ExportLlm && c != FlareAI.Flare.Commands.Diagnostics && c != FlareAI.Flare.Commands.MyRequests) Assert.Contains(c, ownerMenu);
            // My Requests is for writers on a team guide (an owner has Rule Decisions, which holds the requests).
            Assert.Contains(FlareAI.Flare.Commands.MyRequests, FlareAI.Flare.Commands.Menu(FlareAI.Core.Settings.UserRole.Writer, true));
            Assert.DoesNotContain(FlareAI.Flare.Commands.MyRequests, FlareAI.Flare.Commands.Menu(FlareAI.Core.Settings.UserRole.Owner, true));
            Assert.DoesNotContain(FlareAI.Flare.Commands.RuleDecisions, FlareAI.Flare.Commands.Menu(FlareAI.Core.Settings.UserRole.Writer));
        }

        [Fact]
        public void CommandLabelsAreNotRetypedOutsideTheTable()
        {
            var root = RepoRoot();
            var labels = FlareAI.Flare.Commands.All.SelectMany(c => new[] { c.Label, c.ContextLabel }).ToList();
            var bad = new List<string>();
            foreach (var file in new[] { "src/FlareAI.Flare/Plugin.cs", "src/FlareAI.Flare/EditorIntegration.cs" })
                foreach (var lit in Literals(File.ReadAllText(Path.Combine(root, file))))
                    if (labels.Contains(lit.Text)) bad.Add(file + ":" + lit.Line + "  \"" + lit.Text + "\" (use Commands.*)");
            Assert.True(bad.Count == 0, string.Join("\n", bad.ToArray()));
        }

        [Fact]
        public void EveryCommandIsInTheUserGuide()
        {
            var guide = string.Join("\n", Directory.GetFiles(Path.Combine(RepoRoot(), "docs/user-guide/Content"), "*.htm").Select(File.ReadAllText).ToArray());
            var missing = FlareAI.Flare.Commands.All.Where(c => guide.IndexOf(c.Label.TrimEnd('…'), StringComparison.OrdinalIgnoreCase) < 0).Select(c => c.Label).ToList();
            Assert.True(missing.Count == 0, "Not in docs/user-guide/Content: " + string.Join(", ", missing.ToArray()));
        }

        [Fact]
        public void ScannerFindsLiteralsAndSkipsCommentsAndLogs()
        {
            var src = "// \"mechanical\"\nvar a = \"x lint y\";\nDiagnostics.Log(\"lint ok\", ex);\n/* \"layer\" */ var b = @\"c:\\q\"\"r\";\nvar c = '\"';\nvar d = $\"n {1} layer\";\n";
            var lits = Literals(src).ToList();
            Assert.Equal(new[] { "x lint y", "c:\\q\"r", "n {1} layer" }, lits.Select(l => l.Text).ToArray());
            Assert.Equal(2, lits[0].Line);
        }

        private static string Shorten(string s) { s = s.Replace("\r", " ").Replace("\n", " "); return s.Length > 90 ? s.Substring(0, 90) + "..." : s; }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "FlareAI.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private sealed class Literal { public string Text; public int Line; }

        /// <summary>String literals of a C# source file, outside comments and outside Diagnostics.Log statements.</summary>
        private static IEnumerable<Literal> Literals(string s)
        {
            var i = 0; var line = 1; var stmtStart = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\n') { line++; i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; }
                    i += 2; continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < s.Length && s[i] != '\'') { if (s[i] == '\\') i++; i++; }
                    i++; continue;
                }
                if (c == ';' || c == '{' || c == '}') { stmtStart = i + 1; i++; continue; }
                var verbatim = (c == '@' && i + 1 < s.Length && s[i + 1] == '"') || (c == '$' && i + 2 < s.Length && s[i + 1] == '@' && s[i + 2] == '"') || (c == '@' && i + 2 < s.Length && s[i + 1] == '$' && s[i + 2] == '"');
                var regular = c == '"' || (c == '$' && i + 1 < s.Length && s[i + 1] == '"');
                if (!verbatim && !regular) { i++; continue; }
                while (s[i] != '"') i++;
                i++;
                var startLine = line; var sb = new StringBuilder();
                while (i < s.Length)
                {
                    var ch = s[i];
                    if (verbatim && ch == '"' && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    if (ch == '"') break;
                    if (!verbatim && ch == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1] == 'n' ? '\n' : s[i + 1]); i += 2; continue; }
                    if (ch == '\n') line++;
                    sb.Append(ch); i++;
                }
                i++;
                var before = s.Substring(stmtStart, Math.Max(0, Math.Min(i, s.Length) - stmtStart));
                if (before.Contains("Diagnostics.Log(")) continue;
                yield return new Literal { Text = sb.ToString(), Line = startLine };
            }
        }
    }
}
