using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using FlareAI.Core.Actions;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Guide
{
    /// <summary>
    /// A writer with no team guide: the bundled guide is installed read-only in the plugin folder, their own guide lives in
    /// %APPDATA%\FlareAI\StyleGuide (redirected by the workspace) and holds only their changes. Plugin updates to the
    /// bundled guide reach them, including writers whose guide is a full copy made by an earlier version.
    /// </summary>
    public class PersonalGuideScenarios
    {
        private const string ThisPage = "google:word-list/221";
        private const string Topic = "Content/help.htm";

        private sealed class Install
        {
            public string PluginDir, Bundled, Mine;
            public PluginSettings Settings = new PluginSettings();
            public StyleEngine Engine;

            public Team Team()
            {
                var t = FlareAI.Core.Actions.Team.Load(Settings.GuideRoot(PluginDir), null, "pat@example.com");
                t.SeedFrom = Settings.ReadOnlyGuide(PluginDir);
                return t;
            }

            public List<RuleOverride> TurnedOffView()
            {
                return RuleOverrides.InEffect(Settings.GuideRoots(PluginDir), r => PluginSettings.IsUnder(r, PluginDir), r => "My style guide");
            }
        }

        /// <summary>The plugin as installed: the bundled guide in a folder the writer can't change.</summary>
        private static Install Installed(FakeWorkspace ws, Action<string> beforeReadOnly = null)
        {
            var i = new Install { PluginDir = Path.Combine(ws.Root, @"Plugins\FlareAI") };
            i.Bundled = PluginSettings.BundledGuide(i.PluginDir);
            foreach (var f in Directory.GetFiles(Repo.StyleGuide, "*", SearchOption.AllDirectories))
            {
                var to = i.Bundled + f.Substring(Repo.StyleGuide.Length);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(f, to);
            }
            i.Mine = PluginSettings.UserGuidePath;
            Assert.StartsWith(ws.AppData, i.Mine);
            if (beforeReadOnly != null) beforeReadOnly(i.Bundled);
            ws.MakeReadOnly(i.PluginDir);
            i.Engine = new StyleEngine(() => i.Settings.GuideRoots(i.PluginDir), i.Bundled);
            return i;
        }

        private static List<StyleFinding> Check(FakeWorkspace ws, Install i)
        {
            var doc = ws.Open(Topic);
            var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(i.Engine, doc.SourcePath), 16000, i.Engine, doc.SourcePath) { VariableValue = doc.VariableValue };
            return runner.CheckStyle(doc);
        }

        private static void WriteTopic(FakeWorkspace ws)
        {
            ws.WriteFile(Topic, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns:MadCap=\"http://www.madcapsoftware.com/Schemas/MadCap.xsd\"><head><title>Help</title></head>" +
                                "<body><h1>Help</h1><p>Read this page for details.</p></body></html>");
        }

        [Fact]
        public void AWriterWithAnOldFullCopy_GetsTheNewBundledDefault_AndKeepsTheirOwnChange()
        {
            using (var ws = FakeWorkspace.Empty())
            {
                WriteTopic(ws);
                // Given the guide an earlier version copied to %APPDATA% (before the bundled defaults, such as "this page" off), where the
                // writer turned a rule off…
                var i = Installed(ws);
                var mine = i.Mine;
                foreach (var f in Directory.GetFiles(i.Bundled, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(i.Bundled.Length + 1);
                    if (rel.StartsWith(@"Content\Resources\StyleGuide\Google\", StringComparison.OrdinalIgnoreCase)) continue;
                    var to = Path.Combine(mine, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(f, to);
                    File.SetAttributes(to, FileAttributes.Normal);
                }
                var copiedOverrides = Path.Combine(mine, @"Content\Resources\StyleGuide\Company\Google-overrides.htm");
                var x = new XmlDocument { PreserveWhitespace = true };
                x.Load(copiedOverrides);
                foreach (XmlNode row in x.SelectNodes("//*[local-name()='tr'][*[local-name()='td']]")) row.ParentNode.RemoveChild(row);   // no defaults yet
                x.Save(copiedOverrides);
                GuideOverrides.Set(mine, ThisPage, "off", "We say utilize.", "pat@example.com", new DateTime(2026, 9, 1));

                // …when they check a topic after the plugin update
                var findings = Check(ws, i);

                // Then "this page" is not flagged, their change still applies, and their copy now holds only that change.
                Assert.DoesNotContain(findings, f => f.RuleId == ThisPage);
                Assert.True(i.Engine.IsOff(null, ThisPage));
                Assert.True(GuideSeed.IsPersonal(mine));
                Assert.False(File.Exists(copiedOverrides));
                Assert.False(File.Exists(Path.Combine(mine, @"Content\Resources\StyleGuide\Company\Terminology.htm")));
                var rows = RuleOverrides.Read(mine, "My style guide");
                Assert.Equal(ThisPage, rows.Single().RuleId);
                Assert.Equal("pat@example.com", rows.Single().By);
                var backup = Directory.GetDirectories(ws.AppData, "StyleGuide.backup-*").Single();
                Assert.True(File.Exists(Path.Combine(backup, @"Content\Resources\StyleGuide\Company\Google-overrides.htm")));
                var log = File.ReadAllText(ws.DiagLog);
                Assert.Contains("Upgraded your style guide", log);
                Assert.Contains(ThisPage + "=off", log);
            }
        }

        [Fact]
        public void AWriterTurnsADefaultBackOn_FromTheTurnedOffList_AndItIsFlaggedAgain()
        {
            using (var ws = FakeWorkspace.Empty())
            {
                WriteTopic(ws);
                var i = Installed(ws);
                Assert.DoesNotContain(Check(ws, i), f => f.RuleId == ThisPage);

                var team = i.Team();
                var def = i.TurnedOffView().Single(o => o.RuleId == ThisPage);
                Assert.True(def.IsDefault);
                Assert.Equal("Default", def.Scope);
                RuleOverrides.TurnBackOn(def, team);

                Assert.Equal(new[] { @"Content\Resources\StyleGuide\" + GuideSeed.PersonalTopic, @"FlareAI\layer.txt", @"FlareAI\personal.txt", "StyleGuide.flprj" },
                             Directory.GetFiles(i.Mine, "*", SearchOption.AllDirectories).Select(f => f.Substring(i.Mine.Length + 1)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray());
                Assert.Contains(Check(ws, i), f => f.RuleId == ThisPage);
                var on = i.TurnedOffView().Single(o => o.RuleId == ThisPage);
                Assert.True(on.IsTurnOn);
                Assert.Equal("My style guide", on.Scope);
            }
        }

        [Fact]
        public void AWriterAddsRulesWithBuildStyleGuide_AndTheBundledDefaultsStillApply()
        {
            using (var ws = FakeWorkspace.Empty())
            {
                WriteTopic(ws);
                var i = Installed(ws);
                int added;
                var topic = GuideBuilder.AddToGuide(i.Team(), new List<RuleCandidate> { new RuleCandidate { Avoid = "for details", Use = "to learn more", Why = "Plain.", Example = "Read this for details." } }, out added);

                Assert.Equal(Path.Combine(i.Mine, @"Content\Resources\StyleGuide\" + GuideBuilder.ContentTopic), topic);
                var findings = Check(ws, i);
                Assert.Contains(findings, f => f.RuleId != null && f.RuleId.StartsWith("company:from-our-content/"));
                Assert.DoesNotContain(findings, f => f.RuleId == ThisPage);
            }
        }
    }
}
