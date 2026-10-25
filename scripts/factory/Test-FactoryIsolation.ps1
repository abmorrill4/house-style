<#
.SYNOPSIS Runs the agents VM's isolation probes V1 to V22 (factory-runtime spec, Verification plan) and says which pass.
.DESCRIPTION A setup script run by hand on the host, elevated, never from a task (migration steps 6 and 9, and after any
change to the agents VM). Probes that act as the agent user run inside the agents VM as jobs started only through
Invoke-AgentJob, like every factory job: a scratch repository holds Pester probes (tests/probes), and a test job runs
them with the job's own test channel. A canary process runs as the script user, and a canary program writes its own
user name to a drop folder only the script user can read; that name must never be "factory".
Run it once with -Before, before the ACLs, the deny pass, the event log restriction and the firewall rules are in
place: then V4 to V6 and V13 must fail, which shows the probes can tell the difference. -Probe picks probes (V7 runs a
first-ever restore of main and the full suite, so it's slow). Results go to the console and to
E:\HouseStyle\vm\factory-logs\isolation-<date>.json.
V19 runs against the Flare VM (-FlareVM), V20 runs a restore-first -Refresh of the agents VM, V21 reads GitHub with the
owner's gh sign-in, and V22 runs from the host.
.EXAMPLE scripts\factory\Test-FactoryIsolation.ps1
.EXAMPLE scripts\factory\Test-FactoryIsolation.ps1 -Probe V1, V2, V8
#>
param(
    [string]$Name = "HSAGENTS",
    [string]$FlareVM = "HSFACTORY",
    [string]$Root = "E:\HouseStyle\vm",
    [string]$Repo = "abmorrill4/house-style",
    [string]$Checkout = "E:\HouseStyle\repos\house-style",   # main's code for V7, V9 and V17, from its origin/main
    [string[]]$Probe = @("V1", "V2", "V3", "V4", "V5", "V6", "V6b", "V6c", "V8", "V10", "V11", "V12", "V13", "V14", "V15", "V16", "V17", "V18", "V21", "V22"),
    [switch]$Before
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
Set-FactoryVMRoot $Root
$cred = Get-VMCredential $Name
$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$id, [bool]$pass, [string]$detail) {
    # With -Before, the access probes must fail: a pass there means the probe can't tell the difference.
    $want = if ($Before -and @("V4", "V5", "V6", "V13") -contains $id) { $false } else { $true }
    $ok = ($pass -eq $want)
    $results.Add([pscustomobject]@{ probe = $id; ok = $ok; held = $pass; detail = $detail })
    Write-Host ("{0,-4} {1}  {2}" -f $id, $(if ($ok) { "ok  " } else { "FAIL" }), $detail)
}

# ==== Inside the agents VM ===========================================================================================
# The factory's modules from this checkout go to C:\Setup\isolation, Administrators only, and run there as the
# script user. Every probe that acts as the agent user is a job through Invoke-AgentJob.
Invoke-Command -VMName $Name -Credential $cred -ScriptBlock {
    if (Test-Path C:\Setup\isolation) { cmd /c rmdir /s /q C:\Setup\isolation }
    New-Item -ItemType Directory -Force C:\Setup\isolation | Out-Null
    & icacls.exe C:\Setup\isolation /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" | Out-Null
}
foreach ($f in @(Get-ChildItem (Join-Path $PSScriptRoot "*.psm1"), (Join-Path $PSScriptRoot "*.psd1"), (Join-Path (Split-Path $PSScriptRoot) "agent\*") -File)) {
    $rel = if ($f.DirectoryName -like "*\agent") { "agent\$($f.Name)" } else { "factory\$($f.Name)" }
    Copy-VMFile -Name $Name -SourcePath $f.FullName -DestinationPath "C:\Setup\isolation\$rel" -CreateFullPath -FileSource Host -Force
}
# Main's code for the probes that restore and test it.
$mainBundle = Join-Path ([IO.Path]::GetTempPath()) "hs-isolation-main.bundle"
& git.exe -C $Checkout fetch -q origin main
& git.exe -C $Checkout bundle create $mainBundle origin/main 2>$null
if ($LASTEXITCODE) { throw "Couldn't bundle main from $Checkout." }
$mainSha = (& git.exe -C $Checkout rev-parse origin/main).Trim()
Copy-VMFile -Name $Name -SourcePath $mainBundle -DestinationPath "C:\Setup\isolation\main.bundle" -CreateFullPath -FileSource Host -Force

# Runs one probe body inside the agents VM, with the probe helpers loaded. Returns { pass; detail }.
function Invoke-VMProbe([scriptblock]$body, [object[]]$argv = @()) {
    $helpers = {
        $ErrorActionPreference = "Stop"
        Import-Module C:\Setup\isolation\factory\Controls.psm1 -Force
        Import-Module C:\Setup\isolation\factory\AgentsVM.psm1 -Force
        $agentSid = (Get-LocalUser -Name $AgentUser).SID.Value
        $drop = "C:\HouseStyle\setup-log\canary-drop"
        # The canary drop folder: the agent user may write into it, only the script user and Administrators read.
        if (-not (Test-Path $drop)) {
            New-Item -ItemType Directory -Force $drop | Out-Null
            & icacls.exe $drop /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*$((Get-LocalUser -Name factory).SID.Value):(OI)(CI)F" "*${agentSid}:(W)" | Out-Null
        }
        # A scratch main holding Pester probes under tests/probes, bundled for a test job.
        # The base copy of scripts/agent a test job's channel runs comes from this checkout, in every probe repo.
        $sln = "Microsoft Visual Studio Solution File, Format Version 12.00`r`nProject(`"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}`") = `"P`", `"src\P\P.csproj`", `"{11111111-1111-1111-1111-111111111111}`"`r`nEndProject`r`nGlobal`r`nEndGlobal`r`n"
        function New-ProbeRepo([hashtable]$files) {
            foreach ($f in Get-ChildItem C:\Setup\isolation\agent -File) { $files["scripts/agent/$($f.Name)"] = [IO.File]::ReadAllText($f.FullName) }
            if ($files.ContainsKey("FlareAI.sln")) { $files["FlareAI.sln"] = $sln }
            $d = "C:\Setup\isolation\probe-repo-$([guid]::NewGuid().ToString('N').Substring(0, 6))"
            New-Item -ItemType Directory -Force $d | Out-Null
            Push-Location $d
            try {
                & git.exe init -q; & git.exe config user.name probe; & git.exe config user.email probe@example.invalid
                foreach ($k in $files.Keys) { $p = Join-Path $d $k; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p, $files[$k]) }
                & git.exe add -A; & git.exe commit -q -m probes
                $sha = (& git.exe rev-parse HEAD).Trim()
                $bundle = "C:\HouseStyle\agent-in\base-$sha.bundle"
                & git.exe bundle create $bundle HEAD 2>$null | Out-Null
                Copy-Item (Join-Path $d "scripts") "C:\HouseStyle\agent-tools\$sha\scripts" -Recurse -Force -ErrorAction SilentlyContinue
                $sha
            }
            finally { Pop-Location }
        }
        # Runs Pester probes as the agent user in a test job. Returns the job's result (Invoke-AgentJob).
        function Invoke-ProbeJob([string]$probes, [int]$limitSeconds = 600, [hashtable]$extra = @{}) {
            $files = @{ "tests/probes/Probes.Tests.ps1" = $probes }
            foreach ($k in $extra.Keys) { $files[$k] = $extra[$k] }
            $sha = New-ProbeRepo $files
            $ctx = New-JobContext -Root C:\HouseStyle -AgentSid $agentSid -Cache (Get-NewestLockedCache C:\HouseStyle) -LimitMinutes 60
            Invoke-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "pester"; path = "tests/probes"; limitSeconds = $limitSeconds }
        }
        function Get-PesterRows($job) { if ($job.files["pester.json"]) { @($job.files["pester.json"] | ConvertFrom-Json) } else { @() } }
    }
    $sb = [scriptblock]::Create($helpers.ToString() + "`n" + $body.ToString())
    Invoke-Command -VMName $Name -Credential $cred -ScriptBlock $sb -ArgumentList $argv
}

$probes = [ordered]@{
    "V1" = {
        $j = Invoke-ProbeJob @'
Describe 'V1' { It 'is not an admin' { (whoami /groups | Out-String) | Should Not Match 'S-1-5-32-544' } }
'@
        $r = @(Get-PesterRows $j); [pscustomobject]@{ pass = ($r.Count -and -not @($r | Where-Object { -not $_.passed }).Count); detail = "$($r.Count) checks, exit $($j.exit)" }
    }
    "V2" = {
        # A canary process as the script user, with a marker in its environment and command line.
        $marker = "hs-canary-$([guid]::NewGuid().ToString('N'))"
        $env:HS_CANARY = $marker
        $canary = Start-Process powershell.exe -ArgumentList "-NoProfile -Command Start-Sleep 600 # $marker" -PassThru -WindowStyle Hidden
        $runner = @(Get-Process Runner.Listener -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Id)
        try {
            $j = Invoke-ProbeJob @"
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class P { [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(int a, bool i, int id); }'
Describe 'V2' {
    foreach (`$id in @($($canary.Id)$(if ($runner) { ", $runner" }))) {
        It "can't read process `$id's memory or environment" { [P]::OpenProcess(0x0410, `$false, `$id) | Should Be ([IntPtr]::Zero) }
        It "can't read process `$id's command line" { "" + (Get-CimInstance Win32_Process -Filter "ProcessId=`$id").CommandLine | Should Not Match '$marker' }
    }
}
"@
            $r = @(Get-PesterRows $j); [pscustomobject]@{ pass = ($r.Count -ge 2 -and -not @($r | Where-Object { -not $_.passed }).Count); detail = "$($r.Count) checks" }
        }
        finally { Stop-Process -Id $canary.Id -Force -ErrorAction SilentlyContinue }
    }
    "V3" = {
        # A scripted push: a script-user process whose command line holds a token-shaped marker, as git push would.
        $fake = "ghs_$([guid]::NewGuid().ToString('N'))isolation"
        $p = Start-Process powershell.exe -ArgumentList "-NoProfile -Command Start-Sleep 600 # http.extraheader=AUTHORIZATION: basic $fake" -PassThru -WindowStyle Hidden
        try {
            $j = Invoke-ProbeJob @"
Describe 'V3' { It 'sees no token in any command line' { (@(Get-CimInstance Win32_Process | ForEach-Object { "" + `$_.CommandLine }) -join ' ') | Should Not Match '$fake' } }
"@
            $r = @(Get-PesterRows $j); [pscustomobject]@{ pass = ($r.Count -and -not @($r | Where-Object { -not $_.passed }).Count); detail = "exit $($j.exit)" }
        }
        finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    "V4" = {
        # A scripted push by the script user first, so the logs hold something it wrote.
        Write-EventLog -LogName Application -Source "Application" -EventId 1000 -Message "isolation probe: a scripted push ran" -ErrorAction SilentlyContinue
        $j = Invoke-ProbeJob @'
Describe 'V4' {
    foreach ($p in "C:\Users\factory", "C:\actions-runner", "C:\HouseStyle\factory-logs", "C:\HouseStyle\fix-state", "C:\HouseStyle\patch-work", "C:\HouseStyle\flare-outbox", "C:\HouseStyle\flare-inbox", "C:\Windows\Panther") {
        It "can't read $p" { { Get-ChildItem $p -Force -ErrorAction Stop | Out-Null } | Should Throw }
    }
    It "can't read the Winlogon password value" { (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue).DefaultPassword | Should BeNullOrEmpty }
    foreach ($ch in "Microsoft-Windows-PowerShell/Operational", "Windows PowerShell", "Microsoft-Windows-TaskScheduler/Operational", "Application") {
        It "reads no event from $ch" { @(Get-WinEvent -LogName $ch -MaxEvents 5 -ErrorAction SilentlyContinue).Count | Should Be 0 }
    }
}
'@
        $r = @(Get-PesterRows $j); [pscustomobject]@{ pass = ($r.Count -ge 13 -and -not @($r | Where-Object { -not $_.passed }).Count); detail = "$(@($r | Where-Object { -not $_.passed }).Count) of $($r.Count) reads got through" }
    }
    "V5" = {
        # The full write scan, as the agent user: every directory, every HKLM key and loaded hive, every service.
        $j = Invoke-ProbeJob @'
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class S { [DllImport("advapi32.dll", SetLastError=true)] public static extern IntPtr OpenSCManager(string m, string d, int a); [DllImport("advapi32.dll", SetLastError=true)] public static extern IntPtr OpenService(IntPtr h, string n, int a); [DllImport("advapi32.dll")] public static extern bool CloseServiceHandle(IntPtr h); }'
$grant = @("$env:USERPROFILE", "C:\HouseStyle\agent-work")
function Granted($p) { foreach ($g in $grant) { if ($p -eq $g -or $p.StartsWith($g + "\", [StringComparison]::OrdinalIgnoreCase)) { return $true } }; $false }
$wrote = New-Object System.Collections.Generic.List[string]
foreach ($vol in @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq "Fixed" })) {
    $stack = New-Object System.Collections.Stack; $stack.Push($vol.RootDirectory)
    while ($stack.Count) {
        $d = $stack.Pop()
        $probe = Join-Path $d.FullName ("hs-probe-" + [guid]::NewGuid().ToString("N").Substring(0, 6))
        try { [IO.File]::WriteAllText($probe, "x"); Remove-Item $probe -Force; if (-not (Granted $d.FullName)) { $wrote.Add($d.FullName) } } catch { }
        try { foreach ($c in $d.EnumerateDirectories()) { if (-not ($c.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $stack.Push($c) } } } catch { }
    }
}
$keys = New-Object System.Collections.Generic.List[string]
function Walk($hive, $path) {
    $k = $null; try { $k = $hive.OpenSubKey($path, $true) } catch { }
    if ($k) { $keys.Add("$($hive.Name)\$path"); $k.Close() }
    $r = $null; try { $r = $hive.OpenSubKey($path, $false) } catch { }
    if ($r) { try { foreach ($s in $r.GetSubKeyNames()) { Walk $hive $(if ($path) { "$path\$s" } else { $s }) } } finally { $r.Close() } }
}
Walk ([Microsoft.Win32.Registry]::LocalMachine) ""
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
foreach ($h in [Microsoft.Win32.Registry]::Users.GetSubKeyNames()) { if ($h -ne $me -and $h -ne "${me}_Classes") { Walk ([Microsoft.Win32.Registry]::Users) $h } }
$svcs = New-Object System.Collections.Generic.List[string]
$scm = [S]::OpenSCManager($null, $null, 0x1)
foreach ($s in Get-Service) { $h = [S]::OpenService($scm, $s.Name, 0x2 -bor 0x40000 -bor 0x80000); if ($h -ne [IntPtr]::Zero) { $svcs.Add($s.Name); [S]::CloseServiceHandle($h) | Out-Null } }
Describe 'V5' {
    It 'writes no folder outside the grant list' { $wrote -join '; ' | Should BeNullOrEmpty }
    It 'opens no HKLM key or other hive for write' { $keys -join '; ' | Should BeNullOrEmpty }
    It 'changes no service' { $svcs -join '; ' | Should BeNullOrEmpty }
    It "can't change the agent task" { { schtasks.exe /Change /TN "House Style agent" /DISABLE 2>&1 | Out-Null; if ($LASTEXITCODE -eq 0) { throw "changed" } } | Should Not Throw }
    It "can't add a firewall rule" { { New-NetFirewallRule -DisplayName "hs-probe" -Direction Outbound -Action Allow -ErrorAction Stop } | Should Throw }
    It "can't change the cache's ACL" { & icacls.exe $env:NUGET_PACKAGES /grant "*S-1-1-0:F" 2>&1 | Out-Null; $LASTEXITCODE | Should Not Be 0 }
    It "can't open its own job's stdout for writing" {
        $out = Join-Path (Split-Path $env:HOUSESTYLE_TEST_REPO) "out\stdout.txt"
        { [IO.File]::Open($out, "Open", "Write", "ReadWrite").Dispose() } | Should Throw
    }
}
'@ -limitSeconds 3000
        $r = @(Get-PesterRows $j); $bad = @($r | Where-Object { -not $_.passed })
        [pscustomobject]@{ pass = ($r.Count -ge 7 -and -not $bad.Count); detail = $(if ($bad.Count) { "failed: " + (($bad | ForEach-Object { "$($_.name): $($_.message)" }) -join ' | ').Substring(0, [Math]::Min(600, (($bad | ForEach-Object { "$($_.name): $($_.message)" }) -join ' | ').Length)) } else { "only the grant list is writable" }) }
    }
    "V6" = {
        $j = Invoke-ProbeJob @'
function Tcp($h, $p) { $c = New-Object Net.Sockets.TcpClient; try { $t = $c.ConnectAsync($h, $p); $t.Wait(5000) -and $c.Connected } catch { $false } finally { $c.Dispose() } }
function Udp($h, $p) { $u = New-Object Net.Sockets.UdpClient; try { $u.Client.ReceiveTimeout = 3000; $u.Connect($h, $p); [void]$u.Send([byte[]](0, 1), 2); $ep = $null; [void]$u.Receive([ref]$ep); $true } catch { $false } finally { $u.Dispose() } }
Describe 'V6' {
    foreach ($h in "github.com", "api.github.com", "api.nuget.org", "example.com", "1.1.1.1") { It "can't reach $h over TCP 443" { Tcp $h 443 | Should Be $false } }
    It "can't reach 1.1.1.1 over UDP 53 directly" { Udp "1.1.1.1" 53 | Should Be $false }
    It "can't reach an outside port 445" { Tcp "example.com" 445 | Should Be $false }
    It "can't use WebDAV" { Test-Path "\\example.com@SSL\DavWWWRoot" | Should Be $false }
    It "can't run a BITS transfer" { { Start-BitsTransfer -Source "https://example.com/" -Destination "$env:TEMP\b.txt" -ErrorAction Stop } | Should Throw }
    It "can't fetch with certutil" { & certutil.exe -urlcache -f "http://example.com/" "$env:TEMP\c.txt" 2>&1 | Out-Null; $LASTEXITCODE | Should Not Be 0 }
}
'@
        $r = @(Get-PesterRows $j); $bad = @($r | Where-Object { -not $_.passed })
        [pscustomobject]@{ pass = ($r.Count -ge 10 -and -not $bad.Count); detail = $(if ($bad.Count) { "got through: " + (($bad | ForEach-Object { $_.name }) -join ', ') } else { "everything blocked" }) }
    }
    "V6b" = {
        $j = Invoke-ProbeJob @'
Describe 'V6b' { It 'gets an HTTP response from the Anthropic API' {
    $code = try { (Invoke-WebRequest "https://api.anthropic.com/" -UseBasicParsing -TimeoutSec 20).StatusCode } catch { if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 } }
    $code | Should BeGreaterThan 0
} }
'@
        $r = @(Get-PesterRows $j); [pscustomobject]@{ pass = ($r.Count -and -not @($r | Where-Object { -not $_.passed }).Count); detail = "exit $($j.exit)" }
    }
    "V6c" = {
        # Inside a restore job: main's restore runs an Exec target that probes, through the restore rule.
        $props = @'
<Project>
  <Target Name="HsNetProbe" BeforeTargets="Restore;_GenerateRestoreGraph">
    <Exec Command="powershell.exe -NoProfile -Command &quot;foreach ($h in 'api.nuget.org','builds.dotnet.microsoft.com','www.microsoft.com') { $c = New-Object Net.Sockets.TcpClient; $ok = try { $c.ConnectAsync($h, 443).Wait(5000) } catch { $false }; Write-Output ('hs-probe ' + $h + ' ' + $ok) }&quot;" />
  </Target>
</Project>
'@
        $sha = New-ProbeRepo @{ "Directory.Build.targets" = $props; "FlareAI.sln" = ""; "src/P/P.csproj" = "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>" }
        $ctx = New-JobContext -Root C:\HouseStyle -AgentSid $agentSid -Cache (New-RunCache C:\HouseStyle) -LimitMinutes 30
        $j = Invoke-AgentJob $ctx @{ kind = "restore"; base = $sha; limitSeconds = 900 }
        $lines = @(("" + $j.files["stdout.txt"]) -split "`n" | Where-Object { $_ -match 'hs-probe' })
        $after = Invoke-ProbeJob "Describe 'V6c' { It 'cannot reach NuGet in the next job' { `$c = New-Object Net.Sockets.TcpClient; (try { `$c.ConnectAsync('api.nuget.org', 443).Wait(5000) } catch { `$false }) | Should Be `$false } }"
        $r = @(Get-PesterRows $after)
        $nuget = [bool]($lines | Where-Object { $_ -match 'api\.nuget\.org True' })
        [pscustomobject]@{ pass = ($nuget -and $r.Count -and -not @($r | Where-Object { -not $_.passed }).Count); detail = "in the restore: $($lines -join '; '). The .NET host and the unrelated site are recorded, not judged." }
    }
    "V7" = {
        param($mainSha)
        Copy-Item C:\Setup\isolation\main.bundle "C:\HouseStyle\agent-in\base-$mainSha.bundle" -Force
        Get-ChildItem C:\HouseStyle\nuget -Directory -ErrorAction SilentlyContinue | ForEach-Object { cmd /c rmdir /s /q $_.FullName }
        $cache = New-RunCache C:\HouseStyle
        $ctx = New-JobContext -Root C:\HouseStyle -AgentSid $agentSid -Cache $cache -LimitMinutes 60
        $restore = Invoke-AgentJob $ctx @{ kind = "restore"; base = $mainSha; limitSeconds = 3000 }
        $out = "" + $restore.files["stdout.txt"]
        Lock-RunCache $ctx | Out-Null
        $full = Invoke-AgentJob $ctx @{ kind = "test"; base = $mainSha; channel = "full"; limitSeconds = 3000 }
        [pscustomobject]@{ pass = ($restore.exit -eq 0 -and $out -notmatch 'NU3018|NU3028' -and $full.exit -eq 0); detail = "restore exit $($restore.exit), full exit $($full.exit). If the restore needed another host, stop and ask the owner; if full failed on DPAPI under S4U, switch to -AgentLogon Password." }
    }
    "V8" = {
        $sha = New-ProbeRepo @{ "README.md" = "probe" }
        $ctx = New-JobContext -Root C:\HouseStyle -AgentSid $agentSid -Cache (Get-NewestLockedCache C:\HouseStyle) -LimitMinutes 10
        $j = Invoke-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "e2e"; limitSeconds = 120 }
        [pscustomobject]@{ pass = ($j.exit -eq 2 -and ("" + $j.files["stdout.txt"]) -match 'add or update a scenario'); detail = "exit $($j.exit)" }
    }
    "V9" = {
        param($mainSha)
        $ctx = New-JobContext -Root C:\HouseStyle -AgentSid $agentSid -Cache (Get-NewestLockedCache C:\HouseStyle) -LimitMinutes 60
        $j = Invoke-AgentJob $ctx @{ kind = "test"; base = $mainSha; channel = "flare-tests"; limitSeconds = 3000 }
        [pscustomobject]@{ pass = ($j.exit -eq 0); detail = "exit $($j.exit)" }
    }
    "V10" = {
        Invoke-ProbeJob "Describe 'x' { It 'runs' { 1 | Should Be 1 } }" | Out-Null
        $marker = "hs-sleeper-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        $j = Invoke-ProbeJob "Describe 'V10' { It 'sleeps' { Start-Process powershell.exe -ArgumentList '-NoProfile -Command Start-Sleep 600 # $marker' -WindowStyle Hidden; Start-Sleep 600 } }" -limitSeconds 30
        $left = @(Get-CimInstance Win32_Process | Where-Object { ("" + $_.CommandLine) -match $marker })
        [pscustomobject]@{ pass = ($j.exit -eq 259 -and -not $left.Count); detail = "exit $($j.exit), $($left.Count) left running" }
    }
    "V11" = {
        $marker = "hs-detached-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        $j = Invoke-ProbeJob @"
Describe 'V11' { It 'starts sleepers every way it can' {
    `$out = Join-Path (Split-Path `$env:HOUSESTYLE_TEST_REPO) 'out'
    `$swap = "while (`$true) { try { Rename-Item '`$out' 'out-old' -ErrorAction Stop; cmd /c mklink /J '`$out' C:\Windows | Out-Null } catch { }; Start-Sleep 1 } # $marker"
    Start-Process powershell.exe -ArgumentList "-NoProfile -Command `$swap" -WindowStyle Hidden
    Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = 'powershell.exe -NoProfile -Command Start-Sleep 900 # $marker' } | Out-Null
    try { Register-ScheduledTask 'hs-probe-sleeper' -Action (New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -Command Start-Sleep 900 # $marker') -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1)) -ErrorAction Stop | Out-Null } catch { }
    try { `$b = bitsadmin /create hs-probe; bitsadmin /addfile hs-probe https://example.com/ "`$env:TEMP\x" | Out-Null; bitsadmin /SetNotifyCmdLine hs-probe powershell.exe "-NoProfile -Command Start-Sleep 900 # $marker" | Out-Null; bitsadmin /resume hs-probe | Out-Null } catch { }
    1 | Should Be 1
} }
"@ -limitSeconds 120
        $left = @(Get-CimInstance Win32_Process | Where-Object { ("" + $_.CommandLine) -match $marker })
        $tasks = @(Get-ScheduledTask -TaskName "hs-probe-sleeper" -ErrorAction SilentlyContinue)
        [pscustomobject]@{ pass = (-not $left.Count -and -not $tasks.Count -and -not $j.refused); detail = "$($left.Count) processes and $($tasks.Count) tasks left; read $(if ($j.refused) { 'refused: ' + $j.refused } else { 'clean' })" }
    }
    "V12" = {
        $drop = "C:\HouseStyle\setup-log\canary-drop"
        Get-ChildItem $drop -File -ErrorAction SilentlyContinue | Remove-Item -Force
        $canary = "powershell.exe -NoProfile -Command [IO.File]::WriteAllText('$drop\' + [guid]::NewGuid().ToString('N') + '.txt', [Environment]::UserName)"
        $j = Invoke-ProbeJob @"
Describe 'V12' { It 'plants config and build files in its checkout' {
    `$r = `$env:HOUSESTYLE_TEST_REPO
    & git.exe -C `$r config extensions.worktreeConfig true
    [IO.File]::WriteAllText((Join-Path `$r '.git\config.worktree'), "[core]``n`tfsmonitor = $($canary.Replace('"', '\"'))``n")
    New-Item -ItemType Directory -Force (Join-Path `$r 'obj') | Out-Null
    [IO.File]::WriteAllText((Join-Path `$r 'obj\x.props'), '<Project><Target Name="T" BeforeTargets="Build"><Exec Command="$canary" /></Target></Project>')
    New-Item -ItemType Directory -Force (Join-Path `$r 'obj\.git') | Out-Null
    1 | Should Be 1
} }
"@
        $names = @(Get-ChildItem $drop -File -ErrorAction SilentlyContinue | ForEach-Object { (Get-Content $_.FullName -Raw).Trim() })
        [pscustomobject]@{ pass = ($names -notcontains "factory"); detail = "canary ran as: $(if ($names.Count) { ($names | Select-Object -Unique) -join ', ' } else { 'nobody' })" }
    }
    "V13" = {
        $before = (Get-Acl C:\actions-runner -ErrorAction SilentlyContinue).Sddl
        $j1 = Invoke-ProbeJob "Describe 'V13' { It 'swaps out for a junction' { `$o = Join-Path (Split-Path `$env:HOUSESTYLE_TEST_REPO) 'out'; Get-ChildItem `$o | Remove-Item -Force -ErrorAction SilentlyContinue; cmd /c rmdir `$o; cmd /c mklink /J `$o C:\actions-runner | Out-Null; 1 | Should Be 1 } }"
        $j2 = Invoke-ProbeJob "Describe 'V13' { It 'makes a hard link as a result file' { `$o = Join-Path (Split-Path `$env:HOUSESTYLE_TEST_REPO) 'out\results'; New-Item -ItemType Directory -Force `$o | Out-Null; cmd /c mklink /H (Join-Path `$o 'pester.json') `$env:USERPROFILE\NTUSER.DAT | Out-Null; 1 | Should Be 1 } }"
        $after = (Get-Acl C:\actions-runner -ErrorAction SilentlyContinue).Sddl
        [pscustomobject]@{ pass = ([bool]$j1.refused -and [bool]$j2.refused -and $before -eq $after); detail = "junction read: $($j1.refused); hard link read: $($j2.refused); actions-runner ACL unchanged: $($before -eq $after)" }
    }
    "V14" = {
        $j = Invoke-ProbeJob @'
Describe 'V14 job N' { It 'leaves things behind' {
    New-Item -ItemType Directory -Force "$env:USERPROFILE\.claude", "$env:LOCALAPPDATA\Microsoft\MSBuild\Current\Imports\Microsoft.Common.props\ImportBefore", "C:\HouseStyle\agent-work\hs-leftover" | Out-Null
    Set-Content "$env:USERPROFILE\.claude\settings.json" '{"hooks":{}}'
    Set-Content "$env:LOCALAPPDATA\Microsoft\MSBuild\Current\Imports\Microsoft.Common.props\ImportBefore\x.props" '<Project />'
    New-Item -Force "HKCU:\Software\Microsoft\Command Processor" | Out-Null; Set-ItemProperty "HKCU:\Software\Microsoft\Command Processor" AutoRun "echo hs-probe"
    New-Item -ItemType Directory -Force (Split-Path $PROFILE) | Out-Null; Set-Content $PROFILE "# hs-probe"
    Set-ItemProperty HKCU:\Environment HS_PROBE "1"
    Set-Content C:\Windows\Temp\hs-probe.txt "x" -ErrorAction SilentlyContinue
    Set-Content C:\HouseStyle\agent-work\hs-leftover\locked.txt "x"; & icacls.exe C:\HouseStyle\agent-work\hs-leftover\locked.txt /deny "*S-1-1-0:(D)" | Out-Null
    $edits = @()
    foreach ($f in @(Get-ChildItem $env:NUGET_PACKAGES -Recurse -Include "*.TestAdapter.dll", "*.targets" -ErrorAction SilentlyContinue | Select-Object -First 2)) { try { [IO.File]::AppendAllText($f.FullName, " "); $edits += $f.FullName } catch { } }
    $edits.Count | Should Be 0
} }
'@
        $n1 = Invoke-ProbeJob @'
Describe 'V14 job N+1' {
    It 'sees no Claude settings' { Test-Path "$env:USERPROFILE\.claude\settings.json" | Should Be $false }
    It 'sees no MSBuild import' { Test-Path "$env:LOCALAPPDATA\Microsoft\MSBuild\Current\Imports\Microsoft.Common.props\ImportBefore\x.props" | Should Be $false }
    It 'sees no AutoRun' { (Get-ItemProperty "HKCU:\Software\Microsoft\Command Processor" -ErrorAction SilentlyContinue).AutoRun | Should BeNullOrEmpty }
    It 'sees no profile' { Test-Path $PROFILE | Should Be $false }
    It 'sees no environment value' { (Get-ItemProperty HKCU:\Environment -ErrorAction SilentlyContinue).HS_PROBE | Should BeNullOrEmpty }
    It 'sees no leftover agent-work folder' { Test-Path C:\HouseStyle\agent-work\hs-leftover | Should Be $false }
}
'@
        $r = @(Get-PesterRows $n1); $r0 = @(Get-PesterRows $j)
        $temp = Test-Path C:\Windows\Temp\hs-probe.txt
        [pscustomobject]@{ pass = ($r.Count -ge 6 -and -not @($r + $r0 | Where-Object { -not $_.passed }).Count); detail = "Windows\Temp file there: $temp (recorded)" }
    }
    "V15" = {
        # An agent job with a sign-in that isn't real: claude.exe starts and fails to sign in, which spends nothing.
        $sha = New-ProbeRepo @{ "README.md" = "probe" }
        $ctx = New-JobContext -Root C:\HouseStyle -AgentSid $agentSid -Cache (Get-NewestLockedCache C:\HouseStyle) -LimitMinutes 5 -Token ("sk-ant-oat01-isolation-probe-" + [guid]::NewGuid().ToString("N"))
        $j = Invoke-AgentJob $ctx @{ kind = "cover"; base = $sha; prompt = "Reply with the word ok."; limitSeconds = 120 }
        $l = try { $j.files["launcher.json"] | ConvertFrom-Json } catch { $null }
        [pscustomobject]@{ pass = ($l -and $l.signinAtChildStart -eq $false); detail = "signin.txt at child start: $(if ($l) { $l.signinAtChildStart } else { 'unknown' })" }
    }
    "V16" = {
        $target = "C:\HouseStyle\setup-log\v16-target"; New-Item -ItemType Directory -Force $target | Out-Null; Set-Content "$target\keep.txt" "keep"
        & icacls.exe $target /grant "*${agentSid}:(OI)(CI)RX" | Out-Null
        Invoke-ProbeJob "Describe 'V16' { It 'plants a junction in its profile' { cmd /c mklink /J `"`$env:USERPROFILE\trap`" $target | Out-Null; 1 | Should Be 1 } }" | Out-Null
        [pscustomobject]@{ pass = (Test-Path "$target\keep.txt"); detail = "target kept: $(Test-Path "$target\keep.txt")" }
    }
    "V17" = {
        $drop = "C:\HouseStyle\setup-log\canary-drop"
        Get-ChildItem $drop -File -ErrorAction SilentlyContinue | Remove-Item -Force
        $canary = "powershell.exe -NoProfile -Command [IO.File]::WriteAllText('$drop\' + [guid]::NewGuid().ToString('N') + '.txt', [Environment]::UserName)"
        $props = "<Project><Target Name=`"T`" BeforeTargets=`"Restore;_GenerateRestoreGraph;Build`"><Exec Command=`"$canary`" /></Target></Project>"
        $sha = New-ProbeRepo @{ "obj/x.props" = $props; "Directory.Build.props" = "<Project><Import Project=`"obj/x.props`" /></Project>"; "FlareAI.sln" = ""; "src/P/P.csproj" = "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>" }
        $ctx = New-JobContext -Root C:\HouseStyle -AgentSid $agentSid -Cache (New-RunCache C:\HouseStyle) -LimitMinutes 30
        Invoke-AgentJob $ctx @{ kind = "restore"; base = $sha; limitSeconds = 900 } | Out-Null
        $names = @(Get-ChildItem $drop -File -ErrorAction SilentlyContinue | ForEach-Object { (Get-Content $_.FullName -Raw).Trim() } | Select-Object -Unique)
        Import-Module C:\Setup\isolation\factory\Guards.psm1 -Force
        $rules = Import-PatchRules C:\Setup\isolation\factory\PatchRules.psd1
        $patchRefused = [bool](Test-PatchPath "src/P/obj/x.props" $rules)
        [pscustomobject]@{ pass = ($names -notcontains "factory" -and $patchRefused); detail = "canary ran as: $($names -join ', '); the same file as a patch refused: $patchRefused" }
    }
    "V18" = {
        $times = @(); $sha = $null
        foreach ($i in 1..20) { $t = Measure-Command { Invoke-ProbeJob "Describe 'x' { It 'runs' { 1 | Should Be 1 } }" | Out-Null }; $times += $t.TotalSeconds }
        $scan = Measure-Command { Get-UserWritable -Sid $agentSid -Quick | Out-Null }
        foreach ($i in 1..10) { $c = New-RunCache C:\HouseStyle; Lock-RunCache (New-JobContext -Root C:\HouseStyle -AgentSid $agentSid -Cache $c -LimitMinutes 5) | Out-Null; Remove-OldCaches C:\HouseStyle $c | Out-Null }
        $caches = @(Get-ChildItem C:\HouseStyle\nuget -Directory).Count
        [pscustomobject]@{ pass = ($caches -le 2); detail = "20 jobs: $([Math]::Round(($times | Measure-Object -Average).Average, 1)) s each; quick scan $([Math]::Round($scan.TotalSeconds)) s; $caches caches on disk; C: free $([Math]::Round((Get-PSDrive C).Free / 1GB, 1)) GB" }
    }
}

foreach ($id in $Probe) {
    if (@("V19", "V20", "V21", "V22") -contains $id) { continue }
    if (-not $probes.Contains($id)) { Write-Warning "No probe $id."; continue }
    try {
        $r = Invoke-VMProbe $probes[$id] @($mainSha)
        Add-Result $id ([bool]$r.pass) ("" + $r.detail)
    }
    catch { Add-Result $id $false "the probe couldn't run: $($_.Exception.Message)" }
}

# ==== V19: the Flare VM's checkpoints =================================================================================
if ($Probe -contains "V19") {
    try {
        $agentsPass = $cred.GetNetworkCredential().Password
        $forms = @($agentsPass, [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($agentsPass + "Password")), [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($agentsPass)))
        $snap = Get-VMSnapshot -VMName $FlareVM -Name "flare-check-base"
        if ((Get-VM $FlareVM).State -ne "Off") { throw "Turn $FlareVM off first: V19 reads flare-check-base's disk read-only." }
        $disk = @($snap.HardDrives | Select-Object -First 1)[0].Path
        $mounted = Mount-VHD -Path $disk -ReadOnly -Passthru
        try {
            $letter = @($mounted | Get-Disk | Get-Partition | Where-Object DriveLetter | Sort-Object Size -Descending | Select-Object -First 1).DriveLetter
            $hits = @()
            foreach ($f in @(Get-ChildItem "${letter}:\" -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Length -lt 50MB -and $_.FullName -notmatch '\\(WinSxS|Installer)\\' })) {
                try { $t = [IO.File]::ReadAllText($f.FullName) } catch { continue }
                foreach ($x in $forms) { if ($t.Contains($x)) { $hits += $f.FullName; break } }
            }
            $runnerFiles = Test-Path "${letter}:\actions-runner"
            $plugins = @(Get-ChildItem "${letter}:\Program Files*\MadCap Software\*\Flare.app\Plugins" -Recurse -Filter "FlareAI*.dll" -ErrorAction SilentlyContinue)
        }
        finally { Dismount-VHD -Path $disk }
        $expected = Join-Path $Root "factory-base-expected.json"
        $extra = "no recorded list yet"
        if (Test-Path $expected) {
            $want = Get-Content $expected -Raw | ConvertFrom-Json
            Restore-VMSnapshot -VMName $FlareVM -Name "factory-base" -Confirm:$false; Start-VM $FlareVM
            $fcred = Get-VMCredential $FlareVM
            $deadline = (Get-Date).AddMinutes(10); $now = $null
            do { Start-Sleep 10; try { $now = Invoke-Command -VMName $FlareVM -Credential $fcred -ErrorAction Stop { [pscustomobject]@{ tasks = @(Get-ScheduledTask | ForEach-Object { $_.TaskPath + $_.TaskName }); services = @(Get-Service | ForEach-Object Name) } } } catch { } } until ($now -or (Get-Date) -gt $deadline)
            $extra = (@($now.tasks | Where-Object { @($want.tasks) -notcontains $_ }) + @($now.services | Where-Object { @($want.services) -notcontains $_ })) -join ', '
        }
        Add-Result "V19" (-not $hits.Count -and -not $runnerFiles -and -not $plugins.Count -and -not $extra) "password found in $($hits.Count) files; runner files: $runnerFiles; House Style plugin files: $($plugins.Count); unexpected on factory-base: $(if ($extra) { $extra } else { 'none' })"
    }
    catch { Add-Result "V19" $false "the probe couldn't run: $($_.Exception.Message)" }
}

# ==== V20: a restore-first -Refresh of the agents VM =================================================================
if ($Probe -contains "V20") {
    try {
        $drop = Invoke-VMProbe { $j = Invoke-ProbeJob "Describe 'V20' { It 'drops a file in C:\Setup' { { Set-Content C:\Setup\hs-probe.txt 'x' -ErrorAction Stop } | Should Throw } }"; $r = @(Get-PesterRows $j); [pscustomobject]@{ pass = ($r.Count -and -not @($r | Where-Object { -not $_.passed }).Count); detail = "" } }
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Reset-FactoryVM.ps1") -Name $Name -Refresh
        $after = Invoke-Command -VMName $Name -Credential $cred { [pscustomobject]@{ carry = (Test-Path C:\HouseStyle\carry-done); saved = @(Get-ChildItem C:\HouseStyle\fix-state -Directory -ErrorAction SilentlyContinue).Count } }
        $runner = @(((& gh.exe api "repos/$Repo/actions/runners") | Out-String | ConvertFrom-Json).runners | Where-Object name -eq $Name)
        Add-Result "V20" ($drop.pass -and $after.carry -and $runner.Count -and $runner[0].status -eq "online") "drop in C:\Setup refused: $($drop.pass); carry-done: $($after.carry); saved fixes back: $($after.saved); runner: $(if ($runner.Count) { $runner[0].status } else { 'missing' })"
    }
    catch { Add-Result "V20" $false "the probe couldn't run: $($_.Exception.Message)" }
}

# ==== V21: the runner's labels and the fork-PR approval setting, through the API =====================================
if ($Probe -contains "V21") {
    try {
        Import-Module (Join-Path $PSScriptRoot "Controls.psm1") -Force
        $runner = @(((& gh.exe api "repos/$Repo/actions/runners") | Out-String | ConvertFrom-Json).runners | Where-Object name -eq $Name)
        $labels = @($runner | ForEach-Object { $_.labels.name } | Sort-Object)
        $approval = Test-ForkApproval ((& gh.exe api "repos/$Repo/actions/permissions/fork-pr-contributor-approval") | Out-String)
        Add-Result "V21" (($labels -join ',') -eq "agents,self-hosted,windows" -and -not $approval) "labels: $($labels -join ', '); fork-PR approval: $(if ($approval) { $approval } else { 'all_external_contributors' })"
    }
    catch { Add-Result "V21" $false "the probe couldn't run: $($_.Exception.Message)" }
}

# ==== V22: from the host =============================================================================================
if ($Probe -contains "V22") {
    try {
        $ip = @((Get-VMNetworkAdapter -VMName $Name).IPAddresses | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' } | Select-Object -First 1)[0]
        $open = @(foreach ($port in 445, 139, 135, 5985, 5986, 3389) { $c = New-Object Net.Sockets.TcpClient; try { if ($c.ConnectAsync($ip, $port).Wait(3000) -and $c.Connected) { $port } } catch { } finally { $c.Dispose() } })
        # A plain (non-elevated) session of the owner's: runas /trustlevel gives a basic-user token.
        $probeFile = Join-Path ([IO.Path]::GetTempPath()) "hs-v22.txt"
        $targets = @(Get-ChildItem $Root -Filter "*-credential.xml" -File | ForEach-Object FullName) + @((Join-Path $Root "runner-vault"), (Join-Path $Root "scratch"))
        $cmd = "powershell.exe -NoProfile -Command `"`$r = foreach (`$t in '$($targets -join "','")') { try { if (Test-Path `$t -PathType Container) { Get-ChildItem `$t -ErrorAction Stop | Out-Null } else { [IO.File]::ReadAllBytes(`$t) | Out-Null }; 'read ' + `$t } catch { } }; Set-Content '$probeFile' (`$r -join ';')`""
        Remove-Item $probeFile -ErrorAction SilentlyContinue
        & runas.exe /trustlevel:0x20000 $cmd | Out-Null
        Start-Sleep 10
        $read = if (Test-Path $probeFile) { (Get-Content $probeFile -Raw).Trim() } else { "(no answer)" }
        Add-Result "V22" (-not $open.Count -and -not $read) "open ports to the agents VM: $(if ($open.Count) { $open -join ', ' } else { 'none' }); a plain session read: $(if ($read) { $read } else { 'nothing' }). This shows the files aren't open to a plain session, not that they resist admin code (gap 5)."
    }
    catch { Add-Result "V22" $false "the probe couldn't run: $($_.Exception.Message)" }
}

$log = Join-Path $Root "factory-logs\isolation-$(Get-Date -Format yyyy-MM-dd-HHmm).json"
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
$results | ConvertTo-Json -Depth 4 | Set-Content $log -Encoding UTF8
$failed = @($results | Where-Object { -not $_.ok })
Write-Host "$($results.Count - $failed.Count) of $($results.Count) probes as expected$(if ($Before) { ' (before the ACLs: V4, V5, V6 and V13 must fail)' }). Log: $log"
if ($failed.Count) { exit 1 }
