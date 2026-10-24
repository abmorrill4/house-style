# Pester 3 tests for scripts/agent/findings-lint.ps1 and the formats in the findings and skeptic-protocol skills.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$lint = Join-Path $here '..\..\scripts\agent\findings-lint.ps1'
Import-Module (Join-Path $here '..\..\scripts\agent\Findings.psm1') -Force

Describe 'Test-Findings' {
    It 'accepts well-formed findings' {
        $j = '{"findings":[{"severity":"blocking","title":"Retry reuses the token","where":"src/FlareAI.Core/Llm/AnthropicClient.cs:212","detail":"Every retry fails."},{"severity":"note","title":"Name","where":"spec section 2","detail":"x","lens":"goals"}]}'
        @(Test-Findings $j).Count | Should Be 0
    }
    It 'accepts an empty list' { @(Test-Findings '{"findings":[]}').Count | Should Be 0 }
    It 'accepts an issue or PR as the place' {
        @(Test-Findings '{"findings":[{"severity":"note","title":"t","where":"#213","detail":"d"}]}').Count | Should Be 0
    }
    It 'refuses text that is not JSON' { Test-Findings 'nope' | Should Match 'valid JSON' }
    It 'refuses an object with no findings list' { Test-Findings '{"items":[]}' | Should Match 'no findings list' }
    It 'names each broken field' {
        $p = (Test-Findings '{"findings":[{"severity":"major","title":"","where":"somewhere","detail":"","lens":"vibes"}]}') -join ' '
        $p | Should Match "severity 'major'"
        $p | Should Match 'no title'
        $p | Should Match "name a file and line"
        $p | Should Match 'no detail'
        $p | Should Match "unknown lens 'vibes'"
    }
    It 'refuses a title longer than 120 characters' {
        $t = 'x' * 121
        Test-Findings "{`"findings`":[{`"severity`":`"note`",`"title`":`"$t`",`"where`":`"a.cs:1`",`"detail`":`"d`"}]}" | Should Match 'longer than'
    }
    It 'refuses a repeated finding' {
        $f = '{"severity":"note","title":"Same","where":"a.cs:1","detail":"d"}'
        Test-Findings "{`"findings`":[$f,$f]}" | Should Match 'repeats finding 1'
    }
}

Describe 'Test-Verdict' {
    It 'accepts a verdict with evidence' {
        @(Test-Verdict '{"refuted":false,"reason":"It holds.","evidence":"src/a.cs:10-14"}').Count | Should Be 0
    }
    It 'refuses a verdict without evidence' { Test-Verdict '{"refuted":true,"reason":"r"}' | Should Match 'no evidence' }
    It 'refuses "I read the code" as evidence' {
        Test-Verdict '{"refuted":true,"reason":"r","evidence":"I read the code."}' | Should Match 'name what was checked'
    }
    It 'refuses a refuted value that is not true or false' {
        Test-Verdict '{"refuted":"yes","reason":"r","evidence":"a.cs:1"}' | Should Match 'true or false'
    }
}

Describe 'findings-lint.ps1' {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('hs-lint-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $dir | Out-Null
    It 'exits 0 for a good file' {
        $f = Join-Path $dir 'good.json'
        Set-Content $f '{"findings":[]}' -Encoding UTF8
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $lint -Path $f | Out-Null
        $LASTEXITCODE | Should Be 0
    }
    It 'exits 1 and prints the problems for a bad file' {
        $f = Join-Path $dir 'bad.json'
        Set-Content $f '{"findings":[{"severity":"x","title":"t","where":"a.cs:1","detail":"d"}]}' -Encoding UTF8
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $lint -Path $f
        $LASTEXITCODE | Should Be 1
        ($out -join ' ') | Should Match "severity 'x'"
    }
    It 'checks a verdict with -Kind verdict' {
        $f = Join-Path $dir 'verdict.json'
        Set-Content $f '{"refuted":false,"reason":"r","evidence":"a.cs:1"}' -Encoding UTF8
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $lint -Path $f -Kind verdict | Out-Null
        $LASTEXITCODE | Should Be 0
    }
    It 'exits 2 when the file is missing' {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $lint -Path (Join-Path $dir 'none.json') | Out-Null
        $LASTEXITCODE | Should Be 2
    }
    Remove-Item -Recurse -Force $dir
}
