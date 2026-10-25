# Pester 3 tests for the agent jobs (factory-runtime spec section 2): New-AgentJob (P1), the sign-in (P2), the launcher
# Start-AgentJob.ps1 (P3), Invoke-AgentJob and the package cache (P4), and reading what a job wrote (P5). The test's own
# account stands in for the agent user, and a scratch folder for C:\HouseStyle; the task, the sweep and the firewall
# are mocked where they'd need the agents VM.
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $root 'scripts\factory\Triage.psm1') -Force
Import-Module (Join-Path $root 'scripts\factory\Controls.psm1') -Force
Import-Module (Join-Path $root 'scripts\factory\Guards.psm1') -Force
Import-Module (Join-Path $here 'Fixtures.psm1') -Force
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$launcher = Join-Path $root 'scripts\factory\Start-AgentJob.ps1'
$token = "sk-ant-oat01-" + ("Tk7mQ2xZ" * 12)

function New-Root([string]$name) {
    $r = Join-Path $TestDrive $name
    foreach ($d in "agent-jobs", "agent-work", "agent-in", "agent-tools", "nuget") { New-Item -ItemType Directory -Force (Join-Path $r $d) | Out-Null }
    $r
}
# A scratch repository bundled into agent-in, as the run bundles main. Returns its commit.
function New-Base([string]$r) {
    $src = Join-Path $TestDrive ("src-" + [guid]::NewGuid().ToString("N").Substring(0, 6))
    New-Item -ItemType Directory -Force (Join-Path $src "src") | Out-Null
    [IO.File]::WriteAllText((Join-Path $src "src\a.cs"), "class A { }`n")
    Push-Location $src
    try {
        $ErrorActionPreference = "Continue"
        git init -q 2>&1 | Out-Null; git config user.name t; git config user.email t@example.invalid; git config core.autocrlf false
        git add -A 2>&1 | Out-Null; git commit -q -m base 2>&1 | Out-Null
        $sha = (git rev-parse HEAD).Trim()
        git bundle create (Join-Path $r "agent-in\base-$sha.bundle") HEAD 2>&1 | Out-Null
        $sha
    }
    finally { Pop-Location }
}
function New-Ctx([string]$r, [hashtable]$over = @{}) {
    $a = @{ Root = $r; AgentSid = $me; Cache = (Join-Path $r "nuget\20261025000000-abcdef"); LimitMinutes = 45; Token = $token }
    foreach ($k in $over.Keys) { $a[$k] = $over[$k] }
    New-JobContext @a
}

