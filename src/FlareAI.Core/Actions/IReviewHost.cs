using System;
using System.Collections.Generic;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>
    /// What a <see cref="ReviewSession"/> needs from the editor and its dialogs: questions to the author, documents opened
    /// by path, and error notices. The Flare pane implements it with WinForms dialogs; tests implement it with answers.
    /// </summary>
    public interface IReviewHost
    {
        /// <summary>A yes/no question. <paramref name="action"/> names the command for the caption (null = the product name).</summary>
        bool Confirm(string message, string action);

        /// <summary>
        /// "Turn off this rule": the author picks scope (team or project), setting and reason, and whether the other rules on
        /// the same words go too. Null when cancelled.
        /// </summary>
        /// <param name="ruleText">The rule as the author reads it.</param>
        /// <param name="canChangeTeamGuide">False when only the project scope is possible.</param>
        /// <param name="personalGuide">True when "team" means the writer's own style guide.</param>
        /// <param name="otherRules">Other rules that flag the same words (readable names), or null.</param>
        RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules);

        /// <summary>
        /// "Request a change": the guide is read-only for this writer (or they aren't an owner), so the change goes to the
        /// owners. The author picks scope (team or this project), the level or new wording, and gives a reason (required).
        /// Null when cancelled.
        /// </summary>
        RuleSettingChoice ChooseRequest(RequestQuestion question);

        /// <summary>A one-field text prompt. Null when cancelled.</summary>
        string AskText(string title, string label, string initial, bool multiline);

        /// <summary>Opens a topic or snippet in the editor (or brings it to the front) and returns its adapter, or null.</summary>
        IDocumentAdapter OpenDocument(string path);

        /// <summary>Shows what a batch will do; the author may untick rules. False when cancelled.</summary>
        bool PreviewBatch(BatchPlan plan);

        /// <summary>
        /// Saves and closes <paramref name="doc"/> when the plugin opened it just for a batch (a project-wide batch must not
        /// leave one tab per file). False when the author had it open (left open, unsaved). Throws when the save fails.
        /// </summary>
        bool SaveAndClose(IDocumentAdapter doc);

        /// <summary>An error the author must see: what happened, what to do, optional details.</summary>
        void ShowError(string whatHappened, string whatToDo, Exception details, string action);
    }

    /// <summary>
    /// A host whose text prompt keeps OK off until the box has text. Optional, so hosts that only ask
    /// <see cref="IReviewHost.AskText"/> stay as they are.
    /// </summary>
    public interface IRequiredTextHost
    {
        /// <summary>A one-field text prompt that needs an answer. Null when cancelled.</summary>
        string AskRequiredText(string title, string label, string initial, bool multiline);
    }

    /// <summary>The author's answer to <see cref="IReviewHost.ChooseRuleSetting"/>.</summary>
    public sealed class RuleSettingChoice
    {
        /// <summary>True: the style guide (team or the writer's own); false: this project only.</summary>
        public bool ForTeam { get; set; }
        /// <summary>The stored token: off, suggestion, warning or error.</summary>
        public string Setting { get; set; }
        public string Reason { get; set; }
        /// <summary>Also change the other rules that flag the same words (same scope, setting and reason).</summary>
        public bool AlsoOthers { get; set; }
        /// <summary>
        /// The checkers (ids such as Company.GlossaryTerms) on the same words to change too, with the same scope, setting and
        /// reason, whether or not <see cref="AlsoOthers"/> is set. Empty by default; never null.
        /// </summary>
        public List<string> AlsoCheckers
        {
            get { return _alsoCheckers; }
            set { _alsoCheckers = value ?? new List<string>(); }
        }
        private List<string> _alsoCheckers = new List<string>();
        /// <summary>A request only: the rule's proposed new wording, or null to keep it.</summary>
        public string Wording { get; set; }
    }

    /// <summary>What <see cref="IReviewHost.ChooseRequest"/> shows.</summary>
    public sealed class RequestQuestion
    {
        /// <summary>The rule as the author reads it.</summary>
        public string RuleText { get; set; }
        /// <summary>"This will be sent to the owners (…)", plus anything the writer should know about their access.</summary>
        public string Notice { get; set; }
        /// <summary>False when only a project exception can be asked for (the finding isn't tied to a guide rule).</summary>
        public bool CanAskTeam { get; set; }
        /// <summary>False when there is no project to make an exception in.</summary>
        public bool CanAskProject { get; set; }
        /// <summary>The rule's wording, when the owners can change it (prose rules of the team's own guide); else null.</summary>
        public string Wording { get; set; }
        /// <summary>A reason typed earlier (a write that failed), kept so the author doesn't type it again.</summary>
        public string Reason { get; set; }
        /// <summary>The level picked earlier, or null.</summary>
        public string Setting { get; set; }
        /// <summary>True to preselect "for this project only".</summary>
        public bool ForProject { get; set; }
    }
}
