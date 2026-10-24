<#
.SYNOPSIS The code factory: takes one open tester report. A new one is triaged into a brief and routed by a fixed
decision matrix (Get-Route in Triage.psm1). A clear-cut bug is fixed and checked in the same run. A report the owner
marked decision:fix is fixed too.
.DESCRIPTION Runs on the factory VM's runner. FACTORY_ROUTE_MODE=live acts on the routes. Anything else, the default
"shadow", posts the computed route on the private report and pushes, merges and posts nothing public for it.
Safeguards against a report, or code an agent wrote, doing more than the fix:
- Only reports filed by the owner's feedback service (author abmorrill4, label "problem" or "error") in the private reports repo.
- Triage never commits, pushes or opens anything; its scratch tests are thrown away. Only the owner sets decision:*.
- The brief only proposes a route. The script checks every claim it can before anything is pushed, merged or posted:
  the named test fails on the base commit with an assertion and passes on the fix (run with no secrets), the diff size,
  behavior paths, owner-only paths, our words, the daily cap. rules/routing.json is read from main before any agent runs.
- The fix step runs only for a route this run computed or the owner's decision:fix. No label carries trust between
  runs (route:* and waiting:* are for display). Fix for review only ever opens a draft PR.
- Evidence comments go only on open public issues labeled theme or from:*, checked again right before posting.
- The report reaches the agent as a file, framed as untrusted data, never inside the prompt.
- No GitHub token while an agent runs or while agent-written code (tests, e2e) runs; it comes back only to push,
  open the PR and merge. The checkout keeps no credential (persist-credentials: false); the push uses the token once.
- The agents get no web tools and a short command list; that's a speed bump, so the real checks are outside them:
  a clean working tree afterwards, prompts read from main before the agent starts, changed paths checked without
  rename detection (so a move can't hide a deletion), no mode changes or symlinks, a size cap, and the e2e harness
  itself is off limits.
- This script reruns the unit tests and the e2e checks; a second agent reviews the diff only, never the report.
- Merge only after every required check on the pushed commit has passed, pinned to that commit.
- The public PR says only "Fixes tester report n"; what the tester wrote stays in the private reports repo.
Needs: GH_TOKEN (repo and reports access), and CLAUDE_CODE_OAUTH_TOKEN (the owner's subscription, from
`claude setup-token`) or ANTHROPIC_API_KEY for the agents. Windows PowerShell 5.1 or PowerShell 7.
#>
param([int]$Issue, [switch]$DryRun)
$ErrorActionPreference = "Stop"
$repo = "abmorrill4/house-style"
$reports = "abmorrill4/house-style-reports"
$owner = "abmorrill4"
$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$here = Split-Path (Split-Path $PSScriptRoot)   # repo root
$allowed = '^(src|tests|styleguide/Company|docs/user-guide|changes)/'
$denied = '\.csproj$|\.csproj\.user$|Directory\.Build\.|Directory\.Packages\.props$|nuget\.config$|packages\.lock\.json$|^tests/e2e/(Run-FlareE2E\.ps1|FlareE2E\.psm1)$|^tests/e2e/flaui/'
# Code that updates the plugin, holds settings and keys, or talks to AI providers: a fix here goes to the owner.
$ownerOnly = '^src/FlareAI\.Core/(Update|Settings|Llm)/|^src/FlareAI\.Flare/E2EHook\.cs$'
# Routes act on the public repo only in live mode, and never in a dry run.
$routeMode = if ($env:FACTORY_ROUTE_MODE -eq "live") { "live" } else { "shadow" }
$live = $routeMode -eq "live" -and -not $DryRun
# The agents' output stays on the VM: this repository is public, so its job logs are too, and the agents read the report.
$logs = Join-Path $env:SystemDrive "HouseStyle\factory-logs"; New-Item -ItemType Directory -Force $logs | Out-Null
$token = $env:GH_TOKEN
$secrets = "GH_TOKEN", "GITHUB_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN"
# The agents sign in with the subscription token when there is one; an API key in the environment would win over it.
$agentStrips = @("GH_TOKEN", "GITHUB_TOKEN") + $(if ($env:CLAUDE_CODE_OAUTH_TOKEN) { @("ANTHROPIC_API_KEY") } else { @() })
if (-not ($env:CLAUDE_CODE_OAUTH_TOKEN -or $env:ANTHROPIC_API_KEY)) { throw "No CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY for the agents." }

# No git hooks for any git call this run makes: an agent or its tests could plant one in .git/hooks, and the script's
# own git calls (fetch, switch, push) run with the GitHub token. Child processes inherit this too.
$noHooks = Join-Path $tmp "factory-no-hooks"; New-Item -ItemType Directory -Force $noHooks | Out-Null
$env:GIT_CONFIG_COUNT = "1"; $env:GIT_CONFIG_KEY_0 = "core.hooksPath"; $env:GIT_CONFIG_VALUE_0 = $noHooks

# Errors name the gh command only: its arguments can hold a comment body, and job logs are public.
function Gh { $o = & gh.exe @args; if ($LASTEXITCODE) { throw "gh $($args[0]) $($args[1]) failed" }; $o }
function Say($n, $text) {
    if (-not $DryRun) { Gh issue comment $n -R $reports --body "**Factory:** $text" | Out-Null; return }
    $text | Add-Content (Join-Path $logs "report-$n-comments.log"); Write-Host "[comment #$n] $($text.Length) chars, in factory-logs"
}
Import-Module (Join-Path $PSScriptRoot "Triage.psm1") -Force
# Adds the labels in $add (never decision:*: only the owner approves a fix) and removes those in $remove.
function Label($n, $add, $remove) {
    $add = @($add | Where-Object { $_ }); foreach ($x in $add) { Assert-FactoryLabel $x }
    $remove = @($remove | Where-Object { $_ -and $add -notcontains $_ })
    if ($DryRun) { Write-Host "[label #$n] +$($add -join ',') -$($remove -join ',')"; return }
    $a = @("issue", "edit", $n, "-R", $reports); foreach ($x in $add) { $a += @("--add-label", $x) }; foreach ($x in $remove) { $a += @("--remove-label", $x) }
    Gh @a | Out-Null
}
# The display labels a report carries from an earlier triage or route, dropped when it is routed again.
function Get-StaleLabels($r) { @(Get-Labels $r | Where-Object { $_ -like "triage:*" -or $_ -like "route:*" -or $_ -like "waiting:*" -or $_ -eq "factory:queued" -or $_ -eq "factory:working" }) }

# Runs a block with the named environment variables removed, then puts them back.
function Without([string[]]$names, [scriptblock]$block) {
    $saved = @{}; foreach ($v in $names) { $saved[$v] = [Environment]::GetEnvironmentVariable($v); [Environment]::SetEnvironmentVariable($v, $null) }
    try { & $block } finally { foreach ($v in $names) { [Environment]::SetEnvironmentVariable($v, $saved[$v]) } }
}

# Claude Code without the GitHub token, without web tools, with only the commands the job needs. Its own sign-in
# (subscription token or API key) stays: it is the agent's credential. User settings only: the repository's .claude
# settings and hooks don't apply to the factory's agent. Claude's notes go to stderr, which Windows PowerShell would
# treat as a failure, so its exit code decides.
# The runner's PATH is fixed when it is registered and may not include Claude Code's own install folder.
$claude = @((Get-Command claude.exe -ErrorAction SilentlyContinue).Source, (Join-Path $env:USERPROFILE ".local\bin\claude.exe")) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $claude) { throw "Claude Code isn't installed for this account (no claude.exe on PATH or in ~\.local\bin)." }

