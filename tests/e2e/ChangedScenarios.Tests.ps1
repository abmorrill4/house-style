# Pester 3. Get-ChangedScenarios names the scenarios a branch adds or changes, and stop-on-fail wiring is present.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$script = Join-Path $here "Get-ChangedScenarios.ps1"

function New-Repo([scriptblock]$Change) {
    $d = Join-Path $env:TEMP ("changed-" + [guid]::NewGuid().ToString("n"))
    $s = Join-Path $d "tests\e2e\scenarios"
    New-Item -ItemType Directory -Force $s | Out-Null
    foreach ($n in "one", "two", "ai-x") { Set-Content (Join-Path $s "$n.ps1") "# $n" }
    Set-Content (Join-Path $d "other.txt") "a"
    $g = { git -C $d -c user.email=t@t -c user.name=t @args 2>&1 | Out-Null }
    & $g init -b main; & $g add -A; & $g commit -m base
    & $g checkout -b topic
    & $Change $d $s
    & $g add -A; & $g commit -m change
    $d
}

Describe "Get-ChangedScenarios" {
    It "lists added and changed scenarios, not deleted or other files" {
        $d = New-Repo { param($d, $s)
            Add-Content (Join-Path $s "one.ps1") "# edit"; Add-Content (Join-Path $s "ai-x.ps1") "# edit"
            Set-Content (Join-Path $s "three.ps1") "# new"; Remove-Item (Join-Path $s "two.ps1")
            Add-Content (Join-Path $d "other.txt") "b" }
        try {
            (@(& $script -Base main -Root $d) -join ",") | Should Be "ai-x,one,three"
            (@(& $script -Base main -Root $d -NoAi) -join ",") | Should Be "one,three"
        } finally { Remove-Item $d -Recurse -Force }
    }
    It "gives nothing when no scenario changed" {
        $d = New-Repo { param($d, $s) Add-Content (Join-Path $d "other.txt") "b" }
        try { @(& $script -Base main -Root $d).Count | Should Be 0 } finally { Remove-Item $d -Recurse -Force }
    }
}

Describe "Run-FlareE2E -StopOnFail" {
    $text = Get-Content (Join-Path $here "Run-FlareE2E.ps1") -Raw
    It "is a switch parameter" { $text | Should Match '\[switch\]\$StopOnFail' }
    It "breaks out of the loop before results.json is written" {
        $text.IndexOf('-StopOnFail)."; break') | Should BeGreaterThan 0
        $text.IndexOf('-StopOnFail)."; break') | Should BeLessThan $text.IndexOf('results.json")')
    }
}
