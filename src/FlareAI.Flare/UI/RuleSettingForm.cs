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
        private readonly List<KeyValuePair<CheckBox, string>> _checkerBoxes = new List<KeyValuePair<CheckBox, string>>();

        public bool ForTeam { get { return _team.Checked; } }
        /// <summary>The stored token: off, suggestion, warning or error.</summary>
        public string Setting { get { return FlareAI.Core.Severity.Normalize((string)_setting.SelectedItem); } }
        /// <summary>True when the writer also wants the other rules that flag the same words turned off (same scope, setting and note).</summary>
        public bool AlsoOthers { get { return _others != null && _others.Checked; } }
        /// <summary>The checkers (by id) the writer ticked; empty until one is ticked.</summary>
        public List<string> AlsoCheckers
        {
            get
            {
                var ticked = new List<string>();
                foreach (var p in _checkerBoxes) if (p.Key.Checked) ticked.Add(p.Value);
                return ticked;
            }
        }
        public string Reason { get { return _reason.Text.Trim(); } }

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
            var rules = new List<FlareAI.Core.Style.RuleRef>();
            var checkers = new List<FlareAI.Core.Style.RuleRef>();
            if (otherRules != null)
                foreach (var r in otherRules) (r.IsChecker ? checkers : rules).Add(r);
            var disabling = setting == null || setting == "off";
            if (rules.Count > 0)
            {
                var names = new List<string>();
                foreach (var r in rules) names.Add("•  " + (r.Name.Length > 80 ? r.Name.Substring(0, 79) + "…" : r.Name));   // one line each
                _others = new CheckBox { Text = (disabling ? "Also disable " : "Also change ") + rules.Count + (rules.Count == 1 ? " other rule that flags" : " other rules that flag") + " the same words", Left = 12, Top = 208, Width = 496, Checked = true };
                var list = new Label { Text = string.Join(Environment.NewLine, names.ToArray()), Left = 32, Top = 232, Width = 476, Height = 16 * names.Count + 4, AutoEllipsis = true };
                Controls.Add(_others); Controls.Add(list);
                extra = 28 + 16 * names.Count;
                A11y.Name(_others, _others.Text, string.Join(", ", names.ToArray()));
            }
            var sources = new Dictionary<string, int>();
            foreach (var c in checkers) { int n; sources.TryGetValue(c.Name, out n); sources[c.Name] = n + 1; }
            foreach (var c in checkers)
            {
                var label = c.Name + (sources[c.Name] > 1 && !string.IsNullOrEmpty(c.Layer) ? " (" + c.Layer + ")" : "");
                var warning = disabling ? "This disables every glossary spelling in this guide, not just this one."
                                        : "This changes every glossary spelling in this guide, not just this one.";
                var box = new CheckBox { Text = (disabling ? "Also disable " : "Also change ") + label, Left = 12, Top = 208 + extra, Width = 496, Checked = false, TabStop = true };
                var note = new Label { Text = warning, Left = 32, Top = 230 + extra, Width = 476, Height = 16, AutoEllipsis = true };
                Controls.Add(box); Controls.Add(note);
                _checkerBoxes.Add(new KeyValuePair<CheckBox, string>(box, c.Id));
                A11y.Name(box, box.Text, warning);
                extra += 44;
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