Describe 'New-AgentJob: values, tokens and tools (P1)' {
    $r = New-Root 'p1'; $sha = "a" * 40; $ctx = New-Ctx $r
    It 'writes job.json with a new GUID and the kind''s fixed tool list' {
        $id = New-AgentJob $ctx @{ kind = "triage"; base = $sha; prompt = "Read {{JOB}}\report.md and write {{OUT}}\result.json." }
        $id | Should Match '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        $j = Get-Content (Join-Path $r "agent-jobs\$id\job.json") -Raw | ConvertFrom-Json
        $j.kind | Should Be "triage"; $j.base | Should Be $sha; $j.limitSeconds | Should Be 2700
        $j.tools | Should Be (Get-TriageTools (Get-JobTestChannel $ctx $sha))
        $j.tools | Should Match ([regex]::Escape("agent-tools/$sha/scripts/agent/test-channel.ps1"))
        @($j.claudeArgs) -contains "--disallowedTools" | Should Be $true
        (Get-Content (Join-Path $r "agent-jobs\$id\prompt.txt") -Raw) | Should Match ([regex]::Escape("$r\agent-work\$id\out\result.json"))
    }
    It 'gives each kind its own tools, and cover keeps its empty list' {
        $ids = @{}
        foreach ($k in "fix", "review", "cover") { $ids[$k] = New-AgentJob $ctx @{ kind = $k; base = $sha; prompt = "x"; system = "s" } }
        (Get-Content (Join-Path $r "agent-jobs\$($ids.fix)\job.json") -Raw | ConvertFrom-Json).tools | Should Match 'Bash\(git commit:\*\)'
        (Get-Content (Join-Path $r "agent-jobs\$($ids.review)\job.json") -Raw | ConvertFrom-Json).tools | Should Be "Read,Grep,Glob,Write"
        $c = Get-Content (Join-Path $r "agent-jobs\$($ids.cover)\job.json") -Raw | ConvertFrom-Json
        $c.tools | Should Be ""
        $i = [array]::IndexOf(@($c.claudeArgs), "--tools"); @($c.claudeArgs)[$i + 1] | Should Be ""
        Test-Path (Join-Path $r "agent-jobs\$($ids.cover)\system.txt") | Should Be $true
    }
    It 'refuses a bad kind, base, channel, filter or limit' {
        { New-AgentJob $ctx @{ kind = "deploy"; base = $sha } } | Should Throw
        { New-AgentJob $ctx @{ kind = "test"; base = "abc"; channel = "core" } } | Should Throw
        { New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "e2e" } } | Should Throw
        { New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "vitest" } } | Should Throw
        { New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "core"; filter = 'x" & calc' } } | Should Throw
        { New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "core"; filter = "A.B"; exact = $true } } | Should Throw
        { New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "full"; filter = "A=B"; exact = $true } } | Should Throw
        { New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "full"; filter = "A B"; exact = $true } } | Should Throw
        { New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "core"; limitSeconds = 2701 } } | Should Throw
        { New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "core"; patch = "..\x.patch" } } | Should Throw
        { New-AgentJob $ctx @{ kind = "triage"; base = $sha; inputs = @{ "..\evil" = "x" } } } | Should Throw
        $id = New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "full"; filter = "FlareAI.Core.Tests.X.Y"; exact = $true }
        (Get-Content (Join-Path $r "agent-jobs\$id\job.json") -Raw | ConvertFrom-Json).exact | Should Be $true
    }
    It 'writes the token to signin.txt alone, only for agent kinds, and never the other secrets' {
        $env:GH_TOKEN = "ghp_" + ("Zq9" * 12); $env:GITHUB_TOKEN = "ghs_" + ("Yp8" * 12); $env:ANTHROPIC_API_KEY = "sk-ant-api03-" + ("Xo7" * 12)
        try {
            $a = New-AgentJob $ctx @{ kind = "fix"; base = $sha; prompt = "p" }
            $t = New-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "core" }
            (Get-Content (Join-Path $r "agent-jobs\$a\signin.txt") -Raw) | Should Be $token
            Test-Path (Join-Path $r "agent-jobs\$t\signin.txt") | Should Be $false
            $all = (@(Get-ChildItem (Join-Path $r "agent-jobs") -Recurse -File | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n")
            foreach ($s in $env:GH_TOKEN, $env:GITHUB_TOKEN, $env:ANTHROPIC_API_KEY) { $all.Contains($s) | Should Be $false }
            $mine = (@(Get-ChildItem (Join-Path $r "agent-jobs\$a") -Recurse -File | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n")
            ([regex]::Matches($mine, [regex]::Escape($token))).Count | Should Be 1
        }
        finally { Remove-Item env:GH_TOKEN, env:GITHUB_TOKEN, env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue }
    }
    It 'needs a token for agent kinds' {
        { New-AgentJob (New-Ctx $r @{ Token = "" }) @{ kind = "triage"; base = $sha; prompt = "p" } } | Should Throw "CLAUDE_CODE_OAUTH_TOKEN isn't set"
    }
    It 'gives a restore job nuget.org and the newest locked cache as a local feed' {
        $prev = Join-Path $r "nuget\20261024000000-111111"; New-Item -ItemType Directory -Force $prev | Out-Null
        Mock -ModuleName Controls Test-CacheLocked { $path -like "*20261024000000-111111" }
        $id = New-AgentJob $ctx @{ kind = "restore"; base = $sha }
        $cfg = Get-Content (Join-Path $r "agent-jobs\$id\nuget.config") -Raw
        $cfg | Should Match 'https://api\.nuget\.org/v3/index\.json'
        $cfg | Should Match ([regex]::Escape($prev))
    }
}

Describe 'The sign-in (P2)' {
    It 'with only the API key set, sign-in throws' {
        $saved = $env:CLAUDE_CODE_OAUTH_TOKEN
        Remove-Item env:CLAUDE_CODE_OAUTH_TOKEN -ErrorAction SilentlyContinue
        $env:ANTHROPIC_API_KEY = "sk-ant-api03-not-real"
        try { { Get-AgentSignIn } | Should Throw "CLAUDE_CODE_OAUTH_TOKEN isn't set, so the agents can't sign in." }
        finally { Remove-Item env:ANTHROPIC_API_KEY; if ($saved) { $env:CLAUDE_CODE_OAUTH_TOKEN = $saved } }
    }
    It 'reads the OAuth token' {
        $saved = $env:CLAUDE_CODE_OAUTH_TOKEN; $env:CLAUDE_CODE_OAUTH_TOKEN = $token
        try { Get-AgentSignIn | Should Be $token } finally { if ($saved) { $env:CLAUDE_CODE_OAUTH_TOKEN = $saved } else { Remove-Item env:CLAUDE_CODE_OAUTH_TOKEN } }
    }
}

Describe 'The launcher, Start-AgentJob.ps1 (P3)' {
    # Fake programs in place of claude.exe, dotnet and the base copy of test-channel.ps1: they print their environment
    # and do what the test needs.
    function Set-Job([string]$r, [hashtable]$spec) {
        $ctx = New-Ctx $r
        $id = New-AgentJob $ctx $spec
        [IO.File]::WriteAllText((Join-Path $r "agent-jobs\next.txt"), $id)
        $id
    }
    function Invoke-Launcher([string]$r, [string]$claude, [string]$dotnet) {
        $a = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $launcher, "-Root", $r)
        if ($claude) { $a += @("-Claude", $claude) }
        if ($dotnet) { $a += @("-Dotnet", $dotnet) }
        $ErrorActionPreference = "Continue"
        $o = & $ps @a 2>&1 | Out-String
        [pscustomobject]@{ exit = $LASTEXITCODE; text = $o }
    }
    function Set-FakeChannel([string]$r, [string]$sha, [string]$body) {
        $p = Join-Path $r "agent-tools\$sha\scripts\agent"; New-Item -ItemType Directory -Force $p | Out-Null
        Set-Content (Join-Path $p "test-channel.ps1") $body -Encoding ASCII
    }
    $envDump = 'Get-ChildItem env: | Sort-Object Name | ForEach-Object { "ENV " + $_.Name + "=" + $_.Value }; "ARGS " + ($args -join " ")'

    It 'gives the child a fixed environment: a canary doesn''t reach it, and revocation stays offline only for a restore' {
        $r = New-Root 'p3a'; $sha = New-Base $r
        Set-FakeChannel $r $sha $envDump
        $env:HS_CANARY = "canary-value"
        try {
            $id = Set-Job $r @{ kind = "test"; base = $sha; channel = "core"; filter = "A.B" }
            $x = Invoke-Launcher $r
            $x.exit | Should Be 0
            $out = Get-Content (Join-Path $r "agent-work\$id\out\stdout.txt") -Raw
            $out | Should Not Match 'HS_CANARY'
            $out | Should Not Match 'NUGET_CERT_REVOCATION_MODE'
            $out | Should Match ([regex]::Escape("ENV HOUSESTYLE_TEST_REPO=$r\agent-work\$id\repo"))
            $out | Should Match 'ENV DOTNET_CLI_USE_MSBUILD_SERVER=0'
            $out | Should Match 'ENV MSBUILDDISABLENODEREUSE=1'
            $out | Should Match ([regex]::Escape("ENV USERPROFILE=$r\agent-work\$id\home"))
            $out | Should Match 'ENV HOUSESTYLE_AGENT_CHANNELS=rule-fixtures,bench,core'
            $out | Should Match 'ARGS .*-Channel core -RepoRoot .* -Filter A\.B'
            $out | Should Not Match 'CLAUDE_CODE_OAUTH_TOKEN'
            Get-Content (Join-Path $r "agent-work\$id\home\AppData\Roaming\NuGet\NuGet.Config") -Raw | Should Match '<clear />'
            # A restore child gets NUGET_CERT_REVOCATION_MODE=offline.
            $fakeDotnet = Join-Path $r "dotnet.cmd"; Set-Content $fakeDotnet "@echo off`r`nset`r`necho ARGS %*" -Encoding ASCII
            $id2 = Set-Job $r @{ kind = "restore"; base = $sha }
            (Invoke-Launcher $r $null $fakeDotnet).exit | Should Be 0
            $o2 = Get-Content (Join-Path $r "agent-work\$id2\out\stdout.txt") -Raw
            $o2 | Should Match 'NUGET_CERT_REVOCATION_MODE=offline'
            $o2 | Should Not Match 'HS_CANARY'
            $o2 | Should Match 'ARGS restore FlareAI\.sln --configfile'
        }
        finally { Remove-Item env:HS_CANARY }
    }
    It 'reads signin.txt, deletes it before the child starts, and passes the token only in the child''s environment' {
        $r = New-Root 'p3b'; $sha = New-Base $r
        $fake = Join-Path $r "claude.cmd"; Set-Content $fake "@echo off`r`nif exist `"%~dp0agent-jobs\%HS_ID%\signin.txt`" echo SIGNIN-STILL-THERE`r`nset CLAUDE_CODE`r`nset CLAUDE_CODE_OAUTH`r`necho ARGS %*" -Encoding ASCII
        $id = Set-Job $r @{ kind = "review"; base = $sha; prompt = "review it" }
        $x = Invoke-Launcher $r $fake
        $x.exit | Should Be 0
        Test-Path (Join-Path $r "agent-jobs\$id\signin.txt") | Should Be $false
        $out = Get-Content (Join-Path $r "agent-work\$id\out\stdout.txt") -Raw
        $out | Should Match ([regex]::Escape("CLAUDE_CODE_OAUTH_TOKEN=$token"))
        $out | Should Match 'CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1'
        $out | Should Match 'ARGS -p --output-format json'
        $l = Get-Content (Join-Path $r "agent-work\$id\out\launcher.json") -Raw | ConvertFrom-Json
        $l.signinAtChildStart | Should Be $false
        $l.exit | Should Be 0
    }
    It 'stops at its checks: no next.txt, a bad job.json, a missing signin.txt' {
        $r = New-Root 'p3c'; $sha = New-Base $r
        (Invoke-Launcher $r).exit | Should Be 2
        $id = Set-Job $r @{ kind = "triage"; base = $sha; prompt = "p" }
        Remove-Item (Join-Path $r "agent-jobs\$id\signin.txt")
        $x = Invoke-Launcher $r (Join-Path $r "none.cmd")
        $x.exit | Should Be 2; $x.text | Should Match "signin.txt can't be read"
        $id = Set-Job $r @{ kind = "test"; base = $sha; channel = "core" }
        $j = Get-Content (Join-Path $r "agent-jobs\$id\job.json") -Raw | ConvertFrom-Json; $j.kind = "deploy"
        $j | ConvertTo-Json | Set-Content (Join-Path $r "agent-jobs\$id\job.json")
        (Invoke-Launcher $r).text | Should Match "kind isn't one the factory runs"
    }
    It 'a failed delete of another folder under agent-work stops the job' {
        $r = New-Root 'p3d'; $sha = New-Base $r
        Set-FakeChannel $r $sha $envDump
        $old = Join-Path $r "agent-work\old-job"; New-Item -ItemType Directory -Force $old | Out-Null
        $lock = [IO.File]::Open((Join-Path $old "held.txt"), "Create", "ReadWrite", "None")
        try {
            $id = Set-Job $r @{ kind = "test"; base = $sha; channel = "core" }
            $x = Invoke-Launcher $r
            $x.exit | Should Be 2
            $x.text | Should Match "couldn't be deleted"
            Test-Path (Join-Path $r "agent-work\$id") | Should Be $false
        }
        finally { $lock.Dispose() }
    }
    It 'exits 259 on a timeout and leaves nothing running: the Job Object ends every process' {
        $r = New-Root 'p3e'; $sha = New-Base $r
        $marker = "hs-launcher-sleeper-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Set-FakeChannel $r $sha "Start-Process powershell.exe -ArgumentList '-NoProfile -Command Start-Sleep 120 # $marker' -WindowStyle Hidden; Start-Sleep 120"
        $id = Set-Job $r @{ kind = "test"; base = $sha; channel = "core"; limitSeconds = 4 }
        $x = Invoke-Launcher $r
        $x.exit | Should Be 259
        Start-Sleep 1
        @(Get-CimInstance Win32_Process | Where-Object { ("" + $_.CommandLine) -like "*$marker*" }).Count | Should Be 0
        (Get-Content (Join-Path $r "agent-work\$id\out\launcher.json") -Raw | ConvertFrom-Json).timedOut | Should Be $true
    }
    It 'holds stdout with no write sharing while the child runs' {
        $r = New-Root 'p3f'; $sha = New-Base $r
        Set-FakeChannel $r $sha '$o = Join-Path (Split-Path $env:HOUSESTYLE_TEST_REPO) "out\stdout.txt"; try { [IO.File]::Open($o, "Open", "Write", "ReadWrite").Dispose(); "OPENED-FOR-WRITE" } catch { "WRITE-REFUSED" }'
        $id = Set-Job $r @{ kind = "test"; base = $sha; channel = "core" }
        (Invoke-Launcher $r).exit | Should Be 0
        Get-Content (Join-Path $r "agent-work\$id\out\stdout.txt") -Raw | Should Match 'WRITE-REFUSED'
    }
    It 'writes a fix job''s patch with exactly the launcher''s git diff, and its commit messages and status' {
        $r = New-Root 'p3g'; $sha = New-Base $r
        $fake = Join-Path $r "claude.cmd"
        Set-Content $fake ("@echo off`r`ncd /d `"%HOUSESTYLE_TEST_REPO%`"`r`necho class B { }> src\b.cs`r`ngit add -A`r`ngit -c user.name=a -c user.email=a@example.invalid commit -q -m `"Add B`"`r`necho done") -Encoding ASCII
        $id = Set-Job $r @{ kind = "fix"; base = $sha; prompt = "fix it" }
        (Invoke-Launcher $r $fake).exit | Should Be 0
        $out = Join-Path $r "agent-work\$id\out"
        $patch = [IO.File]::ReadAllText((Join-Path $out "fix.patch"))
        $repoDir = Join-Path $r "agent-work\$id\repo"
        $want = (& git.exe -C $repoDir diff --no-ext-diff --no-textconv --no-renames --src-prefix=a/ --dst-prefix=b/ $sha HEAD | Out-String).Replace("`r`n", "`n")
        $patch | Should Be $want
        $patch | Should Match '^diff --git a/src/b\.cs b/src/b\.cs\nnew file mode 100644\n'
        [IO.File]::ReadAllText((Join-Path $out "commits.txt")) | Should Match 'Add B'
        [IO.File]::ReadAllText((Join-Path $out "status.txt")) | Should BeNullOrEmpty
        (Get-Content $launcher -Raw) | Should Match ([regex]::Escape('@("diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--src-prefix=a/", "--dst-prefix=b/", $job.base, "HEAD")'))
    }
    It 'applies the patch for test and review jobs' {
        $r = New-Root 'p3h'; $sha = New-Base $r
        Set-FakeChannel $r $sha 'Get-Content (Join-Path $env:HOUSESTYLE_TEST_REPO "src\a.cs")'
        $pid_ = [guid]::NewGuid().ToString()
        [IO.File]::WriteAllText((Join-Path $r "agent-in\$pid_.patch"), "diff --git a/src/a.cs b/src/a.cs`n--- a/src/a.cs`n+++ b/src/a.cs`n@@ -1 +1 @@`n-class A { }`n+class A { int Patched; }`n")
        $id = Set-Job $r @{ kind = "test"; base = $sha; channel = "core"; patch = "$pid_.patch" }
        (Invoke-Launcher $r).exit | Should Be 0
        Get-Content (Join-Path $r "agent-work\$id\out\stdout.txt") -Raw | Should Match 'Patched'
    }
}

