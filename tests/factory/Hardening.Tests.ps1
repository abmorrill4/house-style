# Pester 3 tests for the factory hardening in Triage.psm1 (command center spec section 4): the test-tamper guard, lane
# suspension, reason codes, the secret-shape check and the owner check on decision:fix.
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $root 'scripts\factory\Triage.psm1') -Force
$rules = Get-Content (Join-Path $root 'rules\routing.json') -Raw | ConvertFrom-Json
$factoryScript = Get-Content (Join-Path $root 'scripts\factory\Invoke-Factory.ps1') -Raw

function New-Brief {
    [pscustomobject]@{
        kind = "bug"; summary = "s"; goal = "g"; impact = "i"; reproduced = [pscustomobject]@{ result = "yes"; how = "h" }
        matches = @(); options = @(); recommendation = $null; questions = @(); confidence = "high"; security = $false; docs_gap = $false
        repro_test = "FlareAI.Core.Tests.IgnoreTests.IgnoreSurvivesRelint"
    }
}
function New-Fix([hashtable]$over = @{}) {
    $f = [pscustomobject]@{ repro = "pass"; lines = 40; files = 2; behavior = @(); owner_only = @(); overlap = @(); tamper = @(); area = "Style"; paths = @("src/FlareAI.Core/Style/IgnoreList.cs", "tests/FlareAI.Core.Tests/IgnoreTests.cs"); uncovered = @() }
    foreach ($k in $over.Keys) { $f.$k = $over[$k] }
    $f
}
function Route-Fix($fix, $r = $rules) { $in = New-RouteInput (New-Brief) @() 3; $in.fix = $fix; Get-Route $in $r }

# A real git diff -U0 of a small repo, so the status and hunk parsing see what git really prints.
$CsBase = @"
using Xunit;

namespace FlareAI.Core.Tests
{
    public class IgnoreTests
    {
        [Fact]
        public void IgnoreSticks()
        {
            var x = Make("a");
            Assert.Equal(1, x.Count);
        }
    }
}
"@ -replace "`r`n", "`n"
$PesterBase = @"
Describe 'Ignore' {
    It 'sticks' {
        1 | Should Be 1
    }
}
"@ -replace "`r`n", "`n"
function Set-File($repo, $path, $text) { [IO.File]::WriteAllText((Join-Path $repo $path), ($text -replace "`r`n", "`n")) }

