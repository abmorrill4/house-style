using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using FlareAI.Core.Editing;

namespace FlareAI.Flare.UI
{
    /// <summary>Paints a <see cref="ReadableDiff"/> into a RichTextBox: removed text struck red, added text green.</summary>
    internal static class DiffView
    {
        private static readonly Color Removed = Color.FromArgb(176, 0, 32), RemovedBack = Color.FromArgb(253, 231, 233);
        private static readonly Color Added = Color.FromArgb(0, 104, 55), AddedBack = Color.FromArgb(226, 245, 233);

        public static RichTextBox Create()
        {
            return new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, DetectUrls = false, Font = SystemFonts.MessageBoxFont, ScrollBars = RichTextBoxScrollBars.Vertical };
        }

        /// <summary>
        /// A box for text shown in full inside the scrolling card: no scroll bar of its own, plain text only (no links), and
        /// the mouse wheel scrolls the card rather than the box.
        /// </summary>
        public static CardTextBox CreateFull()
        {
            return new CardTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, DetectUrls = false, Font = SystemFonts.MessageBoxFont, ScrollBars = RichTextBoxScrollBars.None };
        }

        /// <param name="dimUnchanged">Grey out lines with no changes, so the eye goes to the edits.</param>
        public static void Render(RichTextBox box, IList<DiffLine> lines, bool dimUnchanged)
        {
            box.Clear();
            foreach (var line in lines)
            {
                var dim = dimUnchanged && !line.Changed;
                Append(box, line.Prefix, dim ? SystemColors.GrayText : SystemColors.ControlDarkDark, box.BackColor, FontStyle.Regular, box.Font);
                Segments(box, line.Segments, dim);
                box.AppendText("\n");
            }
            box.Select(0, 0);
        }

        public static void Segments(RichTextBox box, IList<DiffSegment> segments, bool dim)
        {
            foreach (var s in segments)
            {
                if (s.Kind == DiffKind.Same) Append(box, s.Text, dim ? SystemColors.GrayText : SystemColors.WindowText, box.BackColor, FontStyle.Regular, box.Font);
                else if (s.Kind == DiffKind.Deleted) Append(box, s.Text, Removed, RemovedBack, FontStyle.Strikeout, box.Font);
                else Append(box, s.Text, Added, AddedBack, FontStyle.Underline, box.Font);
            }
        }

        private static void Append(RichTextBox box, string text, Color fore, Color back, FontStyle style, Font font)
        {
            if (string.IsNullOrEmpty(text)) return;
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = fore;
            box.SelectionBackColor = back;
            box.SelectionFont = new Font(font, style);
            box.AppendText(text);
        }
    }

    /// <summary>
    /// A read-only RichTextBox that its parent sizes to its text: <see cref="ContentHeight"/> says how tall all of its text
    /// is at its current width (from the box's own line metrics, not a GDI estimate), and the mouse wheel goes to the
    /// parent, so the card scrolls instead of the box.
    /// </summary>
    public sealed class CardTextBox : RichTextBox
    {
        private const int WM_MOUSEWHEEL = 0x020A, EM_REQUESTRESIZE = 0x0400 + 65;
        private int _requested;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public CardTextBox() { ContentsResized += (s, e) => _requested = e.NewRectangle.Height; }

        /// <summary>The height the box needs to show all its text at its current width, or 0 before it has a handle.</summary>
        public int ContentHeight()
        {
            if (!IsHandleCreated || TextLength == 0) return 0;
            _requested = 0;
            SendMessage(Handle, EM_REQUESTRESIZE, IntPtr.Zero, IntPtr.Zero);   // raises ContentsResized with the text's full size
            // The last line's top, measured from the first line's (so a box scrolled inside still measures right), plus a line.
            var lines = GetPositionFromCharIndex(TextLength - 1).Y - GetPositionFromCharIndex(0).Y + Font.Height;
            return Math.Max(_requested, lines) + 4;
        }

        /// <summary>The height ContentsResized last reported (for tests: the box's own answer, not the layout's).</summary>
        public int RequestedHeight { get { return _requested; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && Parent != null && Parent.IsHandleCreated)
            {
                SendMessage(Parent.Handle, m.Msg, m.WParam, m.LParam);   // the card scrolls, the box never does
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }
    }
}
