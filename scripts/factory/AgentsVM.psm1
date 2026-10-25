# The agents VM's own setup pieces (factory-runtime spec sections 2 and 8): the write scan (Get-UserWritable) and its
# deny pass, the grant list, the event log restriction, and the agent user's profile. Runs inside the agents VM, as
# the script user, elevated: at setup (Install-FactoryTools.ps1 -NoFlare), at each -Refresh, and as a quick scan at
# the start of every run. Windows PowerShell 5.1.

$AgentUser = "hsagent"
$HouseStyleRoot = "C:\HouseStyle"

# ==== Rights =========================================================================================================
# Access mask bits that let an account change an object, or change who may: per kind, then the ones every kind has.
$WriteBits = @{
    file    = [ordered]@{ "write data" = 0x2; "append data" = 0x4; "write extended attributes" = 0x10; "write attributes" = 0x100 }
    dir     = [ordered]@{ "add file" = 0x2; "add folder" = 0x4; "write extended attributes" = 0x10; "delete child" = 0x40; "write attributes" = 0x100 }
    key     = [ordered]@{ "set value" = 0x2; "create subkey" = 0x4; "create link" = 0x20 }
    service = [ordered]@{ "change config" = 0x2 }
}
$CommonWriteBits = [ordered]@{ "delete" = 0x10000; "write DAC" = 0x40000; "write owner" = 0x80000 }
# What GENERIC_WRITE and GENERIC_ALL mean for each kind.
$GenericMap = @{
    file = @{ write = 0x120116; all = 0x1F01FF }; dir = @{ write = 0x120116; all = 0x1F01FF }
    key = @{ write = 0x20006; all = 0xF003F }; service = @{ write = 0x20002; all = 0xF01FF }
}
function Expand-GenericMask([int64]$mask, [string]$kind) {
    $m = $mask
    if ($mask -band 0x10000000) { $m = $m -bor $GenericMap[$kind].all }     # GENERIC_ALL
    if ($mask -band 0x40000000) { $m = $m -bor $GenericMap[$kind].write }   # GENERIC_WRITE
    $m
}
function Get-WriteNames([int64]$mask, [string]$kind) {
    $names = @()
    foreach ($t in @($WriteBits[$kind], $CommonWriteBits)) { foreach ($k in $t.Keys) { if ($mask -band $t[$k]) { $names += $k } } }
    $names
}

# The SIDs a process of the agent user carries under an S4U or password task: its own, plus the groups every such logon
# gets. ACEs for any of them count.
function Get-AgentSids([string]$sid) {
    @($sid, "S-1-1-0", "S-1-2-0", "S-1-5-3", "S-1-5-11", "S-1-5-15", "S-1-5-32-545", "S-1-5-113")
}

# The write-leading rights $sids hold on one object, from its SDDL, as names; empty when none. The DACL is read in
# order, so an earlier deny wins over a later allow, as Windows reads a canonical ACL. Ownership gives write DAC unless
# an OWNER RIGHTS entry says otherwise. For a folder, "new files get ..." names what its inheritable entries would give
# a file created inside it.
function Get-WriteRights([string]$sddl, [string[]]$sids, [ValidateSet("file", "dir", "key", "service")][string]$kind) {
    $sd = New-Object Security.AccessControl.RawSecurityDescriptor $sddl
    $owner = if ($sd.Owner) { $sd.Owner.Value } else { "" }
    $isOwner = $sids -contains $owner
    $granted = [int64]0; $denied = [int64]0; $fileGranted = [int64]0; $fileDenied = [int64]0; $ownerRights = $false
    foreach ($ace in @($sd.DiscretionaryAcl)) {
        if ($ace -isnot [Security.AccessControl.CommonAce]) { continue }
        $sid = $ace.SecurityIdentifier.Value
        if ($sid -eq "S-1-3-4") { $ownerRights = $true }
        $applies = ($sids -contains $sid) -or ($sid -eq "S-1-3-4" -and $isOwner)
        if (-not $applies) { continue }
        $mask = Expand-GenericMask (([int64]$ace.AccessMask -band [int64]4294967295)) $kind
        $allow = $ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed
        $flags = [int]$ace.AceFlags
        if (-not ($flags -band [int][Security.AccessControl.AceFlags]::InheritOnly)) {
            if ($allow) { $granted = $granted -bor ($mask -band -bnot $denied) } else { $denied = $denied -bor ($mask -band -bnot $granted) }
        }
        if ($kind -eq "dir" -and ($flags -band [int][Security.AccessControl.AceFlags]::ObjectInherit)) {
            $fm = Expand-GenericMask (([int64]$ace.AccessMask -band [int64]4294967295)) "file"
            if ($allow) { $fileGranted = $fileGranted -bor ($fm -band -bnot $fileDenied) } else { $fileDenied = $fileDenied -bor ($fm -band -bnot $fileGranted) }
        }
    }
    $names = @(Get-WriteNames $granted $kind)
    if ($isOwner) { $names += "owner"; if (-not $ownerRights -and $names -notcontains "write DAC") { $names += "write DAC" } }
    if ($kind -eq "dir") { foreach ($n in (Get-WriteNames $fileGranted "file")) { $names += "new files get $n" } }
    @($names | Select-Object -Unique)
}

