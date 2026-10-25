# The factory's run controls (command center spec section 4): the FACTORY_MODE kill switch, the agent process with its
# wall-clock limit, the run ledger and the daily run cap, the open-PR limit, blocked causes, holdout scenarios, and the
# weekly scorecard. Kept out of Invoke-Factory.ps1 so tests/factory can test them without a VM.

# ==== Kill switch =====================================================================================================
# FACTORY_MODE is off, shadow or live (any case). Unset, empty, or anything else is off, so the factory fails closed.
$FactoryModes = "off", "shadow", "live"
function Get-FactoryMode($value) {
    $v = if ($null -eq $value) { "" } else { ("" + $value).ToLowerInvariant() }
    if ($FactoryModes -contains $v) { $v } else { "off" }
}

# ==== What the factory says (factory-runtime spec, Copy) =============================================================
# Owner-facing strings: private comments, job logs and PR text, in one place. Format-Copy fills the {name} slots.
# A VM or host failure ends with "That points at the ... rather than the fix" (Get-BlockedCause reads it as vm); the
# strings that aren't failures (paused-path waits, ledger entries, Moved, Resumed, Retry needed) don't.
$FactoryCopy = @{
    NoOAuth = "CLAUDE_CODE_OAUTH_TOKEN isn't set, so the agents can't sign in."
    LiveWithoutLicense = "FACTORY_MODE is live, but the Flare VM has no Flare license yet, so this run stays in shadow mode."
    WaitingForCheck = "The fix is built, tested and reviewed. It's waiting for its Flare check."
    Parked = "Fixes are paused until the Flare VM can run Flare checks. The factory picks this report up again then."
    Moved = "Main's packages changed while this fix waited, so the factory moved the fix to the current main, reviewed it again, and is checking it in Flare again."
    SavedFixStale = "the saved fix no longer applies to main, so it waits for you. Retry triage to build it again on the current main."
    Resumed = "Picked up again after your decision:fix. The fix's Flare check runs again with the same fix."
    RetryNeeded = "This fix no longer applies to main, so decision:fix can't resume it. Retry triage to build it again on the current main."
    UsageLimitLog = "The Claude plan's usage limit was reached, so the factory waits an hour."
    UsageLimitReport = "The Claude plan's usage limit was reached. This report is picked again after an hour."
    AgentsNotReady = "Agents VM not ready. {reason}"
    ForkApprovalChanged = "Outside contributors' workflow runs no longer need approval, so the factory stopped. Turn approval back on for all outside contributors."
    SweepFailed = "The agents VM couldn't be cleaned up after a job. That points at the agents VM rather than the fix."
    RestoreLeftOpen = "The package restore's access didn't close on the agents VM. That points at the agents VM rather than the fix."
    CacheHadLink = "The package cache on the agents VM held a link, so the run stopped. That points at the agents VM rather than the fix."
    TaskDidntStop = "The agent task on the agents VM didn't stop. That points at the agents VM rather than the fix."
    SavedFixLost = "The saved fix for this report was lost on the agents VM. That points at the agents VM rather than the fix."
    AgentOutputRefused = "The agent's output on the agents VM was missing or couldn't be read safely, so the factory didn't use it."
    RouteAutoFixPaths = "the fix changes files auto-fix doesn't cover ({paths})"
    ResultUnreadable = "The Flare check's result couldn't be read. That usually points at the Flare VM rather than the fix."
    NotPickedUp = "The Flare check didn't start within 30 minutes. The host task wasn't running, or the VMs were being reset. It's tried again in an hour. That points at the host rather than the fix."
    InUse = "The Flare VM was in use, so the Flare check is tried again in an hour. That points at the Flare VM rather than the fix."
    Busy = "The Flare VM was busy with another job, so the Flare check is tried again in an hour. That points at the Flare VM rather than the fix."
    Interrupted = "The Flare check was interrupted on the host, so it's tried again in an hour. That points at the host rather than the fix."
    WaitingTrialEnded = "Flare's trial on the Flare VM has ended, so this fix waits for its Flare check until a Flare license is added."
    WaitingLicenseLapsed = "Flare's license on the Flare VM is no longer valid, so this fix waits for its Flare check."
    WaitingStateUnknown = "The Flare VM's license state couldn't be read, so this fix waits for its Flare check."
    WaitingHost = "Flare checks haven't started for two hours, so this fix waits for its Flare check."
    PausedTrial = "Flare's trial on the Flare VM has ended, so the factory triages reports but builds no fixes, and saved fixes wait. Add a Flare license to the Flare VM to start fixes again."
    PausedLicense = "Flare's license on the Flare VM is no longer valid, so the factory triages reports but builds no fixes, and saved fixes wait. The host checks it again each day. Once the license is renewed, run Reset-FactoryVM.ps1 -Name HSFACTORY -Refresh on THESEUS to start fixes now."
    PausedUnknown = "The Flare VM's license state couldn't be read, so the factory triages reports but builds no fixes, and saved fixes wait. The host reads it again each day. To read it now, run Reset-FactoryVM.ps1 -Name HSFACTORY -Refresh on THESEUS."
    PausedHost = "Flare checks haven't started for two hours, so the factory triages reports but builds no fixes. Sign in to THESEUS and check that the House Style Flare check task is turned on."
    TrialWarning = "Flare's trial on the Flare VM ends on {date}. Add a Flare license to keep Flare checks running and to let the factory go live."
    CheckCouldntRun = "The Flare check couldn't run on the Flare VM. It's tried again in an hour. The logs are in E:\HouseStyle\vm\flare-logs. That usually points at the Flare VM rather than the fix."
    WaitingForYou = "the Flare check couldn't run for this fix after several tries, so it waits for you. Add decision:fix to try the check again with the same fix. That usually points at the Flare VM or the host rather than the fix."
    RoutePublicScenarioFails = "the end-to-end checks in Flare fail on the fix"
    HoldoutReport = "A holdout scenario fails on the fix, so it stays off GitHub. The patch is in factory-logs on the agents VM, and the screenshots and logs are in E:\HouseStyle\vm\flare-logs. Decide what to do with it."
    HoldNext = "Look at it privately. Nothing more goes to GitHub. The patch is in factory-logs on the agents VM. Add decision:fix if you want it fixed and checked in Flare."
    OwnerOnlyHold = "Not pushing this fix because {reasons}. The patch is in factory-logs on the agents VM."
    ShadowChecked = "The fix is built and passed the tests, review and the Flare check. In live mode it would {action}. Decide as before. The patch is in factory-logs on the agents VM."
    DryRun = "Dry run. The fix is built, tested and reviewed. In live mode it would now wait for its Flare check."
    PrBody = "Unit tests passed on the agents VM, and the end-to-end checks passed in Flare on the Flare VM."
    PrBodyRetries = "{count} of these passed only on a retry after a VM failure, so look for a flaky start-up."
    JobLog = "Agent output is in {file} on the agents VM."
}
function Format-Copy([string]$key, [hashtable]$values = @{}) {
    if (-not $FactoryCopy.ContainsKey($key)) { throw "No factory string '$key'." }
    $t = $FactoryCopy[$key]
    foreach ($k in $values.Keys) { $t = $t.Replace("{$k}", "" + $values[$k]) }
    $t
}

