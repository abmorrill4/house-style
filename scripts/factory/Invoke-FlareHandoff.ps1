<#
.SYNOPSIS The host task "House Style Flare check" (factory-runtime spec section 5): runs every minute on THESEUS, takes
one Flare check request from the agents VM, runs it on the Flare VM, puts the Flare VM back, and only then hands the
result over.
.DESCRIPTION Registered by Install-HostTasks.ps1 to run from E:\HouseStyle\vm\bin, elevated, in the owner's signed-in
session (a locked session is fine), with IgnoreNew and a 4-hour limit. Each tick, in order:
1. a stale maintenance-HSFACTORY flag is removed and logged; a stale maintenance-HSAGENTS starts the task "House Style
   HSAGENTS reset resume". The failed-reset marker is never touched.
2. once a day, an active trial whose date passed becomes expired, a trial ending within seven days is warned about,
   and trial.json goes to the agents VM;
3. held writes go in, oldest first, while maintenance-HSAGENTS is clear;
4. it stops if the failed-reset marker or maintenance-HSFACTORY exists;
5. a stale check lock is recovered (step 9, then step 10);
6. a due state re-read runs, and ends the tick;
7. while the trial state is active or licensed, a new request is claimed and checked: steps 1 to 10 of section 5,
   with the Flare VM restored from flare-check-base, its network cut while the fix's code builds and runs, and
   factory-base restored before anything is delivered.
Every write into the agents VM goes through Write-AgentsVM. Calls into the Flare VM run with deadlines; a hang turns
the Flare VM off and goes to the reset with vm/timeout.
-ScratchVM HSSCRATCH-<n> points one check at a scratch copy for E13: its own lock, staging folder and trial file under
vm\scratch, no claim, no delivery into the agents VM, and no touch of HSFACTORY's checkpoints. The registered task
never passes it.
#>
param([string]$ScratchVM, [string]$Root = "E:\HouseStyle\vm", [string]$Reports = "abmorrill4/house-style-reports")
$script:Dot = $MyInvocation.InvocationName -eq '.'
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "Controls.psm1") -Force   # Save-Holdout
Set-FactoryVMRoot $Root
$script:CheckVM = $FlareVMName; $script:Deliver = $true
if ($ScratchVM) { Use-ScratchPaths $ScratchVM; $script:CheckVM = $ScratchVM; $script:Deliver = $false }

# ==== Seams the tests replace =========================================================================================
function Get-HostNow { (Get-Date).ToUniversalTime() }
function Wait-Briefly([int]$seconds) { Start-Sleep -Seconds $seconds }
function Start-ResumeTask { Start-ScheduledTask -TaskName "House Style HSAGENTS reset resume" }
# The holdout scenarios, read with the owner's own gh sign-in (FACTORY_GH_TOKEN never reads them), into $dest.
function Save-HoldoutFiles([string]$dest) {
    $list = {
        $ErrorActionPreference = "Continue"
        $o = @(& gh.exe api "repos/$Reports/contents/holdout" 2>&1)
        if ($LASTEXITCODE) { if (($o | ForEach-Object { "$_" }) -join " " -match 'HTTP 404|Not Found') { return $null }; throw "gh api contents/holdout failed" }
        , @((@($o | Where-Object { $_ -is [string] }) -join "`n") | ConvertFrom-Json)
    }
    $get = { param($name) $ErrorActionPreference = "Continue"; & gh.exe api -H "Accept: application/vnd.github.raw+json" "repos/$Reports/contents/holdout/$name" }
    @(Save-Holdout $list $get $dest)
}

