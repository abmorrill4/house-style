# Fails unless every FlareAI.* PE file in the plugin folder is signed by the release identity, Valid, and timestamped.
# Windows PowerShell 5.1 or PowerShell 7.
param([Parameter(Mandatory)][string]$Dir, [string]$Subject = "CN=Arthur Morrill, O=Arthur Morrill, L=Glen Allen, S=VA, C=US")
$ErrorActionPreference = "Stop"
$files = @(& (Join-Path $PSScriptRoot "Get-SignFiles.ps1") -Dir $Dir)
if ($files.Count -lt 4) { throw "Expected at least 4 FlareAI.* PE files in $Dir, found $($files.Count)." }
foreach ($f in $files) {
    $s = Get-AuthenticodeSignature -LiteralPath $f
    if ($s.Status -ne "Valid") { throw "$f is $($s.Status): $($s.StatusMessage)" }
    if ($s.SignerCertificate.Subject -ne $Subject) { throw "$f is signed by '$($s.SignerCertificate.Subject)', not '$Subject'." }
    if (-not $s.TimeStamperCertificate) { throw "$f has no timestamp." }
    Write-Host "OK $(Split-Path $f -Leaf)"
}