# ==== The agents' tools ===============================================================================================
# The agent's own commands run as children of claude, which holds its Anthropic sign-in. So agents never run dotnet or
# the e2e harness directly: builds and tests go through scripts/agent/test-channel.ps1, which removes every secret from
# the child environment first. The script's own reruns of the tests run with no secrets too (Invoke-Factory.ps1).
# The test channel the agents run is a copy taken from the base commit into a folder outside the checkout
# (Save-AgentTools), so an agent can't edit the script that runs with its sign-in. $testChannel: that copy's
# test-channel.ps1, with forward slashes (the agents' shell is bash).
function Get-TestChannelTool([string]$testChannel) {
    if (-not $testChannel -or $testChannel -match '\s' -or $testChannel -notmatch '^[A-Za-z]:/') { throw "The test channel copy needs an absolute path with no spaces." }
    "Bash(powershell.exe -NoProfile -ExecutionPolicy Bypass -File $($testChannel):*)"
}
function Get-TriageTools([string]$testChannel) { "Read,Grep,Glob,Edit,Write,Bash(git status:*),Bash(git diff:*),Bash(git log:*),$(Get-TestChannelTool $testChannel)" }
function Get-FixTools([string]$testChannel) { "Read,Grep,Glob,Edit,Write,Bash(git status:*),Bash(git diff:*),Bash(git log:*),Bash(git add:*),Bash(git commit:*),$(Get-TestChannelTool $testChannel)" }
$ReviewTools = "Read,Grep,Glob,Write"
# Paths agents may not edit, as permission rules relative to the checkout: the agent scripts and the e2e harness.
$ProtectedEdits = "scripts/**", "tests/e2e/Run-FlareE2E.ps1", "tests/e2e/FlareE2E.psm1", "tests/e2e/flaui/**"
$DisallowedTools = "WebFetch,WebSearch,Bash(gh:*),Bash(curl:*),Bash(git push:*)," + (@($ProtectedEdits | ForEach-Object { "Edit(./$_),Write(./$_)" }) -join ",")

