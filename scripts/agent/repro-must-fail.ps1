<#
.SYNOPSIS
  Checks a reproduction: the named test is in the diff, fails on the base commit with an assertion failure, and passes
  on head. A build error, a missing type or an exception doesn't count.

.DESCRIPTION
  Base run: a scratch worktree at the merge base of -Base and -Head, with head's changed files under tests/ copied in.
  Head run: a scratch worktree at -Head. Each run goes through the same channel command as test-channel.ps1, with every
  secret removed from the child environment. Channels: core, scenarios, flare-tests, rule-fixtures, pester. An e2e
  reproduction runs on the factory VM as a base batch and a head batch.

  -BaseOnly checks only the base side, for a reproduction written before the fix: the test fails on base with an
  assertion.

  Prints one JSON object (pass, reason, base, head) and exits 0 when the reproduction holds, 1 when it doesn't, 2 when
  the input is refused.

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/repro-must-fail.ps1 -Test IgnoreSurvivesRelint -Channel scenarios
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Test,
    [Parameter(Mandatory)][string]$Channel,
    [string]$Base = 'origin/main',
    [string]$Head = 'HEAD',
    [string]$RepoRoot,
    [string]$WorkRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'housestyle-repro'),
    [string]$LockPath,
    [switch]$BaseOnly
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AgentCommon.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Repro.psm1') -Force

function Stop-Refused([string]$Reason) {
    [pscustomobject]@{ pass = $false; reason = $Reason } | ConvertTo-Json -Compress | Write-Output
    exit 2
}

function Invoke-Git {
    param([string]$Dir, [string[]]$GitArgs)
    $r = Invoke-ScrubbedProcess -FilePath 'git' -ArgumentList (@('-C', $Dir, '-c', 'core.hooksPath=NUL') + $GitArgs) -WorkingDirectory $Dir
    if ($r.ExitCode) { throw "git $($GitArgs -join ' ') failed: $(($r.Output | Select-Object -First 3) -join ' ')" }
    return $r.Output
}

if (-not (Test-ReproChannel $Channel)) {
    Stop-Refused "The $Channel channel isn't supported here. Use core, scenarios, flare-tests, rule-fixtures or pester; an e2e reproduction runs on the factory VM."
}
if ($Channel -eq 'pester') {
    if ($Test -notmatch "^[\w .,()'\-]{1,200}$") { Stop-Refused "A Pester test name uses letters, digits, spaces and . , ( ) ' - only." }
} elseif ($Test -notmatch '^[\w.]{1,300}$') {
    Stop-Refused "A .NET test name uses letters, digits, underscores and dots only."
}
foreach ($ref in @($Base, $Head)) {
    if ($ref -notmatch '^[\w./~^\-]{1,100}$' -or $ref.StartsWith('-')) { Stop-Refused "'$ref' isn't a valid git ref." }
}

if (-not $RepoRoot) { $RepoRoot = Join-Path $PSScriptRoot '..\..' }
$RepoRoot = (Resolve-Path $RepoRoot).Path
try {
    $headSha = @(Invoke-Git $RepoRoot @('rev-parse', '--verify', "$Head^{commit}"))[0].Trim()
    $baseSha = @(Invoke-Git $RepoRoot @('merge-base', $Base, $headSha))[0].Trim()
    $diff = Invoke-Git $RepoRoot @('diff', '--no-ext-diff', '--unified=0', $baseSha, $headSha, '--', 'tests')
    $changed = Invoke-Git $RepoRoot @('diff', '--no-ext-diff', '--name-only', '--diff-filter=d', $baseSha, $headSha, '--', 'tests')
} catch { Stop-Refused $_.Exception.Message }

$short = Get-TestShortName $Test $Channel
$where = @(Find-TestInDiff -DiffLines $diff -Name $short)
if (-not $where.Count) { Stop-Refused "$Test isn't added or changed under tests/ between base and head." }
$pesterPath = $null
if ($Channel -eq 'pester') { $pesterPath = $where[0] }

if (-not $LockPath) { $LockPath = Get-DefaultLockPath }
try { $lock = Enter-RunLock $LockPath } catch { Stop-Refused $_.Exception.Message }

$run = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6)
$results = @{}
$trees = @()
try {
    $sides = if ($BaseOnly) { @('base') } else { @('base', 'head') }
    foreach ($side in $sides) {
        $tree = Join-Path $WorkRoot "$run-$side"
        $out = Join-Path $WorkRoot "$run-$side-results"
        New-Item -ItemType Directory -Force $out | Out-Null
        $sha = if ($side -eq 'base') { $baseSha } else { $headSha }
        Invoke-Git $RepoRoot @('worktree', 'add', '--detach', $tree, $sha) | Out-Null
        $trees += $tree
        if ($side -eq 'base' -and @($changed).Count) {
            Invoke-Git $tree (@('checkout', $headSha, '--') + @($changed)) | Out-Null
        }
        $cmd = Get-ChannelCommand -Channel $Channel -RepoRoot $tree -Filter $(if ($Channel -eq 'pester') { $null } else { $Test }) -Path $pesterPath -ResultsDir $out
        $r = Invoke-ScrubbedProcess -FilePath $cmd.FilePath -ArgumentList $cmd.ArgumentList -WorkingDirectory $cmd.WorkingDirectory
        $file = if ($Channel -eq 'pester') { Join-Path $out 'pester.json' } else { @(Get-ChildItem $out -Filter *.trx -Recurse | Select-Object -First 1 -ExpandProperty FullName) | Select-Object -First 1 }
        $results[$side] = Get-RunOutcome -Channel $Channel -Test $Test -ExitCode $r.ExitCode -Output $r.Output -ResultFile $file
    }
} finally {
    foreach ($t in $trees) { try { Invoke-Git $RepoRoot @('worktree', 'remove', '--force', $t) | Out-Null } catch { } }
    $lock.Dispose()
}

$headResult = if ($BaseOnly) { [pscustomobject]@{ Outcome = 'passed'; Message = '' } } else { $results['head'] }
$v = Get-ReproVerdict -Base $results['base'] -Head $headResult -Test $Test
if ($BaseOnly -and $v.Pass) { $v.Reason = "$Test fails on base with an assertion." }
[pscustomobject]@{
    pass = $v.Pass; reason = $v.Reason; channel = $Channel; test = $Test; base = $baseSha; head = $headSha
    baseOutcome = $results['base'].Outcome; headOutcome = $(if ($BaseOnly) { 'not run' } else { $results['head'].Outcome })
} | ConvertTo-Json -Compress | Write-Output
if ($v.Pass) { exit 0 } else { exit 1 }
