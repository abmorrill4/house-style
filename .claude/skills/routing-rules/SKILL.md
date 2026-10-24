---
name: routing-rules
description: Where the report routing rules live (rules/routing.json and Get-Route) and what an agent may and may not do with them. Use when classifying a report, extracting routing fields, proposing a rule change, or asking why a report took a route.
---

# Routing rules

**Agents propose fields. The script decides the route.** A report's route (hold, evidence, regression, auto-fix,
fix-for-review, brief-only, plan, docs-gap, pile) is computed by `Get-Route` in `scripts/factory/Triage.psm1` from
the rows in `rules/routing.json`, first match wins. No agent picks a route, and no label carries a route between runs.

## The contract
- **Input record** (what `Get-Route` reads, and what the decision log stores in full): the brief's `kind`, `security`,
  `confidence`, whether it was `reproduced` and its `repro_test`, the best `match` (issue or report, its relation
  `same`, `part-of`, `related` or `conflicts`, open or closed, curated or not, and its `area:*` label), the brief's
  **asks** (each distinct thing the tester wants: its kind, whether the brief wants it fixed now, how much the closest
  open issue `covers` it, `full`, `partial` or `none`, and that issue, resolved like a match), the brief's
  `area` when `rules/areas.json` lists it, the **diff facts** (changed lines and files, owner-only paths, behavior
  paths), the our-words check, and the cap state. Curated means open and labeled `from:*`.
- **Split reports.** Routing works per ask. An ask an open curated issue covers fully or partly becomes evidence on
  that issue, and for `partial` the comment carries what the issue lacks, so closing the report loses nothing. A bug
  ask never becomes evidence. A `fix_now` ask that an issue covers is planned work, so it is evidence too; only an
  uncovered `fix_now` ask goes to fix for review as a draft PR. Uncovered feature and friction asks get one new planned
  issue, listing only them. Evidence and that planned issue are posted whatever the route, a fix route included, and
  never for a hold at triage (a fix that turns into a hold later doesn't take back evidence already posted). The asks
  left over decide the route. A question that rides along with covered asks is answered by the issue they go to. An
  ask the judges split on (covered, but no one issue has a majority) sends the report to the owner. A report with evidence asks and other asks gets both:
  the evidence comments, and the route of the rest. A part-of or related match alone never makes evidence. An older
  record with no asks gets one ask from its kind and its first `same` match (owner decision 2026-10-24, from the
  shadow review in `planning/work/shadow-review/`).
- **Output**: one route and the reason, plus `evidence`: the issues that get an evidence comment. A plan or docs-gap route also carries the `area:*` label for the new public
  issue: the brief's, otherwise the first open matched issue's, otherwise none. The factory adds that label and puts
  the issue in Later on the board. Nothing attaches issues to themes any more (owner decision 2026-10-24).
- The diff facts and the our-words check are `Get-Route`'s own helpers. Don't compute them a second way.
- **Replay cases** in `tests/factory/routing-cases.json` hold only an input record and the expected route. No report
  text, ids or reasons, because the repo is public.

Read `rules/routing.json` and `Get-Route` themselves for the current rows and field names. If this skill and those
files disagree, the files win.

## What an agent does
- `hs-intake` fills the brief's fields from the report, as the schema asks, with the enums exactly as written.
- `hs-security-screen` answers whether the report or diff describes a vulnerability. That answer feeds `security`.
- `hs-match` fills `match`, citing only issues and reports that exist in the context it was given. It doesn't judge
  `covers`.
- **The coverage judge** (`.github/factory/cover-prompt.md`, run by `Set-AskCovers` in `Invoke-Factory.ps1`) decides
  each ask's `ref`, `covers` and `missing`, replacing the brief's own: Sonnet with no tools, shown the report, the ask
  and every open curated issue, three runs, majority by `Get-CoverVote` (covered at all by majority, then the issue
  more than half the votes named, else the ask is uncertain). Fewer than two usable answers fails the triage, to be
  retried, rather than read as none. Each ask's three runs are one run against the daily cap and one ledger entry. The
  brief proposes the asks, their kind and `fix_now`. Chosen on 2026-10-24 by comparing designs on the 11 shadow
  reports and 16 held-out ones (`planning/work/shadow-review/eval/`): 25 of 27 right, against 21 for the brief alone.
  Haiku was no good as a judge; Opus was no better than Sonnet.
- `hs-rules` reads the owner's decision log (`planning/work/routing-log.jsonl`, private) once a week and drafts rule
  changes where the owner keeps deciding the same way, with the replay cases passing. Every change, widening or
  narrowing, is the owner's call. Nothing changes a rule on its own.

## What an agent never does
- Pick or argue for a route in free text.
- Edit `rules/`, `scripts/factory/` or `tests/factory/` outside an owner-requested rule proposal, which the owner
  merges or not.
- Copy report text into a rule, a replay case or a commit.

## Suspended areas and `area:*` labels
`suspended` in `rules/routing.json` (and the areas the factory suspends after a reverted auto-fix) names the folder
under `src/FlareAI.Core/` or `src/FlareAI.Flare/` a fix changed most. A file directly in a project folder counts as
`Core` or `Flare`. This is how those names line up with the `area:*` labels, for the weekly rule proposal and the
command center:

| Area (folder) | Project | `area:*` label |
|---|---|---|
| `Style` | Core | `area:rules` |
| `Actions`, `Editing`, `Llm`, `Output`, `Prompts`, `Feedback`, `Core` (files at the root) | Core | `area:core` |
| `Settings` | Core | `area:settings` |
| `Update` | Core | `area:install` |
| `UI` | Flare | `area:pane` |
| `Icons`, `Flare` (files at the root, such as `Plugin.cs` and `Commands.cs`) | Flare | `area:ribbon` |
| `Samples` | Flare | `area:guide` |

Several of these (`Update`, `Settings`, `Llm`, `Editing`, `Feedback`) are owner-only, so a fix there is held and never
auto-fixed in the first place.

The `area:*` labels in turn belong to product areas in `rules/areas.json`, which is how the roadmap groups issues.
`area:ai`, `area:governance`, `area:feedback` and `area:factory` have no code folder of their own: they group issues
by what a writer would look for, and `area:factory` is engineering-only work (the factory, corpus, tests and CI).
