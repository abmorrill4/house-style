# The code factory

Two Hyper-V VMs on the owner's PC do the work.

- **The agents VM (HSAGENTS)** runs the factory, which works through tester reports from the private
  `house-style-reports` repo. Its GitHub Actions runner has the `agents` label. Every agent job runs as the
  `hsagent` user with only the Claude sign-in token, never a GitHub one. `hsagent` can reach only Anthropic, and NuGet
  during a package restore. The script user that runs the factory reaches GitHub. The VM has no Flare.
- **The Flare VM (HSFACTORY)** runs real Flare for the end-to-end checks (`tests/e2e`) and, as a runner with the
  `flare` label, the e2e workflow on every pull request from this repository's own branches. It also runs each saved
  fix's Flare check, which the host starts (below). The factory never runs here.

The host moves work between them. The agents VM leaves a check request in `C:\HouseStyle\flare-outbox`, the host task
"House Style Flare check" claims it, runs it on the Flare VM from `flare-check-base` with the network cut, puts the
Flare VM back on `factory-base`, and writes the result into the agents VM's `C:\HouseStyle\flare-inbox`. Every host
write into the agents VM goes through `Write-AgentsVM` (`FactoryVM.psm1`).

## Build or rebuild a VM (elevated PowerShell on the host)

```powershell
scripts\factory\New-FactoryVM.ps1 -Role agents -Iso E:\HouseStyle\vm\win11-ltsc-eval.iso   # HSAGENTS; -Role flare for HSFACTORY
scripts\factory\Install-FactoryTools.ps1 -Name HSAGENTS -NoFlare    # Git, gh, .NET 8, Claude Code, the hsagent user, the write scan
scripts\factory\Install-FactoryTools.ps1 -Name HSFACTORY            # Flare (trial), Git, gh, .NET 8
scripts\factory\Test-FactoryIsolation.ps1                           # the agents VM's isolation probes
```

`Set-FactoryEgress.ps1` runs elevated inside each VM, not on the host. Run `-Role agents` in the agents VM and `-Role
flare` in the Flare VM (see Network egress below).

- Windows 11 Enterprise LTSC evaluation, activated for 90 days. Rebuild from a newer evaluation ISO when it runs out.
- Each VM's admin password is in `E:\HouseStyle\vm\<Name>-credential.xml`, which only the owner's Windows account can
  read.
- Checkpoints: `agents-base` for the agents VM; `flare-check-base` (Flare with no runner, for checks) and
  `factory-base` (with the runner) for the Flare VM. A reset stops at once if its base checkpoint doesn't exist.

## Keeping them running

```powershell
scripts\factory\Install-Runner.ps1 -Name HSAGENTS -Labels agents -Service -Token <token>
scripts\factory\Install-Runner.ps1 -Name HSFACTORY -Labels flare -Token <token>
scripts\factory\Install-HostTasks.ps1                   # the host tasks, from origin/main into E:\HouseStyle\vm\bin
scripts\factory\Reset-FactoryVM.ps1 -Name HSAGENTS -Refresh    # after updating a VM's tools, and about monthly
scripts\factory\Reset-FactoryVM.ps1 -Name HSFACTORY -Refresh   # also after adding the Flare license
scripts\factory\Set-FlareInUse.ps1 -On                  # before using the Flare VM by hand; -Off afterwards
tests\e2e\Get-E2EHistory.ps1 -Runs 50                   # how each scenario has done lately
```

The host tasks, all `IgnoreNew`, run as the owner's elevated, interactive logon:

| Task | When | Limit |
|---|---|---|
| House Style Flare check | every minute | 4 hours |
| House Style HSFACTORY nightly reset | 03:00 | 6 hours |
| House Style HSAGENTS nightly reset | 03:30 | 6 hours |
| House Style HSAGENTS reset resume | when the Flare check task finds a reset that stopped early | 6 hours |
| House Style factory watch | every 5 minutes | 4 minutes |

