# Fixtures: real output from the pinned tools

The factory's parsers are built and tested only against output captured from the tool versions in
`scripts/factory/Pins.psd1` (factory-runtime spec section 12). `scripts/factory/Save-Fixtures.ps1` captures each one,
redacts it, and writes `<name>.json` here with its tool, version, machine and commands. `expected.json` lists every
fixture and the migration step that captures it. Nothing here is written by hand.

A parser test whose fixture isn't here yet skips, and its title says which migration step captures it. Once the
migration is done, only `claude-usage-limit` and `flare-licensed` may still be missing (`Fixtures.Tests.ps1`).

## Captured

From the pinned git (2.53.0.windows.1) and .NET SDK (8.0.426), on the host, with `Save-Fixtures.ps1 -Set git` and
`-Set trx`. These tools behave the same on the agents VM at their pinned versions.

- `git-patch-forms`, `git-patch-refused`, `git-apply-check`, `git-config-scopes`, `git-diff-name-only`, `git-worktree`
- `trx`

## Still to capture

| Fixture | Step | Set and where |
|---|---|---|
| `robocopy-exit-codes` | 3 | `-Set robocopy-exit-codes -VMName HSAGENTS`, after the `windows` pin is set |
| `rmdir-icacls` | 3 | `-Set rmdir-icacls -VMName HSAGENTS` |
| `get-process-owner` | 3 | `-Set get-process-owner -VMName HSAGENTS` |
| `get-netfirewallrule` | 3 | `-Set get-netfirewallrule -VMName HSAGENTS`, after `Set-FactoryEgress.ps1 -Role agents` |
| `get-scheduledtaskinfo` | 3 | `-Set get-scheduledtaskinfo -VMName HSAGENTS` |
| `sc-sdshow` | 3 | `-Set sc-sdshow -VMName HSAGENTS` |
| `wevtutil-gl` | 3 | `-Set wevtutil-gl -VMName HSAGENTS` (the "before" values come from setup's `C:\HouseStyle\setup-log\eventlog-before.json`) |
| `task-events` | 3 | `-Set task-events -VMName HSAGENTS` |
| `hyperv-shapes` | 3 | `-Set hyperv-shapes -VMName HSAGENTS`, elevated on the host, after the `host-windows` pin is set |
| `psdirect-failures` | 3 | `-Set psdirect-failures -VMName HSAGENTS`, elevated on the host. It turns the VM off and restores a temporary checkpoint. |
| `vmconnect-process` | 3 | `-Set vmconnect-process` with VMConnect open on the Flare VM |
| `runner-state` | 3 | `-Set runner-state -Case idle`, `busy` and `offline`, each while the runner is in that state |
| `fork-pr-approval` | 3 | `-Set fork-pr-approval -Case all_external_contributors`, and `-Case other` while the setting holds another value |
| `label-events` | 3 | `-Set label-events -Report <n>` for a report with a `decision:fix` by the owner, and one added by another account |
| `harness-results` | 3 | `-Set harness-results -Case pass`, `fail` and `vm-retry`, each with `-From` a `results.json` from an e2e run, after the `flare` pin is set |
| `flare-trial-active` | 3 | `-Set flare-trial-active -VMName HSFACTORY` |
| `flare-trial-expired` | 3 | `-Set flare-trial-expired -VMName <scratch copy>` (spec section 12: no network, runner off, clock past the trial's end) |
| `claude-success`, `claude-max-turns`, `claude-error` | 8 | `-Set claude -From <factory-logs fixtures folder>`, after the `capture` dispatch |
| `flare-licensed` | 18 | `-Set flare-licensed -VMName HSFACTORY`, once the license is on `flare-check-base`. May stay pending until then. |
| `claude-usage-limit` | when one happens | `-Set claude-usage-limit -From <factory-logs cli-errors folder>`. May stay pending. |

Two parsers have no shapes until their fixtures land, so they fail closed meanwhile: the Flare license read
(`ConvertFrom-FlareTrialRead`) reads every state as `unknown`, and the usage-limit detection uses its labeled interim
pattern.
