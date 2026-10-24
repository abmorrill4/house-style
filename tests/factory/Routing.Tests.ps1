# Pester 3 tests for the factory's routing (work/report-pipeline spec section 2):
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here '..\..\scripts\factory\Triage.psm1') -Force
$rules = Get-Content (Join-Path $here '..\..\rules\routing.json') -Raw | ConvertFrom-Json
$cases = (Get-Content (Join-Path $here 'routing-cases.json') -Raw | ConvertFrom-Json).cases
$areas = Get-Content (Join-Path $here '..\..\rules\areas.json') -Raw | ConvertFrom-Json

function New-Brief {
    [pscustomobject]@{
        kind = "bug"; summary = "Ignoring an issue doesn't stick after a relint."; goal = "Hide a false positive."
        impact = "Anyone who ignores an issue, every relint. Slows them, doesn't block."
        reproduced = [pscustomobject]@{ result = "yes"; how = "IgnoreSurvivesRelint fails" }
        matches = @(); options = @([pscustomobject]@{ option = "Fix now"; effort = "S"; risk = "Low"; for_writers = "Ignore sticks." },
                                   [pscustomobject]@{ option = "Park"; effort = "S"; risk = "None"; for_writers = "Nothing changes." })
        recommendation = [pscustomobject]@{ option = "Fix now"; why = "Small and clear." }
        questions = @(); confidence = "high"; security = $false; docs_gap = $false
        repro_test = "FlareAI.Core.Tests.IgnoreTests.IgnoreSurvivesRelint"
    }
}
function New-Input([hashtable]$over = @{}) {
    $in = New-RouteInput (New-Brief) @() 3
    foreach ($k in $over.Keys) { $in.$k = $over[$k] }
    $in
}
function New-Fix([hashtable]$over = @{}) {
    $f = [pscustomobject]@{ repro = "pass"; lines = 40; files = 2; behavior = @(); owner_only = @(); overlap = @() }
    foreach ($k in $over.Keys) { $f.$k = $over[$k] }
    $f
}

Describe 'rules/routing.json' {
    It 'holds the owner-approved thresholds' {
        $rules.autoFix.maxChangedLines | Should Be 150
        $rules.autoFix.maxChangedFiles | Should Be 5
        $rules.autoFix.dailyCap | Should Be 3
        $rules.autoFix.confidence | Should Be "high"
        $rules.ourWords.runLength | Should Be 8
    }
}

Describe 'rules/areas.json' {
    It 'puts every area label in exactly one area, and in the precedence list' {
        $all = @($areas.areas | ForEach-Object { $_.labels })
        @($all | Select-Object -Unique).Count | Should Be $all.Count
        @($areas.precedence | Sort-Object) -join ',' | Should Be (@($all | Sort-Object) -join ',')
        foreach ($l in $all) { $l | Should Match '^area:[a-z-]+$' }
    }
    It 'names an area for engineering-only work, and the board ids the plan route needs' {
        @($areas.areas | Where-Object { $_.engineering }).Count | Should Be 1
        $areas.none | Should Be "No area yet"
        $areas.board.project | Should Match '^PVT_'
        $areas.board.whenField | Should Match '^PVTSSF_'
        $areas.board.later | Should Not BeNullOrEmpty
    }
}

Describe 'Areas for planned issues' {
    It 'keeps the brief''s area only when rules/areas.json lists it' {
        $b = New-Brief; $b | Add-Member -NotePropertyName area -NotePropertyValue "area:ai"
        (New-RouteInput $b @() 3 $areas.precedence).area | Should Be "area:ai"
        $b.area = "area:made-up"
        (New-RouteInput $b @() 3 $areas.precedence).area | Should Be ""
        $b.area = "AREA:AI"
        (New-RouteInput $b @() 3 $areas.precedence).area | Should Be ""
        (New-RouteInput (New-Brief) @() 3 $areas.precedence).area | Should Be ""
    }
    It 'a plan with no area anywhere has none, so it shows under No area yet' {
        (Get-Route (New-Input @{ kind = "feature" }) $rules).area | Should Be $null
    }
    It 'a bug route never carries an area' {
        (Get-Route (New-Input @{ area = "area:ai" }) $rules).area | Should Be $null
    }
}

