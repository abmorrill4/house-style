# Pester 3 tests for the factory-runtime parsers and codes (spec sections 3, 5 and 11): blocked causes and reason codes
# for every string (P9), the Flare check's result and trial.json (P10), and the git config scopes (P13).
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $root 'scripts\factory\Triage.psm1') -Force
Import-Module (Join-Path $root 'scripts\factory\Controls.psm1') -Force
Import-Module (Join-Path $here 'Fixtures.psm1') -Force

Describe 'Blocked causes and reason codes for every string (P9)' {
    # Today's strings, built from parts so the old VM name text check (P15) doesn't find them here.
    $old = "factory " + "VM"
    It 'today''s posted strings keep their old codes' {
        $s = "Flare failed to start or stopped responding during the end-to-end checks, twice. That points at the $old rather than the fix; rerun the report once the VM is healthy."
        Get-ReasonCodes @($s) | Should Be "vm"; Get-BlockedCause $s | Should Be "vm"
        $s = "The end-to-end run stopped before any scenario ran (setup, install or the FlaUI restore). See report-5-e2e.log on the $old."
        Get-ReasonCodes @($s) | Should Be "e2e"; Get-BlockedCause $s | Should Be "vm"
        Get-ReasonCodes @("End-to-end checks fail on the fix.") | Should Be "e2e"
        Get-ReasonCodes @("A holdout scenario fails on the fix.") | Should Be "holdout"
        Get-ReasonCodes @("Unit tests fail on the fix.") | Should Be "unit-tests"
        Get-ReasonCodes @("Review blocked the fix: x") | Should Be "review-blocked"
        Get-BlockedCause "Required checks failed on https://x: fail:build" | Should Be "flaky-test"
    }
    It 'maps the new failure strings to their codes and causes' {
        $cases = @(
            @{ key = "UsageLimitReport"; code = "usage-limit"; cause = "other" }
            @{ key = "AgentOutputRefused"; code = "agent-output"; cause = "other" }
            @{ key = "ResultUnreadable"; code = "flare-result"; cause = "vm" }
            @{ key = "NotPickedUp"; code = "vm"; cause = "vm" }
            @{ key = "InUse"; code = "vm"; cause = "vm" }
            @{ key = "Busy"; code = "vm"; cause = "vm" }
            @{ key = "Interrupted"; code = "vm"; cause = "vm" }
            @{ key = "CheckCouldntRun"; code = "vm"; cause = "vm" }
            @{ key = "WaitingForYou"; code = "vm"; cause = "vm" }
            @{ key = "SweepFailed"; code = "vm"; cause = "vm" }
            @{ key = "RestoreLeftOpen"; code = "vm"; cause = "vm" }
            @{ key = "CacheHadLink"; code = "vm"; cause = "vm" }
            @{ key = "TaskDidntStop"; code = "vm"; cause = "vm" }
            @{ key = "LaunchFailed"; code = "vm"; cause = "vm" }
            @{ key = "SavedFixLost"; code = "vm"; cause = "vm" }
            @{ key = "SavedFixStale"; code = "saved-fix-stale"; cause = "other" }
            @{ key = "HoldoutReport"; code = "holdout"; cause = "other" }
            @{ key = "RoutePublicScenarioFails"; code = "e2e"; cause = "other" }
        )
        foreach ($c in $cases) {
            $s = $FactoryCopy[$c.key]
            "$($c.key) $(Get-ReasonCodes @($s))" | Should Be "$($c.key) $($c.code)"
            "$($c.key) $(Get-BlockedCause ("I couldn't finish this one: " + $s))" | Should Be "$($c.key) $($c.cause)"
        }
        Get-ReasonCodes @((Format-Copy RouteAutoFixPaths @{ paths = "tests/agent/x.Tests.ps1" })) | Should Be "auto-fix-paths"
        Get-ReasonCodes @("the fix changes files auto-fix doesn't cover (its changed paths weren't listed)") | Should Be "auto-fix-paths"
    }
    It 'the waiting, ledger and resume strings aren''t failures: no code matches and none reads as vm' {
        foreach ($k in "WaitingTrialEnded", "WaitingLicenseLapsed", "WaitingStateUnknown", "WaitingHost", "PausedTrial", "PausedLicense", "PausedUnknown", "PausedHost",
                       "Moved", "Resumed", "RetryNeeded", "WaitingForCheck", "Parked", "LiveWithoutLicense") {
            $s = $FactoryCopy[$k]
            "$k $(Get-ReasonCodes @($s))" | Should Be "$k other"
            "$k $(Get-BlockedCause $s)" | Should Not Be "$k vm"
            $s | Should Not Match 'rather than the fix'
        }
        "$k $(Get-BlockedCause (Format-Copy TrialWarning @{ date = '2026-11-20' }))" | Should Not Match ' vm$'
    }
    It 'the vm pattern is exactly the spec''s, and today''s unposted phrase is gone' {
        $src = Get-Content (Join-Path $root 'scripts\factory\Controls.psm1') -Raw
        $src | Should Match ([regex]::Escape("'(VM|host) rather than the fix|VM not ready|end-to-end run stopped before any scenario ran'"))
        Get-BlockedCause "Agents VM not ready. No carry-done." | Should Be "vm"
        Get-BlockedCause ("The " + $old + " isn't ready") | Should Be "other"
    }
    It 'eight codes sit after unit-tests, in the spec''s order' {
        $keys = @($ReasonCodes.Keys)
        $i = $keys.IndexOf("unit-tests")
        ($keys[($i + 1)..($i + 8)]) -join ',' | Should Be "usage-limit,agent-output,flare-result,holdout,e2e,vm,auto-fix-paths,saved-fix-stale"
    }
    It 'every string has its slot filled' {
        Format-Copy TrialWarning @{ date = "2026-11-20" } | Should Be "Flare's trial on the Flare VM ends on 2026-11-20. Add a Flare license to keep Flare checks running and to let the factory go live."
        { Format-Copy NoSuchString } | Should Throw
    }
}

