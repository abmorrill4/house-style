using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FlareAI.Flare.UI
{
    /// <summary>A status label that tells assistive technology when its text changes (WinForms has no aria-live).</summary>
    internal sealed class LiveLabel : Label
    {
        public LiveLabel() { AccessibleRole = AccessibleRole.StaticText; }

        public override string Text
        {
            get { return base.Text; }
            set
            {
                var changed = base.Text != value;
                base.Text = value;
                if (changed && IsHandleCreated)
                {
                    AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
                    AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                }
            }
        }
    }

    /// <summary>A link that can tell assistive technology its text changed.</summary>
    internal sealed class NoticeLink : LinkLabel
    {
        public void Announce() { if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1); }
    }

    /// <summary>Shared accessibility helpers: severity glyphs and a pass that names controls and fixes tab order.</summary>
    internal static class A11y
    {
        /// <summary>Severity is never colour-only: ● Error, ▲ Warning, ○ Suggestion.</summary>
        public static string SeverityGlyph(string severity)
        {
            switch ((severity ?? "").ToLowerInvariant())
            {
                case "error": return "●";
                case "warning": return "▲";
                default: return "○";
            }
        }

        public static string SeverityWord(string severity)
        {
            switch ((severity ?? "").ToLowerInvariant())
            {
                case "error": return "Error";
                case "warning": return "Warning";
                default: return "Suggestion";
            }
        }

        /// <summary>"● Error", "▲ Warning" or "○ Suggestion".</summary>
        public static string SeverityLabel(string severity) { return SeverityGlyph(severity) + " " + SeverityWord(severity); }

        /// <summary>What a severity means, in one plain sentence (row tooltips and the legend).</summary>
        public static string SeverityHelp(string severity)
        {
            switch ((severity ?? "").ToLowerInvariant())
            {
                case "error": return "This breaks a required rule.";
                case "warning": return "Change this unless you have a reason not to.";
                default: return "Consider changing this.";
            }
        }

        public static void Name(Control c, string name, string description = null)
        {
            c.AccessibleName = name;
            if (description != null) c.AccessibleDescription = description;
        }

        /// <summary>
        /// Gives every control that has no explicit accessible name one (buttons from their text with symbols removed,
        /// inputs from the label next to or above them), names group boxes, and puts the tab order in reading order
        /// for absolutely positioned layouts. Explicit names set earlier are kept.
        /// </summary>
        public static void Apply(Control root)
        {
            if (root == null) return;
            Walk(root);
        }

        private static void Walk(Control parent)
        {
            var kids = parent.Controls.Cast<Control>().ToList();
            foreach (var c in kids) NameOne(c, kids);
            // Reading order for absolute layouts (docked/flow layouts follow their add order, which callers set).
            if (kids.Count > 1 && kids.All(k => k.Dock == DockStyle.None) && !(parent is FlowLayoutPanel) && !(parent is TableLayoutPanel))
            {
                var ordered = kids.OrderBy(k => k.Top / 6).ThenBy(k => k.Left).ToList();
                for (var i = 0; i < ordered.Count; i++) ordered[i].TabIndex = i;
            }
            foreach (var c in kids) if (c.HasChildren) Walk(c);
        }

        private static void NameOne(Control c, List<Control> siblings)
        {
            if (!string.IsNullOrEmpty(c.AccessibleName)) return;
            if (c is Button || c is CheckBox || c is RadioButton || c is LinkLabel)
            {
                string desc;
                var n = Clean(c.Text, out desc);
                if (n.Length > 0)
                {
                    c.AccessibleName = n;
                    if (string.IsNullOrEmpty(c.AccessibleDescription) && desc != null) c.AccessibleDescription = desc;
                }
            }
            else if (c is GroupBox) { c.AccessibleName = Clean(c.Text); }
            else if (c is TextBoxBase || c is ComboBox || c is ListView || c is ListBox || c is TreeView || c is NumericUpDown)
            {
                var label = LabelFor(c, siblings);
                if (label != null) c.AccessibleName = Clean(label.Text);
            }
        }

        private static Label LabelFor(Control c, List<Control> siblings)
        {
            Label best = null; var bestScore = int.MaxValue;
            foreach (var l in siblings.OfType<Label>())
            {
                if (l == c || string.IsNullOrWhiteSpace(l.Text)) continue;
                int score;
                var sameRow = l.Right <= c.Left + 4 && l.Bottom > c.Top && l.Top < c.Bottom;
                var above = l.Bottom <= c.Top + 4 && l.Right > c.Left && l.Left < c.Right && c.Top - l.Bottom < 70;
                if (sameRow) score = c.Left - l.Right;
                else if (above) score = 1000 + (c.Top - l.Bottom);
                else continue;
                if (score < bestScore) { best = l; bestScore = score; }
            }
            return best;
        }

        private static string Clean(string text) { string d; return Clean(text, out d); }

        /// <summary>Text without access keys and decoration; <paramref name="description"/> says what the decoration meant.</summary>
        private static string Clean(string text, out string description)
        {
            description = null;
            var s = (text ?? "").Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
            if (s.Contains("…")) description = "Opens a dialog.";
            if (s.Contains("▾")) description = "Opens a menu.";
            if (s.Contains("↗")) description = "Opens the page in your browser.";
            s = s.Replace("…", "").Replace("▾", "").Replace("↗", "").Replace("▸", ">").Replace("✓", "").Replace("✗", "");
            s = s.Replace("\r", " ").Replace("\n", " ").Trim().TrimEnd(':').Trim();
            return s;
        }
    }
}
