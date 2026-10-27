---
name: testing
description: The House Style testing channels, cheapest first, how to add a test on each, and how to run one with scripts/agent/test-channel.ps1. Use when reproducing a bug, writing a verification plan, picking where a test belongs, or running tests as an agent.
---

# Testing channels, cheapest first

Start at the top and move down only when a cheaper channel can't show the failure. Say which channel proves the bug
and which proves the fix. The full study is `planning/research/testing-channels.md` in the owner's project folder.

| # | Channel (`-Channel`) | Runs on | Proves | Add a test |
|---|---|---|---|---|
| 1 | `rule-fixtures` | any Windows runner | A lint rule fires, or doesn't, in each markup context | Edit the rule row in `styleguide/Company/`, run `FlareAI.FixtureGen.exe`, commit `tests/fixtures/rules`. A case you can't fix yet goes in `tests/baselines/rule-fixtures.known-failures.json` (that list only shrinks) |
| 1 | `bench` | any Windows runner | Lint and guide text across the corpus, against the baseline | Add or fix a span label in `corpus/DigitalBanking/labels.jsonl`, then `FlareAI.Bench.exe --no-ai --update-baseline` on purpose |
| 2 | `core` | any Windows runner | Engine, settings, updates, IO, prompts, parsing | A `[Fact]` in `tests/FlareAI.Core.Tests`, beside the class it tests |
| 3 | `scenarios` | any Windows runner | Document edits, undo, Change and Ignore, the review session, governance, AI flows with a scripted model | A `[Fact]` in `tests/FlareAI.Scenarios/<Area>/`: `using (var fx = ReviewFixture.Scratch(body, ScriptedReview.Review(...)))`, drive `fx.Session`, assert on `fx.Doc.Xml` |
| 4 | `flare-tests` | any Windows runner | Pane and dialog layout and behavior in real, hidden WinForms | A `[Fact]` in `tests/FlareAI.Flare.Tests`: build the form inside `Sta(() => ...)`, assert on controls and accessible names |
| 5 | `pester`, `vitest` | any runner | Factory, release, guide publisher, agent scripts; the feedback Worker | Pester 3: an `It` in `tests/<area>/*.Tests.ps1`. vitest: an `it(...)` in `tools/feedback-worker/test/` |
| 6 | `conformance` | factory VM | The adapter against Flare's real editor | Not built yet (gap G1). Add the case to `tests/FlareAI.TestKit/Conformance/AdapterConformance.cs` so it runs on the sim now and in Flare once G1 lands |
| 7 | `e2e` | factory VM | Docking, ribbon, Flare events, anything only visible in Flare | Copy `tests/e2e/scenarios/reopen-issues.ps1`: `Start-FlareE2E`, `Send-E2E open`, `Invoke-Ribbon "Lint Topic"`, `Wait-PaneDone`, `Assert-E2E` |

**Until G1 lands, a FlareSim scenario doesn't prove an adapter or write-behavior bug for auto-fix.** Those go to
fix-for-review. FlareSim models Flare's rules from notes, and only the real editor proves them.

Quick picks:
- A rule fires wrongly in plain text: `rule-fixtures`. Only in a markup context: `rule-fixtures`, then `bench`.
- Which text changes, tracked-change shape, undo: `scenarios`.
- Pane logic (row states, Change All, Ignore): `scenarios` through `ReviewSession`. Pane layout: `flare-tests`.
- Settings, DPAPI, first run, updates, installer script: `core`.
- Docking, ribbon, context menu, check on save: `e2e`.

## Running a test

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/test-channel.ps1 -Channel core -Filter StaleFindingTests
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/test-channel.ps1 -Channel pester -Path tests/agent
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/test-channel.ps1 -List
```

- `-Channel full` runs the whole Release suite, as CI does. Run it before you push.
- It runs the test with every secret removed from the environment (no API key, no tokens), one run at a time. The
  hs-* agents run tests only this way, and only from the repository root.
- `-Filter` is a `FullyQualifiedName~` match for the .NET channels. `-Scenario` names one e2e scenario. AI scenarios
  (`ai-*`) never run here because they need a key.
- `e2e` needs real Flare and an unlocked desktop, so it runs on the factory VM. A reproduction there is two batches,
  one plugin build each: base, then head.

## Proving a reproduction

`scripts/agent/repro-must-fail.ps1 -Test <name> -Channel <channel>` checks that the named test is in the diff, fails on
the base commit **with an assertion failure** and passes on head. A build error, a missing type or an exception
doesn't count: write the test against code that already exists on base, so it compiles there and fails on the
behavior. If the bug is a crash, catch it in the test (`var ex = Record.Exception(() => ...); Assert.Null(ex);`).
`-BaseOnly` checks just the base side, for a reproduction written before the fix. It covers `core`, `scenarios`,
`flare-tests`, `rule-fixtures` and `pester` today.

## Writing and proving an e2e scenario

Each real-Flare run takes minutes, so get the scenario right before it runs, and learn from each failure before the
next run. The hs-e2e agent follows these steps. Anyone else writing a scenario does too.

**Before writing**
1. Prove the logic on a cheaper channel first (`flare-tests` or `scenarios`). The scenario proves only what Flare
   adds, such as docking, the ribbon and Flare's events. Keep it to one behavior and about 40 lines. Split a longer
   one.
2. Pick the test data from facts, not guesses.
   - Open the fixture topic (`corpus/DigitalBanking/Content/...`) and find the exact text you'll act on.
   - A Change needs an issue whose fix is a word swap. Rewrite issues need AI.
   - When no topic has what you need, put the topic in a known state first with `Send-E2E replace`.
3. List every Flare behavior the scenario relies on, and look each one up in `docs/flare-api-notes.md`. Name any you
   couldn't find as assumptions in your report.
4. Start from the closest existing scenario, and use only the verbs and helpers in `tests/e2e/README.md`.

**Writing:** use the Edit and Write tools only. Never write a file through Python, sed, echo or a here-string. Their
escapes turn `\b` and `\f` into control characters.

**Running**
1. Run `-Channel e2e-check -Scenario <name>` and fix every problem it lists. It takes seconds and needs no Flare.
2. Run `-Channel e2e -Scenario <name>`, one scenario at a time.
3. After a failure, read these before changing anything:
   - the last line of `E:\HouseStyle\e2e\history.log`
   - the `error` in `results.json`
   - `<scenario>-state.json` and `<scenario>.png` in the same folder
4. Name the cause as one of:
   - `test-data`: the text or issue the scenario expected isn't there
   - `flare-behavior`: Flare did something the notes don't say
   - `product-bug`: the plugin is wrong
   - `test-code`: a mistake in the scenario
   - `vm`: Flare itself didn't come up
5. Write one ledger line for the run: run, failing check, cause, evidence and what you changed.
6. A `flare-behavior` cause goes into `docs/flare-api-notes.md` in the same commit.
7. A `product-bug` cause stops the run. Report it with its evidence, and the session sends it to a builder.

**Limits:** stop and report after 2 runs that fail at the same check, or after 5 runs in all. A `vm` retry doesn't
count toward either.

## Rules for every test
- Never put report text, names or private paths in a test. Write your own minimal example; the repo is public.
- Never call the live API, the live Worker or GitHub from a test. Use `ScriptedLlmClient`, `MockLlmServer`,
  `MockFeedbackServer` or `MockReleaseServer`.
- Run the narrowest channel and filter while you work, and `scripts/agent/test-channel.ps1 -Channel full` before you
  push.
