<#
.SYNOPSIS The agent task's launcher: runs one factory job as the agent user (factory-runtime spec section 2, step 6).
.DESCRIPTION Registered once by Install-FactoryTools.ps1 -NoFlare as the action of the task "House Style agent", which
runs as hsagent with S4U, Limited, one instance at a time, from C:\HouseStyle\agent-runner (Administrators write).
The script user's Invoke-AgentJob writes the job and starts the task; this script:
1. deletes the other folders under agent-work, and stops if a delete fails;
2. checks next.txt and job.json;
3. reads signin.txt and deletes it, or stops;
4. clones repo\ from the run's bundle, checks out the job's base, and for test and review jobs applies the patch;
5. joins a Job Object with KILL_ON_JOB_CLOSE and no breakaway;
6. builds the child's environment from a fixed list, never from its own;
7. runs one program with an argument array (claude.exe, the base copy of test-channel.ps1, or dotnet restore), with
   stdout and stderr in out\, opened with no write sharing;
8. ends everything else in the Job Object when the child exits or the limit passes;
9. for a fix job, writes out\fix.patch, commits.txt and status.txt with exactly
   git diff --no-ext-diff --no-textconv --no-renames --src-prefix=a/ --dst-prefix=b/ <base> HEAD;
10. exits with the child's code, or 259 on a timeout.
Its own facts go in out\launcher.json. -Root and the program paths are for its tests; the task passes none of them.
#>
param(
    [string]$Root = "C:\HouseStyle",
    [string]$Claude = "C:\Tools\claude\claude.exe",
    [string]$Dotnet = "C:\Program Files\dotnet\dotnet.exe",
    [string]$PowerShellExe = (Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"),
    [string]$Git = "C:\Program Files\Git\cmd\git.exe"
)
$ErrorActionPreference = "Stop"
$launchStart = Get-Date   # the job's limit runs from here, as the script's deadline runs from the task's start
$Kinds = "triage", "cover", "fix", "review", "test", "restore"
$AgentKinds = "triage", "cover", "fix", "review"
$jobs = Join-Path $Root "agent-jobs"; $work = Join-Path $Root "agent-work"
# The exit code that means the launcher stopped before it ran anything (step 7). Only Stop-Launch exits with it: a
# child that exits with it is reported as 1, so the script side can trust it as a launch failure.
$LaunchFailedCode = 18515
function Stop-Launch([string]$why) { [Console]::Error.WriteLine("The job stopped: $why"); exit $LaunchFailedCode }
# cmd's rmdir and del never follow a link; their exit codes aren't trusted, only whether the path is gone.
function Remove-Quietly([string]$path, [bool]$dir) {
    $ErrorActionPreference = "Continue"
    if ($dir) { & cmd.exe /c rmdir /s /q $path 2>&1 | Out-Null } else { & cmd.exe /c del /f /q /a $path 2>&1 | Out-Null }
}

# ---- 1. Only this job's folder under agent-work ---------------------------------------------------------------------
$id = $null
try { $id = ([IO.File]::ReadAllText((Join-Path $jobs "next.txt"))).Trim() } catch { Stop-Launch "there's no next.txt" }
$g = [guid]::Empty
if (-not [guid]::TryParse($id, [ref]$g) -or $id -notmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') { Stop-Launch "next.txt doesn't hold a job id" }
foreach ($d in @(Get-ChildItem -LiteralPath $work -Force -ErrorAction SilentlyContinue)) {
    if ($d.Name -eq $id) { continue }
    Remove-Quietly $d.FullName $d.PSIsContainer
    if (Test-Path -LiteralPath $d.FullName) { Stop-Launch "an earlier job's folder in agent-work couldn't be deleted" }
}

# ---- 2. The job -----------------------------------------------------------------------------------------------------
$jobDir = Join-Path $jobs $id
try { $job = [IO.File]::ReadAllText((Join-Path $jobDir "job.json")) | ConvertFrom-Json } catch { Stop-Launch "job.json can't be read" }
if (("" + $job.id) -ne $id) { Stop-Launch "job.json is for another job" }
if ($Kinds -notcontains ("" + $job.kind)) { Stop-Launch "job.json's kind isn't one the factory runs" }
if (("" + $job.base) -notmatch '^[0-9a-f]{40}$') { Stop-Launch "job.json's base isn't a commit" }
if (("" + $job.bundle) -notmatch '^[0-9a-f]{40}$') { Stop-Launch "job.json's bundle isn't a commit" }
$limit = [int]$job.limitSeconds
if ($limit -lt 1 -or $limit -gt 6 * 3600) { Stop-Launch "job.json's limit isn't usable" }
$isAgent = $AgentKinds -contains $job.kind

# ---- 3. The sign-in, read once and deleted ---------------------------------------------------------------------------
$token = $null
$signin = Join-Path $jobDir "signin.txt"
if ($isAgent) {
    try { $token = ([IO.File]::ReadAllText($signin)).Trim() } catch { Stop-Launch "signin.txt can't be read" }
    try { [IO.File]::Delete($signin) } catch { }
    if (Test-Path -LiteralPath $signin) { Stop-Launch "signin.txt couldn't be deleted" }
    if (-not $token) { Stop-Launch "signin.txt is empty" }
}

function Join-Args([string[]]$a) {
    # Quoted the way Windows programs read them back (Join-ProcessArguments in Controls.psm1).
    @(foreach ($x in $a) {
        if ($x -ne "" -and $x -notmatch '[\s"]') { $x; continue }
        $s = '"'; $slashes = 0
        foreach ($ch in $x.ToCharArray()) {
            if ($ch -eq '\') { $slashes++; continue }
            if ($ch -eq '"') { $s += ('\' * ($slashes * 2 + 1)) + '"' } else { $s += ('\' * $slashes) + $ch }
            $slashes = 0
        }
        $s + ('\' * ($slashes * 2)) + '"'
    }) -join " "
}

# ---- 4. repo\ from the base bundle ----------------------------------------------------------------------------------
$me = Join-Path $work $id
$repo = Join-Path $me "repo"; $home_ = Join-Path $me "home"; $out = Join-Path $me "out"
New-Item -ItemType Directory -Force $me, $home_, $out | Out-Null
$hooks = Join-Path $me "no-hooks"; New-Item -ItemType Directory -Force $hooks | Out-Null
$gitSafety = @("-c", "core.hooksPath=$hooks", "-c", "core.fsmonitor=false")
$env:GIT_NO_REPLACE_OBJECTS = "1"
# The run's bundle, which holds main's history and so every saved fix's base; the checkout is the job's base.
$bundle = Join-Path $Root "agent-in\base-$($job.bundle).bundle"
# git with a time limit, so a hung clone or apply can't run past the job's own limit.
function Invoke-Git([string[]]$a, [int]$seconds = 600) {
    $g = New-Object Diagnostics.ProcessStartInfo $Git
    $g.Arguments = Join-Args (@($gitSafety) + $a); $g.UseShellExecute = $false; $g.CreateNoWindow = $true; $g.RedirectStandardOutput = $true; $g.RedirectStandardError = $true
    $p = [Diagnostics.Process]::Start($g); $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($seconds * 1000)) { try { $p.Kill() } catch { }; Stop-Launch "git $($a[0]) took over $seconds seconds" }
    if ($p.ExitCode) { Stop-Launch "git $($a[0]) failed" }
}
Invoke-Git @("clone", "--quiet", "--no-checkout", $bundle, $repo)
Invoke-Git @("-C", $repo, "-c", "advice.detachedHead=false", "checkout", "--quiet", "--detach", $job.base)
if (@("test", "review") -contains $job.kind -and $job.patch) {
    if (("" + $job.patch) -notmatch '^[0-9a-f-]{36}\.patch$') { Stop-Launch "job.json's patch isn't a patch the factory wrote" }
    Invoke-Git @("-C", $repo, "apply", "--index", (Join-Path $Root "agent-in\$($job.patch)"))
}

# ---- 5. The Job Object ----------------------------------------------------------------------------------------------
if (-not ("HsJobObject" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class HsJobObject {
    [StructLayout(LayoutKind.Sequential)] struct Basic { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic BasicLimitInformation; public Io IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr a, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref Extended info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(IntPtr job, int cls, IntPtr info, uint len, IntPtr ret);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    public static IntPtr Handle;
    // A job that kills everything in it when its last handle closes, with no breakaway, and this process in it.
    public static void Join() {
        Handle = CreateJobObject(IntPtr.Zero, null);
        if (Handle == IntPtr.Zero) throw new Exception("CreateJobObject " + Marshal.GetLastWin32Error());
        Extended e = new Extended(); e.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE, and no BREAKAWAY_OK
        if (!SetInformationJobObject(Handle, 9, ref e, (uint)Marshal.SizeOf(typeof(Extended)))) throw new Exception("SetInformationJobObject " + Marshal.GetLastWin32Error());
        if (!AssignProcessToJobObject(Handle, GetCurrentProcess())) throw new Exception("AssignProcessToJobObject " + Marshal.GetLastWin32Error());
    }
    public static int[] Pids() {
        int size = 8 + 8 * 4096; IntPtr buf = Marshal.AllocHGlobal(size);
        try {
            if (!QueryInformationJobObject(Handle, 3, buf, (uint)size, IntPtr.Zero)) return new int[0];
            int n = Marshal.ReadInt32(buf, 4); List<int> r = new List<int>();
            for (int i = 0; i < n; i++) r.Add((int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size));
            return r.ToArray();
        } finally { Marshal.FreeHGlobal(buf); }
    }
}
"@
}
try { [HsJobObject]::Join() } catch { Stop-Launch "the Job Object couldn't be made ($($_.Exception.Message))" }
function Stop-Others {
    foreach ($p in [HsJobObject]::Pids()) { if ($p -ne $PID) { try { Stop-Process -Id $p -Force -ErrorAction Stop } catch { } } }
    $deadline = (Get-Date).AddSeconds(30)
    while (@([HsJobObject]::Pids() | Where-Object { $_ -ne $PID }).Count -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
}

# ---- 6. The child's environment, from a fixed list ------------------------------------------------------------------
$psi = New-Object Diagnostics.ProcessStartInfo
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.RedirectStandardInput = $true
$psi.WorkingDirectory = $repo
$psi.EnvironmentVariables.Clear()
foreach ($n in "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "PATH", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "PSModulePath",
               "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "USERNAME", "COMPUTERNAME") {
    # The machine's value where Windows keeps one (PATH is the machine's, never the user's), else the system's own.
    $v = [Environment]::GetEnvironmentVariable($n, "Machine")
    if (-not $v) { $v = [Environment]::GetEnvironmentVariable($n, "Process") }
    if ($v) { $psi.EnvironmentVariables[$n] = $v }
}
$appdata = Join-Path $home_ "AppData\Roaming"; $local = Join-Path $home_ "AppData\Local"; $temp = Join-Path $home_ "Temp"
New-Item -ItemType Directory -Force $appdata, $local, $temp, (Join-Path $appdata "NuGet"), (Join-Path $home_ ".claude") | Out-Null
# A NuGet config with no sources: only the restore job reaches NuGet, with its own config.
[IO.File]::WriteAllText((Join-Path $appdata "NuGet\NuGet.Config"), "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<configuration>`n  <packageSources>`n    <clear />`n  </packageSources>`n</configuration>`n")
foreach ($kv in @{ USERPROFILE = $home_; HOME = $home_; APPDATA = $appdata; LOCALAPPDATA = $local; CLAUDE_CONFIG_DIR = (Join-Path $home_ ".claude"); DOTNET_CLI_HOME = $home_; TEMP = $temp; TMP = $temp }.GetEnumerator()) {
    $psi.EnvironmentVariables[$kv.Key] = $kv.Value
}
$psi.EnvironmentVariables["HOUSESTYLE_TEST_REPO"] = $repo
$psi.EnvironmentVariables["NUGET_PACKAGES"] = "" + $job.cache
$psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
$psi.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1"
$psi.EnvironmentVariables["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"
$psi.EnvironmentVariables["HOUSESTYLE_AGENT_CHANNELS"] = "" + $job.channels
if ($job.kind -eq "restore") { $psi.EnvironmentVariables["NUGET_CERT_REVOCATION_MODE"] = "offline" }
if ($isAgent) {
    $psi.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"] = $token
    $psi.EnvironmentVariables["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1"
    $psi.EnvironmentVariables["DISABLE_AUTOUPDATER"] = "1"
    $bash = "C:\Program Files\Git\bin\bash.exe"
    $onPath = @(("" + $psi.EnvironmentVariables["PATH"]).Split(';') | Where-Object { $_ -and (Test-Path (Join-Path $_ "bash.exe")) }).Count
    if (-not $onPath -and (Test-Path $bash)) { $psi.EnvironmentVariables["CLAUDE_CODE_GIT_BASH_PATH"] = $bash }
}
$token = $null

# ---- 7. One program, with an argument array --------------------------------------------------------------------------
$stdinFile = $null
switch ($job.kind) {
    { $AgentKinds -contains $_ } {
        $psi.FileName = $Claude
        $argv = @($job.claudeArgs | ForEach-Object { ("" + $_).Replace("{{JOB}}", $jobDir) })
        $stdinFile = Join-Path $jobDir "prompt.txt"
    }
    "test" {
        $psi.FileName = $PowerShellExe
        $channel = Join-Path $Root "agent-tools\$($job.base)\scripts\agent\test-channel.ps1"
        $argv = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $channel, "-Channel", ("" + $job.channel), "-RepoRoot", $repo, "-ResultsDir", (Join-Path $out "results"))
        if ($job.filter) { $argv += @("-Filter", ("" + $job.filter)) }
        if ($job.exact -eq $true) { $argv += "-Exact" }
        if ($job.path) { $argv += @("-Path", ("" + $job.path)) }
    }
    "restore" {
        $psi.FileName = $Dotnet
        $argv = @("restore", "FlareAI.sln", "--configfile", (Join-Path $jobDir "nuget.config"))
    }
}
$psi.Arguments = Join-Args $argv
# stdout and stderr: opened here with no write sharing, so nothing else can write them.
$stdout = [IO.File]::Open((Join-Path $out "stdout.txt"), "CreateNew", "Write", "Read")
$stderr = [IO.File]::Open((Join-Path $out "stderr.txt"), "CreateNew", "Write", "Read")
$signinAtStart = Test-Path -LiteralPath $signin
$started = Get-Date
$child = [Diagnostics.Process]::Start($psi)
$copyOut = $child.StandardOutput.BaseStream.CopyToAsync($stdout)
$copyErr = $child.StandardError.BaseStream.CopyToAsync($stderr)
try {
    if ($stdinFile -and (Test-Path -LiteralPath $stdinFile)) { $b = [IO.File]::ReadAllBytes($stdinFile); $child.StandardInput.BaseStream.Write($b, 0, $b.Length) }
} catch { }
try { $child.StandardInput.Close() } catch { }

# ---- 8. The limit, then nothing left running ------------------------------------------------------------------------
# What's left of the limit after the setup above (the clone, the checkout and the patch), so the script's stop at
# task start + limit + grace never lands before step 9.
$left = [Math]::Max(1, $limit - [int]((Get-Date) - $launchStart).TotalSeconds)
$timedOut = -not $child.WaitForExit($left * 1000)
Stop-Others
$code = if ($timedOut) { 259 } else { $child.ExitCode }
try { [void]$copyOut.Wait(30000); [void]$copyErr.Wait(30000) } catch { }
$stdout.Dispose(); $stderr.Dispose()

# ---- 9. A fix job's patch, commit messages and status ---------------------------------------------------------------
function Save-GitOutput([string[]]$a, [string]$file) {
    $g = New-Object Diagnostics.ProcessStartInfo $Git
    $g.Arguments = Join-Args (@($gitSafety) + $a); $g.WorkingDirectory = $repo
    $g.UseShellExecute = $false; $g.CreateNoWindow = $true; $g.RedirectStandardOutput = $true; $g.RedirectStandardError = $true
    $g.EnvironmentVariables["GIT_NO_REPLACE_OBJECTS"] = "1"
    $p = [Diagnostics.Process]::Start($g)
    $f = [IO.File]::Open($file, "CreateNew", "Write", "Read")
    try { $t = $p.StandardOutput.BaseStream.CopyToAsync($f); $e = $p.StandardError.ReadToEndAsync(); if (-not $p.WaitForExit(120000)) { try { $p.Kill() } catch { }; return -1 }; [void]$t.Wait(60000) } finally { $f.Dispose() }
    $p.ExitCode
}
$gitCodes = @{}
if ($job.kind -eq "fix") {
    $gitCodes.diff = Save-GitOutput @("diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--src-prefix=a/", "--dst-prefix=b/", $job.base, "HEAD") (Join-Path $out "fix.patch")
    $gitCodes.log = Save-GitOutput @("log", "--format=%B%x00", "$($job.base)..HEAD") (Join-Path $out "commits.txt")
    $gitCodes.status = Save-GitOutput @("status", "--porcelain=v1", "--untracked-files=all") (Join-Path $out "status.txt")
}
$facts = [ordered]@{ id = $id; kind = "" + $job.kind; exit = $code; timedOut = $timedOut; seconds = [int]((Get-Date) - $started).TotalSeconds; signinAtChildStart = $signinAtStart; git = $gitCodes }
[IO.File]::WriteAllText((Join-Path $out "launcher.json"), ($facts | ConvertTo-Json -Compress))

# ---- 10. The child's code, or 259 on a timeout ----------------------------------------------------------------------
if (-not $timedOut -and $code -eq $LaunchFailedCode) { $code = 1 }   # only Stop-Launch says the launch failed
exit $code
