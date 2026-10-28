using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Style;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Exemptions" (from Tune style guide or a Lint report): every text the project exempts from a rule, with the rule,
    /// the reason, who and when, and Remove Exemption. A row whose text or topic has changed says so ("Text not found",
    /// "Topic not found"); the rule still checks that topic. The list is the project's own (<see cref="Exemptions"/>).
    /// </summary>
    internal sealed class ExemptionsForm : Form
    {
        private readonly string _anyPath;
        private readonly Func<string, string> _ruleTitle;
        private readonly LiveLabel _head;
        private readonly ListView _list;
        private readonly Button _remove;

        /// <param name="ruleTitle">The rule's title for its id (the Issues pane's name for it); the id itself when there is none.</param>
        public ExemptionsForm(string anyPath, Func<string, string> ruleTitle)
        {
            _anyPath = anyPath;
            _ruleTitle = ruleTitle;
            Text = Notify.Caption("Exemptions");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 440);
            MinimumSize = new Size(560, 300);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _head = new LiveLabel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0) };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            foreach (var c in new[] { "Topic", "Rule", "Text", "Reason", "Who", "When" }) _list.Columns.Add(c);
            _list.Columns[0].Width = 200; _list.Columns[1].Width = 180; _list.Columns[2].Width = 180;
            _list.Columns[3].Width = 200; _list.Columns[4].Width = 100; _list.Columns[5].Width = 80;
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
            A11y.Name(_list, "Exempted text", "Columns are topic, rule, text, reason, who and when. Text not found means the text is no longer in its topic.");
            A11y.Name(_remove, "Remove Exemption", "The text shows as a finding again the next time you lint.");
            A11y.Name(bar, "Dialog buttons");
            A11y.Apply(this);
            Shown += (s, e) => Reload(null);
        }

        private Exemption Current() { return _list.SelectedItems.Count == 1 ? (Exemption)_list.SelectedItems[0].Tag : null; }

        private void UpdateButton() { _remove.Enabled = Current() != null; }

        private string Title(string rule)
        {
            var title = _ruleTitle == null ? null : _ruleTitle(rule);
            return string.IsNullOrEmpty(title) ? rule : title;
        }

        /// <summary>Reads the exemptions and their stale notes once: each topic is read once, when the list opens or reloads.</summary>
        private void Reload(string done)
        {
            _list.Items.Clear();
            IList<Exemption> all;
            Dictionary<Exemption, string> stale;
            try
            {
                all = Exemptions.Load(_anyPath);
                stale = Exemptions.StaleNotes(_anyPath, all);
            }
            catch (Exception ex) { Diagnostics.Log("exemptions", ex); _head.Text = "Could not read the exemptions: " + ex.Message; return; }
            foreach (var e in all)
            {
                string note;
                var text = stale.TryGetValue(e, out note) ? e.Text + " (" + note + ")" : e.Text;
                _list.Items.Add(new ListViewItem(new[] { e.Topic, Title(e.Rule), text, e.Reason, e.Who, e.When }) { Tag = e });
            }
            _head.Text = (done != null ? done + "  " : "") + (all.Count == 0
                ? "No text is exempted from a rule in this project."
                : Plural.Of(all.Count, "exemption") + ". The rule still checks the rest of each topic.");
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            UpdateButton();
        }

        private void RemoveSelected()
        {
            var e = Current();
            if (e == null) return;
            try { Exemptions.Remove(_anyPath, e); }
            catch (GuideWriteException ex) { Diagnostics.Log("remove exemption", ex); Notify.Failed(this, "remove the exemption for " + e.Text, ex, "Exemptions"); return; }
            Reload("Removed. It shows again the next time you lint.");
        }
    }
}
