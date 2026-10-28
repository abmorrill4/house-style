using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using FlareAI.TestKit.Llm;
using FlareAI.TestKit.Workspace;
using Xunit;

namespace FlareAI.Scenarios.Governance
{
    /// <summary>
    /// Style guide governance (#84, #85, #86): a shared team guide whose folder is read-only for writers. Every change a
    /// writer makes becomes a request (never an error), owners approve or decline it, the writer sees the outcome on their
    /// next check, approved project exceptions land in the writer's own project, and requests survive a restart.
    /// </summary>
    [Collection("Core statics")]
    public class GuideRequestScenarios
    {
        private const string Lead = "lead@example.com", Backup = "backup@example.com", Pat = "pat@example.com";
        private const string Topic = "Content/transfers/about.htm";

        /// <summary>A corpus project, a team guide (a copy of the bundled Company rules) and a shared feedback folder.</summary>
        private sealed class Fixture : IDisposable
        {
            public readonly FakeWorkspace Ws = FakeWorkspace.FromCorpus("DigitalBanking");
            public readonly string Guide, Feedback;
            public readonly ScenarioHost Host = new ScenarioHost();

            public Fixture(bool ownersFile = true)
            {
                Guide = Path.Combine(Ws.Root, "TeamGuide");
                Feedback = Path.Combine(Ws.Root, "Feedback");
                Directory.CreateDirectory(Feedback);
                Copy(Path.Combine(Repo.StyleGuide, @"Content\Resources\StyleGuide\Company"), Path.Combine(Guide, "Company"));
                Directory.CreateDirectory(Path.Combine(Guide, "FlareAI"));
                if (ownersFile) File.WriteAllText(Path.Combine(Guide, @"FlareAI\owners.txt"), "# style guide owners\n" + Lead + "\n" + Backup + "  # backup\n");
            }

            public Team As(string who) { return Team.Load(Guide, Feedback, who); }

            public StyleEngine Style() { return new StyleEngine(new[] { Guide }, Repo.StyleGuide); }

            public GuideRule GoogleRule(StyleEngine style) { return style.Rules(null).First(r => r.Layer == "google" && r.IsLint); }

            /// <summary>A prose rule of the team's own guide (its layer is named after the guide folder).</summary>
            public GuideRule ProseRule(StyleEngine style) { return style.Rules(null).First(r => r.Layer != "google" && r.Text.StartsWith("Do not promise timelines")); }

            /// <summary>The writer's review pane over the open topic, with <paramref name="rule"/> flagged in it.</summary>
            public ReviewSession Session(Team team, StyleEngine style, GuideRule rule, out StyleFinding finding)
            {
                var doc = Ws.Open(Topic);
                var runner = new ActionRunner(ScriptedLlmClient.NoFindings(), PromptBuilder.For(style, doc.SourcePath), 16000, style, doc.SourcePath);
                var s = new ReviewSession(runner, doc, Host, null, true) { CurrentTeam = () => team };
                finding = new StyleFinding
                {
                    Source = rule.IsLint ? "lint" : "ai", Rule = rule.IsLint ? "Google.Rule" : rule.Section, RuleId = rule.Id, Layer = rule.Layer,
                    Severity = "warning", Excerpt = "utilize", Replacement = "use", DocumentPath = doc.SourcePath
                };
                s.Add(new[] { finding });
                s.Current = finding;
                return s;
            }

            public string[] RequestFiles() { return Directory.Exists(Path.Combine(Feedback, "requests")) ? Directory.GetFiles(Path.Combine(Feedback, "requests"), "r-*.json") : new string[0]; }

            public string Overrides { get { return Ws.PathOf(@"Content\Resources\StyleGuide\overrides.ini"); } }

            public void Dispose() { Ws.Dispose(); }
        }

        // ---- #84: read-only detection, and every change goes through a request ---------------------------------

