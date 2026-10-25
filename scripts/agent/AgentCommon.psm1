# Shared helpers for the scripts in scripts/agent. Windows PowerShell 5.1, Pester 3 tests in tests/agent.
# Everything here is deterministic and takes only validated input.
Set-StrictMode -Version 2

# --- Secrets never reach a child process -------------------------------------------------------------------------

# GIT_CONFIG_* goes as a whole: a value can carry a credential (http.extraheader), and a count without its keys breaks git.
$script:SecretName = '(KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|COOKIE)|_VK$|^(ANTHROPIC_|GH_|GITHUB_|BIFROST_|FACTORY_|AWS_|AZURE_|OP_|NPM_|NUGET_|FLAREAI_RECORD|GIT_CONFIG_)'

# The package cache's location and the restore's revocation mode aren't secrets: the agents VM's jobs set them, and a
# test run needs them to build from the run's locked cache.
$script:NotSecret = @('NUGET_PACKAGES', 'NUGET_CERT_REVOCATION_MODE')

function Test-SecretName([string]$Name) {
    # True for an environment variable name that is, or may hold, a secret.
    if ($script:NotSecret -contains $Name) { return $false }
    return [bool]($Name -match $script:SecretName)
}

function ConvertTo-ArgumentString([string[]]$Arguments) {
    # Quotes each argument for CreateProcess. Refuses a double quote, which no validated input contains.
    $parts = foreach ($a in $Arguments) {
        if ($null -eq $a) { continue }
        if ($a.Contains('"')) { throw "An argument contains a double quote: $a" }
        if ($a -eq '' -or $a -match '[\s&|<>^()]') { '"' + ($a -replace '(\\+)$', '$1$1') + '"' } else { $a }
    }
    return ($parts -join ' ')
}

function Invoke-ScrubbedProcess {
    # Runs a program with every secret-looking variable removed from its environment. Returns ExitCode and Output
    # (stdout lines, then stderr lines). -Stream also writes each stdout line to the host as it arrives.
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = (Get-Location).Path,
        [switch]$Stream
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = ConvertTo-ArgumentString $ArgumentList
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    foreach ($name in @($psi.EnvironmentVariables.Keys)) {
        if (Test-SecretName $name) { $psi.EnvironmentVariables.Remove($name) }
    }
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $lines = New-Object System.Collections.Generic.List[string]
    while ($null -ne ($line = $p.StandardOutput.ReadLine())) {
        $lines.Add($line)
        if ($Stream) { Write-Host $line }
    }
    $p.WaitForExit()
    $err = $errTask.Result
    if ($err) {
        foreach ($l in ($err -split "`r?`n")) { if ($l) { $lines.Add($l); if ($Stream) { Write-Host $l } } }
    }
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $lines.ToArray() }
}

# --- One run at a time -------------------------------------------------------------------------------------------

function Get-DefaultLockPath { Join-Path ([System.IO.Path]::GetTempPath()) 'housestyle-agent-test.lock' }

function Enter-RunLock([string]$Path = (Get-DefaultLockPath)) {
    # Holds the lock file open with no sharing until the returned stream is disposed.
    try {
        return [System.IO.File]::Open($Path, 'OpenOrCreate', 'ReadWrite', 'None')
    } catch [System.IO.IOException] {
        throw "Another test run is in progress. Wait for it to finish, then run again."
    }
}

# --- Testing channels, cheapest first ------------------------------------------------------------------------------

$script:Channels = @(
    [pscustomobject]@{ Name = 'rule-fixtures'; Order = 1; Where = 'any Windows runner'; Proves = 'Lint rules in each markup context'; Project = 'tests/FlareAI.RuleFixtures.Tests' }
    [pscustomobject]@{ Name = 'bench';         Order = 1; Where = 'any Windows runner'; Proves = 'Lint and guide text across the corpus'; Project = $null }
    [pscustomobject]@{ Name = 'core';          Order = 2; Where = 'any Windows runner'; Proves = 'Engine, settings, updates, IO'; Project = 'tests/FlareAI.Core.Tests' }
    [pscustomobject]@{ Name = 'scenarios';     Order = 3; Where = 'any Windows runner'; Proves = 'Document edits, review session, governance, AI flows with a scripted model'; Project = 'tests/FlareAI.Scenarios' }
    [pscustomobject]@{ Name = 'flare-tests';   Order = 4; Where = 'any Windows runner'; Proves = 'Pane and dialog layout and behavior in hidden WinForms'; Project = 'tests/FlareAI.Flare.Tests' }
    [pscustomobject]@{ Name = 'pester';        Order = 5; Where = 'any runner'; Proves = 'Factory, release, guide publisher and agent scripts'; Project = $null }
    [pscustomobject]@{ Name = 'vitest';        Order = 5; Where = 'any runner'; Proves = 'The feedback Worker'; Project = $null }
    [pscustomobject]@{ Name = 'conformance';   Order = 6; Where = 'Flare VM'; Proves = "The adapter against Flare's real editor (not built yet, gap G1)"; Project = $null }
    [pscustomobject]@{ Name = 'e2e';           Order = 7; Where = 'Flare VM'; Proves = 'Docking, ribbon, events, anything only visible in Flare'; Project = $null }
    [pscustomobject]@{ Name = 'full';          Order = 8; Where = 'any Windows runner'; Proves = 'The whole Release suite, as CI runs it, before a push'; Project = 'FlareAI.sln' }
)

