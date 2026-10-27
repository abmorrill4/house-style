using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using B3.PluginAPIKit;
using FlareAI.Core;
using FlareAI.Core.Editing;
using FlareAI.Core.Actions;
using FlareAI.Core.Llm;
using FlareAI.Core.Settings;
using FlareAI.Core.Style;
using FlareAI.Flare.UI;

namespace FlareAI.Flare
{
    /// <summary>
    /// Entry point Flare discovers in Flare.app\Plugins\FlareAI. Adds a "House Style" ribbon tab.
    /// Everything interesting lives in FlareAI.Core; this class only wires Flare to it.
    /// </summary>
    public class Plugin : IPlugin
    {
        private IHost _host;
        private INavContext _nav;
        private bool _activated;
        private UpdateFlow _updates;

        public bool IsActivated { get { return _activated; } }
        public string GetVersion() { return Version; }

        /// <summary>The release version stamped at build time (package.ps1 -Version), without the "+commit" suffix.</summary>
        internal static string Version
        {
            get
            {
                var a = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    typeof(Plugin).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                var v = a != null ? a.InformationalVersion : typeof(Plugin).Assembly.GetName().Version.ToString();
                var plus = v.IndexOf('+');
                return plus > 0 ? v.Substring(0, plus) : v;
            }
        }
        public string GetAuthor() { return "Arthur Morrill"; }
        public string GetDescription() { return "Lint topics against your style guide and rewrite text with AI. Changes arrive as tracked changes."; }
        public string GetName() { return "House Style"; }

        public void Initialize(IHost host)
        {
            _host = host; // docs: do nothing else here
        }

        public void Execute()
        {
            try
            {
                E2EHook.IsolateIfRequested();
                Diagnostics.Enabled = PluginSettings.Load().WriteDiagnostics;
                Diagnostics.Log("House Style starting in " + _host.GetName() + " (" + _host.GetCompany() + "), plugin dir " + PluginDirectory);
                _nav = _host.GetNavContext();
                RefreshTeam();
                _role = CurrentRole();
                GettingStarted.Owner = () => _nav.GetParentForm();
                GettingStarted.WatchTour();
                CreateRibbon();
                CreateToolStrip();
                try { _host.GetEditorContext().DocumentSwitched += OnDocumentSwitched; } catch (Exception ex) { Diagnostics.Log("subscribe", ex); }
                try { _host.GetCompilerContext().CompileComplete += OnCompileComplete; } catch (Exception ex) { Diagnostics.Log("subscribe compile", ex); }
                _editor = new EditorIntegration(_host, LintRunner, () => _nav.GetParentForm(), WithSetup(OnLintTopic), WithSetup(OnRewrite), WithSetup(OnAsk), WithSetup(OnLintProject));
                try { _host.GetCompilerContext().CompileStart += OnCompileStart; } catch (Exception ex) { Diagnostics.Log("subscribe compile start", ex); }
                _editor.AttachAll();
                _activated = true;
                // Last update's result / What's new, then the daily update check (background; per-user install only).
                _updates = new UpdateFlow(() => _nav.GetParentForm(), PluginDirectory, Version);
                try { _updates.Start(); } catch (Exception ex) { Diagnostics.Log("updates", ex); }
                HookDispatcher();
                E2EHook.StartIfRequested(id => HandlerFor(CommandById(id)), path => OpenAdapter(path) != null, () => _nav.GetParentForm(), ActiveAdapter);
                Welcome();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Execute", ex);
                Notify.Error(Owner, "House Style didn't start.", "Restart Flare. If it happens again, choose Copy details and send them to support.", ex);
            }
        }

        public void Stop()
        {
            try { _host.GetEditorContext().DocumentSwitched -= OnDocumentSwitched; } catch (Exception) { }
            try { _host.GetCompilerContext().CompileComplete -= OnCompileComplete; } catch (Exception) { }
            try { _host.GetCompilerContext().CompileStart -= OnCompileStart; } catch (Exception) { }
            try { if (_dispatcher != null) _dispatcher.UnhandledException -= OnDispatcherException; } catch (Exception) { }
            _style = null;
            try { _host.Dispose(); } catch (Exception ex) { Diagnostics.Log("Stop", ex); }
            _activated = false;
        }

        // ---- unhandled errors on the UI thread (issue #124) -------------------------------------------------------

        private System.Windows.Threading.Dispatcher _dispatcher;

        /// <summary>
        /// The one process-wide hook House Style adds: WPF's dispatcher (Flare's ribbon runs on it). Its event lets a
        /// handler leave an exception unhandled, so House Style takes only exceptions thrown by its own code and leaves
        /// Flare's (and other plugins') to Flare. WinForms' Application.ThreadException is not hooked: a handler there
        /// handles every exception in the process, so it can't let Flare's pass. Our WinForms entry points are wrapped
        /// by <see cref="Safe"/> instead.
        /// </summary>
        private void HookDispatcher()
        {
            try
            {
                _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                _dispatcher.UnhandledException += OnDispatcherException;
            }
            catch (Exception ex) { Diagnostics.Log("dispatcher hook", ex); }
        }

