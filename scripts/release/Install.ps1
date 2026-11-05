# House Style installer. Run by double-clicking Install.cmd (or: powershell -ExecutionPolicy Bypass -File .\Install.ps1).
#
# Default (per-user, self-updating, issue #101):
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
# Options:
#   -Flare <n>       pick a Flare folder number (22 = Flare 2026, 21 = Flare 2025, ...)
#   -Silent          for IT deployment: no questions, no pause; exit code 0 = installed, 1 = failed
#   -NoJunction      the old layout: copy into Program Files\...\Plugins\FlareAI for all users. No automatic updates.
#                    Used automatically when installing as SYSTEM (a per-user install would go to the SYSTEM profile).
#   -NoAutoUpdate    write update-policy.json ({"autoUpdate": false}) so the plugin never updates itself
#
# Every install also enables the plugin in Flare for the installing user (HKCU\Software\MadCap Software\Flare\Plugins\
# House Style, Activated = "True"), so the House Style tab appears the first time Flare starts. Tests point this at
# another key with the HOUSESTYLE_PLUGINS_KEY environment variable.
param(
    [int]$Flare = 0,
    [switch]$NoPause,
    [switch]$Silent,
    [switch]$NoJunction,
    [switch]$NoAutoUpdate,
    [switch]$Elevated,
    [string]$LinkTarget = ""   # internal: the elevated step links Plugins\FlareAI to this folder and exits
)
$ErrorActionPreference = "Stop"
if ($Silent) { $NoPause = $true }

