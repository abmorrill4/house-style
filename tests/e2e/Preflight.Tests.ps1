# Pester 3. The scenario preflight catches each mistake that cost a Flare launch in the #248 pilot, and passes every
# scenario in the repo.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$check = Join-Path $here "Test-E2EScenario.ps1"

function Preflight([string]$Body) {
    $f = Join-Path $env:TEMP ("preflight-" + [guid]::NewGuid().ToString("n") + ".ps1")
    [IO.File]::WriteAllText($f, $Body)
    try { @(& $check -Path $f) } finally { Remove-Item $f }
}

Describe "Test-E2EScenario" {
    It "passes every scenario in the repo" {
        foreach ($s in Get-ChildItem (Join-Path $here "scenarios") -Filter *.ps1) {
            $out = @(& $check -Path $s.FullName)
            ($out -join "`n") | Should Be ""
        }
    }
    It "finds a control character left by a string escape" {
        $out = Preflight ('$p = Join-Path $run.Project "Content' + [char]8 + 'illpay' + [char]12 + 'aq.htm"')
        ($out -join "`n") | Should Match "control character"
    }
    It "finds a path the fixture doesn't have" {
        (Preflight '$p = Join-Path $run.Project "Content\billpay\nope.htm"') -join "`n" | Should Match "no such file"
    }
    It "finds a verb the hook doesn't answer" {
        (Preflight 'Send-E2E clik "Close"') -join "`n" | Should Match "isn't a verb"
    }
    It "finds a ribbon label the plugin doesn't have" {
        (Preflight 'Invoke-Ribbon "Lint Everything"') -join "`n" | Should Match "isn't a label"
    }
    It "finds an Assert-E2E condition that is a list, not a true or false value" {
        (Preflight 'Assert-E2E (@(1,2)) "m"') -join "`n" | Should Match "not a true or false"
        (Preflight 'Assert-E2E (@($a) -match "x") "m"') -join "`n" | Should Match "not a true or false"
        (Preflight 'Assert-E2E ($a | Where-Object { $_ }) "m"') -join "`n" | Should Match "not a true or false"
        (Preflight 'Assert-E2E (1) "m" "extra"') -join "`n" | Should Match "condition in parentheses"
    }
    It "finds a button, menu item, tab, box or dialog title the plugin doesn't have" {
        (Preflight 'Send-E2E click "Chnage"') -join "`n" | Should Match "no button, tab or menu item"
        (Preflight 'Send-E2E rowMenu "Nopeee"') -join "`n" | Should Match "no button, tab or menu item"
        (Preflight 'Send-E2E dialogTab "Nopeee" -Extra @{ title = "Settings" }') -join "`n" | Should Match "no button, tab or menu item"
        (Preflight 'Send-E2E dialogType "x" -Extra @{ name = "Nopeee" }') -join "`n" | Should Match "no box"
        (Preflight 'Send-E2E dialogTab "Advanced" -Extra @{ title = "Nopeee" }') -join "`n" | Should Match "no dialog title"
    }
}