        private void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.Handled || !FlareAI.Core.Feedback.ErrorKinds.ThrownByUs(e.Exception)) return;   // Flare's own: not ours to handle
                e.Handled = true;
                Diagnostics.Log("Unhandled (dispatcher)", e.Exception);
                if (FlareAI.Core.Feedback.ErrorKinds.IsBug(e.Exception))
                    ErrorReporter.Show(Owner, "House Style couldn't finish what it was doing.", e.Exception, null);
                else
                    Notify.PlainError(Owner, "House Style couldn't finish what it was doing.", e.Exception.Message, e.Exception);
            }
            catch (Exception ex) { Diagnostics.Log("dispatcher exception", ex); }
        }

        /// <summary>Flare's main window as a control, for reports from background work (null if it isn't available).</summary>
        private Form OwnerForm
        {
            get { try { return _nav == null ? null : _nav.GetParentForm(); } catch (Exception) { return null; } }
        }

        /// <summary>Help &gt; Report a Problem, and the same button in Settings.</summary>
        private void OnReportProblem(IWin32Window owner)
        {
            using (var f = new ReportProblemForm(FeedbackService.Context())) f.ShowDialog(owner);
        }

        // ---- ribbon ------------------------------------------------------------------

        private UserRole _role;

        /// <summary>A team style guide is configured (read at Flare start, like the role): writers then get My Requests.</summary>
        private static bool TeamGuide
        {
            get { try { return PluginSettings.Load().HasTeamGuide(PluginDirectory); } catch (Exception) { return false; } }
        }

        /// <summary>
        /// Writer or owner: Owner for anyone the team guide's owners.txt lists; otherwise the writer's choice in setup or Settings.
        /// </summary>
        private static UserRole CurrentRole()
        {
            var team = Team.Current;
            return Roles.Effective(PluginSettings.Load(), team != null && team.IsListedOwner);
        }

        /// <summary>
        /// The ribbon for the writer's role, built once at Flare start. Flare's plugin API can add tabs, groups and buttons
        /// but has no call to remove or hide them, so a role change in Settings applies when Flare restarts (Settings says so).
        /// </summary>
        private void CreateRibbon()
        {
            // Per the Ribbon Example: ribbon.AddNewRibbonTab(label, keyTip), then tab.AddNewRibbonGroup(label), then group.AddRibbonButton(RibbonControlData)
            IRibbon ribbon = _nav.GetRibbon();
            IRibbonTab tab = ribbon.AddNewRibbonTab(Notify.Product, "Q");
            var groups = new Dictionary<string, IRibbonGroup>();
            Diagnostics.Log("Ribbon for role " + _role + (TeamGuide ? " with a team guide" : ""));
            foreach (var c in Commands.Ribbon(_role, TeamGuide))
            {
                IRibbonGroup group;
                if (!groups.TryGetValue(c.Group, out group)) groups[c.Group] = group = tab.AddNewRibbonGroup(c.Group);
                if (c == Commands.Help && AddHelpMenu(group)) continue;
                var button = group.AddRibbonButton(Button(c));
                try { if (button != null) button.ToolTip = c.Tooltip; } catch (Exception ex) { Diagnostics.Log("ribbon tooltip " + c.Id, ex); }
            }
        }

        /// <summary>
        /// Help as a split button: the button opens the user guide; its menu has What's New and Start the Tour Again.
        /// Called by reflection (IRibbonGroup.AddRibbonSplitMenuButton and IRibbonMenu.AddMenuItem are in Flare's API but
        /// not in the compile-only stubs). False when Flare can't make it; then Help is a plain button.
        /// </summary>
        private bool AddHelpMenu(IRibbonGroup group)
        {
            try
            {
                var c = Commands.Help;
                var add = typeof(IRibbonGroup).GetMethod("AddRibbonSplitMenuButton", new[] { typeof(string), typeof(System.Windows.Input.ICommand), typeof(object), typeof(string), typeof(RibbonIconSize), typeof(string), typeof(string), typeof(string) });
                if (add == null) return false;
                var menu = add.Invoke(group, new object[] { c.Label, new DelegateCommand(HandlerFor(c)), null, IconPath(c.Icon + ".png"), RibbonIconSize.Large, c.Label, c.Tooltip, c.KeyTip });
                if (menu == null) return false;
                System.Reflection.MethodInfo item = null;
                foreach (var i in menu.GetType().GetInterfaces())
                    if (i.Name == "IRibbonMenu") item = i.GetMethod("AddMenuItem", new[] { typeof(string), typeof(System.Windows.Input.ICommand), typeof(object), typeof(string) });
                if (item == null) item = menu.GetType().GetMethod("AddMenuItem", new[] { typeof(string), typeof(System.Windows.Input.ICommand), typeof(object), typeof(string) });
                if (item == null) { Diagnostics.Log("Help menu: no AddMenuItem"); return true; }
                foreach (var m in Commands.HelpMenu)
                    item.Invoke(menu, new object[] { m.Label, new DelegateCommand(HandlerFor(m)), null, IconPath(m.Icon + "-16.png") });
                return true;
            }
            catch (Exception ex) { Diagnostics.Log("Help menu", ex); return false; }
        }

        /// <summary>
        /// Same actions for Flare's Tool Strip UI mode, where the ribbon tab is invisible: a "House Style" menu and toolbar.
        /// Failures are logged only — the ribbon is the primary surface.
        /// </summary>
        private void CreateToolStrip()
        {
            try
            {
                var menu = _nav.CreateToolStripMenu(Notify.Product, "Q");
                foreach (var c in Commands.Menu(_role, TeamGuide))
                {
                    if (c == null) { menu.AddSeparator(); continue; }
                    menu.AddMenuItem(c.Label, new DelegateCommand(HandlerFor(c)), null, IconPath(c.Icon + "-16.png"), c.Label, c.Tooltip, c.KeyTip);
                }

                var bar = _nav.CreateCustomToolBar(Notify.Product);
                foreach (var c in Commands.Toolbar)
                    bar.AddButton(c.Label, new DelegateCommand(HandlerFor(c)), null, IconPath(c.Icon + "-16.png"), RibbonIconSize.Small, c.Label, c.Tooltip);
            }
            catch (Exception ex) { Diagnostics.Log("Tool Strip entries", ex); }
        }

        private RibbonControlData Button(CommandInfo c)
        {
            return new RibbonControlData
            {
                Label = c.Label, KeyTip = c.KeyTip, Command = new DelegateCommand(HandlerFor(c)),
                LargeImage = Image(c.Icon + ".png"), SmallImage = Image(c.Icon + "-16.png")
            };
        }

        /// <summary>The handler for a command in the table (ribbon, Tool Strip and right-click all call the same one).</summary>
        private Action HandlerFor(CommandInfo c)
        {
            var raw = RawHandlerFor(c);
            // Settings, Help and the like open as they are; every other command offers first-run setup once.
            var plain = c == Commands.Settings || c == Commands.Diagnostics || c == Commands.Help || c == Commands.WhatsNew || c == Commands.Tour || c == Commands.ReportProblem;
            return Safe.Wrap(c.Label, plain ? raw : WithSetup(raw));
        }

        private Action RawHandlerFor(CommandInfo c)
        {
            if (c == Commands.LintTopic) return OnLintTopic;
            if (c == Commands.LintRulesOnly) return () => OnChooseChecks(true);
            if (c == Commands.LintRulesAndAi) return () => OnChooseChecks(false);
            if (c == Commands.Rewrite) return OnRewrite;
            if (c == Commands.Ask) return OnAsk;
            if (c == Commands.LintProject) return OnLintProject;
            if (c == Commands.ShowIssues) return () => { if (!PanelHost.Reopen(_nav.GetParentForm())) OnLintTopic(); };
            if (c == Commands.TopicMetadata) return OnAssist;
            if (c == Commands.TestRules) return OnValidateGuide;
            if (c == Commands.Rules) return OnStyleRules;
            if (c == Commands.RuleDecisions) return () => OnTuneGuide(null);
            if (c == Commands.MyRequests) return () => OnTuneGuide(Commands.MyRequests);
            if (c == Commands.ImportRules) return OnBuildGuide;
            if (c == Commands.ExportLlm) return () => OnExportLlm(Owner);
            if (c == Commands.Settings) return OnSettings;
            if (c == Commands.Diagnostics) return () => OnDiagnostics(Owner);
            if (c == Commands.WhatsNew) return () => UpdateFlow.ShowAll(Owner, PluginDirectory, Version);
            if (c == Commands.Help) return OpenUserGuide;
            if (c == Commands.Tour) return StartTour;
            if (c == Commands.ReportProblem) return () => OnReportProblem(Owner);
            throw new ArgumentException("No handler for command " + c.Id);
        }

        private static CommandInfo CommandById(string id)
        {
            foreach (var c in Commands.All) if (c.Id == id) return c;
            throw new ArgumentException("No command " + id);
        }

        private void OpenUserGuide()
        {
            try { System.Diagnostics.Process.Start(Commands.UserGuideUrl); }
            catch (Exception ex) { Diagnostics.Log("open user guide", ex); Notify.Info(Owner, "Open " + Commands.UserGuideUrl + " in your browser.", Commands.Help.Label); }
        }

        /// <summary>Owner for dialogs: Flare's main window (null if it isn't available).</summary>
        private IWin32Window Owner
        {
            get { try { return _nav == null ? null : _nav.GetParentForm(); } catch (Exception) { return null; } }
        }

        /// <summary>Full path of a bundled icon (Icons\*.png, made by scripts\make-icons.ps1), or null if missing.</summary>
        private string IconPath(string file)
        {
            var path = Path.Combine(PluginDirectory, Path.Combine("Icons", file));
            return File.Exists(path) ? path : null;
        }

        private System.Windows.Media.ImageSource Image(string file)
        {
            var path = IconPath(file);
            if (path == null) return null;
            try { return new System.Windows.Media.Imaging.BitmapImage(new Uri(path)); }
            catch (Exception ex) { Diagnostics.Log("icon " + file, ex); return null; }
        }

        // ---- actions -----------------------------------------------------------------

        /// <summary>Lint Topic with the remembered checks (the ribbon button, right-click, Reopen Issues with no pane).</summary>
        private void OnLintTopic() { LintTopicWith(LintChecksChoice.IsRulesOnly(PluginSettings.Load().LintChecks)); }

        /// <summary>Rules Only or Rules and AI Review from Lint Topic's menu: remembered, then linted that way. The choice
        /// goes straight into the lint, so a failed save can't change which checks run.</summary>
        private void OnChooseChecks(bool rulesOnly)
        {
            LintChecksChoice.TrySave(rulesOnly ? LintChecksChoice.Rules : LintChecksChoice.RulesAndAi, v =>
            {
                var s = PluginSettings.Load(); s.LintChecks = v; s.Save();
            });
            ShowLintChoice(rulesOnly);   // order 6 fills this in; add it now as an empty private method
            LintTopicWith(rulesOnly);
        }

        private void LintTopicWith(bool rulesOnly) { RunAction(Commands.LintTopic.Label, ActionKind.CheckStyle, null, rulesOnly); }
        private void ShowLintChoice(bool rulesOnly) { }

        private void OnRewrite() { RunAction(Commands.Rewrite.Label, ActionKind.Rewrite, null); }

        private void OnAsk()
        {
            using (var f = new AskForm())
            {
                if (f.ShowDialog() != DialogResult.OK || string.IsNullOrEmpty(f.Instruction)) return;
                RunAction(Commands.Ask.Label, ActionKind.Ask, f.Instruction);   // title of the result window
            }
        }

        /// <summary>Lints every topic and snippet in the active topic's project (no AI cost) into the issues pane.</summary>
        private void OnLintProject()
        {
            try
            {
                var adapter = ActiveAdapter();
                var files = ActionRunner.ProjectFiles(adapter == null ? null : adapter.SourcePath);
                if (adapter == null || files.Count == 0)
                {
                    Notify.Info(Owner, "Open a topic in the project first. Lint Project lints every topic and snippet in that project.", Commands.LintProject.Label);
                    return;
                }
                var runner = NewRunner(adapter, true, true);
                if (runner == null) return;
                var root = Path.GetDirectoryName(files[0]);
                while (root != null && Directory.GetFiles(root, "*.flprj").Length == 0) root = Path.GetDirectoryName(root);
                var name = root == null ? "" : Path.GetFileName(root);
                var panel = FindingsPanel.ForProject(runner, adapter, () => NewRunner(adapter, true, true), OpenAdapter, files, name);
                panel.FindOpen = FindOpenAdapter;
                PanelHost.Show(panel, _nav.GetParentForm());
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Lint project", ex);
                Notify.Failed(Owner, "lint the project", ex, Commands.LintProject.Label);
            }
        }

        private void OnSettings()
        {
            bool runSetup, tour;
            using (var f = new SettingsForm(PluginDirectory, _updates == null ? null : (Func<string>)_updates.WhyOff, _role,
                                            owner => OnExportLlm(owner), owner => OnDiagnostics(owner), owner => OnReportProblem(owner)))
            {
                if (f.ShowDialog(Owner) == DialogResult.OK) RefreshTeam();
                runSetup = f.RunSetupRequested;
                tour = f.TourRequested;
            }
            if (runSetup) RunSetup();
            if (tour) StartTour();
        }

        private bool _setupOffered;

        /// <summary>
        /// Welcome on first start: with no settings file yet, setup opens by itself once Flare's window is up, before any
        /// click. Also remembers when House Style first started (the checklist shows for the first week).
        /// </summary>
        private void Welcome()
        {
            try { var o = Onboarding.Load(); if (o.Started(DateTime.UtcNow)) o.Save(); }
            catch (Exception ex) { Diagnostics.Log("onboarding start", ex); }
            if (!FirstRun.IsNeeded()) return;
            var form = _nav.GetParentForm();
            if (form == null) return;
            Action show = () =>
            {
                if (_setupOffered || !FirstRun.IsNeeded()) return;
                _setupOffered = true;
                Safe.Run("Welcome", RunSetup);
            };
            if (form.IsHandleCreated && form.Visible) form.BeginInvoke(show);
            else form.Shown += (s, e) => form.BeginInvoke(show);
        }

        /// <summary>
        /// The command, preceded once by first-run setup when there is no settings file yet (setup didn't open at start, or
        /// the writer closed Flare's window before it did). Never twice in a session even if the settings couldn't be saved.
        /// </summary>
        private Action WithSetup(Action command)
        {
            return () =>
            {
                if (!_setupOffered && FirstRun.IsNeeded())
                {
                    _setupOffered = true;
                    try { RunSetup(); }
                    catch (Exception ex) { Diagnostics.Log("first-run setup", ex); }
                }
                command();
            };
        }

        /// <summary>
        /// The four setup steps; finishing or closing saves the settings, then the team and guide are reloaded. The first
        /// time setup is finished, the guided first lint follows.
        /// </summary>
        private void RunSetup()
        {
            bool completed;
            using (var f = new SetupForm()) { f.ShowDialog(Owner); completed = f.Completed; }
            Diagnostics.Enabled = PluginSettings.Load().WriteDiagnostics;
            RefreshTeam();
            if (!completed) return;
            var o = Onboarding.Load();
            if (o.TourOffered) return;
            StartTour();
        }

        /// <summary>
        /// The guided first lint: the writer picks the sample project or the open topic (or Skip); then five steps in the
        /// issues pane's hint strip. Started after the first setup, and from Help or Settings.
        /// </summary>
        private void StartTour()
        {
            var active = ActiveAdapter();
            var topicOpen = active != null && active.IsEditableTopic;
            TourChoice choice;
            using (var f = new TourStartForm(topicOpen)) choice = f.ShowDialog(Owner) == DialogResult.OK ? f.Choice : TourChoice.Skip;
            var o = Onboarding.Load();
            o.TourOffered = true;
            o.Save();
            if (choice == TourChoice.Skip) return;
            if (choice == TourChoice.Sample)
            {
                string topic;
                try { topic = PracticeProject.Prepare(PluginDirectory, PracticeProject.DefaultFolder); }
                catch (Exception ex)
                {
                    Diagnostics.Log("practice project", ex);
                    Notify.Error(Owner, "House Style couldn't copy the practice project.", "Try again from Help, or practice on a topic of your own.", ex, Commands.Tour.Label);
                    return;
                }
                // Flare's plugin API opens files, not projects: the practice topic opens on its own, and Lint Project
                // still finds its project (the .flprj next to it).
                if (OpenAdapter(topic) == null)
                {
                    Notify.Warn(Owner, "House Style couldn't open the practice topic. Open " + topic + " in Flare, then click Lint Topic.", Commands.Tour.Label);
                }
            }
            var tour = GuidedTour.Start(choice == TourChoice.Sample);
            var form = _nav.GetParentForm();
            if (form != null) Toast.Show(form, tour.Hint, "Skip Tour", GettingStarted.EndTour);
        }

        /// <summary>
        /// Who is writing, who owns the guide, where shared feedback goes; then (in the background) whether the guide
        /// checkout is behind the team's latest.
        /// </summary>
        private void RefreshTeam()
        {
            try
            {
                var settings = PluginSettings.Load();
                FeedbackLog.PersonalPath = Path.Combine(PluginSettings.SettingsDirectory, "feedback.jsonl");
                // With no team guide, rule changes go to the writer's own guide in %APPDATA% (created from the bundled one on
                // first need), never to the plugin folder: standard users can't write under Program Files.
                var team = Team.Load(settings.GuideRoot(PluginDirectory), settings.FeedbackFolder);
                team.SeedFrom = settings.ReadOnlyGuide(PluginDirectory);
                settings.GuideRoots(PluginDirectory);   // upgrades a writer's guide in the old layout (a full copy) before first use
                Team.Current = team;
                PromptBuilder.DefaultOverrideFolder = team.GuideRoot == null ? null : Path.Combine(team.GuideRoot, Path.Combine("FlareAI", "prompts"));
                Diagnostics.Log("Team: writer " + (string.IsNullOrEmpty(team.Writer) ? "not set" : "set") + " guide=" + team.GuideRoot + " strict=" + team.IsStrict + " owner=" + team.IsOwner + " feedback=" + (team.FeedbackFolder ?? "(personal)"));
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    // Read-only detection on load: a create-and-delete probe (shares report attributes unreliably).
                    try { if (team.IsStrict && !team.IsPersonal) Diagnostics.Log("Guide access: " + (team.ProbeGuide() ?? "writable") + (team.AccessNotice == null ? "" : " — " + team.AccessNotice)); }
                    catch (Exception ex) { Diagnostics.Log("guide access", ex); }
                    try { team.Status = Team.ReadStatus(team.GuideRoot, true); if (team.Status != null) Diagnostics.Log(team.Status.ToString()); }
                    catch (Exception ex) { Diagnostics.Log("guide status", ex); }
                });
            }
            catch (Exception ex) { Diagnostics.Log("Team", ex); }
        }

        /// <summary>Meta description, index keywords, synonyms, micro content and glossary candidates for the open topic.</summary>
        private void OnAssist()
        {
            try
            {
                var adapter = ActiveAdapter();
                if (adapter == null || !adapter.IsEditableTopic) { Notify.Info(Owner, "Open a topic in the XML Editor first.", Commands.TopicMetadata.Label); return; }
                var runner = NewRunner(adapter);
                if (runner == null) return;
                using (var f = new AssistForm(runner, adapter)) f.ShowDialog(_nav.GetParentForm());
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Topic metadata", ex);
                Notify.Failed(Owner, "suggest topic metadata", ex, Commands.TopicMetadata.Label);
            }
        }

        // ---- LLM-ready output ------------------------------------------------------------------

        /// <summary>After a build: when enabled, add Markdown + llms.txt to the output that was just built (in the background).</summary>
        private void OnCompileComplete(object sender, EventArgs e)
        {
            try
            {
                var settings = PluginSettings.Load();
                if (!settings.LlmExportAfterBuild) return;
                var adapter = ActiveAdapter();
                var output = NewestOutput(adapter == null ? null : adapter.SourcePath, TimeSpan.FromMinutes(10));
                if (output == null) { Diagnostics.Log("LLM export: no fresh HTML output found after the build"); return; }
                System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { Diagnostics.Log("LLM export after build: " + RunLlmExport(settings, output)); } catch (Exception ex) { Diagnostics.Log("LLM export", ex); } });
            }
            catch (Exception ex) { Diagnostics.Log("CompileComplete", ex); ErrorReporter.Background(OwnerForm, "House Style couldn't add the LLM-ready Markdown after the build.", ex); }
        }

        /// <summary>Exports the newest HTML output of the open project (or a folder the author picks) as Markdown + llms.txt.</summary>
        private void OnExportLlm(IWin32Window owner)
        {
            try
            {
                var settings = PluginSettings.Load();
                var adapter = ActiveAdapter();
                var output = NewestOutput(adapter == null ? null : adapter.SourcePath, TimeSpan.MaxValue);
                using (var dlg = new FolderBrowserDialog { Description = "Choose a built HTML5 or Clean HTML output. House Style adds Markdown for each topic and llms.txt to it.", SelectedPath = output ?? "" })
                {
                    if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                    output = dlg.SelectedPath;
                }
                var summary = RunLlmExport(settings, output);
                Diagnostics.Log("LLM export: " + summary);
                if (Notify.AskYesNo(owner, summary + "\n\nOpen the folder?", Commands.ExportLlm.Label))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + output + "\"");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Export for LLMs", ex);
                Notify.Failed(owner, "export for LLMs", ex, Commands.ExportLlm.Label);
            }
        }

        /// <summary>Turns the built output into Markdown per topic + llms.txt; returns a one-line summary.</summary>
        private static string RunLlmExport(PluginSettings settings, string output)
        {
            var r = FlareAI.Core.Output.LlmExport.Export(output.TrimEnd('\\'));
            return Plural.Of(r.Topics, "topic") + " exported as Markdown to " + r.Markdown + ", with llms.txt and llms-full.txt in " + output + ".";
        }

        /// <summary>The most recently built output folder of the project (Output\user\Target or Output\Target), or null.</summary>
        private static string NewestOutput(string anyProjectPath, TimeSpan maxAge)
        {
            if (string.IsNullOrEmpty(anyProjectPath)) return null;
            var dir = Path.GetDirectoryName(anyProjectPath);
            while (dir != null && Directory.GetFiles(dir, "*.flprj").Length == 0) dir = Path.GetDirectoryName(dir);
            if (dir == null || !Directory.Exists(Path.Combine(dir, "Output"))) return null;
            DirectoryInfo best = null;
            foreach (var d in new DirectoryInfo(Path.Combine(dir, "Output")).GetDirectories("*", SearchOption.AllDirectories))
            {
                if (d.Name.Equals("Temporary", StringComparison.OrdinalIgnoreCase) || d.FullName.IndexOf("\\Temporary\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (d.GetFiles("*.htm").Length == 0 && d.GetFiles("*.html").Length == 0) continue;     // a target root holds Default.htm / index files
                if (d.Parent != null && best != null && d.FullName.StartsWith(best.FullName + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || d.LastWriteTimeUtc > best.LastWriteTimeUtc) best = d;
            }
            if (best == null || DateTime.UtcNow - best.LastWriteTimeUtc > maxAge) return null;
            // Walk up to the target folder (the one directly under Output or Output\user).
            var root = new DirectoryInfo(Path.Combine(dir, "Output")).FullName;
            var t = best;
            while (t.Parent != null && t.Parent.Parent != null && !t.Parent.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)
                   && !t.Parent.Parent.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) t = t.Parent;
            return t.FullName;
        }

        /// <summary>Proposes style-guide rules from the open project's content and from notes.</summary>
        private void OnBuildGuide()
        {
            try
            {
                var settings = PluginSettings.Load();
                var style = StyleService(settings);
                var adapter = ActiveAdapter();
                var project = adapter == null ? null : adapter.SourcePath;
                using (var f = new BuildGuideForm(Team.Current, style, project,
                           () => adapter == null ? NewRunnerWithoutTopic(settings, style, null) : NewRunner(adapter, false), OpenAdapter))
                    f.ShowDialog(_nav.GetParentForm());
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Import rules", ex);
                Notify.Failed(Owner, "import rules", ex, Commands.ImportRules.Label);
            }
        }

        /// <summary>The owner's report: what the team's review decisions say about each rule.</summary>
        private void OnTuneGuide(CommandInfo asCommand)
        {
            try
            {
                var settings = PluginSettings.Load();
                var style = StyleService(settings);
                var adapter = ActiveAdapter();
                var project = settings.UseProjectLayer && adapter != null ? adapter.SourcePath : null;
                // Turned-off rules: the guides in effect (the read-only one in the plugin folder lists its defaults, which the
                // writer turns back on with a row in their own guide) and the open project.
                var pluginDir = PluginDirectory;
                // A team guide gets the bundled defaults too (StyleEngine layers them before it), so they are listed first.
                Func<IList<string>> roots = () =>
                {
                    var list = settings.GuideRoots(pluginDir);
                    var hasBundled = false;
                    foreach (var r in list) hasBundled |= PluginSettings.IsUnder(r, pluginDir);
                    if (!hasBundled) list.Insert(0, PluginSettings.BundledGuide(pluginDir));
                    return list;
                };
                Func<string, bool> isDefault = r => PluginSettings.IsUnder(r, pluginDir);
                var openProject = adapter != null ? adapter.SourcePath : null;
                // My Requests is the same window in its writer view; it also offers Import Rules, which sends add-rule requests.
                var mine = asCommand == Commands.MyRequests;
                using (var f = new TuneGuideForm(Team.Current, style, project, OpenAdapter, doc => NewRunner(doc as FlareDocumentAdapter, false), roots, openProject, isDefault,
                                                 owner => ShowStyleRules(owner, style, project, openProject),
                                                 mine ? Commands.MyRequests.Label : null, mine ? (Action)OnBuildGuide : null))
                    f.ShowDialog(_nav.GetParentForm());
            }
            catch (Exception ex)
            {
                var label = (asCommand ?? Commands.RuleDecisions).Label;
                Diagnostics.Log(label, ex);
                Notify.Failed(Owner, "open " + label, ex, label);
            }
        }

        /// <summary>Every rule of the style guide, with search and filters, to turn rules off or back on.</summary>
        private void OnStyleRules()
        {
            try
            {
                var settings = PluginSettings.Load();
                var style = StyleService(settings);
                var adapter = ActiveAdapter();
                var project = settings.UseProjectLayer && adapter != null ? adapter.SourcePath : null;   // the guide the checks use
                ShowStyleRules(_nav.GetParentForm(), style, project, adapter != null ? adapter.SourcePath : null);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Rules", ex);
                Notify.Failed(Owner, "show the rules", ex, Commands.Rules.Label);
            }
        }

        private void ShowStyleRules(IWin32Window owner, StyleEngine style, string project, string openProject)
        {
            var pluginDir = PluginDirectory;
            using (var f = new StyleRulesForm(Team.Current, style, project, openProject, r => PluginSettings.IsUnder(r, pluginDir)))
                f.ShowDialog(owner);
        }

        /// <summary>Tests the layered style guide (tables against examples; optionally prose rules against the AI).</summary>
        private void OnValidateGuide()
        {
            try
            {
                var settings = PluginSettings.Load();
                var style = StyleService(settings);
                var adapter = ActiveAdapter();
                var project = settings.UseProjectLayer && adapter != null ? adapter.SourcePath : null;
                using (var f = new GuideReportForm(style, () => adapter == null ? NewRunnerWithoutTopic(settings, style, project) : NewRunner(adapter), project))
                    f.ShowDialog(_nav.GetParentForm());
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Test rules", ex);
                Notify.Failed(Owner, "test the rules", ex, Commands.TestRules.Label);
            }
        }

        private ActionRunner NewRunnerWithoutTopic(PluginSettings settings, StyleEngine style, string project)
        {
            ILlmClient llm;
            try { llm = LlmClientFactory.Create(settings); }
            catch (LlmException lex) { Notify.Warn(Owner, lex.Message); return null; }
            return new ActionRunner(llm, PromptBuilder.For(style, project), settings.MaxTokens, style, project, RoutingFor(settings))
            { Parallelism = settings.Parallelism > 0 ? settings.Parallelism : 6 };
        }

        private void OnDiagnostics(IWin32Window owner)
        {
            try
            {
                var adapter = ActiveAdapter();
                var sb = new System.Text.StringBuilder();
                // About: version, automatic updates, last check (issue #101).
                sb.AppendLine(_updates != null ? _updates.Describe() : "House Style " + Version);
                sb.AppendLine("Doc: " + (adapter == null ? "(none)" : adapter.SourcePath));
                if (adapter != null)
                {
                    sb.AppendLine("Editable topic: " + adapter.IsEditableTopic);
                    sb.AppendLine("Selection text: \"" + adapter.SelectionText + "\"");
                    sb.AppendLine("Selection nodes:");
                    foreach (var n in adapter.SelectionNodes) sb.AppendLine("  " + n.NodeType + " " + n.Name + " parent=" + (n.ParentNode == null ? "-" : n.ParentNode.Name));
                    if (adapter.IsEditableTopic)
                    {
                        sb.AppendLine("Blocks BlockSelector would pick:");
                        var picked = Core.Editing.BlockSelector.SelectBlocks(adapter.Document, adapter.SelectionNodes);
                        foreach (var b in picked)
                            sb.AppendLine("  <" + b.Name + "> " + Shorten(b.InnerText, 60));
                        // How Flare holds pending tracked changes in memory (for the tracked-change readers).
                        var ns = new System.Xml.XmlNamespaceManager(adapter.Document.NameTable);
                        ns.AddNamespace("MadCap", Core.Editing.BlockSelector.MadCapNs);
                        var data = adapter.Document.SelectSingleNode("//MadCap:changeData", ns);
                        Diagnostics.LogContent("DIAG changeData: " + (data == null ? "(none)" : Shorten(data.OuterXml, 1500)));
                        if (picked.Count > 0) Diagnostics.LogContent("DIAG first block XML: " + Shorten(picked[0].OuterXml, 3000));
                    }
                }
                var settingsNow = PluginSettings.Load();
                sb.AppendLine().Append(RoutingFor(settingsNow).Describe());
                var usage = Usage.Snapshot();
                if (usage.Count > 0)
                {
                    sb.AppendLine("AI use this session:");
                    foreach (var u in usage)
                        sb.AppendLine("  " + u.Task + " · " + u.Model + ": " + Plural.Of(u.Calls, "call") + ", " + u.Input + " in (" + u.CacheRead + " cached) / " + u.Output + " out, ~$" + u.Usd.ToString("0.000") + ", " + u.Seconds.ToString("0") + " s");
                }
                // The selection and blocks quote the topic: the content log only, never diag.log (which can go with a report).
                Notify.Report(owner, Commands.Diagnostics.Label, sb + "\n(The same text is in the log at " + Diagnostics.ContentLogPath + ".)");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Diagnostics", ex);
                Notify.Failed(owner, "collect diagnostics", ex, Commands.Diagnostics.Label);
            }
        }

        private void RunAction(string title, ActionKind kind, string instruction, bool rulesOnly = false)
        {
            try
            {
                var adapter = ActiveAdapter();
                if (adapter == null || !adapter.IsEditableTopic)
                {
                    Notify.Info(Owner, "Open a topic in the XML Editor first.", title);
                    return;
                }
                var runner = NewRunner(adapter, true, kind == ActionKind.CheckStyle);
                if (runner == null) return;
                if (kind == ActionKind.CheckStyle)
                {
                    // Docked (or floating) and modeless: the author works through the list while editing.
                    ShowFindings(adapter, runner, true, rulesOnly);
                    return;
                }
                using (var f = new ResultForm(title, runner, adapter, kind, instruction))
                {
                    if (f.ShowDialog() != DialogResult.OK) return;
                }
                GettingStarted.Mark(ChecklistItem.AiRewrite);
                GettingStarted.Note(_nav.GetParentForm(), FirstTimeNote.FirstAiRewrite);
            }
            catch (Exception ex)
            {
                Diagnostics.Log(title, ex);
                Notify.Failed(Owner, "finish \"" + title + "\"", ex, title);
            }
        }

        /// <summary>Fresh settings + LLM client per action; the docstyle server process is shared. Null if not configured.</summary>
        private ActionRunner NewRunner(FlareDocumentAdapter adapter)
        {
            return NewRunner(adapter, true);
        }

        /// <param name="projectLayer">False for style-guide topics: the guide project is already a layer.</param>
        /// <param name="lintOnly">True for work that needs no AI (Lint Project): runs without a key; AI buttons then report why.</param>
        private ActionRunner NewRunner(FlareDocumentAdapter adapter, bool projectLayer, bool lintOnly = false)
        {
            if (adapter == null) return null;
            var topicPath = adapter.SourcePath;
            var settings = PluginSettings.Load();
            ILlmClient llm;
            try { llm = LlmClientFactory.Create(settings); }
            catch (LlmException lex)
            {
                if (lintOnly) llm = new UnavailableClient(lex.Message);
                else
                {
                    Notify.Warn(Owner, lex.Message);
                    OnSettings();
                    return null;
                }
            }
            var style = StyleService(settings);
            var project = settings.UseProjectLayer && projectLayer ? topicPath : null;
            return new ActionRunner(llm, PromptBuilder.For(style, project), settings.MaxTokens, style, project, RoutingFor(settings))
            {
                Parallelism = settings.Parallelism > 0 ? settings.Parallelism : 6,
                VariableValue = adapter.VariableValue
            };
        }

        private static Routing RoutingFor(PluginSettings settings)
        {
            return settings.Routing(Team.Current == null ? null : Team.Current.GuideRoot);
        }

        private StyleEngine _style;
        private string _styleKey;

        /// <summary>
        /// The in-process style guide and linter; rebuilt when the configured guide projects change. It asks for the guide
        /// roots before each use (the writer's own guide joins once created) and reloads when any guide file changes.
        /// </summary>
        private StyleEngine StyleService(PluginSettings settings)
        {
            var key = settings.StyleLayers ?? "";
            if (_style == null || _styleKey != key)
            {
                var pluginDir = PluginDirectory;
                // A team guide that can't be reached: check with its last saved copy (#246); with no copy, with the bundled
                // guide plus the writer's own changes. Either way, say so. Copies refresh in the background after a lint.
                var copies = GuideCopy.ForWriter;
                _style = new StyleEngine(() => settings.GuideRoots(pluginDir), PluginSettings.BundledGuide(pluginDir))
                {
                    Fallback = () => new PluginSettings { StyleLayers = "" }.GuideRoots(pluginDir),
                    PersonalGuide = PluginSettings.UserGuidePath,
                    CopyOf = copies.Find,
                    GuideRead = copies.RefreshInBackground
                };
                var configured = settings.GuideRoots(pluginDir);
                System.Threading.ThreadPool.QueueUserWorkItem(_ => copies.Prune(configured));
                _styleKey = key;
            }
            return _style;
        }

        private static readonly ReviewCache Reviews = new ReviewCache(300, Path.Combine(PluginSettings.SettingsDirectory, "reviews.json"));

        private void ShowFindings(FlareDocumentAdapter adapter, ActionRunner runner, bool aiOnStart, bool rulesOnly)
        {
            PanelHost.Show(new FindingsPanel(runner, adapter, () => NewRunner(adapter, true, true), OpenAdapter, Reviews, aiOnStart) { FindOpen = FindOpenAdapter, OpenSettings = OnSettings, RulesOnly = rulesOnly }, _nav.GetParentForm());
        }

        /// <summary>
        /// While the findings view is open it follows the active topic: instant lint for the new topic, plus the AI
        /// review only if this exact content was reviewed before (cache). Snippet tabs opened by our own fixes don't count.
        /// </summary>
        private EditorIntegration _editor;
        private string _lastTopic;

        private void OnCompileStart(object sender, EventArgs e)
        {
            try
            {
                var active = ActiveAdapter();
                _editor.OnCompileStart(active != null ? active.SourcePath : _lastTopic);
            }
            catch (Exception ex) { Diagnostics.Log("CompileStart", ex); ErrorReporter.Background(OwnerForm, "House Style couldn't lint the project before the build.", ex); }
        }

        /// <summary>A runner for the linter only (live check, check on save, report before build): no API key needed.</summary>
        private ActionRunner LintRunner(FlareDocumentAdapter adapter)
        {
            var settings = PluginSettings.Load();
            var style = StyleService(settings);
            if (style == null) return null;
            var project = settings.UseProjectLayer ? adapter.SourcePath : null;
            return new ActionRunner(null, PromptBuilder.For(style, project), 0, style, project, RoutingFor(settings)) { VariableValue = adapter.VariableValue };
        }

        private void OnDocumentSwitched(object sender, EventArgs e)
        {
            try { if (_editor != null) _editor.AttachAll(); var a = ActiveAdapter(); if (a != null) _lastTopic = a.SourcePath; } catch (Exception ex) { Diagnostics.Log("editor", ex); }
            try
            {
                var current = PanelHost.Current;
                if (current == null || current.IsProjectReport) return;
                var adapter = ActiveAdapter();
                if (adapter == null || !adapter.IsEditableTopic) return;
                var path = adapter.SourcePath ?? "";
                if (string.Equals(path, current.TopicPath, StringComparison.OrdinalIgnoreCase)) return;
                if (path.EndsWith(".flsnp", StringComparison.OrdinalIgnoreCase)) return;
                // Style-guide topics list the very words they ban; following into one (e.g. via Guide ↗) would flood the panel.
                if (path.IndexOf(@"\Resources\StyleGuide\", StringComparison.OrdinalIgnoreCase) >= 0) return;
                var runner = NewRunner(adapter, true, true);
                if (runner != null) ShowFindings(adapter, runner, false, LintChecksChoice.IsRulesOnly(PluginSettings.Load().LintChecks));
            }
            catch (Exception ex) { Diagnostics.Log("DocumentSwitched", ex); ErrorReporter.Background(OwnerForm, "House Style couldn't lint the topic you opened.", ex); }
        }

        /// <summary>Opens (or activates) a file in Flare's editor and wraps it; null if Flare can't open it.</summary>
        private IDocumentAdapter OpenAdapter(string path)
        {
            try
            {
                var ctx = _host.GetEditorContext();
                var wasOpen = false;
                foreach (var d in ctx.GetDocuments())
                {
                    var open = d as IDocument;
                    if (open != null && string.Equals(open.GetSourceUrl(), path, StringComparison.OrdinalIgnoreCase)) { wasOpen = true; break; }
                }
                var doc = ctx.OpenDocument(path);
                return doc == null ? null : new FlareDocumentAdapter(doc) { OpenedByPlugin = !wasOpen };
            }
            catch (Exception ex)
            {
                Diagnostics.Log("open " + path, ex);
                return null;
            }
        }

        /// <summary>The document at <paramref name="path"/> if it is open in Flare (a fresh adapter: its current DOM), else null. Never opens or activates it.</summary>
        private IDocumentAdapter FindOpenAdapter(string path)
        {
            try
            {
                var ctx = _host.GetEditorContext();
                if (ctx == null) return null;
                foreach (var d in ctx.GetDocuments())
                {
                    var open = d as IDocument;
                    if (open != null && string.Equals(open.GetSourceUrl(), path, StringComparison.OrdinalIgnoreCase)) return new FlareDocumentAdapter(open);
                }
            }
            catch (Exception ex) { Diagnostics.Log("find open " + path, ex); }
            return null;
        }

        private FlareDocumentAdapter ActiveAdapter()
        {
            var ctx = _host.GetEditorContext();
            if (ctx == null) return null;
            var doc = ctx.GetActiveDocument();
            return doc == null ? null : new FlareDocumentAdapter(doc);
        }

        private static string PluginDirectory
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        private static string Shorten(string s, int n) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length <= n ? s : s.Substring(0, n) + "…"; }
    }
}
