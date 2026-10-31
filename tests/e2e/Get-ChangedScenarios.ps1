<#
.SYNOPSIS Names (without .ps1) of the scenarios this branch adds or changes against a base, in file order. A deleted scenario is left out.
.EXAMPLE powershell.exe -File tests\e2e\Get-ChangedScenarios.ps1 -Base origin/main -NoAi
#>
param([string]$Base = "origin/main", [switch]$NoAi, [string]$Root = ".")
$files = @(git -C $Root -c core.quotePath=false diff --name-only --no-renames --diff-filter=AM "$Base...HEAD" -- tests/e2e/scenarios)
if ($LASTEXITCODE) { throw "git diff against $Base failed" }
$names = @($files | Where-Object { $_ -match '^tests/e2e/scenarios/[^/]+\.ps1$' } | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
if ($NoAi) { $names = @($names | Where-Object { $_ -notlike 'ai-*' }) }
$names
