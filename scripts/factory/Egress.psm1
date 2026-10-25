# The firewall rules for each VM (factory-runtime spec section 7, the egress decision), as data first, so they can be
# checked before anything changes. Set-FactoryEgress.ps1 applies the standing rules, and Invoke-AgentJob adds the
# restore rule for a restore job only (the sweep removes it). Windows Firewall matches addresses, not names, so names
# are resolved when a rule is made. Outbound traffic is blocked by default, except DNS to the VM's own resolvers.
$EgressPrefix = "House Style egress"
$RestoreRuleName = "House Style egress restore"
# The Anthropic API's published address range (docs.anthropic.com, IP addresses).
$AnthropicRange = @("160.79.104.0/23")
# NuGet's API host and its package download host: the restore rule's only names. Never widened from an error: a
# restore that needs another host stops and the owner decides (V7).
$NuGetHosts = @("api.nuget.org", "globalcdn.nuget.org")

# The rule an account's SDDL limits to: that account's processes only.
function Get-UserSddl([string]$sid) { "O:LSD:(A;;CC;;;$sid)" }

# The standing rules for a role. $users: agent and script SIDs (agents role) or the factory SID (flare role).
# $resolvers: the VM's DNS servers. $github: GitHub's published ranges. $hostAddresses: the flare role's other hosts.
function Get-EgressRules([ValidateSet("agents", "flare")][string]$role, [hashtable]$users, $resolvers, $github, $hostAddresses = @()) {
    if (-not @($resolvers).Count) { throw "The VM has no DNS servers configured, so there's nothing to allow DNS to." }
    $rules = @(
        [pscustomobject]@{ Name = "$EgressPrefix DNS (UDP)"; Protocol = "UDP"; RemotePort = 53; RemoteAddress = @($resolvers); LocalUser = $null },
        [pscustomobject]@{ Name = "$EgressPrefix DNS (TCP)"; Protocol = "TCP"; RemotePort = 53; RemoteAddress = @($resolvers); LocalUser = $null }
    )
    if ($role -eq "agents") {
        if (-not $users.agent -or -not $users.script) { throw "The agents role needs the agent user's and the script user's SIDs." }
        # hsagent: Anthropic's API range only. factory: GitHub only.
        $rules += [pscustomobject]@{ Name = "$EgressPrefix Anthropic (agent)"; Protocol = "TCP"; RemotePort = 443; RemoteAddress = @($AnthropicRange); LocalUser = (Get-UserSddl $users.agent) }
        $rules += [pscustomobject]@{ Name = "$EgressPrefix GitHub (script)"; Protocol = "TCP"; RemotePort = 443; RemoteAddress = @($github); LocalUser = (Get-UserSddl $users.script) }
    }
    else {
        # The Flare VM's factory account, unchanged: Anthropic, GitHub and the hosts its tools use.
        $rules += [pscustomobject]@{ Name = "$EgressPrefix Anthropic"; Protocol = "TCP"; RemotePort = 443; RemoteAddress = @($AnthropicRange); LocalUser = (Get-UserSddl $users.factory) }
        $rules += [pscustomobject]@{ Name = "$EgressPrefix GitHub"; Protocol = "TCP"; RemotePort = 443; RemoteAddress = @($github); LocalUser = (Get-UserSddl $users.factory) }
        if (@($hostAddresses).Count) { $rules += [pscustomobject]@{ Name = "$EgressPrefix hosts"; Protocol = "TCP"; RemotePort = 443; RemoteAddress = @($hostAddresses); LocalUser = (Get-UserSddl $users.factory) } }
    }
    $rules
}

# The restore rule: TCP 443 from the agent user to NuGet's addresses as they resolve now, and nothing else.
function Get-RestoreRule([string]$agentSid, $nugetAddresses) {
    $a = @($nugetAddresses | Where-Object { $_ } | Sort-Object -Unique)
    if (-not $a.Count) { throw "NuGet's hosts didn't resolve, so the restore can't run." }
    [pscustomobject]@{ Name = $RestoreRuleName; Protocol = "TCP"; RemotePort = 443; RemoteAddress = $a; LocalUser = (Get-UserSddl $agentSid) }
}
function Resolve-NuGetAddresses {
    @(foreach ($h in $NuGetHosts) { Resolve-DnsName $h -Type A -ErrorAction Stop | Where-Object { $_.IPAddress } | ForEach-Object { $_.IPAddress } }) | Sort-Object -Unique
}

# Makes one rule. Outbound, allow, any profile.
function New-EgressRule($rule) {
    $a = @{ DisplayName = $rule.Name; Direction = "Outbound"; Action = "Allow"; Protocol = $rule.Protocol; RemotePort = $rule.RemotePort; RemoteAddress = $rule.RemoteAddress; Profile = "Any" }
    if ($rule.LocalUser) { $a.LocalUser = $rule.LocalUser }
    New-NetFirewallRule @a | Out-Null
}
function Remove-RestoreRule { Get-NetFirewallRule -DisplayName $RestoreRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule }
function Test-RestoreRule { [bool](Get-NetFirewallRule -DisplayName $RestoreRuleName -ErrorAction SilentlyContinue) }

# Windows' own outbound allow rules (Core Networking's ICMPv6, Delivery Optimization, store apps) would let the agent
# user out past the default Block. On the agents VM setup disables every enabled outbound allow rule but ours and DHCP's
# (which only the DHCP service can use), and the ready check lists any that come back, so the limit holds by
# construction, not only by V6's probes.
$KeepOutboundRules = "CoreNet-DHCP-Out", "CoreNet-DHCPV6-Out"
function Get-ForeignOutboundAllows($rules) {
    @($rules | Where-Object { "$($_.Direction)" -eq "Outbound" -and "$($_.Action)" -eq "Allow" -and "$($_.Enabled)" -eq "True" -and
        "$($_.DisplayName)" -notlike "$EgressPrefix*" -and $KeepOutboundRules -notcontains "$($_.Name)" })
}
function Get-ForeignOutboundRules { Get-ForeignOutboundAllows @(Get-NetFirewallRule -Direction Outbound -Action Allow -Enabled True -ErrorAction SilentlyContinue) }
Export-ModuleMember -Function Get-ForeignOutboundAllows, Get-ForeignOutboundRules, Get-UserSddl, Get-EgressRules, Get-RestoreRule, Resolve-NuGetAddresses, New-EgressRule, Remove-RestoreRule, Test-RestoreRule -Variable KeepOutboundRules, EgressPrefix, RestoreRuleName, AnthropicRange, NuGetHosts
