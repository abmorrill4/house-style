# Pester 3 tests for the run's gates (factory-runtime spec sections 3 and 5): the license gate, the trial warning, the
# usage limit and its stop (P9, P11), the fork-PR approval check (P9), and what a Flare check's outcome does to the route
# (P7's run-level cases), over runs of Invoke-Factory.ps1 in a scripted world (RunHarness.ps1).
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here 'Fixtures.psm1') -Force

Describe 'Gates and stops (P9)' {
    . (Join-Path $here 'RunHarness.ps1')
    It 'live without licensed in trial.json behaves as shadow, and says so' {
        New-World 'gate' 'live' | Out-Null
        Set-Trial "active" "2026-12-31"
        Add-Report 401 @(); Invoke-Run | Out-Null
        $Run.routeMode | Should Be "shadow"; $Run.live | Should Be $false
        Get-EffectiveMode "live" ([pscustomobject]@{ state = "active" }) | Should Be "shadow"
        Get-EffectiveMode "live" ([pscustomobject]@{ state = "licensed" }) | Should Be "live"
        Get-EffectiveMode "live" $null | Should Be "shadow"
        Get-EffectiveMode "shadow" ([pscustomobject]@{ state = "licensed" }) | Should Be "shadow"
    }
    It 'posts the trial warning once per date, seven days ahead, reading earlier marked entries back' {
        New-World 'warn' | Out-Null
        Set-Trial "active" "2026-11-08"   # six days after the world's clock
        Invoke-Run | Out-Null; Move-Clock 60; Invoke-Run | Out-Null
        $w = @($global:W.ledger | Where-Object { $_.step -eq "trial-warning" })
        $w.Count | Should Be 1
        $w[0].key | Should Be "2026-11-08"
        Set-Trial "active" "2026-11-09"; Move-Clock 60; Invoke-Run | Out-Null
        @($global:W.ledger | Where-Object { $_.step -eq "trial-warning" }).Count | Should Be 2
        Get-TrialWarning ([pscustomobject]@{ state = "active"; endDate = "2026-12-31" }) @() ([datetime]"2026-11-02") | Should BeNullOrEmpty
        Get-TrialWarning ([pscustomobject]@{ state = "licensed"; endDate = $null }) @() ([datetime]"2026-11-02") | Should BeNullOrEmpty
    }
    It 'marked entries aren''t agent runs' {
        Get-RunsToday @([pscustomobject]@{ at = "2026-11-02T10:00:00Z"; marked = $true }, [pscustomobject]@{ at = "2026-11-02T11:00:00Z" }) ([datetime]::new(2026, 11, 2, 12, 0, 0, [DateTimeKind]::Utc)) | Should Be 1
    }
    It 'detects the usage limit on the labeled interim example until a real one is captured' {
        $UsageLimitPatternSource | Should Match '^interim'
        # Interim examples in the CLI's -p JSON shape, labeled as such: not a captured fixture.
        $interimHit = '{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached|1762106400"}'
        $interimOther = '{"type":"result","subtype":"success","is_error":true,"result":"API Error: 500"}'
        Test-UsageLimit $interimHit | Should Be $true
        Test-UsageLimit $interimOther | Should Be $false
        Test-UsageLimit '{"type":"result","subtype":"success","is_error":false,"result":"Claude AI usage limit reached"}' | Should Be $false
        Test-CliError $interimOther | Should Be $true
    }
    It (Get-FixtureTitle 'detects the usage limit on the captured hit' @('claude-usage-limit')) -Skip:(Test-FixturesMissing @('claude-usage-limit')) {
        Test-UsageLimit (Get-FixtureCase (Get-Fixture 'claude-usage-limit') 'claude-usage-limit').stdout | Should Be $true
    }
    It (Get-FixtureTitle 'reads the Claude CLI''s results as captured' @('claude-success', 'claude-max-turns', 'claude-error')) -Skip:(Test-FixturesMissing @('claude-success', 'claude-max-turns', 'claude-error')) {
        (ConvertFrom-AgentOutput (Get-FixtureCase (Get-Fixture 'claude-success') 'claude-success').stdout).subtype | Should Be "success"
        (ConvertFrom-AgentOutput (Get-FixtureCase (Get-Fixture 'claude-max-turns') 'claude-max-turns').stdout).subtype | Should Be "error_max_turns"
        Test-CliError (Get-FixtureCase (Get-Fixture 'claude-error') 'claude-error').stdout | Should Be $true
        Test-UsageLimit (Get-FixtureCase (Get-Fixture 'claude-error') 'claude-error').stdout | Should Be $false
    }
    It (Get-FixtureTitle 'the fork-PR approval check passes all_external_contributors and stops on anything else' @('fork-pr-approval')) -Skip:(Test-FixturesMissing @('fork-pr-approval')) {
        $f = Get-Fixture 'fork-pr-approval'
        $ok = (Get-FixtureCase $f 'all_external_contributors').stdout
        Test-ForkApproval $ok | Should BeNullOrEmpty
        Test-ForkApproval (Get-FixtureCase $f 'other').stdout | Should Be (Format-Copy ForkApprovalChanged)
        # The captured answer with its field missing, or renamed.
        $o = $ok | ConvertFrom-Json; $o.PSObject.Properties.Remove('approval_policy')
        Test-ForkApproval ($o | ConvertTo-Json -Compress) | Should Be (Format-Copy ForkApprovalChanged)
        Test-ForkApproval ($ok -replace '"approval_policy"', '"approvalPolicy"') | Should Be (Format-Copy ForkApprovalChanged)
    }
    It 'a ready check that fails stops the run before it picks anything' {
        New-World 'ready' | Out-Null
        function Test-AgentsReady { @("There's no carry-done: the agents VM's reset hasn't finished.") }
        Add-Report 402 @()
        Invoke-Run | Should Be 1
        $global:W.jobs.Count | Should Be 0
    }
}

