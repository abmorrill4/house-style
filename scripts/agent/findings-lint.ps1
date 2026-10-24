<#
.SYNOPSIS
  Checks a review's findings, or a skeptic's verdict, against the formats in the findings and skeptic-protocol skills.

.DESCRIPTION
  Pass the JSON as a file (-Path) or text (-Json). Prints one line per problem and exits 1 when there are any, 0 when
  it's well formed, 2 when there's nothing to read.

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/agent/findings-lint.ps1 -Path findings.json
#>
[CmdletBinding()]
param(
    [string]$Path,
    [string]$Json,
    [ValidateSet('findings', 'verdict')][string]$Kind = 'findings'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Findings.psm1') -Force

if ($Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Write-Output "There's no file at $Path."; exit 2 }
    $Json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
}
if ([string]::IsNullOrWhiteSpace($Json)) { Write-Output "Pass the JSON with -Path or -Json."; exit 2 }

$problems = if ($Kind -eq 'verdict') { @(Test-Verdict $Json) } else { @(Test-Findings $Json) }
if ($problems.Count) { $problems | Write-Output; exit 1 }
exit 0
