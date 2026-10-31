# Pester 3. Get-ChangedScenarios names the scenarios a branch adds or changes, and stop-on-fail wiring is present.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$script = Join-Path $here "Get-ChangedScenarios.ps1"

function New-Repo([scriptblock]$Change) {
    $d = Join-Path $env:TEMP ("changed-" + [guid]::NewGuid().ToString("n"))
    $s = Join-Path $d "tests\e2e\scenarios"
    New-Item -ItemType Directory -Force $s | Out-Null
    foreach ($n in "one", "two", "ai-x") { Set-Content (Join-Path $s "$n.ps1") "# $n" }
    Set-Content (Join-Path $d "other.txt") "a"
    $ErrorActionPreference = "Continue"   # git writes progress to stderr; CI runs Pester with Stop
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
    # Runs the real script (Windows PowerShell 5.1) over stub scenarios: b fails, so c must not run.
    function Invoke-Stubs([switch]$StopOnFail) {
        $d = Join-Path $env:TEMP ("stubs-" + [guid]::NewGuid().ToString("n")); $out = Join-Path $d "out"
        New-Item -ItemType Directory -Force $d | Out-Null
        Set-Content (Join-Path $d "zz-a.ps1") 'param($Fixture)'
        Set-Content (Join-Path $d "zz-b.ps1") 'param($Fixture); throw "boom"'
        Set-Content (Join-Path $d "zz-c.ps1") ('param($Fixture); Set-Content "' + (Join-Path $d "c-ran.txt") + '" x')
        $cmd = "& '" + (Join-Path $here "Run-FlareE2E.ps1") + "' -ScenarioDir '$d' -Scenario zz-a,zz-b,zz-c -OutDir '$out'" + $(if ($StopOnFail) { " -StopOnFail" } else { "" }) + "; exit `$LASTEXITCODE"
        & powershell.exe -NoProfile -Command $cmd 2>&1 | Out-Null
        $code = $LASTEXITCODE
        $rows = @((Get-Content (Join-Path $out "results.json") -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
        $ran = Test-Path (Join-Path $d "c-ran.txt")
        Remove-Item $d -Recurse -Force
        @{ Code = $code; Rows = ($rows | ForEach-Object { $_.scenario }) -join ","; CRan = $ran }
    }
    It "stops after the first failure and keeps the rows so far" {
        $r = Invoke-Stubs -StopOnFail
        $r.Rows | Should Be "zz-a,zz-b"
        $r.CRan | Should Be $false
        $r.Code | Should Be 1
    }
    It "runs every scenario without -StopOnFail" {
        $r = Invoke-Stubs
        $r.Rows | Should Be "zz-a,zz-b,zz-c"
        $r.CRan | Should Be $true
        $r.Code | Should Be 1
    }
    It "records a given -InstallSeconds on every row" {
        $d = Join-Path $env:TEMP ("stubs-" + [guid]::NewGuid().ToString("n")); New-Item -ItemType Directory -Force $d | Out-Null
        Set-Content (Join-Path $d "zz-a.ps1") 'param($Fixture)'
        & powershell.exe -NoProfile -File (Join-Path $here "Run-FlareE2E.ps1") -ScenarioDir $d -Scenario zz-a -OutDir (Join-Path $d "out") -InstallSeconds 77 2>&1 | Out-Null
        $row = @(Get-Content (Join-Path $d "out\results.json") -Raw | ConvertFrom-Json)[0]
        Remove-Item $d -Recurse -Force
        $row.installSeconds | Should Be 77
    }
}
