<#
.SYNOPSIS
  Stop hook for hs-fix and hs-fix-opus. Runs the fast Pester subset (tests/agent, tests/factory) without secrets and
  sends the agent back to work when it fails.

.DESCRIPTION
  Reads the hook's JSON from stdin. Exit 2 with a reason on stderr keeps the agent working. When the agent was already
  sent back once (stop_hook_active), it lets it stop, so a check it can't fix doesn't loop; the workflow's own output
  check (tests green) still refuses the result.
#>
param([string[]]$Paths = @('tests/agent', 'tests/factory'))
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AgentCommon.psm1') -Force
$in = $null
try { $in = [Console]::In.ReadToEnd() | ConvertFrom-Json } catch { }
if ($in -and $in.PSObject.Properties['stop_hook_active'] -and $in.stop_hook_active) { exit 0 }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$failed = @()
foreach ($p in $Paths) {
    if (-not (Test-Path (Join-Path $root $p))) { continue }
    $cmd = Get-ChannelCommand -Channel pester -RepoRoot $root -Path $p
    $r = Invoke-ScrubbedProcess -FilePath $cmd.FilePath -ArgumentList $cmd.ArgumentList -WorkingDirectory $root
    if ($r.ExitCode) { $failed += "$p ($($r.ExitCode) failed)" }
}
if ($failed.Count) {
    [Console]::Error.WriteLine("The fast checks fail: $($failed -join ', '). Fix them before you finish.")
    exit 2
}
exit 0