        [Fact]
        public void OwnersFile_AndReadOnlyDetection_Agree()
        {
            using (var fx = new Fixture())
            {
                var owner = fx.As(Lead);
                Assert.Null(owner.ProbeGuide());
                Assert.False(owner.ChangesNeedRequest);            // owner + writable: changes apply directly
                Assert.Null(owner.AccessNotice);

                var openWriter = fx.As(Pat);
                Assert.True(openWriter.CanWriteGuide);
                Assert.True(openWriter.ChangesNeedRequest);        // writable but not an owner: still a request…
                Assert.Contains("more open than the owners list says", openWriter.AccessNotice);   // …and the writer is told

                fx.Ws.MakeReadOnly(fx.Guide);
                var writer = fx.As(Pat);
                Assert.NotNull(writer.ProbeGuide());
                Assert.True(writer.ChangesNeedRequest);
                Assert.StartsWith("You can read this guide; changes go to the owners (" + Lead + ", " + Backup + ")", writer.AccessNotice);

                var stuckOwner = fx.As(Lead);
                Assert.True(stuckOwner.ChangesNeedRequest);        // an owner who can't write is told what to fix
                Assert.Contains("Ask whoever manages that folder for write access", stuckOwner.AccessNotice);

                // The probe leaves nothing behind.
                Assert.Empty(Directory.GetFiles(Path.Combine(fx.Guide, "FlareAI"), "*.tmp"));
            }
        }

        [Fact]
        public void NoOwnersFile_ChangesApplyDirectly_AsBefore()
        {
            using (var fx = new Fixture(ownersFile: false))
            {
                var style = fx.Style();
                var team = fx.As(Pat);
                Assert.False(team.ChangesNeedRequest);
                StyleFinding f;
                var s = fx.Session(team, style, fx.GoogleRule(style), out f);
                fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Not for us." };

                s.TurnOffRule();

                Assert.Null(fx.Host.Request);
                Assert.Empty(fx.RequestFiles());
                Assert.Contains(GuideOverrides.Read(fx.Guide), o => o.RuleId == f.RuleId && o.Setting == "off");
            }
        }

        [Fact]
        public void AnUnreadableOwnersFile_FailsClosed_ChangesBecomeRequests_AndTheWriterIsTold()
        {
            using (var fx = new Fixture())
            {
                fx.Ws.MakeUnreadable(Path.Combine(fx.Guide, @"FlareAI\owners.txt"));
                var style = fx.Style();
                var team = fx.As(Lead);   // even a listed owner: the list can't be read

                Assert.True(team.IsStrict);
                Assert.False(team.IsOwner);
                Assert.False(team.IsListedOwner);
                Assert.NotNull(team.OwnersProblem);
                Assert.True(team.ChangesNeedRequest);
                Assert.Contains("can't read the guide's owners list", team.AccessNotice);
                Assert.Contains("owner-only", File.ReadAllText(fx.Ws.DiagLog));
                // Settings > Style guide status says the same (the check wants a Flare project: give the folder a project file).
                var flprj = Path.Combine(fx.Guide, "Team.flprj");
                File.WriteAllText(flprj, "<CatapultProject />");
                var status = FlareAI.Core.Settings.GuideFolderCheck.Check(fx.Guide, Lead);
                File.Delete(flprj);
                Assert.False(status.CanChangeRules);
                Assert.Contains("can't read the guide's owners list", status.Message);

                // A rule change becomes a request; the guide is untouched.
                StyleFinding f;
                var s = fx.Session(team, style, fx.GoogleRule(style), out f);
                fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Members say utilize." };
                fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Should not be asked." };
                s.TurnOffRule();
                Assert.NotNull(fx.Host.Request);
                Assert.Single(fx.RequestFiles());
                Assert.DoesNotContain(GuideOverrides.Read(fx.Guide), o => o.RuleId == f.RuleId);

                // Read access comes back: the owner can change rules again, without restarting Flare.
                fx.Ws.ClearReadOnly();
                Assert.False(RuleChanges.NeedsRequest(team));
                Assert.True(team.IsListedOwner);
            }
        }

        [Fact]
        public void PermissionAndOwnerChanges_TakeEffectBeforeTheNextWrite_WithoutRestarting()
        {
            using (var fx = new Fixture())
            {
                var owner = fx.As(Lead);
                Assert.False(RuleChanges.NeedsRequest(owner));
                fx.Ws.MakeReadOnly(fx.Guide);                       // the folder becomes read-only after Flare started
                Assert.True(RuleChanges.NeedsRequest(owner));
                fx.Ws.ClearReadOnly();
                Assert.False(RuleChanges.NeedsRequest(owner));
            }
            using (var fx = new Fixture(ownersFile: false))
            {
                var pat = fx.As(Pat);
                Assert.False(RuleChanges.NeedsRequest(pat));
                File.WriteAllText(Path.Combine(fx.Guide, @"FlareAI\owners.txt"), Lead + "\n");   // the team adds owners
                Assert.True(RuleChanges.NeedsRequest(pat));
                Assert.Contains(Lead, pat.AccessNotice);
            }
        }

