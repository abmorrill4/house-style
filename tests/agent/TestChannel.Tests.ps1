# Pester 3 tests for scripts/agent/test-channel.ps1 and the channel helpers it uses.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$script = Join-Path $repo 'scripts\agent\test-channel.ps1'
Import-Module (Join-Path $repo 'scripts\agent\AgentCommon.psm1') -Force

Describe 'Get-TestChannel' {
    It 'lists the channels cheapest first, as the spec orders them' {
        $c = @(Get-TestChannel)
        ($c | ForEach-Object Name) -join ',' | Should Be 'rule-fixtures,bench,core,scenarios,flare-tests,pester,vitest,conformance,e2e,full'
        for ($i = 1; $i -lt $c.Count; $i++) { $c[$i].Order -ge $c[$i - 1].Order | Should Be $true }
    }
}

Describe 'Get-ChannelCommand' {
    It 'filters a .NET channel by name' {
        $c = Get-ChannelCommand -Channel core -RepoRoot $repo -Filter 'StaleFindingTests'
        $c.FilePath | Should Be 'dotnet'
        ($c.ArgumentList -join ' ') | Should Be 'test tests/FlareAI.Core.Tests --nologo --filter FullyQualifiedName~StaleFindingTests'
    }
    It 'asks for a TRX file when given a results folder' {
        $c = Get-ChannelCommand -Channel scenarios -RepoRoot $repo -ResultsDir 'C:\r'
        ($c.ArgumentList -join ' ') | Should Match 'trx;LogFileName=results.trx --results-directory C:\\r'
    }
    It 'runs Pester 3 on a path under tests/' {
        $c = Get-ChannelCommand -Channel pester -RepoRoot $repo -Path 'tests/agent'
        ($c.ArgumentList -join ' ') | Should Match "Import-Module Pester -MaximumVersion 3.99 -Force; .*Invoke-Pester -Path 'tests/agent'"
    }
    It 'refuses a Pester path outside tests/' {
        { Get-ChannelCommand -Channel pester -RepoRoot $repo -Path '..\secrets' } | Should Throw 'under tests/'
        { Get-ChannelCommand -Channel pester -RepoRoot $repo -Path 'tests/../scripts' } | Should Throw 'under tests/'
    }
    It 'refuses a filter that could break out of the command line' {
        { Get-ChannelCommand -Channel core -RepoRoot $repo -Filter 'x" & calc' } | Should Throw
        { Get-ChannelCommand -Channel core -RepoRoot $repo -Filter 'x; calc' } | Should Throw
    }
    It 'refuses AI scenarios and unknown scenarios on e2e' {
        { Get-ChannelCommand -Channel e2e -RepoRoot $repo -Scenario 'ai-no-double' } | Should Throw 'API key'
        { Get-ChannelCommand -Channel e2e -RepoRoot $repo -Scenario 'no-such-scenario' } | Should Throw "There's no scenario"
    }
    It 'runs e2e scenarios without AI' {
        $c = Get-ChannelCommand -Channel e2e -RepoRoot $repo -Scenario 'reopen-issues'
        ($c.ArgumentList -join ' ') | Should Match '-Install -NoAi -Scenario reopen-issues'
    }
    It 'says conformance is not built yet' {
        { Get-ChannelCommand -Channel conformance -RepoRoot $repo } | Should Throw 'gap G1'
    }
    It 'refuses an unknown channel' {
        { Get-ChannelCommand -Channel manual -RepoRoot $repo } | Should Throw 'Unknown channel'
    }
}

Describe 'Invoke-ScrubbedProcess' {
    It 'removes secrets from the child environment and keeps the rest' {
        $env:ANTHROPIC_API_KEY = 'sk-ant-test-not-real'
        $env:FACTORY_GH_TOKEN = 'ghp_test_not_real'
        $env:BIFROST_VK = 'vk-test'
        $env:HS_AGENT_PLAIN = 'kept'
        try {
            $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $r = Invoke-ScrubbedProcess -FilePath $ps -ArgumentList @('-NoProfile', '-Command', 'Get-ChildItem env: | ForEach-Object { $_.Name + [char]61 + $_.Value }')
            $text = $r.Output -join "`n"
            $text | Should Not Match 'sk-ant-test-not-real'
            $text | Should Not Match 'ghp_test_not_real'
            $text | Should Not Match 'BIFROST_VK'
            $text | Should Match 'HS_AGENT_PLAIN=kept'
            $r.ExitCode | Should Be 0
        } finally {
            Remove-Item env:ANTHROPIC_API_KEY, env:FACTORY_GH_TOKEN, env:BIFROST_VK, env:HS_AGENT_PLAIN -ErrorAction SilentlyContinue
        }
    }
    It 'treats names that may hold secrets as secrets' {
        foreach ($n in 'ANTHROPIC_API_KEY', 'GITHUB_TOKEN', 'GH_TOKEN', 'UPDATE_SIGNING_KEY', 'NPM_TOKEN', 'MY_PASSWORD', 'CLAUDE_CODE_OAUTH_TOKEN', 'FLAREAI_RECORD') {
            Test-SecretName $n | Should Be $true
        }
        foreach ($n in 'PATH', 'SystemRoot', 'TEMP', 'USERPROFILE', 'DOTNET_ROOT') { Test-SecretName $n | Should Be $false }
    }
}

