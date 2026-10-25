# What the host's VM scripts share (factory-runtime spec sections 5, 9 and 11): each VM's own credential file, the
# per-VM maintenance flags, the HSAGENTS write lock, and the host state folders' ACLs. Runs on THESEUS, elevated.
# Windows PowerShell 5.1. The VM scripts are New-FactoryVM.ps1, Install-FactoryTools.ps1, Install-Runner.ps1,
# Reset-FactoryVM.ps1, Watch-Factory.ps1, Invoke-FlareHandoff.ps1, Set-FlareInUse.ps1, Save-Fixtures.ps1 and
# Test-FactoryIsolation.ps1.

$script:VmRoot = "E:\HouseStyle\vm"
# Tests point the module at a scratch folder.
function Set-FactoryVMRoot([string]$path) { $script:VmRoot = $path }
function Get-FactoryVMRoot { $script:VmRoot }

# ==== Credentials =====================================================================================================
# One generated password per VM for its "factory" account, kept DPAPI-encrypted under the host state folders, where only
# Administrators can read it. No VM ever gets another VM's credential.
function Get-VMCredentialPath([string]$name) {
    if ($name -notmatch '^[A-Za-z0-9-]{1,15}$') { throw "'$name' isn't a VM name the factory uses." }
    Join-Path $script:VmRoot "$name-credential.xml"
}
function New-VMPassword([int]$length = 24) {
    $chars = [char[]]"ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"
    $b = New-Object byte[] $length
    (New-Object System.Security.Cryptography.RNGCryptoServiceProvider).GetBytes($b)
    -join ($b | ForEach-Object { $chars[$_ % $chars.Length] })
}
# The VM's credential, made once and then reused, so a rebuild keeps it.
function Get-VMCredential([string]$name, [switch]$Create, [string]$user = "factory") {
    $f = Get-VMCredentialPath $name
    if (Test-Path -LiteralPath $f) { return Import-Clixml -LiteralPath $f }
    if (-not $Create) { throw "$name has no credential file yet ($f). New-FactoryVM.ps1 makes it." }
    $cred = New-Object System.Management.Automation.PSCredential($user, (ConvertTo-SecureString (New-VMPassword) -AsPlainText -Force))
    New-Item -ItemType Directory -Force (Split-Path $f) | Out-Null
    $cred | Export-Clixml -LiteralPath $f
    Protect-AdminOnly $f
    $cred
}

# ==== Maintenance flags ===============================================================================================
# vm\maintenance-<VM>, set by a reset while it works on that VM: the setting process's id and start time, and its last
# completed step. A flag whose process is gone, or whose process id now belongs to a process started at another time,
# is stale.
function Get-FlagPath([string]$name) { Join-Path $script:VmRoot "maintenance-$name" }
function Get-ProcessStartTime([int]$processId) {
    $p = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    try { $p.StartTime.ToUniversalTime().ToString("o") } catch { $null }
}
function Get-MaintenanceFlag([string]$name) {
    $f = Get-FlagPath $name
    if (-not (Test-Path -LiteralPath $f)) { return $null }
    try { $o = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json } catch { $o = $null }
    if (-not $o) { $o = [pscustomobject]@{ pid = 0; start = ""; step = "" } }   # unreadable: no process owns it, so it's stale
    $o
}
function Set-MaintenanceFlag([string]$name, [string]$step = "set", [int]$processId = $PID) {
    $o = [ordered]@{ pid = $processId; start = (Get-ProcessStartTime $processId); step = $step; at = (Get-Date).ToUniversalTime().ToString("o") }
    $f = Get-FlagPath $name
    $tmp = "$f.tmp"
    [IO.File]::WriteAllText($tmp, ($o | ConvertTo-Json -Compress))
    Move-Item -LiteralPath $tmp -Destination $f -Force
}
function Update-MaintenanceStep([string]$name, [string]$step) {
    $o = Get-MaintenanceFlag $name
    if (-not $o) { throw "maintenance-$name isn't set." }
    Set-MaintenanceFlag $name $step ([int]$o.pid)
}
function Test-FlagStale($flag) {
    if (-not $flag) { return $false }
    $start = Get-ProcessStartTime ([int]$flag.pid)
    -not $start -or $start -ne $flag.start
}
# True when the flag was set by this process (same id and start time).
function Test-FlagMine($flag, [int]$processId = $PID) {
    $flag -and [int]$flag.pid -eq $processId -and $flag.start -eq (Get-ProcessStartTime $processId)
}
function Clear-MaintenanceFlag([string]$name) { Remove-Item -LiteralPath (Get-FlagPath $name) -Force -ErrorAction SilentlyContinue }

# ==== The HSAGENTS write lock =========================================================================================
# A named mutex. Windows releases it when the holding process exits, so a killed task can't leave it held (the next
# waiter gets it as abandoned, which still counts as holding it). Callers wait at most 5 minutes.
$AgentsWriteLockName = "Global\HouseStyle-HSAGENTS-write"
$script:LockName = $AgentsWriteLockName
function Set-AgentsWriteLockName([string]$name) { $script:LockName = $name }   # tests use their own
$script:LockWait = 300
function Set-AgentsWriteLockWait([int]$seconds) { $script:LockWait = $seconds }   # tests wait less than 5 minutes
# The mutex, held, or $null when it couldn't be had in time.
function Enter-AgentsWriteLock([int]$timeoutSeconds = -1) {
    if ($timeoutSeconds -lt 0) { $timeoutSeconds = $script:LockWait }
    $m = New-Object System.Threading.Mutex($false, $script:LockName)
    $got = $false
    try { $got = $m.WaitOne([TimeSpan]::FromSeconds($timeoutSeconds)) }
    catch [System.Threading.AbandonedMutexException] { $got = $true }
    if ($got) { $script:LockHeld = $m; return $m }
    $m.Dispose(); $null
}
function Exit-AgentsWriteLock($m) {
    if (-not $m) { return }
    if ($script:LockHeld -eq $m) { $script:LockHeld = $null }
    try { $m.ReleaseMutex() } catch { }
    $m.Dispose()
}

