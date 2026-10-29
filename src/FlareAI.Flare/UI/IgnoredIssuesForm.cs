using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FlareAI.Core.Actions;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Ignored Issues": the issues the writer ignored that are still in the text, and Restore to see them in the list
    /// again. The list is the review session's own (<see cref="ReviewSession.Ignored"/>).
    /// </summary>
    internal sealed class IgnoredIssuesForm : Form
    {
        internal const string EmptyText = "You haven't ignored any issues that are still in the text.";
        internal const string NormalText = "These issues are hidden because you ignored them. Restore one to see it in the list again.";

        /// <summary>Shows that a restore couldn't be saved. Tests replace it so no dialog blocks them.</summary>
        internal static Action<IWin32Window, string, Exception, string> ReportFailure = Notify.Failed;

        private readonly ReviewSession _session;
        private readonly Label _head;
        private readonly ListView _list;
        private readonly Button _restore;

        public IgnoredIssuesForm(ReviewSession session)
        {
            _session = session;
            Text = Notify.Caption("Ignored Issues");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 300);
            MinimumSize = new Size(320, 220);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _head = new LiveLabel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0) };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true };
            _list.Columns.Add("Issue", 240);
            _list.Columns.Add("Where", 140);
            _list.SelectedIndexChanged += (s, e) => _restore.Enabled = _list.SelectedItems.Count > 0;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            _restore = new Button { Text = "&Restore", AutoSize = true, Enabled = false };
            _restore.Click += (s, e) => Safe.Run("Restore", RestoreSelected);
            bar.Controls.AddRange(new Control[] { close, _restore });
            Controls.Add(_list);
            Controls.Add(bar);
            Controls.Add(_head);
            CancelButton = close;
            A11y.Name(_list, "Ignored issues", "Columns are the issue and where it is.");
            A11y.Name(_restore, "Restore", "Show the selected issues in the list again.");
            A11y.Name(bar, "Dialog buttons");
            A11y.Apply(this);
            Shown += (s, e) => Reload(null);
        }

        private void Reload(string done)
        {
            _list.Items.Clear();
            foreach (var f in new List<StyleFinding>(_session.Ignored))
                _list.Items.Add(new ListViewItem(new[] { A11y.SeverityGlyph(f.Severity) + " " + FindingsPanel.ProposalText(f), Where(f) }) { Tag = f });
            _head.Text = (done != null ? done + "  " : "") + (_list.Items.Count == 0 ? EmptyText : NormalText);
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            _restore.Enabled = _list.SelectedItems.Count > 0;
        }

        private string Where(StyleFinding f)
        {
            if (_session.IsProjectReport) return f.DocumentPath == null ? "" : FindingsPanel.RelativeToContent(f.DocumentPath);
            return string.IsNullOrEmpty(f.Section) ? "(top of topic)" : f.Section;
        }

        private void RestoreSelected()
        {
            var picked = new List<StyleFinding>();
            foreach (ListViewItem item in _list.SelectedItems) picked.Add((StyleFinding)item.Tag);
            if (picked.Count == 0) return;
            var result = _session.Restore(picked);
            Reload(_session.Status);
            if (!result.Saved)
                ReportFailure(this, "save the restored issues", new IOException("The ignored-issues file could not be written."), "Ignored Issues");
        }
    }
}
