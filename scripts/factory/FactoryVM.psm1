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
# The mutex, held, or $null when it couldn't be had in time.
function Enter-AgentsWriteLock([int]$timeoutSeconds = 300) {
    $m = New-Object System.Threading.Mutex($false, $script:LockName)
    $got = $false
    try { $got = $m.WaitOne([TimeSpan]::FromSeconds($timeoutSeconds)) }
    catch [System.Threading.AbandonedMutexException] { $got = $true }
    if ($got) { return $m }
    $m.Dispose(); $null
}
function Exit-AgentsWriteLock($m) {
    if (-not $m) { return }
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
    foreach ($p in @($root, (Join-Path $root "bin"))) {
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

Export-ModuleMember -Function Set-FactoryVMRoot, Get-FactoryVMRoot, Get-VMCredentialPath, New-VMPassword, Get-VMCredential, Get-FlagPath,
    Get-ProcessStartTime, Get-MaintenanceFlag, Set-MaintenanceFlag, Update-MaintenanceStep, Test-FlagStale, Test-FlagMine, Clear-MaintenanceFlag,
    Set-AgentsWriteLockName, Enter-AgentsWriteLock, Exit-AgentsWriteLock, Protect-AdminOnly, Set-HostStateAcl, Test-HostStateAcl, Assert-NoReparse -Variable AgentsWriteLockName, AdminOnlyNames
