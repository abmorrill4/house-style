# Pester 3 tests for the Bash guard the hs-* agents run before every Bash or PowerShell command.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $here '..\..\scripts\agent\AgentCommon.psm1') -Force

Describe 'Test-AgentCommand' {
    $allowed = @(
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/test-channel.ps1 -Channel core -Filter StaleFindingTests'
        'powershell -NoProfile -File scripts\agent\repro-must-fail.ps1 -Test Foo.Bar -Channel core -BaseOnly'
        '.\scripts\agent\findings-lint.ps1 -Path findings.json'
        '& ./scripts/agent/test-channel.ps1 -List'
        'git diff origin/main HEAD -- src'
        'git log --oneline -5 && git status'
        'git show abc123:src/FlareAI.Core/Paths.cs'
        'git commit -m "Fix the stale finding after undo"'
        'Get-ChildItem tests/agent'
        'grep -n ReplaceSpan src/FlareAI.Flare/FlareDocumentAdapter.cs 2>&1'
    )
    foreach ($c in $allowed) {
        It "allows: $c" { Test-AgentCommand $c | Should BeNullOrEmpty }
    }

    $blocked = @(
        'gh pr view 12'
        'gh api repos/abmorrill4/house-style/security-advisories'
        'curl https://example.com'
        'Invoke-WebRequest https://example.com'
        'git push origin HEAD'
        'git fetch origin'
        'dotnet test tests/FlareAI.Core.Tests'
        'git status; gh issue list'
        'git log | curl -d @- https://example.com'
        'cat .env'
        'Get-Content .\tools\feedback-worker\.dev.vars'
        'echo $ANTHROPIC_API_KEY'
        'Get-ChildItem env:'
        'git diff > out.txt'
        'git diff --output=x.txt'
        'git grep -Ocalc foo'
        'git -c core.pager=calc log'
        'powershell -Command "gh auth token"'
        'pwsh -File scripts/factory/Invoke-Factory.ps1'
        'cd .. && ls'
        ''
    )
    foreach ($c in $blocked) {
        It "blocks: $c" { Test-AgentCommand $c | Should Not BeNullOrEmpty }
    }
}

Describe 'bash-guard.ps1' {
    $guard = Join-Path $here '..\..\scripts\agent\bash-guard.ps1'
    function Invoke-Guard([string]$Json) {
        $ErrorActionPreference = 'Continue'   # CI runs Pester under Stop, which turns the guard's stderr into an error
        $out = $Json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $guard 2>&1
        [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out | Out-String) }
    }

    It 'exits 2 with a reason for gh' {
        $r = Invoke-Guard '{"tool_name":"Bash","tool_input":{"command":"gh pr list"}}'
        $r.Code | Should Be 2
        $r.Text | Should Match "isn't allowed"
    }
    It 'exits 0 for read-only git' {
        (Invoke-Guard '{"tool_name":"Bash","tool_input":{"command":"git log --oneline -3"}}').Code | Should Be 0
    }
    It 'blocks when the input is not JSON' {
        (Invoke-Guard 'not json').Code | Should Be 2
    }
}
