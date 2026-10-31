# FlareAI.TestKit

Test-support library for scenario tests (`tests/FlareAI.Scenarios`). It lets a test drive real Core flows offline:
a Flare-faithful fake editor, scripted model answers, a localhost HTTP mock of the model endpoints, and a
temp workspace. No test-framework dependency, so the conformance suite can also run inside Flare.

## Editor: `FlareSimDocument`

`FlareSimDocument : IDocumentAdapter` behaves like `FlareDocumentAdapter` over Flare 22, as written down in
`docs/flare-api-notes.md` ("Verified write behavior", "Variables and redo"):

| Behaviour | FlareSim |
|---|---|
| `Edit(op, body)` | One undo step. Nested `Edit`/`ReplaceBlocks` join the outer operation. |
| `ReplaceSpan` outside `Edit` | Throws `FlareSimException`. |
| `ReplaceSpan` | Real `MadCap:change` markup (deletion then insertion), one Remove/Add id pair per operation; ids reach `head/MadCap:changeData` only when the operation ends. The text node is split: the original `XmlText` keeps the text before the span and leaves the tree when that is empty. |
| `SelectText` | First match of the span's text in its run (`Select(string)`). `WidenSpans = true` (default) widens with `TextRuns.UniqueSpan` first, as the adapter does; set it to `false` to see the raw trap. |
| `ReplaceBlocks` | Tracked; copies the new node's content and attributes into the old element (same object; the new node is never attached); a text-node target throws; extra new nodes are inserted, extra old nodes marked removed. |
| `Undo` / `Redo` | In place (node objects survive); Undo restores the original bytes. Redo of a node operation whose content holds a `MadCap:variable` throws `FlareSimRedoCrashException`. |
| `AcceptAll` / `RejectAll` | Review > Accept/Reject All Changes, each one undo step. |
| `Save()` | Writes the file; throws if a `SelectionMarker` reached the DOM. |
| `UntrackedWrites` | Live-DOM writes that bypassed the adapter (Core must never write the DOM directly). |
| `AuthorEdits(edit)` | The author types in the editor: the DOM changes without being listed in `UntrackedWrites`. |

Simplification: a tracked block replacement marks all old content removed and all new content added (Flare diffs words).

Other members: `FromFile`, `FromXml`, `Xml`, `BodyText`, `Variables`, `SelectXPath`, `SelectedRun`/`SelectionStart`/`SelectionLength`,
`UndoCount`, `RedoCount`, `UndoNames`, `OperationLog`, `OpenedByPlugin`, `IsDirty`, `SaveCount`, `Close()`.

## Conformance suite

`AdapterConformance.Cases`: one case per documented behaviour. Kinds: `Flare` (holds for FlareSim and for Flare),
`SimGuard` (a path FlareSim refuses because it crashes or corrupts Flare; never run inside Flare), `SimOnly` (needs a
FlareSim control). `AdapterConformance.RunAll(open, insideFlare)` runs them; the in-Flare self-test (#82) passes an
`OpenScratch` that opens a scratch topic and wraps `FlareDocumentAdapter` as `IConformanceDocument`.

## Model clients

- `ScriptedLlmClient`: queued replies (`Then`, `ThenText`, `ThenFail`), then rules (`When`, `WhenTask`, `Always`), else it
  throws. `ScriptedLlmClient.Failing(LlmFailure, status)` and `ScriptedLlmClient.Reply.Failure(...)` produce every
  `LlmFailure` (`Http` with any status, e.g. 401/403/429/500, `Network`, `Truncated`, `Refusal`, `Contract`) the way the
  live clients do. `ScriptedLlmClient.NoFindings()` answers reviews with no findings.
- `MockLlmServer`: an `HttpListener` on `http://localhost:<free port>` answering `/v1/messages` (Anthropic shape) and
  `/v1/chat/completions` (Bifrost gateway, OpenAI shape). `Enqueue(MockReply...)`, then `Default`. Replies: `Ok(text, stop)`,
  `Unauthorized()` (401), `ModelDenied()` (403), `RateLimited()` (429), `ServerError(500|529)`, `Error(status, type, msg)`,
  `Malformed()` (200, HTML body), `TruncatedBody()` (200, half the body, connection dropped), `Hang()`, and `.After(ms)`
  for slow answers. `Received` lists the requests (headers kept in memory only).

## Workspace: `FakeWorkspace`

`FakeWorkspace.FromCorpus("DigitalBanking")`, `FromFolder(path)` or `Empty()`; dispose it at the end of the test.

- A temp copy of the project (build output and per-user folders skipped), `PathOf("Content/...")`, `WriteFile`, `ReadFile`,
  `Variables` from `Project\VariableSets\*.flvar`.
- Open documents: `Open(path)` (the author), `OpenForPlugin(path)` / `HostOpen` (the plugin's host delegate: returns the
  open document or opens one marked `OpenedByPlugin`), `IsOpen`, `OpenDocuments`, `OpenedByPlugin`. `doc.Close()` unregisters.
- `MakeReadOnly(path)`: a Windows ACL deny-write/create/delete for the current user on a file or folder (inherited), like
  Program Files for a standard user; `ClearReadOnly()`; undone on dispose.
- AppData redirect: `Paths.Root` → `AppData` (settings, `diag.log`, writer id, feedback, dismissals, review cache).
  `Team.Current`, `Team.AnonymousIdPath`, `FeedbackLog.PersonalPath` and `Diagnostics` are reset on entry and restored on
  dispose; `FakeWorkspace.ResetTeam()` clears the team mid-test.
- `Clock` (`FakeClock`): stamps FlareSim's tracked changes. Core reads the real clock.
- Workspaces are exclusive (a second one waits for the first to be disposed), so tests that use them don't race on statics.
