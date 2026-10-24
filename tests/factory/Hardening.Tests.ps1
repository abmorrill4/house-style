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
    $f = [pscustomobject]@{ repro = "pass"; lines = 40; files = 2; behavior = @(); owner_only = @(); overlap = @(); tamper = @(); area = "Style" }
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
    It 'any added line in an existing test file that is not C# or Pester goes to review' {
        $d = New-RealDiff { param($r) Add-Content (Join-Path $r "tests/FlareAI.Core.Tests/OldTests.cs") "public void X() { }" -NoNewline }
        @(Get-TestTamper (Split-Diff $d.diff)).Count | Should Be 1
        Test-WholeTests @("a") "" | Should Be $false
    }
    It 'an owner-approved decision:fix that tampers opens a draft PR instead of merging' {
        $factoryScript | Should Match '\$tamperDraft = -not \$routed -and @\(\$facts\.tamper'
        $factoryScript | Should Match '\$draft = \(\$routed -and \$routed\.route -eq "fix-for-review"\) -or \$tamperDraft'
        $factoryScript.IndexOf('if ($tamperDraft) {') | Should BeLessThan $factoryScript.IndexOf('Gh pr merge')
        $factoryScript.IndexOf('$draft = ($routed') | Should BeLessThan $factoryScript.IndexOf('if ($draft) { $prArgs += "--draft" }')
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
    It 'the e2e harness stays a hard deny in the factory' {
        $m = [regex]::Match($factoryScript, "(?m)^\`$denied = '([^']+)'")
        $m.Success | Should Be $true
        foreach ($p in "tests/e2e/Run-FlareE2E.ps1", "tests/e2e/FlareE2E.psm1", "tests/e2e/flaui/FlaUI.Deps.csproj", "src/X/X.csproj", "Directory.Build.props") { $p -match $m.Groups[1].Value | Should Be $true }
        "tests/e2e/scenarios/new-one.ps1" -match $m.Groups[1].Value | Should Be $false
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
    It 'Invoke-Factory.ps1 checks it with the owner account' { $factoryScript | Should Match 'Test-FixAllowed \$null \(Get-Labels \$r\) \$events \$owner' }
}
