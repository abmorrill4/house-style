# Builds Release into dist\stage, then zips it into dist\HouseStyle-<version>.zip with checksums.txt (zip.ps1).
# -StageOnly stops after dist\stage: the release signs the stage first, then runs zip.ps1.
param([string]$Version = "0.1.0", [switch]$StageOnly)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

dotnet build src\FlareAI.Flare\FlareAI.Flare.csproj -c Release -p:Version=$Version
$stage = Join-Path $repo "dist\stage\FlareAI"
Remove-Item "$repo\dist\stage" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Get-ChildItem "src\FlareAI.Flare\bin\Release\net48" -File | Where-Object { $_.Name -notlike "B3.*" -and $_.Extension -in ".dll", ".pdb", ".json" } | Copy-Item -Destination $stage
Copy-Item "styleguide" -Destination "$stage\StyleGuide" -Recurse -Force
dotnet build tools\FlareAI.Mcp\FlareAI.Mcp.csproj -c Release -p:Version=$Version
Copy-Item "tools\FlareAI.Mcp\bin\Release\net48\FlareAI.Mcp.exe", "tools\FlareAI.Mcp\bin\Release\net48\FlareAI.Mcp.exe.config" -Destination $stage -ErrorAction SilentlyContinue
# The updater (issue #101): waits for Flare to close, then swaps %LOCALAPPDATA%\HouseStyle\current to a staged update.
dotnet build tools\FlareAI.Updater\FlareAI.Updater.csproj -c Release -p:Version=$Version
Copy-Item "tools\FlareAI.Updater\bin\Release\net48\FlareAI.Updater.exe", "tools\FlareAI.Updater\bin\Release\net48\FlareAI.Updater.exe.config" -Destination $stage
# The hand-written notes plus each release's changes/ files (scripts\release\Build-WhatsNew.ps1).
& (Join-Path $PSScriptRoot "release\Build-WhatsNew.ps1") -Version $Version -Out (Join-Path $stage "whats-new.md")
if (Test-Path "src\FlareAI.Flare\Icons") { Copy-Item "src\FlareAI.Flare\Icons" -Destination $stage -Recurse }
# The practice project for the guided first lint (copied to Documents\House Style Practice on first use).
Copy-Item "src\FlareAI.Flare\Samples" -Destination $stage -Recurse
# Zip root = Install.ps1, README-INSTALL.md, FlareAI\ (no wrapper folder)
Copy-Item "docs\README-INSTALL.md" -Destination "$repo\dist\stage\README-INSTALL.md"
Copy-Item "scripts\release\Install.ps1" -Destination "$repo\dist\stage"
Copy-Item "LICENSE", "NOTICE.md" -Destination $stage

if ($StageOnly) { Write-Host "Staged: $repo\dist\stage"; return }
& (Join-Path $PSScriptRoot "zip.ps1") -Version $Version