Describe 'Get-Route replay cases (tests/factory/routing-cases.json)' {
    foreach ($c in $cases) {
        It $c.name {
            $r = Get-Route $c.input $rules
            $r.route | Should Be $c.expect.route
            foreach ($p in $c.expect.PSObject.Properties) {
                switch ($p.Name) {
                    "route" { }
                    "target" { $r.target.type | Should Be $p.Value.type; $r.target.number | Should Be $p.Value.number }
                    "evidence" { (@($r.evidence | ForEach-Object { $_.issue }) -join ',') | Should Be $p.Value }
                    default { $r.($p.Name) | Should Be $p.Value }
                }
            }
        }
    }
    It 'covers every route' {
        foreach ($route in $Routes) { @($cases | Where-Object { $_.expect.route -eq $route }).Count | Should BeGreaterThan 0 }
    }
    It 'holds no report text: every input field is a known routing field' {
        $known = "kind", "security", "reproduced", "confidence", "repro_test", "docs_gap", "area", "matches", "asks", "our_words", "fix", "cap_left"
        foreach ($c in $cases) { foreach ($p in $c.input.PSObject.Properties) { $known -contains $p.Name | Should Be $true } }
    }
}

Describe 'Get-Route matrix order' {
    It 'takes rows in order: hold, evidence, regression, bug, plan, docs-gap, pile' {
        (Get-Route (New-Input @{ security = $true; kind = "feature" }) $rules).row | Should Be 1
        (Get-Route (New-Input @{ kind = "feature"; matches = @([pscustomobject]@{ relation = "same"; target = "issue"; number = 5; state = "open"; curated = $true; area = $null }) }) $rules).row | Should Be 2
        (Get-Route (New-Input) $rules).row | Should Be 4
        (Get-Route (New-Input @{ confidence = "low" }) $rules).row | Should Be 5
        (Get-Route (New-Input @{ reproduced = "no" }) $rules).row | Should Be 6
        (Get-Route (New-Input @{ kind = "feature" }) $rules).row | Should Be 7
        (Get-Route (New-Input @{ kind = "question"; docs_gap = $true }) $rules).row | Should Be 8
        (Get-Route (New-Input @{ kind = "noise" }) $rules).row | Should Be 9
    }
    It 'says why a fix is for review' {
        $r = Get-Route (New-Input @{ fix = (New-Fix @{ lines = 200; behavior = @("styleguide/x") }) }) $rules
        ($r.reasons -join ' ') | Should Match '200 lines'
        ($r.reasons -join ' ') | Should Match 'styleguide/x'
    }
    It 'reads the thresholds from the rules' {
        $wider = $rules | ConvertTo-Json -Depth 5 | ConvertFrom-Json; $wider.autoFix.maxChangedLines = 250
        (Get-Route (New-Input @{ fix = (New-Fix @{ lines = 200 }) }) $wider).route | Should Be "auto-fix"
        (Get-Route (New-Input @{ fix = (New-Fix @{ lines = 200 }) }) $rules).route | Should Be "fix-for-review"
    }
}

