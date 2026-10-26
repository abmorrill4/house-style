<#
.SYNOPSIS How the build factory is doing, from its ledger: cost and minutes per issue, each model's share and
first-pass rate, retries per stage, and what went back to the owner.
.EXAMPLE scripts\factory\Get-FactoryStats.ps1 -Last 20
#>
param([int]$Last = 20, [string]$Ledger = "E:\HouseStyle\planning\factory\ledger.jsonl")
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "Controls.psm1")   # Get-Median
$rows = @(Get-Content $Ledger -ErrorAction SilentlyContinue | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } })
$issues = @($rows | Where-Object { $_.kind -eq "issue" }) | Select-Object -Last $Last
if (-not $issues.Count) { Write-Host "No finished issues in $Ledger yet."; return }
$ids = @($issues | ForEach-Object { $_.issue })
$calls = @($rows | Where-Object { $_.kind -eq "call" -and $ids -contains $_.issue })

function P75($v) { $s = @($v | Sort-Object); if ($s.Count) { $s[[Math]::Max(0, [int][Math]::Ceiling($s.Count * 0.75) - 1)] } }
Write-Host "Last $($issues.Count) issues: $(@($issues | Where-Object outcome -eq 'merged').Count) merged, $(@($issues | Where-Object outcome -ne 'merged').Count) back to the owner"
foreach ($size in "fix", "feature") {
    $g = @($issues | Where-Object size -eq $size)
    if ($g.Count) { Write-Host ("  {0,-8} cost median `${1:N2}, p75 `${2:N2}; minutes median {3}, p75 {4}" -f $size, (Get-Median @($g.cost_usd)), (P75 @($g.cost_usd)), (Get-Median @($g.minutes)), (P75 @($g.minutes))) }
}
$tokens = { param($c) [double]$c.input_tokens + [double]$c.output_tokens + [double]$c.cache_read_tokens + [double]$c.cache_write_tokens }
$total = [double](@($calls | ForEach-Object { & $tokens $_ }) | Measure-Object -Sum).Sum
Write-Host "Models:"
foreach ($m in @($calls | Group-Object model)) {
    $t = [double](@($m.Group | ForEach-Object { & $tokens $_ }) | Measure-Object -Sum).Sum
    $builds = @($m.Group | Where-Object { $_.step -match '^build-\d+-0$' })
    $first = if ($builds.Count) { " first-pass builds {0:P0} of {1}" -f (@($builds | Where-Object result -eq 'ok').Count / $builds.Count), $builds.Count } else { "" }
    Write-Host ("  {0,-7} {1:P0} of tokens, `${2:N2}.{3}" -f $m.Name, $(if ($total) { $t / $total } else { 0 }), [double](@($m.Group.cost_usd) | Measure-Object -Sum).Sum, $first)
}
Write-Host "Retries: $(@($calls | Where-Object { $_.step -match '-again$|^build-\d+-[1-9]$' }).Count) of $($calls.Count) calls"
$back = @($issues | Where-Object outcome -ne 'merged' | Group-Object outcome)
if ($back.Count) { Write-Host "Back to the owner: $(($back | ForEach-Object { "$($_.Name) $($_.Count)" }) -join ', ')" }