function Agent([string]$prompt, $map, $tools, [string]$log) {
    foreach ($k in $map.Keys) { $prompt = $prompt.Replace("{{$k}}", $map[$k]) }
    $log = Join-Path $logs $log
    Without $agentStrips {
        $ErrorActionPreference = "Continue"
        $global:LASTEXITCODE = -1   # a claude that never starts must not look like success
        $prompt | & $claude -p --setting-sources user --permission-mode acceptEdits --allowedTools $tools --disallowedTools "WebFetch,WebSearch,Bash(gh:*),Bash(curl:*),Bash(git push:*)" --max-turns 200 *> $log
        $code = $LASTEXITCODE
        Write-Host "Agent output: $log on the factory VM"
        if ($code) { throw "claude exited $code" }
    }
}

function Assert-CleanTree($who) {
    Assert-GitConfig $who   # first: git status could run a program an agent configured (core.fsmonitor)
    $dirty = git status --porcelain
    if ($dirty) { throw "$who left changes outside its commits: $(@($dirty) -join '; ')" }
}

# The checkout's own git config may hold only harmless keys. Code an agent wrote could add ones that run programs
# (core.fsmonitor, core.sshCommand, credential.helper, url.*.insteadOf, ...), and the script's git calls run with the
# token. The checkout persists between runs, so this is checked at the start too.
function Assert-GitConfig($who) {
    $ok = '^(core\.(repositoryformatversion|filemode|bare|logallrefupdates|symlinks|ignorecase|autocrlf|longpaths)|remote\.origin\.(url|fetch)|branch\..+\.(remote|merge)|user\.(name|email)|gc\.auto|extensions\.worktreeconfig|maintenance\.auto)='
    $bad = @(git config --local --list | Where-Object { $_ -notmatch $ok })
    if ($bad) { throw "$who left git settings the factory doesn't allow: $(($bad | ForEach-Object { ($_ -split '=')[0] }) -join ', ')" }
}

# Auto-merged fixes left today (UTC). Shadow mode merges nothing, so its cap is always open. A count that fails reads
# as a full day.
function Get-CapLeft($rules) {
    if (-not $live) { return [int]$rules.autoFix.dailyCap }
    try {
        $day = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
        $merged = @(Gh pr list -R $repo --state merged --label "factory:auto-fix" --search "merged:>=$day" --limit 50 --json number | ConvertFrom-Json)
        [int]$rules.autoFix.dailyCap - $merged.Count
    }
    catch { 0 }
}

