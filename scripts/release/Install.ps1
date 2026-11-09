# House Style installer. Run by HouseStyle-Setup.exe, or by hand: powershell -ExecutionPolicy Bypass -File .\Install.ps1
#
# Per-user, self-updating (issue #101):
#   copies the FlareAI folder next to this script to %LOCALAPPDATA%\HouseStyle\current (a REAL folder; the version
#   it replaces is kept in ...\HouseStyle\versions\<version> for rollback), and links MadCap Flare's Plugins\FlareAI
#   to ...\HouseStyle\current (a junction; the only step that needs administrator rights, and only the first time).
#   Later updates swap "current" by renaming folders, without admin rights. Re-running the installer is safe: your
#   settings, style guide and decisions live in %APPDATA%\FlareAI and are never touched.
#
#   Flare must see ONE junction hop only. Never make "current" a junction: Flare 2026 crashes at startup (before any
#   plugin code runs) when Plugins\FlareAI is a junction to a junction. Earlier release candidates did exactly that
#   (current -> versions\<version>); this installer replaces such a "current" with a real folder.
#
#   The Plugins link is machine-wide but points into one Windows user's profile, so this layout is for a computer one
#   person uses. The installer refuses to re-point a link that another install already owns (issue #137).
#
# Options:
#   -Flare <n>   pick a Flare folder number (22 = Flare 2026, 21 = Flare 2025, ...)
#   -Silent      unattended (used by the installer and the tests): no questions, no pause; exit code 0 = installed, 1 = failed
#
# Every install also enables the plugin in Flare for the installing user (HKCU\Software\MadCap Software\Flare\Plugins\
# House Style, Activated = "True"), so the House Style tab appears the first time Flare starts. Tests point this at
# another HKCU key with the HOUSESTYLE_PLUGINS_KEY environment variable.
#
# The one elevated step runs a fixed command built by this script (see New-LinkCommand), never a script on disk, and
# takes nothing from the environment.
param(
    [int]$Flare = 0,
    [switch]$Silent
)
$ErrorActionPreference = "Stop"
$NoPause = $Silent

