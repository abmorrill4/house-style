# Pester 3 tests for the factory's run controls (command center spec section 4):
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $root 'scripts\factory\Triage.psm1') -Force
Import-Module (Join-Path $root 'scripts\factory\Controls.psm1') -Force
$factoryScript = Get-Content (Join-Path $root 'scripts\factory\Invoke-Factory.ps1') -Raw
$rules = Get-Content (Join-Path $root 'rules\routing.json') -Raw | ConvertFrom-Json

function New-Rep([int]$n, [string[]]$labels) {
    [pscustomobject]@{ number = $n; author = [pscustomobject]@{ login = "abmorrill4" }; labels = @($labels | ForEach-Object { [pscustomobject]@{ name = $_ } }) }
}

Describe 'FACTORY_MODE (the kill switch)' {
    It 'is off when unset, empty or unreadable' {
        Get-FactoryMode $null | Should Be "off"
        Get-FactoryMode "" | Should Be "off"
        Get-FactoryMode " on" | Should Be "off"
        Get-FactoryMode "shadow" | Should Be "off"
        Get-FactoryMode "OFF" | Should Be "off"
    }
    It 'reads on in any case, and the retired live mode as off' {
        Get-FactoryMode "On" | Should Be "on"
        Get-FactoryMode "Live" | Should Be "off"
    }
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $script = Join-Path $root 'scripts\factory\Invoke-Factory.ps1'
    function Invoke-WithMode($mode) {
        $saved = @{}; foreach ($v in "FACTORY_MODE", "GH_TOKEN", "GITHUB_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY") { $saved[$v] = [Environment]::GetEnvironmentVariable($v); [Environment]::SetEnvironmentVariable($v, $null) }
        [Environment]::SetEnvironmentVariable("FACTORY_MODE", $mode)
        $ErrorActionPreference = "Continue"   # the script's error goes to stderr
        try { $out = & $ps -NoProfile -ExecutionPolicy Bypass -File $script -DryRun 2>&1 | Out-String; @{ code = $LASTEXITCODE; out = $out } }
        finally { foreach ($v in $saved.Keys) { [Environment]::SetEnvironmentVariable($v, $saved[$v]) } }
    }
    It 'Invoke-Factory.ps1 stops at once, successfully, when off, unset or unreadable' {
        foreach ($m in $null, "off", "garbage", "live", "shadow") {
            $r = Invoke-WithMode $m
            $r.code | Should Be 0
            $r.out | Should Match 'FACTORY_MODE is off or unset'
        }
    }
    It 'Invoke-Factory.ps1 goes past the switch when on (here it then stops for want of a sign-in)' {
        foreach ($m in "on") {
            $r = Invoke-WithMode $m
            $r.out | Should Not Match 'FACTORY_MODE is off'
            $r.out | Should Match 'No CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY'
        }
    }
    It 'the switch comes before the script reads or writes anything' {
        $switch = $factoryScript.IndexOf('if ($factoryMode -eq "off")')
        $switch | Should BeGreaterThan 0
        foreach ($first in 'Gh issue', 'gh.exe label', 'Invoke-AgentProcess $claude', 'Get-LedgerIssue', 'New-Item') { $factoryScript.IndexOf($first) | Should BeGreaterThan $switch }
    }
    It 'factory.yml starts the job only when on and passes FACTORY_MODE' {
        $y = Get-Content (Join-Path $root '.github\workflows\factory.yml') -Raw
        $y | Should Match "if: github\.repository == 'abmorrill4/house-style' && vars\.FACTORY_MODE == 'on'"
        $y | Should Match 'FACTORY_MODE: \$\{\{ vars\.FACTORY_MODE \}\}'
    }
    It 'nightly.yml releases whatever FACTORY_MODE says (owner decision 2026-10-26)' {
        Get-Content (Join-Path $root '.github\workflows\nightly.yml') -Raw | Should Not Match 'vars\.FACTORY_MODE'
    }
    It 'the factory only triages: a decision:fix pick and a fix route build nothing' {
        $f = Get-Content $script -Raw
        $f | Should Match 'if \(\$pick -and \$pick\.Mode -eq "fix"\) \{ \$pick = \$null \}'
        $build = $f.IndexOf('$route.build = $false')
        $build | Should BeGreaterThan 0
        $f.IndexOf('if ($route.build -and $wipFull)') | Should BeGreaterThan $build
        $f.IndexOf('$mode = $pick.Mode') | Should BeGreaterThan $f.IndexOf('if ($pick -and $pick.Mode -eq "fix") { $pick = $null }')
        $stop = $f.IndexOf('throw "Triage only. The fix path is off."')
        $stop | Should BeGreaterThan $build
        foreach ($act in 'Invoke-ReproCheck $reproTest', 'push -q -f origin', 'Gh pr create', 'Gh pr merge') { $f.IndexOf($act, $stop) | Should BeGreaterThan $stop }
    }
    It 'nothing reads the old FACTORY_ROUTE_MODE any more' {
        foreach ($f in 'scripts\factory\Invoke-Factory.ps1', '.github\workflows\factory.yml', 'scripts\factory\README.md', '.claude\skills\triage\SKILL.md') {
            Get-Content (Join-Path $root $f) -Raw | Should Not Match 'FACTORY_ROUTE_MODE'
        }
    }
}