# The files the agents' test runs depend on, which the factory copies from the base commit before each agent and
# checks again afterwards: the agent scripts (test-channel.ps1 and the secret scrubbing in AgentCommon.psm1) and the
# e2e harness.
$AgentToolPaths = "scripts/agent", "tests/e2e/Run-FlareE2E.ps1", "tests/e2e/FlareE2E.psm1", "tests/e2e/flaui"

# Hashes of every file under $dir, path -> SHA-256, to check a copy wasn't changed. Build output (bin, obj: the e2e
# harness restores FlaUI there) doesn't count.
function Get-TreeHashes([string]$dir) {
    $h = @{}
    foreach ($f in @(Get-ChildItem $dir -Recurse -File -Force -ErrorAction SilentlyContinue)) {
        $rel = $f.FullName.Substring($dir.Length)
        if ($rel -match '[\\/](bin|obj)[\\/]') { continue }
        $h[$rel] = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    }
    $h
}
function Test-SameHashes($a, $b) {
    if ($a.Count -ne $b.Count) { return $false }
    foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k) -or $b[$k] -ne $a[$k]) { return $false } }
    $true
}

# ==== The agent process ===============================================================================================
# One command line from separate arguments, quoted the way Windows programs read them back.
function Join-ProcessArguments([string[]]$arguments) {
    $parts = foreach ($a in $arguments) {
        if ($a -ne "" -and $a -notmatch '[\s"]') { $a; continue }
        $s = '"'; $slashes = 0
        foreach ($ch in $a.ToCharArray()) {
            if ($ch -eq '\') { $slashes++; continue }
            if ($ch -eq '"') { $s += ('\' * ($slashes * 2 + 1)) + '"' } else { $s += ('\' * $slashes) + $ch }
            $slashes = 0
        }
        $s + ('\' * ($slashes * 2)) + '"'
    }
    $parts -join " "
}

# Runs a program with stdin from $inFile and stdout and stderr to files, and kills it with its whole process tree once
# it runs past $timeoutSeconds. The child gets this process's environment as it is now (callers strip secrets first).
function Invoke-AgentProcess([string]$exe, [string[]]$arguments, [string]$inFile, [string]$outFile, [string]$errFile, [int]$timeoutSeconds) {
    $start = Get-Date
    $p = Start-Process -FilePath $exe -ArgumentList (Join-ProcessArguments $arguments) -RedirectStandardInput $inFile -RedirectStandardOutput $outFile `
        -RedirectStandardError $errFile -NoNewWindow -PassThru
    $null = $p.Handle   # keeps the handle, so ExitCode can be read after the process ends
    $timedOut = -not $p.WaitForExit([Math]::Max(1, $timeoutSeconds) * 1000)
    if ($timedOut) {
        & taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null
        $null = $p.WaitForExit(30000)
    }
    else { $p.WaitForExit() }   # lets the redirected output finish
    [pscustomobject]@{ exit = $(if ($timedOut) { -1 } else { $p.ExitCode }); timedOut = $timedOut; seconds = [int]((Get-Date) - $start).TotalSeconds }
}

# The numbers from claude -p --output-format json, never its text. Missing or unreadable output gives nulls.
function ConvertFrom-AgentOutput([string]$text) {
    $o = $null
    try { if ($text) { $o = $text | ConvertFrom-Json } } catch { $o = $null }
    if ($o -is [array]) { $o = @($o | Where-Object { $_.type -eq "result" }) | Select-Object -Last 1 }
    function N($v) { if ($null -eq $v -or "" -eq "$v") { $null } else { [double]$v } }
    [pscustomobject]@{
        subtype = $(if ($o) { "" + $o.subtype } else { "" })
        turns = $(if ($o) { N $o.num_turns } else { $null })
        input_tokens = $(if ($o) { N $o.usage.input_tokens } else { $null })
        output_tokens = $(if ($o) { N $o.usage.output_tokens } else { $null })
        cache_read_tokens = $(if ($o) { N $o.usage.cache_read_input_tokens } else { $null })
        cache_write_tokens = $(if ($o) { N $o.usage.cache_creation_input_tokens } else { $null })
        cost_usd = $(if ($o) { N $o.total_cost_usd } else { $null })
    }
}

# Why an agent run stopped short, or $null. Max turns and the wall-clock limit are caps: the report goes to the owner.
function Get-AgentStop($proc, $out, [int]$minutes, [int]$maxTurns) {
    if ($proc.timedOut) { return "the agent ran past its $minutes-minute limit and was stopped" }
    if ($out.subtype -eq "error_max_turns") { return "the agent used all its $maxTurns turns" }
    $null
}

# ==== The run ledger ==================================================================================================
# One comment per agent run on the "Factory ledger" issue in the private reports repo: numbers only, never report text.
# Dollars only when the agent signed in with an API key; with the subscription token the ledger records tokens.
$LedgerMarker = "<!-- factory-ledger -->"

function New-LedgerEntry([string]$step, [int]$report, $proc, $out, [bool]$apiKey, [string]$mode, [bool]$dryRun, [string]$stop, [datetime]$at = (Get-Date)) {
    [ordered]@{
        at = $at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"); step = $step; report = $report; mode = $mode; dry_run = $dryRun
        exit = [int]$proc.exit; seconds = [int]$proc.seconds; stopped = $(if ($stop) { $(if ($proc.timedOut) { "wall-clock" } else { "max-turns" }) } else { $null })
        turns = $out.turns; input_tokens = $out.input_tokens; output_tokens = $out.output_tokens
        cache_read_tokens = $out.cache_read_tokens; cache_write_tokens = $out.cache_write_tokens
        cost_usd = $(if ($apiKey) { $out.cost_usd } else { $null })
    }
}

function Format-LedgerComment($entry) {
    "**Factory ledger** $($entry.step) run`n$LedgerMarker`n" + '```json' + "`n" + ($entry | ConvertTo-Json -Compress) + "`n" + '```'
}

# The entry in one ledger comment, or $null when the comment isn't one.
function ConvertFrom-LedgerComment([string]$body) {
    if (-not $body -or -not $body.Contains($LedgerMarker)) { return $null }
    $m = [regex]::Match($body, '(?s)```json\s*(\{.*?\})\s*```')
    if (-not $m.Success) { return $null }
    try { $m.Groups[1].Value | ConvertFrom-Json } catch { $null }
}

# Agent runs started on the UTC day of $now.
function Get-RunsToday($entries, [datetime]$now = (Get-Date)) {
    $day = $now.ToUniversalTime().ToString("yyyy-MM-dd")
    @($entries | Where-Object { $_ -and ("" + $_.at).StartsWith($day) }).Count
}

# Why another agent run may not start, or $null. $used: runs today from the ledger, plus this run's own.
function Test-RunBudget([int]$used, [int]$limit) {
    if ($used -ge $limit) { "today's $limit agent runs are used up" } else { $null }
}

# ==== Open factory PRs (a WIP limit) ==================================================================================
function Get-OpenFactoryPrCount($prs) { @($prs | Where-Object { $_ -and ("" + $_.headRefName) -like "factory/*" }).Count }
function Test-WipFull([int]$open, [int]$limit) { $open -ge $limit }

# ==== Blocked causes ==================================================================================================
# The cause to label a stuck report with, from the reason the run stopped: vm, flaky-test, owner or other.
function Get-BlockedCause([string]$why) {
    if ($why -match '(VM|host) rather than the fix|VM not ready|end-to-end run stopped before any scenario ran') { return "vm" }
    if ($why -match "Required checks failed") { return "flaky-test" }   # passed on the agents VM and in Flare, failed in CI on the same commit
    if ($why -match "agent runs are used up|ran past its|used all its|Triage failed three times") { return "owner" }
    "other"
}

# ==== The Flare check's result (factory-runtime spec section 5) ======================================================
# result.json comes back from the host into flare-inbox\<check id>.claimed\. It's data with a fixed schema: check,
# outcome, vmKind (with vm only), failed and retried (scenario names). A holdout failure names no holdout scenario: the
# outcome says it, and their names stay private. Anything else, or a file over 16 KB, is unreadable, which the run
# treats as a vm outcome with no strike.
$FlareOutcomes = "pass", "fix", "holdout", "vm"
$FlareVmKinds = "in-use", "busy", "interrupted", "not-ready", "no-start", "no-scenarios", "timeout", "trial-expired", "trial-unknown"

# $text: result.json. $checkId: the request's id. $trialState: the state in the host-written trial.json.
# $scenarios: the base's scenario names plus those the saved patch adds. Returns ok, why and result.
function ConvertFrom-FlareCheckResult([string]$text, [string]$checkId, [string]$trialState, $scenarios) {
    function No($why) { [pscustomobject]@{ ok = $false; why = $why; result = $null } }
    if ($null -eq $text -or [Text.Encoding]::UTF8.GetByteCount($text) -gt 16384) { return No "result.json is missing or over 16 KB" }
    try { $o = $text | ConvertFrom-Json } catch { return No "result.json isn't JSON" }
    if ($o -isnot [pscustomobject]) { return No "result.json isn't one object" }
    $names = @($o.PSObject.Properties.Name)
    $extra = @($names | Where-Object { @("check", "outcome", "vmKind", "failed", "retried") -cnotcontains $_ })
    if ($extra.Count) { return No "result.json has a field it shouldn't ($($extra -join ', '))" }
    if (("" + $o.check) -cne $checkId) { return No "result.json is for another check" }
    if ($FlareOutcomes -cnotcontains ("" + $o.outcome)) { return No "result.json's outcome isn't one the factory knows" }
    if ($o.outcome -eq "vm") {
        if ($FlareVmKinds -cnotcontains ("" + $o.vmKind)) { return No "result.json's vmKind isn't one the factory knows" }
        # Only the host's own read of Flare can say the trial ended or its state is unknown.
        if ($o.vmKind -eq "trial-expired" -and $trialState -ne "expired") { return No "result.json says the trial expired, but the host's trial.json doesn't" }
        if ($o.vmKind -eq "trial-unknown" -and $trialState -ne "unknown") { return No "result.json says the license state is unknown, but the host's trial.json doesn't" }
    }
    elseif ($names -ccontains "vmKind") { return No "result.json has a vmKind without a vm outcome" }
    $known = @($scenarios | Where-Object { $_ })
    foreach ($f in "failed", "retried") {
        $v = $o.$f
        if ($null -eq $v) { continue }
        if ($v -is [string] -or @($v).Count -gt 50) { return No "result.json's $f isn't a list of at most 50 names" }
        foreach ($s in @($v)) {
            if ($s -isnot [string] -or $s -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$' -or $known -cnotcontains $s) { return No "result.json's $f names a scenario the base and the patch don't have" }
        }
    }
    [pscustomobject]@{ ok = $true; why = $null; result = [pscustomobject]@{ check = "" + $o.check; outcome = "" + $o.outcome
        vmKind = $(if ($o.outcome -eq "vm") { "" + $o.vmKind } else { $null }); failed = @($o.failed | Where-Object { $_ }); retried = @($o.retried | Where-Object { $_ }) } }
}

# Reads result.json from the inbox, 16 KB at most, then checks it.
function Read-FlareCheckResult([string]$path, [string]$checkId, [string]$trialState, $scenarios) {
    $text = $null
    try {
        $fi = New-Object IO.FileInfo $path
        if ($fi.Exists -and $fi.Length -le 16384 -and -not ($fi.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $text = [IO.File]::ReadAllText($path) }
    } catch { $text = $null }
    ConvertFrom-FlareCheckResult $text $checkId $trialState $scenarios
}

# flare-inbox\trial.json, written by the host: state (active, expired, licensed or unknown), endDate (yyyy-MM-dd, when
# the state comes from a trial) and wasLicensed. Any other content reads as unknown, so the fix path stays off.
function ConvertFrom-TrialJson([string]$text) {
    $unknown = [pscustomobject]@{ state = "unknown"; endDate = $null; wasLicensed = $false }
    if (-not $text -or $text.Length -gt 4096) { return $unknown }
    try { $o = $text | ConvertFrom-Json } catch { return $unknown }
    if ($o -isnot [pscustomobject]) { return $unknown }
    if (@($o.PSObject.Properties.Name | Where-Object { @("state", "endDate", "wasLicensed") -cnotcontains $_ }).Count) { return $unknown }
    if (@("active", "expired", "licensed", "unknown") -cnotcontains ("" + $o.state)) { return $unknown }
    if ($null -ne $o.wasLicensed -and $o.wasLicensed -isnot [bool]) { return $unknown }
    $end = $null
    if ($null -ne $o.endDate) {
        $d = [datetime]::MinValue
        if (("" + $o.endDate) -notmatch '^\d{4}-\d{2}-\d{2}$' -or -not [datetime]::TryParseExact(("" + $o.endDate), "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture, "None", [ref]$d)) { return $unknown }
        $end = "" + $o.endDate
    }
    if ($o.state -eq "active" -and -not $end) { return $unknown }   # a trial always has its end date
    [pscustomobject]@{ state = "" + $o.state; endDate = $end; wasLicensed = ($o.wasLicensed -eq $true) }
}

# ==== Git config scopes (factory-runtime spec section 3, step 8) =====================================================
# The script's git calls run with the token in reach, so the checkout's own config may hold only harmless keys, in the
# local scope only. Nothing may come from a worktree config, and extensions.worktreeconfig (which turns one on) is
# refused in every scope. $lines: git config --list --show-scope output, one "scope<TAB>key=value" per line. The
# system, global and command scopes belong to the script user and the script itself. Returns what isn't allowed.
$GitLocalKeys = '^(core\.(repositoryformatversion|filemode|bare|logallrefupdates|symlinks|ignorecase|autocrlf|longpaths)|remote\.origin\.(url|fetch)|branch\..+\.(remote|merge)|user\.(name|email)|gc\.auto|maintenance\.auto)='
function Test-GitConfigScopes($lines) {
    $bad = @()
    foreach ($l in @($lines | Where-Object { $_ })) {
        $parts = ("" + $l) -split "`t", 2
        if ($parts.Count -ne 2) { $bad += "an unreadable line"; continue }
        $scope = $parts[0]; $entry = $parts[1]; $key = ($entry -split '=', 2)[0].ToLowerInvariant()
        if ($key -eq "extensions.worktreeconfig") { $bad += "$scope $key"; continue }
        switch ($scope) {
            "local" { if ($entry -notmatch $GitLocalKeys) { $bad += "local $key" } }
            { @("system", "global", "command") -contains $_ } { }
            default { $bad += "$scope $key" }
        }
    }
    @($bad | Select-Object -Unique)
}

# Throws when the checkout in the current folder has config the factory doesn't allow. $who names the step for the
# message. git config --show-scope reads every scope, including a worktree config.
function Assert-GitConfig([string]$who) {
    $ErrorActionPreference = "Continue"
    $lines = @(& git.exe config --list --show-scope 2>$null)
    if ($LASTEXITCODE) { throw "$who left a git config the factory can't read." }
    $bad = @(Test-GitConfigScopes $lines)
    if ($bad.Count) { throw "$who left git settings the factory doesn't allow: $($bad -join ', ')" }
}

# ==== Holdout scenarios ===============================================================================================
# e2e scenarios kept in the private reports repo (holdout/*.ps1). The factory fetches them only after the fix agent has
# finished, into a folder outside the checkout, and runs them with the public suite. $list returns the folder's
# entries (name, type), or $null when there is no holdout folder. $get returns one file's text.
function Save-Holdout([scriptblock]$list, [scriptblock]$get, [string]$dest) {
    $entries = & $list
    if ($null -eq $entries) { return @() }
    New-Item -ItemType Directory -Force $dest | Out-Null
    $saved = @()
    $usable = @($entries | Where-Object { $_ -and $_.type -eq "file" -and ("" + $_.name) -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,80}\.ps1$' })
    # AI scenarios need an API key, and the factory's e2e run has none (-NoAi). The holdout- prefix would hide them
    # from that filter, so they're skipped here.
    $ai = @($usable | Where-Object { ("" + $_.name) -like "ai-*" })
    if ($ai.Count) { Write-Host "Skipped $($ai.Count) AI holdout scenarios, since the factory's e2e run has no API key." }
    foreach ($e in @($usable | Where-Object { ("" + $_.name) -notlike "ai-*" })) {
        $text = (@(& $get $e.name) -join "`n")
        $name = "holdout-" + $e.name
        Set-Content (Join-Path $dest $name) $text -Encoding UTF8   # with a BOM, so Windows PowerShell reads non-ASCII text right
        $saved += $name
    }
    $saved
}

# ==== Weekly scorecard (spec section 4.5) =============================================================================
# Counts and fixed reason codes only: no report text, titles or numbers.

# Hours the owner was waited on, from ~/.claude/waiting-log.jsonl lines ({session, event, at}). A Notification starts a
# wait; the next UserPromptSubmit or Stop in the same session ends it. Returns total hours, median hours, wait count.
function Get-OwnerWaits($lines, [datetime]$since, [datetime]$until) {
    $open = @{}; $waits = @()
    foreach ($l in @($lines | Where-Object { $_ })) {
        try { $e = $l | ConvertFrom-Json } catch { continue }
        $at = [datetime]::Parse("" + $e.at, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
        if ($e.event -eq "Notification") { if (-not $open.ContainsKey($e.session)) { $open[$e.session] = $at } }
        elseif ($open.ContainsKey($e.session)) {
            $s = $open[$e.session]; $open.Remove($e.session)
            if ($at -gt $since -and $s -lt $until) { $waits += ($at - $s).TotalHours }
        }
    }
    $median = Get-Median $waits
    [pscustomobject]@{ hours = [Math]::Round(($waits | Measure-Object -Sum).Sum, 1); median = $(if ($null -ne $median) { [Math]::Round($median, 1) } else { $null }); count = $waits.Count }
}

function Get-Median($values) {
    $v = @($values | Where-Object { $null -ne $_ } | Sort-Object)
    if (-not $v.Count) { return $null }
    if ($v.Count % 2) { $v[($v.Count - 1) / 2] } else { ($v[$v.Count / 2 - 1] + $v[$v.Count / 2]) / 2 }
}

# The scorecard numbers. $in holds what Post-Scorecard.ps1 fetched:
#   now; days (the week); reports (number, createdAt, labels); routes (report, at, route, codes) from route comments;
#   stops (at, codes) from stuck comments; prs (factory PRs: title, state, createdAt, mergedAt, closedAt, mergeCommit);
#   reverted (sha -> revert date); ledger (entries); waits (Get-OwnerWaits output or $null); suspended (area names).
function Get-Scorecard($in) {
    $now = [datetime]$in.now; $since = $now.AddDays(-[int]$in.days); $month = $now.AddDays(-30)
    function Within($d, $from) { $d -and ([datetime]$d) -gt $from -and ([datetime]$d) -le $now }
    $reports = @($in.reports | Where-Object { Within $_.createdAt $since })
    # The last route per report this week: a fix route is posted once when the build starts and once when it ends.
    $last = @{}; foreach ($r in @($in.routes | Where-Object { Within $_.at $since } | Sort-Object { [datetime]$_.at })) { $last[[int]$r.report] = $r }
    $routes = [ordered]@{}; foreach ($name in "hold", "evidence", "auto-fix", "fix-for-review", "brief-only", "plan", "docs-gap", "pile") { $routes[$name] = 0 }
    foreach ($r in $last.Values) { if ($routes.Contains($r.route)) { $routes[$r.route]++ } }

    $prs = @($in.prs)
    $closedWeek = @($prs | Where-Object { (Within $_.mergedAt $since) -or ((Within $_.closedAt $since) -and -not $_.mergedAt) })
    $mergedWeek = @($closedWeek | Where-Object { $_.mergedAt })
    $mergedMonth = @($prs | Where-Object { Within $_.mergedAt $month })
    $revertedMonth = @($mergedMonth | Where-Object {
        $sha = "" + $_.mergeCommit.oid
        $sha -and $in.reverted.ContainsKey($sha) -and (([datetime]$in.reverted[$sha]) - ([datetime]$_.mergedAt)).TotalDays -le 14
    })
    # Report to ship: from the report being filed to its fix merging, for fixes merged this week.
    $byNumber = @{}; foreach ($r in @($in.reports)) { $byNumber[[int]$r.number] = $r }
    $ship = foreach ($p in $mergedWeek) {
        if (("" + $p.title) -match '^Fix tester report (\d+)$' -and $byNumber.ContainsKey([int]$Matches[1])) {
            (([datetime]$p.mergedAt) - ([datetime]$byNumber[[int]$Matches[1]].createdAt)).TotalDays
        }
    }
    $runs = @($in.ledger | Where-Object { $_ -and (Within $_.at $since) })
    $tokens = 0; $cost = 0.0; $costKnown = $false
    foreach ($e in $runs) {
        foreach ($k in "input_tokens", "output_tokens", "cache_read_tokens", "cache_write_tokens") { if ($null -ne $e.$k) { $tokens += [double]$e.$k } }
        if ($null -ne $e.cost_usd) { $cost += [double]$e.cost_usd; $costKnown = $true }
    }
    # Rejection reasons: why a report didn't go straight through, as fixed codes.
    $codes = [ordered]@{}
    function Add-Code($c) { if ($codes.Contains($c)) { $codes[$c]++ } else { $codes[$c] = 1 } }
    # An auto-fix counts only when it waited on the daily cap. "passed" and "regression" describe a route, not a rejection.
    foreach ($r in $last.Values) {
        if (@("hold", "fix-for-review", "brief-only", "auto-fix") -notcontains $r.route) { continue }
        foreach ($c in @($r.codes | Where-Object { $_ -and @("passed", "regression") -notcontains $_ })) { if ($r.route -ne "auto-fix" -or $c -eq "daily-cap") { Add-Code $c } }
    }
    foreach ($s in @($in.stops | Where-Object { Within $_.at $since })) { foreach ($c in @($s.codes)) { if ($c) { Add-Code $c } } }
    foreach ($r in $reports) {
        $l = @($r.labels | ForEach-Object { if ($_.name) { $_.name } else { $_ } })
        if ($l -contains "decision:decline") { Add-Code "owner-declined" }
        if ($l -contains "decision:park") { Add-Code "owner-parked" }
    }
    foreach ($p in @($closedWeek | Where-Object { -not $_.mergedAt })) { Add-Code "pr-closed-unmerged" }

    [pscustomobject]@{
        since = $since; until = $now; reportsIn = $reports.Count; routes = $routes
        prsClosed = $closedWeek.Count; prsMerged = $mergedWeek.Count
        mergedMonth = $mergedMonth.Count; revertedMonth = $revertedMonth.Count
        reportToShipDays = $(if ($null -ne (Get-Median $ship)) { [Math]::Round((Get-Median $ship), 1) } else { $null }); shipped = @($ship).Count
        agentRuns = $runs.Count; capStops = @($runs | Where-Object { $_.stopped }).Count
        tokens = $tokens; costUsd = $(if ($costKnown) { [Math]::Round($cost, 2) } else { $null })
        waits = $in.waits; reasons = $codes; suspended = @($in.suspended)
    }
}

function Format-Scorecard($s) {
    function Rate($a, $b) { if ($b) { "$a of $b ($([Math]::Round(100.0 * $a / $b))%)" } else { "none yet" } }
    $per = if ($s.prsMerged) { [Math]::Round($s.tokens / $s.prsMerged) } else { $null }
    $lines = @(
        "**Factory scorecard**, $($s.since.ToString('yyyy-MM-dd')) to $($s.until.ToString('yyyy-MM-dd'))", "",
        "| Measure | This week |", "|---|---|",
        "| Reports in | $($s.reportsIn) |",
        "| Routes taken | $((@($s.routes.Keys | Where-Object { $s.routes[$_] }) | ForEach-Object { "$_ $($s.routes[$_])" }) -join ', ')$(if (-not @($s.routes.Values | Where-Object { $_ }).Count) { 'none' }) |",
        "| Merge rate (factory PRs closed this week) | $(Rate $s.prsMerged $s.prsClosed) |",
        "| Revert rate (merged in 30 days, reverted within 14 days of merge) | $(Rate $s.revertedMonth $s.mergedMonth) |",
        "| Report to ship, median | $(if ($null -ne $s.reportToShipDays) { "$($s.reportToShipDays) days over $($s.shipped) fixes" } else { 'no fixes shipped' }) |",
        "| Agent runs | $($s.agentRuns), $($s.capStops) stopped by a cap |",
        "| Tokens per merged change | $(if ($null -ne $per) { "{0:N0}" -f $per } else { "no merged changes ($("{0:N0}" -f $s.tokens) tokens in all)" }) |",
        "| Cost per merged change | $(if ($null -eq $s.costUsd) { 'not recorded (subscription token)' } elseif ($s.prsMerged) { '$' + [Math]::Round($s.costUsd / $s.prsMerged, 2) } else { '$' + $s.costUsd + ' in all' }) |",
        "| Owner touch time | $(if ($s.waits) { "$($s.waits.hours) hours over $($s.waits.count) waits, median $($s.waits.median) hours" } else { 'not measured here (the waiting log is on the owner''s PC)' }) |",
        "| Suspended areas | $(if ($s.suspended.Count) { $s.suspended -join ', ' } else { 'none' }) |",
        "", "**Rejection reasons**", ""
    )
    if ($s.reasons.Count) { foreach ($k in $s.reasons.Keys) { $lines += "- ``$k`` $($s.reasons[$k])" } } else { $lines += "None this week." }
    $lines -join "`n"
}

Export-ModuleMember -Function Get-FactoryMode, Join-ProcessArguments, Invoke-AgentProcess, ConvertFrom-AgentOutput, Get-AgentStop, New-LedgerEntry,
    Format-LedgerComment, ConvertFrom-LedgerComment, Get-RunsToday, Test-RunBudget, Get-OpenFactoryPrCount, Test-WipFull, Get-BlockedCause,
    Save-Holdout, Get-OwnerWaits, Get-Median, Get-Scorecard, Format-Scorecard, Get-TestChannelTool, Get-TriageTools, Get-FixTools, Get-TreeHashes,
    Test-SameHashes, ConvertFrom-FlareCheckResult, Read-FlareCheckResult, ConvertFrom-TrialJson, Test-GitConfigScopes, Assert-GitConfig, Format-Copy -Variable FactoryCopy, FactoryModes, FlareOutcomes, FlareVmKinds, LedgerMarker, ReviewTools, DisallowedTools, ProtectedEdits, AgentToolPaths