Describe 'Resolve-Matches' {
    $issues = @(
        [pscustomobject]@{ number = 44; state = "OPEN"; labels = @([pscustomobject]@{ name = "from:owner" }, [pscustomobject]@{ name = "area:core" }, [pscustomobject]@{ name = "area:ai" }) },
        [pscustomobject]@{ number = 120; state = "OPEN"; labels = @([pscustomobject]@{ name = "from:tester" }) },
        [pscustomobject]@{ number = 130; state = "OPEN"; labels = @([pscustomobject]@{ name = "bug" }) },
        [pscustomobject]@{ number = 140; state = "CLOSED"; labels = @([pscustomobject]@{ name = "from:owner" }) })
    $reps = @([pscustomobject]@{ number = 8 }, [pscustomobject]@{ number = 9 })
    function Mt($ref, $rel = "same") { [pscustomobject]@{ ref = $ref; relation = $rel; title = "t"; why = "w" } }
    It 'marks issues we curate' {
        $m = @(Resolve-Matches @((Mt "#44" "part-of"), (Mt "#120"), (Mt "#130")) $issues $reps 8)
        $m[0].curated | Should Be $true; $m[0].area | Should Be "area:core"
        $m[1].curated | Should Be $true; $m[1].area | Should Be $null
        $m[2].curated | Should Be $false
    }
    It 'a theme label alone no longer makes an issue curated' {
        Test-CuratedIssue ([pscustomobject]@{ number = 7; state = "OPEN"; labels = @([pscustomobject]@{ name = "theme" }) }) | Should Be $false
    }
    It 'takes the area label first in precedence (rules/areas.json)' {
        $m = @(Resolve-Matches @((Mt "#44" "related")) $issues $reps 8 $areas.precedence)
        $m[0].area | Should Be "area:ai"
    }
    It 'a closed issue is not curated, and keeps its state for regressions' {
        $m = @(Resolve-Matches @((Mt "#140")) $issues $reps 8)
        $m[0].state | Should Be "closed"; $m[0].curated | Should Be $false
    }
    It 'an issue or report the lists do not hold is missing' {
        $m = @(Resolve-Matches @((Mt "#999"), (Mt "report #77")) $issues $reps 8)
        $m[0].state | Should Be "missing"; $m[1].state | Should Be "missing"
    }
    It 'a report never duplicates itself' { (@(Resolve-Matches @((Mt "report #8")) $issues $reps 8))[0].state | Should Be "missing" }
    It 'resolves another open report' { (@(Resolve-Matches @((Mt "report #9")) $issues $reps 8))[0].state | Should Be "open" }
    It 'drops refs and relations it cannot read' {
        @(Resolve-Matches @((Mt "#44; rm -rf"), (Mt "https://evil.example/1"), (Mt "#44" "owns")) $issues $reps 8).Count | Should Be 0
    }
    It 'a forged curated claim in the brief counts for nothing' {
        $forged = [pscustomobject]@{ ref = "#130"; relation = "same"; curated = $true; state = "open" }
        $in = New-RouteInput (New-Brief) @(Resolve-Matches @($forged) $issues $reps 8) 3
        (Get-Route $in $rules).route | Should Not Be "evidence"
    }
}

Describe 'Test-FixAllowed (the fix gate)' {
    function Ev($label) { [pscustomobject]@{ event = "labeled"; label = [pscustomobject]@{ name = $label }; actor = [pscustomobject]@{ login = "abmorrill4" } } }
    It 'allows a fix route computed in this run' {
        Test-FixAllowed (Get-Route (New-Input) $rules) @("problem") @() | Should BeNullOrEmpty
        Test-FixAllowed (Get-Route (New-Input @{ confidence = "medium" }) $rules) @("problem") @() | Should BeNullOrEmpty
    }
    It 'allows a regression computed in this run' {
        $m = [pscustomobject]@{ relation = "same"; target = "issue"; number = 130; state = "closed"; curated = $false; theme = $false }
        Test-FixAllowed (Get-Route (New-Input @{ matches = @($m) }) $rules) @() @() | Should BeNullOrEmpty
    }
    It 'a stray route label grants nothing' {
        Test-FixAllowed $null @("problem", "route:auto-fix") @((Ev "route:auto-fix")) | Should Not BeNullOrEmpty
        Test-FixAllowed $null @("problem", "route:fix-for-review", "triage:ready") @() | Should Not BeNullOrEmpty
    }
    It 'refuses routes that do not build' {
        foreach ($i in (New-Input @{ reproduced = "no" }), (New-Input @{ kind = "feature" }), (New-Input @{ security = $true }), (New-Input @{ cap_left = 0 })) {
            Test-FixAllowed (Get-Route $i $rules) @() @() | Should Not BeNullOrEmpty
        }
    }
    It "allows the owner's decision:fix" { Test-FixAllowed $null @("decision:fix") @((Ev "decision:fix")) | Should BeNullOrEmpty }
    It "refuses decision:fix with a later park" { Test-FixAllowed $null @("decision:fix") @((Ev "decision:fix"), (Ev "decision:park")) | Should Not BeNullOrEmpty }
}

