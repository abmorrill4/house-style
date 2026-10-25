# Pester 3 tests for the agents VM's firewall rules (factory-runtime spec section 7; P12).
#   powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99 -Force; Invoke-Pester tests\factory"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here '..\..')
Import-Module (Join-Path $root 'scripts\factory\Egress.psm1') -Force

Describe 'Get-EgressRules (P12)' {
    $agent = "S-1-5-21-0-0-0-1002"; $scriptUser = "S-1-5-21-0-0-0-1001"
    $github = @("192.0.2.0/24", "198.51.100.0/24")
    $rules = @(Get-EgressRules agents @{ agent = $agent; script = $scriptUser } @("203.0.113.53") $github)
    It 'gives the agent user Anthropic''s range only, on 443' {
        $mine = @($rules | Where-Object { $_.LocalUser -and $_.LocalUser.Contains($agent) })
        $mine.Count | Should Be 1
        @($mine[0].RemoteAddress) -join ',' | Should Be "160.79.104.0/23"
        $mine[0].RemotePort | Should Be 443
        $mine[0].Protocol | Should Be "TCP"
    }
    It 'gives the script user GitHub only, and never the agent user' {
        $gh = @($rules | Where-Object { $_.LocalUser -and $_.LocalUser.Contains($scriptUser) })
        $gh.Count | Should Be 1
        @($gh[0].RemoteAddress) -join ',' | Should Be ($github -join ',')
        foreach ($r in @($rules | Where-Object { $_.LocalUser -and $_.LocalUser.Contains($agent) })) { @($r.RemoteAddress | Where-Object { $github -contains $_ }).Count | Should Be 0 }
    }
    It 'lets every account reach only the VM''s own resolvers on 53, and nothing else without an owner' {
        foreach ($r in @($rules | Where-Object { -not $_.LocalUser })) { $r.RemotePort | Should Be 53; @($r.RemoteAddress) -join ',' | Should Be "203.0.113.53" }
    }
    It 'has no NuGet rule standing: the restore rule exists only during a restore job' {
        @($rules | Where-Object { $_.Name -eq $RestoreRuleName }).Count | Should Be 0
    }
    It 'the restore rule holds NuGet''s addresses and nothing else, for the agent user' {
        $r = Get-RestoreRule $agent @("203.0.113.10", "203.0.113.11", "203.0.113.10")
        $r.Name | Should Be "House Style egress restore"
        @($r.RemoteAddress) -join ',' | Should Be "203.0.113.10,203.0.113.11"
        $r.RemotePort | Should Be 443
        $r.LocalUser | Should Be "O:LSD:(A;;CC;;;$agent)"
        { Get-RestoreRule $agent @() } | Should Throw
        @($NuGetHosts) -join ',' | Should Be "api.nuget.org,globalcdn.nuget.org"
    }
    It 'refuses a VM with no resolvers, and the agents role without both users' {
        { Get-EgressRules agents @{ agent = $agent; script = $scriptUser } @() $github } | Should Throw
        { Get-EgressRules agents @{ agent = $agent } @("203.0.113.53") $github } | Should Throw
    }
    It 'keeps the Flare VM''s rules as they were' {
        $f = @(Get-EgressRules flare @{ factory = $scriptUser } @("203.0.113.53") $github @("203.0.113.20"))
        @($f.Name) -join ',' | Should Be "House Style egress DNS (UDP),House Style egress DNS (TCP),House Style egress Anthropic,House Style egress GitHub,House Style egress hosts"
    }
    It 'Set-FactoryEgress.ps1 takes -Role and builds its rules here' {
        $s = Get-Content (Join-Path $root 'scripts\factory\Set-FactoryEgress.ps1') -Raw
        $s | Should Match '\[ValidateSet\("agents", "flare"\)\]\[string\]\$Role'
        $s | Should Match 'Get-EgressRules agents'
    }
}
