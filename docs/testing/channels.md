# Testing channels, cheapest first

Start at the top and move down only when a cheaper channel can't show the failure. Say which channel proves the bug
and which proves the fix. The full study is `planning/research/testing-channels.md` in the owner's project folder.

| # | Channel (`-Channel`) | Runs on | Proves | Add a test |
|---|---|---|---|---|
| 1 | `rule-fixtures` | any Windows runner | A lint rule fires, or doesn't, in each markup context | Edit the rule row in `styleguide/Company/`, run `FlareAI.FixtureGen.exe`, commit `tests/fixtures/rules`. |
| 1 | `bench` | any Windows runner | Lint and guide text across the corpus, against the baseline | Add or fix a span label in `corpus/DigitalBanking/labels.jsonl`, then `FlareAI.Bench.exe --no-ai --update-baseline` on purpose |
| 2 | `core` | any Windows runner | Engine, settings, updates, IO, prompts, parsing | A `[Fact]` in `tests/FlareAI.Core.Tests`, beside the class it tests |
| 3 | `scenarios` | any Windows runner | Document edits, undo, Change and Ignore, the review session, governance, AI flows with a scripted model | A `[Fact]` in `tests/FlareAI.Scenarios/<Area>/`: `using (var fx = ReviewFixture.Scratch(body, ScriptedReview.Review(...)))`, drive `fx.Session`, assert on `fx.Doc.Xml` |
| 4 | `flare-tests` | any Windows runner | Pane and dialog layout and behavior in real, hidden WinForms | A `[Fact]` in `tests/FlareAI.Flare.Tests`: build the form inside `Sta(() => ...)`, assert on controls and accessible names |
| 5 | `pester`, `vitest` | any runner | Release, guide publisher and e2e scripts; the feedback Worker | Pester 3: an `It` in `tests/<area>/*.Tests.ps1`. vitest: an `it(...)` in `tools/feedback-worker/test/` |
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
