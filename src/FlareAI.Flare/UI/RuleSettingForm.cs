using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// "Turn off / change rule…" for style guide owners: the rule as written, where the change applies (the whole team,
    /// through the style guide, or this project only), its new level, and why. No rule ids to type.
    /// </summary>
    internal sealed class RuleSettingForm : Form
    {
        private readonly RadioButton _team, _project;
        private readonly ComboBox _setting;
        private readonly TextBox _reason;
        private readonly CheckBox _others;

        public bool ForTeam { get { return _team.Checked; } }
        /// <summary>The stored token: off, suggestion, warning or error.</summary>
        public string Setting { get { return FlareAI.Core.Severity.Normalize((string)_setting.SelectedItem); } }
        /// <summary>True when the writer also wants the other rules that flag the same words turned off (same scope, setting and note).</summary>
        public bool AlsoOthers { get { return _others != null && _others.Checked; } }
        /// <summary>The ids of the checker boxes the writer ticked (generated rules such as the glossary's); empty until ticked.</summary>
        public IList<string> AlsoCheckers
        {
            get
            {
                var ids = new List<string>();
                foreach (var p in _checkerBoxes) if (p.Key.Checked) ids.Add(p.Value);
                return ids;
            }
        }
        public string Reason { get { return _reason.Text.Trim(); } }

        private readonly List<KeyValuePair<CheckBox, string>> _checkerBoxes = new List<KeyValuePair<CheckBox, string>>();

        private static string LocalName(FlareAI.Core.Style.RuleRef c)
        {
            var n = c.Name ?? c.Id ?? "";
            var dot = n.LastIndexOf('.');
            return dot >= 0 ? n.Substring(dot + 1) : n;
        }

        private static string ReadableName(FlareAI.Core.Style.RuleRef c)
        {
            var local = LocalName(c);
            return local == "GlossaryTerms" ? "Glossary terms" : local;
        }

        private static string SourceName(FlareAI.Core.Style.RuleRef c)
        {
            var n = c.Name ?? c.Id ?? "";
            var dot = n.LastIndexOf('.');
            return dot > 0 ? n.Substring(0, dot) : (c.Layer ?? "");
        }

        /// <param name="personalGuide">True when there is no team guide: the change goes to the writer's own style guide.</param>
        /// <param name="setting">The level to preselect (default Off).</param>
        /// <param name="canChangeProject">False when no project is open (Style rules without a topic).</param>
        /// <param name="title">The dialog's action, for its caption (default "Disable Rule").</param>
        public RuleSettingForm(string ruleText, bool canChangeTeamGuide, bool personalGuide = false, IList<FlareAI.Core.Style.RuleRef> otherRules = null,
                               string setting = null, bool canChangeProject = true, string title = null)
        {
            Text = "House Style — " + (title ?? "Disable Rule");
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(520, 270); Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            Controls.Add(new Label { Text = ruleText, Left = 12, Top = 12, Width = 496, Height = 48, AutoEllipsis = true });
            _team = new RadioButton { Text = personalGuide ? "For all my projects" : "For the whole team",
                                      Left = 12, Top = 66, Width = 496, Checked = canChangeTeamGuide, Enabled = canChangeTeamGuide };
            _project = new RadioButton { Text = "For this project only", Left = 12, Top = 90, Width = 496, Checked = !canChangeTeamGuide, Enabled = canChangeProject };
            Controls.Add(_team); Controls.Add(_project);
            Controls.Add(new Label { Text = "Set the rule to:", Left = 12, Top = 124, Width = 110 });
            _setting = new ComboBox { Left = 124, Top = 120, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            _setting.Items.AddRange(new object[] { "Disabled", "Suggestion", "Warning", "Error" });
            _setting.SelectedItem = FlareAI.Core.Severity.Label(setting ?? "off");
            if (_setting.SelectedIndex < 0) _setting.SelectedIndex = 0;
            Controls.Add(_setting);
            Controls.Add(new Label { Text = personalGuide ? "Reason (optional):" : "Reason (the team sees it in the guide):", Left = 12, Top = 156, Width = 496 });
            _reason = new TextBox { Left = 12, Top = 176, Width = 496 };
            Controls.Add(_reason);
            var extra = 0;
            var plain = new List<FlareAI.Core.Style.RuleRef>();
            var checkers = new List<FlareAI.Core.Style.RuleRef>();
            if (otherRules != null) foreach (var r in otherRules) { if (r == null) continue; (r.IsChecker ? checkers : plain).Add(r); }
            var disabling = setting == null || setting == "off";
            if (plain.Count > 0)
            {
                var names = new List<string>();
                foreach (var r in plain) names.Add("•  " + (r.Name.Length > 80 ? r.Name.Substring(0, 79) + "…" : r.Name));   // one line each
                _others = new CheckBox { Text = (disabling ? "Also disable " : "Also change ") + plain.Count + (plain.Count == 1 ? " other rule that flags" : " other rules that flag") + " the same words", Left = 12, Top = 208, Width = 496, Checked = true };
                var list = new Label { Text = string.Join(Environment.NewLine, names.ToArray()), Left = 32, Top = 232, Width = 476, Height = 16 * names.Count + 4, AutoEllipsis = true };
                Controls.Add(_others); Controls.Add(list);
                extra = 28 + 16 * names.Count;
                A11y.Name(_others, _others.Text, string.Join(", ", names.ToArray()));
            }
            foreach (var c in checkers)
            {
                var readable = ReadableName(c);
                var same = 0;
                foreach (var o in checkers) if (string.Equals(ReadableName(o), readable, StringComparison.OrdinalIgnoreCase)) same++;
                var shown = same > 1 ? readable + " (" + SourceName(c) + ")" : readable;
                var box = new CheckBox { Text = (disabling ? "Also disable " : "Also change ") + shown, Left = 12, Top = 208 + extra, Width = 496, Checked = false, TabStop = true };
                var note = new Label
                {
                    Text = disabling ? "This disables every glossary spelling in this guide, not just this one."
                                     : "This changes every glossary spelling in this guide, not just this one.",
                    Left = 32, Top = 232 + extra, Width = 476, Height = 20, AutoEllipsis = true
                };
                Controls.Add(box); Controls.Add(note);
                _checkerBoxes.Add(new KeyValuePair<CheckBox, string>(box, c.Id));
                A11y.Name(box, box.Text, note.Text);
                extra += 48;
            }
            ClientSize = new Size(520, 270 + extra);
            var ok = new Button { Text = "OK", Left = 352, Top = 228 + extra, Width = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 433, Top = 228 + extra, Width = 75, DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            A11y.Name(_team, _team.Text);
            A11y.Name(_project, "For this project only");
            A11y.Name(_setting, "Set the rule to");
            A11y.Name(_reason, "Reason", personalGuide ? "Optional." : "The team sees this reason in the guide.");
            A11y.Apply(this);
        }
    }
}
