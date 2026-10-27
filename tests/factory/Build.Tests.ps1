# Pester 3 tests for the build factory's decisions (planning/work/factory-build/spec.md):
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $root 'scripts\factory\Triage.psm1') -Force
Import-Module (Join-Path $root 'scripts\factory\Build.psm1') -Force
$buildScript = Get-Content (Join-Path $root 'scripts\factory\Invoke-Build.ps1') -Raw
$rules = Get-Content (Join-Path $root 'rules\factory.json') -Raw | ConvertFrom-Json

Describe 'FACTORY_BUILD' {
    It 'is on only for "on"' {
        Test-BuildOn "on" | Should Be $true
        Test-BuildOn "ON" | Should Be $true
        foreach ($v in $null, "", "off", "shadow", "live", " on", "yes") { Test-BuildOn $v | Should Be $false }
    }
    It 'is read before every agent call, the push and the merge' {
        $buildScript | Should Match '(?s)function Invoke-BuildAgent.{0,200}Test-SwitchOn'
        $buildScript | Should Match '(?s)function Push-Branch.{0,100}Test-SwitchOn'
        $buildScript | Should Match '(?s)while \(\$true\) \{\s*if \(-not \(Test-SwitchOn\)\).{0,200}pr merge'
    }
}

Describe 'Ready' {
    $ok = [pscustomobject]@{ author = "abmorrill4"; labels = @("ready") }
    $by = { param($a) @([pscustomobject]@{ event = "labeled"; label = "ready"; actor = $a }) }
    It 'is ready when the owner opened it, added ready and made every edit' {
        Get-NotReadyReason $ok (& $by "abmorrill4") @("abmorrill4") "abmorrill4" "ready" | Should BeNullOrEmpty
        Get-NotReadyReason $ok (& $by "abmorrill4") @() "abmorrill4" "ready" | Should BeNullOrEmpty
    }
    It 'refuses another author, another label actor, another editor, or history it cannot read' {
        Get-NotReadyReason ([pscustomobject]@{ author = "someone"; labels = @("ready") }) (& $by "abmorrill4") @() "abmorrill4" "ready" | Should Match 'not opened'
        Get-NotReadyReason $ok (& $by "someone") @() "abmorrill4" "ready" | Should Match "wasn't added by the owner"
        Get-NotReadyReason $ok (& $by "abmorrill4") @("abmorrill4", "someone") "abmorrill4" "ready" | Should Match 'edited by someone else'
        Get-NotReadyReason $ok @() @() "abmorrill4" "ready" | Should Match "can't be read"
        Get-NotReadyReason $ok (& $by "abmorrill4") $null "abmorrill4" "ready" | Should Match "can't be read"
    }
    It 'goes by the last time ready was added' {
        $ev = @((& $by "abmorrill4")[0], [pscustomobject]@{ event = "unlabeled"; label = "ready"; actor = "abmorrill4" }, [pscustomobject]@{ event = "labeled"; label = "ready"; actor = "someone" })
        Get-NotReadyReason $ok $ev @() "abmorrill4" "ready" | Should Match "wasn't added by the owner"
    }
    It 'hashes the body the same across line endings, and differently when it changes' {
        Get-BodyHash "a`r`nb" | Should Be (Get-BodyHash "a`nb")
        Get-BodyHash "a`nb" | Should Not Be (Get-BodyHash "a`nc")
    }
    It 'reads the reproducing test a Fix names' {
        Get-FixTest "Something broke.`n`nTest: FlareAI.Core.Tests.IgnoreTests.Sticks`n" | Should Be "FlareAI.Core.Tests.IgnoreTests.Sticks"
        Get-FixTest "**Reproducing test:** ``A.B.C``" | Should Be "A.B.C"
        Get-FixTest "No test here; rm -rf /" | Should BeNullOrEmpty
        Get-FixTest "Test: a; rm -rf /" | Should BeNullOrEmpty
    }
}

