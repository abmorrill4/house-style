<#
.SYNOPSIS Marks the Flare VM in use for manual testing, or not (factory-runtime spec section 9).
.DESCRIPTION While E:\HouseStyle\vm\flare-in-use exists, no Flare check, state re-read, reset or Watch-Factory action
touches the Flare VM. -On waits for a running Flare check to finish, then restores factory-base before it sets the
flag, so your enhanced session never meets a guest that e2e.yml has run merged code on since its last restore. -Off
removes the flag. Run elevated on the host.
.EXAMPLE scripts\factory\Set-FlareInUse.ps1 -On
#>
param([switch]$On, [switch]$Off, [string]$Root = "E:\HouseStyle\vm")
$script:Dot = $MyInvocation.InvocationName -eq '.'
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
Set-FactoryVMRoot $Root
function Wait-Briefly([int]$seconds) { Start-Sleep -Seconds $seconds }

function Set-FlareInUse {
    param([switch]$On, [switch]$Off)
    $flag = Get-HostPath "flare-in-use"
    if ($Off) { Remove-Item -LiteralPath $flag -Force -ErrorAction SilentlyContinue; return "off" }
    if (-not $On) { throw "Pass -On or -Off." }
    if (Get-CheckLock) {
        Write-Host "A Flare check is running. The flag is set once the Flare VM is back on its checkpoint."
        $deadline = (Get-Date).AddHours(4)
        while ((Get-CheckLock) -and (Get-Date) -lt $deadline) { Wait-Briefly 60 }
        if (Get-CheckLock) { throw "The Flare check still holds the Flare VM after 4 hours." }
    }
    if (Test-FailedResetMarker) { throw "The Flare VM is marked as a failed reset. Restore factory-base first, as the host log says." }
    # factory-base first, then the flag.
    Stop-VMHard $FlareVMName
    Restore-Checkpoint $FlareVMName "factory-base"
    Start-VMPlain $FlareVMName
    Assert-NoReparse $flag
    [IO.File]::WriteAllText($flag, (Get-Date).ToUniversalTime().ToString("o"))
    "on"
}

if (-not $script:Dot) { Write-Host (Set-FlareInUse -On:$On -Off:$Off) }
