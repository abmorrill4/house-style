<#
.SYNOPSIS The Flare check's steps inside the Flare VM (factory-runtime spec section 5). Lives in flare-check-base at
C:\HouseStyle\flare-check with Guards.psm1; the host's Invoke-FlareHandoff.ps1 runs each step over PowerShell Direct.
.DESCRIPTION
-TrialRead (steps 4.1, the state re-read, and each -Refresh): before anything from a patch is on the VM, starts Flare
  with no House Style plugin, reads the text of its licensing window as it shows, closes Flare, and returns Flare's
  version and that text. The host maps it to a state; nothing here decides it.
-Step Prepare (4.2 to 4.4, network on): clones the public repo with no token, checks the base is an ancestor of
  origin/main, loads PatchRules.psd1 from the clone's main, copies base\ to fix\, applies the patch only after the full
  rules pass, and restores fix\ (main's build files: the patch can't change them).
-Step Install (6, offline): Run-FlareE2E.ps1 -RepoRoot fix\ -PrepareOnly, the harness from base\.
-Step Run (7, offline): the harness from base\ runs fix\'s scenarios and the holdouts.
-Step Stage (8): writes results\result.json (outcome, failed and retried public scenarios; holdout names stay private)
  and copies the logs and screenshots next to it, for the host to copy under its rules.