function Get-TestChannel { $script:Channels }

# What test-channel.ps1 says when a channel is refused (factory-runtime spec section 4 and Copy).
$script:ChannelMessages = @{
    NotForAgents = "The factory's agents can't run this channel."
    E2eForAgents = "The factory's agents can't run e2e. Add or update a scenario in tests/e2e/scenarios, and the factory runs it in Flare after you finish."
    NoFlare      = "There's no Flare on this machine. Run e2e on the Flare VM."
}
function Get-ChannelMessage([string]$Key) { $script:ChannelMessages[$Key] }

# Why this channel may not run here, or $null. HOUSESTYLE_AGENT_CHANNELS, set in every factory job, is the agents'
# list and is checked first. Without it, e2e needs Flare on this machine.
function Get-ChannelRefusal([string]$Channel, [string]$AgentChannels, [bool]$HasFlare) {
    if ($AgentChannels) {
        $list = @($AgentChannels -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($list -contains $Channel) { return $null }
        if ($Channel -eq 'e2e') { return $script:ChannelMessages.E2eForAgents }
        return $script:ChannelMessages.NotForAgents
    }
    if ($Channel -eq 'e2e' -and -not $HasFlare) { return $script:ChannelMessages.NoFlare }
    return $null
}

# True when Flare is installed here.
function Test-FlareInstalled {
    foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }) {
        if (@(Get-ChildItem (Join-Path $pf 'MadCap Software') -Filter Flare.exe -Recurse -Depth 3 -ErrorAction SilentlyContinue).Count) { return $true }
    }
    return $false
}

function Assert-SafeFilter([string]$Filter) {
    if ($Filter -notmatch '^[\w.~=!&|,()\- ]{1,300}$') { throw "The filter can use letters, digits and . ~ = ! & | , ( ) - only." }
}

function Assert-SafeTestPath([string]$Path) {
    if ($Path -notmatch '^tests[\\/][\w.\\/\-]+$' -or $Path -match '\.\.') { throw "The path must be a folder or file under tests/." }
}