# ==== Calls into the Flare VM, with their deadlines ===================================================================
# A call that runs past its deadline is a hang: the Flare VM goes off and the check goes to its reset with vm/timeout.
# While it runs, VMConnect opening on the Flare VM turns it off too (vm/in-use). Only plain strings and byte arrays come
# back.
function New-CheckStop([string]$kind, [string]$message) { $e = New-Object System.Exception $message; $e.Data["kind"] = $kind; $e }
function Invoke-FlareCall([scriptblock]$block, [object[]]$argv, [datetime]$deadline, [switch]$WatchVMConnect) {
    $vmConnect = { if ($WatchVMConnect -and (Test-VMConnectOpen $script:CheckVM)) { Stop-VMHard $script:CheckVM; throw (New-CheckStop "in-use" "VMConnect opened on the Flare VM") } }
    & $vmConnect
    # In slices of 10 seconds, so VMConnect is noticed while a long step runs.
    $slice = 10; $r = $null
    while ($true) {
        $left = [int][Math]::Floor(($deadline - (Get-HostNow)).TotalSeconds)
        if ($left -le 0) { Stop-VMHard $script:CheckVM; throw (New-CheckStop "timeout" "a call into the Flare VM hung") }
        if (-not $r) { $job = Start-VMCallJob $script:CheckVM $block $argv; $r = $job }
        if (Wait-Job -Job $job -Timeout ([Math]::Min($slice, $left))) { break }
        try { & $vmConnect } catch { Stop-VMCallJob $job $script:CheckVM | Out-Null; throw }
    }
    $errs = $null
    $out = @(Receive-Job -Job $job -ErrorAction SilentlyContinue -ErrorVariable errs)
    $state = "" + $job.State
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    & $vmConnect
    if ($state -ne "Completed" -or @($errs).Count) { throw "$(if (@($errs).Count) { $errs[0].Exception.Message } else { "the call ended $state" })" }
    if (-not (Test-PlainOutput $out)) { throw (New-CheckStop "no-scenarios" "the Flare VM sent back something other than text and bytes") }
    [pscustomobject]@{ ok = $true; output = $out }
}
$FlareScript = "C:\HouseStyle\flare-check\Invoke-FlareCheck.ps1"
$RunFlareStep = {
    param([string]$script, [string]$step, [string]$base, [byte[]]$patch)
    $a = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script)
    if ($step -eq "TrialRead") { $a += "-TrialRead" } else { $a += @("-Step", $step) }
    if ($base) { $a += @("-Base", $base) }
    if ($patch) { $pf = "C:\HouseStyle\check-patch.patch"; [IO.File]::WriteAllBytes($pf, $patch); $a += @("-PatchFile", $pf) }
    $o = @(& powershell.exe @a 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE) { throw "The Flare VM's $step step failed: $(@($o | Select-Object -Last 2) -join ' ')" }
    $o
}
$ListResults = {
    $res = "C:\HouseStyle\check\results"
    foreach ($f in @(Get-ChildItem -LiteralPath $res -Recurse -Force -ErrorAction SilentlyContinue)) {
        if ($f.PSIsContainer -and -not ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        "" + $f.FullName.Substring($res.Length + 1)
        "" + [bool]($f.Attributes -band [IO.FileAttributes]::ReparsePoint)
        , $(if ($f.PSIsContainer -or ($f.Attributes -band [IO.FileAttributes]::ReparsePoint)) { [byte[]]@() } else { [IO.File]::ReadAllBytes($f.FullName) })
    }
}
$WaitDesktop = { [bool](Get-Process explorer -ErrorAction SilentlyContinue) -and [bool](@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { ("" + $_.CommandLine) -like "*Invoke-FlareCheck.ps1*-Agent*" }).Count) }
$StopListener = { Stop-ScheduledTask -TaskName "GitHub runner" -ErrorAction SilentlyContinue; Get-Process Runner.Listener, Runner.Worker -ErrorAction SilentlyContinue | Stop-Process -Force; "stopped" }
$StartListener = { Start-ScheduledTask -TaskName "GitHub runner" -ErrorAction SilentlyContinue; "started" }

