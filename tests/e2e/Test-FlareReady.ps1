<#
.SYNOPSIS First step of every job on the factory VM: fails in seconds, with the reason, when the VM can't drive Flare.
.DESCRIPTION Without it, a locked or disconnected desktop or a Flare left over from an earlier run shows up minutes later
as a timeout that looks like a test failure. Closes leftover Flare processes; everything else it only reports. A
failure leaves C:\HouseStyle\not-ready.txt, which the host's Watch-Factory.ps1 answers with a reboot.
#>
$ErrorActionPreference = "Stop"
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
    foreach ($p in $problems) { Write-Host "::error::Factory VM not ready: $p" }
    exit 1
}
Remove-Item $note -ErrorAction SilentlyContinue
Write-Host "Factory VM ready: session $session, desktop active, $free GB free."
exit 0
