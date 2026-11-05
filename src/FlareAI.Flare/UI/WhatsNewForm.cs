using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Update;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "What's new": the sections of whats-new.md for the versions the writer hasn't seen, newest first. Shown once after
    /// an update and on the first run of a new version, and from the What's new command. Enter or Esc closes it; when it
    /// opened by itself, "Don't show again for this version" (ticked) decides whether it comes back next start.
    /// Layout is flow-based and wraps to the window width, so nothing clips at 125 % or 150 % scaling.
    /// </summary>
    public sealed class WhatsNewForm : Form
    {
        private readonly FlowLayoutPanel _body;
        private readonly Panel _scroll;
        private readonly CheckBox _dontShow;

        /// <param name="sections">What to show, newest first.</param>
        /// <param name="banner">A line above the notes ("Updated to 0.4.0."), or null.</param>
        /// <param name="offerDontShow">True when the window opened by itself (the check box is shown).</param>
        public WhatsNewForm(IList<WhatsNew.Section> sections, string banner, bool offerDontShow)
        {
            Text = Notify.Caption("What's New");
            AccessibleName = "What's New in House Style";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = true;
            KeyPreview = true;
            ClientSize = new Size(560, 460);
            MinimumSize = new Size(360, 260);

            // A scrolling FlowLayoutPanel undercounts its own height and cut off the last line, so a plain Panel scrolls
            // a FlowLayoutPanel that grows to fit the notes.
            _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = SystemColors.Window, TabStop = false };
            _body = new FlowLayoutPanel
            {
                Location = Point.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Padding = new Padding(16, 12, 16, 12), BackColor = SystemColors.Window, AccessibleName = "Release notes", TabStop = false
            };
            _scroll.Controls.Add(_body);

            if (!string.IsNullOrEmpty(banner)) Add(new Label { Text = banner, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10) });
            if (sections.Count == 0)
                Add(new Label { Text = "There are no notes for this version yet." });
            foreach (var s in sections)
            {
                var head = "House Style " + s.Version + (string.IsNullOrEmpty(s.Title) ? "" : " — " + s.Title);
                Add(new Label { Text = head, Font = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold), Margin = new Padding(0, 6, 0, 6), AccessibleRole = AccessibleRole.Grouping });
                foreach (var b in s.Blocks) Add(BlockControl(b));
            }

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = true };
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 26) };
            _dontShow = new CheckBox { Text = "Don't show again for this version", Checked = true, AutoSize = true, Visible = offerDontShow, Margin = new Padding(3, 6, 12, 3) };
            bar.Controls.Add(close);
            bar.Controls.Add(_dontShow);
            Controls.Add(_scroll);
            Controls.Add(bar);
            AcceptButton = close; CancelButton = close;
            Resize += (s, e) => Rewrap();
            // The release notes link at the end takes focus when the window opens and scrolls it down, so go back to the top.
            Shown += (s, e) => { Rewrap(); close.Focus(); _scroll.AutoScrollPosition = Point.Empty; };
            A11y.Apply(this);
        }

        /// <summary>True unless the writer unticked "Don't show again for this version".</summary>
        public bool DontShowAgain { get { return !_dontShow.Visible || _dontShow.Checked; } }

        private void Add(Control c)
        {
            c.AutoSize = true;
            _body.Controls.Add(c);
        }

        private Control BlockControl(WhatsNew.Block b)
        {
            var text = b.PlainText;
            var prefix = b.Kind == WhatsNew.Kind.Bullet ? "•  " : "";
            var margin = b.Kind == WhatsNew.Kind.Heading ? new Padding(0, 8, 0, 4) : b.Kind == WhatsNew.Kind.Bullet ? new Padding(12, 2, 0, 2) : new Padding(0, 2, 0, 6);
            Font font = b.Kind == WhatsNew.Kind.Heading ? new Font(Font, FontStyle.Bold) : Font;
            if (!b.Runs.Any(r => r.Url != null))
                return new Label { Text = prefix + text, Font = font, Margin = margin, AccessibleName = text };

            var link = new LinkLabel { Text = prefix + text, Font = font, Margin = margin, AccessibleName = text, AccessibleDescription = "Contains links that open in your browser." };
            link.Links.Clear();
            var at = prefix.Length;
            foreach (var r in b.Runs)
            {
                if (r.Url != null) link.Links.Add(at, r.Text.Length, r.Url);
                at += r.Text.Length;
            }
            link.LinkClicked += (s, e) =>
            {
                var url = e.Link.LinkData as string;
                if (url == null || !WhatsNew.IsWebLink(url)) return;
                try { System.Diagnostics.Process.Start(url); } catch (Exception ex) { Diagnostics.Log("open link", ex); }
            };
            return link;
        }

        /// <summary>Wraps every line to the visible width (scrollbar included), at any DPI.</summary>
        private void Rewrap()
        {
            var width = Math.Max(120, _scroll.ClientSize.Width - _body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
            _body.SuspendLayout();
            foreach (Control c in _body.Controls) c.MaximumSize = new Size(Math.Max(60, width - c.Margin.Horizontal), 0);
            _body.ResumeLayout(true);
        }
    }
}
