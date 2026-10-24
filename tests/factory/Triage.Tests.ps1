# Pester 3 tests (built into Windows PowerShell 5.1): powershell.exe -Command "Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here '..\..\scripts\factory\Triage.psm1') -Force

function New-Brief {
    [pscustomobject]@{
        kind = "bug"; summary = "Ignoring an issue doesn't stick after a relint."; goal = "Hide a false positive."
        impact = "Anyone who ignores an issue, every relint. Slows them, doesn't block."
        reproduced = [pscustomobject]@{ result = "yes"; how = "IgnoreSurvivesRelint fails" }
        matches = @([pscustomobject]@{ ref = "#127"; title = "Twin rules"; relation = "related"; why = "Same rule pair." })
        options = @([pscustomobject]@{ option = "Fix now"; effort = "S"; risk = "Low"; for_writers = "Ignore sticks." },
                    [pscustomobject]@{ option = "Park"; effort = "S"; risk = "None"; for_writers = "Nothing changes." })
        recommendation = [pscustomobject]@{ option = "Fix now"; why = "Small and clear." }
        questions = @("Should Ignore apply to the whole project?"); confidence = "high"; security = $false
        asks = @([pscustomobject]@{ ask = "Keep an ignored issue ignored after a relint"; kind = "bug"; ref = ""; covers = "none"; missing = ""; fix_now = $false })
    }
}

Describe 'Test-TriageBrief' {
    It 'accepts a complete brief' { @(Test-TriageBrief (New-Brief)).Count | Should Be 0 }
    It 'names what is missing' {
        $b = New-Brief; $b.kind = "rant"; $b.summary = ""; $b.options = @(); $b.recommendation = $null
        $p = Test-TriageBrief $b
        ($p -join ' ') | Should Match "kind 'rant'"
        ($p -join ' ') | Should Match 'summary is missing'
        ($p -join ' ') | Should Match 'fewer than two options'
        ($p -join ' ') | Should Match 'no recommendation'
    }
    It 'rejects an empty brief' { Test-TriageBrief $null | Should Be "The brief is empty." }
}

Describe 'Format-TriageComment' {
    It 'shows every part of the brief and how to decide' {
        $c = Format-TriageComment (New-Brief)
        $c | Should Match '\*\*Triage:\*\* bug'
        $c | Should Match 'Lines up with'
        $c | Should Match '#127'
        $c | Should Match '\*\*Recommendation:\*\* Fix now'
        $c | Should Match 'decision:fix'
    }
    It 'caps long text and keeps each field on one line' {
        $b = New-Brief; $b.summary = ("x" * 10000) + "`n`nsecond paragraph"
        $c = Format-TriageComment $b
        $line = ($c -split "`n" | Where-Object { $_ -like '**What happened:**' + '*' })
        $line.Length | Should BeLessThan 700
        $c | Should Not Match 'second paragraph'
    }
    It 'shows impact' { Format-TriageComment (New-Brief) | Should Match 'Impact on writers:\*\* Anyone' }
    It 'drops links, images and HTML' {
        $b = New-Brief; $b.summary = 'See [this](https://evil.example/x) ![img](https://evil.example/p.png) <img src=x> https://evil.example/raw'
        $c = Format-TriageComment $b
        $c | Should Not Match 'evil\.example'
        $c | Should Not Match '<img'
        $c | Should Match 'See this'
    }
    It 'defuses @mentions' {
        $b = New-Brief; $b.summary = "Ping @abmorrill4 and @github/team."
        Format-TriageComment $b | Should Not Match '@abmorrill4'
    }
    It 'keeps at most four options and six matches' {
        $b = New-Brief
        $b.options = 1..9 | ForEach-Object { [pscustomobject]@{ option = "Option $_"; effort = "S"; risk = "-"; for_writers = "-" } }
        $b.matches = 1..9 | ForEach-Object { [pscustomobject]@{ ref = "#$_"; title = "T"; relation = "related"; why = "-" } }
        $c = Format-TriageComment $b
        $c | Should Match 'Option 4'
        $c | Should Not Match 'Option 5'
        $c | Should Not Match '#7 '
    }
}

function New-Rep([int]$n, [string[]]$labels, [string]$author = "abmorrill4") {
    [pscustomobject]@{ number = $n; author = [pscustomobject]@{ login = $author }; labels = @($labels | ForEach-Object { [pscustomobject]@{ name = $_ } }) }
}

