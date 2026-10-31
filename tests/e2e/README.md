# End-to-end checks in real Flare

`Run-FlareE2E.ps1` starts Flare on a fresh copy of `corpus\DigitalBanking` for each scenario in `scenarios\`, drives
it, and writes `results.json` (plus Flare's state and a screenshot for each failure) under `E:\HouseStyle\e2e`.
The exit code is the number of failures.

Before each scenario starts Flare, `Test-E2EScenario.ps1` checks it in seconds:
- it parses
- it has no control characters
- its verbs and ribbon labels exist
- every `Assert-E2E` has a condition and a message, and the condition is a true or false value, not a list or pipeline
- the buttons, menu items, tabs, box names and dialog titles it names exist in the plugin's source
- the fixture has its paths and replace text

A scenario with problems fails as a **preflight failure**, with no Flare launch. Each scenario run also adds one line
to `E:\HouseStyle\e2e\history.log` with its time, result, branch and commit, error and results folder. So
`Get-Content E:\HouseStyle\e2e\history.log -Tail 20` shows a run of tries and why each one failed. Writing a scenario
follows "Writing and proving an e2e scenario" in the testing skill.

Each failure is a **VM failure** or a **test failure**:
- **VM failure:** Flare itself never came up (no main window, or the licensing wizard stuck), or it hung before the
  plugin reported ready. It's retried once. A pass on the retry shows as a warning, and both tries stay in
  `results.json` (`firstTry`).
- **Test failure:** everything else, and it's never retried. That covers Flare being up while the plugin never
  reports ready, a scenario failing before it starts Flare, and anything after the plugin is ready, hangs included,
  since a fix can deadlock Flare.

Each `results.json` row also has `installSeconds` (empty without `-Install`), `flareStartSeconds`, `queueSeconds`
(empty when unknown), `resolution` and `windowState`. The run-level values repeat on every row, and the Flare fields
are empty when the scenario failed before Flare started.

The summary line counts both kinds. The factory reports a fix whose only failures are VM failures as a VM problem,
not as a bad fix. A pass that needed a retry is named in the factory's PR.

```powershell
powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -Install          # build, install, run everything
powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -Scenario lint-menu
powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -NoAi             # skip ai-* scenarios (no key, no cost)
```

The factory runs the same entry point on its VMs (`scripts/vm-e2e.ps1` in the factory repo), one scenario per call. `e2e.yml` runs the full suite only when started by hand.

It closes any running Flare. Don't run it while you have unsaved work in Flare.

## How it drives Flare

- **Flare's own UI** (trial dialog, ribbon) through UI Automation, as a writer clicks it. It uses
  [FlaUI](https://github.com/FlaUI/FlaUI) (UIA3, MIT), restored from NuGet by `flaui\FlaUI.Deps.csproj` on first
  use. Every call times out (5 s to connect, 10 s per call), so an unresponsive Flare fails a call instead of
  blocking the run. Inside a wait, a timed-out call counts as "not yet".
- **A heartbeat.** Once the project is open, the hook writes `heartbeat.json` from Flare's UI thread every second,
  listing the open windows. If it's more than 60 s old, or Flare has exited, the run fails at once with "Flare's UI thread stopped
  responding" and names those windows (a modal dialog is the usual cause). It doesn't wait out the full timeout.
- **The issues pane and House Style's dialogs** through `src/FlareAI.Flare/E2EHook.cs`. Their WinForms controls lose
  their roles inside Flare's WPF dock, so UI Automation can't reach them. The hook is off unless Flare starts with
  `HOUSESTYLE_E2E_DIR` set. It then reads `cmd-N.json` files from that folder, clicks the same buttons and menus a
  writer would, and answers with the pane's and dialogs' state. It also points the plugin's settings at the run
  folder, so a run never touches yours.

## A scenario

A `scenarios\*.ps1` file takes `-Fixture` and throws on failure. Use the module's functions: `Start-FlareE2E`,
`Send-E2E <verb>` (`open`, `click`, `rowMenu`, `command`, `dialogClick`, `dialogSelect`, `state`, and the verbs below),
`Invoke-Ribbon`, `Invoke-RibbonMenu`, `Wait-PaneDone`, `Get-E2ESetting`, `Test-PaneOnScreen`, `Assert-E2E`. Name AI scenarios `ai-*` so `-NoAi` skips them and start one with `Start-FlareE2E -Ai`.
`e2e.yml` (by hand) runs them through the owner's Bifrost gateway with the Flare VM's own key. The factory's VM runs skip them.

### More verbs, state fields and helpers

A verb that fails answers `ok = false` with the message shown, so `Send-E2E` throws it. Verbs named `dialog*` answer at
once. The rest run on Flare's UI thread and answer when they return.

- **`paneFocus`** brings the pane's window forward and puts the focus in the issues list, selecting the first row when
  none is. It returns `focused`, false while a modal dialog is open. Then send keys yourself with SendKeys, so they go
  through Flare's dock as a writer's do.
  `Assert-E2E ((Send-E2E paneFocus).value.focused) "list focused"; [System.Windows.Forms.SendKeys]::SendWait("^{ENTER}")`
- **`paneType`** (`name`, `arg`) picks `arg` in a pane combo box or types it in a pane text box, by accessible name.
  Fails "No box named {name}" and lists the valid names. `Send-E2E paneType "Errors" -Extra @{ name = "Level" }`
- **`paneMenu`** (`arg`) clicks the item that starts with `arg` in the pane's open menu (Ignore's reasons or the row
  menu). Fails "No open menu" or "No menu item {arg}" (which lists the valid items). `SendWait("{DELETE}"); Send-E2E paneMenu "It's correct"`
- **`paneSize`** (`width`, `height`) sizes the floating pane, or tries the docked one and fails "Flare didn't resize
  the docked pane." `Send-E2E paneSize -Extra @{ width = 260; height = 600 }`
- **`paneFloat`** (`width`, `height`, both optional) floats the docked pane as Flare's Float command does and sizes the
  window. Returns `floating`, `width`, `height` (the pane's size afterwards) and `hosts` (the controls that hold it).
  In Flare 22 the pane sits in Flare's WPF dock (`SidebarView < WinFormsAdapter`), where the B3.AppKit float calls do
  nothing, so it returns `floating = false` there. Check `height` and skip a tall-pane check when it didn't grow.
  `Send-E2E paneFloat -Extra @{ width = 300; height = 800 }`
- **`replace`** (`arg` old text, `text` new text) replaces the first match in the active topic as one tracked change,
  as typing does. **`select`** (`arg`) selects the first match. The text must sit in one text run (no markup inside
  it). Both fail "No text {arg} in one text run" or "No topic is open."
  `Send-E2E replace "log in" -Extra @{ text = "sign in" }; Send-E2E select "sign in"`
- **`save`** saves the active topic as Ctrl+S does (check-on-save treats it as the writer's save). Returns `saved`.
- **`saveAs`** (`arg` a full path inside the run's hook folder) makes the next Save dialog in the pane write there
  instead of showing. One-shot. Fails "Save path must be inside the run folder". Pane code calls
  `E2EHook.TakeSavePath()` before its `SaveFileDialog`. Export Issues in a project report uses it (`scenarios/project-issues.ps1`). `Send-E2E saveAs (Join-Path $run.Hook "issues.csv")`
- **`dialogTab`** (`title`, `arg`) selects a dialog's tab by its text. Fails "No tab {arg}" and lists the valid names.
  `Send-E2E dialogTab "Advanced" -Extra @{ title = "Settings" }`
- **State.** `editor` is `{ path, selection }` for the active topic (the selection cut to 200 characters), null when no
  topic is open. `pane.size` is `{ width, height }`. A dialog's or the pane's `buttons` give check boxes and option
  buttons a `checked` value. `(Get-E2EState).editor.selection`
- **`Invoke-RibbonMenu -Button <label> -Item <label>`** opens a ribbon split button's menu and clicks the item.
  `Invoke-Ribbon` clicks a split button's main part. `Invoke-RibbonMenu -Button "Help" -Item "What's New"`
- **`Get-E2ESetting <Name>`** returns one field of the run's `settings.json`. It refuses `ProtectedApiKey`.
  `Get-E2ESetting LiveCheck`

## This machine's setup (once)

- Flare's `Plugins\FlareAI` folder must be a junction to `E:\HouseStyle\current`, not into AppData. Commands run
  from the Claude desktop app have their AppData writes redirected, so an install into AppData never reaches Flare.
- Trial Flare works. The runner continues past the licensing wizard.



