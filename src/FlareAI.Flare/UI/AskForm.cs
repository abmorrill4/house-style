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
        private readonly RadioButton _question, _change, _insert;
        public string Instruction { get { return _text.Text.Trim(); } }

        /// <summary>True when the writer chose "Ask a question". False for "Make a change" (and before any choice).</summary>
        public bool IsQuestion { get { return _question.Checked; } }

        /// <summary>True when the writer chose "Insert at the cursor": new text where the caret is, with nothing selected.</summary>
        public bool IsInsert { get { return _insert.Checked; } }

        /// <param name="canInsert">False when text is selected: new text goes at the caret, so there must be no selection.</param>
        public AskForm(bool canInsert = false)
        {
            Text = Notify.Caption(Commands.Ask.Label);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(520, 252);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _question = new ChoiceButton { Text = "Ask a question", Left = 12, Top = 12, Width = 160, Height = 24 };
            _change = new ChoiceButton { Text = "Make a change", Left = 178, Top = 12, Width = 160, Height = 24 };
            _insert = new ChoiceButton { Text = "Insert at the cursor", Left = 344, Top = 12, Width = 164, Height = 24, Enabled = canInsert };
            var label = new Label { Text = "Type your question or your instruction. With nothing selected, it applies to the whole topic.", Left = 12, Top = 44, Width = 496, Height = 32 };
            _text = new TextBox { Left = 12, Top = 80, Width = 496, Height = 96, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };
            var ok = new Button { Name = "Run", Text = "Run", Left = 352, Top = 188, Width = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 433, Top = 188, Width = 75, DialogResult = DialogResult.Cancel };
            var tip = new ToolTip();
            tip.SetToolTip(_question, "Get an answer about the selected text. The topic doesn't change.");
            tip.SetToolTip(_insert, canInsert ? "Have the AI write new text where the cursor is." : "Click in the topic without selecting text to insert new text there.");
            ok.Click += (s, e) =>
            {
                if (!_question.Checked && !_change.Checked && !_insert.Checked) { DialogResult = DialogResult.None; _question.Focus(); }
                else if (Instruction.Length == 0) { DialogResult = DialogResult.None; _text.Focus(); }
            };

            Controls.AddRange(new Control[] { _question, _change, _insert, label, _text, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            A11y.Name(_question, "Ask a question", "Get an answer about the selected text. The topic doesn't change.");
            A11y.Name(_change, "Make a change", "Have the AI change the selected text.");
            A11y.Name(_insert, "Insert at the cursor", "Have the AI write new text where the cursor is.");
            A11y.Name(_text, "Instruction", "Type your question or your instruction. With nothing selected, it applies to the whole topic.");
            A11y.Name(ok, "Run", "Sends your question or instruction.");
            A11y.Apply(this);
        }
    }
}
