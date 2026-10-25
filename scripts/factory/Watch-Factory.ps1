<#
.SYNOPSIS Runs on the host every 5 minutes and keeps both VMs' runners up (factory-runtime spec section 11).
.DESCRIPTION For each VM: starts it if it's off, and reboots the guest if its GitHub runner has been offline for
-OfflineMinutes. A guest reboot through Hyper-V, never a hard reset, and at most one every 30 minutes, never while the
VM is still booting. Logs each action to vm\host.log.
- The Flare VM (HSFACTORY), as before: nothing while maintenance-HSFACTORY, the check lock, the in-use flag or the
  failed-reset marker exists (it forgets an offline time while the check lock holds the VM), and the not-ready note
  the last e2e job left (C:\HouseStyle\not-ready.txt) is answered with a reboot.
- The agents VM (HSAGENTS): only start and reboot, from the runner's state on GitHub, and nothing at all while
  maintenance-HSAGENTS exists. It never runs a command inside the agents VM.
Uses the owner's gh sign-in. Registered by Install-HostTasks.ps1.
#>
param(
    [string]$Repo = "abmorrill4/house-style",
    [string]$Root = "E:\HouseStyle\vm",
    [int]$OfflineMinutes = 10
)
$script:Dot = $MyInvocation.InvocationName -eq '.'
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
Set-FactoryVMRoot $Root
function Get-HostNow { Get-Date }
function Get-VMUptimeMinutes([string]$vm) { (Get-VM -Name $vm).Uptime.TotalMinutes }
# The last e2e job's not-ready note in the Flare VM, removed as it's read. The Flare VM only.
function Read-NotReadyNote {
    $r = Invoke-VMCall $FlareVMName { $f = "$env:SystemDrive\HouseStyle\not-ready.txt"; if (Test-Path $f) { Get-Content $f -Raw; Remove-Item $f } } @() 60
    if ($r.ok) { "" + (@($r.output) -join " ") } else { "" }
}

function Get-WatchState([string]$vm) {
    $s = Read-HostJson (Get-HostPath "watch-$vm.json")
    if (-not $s) { $s = [pscustomobject]@{} }
    foreach ($p in "offlineSince", "lastRestart", "lastSeen") { if (-not ($s.PSObject.Properties.Name -contains $p)) { $s | Add-Member $p $null } }
    $s
}
function Save-WatchState([string]$vm, $s) { $s.lastSeen = (Get-HostNow).ToString("o"); Write-HostJson (Get-HostPath "watch-$vm.json") $s }

