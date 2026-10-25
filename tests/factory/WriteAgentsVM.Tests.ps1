<#
Pester 3 tests for the host's writes into the agents VM (factory-runtime spec section 5; P16): Write-AgentsVM, its
held writes, the write lock, -AsReset, the claim, and the calls into a VM with their timeouts. The agents VM is a
scripted guest: Invoke-VMCall is mocked inside FactoryVM.psm1 and works on a folder in TestDrive.
  powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
#>
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $root 'scripts\factory\FactoryVM.psm1') -Force
Import-Module (Join-Path $here 'Fixtures.psm1') -Force

function New-HostRoot([string]$name) {
    $vm = Join-Path $TestDrive $name; New-Item -ItemType Directory -Force $vm, (Join-Path $vm "flare-pending") | Out-Null
    Set-FactoryVMRoot $vm
    Set-AgentsWriteLockName ("Local\hs-test-" + [guid]::NewGuid().ToString("N"))
    Set-AgentsWriteLockWait 2
    $global:Guest = @{ root = (Join-Path $TestDrive "$name-guest"); calls = (New-Object System.Collections.ArrayList); mode = "ok" }
    New-Item -ItemType Directory -Force (Join-Path $global:Guest.root "flare-outbox"), (Join-Path $global:Guest.root "flare-inbox") | Out-Null
    $vm
}
# The scripted guest: writes land under $Guest.root; $Guest.mode makes calls fail or time out.
function Use-Guest {
    Mock -ModuleName FactoryVM Invoke-VMCall {
        [void]$global:Guest.calls.Add([pscustomobject]@{ vm = $vm; block = $block.ToString(); argv = $argv })
        $r = [pscustomobject]@{ ok = $false; timedOut = $false; output = $null; error = $null; errorId = $null; stopped = $true }
        if ($global:Guest.mode -eq "timeout") { $r.timedOut = $true; $r.error = "timed out"; return $r }
        if ($global:Guest.mode -eq "fail") { $r.error = "failed"; $r.errorId = "PSDirectVMOff"; return $r }
        $src = $block.ToString()
        if ($src -match 'WriteAllBytes\(\$tmp') {
            $path = ("" + $argv[0]).Replace("C:\HouseStyle", $global:Guest.root)
            New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
            $tmp = Join-Path (Split-Path $path) (".tmp-" + (Split-Path $path -Leaf))
            [IO.File]::WriteAllBytes($tmp, [byte[]]$argv[1]); Move-Item $tmp $path -Force
            $r.ok = $true; $r.output = @("written"); return $r
        }
        $r.ok = $true; $r.output = @(); $r
    }
}

