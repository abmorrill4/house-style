using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FlareAI.Core.Style;
using Xunit;

namespace FlareAI.Core.Tests
{
    /// <summary>
    /// The saved copy of a team style guide (#246): what is copied (only what the guide loader reads, never links), when it is
    /// copied again, how a failed copy keeps the previous one, and where copies live.
    /// </summary>
    public class GuideCopyTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "flareai-copy-" + Guid.NewGuid().ToString("N"));
        private readonly GuideCopy _copies;

        public GuideCopyTests()
        {
            Directory.CreateDirectory(_dir);
            _copies = new GuideCopy(Path.Combine(_dir, @"appdata\" + GuideCopy.FolderName));
        }

        public void Dispose()
        {
            // Junctions first, so deleting the tree never reaches through them.
            foreach (var d in Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d); } catch (Exception) { }
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        internal static void Write(string root, string rel, string text)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        internal static string Topic(string avoid)
        {
            return "<html><body><h1>Words</h1><table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr>" +
                   "<tr><td>" + avoid + "</td><td>adjust</td><td>We " + avoid + " it.</td></tr></table></body></html>";
        }

        /// <summary>A Flare project guide with one rule, plus what a shared folder also holds.</summary>
        private string ProjectGuide(string name = "TeamGuide")
        {
            var root = Path.Combine(_dir, @"Share\" + name);
            Write(root, "Guide.flprj", "<CatapultProject />");
            Write(root, @"FlareAI\layer.txt", "company");
            Write(root, @"Content\Resources\StyleGuide\Company\Words.htm", Topic("frobnicate"));
            Write(root, @"Content\Resources\StyleGuide\overrides.ini", "google:word-list/221 = off\r\n");
            Write(root, @"Project\VariableSets\Product.flvar", "<CatapultVariableSet />");
            Write(root, @"Project\Glossaries\Terms.flglo", "<CatapultGlossary />");
            Write(root, @"Project\ConditionTagSets\Product.flcts", "<CatapultConditionTagSet />");
            return root;
        }

        private static bool Junction(string link, string target)
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            p.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(link);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private static IEnumerable<string> Files(string root)
        {
            return Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => f.Substring(root.Length + 1)).Where(f => f != GuideCopy.RecordName).OrderBy(f => f);
        }

        [Fact]
        public void Refresh_CopiesWhatTheLoaderReads_AndNothingElse()
        {
            var g = ProjectGuide();
            Write(g, @".git\config", "[core]");
            Write(g, @"FlareAI\owners.txt", "lead@example.com");
            Write(g, @"FlareAI\models.json", "{}");
            Write(g, @"FlareAI\other.txt", "not read by the loader");
            Write(g, @"Content\Resources\StyleGuide\tool.exe", "MZ");
            Write(g, @"Content\Resources\StyleGuide\Google\Words.htm", Topic("google"));
            Write(g, @"Content\Resources\StyleGuide\Company\.git\HEAD", "ref");
            Write(g, @"Content\Topics\Help.htm", Topic("elsewhere"));

            Assert.True(_copies.Refresh(g));
            var copy = _copies.Find(g);
            Assert.NotNull(copy);
            Assert.Equal(new[]
            {
                @"Content\Resources\StyleGuide\Company\Words.htm", @"Content\Resources\StyleGuide\overrides.ini", @"FlareAI\layer.txt",
                "Guide.flprj", @"Project\ConditionTagSets\Product.flcts", @"Project\Glossaries\Terms.flglo", @"Project\VariableSets\Product.flvar"
            }, Files(copy.Path).ToArray());
            Assert.Equal(GuideLayer.Load(g, null, null, "Google").Items.Count, GuideLayer.Load(copy.Path, null, null, "Google").Items.Count);
            Assert.Equal("company", GuideLayer.Load(copy.Path).Name);
        }

        [Fact]
        public void Refresh_PlainFolderGuide_CopiesTopicsFromTheRoot()
        {
            var g = Path.Combine(_dir, "Plain");
            Write(g, "Words.htm", Topic("frobnicate"));
            Write(g, @"More\Deeper\Other.htm", Topic("blorp"));
            Write(g, @"Google\Words.htm", Topic("google"));
            Write(g, @"FlareAI\layer.txt", "team");
            Write(g, @"FlareAI\Notes.htm", Topic("notes"));
            Write(g, "owners.txt", "lead");

            Assert.True(_copies.Refresh(g));
            var copy = _copies.Find(g).Path;
            Assert.Equal(new[] { @"FlareAI\layer.txt", @"More\Deeper\Other.htm", "Words.htm" }, Files(copy).ToArray());
            var source = GuideLayer.Load(g, null, null, "Google");
            var copied = GuideLayer.Load(copy, null, null, "Google");
            Assert.Equal(source.Items.Count - 1, copied.Items.Count);   // FlareAI\Notes.htm is not part of the copy
            Assert.Equal(2, copied.Items.Count);
        }

        [Fact]
        public void Refresh_SkipsLinks()
        {
            var g = ProjectGuide();
            var outside = Path.Combine(_dir, "Outside");
            Write(outside, "Secret.htm", Topic("secret"));
            if (!Junction(Path.Combine(g, @"Content\Resources\StyleGuide\Linked"), outside)) return;   // no junctions here
            Assert.True(_copies.Refresh(g));
            Assert.DoesNotContain(Files(_copies.Find(g).Path), f => f.Contains("Secret"));
        }

        [Fact]
        public void Refresh_StyleGuideFolderIsAJunction_IsNotFollowed()
        {
            var g = Path.Combine(_dir, @"Share\Linked");
            Write(g, "Guide.flprj", "<CatapultProject />");
            var outside = Path.Combine(_dir, "Outside");
            Write(outside, "Secret.htm", Topic("secret"));
            Directory.CreateDirectory(Path.Combine(g, @"Content\Resources"));
            if (!Junction(Path.Combine(g, @"Content\Resources\StyleGuide"), outside)) return;
            Assert.True(_copies.Refresh(g));
            Assert.Equal(new[] { "Guide.flprj" }, Files(_copies.Find(g).Path).ToArray());
        }

        [Fact]
        public void Refresh_SymlinkedFile_IsSkipped()
        {
            var g = ProjectGuide();
            var outside = Path.Combine(_dir, @"Outside\Secret.htm");
            Write(Path.GetDirectoryName(outside), "Secret.htm", Topic("secret"));
            var link = Path.Combine(g, @"Content\Resources\StyleGuide\Company\Secret.htm");
            if (!CreateSymbolicLink(link, outside, 2 /* allow unprivileged */) || !File.Exists(link)) return;   // symlinks need Developer Mode or admin
            Assert.True(_copies.Refresh(g));
            Assert.DoesNotContain(Files(_copies.Find(g).Path), f => f.Contains("Secret"));
        }

        [Fact]
        public void IsLinkTag_OnlyNameSurrogatesAreLinks()
        {
            Assert.True(GuideCopy.IsLinkTag(0xA000000C));    // IO_REPARSE_TAG_SYMLINK
            Assert.True(GuideCopy.IsLinkTag(0xA0000003));    // IO_REPARSE_TAG_MOUNT_POINT (junction)
            Assert.False(GuideCopy.IsLinkTag(0x9000001A));   // IO_REPARSE_TAG_CLOUD (OneDrive)
            Assert.False(GuideCopy.IsLinkTag(0x9000601A));   // IO_REPARSE_TAG_CLOUD_6
        }

        [Fact]
        public void Refresh_NormalFileInASyncedFolder_IsCopied()
        {
            var g = Path.Combine(_dir, @"OneDrive - Contoso\Docs\TeamGuide");
            Write(g, "Guide.flprj", "<CatapultProject />");
            Write(g, @"Content\Resources\StyleGuide\Words.htm", Topic("frobnicate"));
            Assert.True(_copies.Refresh(g));
            Assert.Contains(@"Content\Resources\StyleGuide\Words.htm", Files(_copies.Find(g).Path));
        }

        [Fact]
        public void Refresh_SameStamp_DoesNotCopyAgain()
        {
            var g = ProjectGuide();
            Assert.True(_copies.Refresh(g));
            var first = _copies.Find(g).CopiedUtc;
            Assert.False(_copies.Refresh(g));
            Assert.Equal(first, _copies.Find(g).CopiedUtc);
        }

        [Fact]
        public void Refresh_LayerTxtChanged_CopiesAgain()
        {
            var g = ProjectGuide();
            Assert.True(_copies.Refresh(g));
            File.WriteAllText(Path.Combine(g, @"FlareAI\layer.txt"), "acme");
            File.SetLastWriteTimeUtc(Path.Combine(g, @"FlareAI\layer.txt"), DateTime.UtcNow.AddMinutes(1));
            Assert.True(_copies.Refresh(g));
            Assert.Equal("acme", File.ReadAllText(Path.Combine(_copies.Find(g).Path, @"FlareAI\layer.txt")));
        }

        [Fact]
        public void Refresh_OverTheLimit_KeepsThePreviousCopy()
        {
            var g = ProjectGuide();
            Assert.True(_copies.Refresh(g));
            var before = _copies.Find(g);
            using (var big = new FileStream(Path.Combine(g, @"Content\Resources\StyleGuide\Big.txt"), FileMode.Create)) big.SetLength(GuideCopy.MaxBytes + 1);
            Assert.False(_copies.Refresh(g));
            var after = _copies.Find(g);
            Assert.Equal(before.CopiedUtc, after.CopiedUtc);
            Assert.DoesNotContain(Files(after.Path), f => f.Contains("Big"));
            Assert.Null(GuideCopy.Stamp(g));
        }

        [Fact]
        public void Refresh_FailureMidway_KeepsThePreviousCopy()
        {
            var g = ProjectGuide();
            Assert.True(_copies.Refresh(g));
            var before = _copies.Find(g);
            var locked = Path.Combine(g, @"Content\Resources\StyleGuide\Company\Words.htm");
            File.WriteAllText(locked, Topic("changed"));
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(_copies.Refresh(g));
            var after = _copies.Find(g);
            Assert.Equal(before.CopiedUtc, after.CopiedUtc);
            Assert.Contains("frobnicate", File.ReadAllText(Path.Combine(after.Path, @"Content\Resources\StyleGuide\Company\Words.htm")));
            Assert.Empty(Directory.GetDirectories(_copies.Folder, "*.tmp-*"));
        }

        [Fact]
        public void Refresh_StrayTempFolders_AreRemoved()
        {
            var g = ProjectGuide();
            Assert.True(_copies.Refresh(g));
            var target = _copies.PathFor(g);
            Directory.CreateDirectory(target + ".tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target + ".old-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(Path.Combine(g, @"Content\Resources\StyleGuide\Company\More.htm"), Topic("more"));
            Assert.True(_copies.Refresh(g));
            Assert.Empty(Directory.GetDirectories(_copies.Folder, "*.tmp-*"));
            Assert.Empty(Directory.GetDirectories(_copies.Folder, "*.old-*"));
        }

        [Fact]
        public void Refresh_StoppedAfterSettingTheOldCopyAside_PutsItBack()
        {
            var g = ProjectGuide();
            Assert.True(_copies.Refresh(g));
            var target = _copies.PathFor(g);
            Directory.Move(target, target + ".old-" + Guid.NewGuid().ToString("N"));
            Assert.Null(_copies.Find(g));
            Assert.False(_copies.Refresh(g));   // the old copy is back and still current
            Assert.NotNull(_copies.Find(g));
        }

        [Fact]
        public void Refresh_GuideFileNamedLikeTheRecord_DoesNotReplaceIt()
        {
            var g = ProjectGuide();
            var hash = Path.GetFileName(_copies.PathFor(g));
            Write(g, @"Content\Resources\StyleGuide\" + hash + ".json", "{\"source\":\"\\\\\\\\evil\\\\share\",\"stamp\":\"x\",\"copiedUtc\":\"2001-01-01T00:00:00Z\"}");
            Write(g, hash + ".json", "{}");
            Write(g, GuideCopy.RecordName, "{\"copiedUtc\":\"2001-01-01T00:00:00Z\"}");
            Write(g, @"Content\Resources\StyleGuide\" + GuideCopy.RecordName, "{\"copiedUtc\":\"2001-01-01T00:00:00Z\"}");
            Assert.True(_copies.Refresh(g));
            var copy = _copies.Find(g);
            Assert.True(copy.CopiedUtc > DateTime.UtcNow.AddMinutes(-5));
            Assert.DoesNotContain("evil", File.ReadAllText(Path.Combine(_copies.PathFor(g), GuideCopy.RecordName)));
        }

        [Fact]
        public void PathFor_DependsOnlyOnTheConfiguredPath()
        {
            Assert.Equal(_copies.PathFor(@"X:\Guide"), _copies.PathFor(@"X:\Guide\"));
            Assert.Equal(_copies.PathFor(@"X:\Guide"), _copies.PathFor(@"x:\GUIDE"));
            Assert.NotEqual(_copies.PathFor(@"X:\Guide"), _copies.PathFor(@"X:\Other"));
            Assert.Equal(_copies.Folder, Path.GetDirectoryName(_copies.PathFor(@"X:\Guide")));
            Assert.Matches("^[0-9a-f]{16}$", Path.GetFileName(_copies.PathFor(@"X:\Guide")));
        }

        [Fact]
        public void IsCopy_IsTrueOnlyInsideACopy()
        {
            var g = ProjectGuide();
            Assert.True(_copies.Refresh(g));
            var copy = _copies.Find(g).Path;
            Assert.True(GuideCopy.IsCopy(copy));
            Assert.True(GuideCopy.IsCopy(Path.Combine(copy, @"Content\Resources\StyleGuide\overrides.ini")));
            Assert.False(GuideCopy.IsCopy(_copies.Folder));
            Assert.False(GuideCopy.IsCopy(g));
            Assert.False(GuideCopy.IsCopy(null));
        }

        [Fact]
        public void Prune_DeletesCopiesOfGuidesNoLongerConfigured()
        {
            var a = ProjectGuide("A");
            var b = ProjectGuide("B");
            Assert.True(_copies.Refresh(a));
            Assert.True(_copies.Refresh(b));
            _copies.Prune(new[] { a });
            Assert.NotNull(_copies.Find(a));
            Assert.Null(_copies.Find(b));
            Assert.False(Directory.Exists(_copies.PathFor(b)));

        }

        [Fact]
        public void Find_WithNoCopy_IsNull()
        {
            Assert.Null(_copies.Find(Path.Combine(_dir, "Nothing")));
            Assert.False(_copies.Refresh(Path.Combine(_dir, "Nothing")));
        }
    }

    /// <summary>How the style engine uses a saved copy when a team guide can't be reached (#246).</summary>
    public class GuideCopyEngineTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "flareai-copyeng-" + Guid.NewGuid().ToString("N"));
        private readonly GuideCopy _copies;

        public GuideCopyEngineTests()
        {
            Directory.CreateDirectory(_dir);
            _copies = new GuideCopy(Path.Combine(_dir, @"appdata\" + GuideCopy.FolderName));
        }

        public void Dispose()
        {
            foreach (var d in Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d); } catch (Exception) { }
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private string Guide(string name, string avoid)
        {
            var root = Path.Combine(_dir, @"Share\" + name);
            GuideCopyTests.Write(root, "Guide.flprj", "<CatapultProject />");
            GuideCopyTests.Write(root, @"FlareAI\layer.txt", "company");
            GuideCopyTests.Write(root, @"Content\Resources\StyleGuide\Company\" + name + ".htm", GuideCopyTests.Topic(avoid));
            return root;
        }

        private StyleEngine Engine(params string[] guides)
        {
            return new StyleEngine(guides, GuideSourceTests.Bundled)
            {
                Fallback = () => new[] { GuideSourceTests.Bundled },
                CopyOf = _copies.Find,
                GuideRead = g => _copies.Refresh(g)
            };
        }

        private static IList<LintHit> Lint(StyleEngine e, params string[] lines) { return e.Lint(lines, null, null); }

        private static void GoOffline(string guide) { Directory.Move(guide, guide + "-offline"); }

        [Fact]
        public void OfflineGuideWithACopy_LintsWithIt_AndTheNoticeNamesTheFolderAndDate()
        {
            var team = Guide("Team", "frobnicate");
            var e = Engine(team);
            Assert.Contains(Lint(e, "We frobnicate it."), h => h.Match == "frobnicate");   // online: GuideRead saves the copy
            Assert.NotNull(_copies.Find(team));

            GoOffline(team);
            var hits = Lint(e, "We frobnicate it.");
            var hit = hits.First(x => x.Match == "frobnicate");
            Assert.Equal("Our rules", e.SourceName(e.Compose(null), hit.Layer, hit.RuleId));
            var notice = e.Notice(null);
            Assert.Equal(string.Format(StyleEngine.CopyNotice, team, StyleEngine.CopyDate(_copies.Find(team).CopiedUtc, DateTime.Now)), notice);
            Assert.DoesNotContain(StyleEngine.UnreadableNotice, notice);
            // The starter rules don't stand in: the team's copy does.
            Assert.DoesNotContain(hits, x => e.SourceName(e.Compose(null), x.Layer, x.RuleId) == StyleEngine.StarterRules);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AGuideWithNoLayerTxt_HasTheSameRuleIdsOffline_AndItsTurnedOffRulesStayOff(bool project)
        {
            var team = Path.Combine(_dir, @"Share\TeamGuide");
            if (project) GuideCopyTests.Write(team, "Guide.flprj", "<CatapultProject />");
            var style = project ? @"Content\Resources\StyleGuide\" : "";
            GuideCopyTests.Write(team, style + "Words.htm", GuideCopyTests.Topic("frobnicate") + "");
            GuideCopyTests.Write(team, style + "More.htm", GuideCopyTests.Topic("blorp"));
            var e = Engine(team);
            var onlineIds = e.Rules(null).Where(r => r.Id != null && r.Layer != "google").Select(r => r.Id).OrderBy(x => x).ToList();
            Assert.NotEmpty(onlineIds);
            var blorp = Lint(e, "We blorp it.").First(h => h.Match == "blorp").RuleId;
            GuideOverrides.Set(team, blorp, "off", "Not for us.");
            Assert.DoesNotContain(Lint(e, "We blorp it."), h => h.Match == "blorp");   // online, and GuideRead saves the copy with the row

            GoOffline(team);
            Assert.StartsWith("House Style couldn't reach", e.Notice(null));
            var offlineIds = e.Rules(null).Where(r => r.Id != null && r.Layer != "google").Select(r => r.Id).OrderBy(x => x).ToList();
            Assert.Equal(onlineIds, offlineIds);
            Assert.DoesNotContain(Lint(e, "We blorp it."), h => h.Match == "blorp");   // the team's turned-off rule stays off
            Assert.Contains(Lint(e, "We frobnicate it."), h => h.Match == "frobnicate");
        }

        [Fact]
        public void AReachableButEmptyGuide_KeepsTheLastGoodCopy()
        {
            var team = Guide("Team", "frobnicate");
            var e = Engine(team);
            e.Notice(null);
            var saved = _copies.Find(team).CopiedUtc;
            File.Delete(Path.Combine(team, @"Content\Resources\StyleGuide\Company\Team.htm"));
            Assert.StartsWith(StyleEngine.EmptyNotice, e.Notice(null));
            Assert.Equal(saved, _copies.Find(team).CopiedUtc);
            Assert.True(File.Exists(Path.Combine(_copies.Find(team).Path, @"Content\Resources\StyleGuide\Company\Team.htm")));
        }
        [Fact]
        public void Notice_OneGuideWithACopy_OneWithout_NamesEach()
        {
            var first = Guide("First", "frobnicate");
            var second = Guide("Second", "blorp");
            var e = Engine(first, second);
            Assert.True(_copies.Refresh(first));
            GoOffline(first);
            GoOffline(second);
            var notice = e.Notice(null);
            var copySentence = string.Format(StyleEngine.CopyNotice, first, StyleEngine.CopyDate(_copies.Find(first).CopiedUtc, DateTime.Now));
            Assert.StartsWith(copySentence + " " + StyleEngine.UnreadableNotice, notice);
            Assert.Contains("Can't reach " + second + ".", notice);
            Assert.DoesNotContain("Can't reach " + first, notice);
            var hits = Lint(e, "We frobnicate it.", "We blorp it.", "We utilize it.");
            Assert.Contains(hits, h => h.Match == "frobnicate");                      // the copy of the first
            Assert.DoesNotContain(hits, h => h.Match == "blorp");                      // no copy of the second
            Assert.Contains(hits, h => h.Match == "utilize");                          // the fallback stands in for it
        }

        [Fact]
        public void Notice_NeverNamesTheCopyFolder()
        {
            var team = Guide("Team", "frobnicate");
            var e = Engine(team);
            Assert.True(_copies.Refresh(team));
            GoOffline(team);
            var notice = e.Notice(null);
            Assert.Contains(team, notice);
            Assert.DoesNotContain(GuideCopy.FolderName, notice);
            Assert.DoesNotContain(_copies.Folder, notice);
        }

        [Fact]
        public void NoCopy_FallsBackAsBefore()
        {
            var team = Guide("Team", "frobnicate");
            var e = Engine(team);
            GoOffline(team);
            Assert.StartsWith(StyleEngine.UnreadableNotice, e.Notice(null));
            Assert.DoesNotContain(Lint(e, "We frobnicate it."), h => h.Match == "frobnicate");
        }

        [Fact]
        public void CopyDate_Today_ShowsTheTime()
        {
            var now = new DateTime(2026, 10, 27, 15, 0, 0, DateTimeKind.Local);
            var today = new DateTime(2026, 10, 27, 9, 30, 0, DateTimeKind.Local);
            var earlier = new DateTime(2026, 10, 20, 9, 30, 0, DateTimeKind.Local);
            Assert.Equal(today.ToString("d") + " " + today.ToString("t"), StyleEngine.CopyDate(today.ToUniversalTime(), now));
            Assert.Equal(earlier.ToString("d"), StyleEngine.CopyDate(earlier.ToUniversalTime(), now));
        }

        [Fact]
        public void GuideRead_NotCalledForTheBundledOrPersonalGuide()
        {
            var team = Guide("Team", "frobnicate");
            var mine = Path.Combine(_dir, "Mine");
            GuideCopyTests.Write(mine, @"FlareAI\personal.txt", "personal");
            var read = new List<string>();
            var e = new StyleEngine(new[] { GuideSourceTests.Bundled, team, mine }, GuideSourceTests.Bundled) { PersonalGuide = mine, GuideRead = read.Add };
            e.Notice(null);
            Assert.Equal(new[] { team }, read.ToArray());
        }

        [Fact]
        public void GuideRead_ProjectSwitch_DoesNotCopyAgain()
        {
            var team = Guide("Team", "frobnicate");
            var copies = 0;
            var e = new StyleEngine(new[] { team }, GuideSourceTests.Bundled) { GuideRead = g => { if (_copies.Refresh(g)) copies++; } };
            foreach (var name in new[] { "One", "Two" })
            {
                var project = Path.Combine(_dir, name);
                GuideCopyTests.Write(project, name + ".flprj", "<CatapultProject />");
                GuideCopyTests.Write(project, @"Content\Topic.htm", "<html><body><p>x</p></body></html>");
                e.Lint(new[] { "We frobnicate it." }, null, Path.Combine(project, @"Content\Topic.htm"));
            }
            Assert.Equal(1, copies);
        }

        [Fact]
        public void AFailingGuideRead_NeverFailsTheLint()
        {
            var team = Guide("Team", "frobnicate");
            var e = new StyleEngine(new[] { team }, GuideSourceTests.Bundled) { GuideRead = g => { throw new IOException("disk full"); } };
            Assert.Contains(Lint(e, "We frobnicate it."), h => h.Match == "frobnicate");
        }
    }
}
