using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Actions;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Exemptions" (from a Lint Project report): every place a rule is exempted in this project, with who, when and why,
    /// and Remove Exemption. A row whose text or topic has changed says so, so the writer can see why it no longer applies.
    /// The list is the project's own (<see cref="Exemptions"/>), shared by everyone on the project.
    /// </summary>
    internal sealed class ExemptionsForm : Form
    {
        /// <summary>How many distinct topics the stale check reads when the form opens; rows in later topics get no note.</summary>
        private const int MaxTopicChecks = 200;

        private readonly string _root;
        private readonly Func<string, string> _titleFor;
        private readonly List<ExemptionEntry> _entries = new List<ExemptionEntry>();
        private readonly List<string> _notes = new List<string>();
        private readonly Label _head;
        private readonly ListView _list;
        private readonly Button _remove;

        public ExemptionsForm(string projectRoot, string contentFolder, Func<string, string> titleFor)
        {
            _root = projectRoot;
            _titleFor = titleFor;
            Text = Notify.Caption("Exemptions");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 340);
            MinimumSize = new Size(560, 240);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _head = new LiveLabel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0) };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            foreach (var c in new[] { "Topic", "Rule", "Text", "Reason", "Who", "When" }) _list.Columns.Add(c);
            _list.Columns[0].Width = 200; _list.Columns[1].Width = 200; _list.Columns[2].Width = 200;
            _list.Columns[3].Width = 160; _list.Columns[4].Width = 100; _list.Columns[5].Width = 80;
            _list.SelectedIndexChanged += (s, e) => UpdateButton();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            _remove = new Button { Text = "Remove Exemption", AutoSize = true, Enabled = false };
            _remove.Click += (s, e) => Safe.Run("Remove exemption", RemoveSelected);
            bar.Controls.AddRange(new Control[] { close, _remove });
            Controls.Add(_list);
            Controls.Add(bar);
            Controls.Add(_head);
            CancelButton = close;
            A11y.Name(_list, "Exempted text", "Columns are topic, rule, text, reason, who and when.");
            A11y.Name(_remove, "Remove Exemption", "Lint shows this text again.");
            A11y.Name(bar, "Dialog buttons");
            A11y.Apply(this);
            Load += (s, e) => LoadRows();
        }

        /// <summary>Reads the file and checks each row's text once, when the form opens.</summary>
        private void LoadRows()
        {
            _entries.Clear();
            _notes.Clear();
            try { _entries.AddRange(Exemptions.Load(_root).Entries); }
            catch (Exception ex) { Diagnostics.Log("exemptions", ex); _head.Text = "Could not read the exemptions: " + ex.Message; return; }
            var checkedTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _entries) _notes.Add(NoteFor(e, checkedTopics));
            ShowRows(null);
        }

        /// <summary>The stale note for a row, or null. Only the first <see cref="MaxTopicChecks"/> distinct topics are read.</summary>
        private string NoteFor(ExemptionEntry e, HashSet<string> checkedTopics)
        {
            if (!checkedTopics.Contains(e.Topic))
            {
                if (checkedTopics.Count >= MaxTopicChecks) return null;
                checkedTopics.Add(e.Topic);
            }
            return Exemptions.IsStale(_root, e);
        }

        private void ShowRows(string done)
        {
            _list.Items.Clear();
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var text = _notes[i] == null ? e.Text : e.Text + " (" + _notes[i] + ")";
                _list.Items.Add(new ListViewItem(new[] { e.Topic, TitleOf(e.Rule), text, e.Reason, e.Who, e.When }) { Tag = e });
            }
            _head.Text = done ?? (_entries.Count == 0
                ? "No text is exempt from a rule in this project."
                : "Each place here is exempt from one rule. The rule still applies everywhere else.");
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            UpdateButton();
        }

        /// <summary>The rule's title as the pane and Disabled Rules show it; the key itself when nothing is found.</summary>
        private string TitleOf(string rule)
        {
            var title = _titleFor == null ? null : _titleFor(rule);
            return string.IsNullOrEmpty(title) ? rule : title;
        }

        private void UpdateButton()
        {
            _remove.Enabled = _list.SelectedItems.Count == 1;
        }

        private void RemoveSelected()
        {
            if (_list.SelectedItems.Count != 1) return;
            var entry = (ExemptionEntry)_list.SelectedItems[0].Tag;
            var error = Exemptions.Remove(_root, entry);
            if (error != null)
            {
                var ex = new IOException(error);
                Diagnostics.Log("remove exemption", ex);
                Notify.Failed(this, "remove this exemption", ex, "Exemptions");
                return;
            }
            var i = _entries.IndexOf(entry);
            if (i >= 0) { _entries.RemoveAt(i); _notes.RemoveAt(i); }
            ShowRows("Removed. It shows again the next time you lint.");
        }
    }
}
