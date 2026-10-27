# Drives real Flare for end-to-end checks. Flare's own UI (trial dialog, ribbon) through UI Automation (FlaUI); the
# issues pane and House Style's dialogs through the plugin's E2E hook (src/FlareAI.Flare/E2EHook.cs), which Flare turns
# on when it starts with HOUSESTYLE_E2E_DIR set. Windows PowerShell 5.1.
Set-StrictMode -Version 2
$ErrorActionPreference = "Stop"

# FlaUI, restored from NuGet by flaui\FlaUI.Deps.csproj on first use. Unlike the UI Automation client in .NET
# Framework, its calls time out, so an unresponsive Flare fails a call instead of blocking the run.
$flaui = Join-Path $PSScriptRoot "flaui\bin"
$deps = Join-Path $PSScriptRoot "flaui\FlaUI.Deps.csproj"
$dll = Join-Path $flaui "FlaUI.UIA3.dll"
if (-not (Test-Path $dll) -or (Get-Item $deps).LastWriteTime -gt (Get-Item $dll).LastWriteTime) {   # missing, or the version changed
    dotnet build $deps -c Release -o $flaui -nologo -v q | Out-Host
    if ($LASTEXITCODE) { throw "Couldn't restore FlaUI (dotnet build tests\e2e\flaui\FlaUI.Deps.csproj)." }
}
foreach ($d in "Interop.UIAutomationClient", "FlaUI.Core", "FlaUI.UIA3") { Add-Type -Path (Join-Path $flaui "$d.dll") }
$script:Auto = New-Object FlaUI.UIA3.UIA3Automation
$script:Auto.ConnectionTimeout = [TimeSpan]::FromSeconds(5)
$script:Auto.TransactionTimeout = [TimeSpan]::FromSeconds(10)
$script:Cf = $script:Auto.ConditionFactory
$script:CT = [FlaUI.Core.Definitions.ControlType]
$script:Run = $null

# A Flare UI thread blocked this long counts as hung (the plugin's E2E hook writes heartbeat.json from it every second).
$script:HeartbeatSeconds = 60

