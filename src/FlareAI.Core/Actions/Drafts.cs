using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using FlareAI.Core.Editing;
using FlareAI.Core.Llm;
using FlareAI.Core.Logging;
using FlareAI.Core.Style;

namespace FlareAI.Core.Actions
{
    /// <summary>New text drafted for the place after a paragraph or heading, checked and ready to insert (or not).</summary>
    public sealed class DraftProposal
    {
        /// <summary>The paragraph or heading the new text follows.</summary>
        public XmlNode Anchor { get; set; }
        /// <summary>The new blocks, imported into the topic's document but not attached.</summary>
        public IList<XmlNode> ProposedBlocks { get; set; }
        public string ProposedXml { get; set; }
        public ValidationResult Validation { get; set; }
        public LlmResponse Raw { get; set; }
        /// <summary>Style issues still in the draft after the one repair round.</summary>
        public List<StyleFinding> RemainingLint { get; set; }
        public List<string> Notes { get; set; }
        /// <summary>How many places the draft leaves for the author to fill in ("Add details here.").</summary>
        public int Gaps { get; set; }
        /// <summary>Numbers, names and code the draft uses that the topic and the instruction don't contain. A warning, not proof.</summary>
        public List<string> NewTerms { get; set; }
        /// <summary>What the topic doesn't say that the model would have needed.</summary>
        public string Missing { get; set; }
    }

    /// <summary>Checks on a draft that need no model.</summary>
    public static class DraftFacts
    {
        public const string GapToken = "{{GAP}}";
        public const string GapHtml = "<strong>Add details here.</strong> The topic doesn't cover this.";

        /// <summary>Swaps every {{GAP}} for the visible gap text. Returns the number of gaps.</summary>
        public static int FillGaps(ref string xml)
        {
            if (string.IsNullOrEmpty(xml)) return 0;
            var n = Regex.Matches(xml, Regex.Escape(GapToken)).Count;
            xml = xml.Replace(GapToken, GapHtml);
            return n;
        }

        /// <summary>
        /// Numbers and capitalized names (not at the start of a sentence) in <paramref name="draft"/> that appear nowhere in
        /// <paramref name="known"/> (the topic text and the instruction). At most six.
        /// </summary>
        public static List<string> NewTerms(string known, string draft)
        {
            var seen = (known ?? "").ToLowerInvariant();
            var found = new List<string>();
            foreach (Match m in Regex.Matches(draft ?? "", @"(?<![\w.])\d[\d.,]*(?:\s?(?:seconds?|minutes?|hours?|days?|MB|GB|KB|%))?|(?<=[a-z,;:] )[A-Z][A-Za-z0-9]{2,}"))
            {
                var term = m.Value.Trim();
                if (term.Length == 0 || seen.Contains(term.ToLowerInvariant()) || found.Contains(term)) continue;
                if (term == "Add" || term == "The") continue;
                found.Add(term);
                if (found.Count == 6) break;
            }
            return found;
        }
    }

    public sealed partial class ActionRunner
    {
        /// <summary>
        /// Drafts new blocks to go right after <paramref name="anchor"/> (see <see cref="DraftAnchor"/>). One model call, plus one repair
        /// round when the draft breaks style rules. Nothing is written: the proposal goes to <see cref="ApplyInsert"/> when the author agrees.
        /// </summary>
        public DraftProposal Draft(IDocumentAdapter doc, XmlNode anchor, string instruction)
        {
            if (doc == null || anchor == null) throw new InvalidOperationException(DraftAnchor.NoPlace);
            var body = BlockSelector.FindBody(doc.Document);
            if (body == null || DraftAnchor.InsertIndex(body, anchor) < 0) throw new InvalidOperationException(DraftAnchor.NoPlace);
            if (string.IsNullOrEmpty((instruction ?? "").Trim())) throw new InvalidOperationException("Type what the new text should say.");

            var section = SectionBlocks(body, anchor);
            var sectionText = string.Join("\n", section.Select(b => Regex.Replace(AcceptedView.Of(new List<XmlNode> { b })[0].InnerText, @"\s+", " ").Trim()).Where(t => t.Length > 0).ToArray());
            if (sectionText.Length > 3000) sectionText = sectionText.Substring(sectionText.Length - 3000);
            var after = Regex.Replace(AcceptedView.Of(new List<XmlNode> { anchor })[0].InnerText, @"\s+", " ").Trim();
            var scope = ScopeOf(section.Count > 0 ? section : new List<XmlNode> { anchor });
            var title = TopicTitle(doc.Document);
            var headings = HeadingPath(body, anchor);
            var known = sectionText + " " + title + " " + instruction;

            var notes = new List<string>();
            var proposal = DraftOnce(doc, anchor, _prompts.BuildCompose(instruction, title, headings, sectionText, after), scope, known, notes);
            if (!proposal.Validation.IsValid || _style == null) { proposal.Notes = notes; return proposal; }

            // Self-check, as Rewrite does: lint the draft and give the model one chance to fix what it left in.
            var left = Significant(SafeLint(proposal.ProposedBlocks, notes));
            if (left.Count > 0)
            {
                var again = DraftOnce(doc, anchor, _prompts.BuildCompose(instruction + "\nAlso fix these, which your first version left in:\n" + Summarize(left), title, headings, sectionText, after), scope, known, notes);
                var leftAfter = again.Validation.IsValid ? Significant(SafeLint(again.ProposedBlocks, notes)) : left;
                if (again.Validation.IsValid && leftAfter.Count < left.Count) { proposal = again; left = leftAfter; }
            }
            proposal.RemainingLint = left;
            proposal.Notes = notes;
            return proposal;
        }

