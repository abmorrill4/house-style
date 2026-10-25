# End-to-end checks in real Flare

`Run-FlareE2E.ps1` starts Flare on a fresh copy of `corpus\DigitalBanking` for each scenario in `scenarios\`, drives
it, and writes `results.json` (plus Flare's state and a screenshot for each failure) under `E:\HouseStyle\e2e`.
The exit code is the number of failures.

Each failure is a **VM failure** or a **test failure**:
- **VM failure:** Flare itself never came up (no main window, or the licensing wizard stuck), or it hung before the
  plugin reported ready. It's retried once. A pass on the retry shows as a warning, and both tries stay in
  `results.json` (`firstTry`).
- **Test failure:** everything else, and it's never retried. That covers Flare being up while the plugin never
  reports ready, a scenario failing before it starts Flare, and anything after the plugin is ready, hangs included,
  since a fix can deadlock Flare.

The summary line counts both kinds. The factory reports a fix whose only failures are VM failures as a VM problem,
not as a bad fix. A pass that needed a retry is named in the factory's PR.

In CI these run on the Flare VM (the `flare` runner). A factory fix is checked there too, by the host's Flare check,
never by a workflow, so `e2e.yml` skips `factory/*` branches.

```powershell
powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -Install          # build, install, run everything
powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -Scenario reopen-issues
powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -NoAi             # skip ai-* scenarios (no key, no cost)
powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -Install -PrepareOnly   # build and install only, without starting Flare
```

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
`Send-E2E <verb>` (`open`, `click`, `rowMenu`, `command`, `dialogClick`, `dialogSelect`, `state`), `Invoke-Ribbon`,
`Wait-PaneDone`, `Test-PaneOnScreen`, `Assert-E2E`. Name AI scenarios `ai-*` so `-NoAi` skips them. CI runs them only on `main`, nightly and by hand, because their
results vary from run to run. `Get-E2EHistory.ps1` shows how each scenario has done over recent runs.

## This machine's setup (once)

- Flare's `Plugins\FlareAI` folder must be a junction to `E:\HouseStyle\current`, not into AppData. Commands run
  from the Claude desktop app have their AppData writes redirected, so an install into AppData never reaches Flare.
- Trial Flare works. The runner continues past the licensing wizard.
