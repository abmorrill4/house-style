using System;
using System.Drawing;
using System.Windows.Forms;

namespace FlareAI.Flare.UI
{
    /// <summary>Free-form instruction for the current selection.</summary>
    public sealed class AskForm : Form
    {
        /// <summary>A radio button that doesn't check itself when focus reaches it, so neither choice is picked for the writer.</summary>
        private sealed class ChoiceButton : RadioButton
        {
            protected override void OnEnter(EventArgs e) { }
        }

        private readonly TextBox _text;
        private readonly RadioButton _question, _change;
        public string Instruction { get { return _text.Text.Trim(); } }

        /// <summary>True when the writer chose "Ask a question". False for "Make a change" (and before any choice).</summary>
        public bool IsQuestion { get { return _question.Checked; } }

        public AskForm()
        {
            Text = Notify.Caption(Commands.Ask.Label);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(520, 252);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _question = new ChoiceButton { Text = "Ask a question", Left = 12, Top = 12, Width = 240, Height = 24 };
            _change = new ChoiceButton { Text = "Make a change", Left = 268, Top = 12, Width = 240, Height = 24 };
            var label = new Label { Text = "Type your question or your instruction. With nothing selected, it applies to the whole topic.", Left = 12, Top = 44, Width = 496, Height = 32 };
            _text = new TextBox { Left = 12, Top = 80, Width = 496, Height = 96, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };
            var ok = new Button { Name = "Run", Text = "Run", Left = 352, Top = 188, Width = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 433, Top = 188, Width = 75, DialogResult = DialogResult.Cancel };
            new ToolTip().SetToolTip(_question, "Get an answer about the selected text. The topic doesn't change.");
            ok.Click += (s, e) =>
            {
                if (!_question.Checked && !_change.Checked) { DialogResult = DialogResult.None; _question.Focus(); }
                else if (Instruction.Length == 0) { DialogResult = DialogResult.None; _text.Focus(); }
            };

            Controls.AddRange(new Control[] { _question, _change, label, _text, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            A11y.Name(_question, "Ask a question", "Get an answer about the selected text. The topic doesn't change.");
            A11y.Name(_change, "Make a change", "Have the AI change the selected text.");
            A11y.Name(_text, "Instruction", "Type your question or your instruction. With nothing selected, it applies to the whole topic.");
            A11y.Name(ok, "Run", "Sends your question or instruction.");
            A11y.Apply(this);
        }
    }
}