Describe 'Select-Report' {
    $owner = "abmorrill4"
    It 'triages an untriaged bug report, never fixes it' {
        $s = Select-Report @((New-Rep 8 "problem")) $owner
        $s.Report.number | Should Be 8; $s.Mode | Should Be "triage"
    }
    It 'triages automatic error reports too' { (Select-Report @((New-Rep 3 "error", "automatic")) $owner).Mode | Should Be "triage" }
    It 'triages before fixing, oldest first' {
        $s = Select-Report @((New-Rep 12 "problem", "decision:fix"), (New-Rep 15 "problem"), (New-Rep 14 "problem")) $owner
        $s.Report.number | Should Be 14; $s.Mode | Should Be "triage"
    }
    It 'retries failed triage' { (Select-Report @((New-Rep 9 "problem", "triage:failed")) $owner).Mode | Should Be "triage" }
    It 'leaves triaged and decided reports alone' {
        Select-Report @((New-Rep 1 "problem", "triage:ready"), (New-Rep 2 "problem", "triage:noise"), (New-Rep 4 "problem", "triage:ready", "decision:plan"), (New-Rep 5 "problem", "factory:stuck")) $owner | Should BeNullOrEmpty
    }
    It 'fixes only an approved report' {
        $s = Select-Report @((New-Rep 1 "problem", "triage:ready"), (New-Rep 6 "problem", "triage:ready", "decision:fix")) $owner
        $s.Report.number | Should Be 6; $s.Mode | Should Be "fix"
    }
    It 'skips a fix that also has a second decision label, and fixes the next one' {
        $s = Select-Report @((New-Rep 6 "problem", "decision:fix", "decision:park"), (New-Rep 9 "problem", "decision:fix")) $owner
        $s.Report.number | Should Be 9
    }
    It 'never fixes one already in the factory' { Select-Report @((New-Rep 6 "problem", "decision:fix", "factory:done")) $owner | Should BeNullOrEmpty }
    It 'ignores reports the feedback service did not file, and other issues' {
        Select-Report @((New-Rep 7 "problem" -author "someone"), (New-Rep 10 "question")) $owner | Should BeNullOrEmpty
    }
    It 'with an issue number, works on that report only' {
        (Select-Report @((New-Rep 8 "problem"), (New-Rep 9 "problem", "decision:fix")) $owner 9).Mode | Should Be "fix"
        Select-Report @((New-Rep 8 "problem")) $owner 99 | Should BeNullOrEmpty
    }
}

function New-Ev([string]$event, [string]$label) { [pscustomobject]@{ event = $event; label = [pscustomobject]@{ name = $label }; actor = [pscustomobject]@{ login = "abmorrill4" } } }

Describe 'Test-FixGate' {
    It 'passes decision:fix alone, added last' { Test-FixGate @("problem", "decision:fix") @((New-Ev labeled "decision:fix")) | Should BeNullOrEmpty }
    It 'refuses fix together with park' { Test-FixGate @("decision:fix", "decision:park") @((New-Ev labeled "decision:fix"), (New-Ev labeled "decision:park")) | Should Match 'not decision:fix alone' }
    It 'refuses when a later decision was added and removed' {
        Test-FixGate @("decision:fix") @((New-Ev labeled "decision:fix"), (New-Ev labeled "decision:park"), (New-Ev unlabeled "decision:park")) | Should Match 'last decision label added was decision:park'
    }
    It 'refuses with no decision' { Test-FixGate @("triage:ready") @() | Should Not BeNullOrEmpty }
}

Describe 'Get-TriageFailures' {
    function C($b) { [pscustomobject]@{ body = $b } }
    It 'counts failed attempts' { Get-TriageFailures @((C "**Factory:** Triage failed (attempt 1 of 3): x"), (C "other"), (C "**Factory:** Triage failed (attempt 2 of 3): y")) | Should Be 2 }
    It 'starts again after the owner asks for a retry' {
        Get-TriageFailures @((C "**Factory:** Triage failed a"), (C "**Factory:** Triage failed b"), (C "**Factory:** Triage failed c"), (C "**Retry triage**"), (C "**Factory:** Triage failed d")) | Should Be 1
    }
    It 'is zero with no comments' { Get-TriageFailures @() | Should Be 0 }
}

Describe 'Assert-FactoryLabel' {
    It 'allows triage and factory labels' { { Assert-FactoryLabel "triage:ready"; Assert-FactoryLabel "factory:done" } | Should Not Throw }
    It 'refuses decision labels' { { Assert-FactoryLabel "decision:fix" } | Should Throw }
    It 'refuses anything else' { { Assert-FactoryLabel "bug" } | Should Throw }
}

Describe 'Get-LabelActor' {
    $events = @(
        [pscustomobject]@{ event = "labeled"; label = [pscustomobject]@{ name = "triage:ready" }; actor = [pscustomobject]@{ login = "abmorrill4" } },
        [pscustomobject]@{ event = "labeled"; label = [pscustomobject]@{ name = "decision:fix" }; actor = [pscustomobject]@{ login = "abmorrill4" } },
        [pscustomobject]@{ event = "unlabeled"; label = [pscustomobject]@{ name = "decision:fix" }; actor = [pscustomobject]@{ login = "abmorrill4" } },
        [pscustomobject]@{ event = "labeled"; label = [pscustomobject]@{ name = "decision:fix" }; actor = [pscustomobject]@{ login = "someone-else" } }
    )
    It 'returns who added the label last' { Get-LabelActor $events "decision:fix" | Should Be "someone-else" }
    It 'returns nothing for a label never added' { Get-LabelActor $events "decision:plan" | Should BeNullOrEmpty }
}
