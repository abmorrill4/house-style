# Pester 3 tests for the agents VM's write scan, Get-UserWritable (factory-runtime spec section 8; P17): files, folders,
# registry keys, hives, services and volumes. A made-up SID stands in for the agent user, so nothing here needs the VM.
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $root 'scripts\factory\AgentsVM.psm1') -Force

$agent = "S-1-5-21-111-222-333-4001"
$sids = @(Get-AgentSids $agent)
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

Describe 'Get-WriteRights on descriptors (P17)' {
    It 'finds direct write on a file, and nothing for read only' {
        Get-WriteRights "O:BAD:(A;;FA;;;BA)(A;;0x120116;;;$agent)" $sids file | Should Be @("write data", "append data", "write extended attributes", "write attributes")
        @(Get-WriteRights "O:BAD:(A;;FA;;;BA)(A;;FR;;;$agent)" $sids file).Count | Should Be 0
    }
    It 'counts the rights that lead to write: write DAC, write owner, delete, and ownership' {
        Get-WriteRights "O:BAD:(A;;0x40000;;;$agent)" $sids file | Should Be "write DAC"
        Get-WriteRights "O:BAD:(A;;0x80000;;;$agent)" $sids file | Should Be "write owner"
        Get-WriteRights "O:BAD:(A;;0x10000;;;$agent)" $sids file | Should Be "delete"
        Get-WriteRights "O:${agent}D:(A;;FA;;;BA)" $sids file | Should Be @("owner", "write DAC")
        Get-WriteRights "O:${agent}D:(A;;FA;;;BA)(A;;FR;;;OW)" $sids file | Should Be "owner"   # an OWNER RIGHTS entry replaces the owner's implicit write DAC
    }
    It 'counts the groups every agent logon carries: Users, Authenticated Users, BATCH, Everyone' {
        foreach ($g in "BU", "AU", "S-1-5-3", "WD") { @(Get-WriteRights "O:BAD:(A;;FA;;;$g)" $sids file).Count | Should BeGreaterThan 0 }
        @(Get-WriteRights "O:BAD:(A;;FA;;;IU)" $sids file).Count | Should Be 0   # INTERACTIVE: an S4U task doesn't have it
    }
    It 'lets an earlier deny win, as a canonical ACL reads' {
        @(Get-WriteRights "O:BAD:(D;;0x120116;;;$agent)(A;;FA;;;BU)" $sids file) -join ',' | Should Be "delete,write DAC,write owner"
    }
    It 'reads generic rights for each kind' {
        @(Get-WriteRights "O:BAD:(A;;GW;;;$agent)" $sids file) -contains "write data" | Should Be $true
        @(Get-WriteRights "O:BAD:(A;;GA;;;$agent)" $sids key) -contains "set value" | Should Be $true
        Get-WriteRights "O:BAD:(A;;GW;;;$agent)" $sids service | Should Be "change config"
    }
    It 'says what a folder''s inheritable entries give a file made inside it' {
        $w = @(Get-WriteRights "O:BAD:(A;;FA;;;BA)(A;OICIIO;0x1301BF;;;AU)" $sids dir)
        $w -contains "new files get write data" | Should Be $true
        $w -contains "add file" | Should Be $false   # inherit only: nothing on the folder itself
        @(Get-WriteRights "O:BAD:(A;;0x4;;;AU)" $sids dir) -join ',' | Should Be "add folder"   # C:\'s create-folders entry
    }
    It 'finds the service right that changes its config, and the key rights' {
        Get-WriteRights "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCLCSWLOCRRC;;;IU)(A;;DCRPWP;;;$agent)" $sids service | Should Be "change config"
        @(Get-WriteRights "D:(A;;CCLCSWLOCRRC;;;AU)" $sids service).Count | Should Be 0
        @(Get-WriteRights "O:BAD:(A;;KW;;;$agent)" $sids key) -contains "create subkey" | Should Be $true
    }
    It 'a tightened descriptor drops only the entries that write' {
        $t = Get-TightenedSddl "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCLCSWLOCRRC;;;BU)(A;;DCRPWP;;;BU)" $sids service
        @(Get-WriteRights $t $sids service).Count | Should Be 0
        $t | Should Match 'CCLCSWLOCRRC;;;BU'
    }
}

