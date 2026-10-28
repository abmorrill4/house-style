using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Actions;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Exemptions" (from a finding's Exempt action): the text this project exempts from a rule, while the rule stays on
    /// everywhere else. Remove Exemption lets the rule flag that text again. A stale row says its text or topic is gone.
    /// </summary>
    internal sealed class ExemptionsForm : Form
    {
        private readonly string _root;
        private readonly Func<string, string> _ruleTitle;
        private readonly Label _head;
        private readonly ListView _list;
        private readonly Button _remove;

        public ExemptionsForm(string projectRoot, Func<string, string> ruleTitle)
        {
            _root = projectRoot;
            _ruleTitle = ruleTitle;
            Text = Notify.Caption("Exemptions");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 380);
            MinimumSize = new Size(480, 240);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _head = new LiveLabel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0) };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            _list.Columns.Add("Topic", 130);
            _list.Columns.Add("Rule", 140);
            _list.Columns.Add("Text", 160);
            _list.Columns.Add("Reason", 130);
            _list.Columns.Add("Who", 80);
            _list.Columns.Add("When", 80);
            _list.SelectedIndexChanged += (s, e) => _remove.Enabled = _list.SelectedItems.Count > 0;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            _remove = new Button { Text = "Remove Exemption", AutoSize = true, Enabled = false };
            _remove.Click += (s, e) => Safe.Run("Remove exemption", RemoveSelected);
            bar.Controls.AddRange(new Control[] { close, _remove });
            Controls.Add(_list);
            Controls.Add(bar);
            Controls.Add(_head);
            CancelButton = close;
            A11y.Name(_list, "Exempted text");
            A11y.Name(_remove, "Remove Exemption", "The rule flags this text again the next time you lint.");
            A11y.Name(bar, "Dialog buttons");
            A11y.Apply(this);
            Shown += (s, e) => Reload(null);
        }

        /// <summary>The exemptions shown, one row each.</summary>
        public int Count { get { return _list.Items.Count; } }

        private void Reload(string done)
        {
            _list.Items.Clear();
            var entries = Exemptions.Load(_root).Entries;
            var stale = Exemptions.Stale(_root, entries);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                _list.Items.Add(new ListViewItem(new[] { e.Topic, RuleTitle(e.Rule), TextCell(e, stale[i]), e.Reason, e.Who, e.When }) { Tag = e });
            }
            _head.Text = (done != null ? done + "  " : "") + (entries.Count == 0
                ? "No text is exempted in this project."
                : "This text is exempted from its rule. The rule stays on everywhere else in the project.");
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            _remove.Enabled = _list.SelectedItems.Count > 0;
        }

        /// <summary>The rule's title, or its key when the rule has no title.</summary>
        private string RuleTitle(string key)
        {
            var title = _ruleTitle == null ? null : _ruleTitle(key);
            return string.IsNullOrEmpty(title) ? key : title;
        }

        private static string TextCell(ExemptionEntry e, StaleState state)
        {
            switch (state)
            {
                case StaleState.TextNotFound: return e.Text + " (Text not found)";
                case StaleState.TopicNotFound: return e.Text + " (Topic not found)";
                default: return e.Text;
            }
        }

        private void RemoveSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var entry = (ExemptionEntry)_list.SelectedItems[0].Tag;
            try { Exemptions.Remove(_root, entry); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Diagnostics.Log("remove exemption", ex);
                Notify.Failed(this, "remove this exemption", ex, "Exemptions");
                return;
            }
            Reload("Removed. It shows again the next time you lint.");
        }
    }
}
