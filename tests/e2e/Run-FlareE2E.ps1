<#
.SYNOPSIS Runs the end-to-end scenarios in real Flare and writes results.json; the exit code is the number of failures.
.DESCRIPTION Each scenario (scenarios\*.ps1) gets a fresh Flare on a fresh copy of the fixture project, with the
plugin's settings isolated in the run folder. A failure saves Flare's state (JSON) and a screenshot next to results.json.
Closes any running Flare: don't run it while you have unsaved work in Flare.
A watchdog ends a scenario that runs past -ScenarioMinutes: it saves a screenshot and the open windows' titles, then
kills Flare, which also frees a UI Automation call stuck on an unresponsive window.
-QueueSeconds is how long the job waited before this run (-1 when unknown). Each results.json row also records
installSeconds, flareStartSeconds, resolution and windowState (see README.md).
-StopOnFail ends the run after the first scenario that fails; results.json is still written, with the rows so far.
.EXAMPLE powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -Install
.EXAMPLE powershell.exe -File tests\e2e\Run-FlareE2E.ps1 -Scenario reopen-issues
#>
param(
    [string[]]$Scenario,                      # names without .ps1; default all
    [switch]$Install,                         # build Release and install into Flare first
    [switch]$NoAi,                            # skip scenarios that call the AI (no key, or to save cost)
    [string]$Fixture,                         # default: corpus\DigitalBanking in the repo
    [string]$RepoRoot,                        # the checkout to build and test, when this harness runs from a copy (the factory)
    [string]$OutDir,
    [string[]]$ScenarioDir,                   # more folders of scenarios to run after these (the factory's holdout set)
    [switch]$StopOnFail,                      # stop after the first scenario that fails; results.json is still written
    [int]$InstallSeconds = -1,                # install time from an earlier run in the same job (a run without -Install); -1 means unknown
    [int]$QueueSeconds = -1,                  # seconds the job waited before this run; -1 means unknown
    [int]$ScenarioMinutes = 12                # a scenario still running after this is hung (its own waits add up to ~11)
)
$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Desktop") { throw "Run with Windows PowerShell 5.1 (powershell.exe): UI Automation needs .NET Framework." }
Import-Module (Join-Path $PSScriptRoot "FlareE2E.psm1") -Force
if (-not $OutDir) { $OutDir = Join-Path (Get-E2ERoot) ("results-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
$repo = if ($RepoRoot) { Resolve-Path $RepoRoot } else { Resolve-Path (Join-Path $PSScriptRoot "..\..") }
if (-not $Fixture) { $Fixture = Join-Path $repo "corpus\DigitalBanking" }
$Fixture = Resolve-Path $Fixture

$installSeconds = if ($InstallSeconds -ge 0) { $InstallSeconds } else { $null }
if ($Install) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Get-Process Flare -ErrorAction SilentlyContinue | Stop-Process -Force -PassThru | Wait-Process -Timeout 30
    & (Join-Path $repo "scripts\install-plugin.ps1")
    if ($LASTEXITCODE) { throw "install-plugin.ps1 failed" }
    $installSeconds = [int]$sw.Elapsed.TotalSeconds
}
# The size this 5.1 process sees: it is DPI-unaware, so a scaled desktop reports the scaled size.
$resolution = try { Add-Type -AssemblyName System.Windows.Forms; $b = [System.Windows.Forms.SystemInformation]::VirtualScreen; "$($b.Width)x$($b.Height)" } catch { $null }

$files = @(Get-ChildItem (Join-Path $repo "tests\e2e\scenarios") -Filter *.ps1) + @(foreach ($d in $ScenarioDir) { if ($d) { Get-ChildItem $d -Filter *.ps1 } }) |
    Where-Object { -not $Scenario -or $Scenario -contains $_.BaseName }
if ($NoAi) { $files = $files | Where-Object { $_.BaseName -notlike "ai-*" } }
if (-not @($files).Count) { throw "No scenarios to run (check -Scenario names)." }   # an empty run must not look like a pass
$out = $OutDir
New-Item -ItemType Directory -Force $out | Out-Null
$env:HOUSESTYLE_E2E_OUT = $out   # Save-E2EScreenshot puts its screenshots here
$results = @()
# Runs in its own process, so it fires even while this one is blocked inside a UI Automation call. It uses no UI
# Automation for the same reason: process window titles only.
$watchdog = {
    param($parent, $seconds, $png, $txt)
    # Waits on this run, not just the clock: if the run dies without its finally, the watchdog leaves a later Flare alone.
    Wait-Process -Id $parent -Timeout $seconds -ErrorAction SilentlyContinue
    if (-not (Get-Process -Id $parent -ErrorAction SilentlyContinue)) { return }
    Get-Process | Where-Object MainWindowTitle | ForEach-Object { "$($_.ProcessName) $($_.Id) responding=$($_.Responding) '$($_.MainWindowTitle)'" } | Set-Content $txt
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
        $bmp.Save($png)
    } catch { }
    Get-Process Flare -ErrorAction SilentlyContinue | Stop-Process -Force
}
$q = { param($p) "'" + ($p -replace "'", "''") + "'" }
# One try of one scenario. A failure is "vm" only when Flare itself never came up (no main window, or the licensing
# wizard stuck) or hung before the plugin reported ready: the machine is the likely cause, so it earns one retry.
# Everything else is "test" and is never retried: Flare up but the plugin never ready, a scenario that fails before
# starting Flare, and anything after the plugin is ready, hangs included (a fix can deadlock Flare).
function Invoke-Once($f, $tag) {
    $start = Get-Date
    $hung = Join-Path $out "$tag-hung.txt"
    $cmd = "& {$watchdog} $PID $($ScenarioMinutes * 60) $(& $q (Join-Path $out "$tag-hung.png")) $(& $q $hung)"
    $dog = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList "-NoProfile", "-EncodedCommand", ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd)))
    $r = [ordered]@{ passed = $false; kind = $null; seconds = 0; error = $null; version = $null; artifacts = @(); flareStartSeconds = $null; windowState = $null }
    (Get-Module FlareE2E).Invoke({ $script:Run = $null })   # no state from the scenario before
    try {
        & $f.FullName -Fixture $Fixture | Out-Host   # to the console, not into this function's result
        $r.passed = $true
    } catch {
        $r.error = $_.Exception.Message
        # Where Flare got to: never launched by this scenario, launched, or the plugin ready.
        $stage = (Get-Module FlareE2E).Invoke({ if (-not $script:Run) { "none" } elseif ($script:Run.PSObject.Properties["PluginReady"]) { "ready" } else { "launched" } })
        $flareUp = $stage -eq "launched" -and -not (Test-Path $hung) -and [bool](Get-Process Flare -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -and $_.MainWindowTitle -ne "Licensing Wizard" })
        $r.kind = if ($stage -eq "launched" -and -not $flareUp) { "vm" } else { "test" }
        if (Test-Path $hung) { $r.error = "Hung for $ScenarioMinutes min, so the watchdog killed Flare: $($r.error)"; $r.artifacts += "$tag-hung.txt", "$tag-hung.png" }
        try { Get-E2EState | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out "$tag-state.json"); $r.artifacts += "$tag-state.json" } catch { }
        try {
            Add-Type -AssemblyName System.Windows.Forms, System.Drawing
            $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
            $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
            [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
            $bmp.Save((Join-Path $out "$tag.png")); $r.artifacts += "$tag.png"
        } catch { }
    } finally {
        Stop-Process -Id $dog.Id -Force -ErrorAction SilentlyContinue
        try { $r.version = (Get-Module FlareE2E).Invoke({ $script:Run.Version }) } catch { }
        foreach ($p in "FlareStartSeconds", "WindowState") {   # StrictMode: only present once Flare was launched
            try { $r[($p.Substring(0, 1).ToLower() + $p.Substring(1))] = (Get-Module FlareE2E).Invoke({ param($n) if ($script:Run -and $script:Run.PSObject.Properties[$n]) { $script:Run.$n } }, $p) } catch { }
        }
        Stop-FlareE2E
        $r.seconds = [int]((Get-Date) - $start).TotalSeconds
    }
    $r
}