Describe 'The agents run no build, test or e2e command of their own' {
    $tc = "C:/actions-runner/_work/_temp/factory-tools/scripts/agent/test-channel.ps1"
    $triage = Get-TriageTools $tc; $fix = Get-FixTools $tc
    It 'their tool lists hold no dotnet or e2e harness entry' {
        foreach ($t in $triage, $fix, $ReviewTools) {
            $t | Should Not Match '(?i)dotnet'
            $t | Should Not Match '(?i)Run-FlareE2E|tests/e2e'
        }
    }
    It 'triage and fix test only through the copied test channel, by its absolute path' {
        $triage | Should Match ([regex]::Escape("Bash(powershell.exe -NoProfile -ExecutionPolicy Bypass -File $($tc):*)"))
        $fix | Should Match ([regex]::Escape("Bash(powershell.exe -NoProfile -ExecutionPolicy Bypass -File $($tc):*)"))
        $triage | Should Not Match 'File scripts/agent'
        $ReviewTools | Should Not Match 'Bash'
    }
    It 'refuses a relative copy path or one with spaces' {
        { Get-TestChannelTool "scripts/agent/test-channel.ps1" } | Should Throw
        { Get-TestChannelTool "C:/Program Files/x/test-channel.ps1" } | Should Throw
    }
    It 'Invoke-Factory.ps1 uses those lists and names no dotnet tool itself' {
        $factoryScript | Should Not Match 'Bash\(dotnet'
        $factoryScript | Should Not Match 'Bash\(powershell\.exe[^)]*Run-FlareE2E'
        $factoryScript | Should Match '\(Get-TriageTools \$testChannel\) "report-\$n-triage\.log"'
        $factoryScript | Should Match '\$tools = Get-FixTools \$testChannel'
        $factoryScript | Should Match '\$ReviewTools "report-\$n-review\.log"'
    }
    It 'web tools, gh, curl and git push stay off, and agents may not edit scripts or the harness' {
        $DisallowedTools | Should Match '^WebFetch,WebSearch,Bash\(gh:\*\),Bash\(curl:\*\),Bash\(git push:\*\),'
        foreach ($p in "scripts/**", "tests/e2e/Run-FlareE2E.ps1", "tests/e2e/FlareE2E.psm1", "tests/e2e/flaui/**") {
            $DisallowedTools | Should Match ([regex]::Escape("Edit(./$p)"))
            $DisallowedTools | Should Match ([regex]::Escape("Write(./$p)"))
        }
    }
    It 'the prompts name the copied test channel' {
        foreach ($p in 'triage-prompt.md', 'fix-prompt.md') {
            $t = Get-Content (Join-Path $root ".github\factory\$p") -Raw
            $t | Should Match '-File \{\{TEST_CHANNEL\}\} -Channel core'
            $t | Should Not Match '-File scripts/agent/test-channel'
            $t | Should Not Match '(?m)^- `dotnet build` and `dotnet test`'
        }
    }
    It 'each agent gets a fresh copy from the base commit first and is checked against it after' {
        $agentFn = $factoryScript.Substring($factoryScript.IndexOf('function Agent('))
        $agentFn.IndexOf('Clear-Scratch') | Should BeLessThan $agentFn.IndexOf('Invoke-AgentProcess')
        $agentFn.IndexOf('Save-AgentTools') | Should BeLessThan $agentFn.IndexOf('Invoke-AgentProcess')
        $agentFn.IndexOf('Assert-AgentTools') | Should BeGreaterThan $agentFn.IndexOf('Invoke-AgentProcess')
        $factoryScript | Should Match 'git archive -o \$tar \$base @AgentToolPaths'
        $factoryScript | Should Match 'git diff --name-only \$base -- @AgentToolPaths'
        $factoryScript | Should Match 'Test-SameHashes \$script:toolsHashes \(Get-TreeHashes \$toolsDir\)'
        $factoryScript | Should Match '\$env:HOUSESTYLE_TEST_REPO = \$here'
        @($AgentToolPaths) -join ',' | Should Be "scripts/agent,tests/e2e/Run-FlareE2E.ps1,tests/e2e/FlareE2E.psm1,tests/e2e/flaui"
    }
    It 'a changed copy is caught, build output aside' {
        $d = Join-Path $TestDrive 'copy'; New-Item -ItemType Directory -Force (Join-Path $d 'scripts\agent'), (Join-Path $d 'flaui\bin') | Out-Null
        Set-Content (Join-Path $d 'scripts\agent\test-channel.ps1') 'one'
        $before = Get-TreeHashes $d
        Set-Content (Join-Path $d 'flaui\bin\x.dll') 'built'
        Test-SameHashes $before (Get-TreeHashes $d) | Should Be $true
        Set-Content (Join-Path $d 'scripts\agent\test-channel.ps1') 'two'
        Test-SameHashes $before (Get-TreeHashes $d) | Should Be $false
        Set-Content (Join-Path $d 'scripts\agent\test-channel.ps1') 'one'; Set-Content (Join-Path $d 'scripts\agent\extra.psm1') 'x'
        Test-SameHashes $before (Get-TreeHashes $d) | Should Be $false
    }
    It 'the copy runs tests in the checkout and the e2e harness from itself' {
        $copy = Join-Path $TestDrive 'tools'; New-Item -ItemType Directory -Force $copy | Out-Null
        $tar = Join-Path $TestDrive 'tools.tar'
        Push-Location $root
        try { git archive -o $tar HEAD scripts/agent tests/e2e/Run-FlareE2E.ps1 tests/e2e/FlareE2E.psm1 tests/e2e/flaui } finally { Pop-Location }
        & tar.exe -xf $tar -C $copy
        $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $env:HOUSESTYLE_TEST_REPO = "$root"
        try { $line = & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $copy 'scripts\agent\test-channel.ps1') -Channel e2e -Scenario reopen-issues -DryRun }
        finally { Remove-Item Env:HOUSESTYLE_TEST_REPO }
        ($line -join ' ') | Should Match ([regex]::Escape((Join-Path $copy 'tests\e2e\Run-FlareE2E.ps1')))
        ($line -join ' ') | Should Match ([regex]::Escape("-RepoRoot $root"))
    }
}