Describe 'The spec path' {
    $work = Join-Path $TestDrive 'work'
    New-Item -ItemType Directory -Force (Join-Path $work '12-review-pane'), (Join-Path $work '120-other'), (Join-Path $work '7-a'), (Join-Path $work '7-b'), (Join-Path $work '9-empty') | Out-Null
    Set-Content (Join-Path $work '12-review-pane\spec.md') 'x'
    It 'comes from the issue number alone' {
        Resolve-SpecPath $work 12 | Should Be ([IO.Path]::GetFullPath((Join-Path $work '12-review-pane\spec.md')))
    }
    It 'is none for a Fix with no folder' { Resolve-SpecPath $work 13 | Should BeNullOrEmpty }
    It 'refuses two folders, a folder without spec.md and a UNC planning folder' {
        { Resolve-SpecPath $work 7 } | Should Throw 'More than one'
        { Resolve-SpecPath $work 9 } | Should Throw 'no spec.md'
        { Resolve-SpecPath '\\server\share\work' 12 } | Should Throw 'UNC'
    }
    It 'refuses a folder that is a link' {
        $target = Join-Path $TestDrive 'elsewhere'; New-Item -ItemType Directory -Force $target | Out-Null; Set-Content (Join-Path $target 'spec.md') 'x'
        cmd /c mklink /J (Join-Path $work '44-linked') $target | Out-Null
        { Resolve-SpecPath $work 44 } | Should Throw 'link'
    }
}

Describe 'Lock and pauses' {
    $lock = Join-Path $TestDrive 'f\run.lock'
    It 'takes a free lock, refuses a held one and takes over a stale one' {
        Enter-BuildLock $lock | Should Be $true
        Exit-BuildLock $lock; Test-Path $lock | Should Be $false
        $other = Start-Process powershell.exe -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 30' -PassThru -WindowStyle Hidden
        try {
            Set-Content $lock "$($other.Id) 2026-10-26T00:00:00Z"
            Enter-BuildLock $lock | Should Be $false
        } finally { Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue; $other.WaitForExit() }
        Enter-BuildLock $lock | Should Be $true   # its process is gone
        Exit-BuildLock $lock
    }
    It 'pauses until the marker time' {
        $p = Join-Path $TestDrive 'f\paused-until'
        Test-Paused $p | Should Be $false
        Set-Pause $p ((Get-Date).AddMinutes(30)); Test-Paused $p | Should Be $true
        Set-Pause $p ((Get-Date).AddMinutes(-1)); Test-Paused $p | Should Be $false
    }
    It 'reads a usage-limit stop and its reset time, and nothing else' {
        $now = [datetime]'2026-10-26T12:00:00Z'
        Get-UsageLimitReset '{"type":"result","subtype":"success","is_error":false,"result":"done"}' $now | Should BeNullOrEmpty
        Get-UsageLimitReset '{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached|1793023200"}' $now | Should Be ([DateTimeOffset]::FromUnixTimeSeconds(1793023200).UtcDateTime)
        Get-UsageLimitReset '{"type":"result","is_error":true,"result":"5-hour limit reached, resets at 3pm"}' $now | Should Be $now.ToUniversalTime().AddHours(1)
        Get-UsageLimitReset '{"type":"result","is_error":true,"result":"some other error"}' $now | Should BeNullOrEmpty
        Get-UsageLimitReset '' $now | Should BeNullOrEmpty
    }
}

Describe 'Budget' {
    $b = $rules.budget
    function Iss($size, $cost, $outcome = "merged") { [pscustomobject]@{ kind = "issue"; size = $size; cost_usd = $cost; outcome = $outcome } }
    It 'starts cold' {
        Get-IssueBudget @() "fix" $b | Should Be 1.5
        Get-IssueBudget @(Iss "feature" 1; Iss "feature" 2) "feature" $b | Should Be 8
    }
    It 'adjusts from the last merged issues of the same size' {
        $l = @(1..10 | ForEach-Object { Iss "fix" $_ }) + @(Iss "fix" 100 "budget") + @(Iss "feature" 50)
        Get-IssueBudget $l "fix" $b | Should Be 12   # 1.5 x p75 (8) of 1..10; the stuck and the feature don't count
    }
    It 'never goes over the ceiling' {
        Get-IssueBudget @(1..10 | ForEach-Object { Iss "feature" 40 }) "feature" $b | Should Be 15
    }
    It 'estimates a card at four characters a token' { Get-CardTokens ("x" * 400) | Should Be 100 }
}