Describe 'Invoke-AgentJob: one job''s order (P4)' {
    $r = New-Root 'p4'; $sha = "b" * 40
    $ctx = New-Ctx $r
    BeforeEach {
        $global:HsJobLog = New-Object System.Collections.Generic.List[string]; $global:HsRuleOn = $false
        Mock -ModuleName Controls Invoke-Sweep { $global:HsJobLog.Add("sweep:$keep"); $global:HsRuleOn = $false; $null }
        Mock -ModuleName Controls Resolve-NuGetAddresses { @("203.0.113.10") }
        Mock -ModuleName Controls New-EgressRule { $global:HsJobLog.Add("rule-on"); $global:HsRuleOn = $true }
        Mock -ModuleName Controls Test-RestoreRule { $global:HsRuleOn }
        Mock -ModuleName Controls Test-CacheWritable { $false }
        Mock -ModuleName Controls Start-AgentTask { $global:HsJobLog.Add("start:rule=$($global:HsRuleOn)"); Get-Date }
        Mock -ModuleName Controls Wait-AgentTask { $global:HsJobLog.Add("wait"); [pscustomobject]@{ exit = 0; timedOut = $false; instance = "{1}"; stopFailed = $false } }
        Mock -ModuleName Controls Read-JobOutput { $global:HsJobLog.Add("read:rule=$($global:HsRuleOn)"); [pscustomobject]@{ files = @{ "stdout.txt" = "ok" }; refused = $null } }
    }
    It 'a restore job has the restore rule only between the two sweeps, and reads after the second' {
        $j = Invoke-AgentJob $ctx @{ kind = "restore"; base = $sha }
        ($global:HsJobLog -join ' ') | Should Be "sweep: rule-on start:rule=True wait sweep:$($j.id) read:rule=False"
        $j.stuck | Should BeNullOrEmpty
    }
    It 'any other job runs with no restore rule, and stops when one is left open or the cache is writable' {
        $j = Invoke-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "core" }
        ($global:HsJobLog -join ' ') | Should Be "sweep: start:rule=False wait sweep:$($j.id) read:rule=False"
        Mock -ModuleName Controls Test-CacheWritable { $true }
        (Invoke-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "core" }).stuck | Should Be "RestoreLeftOpen"
    }
    It 'a failed sweep, or a task that won''t stop, leaves the job stuck for the agents VM' {
        Mock -ModuleName Controls Invoke-Sweep { "SweepFailed" }
        (Invoke-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "core" }).stuck | Should Be "SweepFailed"
        Mock -ModuleName Controls Invoke-Sweep { $null }
        Mock -ModuleName Controls Wait-AgentTask { [pscustomobject]@{ exit = 259; timedOut = $true; instance = $null; stopFailed = $true } }
        (Invoke-AgentJob $ctx @{ kind = "test"; base = $sha; channel = "core" }).stuck | Should Be "TaskDidntStop"
    }
}