Describe 'A usage-limit stop (P11)' {
    . (Join-Path $here 'RunHarness.ps1')
    It 'reverts only the run''s own label changes, records the stop, posts its string, and pauses new work an hour' {
        New-World 'usage' | Out-Null
        Add-Report 411 @("blocked:other", "factory:wip-limit")   # the owner's earlier state, which the pick changes
        $before = (Get-ReportLabelList 411) -join ','
        $global:W.agents["triage"] = { param($spec) [pscustomobject]@{ id = "x"; kind = "triage"; exit = 1; timedOut = $false; refused = $null; stuck = $null
            files = @{ "stdout.txt" = '{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached|1762106400"}'; "stderr.txt" = ""; "launcher.json" = "{}" } } }
        Invoke-Run | Should Be 0
        (Get-ReportLabelList 411) -join ',' | Should Be $before
        @($global:W.ledger | Where-Object { $_.stopped -eq "usage-limit" }).Count | Should Be 1
        (Get-FactoryComments 411) -join "`n" | Should Match ([regex]::Escape((Format-Copy UsageLimitReport)))
        (Get-FactoryComments 411) -join "`n" | Should Not Match 'Triage failed'   # the report isn't charged
        Get-ReasonCodes @((Format-Copy UsageLimitReport)) | Should Be "usage-limit"
        Test-Path (Join-Path $global:W.root "factory-logs\cli-errors") | Should Be $true
        $global:W.agents.Remove("triage")
        $jobs = $global:W.jobs.Count
        Move-Clock 59; Invoke-Run | Out-Null
        $global:W.jobs.Count | Should Be $jobs
        Move-Clock 2; Invoke-Run | Out-Null
        $global:W.jobs.Count | Should BeGreaterThan $jobs
    }
}

