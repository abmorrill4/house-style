using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using FlareAI.Core.Editing;
using FlareAI.Core.Llm;
using FlareAI.Core.Logging;
using FlareAI.Core.Style;
using Newtonsoft.Json.Linq;

namespace FlareAI.Core.Actions
{
    /// <summary>One exchange: what the writer said, and the reply (an answer, or a proposed change).</summary>
    public sealed class ConversationTurn
    {
        public string Message { get; set; }
        /// <summary>The exact user message sent to the model (the numbered text too, on the turns that carried it), kept so the history stays byte-stable.</summary>
        public string UserText { get; set; }
        /// <summary>"answer" or "change".</summary>
        public string Kind { get; set; }
        /// <summary>The reply in words: the answer, or a sentence about the change, with the rules it names.</summary>
        public string Answer { get; set; }
        public List<string> RuleIds { get; set; } = new List<string>();
        /// <summary>For a change: the proposed wording, checked, ready to apply. Null for an answer.</summary>
        public RewriteProposal Proposal { get; set; }
        /// <summary>The text of the selection when this reply was written (see <see cref="Conversation.Fingerprint"/>).</summary>
        public string BaseText { get; set; }
        /// <summary>The reply JSON exactly as the model sent it.</summary>
        public string ReplyJson { get; set; }
        /// <summary>What this turn cost, in dollars (estimated).</summary>
        public double Usd { get; set; }
        public bool Applied { get; set; }
        /// <summary>True once an Apply of this reply changed the topic.</summary>
        public string Note { get; set; }
        public bool IsChange { get { return Kind == "change" && Proposal != null; } }
    }

    /// <summary>
    /// Ask AI as a conversation on one selection: the turns so far and the numbered text the model last saw. Lives in memory only. A turn
    /// that failed is not added, so Try Again resends it.
    /// </summary>
    public sealed class Conversation
    {
        public IList<XmlNode> Blocks { get; private set; }
        public List<ConversationTurn> Turns { get; private set; }
        /// <summary>The numbered runs the model was last given. Replies to a turn that did not resend the text use these ids.</summary>
        public RunMap Map { get; set; }
        /// <summary>Fingerprint of the text when it was last sent.</summary>
        public string SentFingerprint { get; set; }

        public Conversation(IList<XmlNode> blocks) { Blocks = blocks; Turns = new List<ConversationTurn>(); }

        /// <summary>The visible text of the blocks as the writer reads it (pending changes resolved), as one string.</summary>
        public static string Fingerprint(IList<XmlNode> blocks)
        {
            return XhtmlFragment.Serialize(AcceptedView.Of(blocks));
        }

        /// <summary>True when every block is still attached to the topic's document.</summary>
        public bool StillInTopic(XmlDocument document)
        {
            return document != null && Blocks.All(b => b.OwnerDocument == document && Attached(b, document));
        }

        private static bool Attached(XmlNode n, XmlDocument doc)
        {
            for (var p = n; p != null; p = p.ParentNode) if (p == doc.DocumentElement) return true;
            return false;
        }

        /// <summary>The earlier turns as the model should see them, oldest first.</summary>
        public List<LlmTurn> History()
        {
            var h = new List<LlmTurn>();
            foreach (var t in Turns) { h.Add(new LlmTurn("user", t.UserText)); h.Add(new LlmTurn("assistant", t.ReplyJson)); }
            return h;
        }

        public double TotalUsd { get { return Turns.Sum(t => t.Usd); } }

        public const int MaxMessages = 20, MaxWords = 1500;
    }

    public sealed partial class ActionRunner
    {
        /// <summary>
        /// One turn of Ask AI as a conversation. Sends the numbered text on the first turn and whenever it changed since, the writer's message
        /// otherwise, with the history. The model picks the reply kind. Nothing is written: a change goes to <see cref="ApplyTurn"/>
        /// when the writer clicks Apply, and an answer can never reach it.
        /// </summary>
        public ConversationTurn Converse(IDocumentAdapter doc, Conversation conv, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Type your question or what to change.");
            if (conv.Turns.Count * 2 >= Conversation.MaxMessages)
                throw new InvalidOperationException("This conversation is too long to continue. Start a new one.");
            if (!conv.StillInTopic(doc.Document)) throw new InvalidOperationException("This text is no longer in the topic.");
            foreach (var b in conv.Blocks)
                if (BlockSelector.IsOpaque(b))
                    throw new InvalidOperationException("The selection includes a " + b.Name + ", which can't be changed in place. Select text inside a paragraph instead.");

            var fingerprint = Conversation.Fingerprint(conv.Blocks);
            var words = fingerprint.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > Conversation.MaxWords * 4) throw new InvalidOperationException("The selection is too long for a conversation. Select less text.");
            var sendText = conv.Turns.Count == 0 || fingerprint != conv.SentFingerprint;
            if (sendText) { conv.Map = RunMap.Of(conv.Blocks); conv.SentFingerprint = fingerprint; }

            var user = sendText
                ? _prompts.BuildConverse(message, conv.Map.Marked, TopicTitle(doc.Document), conv.Turns.Count == 0)
                : message.Trim();
            var scope = WordingScope(conv.Blocks);
            int callsBefore; double usdBefore;
            Usage.Totals(out callsBefore, out usdBefore);
            var resp = Ask("converse", user, Contracts.Converse, null, scope, true, conv.History());
            int callsAfter; double usdAfter;
            Usage.Totals(out callsAfter, out usdAfter);

