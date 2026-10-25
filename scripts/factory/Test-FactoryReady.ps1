<#
.SYNOPSIS First step of every job: fails in seconds, with the reason, when the VM can't do its job.
.DESCRIPTION On the Flare VM (no switch), for e2e.yml: a locked or disconnected desktop or a Flare left over from an
earlier run would show up minutes later as a timeout that looks like a test failure. Closes leftover Flare processes;
everything else it only reports. A failure leaves C:\HouseStyle\not-ready.txt, which the host's Watch-Factory.ps1
answers with a reboot.
On the agents VM (-NoFlare), for factory.yml (factory-runtime spec section 11): disk space, the agent task, no agent
user process left, no restore rule left, no writable package cache, the egress rules, the Operational log and the event
log restriction, the quick write scan, carry-done, and, when GH_TOKEN is set, the fork-PR approval setting.
Invoke-Factory.ps1 runs the same checks itself before it picks anything.
#>
param([switch]$NoFlare)
$ErrorActionPreference = "Stop"
if ($NoFlare) {
    Import-Module (Join-Path $PSScriptRoot "Controls.psm1") -Force
    $problems = @(Get-AgentsVMProblems)
    if ($env:GH_TOKEN) {
        $ErrorActionPreference = "Continue"
        $fork = Test-ForkApproval ((& gh.exe api "repos/abmorrill4/house-style/actions/permissions/fork-pr-contributor-approval" 2>$null) -join "`n")
        $ErrorActionPreference = "Stop"
        if ($fork) { $problems += $fork }
    }
    if ($problems.Count) { foreach ($p in $problems) { Write-Host "::error::$(Format-Copy AgentsNotReady @{ reason = $p })" }; exit 1 }
    Write-Host "Agents VM ready."
    exit 0
}

$problems = @()
$session = (Get-Process -Id $PID).SessionId
$mine = { param($name) @(Get-Process $name -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session) }

if ($session -eq 0) { $problems += "The runner runs as a service (session 0), which has no desktop for Flare." }
else {
    if (-not (& $mine explorer).Count) { $problems += "The runner's session has no desktop (no explorer)." }
    # Disconnected (a closed Hyper-V enhanced session, say) breaks UI Automation the same way a lock does.
    $row = @(qwinsta 2>$null | Where-Object { ($_.Substring(1) -split '\s+') -contains "$session" })[0]
    if ($row -and $row -notmatch '\bActive\b') { $problems += "The runner's session isn't active (it's disconnected)." }
}
# LogonUI runs in a session while it is locked.
if ((& $mine LogonUI).Count) { $problems += "The desktop is locked." }
$free = [math]::Round((Get-PSDrive C).Free / 1GB, 1)
if ($free -lt 10) { $problems += "Only $free GB free on C:." }

$stray = @(Get-Process Flare -ErrorAction SilentlyContinue)
if ($stray.Count) {
    Write-Host "Closing $($stray.Count) Flare process(es) left by an earlier run."
    try { $stray | Stop-Process -Force -PassThru | Wait-Process -Timeout 30 }
    catch { $problems += "A Flare left by an earlier run won't close ($($_.Exception.Message))." }
}

$note = Join-Path $env:SystemDrive "HouseStyle\not-ready.txt"
if ($problems) {
    New-Item -ItemType Directory -Force (Split-Path $note) | Out-Null
    $problems -join " " | Set-Content $note
    foreach ($p in $problems) { Write-Host "::error::Flare VM not ready. $p" }
    exit 1
}
Remove-Item $note -ErrorAction SilentlyContinue
Write-Host "Flare VM ready. Session $session, desktop active, $free GB free."
exit 0
