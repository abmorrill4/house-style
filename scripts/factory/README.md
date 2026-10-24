# The code factory

A Hyper-V VM on the owner's PC runs real Flare for the end-to-end checks (`tests/e2e`) and, as a GitHub Actions
runner, the e2e workflow on every pull request from this repository's own branches. It also runs the factory, which
works through tester reports from the private `house-style-reports` repo.

## Build or rebuild the VM (elevated PowerShell on the host)

```powershell
scripts\factory\New-FactoryVM.ps1 -Iso E:\HouseStyle\vm\win11-ltsc-eval.iso   # about 20 minutes; -Force rebuilds
scripts\factory\Install-FactoryTools.ps1                                      # Flare, Git, gh, .NET 8, Claude Code
```

- Windows 11 Enterprise LTSC evaluation, activated for 90 days. Rebuild from a newer evaluation ISO when it runs out.
- A local account `factory` signs in by itself, because Flare needs a desktop. Its password is in
  `E:\HouseStyle\vm\factory-credential.xml`, which only the owner's Windows account can read.
- Checkpoints: one production checkpoint, `factory-base`, which the nightly reset goes back to (below). The build
  scripts take `windows-ready`, `tools-ready` and `e2e-ready` along the way. `Reset-FactoryVM.ps1 -Refresh` merges
  them into the disk.

## Keeping it running

```powershell
scripts\factory\Install-Runner.ps1 -Token (gh api -X POST repos/abmorrill4/house-style/actions/runners/registration-token --jq .token)
scripts\factory\Watch-Factory.ps1 -Install   # host task, every 5 minutes
scripts\factory\Reset-FactoryVM.ps1 -Refresh  # once, then after updating the VM's tools and about monthly
scripts\factory\Reset-FactoryVM.ps1 -Install  # host task, nightly at 03:00
tests\e2e\Get-E2EHistory.ps1 -Runs 50         # how each scenario has done lately
```

- **Install-Runner** (elevated, on the host) installs the runner and registers it with `-Token`. It starts the runner
  at sign-in in the factory account's desktop, un-elevated, and restarts it within 30 seconds if it exits. It also
  turns off the screensaver and sleep. Without `-Token` it only refreshes those settings.
- **Test-FactoryReady** is the first step of every e2e and factory job. It fails in seconds, with the reason, if the
  runner has no desktop, the desktop is locked or disconnected, or C: is nearly full. It also closes any Flare left by
  an earlier run. A failure leaves `C:\HouseStyle\not-ready.txt` in the VM.
- **Watch-Factory** runs on the host while the owner is signed in. It starts the VM if it's off. It reboots the guest
  if the runner has been offline for 10 minutes, for example a VM stuck at the sign-in screen (seen 2026-10-23), or if
  `not-ready.txt` says the last job found the desktop unusable. That's a guest reboot through Hyper-V, never a hard
  reset, and at most one every 30 minutes. Create `E:\HouseStyle\vm\maintenance` to keep it away during checkpoint
  merges or rebuilds, and delete it afterwards. Its log is `E:\HouseStyle\vm\watch-factory.log`.