Describe 'Plans and paths' {
    $lim = $rules.limits
    function Plan($chunks) { [pscustomobject]@{ chunks = $chunks } }
    function Ch($files, $model = "haiku") { [pscustomobject]@{ goal = "g"; files = $files; model = $model } }
    It 'accepts 1 to 12 chunks of up to 5 repo files on haiku or sonnet' {
        Get-PlanProblem (Plan @(Ch @("src/A.cs"))) $lim | Should BeNullOrEmpty
        Get-PlanProblem (Plan @(1..12 | ForEach-Object { Ch @("src/A.cs") "sonnet" })) $lim | Should BeNullOrEmpty
    }
    It 'refuses too many chunks, too many files, other models and paths outside the repo' {
        Get-PlanProblem (Plan @(1..13 | ForEach-Object { Ch @("src/A.cs") })) $lim | Should Be "too-big"
        Get-PlanProblem (Plan @()) $lim | Should Match 'no chunks'
        Get-PlanProblem (Plan @(Ch @(1..6 | ForEach-Object { "src/$_.cs" }))) $lim | Should Match '6 files'
        Get-PlanProblem (Plan @(Ch @("src/A.cs") "opus")) $lim | Should Match 'opus'
        foreach ($p in "../x.cs", "C:/x.cs", "/etc/x", "src/../../x") { Get-PlanProblem (Plan @(Ch @($p))) $lim | Should Match 'outside' }
    }
    It 'holds what runs on the owner PC or ships outside the plugin code, and names git would quote' {
        $held = @(".claude/settings.json", ".claude/hooks/session-start.ps1", "scripts/release/Install.ps1", "scripts/package.ps1", "tools/FlareAI.Updater/Program.cs",
                  "tests/e2e/Run-FlareE2E.ps1", "tests/e2e/flaui/X.cs", "CLAUDE.md", "src/FlareAI.Core/Update/$([char]0xE9).cs", '"src/FlareAI.Core/Update/\303\251.cs"', "src/a b.cs")
        @(Get-OwnerOnlyPaths $held).Count | Should Be $held.Count
        @(Get-OwnerOnlyPaths @("src/FlareAI.Core/Style/Rules.cs", "tests/e2e/scenarios/lint.json", "tests/FlareAI.Core.Tests/A.cs", "changes/x.md")).Count | Should Be 0
    }
    It 'accepts a test on a narrow channel only, with a plain test name' {
        $t = { param($name, $channel) $c = Ch @("src/A.cs"); $c | Add-Member test ([pscustomobject]@{ name = $name; channel = $channel }); Plan @($c) }
        Get-PlanProblem (& $t "FlareAI.Core.Tests.A.B" "core") $lim | Should BeNullOrEmpty
        Get-PlanProblem (& $t "A.B; rm -rf /" "core") $lim | Should Match 'unusable test'
        foreach ($ch in "full", "e2e", "bench", "anything") { Get-PlanProblem (& $t "A.B" $ch) $lim | Should Match 'channel' }
    }
    It 'resumes the fix round at its review, and never commits an empty chunk' {
        $buildScript | Should Match 'if \(\$s\.fixRound -and -not \$s\.fixReviewed\)'
        $buildScript | Should Match 'if \(@\(Git status --porcelain\)\.Count\) \{ Git commit'
    }
    It 'finds owner-only paths, with either slash and a leading ./' {
        Get-OwnerOnlyPaths @("src/FlareAI.Core/Style/X.cs", "src\FlareAI.Core\Update\UpdateStager.cs", "./.github/workflows/build.yml", ".claude/agents/hs-build.md", "rules/factory.json", "scripts/factory/Invoke-Build.ps1", "tests/factory/Build.Tests.ps1") |
            Should Be @("src/FlareAI.Core/Update/UpdateStager.cs", ".github/workflows/build.yml", ".claude/agents/hs-build.md", "rules/factory.json", "scripts/factory/Invoke-Build.ps1", "tests/factory/Build.Tests.ps1")
    }
}