<# Where runs go: E:\HouseStyle\e2e on the dev PC, C:\HouseStyle\e2e in the factory VM (no E: there). #>
function Get-E2ERoot { if (Test-Path "E:\") { "E:\HouseStyle\e2e" } else { "$env:SystemDrive\HouseStyle\e2e" } }

<# Throws when the plugin is up but Flare's UI thread has stopped answering, naming the windows it had open. #>
function Assert-Heartbeat {
    if (-not ($script:Run -and $script:Run.PSObject.Properties["ProjectOpen"])) { return }
    if (-not (Get-Process Flare -ErrorAction SilentlyContinue)) { throw "Flare exited (crashed or closed) during the scenario." }
    $f = Join-Path $script:Run.Hook "heartbeat.json"
    if (-not (Test-Path $f)) { return }   # a plugin build without the heartbeat
    $age = ((Get-Date) - (Get-Item $f).LastWriteTime).TotalSeconds
    if ($age -lt $script:HeartbeatSeconds) { return }
    $open = try { ((Get-Content $f -Raw | ConvertFrom-Json).windows) -join "; " } catch { "unknown" }
    throw "Flare's UI thread stopped responding: no heartbeat for $([int]$age)s (open windows: $open)"
}

function Wait-Until([scriptblock]$Test, [int]$Seconds = 60, [string]$What = "condition") {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        Assert-Heartbeat
        # A UI Automation call that times out or finds its element gone is "not yet", not a failure.
        try { $r = & $Test; $last = $null }
        catch {
            # PowerShell wraps a .NET method's exception: look at the inner one too.
            $types = "$($_.Exception.GetType().FullName) $(if ($_.Exception.InnerException) { $_.Exception.InnerException.GetType().FullName })"
            if ($types -notmatch 'COMException|Timeout|ElementNotAvailable|FlaUI\.') { throw }
            $r = $null; $last = $_.Exception.Message
        }
        if ($r) { return $r }
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out after ${Seconds}s waiting for $What$(if ($last) { " (last UI Automation error: $last)" })"
}

function Get-FlareWindow([string]$Name) {
    # Any Flare process: on a fresh install the licensing wizard runs in a child Flare.exe, and Start-FlareE2E closes
    # every other Flare before it starts, so they are all this run's.
    $desktop = $script:Auto.GetDesktop()
    $wins = foreach ($p in @(Get-Process Flare -ErrorAction SilentlyContinue)) { @($desktop.FindAllChildren($script:Cf.ByProcessId($p.Id))) }
    if ($Name) { return @($wins | Where-Object { $_.Name -eq $Name })[0] }
    # Flare has several top-level windows (floating editors, toasts): the main one is "<project> - MadCap Flare …".
    return @($wins | Where-Object { $_.Name -like "* - MadCap Flare*" })[0]
}

<#
.SYNOPSIS Starts Flare on a fresh copy of a fixture project, with the plugin isolated in its own settings folder.
.PARAMETER Fixture  A Flare project folder (copied; the original is never touched).
.PARAMETER Ai       Turn AI review on (needs ANTHROPIC_API_KEY, from the environment or the repo's .env).
.PARAMETER Force    Close a Flare that is already running (its unsaved work is lost).
.PARAMETER Settings More plugin settings for this run (for example StyleLayers, to point at a team guide).
.PARAMETER Bifrost  Turn AI on through the company AI service (Bifrost) instead of Anthropic: needs
                    HOUSESTYLE_TEST_BIFROST_URL and HOUSESTYLE_TEST_BIFROST_KEY. Name such scenarios ai-* too.
#>
function Start-FlareE2E {
    param([Parameter(Mandatory)][string]$Fixture, [switch]$Ai, [switch]$Bifrost, [switch]$Force, [string]$RunRoot = (Get-E2ERoot), [hashtable]$Settings = @{})
    if ($Bifrost) {
        if (-not $env:HOUSESTYLE_TEST_BIFROST_URL -or -not $env:HOUSESTYLE_TEST_BIFROST_KEY) { throw "-Bifrost needs HOUSESTYLE_TEST_BIFROST_URL and HOUSESTYLE_TEST_BIFROST_KEY." }
        $Ai = $true
        $Settings = @{ Provider = 1; BaseUrl = $env:HOUSESTYLE_TEST_BIFROST_URL } + $Settings   # LlmProvider.Bifrost
    }
    $script:Run = $null
    $running = Get-Process Flare -ErrorAction SilentlyContinue
    if ($running) {
        if (-not $Force) { throw "Flare is running. Close it, or pass -Force to close it (unsaved work is lost)." }
        $running | Stop-Process -Force -PassThru | Wait-Process -Timeout 30
    }
    # Program Files on the dev PC; Program Files (x86) where Flare's MSI was installed directly (the factory VM).
    $flare = @("$env:ProgramFiles", "${env:ProgramFiles(x86)}") | ForEach-Object { Join-Path $_ "MadCap Software\MadCap Flare 22\Flare.app\Flare.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $flare) { throw "Flare 22 isn't installed." }
    $dir = Join-Path $RunRoot (Get-Date -Format "yyyyMMdd-HHmmss")
    $hook = Join-Path $dir "hook"
    $appdata = Join-Path $hook "appdata"
    New-Item -ItemType Directory -Force $appdata | Out-Null
    Copy-Item $Fixture (Join-Path $dir "project") -Recurse
    $flprj = Get-ChildItem (Join-Path $dir "project") -Filter *.flprj | Select-Object -First 1

    # Setup already done and the tour already offered, so no first-run dialogs; AI per -Ai.
    (@{ AiOff = (-not $Ai); WriteDiagnostics = $true; LiveCheck = $false } + $Settings) | ConvertTo-Json | Set-Content (Join-Path $appdata "settings.json")
    @{ FirstStart = (Get-Date).ToUniversalTime().ToString("o"); TourOffered = $true; ChecklistHidden = $true; ProjectLinted = $true } | ConvertTo-Json | Set-Content (Join-Path $appdata "onboarding.json")
    # Flare loads a plugin only when it is turned on for this user.
    $key = "HKCU:\Software\MadCap Software\Flare\Plugins\House Style"
    New-Item $key -Force | Out-Null
    New-ItemProperty $key -Name Activated -Value True -PropertyType String -Force | Out-Null
    # A fresh install asks for its UI language at start until this is set (its OK button can't be automated).
    New-ItemProperty "HKCU:\Software\MadCap Software\Flare" -Name RunSelectLanguageUIOnStartup -Value False -PropertyType String -Force | Out-Null

    # The plugin reads its key from ANTHROPIC_API_KEY for either service, so a Bifrost run hands Flare the Bifrost key.
    $savedKey = $env:ANTHROPIC_API_KEY
    if ($Bifrost) { $env:ANTHROPIC_API_KEY = $env:HOUSESTYLE_TEST_BIFROST_KEY }
    if ($Ai -and -not $env:ANTHROPIC_API_KEY) {
        # The repo's .env: this checkout's, else the main checkout's (worktrees don't have one).
        $repo = Split-Path (Split-Path $PSScriptRoot)
        $main = Split-Path (git -C $repo rev-parse --path-format=absolute --git-common-dir)
        foreach ($f in (Join-Path $repo ".env"), (Join-Path $main ".env")) {
            if (-not (Test-Path $f)) { continue }
            foreach ($line in Get-Content $f) { if ($line -match '^\s*ANTHROPIC_API_KEY\s*=\s*"?(.*?)"?\s*$') { $env:ANTHROPIC_API_KEY = $Matches[1] } }
            if ($env:ANTHROPIC_API_KEY) { break }
        }
        if (-not $env:ANTHROPIC_API_KEY) { throw "-Ai needs ANTHROPIC_API_KEY (environment or .env)." }
    }
    $env:HOUSESTYLE_E2E_DIR = $hook
    try { $proc = Start-Process $flare -ArgumentList "`"$($flprj.FullName)`"" -PassThru }
    finally { Remove-Item Env:HOUSESTYLE_E2E_DIR; if ($Bifrost) { $env:ANTHROPIC_API_KEY = $savedKey } }
    $script:Run = [pscustomobject]@{ Dir = $dir; Hook = $hook; Project = (Join-Path $dir "project"); Process = $proc; Next = 1 }

    # Trial copies show the licensing wizard at every start: continue in trial mode.
    $readyFile = Join-Path $hook "ready.json"
    $wiz = Wait-Until { $w = Get-FlareWindow "Licensing Wizard"; if ($w) { $w } elseif (Test-Path $readyFile) { "licensed" } } 120 "the licensing wizard or House Style to start"
    if ($wiz -ne "licensed") {
        $finish = Wait-Until { $wiz.FindFirstDescendant($script:Cf.ByAutomationId("mFinish")) } 15 "the licensing wizard's Finish button"
        # Under UIA3 a WinForms click runs inside the call, so a slow licence check can time it out after the click
        # happened, or the wizard's process exits under it. The ready.json wait below decides either way.
        try { $finish.Patterns.Invoke.Pattern.Invoke() } catch { Write-Host "   (licensing wizard Finish: $($_.Exception.Message))" }
    }
    $ready = Wait-Until { Test-Path (Join-Path $hook "ready.json") } 120 "House Style to start in Flare (is the plugin installed and turned on?)"
    $script:Run | Add-Member PluginReady (Get-Date)   # Run-FlareE2E: after this, every failure is the plugin's (a test failure)
    $script:Run | Add-Member Version ((Get-Content (Join-Path $hook "ready.json") | ConvertFrom-Json).version)
    Wait-Until { $w = Get-FlareWindow; $w -and $w.Name -like "$($flprj.Name)*" } 120 "the project to open" | Out-Null
    $script:Run | Add-Member ProjectOpen (Get-Date)   # the heartbeat is checked from here: opening a project may block the UI thread
    return $script:Run
}

function Stop-FlareE2E {
    if ($script:Run) { Get-Process Flare -ErrorAction SilentlyContinue | Stop-Process -Force -PassThru | Wait-Process -Timeout 30 }   # and its child processes
}

<#
.SYNOPSIS Sends one command to the plugin's E2E hook and returns its answer (ok, value, error, state).
#>
function Send-E2E {
    param([Parameter(Mandatory)][string]$Verb, [string]$Arg, [hashtable]$Extra = @{}, [switch]$NoWait, [int]$Seconds = 60)
    $id = $script:Run.Next; $script:Run.Next++
    $cmd = @{ verb = $Verb; arg = $Arg } + $Extra
    $path = Join-Path $script:Run.Hook "cmd-$id.json"
    ($cmd | ConvertTo-Json -Compress) | Set-Content "$path.tmp"
    Move-Item "$path.tmp" $path
    if ($NoWait) { return }
    $res = Join-Path $script:Run.Hook "res-$id.json"
    Wait-Until { Test-Path $res } $Seconds "an answer to $Verb $Arg" | Out-Null
    $r = Get-Content $res -Raw | ConvertFrom-Json
    if (-not $r.ok) { throw "$Verb $Arg failed: $($r.error)" }
    return $r
}

function Get-E2EState { (Send-E2E state).state }

<# Waits until the pane is open and done linting (status no longer says Linting/Reviewing). #>
function Wait-PaneDone([int]$Seconds = 180) {
    Wait-Until {
        $s = Get-E2EState
        if ($s.pane -and -not $s.pane.panel.reviewing -and $s.pane.panel.status -and $s.pane.panel.status -notmatch '^(Linting|Reviewing)') { $s }
    } $Seconds "the issues pane to finish"
}

<# Clicks a House Style ribbon button by its label, through UI Automation (the writer's path). #>
function Invoke-Ribbon([Parameter(Mandatory)][string]$Label) {
    # UI Automation can miss the tab for a moment (a tooltip opening, the ribbon relaying out): look again until it shows.
    $tabCond = $script:Cf.ByName("House Style").And($script:Cf.ByControlType($script:CT::TabItem))
    $tab = Wait-Until { $w = Get-FlareWindow; if ($w) { $w.FindFirstDescendant($tabCond) } } 15 "the House Style ribbon tab"
    $tab.Patterns.SelectionItem.Pattern.Select()
    $b = Wait-Until { $tab.FindFirstDescendant($script:Cf.ByName($Label).And($script:Cf.ByControlType($script:CT::Button))) } 10 "ribbon button $Label"
    $b.Patterns.Invoke.Pattern.Invoke()
}

<# True while the issues pane is on screen (its title, through UI Automation): what the writer sees, not the plugin's state. #>
function Test-PaneOnScreen {
    $w = Get-FlareWindow
    $titles = $w.FindAllDescendants($script:Cf.ByFrameworkId("WPF").And($script:Cf.ByControlType($script:CT::Text)))
    return [bool]@($titles | Where-Object { $_.Name -like "House Style Issues*" -and -not $_.Properties.IsOffscreen.ValueOrDefault }).Count
}

<# Answers a message box House Style shows (title "House Style - <Action>", with an em dash) by its button. Message boxes
   are Windows' own dialogs, which the hook can't reach, and owned ones don't expose their buttons to UI Automation, so the
   box gets the button's command directly (OK and Yes are 1 and 6, Cancel and No are 2 and 7). #>
function Confirm-MessageBox([Parameter(Mandatory)][string]$Action, [string]$Button = "OK") {
    if (-not ("E2EWin" -as [type])) {
        Add-Type -Name E2EWin -Namespace "" -MemberDefinition @"
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern System.IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint msg, System.IntPtr w, System.IntPtr l);
"@
    }
    $id = @{ OK = 1; Cancel = 2; Yes = 6; No = 7 }[$Button]
    if (-not $id) { throw "Unknown message box button $Button" }
    $title = "House Style $([char]0x2014) $Action"
    $h = Wait-Until { $w = [E2EWin]::FindWindow("#32770", $title); if ($w -ne [IntPtr]::Zero) { $w } } 15 "the $Action message box"
    [void][E2EWin]::PostMessage($h, 0x0111, [IntPtr]$id, [IntPtr]::Zero)   # WM_COMMAND
}

function Save-E2EScreenshot([Parameter(Mandatory)][string]$Name) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $dir = if ($env:HOUSESTYLE_E2E_OUT) { $env:HOUSESTYLE_E2E_OUT } else { $script:Run.Dir }   # the runner's results folder, uploaded by CI
    $bmp.Save((Join-Path $dir "$Name.png"))
}

function Assert-E2E([bool]$Condition, [string]$Message) { if (-not $Condition) { throw "FAIL: $Message" } }

Export-ModuleMember -Function Get-E2ERoot, Start-FlareE2E, Stop-FlareE2E, Send-E2E, Get-E2EState, Wait-PaneDone, Invoke-Ribbon, Test-PaneOnScreen, Assert-E2E, Wait-Until, Confirm-MessageBox, Save-E2EScreenshot
