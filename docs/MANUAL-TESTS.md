# Manual test script (run inside Flare)

Developer smoke script from the first builds (0.1), kept with its results. It still names the old **AI** tab and an
external linter command. For current testing, use `docs/testing/TEST-PLAN.md`.

Record result per row: ✅ / ❌ + note.

| # | Test | Steps | Expected |
|---|---|---|---|
| 1 | Plugin loads | Enable in Options > Plugins, restart | **AI** tab visible with Check style / Rewrite / Ask… / Settings / Diagnostics |
| 2 | Settings round-trip | Settings → paste key, pick model, Save; reopen | Key shows as ••••, model persisted, `%APPDATA%\FlareAI\settings.json` has no plaintext key |
| 3 | Diagnostics, caret only | Place caret in a `<p>`, click Diagnostics | `diag.log` shows doc path, empty selection text, node list, and the `<p>` chosen as block |
| 4 | Diagnostics, selection inside one p | Select a few words | Block = that one `<p>` |
| 5 | Diagnostics, selection across two ps | Select from mid-p1 to mid-p2 | Blocks = p1, p2 in document order, no duplicates |
| 6 | Selection inside `<li>` | Select text in a list item | Block = the whole `<ul>`/`<ol>` (body-level ancestor) — note if that is too coarse |
| 7 | Selection inside table cell | | Block = the `<table>` — note if too coarse |
| 8 | Selection inside dropdown body | | Block = `MadCap:dropDown` |
| 9 | Check style, no selection | Click with caret only | Whole topic analysed; findings dialog lists rule + excerpt + suggestion |
| 10 | Rewrite, plain paragraph | Select a wordy paragraph → Rewrite | Tracked-change replacement appears; Ctrl+Z undoes it in one step; accept/reject works |
| 11 | Rewrite, paragraph with variable + xref | | Variable and xref elements preserved byte-for-byte (compare XML in Text Editor view) |
| 12 | Rewrite, snippet block selected | Select a `MadCap:snippetBlock` | Plugin refuses (cannot edit snippet content inline) with a clear message |
| 13 | Ask… | "Turn this into a numbered list" on a paragraph | Valid `<ol>` replaces the `<p>` as tracked change |
| 14 | Non-topic file active | Open a `.css` or `.fltoc`, click any action | Friendly "open a topic first" message, no exception |
| 15 | No document open | | Same |
| 16 | Bad API key | Set a wrong key | Error dialog with HTTP 401 text, Flare stays responsive |
| 17 | Network off | Disable Wi-Fi | Timeout error within ~60 s, Flare stays responsive |
| 18 | Cancel mid-request | Click Cancel while spinner shows | Dialog closes, no later DOM write |
| 19 | Disable plugin | Options > Plugins > Disable, restart | Tab gone, no errors on exit |
| 21 | Findings list, lint first | Check style on a topic | Lint rows appear in seconds tagged [Google]/[Company]; AI rows appended later; Fix all usable meanwhile |
| 22 | Go to | Click a row | Exact excerpt highlighted in the editor |
| 23 | Fix all | Click Fix all | Word-level Added/Deleted pairs, no stray markup in saved XML; one Ctrl+Z undoes all; Ctrl+Y redoes without error; Accept All works |
| 24 | Rewrite paragraph | Select a "(needs rewrite)" row → Rewrite paragraph… | Preview says whether it passes the linter; Apply = tracked change; rows marked fixed |
| 26 | Word-level rewrite | Rewrite a wordy paragraph | Preview says "Applies as N word-level tracked change(s)"; unchanged phrases stay plain; undo/redo clean |
| 27 | Rewrite over pending changes | Rewrite or Ask on a paragraph with pending changes | Model sees current text; new edits land around the old ones, which stay pending; restructuring it is refused with a clear message |
| 25 | Linter down | Settings → clear Style linter command | Check style shows AI review only, with a note; Rewrite still works |
| 20 | Tool Strip UI mode | Switch Flare to tool strip mode | Note whether any entry point exists (expected: none until context menu added) |

## Results — 2026-10-08, Flare 2026 (22.0.9643), plugin 0.1.0, scratch project

| # | Result | Note |
|---|---|---|
| 1 | ✅ | AI tab appears immediately on Enable — no restart needed |
| 2 | ✅ | Key shows as "stored (protected)"; no plaintext in settings.json. Added "Clear stored key" link (there was no way to remove a stored key) |
| 3 | ✅ | Caret → text node in `<p>` → that `<p>` |
| 4 | ✅ | Selection in `<h1>` → `<h1>` |
| 5 | ✅ | p1 + p2 in order, no duplicates |
| 6 | ✅ | `<li>` → whole `<ol>` (coarse but fine for short lists) |
| 7 | ✅ | `<td>` → whole `<table>` (coarse — large tables will be slow/expensive) |
| 8 | ✅ | dropDownBody `<p>` → `MadCap:dropDown` |
| 9 | ✅ | Fixed: caret-only previously checked just the caret's block; now whole topic (8 blocks, 12 findings) |
| 10 | ✅ | Added/Deleted tracked changes; one Ctrl+Z undoes all; variable preserved |
| 11 | ✅ | xref preserved byte-for-byte, no stray xmlns in saved file |
| 12 | ✅ | Refuses with clear message (message doesn't wrap in the result box — cosmetic) |
| 13 | ✅ | `<p>` → `<ol>` as tracked change |
| 14 | ✅ | CSS active → "Open a topic in the XML Editor first." |
| 15 | ✅ | Same message |
| 16 | ✅ | "HTTP 401: API key is invalid."; Flare responsive |
| 17 | ⏭ | Not run (disabling network would cut the remote session) |
| 18 | ✅ | Response arrives after cancel, logged, never applied |
| 19 | ✅ | Tab gone after restart, no errors on exit |
| 20 | ✅ | (re-run after Phase 2c item 3) Tool Strip mode — Flare 2026 marks it Deprecated — shows an **AI** menu and a Check style / Rewrite / Ask… toolbar; ribbon mode unaffected |
| new | ✅ | Rewrite over pending tracked changes refuses: "Accept or reject them (Review tab) before rewriting." |
