<#
.SYNOPSIS Puts either VM back on its base checkpoint every night, so leftover state, Windows Update and disk growth
can't pile up (factory-runtime spec sections 8, 9 and 11).
.DESCRIPTION Run elevated on the host, as the tasks "HSFACTORY nightly reset" (03:00), "HSAGENTS nightly reset" (03:30)
and "HSAGENTS reset resume" (Install-HostTasks.ps1), or by hand for -Refresh. If the base checkpoint (factory-base or
agents-base) doesn't exist, it stops at once, before setting any flag or touching the VM. It sets maintenance-<VM> with
its process id, start time and each completed step, and never touches the failed-reset marker. Logs go to
E:\HouseStyle\vm\host.log.
-Name HSAGENTS, nightly: takes the HSAGENTS write lock to set its flag (it doesn't wait for the check lock or for held
writes), quiesces the agents runner, deletes carry-done and carries fix-state, the outbox, the inbox and factory-logs out
to vm\hsagents-carry\<date>, restores agents-base, carries them back in (factory-logs also to vm\factory-logs\<date>),
then, holding the write lock, writes every held write and each pending check's latest progress in through
Write-AgentsVM -AsReset, writes carry-done, and clears its flag.
-Name HSAGENTS -Refresh does the same with agents-base rebuilt in between: setup again (Install-FactoryTools.ps1
-NoFlare), the write scan and event log restriction again, the runner quiesced again, and agents-base checkpointed anew.
-Name HSAGENTS -Resume finishes a reset that stopped early: from its flag's last step, before or after the restore.
-Name HSFACTORY, nightly: sets its flag, waits up to 4 hours for the check lock and for the in-use flag (or clears its
flag and skips the night), keeps the nightly e2e results under the Flare VM's copy rules, and restores factory-base.
-Name HSFACTORY -Refresh: the two-checkpoint rebuild. It saves the runner, restores flare-check-base, applies the changes
in vm\flare-refresh (the Flare license, once there is one), starts Flare with the network on, re-checkpoints
flare-check-base, boots it once more to read the license state, copies the runner back and re-checkpoints factory-base
before the runner starts.
.EXAMPLE scripts\factory\Reset-FactoryVM.ps1 -Name HSAGENTS -Refresh
#>
param(
    [ValidateSet("HSFACTORY", "HSAGENTS")][string]$Name = "HSFACTORY",
    [string]$Repo = "abmorrill4/house-style",
    [string]$Root = "E:\HouseStyle\vm",
    [switch]$Refresh,
    [switch]$Resume
)
$script:Dot = $MyInvocation.InvocationName -eq '.'
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
Set-FactoryVMRoot $Root
Set-ResetName $Name

function Log([string]$t) { Write-HostLog "Reset of ${Name}: $t" }
function Get-HostNow { (Get-Date).ToUniversalTime() }
function Wait-Briefly([int]$seconds) { Start-Sleep -Seconds $seconds }
function Get-BaseName { if ($Name -eq $AgentsVMName) { "agents-base" } else { "factory-base" } }
function Get-CarryDir { Get-HostPath "hsagents-carry\$((Get-HostNow).ToString('yyyy-MM-dd'))" }

