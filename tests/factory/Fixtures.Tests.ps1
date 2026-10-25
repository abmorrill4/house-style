# Pester 3 tests for the captured fixtures (factory-runtime spec section 12; P18): each one present records its tool,
# version, machine and commands, matches its pin, and is redacted; only the fixtures the spec allows may stay missing
# once the migration is done; and the parser tests read fixtures only.
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $here 'Fixtures.psm1') -Force
$pins = Import-PowerShellDataFile (Join-Path $root 'scripts\factory\Pins.psd1')
$expected = @(Get-ExpectedFixtures)
$files = @(Get-ChildItem $FixtureDir -Filter *.json | Where-Object Name -ne 'expected.json')

Describe 'The fixture list' {
    It 'names each fixture once, with its tool pinned and a capture step' {
        @($expected.name | Select-Object -Unique).Count | Should Be $expected.Count
        foreach ($e in $expected) {
            $pins.ContainsKey($e.tool) | Should Be $true
            "$($e.step)" | Should Match '^(\d+|when one happens)$'
        }
    }
    It 'holds no fixture it doesn''t expect' {
        foreach ($f in $files) { "$($f.BaseName) expected=$(@($expected.name) -contains $f.BaseName)" | Should Be "$($f.BaseName) expected=True" }
    }
    It 'only claude-usage-limit and flare-licensed may stay missing once the migration is done' {
        foreach ($e in @($expected | Where-Object { -not ("$($_.step)" -match '^\d+$' -and [int]$_.step -lt 18) })) { @("claude-usage-limit", "flare-licensed") -contains $e.name | Should Be $true }
    }
    # Each fixture a parser needs is here, or its tests skip with the step that captures it. Only the two the spec
    # allows to be pending pass while missing.
    foreach ($e in $expected) {
        $allowed = @("claude-usage-limit", "flare-licensed") -contains $e.name
        $gone = [bool](Get-FixtureSkip $e.name)
        if ($allowed -and $gone) { It "$($e.name) may be pending ($(Get-FixtureSkip $e.name))" { $true | Should Be $true }; continue }
        It (Get-FixtureTitle "$($e.name) is captured" @($e.name)) -Skip:$gone { Test-Path (Join-Path $FixtureDir "$($e.name).json") | Should Be $true }
    }
    It 'README.md lists every fixture still to capture, with its step' {
        $readme = Get-Content (Join-Path $FixtureDir 'README.md') -Raw
        foreach ($e in @($expected | Where-Object { Get-FixtureSkip $_.name })) { $readme | Should Match ("``$([regex]::Escape($e.name))``") }
        foreach ($f in $files) { $readme | Should Not Match ("``$([regex]::Escape($f.BaseName))``.*not captured") }
    }
}

Describe 'Each captured fixture' {
    foreach ($f in $files) {
        $fx = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $text = Get-Content $f.FullName -Raw -Encoding UTF8
        It "$($f.BaseName) records its tool, version, machine and commands" {
            $fx.name | Should Be $f.BaseName
            foreach ($k in "tool", "version", "machine", "capturedAt") { "$k=$($fx.$k)" | Should Not Be "$k=" }
            @($fx.cases).Count | Should BeGreaterThan 0
            foreach ($c in @($fx.cases)) { "$($c.case) $($c.command)" | Should Match '^\S+ \S' }
            $fx.redacted | Should Be $true
        }
        It "$($f.BaseName) matches its pin" {
            $want = @($expected | Where-Object name -eq $f.BaseName)[0]
            $fx.tool | Should Be $want.tool
            $pins[$fx.tool] | Should Not BeNullOrEmpty
            $fx.version | Should Be $pins[$fx.tool]
        }
        It "$($f.BaseName) is redacted" {
            foreach ($p in '\bsk-ant-[A-Za-z0-9_-]{8,}', '\bgh[pousr]_[A-Za-z0-9]{20,}', '\bgithub_pat_', '\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.',
                           '(?i)\b[A-Z]:[\\/]+HouseStyle\b', '(?i)\b[A-Z]:[\\/]+Users[\\/]+(?!_user_|Public|Default|factory|hsagent)\w', '(?i)/[a-z]/Users/(?!_user_|Public|Default|factory|hsagent)\w',
                           '\bS-1-5-21-(?!0-0-0\b)\d+-\d+-\d+', '(?i)\b[\w.+-]+@(?!example\.invalid\b|users\.noreply\.github\.com\b)[\w-]+\.[\w.-]+', '(?i)\b([0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b',
                           '\b[A-Z0-9]{4,6}(-[A-Z0-9]{4,6}){3,}\b') {
                "$($f.BaseName) /$p/ $([regex]::IsMatch($text, $p))" | Should Be "$($f.BaseName) /$p/ False"
            }
            foreach ($m in [regex]::Matches($text, '\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b')) {
                $m.Value | Should Match '^(127\.|0\.0\.0\.0$|255\.255\.|192\.0\.2\.|198\.51\.100\.|203\.0\.113\.)'
            }
            foreach ($n in @($env:COMPUTERNAME, $env:USERNAME) | Where-Object { $_ -and $_.Length -ge 4 }) { $text -match "(?i)(?<![A-Za-z0-9])$([regex]::Escape($n))(?![A-Za-z0-9])" | Should Be $false }
        }
    }
}

Describe 'Parser tests read real output only' {
    It 'each parser test file reads its fixtures through Fixtures.psm1' {
        foreach ($t in "PatchCheck.Tests.ps1", "Checks.Tests.ps1") {
            $src = Get-Content (Join-Path $here $t) -Raw
            $src | Should Match "Import-Module \(Join-Path \`$here 'Fixtures\.psm1'\)"
            $src | Should Match 'Get-Fixture '
        }
    }
    It 'Save-Fixtures.ps1 refuses a version that isn''t pinned, and redacts before it writes' {
        $src = Get-Content (Join-Path $root 'scripts\factory\Save-Fixtures.ps1') -Raw
        $src | Should Match 'but Pins\.psd1 pins'
        $src.IndexOf('Protect-Value $c[$p]') | Should BeLessThan $src.IndexOf('[IO.File]::WriteAllText($path')
    }
}