# The elevated copy runs with administrator rights, so it takes nothing from settings the signed-in user can change:
# the test-only environment variables (a fake Flare folder, another registry key, skipping the "Flare is running" check)
# are dropped before anything else runs. The non-elevated install keeps using them for tests.
if ($Elevated -or $LinkTarget) {
    foreach ($name in @("FLARE_ROOT", "HOUSESTYLE_PLUGINS_KEY", "HOUSESTYLE_INSTALL_TEST")) {
        Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
    }
}
# The only folder the elevated step may link Flare to is a per-user install: ...\HouseStyle\current holding the plugin.
if ($LinkTarget) {
    $okTarget = $false
    try {
        $full = [IO.Path]::GetFullPath($LinkTarget).TrimEnd('\')
        $parent = Split-Path $full -Parent
        $okTarget = ($full -eq $LinkTarget.TrimEnd('\')) -and ((Split-Path $full -Leaf) -eq "current") -and `
                    $parent -and ((Split-Path $parent -Leaf) -eq "HouseStyle") -and `
                    (Test-Path -LiteralPath (Join-Path $full "FlareAI.Flare.dll")) -and `
                    -not ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
    } catch { $okTarget = $false }
    if (-not $okTarget) {
        Write-Host ""
        Write-Host "ERROR: Refusing to link Flare to '$LinkTarget'. It is not a House Style install folder." -ForegroundColor Red
        exit 1
    }
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
    if ($identity.IsSystem -and -not $env:HOUSESTYLE_PLUGINS_KEY) {
        Write-Host "Installing as SYSTEM: each writer turns House Style on once in Flare (File > Options > Plugins)."
        return $false
    }
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
# Plugins\FlareAI -> $target. Replaces an old link, or an old copy install (migration), never user data.
function Set-PluginsLink([string]$dest, [string]$target) {
    New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
    if (Test-Link $dest) { Remove-Link $dest }
    elseif (Test-Path -LiteralPath $dest) {
        Write-Host "Replacing the previous install at $dest with a link to your per-user install."
        Remove-RealFolder $dest
    }
    New-Junction $dest $target
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

    # Elevated step of a per-user install: only the Plugins link (the files were copied as the signed-in user).
    if ($LinkTarget) {
        if (-not $isAdmin) { Fail "Administrator rights were not granted." }
        Set-PluginsLink $dest $LinkTarget
        Write-Host "Linked $dest -> $LinkTarget"
        exit 0
    }

    Write-Host "House Style installer" -ForegroundColor Cyan
    if (-not (Test-Path (Join-Path $src "FlareAI.Flare.dll"))) {
        Fail ("Cannot find the FlareAI folder next to Install.ps1 (looked in $src).`n" +
              "Unzip the whole download first, then run Install.cmd from the unzipped folder, not from inside the zip.")
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
    if ($running.Count -gt 0) { Fail "Flare is running. Save your work, close Flare completely, then run Install.cmd again." }

    if (-not $NoJunction -and $identity.IsSystem) {
        Write-Host "Installing as SYSTEM: using the all-users layout (-NoJunction). Automatic updates are off for this install."
        $NoJunction = $true
    }
    $underPF = ($target.App -like "$env:ProgramFiles*") -or (${env:ProgramFiles(x86)} -and ($target.App -like "${env:ProgramFiles(x86)}*"))
    $policyJson = '{ "autoUpdate": false }'

    if ($NoJunction) {
        # ---- all-users copy in Program Files (no automatic updates) ----------------------------------------
        if ($underPF -and -not $isAdmin) {
            if ($Elevated) { Fail "Administrator rights were not granted." }
            Write-Host "Administrator rights are needed to write to Program Files. Asking Windows for permission..."
            $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Flare $($target.Number) -Elevated -NoJunction"
            $psExe = Join-Path ([Environment]::SystemDirectory) "WindowsPowerShell\v1.0\powershell.exe"
            if ($NoPause) { $argList += " -NoPause" }
            if ($Silent) { $argList += " -Silent" }
            if ($NoAutoUpdate) { $argList += " -NoAutoUpdate" }
            try { $p = Start-Process -FilePath $psExe -ArgumentList $argList -Verb RunAs -Wait -PassThru } catch { Fail "Elevation was cancelled or failed: $($_.Exception.Message)" }
            # The elevated copy may run as another (admin) account: turn the plugin on for the signed-in user here.
            if ($p.ExitCode -eq 0) { [void](Enable-Plugin) }
            Wait-Exit $p.ExitCode
        }
        $keepPolicy = $null
        $oldPolicy = Join-Path $dest "update-policy.json"
        if (Test-Path -LiteralPath $oldPolicy) { $keepPolicy = Get-Content -LiteralPath $oldPolicy -Raw }
        if (Test-Path -LiteralPath $dest) {
            Write-Host "Removing the previous install at $dest"
            Remove-RealFolder $dest   # a link (per-user install) is unlinked, not emptied
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Copy-Item $src -Destination $dest -Recurse -Force
        if ($NoAutoUpdate) { Set-Content -LiteralPath (Join-Path $dest "update-policy.json") -Value $policyJson -Encoding UTF8 }
        elseif ($keepPolicy) { Set-Content -LiteralPath (Join-Path $dest "update-policy.json") -Value $keepPolicy -Encoding UTF8 }
        Get-ChildItem $dest -Recurse -File | ForEach-Object { try { Unblock-File -LiteralPath $_.FullName } catch { } }
        if (-not (Test-Path (Join-Path $dest "FlareAI.Flare.dll"))) { Fail "Copy finished but FlareAI.Flare.dll is missing from $dest." }
        $enabled = Enable-Plugin
        Write-Host ""
        Write-Host "Installed to: $dest (all users; no automatic updates)" -ForegroundColor Green
    } else {
        # ---- per-user install, linked from Flare (automatic updates) ----------------------------------------
        $root = Join-Path $env:LOCALAPPDATA "HouseStyle"
        $versions = Join-Path $root "versions"
        $current = Join-Path $root "current"
        New-Item -ItemType Directory -Force -Path $versions | Out-Null

        # Keep an IT policy file from the install being replaced.
        $keepPolicy = $null
        foreach ($p in @((Join-Path $current "update-policy.json"), (Join-Path $dest "update-policy.json"))) {
            if (-not $keepPolicy -and (Test-Path -LiteralPath $p)) { $keepPolicy = Get-Content -LiteralPath $p -Raw }
        }

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
        if ($NoAutoUpdate) { Set-Content -LiteralPath (Join-Path $tmp "update-policy.json") -Value $policyJson -Encoding UTF8 }
        elseif ($keepPolicy) { Set-Content -LiteralPath (Join-Path $tmp "update-policy.json") -Value $keepPolicy -Encoding UTF8 }
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
            $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Flare $($target.Number) -Elevated -NoPause -LinkTarget `"$current`""
            $psExe = Join-Path ([Environment]::SystemDirectory) "WindowsPowerShell\v1.0\powershell.exe"
            try { $p = Start-Process -FilePath $psExe -ArgumentList $argList -Verb RunAs -Wait -PassThru } catch { Fail "Elevation was cancelled or failed: $($_.Exception.Message)" }
            if ($p.ExitCode -ne 0) { Fail "Linking Flare's Plugins folder failed (exit code $($p.ExitCode))." }
        } else {
            Set-PluginsLink $dest $current
        }
        if (-not (Test-Path (Join-Path $dest "FlareAI.Flare.dll"))) { Fail "Install finished but Flare can't see FlareAI.Flare.dll at $dest." }
        $enabled = Enable-Plugin
        Write-Host ""
        Write-Host "Installed House Style $version to: $current" -ForegroundColor Green
        Write-Host "Flare loads it through: $dest"
        if ($NoAutoUpdate -or $keepPolicy) { Write-Host "Automatic updates: set by update-policy.json." }
        else { Write-Host "Automatic updates: on. House Style checks GitHub once a day and installs updates when you close Flare (no admin prompt)." }
    }

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
