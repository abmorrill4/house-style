using System.Collections.Generic;
using FlareAI.Core.Settings;

namespace FlareAI.Flare
{
    /// <summary>One command: the same label, tooltip, key tip and icon wherever it appears.</summary>
    internal sealed class CommandInfo
    {
        public string Id, Label, Tooltip, KeyTip, Icon, Group;
        /// <summary>The label in Flare's right-click menu, which also holds Flare's own items.</summary>
        public string ContextLabel { get { return "House Style: " + Label; } }
    }

    /// <summary>
    /// The one place command text lives. The ribbon, the Tool Strip menu and toolbar, the editor's right-click menu,
    /// Settings and the Help menu are all built from this table, so a command cannot be named differently in two places.
    /// Adding a command here (and a handler in Plugin.cs) is all it takes. The ribbon shows the commands for the writer's
    /// role (<see cref="Ribbon"/>); the rest are reached from Settings or the Help menu.
    /// </summary>
    internal static class Commands
    {
        public const string GroupLint = "Lint", GroupAi = "AI", GroupGuide = "Style Guide", GroupOptions = "Options";

        public static readonly CommandInfo LintTopic = new CommandInfo
        {
            Id = "check", Label = "Lint Topic", KeyTip = "L", Icon = "check", Group = GroupLint,
            Tooltip = "Lint this topic against your style guide."
        };
        public static readonly CommandInfo LintProject = new CommandInfo
        {
            Id = "project", Label = "Lint Project", KeyTip = "P", Icon = "project", Group = GroupLint,
            Tooltip = "Lint every topic and snippet in this project."
        };
        public static readonly CommandInfo LintRulesOnly = new CommandInfo
        {
            Id = "rulesonly", Label = "Rules Only", KeyTip = "N", Icon = "check", Group = GroupLint,
            Tooltip = "Lint this topic against your style guide rules only. No AI is used."
        };
        public static readonly CommandInfo LintRulesAndAi = new CommandInfo
        {
            Id = "rulesai", Label = "Rules and AI Review", KeyTip = "V", Icon = "check", Group = GroupLint,
            Tooltip = "Lint this topic against your style guide rules and the AI review."
        };
        /// <summary>Added to Lint Topic's tooltip, after a space, while Rules Only is the remembered choice.</summary>
        public const string RulesOnlyNote = "Rules Only is on. Select the arrow to add the AI review.";
        public static readonly CommandInfo ShowIssues = new CommandInfo
        {
            Id = "issues", Label = "Reopen Issues", KeyTip = "E", Icon = "check", Group = GroupLint,
            Tooltip = "Reopen the issues from your last lint."
        };
        public static readonly CommandInfo Rewrite = new CommandInfo
        {
            Id = "rewrite", Label = "Rewrite", KeyTip = "R", Icon = "rewrite", Group = GroupAi,
            Tooltip = "Rewrite the selected text with AI."
        };
        public static readonly CommandInfo Ask = new CommandInfo
        {
            Id = "ask", Label = "Ask AI", KeyTip = "A", Icon = "ask", Group = GroupAi,
            Tooltip = "Tell the AI what to change in the selected text."
        };
        public static readonly CommandInfo Rules = new CommandInfo
        {
            Id = "rules", Label = "Rules", KeyTip = "U", Icon = "rules", Group = GroupLint,
            Tooltip = "View the style guide rules and enable or disable them."
        };
        public static readonly CommandInfo TopicMetadata = new CommandInfo
        {
            Id = "assist", Label = "Topic Metadata", KeyTip = "M", Icon = "assist", Group = GroupAi,
            Tooltip = "Suggest a description, keywords, and other metadata for this topic."
        };
        public static readonly CommandInfo Settings = new CommandInfo
        {
            Id = "settings", Label = "Settings", KeyTip = "S", Icon = "settings", Group = GroupOptions,
            Tooltip = "Change your AI and style guide settings."
        };
        public static readonly CommandInfo Help = new CommandInfo
        {
            Id = "help", Label = "Help", KeyTip = "H", Icon = "help", Group = GroupOptions,
            Tooltip = "Open the House Style user guide."
        };
        public static readonly CommandInfo RuleDecisions = new CommandInfo
        {
            Id = "tune", Label = "Rule Decisions", KeyTip = "D", Icon = "tune", Group = GroupGuide,
            Tooltip = "See how writers handle each rule, and review change requests."
        };
        public static readonly CommandInfo ImportRules = new CommandInfo
        {
            Id = "build", Label = "Import Rules", KeyTip = "I", Icon = "build", Group = GroupGuide,
            Tooltip = "Add rules to your style guide from an existing document."
        };
        public static readonly CommandInfo TestRules = new CommandInfo
        {
            Id = "validate", Label = "Test Rules", KeyTip = "T", Icon = "validate", Group = GroupGuide,
            Tooltip = "Check that each rule catches its own examples."
        };
        // Not on the ribbon: Settings (Advanced tab) and the Help menu open these.
        public static readonly CommandInfo ExportLlm = new CommandInfo
        {
            Id = "llm", Label = "Export for LLMs", KeyTip = "X", Icon = "llm", Group = GroupOptions,
            Tooltip = "Add Markdown for each topic, plus llms.txt, to a built HTML output."
        };
        public static readonly CommandInfo Diagnostics = new CommandInfo
        {
            Id = "diag", Label = "Diagnostics", KeyTip = "G", Icon = "diag", Group = GroupOptions,
            Tooltip = "Show what House Style sees in the open topic, to send to support."
        };
        public static readonly CommandInfo WhatsNew = new CommandInfo
        {
            Id = "whatsnew", Label = "What's New", KeyTip = "W", Icon = "whatsnew", Group = GroupOptions,
            Tooltip = "See what changed in this version of House Style."
        };
        public static readonly CommandInfo Tour = new CommandInfo
        {
            Id = "tour", Label = "Start the Tour Again", KeyTip = "O", Icon = "help", Group = GroupOptions,
            Tooltip = "Practice a first lint step by step."
        };
        public static readonly CommandInfo MyRequests = new CommandInfo
        {
            Id = "requests", Label = "My Requests", KeyTip = "Y", Icon = "tune", Group = GroupLint,
            Tooltip = "See your rule change requests and the owners' answers."
        };
        public static readonly CommandInfo ReportProblem = new CommandInfo
        {
            Id = "report", Label = "Report a Problem…", KeyTip = "B", Icon = "help", Group = GroupOptions,
            Tooltip = "Tell the House Style team what went wrong."
        };