Describe 'Overnight run fixes (2026-10-27)' {
    It 'commits the release note itself, also on a resume, and nothing else the notes agent wrote' {
        $buildScript | Should Match '(?s)\$note = "changes/factory-\$n\.md".{0,200}Git add -- \$note.{0,200}Git reset -q --hard'
        $buildScript | Should Not Match 'status --porcelain\)\.Count -gt \$before'
    }
    It 'keeps a capped try''s work for the retry instead of wiping it' {
        $buildScript | Should Match '(?s)lastResult -eq "cap"\) \{ \$failure = .{0,300}; continue \}\s*Push-Location \$s\.worktree; try \{ Git checkout'
    }
    It 'retries the edit history call and keeps its error' {
        $buildScript | Should Match '(?s)foreach \(\$wait in 0, 10, 30\).{0,800}editError ='
        $buildScript | Should Match 'not ready: \$why\$\(if \(\$null -eq \$f\.editors -and \$f\.editError\)'
    }
}

Describe 'Agents' {
    It 'climbs the ladder: the chunk model, then sonnet, then the owner' {
        (Get-ChunkAttempt "haiku" 0).model | Should Be "haiku"
        (Get-ChunkAttempt "haiku" 1).model | Should Be "sonnet"
        Get-ChunkAttempt "haiku" 2 | Should BeNullOrEmpty
    }
    It 'launches only haiku or sonnet, restricted, with no MCP servers' {
        { New-AgentArgs "opus" "Read" 10 } | Should Throw 'only haiku or sonnet'
        { New-AgentArgs "claude-opus-5-5" "Read" 10 } | Should Throw
        $a = New-AgentArgs "haiku" "Read,Grep,Edit,Bash(powershell.exe -File E:/x/test-channel.ps1:*)" 60
        ($a -join " ") | Should Match '--model haiku'
        $a -contains "--restricted" | Should Be $true
        $a -contains "--strict-mcp-config" | Should Be $true
        $a[[array]::IndexOf($a, "--tools") + 1] | Should Be "Read,Grep,Edit,Bash"
        $a[[array]::IndexOf($a, "--max-turns") + 1] | Should Be "60"
        $deny = $a[[array]::IndexOf($a, "--disallowedTools") + 1]
        foreach ($r in 'Read(~/.claude/**)', 'Read(**/.env)', 'Read(//E:/HouseStyle/planning/**)', 'Read(//E:/HouseStyle/repos/house-style/**)', 'Bash(gh:*)') { $deny.Contains($r) | Should Be $true }
    }
    It 'runs the planner with no tools at all' {
        $a = New-AgentArgs "sonnet" "" 3
        $a[[array]::IndexOf($a, "--tools") + 1] | Should Be ""
        $a -contains "--allowedTools" | Should Be $false
        $buildScript | Should Match '"hs-plan" "sonnet" \$card "" "plan"'
    }
    It 'passes only a literal haiku or sonnet from the script' {
        $models = [regex]::Matches($buildScript, 'Invoke-(?:BuildAgent|AgentTwice)\s+\$s\s+"[^"]+"\s+"[^"]+"\s+("[^"]+"|\$\S+)') | ForEach-Object { $_.Groups[1].Value }
        $models.Count | Should BeGreaterThan 5
        foreach ($m in $models) { @('"haiku"', '"sonnet"', '$attempt.model') -contains $m | Should Be $true }
        $buildScript | Should Not Match '(?i)opus'
    }
    It 'reads the agent body without its frontmatter' {
        Get-AgentBody "---`nname: x`nmodel: opus`n---`n# Body" | Should Be "# Body"
    }
    It 'reads the last JSON object from fenced or bare text' {
        (ConvertFrom-AgentJson "text`n``````json`n{""kind"":""built""}`n``````").kind | Should Be "built"
        (ConvertFrom-AgentJson '{"kind":"cannot_build"}').kind | Should Be "cannot_build"
        ConvertFrom-AgentJson "no json" | Should BeNullOrEmpty
    }
    It 'takes the answer after anything the agent quoted' {
        $t = "The diff says:`n``````json`n{""findings"":[]}`n```````nMy review:`n``````json`n{""findings"":[{""severity"":""blocking""}]}`n``````"
        @(Get-BlockingFindings (ConvertFrom-AgentJson $t)).Count | Should Be 1
    }
    It 'reads the whole board, page by page' {
        $buildScript | Should Match 'items\(first:100,after:\$c\)\{pageInfo\{hasNextPage endCursor\}'
        $buildScript | Should Match 'while \(\$page\.pageInfo\.hasNextPage\)'
    }
    It 'commits with message files, publishes no binaries, and checks a title-and-body hash' {
        $buildScript | Should Not Match 'commit -q -m'
        $buildScript | Should Match 'core\.quotePath=false diff --name-only'
        $buildScript | Should Match 'Where-Object \{ \$_ -match "\^-\\s" \}'
        $buildScript | Should Match 'Get-BodyHash \(\$cur\.title'
    }
    It 'records every call with its issue, stage, model and dollars' {
        $e = New-BuildLedgerEntry 12 "build-1-0" "haiku" ([pscustomobject]@{ exit = 0; seconds = 30; timedOut = $false }) (ConvertFrom-AgentOutput '{"type":"result","num_turns":4,"total_cost_usd":0.02,"usage":{"input_tokens":10,"output_tokens":5}}') "ok"
        $e.issue | Should Be 12; $e.model | Should Be "haiku"; $e.cost_usd | Should Be 0.02; $e.kind | Should Be "call"
        $e.Contains("report") | Should Be $false
    }
}

