<#
.SYNOPSIS
  PreToolUse hook for the hs-* agents that have Bash or PowerShell. Deny by default: each agent role gets its own allow
  list (AgentCommon.psm1, Test-AgentCommand), and everything else is blocked, including gh, web requests and bare
  test runs.

.DESCRIPTION
  Reads the hook's JSON from stdin. Exit 0 allows the command; exit 2 blocks it and tells the agent why (stderr).
  The agent scripts run only from the repository root, and not while scripts/agent differs from origin/main, so an
  agent can't change a check and then run it. Any error blocks.

.PARAMETER Role
  worker (hs-repro, hs-fix, hs-rules), tester (hs-review-correctness, hs-skeptic) or reviewer (the other lenses).
#>
param([string]$Role = 'reviewer')
$ErrorActionPreference = 'Stop'
$why = $null
try {
    Import-Module (Join-Path $PSScriptRoot 'AgentCommon.psm1') -Force
    $in = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $command = [string]$in.tool_input.command
    $why = Test-AgentCommand -Command $command -Role $Role
    if (-not $why -and (Test-InvokesAgentScript $command)) {
        $root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\', '/')
        $cwd = [string]$in.cwd
        $here = if ($cwd -and (Test-Path -LiteralPath $cwd)) { (Resolve-Path -LiteralPath $cwd).Path.TrimEnd('\', '/') } else { '' }
        if ($here -ne $root) {
            $why = "Run the agent scripts from the repository root."
        } else {
            $ErrorActionPreference = 'Continue'   # git may write to stderr; its exit code is what counts
            $ref = 'origin/main'
            & git -C $root rev-parse --verify --quiet $ref *> $null
            if ($LASTEXITCODE) { $ref = 'HEAD' }
            & git -C $root diff --quiet $ref -- scripts/agent *> $null
            $changed = $LASTEXITCODE
            $untracked = & git -C $root ls-files --others --exclude-standard -- scripts/agent
            if ($changed -or $untracked) { $why = "scripts/agent has changes, so its checks can't be trusted in this run. Undo them first." }
        }
    }
} catch {
    $why = "The command couldn't be checked, so it was blocked."
}
if ($why) { [Console]::Error.WriteLine($why); exit 2 }
exit 0
