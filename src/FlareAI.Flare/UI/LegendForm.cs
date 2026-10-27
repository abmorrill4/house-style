using System;
using System.Drawing;
using System.Windows.Forms;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// The "?" in the issues pane: what the severity symbols, the rule sources and the issue buttons mean, one plain
    /// sentence each.
    /// </summary>
    internal sealed class LegendForm : Form
    {
        /// <summary>Label, then meaning; a null label starts a new heading (the meaning is the heading).</summary>
        internal static readonly string[][] Entries =
        {
            new[] { null, "Severity" },
            new[] { A11y.SeverityLabel("error"), A11y.SeverityHelp("error") },
            new[] { A11y.SeverityLabel("warning"), A11y.SeverityHelp("warning") },
            new[] { A11y.SeverityLabel("suggestion"), A11y.SeverityHelp("suggestion") },
            new[] { null, "Where a rule comes from" },
            new[] { "Google", "Google's developer documentation style guide." },
            new[] { "Our rules", "Your team's style guide, or your own changes if you don't have a team guide." },
            new[] { "Starter rules", "Rules that come with House Style, used when you don't have a team style guide or can't reach it." },
            new[] { "This project", "The open project's variables, glossary and rules." },
            new[] { "AI", "The AI's judgment, not a rule in the guide." },
            new[] { null, "Buttons" },
            new[] { "Change", "Replace the text with the suggestion." },
            new[] { "Change All", "Replace every instance of this issue in the topic." },
            new[] { "Ignore", "Ignore this issue." },
            new[] { "Disable Rule", "Stop linting for this rule." },
        };

        public LegendForm()
        {
            Text = "House Style — What the symbols mean";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            var page = new Panel { Left = 0, Top = 0 };
            Controls.Add(page);
            var l = new StackLayout(page, DeviceDpi / 96f);
            foreach (var e in Entries)
            {
                if (e[0] == null) { if (l.Y > l.S(12)) l.Gap(6); l.Heading(e[1]); continue; }
                l.Row(e[0], new Label { Text = e[1], AutoSize = true, MaximumSize = new Size(l.FieldWidth, 0) });
            }
            page.Size = new Size(l.Width, l.Y + l.S(4));
            var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(l.S(80), l.S(26)), DialogResult = DialogResult.Cancel };
            Controls.Add(close);
            ClientSize = new Size(l.Width, page.Bottom + l.S(26) + l.S(16));
            close.Location = new Point(ClientSize.Width - l.S(16) - Math.Max(l.S(80), close.PreferredSize.Width), page.Bottom + l.S(4));
            AcceptButton = close; CancelButton = close;
            A11y.Apply(this);
        }
    }
}
