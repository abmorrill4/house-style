using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace FlareAI.Core.Actions
{
    public enum ActionKind { CheckStyle, Rewrite, Ask, Question }

    /// <summary>
    /// Assembles system + user prompts from embedded templates plus optional rule files shipped next to the
    /// plugin (rules\*.md). Pure function of its inputs so tests can snapshot the output.
    /// </summary>
    public sealed class PromptBuilder
    {
        private readonly Func<string> _guide;

        /// <param name="companyGuide">
        /// Supplies the company style guide (from docstyle). Called on the worker thread when a prompt is built;
        /// may be null, return null, or throw — the baseline still applies.
        /// </param>
        public PromptBuilder(Func<string> companyGuide)
        {
            _guide = companyGuide;
            OverrideFolder = DefaultOverrideFolder;
        }

        /// <summary>Prompts over the layered guide: each call gets the always-on rules plus the packs its text needs.</summary>
        public static PromptBuilder For(FlareAI.Core.Style.IStyleService style, string project)
        {
            if (style == null) return new PromptBuilder(null);
            return new PromptBuilder(() => style.Guide(project)) { GuideFor = scope => style.GuideFor(project, scope) };
        }

        /// <summary>
        /// The guide for one unit: [0] always-on rules, [1] packs for the unit's content (Content tags). When set, it
        /// replaces the whole-guide supplier, so each call carries only the rules that can apply to its text.
        /// </summary>
        public Func<FlareAI.Core.Style.GuideScope, string[]> GuideFor { get; set; }

        /// <summary>The system prompt for a unit: [0] instructions + always-on rules, [1] content packs, [2] rules for words it uses (null when empty).</summary>
        public string[] BuildSystemParts(FlareAI.Core.Style.GuideScope scope)
        {
            if (GuideFor == null) return new[] { BuildSystem(), null, null };
            string[] parts = null;
            try { parts = GuideFor(scope ?? new FlareAI.Core.Style.GuideScope()); }
            catch (Exception ex) { Diagnostics.Log("style guide", ex); }
            var system = BuildSystem(parts == null ? null : parts[0]);
            Func<int, string, string> part = (i, intro) => parts != null && parts.Length > i && !string.IsNullOrEmpty(parts[i]) ? intro + "\n\n" + parts[i].Trim() : null;
            var packs = part(1, "These rules also apply, because of what this text contains (same precedence: a later layer overrides an earlier one):");
            var terms = part(2, "These rules also apply, because this text uses the words they're about:");
            // Packs shared by several calls get their own cache block; for a single call a cache write would never be read.
            if (scope != null && !scope.SharedPacks && packs != null) { terms = terms == null ? packs : packs + "\n\n" + terms; packs = null; }
            return new[] { system, packs, terms };
        }

        /// <summary>
        /// The system prompt for tasks that don't apply style rules (alt text, topic assists): a few lines of house manner,
        /// no guide. Sending the ~35k-token guide for a 64-token answer made alt text cost $0.14 on a cold cache.
        /// </summary>
        public string BuildPlainSystem() { return Embedded("system-plain.md"); }

        public string BuildSystem()
        {
            string guide = null;
            try { if (_guide != null) guide = _guide(); }
            catch (Exception ex) { Diagnostics.Log("style guide", ex); }
            return BuildSystem(guide);
        }

        private string BuildSystem(string guide)
        {
            var sb = new StringBuilder(Embedded("system-base.md"));
            sb.AppendLine().AppendLine();
            if (!string.IsNullOrEmpty(guide))
            {
                sb.AppendLine("Style guide: layers in order; each later layer overrides the ones before it on any conflict. Every rule has an id in [brackets]; cite it when you apply the rule.").AppendLine();
                sb.Append(guide.Trim());
            }
            else
            {
                sb.Append("Style guide: follow the Google developer documentation style guide (https://developers.google.com/style).");
            }
            return sb.ToString();
        }

        /// <param name="lint">One-line-per-finding summary from the linter, or "".</param>
        public string BuildUser(ActionKind kind, string fragment, string instruction, string lint = "")
        {
            var lintBlock = string.IsNullOrEmpty(lint) ? "(none)" : lint;
            switch (kind)
            {
                case ActionKind.CheckStyle:
                    return Embedded("action-check.md").Replace("{{LINT}}", lintBlock).Replace("{{FRAGMENT}}", fragment);
                case ActionKind.Rewrite:
                    return Embedded("action-rewrite.md")
                        .Replace("{{INSTRUCTION}}", "Task: make this text meet the style guide. Fix every linter finding below, and tighten wordy, vague, or passive sentences into plain, direct instructions and statements. Sentences that are already clear and compliant stay as they are.")
                        .Replace("{{LINT}}", lintBlock)
                        .Replace("{{FRAGMENT}}", fragment);
                case ActionKind.Ask:
                    return Embedded("action-rewrite.md")
                        .Replace("{{INSTRUCTION}}", "Task (the author's instruction): " + (instruction ?? "").Trim())
                        .Replace("{{LINT}}", lintBlock)
                        .Replace("{{FRAGMENT}}", fragment);
                default:
                    throw new ArgumentOutOfRangeException("kind");
            }
        }

        /// <summary>Second round: the model's own revision still trips the linter.</summary>
        public string BuildRepair(string revisedXml, string lint)
        {
            return Embedded("action-repair.md").Replace("{{LINT}}", lint).Replace("{{FRAGMENT}}", revisedXml);
        }

        /// <summary>The instruction of a one-click rewrite: the team's file when there is one, else the built-in wording. No rules, no guide.</summary>
        public string BuildPreset(RewritePreset p) { return Embedded(p.Template).Trim(); }

        /// <summary>A question about the text: answered in words, nothing rewritten.</summary>
        public string BuildQuestion(string fragment, string question)
        {
            return Embedded("action-question.md").Replace("{{QUESTION}}", (question ?? "").Trim()).Replace("{{FRAGMENT}}", fragment);
        }

        public string BuildAlt(string context) { return Embedded("action-alt.md").Replace("{{CONTEXT}}", context ?? ""); }

        /// <summary>One part of Topic assists (description + keywords, synonyms, or micro content + glossary).</summary>
        public string BuildAssist(string fragment, string glossary, string part)
        {
            return Embedded("action-assist.md").Replace("{{PART}}", part ?? "").Replace("{{GLOSSARY}}", string.IsNullOrEmpty(glossary) ? "(none)" : glossary).Replace("{{FRAGMENT}}", fragment ?? "");
        }

        /// <summary>Review of one unit: the marked text, its context (topic title, section), and what the linter already found.</summary>
        public string BuildReview(string context, string marked, string lint)
        {
            return Contracts.ReviewFields + "\n\n" + Embedded("action-check.md").Replace("{{CONTEXT}}", context ?? "").Replace("{{LINT}}", string.IsNullOrEmpty(lint) ? "(none)" : lint).Replace("{{FRAGMENT}}", marked ?? "");
        }

        /// <summary>Run-level rewrite of one block: the task (instruction, or the default style pass) and the marked text.</summary>
        /// <param name="continuations">Runs that finish a sentence begun before them (<see cref="Contracts.DescribeContinuations"/>), or "".</param>
        public string BuildRunRewrite(string instruction, string marked, string lint, string continuations = "")
        {
            var task = string.IsNullOrEmpty(instruction)
                ? "Task: make this text meet the style guide. Fix every linter finding below, and tighten wordy, vague, or passive sentences into plain, direct instructions and statements. Runs that are already clear and compliant stay as they are."
                : "Task (the author's instruction): " + instruction.Trim();
            if (!string.IsNullOrEmpty(continuations)) task += "\n\n" + continuations;
            return Contracts.RewriteFields + "\n\n" + Embedded("action-rewrite-runs.md").Replace("{{INSTRUCTION}}", task).Replace("{{LINT}}", string.IsNullOrEmpty(lint) ? "(none)" : lint).Replace("{{FRAGMENT}}", marked ?? "");
        }

        /// <summary>Notes, an old guide, or another tool's prompt → candidate rules (JSON array).</summary>
        public string BuildExtract(string notes)
        {
            return Embedded("action-extract.md").Replace("{{NOTES}}", notes ?? "");
        }

        /// <summary>One proposal, re-asked with the author's instruction ("keep SSO", "shorter").</summary>
        public string BuildRefine(string context, string excerpt, string proposal, string instruction)
        {
            return Embedded("action-refine.md").Replace("{{CONTEXT}}", context ?? "").Replace("{{EXCERPT}}", excerpt ?? "")
                .Replace("{{PROPOSAL}}", proposal ?? "").Replace("{{INSTRUCTION}}", (instruction ?? "").Trim());
        }

        /// <summary>
        /// Folder of team prompt overrides (the guide's FlareAI\prompts): a file there with a template's name replaces the
        /// built-in template, so owners tune the AI's instructions in the guide, versioned with the rules. Null = built-ins only.
        /// </summary>
        public static string DefaultOverrideFolder { get; set; }

        /// <summary>This builder's override folder (defaults to <see cref="DefaultOverrideFolder"/>).</summary>
        public string OverrideFolder { get; set; }

        /// <summary>Names of the templates a team can override.</summary>
        public static readonly string[] Templates = { "system-base.md", "system-plain.md", "action-check.md", "action-rewrite.md", "action-repair.md", "action-refine.md", "action-extract.md", "action-alt.md", "action-assist.md", "action-question.md", "action-rewrite-runs.md", "preset-simplify.md", "preset-shorten.md" };

        private string Embedded(string name)
        {
            var custom = OverrideFolder == null ? null : Path.Combine(OverrideFolder, name);
            if (custom != null && File.Exists(custom))
            {
                try { return File.ReadAllText(custom, Encoding.UTF8); }
                catch (Exception ex) { Diagnostics.Log("prompt override " + custom, ex); }
            }
            return BuiltIn(name);
        }

        /// <summary>The template shipped with the plugin (used to seed an override file).</summary>
        public static string BuiltIn(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("FlareAI.Core.Prompts." + name))
            {
                if (s == null) throw new InvalidOperationException("Missing embedded prompt: " + name);
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }
    }
}