function New-RealDiff([scriptblock]$change) {
    $repo = Join-Path $TestDrive ("r" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force $repo | Out-Null
    Push-Location $repo
    try {
        $ErrorActionPreference = "Continue"
        git init -q 2>&1 | Out-Null
        git config user.email t@example.invalid; git config user.name t; git config core.autocrlf false
        foreach ($p in "tests/FlareAI.Core.Tests/IgnoreTests.cs", "tests/FlareAI.Core.Tests/OldTests.cs", "tests/baselines/bench-lint.json", "src/FlareAI.Core/Style/Ignore.cs") {
            New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
            [IO.File]::WriteAllText((Join-Path $repo $p), "line one`nline two`nline three`n")
        }
        Set-File $repo "tests/FlareAI.Core.Tests/IgnoreTests.cs" $CsBase
        New-Item -ItemType Directory -Force (Join-Path $repo "tests/agent") | Out-Null
        Set-File $repo "tests/agent/Ignore.Tests.ps1" $PesterBase
        git add -A 2>&1 | Out-Null; git commit -q -m base 2>&1 | Out-Null
        & $change $repo
        git add -A 2>&1 | Out-Null
        @{ diff = ((git diff --cached --no-renames -U0) -join "`n"); numstat = @(git diff --cached --no-renames --numstat) }
    }
    finally { Pop-Location }
}

Describe 'The test-tamper guard (spec section 4.2)' {
    $newFact = @"

        [Fact]
        public void IgnoreSurvivesRelint()
        {
            var x = Make("a { b");
            if (x != null) { Assert.Equal(1, x.Count); }
        }
"@
    It 'allows a new [Fact] method added to an existing test class' {
        $d = New-RealDiff { param($r) Set-File $r "tests/FlareAI.Core.Tests/IgnoreTests.cs" ($CsBase.Replace("        }`n    }`n}", "        }`n$($newFact -replace "`r`n", "`n")`n    }`n}")) }
        $f = Split-Diff $d.diff
        @($f["tests/FlareAI.Core.Tests/IgnoreTests.cs"].removed).Count | Should Be 0
        @(Get-TestTamper $f).Count | Should Be 0
    }
    It 'allows a new [Theory] with [InlineData] rows' {
        $theory = "`n        [Theory]`n        [InlineData(1)]`n        [InlineData(2)]`n        public void Counts(int n)`n        {`n            Assert.True(n > 0);`n        }"
        $d = New-RealDiff { param($r) Set-File $r "tests/FlareAI.Core.Tests/IgnoreTests.cs" ($CsBase.Replace("        }`n    }`n}", "        }`n$theory`n    }`n}")) }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 0
    }
    It 'sends an early return added inside an existing test to review' {
        $d = New-RealDiff { param($r) Set-File $r "tests/FlareAI.Core.Tests/IgnoreTests.cs" ($CsBase.Replace("        {`n            var x", "        {`n            return;`n            var x")) }
        $f = Split-Diff $d.diff
        @($f["tests/FlareAI.Core.Tests/IgnoreTests.cs"].removed).Count | Should Be 0   # added lines only, and still caught
        Get-TestTamper $f | Should Be "tests/FlareAI.Core.Tests/IgnoreTests.cs (added lines that aren't whole new tests)"
    }
    It 'sends a Skip to review, on an existing test or on a new one' {
        $d = New-RealDiff { param($r) Set-File $r "tests/FlareAI.Core.Tests/IgnoreTests.cs" ($CsBase.Replace("[Fact]", "[Fact(Skip = `"later`")]")) }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 1
        $skipped = $newFact.Replace("[Fact]", "[Fact(Skip = `"later`")]")
        $d = New-RealDiff { param($r) Set-File $r "tests/FlareAI.Core.Tests/IgnoreTests.cs" ($CsBase.Replace("        }`n    }`n}", "        }`n$($skipped -replace "`r`n", "`n")`n    }`n}")) }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 1
    }
    It 'sends a new using, a helper method or an attribute that is not a test to review' {
        $d = New-RealDiff { param($r) Set-File $r "tests/FlareAI.Core.Tests/IgnoreTests.cs" ("using Assert = Fake.Assert;`n" + $CsBase) }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 1
        Test-WholeTests @("        private static int Helper()", "        {", "            return 1;", "        }") "cs" | Should Be $false
        Test-WholeTests @("        [Obsolete]", "        [Fact]", "        public void A()", "        {", "        }") "cs" | Should Be $false
        Test-WholeTests @("        [Fact]", "        public void A()", "        {") "cs" | Should Be $false   # unbalanced
    }
    It 'allows a new It block in an existing Pester file, and refuses a skipped one or a line inside an existing It' {
        $d = New-RealDiff { param($r) Set-File $r "tests/agent/Ignore.Tests.ps1" ($PesterBase.Replace("    }`n}", "    }`n    It 'survives a relint' {`n        `$x = @{ a = '}' }`n        2 | Should Be 2`n    }`n}")) }
        $f = Split-Diff $d.diff
        @($f["tests/agent/Ignore.Tests.ps1"].removed).Count | Should Be 0
        @(Get-TestTamper $f).Count | Should Be 0
        Test-WholeTests @("    It 'later' -Skip {", "    }") "pester" | Should Be $false
        $d = New-RealDiff { param($r) Set-File $r "tests/agent/Ignore.Tests.ps1" ($PesterBase.Replace("    It 'sticks' {`n", "    It 'sticks' {`n        return`n")) }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 1
    }
    # Hunks that each look like whole new tests but switch off the existing tests between them.
    It 'refuses new [Fact]s that wrap existing tests in #if NEVER ... #endif' {
        $first = @("        [Fact]", "        public void A()", "        {", "        }", "#if NEVER")
        $second = @("#endif", "        [Fact]", "        public void B()", "        {", "        }")
        Test-WholeTests $first "cs" | Should Be $false
        Test-WholeTests $second "cs" | Should Be $false
        $d = New-RealDiff { param($r) Set-File $r "tests/FlareAI.Core.Tests/IgnoreTests.cs" ($CsBase.Replace("        [Fact]`n        public void IgnoreSticks()", (($first -join "`n") + "`n        [Fact]`n        public void IgnoreSticks()")).Replace("        }`n    }`n}", "        }`n" + ($second -join "`n") + "`n    }`n}")) }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 1
    }
    It 'refuses Pester blocks that open a block comment over existing tests' {
        Test-WholeTests @("    It 'a' {", "        1 | Should Be 1", "    } <#") "pester" | Should Be $false
        Test-WholeTests @("    It 'b' { #>", "        1 | Should Be 1", "    }") "pester" | Should Be $false
    }
    It 'refuses a new [Fact] that ends by opening a block comment' {
        Test-WholeTests @("        [Fact]", "        public void C()", "        {", "        } /*") "cs" | Should Be $false
        Test-WholeTests @("        [Fact]", "        public void C()", "        {", "        } /* closed */") "cs" | Should Be $true
    }
    It 'refuses an open verbatim or raw string, and a Pester here-string' {
        Test-WholeTests @("        [Fact]", "        public void D()", "        {", "            var s = @`"", "        }") "cs" | Should Be $false
        Test-WholeTests @("        [Fact]", "        public void D()", "        {", "            var s = `"`"`"", "        }") "cs" | Should Be $false
        Test-WholeTests @("        [Fact]", "        public void D()", "        {", "            var s = @`"a { b`";", "        }") "cs" | Should Be $true
        Test-WholeTests @("    It 'e' {", "        `$s = @'", "    }") "pester" | Should Be $false
    }
    It 'any added line in an existing test file that is not C# or Pester goes to review' {
        $d = New-RealDiff { param($r) Add-Content (Join-Path $r "tests/FlareAI.Core.Tests/OldTests.cs") "public void X() { }" -NoNewline }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 1
        Test-WholeTests @("a") "" | Should Be $false
    }
    It 'an owner-approved decision:fix that tampers opens a draft PR instead of merging' {
        $factoryScript | Should Match '\$tamper = @\(\$routeIn\.fix\.tamper \| Where-Object \{ \$_ \}\)'
        $factoryScript | Should Match '\$tamperDraft = \[bool\]\$why\.Count'
        $factoryScript | Should Match 'Publish-Fix \$n \$commit \$prTitle \(\$prBody \+ \$sign\) \$tamperDraft \(-not \$tamperDraft\)'
        # Publish-Fix merges only an auto-fix that isn't a draft.
        $factoryScript | Should Match 'if \(\$draft -or -not \$autoFix\) \{ return \$url \}'
        $factoryScript.IndexOf('if ($draft -or -not $autoFix) { return $url }') | Should BeLessThan $factoryScript.IndexOf('Gh pr merge')
    }
    It 'allows a new test file' {
        $d = New-RealDiff { param($r) [IO.File]::WriteAllText((Join-Path $r "tests/FlareAI.Core.Tests/NewTests.cs"), "class NewTests {}`n") }
        $f = Split-Diff $d.diff
        $f["tests/FlareAI.Core.Tests/NewTests.cs"].status | Should Be "A"
        @(Get-TestTamper $f).Count | Should Be 0
    }
    It 'sends a changed line in an existing test file to review' {
        $d = New-RealDiff { param($r) [IO.File]::WriteAllText((Join-Path $r "tests/FlareAI.Core.Tests/OldTests.cs"), "line one`nline 2 changed`nline three`n") }
        $t = @(Get-TestTamper (Split-Diff $d.diff))
        $t | Should Be "tests/FlareAI.Core.Tests/OldTests.cs (changed or deleted lines)"
        (Route-Fix (New-Fix @{ tamper = $t })).route | Should Be "fix-for-review"
    }
    It 'sends a deleted line, and a deleted test file, to review' {
        $d = New-RealDiff { param($r) [IO.File]::WriteAllText((Join-Path $r "tests/FlareAI.Core.Tests/OldTests.cs"), "line one`nline three`n") }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 1
        $d = New-RealDiff { param($r) Remove-Item (Join-Path $r "tests/FlareAI.Core.Tests/OldTests.cs") }
        $f = Split-Diff $d.diff
        $f["tests/FlareAI.Core.Tests/OldTests.cs"].status | Should Be "D"
        Get-TestTamper $f | Should Be "tests/FlareAI.Core.Tests/OldTests.cs (deleted)"
    }
    It 'sends any change under tests/baselines/ to review, an added line too' {
        $d = New-RealDiff { param($r) Add-Content (Join-Path $r "tests/baselines/bench-lint.json") "more" -NoNewline }
        Get-TestTamper (Split-Diff $d.diff) | Should Match 'tests/baselines/bench-lint\.json \(protected'
    }
    It 'sends a new file in a protected folder to review' {
        foreach ($p in "tests/fixtures/rules/google/new-rule.htm", "tests/FlareAI.Scoring.Tests/fixtures/x.json", "tests/FlareAI.Scenarios/Guide/golden/out.htm",
                       "tests/FlareAI.Scenarios/Output/html5/Content/x.htm", "corpus/ground-truth.json", "corpus/DigitalBanking/labels.jsonl",
                       "tests/FlareAI.TestKit/Llm/Recorded.cs", "tests/Isolation/IsolatedTestFramework.cs", "tests/Directory.Build.targets",
                       "tests/FlareAI.Core.Tests/FlareAI.Core.Tests.csproj") {
            $f = @{ $p = @{ added = @("x"); removed = @(); status = "A" } }
            @(Get-TestTamper $f).Count | Should Be 1
        }
    }
    It 'leaves source changes alone' {
        $d = New-RealDiff { param($r) [IO.File]::WriteAllText((Join-Path $r "src/FlareAI.Core/Style/Ignore.cs"), "line one`nfixed`nline three`n") }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 0
    }
    It 'Get-DiffFacts reports it and Get-Route sends the fix for review with the reason' {
        $d = New-RealDiff { param($r) [IO.File]::WriteAllText((Join-Path $r "tests/FlareAI.Core.Tests/OldTests.cs"), "line one`nline three`n") }
        $facts = Get-DiffFacts $d.numstat $d.diff "" $rules
        @($facts.tamper).Count | Should Be 1
        $route = Route-Fix (New-Fix @{ tamper = $facts.tamper })
        ($route.reasons -join ' ') | Should Match 'changes existing tests or protected test files'
        (Route-Fix (New-Fix)).route | Should Be "auto-fix"
    }
    It 'the e2e harness stays a hard deny in the factory (PatchRules.psd1)' {
        Import-Module (Join-Path $root 'scripts\factory\Guards.psm1') -Force
        $pr = Import-PatchRules (Join-Path $root 'scripts\factory\PatchRules.psd1')
        foreach ($p in "tests/e2e/Run-FlareE2E.ps1", "tests/e2e/FlareE2E.psm1", "tests/e2e/flaui/FlaUI.Deps.csproj", "src/X/X.csproj", "tests/Directory.Build.props") { Test-PatchPath $p $pr | Should Not BeNullOrEmpty }
        Test-PatchPath "tests/e2e/scenarios/new-one.ps1" $pr | Should BeNullOrEmpty
    }
    It 'Invoke-Factory.ps1 passes the tamper list and area into the route' { $factoryScript | Should Match 'tamper = @\(\$facts\.tamper\); area = \$facts\.area' }
}

Describe 'Lane suspension (spec section 4.6)' {
    It 'rules/routing.json lists suspended areas and the starting targets' {
        @($rules.suspended).Count | Should Be 0
        $rules.PSObject.Properties.Name -contains "suspended" | Should Be $true
        $rules.startingTargets.security | Should Be 2; $rules.startingTargets.bug | Should Be 5
        $rules.startingTargets.feature | Should Be 30; $rules.startingTargets.improvement | Should Be 30
    }
    It 'the area is the folder the fix changed most, on real paths' {
        Get-FixArea @("3`t1`tsrc/FlareAI.Core/Style/StyleEngine.cs", "40`t2`ttests/FlareAI.Core.Tests/StyleTests.cs") | Should Be "Style"
        Get-FixArea @("2`t0`tsrc/FlareAI.Core/Style/GuideLayer.cs", "10`t5`tsrc/FlareAI.Flare/UI/IssuesPane.cs") | Should Be "UI"
        Get-FixArea @("1`t1`tsrc/FlareAI.Flare/Plugin.cs") | Should Be "Flare"
        Get-FixArea @("1`t1`tsrc/FlareAI.Core/Paths.cs") | Should Be "Core"
        Get-FixArea @("1`t1`tstyleguide/Company/Rules.flsnp") | Should BeNullOrEmpty
    }
    It 'a fix in a suspended area goes for review; elsewhere it can still auto-fix' {
        $r = $rules | ConvertTo-Json -Depth 6 | ConvertFrom-Json; $r.suspended = @("Style")
        $route = Route-Fix (New-Fix @{ area = "Style" }) $r
        $route.route | Should Be "fix-for-review"
        ($route.reasons -join ' ') | Should Match 'suspended for the Style area'
        (Route-Fix (New-Fix @{ area = "UI" }) $r).route | Should Be "auto-fix"
    }
    $log = "$([char]0x1e)2026-10-20T10:00:00+00:00$([char]0x1f)Revert `"Fix tester report 12`"`n`nThis reverts commit $("a" * 40), reversing`nchanges made to $("b" * 40).`n" +
           "$([char]0x1e)2026-10-21T10:00:00+00:00$([char]0x1f)Ordinary commit`n"
    $prs = @([pscustomobject]@{ number = 300; mergedAt = "2026-10-18T10:00:00Z"; mergeCommit = [pscustomobject]@{ oid = "a" * 40 }
                                files = @([pscustomobject]@{ path = "src/FlareAI.Core/Style/Ignore.cs"; additions = 5; deletions = 2 }, [pscustomobject]@{ path = "tests/FlareAI.Core.Tests/IgnoreTests.cs"; additions = 30; deletions = 0 }) },
             [pscustomobject]@{ number = 301; mergedAt = "2026-10-18T11:00:00Z"; mergeCommit = [pscustomobject]@{ oid = "c" * 40 }
                                files = @([pscustomobject]@{ path = "src/FlareAI.Flare/UI/IssuesPane.cs"; additions = 5; deletions = 2 }) })
    It 'finds reverted merge commits in main''s history' {
        $rev = Get-RevertedShas $log
        $rev.Count | Should Be 1
        $rev.ContainsKey("a" * 40) | Should Be $true
    }
    It 'a reverted auto-fix suspends its area, and only that one' {
        $fixes = @(Get-RevertedFixes $prs (Get-RevertedShas $log))
        $fixes.Count | Should Be 1; $fixes[0].area | Should Be "Style"; $fixes[0].number | Should Be 300
        Get-SuspendedAreas $rules $fixes | Should Be "Style"
    }
    It 'nothing is reinstated automatically: only the owner''s reinstated date lifts it' {
        $fixes = @(Get-RevertedFixes $prs (Get-RevertedShas $log))
        $r = $rules | ConvertTo-Json -Depth 6 | ConvertFrom-Json
        $r.reinstated = [pscustomobject]@{ Style = "2026-11-01" }
        @(Get-SuspendedAreas $r $fixes).Count | Should Be 0
        $r.reinstated = [pscustomobject]@{ Style = "2026-10-01" }   # reinstated before this revert: suspended again
        Get-SuspendedAreas $r $fixes | Should Be "Style"
    }
    It 'keeps the areas rules/routing.json already lists' {
        $r = $rules | ConvertTo-Json -Depth 6 | ConvertFrom-Json; $r.suspended = @("UI")
        @(Get-SuspendedAreas $r @(Get-RevertedFixes $prs (Get-RevertedShas $log))) -join ',' | Should Be "UI,Style"
    }
    It 'Invoke-Factory.ps1 applies the suspension before routing' {
        $factoryScript.IndexOf('-NotePropertyName suspended') | Should BeGreaterThan 0
        $factoryScript.IndexOf('-NotePropertyName suspended') | Should BeLessThan $factoryScript.IndexOf('$route = Get-Route $in $rules')
    }
}

Describe 'Reason codes' {
    It 'maps Get-Route and stop reasons to the fixed list' {
        (Get-ReasonCodes @("the fix changes 200 lines, over 150", "confidence is low, not high", "the fix changes 9 files, over 5")) -join ',' | Should Be "too-big,low-confidence"
        Get-ReasonCodes @("it wasn't reproduced") | Should Be "not-reproduced"
        Get-ReasonCodes @("auto-fix is suspended for the Style area after a reverted fix") | Should Be "suspended-area"
        Get-ReasonCodes @("today's 3 auto-fixes are used up, so it waits for tomorrow") | Should Be "daily-cap"
        Get-ReasonCodes @("Stopped by a cap, because the agent ran past its 45-minute limit and was stopped.") | Should Be "run-cap"
        Get-ReasonCodes @("A holdout scenario fails on the fix.") | Should Be "holdout"
        Get-ReasonCodes @("something nobody wrote a code for") | Should Be "other"
    }
}

Describe 'Secret shapes in published text' {
    It 'finds keys and tokens' {
        foreach ($t in "key sk-ant-api03-abcdefghijklmnop", "ghp_$('a1' * 12)", "github_pat_$('A1' * 12)", 'apiKey = "q8Zr2LmP0xVb7TnYs4Kw9Hd"', "Authorization: Bearer $('x9' * 12)") {
            Test-SecretShape $t | Should Be $true
            (Test-OurWords @{ "the PR" = $t } "" 8) | Should Match 'shaped like a key or token'
        }
    }
    It 'leaves ordinary code alone' {
        foreach ($t in 'var token = GetToken();', 'string password = settings.Password;', 'task-runner', 'if (secret == null) return;') { Test-SecretShape $t | Should Be $false }
    }
    It 'a hit holds the fix: nothing is pushed' {
        $in = New-RouteInput (New-Brief) @() 3; $in.our_words = $false
        (Get-Route $in $rules).route | Should Be "hold"
    }
}

Describe 'decision:fix must come from the owner' {
    function Ev($label, $who) { [pscustomobject]@{ event = "labeled"; label = [pscustomobject]@{ name = $label }; actor = [pscustomobject]@{ login = $who } } }
    It 'passes when the owner added it' { Test-FixGate @("decision:fix") @((Ev "decision:fix" "abmorrill4")) "abmorrill4" | Should BeNullOrEmpty }
    It 'refuses when any other account added it' {
        Test-FixGate @("decision:fix") @((Ev "decision:fix" "someone-else")) "abmorrill4" | Should Match 'other than the owner'
        Test-FixAllowed $null @("decision:fix") @((Ev "decision:fix" "abmorrill4"), (Ev "decision:fix" "bot[bot]")) "abmorrill4" | Should Match 'other than the owner'
    }
    It 'Invoke-Factory.ps1 checks it with the owner account' { $factoryScript | Should Match 'Test-FixAllowed \$null \(Get-Labels \$r\) \$events \$Run\.owner' }
}

# ==== Shape (factory-runtime spec, P15) =============================================================================
Describe 'Workflows, credentials, host calls and text (P15)' {
    $wfDir = Join-Path $root '.github\workflows'
    $factoryYml = Get-Content (Join-Path $wfDir 'factory.yml') -Raw
    $e2eYml = Get-Content (Join-Path $wfDir 'e2e.yml') -Raw
    # The workflow hunks are the owner's commit (workflows.patch, migration step 5). Until then their checks skip.
    $applied = $factoryYml -match 'runs-on: \[self-hosted, windows, agents\]'
    $pending = 'workflow hunks not committed yet (migration step 5)'
    $files = @(& git.exe -C $root ls-files --cached --others --exclude-standard)

    It ($(if ($applied) { 'factory.yml runs only on agents and never names flare; agents appears in no other workflow' } else { "factory.yml runs only on agents: $pending" })) -Skip:(-not $applied) {
        $factoryYml | Should Match 'runs-on: \[self-hosted, windows, agents\]'
        $factoryYml | Should Not Match '\bflare\]'
        foreach ($w in Get-ChildItem $wfDir -Filter *.yml) {
            if ($w.Name -ne 'factory.yml') { (Get-Content $w.FullName -Raw) | Should Not Match 'runs-on:.*\bagents\b' }
        }
        $factoryYml | Should Not Match 'ANTHROPIC_API_KEY'
        $factoryYml | Should Match 'Test-FactoryReady\.ps1 -NoFlare'
        $factoryYml | Should Match 'name: Check the agents VM is ready'
        $factoryYml | Should Match 'usage limit was reached'
    }
    It ($(if ($applied) { 'factory.yml''s capture input keeps the FACTORY_MODE gate and has its own concurrency group' } else { "capture, its gate and its group: $pending" })) -Skip:(-not $applied) {
        $factoryYml | Should Match '(?m)^\s+capture:\s*$'
        $factoryYml | Should Match ([regex]::Escape("group: factory-`${{ inputs.capture && 'capture' || 'run' }}"))
        $factoryYml | Should Match "if: github\.repository == 'abmorrill4/house-style' && \(vars\.FACTORY_MODE == 'shadow' \|\| vars\.FACTORY_MODE == 'live'\)"
        $factoryYml | Should Match ([regex]::Escape('if ($env:CAPTURE -eq ''true'') { $a += "-Capture" }'))
    }
    It ($(if ($applied) { 'e2e.yml ignores factory/* pushes and skips a manual run on a factory/* ref' } else { "e2e.yml's filter: $pending" })) -Skip:(-not $applied) {
        $e2eYml | Should Match ([regex]::Escape("branches-ignore: ['factory/**']"))
        $e2eYml | Should Match ([regex]::Escape("!startsWith(github.ref, 'refs/heads/factory/')"))
        $e2eYml | Should Match 'name: Check the Flare VM is ready'
    }
    It 'every checkout of repo code keeps no credentials, and no secret appears inside a run: body' {
        foreach ($w in Get-ChildItem $wfDir -Filter *.yml) {
            $lines = @(Get-Content $w.FullName)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match 'uses: actions/checkout@') {
                    $block = ($lines[($i + 1)..([Math]::Min($i + 6, $lines.Count - 1))] | Where-Object { $_ -notmatch '^\s*-\s' }) -join "`n"
                    if ($block -notmatch 'persist-credentials: false') { throw "$($w.Name) line $($i + 1) checks out with credentials." }
                }
                if ($lines[$i] -match '^(\s*)(-\s+)?run:\s*(.*)$') {
                    $indent = $Matches[1].Length + $(if ($Matches[2]) { $Matches[2].Length } else { 0 })
                    $body = @($Matches[3])
                    for ($j = $i + 1; $j -lt $lines.Count -and ($lines[$j] -match '^\s*$' -or ($lines[$j] -match '^(\s*)' -and $Matches[1].Length -gt $indent)); $j++) { $body += $lines[$j] }
                    if (($body -join "`n") -match '\$\{\{\s*secrets\.') { throw "$($w.Name) line $($i + 1) puts a secret inside a run: body." }
                }
            }
        }
    }
    It 'the old VM name appears in no tracked file' {
        $old = "factory " + "VM"
        $skip = '\.(png|jpg|ico|dll|exe|zip|pfx|snk|nupkg|trx)$'
        $hits = foreach ($f in $files) {
            if ($f -match $skip) { continue }
            if (-not $applied -and $f -like '.github/workflows/*') { continue }   # the owner's commit fixes these
            $p = Join-Path $root $f
            if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { continue }
            if ((Get-Item -LiteralPath $p).Length -gt 5MB) { continue }
            if ([IO.File]::ReadAllText($p) -match "(?i)$old") { $f }
        }
        @($hits) -join ', ' | Should BeNullOrEmpty
    }
    It 'the kill-switch line in CLAUDE.md and the factory README says who sets off, shadow and live' {
        foreach ($f in 'CLAUDE.md', 'scripts\factory\README.md') {
            $t = (Get-Content (Join-Path $root $f) -Raw) -replace '\s+', ' '
            $t | Should Match 'Claude may set `off` or `shadow` under the owner-ops authorization\. Only the owner sets `live`\.'
            $t | Should Not Match 'Only the owner changes it'
        }
    }
    It 'no task registration passes -ScratchVM, and no task runs a hand-run setup script' {
        . (Join-Path $root 'scripts\factory\Install-HostTasks.ps1')
        foreach ($t in $HostTasks) {
            $t.args | Should Not Match 'ScratchVM'
            $t.script | Should Not Match '^(New-FactoryVM|Install-FactoryTools|Install-Runner|Set-FactoryEgress|Save-Fixtures|Test-FactoryIsolation)\.ps1$'
        }
    }
    It 'host calls into the agents VM appear only on the allow list' {
        $dir = Join-Path $root 'scripts\factory'
        $setup = 'New-FactoryVM.ps1', 'Install-FactoryTools.ps1', 'Install-Runner.ps1', 'Set-FactoryEgress.ps1', 'Save-Fixtures.ps1', 'Test-FactoryIsolation.ps1'
        foreach ($f in Get-ChildItem $dir -Include *.ps1, *.psm1 -Recurse) {
            $src = Get-Content $f.FullName -Raw
            if ($setup -contains $f.Name) { continue }
            # Direct calls: only Invoke-VMCall's own body in FactoryVM.psm1, and the reset's on the Flare VM.
            if ($f.Name -ne 'FactoryVM.psm1') { $src | Should Not Match 'Invoke-Command' }
            if ($f.Name -ne 'Reset-FactoryVM.ps1') { $src | Should Not Match 'New-PSSession|Copy-VMFile' }
            else { [regex]::Matches($src, '(New-PSSession|Copy-VMFile)[^\r\n]*') | ForEach-Object { $_.Value | Should Match 'FlareVMName' } }
            # Invoke-VMCall into the agents VM: Write-AgentsVM and the claim (FactoryVM.psm1), and the reset's quiesce,
            # carry and wait (Reset-FactoryVM.ps1). Anywhere else, only into the Flare VM.
            if ('FactoryVM.psm1', 'Reset-FactoryVM.ps1' -notcontains $f.Name) {
                [regex]::Matches($src, 'Invoke-VMCall\s+(\S+)') | ForEach-Object { $_.Groups[1].Value | Should Match '^\$(FlareVMName|script:CheckVM|vm|FlareVM)$' }
                $src | Should Not Match 'Invoke-VMCall\s+\$AgentsVMName|Invoke-VMCall\s+"?HSAGENTS'
            }
            # -AsReset: only Reset-FactoryVM.ps1 passes it.
            if ('FactoryVM.psm1', 'Reset-FactoryVM.ps1' -notcontains $f.Name) { $src | Should Not Match '-AsReset' }
        }
        $watch = Get-Content (Join-Path $dir 'Watch-Factory.ps1') -Raw
        [regex]::Matches($watch, 'Invoke-VMCall\s+(\S+)') | ForEach-Object { $_.Groups[1].Value | Should Be '$FlareVMName' }
    }
    It 'rules/routing.json has the auto-fix path rule exactly, and its _about mentions it' {
        $r = Get-Content (Join-Path $root 'rules\routing.json') -Raw | ConvertFrom-Json
        @($r.autoFix.paths) -join '|' | Should BeExactly '^(src|tests)/.+\.cs$'
        @($r.autoFix.dataPaths) -join '|' | Should BeExactly '^changes/(?!README\.md$)[^/]+\.md$'
        $r._about | Should Match 'autoFix\.paths and autoFix\.dataPaths'
    }
}

Describe 'Native and cmdlet arguments' {
    It 'never passes a bare cast in argument mode, which PowerShell reads as text' {
        # `-ArgumentList $a, [bool]$b` passes the string "[bool]False", which is truthy. Wrap casts in parentheses.
        $root = Join-Path $PSScriptRoot "..\.."
        $hits = @(Get-ChildItem (Join-Path $root "scripts") -Recurse -Include *.ps1, *.psm1 | Select-String -Pattern '-ArgumentList\b[^(\r\n]*?(,\s*|\s)\[(bool|int|string)\]\$' |
                  ForEach-Object { "$($_.Path | Split-Path -Leaf):$($_.LineNumber)" })
        $hits | Should BeNullOrEmpty
    }
}