Describe 'Write-AgentsVM (P16)' {
    It 'with maintenance-HSAGENTS set, holds a progress, result or trial.json write on the host and reaches nothing' {
        New-HostRoot 'held' | Out-Null; Use-Guest
        Set-MaintenanceFlag HSAGENTS "set"
        $id = [guid]::NewGuid().ToString()
        foreach ($p in "flare-inbox\$id\progress.json", "flare-inbox\$id\result.json", "flare-inbox\trial.json") { Write-AgentsVM $p ([byte[]](1, 2, 3)) $id | Should Be "held" }
        $global:Guest.calls.Count | Should Be 0
        @(Get-HeldWrites).Count | Should Be 3
        (Get-Content (Get-HostPath "host.log") -Raw) | Should Match ([regex]::Escape("A write into the agents VM was held because the agents VM is being reset. It goes in once the agents VM is ready."))
        Clear-MaintenanceFlag HSAGENTS
    }
    It 'keeps only the newest progress and trial.json' {
        New-HostRoot 'newest' | Out-Null; Use-Guest
        Set-MaintenanceFlag HSAGENTS "set"
        $id = [guid]::NewGuid().ToString()
        foreach ($b in 1..3) { Write-AgentsVM "flare-inbox\$id\progress.json" ([byte[]]@($b)) $id | Out-Null; Write-AgentsVM "flare-inbox\trial.json" ([byte[]]@($b)) | Out-Null }
        $h = @(Get-HeldWrites)
        $h.Count | Should Be 2
        foreach ($x in $h) { [Convert]::FromBase64String($x.item.data)[0] | Should Be 3 }
        Clear-MaintenanceFlag HSAGENTS
    }
    It 'writes under a .tmp- name and renames it into place, so writing twice is safe' {
        New-HostRoot 'tmp' | Out-Null; Use-Guest
        $id = [guid]::NewGuid().ToString()
        Write-AgentsVM "flare-inbox\$id\result.json" ([Text.Encoding]::UTF8.GetBytes('{"a":1}')) $id | Should Be "written"
        Write-AgentsVM "flare-inbox\$id\result.json" ([Text.Encoding]::UTF8.GetBytes('{"a":2}')) $id | Should Be "written"
        Get-Content (Join-Path $global:Guest.root "flare-inbox\$id\result.json") -Raw | Should Be '{"a":2}'
        @(Get-ChildItem (Join-Path $global:Guest.root "flare-inbox\$id") -Filter ".tmp-*").Count | Should Be 0
        $WriteBlock.ToString() | Should Match ([regex]::Escape('$tmp = Join-Path $dir (".tmp-" + (Split-Path $path -Leaf))'))
        $WriteBlock.ToString() | Should Match ([regex]::Escape('Move-Item -LiteralPath $tmp -Destination $path -Force'))
    }
    It 'a call that fails or times out is held and logged, never retried, and nothing turns the agents VM off' {
        New-HostRoot 'fails' | Out-Null; Use-Guest
        Mock -ModuleName FactoryVM Stop-VMHard { throw "nothing may turn a VM off here" }
        $id = [guid]::NewGuid().ToString()
        $global:Guest.mode = "fail"
        Write-AgentsVM "flare-inbox\$id\result.json" ([byte[]](1)) $id | Should Be "held"
        $global:Guest.mode = "timeout"
        Write-AgentsVM "flare-inbox\$id\result.json" ([byte[]](2)) $id | Should Be "held"
        $global:Guest.calls.Count | Should Be 2
        (Get-Content (Get-HostPath "host.log") -Raw) | Should Match 'held because the call timed out'
        Assert-MockCalled -ModuleName FactoryVM Stop-VMHard -Times 0
    }
    It 'refuses paths it doesn''t write' {
        New-HostRoot 'paths' | Out-Null
        foreach ($p in "fix-state\1\state.json", "flare-inbox\..\x", "C:\Windows\x", "flare-outbox\abc") { { Write-AgentsVM $p ([byte[]](1)) } | Should Throw }
    }
    It (Get-FixtureTitle 'tells the captured "VM off" and "VM restoring" failures apart only by holding them' @('psdirect-failures')) -Skip:(Test-FixturesMissing @('psdirect-failures')) {
        $f = Get-Fixture 'psdirect-failures'
        foreach ($c in "vm-off", "vm-restoring") {
            $case = Get-FixtureCase $f $c
            $case.exit | Should Be 1
            $case.stderr | Should Not BeNullOrEmpty
        }
        (Get-FixtureCase $f 'rename-stopped-after-landing').stdout | Should Match '\.claimed'
    }
}