Describe 'Get-UserWritable on real objects (P17)' {
    $tree = Join-Path $TestDrive 'vol'
    New-Item -ItemType Directory -Force (Join-Path $tree 'open\deep'), (Join-Path $tree 'closed'), (Join-Path $tree 'grant\job1') | Out-Null
    Set-Content (Join-Path $tree 'closed\file.txt') 'x'
    Set-Content (Join-Path $tree 'open\deep\file.txt') 'x'
    function Set-Sddl($path, $sddl) { $a = Get-Acl -LiteralPath $path; $a.SetSecurityDescriptorSddlForm($sddl); Set-Acl -LiteralPath $path -AclObject $a }
    # The test's own account keeps full control, so TestDrive can be cleaned up.
    Set-Sddl (Join-Path $tree 'open') "D:P(A;OICI;FA;;;$me)(A;OICI;0x1301BF;;;$agent)"
    Set-Sddl (Join-Path $tree 'closed\file.txt') "D:P(A;;FA;;;$me)(A;;0x40000;;;$agent)"
    Set-Sddl (Join-Path $tree 'grant') "D:P(A;OICI;FA;;;$me)(A;OICI;FA;;;$agent)"
    $grants = @([pscustomobject]@{ path = (Join-Path $tree 'grant'); rights = "*" })
    $found = @(Get-UserWritable -Sid $agent -Roots @($tree) -NoRegistry -NoServices -Grants $grants)
    It 'finds folders and files the agent user could change, through inheritance too' {
        @($found.path) -contains (Join-Path $tree 'open') | Should Be $true
        @($found.path) -contains (Join-Path $tree 'open\deep') | Should Be $true
        @($found.path) -contains (Join-Path $tree 'open\deep\file.txt') | Should Be $true
        (@($found | Where-Object path -eq (Join-Path $tree 'closed\file.txt'))[0].rights) -join ',' | Should Be "write DAC"
    }
    It 'leaves out the grant list, and what the agent user can''t change' {
        @($found.path | Where-Object { $_ -like "$tree\grant*" }).Count | Should Be 0
        @($found.path) -contains (Join-Path $tree 'closed') | Should Be $false
    }
    It 'the quick scan looks at folders only' {
        $q = @(Get-UserWritable -Sid $agent -Roots @($tree) -NoRegistry -NoServices -Grants $grants -Quick)
        @($q | Where-Object kind -eq "file").Count | Should Be 0
        @($q.path) -contains (Join-Path $tree 'open') | Should Be $true
    }
    It 'the deny pass leaves nothing writable outside the grant list' {
        foreach ($f in $found) { Add-DenyWrite $f $agent }
        @(Get-UserWritable -Sid $agent -Roots @($tree) -NoRegistry -NoServices -Grants $grants).Count | Should Be 0
        @(Get-UserWritable -Sid $agent -Roots @($tree) -NoRegistry -NoServices -Grants @()).path -contains (Join-Path $tree 'grant') | Should Be $true
    }
    It 'never follows a junction, and reports the junction itself' {
        $j = Join-Path $tree 'link'
        cmd /c mklink /J "$j" "$(Join-Path $tree 'open')" | Out-Null
        $objs = @(Get-FileSystemObjects $tree -Files)
        @($objs | Where-Object { $_.FullName -like "$j\*" }).Count | Should Be 0
        @($objs | Where-Object { $_.FullName -eq $j }).Count | Should Be 1
    }
    It 'finds a registry key the agent user could change' {
        $k = "HKCU:\Software\HouseStyle-WriteScan-$([guid]::NewGuid().ToString('N').Substring(0, 6))"
        New-Item $k -Force | Out-Null
        try {
            $sub = $k -replace '^HKCU:\\', ''; $rk = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($sub, $true); $sec = New-Object Security.AccessControl.RegistrySecurity; $sec.SetSecurityDescriptorSddlForm("D:P(A;CI;KA;;;$me)(A;;KW;;;$agent)", "Access"); $rk.SetAccessControl($sec); $rk.Close()
            $keys = @(Get-RegistryKeys ([Microsoft.Win32.Registry]::CurrentUser) ($k -replace '^HKCU:\\', ''))
            $keys.Count | Should Be 1
            @(Get-WriteRights $keys[0].sddl $sids key) -contains "set value" | Should Be $true
        }
        finally { Remove-Item $k -Recurse -Force }
    }
    It 'scans every loaded hive but the agent user''s own' {
        Get-OtherHives @(".DEFAULT", "S-1-5-18", $agent, "${agent}_Classes", "S-1-5-21-9-9-9-1001") $agent | Should Be @(".DEFAULT", "S-1-5-18", "S-1-5-21-9-9-9-1001")
    }
    It 'looks at every fixed volume by default' {
        $v = @(Get-FixedVolumeRoots)
        $v.Count | Should BeGreaterThan 0
        $v -contains "$($env:SystemDrive)\" | Should Be $true
    }
    It 'a granted delete on signin.txt counts only for delete' {
        $g = @(Get-GrantList "C:\Users\hsagent")
        Test-Granted ([pscustomobject]@{ path = "C:\HouseStyle\agent-jobs\abc\signin.txt"; rights = @("delete") }) $g | Should Be $true
        Test-Granted ([pscustomobject]@{ path = "C:\HouseStyle\agent-jobs\abc\signin.txt"; rights = @("delete", "write data") }) $g | Should Be $false
        Test-Granted ([pscustomobject]@{ path = "C:\HouseStyle\agent-work\x\repo"; rights = @("write data") }) $g | Should Be $true
        Test-Granted ([pscustomobject]@{ path = "C:\HouseStyle\agent-workx"; rights = @("write data") }) $g | Should Be $false
    }
}

Describe 'The event log restriction' {
    It 'takes BATCH and the agent user off a channel''s readers and keeps the rest' {
        $app = "O:BAG:SYD:(A;;0xf0007;;;SY)(A;;0x7;;;BA)(A;;0x7;;;SO)(A;;0x3;;;IU)(A;;0x3;;;SU)(A;;0x3;;;S-1-5-3)(A;;0x3;;;S-1-5-33)(A;;0x1;;;S-1-5-32-573)(A;;0x1;;;$agent)"
        $new = Get-RestrictedChannelSddl $app @("S-1-5-3", $agent)
        $new | Should Not Match 'S-1-5-3\)'
        $new | Should Not Match $agent
        $new | Should Match ';;;WR\)'   # S-1-5-33, written back as its alias
        $new | Should Match ';;;BA\)'
    }
}
