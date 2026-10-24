<#
.SYNOPSIS
  Runs tests on one testing channel, with every secret removed from the child environment, one run at a time.

.DESCRIPTION
  Channels, cheapest first (the testing skill says which to pick):
    rule-fixtures, bench, core, scenarios, flare-tests, pester, vitest, conformance (not built yet), e2e, and full
    (the whole Release suite before a push).
  -Filter narrows the .NET channels (FullyQualifiedName~<Filter>), Pester (Describe name) and vitest (-t).
  -Path is the Pester folder or file under tests/. -Scenario is one e2e scenario; AI scenarios never run here.
  -ResultsDir also writes a result file there (results.trx, or pester.json).
  Exits with the test run's exit code.

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/test-channel.ps1 -Channel core -Filter StaleFindingTests
#>
[CmdletBinding()]
param(
    [string]$Channel,
    [string]$Filter,
    [string]$Path,
    [string]$Scenario,
    [string]$ResultsDir,
    [string]$RepoRoot,
    [string]$LockPath,
    [switch]$List,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AgentCommon.psm1') -Force
# The scripts' own repo root, which also holds the e2e harness. The factory runs a copy of these scripts from the base
# commit outside the checkout and names the checkout in HOUSESTYLE_TEST_REPO, so the tests run there and the harness
# and secret scrubbing come from the copy.
$harnessRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $RepoRoot) { $RepoRoot = if ($env:HOUSESTYLE_TEST_REPO) { $env:HOUSESTYLE_TEST_REPO } else { $harnessRoot } }

if ($List) {
    Get-TestChannel | Format-Table Order, Name, Where, Proves -AutoSize | Out-String -Width 160 | Write-Host
    exit 0
}
if (-not $Channel) { Write-Host "Name a channel with -Channel. Run with -List to see them."; exit 2 }

try {
    $cmd = Get-ChannelCommand -Channel $Channel -RepoRoot $RepoRoot -Filter $Filter -Path $Path -Scenario $Scenario -ResultsDir $ResultsDir -HarnessRoot $harnessRoot
} catch {
    Write-Host $_.Exception.Message
    exit 2
}

$line = "$($cmd.FilePath) $(ConvertTo-ArgumentString $cmd.ArgumentList)"
if ($DryRun) { Write-Output $line; exit 0 }

if (-not $LockPath) { $LockPath = Get-DefaultLockPath }
try { $lock = Enter-RunLock $LockPath } catch { Write-Host $_.Exception.Message; exit 3 }
try {
    if ($ResultsDir -and -not (Test-Path $ResultsDir)) { New-Item -ItemType Directory -Force $ResultsDir | Out-Null }
    Write-Host "Channel $Channel`: $line"
    $r = Invoke-ScrubbedProcess -FilePath $cmd.FilePath -ArgumentList $cmd.ArgumentList -WorkingDirectory $cmd.WorkingDirectory -Stream
    exit $r.ExitCode
} finally {
    $lock.Dispose()
}