Describe 'Wait-AgentTask (P4)' {
    It 'the wait matches the instance the first 100 event names, and stops a task past its limit' {
        InModuleScope Controls {
            $c = [pscustomobject]@{ taskName = "House Style agent"; grace = 0 }
            Mock Get-TaskEvents {
                @([pscustomobject]@{ id = 100; instance = "{mine}"; result = $null }, [pscustomobject]@{ id = 100; instance = "{later}"; result = $null },
                  [pscustomobject]@{ id = 201; instance = "{later}"; result = 1 }, [pscustomobject]@{ id = 102; instance = "{later}"; result = $null },
                  [pscustomobject]@{ id = 201; instance = "{mine}"; result = 7 }, [pscustomobject]@{ id = 102; instance = "{mine}"; result = $null })
            }
            $w = Wait-AgentTask $c (Get-Date) 60 0
            $w.instance | Should Be "{mine}"; $w.exit | Should Be 7; $w.timedOut | Should Be $false
            Mock Get-TaskEvents { @([pscustomobject]@{ id = 100; instance = "{mine}"; result = $null }) }
            Mock Stop-AgentTask { }
            Mock Get-AgentTaskState { "Ready" }
            $w = Wait-AgentTask $c (Get-Date).AddSeconds(-10) 1 0
            $w.timedOut | Should Be $true; $w.exit | Should Be 259; $w.stopFailed | Should Be $false
            Assert-MockCalled Stop-AgentTask -Times 1
        }
    }
    It (Get-FixtureTitle 'reads the task''s events as the fixture shows them' @('task-events')) -Skip:(Test-FixturesMissing @('task-events')) {
        $f = Get-Fixture 'task-events'
        $ev = @($f.cases | ForEach-Object { ConvertFrom-TaskEventXml $_.stdout })
        @($ev | Where-Object id -eq 100).Count | Should BeGreaterThan 0
        @($ev | ForEach-Object instance | Select-Object -Unique).Count | Should Be 1
        $ev[0].task | Should Be "\House Style agent"
        @($ev | Where-Object id -eq 201)[0].result | Should Not BeNullOrEmpty
    }
}

