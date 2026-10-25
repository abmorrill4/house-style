<#
.SYNOPSIS Sets up a factory VM's GitHub Actions runner: installs it and registers it (with -Token).
.DESCRIPTION Run elevated on the host, by hand. Two kinds:
- The Flare VM (default, -Labels flare): the runner can't be a service, because a service has no desktop and Flare
  needs one. It starts at sign-in in the factory account's desktop session, un-elevated, and restarts if it exits.
- The agents VM (-Labels agents -Service): a service under the factory account (the script user), with exactly the
  labels self-hosted, windows and agents, never flare. The agents VM has no auto sign-in, so no desktop runner.
-NoStart registers the runner and sets it up without starting it, so the VM can be checkpointed before the runner
ever takes a job (migration steps 13 and 14 for the Flare VM's factory-base). Without -Token it only refreshes the
start-up settings.
.EXAMPLE scripts\factory\Install-Runner.ps1 -NoStart -Token (gh api -X POST repos/abmorrill4/house-style/actions/runners/registration-token --jq .token)
.EXAMPLE scripts\factory\Install-Runner.ps1 -Name HSAGENTS -Labels agents -Service -Token <token>
#>
param(
    [string]$Name = "HSFACTORY",
    [string]$Root = "E:\HouseStyle\vm",
    [string]$Repo = "abmorrill4/house-style",
    [string]$Token,   # one-hour registration token; only needed to register
    [ValidateSet("flare", "agents")][string]$Labels = "flare",
    [switch]$Service,
    [switch]$NoStart
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
Set-FactoryVMRoot $Root
if ($Labels -eq "agents" -and -not $Service) { throw "The agents VM's runner is a service: pass -Service." }
if ($Labels -eq "flare" -and $Service) { throw "The Flare VM's runner needs a desktop, so it can't be a service." }
$cred = Get-VMCredential $Name
# The service logs on as the factory account, so config.cmd needs its password, passed in memory over PowerShell Direct.
$password = if ($Service) { $cred.GetNetworkCredential().Password } else { $null }

Invoke-Command -VMName $Name -Credential $cred -ArgumentList $Repo, $Token, $Name, $Labels, [bool]$Service, [bool]$NoStart, $password -ScriptBlock {
    param($repo, $token, $name, $labels, $asService, $noStart, $password)
    $ErrorActionPreference = "Stop"
    $ProgressPreference = "SilentlyContinue"
    $dir = "C:\actions-runner"
    if (-not (Test-Path "$dir\run.cmd")) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $rel = Invoke-RestMethod "https://api.github.com/repos/actions/runner/releases/latest" -Headers @{ "User-Agent" = "house-style-factory" }
        New-Item -ItemType Directory -Force C:\Setup | Out-Null
        $zip = "C:\Setup\actions-runner.zip"
        Invoke-WebRequest ($rel.assets | Where-Object name -match '^actions-runner-win-x64-[\d.]+\.zip$' | Select-Object -First 1).browser_download_url -OutFile $zip -UseBasicParsing
        Expand-Archive $zip $dir -Force
    }
    if ($token) {
        $a = @("--unattended", "--replace", "--url", "https://github.com/$repo", "--token", $token, "--name", $name, "--work", "_work")
        # The agents runner gets exactly self-hosted, windows and agents: GitHub's defaults are replaced (V21).
        if ($labels -eq "agents") { $a += @("--no-default-labels", "--labels", "self-hosted,windows,agents") } else { $a += @("--labels", $labels) }
        if ($asService) { $a += @("--runasservice", "--windowslogonaccount", ".\factory", "--windowslogonpassword", $password) }
        & "$dir\config.cmd" @a
        if ($LASTEXITCODE) { throw "config.cmd failed" }
    }
    if ($asService) {
        # Only the factory account and Administrators reach the runner's folder; the agent user gets nothing.
        $sid = (Get-LocalUser -Name factory).SID.Value
        & icacls.exe $dir /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*${sid}:(OI)(CI)F" /T /C /Q | Out-Null
        $svc = @(Get-Service "actions.runner.*" -ErrorAction SilentlyContinue)
        if (-not $svc.Count) { "The runner service isn't registered yet. Run again with -Token."; return }
        Set-Service $svc[0].Name -StartupType Automatic
        if ($noStart) { Stop-Service $svc[0].Name -Force; "Runner service registered and stopped: $($svc[0].Name)." }
        else { if ($svc[0].Status -ne "Running") { Start-Service $svc[0].Name }; "Runner service running: $($svc[0].Name)." }
        return
    }

    # Started at sign-in, in the desktop session, un-elevated (an elevated Flare relaunches itself and drops its
    # environment). The loop restarts run.cmd whenever it exits: Task Scheduler's own restart setting only covers a task
    # that fails to start, not one that exits.
    $loop = "while (`$true) { & '$dir\run.cmd'; Start-Sleep 30 }"
    $action = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -WindowStyle Hidden -Command `"$loop`"" -WorkingDirectory $dir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:COMPUTERNAME\factory"
    $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\factory" -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask "GitHub runner" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    if (-not $noStart -and -not (Get-Process Runner.Listener -ErrorAction SilentlyContinue)) { Start-ScheduledTask "GitHub runner" }

    # A screensaver or lock would take the desktop away from Flare in the middle of a run.
    $u = New-Object System.Security.Principal.NTAccount("factory")
    $sid = $u.Translate([System.Security.Principal.SecurityIdentifier]).Value
    $hive = "Registry::HKEY_USERS\$sid\Control Panel\Desktop"
    if (Test-Path $hive) { Set-ItemProperty $hive ScreenSaveActive 0; Set-ItemProperty $hive ScreenSaverIsSecure 0 }
    else { Write-Warning "The factory account's settings aren't loaded (not signed in): screensaver not changed." }
    powercfg /change monitor-timeout-ac 0; powercfg /change standby-timeout-ac 0
    "Runner task registered$(if ($noStart) { ', not started' }). Registered: $([bool](Test-Path "$dir\.runner"))"
}