# ==== The steps (section 5's table) ===================================================================================
# Step 2: the in-use flag and VMConnect end it at once; a busy flare runner is waited for (30 minutes), its listener
# stopped, and checked again. Returns $null when the Flare VM is free, else the vm kind.
function Wait-FlareFree {
    if ((Test-InUseFlag) -or (Test-VMConnectOpen $script:CheckVM)) { return "in-use" }
    if ($ScratchVM) { return $null }
    $deadline = (Get-HostNow).AddMinutes(30)
    while ((Get-HostNow) -lt $deadline) {
        if ((Get-RunnerState $FlareVMName) -ne "busy") {
            Invoke-VMCall $FlareVMName $StopListener @() 60 | Out-Null
            if ((Get-RunnerState $FlareVMName) -ne "busy") { return $null }
            Invoke-VMCall $FlareVMName $StartListener @() 60 | Out-Null   # a job slipped in: let it run, and wait again
        }
        Wait-Briefly 30
    }
    "busy"
}
# Step 3: enhanced session off, flare-check-base restored, started with the network on, and the desktop's agent task up.
function Start-FlareCheckBase {
    $deadline = (Get-HostNow).AddMinutes(15)
    Set-EnhancedSession $false
    Stop-VMHard $script:CheckVM
    if (-not $ScratchVM) { Restore-Checkpoint $script:CheckVM "flare-check-base" }
    Start-VMPlain $script:CheckVM
    while ((Get-HostNow) -lt $deadline) {
        if (Test-VMConnectOpen $script:CheckVM) { Stop-VMHard $script:CheckVM; throw (New-CheckStop "in-use" "VMConnect opened on the Flare VM") }
        $r = Invoke-VMCall $script:CheckVM $WaitDesktop @() 60
        if ($r.ok -and @($r.output)[0] -eq $true) { return }
        Wait-Briefly 15
    }
    throw (New-CheckStop "no-start" "the Flare VM didn't reach its desktop and agent task")
}
# Step 4.1, also the state re-read and each -Refresh: Flare's license state, before anything from a patch is on the VM.
function Read-FlareLicenseState([datetime]$deadline) {
    $r = Invoke-FlareCall $RunFlareStep @($FlareScript, "TrialRead", $null, $null) $deadline -WatchVMConnect
    $text = (@($r.output) | Select-Object -Skip 1) -join "`n"
    Set-TrialFromRead (ConvertFrom-FlareTrialRead $text) $text (Get-HostNow)
}
# Step 9: factory-base restored. If that worked: started, reconnected, enhanced session on, the staged result moved to
# flare-pending under the write lock, and the check lock released. If not: off, offline, the marker set, nothing
# delivered, and the lock kept for the owner.
function Invoke-CheckReset([string]$checkId) {
    try {
        Stop-VMHard $script:CheckVM
        if (-not $ScratchVM) { Restore-Checkpoint $script:CheckVM "factory-base" }
    }
    catch {
        try { Stop-VMHard $script:CheckVM } catch { }
        Set-FailedResetMarker
        Write-HostLog "The Flare VM couldn't be put back on its checkpoint, so it's off, offline and marked as a failed reset. Nothing was delivered. Restore factory-base before you start or reconnect the Flare VM. Then turn enhanced session mode back on in Hyper-V Settings, and remove flare-check.lock and flare-reset-failed."
        return $false
    }
    if (-not $ScratchVM) {
        Start-VMPlain $script:CheckVM
        Connect-VMNet $script:CheckVM
        Set-EnhancedSession $true
    }
    if ($checkId -and $checkId -ne "re-read") {
        $staged = Get-StagingPath $checkId
        if (Test-Path -LiteralPath $staged) {
            $lock = Enter-AgentsWriteLock   # all writes to flare-pending happen under the write lock
            try {
                $dest = Get-HostPath "flare-pending\$checkId\staged"
                Assert-NoReparse $dest
                New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
                if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
                Move-Item -LiteralPath $staged -Destination $dest
            }
            finally { Exit-AgentsWriteLock $lock }
        }
    }
    Set-CheckStep 9
    Exit-CheckLock
    $true
}
# Step 10: result.json, the logs and the current trial.json, through Write-AgentsVM. The screenshots stay on the host,
# in vm\flare-logs\<check id>.
function Send-CheckResult([string]$checkId, $result, [bool]$nothingRestored) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Compress))
    if (-not $script:Deliver) { Write-HostJson (Get-HostPath "scratch\result.json") $result; return }
    $staged = Get-HostPath "flare-pending\$checkId\staged"
    if (Test-Path -LiteralPath $staged) {
        $logs = Get-HostPath "flare-logs\$checkId"; New-Item -ItemType Directory -Force $logs | Out-Null
        foreach ($f in @(Get-ChildItem -LiteralPath $staged -Recurse -File)) {
            $rel = $f.FullName.Substring($staged.Length + 1)
            if ($rel -ceq "result.json") { continue }
            Copy-Item -LiteralPath $f.FullName (Join-Path $logs ($rel -replace '\\', '-')) -Force
            if ($f.Extension -in ".log", ".txt", ".trx" -and $f.Length -le 4MB) { Write-AgentsVM "flare-inbox\$checkId\logs\$($rel -replace '\\', '-')" ([IO.File]::ReadAllBytes($f.FullName)) $checkId | Out-Null }
        }
        Remove-Item -LiteralPath $staged -Recurse -Force
    }
    # trial.json first: a trial-expired or trial-unknown result is read against it, so it must never arrive before it.
    # If trial.json is held, the result is held after it, and the held writes go in oldest first.
    if ((Write-AgentsVM "flare-inbox\trial.json" (Get-InboxTrialBytes)) -eq "written") { Write-AgentsVM "flare-inbox\$checkId\result.json" $bytes $checkId | Out-Null }
    else { $l = Enter-AgentsWriteLock; try { Save-HeldWrite "flare-inbox\$checkId\result.json" $bytes $checkId | Out-Null } finally { if ($l) { Exit-AgentsWriteLock $l } } }
    if ($nothingRestored) {
        Exit-CheckLock
        if (-not $ScratchVM) { Invoke-VMCall $FlareVMName $StartListener @() 60 | Out-Null }
    }
}
# The VM's own result.json, read for its outcome and names only. A license report while the host's state was active or
# licensed, or no scenarios at all, is vm/no-scenarios.
function ConvertFrom-StagedResult([string]$checkId, [string]$stagedDir) {
    $vm = $null
    $p = Join-Path $stagedDir "result.json"
    $o = try { [IO.File]::ReadAllText($p) | ConvertFrom-Json } catch { $null }
    if (Test-Path -LiteralPath (Join-Path $stagedDir "license-report.txt")) { $vm = "no-scenarios" }
    elseif (-not $o -or @("pass", "fix", "holdout") -notcontains ("" + $o.outcome)) { $vm = $(if ($o -and $o.outcome -eq "vm") { "no-start" } else { "no-scenarios" }) }
    if ($vm) { return [pscustomobject]@{ check = $checkId; outcome = "vm"; vmKind = $vm } }
    $names = { param($v) @($v | Where-Object { $_ -is [string] -and $_ -cmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$' } | Select-Object -First 50) }
    [pscustomobject]@{ check = $checkId; outcome = "" + $o.outcome; failed = @(& $names $o.failed); retried = @(& $names $o.retried) }
}
# Steps 2 to 10 for a claimed request.
function Invoke-CheckRun($claim) {
    $id = $claim.id
    $kind = $null; $restored = $false; $result = $null
    try {
        # Step 1's request check: its id, a base commit and the patch's SHA-256.
        $req = try { [Text.Encoding]::UTF8.GetString($claim.request) | ConvertFrom-Json } catch { $null }
        $sha = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($claim.patch) | ForEach-Object { $_.ToString("x2") })
        if (-not $req -or $req.check -ne $id -or ("" + $req.base) -notmatch '^[0-9a-f]{40}$' -or $req.patchSha256 -ne $sha) { throw (New-CheckStop "interrupted" "the request didn't check out") }
        Set-CheckStep 1
        # Step 2.
        $busy = Wait-FlareFree
        if ($busy) { throw (New-CheckStop $busy "the Flare VM wasn't free") }
        Set-CheckStep 2; Write-CheckProgress $id 2
        # Step 3.
        $restored = $true
        Start-FlareCheckBase
        Set-CheckStep 3; Write-CheckProgress $id 3
        # Step 4, network on: the trial read first, then the base, the fix tree and the restore.
        $deadline = (Get-HostNow).AddMinutes(30)
        $t = Read-FlareLicenseState $deadline
        if ($t.state -eq "expired") { throw (New-CheckStop "trial-expired" "Flare's trial or license expired") }
        if ($t.state -eq "unknown") { throw (New-CheckStop "trial-unknown" "Flare's license state couldn't be read") }
        try { Invoke-FlareCall $RunFlareStep @($FlareScript, "Prepare", $req.base, $claim.patch) $deadline -WatchVMConnect | Out-Null }
        catch { if ($_.Exception.Data["kind"]) { throw }; throw (New-CheckStop "no-start" $_.Exception.Message) }
        Set-CheckStep 4; Write-CheckProgress $id 4
        # Step 5: the network cut, read back, then the holdouts in.
        try { Disconnect-VMNet $script:CheckVM } catch { throw (New-CheckStop "not-ready" "the Flare VM's network didn't disconnect") }
        if (Test-VMNetConnected $script:CheckVM) { throw (New-CheckStop "not-ready" "an adapter is still connected") }
        $hold = Get-StagingPath "$id-holdout"
        foreach ($h in @(Save-HoldoutFiles $hold)) {
            Invoke-FlareCall { param($n, [byte[]]$b) New-Item -ItemType Directory -Force C:\HouseStyle\check\holdout | Out-Null; [IO.File]::WriteAllBytes("C:\HouseStyle\check\holdout\$n", $b); "copied" } @($h, [IO.File]::ReadAllBytes((Join-Path $hold $h))) ((Get-HostNow).AddMinutes(5)) -WatchVMConnect | Out-Null
        }
        if (Test-Path -LiteralPath $hold) { Remove-Item -LiteralPath $hold -Recurse -Force }
        Set-CheckStep 5; Write-CheckProgress $id 5
        # Steps 6 and 7, offline.
        Invoke-FlareCall $RunFlareStep @($FlareScript, "Install", $null, $null) ((Get-HostNow).AddMinutes(20)) -WatchVMConnect | Out-Null
        Set-CheckStep 6; Write-CheckProgress $id 6
        Invoke-FlareCall $RunFlareStep @($FlareScript, "Run", $null, $null) ((Get-HostNow).AddMinutes(60)) -WatchVMConnect | Out-Null
        Set-CheckStep 7; Write-CheckProgress $id 7
        # Step 8: staged under the copy rules.
        $deadline = (Get-HostNow).AddMinutes(5)
        Invoke-FlareCall $RunFlareStep @($FlareScript, "Stage", $null, $null) $deadline -WatchVMConnect | Out-Null
        $list = @((Invoke-FlareCall $ListResults @() $deadline -WatchVMConnect).output)
        $files = @(for ($i = 0; $i + 2 -lt $list.Count; $i += 3) { [pscustomobject]@{ rel = $list[$i]; reparse = ($list[$i + 1] -eq "True"); bytes = $list[$i + 2] } })
        $staged = Get-StagingPath $id
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Recurse -Force }
        $copy = Copy-FlareResults $files $staged
        if (-not $copy.ok) { throw (New-CheckStop "no-scenarios" "the results broke the copy rules: $($copy.why)") }
        $result = ConvertFrom-StagedResult $id $staged
        Set-CheckStep 8; Write-CheckProgress $id 8
    }
    catch {
        $kind = $_.Exception.Data["kind"]
        if (-not $kind) { $kind = "no-start"; Write-HostLog "The Flare check $id stopped: $($_.Exception.Message)" }
        $result = [pscustomobject]@{ check = $id; outcome = "vm"; vmKind = $kind }
        $staged = Get-StagingPath $id
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if (-not $restored) { Send-CheckResult $id $result $true; return $result }
    # Step 9, then step 10 only after a good reset.
    if (-not (Invoke-CheckReset $id)) { return $result }
    Send-CheckResult $id $result $false
    $result
}

# ==== The state re-read (section 5) ===================================================================================
# Once a day while the state is one a later read could clear: the check's own steps with no request and no patch.
function Invoke-StateReRead {
    if (-not (Enter-CheckLock "re-read")) { return "locked" }
    try {
        if ((Test-FailedResetMarker) -or (Test-Path -LiteralPath (Get-FlagPath $FlareVMName))) { Exit-CheckLock; return "stopped" }
        $busy = Wait-FlareFree
        if ($busy) { Exit-CheckLock; return "skipped" }   # in use, or a runner still busy: the next tick tries again
        Start-FlareCheckBase
        Read-FlareLicenseState ((Get-HostNow).AddMinutes(30)) | Out-Null
    }
    catch { Write-HostLog "The state re-read stopped: $($_.Exception.Message)" }
    $file = Read-HostJson (Get-TrialPath)
    if ($file) { $file | Add-Member -Force reReadDay ((Get-HostNow).ToString("yyyy-MM-dd")); Write-HostJson (Get-TrialPath) $file }
    if (-not (Invoke-CheckReset "re-read")) { return "failed-reset" }
    if ($script:Deliver) { Write-AgentsVM "flare-inbox\trial.json" (Get-InboxTrialBytes) | Out-Null }
    "re-read"
}

# ==== The tick (section 5, "Tick order") =============================================================================
function Invoke-HandoffTick {
    $acl = @(Test-HostStateAcl)
    if ($acl.Count) { Write-HostLog "The Flare check didn't run, because the host state folders' owner or ACL is wrong: $($acl[0])."; return "acl" }
    if (-not $ScratchVM) {
        # 1. Stale maintenance flags.
        $f = Get-MaintenanceFlag $FlareVMName
        if ($f -and (Test-FlagStale $f)) { Clear-MaintenanceFlag $FlareVMName; Write-HostLog "A maintenance flag was left by a reset that stopped early, so it was removed. Check the reset's log for why it stopped." }
        $a = Get-MaintenanceFlag $AgentsVMName
        if ($a -and (Test-FlagStale $a)) { Start-ResumeTask; Write-HostLog "The agents VM's reset stopped early, so it's being finished now. Factory runs wait until it's done." }
        # 2. The daily trial check, whether or not a check is due.
        Invoke-DailyTrialCheck (Get-HostNow) | Out-Null
        # 3. Held writes, once the agents VM's flag is clear.
        if (-not (Test-Path -LiteralPath (Get-FlagPath $AgentsVMName))) { Send-HeldWrites }
    }
    # 4. Stop, or go on.
    if ((Test-FailedResetMarker) -or (Test-Path -LiteralPath (Get-FlagPath $FlareVMName))) { return "stopped" }
    # 5. A stale check lock: step 9 under its rules, then step 10 for a check.
    $lock = Get-CheckLock
    if ($lock -and (Test-CheckLockStale $lock (Get-HostNow))) {
        $start = Get-ProcessStartTime ([int]$lock.pid)
        if ($start -and $start -eq $lock.start) { Stop-Process -Id ([int]$lock.pid) -Force -ErrorAction SilentlyContinue }
        # A lock still marked "claiming" (or a scratch check's) never touched the Flare VM and has no check to answer:
        # it's released, with no reset and nothing delivered.
        if (@("claiming", "scratch") -contains $lock.check) { Exit-CheckLock; Write-HostLog "A Flare check stopped before it started, so its lock was released."; return "recovered" }
        if ($lock.check -eq "re-read") { Invoke-CheckReset "re-read" | Out-Null; return "recovered" }
        if ([int]$lock.step -lt 9) { if (Invoke-CheckReset $lock.check) { Send-CheckResult $lock.check ([pscustomobject]@{ check = $lock.check; outcome = "vm"; vmKind = "interrupted" }) $false } }
        else { Send-CheckResult $lock.check ([pscustomobject]@{ check = $lock.check; outcome = "vm"; vmKind = "interrupted" }) $true }
        return "recovered"
    }
    if ($lock) { return "busy" }
    # 6. A due state re-read ends the tick.
    if (-not $ScratchVM -and (Test-ReReadDue (Get-HostNow))) { return (Invoke-StateReRead) }   # a scratch copy is never re-read
    # 7. A new request, only while the trial state is active or licensed.
    $t = Get-TrialState
    if ($ScratchVM) {
        if (Test-Path -LiteralPath (Get-TrialPath)) { if (@("active", "licensed") -notcontains $t.state) { return "no-claim" } }
        if (-not (Enter-CheckLock "scratch")) { return "locked" }
        $r = Invoke-ScratchCheck
        return $r
    }
    if (@("active", "licensed") -notcontains $t.state) { return "no-claim" }
    if (-not (Enter-CheckLock "claiming")) { return "locked" }
    if ((Test-FailedResetMarker) -or (Test-Path -LiteralPath (Get-FlagPath $FlareVMName))) { Exit-CheckLock; return "stopped" }
    $claim = Write-AgentsVM -Claim
    if (-not $claim.claimed) { Exit-CheckLock; return "no-request" }
    $l = Get-CheckLock; $l.check = $claim.id; Write-HostJson (Get-LockPath) $l
    $r = Invoke-CheckRun $claim
    "checked:$($r.outcome)$(if ($r.vmKind) { "/$($r.vmKind)" })"
}
# E13's check on a scratch copy: steps 3 and 4.1 and the scratch's own step 9, with its result kept in vm\scratch.
function Invoke-ScratchCheck {
    $result = $null
    try {
        Start-FlareCheckBase
        $t = Read-FlareLicenseState ((Get-HostNow).AddMinutes(30))
        $result = if ($t.state -eq "expired") { [pscustomobject]@{ check = "scratch"; outcome = "vm"; vmKind = "trial-expired" } }
                  elseif ($t.state -eq "unknown") { [pscustomobject]@{ check = "scratch"; outcome = "vm"; vmKind = "trial-unknown" } }
                  else { [pscustomobject]@{ check = "scratch"; outcome = "pass" } }
    }
    catch { $result = [pscustomobject]@{ check = "scratch"; outcome = "vm"; vmKind = $(if ($_.Exception.Data["kind"]) { $_.Exception.Data["kind"] } else { "no-start" }) } }
    try { Stop-VMHard $script:CheckVM } catch { }
    Write-HostJson (Get-HostPath "scratch\result.json") $result
    Exit-CheckLock
    "scratch:$($result.outcome)$(if ($result.vmKind) { "/$($result.vmKind)" })"
}

if (-not $script:Dot) {
    try { $r = Invoke-HandoffTick; Write-Host $r }
    catch { Write-HostLog "The Flare check task failed: $($_.Exception.Message)"; throw }
}