Describe 'Calls into a VM, with their timeout (P16)' {
    It 'a call past its timeout is stopped and confirmed gone, and a stub that would finish later writes nothing' {
        New-HostRoot 'calls' | Out-Null
        $marker = Join-Path $TestDrive "late-$([guid]::NewGuid().ToString('N')).txt"
        Mock -ModuleName FactoryVM Start-VMCallJob { Start-Job -ScriptBlock { param($m) Start-Sleep 5; Set-Content $m "landed late" } -ArgumentList $argv[0] }
        $r = Invoke-VMCall "HSAGENTS" { } @($marker) 1
        $r.timedOut | Should Be $true
        $r.stopped | Should Be $true
        @(Get-Job | Where-Object { $_.State -eq "Running" -and $_.Command -match 'landed late' }).Count | Should Be 0
        Start-Sleep 6
        Test-Path $marker | Should Be $false
    }
    It 'returns the output of a call that finishes' {
        Mock -ModuleName FactoryVM Start-VMCallJob { Start-Job -ScriptBlock { "answer" } }
        $r = Invoke-VMCall "HSAGENTS" { } @() 30
        $r.ok | Should Be $true; @($r.output)[0] | Should Be "answer"
    }
    It 'accepts only byte arrays and plain strings from the Flare VM' {
        Test-PlainOutput @("a", [byte[]](1, 2)) | Should Be $true
        Test-PlainOutput @([pscustomobject]@{ a = 1 }) | Should Be $false
        Test-PlainOutput @(5) | Should Be $false
    }
}

Describe 'The HSAGENTS write lock (P16)' {
    It 'a caller that can''t get it in time holds its write' {
        New-HostRoot 'busy' | Out-Null; Use-Guest
        $name = (Get-Variable -Name LockName -Scope Script -ValueOnly -ErrorAction SilentlyContinue)
        $lockName = & (Get-Module FactoryVM) { $script:LockName }
        $p = Start-Process powershell.exe -ArgumentList "-NoProfile -Command `"`$m = New-Object System.Threading.Mutex(`$false, '$lockName'); [void]`$m.WaitOne(); Start-Sleep 8`"" -PassThru -WindowStyle Hidden
        Start-Sleep 2
        try {
            $id = [guid]::NewGuid().ToString()
            Write-AgentsVM "flare-inbox\$id\progress.json" ([byte[]](1)) $id | Should Be "held"
            (Get-Content (Get-HostPath "host.log") -Raw) | Should Match 'the write lock was busy'
        }
        finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    It 'a process killed while holding it doesn''t block the next caller' {
        New-HostRoot 'killed' | Out-Null
        $lockName = & (Get-Module FactoryVM) { $script:LockName }
        $p = Start-Process powershell.exe -ArgumentList "-NoProfile -Command `"`$m = New-Object System.Threading.Mutex(`$false, '$lockName'); [void]`$m.WaitOne(); Start-Sleep 60`"" -PassThru -WindowStyle Hidden
        Start-Sleep 2
        Stop-Process -Id $p.Id -Force
        Start-Sleep 1
        $m = Enter-AgentsWriteLock 5
        $m | Should Not BeNullOrEmpty
        Exit-AgentsWriteLock $m
    }
}

