using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FlareAI.Core.Llm;
using FlareAI.Core.Settings;

namespace FlareAI.Flare.UI
{
    /// <summary>
    /// Settings in four tabs: AI, Style guide (with the team feedback folder), Linting, Advanced. The key is DPAPI-protected on save and never shown back.
    /// Which AI fields show for each choice comes from <see cref="AiChoices"/> (Core), shared with first-run setup.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private readonly ListBox _layers;
        private readonly TextBox _feedback;
        private readonly Button _addGuide, _removeGuide, _browseFeedback, _refreshModels, _test, _runSetup;
        private readonly Label _guideStatus, _feedbackStatus, _modelStatus, _testResult, _keyLabel, _keyStatus;
        private readonly CheckBox _projectLayer, _llmExport, _markers, _live, _onSave, _beforeBuild, _diag, _autoUpdate, _autoReports;
        private readonly Label _updateStatus;
        private readonly StackLayout _guide, _adv;
        private readonly StackLayout.Block _modelNoteBlock;
        private readonly string _pluginDir;
        private readonly Func<string> _whyUpdatesOff;
        private readonly FlareAI.Core.Update.UpdatePolicy _updatePolicy;
        private readonly RadioButton[] _choices;
        private readonly ComboBox _model, _fastModel, _deepModel;
        private readonly TextBox _baseUrl, _key, _timeout;
        private readonly TabControl _tabs;
        private readonly StackLayout _ai;
        private readonly StackLayout.Block _urlBlock, _keyBlock, _testBlock, _aiNoteBlock, _noAiBlock;
        private readonly LinkLabel _clearKey;
        private readonly PluginSettings _settings;

        /// <summary>True when the writer chose "Run setup again": Settings closed without saving and setup should start.</summary>
        public bool RunSetupRequested { get; private set; }
        /// <summary>True when the writer chose "Start the Tour Again": Settings closed without saving and the tour should start.</summary>
        public bool TourRequested { get; private set; }

        private readonly RadioButton[] _roles;
        private readonly UserRole _startRole;

        private const string GatewayHint = "The address your admin gave you, e.g. https://gateway.example.com";

