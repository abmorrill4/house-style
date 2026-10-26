# The build factory's decisions (planning/work/factory-build/spec.md): what counts as ready, where the spec lives, the
# run lock and pauses, budgets, the retry ladder, agent launch arguments and the merge gate. Kept out of
# Invoke-Build.ps1 so tests/factory can test them without GitHub, git or claude.
Import-Module (Join-Path $PSScriptRoot "Controls.psm1")
Import-Module (Join-Path $PSScriptRoot "Triage.psm1")   # $OwnerOnly and Test-SecretShape: one list for both factories

# ==== Switches and rules ==============================================================================================
function Get-BuildRules([string]$root) { Get-Content (Join-Path $root "rules\factory.json") -Raw | ConvertFrom-Json }

# FACTORY_BUILD: only "on" (any case, no spaces) is on. Unset, empty, unreadable or anything else is off.
function Test-BuildOn($value) { ("" + $value).ToLowerInvariant() -eq "on" }

# ==== Ready =========================================================================================================
# Why an issue isn't ready, or $null. $issue: author login and label names. $labelEvents: the timeline's labeled and
# unlabeled events in order ({event, label, actor}). $editors: the login of every body edit. Unverifiable is not ready.
function Get-NotReadyReason($issue, $labelEvents, $editors, [string]$owner, [string]$readyLabel) {
    if (("" + $issue.author) -ne $owner) { return "not opened by the owner" }
    if (@($issue.labels) -notcontains $readyLabel) { return "no $readyLabel label" }
    $last = @($labelEvents | Where-Object { $_.label -eq $readyLabel -and $_.event -eq "labeled" }) | Select-Object -Last 1
    if (-not $last) { return "the $readyLabel label's history can't be read" }
    if (("" + $last.actor) -ne $owner) { return "$readyLabel wasn't added by the owner" }
    if ($null -eq $editors) { return "the body's edit history can't be read" }
    if (@($editors | Where-Object { ("" + $_) -ne $owner }).Count) { return "the body was edited by someone else" }
    $null
}

# The hash covers the title too: it's outside the body's edit history, and it becomes the PR title.
function Get-BodyHash([string]$body) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(("" + $body).Replace("`r`n", "`n"))) | ForEach-Object { $_.ToString("x2") }) }
    finally { $sha.Dispose() }
}

# The spec for an issue, from its number alone: exactly one folder <work>\<n>-* holding spec.md, inside <work>, with
# no reparse point on the way and no UNC path. $null when there's no folder (a Fix). Throws on anything else.
function Resolve-SpecPath([string]$work, [int]$n) {
    if ($work -match '^\\\\' -or $work -match '^//') { throw "The planning folder is a UNC path." }
    $full = [IO.Path]::GetFullPath($work).TrimEnd('\') + '\'
    $dirs = @(Get-ChildItem -LiteralPath $full -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "^$n-[A-Za-z0-9._-]+$" })
    if (-not $dirs.Count) { return $null }
    if ($dirs.Count -gt 1) { throw "More than one spec folder for #$n." }
    $spec = Join-Path $dirs[0].FullName "spec.md"
    foreach ($p in $dirs[0].FullName, $spec) {
        $i = Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
        if (-not $i) { throw "Spec folder for #$n has no spec.md." }
        if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "The spec for #$n goes through a link." }
    }
    $resolved = [IO.Path]::GetFullPath($spec)
    if (-not $resolved.StartsWith($full, [StringComparison]::OrdinalIgnoreCase)) { throw "The spec for #$n is outside the planning folder." }
    $resolved
}

# The reproducing test a Fix names in its body: a line "Test: <name>" (one word, a test name). $null when there's none.
function Get-FixTest([string]$body) {
    $m = [regex]::Match("" + $body, '(?im)^\s*(?:\*\*)?(?:reproducing\s+)?test\s*(?:\*\*)?\s*:\s*(?:\*\*)?\s*`?([A-Za-z_][A-Za-z0-9_.]*)`?\s*$')
    if ($m.Success) { $m.Groups[1].Value } else { $null }
}