# The named test, run by the script with no secrets: once on the base commit with only the fix's test files added, once
# on the fix. "pass" when it fails there with an assertion and passes here, else why not.
function Invoke-ReproCheck([string]$name, $testFiles, [string]$base, [string]$branch) {
    $runs = @{}
    foreach ($side in "base", "fix") {
        $out = Join-Path $tmp "repro-$n-$side"; Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
        $head = git rev-parse HEAD
        Without $secrets {
            $ErrorActionPreference = "Continue"
            if ($side -eq "base") { git switch -q --detach $base; git checkout -q $head -- @testFiles }
            dotnet test FlareAI.sln -c Release --filter "FullyQualifiedName~$name" --logger trx --results-directory $out *> (Join-Path $logs "report-$n-repro-$side.log")
        }
        Assert-GitConfig "The reproducing test"
        Without $secrets { git reset -q --hard; git clean -xfdq -e bin -e obj; git switch -q $branch }
        $runs[$side] = @(Get-ChildItem $out -Filter *.trx -Recurse -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw })
    }
    Assert-CleanTree "The reproducing test"
    $why = Test-ReproResult $runs["base"] $name "fail"
    if (-not $why) { $why = Test-ReproResult $runs["fix"] $name "pass" }
    if ($why) { $why } else { "pass" }
}

# Open factory PRs, other than this report's, that change any of $paths.
function Get-Overlap($paths, [string]$branch) {
    $open = @(Gh pr list -R $repo --state open --limit 100 --json "number,headRefName,files" | ConvertFrom-Json)
    @($open | Where-Object { $_.headRefName -like "factory/*" -and $_.headRefName -ne $branch -and @($_.files | Where-Object { $paths -contains $_.path }).Count } | ForEach-Object { $_.number })
}

$NextSteps = @{
    "hold" = "Look at it privately. Nothing went to GitHub, and any branch stays on the factory VM."
    "evidence" = "Nothing for you. The issue gets the evidence and this report is closed."
    "auto-fix" = "Nothing for you. The fix merges once CI passes and ships in tonight's release."
    "fix-for-review" = "Review the draft PR. Merging it is the one step left."
    "brief-only" = "Decide with a label, ``decision:fix``, ``decision:plan``, ``decision:park`` or ``decision:decline``."
    "plan" = "Nothing for you yet. The feature loop picks it up and asks for its own checkpoints."
    "docs-gap" = "Nothing for you. The docs issue goes through the work loop."
    "pile" = "Glance at it with the rest of the pile."
}

$labelSet = @("factory:working", "factory:done", "factory:needs-decision", "factory:stuck", "factory:queued", "triage:ready", "triage:noise",
              "triage:failed", "decision:fix", "decision:plan", "decision:park", "decision:decline") +
            @($Routes | ForEach-Object { "route:$_" }) + @($WaitingReasons | ForEach-Object { "waiting:$_" })
foreach ($l in $labelSet) {
    if (-not $DryRun) { & gh.exe label create $l -R $reports --force | Out-Null }
}
if ($live) { foreach ($l in "factory:auto-fix", "loop:auto", "from:tester", "documentation") { & gh.exe label create $l -R $repo --force | Out-Null } }

# ---- pick one report: triage what's new first, then fix what the owner approved ------------------------------------
# A route computed in this run can lead to a fix in this run. Otherwise only decision:fix leads to code, and only the
# owner can set it: the feedback service sets fixed labels only, and Label refuses decision:* here.
$pickRules = Get-Content (Join-Path $here "rules\routing.json") -Raw | ConvertFrom-Json   # the cap only; the run reads main's copy
$all = Gh issue list -R $reports --state open --limit 200 --json "number,title,body,author,labels" | ConvertFrom-Json
$pick = Select-Report $all $owner $Issue (Get-CapLeft $pickRules)
if (-not $pick) { Write-Host "Nothing to triage and no fix approved."; exit 0 }
$r = $pick.Report; $mode = $pick.Mode; $n = $r.number
$stale = @(Get-StaleLabels $r) + "factory:working"   # what a new route replaces
$comments = @((Gh issue view $n -R $reports --json comments | ConvertFrom-Json).comments)
if ($mode -eq "triage") {
    $failures = Get-TriageFailures $comments
    if ($failures -ge 3) { Say $n "Triage failed three times. Over to the owner."; Label $n @("factory:stuck", "waiting:stuck") "triage:failed"; exit 0 }
}
if ($mode -eq "fix") {
    # One event per line, so more than 100 events still parse.
    $events = @(Gh api "repos/$reports/issues/$n/events?per_page=100" --paginate --jq '.[]' | ForEach-Object { $_ | ConvertFrom-Json })
    $why = Test-FixAllowed $null (Get-Labels $r) $events   # no route in this run: the owner's decision:fix alone
    if ($why) { Say $n "Not fixing this yet: $why. Over to the owner."; Label $n @("factory:needs-decision", "waiting:decide") $null; exit 0 }
    "decision:fix added by $(Get-LabelActor $events 'decision:fix')" | Add-Content (Join-Path $logs "report-$n-gate.log")
}
Write-Host "Report #$n ($mode, routes in $routeMode mode)"
Label $n "factory:working" $null

