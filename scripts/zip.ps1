# Zips dist\stage into dist\HouseStyle-<version>.zip and writes dist\checksums.txt (sha256sum format).
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$zip = Join-Path $repo "dist\HouseStyle-$Version.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
# Build the zip by hand: Compress-Archive in Windows PowerShell 5.1 writes backslash entry names, which some unzip tools mishandle.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$stageRoot = (Resolve-Path "$repo\dist\stage").Path.TrimEnd('\')
$za = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stageRoot -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stageRoot.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $za.Dispose() }
# Same format as the release's checksums.txt (sha256sum style), so a local test server can serve both.
$sha = [Security.Cryptography.SHA256]::Create()
$fs = [IO.File]::OpenRead($zip)
try { $hash = ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLowerInvariant() } finally { $fs.Dispose(); $sha.Dispose() }
Set-Content -Path (Join-Path $repo "dist\checksums.txt") -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding Ascii
Write-Host "Packaged: $zip"
