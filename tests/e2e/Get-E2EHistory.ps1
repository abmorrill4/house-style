<#
.SYNOPSIS How each e2e scenario has done over the last runs on the Flare VM: passes, test failures, VM failures and
passes that needed a retry. Flaky scenarios and an unhealthy VM show up here before they show up as a bad week.
.DESCRIPTION Reads the e2e-results artifact (results.json) of each completed e2e run through gh. Runs from before the
VM/test split (2026-10-24) count their failures as "unknown". Artifacts expire after GitHub's retention period.
.EXAMPLE powershell.exe -File tests\e2e\Get-E2EHistory.ps1 -Runs 50
.EXAMPLE powershell.exe -File tests\e2e\Get-E2EHistory.ps1 -Branch main
#>
param(
    [int]$Runs = 30,
    [string]$Branch,
    [string]$Repo = "abmorrill4/house-style"
)
$ErrorActionPreference = "Stop"
$list = @("run", "list", "-R", $Repo, "--workflow", "e2e", "--status", "completed", "--limit", "$Runs", "--json", "databaseId,headBranch,event,createdAt,conclusion")
if ($Branch) { $list += @("--branch", $Branch) }
# In parentheses: Windows PowerShell's ConvertFrom-Json sends a whole array down the pipeline as one object.
$history = @((& gh.exe @list | Out-String | ConvertFrom-Json))
if ($LASTEXITCODE) { throw "gh run list failed" }

$tmp = Join-Path ([IO.Path]::GetTempPath()) "e2e-history-$PID"
$rows = @(); $missing = 0; $notReady = 0
foreach ($run in $history) {
    $dir = Join-Path $tmp $run.databaseId
    $ErrorActionPreference = "Continue"   # gh's stderr (no artifact) would otherwise throw
    & gh.exe run download $run.databaseId -R $Repo -n e2e-results -D $dir 2>$null | Out-Null
    $ok = -not $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    $file = Join-Path $dir "results.json"
    if (-not $ok -or -not (Test-Path $file)) {
        # A failed run with no results stopped before any scenario: the VM wasn't ready, or install or setup failed.
        if ($run.conclusion -eq "failure") { $notReady++ } else { $missing++ }
        continue
    }
    foreach ($r in @((Get-Content $file -Raw | ConvertFrom-Json))) {
        $kind = if ($r.passed) { if ($r.PSObject.Properties["firstTry"] -and $r.firstTry) { "retried" } else { "pass" } }
                elseif ($r.PSObject.Properties["kind"] -and $r.kind) { $r.kind } else { "unknown" }
        $rows += [pscustomobject]@{ scenario = $r.scenario; kind = $kind; branch = $run.headBranch; at = $run.createdAt }
    }
}
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "$($history.Count) runs read. $notReady failed before any scenario (VM not ready, or install or setup failed). $missing had no results (cancelled, or the artifact expired)."
$rows | Group-Object scenario | ForEach-Object {
    $g = $_.Group
    $count = { param($k) @($g | Where-Object kind -eq $k).Count }
    [pscustomobject]@{
        Scenario      = $_.Name
        Runs          = $g.Count
        Pass          = & $count "pass"
        "Pass on retry" = & $count "retried"
        "Test fail"   = & $count "test"
        "VM fail"     = & $count "vm"
        "Unknown fail" = & $count "unknown"
        "Pass %"      = [math]::Round(100 * @($g | Where-Object { $_.kind -in "pass", "retried" }).Count / $g.Count)
        "Last fail"   = (@($g | Where-Object { $_.kind -notin "pass", "retried" } | Sort-Object at -Descending)[0]).at
    }
} | Sort-Object "Pass %", Scenario | Format-Table -AutoSize
