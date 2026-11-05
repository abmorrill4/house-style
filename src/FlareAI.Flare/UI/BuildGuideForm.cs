using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Build style guide": proposes rules from evidence instead of asking anyone to write a guide. Mines the open
    /// project (spelling variants, competing words, capitalization, heading case — with counts and real sentences)
    /// and extracts rules from pasted notes, an old guide, or another tool's prompt. The owner checks the ones to
    /// keep, picks the preferred form where the team was split, and adds them to the guide; Validate style guide
    /// then tests each new row against its example. When the guide is read-only for this writer (or they aren't an owner),
    /// the checked rules go to the owners as requests instead.
    /// </summary>
    public sealed class BuildGuideForm : Form
    {
        private readonly Team _team;
        private readonly StyleEngine _style;
        private readonly string _project;
        private readonly Func<ActionRunner> _runner;
        private readonly Func<string, IDocumentAdapter> _open;
        private readonly Label _head;
        private readonly ListView _list;
        private readonly RichTextBox _detail;
        private readonly ComboBox _prefer;
        private readonly Label _preferLabel;
        private readonly Button _mine, _notes, _add, _edit, _remove;
        private readonly ProgressBar _progress;

        public BuildGuideForm(Team team, StyleEngine style, string project, Func<ActionRunner> runner, Func<string, IDocumentAdapter> open)
        {
            _team = team; _style = style; _project = project; _runner = runner; _open = open;
            Text = Notify.Caption(Commands.ImportRules.Label);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(940, 640);
            MinimumSize = new Size(640, 420);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _head = new LiveLabel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0) };
            _progress = new ProgressBar { Dock = DockStyle.Top, Height = 5, Style = ProgressBarStyle.Marquee, Visible = false };
            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(6, 4, 6, 0) };
            _mine = new Button { Text = "Mine this project", AutoSize = true, Enabled = project != null && style != null };
            _notes = new Button { Text = "Add notes or an old guide…", AutoSize = true };
            _mine.Click += (s, e) => Mine();
            _notes.Click += (s, e) => Notes();
            tools.Controls.AddRange(new Control[] { _mine, _notes });

            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, CheckBoxes = true };
            _list.Columns.Add("Suggested rule", 430); _list.Columns.Add("Becomes", 110); _list.Columns.Add("Evidence", 330);
            _list.Groups.Add("content", "From your content");
            _list.Groups.Add("notes", "From your notes");
            _list.SelectedIndexChanged += (s, e) => ShowDetail();
            _list.ItemChecked += (s, e) => UpdateAdd();

            var detail = new Panel { Dock = DockStyle.Bottom, Height = 200 };
            var pick = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4, 4, 4, 0) };
            _preferLabel = new Label { Text = "Preferred form:", AutoSize = true, Padding = new Padding(0, 6, 0, 0), Visible = false };
            _prefer = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Visible = false };
            _prefer.SelectedIndexChanged += (s, e) => { var c = Current(); if (c != null && _prefer.SelectedItem != null) { c.Choose((string)_prefer.SelectedItem); Refresh(c); } };
            pick.Controls.AddRange(new Control[] { _preferLabel, _prefer });
            _detail = DiffView.Create();
            _detail.Dock = DockStyle.Fill; _detail.BackColor = SystemColors.Control;
            detail.Controls.Add(_detail);
            detail.Controls.Add(pick);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            _add = new Button { Text = "Add checked rules to the guide", AutoSize = true, Enabled = false };
            _add.Click += (s, e) => AddToGuide();
            _edit = new Button { Text = "Edit rule…", AutoSize = true, Enabled = false };
            _edit.Click += (s, e) => EditCurrent();
            _remove = new Button { Text = "Remove", AutoSize = true, Enabled = false };
            _remove.Click += (s, e) => RemoveCurrent();
            bar.Controls.AddRange(new Control[] { close, _add, _remove, _edit });
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F2 && _edit.Enabled) { e.Handled = e.SuppressKeyPress = true; EditCurrent(); }
                else if (e.KeyCode == Keys.Delete && _remove.Enabled) { e.Handled = e.SuppressKeyPress = true; RemoveCurrent(); }
            };
            _list.DoubleClick += (s, e) => { if (_edit.Enabled) EditCurrent(); };

            Controls.Add(_list);
            Controls.Add(detail);
            Controls.Add(bar);
            Controls.Add(tools);
            Controls.Add(_progress);
            Controls.Add(_head);
            CancelButton = close;
            A11y.Name(_list, "Suggested rules", "Tick the rules to add to the guide. Columns: suggested rule, becomes, evidence.");
            A11y.Name(_detail, "Details for the selected rule");
            A11y.Name(_edit, "Edit rule", "Change the selected rule's text, Avoid, Use, severity or reason before adding it. Shortcut F2.");
            A11y.Name(_remove, "Remove rule", "Drop the selected rule from this list. Shortcut Delete.");
            A11y.Name(_prefer, "Preferred form", "Pick which form the guide should prefer where the team was split.");
            A11y.Name(detail, "Rule details"); A11y.Name(bar, "Dialog buttons");
            A11y.Apply(this);
            if (_team != null) _team.Recheck();
            _head.Text = "Find rules in what your team already writes, or in a document such as an old style guide. Select the ones to keep, then add them to the guide." +
                         (Requesting ? "\n" + _team.AccessNotice : "");
            UpdateAdd();
        }

        /// <summary>Checked rules go to the owners as requests (read-only guide, or not an owner).</summary>
        private bool Requesting { get { return _team != null && _team.ChangesNeedRequest; } }

        private void Mine()
        {
            Busy(true, "Reading every topic and snippet in the project…");
            var worker = new BackgroundWorker();
            int files = 0;
            worker.DoWork += (s, e) => e.Result = _style.Mine(_project, out files);
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (IsDisposed) return;
                Busy(false, null);
                if (e.Error != null) { Diagnostics.Log("Build guide: mine", e.Error); ErrorReporter.Background(this, "House Style couldn't look through the project.", e.Error); _head.Text = "Couldn't look through the project: " + e.Error.Message; return; }
                var found = (List<RuleCandidate>)e.Result;
                Add(found, "content");
                _head.Text = files + " topics and snippets read. " + Plural.Of(found.Count, "suggested rule") + " the guide doesn't cover yet." +
                             (found.Count == 0 ? " Your content is consistent with the guide." : " Where the team was split, pick the preferred form.");
            };
            worker.RunWorkerAsync();
        }

        private void Notes()
        {
            var text = NotesDialog.Ask(this);
            if (string.IsNullOrEmpty(text)) return;
            var runner = _runner();
            if (runner == null) return;
            Busy(true, "Reading your notes…");
            var worker = new BackgroundWorker();
            var skipped = 0;
            worker.DoWork += (s, e) =>
            {
                var found = runner.ExtractRules(text);
                // Like mining: drop rows whose words the guide already flags (the validator would report the duplicate).
                if (_style != null)
                {
                    var rows = found.Where(c => c.IsRow).ToList();
                    var words = rows.SelectMany(c => Split(c.Avoid)).Distinct().ToList();
                    if (words.Count > 0)
                    {
                        var flagged = new HashSet<string>(_style.Lint(words, new List<int>(), _project).Select(h => words[h.Index]));
                        foreach (var c in rows) if (Split(c.Avoid).All(flagged.Contains)) { found.Remove(c); skipped++; }
                    }
                }
                e.Result = found;
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                if (IsDisposed) return;
                Busy(false, null);
                if (e.Error != null) { Diagnostics.Log("Build guide: notes", e.Error); ErrorReporter.Background(this, "House Style couldn't read the notes.", e.Error); _head.Text = "Could not read the notes: " + e.Error.Message; return; }
                var found = (List<RuleCandidate>)e.Result;
                // A second import can repeat what the first one found: leave out rules already in the list.
                var have = _list.Items.Cast<ListViewItem>().Select(i => (RuleCandidate)i.Tag).ToList();
                var fresh = new List<RuleCandidate>();
                foreach (var c in found)
                    if (!have.Any(h => RuleCandidate.SameRule(h, c))) { fresh.Add(c); have.Add(c); }
                var repeated = found.Count - fresh.Count;
                Add(fresh, "notes");
                _head.Text = Plural.Of(fresh.Count, "rule") + " found in your notes. Each shows the words it came from. Select the ones to keep." +
                             (skipped > 0 ? " " + skipped + " more are already in the guide." : "") +
                             (repeated > 0 ? " " + repeated + " were already in the list." : "");
            };
            worker.RunWorkerAsync();
        }

        private void Add(List<RuleCandidate> found, string group)
        {
            foreach (var c in found)
            {
                var item = new ListViewItem(new[] { c.Title, Becomes(c), Evidence(c) }) { Tag = c, Group = _list.Groups[group] };
                _list.Items.Add(item);
            }
            if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
            UpdateAdd();
        }

        private static IEnumerable<string> Split(string list)
        {
            return (list ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);
        }

        private static string Becomes(RuleCandidate c) { return c.IsRow ? "Avoid/Use row" : "Prose rule"; }

        private static string Evidence(RuleCandidate c)
        {
            if (c.Forms.Count > 0) return string.Join(" · ", c.Forms.Select(f => "“" + f.Text + "” " + f.Count + "×").ToArray());
            return string.IsNullOrEmpty(c.Quote) ? "" : "“" + c.Quote + "”";
        }

        private RuleCandidate Current() { return _list.SelectedItems.Count == 1 ? (RuleCandidate)_list.SelectedItems[0].Tag : null; }

        private void ShowDetail()
        {
            var c = Current();
            _edit.Enabled = _remove.Enabled = c != null;
            var terms = c != null && c.Forms.Count > 1 && c.Kind != "heading";
            _prefer.Visible = _preferLabel.Visible = terms;
            if (terms)
            {
                _prefer.Items.Clear();
                foreach (var f in c.Forms) _prefer.Items.Add(f.Text);
                _prefer.SelectedItem = c.Prefer;
            }
            if (c != null) Refresh(c); else _detail.Clear();
        }

        private void Refresh(RuleCandidate c)
        {
            _detail.Clear();
            Write(c.Title, FontStyle.Bold);
            if (c.Forms.Count > 0) Write("Your content: " + Evidence(c), FontStyle.Regular);
            if (!string.IsNullOrEmpty(c.Quote)) Write("From your notes: “" + c.Quote + "”", FontStyle.Italic);
            foreach (var e in c.Examples.Take(4)) Write("• " + e.Text + (string.IsNullOrEmpty(e.File) ? "" : "  (" + e.File + ")"), FontStyle.Regular);
            Write("", FontStyle.Regular);
            if (c.IsRow)
                Write("Adds a table row  ·  Avoid: " + c.Avoid + "  ·  Use: " + (string.IsNullOrEmpty(c.Use) ? "(flag only)" : c.Use) + "  ·  " + c.Severity +
                      "\nWhy: " + c.Why + "\nExample (must break the rule): " + c.Example, FontStyle.Italic);
            else
                Write("Adds a prose rule: " + c.Text + (string.IsNullOrEmpty(c.Bad) ? "" : "\nBad: " + c.Bad) + (string.IsNullOrEmpty(c.Good) ? "" : "\nGood: " + c.Good), FontStyle.Italic);
            _detail.Select(0, 0);
            foreach (ListViewItem item in _list.Items) if (item.Tag == c) item.SubItems[2].Text = Evidence(c);
        }

        private void EditCurrent()
        {
            var c = Current();
            if (c == null) return;
            using (var dlg = new CandidateEditDialog(c))
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (ListViewItem item in _list.Items)
                if (item.Tag == c) { item.SubItems[0].Text = c.Title; item.SubItems[1].Text = Becomes(c); }
            ShowDetail();
        }

        private void RemoveCurrent()
        {
            if (_list.SelectedItems.Count != 1) return;
            var item = _list.SelectedItems[0];
            var at = item.Index;
            _list.Items.Remove(item);
            if (_list.Items.Count > 0) _list.Items[Math.Min(at, _list.Items.Count - 1)].Selected = true; else ShowDetail();
            UpdateAdd();
        }

        private void Write(string text, FontStyle style)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionFont = new Font(_detail.Font, style);
            _detail.AppendText((_detail.TextLength > 0 ? "\n" : "") + text);
        }

        private void UpdateAdd()
        {
            _add.Enabled = _team != null && _team.GuideRoot != null && _list.CheckedItems.Count > 0;
            var n = _list.CheckedItems.Count > 0 ? _list.CheckedItems.Count + " " : "";
            _add.Text = Requesting ? "Request adding " + n + "checked " + (_list.CheckedItems.Count == 1 ? "rule" : "rules") + "…" : "Add " + n + "checked " + (_list.CheckedItems.Count == 1 ? "rule" : "rules") + " to the guide";
            _add.AccessibleName = _add.Text;
        }

        private void AddToGuide()
        {
            var accepted = _list.CheckedItems.Cast<ListViewItem>().Select(i => (RuleCandidate)i.Tag).ToList();
            _team.Recheck();   // before any write: permissions or the owners list may have changed
            if (Requesting) { RequestAdding(accepted, null); return; }
            var root = _team.GuideRoot;
            var topic = Path.Combine(root, @"Content\Resources\StyleGuide\From-our-content.htm");
            try
            {
                // The team guide, or the writer's own guide (created on first use): never the read-only guide in the plugin folder.
                int n;
                topic = GuideBuilder.AddToGuide(_team, accepted, out n);
                foreach (var i in _list.CheckedItems.Cast<ListViewItem>().ToList()) _list.Items.Remove(i);
                _head.Text = "Added " + Plural.Of(n, "rule") + " to " + topic + ". Review the topic in Flare and run Test Rules" +
                             (!_team.IsPersonal && Team.IsUnderSourceControl(_team.GuideRoot) ? ", then commit the guide." : ".");
                if (_open != null) _open(topic);
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && _team.IsStrict && !_team.IsPersonal)
            {
                Diagnostics.Log("Build guide: write", ex);
                _team.ProbeGuide();
                if (Notify.Confirm(this, "House Style couldn't write to " + topic + ". " + ex.Message + "\n\nNothing was added. Send the selected rules to the style guide owners (" + _team.OwnerNames + ") as requests instead?", Commands.ImportRules.Label))
                    RequestAdding(accepted, null);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Build guide: write", ex);
                Notify.Error(this, "House Style couldn't add the rules because it couldn't write to " + topic + ".",
                             ex.Message + "\n\nNothing was added. Check that you can write to that folder and the file isn't read-only, then try again.", ex, Commands.ImportRules.Label);
            }
            UpdateAdd();
        }

        /// <summary>One request per checked rule, all with the same reason; they leave the list once sent (or saved to send).</summary>
        private void RequestAdding(List<RuleCandidate> rules, string reason)
        {
            if (rules.Count == 0) return;
            reason = reason ?? RequestForm.AskReason(this, "Add " + Plural.Of(rules.Count, "rule") + " to the style guide: " + string.Join("; ", rules.Take(3).Select(c => c.Title).ToArray()) + (rules.Count > 3 ? "; …" : ""),
                "This goes to the style guide owners (" + _team.OwnerNames + "), one request per rule. You see their answer the next time you lint." +
                (_team.AccessNotice == null ? "" : Environment.NewLine + _team.AccessNotice));
            if (reason == null) return;
            string problem = null;
            var sent = 0;
            foreach (var c in rules)
            {
                var r = GuideRequests.New(_team, RequestKinds.AddRule, reason);
                r.Candidate = c; r.Rule = c.Title;
                if (c.Forms.Count > 0) r.Evidence.Add("Your content: " + Evidence(c));
                foreach (var e in c.Examples.Take(3)) r.Evidence.Add(e.Text + (string.IsNullOrEmpty(e.File) ? "" : " (" + e.File + ")"));
                try
                {
                    string why;
                    GuideRequests.Send(_team, r, out why);
                    problem = problem ?? why;
                    sent++;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("Build guide: request", ex);
                    Notify.Error(this, "House Style couldn't send the request for \"" + c.Title + "\".", ex.Message + "\n\nYour reason: " + reason, ex, Commands.ImportRules.Label);
                    break;
                }
                foreach (var i in _list.Items.Cast<ListViewItem>().Where(i => i.Tag == c).ToList()) _list.Items.Remove(i);
            }
            _head.Text = problem == null ? "Sent " + Plural.Of(sent, "request") + " to the style guide owners (" + _team.OwnerNames + "). You see their answer the next time you lint."
                                         : Plural.Of(sent, "request") + " saved but not sent yet (" + problem + "). House Style sends them the next time you lint.";
            UpdateAdd();
        }

        private void Busy(bool on, string text)
        {
            _progress.Visible = on;
            _mine.Enabled = !on && _project != null && _style != null;
            _notes.Enabled = !on;
            if (on) _head.Text = text;
        }
    }

    /// <summary>A large box for pasting notes, an old guide, or another tool's prompt (or loading a text file).</summary>
    internal static class NotesDialog
    {
        public static string Ask(IWin32Window owner)
        {
            using (var form = new Form { Text = "House Style — Notes for the style guide", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(680, 460), MinimumSize = new Size(480, 300), Font = SystemFonts.MessageBoxFont, AutoScaleMode = AutoScaleMode.Font })
            {
                var label = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 0),
                    Text = "Paste or dictate (Win+H) anything: how the team writes, words you never use, an old style guide, or the prompt you used with another AI tool. Or add files: Word, Excel, PowerPoint, OpenDocument, Rich Text, web pages, .txt or .md. Add as many as you like, all at once or one at a time." };
                var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, WordWrap = true };
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                var ok = new Button { Text = "Extract rules", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false, Font = new Font(form.Font, FontStyle.Bold) };
                var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
                var load = new Button { Text = "Add Files…", AutoSize = true };
                var hint = new Label { Text = "Ctrl+Enter extracts", AutoSize = true, Padding = new Padding(0, 6, 8, 0), ForeColor = SystemColors.GrayText };
                box.TextChanged += (s, e) => ok.Enabled = box.Text.Trim().Length > 0;
                // Enter stays a newline in the box (AcceptsReturn); the default button only fires from the other controls.
                box.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.Return && ok.Enabled) { e.Handled = e.SuppressKeyPress = true; ok.PerformClick(); } };
                load.Click += (s, e) =>
                {
                    using (var dlg = new OpenFileDialog { Multiselect = true, Filter = "Documents|*.docx;*.xlsx;*.pptx;*.odt;*.rtf;*.txt;*.md;*.htm;*.html;*.pdf|All files|*.*" })
                    {
                        if (dlg.ShowDialog(form) != DialogResult.OK) return;
                        var problems = new System.Collections.Generic.List<string>();
                        foreach (var file in dlg.FileNames)
                        {
                            try
                            {
                                var text = NoteText.ReadFile(file).Replace("\r\n", "\n").Replace("\n", "\r\n");   // a TextBox shows only CRLF as a line break
                                box.AppendText((box.TextLength > 0 ? "\r\n\r\n" : "") + "— " + Path.GetFileName(file) + " —\r\n" + text);   // each file under its own name, added to what is there
                            }
                            catch (Exception ex)
                            {
                                Diagnostics.Log("Build guide: load notes file", ex);
                                problems.Add(Path.GetFileName(file) + ": " + LoadProblem(ex));
                            }
                        }
                        if (problems.Count > 0) Notify.Warn(form, string.Join(Environment.NewLine + Environment.NewLine, problems.ToArray()), "Notes");
                    }
                };
                bar.Controls.AddRange(new Control[] { cancel, ok, load, hint });
                A11y.Name(box, "Notes", "Paste or type notes about how the team writes, or load a file. Control+Enter extracts rules.");
                A11y.Name(ok, "Extract rules", "Find style rules in these notes.");
                A11y.Name(bar, "Dialog buttons");
                form.Controls.Add(box); form.Controls.Add(bar); form.Controls.Add(label);
                form.AcceptButton = ok; form.CancelButton = cancel;
                return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }

        private static string LoadProblem(Exception ex)
        {
            if (ex is IOException && !(ex is FileNotFoundException) && !(ex is DirectoryNotFoundException) && !(ex is InvalidDataException))
            {
                var code = System.Runtime.InteropServices.Marshal.GetHRForException(ex) & 0xFFFF;
                if (code == 32 || code == 33) return "This file is open in another program (Word, for example) and can't be read. Close it there, or save a copy and load that.";
            }
            if (ex is UnauthorizedAccessException) return "House Style isn't allowed to read that file. Pick another file, or check its permissions.";
            return "House Style couldn't read that file. " + ex.Message;
        }
    }
}