Describe 'The package cache (P4)' {
    It 'keeps this run''s cache and the newest locked one before it, and three runs in a row leave two' {
        $r = New-Root 'cache1'
        $global:HsLocked = @()
        Mock -ModuleName Controls Test-CacheLocked { $global:HsLocked -contains (Split-Path $path -Leaf) }
        $names = "20261020000000-aaaaaa", "20261021000000-bbbbbb", "20261022000000-cccccc"
        foreach ($n in $names) {
            New-Item -ItemType Directory -Force (Join-Path $r "nuget\$n") | Out-Null
            $global:HsLocked += $n
            @(Remove-OldCaches $r (Join-Path $r "nuget\$n") $me).Count | Should Be 0
        }
        @(Get-ChildItem (Join-Path $r "nuget") -Directory | ForEach-Object Name) -join ',' | Should Be "20261021000000-bbbbbb,20261022000000-cccccc"
    }
    It 'never follows a junction planted among the caches' {
        $r = New-Root 'cache2'
        $target = Join-Path $TestDrive "precious"; New-Item -ItemType Directory -Force $target | Out-Null; Set-Content (Join-Path $target "keep.txt") "keep"
        $j = Join-Path $r "nuget\20261019000000-dddddd"
        cmd /c mklink /J "$j" "$target" | Out-Null
        Mock -ModuleName Controls Test-CacheLocked { $true }
        foreach ($n in "20261020000000-aaaaaa", "20261021000000-bbbbbb") { New-Item -ItemType Directory -Force (Join-Path $r "nuget\$n") | Out-Null }
        @(Remove-OldCaches $r (Join-Path $r "nuget\20261021000000-bbbbbb") $me).Count | Should Be 0
        Test-Path $j | Should Be $false
        Test-Path (Join-Path $target "keep.txt") | Should Be $true
    }
    It 'deletes an unlocked cache a stopped run left, before the ready check, and never uses it as the local feed' {
        $r = New-Root 'cache3'
        $locked = Join-Path $r "nuget\20261020000000-aaaaaa"; $stray = Join-Path $r "nuget\20261021000000-bbbbbb"
        New-Item -ItemType Directory -Force $locked, $stray | Out-Null
        $global:HsLockedPath = $locked
        Mock -ModuleName Controls Test-CacheLocked { $path -eq $global:HsLockedPath }
        Get-NewestLockedCache $r "" $me | Should Be $locked
        @(Remove-UnlockedCaches $r $me).Count | Should Be 0
        Test-Path $stray | Should Be $false
        Test-Path $locked | Should Be $true
    }
    It 'a delete that returns 0 but leaves something behind is caught by the readback' {
        $d = Join-Path $TestDrive "partial"; New-Item -ItemType Directory -Force "$d\x" | Out-Null
        $h = [IO.File]::Open("$d\x\held.txt", "Create", "ReadWrite", "None")
        try {
            & cmd.exe /c rmdir /s /q "$d" 2>$null | Out-Null
            Remove-LinkSafe $d | Should Be $false
        }
        finally { $h.Dispose() }
        Remove-LinkSafe $d | Should Be $true
    }
    It 'a lock whose owner change didn''t take is reported, so the run stops' {
        $r = New-Root 'cache4'
        $c = Join-Path $r "nuget\20261022000000-cccccc"; New-Item -ItemType Directory -Force "$c\pkg" | Out-Null; Set-Content "$c\pkg\a.nupkg" "x"
        $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $problems = @(Lock-RunCache (New-Ctx $r @{ Cache = $c; AgentSid = "S-1-5-21-111-222-333-4001" }))
        if (-not $admin) { ($problems -join ' ') | Should Match "isn't owned by Administrators" } else { $problems.Count | Should Be 0 }
    }
    It 'finds a reparse point or a hard link in the cache' {
        $c = Join-Path $TestDrive "cache5"; New-Item -ItemType Directory -Force "$c\pkg" | Out-Null; Set-Content "$c\pkg\a.dll" "x"
        @(Test-CacheLinks $c).Count | Should Be 0
        cmd /c mklink /H "$c\pkg\b.dll" "$c\pkg\a.dll" | Out-Null
        (Test-CacheLinks $c) -join ' ' | Should Match 'more than one link'
        Remove-Item "$c\pkg\b.dll"
        cmd /c mklink /J "$c\pkg\j" "$TestDrive" | Out-Null
        (Test-CacheLinks $c) -join ' ' | Should Match 'is a link'
    }
    It (Get-FixtureTitle 'reads rmdir and icacls outcomes as the fixtures show them' @('rmdir-icacls')) -Skip:(Test-FixturesMissing @('rmdir-icacls')) {
        $f = Get-Fixture 'rmdir-icacls'
        (Get-FixtureCase $f 'rmdir-partial-locked').exists | Should Be $true   # the readback, not the exit code, says it failed
        (Get-FixtureCase $f 'icacls-setowner-ok').owner | Should Match 'Administrators'
        (Get-FixtureCase $f 'icacls-setowner-bad-account').exit | Should Not Be 0
    }
}