        [Fact]
        public void ReadOnlyGuide_TurnOff_Severity_Wording_AddRule_AndProjectException_AllBecomeRequests()
        {
            using (var fx = new Fixture())
            {
                var style = fx.Style();
                fx.Ws.MakeReadOnly(fx.Guide);
                var before = Snapshot(fx.Guide);
                var team = fx.As(Pat);
                StyleFinding google, prose;
                var s = fx.Session(team, style, fx.GoogleRule(style), out google);

                fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Members say utilize." };
                s.TurnOffRule();
                fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "suggestion", Reason = "Make it a tip." };
                s.TurnOffRule();
                fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Legal copy must say utilize." };
                s.TurnOffRule();

                Assert.Null(fx.Host.Request.Wording);   // a Google rule: owners change its level, not its words
                var s2 = fx.Session(team, style, fx.ProseRule(style), out prose);
                fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Wording = "Don't promise dates, prices or availability.", Reason = "Shorter." };
                s2.TurnOffRule();
                Assert.Equal(fx.ProseRule(style).Text, fx.Host.Request.Wording);   // the prose rule's wording was offered to edit

                // Build style guide's "Add to guide" on a read-only guide sends the rule as a request.
                Assert.True(team.ChangesNeedRequest);
                var add = GuideRequests.New(team, RequestKinds.AddRule, "We keep writing sign in.");
                add.Candidate = new RuleCandidate { Title = "Use \"sign in\"", Avoid = "log in", Use = "sign in", Why = "Our term.", Example = "Log in to the app." };
                string problem;
                Assert.True(GuideRequests.Send(team, add, out problem));