# One line per scenario run, kept across runs, so the sequence of tries and why each failed is readable at a glance.
$history = Join-Path (Get-E2ERoot) "history.log"
$head = try { "$(git -C $repo rev-parse --abbrev-ref HEAD 2>$null)@$(git -C $repo rev-parse --short HEAD 2>$null)" } catch { "" }

foreach ($f in $files) {
    Write-Host "== $($f.BaseName)"
    # Mistakes a static check finds cost no Flare launch.
    $problems = try { @(& (Join-Path $PSScriptRoot "Test-E2EScenario.ps1") -Path $f.FullName -Fixture $Fixture -RepoRoot $repo) } catch { @("the preflight check itself failed: $($_.Exception.Message)") }
    if ($problems.Count) {
        $r = [ordered]@{ passed = $false; kind = "preflight"; seconds = 0; error = ($problems -join " | "); version = $null; artifacts = @(); flareStartSeconds = $null; windowState = $null }
    } else {
        $r = Invoke-Once $f $f.BaseName
    }
    if (-not $r.passed -and $r.kind -eq "vm") {
        Write-Host "   VM failure, retrying once: $($r.error)" -ForegroundColor Yellow
        $first = $r
        $r = Invoke-Once $f "$($f.BaseName)-retry"
        $r.firstTry = [ordered]@{ kind = $first.kind; error = $first.error; artifacts = $first.artifacts }
        $r.artifacts = @($first.artifacts) + @($r.artifacts)
        $r.seconds += $first.seconds
        if ($r.passed) { Write-Host "::warning::$($f.BaseName) passed on a retry after a VM failure: $($first.error)" }
    }
    if ($r.passed) { Write-Host "   PASS$(if ($r.Contains('firstTry')) { ' (on retry)' })" -ForegroundColor Green }
    else { Write-Host "   $($r.kind.ToUpper()) FAILURE: $($r.error)" -ForegroundColor Red; Write-Host "::error::$($f.BaseName) ($($r.kind) failure): $($r.error)" }
    $results += New-E2EResultRow $f.BaseName $r $installSeconds $QueueSeconds $resolution
    $status = if ($r.passed) { "PASS" } else { "FAIL-$($r.kind)" }
    try { Add-Content $history ("{0}`t{1}`t{2}`t{3}s`t{4}`t{5}`t{6}" -f (Get-Date -Format s), $f.BaseName, $status, $r.seconds, $head, ($r.error -replace '\s+', ' '), $out) } catch { }
    if ($StopOnFail -and -not $r.passed) { Write-Host "Stopping after the first failure (-StopOnFail)."; break }
}
$results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out "results.json")
$failed = @($results | Where-Object { -not $_.passed })
$vm = @($failed | Where-Object kind -eq "vm").Count
$retried = @($results | Where-Object { $_.passed -and $_.firstTry }).Count
Write-Host "$($results.Count - $failed.Count) passed$(if ($retried) { " ($retried on retry)" }), $($failed.Count) failed ($($failed.Count - $vm) test, $vm VM). $out\results.json"
exit $failed.Count