# ==== Lock and pauses ===============================================================================================
# The run lock holds "<pid> <start>". A lock whose process isn't running is stale and taken over. $true when held.
function Enter-BuildLock([string]$path) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    if (Test-Path -LiteralPath $path) {
        $owner = ("" + (Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue)).Trim() -split '\s+' | Select-Object -First 1
        if ($owner -match '^\d+$' -and [int]$owner -ne $PID -and (Get-Process -Id ([int]$owner) -ErrorAction SilentlyContinue)) { return $false }
        Remove-Item -LiteralPath $path -Force
    }
    try {
        $fs = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        $b = [Text.Encoding]::ASCII.GetBytes("$PID $((Get-Date).ToUniversalTime().ToString('o'))")
        $fs.Write($b, 0, $b.Length); $fs.Dispose(); $true
    } catch { $false }   # another run created it first
}
function Exit-BuildLock([string]$path) {
    $owner = ("" + (Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue)).Trim() -split '\s+' | Select-Object -First 1
    if ($owner -eq "$PID") { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

# The pause marker: a UTC time. $true while it's in the future. An unreadable marker counts as no pause.
function Test-Paused([string]$path, [datetime]$now = (Get-Date)) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try { [datetime]::Parse((Get-Content -LiteralPath $path -Raw).Trim()).ToUniversalTime() -gt $now.ToUniversalTime() } catch { $false }
}
function Set-Pause([string]$path, [datetime]$until) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    Set-Content -LiteralPath $path $until.ToUniversalTime().ToString("o") -Encoding ASCII
}

# A usage-limit stop in claude's output: the time it resets (an hour ahead when the message gives none), or $null.
# Such a stop spends no try (spec section 3).
function Get-UsageLimitReset([string]$text, [datetime]$now = (Get-Date)) {
    if (-not $text) { return $null }
    $o = $null; try { $o = $text | ConvertFrom-Json } catch { }
    if ($o -is [array]) { $o = @($o | Where-Object { $_.type -eq "result" }) | Select-Object -Last 1 }
    $msg = if ($o) { "" + $o.result } else { $text }
    if ($o -and -not $o.is_error -and $o.subtype -eq "success") { return $null }
    if ($msg -notmatch '(?i)usage limit|limit reached|rate limit|resets? at|try again (at|in)') { return $null }
    $epoch = [regex]::Match($msg, '\|(\d{10})\b')
    if ($epoch.Success) { return [DateTimeOffset]::FromUnixTimeSeconds([long]$epoch.Groups[1].Value).UtcDateTime }
    $now.ToUniversalTime().AddHours(1)
}

# ==== Budget and size ================================================================================================
# The issue's budget in API-price dollars: factor x the percentile of the last <window> merged issues of the same size,
# capped at the ceiling. Before coldStartIssues such issues, the cold-start value. $summaries: the ledger's issue lines.
function Get-IssueBudget($summaries, [string]$size, $budget) {
    $done = @($summaries | Where-Object { $_.kind -eq "issue" -and $_.size -eq $size -and $_.outcome -eq "merged" -and $null -ne $_.cost_usd }) |
        Select-Object -Last ([int]$budget.window)
    $b = if ($done.Count -lt [int]$budget.coldStartIssues) { [double]$budget.coldStartUsd.$size }
         else {
             $costs = @($done | ForEach-Object { [double]$_.cost_usd } | Sort-Object)
             $i = [Math]::Min($costs.Count - 1, [int][Math]::Ceiling($costs.Count * [double]$budget.percentile / 100) - 1)
             [double]$budget.factor * $costs[[Math]::Max(0, $i)]
         }
    [Math]::Min($b, [double]$budget.ceilingUsd)
}

function Get-CardTokens([string]$text) { [int][Math]::Ceiling(("" + $text).Length / 4) }   # ponytail: chars/4, a rough cap, not a count

