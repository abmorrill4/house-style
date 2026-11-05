# FlareAI — Build & Test Plan

AI editing assistant for MadCap Flare Desktop. Phase 1 talks to the Anthropic API
directly; Phase 2 swaps the transport for the Bifrost gateway (virtual key, MCP
tools, agent mode) without touching the Flare-facing code.

Status legend: `[ ]` todo · `[~]` in progress · `[x]` done

---

## 0. Ground truth we are building on

What the Flare plug-in API gives us (verified against the Flare 2026 docs):

| Need | API |
|---|---|
| Entry point | `IPlugin` (`Initialize(IHost)`, `Execute()`, `Stop()`, `IsActivated`, `GetName/GetVersion/GetAuthor/GetDescription`) |
| UI surface | `IHost.GetNavContext().GetRibbon()` → `AddNewRibbonTab` → `AddNewRibbonGroup` → `AddRibbonButton(RibbonControlData)` (WPF ribbon; `RibbonControlData.Command` is an `ICommand`) |
| Active topic | `IHost.GetEditorContext().GetActiveDocument()` → `IDocument` |
| Read topic | `IDocument.GetXmlDocument()` (live `System.Xml.XmlDocument`), `GetDocumentXml()`, `GetSourceUrl()` |
| Read selection | `IDocument.Selection.GetText()`, `Selection.GetXmlNodeList()` (nodes the selection spans) |
| Write back | `IDocument.ReplaceDocumentNode(old, new, trackChanges)`, `InsertDocumentNode`, `RemoveDocumentNode` |
| Undo grouping | `IDocument.StartOperation(name)` … `EndOperation()` |
| Schema | `IEditorContext.GetMadCapSchema()` — list of MadCap element names, useful for validation |
| Constraint | Plugin DLL **must target .NET Framework 4.0** |

Unknowns that only the real DLL can answer (resolved by `scripts/dump-api.ps1`):

- Exact namespace/assembly of `RibbonControlData`, `ButtonCommand`, `IRibbonTab` etc.
- Whether `GetXmlDocument()` returns the live DOM (edits reflected) or a copy.
- Whether `ReplaceDocumentNode` requires the new node to be imported into the same `XmlDocument`.
- What `Selection.GetXmlNodeList()` returns for a selection inside a single `<p>` (text node? the `<p>`?).
- ~~Which CLR version~~ → Flare 22 API DLL targets 4.8; plugin targets net48.

## 1. Architecture

```
┌────────────────────────── Flare Desktop (WPF/WinForms, .NET 4.x) ──────────────────────────┐
│  FlareAI.Flare.dll  (net40)                                                                │
│   ├─ Plugin.cs            IPlugin: ribbon tab "AI" with 4 buttons, lifecycle               │
│   ├─ FlareDocumentAdapter IDocumentAdapter over IDocument/ISelection (thin, no logic)      │
│   └─ UI/                  AskForm, ResultForm, SettingsForm (WinForms, BackgroundWorker)   │
│              │ IDocumentAdapter + ILlmClient                                               │
│  FlareAI.Core.dll   (net40, no Flare references — builds & tests on any OS)                │
│   ├─ Editing/   IDocumentAdapter, BlockSelector, XhtmlValidator, XhtmlFragment            │
│   ├─ Actions/   ActionRunner (CheckStyle, Rewrite, Ask, Summarize) + PromptBuilder         │
│   ├─ Llm/       ILlmClient, AnthropicClient (Phase 1), BifrostClient (Phase 2)             │
│   └─ Settings/  PluginSettings (DPAPI-protected key, JSON in %APPDATA%\FlareAI)             │
└────────────────────────────────────────────────────────────────────────────────────────────┘
                                       │ HTTPS
                     Phase 1: api.anthropic.com/v1/messages
                     Phase 2: bifrost/v1/chat/completions (x-bf-vk, agent mode, MCP tools)
```

Design rules:

1. **Nothing in Core references Flare.** Core is unit-tested with a `FakeDocumentAdapter`
   fed real `.htm` topics from `tests/fixtures`.
