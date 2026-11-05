using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using FlareAI.Core;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Llm;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// Insert at the cursor: asks the model for new text, shows it with what to check, and adds it to the topic as one tracked change
    /// only when the writer clicks Insert. The draft runs on a BackgroundWorker; the write happens on the UI thread.
    /// </summary>
    public sealed class InsertForm : Form
    {
        private readonly ActionRunner _runner;
        private readonly IDocumentAdapter _doc;
        private readonly XmlNode _anchor;
        private readonly string _instruction;
        private readonly string _title;

        private readonly LiveLabel _status;
        private readonly ProgressBar _progress;
        private readonly TextBox _where;
        private readonly TextBox _preview;
        private readonly TextBox _checks;
        private readonly Button _insert, _close;
        private readonly BackgroundWorker _worker;
        private DraftProposal _proposal;

        /// <summary>After OK: what was inserted, for the caller's status line.</summary>
        public string AppliedSummary { get; private set; }

        public InsertForm(string title, ActionRunner runner, IDocumentAdapter doc, XmlNode anchor, string instruction)
        {
            _runner = runner; _doc = doc; _anchor = anchor; _instruction = instruction; _title = title;

            Text = Notify.Caption(title);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 500);
            MinimumSize = new Size(360, 320);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            var anchorText = System.Text.RegularExpressions.Regex.Replace(anchor.InnerText, @"\s+", " ").Trim();
            if (anchorText.Length > 60) anchorText = anchorText.Substring(0, 57) + "…";

            _status = new LiveLabel { Left = 12, Top = 10, Width = 616, Height = 22, Text = "Writing the new text…", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _progress = new ProgressBar { Left = 12, Top = 34, Width = 616, Height = 14, Style = ProgressBarStyle.Marquee, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _where = new TextBox { Left = 12, Top = 54, Width = 616, Height = 22, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = false,
                                   Text = "The new text goes after \"" + anchorText + "\".", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _preview = new TextBox { Left = 12, Top = 80, Width = 616, Height = 250, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            _checks = new TextBox { Left = 12, Top = 338, Width = 616, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Visible = false, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            _insert = new Button { Text = "Insert", Left = 478, Top = 456, Width = 90, Enabled = false, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            _close = new Button { Text = "Cancel", Left = 574, Top = 456, Width = 54, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            new ToolTip().SetToolTip(_insert, "Add the new text to the topic as a tracked change.");
            _insert.Click += (s, e) => OnInsert();
            Controls.AddRange(new Control[] { _status, _progress, _where, _preview, _checks, _insert, _close });
            CancelButton = _close;
            A11y.Name(_where, "Where the new text goes");
            A11y.Name(_preview, "New text", "Read-only view of the new text.");
            A11y.Name(_checks, "Things to check", "What to look at before you insert the new text.");
            A11y.Name(_insert, "Insert", "Add the new text to the topic as a tracked change.");
            A11y.Name(_progress, "Progress");
            A11y.Apply(this);

            _worker = new BackgroundWorker { WorkerSupportsCancellation = true };
            _worker.DoWork += (s, e) => { e.Result = _runner.Draft(_doc, _anchor, _instruction); };
            _worker.RunWorkerCompleted += Done;
            Shown += (s, e) => _worker.RunWorkerAsync();
            FormClosing += (s, e) => { if (_worker.IsBusy) _worker.CancelAsync(); };
        }

        private void Done(object sender, RunWorkerCompletedEventArgs e)
        {
            _progress.Visible = false;
            if (IsDisposed || _worker.CancellationPending) return;   // closed meanwhile: never write afterwards
            if (e.Error != null)
            {
                Diagnostics.Log(_title, e.Error);
                ErrorReporter.Background(this, "House Style couldn't finish \"" + _title + "\".", e.Error);
                _status.Text = "Failed.";
                _preview.Text = e.Error is LlmException || e.Error is InvalidOperationException ? e.Error.Message : e.Error.ToString();
                _close.Text = "Close";
                return;
            }
            _proposal = (DraftProposal)e.Result;
            if (!_proposal.Validation.IsValid)
            {
                _status.Text = "Nothing was changed.";
                _preview.Text = string.Join(Environment.NewLine, _proposal.Validation.Errors.ToArray());
                _close.Text = "Close";
                return;
            }
            _status.Text = "Check the new text, then select Insert.";
            _preview.Text = Render(_proposal);
            var checks = Checks(_proposal);
            if (checks.Length > 0) { _checks.Text = checks; _checks.Visible = true; _preview.Height = 250; }
            else _preview.Height = 366;
            _insert.Enabled = true;
            _insert.Focus();
        }

        /// <summary>The new blocks as plain text: paragraphs on their own lines, list items with a bullet or number.</summary>
        internal static string Render(DraftProposal p)
        {
            var sb = new StringBuilder();
            foreach (var b in p.ProposedBlocks.Where(n => n.NodeType == XmlNodeType.Element))
            {
                if (b.LocalName == "p") sb.AppendLine(Flat(b.InnerText)).AppendLine();
                else
                {
                    var i = 0;
                    foreach (var li in b.ChildNodes.Cast<XmlNode>().Where(n => n.LocalName == "li"))
                        sb.AppendLine((b.LocalName == "ol" ? (++i) + ".  " : "•  ") + Flat(li.InnerText));
                    sb.AppendLine();
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>What to look at before inserting: gaps, new facts, what the topic doesn't say, and style issues left in.</summary>
        internal static string Checks(DraftProposal p)
        {
            var lines = new System.Collections.Generic.List<string>();
            if (p.Gaps > 0) lines.Add("The draft has " + Plural.Of(p.Gaps, "place") + " to fill in. Look for Add details here.");
            if (p.NewTerms != null && p.NewTerms.Count > 0) lines.Add("Check these words. They aren't in the topic or your instruction: " + string.Join(", ", p.NewTerms.ToArray()) + ".");
            if (!string.IsNullOrEmpty(p.Missing)) lines.Add(p.Missing.Trim());
            if (p.RemainingLint != null && p.RemainingLint.Count > 0) lines.Add(Plural.Of(p.RemainingLint.Count, "style issue") + " will still show in House Style Issues after you insert.");
            if (p.Notes != null) lines.AddRange(p.Notes);
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private static string Flat(string s) { return System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim(); }

        private void OnInsert()
        {
            if (_proposal == null) return;
            try
            {
                AppliedSummary = _runner.ApplyInsert(_doc, _proposal, _title);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(_title, ex);
                _status.Text = "Nothing was changed.";
                _checks.Text = ex.Message; _checks.Visible = true;
                _insert.Enabled = false;
                _close.Text = "Close";
            }
        }
    }
}
