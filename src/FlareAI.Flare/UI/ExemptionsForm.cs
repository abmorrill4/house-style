using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Exemptions" (from a Lint Project report): the text this project exempts from one rule in one topic, with who, when
    /// and why, and Remove Exemption. Each row is one finding hidden; the rule stays on everywhere else. A row whose text
    /// or topic is gone says so in its Text cell. Nothing here writes the topics.
    /// </summary>
    internal sealed class ExemptionsForm : Form
    {
        private readonly string _anyPath;
        private readonly Func<string, string> _ruleTitle;
        private readonly Label _head;
        private readonly ListView _list;
        private readonly Button _remove;

        public ExemptionsForm(string anyProjectPath, Func<string, string> ruleTitle)
        {
            _anyPath = anyProjectPath;
            _ruleTitle = ruleTitle;
            Text = Notify.Caption("Exemptions");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 360);
            MinimumSize = new Size(520, 240);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _head = new LiveLabel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0) };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            foreach (var c in new[] { "Topic", "Rule", "Text", "Reason", "Who", "When" }) _list.Columns.Add(c);
            _list.Columns[0].Width = 150; _list.Columns[1].Width = 180; _list.Columns[2].Width = 200;
            _list.Columns[3].Width = 140; _list.Columns[4].Width = 90; _list.Columns[5].Width = 80;
            _list.SelectedIndexChanged += (s, e) => _remove.Enabled = Current() != null;
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
            Shown += (s, e) => Reload(null);
        }

        /// <summary>The list as shown: one row per exemption, in file order. Tests read the cells from here.</summary>
        internal ListView Rows { get { return _list; } }

        /// <summary>The header line under the title.</summary>
        internal string Header { get { return _head.Text; } }

        internal Button RemoveButton { get { return _remove; } }

        /// <summary>The stale note of each row, in row order (None when the text is still in its topic).</summary>
        internal IList<StaleNote> RowNotes
        {
            get
            {
                var notes = new List<StaleNote>();
                foreach (ListViewItem item in _list.Items) notes.Add(((Row)item.Tag).Note);
                return notes;
            }
        }

        private Row Current() { return _list.SelectedItems.Count == 1 ? (Row)_list.SelectedItems[0].Tag : null; }

        private void Reload(string done)
        {
            _list.Items.Clear();
            var stale = 0;
            try
            {
                var entries = Exemptions.Load(_anyPath).Entries;
                var notes = Exemptions.StaleNotes(StyleEngine.FlareProjectRoot(_anyPath), entries);
                for (var i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    var note = notes[i];
                    if (note != StaleNote.None) stale++;
                    var text = note == StaleNote.None ? e.Text : e.Text + "  (" + (note == StaleNote.TopicNotFound ? "Topic not found" : "Text not found") + ")";
                    var item = new ListViewItem(new[] { e.Topic, _ruleTitle(e.Rule) ?? e.Rule, text, e.Reason, e.Who, e.When }) { Tag = new Row { Entry = e, Note = note } };
                    _list.Items.Add(item);
                }
            }
            catch (Exception ex) { Diagnostics.Log("exemptions", ex); _head.Text = "Could not read the exemptions: " + ex.Message; return; }
            _head.Text = (done != null ? done + "  " : "") + (_list.Items.Count == 0
                ? "This project has no exemptions. Lint Project reports every finding."
                : "Each exemption hides one finding in one topic. The rule stays on everywhere else.") +
                (stale == 0 ? "" : "  Some exemptions no longer find their text. Check the Text column.");
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            _remove.Enabled = Current() != null;
        }

        internal void RemoveSelected()
        {
            var row = Current();
            if (row == null) return;
            try { Exemptions.Remove(_anyPath, row.Entry); }
            catch (Exception ex) when (ex is GuideWriteException || ex is IOException)
            {
                Diagnostics.Log("remove exemption", ex);
                Notify.Failed(this, "remove this exemption", ex, "Exemptions");
                return;
            }
            Reload("Removed. It shows again the next time you lint.");
        }

        private sealed class Row
        {
            public Exemptions.Entry Entry;
            public StaleNote Note;
        }
    }
}
