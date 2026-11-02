using System;
using System.Drawing;
using System.Windows.Forms;

namespace FlareAI.Flare.UI
{
    public enum AskMode { None, Question, Change }

    /// <summary>Free-form instruction for the current selection.</summary>
    public sealed class AskForm : Form
    {
        private readonly TextBox _text;
        private readonly RadioButton _question, _change;

        /// <summary>What the writer picked. None until they choose; House Style doesn't guess.</summary>
        public AskMode Mode { get { return _question.Checked ? AskMode.Question : _change.Checked ? AskMode.Change : AskMode.None; } }
        public string Instruction { get { return _text.Text.Trim(); } }

        public AskForm()
        {
            Text = Notify.Caption(Commands.Ask.Label);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(520, 260);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            _question = new RadioButton { Text = "Ask a question", Left = 12, Top = 12, Width = 240, Height = 24, Checked = false, TabStop = true };
            _change = new RadioButton { Text = "Make a change", Left = 260, Top = 12, Width = 240, Height = 24, Checked = false, TabStop = true };
            new ToolTip().SetToolTip(_question, "Get an answer about the selected text. The topic doesn't change.");
            var label = new Label { Text = "Pick what you want, then type it. With nothing selected, it works on the whole topic.", Left = 12, Top = 44, Width = 496, Height = 32 };
            _text = new TextBox { Left = 12, Top = 80, Width = 496, Height = 96, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };
            var ok = new Button { Text = "Run", Left = 352, Top = 192, Width = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 433, Top = 192, Width = 75, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) =>
            {
                if (Mode == AskMode.None) { DialogResult = DialogResult.None; _question.Focus(); }
                else if (Instruction.Length == 0) { DialogResult = DialogResult.None; _text.Focus(); }
            };

            Controls.AddRange(new Control[] { _question, _change, label, _text, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            A11y.Name(_question, "Ask a question", "Get an answer about the selected text. The topic doesn't change.");
            A11y.Name(_change, "Make a change", "Have the AI change the selected text as a tracked change.");
            A11y.Name(_text, "Instruction", "Tell the AI what to change in the selected text, or in the whole topic if nothing is selected.");
            A11y.Name(ok, "Run", "Sends the instruction.");
            A11y.Apply(this);
        }
    }
}
