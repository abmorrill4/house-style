<#
.SYNOPSIS Installs the host's tasks on THESEUS (factory-runtime spec section 11, migration step 15).
.DESCRIPTION Run elevated on the host, by hand. Copies the host scripts into E:\HouseStyle\vm\bin from origin/main of
-Checkout (never from a working tree), sets the host state folders' owner and ACLs, and registers each task with its
trigger, time limit and IgnoreNew, as the owner's interactive, elevated logon (they need the owner's gh sign-in, so a
night the owner is signed out is skipped):
  House Style Flare check            every minute       4 hours
  House Style HSFACTORY nightly reset 03:00              6 hours
  House Style HSAGENTS nightly reset  03:30              6 hours
  House Style HSAGENTS reset resume   on demand          6 hours (the Flare check task starts it for a stale flag)
  House Style factory watch           every 5 minutes    4 minutes
No task passes -ScratchVM. The earlier "House Style factory reset" task is removed.
.EXAMPLE scripts\factory\Install-HostTasks.ps1
#>
param([string]$Root = "E:\HouseStyle\vm", [string]$Checkout = "E:\HouseStyle\repos\house-style", [switch]$DryRun)
$script:Dot = $MyInvocation.InvocationName -eq '.'
$ErrorActionPreference = "Stop"

# The scripts the tasks run, and the modules they load.
$HostFiles = "FactoryVM.psm1", "Controls.psm1", "Guards.psm1", "Egress.psm1", "AgentsVM.psm1", "Triage.psm1", "PatchRules.psd1", "Pins.psd1",
             "Invoke-FlareHandoff.ps1", "Reset-FactoryVM.ps1", "Watch-Factory.ps1", "Set-FlareInUse.ps1", "Install-FactoryTools.ps1", "Install-Runner.ps1"
# The limits table, as data.
$HostTasks = @(
    [pscustomobject]@{ name = "House Style Flare check"; script = "Invoke-FlareHandoff.ps1"; args = ""; trigger = "minute"; limit = (New-TimeSpan -Hours 4) },
    [pscustomobject]@{ name = "House Style HSFACTORY nightly reset"; script = "Reset-FactoryVM.ps1"; args = "-Name HSFACTORY"; trigger = "03:00"; limit = (New-TimeSpan -Hours 6) },
    [pscustomobject]@{ name = "House Style HSAGENTS nightly reset"; script = "Reset-FactoryVM.ps1"; args = "-Name HSAGENTS"; trigger = "03:30"; limit = (New-TimeSpan -Hours 6) },
    [pscustomobject]@{ name = "House Style HSAGENTS reset resume"; script = "Reset-FactoryVM.ps1"; args = "-Name HSAGENTS -Resume"; trigger = "demand"; limit = (New-TimeSpan -Hours 6) },
    [pscustomobject]@{ name = "House Style factory watch"; script = "Watch-Factory.ps1"; args = ""; trigger = "5minutes"; limit = (New-TimeSpan -Minutes 4) }
)
function New-HostTaskParts($t, [string]$bin) {
    # conhost --headless: no console window flashing on the owner's desktop.
    $action = New-ScheduledTaskAction -Execute conhost.exe -Argument ("--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $bin $t.script)`" $($t.args)").Trim()
    $trigger = switch ($t.trigger) {
        "minute" { New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1) }
        "5minutes" { New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5) }
        "demand" { $null }
        default { New-ScheduledTaskTrigger -Daily -At $t.trigger }
    }
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit $t.limit
    @{ Action = $action; Trigger = $trigger; Principal = $principal; Settings = $settings }
}

function Install-HostTasks {
    Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
    Set-FactoryVMRoot $Root
    $bin = Get-HostPath "bin"
    Set-HostStateAcl
    # The scripts from origin/main, never a working tree's.
    & git.exe -C $Checkout fetch -q origin main
    if ($LASTEXITCODE) { throw "Couldn't fetch main into $Checkout." }
    foreach ($f in $HostFiles) {
        $text = (& git.exe -C $Checkout show "origin/main:scripts/factory/$f") -join "`r`n"
        if ($LASTEXITCODE) { throw "origin/main has no scripts/factory/$f." }
        $dest = Join-Path $bin $f
        Assert-NoReparse $dest
        if (-not $DryRun) { [IO.File]::WriteAllText($dest, $text + "`r`n", (New-Object Text.UTF8Encoding $true)) }
    }
    $agentDir = Join-Path (Split-Path $bin) "agent"   # Controls.psm1 loads ..\agent\AgentCommon.psm1
    New-Item -ItemType Directory -Force $agentDir | Out-Null
    if (-not $DryRun) { [IO.File]::WriteAllText((Join-Path $agentDir "AgentCommon.psm1"), ((& git.exe -C $Checkout show "origin/main:scripts/agent/AgentCommon.psm1") -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding $true)) }
    $bad = @(Test-HostStateAcl)
    if ($bad.Count) { throw "The host state folders' ACL didn't take: $($bad -join '; ')" }
    foreach ($t in $HostTasks) {
        $p = New-HostTaskParts $t $bin
        if ($DryRun) { Write-Host "Would register '$($t.name)': $($t.script) $($t.args), $($t.trigger), limit $($t.limit)"; continue }
        $a = @{ TaskName = $t.name; Action = $p.Action; Principal = $p.Principal; Settings = $p.Settings; Force = $true }
        if ($p.Trigger) { $a.Trigger = $p.Trigger }
        Register-ScheduledTask @a | Out-Null
    }
    if (-not $DryRun) { Unregister-ScheduledTask -TaskName "House Style factory reset" -Confirm:$false -ErrorAction SilentlyContinue }
    "Installed $($HostTasks.Count) host tasks, running from $bin."
}

if (-not $script:Dot) { Write-Host (Install-HostTasks) }