2. **Every write to the topic is a tracked change** inside one `StartOperation/EndOperation`
   so the author reviews with Flare's native accept/reject and can undo in one step.
3. **The model never touches the DOM directly.** It returns an XHTML fragment; `XhtmlValidator`
   must pass before `ReplaceDocumentNode` is called. Fail closed.
4. **No `async`/`await`, no `HttpClient`, no `ValueTuple`** (net40). HTTP via `HttpWebRequest`,
   JSON via Newtonsoft.Json 13 (supports net40), threading via `BackgroundWorker`.
5. **Secrets** live in DPAPI-protected settings. Dev fallback: `ANTHROPIC_API_KEY` env var,
   loaded from the git-ignored `.env` at the repo root.

## 2. Phases

### Phase 0 — Bootstrap `[x]`

- [x] `scripts\bootstrap.ps1` → git init, locate Flare, copy `B3.PluginAPIKit.dll` to `lib\`,
      run `dump-api.ps1`, restore + build.
- [x] Read `docs\api-dump.txt`; fix any type/namespace mismatches in `FlareAI.Flare`.
      (All types in `B3.PluginAPIKit`; code matched. DLL targets net48 → plugin retargeted to net48.)
- [x] `scripts\smoke-api.ps1` (key from `.env`) → confirms the key and model id work from this box.
- [x] `dotnet test` green (26/26).

Exit criteria: solution builds, tests pass, we know the real shape of the ribbon types.

### Phase 1 — Hello Flare `[x]`

- [x] Deploy with `scripts\install-plugin.ps1` (needs admin — copies to `Flare.app\Plugins\FlareAI`).
- [x] Enable on File > Options > Plugins → **AI** tab appears immediately (no restart needed in Flare 22).
- [x] Button "Diagnostics" dumps: active doc path, selection text, selection node names,
      block nodes `BlockSelector` would pick → to `%APPDATA%\FlareAI\diag.log`.
- [x] Settings dialog: store API key (DPAPI), model, base URL. Verify round-trip.

Exit criteria: we can see the selection → block mapping is right on a real topic.

### Phase 2 — The three actions, direct API `[x]`

- [x] **Check style**: send selection (or whole topic if nothing selected) + style rules;
      show findings list in `ResultForm` (rule, excerpt, suggestion). Read-only.
- [x] **Rewrite selection**: prompt → model returns `<revised>…</revised>` XHTML →
      validate → replace block nodes as tracked changes.
- [x] **Ask…**: free-form instruction on the selection; same write-back path.
- [x] Round-trip safety: `XhtmlValidator` rejects dropped `MadCap:*` elements, changed
      snippet/variable/xref refs, unknown elements, namespace drift.
- [x] Error UX: network/auth failures surface in the dialog, never crash Flare.

- [x] Pending tracked changes: Check style omits deleted text; Rewrite/Ask refuse blocks with
      pending changes (Flare stores them as `MadCap:changes` ids → `head/MadCap:changeData`).
- [x] `docs\MANUAL-TESTS.md` pass: 18/20 ✅, 2 skipped (network-off, tool-strip mode) — see results table there.

Verified live in Flare 22 (2026-10-08): live DOM (edits reflected), `ReplaceDocumentNode` accepts
imported nodes, rewrite shows as Added/Deleted tracked changes, one Ctrl+Z undoes the whole edit,
selection inside a `<p>`/`<h1>` yields that element; inside an `<li>` maps to the whole `<ol>`.

Exit criteria: tester can run all three on their own project for a day without a crash.

### Phase 2b — docstyle: layered style guide + linter over MCP `[x]` (superseded by 2i: now in process)

- [x] `mcp/docstyle`: Python MCP stdio server (official SDK, run via `uv`) with `lint`, `style_lookup`,
      `glossary_lookup`. Layers: Google developer documentation style guide (baseline: condensed guide +
      vendored errata-ai/Google Vale package) with the company guide + `Company` Vale style on top.
- [x] Plugin is an MCP client (`McpStdioClient`, net40): Check style = Vale lint (instant, exact spans,
      mechanical fixes) + model review for judgment issues only; findings must quote the topic verbatim.
- [x] Findings window (modeless): select a row → text highlighted in Flare; Fix / Fix all → word-level
      tracked changes, one undo step; Rewrite paragraph → model resolves that paragraph's findings; Guide ↗.
- [x] Rewrite/Ask: lint findings go into the prompt; output is linted and repaired once if needed.
- [x] `docstyle` Claude skill (`.claude/skills/docstyle`) and `.mcp.json` so Claude Code applies the same layers.
- [x] Rewrites land word-level: when the proposal keeps the structure, `WordDiff` turns it into word
      replacements applied like fixes (one undo step); restructures fall back to a block replacement.
- [x] Rewrites over pending tracked changes: the model sees the text as the author reads it (`AcceptedView`),
      edits land word-level around the pending changes, which stay pending. Only restructuring such text is refused.

### Phase 2c — Close the gaps (benchmarked against Markup AI's documented limitations) `[~]`

Order matters: 1 unblocks the company-guide decision; 2 is the biggest UX change; the rest are independent.

1. `[x]` **Configurable style layers + project layer.** Google (built in) → any number of layer folders
   (`guide.md` + `rules/*.yml` + `accept.txt`; network share, git checkout, or inside a Flare project) →
   optional **project layer generated from the open Flare project**: variable values ("type the variable,
   not the text"), glossary terms (canonical spelling, definitions for the AI, spelling vocabulary),
   `Resources/StyleGuide/` if present. Settings: ordered layer list + "Use this project's terms" toggle.
   Same config format for the plugin, Claude Code, and the gateway.
2. `[x]` **Docked sidebar.** Host the findings list as a native Flare pane via B3.AppKit (`IPaneDock`/`Pane`/
   `IPaneView`; internal, not public API) behind one adapter; fall back to the floating window if Flare
   changes. Done: native pane (Flare's own dock/float/close), responsive layout; Flare places it with the left
   panes and remembers where the author drags it. Results following the active topic is item 7.
3. `[x]` **Tool Strip mode** (Markup AI has it, we don't): `CreateCustomToolBar` buttons for the same actions.
4. `[x]` **Mark of the Web** (Markup AI's #1 install issue, same for us): installer runs `Unblock-File`;
   tester guide says so. (A blocked plugin never loads, so in-plugin detection is moot.)
5. `[x]` **Variables resolved** (Markup AI: not checked). Lint sees variable values in context (sentence rules
   work across them); findings inside a value point to the variable set, not the topic; the AI review gets
   the values. **Insert variable** fix for project-variable findings: replace the typed text with the
   `MadCap:variable` (tracked; the validator allows introducing only variables that exist in the project).
   Done: lint runs on whole lines (inline markup flattened, variable values resolved via LookupVariableValue);
   hits inside variable values are dropped; hits across markup show on their first run, not one-click.
   Insert-variable is **guided** (Go to + Insert ▸ Variable): every plugin-side insert crashes Flare 22 on Redo.
   Follow-up: drive Flare's internal TextVariableInsertCommand on the selection for a true one-click fix.
6. `[x]` **Snippets checked** (Markup AI: not checked). Check style includes referenced snippets' text,
   labelled "in snippet X"; Fix opens the snippet and applies there, tracked.
7. `[x]` **Results that survive** (Markup AI: ~20 topics, lost on rename). Lint re-runs instantly on topic
   switch; AI review cached by file path + content hash (rename-proof); dismissed findings remembered.
   Done: the open findings view follows the active topic (instant lint); AI review cached by a hash of the exact
   review prompt (content-addressed, so renames and switches don't matter); a new review on switch runs only on
   Re-check (no surprise cost). Dismissals: item 9.
8. `[x]` **Project / TOC check** (Markup AI: one topic only). "Check project": lint every topic (no AI cost),
   report grouped by topic → open topic + go to finding; optional AI review per topic on demand.
   Done: AI ▸ Check project lints Content/**/*.htm|html|flsnp (StyleGuide folder excluded) in batched linter calls,
   grouped by file; Go to/Fix open the file; Fix all across >3 files asks first. TOC-scoped check: not yet.