# ==== The grant list =================================================================================================
# The only places the agent user may write: its profile, agent-work, the current run's cache during the restore job,
# and delete on its own signin.txt in a job folder.
function Get-GrantList([string]$profile = "C:\Users\$AgentUser", [string]$restoreCache) {
    $g = @(
        [pscustomobject]@{ path = $profile; rights = "*" },
        [pscustomobject]@{ path = (Join-Path $HouseStyleRoot "agent-work"); rights = "*" },
        [pscustomobject]@{ path = (Join-Path $HouseStyleRoot "agent-jobs\*\signin.txt"); rights = "delete" }
    )
    if ($restoreCache) { $g += [pscustomobject]@{ path = $restoreCache; rights = "*" } }
    $g
}
# True when a finding is one the grant list allows.
function Test-Granted($finding, $grants) {
    foreach ($g in @($grants)) {
        $isUnder = if ($g.path.Contains("*")) { $finding.path -like $g.path } else { $finding.path -eq $g.path -or $finding.path.StartsWith($g.path.TrimEnd('\') + "\", [StringComparison]::OrdinalIgnoreCase) }
        if (-not $isUnder) { continue }
        if ($g.rights -eq "*") { return $true }
        if (-not @($finding.rights | Where-Object { $_ -ne $g.rights }).Count) { return $true }
    }
    $false
}

# ==== Where to look ==================================================================================================
function Get-FixedVolumeRoots { @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq "Fixed" -and $_.IsReady } | ForEach-Object { $_.RootDirectory.FullName }) }
# Loaded user hives other than the agent user's own (and its _Classes hive).
function Get-OtherHives([string[]]$loaded, [string]$agentSid) { @($loaded | Where-Object { $_ -ne $agentSid -and $_ -ne "${agentSid}_Classes" }) }

# Every folder (and with -Files every file) under $root, never through a reparse point. Unreadable ones are skipped.
function Get-FileSystemObjects([string]$root, [switch]$Files, [int]$maxDepth = [int]::MaxValue) {
    $stack = New-Object System.Collections.Stack
    $stack.Push(@((New-Object IO.DirectoryInfo $root), 0))
    while ($stack.Count) {
        $item = $stack.Pop(); $d = $item[0]; $depth = $item[1]
        $d
        if ($depth -ge $maxDepth) { continue }
        try { $children = @($d.EnumerateFileSystemInfos()) } catch { continue }
        foreach ($c in $children) {
            if ($c.Attributes -band [IO.FileAttributes]::ReparsePoint) { $c; continue }   # its own ACL counts; never followed
            if ($c -is [IO.DirectoryInfo]) { $stack.Push(@($c, ($depth + 1))) } elseif ($Files) { $c }
        }
    }
}
function Get-ObjectSddl($fsi) {
    try {
        $sec = if ($fsi -is [IO.DirectoryInfo]) { $fsi.GetAccessControl("Access, Owner") } else { $fsi.GetAccessControl("Access, Owner") }
        $sec.GetSecurityDescriptorSddlForm("Access, Owner")
    } catch { $null }
}
function Get-RegistryKeys($hive, [string]$name, [int]$maxDepth = [int]::MaxValue) {
    $stack = New-Object System.Collections.Stack
    $stack.Push(@($name, 0))
    while ($stack.Count) {
        $item = $stack.Pop(); $path = $item[0]; $depth = $item[1]
        $k = $null
        try { $k = $hive.OpenSubKey($path, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadSubTree, [Security.AccessControl.RegistryRights]"ReadKey") } catch { continue }
        if (-not $k) { continue }
        try {
            $sddl = try { $k.GetAccessControl("Access, Owner").GetSecurityDescriptorSddlForm("Access, Owner") } catch { $null }
            [pscustomobject]@{ path = "$($hive.Name)\$path".TrimEnd('\'); sddl = $sddl }
            if ($depth -lt $maxDepth) { foreach ($s in @(try { $k.GetSubKeyNames() } catch { @() })) { $stack.Push(@($(if ($path) { "$path\$s" } else { $s }), ($depth + 1))) } }
        }
        finally { $k.Close() }
    }
}
# A service's descriptor from sc.exe sdshow: the line that holds the SDDL (read as the sc-sdshow fixture shows it).
function ConvertFrom-ScSdShow([string]$text) { @(("" + $text) -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^(O:|G:|D:|S:)' })[0] }

# ==== The scan =======================================================================================================
# Every object outside the grant list that the agent user could change, as kind, path and rights. -Quick looks at
# folders three levels down, the services and their keys, and HKLM\SOFTWARE's first level, for the start of each run.
# $roots defaults to every fixed volume; $sids to the agent user's (Get-AgentSids).
function Get-UserWritable {
    param([string]$Sid, [string[]]$Sids, [string[]]$Roots, [switch]$Quick, [switch]$NoRegistry, [switch]$NoServices, $Grants = (Get-GrantList))
    if (-not $Sids) { $Sids = Get-AgentSids $Sid }
    if (-not $Roots) { $Roots = Get-FixedVolumeRoots }
    $depth = if ($Quick) { 3 } else { [int]::MaxValue }
    $out = New-Object System.Collections.Generic.List[object]
    function Add-Finding($kind, $path, $rights) {
        $f = [pscustomobject]@{ kind = $kind; path = $path; rights = @($rights) }
        if (-not (Test-Granted $f $Grants)) { $out.Add($f) }
    }
    foreach ($r in $Roots) {
        foreach ($o in (Get-FileSystemObjects $r -Files:(-not $Quick) -maxDepth $depth)) {
            $sddl = Get-ObjectSddl $o
            if (-not $sddl) { continue }
            $kind = if ($o -is [IO.DirectoryInfo] -and -not ($o.Attributes -band [IO.FileAttributes]::ReparsePoint)) { "dir" } else { "file" }
            $w = @(Get-WriteRights $sddl $Sids $kind)
            if ($w.Count) { Add-Finding $kind $o.FullName $w }
        }
    }
    if (-not $NoRegistry) {
        $hives = @(@{ hive = [Microsoft.Win32.Registry]::LocalMachine; name = $(if ($Quick) { "SYSTEM\CurrentControlSet\Services" } else { "" }); depth = $(if ($Quick) { 1 } else { [int]::MaxValue }) })
        if ($Quick) { $hives += @{ hive = [Microsoft.Win32.Registry]::LocalMachine; name = "SOFTWARE"; depth = 1 } }
        else { foreach ($h in (Get-OtherHives ([Microsoft.Win32.Registry]::Users.GetSubKeyNames()) $Sid)) { $hives += @{ hive = [Microsoft.Win32.Registry]::Users; name = $h; depth = [int]::MaxValue } } }
        foreach ($h in $hives) {
            foreach ($k in (Get-RegistryKeys $h.hive $h.name $h.depth)) {
                if (-not $k.sddl) { continue }
                $w = @(Get-WriteRights $k.sddl $Sids "key")
                if ($w.Count) { Add-Finding "key" $k.path $w }
            }
        }
    }
    if (-not $NoServices) {
        foreach ($s in @(Get-Service -ErrorAction SilentlyContinue | ForEach-Object Name)) {
            $sddl = ConvertFrom-ScSdShow ((& sc.exe sdshow $s 2>$null) -join "`n")
            if (-not $sddl) { continue }
            $w = @(Get-WriteRights $sddl $Sids "service")
            if ($w.Count) { Add-Finding "service" $s $w }
        }
    }
    $out.ToArray()
}

# ==== The deny pass ==================================================================================================
# For each finding: a deny-write entry for the agent user alone (this object only, so the grant list beneath stays
# writable), or, where it owns the object, ownership to Administrators with an OWNER RIGHTS entry that reads only, or
# for a service a descriptor without the entries that let it write.
function Add-DenyWrite($finding, [string]$sid) {
    $who = New-Object Security.Principal.SecurityIdentifier $sid
    switch ($finding.kind) {
        { $_ -in "file", "dir" } {
            # Access and owner only: Get-Acl would read the audit list too, which needs a privilege the scan doesn't use.
            $fsi = if ($finding.kind -eq "dir") { New-Object IO.DirectoryInfo $finding.path } else { New-Object IO.FileInfo $finding.path }
            $acl = $fsi.GetAccessControl("Access, Owner")
            if (@($finding.rights) -contains "owner") {
                $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier "S-1-5-32-544"))
                $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier "S-1-3-4"), "ReadAndExecute", "None", "None", "Allow")))
            }
            $rights = [Security.AccessControl.FileSystemRights]"WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, ChangePermissions, TakeOwnership"
            if ($finding.kind -eq "dir") { $rights = $rights -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles }
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, $rights, "None", "None", "Deny")))
            if (@($finding.rights | Where-Object { $_ -like "new files get *" }).Count) {
                # Files made inside later would inherit write: deny that too, for files only.
                $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, [Security.AccessControl.FileSystemRights]"WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, ChangePermissions, TakeOwnership", "ObjectInherit", "InheritOnly", "Deny")))
            }
            $fsi.SetAccessControl($acl)
        }
        "key" {
            $hiveName, $sub = $finding.path -split '\\', 2
            $hive = switch -Regex ($hiveName) { '^HKEY_LOCAL_MACHINE$' { [Microsoft.Win32.Registry]::LocalMachine } '^HKEY_USERS$' { [Microsoft.Win32.Registry]::Users } '^HKEY_CURRENT_USER$' { [Microsoft.Win32.Registry]::CurrentUser } default { throw "Unknown hive $hiveName." } }
            $k = $hive.OpenSubKey($sub, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [Security.AccessControl.RegistryRights]"ReadPermissions, ChangePermissions, TakeOwnership")
            try {
                $acl = $k.GetAccessControl("Access, Owner")
                if (@($finding.rights) -contains "owner") { $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier "S-1-5-32-544")) }
                $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule($who, [Security.AccessControl.RegistryRights]"SetValue, CreateSubKey, CreateLink, Delete, ChangePermissions, TakeOwnership", "None", "None", "Deny")))
                $k.SetAccessControl($acl)
            }
            finally { $k.Close() }
        }
        "service" {
            $sddl = ConvertFrom-ScSdShow ((& sc.exe sdshow $finding.path 2>$null) -join "`n")
            $new = Get-TightenedSddl $sddl (Get-AgentSids $sid) "service"
            & sc.exe sdset $finding.path $new | Out-Null
            if ($LASTEXITCODE) { throw "Couldn't tighten the service $($finding.path)." }
        }
    }
}
# $sddl without the allow entries that give any of $sids a write-leading right on this kind of object.
function Get-TightenedSddl([string]$sddl, [string[]]$sids, [string]$kind) {
    $sd = New-Object Security.AccessControl.RawSecurityDescriptor $sddl
    $acl = $sd.DiscretionaryAcl
    for ($i = $acl.Count - 1; $i -ge 0; $i--) {
        $ace = $acl[$i]
        if ($ace -isnot [Security.AccessControl.CommonAce] -or $ace.AceQualifier -ne [Security.AccessControl.AceQualifier]::AccessAllowed) { continue }
        if ($sids -notcontains $ace.SecurityIdentifier.Value) { continue }
        if (@(Get-WriteNames (Expand-GenericMask (([int64]$ace.AccessMask -band [int64]4294967295)) $kind) $kind).Count) { $acl.RemoveAce($i) }
    }
    $sd.GetSddlForm("All")
}

