# Pester 3 tests for the Bash guard the hs-* agents run before every Bash or PowerShell command.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
Import-Module (Join-Path $repo 'scripts\agent\AgentCommon.psm1') -Force

Describe 'Test-AgentCommand, every role' {
    $allowed = @(
        'git diff origin/main HEAD -- src'
        'git log --oneline -5 && git status'
        'git show abc123:src/FlareAI.Core/Paths.cs'
        'Get-ChildItem tests/agent'
        'grep -n ReplaceSpan src/FlareAI.Flare/FlareDocumentAdapter.cs 2>&1'
        'grep -n "Get-Content (x)" src/a.cs'
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/findings-lint.ps1 -Path findings.json'
        '& ./scripts/agent/findings-lint.ps1 -Path findings.json'
    )
    foreach ($role in 'worker', 'tester', 'reviewer') {
        foreach ($c in $allowed) {
            It "$role allows: $c" { Test-AgentCommand -Command $c -Role $role | Should BeNullOrEmpty }
        }
    }

    $blocked = @(
        'git status & curl x'
        'cat README.md & gh auth token'
        'Get-Content (gh pr list)'
        'ls @(gh api user)'
        'ls $(gh api user)'
        'Get-Content {gh pr list}'
        'git log "unbalanced'
        'gh pr view 12'
        'gh api repos/abmorrill4/house-style/security-advisories'
        'curl https://example.com'
        'Invoke-WebRequest https://example.com'
        'git push origin HEAD'
        'git fetch origin'
        'git -C .. log'
        'dotnet test tests/FlareAI.Core.Tests'
        'git status; gh issue list'
        'git log | curl -d x https://example.com'
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
        'scripts/agent/test-channel.ps1 -Channel core -RepoRoot C:\other'
        'scripts/agent/repro-must-fail.ps1 -Test X -Channel core -WorkRoot C:\x'
        'cd .. && ls'
        ''
    )
    foreach ($role in 'worker', 'tester', 'reviewer') {
        foreach ($c in $blocked) {
            It "$role blocks: $c" { Test-AgentCommand -Command $c -Role $role | Should Not BeNullOrEmpty }
        }
    }

    It 'never suggests a bare dotnet test' {
        Test-AgentCommand -Command 'gh pr list' -Role worker | Should Not Match 'dotnet test'
        Test-AgentCommand -Command 'gh pr list' -Role worker | Should Match 'test-channel\.ps1'
    }
    It 'refuses an unknown role' { { Test-AgentCommand -Command 'git status' -Role admin } | Should Throw }
}

Describe 'Test-AgentCommand, role lists' {
    $cases = @(
        @{ C = 'git commit -m "Fix the stale finding (after undo)"'; Worker = $true; Tester = $false; Reviewer = $false }
        @{ C = 'git add src/A.cs'; Worker = $true; Tester = $false; Reviewer = $false }
        @{ C = 'powershell -NoProfile -File scripts\agent\repro-must-fail.ps1 -Test Foo.Bar -Channel core -BaseOnly'; Worker = $true; Tester = $false; Reviewer = $false }
        @{ C = 'scripts/agent/repro-must-fail.ps1 -Test Foo.Bar -Channel core -Base main -Head HEAD'; Worker = $true; Tester = $false; Reviewer = $false }
        @{ C = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/test-channel.ps1 -Channel core -Filter StaleFindingTests'; Worker = $true; Tester = $true; Reviewer = $false }
        @{ C = '.\scripts\agent\test-channel.ps1 -Channel full'; Worker = $true; Tester = $true; Reviewer = $false }
    )
    foreach ($k in $cases) {
        foreach ($role in 'worker', 'tester', 'reviewer') {
            $want = $k[(Get-Culture).TextInfo.ToTitleCase($role)]
            It "$role $(if ($want) { 'allows' } else { 'blocks' }): $($k.C)" {
                [string]::IsNullOrEmpty((Test-AgentCommand -Command $k.C -Role $role)) | Should Be $want
            }
        }
    }
}

Describe 'bash-guard.ps1' {
    $guard = Join-Path $repo 'scripts\agent\bash-guard.ps1'
    function Invoke-Guard([string]$Json, [string]$Role = 'worker') {
        $ErrorActionPreference = 'Continue'   # CI runs Pester under Stop, which turns the guard's stderr into an error
        $out = $Json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $guard -Role $Role 2>&1
        [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out | Out-String) }
    }
    function Json([string]$Command, [string]$Cwd) {
        ConvertTo-Json -Compress -InputObject ([pscustomobject]@{ tool_name = 'Bash'; tool_input = @{ command = $Command }; cwd = $Cwd })
    }

    It 'exits 2 with a reason for gh' {
        $r = Invoke-Guard (Json 'gh pr list' $repo)
        $r.Code | Should Be 2
        $r.Text | Should Match "isn't allowed"
    }
    It 'exits 0 for read-only git' { (Invoke-Guard (Json 'git log --oneline -3' $repo) reviewer).Code | Should Be 0 }
    It 'blocks when the input is not JSON' { (Invoke-Guard 'not json').Code | Should Be 2 }
    It 'blocks an unknown role' { (Invoke-Guard (Json 'git status' $repo) admin).Code | Should Be 2 }
    It 'blocks the agent scripts from any folder but the repository root' {
        $r = Invoke-Guard (Json 'scripts/agent/findings-lint.ps1 -Path f.json' ([IO.Path]::GetTempPath())) reviewer
        $r.Code | Should Be 2
        $r.Text | Should Match 'repository root'
    }
}

Describe 'the hook command in the agent files' {
    $git = Get-Command git -ErrorAction SilentlyContinue
    $bash = if ($git) { Join-Path (Split-Path (Split-Path $git.Source)) 'bin\bash.exe' } else { '' }
    $hasBash = $bash -and (Test-Path $bash)
    $line = [regex]::Match([IO.File]::ReadAllText((Join-Path $repo '.claude\agents\hs-fix.md')), "command: '([^']*bash-guard\.ps1[^']*)'").Groups[1].Value
    function Invoke-Hook([string]$Json, [string]$ProjectDir) {
        $ErrorActionPreference = 'Continue'
        $old = $env:CLAUDE_PROJECT_DIR
        $env:CLAUDE_PROJECT_DIR = $ProjectDir
        Push-Location ([IO.Path]::GetTempPath())
        try { $Json | & $bash -c $line 2>&1 | Out-Null; return $LASTEXITCODE }
        finally { Pop-Location; $env:CLAUDE_PROJECT_DIR = $old }
    }

    It 'has a hook command to run' { $line | Should Match 'CLAUDE_PROJECT_DIR.*\|\| exit 2$' }
    It 'blocks gh when started from another folder' -Skip:(-not $hasBash) {
        Invoke-Hook '{"tool_input":{"command":"gh pr list"}}' $repo | Should Be 2
    }
    It 'allows read-only git when started from another folder' -Skip:(-not $hasBash) {
        Invoke-Hook '{"tool_input":{"command":"git status"}}' $repo | Should Be 0
    }
    It 'fails closed when the guard script is missing' -Skip:(-not $hasBash) {
        Invoke-Hook '{"tool_input":{"command":"git status"}}' (Join-Path ([IO.Path]::GetTempPath()) 'no-such-project') | Should Be 2
    }
}