# ==== Host state folders ==============================================================================================
# E:\HouseStyle\vm and everything under it. Inheritance off; Administrators and SYSTEM have full control and the owner's
# account reads. The credential files, runner-vault and scratch are Administrators only. Every host task checks the
# owner and ACL of vm and vm\bin before it runs, and refuses a reparse point in any path it writes under them.
$AdminOnlyNames = "runner-vault", "scratch"
function Protect-AdminOnly([string]$path) {
    $isDir = Test-Path -LiteralPath $path -PathType Container
    $acl = if ($isDir) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
    $acl.SetAccessRuleProtection($true, $false)
    $inh = if ($isDir) { [Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit" } else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($sid in "S-1-5-32-544", "S-1-5-18") {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), "FullControl", $inh, "None", "Allow")))
    }
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier "S-1-5-32-544"))
    Set-Acl -LiteralPath $path -AclObject $acl
}
function Set-HostStateAcl([string]$ownerAccount = "$env:USERDOMAIN\$env:USERNAME") {
    $root = $script:VmRoot
    New-Item -ItemType Directory -Force $root, (Join-Path $root "bin") | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inh = [Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    foreach ($sid in "S-1-5-32-544", "S-1-5-18") { $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), "FullControl", $inh, "None", "Allow"))) }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($ownerAccount, "ReadAndExecute", $inh, "None", "Allow")))
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier "S-1-5-32-544"))
    Set-Acl -LiteralPath $root -AclObject $acl
    foreach ($n in $AdminOnlyNames) { $p = Join-Path $root $n; New-Item -ItemType Directory -Force $p | Out-Null; Protect-AdminOnly $p }
    foreach ($f in @(Get-ChildItem -LiteralPath $root -Filter "*-credential.xml" -File -ErrorAction SilentlyContinue)) { Protect-AdminOnly $f.FullName }
}
# What's wrong with the host state folders' owner and ACL, as a list; empty when they're right. Checks vm and vm\bin,
# and that the Administrators-only folders and credential files give nobody else anything.
function Test-HostStateAcl {
    $problems = @()
    $root = $script:VmRoot
    $admins = "S-1-5-32-544"; $system = "S-1-5-18"
    # vm and vm\bin, the scripts in bin (a rewrite could keep an ACE an older file had), and flare-pending, whose held
    # writes the reset delivers as host-written.
    $bin = Join-Path $root "bin"
    $more = @(@(Get-ChildItem -LiteralPath $bin -File -Force -ErrorAction SilentlyContinue | ForEach-Object FullName) + @(Join-Path $root "flare-pending" | Where-Object { Test-Path -LiteralPath $_ }))
    foreach ($p in @($root, $bin) + $more) {
        if (-not (Test-Path -LiteralPath $p)) { $problems += "$p is missing"; continue }
        $item = Get-Item -LiteralPath $p -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { $problems += "$p is a reparse point"; continue }
        $acl = Get-Acl -LiteralPath $p
        $owner = (New-Object Security.Principal.NTAccount $acl.Owner).Translate([Security.Principal.SecurityIdentifier]).Value
        if (@($admins, $system) -notcontains $owner) { $problems += "$p isn't owned by Administrators" }
        if (-not $acl.AreAccessRulesProtected -and $p -eq $root) { $problems += "$p inherits its ACL" }
        foreach ($r in @($acl.Access | Where-Object { $_.AccessControlType -eq "Allow" })) {
            $sid = try { $r.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { "" + $r.IdentityReference }
            if (@($admins, $system) -contains $sid) { continue }
            $writes = [int]$r.FileSystemRights -band 0xD0156   # write data, append, write EA, delete child, write attributes, delete, write DAC, write owner
            if ($writes) { $problems += "$p lets $($r.IdentityReference) write" }
        }
    }
    foreach ($n in @($AdminOnlyNames) + @(Get-ChildItem -LiteralPath $root -Filter "*-credential.xml" -File -ErrorAction SilentlyContinue | ForEach-Object Name)) {
        $p = Join-Path $root $n
        if (-not (Test-Path -LiteralPath $p)) { continue }
        foreach ($r in @((Get-Acl -LiteralPath $p).Access | Where-Object { $_.AccessControlType -eq "Allow" })) {
            $sid = try { $r.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { "" + $r.IdentityReference }
            if (@($admins, $system) -notcontains $sid) { $problems += "$p isn't Administrators only ($($r.IdentityReference))" }
        }
    }
    $problems
}
# Throws when any part of $path under the host state folders is a reparse point, or $path isn't under them.
function Assert-NoReparse([string]$path) {
    $root = [IO.Path]::GetFullPath($script:VmRoot).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($root + "\", [StringComparison]::OrdinalIgnoreCase) -and $full -ne $root) { throw "$path isn't under the host state folders." }
    $cur = $root
    foreach ($part in @($full.Substring($root.Length).Trim('\').Split('\') | Where-Object { $_ })) {
        $cur = Join-Path $cur $part
        if (-not (Test-Path -LiteralPath $cur)) { break }
        if ((Get-Item -LiteralPath $cur -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$cur is a reparse point, so nothing is written there." }
    }
}

# ==== The host's own files ============================================================================================
$AgentsVMName = "HSAGENTS"; $FlareVMName = "HSFACTORY"
$GuidPattern = '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
# The host log, vm\host.log: one line each, which the owner reads. Never a secret.
function Write-HostLog([string]$text) {
    $p = Join-Path $script:VmRoot "host.log"
    "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')) $text" | Add-Content -LiteralPath $p -Encoding UTF8
}
function Read-HostJson([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { $null }
}
# Written under a .tmp- name and renamed into place, never through a reparse point.
function Write-HostJson([string]$path, $object) {
    Assert-NoReparse $path
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $tmp = Join-Path (Split-Path $path) (".tmp-" + (Split-Path $path -Leaf))
    [IO.File]::WriteAllText($tmp, ($object | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $tmp -Destination $path -Force
}
function Get-HostPath([string]$name) { Join-Path $script:VmRoot $name }

# ==== Calls into a VM (factory-runtime spec section 5) ================================================================
# Each call runs as a background job with a timeout. A call that times out is stopped and removed, its session too,
# and both are checked gone before the caller goes on, so nothing can land afterwards. Only byte arrays and plain
# strings come back from the Flare VM. Returns ok, timedOut, output, error and errorId.
function Start-VMCallJob([string]$vm, [scriptblock]$block, [object[]]$argv) {
    Invoke-Command -VMName $vm -Credential (Get-VMCredential $vm) -ScriptBlock $block -ArgumentList $argv -AsJob
}
function Stop-VMCallJob($job, [string]$vm) {
    try { Stop-Job -Job $job -ErrorAction SilentlyContinue } catch { }
    try { Remove-Job -Job $job -Force -ErrorAction SilentlyContinue } catch { }
    foreach ($s in @(Get-PSSession -ErrorAction SilentlyContinue | Where-Object { "" + $_.VMName -eq $vm })) { Remove-PSSession $s -ErrorAction SilentlyContinue }
    $jobGone = -not (Get-Job -Id $job.Id -ErrorAction SilentlyContinue)
    $sessionGone = -not @(Get-PSSession -ErrorAction SilentlyContinue | Where-Object { "" + $_.VMName -eq $vm }).Count
    $jobGone -and $sessionGone
}
function Invoke-VMCall([string]$vm, [scriptblock]$block, [object[]]$argv = @(), [int]$timeoutSeconds = 60) {
    $r = [pscustomobject]@{ ok = $false; timedOut = $false; output = $null; error = $null; errorId = $null; stopped = $true }
    try { $job = Start-VMCallJob $vm $block $argv }
    catch { $r.error = $_.Exception.Message; $r.errorId = $_.FullyQualifiedErrorId; return $r }
    $done = Wait-Job -Job $job -Timeout $timeoutSeconds
    if (-not $done) {
        $r.timedOut = $true; $r.error = "the call timed out after $timeoutSeconds seconds"
        $r.stopped = Stop-VMCallJob $job $vm
        if (-not $r.stopped) { throw "A call into $vm timed out and couldn't be stopped." }
        return $r
    }
    $errs = $null
    $out = @(Receive-Job -Job $job -ErrorAction SilentlyContinue -ErrorVariable errs)
    $state = "" + $job.State
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    if ($state -ne "Completed" -or @($errs).Count) {
        $e = @($errs | Select-Object -First 1)
        $r.error = if ($e.Count) { "" + $e[0].Exception.Message } else { "the call ended $state" }
        $r.errorId = if ($e.Count) { "" + $e[0].FullyQualifiedErrorId } else { $state }
        return $r
    }
    $r.ok = $true; $r.output = $out
    $r
}
# True when every item is a byte array or a plain string: what the host accepts from the Flare VM.
function Test-PlainOutput($items) {
    foreach ($i in @($items)) { if ($null -ne $i -and $i -isnot [string] -and $i -isnot [byte[]]) { return $false } }
    $true
}

# ---- Hyper-V, one function each, so the host task's steps can be tested without Hyper-V ----------------------------
function Get-VMStateName([string]$vm) { "" + (Get-VM -Name $vm -ErrorAction Stop).State }
function Stop-VMHard([string]$vm) { Stop-VM -Name $vm -TurnOff -Force -ErrorAction Stop }
function Start-VMPlain([string]$vm) { Start-VM -Name $vm -ErrorAction Stop }
function Restore-Checkpoint([string]$vm, [string]$name) { Restore-VMSnapshot -VMName $vm -Name $name -Confirm:$false -ErrorAction Stop }
function Test-Checkpoint([string]$vm, [string]$name) { [bool](Get-VMSnapshot -VMName $vm -Name $name -ErrorAction SilentlyContinue) }
function New-Checkpoint([string]$vm, [string]$name) {
    Get-VMSnapshot -VMName $vm -Name $name -ErrorAction SilentlyContinue | Remove-VMSnapshot
    Checkpoint-VM -Name $vm -SnapshotName $name -ErrorAction Stop
}
function Disconnect-VMNet([string]$vm) { Disconnect-VMNetworkAdapter -VMName $vm -ErrorAction Stop }
# True when any of the VM's adapters is still connected (read as the hyperv-shapes fixture shows them).
function Test-VMNetConnected([string]$vm) { [bool]@(Get-VMNetworkAdapter -VMName $vm | Where-Object { $_.Connected -or $_.SwitchName }).Count }
function Connect-VMNet([string]$vm, [string]$switch = "Default Switch") { Get-VMNetworkAdapter -VMName $vm | Connect-VMNetworkAdapter -SwitchName $switch -ErrorAction Stop }
function Set-EnhancedSession([bool]$on) { Set-VMHost -EnableEnhancedSessionMode $on -ErrorAction Stop }
function Restart-VMGuest([string]$vm) { Restart-VM -Name $vm -Type Reboot -Force -ErrorAction Stop }
# VMConnect open on $vm, from its process on the host (the vmconnect-process fixture).
function Test-VMConnectOpen([string]$vm) {
    [bool]@(Get-CimInstance Win32_Process -Filter "Name='vmconnect.exe'" -ErrorAction SilentlyContinue | Where-Object { ("" + $_.CommandLine) -match "(?i)(^|\s|`")$([regex]::Escape($vm))(`"|\s|$)" }).Count
}
# A runner's state from GitHub with the owner's gh sign-in (the runner-state fixture): idle, busy, offline or unknown.
function Get-RunnerState([string]$name, [string]$repo = "abmorrill4/house-style") {
    $ErrorActionPreference = "Continue"
    $json = (& gh.exe api "repos/$repo/actions/runners" 2>$null) -join "`n"
    if ($LASTEXITCODE) { return "unknown" }
    ConvertFrom-RunnerState $json $name
}
function ConvertFrom-RunnerState([string]$json, [string]$name) {
    $o = try { $json | ConvertFrom-Json } catch { $null }
    $mine = @($o.runners | Where-Object { $_.name -eq $name })
    if ($mine.Count -ne 1) { return "unknown" }
    if ($mine[0].status -ne "online") { "offline" } elseif ($mine[0].busy -eq $true) { "busy" } else { "idle" }
}

# ==== Writes into the agents VM (section 5, "Writes into the agents VM") =============================================
# Every host write into HSAGENTS goes through Write-AgentsVM: the claim rename, progress.json, result.json, the logs,
# trial.json and carry-done. It takes the write lock (5 minutes at most), holds the write on the host while
# maintenance-HSAGENTS exists, writes over PowerShell Direct with a 60-second timeout under a .tmp- name renamed into
# place, and holds any write that failed or timed out. It never retries in a loop, fails a check, adds a strike or turns
# HSAGENTS off. -AsReset is the reset's own mode (see Test-ResetCaller).
$AgentsVMRoot = "C:\HouseStyle"
$AgentsPathPattern = '^(flare-inbox\\[0-9a-f-]{36}\\(logs\\)?[A-Za-z0-9][A-Za-z0-9._-]{0,118}|flare-inbox\\trial\.json|carry-done)$'
$WriteBlock = {
    param([string]$path, [byte[]]$bytes)
    $ErrorActionPreference = "Stop"
    if (-not $path.StartsWith("C:\HouseStyle\", [StringComparison]::OrdinalIgnoreCase) -or $path.Contains("..")) { throw "Not a path the host writes." }
    $dir = Split-Path $path
    New-Item -ItemType Directory -Force $dir | Out-Null
    $tmp = Join-Path $dir (".tmp-" + (Split-Path $path -Leaf))
    [IO.File]::WriteAllBytes($tmp, $bytes)
    Move-Item -LiteralPath $tmp -Destination $path -Force
    "written"
}
# A held write: kept on the host until the agents VM can take it. A check's progress keeps only its newest, and so
# does trial.json. Returns where it went.
function Save-HeldWrite([string]$relPath, [byte[]]$bytes, [string]$checkId) {
    $item = [pscustomobject]@{ path = $relPath; data = [Convert]::ToBase64String($bytes); at = (Get-Date).ToUniversalTime().ToString("o") }
    if ($relPath -eq "flare-inbox\trial.json") { $p = Get-HostPath "trial-pending.json" }
    else {
        if ($checkId -notmatch $GuidPattern) { throw "A held write needs its check's id." }
        $dir = Join-Path (Get-HostPath "flare-pending\$checkId") "held"
        $leaf = Split-Path $relPath -Leaf
        $p = if ($leaf -eq "progress.json") { Join-Path $dir "progress.json" } else { Join-Path $dir ("{0:D20}-{1}.json" -f (Get-Date).ToUniversalTime().Ticks, ($relPath -replace '[\\/]', '_')) }
    }
    Write-HostJson $p $item
    $p
}
# Every held write, oldest first: trial.json, then each pending delivery's in the order they were held.
function Get-HeldWrites {
    $out = @()
    $t = Get-HostPath "trial-pending.json"
    if (Test-Path -LiteralPath $t) { $out += [pscustomobject]@{ file = $t; checkId = $null; item = (Read-HostJson $t) } }
    foreach ($d in @(Get-ChildItem -LiteralPath (Get-HostPath "flare-pending") -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $GuidPattern } | Sort-Object CreationTimeUtc)) {
        foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $d.FullName "held") -File -Filter *.json -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike ".tmp-*" } | Sort-Object Name)) {
            $out += [pscustomobject]@{ file = $f.FullName; checkId = $d.Name; item = (Read-HostJson $f.FullName) }
        }
    }
    @($out | Where-Object { $_.item })
}
# The progress mirror: the host's own copy of a check's latest progress, kept after every step whatever the write did.
function Set-ProgressMirror([string]$checkId, [int]$step) {
    $p = Join-Path (Get-HostPath "flare-pending\$checkId") "latest-progress.json"
    Write-HostJson $p ([pscustomobject]@{ check = $checkId; step = $step; at = (Get-Date).ToUniversalTime().ToString("o") })
}
# -AsReset is accepted only from Reset-FactoryVM.ps1 -Name HSAGENTS holding the write lock, with its process id and
# start time in maintenance-HSAGENTS.
$script:ResetScript = "Reset-FactoryVM.ps1"
function Test-ResetCaller {
    if (-not $script:LockHeld) { return "it doesn't hold the write lock" }
    if (-not (Test-FlagMine (Get-MaintenanceFlag $AgentsVMName))) { return "its process doesn't match maintenance-HSAGENTS" }
    if (-not @(Get-PSCallStack | Where-Object { ("" + $_.ScriptName) -like "*\$($script:ResetScript)" }).Count) { return "it isn't Reset-FactoryVM.ps1" }
    if ($script:ResetName -ne $AgentsVMName) { return "it isn't the agents VM's reset" }
    $null
}
function Set-ResetName([string]$name) { $script:ResetName = $name }
function Write-AgentsVM {
    param([string]$Path, [byte[]]$Bytes, [string]$CheckId, [switch]$AsReset, [switch]$NoHold, [switch]$Claim)
    # The claim is a write too: the rename, under the same lock, with its read-back (Invoke-ClaimLocked).
    if ($Claim) { return (Invoke-ClaimLocked) }
    if ($Path -notmatch $AgentsPathPattern) { throw "Write-AgentsVM doesn't write $Path." }
    if ($Path -eq "carry-done" -and -not $AsReset) { throw "Only the agents VM's reset writes carry-done, through -AsReset." }
    $why = $null; $lock = $null
    if ($AsReset) {
        $refused = Test-ResetCaller
        if ($refused) { Write-HostLog "Write-AgentsVM -AsReset was refused because $refused."; $why = "a caller passed -AsReset that may not" }
    }
    else {
        $lock = Enter-AgentsWriteLock
        if (-not $lock) { $why = "the write lock was busy for 5 minutes" }
        elseif (Test-Path -LiteralPath (Get-FlagPath $AgentsVMName)) { $why = "the agents VM is being reset" }
    }
    try {
        if (-not $why) {
            $r = Invoke-VMCall $AgentsVMName $WriteBlock @((Join-Path $AgentsVMRoot $Path), $Bytes) 60
            if ($r.ok) { return "written" }
            $why = if ($r.timedOut) { "the call timed out" } else { "the call failed ($($r.errorId))" }
        }
        if ($NoHold) { return "failed" }
        Save-HeldWrite $Path $Bytes $CheckId | Out-Null
        Write-HostLog "A write into the agents VM was held because $why. It goes in once the agents VM is ready."
        "held"
    }
    finally { if ($lock) { Exit-AgentsWriteLock $lock } }
}
# A check's progress: the mirror first (always), then progress.json through Write-AgentsVM.
function Write-CheckProgress([string]$checkId, [int]$step) {
    $lock = Enter-AgentsWriteLock   # all writes to flare-pending happen under the write lock
    try { Set-ProgressMirror $checkId $step } finally { Exit-AgentsWriteLock $lock }
    $bytes = [Text.Encoding]::UTF8.GetBytes(([pscustomobject]@{ check = $checkId; step = $step; at = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json -Compress))
    Write-AgentsVM "flare-inbox\$checkId\progress.json" $bytes $checkId | Out-Null
}
# Writes the held writes in, oldest first. With -AsReset (the reset's held-write step) it also writes each pending
# delivery's progress mirror, while the reset holds the lock. A write that fails stays held. The writer of a pending
# delivery's last held write removes its folder.
function Send-HeldWrites([switch]$AsReset) {
    foreach ($h in (Get-HeldWrites)) {
        $bytes = [Convert]::FromBase64String($h.item.data)
        $r = Write-AgentsVM $h.item.path $bytes $h.checkId -AsReset:$AsReset -NoHold
        if ($r -eq "written") { $l = if ($AsReset) { $null } else { Enter-AgentsWriteLock }; try { Remove-Item -LiteralPath $h.file -Force -ErrorAction SilentlyContinue } finally { if ($l) { Exit-AgentsWriteLock $l } } }
    }
    foreach ($d in @(Get-ChildItem -LiteralPath (Get-HostPath "flare-pending") -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $GuidPattern })) {
        $mirror = Join-Path $d.FullName "latest-progress.json"
        if ($AsReset -and (Test-Path -LiteralPath $mirror)) {
            Write-AgentsVM "flare-inbox\$($d.Name)\progress.json" ([IO.File]::ReadAllBytes($mirror)) $d.Name -AsReset -NoHold | Out-Null
        }
        # Every change to flare-pending happens under the write lock. With -AsReset the reset already holds it.
        $l = if ($AsReset) { $null } else { Enter-AgentsWriteLock }
        if (-not $AsReset -and -not $l) { continue }
        try { if (-not @(Get-ChildItem -LiteralPath (Join-Path $d.FullName "held") -File -ErrorAction SilentlyContinue).Count -and -not (Test-CheckRunning $d.Name)) { Remove-Item -LiteralPath $d.FullName -Recurse -Force } }
        finally { if ($l) { Exit-AgentsWriteLock $l } }
    }
}
function Test-CheckRunning([string]$checkId) { $l = Read-HostJson (Get-HostPath $script:LockFile); [bool]($l -and $l.check -eq $checkId) }

# ---- The claim (section 5, "Claiming is a rename") ------------------------------------------------------------------
# Under the write lock for the whole claim: with maintenance-HSAGENTS clear, read the oldest request and its patch from
# the outbox, rename it to .claimed, read the outbox back if the rename failed or timed out, then write the first
# progress.json (held if that fails). Returns claimed, id, request (bytes) and patch (bytes), or why not.
$ClaimReadBlock = {
    $o = "C:\HouseStyle\flare-outbox"
    $d = @(Get-ChildItem -LiteralPath $o -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } | Sort-Object CreationTimeUtc | Select-Object -First 1)
    if (-not $d.Count) { return }
    $d[0].Name
    , [IO.File]::ReadAllBytes((Join-Path $d[0].FullName "request.json"))
    , [IO.File]::ReadAllBytes((Join-Path $d[0].FullName "fix.patch"))
}
$ClaimRenameBlock = { param([string]$id) Rename-Item -LiteralPath "C:\HouseStyle\flare-outbox\$id" -NewName "$id.claimed" -ErrorAction Stop; "renamed" }
$ClaimReadBackBlock = { param([string]$id) if (Test-Path "C:\HouseStyle\flare-outbox\$id.claimed") { "claimed" } elseif (Test-Path "C:\HouseStyle\flare-outbox\$id") { "unclaimed" } else { "gone" } }
function Invoke-ClaimLocked {
    $no = { param($why) [pscustomobject]@{ claimed = $false; why = $why; id = $null; request = $null; patch = $null } }
    $lock = Enter-AgentsWriteLock
    if (-not $lock) { return (& $no "lock") }
    try {
        if (Test-Path -LiteralPath (Get-FlagPath $AgentsVMName)) { return (& $no "maintenance") }
        $read = Invoke-VMCall $AgentsVMName $ClaimReadBlock @() 60
        if (-not $read.ok -or @($read.output).Count -ne 3) { return (& $no "no request") }
        $id = "" + $read.output[0]
        if ($id -notmatch $GuidPattern -or $read.output[1] -isnot [byte[]] -or $read.output[2] -isnot [byte[]]) { return (& $no "not a request") }
        $rn = Invoke-VMCall $AgentsVMName $ClaimRenameBlock @($id) 60
        if (-not $rn.ok) {
            $back = Invoke-VMCall $AgentsVMName $ClaimReadBackBlock @($id) 60
            $state = if ($back.ok) { "" + @($back.output)[0] } else { "" }
            if ($state -ne "claimed") {
                if ($state -ne "unclaimed") { Write-HostLog "The Flare check for $id couldn't confirm its claim on the agents VM, so it didn't start. The factory retries the fix after its timeout." }
                return (& $no "not confirmed")
            }
        }
        # The first progress: held like any progress write when it fails, and the claim goes on.
        Set-ProgressMirror $id 1
        $bytes = [Text.Encoding]::UTF8.GetBytes(([pscustomobject]@{ check = $id; step = 1; at = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json -Compress))
        $pr = Invoke-VMCall $AgentsVMName $WriteBlock @((Join-Path $AgentsVMRoot "flare-inbox\$id\progress.json"), $bytes) 60
        if (-not $pr.ok) { Save-HeldWrite "flare-inbox\$id\progress.json" $bytes $id | Out-Null; Write-HostLog "A write into the agents VM was held because the first progress write failed. It goes in once the agents VM is ready." }
        [pscustomobject]@{ claimed = $true; why = $null; id = $id; request = $read.output[1]; patch = $read.output[2] }
    }
    finally { Exit-AgentsWriteLock $lock }
}

# ==== The check lock, the marker and the in-use flag (section 5) =====================================================
# flare-check.lock: the process id and start time, the check id (or re-read) and the last completed step. Held from the
# claim, or the start of a state re-read, until the Flare VM is back on factory-base. A scratch check (-ScratchVM) uses
# its own lock, staging folder and trial file under vm\scratch.
$script:LockFile = "flare-check.lock"; $script:StagingDir = "flare-staging"; $script:TrialFile = "flare-trial.json"
function Use-ScratchPaths([string]$name) {
    if ($name -notmatch '^HSSCRATCH-[0-9]{1,4}$') { throw "-ScratchVM takes a name like HSSCRATCH-1." }
    $script:LockFile = "scratch\flare-check.lock"; $script:StagingDir = "scratch\flare-staging"; $script:TrialFile = "scratch\flare-trial.json"
}
function Get-CheckLock { Read-HostJson (Get-HostPath $script:LockFile) }
# Takes the lock: a new file only, so two processes can't both have it. Returns whether it was taken.
function Enter-CheckLock([string]$check) {
    $p = Get-HostPath $script:LockFile
    Assert-NoReparse $p
    $o = [pscustomobject]@{ pid = $PID; start = (Get-ProcessStartTime $PID); check = $check; step = 0; at = (Get-Date).ToUniversalTime().ToString("o") }
    try { $fs = [IO.File]::Open($p, "CreateNew", "Write", "None") } catch { return $false }
    try { $b = [Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Compress)); $fs.Write($b, 0, $b.Length) } finally { $fs.Dispose() }
    $true
}
function Set-CheckStep([int]$step) { $l = Get-CheckLock; if ($l) { $l.step = $step; Write-HostJson (Get-HostPath $script:LockFile) $l } }
function Exit-CheckLock { Remove-Item -LiteralPath (Get-HostPath $script:LockFile) -Force -ErrorAction SilentlyContinue }
# Stale: the process is gone (or its id now belongs to a later process), or the lock is older than 200 minutes.
function Test-CheckLockStale($lock, [datetime]$now = (Get-Date).ToUniversalTime()) {
    if (-not $lock) { return $false }
    $start = Get-ProcessStartTime ([int]$lock.pid)
    if (-not $start -or $start -ne $lock.start) { return $true }
    ($now - ([datetime]::Parse($lock.at, [Globalization.CultureInfo]::InvariantCulture, "AdjustToUniversal"))).TotalMinutes -gt 200
}
# The trial and lock files, for the scripts (-ScratchVM points them into vm\scratch).
function Get-TrialPath { Get-HostPath $script:TrialFile }
function Get-LockPath { Get-HostPath $script:LockFile }
function Get-StagingPath([string]$name) { Get-HostPath (Join-Path $script:StagingDir $name) }
function Test-FailedResetMarker { Test-Path -LiteralPath (Get-HostPath "flare-reset-failed") }
function Set-FailedResetMarker { [IO.File]::WriteAllText((Get-HostPath "flare-reset-failed"), (Get-Date).ToUniversalTime().ToString("o")) }
function Test-InUseFlag { Test-Path -LiteralPath (Get-HostPath "flare-in-use") }

# ==== The trial state (section 5, "The trial state") =================================================================
# vm\flare-trial.json: state (active, expired, licensed, unknown), endDate while a trial, source (date or flare) for
# expired, and wasLicensed. Only the host's own reads of Flare set it, before any patch is on the VM.
# The shapes of Flare's licensing window, from the flare-trial-active and flare-trial-expired fixtures (and
# flare-licensed at migration step 18). Each is a pattern over the read's text and the state it means; an active shape
# names its end date. Until the fixtures are captured at migration step 3, there are none, so every read is unknown and
# the fix path stays off: fail closed.
$FlareTrialShapes = @()
function ConvertFrom-FlareTrialRead([string]$text) {
    foreach ($s in $FlareTrialShapes) {
        $m = [regex]::Match(("" + $text), $s.pattern)
        if (-not $m.Success) { continue }
        $end = $null
        if ($s.state -eq "active") {
            $d = [datetime]::MinValue
            if (-not [datetime]::TryParseExact($m.Groups["end"].Value, $s.dateFormat, [Globalization.CultureInfo]::InvariantCulture, "None", [ref]$d)) { continue }
            $end = $d.ToString("yyyy-MM-dd")
        }
        return [pscustomobject]@{ state = $s.state; endDate = $end }
    }
    [pscustomobject]@{ state = "unknown"; endDate = $null }
}
function Get-TrialState { $t = Read-HostJson (Get-HostPath $script:TrialFile); if ($t) { $t } else { [pscustomobject]@{ state = "unknown"; endDate = $null; source = $null; wasLicensed = $false } } }
# A read of Flare's license state: the new state, its date, where an expiry came from, and whether the state before it
# was licensed. A licensed read drops the trial date. The host logs every change, and an unknown shape's redacted text.
function Set-TrialFromRead($read, [string]$text, [datetime]$now = (Get-Date).ToUniversalTime()) {
    $old = Get-TrialState
    $new = [ordered]@{ state = $read.state; endDate = $null; source = $null; wasLicensed = ($old.state -eq "licensed" -or ($old.state -eq "expired" -and $old.wasLicensed -eq $true)) }
    switch ($read.state) {
        "active" {
            $new.endDate = $read.endDate
            if ([datetime]::ParseExact($read.endDate, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture) -lt $now.Date) { $new.state = "expired"; $new.source = "date" }
            $new.wasLicensed = $false
        }
        "expired" { $new.source = "flare"; $new.endDate = $(if ($old.state -ne "licensed") { $old.endDate } else { $null }) }
        "licensed" { $new.wasLicensed = $false }
        default { $new.state = "unknown"; $new.endDate = $old.endDate }
    }
    $new.readAt = $now.ToString("o")
    if ($new.state -ne $old.state -or $new.endDate -ne $old.endDate) {
        $line = "Flare's license state on the Flare VM is now $($new.state)$(if ($new.endDate) { ", ending $($new.endDate)" })$(if ($new.source) { ", from $(if ($new.source -eq 'date') { 'the trial date' } else { "Flare's own report" })" })."
        if ($new.state -eq "unknown") { $line += " The read showed: $(Protect-HostText $text)" }
        Write-HostLog $line
    }
    $prev = Read-HostJson (Get-HostPath $script:TrialFile)
    foreach ($k in "warnedFor", "reReadDay", "dailyCheckDay") { if ($prev -and $prev.$k) { $new[$k] = $prev.$k } }
    Write-HostJson (Get-HostPath $script:TrialFile) ([pscustomobject]$new)
    [pscustomobject]$new
}
# The redacted text of a read the host doesn't recognise: no emails, license keys or long numbers.
function Protect-HostText([string]$t) {
    $t = ("" + $t) -replace '(?i)\b[\w.+-]+@[\w-]+(\.[\w-]+)+', '_email_' -replace '\b[A-Z0-9]{4,6}(-[A-Z0-9]{4,6}){3,}\b', '_license-key_' -replace '\d{6,}', '_number_'
    if ($t.Length -gt 400) { $t = $t.Substring(0, 400) + "..." }
    $t -replace '[\r\n]+', ' '
}
# trial.json as the agents VM gets it: state, endDate, wasLicensed.
function Get-InboxTrialBytes {
    $t = Get-TrialState
    $o = [ordered]@{ state = $t.state }
    if ($t.endDate -and @("active", "expired") -contains $t.state) { $o.endDate = $t.endDate }
    $o.wasLicensed = ($t.wasLicensed -eq $true)
    [Text.Encoding]::UTF8.GetBytes(([pscustomobject]$o | ConvertTo-Json -Compress))
}
# The daily trial check (tick step 2): an active trial whose date has passed is expired (from the date), and one ending
# in seven days or less is warned about once per date. Then trial.json goes to the agents VM. Once a day.
function Invoke-DailyTrialCheck([datetime]$now = (Get-Date).ToUniversalTime()) {
    $t = Get-TrialState
    $day = $now.ToString("yyyy-MM-dd")
    $file = Read-HostJson (Get-HostPath $script:TrialFile)
    if ($file -and $file.dailyCheckDay -eq $day) { return $false }
    if ($t.state -eq "active" -and $t.endDate) {
        $end = [datetime]::ParseExact($t.endDate, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
        if ($end -lt $now.Date) { $t = Set-TrialFromRead ([pscustomobject]@{ state = "active"; endDate = $t.endDate }) "" $now }
        elseif (($end - $now.Date).TotalDays -le 7 -and (-not $file -or $file.warnedFor -ne $t.endDate)) {
            Write-HostLog "Flare's trial on the Flare VM ends on $($t.endDate). Add a Flare license to keep Flare checks running and to let the factory go live."
            $file = Read-HostJson (Get-HostPath $script:TrialFile); $file | Add-Member -Force warnedFor $t.endDate; Write-HostJson (Get-HostPath $script:TrialFile) $file
        }
    }
    $file = Read-HostJson (Get-HostPath $script:TrialFile)
    if (-not $file) { $file = [pscustomobject]@{ state = "unknown"; endDate = $null; source = $null; wasLicensed = $false } }
    $file | Add-Member -Force dailyCheckDay $day; Write-HostJson (Get-HostPath $script:TrialFile) $file
    Write-AgentsVM "flare-inbox\trial.json" (Get-InboxTrialBytes) | Out-Null
    $true
}
# A state re-read is due once a day while the state is one a later read could clear: unknown, expired from Flare with
# a future trial date, or expired from Flare with no date (a lapsed license). Never for expired from the date.
function Test-ReReadDue([datetime]$now = (Get-Date).ToUniversalTime()) {
    $t = Get-TrialState
    $file = Read-HostJson (Get-HostPath $script:TrialFile)
    if ($file -and $file.reReadDay -eq $now.ToString("yyyy-MM-dd")) { return $false }
    if ($t.state -eq "unknown") { return $true }
    if ($t.state -ne "expired" -or $t.source -ne "flare") { return $false }
    if (-not $t.endDate) { return $true }
    [datetime]::ParseExact($t.endDate, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture) -ge $now.Date
}

# ==== The copy rules for the Flare VM (section 5, step 8) ============================================================
# Files in the results folder or one level down, safe names, no reparse points, at most 200 files and 50 MB, written as
# new plain files through \\?\ paths. result.json is refused over 16 KB. $files: rel (folder\name or name), bytes,
# reparse. Returns ok and why.
$DeviceNames = '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$'
function Test-CopyName([string]$name, [switch]$Folder) {
    if (-not $name -or $name -eq "." -or $name -eq ".." -or $name -match $DeviceNames) { return $false }
    if ($Folder) { $name -cmatch '^[A-Za-z0-9]([A-Za-z0-9._-]{0,78}[A-Za-z0-9])?$' } else { $name -cmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\.(png|txt|log|trx)$' -or $name -ceq "result.json" }
}
function Copy-FlareResults($files, [string]$dest) {
    $list = @($files)
    if ($list.Count -gt 200) { return [pscustomobject]@{ ok = $false; why = "more than 200 files" } }
    $total = 0
    foreach ($f in $list) {
        if ($f.reparse) { return [pscustomobject]@{ ok = $false; why = "$($f.rel) is a reparse point" } }
        $parts = @(("" + $f.rel) -split '\\')
        if ($parts.Count -gt 2) { return [pscustomobject]@{ ok = $false; why = "$($f.rel) is too deep" } }
        if ($parts.Count -eq 2 -and -not (Test-CopyName $parts[0] -Folder)) { return [pscustomobject]@{ ok = $false; why = "$($f.rel) has a folder name the host doesn't take" } }
        if (-not (Test-CopyName $parts[-1])) { return [pscustomobject]@{ ok = $false; why = "$($f.rel) has a name the host doesn't take" } }
        if ($parts[-1] -ceq "result.json" -and ($parts.Count -ne 1 -or @($f.bytes).Count -gt 16384)) { return [pscustomobject]@{ ok = $false; why = "result.json is over 16 KB or not at the top" } }
        if ($f.bytes -isnot [byte[]]) { return [pscustomobject]@{ ok = $false; why = "$($f.rel) isn't plain bytes" } }
        $total += $f.bytes.Length
        if ($total -gt 50MB) { return [pscustomobject]@{ ok = $false; why = "more than 50 MB" } }
    }
    Assert-NoReparse $dest
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($f in $list) {
        $target = Join-Path $dest $f.rel
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        $fs = [IO.File]::Open("\\?\" + [IO.Path]::GetFullPath($target), "CreateNew", "Write", "None")
        try { $fs.Write($f.bytes, 0, $f.bytes.Length) } finally { $fs.Dispose() }
    }
    [pscustomobject]@{ ok = $true; why = $null }
}

Export-ModuleMember -Function Get-TrialPath, Get-LockPath, Get-StagingPath, Set-FactoryVMRoot, Get-FactoryVMRoot, Get-VMCredentialPath, New-VMPassword, Get-VMCredential, Get-FlagPath,
    Get-ProcessStartTime, Get-MaintenanceFlag, Set-MaintenanceFlag, Update-MaintenanceStep, Test-FlagStale, Test-FlagMine, Clear-MaintenanceFlag,
    Set-AgentsWriteLockName, Set-AgentsWriteLockWait, Enter-AgentsWriteLock, Exit-AgentsWriteLock, Protect-AdminOnly, Set-HostStateAcl, Test-HostStateAcl, Assert-NoReparse,
    Write-HostLog, Read-HostJson, Write-HostJson, Get-HostPath, Start-VMCallJob, Stop-VMCallJob, Invoke-VMCall, Test-PlainOutput, Get-VMStateName, Stop-VMHard,
    Start-VMPlain, Restore-Checkpoint, Test-Checkpoint, New-Checkpoint, Disconnect-VMNet, Test-VMNetConnected, Connect-VMNet, Set-EnhancedSession, Restart-VMGuest,
    Test-VMConnectOpen, Get-RunnerState, ConvertFrom-RunnerState, Save-HeldWrite, Get-HeldWrites, Set-ProgressMirror, Test-ResetCaller, Set-ResetName,
    Write-AgentsVM, Write-CheckProgress, Send-HeldWrites, Test-CheckRunning, ConvertFrom-FlareTrialRead, Get-TrialState, Set-TrialFromRead,
    Protect-HostText, Get-InboxTrialBytes, Invoke-DailyTrialCheck, Test-ReReadDue, Test-CopyName, Copy-FlareResults, Invoke-ClaimLocked, Use-ScratchPaths,
    Get-CheckLock, Enter-CheckLock, Set-CheckStep, Exit-CheckLock, Test-CheckLockStale, Test-FailedResetMarker, Set-FailedResetMarker, Test-InUseFlag -Variable AgentsWriteLockName,
    AdminOnlyNames, AgentsVMName, FlareVMName, GuidPattern, WriteBlock, FlareTrialShapes
