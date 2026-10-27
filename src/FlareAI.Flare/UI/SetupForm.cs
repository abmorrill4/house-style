using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Core;
using FlareAI.Core.Llm;
using FlareAI.Core.Settings;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// First-run setup in four steps: how you use House Style (writer or style guide owner), how you use AI, whose rules,
    /// done. Shown once when Flare first starts with House Style (or on the first command if that didn't happen), and from
    /// Settings (Run Setup Again). Finishing or skipping saves the settings file, so it doesn't show again. What is valid
    /// and what gets saved is <see cref="SetupAnswers"/> (Core).
    /// </summary>
    public sealed class SetupForm : Form
    {
        private readonly PluginSettings _settings;
        private readonly SetupAnswers _answers;
        private readonly Panel[] _steps;
        private readonly Label _stepTitle;
        private readonly Button _back, _next, _skipStep;
        private SetupStep _step;
        private bool _closed, _finished;

        // Step 1: role
        private readonly StackLayout _roleLayout;
        private readonly RadioButton[] _roleChoices;
        private readonly Label _guideHeading, _guideOwnerNote;
        private readonly StackLayout.Block _ownerNoteBlock;

        // Step 1: AI
        private readonly StackLayout _aiLayout;
        private readonly RadioButton[] _aiChoices;
        private readonly StackLayout.Block _urlBlock, _keyBlock, _noAiBlock;
        private readonly TextBox _baseUrl, _key;
        private readonly Label _keyLabel, _keyStatus, _testResult, _aiError;
        private readonly Button _test;

        // Step 2: style guide
        private readonly StackLayout _guideLayout;
        private readonly RadioButton[] _guideChoices;
        private readonly StackLayout.Block _folderBlock, _googleNoteBlock;
        private readonly TextBox _folder;
        private readonly Button _browse;
        private readonly Label _folderStatus;

        // Step 3: done
        private readonly StackLayout _doneLayout;
        private readonly Label _summary;
        private readonly Func<int, int> _s;

        public SetupForm()
        {
            _settings = PluginSettings.Load();
            _answers = SetupAnswers.From(_settings);

            Text = "House Style — Setup";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;   // sizes below are measured at the real DPI and font
            var scale = DeviceDpi / 96f;
            Func<int, int> S = _s = px => (int)Math.Round(px * scale);

            _stepTitle = new LiveLabel { AutoSize = true, Left = S(12), Top = S(12), ForeColor = SystemColors.GrayText };
            Controls.Add(_stepTitle);
            _steps = new Panel[StepCount];
            for (var i = 0; i < StepCount; i++) { _steps[i] = new Panel { Left = 0, Top = S(30), Visible = i == 0 }; Controls.Add(_steps[i]); }

            // ---- 1. Role -------------------------------------------------------------------------------------------
            var role = _roleLayout = new StackLayout(_steps[(int)SetupStep.Role], scale);
            role.Heading(Roles.Question);
            _roleChoices = role.Choices(Roles.Question, Roles.Answers, role.S(8));
            role.Note("Style guide owners also get the Style Guide group on the ribbon. You can change this in Settings.", true);

            // ---- 2. AI ---------------------------------------------------------------------------------------------
            var ai = _aiLayout = new StackLayout(_steps[(int)SetupStep.Ai], scale);
            ai.Heading("How will you use AI?");
            ai.Note("Linting works without AI and costs nothing. AI adds wording issues, Rewrite and Ask AI.", true, false);
            _aiChoices = ai.Choices("How will you use AI", AiChoices.All.Select(AiChoices.Label).ToArray(), ai.S(8));
            foreach (var r in _aiChoices) r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) OnAiChoice(); };
            _urlBlock = ai.Begin();
            _baseUrl = ai.Row("Base URL", new TextBox { Text = _answers.BaseUrl ?? "" });
            ai.Note("The address your admin gave you, e.g. https://gateway.example.com");
            _keyBlock = ai.Begin();
            _key = ai.Row("API key", new TextBox { UseSystemPasswordChar = true });
            _keyLabel = (Label)_key.Tag;
            _keyStatus = ai.Status(1);
            _test = ai.Add(new Button { Text = "Test connection", AutoSize = true, Left = ai.FieldLeft, Top = ai.Y });
            _test.Click += (s, e) => TestConnection();
            var testW = _test.PreferredSize.Width + ai.S(10);
            _testResult = ai.Add(new LiveLabel { AutoSize = false, Left = ai.FieldLeft + testW, Top = ai.Y + ai.S(4), Width = ai.FieldWidth - testW, Height = (Font.Height + 1) * 4, ForeColor = SystemColors.GrayText });
            ai.Y += Math.Max(_test.PreferredSize.Height + ai.S(4), _testResult.Height + ai.S(4)) + ai.S(4);
            ai.Note("The key is stored encrypted for your Windows account and never shown again.");
            _noAiBlock = ai.Begin();
            ai.Note("You can turn AI on later in House Style > Settings, on the AI tab.", true);
            ai.Begin();
            _aiError = ai.Status(2, true);
            _aiError.ForeColor = Color.FromArgb(160, 0, 0);

            // ---- 3. Style guide ------------------------------------------------------------------------------------
            var g = _guideLayout = new StackLayout(_steps[(int)SetupStep.Guide], scale);
            _guideHeading = g.Heading("Whose rules?");
            _guideChoices = g.Choices("Whose rules", new[] { "Google's guide plus my own changes", "My team's shared style guide folder" }, g.S(8));
            foreach (var r in _guideChoices) r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) OnGuideChoice(); };
            // Each choice's note follows the choice: Google's guide, or where the team's guide is.
            _googleNoteBlock = g.Begin();
            g.Note("Google's developer documentation style guide is built in. Rules you disable or add are kept for you alone.", true);
            _ownerNoteBlock = g.Begin();
            _guideOwnerNote = g.Note("Choose the Flare project folder that holds your team's style guide. Writers on your team choose the same folder.", true, false);
            _folderBlock = g.Begin();
            _folder = g.Row("Team style guide", new TextBox { Text = _answers.TeamGuideFolder ?? "" }, g.FieldWidth - g.S(92));
            _browse = g.Add(new Button { Text = "Browse…", Left = g.FieldLeft + g.FieldWidth - g.S(86), Top = _folder.Top - 1, Width = g.S(86), Height = Math.Max(g.S(26), _folder.Height + 2) });
            _browse.Click += (s, e) => Browse();
            _folder.Leave += (s, e) => CheckFolder();
            g.Y = Math.Max(g.Y, _browse.Bottom + g.S(4));
            _folderStatus = g.Status(1);
            _folderStatus.TextChanged += (s, e) => { _guideLayout.FitHeight(_folderStatus); FitToStep(); };
            g.Note("Ask your style guide owner where the team's guide is. It's a Flare project folder, often on OneDrive, SharePoint, Google Drive or a network drive.");

            // ---- 4. Done -------------------------------------------------------------------------------------------
            var d = _doneLayout = new StackLayout(_steps[(int)SetupStep.Done], scale);
            d.Heading("You're ready");
            d.Note("Open a topic and click Lint Topic. Lint Project lints every topic in the project.", true, false);
            _summary = d.Status(4, true);
            var guideLink = d.Add(new LinkLabel { Text = "Open the getting started guide", AutoSize = true, Left = d.LabelLeft, Top = d.Y });
            guideLink.LinkClicked += (s, e) => OpenGuide();
            d.Y += guideLink.PreferredHeight + d.S(8);
            d.Note("You can change any of this in Settings on the House Style tab.", true);

            // ---- size: each step as tall as what it shows (FitToStep), so no step has a big empty area ------------
            var stepH = new[] { role.FullHeight(), ai.FullHeight(), g.FullHeight(), d.FullHeight() }.Max() + S(8);
            foreach (var p in _steps) p.Size = new Size(ai.Width, stepH);

            _skipStep = new Button { Text = "Skip this step", AutoSize = true, MinimumSize = new Size(S(80), S(26)) };
            _back = new Button { Text = "Back", AutoSize = true, MinimumSize = new Size(S(80), S(26)) };
            _next = new Button { Text = "Next", AutoSize = true, MinimumSize = new Size(S(80), S(26)) };
            _skipStep.Click += (s, e) => SkipStep();
            _back.Click += (s, e) => Go(_step - 1);
            _next.Click += (s, e) => Next();
            Controls.AddRange(new Control[] { _skipStep, _back, _next });
            var by = _steps[0].Bottom + S(10);
            var buttonW = Math.Max(S(80), new[] { "Next", "Finish" }.Max(t => TextRenderer.MeasureText(t, Font).Width + S(24)));
            ClientSize = new Size(ai.Width, by + S(26) + S(12));
            _next.MinimumSize = new Size(buttonW, S(26));
            _next.Location = new Point(ClientSize.Width - S(16) - buttonW, by);
            _back.Location = new Point(_next.Left - S(8) - Math.Max(S(80), _back.PreferredSize.Width), by);
            _skipStep.Location = new Point(S(12), by);
            AcceptButton = _next;

            A11y.Name(_baseUrl, "Base URL", "The gateway address your admin gave you.");
            A11y.Name(_keyStatus, "Key status");
            A11y.Name(_test, "Test connection", "Makes one small request with the address and key as shown, and reports whether it worked.");
            A11y.Name(_testResult, "Connection test result");
            A11y.Name(_aiError, "Problem with this step");
            A11y.Name(_folder, "Team style guide folder");
            A11y.Name(_browse, "Browse for the team style guide folder");
            A11y.Name(_folderStatus, "Style guide folder status");
            A11y.Name(_summary, "What was set up");
            A11y.Name(guideLink, "Open the getting started guide", "Opens the page in your browser.");
            A11y.Name(_skipStep, "Skip this step", "Keeps your current choice for this step and goes on.");

            var team = FlareAI.Core.Actions.Team.Current;
            _answers.Role = Roles.Effective(_settings, team != null && team.IsListedOwner);
            _roleChoices[(int)_answers.Role].Checked = true;
            foreach (var r in _roleChoices) r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) OnRoleChoice(); };
            _aiChoices[Array.IndexOf(AiChoices.All, _answers.Ai)].Checked = true;
            _guideChoices[_answers.UseTeamGuide ? 1 : 0].Checked = true;
            OnRoleChoice();
            if (_settings.HasStoredKey) _keyStatus.Text = "A key is stored. Leave the box blank to keep it.";
            A11y.Apply(this);
            Go(SetupStep.Role);
            FormClosing += OnClosing;
            KeyPreview = true;   // Esc closes, which skips setup
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };
            FormClosed += (s, e) => _closed = true;
        }

        /// <summary>True when the writer finished the steps (false when they closed setup, which also counts as skipped).</summary>
        public bool Completed { get { return _finished; } }

        // ---- navigation --------------------------------------------------------------------------------------------

        private const int StepCount = 4;

        private void Go(SetupStep step)
        {
            if (step < SetupStep.Role) step = SetupStep.Role;
            _step = step;
            for (var i = 0; i < _steps.Length; i++) _steps[i].Visible = i == (int)step;
            _stepTitle.Text = "Step " + ((int)step + 1) + " of " + StepCount;
            _back.Visible = step != SetupStep.Role;
            _skipStep.Visible = step != SetupStep.Done && step != SetupStep.Role;
            _next.Text = step == SetupStep.Done ? "Finish" : "Next";
            if (step == SetupStep.Done) _summary.Text = PreviewSummary();
            FitToStep();
            var first = step == SetupStep.Role ? (Control)_roleChoices.First(r => r.Checked)
                      : step == SetupStep.Ai ? (Control)_aiChoices.First(r => r.Checked)
                      : step == SetupStep.Guide ? (Control)_guideChoices.First(r => r.Checked) : _next;
            if (IsHandleCreated) first.Focus(); else Shown += (s, e) => first.Focus();
        }

        /// <summary>
        /// Sizes the dialog to the step on show: its visible blocks plus the buttons. A step with less to say is shorter,
        /// instead of every step keeping the empty space the tallest one (step 1 with a gateway) needs.
        /// </summary>
        private void FitToStep()
        {
            if (_next == null || _steps == null) return;
            var layout = _step == SetupStep.Role ? _roleLayout : _step == SetupStep.Ai ? _aiLayout : _step == SetupStep.Guide ? _guideLayout : _doneLayout;
            if (layout == null) return;
            var page = _steps[(int)_step];
            page.Height = layout.VisibleHeight() + _s(8);
            var by = page.Bottom + _s(10);
            ClientSize = new Size(ClientSize.Width, by + Math.Max(_next.Height, _s(26)) + _s(12));
            foreach (var b in new[] { _skipStep, _back, _next }) b.Top = by;
        }

        private void Next()
        {
            if (_step == SetupStep.Role)
            {
                Collect();
                Go(SetupStep.Ai);
            }
            else if (_step == SetupStep.Ai)
            {
                Collect();
                _answers.AiSkipped = false;
                var problem = _answers.AiProblem(_settings);
                _aiError.Text = problem ?? "";
                if (problem != null) { (AiChoices.UsesBaseUrl(_answers.Ai) && problem.Contains("address") ? _baseUrl : _key).Focus(); return; }
                Go(SetupStep.Guide);
            }
            else if (_step == SetupStep.Guide)
            {
                Collect();
                _answers.GuideSkipped = false;
                var problem = _answers.GuideProblem();
                if (problem == null && _answers.UseTeamGuide)
                {
                    var folder = _folder.Text.Trim();
                    if (_checking) problem = "Still checking the folder. Try again in a moment.";
                    else if (_checkedFolder != folder) { CheckFolder(); problem = "Checking the folder…"; }
                    else if (!_checkedOk) problem = _folderStatus.Text;
                }
                if (problem != null) { _folderStatus.Text = problem; _folder.Focus(); return; }
                Go(SetupStep.Done);
            }
            else Finish();
        }

        private void SkipStep()
        {
            if (_step == SetupStep.Ai) { _answers.AiSkipped = true; _aiError.Text = ""; Go(SetupStep.Guide); }
            else if (_step == SetupStep.Guide) { _answers.GuideSkipped = true; Go(SetupStep.Done); }
        }

        private void Collect()
        {
            _answers.Role = (UserRole)Array.FindIndex(_roleChoices, r => r.Checked);
            _answers.Ai = AiChoices.All[Array.FindIndex(_aiChoices, r => r.Checked)];
            _answers.BaseUrl = _baseUrl.Text.Trim();
            _answers.Key = _key.Text.Trim();
            _answers.UseTeamGuide = _guideChoices[1].Checked;
            _answers.TeamGuideFolder = _folder.Text.Trim();
        }

        private void Finish()
        {
            Collect();
            string error;
            try
            {
                if (!FirstRun.Finish(_settings, _answers, out error))
                {
                    Go(_answers.AiProblem(_settings) != null ? SetupStep.Ai : SetupStep.Guide);
                    if (_step == SetupStep.Ai) _aiError.Text = error; else _folderStatus.Text = error;
                    return;
                }
            }
            catch (Exception ex)
            {
                Notify.Error(this, "House Style couldn't save your settings.", "Check that the settings folder isn't read-only, then try again.", ex, "Setup");
                return;
            }
            _finished = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Closing the window skips setup: the settings are saved as they are, so it doesn't show again.</summary>
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_finished) return;
            try { string error; FirstRun.Finish(_settings, null, out error); }
            catch (Exception ex) { Diagnostics.Log("setup skipped: settings not saved", ex); }
        }

        private string PreviewSummary()
        {
            var preview = _settings.Clone();   // nothing is saved until Finish
            string error;
            return _answers.ApplyTo(preview, out error) ? FirstRun.Summary(preview, _answers.FeedbackWritable) : error;
        }

        private static void OpenGuide()
        {
            try { System.Diagnostics.Process.Start(FirstRun.TesterGuideUrl); }
            catch (Exception ex) { Diagnostics.Log("open tester guide", ex); }
        }

        // ---- step 1 ------------------------------------------------------------------------------------------------

        /// <summary>
        /// An owner sets up the team's guide: the guide step leads with the team folder. A writer gets the guide question
        /// as before. Only the default changes; either answer stays possible.
        /// </summary>
        private void OnRoleChoice()
        {
            var owner = _roleChoices[(int)UserRole.Owner].Checked;
            _guideHeading.Text = owner ? "Where is your team's style guide?" : "Whose rules?";
            if (owner && !_answers.UseTeamGuide && _folder.Text.Trim().Length == 0) _guideChoices[1].Checked = true;
            else if (!owner && _guideChoices[1].Checked && _folder.Text.Trim().Length == 0) _guideChoices[0].Checked = true;
            OnGuideChoice();
        }

        // ---- step 2 ------------------------------------------------------------------------------------------------

        private void OnAiChoice()
        {
            var choice = AiChoices.All[Array.FindIndex(_aiChoices, r => r.Checked)];
            _urlBlock.Visible = AiChoices.UsesBaseUrl(choice);
            _keyBlock.Visible = AiChoices.UsesKey(choice);
            _noAiBlock.Visible = choice == AiChoice.None;
            _keyLabel.Text = AiChoices.KeyLabel(choice);
            A11y.Name(_key, AiChoices.KeyLabel(choice), "Write only: the key is stored encrypted and never shown back.");
            _testResult.Text = ""; _aiError.Text = "";
            _aiLayout.Reflow();
            FitToStep();
        }

        private int _testSeq;

        private void TestConnection()
        {
            Collect();
            var choice = _answers.Ai;
            var provider = AiChoices.Provider(choice);
            string url = PluginSettings.DefaultAnthropicBaseUrl, err;
            if (AiChoices.UsesBaseUrl(choice))
            {
                if (!LlmErrors.TryNormalizeBaseUrl(_baseUrl.Text, out url, out err)) { ShowTestResult(false, err); return; }
                _baseUrl.Text = url;   // show what will be used
            }
            var key = _answers.Key.Length > 0 ? _answers.Key : AiChoices.Of(_settings) == choice ? _settings.ResolveApiKey() : null;
            var model = choice == AiChoice.Gateway ? BifrostClient.GatewayModel(PluginSettings.DefaultModel) : string.IsNullOrEmpty(_settings.Model) ? PluginSettings.DefaultModel : _settings.Model;
            var timeout = _settings.TimeoutSeconds;
            var seq = ++_testSeq;
            _test.Enabled = false;
            _testResult.ForeColor = SystemColors.GrayText;
            _testResult.Text = "Testing…";
            var w = new BackgroundWorker();
            w.DoWork += (s, e) => e.Result = ConnectionTest.Run(provider, url, key, model, timeout);
            w.RunWorkerCompleted += (s, e) =>
            {
                if (_closed) return;
                _test.Enabled = true;
                if (seq != _testSeq) return;
                if (e.Error != null) { ShowTestResult(false, "The test couldn't run."); return; }
                var r = (ConnectionResult)e.Result;
                ShowTestResult(r.Ok, r.Message);
            };
            w.RunWorkerAsync();
        }

        private void ShowTestResult(bool ok, string message)
        {
            _testResult.ForeColor = ok ? SystemColors.ControlText : Color.FromArgb(160, 0, 0);
            _testResult.Text = (ok ? "✓ " : "") + message;
        }

        // ---- step 3 ------------------------------------------------------------------------------------------------

        private void OnGuideChoice()
        {
            _folderBlock.Visible = _guideChoices[1].Checked;
            _googleNoteBlock.Visible = !_folderBlock.Visible;
            _ownerNoteBlock.Visible = _folderBlock.Visible && _roleChoices != null && _roleChoices[(int)UserRole.Owner].Checked;
            _guideLayout.Reflow();
            FitToStep();
            if (_folderBlock.Visible && _folder.Text.Trim().Length == 0) _folderStatus.Text = "Choose the folder that holds your team's style guide.";
            if (_folderBlock.Visible && _folder.Text.Trim().Length > 0 && _checkedFolder != _folder.Text.Trim()) CheckFolder();
        }

        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose your team's style guide (a Flare project folder)", SelectedPath = _folder.Text.Trim() })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _folder.Text = dlg.SelectedPath;
            }
            CheckFolder();
        }

        private int _folderSeq;
        private bool _checking, _checkedOk;
        private string _checkedFolder;

        /// <summary>Checks the folder in the background (network and synced folders can be slow) and says what it is in plain words.</summary>
        private void CheckFolder()
        {
            var path = _folder.Text.Trim();
            if (path.Length == 0) { _folderSeq++; _checking = false; _checkedFolder = null; _folderStatus.Text = "Choose the folder that holds your team's style guide."; return; }
            if (path == _checkedFolder && !_checking) return;
            var seq = ++_folderSeq;
            _checking = true;
            _folderStatus.ForeColor = SystemColors.GrayText;
            _folderStatus.Text = "Checking the folder…";
            var w = new BackgroundWorker();
            w.DoWork += (s, e) => e.Result = GuideFolderCheck.Check(path);
            w.RunWorkerCompleted += (s, e) =>
            {
                if (_closed || seq != _folderSeq) return;
                _checking = false;
                _checkedFolder = path;
                var r = e.Error == null ? (GuideFolderStatus)e.Result : null;
                _checkedOk = r != null && r.Ok;
                _folderStatus.ForeColor = _checkedOk ? SystemColors.ControlText : Color.FromArgb(160, 0, 0);
                _folderStatus.Text = r == null ? "Couldn't check this folder." : r.Message;
            };
            w.RunWorkerAsync();
        }
    }
}
