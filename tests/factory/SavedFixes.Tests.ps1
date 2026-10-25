# Pester 3 tests for saved fixes and their Flare checks (factory-runtime spec sections 3 and 5; P8), over runs of
# Invoke-Factory.ps1 in a scripted world (RunHarness.ps1): the host's claim and result are files the test writes.
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

Describe 'Saved fixes (P8)' {
    . (Join-Path $here 'RunHarness.ps1')
    # A report whose fix is built and handed off: one run with the fix path on.
    function New-HandedOff([int]$n) {
        Add-Report $n @()
        Invoke-Run | Out-Null
        $f = Get-Fix $n
        if (-not $f) { throw "Report $n has no saved fix." }
        $f
    }
    function Get-FixComments([int]$n) { (Get-FactoryComments $n) -join "`n" }

    It 'six not-picked-up outcomes never send a fix to waiting ($NotPickedUpCountsTowardCap is false)' {
        New-World 'npu' | Out-Null
        $NotPickedUpCountsTowardCap | Should Be $false
        New-HandedOff 201 | Out-Null
        foreach ($i in 1..6) {
            Move-Clock 31; Invoke-Run | Out-Null        # expired by rename: not-picked-up
            $f = Get-Fix 201
            $f.lastOutcome | Should Be "not-picked-up"
            $f.waiting | Should BeNullOrEmpty
            [int]$f.vmToday | Should Be 0; [int]$f.strikes | Should Be 0
            Move-Clock 60; Invoke-Run | Out-Null        # an hour later it's sent again
        }
        @(Get-ChildItem (Join-Path $global:W.root "flare-outbox") -Directory | Where-Object Name -like "*.expired").Count | Should Be 6
    }
    It 'sends nothing while trial.json is expired, unknown or missing, and sends oldest first once licensed' {
        New-World 'trialwait' | Out-Null
        New-HandedOff 211 | Out-Null
        $id = Invoke-HostClaim; Set-HostResult $id "vm" "trial-expired"
        Set-Trial "expired" "2026-10-30"
        Move-Clock 5; Invoke-Run | Out-Null
        $f = Get-Fix 211
        [int]$f.strikes | Should Be 0; [int]$f.vmToday | Should Be 0; $f.waiting | Should BeNullOrEmpty
        (Get-FixComments 211) | Should Match ([regex]::Escape((Format-Copy WaitingTrialEnded)))
        $labels = (Get-ReportLabelList 211) -join ','
        foreach ($state in "unknown", "missing") {
            Set-Trial $state $null
            Move-Clock 90; Invoke-Run | Out-Null
            @(Get-OutboxRequests).Count | Should Be 0
        }
        ([regex]::Matches((Get-FixComments 211), [regex]::Escape((Format-Copy WaitingTrialEnded)))).Count | Should Be 1
        (Get-FixComments 211) | Should Match ([regex]::Escape((Format-Copy WaitingStateUnknown)))
        (Get-ReportLabelList 211) -join ',' | Should Be $labels   # plain comments: no label change
        # A second saved fix, younger; once licensed, the older goes first.
        $global:W.now = $global:W.now.AddMinutes(1)
        Set-Trial "licensed" $null
        Add-Report 212 @(); Invoke-Run | Out-Null   # 211 is due and goes out; 212 is built and waits its turn
        $sent = @(Get-OutboxRequests)
        $sent.Count | Should Be 1
        (Get-Fix 211).checkId | Should Be $sent[0]
        (Get-Fix 212).checkId | Should BeNullOrEmpty
    }
    It 'two hours of not-picked-up with no claim turn the path off for the host, and the first claim turns it back on' {
        New-World 'stall' | Out-Null
        New-HandedOff 221 | Out-Null
        Move-Clock 31; Invoke-Run | Out-Null                    # not picked up: "Not picked up" is posted, the path is still on
        (Get-FixComments 221) | Should Match ([regex]::Escape((Format-Copy NotPickedUp)))
        foreach ($i in 1..3) { Move-Clock 30; Invoke-Run | Out-Null; Move-Clock 31; Invoke-Run | Out-Null }
        $Run.fixPath.on | Should Be $false
        $Run.fixPath.reason | Should Be "host"
        ([regex]::Matches((Get-FixComments 221), [regex]::Escape((Format-Copy NotPickedUp)))).Count | Should BeLessThan 4   # probes post nothing once paused
        (Get-FixComments 221) | Should Match ([regex]::Escape((Format-Copy WaitingHost)))
        # A new fix route is parked, and the probe still goes out each hour.
        Add-Report 222 @(); Move-Clock 30; Invoke-Run | Out-Null
        (Get-ReportLabelList 222) -contains "factory:queued" | Should Be $true
        Move-Clock 30; Invoke-Run | Out-Null
        @(Get-OutboxRequests).Count | Should Be 1
        Invoke-HostClaim | Should Not BeNullOrEmpty
        Move-Clock 5; Invoke-Run | Out-Null
        $Run.fixPath.on | Should Be $true
    }
    It 'resumes a fix waiting after three strikes on the owner''s newer decision:fix, and nothing else' {
        New-World 'resume' | Out-Null
        New-HandedOff 231 | Out-Null
        foreach ($i in 1..3) {
            $id = Invoke-HostClaim; Set-HostResult $id "vm" "no-start"
            Move-Clock 5; Invoke-Run | Out-Null
            if ($i -lt 3) { Move-Clock 60; Invoke-Run | Out-Null }
        }
        $f = Get-Fix 231
        $f.waiting.why | Should Be "vm"
        (Get-ReportLabelList 231) -join ',' | Should Match 'factory:stuck'
        (Get-ReportLabelList 231) -contains "blocked:vm" | Should Be $true
        (Get-FixComments 231) | Should Match ([regex]::Escape((Format-Copy WaitingForYou)))
        # Someone else's decision:fix changes nothing.
        Move-Clock 10; Add-OwnerLabel 231 "decision:fix" "someone-else"; Invoke-Run | Out-Null
        (Get-Fix 231).waiting.why | Should Be "vm"
        # The owner's resumes it.
        Move-Clock 10; Add-OwnerLabel 231 "decision:fix"; Invoke-Run | Out-Null
        $f = Get-Fix 231
        $f.waiting | Should BeNullOrEmpty; [int]$f.strikes | Should Be 0
        (Get-ReportLabelList 231) -contains "factory:working" | Should Be $true
        (Get-ReportLabelList 231) -contains "blocked:vm" | Should Be $false
        (Get-FixComments 231) | Should Match ([regex]::Escape((Format-Copy Resumed)))
        @(Get-OutboxRequests).Count | Should Be 1   # sent at step 9
    }
    It 'an owner decision:fix older than the waiting time does nothing' {
        New-World 'older' | Out-Null
        New-HandedOff 232 | Out-Null
        Add-OwnerLabel 232 "decision:fix"
        Move-Clock 1
        $f = Get-Fix 232; $f.waiting = [pscustomobject]@{ why = "vm"; since = $global:W.now.ToString("o") }; $f.checkId = $null; Save-FixState $f
        Move-Clock 5; Invoke-Run | Out-Null
        (Get-Fix 232).waiting.why | Should Be "vm"
    }
    It 'decision:fix on an out-of-date fix changes nothing, and posts Retry needed once' {
        New-World 'stale' | Out-Null
        New-HandedOff 241 | Out-Null
        $f = Get-Fix 241; $f.waiting = [pscustomobject]@{ why = "stale"; since = $global:W.now.ToString("o") }; $f.checkId = $null; Save-FixState $f
        Set-ReportLabels 241 @("factory:stuck", "waiting:stuck", "blocked:other") @("factory:working")
        Move-Clock 5; Add-OwnerLabel 241 "decision:fix"
        Invoke-Run | Out-Null; Move-Clock 5; Invoke-Run | Out-Null
        (Get-Fix 241).waiting.why | Should Be "stale"
        (Get-ReportLabelList 241) -contains "blocked:other" | Should Be $true
        ([regex]::Matches((Get-FixComments 241), [regex]::Escape((Format-Copy RetryNeeded)))).Count | Should Be 1
    }
    It 'Retry deletes the saved fix before the pick, and the same run picks and fixes the report again' {
        New-World 'retry' | Out-Null
        New-HandedOff 251 | Out-Null
        $f = Get-Fix 251; $f.waiting = [pscustomobject]@{ why = "vm"; since = $global:W.now.ToString("o") }; $f.checkId = $null; Save-FixState $f
        Get-OutboxRequests | ForEach-Object { Remove-Item (Join-Path $global:W.root "flare-outbox\$_") -Recurse }
        # The triage skill's Retry: the comment, and the labels back to a new report.
        Move-Clock 5; Add-OwnerComment 251 "**Retry triage**"
        foreach ($l in @(Get-ReportLabelList 251)) { if ($l -ne "problem") { Set-ReportLabels 251 @() @($l) } }
        $before = @($global:W.jobs | Where-Object kind -eq "fix").Count
        Invoke-Run | Out-Null
        @($global:W.jobs | Where-Object kind -eq "fix").Count | Should Be ($before + 1)
        $g = Get-Fix 251
        $g | Should Not BeNullOrEmpty
        $g.waiting | Should BeNullOrEmpty
        $g.createdAt | Should Not Be $f.createdAt
    }
    It 'continues a pass only after the fetch and restore, with this run''s cache and bundle, and leaves the checkout on the base' {
        New-World 'order' | Out-Null
        $old = (New-HandedOff 261).base
        Add-MainCommit | Out-Null                       # main moves, no package change
        Complete-HostCheck "pass" | Out-Null
        $global:W.calls.Clear(); $global:W.jobs.Clear()
        Move-Clock 5; Invoke-Run | Out-Null
        $calls = @($global:W.calls)
        $restore = [array]::IndexOf($calls, @($calls | Where-Object { $_ -like "restore:*" })[0])
        $firstJob = [array]::IndexOf($calls, @($calls | Where-Object { $_ -like "job:test:*" })[0])
        $restore | Should BeLessThan $firstJob
        $tests = @($global:W.jobs | Where-Object kind -eq "test")
        $tests[0].base | Should Be $old                  # on its saved base, from this run's bundle
        $tests[0].cache | Should Be $Run.cache
        Test-Path (Join-Path $global:W.root "agent-in\base-$($Run.base).bundle") | Should Be $true
        (& git.exe -C $Run.checkout rev-parse HEAD).Trim() | Should Be $Run.base
        @(& git.exe -C $Run.patchWork status --porcelain).Count | Should Be 0
        (& git.exe -C $Run.patchWork rev-parse HEAD).Trim() | Should Be $Run.base
        $Run.rules.autoFix.paths | Should Not BeNullOrEmpty   # read from the run's base
        $Run.patchRules.MaxLines | Should Be 800
        Get-Fix 261 | Should BeNullOrEmpty                     # routed and finished
    }
    It 'main''s packages changed and the check passed: the fix moves to the new base, is checked and reviewed again, and goes out again' {
        New-World 'move' | Out-Null
        $a = (New-HandedOff 271).base
        $b = Add-MainCommit -PackageChange
        Complete-HostCheck "pass" | Out-Null
        $global:W.jobs.Clear()
        Move-Clock 5; Invoke-Run | Out-Null
        $f = Get-Fix 271
        $f.base | Should Be $b
        [int]$f.moves | Should Be 1
        @($global:W.jobs | Where-Object kind -eq "fix").Count | Should Be 0
        @($global:W.jobs | Where-Object kind -eq "review").Count | Should Be 1
        @($global:W.jobs | Where-Object { $_.kind -eq "test" -and $_.base -eq $b }).Count | Should BeGreaterThan 0
        (Get-FixComments 271) | Should Match ([regex]::Escape((Format-Copy Moved)))
        $req = @(Get-OutboxRequests); $req.Count | Should Be 1
        (Get-Content (Join-Path $global:W.root "flare-outbox\$($req[0])\request.json") -Raw | ConvertFrom-Json).base | Should Be $b
        @($global:W.published).Count | Should Be 0   # the old pass isn't used
    }
    It 'main''s packages changed and the result was holdout or fix: it routes as it came back, on its saved base' {
        New-World 'nomove' | Out-Null
        $a = (New-HandedOff 281).base
        Add-MainCommit -PackageChange | Out-Null
        Complete-HostCheck "holdout" | Out-Null
        Move-Clock 5; Invoke-Run | Out-Null
        Get-Fix 281 | Should BeNullOrEmpty
        (Get-FixComments 281) | Should Match ([regex]::Escape((Format-Copy HoldoutReport)))
        (Get-ReportLabelList 281) -contains "factory:needs-decision" | Should Be $true
        (Get-FixComments 281) | Should Not Match ([regex]::Escape((Format-Copy Moved)))
        Add-Report 282 @(); Invoke-Run | Out-Null
        $id = Invoke-HostClaim; Set-HostResult $id "fix" $null @("reopen-issues")
        Move-Clock 5; Invoke-Run | Out-Null
        (Get-FixComments 282) | Should Match ([regex]::Escape((Format-Copy RoutePublicScenarioFails)))
        (Get-ReportLabelList 282) -contains "route:fix-for-review" | Should Be $true
    }
    It 'a fix that waited through a paused path moves before its first check, so only one check goes out' {
        New-World 'waitmove' | Out-Null
        Set-Trial "active" "2026-12-31"
        Add-Report 291 @(); Invoke-Run | Out-Null
        # Its request was never claimed; the trial then ended.
        Get-OutboxRequests | ForEach-Object { Remove-Item (Join-Path $global:W.root "flare-outbox\$_") -Recurse }
        $f = Get-Fix 291; $f.checkId = $null; $f.nextDue = $global:W.now.ToString("o"); Save-FixState $f
        Set-Trial "expired" "2026-10-30"; Move-Clock 5; Invoke-Run | Out-Null
        $b = Add-MainCommit -PackageChange
        Set-Trial "active" "2026-12-31"; Move-Clock 5; Invoke-Run | Out-Null
        $f = Get-Fix 291
        $f.base | Should Be $b; [int]$f.moves | Should Be 1
        @(Get-OutboxRequests).Count | Should Be 1
    }
    It 'main moved with no package change: the continuation stays on its base and routes with the result it has' {
        New-World 'samepkg' | Out-Null
        $a = (New-HandedOff 301).base
        Add-MainCommit | Out-Null
        Complete-HostCheck "pass" | Out-Null
        $global:W.jobs.Clear(); Move-Clock 5; Invoke-Run | Out-Null
        @($global:W.jobs | Where-Object { $_.kind -eq "test" }) | ForEach-Object { $_.base | Should Be $a }
        @($global:W.jobs | Where-Object { $_.kind -eq "review" }).Count | Should Be 0
        (Get-FixComments 301) | Should Match 'passed the tests, review and the Flare check'
    }
    It 'a patch that conflicts on the new base, a failed review, or a third move leave it out of date, with no strike' {
        New-World 'stalemove' | Out-Null
        New-HandedOff 311 | Out-Null
        $global:W.reviewVerdict = "BLOCK"
        Add-MainCommit -PackageChange | Out-Null
        Complete-HostCheck "pass" | Out-Null
        Move-Clock 5; Invoke-Run | Out-Null
        $f = Get-Fix 311
        $f.waiting.why | Should Be "stale"; [int]$f.strikes | Should Be 0
        (Get-FixComments 311) | Should Match ([regex]::Escape("I couldn't finish this one: " + (Format-Copy SavedFixStale)))
        (Get-ReportLabelList 311) -contains "blocked:other" | Should Be $true
        (Get-ReportLabelList 311) -contains "factory:working" | Should Be $false
        Get-ReasonCodes @((Format-Copy SavedFixStale)) | Should Be "saved-fix-stale"
        # A third move.
        $global:W.reviewVerdict = "MERGE"
        Add-Report 312 @(); Invoke-Run | Out-Null
        $g = Get-Fix 312; $g.moves = 2; Save-FixState $g
        Add-MainCommit -PackageChange | Out-Null
        Get-OutboxRequests | ForEach-Object { Remove-Item (Join-Path $global:W.root "flare-outbox\$_") -Recurse }
        $g = Get-Fix 312; $g.checkId = $null; $g.nextDue = $global:W.now.ToString("o"); Save-FixState $g
        Move-Clock 5; Invoke-Run | Out-Null
        (Get-Fix 312).waiting.why | Should Be "stale"
    }
    It 'an unreadable result posts Result unreadable and is retried as a vm outcome' {
        New-World 'unread' | Out-Null
        New-HandedOff 321 | Out-Null
        $id = Invoke-HostClaim
        [IO.File]::WriteAllText((Join-Path $global:W.root "flare-inbox\$id\result.json"), '{"check":"' + $id + '","outcome":"pass","trial":"expired"}')
        Move-Clock 5; Invoke-Run | Out-Null
        $f = Get-Fix 321
        $f.lastOutcome | Should Be "unreadable"; [int]$f.vmToday | Should Be 1; [int]$f.strikes | Should Be 0
        (Get-FixComments 321) | Should Match ([regex]::Escape((Format-Copy ResultUnreadable)))
        Move-Clock 61; Invoke-Run | Out-Null
        @(Get-OutboxRequests).Count | Should Be 1
    }
    It 'a claimed request with no progress isn''t expired, and times out 210 minutes after its send time' {
        New-World 'noprog' | Out-Null
        New-HandedOff 331 | Out-Null
        $id = @(Get-OutboxRequests)[0]
        Rename-Item (Join-Path $global:W.root "flare-outbox\$id") "$id.claimed"     # claimed, but no progress.json and no result
        Move-Clock 209; Invoke-Run | Out-Null
        $f = Get-Fix 331
        $f.checkId | Should Be $id
        $f.lastOutcome | Should BeNullOrEmpty
        Test-Path (Join-Path $global:W.root "flare-outbox\$id.expired") | Should Be $false
        Move-Clock 1; Invoke-Run | Out-Null
        $f = Get-Fix 331
        $f.lastOutcome | Should Be "no-result"; $f.checkId | Should BeNullOrEmpty
        Move-Clock 60; Invoke-Run | Out-Null
        @(Get-OutboxRequests).Count | Should Be 1                                       # the next check goes out
    }
    It 'times no-result from the host''s latest progress, and counts a late result until the next send' {
        New-World 'progress' | Out-Null
        New-HandedOff 341 | Out-Null
        $id = Invoke-HostClaim
        Move-Clock 200; Set-HostProgress $id
        Move-Clock 100; Invoke-Run | Out-Null
        (Get-Fix 341).lastOutcome | Should BeNullOrEmpty                              # 100 minutes since its progress
        Move-Clock 111; Invoke-Run | Out-Null
        (Get-Fix 341).lastOutcome | Should Be "no-result"
        Test-Path (Join-Path $global:W.root "flare-inbox\$id") | Should Be $true      # the inbox folder stays
        Set-HostResult $id "pass"
        Move-Clock 5; Invoke-Run | Out-Null
        Get-Fix 341 | Should BeNullOrEmpty                                              # the late result counted
    }
    It 'ignores a .tmp- result, and reads the renamed result.json on the next run' {
        New-World 'tmp' | Out-Null
        New-HandedOff 351 | Out-Null
        $id = Invoke-HostClaim; Set-HostResult $id "pass" -LeaveTmp
        Move-Clock 5; Invoke-Run | Out-Null
        (Get-Fix 351).checkId | Should Be $id
        Move-Item (Join-Path $global:W.root "flare-inbox\$id\.tmp-result.json") (Join-Path $global:W.root "flare-inbox\$id\result.json")
        Move-Clock 5; Invoke-Run | Out-Null
        Get-Fix 351 | Should BeNullOrEmpty
    }
    It 'one check at a time, expired by rename; a working report with no saved fix is lost; a usage stop pauses new work' {
        New-World 'misc' | Out-Null
        New-HandedOff 361 | Out-Null
        Add-Report 362 @(); Invoke-Run | Out-Null
        @(Get-OutboxRequests).Count | Should Be 1
        (Get-Fix 362).checkId | Should BeNullOrEmpty
        Add-Report 363 @("triage:ready", "factory:working")
        Move-Clock 5; Invoke-Run | Out-Null
        (Get-FixComments 363) | Should Match ([regex]::Escape((Format-Copy SavedFixLost)))
        (Get-ReportLabelList 363) -contains "blocked:vm" | Should Be $true
        [void]$global:W.ledger.Add([pscustomobject]@{ at = $global:W.now.ToString("yyyy-MM-ddTHH:mm:ssZ"); step = "fix"; stopped = "usage-limit" })
        Add-Report 364 @(); $jobs = $global:W.jobs.Count
        Move-Clock 1; Invoke-Run | Out-Null
        @($global:W.jobs | Select-Object -Skip $jobs | Where-Object kind -eq "triage").Count | Should Be 0
    }
}
