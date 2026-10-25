# Pester 3 tests for the paused fix path (factory-runtime spec section 3, step 5, and section 5; P9b): a scripted
# sequence of runs with scripted agents, once with the trial ended and once with the host not picking up checks. A new
# report routed to a fix while paused is parked under factory:queued, a decision:fix report waits, and both are picked
# and fixed once the path is back on. Neither is ever marked "saved fix lost".
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

Describe 'The paused path re-picks both kinds (P9b)' {
    . (Join-Path $here 'RunHarness.ps1')

    function Assert-Sequence([string]$reason) {
        # ---- 1. Paused. A is new, B has the owner's decision:fix, and the open-PR count is below its limit.
        Add-Report 101 @()
        Add-Report 102 @("decision:fix")
        $global:W.openPrs = 0
        # ---- 2. Run 1 triages A to auto-fix and parks it.
        Invoke-Run | Out-Null
        $a = Get-ReportLabelList 101
        "$reason A labels: $($a -join ',')" | Should Be "$reason A labels: problem,triage:ready,route:auto-fix,factory:queued"
        (Get-FactoryComments 101) -join "`n" | Should Match ([regex]::Escape((Format-Copy Parked)))
        @($global:W.jobs | Where-Object kind -eq "fix").Count | Should Be 0
        Get-ReportLabelList 102 | Should Not Be "factory:wip-limit"
        @(Get-ReportLabelList 102) -contains "factory:working" | Should Be $false
        @($global:W.ledger | Where-Object { $_.step -eq "fix-path-paused" }).Count | Should Be 1
        # ---- 3. Run 2, still paused, picks neither, and posts no second pause entry.
        $jobs = $global:W.jobs.Count
        Invoke-Run | Out-Null
        $global:W.jobs.Count | Should Be $jobs
        @($global:W.ledger | Where-Object { $_.step -eq "fix-path-paused" }).Count | Should Be 1
        "$reason A labels: $((Get-ReportLabelList 101) -join ',')" | Should Be "$reason A labels: problem,triage:ready,route:auto-fix,factory:queued"
    }
    function Assert-Resume([string]$reason) {
        # ---- 4. Run 3 picks A through Select-Report's second branch, routes it again, builds and hands off its fix;
        # the host's check passes and the next run finishes it. Run 4 picks B through the third branch and fixes it.
        Invoke-Run | Out-Null
        $fix = Get-Fix 101
        $fix | Should Not BeNullOrEmpty
        @($global:W.jobs | Where-Object kind -eq "triage").Count | Should Be 2
        @($global:W.jobs | Where-Object kind -eq "fix").Count | Should Be 1
        (Get-FactoryComments 101)[-1] | Should Match ([regex]::Escape((Format-Copy WaitingForCheck)))
        @(Get-ReportLabelList 101) -contains "factory:working" | Should Be $true
        Move-Clock 5; Complete-HostCheck "pass" | Should Not BeNullOrEmpty
        Move-Clock 5; Invoke-Run | Out-Null
        Get-Fix 101 | Should BeNullOrEmpty
        (Get-FactoryComments 101)[-1] | Should Match 'passed the tests, review and the Flare check'
        @(Get-ReportLabelList 101) -contains "factory:working" | Should Be $false
        # Run 4: B.
        Move-Clock 5; Invoke-Run | Out-Null
        (Get-Fix 102) | Should Not BeNullOrEmpty
        @($global:W.jobs | Where-Object kind -eq "fix").Count | Should Be 2
        Move-Clock 5; Complete-HostCheck "pass" | Should Not BeNullOrEmpty
        Move-Clock 5; Invoke-Run | Out-Null
        Get-Fix 102 | Should BeNullOrEmpty
        @($global:W.published | Where-Object report -eq 102).Count | Should Be 1   # decision:fix ships as before
        # ---- 5. Neither was ever marked lost.
        foreach ($n in 101, 102) { ((Get-FactoryComments $n) -join "`n") | Should Not Match ([regex]::Escape((Format-Copy SavedFixLost))) }
    }

    It 'with the trial ended' {
        New-World 'trial' | Out-Null
        Set-Trial "expired" "2026-10-30"
        Assert-Sequence "trial"
        # B waits under the paused path: Select-Report gets capLeft 0 and wipFull, and its third branch never runs.
        Set-Trial "active" "2026-12-31"
        Assert-Resume "trial"
    }
    It 'with the host not picking up checks' {
        New-World 'host' | Out-Null
        # Report 100's saved fix: its last request wasn't picked up, and none has been claimed for over two hours.
        Add-Report 100 @("triage:ready", "route:auto-fix", "factory:working")
        Invoke-Run | Out-Null   # nothing to pick yet; makes the run's base
        $Run.base = (& git.exe -C $Run.checkout rev-parse HEAD).Trim()
        $s = New-SavedFix 100 $true ([pscustomobject]@{ fix = [pscustomobject]@{ paths = @("src/App/Ignore.cs") } }) ((New-FixFiles $Run.base)["fix.patch"]) "msg" "{}" "summary" "" @()
        $s.lastOutcome = "not-picked-up"; $s.lastOutcomeAt = $global:W.now.AddMinutes(-30).ToString("o"); $s.pendingSince = $global:W.now.AddHours(-3).ToString("o")
        $s.nextDue = $global:W.now.AddMinutes(30).ToString("o"); Save-FixState $s
        Assert-Sequence "host"
        # The host comes back: the hourly probe is claimed, which turns the path back on, and its check passes.
        Move-Clock 40; Invoke-Run | Out-Null   # the probe goes out
        @(Get-OutboxRequests).Count | Should Be 1
        ((Get-FactoryComments 100) -join "`n") | Should Not Match ([regex]::Escape((Format-Copy NotPickedUp)))
        Complete-HostCheck "pass" | Should Not BeNullOrEmpty
        # The next run sees the claim, which turns the fix path back on in that run: report 100 finishes, and A is
        # picked and fixed.
        Move-Clock 5
        Assert-Resume "host"
        Get-Fix 100 | Should BeNullOrEmpty
    }
}
