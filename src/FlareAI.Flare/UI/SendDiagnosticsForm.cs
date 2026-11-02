using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using FlareAI.Flare.Logging;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// Settings &gt; Send Diagnostics: lists every file that goes in the zip and saves it where the writer chooses.
    /// Nothing is uploaded. The writer sends the zip themselves.
    /// </summary>
    internal sealed class SendDiagnosticsForm : Form
    {
        public const string Note = "This is everything in the zip. It holds no topic text and no key.";

        private readonly string _folder;
        private readonly Button _save;

        public SendDiagnosticsForm() : this(FileLog.DefaultFolder) { }

        public SendDiagnosticsForm(string folder)
        {
            _folder = folder;
            Text = Notify.Caption("Send Diagnostics");
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            var s = new StackLayout(this, DeviceDpi / 96f);
            s.Heading("Send diagnostics");
            var names = DiagnosticsBundle.Files(folder);
            var list = new StringBuilder();
            foreach (var n in names) list.AppendLine(n + "  (" + Size(Path.Combine(folder, n)) + ")");
            if (names.Count == 0) list.Append("There are no log files yet.");
            var box = s.Add(new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Left = s.LabelLeft, Top = s.Y,
                Width = s.FullWidth, Height = s.S(150), Text = list.ToString().TrimEnd()
            });
            A11y.Name(box, "Files in the zip", "Every file that goes in the zip, with its size.");
            s.Y += box.Height + s.S(8);
            s.Note(Note, true, false);

            _save = new Button { Text = "Save Zip…", AutoSize = true, MinimumSize = new Size(s.S(80), s.S(26)), Enabled = names.Count > 0 };
            var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(s.S(80), s.S(26)), DialogResult = DialogResult.Cancel };
            A11y.Name(_save, "Save Zip", "Saves the zip in Documents. Nothing is sent.");
            _save.Click += (o, e) => Safe.Run("Save Zip", SaveZip);
            cancel.Left = s.LabelLeft + s.FullWidth - cancel.PreferredSize.Width;
            cancel.Top = s.Y;
            _save.Top = s.Y;
            _save.Left = cancel.Left - _save.PreferredSize.Width - s.S(8);
            Controls.Add(_save); Controls.Add(cancel);
            CancelButton = cancel;
            ClientSize = new Size(s.Width, s.Y + cancel.PreferredSize.Height + s.S(12));
            A11y.Apply(this);
        }

        private void SaveZip()
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = Path.Combine(folder, "House Style diagnostics " + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".zip");
            DiagnosticsBundle.Build(_folder, path);
            FeedbackForm.Launch(ReportZip.SelectInFolder(path));
            DialogResult = DialogResult.OK;
        }

        private static string Size(string path)
        {
            try
            {
                var bytes = new FileInfo(path).Length;
                return bytes < 1024 ? bytes + " bytes" : (bytes / 1024) + " KB";
            }
            catch (Exception) { return "unknown size"; }
        }
    }
}
