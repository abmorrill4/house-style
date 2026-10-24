<#
.SYNOPSIS The factory's weekly scorecard (command center spec section 4.5), posted as a comment on the "Factory
scorecard" issue in the private reports repo.
.DESCRIPTION Reports in, routes taken, merge rate, revert rate (factory PRs reverted within 14 days of merge, over the
factory PRs merged in 30 days), report-to-ship time, tokens and cost per merged change, owner touch time (from the
waiting log, when -WaitingLog points at it), suspended areas, and rejection reasons as fixed codes ($ReasonCodes in
Triage.psm1). Counts and codes only: no report text, titles or numbers, so -DryRun can print it to a job log.
Needs GH_TOKEN with access to both repos, and the repo's full git history (for reverts). Windows PowerShell 5.1 or 7.
.EXAMPLE scripts\factory\Post-Scorecard.ps1 -DryRun
#>
param([switch]$DryRun, [int]$Days = 7, [string]$WaitingLog)
$ErrorActionPreference = "Stop"
$repo = "abmorrill4/house-style"
$reports = "abmorrill4/house-style-reports"
$owner = "abmorrill4"
$root = Split-Path (Split-Path $PSScriptRoot)
Import-Module (Join-Path $PSScriptRoot "Triage.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "Controls.psm1") -Force

$ghExe = (Get-Command gh -CommandType Application | Select-Object -First 1).Source   # gh.exe on Windows, gh elsewhere; never the function below
function Gh { $o = & $ghExe @args; if ($LASTEXITCODE) { throw "gh $($args[0]) $($args[1]) failed" }; $o }
function Json($lines) { @($lines | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) }

$now = Get-Date
$monthAgo = $now.AddDays(-45).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")   # the revert rate looks back 30 days, plus margin
$weekAgo = $now.AddDays(-$Days).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$me = ("" + (Gh api user --jq ".login")).Trim()
$rules = Get-Content (Join-Path $root "rules/routing.json") -Raw | ConvertFrom-Json

# Reports the feedback service filed.
$all = @((Gh issue list -R $reports --state all --limit 1000 --json "number,createdAt,labels,author" | ConvertFrom-Json) | Where-Object {
    $l = Get-Labels $_; $_.author.login -eq $owner -and ($l -contains "problem" -or $l -contains "error") })

# The factory's own comments this week: route records (route and reason codes) and runs that stopped (codes).
$routes = @(); $stops = @()
$rows = Json (Gh api "repos/$reports/issues/comments?since=$weekAgo&per_page=100" --paginate --jq '.[] | {issue_url: .issue_url, at: .created_at, login: .user.login, body: .body} | tojson')   # no quotes in the jq: Windows PowerShell drops them
foreach ($c in $rows) { $c | Add-Member -NotePropertyName issue -NotePropertyValue ($c.issue_url -split '/')[-1] }
foreach ($c in @($rows | Where-Object { $_.login -eq $me })) {
    $body = "" + $c.body
    if ($body.StartsWith("**Factory:** **Route:**")) {
        $m = [regex]::Match($body, '(?s)```json\s*(\{.*?\})\s*```')
        if (-not $m.Success) { continue }
        $rec = $m.Groups[1].Value | ConvertFrom-Json
        $routes += [pscustomobject]@{ report = [int]$c.issue; at = $c.at; route = "" + $rec.route; codes = @(Get-ReasonCodes $rec.reasons) }
    }
    elseif ($body -match '^\*\*Factory:\*\* (I couldn''t finish this one|I couldn''t fix this|Not pushing this fix|Not fixing this yet)') {
        $stops += [pscustomobject]@{ at = $c.at; codes = @(Get-ReasonCodes @($body)) }
    }
}

# Factory PRs, and the merge commits main's history reverted.
$prs = @((Gh pr list -R $repo --state all --limit 300 --search "created:>=$($monthAgo.Substring(0, 10))" --json "number,title,headRefName,state,createdAt,mergedAt,closedAt,mergeCommit,labels,files" | ConvertFrom-Json) |
         Where-Object { ("" + $_.headRefName) -like "factory/*" })
Push-Location $root
try { $reverted = Get-RevertedShas ((git log HEAD -E --grep "This reverts commit [0-9a-f]{40}" --format="%x1e%cI%x1f%B") -join "`n") }
finally { Pop-Location }
$autoFixes = @($prs | Where-Object { @($_.labels | ForEach-Object { $_.name }) -contains "factory:auto-fix" -and $_.mergedAt })
$suspended = Get-SuspendedAreas $rules @(Get-RevertedFixes $autoFixes $reverted)

# The ledger: the agent runs this week.
$ledger = @()
$li = @((Gh issue list -R $reports --state open --author "@me" --search "Factory ledger in:title" --limit 20 --json "number,title" | ConvertFrom-Json) | Where-Object { $_.title -eq "Factory ledger" }) | Sort-Object number | Select-Object -First 1
if ($li) {
    $ledgerRows = Json (Gh api "repos/$reports/issues/$($li.number)/comments?since=$weekAgo&per_page=100" --paginate --jq '.[] | {login: .user.login, body: .body} | tojson')
    $ledger = @($ledgerRows | Where-Object { $_.login -eq $me } | ForEach-Object { ConvertFrom-LedgerComment $_.body } | Where-Object { $_ })
}

$waits = $null
if ($WaitingLog -and (Test-Path $WaitingLog)) { $waits = Get-OwnerWaits (Get-Content $WaitingLog) $now.AddDays(-$Days) $now }

$card = Get-Scorecard ([pscustomobject]@{
    now = $now; days = $Days; reports = $all; routes = $routes; stops = $stops; prs = $prs; reverted = $reverted
    ledger = $ledger; waits = $waits; suspended = $suspended })
$text = Format-Scorecard $card

if ($DryRun) { Write-Output "Dry run: this would be posted on the Factory scorecard issue in the private reports repo."; Write-Output ""; Write-Output $text; return }
$si = @((Gh issue list -R $reports --state open --author "@me" --search "Factory scorecard in:title" --limit 20 --json "number,title" | ConvertFrom-Json) | Where-Object { $_.title -eq "Factory scorecard" }) | Sort-Object number | Select-Object -First 1
$num = if ($si) { [int]$si.number } else {
    $url = "" + (Gh issue create -R $reports --title "Factory scorecard" --body "The factory's weekly scorecard, posted every Monday. Counts and reason codes only.")
    [int]($url.Trim().Split('/')[-1])
}
Gh issue comment $num -R $reports --body $text | Out-Null
Write-Output "Posted the scorecard on the Factory scorecard issue in the private reports repo."