        /// <summary>The fixed instruction for Write an Introduction.</summary>
        public string IntroductionInstruction() { return _prompts.BuildIntroduction(); }

        private DraftProposal DraftOnce(IDocumentAdapter doc, XmlNode anchor, string user, GuideScope scope, string known, List<string> notes)
        {
            var resp = Ask("compose", user, Contracts.EditSchema, null, scope);
            var p = new DraftProposal
            {
                Anchor = anchor, Raw = resp, Validation = new ValidationResult(), RemainingLint = new List<StyleFinding>(),
                Notes = notes, NewTerms = new List<string>(), ProposedBlocks = new List<XmlNode>()
            };
            try
            {
                var xml = Contracts.ReadRevised(resp);
                p.Gaps = DraftFacts.FillGaps(ref xml);
                p.ProposedXml = xml;
                p.Missing = resp.Json == null ? null : (string)resp.Json["note"];
                p.ProposedBlocks = XhtmlFragment.ParseInto(xml, doc.Document);
                var refusal = NodeWrites.InsertRefusal(p.ProposedBlocks);
                if (refusal != null) p.Validation.Errors.Add(refusal);
                else p.NewTerms = DraftFacts.NewTerms(known, string.Join(" ", p.ProposedBlocks.Select(b => b.InnerText).ToArray()));
            }
            catch (XmlException xe) { p.Validation.Errors.Add("The draft was not well-formed XML, so nothing was changed. " + xe.Message); }
            catch (FormatException fe) { p.Validation.Errors.Add(fe.Message); }
            return p;
        }

        /// <summary>Inserts a valid draft right after its anchor as one undoable tracked change. Refuses an invalid one.</summary>
        public string ApplyInsert(IDocumentAdapter doc, DraftProposal proposal, string operationName)
        {
            if (proposal == null || !proposal.Validation.IsValid) throw new InvalidOperationException("Cannot insert: the draft failed its checks.");
            var body = BlockSelector.FindBody(doc.Document);
            var index = DraftAnchor.InsertIndex(body, proposal.Anchor);
            if (index < 0) throw new InvalidOperationException("The paragraph the new text follows is no longer in the topic. Click in the topic and try again.");
            var blocks = proposal.ProposedBlocks.Where(b => b.NodeType == XmlNodeType.Element).ToList();
            var refusal = NodeWrites.InsertRefusal(blocks);
            if (refusal != null) throw new InvalidOperationException(refusal);
            WriteTracked(doc.SourcePath, () => doc.InsertBlocks(operationName, body, index, blocks));
            Diagnostics.Log("Applied " + operationName + ": " + blocks.Count + " blocks inserted");
            return "Inserted " + Plural.Of(blocks.Count, "block") + " as a tracked change.";
        }

        /// <summary>The blocks of the section the anchor is in: from the heading above it (or the top) to the next heading.</summary>
        private static List<XmlNode> SectionBlocks(XmlNode body, XmlNode anchor)
        {
            var all = body.ChildNodes.Cast<XmlNode>().Where(n => n.NodeType == XmlNodeType.Element).ToList();
            var at = all.IndexOf(anchor);
            if (at < 0) return new List<XmlNode>();
            var start = at;
            while (start > 0 && !IsHeading(all[start])) start--;
            var end = at;
            while (end + 1 < all.Count && !IsHeading(all[end + 1])) end++;
            return all.GetRange(start, end - start + 1);
        }

        private static bool IsHeading(XmlNode n) { var s = n.LocalName; return s.Length == 2 && s[0] == 'h' && s[1] >= '1' && s[1] <= '6'; }

        /// <summary>"Install the agent > Before you start": the headings above the anchor, outermost first.</summary>
        private static string HeadingPath(XmlNode body, XmlNode anchor)
        {
            var all = body.ChildNodes.Cast<XmlNode>().Where(n => n.NodeType == XmlNodeType.Element).ToList();
            var at = all.IndexOf(anchor);
            var path = new List<string>();
            var level = 7;
            for (var i = at; i >= 0; i--)
            {
                if (!IsHeading(all[i])) continue;
                var l = all[i].LocalName[1] - '0';
                if (l >= level) continue;
                level = l;
                path.Insert(0, Regex.Replace(all[i].InnerText, @"\s+", " ").Trim());
            }
            return string.Join(" > ", path.ToArray());
        }
    }
}