Describe 'Read-FlareCheckResult (P10)' {
    $id = "6f1c0a52-3c1e-4a8e-9a39-1b2a3c4d5e6f"
    $sc = @("reopen-issues", "rules-edit", "new-from-patch")
    function Read-Result($o, $trial = "active") { ConvertFrom-FlareCheckResult ($o | ConvertTo-Json -Compress) $id $trial $sc }
    It 'reads each outcome' {
        (Read-Result @{ check = $id; outcome = "pass"; failed = @(); retried = @("reopen-issues") }).result.retried | Should Be "reopen-issues"
        (Read-Result @{ check = $id; outcome = "fix"; failed = @("rules-edit", "new-from-patch") }).result.failed.Count | Should Be 2
        (Read-Result @{ check = $id; outcome = "holdout" }).result.outcome | Should Be "holdout"
        (Read-Result @{ check = $id; outcome = "vm"; vmKind = "busy" }).result.vmKind | Should Be "busy"
    }
    It 'accepts trial-expired only when the host''s trial.json is expired, and trial-unknown only when it''s unknown' {
        (Read-Result @{ check = $id; outcome = "vm"; vmKind = "trial-expired" } "expired").ok | Should Be $true
        (Read-Result @{ check = $id; outcome = "vm"; vmKind = "trial-expired" } "active").ok | Should Be $false
        (Read-Result @{ check = $id; outcome = "vm"; vmKind = "trial-expired" } "licensed").ok | Should Be $false
        (Read-Result @{ check = $id; outcome = "vm"; vmKind = "trial-unknown" } "unknown").ok | Should Be $true
        (Read-Result @{ check = $id; outcome = "vm"; vmKind = "trial-unknown" } "expired").ok | Should Be $false
    }
    It 'refuses a trial field, any extra field, another check''s id, and unknown values' {
        (Read-Result @{ check = $id; outcome = "pass"; trial = "expired" }).why | Should Match "field it shouldn't \(trial\)"
        (Read-Result @{ check = $id; outcome = "pass"; note = "x" }).ok | Should Be $false
        (Read-Result @{ check = "other"; outcome = "pass" }).ok | Should Be $false
        (Read-Result @{ check = $id; outcome = "maybe" }).ok | Should Be $false
        (Read-Result @{ check = $id; outcome = "vm"; vmKind = "gremlins" }).ok | Should Be $false
        (Read-Result @{ check = $id; outcome = "pass"; vmKind = "busy" }).ok | Should Be $false
    }
    It 'refuses names that aren''t base or patch scenarios, bad names and long lists' {
        (Read-Result @{ check = $id; outcome = "fix"; failed = @("holdout-secret") }).ok | Should Be $false
        (Read-Result @{ check = $id; outcome = "fix"; failed = @("../x") }).ok | Should Be $false
        (Read-Result @{ check = $id; outcome = "fix"; failed = "reopen-issues" }).ok | Should Be $false
        $many = @(1..51 | ForEach-Object { "reopen-issues" })
        (Read-Result @{ check = $id; outcome = "fix"; failed = $many }).ok | Should Be $false
    }
    It 'refuses a file over 16 KB, broken JSON, and an array' {
        $big = (@{ check = $id; outcome = "pass"; failed = @() } | ConvertTo-Json) + (" " * 16400)
        (ConvertFrom-FlareCheckResult $big $id "active" $sc).ok | Should Be $false
        (ConvertFrom-FlareCheckResult "{ not json" $id "active" $sc).ok | Should Be $false
        (ConvertFrom-FlareCheckResult "[1,2]" $id "active" $sc).ok | Should Be $false
        $p = Join-Path $TestDrive 'result.json'; [IO.File]::WriteAllText($p, $big)
        (Read-FlareCheckResult $p $id "active" $sc).ok | Should Be $false
        (Read-FlareCheckResult (Join-Path $TestDrive 'none.json') $id "active" $sc).ok | Should Be $false
    }
    It 'trial.json with any other content reads as unknown' {
        (ConvertFrom-TrialJson '{"state":"active","endDate":"2026-11-20","wasLicensed":false}').state | Should Be "active"
        (ConvertFrom-TrialJson '{"state":"licensed","wasLicensed":false}').state | Should Be "licensed"
        $e = ConvertFrom-TrialJson '{"state":"expired","wasLicensed":true}'; $e.state | Should Be "expired"; $e.wasLicensed | Should Be $true
        foreach ($t in '', 'nope', '[]', '{"state":"active"}', '{"state":"Active","endDate":"2026-11-20"}', '{"state":"active","endDate":"2026-13-40"}',
                       '{"state":"active","endDate":"20 Nov 2026"}', '{"state":"licensed","extra":1}', '{"state":"licensed","wasLicensed":"yes"}', '{"state":"trial"}') {
            (ConvertFrom-TrialJson $t).state | Should Be "unknown"
        }
    }
}