# ==== Event logs =====================================================================================================
# An S4U token carries BATCH, which some channels let read. Those entries, and any for the agent user, come out of the
# channel's access SDDL (wevtutil gl shows it as channelAccess).
$RestrictedChannels = "Microsoft-Windows-PowerShell/Operational", "Windows PowerShell", "Microsoft-Windows-TaskScheduler/Operational", "Application"
function Get-RestrictedChannelSddl([string]$sddl, [string[]]$removeSids) {
    $sd = New-Object Security.AccessControl.RawSecurityDescriptor $sddl
    $acl = $sd.DiscretionaryAcl
    for ($i = $acl.Count - 1; $i -ge 0; $i--) {
        $ace = $acl[$i]
        if ($ace -is [Security.AccessControl.CommonAce] -and $removeSids -contains $ace.SecurityIdentifier.Value -and $ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed) { $acl.RemoveAce($i) }
    }
    $sd.GetSddlForm("All")
}
function ConvertFrom-WevtutilGl([string]$text) {
    $m = [regex]::Match($text, '(?m)^channelAccess:\s*(\S+)\s*$')
    if ($m.Success) { $m.Groups[1].Value } else { $null }
}
# Takes BATCH and the agent user off each channel's readers. Returns each channel's text before, for the fixtures.
function Set-EventLogRestriction([string]$agentSid) {
    foreach ($ch in $RestrictedChannels) {
        $before = (& wevtutil.exe gl $ch) -join "`n"
        $sddl = ConvertFrom-WevtutilGl $before
        if (-not $sddl) { throw "Couldn't read the access of the $ch log." }
        $new = Get-RestrictedChannelSddl $sddl @("S-1-5-3", $agentSid)
        & wevtutil.exe sl $ch "/ca:$new" | Out-Null
        if ($LASTEXITCODE) { throw "Couldn't restrict the $ch log." }
        $after = ConvertFrom-WevtutilGl ((& wevtutil.exe gl $ch) -join "`n")
        if ($after -ne $new) { throw "The $ch log's access didn't change." }
        [pscustomobject]@{ channel = $ch; exit = 0; stdout = $before }
    }
}
# What's wrong with the restriction now, as a list.
function Test-EventLogRestriction([string]$agentSid) {
    @(foreach ($ch in $RestrictedChannels) {
        $sddl = ConvertFrom-WevtutilGl ((& wevtutil.exe gl $ch) -join "`n")
        if (-not $sddl) { "the $ch log's access can't be read"; continue }
        $w = @((New-Object Security.AccessControl.RawSecurityDescriptor $sddl).DiscretionaryAcl | Where-Object { $_ -is [Security.AccessControl.CommonAce] -and $_.AceQualifier -eq "AccessAllowed" -and @("S-1-5-3", $agentSid) -contains $_.SecurityIdentifier.Value })
        if ($w.Count) { "the $ch log still lets BATCH or the agent user read" }
    })
}

