<#
.SYNOPSIS
  PreToolUse hook for the hs-* agents that have Bash or PowerShell. Allows only the scripts in scripts/agent, read-only
  git and read-only listing; blocks everything else, including gh, web requests and bare test runs.

.DESCRIPTION
  Reads the hook's JSON from stdin. Exit 0 allows the command; exit 2 blocks it and tells the agent why (stderr).
  It also blocks running scripts/agent while that folder differs from origin/main, so an agent can't change a check
  and then run it.
#>
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'AgentCommon.psm1') -Force
    $in = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $command = [string]$in.tool_input.command
    $why = Test-AgentCommand $command
    if (-not $why -and (Test-InvokesAgentScript $command)) {
        $root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
        $ref = 'origin/main'
        & git -C $root rev-parse --verify --quiet $ref *> $null
        if ($LASTEXITCODE) { $ref = 'HEAD' }
        & git -C $root diff --quiet $ref -- scripts/agent *> $null
        $changed = $LASTEXITCODE
        $untracked = & git -C $root ls-files --others --exclude-standard -- scripts/agent
        if ($changed -or $untracked) { $why = "scripts/agent has changes, so its checks can't be trusted in this run. Undo them first." }
    }
} catch {
    $why = "The command couldn't be checked, so it was blocked."
}
if ($why) { [Console]::Error.WriteLine($why); exit 2 }
exit 0
