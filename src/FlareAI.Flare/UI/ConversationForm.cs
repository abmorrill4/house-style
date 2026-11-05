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
    /// Ask AI as a conversation on the selected text. The writer sends a message, the AI replies with an answer or a proposed change, and
    /// any proposed change, an earlier one included, can be applied as one tracked change. The window is modal, so only House Style's own
    /// Apply changes the topic while it is open. Closing it ends the conversation.
    /// </summary>
    public sealed class ConversationForm : Form
    {
        private readonly ActionRunner _runner;
        private readonly IDocumentAdapter _doc;
        private readonly Conversation _conv;
        private readonly string _title;

        private readonly TextBox _quote, _transcript, _message;
        private readonly ComboBox _replies;
        private readonly Button _send, _apply, _close;
        private readonly Label _status, _cost;
        private readonly BackgroundWorker _worker;
        private string _pending;
        private readonly string _first;

        /// <param name="firstMessage">Sent as soon as the window opens (what the writer typed in Ask AI), or null.</param>
        public ConversationForm(string title, ActionRunner runner, IDocumentAdapter doc, System.Collections.Generic.IList<XmlNode> blocks, string firstMessage = null)
        {
            _first = firstMessage;
            _runner = runner; _doc = doc; _title = title;
            _conv = new Conversation(blocks);

            Text = Notify.Caption(title);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 540);
            MinimumSize = new Size(340, 420);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            var all = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            var topWide = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var bottomWide = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            var text = string.Join(" ", AcceptedView.Of(blocks).Select(b => System.Text.RegularExpressions.Regex.Replace(b.InnerText, @"\s+", " ").Trim()).ToArray());
            if (text.Length > 140) text = text.Substring(0, 137) + "…";
            _quote = new TextBox { Left = 12, Top = 10, Width = 536, Height = 22, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = false, Text = "About: " + text, Anchor = topWide };
            _transcript = new TextBox { Left = 12, Top = 38, Width = 536, Height = 250, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = SystemColors.Window, Anchor = all };
            var ask = new Label { Text = "Ask about this text or describe a change", Left = 12, Top = 296, Width = 536, Height = 18, Anchor = bottomWide };
            _message = new TextBox { Left = 12, Top = 316, Width = 536, Height = 64, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Anchor = bottomWide };
            _send = new Button { Text = "Send", Left = 478, Top = 386, Width = 70, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var applyLabel = new Label { Text = "Reply to apply", Left = 12, Top = 420, Width = 536, Height = 18, Anchor = bottomWide };
            _replies = new ComboBox { Left = 12, Top = 440, Width = 400, DropDownStyle = ComboBoxStyle.DropDownList, Anchor = bottomWide };
            _apply = new Button { Text = "Apply", Left = 418, Top = 438, Width = 130, Enabled = false, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            _status = new LiveLabel { Left = 12, Top = 472, Width = 536, Height = 32, Anchor = bottomWide };
            _cost = new Label { Left = 12, Top = 508, Width = 440, Height = 22, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            _close = new Button { Text = "Close", Left = 478, Top = 506, Width = 70, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

            new ToolTip().SetToolTip(_apply, "Make the change in the topic as a tracked change.");
            _send.Click += (s, e) => Send();
            _apply.Click += (s, e) => Apply();
            _replies.SelectedIndexChanged += (s, e) => _apply.Enabled = _replies.SelectedIndex >= 0 && !_worker.IsBusy;
            _message.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && _send.Enabled) { e.SuppressKeyPress = true; Send(); }
            };
            Controls.AddRange(new Control[] { _quote, _transcript, ask, _message, _send, applyLabel, _replies, _apply, _status, _cost, _close });
            CancelButton = _close;
            A11y.Name(_quote, "About", "The text this conversation is about.");
            A11y.Name(_transcript, "Conversation", "What you asked and how the AI replied.");
            A11y.Name(_message, "Message", "Ask about this text or describe a change. Press Enter to send, or Shift+Enter for a new line.");
            A11y.Name(_send, "Send", "Sends your message.");
            A11y.Name(_replies, "Reply to apply", "Choose which proposed change to apply. Any earlier one works.");
            A11y.Name(_apply, "Apply", "Make the chosen change in the topic as a tracked change.");
            A11y.Apply(this);

            _worker = new BackgroundWorker { WorkerSupportsCancellation = true };
            _worker.DoWork += (s, e) => { e.Result = _runner.Converse(_doc, _conv, _pending); };
            _worker.RunWorkerCompleted += Done;
            FormClosing += (s, e) => { if (_worker.IsBusy) _worker.CancelAsync(); };
            Shown += (s, e) =>
            {
                _message.Focus();
                if (!string.IsNullOrEmpty(_first)) SendMessage(_first);
            };
        }

        /// <summary>For tests: types a message and sends it.</summary>
        internal void SendMessage(string message) { _message.Text = message; Send(); }

        private void Send()
        {
            var message = _message.Text.Trim();
            if (message.Length == 0 || _worker.IsBusy) return;
            _pending = message;
            _send.Enabled = false; _apply.Enabled = false; _message.Enabled = false;
            _status.Text = "Waiting for the reply…";
            _worker.RunWorkerAsync();
        }

        private void Done(object sender, RunWorkerCompletedEventArgs e)
        {
            if (IsDisposed || _worker.CancellationPending) return;   // closed meanwhile: the turn is dropped
            _send.Enabled = true; _message.Enabled = true;
            if (e.Error != null)
            {
                Diagnostics.Log(_title, e.Error);
                var friendly = e.Error is LlmException || e.Error is InvalidOperationException;
                if (!friendly) ErrorReporter.Background(this, "House Style couldn't finish \"" + _title + "\".", e.Error);
                _status.Text = friendly ? e.Error.Message : "The reply didn't arrive.";
                _send.Text = "Try Again";
                _replies_Update();
                _message.Focus();
                return;
            }
            _send.Text = "Send";
            var turn = (ConversationTurn)e.Result;
            _message.Clear();
            Render();
            _status.Text = turn.IsChange ? "Select Apply to make this change." : "";
            if (turn.Note.Length > 0) _status.Text = (_status.Text + " " + turn.Note).Trim();
            _cost.Text = Cost(turn, _conv);
            _replies_Update();
            if (turn.IsChange) _replies.SelectedIndex = _replies.Items.Count - 1;
            _message.Focus();
        }

        internal static string Cost(ConversationTurn turn, Conversation conv)
        {
            if (turn.Usd <= 0) return "";
            return "This reply cost about " + Money(turn.Usd) + ". Conversation total " + Money(conv.TotalUsd) + ".";
        }

        private static string Money(double usd) { return usd < 0.01 ? "$" + usd.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "$" + usd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); }

        private void Render()
        {
            var sb = new StringBuilder();
            var n = 0;
            foreach (var t in _conv.Turns)
            {
                n++;
                sb.AppendLine("You: " + t.Message).AppendLine();
                sb.AppendLine("Reply " + n + (t.IsChange ? " (a change" + (t.Applied ? ", applied" : "") + ")" : "") + ": " + t.Answer.Replace("\n", Environment.NewLine));
                if (t.IsChange) sb.AppendLine().AppendLine("Proposed text: " + System.Text.RegularExpressions.Regex.Replace(string.Join(" ", t.Proposal.ProposedBlocks.Select(b => b.InnerText).ToArray()), @"\s+", " ").Trim());
                sb.AppendLine().AppendLine();
            }
            _transcript.Text = sb.ToString().TrimEnd();
            _transcript.SelectionStart = _transcript.TextLength;
            _transcript.ScrollToCaret();
        }

        private void _replies_Update()
        {
            var keep = _replies.SelectedIndex;
            _replies.Items.Clear();
            for (var i = 0; i < _conv.Turns.Count; i++)
            {
                var t = _conv.Turns[i];
                if (!t.IsChange) continue;
                var said = t.Answer.Split('\n')[0];
                if (said.Length > 50) said = said.Substring(0, 47) + "…";
                _replies.Items.Add("Reply " + (i + 1) + ": " + said);
            }
            if (keep >= 0 && keep < _replies.Items.Count) _replies.SelectedIndex = keep;
            _apply.Enabled = _replies.SelectedIndex >= 0 && !_worker.IsBusy;
        }

        private ConversationTurn Chosen()
        {
            var number = _replies.SelectedItem == null ? 0 : int.Parse(((string)_replies.SelectedItem).Substring(6).Split(':')[0]);
            return number > 0 ? _conv.Turns[number - 1] : null;
        }

        /// <summary>For tests: applies the reply with this number (1 is the first).</summary>
        internal string ApplyReply(int number) { return Run(_conv.Turns[number - 1]); }

        private void Apply()
        {
            var turn = Chosen();
            if (turn == null) return;
            Run(turn);
        }

        private string Run(ConversationTurn turn)
        {
            try
            {
                var said = _runner.ApplyTurn(_doc, _conv, turn, _title);
                _status.Text = said + " Press Ctrl+Z to undo.";
                Render();
                return said;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(_title, ex);
                _status.Text = ex.Message;
                return ex.Message;
            }
        }
    }
}
