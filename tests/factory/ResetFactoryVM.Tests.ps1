<#
Pester 3 tests for Reset-FactoryVM.ps1 (factory-runtime spec sections 8, 9 and 11; P16): no base checkpoint stops before
anything, the agents VM's nightly reset in order with the carry's name rules and the held writes before carry-done,
-Resume from either side of the restore, and the Flare VM's reset waiting on the check lock. Hyper-V and the VMs are
scripted; the agents VM is a folder in TestDrive.
  powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
#>
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $here '..\..')

Describe 'Reset-FactoryVM.ps1 (P16)' {
    $reset = Join-Path $repoRoot 'scripts\factory\Reset-FactoryVM.ps1'
    function New-Root([string]$label) { $v = Join-Path $TestDrive $label; New-Item -ItemType Directory -Force $v | Out-Null; $v }
    function Start-World {
        Set-AgentsWriteLockName ("Local\hs-test-" + [guid]::NewGuid().ToString("N")); Set-AgentsWriteLockWait 2
        $global:Ops = New-Object System.Collections.ArrayList
        $global:Clock = [datetime]::new(2026, 11, 2, 3, 30, 0, [DateTimeKind]::Utc)
        $global:HasBase = $true; $global:Carry = @(); $global:FailRestore = $false
        # Writes from inside the module (Write-AgentsVM). The script re-imports the module, so this lasts one test.
        & (Get-Module FactoryVM) { function script:Invoke-VMCall([string]$vm, [scriptblock]$block, [object[]]$argv = @(), [int]$timeoutSeconds = 60) {
            $path = ("" + $argv[0]); [void]$global:Ops.Add("write:" + $path.Replace("C:\HouseStyle\", ""))
            [pscustomobject]@{ ok = $true; timedOut = $false; output = @("written"); error = $null; errorId = $null }
        } }
    }
    # The script's own seams, answered here; dot-sourced after the script, which defines some of them itself.
    $Seams = {
    function Get-HostNow { $global:Clock }
    function Wait-Briefly([int]$s) { $global:Clock = $global:Clock.AddSeconds($s) }
    function Test-Checkpoint([string]$vm, [string]$n) { [void]$global:Ops.Add("test:${vm}:$n"); $global:HasBase }
    function Stop-VMHard([string]$vm) { [void]$global:Ops.Add("off:$vm") }
    function Start-VMPlain([string]$vm) { [void]$global:Ops.Add("start:$vm") }
    function Restore-Checkpoint([string]$vm, [string]$n) { if ($global:FailRestore) { throw "restore failed" }; [void]$global:Ops.Add("restore:${vm}:$n") }
    function New-Checkpoint([string]$vm, [string]$n) { [void]$global:Ops.Add("checkpoint:${vm}:$n") }
    function Get-VMStateName([string]$vm) { "Off" }
    function Get-RunnerState([string]$vm, [string]$repo) { "idle" }
    function Invoke-VMCall([string]$vm, [scriptblock]$block, [object[]]$argv = @(), [int]$timeoutSeconds = 60) {
        $ok = [pscustomobject]@{ ok = $true; timedOut = $false; output = @(); error = $null }
        if ($block -eq $CarryReadBlock) { [void]$global:Ops.Add("carry-out"); $ok.output = $global:Carry; return $ok }
        if ($block -eq $StopRunnerService) { [void]$global:Ops.Add("runner-stop"); return $ok }
        if ($block -eq $StartRunnerService) { [void]$global:Ops.Add("runner-start"); return $ok }
        if ($block -eq $WriteBlock) { [void]$global:Ops.Add("carry-in:" + ("" + $argv[0]).Replace("C:\HouseStyle\", "")); return $ok }
        $ok.output = @("up"); $ok
    }
    }
    function B([string]$s) { , [Text.Encoding]::UTF8.GetBytes($s) }

    It 'with no base checkpoint stops before setting a flag or touching the VM' {
        foreach ($vm in "HSAGENTS", "HSFACTORY") {
            $v = New-Root "nobase-$vm"; . $reset -Name $vm -Root $v; . $Seams; Start-World; $global:HasBase = $false
            Invoke-Reset | Should Be "no-base"
            Test-Path (Get-FlagPath $vm) | Should Be $false
            @($global:Ops | Where-Object { $_ -notlike "test:*" }).Count | Should Be 0
            (Get-Content (Get-HostPath "host.log") -Raw) | Should Match "has no (agents|factory)-base checkpoint yet"
        }
    }
    It 'resets the agents VM in order: flag, quiesce, carry-out, restore, carry-in, held writes, carry-done last, flag cleared' {
        $v = New-Root "agents"; . $reset -Name HSAGENTS -Root $v; . $Seams; Start-World
        $id = [guid]::NewGuid().ToString()
        $global:Carry = @("fix-state\12\state.json", (B '{}'), "flare-outbox\$id.claimed\request.json", (B '{}'), "factory-logs\run-1\run.log", (B 'x'))
        Save-HeldWrite "flare-inbox\$id\result.json" ([byte[]](1)) $id | Out-Null
        Invoke-Reset | Should Be "done"
        $ops = @($global:Ops)
        $i = { param($o) [array]::IndexOf($ops, $o) }
        (& $i "runner-stop") | Should BeLessThan (& $i "carry-out")
        (& $i "carry-out") | Should BeLessThan (& $i "restore:HSAGENTS:agents-base")
        (& $i "restore:HSAGENTS:agents-base") | Should BeLessThan (& $i "carry-in:fix-state\12\state.json")
        (& $i "carry-in:flare-outbox\$id.claimed\request.json") | Should BeLessThan (& $i "write:flare-inbox\$id\result.json")
        $ops[-1] | Should Be "write:carry-done"
        Test-Path (Get-FlagPath HSAGENTS) | Should Be $false
        @(Get-HeldWrites).Count | Should Be 0
        Test-Path (Get-HostPath "factory-logs\2026-11-02\run-1\run.log") | Should Be $true
        $ops -contains "off:HSFACTORY" | Should Be $false
    }
    It 'carries only names its rules take' {
        $v = New-Root "names"; . $reset -Name HSAGENTS -Root $v; . $Seams; Start-World
        $ok = "fix-state\12\fix.patch", "flare-outbox\$([guid]::NewGuid())\fix.patch", "flare-inbox\trial.json", "flare-inbox\$([guid]::NewGuid())\result.json", "factory-logs\a\b.log"
        $bad = "fix-state\12\evil.ps1", "fix-state\..\x", "flare-outbox\abc\request.json", "flare-inbox\other.json", "factory-logs\CON\x.log", "factory-logs\.hidden", "other\x.txt", "fix-state"
        foreach ($p in $ok) { Test-CarryPath $p | Should Be $true }
        foreach ($p in $bad) { Test-CarryPath $p | Should Be $false }
        $global:Carry = @("fix-state\12\evil.ps1", (B 'x'), "fix-state\12\state.json", (B '{}'))
        Save-CarryOut (Join-Path $v "carry")
        Test-Path (Join-Path $v "carry\fix-state\12\evil.ps1") | Should Be $false
        Test-Path (Join-Path $v "carry\fix-state\12\state.json") | Should Be $true
    }
    It 'a reset that stops leaves its flag, and -Resume finishes it from after the restore' {
        $v = New-Root "stops"; . $reset -Name HSAGENTS -Root $v; . $Seams; Start-World
        $global:Carry = @("fix-state\7\state.json", (B '{}'))
        $global:FailRestore = $true
        { Invoke-Reset } | Should Throw "restore failed"
        (Get-MaintenanceFlag HSAGENTS).step | Should Be "carried-out"
        # Pretend the restore did happen before the stop.
        Update-MaintenanceStep HSAGENTS "restored"
        $v2 = New-Root "stops"; . $reset -Name HSAGENTS -Root $v2 -Resume; . $Seams; Start-World
        Invoke-Reset | Should Be "resumed"
        $global:Ops -contains "start:HSAGENTS" | Should Be $true
        $global:Ops -contains "carry-in:fix-state\7\state.json" | Should Be $true
        $global:Ops[-1] | Should Be "write:carry-done"
        Test-Path (Get-FlagPath HSAGENTS) | Should Be $false
    }
    It '-Resume from before the restore restores nothing, writes carry-done and starts the runner again' {
        $v = New-Root "before"; . $reset -Name HSAGENTS -Root $v -Resume; . $Seams; Start-World
        Set-MaintenanceFlag HSAGENTS "quiesced"
        Invoke-Reset | Should Be "resumed"
        @($global:Ops | Where-Object { $_ -like "restore:*" -or $_ -like "carry-in:*" }).Count | Should Be 0
        $global:Ops -contains "write:carry-done" | Should Be $true
        $global:Ops[-1] | Should Be "runner-start"
        Test-Path (Get-FlagPath HSAGENTS) | Should Be $false
        Invoke-Reset | Should Be "nothing"
    }
    It 'the Flare VM''s reset skips the night when a check holds the lock for 4 hours, and clears its flag' {
        $v = New-Root "flarebusy"; . $reset -Name HSFACTORY -Root $v; . $Seams; Start-World
        Write-HostJson (Get-HostPath "flare-check.lock") ([pscustomobject]@{ pid = $PID; start = "x"; check = "c"; step = 6 })
        Invoke-Reset | Should Be "skipped"
        Test-Path (Get-FlagPath HSFACTORY) | Should Be $false
        @($global:Ops | Where-Object { $_ -like "restore:*" }).Count | Should Be 0
    }
    It 'the Flare VM''s reset restores factory-base and never touches the agents VM' {
        $v = New-Root "flare"; . $reset -Name HSFACTORY -Root $v; . $Seams; Start-World
        Invoke-Reset | Should Be "done"
        $global:Ops -contains "restore:HSFACTORY:factory-base" | Should Be $true
        @($global:Ops | Where-Object { $_ -like "*HSAGENTS*" }).Count | Should Be 0
        Test-Path (Get-FlagPath HSFACTORY) | Should Be $false
    }
    It 'the Flare VM''s -Refresh reads the license state after re-checkpointing flare-check-base, and delivers it' {
        $v = New-Root "refresh"; . $reset -Name HSFACTORY -Root $v -Refresh; . $Seams; Start-World
        function Get-VMCredential([string]$n) { "cred" }
        function New-PSSession { [void]$global:Ops.Add("session"); if (@($global:Ops | Where-Object { $_ -eq "session" }).Count -gt 1) { throw "stop at the runner copy" }; "s" }
        function Remove-PSSession { }
        function Copy-Item { [void]$global:Ops.Add("copy-runner") }
        function Protect-AdminOnly([string]$p) { }
        function Invoke-VMCall([string]$vm, [scriptblock]$block, [object[]]$argv = @(), [int]$timeoutSeconds = 60) {
            if ($block.ToString() -match 'TrialRead 2>&1') { [void]$global:Ops.Add("trial-read"); return [pscustomobject]@{ ok = $true; output = @("22.1", "trial text") } }
            [pscustomobject]@{ ok = $true; output = @($true) }
        }
        function ConvertFrom-FlareTrialRead([string]$text) { [pscustomobject]@{ state = "active"; endDate = "2027-01-31" } }
        { Invoke-Reset } | Should Throw "stop at the runner copy"
        $ops = @($global:Ops)
        [array]::IndexOf($ops, "checkpoint:HSFACTORY:flare-check-base") | Should BeLessThan ([array]::IndexOf($ops, "trial-read"))
        [array]::LastIndexOf($ops, "restore:HSFACTORY:flare-check-base") | Should BeLessThan ([array]::IndexOf($ops, "trial-read"))
        (Get-TrialState).state | Should Be "active"; (Get-TrialState).endDate | Should Be "2027-01-31"
        $ops -contains "write:flare-inbox\trial.json" | Should Be $true
        Test-Path (Get-FlagPath HSFACTORY) | Should Be $false
    }
}