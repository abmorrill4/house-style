# Pester 3 tests for scripts/agent/repro-must-fail.ps1: the rules as functions, then whole runs in a scratch git repo.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$script = Join-Path $repo 'scripts\agent\repro-must-fail.ps1'
Import-Module (Join-Path $repo 'scripts\agent\Repro.psm1') -Force

function New-Trx([string[]]$Rows) {
    # $Rows: "Name|Outcome|Message"
    $results = foreach ($r in $Rows) {
        $n, $o, $m = $r -split '\|', 3
        $msg = [Security.SecurityElement]::Escape($m)
        "<UnitTestResult testName=`"$n`" outcome=`"$o`"><Output><ErrorInfo><Message>$msg</Message><StackTrace>at $n()</StackTrace></ErrorInfo></Output></UnitTestResult>"
    }
    $path = Join-Path ([IO.Path]::GetTempPath()) ('hs-trx-' + [guid]::NewGuid().ToString('N') + '.trx')
    Set-Content $path "<?xml version=`"1.0`" encoding=`"utf-8`"?><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Results>$($results -join '')</Results></TestRun>" -Encoding UTF8
    return $path
}

Describe 'Get-XunitFailureKind' {
    It 'reads an xunit assertion as an assertion' { Get-XunitFailureKind "Assert.Equal() Failure: Values differ`nExpected: 2" | Should Be 'assertion' }
    It 'reads a custom assertion message as an assertion' { Get-XunitFailureKind 'custom' | Should Be 'assertion' }
    It 'reads an unexpected exception as an exception' {
        Get-XunitFailureKind 'System.NullReferenceException : Object reference not set to an instance of an object.' | Should Be 'exception'
        Get-XunitFailureKind 'System.IO.FileNotFoundException : Could not load file' | Should Be 'exception'
    }
}

Describe 'Read-TrxOutcome' {
    It 'finds an assertion failure by full or short name' {
        $p = New-Trx @('FlareAI.Core.Tests.StaleTests.Undo|Failed|Assert.True() Failure', 'FlareAI.Core.Tests.StaleTests.Other|Passed|')
        (Read-TrxOutcome $p 'FlareAI.Core.Tests.StaleTests.Undo').Outcome | Should Be 'assertion'
        (Read-TrxOutcome $p 'Undo').Outcome | Should Be 'assertion'
        (Read-TrxOutcome $p 'Other').Outcome | Should Be 'passed'
        Remove-Item $p
    }
    It 'counts an exception in any theory row' {
        $p = New-Trx @('A.B.T(x: 1)|Failed|Assert.Equal() Failure', 'A.B.T(x: 2)|Failed|System.InvalidOperationException : boom')
        (Read-TrxOutcome $p 'A.B.T').Outcome | Should Be 'exception'
        Remove-Item $p
    }
    It 'says when the test did not run' {
        $p = New-Trx @('A.B.Other|Passed|')
        (Read-TrxOutcome $p 'A.B.Missing').Outcome | Should Be 'notfound'
        Remove-Item $p
    }
}

Describe 'Get-RunOutcome' {
    It 'reads a build error when there is no result file' {
        $o = Get-RunOutcome -Channel core -Test 'X' -ExitCode 1 -Output @('Build started', "C:\x\T.cs(3,5): error CS0246: The type or namespace name 'Widget' could not be found", 'Build FAILED.')
        $o.Outcome | Should Be 'build-error'
        $o.Message | Should Match 'CS0246'
    }
    It 'reads no result when nothing built or ran' {
        (Get-RunOutcome -Channel core -Test 'X' -ExitCode 1 -Output @('something else')).Outcome | Should Be 'no-result'
    }
}

Describe 'Get-ReproVerdict' {
    $assert = [pscustomobject]@{ Outcome = 'assertion'; Message = 'Expected 2' }
    $pass = [pscustomobject]@{ Outcome = 'passed'; Message = '' }
    It 'passes red-by-assertion on base and green on head' { (Get-ReproVerdict $assert $pass 'T').Pass | Should Be $true }
    It 'refuses red-by-build-error on base' {
        $v = Get-ReproVerdict ([pscustomobject]@{ Outcome = 'build-error'; Message = 'error CS0246' }) $pass 'T'
        $v.Pass | Should Be $false
        $v.Reason | Should Match "don't build on base"
    }
    It 'refuses an exception on base' {
        (Get-ReproVerdict ([pscustomobject]@{ Outcome = 'exception'; Message = 'NRE' }) $pass 'T').Reason | Should Match 'not an assertion'
    }
    It 'refuses a test that passes on base' { (Get-ReproVerdict $pass $pass 'T').Reason | Should Match "doesn't show the bug" }
    It 'refuses a test that still fails on head' { (Get-ReproVerdict $assert $assert 'T').Reason | Should Match 'still fails on head' }
}

