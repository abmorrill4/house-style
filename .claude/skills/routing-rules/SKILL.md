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
  `same`, `part-of`, `related` or `conflicts`, open or closed, curated or not), the **diff facts** (changed lines and
  files, owner-only paths, behavior paths), the our-words check, and the cap state.
- **Output**: one route and the reason.
- The diff facts and the our-words check are `Get-Route`'s own helpers. Don't compute them a second way.
- **Replay cases** in `tests/factory/routing-cases.json` hold only an input record and the expected route. No report
  text, ids or reasons, because the repo is public.

Read `rules/routing.json` and `Get-Route` themselves for the current rows and field names. If this skill and those
files disagree, the files win.

## What an agent does
- `hs-intake` fills the brief's fields from the report, as the schema asks, with the enums exactly as written.
- `hs-security-screen` answers whether the report or diff describes a vulnerability. That answer feeds `security`.
- `hs-match` fills `match`, citing only issues and reports that exist in the context it was given.
- `hs-rules` reads the owner's decision log (`planning/work/routing-log.jsonl`, private) once a week and drafts rule
  changes where the owner keeps deciding the same way, with the replay cases passing. Every change, widening or
  narrowing, is the owner's call. Nothing changes a rule on its own.

## What an agent never does
- Pick or argue for a route in free text.
- Edit `rules/`, `scripts/factory/` or `tests/factory/` outside an owner-requested rule proposal, which the owner
  merges or not.
- Copy report text into a rule, a replay case or a commit.
