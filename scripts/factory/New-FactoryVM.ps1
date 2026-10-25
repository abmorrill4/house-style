<#
.SYNOPSIS Builds (or rebuilds) one of the factory's Hyper-V VMs: Windows installed from an ISO, unattended, ready for
PowerShell Direct.
.DESCRIPTION Run elevated (Hyper-V needs it). -Role flare builds the Flare VM (HSFACTORY: 12 GB, 6 CPUs) and -Role
agents the agents VM (HSAGENTS: 8 GB static, 4 CPUs). Creates a Gen 2 VM on the Default Switch with an empty disk and
a small second disk holding the installer and autounattend.xml (local admin "factory", auto sign-in for the first
boot, remoting on). Windows Setup does the partitioning and boot files inside the VM. Then it waits for the signed-in
desktop, removes the installer media and checkpoints the VM as "windows-ready". Each VM has its own generated password
in E:\HouseStyle\vm\<Name>-credential.xml (DPAPI, Administrators only), made once and kept across rebuilds. No VM gets
another VM's credential. -Force rebuilds an existing VM. Then run Install-FactoryTools.ps1 (with -NoFlare for the
agents VM).
.EXAMPLE powershell -File scripts\factory\New-FactoryVM.ps1 -Iso E:\HouseStyle\vm\win11-ltsc-eval.iso
.EXAMPLE scripts\factory\New-FactoryVM.ps1 -Role agents -Iso E:\HouseStyle\vm\win11-ltsc-eval.iso
#>
param(
    [Parameter(Mandatory)][string]$Iso,
    [ValidateSet("flare", "agents")][string]$Role = "flare",
    [string]$Name,
    [string]$Root = "E:\HouseStyle\vm",
    [int64]$DiskBytes = 128GB,
    [int64]$MemoryBytes,
    [int]$Cpus,
    [switch]$Force
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
Set-FactoryVMRoot $Root
# Section 10's sizes: one agent job at a time on the agents VM, Flare plus a plugin build on the Flare VM.
if (-not $Name) { $Name = if ($Role -eq "agents") { "HSAGENTS" } else { "HSFACTORY" } }
if (-not $MemoryBytes) { $MemoryBytes = if ($Role -eq "agents") { 8GB } else { 12GB } }
if (-not $Cpus) { $Cpus = if ($Role -eq "agents") { 4 } else { 6 } }
$vhd = Join-Path $Root "$Name.vhdx"
$answer = Join-Path $Root "$Name-answer.vhdx"

if (Get-VM -Name $Name -ErrorAction SilentlyContinue) {
    if (-not $Force) { throw "VM $Name exists. Pass -Force to delete and rebuild it." }
    Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue
    Remove-VM -Name $Name -Force
}
foreach ($f in $vhd, $answer) { if (Test-Path $f) { if (-not $Force) { throw "$f exists. Pass -Force." }; Remove-Item $f -Force } }

# This VM's own password: made once, then reused so a rebuild keeps the same credential.
$cred = Get-VMCredential $Name -Create

# The installer on a disk, not a DVD: a Gen 2 VM booting the ISO stops at "Press any key to boot from CD or DVD", and a
# disk boots without asking. One FAT32 partition (UEFI boots only FAT), so install.wim is split under FAT32's 4 GB limit.
# autounattend.xml goes at its root, where Windows Setup looks for it.
New-VHD -Path $answer -SizeBytes 12GB -Dynamic | Out-Null
$disk = Mount-VHD -Path $answer -Passthru | Get-Disk
try {
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT
    $part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
    Format-Volume -Partition $part -FileSystem FAT32 -NewFileSystemLabel INSTALL -Confirm:$false | Out-Null
    $letter = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber).DriveLetter
    $img = Mount-DiskImage -ImagePath $Iso -PassThru
    try {
        $src = ($img | Get-Volume).DriveLetter + ":\"
        Write-Host "Copying the installer to its disk..."
        & robocopy $src "${letter}:\" /E /XF install.wim /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Copying the installer failed (robocopy $LASTEXITCODE)." }
        # dism.exe, not Split-WindowsImage: the cmdlet gets "access denied" where dism.exe works.
        $scratch = Join-Path $Root "scratch"; New-Item -ItemType Directory -Force $scratch | Out-Null
        & dism.exe /Split-Image /ImageFile:"${src}sources\install.wim" /SWMFile:"${letter}:\sources\install.swm" /FileSize:3800 /ScratchDir:$scratch | Out-Null
        if ($LASTEXITCODE) { throw "Splitting install.wim failed (dism $LASTEXITCODE)." }
    } finally { Dismount-DiskImage -ImagePath $Iso | Out-Null }
    $xml = (Get-Content (Join-Path $PSScriptRoot "unattend.xml") -Raw).Replace("__NAME__", $Name).Replace("__PASSWORD__", [System.Security.SecurityElement]::Escape($cred.GetNetworkCredential().Password))
    [IO.File]::WriteAllText("${letter}:\autounattend.xml", $xml, (New-Object System.Text.UTF8Encoding($false)))
} finally { Dismount-VHD -Path $answer }

New-VHD -Path $vhd -SizeBytes $DiskBytes -Dynamic | Out-Null
New-VM -Name $Name -Generation 2 -MemoryStartupBytes $MemoryBytes -VHDPath $vhd -SwitchName "Default Switch" | Out-Null
Set-VM -Name $Name -ProcessorCount $Cpus -StaticMemory -AutomaticCheckpointsEnabled $false -AutomaticStartAction Start -AutomaticStopAction ShutDown
$installer = Add-VMHardDiskDrive -VMName $Name -Path $answer -Passthru
# The Windows disk first: empty, it fails over to the installer; once Setup has written its boot files, it boots Windows.
$os = Get-VMHardDiskDrive -VMName $Name | Where-Object Path -eq $vhd
Set-VMFirmware -VMName $Name -SecureBootTemplate MicrosoftWindows -BootOrder $os, $installer
Set-VMKeyProtector -VMName $Name -NewLocalKeyProtector; Enable-VMTPM -VMName $Name
Enable-VMIntegrationService -VMName $Name -Name "Guest Service Interface"
Start-VM -Name $Name

Write-Host "Installing Windows and signing in (about 10-20 minutes)..."
$deadline = (Get-Date).AddMinutes(45)
do {
    Start-Sleep 30
    $ok = $null
    try { $ok = Invoke-Command -VMName $Name -Credential $cred -ScriptBlock { [bool](Get-Process explorer -ErrorAction SilentlyContinue) } -ErrorAction Stop } catch { }
} until ($ok -or (Get-Date) -gt $deadline)
if (-not $ok) { throw "The VM didn't reach a signed-in desktop in 45 minutes. Open it in Hyper-V Manager to see where it stopped." }

# Installer disk out (the VM must be off to remove it).
Stop-VM -Name $Name -Force
Get-VMHardDiskDrive -VMName $Name | Where-Object Path -eq $answer | Remove-VMHardDiskDrive
Remove-Item $answer -Force
Start-VM -Name $Name
$deadline = (Get-Date).AddMinutes(10)
do { Start-Sleep 15; $ok = $null; try { $ok = Invoke-Command -VMName $Name -Credential $cred -ScriptBlock { [bool](Get-Process explorer -ErrorAction SilentlyContinue) } -ErrorAction Stop } catch { } } until ($ok -or (Get-Date) -gt $deadline)
if (-not $ok) { throw "The VM didn't sign in again after removing the installer media." }
Checkpoint-VM -Name $Name -SnapshotName "windows-ready"
Write-Host "VM $Name is up, signed in as factory, and checkpointed as windows-ready."