9. `[x]` **Dismiss / ignore** (Markup AI has it): dismiss a finding; "ignore rule for this project" writes
   to the project layer.
10. `[x]` **Finer blocks for tables and lists**: send the row/item, not the whole table, when the selection is
    inside one cell/item.
11. `[~]` Manual tests: 20 (Tool Strip) ✅; 17 (network off) still open.
12. `[x]` **Company guide as a Flare project.** `layers/company` is `CompanyStyle.flprj`; StyleGuide topics are parsed:
    bullets = citable prose rules (nested Bad/Good), Avoid/Use tables = Vale rules with fixes, Rule/Setting tables =
    Google overrides, plus the project's variables and glossary. Stable rule IDs; the AI must cite one (grounded to
    layer/section/link, unknown IDs dropped to "general"). **Validate style guide**: table rows vs. their Examples,
    contradictions, then optional AI check of prose rules' Bad/Good examples. Verified in Flare: 16/16 table rules,
    AI 5/5 prose rules. Follow-up: verify Global Project Linking as a way to import the guide into each project.

Won't fix: Flare Online (Desktop plugin; docstyle MCP + skill cover repo-based authoring); plugin enable
is per Windows user (Flare behavior; documented).

Already better than Markup AI: fixes and rewrites are tracked changes (theirs are not); selection checks;
pending changes preserved; no WebView2/SaaS dependency; same rules in Claude Code and the gateway.