        /// <param name="pluginDir">The plugin folder (where an IT department's update-policy.json lives), or null.</param>
        /// <param name="whyUpdatesOff">Why automatic updates are off now (<c>UpdateFlow.WhyOff</c>), null when they are on.</param>
        /// <param name="role">The role the ribbon was built for (the one in effect until Flare restarts).</param>
        /// <param name="exportLlm">Export for LLMs, from the Advanced tab (null hides the button).</param>
        /// <param name="diagnostics">Diagnostics, from the Advanced tab (null hides the button).</param>
        /// <param name="reportProblem">Report a Problem, from the Advanced tab (null hides the button).</param>
        public SettingsForm(string pluginDir = null, Func<string> whyUpdatesOff = null, UserRole role = UserRole.Writer,
                            Action<IWin32Window> exportLlm = null, Action<IWin32Window> diagnostics = null, Action<IWin32Window> reportProblem = null)
        {
            _settings = PluginSettings.Load();
            _startRole = _settings.Role ?? role;
            _pluginDir = pluginDir;
            _whyUpdatesOff = whyUpdatesOff;
            _updatePolicy = FlareAI.Core.Update.UpdatePolicy.Resolve(_settings.AutoUpdate, pluginDir);

            Text = "House Style — Settings";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;   // sizes below are measured at the real DPI and font
            var scale = DeviceDpi / 96f;

            // The tab control is in the form before the pages are filled, so the pages measure text with the dialog font.
            _tabs = new TabControl { Left = (int)(8 * scale), Top = (int)(8 * scale) };
            Controls.Add(_tabs);
            var aiPage = Page("AI"); var guidePage = Page("Style guide"); var checkPage = Page("Linting"); var advPage = Page("Advanced");

            // ---- AI ----------------------------------------------------------------------------------------------
            var ai = _ai = new StackLayout(aiPage, scale);
            ai.Heading("How should House Style use AI?");
            _choices = ai.Choices("How House Style uses AI", AiChoices.All.Select(AiChoices.Label).ToArray(), ai.S(8));
            foreach (var r in _choices) r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) OnChoiceChanged(); };

            _urlBlock = ai.Begin();
            _baseUrl = ai.Row("Base URL", new TextBox { Text = _settings.BaseUrl });
            ai.Note(GatewayHint);

            _keyBlock = ai.Begin();
            _key = ai.Row("API key", new TextBox { UseSystemPasswordChar = true });
            _keyLabel = (Label)_key.Tag;
            _key.Leave += (s, e) => { if (_key.Text.Trim().Length > 0 && _key.Text.Trim() != _loadedKey) LoadModels(); };
            _keyStatus = ai.Status(2);
            var clearKey = _clearKey = ai.Add(new LinkLabel { Text = "Remove the stored key", AutoSize = true, Left = ai.FieldLeft, Top = ai.Y });
            clearKey.LinkClicked += (s, e) => { _settings.SetApiKey(null); _key.Text = ""; RefreshKeyStatus(); _keyStatus.Text += " Save to apply."; };
            ai.Y += clearKey.PreferredHeight + ai.S(8);

            _testBlock = ai.Begin();
            _test = ai.Add(new Button { Text = "Test connection", AutoSize = true, Left = ai.FieldLeft, Top = ai.Y });
            _test.Click += (s, e) => TestConnection();
            var testW = _test.PreferredSize.Width + ai.S(10);
            _testResult = ai.Add(new LiveLabel { AutoSize = false, Text = "", Left = ai.FieldLeft + testW, Top = ai.Y + ai.S(4), Width = ai.FieldWidth - testW, Height = (Font.Height + 1) * 4, ForeColor = SystemColors.GrayText });
            ai.Y += Math.Max(_test.PreferredSize.Height + ai.S(4), _testResult.Height + ai.S(4)) + ai.S(6);

            _aiNoteBlock = ai.Begin();
            ai.Note("AI runs only when you ask for it, in Lint Topic's wording review, Rewrite, Ask AI, Topic Metadata and Test Rules. " +
                    "Linting as you type, on save, before a build and Lint Project never use AI and cost nothing.");

            _noAiBlock = ai.Begin();
            ai.Note("Without AI, House Style still lints your writing as you type, when you save, before a build, and with Lint Topic and Lint Project. " +
                    "Rewrite, Ask AI, Topic Metadata and the AI wording review need AI.", true);

            // ---- Style guide --------------------------------------------------------------------------------------
            var g = _guide = new StackLayout(guidePage, scale);
            g.Heading(Roles.Question);
            _roles = g.Choices(Roles.Question, Roles.Answers, g.S(8));
            _roles[(int)_startRole].Checked = true;
            g.Note("A change to the ribbon applies the next time you start Flare.", true);
            g.Gap(4);
            _layers = g.Row("Team style guide", new ListBox { Height = g.S(64), IntegralHeight = false, HorizontalScrollbar = true }, g.FieldWidth - g.S(92));
            foreach (var line in (_settings.StyleLayers ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.Trim().Length > 0) _layers.Items.Add(line.Trim());
            _addGuide = g.Add(new Button { Text = "Add…", Left = g.FieldLeft + g.FieldWidth - g.S(86), Top = _layers.Top - 1, Width = g.S(86), Height = g.S(26) });
            _removeGuide = g.Add(new Button { Text = "Remove", Left = _addGuide.Left, Top = _addGuide.Bottom + g.S(4), Width = g.S(86), Height = g.S(26) });
            _addGuide.Click += (s, e) =>
            {
                var f = PickFolder("Choose your team's style guide (a Flare project folder)");
                if (f == null || _layers.Items.Contains(f)) return;
                if (GuideFolderCheck.HasGoogleLayer(f))
                {
                    MessageBox.Show(this, GuideFolderCheck.GoogleLayerMessage, "House Style", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _layers.Items.Add(f);
                CheckGuides(); CheckFeedback();
            };
            _removeGuide.Click += (s, e) => { if (_layers.SelectedIndex >= 0) { _layers.Items.RemoveAt(_layers.SelectedIndex); CheckGuides(); CheckFeedback(); } };
            g.Y = Math.Max(g.Y, _removeGuide.Bottom + g.S(6));
            _guideStatus = g.Status(1);   // grows with its text (FitHeight): no gap under a one-line status
            g.Note("Leave the list empty to use Google's developer documentation style guide plus your own changes. " +
                   "A team guide is a Flare project folder your style guide owner shares (on OneDrive, SharePoint, Google Drive or a network drive). If you add more than one, the later one wins.");
            _projectLayer = g.Check("Also use this project's variables, glossary and StyleGuide folder", _settings.UseProjectLayer);
            g.Note("House Style also lints with the open project's variables, glossary terms and any rules in its Resources\\StyleGuide folder.");
            // The team feedback folder sits with the team's guide: where decisions and requests to its owners go.
            _feedback = g.Row("Team feedback folder", new TextBox { Text = _settings.FeedbackFolder ?? "" }, g.FieldWidth - g.S(92));
            _browseFeedback = g.Add(new Button { Text = "Browse…", Left = g.FieldLeft + g.FieldWidth - g.S(86), Top = _feedback.Top - 1, Width = g.S(86), Height = Math.Max(g.S(26), _feedback.Height + 2) });
            _browseFeedback.Click += (s, e) => { var f = PickFolder("Choose the shared team feedback folder"); if (f != null) { _feedback.Text = f; CheckFeedback(); } };
            _feedback.Leave += (s, e) => CheckFeedback();
            g.Y = Math.Max(g.Y, _browseFeedback.Bottom + g.S(4));
            _feedbackStatus = g.Status(2);   // room for the requests sentence; FitHeight sizes it to its text
            g.Note("A shared folder where your team's decisions are collected. Leave it empty to keep them on this computer. Owners see each decision, its rule and the text, and names only if the style guide is set to show them.");
            g.Reflow();
            _guideStatus.TextChanged += (s, e) => _guide.FitHeight(_guideStatus);
            _feedbackStatus.TextChanged += (s, e) => _guide.FitHeight(_feedbackStatus);

            // ---- Checking -----------------------------------------------------------------------------------------
            var c = new StackLayout(checkPage, scale);
            c.Note("Linting uses only the rules, with no AI and no cost. It never stops you from saving or building.", true);
            _live = c.Check("Lint my writing as I type", _settings.LiveCheck, true);
            _onSave = c.Check("When I save a topic, tell me how many issues it has", _settings.CheckOnSave, true);
            _beforeBuild = c.Check("When I start a build, lint the whole project", _settings.ReportBeforeBuild, true);
            _markers = c.Check("Mark paragraphs that have issues with a bar in the margin", _settings.ShowMarkers, true);
            c.Reflow();

            // ---- Advanced -----------------------------------------------------------------------------------------
            var a = _adv = new StackLayout(advPage, scale);
            _model = a.Row("Model (standard)", NewModelCombo());
            _fastModel = a.Row("Fast model", NewModelCombo());
            _deepModel = a.Row("Deep model", NewModelCombo());
            _refreshModels = a.Add(new Button { Text = "Refresh", AutoSize = true, Left = a.FieldLeft, Top = a.Y });
            _refreshModels.Click += (s, e) => LoadModels();
            var refreshW = _refreshModels.PreferredSize.Width + a.S(10);
            _modelStatus = a.Add(new LiveLabel { Text = "", AutoSize = false, Left = a.FieldLeft + refreshW, Top = a.Y + a.S(4), Width = a.FieldWidth - refreshW, Height = (Font.Height + 1) * 2, ForeColor = SystemColors.GrayText });
            a.Y += Math.Max(_refreshModels.PreferredSize.Height + a.S(4), _modelStatus.Height + a.S(4)) + a.S(4);
            _modelNoteBlock = a.Begin();   // only when the lists do come from the key (not with AI off or a gateway)
            a.Note("The lists come from your key. Default means the plugin's built-in model. A style guide's FlareAI\\models.json overrides these for the team.");
            a.Begin();
            _models = new ModelInfo[0];
            _timeout = a.Row("Timeout (s)", new TextBox { Text = _settings.TimeoutSeconds.ToString() }, a.S(60));
            _diag = a.Check("Write a diagnostics log (for support)", _settings.WriteDiagnostics);
            _autoReports = a.Check("Send error reports automatically", _settings.SendErrorReportsAutomatically);
            a.Note("House Style sends a report only when you choose Send, or by itself when this box is checked. Reports never include your topic text unless you type it in.");
            _llmExport = a.Check("After each HTML build, add LLM-ready Markdown and llms.txt to the output", _settings.LlmExportAfterBuild);
            _autoUpdate = a.Check("Keep House Style up to date (checks GitHub once a day)", _updatePolicy.Enabled);
            _autoUpdate.Enabled = !_updatePolicy.FromPolicyFile;   // the IT department's update-policy.json decides
            _updateStatus = a.Status(1);
            _updateStatus.Left = a.LabelLeft + a.S(18); _updateStatus.Width = a.FullWidth - a.S(18);
            _autoUpdate.CheckedChanged += (s, e) => ShowUpdateStatus();
            a.Reflow();
            _updateStatus.TextChanged += (s, e) => _adv.FitHeight(_updateStatus);
            // Tools that left the ribbon: Export for LLMs and Diagnostics, plus the tour.
            var tools = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Left = a.LabelLeft, Top = a.Y, MaximumSize = new Size(a.FullWidth, 0) };
            a.Add(tools);
            if (exportLlm != null) ToolButton(tools, Commands.ExportLlm.Label + "…", Commands.ExportLlm.Tooltip, () => exportLlm(this));
            if (diagnostics != null) ToolButton(tools, Commands.Diagnostics.Label + "…", Commands.Diagnostics.Tooltip, () => diagnostics(this));
            if (reportProblem != null) ToolButton(tools, Commands.ReportProblem.Label, Commands.ReportProblem.Tooltip, () => reportProblem(this));
            ToolButton(tools, Commands.Tour.Label, Commands.Tour.Tooltip, () => { TourRequested = true; DialogResult = DialogResult.Cancel; Close(); });
            a.Y += tools.PreferredSize.Height + a.S(8);

            // ---- size: the tallest page, every AI block shown ----------------------------------------------------
            // The Style guide page leaves a line of room for statuses that grow (FitHeight).
            var pageH = new[] { ai.FullHeight(), g.Y + Font.Height + 1, c.Y, a.Y }.Max() + ai.S(8);
            var pageW = ai.Width;
            _tabs.Size = new Size(pageW + ai.S(20), pageH + ai.S(40));
            var h = _tabs.Handle;   // the real tab header size needs the window
            var extraW = _tabs.Width - _tabs.DisplayRectangle.Width; var extraH = _tabs.Height - _tabs.DisplayRectangle.Height;
            _tabs.Size = new Size(pageW + extraW, pageH + extraH);

            _runSetup = new Button { Text = "Run setup again…", AutoSize = true, MinimumSize = new Size(ai.S(80), ai.S(26)) };
            _runSetup.Click += (s, e) => { RunSetupRequested = true; DialogResult = DialogResult.Cancel; Close(); };
            var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(ai.S(80), ai.S(26)) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(ai.S(80), ai.S(26)), DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { _runSetup, save, cancel });
            var by = _tabs.Bottom + ai.S(10);
            ClientSize = new Size(_tabs.Right + _tabs.Left, by + Math.Max(cancel.PreferredSize.Height, ai.S(26)) + ai.S(12));
            cancel.Location = new Point(ClientSize.Width - _tabs.Left - Math.Max(ai.S(80), cancel.PreferredSize.Width), by);
            save.Location = new Point(cancel.Left - ai.S(8) - Math.Max(ai.S(80), save.PreferredSize.Width), by);
            _runSetup.Location = new Point(_tabs.Left, by);
            save.Click += (s, e) => OnSave();
            AcceptButton = save; CancelButton = cancel;
            var room = Screen.FromPoint(Cursor.Position).WorkingArea.Height - ai.S(40);
            if (ClientSize.Height > room)
            {
                // Very small screens: the pages scroll instead of the dialog growing off screen.
                var cut = ClientSize.Height - room;
                _tabs.Height -= cut; ClientSize = new Size(ClientSize.Width, room);
                foreach (var b in new[] { _runSetup, save, cancel }) b.Top -= cut;
                foreach (TabPage p in _tabs.TabPages) p.AutoScroll = true;
            }

            A11y.Name(_choices[0].Parent, "How House Style uses AI");
            A11y.Name(_baseUrl, "Base URL", "The gateway address your admin gave you.");
            A11y.Name(_model, "Standard model", "Pick from the models your key can use.");
            A11y.Name(_deepModel, "Deep model");
            A11y.Name(_fastModel, "Fast model");
            A11y.Name(_refreshModels, "Refresh models", "Reload the model lists from your key.");
            A11y.Name(_modelStatus, "Model list status");
            A11y.Name(_addGuide, "Add a team style guide folder");
            A11y.Name(_removeGuide, "Remove the selected style guide folder");
            A11y.Name(_guideStatus, "Style guide status");
            A11y.Name(_browseFeedback, "Browse for the team feedback folder");
            A11y.Name(_feedback, "Team feedback folder", "Shared folder where review decisions are pooled. Empty keeps them personal.");
            A11y.Name(_feedbackStatus, "Team feedback folder status");
            A11y.Name(_keyStatus, "Key status");
            A11y.Name(clearKey, "Remove the stored key", "Takes effect when you save.");
            A11y.Name(_test, "Test connection", "Makes one small request with the address and key as shown, and reports whether it worked.");
            A11y.Name(_testResult, "Connection test result");
            A11y.Name(_layers, "Team style guide folders", "Folders of your team's style guide. None uses Google's guide plus your own changes.");
            A11y.Name(_timeout, "Timeout in seconds");
            A11y.Name(_runSetup, "Run setup again", "Closes Settings without saving and starts setup.");
            A11y.Name(_roles[0].Parent, Roles.Question);

            var choice = AiChoices.Of(_settings);
            _choices[Array.IndexOf(AiChoices.All, choice)].Checked = true;   // runs OnChoiceChanged
            if (_shownProvider < 0) OnChoiceChanged();
            RefreshKeyStatus();
            A11y.Apply(this);
            CheckGuides(); CheckFeedback(); ShowUpdateStatus();
            A11y.Name(_autoUpdate, "Keep House Style up to date", "Checks GitHub once a day for a newer release and offers to install it.");
            A11y.Name(_updateStatus, "Automatic update status");
            A11y.Name(_autoReports, "Send error reports automatically", "When House Style hits an error, it sends the report in the background and shows a short notice.");
            Shown += (s, e) => LoadModels();
            FormClosed += (s, e) => _closed = true;
        }

        private readonly ToolTip _tips = new ToolTip();

        private void ToolButton(FlowLayoutPanel bar, string text, string tip, Action click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, (int)(26 * DeviceDpi / 96f)), Margin = new Padding(0, 0, 8, 4) };
            b.Click += (s, e) => Safe.Run(text, click);
            _tips.SetToolTip(b, tip);
            A11y.Name(b, text.TrimEnd('…'), tip);
            bar.Controls.Add(b);
        }

        /// <summary>Under the update check box: who decides, and whether updates are on (from <c>UpdateFlow.WhyOff</c>).</summary>
        private void ShowUpdateStatus()
        {
            string why = null;
            try { why = _whyUpdatesOff == null ? null : _whyUpdatesOff(); } catch (Exception ex) { FlareAI.Core.Diagnostics.Log("update status", ex); }
            if (_updatePolicy.FromPolicyFile)
                _updateStatus.Text = "Set by your IT department. Automatic updates are " + (_updatePolicy.Enabled ? "on." : "off.");
            else if (why != null && why.StartsWith("not available", StringComparison.OrdinalIgnoreCase))
                _updateStatus.Text = "Automatic updates are " + why + ".";
            else
                _updateStatus.Text = _autoUpdate.Checked == _settings.AutoUpdate
                    ? "Automatic updates are " + (_autoUpdate.Checked ? "on." : "off.")
                    : "Automatic updates turn " + (_autoUpdate.Checked ? "on" : "off") + " when you save.";
        }

        private TabPage Page(string text)
        {
            var p = new TabPage(text) { UseVisualStyleBackColor = true };
            _tabs.TabPages.Add(p);
            return p;
        }

        // ---- AI choice ---------------------------------------------------------------------------------------------

        private AiChoice Choice
        {
            get
            {
                for (var i = 0; i < _choices.Length; i++) if (_choices[i].Checked) return AiChoices.All[i];
                return AiChoice.AnthropicKey;
            }
        }

        private bool Gateway { get { return Choice == AiChoice.Gateway; } }

        private int _shownProvider = -1;   // the provider whose address is in the Base URL box
        private string _otherBaseUrl;      // the other provider's address, so switching back and forth restores it

        /// <summary>Shows only the fields the choice needs (<see cref="AiChoices"/>) and keeps each provider's address.</summary>
        private void OnChoiceChanged()
        {
            var choice = Choice;
            if (choice != AiChoice.None)
            {
                var provider = (int)AiChoices.Provider(choice);
                if (_shownProvider >= 0 && _shownProvider != provider)
                {
                    var url = _baseUrl.Text;
                    _baseUrl.Text = _otherBaseUrl ?? "";
                    _otherBaseUrl = url;
                }
                _shownProvider = provider;
                var gateway = choice == AiChoice.Gateway;
                if (gateway && _baseUrl.Text.Trim() == PluginSettings.DefaultAnthropicBaseUrl) _baseUrl.Text = "";   // the gateway's URL goes here
                if (!gateway && (string.IsNullOrEmpty(_baseUrl.Text) || _baseUrl.Text.Contains("/v1"))) _baseUrl.Text = PluginSettings.DefaultAnthropicBaseUrl;
            }
            _urlBlock.Visible = AiChoices.UsesBaseUrl(choice);
            _keyBlock.Visible = _testBlock.Visible = _aiNoteBlock.Visible = AiChoices.UsesKey(choice);
            _noAiBlock.Visible = choice == AiChoice.None;
            _keyLabel.Text = AiChoices.KeyLabel(choice);
            A11y.Name(_key, AiChoices.KeyLabel(choice), "Write only: a stored key is never shown back. Leave blank to keep the stored key.");
            _testResult.Text = "";
            _ai.Reflow();
            _modelNoteBlock.Visible = AiChoices.UsesModelPickers(choice);   // AI off or a gateway: the lists don't come from a key
            _adv.Reflow();
            ShowModels(choice);
        }

        private int _testSeq;

        private void TestConnection()
        {
            var provider = AiChoices.Provider(Choice);
            string url, err;
            var typed = _baseUrl.Text.Trim();
            if (!Gateway && typed.Length == 0) typed = PluginSettings.DefaultAnthropicBaseUrl;
            if (!LlmErrors.TryNormalizeBaseUrl(typed, out url, out err)) { ShowTestResult(false, err); return; }
            _baseUrl.Text = url;   // show what will be used
            var key = KeyForLoading();
            var model = GetModel(_model);
            int timeout;
            if (!int.TryParse(_timeout.Text, out timeout)) timeout = 30;
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

        // ---- models --------------------------------------------------------------------------------------------

        private sealed class ModelItem
        {
            public string Value, Text;
            public override string ToString() { return Text; }
        }

        private IList<ModelInfo> _models;
        private string _loadedKey = "";
        private int _loadSeq;
        private bool _closed;

        private ComboBox NewModelCombo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 480 }; }

        private string DefaultOf(ComboBox c)
        {
            return c == _model ? PluginSettings.DefaultModel : c == _fastModel ? "claude-haiku-5-5" : "claude-opus-5-5";
        }

        private string GetModel(ComboBox c)
        {
            var item = c.SelectedItem as ModelItem;
            return item != null ? item.Value : c.Text.Trim();
        }

        /// <summary>Fills one combo from the loaded list, keeping <paramref name="value"/> selectable even if the list lacks it.</summary>
        private void SetModel(ComboBox c, string value)
        {
            value = (value ?? "").Trim();
            if (Gateway)
            {
                var gm = BifrostClient.GatewayModel(DefaultOf(c));
                c.Items.Clear(); c.Items.Add(new ModelItem { Value = gm, Text = gm }); c.SelectedIndex = 0;
                return;
            }
            c.Items.Clear();
            var optional = c != _model;
            if (optional) c.Items.Add(new ModelItem { Value = "", Text = "Default (" + DefaultOf(c) + ")" });
            foreach (var m in _models) c.Items.Add(new ModelItem { Value = m.Id, Text = m.ToString() });
            if (value.Length == 0 && !optional) value = PluginSettings.DefaultModel;
            var pick = c.Items.Cast<ModelItem>().FirstOrDefault(i => string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase));
            if (pick == null) { pick = new ModelItem { Value = value, Text = value }; c.Items.Add(pick); }
            c.SelectedItem = pick;
        }

        /// <summary>Model pickers work for your own key only: the gateway's models are fixed, and No AI has none.</summary>
        private void ShowModels(AiChoice choice)
        {
            var pickers = AiChoices.UsesModelPickers(choice);
            foreach (var c in new[] { _model, _fastModel, _deepModel }) c.Enabled = pickers;
            _refreshModels.Enabled = pickers;
            _modelStatus.Text = choice == AiChoice.Gateway ? "Fixed for the gateway." : choice == AiChoice.None ? "AI is off (see the AI tab)." : "";
            SetModel(_model, _settings.Model); SetModel(_fastModel, _settings.FastModel); SetModel(_deepModel, _settings.DeepModel);
            if (pickers && IsHandleCreated) LoadModels();
        }

        private string KeyForLoading()
        {
            var typed = _key.Text.Trim();
            return typed.Length > 0 ? typed : _settings.ResolveApiKey();
        }

        private void LoadModels()
        {
            if (!AiChoices.UsesModelPickers(Choice) || _closed) return;
            var key = KeyForLoading();
            if (string.IsNullOrEmpty(key)) { _modelStatus.Text = "Add a key on the AI tab to load the models it can use."; return; }
            var baseUrl = _baseUrl.Text.Trim(); var timeout = 30;
            var seq = ++_loadSeq;
            _loadedKey = _key.Text.Trim();
            _modelStatus.Text = "Loading models…";
            _refreshModels.Enabled = false;
            var w = new BackgroundWorker();
            w.DoWork += (s, e) => e.Result = AnthropicModels.List(baseUrl, key, timeout);
            w.RunWorkerCompleted += (s, e) =>
            {
                if (_closed || seq != _loadSeq) return;
                var pickers = AiChoices.UsesModelPickers(Choice);
                _refreshModels.Enabled = pickers;
                if (e.Error != null)
                {
                    if (pickers) _modelStatus.Text = e.Error is LlmException ? e.Error.Message : "Couldn't load models.";
                    return;
                }
                if (!pickers) return;
                var list = (IList<ModelInfo>)e.Result;
                var vals = new[] { GetModel(_model), GetModel(_fastModel), GetModel(_deepModel) };
                _models = list;
                SetModel(_model, vals[0]); SetModel(_fastModel, vals[1]); SetModel(_deepModel, vals[2]);
                _modelStatus.Text = list.Count + (list.Count == 1 ? " model" : " models") + " available to this key";
            };
            w.RunWorkerAsync();
        }

        // ---- folders -------------------------------------------------------------------------------------------

        private string PickFolder(string description)
        {
            using (var d = new FolderBrowserDialog { Description = description })
                return d.ShowDialog(this) == DialogResult.OK ? d.SelectedPath : null;
        }

        private int _guideSeq, _feedbackSeq;

        private void CheckGuides()
        {
            var paths = _layers.Items.Cast<string>().ToList();
            if (paths.Count == 0) { _guideSeq++; _guideStatus.Text = "Using Google's guide plus your own changes."; return; }
            var seq = ++_guideSeq;
            _guideStatus.Text = "Checking…";
            var w = new BackgroundWorker();
            w.DoWork += (s, e) => e.Result = string.Join("\n", paths.Select(p => GuideFolderCheck.Check(p).Message).ToArray());
            w.RunWorkerCompleted += (s, e) =>
            {
                if (_closed || seq != _guideSeq) return;
                _guideStatus.Text = e.Error != null ? "Couldn't check these folders." : (string)e.Result;
            };
            w.RunWorkerAsync();
        }

        /// <summary>A team guide is in the list: an entry that isn't the plugin's own guide (the test <see cref="SetupAnswers.From"/> uses).</summary>
        private bool HasTeamGuide
        {
            get { return _layers.Items.Cast<string>().Any(p => p.Trim().Length > 0 && !p.Contains("{PluginDir}")); }
        }

        /// <summary>Whether the feedback folder can be written, and with a team guide, where requests to its owners go.</summary>
        private void CheckFeedback()
        {
            var path = _feedback.Text.Trim();
            if (path.Length == 0)
            {
                _feedbackSeq++;
                _feedbackStatus.Text = HasTeamGuide ? "Personal only. Requests to your style guide owners stay on this computer until you choose a folder." : "Personal only";
                return;
            }
            var seq = ++_feedbackSeq;
            _feedbackStatus.Text = "Checking…";
            var w = new BackgroundWorker();
            w.DoWork += (s, e) => e.Result = FolderAccess.CanWrite(path);
            w.RunWorkerCompleted += (s, e) =>
            {
                if (_closed || seq != _feedbackSeq) return;
                var ok = e.Error == null && (bool)e.Result;
                var team = HasTeamGuide;
                if (ok) _feedbackStatus.Text = team ? "✓ Shared with your team. Your requests to your style guide owners go here too." : "✓ Shared with your team";
                else _feedbackStatus.Text = team ? "Can't write to this folder. Requests to your style guide owners wait on this computer until it can be reached." : "Can't write to this folder";
            };
            w.RunWorkerAsync();
        }

        private void RefreshKeyStatus()
        {
            if (_settings.HasStoredKey) _keyStatus.Text = "A key is stored (protected). Leave the box blank to keep it.";
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PluginSettings.EnvKeyName))) _keyStatus.Text = "Using " + PluginSettings.EnvKeyName + " from the environment.";
            else _keyStatus.Text = "No key yet.";
            _clearKey.Enabled = _settings.HasStoredKey;
        }

        private void OnSave()
        {
            int timeout;
            if (!int.TryParse(_timeout.Text, out timeout) || timeout < 10)
            {
                Notify.Warn(this, "Timeout must be a number of seconds, 10 or more.", "Settings");
                _tabs.SelectedIndex = 3; _timeout.Focus(); return;
            }
            var choice = Choice;
            string cleanUrl = _baseUrl.Text.Trim(), urlError;
            if (choice != AiChoice.None)
            {
                var typedUrl = cleanUrl;
                if (!Gateway && typedUrl.Length == 0) typedUrl = PluginSettings.DefaultAnthropicBaseUrl;
                if ((typedUrl.Length > 0 || !Gateway) && !LlmErrors.TryNormalizeBaseUrl(typedUrl, out cleanUrl, out urlError))
                {
                    Notify.Warn(this, urlError, "Settings");
                    _tabs.SelectedIndex = 0; _baseUrl.Focus(); return;
                }
                _baseUrl.Text = cleanUrl;
            }
            AiChoices.Apply(_settings, choice);   // No AI also removes the stored key
            if (choice != AiChoice.None) _settings.BaseUrl = cleanUrl;
            if (choice != AiChoice.Gateway)   // the gateway's fixed models would overwrite the direct-key choices
            {
                _settings.Model = GetModel(_model);
                _settings.FastModel = GetModel(_fastModel);
                _settings.DeepModel = GetModel(_deepModel);
            }
            _settings.TimeoutSeconds = timeout;
            _settings.WriteDiagnostics = _diag.Checked;
            _settings.SendErrorReportsAutomatically = _autoReports.Checked;
            // A report sent while Settings was open may have created this install's id: keep it.
            if (string.IsNullOrEmpty(_settings.ClientId)) _settings.ClientId = PluginSettings.Load().ClientId;
            _settings.StyleLayers = string.Join("\n", _layers.Items.Cast<string>().ToArray());
            _settings.UseProjectLayer = _projectLayer.Checked;
            _settings.FeedbackFolder = _feedback.Text.Trim();
            _settings.LlmExportAfterBuild = _llmExport.Checked;
            _settings.ShowMarkers = _markers.Checked;
            _settings.LiveCheck = _live.Checked;
            _settings.CheckOnSave = _onSave.Checked;
            _settings.ReportBeforeBuild = _beforeBuild.Checked;
            _settings.Role = (UserRole)Array.FindIndex(_roles, r => r.Checked);
            if (!_updatePolicy.FromPolicyFile) _settings.AutoUpdate = _autoUpdate.Checked;
            if (choice != AiChoice.None && _key.Text.Trim().Length > 0) _settings.SetApiKey(_key.Text.Trim());
            try
            {
                _settings.Save();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Notify.Error(this, "House Style couldn't save your settings.", "Check that the settings folder isn't read-only, then try again.", ex, "Settings");
            }
        }
    }
}