Describe 'nightly.yml waits for its own build' {
    $y = Get-Content (Join-Path $root '.github\workflows\nightly.yml') -Raw
    It 'takes the run id from the dispatch, else only runs created after it, never the newest run of the tag' {
        $y | Should Not Match '-L 1 --json databaseId'
        $y | Should Match "grep -oE 'actions/runs/\[0-9\]\+'"
        $y | Should Match 'select\(\.createdAt >= \\"\$since\\"\)'
        $y.IndexOf('since=$(date -u') | Should BeLessThan $y.IndexOf('gh workflow run build.yml')
    }
    It 'a good release ends the step with success, and stuck is set in the step itself' {
        $y | Should Match '(?m)^\s+stuck="\$last"'
        $y | Should Match '(?m)^\s+exit 0   # a false test above'
    }
    It 'the failure report reuses the open Nightly release issue' {
        $y | Should Match 'creator=app%2Fgithub-actions'
        $y | Should Match 'select\(\.title == "Nightly release" and \.user\.login == "github-actions\[bot\]"\)'
        $y | Should Not Match 'gh issue list -R "\$REPO" --state open --author'
    }
}

Describe 'Join-ProcessArguments' {
    It 'quotes arguments with spaces and leaves plain ones alone' {
        Join-ProcessArguments @("-p", "--allowedTools", "Read,Bash(git status:*)") | Should Be '-p --allowedTools "Read,Bash(git status:*)"'
    }
    It 'escapes quotes and a trailing backslash, and keeps an empty argument' {
        Join-ProcessArguments @('a "b"', 'C:\x y\', '') | Should Be '"a \"b\"" "C:\x y\\" ""'
    }
}

Describe 'Invoke-AgentProcess (the wall-clock limit)' {
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $in = Join-Path $TestDrive 'in.txt'; Set-Content $in "" -Encoding ASCII
    It 'returns the exit code and keeps stdout in its file' {
        $out = Join-Path $TestDrive 'out1.txt'; $err = Join-Path $TestDrive 'err1.txt'
        $r = Invoke-AgentProcess $ps @("-NoProfile", "-Command", "Write-Output 'hello there'; exit 3") $in $out $err 60
        $r.timedOut | Should Be $false
        $r.exit | Should Be 3
        (Get-Content $out -Raw) | Should Match 'hello there'
    }
    It 'stops a run that goes past its limit, with its child processes' {
        $out = Join-Path $TestDrive 'out2.txt'; $err = Join-Path $TestDrive 'err2.txt'
        $marker = "hs-wallclock-$([guid]::NewGuid().ToString('N'))"
        $r = Invoke-AgentProcess $ps @("-NoProfile", "-Command", "Start-Process $ps -ArgumentList '-NoProfile','-Command','Start-Sleep 120 # $marker' -NoNewWindow; Start-Sleep 120") $in $out $err 3
        $r.timedOut | Should Be $true
        $r.exit | Should Be -1
        $r.seconds | Should BeLessThan 60
        Start-Sleep 1
        @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object { $_.CommandLine -like "*$marker*" }).Count | Should Be 0
    }
    It 'a stopped run is a cap stop, and so is running out of turns' {
        Get-AgentStop ([pscustomobject]@{ timedOut = $true }) (ConvertFrom-AgentOutput "") 45 200 | Should Match '45-minute limit'
        Get-AgentStop ([pscustomobject]@{ timedOut = $false }) (ConvertFrom-AgentOutput '{"type":"result","subtype":"error_max_turns","num_turns":200}') 45 200 | Should Match 'all its 200 turns'
        Get-AgentStop ([pscustomobject]@{ timedOut = $false }) (ConvertFrom-AgentOutput '{"type":"result","subtype":"success"}') 45 200 | Should BeNullOrEmpty
    }
}

