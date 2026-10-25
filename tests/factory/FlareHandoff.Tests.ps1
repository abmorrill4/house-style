<#
Pester 3 tests for the host task "House Style Flare check" (factory-runtime spec section 5; P16): the tick order, the
check's steps and deadlines, the reset step and its marker, the trial state and the state re-read, -ScratchVM, the copy
rules, a check running when the agents VM's reset starts, and the other host scripts (Set-FlareInUse.ps1,
Watch-Factory.ps1, Install-HostTasks.ps1). Hyper-V and both VMs are scripted: the Flare VM's steps answer from
$global:Flare, and the agents VM is a folder in TestDrive behind a mocked Invoke-VMCall.
  powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
#>
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $here 'Fixtures.psm1') -Force

Describe 'The Flare check host task (P16)' {
    $vmRoot = Join-Path $TestDrive "vm"; New-Item -ItemType Directory -Force $vmRoot | Out-Null
    . (Join-Path $repoRoot 'scripts\factory\Invoke-FlareHandoff.ps1') -Root $vmRoot

    # ---- the scripted world -------------------------------------------------------------------------------------
    function Reset-World([string]$name) {
        $v = Join-Path $TestDrive $name; New-Item -ItemType Directory -Force $v, (Join-Path $v "flare-pending"), (Join-Path $v "bin") | Out-Null
        Set-FactoryVMRoot $v
        Set-AgentsWriteLockName ("Local\hs-test-" + [guid]::NewGuid().ToString("N")); Set-AgentsWriteLockWait 2
        $global:Clock = [datetime]::new(2026, 11, 2, 12, 0, 0, [DateTimeKind]::Utc)
        $global:Ops = New-Object System.Collections.ArrayList
        $global:Guest = @{ root = (Join-Path $TestDrive "$name-guest"); writes = (New-Object System.Collections.ArrayList); rename = "lands" }
        New-Item -ItemType Directory -Force (Join-Path $global:Guest.root "flare-outbox"), (Join-Path $global:Guest.root "flare-inbox") | Out-Null
        $global:Flare = @{ trialText = "ACTIVE"; read = [pscustomobject]@{ state = "active"; endDate = "2026-12-31" }; hang = ""; fail = ""; result = '{"outcome":"pass","failed":[],"retried":["reopen-issues"]}'; extra = @{}; desktop = $true; onStep = @{} }
        $global:Runner = @("idle"); $global:VMConnect = $false; $global:FailRestore = $false; $global:FailDisconnect = $false; $global:NetStays = $false
        Write-HostJson (Get-HostPath "flare-trial.json") ([pscustomobject]@{ state = "active"; endDate = "2026-12-31"; source = $null; wasLicensed = $false; dailyCheckDay = "2026-11-02" })
        $v
    }
    function Get-HostNow { $global:Clock }
    function Wait-Briefly([int]$seconds) { $global:Clock = $global:Clock.AddSeconds($seconds) }
    function Start-ResumeTask { [void]$global:Ops.Add("resume-task") }
    function Save-HoldoutFiles([string]$dest) { [void]$global:Ops.Add("holdouts"); @() }
    function Test-HostStateAcl { @() }
    function Stop-VMHard([string]$vm) { [void]$global:Ops.Add("off:$vm") }
    function Start-VMPlain([string]$vm) { [void]$global:Ops.Add("start:$vm") }
    function Restore-Checkpoint([string]$vm, [string]$name) { if ($global:FailRestore -and $name -eq "factory-base") { throw "restore failed" }; [void]$global:Ops.Add("restore:${vm}:$name") }
    function Test-Checkpoint([string]$vm, [string]$name) { $true }
    function Disconnect-VMNet([string]$vm) { if ($global:FailDisconnect) { throw "no" }; [void]$global:Ops.Add("disconnect:$vm") }
    function Test-VMNetConnected([string]$vm) { $global:NetStays }
    function Connect-VMNet([string]$vm) { [void]$global:Ops.Add("connect:$vm") }
    function Set-EnhancedSession([bool]$on) { [void]$global:Ops.Add("enhanced:$on") }
    function Test-VMConnectOpen([string]$vm) { $global:VMConnect }
    function Get-RunnerState([string]$name) { $s = $global:Runner[0]; if ($global:Runner.Count -gt 1) { $global:Runner = @($global:Runner | Select-Object -Skip 1) }; $s }
    function ConvertFrom-FlareTrialRead([string]$text) { $global:Flare.read }
    # Calls into the Flare VM from the script: the desktop wait and the runner's listener.
    function Invoke-VMCall([string]$vm, [scriptblock]$block, [object[]]$argv = @(), [int]$timeoutSeconds = 60) {
        [void]$global:Ops.Add("call:${vm}:$(if ($block.ToString() -match 'Stop-ScheduledTask') { 'stop-listener' } elseif ($block.ToString() -match 'Start-ScheduledTask') { 'start-listener' } else { 'desktop' })")
        [pscustomobject]@{ ok = $true; timedOut = $false; output = @($global:Flare.desktop); error = $null }
    }
    # The Flare VM's steps, by name.
    function Invoke-FlareCall([scriptblock]$block, [object[]]$argv, [datetime]$deadline, [switch]$WatchVMConnect) {
        $step = if ($block.ToString() -match 'ReadAllBytes\(\$f\.FullName\)') { "List" } elseif ($argv.Count -ge 2 -and $argv[1]) { "" + $argv[1] } else { "Holdout" }
        [void]$global:Ops.Add("step:$step")
        if ($global:Flare.onStep.ContainsKey($step)) { & $global:Flare.onStep[$step] }
        if ($WatchVMConnect -and $global:VMConnect) { Stop-VMHard $script:CheckVM; throw (New-CheckStop "in-use" "VMConnect") }
        if ($global:Flare.hang -eq $step) { Stop-VMHard $script:CheckVM; throw (New-CheckStop "timeout" "hung") }
        if ($global:Flare.fail -eq $step) { throw "the $step step failed" }
        switch ($step) {
            "TrialRead" { return [pscustomobject]@{ ok = $true; output = @("22.1", $global:Flare.trialText) } }
            "List" {
                $out = @("result.json", "False", ([Text.Encoding]::UTF8.GetBytes($global:Flare.result)), "shots\001-a.png", "False", ([byte[]](1, 2)), "install.log", "False", ([Text.Encoding]::UTF8.GetBytes("built")))
                foreach ($k in $global:Flare.extra.Keys) { $out += @($k, "False", ([Text.Encoding]::UTF8.GetBytes($global:Flare.extra[$k]))) }
                return [pscustomobject]@{ ok = $true; output = $out }
            }
            default { return [pscustomobject]@{ ok = $true; output = @("done") } }
        }
    }
    # The agents VM behind Write-AgentsVM: writes land in the guest folder; claims rename there.
    function Use-AgentsGuest {
        Mock -ModuleName FactoryVM Invoke-VMCall {
            $src = $block.ToString(); $g = $global:Guest
            $r = [pscustomobject]@{ ok = $true; timedOut = $false; output = @(); error = $null; errorId = $null; stopped = $true }
            $out = Join-Path $g.root "flare-outbox"
            if ($src -match 'Sort-Object CreationTimeUtc') {
                $d = @(Get-ChildItem $out -Directory | Where-Object { $_.Name -match '^[0-9a-f-]{36}$' } | Select-Object -First 1)
                if ($d.Count) { $r.output = @($d[0].Name, [IO.File]::ReadAllBytes((Join-Path $d[0].FullName "request.json")), [IO.File]::ReadAllBytes((Join-Path $d[0].FullName "fix.patch"))) }
                return $r
            }
            if ($src -match 'NewName "\$id\.claimed"') { Rename-Item (Join-Path $out $argv[0]) "$($argv[0]).claimed"; [void]$g.writes.Add("claim:$($argv[0])"); return $r }
            if ($src -match 'WriteAllBytes\(\$tmp') {
                $path = ("" + $argv[0]).Replace("C:\HouseStyle", $g.root)
                New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
                [IO.File]::WriteAllBytes($path, [byte[]]$argv[1]); [void]$g.writes.Add(("" + $argv[0]).Replace("C:\HouseStyle\", ""))
                return $r
            }
            $r
        }
    }
    function Add-Request {
        $id = [guid]::NewGuid().ToString(); $patch = [Text.Encoding]::UTF8.GetBytes("diff --git a/src/x.cs b/src/x.cs`n")
        $sha = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($patch) | ForEach-Object { $_.ToString("x2") })
        $d = Join-Path $global:Guest.root "flare-outbox\$id"; New-Item -ItemType Directory -Force $d | Out-Null
        [IO.File]::WriteAllText((Join-Path $d "request.json"), (@{ check = $id; base = ("a" * 40); patchSha256 = $sha } | ConvertTo-Json -Compress))
        [IO.File]::WriteAllBytes((Join-Path $d "fix.patch"), $patch)
        $id
    }
    function Get-GuestResult([string]$id) { $p = Join-Path $global:Guest.root "flare-inbox\$id\result.json"; if (Test-Path $p) { Get-Content $p -Raw | ConvertFrom-Json } else { $null } }
    function Get-HostLogText { $p = Get-HostPath "host.log"; if (Test-Path $p) { Get-Content $p -Raw } else { "" } }

    # ---- a whole check ------------------------------------------------------------------------------------------
    It 'runs a check: flare-check-base with the network on, cut before the build, factory-base before the result goes in' {
        Reset-World 'pass' | Out-Null; Use-AgentsGuest
        $id = Add-Request
        Invoke-HandoffTick | Should Be "checked:pass"
        $ops = @($global:Ops)
        $i = { param($o) [array]::IndexOf($ops, $o) }
        (& $i "enhanced:False") | Should BeLessThan (& $i "restore:HSFACTORY:flare-check-base")
        (& $i "step:TrialRead") | Should BeLessThan (& $i "step:Prepare")
        (& $i "step:Prepare") | Should BeLessThan (& $i "disconnect:HSFACTORY")
        (& $i "disconnect:HSFACTORY") | Should BeLessThan (& $i "step:Install")
        (& $i "step:Run") | Should BeLessThan (& $i "restore:HSFACTORY:factory-base")
        (& $i "restore:HSFACTORY:factory-base") | Should BeLessThan (& $i "connect:HSFACTORY")
        $ops -contains "enhanced:True" | Should Be $true
        $r = Get-GuestResult $id
        $r.outcome | Should Be "pass"; @($r.retried) -join ',' | Should Be "reopen-issues"
        Test-Path (Join-Path $global:Guest.root "flare-outbox\$id.claimed") | Should Be $true
        Test-Path (Join-Path $global:Guest.root "flare-inbox\trial.json") | Should Be $true
        Test-Path (Join-Path $global:Guest.root "flare-inbox\$id\logs\install.log") | Should Be $true
        Test-Path (Get-HostPath "flare-logs\$id\shots-001-a.png") | Should Be $true   # screenshots stay on the host
        Get-CheckLock | Should BeNullOrEmpty
    }
    It 'in use or VMConnect open at step 2 gives vm/in-use, restores nothing, releases the lock and restarts the listener' {
        Reset-World 'inuse' | Out-Null; Use-AgentsGuest
        [IO.File]::WriteAllText((Get-HostPath "flare-in-use"), "x")
        $id = Add-Request
        Invoke-HandoffTick | Should Be "checked:vm/in-use"
        @($global:Ops | Where-Object { $_ -like "restore:*" }).Count | Should Be 0
        $global:Ops -contains "call:HSFACTORY:start-listener" | Should Be $true
        Get-CheckLock | Should BeNullOrEmpty
        Remove-Item (Get-HostPath "flare-in-use")
        $global:VMConnect = $true; Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "checked:vm/in-use"
    }
    It 'waits for a busy runner, and after 30 minutes gives vm/busy with nothing restored' {
        Reset-World 'busy' | Out-Null; Use-AgentsGuest
        $global:Runner = @(@("busy") * 100)
        Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "checked:vm/busy"
        @($global:Ops | Where-Object { $_ -like "restore:*" }).Count | Should Be 0
        Get-CheckLock | Should BeNullOrEmpty
        # A runner that frees up: its listener stops, and a job that slipped in gets it back first.
        Reset-World 'busy2' | Out-Null; Use-AgentsGuest
        $global:Runner = @("idle", "busy", "idle", "idle")
        Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "checked:pass"
        @($global:Ops | Where-Object { $_ -eq "call:HSFACTORY:start-listener" }).Count | Should BeGreaterThan 0
    }
    It 'a failure at step 3 is vm/no-start, still reset' {
        Reset-World 'nostart' | Out-Null; Use-AgentsGuest
        $global:Flare.desktop = $false
        $id = Add-Request
        Invoke-HandoffTick | Should Be "checked:vm/no-start"
        $global:Ops -contains "restore:HSFACTORY:factory-base" | Should Be $true
        (Get-GuestResult $id).vmKind | Should Be "no-start"
    }
    It 'a failed disconnect, or an adapter still connected, is vm/not-ready with no holdouts copied in' {
        Reset-World 'nonet' | Out-Null; Use-AgentsGuest
        $global:FailDisconnect = $true
        Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "checked:vm/not-ready"
        $global:Ops -contains "holdouts" | Should Be $false
        Reset-World 'nonet2' | Out-Null; Use-AgentsGuest
        $global:NetStays = $true
        Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "checked:vm/not-ready"
    }
    It 'a hang turns only the Flare VM off and goes to the reset with vm/timeout' {
        Reset-World 'hang' | Out-Null; Use-AgentsGuest
        Mock -ModuleName FactoryVM Stop-VMHard { throw "the agents VM may not be turned off" }
        $global:Flare.hang = "Run"
        $id = Add-Request
        Invoke-HandoffTick | Should Be "checked:vm/timeout"
        @($global:Ops | Where-Object { $_ -like "off:*" -and $_ -ne "off:HSFACTORY" }).Count | Should Be 0
        $global:Ops -contains "restore:HSFACTORY:factory-base" | Should Be $true
        (Get-GuestResult $id).vmKind | Should Be "timeout"
    }
    It 'VMConnect opening during the check turns the VM off and gives vm/in-use' {
        Reset-World 'vmconnect' | Out-Null; Use-AgentsGuest
        $global:Flare.onStep["Install"] = { $global:VMConnect = $true }
        Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "checked:vm/in-use"
        $global:Ops -contains "off:HSFACTORY" | Should Be $true
    }
    It 'a failed restore turns the VM off, keeps the lock and the staged result, sets the marker, and delivers nothing' {
        Reset-World 'failedreset' | Out-Null; Use-AgentsGuest
        $global:FailRestore = $true
        $id = Add-Request
        Invoke-HandoffTick | Out-Null
        Get-GuestResult $id | Should BeNullOrEmpty
        Test-FailedResetMarker | Should Be $true
        Get-CheckLock | Should Not BeNullOrEmpty
        $global:Ops -contains "connect:HSFACTORY" | Should Be $false
        (Get-HostLogText) | Should Match ([regex]::Escape("The Flare VM couldn't be put back on its checkpoint, so it's off, offline and marked as a failed reset."))
        # The next tick stops at the marker.
        Invoke-HandoffTick | Should Be "stopped"
    }
    It 'a slow runner doesn''t hold up the reset: nothing waits for it to come back' {
        Reset-World 'slowrunner' | Out-Null; Use-AgentsGuest
        $global:Runner = @("idle", "idle") + @(@("offline") * 50)
        Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "checked:pass"
        Get-CheckLock | Should BeNullOrEmpty
    }
    It 'a step 7 report of an expired trial or invalid license, while the host''s state is active or licensed, is vm/no-scenarios' {
        Reset-World 'forged' | Out-Null; Use-AgentsGuest
        $global:Flare.extra["license-report.txt"] = "Flare reported its trial or license expired during the run."
        $global:Flare.result = '{"outcome":"vm","vmKind":"trial-expired","trial":"expired","endDate":"2099-01-01"}'
        $id = Add-Request
        Invoke-HandoffTick | Should Be "checked:vm/no-scenarios"
        # A forged far-future date in the VM's output changes nothing the host keeps.
        (Get-TrialState).endDate | Should Be "2026-12-31"
        (Get-TrialState).state | Should Be "active"
    }
    It 'stages under the copy rules: a name or depth the host doesn''t take refuses the results' {
        Reset-World 'copyrules' | Out-Null; Use-AgentsGuest
        $global:Flare.extra["a\b\c.png"] = "x"
        $id = Add-Request
        Invoke-HandoffTick | Should Be "checked:vm/no-scenarios"
    }

    # ---- the tick order -----------------------------------------------------------------------------------------
    It 'removes a stale HSFACTORY flag, starts the resume for a stale HSAGENTS flag, and never touches the marker' {
        Reset-World 'stale' | Out-Null; Use-AgentsGuest
        Write-HostJson (Get-FlagPath HSFACTORY) ([pscustomobject]@{ pid = 999999; start = "x"; step = "restoring" })
        Write-HostJson (Get-FlagPath HSAGENTS) ([pscustomobject]@{ pid = 999999; start = "x"; step = "carried-out" })
        Set-FailedResetMarker
        Invoke-HandoffTick | Should Be "stopped"
        Test-Path (Get-FlagPath HSFACTORY) | Should Be $false
        $global:Ops -contains "resume-task" | Should Be $true
        Test-FailedResetMarker | Should Be $true
        (Get-HostLogText) | Should Match 'A maintenance flag was left by a reset that stopped early'
        (Get-HostLogText) | Should Match "The agents VM's reset stopped early, so it's being finished now"
    }
    It 'runs the daily trial check with no check due, and writes held writes once the flag is clear' {
        Reset-World 'daily' | Out-Null; Use-AgentsGuest
        Write-HostJson (Get-HostPath "flare-trial.json") ([pscustomobject]@{ state = "active"; endDate = "2026-12-31"; source = $null; wasLicensed = $false })
        $id = [guid]::NewGuid().ToString()
        Set-MaintenanceFlag HSAGENTS "set"
        Write-AgentsVM "flare-inbox\$id\result.json" ([byte[]](1)) $id | Out-Null
        Clear-MaintenanceFlag HSAGENTS
        Invoke-HandoffTick | Out-Null
        $global:Guest.writes -contains "flare-inbox\trial.json" | Should Be $true
        $global:Guest.writes -contains "flare-inbox\$id\result.json" | Should Be $true
        # Once a day only.
        $n = @($global:Guest.writes | Where-Object { $_ -eq "flare-inbox\trial.json" }).Count
        Invoke-HandoffTick | Out-Null
        @($global:Guest.writes | Where-Object { $_ -eq "flare-inbox\trial.json" }).Count | Should Be $n
    }
    It 'a date seven days away warns from the daily check, with no check run, once per date' {
        Reset-World 'warn' | Out-Null; Use-AgentsGuest
        Write-HostJson (Get-HostPath "flare-trial.json") ([pscustomobject]@{ state = "active"; endDate = "2026-11-09"; source = $null; wasLicensed = $false })
        Invoke-DailyTrialCheck $global:Clock | Should Be $true
        (Get-HostLogText) | Should Match ([regex]::Escape("Flare's trial on the Flare VM ends on 2026-11-09. Add a Flare license"))
        $global:Clock = $global:Clock.AddDays(1)
        Invoke-DailyTrialCheck $global:Clock | Out-Null
        ([regex]::Matches((Get-HostLogText), 'ends on 2026-11-09')).Count | Should Be 1
        @($global:Ops | Where-Object { $_ -like "restore:*" }).Count | Should Be 0
    }
    It 'turns an active trial whose date passed into expired from the date, and then claims nothing' {
        Reset-World 'datepassed' | Out-Null; Use-AgentsGuest
        Write-HostJson (Get-HostPath "flare-trial.json") ([pscustomobject]@{ state = "active"; endDate = "2026-11-01"; source = $null; wasLicensed = $false })
        Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "no-claim"
        (Get-TrialState).state | Should Be "expired"; (Get-TrialState).source | Should Be "date"
        @($global:Ops | Where-Object { $_ -like "restore:*" }).Count | Should Be 0
        Test-ReReadDue $global:Clock | Should Be $false   # never re-read for expired from the date
    }
    It 'maintenance-HSAGENTS doesn''t stop a running check, and nothing is claimed while it exists' {
        Reset-World 'agentsflag' | Out-Null; Use-AgentsGuest
        $sleeper = Start-Process powershell.exe -ArgumentList "-NoProfile -Command Start-Sleep 120" -PassThru -WindowStyle Hidden
        try {
            Write-HostJson (Get-FlagPath HSAGENTS) ([pscustomobject]@{ pid = $sleeper.Id; start = (Get-ProcessStartTime $sleeper.Id); step = "set" })
            Add-Request | Out-Null
            Invoke-HandoffTick | Should Be "no-request"
        }
        finally { Stop-Process -Id $sleeper.Id -Force -ErrorAction SilentlyContinue; Clear-MaintenanceFlag HSAGENTS }
    }
    It 'a check running when the agents VM''s reset starts finishes with its real outcome; every write is held until the reset writes them in before carry-done' {
        Reset-World 'atreset' | Out-Null; Use-AgentsGuest
        Mock -ModuleName FactoryVM Stop-VMHard { throw "nothing turns the agents VM off" }
        $sleeper = Start-Process powershell.exe -ArgumentList "-NoProfile -Command Start-Sleep 120" -PassThru -WindowStyle Hidden
        try {
            # The reset sets its flag while the check is at step 6.
            $global:Flare.onStep["Install"] = { Write-HostJson (Get-FlagPath HSAGENTS) ([pscustomobject]@{ pid = $sleeper.Id; start = (Get-ProcessStartTime $sleeper.Id); step = "set" }); $global:FlagAt = $global:Guest.writes.Count }
            $id = Add-Request
            Invoke-HandoffTick | Should Be "checked:pass"
            $global:Guest.writes.Count | Should Be $global:FlagAt                       # nothing reached the agents VM after the flag
            Test-Path (Get-HostPath "flare-pending\$id\latest-progress.json") | Should Be $true
            (Read-HostJson (Get-HostPath "flare-pending\$id\latest-progress.json")).step | Should Be 8
            @(Get-HeldWrites | Where-Object { $_.item.path -like "*result.json" }).Count | Should Be 1
            # The reset's held-write step: -AsReset from Reset-FactoryVM.ps1, holding the lock, its flag its own.
            Clear-MaintenanceFlag HSAGENTS
            $stub = Join-Path $TestDrive "atreset-stub\Reset-FactoryVM.ps1"; New-Item -ItemType Directory -Force (Split-Path $stub) | Out-Null
            Set-Content $stub 'Set-ResetName HSAGENTS; $l = Enter-AgentsWriteLock; Set-MaintenanceFlag HSAGENTS "carried-back"; try { Send-HeldWrites -AsReset; Write-AgentsVM "carry-done" ([byte[]](1)) -AsReset -NoHold } finally { Clear-MaintenanceFlag HSAGENTS; Exit-AgentsWriteLock $l }'
            & $stub | Out-Null
            $w = @($global:Guest.writes | Select-Object -Skip $global:FlagAt)
            $w[-1] | Should Be "carry-done"
            $w -contains "flare-inbox\$id\result.json" | Should Be $true
            $w -contains "flare-inbox\$id\progress.json" | Should Be $true
            $w -contains "flare-inbox\trial.json" | Should Be $true
            (Get-GuestResult $id).outcome | Should Be "pass"
        }
        finally { Stop-Process -Id $sleeper.Id -Force -ErrorAction SilentlyContinue }
    }

    # ---- stale locks --------------------------------------------------------------------------------------------
    It 'a stale check lock before step 9 gets step 9 then step 10; a stale re-read lock gets step 9 only' {
        Reset-World 'stalelock' | Out-Null; Use-AgentsGuest
        $id = [guid]::NewGuid().ToString()
        Write-HostJson (Get-HostPath "flare-check.lock") ([pscustomobject]@{ pid = 999999; start = "x"; check = $id; step = 6; at = $global:Clock.ToString("o") })
        Invoke-HandoffTick | Should Be "recovered"
        $global:Ops -contains "restore:HSFACTORY:factory-base" | Should Be $true
        (Get-GuestResult $id).vmKind | Should Be "interrupted"
        Get-CheckLock | Should BeNullOrEmpty
        Reset-World 'stalereread' | Out-Null; Use-AgentsGuest
        Write-HostJson (Get-HostPath "flare-check.lock") ([pscustomobject]@{ pid = 999999; start = "x"; check = "re-read"; step = 3; at = $global:Clock.ToString("o") })
        Invoke-HandoffTick | Should Be "recovered"
        $global:Ops -contains "restore:HSFACTORY:factory-base" | Should Be $true
        @($global:Guest.writes | Where-Object { $_ -like "*result.json" }).Count | Should Be 0
    }

    # ---- the trial state and the re-read ------------------------------------------------------------------------
    It 'a read writes active and its date; Flare''s own expiry with a future date writes expired from Flare, and the check gives trial-expired' {
        Reset-World 'expiredflare' | Out-Null; Use-AgentsGuest
        $global:Flare.read = [pscustomobject]@{ state = "expired"; endDate = $null }
        $id = Add-Request
        Invoke-HandoffTick | Should Be "checked:vm/trial-expired"
        $t = Get-TrialState
        $t.state | Should Be "expired"; $t.source | Should Be "flare"; $t.endDate | Should Be "2026-12-31"
        (Get-Content (Join-Path $global:Guest.root "flare-inbox\trial.json") -Raw | ConvertFrom-Json).state | Should Be "expired"
        (Get-HostLogText) | Should Match 'is now expired, ending 2026-12-31, from Flare''s own report'
        Test-ReReadDue $global:Clock.AddDays(1) | Should Be $true
    }
    It 'a shape it doesn''t know is unknown: trial-unknown, a host log line with the redacted text, and no strike' {
        Reset-World 'unknown' | Out-Null; Use-AgentsGuest
        $global:Flare.read = [pscustomobject]@{ state = "unknown"; endDate = $null }
        $global:Flare.trialText = "Something new for licensee person@example.com key ABCD-EFGH-IJKL-MNOP"
        $id = Add-Request
        Invoke-HandoffTick | Should Be "checked:vm/trial-unknown"
        (Get-HostLogText) | Should Match 'is now unknown'
        (Get-HostLogText) | Should Not Match 'person@example\.com'
        (Get-HostLogText) | Should Not Match 'ABCD-EFGH'
        (Get-GuestResult $id).vmKind | Should Be "trial-unknown"
    }
    It 'a transient unknown clears on the next day''s re-read, without -Refresh, and the next tick claims' {
        Reset-World 'transient' | Out-Null; Use-AgentsGuest
        Write-HostJson (Get-HostPath "flare-trial.json") ([pscustomobject]@{ state = "unknown"; endDate = "2026-12-31"; source = $null; wasLicensed = $false; dailyCheckDay = "2026-11-02" })
        $global:Flare.read = [pscustomobject]@{ state = "active"; endDate = "2026-12-31" }
        Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "re-read"
        (Get-TrialState).state | Should Be "active"
        $global:Ops -contains "restore:HSFACTORY:factory-base" | Should Be $true
        (Get-Content (Join-Path $global:Guest.root "flare-inbox\trial.json") -Raw | ConvertFrom-Json).state | Should Be "active"
        Invoke-HandoffTick | Should Be "checked:pass"
        # No second re-read the same day.
        Write-HostJson (Get-HostPath "flare-trial.json") ([pscustomobject]@{ state = "unknown"; endDate = $null; source = $null; wasLicensed = $false; reReadDay = "2026-11-02"; dailyCheckDay = "2026-11-02" })
        Test-ReReadDue $global:Clock | Should Be $false
    }
    It 'a lapsed license: expired from Flare with no date and wasLicensed, re-read daily, cleared by a licensed read' {
        Reset-World 'lapsed' | Out-Null; Use-AgentsGuest
        Write-HostJson (Get-HostPath "flare-trial.json") ([pscustomobject]@{ state = "licensed"; endDate = $null; source = $null; wasLicensed = $false; dailyCheckDay = "2026-11-02" })
        $global:Flare.read = [pscustomobject]@{ state = "expired"; endDate = $null }
        Add-Request | Out-Null
        Invoke-HandoffTick | Should Be "checked:vm/trial-expired"
        $t = Get-TrialState
        $t.state | Should Be "expired"; $t.endDate | Should BeNullOrEmpty; $t.wasLicensed | Should Be $true
        (Get-Content (Join-Path $global:Guest.root "flare-inbox\trial.json") -Raw | ConvertFrom-Json).wasLicensed | Should Be $true
        $global:Clock = $global:Clock.AddDays(1)
        Test-ReReadDue $global:Clock | Should Be $true
        $global:Flare.read = [pscustomobject]@{ state = "licensed"; endDate = $null }
        Invoke-StateReRead | Should Be "re-read"
        (Get-TrialState).state | Should Be "licensed"
    }
    It 'a re-read waits on the in-use flag, VMConnect and a busy runner' {
        Reset-World 'rereadwait' | Out-Null; Use-AgentsGuest
        Write-HostJson (Get-HostPath "flare-trial.json") ([pscustomobject]@{ state = "unknown"; source = $null; wasLicensed = $false; dailyCheckDay = "2026-11-02" })
        [IO.File]::WriteAllText((Get-HostPath "flare-in-use"), "x")
        Invoke-StateReRead | Should Be "skipped"
        Remove-Item (Get-HostPath "flare-in-use")
        $global:VMConnect = $true; Invoke-StateReRead | Should Be "skipped"; $global:VMConnect = $false
        $global:Runner = @(@("busy") * 100); Invoke-StateReRead | Should Be "skipped"
        @($global:Ops | Where-Object { $_ -like "restore:*" }).Count | Should Be 0
        Get-CheckLock | Should BeNullOrEmpty
    }
    It (Get-FixtureTitle 'maps the captured active and expired reads' @('flare-trial-active', 'flare-trial-expired')) -Skip:(Test-FixturesMissing @('flare-trial-active', 'flare-trial-expired')) {
        Remove-Item function:ConvertFrom-FlareTrialRead -ErrorAction SilentlyContinue
        $a = & (Get-Module FactoryVM) { param($t) ConvertFrom-FlareTrialRead $t } (Get-FixtureCase (Get-Fixture 'flare-trial-active') 'flare-trial-active').stdout
        $a.state | Should Be "active"; $a.endDate | Should Match '^\d{4}-\d{2}-\d{2}$'
        $e = & (Get-Module FactoryVM) { param($t) ConvertFrom-FlareTrialRead $t } (Get-FixtureCase (Get-Fixture 'flare-trial-expired') 'flare-trial-expired').stdout
        $e.state | Should Be "expired"
    }
    It (Get-FixtureTitle 'maps the captured licensed read, and drops the trial date' @('flare-licensed')) -Skip:(Test-FixturesMissing @('flare-licensed')) {
        $l = & (Get-Module FactoryVM) { param($t) ConvertFrom-FlareTrialRead $t } (Get-FixtureCase (Get-Fixture 'flare-licensed') 'flare-licensed').stdout
        $l.state | Should Be "licensed"
    }
    It 'until the fixtures are captured, the parser knows no shapes, so every read is unknown' {
        if (-not (Test-FixturesMissing @('flare-trial-active'))) { return }
        @(& (Get-Module FactoryVM) { $FlareTrialShapes }).Count | Should Be 0
        (& (Get-Module FactoryVM) { ConvertFrom-FlareTrialRead "anything Flare shows" }).state | Should Be "unknown"
    }
}

Describe '-ScratchVM (P16)' {
    It 'refuses any name outside the pattern' {
        { & (Join-Path $repoRoot 'scripts\factory\Invoke-FlareHandoff.ps1') -ScratchVM "HSFACTORY" -Root (Join-Path $TestDrive "sv") } | Should Throw "HSSCRATCH"
        { & (Join-Path $repoRoot 'scripts\factory\Invoke-FlareHandoff.ps1') -ScratchVM "HSSCRATCH-12345" -Root (Join-Path $TestDrive "sv") } | Should Throw
    }
    It 'with a valid name touches only vm\scratch: its own lock and trial file, no claim, no HSFACTORY checkpoint, and a second tick claims nothing' {
        $vm = Join-Path $TestDrive "scratchvm"; New-Item -ItemType Directory -Force $vm, (Join-Path $vm "scratch") | Out-Null
        . (Join-Path $repoRoot 'scripts\factory\Invoke-FlareHandoff.ps1') -Root $vm -ScratchVM "HSSCRATCH-1"
        $global:Ops = New-Object System.Collections.ArrayList
        function Test-HostStateAcl { @() }
        function Get-HostNow { [datetime]::new(2026, 11, 2, 12, 0, 0, [DateTimeKind]::Utc) }
        function Wait-Briefly([int]$s) { }
        function Stop-VMHard([string]$v) { [void]$global:Ops.Add("off:$v") }
        function Start-VMPlain([string]$v) { [void]$global:Ops.Add("start:$v") }
        function Restore-Checkpoint([string]$v, [string]$n) { [void]$global:Ops.Add("restore:${v}:$n") }
        function Set-EnhancedSession([bool]$on) { }
        function Test-VMConnectOpen([string]$v) { $false }
        function Invoke-VMCall([string]$vm, [scriptblock]$block, [object[]]$argv = @(), [int]$timeoutSeconds = 60) { [void]$global:Ops.Add("call:$vm"); [pscustomobject]@{ ok = $true; output = @($true) } }
        function Invoke-FlareCall([scriptblock]$block, [object[]]$argv, [datetime]$deadline, [switch]$WatchVMConnect) { [void]$global:Ops.Add("step:$($argv[1])"); [pscustomobject]@{ ok = $true; output = @("22.1", "expired text") } }
        function ConvertFrom-FlareTrialRead([string]$t) { [pscustomobject]@{ state = "expired"; endDate = $null } }
        Mock -ModuleName FactoryVM Invoke-VMCall { throw "no call into the agents VM" }
        Invoke-HandoffTick | Should Be "scratch:vm/trial-expired"
        (Read-HostJson (Join-Path $vm "scratch\flare-trial.json")).state | Should Be "expired"
        (Read-HostJson (Join-Path $vm "scratch\result.json")).vmKind | Should Be "trial-expired"
        Test-Path (Join-Path $vm "flare-trial.json") | Should Be $false
        @($global:Ops | Where-Object { $_ -like "*HSFACTORY*" }).Count | Should Be 0
        @($global:Ops | Where-Object { $_ -like "restore:*" }).Count | Should Be 0
        $n = $global:Ops.Count
        Invoke-HandoffTick | Should Be "no-claim"
        $global:Ops.Count | Should Be $n
    }
    It 'no task registration passes -ScratchVM' {
        . (Join-Path $repoRoot 'scripts\factory\Install-HostTasks.ps1')
        @($HostTasks | Where-Object { $_.args -match 'ScratchVM' }).Count | Should Be 0
    }
}

Describe 'The copy rules and the host state folders (P16)' {
    Import-Module (Join-Path $repoRoot 'scripts\factory\FactoryVM.psm1') -Force
    function F($rel, $len = 10, [switch]$Reparse) { [pscustomobject]@{ rel = $rel; reparse = [bool]$Reparse; bytes = [byte[]](@(1) * $len) } }
    It 'takes files at the top or one level down with safe names, as new plain files' {
        $d = Join-Path $TestDrive "copy1"; Set-FactoryVMRoot $TestDrive
        (Copy-FlareResults @((F "result.json"), (F "run.log"), (F "shots\001-a.png"), (F "x.trx")) $d).ok | Should Be $true
        Test-Path (Join-Path $d "shots\001-a.png") | Should Be $true
    }
    It 'refuses depth, bad names, . and .., device names, reparse points, more than 200 files, and result.json over 16 KB' {
        Set-FactoryVMRoot $TestDrive
        foreach ($bad in @(@(F "a\b\c.png"), @(F ".."), @(F "x.exe"), @(F "CON.txt"), @(F "-x.txt"), @(F "shots\.\a.png"), @(F "shots\NUL\a.png"), @(F "a.png" -Reparse), @(F "result.json" 16385), @(F "shots\result.json"))) {
            (Copy-FlareResults $bad (Join-Path $TestDrive ("c" + [guid]::NewGuid().ToString("N")))).ok | Should Be $false
        }
        (Copy-FlareResults @(1..201 | ForEach-Object { F "f$_.txt" }) (Join-Path $TestDrive "many")).ok | Should Be $false
    }
    It 'checks the host state folders: a writable vm or vm\bin, and runner-vault or scratch open to anyone else, are wrong' {
        $v = Join-Path $TestDrive "aclroot"; New-Item -ItemType Directory -Force (Join-Path $v "bin"), (Join-Path $v "runner-vault"), (Join-Path $v "scratch") | Out-Null
        Set-FactoryVMRoot $v
        $p = @(Test-HostStateAcl)
        ($p -join ' ') | Should Match "isn't owned by Administrators|lets .* write|inherits its ACL"
        ($p -join ' ') | Should Match "runner-vault isn't Administrators only|scratch isn't Administrators only"
        (Get-Content (Join-Path $repoRoot 'scripts\factory\FactoryVM.psm1') -Raw) | Should Match '\$AdminOnlyNames = "runner-vault", "scratch"'
    }
}

Describe 'The other host scripts (P16)' {
    It 'Set-FlareInUse.ps1 -On restores factory-base before it sets the flag' {
        $v = Join-Path $TestDrive "inuse"; New-Item -ItemType Directory -Force $v | Out-Null
        . (Join-Path $repoRoot 'scripts\factory\Set-FlareInUse.ps1') -Root $v
        $global:Ops = New-Object System.Collections.ArrayList
        function Stop-VMHard([string]$vm) { [void]$global:Ops.Add("off") }
        function Restore-Checkpoint([string]$vm, [string]$n) { [void]$global:Ops.Add("restore:${n}:flag=$(Test-Path (Join-Path $v 'flare-in-use'))") }
        function Start-VMPlain([string]$vm) { [void]$global:Ops.Add("start") }
        Set-FlareInUse -On | Should Be "on"
        $global:Ops -contains "restore:factory-base:flag=False" | Should Be $true
        Test-Path (Join-Path $v "flare-in-use") | Should Be $true
        Set-FlareInUse -Off | Should Be "off"
        Test-Path (Join-Path $v "flare-in-use") | Should Be $false
    }
    It 'Watch-Factory makes no call into the agents VM, and does nothing to it while maintenance-HSAGENTS exists' {
        $v = Join-Path $TestDrive "watch"; New-Item -ItemType Directory -Force $v | Out-Null
        . (Join-Path $repoRoot 'scripts\factory\Watch-Factory.ps1') -Root $v
        $global:Ops = New-Object System.Collections.ArrayList
        function Invoke-VMCall([string]$vm) { [void]$global:Ops.Add("call:$vm"); [pscustomobject]@{ ok = $true; output = @() } }
        function Get-VMStateName([string]$vm) { "Off" }
        function Start-VMPlain([string]$vm) { [void]$global:Ops.Add("start:$vm") }
        function Restart-VMGuest([string]$vm) { [void]$global:Ops.Add("reboot:$vm") }
        function Get-RunnerState([string]$n) { "offline" }
        function Get-VMUptimeMinutes([string]$vm) { 100 }
        Set-MaintenanceFlag HSAGENTS "set"
        Invoke-WatchVM HSAGENTS | Should Be "maintenance"
        $global:Ops.Count | Should Be 0
        Clear-MaintenanceFlag HSAGENTS
        Invoke-WatchVM HSAGENTS | Should Be "started"
        $global:Ops -contains "start:HSAGENTS" | Should Be $true
        @($global:Ops | Where-Object { $_ -eq "call:HSAGENTS" }).Count | Should Be 0
        $src = Get-Content (Join-Path $repoRoot 'scripts\factory\Watch-Factory.ps1') -Raw
        $src | Should Not Match 'Invoke-Command'
        $src | Should Match ([regex]::Escape('Invoke-VMCall $FlareVMName'))
        $src | Should Not Match 'Invoke-VMCall \$AgentsVMName|Invoke-VMCall \$vm'
    }
    It 'the task registrations match the limits table' {
        . (Join-Path $repoRoot 'scripts\factory\Install-HostTasks.ps1')
        $t = @{}; foreach ($x in $HostTasks) { $t[$x.name] = $x }
        $t["House Style Flare check"].limit | Should Be (New-TimeSpan -Hours 4); $t["House Style Flare check"].trigger | Should Be "minute"
        $t["House Style HSFACTORY nightly reset"].trigger | Should Be "03:00"; $t["House Style HSFACTORY nightly reset"].limit | Should Be (New-TimeSpan -Hours 6)
        $t["House Style HSAGENTS nightly reset"].trigger | Should Be "03:30"; $t["House Style HSAGENTS nightly reset"].limit | Should Be (New-TimeSpan -Hours 6)
        $t["House Style HSAGENTS reset resume"].trigger | Should Be "demand"; $t["House Style HSAGENTS reset resume"].args | Should Be "-Name HSAGENTS -Resume"
        $t["House Style factory watch"].trigger | Should Be "5minutes"; $t["House Style factory watch"].limit | Should Be (New-TimeSpan -Minutes 4)
        (Get-Content (Join-Path $repoRoot 'scripts\factory\Install-HostTasks.ps1') -Raw) | Should Match '-MultipleInstances IgnoreNew'
        $HostTasks.Count | Should Be 5
    }
    It 'Run-FlareE2E.ps1 builds and installs without starting Flare with -PrepareOnly' {
        $src = Get-Content (Join-Path $repoRoot 'tests\e2e\Run-FlareE2E.ps1') -Raw
        $src | Should Match '\[switch\]\$PrepareOnly'
        $src.IndexOf('if ($PrepareOnly) {') | Should BeLessThan $src.IndexOf('Get-ChildItem (Join-Path $repo "tests\e2e\scenarios")')
    }
    It 'Invoke-FlareCheck loads PatchRules.psd1 from the clone''s main (P6)' {
        $src = Get-Content (Join-Path $repoRoot 'scripts\factory\Invoke-FlareCheck.ps1') -Raw
        $src | Should Match ([regex]::Escape('show "origin/main:scripts/factory/PatchRules.psd1"'))
        $src.IndexOf('Test-PatchText $patch $rules') | Should BeLessThan $src.IndexOf('"apply", $PatchFile')
    }
}