Describe 'Write-AgentsVM -AsReset (P16)' {
    # A stand-in for Reset-FactoryVM.ps1 -Name HSAGENTS: same file name, holding the lock with its flag.
    function New-ResetStub([string]$dir) {
        New-Item -ItemType Directory -Force $dir | Out-Null
        $p = Join-Path $dir "Reset-FactoryVM.ps1"
        Set-Content $p 'param($path, $id, [switch]$NoLock, [switch]$OtherFlag) Set-ResetName HSAGENTS; if (-not $NoLock) { $l = Enter-AgentsWriteLock }; if ($OtherFlag) { Set-MaintenanceFlag HSAGENTS "set" 4 } else { Set-MaintenanceFlag HSAGENTS "set" }; try { Write-AgentsVM $path ([byte[]](7)) $id -AsReset } finally { if ($l) { Exit-AgentsWriteLock $l }; Clear-MaintenanceFlag HSAGENTS }'
        $p
    }
    It 'is accepted from the agents VM''s reset holding the lock with a matching flag, and writes though the flag exists' {
        New-HostRoot 'asreset' | Out-Null; Use-Guest
        $stub = New-ResetStub (Join-Path $TestDrive "stub1"); $id = [guid]::NewGuid().ToString()
        & $stub "flare-inbox\$id\progress.json" $id | Should Be "written"
        Test-Path (Join-Path $global:Guest.root "flare-inbox\$id\progress.json") | Should Be $true
    }
    It 'is refused, with the write held, for any other caller, one not holding the lock, or a process that doesn''t match the flag' {
        New-HostRoot 'refused' | Out-Null; Use-Guest
        $id = [guid]::NewGuid().ToString()
        Set-MaintenanceFlag HSAGENTS "set"
        Write-AgentsVM "flare-inbox\$id\progress.json" ([byte[]](1)) $id -AsReset | Should Be "held"   # not the reset's script
        Clear-MaintenanceFlag HSAGENTS
        $stub = New-ResetStub (Join-Path $TestDrive "stub2")
        & $stub "flare-inbox\$id\result.json" $id -NoLock | Should Be "held"
        & $stub "flare-inbox\$id\result.json" $id -OtherFlag | Should Be "held"
        $global:Guest.calls.Count | Should Be 0
        (Get-Content (Get-HostPath "host.log") -Raw) | Should Match 'AsReset was refused because'
    }
    It 'a failed -AsReset write stays held' {
        New-HostRoot 'asresetfail' | Out-Null; Use-Guest
        $global:Guest.mode = "fail"
        $stub = New-ResetStub (Join-Path $TestDrive "stub3"); $id = [guid]::NewGuid().ToString()
        & $stub "flare-inbox\$id\result.json" $id | Should Be "held"
        @(Get-HeldWrites).Count | Should Be 1
    }
}