- **Test-FactoryReady** is the first step of every e2e and factory job. On the Flare VM it fails in seconds, with the
  reason, if the runner has no desktop, the desktop is locked or disconnected, or C: is nearly full, and leaves
  `C:\HouseStyle\not-ready.txt`. On the agents VM (`-NoFlare`) it checks the disk, the job task, the egress rules, the
  write scan, the event log restriction, `carry-done` and the fork-PR approval setting.
- **Watch-Factory** starts either VM if it's off, and reboots the guest if its runner has been offline for 10 minutes,
  at most once every 30 minutes. For the Flare VM it also answers `not-ready.txt` with a reboot and leaves the VM alone
  while a check, the in-use flag or the failed-reset marker holds it. It never runs a command inside the agents VM.
- **The nightly resets** put each VM back on its base, so leftover files, Windows updates and disk growth don't pile up.
  The Flare VM's waits up to 4 hours for a running check or the in-use flag and keeps the nightly e2e results. The
  agents VM's carries `fix-state`, the outbox, the inbox and `factory-logs` across the restore (through
  `E:\HouseStyle\vm\hsagents-carry`), writes in what the host held while it ran, then writes `carry-done`. Each reset
  sets `E:\HouseStyle\vm\maintenance-<Name>` while it runs. Everything is logged to `E:\HouseStyle\vm\host.log`.
- **A failed reset of the Flare VM** leaves it off and offline with `E:\HouseStyle\vm\flare-reset-failed`. Nothing
  runs on it until the owner deletes that file.
- **The ai-\* e2e scenarios** run on pushes to `main`, nightly at 10:23 UTC (well after the reset) and by hand, never on
  a branch push. Their results vary from run to run, so they don't judge a change.
- **Get-E2EHistory** reads the last runs' `results.json` artifacts and shows each scenario's passes, test failures,
  VM failures and passes on retry. A scenario that keeps passing only on a retry points at the VM. One that fails now
  and then as a test failure is flaky.

## Things learned the hard way

- Flare's setup bundle installs nothing with `/quiet`, so the Visual C++ runtimes and Flare's MSI go in directly.
  The MSI then installs to `Program Files (x86)`.
- A fresh Flare asks for its UI language at start. `tests/e2e` turns that off with
  `HKCU\Software\MadCap Software\Flare\RunSelectLanguageUIOnStartup = False`.
- Run Flare and the runner **un-elevated**. An elevated Flare relaunches itself and drops its arguments and
  environment, so the plugin never sees `HOUSESTYLE_E2E_DIR`. The `factory` account can write Flare's Plugins folder
  instead.
- PowerShell Direct has no desktop. Anything that drives Flare runs in a task with an interactive logon for `factory`.

## The factory (`Invoke-Factory.ps1`)

It takes one open report per run: triage, then a route, then a fix when the route calls for one.

1. **Triage** (any new report, `problem` or automatic `error`). An agent reads the report, tries to reproduce a bug
   with a scratch test, compares it with the open issues, and writes a brief: what happened, the kind
   (bug, friction, feature, question, duplicate, noise), whether it reproduces and the test that should fail on main
   (`repro_test`), whether it describes a vulnerability (`security`), what it lines up with, 2 to 4 options with
   effort and risk, a recommendation and its impact on writers. The brief goes on the private report as a comment,
   labeled `triage:ready` or `triage:noise`. No branch, commit, PR or public issue. A failed triage gets
   `triage:failed` and is retried, three times at most.
2. **Route.** The brief only proposes. `Get-Route` (`Triage.psm1`) takes the first row that matches, from facts the
   script checks itself. The route goes on the private report as a comment with its routing record (the input, no
   report text), labeled `route:<route>` and, when the owner is needed, `waiting:<reason>`.
