using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Style rules": every rule of the style guide in one place (Google, Company, the team's guide, the writer's own, the
    /// open project), grouped by source and page, with search and filters. The detail pane shows the rule as written, how it
    /// is enforced, its examples, where it applies, and its state with who, when and why. Turn off, Change level, Turn back
    /// on and Use the default go through <see cref="RuleChanges"/>, the same paths as the review pane: owners and a
    /// writer's own guide change directly; on a team guide that is read-only for this writer, each becomes a request.
    /// </summary>
    internal sealed class StyleRulesForm : Form
    {
        private static string ActionName { get { return Commands.Rules.Label; } }
        private readonly Team _team;
        private readonly StyleEngine _style;
        private readonly string _project, _openProject;
        private readonly Func<string, bool> _isDefault;
        private readonly LiveLabel _head;
        private readonly TextBox _search, _detail;
        private readonly ComboBox _source, _state, _fix, _content;
        private readonly ListView _list;
        private readonly Button _edit, _delete, _off, _level, _restore, _clear;
        private readonly Timer _typing = new Timer { Interval = 250 };
        private RuleCatalog _catalog;
        private bool _filling;

        private sealed class Choice
        {
            public string Text; public object Value;
            public override string ToString() { return Text; }
        }

        /// <param name="project">The path the guide is composed for (the open topic when the project's own rules apply), or null.</param>
        /// <param name="openProject">A topic of the open Flare project, for project exceptions; null when none is open.</param>
        /// <param name="isDefault">True for the read-only guide shipped with the plugin: its changes are labelled Default.</param>
        public StyleRulesForm(Team team, StyleEngine style, string project, string openProject, Func<string, bool> isDefault)
        {
            _team = team; _style = style; _project = project; _openProject = openProject; _isDefault = isDefault;
            Text = Notify.Caption(Commands.Rules.Label);
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1000, 640);
            MinimumSize = new Size(640, 420);
            KeyPreview = true;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _head = new LiveLabel { AutoSize = true, Text = "Reading the style guide…", Margin = new Padding(0, 0, 0, 6) };

            var filters = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            _search = new TextBox { Width = 220, Margin = new Padding(0, 3, 12, 3) };
            _source = Combo(200);
            _state = Combo(130);
            _fix = Combo(170);
            _content = Combo(150);
            _clear = new Button { Text = "Clear &filters", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            filters.Controls.Add(Caption("&Search:")); filters.Controls.Add(_search);
            filters.Controls.Add(Caption("S&ource:")); filters.Controls.Add(_source);
            filters.Controls.Add(Caption("St&ate:")); filters.Controls.Add(_state);
            filters.Controls.Add(Caption("Aut&omatic:")); filters.Controls.Add(_fix);
            filters.Controls.Add(Caption("A&pplies to:")); filters.Controls.Add(_content);
            filters.Controls.Add(_clear);

            // TabStop off: Tab goes from the list straight to the details, not to the divider between them.
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, Margin = new Padding(0, 6, 0, 6), TabStop = false };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
            foreach (var c in new[] { "Rule", "Level", "State", "Changed by" }) _list.Columns.Add(c);
            _detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = SystemColors.Window };
            split.Panel1.Controls.Add(_list);
            split.Panel2.Controls.Add(_detail);

            var bar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0) };
            var close = new Button { Text = "&Close", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, DialogResult = DialogResult.Cancel };
            _restore = new Button { Text = "&Enable Rule", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
            _level = new Button { Text = "Change &Level…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
            _off = new Button { Text = "&Disable Rule…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
            _delete = new Button { Text = "Dele&te Rule…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
            _edit = new Button { Text = "Ed&it Rule…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
            bar.Controls.AddRange(new Control[] { close, _restore, _level, _off, _delete, _edit });
            _edit.TabIndex = 0; _delete.TabIndex = 1; _off.TabIndex = 2; _level.TabIndex = 3; _restore.TabIndex = 4; close.TabIndex = 5;   // Tab follows the buttons left to right

            root.Controls.Add(_head, 0, 0);
            root.Controls.Add(filters, 0, 1);
            root.Controls.Add(split, 0, 2);
            root.Controls.Add(bar, 0, 3);
            Controls.Add(root);
            CancelButton = close;
            // Reading order for Tab: search and filters, the list, the details, then the buttons.
            root.TabIndex = 0; filters.TabIndex = 0; split.TabIndex = 1; bar.TabIndex = 2;

            var tips = new ToolTip();
            tips.SetToolTip(_search, "Words from the rule, its examples, its page or its reference (for example google:word-list/221). Every word must match.");
            tips.SetToolTip(_off, "Stop linting for this rule.");
            tips.SetToolTip(_level, "Make this rule an error, a warning or a suggestion.");
            tips.SetToolTip(_restore, "Lint with this rule again.");
            tips.SetToolTip(_edit, "Change the wording, words or examples of this rule in your style guide.");
            tips.SetToolTip(_delete, "Remove this rule from your style guide.");

            A11y.Name(_search, "Search rules", "Words from the rule, its examples, its page or its reference. Every word must match.");
            A11y.Name(_source, "Source", "Show the rules from one source: Google, Starter rules, your team's guide, your own guide or the project.");
            A11y.Name(_state, "State", "Show rules that are enabled, disabled or at a changed level.");
            A11y.Name(_fix, "Issue type", "Show rules whose issues are Auto-fix, or rules whose issues need you.");
            A11y.Name(_content, "Applies to", "Show rules limited to one kind of content, or rules that apply everywhere.");
            A11y.Name(_list, "Rules", "Grouped by source and page. Columns are rule, level, state and changed by.");
            A11y.Name(_detail, "Details for the selected rule");
            A11y.Name(filters, "Search and filters");
            A11y.Name(bar, "Rule actions");
            A11y.Apply(this);

            _typing.Tick += (s, e) => { _typing.Stop(); Fill(); };
            _search.TextChanged += (s, e) => { _typing.Stop(); _typing.Start(); };
            _search.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; _typing.Stop(); Fill(); FocusList(); } };
            foreach (var c in new[] { _source, _state, _fix, _content }) c.SelectedIndexChanged += (s, e) => { if (!_filling) Fill(); };
            _clear.Click += (s, e) => ClearFilters();
            _list.SelectedIndexChanged += (s, e) => ShowSelected();
            _list.Resize += (s, e) => SizeColumns();
            _list.ColumnWidthChanged += (s, e) =>
            {
                if (_sizing || e.ColumnIndex < 1 || e.ColumnIndex > 3) return;
                if (_cols == null) _cols = _list.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
                _cols[e.ColumnIndex] = _list.Columns[e.ColumnIndex].Width;
                SizeColumns();
            };
            _off.Click += (s, e) => ChangeRule(false);
            _level.Click += (s, e) => ChangeRule(true);
            _restore.Click += (s, e) => Restore();
            _edit.Click += (s, e) => EditRule();
            _delete.Click += (s, e) => DeleteRule();
            KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; _search.Focus(); _search.SelectAll(); } else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; Reload(null, Current() == null ? null : Current().Key); } };
            Resize += (s, e) => FitHead();
            Shown += (s, e) => { split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Width * 3 / 5); SizeColumns(); FitHead(); Reload(null, null); _search.Focus(); };
            FormClosed += (s, e) => _typing.Dispose();
        }

        // ---- window size, position and column widths, remembered per user -------------------------------------------

        private const string StateName = "StyleRules";
        private int[] _cols;
        private bool _sizing;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect r);

        /// <summary>Flare's main window (this dialog's owner), or an empty rectangle.</summary>
        private Rectangle OwnerBounds()
        {
            try
            {
                var owner = GetWindow(Handle, 4 /* GW_OWNER */);
                Rect r;
                if (owner != IntPtr.Zero && GetWindowRect(owner, out r)) return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            catch (Exception ex) { Diagnostics.Log("style rules: owner bounds", ex); }
            return Rectangle.Empty;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                var saved = FlareAI.Core.Settings.UiState.Load(StateName);
                var ob = OwnerBounds();
                var area = (ob.IsEmpty ? Screen.FromControl(this) : Screen.FromRectangle(ob)).WorkingArea;
                var scale = DeviceDpi / 96f;
                int w, h;
                FlareAI.Core.Settings.UiState.Fit(saved, ob.IsEmpty ? area.Width : ob.Width, ob.IsEmpty ? area.Height : ob.Height, area.Width, area.Height, out w, out h,
                                                  (int)(900 * scale), (int)(600 * scale));
                var screens = Screen.AllScreens.Select(s => new[] { s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height });
                StartPosition = FormStartPosition.Manual;
                if (saved != null && FlareAI.Core.Settings.UiState.IsVisible(saved, screens)) Bounds = new Rectangle(saved.X, saved.Y, w, h);
                else
                {
                    var cx = ob.IsEmpty ? area.X + area.Width / 2 : ob.X + ob.Width / 2;
                    var cy = ob.IsEmpty ? area.Y + area.Height / 2 : ob.Y + ob.Height / 2;
                    Bounds = new Rectangle(Math.Max(area.X, Math.Min(cx - w / 2, area.Right - w)), Math.Max(area.Y, Math.Min(cy - h / 2, area.Bottom - h)), w, h);
                }
                if (saved != null && saved.Maximized) WindowState = FormWindowState.Maximized;
                if (saved != null && saved.Columns != null && saved.Columns.Count == 4) _cols = saved.Columns.ToArray();
            }
            catch (Exception ex) { Diagnostics.Log("style rules: restore window", ex); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try
            {
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                FlareAI.Core.Settings.UiState.Save(StateName, new FlareAI.Core.Settings.WindowState
                {
                    X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, Maximized = WindowState == FormWindowState.Maximized,
                    Columns = _list.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToList()
                });
            }
            catch (Exception ex) { Diagnostics.Log("style rules: save window", ex); }
        }

        private static ComboBox Combo(int width) { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(0, 3, 12, 3) }; }

        private static Label Caption(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 7, 4, 3) }; }

        /// <summary>Wraps the status line to the window's width (an AutoSize label doesn't wrap by itself).</summary>
        private void FitHead() { _head.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 24), 0); }

        private void SizeColumns()
        {
            if (_list.Columns.Count < 4) return;
            // In font heights, so the columns grow with the text at 125% and 150% scaling; the rule takes the rest.
            var em = _list.Font.Height;
            _sizing = true;
            try
            {
                _list.Columns[1].Width = _cols != null ? Math.Max(em * 3, _cols[1]) : em * 5;
                _list.Columns[2].Width = _cols != null ? Math.Max(em * 3, _cols[2]) : em * 8;
                _list.Columns[3].Width = _cols != null ? Math.Max(em * 3, _cols[3]) : em * 8;
                _list.Columns[0].Width = Math.Max(em * 10, _list.ClientSize.Width - _list.Columns[1].Width - _list.Columns[2].Width - _list.Columns[3].Width - 4);
            }
            finally { _sizing = false; }
        }

        private void FocusList()
        {
            _list.Focus();
            if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) { _list.Items[0].Selected = true; _list.Items[0].Focused = true; }
        }

        // ---- loading ---------------------------------------------------------------------------------------------

        /// <summary>Reads the guide (and the requests) off the UI thread, then shows the list; <paramref name="select"/> stays selected.</summary>
        private void Reload(string done, string select)
        {
            SetButtons(null);
            _head.Text = (done != null ? done + "  " : "") + "Reading the style guide…";
            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                if (_team != null) _team.Recheck();
                var requests = new List<GuideRequest>();
                if (_team != null && _team.IsStrict)
                    try { requests = GuideRequests.Load(_team); } catch (Exception ex) { Diagnostics.Log("style rules: requests", ex); }
                e.Result = RuleCatalog.Build(_style.Compose(_project), _isDefault, requests, r => GuideRequests.IsMine(_team, r));
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (IsDisposed) return;
                if (e.Error != null)
                {
                    Diagnostics.Log("Style rules", e.Error); ErrorReporter.Background(this, "House Style couldn't read the style guide.", e.Error);
                    _head.Text = "House Style couldn't read the style guide: " + e.Error.Message;
                    return;
                }
                _catalog = (RuleCatalog)e.Result;
                FillFilters();
                Fill(select, done);
            };
            worker.RunWorkerAsync();
        }

        private void FillFilters()
        {
            _filling = true;
            try
            {
                Refill(_source, new[] { new Choice { Text = "All sources" } }.Concat(_catalog.Sources.Select(x => new Choice { Text = x, Value = x })));
                Refill(_state, new[]
                {
                    new Choice { Text = "Any state" }, new Choice { Text = "Enabled", Value = RuleState.On },
                    new Choice { Text = "Disabled", Value = RuleState.Off }, new Choice { Text = "Level changed", Value = RuleState.LevelChanged }
                });
                Refill(_fix, new[] { new Choice { Text = "Any issue type" }, new Choice { Text = "Auto-fix", Value = true }, new Choice { Text = "Needs you", Value = false } });
                Refill(_content, new[] { new Choice { Text = "Any content" }, new Choice { Text = "Everywhere", Value = RuleCatalog.Everywhere } }
                    .Concat(_catalog.ContentTags.Select(t => new Choice { Text = t, Value = t })));
            }
            finally { _filling = false; }
        }

        /// <summary>Replaces a filter's choices, keeping the one selected when it still exists.</summary>
        private static void Refill(ComboBox box, IEnumerable<Choice> choices)
        {
            var was = box.SelectedItem as Choice;
            box.BeginUpdate();
            box.Items.Clear();
            foreach (var c in choices) box.Items.Add(c);
            var keep = was == null ? -1 : box.Items.Cast<Choice>().ToList().FindIndex(c => c.Text == was.Text);
            box.SelectedIndex = keep >= 0 ? keep : 0;
            box.EndUpdate();
        }

        private void ClearFilters()
        {
            _filling = true;
            _search.Text = "";
            foreach (var c in new[] { _source, _state, _fix, _content }) if (c.Items.Count > 0) c.SelectedIndex = 0;
            _filling = false;
            _typing.Stop();
            Fill();
            _search.Focus();
        }

        private static object Value(ComboBox box) { var c = box.SelectedItem as Choice; return c == null ? null : c.Value; }

        private void Fill() { Fill(Current() == null ? null : Current().Key, null); }

        private void Fill(string select, string done)
        {
            if (_catalog == null) return;
            var filter = new CatalogFilter
            {
                Text = _search.Text, Source = (string)Value(_source), State = (RuleState?)Value(_state), HasFix = (bool?)Value(_fix), Content = (string)Value(_content)
            };
            var rules = _catalog.Search(filter);
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                _list.Groups.Clear();
                var groups = new Dictionary<string, ListViewGroup>();
                ListViewItem chosen = null;
                foreach (var r in rules.OrderBy(r => r.SourceIndex))   // stable: guide order within a source
                {
                    var name = r.Source + " › " + r.Page;
                    ListViewGroup g;
                    if (!groups.TryGetValue(name, out g)) { g = new ListViewGroup(name, name); groups[name] = g; _list.Groups.Add(g); }
                    var item = new ListViewItem(new[] { Row(r), r.LevelLabel, r.StateLabel + (r.OpenRequest != null ? " · request open" : ""), r.Change == null ? "" : Who(r.Change) }, g)
                    {
                        Tag = r, ToolTipText = r.Key + (r.Section.Length > 0 ? " · " + r.Section : "")
                    };
                    if (r.State == RuleState.Off) item.ForeColor = SystemColors.GrayText;   // the State column says it in words too
                    _list.Items.Add(item);
                    if (select != null && string.Equals(r.Key, select, StringComparison.OrdinalIgnoreCase)) chosen = item;
                }
                // Keep the same rule selected; if it isn't listed any more, select nothing (never an unrelated rule).
                if (chosen != null) { chosen.Selected = true; chosen.Focused = true; chosen.EnsureVisible(); }
            }
            finally { _list.EndUpdate(); }
            SizeColumns();   // a vertical scroll bar may have come or gone
            var all = _catalog.Rules.Count;
            var off = _catalog.Rules.Count(r => r.State == RuleState.Off);
            var notice = _team == null ? null : _team.AccessNotice;
            _head.Text = (done != null ? done + "  " : "") +
                         (rules.Count == all ? Plural.Of(all, "rule") : rules.Count + " of " + Plural.Of(all, "rule")) + " (" + off + " disabled). Default means it ships with House Style." +
                         (notice != null ? "  " + notice : "");
            ShowSelected();
        }

        private static string Row(CatalogRule r)
        {
            var text = r.Title ?? r.Text ?? "";
            return text.Length > 160 ? text.Substring(0, 159) + "…" : text;
        }

        private static string Who(RuleOverride o)
        {
            if (o.IsDefault) return "Default";
            var who = string.IsNullOrEmpty(o.By) ? o.Scope : o.By;
            return who + (string.IsNullOrEmpty(o.Date) ? "" : ", " + o.Date);
        }

        private CatalogRule Current() { return _list.SelectedItems.Count == 1 ? (CatalogRule)_list.SelectedItems[0].Tag : null; }

        /// <summary>True for a built-in check: its layer is the one built in code (IsBuiltIn), not a guide's. Its words are in
        /// code, so it has no Avoid line; its level can still change.</summary>
        private bool IsBuiltIn(CatalogRule r)
        {
            return r != null && _style.Compose(_project).Layers.Any(l => l.IsBuiltIn && string.Equals(l.Name, r.Origin, StringComparison.OrdinalIgnoreCase));
        }

        // ---- details --------------------------------------------------------------------------------------------

        private void ShowSelected()
        {
            var r = Current();
            SetButtons(r);
            if (r == null) { _detail.Text = _catalog != null && _list.Items.Count == 0 ? "No rule matches. Clear the filters or change the search." : ""; return; }
            var nl = Environment.NewLine;
            var sb = new StringBuilder();
            sb.Append(r.Title).Append(nl).Append(nl);
            sb.Append("Source: ").Append(r.Source).Append(" › ").Append(r.Page).Append(r.Section.Length > 0 ? " › " + r.Section : "").Append(nl);
            if (r.IsTable)
            {
                sb.Append("Level: ").Append(r.LevelLabel);
                if (r.State == RuleState.LevelChanged || (r.State == RuleState.Off && r.DefaultLevel != null)) sb.Append(" (written as ").Append(Severity.Label(r.DefaultLevel)).Append(")");
                sb.Append(nl);
            }
            else sb.Append("Level: set by the AI review (the AI applies this rule)").Append(nl);
            sb.Append("State: ").Append(r.StateLabel).Append(nl);
            if (r.Change != null)
            {
                var o = r.Change;
                sb.Append(o.IsDefault ? "Shipped with House Style" : "Changed" + (string.IsNullOrEmpty(o.By) ? "" : " by " + o.By) + (string.IsNullOrEmpty(o.Date) ? "" : " on " + o.Date))
                  .Append(" in ").Append(o.IsDefault ? "the bundled style guide" : o.Scope)
                  .Append(": ").Append(o.IsTurnOn ? "enabled" : o.Setting == "off" ? "disabled" : Severity.Label(o.Setting)).Append(nl);
                if (!string.IsNullOrEmpty(o.Note)) sb.Append("Why: ").Append(o.Note).Append(nl);
                if (o.RequestId != null) sb.Append("Approved by an owner (request ").Append(o.RequestId).Append(")").Append(nl);
            }
            if (r.OpenRequest != null)
            {
                var q = r.OpenRequest;
                sb.Append("Request open").Append(r.RequestIsMine ? " (yours)" : "").Append(": ").Append(q.TitleWith(_ => "this rule")).Append(". Why: ").Append(q.Reason).Append(nl);
            }
            sb.Append(nl);
            if (r.IsTable)
            {
                if (r.IsPattern) sb.Append("Pattern: ").Append(r.Avoid.FirstOrDefault()).Append(nl);
                else if (!IsBuiltIn(r)) sb.Append("Avoid: ").Append(string.Join(", ", r.Avoid.Select(a => "'" + a + "'").ToArray())).Append(nl);
                sb.Append(r.Action == "replace" ? "Use: '" + r.Use + "' (changed automatically)"
                        : r.Action == "remove" ? "Use: delete it (changed automatically)"
                        : "Use: rewrite it (the writer decides)").Append(nl);
                if (!string.IsNullOrEmpty(r.Example)) sb.Append("Example: ").Append(r.Example).Append(nl);
            }
            if (r.Terms.Count > 0) sb.Append("About the words: ").Append(string.Join(", ", r.Terms.ToArray())).Append(nl);
            if (!string.IsNullOrEmpty(r.Bad)) sb.Append("Bad: ").Append(r.Bad).Append(nl);   // built-in checks have no examples: null
            if (!string.IsNullOrEmpty(r.Good)) sb.Append("Good: ").Append(r.Good).Append(nl);
            sb.Append("Applies to: ").Append(r.Content.Count == 0 ? "everywhere" : string.Join(", ", r.Content.ToArray()));
            if (r.Products.Count > 0) sb.Append("; products: ").Append(string.Join(", ", r.Products.ToArray()));
            sb.Append(nl);
            if (!r.ForOurProducts) sb.Append("Not used: it is for products your style guide doesn't cover.").Append(nl);
            var others = _catalog.OverlapsOf(r);
            if (others.Count > 0)
            {
                sb.Append(nl).Append("These rules flag the same words. Disabling this rule can disable them too.").Append(nl);
                foreach (var o in others) sb.Append("  • ").Append(o.Source).Append(" › ").Append(o.Page).Append(": ").Append(Row(o)).Append(" [").Append(o.StateLabel).Append("]").Append(nl);
            }
            sb.Append(nl).Append("Reference: ").Append(r.Key);
            _detail.Text = sb.ToString();
            _detail.SelectionStart = 0;
        }

        // ---- actions ----------------------------------------------------------------------------------------------

        private bool Requesting { get { return _team != null && _team.ChangesNeedRequest; } }

        private void SetButtons(CatalogRule r)
        {
            var asking = Requesting;
            _off.Text = asking ? "&Request to Disable…" : "&Disable Rule…";
            _level.Text = asking ? "Request a &Level…" : "Change &Level…";
            var canTeam = _team != null && _team.GuideRoot != null;
            var canProject = _openProject != null && ProjectRules.OverridesPath(_openProject) != null;
            _off.Enabled = r != null && r.State != RuleState.Off && (canTeam || canProject);
            _level.Enabled = r != null && r.IsTable && (canTeam || canProject);
            var restoreAsks = r != null && !r.ChangeIsProject && asking;
            var toDefault = r != null && r.State != RuleState.Off && r.Change != null && !r.IsDefault;
            _restore.Text = toDefault ? (restoreAsks ? "Request the &Default…" : "Use the &Default") : (restoreAsks ? "Request to &Enable…" : "&Enable Rule");
            _restore.Enabled = r != null && r.Change != null && !(r.IsDefault && r.State != RuleState.Off) && (r.ChangeIsProject || canTeam);
            // Edit and Delete change the guide's own topic, so they're for whoever can write it (owners, a writer's own guide).
            _edit.Visible = _delete.Visible = !asking;
            _edit.Enabled = _delete.Enabled = !asking && Editable(r);
            foreach (var b in new[] { _edit, _delete, _off, _level, _restore }) b.AccessibleName = b.Text.Replace("&", "").Replace("…", "");
        }

        /// <summary>A rule written in a topic of the guide this writer's changes go to (the team's guide, or their own):
        /// not Google's generated rules, not the guide shipped with House Style, not an earlier configured guide with owners
        /// of its own, not the open project's (edited in that project), not generated from variables or a guide.md file.</summary>
        private bool Editable(CatalogRule r)
        {
            if (r == null || r.Id == null || r.File == null || _team == null || _team.GuideRoot == null) return false;
            var ext = System.IO.Path.GetExtension(r.File);
            return (ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".html", StringComparison.OrdinalIgnoreCase)) &&
                   r.Origin != "google" && !(_isDefault != null && _isDefault(r.File)) && !r.Source.StartsWith("Project ", StringComparison.Ordinal) &&
                   FlareAI.Core.Settings.PluginSettings.IsUnder(r.File, _team.GuideRoot);
        }

        /// <summary>The guide topic as it is now, for Edit Rule and Delete Rule; null (with the reason shown) when this
        /// writer may no longer change the guide or the rule isn't where Rules last read it.</summary>
        private GuideLayer GuideFor(CatalogRule r)
        {
            if (RuleChanges.NeedsRequest(_team))
            {
                Reload("Your changes to this style guide now go to its owners as requests.", r.Key);
                return null;
            }
            var layer = _style.EditableGuide(_team.GuideRoot);
            if (layer == null || layer.RuleFields(r.Key, r.File, r.Text) == null)
            {
                Reload("The style guide changed. Check the rule and try again.", r.Key);
                return null;
            }
            return layer;
        }

        /// <summary>Edit Rule: rewrites the rule's row or bullet in its guide topic.</summary>
        private void EditRule()
        {
            var r = Current();
            if (!Editable(r)) return;
            try
            {
                var layer = GuideFor(r);
                if (layer == null) return;
                List<KeyValuePair<string, string>> fields;
                using (var d = new RuleEditDialog(layer.RuleFields(r.Key, r.File, r.Text)))
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    fields = d.Fields;
                }
                // Read again: the topic may have changed while the dialog was open.
                layer = GuideFor(r);
                if (layer == null) return;
                var file = layer.EditRule(r.Key, r.File, r.Text, fields);
                if (file == null) { Reload("The style guide changed. Check the rule and try again.", r.Key); return; }
                _style.Invalidate();
                Diagnostics.Log("Rules: edited " + r.Key + " in " + file);
                Reload("Rule saved." + Team.CommitHint(_team == null ? null : _team.GuideRoot) + " It applies the next time you lint.", r.Key);
            }
            catch (Exception ex) { Failed(ex, "save the rule"); }
        }

        /// <summary>Delete Rule: removes the rule's row or bullet from its guide topic, after asking.</summary>
        private void DeleteRule()
        {
            var r = Current();
            if (!Editable(r)) return;
            if (!Notify.Confirm(this, "Delete this rule from your style guide?\n\n" + r.Source + " · " + r.Page + ": " + r.Title +
                                      "\n\nTo stop it for now and keep it in the guide, use Disable Rule instead.", "Delete Rule")) return;
            try
            {
                var layer = GuideFor(r);
                if (layer == null) return;
                var file = layer.DeleteRule(r.Key, r.File, r.Text);
                if (file == null) { Reload("The style guide changed. Check the rule and try again.", r.Key); return; }
                _style.Invalidate();
                Diagnostics.Log("Rules: deleted " + r.Key + " from " + file);
                Reload("Rule deleted." + Team.CommitHint(_team == null ? null : _team.GuideRoot) + " It stops applying the next time you lint.", null);
            }
            catch (Exception ex) { Failed(ex, "delete the rule"); }
        }

        /// <summary>Turn off (or Change level): asks scope, level and why (and, on a read-only guide, sends a request).</summary>
        private void ChangeRule(bool level)
        {
            var r = Current();
            if (r == null) return;
            var asking = RuleChanges.WillRequest(_team, r, false);
            var canTeam = _team != null && _team.GuideRoot != null;
            var canProject = _openProject != null && ProjectRules.OverridesPath(_openProject) != null;
            var others = _catalog.OverlapsOf(r).Where(o => o.State != RuleState.Off).ToList();
            var setting = level ? r.Level ?? r.DefaultLevel ?? "warning" : "off";
            var described = r.Source + " · " + r.Page + ": " + r.Title;
            RuleSettingChoice choice;
            if (asking)
            {
                choice = RequestForm.Ask(this, new RequestQuestion
                {
                    RuleText = described,
                    Notice = "This goes to the style guide owners (" + _team.OwnerNames + "). You see their answer the next time you lint." +
                             (_team.AccessNotice == null ? "" : Environment.NewLine + _team.AccessNotice),
                    CanAskTeam = true, CanAskProject = canProject,
                    Wording = !level && !r.IsTable && r.Origin != "google" ? r.Text : null,
                    Setting = setting, ForProject = false
                });
            }
            else
            {
                using (var form = new RuleSettingForm(described, canTeam, _team != null && _team.IsPersonal,
                                                      others.Select(o => new RuleRef { Id = o.Key, Layer = o.Origin, Name = o.Source + " · " + o.Page + ": " + o.Title }).ToList(),
                                                      setting, canProject, level ? "Change Level" : "Disable Rule"))
                {
                    if (form.ShowDialog(this) != DialogResult.OK) return;
                    choice = new RuleSettingChoice { ForTeam = form.ForTeam, Setting = form.Setting, Reason = form.Reason, AlsoOthers = form.AlsoOthers };
                }
            }
            if (choice == null) return;
            if (asking && string.IsNullOrWhiteSpace(choice.Reason)) { _head.Text = "Not sent. Add a reason for the owners."; return; }
            try
            {
                var result = RuleChanges.Change(_team, r, choice, others, _openProject);
                Reload(Describe(result, choice.ForTeam, choice.Setting), r.Key);
            }
            catch (GuideWriteException ex) when (choice.ForTeam && _team != null && _team.IsStrict && !_team.IsPersonal)
            {
                // The guide stopped being writable (share offline, permissions changed): the same change can go as a request.
                Diagnostics.Log("Style rules: change", ex);
                _team.ProbeGuide();
                if (Notify.Confirm(this, "House Style couldn't write to " + ex.Target + ". " + ex.Message + "\n\nNothing was changed. Send this change to the style guide owners (" +
                                         _team.OwnerNames + ") as a request instead? Your reason is kept.", ActionName))
                {
                    try { Reload(Describe(RuleChanges.Change(_team, r, choice, others, _openProject), choice.ForTeam, choice.Setting), r.Key); }
                    catch (Exception again) { Failed(again, "send your request"); }
                }
                else Reload("The rule was not changed.", r.Key);
            }
            catch (Exception ex) { Failed(ex, "change the rule"); }
        }

        /// <summary>Turn back on / Use the default: removes the change in effect (a request on a read-only guide).</summary>
        private void Restore()
        {
            var r = Current();
            if (r == null || r.Change == null) return;
            string reason = null;
            var toDefault = r.State != RuleState.Off;
            var also = toDefault ? new List<CatalogRule>() : RuleChanges.CompanionsOf(_catalog, r);
            if (also.Count > 0)
            {
                var asks = RuleChanges.WillRequest(_team, r, true);
                using (var f = new AlsoRestoreForm(r.Source + " · " + r.Page + ": " + r.Title, also.Select(o => o.Source + " · " + o.Page + ": " + o.Title).ToList(),
                                                   asks ? "Request to Enable" : "Enable Rule"))
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    if (!f.AlsoOthers) also.Clear();
                }
            }
            if (RuleChanges.WillRequest(_team, r, true))
            {
                reason = RequestForm.AskReason(this, (toDefault ? "Return to the default " : "Enable ") + r.Title + (string.IsNullOrEmpty(r.Change.Note) ? "" : " (changed because " + r.Change.Note + ")"),
                    "This goes to the style guide owners (" + _team.OwnerNames + "). You see their answer the next time you lint." +
                    (_team.AccessNotice == null ? "" : Environment.NewLine + _team.AccessNotice));
                if (reason == null) return;
            }
            try
            {
                var result = RuleChanges.Restore(_team, r, also, reason);
                Reload(result.Requested ? Describe(result, true, null)
                       : (toDefault ? "Back to the default" : "Enabled") + (result.Changed.Count > 1 ? " (" + result.Changed.Count + " rules)" : "") + ". It applies the next time you lint." +
                         (result.Incomplete != null ? " " + result.Incomplete : ""), r.Key);
            }
            catch (GuideWriteException ex) when (!r.ChangeIsProject && _team != null && _team.IsStrict && !_team.IsPersonal)
            {
                Diagnostics.Log("Style rules: restore", ex);
                _team.ProbeGuide();
                Notify.Error(this, "House Style couldn't write to " + ex.Target + ".", ex.Message + "\n\nNothing was changed. Select the button again to ask the style guide owners instead.", ex, ActionName);
                Reload(null, r.Key);
            }
            catch (Exception ex) { Failed(ex, "enable the rule"); }
        }

        private string Describe(RuleChangeResult result, bool forTeam, string setting)
        {
            if (result.Requested)
                return result.Problem == null
                    ? "Request sent to the style guide owners (" + _team.OwnerNames + "). The rule stays as it is until they decide."
                    : "Request saved but not sent yet (" + result.Problem + "). House Style sends it the next time you lint.";
            var what = (setting == "off" ? "Disabled" : "Set to " + Severity.Label(setting)) + (result.Changed.Count > 1 ? " (" + result.Changed.Count + " rules)" : "");
            FlareAI.Core.Diagnostics.Log("Rules: " + setting + " written to " + result.File);
            var where = !forTeam ? " for this project."
                      : _team != null && _team.IsPersonal ? "."
                      : " for the team." + Team.CommitHint(_team == null ? null : _team.GuideRoot);
            return what + where + " It applies the next time you lint." + (result.Incomplete != null ? " " + result.Incomplete : "");
        }

        private void Failed(Exception ex, string what)
        {
            Diagnostics.Log("Style rules: " + what, ex);
            Notify.Error(this, "House Style couldn't " + what + ".", ex.Message + "\n\nNothing was changed. The rule still applies.", ex, ActionName);
            Reload(null, Current() == null ? null : Current().Key);
        }
    }
}
