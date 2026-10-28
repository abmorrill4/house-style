using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Xml;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>What the writer did in the pane, for the guided first lint and the checklist.</summary>
    public enum ReviewEvent
    {
        /// <summary>An issue was changed in a document (one or many, by any Change command).</summary>
        Changed,
        /// <summary>A change was undone in the editor and its issue is live again.</summary>
        Undone,
        /// <summary>A rule was disabled, or a request to change it went to the owners.</summary>
        RuleDisabled
    }

    /// <summary>What <see cref="ReviewSession.Accept"/> did, so the view can run the flows that need its own dialogs.</summary>
    public enum AcceptOutcome
    {
        /// <summary>Nothing selected, or it is already applied.</summary>
        None,
        /// <summary>The text changed since the check; the status says to check the paragraph again.</summary>
        Stale,
        /// <summary>A variable or manual fix: the text is selected in the editor and the status says what to do.</summary>
        WentTo,
        /// <summary>Needs a rewrite of the paragraph: the view runs the rewrite (see <see cref="ReviewSession.RewriteTarget"/>).</summary>
        Rewrite,
        /// <summary>An image without alt text: the view runs the alt-text flow (see <see cref="ReviewSession.AltTextApplied"/>).</summary>
        AltText,
        /// <summary>The author said no to changing a shared snippet.</summary>
        Declined,
        /// <summary>Written as a tracked change; the next suggestion is selected.</summary>
        Applied,
        /// <summary>Tried, but nothing was written (the status says why).</summary>
        NotApplied
    }

    /// <summary>
    /// The review pane's decisions, without the pane: the list of suggestions in reading order, which one is selected, and
    /// what Apply, Apply all fixes, Write all as tracked changes, Reject, Skip, Edit, Turn off this rule and Go to do to the
    /// topic, its snippets, the feedback log and the style guide. The Flare pane renders it and forwards clicks; tests drive
    /// it directly. No WinForms or Flare types.
    /// </summary>
    public sealed class ReviewSession
    {
        private readonly ActionRunner _runner;
        private readonly IDocumentAdapter _doc;
        private readonly IReviewHost _host;
        private readonly Dismissals _dismissed;
        private readonly bool _project;
        private readonly List<StyleFinding> _findings = new List<StyleFinding>();
        private readonly Dictionary<string, int> _usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Other files this session wrote fixes in that stayed open in the editor (their undo history is there).</summary>
        private readonly Dictionary<string, IDocumentAdapter> _writtenIn = new Dictionary<string, IDocumentAdapter>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _ignoredAi;
        private HashSet<string> _requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skippedRules = new HashSet<string>(StringComparer.Ordinal);
        private string _status;
        private IList<Exemption> _exemptions = new List<Exemption>();
        private DateTime _exemptionsStamp = DateTime.MinValue;
        private string _exemptionsFile;
        private readonly HashSet<string> _exemptSeen = new HashSet<string>(StringComparer.Ordinal);
        private bool _watching, _offThreadLogged;
        private int _uiThread, _pendingMarked, _pendingReopened;

        /// <param name="topic">The checked topic (in a project report: the topic that was active).</param>
        /// <param name="dismissed">Where rejected suggestions are remembered.</param>
        /// <param name="projectReport">True for a project-wide check: findings are grouped by file, and files opened for a batch are saved and closed.</param>
        public ReviewSession(ActionRunner runner, IDocumentAdapter topic, IReviewHost host, Dismissals dismissed, bool projectReport)
        {
            _runner = runner; _doc = topic; _host = host; _dismissed = dismissed; _project = projectReport;
        }

        // ---- seams (defaults are the real stores; tests replace them) ---------------------------

        /// <summary>The team context in effect.</summary>
        public Func<Team> CurrentTeam { get; set; } = () => Team.Current;
        /// <summary>Records a decision (finding, topic path, decision, reason); returns the file it couldn't write, or null.</summary>
        public Func<StyleFinding, string, string, string, string> RecordFeedback { get; set; } = FeedbackLog.Record;
        /// <summary>Sets a rule in the team's (or the writer's own) guide; returns the file written.</summary>
        public Func<Team, string, string, string, string> SetGuideRule { get; set; } = GuideOverrides.Set;
        /// <summary>Sets a rule for the project of a path (path, finding, setting, reason, by).</summary>
        public Action<string, StyleFinding, string, string, string> SetProjectRule { get; set; } = ProjectRules.Ignore;
        /// <summary>The project overrides file for a path, or null.</summary>
        public Func<string, string> ProjectOverridesPath { get; set; } = ProjectRules.OverridesPath;
        /// <summary>The AI rule names a project ignores.</summary>
        public Func<string, HashSet<string>> IgnoredAiRules { get; set; } = ProjectRules.IgnoredAiRules;
        /// <summary>Why a file can't be written, or null.</summary>
        public Func<string, string> WhyNotWritable { get; set; } = BatchPlan.WhyNotWritable;
        /// <summary>How many topics use a snippet.</summary>
        public Func<string, int> SnippetUsage { get; set; } = ActionRunner.SnippetUsage;
        /// <summary>
        /// The document at a path when it is open in the editor, with its current DOM, without opening or activating it;
        /// null when it isn't open. Used to notice that a fix written in another file was undone there (Ctrl+Z). Null: the
        /// documents this session wrote in are asked directly.
        /// </summary>
        public Func<string, IDocumentAdapter> OpenedDocument { get; set; }
        /// <summary>Sends a request to the owners; returns why it only reached the outbox, or null when it was delivered.</summary>
        public Func<Team, GuideRequest, string> SendRequest { get; set; } = (team, r) => { string problem; GuideRequests.Send(team, r, out problem); return problem; };
        /// <summary>The writer's request check (outcomes, approved project exceptions, rules still waiting).</summary>
        public Func<Team, RequestCheck> SyncRequests { get; set; } = GuideRequests.Check;
        /// <summary>
        /// Runs a batch that is ready to write. The default writes every file at once; the pane steps it file by file
        /// between messages, with progress and Stop.
        /// </summary>
        public Action<BatchRun> RunBatch { get; set; } = run => run.RunToEnd();
        /// <summary>True while the pane showing this session is the one on screen. Null means always (tests).</summary>
        public Func<bool> IsShown { get; set; }

        // ---- state the view reads ---------------------------------------------------------------

        /// <summary>The suggestions, in reading order (by file in a project report).</summary>
        public ReadOnlyCollection<StyleFinding> Findings { get { return _findings.AsReadOnly(); } }

        public IDocumentAdapter Topic { get { return _doc; } }
        public bool IsProjectReport { get { return _project; } }

        /// <summary>The selected suggestion, or null. The view keeps it in step with its list.</summary>
        public StyleFinding Current { get; set; }

        /// <summary>The last status line the session wrote.</summary>
        public string Status
        {
            get { return _status; }
            private set { _status = value; var h = StatusChanged; if (h != null) h(value); }
        }

        /// <summary>The list changed (added, applied, removed, re-sorted): redraw it, keeping <see cref="Current"/> selected.</summary>
        public event Action ListChanged;
        /// <summary>The session moved the selection (null clears it).</summary>
        public event Action<StyleFinding> SelectRequested;
        public event Action<string> StatusChanged;
        /// <summary>A batch finished: the per-file outcome, for the pane to show when it has more to say than the status line.</summary>
        public event Action<BatchResult> BatchReported;
        /// <summary>Something the writer did that the guided first lint and the getting-started checklist follow.</summary>
        public event Action<ReviewEvent> Happened;

        /// <summary>The topic's empty state.</summary>
        public const string NoIssuesInTopic = "No issues found in this topic.";
        private const string TextChanged = "The text changed. Lint the paragraph again.";
        private const string UndoneBack = "The change was undone. The issue is back.";

        private void Raise(ReviewEvent e)
        {
            var h = Happened;
            if (h == null) return;
            try { h(e); } catch (Exception ex) { Diagnostics.Log("review event " + e, ex); }
        }

        // ---- the list ---------------------------------------------------------------------------

        /// <summary>Forgets every suggestion (a new check starts).</summary>
        public void Clear()
        {
            _findings.Clear();
            _exemptSeen.Clear();
            _skippedRules.Clear();
            FocusRule = null;
            Current = null;
        }

        /// <summary>Adds suggestions, leaving out rejected ones and AI rules the project ignores, and labels them by guide section.</summary>
        public void Add(IEnumerable<StyleFinding> found)
        {
            var ignoredAi = IgnoredAi();
            var added = new List<StyleFinding>();
            var exemptions = ExemptionList();
            foreach (var f in found)
            {
                if (_dismissed != null && _dismissed.IsDismissed(f, _doc.SourcePath)) continue;
                if (exemptions.Count > 0 && !string.IsNullOrEmpty(f.Excerpt))
                {
                    var topicKey = Exemptions.TopicKey(f, _doc.SourcePath);
                    if (topicKey != null && Exemptions.Covers(exemptions, topicKey, Exemptions.RuleKey(f), f.Excerpt, f.Before ?? ""))
                    {
                        _exemptSeen.Add(ExemptId(topicKey, f));
                        continue;
                    }
                }
                if (f.Layer == "ai" && ignoredAi.Contains(f.Rule ?? "")) continue;
                if (f.Layer != "ai" && f.RuleSection == null)
                {
                    var rule = _runner.RuleFor(f.RuleId);   // company/project table rules: label by guide section
                    if (rule != null) f.RuleSection = rule.Section;
                }
                if (f.RuleText == null && f.RuleId != null)
                {
                    var cited = _runner.RuleFor(f.RuleId);   // its wording now: feedback records a fingerprint of it
                    if (cited != null) f.RuleText = cited.Text;
                }
                if (_requested.Contains(f.RuleId ?? f.Rule ?? "")) f.RequestSent = true;   // still visible while the owners decide
                if (!string.IsNullOrEmpty(f.Excerpt) && _findings.Exists(m => m.MarkedFixed && !string.IsNullOrEmpty(m.Excerpt) &&
                        m.RuleId == f.RuleId && m.DocumentPath == f.DocumentPath && HandFixes.ContextKey(m) == HandFixes.ContextKey(f))) continue;
                _findings.Add(f);
                added.Add(f);
            }
            var addDoms = new Dictionary<string, XmlDocument>(StringComparer.OrdinalIgnoreCase);
            var addTexts = new Dictionary<XmlNode, string>();
            foreach (var f in added)
            {
                if (f.SeenInBody != -1) continue;
                try { Record(f, DomOf(f, addDoms), addTexts); }
                catch (Exception ex) { Diagnostics.Log("record baseline: " + (f.DocumentPath ?? "") + " " + ex.GetType().Name); }
            }
            Sort();
        }

        /// <summary>
        /// Live check: replaces the topic's style-rule and structure findings with fresh ones (the author typed); AI findings
        /// stay, and any whose text changed show as stale. Not in a project report.
        /// </summary>
        public void RefreshLint(List<StyleFinding> lint)
        {
            if (_project) return;
            RecheckApplied();   // an undone fix is live again, so the fresh lint below replaces it instead of duplicating it
            _findings.RemoveAll(f => f.Source != "ai" && f.DocumentPath == null && !f.IsFixed);
            var entry = string.IsNullOrEmpty(_doc.SourcePath) ? null : LintExclusions.EntryFor(_doc.SourcePath, false);
            if (entry != null)
            {
                var prefix = entry.Replace('/', '\\').ToUpperInvariant() + "\u0001";
                _exemptSeen.RemoveWhere(id => id.StartsWith(prefix, StringComparison.Ordinal));   // the fresh lint counts them again
            }
            Add(lint);
        }

        private static string ExemptId(string topic, StyleFinding f)
        {
            return topic.Replace('/', '\\').ToUpperInvariant() + "\u0001" + Exemptions.RuleKey(f) + "\u0001" + f.Excerpt + "\u0001" + (f.Before ?? "");
        }

        /// <summary>How many issues the exemptions hid from this list (each place once).</summary>
        public int ExemptHidden { get { return _exemptSeen.Count; } }

        /// <summary>"2 issues are exempt." for the status line, or "" when none are hidden.</summary>
        public string ExemptTail
        {
            get
            {
                var n = _exemptSeen.Count;
                return n == 0 ? "" : Plural.Of(n, "issue") + (n == 1 ? " is" : " are") + " exempt.";
            }
        }

        /// <summary>The project's exemptions; the file is re-read only when its write time changed.</summary>
        private IList<Exemption> ExemptionList()
        {
            var file = string.IsNullOrEmpty(_doc.SourcePath) ? null : Exemptions.FileFor(_doc.SourcePath);
            if (file == null) { _exemptionsFile = null; return _exemptions = new List<Exemption>(); }
            DateTime stamp;
            try { stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { stamp = DateTime.MinValue; }
            if (file != _exemptionsFile || stamp != _exemptionsStamp)
            {
                _exemptionsFile = file;
                _exemptionsStamp = stamp;
                _exemptions = stamp == DateTime.MinValue ? new List<Exemption>() : Exemptions.Load(_doc.SourcePath);
            }
            return _exemptions;
        }

        /// <summary>True when the selected-kind finding can be exempted: it has text, a topic in the project, and a rule with a permanent id (or a structure check).</summary>
        public bool CanExempt(StyleFinding f)
        {
            if (f == null || string.IsNullOrEmpty(f.Excerpt)) return false;
            if (Exemptions.TopicKey(f, _doc.SourcePath) == null) return false;
            return !string.IsNullOrEmpty(f.RuleId) || (f.Rule != null && f.Rule.StartsWith("Structure.", StringComparison.Ordinal));
        }

        /// <summary>Exempt this text from its rule on this topic, for everyone on the project; the rule keeps checking the rest.</summary>
        public void Exempt(StyleFinding f, string reason)
        {
            if (f == null || !CanExempt(f)) return;
            var topic = Exemptions.TopicKey(f, _doc.SourcePath);
            var team = CurrentTeam();
            Exemptions.Add(_doc.SourcePath, topic, Exemptions.RuleKey(f), f.Excerpt, f.Before ?? "", reason, team == null ? "" : team.FeedbackWriter);
            _exemptSeen.Add(ExemptId(topic, f));
            var next = NextAfter(f);
            _findings.Remove(f);
            Sort();
            Select(next);
            Status = Summary() + "  Exempted. It stays hidden for this text for everyone on this project.";
        }

        /// <summary>Lint Project: drops the suggestions in files the project now leaves out (<see cref="LintExclusions"/>).</summary>
        public void RemoveFiles(Func<string, bool> excluded)
        {
            _findings.RemoveAll(f => f.DocumentPath != null && excluded(f.DocumentPath));
            if (Current != null && !_findings.Contains(Current)) Current = null;
            Sort();
        }

        /// <summary>Re-sorts into reading order (which also marks suggestions whose text is gone as stale) and tells the view.</summary>
        public void Sort()
        {
            if (!_project) ReviewOrder.Annotate(_doc.Document, _findings);
            _findings.Sort((a, b) => _project
                ? string.Compare(a.DocumentPath, b.DocumentPath, StringComparison.OrdinalIgnoreCase)
                : a.Order.CompareTo(b.Order));
            var h = ListChanged;
            if (h != null) h();
        }

        /// <summary>Suggestions not yet applied whose text is still there.</summary>
        public List<StyleFinding> Open() { return _findings.FindAll(f => !f.IsFixed && !f.IsStale); }

        /// <summary>Open proposals in the topic itself (not in snippets), for the editor markers.</summary>
        public List<StyleFinding> OpenTopicFindings() { return _findings.FindAll(f => !f.IsFixed && !f.IsStale && f.DocumentPath == null); }

        /// <summary>What "Apply all fixes" would apply: exact rule fixes in the topic (in a project report: everywhere).</summary>
        public List<StyleFinding> FixAllCandidates()
        {
            return _findings.FindAll(f => f.CanFix && f.Kind == "mechanical" && (f.DocumentPath == null || _project));
        }

        /// <summary>What "Write all as tracked changes" would write: every exact suggestion in the topic.</summary>
        public List<StyleFinding> WriteAllCandidates()
        {
            return _findings.FindAll(f => f.CanFix && (f.Kind == "mechanical" || f.Kind == "wording") && f.DocumentPath == null);
        }

        /// <summary>
        /// "5 issues in 2 sections. 2 Auto-fix, 3 Needs you." for a topic (the card's issue types); for a project check, the backlog
        /// line ("117 suggestions in 42 files. 63 can be fixed automatically across 9 rules.").
        /// </summary>
        public string Summary()
        {
            if (_project) return BacklogSummary();
            var open = Open();
            if (open.Count == 0) return NoIssuesInTopic;
            int fixes = 0, needsYou = 0;
            var sections = new HashSet<string>();
            foreach (var f in open)
            {
                sections.Add(f.Section ?? "");
                if (f.Kind == "mechanical") fixes++; else needsYou++;
            }
            var parts = new List<string>();
            if (fixes > 0) parts.Add(fixes + " Auto-fix");
            if (needsYou > 0) parts.Add(needsYou + " Needs you");
            return Plural.Of(open.Count, "issue") + (sections.Count > 1 ? " in " + sections.Count + " sections" : "") + ". "
                   + Cap(string.Join(", ", parts.ToArray())) + ".";
        }

        /// <summary>How many topics use a snippet (asked once per path).</summary>
        public int SnippetUses(string path)
        {
            int uses;
            if (!_usage.TryGetValue(path, out uses)) _usage[path] = uses = SnippetUsage(path);
            return uses;
        }

        /// <summary>The use count if it is already known (the list's group headings don't scan the project).</summary>
        public bool KnownSnippetUses(string path, out int uses) { return _usage.TryGetValue(path, out uses); }

        /// <summary>Counts snippet uses ahead of time, off the UI thread, for the findings that live in snippets.</summary>
        public void PrimeSnippetUses(IEnumerable<StyleFinding> found)
        {
            foreach (var f in found)
                if (f.DocumentPath != null && !_usage.ContainsKey(f.DocumentPath)) _usage[f.DocumentPath] = SnippetUsage(f.DocumentPath);
        }

        // ---- selection --------------------------------------------------------------------------

        /// <summary>The author selected a row: a suggestion in the topic is selected in the editor too (quietly).</summary>
        public void OnSelected(StyleFinding f)
        {
            if (f != null && f.IsFixed) RecheckApplied();
            Current = f;
            if (f != null && f.DocumentPath == null) GoTo(f, false);
        }

        /// <summary>Selects a suggestion (null clears the selection).</summary>
        public void Select(StyleFinding f)
        {
            Current = f != null && _findings.Contains(f) ? f : null;
            var h = SelectRequested;
            if (h != null) h(f);
        }

        /// <summary>
        /// Selects the next open proposal after the current one (wrapping), or clears the selection. Also "Skip for now".
        /// While one rule is reviewed one by one, only that rule's suggestions; after its last one, back to all rules.
        /// </summary>
        public void Next()
        {
            var next = NextAfter(Current);
            if (next == null && FocusRule != null)
            {
                var cur = Current;
                if (cur != null && !cur.IsFixed && !cur.IsStale && RuleBacklog.KeyOf(cur) == FocusRule)
                {
                    Select(cur);   // Skip for now on the rule's last one: it stays, nothing else to go to
                    Status = "This is the last issue for " + FocusTitle + ".";
                    return;
                }
                var done = FocusTitle;
                EndRuleReview();
                Status = Summary() + "  Done with " + done + ".";
                return;
            }
            Select(next);
        }

        public StyleFinding NextAfter(StyleFinding cur)
        {
            var open = Open();
            if (open.Count == 0) return null;
            var start = cur == null ? -1 : _findings.IndexOf(cur);
            for (var i = 1; i <= _findings.Count; i++)
            {
                var f = _findings[(start + i + _findings.Count) % _findings.Count];
                if (f != cur && !f.IsFixed && !f.IsStale && (FocusRule == null || RuleBacklog.KeyOf(f) == FocusRule)) return f;
            }
            return null;
        }

        /// <summary>
        /// Applied fixes the editor took back (Ctrl+Z, or Reject change in the Review tab): when the original text is
        /// back where it was, the suggestion is live again; when it is back but its surroundings changed too, it shows
        /// "Text changed: check this paragraph again". A fix whose text just isn't found stays applied (accepted, or
        /// edited away on purpose), and so does one the writer left alone while editing elsewhere. One locate per applied
        /// suggestion; called when the document changes, a row is selected or Apply is pressed. Returns how many changed.
        /// </summary>
        public int RecheckApplied()
        {
            var revived = 0; var stale = 0;
            var doms = new Dictionary<string, XmlDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in _findings)
            {
                if (!f.IsFixed || !f.Written || f.Replacement == null || string.IsNullOrEmpty(f.Excerpt)) continue;
                var dom = DomOf(f, doms);   // the topic, or another file (a project check, a snippet) while it is open
                if (dom == null) continue;
                XmlText run; int start; bool exact;
                if (!FindingLocator.TryLocate(dom, f, out run, out start, out exact)) continue;
                if (!exact && AppliedTextPresent(f, dom)) continue;   // the same words elsewhere; our fix is still in place
                f.IsFixed = false; f.Written = false;
                f.IsStale = f.ContextChanged = !exact;
                if (exact) revived++; else stale++;
                Record(f, dom, new Dictionary<XmlNode, string>());   // the count now is the new baseline for the check of hand fixes
            }
            if (revived + stale == 0) return 0;
            Sort();
            Status = stale > 0 ? TextChanged : UndoneBack;
            if (revived > 0) Raise(ReviewEvent.Undone);
            return revived + stale;
        }

        /// <summary>
        /// The current DOM of the file an applied fix lives in: the checked topic, or another file that is still open in the
        /// editor (asked through <see cref="OpenedDocument"/>, else the document the fix was written in). Null when that file
        /// isn't open any more: its undo history is gone, so the fix stays applied.
        /// </summary>
        private XmlDocument DomOf(StyleFinding f, Dictionary<string, XmlDocument> seen)
        {
            var path = f.DocumentPath;
            if (path == null) return _doc.Document;
            var checkedTopic = string.Equals(path, _doc.SourcePath, StringComparison.OrdinalIgnoreCase);
            XmlDocument dom;
            if (seen.TryGetValue(path, out dom)) return dom;
            try
            {
                IDocumentAdapter d = null;
                if (OpenedDocument != null) d = OpenedDocument(path);
                else if (!_writtenIn.TryGetValue(path, out d) && checkedTopic) d = _doc;
                dom = d == null ? (checkedTopic ? _doc.Document : null) : d.Document;
            }
            catch (Exception ex) { Diagnostics.Log("recheck: " + path + " " + ex.GetType().Name); dom = null; }
            if (dom == null) _writtenIn.Remove(path);
            seen[path] = dom;
            return dom;
        }

        // ---- issues the writer fixed by hand ----------------------------------------------------

        private static string TextOf(XmlNode scope, Dictionary<XmlNode, string> texts)
        {
            string t;
            if (!texts.TryGetValue(scope, out t)) texts[scope] = t = HandFixes.ScopeText(scope);
            return t;
        }

        /// <summary>Remembers how often the issue's text is in its paragraph and in the file's body right now.</summary>
        private void Record(StyleFinding f, XmlDocument dom, Dictionary<XmlNode, string> texts)
        {
            if (string.IsNullOrEmpty(f.Excerpt) || dom == null) return;
            var body = BlockSelector.FindBody(dom);
            if (body == null) return;
            f.SeenInBody = HandFixes.Count(TextOf(body, texts), f.Excerpt);
            f.Seen = HandFixes.IsAttached(f) ? HandFixes.Count(TextOf(f.Block, texts), f.Excerpt) : f.SeenInBody;
        }

        private sealed class EditGroup
        {
            public XmlNode Scope;
            public string Excerpt;
            public bool Body;
            public XmlDocument Dom;
            public string File;
            public readonly List<StyleFinding> Members = new List<StyleFinding>();
        }

        /// <summary>
        /// Compares how often each issue's text is in its paragraph (or file) with the last check. Fewer: the issues whose
        /// own surroundings are gone are fixed by the writer's edit. More: issues marked that way come back (Ctrl+Z).
        /// </summary>
        private void CheckEdits(string path, XmlDocument given, List<StyleFinding> marked, out int reopened)
        {
            reopened = 0;
            var doms = new Dictionary<string, XmlDocument>(StringComparer.OrdinalIgnoreCase);
            var texts = new Dictionary<XmlNode, string>();
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groups = new List<EditGroup>();
            foreach (var f in _findings)
            {
                if (string.IsNullOrEmpty(f.Excerpt) || f.MarkedFixed || (f.IsFixed && !f.FixedByEdit)) continue;
                if (path != null && !HandFixes.SamePath(FilePathOf(f), path)) continue;
                var file = FilePathOf(f) ?? "";
                if (failed.Contains(file)) continue;
                try
                {
                    var dom = given != null ? given : DomOf(f, doms);
                    if (dom == null) continue;
                    var inBlock = f.DocumentPath == null && HandFixes.IsAttached(f);
                    XmlNode scope = inBlock ? f.Block : BlockSelector.FindBody(dom);
                    if (scope == null) continue;
                    var g = groups.Find(x => x.Scope == scope && x.Excerpt == f.Excerpt);
                    if (g == null)
                    {
                        g = new EditGroup { Scope = scope, Excerpt = f.Excerpt, Body = !inBlock, Dom = dom, File = file };
                        groups.Add(g);
                    }
                    g.Members.Add(f);
                }
                catch (Exception ex)
                {
                    if (failed.Add(file)) Diagnostics.Log("recheck edits: " + file + " " + ex.GetType().Name);
                }
            }
            foreach (var g in groups)
            {
                if (failed.Contains(g.File)) continue;
                try
                {
                    var text = TextOf(g.Scope, texts);
                    var c = HandFixes.Count(text, g.Excerpt);
                    var p = -1;
                    foreach (var m in g.Members)
                    {
                        var b = g.Body ? m.SeenInBody : m.Seen;
                        if (b > p) p = b;
                    }
                    if (p >= 0)
                    {
                        if (c < p)
                        {
                            var candidates = g.Members.FindAll(m => !m.IsFixed && text.IndexOf(HandFixes.ContextKey(m), StringComparison.Ordinal) < 0);
                            if (candidates.Count > 0 && candidates.Count <= p - c)
                                foreach (var m in candidates)
                                {
                                    m.IsFixed = true; m.FixedByEdit = true; m.IsStale = false; m.ContextChanged = false;
                                    marked.Add(m);
                                }
                        }
                        else if (c > p)
                        {
                            var room = c - p;
                            foreach (var m in g.Members)
                            {
                                if (room <= 0) break;
                                if (!m.FixedByEdit || text.IndexOf(HandFixes.ContextKey(m), StringComparison.Ordinal) < 0) continue;
                                m.IsFixed = false; m.FixedByEdit = false;
                                reopened++; room--;
                            }
                        }
                    }
                    foreach (var m in g.Members) Record(m, g.Dom, texts);
                    // Rebind: an issue whose paragraph was replaced finds its text again, so the next check compares that paragraph.
                    foreach (var m in g.Members)
                    {
                        if (m.DocumentPath != null || m.IsFixed || m.Block == null || HandFixes.IsAttached(m)) continue;
                        XmlText run; int start;
                        m.Block = FindingLocator.TryLocate(g.Dom, m, out run, out start) ? TextRuns.BlockOf(run) : null;
                        Record(m, g.Dom, texts);
                    }
                }
                catch (Exception ex)
                {
                    if (failed.Add(g.File)) Diagnostics.Log("recheck edits: " + g.File + " " + ex.GetType().Name);
                }
            }
        }

        /// <summary>Issues the writer fixed by typing show as fixed. Returns how many. Optionally only for one file.</summary>
        public int RecheckEdited(string path = null)
        {
            var marked = new List<StyleFinding>(); int reopened;
            CheckEdits(path, null, marked, out reopened);
            if (marked.Count + reopened == 0) return 0;
            Sort();
            Status = marked.Count > 0 ? Summary() + "  You fixed " + Plural.Of(marked.Count, "issue") + "." : UndoneBack;
            return marked.Count;
        }

        /// <summary>Mark as Fixed: the selected issue shows as fixed. Nothing is recorded or hidden, and only Lint Again brings it back.</summary>
        public void MarkFixed()
        {
            var f = Current;
            if (f == null || f.IsFixed) return;
            f.IsFixed = true; f.MarkedFixed = true; f.FixedByEdit = false; f.IsStale = false; f.ContextChanged = false;
            Sort();
            Next();
            Status = Summary() + "  Marked as fixed.";
        }

        /// <summary>Starts or stops telling House Style's own writes from the writer's edits.</summary>
        public void WatchWrites(bool on)
        {
            ActionRunner.Writing -= OnWriting;
            ActionRunner.Wrote -= OnWrote;
            if (on)
            {
                ActionRunner.Writing += OnWriting;
                ActionRunner.Wrote += OnWrote;
                _watching = true;
                _uiThread = Thread.CurrentThread.ManagedThreadId;
            }
            else _watching = false;
        }

        private bool WriteGuard()
        {
            if (!_watching) return false;
            if (IsShown != null && !IsShown()) return false;
            if (Thread.CurrentThread.ManagedThreadId != _uiThread)
            {
                if (!_offThreadLogged) { _offThreadLogged = true; Diagnostics.Log("write event off the UI thread"); }
                return false;
            }
            return true;
        }

        private void OnWriting(string path)
        {
            if (!WriteGuard()) return;
            try
            {
                var marked = new List<StyleFinding>(); int reopened;
                CheckEdits(path, null, marked, out reopened);
                _pendingMarked += marked.Count; _pendingReopened += reopened;
            }
            catch (Exception ex) { Diagnostics.Log("recheck before write: " + path + " " + ex.GetType().Name); }
        }

        private void OnWrote(string path)
        {
            if (!WriteGuard()) return;
            try
            {
                var doms = new Dictionary<string, XmlDocument>(StringComparer.OrdinalIgnoreCase);
                var texts = new Dictionary<XmlNode, string>();
                foreach (var f in _findings)
                {
                    if (string.IsNullOrEmpty(f.Excerpt)) continue;
                    if (path != null && !HandFixes.SamePath(FilePathOf(f), path)) continue;
                    var dom = DomOf(f, doms);
                    if (dom == null) continue;
                    if (f.DocumentPath == null && !f.IsFixed && f.Block != null && !HandFixes.IsAttached(f))
                    {
                        XmlText run; int start;
                        f.Block = FindingLocator.TryLocate(dom, f, out run, out start) ? TextRuns.BlockOf(run) : null;
                    }
                    Record(f, dom, texts);
                }
                if (_pendingMarked + _pendingReopened > 0) Sort();
            }
            catch (Exception ex) { Diagnostics.Log("record after write: " + path + " " + ex.GetType().Name); }
            finally { _pendingMarked = 0; _pendingReopened = 0; }
        }

        /// <summary>True when the report has issues in this file.</summary>
        public bool Covers(string path)
        {
            return path != null && _findings.Exists(f => f.DocumentPath != null && HandFixes.SamePath(f.DocumentPath, path));
        }

        /// <summary>A file in a project report was saved: issues the writer fixed in it show as fixed. Returns how many.</summary>
        public int RecheckSaved(string path, XmlDocument dom)
        {
            if (!_project || dom == null || SelfSaves.IsOwn(path) || !Covers(path)) return 0;
            var marked = new List<StyleFinding>(); int reopened;
            CheckEdits(path, dom, marked, out reopened);
            if (marked.Count + reopened == 0) return 0;
            Sort();
            Status = marked.Count > 0 ? Summary() + "  You fixed " + Plural.Of(marked.Count, "issue") + "." : UndoneBack;
            return marked.Count;
        }

        /// <summary>A moment after typing stops: undone changes come back, hand fixes show as fixed, then Live check's lint (or null).</summary>
        public void AfterEdit(List<StyleFinding> lint)
        {
            RecheckApplied();
            var marked = new List<StyleFinding>(); int reopened;
            CheckEdits(null, null, marked, out reopened);
            if (marked.Count + reopened > 0) Sort();
            if (lint != null && !_project) RefreshLint(lint);
            // A rule issue the writer only partly changed, which this pause's fresh lint lists again in the same paragraph, isn't counted.
            var n = marked.FindAll(m => !(lint != null && m.RuleId != null && m.Block != null &&
                                          _findings.Exists(x => !x.IsFixed && x.RuleId == m.RuleId && x.Block == m.Block))).Count;
            if (n > 0) Status = Summary() + "  You fixed " + Plural.Of(n, "issue") + ".";
            else if (marked.Count > 0) Status = Summary();
            else if (reopened > 0) Status = UndoneBack;
        }

        /// <summary>The fix's new text, preceded by a few characters of its old context, is in the finding's paragraph.</summary>
        private bool AppliedTextPresent(StyleFinding f, XmlDocument dom)
        {
            var scope = new List<XmlNode>();
            if (f.Block != null && TextRuns.BlockOf(f.Block) == f.Block) scope.Add(f.Block);
            else { var body = BlockSelector.FindBody(dom); if (body != null) scope.Add(body); }
            var sb = new System.Text.StringBuilder();
            foreach (var r in TextRuns.Of(scope)) sb.Append(r.Value);
            var before = f.Before ?? "";
            var context = before.Length <= 12 ? before : before.Substring(before.Length - 12);
            return sb.ToString().IndexOf(context + f.Replacement, StringComparison.Ordinal) >= 0;
        }

        // ---- files ------------------------------------------------------------------------------

        /// <summary>The file a suggestion lives in: its snippet or topic, or the checked topic.</summary>
        public string FilePathOf(StyleFinding f) { return f == null ? null : f.DocumentPath ?? _doc.SourcePath; }

        /// <summary>Open file button and Shift+Enter: opens the selected suggestion's file in Flare without selecting text.</summary>
        public IDocumentAdapter OpenFileOfCurrent() { return OpenFile(Current); }

        /// <summary>Opens (or brings to the front) the file a suggestion lives in, without selecting text.</summary>
        public IDocumentAdapter OpenFile(StyleFinding f)
        {
            var path = FilePathOf(f);
            if (path == null) return null;
            IDocumentAdapter doc = null;
            try { doc = _host.OpenDocument(path); } catch (Exception ex) { Diagnostics.Log("OpenFile", ex); }
            Status = doc == null ? "Could not open " + path : "Opened " + System.IO.Path.GetFileName(path) + ".";
            return doc;
        }

        /// <summary>Go to button and Ctrl+Enter: open the topic or snippet and select the flagged text.</summary>
        public void GoToCurrent()
        {
            var f = Current;
            if (f != null) GoTo(f, true);
        }

        /// <summary>Selects the finding's text in its document; <paramref name="report"/> opens a snippet and says when the text is gone.</summary>
        public void GoTo(StyleFinding f, bool report)
        {
            if (f.IsFixed) return;
            var doc = DocOf(f, report);
            if (doc == null) { if (report) Status = "Could not open " + f.DocumentPath; return; }
            var ok = false;
            try { ok = _runner.GoTo(doc, f); } catch (Exception ex) { Diagnostics.Log("GoTo", ex); }
            if (!ok && report) Status = "That text is no longer in the topic. Lint the paragraph again.";
        }

        // ---- decisions --------------------------------------------------------------------------

        /// <summary>Apply (Enter, double-click): writes the selected suggestion, or says what the author must do instead.</summary>
        public AcceptOutcome Accept()
        {
            var f = Current;
            if (f != null && f.IsFixed) RecheckApplied();   // Ctrl+Z since: it may be live again
            if (f == null || f.IsFixed) return AcceptOutcome.None;
            if (f.IsStale) { Status = TextChanged; return AcceptOutcome.Stale; }
            if (f.Kind == "variable")
            {
                GoTo(f, true);
                Status = "The text is selected. On the Insert tab, click Variable and choose " + f.Variable + ".";
                return AcceptOutcome.WentTo;
            }
            if (f.Kind == "restructure") return AcceptOutcome.Rewrite;
            if (f.Rule == "Structure.AltText" && f.DocumentPath == null) return AcceptOutcome.AltText;
            if (f.Kind == "manual")
            {
                GoTo(f, true);
                Status = "Change it in the topic, then lint the paragraph again.";
                return AcceptOutcome.WentTo;
            }
            if (!ConfirmSnippet(f)) return AcceptOutcome.Declined;
            if (Apply(new List<StyleFinding> { f }, "House Style: " + f.Rule) > 0)
            {
                RecordFeedback(f, _doc.SourcePath, "accepted", null);
                Next();
                return AcceptOutcome.Applied;
            }
            if (f.FixedByEdit)
            {
                Status = Summary() + "  You fixed 1 issue.";
                Next();
                return AcceptOutcome.None;
            }
            return AcceptOutcome.NotApplied;
        }

        /// <summary>One question before a snippet changes: it is shared, so every topic that includes it changes too.</summary>
        public bool ConfirmSnippet(StyleFinding f)
        {
            if (f.DocumentPath == null || !f.DocumentPath.EndsWith(".flsnp", StringComparison.OrdinalIgnoreCase)) return true;
            return _host.Confirm(BatchPlan.SnippetQuestion(f.DocumentPath, SnippetUses(f.DocumentPath)) + "\n\nThe change is tracked.", null);
        }

        /// <summary>Fix All: exact rule fixes as tracked changes; a batch that reaches another file is previewed first.</summary>
        public void FixAll()
        {
            FixBatch(FixAllCandidates(), false);
        }

        /// <summary>What Change All writes for <paramref name="f"/>: every automatic change of the same rule (in this topic, or everywhere in a project report).</summary>
        public List<StyleFinding> ChangeAllCandidates(StyleFinding f)
        {
            if (f == null || !RuleBacklog.IsAutomatic(f)) return new List<StyleFinding>();
            var key = RuleBacklog.KeyOf(f);
            return FixAllCandidates().FindAll(x => RuleBacklog.KeyOf(x) == key);
        }

        /// <summary>Change All: every instance of the selected issue's rule, as tracked changes in one undo step.</summary>
        public void ChangeAll()
        {
            FixBatch(ChangeAllCandidates(Current), false);
        }

        /// <summary>
        /// Previews and writes a batch of exact fixes. One policy for every batch: anything that reaches a file other than
        /// the open topic is previewed first (<paramref name="alwaysPreview"/>: always); a batch only in the open topic
        /// writes directly (one Ctrl+Z). Nothing is written when the preview is cancelled.
        /// </summary>
        private void FixBatch(List<StyleFinding> todo, bool alwaysPreview)
        {
            if (todo.Count == 0) return;
            var plan = BatchPlan.Build(todo, _doc.SourcePath, WhyNotWritable, SnippetUses);
            if ((alwaysPreview || plan.NeedsPreview || plan.Skipped.Count > 0) && plan.Groups.Count > 0)
            {
                if (!_host.PreviewBatch(plan)) { Status = Summary() + "  Nothing was changed."; return; }
            }
            var chosen = plan.SelectedFindings;
            if (chosen.Count == 0 && plan.Skipped.Count == 0) return;
            var run = BeginBatch(chosen, chosen.Count == 1 ? "House Style: " + chosen[0].Rule : "House Style: " + chosen.Count + " changes", plan);
            run.Finished += result =>
            {
                foreach (var f in chosen) if (f.IsFixed && !f.FixedByEdit) RecordFeedback(f, _doc.SourcePath, "accepted", null);
                var inSnippets = _findings.FindAll(f => f.CanFix && f.DocumentPath != null && !_project).Count;
                if (inSnippets > 0) Status = Status + "  " + inSnippets + " more are in snippets. Select one to change it there.";
                if (FocusRule != null && !_findings.Exists(f => !f.IsFixed && !f.IsStale && RuleBacklog.KeyOf(f) == FocusRule)) EndRuleReview();
                else Next();
            };
            RunBatch(run);
        }

        // ---- the backlog: a project check, one rule at a time -----------------------------------

        /// <summary>The rule being reviewed one by one, or null. Next, Skip and Apply stay within it.</summary>
        public string FocusRule { get; private set; }

        /// <summary>The focused rule's heading, or null.</summary>
        public string FocusTitle
        {
            get
            {
                if (FocusRule == null) return null;
                var f = _findings.Find(x => RuleBacklog.KeyOf(x) == FocusRule);
                return f == null ? "this rule" : RuleBacklog.Group(new[] { f }, _runner.RuleFor, _doc.SourcePath, _runner.SourceName)[0].Title;
            }
        }

        /// <summary>Open suggestions grouped under their rule, most frequent first, without the rules skipped for now.</summary>
        public List<BacklogRule> Backlog()
        {
            return RuleBacklog.Group(Open().FindAll(f => !_skippedRules.Contains(RuleBacklog.KeyOf(f))), _runner.RuleFor, _doc.SourcePath, _runner.SourceName);
        }

        /// <summary>The rule a key names in the backlog now, or null (fixed, turned off or skipped since).</summary>
        public BacklogRule FindBacklogRule(string key) { return Backlog().Find(r => r.Key == key); }

        /// <summary>"117 suggestions in 42 files. 63 can be fixed automatically across 9 rules."</summary>
        public string BacklogSummary()
        {
            return RuleBacklog.Summary(Open().FindAll(f => !_skippedRules.Contains(RuleBacklog.KeyOf(f))), _doc.SourcePath, _skippedRules.Count);
        }

        /// <summary>What "Fix all automatic suggestions" writes: every exact rule fix, except rules skipped for now.</summary>
        public List<StyleFinding> AutomaticCandidates()
        {
            return FixAllCandidates().FindAll(f => !_skippedRules.Contains(RuleBacklog.KeyOf(f)));
        }

        /// <summary>Fix all automatic suggestions…: every exact rule fix in every file, after the preview.</summary>
        public void FixAllAutomatic()
        {
            var todo = AutomaticCandidates();
            if (todo.Count == 0) { Status = Summary() + "  Nothing can be changed automatically."; return; }
            FixBatch(todo, true);
        }

        /// <summary>Fix all for one rule: its exact fixes in every file, as tracked changes, after the preview.</summary>
        public void FixRule(BacklogRule rule)
        {
            if (rule == null) return;
            var todo = rule.Automatic;
            if (todo.Count == 0) { Status = Summary() + "  This rule has no automatic changes. Click Review One by One."; return; }
            FixBatch(todo, true);
        }

        /// <summary>Review one by one: steps through this rule's suggestions only, starting with the first.</summary>
        public void ReviewRule(BacklogRule rule)
        {
            if (rule == null || rule.Findings.Count == 0) return;
            FocusRule = rule.Key;
            var h = ListChanged;
            if (h != null) h();
            Select(rule.Findings[0]);
            Status = rule.Title + " · " + rule.Counts() + ". Change or skip each issue for this rule.";
        }

        /// <summary>Back to all rules (Review one by one ends).</summary>
        public void EndRuleReview()
        {
            if (FocusRule == null) return;
            FocusRule = null;
            Current = null;
            var h = ListChanged;
            if (h != null) h();
            Select(null);
        }

        /// <summary>Skip: the rule's group is hidden for this session (Check again shows it again). Nothing is recorded.</summary>
        public void SkipRule(BacklogRule rule)
        {
            if (rule == null) return;
            _skippedRules.Add(rule.Key);
            if (FocusRule == rule.Key) FocusRule = null;
            if (Current != null && RuleBacklog.KeyOf(Current) == rule.Key) Current = null;
            var h = ListChanged;
            if (h != null) h();
            Status = Summary() + "  Skipped " + rule.Title + " for now.";
        }

        /// <summary>Shows the skipped rules again.</summary>
        public void ShowSkippedRules()
        {
            if (_skippedRules.Count == 0) return;
            _skippedRules.Clear();
            var h = ListChanged;
            if (h != null) h();
            Status = Summary();
        }

        /// <summary>How many rules are skipped for now.</summary>
        public int SkippedRuleCount { get { return _skippedRules.Count; } }

        /// <summary>True when the finding's rule is skipped for now (Skip in a project report).</summary>
        public bool IsSkipped(StyleFinding f) { return f != null && _skippedRules.Contains(RuleBacklog.KeyOf(f)); }

        /// <summary>Turn off rule… for a whole group: the same path as for one suggestion (owners, requests, other rules on the same words).</summary>
        public void TurnOffRule(BacklogRule rule)
        {
            if (rule == null || rule.Findings.Count == 0) return;
            Current = rule.Findings[0];
            TurnOffRule();
            if (FocusRule != null && !_findings.Exists(f => RuleBacklog.KeyOf(f) == FocusRule)) EndRuleReview();
        }

        /// <summary>Bulk mode: every exact suggestion in the topic becomes a tracked change, for review in Flare's own tools.</summary>
        public void WriteAll()
        {
            var todo = WriteAllCandidates();
            if (todo.Count == 0) return;
            if (!_host.Confirm("Make " + Plural.Of(todo.Count, "change") + " in the topic as tracked changes?\n\n" +
                    "You can accept them on the Review tab. One Ctrl+Z undoes them all.", "Change All Issues")) return;
            var n = Apply(todo, "House Style: " + Plural.Of(todo.Count, "change"));
            foreach (var f in todo) if (f.IsFixed && !f.FixedByEdit) RecordFeedback(f, _doc.SourcePath, "proposed", "bulk");
            Status = Summary() + "  Made " + Plural.Of(n, "tracked change") + ". Accept them on the Review tab. Issues that need you are still listed.";
            Next();
        }

        /// <summary>Applies exact replacements as tracked changes; returns how many were applied.</summary>
        public int Apply(List<StyleFinding> todo, string operation)
        {
            return ApplyBatch(todo, operation, null).FixesApplied;
        }

        /// <summary>
        /// Applies exact replacements as tracked changes, one undo step per document, and reports what happened to each
        /// file. Files that can't be written (read-only, missing) are skipped, not attempted. Snippet fixes open the
        /// snippet (that is where they must be tracked), then the topic is brought back to the front. In a project report,
        /// a file the plugin opened just for this is saved and closed.
        /// </summary>
        public BatchResult ApplyBatch(List<StyleFinding> todo, string operation, BatchPlan plan)
        {
            return BeginBatch(todo, operation, plan).RunToEnd();
        }

        /// <summary>A batch ready to write file by file (see <see cref="BatchRun"/>); nothing is written until it is stepped.</summary>
        public BatchRun BeginBatch(List<StyleFinding> todo, string operation, BatchPlan plan)
        {
            return new BatchRun(this, GroupByDocument(todo), operation, plan, todo.Exists(f => f.DocumentPath != null));
        }

        /// <summary>One file of a batch: one undo step, tracked changes only; skipped when it can't be written.</summary>
        internal void ApplyFile(List<StyleFinding> group, string operation, BatchResult result)
        {
            var path = group[0].DocumentPath ?? _doc.SourcePath;
            var elsewhere = group[0].DocumentPath != null && !string.Equals(path, _doc.SourcePath, StringComparison.OrdinalIgnoreCase);
            try
            {
                if (elsewhere)
                {
                    var why = WhyNotWritable(path);
                    if (why != null) { result.AddSkipped(path, group.Count, why); return; }
                }
                var doc = DocOf(group[0], true);
                if (doc == null) { Diagnostics.Log("Apply: could not open " + path); result.AddFailed(path, group.Count, "could not open it in Flare"); return; }
                var n = _runner.ApplyFixes(doc, group, operation);
                var saved = false;
                // Project-wide: a file opened just for this is saved and closed (one tab per file crashes Flare at ~100).
                if (_project && doc != _doc)
                {
                    try { saved = _host.SaveAndClose(doc); }
                    catch (Exception ex) { Diagnostics.Log("Apply: save/close " + doc.SourcePath, ex); }
                }
                // A file left open keeps its undo history: remember it, so a Ctrl+Z there brings the suggestion back.
                if (doc != _doc) { if (saved) _writtenIn.Remove(path); else if (n > 0) _writtenIn[path] = doc; }
                if (n > 0) { result.AddChanged(path, n, saved); Raise(ReviewEvent.Changed); }
                var handFixed = group.FindAll(x => x.FixedByEdit).Count;
                if (n + handFixed < group.Count) result.AddSkipped(path, group.Count - n - handFixed, "text changed");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Apply " + path, ex);
                result.AddFailed(path, group.Count, ex.Message);
            }
        }

        /// <summary>After the last file of a batch: back to the topic, the list redrawn, and one honest status line.</summary>
        internal void FinishBatch(BatchResult result, bool reachedOtherFiles)
        {
            try
            {
                if (!_project && reachedOtherFiles) _host.OpenDocument(_doc.SourcePath); // back to the topic
                Sort();
                var status = Summary() + "  " + result.Status();
                if (!_project && result.Changed.Exists(e => !string.Equals(e.Path, _doc.SourcePath, StringComparison.OrdinalIgnoreCase)))
                    status += " The snippet stays open in its own tab, unsaved.";
                Status = status;
                var h = BatchReported;
                if (h != null) h(result);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Apply", ex);
                Status = "Couldn't make the change. " + ex.Message;
            }
        }

        /// <summary>Reject with a reason: hidden for this text from now on, recorded for tuning, and the next one is selected.</summary>
        public void Reject(string reason)
        {
            var f = Current;
            if (f == null) return;
            if (_dismissed != null) _dismissed.Dismiss(f, _doc.SourcePath);
            RecordFeedback(f, _doc.SourcePath, "rejected", reason);
            var next = NextAfter(f);
            _findings.Remove(f);
            Sort();
            Select(next);
            Status = Summary() + "  Ignored. It stays hidden for this text.";
        }

        /// <summary>Skip for now: the next suggestion, nothing recorded.</summary>
        public void Skip() { Next(); }

        /// <summary>Edit…: the author changes the suggested text, then it is applied as a tracked change.</summary>
        public void Edit()
        {
            var f = Current;
            if (f == null || f.IsFixed || f.IsStale) return;
            var text = _host.AskText("Edit", "Change \"" + Shorten(f.Excerpt, 80) + "\" to:", f.Replacement ?? f.Excerpt, false);
            if (text == null || text == f.Excerpt) return;
            var edited = Clone(f);
            edited.Replacement = text;
            if (!ConfirmSnippet(f)) return;
            if (Apply(new List<StyleFinding> { edited }, "House Style: " + f.Rule + " (edited)") > 0)
            {
                f.IsFixed = true;
                RecordFeedback(edited, _doc.SourcePath, "edited", f.Replacement);
                Sort();
                Next();
            }
        }

        /// <summary>Refine… came back: "keep it" rejects the suggestion as fine here; anything else replaces it.</summary>
        public void Refined(StyleFinding f, StyleFinding refined, string instruction)
        {
            RecordFeedback(f, _doc.SourcePath, "refined", instruction);
            if (refined.Replacement != null && refined.Replacement == f.Excerpt)
            {
                // The author's instruction amounts to "keep it": nothing to apply.
                Select(f);
                Reject("fine here (refined: " + refined.Suggestion + ")");
                return;
            }
            _findings[_findings.IndexOf(f)] = refined;
            Sort();
            Select(refined);
            Status = Summary() + "  The AI rewrote the suggestion. Change it, edit it, or rewrite it again.";
        }

        /// <summary>The paragraph "Check this paragraph again" would check, or null (the status says why).</summary>
        public XmlNode ReanalyzeTarget(StyleFinding f)
        {
            if (f == null || f.DocumentPath != null) { Status = "Lint Paragraph works on the topic's paragraphs."; return null; }
            var block = _runner.LocateBlock(_doc, f) ?? f.Block;
            if (block == null || block.ParentNode == null) { Status = "That paragraph is gone. Click Lint Again to lint the topic."; return null; }
            return block;
        }

        /// <summary>The paragraph was checked again: its open suggestions (and stale ones from it) give way to the fresh ones.</summary>
        public void Reanalyzed(StyleFinding f, XmlNode block, List<StyleFinding> fresh)
        {
            _findings.RemoveAll(x => !x.IsFixed && x.DocumentPath == null && (x.Block == block || (x.IsStale && x.Block == f.Block)));
            Add(fresh);
            Select(fresh.Count > 0 ? fresh[0] : null);
            Status = Summary() + "  Paragraph linted again. " + Plural.Of(fresh.Count, "issue") + ".";
        }

        /// <summary>The paragraph a Rewrite… of <paramref name="f"/> changes, and every open suggestion in it; null when it can't (the status says why).</summary>
        public XmlNode RewriteTarget(StyleFinding f, out List<StyleFinding> related)
        {
            related = null;
            if (f.DocumentPath != null) { Status = "Open the snippet and use Rewrite there."; return null; }
            var block = _runner.LocateBlock(_doc, f);
            if (block == null) { Status = "That text is no longer in the topic. Click Lint Again."; return null; }
            related = _findings.FindAll(x => !x.IsFixed && x.DocumentPath == null && _runner.LocateBlock(_doc, x) == block);
            return block;
        }

        /// <summary>The rewrite was applied: the suggestions it resolved are done.</summary>
        public void Rewritten(List<StyleFinding> related, string appliedSummary)
        {
            foreach (var x in related) { x.IsFixed = true; RecordFeedback(x, _doc.SourcePath, "accepted", "rewrite"); }
            Sort();
            Status = Summary() + "  " + appliedSummary;
            Next();
        }

        /// <summary>The author's alt text was written: the suggestion is done (edited when it differs from the AI's).</summary>
        public void AltTextApplied(StyleFinding f, string suggestion, string alt)
        {
            f.IsFixed = true;
            RecordFeedback(f, _doc.SourcePath, suggestion != null && alt == suggestion ? "accepted" : "edited", alt);
            Sort();
            Status = Summary() + "  Alt text added as a tracked change.";
            Next();
        }

        /// <summary>
        /// At the start of a check (off the UI thread): the writer's requests to the owners. Sends any that were waiting,
        /// writes approved project exceptions into the project, and returns the outcomes not seen yet ("Approved", "Declined:
        /// note") as one notice, or null. Findings whose rule still has an open request are marked "Request sent".
        /// </summary>
        public string CheckRequests()
        {
            var team = CurrentTeam();
            if (team == null || !team.IsStrict) return null;
            try
            {
                var check = SyncRequests(team);
                _requested = check.OpenRules;
                foreach (var f in _findings) f.RequestSent = _requested.Contains(f.RuleId ?? f.Rule ?? "");
                return check.Notice;
            }
            catch (Exception ex) { Diagnostics.Log("requests check", ex); return null; }
        }

        /// <summary>The label the pane gives "Turn off this rule…": "Request a change…" when changes go to the owners.</summary>
        public string RuleChangeLabel()
        {
            var team = CurrentTeam();
            return team != null && team.ChangesNeedRequest ? "Request Rule Change…" : "Disable Rule…";
        }

        /// <summary>
        /// Turn off this rule…: an owner (or a team without an owners file) turns the rule off or changes its level for the
        /// guide or this project, optionally with the other rules on the same words. When the guide is read-only for this
        /// writer, or they aren't an owner, nothing is written: the change goes to the owners as a request with a reason.
        /// </summary>
        public void TurnOffRule()
        {
            var f = Current;
            if (f == null) { Status = "Select an issue whose rule you want to change."; return; }
            var team = CurrentTeam();
            if (team != null) team.Recheck();   // before any write: permissions or the owners list may have changed
            if (team != null && team.ChangesNeedRequest) { RequestChange(f, team, null); return; }
            // Owners (or a writer with no team): turn the rule off or change its level, for the team or this project.
            var rule = _runner.RuleFor(f.RuleId);
            var guide = team != null ? team.GuideRoot : null;
            var personal = team != null && team.IsPersonal;
            var described = Describe(f, rule);
            var others = rule != null ? f.AlsoRules : null;   // other rules on the same words: turning off this one would expose them
            if (others != null)
            {
                var readable = new List<RuleRef>();   // the rule's text, not its id
                foreach (var o in others)
                {
                    var r = _runner.RuleFor(o.Id);
                    readable.Add(new RuleRef { Id = o.Id, Layer = o.Layer, Name = r != null ? _runner.SourceName(r.Layer, r.Id) + " · " + r.Section + ": " + r.Text : o.Name });
                }
                others = readable;
            }
            var choice = _host.ChooseRuleSetting(described, rule != null && guide != null, personal, others);
            if (choice == null) return;
            var path = f.DocumentPath ?? _doc.SourcePath;
            var target = choice.ForTeam ? guide : ProjectOverridesPath(path);
            var changed = new List<string>();
            var all = new List<StyleFinding> { f };
            if (choice.AlsoOthers && others != null) foreach (var o in others) all.Add(new StyleFinding { RuleId = o.Id, Rule = o.Name, Layer = o.Layer, Source = "lint" });
            foreach (var one in all)
            {
                try
                {
                    // Owners only; records who and when; the writer's own guide is created on first use (never the plugin folder).
                    if (choice.ForTeam) target = SetGuideRule(team, one.RuleId, choice.Setting, choice.Reason);
                    else SetProjectRule(path, one, choice.Setting, choice.Reason, team != null ? team.Writer : null);
                    changed.Add(one.RuleId);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("Change rule", ex);
                    // A team with owners: a guide that stopped being writable (share offline, permissions changed) can still
                    // take the change as a request, with the reason already typed.
                    if (one == f && choice.ForTeam && team != null && team.IsStrict && !team.IsPersonal && ex is GuideWriteException)
                    {
                        team.ProbeGuide();
                        if (_host.Confirm("House Style couldn't write to " + (target ?? guide) + ". " + ex.Message + "\n\nNothing was changed. Send this change to the style guide owners (" +
                                          team.OwnerNames + ") as a request instead? Your reason is kept.", "Disable Rule"))
                            RequestChange(f, team, choice);
                        else Status = Summary() + "  The rule was not changed.";
                        return;
                    }
                    _host.ShowError("House Style couldn't change " + (one == f ? "the rule" : "\"" + one.Rule + "\"") + (target == null ? "." : " in " + target + "."),
                                    ex.Message + Environment.NewLine + Environment.NewLine + (one == f ? "Nothing was changed. The rule still applies." : "This rule and the ones after it were not changed. They still apply."), ex, "Disable Rule");
                    if (one == f) { Status = Summary() + "  The rule was not changed."; return; }
                    break;
                }
            }
            RecordFeedback(f, _doc.SourcePath, choice.Setting == "off" ? "rule ignored" : "rule changed", choice.Reason);
            if (choice.Setting == "off")
            {
                if (f.Layer == "ai" && f.RuleId == null) IgnoredAi().Add(f.Rule);
                _findings.RemoveAll(x => f.RuleId != null ? x.RuleId == f.RuleId || changed.Contains(x.RuleId) : x.Rule == f.Rule);
                Sort();
            }
            // Where it was written goes to the log, not the status line.
            Diagnostics.Log("Rule " + string.Join(", ", changed.ToArray()) + " set to " + choice.Setting + " in " + target);
            Status = Summary() + "  Rule" + (changed.Count > 1 ? "s" : "") + (choice.Setting == "off" ? " disabled" : " set to " + Severity.Label(choice.Setting)) +
                     (!choice.ForTeam ? " for this project."
                      : personal ? "."
                      : " for the team." + Team.CommitHint(guide)) +
                     (changed.Count < all.Count ? " " + (all.Count - changed.Count) + " could not be changed." : "");
            if (choice.Setting == "off") Raise(ReviewEvent.RuleDisabled);
        }

        /// <summary>
        /// "Request a change": asks scope, level or wording, and a reason, then sends one request to the owners. The finding
        /// stays in the list, marked "Request sent"; the writer can still skip it for now. Nothing is written to the guide or
        /// the project until an owner approves.
        /// </summary>
        public void RequestChange(StyleFinding f, Team team, RuleSettingChoice earlier)
        {
            if (f == null || team == null) return;
            var rule = _runner.RuleFor(f.RuleId);
            var path = f.DocumentPath ?? _doc.SourcePath;
            var overrides = ProjectOverridesPath(path);
            var editable = rule != null && rule.Layer != "google";   // Google rules are generated: owners change them with a level, not words
            var choice = _host.ChooseRequest(new RequestQuestion
            {
                RuleText = Describe(f, rule),
                Notice = "This goes to the style guide owners (" + team.OwnerNames + "). You see their answer the next time you lint." +
                         (team.AccessNotice == null ? "" : Environment.NewLine + team.AccessNotice),
                CanAskTeam = rule != null, CanAskProject = overrides != null,
                Wording = editable ? rule.Text : null,
                Reason = earlier != null ? earlier.Reason : null, Setting = earlier != null ? earlier.Setting : null,
                ForProject = earlier != null ? !earlier.ForTeam : rule == null
            });
            if (choice == null) return;
            var reason = (choice.Reason ?? "").Trim();
            if (reason.Length == 0) { Status = Summary() + "  Not sent. Add a reason for the owners."; return; }
            if (choice.ForTeam && rule == null) choice.ForTeam = false;
            if (!choice.ForTeam && overrides == null) { Status = Summary() + "  Not sent. This topic isn't in a Flare project."; return; }
            var wording = choice.ForTeam && editable && !string.IsNullOrEmpty(choice.Wording) && choice.Wording.Trim() != rule.Text.Trim() ? choice.Wording.Trim() : null;
            choice.Reason = reason;
            var r = RuleChanges.RuleRequest(team, choice, f.RuleId, f.Rule, f.Layer, rule != null ? rule.Text : null, RuleFile(rule), path, wording);
            if (!choice.ForTeam) r.Project = RuleChanges.ProjectName(overrides);
            if (!string.IsNullOrEmpty(f.Excerpt)) r.Evidence.Add("\"" + f.Excerpt + "\" (" + System.IO.Path.GetFileName(path ?? "") + ")");
            string problem;
            try { problem = SendRequest(team, r); }
            catch (Exception ex)
            {
                Diagnostics.Log("Send request", ex);
                _host.ShowError("House Style couldn't send your request.", ex.Message + Environment.NewLine + Environment.NewLine + "Your reason: " + reason, ex, "Request Rule Change");
                return;
            }
            RecordFeedback(f, _doc.SourcePath, "request", reason);
            Raise(ReviewEvent.RuleDisabled);   // the writer's way to disable a rule in a team with owners
            var key = f.RuleId ?? f.Rule ?? "";
            _requested.Add(key);
            foreach (var x in _findings) if ((x.RuleId ?? x.Rule ?? "") == key) x.RequestSent = true;
            Sort();
            if (problem != null)
            {
                _host.ShowError("Your request is saved but not sent yet (" + problem + ").",
                                "House Style sends it the next time you lint, once the requests folder is available. Check the team feedback folder in Settings.", null, "Request Rule Change");
                Status = Summary() + "  Request saved. It's sent the next time you lint.";
                return;
            }
            Status = Summary() + "  Request sent to the style guide owners (" + team.OwnerNames + "). The issue stays here until they decide.";
        }

        private string Describe(StyleFinding f, GuideRule rule)
        {
            return rule != null ? _runner.SourceName(rule.Layer, rule.Id) + " · " + rule.Section + ": " + rule.Display : "\"" + f.Rule + "\" (" + (f.Layer == "ai" ? "AI judgment" : _runner.SourceName(f.Layer, f.RuleId)) + ")";
        }

        /// <summary>The guide topic a rule is written in (a local file), or null.</summary>
        public static string RuleFile(GuideRule rule)
        {
            if (rule == null) return null;
            // A rule read from the saved copy of a team guide that can't be reached has no topic to open or to send (#246).
            if (GuideCopy.IsCopy(rule.Source)) return null;
            if (!string.IsNullOrEmpty(rule.Source) && System.IO.File.Exists(rule.Source)) return rule.Source;
            try
            {
                var file = !string.IsNullOrEmpty(rule.Link) && rule.Link.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(rule.Link).LocalPath : null;
                return GuideCopy.IsCopy(file) ? null : file;
            }
            catch (UriFormatException) { return null; }
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>Where a rule comes from, in the writer's words: Google, Our rules, This project, AI.</summary>
        public static string SourceName(string layer)
        {
            switch ((layer ?? "").ToLowerInvariant())
            {
                case "google": return "Google";
                case "company": return "Our rules";
                case "project": return "This project";
                case "ai": return "AI";
                default: return Cap(layer ?? "");
            }
        }

        private static string Cap(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        /// <summary>Cuts <paramref name="s"/> to <paramref name="n"/> characters with an ellipsis.</summary>
        public static string Shorten(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        private HashSet<string> IgnoredAi()
        {
            if (_ignoredAi == null) _ignoredAi = IgnoredAiRules(_doc.SourcePath);
            return _ignoredAi;
        }

        /// <summary>The document a finding lives in: the topic, or its snippet (opened in the editor on demand).</summary>
        private IDocumentAdapter DocOf(StyleFinding f, bool open)
        {
            var path = f.DocumentPath ?? _doc.SourcePath;
            if (open && path != null)
            {
                var opened = _host.OpenDocument(path);
                if (opened != null && f.DocumentPath == null) return _doc; // keep the adapter whose DOM the findings came from
                if (opened != null) return opened;
            }
            return f.DocumentPath == null ? _doc : null;
        }

        private static List<List<StyleFinding>> GroupByDocument(List<StyleFinding> findings)
        {
            var groups = new List<List<StyleFinding>>();
            foreach (var f in findings)
            {
                var g = groups.Find(x => x[0].DocumentPath == f.DocumentPath);
                if (g == null) groups.Add(g = new List<StyleFinding>());
                g.Add(f);
            }
            groups.Sort((a, b) => (a[0].DocumentPath == null ? 0 : 1).CompareTo(b[0].DocumentPath == null ? 0 : 1));
            return groups;
        }

        private static StyleFinding Clone(StyleFinding f)
        {
            return new StyleFinding
            {
                Source = f.Source, Rule = f.Rule, Severity = f.Severity, Excerpt = f.Excerpt, Suggestion = f.Suggestion, Replacement = f.Replacement,
                Before = f.Before, Block = f.Block, Layer = f.Layer, Link = f.Link, RuleId = f.RuleId, RuleLayer = f.RuleLayer, RuleSection = f.RuleSection, RuleText = f.RuleText, AlsoRules = f.AlsoRules,
                DocumentPath = f.DocumentPath, Variable = f.Variable, Section = f.Section, Order = f.Order
            };
        }
    }
}