Describe 'The run ledger' {
    $json = '{"type":"result","subtype":"success","is_error":false,"duration_ms":81234,"num_turns":37,"result":"SECRET REPORT WORDS from the tester","session_id":"x","total_cost_usd":1.2345,"usage":{"input_tokens":1200,"output_tokens":3400,"cache_read_input_tokens":56000,"cache_creation_input_tokens":7800}}'
    $out = ConvertFrom-AgentOutput $json
    $proc = [pscustomobject]@{ exit = 0; timedOut = $false; seconds = 82 }
    It 'reads tokens, turns and cost from claude -p --output-format json' {
        $out.turns | Should Be 37; $out.input_tokens | Should Be 1200; $out.output_tokens | Should Be 3400
        $out.cache_read_tokens | Should Be 56000; $out.cache_write_tokens | Should Be 7800; $out.cost_usd | Should Be 1.2345
    }
    It 'reads nothing from missing or broken output' {
        $o = ConvertFrom-AgentOutput "not json"
        $o.turns | Should BeNullOrEmpty; $o.input_tokens | Should BeNullOrEmpty
    }
    It 'records dollars only when an API key paid' {
        (New-LedgerEntry "fix" 12 $proc $out $false "on" $false $null).cost_usd | Should BeNullOrEmpty
        (New-LedgerEntry "fix" 12 $proc $out $true "on" $false $null).cost_usd | Should Be 1.2345
    }
    It 'holds numbers only, never the agent text' {
        $e = New-LedgerEntry "triage" 12 $proc $out $true "live" $false $null
        $c = Format-LedgerComment $e
        $c | Should Not Match 'SECRET REPORT WORDS'
        @($e.Keys) -join ',' | Should Be 'at,step,report,mode,dry_run,exit,seconds,stopped,turns,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,cost_usd'
    }
    It 'parses back from its comment, and ignores other comments' {
        $e = New-LedgerEntry "review" 7 ([pscustomobject]@{ exit = -1; timedOut = $true; seconds = 2700 }) $out $false "on" $true "stopped"
        $back = ConvertFrom-LedgerComment (Format-LedgerComment $e)
        $back.step | Should Be "review"; $back.report | Should Be 7; $back.stopped | Should Be "wall-clock"; $back.dry_run | Should Be $true; $back.turns | Should Be 37
        ConvertFrom-LedgerComment "**Factory:** something else" | Should BeNullOrEmpty
        ConvertFrom-LedgerComment ('```json' + "`n{}`n" + '```') | Should BeNullOrEmpty   # no marker
    }
    It 'counts the UTC day of runs and stops at the cap of 20' {
        $now = [datetime]::new(2026, 10, 24, 15, 0, 0, [DateTimeKind]::Utc)
        $entries = @(1..19 | ForEach-Object { [pscustomobject]@{ at = "2026-10-24T0$([Math]::Min(9, $_ % 10)):00:00Z" } }) + [pscustomobject]@{ at = "2026-10-23T23:59:00Z" }
        Get-RunsToday $entries $now | Should Be 19
        Test-RunBudget 19 20 | Should BeNullOrEmpty
        Test-RunBudget 20 20 | Should Match "today's 20 agent runs are used up"
    }
    It 'the cap is checked before every agent run, and a cap stop is posted on the report as stuck' {
        $factoryScript | Should Match '\$over = Test-RunBudget \$script:runsUsed \$runLimit\s+if \(\$over\) \{ \$script:runCapStop = \$true; throw'
        $factoryScript | Should Match 'if \(\$mode -eq "triage" -and -not \$script:capStop\)'
        $factoryScript | Should Match 'Label \$n @\("factory:stuck", "waiting:stuck", "blocked:\$\(Get-BlockedCause \$why\)"\)'
        $factoryScript | Should Match 'Write-Ledger \(New-LedgerEntry'
        $factoryScript | Should Match '"--output-format", "json"'
    }
    It 'running out of the day''s agent runs queues the report for tomorrow instead of marking it stuck' {
        $catch = $factoryScript.Substring([regex]::Match($factoryScript, '(?m)^catch \{').Index)   # the run's own catch
        $catch | Should Match 'if \(\$script:runCapStop\) \{'
        $catch | Should Match 'Label \$n "factory:queued" @\("factory:working", "triage:failed"\)'
        $catch.IndexOf('$script:runCapStop') | Should BeLessThan $catch.IndexOf('"factory:stuck"')
        # A queued report is picked again: a routed one is triaged again, an approved fix is fixed.
        (Select-Report @((New-Rep 8 "problem", "triage:ready", "route:auto-fix", "factory:queued")) "abmorrill4" 0 1).Mode | Should Be "triage"
        (Select-Report @((New-Rep 6 "problem", "decision:fix", "factory:queued")) "abmorrill4" 0 0).Mode | Should Be "fix"
    }
    It 'rules/routing.json sets the limits' {
        $rules.limits.agentRunsPerDay | Should Be 20
        $rules.limits.openFactoryPrs | Should Be 5
        $rules.limits.agentMinutes | Should Be 45
    }
}

