using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Flare.Logging;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// Settings &gt; Send Diagnostics: lists the log files the zip would hold. Nothing is written until the writer clicks
    /// Create Zip, and nothing is uploaded: the zip goes into the reports folder and the writer sends it.
    /// </summary>
    public sealed class SendDiagnosticsForm : Form
    {
        public const string Explanation = "These files are what the zip holds. They have no topic text and no API key.";
        public const string NoFiles = "There are no log files yet.";
        public const string Created = "Your diagnostics zip is ready. Send it to support.";

        private readonly ListView _list;
        private readonly Button _create, _cancel;
        private readonly string _logs;

        /// <summary>The zip made by Create Zip (null until then).</summary>
        public string ZipPath { get; private set; }

        public SendDiagnosticsForm() : this(Paths.Logs) { }

        internal SendDiagnosticsForm(string logsFolder)
        {
            _logs = logsFolder;
            Text = Notify.Caption("Send Diagnostics");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            var scale = DeviceDpi / 96f;
            Func<int, int> s = v => (int)(v * scale);
            ClientSize = new Size(s(460), s(300));

            var files = DiagnosticsBundle.Files(_logs);

            var note = new Label { Left = s(12), Top = s(12), Width = s(436), Height = s(40), Text = files.Count == 0 ? NoFiles : Explanation, AutoSize = false };
            Controls.Add(note);

            _list = new ListView { Left = s(12), Top = s(58), Width = s(436), Height = s(180), View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            _list.Columns.Add("File", s(220));
            _list.Columns.Add("Size", s(70), HorizontalAlignment.Right);
            _list.Columns.Add("Date", s(130));
            foreach (var f in files)
                _list.Items.Add(new ListViewItem(new[] { f.Name, FormatSize(f.Length), f.LastWriteTime.ToString("g") }));
            Controls.Add(_list);

            _cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = s(90), Height = s(28), Left = s(358), Top = s(254) };
            _create = new Button { Text = "Create Zip", Width = s(100), Height = s(28), Left = s(252), Top = s(254), Enabled = files.Count > 0 };
            _create.Click += (a, b) => Safe.Run("Create Zip", CreateZip);
            Controls.Add(_create);
            Controls.Add(_cancel);
            AcceptButton = _create;
            CancelButton = _cancel;
            A11y.Name(_create, "Create Zip", "Collects the log files listed above into a zip in the reports folder. Nothing is sent.");
            A11y.Name(_list, "Log files in the zip");
        }

        private void CreateZip()
        {
            var folder = FeedbackForm.SaveFolder ?? ReportZip.DefaultFolder;
            var path = Path.Combine(folder, "House Style diagnostics " + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".zip");
            for (var i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, "House Style diagnostics " + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + " (" + i + ").zip");
            ZipPath = DiagnosticsBundle.Build(_logs, path);
            _create.Enabled = false;
            _cancel.Text = "Close";
            FeedbackForm.Launch(ReportZip.SelectInFolder(ZipPath));
            Toast.Show(this, Created);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " KB";
            return (bytes / (1024 * 1024.0)).ToString("0.0") + " MB";
        }
    }
}
