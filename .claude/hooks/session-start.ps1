# SessionStart hook: a few lines of orientation for a new chat in this repo (stdout goes into Claude's context).
# Fast and offline: no fetch, no network. Never fails the session.
$ErrorActionPreference = "SilentlyContinue"
$top = (git rev-parse --show-toplevel 2>$null)
if (-not $top) { exit 0 }
$common = (git rev-parse --path-format=absolute --git-common-dir 2>$null)
$main = if ($common) { Split-Path $common -Parent } else { $top }
$branch = (git branch --show-current 2>$null)
$behind = (git rev-list --count "HEAD..origin/main" 2>$null)
$dirty = @(git status --porcelain 2>$null).Count

$lines = @("House Style session: $top on '$branch'" + $(if ($behind -and $behind -ne "0") { ", $behind commit(s) behind origin/main (as of the last fetch)" } else { "" }) + $(if ($dirty) { ", $dirty uncommitted change(s)" } else { "" }) + ".")
if ((Resolve-Path $top).Path -eq (Resolve-Path $main).Path) {
    $lines += "This is the shared main checkout: start task work in a worktree from origin/main (see CLAUDE.md)."
}
$handoff = "G:\Obsidian\Log Pose\Handoffs"   # the Log Pose vault, outside the repo
$notes = @(Get-ChildItem $handoff -Filter *.md -File | Sort-Object LastWriteTime -Descending | Select-Object -First 5)
if ($notes.Count) {
    $lines += "Handoff notes (newest first; read the matching one when the user says 'continue <topic>'):"
    foreach ($n in $notes) {
        $title = (Get-Content $n.FullName -TotalCount 1) -replace '^#\s*', ''
        $status = (Select-String -Path $n.FullName -Pattern 'Status:\s*(.+)$' | Select-Object -First 1).Matches.Groups[1].Value
        $lines += "  - $($n.FullName) :: $title" + $(if ($status) { " [$status]" } else { "" })
    }
}
$state = "G:\Obsidian\Log Pose\_System\state.ps1"   # digest of planning cards and what changed since last session
if (Test-Path $state) {
    $digest = @(& $state 2>$null) -join "`n"
    if ($digest) { $lines += $digest }
}
$lines -join "`n"
exit 0