Describe 'The open factory PR limit' {
    $prs = @([pscustomobject]@{ headRefName = "factory/report-1" }, [pscustomobject]@{ headRefName = "factory/report-2" }, [pscustomobject]@{ headRefName = "feature-x" })
    It 'counts factory branches only' { Get-OpenFactoryPrCount $prs | Should Be 2 }
    It 'is full at the limit' { Test-WipFull 5 5 | Should Be $true; Test-WipFull 4 5 | Should Be $false }
    It 'at the limit no new fix starts, but new reports are still triaged' {
        Select-Report @((New-Rep 6 "problem", "decision:fix")) "abmorrill4" 0 3 $true | Should BeNullOrEmpty
        (Select-Report @((New-Rep 6 "problem", "decision:fix")) "abmorrill4" 0 3 $false).Mode | Should Be "fix"
        (Select-Report @((New-Rep 8 "problem")) "abmorrill4" 0 3 $true).Mode | Should Be "triage"
    }
    It 'an approved fix waiting on the limit is labeled, and starts once there is room' {
        @(Get-WipWaiting @((New-Rep 6 "problem", "decision:fix"), (New-Rep 7 "problem", "decision:fix", "factory:done"), (New-Rep 8 "problem")) "abmorrill4").Count | Should Be 1
        (Select-Report @((New-Rep 6 "problem", "decision:fix", "factory:wip-limit")) "abmorrill4" 0 3 $false).Mode | Should Be "fix"
        Select-Report @((New-Rep 6 "problem", "decision:fix", "factory:wip-limit")) "abmorrill4" 0 3 $true | Should BeNullOrEmpty
    }
    It 'a fix route that waited on the limit is routed again once there is room' {
        (Select-Report @((New-Rep 9 "problem", "triage:ready", "route:auto-fix", "factory:wip-limit")) "abmorrill4" 0 3 $false).Mode | Should Be "triage"
        Select-Report @((New-Rep 9 "problem", "triage:ready", "route:auto-fix", "factory:wip-limit")) "abmorrill4" 0 3 $true | Should BeNullOrEmpty
    }
    It 'the queued (daily cap) rule still holds' {
        Select-Report @((New-Rep 8 "problem", "triage:ready", "factory:queued")) "abmorrill4" 0 0 $false | Should BeNullOrEmpty
        (Select-Report @((New-Rep 8 "problem", "triage:ready", "factory:queued")) "abmorrill4" 0 1 $false).Mode | Should Be "triage"
    }
    It 'Invoke-Factory.ps1 holds a fix route at the limit and labels it' {
        $factoryScript | Should Match 'if \(\$route\.build -and \$wipFull\)'
        $factoryScript | Should Match '"factory:wip-limit"\) \$stale'
    }
}