function Get-ChannelCommand {
    # Returns FilePath, ArgumentList and WorkingDirectory for one channel. -ResultsDir asks for a machine-readable
    # result file there (TRX for the .NET channels, pester.json for Pester).
    param(
        [Parameter(Mandatory)][string]$Channel,
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$Filter,
        [string]$Path,
        [string]$Scenario,
        [string]$ResultsDir,
        [string]$HarnessRoot,   # where the e2e harness comes from; the factory passes its copy from the base commit
        [switch]$Exact          # with full only: one test, by its exact name (the factory's repro check)
    )
    if (-not $HarnessRoot) { $HarnessRoot = $RepoRoot }
    $c = $script:Channels | Where-Object Name -eq $Channel
    if (-not $c) { throw "Unknown channel '$Channel'. Run test-channel.ps1 -List for the channels." }
    if ($Filter) { Assert-SafeFilter $Filter }
    if ($Exact) {
        if ($Channel -ne 'full') { throw "-Exact runs only with -Channel full." }
        if (-not $Filter -or $Filter -match '[~=!&|,() ]') { throw "-Exact takes one test's full name: no ~ = ! & | , ( ) or space." }
    }
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    switch ($Channel) {
        { $_ -in 'rule-fixtures', 'core', 'scenarios', 'flare-tests' } {
            $a = @('test', $c.Project, '--nologo')
            if ($Filter) { $a += @('--filter', "FullyQualifiedName~$Filter") }
            if ($ResultsDir) { $a += @('--logger', 'trx;LogFileName=results.trx', '--results-directory', $ResultsDir) }
            return [pscustomobject]@{ FilePath = 'dotnet'; ArgumentList = $a; WorkingDirectory = $RepoRoot }
        }
        'full' {
            if ($Filter -and -not $Exact) { throw "The full channel runs every test; use a narrower channel to filter, or -Exact for one test." }
            $a = @('test', 'FlareAI.sln', '-c', 'Release', '--nologo')
            if ($Exact) { $a += @('--filter', "FullyQualifiedName=$Filter") }
            if ($ResultsDir) { $a += @('--logger', 'trx;LogFileName=results.trx', '--results-directory', $ResultsDir) }
            return [pscustomobject]@{ FilePath = 'dotnet'; ArgumentList = $a; WorkingDirectory = $RepoRoot }
        }
        'bench' {
            if ($Filter) { throw "The bench channel runs the whole gate; it takes no filter." }
            return [pscustomobject]@{ FilePath = $ps; WorkingDirectory = $RepoRoot
                ArgumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts/check-baseline.ps1', '-SkipPolicy') }
        }
        'pester' {
            if (-not $Path) { throw "The pester channel needs -Path, a folder or file under tests/." }
            Assert-SafeTestPath $Path
            $p = $Path -replace "'", "''"
            $cmd = "Import-Module Pester -MaximumVersion 3.99 -Force; "
            if ($Filter) { $cmd += "`$r = Invoke-Pester -Path '$p' -TestName '$Filter' -PassThru" } else { $cmd += "`$r = Invoke-Pester -Path '$p' -PassThru" }
            if ($ResultsDir) {
                $out = (Join-Path $ResultsDir 'pester.json') -replace "'", "''"
                $cmd += " -Quiet; `$rows = @(`$r.TestResult | ForEach-Object { [pscustomobject]@{ describe = `$_.Describe; name = `$_.Name; passed = `$_.Passed; result = `$_.Result; message = `$_.FailureMessage; errorId = `$(if (`$_.ErrorRecord) { `$_.ErrorRecord.FullyQualifiedErrorId } else { `$null }) } }); ConvertTo-Json -InputObject `$rows -Depth 3 | Set-Content -Encoding UTF8 '$out'"
            }
            $cmd += "; exit `$r.FailedCount"
            return [pscustomobject]@{ FilePath = $ps; ArgumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $cmd); WorkingDirectory = $RepoRoot }
        }
        'vitest' {
            $a = @('test')
            if ($Filter) { $a += @('--', '-t', $Filter) }
            return [pscustomobject]@{ FilePath = 'npm.cmd'; ArgumentList = $a; WorkingDirectory = (Join-Path $RepoRoot 'tools/feedback-worker') }
        }
        'conformance' {
            throw "In-Flare conformance isn't built yet (gap G1). Add the case to AdapterConformance so it runs on the sim, and use e2e for proof in Flare."
        }
        'e2e' {
            if (-not $Scenario) { throw "The e2e channel needs -Scenario, the name of one file in tests/e2e/scenarios." }
            if ($Scenario -notmatch '^[a-z0-9][a-z0-9-]{0,60}$') { throw "A scenario name uses lowercase letters, digits and hyphens." }
            if ($Scenario -like 'ai-*') { throw "AI scenarios need an API key, so they never run here. They run nightly on main." }
            if (-not (Test-Path (Join-Path $RepoRoot "tests/e2e/scenarios/$Scenario.ps1"))) { throw "There's no scenario tests/e2e/scenarios/$Scenario.ps1." }
            $harness = if ($HarnessRoot -eq $RepoRoot) { 'tests/e2e/Run-FlareE2E.ps1' } else { Join-Path $HarnessRoot 'tests\e2e\Run-FlareE2E.ps1' }
            $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $harness, '-Install', '-NoAi', '-Scenario', $Scenario)
            if ($HarnessRoot -ne $RepoRoot) { $a += @('-RepoRoot', $RepoRoot) }
            if ($ResultsDir) { $a += @('-OutDir', $ResultsDir) }
            return [pscustomobject]@{ FilePath = $ps; ArgumentList = $a; WorkingDirectory = $RepoRoot }
        }
    }
}


# --- Commands an agent may run through Bash or PowerShell -----------------------------------------------------------