$routed = $null   # the route this run computed, when it leads to a fix
try {
    # The report as data: comments stripped, length capped, inside <report> tags the prompt tells the agent not to obey.
    $body = ($r.body -replace '(?s)<!--.*?-->', '') -replace '(?i)</?report>', '[report tag]'
    if ($body.Length -gt 8000) { $body = $body.Substring(0, 8000) + "`n[cut]" }
    $title = ("" + $r.title) -replace '(?i)</?report>', '[report tag]' -replace '[\r\n]', ' '
    $reportText = ("" + $r.title) + "`n" + ("" + $r.body)   # for the our-words check; never written anywhere
    $reportPath = Join-Path $tmp "report-$n.md"
    function Write-Report { Set-Content $reportPath ("<report>`n# " + $title + "`n`n" + $body + "`n</report>") -Encoding UTF8 }
    Write-Report
    $resultPath = Join-Path $tmp "fix-$n.json"; Remove-Item $resultPath -ErrorAction SilentlyContinue

    Push-Location $here
    git fetch -q origin main
    $branch = "factory/report-$n"
    if ($mode -eq "triage") { git switch -q --detach origin/main } else { git switch -q -C $branch origin/main }   # triage makes no branch
    git clean -xfdq
    Assert-GitConfig "An earlier run"
    $base = git rev-parse HEAD
    # Prompts and routing rules from main, read before any agent runs, so an agent can't rewrite the reviewer's
    # instructions or the thresholds its own fix is judged by.
    $fixPrompt = (git show "${base}:.github/factory/fix-prompt.md") -join "`n"
    $reviewPrompt = (git show "${base}:.github/factory/review-prompt.md") -join "`n"
    $triagePrompt = (git show "${base}:.github/factory/triage-prompt.md") -join "`n"
    $rules = (git show "${base}:rules/routing.json") -join "`n" | ConvertFrom-Json

    if ($mode -eq "triage") {
        # ---- triage: read, try to reproduce, compare with the roadmap, write a brief. Never a branch, PR or issue. --
        # Context for matching, gathered now while the script has the token (the agent gets none).
        $contextPath = Join-Path $tmp "context-$n.md"
        $open = Gh issue list -R $repo --state open --limit 300 --json "number,title,labels" | ConvertFrom-Json
        $ctx = @("# Open issues and themes in $repo", "")
        foreach ($i in $open) { $ctx += "- #$($i.number) $($i.title) [$(($i.labels.name) -join ', ')]" }
        $ctx += "", "# Other reports' triage state", ""
        foreach ($o in $all | Where-Object number -ne $n) { $ctx += "- report #$($o.number): $((@($o.labels.name | Where-Object { $_ -like 'triage:*' -or $_ -like 'decision:*' })) -join ', ')" }
        Set-Content $contextPath ($ctx -join "`n") -Encoding UTF8
        $briefPath = Join-Path $tmp "triage-$n.json"; Remove-Item $briefPath -ErrorAction SilentlyContinue
        $triageTools = "Read,Grep,Glob,Edit,Write,Bash(git status:*),Bash(git diff:*),Bash(git log:*),Bash(dotnet build:*),Bash(dotnet test:*)"
        Agent $triagePrompt @{ REPORT_PATH = $reportPath; CONTEXT_PATH = $contextPath; RESULT_PATH = $briefPath } $triageTools "report-$n-triage.log"
        # Scratch tests are thrown away: triage never leaves code behind, and may not commit. The git config is checked
        # before any git call, and the reset runs without secrets, since the agent could have set keys that run programs.
        Assert-GitConfig "The triage agent"
        Without $secrets {
            if (@(git rev-list "$base..HEAD").Count) { throw "The triage agent made a commit; triage may not change code." }
            git reset -q --hard $base
            git clean -xfdq -e bin -e obj
        }
        Assert-CleanTree "The triage agent"
        Remove-Item $reportPath, $contextPath -ErrorAction SilentlyContinue
        if (-not (Test-Path $briefPath)) { throw "The triage agent wrote no brief." }
        $brief = Get-Content $briefPath -Raw | ConvertFrom-Json
        $problems = @(Test-TriageBrief $brief)
        if ($problems.Count) { throw "The triage brief isn't usable: $($problems -join ' ')" }
        $triageComment = Format-TriageComment $brief
        Say $n $triageComment
        $triageLabel = if ($brief.kind -eq "noise") { "triage:noise" } else { "triage:ready" }

        # ---- route: the brief proposes, the script decides --------------------------------------------------------
        # Matches are checked against lists fetched here, so a brief can't point evidence at an issue we don't curate.
        $issuesAll = Gh issue list -R $repo --state all --limit 1000 --json "number,state,labels" | ConvertFrom-Json
        $in = New-RouteInput $brief @(Resolve-Matches $brief.matches $issuesAll $all $n) (Get-CapLeft $rules)
        $route = Get-Route $in $rules
        $pub = Get-PublicText $route $brief $n
        $ow = @()
        if ($pub.Count) {
            $ow = @(Test-OurWords $pub $reportText ([int]$rules.ourWords.runLength))
            $in.our_words = -not $ow.Count
            $route = Get-Route $in $rules
            if ($ow.Count) { $route.reasons = @($route.reasons) + $ow }
        }
        "$(Get-Date -Format o) triage route: $($route | ConvertTo-Json -Depth 5 -Compress)" | Add-Content (Join-Path $logs "report-$n-route.log")

        if ($route.build) {
            # A fix route computed in this run: build and check it now (the fix gate, Test-FixAllowed).
            $routed = $route; $routeIn = $in
            $mode = "fix"
        }
        else {
            $waiting = Get-WaitingReason $route $routeMode
            $add = @($triageLabel, "route:$($route.route)") + $(if ($waiting) { @("waiting:$waiting") } else { @() })
            if (-not $live) {
                Say $n (Format-RouteComment $route $in $routeMode "Live mode would go on like this. $($NextSteps[$route.route])")
                Label $n $add $stale
                return
            }
            switch ($route.route) {
                "hold" {
                    Say $n (Format-RouteComment $route $in $routeMode $NextSteps["hold"])
                    Label $n ($add + "factory:needs-decision") $stale
                }
                "evidence" {
                    $t = $route.target.number
                    if ($route.target.type -eq "issue") {
                        # Checked again right before posting: open, and labeled theme or from:*.
                        $target = Gh issue view $t -R $repo --json "number,state,labels,comments" | ConvertFrom-Json
                        if (-not (Test-CuratedIssue $target)) { throw "Issue #$t isn't an open issue we curate any more, so no evidence was added." }
                        $count = 1 + @($target.comments | Where-Object { ("" + $_.body).StartsWith("**Evidence from testers**") }).Count
                        $text = $pub["evidence comment"].Replace("**Evidence from testers**", "**Evidence from testers** ($count so far)")
                        Gh issue comment $t -R $repo --body $text | Out-Null
                        Say $n (Format-RouteComment $route $in $routeMode "Added as evidence on https://github.com/$repo/issues/$t. This report is closed.")
                    }
                    else {
                        Say $t "Report #$n is the same as this one and was closed."
                        Say $n (Format-RouteComment $route $in $routeMode "Closed as the same as report #$t.")
                    }
                    Label $n @($triageLabel, "route:evidence") $stale
                    Gh issue close $n -R $reports | Out-Null
                }
                { $_ -eq "plan" -or $_ -eq "docs-gap" } {
                    $pubLabels = if ($route.route -eq "plan") { @("from:tester", "loop:auto") } else { @("from:tester", "documentation") }
                    $la = @(); foreach ($x in $pubLabels) { $la += @("--label", $x) }
                    $url = Gh issue create -R $repo --title $pub["issue title"] --body $pub["issue body"] @la
                    $num = ("" + $url).Trim().Split('/')[-1]
                    # The issue exists from here on, so nothing below may fail the run and make a retry create it twice.
                    $under = ""
                    if ($route.theme) {
                        try {
                            $id = Gh api "repos/$repo/issues/$num" --jq ".id"
                            Gh api -X POST "repos/$repo/issues/$($route.theme)/sub_issues" -F "sub_issue_id=$id" | Out-Null
                            $under = " under theme #$($route.theme)"
                        }
                        catch { $under = ". Attaching it to theme #$($route.theme) failed, so attach it by hand" }
                    }
                    Say $n (Format-RouteComment $route $in $routeMode "Planned as $url$under. This report is closed.")
                    Label $n @($triageLabel, "route:$($route.route)") $stale
                    Gh issue close $n -R $reports | Out-Null
                }
                default {
                    # brief-only and pile, and an auto-fix queued for tomorrow
                    if ($route.queued) { $add += "factory:queued" }
                    Say $n (Format-RouteComment $route $in $routeMode $(if ($route.queued) { "It is routed again tomorrow." } else { $NextSteps[$route.route] }))
                    Label $n $add $stale
                }
            }
            return
        }
        # The fix step starts from the same base, on its own branch, with the report back on disk for the fix agent.
        git switch -q -C $branch $base
        Write-Report
    }

    # ---- fix: only for a route this run computed, or the owner's decision:fix -----------------------------------
    if ($routed) {
        $gate = Test-FixAllowed $routed (Get-Labels $r) @()
        if ($gate) { throw "The fix gate refused: $gate" }
        $orderText = $triageComment + "`n`n**Route:** $($routed.route), computed by the factory in this run. Add the test named above, with that exact name, and make it pass."
    }
    else {
        # The work order: the triage brief the owner decided on, and the owner's decision comment. Both quote nothing
        # from the report, but they came from an agent that read it, so they go in as data too.
        $briefComment = @($comments | Where-Object { $_.body.StartsWith("**Factory:** **Triage:**") }) | Select-Object -Last 1
        $decision = @($comments | Where-Object { $_.body.StartsWith("**Decision:**") }) | Select-Object -Last 1
        $orderText = $(if ($briefComment) { $briefComment.body } else { "(no triage brief)" }) + "`n`n" + $(if ($decision) { $decision.body } else { "(no decision comment)" })
    }
    $tools = "Read,Grep,Glob,Edit,Write,Bash(git status:*),Bash(git diff:*),Bash(git log:*),Bash(git add:*),Bash(git commit:*)," +
             "Bash(dotnet build:*),Bash(dotnet test:*),Bash(powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/e2e/Run-FlareE2E.ps1:*)"
    $orderPath = Join-Path $tmp "order-$n.md"
    Set-Content $orderPath ("<workorder>`n" + $orderText + "`n</workorder>") -Encoding UTF8
    Agent $fixPrompt @{ REPORT_PATH = $reportPath; ORDER_PATH = $orderPath; RESULT_PATH = $resultPath } $tools "report-$n-fix.log"
    # The agent's tests could print the report or anything else readable: from here on no report or order on disk,
    # and the output of agent-written code goes to factory-logs.
    Remove-Item $orderPath, $reportPath -ErrorAction SilentlyContinue
    Assert-CleanTree "The fixing agent"
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    $summary = ("" + $result.summary).Trim()
    if ($summary.Length -gt 1500) { $summary = $summary.Substring(0, 1500) }
    $commits = @(git rev-list "$base..HEAD")

    if ($result.kind -ne "fixed" -or -not $commits.Count) {
        # A fix was approved or routed and it didn't happen: back to the owner with the reason, never "done".
        Say $n "I couldn't fix this as $(if ($routed) { 'routed' } else { 'approved' }). $summary`n`nOver to the owner."
        Label $n @("factory:needs-decision", "waiting:decide") @("factory:working")
        return
    }

    # ---- check what the agent changed (no rename detection: a move shows as a delete plus an add) ------------------
    $changes = @(git diff --no-renames --name-status "$base..HEAD")
    $bad = foreach ($c in $changes) { $path = ($c -split "`t")[-1]; if ($path -notmatch $allowed -or $path -match $denied) { $c } }
    if ($bad) { throw "The fix changed files the factory may not touch: $($bad -join ', ')" }
    $modes = @(git diff --no-renames --summary "$base..HEAD" | Where-Object { $_ -match 'mode change|create mode 120000|delete mode 120000' })
    if ($modes) { throw "The fix changes file modes or adds symlinks: $($modes -join '; ')" }
    # Binary files count as no lines, so they'd get past the cap; a fix has no reason to add one (the harness loads DLLs).
    $binary = @(git diff --no-renames --numstat "$base..HEAD" | Where-Object { $_ -match '^-\t-\t' } | ForEach-Object { ($_ -split "`t")[-1] })
    if ($binary) { throw "The fix adds or changes binary files: $($binary -join ', ')" }
    $numstat = @(git diff --no-renames --numstat "$base..HEAD")
    $lines = ($numstat | ForEach-Object { $p = $_ -split "`t"; [int]($p[0] -replace '-', '0') + [int]($p[1] -replace '-', '0') } | Measure-Object -Sum).Sum
    if ($lines -gt 800) { throw "The fix is $lines lines; too big for the factory to merge on its own." }

    # ---- what the diff says, and our words over everything we would publish -----------------------------------------
    $diffText = (git diff --no-renames -U0 "$base..HEAD") -join "`n"
    $reproTest = if ($routed) { $routeIn.repro_test } else { "" }
    $facts = Get-DiffFacts $numstat $diffText $reproTest $rules $ownerOnly
    $addedText = (($diffText -split "`n") | Where-Object { $_.StartsWith("+") -and -not $_.StartsWith("+++") }) -join "`n"
    $prTitle = "Fix tester report $n"
    $prLead = "Fixes tester report $n (private reports repo)."
    $ow = @(Test-OurWords @{ "the added lines and changes note" = $addedText; "the commit messages" = ((git log --format=%B "$base..HEAD") -join "`n"); "the PR" = "$prTitle`n$prLead" } $reportText ([int]$rules.ourWords.runLength))

    if ($routed) {
        $repro = if (-not $reproTest) { "the brief names no test that fails on main" }
                 elseif (-not $facts.repro_in_diff) { "the fix doesn't add the test $reproTest" }
                 else { Invoke-ReproCheck $reproTest $facts.test_files $base $branch }
        $routeIn.fix = [pscustomobject]@{ repro = $repro; lines = $facts.lines; files = $facts.files; behavior = @($facts.behavior)
                                          owner_only = @($facts.owner_only); overlap = @(Get-Overlap @($numstat | ForEach-Object { ($_ -split "`t")[-1] }) $branch) }
        $routeIn.our_words = -not $ow.Count
        $routeIn.cap_left = Get-CapLeft $rules
        $routed = Get-Route $routeIn $rules
        if ($ow.Count) { $routed.reasons = @($routed.reasons) + $ow }
        "$(Get-Date -Format o) fix route: $($routed | ConvertTo-Json -Depth 5 -Compress)" | Add-Content (Join-Path $logs "report-$n-route.log")
        if ($routed.route -eq "hold") {
            Say $n (Format-RouteComment $routed $routeIn $routeMode $NextSteps["hold"])
            Label $n @("triage:ready", "route:hold", "waiting:security", "factory:needs-decision") $stale
            return
        }
        if ($routed.queued) {
            Say $n (Format-RouteComment $routed $routeIn $routeMode "It is routed again tomorrow. The branch is rebuilt then.")
            Label $n @("triage:ready", "route:auto-fix", "factory:queued") $stale
            return
        }
    }
    elseif ($facts.owner_only.Count -or $ow.Count) {
        # The owner approved this fix, but it touches owner-reviewed code or fails our words: it stays off GitHub.
        $why = @(); if ($facts.owner_only.Count) { $why += "it touches code the owner reviews (updates, settings, AI providers), $($facts.owner_only -join ', ')" }; $why += $ow
        Say $n "Not pushing this fix because $($why -join ', and '). The branch stays on the factory VM for the owner; nothing was pushed."
        Label $n @("factory:needs-decision", "route:hold", "waiting:security") "factory:working"
        return
    }

    # ---- test it again here, not on the agent's word; agent-written code runs with no secrets ----------------------
    Without $secrets {
        $ErrorActionPreference = "Continue"   # with Stop, redirected stderr from a native command throws; exit codes decide
        dotnet test FlareAI.sln -c Release *> (Join-Path $logs "report-$n-unit.log")
        if ($LASTEXITCODE) { throw "Unit tests fail on the fix." }
        $e2eOut = Join-Path $tmp "e2e-$n"
        Remove-Item $e2eOut -Recurse -Force -ErrorAction SilentlyContinue   # never read an earlier run's results
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\e2e\Run-FlareE2E.ps1 -Install -NoAi -OutDir $e2eOut *> (Join-Path $logs "report-$n-e2e.log")
        if ($LASTEXITCODE) {
            # In parentheses: Windows PowerShell's ConvertFrom-Json sends a whole array down the pipeline as one object.
            $failed = @((Get-Content (Join-Path $e2eOut "results.json") -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json) | Where-Object { -not $_.passed })
            if ($failed.Count -and -not ($failed | Where-Object kind -ne "vm")) { throw "Flare failed to start or stopped responding during the end-to-end checks, twice. That points at the factory VM rather than the fix; rerun the report once the VM is healthy." }
            if (-not (Test-Path (Join-Path $e2eOut "results.json"))) { throw "The end-to-end run stopped before any scenario ran (setup, install or the FlaUI restore). See report-$n-e2e.log on the factory VM." }
            throw "End-to-end checks fail on the fix."
        }
        $retried = @((Get-Content (Join-Path $e2eOut "results.json") -Raw | ConvertFrom-Json) | Where-Object { $_.passed -and $_.firstTry } | ForEach-Object scenario)
    }
    git clean -xfdq -e bin -e obj   # build output is ignored; anything else the tests left is dropped
    Assert-CleanTree "The tests"

    # ---- second agent reviews the diff, never the report ------------------------------------------------------------
    $diffPath = Join-Path $tmp "fix-$n.diff"; git diff --no-renames "$base..HEAD" | Set-Content $diffPath -Encoding UTF8
    $summaryPath = Join-Path $tmp "summary-$n.txt"; Set-Content $summaryPath $summary -Encoding UTF8
    $reviewPath = Join-Path $tmp "review-$n.json"; Remove-Item $reviewPath -ErrorAction SilentlyContinue
    Agent $reviewPrompt @{ DIFF_PATH = $diffPath; SUMMARY_PATH = $summaryPath; RESULT_PATH = $reviewPath } "Read,Grep,Glob,Write" "report-$n-review.log"
    Assert-CleanTree "The reviewing agent"
    $review = Get-Content $reviewPath -Raw | ConvertFrom-Json
    if ($review.verdict -ne "MERGE") { throw "Review blocked the fix: $($review.reasons -join '; ')" }

    if ($DryRun) { Write-Host "Dry run: would push $branch, open a $(if ($routed -and $routed.route -eq 'fix-for-review') { 'draft ' })PR$(if (-not $routed -or $routed.route -eq 'auto-fix') { ' and merge it' })."; return }
    if ($routed -and -not $live) {
        # Shadow: the route and its checks are posted privately; the branch stays on the factory VM.
        $would = if ($routed.route -eq "auto-fix") { "merge on its own once CI passed" } else { "open a draft PR for you to merge" }
        Say $n (Format-RouteComment $routed $routeIn $routeMode "The fix is built and passed the tests and review. In live mode it would $would. Decide as before; the branch stays on the factory VM.")
        Label $n @("triage:ready", "route:$($routed.route)", "waiting:decide") $stale
        return
    }

    # ---- ship: the token is used here and only here -----------------------------------------------------------------
    $head = git rev-parse HEAD
    git -c "http.https://github.com/.extraheader=AUTHORIZATION: basic $([Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$token")))" push -q -f origin $branch
    if ($LASTEXITCODE) { throw "Push failed." }
    $draft = $routed -and $routed.route -eq "fix-for-review"
    $robot = [char]::ConvertFromUtf32(0x1F916)
    $prBody = "$prLead`n`nThe factory's second agent reviewed the diff: MERGE. Unit tests and the end-to-end checks in Flare passed on the factory VM.$(if ($retried) { " These passed only on a retry after a VM failure, so look for a flaky start-up: $($retried -join ', ')." })"
    if ($draft) { $prBody += "`n`nThe factory didn't merge this on its own, because`n" + ((@($routed.reasons) | ForEach-Object { "- $_" }) -join "`n") }
    $prBody += "`n`n$robot Generated with [Claude Code](https://claude.com/claude-code)"
    # Every fix should carry a changes/ note; if the agent left it out, the PR says so with no-notes rather than fail CI.
    $prArgs = @()
    if (-not @($changes | Where-Object { ($_ -split "`t")[-1] -like "changes/*.md" }).Count) { $prArgs += @("--label", "no-notes") }
    if ($draft) { $prArgs += "--draft" }
    if ($routed -and $routed.route -eq "auto-fix") { $prArgs += @("--label", "factory:auto-fix") }   # counted for the daily cap
    $url = Gh pr create -R $repo --base main --head $branch --title $prTitle --body $prBody @prArgs
    $pr = ($url -split '/')[-1]
    if ($draft) {
        Say $n (Format-RouteComment $routed $routeIn $routeMode "Review the draft PR, $url. Merging it is the one step left.")
        Label $n @("triage:ready", "route:fix-for-review", "waiting:merge", "factory:needs-decision") $stale
        return
    }

    # Required checks: wait until they exist and none is pending; every one must pass.
    $required = @((Gh api "repos/$repo/branches/main/protection/required_status_checks" | ConvertFrom-Json).contexts | Where-Object { $_ })
    if (-not $required.Count) { throw "main has no required checks configured; the factory won't merge without CI." }
    $deadline = (Get-Date).AddMinutes(60)
    do {
        Start-Sleep 30
        $runs = @((Gh api "repos/$repo/commits/$head/check-runs?per_page=100" | ConvertFrom-Json).check_runs)
        $state = foreach ($c in $required) {
            $run = $runs | Where-Object name -eq $c | Sort-Object started_at | Select-Object -Last 1
            if (-not $run -or $run.status -ne "completed") { "pending" } elseif ($run.conclusion -eq "success") { "pass" } else { "fail:$c" }
        }
    } until (-not ($state -contains "pending") -or (Get-Date) -gt $deadline)
    if ($state -contains "pending") { throw "Required checks didn't finish in an hour on $url." }
    if ($state | Where-Object { $_ -like "fail:*" }) { throw "Required checks failed on ${url}: $(($state | Where-Object { $_ -like 'fail:*' }) -join ', ')" }

    Gh pr merge $pr -R $repo --merge --delete-branch --match-head-commit $head | Out-Null
    if ($routed) { Say $n (Format-RouteComment $routed $routeIn $routeMode "Merged in $url. It ships in tonight's release.") }
    Say $n "Fixed in $url. It ships in the next release.`n`n$summary"
    if ($routed) { Label $n @("factory:done", "route:auto-fix") $stale } else { Label $n "factory:done" "factory:working" }
    Gh issue close $n -R $reports | Out-Null
}
catch {
    if ($mode -eq "triage") {
        # A failed triage stays visible and is retried; the third failure goes to the owner (see the pick step).
        try { Say $n "Triage failed (attempt $($failures + 1) of 3): $($_.Exception.Message)"; Label $n "triage:failed" "factory:working" }
        catch { Write-Host "Couldn't mark report #$n as failed on GitHub." }
        throw "Triage of report #$n failed. The reason is on the report, in the private reports repo."
    }
    try { Say $n "I couldn't finish this one: $($_.Exception.Message)"; Label $n @("factory:stuck", "waiting:stuck") "factory:working" }
    catch { Write-Host "Couldn't mark report #$n as stuck on GitHub." }
    throw "Report #$n is stuck. The reason is on the report, in the private reports repo."   # not the message: it can quote the report
}
finally { Pop-Location -ErrorAction SilentlyContinue }