Describe 'Enter-RunLock' {
    It 'refuses a second run while one holds the lock' {
        $lockPath = Join-Path ([IO.Path]::GetTempPath()) ('hs-lock-' + [guid]::NewGuid().ToString('N'))
        $held = Enter-RunLock $lockPath
        try {
            { Enter-RunLock $lockPath } | Should Throw 'Another test run is in progress'
        } finally { $held.Dispose(); Remove-Item $lockPath -ErrorAction SilentlyContinue }
    }
}

Describe 'test-channel.ps1' {
    It 'prints the command for a dry run' {
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Channel core -Filter StaleFindingTests -DryRun
        $LASTEXITCODE | Should Be 0
        ($out -join ' ') | Should Match 'dotnet test tests/FlareAI.Core.Tests --nologo --filter FullyQualifiedName~StaleFindingTests'
    }
    It 'exits 2 with a reason for a refused channel' {
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Channel conformance 6>&1
        $LASTEXITCODE | Should Be 2
    }
    It 'runs a Pester channel end to end without secrets' {
        $dir = Join-Path $repo ('tests\agent-tmp-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory $dir | Out-Null
        Set-Content (Join-Path $dir 'Env.Tests.ps1') 'Describe Env { It "has no key" { $env:ANTHROPIC_API_KEY | Should BeNullOrEmpty } }'
        $env:ANTHROPIC_API_KEY = 'sk-ant-test-not-real'
        try {
            $rel = 'tests/' + (Split-Path $dir -Leaf)
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Channel pester -Path $rel -LockPath (Join-Path $dir 'lock') | Out-Null
            $LASTEXITCODE | Should Be 0
        } finally {
            Remove-Item env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
            Remove-Item -Recurse -Force $dir
        }
    }
}

Describe 'The factory-runtime channel rules (spec section 4; P14)' {
    It 'says the Flare VM runs e2e and conformance' {
        foreach ($n in 'e2e', 'conformance') { (@(Get-TestChannel) | Where-Object Name -eq $n).Where | Should Be 'Flare VM' }
    }
    It '-Exact builds one exact test name on the full channel, with a fixed TRX name' {
        $c = Get-ChannelCommand -Channel full -RepoRoot $repo -Filter 'FlareAI.Core.Tests.IgnoreTests.IgnoreSurvivesRelint' -Exact -ResultsDir 'C:\r'
        ($c.ArgumentList -join ' ') | Should Be 'test FlareAI.sln -c Release --nologo --filter FullyQualifiedName=FlareAI.Core.Tests.IgnoreTests.IgnoreSurvivesRelint --logger trx;LogFileName=results.trx --results-directory C:\r'
    }
    It '-Exact is refused with another channel, without a name, or with a filter that isn''t one name' {
        { Get-ChannelCommand -Channel core -RepoRoot $repo -Filter 'A.B' -Exact } | Should Throw 'only with -Channel full'
        { Get-ChannelCommand -Channel full -RepoRoot $repo -Exact } | Should Throw
        foreach ($f in 'A~B', 'A=B', 'A!B', 'A&B', 'A|B', 'A,B', 'A(B)', 'A B') { { Get-ChannelCommand -Channel full -RepoRoot $repo -Filter $f -Exact } | Should Throw }
        { Get-ChannelCommand -Channel full -RepoRoot $repo -Filter 'A.B' } | Should Throw
    }
    It 'checks the agents'' list first: e2e gets the add-a-scenario message, other channels a plain refusal' {
        $list = 'rule-fixtures,bench,core,scenarios,flare-tests,pester,full'
        Get-ChannelRefusal -Channel e2e -AgentChannels $list -HasFlare $true | Should Be "The factory's agents can't run e2e. Add or update a scenario in tests/e2e/scenarios, and the factory runs it in Flare after you finish."
        Get-ChannelRefusal -Channel vitest -AgentChannels $list -HasFlare $true | Should Be "The factory's agents can't run this channel."
        Get-ChannelRefusal -Channel conformance -AgentChannels $list -HasFlare $true | Should Be "The factory's agents can't run this channel."
        Get-ChannelRefusal -Channel core -AgentChannels $list -HasFlare $false | Should BeNullOrEmpty
    }
    It 'without the list, e2e needs Flare on the machine' {
        Get-ChannelRefusal -Channel e2e -AgentChannels '' -HasFlare $false | Should Be "There's no Flare on this machine. Run e2e on the Flare VM."
        Get-ChannelRefusal -Channel e2e -AgentChannels '' -HasFlare $true | Should BeNullOrEmpty
    }
    It 'test-channel.ps1 refuses e2e for the agents with exit 2 and the message' {
        $env:HOUSESTYLE_AGENT_CHANNELS = 'core,pester'
        try { $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Channel e2e -Scenario reopen-issues }
        finally { Remove-Item env:HOUSESTYLE_AGENT_CHANNELS }
        $LASTEXITCODE | Should Be 2
        ($out -join ' ') | Should Match 'Add or update a scenario in tests/e2e/scenarios'
    }
    It 'keeps the package cache''s variables, which aren''t secrets' {
        Test-SecretName 'NUGET_PACKAGES' | Should Be $false
        Test-SecretName 'NUGET_CERT_REVOCATION_MODE' | Should Be $false
        Test-SecretName 'NUGET_API_KEY' | Should Be $true
    }
}