Describe 'blocked:<cause> labels' {
    It 'names the cause from the reason a run stopped' {
        Get-BlockedCause "Flare failed to start or stopped responding during the end-to-end checks, twice. That points at the factory VM rather than the fix; rerun." | Should Be "vm"
        Get-BlockedCause "The end-to-end run stopped before any scenario ran (setup)." | Should Be "vm"
        Get-BlockedCause "Required checks failed on https://x: fail:build" | Should Be "flaky-test"
        Get-BlockedCause "Stopped by a cap, because the agent ran past its 45-minute limit and was stopped." | Should Be "owner"
        Get-BlockedCause "Stopped by a cap before the fix agent started, because today's 20 agent runs are used up." | Should Be "owner"
        Get-BlockedCause "Review blocked the fix: x" | Should Be "other"
    }
    It 'the factory may set its four causes and no others' {
        { foreach ($c in $BlockedCauses) { Assert-FactoryLabel "blocked:$c" } } | Should Not Throw
        { Assert-FactoryLabel "blocked:upstream" } | Should Throw
        { Assert-FactoryLabel "blocked:anything" } | Should Throw
    }
    It 'a report picked again drops its blocked label' {
        $factoryScript | Should Match '\$_ -like "blocked:\*"'
        $factoryScript | Should Match 'Label \$n "factory:working" @\(Get-Labels \$r \| Where-Object \{ \$_ -like "blocked:\*"'
    }
}

Describe 'Holdout scenarios' {
    It 'no holdout folder is fine' { @(Save-Holdout { $null } { throw "never" } (Join-Path $TestDrive 'h0')).Count | Should Be 0 }
    It 'skips AI holdout scenarios, which the holdout- prefix would hide from -NoAi' {
        $list = { @([pscustomobject]@{ name = "ai-rewrite.ps1"; type = "file" }, [pscustomobject]@{ name = "lint-table.ps1"; type = "file" }) }
        $saved = @(Save-Holdout $list { param($n) "# $n" } (Join-Path $TestDrive 'h2'))
        $saved -join ',' | Should Be "holdout-lint-table.ps1"
        Test-Path (Join-Path $TestDrive 'h2\holdout-ai-rewrite.ps1') | Should Be $false
    }
    It 'a holdout failure is reported only when something failed' {
        $factoryScript | Should Match 'if \(\$failed\.Count -and -not \(\$failed \| Where-Object \{ \(\"\" \+ \$_\.scenario\) -notlike "holdout-\*" \}\)\)'
    }
    It 'saves only top-level .ps1 files with safe names, outside the checkout' {
        $list = { @([pscustomobject]@{ name = "ignore-sticks.ps1"; type = "file" }, [pscustomobject]@{ name = "README.md"; type = "file" },
                    [pscustomobject]@{ name = "sub"; type = "dir" }, [pscustomobject]@{ name = "..\evil.ps1"; type = "file" }) }
        $dest = Join-Path $TestDrive 'h1'
        $saved = @(Save-Holdout $list { param($n) "# scenario $n" } $dest)
        $saved | Should Be "holdout-ignore-sticks.ps1"
        Get-Content (Join-Path $dest "holdout-ignore-sticks.ps1") -Raw | Should Match 'scenario ignore-sticks'
    }
    # The order in Invoke-Factory.ps1: the holdout is fetched only after the fix agent has finished, and is gone before
    # the review agent starts. The agent never holds a token, so it can't fetch them itself.
    $fixAgent = $factoryScript.IndexOf('Agent $fixPrompt')
    $fetch = $factoryScript.IndexOf('Save-Holdout {')
    $review = $factoryScript.IndexOf('Agent $reviewPrompt')
    It 'is fetched after the fix agent step' {
        $fixAgent | Should BeGreaterThan 0
        $fetch | Should BeGreaterThan $fixAgent
        $factoryScript.IndexOf('Assert-CleanTree "The fixing agent"') | Should BeLessThan $fetch
    }
    It 'is fetched in one place only, from the private reports repo' {
        ([regex]::Matches($factoryScript, 'Save-Holdout ')).Count | Should Be 1
        ([regex]::Matches($factoryScript, [regex]::Escape('repos/$reports/contents/holdout'))).Count | Should Be 2   # the list (Get-HoldoutList) and one file
        ([regex]::Matches($factoryScript, 'Get-HoldoutList')).Count | Should Be 2   # its definition and the one call inside Save-Holdout
        $factoryScript.IndexOf('function Get-HoldoutList') | Should BeLessThan $fixAgent   # defined early, called late
    }
    It 'lands outside the checkout, runs with the public suite, and is deleted before the review agent' {
        $factoryScript | Should Match '\$holdoutDir = Join-Path \$tmp "holdout-\$n"'
        $factoryScript | Should Match 'Run-FlareE2E\.ps1 -Install -NoAi -OutDir \$e2eOut @e2eExtra'
        $factoryScript | Should Match '"-ScenarioDir", \$holdoutDir'
        $delete = $factoryScript.IndexOf('finally { Remove-Item $holdoutDir')
        $delete | Should BeGreaterThan $fetch
        $delete | Should BeLessThan $review
    }
    It 'the fix prompt and the agent tools never mention it' {
        Get-Content (Join-Path $root '.github\factory\fix-prompt.md') -Raw | Should Not Match '(?i)holdout'
        Get-Content (Join-Path $root '.github\factory\triage-prompt.md') -Raw | Should Not Match '(?i)holdout'
        Get-FixTools "C:/t/factory-tools/scripts/agent/test-channel.ps1" | Should Not Match '(?i)holdout|gh'
    }
    It 'Run-FlareE2E.ps1 takes extra scenario folders' {
        Get-Content (Join-Path $root 'tests\e2e\Run-FlareE2E.ps1') -Raw | Should Match '\[string\[\]\]\$ScenarioDir'
    }
}