Describe 'The claim (P16)' {
    function Add-Request([string]$id) {
        $d = Join-Path $global:Guest.root "flare-outbox\$id"; New-Item -ItemType Directory -Force $d | Out-Null
        Set-Content (Join-Path $d "request.json") "{}"; Set-Content (Join-Path $d "fix.patch") "patch"
    }
    # The guest for claims: read, rename (which can land and still time out), read back, write.
    function Use-ClaimGuest {
        Mock -ModuleName FactoryVM Invoke-VMCall {
            $src = $block.ToString(); $g = $global:Guest
            [void]$g.calls.Add($src)
            $r = [pscustomobject]@{ ok = $true; timedOut = $false; output = @(); error = $null; errorId = $null; stopped = $true }
            $out = Join-Path $g.root "flare-outbox"
            if ($src -match 'Sort-Object CreationTimeUtc') {
                $d = @(Get-ChildItem $out -Directory | Where-Object { $_.Name -match '^[0-9a-f-]{36}$' } | Select-Object -First 1)
                if ($d.Count) { $r.output = @($d[0].Name, [IO.File]::ReadAllBytes((Join-Path $d[0].FullName "request.json")), [IO.File]::ReadAllBytes((Join-Path $d[0].FullName "fix.patch"))) }
                return $r
            }
            if ($src -match 'NewName "\$id\.claimed"') {
                if ($g.rename -in "lands-timeout", "lands") { Rename-Item (Join-Path $out $argv[0]) "$($argv[0]).claimed" }
                if ($g.rename -ne "lands") { $r.ok = $false; $r.timedOut = $true }
                return $r
            }
            if ($src -match '"unclaimed"') {
                if ($g.readback -eq "fail") { $r.ok = $false; return $r }
                $r.output = @($(if (Test-Path (Join-Path $out "$($argv[0]).claimed")) { "claimed" } elseif (Test-Path (Join-Path $out $argv[0])) { "unclaimed" } else { "gone" }))
                return $r
            }
            if ($src -match 'WriteAllBytes\(\$tmp') { if ($g.progress -eq "fail") { $r.ok = $false; return $r }; $r.output = @("written"); return $r }
            $r
        }
    }
    It 'with the flag set, claims nothing and leaves the request in the outbox' {
        New-HostRoot 'claimflag' | Out-Null; Use-ClaimGuest
        $id = [guid]::NewGuid().ToString(); Add-Request $id
        Set-MaintenanceFlag HSAGENTS "set"
        (Write-AgentsVM -Claim).claimed | Should Be $false
        Test-Path (Join-Path $global:Guest.root "flare-outbox\$id") | Should Be $true
        Clear-MaintenanceFlag HSAGENTS
    }
    It 'a rename that lands although the call timed out is found by the read-back, and the claim goes on' {
        New-HostRoot 'claimlands' | Out-Null; Use-ClaimGuest
        $id = [guid]::NewGuid().ToString(); Add-Request $id
        $global:Guest.rename = "lands-timeout"
        $c = Write-AgentsVM -Claim
        $c.claimed | Should Be $true; $c.id | Should Be $id
        [Text.Encoding]::UTF8.GetString($c.patch).Trim() | Should Be "patch"
    }
    It 'a rename that didn''t land leaves the request, and a read-back that fails too logs Claim not confirmed' {
        New-HostRoot 'claimnot' | Out-Null; Use-ClaimGuest
        $id = [guid]::NewGuid().ToString(); Add-Request $id
        $global:Guest.rename = "no"
        (Write-AgentsVM -Claim).claimed | Should Be $false
        Test-Path (Join-Path $global:Guest.root "flare-outbox\$id") | Should Be $true
        $global:Guest.readback = "fail"
        (Write-AgentsVM -Claim).claimed | Should Be $false
        (Get-Content (Get-HostPath "host.log") -Raw) | Should Match ([regex]::Escape("The Flare check for $id couldn't confirm its claim on the agents VM, so it didn't start. The factory retries the fix after its timeout."))
    }
    It 'a failed first progress.json is held, and the claim goes on' {
        New-HostRoot 'claimprog' | Out-Null; Use-ClaimGuest
        $id = [guid]::NewGuid().ToString(); Add-Request $id
        $global:Guest.rename = "lands"; $global:Guest.progress = "fail"
        (Write-AgentsVM -Claim).claimed | Should Be $true
        @(Get-HeldWrites | Where-Object { $_.item.path -like "*progress.json" }).Count | Should Be 1
        Test-Path (Get-HostPath "flare-pending\$id\latest-progress.json") | Should Be $true
    }
    It 'a claim and the reset''s flag can''t overlap: the reset waits on the lock the claim holds' {
        New-HostRoot 'claimlock' | Out-Null
        $lockName = & (Get-Module FactoryVM) { $script:LockName }
        $l = Enter-AgentsWriteLock 1
        try {
            # Another process stands in for the reset: it can't take the lock to set its flag while the claim holds it.
            $out = & powershell.exe -NoProfile -Command "`$m = New-Object System.Threading.Mutex(`$false, '$lockName'); `$m.WaitOne(1500)"
            ($out -join '') | Should Be "False"
        }
        finally { Exit-AgentsWriteLock $l }
    }
}

Describe 'Held writes going in (P16)' {
    It 'writes them in oldest first once the flag is clear, removing each, and the delivery folder with the last' {
        New-HostRoot 'deliver' | Out-Null; Use-Guest
        $id = [guid]::NewGuid().ToString()
        Set-MaintenanceFlag HSAGENTS "set"
        Write-AgentsVM "flare-inbox\$id\result.json" ([byte[]](1)) $id | Out-Null
        Write-AgentsVM "flare-inbox\trial.json" ([byte[]](2)) | Out-Null
        Clear-MaintenanceFlag HSAGENTS
        Send-HeldWrites
        @(Get-HeldWrites).Count | Should Be 0
        Test-Path (Join-Path $global:Guest.root "flare-inbox\$id\result.json") | Should Be $true
        Test-Path (Get-HostPath "flare-pending\$id") | Should Be $false
        $paths = @($global:Guest.calls | ForEach-Object { "" + $_.argv[0] })
        $paths[0] | Should Match 'trial\.json$'
    }
}