# The only folder the elevated step may link Flare to is a per-user install: ...\HouseStyle\current holding the plugin.
function Test-AllowedLinkTarget([string]$t) {
    try {
        $full = [IO.Path]::GetFullPath($t).TrimEnd('\')
        $parent = Split-Path $full -Parent
        return (($full -eq $t.TrimEnd('\')) -and ((Split-Path $full -Leaf) -eq "current") -and `
                $parent -and ((Split-Path $parent -Leaf) -eq "HouseStyle") -and `
                (Test-Path -LiteralPath (Join-Path $full "FlareAI.Flare.dll")) -and `
                -not ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint))
    } catch { return $false }
}
# The link itself may only sit at Plugins\FlareAI inside a Flare under Program Files. The Program Files folders come from
# Windows, not from environment variables.
function Test-AllowedLinkDest([string]$d) {
    try {
        $full = [IO.Path]::GetFullPath($d).TrimEnd('\')
        if ($full -ne $d.TrimEnd('\')) { return $false }
        foreach ($pf in @([Environment]::GetFolderPath("ProgramFiles"), [Environment]::GetFolderPath("ProgramFilesX86"))) {
            if (-not $pf) { continue }
            $pat = "^" + [regex]::Escape($pf.TrimEnd('\')) + "\\MadCap Software\\MadCap Flare[^\\]*\\Flare\.app\\Plugins\\FlareAI$"
            if ($full -match $pat) { return $true }
        }
    } catch { }
    return $false
}
function Wait-Exit([int]$code) {
    if (-not $NoPause) { Write-Host ""; Read-Host "Press Enter to close" | Out-Null }
    exit $code
}
function Fail([string]$msg) {
    Write-Host ""
    Write-Host "ERROR: $msg" -ForegroundColor Red
    Wait-Exit 1
}
# Turns the plugin on in Flare for the installing user: Flare reads Activated under its Plugins key at start, so the
# House Style tab appears without File > Options > Plugins > Enable. Returns $false (and says so) when it can't.
function Enable-Plugin {
    $key = $env:HOUSESTYLE_PLUGINS_KEY
    if (-not $key) { $key = "HKCU:\Software\MadCap Software\Flare\Plugins\House Style" }
    if ($key -notmatch '^HKCU:\\') { Write-Host "Ignoring a plugins key outside HKCU." -ForegroundColor Yellow; return $false }
    try {
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
        New-ItemProperty -LiteralPath $key -Name "Activated" -Value "True" -PropertyType String -Force | Out-Null
        return $true
    } catch {
        Write-Host "Could not turn House Style on in Flare's settings: $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
}

function Get-FlareName([int]$n) {
    # MadCap folder number -> marketing name. 22 = 2026, 21 = 2025, 20 = 2024, 19 = 2023 ...
    if ($n -ge 17) { return "Flare " + (2004 + $n) }
    return "Flare (folder $n)"
}

# ---- junction helpers ----------------------------------------------------------------------------------------
# Never use Remove-Item -Recurse on a junction: in Windows PowerShell 5.1 it deletes the files of the folder the
# junction points to. Remove-Link removes only the link (rmdir without /s).
function Test-Link([string]$path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    return ($null -ne $item) -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}
function Get-LinkTarget([string]$path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or -not $item.Target) { return $null }
    return ([string](@($item.Target)[0])).TrimEnd('\')
}
function Remove-Link([string]$path) {
    if (-not (Test-Link $path)) { throw "$path is not a link." }
    cmd.exe /d /c "rmdir `"$path`"" | Out-Null
    if (Test-Path -LiteralPath $path) { throw "Could not remove the link $path." }
}
function New-Junction([string]$link, [string]$target) {
    $out = cmd.exe /d /c "mklink /J `"$link`" `"$target`"" 2>&1
    if (-not (Test-Link $link)) { throw "Could not create the link $link -> ${target}: $out" }
}
# Removes a real folder (refuses links, so it can never follow one into another folder).
function Remove-RealFolder([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    if (Test-Link $path) { Remove-Link $path; return }
    Get-ChildItem -LiteralPath $path -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | ForEach-Object { Remove-Link $_.FullName }
    Remove-Item -LiteralPath $path -Recurse -Force
}
function Get-PluginVersion([string]$dir) {
    $dll = Join-Path $dir "FlareAI.Flare.dll"
    if (-not (Test-Path -LiteralPath $dll)) { return $null }
    $v = (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion
    if (-not $v) { return $null }
    return ($v -split '\+')[0].Trim()
}
# Issue #137: Plugins\FlareAI is machine-wide, so a live link into another HouseStyle root belongs to another install
# (often another Windows user). Returns that folder, or $null when the link is missing, dangling, or points into this
# install's own root (earlier layouts). A target that exists but can't be read counts as live, not dangling.
function Get-ForeignLinkOwner([string]$dest, [string]$target) {
    $item = Get-Item -LiteralPath $dest -Force -ErrorAction SilentlyContinue
    if (-not $item -or -not $item.Target) { return $null }
    $old = [IO.Path]::GetFullPath(([string](@($item.Target)[0]))).TrimEnd('\')
    $root = [IO.Path]::GetFullPath((Split-Path $target -Parent)).TrimEnd('\') + '\'
    if ($old.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    $exists = $false
    try { $exists = [IO.Directory]::Exists($old) } catch { $exists = $true }
    if (-not $exists) { try { [void][IO.Directory]::GetDirectories((Split-Path $old -Parent)) } catch [UnauthorizedAccessException] { $exists = $true } catch { } }
    if ($exists) { return $old }
    return $null
}
function Get-ForeignLinkMessage([string]$owner, [string]$dest) {
    return "Flare already loads House Style from $owner, which belongs to another install. House Style supports one Windows user per computer. To use this install instead, remove the folder $dest first."
}
# Plugins\FlareAI -> $target. Replaces an old link, or an old copy install (migration), never user data.
function Set-PluginsLink([string]$dest, [string]$target) {
    New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
    if (Test-Link $dest) {
        $owner = Get-ForeignLinkOwner $dest $target
        if ($owner) { throw (Get-ForeignLinkMessage $owner $dest) }
        Remove-Link $dest
    }
    elseif (Test-Path -LiteralPath $dest) {
        Write-Host "Replacing the previous install at $dest with a link to your per-user install."
        Remove-RealFolder $dest
    }
    New-Junction $dest $target
}

# The text of the one command the elevated process runs: the link helpers, the two checks, then the link.
function New-LinkCommand([string]$dest, [string]$target) {
    $text = "`$ErrorActionPreference = 'Stop'`n"
    foreach ($name in @("Test-Link", "Remove-Link", "New-Junction", "Remove-RealFolder", "Get-ForeignLinkOwner", "Get-ForeignLinkMessage", "Set-PluginsLink", "Test-AllowedLinkTarget", "Test-AllowedLinkDest")) {
        $text += "function $name {`n" + (Get-Command $name -CommandType Function).Definition + "`n}`n"
    }
    $text += "`$dest = '" + ($dest -replace "'", "''") + "'`n`$target = '" + ($target -replace "'", "''") + "'`n"
    $text += "if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Write-Host 'Administrator rights were not granted.'; exit 1 }`n"
    $text += "if (-not (Test-AllowedLinkTarget `$target) -or -not (Test-AllowedLinkDest `$dest)) { Write-Host 'Refusing to link Flare to that folder.'; exit 1 }`n"
    $text += "try { Set-PluginsLink `$dest `$target; exit 0 } catch { Write-Host `$_.Exception.Message; exit 1 }`n"
    return $text
}

function Find-Flare {
    $found = @()
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $base) { continue }
        $mc = Join-Path $base "MadCap Software"
        if (-not (Test-Path $mc)) { continue }
        Get-ChildItem $mc -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "MadCap Flare*" } | ForEach-Object {
            $num = [int]("0" + ($_.Name -replace '[^\d]', ''))
            $app = Join-Path $_.FullName "Flare.app"
            if (Test-Path (Join-Path $app "Flare.exe")) {
                $found += New-Object psobject -Property @{ Number = $num; Name = (Get-FlareName $num); Folder = $_.FullName; App = $app }
            }
        }
    }
    if ($env:FLARE_ROOT -and (Test-Path (Join-Path $env:FLARE_ROOT "Flare.app\Flare.exe"))) {
        $num = [int]("0" + ((Split-Path $env:FLARE_ROOT -Leaf) -replace '[^\d]', ''))
        $found += New-Object psobject -Property @{ Number = $num; Name = (Get-FlareName $num); Folder = $env:FLARE_ROOT; App = (Join-Path $env:FLARE_ROOT "Flare.app") }
    }
    return @($found | Sort-Object Number -Descending)
}

try {
    $here = $PSScriptRoot
    if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $src = Join-Path $here "FlareAI"
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    $found = Find-Flare
    if (-not $found) { Fail "No MadCap Flare installation found under Program Files\MadCap Software." }
    if ($Flare -gt 0) {
        $target = $found | Where-Object { $_.Number -eq $Flare } | Select-Object -First 1
        if (-not $target) { Fail "Flare folder number $Flare is not installed. Installed: $((($found | ForEach-Object { $_.Number }) -join ', '))." }
    } else {
        $target = $found[0]
    }
    $dest = Join-Path $target.App "Plugins\FlareAI"

    Write-Host "House Style installer" -ForegroundColor Cyan
    if (-not (Test-Path (Join-Path $src "FlareAI.Flare.dll"))) {
        Fail ("Cannot find the FlareAI folder next to Install.ps1 (looked in $src).`n" +
              "Unzip the whole download first, then run Install.ps1 from the unzipped folder, not from inside the zip.")
    }
    $version = Get-PluginVersion $src
    if (-not $version) { Fail "Cannot read the version of $src\FlareAI.Flare.dll." }

    # 1. Unblock every file (Mark of the Web) so Windows lets Flare load the DLLs.
    $n = 0
    Get-ChildItem $here -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        try { Unblock-File -LiteralPath $_.FullName -ErrorAction Stop; $n++ } catch { }
    }
    Write-Host "Unblocked $n files."

    # 2. Which Flare.
    Write-Host ""
    Write-Host "Installed Flare versions:"
    foreach ($f in $found) { Write-Host ("  {0,-12} folder: {1}" -f $f.Name, (Split-Path $f.Folder -Leaf)) }
    if ($Flare -le 0 -and $found.Count -gt 1) { Write-Host "Picking the newest. Use -Flare <n> (for example -Flare $($found[1].Number)) to choose another." }
    Write-Host ""
    Write-Host ("Installing House Style {0} for {1} ({2})" -f $version, $target.Name, $target.Folder) -ForegroundColor Cyan

    # 3. Refuse if Flare is running.
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq "Flare" })
    if ($env:HOUSESTYLE_INSTALL_TEST -eq "1") { $running = @() }   # tests against a fake Flare while a real one is open
    if ($running.Count -gt 0) { Fail "Flare is running. Save your work, close Flare completely, then run the installer again." }

    if ($identity.IsSystem) { Fail "House Style installs for the signed-in Windows user. Run the installer as that user, not as SYSTEM." }
    $underPF = ($target.App -like "$env:ProgramFiles*") -or (${env:ProgramFiles(x86)} -and ($target.App -like "${env:ProgramFiles(x86)}*"))

    # ---- per-user install, linked from Flare (automatic updates) ----------------------------------------
    $root = Join-Path $env:LOCALAPPDATA "HouseStyle"
    $versions = Join-Path $root "versions"
    $current = Join-Path $root "current"
    New-Item -ItemType Directory -Force -Path $versions | Out-Null

    # Leftovers of an interrupted update swap (the updater renames current <-> current.old / current.new).
    $fresh = "$current.new"
    $oldCur = "$current.old"
    if (-not (Test-Path -LiteralPath $current) -and (Test-Path -LiteralPath $oldCur)) { Rename-Item -LiteralPath $oldCur -NewName "current" }
    Remove-RealFolder $fresh
    Remove-RealFolder $oldCur

    # Copy the new files to current.new (a real folder), then swap it in by renaming.
    $tmp = "$current.installing"
    Remove-RealFolder $tmp
    Copy-Item $src -Destination $tmp -Recurse -Force
    Get-ChildItem $tmp -Recurse -File | ForEach-Object { try { Unblock-File -LiteralPath $_.FullName } catch { } }
    if (-not (Test-Path -LiteralPath (Join-Path $tmp "FlareAI.Flare.dll"))) { Fail "Copy finished but FlareAI.Flare.dll is missing from $tmp." }

    if (Test-Link $current) {
        # Migration from earlier release candidates: "current" was a junction to versions\<version>. Flare 2026
        # crashes with that (two junction hops). Drop only the link; the folder it pointed to stays in versions\.
        Write-Host "Repairing the folder layout of an earlier House Style build."
        Remove-Link $current
    } elseif (Test-Path -LiteralPath $current) {
        # The version being replaced is kept in versions\<version> for rollback (re-installing replaces that copy).
        $old = Get-PluginVersion $current
        if (-not $old) { $old = "previous" }
        $keep = Join-Path $versions $old
        Remove-RealFolder $keep
        Rename-Item -LiteralPath $current -NewName "current.old"
        Move-Item -LiteralPath $oldCur -Destination $keep
    }
    Rename-Item -LiteralPath $tmp -NewName "current"
    if (Test-Link $current) { Fail "$current must be a real folder, not a link." }
    # A download from before this install would be older than or equal to what was just installed.
    Remove-RealFolder (Join-Path $root "staged")

    # Flare's Plugins\FlareAI -> current. Needs admin under Program Files, once; re-installs skip it.
    if ((Test-Link $dest) -and ((Get-LinkTarget $dest) -eq $current.TrimEnd('\'))) {
        Write-Host "Flare already loads House Style from $current."
    } elseif ($underPF -and -not $isAdmin) {
        Write-Host "Administrator rights are needed once, to link Flare's Plugins folder to your install. Asking Windows for permission..."
        # The elevated process runs a fixed command built here, not the Install.ps1 on disk, so a script swapped in the
        # unzipped folder after this point is never run with administrator rights.
        $psExe = Join-Path ([Environment]::SystemDirectory) "WindowsPowerShell\v1.0\powershell.exe"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((New-LinkCommand $dest $current)))
        try { $p = Start-Process -FilePath $psExe -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded" -Verb RunAs -Wait -PassThru } catch { Fail "Elevation was cancelled or failed: $($_.Exception.Message)" }
        if ($p.ExitCode -ne 0) { Fail "Linking Flare's Plugins folder failed (exit code $($p.ExitCode))." }
    } else {
        Set-PluginsLink $dest $current
    }
    if (-not (Test-Path (Join-Path $dest "FlareAI.Flare.dll"))) { Fail "Install finished but Flare can't see FlareAI.Flare.dll at $dest." }
    $enabled = Enable-Plugin
    Write-Host ""
    Write-Host "Installed House Style $version to: $current" -ForegroundColor Green
    Write-Host "Flare loads it through: $dest"
    Write-Host "Automatic updates: on. House Style checks GitHub once a day and installs updates when you close Flare (no admin prompt)."

    Write-Host ""
    Write-Host "Next steps:"
    if ($enabled) {
        Write-Host "  1. Start $($target.Name). The House Style tab appears and a short setup opens."
        Write-Host "  2. Answer the setup questions, then practice on the sample project or your own topic."
        Write-Host ""
        Write-Host "If the House Style tab doesn't appear, choose File > Options > Plugins, select House Style, click Enable, and restart Flare."
    } else {
        Write-Host "  1. Start $($target.Name)."
        Write-Host "  2. Choose File > Options > Plugins, select House Style, click Enable, and restart Flare."
        Write-Host "  3. The House Style tab appears and a short setup opens."
    }
    Wait-Exit 0
} catch {
    Fail $_.Exception.Message
}