        /// <summary>The user guide on the project site, opened by Help.</summary>
        public const string UserGuideUrl = "https://gethousestyle.com/guide/";

        /// <summary>Every command, in ribbon order.</summary>
        public static readonly IList<CommandInfo> All = new List<CommandInfo>
        {
            LintTopic, LintRulesOnly, LintRulesAndAi, LintProject, ShowIssues, Rules,
            Rewrite, Ask, TopicMetadata,
            RuleDecisions, ImportRules, TestRules,
            Settings, Help,
            ExportLlm, Diagnostics, WhatsNew, Tour, ReportProblem, MyRequests
        };

        /// <summary>The style guide owner's group, added to the ribbon for the Owner role.</summary>
        public static readonly IList<CommandInfo> OwnerOnly = new List<CommandInfo> { RuleDecisions, ImportRules, TestRules };

        /// <summary>The items of the Help menu (Help itself opens the user guide). Report a Problem is also in Settings.</summary>
        public static readonly IList<CommandInfo> HelpMenu = new List<CommandInfo> { WhatsNew, Tour, ReportProblem };

        /// <summary>The items of Lint Topic's menu: the checks Lint Topic runs.</summary>
        public static readonly IList<CommandInfo> LintMenu = new List<CommandInfo> { LintRulesOnly, LintRulesAndAi };

        /// <summary>The ribbon's split buttons and their menu items. The button runs its own command.</summary>
        public static readonly IDictionary<CommandInfo, IList<CommandInfo>> SplitMenus = new Dictionary<CommandInfo, IList<CommandInfo>>
        {
            { LintTopic, LintMenu }, { Help, HelpMenu }
        };

        /// <summary>
        /// The ribbon for a role, in order; groups appear in the order of their first command. A writer gets Lint Topic,
        /// Lint Project, Rules, Rewrite, Ask AI, Topic Metadata, Settings and Help, plus My Requests when a team guide is set;
        /// an owner also gets the Style Guide group (whose Rule Decisions has the requests).
        /// </summary>
        /// <param name="teamGuide">A team style guide is configured: writers send it requests, so they get My Requests.</param>
        public static IList<CommandInfo> Ribbon(UserRole role, bool teamGuide = false)
        {
            var list = new List<CommandInfo> { LintTopic, LintProject, ShowIssues, Rules };
            if (role != UserRole.Owner && teamGuide) list.Add(MyRequests);
            list.AddRange(new[] { Rewrite, Ask, TopicMetadata });
            if (role == UserRole.Owner) list.AddRange(OwnerOnly);
            list.Add(Settings);
            list.Add(Help);
            return list;
        }

        /// <summary>The commands that need an open topic and a selection or paragraph, offered on right-click.</summary>
        public static readonly IList<CommandInfo> Context = new List<CommandInfo> { LintTopic, Rewrite, Ask };

        /// <summary>The Tool Strip menu for a role, in order; null is a separator.</summary>
        public static IList<CommandInfo> Menu(UserRole role, bool teamGuide = false)
        {
            var list = new List<CommandInfo> { LintTopic, LintRulesOnly, LintRulesAndAi, LintProject, ShowIssues, Rules };
            if (role != UserRole.Owner && teamGuide) list.Add(MyRequests);
            list.AddRange(new CommandInfo[] { Rewrite, Ask, TopicMetadata, null });
            if (role == UserRole.Owner) { list.AddRange(OwnerOnly); list.Add(null); }
            list.Add(Settings); list.Add(Help); list.Add(WhatsNew); list.Add(Tour); list.Add(ReportProblem);
            return list;
        }

        /// <summary>The Tool Strip toolbar (the most used commands).</summary>
        public static readonly IList<CommandInfo> Toolbar = new List<CommandInfo> { LintTopic, Rewrite, Ask };
    }
}