Flare needs a desktop, which PowerShell Direct doesn't have, so -TrialRead and -Step Run hand their work to the agent
task (-Agent, at sign-in in the factory account's session, registered once with -Register) and wait for its answer.
#>
param(
    [switch]$TrialRead,
    [ValidateSet("Prepare", "Install", "Run", "Stage")][string]$Step,
    [string]$Base,
    [string]$PatchFile,
    [switch]$Agent,
    [switch]$Register,
    [string]$Repo = "https://github.com/abmorrill4/house-style.git",
    [string]$Work = "C:\HouseStyle\check"
)
$ErrorActionPreference = "Stop"
$flareExe = @(Get-ChildItem "C:\Program Files*\MadCap Software\MadCap Flare *\Flare.app\Flare.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName)[0]
$ask = Join-Path $Work "ask"; $answer = Join-Path $Work "answer"

# ==== The agent task: work that needs the desktop ====================================================================
if ($Register) {
    $action = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Agent"
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:COMPUTERNAME\factory"
    $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\factory" -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask "House Style Flare agent" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    "Registered the House Style Flare agent task."
    return
}
# The licensing window's text, through UI Automation (.NET's own, so nothing from the repo loads): the window's name
# and every named element in it.
function Read-FlareLicense {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    Get-Process Flare -ErrorAction SilentlyContinue | Stop-Process -Force
    $p = Start-Process $flareExe -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(150); $win = $null
        do {
            Start-Sleep 2
            $all = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
            $mine = @($all | Where-Object { $_.Current.ProcessId -eq $p.Id -and $_.Current.Name })
            $win = @($mine | Where-Object { $_.Current.Name -match '(?i)licens' } | Select-Object -First 1)
            if (-not $win.Count -and @($mine | Where-Object { $_.Current.Name -match '(?i)MadCap Flare' }).Count) { $win = @($mine | Select-Object -First 1) }
        } until ($win.Count -or (Get-Date) -gt $deadline)
        if (-not $win.Count) { return "no window" }
        Start-Sleep 3   # the window fills its fields after it shows
        $names = @($win[0].Current.Name)
        $walker = [Windows.Automation.TreeWalker]::RawViewWalker
        $stack = New-Object System.Collections.Stack; $stack.Push($win[0])
        while ($stack.Count -and $names.Count -lt 500) {
            $e = $stack.Pop()
            $c = $walker.GetFirstChild($e)
            while ($c) { if ($c.Current.Name) { $names += $c.Current.Name }; $stack.Push($c); $c = $walker.GetNextSibling($c) }
        }
        ($names | Where-Object { $_ }) -join "`n"
    }
    finally { Get-Process Flare -ErrorAction SilentlyContinue | Stop-Process -Force }
}
if ($Agent) {
    New-Item -ItemType Directory -Force $ask, $answer | Out-Null
    while ($true) {
        foreach ($f in @(Get-ChildItem $ask -Filter *.json -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike ".tmp-*" })) {
            $req = Get-Content $f.FullName -Raw | ConvertFrom-Json
            Remove-Item $f.FullName -Force
            $out = [ordered]@{ ok = $true; text = "" }
            try {
                switch ($req.action) {
                    "trial" { $out.text = Read-FlareLicense }
                    "run" {
                        $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Work "base\tests\e2e\Run-FlareE2E.ps1") -RepoRoot (Join-Path $Work "fix") -NoAi `
                            -OutDir (Join-Path $Work "results\e2e") -ScenarioDir (Join-Path $Work "holdout") *>&1 | Out-String
                        [IO.File]::WriteAllText((Join-Path $Work "run.log"), $o)
                    }
                }
            }
            catch { $out.ok = $false; $out.text = $_.Exception.Message }
            $tmp = Join-Path $answer ".tmp-$($f.Name)"
            [IO.File]::WriteAllText($tmp, ($out | ConvertTo-Json -Compress)); Move-Item $tmp (Join-Path $answer $f.Name) -Force
        }
        Start-Sleep 2
    }
}
# Asks the agent task for one piece of work and waits for its answer.
function Invoke-Desktop([string]$action, [int]$minutes) {
    New-Item -ItemType Directory -Force $ask, $answer | Out-Null
    $name = "$action-$([guid]::NewGuid().ToString('N')).json"
    $tmp = Join-Path $ask ".tmp-$name"; [IO.File]::WriteAllText($tmp, (@{ action = $action } | ConvertTo-Json -Compress)); Move-Item $tmp (Join-Path $ask $name)
    $deadline = (Get-Date).AddMinutes($minutes)
    do { Start-Sleep 2 } until ((Test-Path (Join-Path $answer $name)) -or (Get-Date) -gt $deadline)
    if (-not (Test-Path (Join-Path $answer $name))) { throw "The desktop didn't answer $action in $minutes minutes." }
    $a = Get-Content (Join-Path $answer $name) -Raw | ConvertFrom-Json
    Remove-Item (Join-Path $answer $name) -Force
    if (-not $a.ok) { throw "$action failed in the desktop: $($a.text)" }
    "" + $a.text
}

# ==== The steps, over PowerShell Direct ===============================================================================
if ($TrialRead) {
    # Byte arrays and plain strings only go back to the host.
    "" + (Get-Item $flareExe).VersionInfo.FileVersion
    Invoke-Desktop "trial" 5
    return
}
function Invoke-Git([string[]]$a) { & git.exe -c core.hooksPath=NUL -c core.fsmonitor=false @a 2>&1 | Out-Null; if ($LASTEXITCODE) { throw "git $($a[0]) failed" } }
switch ($Step) {
    "Prepare" {
        if ($Base -notmatch '^[0-9a-f]{40}$') { throw "The base isn't a commit." }
        if (Test-Path $Work) { cmd /c rmdir /s /q "$Work" 2>$null }
        New-Item -ItemType Directory -Force $Work | Out-Null
        $b = Join-Path $Work "base"; $f = Join-Path $Work "fix"
        $env:GIT_NO_REPLACE_OBJECTS = "1"
        # The public repo with no token, and the base must be on main.
        Invoke-Git @("clone", "--quiet", $Repo, $b)
        Invoke-Git @("-C", $b, "merge-base", "--is-ancestor", $Base, "origin/main")
        Invoke-Git @("-C", $b, "-c", "advice.detachedHead=false", "checkout", "--quiet", "--detach", $Base)
        # The patch rules from the clone's main, never from the patch or the base.
        Import-Module (Join-Path $PSScriptRoot "Guards.psm1") -Force
        $rulesFile = Join-Path $Work "PatchRules.psd1"
        [IO.File]::WriteAllText($rulesFile, ((& git.exe -C $b show "origin/main:scripts/factory/PatchRules.psd1") -join "`n"))
        $rules = Import-PatchRules $rulesFile
        $patch = [IO.File]::ReadAllText($PatchFile)
        $check = Test-PatchText $patch $rules
        if (-not $check.ok) { throw "The patch failed the rules: $(@($check.problems | Select-Object -First 3) -join '; ')" }
        & robocopy.exe $b $f /MIR /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -gt 7) { throw "Copying base to fix failed." }
        if ($check.files.Count) { Invoke-Git @("-C", $f, "apply", "--check", $PatchFile); Invoke-Git @("-C", $f, "apply", $PatchFile) }
        # The restore, with the network on: main's build files, which the patch can't change.
        & dotnet.exe restore (Join-Path $f "FlareAI.sln") 2>&1 | Out-Null
        if ($LASTEXITCODE) { throw "The restore failed." }
        foreach ($p in @(Get-ChildItem (Join-Path $f "tests\e2e\flaui") -Filter *.csproj -ErrorAction SilentlyContinue)) { & dotnet.exe restore $p.FullName 2>&1 | Out-Null; if ($LASTEXITCODE) { throw "The FlaUI restore failed." } }
        "prepared"
    }
    "Install" {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Work "base\tests\e2e\Run-FlareE2E.ps1") -RepoRoot (Join-Path $Work "fix") -PrepareOnly *>&1 | Out-File (Join-Path $Work "install.log")
        if ($LASTEXITCODE) { throw "The plugin didn't build and install." }
        "installed"
    }
    "Run" { Invoke-Desktop "run" 58 | Out-Null; "ran" }
    "Stage" {
        $res = Join-Path $Work "results"; New-Item -ItemType Directory -Force $res | Out-Null
        $json = Join-Path $res "e2e\results.json"
        $rows = if (Test-Path $json) { @((Get-Content $json -Raw | ConvertFrom-Json)) } else { @() }
        $failed = @($rows | Where-Object { -not $_.passed })
        $public = @($failed | Where-Object { ("" + $_.scenario) -notlike "holdout-*" })
        $outcome = if (-not $rows.Count) { "none" } elseif (-not $failed.Count) { "pass" } elseif (@($failed | Where-Object kind -ne "vm").Count -eq 0) { "vm" } elseif ($public.Count) { "fix" } else { "holdout" }
        $result = [ordered]@{ outcome = $outcome; failed = @($public | ForEach-Object { "" + $_.scenario }); retried = @($rows | Where-Object { $_.passed -and $_.firstTry -and ("" + $_.scenario) -notlike "holdout-*" } | ForEach-Object { "" + $_.scenario }) }
        [IO.File]::WriteAllText((Join-Path $res "result.json"), ($result | ConvertTo-Json -Compress))
        # Flare saying its trial or license ended, anywhere in the run: the host decides what that means.
        $log = (@(Get-ChildItem $Work -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw }) + @($rows | ForEach-Object { "" + $_.error })) -join "`n"
        if ($log -match '(?i)(trial|licen[cs]e).{0,60}(expired|invalid|ended|not valid)') { [IO.File]::WriteAllText((Join-Path $res "license-report.txt"), "Flare reported its trial or license expired during the run.") }
        foreach ($l in @(Get-ChildItem $Work -Filter *.log -ErrorAction SilentlyContinue)) { Copy-Item $l.FullName (Join-Path $res $l.Name) -Force }
        # Screenshots and logs one level down, with names the host's copy rules take.
        $shots = Join-Path $res "shots"; New-Item -ItemType Directory -Force $shots | Out-Null
        $n = 0
        foreach ($s in @(Get-ChildItem (Join-Path $res "e2e") -Recurse -File -Include *.png, *.txt, *.log -ErrorAction SilentlyContinue | Select-Object -First 150)) {
            $n++; $safe = (($s.BaseName -replace '[^A-Za-z0-9._-]', '-') -replace '^[^A-Za-z0-9]+', '')
            if ($safe.Length -gt 60) { $safe = $safe.Substring(0, 60) }
            Copy-Item $s.FullName (Join-Path $shots ("{0:D3}-{1}{2}" -f $n, $safe, $s.Extension.ToLowerInvariant())) -Force
        }
        if (Test-Path (Join-Path $res "e2e")) { cmd /c rmdir /s /q "$(Join-Path $res 'e2e')" 2>$null }
        "staged"
    }
}
