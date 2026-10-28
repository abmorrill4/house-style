using System;
using System.Collections.Generic;
using FlareAI.Core.Actions;
using FlareAI.Core.Editing;
using FlareAI.Core.Style;
using FlareAI.TestKit.Editor;
using FlareAI.TestKit.Workspace;

namespace FlareAI.Scenarios.Support
{
    /// <summary>
    /// The review pane's dialogs, answered by the test. Opens documents through the workspace (so snippets are opened "by the
    /// plugin" like the real host does) and records every question, notice and error the writer would have seen.
    /// </summary>
    public sealed class ScenarioHost : IReviewHost
    {
        private readonly FakeWorkspace _ws;

        public ScenarioHost(FakeWorkspace ws = null) { _ws = ws; }

        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Confirms = new List<string>();
        public readonly List<string> Opened = new List<string>();
        public readonly List<BatchPlan> Previews = new List<BatchPlan>();
        public bool ConfirmAnswer = true;
        public bool PreviewAnswer = true;
        public RuleSettingChoice RuleChoice, RequestChoice;
        public IList<RuleRef> Offered;
        public RequestQuestion Request;
        public string AskTextAnswer;

        public bool Confirm(string message, string action) { Confirms.Add(message); return ConfirmAnswer; }

        public RuleSettingChoice ChooseRuleSetting(string ruleText, bool canChangeTeamGuide, bool personalGuide, IList<RuleRef> otherRules)
        {
            Offered = otherRules;
            return RuleChoice;
        }

        public RuleSettingChoice ChooseRequest(RequestQuestion question) { Request = question; return RequestChoice; }
        public string AskText(string title, string label, string initial, bool multiline, bool required = false) { return AskTextAnswer; }

        public IDocumentAdapter OpenDocument(string path)
        {
            Opened.Add(path);
            return _ws == null ? null : _ws.OpenForPlugin(path);
        }

        public bool PreviewBatch(BatchPlan plan) { Previews.Add(plan); return PreviewAnswer; }

        public bool SaveAndClose(IDocumentAdapter doc)
        {
            var sim = doc as FlareSimDocument;
            if (sim == null || !sim.OpenedByPlugin) return false;
            sim.Save();
            sim.Close();
            return true;
        }

        public void ShowError(string whatHappened, string whatToDo, Exception details, string action) { Errors.Add(whatHappened + " " + whatToDo); }
    }
}
