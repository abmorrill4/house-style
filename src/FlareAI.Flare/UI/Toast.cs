using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FlareAI.Flare.UI
{
    /// <summary>A notice shown by <see cref="Toast"/>; the last one stays reachable in the findings pane.</summary>
    internal sealed class Notice
    {
        public string Text, ActionLabel;
        public Action Action;
    }

    /// <summary>
    /// A small notice at the bottom right of Flare's window that goes away by itself: "3 style issues in about.htm — Show".
    /// Never takes focus, never blocks; clicking it runs the action. It stays while the mouse is over it and lasts 8 s.
    /// A close mark or Esc dismisses it. Because it can't take focus, the last notice and its action are also shown in the findings
    /// pane (<see cref="Last"/>), and a screen reader is told about it (alert).
    /// </summary>
    internal sealed class Toast : Form
    {
        private const int PlainMs = 7000, ActionMs = 8000;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        private readonly Timer _keys;
        private static Toast _open;
        private readonly Timer _timer;

        /// <summary>The most recent notice (kept after the toast hides, so the pane can offer its action).</summary>
        public static Notice Last { get; private set; }

        internal static object E2EState()
        {
            var n = Last;
            return new { text = n == null ? null : n.Text, action = n == null ? null : n.ActionLabel, open = _open != null && !_open.IsDisposed && _open.Visible };
        }

        internal static bool E2EClickAction()
        {
            var n = Last;
            if (n == null || n.Action == null) return false;
            if (_open != null && !_open.IsDisposed) _open.Close();
            n.Action();
            return true;
        }

        /// <summary>
        /// For the demo runner: the showing notice's rectangle on the screen, or with <paramref name="action"/> its action
        /// link's ("Show Issues"). Changes nothing.
        /// </summary>
        internal static System.Drawing.Rectangle E2EBounds(bool action)
        {
            if (_open == null || _open.IsDisposed || !_open.Visible) throw new InvalidOperationException("No notice is showing");
            if (!action) return _open.RectangleToScreen(_open.ClientRectangle);
            var link = _open.Controls.OfType<LinkLabel>().FirstOrDefault(l => l.Visible);
            if (link == null) throw new InvalidOperationException("The notice has no action");
            return link.RectangleToScreen(link.ClientRectangle);
        }

        internal static void E2EClose() { if (_open != null && !_open.IsDisposed) _open.Close(); }

        /// <summary>Raised when <see cref="Last"/> changes.</summary>
        public static event Action LastChanged;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x80 /* WS_EX_TOOLWINDOW */; return cp; }
        }

        private Toast(string text, string actionLabel, Action action, string secondLabel, Action second)
        {
            AutoScaleMode = AutoScaleMode.Font;
            AccessibleRole = AccessibleRole.Alert;
            AccessibleName = "House Style notice";
            AccessibleDescription = text + (action != null ? ". " + actionLabel + " is also in the House Style Issues pane." : "");
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
            BackColor = Color.FromArgb(45, 45, 48); ForeColor = Color.White; Font = SystemFonts.MessageBoxFont; Padding = new Padding(12, 10, 12, 10);
            var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(340, 0), Left = 12, Top = 10, ForeColor = Color.White };
            Controls.Add(label);
            var w = Math.Max(240, label.PreferredWidth + 24) + 20;   // room for the close mark
            var close = new Label { Text = "×", AutoSize = false, Size = new Size(20, 20), TextAlign = ContentAlignment.MiddleCenter, Top = 4, Cursor = Cursors.Hand,
                                    ForeColor = Color.White, AccessibleName = "Close notice", AccessibleRole = AccessibleRole.PushButton };
            close.Left = w - 24;
            close.Click += (s, e) => Close();
            Controls.Add(close);
            var h = label.PreferredHeight + 20;
            LinkLabel link = null;
            if (action != null)
            {
                link = new LinkLabel { Text = actionLabel, AutoSize = true, LinkColor = Color.FromArgb(120, 180, 255), Left = 12, Top = h, AccessibleName = actionLabel };
                link.LinkClicked += (s, e) => { Close(); action(); };
                Controls.Add(link);
                if (secondLabel != null)
                {
                    // A second choice ("Later") on the same line, after the main one.
                    var other = new LinkLabel { Text = secondLabel, AutoSize = true, LinkColor = Color.FromArgb(120, 180, 255), Top = h, AccessibleName = secondLabel };
                    other.Left = link.Left + link.PreferredWidth + 16;
                    other.LinkClicked += (s, e) => { Close(); if (second != null) second(); };
                    Controls.Add(other);
                    Hover(other);
                    w = Math.Max(w, other.Left + other.PreferredWidth + 12);
                }
                h += 24;
                new ToolTip().SetToolTip(this, "Also kept in the House Style pane, where you can reach \"" + actionLabel + "\" with the keyboard.");
            }
            ClientSize = new Size(w, h);
            _timer = new Timer { Interval = action != null ? ActionMs : PlainMs };
            _timer.Tick += (s, e) => { _timer.Stop(); if (!IsDisposed) Close(); };
            _timer.Start();
            // The notice never takes focus, so Esc is read from the keyboard while Flare is the active window. Esc still reaches Flare.
            GetAsyncKeyState(0x1B);   // clears the "pressed since the last call" bit
            _keys = new Timer { Interval = 30 };
            _keys.Tick += (s, e) =>
            {
                // Bit 0 says Esc went down since the last call, which catches a tap shorter than the polling interval.
                var state = GetAsyncKeyState(0x1B);
                var pressed = (state & 1) != 0;
                if (EscapeDismisses(pressed, false, !IsDisposed && Visible, pressed && InOurProcess())) Close();
            };
            _keys.Start();
            // Pause while the mouse is over the notice (any part of it), restart when it leaves.
            Hover(this); Hover(label); Hover(close); if (link != null) Hover(link);
            Click += (s, e) => Close();
            label.Click += (s, e) => { if (action != null) { Close(); action(); } };
            FormClosed += (s, e) => { _timer.Dispose(); _keys.Dispose(); };
        }

        /// <summary>Esc closes the notice only while it is showing, on the press itself (not while held), and when Flare is the active window.</summary>
        internal static bool EscapeDismisses(bool escDown, bool wasDown, bool showing, bool flareActive)
        {
            return showing && escDown && !wasDown && flareActive;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (_timer != null) _timer.Dispose(); if (_keys != null) _keys.Dispose(); }
            base.Dispose(disposing);
        }

        private static bool InOurProcess()
        {
            try { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch (Exception) { return false; }
        }

        private void Hover(Control c)
        {
            c.MouseEnter += (s, e) => _timer.Stop();
            c.MouseLeave += (s, e) => { if (!IsDisposed && !ClientRectangle.Contains(PointToClient(Cursor.Position))) { _timer.Stop(); _timer.Start(); } };
        }

        /// <summary>
        /// Takes back a notice that no longer applies (a tour hint after its step): closes it if it is still showing and
        /// clears it from the pane. Does nothing when a newer notice has replaced it.
        /// </summary>
        public static void Withdraw(string text)
        {
            try
            {
                var last = Last;
                if (last == null || last.Text != text) return;
                Last = null;
                if (_open != null && !_open.IsDisposed) _open.Close();
                var changed = LastChanged;
                if (changed != null) changed();
            }
            catch (Exception ex) { FlareAI.Core.Diagnostics.Log("toast withdraw", ex); }
        }

        /// <summary>Shows the notice over the owner's bottom-right corner, replacing any notice still showing.</summary>
        public static void Show(Form owner, string text, string actionLabel = null, Action action = null, string secondLabel = null, Action second = null)
        {
            try
            {
                Last = new Notice { Text = text, ActionLabel = actionLabel, Action = action };
                var changed = LastChanged;
                if (changed != null) try { changed(); } catch (Exception ex) { FlareAI.Core.Diagnostics.Log("toast notice", ex); }
                if (_open != null && !_open.IsDisposed) _open.Close();
                Toast t = null;
                try {
                t = new Toast(text, actionLabel, action, secondLabel, second);
                var r = owner != null ? owner.Bounds : Screen.PrimaryScreen.WorkingArea;
                t.Location = new Point(r.Right - t.Width - 24, r.Bottom - t.Height - 48);
                _open = t;
                t.Show(owner);
                t.AccessibilityNotifyClients(AccessibleEvents.SystemAlert, -1);
                }
                catch { if (t != null) { if (ReferenceEquals(_open, t)) _open = null; t.Dispose(); } throw; }   // its timers must not keep ticking
            }
            catch (Exception ex) { FlareAI.Core.Diagnostics.Log("toast", ex); }
        }
    }
}
