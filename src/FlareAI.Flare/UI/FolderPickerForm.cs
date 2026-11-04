using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Core;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Lint Folders": the folders of the project's Content that hold topics or snippets. The writer picks one or more
    /// (Ctrl or Shift to pick several); a folder covers its subfolders.
    /// </summary>
    internal sealed class FolderPickerForm : Form
    {
        private readonly ListView _list;
        private readonly Button _ok;

        /// <summary>The folders picked, relative to Content, each ending with a backslash.</summary>
        public List<string> Picked { get; private set; }

        public FolderPickerForm(IList<string> folders)
        {
            Picked = new List<string>();
            Text = Notify.Caption(Commands.LintFolders.Label);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 360);
            MinimumSize = new Size(320, 260);
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;

            var head = new LiveLabel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 8, 8, 0), Text = "Pick the folders to lint. Hold Ctrl or Shift to pick more than one. A folder includes its subfolders." };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, MultiSelect = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, HideSelection = false };
            _list.Columns.Add("Folder", -2);
            foreach (var f in folders) _list.Items.Add(new ListViewItem(f.TrimEnd('\\')) { Tag = f });
            _list.SizeChanged += (s, e) => { if (_list.Columns.Count > 0) _list.Columns[0].Width = _list.ClientSize.Width; };
            _list.SelectedIndexChanged += (s, e) => _ok.Enabled = _list.SelectedItems.Count > 0;
            _list.DoubleClick += (s, e) => { if (_ok.Enabled) _ok.PerformClick(); };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            _ok = new Button { Text = "Lint", AutoSize = true, Enabled = false };
            _ok.Click += (s, e) =>
            {
                Picked = _list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
                DialogResult = DialogResult.OK;
            };
            bar.Controls.AddRange(new Control[] { cancel, _ok });
            Controls.Add(_list);
            Controls.Add(bar);
            Controls.Add(head);
            AcceptButton = _ok;
            CancelButton = cancel;
            A11y.Name(_list, "Folders", "Pick one or more folders to lint.");
            A11y.Name(_ok, "Lint", "Lint the topics and snippets in the picked folders.");
            A11y.Name(bar, "Dialog buttons");
            A11y.Apply(this);
        }
    }
}