Describe 'Weekly scorecard' {
    $now = [datetime]"2026-10-26T14:00:00Z"
    $day = { param($d) $now.AddDays(-$d).ToString("o") }
    $mk = { param($sha) [pscustomobject]@{ oid = $sha } }
    $prs = @(
        [pscustomobject]@{ title = "Fix tester report 98765"; headRefName = "factory/report-98765"; createdAt = (& $day 3); mergedAt = (& $day 2); closedAt = (& $day 2); mergeCommit = (& $mk ("a" * 40)) },
        [pscustomobject]@{ title = "Fix tester report 98766"; headRefName = "factory/report-98766"; createdAt = (& $day 20); mergedAt = (& $day 20); closedAt = (& $day 20); mergeCommit = (& $mk ("b" * 40)) },
        [pscustomobject]@{ title = "Fix tester report 98767"; headRefName = "factory/report-98767"; createdAt = (& $day 25); mergedAt = (& $day 25); closedAt = (& $day 25); mergeCommit = (& $mk ("c" * 40)) },
        [pscustomobject]@{ title = "Fix tester report 98768"; headRefName = "factory/report-98768"; createdAt = (& $day 4); mergedAt = $null; closedAt = (& $day 1); mergeCommit = $null }
    )
    $reverted = @{ ("b" * 40) = (& $day 15); ("c" * 40) = (& $day 1) }   # b within 14 days of merge, c 24 days after
    $reports = @(
        [pscustomobject]@{ number = 98765; createdAt = (& $day 6); labels = @() },
        [pscustomobject]@{ number = 98769; createdAt = (& $day 1); labels = @([pscustomobject]@{ name = "decision:decline" }) },
        [pscustomobject]@{ number = 98770; createdAt = (& $day 30); labels = @() }
    )
    $routes = @(
        [pscustomobject]@{ report = 98769; at = (& $day 1); route = "fix-for-review"; codes = @("too-big", "regression") },
        [pscustomobject]@{ report = 98765; at = (& $day 5); route = "fix-for-review"; codes = @() },
        [pscustomobject]@{ report = 98765; at = (& $day 4); route = "auto-fix"; codes = @("passed") },
        [pscustomobject]@{ report = 98771; at = (& $day 2); route = "auto-fix"; codes = @("daily-cap") }
    )
    $ledger = @([pscustomobject]@{ at = (& $day 2); input_tokens = 1000; output_tokens = 500; cache_read_tokens = 0; cache_write_tokens = 0; cost_usd = $null; stopped = $null },
                [pscustomobject]@{ at = (& $day 3); input_tokens = 100; output_tokens = 400; cache_read_tokens = 0; cache_write_tokens = 0; cost_usd = $null; stopped = "wall-clock" })
    $card = Get-Scorecard ([pscustomobject]@{ now = $now; days = 7; reports = $reports; routes = $routes; stops = @([pscustomobject]@{ at = (& $day 1); codes = @("e2e") })
                                              prs = $prs; reverted = $reverted; ledger = $ledger; waits = $null; suspended = @("Style") })
    It 'counts reports in and the last route per report' {
        $card.reportsIn | Should Be 2
        $card.routes["auto-fix"] | Should Be 2; $card.routes["fix-for-review"] | Should Be 1
    }
    It 'uses the one revert rate: reverted within 14 days of merge, over factory PRs merged in 30 days' {
        $card.mergedMonth | Should Be 3; $card.revertedMonth | Should Be 1
    }
    It 'counts merge rate, report to ship, runs and tokens per merged change' {
        $card.prsClosed | Should Be 2; $card.prsMerged | Should Be 1
        $card.reportToShipDays | Should Be 4; $card.agentRuns | Should Be 2; $card.capStops | Should Be 1; $card.tokens | Should Be 2000
    }
    It 'counts rejection reasons as fixed codes only' {
        $card.reasons["too-big"] | Should Be 1; $card.reasons["daily-cap"] | Should Be 1; $card.reasons["e2e"] | Should Be 1
        $card.reasons["owner-declined"] | Should Be 1; $card.reasons["pr-closed-unmerged"] | Should Be 1
        $card.reasons.Contains("passed") | Should Be $false; $card.reasons.Contains("regression") | Should Be $false
    }
    It 'holds no report numbers or titles' {
        $t = Format-Scorecard $card
        $t | Should Not Match '9876'
        $t | Should Not Match 'Fix tester report'
        $t | Should Match 'Revert rate .* \| 1 of 3 \(33%\)'
        $t | Should Match 'Suspended areas \| Style'
        $t | Should Match '`too-big` 1'
    }
    It 'pairs waits from the waiting log' {
        $lines = @('{"session":"a","event":"Notification","at":"2026-10-25T10:00:00Z"}', '{"session":"a","event":"UserPromptSubmit","at":"2026-10-25T12:00:00Z"}',
                   '{"session":"b","event":"Notification","at":"2026-10-25T10:00:00Z"}', '{"session":"b","event":"Stop","at":"2026-10-25T11:00:00Z"}',
                   '{"session":"c","event":"UserPromptSubmit","at":"2026-10-25T11:00:00Z"}')
        $w = Get-OwnerWaits $lines ([datetime]"2026-10-19T00:00:00Z") $now
        $w.count | Should Be 2; $w.hours | Should Be 3; $w.median | Should Be 1.5   # Get-Median: the mean of the middle two
    }
    It 'runs on Mondays with a dry-run mode' {
        $y = Get-Content (Join-Path $root '.github\workflows\scorecard.yml') -Raw
        $y | Should Match "cron: '\d+ \d+ \* \* 1'"
        $y | Should Match 'dry_run'
        $y | Should Match 'Post-Scorecard\.ps1'
        Get-Content (Join-Path $root 'scripts\factory\Post-Scorecard.ps1') -Raw | Should Match '\[switch\]\$DryRun'
    }
}