function New-Rep([int]$n, [string[]]$labels) {
    [pscustomobject]@{ number = $n; author = [pscustomobject]@{ login = "abmorrill4" }; labels = @($labels | ForEach-Object { [pscustomobject]@{ name = $_ } }) }
}
Describe 'Select-Report with routes' {
    It 'a route label on an untriaged report still means triage, never fix' {
        (Select-Report @((New-Rep 8 "problem", "route:auto-fix")) "abmorrill4" 0 3).Mode | Should Be "triage"
    }
    It 'a routed report with no decision is not picked again' {
        Select-Report @((New-Rep 8 "problem", "triage:ready", "route:auto-fix", "waiting:decide")) "abmorrill4" 0 3 | Should BeNullOrEmpty
    }
    It 'a queued report waits while the cap is full' { Select-Report @((New-Rep 8 "problem", "triage:ready", "factory:queued")) "abmorrill4" 0 0 | Should BeNullOrEmpty }
    It 'a queued report is routed again once the cap allows' { (Select-Report @((New-Rep 8 "problem", "triage:ready", "factory:queued")) "abmorrill4" 0 1).Mode | Should Be "triage" }
    It 'new reports go before queued ones' { (Select-Report @((New-Rep 8 "problem", "factory:queued"), (New-Rep 9 "problem")) "abmorrill4" 0 1).Report.number | Should Be 9 }
    It 'a queued report the owner decided on is left to the decision' { Select-Report @((New-Rep 8 "problem", "factory:queued", "decision:park")) "abmorrill4" 0 1 | Should BeNullOrEmpty }
}

Describe 'Assert-FactoryLabel with routes' {
    It 'allows route and waiting labels' { { Assert-FactoryLabel "route:auto-fix"; Assert-FactoryLabel "waiting:security" } | Should Not Throw }
    It 'still refuses decision labels' { { Assert-FactoryLabel "decision:fix" } | Should Throw }
}

Describe 'Get-WaitingReason' {
    It 'live mode uses the route' {
        Get-WaitingReason (Get-Route (New-Input @{ reproduced = "no" }) $rules) "live" | Should Be "decide"
        Get-WaitingReason (Get-Route (New-Input @{ fix = (New-Fix @{ lines = 999 }) }) $rules) "live" | Should Be "merge"
        Get-WaitingReason (Get-Route (New-Input @{ kind = "feature" }) $rules) "live" | Should BeNullOrEmpty
    }
    It 'shadow mode leaves every decision to the owner, except the pile' {
        Get-WaitingReason (Get-Route (New-Input @{ kind = "feature" }) $rules) "shadow" | Should Be "decide"
        Get-WaitingReason (Get-Route (New-Input @{ kind = "noise" }) $rules) "shadow" | Should BeNullOrEmpty
    }
    It 'a hold is security in both modes' {
        Get-WaitingReason (Get-Route (New-Input @{ security = $true }) $rules) "shadow" | Should Be "security"
        Get-WaitingReason (Get-Route (New-Input @{ security = $true }) $rules) "live" | Should Be "security"
    }
}

Describe 'Test-OurWords' {
    $report = "When I click Lint Topic on a long topic with tables the pane freezes and Flare stops responding for a minute."
    It 'passes text in our own words' {
        @(Test-OurWords @{ "the PR" = "Lint Topic no longer stalls on topics with large tables." } $report 8).Count | Should Be 0
    }
    It 'fails an 8-word run shared with the report, ignoring case and punctuation' {
        $p = @(Test-OurWords @{ "the commit messages" = "Fix: the PANE freezes, and Flare stops responding for a while" } $report 8)
        $p.Count | Should Be 1
        $p[0] | Should Match 'commit messages shares a run of 8 words'
    }
    It 'allows a 7-word run' {
        @(Test-OurWords @{ "the PR" = "so the pane freezes and Flare stops. Fixed now." } $report 8).Count | Should Be 0
    }
    It 'never repeats the matching words' {
        $p = Test-OurWords @{ "the PR" = "the pane freezes and Flare stops responding for a minute" } $report 8
        ($p -join ' ') | Should Not Match 'freezes'
    }
    It 'fails an email address, also one with a defused mention' {
        @(Test-OurWords @{ "the PR" = "Thanks to jane.doe@example.com" } "" 8).Count | Should Be 1
        @(Test-OurWords @{ "the PR" = "Thanks to jane.doe@$([char]0x200B)example.com" } "" 8).Count | Should Be 1
    }
    It 'fails UNC and user-profile paths' {
        (Test-OurWords @{ "a" = 'Open \\fileserver\docs\guide' } "" 8) | Should Match 'UNC'
        (Test-OurWords @{ "a" = 'C:\Users\jane\Documents\proj.flprj' } "" 8) | Should Match 'user-profile'
        (Test-OurWords @{ "a" = '/Users/jane/Documents' } "" 8) | Should Match 'user-profile'
        (Test-OurWords @{ "a" = '%USERPROFILE%\x' } "" 8) | Should Match 'user-profile'
    }
    It 'passes ordinary code' {
        @(Test-OurWords @{ "a" = 'var path = Path.Combine(root, "Content", "a.htm"); Assert.Equal(2, issues.Count);' } $report 8).Count | Should Be 0
    }
}

