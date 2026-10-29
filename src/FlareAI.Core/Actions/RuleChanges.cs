using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>What a rule change did: written (where), or sent to the owners as a request.</summary>
    public sealed class RuleChangeResult
    {
        /// <summary>True when the change went to the style guide owners as a request (nothing was written).</summary>
        public bool Requested { get; set; }
        public GuideRequest Request { get; set; }
        /// <summary>The request is saved but not sent yet (it goes on the next check): why. Null when sent.</summary>
        public string Problem { get; set; }
        /// <summary>The rules changed (ids or names), in order.</summary>
        public List<string> Changed { get; private set; }
        /// <summary>Rules of the group that could not be changed after the first one was: why. Null when all were.</summary>
        public string Incomplete { get; set; }
        /// <summary>The topic or overrides.ini written, or null.</summary>
        public string File { get; set; }

        public RuleChangeResult() { Changed = new List<string>(); }
    }

    /// <summary>
    /// Turning a rule off, changing its level and turning it back on, routed the way the review pane does it: owners (and
    /// teams without an owners file, and a writer's own guide) write the style guide directly; a writer on a team guide that
    /// has owners, or that is read-only for them, sends a request to the owners instead, project exceptions included.
    /// The writes are <see cref="GuideOverrides.Set(Team,string,string,string)"/>, <see cref="ProjectRules.Ignore"/> and
    /// <see cref="RuleOverrides.TurnBackOn"/>; the requests are <see cref="GuideRequests"/>.
    /// </summary>
    public static class RuleChanges
    {
        /// <summary>Why a team change to a rule read from the saved copy of an unreachable team guide is refused (#246).</summary>
        public const string CopyRefusal = "You can't change this rule while your team's style guide can't be reached. Try again when the folder is back.";

        /// <summary>
        /// True when the rule, or the team change in effect for it, was read from the saved copy of a team guide that can't be
        /// reached: changing it would write into the copy, or send the owners a request about it. A project's own exception
        /// doesn't count.
        /// </summary>
        public static bool IsFromCopy(CatalogRule rule)
        {
            if (rule == null) return false;
            if (GuideCopy.IsCopy(rule.File)) return true;
            return rule.Change != null && !rule.ChangeIsProject && GuideCopy.IsCopy(rule.Change.File);
        }

        /// <summary>True when this writer's changes to the style guide go to the owners. Probes write access first.</summary>
        public static bool NeedsRequest(Team team)
        {
            if (team == null) return false;
            team.Recheck();   // before any write: permissions or the owners list may have changed
            return team.ChangesNeedRequest;
        }

        /// <summary>The kind of request a choice makes: a project exception, new wording, turning off, or a level.</summary>
        public static string KindFor(RuleSettingChoice choice, string wording)
        {
            return !choice.ForTeam ? RequestKinds.ProjectException : wording != null ? RequestKinds.Wording : choice.Setting == "off" ? RequestKinds.TurnOff : RequestKinds.Severity;
        }

        /// <summary>A request to change a rule (team: off, level or wording; or an exception for the project of <paramref name="projectPath"/>).</summary>
        /// <param name="ruleText">The rule as written, and <paramref name="ruleFile"/> the topic it is in (for a wording change).</param>
        public static GuideRequest RuleRequest(Team team, RuleSettingChoice choice, string ruleId, string rule, string layer, string ruleText, string ruleFile, string projectPath, string wording)
        {
            var r = GuideRequests.New(team, KindFor(choice, wording), (choice.Reason ?? "").Trim());
            r.RuleId = ruleId; r.Rule = rule; r.Layer = layer; r.Setting = choice.Setting ?? "off"; r.Wording = wording;
            r.RuleText = ruleText; r.RuleFile = ruleFile;
            if (!choice.ForTeam) { r.ProjectPath = projectPath; r.Project = ProjectName(ProjectRules.OverridesPath(projectPath)); }
            return r;
        }

        /// <summary>A request to turn a rule back on (or return it to the default) by removing <paramref name="o"/>.</summary>
        public static GuideRequest TurnOnRequest(Team team, RuleOverride o, string reason)
        {
            var r = GuideRequests.New(team, RequestKinds.TurnOn, reason);
            r.RuleId = o.RuleId; r.Rule = o.RuleId; r.Setting = o.Setting;
            return r;
        }

        /// <summary>Sends <paramref name="r"/>; returns why it is only saved (sent on the next check), or null.</summary>
        public static string Send(Team team, GuideRequest r)
        {
            string problem;
            GuideRequests.Send(team, r, out problem);
            return problem;
        }

        /// <summary>The project's folder name from its overrides.ini path (Project\Content\Resources\StyleGuide\overrides.ini).</summary>
        public static string ProjectName(string overrides)
        {
            var dir = overrides == null ? null : new FileInfo(overrides).Directory;
            for (var i = 0; i < 3 && dir != null; i++) dir = dir.Parent;
            return dir == null ? null : dir.Name;
        }

        /// <summary>
        /// Style rules: turn <paramref name="rule"/> off or change its level (<paramref name="choice"/>), for the style guide or
        /// for the project of <paramref name="projectPath"/>, with <paramref name="others"/> (rules on the same words) when
        /// the writer ticked them. A request when <see cref="NeedsRequest"/>; otherwise written, recording who and when.
        /// Throws when the first write fails (<see cref="GuideWriteException"/> names the file); nothing was changed then.
        /// </summary>
        public static RuleChangeResult Change(Team team, CatalogRule rule, RuleSettingChoice choice, IList<CatalogRule> others, string projectPath)
        {
            if (rule == null) throw new ArgumentNullException("rule");
            if (choice == null) throw new ArgumentNullException("choice");
            var project = ProjectRules.OverridesPath(projectPath);
            if (!choice.ForTeam && project == null) throw new InvalidOperationException("Open a topic in the project first. A project exception is written into that project.");
            // Before any write or request: a team change to a rule read from a saved copy (#246).
            if (choice.ForTeam && (IsFromCopy(rule) || (choice.AlsoOthers && others != null && others.Any(IsFromCopy))))
                throw new InvalidOperationException(CopyRefusal);
            var result = new RuleChangeResult();
            if (NeedsRequest(team))
            {
                if (string.IsNullOrWhiteSpace(choice.Reason)) throw new ArgumentException("The owners need a reason to decide.");
                var editable = choice.ForTeam && !rule.IsTable && rule.Origin != "google";   // Google rules are generated: a level, not words
                var wording = editable && !string.IsNullOrWhiteSpace(choice.Wording) && choice.Wording.Trim() != (rule.Text ?? "").Trim() ? choice.Wording.Trim() : null;
                var r = RuleRequest(team, choice, rule.Key, rule.Name ?? rule.Key, rule.Origin, rule.Text, RuleFile(rule), projectPath, wording);
                r.Evidence.Add("Asked from Rules (" + rule.Source + " · " + rule.Page + ")");
                result.Requested = true;
                result.Request = r;
                result.Problem = Send(team, r);
                return result;
            }
            if (choice.ForTeam && (team == null || team.GuideRoot == null)) throw new InvalidOperationException("No style guide is set up to record the change. Choose one in Settings.");
            var all = new List<CatalogRule> { rule };
            if (choice.AlsoOthers && others != null) all.AddRange(others.Where(o => o != null && o != rule));
            foreach (var one in all)
            {
                try
                {
                    if (choice.ForTeam) result.File = GuideOverrides.Set(team, one.Key, choice.Setting, choice.Reason);
                    else
                    {
                        // A project's own earlier exception for the rule goes first: the new line is then the only one.
                        if (one.ChangeIsProject && one.Change != null) RuleOverrides.TurnBackOn(one.Change, null);
                        ProjectRules.Ignore(projectPath, new StyleFinding { RuleId = one.Id, Rule = one.Name ?? one.Key, Layer = one.Origin, Source = "lint" },
                                            choice.Setting, choice.Reason, team != null ? team.Writer : null);
                        result.File = project;
                    }
                    result.Changed.Add(one.Key);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("Style rules: change " + one.Key, ex);
                    if (one == rule) throw;
                    result.Incomplete = "\"" + one.Title + "\" and the rules after it were not changed. " + ex.Message;
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// Style rules: Turn back on / Use the default: removes the change in effect (<see cref="CatalogRule.Change"/>), as
        /// Turned-off rules does. A default of the read-only guide gets an "on" row in the writer's guide. A style guide
        /// change becomes a request when <see cref="NeedsRequest"/>; the project's own exception is removed directly.
        /// </summary>
        /// <param name="reason">Why (required for a request; otherwise unused).</param>
        public static RuleChangeResult Restore(Team team, CatalogRule rule, string reason)
        {
            if (rule == null) throw new ArgumentNullException("rule");
            var o = rule.Change;
            if (o == null) throw new InvalidOperationException("Nothing changed this rule, so there is nothing to undo.");
            if (!rule.ChangeIsProject && IsFromCopy(rule)) throw new InvalidOperationException(CopyRefusal);   // #246, before any write or request
            var result = new RuleChangeResult();
            if (!rule.ChangeIsProject && NeedsRequest(team))
            {
                if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("The owners need a reason to decide.");
                var r = TurnOnRequest(team, o, reason);
                result.Requested = true;
                result.Request = r;
                result.Problem = Send(team, r);
                return result;
            }
            result.File = RuleOverrides.TurnBackOn(o, rule.ChangeIsProject ? null : team);   // a project's own exception: no owner needed
            result.Changed.Add(rule.Key);
            return result;
        }

        /// <summary>
        /// The other rules that were turned off together with <paramref name="rule"/>: rules that flag the same words, off now
        /// by a change with the same author, date, reason and place. Turn back on offers them, as Turn off offered them.
        /// </summary>
        public static List<CatalogRule> CompanionsOf(RuleCatalog catalog, CatalogRule rule)
        {
            var found = new List<CatalogRule>();
            if (catalog == null || rule == null || rule.State != RuleState.Off || rule.Change == null) return found;
            var c = rule.Change;
            foreach (var o in catalog.OverlapsOf(rule))
            {
                var d = o.Change;
                if (o == rule || o.State != RuleState.Off || d == null || o.ChangeIsProject != rule.ChangeIsProject) continue;
                if (Same(c.By, d.By) && Same(c.Date, d.Date) && Same(c.Note, d.Note) && Same(c.File, d.File) && Same(c.Setting, d.Setting)) found.Add(o);
            }
            return found;
        }

        /// <summary>
        /// The rules that flag the same words as <paramref name="ruleId"/> and are still on: turning this one off shows them too.
        /// Empty for a checker with no id (its words are the glossary's, not a rule's) and for an unknown rule.
        /// </summary>
        public static List<CatalogRule> TwinsOf(RuleCatalog catalog, string ruleId)
        {
            var found = new List<CatalogRule>();
            var rule = catalog == null ? null : catalog.Find(ruleId);
            if (rule == null || rule.Id == null) return found;
            foreach (var o in catalog.OverlapsOf(rule))
                if (o != rule && o.State != RuleState.Off) found.Add(o);
            return found;
        }

        private static bool Same(string a, string b) { return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase); }

        /// <summary>Turn back on for <paramref name="rule"/> and, when given, the rules turned off with it (<see cref="CompanionsOf"/>).</summary>
        public static RuleChangeResult Restore(Team team, CatalogRule rule, IList<CatalogRule> also, string reason)
        {
            var result = Restore(team, rule, reason);
            if (also == null) return result;
            foreach (var one in also)
            {
                if (one == null || one == rule || one.Change == null) continue;
                try
                {
                    var r = Restore(team, one, reason);
                    result.Changed.AddRange(r.Changed);
                    if (r.File != null) result.File = r.File;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("Style rules: restore " + one.Key, ex);
                    result.Incomplete = "\"" + one.Title + "\" and the rules after it were not enabled. " + ex.Message;
                    break;
                }
            }
            return result;
        }

        /// <summary>True when changing (or restoring) <paramref name="rule"/> this way would be a request, not a write.</summary>
        public static bool WillRequest(Team team, CatalogRule rule, bool restore)
        {
            if (restore && rule != null && rule.ChangeIsProject) return false;
            return NeedsRequest(team);
        }

        private static string RuleFile(CatalogRule rule)
        {
            return ReviewSession.RuleFile(new GuideRule { Source = rule.File, Link = rule.Link });
        }
    }
}