Describe 'What the Flare check''s outcome does to the route (P7, run level)' {
    . (Join-Path $here 'RunHarness.ps1')
    It 'auto-fix only on a pass, with the path rule, and live with a license' {
        New-World 'livepass' 'live' | Out-Null
        Set-Trial "licensed" $null
        Add-Report 421 @(); Invoke-Run | Out-Null
        Complete-HostCheck "pass" | Out-Null
        Move-Clock 5; Invoke-Run | Out-Null
        $p = @($global:W.published | Where-Object report -eq 421)
        $p.Count | Should Be 1; $p[0].autoFix | Should Be $true; $p[0].draft | Should Be $false
        (Get-ReportLabelList 421) -contains "factory:done" | Should Be $true
    }
    It 'a fix that changes a path auto-fix doesn''t cover goes for review even on a pass' {
        New-World 'livepaths' 'live' | Out-Null
        Set-Trial "licensed" $null
        $global:W.agents["fix"] = { param($spec) $f = New-FixFiles $spec.base @{ "tests/e2e/scenarios/ignore-sticks.ps1" = "# a scenario`n" }
            $files = @{ "stdout.txt" = '{"type":"result","subtype":"success","is_error":false,"num_turns":3}'; "stderr.txt" = ""; "launcher.json" = "{}" }; foreach ($k in $f.Keys) { $files[$k] = $f[$k] }
            [pscustomobject]@{ id = "x"; kind = "fix"; exit = 0; timedOut = $false; refused = $null; stuck = $null; files = $files } }
        Add-Report 422 @(); Invoke-Run | Out-Null
        Complete-HostCheck "pass" | Out-Null
        Move-Clock 5; Invoke-Run | Out-Null
        $p = @($global:W.published | Where-Object report -eq 422)
        $p[0].draft | Should Be $true; $p[0].autoFix | Should Be $false
        $p[0].body | Should Match "files auto-fix doesn't cover \(tests/e2e/scenarios/ignore-sticks\.ps1\)"
    }
    It 'a public scenario failing is never an auto-fix, and a holdout failure pushes nothing' {
        New-World 'livefix' 'live' | Out-Null
        Set-Trial "licensed" $null
        Add-Report 423 @(); Invoke-Run | Out-Null
        $id = Invoke-HostClaim; Set-HostResult $id "fix" $null @("reopen-issues")
        Move-Clock 5; Invoke-Run | Out-Null
        @($global:W.published | Where-Object report -eq 423)[0].draft | Should Be $true
        Add-Report 424 @(); Invoke-Run | Out-Null
        Complete-HostCheck "holdout" | Out-Null
        Move-Clock 5; Invoke-Run | Out-Null
        @($global:W.published | Where-Object report -eq 424).Count | Should Be 0
        (Get-ReportLabelList 424) -contains "factory:needs-decision" | Should Be $true
        (Get-ReportLabelList 424) -contains "waiting:decide" | Should Be $true
    }
    It 'a hold gets no Flare check, routed or with decision:fix' {
        New-World 'hold' | Out-Null
        $global:W.agents["fix"] = { param($spec) $f = New-FixFiles $spec.base @{ "src/FlareAI.Core/Settings/PluginSettings.cs" = "class PluginSettings { }`n" }
            $files = @{ "stdout.txt" = '{"type":"result","subtype":"success","is_error":false,"num_turns":3}'; "stderr.txt" = ""; "launcher.json" = "{}" }; foreach ($k in $f.Keys) { $files[$k] = $f[$k] }
            [pscustomobject]@{ id = "x"; kind = "fix"; exit = 0; timedOut = $false; refused = $null; stuck = $null; files = $files } }
        Add-Report 431 @(); Invoke-Run | Out-Null
        (Get-ReportLabelList 431) -contains "route:hold" | Should Be $true
        @(Get-OutboxRequests).Count | Should Be 0
        Get-Fix 431 | Should BeNullOrEmpty
        Add-Report 432 @("decision:fix"); Invoke-Run | Out-Null
        (Get-FactoryComments 432) -join "`n" | Should Match 'Not pushing this fix because it touches code the owner reviews'
        @(Get-OutboxRequests).Count | Should Be 0
    }
}