Describe 'Get-DiffFacts' {
    $owner = '^src/FlareAI\.Core/(Update|Settings|Llm)/|^src/FlareAI\.Flare/E2EHook\.cs$'
    $test = "FlareAI.Core.Tests.IgnoreTests.IgnoreSurvivesRelint"
    function New-Diff([hashtable]$files) {
        ($files.Keys | ForEach-Object { "diff --git a/$_ b/$_`n--- a/$_`n+++ b/$_`n@@ -1 +1 @@`n" + ($files[$_] -join "`n") }) -join "`n"
    }
    It 'counts size without the reproducing test' {
        $d = New-Diff @{ "src/FlareAI.Core/Style/Ignore.cs" = @("-old", "+new"); "tests/FlareAI.Core.Tests/IgnoreTests.cs" = @("+public void IgnoreSurvivesRelint() {") }
        $f = Get-DiffFacts @("1`t1`tsrc/FlareAI.Core/Style/Ignore.cs", "300`t0`ttests/FlareAI.Core.Tests/IgnoreTests.cs") $d $test $rules $owner
        $f.lines | Should Be 2; $f.files | Should Be 1; $f.repro_in_diff | Should Be $true
    }
    It 'notices when the named test is not in the diff' {
        $d = New-Diff @{ "tests/FlareAI.Core.Tests/OtherTests.cs" = @("+public void Something() {") }
        $f = Get-DiffFacts @("3`t0`ttests/FlareAI.Core.Tests/OtherTests.cs") $d $test $rules $owner
        $f.repro_in_diff | Should Be $false; $f.files | Should Be 1
    }
    It 'reads behavior paths from the diff' {
        $paths = "styleguide/Company/Rules.flsnp", "docs/user-guide/lint.md", "src/FlareAI.Flare/Commands.cs", "src/FlareAI.Flare/UI/Strings.resx", "src/FlareAI.Core/Settings/X.cs", "src/FlareAI.Core/Style/LintSettings.cs"
        $f = Get-DiffFacts @($paths | ForEach-Object { "1`t0`t$_" }) "" "" $rules $owner
        @($f.behavior).Count | Should Be 6
    }
    It 'finds new or changed UI strings, not other UI edits' {
        $d = New-Diff @{ "src/FlareAI.Flare/UI/AskForm.cs" = @('-  button.Text = "Ask";', '+  button.Text = "Ask House Style";') }
        (Get-DiffFacts @("1`t1`tsrc/FlareAI.Flare/UI/AskForm.cs") $d "" $rules $owner).behavior | Should Match 'UI text'
        $d = New-Diff @{ "src/FlareAI.Flare/UI/AskForm.cs" = @('-  if (x == null) return;', '+  if (x == null || y) return;', '+  // a "comment" only') }
        @((Get-DiffFacts @("2`t1`tsrc/FlareAI.Flare/UI/AskForm.cs") $d "" $rules $owner).behavior).Count | Should Be 0
    }
    It "leaves the fix's test files out of the behavior scan" {
        $d = New-Diff @{ "tests/FlareAI.Core.Tests/PluginSettingsTests.cs" = @('+public void IgnoreSurvivesRelint() { Assert.Equal("Ignore", x); }') }
        $f = Get-DiffFacts @("1`t0`ttests/FlareAI.Core.Tests/PluginSettingsTests.cs") $d $test $rules $owner
        @($f.behavior).Count | Should Be 0
        $f = Get-DiffFacts @("1`t0`ttests/FlareAI.Core.Tests/PluginSettingsTests.cs") $d "" $rules $owner
        @($f.behavior).Count | Should Be 1
    }
    It 'lists owner-only paths' {
        (Get-DiffFacts @("1`t0`tsrc/FlareAI.Core/Update/UpdateStager.cs") "" "" $rules $owner).owner_only | Should Be "src/FlareAI.Core/Update/UpdateStager.cs"
    }
}

