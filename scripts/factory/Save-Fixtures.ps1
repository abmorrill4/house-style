<#
.SYNOPSIS Captures real output from the pinned tools into tests/factory/fixtures, redacted, for the factory's parser
tests (factory-runtime spec section 12).
.DESCRIPTION A setup script run by hand, never from a task. Each -Set captures one fixture, runs the real command the
factory's code reads, and writes <name>.json with the tool, its version, the machine, the commands, and each case's
exit code, stdout and stderr. The version must match scripts\factory\Pins.psd1: a capture from any other version is
refused, so a pin change means capturing again. Everything is redacted before it's written: token shapes, report text
and private paths, computer and user names, SIDs, IP and MAC addresses, switch names, E:\HouseStyle paths, and license
keys and licensee details.

Sets that run here, on any machine with the pinned tool: git-patch-forms, git-patch-refused, git-apply-check,
git-config-scopes, git-diff-name-only, git-worktree (all with -Set git), and trx.
Sets that run in a VM over PowerShell Direct (elevated, on the host, with -VMName): robocopy-exit-codes, rmdir-icacls,
get-process-owner, get-netfirewallrule, get-scheduledtaskinfo, sc-sdshow, wevtutil-gl and task-events on the agents VM;
flare-trial-active, flare-trial-expired and flare-licensed on the Flare VM or its scratch copy (the copy's disk under
vm\scratch).
Sets that run on the host: hyperv-shapes, psdirect-failures and vmconnect-process.
Sets that read GitHub with the owner's gh sign-in: runner-state, fork-pr-approval and label-events. They capture the
state as it is now under -Case, and refuse a -Case that doesn't match what they see.
Sets that import output captured elsewhere, with -From: claude (claude-success, claude-max-turns and claude-error from
the capture dispatch's factory-logs\fixtures folder), claude-usage-limit (from factory-logs\cli-errors) and
harness-results (results.json files from e2e runs, with -Case pass, fail or vm-retry).
.EXAMPLE powershell -File scripts\factory\Save-Fixtures.ps1 -Set git
.EXAMPLE scripts\factory\Save-Fixtures.ps1 -Set robocopy-exit-codes -VMName HSAGENTS
#>
param(
    [Parameter(Mandatory)][string[]]$Set,
    [string]$VMName,
    [string]$Case,
    [string]$From,
    [int]$Report,                                # label-events: the private report whose events to read
    [string]$Repo = "abmorrill4/house-style",
    [string]$ReportsRepo = "abmorrill4/house-style-reports",
    [string]$RunnerName = "HSFACTORY",
    [string]$Root = "E:\HouseStyle\vm",
    [string]$Out   # default: tests\factory\fixtures in this checkout
)
$ErrorActionPreference = "Stop"
if (-not $Out) { $Out = Join-Path (Split-Path (Split-Path $PSScriptRoot)) "tests\factory\fixtures" }
Import-Module (Join-Path $PSScriptRoot "Controls.psm1") -Force   # Join-ProcessArguments
$pins = Import-PowerShellDataFile (Join-Path $PSScriptRoot "Pins.psd1")

# ==== Redaction =======================================================================================================
$script:Names = @(@($env:COMPUTERNAME, $env:USERNAME, $env:USERDOMAIN) | Where-Object { $_ -and $_.Length -ge 3 } | Select-Object -Unique)
$script:IpMap = @{}
function Protect-FixtureText([string]$t, [switch]$Network) {
    if ($null -eq $t) { return $null }
    foreach ($p in '\bsk-ant-[A-Za-z0-9_-]{8,}', '\bsk-[A-Za-z0-9_-]{20,}', '\bgh[pousr]_[A-Za-z0-9]{20,}', '\bgithub_pat_[A-Za-z0-9_]{20,}',
                   '\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}', '(?i)\b(bearer|basic|token)\s+[A-Za-z0-9._~+/=-]{20,}') {
        $t = [regex]::Replace($t, $p, '_redacted-token_')
    }
    $t = [regex]::Replace($t, '(?i)\b[A-Z]:[\\/]+HouseStyle\b', '_project_')
    $t = [regex]::Replace($t, '(?i)(\b[A-Z]:[\\/]+Users[\\/]+)(?!Public\b|Default\b|factory\b|hsagent\b)[^\\/\s"''<>]+', '${1}_user_')
    $t = [regex]::Replace($t, '(?i)(/[a-z]/Users/)(?!Public\b|Default\b|factory\b|hsagent\b)[^/\s"''<>]+', '${1}_user_')
    foreach ($n in $script:Names) { $t = [regex]::Replace($t, "(?i)(?<![A-Za-z0-9])$([regex]::Escape($n))(?![A-Za-z0-9])", '_name_') }
    $t = [regex]::Replace($t, '\bS-1-5-21-\d+-\d+-\d+', 'S-1-5-21-0-0-0')
    $t = [regex]::Replace($t, '(?i)\b[\w.+-]+@(?!example\.invalid\b|users\.noreply\.github\.com\b)[\w-]+(\.[\w-]+)+', '_email_')
    $t = [regex]::Replace($t, '\b[A-Z0-9]{4,6}(-[A-Z0-9]{4,6}){3,}\b', '_license-key_')
    if ($Network) {
        $t = [regex]::Replace($t, '(?i)\b([0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b|\b00155D[0-9A-F]{6}\b', '_mac_')
        $t = [regex]::Replace($t, '\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b', {
            param($m)
            $ip = $m.Value
            if ($ip -match '^(127\.|0\.0\.0\.0$|255\.255\.|192\.0\.2\.|198\.51\.100\.|203\.0\.113\.)') { return $ip }
            if (-not $script:IpMap.ContainsKey($ip)) { $script:IpMap[$ip] = "203.0.113.$(1 + $script:IpMap.Count % 254)" }
            $script:IpMap[$ip]
        })
    }
    $t
}
function Protect-Value($v, [switch]$Network) {
    if ($null -eq $v) { return $null }
    if ($v -is [string]) { return (Protect-FixtureText $v -Network:$Network) }
    $v
}

# ==== Writing a fixture ===============================================================================================
# $cases: case, command, exit, stdout, stderr and anything else the parser reads. With -Merge, an existing fixture of
# the same version keeps its other cases (runner-state and fork-pr-approval are captured one state at a time).
function Write-Fixture([string]$name, [string]$tool, [string]$version, [string]$machine, $cases, [switch]$Network, [switch]$Merge) {
    if (-not $pins.ContainsKey($tool) -or -not $pins[$tool]) { throw "Pins.psd1 has no pin for $tool. Set it to $version (what this machine has) if that's the version setup installs, then capture again." }
    if ($pins[$tool] -ne $version) { throw "$tool is $version here, but Pins.psd1 pins $($pins[$tool]). Capture on a machine with the pinned version." }
    $clean = @(foreach ($c in @($cases)) {
        $o = [ordered]@{}
        $keys = @(@("case", "command", "exit", "stdout", "stderr") | Where-Object { $c.ContainsKey($_) }) + @($c.Keys | Where-Object { @("case", "command", "exit", "stdout", "stderr") -notcontains $_ } | Sort-Object)
        foreach ($p in $keys) { $o[$p] = Protect-Value $c[$p] -Network:$Network }
        [pscustomobject]$o
    })
    $path = Join-Path $Out "$name.json"
    if ($Merge -and (Test-Path $path)) {
        $old = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($old.version -eq $version) { $clean = @(@($old.cases | Where-Object { @($clean.case) -notcontains $_.case }) + $clean) }
    }
    $f = [ordered]@{ name = $name; tool = $tool; version = $version; machine = $machine; capturedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"); redacted = $true; cases = $clean }
    New-Item -ItemType Directory -Force $Out | Out-Null
    [IO.File]::WriteAllText($path, ($f | ConvertTo-Json -Depth 8) + "`n", (New-Object Text.UTF8Encoding $false))
    Write-Host "Wrote $name ($(@($clean).Count) cases, $tool $version, $machine)."
}

# ==== Running a program, with its output exactly as it wrote it ======================================================
function Invoke-Raw([string]$exe, [string[]]$argv, [string]$cwd, [hashtable]$envs = @{}) {
    $psi = New-Object Diagnostics.ProcessStartInfo $exe
    $psi.Arguments = Join-ProcessArguments $argv
    $psi.WorkingDirectory = $cwd
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($k in $envs.Keys) { $psi.EnvironmentVariables[$k] = $envs[$k] }
    $p = [Diagnostics.Process]::Start($psi)
    $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    [pscustomobject]@{ exit = $p.ExitCode; stdout = $o.Result; stderr = $e.Result }
}
function New-Scratch([string]$what) {
    $d = Join-Path ([IO.Path]::GetTempPath()) "hs-fixtures-$what-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    New-Item -ItemType Directory -Force $d | Out-Null
    $d
}

# ==== git (here) ======================================================================================================
# The launcher's exact command, with the script's git overrides: no hooks, no fsmonitor, no replace objects. The global
# config is left out, so the owner's own git settings never shape the output (the agent user has none either).
function Save-GitFixtures {
    $ver = ((Invoke-Raw "git.exe" @("--version") $env:TEMP).stdout -replace '^git version\s+', '').Trim()
    $work = New-Scratch "git"
    $hooks = Join-Path $work "no-hooks"; New-Item -ItemType Directory -Force $hooks | Out-Null
    $globalCfg = Join-Path $work "empty-global"; [IO.File]::WriteAllText($globalCfg, "")
    $envs = @{ GIT_NO_REPLACE_OBJECTS = "1"; GIT_CONFIG_GLOBAL = $globalCfg }
    $repo = Join-Path $work "repo"; New-Item -ItemType Directory -Force $repo | Out-Null
    function G([string[]]$a, [string]$dir = $repo) {
        $r = Invoke-Raw "git.exe" (@("-c", "core.hooksPath=$hooks", "-c", "core.fsmonitor=false") + $a) $dir $envs
        $r
    }
    function GOk([string[]]$a, [string]$dir = $repo) { $r = G $a $dir; if ($r.exit) { throw "git $($a -join ' ') failed: $($r.stderr)" }; $r.stdout.Trim() }
    function Put([string]$p, [string]$text) { $f = Join-Path $repo $p; New-Item -ItemType Directory -Force (Split-Path $f) | Out-Null; [IO.File]::WriteAllText($f, $text) }
    $launcher = @("diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--src-prefix=a/", "--dst-prefix=b/")
    try {
        GOk @("init", "-q") | Out-Null
        GOk @("config", "user.name", "fixture") | Out-Null; GOk @("config", "user.email", "fixture@example.invalid") | Out-Null
        GOk @("config", "core.autocrlf", "false") | Out-Null
        Put "src/App/Thing.cs" "class Thing`n{`n    int A() => 1;`n    int B() => 2;`n}`n"
        Put "src/App/Old.cs" "class Old`n{`n    int C() => 3;`n}`n"
        Put "src/App/Gone.cs" "class Gone`n{`n}`n"
        Put "src/App/Empty.cs" ""
        Put "src/App/NoEol.cs" "class NoEol { }"
        Put "src/App/Crlf.cs" "class Crlf`r`n{`r`n    int D() => 4;`r`n}`r`n"
        Put "src/App/App.csproj" "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <ItemGroup>`n    <PackageReference Include=`"Newtonsoft.Json`" Version=`"13.0.1`" />`n  </ItemGroup>`n</Project>`n"
        Put "changes/old-note.md" "- An older note.`n"
        GOk @("add", "-A") | Out-Null; GOk @("commit", "-q", "-m", "base") | Out-Null
        $base = GOk @("rev-parse", "HEAD")

        # One commit on top of the base per case, and the diff the factory would read.
        $script:gitCases = @()
        function Case([string]$name, [scriptblock]$change, [string[]]$diffArgs = $launcher, [string]$fixture = "forms") {
            GOk @("checkout", "-q", "--detach", $base) | Out-Null
            GOk @("clean", "-xffdq") | Out-Null
            & $change
            GOk @("commit", "-q", "--allow-empty", "-m", $name) | Out-Null
            $r = G (@($diffArgs) + @($base, "HEAD"))
            $script:gitCases += @{ fixture = $fixture; case = $name; command = "git $(($diffArgs) -join ' ') <base> HEAD"; exit = $r.exit; stdout = $r.stdout; stderr = $r.stderr }
        }
        function AddAll { GOk @("add", "-A") | Out-Null }
        Case "change" { Put "src/App/Thing.cs" "class Thing`n{`n    int A() => 1;`n    int B() => 22;`n}`n"; AddAll }
        Case "new" { Put "src/App/NewThing.cs" "class NewThing`n{`n}`n"; AddAll }
        Case "new-empty" { Put "src/App/Blank.cs" ""; AddAll }
        Case "deleted" { Remove-Item (Join-Path $repo "src/App/Gone.cs"); AddAll }
        Case "deleted-empty" { Remove-Item (Join-Path $repo "src/App/Empty.cs"); AddAll }
        Case "several" {
            Put "src/App/Thing.cs" "class Thing`n{`n    int A() => 11;`n    int B() => 2;`n}`n"; Put "tests/App.Tests/ThingTests.cs" "class ThingTests`n{`n}`n"
            Put "changes/fix-thing.md" "- Thing counts right.`n"; Remove-Item (Join-Path $repo "src/App/Gone.cs"); AddAll
        }
        Case "no-newline-at-end" { Put "src/App/NoEol.cs" "class NoEol { int E() => 5; }"; AddAll }
        Case "crlf-lines" { Put "src/App/Crlf.cs" "class Crlf`r`n{`r`n    int D() => 44;`r`n}`r`n"; AddAll }
        Case "content-looks-like-headers" { Put "src/App/Thing.cs" "class Thing`n{`ndiff --git a/x b/x`n+++ b/x`n--- a/x`n@@ -1 +1 @@`n    int B() => 2;`n}`n"; AddAll }
        # Refused shapes.
        Case "mode-change" { GOk @("update-index", "--chmod=+x", "src/App/Thing.cs") | Out-Null } -fixture "refused"
        Case "exec-bit" { Put "src/App/Run.cs" "class Run { }`n"; GOk @("add", "src/App/Run.cs") | Out-Null; GOk @("update-index", "--chmod=+x", "src/App/Run.cs") | Out-Null } -fixture "refused"
        Case "symlink" {
            $tmp = Join-Path $work "link-target.txt"; [IO.File]::WriteAllText($tmp, "Thing.cs")
            $blob = GOk @("hash-object", "-w", $tmp)
            GOk @("update-index", "--add", "--cacheinfo", "120000,$blob,src/App/Link.cs") | Out-Null
        } -fixture "refused"
        Case "binary" { [IO.File]::WriteAllBytes((Join-Path $repo "src/App/Data.cs"), [byte[]](0, 1, 2, 0, 255, 10)); AddAll } -fixture "refused"
        Case "rename" { GOk @("mv", "src/App/Old.cs", "src/App/Renamed.cs") | Out-Null } -diffArgs @("diff", "--no-ext-diff", "--no-textconv", "-M", "--src-prefix=a/", "--dst-prefix=b/") -fixture "refused"
        Case "no-prefix" { Put "src/App/Thing.cs" "class Thing`n{`n    int A() => 1;`n    int B() => 23;`n}`n"; AddAll } -diffArgs @("diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--no-prefix") -fixture "refused"
        Case "package-change" { Put "src/App/App.csproj" "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <ItemGroup>`n    <PackageReference Include=`"Newtonsoft.Json`" Version=`"13.0.3`" />`n  </ItemGroup>`n</Project>`n"; AddAll } -fixture "refused"
        $denied = "src/App/App.csproj.user", "src/App/bin/Out.cs", "src/App/obj/x.props", "tests/Directory.Build.props", "src/App/Build.targets", "src/App/csc.rsp",
                  "src/App/Lib.vbproj", "src/App/Shared.projitems", "src/Directory.Packages.props", "src/Directory.Solution.props", "src/App.sln", "src/global.json",
                  "src/nuget.config", "src/App/packages.lock.json", "src/CLAUDE.md", "src/.claude/settings.json", "src/.gitattributes", "src/.gitmodules",
                  "src/.lfsconfig", "tests/App.runsettings", "tests/xunit.runner.json", "src/.editorconfig", "tests/e2e/Run-FlareE2E.ps1", "tests/e2e/FlareE2E.psm1",
                  "tests/e2e/flaui/Deps.cs", "src/App/Tool.CSPROJ", "scripts/factory/Extra.ps1", "rules/routing.json", "README.md"
        foreach ($d in $denied) { $script:curPath = $d; Case "path:$d" { Put $script:curPath "x`n"; AddAll } -fixture "refused" }

        $forms = @($script:gitCases | Where-Object { $_.fixture -eq "forms" } | ForEach-Object { $_.Remove("fixture"); $_ })
        $refused = @($script:gitCases | Where-Object { $_.fixture -eq "refused" } | ForEach-Object { $_.Remove("fixture"); $_ })
        Write-Fixture "git-patch-forms" "git" $ver "host" $forms
        Write-Fixture "git-patch-refused" "git" $ver "host" $refused

        # git apply --check of the "change" patch: on the base (ok), and on a base whose line moved (conflict).
        $patch = Join-Path $work "change.patch"
        [IO.File]::WriteAllText($patch, ($forms | Where-Object { $_.case -eq "change" }).stdout, (New-Object Text.UTF8Encoding $false))
        GOk @("checkout", "-q", "--detach", $base) | Out-Null
        $ok = G @("apply", "--check", $patch)
        Put "src/App/Thing.cs" "class Thing`n{`n    int A() => 1;`n    int B() => 3;`n}`n"; GOk @("commit", "-q", "-am", "moved") | Out-Null
        $bad = G @("apply", "--check", $patch)
        Write-Fixture "git-apply-check" "git" $ver "host" @(
            @{ case = "ok"; command = "git apply --check <patch>"; exit = $ok.exit; stdout = $ok.stdout; stderr = $ok.stderr },
            @{ case = "conflict"; command = "git apply --check <patch>"; exit = $bad.exit; stdout = $bad.stdout; stderr = $bad.stderr })

        # git config --list --show-scope: the checkout as it is, then with a worktree config turned on.
        GOk @("checkout", "-q", "--detach", $base) | Out-Null
        $local = G @("config", "--list", "--show-scope")
        GOk @("config", "extensions.worktreeConfig", "true") | Out-Null
        GOk @("config", "--worktree", "core.fsmonitor", "true") | Out-Null
        $wt = G @("config", "--list", "--show-scope")
        GOk @("config", "--worktree", "--unset", "core.fsmonitor") | Out-Null; GOk @("config", "--unset", "extensions.worktreeConfig") | Out-Null
        Write-Fixture "git-config-scopes" "git" $ver "host" @(
            @{ case = "local"; command = "git config --list --show-scope"; exit = $local.exit; stdout = $local.stdout; stderr = $local.stderr },
            @{ case = "worktree"; command = "git config --list --show-scope"; exit = $wt.exit; stdout = $wt.stdout; stderr = $wt.stderr })

        # git diff --name-only between two bases, one a package-file change.
        GOk @("checkout", "-q", "--detach", $base) | Out-Null
        Put "src/App/App.csproj" "<Project Sdk=`"Microsoft.NET.Sdk`">`n  <ItemGroup>`n    <PackageReference Include=`"Newtonsoft.Json`" Version=`"13.0.3`" />`n  </ItemGroup>`n</Project>`n"
        Put "src/App/Thing.cs" "class Thing`n{`n    int A() => 1;`n    int B() => 2;`n    int F() => 6;`n}`n"
        AddAll; GOk @("commit", "-q", "-m", "packages") | Out-Null
        $b2 = GOk @("rev-parse", "HEAD")
        Put "src/App/Old.cs" "class Old`n{`n    int C() => 33;`n}`n"; AddAll; GOk @("commit", "-q", "-m", "code only") | Out-Null
        $b3 = GOk @("rev-parse", "HEAD")
        $pk = G @("diff", "--name-only", $base, $b2)
        $code = G @("diff", "--name-only", $b2, $b3)
        Write-Fixture "git-diff-name-only" "git" $ver "host" @(
            @{ case = "package-change"; command = "git diff --name-only <saved base> <run base>"; exit = $pk.exit; stdout = $pk.stdout; stderr = $pk.stderr },
            @{ case = "code-only"; command = "git diff --name-only <saved base> <run base>"; exit = $code.exit; stdout = $code.stdout; stderr = $code.stderr })

        # git worktree add and remove, as the patch worktree is made and dropped.
        $wtPath = Join-Path $work "patch-work"
        $add = G @("worktree", "add", "--detach", $wtPath, $base)
        $list = G @("worktree", "list", "--porcelain")
        $rm = G @("worktree", "remove", "--force", $wtPath)
        Write-Fixture "git-worktree" "git" $ver "host" @(
            @{ case = "add"; command = "git worktree add --detach <path> <base>"; exit = $add.exit; stdout = $add.stdout; stderr = $add.stderr },
            @{ case = "list"; command = "git worktree list --porcelain"; exit = $list.exit; stdout = $list.stdout; stderr = $list.stderr },
            @{ case = "remove"; command = "git worktree remove --force <path>"; exit = $rm.exit; stdout = $rm.stdout; stderr = $rm.stderr })
    }
    finally { cmd /c rmdir /s /q "$work" 2>$null }
}

# ==== TRX (here) ======================================================================================================
# A scratch xunit project like the repo's test projects, run the way the repro check runs one test.
function Save-TrxFixtures {
    $ver = ((Invoke-Raw "dotnet.exe" @("--version") $env:TEMP).stdout).Trim()
    $work = New-Scratch "trx"
    try {
        $proj = Join-Path $work "Probe.Tests"; New-Item -ItemType Directory -Force $proj | Out-Null
        [IO.File]::WriteAllText((Join-Path $proj "Probe.Tests.csproj"), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" PrivateAssets="all" />
  </ItemGroup>
</Project>
"@)
        $tests = @"
using Xunit;

namespace Probe.Tests
{
    public class ProbeTests
    {
        [Fact]
        public void Passes() { Assert.Equal(2, 1 + 1); }

        [Fact]
        public void FailsAnAssertion() { Assert.Equal(3, 1 + 1); }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Theory(int n) { Assert.True(n > 0); }
    }
}
"@
        [IO.File]::WriteAllText((Join-Path $proj "ProbeTests.cs"), $tests)
        $cases = @()
        foreach ($c in @(@{ case = "pass"; name = "Probe.Tests.ProbeTests.Passes" }, @{ case = "assertion-failure"; name = "Probe.Tests.ProbeTests.FailsAnAssertion" },
                         @{ case = "theory"; name = "Probe.Tests.ProbeTests.Theory" }, @{ case = "build-failure"; name = "Probe.Tests.ProbeTests.Passes" })) {
            if ($c.case -eq "build-failure") { [IO.File]::WriteAllText((Join-Path $proj "Broken.cs"), "namespace Probe.Tests { class Broken { void X() { int y = } } }") }
            $res = Join-Path $work "results-$($c.case)"
            $r = Invoke-Raw "dotnet.exe" @("test", $proj, "-c", "Release", "--filter", "FullyQualifiedName=$($c.name)", "--logger", "trx", "--results-directory", $res) $work @{ DOTNET_CLI_TELEMETRY_OPTOUT = "1"; MSBUILDDISABLENODEREUSE = "1" }
            $trx = @(Get-ChildItem $res -Filter *.trx -Recurse -ErrorAction SilentlyContinue | ForEach-Object { [IO.File]::ReadAllText($_.FullName) })
            $cases += @{ case = $c.case; command = "dotnet test <project> -c Release --filter FullyQualifiedName=$($c.name) --logger trx --results-directory <dir>"; exit = $r.exit; stdout = $r.stdout; stderr = $r.stderr; trx = $(if ($trx.Count) { $trx[0] } else { $null }) }
        }
        Write-Fixture "trx" "dotnet" $ver "host" $cases
    }
    finally { cmd /c rmdir /s /q "$work" 2>$null }
}

# ==== In a VM, over PowerShell Direct =================================================================================
function Get-VMCred([string]$name) {
    Import-Module (Join-Path $PSScriptRoot "FactoryVM.psm1") -Force
    Set-FactoryVMRoot $Root
    Get-VMCredential $name
}
function Invoke-InVM([scriptblock]$block, [object[]]$argv = @()) {
    if (-not $VMName) { throw "This set runs in a VM: pass -VMName." }
    Invoke-Command -VMName $VMName -Credential (Get-VMCred $VMName) -ScriptBlock $block -ArgumentList $argv -ErrorAction Stop
}
function Get-VMWindowsVersion {
    Invoke-InVM {
        $v = [Environment]::OSVersion.Version
        "$($v.Major).$($v.Minor).$($v.Build).$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR)"
    }
}
function ConvertTo-CaseList($rows) { @(foreach ($r in @($rows)) { $h = @{}; foreach ($p in $r.PSObject.Properties) { if ($p.Name -notmatch '^(PSComputerName|RunspaceId|PSShowComputerName)$') { $h[$p.Name] = $p.Value } }; $h }) }

function Save-RobocopyFixtures {
    $rows = Invoke-InVM {
        $w = "C:\Setup\fixture-robocopy"; cmd /c rmdir /s /q $w 2>$null; New-Item -ItemType Directory -Force "$w\src\sub" | Out-Null
        Set-Content "$w\src\a.txt" "a"; Set-Content "$w\src\sub\b.txt" "b"
        function R($case, $src, $dst) { $o = (& robocopy.exe $src $dst /MIR /COPYALL /B /XJ /R:0 /W:0 2>&1 | Out-String); [pscustomobject]@{ case = $case; command = "robocopy <src> <dst> /MIR /COPYALL /B /XJ /R:0 /W:0"; exit = $LASTEXITCODE; stdout = $o; stderr = "" } }
        R "copied" "$w\src" "$w\dst"
        R "nothing-to-do" "$w\src" "$w\dst"
        Set-Content "$w\src\c.txt" "c"; Set-Content "$w\dst\extra.txt" "x"
        R "copied-and-extra" "$w\src" "$w\dst"
        R "failure" "$w\missing" "$w\dst2"
        cmd /c rmdir /s /q $w 2>$null
    }
    Write-Fixture "robocopy-exit-codes" "windows" (Get-VMWindowsVersion) "agents-vm" (ConvertTo-CaseList $rows)
}

function Save-RmdirIcaclsFixtures {
    $rows = Invoke-InVM {
        $w = "C:\Setup\fixture-rmdir"; cmd /c rmdir /s /q $w 2>$null
        function Tree($d) { New-Item -ItemType Directory -Force "$d\x\y" | Out-Null; Set-Content "$d\x\one.txt" "1"; Set-Content "$d\x\y\two.txt" "2" }
        function Left($d) { if (Test-Path $d) { @(Get-ChildItem $d -Recurse -Force | ForEach-Object { $_.FullName.Substring($d.Length) }) -join "`n" } else { "<gone>" } }
        Tree "$w\full"
        $o = (cmd /c rmdir /s /q "$w\full" 2>&1 | Out-String); $code = $LASTEXITCODE
        [pscustomobject]@{ case = "rmdir-full"; command = "cmd /c rmdir /s /q <dir>"; exit = $code; stdout = $o; stderr = ""; exists = (Test-Path "$w\full"); left = (Left "$w\full") }
        Tree "$w\partial"
        $fs = [IO.File]::Open("$w\partial\x\y\two.txt", "Open", "Read", "None")
        try { $o = (cmd /c rmdir /s /q "$w\partial" 2>&1 | Out-String); $code = $LASTEXITCODE } finally { $fs.Dispose() }
        [pscustomobject]@{ case = "rmdir-partial-locked"; command = "cmd /c rmdir /s /q <dir>"; exit = $code; stdout = $o; stderr = ""; exists = (Test-Path "$w\partial"); left = (Left "$w\partial") }
        cmd /c rmdir /s /q "$w\partial" 2>$null
        function Acl($p) { if (Test-Path $p) { $a = Get-Acl $p; [pscustomobject]@{ owner = $a.Owner; sddl = $a.Sddl; access = @($a.Access | ForEach-Object { "$($_.IdentityReference) $($_.AccessControlType) $($_.FileSystemRights) $($_.InheritanceFlags)" }) -join "`n" } } else { [pscustomobject]@{ owner = $null; sddl = $null; access = $null } } }
        Tree "$w\own"
        $o = (& icacls.exe "$w\own" /setowner Administrators /T /L 2>&1 | Out-String); $code = $LASTEXITCODE; $a = Acl "$w\own"
        [pscustomobject]@{ case = "icacls-setowner-ok"; command = "icacls <dir> /setowner Administrators /T /L"; exit = $code; stdout = $o; stderr = ""; owner = $a.owner; sddl = $a.sddl; access = $a.access }
        $o = (& icacls.exe "$w\no-such-folder" /setowner Administrators /T /L 2>&1 | Out-String); $code = $LASTEXITCODE
        [pscustomobject]@{ case = "icacls-setowner-missing"; command = "icacls <missing> /setowner Administrators /T /L"; exit = $code; stdout = $o; stderr = ""; owner = $null; sddl = $null; access = $null }
        $o = (& icacls.exe "$w\own" /setowner "NoSuchAccount-$([guid]::NewGuid().ToString('N').Substring(0,6))" /T /L 2>&1 | Out-String); $code = $LASTEXITCODE; $a = Acl "$w\own"
        [pscustomobject]@{ case = "icacls-setowner-bad-account"; command = "icacls <dir> /setowner <no such account> /T /L"; exit = $code; stdout = $o; stderr = ""; owner = $a.owner; sddl = $a.sddl; access = $a.access }
        cmd /c rmdir /s /q $w 2>$null
    }
    Write-Fixture "rmdir-icacls" "windows" (Get-VMWindowsVersion) "agents-vm" (ConvertTo-CaseList $rows)
}

function Save-ProcessOwnerFixtures {
    $rows = Invoke-InVM {
        foreach ($c in @(@{ case = "own"; filter = "ProcessId=$PID" }, @{ case = "system"; filter = "Name='lsass.exe'" }, @{ case = "service-host"; filter = "Name='svchost.exe'" })) {
            $p = Get-CimInstance Win32_Process -Filter $c.filter | Select-Object -First 1
            $o = $p | Invoke-CimMethod -MethodName GetOwner
            [pscustomobject]@{ case = $c.case; command = "Get-CimInstance Win32_Process | Invoke-CimMethod -MethodName GetOwner"; exit = 0; stdout = ($o | Select-Object Domain, User, ReturnValue | ConvertTo-Json -Compress); stderr = "" }
        }
    }
    Write-Fixture "get-process-owner" "windows" (Get-VMWindowsVersion) "agents-vm" (ConvertTo-CaseList $rows)
}

function Save-FirewallFixtures {
    $rows = Invoke-InVM {
        foreach ($r in @(Get-NetFirewallRule -DisplayName "House Style egress*" -ErrorAction SilentlyContinue)) {
            $a = $r | Get-NetFirewallAddressFilter; $p = $r | Get-NetFirewallPortFilter; $s = $r | Get-NetFirewallSecurityFilter
            [pscustomobject]@{ case = $r.DisplayName; command = "Get-NetFirewallRule | Get-NetFirewallAddressFilter, Get-NetFirewallPortFilter, Get-NetFirewallSecurityFilter"; exit = 0
                stdout = ([pscustomobject]@{ DisplayName = $r.DisplayName; Direction = "$($r.Direction)"; Action = "$($r.Action)"; Enabled = "$($r.Enabled)"; Profile = "$($r.Profile)"
                    RemoteAddress = @($a.RemoteAddress); Protocol = "$($p.Protocol)"; RemotePort = @($p.RemotePort); LocalUser = $s.LocalUser } | ConvertTo-Json -Compress -Depth 4); stderr = "" }
        }
        [pscustomobject]@{ case = "profiles"; command = "Get-NetFirewallProfile"; exit = 0; stdout = (@(Get-NetFirewallProfile | ForEach-Object { [pscustomobject]@{ Name = $_.Name; DefaultOutboundAction = "$($_.DefaultOutboundAction)" } }) | ConvertTo-Json -Compress); stderr = "" }
    }
    Write-Fixture "get-netfirewallrule" "windows" (Get-VMWindowsVersion) "agents-vm" (ConvertTo-CaseList $rows) -Network
}

function Save-TaskInfoFixtures {
    $rows = Invoke-InVM {
        $t = Get-ScheduledTask -TaskName "House Style agent"
        $i = $t | Get-ScheduledTaskInfo
        [pscustomobject]@{ case = "agent-task"; command = "Get-ScheduledTask, Get-ScheduledTaskInfo"; exit = 0; stderr = ""
            stdout = ([pscustomobject]@{ State = "$($t.State)"; LastRunTime = "$($i.LastRunTime)"; LastTaskResult = $i.LastTaskResult; NumberOfMissedRuns = $i.NumberOfMissedRuns
                UserId = $t.Principal.UserId; LogonType = "$($t.Principal.LogonType)"; RunLevel = "$($t.Principal.RunLevel)"; MultipleInstances = "$($t.Settings.MultipleInstances)"
                Execute = $t.Actions[0].Execute; Arguments = $t.Actions[0].Arguments; Sddl = (Get-ScheduledTask -TaskName "House Style agent" | ForEach-Object { (New-Object -ComObject Schedule.Service | ForEach-Object { $_.Connect(); $_.GetFolder("\").GetTask("House Style agent").GetSecurityDescriptor(0xF) }) }) } | ConvertTo-Json -Compress) }
    }
    Write-Fixture "get-scheduledtaskinfo" "windows" (Get-VMWindowsVersion) "agents-vm" (ConvertTo-CaseList $rows)
}

function Save-ServiceSddlFixtures {
    $rows = Invoke-InVM {
        $runner = @(Get-Service "actions.runner.*" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Name)
        foreach ($s in @("Schedule", "BITS", "W32Time") + $runner) {
            if (-not $s) { continue }
            $o = (& sc.exe sdshow $s 2>&1 | Out-String)
            [pscustomobject]@{ case = $(if ($s -like "actions.runner.*") { "runner" } else { $s }); command = "sc.exe sdshow <service>"; exit = $LASTEXITCODE; stdout = $o; stderr = "" }
        }
    }
    Write-Fixture "sc-sdshow" "windows" (Get-VMWindowsVersion) "agents-vm" (ConvertTo-CaseList $rows)
}

function Save-EventLogAccessFixtures {
    $rows = Invoke-InVM {
        $before = if (Test-Path "C:\HouseStyle\setup-log\eventlog-before.json") { Get-Content "C:\HouseStyle\setup-log\eventlog-before.json" -Raw | ConvertFrom-Json } else { $null }
        foreach ($ch in "Microsoft-Windows-PowerShell/Operational", "Windows PowerShell", "Microsoft-Windows-TaskScheduler/Operational", "Application") {
            if ($before) { $b = @($before | Where-Object channel -eq $ch); if ($b.Count) { [pscustomobject]@{ case = "before:$ch"; command = "wevtutil gl <channel>"; exit = $b[0].exit; stdout = $b[0].stdout; stderr = "" } } }
            $o = (& wevtutil.exe gl $ch 2>&1 | Out-String)
            [pscustomobject]@{ case = "after:$ch"; command = "wevtutil gl <channel>"; exit = $LASTEXITCODE; stdout = $o; stderr = "" }
        }
    }
    Write-Fixture "wevtutil-gl" "windows" (Get-VMWindowsVersion) "agents-vm" (ConvertTo-CaseList $rows)
}

# One run of the agent task with no job waiting, so the launcher stops at its first check: a token-free instance of
# the task, whose events are the shape the wait reads.
function Save-TaskEventFixtures {
    $rows = Invoke-InVM {
        $start = Get-Date
        Remove-Item C:\HouseStyle\agent-jobs\next.txt -ErrorAction SilentlyContinue
        Start-ScheduledTask -TaskName "House Style agent"
        $deadline = (Get-Date).AddMinutes(3)
        do { Start-Sleep 5; $ev = @(Get-WinEvent -FilterHashtable @{ LogName = "Microsoft-Windows-TaskScheduler/Operational"; Id = 100, 102, 201; StartTime = $start } -ErrorAction SilentlyContinue | Where-Object { $_.Message -like "*House Style agent*" }) }
        until (@($ev | Where-Object Id -eq 102).Count -or (Get-Date) -gt $deadline)
        foreach ($e in ($ev | Sort-Object TimeCreated)) { [pscustomobject]@{ case = "event-$($e.Id)"; command = "Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-TaskScheduler/Operational'; Id = 100, 102, 201 }"; exit = 0; stdout = $e.ToXml(); stderr = "" } }
    }
    Write-Fixture "task-events" "windows" (Get-VMWindowsVersion) "agents-vm" (ConvertTo-CaseList $rows)
}

# ==== On the host =====================================================================================================
function Get-HostWindowsVersion { $v = [Environment]::OSVersion.Version; "$($v.Major).$($v.Minor).$($v.Build).$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR)" }

function Save-HyperVFixtures {
    if (-not $VMName) { throw "Pass -VMName." }
    $vm = Get-VM -Name $VMName
    $cases = @(
        @{ case = "get-vm"; command = "Get-VM <name>"; exit = 0; stderr = ""; stdout = ($vm | Select-Object Name, State, Status, Uptime, Generation, ProcessorCount, MemoryStartup, CheckpointType, AutomaticCheckpointsEnabled | ConvertTo-Json -Compress) },
        @{ case = "get-vmsnapshot"; command = "Get-VMSnapshot -VMName <name>"; exit = 0; stderr = ""; stdout = (@(Get-VMSnapshot -VMName $VMName | Select-Object Name, SnapshotType, ParentSnapshotName, CreationTime) | ConvertTo-Json -Compress) },
        @{ case = "get-vmnetworkadapter"; command = "Get-VMNetworkAdapter -VMName <name>"; exit = 0; stderr = ""; stdout = (@(Get-VMNetworkAdapter -VMName $VMName | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Connected = $_.Connected; SwitchName = $(if ($_.SwitchName) { "_switch_" } else { $null }); MacAddress = $_.MacAddress; IPAddresses = @($_.IPAddresses); Status = @($_.Status | ForEach-Object { "$_" }) } }) | ConvertTo-Json -Compress) }
    )
    Write-Fixture "hyperv-shapes" "host-windows" (Get-HostWindowsVersion) "host" $cases -Network
}

# The three PowerShell Direct failures the host must tell apart: the VM off, the VM restoring, and a rename whose call
# is stopped after it lands, with the outbox read back afterwards. Run while the agents VM is being built (step 3):
# it turns the VM off and restores a temporary checkpoint.
function Save-PSDirectFixtures {
    if (-not $VMName) { throw "Pass -VMName." }
    $cred = Get-VMCred $VMName
    function ErrCase($case, [scriptblock]$call) {
        try { & $call | Out-Null; @{ case = $case; command = "Invoke-Command -VMName <name>"; exit = 0; stdout = "no error"; stderr = "" } }
        catch { @{ case = $case; command = "Invoke-Command -VMName <name>"; exit = 1; stdout = ""; stderr = "$($_.Exception.Message)"; errorId = $_.FullyQualifiedErrorId; exceptionType = $_.Exception.GetType().FullName } }
    }
    $cases = @()
    Checkpoint-VM -Name $VMName -SnapshotName "fixture-tmp"
    try {
        Stop-VM -Name $VMName -TurnOff -Force
        $cases += ErrCase "vm-off" { Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { 1 } -ErrorAction Stop }
        $job = Start-Job -ScriptBlock { param($n) Restore-VMSnapshot -VMName $n -Name "fixture-tmp" -Confirm:$false } -ArgumentList $VMName
        Start-Sleep 1
        $cases += ErrCase "vm-restoring" { Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { 1 } -ErrorAction Stop }
        $job | Wait-Job | Remove-Job
        Start-VM -Name $VMName
        $deadline = (Get-Date).AddMinutes(10)
        do { Start-Sleep 10; $up = $false; try { $up = Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { $true } -ErrorAction Stop } catch { } } until ($up -or (Get-Date) -gt $deadline)
        $id = [guid]::NewGuid().ToString()
        Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { param($i) New-Item -ItemType Directory -Force "C:\HouseStyle\flare-outbox\$i" | Out-Null } -ArgumentList $id
        $j = Invoke-Command -VMName $VMName -Credential $cred -AsJob -ScriptBlock { param($i) Rename-Item "C:\HouseStyle\flare-outbox\$i" "$i.claimed"; Start-Sleep 60 } -ArgumentList $id
        $landed = $false; $deadline = (Get-Date).AddSeconds(30)
        do { Start-Sleep 2; $landed = Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { param($i) Test-Path "C:\HouseStyle\flare-outbox\$i.claimed" } -ArgumentList $id } until ($landed -or (Get-Date) -gt $deadline)
        Stop-Job $j; $state = "$($j.State)"; $reason = "$($j.ChildJobs[0].JobStateInfo.Reason)"; Remove-Job $j -Force
        $back = Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { @(Get-ChildItem C:\HouseStyle\flare-outbox -Force | ForEach-Object Name) -join "`n" }
        $cases += @{ case = "rename-stopped-after-landing"; command = "Invoke-Command -AsJob { Rename-Item }; Stop-Job; then the outbox read back"; exit = 0; stdout = ($back -replace [regex]::Escape($id), '_check-id_'); stderr = $reason; jobState = $state }
        Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { param($i) Remove-Item "C:\HouseStyle\flare-outbox\$i.claimed" -Force } -ArgumentList $id
    }
    finally { Get-VMSnapshot -VMName $VMName -Name "fixture-tmp" -ErrorAction SilentlyContinue | Remove-VMSnapshot }
    Write-Fixture "psdirect-failures" "host-windows" (Get-HostWindowsVersion) "host" $cases
}

function Save-VMConnectFixtures {
    $p = @(Get-CimInstance Win32_Process -Filter "Name='vmconnect.exe'")
    if (-not $p.Count) { throw "Open VMConnect on the Flare VM first (vmconnect.exe localhost HSFACTORY)." }
    $cases = @(@{ case = "open"; command = "Get-CimInstance Win32_Process -Filter Name='vmconnect.exe'"; exit = 0; stderr = ""; stdout = (@($p | ForEach-Object { [pscustomobject]@{ Name = $_.Name; CommandLine = $_.CommandLine } }) | ConvertTo-Json -Compress) })
    Write-Fixture "vmconnect-process" "host-windows" (Get-HostWindowsVersion) "host" $cases
}

# ==== GitHub, with the owner's gh sign-in =============================================================================
function Invoke-GhApi([string]$path) {
    $ErrorActionPreference = "Continue"
    $o = (& gh.exe api -H "X-GitHub-Api-Version: $($pins['github-api'])" $path 2>&1 | Out-String)
    [pscustomobject]@{ exit = $LASTEXITCODE; stdout = $o }
}
function Save-RunnerStateFixtures {
    if (@("idle", "busy", "offline") -notcontains $Case) { throw "Pass -Case idle, busy or offline: the state the runner is in now." }
    $r = Invoke-GhApi "repos/$Repo/actions/runners"
    $mine = @(($r.stdout | ConvertFrom-Json).runners | Where-Object name -eq $RunnerName)
    if ($mine.Count -ne 1) { throw "No runner named $RunnerName." }
    $seen = if ($mine[0].status -ne "online") { "offline" } elseif ($mine[0].busy) { "busy" } else { "idle" }
    if ($seen -ne $Case) { throw "The runner is $seen now, not $Case." }
    Write-Fixture "runner-state" "github-api" $pins['github-api'] "github" @(@{ case = $Case; command = "gh api repos/<repo>/actions/runners"; exit = $r.exit; stdout = ($mine[0] | ConvertTo-Json -Compress -Depth 4); stderr = "" }) -Merge
}
function Save-ForkApprovalFixtures {
    if (@("all_external_contributors", "other") -notcontains $Case) { throw "Pass -Case all_external_contributors, or other while the setting holds another value." }
    $r = Invoke-GhApi "repos/$Repo/actions/permissions/fork-pr-contributor-approval"
    $now = try { "" + ($r.stdout | ConvertFrom-Json).approval_policy } catch { "" }
    if (($Case -eq "all_external_contributors") -ne ($now -eq "all_external_contributors")) { throw "The setting doesn't match -Case $Case now." }
    Write-Fixture "fork-pr-approval" "github-api" $pins['github-api'] "github" @(@{ case = $Case; command = "gh api repos/<repo>/actions/permissions/fork-pr-contributor-approval"; exit = $r.exit; stdout = $r.stdout; stderr = "" }) -Merge
}
function Save-LabelEventFixtures {
    if (-not $Report) { throw "Pass -Report, a private report with a decision:fix label event." }
    $r = Invoke-GhApi "repos/$ReportsRepo/issues/$Report/events?per_page=100"
    $ev = @(($r.stdout | ConvertFrom-Json) | Where-Object { $_.event -eq "labeled" -and $_.label.name -eq "decision:fix" })
    if (-not $ev.Count) { throw "Report $Report has no decision:fix label event." }
    $cases = @(foreach ($e in $ev) {
        $who = if ($e.actor.login -eq "abmorrill4") { "owner" } else { "other-account" }
        foreach ($k in "url", "issue_url", "node_id") { if ($e.PSObject.Properties.Name -contains $k) { $e.$k = "_redacted_" } }
        if ($who -ne "owner") { $e.actor.login = "other-account"; $e.actor.id = 0 }
        @{ case = "decision-fix-by-$who"; command = "gh api repos/<reports>/issues/<n>/events"; exit = $r.exit; stdout = ($e | ConvertTo-Json -Compress -Depth 6); stderr = "" }
    })
    Write-Fixture "label-events" "github-api" $pins['github-api'] "github" $cases -Merge
}

# ==== Imported from elsewhere =========================================================================================
function Save-HarnessFixtures {
    if (@("pass", "fail", "vm-retry") -notcontains $Case -or -not $From) { throw "Pass -Case pass, fail or vm-retry and -From, a results.json from an e2e run on the Flare VM." }
    $text = [IO.File]::ReadAllText($From)
    $ver = (Get-Content $From -Raw | ConvertFrom-Json | Select-Object -First 1).version
    Write-Fixture "harness-results" "flare" ("" + $ver) "flare-vm" @(@{ case = $Case; command = "Run-FlareE2E.ps1 results.json"; exit = 0; stdout = $text; stderr = "" }) -Merge
}
function Save-ClaudeFixtures([string[]]$names) {
    if (-not $From) { throw "Pass -From, the folder the capture dispatch wrote (factory-logs\fixtures, copied to the host by the reset)." }
    foreach ($n in $names) {
        $f = Join-Path $From "$n.json"
        if (-not (Test-Path $f)) { Write-Host "No $n.json in $From."; continue }
        $c = Get-Content $f -Raw | ConvertFrom-Json   # written by the capture dispatch: version, command, exit, stdout, stderr
        Write-Fixture $n "claude" ("" + $c.version) "agents-vm" @(@{ case = $n; command = "" + $c.command; exit = [int]$c.exit; stdout = "" + $c.stdout; stderr = "" + $c.stderr })
    }
}

# ==== Flare's license state, on the Flare VM or its scratch copy =====================================================
# The same read the Flare check makes at its step 4 (Invoke-FlareCheck.ps1 -TrialRead): Flare started with no plugin,
# and the text of its licensing window as it shows.
function Save-FlareTrialFixture([string]$name) {
    $read = Invoke-InVM { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\HouseStyle\flare-check\Invoke-FlareCheck.ps1 -TrialRead | Out-String }
    $o = $read | ConvertFrom-Json
    Write-Fixture $name "flare" ("" + $o.flareVersion) "flare-vm" @(@{ case = $name; command = "Invoke-FlareCheck.ps1 -TrialRead"; exit = 0; stdout = "" + $o.text; stderr = "" })
}

foreach ($s in $Set) {
    switch ($s) {
        "git" { Save-GitFixtures }
        "trx" { Save-TrxFixtures }
        "robocopy-exit-codes" { Save-RobocopyFixtures }
        "rmdir-icacls" { Save-RmdirIcaclsFixtures }
        "get-process-owner" { Save-ProcessOwnerFixtures }
        "get-netfirewallrule" { Save-FirewallFixtures }
        "get-scheduledtaskinfo" { Save-TaskInfoFixtures }
        "sc-sdshow" { Save-ServiceSddlFixtures }
        "wevtutil-gl" { Save-EventLogAccessFixtures }
        "task-events" { Save-TaskEventFixtures }
        "hyperv-shapes" { Save-HyperVFixtures }
        "psdirect-failures" { Save-PSDirectFixtures }
        "vmconnect-process" { Save-VMConnectFixtures }
        "runner-state" { Save-RunnerStateFixtures }
        "fork-pr-approval" { Save-ForkApprovalFixtures }
        "label-events" { Save-LabelEventFixtures }
        "harness-results" { Save-HarnessFixtures }
        "claude" { Save-ClaudeFixtures @("claude-success", "claude-max-turns", "claude-error") }
        "claude-usage-limit" { Save-ClaudeFixtures @("claude-usage-limit") }
        { @("flare-trial-active", "flare-trial-expired", "flare-licensed") -contains $_ } { Save-FlareTrialFixture $s }
        default { throw "Unknown set '$s'." }
    }
}