Describe 'gh bodies go through a file' {
    $mod = Join-Path $PSScriptRoot '..\..\scripts\factory\Triage.psm1'
    Import-Module $mod -Force
    It 'keeps quotes, JSON and new lines exactly' {
        $text = "**Route:** hold`n" + '```json' + "`n" + '{"route":"hold","reasons":["a \"quoted\" reason"]}' + "`n" + '```'
        $p = New-BodyFile $text
        try { [IO.File]::ReadAllText($p) | Should Be $text } finally { Remove-Item $p }
    }
    It 'no factory script passes text to gh with --body' {
        $dir = Join-Path $PSScriptRoot '..\..\scripts\factory'
        $hits = @(Get-ChildItem $dir -Filter *.ps1 | Select-String -Pattern ' --body (?!-file)' -SimpleMatch:$false)
        $hits.Count | Should Be 0
    }
}

Describe 'factory.yml drains the queue' {
    $yml = Get-Content (Join-Path $PSScriptRoot '..\..\.github\workflows\factory.yml') -Raw
    $script = Get-Content (Join-Path $PSScriptRoot '..\..\scripts\factory\Invoke-Factory.ps1') -Raw
    It 'stops on the messages Invoke-Factory prints when the queue is empty or the day is used up' {
        $yml | Should Match 'Nothing to triage and no fix approved\|waits for tomorrow'
        $script | Should Match 'Nothing to triage and no fix approved\.'
        $script | Should Match "waits for tomorrow's agent runs"
    }
    It 'runs each pass in its own process' { $yml | Should Match ([regex]::Escape('-File .\scripts\factory\Invoke-Factory.ps1 @a')) }
}
