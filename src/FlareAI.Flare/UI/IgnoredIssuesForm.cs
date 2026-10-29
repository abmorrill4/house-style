using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Ignored Issues" (from the review pane): the issues the writer ignored that are still in the text, and Restore.
    /// The list is the session's (<see cref="ReviewSession.Ignored"/>); a restored issue goes back into the findings list.
    /// The dialog stays open after Restore so several can be restored one after another.
    /// </summary>
    internal sealed class IgnoredIssuesForm : Form
    {
        private const string Intro = "These issues are hidden because you ignored them. Restore one to see it in the list again.";
        private const string Empty = "You haven't ignored any issues that are still in the text.";

        /// <summary>Tells the writer a restore couldn't be saved. A seam so tests don't open a modal box.</summary>
        internal static Action<IWin32Window> ShowSaveFailed = owner =>
            Notify.PlainError(owner, "House Style couldn't save the restore.",
                              "The issues are back in the list for now, but they may be hidden again the next time you check the text. Try again. If it keeps happening, check that the House Style settings folder can be written to.",
                              null, "Ignored Issues");

        private readonly ReviewSession _session;
        private readonly Label _head;
        private readonly ListView _list;
        private readonly Button _restore;
        private readonly Action _changed;
        private string _done;

        public IgnoredIssuesForm(ReviewSession session)
        {
            _session = session;
            Text = Notify.Caption("Ignored Issues");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(420, 300);
            MinimumSize = new Size(320, 220);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _head = new LiveLabel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0) };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, MultiSelect = true, FullRowSelect = true, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
            _list.Columns.Add("Issue", 240);
            _list.Columns.Add("Where", 140);
            _list.SelectedIndexChanged += (s, e) => _restore.Enabled = _list.SelectedItems.Count > 0;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            _restore = new Button { Text = "&Restore", AutoSize = true, Enabled = false };
            _restore.Click += (s, e) => Safe.Run("Restore ignored issues", RestoreSelected);
            bar.Controls.AddRange(new Control[] { close, _restore });
            Controls.Add(_list);
            Controls.Add(bar);
            Controls.Add(_head);
            CancelButton = close;
            A11y.Name(_list, "Ignored issues", "The issues you ignored. Select one or more, then choose Restore.");
            A11y.Name(_restore, "Restore", "Shows the selected issues in the list again.");
            A11y.Name(close, "Close", "Closes this window.");
            A11y.Name(bar, "Dialog buttons");
            A11y.Apply(this);

            _changed = () => { if (!IsDisposed) Reload(); };
            _session.IgnoredChanged += _changed;
            Shown += (s, e) => { Reload(); _list.Focus(); };
            Reload();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _session.IgnoredChanged -= _changed;
            base.Dispose(disposing);
        }

        private void Reload()
        {
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var f in new List<StyleFinding>(_session.Ignored))
                    _list.Items.Add(new ListViewItem(new[] { IssueText(f), Where(f) }) { Tag = f });
            }
            finally { _list.EndUpdate(); }
            _head.Text = (_done != null ? _done + "  " : "") + (_list.Items.Count == 0 ? Empty : Intro);
            if (_list.Items.Count > 0 && _list.SelectedItems.Count == 0) _list.Items[0].Focused = true;
            _restore.Enabled = _list.SelectedItems.Count > 0;
        }

        private void RestoreSelected()
        {
            var picked = new List<StyleFinding>();
            foreach (ListViewItem item in _list.SelectedItems)
            {
                var f = item.Tag as StyleFinding;
                if (f != null) picked.Add(f);
            }
            if (picked.Count == 0) return;
            var result = _session.Restore(picked);
            _done = !result.Saved ? ReviewSession.SaveFailedText
                  : result.Restored == 1 ? ReviewSession.RestoredOneText
                  : ReviewSession.RestoredManyText(result.Restored);
            Reload();
            if (!result.Saved) ShowSaveFailed(this);
        }

        /// <summary>The issue as the findings list words it.</summary>
        private static string IssueText(StyleFinding f)
        {
            var text = string.IsNullOrEmpty(f.Excerpt) ? (f.Suggestion ?? "") : f.Excerpt;
            switch (f.Kind)
            {
                case "variable": text = "Use variable " + f.Variable + " for \"" + f.Excerpt + "\""; break;
                case "restructure": text = "Rewrite \"" + ReviewSession.Shorten(f.Excerpt, 48) + "\""; break;
                case "manual": text = FlareAI.Core.Style.RuleWording.FromName(f.Rule) + " in \"" + ReviewSession.Shorten(f.Excerpt, 36) + "\""; break;
                default: if (!string.IsNullOrEmpty(f.Excerpt)) text = ChangeText.Row(f.Excerpt, f.Replacement); break;
            }
            return A11y.SeverityGlyph(f.Severity) + " " + text;
        }

        /// <summary>The file in a project report, otherwise the section of the topic.</summary>
        private string Where(StyleFinding f)
        {
            if (_session.IsProjectReport && !string.IsNullOrEmpty(f.DocumentPath)) return Path.GetFileName(f.DocumentPath);
            return string.IsNullOrEmpty(f.Section) ? "(top of topic)" : f.Section;
        }
    }
}