### Phase 2d — Review workflow: discrete, evidence-based proposals `[~]`

The output of a review is where the plugin wins or loses. A long topic must turn into small, understandable pieces of
work the author can decide on one by one — never a wall of suggestions, never output that vanishes on close.

**Proposal card** (one per finding): what — the exact span as a readable before/after word diff (never raw XHTML);
why — the cited rule, its text and its Bad/Good example (an AI finding that cannot cite a rule is dropped); source —
deterministic rule vs. AI judgment; impact — "in snippet X (used by N topics)", "inside variable Y".

1. `[x]` **Readable diffs.** Rewrite/Ask proposals render as text with deletions struck and insertions marked, per
   block (list items numbered); raw XHTML only behind "Show XHTML". Cards show the same diff for their span.
2. `[x]` **Cards + triage.** The pane leads with a summary ("23 proposals in 5 sections: 14 mechanical · 6 wording ·
   3 restructure"), lists proposals in document order grouped by section heading, and shows the selected one as a
   card with Accept · Reject ▾ (wrong / fine here / rule doesn't apply) · Edit… · Refine… · Explain.
   "Accept all mechanical" replaces Fix all.
3. `[x]` **No view jumping.** Accepting never switches the editor away from the topic. Snippet proposals are listed
   with their impact and applied only when the author opens that snippet ("Apply in snippet"), then the topic is
   re-activated.
4. `[x]` **Persistent review sessions.** Per topic and per user (`%APPDATA%\FlareAI\reviews`), keyed by content:
   statuses (open / accepted / rejected / deferred) survive closing the pane or Flare; a proposal whose text changed
   underneath is marked stale, not silently dropped.
5. `[x]` **Refine / re-analyze.** Refine re-asks the model for one proposal with the author's instruction ("keep
   SSO", "shorter"); Re-analyze re-reviews one block after the author edited it.
6. `[x]` **Streaming by section.** The AI review runs section by section and appends cards as each finishes
   (cached per section), so long topics show results in seconds.
   Done, verified in Flare: AI reviews are saved to `%APPDATA%\FlareAI\reviews.json` and reappear instantly after a
   restart; rejections (with reason) persist in `dismissed.json`; every decision is logged to `feedback.jsonl` for 2e;
   the system prompt is sent with prompt caching. A reported diff glitch (a pending deletion shown as "removed") could not
   be reproduced: Diagnostics now logs the live tracked-change XML, and Flare's in-memory changeData matches the reader
   for both loaded and in-session changes.
7. `[x]` **Bulk mode.** "Propose as tracked changes": every accepted-in-principle proposal is written as a
   tracked change with a Flare annotation carrying rule + why, for Flare's own Review tools and review packages.
   Done as "Propose all as tracked changes": every exact proposal (rule fixes + AI wording) is written as tracked changes
   in one step for review in Flare's Review tab or a review package. Annotations are left out: inserting MadCap
   elements from the plugin is blocked by the validator and hits Flare 22's redo crash. Verified in Flare (9 changes).

### Phase 2e — Team sync + feedback loop (strict ownership) `[~]`

No hosted server: the company guide is a Flare project in git (Flare's own source control); each writer's plugin
runs docstyle locally. Shared rules change only through the guide owner(s); git branch protection enforces it.

1. `[x]` **Team context.** Owners listed in the guide (`<guide>/FlareAI/owners.txt`); writer = git `user.email`
   (fallback: Windows user). Owners file present = strict mode: non-owners' "Ignore rule…" becomes a proposal to the owner.
2. `[x]` **Shared feedback.** Every decision also goes to `<Feedback folder>/<writer>.jsonl` (setting; a OneDrive/
   SharePoint-synced folder, network share, or repo writers can push to). Append-only per writer = no merge conflicts.
3. `[x]` **Guide version.** The guide's commit and "N changes behind origin" (background fetch), shown in the pane.
4. `[x]` **Tune style guide** (owner report): per-rule acceptance across writers; rules to review (threshold: many
   decisions, several writers, high rejection); disputed rules (writers split); repeated identical edits (candidate
   Avoid/Use rows); ignore requests; evidence = counts, reasons, real excerpts, who.
   Verified in Flare with a git clone of the guide one commit behind, an owners file, and seeded feedback from two
   writers: report items, evidence, Open rule, behind-notice, and a non-owner's ignore request landing in the shared
   folder. A guide names itself in `FlareAI/layer.txt` so rule ids match across clones.
5. `[x]` **AI-drafted guide edits** (owners): Tune ▸ Draft a change with AI finds the rule's bullet/row in the guide
   topic, drafts the change from the team's evidence (scope, exception, new Bad/Good pair), shows the readable diff,
   applies as tracked changes (no lint on guide topics). Verified in Flare on a disputed rule.
6. `[x]` **Prompts as files**: `<guide>/FlareAI/prompts/*.md` overrides the built-in templates for everyone using the
   guide; Tune ▸ Customize AI prompts seeds them.
7. `[x]` **Style-guide builder** (AI ▸ Build style guide): docstyle `mine_style` reads every topic/snippet and proposes
   rules for spelling variants, competing words (plurals paired), inconsistent capitalization and heading case, with
   counts and real sentences, minus what the guide already enforces; the owner picks the preferred form (the
   "interview"). Notes, an old guide or another tool's prompt → AI-extracted rules quoting their source. Checked rules
   are written to `StyleGuide/From-our-content.htm` (Avoid/Use table + Conventions bullets). Verified in Flare: 7 rules
   added, Validate passed all 22 table rows, and caught a duplicate from the notes (now filtered).

Next phases (agreed order): **2e** feedback loop (rejection reasons → per-rule precision; teach actions that edit
the company guide; prompts as overridable files in the guide project) · **2f** accessibility, white-label and
snippet-impact checks · **2g** LLM-ready output on build (per-topic Markdown + llms.txt via CompileComplete / build
events) · **2h** authoring assists (alt text, micro content, search synonyms, glossary, meta descriptions).

### Phase 2f — Accessibility, structure, white-label `[x]`

Deterministic DOM checks in the review pane and Check project (Kind "manual": Go to, fix in the editor, Re-analyze):
images without alt text, skipped heading levels, a second h1, tables without a header row, vague link text, and
hard-coded phone numbers / email addresses / URLs (white-label docs put them in variables; typed variable values were
already flagged). Snippet impact: proposal cards show how many files use the snippet.

### Phase 2g — LLM-ready output `[x]`

docstyle `--llm-export` (llm_export.py, stdlib): built HTML5 / Clean XHTML → Markdown per topic (main content only;
skin, nav, scripts dropped; lists, tables, links rewritten to .md) + `llms.txt` + `llms-full.txt` in the output.
Plugin: AI ▸ Export for LLMs, and an automatic export on Flare's CompileComplete (setting). Verified: madbuild output
and an in-app build exported automatically (trial-mode output is scrambled by Flare itself). The post-build event
command is documented in the README for madbuild/CI.

### Phase 2h — Authoring assists `[x]`

- **Alt text (vision):** the review pane's alt-text proposals offer "Suggest alt text…": the AI reads the image
  (PNG/JPEG/GIF/WebP) with the heading and paragraph as context, the author edits, and the alt is applied as a validated
  tracked change. Verified in Flare on a real screenshot.
- **Topic assists** (AI ▸ Topic assists): one call drafts a meta description, index keywords (with subentries), search
  synonym groups, micro content (question/answer pairs) and glossary candidates (minus terms the glossary has), each with
  a Copy button. Copy, not write: Flare's head metadata, index markers, synonym and micro-content files aren't safely
  writable through the plugin API.
- LLM clients send images (Anthropic image blocks; Bifrost/OpenAI image_url data URIs).

### Phase 2i — Everything inside the plugin `[x]`

Install is "copy the plugin folder, paste the key": no Python, uv or Vale.
- `StyleEngine` (Core) replaces the Python docstyle server: layered guide from Flare-project topics, lint rules compiled
  from guide tables (Avoid/Use and Pattern rows), override tables, variables and glossary rules, validation (examples,
  contradictions, Company↔Google duplicates and overrides, unknown override ids), mining, LLM export. Linting the
  250-topic corpus: 94 s → 0.3 s.
- One style guide project (`styleguide/`, installs as `Plugins\FlareAI\StyleGuide`): `Google/` (generated) and
  `Company/`. Teams copy it and point Settings at their clone.
- `tools/FlareAI.Mcp`: the same engine as an MCP stdio server and the `--llm-export` command for madbuild post-builds.
- Fixed on the way: .NET Framework's 2-connections-per-host default had serialized every "parallel" model call.

### Phase 2j — The full Google guide, routed by content `[x]`

- [x] `tools/google-style/snapshot.py`: all 70 pages of developers.google.com/style (CC BY 4.0) as Markdown with
      hashes; 3,855 citable source units. Google's AI-generated page summaries excluded.
- [x] `coverage.json`: each page core / pack (by content trigger) / word list / reference (with reasons).
- [x] `tools/FlareAI.GuideCompiler`: per page, a strict output contract (prose rules with Bad/Good, Avoid/Use rows,
      patterns; units covered; units not applicable with reasons), coverage enforced (retry naming missing units), every
      lint row through the real validator (retry with errors, then dropped and reported). `coverage.md` is the report.
- [x] Condition tags scope rules: Content.* (what a section must contain) and Product.* (products the guide's Product
      tag set lists). Each AI call gets the always-on rules plus only the packs its section needs, as two cache blocks.
- [x] Full compile (2,023 rules, $12) and test on Google's own examples: Bad flagged 65% → 98%, cited exactly 0% → 64%,
      Good left alone 60% → 81%. Word-list judgment rules load only when the text uses the word. `docs/testing/GOOGLE-GUIDE-REPORT.md`.

### Phase 3 — Tester handoff `[ ]`

- [x] `scripts\package.ps1` → `dist\FlareAI-<ver>.zip` with install instructions.
- [x] Write `docs\TESTER-GUIDE.md` (install, enable, enter key, the three buttons, how to
      report problems, where `diag.log` is).
- [x] Package refreshed (`dist\FlareAI-0.1.0.zip`: plugin, icons, docstyle with mining + LLM export); tester guide covers
      the review pane, structure checks, assists, LLM export, Build/Tune style guide and strict ownership.
- [x] Output contracts per task (structured outputs, run-addressed rewrites, check-and-retry), routing tiers
      (fast/standard/deep, team `FlareAI/models.json`), usage and cost in Diagnostics.
- [x] Stress test: synthetic 250-topic corpus with ground truth (`scripts/make-corpus.py`), headless bench
      (`tools/FlareAI.Bench`), model bake-off, in-Flare runs. Findings and fixes: `docs/testing/STRESS-REPORT.md`.
- [x] Tester pack: user guide (first a Flare project, now `docs-site`), test plan with
      expected results and bug template (`docs/testing`).
- [ ] Feedback loop: 1-week trial, collect findings in `docs\FEEDBACK.md`. (Needs real writers.)

### Phase 4 — Bifrost integration `[ ]`

Blocked on a running gateway: `BifrostClient` exists (now with image support); the gateway side (MCP registration,
virtual keys) needs the Bifrost host. Not needed for team sync (git + shared feedback folder cover it).

- [x] `BifrostClient : ILlmClient` → `POST {gateway}/v1/chat/completions`, header `x-bf-vk`, non-streaming.
- [x] Settings dialog has a "Provider" switch (Anthropic direct / Bifrost); gateway models fixed to the 5.5 family.
- [x] No gateway-side MCP: the style engine runs in the plugin, so the gateway only needs a virtual key.
- [x] Verified against a local gateway (`maximhq/bifrost:v2.3.2`): review, rewrite and vision on all three models.
- [ ] Verify the Settings dialog's gateway mode inside Flare.

See `docs\bifrost-migration.md`.

## 3. Test strategy

| Layer | How | Where it runs |
|---|---|---|
| Core unit | xunit, `FakeDocumentAdapter`, fixture topics | Mac or Windows, `dotnet test` |
| Validator property tests | Feed every fixture through Rewrite with a mock LLM that returns the input unchanged → must be a no-op; mock that drops a `MadCap:snippetText` → must be rejected | `dotnet test` |
| Prompt snapshot | Prompt builder output for each action is snapshotted; diffs are reviewed | `dotnet test` |
| API smoke | `scripts\smoke-api.ps1` (real call, tiny prompt) | Windows, `.env` key |
| Flare integration | Manual script in `docs\MANUAL-TESTS.md`: 12 scenarios (selection inside p, across two ps, inside a list item, inside a table cell, inside a dropdown, no selection, topic with snippets/variables/conditions, non-topic file open e.g. CSS, no doc open, bad key, network off, cancel mid-request) | Windows + Flare |
| Tester trial | One writer, one week, real project | Their machine |

## 4. Repo layout

```
house-style/
  PLAN.md                      ← this file
  README.md                    ← quick start
  CLAUDE.md                    ← working notes for Claude Code sessions on the Windows box
  FlareAI.sln                  ← created by bootstrap.ps1
  Directory.Build.props
  lib/B3.PluginAPIKit.dll      ← copied from Flare install, git-ignored
  rules/                       ← style rules shipped with the plugin (Phase 1–3)
  src/FlareAI.Core/            ← net40, no Flare refs
  src/FlareAI.Flare/           ← net40, the plugin
  tests/FlareAI.Core.Tests/    ← net48, xunit
  tests/fixtures/              ← real-shaped Flare topics
  scripts/                     ← PowerShell tooling
  docs/                        ← api-dump.txt, flare-api-notes.md, bifrost-migration.md (gateway setup), manual tests
```

## 5. Risks

- **Ribbon types are WPF.** The plugin must reference PresentationCore/PresentationFramework/
  WindowsBase/System.Xaml. If `RibbonControlData` lives somewhere unexpected, fix after the dump.
- **Ribbon mode only.** Tab is invisible in "Tool Strip" UI mode — add a context-menu fallback
  (`IDocument.GetUserPendingContextMenuItems`) in Phase 2 if the tester uses that mode.
- **Selection granularity.** If `GetXmlNodeList()` returns text nodes, `BlockSelector` walks up
  to the nearest body-level block. If it returns nothing for a caret, fall back to whole topic.
- **Live DOM vs copy.** If `GetXmlDocument()` is a copy, `ReplaceDocumentNode` must be fed
  nodes obtained from the same call that will be replaced; the adapter hides this.
- **Plugins folder is under Program Files.** Tester needs admin or IT to copy the folder.
- **Model id drift.** Default model is a setting; smoke-api validates it before handoff.