Describe 'Assert-GitConfig on the captured configs (P13)' {
    $missing = Test-FixturesMissing @('git-config-scopes')
    It (Get-FixtureTitle 'allows the local keys git writes, and the system and command scopes' @('git-config-scopes')) -Skip:$missing {
        $f = Get-Fixture 'git-config-scopes'
        $lines = @((Get-FixtureCase $f 'local').stdout -split "`n" | Where-Object { $_ })
        @($lines | Where-Object { $_ -like "local`t*" }).Count | Should BeGreaterThan 3
        @(Test-GitConfigScopes $lines).Count | Should Be 0
    }
    It (Get-FixtureTitle 'refuses anything in the worktree scope, and extensions.worktreeconfig' @('git-config-scopes')) -Skip:$missing {
        $f = Get-Fixture 'git-config-scopes'
        $bad = @(Test-GitConfigScopes @((Get-FixtureCase $f 'worktree').stdout -split "`n" | Where-Object { $_ }))
        $bad -contains "local extensions.worktreeconfig" | Should Be $true
        $bad -contains "worktree core.fsmonitor" | Should Be $true
    }
    It 'refuses a local key that runs programs, and lines it can''t read' {
        @(Test-GitConfigScopes @("local`tcore.fsmonitor=evil.exe")) | Should Be "local core.fsmonitor"
        @(Test-GitConfigScopes @("local`tcore.sshcommand=x")) | Should Be "local core.sshcommand"
        @(Test-GitConfigScopes @("no tab here")) | Should Be "an unreadable line"
    }
    It 'checks the checkout it runs in' {
        $r = Join-Path $TestDrive 'g'; New-Item -ItemType Directory $r | Out-Null
        Push-Location $r
        try {
            $ErrorActionPreference = "Continue"
            git init -q 2>&1 | Out-Null
            { Assert-GitConfig "The test" } | Should Not Throw
            git config core.fsmonitor true 2>&1 | Out-Null
            { Assert-GitConfig "The test" } | Should Throw "core.fsmonitor"
        }
        finally { Pop-Location }
    }
}