Describe 'Review, checks and comments' {
    It 'treats a missing review as a failure, never as no findings' {
        { Get-BlockingFindings $null } | Should Throw
        { Get-BlockingFindings ([pscustomobject]@{ summary = "fine" }) } | Should Throw
        @(Get-BlockingFindings ([pscustomobject]@{ findings = @() })).Count | Should Be 0
        @(Get-BlockingFindings ([pscustomobject]@{ findings = @([pscustomobject]@{ severity = "blocking" }, [pscustomobject]@{ severity = "note" }) })).Count | Should Be 1
    }
    It 'needs all three checks green on the head commit' {
        $req = [string[]]$rules.requiredChecks
        $run = { param($n, $sha, $st, $c) [pscustomobject]@{ name = $n; sha = $sha; status = $st; conclusion = $c } }
        $green = @("build", "feedback-worker", "e2e" | ForEach-Object { & $run $_ "h" "completed" "success" })
        (Get-CheckState $green "h" $req).state | Should Be "pass"
        (Get-CheckState $green "other" $req).state | Should Be "pending"
        (Get-CheckState @($green[0], $green[1]) "h" $req).state | Should Be "pending"
        (Get-CheckState @($green[0], $green[1], (& $run "e2e" "h" "completed" "failure")) "h" $req).names | Should Be "e2e"
    }
    It 'posts a fixed-form comment with a known reason code' {
        Format-StuckComment "build" 2 "budget" | Should Match '`budget`'
        Format-StuckComment "build" 2 "anything an agent wrote" | Should Match '`build-failed`'
    }
    It 'runs the AI scenarios on a factory branch only when the change touches the AI' {
        foreach ($p in "src/FlareAI.Core/Llm/AnthropicClient.cs", "src/FlareAI.Core/Prompts/review.md", "src/FlareAI.Core/Actions/ReviewSession.cs", "tests/e2e/scenarios/ai-turned-off.ps1") { $p | Should Match $rules.aiPaths }
        foreach ($p in "src/FlareAI.Core/Style/StyleEngine.cs", "src/FlareAI.Core/Actions/RuleChanges.cs", "tests/e2e/scenarios/rules-edit.ps1", "src/FlareAI.Flare/UI/FindingsPanel.cs") { $p | Should Not Match $rules.aiPaths }
        $e2e = Get-Content (Join-Path $root '.github\workflows\e2e.yml') -Raw
        $e2e | Should Match 'aiPaths'
        $e2e | Should Not Match 'secrets\.ANTHROPIC_API_KEY'   # the runner also runs factory code, so never the main key
        $e2e | Should Match "ANTHROPIC_API_KEY: ''"
        $e2e | Should Match "HOUSESTYLE_TEST_BIFROST_KEY: \$\{\{ \(startsWith\(github\.ref_name, 'factory/'\)"
        $e2e | Should Match "runs-on: \[self-hosted, windows, flare\]"   # the key is for the Flare VM only
    }
    It 'keeps the limits the spec names in rules/factory.json' {
        $rules.limits.chunksPerIssue | Should Be 12
        $rules.limits.cardTokens | Should Be 80000
        $rules.budget.ceilingUsd | Should Be 15
        @($rules.requiredChecks) -join "," | Should Be "build,feedback-worker,e2e"
    }
}
