using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// The strip above the list while one rule is walked place by place: the rule's title and severity, where you are
    /// ("12 of 48", announced), why and an example (3 lines, More opens the rest in place), and the rule-wide actions.
    /// It lays itself out to its width and is as tall as its parts need: it never has a scroll box.
    /// </summary>
    internal sealed class WalkStrip : Panel
    {
        public const string KeyboardTip = "Press Enter to change, I to ignore, or N for the next one.";
        private const int ClampLines = 3;

        private readonly Label _title, _severity, _reason, _example, _tip, _finished;
        private readonly LiveLabel _counter;
        private readonly LinkLabel _reasonMore, _exampleMore;
        private readonly Button _changeAll, _allRules, _nextRule;
        private readonly ToolTip _tips = new ToolTip();
        private bool _reasonOpen, _exampleOpen, _isFinished, _laying;
        private string _reasonText = "", _exampleText = "";

        public event EventHandler ChangeAllClicked;
        public event EventHandler AllRulesClicked;
        public event EventHandler NextRuleClicked;

        public WalkStrip()
        {
            Name = "Rule";
            AccessibleRole = AccessibleRole.Grouping;
            A11y.Name(this, "Rule");
            Font = SystemFonts.MessageBoxFont;
            Padding = new Padding(6, 4, 6, 4);
            Dock = DockStyle.Top;
            AutoScroll = false;

            _title = new Label { AutoSize = false, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), UseMnemonic = false, TabStop = false };
            _severity = new Label { AutoSize = false, UseMnemonic = false, TabStop = false };
            _counter = new LiveLabel { AutoSize = false, UseMnemonic = false, TabStop = false };
            _reason = new Label { AutoSize = false, UseMnemonic = false, TabStop = false };
            _example = new Label { AutoSize = false, UseMnemonic = false, TabStop = false };
            _tip = new Label { AutoSize = false, UseMnemonic = false, TabStop = false, ForeColor = SystemColors.GrayText, Text = KeyboardTip };
            _finished = new Label { AutoSize = false, UseMnemonic = false, TabStop = false, Visible = false };
            _reasonMore = MoreLink("reason", () => { _reasonOpen = !_reasonOpen; Relayout(); });
            _exampleMore = MoreLink("example", () => { _exampleOpen = !_exampleOpen; Relayout(); });
            _changeAll = Btn("Change All…", "Replace every instance of this issue in every file. Opens a preview.", () => Raise(ChangeAllClicked));
            _allRules = Btn("All Rules", null, () => Raise(AllRulesClicked));
            _nextRule = Btn("Next Rule", "Review the next most frequent rule one by one.", () => Raise(NextRuleClicked));
            _nextRule.Visible = false;
            _changeAll.Visible = false;

            // Tab order inside the strip: the buttons, then the More/Less links.
            var order = new Control[] { _changeAll, _allRules, _nextRule, _reasonMore, _exampleMore };
            for (var i = 0; i < order.Length; i++) order[i].TabIndex = i;
            Controls.AddRange(new Control[] { _title, _severity, _counter, _reason, _reasonMore, _example, _exampleMore, _changeAll, _allRules, _nextRule, _tip, _finished });
            Resize += (s, e) => Relayout();
            FontChanged += (s, e) => Relayout();
            Disposed += (s, e) => _tips.Dispose();
            Relayout();
        }

        /// <summary>Shows a rule being walked.</summary>
        public void ShowWalk(string title, string severity, string reason, string example, int position, int total, int changeAllCount, string changeAllTooltip)
        {
            _isFinished = false;
            _reasonOpen = _exampleOpen = false;
            _title.Text = title ?? "";
            _severity.Text = A11y.SeverityLabel(severity);
            _reasonText = reason ?? "";
            _exampleText = FormatExample(example);
            SetCounter(position, total);
            _changeAll.Tag = changeAllTooltip;
            SetChangeAllCount(changeAllCount);
            if (!string.IsNullOrEmpty(changeAllTooltip)) _tips.SetToolTip(_changeAll, changeAllTooltip);
            else _tips.SetToolTip(_changeAll, "Replace every instance of this issue in every file. Opens a preview.");
            _nextRule.Visible = false;
            Relayout();
        }

        /// <summary>Shows that the rule is done, with Next Rule when another rule is waiting.</summary>
        public void ShowFinished(string message, bool nextRuleAvailable)
        {
            _isFinished = true;
            _finished.Text = message ?? "";
            _nextRule.Visible = nextRuleAvailable;
            _changeAll.Visible = false;
            Relayout();
        }

        /// <summary>Change All (n)… — hidden when nothing is left to change everywhere.</summary>
        public void SetChangeAllCount(int n)
        {
            _changeAll.Visible = n > 0 && !_isFinished;
            _changeAll.Text = "Change All (" + n + ")…";
            A11y.Name(_changeAll, "Change All (" + n + ")", "Opens a dialog.");
            Relayout();
        }

        /// <summary>Writes "12 of 48" into the announced counter.</summary>
        public void SetCounter(int position, int total) { _counter.Text = position + " of " + total; }

        private static string FormatExample(string example)
        {
            if (string.IsNullOrEmpty(example)) return "";
            var lines = example.Replace("\r", "").Split('\n');
            var o = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (l[0] == '✗' || l[0] == '✓') o.Add(l);
                else o.Add((i == 0 ? "✗ " : "✓ ") + l);
            }
            return string.Join("\n", o.ToArray());
        }

        private static void Raise(EventHandler e) { if (e != null) e(null, EventArgs.Empty); }

        private LinkLabel MoreLink(string what, Action click)
        {
            var l = new LinkLabel { Text = "More", AutoSize = true, TabStop = true, Visible = false };
            l.LinkClicked += (s, e) => Safe.Run("More", click);
            A11y.Name(l, "Show more of the " + what);
            return l;
        }

        private Button Btn(string text, string tip, Action click)
        {
            var b = new Button { Text = text, AutoSize = false, TabStop = true };
            b.Click += (s, e) => Safe.Run(text, click);
            if (tip != null) _tips.SetToolTip(b, tip);
            return b;
        }

        private int S(int px) { return PaneRows.S(px, DeviceDpi / 96f); }

        private const TextFormatFlags Wrap = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

        /// <summary>Puts a wrapped text at the next row, clamped to 3 lines unless open; returns the next top.</summary>
        private int Place(Label l, LinkLabel more, string text, bool open, int top, int width)
        {
            l.Visible = text.Length > 0;
            if (more != null) more.Visible = false;
            if (text.Length == 0) return top;
            if (l.Text != text) l.Text = text;
            var full = TextRenderer.MeasureText(text, l.Font, new Size(width, 0), Wrap).Height;
            var max = l.Font.Height * ClampLines;
            var clamped = full > max + 1;
            var h = clamped && !open ? max : full;
            l.SetBounds(Padding.Left, top, width, h);
            top += h;
            if (more != null && clamped)
            {
                more.Text = open ? "Less" : "More";
                more.Visible = true;
                more.Location = new Point(Padding.Left, top);
                top += more.Height;
            }
            return top + S(4);
        }

        private int PlaceLine(Label l, int top, int width)
        {
            var h = Math.Max(l.Font.Height, TextRenderer.MeasureText(l.Text, l.Font, new Size(width, 0), Wrap).Height);
            l.Visible = true;
            l.SetBounds(Padding.Left, top, width, h);
            return top + h + S(2);
        }

        private void Relayout()
        {
            if (_laying || _title == null) return;
            var width = ClientSize.Width - Padding.Horizontal;
            if (width <= 0) return;
            _laying = true;
            SuspendLayout();
            try
            {
                var top = Padding.Top;
                top = PlaceLine(_title, top, width);
                if (!_isFinished)
                {
                    top = PlaceLine(_severity, top, width);
                    top = PlaceLine(_counter, top, width) + S(2);
                }
                else _severity.Visible = _counter.Visible = false;
                if (_isFinished)
                {
                    _reason.Visible = _example.Visible = _reasonMore.Visible = _exampleMore.Visible = false;
                    top = PlaceLine(_finished, top, width) + S(2);
                    _tip.Visible = false;
                }
                else
                {
                    _finished.Visible = false;
                    top = Place(_reason, _reasonMore, _reasonText, _reasonOpen, top, width);
                    top = Place(_example, _exampleMore, _exampleText, _exampleOpen, top, width);
                }
                top = PlaceButtons(top, width);
                if (!_isFinished) { top = PlaceLine(_tip, top, width); }
                var height = top + Padding.Bottom;
                if (Height != height) Height = height;
            }
            finally { ResumeLayout(false); _laying = false; }
        }

        /// <summary>Buttons flow left to right and wrap below when the strip is narrow.</summary>
        private int PlaceButtons(int top, int width)
        {
            var x = 0; var rowH = 0; var any = false;
            foreach (var b in new[] { _changeAll, _allRules, _nextRule })
            {
                if (!b.Visible) continue;
                var one = TextRenderer.MeasureText(b.Text, b.Font, Size.Empty, TextFormatFlags.NoPrefix);
                var w = Math.Min(width, one.Width + S(24));
                var h = Math.Max(one.Height + S(10), S(23));
                if (w >= width && one.Width + S(24) > width)
                    h = TextRenderer.MeasureText(b.Text, b.Font, new Size(width - S(16), 0), Wrap).Height + S(10);
                if (x > 0 && x + w > width) { top += rowH + S(4); x = 0; rowH = 0; }
                b.SetBounds(Padding.Left + x, top, w, h);
                x += w + S(4);
                rowH = Math.Max(rowH, h);
                any = true;
            }
            return any ? top + rowH + S(6) : top;
        }
    }
}
