# Pester 3 tests for scripts/agent/stop-check.ps1, the Stop hook of hs-fix and hs-fix-opus.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$hook = Join-Path $repo 'scripts\agent\stop-check.ps1'

Describe 'stop-check.ps1' {
    $name = 'agent-tmp-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $dir = Join-Path $repo "tests\$name"
    New-Item -ItemType Directory $dir | Out-Null
    function Invoke-Hook([string]$Json) {
        $out = $Json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hook -Paths "tests/$name" 2>&1
        [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out | Out-String) }
    }

    It 'lets the agent stop when the fast checks pass' {
        Set-Content (Join-Path $dir 'Ok.Tests.ps1') 'Describe Ok { It "passes" { 1 | Should Be 1 } }'
        (Invoke-Hook '{"stop_hook_active":false}').Code | Should Be 0
    }
    It 'sends the agent back with a reason when they fail' {
        Set-Content (Join-Path $dir 'Bad.Tests.ps1') 'Describe Bad { It "fails" { 1 | Should Be 2 } }'
        $r = Invoke-Hook '{"stop_hook_active":false}'
        $r.Code | Should Be 2
        $r.Text | Should Match 'fast checks fail'
    }
    It 'lets the agent stop the second time, so it never loops' {
        (Invoke-Hook '{"stop_hook_active":true}').Code | Should Be 0
    }

    Remove-Item -Recurse -Force $dir
}
