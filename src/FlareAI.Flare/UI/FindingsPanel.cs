using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Settings;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// The House Style Issues pane. The list shows every issue in reading order, grouped by section; the card below
    /// shows the selected one (what changes as a word diff, why with the cited rule, its text and example) with Change,
    /// Change All, Ignore, Edit, Rewrite and Lint Paragraph. Change writes a tracked change and moves to the next issue;
    /// the editor never switches away from the topic on its own. Rule issues show at once; the AI review arrives
    /// section by section. While getting started, a hint strip at the top carries the guided first lint and the empty
    /// list shows the getting-started checklist.
    /// </summary>
    public sealed class FindingsPanel : UserControl, IReviewHost, IRequiredTextHost
    {
        private readonly ActionRunner _runner;
        private readonly IDocumentAdapter _doc;
        private readonly Func<ActionRunner> _newRunner;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly ListView _list;
        private readonly Button _acceptAll, _bulk, _recheck, _ignore, _stop;
        private readonly Label _cost;
        private readonly NoticeLink _notice;   // the last toast, kept here so a keyboard user can still reach its action
        private readonly TextBox _result;
        private int _unitCount, _doneUnits;
        private bool _stopped;
        // card
        private readonly Label _cardHead;
        private readonly RichTextBox _cardDiff;
        private readonly CardTextBox _cardWhy;   // shown in full: the card grows or scrolls, the box never does
        private readonly Button _accept, _changeAll, _goTo, _openFile, _reject, _edit, _refine, _reanalyze, _rule, _legend;
        // getting started: the guided first lint's hint strip, the checklist in the empty state, and the focused first Lint Project
        private readonly LinkLabel _checklist;
        // The tour's hint strip: the step's sentence, then real buttons (a link label's links can't be trusted to hit-test
        // right once its text wraps in a narrow pane).
        private readonly Panel _tourStrip;
        private readonly Label _tourText;
        private readonly FlowLayoutPanel _tourBar;
        private readonly Button _tourNext, _tourSkip, _tourDone;
        private readonly Button _showAllRules;
        private bool _focusTop;
        private string _focusLine;
        private readonly ContextMenuStrip _rejectMenu;
        private readonly ToolTip _ignoreTip;
        // project check: by rule / by file, the rule card's actions, and batches with progress
        private readonly FlowLayoutPanel _viewBar, _actions, _ruleActions;
        private readonly Label _viewLabel;
        private readonly ComboBox _levelBox, _sourceBox;
        private readonly Button _clearFilters;
        private readonly ToolTip _filterTip;
        private ReviewSource _savedSource;   // the Source the writer chose; a project report shows both sources but keeps this
        private ReviewFilter _guidedSaved;   // the filter the guided first check set aside (it lints with every issue shown)
        private bool _syncingFilter;
        private readonly RadioButton _byRule, _byFile;
        // tab bar: This Topic and Project tabs
        private readonly FlowLayoutPanel _tabs;
        private readonly TabRadio _tabTopic, _tabProject;
        private readonly Button _excluded, _ignoredBtn, _exemptions, _export;
        private string _projectName;
        private readonly Button _backToRules, _showSkipped, _fixRule, _reviewRule, _turnOffRule, _skipRule, _batchStop;
        private BatchRun _batch;
        private string _lastRule;   // the rule row to come back to after Review one by one
        private string _lastReason;   // the reason of the last Ignore: I in a walk uses it (the reasons menu asks the first time only)

        private static readonly string SettingsDir = FlareAI.Core.Settings.PluginSettings.SettingsDirectory;
        private static readonly Dismissals Dismissed = Dismissals.Load(System.IO.Path.Combine(SettingsDir, "dismissed.json"));
        private readonly ReviewSession _session;   // the decisions; this pane renders them and forwards clicks
        private BackgroundWorker _worker;
        private bool _reviewing, _started;
        private string _lintNote;
        private bool _aiOff;              // the writer chose no AI: no AI review, and cached AI suggestions stay hidden
        private volatile bool _aiHidden;  // a cached AI review was hidden this check
        private readonly LinkLabel _aiOffNote;
        // Narrow pane (Flare docks it at ~150 logical px and ignores the width we ask for): rows wrap, buttons wrap,
        // labels grow to their text; sizing rules in PaneRows.
        private readonly LinkLabel _widthTip;
        private static bool _widthTipShown;   // once per Flare session, in the first pane that is narrow
        private bool _widthTipHere;
        private readonly SplitContainer _split;
        private readonly WalkStrip _walkStrip;   // the rule walked place by place, over the list (hidden otherwise)
        private readonly Panel _card;
        private readonly List<Button> _buttons = new List<Button>();
        private Font _metaFont;
        private int _rowLines = -1;            // lines of text per row now (1 = two one-line columns)
        private readonly Dictionary<string, List<string>> _wrapped = new Dictionary<string, List<string>>();
        private int _wrappedWidth;

        /// <summary>Opens House Style Settings (the AI-off note's link).</summary>
        public Action OpenSettings { get; set; }

        private bool _rulesOnly;

        /// <summary>Lint Topic's Rules Only: no AI review, no cached AI issues, and no AI from the pane's buttons.
        /// Separate from No AI in Settings (_aiOff). Set when the pane is made; Lint Again keeps it.</summary>
        public bool RulesOnly
        {
            get { return _rulesOnly; }
            set
            {
                _rulesOnly = value;
                _ignoreTip.SetToolTip(_reanalyze, value ? LintChecksChoice.LintParagraphTip : "Lint this paragraph again.");
                _ignoreTip.SetToolTip(_refine, value ? LintChecksChoice.RewriteTip : "Ask the AI for a different suggestion.");
            }
        }

        /// <summary>Set the tab counts and show or hide the bar. -1 for an absent slot. Bar visible only when both are >= 0.</summary>
        public void SetTabs(int topicCount, int projectCount)
        {
            _tabTopic.Text = "This &Topic (" + topicCount + ")";
            _tabProject.Text = "&Project (" + projectCount + ")";

            var topicName = topicCount >= 0 ? "This Topic, " + Plural.Of(topicCount, "issue") : "This Topic";
            var projName = projectCount >= 0 ? "Project, " + Plural.Of(projectCount, "issue") : "Project";
            _tabTopic.AccessibleName = topicName;
            _tabProject.AccessibleName = projName;

            _tabTopic.Checked = (Slot == PaneSlot.Topic);
            _tabProject.Checked = (Slot == PaneSlot.Project);

            _tabs.Visible = topicCount >= 0 && projectCount >= 0;
        }

        /// <summary>Focus the list with the first row selected, or focus the checked tab radio if the list is empty.</summary>
        public void FocusAfterSwitch()
        {
            if (_list.Items.Count > 0)
            {
                _list.Focus();
                if (_list.SelectedItems.Count == 0) FirstRow().Selected = true;
            }
            else
            {
                var checked_ = Slot == PaneSlot.Topic ? _tabTopic : _tabProject;
                checked_.Focus();
            }
        }

        private volatile string _requestNote;
        private List<StyleFinding> _lint;

        /// <summary>Raised when the author clicks Close; the host hides its pane or window.</summary>
        public event EventHandler CloseRequested;

        /// <summary>Raised when the author clicks a different tab; the host shows that slot.</summary>
        public event Action<PaneSlot> TabRequested;

        /// <summary>"House Style — topic.htm" — for the host's title.</summary>
        public string Title { get; private set; }

        private readonly bool _wholeTopic;
        private IList<string> _files; // project mode: lint these files (no AI), group by file

        /// <summary>A project-wide check: every topic and snippet, style rules only, grouped by file.</summary>
        public static FindingsPanel ForProject(ActionRunner runner, IDocumentAdapter activeDoc, Func<ActionRunner> newRunner,
                                               Func<string, IDocumentAdapter> open, IList<string> files, string projectName)
        {
            var panel = new FindingsPanel(runner, activeDoc, newRunner, open, null, false, files);
            panel._projectName = projectName;
            panel.Title = PaneTitle + " · " + projectName + " (" + Plural.Of(files.Count, "file") + ")";
            return panel;
        }

        /// <summary>The pane's name in Flare.</summary>
        public const string PaneTitle = "House Style Issues";

        public bool IsProjectReport { get { return _files != null; } }

        /// <summary>The pane slot shown: Topic or Project.</summary>
        public PaneSlot Slot { get { return IsProjectReport ? PaneSlot.Project : PaneSlot.Topic; } }

        /// <summary>Count of open issues for the bar: for a topic, all open; for a project, open and not skipped.</summary>
        public int OpenCount()
        {
            if (IsProjectReport)
            {
                return _session.Open().FindAll(f => !_session.IsSkipped(f)).Count;
            }
            else
            {
                return _session.Open().Count;
            }
        }

        /// <summary>
        /// Finds a file open in the editor without opening it (the host's): lets the pane notice a fix undone in another
        /// file (Ctrl+Z there), on row selection and when that document changes.
        /// </summary>
        public Func<string, IDocumentAdapter> FindOpen { set { _session.OpenedDocument = value; } }

        /// <summary>A document changed in the editor: applied fixes that were undone there are live again.</summary>
        public void RecheckApplied()
        {
            if (_reviewing) return;
            _session.RecheckApplied();
        }

        /// <summary>Path of the topic this panel checks.</summary>
        public string TopicPath { get { return _doc.SourcePath; } }

        private readonly Func<string, IDocumentAdapter> _open;
        private readonly ReviewCache _cache;
        private bool _aiOnStart;
        /// <summary>Check again (not after Stop): unchanged paragraphs get a new AI review instead of the saved one (#108).</summary>
        private bool _fresh;

        /// <param name="open">Opens another file (a snippet) in Flare and returns its adapter, or null.</param>
        /// <param name="cache">Shared AI review cache (same content = same review, across topics, renames and sessions).</param>
        /// <param name="aiOnStart">False when the panel opens because the author switched topics: only a cached AI
        /// review is shown automatically; a new one runs on Check again (no surprise cost).</param>
        public FindingsPanel(ActionRunner runner, IDocumentAdapter doc, Func<ActionRunner> newRunner, Func<string, IDocumentAdapter> open,
                             ReviewCache cache, bool aiOnStart)
            : this(runner, doc, newRunner, open, cache, aiOnStart, null) { }

        private FindingsPanel(ActionRunner runner, IDocumentAdapter doc, Func<ActionRunner> newRunner, Func<string, IDocumentAdapter> open,
                              ReviewCache cache, bool aiOnStart, IList<string> files)
        {
            _runner = runner; _doc = doc; _newRunner = newRunner; _open = open; _cache = cache; _aiOnStart = aiOnStart; _files = files;
            _session = new ReviewSession(runner, doc, this, Dismissed, files != null);
            // The remembered level and source, applied before the first Rebuild (no handler yet). A project report has no AI
            // issues, so it always shows both sources and keeps the saved Source as it was.
            ReviewLevel savedLevel; ReviewSource savedSource;
            ReviewViewPrefs.Load(out savedLevel, out savedSource);
            _savedSource = savedSource;
            _session.Filter = new ReviewFilter(savedLevel, files != null ? ReviewSource.RulesAndAi : savedSource);
            _session.ListChanged += Rebuild;
            _session.SelectRequested += Select;
            _session.StatusChanged += text => _status.Text = text;
            _session.IgnoredChanged += OnIgnoredChanged;
            _session.BatchReported += ShowResult;
            _session.Happened += OnHappened;
            _session.IsShown = () => PanelHost.Current == this;
            Disposed += (s, e) => _session.WatchWrites(false);
            // Keep the first check's scope: Go to selects text in the editor, which must not shrink a Check again.
            _wholeTopic = string.IsNullOrEmpty(doc.SelectionText);
            Title = PaneTitle + " · " + System.IO.Path.GetFileName(doc.SourcePath ?? "");
            Font = SystemFonts.MessageBoxFont;
            Padding = new Padding(6);   // no minimum width: Flare docks the pane narrow, and a minimum only cuts the right side off

            _status = new LiveLabel { Dock = DockStyle.Top, AutoSize = false, AutoEllipsis = true, Height = 36, Text = "Linting…" };
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = 5, Style = ProgressBarStyle.Marquee };
            var triage = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
            _acceptAll = Btn(triage, "Fix All", (s, e) => { if (_files != null) _session.FixAllAutomatic(); else _session.FixAll(); });
            _bulk = Btn(triage, "Change All Issues", (s, e) => _session.WriteAll());
            _stop = Btn(triage, "Stop AI Review", (s, e) => StopReview());
            _stop.Visible = false;
            _batchStop = Btn(triage, "Stop after this file", (s, e) =>
            {
                if (_batch == null) return;
                _batch.Cancel();
                _batchStop.Enabled = false;
                _status.Text = "Stopping after this file. Files already changed keep their tracked changes.";
            });
            _batchStop.Visible = false;
            _legend = Btn(triage, "?", (s, e) => { using (var f = new LegendForm()) f.ShowDialog(FindForm()); });
            _session.RunBatch = RunBatchInPane;

            // Lint Project: one row per rule (most frequent first) or the issues file by file. Remembered.
            _viewBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Visible = true };
            _viewLabel = new Label { Text = "View:", AutoSize = true, Margin = new Padding(0, 6, 4, 0), Visible = files != null };
            _viewBar.Controls.Add(_viewLabel);
            var byFile = files != null && FlareAI.Core.Settings.PluginSettings.Load().ProjectViewByFile;
            _byRule = new RadioButton { Text = "By &rule", AutoSize = true, Checked = !byFile, Margin = new Padding(0, 3, 8, 0) };
            _byFile = new RadioButton { Text = "By &file", AutoSize = true, Checked = byFile, Margin = new Padding(0, 3, 12, 0) };
            _viewBar.Controls.Add(_byRule);
            _viewBar.Controls.Add(_byFile);
            _byRule.Visible = _byFile.Visible = files != null;
            _viewBar.SetFlowBreak(_byFile, true);

            // Level and Source: which issues the list shows. Remembered; locked during Review One by One.
            _filterTip = new ToolTip();
            // No labels: the choices say what they filter, and a narrow pane has no height to spare for a label row.
            _levelBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Level", Width = 76, DropDownWidth = 110, Margin = new Padding(0, 0, 3, 0) };
            _levelBox.Items.AddRange(new object[] { "All Levels", "Errors", "Warnings", "Suggestions" });
            _sourceBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Source", Width = 76, DropDownWidth = 110, Margin = new Padding(0, 0, 3, 0) };
            _sourceBox.Items.AddRange(new object[] { "Rules and AI", "Rules Only", "AI Only" });
            _levelBox.SelectedIndex = (int)_session.Filter.Level;
            _sourceBox.SelectedIndex = (int)_session.Filter.Source;
            _filterTip.SetToolTip(_levelBox, "Show only issues of one level.");
            _filterTip.SetToolTip(_sourceBox, "Show only issues from the rules, or only those from the AI.");
            _viewBar.Controls.Add(_levelBox);
            _viewBar.Controls.Add(_sourceBox);
            _viewBar.SetFlowBreak(_sourceBox, true);
            _levelBox.SelectedIndexChanged += (s, e) => Safe.Run("Filter level", ApplyFilterChoice);
            _sourceBox.SelectedIndexChanged += (s, e) => Safe.Run("Filter source", ApplyFilterChoice);
            _clearFilters = Btn(_viewBar, "Clear Filters", (s, e) => ClearFilters());
            _filterTip.SetToolTip(_clearFilters, "Show every issue again.");
            _clearFilters.Visible = !_session.Filter.IsAll;
            _byRule.CheckedChanged += (s, e) => Safe.Run("View by rule", () => { if (_byRule.Checked) SwitchView(false); });
            _byFile.CheckedChanged += (s, e) => Safe.Run("View by file", () => { if (_byFile.Checked) SwitchView(true); });
            _backToRules = Btn(_viewBar, "All Rules", (s, e) => _session.EndRuleReview());
            _showSkipped = Btn(_viewBar, "Show Skipped Rules", (s, e) => _session.ShowSkippedRules());
            _showAllRules = Btn(_viewBar, "Show All Rules", (s, e) => ShowAllRules());
            _excluded = Btn(_viewBar, "Excluded Files", (s, e) => ShowExcluded());
            _export = Btn(_viewBar, "Export Issues…", (s, e) => ExportIssues());
            _backToRules.Visible = _showSkipped.Visible = _showAllRules.Visible = _excluded.Visible = false;
            _export.Visible = files != null;   // the bar is always shown now (the filter lives on it); the project report buttons only in Lint Project

            // Owner-drawn so a row can wrap: the suggestion on up to 3 lines, then a smaller grey line (source or counts).
            // Items, groups, selection, keys, menu and tooltips are the ListView's own; only the painting is ours.
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, OwnerDraw = true };
            _list.Columns.Add("Issue");
            _list.Columns.Add("Source");
            _list.Resize += (s, e) => FitColumns();
            _list.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            _list.DrawItem += (s, e) => Safe.Run("Draw row", () => DrawRow(e));
            _list.DrawSubItem += (s, e) => { };   // the whole row is painted in DrawItem (hover repaints raise only DrawItem)
            _list.ColumnWidthChanging += (s, e) => { if (_rowLines > 1 && e.ColumnIndex == 1) { e.Cancel = true; e.NewWidth = 0; } };
            _list.FontChanged += (s, e) => { _rowLines = -1; FitColumns(); };
            _list.GotFocus += (s, e) => _list.Invalidate();   // the selected row: highlighted when the list has focus, grey when not
            _list.LostFocus += (s, e) => _list.Invalidate();
            _list.SelectedIndexChanged += (s, e) => QueueSelectionChanged();
            _list.DoubleClick += (s, e) => { if (SelectedHeading() != null) return; Safe.Run("Change", Accept); };
            _list.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var hit = _list.GetItemAt(e.X, e.Y);
                if (hit == null || !(hit.Tag is FileHeading)) return;
                BeginInvoke(new Action(() => Safe.Run("Collapse file", () => ClickRow(hit))));
            };
            _list.KeyDown += (s, e) => Safe.Run("List key", () => OnListKey(s, e));
            _list.ContextMenuStrip = BuildRowMenu();

            // The card: what, why, impact, and the decision.
            // Laid out by LayoutCard (not docking): heading, what changes, why, then the buttons, each measured at the card's
            // width; when the pane is too short for all of it the card scrolls instead of stacking controls on each other.
            var card = _card = new CardPanel { Dock = DockStyle.Fill };
            // Vertical scrolling only: the parts are laid out to the card's width, so a horizontal bar is never needed (and
            // left to AutoScroll the two bars can keep each other on screen).
            card.HorizontalScroll.Maximum = 0;
            card.HorizontalScroll.Enabled = false;
            card.HorizontalScroll.Visible = false;
            card.AutoScroll = true;
            _cardHead = new Label { AutoSize = false, Height = 34, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
            _cardDiff = DiffView.Create();
            _cardDiff.Height = 64; _cardDiff.BackColor = SystemColors.Window;
            _cardWhy = DiffView.CreateFull();
            _cardWhy.BackColor = SystemColors.Control;
            ((CardPanel)card).KeepPlace = _cardWhy;   // clicking or tabbing into the why text doesn't move the card
            _cardWhy.SelectionChanged += (s, e) => KeepCaretInView();
            var actions = _actions = new FlowLayoutPanel { AutoSize = false, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
            // A rule row's card: change it everywhere, step through it, disable it, or put it aside for now.
            _ruleActions = new FlowLayoutPanel { AutoSize = false, WrapContents = true, Padding = new Padding(0, 4, 0, 0), Visible = false };
            _fixRule = Btn(_ruleActions, "Change All…", (s, e) => _session.FixRule(CurrentRule()));
            _reviewRule = Btn(_ruleActions, "Review One by One", (s, e) => ReviewRule());
            _turnOffRule = Btn(_ruleActions, "Disable Rule…", (s, e) => _session.TurnOffRule(CurrentRule()));
            _skipRule = Btn(_ruleActions, "Skip", (s, e) => _session.SkipRule(CurrentRule()));
            _accept = Btn(actions, "Change", (s, e) => Accept());
            _changeAll = Btn(actions, "Change All", (s, e) => _session.ChangeAll());
            _goTo = Btn(actions, "Go To", (s, e) => _session.GoToCurrent());
            _openFile = Btn(actions, "Open File", (s, e) => _session.OpenFileOfCurrent());
            _reject = Btn(actions, "Ignore ▾", (s, e) => _rejectMenu.Show(_reject, new Point(0, _reject.Height)));
            _edit = Btn(actions, "Edit…", (s, e) => _session.Edit());
            _refine = Btn(actions, "Rewrite…", (s, e) => Refine());
            _reanalyze = Btn(actions, "Lint Paragraph", (s, e) => Reanalyze());
            _rule = Btn(actions, "Open Guide", (s, e) => { var f = Current(); if (f != null && !string.IsNullOrEmpty(f.Link)) OpenLink(f.Link); });
            // Ignore asks why (recorded for Rule Decisions): the stored reasons stay "wrong", "fine here", "rule doesn't apply".
            _rejectMenu = new ContextMenuStrip();
            var reasons = new[]
            {
                _rejectMenu.Items.Add("The suggestion is wrong", null, (s, e) => Safe.Run("Ignore", () => RejectWith("wrong"))),
                _rejectMenu.Items.Add("It's correct in this context", null, (s, e) => Safe.Run("Ignore", () => RejectWith("fine here"))),
                _rejectMenu.Items.Add("The rule doesn't apply here", null, (s, e) => Safe.Run("Ignore", () => RejectWith("rule doesn't apply")))
            };
            foreach (var reason in reasons) reason.ToolTipText = IgnoreTip;
            var exempt = _rejectMenu.Items.Add(ExemptLabel, null, (s, e) => Safe.Run("Exempt", ExemptSelectedHere));
            exempt.ToolTipText = exempt.AccessibleDescription = ExemptTip;
            _rejectMenu.Items.Add(new ToolStripSeparator());
            _rejectMenu.Items.Add("Skip for Now", null, (s, e) => Safe.Run("Skip", _session.Skip));
            _rejectMenu.Items.Add(new ToolStripSeparator());
            var markFixed = _rejectMenu.Items.Add("Mark as Fixed", null, (s, e) => Safe.Run("Mark as Fixed", _session.MarkFixed));
            markFixed.AccessibleName = "Mark as Fixed";
            _rejectMenu.Items.Add(new ToolStripSeparator());
            _rejectMenu.Opening += (s, e) =>
            {
                var f = Current();
                markFixed.Enabled = f != null && !f.IsFixed;
                exempt.Visible = _session.CanExempt(f);
            };
            _rejectMenu.Items.Add(ReportFalsePositiveLabel, null, (s, e) => Safe.Run("Report False Positive", ReportFalsePositive));
            card.Controls.Add(actions);   // in tab order: the decision first, then the text
            card.Controls.Add(_ruleActions);
            card.Controls.Add(_cardHead);
            card.Controls.Add(_cardDiff);
            card.Controls.Add(_cardWhy);

            var split = _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(_list);
            // What the last batch did (changed, skipped with the reason, failed) stays here until the next check.
            _result = new TextBox { Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 90, Visible = false, BackColor = SystemColors.Window };
            split.Panel1.Controls.Add(_result);
            // Getting started, in the empty state: what to try next, ticked as it happens. Hide puts it away for good.
            _checklist = new LinkLabel { Dock = DockStyle.Top, AutoSize = false, Height = 20, Visible = false, Padding = new Padding(4, 4, 4, 4), AccessibleRole = AccessibleRole.Grouping, AccessibleName = "Getting started" };
            _checklist.LinkClicked += (s, e) => Safe.Run("Checklist", () => OnChecklistLink(e.Link.LinkData as string));
            split.Panel1.Controls.Add(_checklist);
            // The walk strip: the rule walked place by place. Added last, so it docks above the list and the list keeps its rows.
            _walkStrip = new WalkStrip { Visible = false };
            _walkStrip.ChangeAllClicked += (s, e) => Safe.Run("Change All", () => _session.FixRule(_session.FindBacklogRule(_session.FocusRule)));
            _walkStrip.AllRulesClicked += (s, e) => Safe.Run("All Rules", _session.EndRuleReview);
            _walkStrip.NextRuleClicked += (s, e) => Safe.Run("Next Rule", NextRuleInWalk);
            split.Panel1.Controls.Add(_walkStrip);
            split.Panel2.Controls.Add(card);
            // The pane is laid out small first and grows: the card gets the height it needs (or the writer's, once they drag
            // the splitter) and the list what's left; the card is laid out again whenever its size or content changes.
            split.SizeChanged += (s, e) => SizeCard();
            split.SplitterMoving += (s, e) => _draggingSplitter = true;
            split.SplitterMoved += (s, e) => { if (_draggingSplitter && !_settingSplitter) _cardByWriter = split.Panel2.Height; _draggingSplitter = false; };
            card.ClientSizeChanged += (s, e) => LayoutCard();
            card.VisibleChanged += (s, e) => LayoutCard();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
            _recheck = Btn(bar, "Lint Again", (s, e) => { _aiOnStart = true; _fresh = !_stopped; Start(); });   // after Stop: carry on, not start over
            _exemptions = Btn(bar, "Exemptions", (s, e) => ShowExemptions()); _exemptions.Visible = false;   // on the main bar, so a topic lint shows it too; shown once the project has any
            _ignoredBtn = Btn(_files != null ? _viewBar : bar, "Ignored Issues", (s, e) => ShowIgnored()); _ignoredBtn.Visible = false;   // the view bar in Lint Project, else the main bar
            _ignore = Btn(bar, "Disable Rule…", (s, e) => { var r = CurrentRule(); if (r != null) _session.TurnOffRule(r); else _session.TurnOffRule(); });
            Btn(bar, "Close", (s, e) => { var h = CloseRequested; if (h != null) h(this, EventArgs.Empty); });

            var tips = new ToolTip();
            tips.SetToolTip(_accept, "Replace the text with the suggestion.");
            tips.SetToolTip(_changeAll, "Replace every instance of this issue in the topic.");
            tips.SetToolTip(_reject, "Ignore this issue.");
            tips.SetToolTip(_goTo, "Select this text in the topic.");
            tips.SetToolTip(_openFile, "Open the file that contains this issue.");
            tips.SetToolTip(_edit, "Change the suggested text before you apply it.");
            tips.SetToolTip(_refine, "Ask the AI for a different suggestion.");
            tips.SetToolTip(_reanalyze, "Lint this paragraph again.");
            tips.SetToolTip(_rule, "Open this rule's page in the style guide.");
            tips.SetToolTip(_bulk, "Make every change in this topic, including AI rewording.");
            tips.SetToolTip(_acceptAll, "Make every automatic change in this topic.");
            tips.SetToolTip(_stop, "Stop the AI review. The issues found so far stay.");
            if (files != null) tips.SetToolTip(_acceptAll, "Make every automatic change in this project.");
            tips.SetToolTip(_batchStop, "Stop before the next file.");
            tips.SetToolTip(_byRule, "List one row per rule, most frequent first.");
            tips.SetToolTip(_byFile, "List every issue, file by file.");
            tips.SetToolTip(_fixRule, "Replace every instance of this issue in every file.");
            tips.SetToolTip(_reviewRule, "Go through this rule's issues one at a time. (Enter)");
            tips.SetToolTip(_turnOffRule, "Stop linting for this rule.");
            tips.SetToolTip(_skipRule, "Hide this rule until you lint again.");
            tips.SetToolTip(_showAllRules, "List every rule.");
            tips.SetToolTip(_export, "Save the issues to a file you can open in Excel.");
            tips.SetToolTip(_recheck, "Lint this topic again.");
            tips.SetToolTip(_legend, "Show what the symbols and buttons mean.");
            tips.SetToolTip(_ignoredBtn, "Show the issues you ignored. They stay hidden each time you lint.");
            _ignoreTip = tips;

            // The last notice (a toast vanishes by itself) stays reachable here, with its action as a focusable link.
            _notice = new NoticeLink { Dock = DockStyle.Top, AutoSize = false, Height = 20, Visible = false, AccessibleRole = AccessibleRole.Link };
            _notice.TextChanged += (s, e) => FitLabel(_notice, 0);
            _notice.VisibleChanged += (s, e) => FitLabel(_notice, 0);
            _notice.LinkClicked += (s, e) => { var n = Toast.Last; if (n != null && n.Action != null) n.Action(); };
            // Static events: subscribed while the pane has a window. Flare can destroy and recreate the handle when it
            // moves or re-docks the pane, so a subscription made once in the constructor would be lost.
            HandleCreated += (s, e) => { Toast.LastChanged -= OnNoticeChanged; Toast.LastChanged += OnNoticeChanged; OnNoticeChanged(); };
            HandleDestroyed += (s, e) => Toast.LastChanged -= OnNoticeChanged;
            OnNoticeChanged();

            Controls.Add(split);      // Fill must be added first so docked bars take their space
            Controls.Add(bar);
            _cost = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 18, ForeColor = SystemColors.GrayText, Text = AiCost.Session() };
            Controls.Add(_cost);
            Controls.Add(_viewBar);
            Controls.Add(triage);
            Controls.Add(_progress);
            Controls.Add(_notice);
            _aiOffNote = new LinkLabel { Dock = DockStyle.Top, AutoSize = false, Height = 20, Visible = false, Text = ReviewCache.AiOffNote + "  Settings", AccessibleRole = AccessibleRole.Link };
            _aiOffNote.LinkArea = new LinkArea(ReviewCache.AiOffNote.Length + 2, "Settings".Length);
            _aiOffNote.LinkClicked += (s, e) => Safe.Run("Settings", () => { var settings = OpenSettings; if (settings != null) settings(); });
            Controls.Add(_aiOffNote);
            Controls.Add(_status);
            _widthTip = new LinkLabel { Dock = DockStyle.Top, AutoSize = false, Height = 20, Visible = false, Text = WidthTipText + "  Got it", AccessibleRole = AccessibleRole.Link,
                                        AccessibleName = WidthTipText, AccessibleDescription = "Got it hides this tip for good.", Padding = new Padding(0, 0, 0, 4) };
            _widthTip.LinkArea = new LinkArea(WidthTipText.Length + 2, "Got it".Length);
            _widthTip.LinkClicked += (s, e) => Safe.Run("Hide tip", HideWidthTip);
            Controls.Add(_widthTip);
            // The guided first lint: one step at a time, at the very top.
            _tourStrip = new Panel { Dock = DockStyle.Top, Height = 20, Visible = false, BackColor = SystemColors.Info, ForeColor = SystemColors.InfoText,
                                     Padding = new Padding(0, 4, 0, 2), AccessibleRole = AccessibleRole.Grouping, AccessibleName = "Practice" };
            _tourBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = false, WrapContents = true, Padding = new Padding(4, 2, 4, 0), BackColor = SystemColors.Info };
            _tourText = new Label { Dock = DockStyle.Top, AutoSize = false, UseMnemonic = false, Padding = new Padding(4, 0, 4, 0), BackColor = SystemColors.Info, ForeColor = SystemColors.InfoText };
            _tourStrip.Controls.Add(_tourBar);    // docked under the text (the last added docks topmost)
            _tourStrip.Controls.Add(_tourText);
            // Run after the click returns: the strip's own buttons change under it.
            _tourNext = Btn(_tourBar, "Next", (s, e) => BeginInvoke((Action)(() => Safe.Run("Tour next", () => OnTourButton("next")))));
            _tourSkip = Btn(_tourBar, "Skip Tour", (s, e) => BeginInvoke((Action)(() => Safe.Run("Tour skip", () => OnTourButton("skip")))));
            _tourDone = Btn(_tourBar, "Done", (s, e) => BeginInvoke((Action)(() => Safe.Run("Tour done", () => OnTourButton("done")))));
            foreach (var b in new[] { _tourNext, _tourSkip, _tourDone }) b.BackColor = SystemColors.Control;
            // Tab bar: This Topic and Project tabs
            _tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Visible = false };
            _tabTopic = new TabRadio { Text = "This &Topic", Appearance = Appearance.Button, AutoSize = true, Margin = new Padding(0, 3, 4, 0) };
            _tabProject = new TabRadio { Text = "&Project", Appearance = Appearance.Button, AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
            _tabs.Controls.Add(_tabTopic);
            _tabs.Controls.Add(_tabProject);
            _tabTopic.Click += (s, e) => { if (Slot != PaneSlot.Topic) { var h = TabRequested; if (h != null) h(PaneSlot.Topic); } };
            _tabProject.Click += (s, e) => { if (Slot != PaneSlot.Project) { var h = TabRequested; if (h != null) h(PaneSlot.Project); } };
            tips.SetToolTip(_tabTopic, "Show the issues in the open topic.");
            tips.SetToolTip(_tabProject, "Show the issues from Lint Project.");
            Controls.Add(_tourStrip);   // added last: the topmost line
            Controls.Add(_tabs);        // very last: docks at the very top
            HandleCreated += (s, e) =>
            {
                GuidedTour.Changed -= OnGettingStartedChanged; GettingStarted.Changed -= OnGettingStartedChanged;
                GuidedTour.Changed += OnGettingStartedChanged; GettingStarted.Changed += OnGettingStartedChanged;
                OnGettingStartedChanged();
            };
            HandleDestroyed += (s, e) => { GuidedTour.Changed -= OnGettingStartedChanged; GettingStarted.Changed -= OnGettingStartedChanged; };
            _status.TextChanged += (s, e) => { FitLabel(_status, 2); _ignoreTip.SetToolTip(_status, _status.Text); };
            _aiOffNote.VisibleChanged += (s, e) => FitLabel(_aiOffNote, 0);
            _tourStrip.VisibleChanged += (s, e) => LayoutTourStrip();
            _checklist.VisibleChanged += (s, e) => FitLabel(_checklist, 0);
            _cost.TextChanged += (s, e) => FitLabel(_cost, 0);
            SizeChanged += (s, e) => FitToWidth();
            HandleCreated += (s, e) => FitToWidth();
            NameControls();
            HandleDestroyed += (s, e) => { if (_worker != null && _worker.IsBusy) _worker.CancelAsync(); };
            ShowCard();
            UpdateTourStrip();
        }

        // ---- getting started -----------------------------------------------------------------------------------

        private void OnGettingStartedChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)OnGettingStartedChanged); return; }
            UpdateTourStrip();
            UpdateChecklist();
            var n = Toast.Last;
            var tour = GuidedTour.Current;
            _notice.Visible = n != null && !(tour != null && n.Text == tour.Hint);
        }

        /// <summary>What the writer did in the session: the tour and the checklist follow; the first Change gets its note.</summary>
        private void OnHappened(ReviewEvent e)
        {
            switch (e)
            {
                case ReviewEvent.Changed:
                    GettingStarted.Did(TourEvent.Changed);
                    var form = FindForm();
                    if (form != null) GettingStarted.Note(form, FirstTimeNote.FirstChange);
                    break;
                case ReviewEvent.Undone: GettingStarted.Did(TourEvent.Undone); break;
                case ReviewEvent.RuleDisabled: GettingStarted.Did(TourEvent.RuleDisabled); break;
            }
        }

        /// <summary>
        /// The hint strip: "Step 2 of 5. Select an issue and click Change." with Next (only on the step House Style can't
        /// see) and Skip Tour; after the last step, "You're ready. Lint Project lints every topic." with Done, until Done.
        /// </summary>
        private void UpdateTourStrip()
        {
            var tour = GuidedTour.Current;
            var show = tour != null;
            if (show)
            {
                var finished = tour.Step == TourStep.Finished;
                var text = finished ? tour.Hint : tour.Progress + ". " + tour.Hint;
                if (_tourText.Text != text) _tourText.Text = text;
                _tourNext.Visible = !finished && tour.NeedsNext;
                _tourSkip.Visible = !finished;
                _tourDone.Visible = finished;
                _tourStrip.AccessibleDescription = text;
            }
            if (_tourStrip.Visible != show) _tourStrip.Visible = show;
            LayoutTourStrip();
        }

        /// <summary>The strip as tall as its wrapped sentence plus its buttons, at the pane's width.</summary>
        private void LayoutTourStrip()
        {
            if (_tourStrip == null || !_tourStrip.Visible) return;
            var w = Math.Max(20, ClientSize.Width - Padding.Horizontal - _tourText.Padding.Horizontal);
            foreach (var b in new[] { _tourNext, _tourSkip, _tourDone }) FitButton(b);
            var text = TextRenderer.MeasureText(string.IsNullOrEmpty(_tourText.Text) ? " " : _tourText.Text, _tourText.Font, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + S(2);
            text += _tourText.Padding.Vertical;
            if (_tourText.Height != text) _tourText.Height = text;
            var bar = _tourBar.GetPreferredSize(new Size(w, 0)).Height;
            if (_tourBar.Height != bar) _tourBar.Height = bar;
            var h = text + bar + _tourStrip.Padding.Vertical;
            if (_tourStrip.Height != h) _tourStrip.Height = h;
        }

        private void OnTourButton(string what)
        {
            var tour = GuidedTour.Current;
            Diagnostics.Log("Tour: " + what + " clicked at " + (tour == null ? "no tour" : tour.Step.ToString()));
            if (what == "next") GettingStarted.Did(TourEvent.Next);
            else if (what == "skip" || what == "done") GettingStarted.EndTour();
        }

        /// <summary>
        /// The getting-started checklist, in the empty state (no issues listed) during the writer's first week, until every
        /// item is done or they hide it. The Review tab step can't be seen from House Style, so it has its own Done link.
        /// </summary>
        private void UpdateChecklist()
        {
            if (_checklist == null) return;
            var show = false;
            try
            {
                // A list the filter emptied shows the empty line and Clear Filters, never the checklist.
                if (!_reviewing && _list.Items.Count == 0 && (_session.Open().Count == 0 || _session.Filter.IsAll))
                {
                    var o = GettingStarted.Load();
                    var aiOn = !FlareAI.Core.Settings.PluginSettings.Load().AiOff;
                    show = o.ChecklistVisible(DateTime.UtcNow, aiOn);
                    if (show)
                    {
                        var sb = new System.Text.StringBuilder("Getting started");
                        var links = new List<int[]>(); var data = new List<string>();
                        foreach (var item in Onboarding.Items(aiOn))
                        {
                            sb.Append("\n").Append(o.IsDone(item) ? "✓ " : "○ ").Append(Onboarding.Label(item));
                            if (item == ChecklistItem.AcceptOnReviewTab && !o.IsDone(item))
                            {
                                sb.Append("  "); links.Add(new[] { sb.Length, "Done".Length }); data.Add("review"); sb.Append("Done");
                            }
                        }
                        sb.Append("\n"); links.Add(new[] { sb.Length, "Hide".Length }); data.Add("hide"); sb.Append("Hide");
                        _checklist.Links.Clear();
                        _checklist.Text = sb.ToString();
                        _checklist.Links.Clear();
                        for (var i = 0; i < links.Count; i++) _checklist.Links.Add(links[i][0], links[i][1], data[i]);
                        _checklist.AccessibleDescription = _checklist.Text.Replace("\n", ". ");
                    }
                }
            }
            catch (Exception ex) { Diagnostics.Log("checklist", ex); show = false; }
            if (_checklist.Visible != show) _checklist.Visible = show;
            if (show) FitLabel(_checklist, 0);
        }

        private void OnChecklistLink(string what)
        {
            if (what == "review") GettingStarted.Mark(ChecklistItem.AcceptOnReviewTab);
            else if (what == "hide") GettingStarted.HideChecklist();
        }

        /// <summary>Show All Rules: the first Lint Project's focus on the top rules ends.</summary>
        private void ShowAllRules()
        {
            _focusTop = false; _focusLine = null;
            _showAllRules.Visible = false;
            Rebuild();
            _status.Text = Summary();
            _list.Focus();
        }

        private void OnNoticeChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)OnNoticeChanged); return; }
            var n = Toast.Last;
            // A tour notice the hint strip already shows isn't repeated here.
            var tour = GuidedTour.Current;
            _notice.Visible = n != null && !(tour != null && n.Text == tour.Hint);
            if (n == null) return;
            _notice.Links.Clear();
            _notice.Text = n.Action == null ? n.Text : n.Text + "  " + n.ActionLabel;
            if (n.Action != null) _notice.Links.Add(n.Text.Length + 2, n.ActionLabel.Length);
            _notice.AccessibleName = "Last notice: " + n.Text + (n.Action != null ? ". " + n.ActionLabel : "");
            _notice.Announce();
        }

        private const string ListHelp = "Issues in reading order. Each row starts with its severity. A filled circle is an error, a triangle is a warning and an open circle is a suggestion. Enter changes the issue. Delete ignores it.";
        private const string HeadingHelp = " File headings collapse with Left and Right.";

        /// <summary>Accessible names, descriptions and a reading-order tab sequence (status, triage, list, card, bar).</summary>
        private void NameControls()
        {
            A11y.Name(this, PaneTitle, "Issues in the open topic, with a card for the selected one.");
            A11y.Name(_progress, "Progress");
            A11y.Name(_list, "Issues", ListHelp);
            A11y.Name(_result, "Result of the last batch");
            A11y.Name(_cardDiff, "What changes", "The text around the change, with removed words struck through and added words underlined.");
            A11y.Name(_cardWhy, "Why", "The reason, the guide rule and its example.");
            A11y.Name(_accept, "Change", "Replace the text with the suggestion.");
            A11y.Name(_changeAll, "Change All", "Replace every instance of this issue in the topic.");
            A11y.Name(_reject, "Ignore", "Ignore this issue. Opens a menu of reasons.");
            A11y.Name(_goTo, "Go To", "Select this text in the topic.");
            A11y.Name(_openFile, "Open File", "Open the file that contains this issue.");
            A11y.Name(_edit, "Edit", "Change the suggested text before you apply it. Opens a dialog.");
            A11y.Name(_refine, "Rewrite", "Ask the AI for a different suggestion. Opens a dialog.");
            A11y.Name(_rule, "Open Guide", "Open this rule's page in the style guide.");
            A11y.Name(_recheck, "Lint Again");
            A11y.Name(_legend, "What the symbols mean", "Show what the symbols and buttons mean. Opens a dialog.");
            A11y.Name(_ignore, "Disable Rule", "Stop linting for this rule. Opens a dialog.");
            // Tab order: tour, notice, triage buttons, list, decision buttons, card text, bottom bar.
            var top = new Control[] { _tabs, _tourStrip, _widthTip, _status, _notice, _acceptAll.Parent, _viewBar, _progress, _list.Parent.Parent, _cost, _recheck.Parent };
            for (var i = 0; i < top.Length; i++) top[i].TabIndex = i;
            A11y.Name(_acceptAll.Parent, "Actions for all issues"); A11y.Name(_recheck.Parent, "Pane actions"); A11y.Name(_accept.Parent, "Decision for the selected issue");
            A11y.Name(_viewBar, "Project report", "List the project's issues by rule or by file, or save them to a file.");
            A11y.Name(_exemptions, "Exemptions", "The text exempted from lint for this project. Opens a dialog.");
            A11y.Name(_ignoredBtn, "Ignored Issues", "Show the issues you ignored. They stay hidden each time you lint.");
            A11y.Name(_export, "Export Issues", "Save the issues to a file you can open in Excel. Opens a dialog.");
            A11y.Name(_ruleActions, "Actions for the selected rule");
            A11y.Name(_fixRule, "Change All", "Replace every instance of this issue in every file. Opens a preview.");
            A11y.Name(_reviewRule, "Review One by One", "Go through this rule's issues one at a time.");
            A11y.Name(_skipRule, "Skip", "Hide this rule until you lint again.");
            A11y.Name(_backToRules, "All Rules", "List every rule again (Escape).");
            A11y.Name(_showAllRules, "Show All Rules");
            A11y.Name(_batchStop, "Stop after this file");
            A11y.Name(_tourNext, "Next", "Go to the last step.");
            A11y.Name(_tourSkip, "Skip Tour", "End the practice.");
            A11y.Name(_tourDone, "Done", "End the practice.");
            A11y.Name(_tabs, "Issues for");
            var order = new Control[] { _tourStrip, _widthTip, _notice, _acceptAll, _bulk, _stop, _batchStop, _legend, _byRule, _byFile, _backToRules, _showSkipped, _showAllRules, _checklist, _list, _result,
                                        _accept, _changeAll, _reject, _edit, _refine, _reanalyze, _rule, _fixRule, _reviewRule, _turnOffRule, _skipRule, _cardDiff, _cardWhy, _recheck, _ignore };
            for (var i = 0; i < order.Length; i++) order[i].TabIndex = i;
            A11y.Apply(this);
        }

        /// <summary>Starts the first check. Hosts call this once the panel is placed (it needs a window handle).</summary>
        public void Begin()
        {
            if (_started) return;
            _started = true;
            if (IsHandleCreated) BeginInvoke((Action)Start);
            else HandleCreated += (s, e) => BeginInvoke((Action)Start);
        }

        /// <summary>A button sized to its text, or to the pane's width with its text wrapped when the pane is narrower (<see cref="FitButton"/>).</summary>
        private Button Btn(FlowLayoutPanel bar, string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = false, Margin = new Padding(0, 0, 4, 4) };
            b.Click += (s, e) => Safe.Run(text, () => click(s, e));
            b.TextChanged += (s, e) => FitButton(b);
            b.FontChanged += (s, e) => FitButton(b);
            bar.Controls.Add(b);
            _buttons.Add(b);
            FitButton(b);
            return b;
        }

        // ---- narrow pane -------------------------------------------------------------------------

        private const string WidthTipText = "Drag this pane's edge to make it wider.";

        /// <summary>This pane's DPI scale (1 at 100%, 1.25 at 125%).</summary>
        private float Dpi { get { return DeviceDpi / 96f; } }

        private int S(int px) { return PaneRows.S(px, Dpi); }

        /// <summary>The pane changed width: buttons, labels and the card head follow, and the tip shows or hides.</summary>
        private void FitToWidth()
        {
            if (Width <= 0) return;
            foreach (var b in _buttons) FitButton(b);
            FitLabel(_status, 2); FitLabel(_notice, 0); FitLabel(_aiOffNote, 0); FitLabel(_cost, 0); LayoutTourStrip(); FitLabel(_checklist, 0);
            UpdateWidthTip();
            LayoutCard();
        }

        /// <summary>One line when it fits, else the full width with the text wrapped: a button never runs past the pane's edge.</summary>
        private void FitButton(Button b)
        {
            var bar = b.Parent;
            var outer = _card != null && bar != null && bar.Parent == _card && _cardWidth > 0 ? _cardWidth : ClientSize.Width - Padding.Horizontal;
            var width = outer - (bar != null ? bar.Padding.Horizontal : 0);
            var available = width - b.Margin.Horizontal;
            const TextFormatFlags Flags = TextFormatFlags.NoPrefix;
            var text = (b.Text ?? "").Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
            var one = TextRenderer.MeasureText(text, b.Font, Size.Empty, Flags);
            var wrapped = TextRenderer.MeasureText(text, b.Font, new Size(PaneRows.ButtonTextWidth(available, Dpi), 0), Flags | TextFormatFlags.WordBreak).Height;
            var size = PaneRows.ButtonSize(one, wrapped, available, Dpi);
            if (b.Size != size) b.Size = size;
        }

        /// <summary>A label as tall as its wrapped text (the status line: at least <paramref name="minLines"/> lines, at most 40% of the pane).</summary>
        private void FitLabel(Control label, int minLines)
        {
            if (label == null || (!label.Visible && label != _status)) return;
            var w = label.Width - label.Padding.Horizontal - S(4);
            if (w <= 0) return;
            var text = string.IsNullOrEmpty(label.Text) ? " " : label.Text;
            var measured = TextRenderer.MeasureText(text, label.Font, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            // A very long status ends with … (full text in its tooltip and for screen readers) so the list keeps its room.
            var max = label == _status && Height > 0 ? Math.Min(Height / 5, label.Font.Height * 4) : 0;
            var h = PaneRows.LabelHeight(measured, label.Font.Height, minLines, max, Dpi) + label.Padding.Vertical;
            if (label.Height != h) label.Height = h;
        }

        /// <summary>Shows the "make it wider" tip the first time a narrow pane appears in this Flare session, until Got it.</summary>
        private void UpdateWidthTip()
        {
            if (_widthTip == null) return;
            var narrow = PaneRows.ShowWidthTip(Width, Dpi);
            if (!_widthTipHere && narrow && !_widthTipShown && IsHandleCreated && Parent != null)
            {
                bool dismissed;
                try { dismissed = FlareAI.Core.Settings.PluginSettings.Load().PaneWidthTipDismissed; }
                catch (Exception ex) { Diagnostics.Log("width tip", ex); dismissed = true; }
                _widthTipShown = true;
                _widthTipHere = !dismissed;
            }
            var show = _widthTipHere && narrow;
            if (_widthTip.Visible != show) _widthTip.Visible = show;
            if (show) FitLabel(_widthTip, 0);
        }

        private void HideWidthTip()
        {
            _widthTipHere = false;
            _widthTip.Visible = false;
            try
            {
                var settings = FlareAI.Core.Settings.PluginSettings.Load();
                if (!settings.PaneWidthTipDismissed) { settings.PaneWidthTipDismissed = true; settings.Save(); }
            }
            catch (Exception ex) { Diagnostics.Log("width tip: save", ex); }
            _list.Focus();
        }

        private bool _layingOutCard, _settingSplitter, _draggingSplitter;
        private int _cardWanted;      // the height the card needs to show everything without scrolling
        private int _cardByWriter;    // the card height the writer chose by dragging the splitter (0 = not yet)

        private const int DiffHeight = 64;   // logical px: the "what changes" box at full size

        /// <summary>The buttons the card shows now: a suggestion's decision, a rule row's actions, or none.</summary>
        private FlowLayoutPanel CardButtons()
        {
            if (!RuleRows) return _actions;
            return CurrentRule() != null ? _ruleActions : null;
        }

        /// <summary>
        /// Places the card's parts top to bottom at the card's width: the heading (wrapped), what changes (at least two
        /// lines), the buttons (wrapped) and why (all of its text, at least three lines, so it never has a scroll bar of its
        /// own). Nothing overlaps; a card too short for all of it scrolls as a whole. Then asks the splitter for the height
        /// the card needs.
        /// </summary>
        private void LayoutCard()
        {
            if (_card == null || _actions == null || _ruleActions == null || _cardWhy == null || _layingOutCard) return;
            // Sized from the card's outer width, not its client width: whether the scroll bar shows depends on the height, and
            // the height the card asks for must not depend on the scroll bar (or the two would chase each other).
            var full = _card.Width;
            if (full <= 0) return;
            var total = 0;
            _layingOutCard = true;
            try
            {
                var flow = CardButtons();
                var diffMin = 2 * _cardDiff.Font.Height + S(8);
                var diffFull = Math.Max(diffMin, S(DiffHeight));
                var whyMin = 3 * _cardWhy.Font.Height + S(6);
                var ceiling = Math.Min(PaneRows.WhyCeilingMax, S(PaneRows.WhyCeiling));
                var top = S(6); var gap = S(4);
                int head, buttons;
                MeasureCard(flow, full, out head, out buttons);
                var whyText = MeasureWhy(full);
                var whyWanted = Math.Min(ceiling, Math.Max(whyText, whyMin));
                _cardWanted = top + head + diffFull + whyWanted + buttons + 3 * gap;
                // What the card asked for before the why text was shown in full (at most 160 px): the 60% cap never makes an
                // ordinary card smaller than that.
                _cardWantedShort = top + head + diffFull + PaneRows.Clamp(whyText, whyMin, S(160)) + buttons + 3 * gap;
                var w = full;
                var parts = PaneRows.CardLayout(_card.Height, top, head, buttons, diffMin, diffFull, whyMin, whyText, ceiling, gap);
                if (parts[2] > _card.Height)   // it will scroll: lay out beside the scroll bar, measuring the text again at that width
                {
                    // Decided once per pass: narrower text is never shorter, so the second layout scrolls too and can't flip back.
                    w = Math.Max(20, full - SystemInformation.VerticalScrollBarWidth);
                    MeasureCard(flow, w, out head, out buttons);
                    whyText = MeasureWhy(w);
                    parts = PaneRows.CardLayout(_card.Height, top, head, buttons, diffMin, diffFull, whyMin, whyText, ceiling, gap);
                }
                int diff = parts[0], why = parts[1];
                var y = top + _card.AutoScrollPosition.Y;   // ≤ 0 when the card is scrolled
                // The decision comes right after what changes: in a short pane the card scrolls to the why text, the buttons stay put.
                _cardHead.SetBounds(0, y, w, head); y += head + gap;
                _cardDiff.SetBounds(0, y, w, diff); y += diff + gap;
                if (flow != null) { flow.SetBounds(0, y, w, buttons); y += buttons + gap; }
                _cardWhy.SetBounds(0, y, w, why);
                total = parts[2];
            }
            finally { _layingOutCard = false; }
            // Tell the card how tall its content is: it shows the scroll bar exactly when that is more than it has.
            if (total > 0 && _card.AutoScrollMinSize.Height != total) _card.AutoScrollMinSize = new Size(0, total);
            if (total > 0 && (total > _card.Height) != _card.VerticalScroll.Visible) _card.PerformLayout();
            // Not now: this often runs inside the card's own resize, where a new splitter position doesn't resize the card.
            if (!_sizeCardPosted && IsHandleCreated && !IsDisposed)
            {
                _sizeCardPosted = true;
                BeginInvoke((Action)(() => { _sizeCardPosted = false; if (!IsDisposed) SizeCard(); }));
            }
        }

        private int _cardWantedShort;   // what the card would need with its why text held to the old 160 px box

        /// <summary>
        /// The why text's full height at width <paramref name="w"/>, read from the box itself (RichEdit's own line metrics, so
        /// italic and bold lines and symbol fallback count). Before the box has a handle, a GDI estimate stands in.
        /// </summary>
        private int MeasureWhy(int w)
        {
            if (_cardWhy.TextLength == 0) return 0;
            if (_cardWhy.Width != w) _cardWhy.Width = w;
            var h = _cardWhy.ContentHeight();
            if (h > 0) return h;
            return TextRenderer.MeasureText(_cardWhy.Text, _cardWhy.Font, new Size(Math.Max(20, w - S(10)), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + S(8);
        }

        /// <summary>Arrow keys and Page Down in the why text: the card scrolls so the caret's line stays in view.</summary>
        private void KeepCaretInView()
        {
            if (_card == null || !_cardWhy.Focused || !_card.VerticalScroll.Visible) return;
            var p = _cardWhy.GetPositionFromCharIndex(_cardWhy.SelectionStart);
            var lineTop = _cardWhy.Top + p.Y;   // in the card's client area (Top already includes the scroll offset)
            var lineBottom = lineTop + _cardWhy.Font.Height + S(4);
            var scroll = -_card.AutoScrollPosition.Y;
            if (lineTop < 0) scroll += lineTop;
            else if (lineBottom > _card.ClientSize.Height) scroll += lineBottom - _card.ClientSize.Height;
            else return;
            _card.AutoScrollPosition = new Point(0, Math.Max(0, scroll));
        }

        /// <summary>The card: a tall control taking focus (the why text) doesn't scroll the card to its top.</summary>
        private sealed class CardPanel : Panel
        {
            public Control KeepPlace;
            protected override Point ScrollToControl(Control activeControl)
            {
                if (activeControl == null || activeControl != KeepPlace) return base.ScrollToControl(activeControl);
                // Part of it on screen: stay put. All of it below or above the fold: bring its top into view.
                if (activeControl.Bottom > 0 && activeControl.Top < ClientSize.Height) return DisplayRectangle.Location;
                return new Point(DisplayRectangle.X, DisplayRectangle.Y - activeControl.Top);
            }
        }

        private bool _sizeCardPosted;
        private int _cardWidth;   // the width the card's buttons were last fitted to

        /// <summary>Fits the card's buttons to <paramref name="w"/> and measures the heading and the button rows at that width.</summary>
        private void MeasureCard(FlowLayoutPanel flow, int w, out int head, out int buttons)
        {
            _cardWidth = w;
            if (flow != null) foreach (Control c in flow.Controls) { var b = c as Button; if (b != null) FitButton(b); }
            var headText = string.IsNullOrEmpty(_cardHead.Text) ? " " : _cardHead.Text;
            head = TextRenderer.MeasureText(headText, _cardHead.Font, new Size(Math.Max(20, w - S(4)), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + S(6);
            buttons = flow == null ? 0 : flow.GetPreferredSize(new Size(w, 0)).Height;
        }

        /// <summary>
        /// The card gets the height it needs, up to 60% of the pane for a long rule (or the writer's, once they dragged the
        /// splitter); the list gets what's left, at least a row.
        /// </summary>
        private void SizeCard()
        {
            if (_split == null || _split.Height <= 0 || _cardWanted <= 0 || _settingSplitter) return;
            // Moving the splitter re-lays the card out, which can change what it needs (a new width wraps the buttons
            // differently): go again until it settles.
            for (var pass = 0; pass < 3; pass++)
            {
                var wanted = _cardWanted;
                var row = _list.SmallImageList != null ? _list.SmallImageList.ImageSize.Height : _list.Font.Height + S(6);
                var listMin = _list.Font.Height + S(10) + row + S(4);   // the header and one row
                var target = PaneRows.CardHeight(wanted, _cardWantedShort, _cardByWriter, _split.Height, _split.SplitterWidth, listMin);
                var distance = _split.Height - target - _split.SplitterWidth;
                if (distance < _split.Panel1MinSize || Math.Abs(_split.SplitterDistance - distance) <= 1) return;
                _settingSplitter = true;
                try { _split.SplitterDistance = distance; _split.Panel2.PerformLayout(); }
                catch (Exception) { return; }
                finally { _settingSplitter = false; }
                if (_cardWanted == wanted) return;
            }
        }

        /// <summary>
        /// One column of wrapped rows when the list is narrow, two one-line columns when it is wide; the row height follows
        /// (a ListView's rows take their height from its small image list).
        /// </summary>
        private bool _fitting;   // setting a column width can resize the list again

        private void FitColumns()
        {
            if (_fitting) return;
            _fitting = true;
            try { FitColumnsCore(); } finally { _fitting = false; }
        }

        private void FitColumnsCore()
        {
            var lines = PaneRows.TextLines(_list.Width, Dpi);
            if (lines != _rowLines)
            {
                _rowLines = lines;
                if (_metaFont != null) _metaFont.Dispose();
                _metaFont = new Font(_list.Font.FontFamily, Math.Max(6f, _list.Font.SizeInPoints - 1f), FontStyle.Regular, GraphicsUnit.Point);
                var h = PaneRows.RowHeight(lines, lines == 1, _list.Font.Height, _metaFont.Height, Dpi);
                var old = _list.SmallImageList;
                _list.SmallImageList = new ImageList { ImageSize = new Size(1, Math.Min(256, h)) };
                if (old != null) old.Dispose();
            }
            var w = Math.Max(S(40), _list.ClientSize.Width - 4);
            if (lines == 1)
            {
                _list.Columns[0].Width = w * 62 / 100;
                _list.Columns[1].Width = w - _list.Columns[0].Width;
            }
            else
            {
                _list.Columns[0].Width = w;
                _list.Columns[1].Width = 0;
            }
            _list.Invalidate();
        }

        private const TextFormatFlags OneLine = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        /// <summary>
        /// Paints a row: the severity glyph and suggestion (wrapped, or one line when wide), then the source or counts in small
        /// grey type. Colour and strike-through come from the item (Restyle), so an applied fix still reads as struck through.
        /// </summary>
        private void DrawRow(DrawListViewItemEventArgs e)
        {
            var item = e.Item;
            var g = e.Graphics;
            var b = e.Bounds;
            var focused = _list.Focused;
            var selected = item.Selected;
            var back = !selected ? _list.BackColor : focused ? SystemColors.Highlight : SystemColors.ButtonFace;
            var fore = !_list.Enabled ? SystemColors.GrayText : selected && focused ? SystemColors.HighlightText : item.ForeColor;
            var grey = selected && focused ? SystemColors.HighlightText : SystemColors.GrayText;
            using (var brush = new SolidBrush(back)) g.FillRectangle(brush, b);
            if (item.Tag is FileHeading)
            {
                using (var bold = new Font(_list.Font, FontStyle.Bold))
                {
                    var hp = S(4);
                    var area = new Rectangle(b.X + hp, b.Y, Math.Max(1, b.Width - 2 * hp), b.Height);
                    TextRenderer.DrawText(g, item.Text, bold, area, fore, OneLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                if ((e.State & ListViewItemStates.Focused) != 0 && focused) ControlPaint.DrawFocusRectangle(g, b, fore, back);
                return;
            }
            var meta = item.SubItems.Count > 1 ? item.SubItems[1].Text : "";
            var font = item.Font ?? _list.Font;
            var pad = S(4);
            if (_rowLines <= 1 || _metaFont == null)
            {
                var c0 = _list.Columns[0].Width;
                var first = new Rectangle(b.X + pad, b.Y, Math.Max(1, c0 - 2 * pad), b.Height);
                var second = new Rectangle(b.X + c0 + pad, b.Y, Math.Max(1, b.Width - c0 - 2 * pad), b.Height);
                TextRenderer.DrawText(g, item.Text, font, first, fore, OneLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, meta, _list.Font, second, grey, OneLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                var width = Math.Max(1, b.Width - 2 * pad);
                var y = b.Y + S(3);
                foreach (var line in Wrapped(item.Text, font, width, _rowLines))
                {
                    TextRenderer.DrawText(g, line, font, new Point(b.X + pad, y), fore, OneLine);
                    y += font.Height;
                }
                y += S(1);   // the meta line follows the text, however many lines it used
                var metaFont = _metaFont;
                var metaText = PaneRows.Ellipsize(meta, s => TextRenderer.MeasureText(g, s, metaFont, Size.Empty, OneLine).Width, width);
                TextRenderer.DrawText(g, metaText, metaFont, new Point(b.X + pad, y), grey, OneLine);
            }
            if ((e.State & ListViewItemStates.Focused) != 0 && focused) ControlPaint.DrawFocusRectangle(g, b, fore, back);
        }

        /// <summary>A row's text wrapped at this width, remembered until the width changes (painting a row must stay cheap).</summary>
        private List<string> Wrapped(string text, Font font, int width, int lines)
        {
            if (width != _wrappedWidth || _wrapped.Count > 4000) { _wrapped.Clear(); _wrappedWidth = width; }
            var key = lines + "|" + (int)font.Style + "|" + text;
            List<string> result;
            if (_wrapped.TryGetValue(key, out result)) return result;
            result = PaneRows.Wrap(text, s => TextRenderer.MeasureText(s, font, Size.Empty, OneLine).Width, width, lines);
            _wrapped[key] = result;
            return result;
        }

        /// <summary>Right-click on a row: Go to, Open file (the whole topic or snippet, nothing selected), Copy file path.</summary>
        private ContextMenuStrip BuildRowMenu()
        {
            var menu = new ContextMenuStrip();
            var goTo = menu.Items.Add("Go To", null, (s, e) => Safe.Run("Go To", () => _session.GoToCurrent()));
            var markFixed = menu.Items.Add("Mark as Fixed", null, (s, e) => Safe.Run("Mark as Fixed", _session.MarkFixed));
            markFixed.AccessibleName = "Mark as Fixed";
            var open = menu.Items.Add("Open File", null, (s, e) => Safe.Run("Open File", () => _session.OpenFileOfCurrent()));
            menu.Items.Add("Copy File Path", null, (s, e) => Safe.Run("Copy File Path", () =>
            {
                var path = _session.FilePathOf(Current());
                if (path == null) return;
                try { Clipboard.SetText(path); _status.Text = "Copied " + path; }
                catch (Exception ex) { Diagnostics.Log("copy path", ex); _status.Text = "Could not copy the path: " + ex.Message; }
            }));
            var exSep = new ToolStripSeparator();
            menu.Items.Add(exSep);
            var exFile = menu.Items.Add("Exclude File from Lint Project", null, (s, e) => Safe.Run("Exclude file", () => Exclude(false)));
            var exFolder = menu.Items.Add("Exclude Folder from Lint Project", null, (s, e) => Safe.Run("Exclude folder", () => Exclude(true)));
            menu.Items.Add(new ToolStripSeparator());
            var report = menu.Items.Add(ReportFalsePositiveLabel, null, (s, e) => Safe.Run("Report False Positive", ReportFalsePositive));
            var exempt = menu.Items.Add(ExemptLabel, null, (s, e) => Safe.Run("Exempt", ExemptSelectedHere));
            exempt.ToolTipText = exempt.AccessibleDescription = ExemptTip;
            menu.Opening += (s, e) =>
            {
                var p = _list.PointToClient(Cursor.Position);
                var hit = _list.GetItemAt(p.X, p.Y);
                if (hit != null && !hit.Selected) hit.Selected = true;
                var f = Current();
                _session.Current = f;   // the menu's commands act on the session's current row; the selection handler runs later
                if (f == null) { e.Cancel = true; return; }
                goTo.Enabled = !f.IsFixed;
                markFixed.Enabled = !f.IsFixed;
                open.Enabled = _session.FilePathOf(f) != null;
                report.Enabled = !string.IsNullOrEmpty(f.Excerpt);
                exempt.Visible = _session.CanExempt(f);
                var folder = _files != null && f.DocumentPath != null ? LintExclusions.EntryFor(f.DocumentPath, true) : null;
                exSep.Visible = exFile.Visible = _files != null && f.DocumentPath != null;
                exFolder.Visible = folder != null;
                if (folder != null) exFolder.Text = "Exclude Folder " + folder.TrimEnd('\\') + " from Lint Project";
            };
            return menu;
        }

        /// <summary>Lint Project leaves the selected issue's file (or its folder) out from now on, for everyone on the project.</summary>
        private void Exclude(bool folder)
        {
            var path = Current() == null ? null : Current().DocumentPath;
            var entry = path == null ? null : LintExclusions.EntryFor(path, folder);
            if (entry == null) return;
            try { LintExclusions.Add(path, entry); }
            catch (Exception ex) { Diagnostics.Log("exclude from lint project", ex); Notify.Failed(FindForm(), "exclude " + entry, ex, "Lint Project"); return; }
            var entries = LintExclusions.Load(path);
            _files = _files.Where(f => !LintExclusions.IsExcluded(f, entries)).ToList();
            _session.RemoveFiles(f => LintExclusions.IsExcluded(f, entries));
            _status.Text = "Excluded " + entry + " from Lint Project.";
            UpdateExcluded();
        }

        /// <summary>The project's excluded files and folders, to lint again.</summary>
        private void ShowExcluded()
        {
            var anyPath = _files.Count > 0 ? _files[0] : _doc.SourcePath;
            using (var f = new ExcludedFilesForm(anyPath)) f.ShowDialog(FindForm());
            UpdateExcluded();
        }

        private void UpdateExcluded()
        {
            var n = _files == null ? 0 : LintExclusions.Load(_files.Count > 0 ? _files[0] : _doc.SourcePath).Count;
            _excluded.Visible = n > 0;
            _excluded.Text = "Excluded Files (" + n + ")";
        }

        /// <summary>The issues the writer ignored, to restore. The count and the status line follow the restore.</summary>
        private void ShowIgnored()
        {
            using (var f = new IgnoredIssuesForm(_session)) f.ShowDialog(FindForm());
            UpdateIgnored();
        }

        private void OnIgnoredChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)OnIgnoredChanged); return; }
            UpdateIgnored();
        }

        private void UpdateIgnored()
        {
            var n = _session.Ignored.Count;
            _ignoredBtn.Visible = n > 0;
            _ignoredBtn.Text = "Ignored Issues (" + n + ")";
        }

        /// <summary>The project's exempted text, to review or remove.</summary>
        private void ShowExemptions()
        {
            using (var f = new ExemptionsForm(_doc.SourcePath, RuleTitle)) f.ShowDialog(FindForm());
            UpdateExemptions();
        }

        private void UpdateExemptions()
        {
            var n = Exemptions.Load(_doc.SourcePath).Entries.Count;
            _exemptions.Visible = n > 0;
            _exemptions.Text = "Exemptions (" + n + ")";
        }

        /// <summary>Exempts the selected issue, then refreshes the Exemptions button.</summary>
        private void ExemptSelectedHere()
        {
            _session.ExemptSelected();
            UpdateExemptions();
        }

        /// <summary>The rule's display title for the Exemptions list; null when the rule isn't known.</summary>
        private string RuleTitle(string ruleId)
        {
            var rule = _runner.RuleFor(ruleId);
            return rule != null && !string.IsNullOrEmpty(rule.Display) ? rule.Display : null;
        }

        /// <summary>The status line's exempt piece: "  2 issues are exempt.", or nothing when there are none.</summary>
        private string ExemptNote()
        {
            var tail = _session.ExemptTail;
            return string.IsNullOrEmpty(tail) ? "" : "  " + tail;
        }

        // ---- end-to-end runner (E2EHook) ---------------------------------------------------------------

        /// <summary>The status line and the rows as the writer sees them.</summary>
        internal object E2EState()
        {
            var rows = new List<object>();
            foreach (ListViewItem i in _list.Items)
            {
                var f = i.Tag as StyleFinding;
                var h = i.Tag as FileHeading;
                rows.Add(new { text = i.Text, source = i.SubItems.Count > 1 ? i.SubItems[1].Text : "", file = h != null ? h.Path : f == null ? null : f.DocumentPath, rule = f == null ? null : f.Rule, excerpt = f == null ? null : f.Excerpt, layer = f == null ? null : f.Layer, heading = h != null, collapsed = h != null && _session.IsCollapsed(h.Path) });
            }
            // Tab bar captions without the & mnemonics
            var tabCaptions = new List<string>();
            if (_tabs.Visible)
            {
                tabCaptions.Add(_tabTopic.Text.Replace("&", ""));
                tabCaptions.Add(_tabProject.Text.Replace("&", ""));
            }
            var tab = Slot == PaneSlot.Topic ? "topic" : "project";
            // id: every lint makes a new panel, so the same id means the same results, not a new lint.
            return new { id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this), title = Title, status = _status.Text, reviewing = _reviewing, rulesOnly = _rulesOnly, rows, card = E2ECard(), tab, tabs = tabCaptions, walk = E2EWalk(), filter = new { level = _levelBox.Text, source = _sourceBox.Text } };
        }

        /// <summary>
        /// The walk as the scenario reads it: the rule's key (never its title or text), the place, whether the walk went past its
        /// last place, and whether the list has the focus. Null when no rule is walked.
        /// </summary>
        private object E2EWalk()
        {
            if (!_session.IsWalking) return null;
            return new { rule = _session.FocusRule, position = _session.WalkPosition, total = _session.WalkTotal, finished = _session.WalkFinished, listFocused = ListHasFocus };
        }

        /// <summary>The issues list has the keyboard focus (or one of its children does).</summary>
        private bool ListHasFocus { get { return _list.Focused || _list.ContainsFocus; } }

        /// <summary>
        /// The card as laid out: the why text's full height comes from the box itself, so whyClipped is true when the box is
        /// shorter than its text (the bug in #247).
        /// </summary>
        private object E2ECard()
        {
            var f = Current();
            var rule = f == null ? null : _runner.RuleFor(f.RuleId);
            var r = CurrentRule();
            var content = _cardWhy.ContentHeight();
            return new
            {
                row = _list.SelectedIndices.Count == 1 ? _list.SelectedIndices[0] : -1,
                head = _cardHead.Text,
                why = _cardWhy.Text,
                ruleLine = r != null ? r.Reason : rule != null ? rule.Display : null,
                whyContentHeight = content,
                whyHeight = _cardWhy.ClientSize.Height,
                whyClipped = content > _cardWhy.ClientSize.Height,
                whyScrollBar = _cardWhy.ScrollBars.ToString(),
                cardScrolls = _card.VerticalScroll.Visible,
                cardHeight = _card.ClientSize.Height,
                paneWidth = Width,
                paneHeight = Height,
            };
        }

        /// <summary>
        /// Right-clicks row <paramref name="index"/> (the real menu, so its Opening handler runs) and clicks the first item
        /// whose text starts with <paramref name="prefix"/>; returns the items the menu showed.
        /// </summary>
        internal List<string> E2ERowMenu(int index, string prefix)
        {
            var item = _list.Items[index];
            _list.SelectedItems.Clear();
            item.Selected = true;
            var at = new Point(item.Bounds.Left + 8, item.Bounds.Top + item.Bounds.Height / 2);
            Cursor.Position = _list.PointToScreen(at);   // Opening hit-tests the cursor
            var menu = _list.ContextMenuStrip;
            menu.Show(_list, at);
            var shown = new List<string>();
            ToolStripItem hit = null;
            foreach (ToolStripItem m in menu.Items)
            {
                if (m is ToolStripSeparator || !m.Available) continue;
                shown.Add(m.Text);
                if (hit == null && prefix != null && m.Text.StartsWith(prefix, StringComparison.Ordinal)) hit = m;
            }
            menu.Close();
            if (hit != null) hit.PerformClick();
            return shown;
        }

        /// <summary>
        /// Brings the pane's window forward and puts the keyboard focus in the issues list, selecting the first row when
        /// none is. False when the focus didn't arrive (a modal dialog is open, say).
        /// </summary>
        internal bool E2EFocusList()
        {
            var form = FindForm();
            if (form != null && form.CanFocus) form.Activate();
            var first = FirstRow();
            if (_list.SelectedItems.Count == 0 && first != null) { first.Selected = true; first.Focused = true; }
            _list.Focus();
            return ListHasFocus;
        }

        /// <summary>Selects row <paramref name="index"/> and, when it is a file heading, collapses or expands it (like a click).</summary>
        internal void E2EClickRow(int index)
        {
            if (index < 0 || index >= _list.Items.Count)
                throw new InvalidOperationException("No pane row " + index + " (the pane has " + _list.Items.Count + " rows)");
            ClickRow(_list.Items[index]);
        }

        /// <summary>
        /// Clicks the first available, enabled item of the pane's open drop-down menu (Ignore's reasons, or the row menu)
        /// whose text, without "&amp;" or "…", starts with <paramref name="prefix"/>.
        /// </summary>
        internal void E2EMenuPick(string prefix)
        {
            var menu = _rejectMenu.Visible ? _rejectMenu : _list.ContextMenuStrip != null && _list.ContextMenuStrip.Visible ? _list.ContextMenuStrip : null;
            if (menu == null) throw new InvalidOperationException("No open menu");
            var hit = menu.Items.Cast<ToolStripItem>().FirstOrDefault(m => !(m is ToolStripSeparator) && m.Available && m.Enabled &&
                                                                         m.Text.Replace("&", "").TrimEnd('…').StartsWith(prefix ?? "", StringComparison.Ordinal));
            if (hit == null) throw new InvalidOperationException("No menu item " + prefix + E2EHook.Valid(
                menu.Items.Cast<ToolStripItem>().Where(m => !(m is ToolStripSeparator) && m.Available).Select(m => m.Text.Replace("&", "").TrimEnd('…'))));
            menu.Close();
            hit.PerformClick();
        }

        public const string ReportFalsePositiveLabel = "Report False Positive…";
        private const string ExemptLabel = "Exempt This Text…";
        private const string ExemptTip = "Hides this issue for everyone on this project.";
        private const string IgnoreTip = "Hides this issue for you only.";

        /// <summary>
        /// Report False Positive (issue #124): the rule, the flagged words and the writer's reason go to the House Style
        /// team, and the sentence around them only if the writer ticks it. The issue stays in the list.
        /// </summary>
        private void ReportFalsePositive()
        {
            var f = Current();
            if (f == null) return;
            var rule = _runner.RuleFor(f.RuleId);
            var name = rule != null && !string.IsNullOrEmpty(rule.Display) ? rule.Display : f.Rule;
            string sentence = null;
            try { if (f.Block != null) sentence = FlareAI.Core.Feedback.ReportBuilder.SentenceAround(f.Block.InnerText, f.Excerpt); }
            catch (Exception ex) { Diagnostics.Log("false positive sentence", ex); }
            using (var form = new FalsePositiveForm(f.RuleId, name, f.Excerpt, sentence, FeedbackService.Context()))
                if (form.ShowDialog(this) == DialogResult.OK) _status.Text = FalsePositiveForm.Sent;
        }

        private void OnListKey(object sender, KeyEventArgs e)
        {
            if (RuleRows)
            {
                if (e.KeyCode == Keys.Enter && CurrentRule() != null) { ReviewRule(); e.Handled = true; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Delete && CurrentRule() != null) { _session.SkipRule(CurrentRule()); e.Handled = true; }
                return;
            }
            if ((e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back) && _session.FocusRule != null) { _session.EndRuleReview(); e.Handled = true; e.SuppressKeyPress = true; return; }
            if (FileRows)
            {
                var heading = SelectedHeading();
                if (heading != null)
                {
                    if (e.KeyCode == Keys.Left) SetFileCollapsed(heading.Path, true);
                    else if (e.KeyCode == Keys.Right) SetFileCollapsed(heading.Path, false);
                    else if (e.KeyCode == Keys.Enter) SetFileCollapsed(heading.Path, !_session.IsCollapsed(heading.Path));
                    else if (e.KeyCode != Keys.Delete) return;
                    e.Handled = true; e.SuppressKeyPress = true;
                    return;
                }
                var cur = Current();
                if (e.KeyCode == Keys.Left && !e.Control && !e.Alt && !e.Shift && cur != null && cur.DocumentPath != null)
                {
                    SetFileCollapsed(cur.DocumentPath, true);
                    e.Handled = true; e.SuppressKeyPress = true;
                    return;
                }
            }
            if (WalkKey(e)) { e.Handled = true; e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Enter && e.Shift) { _session.OpenFileOfCurrent(); FocusWalkList(); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter && e.Control) { _session.GoToCurrent(); FocusWalkList(); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { _rejectMenu.Show(_reject, new Point(0, _reject.Height)); e.Handled = true; }
        }

        // ---- check -------------------------------------------------------------------------------

        private void Start()
        {
            if (_worker != null && _worker.IsBusy) return;
            // The guided first check shows every issue, whatever filter the writer saved: the saved one comes back when it ends.
            if (_files == null && GuidedTour.Current != null && GuidedTour.Current.Step == TourStep.LintTopic && !_session.Filter.IsAll)
            {
                _guidedSaved = _session.Filter;
                _session.Filter = ReviewFilter.All;   // in memory only: ReviewViewPrefs is not saved
            }
            _session.Clear(); UpdateIgnored(); _list.Items.Clear(); _list.Groups.Clear(); _lintNote = null;
            _aiOff = FlareAI.Core.Settings.PluginSettings.Load().AiOff; _aiHidden = false; _aiOffNote.Visible = false;
            _reviewing = true; _progress.Visible = true; _progress.Style = ProgressBarStyle.Marquee; _status.Text = "Linting…";
            _result.Visible = false; _stop.Visible = false; _stop.Enabled = true; _stopped = false; _unitCount = 0; _doneUnits = 0;
            ShowCard();
            UpdateChecklist();

            _worker = new BackgroundWorker { WorkerReportsProgress = true, WorkerSupportsCancellation = true };
            if (_files != null) { StartProject(); return; }
            var cachedAny = false;
            var fresh = _fresh && !_aiOff; _fresh = false;
            var rulesOnly = _rulesOnly;
            _worker.DoWork += (s, e) =>
            {
                _requestNote = _session.CheckRequests();   // outcomes of the writer's requests to the owners
                var blocks = _wholeTopic ? _runner.PickAllBlocks(_doc) : _runner.PickCheckBlocks(_doc);
                List<StyleFinding> lint;
                try { lint = _runner.Lint(blocks); }
                catch (Exception ex) { Diagnostics.Log("docstyle", ex); lint = new List<StyleFinding>(); _lintNote = "The rules couldn't be loaded, so only the AI review is shown. For details, see Diagnostics in Settings."; }
                try { lint.AddRange(_runner.StructureFindings(blocks)); }
                catch (Exception ex) { Diagnostics.Log("structure", ex); }
                try { lint.AddRange(_runner.LintSnippets(blocks, _doc.SourcePath)); }
                catch (Exception ex) { Diagnostics.Log("snippets", ex); }
                _session.PrimeSnippetUses(lint);
                // The AI review is split into small units (one block each, by default) reviewed several at a time, so the
                // first cards arrive in seconds and each model call has one focused job. Prompts read the DOM: build them here.
                var units = rulesOnly ? new List<ReviewUnit>() : _runner.PrepareReview(blocks, lint);
                _lint = lint;
                _worker.ReportProgress(0, new object[] { lint, null, 0, units.Count });  // from here the author may edit while we wait
                var done = 0;
                // The first unit alone warms the prompt cache; the rest then read it (see ActionRunner.WarmThenParallel).
                ActionRunner.WarmThenParallel(units.Count, _runner.Parallelism, i =>
                {
                    if (_worker.CancellationPending) return;
                    var hidden = false;
                    var raw = _cache == null ? null : _cache.Shown(units[i], _aiOff, fresh, out hidden);   // exact hit (not on Check again), or re-anchored after applied fixes
                    if (hidden) _aiHidden = true;
                    if (raw != null) cachedAny = true;
                    else if (_aiOnStart && !_aiOff)
                    {
                        raw = _runner.CallReview(units[i]);
                        if (_cache != null) _cache.Put(units[i], raw);
                    }
                    var n = System.Threading.Interlocked.Increment(ref done);
                    if (raw != null) _worker.ReportProgress(n, new object[] { raw, units[i], n, units.Count });
                });
                if (_worker.CancellationPending) e.Cancel = true;
            };
            _worker.ProgressChanged += (s, e) =>
            {
                if (IsDisposed) return;
                var p = (object[])e.UserState;
                var found = (List<StyleFinding>)p[0];
                var unit = p[1] as ReviewUnit;
                int doneUnits = (int)p[2], total = (int)p[3];
                _session.Add(unit == null ? found : _runner.AnchorReview(found, unit, _lint)); // DOM work on the UI thread
                _unitCount = total; _doneUnits = doneUnits;
                if (_aiOnStart && !_aiOff && doneUnits < total && !_stopped)
                {
                    // Rule issues are already in the list, and they cost nothing; only this phase spends.
                    _status.Text = Summary() + "  Reviewing wording with AI, " + doneUnits + " of " + total + " paragraphs. You can work on these issues now.";
                    _stop.Visible = true;
                }
                else _status.Text = Summary();
                _cost.Text = AiCost.Session();
            };
            _worker.RunWorkerCompleted += (s, e) =>
            {
                if (IsDisposed) return;
                RestoreGuidedFilter();
                _reviewing = false; _progress.Visible = false; _stop.Visible = false;
                _cost.Text = AiCost.Session();
                if (e.Cancelled)
                {
                    _status.Text = Summary() + "  Stopped after " + _doneUnits + " of " + _unitCount + " paragraphs. Lint Again reviews the rest.";
                    UpdateButtons();
                    UpdateChecklist();
                    return;
                }
                if (e.Error != null)
                {
                    Diagnostics.Log("Lint topic", e.Error); ErrorReporter.Background(this, "House Style couldn't lint the topic.", e.Error);
                    _status.Text = Summary() + "  " + (e.Error.Message == FlareAI.Core.Llm.LlmClientFactory.AiOffMessage
                        ? "AI review is off. Turn it on in Settings."   // the writer chose No AI: not a failure
                        : "AI review failed. " + e.Error.Message);
                }
                else if (_rulesOnly)
                    _status.Text = Open().Count == 0 ? Summary() : Summary() + "  " + LintChecksChoice.StatusEnding;
                else if (_aiOff)
                    _status.Text = Open().Count == 0 ? Summary() : Summary() + "  These issues come from the rules only. No AI was used.";
                else if (!_aiOnStart && !cachedAny)
                    _status.Text = Summary() + "  These issues come from the rules only. Lint Again" + (_unitCount > 0 ? " reviews " + Plural.Of(_unitCount, "paragraph") + " with AI." : " adds the AI wording review.");
                else _status.Text = Summary();
                _status.Text += ExemptNote();
                if (_lintNote != null) _status.Text += "  " + _lintNote;
                _aiOffNote.Visible = _aiOff && _aiHidden && !_rulesOnly;
                var guide = _runner.GuideNotice;
                if (guide != null) _status.Text += "  " + guide;
                ShowRequestNote();
                var team = Team.Current;
                if (team != null && team.Status != null && team.Status.Behind > 0) _status.Text += "  " + team.Status;
                if (_list.SelectedItems.Count == 0) _session.Next();
                UpdateExemptions();
                UpdateIgnored();
                UpdateButtons();
                UpdateChecklist();
                GettingStarted.Did(TourEvent.TopicLinted);
            };
            _worker.RunWorkerAsync();
        }

        /// <summary>Outcomes of the writer's requests (approved, declined), once each: in the status line and as a notice.</summary>
        private void ShowRequestNote()
        {
            var note = _requestNote;
            _requestNote = null;
            if (string.IsNullOrEmpty(note)) return;
            _status.Text += "  " + note;
            try { Toast.Show(FindForm(), note); } catch (Exception ex) { Diagnostics.Log("request notice", ex); }
        }

        private void StopReview()
        {
            if (_worker == null || !_worker.IsBusy) return;
            _stopped = true;
            _stop.Enabled = false;
            _status.Text = "Stopping after the paragraphs already being reviewed…";
            _worker.CancelAsync();
        }

        private void StartProject()
        {
            _status.Text = "Linting " + Plural.Of(_files.Count, "file") + "…";
            _worker.DoWork += (s, e) => { _requestNote = _session.CheckRequests(); e.Result = _runner.LintFiles(_files, (done, total) => _worker.ReportProgress(done * 100 / Math.Max(1, total), done + " of " + total)); };
            _worker.ProgressChanged += (s, e) => { if (!IsDisposed) _status.Text = "Linting " + e.UserState + " files…"; };
            _worker.RunWorkerCompleted += (s, e) =>
            {
                if (IsDisposed || e.Cancelled) return;
                _reviewing = false; _progress.Visible = false;
                if (e.Error != null) { Diagnostics.Log("Lint project", e.Error); ErrorReporter.Background(this, "House Style couldn't lint the project.", e.Error); _status.Text = "Lint Project failed. " + e.Error.Message; }
                else
                {
                    var found = (List<StyleFinding>)e.Result;
                    FirstProjectLint();
                    _session.Add(found);
                    var lint = Summary() + "  Linted " + Plural.Of(_files.Count, "file") + ". No AI was used." + ExemptNote() + (_focusLine != null ? "  " + _focusLine : "");
                    _status.Text = _session.RememberedRule != null ? _session.Status + "  " + lint : lint;   // the rule walked last is named, not started
                    var guide = _runner.GuideNotice;
                    if (guide != null) _status.Text += "  " + guide;
                    ShowRequestNote();
                    if (RuleRows && _list.Items.Count > 0)
                    {
                        // The rule walked last is the row selected (not walked); else the most frequent rule.
                        var row = _list.Items[0];
                        if (_session.RememberedRule != null)
                        {
                            foreach (ListViewItem item in _list.Items) if (((BacklogRule)item.Tag).Key == _session.RememberedRule.Key) row = item;
                        }
                        row.Selected = true; row.Focused = true;
                    }
                    else if (FileRows) SelectFirstIssueRow();
                    GettingStarted.Mark(ChecklistItem.LintProject);
                    UpdateExcluded();
                    UpdateExemptions();
                    UpdateIgnored();
                }
                UpdateButtons();
                UpdateChecklist();
            };
            _worker.RunWorkerAsync();
        }

        /// <summary>The writer's first Lint Project starts with the top rules (Show All Rules lists the rest); later ones list every rule.</summary>
        private void FirstProjectLint()
        {
            try
            {
                var o = GettingStarted.Load();
                if (o.ProjectLinted) return;
                o.ProjectLinted = true;
                o.Save();
                _focusTop = true;
            }
            catch (Exception ex) { Diagnostics.Log("first project lint", ex); }
        }

        /// <summary>Redraws the list in the session's order (it changed), keeping the selection.</summary>
        private void Rebuild()
        {
            RefreshFilterBar();
            if (_files != null)
            {
                var reviewing = _session.FocusRule != null;
                _backToRules.Visible = reviewing && !ShowsWalk;   // during a walk the strip's All Rules is the one
                _showSkipped.Visible = _session.SkippedRuleCount > 0 && !reviewing;
                _showSkipped.Text = "Show Skipped Rules (" + _session.SkippedRuleCount + ")";
                _list.Columns[0].Text = RuleRows ? "Rule" : "Issue";
                _list.Columns[1].Text = RuleRows ? "How many" : "Source";
                _showAllRules.Visible = RuleRows && _focusTop;
                if (RuleRows) { RebuildRules(); return; }
            }
            _actions.Visible = true; _ruleActions.Visible = false;
            var keep = Current();
            var focus = _session.FocusRule;
            var fileRows = FileRows;
            var keepHeading = fileRows ? SelectedHeading() : null;
            var keepHeadingPath = keepHeading == null ? null : keepHeading.Path;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear(); _list.Groups.Clear();
                var snapshot = new List<StyleFinding>();
                foreach (var f in new List<StyleFinding>(_session.Findings))   // a snapshot: the session's list is never read while it changes
                {
                    if (focus != null && RuleBacklog.KeyOf(f) != focus) continue;
                    if (!_session.Filter.IsAll && !_session.Filter.Shows(f)) continue;
                    snapshot.Add(f);
                }
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (fileRows)
                    foreach (var f in snapshot)
                    {
                        var key = f.DocumentPath ?? "";
                        int n; counts.TryGetValue(key, out n); counts[key] = n + 1;
                    }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ListViewItem selectedRow = null;
                foreach (var f in snapshot)
                {
                    var collapsed = false;
                    if (fileRows)
                    {
                        var path = f.DocumentPath ?? "";
                        collapsed = _session.IsCollapsed(path);
                        if (seen.Add(path))
                        {
                            var text = FileHeadings.Text(RelativeToContent(path), counts[path], collapsed);
                            var heading = new ListViewItem(new[] { text, "" }) { Tag = new FileHeading { Path = path, Count = counts[path] }, ToolTipText = text };
                            _list.Items.Add(heading);
                            if (keepHeadingPath != null && string.Equals(path, keepHeadingPath, StringComparison.OrdinalIgnoreCase)) selectedRow = heading;
                            if (collapsed && keep != null && string.Equals(path, keep.DocumentPath ?? "", StringComparison.OrdinalIgnoreCase)) selectedRow = heading;
                        }
                        if (collapsed) continue;
                    }
                    var item = new ListViewItem(new[] { A11y.SeverityGlyph(f.Severity) + " " + ProposalText(f), LayerLabel(f) }) { Tag = f };
                    if (!fileRows) item.Group = GroupFor(f);
                    item.ToolTipText = A11y.SeverityLabel(f.Severity) + ". " + A11y.SeverityHelp(f.Severity) + "\n" + LayerLabel(f) + " · " + ReadableRule(f) + "\n" + f.Suggestion;
                    Restyle(item);
                    _list.Items.Add(item);
                    if (f == keep) { if (fileRows) selectedRow = item; else item.Selected = true; }
                }
                if (selectedRow != null) { selectedRow.Selected = true; selectedRow.Focused = true; }
            }
            finally { _list.EndUpdate(); }   // never leave the list frozen (it would look empty)
            _session.Current = Current();
            ShowCard();   // the selection handler is deferred (QueueSelectionChanged): show the kept row's card now
            UpdateChecklist();
            var changed = Changed;
            if (changed != null) try { changed(this); } catch (Exception ex) { Diagnostics.Log("markers", ex); }
        }

        // ---- project check: by rule ----------------------------------------------------------------

        /// <summary>The list shows one row per rule (a project check, By rule, not reviewing one rule).</summary>
        private bool RuleRows { get { return _files != null && _byRule.Checked && _session.FocusRule == null; } }

        // ---- project check: by file, collapsible ----------------------------------------------------

        /// <summary>A file heading row in the By file view. The issue rows of a collapsed file are left out of the list.</summary>
        private sealed class FileHeading
        {
            public string Path;
            public int Count;
        }

        /// <summary>The list shows a heading row before each file's issues (a project check, By file, not reviewing one rule).</summary>
        private bool FileRows { get { return _files != null && _byFile.Checked && _session.FocusRule == null; } }

        private FileHeading SelectedHeading()
        {
            return _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as FileHeading : null;
        }

        /// <summary>The first issue row, else the first row, else null.</summary>
        private ListViewItem FirstRow()
        {
            foreach (ListViewItem i in _list.Items) if (i.Tag is StyleFinding) return i;
            return _list.Items.Count > 0 ? _list.Items[0] : null;
        }

        private void SetFileCollapsed(string path, bool on)
        {
            _session.SetCollapsed(path, on);
        }

        /// <summary>Selects the row; on a file heading, collapses or expands the file.</summary>
        private void ClickRow(ListViewItem hit)
        {
            if (hit == null) return;
            var heading = hit.Tag as FileHeading;
            if (heading == null) { hit.Selected = true; hit.Focused = true; return; }
            SetFileCollapsed(heading.Path, !_session.IsCollapsed(heading.Path));
            // the rebuild replaced the rows: select the heading again
            foreach (ListViewItem i in _list.Items)
            {
                var h = i.Tag as FileHeading;
                if (h != null && string.Equals(h.Path, heading.Path, StringComparison.OrdinalIgnoreCase)) { i.Selected = true; i.Focused = true; break; }
            }
        }

        /// <summary>In the By file view with nothing selected, selects the first issue row (not a heading).</summary>
        private void SelectFirstIssueRow()
        {
            if (!FileRows || _list.SelectedItems.Count > 0) return;
            var first = FirstRow();
            if (first != null) { first.Selected = true; first.Focused = true; }
        }

        /// <summary>The selected rule row, or null.</summary>
        private BacklogRule CurrentRule()
        {
            return _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as BacklogRule : null;
        }

        /// <summary>One row per rule, most frequent first, keeping the selected rule (or the one just reviewed) selected.</summary>
        private void RebuildRules()
        {
            var cur = CurrentRule();
            var keep = cur != null ? cur.Key : _lastRule;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear(); _list.Groups.Clear();
                ListViewItem selected = null;
                var backlog = _session.Backlog();
                var focus = RuleFocus.Of(backlog);
                if (_focusTop && !focus.Focused) _focusTop = false;   // few rules: nothing to focus on
                _focusLine = _focusTop ? focus.Line : null;
                _showAllRules.Visible = _focusTop;
                foreach (var r in _focusTop ? focus.Shown : backlog)
                {
                    var counts = r.Counts() + (r.Snippets.Count > 0 ? " · in " + Plural.Of(r.Snippets.Count, "snippet") : "");
                    var item = new ListViewItem(new[] { A11y.SeverityGlyph(r.Severity) + " " + Shorten(r.Reason, 160), counts }) { Tag = r };
                    item.ToolTipText = A11y.SeverityLabel(r.Severity) + ". " + A11y.SeverityHelp(r.Severity) + "\n" + r.Title + "\n" + r.Reason + "\n" + counts;
                    item.ForeColor = r.Severity == "error" ? Color.Firebrick : SystemColors.WindowText;
                    _list.Items.Add(item);
                    if (r.Key == keep) selected = item;
                }
                if (selected == null && _list.Items.Count > 0 && keep != null) selected = _list.Items[0];   // the rule is done: on to the next biggest
                if (selected != null) { selected.Selected = true; selected.Focused = true; selected.EnsureVisible(); }
            }
            finally { _list.EndUpdate(); }
            _lastRule = null;
            _list.AccessibleDescription = "Rules, most frequent first, with how many issues and files each has. Enter reviews the selected rule one issue at a time. Delete skips it.";
            _session.Current = null;
            ShowCard();
            var changed = Changed;
            if (changed != null) try { changed(this); } catch (Exception ex) { Diagnostics.Log("markers", ex); }
        }

        /// <summary>Puts back the filter the guided first check set aside (when that check ends).</summary>
        private void RestoreGuidedFilter()
        {
            var saved = _guidedSaved;
            if (saved == null) return;
            _guidedSaved = null;
            _session.Filter = saved;
        }

        /// <summary>A combo changed: the session shows the new choice, and the choice is remembered.</summary>
        private void ApplyFilterChoice()
        {
            if (_syncingFilter) return;
            _guidedSaved = null;   // the writer picked a filter: it stays, and nothing is put back over it
            var level = (ReviewLevel)Math.Max(0, _levelBox.SelectedIndex);
            if (_files == null) _savedSource = (ReviewSource)Math.Max(0, _sourceBox.SelectedIndex);
            _session.Filter = new ReviewFilter(level, _files != null ? ReviewSource.RulesAndAi : _savedSource);
            ReviewViewPrefs.Save(level, _savedSource);
            Diagnostics.Log("filter: " + _levelBox.Text + ", " + _sourceBox.Text);
        }

        private void ClearFilters()
        {
            _guidedSaved = null;
            _syncingFilter = true;
            try
            {
                _levelBox.SelectedIndex = 0;
                _sourceBox.SelectedIndex = 0;
            }
            finally { _syncingFilter = false; }
            if (_files == null) _savedSource = ReviewSource.RulesAndAi;
            _session.Filter = ReviewFilter.All;
            ReviewViewPrefs.Save(ReviewLevel.All, _savedSource);
            _levelBox.Focus();
        }

        /// <summary>The combos and Clear Filters show the session's filter; the combos are locked during a walk (and Source in a project).</summary>
        private void RefreshFilterBar()
        {
            _syncingFilter = true;
            try
            {
                var filter = _session.Filter;
                if ((int)filter.Level != _levelBox.SelectedIndex) _levelBox.SelectedIndex = (int)filter.Level;
                if ((int)filter.Source != _sourceBox.SelectedIndex) _sourceBox.SelectedIndex = (int)filter.Source;
                _clearFilters.Visible = !filter.IsAll;
            }
            finally { _syncingFilter = false; }
            var walking = _session.WalkActive;
            _levelBox.Enabled = !walking;
            _sourceBox.Enabled = !walking && _files == null;
            var tip = walking ? "Finish Review One by One to change the filters." : "";
            _filterTip.SetToolTip(_levelBox, tip);
            _filterTip.SetToolTip(_sourceBox, tip);
        }

        private void SwitchView(bool byFile)
        {
            try
            {
                var settings = FlareAI.Core.Settings.PluginSettings.Load();
                if (settings.ProjectViewByFile != byFile) { settings.ProjectViewByFile = byFile; settings.Save(); }
            }
            catch (Exception ex) { Diagnostics.Log("save view", ex); }
            // A walk is kept across the switch: the strip hides (or shows again at the same place) and All Rules ends it.
            if (_session.FocusRule != null && !_session.IsWalking) _session.EndRuleReview();
            else Rebuild();
            SelectFirstIssueRow();
            _list.Focus();
        }

        private void ReviewRule()
        {
            var r = CurrentRule();
            if (r == null) return;
            _lastRule = r.Key;
            _session.ReviewRule(r);
            _list.Focus();
        }

        /// <summary>Next Rule on the strip: walks the next rule not yet walked, as Review one by one does (or says every rule is done).</summary>
        private void NextRuleInWalk()
        {
            _session.NextRule();
            _lastRule = _session.FocusRule;
            _list.Focus();
        }

        /// <summary>
        /// The keys of a walk, place by place: Enter changes the place and moves on, I ignores it (with the reason last given),
        /// N goes to the next place. On the finished strip Enter is Next Rule, or All Rules when none is left. The list takes
        /// the focus back after each, so the next key never goes to the editor. False for any other key (and outside a walk).
        /// </summary>
        private bool WalkKey(KeyEventArgs e)
        {
            if (!ShowsWalk || e.Control || e.Alt) return false;
            if (_session.WalkFinished)
            {
                if (e.KeyCode != Keys.Enter || e.Shift) return false;
                if (_session.HasNextRule) NextRuleInWalk(); else _session.EndRuleReview();
                _list.Focus();
                return true;
            }
            if (e.KeyCode == Keys.Enter && !e.Shift) Accept();
            else if (e.KeyCode == Keys.N) _session.Next();
            else if (e.KeyCode == Keys.I)
            {
                // The first ignore asks why, in the reasons menu (it takes the focus, and RejectWith gives it back); then the last reason is used.
                if (_lastReason == null) { _rejectMenu.Show(_reject, new Point(0, _reject.Height)); return true; }
                _session.Reject(_lastReason);
            }
            else return false;
            FocusWalkList();
            return true;
        }

        /// <summary>Ignore with a reason: the reason is kept for I in a walk, and the walk goes on from the list.</summary>
        private void RejectWith(string reason)
        {
            _lastReason = reason;
            _session.Reject(reason);
            FocusWalkList();
        }

        /// <summary>In a walk the list takes the focus back after an action: an open or a move to another place may have put it in the editor.</summary>
        private void FocusWalkList() { if (ShowsWalk) _list.Focus(); }

        /// <summary>True while the strip shows the walk: a walk in By Rule (or a topic), not after the view moved to By File.</summary>
        private bool ShowsWalk { get { return _session.IsWalking && (_files == null || _byRule.Checked); } }

        /// <summary>The walked rule's title, reason and severity, from its suggestions (fixed ones too), or null.</summary>
        private BacklogRule WalkedRule()
        {
            var key = _session.FocusRule;
            if (key == null) return null;
            var mine = _session.Findings.Where(f => RuleBacklog.KeyOf(f) == key).ToList();
            return mine.Count == 0 ? null : RuleBacklog.Group(mine, _runner.RuleFor, _doc.SourcePath, _runner.SourceName)[0];
        }

        /// <summary>Shows the walk in the strip above the list, with the counter, the change and the finished state; hides it otherwise.</summary>
        private void UpdateWalkStrip()
        {
            if (_walkStrip == null) return;
            var rule = ShowsWalk ? WalkedRule() : null;
            _walkStrip.Visible = rule != null;
            if (rule == null) return;
            if (_session.WalkFinished)
            {
                var more = _session.HasNextRule;
                _walkStrip.ShowFinished(_session.WalkFinishedText + (more ? "" : "  You've been through every rule."), more);
                return;
            }
            var current = Current();
            var example = current == null || string.IsNullOrEmpty(current.Excerpt) ? "" : current.Excerpt + "\n" + (current.Replacement ?? "");
            _walkStrip.ShowWalk(rule.Title, rule.Severity, rule.Reason, example, _session.WalkPosition, _session.WalkTotal, _session.WalkChangeAllCount, null);
        }

        /// <summary>The card for a rule row: why the rule exists, where it fires, real examples, and what Fix all would do.</summary>
        private void ShowRuleCard(BacklogRule r)
        {
            _cardHead.Text = A11y.SeverityLabel(r.Severity) + "  ·  " + r.Title + "\n" + r.Counts();
            var shown = 0;
            foreach (var f in r.Findings)
            {
                if (shown == 3) break;
                if (shown++ > 0) _cardDiff.AppendText("\n");
                DiffView.Segments(_cardDiff, new List<DiffSegment> { new DiffSegment { Kind = DiffKind.Same, Text = BatchPlan.ProjectPath(f.DocumentPath ?? "") + ":  …" + Tail(f.Before, 30) } }, true);
                if (f.Replacement != null) DiffView.Segments(_cardDiff, ChangeText.Diff(f.Excerpt, f.Replacement), false);
                else DiffView.Segments(_cardDiff, new List<DiffSegment> { new DiffSegment { Kind = DiffKind.Same, Text = f.Excerpt } }, false);
            }
            Line(_cardWhy, r.Reason, FontStyle.Regular);
            var auto = r.Automatic.Count;
            Line(_cardWhy, auto == r.Occurrences ? "Every issue can be changed automatically. Change All shows the files first."
                         : auto > 0 ? auto + " of " + r.Occurrences + " can be changed automatically. Review the rest one by one."
                         : "None can be changed automatically. Review One by One takes you to each issue.", FontStyle.Italic);
            if (r.Snippets.Count > 0)
            {
                var uses = new List<string>();
                foreach (var p in r.Snippets) { int n; uses.Add(System.IO.Path.GetFileName(p) + (_session.KnownSnippetUses(p, out n) ? " (used in " + UsesLabel(n) + ")" : "")); }
                Line(_cardWhy, "Also in snippets " + string.Join(", ", uses.ToArray()) + ". A change in a snippet changes every topic that uses it.", FontStyle.Bold);
            }
            const int ShowFiles = 6;
            var files = new List<string>();
            for (var i = 0; i < r.Files.Count && i < ShowFiles; i++) files.Add(BatchPlan.ProjectPath(r.Files[i]));
            Line(_cardWhy, "Files: " + string.Join(", ", files.ToArray()) + (r.Files.Count > ShowFiles ? " and " + (r.Files.Count - ShowFiles) + " more." : "."), FontStyle.Regular);
            _cardDiff.Select(0, 0); _cardWhy.Select(0, 0);
            _list.AccessibleDescription = (r.Title + ". " + r.Counts() + ". " + r.Reason).Replace("\n", " ");
        }

        private static string Tail(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(s.Length - n); }

        /// <summary>
        /// Runs a batch from the pane: a few files at once; more file by file between messages, with progress and
        /// Stop after this file. The list and actions wait until it finishes.
        /// </summary>
        private void RunBatchInPane(BatchRun run)
        {
            if (run.FileCount <= 3) { run.RunToEnd(); return; }
            _batch = run;
            _progress.Style = ProgressBarStyle.Continuous; _progress.Minimum = 0; _progress.Maximum = run.FileCount; _progress.Value = 0; _progress.Visible = true;
            _batchStop.Visible = true; _batchStop.Enabled = true;
            _list.Enabled = false;
            UpdateButtons();
            _status.Text = "Changing " + Plural.Of(run.FileCount, "file") + "…";
            run.Finished += r => EndBatch(run);
            var timer = new Timer { Interval = 1 };   // WM_TIMER is low priority: clicks and painting get in between files
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var more = false;
                try
                {
                    if (IsDisposed) run.Cancel();
                    more = run.Step();
                    if (more && !IsDisposed)
                    {
                        _progress.Value = Math.Min(run.FileCount, run.FilesDone);
                        if (!run.IsCancelled) _status.Text = "Changing files. " + run.FilesDone + " of " + run.FileCount + " done. Next is " + BatchPlan.ProjectPath(run.NextFile ?? "") + ".";
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("Change files", ex);
                    run.Cancel();
                    try { more = run.Step(); } catch (Exception ex2) { Diagnostics.Log("Change files: finish", ex2); more = false; }
                    EndBatch(run);
                }
                if (more) timer.Start(); else timer.Dispose();
            };
            timer.Start();
        }

        /// <summary>The batch is over (finished, stopped or failed): the list and actions are back.</summary>
        private void EndBatch(BatchRun run)
        {
            if (_batch != run) return;
            _batch = null;
            if (IsDisposed) return;
            _progress.Visible = false; _progress.Style = ProgressBarStyle.Marquee;
            _batchStop.Visible = false;
            _list.Enabled = true;
            UpdateButtons();
            _list.Focus();
        }

        /// <summary>Raised whenever the list changes (new findings, apply, reject, check again): the editor markers follow it.</summary>
        public static event Action<FindingsPanel> Changed;

        /// <summary>The topic this panel shows.</summary>
        public IDocumentAdapter Doc { get { return _doc; } }

        /// <summary>Open proposals in the topic itself (not in snippets), for the editor markers.</summary>
        public List<StyleFinding> OpenTopicFindings() { return _session.OpenTopicFindings(); }

        /// <summary>
        /// Live check: replaces the topic's style-rule and structure findings with fresh ones (the author typed); AI findings
        /// stay, and any whose text changed show as stale. Skipped while a review is running.
        /// </summary>
        public void RefreshLint(List<StyleFinding> lint)
        {
            if (_reviewing) return;
            _session.RefreshLint(lint);   // not in a project report
        }

        /// <summary>A moment after typing stops: undone changes come back, issues the writer fixed by hand show as fixed, then Live check's lint (or null).</summary>
        public void AfterEdit(List<StyleFinding> lint) { if (_reviewing) return; _session.AfterEdit(lint); }
        /// <summary>A file in this report was saved: issues the writer fixed in it show as fixed.</summary>
        public void RecheckSaved(string path, System.Xml.XmlDocument dom) { if (_reviewing) return; _session.RecheckSaved(path, dom); }
        /// <summary>True when the report has issues in this file.</summary>
        public bool Covers(string path) { return _session.Covers(path); }
        /// <summary>Starts or stops telling House Style's own writes from the writer's (while the pane is shown).</summary>
        internal void WatchWrites(bool on) { _session.WatchWrites(on); }

        private ListViewGroup GroupFor(StyleFinding f)
        {
            string key, label;
            if (_files != null) { key = f.DocumentPath ?? ""; label = RelativeToContent(key); }
            else { key = f.Section ?? ""; label = key.Length == 0 ? "(top of topic)" : key; }
            if (f.DocumentPath != null && _files == null)
            {
                int uses;
                label += _session.KnownSnippetUses(f.DocumentPath, out uses) && uses > 1 ? " · used in " + uses + " files" : "";
            }
            return _list.Groups[key] ?? _list.Groups.Add(key, label);
        }

        private static string ProposalText(StyleFinding f)
        {
            var mark = f.IsFixed ? "✓ " : f.IsStale ? "⚠ " : "";
            return mark + ProposalBody(f);
        }

        private static string ProposalBody(StyleFinding f)
        {
            switch (f.Kind)
            {
                case "variable": return "Use variable " + f.Variable + " for \"" + f.Excerpt + "\"";
                case "restructure": return "Rewrite \"" + Shorten(f.Excerpt, 48) + "\"";
                case "manual": return FlareAI.Core.Style.RuleWording.FromName(f.Rule) + " in \"" + Shorten(f.Excerpt, 36) + "\"";
                default: return ChangeText.Row(f.Excerpt, f.Replacement);
            }
        }

        /// <summary>An AI issue (no guide rule behind it, or one the model judged); those stay out of the export (#238).</summary>
        private static bool IsAi(StyleFinding f) { return f.RuleLayer != null || string.IsNullOrEmpty(f.Layer) || f.Layer == "ai"; }

        private static string Shorten(string s, int n) { return ReviewSession.Shorten(s, n); }

        private string RelativeToContent(string path)
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            while (dir != null && !System.IO.Path.GetFileName(dir).Equals("Content", StringComparison.OrdinalIgnoreCase)) dir = System.IO.Path.GetDirectoryName(dir);
            return dir == null ? System.IO.Path.GetFileName(path) : path.Substring(dir.Length + 1);
        }

        /// <summary>The rule by what it says (tooltips), not by its internal name.</summary>
        private string ReadableRule(StyleFinding f)
        {
            var rule = f.RuleId == null ? null : _runner.RuleFor(f.RuleId);
            return rule != null ? ReviewSession.Shorten(rule.Display, 80) : FlareAI.Core.Style.RuleWording.FromName(f.Rule);
        }

        private string LayerLabel(StyleFinding f)
        {
            return (f.RequestSent ? "Request sent · " : "") + RuleSource(f);
        }

        private string RuleSource(StyleFinding f)
        {
            if (f.RuleLayer != null) return "AI · " + _runner.SourceName(f) + ": " + f.RuleSection;
            if (string.IsNullOrEmpty(f.Layer) || f.Layer == "ai") return "AI";
            if (!string.IsNullOrEmpty(f.RuleSection)) return _runner.SourceName(f) + " · " + f.RuleSection;
            return _runner.SourceName(f) + (string.IsNullOrEmpty(f.Rule) ? "" : " · " + f.Rule.Substring(f.Rule.IndexOf('.') + 1));
        }

        private List<StyleFinding> Open() { return _session.Open(); }

        private string Summary() { return _session.Summary(); }

        /// <summary>The non-AI findings as export rows, in input order (#238). The ✓ and ⚠ marks stay in the pane only.</summary>
        internal List<FlareAI.Core.Output.IssueExport.Row> RowsFrom(IEnumerable<StyleFinding> findings)
        {
            var rows = new List<FlareAI.Core.Output.IssueExport.Row>();
            foreach (var f in findings)
            {
                if (IsAi(f)) continue;
                rows.Add(new FlareAI.Core.Output.IssueExport.Row
                {
                    Topic = f.DocumentPath == null ? "" : RelativeToContent(f.DocumentPath),
                    Level = A11y.SeverityWord(f.Severity),
                    Issue = ProposalBody(f),
                    Rule = ReadableRule(f),
                    Source = LayerLabel(f),
                    Text = f.Excerpt,
                    SuggestedText = f.Replacement,
                    Status = FlareAI.Core.Output.IssueExport.StatusOf(f.IsFixed, f.IsStale, _session.IsSkipped(f)),
                    Path = f.DocumentPath
                });
            }
            return rows;
        }

        /// <summary>Export Issues…: every non-AI issue in this report to a CSV file (#238). A scenario's saveAs path skips the dialog.</summary>
        private void ExportIssues()
        {
            var rows = RowsFrom(new List<StyleFinding>(_session.Findings));   // a snapshot before any dialog
            if (rows.Count == 0) { _status.Text = FlareAI.Core.Output.IssueExport.NothingToExport; return; }
            var path = E2EHook.TakeSavePath() ?? AskExportPath();
            if (path == null) return;
            Exception error;
            if (!ExportTo(path, rows, out error)) Notify.Failed(FindForm(), "export the issues to " + System.IO.Path.GetFileName(path), error, "Export Issues");
        }

        private string AskExportPath()
        {
            using (var d = new SaveFileDialog { Title = "Export Issues", Filter = "CSV files (*.csv)|*.csv", DefaultExt = "csv", AddExtension = true,
                                                OverwritePrompt = true, RestoreDirectory = true,
                                                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                                FileName = FlareAI.Core.Output.IssueExport.SuggestedName(_projectName, DateTime.Today) })
                return d.ShowDialog(FindForm()) == DialogResult.OK ? d.FileName : null;
        }

        internal bool ExportTo(string path, IList<FlareAI.Core.Output.IssueExport.Row> rows, out Exception error)
        {
            error = null;
            var temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                System.IO.File.WriteAllText(temp, FlareAI.Core.Output.IssueExport.ToCsv(rows), new System.Text.UTF8Encoding(true));
                if (System.IO.File.Exists(path)) System.IO.File.Replace(temp, path, null); else System.IO.File.Move(temp, path);
                _status.Text = FlareAI.Core.Output.IssueExport.Saved(rows.Count, path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch (Exception) { }
                Diagnostics.Log("Export issues", ex);
                _status.Text = "House Style couldn't export the issues to " + System.IO.Path.GetFileName(path) + ".";
                return false;
            }
        }

        // ---- the card ----------------------------------------------------------------------------

        private StyleFinding Current()
        {
            return _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as StyleFinding : null;
        }

        private void ShowCard()
        {
            UpdateWalkStrip();   // the strip's counter and finished state follow the selection
            var f = Current();
            _cardDiff.Clear(); _cardWhy.Clear(); _cardWhy.Tag = null;
            var ruleRow = CurrentRule();
            var rows = RuleRows;
            _actions.Visible = !rows;
            _ruleActions.Visible = rows && ruleRow != null;
            if (ruleRow != null) { ShowRuleCard(ruleRow); UpdateButtons(); return; }
            if (f == null)
            {
                if (FileRows) _list.AccessibleDescription = ListHelp + HeadingHelp;
                _cardHead.Text = _session.Findings.Count == 0 ? ""
                               : rows ? (_list.Items.Count == 0 ? "" : "Select a rule to see why it matters and where it appears.")
                               : "Select an issue to see what changes and why.";
                UpdateButtons();
                return;
            }
            var where = f.DocumentPath == null ? ""
                : f.DocumentPath.EndsWith(".flsnp", StringComparison.OrdinalIgnoreCase)
                    ? "In snippet " + System.IO.Path.GetFileName(f.DocumentPath) + ", used in " + UsesLabel(_session.SnippetUses(f.DocumentPath)) + ". A change here changes all of them."
                    : "In " + System.IO.Path.GetFileName(f.DocumentPath);
            _cardHead.Text = A11y.SeverityLabel(f.Severity) + "  ·  " + LayerLabel(f) + "  ·  " + KindLabel(f) + (f.IsFixed ? (f.FixedByEdit || f.MarkedFixed ? "  ·  fixed" : "  ·  changed") : f.IsStale ? "  ·  text changed" : "")
                             + (where.Length > 0 ? "\n" + where : "");

            _list.AccessibleDescription = (_cardHead.Text + ". " + f.Suggestion).Replace("\n", " ") + (FileRows ? HeadingHelp : "");   // what a screen reader says for the selected row

            // What: the words around it, and the change marked.
            if (!string.IsNullOrEmpty(f.Before)) DiffView.Segments(_cardDiff, new List<DiffSegment> { new DiffSegment { Kind = DiffKind.Same, Text = "…" + f.Before } }, true);
            if (f.Replacement != null) DiffView.Segments(_cardDiff, ChangeText.Diff(f.Excerpt, f.Replacement), false);
            else
            {
                _cardDiff.SelectionStart = _cardDiff.TextLength;
                _cardDiff.SelectionBackColor = Color.FromArgb(255, 242, 204);
                _cardDiff.SelectionFont = new Font(_cardDiff.Font, FontStyle.Bold);
                _cardDiff.AppendText(f.Excerpt);
            }

            // Why: the reason, the rule as written in the guide, and its example.
            Line(_cardWhy, f.Suggestion, FontStyle.Regular);
            var rule = _runner.RuleFor(f.RuleId);
            // The rule by what it says and where it lives; the id is only a reference for owners (Validate, Tune).
            if (rule != null) Line(_cardWhy, _runner.SourceName(rule.Layer, rule.Id) + " · " + rule.Section + ": " + rule.Display, FontStyle.Italic);
            var ex = _runner.ExampleFor(f.RuleId);
            if (ex != null)
            {
                if (!string.IsNullOrEmpty(ex.Bad)) Line(_cardWhy, "✗ " + ex.Bad, FontStyle.Regular);
                if (!string.IsNullOrEmpty(ex.Good)) Line(_cardWhy, "✓ " + ex.Good, FontStyle.Regular);
            }
            if (rule == null && f.Source == "ai") Line(_cardWhy, "General editing judgment (not tied to a guide rule).", FontStyle.Italic);
            _cardDiff.Select(0, 0); _cardWhy.Select(0, 0);
            UpdateButtons();
        }

        private static string UsesLabel(int n) { return n + (n == 1 ? " topic" : " topics"); }

        /// <summary>The most text the why box shows: past it, the text ends with "and more" (a team-guide row can be huge).</summary>
        private const int WhyMaxChars = 4000;
        private const string MoreTag = "and more";

        private static void Line(RichTextBox box, string text, FontStyle style)
        {
            if (string.IsNullOrEmpty(text) || box.Tag as string == MoreTag) return;
            var room = WhyMaxChars - box.TextLength - (box.TextLength > 0 ? 1 : 0);   // the line break counts too
            if (text.Length > room)
            {
                var cut = Math.Max(0, room);
                var space = cut > 0 ? text.LastIndexOf(' ', cut - 1) : -1;
                var kept = (space > 0 ? text.Substring(0, space) : text.Substring(0, cut)).TrimEnd();
                text = kept.Length > 0 ? kept + " and more" : "and more";
                box.Tag = MoreTag;
            }
            box.SelectionStart = box.TextLength;
            box.SelectionFont = new Font(box.Font, style);
            box.SelectionColor = SystemColors.ControlText;
            box.AppendText((box.TextLength > 0 ? "\n" : "") + text);
        }


        private static string KindLabel(StyleFinding f)
        {
            switch (f.Kind)
            {
                case "mechanical": return "Auto-fix";
                case "wording": return "Needs you";
                case "variable": return "Needs you";
                case "manual": return "Needs you";
                default: return "Needs you";
            }
        }

        // ---- decisions ---------------------------------------------------------------------------

        private void Accept()
        {
            var f = Current();
            var outcome = _session.Accept();
            if ((outcome == AcceptOutcome.Rewrite || outcome == AcceptOutcome.AltText) && _rulesOnly)
            {
                _status.Text = LintChecksChoice.ChangeNeedsAi;   // nothing goes to the model
                return;
            }
            switch (outcome)
            {
                case AcceptOutcome.Rewrite: RewriteParagraph(f); break;
                case AcceptOutcome.AltText: SuggestAltText(f); break;
            }
        }

        /// <summary>The AI looks at the image and drafts alt text; the author edits it; it is applied as a tracked change.</summary>
        private void SuggestAltText(StyleFinding f)
        {
            var block = _runner.LocateBlock(_doc, f) ?? f.Block;
            var img = AltText.MissingIn(block);
            if (img == null) { _status.Text = "That image already has alt text, or it's gone. Lint the paragraph again."; return; }
            var inside = false;
            for (XmlNode n = img; n != null && !inside; n = n.ParentNode) inside = n == block;
            if (!inside) block = img.ParentNode; // the finding sits on the text next to an image-only paragraph
            LlmImageFile image;
            try { image = AltText.Load(_doc.SourcePath, img.GetAttribute("src")); }
            catch (Exception ex) { _status.Text = ex.Message + " Write the alt text in the image's properties."; return; }
            var src = img.GetAttribute("src");
            var context = AltText.Context(block);
            var runner = _newRunner() ?? _runner;
            Busy(true, "Looking at " + System.IO.Path.GetFileName(image.Path) + "…");
            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) => e.Result = runner.SuggestAlt(image, context);
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (IsDisposed) return;
                Busy(false, null);
                if (e.Error != null) { Diagnostics.Log("Alt text", e.Error); ErrorReporter.Background(this, "House Style couldn't suggest alt text.", e.Error); _status.Text = "Alt text failed. " + e.Error.Message; return; }
                var suggestion = (string)e.Result;
                var alt = TextPrompt.Ask(this, "Alt text", suggestion == null
                    ? "The AI thinks " + System.IO.Path.GetFileName(image.Path) + " is decorative. If it isn't, type alt text for screen readers."
                    : "Alt text for " + System.IO.Path.GetFileName(image.Path) + ". Edit it before you add it.", suggestion ?? "", false);
                if (string.IsNullOrEmpty(alt)) return;
                try
                {
                    AltText.Apply(_doc, block, src, alt.Trim());
                    _session.AltTextApplied(f, suggestion, alt.Trim());
                }
                catch (Exception ex) { Diagnostics.Log("Alt text apply", ex); _status.Text = "Couldn't add the alt text. " + ex.Message; }
            };
            worker.RunWorkerAsync();
        }

        /// <summary>Keeps the per-file outcome in the pane when it has more to say than the status line.</summary>
        private void ShowResult(BatchResult result)
        {
            var many = result.Changed.Count + result.Skipped.Count + result.Failed.Count > 1;
            if (!many && result.Skipped.Count == 0 && result.Failed.Count == 0) return;
            var text = result.Report().Replace("\r\n", "\n").Replace("\n", "\r\n");
            _result.Text = text;
            _result.Height = Math.Min(140, 18 * (text.Split('\n').Length + 1));
            _result.Visible = true;
        }

        private void Refine()
        {
            if (_rulesOnly) return;
            var f = Current();
            if (f == null || f.IsFixed || f.IsStale) return;
            var instruction = TextPrompt.Ask(this, "Rewrite", "What should the AI change in this suggestion? For example, keep the term SSO, or make it shorter.", "", true);
            if (string.IsNullOrEmpty(instruction)) return;
            var block = f.DocumentPath == null ? _runner.LocateBlock(_doc, f) : null;
            var context = block != null ? block.InnerText : (f.Before ?? "") + f.Excerpt;
            var runner = _newRunner() ?? _runner;
            Busy(true, "Rewriting…");
            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) => e.Result = runner.Refine(f, context, instruction);
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (IsDisposed) return;
                Busy(false, null);
                if (e.Error != null) { Diagnostics.Log("Refine", e.Error); ErrorReporter.Background(this, "House Style couldn't rewrite the text.", e.Error); _status.Text = "Rewrite failed. " + e.Error.Message; return; }
                _session.Refined(f, (StyleFinding)e.Result, instruction);
                AiRewriteDone();
            };
            worker.RunWorkerAsync();
        }

        /// <summary>Checklist and first-time note after an AI rewrite in the pane.</summary>
        private void AiRewriteDone()
        {
            GettingStarted.Mark(ChecklistItem.AiRewrite);
            var form = FindForm();
            if (form != null) GettingStarted.Note(form, FirstTimeNote.FirstAiRewrite);
            _cost.Text = AiCost.Session();
        }

        /// <summary>Lints the selected issue's paragraph again (rules and AI) and replaces its issues.</summary>
        private void Reanalyze()
        {
            if (_rulesOnly) return;
            var f = Current();
            var block = _session.ReanalyzeTarget(f);
            if (block == null) return;
            var blocks = new List<XmlNode> { block };
            Busy(true, "Linting the paragraph again…");
            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                var lint = _runner.Lint(blocks);
                var units = _runner.PrepareReview(blocks, lint, 1);
                var raw = units.Count == 0 ? new List<StyleFinding>() : _runner.CallReview(units[0]);
                e.Result = new object[] { lint, raw, units.Count == 0 ? null : units[0] };
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (IsDisposed) return;
                Busy(false, null);
                if (e.Error != null) { Diagnostics.Log("Re-analyze", e.Error); ErrorReporter.Background(this, "House Style couldn't lint the paragraph again.", e.Error); _status.Text = "Couldn't lint the paragraph again. " + e.Error.Message; return; }
                var parts = (object[])e.Result;
                var lint = (List<StyleFinding>)parts[0];
                var fresh = new List<StyleFinding>(lint);
                if (parts[2] != null) fresh.AddRange(_runner.AnchorReview((List<StyleFinding>)parts[1], (ReviewUnit)parts[2], lint));
                _session.Reanalyzed(f, block, fresh);
            };
            worker.RunWorkerAsync();
        }

        private void RewriteParagraph(StyleFinding f)
        {
            List<StyleFinding> related;
            var block = _session.RewriteTarget(f, out related);
            if (block == null) return;
            var instruction = "Resolve these review issues and change nothing else:\n" + ActionRunner.Summarize(related);
            string applied;
            using (var form = new ResultForm("Rewrite Paragraph", _newRunner(), _doc, ActionKind.Ask, instruction, new List<XmlNode> { block }))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                applied = form.AppliedSummary;
            }
            _session.Rewritten(related, applied);
            AiRewriteDone();
        }

        // ---- navigation --------------------------------------------------------------------------

        /// <summary>The session moved the selection: select that row (null clears the card).</summary>
        private void Select(StyleFinding f)
        {
            if (RuleRows) return;   // rule rows: the rule stays selected (Rebuild picks the next rule when one is done)
            var items = new ListViewItem[_list.Items.Count];
            _list.Items.CopyTo(items, 0);
            foreach (var item in items)
            {
                if (item.ListView == null) continue;   // the list was rebuilt meanwhile
                var on = item.Tag == f;
                if (item.Selected != on) item.Selected = on;
                if (on) { item.Focused = true; item.EnsureVisible(); }
            }
            ShowCard();   // now; the deferred selection handler only tells the session (and moves the editor) if the row changed
        }

        // ---- selection ---------------------------------------------------------------------------

        private bool _selectionQueued;
        private object _lastSelected;   // the finding or rule the session was last told about

        /// <summary>
        /// The ListView raises SelectedIndexChanged from inside ListViewItem.Selected. Telling the session there can rebuild
        /// the list (an undone fix revived, the editor's document event after Go to) while the ListView is still selecting
        /// a row, which ends in a NullReferenceException in ListViewItem.set_Selected and a list frozen in BeginUpdate.
        /// So the reaction runs once, after the selection has settled.
        /// </summary>
        private void QueueSelectionChanged()
        {
            if (_selectionQueued || IsDisposed) return;
            if (!IsHandleCreated) { Safe.Run("Select issue", OnSelectionChanged); return; }
            _selectionQueued = true;
            BeginInvoke((Action)(() => { _selectionQueued = false; if (!IsDisposed) Safe.Run("Select issue", OnSelectionChanged); }));
        }

        private void OnSelectionChanged()
        {
            var f = Current();
            _session.Current = f;
            ShowCard();
            var selected = (object)f ?? CurrentRule();
            if (ReferenceEquals(selected, _lastSelected)) return;   // the same row again (a rebuild kept it): nothing new to say
            _lastSelected = selected;
            _session.OnSelected(f);
        }

        private void Busy(bool on, string text)
        {
            _reviewing = on; _progress.Visible = on;
            if (on) { _progress.Style = ProgressBarStyle.Marquee; _status.Text = text; }
            _cost.Text = AiCost.Session();
            UpdateButtons();
        }

        private void OpenLink(string url)
        {
            // Rules written in a Flare style-guide topic link to that topic: open it in Flare, next to the work.
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var path = new Uri(url).LocalPath;
                    if (_open != null && (path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) && _open(path) != null) return;
                }
                catch (Exception ex) { Diagnostics.Log("guide link", ex); }
                _status.Text = "Could not open " + url;
                return;
            }
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return; // otherwise only web guide pages
            try { System.Diagnostics.Process.Start(url); } catch (Exception ex) { _status.Text = "Could not open " + url + ": " + ex.Message; }
        }

        private static void Restyle(ListViewItem item)
        {
            var f = (StyleFinding)item.Tag;
            var done = f.IsFixed || f.IsStale;
            item.ForeColor = done ? SystemColors.GrayText : (f.Severity == "error" ? Color.Firebrick : SystemColors.WindowText);
            item.Font = f.IsFixed ? new Font(SystemFonts.MessageBoxFont, FontStyle.Strikeout) : f.IsStale ? new Font(SystemFonts.MessageBoxFont, FontStyle.Italic) : SystemFonts.MessageBoxFont;
        }

        private void UpdateButtons()
        {
            var f = Current();
            var open = f != null && !f.IsFixed && !f.IsStale;
            _accept.Enabled = open;
            _goTo.Enabled = f != null && !f.IsFixed;
            _openFile.Enabled = _session.FilePathOf(f) != null;
            _accept.Text = f == null ? "Change" : f.Rule == "Structure.AltText" && f.DocumentPath == null ? "Add Alt Text…" : f.Kind == "restructure" ? "Change…" : f.Kind == "variable" || f.Kind == "manual" ? "Go To" : "Change";
            _accept.AccessibleName = _accept.Text.TrimEnd('…');
            var sameRule = open && _files == null ? _session.ChangeAllCandidates(f).Count : 0;
            _changeAll.Visible = sameRule > 1;   // only when there is more than one of this issue (a narrow pane has no room for a dead button)
            _changeAll.Text = sameRule > 1 ? "Change All (" + sameRule + ")" : "Change All";
            _reject.Enabled = f != null && !f.IsFixed;
            _edit.Enabled = open && f.Kind != "variable" && f.Kind != "manual";
            _refine.Enabled = open && !_reviewing && !_rulesOnly;
            _reanalyze.Enabled = f != null && f.DocumentPath == null && !_reviewing && _files == null && !_rulesOnly;
            _rule.Enabled = f != null && !string.IsNullOrEmpty(f.Link);
            var busy = _batch != null;
            if (_files != null)
            {
                var auto = _session.AutomaticCandidates().Count;
                _acceptAll.Text = "Fix All (" + auto + ")…";
                _acceptAll.Enabled = auto > 0 && !busy;
                _bulk.Visible = false;
            }
            else
            {
                var mech = _session.FixAllCandidates().Count;
                _acceptAll.Text = "Fix All (" + mech + ")";
                _acceptAll.Enabled = mech > 0;
            }
            var exact = _session.WriteAllCandidates().Count;
            _bulk.Text = "Change All Issues (" + exact + ")";
            _bulk.Enabled = exact > 0 && _files == null;
            _recheck.Enabled = !_reviewing && !busy;
            var r = CurrentRule();
            _ignore.Enabled = (f != null || r != null) && !busy;
            _viewBar.Enabled = !busy;
            RefreshFilterBar();
            _tabs.Enabled = !busy;
            _actions.Enabled = !busy;
            _ruleActions.Enabled = !busy;
            if (r != null)
            {
                var n = r.Automatic.Count;
                _fixRule.Text = n == 0 ? "Change All…" : "Change All (" + n + ")…";
                _fixRule.Enabled = n > 0;
                _fixRule.AccessibleName = n == 0 ? "Change All" : "Change All " + n;
            }
            var request = Team.Current != null && Team.Current.ChangesNeedRequest;
            _turnOffRule.Text = request ? "Request Rule Change…" : "Disable Rule…";
            _turnOffRule.AccessibleName = _turnOffRule.Text.TrimEnd('…');
            _ignore.Text = _session.RuleChangeLabel();
            _ignore.AccessibleName = _ignore.Text.TrimEnd('…');
            var ruleTip = request ? "Ask the style guide owners to change this rule." : "Stop linting for this rule.";
            _ignoreTip.SetToolTip(_ignore, ruleTip);
            _ignoreTip.SetToolTip(_turnOffRule, ruleTip);
            LayoutCard();   // the selection, the card's text or a button's label changed
        }

        // ---- IReviewHost: dialogs and documents for the session ---------------------------------

        bool IReviewHost.Confirm(string message, string action) { return Notify.Confirm(this, message, action); }

        RuleSettingChoice IReviewHost.ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<FlareAI.Core.Style.RuleRef> otherRules)
        {
            using (var form = new RuleSettingForm(ruleText, canChangeTeamGuide, personalGuide, otherRules))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return null;
                return new RuleSettingChoice { ForTeam = form.ForTeam, Setting = form.Setting, Reason = form.Reason, AlsoOthers = form.AlsoOthers, AlsoCheckers = form.AlsoCheckers };
            }
        }

        RuleSettingChoice IReviewHost.ChooseRequest(RequestQuestion question) { return RequestForm.Ask(this, question); }

        string IReviewHost.AskText(string title, string label, string initial, bool multiline) { return TextPrompt.Ask(this, title, label, initial, multiline); }

        string IRequiredTextHost.AskRequiredText(string title, string label, string initial, bool multiline) { return TextPrompt.Ask(this, title, label, initial, multiline, required: true); }

        IDocumentAdapter IReviewHost.OpenDocument(string path) { return _open == null ? null : _open(path); }

        bool IReviewHost.PreviewBatch(BatchPlan plan)
        {
            using (var form = new BatchPreviewForm(plan))
                return form.ShowDialog(this) == DialogResult.OK;
        }

        bool IReviewHost.SaveAndClose(IDocumentAdapter doc)
        {
            var fa = doc as FlareAI.Flare.FlareDocumentAdapter;
            if (fa == null || !fa.OpenedByPlugin) return false;
            fa.SaveAndClose();
            return true;
        }

        void IReviewHost.ShowError(string whatHappened, string whatToDo, Exception details, string action) { Notify.Error(this, whatHappened, whatToDo, details, action); }
    }

    /// <summary>A tab button. A plain radio button clicks itself when it gets the focus, which switched tabs whenever a panel was shown.</summary>
    internal sealed class TabRadio : RadioButton
    {
        protected override void OnEnter(EventArgs e) { }
    }

    /// <summary>A one-field text prompt (Edit / Refine, Exempt). Required: OK stays off until the box has text.</summary>
    internal static class TextPrompt
    {
        public static string Ask(IWin32Window owner, string title, string label, string initial, bool multiline, bool required = false)
        {
            using (var form = Build(title, label, initial, multiline, required))
                return form.ShowDialog(owner) == DialogResult.OK ? form.Controls.OfType<TextBox>().Single().Text : null;
        }

        /// <summary>A plain Form would be invisible to the end-to-end hook, which lists only this plugin's own forms.</summary>
        private sealed class PromptForm : Form { }

        /// <summary>The prompt, not shown. The one text box is the only TextBox on it.</summary>
        internal static Form Build(string title, string label, string initial, bool multiline, bool required)
        {
            var form = new PromptForm { Text = Notify.Caption(title), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(520, multiline ? 190 : 150), Font = SystemFonts.MessageBoxFont, AutoScaleMode = AutoScaleMode.Font };
            var text = new Label { Text = label, Left = 12, Top = 12, Width = 496, Height = 34 };
            var box = new TextBox { Left = 12, Top = 50, Width = 496, Height = multiline ? 84 : 24, Multiline = multiline, AcceptsReturn = false, Text = initial ?? "", ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None };
            var ok = new Button { Text = "OK", Left = 352, Top = form.ClientSize.Height - 36, Width = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 433, Top = form.ClientSize.Height - 36, Width = 75, DialogResult = DialogResult.Cancel };
            if (required)
            {
                ok.Enabled = !string.IsNullOrWhiteSpace(box.Text);
                box.TextChanged += (s, e) => ok.Enabled = !string.IsNullOrWhiteSpace(box.Text);
            }
            form.Controls.AddRange(new Control[] { text, box, ok, cancel });
            A11y.Name(box, label);
            A11y.Apply(form);
            form.AcceptButton = ok; form.CancelButton = cancel;
            return form;
        }
    }
}