            var json = resp.Json ?? new JObject();
            var turn = new ConversationTurn
            {
                Message = message.Trim(), UserText = user, ReplyJson = resp.Text, BaseText = fingerprint, Usd = Math.Max(0, usdAfter - usdBefore)
            };
            var known = _style == null ? null : _style.RuleIdsFor(_project, scope);
            var ids = new List<string>();
            if (json["rule_ids"] is JArray)
                foreach (var t in (JArray)json["rule_ids"])
                {
                    var id = (string)t;
                    if (id != null && (known == null || known.Contains(id)) && !ids.Contains(id)) ids.Add(id);
                }
            turn.RuleIds = ids;
            var said = ((string)json["answer"] ?? "").Trim();
            turn.Note = ((string)json["note"] ?? "").Trim();

            var errors = new List<string>();
            bool restructure; string note;
            var changes = Contracts.ReadRewrite(json, conv.Map, errors, out restructure, out note);
            if (errors.Count > 0) Diagnostics.Log("converse: " + errors.Count + " contract breach(es); kept the valid changes");
            if ((string)json["kind"] == "change" && changes.Count > 0 && !restructure)
            {
                turn.Kind = "change";
                turn.Proposal = ProposalFromChanges(conv.Blocks, conv.Map, changes);
                turn.Answer = said.Length > 0 ? said : "Here is a change.";
            }
            else
            {
                turn.Kind = "answer";
                turn.Answer = said.Length > 0 ? said : (restructure ? "This needs a structural change that wording alone can't make." : "I have nothing to add to that.");
                if (restructure && turn.Note.Length == 0) turn.Note = note;
            }
            if (turn.RuleIds.Count > 0) turn.Answer += "\n\nRule: " + string.Join(", ", turn.RuleIds.ToArray());
            conv.Turns.Add(turn);
            return turn;
        }

        /// <summary>A proposal from new text for runs: each changed run's text replaced on a clone, planned as word-level changes, held back where only the author may decide.</summary>
        private RewriteProposal ProposalFromChanges(IList<XmlNode> blocks, RunMap map, Dictionary<string, string> changes)
        {
            var proposal = new RewriteProposal
            {
                OriginalBlocks = blocks, View = AcceptedView.Of(blocks), Validation = new ValidationResult(), Notes = new List<string>(),
                RemainingLint = new List<StyleFinding>(), WordPlan = new List<StyleFinding>()
            };
            foreach (var id in changes.Keys.ToList())
            {
                var held = MeaningGuard.Check(map.Texts[id], changes[id], MeaningGuard.InCode(map.Runs[id]), "Kept a sentence as it was: the suggested wording");
                if (held == null) continue;
                changes.Remove(id);
                proposal.Held.Add(held);
            }
            var proposed = new List<XmlNode>();
            foreach (var block in blocks)
            {
                var clone = block.CloneNode(true);
                var cloneRuns = TextRuns.Of(new List<XmlNode> { clone }).Where(r => r.Value.Trim().Length > 0).ToList();
                var blockRuns = TextRuns.Of(new List<XmlNode> { block }).Where(r => r.Value.Trim().Length > 0).ToList();
                for (var k = 0; k < blockRuns.Count && k < cloneRuns.Count; k++)
                {
                    var run = blockRuns[k];
                    var entry = map.Runs.FirstOrDefault(kv => kv.Value == run);
                    string text;
                    if (entry.Key == null || !changes.TryGetValue(entry.Key, out text)) continue;
                    cloneRuns[k].Value = text;
                    var owner = TextRuns.BlockOf(run) ?? block;
                    if (!WordDiff.PlanRun(run, text, owner, proposal.WordPlan))
                        proposal.WordPlan.Add(FindingLocator.ForSpan(run, 0, run.Value.Length, text, owner));
                }
                proposed.Add(clone);
            }
            proposal.ProposedBlocks = AcceptedView.Of(proposed);
            proposal.ProposedXml = XhtmlFragment.Serialize(proposal.ProposedBlocks);
            var notes = new List<string>();
            proposal.RemainingLint = _style == null ? new List<StyleFinding>() : Significant(SafeLint(proposal.ProposedBlocks, notes));
            proposal.Notes = notes.Concat(proposal.Held).ToList();
            return proposal;
        }

        /// <summary>
        /// Applies any reply of the conversation, an earlier one included, as one undoable tracked change. When the text is as it was
        /// when the reply was written, this is the ordinary Apply. When it has changed since, the reply's wording is planned again against
        /// the text as it is now, with no model call. Returns what happened, in the writer's words.
        /// </summary>
        public string ApplyTurn(IDocumentAdapter doc, Conversation conv, ConversationTurn turn, string operationName)
        {
            if (turn == null || !turn.IsChange) throw new InvalidOperationException("This reply is an answer, so there is nothing to apply.");
            if (!conv.StillInTopic(doc.Document)) throw new InvalidOperationException("This text is no longer in the topic.");
            var proposal = turn.Proposal;
            if (Conversation.Fingerprint(conv.Blocks) == turn.BaseText)
            {
                var done = Apply(doc, proposal, operationName);
                turn.Applied = true;
                return done;
            }
            // The text changed after this reply: plan this reply's wording against the text as it is now.
            var current = AcceptedView.Of(conv.Blocks);
            var plan = WordDiff.Plan(current, proposal.ProposedBlocks, conv.Blocks);
            if (plan == null) throw new InvalidOperationException("The text changed after this reply, and this reply can't be applied to it word by word. Ask again.");
            var replanned = new RewriteProposal
            {
                OriginalBlocks = conv.Blocks, ProposedBlocks = proposal.ProposedBlocks, ProposedXml = proposal.ProposedXml, View = current,
                Validation = new ValidationResult(), WordPlan = plan, Notes = new List<string>(), RemainingLint = new List<StyleFinding>()
            };
            var result = Apply(doc, replanned, operationName);
            turn.Applied = true;
            return "The text changed after this reply, so this reply's wording was applied to the text as it is now. " + result;
        }
    }
}