                Assert.Empty(fx.Host.Errors);                       // a request, never an error
                Assert.Equal(before, Snapshot(fx.Guide));            // no write was attempted
                Assert.False(File.Exists(fx.Overrides));             // the project exception waits for an owner
                var kinds = GuideRequests.Load(team).Select(r => r.Kind).OrderBy(k => k).ToArray();
                Assert.Equal(new[] { RequestKinds.AddRule, RequestKinds.ProjectException, RequestKinds.Severity, RequestKinds.TurnOff, RequestKinds.Wording }, kinds);
                Assert.Equal(5, fx.RequestFiles().Length);           // one file per request
                Assert.True(google.RequestSent);                     // still listed, marked "Request sent"
                Assert.Contains(google, s.Findings);
                var exception = GuideRequests.Load(team).Single(r => r.Kind == RequestKinds.ProjectException);
                Assert.Equal("DigitalBanking", exception.Project);
                Assert.Contains("\"utilize\"", exception.Evidence.Single());
            }
        }

        [Fact]
        public void WriteFailsPartway_OffersARequest_AndKeepsTheReason()
        {
            using (var fx = new Fixture())
            {
                var style = fx.Style();
                var owner = fx.As(Lead);
                StyleFinding f;
                var s = fx.Session(owner, style, fx.GoogleRule(style), out f);
                // The share turns read-only after the check found it writable (permissions changed).
                s.SetGuideRule = (t, id, setting, reason) => { fx.Ws.MakeReadOnly(fx.Guide); return GuideOverrides.Set(t, id, setting, reason); };
                fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Our members say it." };
                fx.Host.OnRequest = q => new RuleSettingChoice { ForTeam = true, Setting = q.Setting, Reason = q.Reason };

                s.TurnOffRule();

                Assert.Contains("couldn't write to " + fx.Guide, fx.Host.Confirms.Single());
                Assert.Equal("Our members say it.", fx.Host.Request.Reason);   // not typed again
                Assert.Equal("Our members say it.", GuideRequests.Load(owner).Single().Reason);
                Assert.Empty(fx.Host.Errors);
                Assert.True(owner.ChangesNeedRequest);
            }
        }

        // ---- #85: the lifecycle --------------------------------------------------------------------------------

        [Fact]
        public void TurningOnABundledDefault_OnAReadOnlyGuide_IsARequest_ThatWritesAnOnRowWhenApproved()
        {
            using (var fx = new Fixture())
            {
                fx.Ws.MakeReadOnly(fx.Guide);
                var writer = fx.As(Pat);
                Assert.True(writer.ChangesNeedRequest);            // the Turned-off rules view sends a request, not a write
                var r = GuideRequests.New(writer, RequestKinds.TurnOn, "We want this rule.");
                r.RuleId = "Google.Bundled-default"; r.Rule = r.RuleId; r.Setting = "off";   // a bundled default: no row in the team guide
                string problem;
                GuideRequests.Send(writer, r, out problem);
                Assert.Null(problem);

                fx.Ws.ClearReadOnly();
                var owner = fx.As(Lead);
                Assert.Null(GuideRequests.Approve(owner, GuideRequests.Load(owner).Single(), null, (x, d) => GuideRequests.ApplyToGuide(owner, x, d)));
                var row = RuleOverrides.Read(fx.Guide, "Team style guide").Single(o => o.RuleId == "Google.Bundled-default");
                Assert.True(row.IsTurnOn);                           // an "on" row overrides the bundled default
            }
        }

        [Fact]
        public void OwnerApproves_TheGuideChanges_AndTheWriterSeesItOnTheirNextCheck_Once()
        {
            using (var fx = new Fixture())
            {
                var style = fx.Style();
                var rule = fx.GoogleRule(style);
                fx.Ws.MakeReadOnly(fx.Guide);
                var writer = fx.As(Pat);
                StyleFinding f;
                var s = fx.Session(writer, style, rule, out f);
                fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = true, Setting = "off", Reason = "Members say utilize." };
                s.TurnOffRule();
                Assert.Null(s.CheckRequests());                      // nothing decided yet; still marked
                Assert.True(f.RequestSent);

                fx.Ws.ClearReadOnly();                               // the owner's machine: read-write
                var owner = fx.As(Lead);
                var request = GuideRequests.Load(owner).Single();
                Assert.True(request.IsOpen);
                Assert.Null(GuideRequests.Approve(owner, request, new RequestDecision { Setting = "suggestion", Note = "A tip, not off." }, (r, d) => GuideRequests.ApplyToGuide(owner, r, d)));

                Assert.Equal("suggestion", GuideOverrides.Read(fx.Guide).Single(o => o.RuleId == rule.Id).Setting);   // the owner's edit applied
                Assert.Contains(request.Id, GuideOverrides.Read(fx.Guide).Single(o => o.RuleId == rule.Id).Note);

                var notice = fx.Session(writer, fx.Style(), rule, out f).CheckRequests();
                Assert.Contains("approved", notice);
                Assert.Contains("A tip, not off.", notice);
                Assert.False(f.RequestSent);
                Assert.Null(fx.Session(writer, fx.Style(), rule, out f).CheckRequests());   // shown once
                Assert.Equal(RequestStatus.Approved, GuideRequests.Load(writer).Single().Status);
            }
        }

        [Fact]
        public void OwnerDeclines_NothingChanges_AndTheWriterSeesTheNote()
        {
            using (var fx = new Fixture())
            {
                var writer = fx.As(Pat);
                var r = GuideRequests.New(writer, RequestKinds.TurnOff, "Too strict.");
                r.RuleId = "google:words/2"; r.Rule = "Google.WordList"; r.Setting = "off";
                string problem;
                GuideRequests.Send(writer, r, out problem);
                var before = Snapshot(fx.Guide);

                var owner = fx.As(Backup);   // the backup owner decides alone
                Assert.Null(GuideRequests.Decline(owner, GuideRequests.Load(owner).Single(), "We keep this one; see the Word list."));

                Assert.Equal(before, Snapshot(fx.Guide));
                var check = GuideRequests.Check(writer);
                Assert.Contains("declined", check.Notice);
                Assert.Contains("Declined by " + Backup, check.Notice);
                Assert.Contains("We keep this one; see the Word list.", check.Notice);
            }
        }

        [Fact]
        public void TwoOwnersDecide_TheFirstWins_AndTheSecondSeesWhoDecided()
        {
            using (var fx = new Fixture())
            {
                var writer = fx.As(Pat);
                var r = GuideRequests.New(writer, RequestKinds.Severity, "Make it a tip.");
                r.RuleId = "google:words/2"; r.Setting = "suggestion";
                string problem;
                GuideRequests.Send(writer, r, out problem);
                var lead = fx.As(Lead);
                var backup = fx.As(Backup);
                var seenByBackup = GuideRequests.Load(backup).Single();   // opened before the lead decided

                Assert.Null(GuideRequests.Approve(lead, GuideRequests.Load(lead).Single(), null, (x, d) => GuideRequests.ApplyToGuide(lead, x, d)));
                var earlier = GuideRequests.Decline(backup, seenByBackup, "No.");

                Assert.NotNull(earlier);
                Assert.Equal(Lead, earlier.By);
                Assert.Equal(RequestStatus.Approved, earlier.Status);
                Assert.Equal(RequestStatus.Approved, GuideRequests.Load(backup).Single().Status);
                Assert.Single(Directory.GetFiles(Path.Combine(fx.Feedback, "requests"), "*.decision.json"));
            }
        }

        [Fact]
        public void Writer_WithdrawsAnOpenRequest_AndOwnersCanNoLongerDecideIt()
        {
            using (var fx = new Fixture())
            {
                var writer = fx.As(Pat);
                var r = GuideRequests.New(writer, RequestKinds.TurnOff, "Changed my mind later.");
                r.RuleId = "google:words/2"; r.Setting = "off";
                string problem;
                GuideRequests.Send(writer, r, out problem);

                Assert.Null(GuideRequests.Withdraw(writer, GuideRequests.Load(writer).Single()));
                var owner = fx.As(Lead);
                var earlier = GuideRequests.Approve(owner, GuideRequests.Load(owner).Single(), null, (x, d) => GuideRequests.ApplyToGuide(owner, x, d));

                Assert.Equal(RequestStatus.Withdrawn, earlier.Status);
                Assert.Empty(GuideOverrides.Read(fx.Guide).Where(o => o.RuleId == "google:words/2"));
                Assert.Null(GuideRequests.Check(writer).Notice);   // a withdrawal isn't news to the writer
            }
        }

        [Fact]
        public void OwnersAlone_Decide_AWriterCannot()
        {
            using (var fx = new Fixture())
            {
                var writer = fx.As(Pat);
                var r = GuideRequests.New(writer, RequestKinds.TurnOff, "Please.");
                r.RuleId = "google:words/2"; r.Setting = "off";
                string problem;
                GuideRequests.Send(writer, r, out problem);
                Assert.Throws<UnauthorizedAccessException>(() => GuideRequests.Approve(writer, GuideRequests.Load(writer).Single(), null, (x, d) => GuideRequests.ApplyToGuide(writer, x, d)));
                Assert.True(GuideRequests.Load(writer).Single().IsOpen);
            }
        }

        [Fact]
        public void Requests_SurviveARestart_AndOnesThatCouldNotBeSent_GoOutOnTheNextCheck()
        {
            using (var fx = new Fixture())
            {
                fx.Ws.MakeReadOnly(fx.Feedback);   // the requests folder is offline
                var writer = fx.As(Pat);
                var r = GuideRequests.New(writer, RequestKinds.TurnOff, "Members say utilize.");
                r.RuleId = "google:words/2"; r.Setting = "off";
                string problem;
                Assert.False(GuideRequests.Send(writer, r, out problem));
                Assert.Contains(Path.Combine(fx.Feedback, "requests"), problem);

                // Restart: a new team context and nothing in memory.
                var again = fx.As(Pat);
                var unsent = GuideRequests.Load(again).Single();
                Assert.True(unsent.Unsent);
                Assert.Equal("Members say utilize.", unsent.Reason);   // the reason isn't lost

                fx.Ws.ClearReadOnly();
                GuideRequests.Check(again);   // the next check sends it
                Assert.Single(fx.RequestFiles());
                var sent = GuideRequests.Load(fx.As(Lead)).Single();
                Assert.False(sent.Unsent);
                Assert.True(sent.IsOpen);
                Assert.Equal(r.Id, sent.Id);
                Assert.True(GuideRequests.IsMine(fx.As(Pat), sent));
            }
        }

        [Fact]
        public void OlderProposals_InTheFeedbackFolder_OpenAsRequests_WithNothingLost()
        {
            using (var fx = new Fixture())
            {
                File.WriteAllText(Path.Combine(fx.Feedback, "writer-abc.jsonl"),
                    "{\"at\":\"2026-09-01T10:00:00Z\",\"writer\":\"writer-abc\",\"decision\":\"propose-ignore\",\"reason\":\"We say utilize.\",\"layer\":\"google\",\"rule\":\"Google.WordList\",\"rule_id\":\"google:words/2\",\"excerpt\":\"utilize\",\"file\":\"C:\\\\docs\\\\a.htm\"}\n" +
                    "{\"at\":\"2026-09-01T10:01:00Z\",\"writer\":\"writer-abc\",\"decision\":\"accepted\",\"rule\":\"Google.WordList\",\"rule_id\":\"google:words/2\"}\n");
                var owner = fx.As(Lead);
                var legacy = GuideRequests.Load(owner).Single();
                Assert.True(legacy.IsLegacy && legacy.IsOpen);
                Assert.Equal(RequestKinds.TurnOff, legacy.Kind);
                Assert.Equal("We say utilize.", legacy.Reason);
                Assert.Equal("google:words/2", legacy.RuleId);
                Assert.Contains("\"utilize\" (a.htm)", legacy.Evidence);

                Assert.Null(GuideRequests.Decline(owner, legacy, "Keep it."));
                var reread = GuideRequests.Load(fx.As(Lead)).Single();
                Assert.Equal(legacy.Id, reread.Id);                         // a stable id: the decision sticks
                Assert.Equal(RequestStatus.Declined, reread.Status);
            }
        }

        [Fact]
        public void OwnerApproves_Wording_And_AddRule_ChangeTheGuideTopics()
        {
            using (var fx = new Fixture())
            {
                var style = fx.Style();
                var prose = fx.ProseRule(style);
                var writer = fx.As(Pat);
                var reword = GuideRequests.New(writer, RequestKinds.Wording, "Shorter.");
                reword.RuleId = prose.Id; reword.RuleText = prose.Text; reword.RuleFile = ReviewSession.RuleFile(prose); reword.Wording = "Don't promise dates, prices or availability.";
                Assert.StartsWith(fx.Guide, reword.RuleFile);   // the team guide's copy, never the repo's style guide
                var add = GuideRequests.New(writer, RequestKinds.AddRule, "We keep writing sign in.");
                add.Candidate = new RuleCandidate { Title = "Use \"sign in\"", Avoid = "log in", Use = "sign in", Severity = "warning", Why = "Our term.", Example = "Log in to the app." };
                string problem;
                GuideRequests.Send(writer, reword, out problem);
                GuideRequests.Send(writer, add, out problem);

                var owner = fx.As(Lead);
                foreach (var r in GuideRequests.Load(owner))
                    Assert.Null(GuideRequests.Approve(owner, r, null, (x, d) => GuideRequests.ApplyToGuide(owner, x, d)));

                var claims = File.ReadAllText(Path.Combine(fx.Guide, @"Company\Claims.htm"));
                Assert.Contains("<li id=\"rule-1\">Don't promise dates, prices or availability.<ul><li>Bad:", claims);   // its Bad/Good examples stay
                Assert.Contains(fx.Style().Rules(null), r => r.Id == prose.Id && r.Text == "Don't promise dates, prices or availability.");
                Assert.Contains("log in", File.ReadAllText(Path.Combine(fx.Guide, "From-our-content.htm")));
            }
        }

        // ---- #86: project-only exceptions ----------------------------------------------------------------------

        [Fact]
        public void ProjectException_Approved_TheWritersPluginWritesIt_OnTheNextCheck()
        {
            using (var fx = new Fixture())
            {
                var style = fx.Style();
                var rule = fx.GoogleRule(style);
                fx.Ws.MakeReadOnly(fx.Guide);
                var writer = fx.As(Pat);
                StyleFinding f;
                var s = fx.Session(writer, style, rule, out f);
                fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Legal copy." };
                s.TurnOffRule();
                Assert.False(File.Exists(fx.Overrides));
                Assert.True(f.RequestSent);   // visible until decided; Skip still works without a request
                s.Skip();

                // The owner's machine doesn't have the writer's project: approving writes nothing there, or to the guide.
                fx.Ws.ClearReadOnly();
                var guideBefore = Snapshot(fx.Guide);
                var owner = fx.As(Lead);
                var request = GuideRequests.Load(owner).Single();
                request.ProjectPath = @"Z:\nowhere\Docs\Content\a.htm";   // as the owner sees the writer's path
                Assert.Null(GuideRequests.Approve(owner, request, new RequestDecision { Note = "Fine for legal." }, (x, d) => GuideRequests.ApplyToGuide(owner, x, d)));
                Assert.Equal(guideBefore, Snapshot(fx.Guide));
                Assert.False(File.Exists(fx.Overrides));

                // The writer's next check writes it, once, with who/when/why and the approved request.
                var notice = fx.Session(writer, style, rule, out f).CheckRequests();
                Assert.Contains("approved", notice);
                var ini = File.ReadAllText(fx.Overrides);
                Assert.Contains("# by " + Lead + " on ", ini);
                Assert.Contains(rule.Id + " = off", ini);
                var entry = RuleOverrides.ReadIni(fx.Overrides).Single(o => o.RuleId == rule.Id);
                Assert.Equal(request.Id, entry.RequestId);   // Turned-off rules flags it as approved
                Assert.Equal("Legal copy. Owner: Fine for legal.", entry.Note);
                Assert.Equal(Lead, entry.By);
                GuideRequests.Check(writer);
                Assert.Single(RuleOverrides.ReadIni(fx.Overrides), o => o.RuleId == rule.Id);
                Assert.True(fx.Style().IsOff(fx.Ws.PathOf(Topic), rule.Id));   // the team rule stays on; this project relaxes it
                Assert.False(fx.Style().IsOff(null, rule.Id));
            }
        }

        [Fact]
        public void ProjectException_Declined_NothingIsWritten()
        {
            using (var fx = new Fixture())
            {
                var style = fx.Style();
                fx.Ws.MakeReadOnly(fx.Guide);
                var writer = fx.As(Pat);
                StyleFinding f;
                var s = fx.Session(writer, style, fx.GoogleRule(style), out f);
                fx.Host.RequestChoice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Legal copy." };
                s.TurnOffRule();
                fx.Ws.ClearReadOnly();
                var owner = fx.As(Lead);
                GuideRequests.Decline(owner, GuideRequests.Load(owner).Single(), "Reword the legal copy instead.");

                var check = GuideRequests.Check(writer);
                Assert.Contains("Reword the legal copy instead.", check.Notice);
                Assert.False(File.Exists(fx.Overrides));
            }
        }

        [Fact]
        public void ProjectException_ForAProjectNotOnThisMachine_WaitsWithoutFailing()
        {
            using (var fx = new Fixture())
            {
                var writer = fx.As(Pat);
                var r = GuideRequests.New(writer, RequestKinds.ProjectException, "Legal copy.");
                r.RuleId = "google:words/2"; r.Setting = "off"; r.ProjectPath = Path.Combine(fx.Ws.Root, @"Elsewhere\Content\a.htm");
                string problem;
                GuideRequests.Send(writer, r, out problem);
                var owner = fx.As(Lead);
                GuideRequests.Approve(owner, GuideRequests.Load(owner).Single(), null, (x, d) => GuideRequests.ApplyToGuide(owner, x, d));

                Assert.Contains("approved", GuideRequests.Check(writer).Notice);   // told once…
                Assert.Empty(Directory.GetFiles(fx.Ws.Root, "overrides.ini", SearchOption.AllDirectories));

                // …and once the project is on this machine, the next check writes it.
                Directory.CreateDirectory(Path.Combine(fx.Ws.Root, @"Elsewhere\Content"));
                File.WriteAllText(Path.Combine(fx.Ws.Root, @"Elsewhere\Elsewhere.flprj"), "<CatapultProject />");
                var check = GuideRequests.Check(writer);
                Assert.Contains("now in", check.Notice);
                Assert.True(File.Exists(Path.Combine(fx.Ws.Root, @"Elsewhere\Content\Resources\StyleGuide\overrides.ini")));
            }
        }

        [Fact]
        public void Owners_StillWriteProjectExceptionsDirectly()
        {
            using (var fx = new Fixture())
            {
                var style = fx.Style();
                var owner = fx.As(Lead);
                StyleFinding f;
                var s = fx.Session(owner, style, fx.GoogleRule(style), out f);
                fx.Host.RuleChoice = new RuleSettingChoice { ForTeam = false, Setting = "off", Reason = "Legal copy." };

                s.TurnOffRule();

                Assert.Null(fx.Host.Request);
                Assert.Empty(fx.RequestFiles());
                Assert.Contains(f.RuleId + " = off", File.ReadAllText(fx.Overrides));
            }
        }

        // ---- security: the shared requests folder is writable by every writer ----------------------------------

        [Fact]
        public void AForgedRequestAndApproval_InTheSharedFolder_AreNeverAppliedOnAWritersMachine()
        {
            using (var fx = new Fixture())
            {
                var writer = fx.As(Pat);
                var folder = GuideRequests.Folder(writer);
                Directory.CreateDirectory(folder);
                var target = Path.Combine(fx.Ws.Root, @"Victim\Content\a.htm");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(Path.Combine(fx.Ws.Root, @"Victim\Victim.flprj"), "<CatapultProject />");
                // Another writer plants a project exception "from" this writer, approved "by" an owner.
                File.WriteAllText(Path.Combine(folder, "r-x.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new GuideRequest
                {
                    Id = "r-x", Writer = writer.FeedbackWriter, Kind = RequestKinds.ProjectException, RuleId = "google:words/2\r\ncompany:claims/1",
                    Setting = "off", ProjectPath = target, Reason = "x"
                }));
                File.WriteAllText(Path.Combine(folder, "r-x.decision.json"), "{\"Id\":\"r-x\",\"Status\":\"approved\",\"By\":\"" + Lead + "\"}");

                GuideRequests.Check(writer);

                Assert.Empty(Directory.GetFiles(fx.Ws.Root, "overrides.ini", SearchOption.AllDirectories));
            }
        }

        [Fact]
        public void AnApprovedException_IsAppliedAsThisMachineSentIt_NotAsTheSharedFileNowSays()
        {
            using (var fx = new Fixture())
            {
                var writer = fx.As(Pat);
                var mine = Path.Combine(fx.Ws.Root, @"Mine\Content\a.htm");
                var other = Path.Combine(fx.Ws.Root, @"Other\Content\a.htm");
                foreach (var p in new[] { "Mine", "Other" })
                {
                    Directory.CreateDirectory(Path.Combine(fx.Ws.Root, p, "Content"));
                    File.WriteAllText(Path.Combine(fx.Ws.Root, p, p + ".flprj"), "<CatapultProject />");
                }
                var r = GuideRequests.New(writer, RequestKinds.ProjectException, "Legal copy.");
                r.RuleId = "google:words/2"; r.Setting = "off"; r.ProjectPath = mine;
                string problem;
                Assert.True(GuideRequests.Send(writer, r, out problem));
                // Someone rewrites the shared copy to point at another project and rule before the owner approves it.
                var shared = Path.Combine(GuideRequests.Folder(writer), r.Id + ".json");
                File.WriteAllText(shared, File.ReadAllText(shared).Replace(Newtonsoft.Json.JsonConvert.ToString(mine).Trim('"'), Newtonsoft.Json.JsonConvert.ToString(other).Trim('"'))
                                                                  .Replace("google:words/2", "company:claims/1"));
                var owner = fx.As(Lead);
                GuideRequests.Approve(owner, GuideRequests.Load(owner).Single(), null, (x, d) => GuideRequests.ApplyToGuide(owner, x, d));

                GuideRequests.Check(writer);

                var ini = File.ReadAllText(Path.Combine(fx.Ws.Root, @"Mine\Content\Resources\StyleGuide\overrides.ini"));
                Assert.Contains("google:words/2 = off", ini);
                Assert.DoesNotContain("company:claims/1", ini);
                Assert.False(File.Exists(Path.Combine(fx.Ws.Root, @"Other\Content\Resources\StyleGuide\overrides.ini")));
            }
        }

        [Fact]
        public void RequestFiles_WithAnIdThatIsAPath_AreIgnored()
        {
            using (var fx = new Fixture())
            {
                var owner = fx.As(Lead);
                var folder = GuideRequests.Folder(owner);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "evil.json"), "{\"Id\":\"..\\\\..\\\\escape\",\"Kind\":\"turn-off\",\"Reason\":\"x\"}");
                File.WriteAllText(Path.Combine(folder, "r-ok.json"), "{\"Id\":\"r-other\",\"Kind\":\"turn-off\",\"Reason\":\"x\"}");   // not the file it claims

                Assert.Empty(GuideRequests.Load(owner));
                Assert.False(GuideRequests.IsValidId(@"..\x"));
                Assert.False(GuideRequests.IsValidId(@"\\host\share\x"));
                Assert.True(GuideRequests.IsValidId(GuideRequests.New(owner, RequestKinds.TurnOff, "x").Id));
            }
        }

        // ---- helpers -------------------------------------------------------------------------------------------

        private static string Snapshot(string folder)
        {
            return string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p)
                .Select(p => p.Substring(folder.Length) + ":" + File.ReadAllText(p).GetHashCode()).ToArray());
        }

        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
        }

        /// <summary>The review pane's dialogs, answered by the test.</summary>
        private sealed class ScenarioHost : IReviewHost
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Confirms = new List<string>();
            public RuleSettingChoice RuleChoice, RequestChoice;
            public Func<RequestQuestion, RuleSettingChoice> OnRequest;
            public RequestQuestion Request;

            public bool Confirm(string message, string action) { Confirms.Add(message); return true; }
            public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules) { return RuleChoice; }
            public RuleSettingChoice ChooseRequest(RequestQuestion question) { Request = question; return OnRequest != null ? OnRequest(question) : RequestChoice; }
            public string AskText(string title, string label, string initial, bool multiline, bool required = false) { return null; }
            public IDocumentAdapter OpenDocument(string path) { return null; }
            public bool PreviewBatch(BatchPlan plan) { return true; }
            public bool SaveAndClose(IDocumentAdapter doc) { return false; }
            public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { Errors.Add(whatHappened + " " + whatToDo); }
        }
    }
}