# ==== The agents VM ==================================================================================================
# The quiesce: the agents runner idle through the API, its service stopped inside the VM, and checked again. A job that
# slipped in gets the service back, and the wait starts over.
$StopRunnerService = { Get-Service "actions.runner.*" -ErrorAction SilentlyContinue | Stop-Service -Force; "stopped" }
$StartRunnerService = { Get-Service "actions.runner.*" -ErrorAction SilentlyContinue | Start-Service; "started" }
function Invoke-AgentsQuiesce([int]$minutes = 60) {
    $deadline = (Get-HostNow).AddMinutes($minutes)
    while ((Get-HostNow) -lt $deadline) {
        if ((Get-RunnerState $AgentsVMName $Repo) -ne "busy") {
            $r = Invoke-VMCall $AgentsVMName $StopRunnerService @() 120
            if (-not $r.ok) { throw "The agents runner's service didn't stop: $($r.error)" }
            if ((Get-RunnerState $AgentsVMName $Repo) -ne "busy") { return }
            Invoke-VMCall $AgentsVMName $StartRunnerService @() 120 | Out-Null
        }
        Wait-Briefly 30
    }
    throw "The agents runner stayed busy for $minutes minutes."
}
# The carry-out: carry-done deleted first, then the four folders read as bytes, every name checked, into vm\hsagents-carry.
$CarryReadBlock = {
    $ErrorActionPreference = "Stop"
    Remove-Item C:\HouseStyle\carry-done -Force -ErrorAction SilentlyContinue
    if (Test-Path C:\HouseStyle\carry-done) { throw "carry-done wouldn't go." }
    $cut14 = (Get-Date).AddDays(-14); $cut90 = (Get-Date).AddDays(-90)
    foreach ($top in "fix-state", "flare-outbox", "flare-inbox", "factory-logs") {
        $root = "C:\HouseStyle\$top"
        foreach ($f in @(Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue)) {
            if ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            $rel = $f.FullName.Substring($root.Length + 1)
            if ($top -eq "factory-logs") {
                $first = $rel.Split('\')[0]
                if ($first -eq "cli-errors") { if ($f.LastWriteTime -lt $cut90) { continue } }
                elseif ($first -ne "fixtures" -and $f.LastWriteTime -lt $cut14) { continue }
            }
            "$top\$rel"
            , [IO.File]::ReadAllBytes($f.FullName)
        }
    }
}
$CarryNameRules = @{
    "fix-state"    = @{ folders = '^\d{1,7}$'; files = '^(fix\.patch|commits\.txt|review\.txt|state\.json)$' }
    "flare-outbox" = @{ folders = '^[0-9a-f-]{36}(\.claimed|\.expired)?$'; files = '^(request\.json|fix\.patch)$' }
    "flare-inbox"  = @{ folders = '^[0-9a-f-]{36}(\.claimed|\.expired)?$|^logs$'; files = '^(result\.json|progress\.json|trial\.json|[A-Za-z0-9][A-Za-z0-9._-]{0,118}\.(log|txt|trx))$' }
    "factory-logs" = @{ folders = '^[A-Za-z0-9]([A-Za-z0-9._-]{0,118}[A-Za-z0-9])?$'; files = '^[A-Za-z0-9]([A-Za-z0-9._-]{0,118}[A-Za-z0-9])?$' }
}
# True when a carried path's names pass: letters or digits at both ends, no . or .., no device names, its folder's rules.
function Test-CarryPath([string]$rel) {
    $parts = @($rel -split '\\')
    if ($parts.Count -lt 2 -or -not $CarryNameRules.ContainsKey($parts[0])) { return $false }
    $rule = $CarryNameRules[$parts[0]]
    foreach ($p in ($parts | Select-Object -Skip 1)) { if ($p -in ".", ".." -or $p -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$' -or $p -notmatch '^[A-Za-z0-9](.*[A-Za-z0-9])?$') { return $false } }
    if ($parts[0] -eq "flare-inbox" -and $parts.Count -eq 2) { return $parts[1] -eq "trial.json" }
    foreach ($p in ($parts | Select-Object -Skip 1 | Select-Object -SkipLast 1)) { if ($p -notmatch $rule.folders) { return $false } }
    $parts[-1] -match $rule.files
}
function Save-CarryOut([string]$dest) {
    $r = Invoke-VMCall $AgentsVMName $CarryReadBlock @() 1800
    if (-not $r.ok) { throw "The carry-out failed: $($r.error)" }
    $out = @($r.output); $total = 0
    Assert-NoReparse $dest
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    New-Item -ItemType Directory -Force $dest | Out-Null
    for ($i = 0; $i + 1 -lt $out.Count; $i += 2) {
        $rel = "" + $out[$i]; $bytes = $out[$i + 1]
        if (-not (Test-CarryPath $rel) -or $bytes -isnot [byte[]]) { Log "Left $rel behind: its name or content isn't one the carry takes."; continue }
        if ($rel -like "factory-logs\*") { $total += $bytes.Length; if ($total -gt 500MB) { Log "factory-logs passed 500 MB, so the rest stays behind."; continue } }
        $target = Join-Path $dest $rel
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        $fs = [IO.File]::Open("\\?\" + [IO.Path]::GetFullPath($target), "CreateNew", "Write", "None")
        try { $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Dispose() }
    }
}
# The carry back in, through the same written-then-renamed writes; factory-logs is also kept on the host.
function Send-CarryIn([string]$from) {
    foreach ($f in @(Get-ChildItem -LiteralPath $from -Recurse -File -ErrorAction SilentlyContinue)) {
        $rel = $f.FullName.Substring($from.Length + 1)
        if (-not (Test-CarryPath $rel)) { continue }
        $r = Invoke-VMCall $AgentsVMName $WriteBlock @((Join-Path "C:\HouseStyle" $rel), [IO.File]::ReadAllBytes($f.FullName)) 120
        if (-not $r.ok) { throw "Carrying $rel back in failed: $($r.error)" }
    }
    $logs = Join-Path $from "factory-logs"
    if (Test-Path -LiteralPath $logs) {
        $keep = Get-HostPath "factory-logs\$((Get-HostNow).ToString('yyyy-MM-dd'))"
        New-Item -ItemType Directory -Force $keep | Out-Null
        Copy-Item -Path (Join-Path $logs "*") -Destination $keep -Recurse -Force
    }
}
function Wait-AgentsUp([int]$minutes = 15) {
    $deadline = (Get-HostNow).AddMinutes($minutes)
    while ((Get-HostNow) -lt $deadline) { $r = Invoke-VMCall $AgentsVMName { "up" } @() 60; if ($r.ok) { return }; Wait-Briefly 15 }
    throw "The agents VM didn't come back after its restore."
}
function Wait-RunnerOnline([string]$vm, [int]$minutes = 15) {
    $deadline = (Get-HostNow).AddMinutes($minutes)
    while ((Get-HostNow) -lt $deadline) { if (@("idle", "busy") -contains (Get-RunnerState $vm $Repo)) { return $true }; Wait-Briefly 30 }
    Log "The runner isn't online 15 minutes after the restore. Watch-Factory takes it from here."
    $false
}
# Step 5 of the reset: holding the write lock, every held write and each pending check's latest progress in through
# -AsReset, then carry-done, then the flag cleared. A write that fails stays held, and carry-done is still written.
function Complete-AgentsReset {
    $lock = Enter-AgentsWriteLock
    if (-not $lock) { throw "The write lock was busy for 5 minutes at the held-write step." }
    try {
        Send-HeldWrites -AsReset
        $r = Write-AgentsVM "carry-done" ([Text.Encoding]::UTF8.GetBytes((Get-HostNow).ToString("o"))) -AsReset -NoHold
        if ($r -ne "written") { throw "carry-done couldn't be written." }
        Clear-MaintenanceFlag $AgentsVMName
    }
    finally { Exit-AgentsWriteLock $lock }
}
# Sets maintenance-HSAGENTS while holding the write lock, so a claim and a flag change can't overlap. Returns whether
# it could.
function Set-AgentsFlag([string]$step) {
    $lock = Enter-AgentsWriteLock
    if (-not $lock) { return $false }
    try { Set-MaintenanceFlag $AgentsVMName $step } finally { Exit-AgentsWriteLock $lock }
    $true
}
function Invoke-AgentsReset {
    if (-not (Set-AgentsFlag "set")) { Log "The write lock was busy for 5 minutes, so the reset skipped this night."; return "skipped" }
    $carry = Get-CarryDir
    try {
        Invoke-AgentsQuiesce; Update-MaintenanceStep $AgentsVMName "quiesced"
        Save-CarryOut $carry; Update-MaintenanceStep $AgentsVMName "carried-out"
        Stop-VMHard $AgentsVMName
        Restore-Checkpoint $AgentsVMName "agents-base"
        Start-VMPlain $AgentsVMName
        Update-MaintenanceStep $AgentsVMName "restored"
        Wait-AgentsUp
        if ($Refresh) {
            & (Join-Path $PSScriptRoot "Install-FactoryTools.ps1") -Name $AgentsVMName -Root $Root -NoFlare; Update-MaintenanceStep $AgentsVMName "updated"
            & (Join-Path $PSScriptRoot "Install-FactoryTools.ps1") -Name $AgentsVMName -Root $Root -NoFlare -ScanOnly; Update-MaintenanceStep $AgentsVMName "rescanned"
            Invoke-AgentsQuiesce
            New-Checkpoint $AgentsVMName "agents-base"; Update-MaintenanceStep $AgentsVMName "checkpointed"
            Invoke-VMCall $AgentsVMName $StartRunnerService @() 120 | Out-Null
        }
        Send-CarryIn $carry; Update-MaintenanceStep $AgentsVMName "carried-back"
        Complete-AgentsReset
        Wait-RunnerOnline $AgentsVMName | Out-Null
        Log "Done: $(if ($Refresh) { 'agents-base rebuilt' } else { 'restored to agents-base' }), folders carried across, carry-done written."
        "done"
    }
    catch { Log "It stopped: $($_.Exception.Message). The flag stays, so the Flare check task starts the resume."; throw }
}
# Finishes a reset that stopped early, from its flag's last step.
function Resume-AgentsReset {
    $flag = Get-MaintenanceFlag $AgentsVMName
    if (-not $flag) { Log "There's no reset to resume."; return "nothing" }
    if (-not (Set-AgentsFlag ("" + $flag.step))) { Log "The write lock was busy, so the resume waits for the next tick."; return "skipped" }
    try {
        $before = @("set", "quiesced", "carried-out") -contains ("" + $flag.step)
        if ($before) {
            # Before the restore: the VM is as it was, so its folders are still there. carry-done, and the runner back.
            Complete-AgentsReset
            Invoke-VMCall $AgentsVMName $StartRunnerService @() 120 | Out-Null
        }
        else {
            if ($flag.step -eq "restored" -and (Get-VMStateName $AgentsVMName) -ne "Running") { Start-VMPlain $AgentsVMName }
            Wait-AgentsUp
            $carry = @(Get-ChildItem -LiteralPath (Get-HostPath "hsagents-carry") -Directory -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1)
            if (-not $carry.Count) { throw "There's nothing in vm\hsagents-carry to carry back in." }
            if ($flag.step -ne "carried-back") { Send-CarryIn $carry[0].FullName }
            Complete-AgentsReset
        }
        Log "Resumed and finished from step $($flag.step)."
        "resumed"
    }
    catch {
        Write-HostLog "The agents VM's reset couldn't be finished. Factory runs stay stopped. The carried folders are in E:\HouseStyle\vm\hsagents-carry."
        throw
    }
}

# ==== The Flare VM ===================================================================================================
# Waits up to 4 hours for the check lock and the in-use flag. Returns whether both are clear.
function Wait-FlareQuiet([int]$minutes = 240) {
    $deadline = (Get-HostNow).AddMinutes($minutes)
    while ((Get-HostNow) -lt $deadline) {
        if (-not (Get-CheckLock) -and -not (Test-InUseFlag)) { return $true }
        Wait-Briefly 60
    }
    $false
}
$ListNightly = {
    $res = @(Get-ChildItem C:\HouseStyle\e2e -Directory -Filter "results-*" -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1)
    if (-not $res.Count) { return }
    foreach ($f in @(Get-ChildItem -LiteralPath $res[0].FullName -Recurse -Force -ErrorAction SilentlyContinue)) {
        if ($f.PSIsContainer -and -not ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        "" + $f.FullName.Substring($res[0].FullName.Length + 1)
        "" + [bool]($f.Attributes -band [IO.FileAttributes]::ReparsePoint)
        , $(if ($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)) { [byte[]]@() } else { [IO.File]::ReadAllBytes($f.FullName) })
    }
}
function Invoke-FlareReset {
    Set-MaintenanceFlag $FlareVMName "set"
    if (-not (Wait-FlareQuiet)) { Clear-MaintenanceFlag $FlareVMName; Log "A Flare check or the in-use flag held the Flare VM for 4 hours, so the reset skipped this night."; return "skipped" }
    if (Test-FailedResetMarker) { Clear-MaintenanceFlag $FlareVMName; Log "The failed-reset marker is set, so nothing runs until the owner clears it."; return "marker" }
    try {
        if ($Refresh) { Invoke-FlareRefresh; return "refreshed" }
        # The nightly e2e results, under the Flare VM's copy rules.
        $r = Invoke-VMCall $FlareVMName $ListNightly @() 300
        if ($r.ok -and @($r.output).Count -and (Test-PlainOutput $r.output)) {
            $l = @($r.output)
            $files = @(@(for ($i = 0; $i + 2 -lt $l.Count; $i += 3) { [pscustomobject]@{ rel = $l[$i]; reparse = ($l[$i + 1] -eq "True"); bytes = $l[$i + 2] } }) | Where-Object { $_.rel -ne "results.json" })
            $c = Copy-FlareResults $files (Get-HostPath "flare-logs\nightly-$((Get-HostNow).ToString('yyyy-MM-dd'))")
            if (-not $c.ok) { Log "The nightly results weren't kept: $($c.why)." }
        }
        if ((Get-RunnerState $FlareVMName $Repo) -eq "busy") { Log "The flare runner is busy, so the reset skipped this night."; return "skipped" }
        Stop-VMHard $FlareVMName
        Restore-Checkpoint $FlareVMName "factory-base"
        Start-VMPlain $FlareVMName
        Log "Reset to factory-base."
        "done"
    }
    finally { Clear-MaintenanceFlag $FlareVMName }
}
# The two-checkpoint rebuild (section 9): runner saved, flare-check-base restored and changed, Flare started with the
# network on, flare-check-base checkpointed, booted once more for the license read, then the runner back and
# factory-base checkpointed before the runner starts.
function Invoke-FlareRefresh {
    $cred = Get-VMCredential $FlareVMName
    $vault = Get-HostPath "runner-vault\actions-runner"
    Assert-NoReparse $vault
    Update-MaintenanceStep $FlareVMName "saving-runner"
    $s = New-PSSession -VMName $FlareVMName -Credential $cred
    try {
        if (Test-Path -LiteralPath $vault) { Remove-Item -LiteralPath $vault -Recurse -Force }
        Copy-Item -FromSession $s -Path "C:\actions-runner" -Destination $vault -Recurse -Force
    }
    finally { Remove-PSSession $s }
    Protect-AdminOnly (Get-HostPath "runner-vault")
    Update-MaintenanceStep $FlareVMName "restoring"
    Stop-VMHard $FlareVMName
    Restore-Checkpoint $FlareVMName "flare-check-base"
    Start-VMPlain $FlareVMName
    Wait-FlareDesktop
    # The changes: each script in vm\flare-refresh, the Flare license's once there is one. None installs a runner or a plugin.
    foreach ($f in @(Get-ChildItem -LiteralPath (Get-HostPath "flare-refresh") -Filter *.ps1 -ErrorAction SilentlyContinue | Sort-Object Name)) {
        Copy-VMFile -Name $FlareVMName -SourcePath $f.FullName -DestinationPath "C:\Setup\refresh\$($f.Name)" -CreateFullPath -FileSource Host -Force
        $r = Invoke-VMCall $FlareVMName { param($p) & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $p 2>&1 | Out-Null; if ($LASTEXITCODE) { throw "failed" }; "ok" } @("C:\Setup\refresh\$($f.Name)") 3600
        if (-not $r.ok) { throw "The change $($f.Name) failed on the Flare VM." }
    }
    # Flare with the network on, so a license that checks in online does so; the read starts and closes Flare.
    Invoke-VMCall $FlareVMName { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\HouseStyle\flare-check\Invoke-FlareCheck.ps1 -TrialRead | Out-Null; "ok" } @() 600 | Out-Null
    Update-MaintenanceStep $FlareVMName "checkpointing-flare-check-base"
    New-Checkpoint $FlareVMName "flare-check-base"
    # Boots the new checkpoint once more and reads the license state the way a check's step 4 does.
    Stop-VMHard $FlareVMName
    Restore-Checkpoint $FlareVMName "flare-check-base"
    Start-VMPlain $FlareVMName
    Wait-FlareDesktop
    $r = Invoke-VMCall $FlareVMName { @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\HouseStyle\flare-check\Invoke-FlareCheck.ps1 -TrialRead 2>&1 | ForEach-Object { "$_" }) } @() 600
    $text = if ($r.ok -and (Test-PlainOutput $r.output)) { (@($r.output) | Select-Object -Skip 1) -join "`n" } else { "" }
    Set-TrialFromRead (ConvertFrom-FlareTrialRead $text) $text (Get-HostNow) | Out-Null
    Write-AgentsVM "flare-inbox\trial.json" (Get-InboxTrialBytes) | Out-Null
    Update-MaintenanceStep $FlareVMName "restoring-runner"
    $s = New-PSSession -VMName $FlareVMName -Credential $cred
    try { Copy-Item -ToSession $s -Path $vault -Destination "C:\actions-runner" -Recurse -Force } finally { Remove-PSSession $s }
    & (Join-Path $PSScriptRoot "Install-Runner.ps1") -Name $FlareVMName -Root $Root -NoStart
    New-Checkpoint $FlareVMName "factory-base"
    Invoke-VMCall $FlareVMName { Start-ScheduledTask -TaskName "GitHub runner"; "started" } @() 60 | Out-Null
    Log "Refreshed: flare-check-base and factory-base rebuilt, license state read."
}
function Wait-FlareDesktop([int]$minutes = 15) {
    $deadline = (Get-HostNow).AddMinutes($minutes)
    while ((Get-HostNow) -lt $deadline) { $r = Invoke-VMCall $FlareVMName { [bool](Get-Process explorer -ErrorAction SilentlyContinue) } @() 60; if ($r.ok -and @($r.output)[0] -eq $true) { return }; Wait-Briefly 15 }
    throw "The Flare VM didn't reach its desktop."
}

# ==== Either ==========================================================================================================
function Invoke-Reset {
    # The host state folders first: the reset delivers held writes from them as host-written.
    $acl = @(Test-HostStateAcl)
    if ($acl.Count) { Write-HostLog "The reset of $Name didn't run, because the host state folders' owner or ACL is wrong: $($acl[0])."; return "acl" }
    # No base checkpoint: stop before setting any flag or touching the VM.
    $base = Get-BaseName
    if (-not (Test-Checkpoint $Name $base)) {
        Write-HostLog "$Name has no $base checkpoint yet, so the reset didn't run. Take the first one with the migration step."
        return "no-base"
    }
    if ($Name -eq $AgentsVMName) { if ($Resume) { return (Resume-AgentsReset) }; return (Invoke-AgentsReset) }
    Invoke-FlareReset
}

if (-not $script:Dot) { Write-Host (Invoke-Reset) }
