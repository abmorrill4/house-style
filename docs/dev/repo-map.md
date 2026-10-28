# Repo map

CLAUDE.md has the top-level map. This adds the files people ask about most. Prefer `Grep` and `Glob` over reading
folders whole, and never read `corpus/` wholesale (it is generated test data).

## Plugin (`src/FlareAI.Flare`, net48, the only code that sees Flare types)
| What | Where |
|---|---|
| Ribbon, commands, roles | `Plugin.cs`, `Commands.cs` (the one command table) |
| Editor bridge, every DOM write | `FlareDocumentAdapter.cs` (implements `IDocumentAdapter`) |
| Flare events (save, content changed, context menu, build) | `EditorIntegration.cs` |
| Docked pane | `UI/PanelHost.cs`, `UI/FindingsPanel.cs` |
| Dialogs | `UI/*Form.cs` (Settings, StyleRules, TuneGuide, BuildGuide, BatchPreview, Result, Setup, WhatsNew, feedback forms) |
| Update notice and restart | `UpdateFlow.cs` |
| Test hook for real-Flare e2e (owner-reviewed) | `E2EHook.cs` |
| What's new text | `whats-new.md`, built from `changes/` |

## Engine (`src/FlareAI.Core`, net40, no Flare, WPF or WinForms)
| What | Where |
|---|---|
| Lint, guide layers, rule catalog | `Style/` (`StyleEngine`, `GuideLayer`) |
| Review and rewrite flows, pane decisions | `Actions/` (`ActionRunner`, `ReviewSession`, `Team`, `GuideRequests`) |
| XHTML validation, diffs, tracked changes | `Editing/` |
| Model clients | `Llm/` (Anthropic and gateway) |
| Prompts (no style rules in them) | `Prompts/` |
| Settings, DPAPI key, first run | `Settings/` |
| Reports and redaction | `Feedback/` |
| Self-update, signature check | `Update/` (read `Update/CLAUDE.md` first) |
| Export for LLMs | `Output/` |
| Data folder root | `Paths.cs` |

## Tests
| Area | Where |
|---|---|
| Unit | `tests/FlareAI.Core.Tests` |
| Writer flows over the fake editor | `tests/FlareAI.Scenarios/<Area>/` over `tests/FlareAI.TestKit` (`Editor/FlareSimDocument`, `Llm/ScriptedLlmClient`, `Workspace/FakeWorkspace`) |
| One fixture per lint rule | `tests/FlareAI.RuleFixtures.Tests`, data in `tests/fixtures/rules` |
| Real WinForms, hidden | `tests/FlareAI.Flare.Tests` |
| Real Flare, no clicking | `tests/e2e` (`Run-FlareE2E.ps1`, `scenarios/*.ps1`) |
| Scripts (Pester 3) | `tests/factory`, `tests/release`, `tests/publish-guide`, `tests/agent` |
| Feedback Worker (vitest) | `tools/feedback-worker/test` |

The `testing` skill says which of these to use first.

## Other places
- `styleguide/Company/` holds team rule rows. `styleguide/Google/` is generated, never edited by hand.
- `changes/<name>.md` holds one note per writer-visible change (`changes/README.md`).
- The build factory (its agents, skills, review workflow, routing rules and scripts) lives in the private repo
  abmorrill4/house-style-factory, not here.
- `docs/api-dump.txt` is the real Flare API and `docs/flare-api-notes.md` the verified Flare behavior.
