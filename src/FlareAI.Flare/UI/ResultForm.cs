using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
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
    /// Runs the action on a BackgroundWorker (net40-friendly), shows progress, then either the findings list
    /// (Check style) or the proposed XHTML with Apply/Cancel (Rewrite/Ask). Apply is only enabled when the
    /// validator passed; the DOM write happens on the UI thread after the dialog closes.
    /// </summary>
    public sealed class ResultForm : Form
    {
        private readonly ActionRunner _runner;
        private readonly IDocumentAdapter _doc;
        private readonly ActionKind _kind;
        private readonly string _instruction;
        private readonly string _title;

        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly TextBox _output;
        private readonly RichTextBox _diff;
        private readonly CheckBox _showXml;
        private readonly Button _apply, _close;
        private readonly BackgroundWorker _worker;
        private RewriteProposal _proposal;
        private readonly IList<XmlNode> _blocks;

        /// <summary>False when rewriting a style-guide topic (see <see cref="ActionRunner.Rewrite(IDocumentAdapter, IList{XmlNode}, string, bool)"/>).</summary>
        public bool Lint { get; set; } = true;

        /// <summary>The route for wording edits: "rewrite", or "draft" for owner edits to the style guide.</summary>
        public string Task { get; set; } = "rewrite";

        private bool _restructuring;
        private readonly Button _restructure;

        /// <summary>After OK: what was applied, for the caller's status line.</summary>
        public string AppliedSummary { get; private set; }

        public ResultForm(string title, ActionRunner runner, IDocumentAdapter doc, ActionKind kind, string instruction)
            : this(title, runner, doc, kind, instruction, null) { }

        /// <param name="blocks">Rewrite these blocks instead of the editor selection (used by the findings list).</param>
        public ResultForm(string title, ActionRunner runner, IDocumentAdapter doc, ActionKind kind, string instruction, IList<XmlNode> blocks)
        {
            _runner = runner; _doc = doc; _kind = kind; _instruction = instruction; _title = title; _blocks = blocks;

            Text = "House Style — " + title;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(500, 300);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _status = new LiveLabel { Left = 12, Top = 10, Width = 736, Height = 48, Text = "Contacting model…", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _progress = new ProgressBar { Left = 12, Top = 60, Width = 736, Height = 14, Style = ProgressBarStyle.Marquee, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _output = new TextBox { Left = 12, Top = 78, Width = 736, Height = 392, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Font = new Font(FontFamily.GenericMonospace, 9f), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            _diff = DiffView.Create();
            _diff.SetBounds(12, 78, 736, 392);
            _diff.Anchor = _output.Anchor;
            _diff.Visible = false;
            _showXml = new CheckBox { Text = "Show the markup", Left = 12, Top = 486, AutoSize = true, Visible = false, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            _showXml.CheckedChanged += (s, e) => { _output.Visible = _showXml.Checked; _diff.Visible = !_showXml.Checked; };
            _apply = new Button { Text = "Change", Left = 540, Top = 482, Width = 160, Enabled = false, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            new ToolTip().SetToolTip(_apply, "Make the change in the topic as a tracked change.");
            _close = new Button { Text = "Cancel", Left = 706, Top = 482, Width = 42, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            _close.Width = 60; _close.Left = 688;
            _restructure = new Button { Text = "Restructure Instead…", Left = 380, Top = 482, Width = 150, Visible = false, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            _restructure.Click += (s, e) =>
            {
                // The wording-only path said the task needs structural edits: ask for XHTML (validated) instead.
                _restructuring = true;
                _restructure.Visible = false; _apply.Enabled = false; _progress.Visible = true;
                _status.Text = "Asking for a structural edit…";
                _worker.RunWorkerAsync();
            };
            _apply.Click += (s, e) => OnApply();
            Controls.AddRange(new Control[] { _status, _progress, _diff, _output, _showXml, _restructure, _apply, _close });
            CancelButton = _close;
            A11y.Name(_output, "Suggested markup", "Read-only view of the suggested markup.");
            A11y.Name(_diff, "Suggested change", "Read-only view of the text with removed words struck through and added words underlined.");
            A11y.Name(_progress, "Progress");
            A11y.Apply(this);

            if (kind == ActionKind.Question)
            {
                // An answer is text to read. Nothing here can change the topic: no Change button, no markup view.
                Text = "House Style — Answer";
                _apply.Visible = false; _apply.Enabled = false;
                _restructure.Visible = false;
                _showXml.Visible = false;
                _output.Font = SystemFonts.MessageBoxFont;
                _close.Text = "Close";
            }

            _worker = new BackgroundWorker { WorkerSupportsCancellation = true };
            _worker.DoWork += Work;
            _worker.RunWorkerCompleted += Done;
            Shown += (s, e) => _worker.RunWorkerAsync();
            FormClosing += (s, e) => { if (_worker.IsBusy) _worker.CancelAsync(); };
        }

        private void Work(object sender, DoWorkEventArgs e)
        {
            var blocks = _blocks ?? _runner.PickBlocks(_doc);
            if (_kind == ActionKind.Question) { e.Result = _runner.Answer(_doc, blocks, _instruction); return; }
            e.Result = _restructuring ? _runner.Restructure(_doc, blocks, _instruction) : _runner.Rewrite(_doc, blocks, _instruction, Lint, Task);
        }

        private void Done(object sender, RunWorkerCompletedEventArgs e)
        {
            _progress.Visible = false;
            if (IsDisposed || _worker.CancellationPending) return; // user closed the dialog — never write afterwards

            if (e.Error != null)
            {
                Diagnostics.Log(_title, e.Error); ErrorReporter.Background(this, "House Style couldn't finish \"" + _title + "\".", e.Error);
                _status.Text = "Failed.";
                _output.Text = e.Error is LlmException || e.Error is InvalidOperationException ? e.Error.Message : e.Error.ToString();
                _close.Text = "Close";
                return;
            }

            if (_kind == ActionKind.Question)
            {
                _status.Text = "Answer";
                _output.Text = (string)e.Result;
                _output.Visible = true; _diff.Visible = false;
                _close.Text = "Close";
                return;
            }

            _proposal = (RewriteProposal)e.Result;
            if (_proposal.Validation.IsValid)
            {
                var notes = new List<string>(_proposal.Validation.Warnings);
                notes.AddRange(_proposal.Notes);
                _status.Text = (_proposal.RemainingLint.Count == 0 ? "The suggested change passes the rules." : "The suggested change still has " + Plural.Of(_proposal.RemainingLint.Count, "issue") + " (listed below).")
                               + (notes.Count > 0 ? "  " + string.Join("  ", notes.ToArray()) : "")
                               + (_proposal.WordPlan != null ? "  It's made as " + Plural.Of(_proposal.WordPlan.Count, "word-level tracked change") + "." : "  It's made as one tracked change to the paragraph, because the structure changes.");
                // A paragraph-level change Flare can't take safely (a variable inside, a different element) is refused below (ApplyBlocked).
                var sb = new StringBuilder(_proposal.ProposedXml);
                if (_proposal.RemainingLint.Count > 0)
                    sb.AppendLine().AppendLine().AppendLine("--- issues the rules still find ---").Append(ActionRunner.Summarize(_proposal.RemainingLint));
                _output.Text = sb.ToString();
                // What the author reads: the text with word changes marked; the XHTML is one click away.
                var diff = ReadableDiff.Of(_proposal.View ?? _proposal.OriginalBlocks, _proposal.ProposedBlocks);
                DiffView.Render(_diff, diff, true);
                if (_proposal.RemainingLint.Count > 0)
                    _diff.AppendText("\nIssues the rules still find:\n" + ActionRunner.Summarize(_proposal.RemainingLint));
                _output.Visible = false; _diff.Visible = true; _showXml.Visible = true;
                _apply.Enabled = true;
                if (_proposal.NeedsRestructure)
                {
                    _restructure.Visible = true;
                    _status.Text += "  The AI says this also needs a structural change" + (string.IsNullOrEmpty(_proposal.RestructureNote) ? "." : " (" + _proposal.RestructureNote.TrimEnd('.') + ").") + " Change the wording now, or click Restructure Instead.";
                }
                else if (!string.IsNullOrEmpty(_proposal.RestructureNote))
                    _status.Text += "  Note from the AI: " + _proposal.RestructureNote;   // e.g. a fact only the author can supply
                if (_proposal.ApplyBlocked != null)
                {
                    _apply.Enabled = false;   // the wording pass deleted what the task means to move
                    _status.Text = _proposal.ApplyBlocked;
                }
                if (_proposal.WordPlan != null && _proposal.WordPlan.Count == 0)
                {
                    _status.Text = _proposal.NeedsRestructure ? "No wording changes. The AI says this needs a structural change" + (string.IsNullOrEmpty(_proposal.RestructureNote) ? "." : " (" + _proposal.RestructureNote.TrimEnd('.') + ").")
                                 : !string.IsNullOrEmpty(_proposal.RestructureNote) ? "No wording changes. Note from the AI: " + _proposal.RestructureNote
                                 : "This text already follows the style guide.";
                    _apply.Enabled = false;
                    _close.Text = "Close";
                }
            }
            else
            {
                _status.Text = "The AI's answer would damage the topic's structure, so nothing was changed.";
                _output.Text = _proposal.Validation + Environment.NewLine + Environment.NewLine + "--- raw response ---" + Environment.NewLine + (_proposal.Raw == null ? "" : _proposal.Raw.Text);
                _close.Text = "Close";
            }
        }

        private void OnApply()
        {
            try
            {
                AppliedSummary = _runner.Apply(_doc, _proposal, "House Style: " + _title);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Apply", ex);
                Notify.Failed(this, "make the change", ex);
            }
        }
    }
}