# What the build factory may never change without the owner: Triage.psm1's $OwnerOnly, plus everything that runs on the
# owner's PC or ships outside the plugin's own code (Claude settings and hooks, every script including the installer,
# the updater, the e2e harness that judges the e2e check, CLAUDE.md).
$FactoryOwnerOnly = '^\.claude/|^scripts/|^tests/factory/|^tests/e2e/(Run-FlareE2E\.ps1|FlareE2E\.psm1|flaui/)|^tools/FlareAI\.Updater/|^CLAUDE\.md$'

# The planned or changed paths on the owner-only lists, forward slashes, repo-relative. A path that isn't plain printable
# ASCII counts as owner-only too: git quotes such names, and a quoted name would slip past every anchored pattern.
function Get-OwnerOnlyPaths($paths) {
    @($paths | ForEach-Object { ("" + $_).Replace('\', '/') -replace '^(\./)+', '' } |
        Where-Object { $_ -match $OwnerOnly -or $_ -match $FactoryOwnerOnly -or $_ -cmatch '[^\x21-\x7E]|"' })
}

# The channels a chunk's test may run on locally: the narrow ones. The Flare tests and the full suite run in CI.
$PlanChannels = "rule-fixtures", "core", "scenarios", "flare-tests", "pester", "vitest"

# A plan from hs-plan: a problem with it, or $null. Chunks 1..max, each with a goal, 1..maxFiles files, a model.
function Get-PlanProblem($plan, $limits) {
    $chunks = @($plan.chunks)
    if (-not $chunks.Count) { return "the plan has no chunks" }
    if ($chunks.Count -gt [int]$limits.chunksPerIssue) { return "too-big" }
    foreach ($c in $chunks) {
        if (-not $c.goal) { return "a chunk has no goal" }
        $f = @($c.files | Where-Object { $_ })
        if (-not $f.Count -or $f.Count -gt [int]$limits.filesPerChunk) { return "a chunk names $($f.Count) files" }
        if (@("haiku", "sonnet") -notcontains ("" + $c.model)) { return "a chunk names model '$($c.model)'" }
        foreach ($p in $f) { if ("$p" -match '\.\.|^[A-Za-z]:|^[\\/]') { return "a chunk names a path outside the repo" } }
        if ($c.test -and $c.test.name) {
            if (-not (Test-TestName $c.test.name)) { return "a chunk names an unusable test" }
            if ($PlanChannels -notcontains ("" + $c.test.channel)) { return "a chunk names test channel '$($c.test.channel)'" }
        }
    }
    $null
}

# ==== The retry ladder ===============================================================================================
# What a chunk build does next, from its tries so far: its own model, then sonnet, then back to the owner.
function Get-ChunkAttempt([string]$model, [int]$tries) {
    switch ($tries) { 0 { @{ model = $model; agent = "build" } } 1 { @{ model = "sonnet"; agent = "fix" } } default { $null } }
}

# ==== Agents =========================================================================================================
# The arguments for one claude run. --restricted confines the file tools to the worktree, ignores user, project and
# local settings and allows only the tools named; --strict-mcp-config with no config loads no MCP server. Only haiku
# or sonnet, never anything else (owner decision: no Opus in the factory).
function New-AgentArgs([string]$model, [string]$tools, [int]$maxTurns) {
    if (@("haiku", "sonnet") -notcontains $model) { throw "The factory runs only haiku or sonnet, not '$model'." }
    $builtIn = (@($tools -split ',' | ForEach-Object { ($_ -replace '\(.*$', '').Trim() } | Where-Object { $_ }) | Select-Object -Unique) -join ","
    # No tools at all (the planner works from its card) is --tools "" with no allow list.
    @("-p", "--output-format", "json", "--model", $model, "--restricted", "--tools", $builtIn, "--strict-mcp-config", "--permission-mode", "acceptEdits") +
      $(if ($tools) { @("--allowedTools", $tools) } else { @() }) +
      @("--disallowedTools", ($DisallowedTools + "," + $ReadDenied), "--max-turns", "$maxTurns")
}
# A second layer under --restricted's confinement: no reading the planning notes, the main checkout, any .env or the
# Claude sign-in and settings.
$ReadDenied = "Read(~/.claude/**),Read(**/.env),Read(//E:/HouseStyle/planning/**),Read(//E:/HouseStyle/repos/house-style/**),Read(//E:/HouseStyle/factory/*.xml)"

# An agent's prompt from a copy of an agent definition: its body, without the frontmatter.
function Get-AgentBody([string]$text) { [regex]::Replace("" + $text, '(?s)^\s*---.*?\r?\n---\r?\n', '') }

# The last JSON object in an agent's final text (fenced, else the whole text), or $null. The last one, because an
# agent's answer comes after anything it quotes.
function ConvertFrom-AgentJson([string]$text) {
    if (-not $text) { return $null }
    $found = $null
    foreach ($m in [regex]::Matches($text, '(?s)```(?:json)?\s*(\{.*?\})\s*```')) { try { $found = $m.Groups[1].Value | ConvertFrom-Json } catch { } }
    if ($found) { return $found }
    $m = [regex]::Match($text, '(?s)(\{.*\})')
    if ($m.Success) { try { return ($m.Groups[1].Value | ConvertFrom-Json) } catch { } }
    $null
}

# The final text of claude -p --output-format json.
function Get-AgentResultText([string]$json) {
    $o = $null; try { $o = $json | ConvertFrom-Json } catch { return $null }
    if ($o -is [array]) { $o = @($o | Where-Object { $_.type -eq "result" }) | Select-Object -Last 1 }
    if ($o) { "" + $o.result } else { $null }
}

# One build ledger line: the triage ledger's entry, plus the issue, stage and model, and always the dollar estimate.
function New-BuildLedgerEntry([int]$issue, [string]$stage, [string]$model, $proc, $out, [string]$result, [datetime]$at = (Get-Date)) {
    $e = New-LedgerEntry $stage 0 $proc $out $true "build" $false $null $at
    $e.Remove("report"); $e.Remove("mode"); $e.Remove("dry_run")
    $e["kind"] = "call"; $e["issue"] = $issue; $e["model"] = $model; $e["result"] = $result
    $e
}

# ==== Review and merge ===============================================================================================
# The blocking findings in one review's output. $null output, or one that doesn't parse as findings, throws: a missing
# review is never "no findings".
function Get-BlockingFindings($review) {
    if ($null -eq $review -or $null -eq $review.PSObject.Properties["findings"]) { throw "The review returned no findings list." }
    @($review.findings | Where-Object { ("" + $_.severity) -eq "blocking" })
}

# The required checks on one commit: pass, fail (with the failed names) or pending. $runs: {name, sha, status, conclusion}.
function Get-CheckState($runs, [string]$sha, [string[]]$required) {
    $failed = @(); $pending = @()
    foreach ($name in $required) {
        $r = @($runs | Where-Object { $_.name -eq $name -and $_.sha -eq $sha }) | Select-Object -Last 1
        if (-not $r -or $r.status -ne "completed") { $pending += $name; continue }
        if (@("success", "skipped", "neutral") -notcontains ("" + $r.conclusion)) { $failed += $name }
    }
    if ($failed.Count) { return @{ state = "fail"; names = $failed } }
    if ($pending.Count) { return @{ state = "pending"; names = $pending } }
    @{ state = "pass"; names = @() }
}

# The comment when an issue goes back to the owner: fixed form, no agent text (the repo is public).
$StuckCodes = "too-big", "owner-only-path", "build-failed", "review-blocking", "ci-failed", "budget", "unverified-ready", "no-spec"
function Format-StuckComment([string]$stage, [int]$tries, [string]$code) {
    if ($StuckCodes -notcontains $code) { $code = "build-failed" }
    "The factory stopped at **$stage** after $tries $(if ($tries -eq 1) { 'try' } else { 'tries' }). Reason: ``$code``. The details are in the factory's notes for this issue."
}

Export-ModuleMember -Function * -Variable StuckCodes