Describe 'Split-Diff and the our-words check over added lines' {
    $report = "When I click Lint Topic on a long topic with tables the pane freezes and Flare stops responding for a minute."
    $diff = @(
        "diff --git a/src/A.cs b/src/A.cs", "index 1111111..2222222 100644", "--- a/src/A.cs", "+++ b/src/A.cs", "@@ -1 +1,2 @@",
        "--- an old line whose text starts with two dashes", "+++ the pane freezes and Flare stops responding for a minute", "+ordinary added line",
        "diff --git a/src/New.cs b/src/New.cs", "new file mode 100644", "--- /dev/null", "+++ b/src/New.cs", "@@ -0,0 +1 @@", "+class New {}"
    ) -join "`n"
    It 'counts hunk lines that start with ++ or -- and skips only real headers' {
        $f = Split-Diff $diff
        $f["src/A.cs"].added.Count | Should Be 2
        $f["src/A.cs"].removed | Should Be "-- an old line whose text starts with two dashes"
        $f["src/New.cs"].added | Should Be "class New {}"
        @($f["src/New.cs"].removed).Count | Should Be 0
    }
    It 'an added line starting with ++ that carries report text fails our words' {
        $added = Get-AddedText $diff
        $added | Should Match 'the pane freezes'
        (Test-OurWords @{ "the added lines" = $added } $report 8) | Should Match 'shares a run of 8 words'
    }
}