# One VM. Returns what it did.
function Invoke-WatchVM([string]$vm) {
    if (Test-Path -LiteralPath (Get-FlagPath $vm)) { return "maintenance" }
    $flare = $vm -eq $FlareVMName
    $s = Get-WatchState $vm
    if ($flare -and ((Test-InUseFlag) -or (Test-FailedResetMarker))) { return "held" }
    if ($flare -and (Get-CheckLock)) { $s.offlineSince = $null; Save-WatchState $vm $s; return "checking" }   # the Flare check owns it; its offline time means nothing
    # An offline time from before a gap in watching (gh failing, the host asleep) says nothing about now.
    if ($s.lastSeen -and ((Get-HostNow) - [datetime]$s.lastSeen).TotalMinutes -gt 15) { $s.offlineSince = $null }
    $sinceRestart = if ($s.lastRestart) { (Get-HostNow) - [datetime]$s.lastRestart } else { [timespan]::MaxValue }
    $state = Get-VMStateName $vm
    if ($state -ne "Running") { Write-HostLog "$vm was $state; starting it."; Start-VMPlain $vm; $s.offlineSince = $null; $s.lastRestart = (Get-HostNow).ToString("o"); Save-WatchState $vm $s; return "started" }
    $runner = Get-RunnerState $vm $Repo
    if ($runner -eq "unknown") { return "unknown" }   # GitHub unreachable, gh signed out or runner unknown: no evidence either way
    if ($runner -ne "offline") {
        if ($s.offlineSince) { Write-HostLog "$vm's runner is back online."; $s.offlineSince = $null }
        Save-WatchState $vm $s
        if (-not $flare -or $sinceRestart.TotalMinutes -lt 30) { return "online" }
        # Online but unusable: the e2e job's ready check leaves a note when the desktop is locked or disconnected.
        $note = Read-NotReadyNote
        if ($note.Trim()) { return (Invoke-GuestReboot $vm $s "The last job found the VM not ready ($($note.Trim()))") }
        return "online"
    }
    if (-not $s.offlineSince) { $s.offlineSince = (Get-HostNow).ToString("o"); Save-WatchState $vm $s; Write-HostLog "$vm's runner is offline."; return "offline" }
    Save-WatchState $vm $s
    $down = (Get-HostNow) - [datetime]$s.offlineSince
    if ($down.TotalMinutes -lt $OfflineMinutes -or (Get-VMUptimeMinutes $vm) -lt $OfflineMinutes -or $sinceRestart.TotalMinutes -lt 30) { return "waiting" }
    Invoke-GuestReboot $vm $s "$vm's runner has been offline for $([int]$down.TotalMinutes) minutes"
}
# A guest reboot through Hyper-V's integration services: never a hard reset, which could break a Windows update.
function Invoke-GuestReboot([string]$vm, $s, [string]$why) {
    Write-HostLog "$why; rebooting $vm."
    try { Restart-VMGuest $vm } catch { Write-HostLog "Rebooting $vm failed ($($_.Exception.Message)); leaving it for the owner."; return "reboot-failed" }
    $s.offlineSince = $null; $s.lastRestart = (Get-HostNow).ToString("o"); Save-WatchState $vm $s
    "rebooted"
}

# FACTORY_MODE, read with the owner's gh: off, shadow, live, or $null when it can't be read.
function Get-FactoryMode {
    $ErrorActionPreference = "Continue"
    $v = & gh.exe variable get FACTORY_MODE -R $Repo 2>$null
    if (-not $LASTEXITCODE) { return ("" + $v).Trim().ToLowerInvariant() }
    & gh.exe auth status 2>$null | Out-Null
    if ($LASTEXITCODE) { return $null }   # signed out: no evidence either way
    "off"   # signed in and no variable: unset is off
}
# Logs the "factory off for a day" line once a day while FACTORY_MODE has been off for 24 hours. Nothing when it can't
# be read.
function Test-FactoryOff {
    $mode = Get-FactoryMode
    if ($null -eq $mode) { return "unknown" }
    $p = Get-HostPath "factory-mode.json"
    $s = Read-HostJson $p
    $now = Get-HostNow
    if (@("shadow", "live") -contains $mode) { if ($s) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }; return "on" }
    if (-not $s) { $s = [pscustomobject]@{ offSince = $now.ToString("o"); warnedDay = $null }; Write-HostJson $p $s; return "off" }
    $day = $now.ToString("yyyy-MM-dd")
    if (($now - [datetime]$s.offSince).TotalHours -ge 24 -and $s.warnedDay -ne $day) {
        Write-HostLog "FACTORY_MODE has been off for 24 hours, so no nightly release has gone out. Decide whether to keep it off."
        $s.warnedDay = $day; Write-HostJson $p $s; return "warned"
    }
    "off"
}
# Both VMs, once the host state folders' owner and ACL are right.
function Invoke-Watch {
    $acl = @(Test-HostStateAcl)
    if ($acl.Count) { Write-HostLog "The factory watch didn't run, because the host state folders' owner or ACL is wrong: $($acl[0])."; return "acl" }
    foreach ($vm in $FlareVMName, $AgentsVMName) { try { Invoke-WatchVM $vm | Out-Null } catch { Write-HostLog "Watching $vm failed: $($_.Exception.Message)" } }
    try { Test-FactoryOff | Out-Null } catch { }
    "watched"
}
if (-not $script:Dot) { Invoke-Watch | Out-Null }
