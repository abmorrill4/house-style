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
    It "passes a path the fixture has" {
        (Preflight '$p = Join-Path $run.Project "Content\billpay\faq.htm"').Count | Should Be 0
    }
    It "finds a verb the hook doesn't answer" {
        (Preflight 'Send-E2E clik "Close"') -join "`n" | Should Match "isn't a verb"
    }
    It "finds replace text no topic has, but not text the scenario typed" {
        (Preflight 'Send-E2E replace "zzqq never written" -Extra @{ text = "x" }') -join "`n" | Should Match "no fixture topic"
        (Preflight "Send-E2E replace `"a`" -Extra @{ text = `"zzqq typed`" }`nSend-E2E replace `"zzqq typed`" -Extra @{ text = `"a`" }") -join "`n" | Should Not Match "zzqq typed"
    }
    It "finds a ribbon label the plugin doesn't have" {
        (Preflight 'Invoke-Ribbon "Lint Everything"') -join "`n" | Should Match "isn't a label"
    }
    It "skips calls meant to fail" {
        (Preflight 'Fails { Send-E2E replace "zzqq never written" -Extra @{ text = "x" } }').Count | Should Be 0
    }
}

Describe "New-E2EResultRow" {
    Import-Module (Join-Path $here "FlareE2E.psm1") -Force
    $r = [ordered]@{ passed = $true; kind = $null; seconds = 5; error = $null; version = "1"; artifacts = @(); flareStartSeconds = 7; windowState = "Normal" }
    It "adds the run-level fields after the scenario's own" {
        $row = New-E2EResultRow "x" $r 42 9 "1920x1080"
        $row.scenario | Should Be "x"
        $row.installSeconds | Should Be 42
        $row.queueSeconds | Should Be 9
        $row.resolution | Should Be "1920x1080"
        $row.flareStartSeconds | Should Be 7
        $row.windowState | Should Be "Normal"
        (@($row.PSObject.Properties.Name)[-3..-1]) -join "," | Should Be "installSeconds,queueSeconds,resolution"
    }
    It "leaves queueSeconds and installSeconds empty when unknown" {
        $row = New-E2EResultRow "x" $r $null -1 $null
        $row.queueSeconds | Should BeNullOrEmpty
        $row.installSeconds | Should BeNullOrEmpty
    }
}