- **Reset-FactoryVM** runs on the host every night at 03:00. It waits up to an hour for a running job, then holds
  Watch-Factory off and copies `C:\HouseStyle\factory-logs` to a dated folder under `E:\HouseStyle\vm\factory-logs`. It checks for a
  running job again just before the VM goes off, and skips the night if it can't read the runner's state. It puts the VM back
  on `factory-base` and waits for the runner to come online. Whatever happened in the VM since the base is gone:
  leftover files, Windows updates, disk growth. `-Refresh` makes the VM as it is now the new base. Run it after
  updating the VM's tools, and about monthly so Windows updates and the runner version stay current. Its log is
  `E:\HouseStyle\vm\reset-factory.log`.
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
| 2 | evidence | `same` or `part-of` an **open** public issue labeled `from:*`, or `kind: duplicate` and `same` as another open report | A comment on that issue in our words with its evidence count, the report closed. Checked again right before posting |
| 3 | regression | `same` as a **closed** public issue | Handled as a bug (rows 4 to 6), the closed issue named |
| 4 | auto-fix | a bug, reproduced, and every auto-fix check passes | PR labeled `factory:auto-fix`, CI, merge. Ships that night |
| 5 | fix-for-review | a bug, reproduced, and any check fails | A **draft** PR with the reasons. `waiting:merge` |
| 6 | brief-only | a bug, not reproduced (or a duplicate of nothing open) | No code. `waiting:decide` |
| 7 | plan | `feature` or `friction` | A public `from:tester` + `loop:auto` issue in our words, with its `area:*` label, in Later on the board |
| 8 | docs-gap | a `question` and `docs_gap: true` | A public `from:tester` + `documentation` + `docs-gap` issue, with its `area:*` label, in Later on the board |

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
To go live, `--body live`; to stop everything, `--body off`. Only the owner changes it. Reports routed in shadow mode
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
  (and dollars, only when `ANTHROPIC_API_KEY` paid for it) as a comment on the pinned "Factory ledger" issue in the
  private reports repo, which it creates when missing. Numbers only, never report text. Dry runs are recorded too,
  because they spend tokens. The daily cap reads this ledger, never a file on the VM that agent code could write. A
  ledger the script can't read or write stops the run.
- **Blocked:** a report that gets stuck carries one `blocked:<cause>` label: `vm` (Flare or the VM failed),
  `flaky-test` (it passed on the VM and failed in CI on the same commit), `owner` (a cap, or triage failed three
  times) or `other`. It goes when the report is picked again.

### Holdout scenarios

A few e2e scenarios live in the private reports repo under `holdout/*.ps1`. The script fetches them only after the fix
agent has finished, into a folder outside the checkout, runs them on the VM with the public suite
(`Run-FlareE2E.ps1 -ScenarioDir`), and deletes them before the review agent starts. The agent never holds a token, so
it can't fetch them, and the fix prompt doesn't change. No holdout folder is fine; the run logs it. A holdout failure
is reported as "A holdout scenario fails on the fix", in the private report only.

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

`Set-FactoryEgress.ps1`, run elevated inside the VM, sets Windows Firewall to block outbound traffic except DNS to the VM's own resolvers and
HTTPS from the `factory` account to the Anthropic API, GitHub and NuGet. Run `-DryRun` first to see the rules, and
again monthly with `Reset-FactoryVM.ps1 -Refresh`, since CDN addresses change. `-Remove` undoes it. The factory never
runs it.

Waiting reasons, one `waiting:*` label per item: `security`, `merge`, `decide`, `stuck`, and, for later pieces,
`want`, `plan-input`, `plan-review` and `rule-change`. The `triage` skill logs every owner decision on a routed report
in the private `planning/work/routing-log.jsonl`. Replay cases for `Get-Route` are in
`tests/factory/routing-cases.json` (input records and expected routes only).

Guards against report text steering the agent:

- Only reports the feedback service filed (author `abmorrill4`, label `problem` or `error`).
- The report reaches the agent as a file, framed as untrusted data, never inside the prompt.
- No GitHub token while an agent runs or while agent-written code runs (the unit tests and e2e run with no secrets at
  all). The token is used only to push, open the PR and merge. The checkout keeps no credential.
- The agents never run `dotnet` or the e2e harness themselves. Their only test command is a copy of
  `scripts/agent/test-channel.ps1`, which removes every secret, their own sign-in included, from the child process.
  Before each agent, the factory copies `scripts/agent` and the e2e harness from the base commit to a folder outside
  the checkout and allows only that copy, so an agent can't edit the script that runs with its sign-in. After each
  agent, those files in the checkout must still match the base commit and the copy must be unchanged, or the run
  stops. The agents also can't edit `scripts/` or the harness (`--disallowedTools`).
- Before each agent, the scratch folders are cleared of anything an earlier step left (holdout scenarios, e2e and
  reproduction output, and the factory account's temp folder).
- `decision:fix` counts only when the owner's account added it last.
- Text the factory would publish is held if it holds something shaped like a key or token (the same shapes the
  feedback worker redacts).
- The agents sign in with the owner's subscription (`CLAUDE_CODE_OAUTH_TOKEN`, from `claude setup-token`) when that
  secret is set, else with `ANTHROPIC_API_KEY`.
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