Describe 'Find-TestInDiff' {
    It 'finds the test on an added line under tests/ only' {
        $diff = @('+++ b/src/A.cs', '+ // IgnoreSticks', '+++ b/tests/X/ATests.cs', '@@ -1 +1,2 @@', '+    public void IgnoreSticks() {', ' context IgnoreSticks')
        Find-TestInDiff -DiffLines $diff -Name 'IgnoreSticks' | Should Be 'tests/X/ATests.cs'
    }
    It 'finds nothing when the name is only on context or removed lines' {
        @(Find-TestInDiff -DiffLines @('+++ b/tests/A.cs', ' IgnoreSticks', '-IgnoreSticks') -Name 'IgnoreSticks').Count | Should Be 0
    }
}

Describe 'repro-must-fail.ps1 in a scratch repo (Pester channel)' {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('hs-repro-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $work = Join-Path $root 'work'
    $lock = Join-Path $root 'lock'
    New-Item -ItemType Directory -Force (Join-Path $root 'r\src'), (Join-Path $root 'r\tests') | Out-Null
    $r = Join-Path $root 'r'
    function G { $ErrorActionPreference = 'Continue'; & git -C $r -c user.name=test -c user.email=test@example.invalid -c commit.gpgsign=false @args 2>&1 | Out-Null }
    function Commit([string]$Message) { G add -A; G commit -q -m $Message }
    function Put([string]$Rel, [string]$Text) {
        $p = Join-Path $r $Rel
        New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
        Set-Content $p $Text
    }
    function Repro([string]$Test, [string]$Head, [switch]$BaseOnly) {
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-Test', $Test, '-Channel', 'pester', '-Base', 'main', '-Head', $Head, '-RepoRoot', $r, '-WorkRoot', $work, '-LockPath', $lock)
        if ($BaseOnly) { $a += '-BaseOnly' }
        $out = & powershell.exe @a
        [pscustomobject]@{ Code = $LASTEXITCODE; Json = (($out | Where-Object { $_ -like '{*' }) | Select-Object -Last 1 | ConvertFrom-Json) }
    }

    G init -q -b main
    Put ('src\Calc.ps1') 'function Add-Two([int]$x) { $x + 1 }'
    Commit 'base with the bug'

    # Red by assertion on base, green on head.
    G checkout -q -b fix
    Put ('src\Calc.ps1') 'function Add-Two([int]$x) { $x + 2 }'
    Put ('tests\Calc.Tests.ps1') '. "$PSScriptRoot\..\src\Calc.ps1"; Describe Calc { It "adds two" { Add-Two 1 | Should Be 3 } }'
    Commit 'fix and test'

    # Red by a missing command on base (the Pester form of a build error): the test calls something only head has.
    G checkout -q -b newcmd main
    Put ('src\Calc.ps1') 'function Add-Two([int]$x) { $x + 1 }; function Add-Three([int]$x) { $x + 3 }'
    Put ('tests\Three.Tests.ps1') '. "$PSScriptRoot\..\src\Calc.ps1"; Describe Three { It "adds three" { Add-Three 1 | Should Be 4 } }'
    Commit 'new command and test'

    # Green on base: the test doesn't show the bug.
    G checkout -q -b weak main
    Put ('tests\Weak.Tests.ps1') '. "$PSScriptRoot\..\src\Calc.ps1"; Describe Weak { It "adds something" { Add-Two 1 | Should Be 2 } }'
    Commit 'weak test'

    It 'passes when the test fails on base by assertion and passes on head' {
        $x = Repro 'adds two' 'fix'
        $x.Code | Should Be 0
        $x.Json.pass | Should Be $true
        $x.Json.baseOutcome | Should Be 'assertion'
        $x.Json.headOutcome | Should Be 'passed'
    }
    It 'passes -BaseOnly for a test written before the fix' {
        $x = Repro 'adds two' 'fix' -BaseOnly
        $x.Code | Should Be 0
        $x.Json.headOutcome | Should Be 'not run'
    }
    It 'refuses a test that fails on base because the code it calls is missing' {
        $x = Repro 'adds three' 'newcmd'
        $x.Code | Should Be 1
        $x.Json.baseOutcome | Should Be 'exception'
        $x.Json.reason | Should Match 'not an assertion'
    }
    It 'refuses a test that passes on base' {
        $x = Repro 'adds something' 'weak'
        $x.Code | Should Be 1
        $x.Json.reason | Should Match "doesn't show the bug"
    }
    It 'refuses a test that is not in the diff' {
        $x = Repro 'adds four' 'fix'
        $x.Code | Should Be 2
        $x.Json.reason | Should Match "isn't added or changed"
    }
    It 'refuses an unsupported channel' {
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Test 'x' -Channel e2e -RepoRoot $r
        $LASTEXITCODE | Should Be 2
        ($out -join ' ') | Should Match 'factory VM'
    }
    It 'cleans up its scratch worktrees' {
        (& git -C $r worktree list | Measure-Object).Count | Should Be 1
    }

    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