# ==== The agent user's profile =======================================================================================
# Unloads the agent user's hive, waiting up to 60 seconds, then stopping what still holds it and trying once more.
function Dismount-AgentHive([string]$sid, [int]$waitSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($waitSeconds)
    while ($true) {
        if (-not (Test-Path "Registry::HKEY_USERS\$sid")) { return $true }
        & reg.exe unload "HKU\$sid" 2>$null | Out-Null
        if (-not (Test-Path "Registry::HKEY_USERS\$sid")) { return $true }
        if ((Get-Date) -gt $deadline) { break }
        Start-Sleep 5
    }
    # Forced: every process the agent user still runs, then one more try.
    Stop-AgentProcesses $sid | Out-Null
    [GC]::Collect()
    & reg.exe unload "HKU\$sid" 2>$null | Out-Null
    -not (Test-Path "Registry::HKEY_USERS\$sid")
}
# Stops every process the agent user owns. Returns how many were left afterwards.
function Stop-AgentProcesses([string]$sid) {
    foreach ($p in @(Get-AgentProcesses $sid)) { try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch { } }
    Start-Sleep 1
    @(Get-AgentProcesses $sid).Count
}
# The agent user's processes, from each process's owner (read as the get-process-owner fixture shows it).
function Get-AgentProcesses([string]$sid) {
    $name = (New-Object Security.Principal.SecurityIdentifier $sid).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
    @(Get-CimInstance Win32_Process | Where-Object {
        $o = $_ | Invoke-CimMethod -MethodName GetOwner -ErrorAction SilentlyContinue
        $o -and $o.ReturnValue -eq 0 -and $o.User -eq $name
    })
}
# The profile template holds the agent user's profile with its owners and ACLs, which would leave it writable for the
# agent user. Its root gets an ACL that every file below inherits: Administrators, SYSTEM and the script user, an OWNER
# RIGHTS entry that reads only (so the agent user owning a file gives it nothing), and a deny for the agent user. The
# files keep their owners, so a restore puts them back, and Restore-AgentProfile hands the agent user its rights again
# from the live profile's own root.
function Protect-ProfileTemplate([string]$template, [string]$agentSid, [string]$scriptSid) {
    & icacls.exe $template /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*${scriptSid}:(OI)(CI)F" "*S-1-3-4:(OI)(CI)RX" /C /Q | Out-Null
    & icacls.exe $template /deny "*${agentSid}:(OI)(CI)F" /C /Q | Out-Null
    & icacls.exe $template /setowner "*S-1-5-32-544" /C /Q | Out-Null
}
# Puts the profile back from the template (robocopy /MIR /COPYALL /B /XJ), then resets the profile root's ACL so every
# file inherits the agent user's own rights again instead of the template's. Returns robocopy's exit code.
function Restore-AgentProfile([string]$template, [string]$profilePath, [string]$agentSid) {
    $code = Copy-Profile $template $profilePath
    & icacls.exe $profilePath /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*${agentSid}:(OI)(CI)F" /C /Q | Out-Null
    if ($LASTEXITCODE) { throw "Couldn't reset the agent user's profile ACL." }
    & icacls.exe $profilePath /setowner "*$agentSid" /C /Q | Out-Null
    $code
}

# Copies a profile with robocopy /MIR /COPYALL /B /XJ, which keeps owners and ACLs and never follows a junction.
# Exit codes 0 to 7 are success (the robocopy-exit-codes fixture).
function Copy-Profile([string]$from, [string]$to) {
    & robocopy.exe $from $to /MIR /COPYALL /B /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $code = $LASTEXITCODE
    if ($code -gt 7) { throw "Copying the profile failed (robocopy $code)." }
    $code
}

Export-ModuleMember -Function Expand-GenericMask, Get-WriteNames, Get-AgentSids, Get-WriteRights, Get-GrantList, Test-Granted, Get-FixedVolumeRoots, Get-OtherHives,
    Get-FileSystemObjects, Get-ObjectSddl, Get-RegistryKeys, ConvertFrom-ScSdShow, Get-UserWritable, Add-DenyWrite, Get-TightenedSddl, Get-RestrictedChannelSddl,
    ConvertFrom-WevtutilGl, Set-EventLogRestriction, Test-EventLogRestriction, Dismount-AgentHive, Stop-AgentProcesses, Get-AgentProcesses, Copy-Profile,
    Protect-ProfileTemplate, Restore-AgentProfile -Variable AgentUser, HouseStyleRoot, RestrictedChannels