Describe 'Reading what a job wrote (P5)' {
    $r = New-Root 'p5'; $ctx = New-Ctx $r
    function New-Out([string]$id) { $o = Join-Path $r "agent-work\$id\out"; New-Item -ItemType Directory -Force $o | Out-Null; foreach ($n in "launcher.json", "stdout.txt", "stderr.txt") { Set-Content (Join-Path $o $n) "{}" -Encoding ASCII }; $o }
    It 'reads the kind''s fixed files' {
        $o = New-Out "a1"; Set-Content (Join-Path $o "result.json") '{"kind":"bug"}' -Encoding ASCII
        $x = Read-JobOutput $ctx "a1" "triage"
        $x.refused | Should BeNullOrEmpty
        $x.files["result.json"] | Should Match 'bug'
    }
    It 'refuses a missing required file' { New-Out "a2" | Out-Null; (Read-JobOutput $ctx "a2" "fix").refused | Should Match 'missing' }
    It 'refuses a junction anywhere in the path' {
        $o = New-Out "a3"; $real = Join-Path $TestDrive "real-out"; New-Item -ItemType Directory -Force $real | Out-Null
        foreach ($n in "launcher.json", "stdout.txt", "stderr.txt", "result.json") { Set-Content (Join-Path $real $n) "{}" }
        Remove-Item $o -Recurse -Force; cmd /c mklink /J "$o" "$real" | Out-Null
        (Read-JobOutput $ctx "a3" "triage").refused | Should Match 'is a link'
    }
    It 'refuses a file with more than one link' {
        $o = New-Out "a4"; $other = Join-Path $TestDrive "elsewhere.txt"; Set-Content $other "{}"
        Remove-Item (Join-Path $o "stdout.txt"); cmd /c mklink /H "$(Join-Path $o 'stdout.txt')" "$other" | Out-Null
        (Read-JobOutput $ctx "a4" "test").refused | Should Match 'links'
    }
    It 'refuses a file the agent user doesn''t own, and one over its cap' {
        New-Out "a5" | Out-Null
        (Read-JobOutput (New-Ctx $r @{ AgentSid = "S-1-5-21-111-222-333-4001" }) "a5" "test").refused | Should Match "isn't owned"
        $o = New-Out "a6"; [IO.File]::WriteAllText((Join-Path $o "launcher.json"), ("x" * 17000))
        (Read-JobOutput $ctx "a6" "test").refused | Should Match 'over'
    }
    It 'refuses a file that holds the token, in any form' {
        $o = New-Out "a7"; Set-Content (Join-Path $o "stdout.txt") ("noise " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("xx" + $token + "yy")))
        (Read-JobOutput $ctx "a7" "test").refused | Should Match 'token'
        Test-AgentText "a summary with $token in it" $token | Should Be $false
        Test-AgentText "a clean summary" $token | Should Be $true
    }
    It 'parses TRX with DTDs refused' {
        { ConvertFrom-TrxText '<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><x>&e;</x>' } | Should Throw
        (ConvertFrom-TrxText '<TestRun><Results /></TestRun>').TestRun | Should Not BeNullOrEmpty
    }
    It (Get-FixtureTitle 'reads the captured TRX for each outcome' @('trx')) -Skip:(Test-FixturesMissing @('trx')) {
        $f = Get-Fixture 'trx'
        $x = ConvertFrom-TrxText (Get-FixtureCase $f 'assertion-failure').trx
        @($x.TestRun.Results.UnitTestResult)[0].outcome | Should Be "Failed"
        Test-ReproResult @((Get-FixtureCase $f 'assertion-failure').trx) "Probe.Tests.ProbeTests.FailsAnAssertion" "fail" | Should BeNullOrEmpty
        Test-ReproResult @((Get-FixtureCase $f 'pass').trx) "Probe.Tests.ProbeTests.Passes" "pass" | Should BeNullOrEmpty
        Test-ReproResult @((Get-FixtureCase $f 'theory').trx) "Probe.Tests.ProbeTests.Theory" "pass" | Should BeNullOrEmpty
        Test-ReproResult @() "Probe.Tests.ProbeTests.Passes" "fail" | Should Match "didn't run"   # a build failure writes no TRX
        (Get-FixtureCase $f 'build-failure').trx | Should BeNullOrEmpty
    }
}
