using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Tune style guide": what the team's review decisions say about each rule, for the style guide owner. Pools every
    /// writer's feedback file, then lists disputed rules (writers split), often-rejected rules, repeated hand edits
    /// (candidate Avoid/Use rows), requests to turn rules off, and rules that work. Each item shows its evidence;
    /// Open rule takes the owner to the rule in the guide, where changes are made as tracked changes. Requests to the
    /// owners come first: owners approve or decline them here; writers see their own, with the outcome, and can withdraw.
    /// </summary>
    public sealed class TuneGuideForm : Form
    {
        private readonly Team _team;
        private readonly IStyleService _style;
        private readonly string _project;
        private readonly Func<string, IDocumentAdapter> _open;
        private readonly Func<IDocumentAdapter, ActionRunner> _runnerFor;
        private readonly Func<IList<string>> _guideRoots;
        private readonly Func<string, bool> _isDefault;
        private readonly string _openProject;
        private readonly Button _draft, _prompts, _turnedOff;
        private readonly Label _head;
        private readonly ListView _list;
        private readonly RichTextBox _detail;
        private readonly Button _openRule, _decide, _withdraw;
        private List<Decision> _decisions = new List<Decision>();
        private Dictionary<string, GuideRule> _rules = new Dictionary<string, GuideRule>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> Groups = new Dictionary<string, string>
        {
            { "requests", "Requests to the style guide owners" },
            { "disputed", "Writers disagree about these rules" },
            { "review", "Often ignored" },
            { "edit", "Often edited by hand" },
            { "ignore", "Requests to disable a rule" },
            { "working", "Working well" }
        };

        /// <param name="runnerFor">An action runner for a style-guide topic opened in Flare (drafts guide edits).</param>
        /// <param name="guideRoots">The guides in effect, whose turned-off rules can be reviewed.</param>
        /// <param name="isDefault">True for a read-only guide (bundled with the plugin): its rows are listed as defaults.</param>
        /// <param name="openProject">A path in the open Flare project, for its own turned-off rules.</param>
        /// <param name="styleRules">Opens Rules (every rule, to disable or enable) over this window; null hides the button.</param>
        /// <param name="title">The window's name: Rule Decisions, or My Requests for a writer's own requests.</param>
        /// <param name="importRules">Opens Import Rules, which sends add-rule requests (My Requests); null hides the button.</param>
        public TuneGuideForm(Team team, IStyleService style, string project, Func<string, IDocumentAdapter> open, Func<IDocumentAdapter, ActionRunner> runnerFor,
                             Func<IList<string>> guideRoots = null, string openProject = null, Func<string, bool> isDefault = null, Action<IWin32Window> styleRules = null,
                             string title = null, Action importRules = null)
        {
            _team = team; _style = style; _project = project; _open = open; _runnerFor = runnerFor; _guideRoots = guideRoots; _openProject = openProject; _isDefault = isDefault;
            Text = Notify.Caption(title ?? Commands.RuleDecisions.Label);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 600);
            MinimumSize = new Size(600, 400);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _head = new LiveLabel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(8, 8, 8, 0), Text = "Reading the team's review decisions…" };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            _list.Columns.Add("Item", 420); _list.Columns.Add("Changed", 70); _list.Columns.Add("Ignored", 70); _list.Columns.Add("Writers", 200);
            foreach (var g in Groups) _list.Groups.Add(g.Key, g.Value);
            _list.SelectedIndexChanged += (s, e) => ShowDetail();
            _detail = DiffView.Create();
            _detail.Dock = DockStyle.Bottom; _detail.Height = 170; _detail.BackColor = SystemColors.Control;
            // Grows to a second row rather than clipping buttons at 125-150% scaling or in a narrow window.
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var rules = new Button { Text = Commands.Rules.Label + "…", AutoSize = true, Visible = styleRules != null };
            rules.Click += (s, e) => { styleRules(this); Load(); };
            var import = new Button { Text = Commands.ImportRules.Label + "…", AutoSize = true, Visible = importRules != null };
            import.Click += (s, e) => { importRules(); Load(); };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            var refresh = new Button { Text = "Refresh", AutoSize = true };
            _openRule = new Button { Text = "Open rule in the guide", AutoSize = true, Enabled = false };
            _draft = new Button { Text = "Draft a change with AI…", AutoSize = true, Enabled = false };
            _prompts = new Button { Text = "Customize AI prompts…", AutoSize = true, Enabled = IsOwner };
            _turnedOff = new Button { Text = "Disabled Rules…", AutoSize = true };
            _decide = new Button { Text = "Approve or decline…", AutoSize = true, Enabled = false, Visible = CanDecide };
            _withdraw = new Button { Text = "Withdraw request", AutoSize = true, Enabled = false, Visible = false };
            _decide.Click += (s, e) => DecideCurrent();
            _withdraw.Click += (s, e) => WithdrawCurrent();
            refresh.Click += (s, e) => Load();
            _openRule.Click += (s, e) => OpenRule();
            _draft.Click += (s, e) => { if (Requesting) RequestRuleChange(); else DraftChange(); };
            _prompts.Click += (s, e) => CustomizePrompts();
            _turnedOff.Click += (s, e) => { using (var f = new TurnedOffRulesForm(_team, _style, _project, _guideRoots, _openProject, _isDefault)) f.ShowDialog(this); };
            bar.Controls.AddRange(new Control[] { close, refresh, _openRule, _draft, _prompts, _turnedOff, rules, import, _withdraw, _decide });
            if (Requesting) _draft.Text = "Request Rule Change…";
            var tips = new ToolTip();
            tips.SetToolTip(rules, Commands.Rules.Tooltip);
            tips.SetToolTip(import, "Ask the style guide owners to add rules from an existing document.");
            tips.SetToolTip(_turnedOff, "See every disabled or changed rule, with who changed it, when and why.");
            tips.SetToolTip(_decide, "Approve the request, or decline it with a note. The writer sees the answer the next time they lint.");
            tips.SetToolTip(_withdraw, "Take back your open request.");
            tips.SetToolTip(_draft, Requesting ? "Ask the style guide owners to change this rule." : "Have the AI rewrite this rule from the team's decisions. You review the change before it's made.");
            tips.SetToolTip(_prompts, "Copy the AI's instructions into the style guide so your team can adjust them.");
            Controls.Add(_list);
            Controls.Add(_detail);
            Controls.Add(bar);
            Controls.Add(_head);
            CancelButton = close;
            A11y.Name(_list, "Rules and the team's decisions", "Columns are item, changed, ignored and writers.");
            A11y.Name(_detail, "Details for the selected item");
            A11y.Name(bar, "Dialog buttons");
            A11y.Name(_decide, "Approve or decline", "Decide the selected request.");
            A11y.Name(_withdraw, "Withdraw request", "Take back your selected open request.");
            A11y.Apply(this);
            Shown += (s, e) => Load();
        }

        private new void Load()
        {
            _list.Items.Clear(); _detail.Clear();
            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                var files = new List<string>(FeedbackStats.FilesIn(_team == null ? null : _team.FeedbackFolder));
                if (files.Count == 0 && FeedbackLog.PersonalPath != null) files.Add(FeedbackLog.PersonalPath);
                var decisions = FeedbackStats.Load(files);
                if (!ShowNames) decisions = FeedbackStats.Anonymize(decisions); // counts: never show names, even from older records
                var rules = new Dictionary<string, GuideRule>(StringComparer.OrdinalIgnoreCase);
                if (_style != null) try { foreach (var r in _style.Rules(_project)) rules[r.Id] = r; } catch (Exception ex) { Diagnostics.Log("tune: rules", ex); }
                var requests = new List<GuideRequest>();
                if (_team != null) _team.Recheck();
                if (_team != null && _team.IsStrict)
                {
                    try { requests = GuideRequests.Load(_team).FindAll(r => CanDecide ? r.Status != RequestStatus.Withdrawn : GuideRequests.IsMine(_team, r)); }
                    catch (Exception ex) { Diagnostics.Log("tune: requests", ex); }
                }
                e.Result = new object[] { decisions, FeedbackStats.Analyze(decisions), rules, requests };
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (IsDisposed) return;
                if (e.Error != null) { Diagnostics.Log("Tune style guide", e.Error); ErrorReporter.Background(this, "House Style couldn't read the team's decisions.", e.Error); _head.Text = "Could not read feedback: " + e.Error.Message; return; }
                var parts = (object[])e.Result;
                var decisions = (List<Decision>)parts[0];
                var items = (List<TuneItem>)parts[1];
                _rules = (Dictionary<string, GuideRule>)parts[2];
                _decisions = decisions;
                var requests = (List<GuideRequest>)parts[3];
                items.RemoveAll(i => i.Kind == "ignore");   // older turn-off proposals are listed as requests now
                Header(decisions, items, requests);
                foreach (var r in requests)
                {
                    int acc, rej;
                    Counts(r, out acc, out rej);
                    _list.Items.Add(new ListViewItem(new[] { "[" + StatusLabel(r) + "] " + ReqTitle(r), acc.ToString(), rej.ToString(), ShowNames ? r.Writer ?? "" : FeedbackStats.WriterCount(1) })
                    { Tag = r, Group = _list.Groups["requests"] });
                }
                foreach (var it in items)
                {
                    var row = new ListViewItem(new[] { ItemTitle(it), it.Accepted.ToString(), it.Rejected.ToString(), ShowNames ? string.Join(", ", it.Writers.ToArray()) : FeedbackStats.WriterCount(it.Writers.Count) })
                    { Tag = it, Group = _list.Groups[it.Kind] };
                    _list.Items.Add(row);
                }
                if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            };
            worker.RunWorkerAsync();
        }

        private void Header(List<Decision> decisions, List<TuneItem> items, List<GuideRequest> requests)
        {
            var writers = decisions.Select(d => d.Writer).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var dated = decisions.Where(d => d.At > DateTime.MinValue).ToList();
            var range = dated.Count == 0 ? "" : " (" + dated.Min(d => d.At).ToLocalTime().ToString("d") + " – " + dated.Max(d => d.At).ToLocalTime().ToString("d") + ")";
            var source = _team == null || _team.FeedbackFolder == null ? "your own decisions only. Set a team feedback folder in Settings to collect everyone's" : _team.FeedbackFolder;
            var role = _team == null || !_team.IsStrict ? "There's no owners file, so anyone can change the guide."
                     : _team.AccessNotice ?? (_team.IsListedOwner ? "You are a style guide owner." : "The owners are " + _team.OwnerNames + ".");
            var open = requests.Count(r => r.IsOpen);
            var actionable = items.Count(i => i.Kind != "working") + (CanDecide ? open : 0);
            _head.Text = Plural.Of(decisions.Count, "decision") + " from " + FeedbackStats.WriterCount(writers) + range + ". Source: " + source + ".\n"
                         + (actionable == 0 ? "Nothing needs attention yet. " : Plural.Of(actionable, "item") + " to look at. ") + role
                         + (_team != null && _team.Status != null ? "  " + _team.Status : "");
        }

        private bool ShowNames { get { return _team != null && _team.ShareNames; } }

        private TuneItem Current() { return _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as TuneItem : null; }

        private GuideRequest CurrentRequest() { return _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as GuideRequest : null; }

        /// <summary>The selected rule, from a report item or a request.</summary>
        private string CurrentRuleId()
        {
            var it = Current();
            if (it != null) return it.Rule;
            var r = CurrentRequest();
            return r == null ? null : r.RuleId;
        }

        /// <summary>Owners (and teams without an owners file) decide requests.</summary>
        private bool CanDecide { get { return _team == null || !_team.IsStrict || _team.IsListedOwner; } }

        /// <summary>Rule changes from this form go to the owners as requests.</summary>
        private bool Requesting { get { return _team != null && _team.ChangesNeedRequest; } }

        /// <summary>The rule's readable name (what it says, else its words from the name), never the internal id.</summary>
        private string ReadableRule(string key)
        {
            GuideRule g;
            if (!string.IsNullOrEmpty(key) && _rules.TryGetValue(key, out g)) return ReviewSession.Shorten(g.Display, 80);
            return RuleWording.FromName(key);
        }

        private string ReqTitle(GuideRequest r)
        {
            return r.TitleWith(q => ReadableRule(q.RuleId != null && _rules.ContainsKey(q.RuleId) ? q.RuleId : q.Rule ?? q.RuleId));
        }

        private string ItemTitle(TuneItem it)
        {
            return string.IsNullOrEmpty(it.Rule) || it.Title == null ? it.Title : it.Title.Replace(it.Rule, ReadableRule(it.Rule));
        }

        private static string StatusLabel(GuideRequest r)
        {
            if (r.Unsent) return "Not sent yet";
            switch (r.Status)
            {
                case RequestStatus.Approved: return "Approved";
                case RequestStatus.Declined: return "Declined";
                case RequestStatus.Withdrawn: return "Withdrawn";
                default: return "Open";
            }
        }

        /// <summary>The team's accepted and rejected decisions on the request's rule.</summary>
        private void Counts(GuideRequest r, out int accepted, out int rejected)
        {
            var key = r.RuleId ?? r.Rule;
            var about = _decisions.Where(d => key != null && (string.Equals(d.RuleId, key, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Rule, key, StringComparison.OrdinalIgnoreCase))).ToList();
            accepted = about.Count(d => d.IsAccept);
            rejected = about.Count(d => d.IsReject);
        }

        /// <summary>What the request asks, why, and the evidence, one line each.</summary>
        private List<string> RequestDetails(GuideRequest r)
        {
            var lines = new List<string>();
            lines.Add("From " + (ShowNames ? r.Writer : "a writer") + " on " + (r.Created > DateTime.MinValue ? r.Created.ToLocalTime().ToString("yyyy-MM-dd") : "an earlier date") + (r.IsLegacy ? " (sent before requests were tracked)" : "") + ".");
            lines.Add("Reason: " + (string.IsNullOrEmpty(r.Reason) ? "(none given)" : r.Reason));
            GuideRule rule;
            if (r.RuleId != null && _rules.TryGetValue(r.RuleId, out rule)) lines.Add("Rule " + rule.Id + " (" + SourceOf(rule) + " · " + rule.Section + "): " + rule.Display);
            else if (!string.IsNullOrEmpty(r.RuleText)) lines.Add("Rule: " + r.RuleText);
            if (r.Kind == RequestKinds.Wording) lines.Add("New wording asked for: " + r.Wording);
            if (r.Kind == RequestKinds.ProjectException)
                lines.Add("For project " + (r.Project ?? "(unnamed)") + " only. The team rule stays enabled. When you approve it, the exception is written into that project the next time the writer lints.");
            if (r.Kind == RequestKinds.AddRule && r.Candidate != null)
            {
                var c = r.Candidate;
                lines.Add(c.IsRow ? "Adds a row · Avoid: " + c.Avoid + " · Use: " + (string.IsNullOrEmpty(c.Use) ? "(flag only)" : c.Use) + " · " + c.Severity + " · Why: " + c.Why
                                  : "Adds a prose rule: " + c.Text);
                foreach (var f in c.Forms) lines.Add("“" + f.Text + "” " + f.Count + "×");
                foreach (var ex in c.Examples.Take(3)) lines.Add("• " + ex.Text + (string.IsNullOrEmpty(ex.File) ? "" : "  (" + ex.File + ")"));
            }
            int acc, rej;
            Counts(r, out acc, out rej);
            if (acc + rej > 0) lines.Add("The team's decisions on this rule: " + acc + " changed, " + rej + " ignored.");
            foreach (var e in r.Evidence ?? new List<string>()) lines.Add("• " + e);
            return lines;
        }

        private void ShowDetail()
        {
            var r = CurrentRequest();
            _decide.Enabled = r != null && r.IsOpen && !r.Unsent && CanDecide;
            var mine = r != null && _team != null && GuideRequests.IsMine(_team, r);
            _withdraw.Visible = mine;
            _withdraw.Enabled = mine && r.IsOpen;
            if (r != null)
            {
                _detail.Clear();
                Write(ReqTitle(r), FontStyle.Bold);
                Write(r.Outcome + ".", FontStyle.Italic);
                foreach (var line in RequestDetails(r)) Write(line, FontStyle.Regular);
                _detail.Select(0, 0);
                GuideRule cited;
                _openRule.Enabled = r.RuleId != null && _rules.TryGetValue(r.RuleId, out cited) && !string.IsNullOrEmpty(cited.Link);
                _draft.Enabled = false;
                return;
            }
            var it = Current();
            _detail.Clear();
            if (it == null) { _openRule.Enabled = false; return; }
            GuideRule rule;
            var known = _rules.TryGetValue(it.Rule ?? "", out rule);
            Write(ItemTitle(it), FontStyle.Bold);
            if (known) Write("Rule " + rule.Id + " (" + SourceOf(rule) + " · " + rule.Section + "): " + rule.Display, FontStyle.Italic);
            foreach (var line in it.Evidence) Write("• " + line, FontStyle.Regular);
            Write(Advice(it.Kind), FontStyle.Italic);
            _detail.Select(0, 0);
            _openRule.Enabled = known && !string.IsNullOrEmpty(rule.Link);
            // Google rules are generated (and ship read-only with the plugin): change them with an override, not an edit.
            _draft.Enabled = Requesting ? known
                : _openRule.Enabled && IsOwner && it.Kind != "working" && rule.Link.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && rule.Layer != "google";
        }

        private bool IsOwner { get { return _team == null || (_team.IsOwner && !_team.ChangesNeedRequest); } }

        /// <summary>Approve (optionally adjusted) or decline the selected request. The first owner to decide wins.</summary>
        private void DecideCurrent()
        {
            var r = CurrentRequest();
            if (r == null || !r.IsOpen) return;
            string decision; RequestDecision edits;
            using (var form = new RequestDecisionForm(r, RequestDetails(r)))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                decision = form.Decision; edits = form.Edits;
            }
            try
            {
                RequestDecision earlier;
                if (decision == RequestStatus.Declined) earlier = GuideRequests.Decline(_team, r, edits.Note);
                else
                {
                    if (r.Kind != RequestKinds.ProjectException && _team != null && _team.ProbeGuide() != null)
                    {
                        Notify.Error(this, "House Style can't write to the style guide at " + _team.GuideRoot + " (" + _team.GuideProblem + ").",
                                     "Ask whoever manages that folder to give you write access, then approve again. The request stays open.", null, "Approve request");
                        return;
                    }
                    earlier = GuideRequests.Approve(_team, r, edits, (req, d) => GuideRequests.ApplyToGuide(_team, req, d));
                }
                if (earlier != null)
                    Notify.Info(this, "This request was already " + earlier.Status + " by " + earlier.By + " on " + earlier.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
                                      (string.IsNullOrEmpty(earlier.Note) ? "." : ": " + earlier.Note), "Decide a request");
                Load();
                _head.Text = earlier != null ? "Already decided by " + earlier.By + "." :
                             decision == RequestStatus.Declined ? "Declined. The writer sees your note the next time they lint." :
                             r.Kind == RequestKinds.ProjectException ? "Approved. The exception is written into the writer's project the next time they lint."
                             : "Approved and changed in the style guide. Writers get it the next time they lint.";
            }
            catch (Exception ex)
            {
                Diagnostics.Log("tune: decide " + r.Id, ex);
                Notify.Error(this, "House Style couldn't " + (decision == RequestStatus.Declined ? "decline" : "apply") + " this request.", ex.Message + "\n\nNothing was changed. The request stays open.", ex, "Decide a request");
            }
        }

        /// <summary>The writer takes back an open request of theirs.</summary>
        private void WithdrawCurrent()
        {
            var r = CurrentRequest();
            if (r == null || !r.IsOpen) return;
            try
            {
                var earlier = GuideRequests.Withdraw(_team, r);
                Load();
                _head.Text = earlier != null ? "An owner already decided it: " + earlier.Status + " by " + earlier.By + "." : "Withdrawn: " + ReqTitle(r) + ".";
            }
            catch (Exception ex)
            {
                Diagnostics.Log("tune: withdraw " + r.Id, ex);
                Notify.Error(this, "House Style couldn't withdraw this request.", ex.Message, ex, "Withdraw request");
            }
        }

        /// <summary>Where a rule comes from, as the Issues pane says it (Our rules, Starter rules, Google).</summary>
        private string SourceOf(GuideRule rule)
        {
            var engine = _style as StyleEngine;
            if (engine == null) return ReviewSession.SourceName(rule.Layer);
            try { return engine.SourceName(engine.Compose(_project), rule.Layer, rule.Id); }
            catch (Exception ex) { Diagnostics.Log("tune: rule source", ex); return ReviewSession.SourceName(rule.Layer); }
        }

        /// <summary>A writer (or an owner who can't write to the guide) asks the owners to change the selected rule.</summary>
        private void RequestRuleChange()
        {
            GuideRule rule;
            var id = CurrentRuleId();
            if (id == null || !_rules.TryGetValue(id, out rule)) return;
            var choice = RequestForm.Ask(this, new RequestQuestion
            {
                RuleText = SourceOf(rule) + " · " + rule.Section + ": " + rule.Display,
                Notice = "This goes to the style guide owners (" + _team.OwnerNames + "). You see their answer the next time you lint." +
                         (_team.AccessNotice == null ? "" : Environment.NewLine + _team.AccessNotice),
                CanAskTeam = true, CanAskProject = false, Wording = rule.Layer != "google" ? rule.Text : null
            });
            if (choice == null) return;
            var kind = choice.Wording != null ? RequestKinds.Wording : choice.Setting == "off" ? RequestKinds.TurnOff : RequestKinds.Severity;
            var r = GuideRequests.New(_team, kind, choice.Reason);
            r.RuleId = rule.Id; r.Rule = rule.Id; r.Layer = rule.Layer; r.RuleText = rule.Text; r.RuleFile = ReviewSession.RuleFile(rule);
            r.Setting = choice.Setting; r.Wording = choice.Wording;
            var it = Current();
            if (it != null) r.Evidence.AddRange(it.Evidence.Take(5));
            Send(r);
        }

        private void Send(GuideRequest r)
        {
            string problem;
            try { GuideRequests.Send(_team, r, out problem); }
            catch (Exception ex) { Diagnostics.Log("tune: send request", ex); Notify.Error(this, "House Style couldn't send your request.", ex.Message + "\n\nYour reason: " + r.Reason, ex, "Request a change"); return; }
            Load();
            _head.Text = problem == null ? "Request sent to the style guide owners (" + _team.OwnerNames + "). You see their answer the next time you lint."
                                         : "Request saved but not sent yet (" + problem + "). House Style sends it the next time you lint.";
        }

        /// <summary>Opens the rule's topic, finds the rule's bullet or row, and has the AI draft the change from the evidence.</summary>
        private void DraftChange()
        {
            var it = Current();
            GuideRule rule;
            if (it == null || !_rules.TryGetValue(it.Rule ?? "", out rule)) return;
            try
            {
                if (_team != null && _team.IsPersonal)
                {
                    // Edit the writer's own copy of the rule (created on first use), not the read-only one in the plugin folder.
                    _team.WritableGuide();
                    var mine = _style == null ? null : _style.Rules(_project).FirstOrDefault(r => string.Equals(r.Id, rule.Id, StringComparison.OrdinalIgnoreCase));
                    if (mine != null) rule = mine;
                }
                var doc = _open == null ? null : _open(new Uri(rule.Link).LocalPath);
                if (doc == null) { Notify.Warn(this, "House Style couldn't open " + rule.Link + ". Open it from the Content pane instead."); return; }
                var block = GuideEdits.Locate(doc.Document, rule.Text);
                if (block == null) { Notify.Warn(this, "House Style couldn't find this rule in " + System.IO.Path.GetFileName(doc.SourcePath) + ". Open that topic and edit the rule there."); return; }
                var runner = _runnerFor == null ? null : _runnerFor(doc);
                if (runner == null) return;
                var instruction = GuideEdits.Instruction(it, rule, block.LocalName == "tr");
                using (var form = new ResultForm("Draft style guide change", runner, doc, ActionKind.Ask, instruction, new List<System.Xml.XmlNode> { block }) { Lint = false, Task = "draft" })
                {
                    if (form.ShowDialog(this) != DialogResult.OK) return;
                    _head.Text = form.AppliedSummary + " In " + System.IO.Path.GetFileName(doc.SourcePath) + (Team.IsUnderSourceControl(_team == null ? null : _team.GuideRoot) ? ". Review, save and commit the guide, then run Test Rules." : ". Review and save it, then run Test Rules.");
                }
            }
            catch (GuideWriteException ex) { Diagnostics.Log("tune: draft", ex); Notify.Error(this, "House Style couldn't set up your style guide for the change.", ex.Message + "\n\nNothing was changed.", ex, "Draft change"); }
            catch (Exception ex) { Diagnostics.Log("tune: draft", ex); Notify.Failed(this, "draft the change", ex, "Draft change"); }
        }

        /// <summary>Seeds the guide's FlareAI\prompts folder with the built-in templates (missing ones only) and opens it.</summary>
        private void CustomizePrompts()
        {
            if (_team == null || _team.GuideRoot == null) return;
            var dir = System.IO.Path.Combine(_team.GuideRoot, System.IO.Path.Combine("FlareAI", "prompts"));
            try
            {
                _team.WritableGuide();   // the writer's own guide is created on first need
                System.IO.Directory.CreateDirectory(dir);
                var added = 0;
                foreach (var name in PromptBuilder.Templates)
                {
                    var file = System.IO.Path.Combine(dir, name);
                    if (System.IO.File.Exists(file)) continue;
                    System.IO.File.WriteAllText(file, PromptBuilder.BuiltIn(name), new System.Text.UTF8Encoding(false));
                    added++;
                }
                var readme = System.IO.Path.Combine(dir, "README.txt");
                if (!System.IO.File.Exists(readme))
                    System.IO.File.WriteAllText(readme, "The AI's instructions. A file here replaces the built-in template of the same name for every writer\r\n" +
                        "who uses this style guide. Keep the {{PLACEHOLDERS}}. Delete a file to go back to the built-in version.\r\n" +
                        (Team.IsUnderSourceControl(_team == null ? null : _team.GuideRoot) ? "Commit changes like any rule change. " : "") + "Afterwards, run Test Rules and test the prose rules with AI.\r\n", new System.Text.UTF8Encoding(false));
                _head.Text = (added > 0 ? "Copied " + Plural.Of(added, "prompt template") + " to " : "Prompt templates are in ") + dir + (Team.IsUnderSourceControl(_team == null ? null : _team.GuideRoot) ? ". Edit, then commit with the guide." : ". Edit them there.");
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("tune: prompts", ex);
                Notify.Error(this, "House Style couldn't copy the prompt templates to " + dir + ".", ex.Message + "\n\nNothing was copied. Check that you can write to that folder, then try again.", ex, "Customize AI prompts");
            }
        }

        private static string Advice(string kind)
        {
            switch (kind)
            {
                case "disputed": return "The rule is probably right for some content and wrong for other. Narrow it with a project rule or a separate section, or rewrite it to say when it applies.";
                case "review": return "Read the ignored excerpts. If the rule is wrong there, narrow its wording or make it a prose rule with an example of the exception.";
                case "edit": return "Writers agree on better text. Update the rule's Use column or add an Avoid/Use row, then run Test Rules.";
                case "ignore": return "Decide for the team. Disable the rule in the Google overrides table, narrow it, or answer the writers.";
                default: return "";
            }
        }

        private void Write(string text, FontStyle style)
        {
            if (string.IsNullOrEmpty(text)) return;
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionFont = new Font(_detail.Font, style);
            _detail.AppendText((_detail.TextLength > 0 ? "\n" : "") + text);
        }

        private void OpenRule()
        {
            GuideRule rule;
            if (!_rules.TryGetValue(CurrentRuleId() ?? "", out rule) || string.IsNullOrEmpty(rule.Link)) return;
            try
            {
                if (rule.Link.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    var path = new Uri(rule.Link).LocalPath;
                    if (_open != null && _open(path) != null) { Close(); return; }
                }
                else if (rule.Link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) System.Diagnostics.Process.Start(rule.Link);
            }
            catch (Exception ex) { Diagnostics.Log("tune: open rule", ex); Notify.Failed(this, "open " + rule.Link, ex); }
        }
    }
}