Describe 'Owner-only code holds' {
    $owned = "src/FlareAI.Core/Update/UpdateStager.cs", "src/FlareAI.Core/Settings/PluginSettings.cs", "src/FlareAI.Core/Llm/Client.cs",
             "src/FlareAI.Core/Editing/XhtmlValidator.cs", "src/FlareAI.Core/Feedback/Report.cs", "src/FlareAI.Core/Actions/GuideRequests.cs",
             "src/FlareAI.Core/Actions/Team.cs", "src/FlareAI.Flare/E2EHook.cs", ".claude/agents/x.md", ".claude/skills/triage/SKILL.md",
             ".claude/workflows/w.js", "scripts/factory/Triage.psm1", "scripts/agent/run.ps1", "rules/routing.json",
             "tests/factory/routing-cases.json", ".github/workflows/factory.yml"
    foreach ($p in $owned) {
        It "holds a fix touching $p" {
            $f = Get-DiffFacts @("1`t0`t$p") "" "" $rules
            @($f.owner_only).Count | Should Be 1
            (Get-Route (New-Input @{ fix = (New-Fix @{ owner_only = $f.owner_only }) }) $rules).route | Should Be "hold"
        }
    }
    It 'leaves ordinary code alone' {
        @((Get-DiffFacts @("1`t0`tsrc/FlareAI.Core/Style/StyleEngine.cs", "1`t0`tsrc/FlareAI.Core/Actions/ActionRunner.cs") "" "" $rules).owner_only).Count | Should Be 0
    }
    It 'holds a fix that defines the Git class or Git.Untrusted anywhere' {
        $d = "diff --git a/src/FlareAI.Core/Style/Helpers.cs b/src/FlareAI.Core/Style/Helpers.cs`n@@ -1 +1 @@`n+    public static class Git { public static readonly string Untrusted = `"`"; }"
        $f = Get-DiffFacts @("1`t0`tsrc/FlareAI.Core/Style/Helpers.cs") $d "" $rules
        $f.owner_only | Should Match 'Git\.Untrusted'
        (Get-Route (New-Input @{ fix = (New-Fix @{ owner_only = $f.owner_only }) }) $rules).route | Should Be "hold"
    }
}

Describe 'Get-TriageLabel follows the route' {
    It 'the pile is triage:noise, even for a question' {
        Get-TriageLabel (Get-Route (New-Input @{ kind = "question" }) $rules) | Should Be "triage:noise"
        Get-TriageLabel (Get-Route (New-Input @{ kind = "noise" }) $rules) | Should Be "triage:noise"
    }
    It 'everything else is triage:ready, even noise held for security' {
        Get-TriageLabel (Get-Route (New-Input @{ kind = "noise"; security = $true }) $rules) | Should Be "triage:ready"
        Get-TriageLabel (Get-Route (New-Input @{ kind = "feature" }) $rules) | Should Be "triage:ready"
    }
}

function Trx([string]$name, [string]$outcome, [string]$message = "", [string]$stack = "") {
    @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
<UnitTestResult testName="$name" outcome="$outcome"><Output><ErrorInfo><Message>$message</Message><StackTrace>$stack</StackTrace></ErrorInfo></Output></UnitTestResult>
</Results></TestRun>
"@
}
Describe 'Test-ReproResult' {
    $name = "FlareAI.Core.Tests.IgnoreTests.IgnoreSurvivesRelint"
    It 'accepts a test that fails on base with an assertion' {
        Test-ReproResult @((Trx $name "Failed" "Assert.Equal() Failure: Values differ" "at FlareAI.Core.Tests.IgnoreTests.IgnoreSurvivesRelint()")) $name "fail" | Should BeNullOrEmpty
    }
    It 'refuses a test that passes on base (a forged reproduction)' {
        Test-ReproResult @((Trx $name "Passed")) $name "fail" | Should Match 'passes on the base commit'
    }
    It 'refuses a test that did not run on base, for example because it does not build there' {
        Test-ReproResult @() $name "fail" | Should Match "didn't run"
        Test-ReproResult @((Trx "Other.Test" "Failed" "Assert.True() Failure")) $name "fail" | Should Match "didn't run"
    }
    It 'refuses a test that fails on base without an assertion' {
        Test-ReproResult @((Trx $name "Failed" "System.NullReferenceException: Object reference not set" "at FlareAI.Core.Style.Ignore.Apply()")) $name "fail" | Should Match 'without an assertion'
    }
    It 'matches the exact name, or a theory with arguments, never a longer name' {
        Test-ReproResult @((Trx "$($name)Twice" "Failed" "Assert.Equal() Failure")) $name "fail" | Should Match "didn't run"
        Test-ReproResult @((Trx "$name(count: 2)" "Failed" "Assert.Equal() Failure")) $name "fail" | Should BeNullOrEmpty
        Test-ReproResult @((Trx $name "Passed"), (Trx "$($name)Twice" "Failed")) $name "pass" | Should BeNullOrEmpty
    }
    It 'accepts a pass on the fix and refuses a failure' {
        Test-ReproResult @((Trx $name "Passed")) $name "pass" | Should BeNullOrEmpty
        Test-ReproResult @((Trx $name "Failed" "Assert.Equal() Failure")) $name "pass" | Should Match "doesn't pass"
    }
}

Describe 'The forged report never auto-fixes' {
    $brief = New-Brief   # claims reproduced: yes, confidence: high, and names a test
    $owner = '^src/FlareAI\.Core/(Update|Settings|Llm)/'
    It 'when the named test passes on the base commit' {
        $in = New-RouteInput $brief @() 3
        (Get-Route $in $rules).build | Should Be $true
        $repro = Test-ReproResult @((Trx $brief.repro_test "Passed")) $brief.repro_test "fail"
        $in.fix = New-Fix @{ repro = $repro }
        $r = Get-Route $in $rules
        $r.route | Should Be "fix-for-review"; $r.build | Should Be $false
    }
    It 'when the diff touches styleguide/' {
        $in = New-RouteInput $brief @() 3
        $f = Get-DiffFacts @("1`t1`tstyleguide/Company/Rules.flsnp", "5`t0`ttests/FlareAI.Core.Tests/IgnoreTests.cs") "diff --git a/tests/FlareAI.Core.Tests/IgnoreTests.cs b/tests/FlareAI.Core.Tests/IgnoreTests.cs`n@@ -0,0 +1 @@`n+void IgnoreSurvivesRelint()" $brief.repro_test $rules $owner
        $in.fix = New-Fix @{ behavior = $f.behavior; lines = $f.lines; files = $f.files }
        (Get-Route $in $rules).route | Should Be "fix-for-review"
    }
}

Describe 'Test-TriageBrief routing fields' {
    It 'needs security as true or false' {
        $b = New-Brief; $b.security = "yes"; (Test-TriageBrief $b) -join ' ' | Should Match 'security'
    }
    It 'refuses a test name that could reach a shell or filter' {
        foreach ($bad in 'X"; rm -rf /', 'Name|Other', 'A.B&C', '..\x', ('a' * 201)) {
            $b = New-Brief; $b.repro_test = $bad; (Test-TriageBrief $b) -join ' ' | Should Match 'repro_test'
        }
    }
    It 'allows an empty test name' { $b = New-Brief; $b.repro_test = ""; @(Test-TriageBrief $b).Count | Should Be 0 }
    It 'a bad test name never reaches the route' {
        $b = New-Brief; $b.repro_test = 'X"; rm'
        (New-RouteInput $b @() 3).repro_test | Should Be ""
    }
}

Describe 'Public text and the route comment' {
    It 'builds plan text from the brief, in our words' {
        $route = Get-Route (New-Input @{ kind = "feature" }) $rules
        $t = Get-PublicText $route (New-Brief) 12
        $t["issue title"] | Should Be "Hide a false positive"
        $t["issue body"] | Should Match 'tester report 12'
    }
    It 'evidence text for an issue, none for a duplicate report' {
        $m = [pscustomobject]@{ relation = "same"; target = "issue"; number = 5; state = "open"; curated = $true; theme = $false }
        (Get-PublicText (Get-Route (New-Input @{ kind = "friction"; matches = @($m) }) $rules) (New-Brief) 12)["evidence on #5"] | Should Match '^\*\*Evidence from testers\*\*'
        $d = [pscustomobject]@{ relation = "same"; target = "report"; number = 9; state = "open"; curated = $false; theme = $false }
        (Get-PublicText (Get-Route (New-Input @{ kind = "duplicate"; matches = @($d) }) $rules) (New-Brief) 12).Count | Should Be 0
    }
    It 'names each ask on its own issue, says what a partial issue lacks, and plans only the uncovered asks' {
        $b = New-Brief; $b.kind = "feature"
        $b | Add-Member asks @(
            [pscustomobject]@{ ask = "Lint one folder"; kind = "feature"; ref = ""; covers = "none"; missing = ""; fix_now = $false },
            [pscustomobject]@{ ask = "Drop fixed issues from the list"; kind = "friction"; ref = "#162"; covers = "partial"; missing = "Fixed issues leave the list."; fix_now = $false })
        $i162 = [pscustomobject]@{ relation = "same"; target = "issue"; number = 162; state = "open"; curated = $true; area = $null }
        $in = New-RouteInput $b @() 3 @() @($i162)
        $r = Get-Route $in $rules
        $r.route | Should Be "plan"
        $t = Get-PublicText $r $b 12
        $t["evidence on #162"] | Should Match 'Drop fixed issues from the list'
        $t["evidence on #162"] | Should Match 'Not covered here yet:\*\* Fixed issues leave the list'
        $t["issue body"] | Should Match 'Lint one folder'
        $t["issue body"] | Should Not Match 'Drop fixed issues'
    }
    It 'carries a routing record that parses back to the input' {
        $in = New-Input
        $c = Format-RouteComment (Get-Route $in $rules) $in "shadow" "Next."
        $c | Should Match 'Shadow mode'
        $json = [regex]::Match($c, '(?s)```json\r?\n(.+?)\r?\n```').Groups[1].Value
        $rec = $json | ConvertFrom-Json
        $rec.route | Should Be "auto-fix"; $rec.input.repro_test | Should Be $in.repro_test; $rec.mode | Should Be "shadow"
    }
}
