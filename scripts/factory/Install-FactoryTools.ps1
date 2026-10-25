<#
.SYNOPSIS Puts what a factory VM needs into it. The Flare VM (default): Flare (trial), Git, .NET SDK 8, GitHub CLI, Claude
Code, and the Flare check. The agents VM (-NoFlare): the pinned tools, the agent user and everything section 8 of the
factory-runtime spec sets up.
.DESCRIPTION Run elevated on the host after New-FactoryVM.ps1, by hand, never from a task. Files go into the VM over
Hyper-V (Copy-VMFile, no network share) and everything installs silently over PowerShell Direct.
Flare VM: copies the Flare installer from this PC's Package Cache, installs it and the tools, turns the House Style
plugin on for the factory account, puts Invoke-FlareCheck.ps1 in C:\HouseStyle\flare-check, and checkpoints the VM as
"tools-ready". It installs no House Style plugin and no license.
Agents VM (-NoFlare): no Flare and no auto sign-in. It installs the pinned Git, .NET SDK and Claude Code (Pins.psd1;
Claude Code in C:\Tools\claude), makes the agent user "hsagent" and the folders with section 2's ACLs, registers the
agent task (S4U by default, -AgentLogon Password if the isolation probes V7 or V9 need it), turns on the Task
Scheduler Operational log at 50 MB, restricts the event logs, removes the WinRM listener and blocks inbound SMB and
remote management, turns WebClient off, removes the auto sign-in values, the LSA secret and the Panther files, builds
the profile template under the task's logon type, and runs the write scan, its deny pass and the re-scan. It's safe to
run again: each -Refresh of the agents VM runs its scan and event log steps again (-ScanOnly). Downloads come only
from the publishers: github.com releases (Git, gh, the runner), dot.net (dotnet-install), claude.ai (Claude Code).
Then: Set-FactoryEgress.ps1 -Role agents inside the VM, and Install-Runner.ps1.
.EXAMPLE scripts\factory\Install-FactoryTools.ps1
.EXAMPLE scripts\factory\Install-FactoryTools.ps1 -Name HSAGENTS -NoFlare
#>
param(
    [string]$Name,
    [string]$Root = "E:\HouseStyle\vm",
    [switch]$NoFlare,
    [ValidateSet("S4U", "Password")][string]$AgentLogon = "S4U",
    [switch]$ScanOnly,   # the agents VM's -Refresh: the write scan and the event log restriction again, nothing else
    [string]$FlareSetup
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
Set-FactoryVMRoot $Root
if (-not $Name) { $Name = if ($NoFlare) { "HSAGENTS" } else { "HSFACTORY" } }
$cred = Get-VMCredential $Name
$pins = Import-PowerShellDataFile (Join-Path $PSScriptRoot "Pins.psd1")

# Copies repo files into the VM, under $dest. Copy-VMFile writes as SYSTEM, into a folder setup then locks down.
function Copy-IntoVM([string[]]$files, [string]$dest) {
    foreach ($f in $files) {
        $src = Join-Path $PSScriptRoot $f
        if (-not (Test-Path $src)) { Write-Warning "$f isn't in this checkout, so it wasn't copied."; continue }
        Copy-VMFile -Name $Name -SourcePath $src -DestinationPath (Join-Path $dest $f) -CreateFullPath -FileSource Host -Force
    }
}

if (-not $NoFlare) {
    if (-not $FlareSetup) { $FlareSetup = Get-ChildItem "C:\ProgramData\Package Cache" -Recurse -Filter SetupMadCapFlare.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName }
    if (-not $FlareSetup) { throw "No SetupMadCapFlare.exe in the Package Cache. Pass -FlareSetup." }
    # Flare's bundle and its MSI (the bundle looks for the MSI next to itself or in its cache).
    $msi = Get-ChildItem "C:\ProgramData\Package Cache" -Recurse -Filter SetupMadCapFlare.msi -ErrorAction SilentlyContinue | Select-Object -First 1
    foreach ($f in @($FlareSetup) + @($msi.FullName)) {
        if ($f) { Copy-VMFile -Name $Name -SourcePath $f -DestinationPath ("C:\Setup\" + (Split-Path $f -Leaf)) -CreateFullPath -FileSource Host -Force }
    }
    # The Flare check's own copy: the script the host runs at each check's steps, and the patch checks it shares.
    Copy-IntoVM @("Invoke-FlareCheck.ps1", "Guards.psm1") "C:\HouseStyle\flare-check"

    Invoke-Command -VMName $Name -Credential $cred -ScriptBlock {
        $ErrorActionPreference = "Stop"
        $ProgressPreference = "SilentlyContinue"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        function Latest($repo, $pattern) {
            $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ "User-Agent" = "house-style-factory" }
            ($rel.assets | Where-Object name -match $pattern | Select-Object -First 1).browser_download_url
        }
        function Fetch($url, $file) { $p = "C:\Setup\$file"; Invoke-WebRequest $url -OutFile $p -UseBasicParsing; $p }

        # The runner and the e2e suite run PowerShell scripts; Windows blocks them by default.
        Set-ExecutionPolicy RemoteSigned -Scope LocalMachine -Force
        # The evaluation needs online activation for its 90 days; its automatic attempt at first sign-in can fail.
        cscript //nologo C:\Windows\System32\slmgr.vbs /ato | Out-Host

        # Each step skips what is already there, so a rerun only finishes what's missing.
        # The MSI installs to Program Files (x86) when run directly.
        $flareExe = "C:\Program Files (x86)\MadCap Software\MadCap Flare 22\Flare.app\Flare.exe"
        if (-not (Test-Path $flareExe)) {
            # Flare's bundle installs nothing with /quiet (it selects no features), so its prerequisites and MSI go in directly.
            Write-Host "Visual C++ runtimes..."
            foreach ($vc in @{ u = "https://aka.ms/highdpimfc2013x64enu"; f = "vc120.exe" }, @{ u = "https://aka.ms/vs/17/release/vc_redist.x64.exe"; f = "vc140.exe" }) {
                $p = Start-Process (Fetch $vc.u $vc.f) -ArgumentList "/install /quiet /norestart" -Wait -PassThru
                if ($p.ExitCode -notin 0, 1638, 3010) { throw "$($vc.f) exit code $($p.ExitCode)" }
            }
            Write-Host "Flare..."
            $p = Start-Process msiexec.exe -ArgumentList '/i "C:\Setup\SetupMadCapFlare.msi" ADDLOCAL=ProductFeature /qn /norestart /l*v C:\Setup\flare-msi.log' -Wait -PassThru
            if ($p.ExitCode -notin 0, 3010) { throw "Flare MSI exit code $($p.ExitCode) (C:\Setup\flare-msi.log in the VM)" }
            if (-not (Test-Path $flareExe)) { throw "Flare MSI finished but $flareExe isn't there." }
        }
        if (-not (Test-Path "C:\Program Files\Git\cmd\git.exe")) {
            Write-Host "Git..."
            $git = Fetch (Latest "git-for-windows/git" '^Git-[\d.]+-64-bit\.exe$') "git.exe"
            Start-Process $git -ArgumentList "/VERYSILENT /NORESTART /NOCANCEL /SP-" -Wait
        }
        if (-not (Test-Path "C:\Program Files\GitHub CLI\gh.exe")) {
            Write-Host "GitHub CLI..."
            $gh = Fetch (Latest "cli/cli" '_windows_amd64\.msi$') "gh.msi"
            Start-Process msiexec.exe -ArgumentList "/i `"$gh`" /qn /norestart" -Wait
        }
        if (-not (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
            Write-Host ".NET SDK 8..."
            $dn = Fetch "https://dot.net/v1/dotnet-install.ps1" "dotnet-install.ps1"
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dn -Channel 8.0 -InstallDir "C:\Program Files\dotnet"
            if ($LASTEXITCODE) { throw "dotnet-install failed ($LASTEXITCODE)" }
        }
        $path = [Environment]::GetEnvironmentVariable("Path", "Machine")
        if ($path -notlike "*C:\Program Files\dotnet*") { [Environment]::SetEnvironmentVariable("Path", "$path;C:\Program Files\dotnet", "Machine") }
        if (-not (Test-Path "$env:USERPROFILE\.local\bin\claude.exe")) {
            Write-Host "Claude Code..."
            $cc = Fetch "https://claude.ai/install.ps1" "claude-install.ps1"
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $cc
        }

        # Tests and the runner run un-elevated (an elevated Flare relaunches itself and drops its arguments and environment),
        # so the factory account gets write access to the Plugins folder for install-plugin.ps1.
        icacls "C:\Program Files (x86)\MadCap Software\MadCap Flare 22\Flare.app\Plugins" /grant "factory:(OI)(CI)M" /T /Q | Out-Null
        # Flare loads House Style only when it is turned on for the user.
        $key = "HKCU:\Software\MadCap Software\Flare\Plugins\House Style"
        New-Item $key -Force | Out-Null
        New-ItemProperty $key -Name Activated -Value True -PropertyType String -Force | Out-Null
        # The scenarios write their runs here.
        New-Item -ItemType Directory -Force "C:\HouseStyle\e2e" | Out-Null
        Write-Host "Done."
    }
    Get-VMSnapshot -VMName $Name -Name "tools-ready" -ErrorAction SilentlyContinue | Remove-VMSnapshot
    # Hyper-V sometimes makes the checkpoint but fails to name it: rename the newest one then.
    try { Checkpoint-VM -Name $Name -SnapshotName "tools-ready" }
    catch { Get-VMSnapshot -VMName $Name | Sort-Object CreationTime | Select-Object -Last 1 | Rename-VMSnapshot -NewName "tools-ready" }
    Write-Host "Tools installed in $Name and checkpointed as tools-ready."
    return
}

# ==== The agents VM ==================================================================================================
# The setup modules go to C:\Setup\factory (Administrators only), the launcher to C:\HouseStyle\agent-runner.
Invoke-Command -VMName $Name -Credential $cred -ScriptBlock {
    # Elevated steps run only from Administrators-only folders: C:\Setup is made again before every download.
    if (Test-Path C:\Setup) { cmd /c rmdir /s /q C:\Setup }
    New-Item -ItemType Directory -Force C:\Setup | Out-Null
    & icacls.exe C:\Setup /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" | Out-Null
}
Copy-IntoVM @("AgentsVM.psm1", "Egress.psm1", "Set-FactoryEgress.ps1", "Guards.psm1") "C:\Setup\factory"
Copy-IntoVM @("Start-AgentJob.ps1", "Guards.psm1") "C:\Setup\agent-runner"

Invoke-Command -VMName $Name -Credential $cred -ArgumentList $pins, $AgentLogon, [bool]$ScanOnly -ScriptBlock {
    param($pins, $logon, $scanOnly)
    $ErrorActionPreference = "Stop"
    $ProgressPreference = "SilentlyContinue"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Import-Module C:\Setup\factory\AgentsVM.psm1 -Force
    $hs = $HouseStyleRoot
    function Fetch($url, $file) { $p = "C:\Setup\$file"; Invoke-WebRequest $url -OutFile $p -UseBasicParsing; $p }
    # icacls with an ACL that doesn't inherit. $grants: "*SID:(rights)" entries. Checked by reading the ACL back.
    function Set-FolderAcl([string]$path, [string[]]$grants) {
        New-Item -ItemType Directory -Force $path | Out-Null
        & icacls.exe $path /inheritance:r /grant:r @(@("*S-1-5-32-544:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F") + $grants) | Out-Null
        if ($LASTEXITCODE) { throw "Couldn't set the ACL of $path." }
        & icacls.exe $path /setowner "*S-1-5-32-544" | Out-Null
    }

    if (-not $scanOnly) {
        Set-ExecutionPolicy RemoteSigned -Scope LocalMachine -Force
        cscript //nologo C:\Windows\System32\slmgr.vbs /ato | Out-Host

        # ---- the pinned tools ---------------------------------------------------------------------------------------
        $gitVer = (& "C:\Program Files\Git\cmd\git.exe" --version 2>$null) -replace '^git version\s+', ''
        if ($gitVer -ne $pins.git) {
            Write-Host "Git $($pins.git)..."
            $m = [regex]::Match($pins.git, '^(\d+\.\d+\.\d+)\.windows\.(\d+)$')
            $file = if ($m.Groups[2].Value -eq "1") { "Git-$($m.Groups[1].Value)-64-bit.exe" } else { "Git-$($m.Groups[1].Value).$($m.Groups[2].Value)-64-bit.exe" }
            $git = Fetch "https://github.com/git-for-windows/git/releases/download/v$($pins.git)/$file" "git.exe"
            Start-Process $git -ArgumentList "/VERYSILENT /NORESTART /NOCANCEL /SP-" -Wait
        }
        if (-not (Test-Path "C:\Program Files\GitHub CLI\gh.exe")) {
            Write-Host "GitHub CLI..."
            $rel = Invoke-RestMethod "https://api.github.com/repos/cli/cli/releases/latest" -Headers @{ "User-Agent" = "house-style-factory" }
            $gh = Fetch (($rel.assets | Where-Object name -match '_windows_amd64\.msi$' | Select-Object -First 1).browser_download_url) "gh.msi"
            Start-Process msiexec.exe -ArgumentList "/i `"$gh`" /qn /norestart" -Wait
        }
        $dnVer = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { (& "C:\Program Files\dotnet\dotnet.exe" --version).Trim() } else { "" }
        if ($dnVer -ne $pins.dotnet) {
            Write-Host ".NET SDK $($pins.dotnet)..."
            $dn = Fetch "https://dot.net/v1/dotnet-install.ps1" "dotnet-install.ps1"
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $dn -Version $pins.dotnet -InstallDir "C:\Program Files\dotnet"
            if ($LASTEXITCODE) { throw "dotnet-install failed ($LASTEXITCODE)" }
        }
        $path = [Environment]::GetEnvironmentVariable("Path", "Machine")
        if ($path -notlike "*C:\Program Files\dotnet*") { [Environment]::SetEnvironmentVariable("Path", "$path;C:\Program Files\dotnet", "Machine") }
        [Environment]::SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1", "Machine")
        $ccVer = if (Test-Path C:\Tools\claude\claude.exe) { ((& C:\Tools\claude\claude.exe --version) -split ' ')[0] } else { "" }
        if ($ccVer -ne $pins.claude) {
            Write-Host "Claude Code $($pins.claude)..."
            $cc = Fetch "https://claude.ai/install.ps1" "claude-install.ps1"
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $cc $pins.claude
            New-Item -ItemType Directory -Force C:\Tools\claude | Out-Null
            Copy-Item "$env:USERPROFILE\.local\bin\claude.exe" C:\Tools\claude\claude.exe -Force
        }
        & icacls.exe C:\Tools\claude /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null

        # ---- the agent user ----------------------------------------------------------------------------------------
        $rnd = -join ((1..32) | ForEach-Object { [char](Get-Random -Minimum 33 -Maximum 126) })
        $secure = ConvertTo-SecureString $rnd -AsPlainText -Force
        if (-not (Get-LocalUser -Name $AgentUser -ErrorAction SilentlyContinue)) {
            New-LocalUser -Name $AgentUser -Password $secure -PasswordNeverExpires -UserMayNotChangePassword -Description "Runs the factory's agents, builds and tests" | Out-Null
        }
        $agentSid = (Get-LocalUser -Name $AgentUser).SID.Value
        $factorySid = (Get-LocalUser -Name factory).SID.Value

        # ---- folders (section 2's table and section 8's grant list) -------------------------------------------------
        Set-FolderAcl $hs @("*${factorySid}:(OI)(CI)F")
        foreach ($f in "fix-state", "flare-outbox", "flare-inbox", "factory-logs", "patch-work", "profile-template", "setup-log") { Set-FolderAcl (Join-Path $hs $f) @("*${factorySid}:(OI)(CI)F") }
        foreach ($f in "agent-jobs", "agent-in", "agent-tools") { Set-FolderAcl (Join-Path $hs $f) @("*${factorySid}:(OI)(CI)F", "*${agentSid}:(OI)(CI)RX") }
        # agent-work: the agent user makes its own job folders and has full control of them only (CREATOR OWNER).
        Set-FolderAcl (Join-Path $hs "agent-work") @("*${factorySid}:(OI)(CI)RX", "*${agentSid}:(RD,X,RA,REA,RC,AD)", "*S-1-3-0:(OI)(CI)(IO)F")
        # nuget: the script makes each run's cache and sets its ACL; the agent user may only look in.
        Set-FolderAcl (Join-Path $hs "nuget") @("*${factorySid}:(OI)(CI)F", "*${agentSid}:(RX)")
        # The launcher: Administrators write, everyone else reads and runs it.
        Set-FolderAcl (Join-Path $hs "agent-runner") @("*S-1-5-32-545:(OI)(CI)RX")
        Copy-Item C:\Setup\agent-runner\* (Join-Path $hs "agent-runner") -Force
        if (Test-Path C:\actions-runner) { Set-FolderAcl C:\actions-runner @("*${factorySid}:(OI)(CI)F") }

        # ---- the agent task ---------------------------------------------------------------------------------------
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File $hs\agent-runner\Start-AgentJob.ps1"
        $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 3) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        if ($logon -eq "S4U") {
            $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$AgentUser" -LogonType S4U -RunLevel Limited
            Register-ScheduledTask "House Style agent" -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        }
        else {
            # A password only Task Scheduler keeps: set on the account and handed to the task, never written anywhere.
            Set-LocalUser -Name $AgentUser -Password $secure
            Register-ScheduledTask "House Style agent" -Action $action -User "$env:COMPUTERNAME\$AgentUser" -Password $rnd -RunLevel Limited -Settings $settings -Force | Out-Null
        }
        $rnd = $null; $secure = $null
        # The agent user has no rights on the task: Administrators and SYSTEM only.
        $svc = New-Object -ComObject Schedule.Service; $svc.Connect()
        $svc.GetFolder("\").GetTask("House Style agent").SetSecurityDescriptor("D:P(A;;FA;;;BA)(A;;FA;;;SY)", 0)

        # ---- event logs -------------------------------------------------------------------------------------------
        & wevtutil.exe sl Microsoft-Windows-TaskScheduler/Operational /e:true /ms:52428800 | Out-Null
    }
    $agentSid = (Get-LocalUser -Name $AgentUser).SID.Value
    $before = Join-Path $hs "setup-log\eventlog-before.json"
    $rows = @(Set-EventLogRestriction $agentSid)
    if (-not (Test-Path $before)) { $rows | ConvertTo-Json | Set-Content $before -Encoding UTF8 }
    $left = @(Test-EventLogRestriction $agentSid)
    if ($left.Count) { throw "The event log restriction didn't hold: $($left -join '; ')" }

    if (-not $scanOnly) {
        # ---- inbound: no WinRM listener, no SMB or remote management in ---------------------------------------------
        & winrm.cmd delete "winrm/config/Listener?Address=*+Transport=HTTP" 2>$null | Out-Null
        & winrm.cmd delete "winrm/config/Listener?Address=*+Transport=HTTPS" 2>$null | Out-Null
        foreach ($g in "File and Printer Sharing", "Windows Remote Management", "Remote Desktop", "Remote Service Management", "Remote Scheduled Tasks Management",
                       "Remote Event Log Management", "Windows Management Instrumentation (WMI)", "Remote Volume Management", "Network Discovery") {
            Get-NetFirewallRule -DisplayGroup $g -Direction Inbound -ErrorAction SilentlyContinue | Disable-NetFirewallRule
        }
        Get-NetFirewallRule -DisplayName "House Style inbound*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName "House Style inbound blocked" -Direction Inbound -Action Block -Protocol TCP -LocalPort 135, 139, 445, 3389, 5985, 5986 -Profile Any | Out-Null
        Set-NetFirewallProfile -All -DefaultInboundAction Block
        Set-Service WebClient -StartupType Disabled -ErrorAction SilentlyContinue; Stop-Service WebClient -ErrorAction SilentlyContinue

        # ---- no auto sign-in: the Winlogon values, the LSA secret and the Panther files ----------------------------
        $wl = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
        Set-ItemProperty $wl AutoAdminLogon "0"
        foreach ($v in "DefaultPassword", "AutoLogonCount") { Remove-ItemProperty $wl $v -ErrorAction SilentlyContinue }
        Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class HsLsa {
    [StructLayout(LayoutKind.Sequential)] struct U { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct A { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref A attrs, uint access, out IntPtr handle);
    [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr handle, ref U key, IntPtr data);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr handle);
    public static uint Delete(string name) {
        A a = new A(); IntPtr h; uint r = LsaOpenPolicy(IntPtr.Zero, ref a, 0x00000020, out h); if (r != 0) return r;
        U k = new U(); k.Buffer = Marshal.StringToHGlobalUni(name); k.Length = (ushort)(name.Length * 2); k.MaximumLength = (ushort)(k.Length + 2);
        try { return LsaStorePrivateData(h, ref k, IntPtr.Zero); } finally { Marshal.FreeHGlobal(k.Buffer); LsaClose(h); }
    }
}
"@
        $r = [HsLsa]::Delete("DefaultPassword")
        if ($r -ne 0 -and $r -ne 0xC0000034) { throw "Couldn't remove the auto sign-in secret (LSA $r)." }   # 0xC0000034: there wasn't one
        foreach ($p in "C:\Windows\Panther\unattend.xml", "C:\Windows\Panther\Unattend", "C:\Windows\System32\Sysprep\unattend.xml", "C:\Windows\System32\Sysprep\Panther") {
            if (Test-Path $p) { cmd /c rmdir /s /q "$p" 2>$null; Remove-Item $p -Force -Recurse -ErrorAction SilentlyContinue }
        }
        foreach ($f in @(Get-ChildItem C:\Windows\Panther -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in ".xml", ".log" })) {
            if (Select-String -LiteralPath $f.FullName -Pattern "<Password>|AutoLogon" -Quiet -ErrorAction SilentlyContinue) { Remove-Item $f.FullName -Force }
        }

        # ---- the profile template, under the task's final logon type -----------------------------------------------
        # One run of the task with no job waiting makes the profile; the launcher stops at its first check.
        Remove-Item (Join-Path $hs "agent-jobs\next.txt") -ErrorAction SilentlyContinue
        Start-ScheduledTask "House Style agent"
        $deadline = (Get-Date).AddMinutes(5)
        do { Start-Sleep 5 } until ((Get-ScheduledTask "House Style agent").State -ne "Running" -or (Get-Date) -gt $deadline)
        $agentProfile = (Get-CimInstance Win32_UserProfile | Where-Object SID -eq $agentSid).LocalPath
        if (-not $agentProfile) { throw "The agent task's run made no profile for $AgentUser." }
        if (-not (Dismount-AgentHive $agentSid)) { throw "The agent user's hive wouldn't unload." }
        $template = Join-Path $hs "profile-template"
        Copy-Profile $agentProfile $template | Out-Null
        Protect-ProfileTemplate $template $agentSid (Get-LocalUser -Name factory).SID.Value
        Set-Content (Join-Path $hs "setup-log\profile.txt") $agentProfile
    }

    # ---- the write scan, its deny pass and the re-scan ----------------------------------------------------------------
    $found = @(Get-UserWritable -Sid $agentSid)
    Write-Host "The write scan found $($found.Count) objects outside the grant list the agent user could change."
    foreach ($f in $found) { try { Add-DenyWrite $f $agentSid } catch { Write-Warning "Couldn't close $($f.kind) $($f.path): $($_.Exception.Message)" } }
    $left = @(Get-UserWritable -Sid $agentSid)
    if ($left.Count) { throw "After the deny pass the agent user can still change $($left.Count) objects outside the grant list, the first: $(@($left | Select-Object -First 10 | ForEach-Object { "$($_.kind) $($_.path) ($($_.rights -join ', '))" }) -join '; ')" }
    Write-Host "Only the grant list is writable for $AgentUser."
}
Write-Host "Agents VM $Name set up. Next: Set-FactoryEgress.ps1 -Role agents inside it, then Install-Runner.ps1 -Name $Name -Labels agents -Service."