# Tests and builds run only through scripts/agent/test-channel.ps1, which removes secrets from the child environment.
# A bare `dotnet test` would run code from the diff with this agent's environment, so it isn't on any list.
# Roles: worker (hs-repro, hs-fix, hs-rules) may commit and run every agent script; tester (hs-review-correctness,
# hs-skeptic) may run test-channel and findings-lint; reviewer (the other review lenses) only reads.
$script:Token = '[\w./\\:=,~^+%#*\-]+'
$script:ReadGit = '^git (diff|log|show|status|rev-parse|ls-files|grep|blame|merge-base)( TOKEN)*$'
$script:WriteGit = '^git (add|commit)( TOKEN)*$'
$script:Listing = '^(ls|dir|cat|head|tail|wc|grep|Get-ChildItem|Get-Content|Select-String|Test-Path|pwd|Get-Location)( TOKEN)*$'
$script:ScriptCall = '^((powershell|pwsh)(\.exe)? ((-NoProfile|-NonInteractive|-ExecutionPolicy Bypass) )*-File )?(\.[\\/])?scripts[\\/]agent[\\/](NAMES)\.ps1( TOKEN)*$'
$script:RoleScripts = @{
    worker   = 'test-channel|repro-must-fail|findings-lint'
    tester   = 'test-channel|findings-lint'
    reviewer = 'findings-lint'
}

function Get-RoleAllowList([string]$Role) {
    if (-not $script:RoleScripts.ContainsKey($Role)) { throw "Unknown agent role '$Role'." }
    $list = @($script:ReadGit, $script:Listing, ($script:ScriptCall -replace 'NAMES', $script:RoleScripts[$Role]))
    if ($Role -eq 'worker') { $list += $script:WriteGit }
    return @($list | ForEach-Object { $_ -replace 'TOKEN', $script:Token })
}

function Test-InvokesAgentScript([string]$Command) { return [bool]($Command -match '(?i)scripts[\\/]agent[\\/]') }

function Test-AgentCommand {
    # Returns $null when every part of the command is allowed for the role, otherwise the reason it isn't. Deny by
    # default: each part must match one allow pattern from start to end, with plain arguments only.
    param([string]$Command, [ValidateSet('worker', 'tester', 'reviewer')][string]$Role = 'reviewer')
    if ([string]::IsNullOrWhiteSpace($Command)) { return "The command is empty." }
    $c = $Command -replace '\s+2>&1', ''
    if ($c -match '`|\$\(|\$\{|<\(|>|<') { return "Command substitution and redirection aren't allowed here." }
    if ($c -match '\$') { return "Variables aren't allowed in commands here." }
    if ($c -match '(?i)(^|[\\/\s])\.env\b|\.dev\.vars|env:|printenv') { return "Reading the environment or .env files isn't allowed." }
    if ($c -match '(?i)--output|--open-files-in-pager|\s-O\S*|--ext-diff|--textconv|\s-c\s') { return "Git options that write files or run programs aren't allowed." }
    if ($c -match '(?i)-(RepoRoot|WorkRoot|LockPath|ResultsDir)\b') { return "The agent scripts run on this repository only, with their own folders." }
    # Quoted literals become one plain token; anything still holding ( ) { } @ or a quote is refused.
    $c = $c -replace '"[^"]*"', 'Q' -replace "'[^']*'", 'Q'
    if ($c -match '[(){}@"'']') { return "Parentheses, braces, @ and unbalanced quotes aren't allowed in commands here." }
    $allow = Get-RoleAllowList $Role
    # A lone & runs a second command (and is PowerShell's call operator), so it splits like && and ;.
    foreach ($part in ($c -split '&&|\|\||;|\||&|\r?\n')) {
        $p = ($part.Trim() -replace '\s+', ' ')
        if (-not $p) { continue }
        $ok = $false
        foreach ($rx in $allow) { if ($p -match "(?i)$rx") { $ok = $true; break } }
        if (-not $ok) {
            $first = ($p -split ' ')[0]
            return "'$first' isn't allowed for this agent. Run tests with scripts/agent/test-channel.ps1 (-Channel full for the whole suite), or use read-only git."
        }
    }
    return $null
}

Export-ModuleMember -Function Test-SecretName, ConvertTo-ArgumentString, Invoke-ScrubbedProcess, Get-DefaultLockPath,
    Enter-RunLock, Get-TestChannel, Get-ChannelMessage, Get-ChannelRefusal, Test-FlareInstalled, Assert-SafeFilter, Assert-SafeTestPath, Get-ChannelCommand, Test-AgentCommand,
    Test-InvokesAgentScript, Get-RoleAllowList