3. **Fix**, only for a fix route computed in the same run, or the owner's `decision:fix` (alone and added last). No
   label carries trust between runs, and `route:*` labels grant nothing. The fix agent gets the brief (and the
   owner's decision, if any) as its work order. If it can't fix it, the report goes back to the owner
   (`factory:needs-decision`), never `factory:done`. The factory refuses to set any `decision:*` label itself.

### The routing matrix

| # | Route | When | Live mode |
|---|---|---|---|
| 1 | hold | `security: true`, the fix touches owner-only code, or the our-words check fails | Nothing public; any branch stays on the VM. `waiting:security` |
| 2 | evidence | every ask is covered (`full` or `partial`) by an **open** public issue labeled `from:*` and written by the owner or the factory, apart from questions the docs answer riding along, and none is a bug; or `kind: duplicate` and `same` as another open report | A comment on each covering issue in our words, naming the ask and, for `partial`, what the issue lacks, with its evidence count. The report closed. Checked again right before posting |
| 3 | regression | `same` as a **closed** public issue | Handled as a bug (rows 4 to 6), the closed issue named |
| 4 | auto-fix | a bug, reproduced, every auto-fix check passes, and the fix changes only `.cs` files under `src/` or `tests/` plus its `changes/` note | PR labeled `factory:auto-fix`, CI, merge. Ships that night |
| 5 | fix-for-review | a bug, reproduced, and any check fails; or an ask the brief wants fixed now (`fix_now`: a small change to existing behavior, never a bug or a feature) | A **draft** PR with the reasons. `waiting:merge` |
| 6 | brief-only | a bug, not reproduced (or a duplicate of nothing open) | No code. `waiting:decide` |
| 7 | plan | a `feature` or `friction` ask no open issue covers | A public `from:tester` + `loop:auto` issue in our words, listing only the uncovered asks, with its `area:*` label, in Later on the board |
| 8 | docs-gap | a `question` and `docs_gap: true` | A public `from:tester` + `documentation` + `docs-gap` issue, with its `area:*` label, in Later on the board |

Auto-fix covers only the paths in `rules/routing.json`'s `autoFix.paths` (C# under `src/` and `tests/`) and
`autoFix.dataPaths` (the fix's own note under `changes/`). The note must be new, with no link, and at most 20 lines
and 1,000 characters. A fix that changes any other file, or edits a note, or whose note breaks those limits, goes to
fix-for-review instead.

**Split reports.** The brief splits a report into its asks (one to four), and routing works per ask. Which open
issue covers each ask, and how much, is decided by the coverage judge (`.github/factory/cover-prompt.md`): Sonnet with
no tools, shown every open curated issue, three runs and a majority. Each ask's three runs count as one run against the daily cap and one ledger entry. Asks an open
issue covers become evidence whatever the route, and the asks left over pick the row: a report with one covered ask
and one bug adds the evidence and goes to the bug rows. A bug never becomes evidence, and a part-of or related match
alone never does either (owner decision 2026-10-24).
Uncovered feature and friction asks get one new planned issue whatever the route, listing only them. A question riding
along with covered asks goes with them, unless the docs don't answer it (`docs_gap`): then it gets a docs-gap issue,
or a place in the new planned issue. A second fix-now ask, or one beside a bug, is planned. A fix-now ask that an open issue covers gets both: the draft
fix and the evidence on that issue (owner decision 2026-10-24). An ask the judges split on sends the report to the owner (brief-only).

The `area:*` label of a plan or docs-gap issue is the brief's `area` when `rules/areas.json` lists it, otherwise the
area of the first open public issue the brief lines up with, otherwise none (the roadmap shows it under No area yet).
Putting the issue in Later uses the board ids in `rules/areas.json` and needs `FACTORY_GH_TOKEN` to have access to the
owner's projects; if that fails, the route comment says to set When by hand and nothing else fails.
| 9 | pile | `noise`, or a question the docs answer | `triage:noise`, open |

Matches in the brief count only once the script finds them in the issue lists it fetched itself. The auto-fix checks,
all run by the script with thresholds from `rules/routing.json` (read from main before any agent runs):

1. The named `repro_test` is added by the fix, **fails on the base commit with an assertion** (the fix's test files on
   top of main) and **passes on the fix**. Both runs are `dotnet test --filter`, with no secrets, results read from
   TRX, matching the fully qualified name exactly (or with a theory's arguments after it). A test that doesn't build on
   main, passes there, or fails without an assertion fails the check. The route is posted before the fix starts, so a
   fix that fails still leaves its route on the report.
2. `confidence: high`.
3. At most 150 changed lines and 5 files, not counting the test files that add the named test.
4. No behavior paths: `styleguide/`, `docs/user-guide/`, `Commands.cs`, `*.resx`, settings classes, or changed string
   literals in `src/FlareAI.Flare/UI/`.
5. No owner-only path (a hold, list below).
6. Our words: the added lines (with the `changes/` note), commit messages and PR text share no run of 8 words with the
   report, and hold no email address, UNC path or user-profile path (a hold). The evidence comment and plan or docs
   issue are checked the same way before they're posted.
7. At most 3 auto-merged fixes per UTC day (merged PRs labeled `factory:auto-fix`). Over the cap, the report gets
   `factory:queued` and is triaged and routed again once the next day has room. A fix that changes a file another
   open factory PR changes goes for review.
8. **No test tampering.** A deleted or changed line in an existing file under `tests/`, a deleted test file, or any
   change (new files too) to protected test data goes for review: `tests/baselines/`, any `fixtures/` or `golden/`
   folder under `tests/`, `tests/FlareAI.Scenarios/Output/`, `corpus/` (its ground truth included), `tests/FlareAI.TestKit/`,
   `tests/Isolation/`, project wiring (`*.csproj`, `Directory.Build.*`) and the e2e harness (also refused outright).
   New test files are fine, and so is a hunk in an existing test file that only adds whole new tests: C# members
   that start with a test attribute (`[Fact]`, `[Theory]`, `[InlineData]` and the like, never a Skip) and end with
   their balanced closing brace, or complete Pester `It`, `Describe` or `Context` blocks with no `-Skip` or
   `-Pending`. That's how a reproduction test is written. A hunk with a line that could hide code over several
   lines goes for review whatever else it holds: a C# preprocessor directive (`#if`, `#endif`), a block comment or
   verbatim or raw string left open, or a Pester block comment mark (`<#`, `#>`) or here-string. Any other added line in an existing test file (inside an
   existing test, a `using`, a helper, another attribute) goes for review. `$TestProtected`, `Get-TestTamper` and
   `Test-WholeTests` in `Triage.psm1`. The same check applies to a fix the owner approved with `decision:fix`: a
   tampering diff there opens a draft PR with the reason instead of merging.
9. **Not in a suspended area.** The area is the folder under `src/FlareAI.Core/` or `src/FlareAI.Flare/` the fix
   changed most (`Style`, `UI`, ...; a file directly in a project folder counts as `Core` or `Flare`). See lane
   suspension below.

### The kill switch: `FACTORY_MODE`

One repository variable, read by `factory.yml` (triage and fix) and `nightly.yml`:

- **`off`**, unset, or anything it can't read: the factory fails closed. `factory.yml` doesn't start the job, and
  `Invoke-Factory.ps1` checks again and stops before it reads or writes anything. `nightly.yml` tags nothing, so
  nothing is released either.
- **`shadow`**: the factory triages, routes and, for a fix route, builds, tests and reviews the fix on the VM as in
  live mode, but only posts the route on the private report. It pushes, merges and posts nothing public for a route,
  and the owner still decides every report the old way (`waiting:decide`). A fix the owner approved with
  `decision:fix` ships as before in shadow and live.
- **`live`**: routes act.

Manual runs need shadow or live too, dry runs included: a `factory` or `nightly` run with `dry_run` stops at the
switch like any other while the mode is off. Turning the factory on is setting it to shadow: `gh variable set FACTORY_MODE -R abmorrill4/house-style --body shadow`.
To go live, `--body live`; to stop everything, `--body off`. Claude may set `off` or `shadow` under the owner-ops
authorization. Only the owner sets `live`. Reports routed in shadow mode
keep their labels when it goes live; a `**Retry triage**` routes one again.

### Run controls

Limits live in `rules/routing.json` (`limits`), read at the start of each run.

- **Per agent:** at most 200 turns and `agentMinutes` (45) of wall-clock time. A run past the limit is stopped with
  its whole process tree.
- **Per day:** at most `agentRunsPerDay` (20) agent runs a UTC day, counted from the run ledger. At the cap no report
  is picked. A report whose next agent would go over the cap gets the reason and `factory:queued`, and is picked
  again once a new day has room.
- A run stopped by the wall-clock limit or the turn cap posts the reason on the report and labels it
  `factory:stuck`, `waiting:stuck` and `blocked:owner`, so the command center shows it as stuck.
- `featureLoop` (1) and `vmBatch` (1) are placeholders the command center shows as limits. Nothing enforces them yet.
- **Open factory PRs:** at `openFactoryPrs` (5) open `factory/*` PRs, no new fix starts. A fix route waits as
  `factory:wip-limit` and is routed again once a PR closes; an approved `decision:fix` waits with the same label.
- **The run ledger.** Every agent run is `claude -p --output-format json`. The script appends its tokens, turns and time
  as a comment on the pinned "Factory ledger" issue in the
  private reports repo, which it creates when missing. Numbers only, never report text. Dry runs are recorded too,
  because they spend tokens. The daily cap reads this ledger, never a file on the VM that agent code could write. A
  ledger the script can't read or write stops the run.
- **Blocked:** a report that gets stuck carries one `blocked:<cause>` label: `vm` (Flare or the VM failed),
  `flaky-test` (it passed on the VM and failed in CI on the same commit), `owner` (a cap, or triage failed three
  times) or `other`. It goes when the report is picked again.

### Holdout scenarios

A few e2e scenarios live in the private reports repo under `holdout/*.ps1`. The host's Flare check fetches them with
the owner's gh sign-in, after the fix's patch is staged, and runs them on the Flare VM with the public suite
(`Run-FlareE2E.ps1 -ScenarioDir`). The agents VM never sees them, and no agent holds a token that could fetch them. No
holdout folder is fine; the check logs it. A holdout failure is reported as "A holdout scenario fails on the fix", in
the private report only.

### Lane suspension

When an auto-fix is reverted (GitHub's Revert button or `git revert -m 1` on main, which write "This reverts commit
..."), auto-fix is suspended for its area: every fix there goes for review. Each run reads the reverts from main's
history and adds their areas to the ones `suspended` in `rules/routing.json` lists (the scorecard shows them all).
Nothing is reinstated automatically. The weekly rule proposal offers it once the area has 5 merged fix-for-review
changes and no reverts. The owner reinstates an area by adding it to `reinstated` with the date, so reverts before
then no longer count, and taking it off `suspended` if it's listed there.

### Weekly scorecard

`scorecard.yml` runs `Post-Scorecard.ps1` on Mondays and posts the week on the "Factory scorecard" issue in the private
reports repo: reports in, routes taken, merge rate, revert rate (factory PRs reverted within 14 days of merge, over the
factory PRs merged in 30 days), report-to-ship time, agent runs, tokens and cost per merged change, suspended areas,
and rejection reasons as fixed codes (`$ReasonCodes` in `Triage.psm1`). Counts and codes only: no report text, titles
or numbers. Owner touch time needs the waiting log on the owner's PC: `Post-Scorecard.ps1 -WaitingLog
~\.claude\waiting-log.jsonl`. `-DryRun` (or the workflow's `dry_run`) prints it instead of posting.

### Network egress (owner step)

`Set-FactoryEgress.ps1`, run elevated inside each VM, blocks outbound traffic by default and allows DNS to the VM's own
resolvers.
- **The agents VM (`-Role agents`):** `hsagent` gets HTTPS to Anthropic's API range only. NuGet opens only for the
  package-restore job, between two sweeps. The script user (`factory`) gets HTTPS to GitHub. Windows' own outbound allow
  rules are turned off, all but DHCP's, and the agents VM's ready check fails if any come back on.
- **The Flare VM (`-Role flare`):** `factory` gets HTTPS to Anthropic, GitHub and the hosts Flare and its setup need.
  The host also cuts the Flare VM's network while a fix's code builds and runs.

Run `-DryRun` first to see the rules, and again monthly with `Reset-FactoryVM.ps1 -Refresh`, since CDN addresses
change. `-Remove` undoes it, and turns Windows' own rules back on. The factory never runs it.

Waiting reasons, one `waiting:*` label per item: `security`, `merge`, `decide`, `stuck`, and, for later pieces,
`want`, `plan-input`, `plan-review` and `rule-change`. The `triage` skill logs every owner decision on a routed report
in the private `planning/work/routing-log.jsonl`. Replay cases for `Get-Route` are in
`tests/factory/routing-cases.json` (input records and expected routes only).

Guards against report text steering the agent:

- Only reports the feedback service filed (author `abmorrill4`, label `problem` or `error`).
- The report reaches the agent as a file, framed as untrusted data, never inside the prompt.
- No GitHub token while an agent runs or while agent-written code runs (the unit tests and e2e run with no secrets at
  all). The token is used only to push, open the PR and merge. The checkout keeps no credential.
- The agents never run `dotnet` themselves, and they have no e2e channel. Flare checks run only on the Flare VM. Their
  only test command is the base commit's copy of `scripts/agent/test-channel.ps1` in `agent-tools`, which `hsagent`
  can read but not change, and which removes every secret, their own sign-in included, from the child process. The
  agents also can't edit `scripts/` or the harness (`--disallowedTools`).
- Before and after each job, the sweep stops every `hsagent` process, removes its tasks and BITS jobs, deletes the
  job folders and restores `hsagent`'s profile from its template.
- `decision:fix` counts only when the owner's account added it last.
- Text the factory would publish is held if it holds something shaped like a key or token (the same shapes the
  feedback worker redacts).
- The agents sign in only with the owner's Claude Max plan (`CLAUDE_CODE_OAUTH_TOKEN`, from `claude setup-token`),
  never an API key. Without that secret the run stops.
- None of the run's git calls use hooks (`core.hooksPath` points at an empty folder), so a hook planted in `.git/hooks`
  never runs with the token.
- The agents get no web tools and only a short list of commands. That's a speed bump, so the real checks happen
  outside the agent:
  - the working tree must be clean afterwards;
  - prompts are read from `main` before the agent starts;
  - paths are checked without rename detection, so a move can't hide a deletion;
  - no mode changes or symlinks, and the diff has a size cap;
  - the e2e harness itself is off limits.
- The script reruns the unit tests and the e2e checks itself, rather than taking the agent's word.
- A second agent reviews the diff only, never the report.
- A fix merges only after every required check on that exact commit has passed.
- The public PR says only "Fix tester report n". What the tester wrote stays in the private reports repo.
- Routing rules come from `main` (`rules/routing.json`), read before any agent runs, and the fix agent can't change
  `rules/`, so no fix sets the bar it's judged by.
- The e2e workflow runs on pushes to this repo's branches, never on `pull_request`. For a PR, GitHub runs the
  workflow file from the PR itself, so a fork could rewrite any guard in it.
- A fix that touches owner-only code is held and goes back to the owner (`factory:needs-decision`, `waiting:security`):
  `src/FlareAI.Core/Update/`, `Settings/`, `Llm/`, `Editing/`, `Feedback/`, `Actions/GuideRequests.cs`, `Actions/Team.cs`,
  any line defining the `Git` class or `Git.Untrusted`, `src/FlareAI.Flare/E2EHook.cs`, and the factory's own gates
  (`.claude/agents/`, `.claude/skills/`, `.claude/workflows/`, `scripts/factory/`, `scripts/agent/`, `rules/`,
  `tests/factory/`, `.github/`). The list is `$OwnerOnly` in `Triage.psm1`.
  Nothing is pushed.
- The agents' output, the rerun tests' output and dry-run comments go to `C:\HouseStyle\factory-logs` on the VM, never
  the job log. This repo is public, so its job logs are too. A failed run logs only "report n is stuck", and the
  reason is on the private report. The report file is deleted before agent-written tests run.
- e2e runs on `factory/*` branches get no API key, so their `ai-*` scenarios are skipped.
- The e2e workflow has no concurrency group, because the single `flare` runner already queues its jobs. A group keeps
  only one waiting run and cancels the rest. The factory has its own group, so two factory runs never overlap.
- Each e2e scenario has a watchdog (`-ScenarioMinutes`, 12 by default, above the scenarios' own waits). A hung
  scenario leaves a screenshot and the open windows' titles, then Flare is killed. The watchdog stands down if its
  run has already died.
